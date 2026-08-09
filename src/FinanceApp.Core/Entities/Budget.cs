namespace FinanceApp.Core.Entities;

/// <summary>
/// A per-category spending limit for one calendar month. <see cref="PeriodMonth"/> is always
/// normalized to the first day of the month; the combination (UserId, CategoryId, PeriodMonth)
/// is unique — see FinanceDbContext.OnModelCreating.
/// </summary>
public sealed class Budget
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid CategoryId { get; set; }

    public Category Category { get; set; } = null!;

    public DateOnly PeriodMonth { get; set; }

    public decimal LimitAmount { get; set; }
}
