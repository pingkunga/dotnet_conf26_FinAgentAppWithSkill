using System.Globalization;
using System.Text.Json;
using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Repositories;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FinanceApp.McpServer;

/// <summary>
/// Serves the two resources <c>Microsoft.Agents.AI.Mcp</c>'s skill-index mechanism fetches
/// (docs/spec.md §4.4): the well-known <c>skill://index.json</c> list, and each skill's content/data
/// resources. **This is not a tool-calling bridge** — confirmed via a live spike (server+client, no
/// LLM) that <c>AgentSkillsProviderBuilder.UseMcpSkills(...)</c> fetches <c>skill://index.json</c> at
/// <c>load_skill</c> discovery time, then the index entry's <c>url</c> live via <c>ReadResourceAsync</c>
/// when the skill is actually loaded, and resolves <c>read_skill_resource</c> calls to
/// <c>&lt;skill-root&gt;/&lt;resourceName&gt;</c> **live, per call** — not a prefetched/cached blob. That's
/// what makes this genuinely useful for a monthly summary: <see cref="ReadResourceAsync"/> below computes
/// real numbers from Postgres on every call, scoped to whichever user the current HTTP request belongs to.
/// </summary>
/// <remarks>
/// HTTP transport, Step 1 (docs/spec.md §5). Registered **scoped** in DI — <c>FinanceApp.McpServer</c> is
/// now a standing service shared across every user's requests, so a fresh instance (and a fresh,
/// request-scoped <paramref name="db"/>) is resolved per HTTP request via <c>RequestContext&lt;T&gt;
/// .Services</c>, never captured once for the process's whole lifetime the way the old stdio-per-session
/// design did. <paramref name="currentUserAccessor"/> resolves userId from the authenticated request's
/// JWT claims (see <c>HttpUserContextAccessor</c>) — never from an MCP request argument (CLAUDE.md's firm
/// "never trust an LLM-provided user-identifier-shaped value" rule — MCP resource/tool arguments are
/// exactly that, and doubly so now that one process serves every user).
/// </remarks>
public sealed class MonthlySummaryResourceHandlers(
    FinanceDbContext db,
    ICurrentUserAccessor currentUserAccessor,
    ILogger<MonthlySummaryResourceHandlers> logger)
{
    private const string SkillName = "monthly-summary";
    private const string IndexUri = "skill://index.json";
    private const string SkillMdUri = "skill://monthly-summary/SKILL.md";
    private const string ResourceUriPrefix = "skill://monthly-summary/";

    // Thin wrappers around the *Core methods below, which take plain arguments rather than a
    // RequestContext<T> — ModelContextProtocol.Server.RequestContext<T>'s only constructor requires a real
    // (non-null) McpServer + JsonRpcRequest, too heavy to stand up in a unit test. Splitting the actual
    // logic out keeps it testable without a live transport, same "wrapper vs. testable core" split
    // ReceiptOcrSkillFactory uses for its own AI-facing script method (docs/spec.md §4.2).
    public static ValueTask<ListResourcesResult> ListResourcesAsync(RequestContext<ListResourcesRequestParams> _, CancellationToken __) =>
        ListResourcesCoreAsync();

    public ValueTask<ReadResourceResult> ReadResourceAsync(RequestContext<ReadResourceRequestParams> context, CancellationToken cancellationToken)
    {
        var uri = context.Params?.Uri
            ?? throw new McpException("Missing resource uri.");
        return ReadResourceCoreAsync(uri, cancellationToken);
    }

    public static ValueTask<ListResourcesResult> ListResourcesCoreAsync() =>
        ValueTask.FromResult(new ListResourcesResult
        {
            Resources =
            [
                new Resource { Uri = IndexUri, Name = "skill-index", MimeType = "application/json" },
                new Resource { Uri = SkillMdUri, Name = SkillName, MimeType = "text/markdown" },
            ],
        });

    public async ValueTask<ReadResourceResult> ReadResourceCoreAsync(string uri, CancellationToken cancellationToken)
    {
        logger.LogInformation("MCP read_skill_resource: {Uri}", uri);

        var text = uri switch
        {
            IndexUri => BuildIndexJson(),
            SkillMdUri => await ReadSkillMdAsync(cancellationToken),
            _ when uri.StartsWith(ResourceUriPrefix, StringComparison.Ordinal) =>
                await BuildSummaryJsonAsync(uri[ResourceUriPrefix.Length..], cancellationToken),
            _ => throw new McpException($"Unknown resource: {uri}"),
        };

        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = text }],
        };
    }

    private static string BuildIndexJson()
    {
        var index = new
        {
            skills = new[]
            {
                new
                {
                    name = SkillName,
                    type = "skill-md",
                    description = "Produces a monthly income/expense summary with budget-vs-actual status.",
                    url = SkillMdUri,
                    digest = "v1",
                },
            },
        };
        return JsonSerializer.Serialize(index);
    }

    private static Task<string> ReadSkillMdAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "skills", "monthly-summary", "SKILL.md");
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    /// <summary>
    /// <paramref name="resourceName"/> is expected as <c>summary-&lt;year&gt;-&lt;month&gt;</c> (e.g.
    /// <c>summary-2026-08</c>) — MCP resource reads don't carry free-form structured arguments the way
    /// tool calls do, so the month/year travel encoded in the resource name itself. SKILL.md tells the
    /// agent this exact convention (same "spell out the convention" approach already used for
    /// <c>skills/savings-calculator</c>'s script-path gotcha, docs/spec.md §4.3).
    /// </summary>
    private async Task<string> BuildSummaryJsonAsync(string resourceName, CancellationToken cancellationToken)
    {
        if (!TryParsePeriod(resourceName, out var period))
        {
            throw new McpException(
                $"Unrecognized resource '{resourceName}'. Expected 'summary-<year>-<month>', e.g. 'summary-2026-08'.");
        }

        // userId comes from this request's validated JWT claims (HttpUserContextAccessor), never from the
        // resource name or any other MCP-request-supplied value — the isolation boundary now lives here,
        var userId = currentUserAccessor.UserId
            ?? throw new McpException("No authenticated user for this request.");

        var summary = await MonthlySummaryRepository.GetMonthlySummaryAsync(db, userId, period, cancellationToken);

        logger.LogInformation(
            "MCP monthly-summary computed for user {UserId}, period {Period}: income={TotalIncome} expense={TotalExpense}",
            userId, period.ToString("yyyy-MM", CultureInfo.InvariantCulture), summary.TotalIncome, summary.TotalExpense);

        return JsonSerializer.Serialize(new
        {
            month = summary.PeriodMonth.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            totalIncome = summary.TotalIncome,
            totalExpense = summary.TotalExpense,
            net = summary.TotalIncome - summary.TotalExpense,
            byCategory = summary.ByCategory.Select(c => new
            {
                category = c.CategoryName,
                kind = c.Kind.ToString(),
                total = c.TotalAmount,
            }),
            budgetStatuses = summary.BudgetStatuses.Select(b => new
            {
                category = b.CategoryName,
                limit = b.LimitAmount,
                spent = b.SpentAmount,
                percentUsed = b.PercentUsed,
                status = b.IsOver ? "Over" : b.IsNear ? "Near" : "Ok",
            }),
        });
    }

    private static bool TryParsePeriod(string resourceName, out DateOnly period)
    {
        period = default;
        const string prefix = "summary-";
        if (!resourceName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = resourceName[prefix.Length..].Split('-');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || month is < 1 or > 12)
        {
            return false;
        }

        period = new DateOnly(year, month, 1);
        return true;
    }
}
