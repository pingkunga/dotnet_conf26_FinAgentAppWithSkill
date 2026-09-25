namespace FinanceApp.Web.Services;

public static class McpSkillsExtractionDirectory
{
    public static string BaseDirectory { get; } = Path.Combine(Path.GetTempPath(), "financeapp-mcp-skills");

    /// <summary>Creates and returns a fresh, uniquely-named directory for one chat session.</summary>
    public static string CreateForSession()
    {
        var path = Path.Combine(BaseDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Deletes a session's directory. Never throws — a locked file must not break circuit teardown; whatever
    /// is left behind is picked up by the next <see cref="CleanupStale"/>.
    /// </summary>
    public static void TryDelete(string? path, ILogger logger)
    {
        if (path is null || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete MCP skills extraction directory {Path}", path);
        }
    }

    /// <summary>
    /// Removes every session directory left over from a previous run (crash, kill, circuits that never
    /// disposed). Only safe at startup, before any chat session exists. <paramref name="baseDirectory"/> is a
    /// test seam — tests must never sweep the real <see cref="BaseDirectory"/> a running dev app is using.
    /// </summary>
    public static void CleanupStale(ILogger logger, string? baseDirectory = null)
    {
        baseDirectory ??= BaseDirectory;
        if (!Directory.Exists(baseDirectory))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(baseDirectory))
        {
            TryDelete(directory, logger);
        }
    }
}
