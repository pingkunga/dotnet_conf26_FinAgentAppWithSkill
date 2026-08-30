using System.Globalization;
using System.Text.Json;
using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceApp.Skills.ReceiptOcr;

/// <summary>
/// Code-defined inline skill (docs/spec.md §4.2) — built fresh per receipt upload, not a static instance,
/// so it can close over the specific <see cref="Receipt"/> and the app's own <see cref="IChatClient"/>.
/// Registered into a running chat session via <c>ChatSessionService.RegisterReceiptSkillAsync</c>, which
/// adds it to a mutable, uncached skills source — confirmed via a spike that the framework re-discovers
/// skills every turn in that mode, so a skill can join an in-progress conversation without rebuilding the
/// agent/session (and losing chat history).
/// </summary>
public static class ReceiptOcrSkillFactory
{
    public static AgentInlineSkill Create(
        IChatClient chatClient, IServiceScopeFactory scopeFactory, Guid userId, Guid receiptId, bool supportsVision)
    {
        var skill = new AgentInlineSkill(
            name: $"receipt-ocr-{receiptId:N}",
            description: "Extract vendor, amount, and date from this specific uploaded receipt image and record it as a transaction.",
            instructions: "Call extract_receipt to run OCR on this receipt and create the resulting transaction from it. " +
                          "To check this receipt's current status without re-running OCR, read the receipt_status resource instead.",
            license: null,
            compatibility: null,
            allowedTools: null,
            metadata: null,
            serializerOptions: null,
            argumentMarshaler: null);

        skill.AddScript(
            "extract_receipt",
            async Task<string> () => await ExtractAsync(chatClient, scopeFactory, userId, receiptId, supportsVision),
            "Runs OCR on the uploaded receipt image and creates a Transaction from the extracted fields. Takes no arguments — the receipt is fixed at skill-creation time.",
            null);

        // ChatSessionService.cs: DisableReadSkillResourceApproval = true, unconditional, 
        // what's the status of this receipt"
        skill.AddResource(
            "receipt_status",
            async Task<string> () => await DescribeStatusAsync(scopeFactory, userId, receiptId),
            "Current status of this receipt (pending/succeeded/failed/manual) and any already-extracted fields, " +
            "without re-running OCR. Read this to answer status questions instead of calling extract_receipt again.",
            null);

        return skill;
    }

    private static async Task<string> DescribeStatusAsync(IServiceScopeFactory scopeFactory, Guid userId, Guid receiptId)
    {
        using var scope = scopeFactory.CreateScope();
        await using var db = CreateDbContext(scope.ServiceProvider, userId);

        var receipt = await db.Receipts.Include(r => r.ExtractedCategory).FirstOrDefaultAsync(r => r.Id == receiptId);
        if (receipt is null)
        {
            // Query-filtered to userId already (FinanceDbContext.OnModelCreating) — same "not found or
            // someone else's" collapse as ExtractAsync's own not-found branch.
            return $"Error: receipt {receiptId} not found.";
        }

        if (receipt.OcrStatus == ReceiptOcrStatus.Pending)
        {
            return "Status: Pending. No extraction has run yet — call extract_receipt to process it.";
        }

        if (receipt.OcrStatus == ReceiptOcrStatus.Failed)
        {
            return $"Status: Failed. Raw AI response: {receipt.OcrRawResponse ?? "(none)"}.";
        }

        // Succeeded or Manual — both have real extracted fields worth reporting.
        var fields = $"vendor={receipt.ExtractedVendor ?? "(unknown)"}, " +
                     $"amount={(receipt.ExtractedAmount is { } a ? a.ToString("C") : "(unknown)")}, " +
                     $"date={(receipt.ExtractedDate is { } d ? d.ToString("d") : "(unknown)")}, " +
                     $"category={receipt.ExtractedCategory?.Name ?? "(unknown)"}";

        return $"Status: {receipt.OcrStatus}. {fields}. Linked transaction: {receipt.ResultingTransactionId}.";
    }

