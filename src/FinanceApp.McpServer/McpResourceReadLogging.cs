using System.Diagnostics;
using FinanceApp.Core.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FinanceApp.McpServer;

/// <summary>
/// Logs each MCP resource read request, including the URI, the user making the request, the elapsed time, and a preview of the returned contents.
/// </summary>
public static class McpResourceReadLogging
{
    public static McpRequestFilter<ReadResourceRequestParams, ReadResourceResult> Create(
        bool includePayloads, int maxPayloadChars) =>
        next => async (context, cancellationToken) =>
        {
            // Filters are built once at startup, before any service provider exists — so the logger is
            // resolved from the request's own scope, same as the ICurrentUserAccessor below.
            var logger = context.Services!.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(McpResourceReadLogging));
            var uri = context.Params?.Uri ?? "-";
            var userId = context.Services?.GetService<ICurrentUserAccessor>()?.UserId;

            var stopwatch = Stopwatch.StartNew();
            try
            {
                var result = await next(context, cancellationToken);

                logger.LogInformation(
                    "MCP resources/read {Uri} user={UserId} in {ElapsedMs}ms contents={ContentCount} result={Result}",
                    uri, userId, stopwatch.ElapsedMilliseconds, result.Contents.Count,
                    includePayloads ? Describe(result, maxPayloadChars) : "(omitted)");

                return result;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "MCP resources/read {Uri} user={UserId} failed after {ElapsedMs}ms",
                    uri, userId, stopwatch.ElapsedMilliseconds);
                throw;
            }
        };

    /// <summary>
    /// Text contents are previewed (truncated); blob contents (the zipped <c>archive</c>-type skills) are
    /// reported by size only — dumping a base64 zip into the log is noise, not information.
    /// </summary>
    private static string Describe(ReadResourceResult result, int maxPayloadChars) =>
        string.Join(" | ", result.Contents.Select(content => content switch
        {
            TextResourceContents text => Truncate(text.Text, maxPayloadChars),
            BlobResourceContents blob => $"(blob {blob.MimeType ?? "?"}, {blob.Blob.Length} bytes)",
            _ => $"({content.GetType().Name})",
        }));

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : $"{value[..maxChars]}…(+{value.Length - maxChars} chars)";
}
