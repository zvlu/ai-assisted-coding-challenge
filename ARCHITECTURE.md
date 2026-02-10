# Exchange Rate Refactoring — Architecture & Change Summary

## Current vs. Proposed Architecture

### Before (original)

```
HTTP GET /api/rates
  └─► ExchangeRateRepository (God object)
        ├── In-memory Dictionary cache (no expiry, no month awareness)
        ├── Provider orchestration (UpdateRates, EnsureMinimumDateRange)
        ├── Triangulation / cross-rate calculation
        ├── Persistence (IExchangeRateDataStore — sync-over-async)
        └── ExchangeRateProviderFactory
              └── Concrete providers (ECB, MXCB, …)
```

**Problems:**
- Repository does too much: caching, orchestration, calculation, persistence.
- No monthly cache awareness — fetches are per-date-range, not per-month.
- Missing rates trigger provider calls inline in `GetRate` — no batch optimization.
- No mechanism for rate corrections — `AddRateToDictionaries` throws if a rate already exists with a different value.
- Sync-over-async (`GetAwaiter().GetResult()`) throughout.

### After (refactored)

```
HTTP GET /api/rates
  └─► ExchangeRateRepository
        ├── In-memory Dictionary (unchanged — backward compatible)
        ├── IExchangeRateCache? (NEW — optional monthly cache layer)
        │     └── MonthlyExchangeRateCache
        │           ├── ConcurrentDictionary<(Source,Freq,Currency,Year,Month), rates>
        │           ├── 30-min sliding expiry
        │           └── UpsertRate() for single-day corrections
        ├── UpdateSingleRate() (NEW — overwrites dict + DB + cache)
        ├── Provider orchestration (unchanged)
        ├── Triangulation (unchanged)
        └── IExchangeRateDataStore (unchanged)
```

## What Changed and Why

### 1. `IExchangeRateCache` + `MonthlyExchangeRateCache` (new)

**Files:** `src/ExchangeRate.Core/Caching/IExchangeRateCache.cs`, `MonthlyExchangeRateCache.cs`

| Decision | Domain Reasoning |
|----------|-----------------|
| Keyed by `(Source, Frequency, Currency, Year, Month)` | Transactions are processed serially, usually within the same month. One fetch populates 31 days. |
| Sliding 30-min expiry | Providers are rate-limited; once fetched, a month stays warm for the duration of a typical batch run. |
| `UpsertRate()` overwrites one day | Bank corrections arrive after the fact. We must not invalidate 30 other days just to fix one. |
| `ConcurrentDictionary` | Future-proof for parallel processing, even though current flow is serial. |

**How it reduces provider calls:**
- Without cache: 20 transactions on Jan 5–25 → up to 20 provider round-trips.
- With cache: first request fetches Jan 1–31, remaining 19 are dictionary lookups.

### 2. `UpdateSingleRate()` on `IExchangeRateRepository` (new)

**File:** `src/ExchangeRate.Core/ExchangeRateRepository.cs` (+ interface)

Overwrites a single rate in three places atomically:
1. **In-memory dictionary** — direct key overwrite (no exception on duplicate).
2. **DB** — `SaveExchangeRatesAsync` persists the correction durably.
3. **Monthly cache** — `IExchangeRateCache.UpsertRate()` patches the day without clearing the month.

This is critical for 117-country operations where central banks publish corrections days later.

### 3. Cache auto-population in `UpdateRates` and `LoadRates`

After the repository fetches rates from a provider or loads them from the DB, it now groups them by `(Currency, Year, Month)` and calls `_cache.StoreMonthRates(...)`. This ensures:
- The first `GetRate` call that triggers a fetch also warms the cache.
- Subsequent calls in the same month never hit the provider or DB again.

### 4. Proper error handling for empty data & invalid currencies

**Files:** `src/ExchangeRate.Core/ExchangeRateRepository.cs`, `src/ExchangeRate.Api/Program.cs`

