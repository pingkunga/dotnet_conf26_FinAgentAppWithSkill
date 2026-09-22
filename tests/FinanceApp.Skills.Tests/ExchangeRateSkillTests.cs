using FinanceApp.Core.Abstractions;
using FinanceApp.Skills.ExchangeRates;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceApp.Skills.Tests;

/// <summary>
/// Calls <see cref="ExchangeRateSkill"/>'s <c>[AgentSkillScript]</c> methods as plain async C# calls — no
/// LLM/agent, no DB (this skill touches no per-user data), no EF Core InMemory. Simplest test setup in the
/// project: just a minimal <see cref="ServiceCollection"/> registering <see cref="FakeExchangeRateService"/>
/// as <see cref="IExchangeRateService"/>, wrapped in a real <see cref="IServiceScopeFactory"/> — mirrors
/// what the skill resolves per script call in production (see its class remarks for why).
/// </summary>
public sealed class ExchangeRateSkillTests
{
    private static ExchangeRateSkill CreateSkill(IReadOnlyDictionary<(string, string), decimal?> rates)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IExchangeRateService>(new FakeExchangeRateService(rates));
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new ExchangeRateSkill(scopeFactory);
    }

    [Fact]
    public async Task GetExchangeRate_KnownPair_ReturnsTheRate()
    {
        var skill = CreateSkill(new Dictionary<(string, string), decimal?> { [("USD", "THB")] = 35m });

        var result = await skill.GetExchangeRateAsync("USD", "THB");

        Assert.Equal("1 USD = 35.0000 THB", result);
    }

    [Fact]
    public async Task ConvertAmount_KnownPair_ReturnsBothFormattedAmounts()
    {
        var skill = CreateSkill(new Dictionary<(string, string), decimal?> { [("USD", "THB")] = 35m });

        var result = await skill.ConvertAmountAsync(100m, "USD", "THB");

        Assert.Equal("$100.00 = ฿3,500.00", result);
    }

    [Fact]
    public async Task GetExchangeRate_UnavailableRate_ReturnsAnErrorStringRatherThanGuessing()
    {
        var skill = CreateSkill(new Dictionary<(string, string), decimal?>());

        var result = await skill.GetExchangeRateAsync("USD", "THB");

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task ConvertAmount_UnavailableRate_ReturnsAnErrorStringRatherThanGuessing()
    {
        var skill = CreateSkill(new Dictionary<(string, string), decimal?>());

        var result = await skill.ConvertAmountAsync(100m, "USD", "THB");

        Assert.StartsWith("Error:", result);
    }

    [Fact]
    public async Task ConvertAmount_SameCurrency_ShortCircuitsToOneToOne()
    {
        var skill = CreateSkill(new Dictionary<(string, string), decimal?>());

        var result = await skill.ConvertAmountAsync(100m, "USD", "USD");

        Assert.Equal("$100.00 = $100.00", result);
    }
}
