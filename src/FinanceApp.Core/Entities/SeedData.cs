namespace FinanceApp.Core.Entities;

/// <summary>
/// Fixed IDs for the global default category set (see docs/spec.md §2a point 2 — these categories
/// carry <c>UserId = null</c>, not a real <c>ApplicationUser</c> id), used by
/// FinanceDbContext.OnModelCreating's HasData call.
/// </summary>
public static class SeedData
{
    public static readonly Guid GroceriesCategoryId = new("00000000-0000-0000-0000-000000000101");
    public static readonly Guid DiningCategoryId = new("00000000-0000-0000-0000-000000000102");
    public static readonly Guid TransportCategoryId = new("00000000-0000-0000-0000-000000000103");
    public static readonly Guid UtilitiesCategoryId = new("00000000-0000-0000-0000-000000000104");
    public static readonly Guid EntertainmentCategoryId = new("00000000-0000-0000-0000-000000000105");
    public static readonly Guid IncomeCategoryId = new("00000000-0000-0000-0000-000000000106");
    public static readonly Guid OtherCategoryId = new("00000000-0000-0000-0000-000000000107");
}
