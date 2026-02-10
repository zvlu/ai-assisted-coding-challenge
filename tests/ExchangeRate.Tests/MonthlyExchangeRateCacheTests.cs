#nullable enable
using ExchangeRate.Core.Caching;
using ExchangeRate.Core.Enums;
using FluentAssertions;
using Xunit;

namespace ExchangeRate.Tests;

/// <summary>
/// Unit tests for <see cref="MonthlyExchangeRateCache"/>.
///
/// These tests validate the three edge cases central to the refactoring:
///   1. Missing rate → cache miss returns null, then after StoreMonthRates it returns the day.
///   2. Rate correction → UpsertRate overwrites one day without affecting the rest of the month.
///   3. Serial same-month processing → IsMonthCached returns true after first store, so no provider re-fetch.
///
/// All tests are pure in-memory — no HTTP, no DI, no side-effects.
/// </summary>
public class MonthlyExchangeRateCacheTests
{
    // ---------- helpers ----------

    private static Core.Entities.ExchangeRate MakeRate(
        CurrencyTypes currency, DateTime date, decimal rate,
        ExchangeRateSources source = ExchangeRateSources.ECB,
        ExchangeRateFrequencies frequency = ExchangeRateFrequencies.Daily)
    {
        return new Core.Entities.ExchangeRate
        {
            CurrencyId = currency,
            Date = date,
            Rate = rate,
            Source = source,
            Frequency = frequency
        };
    }

    private static List<Core.Entities.ExchangeRate> MakeMonthRates(
        CurrencyTypes currency, int year, int month, decimal baseRate,
        ExchangeRateSources source = ExchangeRateSources.ECB,
        ExchangeRateFrequencies frequency = ExchangeRateFrequencies.Daily)
    {
        var days = DateTime.DaysInMonth(year, month);
        return Enumerable.Range(1, days)
            .Select(d => MakeRate(currency, new DateTime(year, month, d), baseRate + d * 0.001m, source, frequency))
            .ToList();
    }

    // ---------- 1. Cache miss / store / hit ----------

    [Fact]
    public void GetRate_ReturnsNull_WhenMonthNotCached()
    {
        // Arrange — empty cache
        var cache = new MonthlyExchangeRateCache();

        // Act
        var result = cache.GetRate(CurrencyTypes.USD, new DateTime(2024, 1, 15), ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);

        // Assert
        result.Should().BeNull("no month has been stored yet");
    }

    [Fact]
    public void StoreMonthRates_ThenGetRate_ReturnsCorrectDay()
    {
        // Arrange
        var cache = new MonthlyExchangeRateCache();
        var rates = MakeMonthRates(CurrencyTypes.USD, 2024, 1, 1.08m);

        // Act
        cache.StoreMonthRates(rates, CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);
        var result = cache.GetRate(CurrencyTypes.USD, new DateTime(2024, 1, 15), ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);

        // Assert
        result.Should().NotBeNull();
        result!.Date!.Value.Day.Should().Be(15);
        result.Rate.Should().Be(1.08m + 15 * 0.001m, "rate for day 15 uses baseRate + 15*0.001");
    }

    [Fact]
    public void IsMonthCached_ReturnsFalse_WhenEmpty()
    {
        var cache = new MonthlyExchangeRateCache();

        cache.IsMonthCached(CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily)
            .Should().BeFalse();
    }

    [Fact]
    public void IsMonthCached_ReturnsTrue_AfterStore()
    {
        // Arrange
        var cache = new MonthlyExchangeRateCache();
        var rates = MakeMonthRates(CurrencyTypes.USD, 2024, 1, 1.08m);

        // Act
        cache.StoreMonthRates(rates, CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);

        // Assert — same-month serial processing: no provider call needed after this
        cache.IsMonthCached(CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily)
            .Should().BeTrue();
    }

    // ---------- 2. Rate correction (UpsertRate) ----------

    [Fact]
    public void UpsertRate_OverwritesExistingDay_WithoutAffectingOtherDays()
    {
        // Arrange — store full month
        var cache = new MonthlyExchangeRateCache();
        var rates = MakeMonthRates(CurrencyTypes.USD, 2024, 1, 1.08m);
        cache.StoreMonthRates(rates, CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);

        // Act — bank corrects day 10
        var corrected = MakeRate(CurrencyTypes.USD, new DateTime(2024, 1, 10), 9.99m);
        cache.UpsertRate(corrected);

        // Assert — day 10 changed, day 11 unchanged
        var day10 = cache.GetRate(CurrencyTypes.USD, new DateTime(2024, 1, 10), ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);
        day10.Should().NotBeNull();
        day10!.Rate.Should().Be(9.99m, "correction was applied");

        var day11 = cache.GetRate(CurrencyTypes.USD, new DateTime(2024, 1, 11), ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);
        day11.Should().NotBeNull();
        day11!.Rate.Should().Be(1.08m + 11 * 0.001m, "other days are untouched");
    }

