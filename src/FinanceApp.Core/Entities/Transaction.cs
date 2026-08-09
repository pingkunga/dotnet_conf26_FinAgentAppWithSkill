namespace FinanceApp.Core.Entities;

public sealed class Transaction
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public Guid? CategoryId { get; set; }

    public Category? Category { get; set; }

    /// <summary>Positive for both income and expense; <see cref="Category.Kind"/> gives the sign meaning.</summary>
    public decimal Amount { get; set; }

    public string Currency { get; set; } = "USD";

    public DateOnly OccurredOn { get; set; }

    public string? Description { get; set; }

    public TransactionSource Source { get; set; } = TransactionSource.Manual;

    /// <summary>Set when this transaction was created from a receipt via the Receipt OCR skill.</summary>
    public Guid? ReceiptId { get; set; }

    public Receipt? Receipt { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}
