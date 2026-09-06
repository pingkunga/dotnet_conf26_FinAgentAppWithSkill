using System.Text.Json;
using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Repositories;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace FinanceApp.McpServer;

/// <summary>
/// Serves the <c>goals-progress</c> skill (docs/spec.md §4.4) — the second <c>skill-md</c>-type skill
/// hosted by this server, added alongside <see cref="MonthlySummaryResourceHandlers"/> once
/// <see cref="McpSkillRegistry"/> made a second slot a matter of writing this class, not editing the
/// first one's index array/switch. Exists because no other skill source in this app can read
/// <see cref="Core.Entities.SavingsGoal"/> rows at all (see <see cref="Core.Repositories.SavingsGoalRepository"/>'s
/// remarks). Unlike <c>monthly-summary</c>'s <c>summary-&lt;year&gt;-&lt;month&gt;</c> convention, there's
/// no time dimension for "how are my goals doing right now" — the single live resource is always
/// <c>current</c>.
/// </summary>
/// <remarks>
/// Same DI/isolation shape as <see cref="MonthlySummaryResourceHandlers"/>: registered scoped (see
/// <c>Program.cs</c>), userId resolved per-request from <paramref name="currentUserAccessor"/>'s validated
/// JWT claims — never from a resource name or any other MCP-request-supplied value.
/// </remarks>
public sealed class GoalsProgressResourceHandlers(
    FinanceDbContext db,
    ICurrentUserAccessor currentUserAccessor,
    ILogger<GoalsProgressResourceHandlers> logger) : IMcpSkillResourceHandler
{
    private const string SkillName = "goals-progress";
    private const string SkillMdUri = "skill://goals-progress/SKILL.md";
    private const string CurrentUri = "skill://goals-progress/current";

    public object IndexEntry => new
    {
        name = SkillName,
        type = "skill-md",
        description = "Reports the live status of every one of the user's savings/investment goals — percent complete and months remaining at the current contribution pace.",
        url = SkillMdUri,
        digest = "v1",
    };

    public IEnumerable<Resource> ListableResources =>
        [new Resource { Uri = SkillMdUri, Name = SkillName, MimeType = "text/markdown" }];

    public bool CanHandle(string uri) =>
        uri is SkillMdUri or CurrentUri;

    public async ValueTask<ReadResourceResult> ReadResourceAsync(string uri, CancellationToken cancellationToken)
    {
        logger.LogInformation("MCP read_skill_resource: {Uri}", uri);

        var text = uri switch
        {
            SkillMdUri => await ReadSkillMdAsync(cancellationToken),
            CurrentUri => await BuildGoalsJsonAsync(cancellationToken),
            _ => throw new McpException($"Unknown resource: {uri}"),
        };

        return new ReadResourceResult
        {
            Contents = [new TextResourceContents { Uri = uri, MimeType = "text/plain", Text = text }],
        };
    }

    private static Task<string> ReadSkillMdAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "skills", "goals-progress", "SKILL.md");
        return File.ReadAllTextAsync(path, cancellationToken);
    }

    private async Task<string> BuildGoalsJsonAsync(CancellationToken cancellationToken)
    {
        // userId comes from this request's validated JWT claims (HttpUserContextAccessor), never from the
        // resource name or any other MCP-request-supplied value — same isolation boundary as
        // MonthlySummaryResourceHandlers.
        var userId = currentUserAccessor.UserId
            ?? throw new McpException("No authenticated user for this request.");

        var goals = await SavingsGoalRepository.GetGoalsProgressAsync(db, userId, cancellationToken);

        logger.LogInformation("MCP goals-progress computed for user {UserId}: {Count} goal(s)", userId, goals.Count);

        return JsonSerializer.Serialize(goals.Select(g => new
        {
            name = g.Name,
            targetAmount = g.TargetAmount,
            currentAmount = g.CurrentAmount,
            percentComplete = g.PercentComplete,
            targetDate = g.TargetDate,
            monthlyContribution = g.MonthlyContribution,
            projectedMonthsRemaining = g.ProjectedMonthsRemaining,
        }));
    }
}
