using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace FinanceApp.McpServer.Tests;

/// <summary>
/// Spawns the **real** <c>FinanceApp.McpServer</c> executable and talks to it over a real HTTP
/// <see cref="McpClient"/> — one .NET process to another, needing neither Docker nor an LLM (same bar as
/// <c>FileSkillTests</c>/<c>SubprocessScriptRunnerTests</c>). HTTP transport, Step 1 of the HTTP-migration
/// plan (docs/spec.md §5) replaced the old stdio subprocess-per-session design, so this file now exercises
/// the JwtBearer auth path too, not just resource content — only resources that don't touch Postgres
/// (<c>skill://index.json</c>, <c>skill://monthly-summary/SKILL.md</c>) are read; a live summary read needs
/// a real database connection, unavailable in this environment (same shape of ceiling as §4.3's python3/
/// §4.2's vision-model gaps) — covered instead by <see cref="MonthlySummaryResourceHandlersTests"/>
/// (including the concurrent multi-user isolation test) and <c>MonthlySummaryRepositoryTests</c>.
/// </summary>
public sealed class McpServerProcessTests : IAsyncLifetime
{
    // Deliberately does NOT match FinanceApp.McpServer's checked-in appsettings.json dev placeholder — the
    // test mints its own tokens against a signing key it controls via an env-var override, so this file
    // never depends on that file's content staying in sync.
    private const string SigningKey = "test-only-shared-secret-test-only-shared-secret";

    // ProjectReference copies FinanceApp.McpServer's own build output (including its .dll) into this test
    // project's output directory alongside FinanceApp.McpServer.Tests.dll — confirmed by inspecting the
    // actual bin/ output, not assumed — so no cross-project bin/<Config>/<TFM> path guessing is needed.
    private static readonly string McpServerDllPath = Path.Combine(AppContext.BaseDirectory, "FinanceApp.McpServer.dll");

    private System.Diagnostics.Process? _serverProcess;
    private string _baseUrl = "";