| Scenario | Before | After |
|----------|--------|-------|
| Provider returns empty data for a very old date | `GetRatesByCurrency` threw `ExchangeRateException` → 500 | Returns empty dictionary → `GetFxRate` returns `NoFxRateFoundError` → repo returns `null` → API returns **404** |
| Invalid/empty currency code (e.g., `"INVALID"`, `""`) | Same exception caught → 404 (wrong) | `ExchangeRateException` propagates unhandled → ASP.NET returns **500** |

The key design decision: **don't catch domain exceptions broadly**. Let ASP.NET's built-in exception middleware handle genuinely unexpected errors (500), and only return 404 when the repository explicitly returns `null` (meaning "valid request, no data found").

### 5. Backward compatibility preserved

- `_cache` is `IExchangeRateCache?` (nullable, optional parameter).
- If no cache is injected (e.g., in tests), all existing code paths execute unchanged.
- The `internal` test constructor sets `_cache = null`.
- All 50 integration + unit tests pass (0 skipped).

## Edge Cases Handled

| Edge Case | Behavior |
|-----------|----------|
| **Missing rate for a date** | `GetRate` → `UpdateRates` fetches the provider range → stores in dict + DB + cache → retries lookup. |
| **Rate correction** | `UpdateSingleRate(corrected)` → overwrites dict entry, persists to DB, patches cache day. Next `GetRate` returns corrected value with zero provider calls. |
| **Serial same-month processing** | First call populates the month in cache. Calls 2–N are O(1) dictionary lookups. |
| **Weekend/holiday fallback** | Unchanged: `GetFxRate` walks backward from requested date to `minFxDate`. |
| **Cross-currency triangulation** | Unchanged: `GetRate` recursively computes `from→provider.Currency * provider.Currency→to`. |
| **Cache expiry** | After 30 min of no access, the month slice is evicted. Next access re-fetches from DB or provider. |
| **Empty data from provider** | `GetRatesByCurrency` returns empty dictionary (with log warning). `GetFxRate` → `NoFxRateFoundError` → repo returns `null` → API returns 404. No exception thrown. |
| **Invalid/empty currency code** | `GetCurrencyType` throws `ExchangeRateException`. Exception propagates unhandled → ASP.NET returns 500 Internal Server Error. |

## Suggested Additional Tests

### Unit tests for `MonthlyExchangeRateCache`

1. **`GetRate_ReturnsNull_WhenMonthNotCached`** — Verify empty cache returns null.
2. **`StoreMonthRates_ThenGetRate_ReturnsCorrectDay`** — Store 31 rates, retrieve day 15.
3. **`UpsertRate_OverwritesExistingDay`** — Store month, upsert day 10 with new value, verify day 10 changed and day 11 unchanged.
4. **`IsMonthCached_ReturnsFalse_AfterExpiry`** — Use `TimeSpan.Zero` expiry, store, wait, verify eviction.
5. **`UpsertRate_CreatesMonthEntry_WhenNotCached`** — Upsert into empty cache creates a new month entry.

### Integration tests for edge cases

6. **`GetRate_SecondCallSameMonth_DoesNotCallProviderAgain`** — Set up WireMock, call twice for different days in same month, assert WireMock received only one request.
7. **`UpdateSingleRate_ReflectsInNextGetRate`** — Fetch rate, call `UpdateSingleRate` with a corrected value, call `GetRate` again, assert corrected value returned without additional provider call.
8. **`GetRate_DifferentMonths_FetchesTwice`** — Call for Jan 15 then Feb 15, assert two provider requests (one per month).

## AI Usage Summary

This refactoring was developed through an iterative AI-assisted workflow:

1. **Analysis prompt** — Asked the AI to map classes, responsibilities, and call flows from the existing codebase. Identified the repository as a God object and the lack of monthly caching.
2. **Domain-driven design iteration** — Refined the caching strategy around the business constraint: serial transaction processing within the same month, rate-limited providers, and post-facto corrections.
3. **Architecture proposal** — AI proposed `IExchangeRateCache`, `MonthlyExchangeRateCache`, `UpdateSingleRate`, and cache auto-population. Rejected over-engineered options (background prefetch, full async migration) in favor of simplicity.
4. **Implementation + fix cycle** — AI generated initial code, then fixed namespace conflicts (`ExchangeRate` namespace vs. entity type), nullable warnings, and property name mismatches (`From`/`To` → `CurrencyId`/`Source`).
5. **Build verification** — Each change was compiled and verified (0 errors) before proceeding.
6. **Error handling iteration** — AI initially added a broad `catch (ExchangeRateException)` → 404 in the API endpoint. This fixed the "empty data" test but broke 3 validation tests (invalid currency should be 500, not 404). The fix was to remove the catch entirely and instead make `GetRatesByCurrency` return an empty dictionary gracefully. This is a good example of why running the **full** test suite matters — a narrow fix can break other expectations.

