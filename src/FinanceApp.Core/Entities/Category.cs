namespace FinanceApp.Core.Entities;

public sealed class Category
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    public CategoryKind Kind { get; set; }

    /// <summary>True for the seeded starter categories (Groceries, Dining, ...) — see SeedData.</summary>
    public bool IsSystemDefault { get; set; }
}