    private static async Task<string> ExtractAsync(
        IChatClient chatClient, IServiceScopeFactory scopeFactory, Guid userId, Guid receiptId, bool supportsVision)
    {
        // Graceful degradation
        if (!supportsVision)
        {
            return "This AI provider doesn't support image input — please enter the receipt's vendor, " +
                   "amount, and date manually instead.";
        }

        using var scope = scopeFactory.CreateScope();
        await using var db = CreateDbContext(scope.ServiceProvider, userId);

        var receipt = await db.Receipts.FirstOrDefaultAsync(r => r.Id == receiptId);
        if (receipt is null)
        {
            // Query-filtered to userId already (FinanceDbContext.OnModelCreating) — this also covers
            // "exists but belongs to someone else" without distinguishing the two cases in the response.
            return $"Error: receipt {receiptId} not found.";
        }

        if (receipt.OcrStatus != ReceiptOcrStatus.Pending)
        {
            return $"This receipt was already processed (status: {receipt.OcrStatus}).";
        }

        string rawResponse;
        try
        {
            var response = await chatClient.GetResponseAsync(
            [
                new ChatMessage(ChatRole.User,
                [
                    new TextContent(ExtractionPrompt),
                    new DataContent(receipt.ImageBytes, receipt.ContentType),
                ]),
            ]);
            rawResponse = response.Text;
        }
        catch (Exception ex)
        {
            receipt.OcrStatus = ReceiptOcrStatus.Failed;
            receipt.OcrRawResponse = $"Error calling the AI provider: {ex.Message}";
            await db.SaveChangesAsync();
            return "Error: the AI provider call failed. The receipt is marked failed — try manual entry instead.";
        }

        receipt.OcrRawResponse = rawResponse;

        var extracted = TryParseExtraction(rawResponse);
        receipt.ExtractedVendor = extracted?.Vendor;
        receipt.ExtractedAmount = extracted?.Amount;
        receipt.ExtractedDate = extracted?.Date;

        if (extracted?.Amount is not { } amount)
        {
            // No amount worth creating a Transaction from — a partial extraction (or a parse failure) is
            // "Failed", not a crash; OcrRawResponse above is kept either way for debugging (docs/spec.md §4.2).
            receipt.OcrStatus = ReceiptOcrStatus.Failed;
            await db.SaveChangesAsync();
            return $"Couldn't extract a usable amount from this receipt. Raw AI response: {rawResponse}";
        }

        var category = await FindCategoryAsync(db, extracted.Category)
            ?? await db.Categories.FirstAsync(c => c.Id == SeedData.OtherCategoryId);

        
        // grounding the AI's guess in the user's own history is a deliberate design choice 
        string? historyNote = null;
        if (extracted.Vendor is { Length: > 0 } vendor)
        {
            var (historicalCategory, matchCount) = await FindVendorHistoryCategoryAsync(db, userId, vendor);
            if (historicalCategory is not null && historicalCategory.Id != category.Id)
            {
                historyNote = $" (based on {matchCount} prior transaction(s) at this vendor, overriding the AI's initial guess of '{extracted.Category ?? "(none)"}')";
                category = historicalCategory;
            }
        }

        receipt.ExtractedCategoryId = category.Id;

        var transaction = new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CategoryId = category.Id,
            Amount = amount,

            OccurredOn = extracted.Date ?? DateOnly.FromDateTime(DateTime.Now),
            Description = extracted.Vendor,
            Source = TransactionSource.ReceiptOcr,
            ReceiptId = receipt.Id,
            CreatedAtUtc = DateTime.UtcNow,
        };
        db.Transactions.Add(transaction);

        receipt.OcrStatus = ReceiptOcrStatus.Succeeded;
        receipt.ResultingTransactionId = transaction.Id;

        await db.SaveChangesAsync();

