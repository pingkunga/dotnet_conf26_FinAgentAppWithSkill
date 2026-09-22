using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Formatting;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceApp.Skills.ExchangeRates;

/// <summary>
/// Class-based skill exposing <see cref="IExchangeRateService"/> to chat — added alongside
/// <see cref="Budgeting.BudgetSkill"/>, not in place of any of the app's 4 firm feature-to-skill-type
/// mappings (docs/spec.md/CLAUDE.md): it's a second, independent class-based skill, not a 5th type.
/// </summary>
/// <remarks>
/// Unlike <see cref="Budgeting.BudgetSkill"/>, this skill touches no per-user data — exchange rates aren't
/// user-scoped — so <see cref="IServiceScopeFactory"/> isn't needed for user-isolation here. It's still the
/// right shape to take, though: <c>IExchangeRateService</c> is registered via
/// <c>AddHttpClient&lt;IExchangeRateService, FrankfurterExchangeRateService&gt;()</c>, a transient typed
/// client, and <c>ChatSessionService</c> is scoped and holds this skill for the whole Blazor circuit.
/// Resolving a fresh instance per script call (rather than once in the constructor) avoids holding one
/// transient <see cref="HttpClient"/>-wrapping instance for the circuit's life, which would never benefit
/// from <see cref="IHttpClientFactory"/>'s handler rotation.
/// </remarks>
public sealed class ExchangeRateSkill(IServiceScopeFactory scopeFactory)
    : AgentClassSkill<ExchangeRateSkill>(argumentMarshaler: null)
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        name: "exchange-rates",
        description: "Look up current currency exchange rates and convert an amount between currencies.",
        compatibility: null);

    protected override string Instructions =>
        """
        Use this skill when the user asks about currency exchange rates or wants an amount converted
        between currencies (e.g. "how much is 100 USD in THB").
        - get_exchange_rate: look up the current rate between two currency codes.
        - convert_amount: convert a specific amount from one currency to another using the current rate.
        Currency codes are ISO 4217 (e.g. USD, THB, EUR, GBP, JPY). If a rate can't be fetched right now,
        say so plainly rather than guessing a number.
        """;

    [AgentSkillScript("get_exchange_rate")]
    public async Task<string> GetExchangeRateAsync(string fromCurrency, string toCurrency)
    {
        var rate = await GetRateAsync(fromCurrency, toCurrency);
        return rate is null
            ? $"Error: couldn't fetch the exchange rate for {fromCurrency} to {toCurrency} right now."
            : $"1 {fromCurrency} = {rate.Value.ToString("N4", System.Globalization.CultureInfo.InvariantCulture)} {toCurrency}";
    }

    [AgentSkillScript("convert_amount")]
    public async Task<string> ConvertAmountAsync(decimal amount, string fromCurrency, string toCurrency)
    {
        var rate = await GetRateAsync(fromCurrency, toCurrency);
        if (rate is null)
        {
            return $"Error: couldn't fetch the exchange rate for {fromCurrency} to {toCurrency} right now.";
        }

        var converted = amount * rate.Value;
        return $"{CurrencyFormatter.Format(amount, fromCurrency)} = {CurrencyFormatter.Format(converted, toCurrency)}";
    }

    private async Task<decimal?> GetRateAsync(string fromCurrency, string toCurrency)
    {
        using var scope = scopeFactory.CreateScope();
        var exchangeRateService = scope.ServiceProvider.GetRequiredService<IExchangeRateService>();
        return await exchangeRateService.GetRateAsync(fromCurrency, toCurrency);
    }
}
