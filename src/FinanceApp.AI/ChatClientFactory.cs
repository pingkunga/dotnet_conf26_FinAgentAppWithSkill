using System.ClientModel;
using Anthropic.SDK;
using GenerativeAI.Microsoft;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;

namespace FinanceApp.AI;

/// <summary>
/// Multi-provider <see cref="IChatClient"/> factory, ported from
/// <c>github.com/pingkunga/gitea-aihook</c>'s <c>Services/ChatClientFactory.cs</c> (docs/spec.md §3.1).
/// Same engine-type switch, same "unknown type throws" behavior. Construction only — no network calls
/// happen here for any provider (verified: OllamaApiClient, GenerativeAIChatClient, AnthropicClient's
/// .Messages, and OpenAI.Chat.ChatClient all defer the first HTTP call to the first chat request).
/// </summary>
public static class ChatClientFactory
{
    public const string EngineAzure = "Azure";
    public const string EngineOpenAi = "OpenAI";
    public const string EngineOllama = "Ollama";
    public const string EngineGemini = "Gemini";
    public const string EngineAnthropic = "Anthropic";

    public static IChatClient CreateChatClient(AiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrEmpty(options.EngineType))
        {
            throw new ArgumentException("AI engine type is not configured.", nameof(options));
        }

        return options.EngineType switch
        {
            var t when EngineAzure.Equals(t, StringComparison.OrdinalIgnoreCase) => CreateAzure(options),
            var t when EngineOpenAi.Equals(t, StringComparison.OrdinalIgnoreCase) => CreateOpenAi(options),
            var t when EngineOllama.Equals(t, StringComparison.OrdinalIgnoreCase) => CreateOllama(options),
            var t when EngineGemini.Equals(t, StringComparison.OrdinalIgnoreCase) => CreateGemini(options),
            var t when EngineAnthropic.Equals(t, StringComparison.OrdinalIgnoreCase) => CreateAnthropic(options),
            _ => throw new NotSupportedException($"The AI engine type '{options.EngineType}' is not supported."),
        };
    }

    private static IChatClient CreateAzure(AiOptions options)
    {
        RequireEndpoint(options);
        RequireModelName(options);
        RequireApiKey(options);

        // Points the plain OpenAI SDK at an Azure-compatible endpoint via OpenAIClientOptions.Endpoint,
        // same as gitea-aihook — deliberately not Azure.AI.OpenAI's AzureOpenAIClient (not needed once
        // OpenAI.Chat.ChatClient can be redirected this way).
        ChatClient client = new(
            credential: new ApiKeyCredential(options.ApiKey!),
            model: options.ModelName,
            options: new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint!) });

        return client.AsIChatClient();
    }

    private static IChatClient CreateOpenAi(AiOptions options)
    {
        RequireModelName(options);
        RequireApiKey(options);

        // Endpoint is optional here (unlike Azure, where it's required) — omitted, this hits the real
        // OpenAI API as before. Set it to point at any OpenAI-API-compatible local/self-hosted server
        // instead (LM Studio, vLLM, llama.cpp's server, ...); those don't check the API key server-side,
        // so ApiKey can be any non-empty placeholder (LM Studio's own docs suggest "lm-studio").
        // Found missing (and fixed) after a real 401 against LM Studio, 2026-08-12 — this branch
        // previously ignored AiOptions.Endpoint entirely and always hit api.openai.com regardless.
        var clientOptions = string.IsNullOrEmpty(options.Endpoint)
            ? null
            : new OpenAIClientOptions { Endpoint = new Uri(options.Endpoint) };

        return new OpenAIClient(new ApiKeyCredential(options.ApiKey!), clientOptions)
            .GetChatClient(options.ModelName).AsIChatClient();
    }

    private static IChatClient CreateOllama(AiOptions options)
    {
        RequireEndpoint(options);
        RequireModelName(options);

        var httpClient = new HttpClient
        {
            // Local models can be slow to first-token on modest hardware — ported as-is from gitea-aihook.
            Timeout = TimeSpan.FromMinutes(40),
            BaseAddress = new Uri(options.Endpoint!),
        };
        return new OllamaApiClient(httpClient) { SelectedModel = options.ModelName };
    }

    private static IChatClient CreateGemini(AiOptions options)
    {
        RequireModelName(options);
        RequireApiKey(options);

        return new GenerativeAIChatClient(options.ApiKey!, options.ModelName);
    }

    private static IChatClient CreateAnthropic(AiOptions options)
    {
        RequireModelName(options);
        RequireApiKey(options);

        // Finishes what gitea-aihook left commented out (docs/spec.md §3.1, verify-item 2). The
        // Microsoft-authored `Microsoft.Agents.AI.Anthropic` package (tried first, per the original plan)
        // turned out to be agent-level only — its public surface is `AnthropicClient.AsAIAgent(...)`,
        // with no `IChatClient` exposed. `Anthropic.SDK` (tghamm, community — the spec's documented
        // fallback) does expose one directly via `AnthropicClient.Messages`, so that's what's used here.
        // Model isn't set at construction (unlike every other provider) — Anthropic.SDK takes it per-call
        // via ChatOptions.ModelId, so a default is baked in with ConfigureOptions instead.
        IChatClient client = new AnthropicClient(options.ApiKey!).Messages;
        return new ChatClientBuilder(client)
            .ConfigureOptions(o => o.ModelId ??= options.ModelName)
            .Build();
    }

    private static void RequireEndpoint(AiOptions options)
    {
        if (string.IsNullOrEmpty(options.Endpoint))
        {
            throw new ArgumentException($"AI endpoint is required for engine type '{options.EngineType}'.", nameof(options));
        }
    }

    private static void RequireModelName(AiOptions options)
    {
        if (string.IsNullOrEmpty(options.ModelName))
        {
            throw new ArgumentException($"AI model name is required for engine type '{options.EngineType}'.", nameof(options));
        }
    }

    private static void RequireApiKey(AiOptions options)
    {
        if (string.IsNullOrEmpty(options.ApiKey))
        {
            throw new ArgumentException($"AI API key is required for engine type '{options.EngineType}'.", nameof(options));
        }
    }
}
