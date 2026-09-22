using FinanceApp.Core.Formatting;

namespace FinanceApp.Skills.Tests;

/// <summary>
/// <see cref="CurrencyFormatter"/> formats money manually instead of via <c>CultureInfo</c>-driven
/// <c>"C"</c> formatting, because <c>Directory.Build.props</c>' <c>InvariantGlobalization=true</c> makes
/// the invariant culture's own currency symbol render as the generic "¤" sign instead of a real one.
/// </summary>
public sealed class CurrencyFormatterTests
{
    [Theory]
    [InlineData("USD", "$")]
    [InlineData("THB", "฿")]
    [InlineData("EUR", "€")]
    [InlineData("GBP", "£")]
    [InlineData("JPY", "¥")]
    public void Format_KnownCurrency_UsesItsRealSymbol(string currencyCode, string expectedSymbol)
    {
        var result = CurrencyFormatter.Format(1234.5m, currencyCode);

        Assert.Equal($"{expectedSymbol}1,234.50", result);
    }

    [Fact]
    public void Format_UnknownCurrency_FallsBackToTheCodeItself()
    {
        var result = CurrencyFormatter.Format(10m, "XYZ");

        Assert.Equal("XYZ 10.00", result);
    }

    [Fact]
    public void Format_NeverRendersTheGenericCurrencySign()
    {
        var result = CurrencyFormatter.Format(0m, "USD");

        Assert.DoesNotContain('¤', result);
    }

    [Fact]
    public void SupportedCurrencies_AllHaveAKnownSymbol()
    {
        foreach (var code in CurrencyFormatter.SupportedCurrencies)
        {
            Assert.DoesNotContain('¤', CurrencyFormatter.Format(1m, code));
        }
    }
}
