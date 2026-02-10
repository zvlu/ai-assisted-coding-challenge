using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using ExchangeRate.Core.Enums;

namespace ExchangeRate.Core.Caching;

/// <summary>
/// In-memory cache for exchange rates, keyed by (source, frequency, currency, year, month).
/// Implements a simple sliding expiration (default 30 minutes).
///
/// Domain rationale:
/// - Transactions are processed serially, usually within the same month.
///   Caching whole months means the first request populates the month and
///   every subsequent request in that month is a fast dictionary lookup.
/// - Provider calls are rate-limited and slow, so we never fetch the same
///   month twice within the expiry window.
/// - UpsertRate allows post-facto bank corrections to overwrite a single day
///   without invalidating the rest of the month.
/// </summary>
public class MonthlyExchangeRateCache : IExchangeRateCache
{
    private readonly TimeSpan _expiry;

    // Key: (Source, Frequency, Currency, Year, Month)
    private readonly ConcurrentDictionary<CacheKey, CacheEntry> _monthCache = new();

    /// <param name="slidingExpiry">Sliding expiration. Defaults to 30 minutes.</param>
    public MonthlyExchangeRateCache(TimeSpan? slidingExpiry = null)
    {
        _expiry = slidingExpiry ?? TimeSpan.FromMinutes(30);
    }

    // ---------- IExchangeRateCache ----------

    public Entities.ExchangeRate? GetRate(CurrencyTypes currency, DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency)
    {
        var key = new CacheKey(source, frequency, currency, date.Year, date.Month);
        if (!_monthCache.TryGetValue(key, out var entry))
            return null;

        if (IsExpired(entry, key))
            return null;

        entry.LastAccessUtc = DateTime.UtcNow;
        return entry.Rates.FirstOrDefault(r => r.Date.HasValue && r.Date.Value.Date == date.Date);
    }

    public bool IsMonthCached(CurrencyTypes currency, int year, int month, ExchangeRateSources source, ExchangeRateFrequencies frequency)
    {
        var key = new CacheKey(source, frequency, currency, year, month);
        if (!_monthCache.TryGetValue(key, out var entry))
            return false;

        if (IsExpired(entry, key))
            return false;

        entry.LastAccessUtc = DateTime.UtcNow;
        return true;
    }

    public void StoreMonthRates(IEnumerable<Entities.ExchangeRate> rates, CurrencyTypes currency, int year, int month, ExchangeRateSources source, ExchangeRateFrequencies frequency)
    {
        var key = new CacheKey(source, frequency, currency, year, month);
        var rateList = rates
            .Where(r => r.Date.HasValue && r.Date.Value.Year == year && r.Date.Value.Month == month)
            .ToList();
        _monthCache[key] = new CacheEntry(rateList);
    }

    public void UpsertRate(Entities.ExchangeRate rate)
    {
        if (!rate.CurrencyId.HasValue || !rate.Source.HasValue || !rate.Frequency.HasValue || !rate.Date.HasValue)
            return; // cannot cache a rate without required fields

        var key = new CacheKey(rate.Source.Value, rate.Frequency.Value, rate.CurrencyId.Value, rate.Date.Value.Year, rate.Date.Value.Month);

        _monthCache.AddOrUpdate(key,
            _ => new CacheEntry(new List<Entities.ExchangeRate> { rate }),
            (_, entry) =>
            {
                // Overwrite or add the rate for the specific day, preserving the rest of the month
                var idx = entry.Rates.FindIndex(r => r.Date.HasValue && r.Date.Value.Date == rate.Date.Value.Date);
                if (idx >= 0)
                    entry.Rates[idx] = rate;
                else
                    entry.Rates.Add(rate);
                entry.LastAccessUtc = DateTime.UtcNow;
                return entry;
            });
    }

    // ---------- Internals ----------

    private record CacheKey(ExchangeRateSources Source, ExchangeRateFrequencies Frequency, CurrencyTypes Currency, int Year, int Month);

    private sealed class CacheEntry
    {
        public List<Entities.ExchangeRate> Rates { get; }
        public DateTime LastAccessUtc { get; set; }

        public CacheEntry(List<Entities.ExchangeRate> rates)
        {
            Rates = rates;
            LastAccessUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Checks sliding expiry and evicts if expired.</summary>
    private bool IsExpired(CacheEntry entry, CacheKey key)
    {
        if (DateTime.UtcNow - entry.LastAccessUtc <= _expiry)
            return false;

        _monthCache.TryRemove(key, out _);
        return true;
    }
}
