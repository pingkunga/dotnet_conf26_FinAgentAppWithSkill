using Microsoft.AspNetCore.Identity;

namespace FinanceApp.Core.Entities;

/// <summary>
/// The app's Identity-backed user (see docs/spec.md §2a). Replaces the earlier hand-rolled
/// <c>User</c> entity — <c>Email</c>/<c>UserName</c> come from <see cref="IdentityUser{TKey}"/> itself;
/// <see cref="DisplayName"/> is the one extra profile property this app needs.
/// </summary>
public sealed class ApplicationUser : IdentityUser<Guid>
{
    public string DisplayName { get; set; } = "";

    // Per-action-kind approval toggles for AgentSkill
    public bool AutoApproveWrites { get; set; } = true;
    public bool AutoApproveExecuteScript { get; set; } = true;
}
