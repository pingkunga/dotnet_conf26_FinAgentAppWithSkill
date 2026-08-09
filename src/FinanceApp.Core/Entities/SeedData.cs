namespace FinanceApp.Core.Entities;

/// <summary>
/// Fixed IDs for the single seeded user (v1, no auth) and the default category set, used by
/// FinanceDbContext.OnModelCreating's HasData calls and by the query filter scoping every
/// entity to this one user.
/// </summary>
public static class SeedData
{
    public static readonly Guid DefaultUserId = new("00000000-0000-0000-0000-000000000001");

    public static readonly Guid GroceriesCategoryId = new("00000000-0000-0000-0000-000000000101");
    public static readonly Guid DiningCategoryId = new("00000000-0000-0000-0000-000000000102");
    public static readonly Guid TransportCategoryId = new("00000000-0000-0000-0000-000000000103");
    public static readonly Guid UtilitiesCategoryId = new("00000000-0000-0000-0000-000000000104");
    public static readonly Guid EntertainmentCategoryId = new("00000000-0000-0000-0000-000000000105");
    public static readonly Guid IncomeCategoryId = new("00000000-0000-0000-0000-000000000106");
    public static readonly Guid OtherCategoryId = new("00000000-0000-0000-0000-000000000107");
}
