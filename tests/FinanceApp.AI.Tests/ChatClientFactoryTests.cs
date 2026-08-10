using FinanceApp.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace FinanceApp.AI.Tests;

/// <summary>
/// Docker-free, network-free, key-free — mirrors FinanceApp.Skills.Tests' bar (docs/spec.md §8): these
/// prove construction succeeds/fails correctly, not that any provider is actually reachable. No test here
/// needs a real API key or a running Ollama server.
/// </summary>
public class ChatClientFactoryTests
{
    [Theory]
    [InlineData(ChatClientFactory.EngineAzure)]
    [InlineData(ChatClientFactory.EngineOpenAi)]
    [InlineData(ChatClientFactory.EngineOllama)]
    [InlineData(ChatClientFactory.EngineGemini)]
    [InlineData(ChatClientFactory.EngineAnthropic)]
    public void CreateChatClient_WithValidShapeAndBogusCredentials_ReturnsNonNullWithoutThrowing(string engineType)
    {
        var options = new AiOptions
        {
            EngineType = engineType,
            Endpoint = "http://localhost:11434",
            ModelName = "bogus-model",
            ApiKey = "bogus-key",
        };

        // Construction must not touch the network for any provider — no real key/endpoint is reachable here.
        IChatClient client = ChatClientFactory.CreateChatClient(options);

        Assert.NotNull(client);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Bedrock")]
    [InlineData("gpt")]
    public void CreateChatClient_UnsupportedEngineType_Throws(string engineType)
    {
        var options = new AiOptions { EngineType = engineType, ModelName = "m", ApiKey = "k", Endpoint = "http://x" };

        Assert.ThrowsAny<Exception>(() => ChatClientFactory.CreateChatClient(options));
    }

    [Fact]
    public void CreateChatClient_UnknownEngineType_ThrowsNotSupportedException()
    {
        var options = new AiOptions { EngineType = "Bedrock", ModelName = "m", ApiKey = "k", Endpoint = "http://x" };

        Assert.Throws<NotSupportedException>(() => ChatClientFactory.CreateChatClient(options));
    }

    [Theory]
    [InlineData(ChatClientFactory.EngineAzure)] // needs Endpoint + ModelName + ApiKey
    [InlineData(ChatClientFactory.EngineOllama)] // needs Endpoint + ModelName
    [InlineData(ChatClientFactory.EngineOpenAi)] // needs ApiKey
    [InlineData(ChatClientFactory.EngineGemini)] // needs ModelName + ApiKey
    [InlineData(ChatClientFactory.EngineAnthropic)] // needs ApiKey
    public void CreateChatClient_MissingRequiredField_ThrowsArgumentException(string engineType)
    {
        // Every field left null/empty — every engine type is missing at least one required field.
        var options = new AiOptions { EngineType = engineType };

        Assert.Throws<ArgumentException>(() => ChatClientFactory.CreateChatClient(options));
    }

    /// <summary>
    /// Discriminates docs/spec.md §3.2's claim that the gitea-aihook env-var convention
    /// (<c>AI__ENGINE_TYPE</c> etc.) binds straight into <see cref="AiOptions"/> "with no extra binding
    /// code needed". <c>AI__X</c> env vars map to config key <c>AI:X</c> (double-underscore → colon) —
    /// this test emulates that mapping directly via AddInMemoryCollection (no real process env vars, so
    /// it's safe under parallel test runs) and binds with the plain <see cref="ConfigurationBinder.Bind"/>
    /// the same way a naive <c>services.Configure&lt;AiOptions&gt;(config.GetSection("AI"))</c> would.
    /// </summary>
    [Fact]
    public void ConfigurationBinder_DoesNotMatchUnderscoredKeysToPascalCaseProperties()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI:ENGINE_TYPE"] = "Ollama",
                ["AI:MODEL_NAME"] = "llama3",
                ["AI:ENDPOINT"] = "http://localhost:11434",
            })
            .Build();

        var bound = new AiOptions();
        config.GetSection(AiOptions.SectionName).Bind(bound);

        // If EngineType/ModelName ever start binding, ConfigurationBinder became underscore-insensitive
        // and §3.2's original claim (bind directly, no indexer code needed) would be true again — update
        // the spec and Program.cs's config-reading code together if so. ENGINE_TYPE/MODEL_NAME (keys with
        // an underscore) fail to match EngineType/ModelName (no underscore) — confirmed empirically here.
        Assert.Equal("", bound.EngineType);
        Assert.Equal("", bound.ModelName);
        // ENDPOINT (no underscore) *does* bind to Endpoint — case-insensitive match is fine on its own;
        // it's specifically the underscore that breaks ENGINE_TYPE/MODEL_NAME/API_KEY/SUPPORTS_VISION.
        Assert.Equal("http://localhost:11434", bound.Endpoint);

        // The indexer read Program.cs actually uses (docs/spec.md §3.2) works correctly instead:
        var section = config.GetSection(AiOptions.SectionName);
        Assert.Equal("Ollama", section["ENGINE_TYPE"]);
        Assert.Equal("llama3", section["MODEL_NAME"]);
        Assert.Equal("http://localhost:11434", section["ENDPOINT"]);
    }
}
