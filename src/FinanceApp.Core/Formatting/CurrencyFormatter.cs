using System.Globalization;

namespace FinanceApp.Core.Formatting;

public static class CurrencyFormatter
{
    public static readonly IReadOnlyList<string> SupportedCurrencies = ["USD", "THB", "EUR", "GBP", "JPY"];

    public static string Format(decimal amount, string currencyCode) =>
        $"{SymbolFor(currencyCode)}{amount.ToString("N2", CultureInfo.InvariantCulture)}";

    private static string SymbolFor(string currencyCode) => currencyCode switch
    {
        "USD" => "$",
        "THB" => "฿",
        "EUR" => "€",
        "GBP" => "£",
        "JPY" => "¥",
        _ => currencyCode + " ",
    };
}
