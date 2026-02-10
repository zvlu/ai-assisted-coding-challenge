using System;
using System.Collections.Generic;
using ExchangeRate.Core.Enums;

namespace ExchangeRate.Core.Caching;

/// <summary>
/// Abstraction for exchange rate caching.
/// Designed for monthly-keyed lookups to minimize provider calls
/// when processing serial transactions within the same month.
/// </summary>
public interface IExchangeRateCache
{
    /// <summary>
    /// Returns a cached rate for the given currency, date and source, or null if not cached.
    /// </summary>
    Entities.ExchangeRate? GetRate(CurrencyTypes currency, DateTime date, ExchangeRateSources source, ExchangeRateFrequencies frequency);

    /// <summary>
    /// Returns true if the full month of rates is already cached for the given currency/source/frequency.
    /// </summary>
    bool IsMonthCached(CurrencyTypes currency, int year, int month, ExchangeRateSources source, ExchangeRateFrequencies frequency);

    /// <summary>
    /// Stores all rates for a given month in the cache, replacing any existing entries for that month.
    /// </summary>
    void StoreMonthRates(IEnumerable<Entities.ExchangeRate> rates, CurrencyTypes currency, int year, int month, ExchangeRateSources source, ExchangeRateFrequencies frequency);

    /// <summary>
    /// Overwrites or inserts a single rate in the cache without invalidating the rest of the month.
    /// Supports post-facto bank corrections.
    /// </summary>
    void UpsertRate(Entities.ExchangeRate rate);
}