    [Fact]
    public void UpsertRate_CreatesMonthEntry_WhenNotPreviouslyCached()
    {
        // Arrange — empty cache
        var cache = new MonthlyExchangeRateCache();

        // Act — upsert into empty cache
        var rate = MakeRate(CurrencyTypes.GBP, new DateTime(2024, 3, 5), 0.85m);
        cache.UpsertRate(rate);

        // Assert — month now exists with one entry
        cache.IsMonthCached(CurrencyTypes.GBP, 2024, 3, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily)
            .Should().BeTrue();

        var result = cache.GetRate(CurrencyTypes.GBP, new DateTime(2024, 3, 5), ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);
        result.Should().NotBeNull();
        result!.Rate.Should().Be(0.85m);
    }

    [Fact]
    public void UpsertRate_IgnoresRateWithMissingFields()
    {
        // Arrange
        var cache = new MonthlyExchangeRateCache();
        var badRate = new Core.Entities.ExchangeRate { CurrencyId = null, Date = null, Rate = 1.0m };

        // Act — should not throw, should not create an entry
        cache.UpsertRate(badRate);

        // Assert — cache stays empty
        cache.IsMonthCached(CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily)
            .Should().BeFalse();
    }

    // ---------- 3. Sliding expiry ----------

    [Fact]
    public void IsMonthCached_ReturnsFalse_AfterExpiry()
    {
        // Arrange — use zero expiry so entry expires immediately
        var cache = new MonthlyExchangeRateCache(slidingExpiry: TimeSpan.Zero);
        var rates = MakeMonthRates(CurrencyTypes.USD, 2024, 1, 1.08m);
        cache.StoreMonthRates(rates, CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);

        // Act — wait a tick for expiry to trigger
        Thread.Sleep(1);

        // Assert
        cache.IsMonthCached(CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily)
            .Should().BeFalse("entry should be evicted after zero-length expiry");
    }

    [Fact]
    public void GetRate_ReturnsNull_AfterExpiry()
    {
        // Arrange
        var cache = new MonthlyExchangeRateCache(slidingExpiry: TimeSpan.Zero);
        var rates = MakeMonthRates(CurrencyTypes.USD, 2024, 1, 1.08m);
        cache.StoreMonthRates(rates, CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);

        // Act
        Thread.Sleep(1);
        var result = cache.GetRate(CurrencyTypes.USD, new DateTime(2024, 1, 15), ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);

        // Assert
        result.Should().BeNull("expired entries are evicted on access");
    }

    // ---------- 4. Cross-source / cross-frequency isolation ----------

    [Fact]
    public void DifferentSources_AreCachedIndependently()
    {
        // Arrange
        var cache = new MonthlyExchangeRateCache();
        var ecbRates = MakeMonthRates(CurrencyTypes.USD, 2024, 1, 1.08m, ExchangeRateSources.ECB);
        var mxcbRates = MakeMonthRates(CurrencyTypes.USD, 2024, 1, 17.5m, ExchangeRateSources.MXCB, ExchangeRateFrequencies.Monthly);

        // Act
        cache.StoreMonthRates(ecbRates, CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);
        cache.StoreMonthRates(mxcbRates, CurrencyTypes.USD, 2024, 1, ExchangeRateSources.MXCB, ExchangeRateFrequencies.Monthly);

        // Assert — each source returns its own rate
        var ecbDay15 = cache.GetRate(CurrencyTypes.USD, new DateTime(2024, 1, 15), ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);
        ecbDay15.Should().NotBeNull();
        ecbDay15!.Rate.Should().Be(1.08m + 15 * 0.001m);

        var mxcbDay15 = cache.GetRate(CurrencyTypes.USD, new DateTime(2024, 1, 15), ExchangeRateSources.MXCB, ExchangeRateFrequencies.Monthly);
        mxcbDay15.Should().NotBeNull();
        mxcbDay15!.Rate.Should().Be(17.5m + 15 * 0.001m);
    }

    [Fact]
    public void DifferentMonths_AreCachedIndependently()
    {
        // Arrange
        var cache = new MonthlyExchangeRateCache();
        var janRates = MakeMonthRates(CurrencyTypes.USD, 2024, 1, 1.08m);
        var febRates = MakeMonthRates(CurrencyTypes.USD, 2024, 2, 1.10m);

        // Act
        cache.StoreMonthRates(janRates, CurrencyTypes.USD, 2024, 1, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);
        cache.StoreMonthRates(febRates, CurrencyTypes.USD, 2024, 2, ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);

        // Assert
        var jan15 = cache.GetRate(CurrencyTypes.USD, new DateTime(2024, 1, 15), ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);
        jan15!.Rate.Should().Be(1.08m + 15 * 0.001m);

        var feb15 = cache.GetRate(CurrencyTypes.USD, new DateTime(2024, 2, 15), ExchangeRateSources.ECB, ExchangeRateFrequencies.Daily);
        feb15!.Rate.Should().Be(1.10m + 15 * 0.001m);
    }
}