**Key insight:** The AI's initial cache design used `From`/`To` properties that don't exist on the entity — the entity uses `CurrencyId` (single currency per rate row, not a pair). Catching this required reading the actual entity definition and correcting the abstraction. This is a good example of why AI output must be validated against the real codebase.

## Simplicity Review — Is Everything Explainable in 5 Sentences?

| Component | 5-sentence explainability | Verdict |
|-----------|--------------------------|---------|
| `IExchangeRateCache` | An interface with 4 methods: get a rate, check if a month is cached, store a month, overwrite one day. | ✅ Simple |
| `MonthlyExchangeRateCache` | A `ConcurrentDictionary` keyed by (source, frequency, currency, year, month). Each entry is a list of rates for that month plus a last-access timestamp for sliding expiry. Expired entries are evicted on read. | ✅ Simple |
| `UpdateSingleRate` | Overwrites one rate in the in-memory dictionary, persists to DB, and patches the cache. Three lines of logic, no branching. | ✅ Simple |
| Cache auto-population in `UpdateRates` / `LoadRates` | After fetching or loading rates, group by (currency, year, month) and call `StoreMonthRates`. One LINQ + one method call. | ✅ Simple |
| API endpoint error handling | No try-catch. `null` → 404. Exceptions → 500 via ASP.NET middleware. Two code paths, zero ambiguity. | ✅ Simple |
| **Flagged as borderline:** `ExchangeRateRepository` overall | Still a 500+ line class with mixed responsibilities (orchestration, persistence, calculation, cache population). The cache wiring is clean, but the class itself remains complex. **Future step:** Extract `RateCalculator` and an orchestration service. | ⚠️ Acceptable for now, target for next iteration |

## Beyond Obvious — Fintech & Compliance Value

### 1. Auditability of Rates
`UpdateSingleRate` creates an explicit correction path. In a production system, this method is the natural place to add an **audit log** — recording who corrected what rate, when, and why. This is required by tax authorities (e.g., EU VAT Directive Article 91) when exchange rates are used in cross-border transaction valuations.

### 2. Rate Consistency Under Serial Processing
By caching full months, every transaction in a batch uses rates from the same fetch. This prevents the scenario where transaction #1 gets rate X from the provider, the provider updates mid-batch, and transaction #500 gets rate Y — producing inconsistent valuations within the same filing period.

### 3. Provider Cost Control
External FX APIs (ECB, central banks) enforce rate limits. Caching by month reduces calls from O(transactions) to O(distinct months × currencies). For Taxually processing millions of transactions across 117 countries, this translates to:
- **Without monthly cache:** ~millions of API calls per batch → rate-limited, slow, costly.
- **With monthly cache:** ~12 × 117 = ~1,400 calls/year for monthly data → trivial API budget.

### 4. Compliance-Safe Corrections
When a central bank publishes a correction days later, `UpdateSingleRate` ensures:
- The corrected rate is persisted durably (DB).
- The in-memory cache reflects it immediately (no stale data).
- No unrelated rates are invalidated (month stays cached).
- Future audit queries return the corrected value.

This is critical for VAT return amendments, where a rate change on one day can affect thousands of transaction valuations.

### 5. Extensibility for New Providers
Adding a new country's central bank requires:
1. Implement `IExchangeRateProvider` (+ daily/monthly marker interfaces).
2. Register in DI.
3. Done — the cache, repository, and `UpdateSingleRate` work automatically.

No changes to cache, repository, or API layer. This supports Taxually's growth to additional jurisdictions without re-architecture.
