namespace FinanceApp.AI;

/// <summary>
/// Strongly typed config for <see cref="ChatClientFactory"/> (docs/spec.md §3.2). Deliberately a plain
/// POCO with no ASP.NET Core / Microsoft.Extensions.Options dependency, so it stays constructible (and
/// this factory testable) without a host. <see cref="Web.Program"/> is responsible for populating one
/// from configuration — see its comment for why that's an indexer read, not <c>config.Bind(options)</c>.
/// </summary>
public sealed class AiOptions
{
    public const string SectionName = "AI";

    /// <summary>One of "Azure", "OpenAI", "Ollama", "Gemini", "Anthropic" (case-insensitive).</summary>
    public string EngineType { get; set; } = "";

    /// <summary>Required for Azure and Ollama; ignored otherwise.</summary>
    public string? Endpoint { get; set; }

    public string ModelName { get; set; } = "";

    /// <summary>Required for Azure, OpenAI, Gemini, Anthropic; ignored for Ollama.</summary>
    public string? ApiKey { get; set; }

    public bool SupportsVision { get; set; }
}
