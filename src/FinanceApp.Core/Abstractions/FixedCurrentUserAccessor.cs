namespace FinanceApp.Core.Abstractions;

/// <summary>
/// <see cref="ICurrentUserAccessor"/> implementation that returns a fixed, pre-determined user id —
/// for contexts with no ambient HTTP request/Blazor circuit to derive one from (agent/skill scripts
/// firing via <c>IServiceScopeFactory.CreateScope()</c>, the MCP server, tests). The caller must obtain
/// <paramref name="userId"/> from a trustworthy source itself (e.g. the value a skill was constructed
/// with, captured from <c>ChatSessionService</c>/<c>ICurrentUserAccessor</c> at session-start time) —
/// this type performs no validation of its own. See docs/spec.md §2a point 7 and §3.4.
/// </summary>
public sealed class FixedCurrentUserAccessor(Guid? userId) : ICurrentUserAccessor
{
    public Guid? UserId { get; } = userId;
}
