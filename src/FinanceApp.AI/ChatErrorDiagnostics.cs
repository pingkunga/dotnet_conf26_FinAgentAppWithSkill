using System.ClientModel;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace FinanceApp.AI;

public static class ChatErrorDiagnostics
{
    public static void LogFull(ILogger logger, Exception ex, string context)
    {
        if (ex is ClientResultException cre)
        {
            var response = cre.GetRawResponse();
            if (response is not null)
            {
                string? body = null;
                try
                {
                    body = response.Content?.ToString();
                }
                catch (InvalidOperationException)
                {
                    // Response wasn't buffered — nothing to show, not fatal to the rest of the log.
                }

                var headers = string.Join(
                    "; ", response.Headers.Select(h => $"{h.Key}={h.Value}"));

                logger.LogError(
                    ex,
                    "{Context} failed: HTTP {Status} {ReasonPhrase}. Headers: {Headers}. Body: {Body}",
                    context, response.Status, response.ReasonPhrase, headers, body);
                return;
            }
        }

        logger.LogError(ex, "{Context} failed: {Message}", context, ex.Message);
    }
}
