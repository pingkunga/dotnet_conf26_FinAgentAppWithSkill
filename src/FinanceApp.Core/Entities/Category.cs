namespace FinanceApp.Core.Entities;

public sealed class Category
{
    public Guid Id { get; set; }

    /// <summary>
    /// Null for the global starter category set (<see cref="IsSystemDefault"/> = true) — see
    /// docs/spec.md §2a point 2 for why this is nullable rather than a straight FK to
    /// <c>ApplicationUser</c>. Set to a real user id for user-created categories.
    /// </summary>
    public Guid? UserId { get; set; }

    public required string Name { get; set; }

    public CategoryKind Kind { get; set; }

    /// <summary>True for the seeded starter categories (Groceries, Dining, ...) — see SeedData.</summary>
    public bool IsSystemDefault { get; set; }
}
