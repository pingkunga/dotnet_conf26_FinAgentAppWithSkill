using System.Diagnostics;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FinanceApp.AI;

/// <summary>
/// Config for <see cref="SkillCallLogging"/>. Plain POCO, same "no Options dependency" posture as
/// <see cref="AiOptions"/> — <c>FinanceApp.Web</c>'s <c>Program.cs</c> populates it from the
/// <c>SkillCallLogging</c> config section.
/// </summary>
public sealed class SkillCallLoggingOptions
{
    public const string SectionName = "SkillCallLogging";

    /// <summary>
    /// Whether to include the call's arguments and result in the log line. Results carry the user's own
    /// financial data (balances, transactions, goal amounts) — turn this off where logs leave the machine.
    /// </summary>
    public bool IncludePayloads { get; set; } = true;

    /// <summary>Arguments/result longer than this are truncated, with the dropped length noted.</summary>
    public int MaxPayloadChars { get; set; } = 2000;
}

/// <summary>
/// Logs the input and result of every skill call executed by the agent.
/// </summary>
public static class SkillCallLogging
{
    public static AIAgent WithSkillCallLogging(this AIAgent agent, ILogger logger, Guid userId, SkillCallLoggingOptions options) =>
        agent.AsBuilder()
            .Use((innerAgent, context, next, cancellationToken) => LogCallAsync(logger, userId, options, context, next, cancellationToken))
            .Build();

    private static async ValueTask<object?> LogCallAsync(
        ILogger logger,
        Guid userId,
        SkillCallLoggingOptions options,
        FunctionInvocationContext context,
        Func<FunctionInvocationContext, CancellationToken, ValueTask<object?>> next,
        CancellationToken cancellationToken)
    {
        var toolName = context.Function.Name;
        var skillName = GetArg(context, "skillName") ?? "-";
        var target = toolName == AgentSkillsProvider.ReadSkillResourceToolName ? GetArg(context, "resourceName")
            : toolName == AgentSkillsProvider.RunSkillScriptToolName ? GetArg(context, "scriptName")
            : null;
        var args = options.IncludePayloads ? Truncate(Serialize(context.Arguments), options.MaxPayloadChars) : "(omitted)";

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await next(context, cancellationToken);

            logger.LogInformation(
                "Skill call {Tool} {Skill}/{Target} user={UserId} in {ElapsedMs}ms args={Args} result={Result}",
                toolName, skillName, target ?? "-", userId, stopwatch.ElapsedMilliseconds, args,
                options.IncludePayloads ? Truncate(Serialize(result), options.MaxPayloadChars) : "(omitted)");

            return result;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Skill call {Tool} {Skill}/{Target} user={UserId} failed after {ElapsedMs}ms args={Args}",
                toolName, skillName, target ?? "-", userId, stopwatch.ElapsedMilliseconds, args);
            throw;
        }
    }

    private static string? GetArg(FunctionInvocationContext context, string key) =>
        context.Arguments.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static string Serialize(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return s;
            case JsonElement element:
                return element.GetRawText();
        }

        try
        {
            return JsonSerializer.Serialize(value);
        }
        catch (Exception)
        {
            // A result type System.Text.Json can't handle shouldn't turn a successful call into a failure.
            return value.ToString() ?? value.GetType().Name;
        }
    }

    internal static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : $"{value[..maxChars]}…(+{value.Length - maxChars} chars)";
}
