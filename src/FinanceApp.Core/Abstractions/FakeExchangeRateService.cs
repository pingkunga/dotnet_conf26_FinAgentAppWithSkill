namespace FinanceApp.Core.Abstractions;

/// <summary>
/// Deterministic test double for <see cref="IExchangeRateService"/> that returns predefined exchange rates.
/// For Testing Purposes Only
/// </summary>
/// <param name="rates">Predefined exchange rates keyed by currency pair.</param>
public sealed class FakeExchangeRateService(IReadOnlyDictionary<(string From, string To), decimal?> rates) : IExchangeRateService
{
    public Task<decimal?> GetRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default)
    {
        if (fromCurrency == toCurrency)
        {
            return Task.FromResult<decimal?>(1m);
        }

        return Task.FromResult(rates.TryGetValue((fromCurrency, toCurrency), out var rate) ? rate : null);
    }
}
