namespace FinanceApp.Core.Entities;

/// <summary>
/// A single seeded row for v1 (see docs/spec.md §0/§2 — no auth/login system yet).
/// Other entities carry a <see cref="Transaction.UserId"/>-style FK so multi-user is a
/// smaller future diff rather than a rewrite.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }

    public required string DisplayName { get; set; }

    public required string Email { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
