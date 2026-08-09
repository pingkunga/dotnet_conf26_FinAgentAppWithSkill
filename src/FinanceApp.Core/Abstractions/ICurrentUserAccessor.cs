namespace FinanceApp.Core.Abstractions;

/// <summary>
/// Resolves the requesting user's id for the current operation. The interface lives in <c>Core</c>
/// (no ASP.NET dependency) so <see cref="FinanceApp.Core.FinanceDbContext"/> can depend on it directly;
/// the real implementation — reading an authenticated Blazor circuit's <c>AuthenticationStateProvider</c>
/// — lives in <c>FinanceApp.Web/Services</c> (see docs/spec.md §2a point 3).
/// </summary>
/// <remarks>
/// Firm rule (docs/spec.md §2a point 7): every agent/skill call must be scoped to this value, never to a
/// user id taken from an LLM-provided argument.
/// </remarks>
public interface ICurrentUserAccessor
{
    /// <summary>The authenticated user's id, or <see langword="null"/> if there is no authenticated user.</summary>
    Guid? UserId { get; }
}