        return $"Extracted vendor={extracted.Vendor ?? "(unknown)"}, amount={amount:C}, " +
               $"date={transaction.OccurredOn:d}, category={category.Name}{historyNote}. Recorded as a transaction.";
    }

    /// <summary>
    /// Finds the most common category among the user's past transactions whose description contains
    /// <paramref name="vendor"/> (case-insensitive substring — deliberately <c>.ToLower().Contains(...)</c>,
    /// not <c>EF.Functions.ILike</c>, which is Npgsql-only and would throw against this project's InMemory-
    /// backed unit tests). Requires at least 2 matches before returning a category at all, so a single past
    /// mis-categorization can't override anything.
    /// </summary>
    private static async Task<(Category? Category, int Count)> FindVendorHistoryCategoryAsync(
        FinanceDbContext db, Guid userId, string vendor)
    {
        var vendorLower = vendor.ToLower();

        var grouped = await db.Transactions
            .Where(t => t.UserId == userId && t.Description != null && t.Description.ToLower().Contains(vendorLower))
            .GroupBy(t => t.CategoryId)
            .Select(g => new { CategoryId = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .FirstOrDefaultAsync();

        if (grouped is null || grouped.Count < 2)
        {
            return (null, 0);
        }

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == grouped.CategoryId);
        return (category, grouped.Count);
    }

    private const string ExtractionPrompt = """
        Look at this receipt image and extract these fields as a single JSON object and nothing else — no
        markdown, no explanation:
        {"vendor": string or null, "amount": number or null, "date": "YYYY-MM-DD" or null, "category": string or null}
        "category" should be your best guess at a short category label (e.g. "Groceries", "Dining",
        "Transport"). If a field isn't clearly readable, use null for it rather than guessing.
        """;

    /// <summary>
    /// Same construction pattern as <c>BudgetSkill.CreateDbContext</c> (docs/spec.md §3.4's addendum) —
    /// resolves only <see cref="DbContextOptions{FinanceDbContext}"/> from the background scope and builds
    /// <see cref="FinanceDbContext"/> manually with a <see cref="FixedCurrentUserAccessor"/> bound to
    /// <paramref name="userId"/>, because the real <see cref="ICurrentUserAccessor"/> has nothing to derive
    /// a user from outside an HTTP/circuit context.
    /// </summary>
    private static FinanceDbContext CreateDbContext(IServiceProvider scopedProvider, Guid userId)
    {
        var options = scopedProvider.GetRequiredService<DbContextOptions<FinanceDbContext>>();
        return new FinanceDbContext(options, new FixedCurrentUserAccessor(userId));
    }

    private static async Task<Category?> FindCategoryAsync(FinanceDbContext db, string? categoryName) =>
        string.IsNullOrWhiteSpace(categoryName)
            ? null
            : await db.Categories.FirstOrDefaultAsync(c => c.Name.ToLower() == categoryName.ToLower());

    /// <summary>
    /// Tolerant on purpose — model output is never trusted to be exactly the requested JSON shape. Finds
    /// the first <c>{...}</c> substring (handles a model wrapping the JSON in prose or a markdown fence)
    /// and parses only known fields; any field that's missing, the wrong type, or fails to parse is simply
    /// left null rather than throwing, so an imperfect OCR result degrades to "partially extracted", not
    /// a skill-script exception.
    /// </summary>
    private static ExtractedFields? TryParseExtraction(string rawResponse)
    {
        var start = rawResponse.IndexOf('{');
        var end = rawResponse.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(rawResponse[start..(end + 1)]);
            var root = doc.RootElement;

            string? vendor = root.TryGetProperty("vendor", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;

            decimal? amount = root.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetDecimal(out var parsedAmount)
                ? parsedAmount
                : null;

            DateOnly? date = root.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
                && DateOnly.TryParse(d.GetString(), CultureInfo.InvariantCulture, out var parsedDate)
                ? parsedDate
                : null;

            string? category = root.TryGetProperty("category", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            return new ExtractedFields(vendor, amount, date, category);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ExtractedFields(string? Vendor, decimal? Amount, DateOnly? Date, string? Category);
}
