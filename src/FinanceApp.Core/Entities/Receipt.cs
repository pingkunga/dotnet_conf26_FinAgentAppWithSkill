namespace FinanceApp.Core.Entities;

/// <summary>
/// An uploaded receipt image plus the result of running it through the Receipt OCR inline skill
/// (see docs/spec.md §4.2). Image bytes are stored directly in Postgres (bytea) for v1 — no
/// external object storage — which is fine at demo scale.
/// </summary>
public sealed class Receipt
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public required byte[] ImageBytes { get; set; }

    public required string ContentType { get; set; }

    public DateTime UploadedAtUtc { get; set; }

    public ReceiptOcrStatus OcrStatus { get; set; } = ReceiptOcrStatus.Pending;

    /// <summary>Raw model output, kept for debugging OCR extraction quality.</summary>
    public string? OcrRawResponse { get; set; }

    public string? ExtractedVendor { get; set; }

    public decimal? ExtractedAmount { get; set; }

    public DateOnly? ExtractedDate { get; set; }

    public Guid? ExtractedCategoryId { get; set; }

    public Category? ExtractedCategory { get; set; }

    /// <summary>Set once the skill has created the resulting Transaction from this receipt.</summary>
    public Guid? ResultingTransactionId { get; set; }
}
