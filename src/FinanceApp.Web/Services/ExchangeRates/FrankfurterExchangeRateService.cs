using System.Text.Json;
using System.Text.Json.Serialization;
using FinanceApp.Core.Abstractions;
using Microsoft.Extensions.Caching.Memory;

namespace FinanceApp.Web.Services.ExchangeRates;

/// <summary>
/// <see cref="IExchangeRateService"/> backed by api.frankfurter.dev — verified live: no API key required,
/// <c>GET /v1/latest?base=USD&amp;symbols=THB</c> returns <c>{"amount":1,"base":"USD","date":"...","rates":{"THB":...}}</c>,
/// and <c>/v1/currencies</c> confirms every code in <see cref="Core.Formatting.CurrencyFormatter.SupportedCurrencies"/>
/// is supported. Latest-rate only, no historical/as-of lookups.
/// </summary>
/// <remarks>
/// Caches successful lookups per calendar day (rates are daily) and separately keeps the last successfully
/// fetched rate per pair with a long TTL — if the API call fails, that stale value is returned instead of
/// <c>null</c>, so a transient outage degrades to "slightly stale rate" rather than "conversion unavailable".
/// Only returns <c>null</c> when there is truly nothing cached to fall back on.
/// </remarks>
public sealed class FrankfurterExchangeRateService(
    HttpClient httpClient,
    IMemoryCache cache,
    ILogger<FrankfurterExchangeRateService> logger) : IExchangeRateService
{
    private static readonly TimeSpan DailyCacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan LastKnownGoodTtl = TimeSpan.FromDays(30);

    public async Task<decimal?> GetRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        if (string.Equals(fromCurrency, toCurrency, StringComparison.OrdinalIgnoreCase))
        {
            return 1m;
        }

        var todayKey = DailyCacheKey(fromCurrency, toCurrency, DateOnly.FromDateTime(DateTime.UtcNow));
        if (cache.TryGetValue(todayKey, out decimal cachedRate))
        {
            return cachedRate;
        }

        var lastKnownGoodKey = LastKnownGoodKey(fromCurrency, toCurrency);

        try
        {
            var response = await httpClient.GetFromJsonAsync<FrankfurterResponse>(
                $"v1/latest?base={Uri.EscapeDataString(fromCurrency)}&symbols={Uri.EscapeDataString(toCurrency)}",
                cancellationToken);

            if (response?.Rates is not null && response.Rates.TryGetValue(toCurrency, out var rate))
            {
                cache.Set(todayKey, rate, DailyCacheTtl);
                cache.Set(lastKnownGoodKey, rate, LastKnownGoodTtl);
                return rate;
            }

            logger.LogWarning("Frankfurter response for {From}->{To} didn't include the requested rate.", fromCurrency, toCurrency);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Failed to fetch exchange rate {From}->{To} from Frankfurter.", fromCurrency, toCurrency);
        }

        return cache.TryGetValue(lastKnownGoodKey, out decimal staleRate) ? staleRate : null;
    }

    private static string DailyCacheKey(string from, string to, DateOnly asOf) => $"fx:{from}:{to}:{asOf:yyyy-MM-dd}";

    private static string LastKnownGoodKey(string from, string to) => $"fx-last-known-good:{from}:{to}";

    private sealed class FrankfurterResponse
    {
        [JsonPropertyName("amount")]
        public decimal Amount { get; set; }

        [JsonPropertyName("base")]
        public string? Base { get; set; }

        [JsonPropertyName("date")]
        public string? Date { get; set; }

        [JsonPropertyName("rates")]
        public Dictionary<string, decimal>? Rates { get; set; }
    }
}
