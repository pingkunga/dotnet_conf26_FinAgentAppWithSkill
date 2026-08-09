namespace FinanceApp.Core.Entities;

public sealed class SavingsGoal
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required string Name { get; set; }

    public decimal TargetAmount { get; set; }

    public decimal CurrentAmount { get; set; }

    public DateOnly? TargetDate { get; set; }

    public decimal? MonthlyContribution { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