    public async Task InitializeAsync()
    {
        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";

        _serverProcess = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                // --urls (command-line config source) takes precedence over appsettings.json's dev-default
                // Urls unconditionally — ASPNETCORE_URLS env var was tried first and empirically did NOT
                // override appsettings.json's "Urls" key in this environment, leaving every test run
                // silently bound to the dev-default port 5299 instead of this run's freshly-picked one.
                Arguments = $"{McpServerDllPath} --urls {_baseUrl}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                EnvironmentVariables =
                {
                    ["Mcp__SigningKey"] = SigningKey,
                    // Never actually connected to for the resources exercised here — the DB is only opened
                    // lazily, the first time a summary resource is read.
                    ["ConnectionStrings__Finance"] = "Host=localhost;Database=unused;Username=unused;Password=unused",
                },
            },
        };
        _serverProcess.Start();

        try
        {
            await WaitForHealthyAsync(_baseUrl, TimeSpan.FromSeconds(15));
        }
        catch (Exception ex) when (_serverProcess is { HasExited: true })
        {
            var stderr = await _serverProcess.StandardError.ReadToEndAsync();
            throw new InvalidOperationException(
                $"FinanceApp.McpServer exited early (code {_serverProcess.ExitCode}). Stderr:\n{stderr}", ex);
        }
    }

    public async Task DisposeAsync()
    {
        if (_serverProcess is { HasExited: false })
        {
            _serverProcess.Kill(entireProcessTree: true);
            await _serverProcess.WaitForExitAsync();
        }
        _serverProcess?.Dispose();
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForHealthyAsync(string baseUrl, TimeSpan timeout)
    {
        using var http = new HttpClient();
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = await http.GetAsync($"{baseUrl}/health");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"FinanceApp.McpServer did not become healthy in time.", last);
    }

    private static string MintToken(Guid userId, string signingKey = SigningKey, bool expired = false)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "FinanceApp.Web",
            audience: "FinanceApp.McpServer",
            claims: [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            expires: expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private async Task<McpClient> ConnectAsync(string? bearerToken)
    {
        var headers = new Dictionary<string, string>();
        if (bearerToken is not null)
        {
            headers["Authorization"] = $"Bearer {bearerToken}";
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_baseUrl),
            Name = "finance-mcp-test",
            AdditionalHeaders = headers,
        });

        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task RealServer_WithValidToken_ReadsTheSkillIndexOverHttp()
    {
        await using var client = await ConnectAsync(MintToken(Guid.NewGuid()));

        var result = await client.ReadResourceAsync("skill://index.json");

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Contains("monthly-summary", text.Text);
    }

    [Fact]
    public async Task RealServer_WithValidToken_ReadsSkillMdOverHttp()
    {
        await using var client = await ConnectAsync(MintToken(Guid.NewGuid()));

        var result = await client.ReadResourceAsync("skill://monthly-summary/SKILL.md");

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Contains("name: monthly-summary", text.Text);
    }

    [Fact]
    public async Task RealServer_WithValidToken_ListsResourcesFromEverySkillMdHandler()
    {
        // Archive-type skills (emergency-fund, debt-payoff-strategies) have no separate listed SKILL.md —
        // their content lives entirely inside their one archive resource (see McpSkillRegistryTests for
        // the general aggregation logic, tested against fakes). Only the two skill-md skills add a listed
        // resource beyond the shared index itself.
        await using var client = await ConnectAsync(MintToken(Guid.NewGuid()));

        var result = await client.ListResourcesAsync();

        Assert.Contains(result, r => r.Uri == "skill://index.json");
        Assert.Contains(result, r => r.Uri == "skill://monthly-summary/SKILL.md");
        Assert.Contains(result, r => r.Uri == "skill://goals-progress/SKILL.md");
    }

    [Fact]
    public async Task RealServer_WithValidToken_ReadsGoalsProgressSkillMdOverHttp()
    {
        await using var client = await ConnectAsync(MintToken(Guid.NewGuid()));

        var result = await client.ReadResourceAsync("skill://goals-progress/SKILL.md");

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        Assert.Contains("name: goals-progress", text.Text);
    }

    [Fact]
    public async Task RealServer_WithValidToken_IndexIncludesBothArchiveTypeSkills()
    {
        await using var client = await ConnectAsync(MintToken(Guid.NewGuid()));

        var result = await client.ReadResourceAsync("skill://index.json");

        var text = Assert.IsType<TextResourceContents>(Assert.Single(result.Contents));
        using var doc = JsonDocument.Parse(text.Text);
        var skills = doc.RootElement.GetProperty("skills").EnumerateArray().ToList();

        var emergencyFund = Assert.Single(skills, s => s.GetProperty("name").GetString() == "emergency-fund");
        Assert.Equal("archive", emergencyFund.GetProperty("type").GetString());
        Assert.Equal("skill://emergency-fund/archive.zip", emergencyFund.GetProperty("url").GetString());

        var debtPayoff = Assert.Single(skills, s => s.GetProperty("name").GetString() == "debt-payoff-strategies");
        Assert.Equal("archive", debtPayoff.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RealServer_WithValidToken_ReadsTheEmergencyFundArchiveAsAValidZipOverHttp()
    {
        await using var client = await ConnectAsync(MintToken(Guid.NewGuid()));

        var result = await client.ReadResourceAsync("skill://emergency-fund/archive.zip");

        var blob = Assert.IsType<BlobResourceContents>(Assert.Single(result.Contents));
        Assert.Equal("application/zip", blob.MimeType);

        // Confirmed via a diagnostic spike (real HTTP + a raw curl comparison) that on this SDK version
        // (ModelContextProtocol.Core 0.4.0-preview.3) BlobResourceContents.Blob carries base64 *text* as
        // bytes on BOTH sides of the wire — nothing auto-decodes it, on write or on read. The server
        // (ArchiveSkillResourceHandler) pre-encodes for the same reason this test must decode here.
        var zipBytes = Convert.FromBase64String(Encoding.UTF8.GetString(blob.Blob.ToArray()));
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var skillMdEntry = archive.GetEntry("SKILL.md");
        Assert.NotNull(skillMdEntry);
        using var reader = new StreamReader(skillMdEntry.Open());
        Assert.Contains("name: emergency-fund", await reader.ReadToEndAsync());
    }

    /// <summary>
    /// Asserts specifically on a 401 <see cref="HttpRequestException.StatusCode"/> for all three rejection
    /// cases below — a bare "some exception was thrown" check would also pass for a connection-refused, a
    /// timeout, or an unrelated serialization bug, none of which prove the auth boundary itself is working.
    /// A future change that accidentally makes <c>MapMcp()</c> anonymous must fail these tests, not pass
    /// them for the wrong reason.
    /// </summary>
    private async Task AssertRejectedWithUnauthorizedAsync(string? bearerToken)
    {
        var ex = await Record.ExceptionAsync(async () =>
        {
            await using var client = await ConnectAsync(bearerToken);
            await client.ListResourcesAsync();
        });

        var httpEx = Assert.IsType<HttpRequestException>(ex);
        Assert.Equal(HttpStatusCode.Unauthorized, httpEx.StatusCode);
    }

    [Fact]
    public Task RealServer_WithNoToken_RejectsTheConnection() =>
        AssertRejectedWithUnauthorizedAsync(bearerToken: null);

    [Fact]
    public Task RealServer_WithInvalidSignature_RejectsTheConnection() =>
        AssertRejectedWithUnauthorizedAsync(MintToken(Guid.NewGuid(), signingKey: "wrong-signing-key-wrong-signing-key"));

    [Fact]
    public Task RealServer_WithExpiredToken_RejectsTheConnection() =>
        AssertRejectedWithUnauthorizedAsync(MintToken(Guid.NewGuid(), expired: true));
}
