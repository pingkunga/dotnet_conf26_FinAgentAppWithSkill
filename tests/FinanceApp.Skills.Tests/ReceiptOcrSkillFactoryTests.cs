using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Skills.ReceiptOcr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace FinanceApp.Skills.Tests;

/// <summary>
/// Covers <see cref="ReceiptOcrSkillFactory"/>'s deterministic paths only (docs/spec.md §4.2/§8): the
/// non-vision short-circuit, and the DB/category-matching logic behind a scripted <see cref="IChatClient"/>
/// returning canned JSON. The actual multimodal OCR call against a real vision-capable provider isn't
/// mechanically testable here (same ceiling as <c>savings-calculator</c>'s python3 gap) — flagged for a
/// manual check once one is running, not covered by this suite.
/// </summary>
public sealed class ReceiptOcrSkillFactoryTests
{
    [Fact]
    public async Task Extract_WhenVisionUnsupported_ReturnsManualEntryMessage_WithoutTouchingChatClientOrDb()
    {
        // chatClient/scopeFactory are never dereferenced on this path — null! makes that a hard guarantee,
        // not just an assumption.
        var skill = ReceiptOcrSkillFactory.Create(
            chatClient: null!, scopeFactory: null!, userId: Guid.NewGuid(), receiptId: Guid.NewGuid(), supportsVision: false);

        var result = await RunExtractScriptAsync(skill);

        Assert.Contains("manually", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Extract_WithAMatchingCategoryName_CreatesTransactionAndMarksReceiptSucceeded()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        var chatClient = new FakeChatClient("""{"vendor":"Starbucks","amount":4.5,"date":"2026-08-01","category":"Dining"}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        var result = await RunExtractScriptAsync(skill);

        Assert.Contains("Starbucks", result);

        await using var db = await OpenDbAsync(dbName, userId);
        var savedReceipt = await db.Receipts.FirstAsync(r => r.Id == receipt.Id);
        Assert.Equal(ReceiptOcrStatus.Succeeded, savedReceipt.OcrStatus);
        Assert.Equal("Starbucks", savedReceipt.ExtractedVendor);
        Assert.NotNull(savedReceipt.ResultingTransactionId);

        var transaction = await db.Transactions.FirstAsync(t => t.Id == savedReceipt.ResultingTransactionId);
        Assert.Equal(TransactionSource.ReceiptOcr, transaction.Source);
        Assert.Equal(4.5m, transaction.Amount);
        Assert.Equal(receipt.Id, transaction.ReceiptId);

        var category = await db.Categories.FirstAsync(c => c.Id == transaction.CategoryId);
        Assert.Equal("Dining", category.Name);
    }

    [Fact]
    public async Task Extract_WithADetectedCurrencyDifferentFromPreferred_RecordsItRawWithoutConverting()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        // No ApplicationUser row is seeded, so GetPreferredCurrencyAsync falls back to "USD" — this receipt
        // is in THB, a different currency, so it should be recorded as-is (350 THB), not converted.
        var chatClient = new FakeChatClient("""{"vendor":"Bangkok Cafe","amount":350,"date":"2026-08-01","category":"Dining","currency":"THB"}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        var result = await RunExtractScriptAsync(skill);

        Assert.Contains("recorded in THB", result);

        await using var db = await OpenDbAsync(dbName, userId);
        var savedReceipt = await db.Receipts.FirstAsync(r => r.Id == receipt.Id);
        Assert.Equal("THB", savedReceipt.ExtractedCurrency);

        var transaction = await db.Transactions.FirstAsync(t => t.ReceiptId == receipt.Id);
        Assert.Equal("THB", transaction.Currency);
        Assert.Equal(350m, transaction.Amount);
    }

    [Fact]
    public async Task Extract_WithNoCurrencyField_FallsBackToThePreferredCurrencyWithoutANote()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        var chatClient = new FakeChatClient("""{"vendor":"Starbucks","amount":4.5,"date":"2026-08-01","category":"Dining"}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        var result = await RunExtractScriptAsync(skill);

        Assert.DoesNotContain("recorded in", result);

        await using var db = await OpenDbAsync(dbName, userId);
        var transaction = await db.Transactions.FirstAsync(t => t.ReceiptId == receipt.Id);
        Assert.Equal("USD", transaction.Currency);
        var savedReceipt = await db.Receipts.FirstAsync(r => r.Id == receipt.Id);
        Assert.Equal("USD", savedReceipt.ExtractedCurrency);
    }

    [Fact]
    public async Task Extract_WithAnUnrecognizedCurrencyCode_FallsBackToThePreferredCurrencyRatherThanStoringGarbage()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        var chatClient = new FakeChatClient("""{"vendor":"Somewhere","amount":10,"date":"2026-08-01","category":"Dining","currency":"XYZ"}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        await RunExtractScriptAsync(skill);

        await using var db = await OpenDbAsync(dbName, userId);
        var transaction = await db.Transactions.FirstAsync(t => t.ReceiptId == receipt.Id);
        Assert.Equal("USD", transaction.Currency);
    }

    [Fact]
    public async Task Extract_WithALowercaseCurrencyCode_NormalizesItToUppercase()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        var chatClient = new FakeChatClient("""{"vendor":"Bangkok Cafe","amount":350,"date":"2026-08-01","category":"Dining","currency":"thb"}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        await RunExtractScriptAsync(skill);

        await using var db = await OpenDbAsync(dbName, userId);
        var transaction = await db.Transactions.FirstAsync(t => t.ReceiptId == receipt.Id);
        Assert.Equal("THB", transaction.Currency);
    }

    [Fact]
    public async Task Extract_WithAnUnrecognizedCategoryName_FallsBackToOther()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        var chatClient = new FakeChatClient("""{"vendor":"Mystery Shop","amount":12,"date":"2026-08-01","category":"NotARealCategory"}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        await RunExtractScriptAsync(skill);

        await using var db = await OpenDbAsync(dbName, userId);
        var transaction = await db.Transactions.FirstAsync(t => t.ReceiptId == receipt.Id);
        Assert.Equal(SeedData.OtherCategoryId, transaction.CategoryId);
    }

    [Fact]
    public async Task Extract_WithTwoOrMorePriorTransactionsAtVendor_OverridesTheVisionGuess()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);
        await SeedPastTransactionAsync(dbName, userId, "Corner Cafe receipt", SeedData.DiningCategoryId);
        await SeedPastTransactionAsync(dbName, userId, "Corner Cafe lunch", SeedData.DiningCategoryId);

        // Vision guesses "Groceries" — history at this vendor says Dining, 2 matching prior transactions.
        var chatClient = new FakeChatClient("""{"vendor":"Corner Cafe","amount":15,"date":"2026-08-01","category":"Groceries"}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        var result = await RunExtractScriptAsync(skill);

        Assert.Contains("category=Dining", result);
        Assert.Contains("overriding", result, StringComparison.OrdinalIgnoreCase);

        await using var db = await OpenDbAsync(dbName, userId);
        var transaction = await db.Transactions.FirstAsync(t => t.ReceiptId == receipt.Id);
        Assert.Equal(SeedData.DiningCategoryId, transaction.CategoryId);
    }

    [Fact]
    public async Task Extract_WithOnlyOnePriorTransactionAtVendor_DoesNotOverride()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);
        await SeedPastTransactionAsync(dbName, userId, "Corner Cafe receipt", SeedData.DiningCategoryId);

        var chatClient = new FakeChatClient("""{"vendor":"Corner Cafe","amount":15,"date":"2026-08-01","category":"Groceries"}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        var result = await RunExtractScriptAsync(skill);

        Assert.Contains("category=Groceries", result);
        Assert.DoesNotContain("overriding", result, StringComparison.OrdinalIgnoreCase);

        await using var db = await OpenDbAsync(dbName, userId);
        var transaction = await db.Transactions.FirstAsync(t => t.ReceiptId == receipt.Id);
        Assert.Equal(SeedData.GroceriesCategoryId, transaction.CategoryId);
    }

    [Fact]
    public async Task Extract_WhenAmountIsMissing_MarksFailedAndCreatesNoTransaction()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        // No "amount" field at all — TryParseExtraction leaves it null rather than throwing.
        var chatClient = new FakeChatClient("""{"vendor":"Blurry Receipt","date":null}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        var result = await RunExtractScriptAsync(skill);

        Assert.StartsWith("Couldn't extract", result);

        await using var db = await OpenDbAsync(dbName, userId);
        var savedReceipt = await db.Receipts.FirstAsync(r => r.Id == receipt.Id);
        Assert.Equal(ReceiptOcrStatus.Failed, savedReceipt.OcrStatus);
        Assert.False(await db.Transactions.AnyAsync(t => t.ReceiptId == receipt.Id));
    }

    [Fact]
    public async Task Extract_WhenChatClientThrows_MarksFailedWithTheErrorRecorded()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        var chatClient = new ThrowingChatClient();
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        var result = await RunExtractScriptAsync(skill);

        Assert.StartsWith("Error:", result);

        await using var db = await OpenDbAsync(dbName, userId);
        var savedReceipt = await db.Receipts.FirstAsync(r => r.Id == receipt.Id);
        Assert.Equal(ReceiptOcrStatus.Failed, savedReceipt.OcrStatus);
        Assert.Contains("simulated provider failure", savedReceipt.OcrRawResponse);
    }

    [Fact]
    public async Task ReceiptStatus_WhenPending_ReturnsPendingMessage_WithoutRunningOcr()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        // chatClient is never dereferenced by the resource — null! makes that a hard guarantee, same
        // reasoning as the vision-unsupported script test above.
        var skill = ReceiptOcrSkillFactory.Create(chatClient: null!, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);

        var result = await RunStatusResourceAsync(skill);

        Assert.Contains("Pending", result);

        await using var db = await OpenDbAsync(dbName, userId);
        Assert.False(await db.Transactions.AnyAsync(t => t.ReceiptId == receipt.Id));
    }

    [Fact]
    public async Task ReceiptStatus_WhenSucceeded_ReturnsExtractedFieldsAndTransactionId()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        var chatClient = new FakeChatClient("""{"vendor":"Starbucks","amount":4.5,"date":"2026-08-01","category":"Dining"}""");
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);
        await RunExtractScriptAsync(skill);

        var result = await RunStatusResourceAsync(skill);

        Assert.Contains("Succeeded", result);
        Assert.Contains("Starbucks", result);
        Assert.Contains("Dining", result);

        await using var db = await OpenDbAsync(dbName, userId);
        var savedReceipt = await db.Receipts.FirstAsync(r => r.Id == receipt.Id);
        Assert.Contains(savedReceipt.ResultingTransactionId.ToString()!, result);
    }

    [Fact]
    public async Task ReceiptStatus_WhenFailed_ReturnsRawResponse()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        var receipt = await SeedPendingReceiptAsync(dbName, userId);

        var chatClient = new FakeChatClient("""{"vendor":"Blurry Receipt","date":null}"""); // no amount
        var skill = ReceiptOcrSkillFactory.Create(chatClient, BuildScopeFactory(dbName), userId, receipt.Id, supportsVision: true);
        await RunExtractScriptAsync(skill);

        var result = await RunStatusResourceAsync(skill);

        Assert.Contains("Failed", result);
        Assert.Contains("Blurry Receipt", result); // the raw AI response text
    }

    [Fact]
    public async Task ReceiptStatus_WhenReceiptDoesNotExist_ReturnsError()
    {
        var dbName = Guid.NewGuid().ToString();
        var userId = Guid.NewGuid();
        await using (var db = await OpenDbAsync(dbName, userId)) { } // materialize the InMemory DB, no receipt seeded

        var skill = ReceiptOcrSkillFactory.Create(chatClient: null!, BuildScopeFactory(dbName), userId, receiptId: Guid.NewGuid(), supportsVision: true);

        var result = await RunStatusResourceAsync(skill);

        Assert.Contains("not found", result, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RunExtractScriptAsync(Microsoft.Agents.AI.AgentInlineSkill skill)
    {
        var script = await skill.GetScriptAsync("extract_receipt", CancellationToken.None);
        Assert.NotNull(script);
        var result = await script.RunAsync(skill, arguments: null, serviceProvider: null, CancellationToken.None);
        // Calling RunAsync directly (bypassing the normal tool-call pipeline) still comes back JSON-
        // serialized, not the raw string the script returned — found via this test failing, not assumed.
        var element = Assert.IsType<System.Text.Json.JsonElement>(result);
        return element.GetString() ?? throw new InvalidOperationException("Script result wasn't a JSON string.");
    }

    private static async Task<string> RunStatusResourceAsync(Microsoft.Agents.AI.AgentInlineSkill skill)
    {
        var resource = await skill.GetResourceAsync("receipt_status", CancellationToken.None);
        Assert.NotNull(resource);
        var result = await resource.ReadAsync(serviceProvider: null, CancellationToken.None);
        // Same JsonElement-wrapping as RunAsync above — confirmed via reflection against the pinned
        // Microsoft.Agents.AI 1.17.0 assembly, not assumed.
        var element = Assert.IsType<System.Text.Json.JsonElement>(result);
        return element.GetString() ?? throw new InvalidOperationException("Resource result wasn't a JSON string.");
    }

    private static IServiceScopeFactory BuildScopeFactory(string dbName)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static async Task<FinanceDbContext> OpenDbAsync(string dbName, Guid? userId)
    {
        var options = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options;
        var db = new FinanceDbContext(options, new FixedCurrentUserAccessor(userId));
        await db.Database.EnsureCreatedAsync(); // materializes the HasData-seeded global categories
        return db;
    }

    private static async Task<Receipt> SeedPendingReceiptAsync(string dbName, Guid userId)
    {
        await using var db = await OpenDbAsync(dbName, userId);
        var receipt = new Receipt
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ImageBytes = [1, 2, 3],
            ContentType = "image/jpeg",
            UploadedAtUtc = DateTime.UtcNow,
            OcrStatus = ReceiptOcrStatus.Pending,
        };
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync();
        return receipt;
    }

    private static async Task SeedPastTransactionAsync(string dbName, Guid userId, string description, Guid categoryId)
    {
        await using var db = await OpenDbAsync(dbName, userId);
        db.Transactions.Add(new Transaction
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            CategoryId = categoryId,
            Amount = 1m,
            OccurredOn = DateOnly.FromDateTime(DateTime.UtcNow),
            Description = description,
            Source = TransactionSource.Manual,
            CreatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private sealed class FakeChatClient(string jsonResponse) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, jsonResponse)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Test double only exercises the non-streaming path.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated provider failure");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
