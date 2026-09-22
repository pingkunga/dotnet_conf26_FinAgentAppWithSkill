namespace FinanceApp.Core.Abstractions;


public interface IExchangeRateService
{
    Task<decimal?> GetRateAsync(string fromCurrency, string toCurrency, CancellationToken cancellationToken = default);
}
