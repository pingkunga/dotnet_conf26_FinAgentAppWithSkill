using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Xunit.Abstractions;

namespace FinanceApp.McpServer.Tests;

/// <summary>
/// Not a correctness test — a manual dev tool. Mints a bearer token that validates against a
/// <c>FinanceApp.McpServer</c> instance running with its checked-in dev appsettings.json
/// (<c>Mcp:SigningKey</c> = "dev-only-shared-secret-CHANGE-ME-32-bytes-min"), for pasting into MCP
/// Inspector (https://github.com/modelcontextprotocol/inspector) as the
/// <c>Authorization: Bearer &lt;token&gt;</c> custom header — there's no endpoint in the app itself to
/// request one manually (<see cref="global::FinanceApp.Web.Services.McpAccessTokenIssuer"/> only mints
/// tokens for real chat sessions, and isn't referenced from this test project, hence the fully-qualified
/// doc-only name above rather than a real dependency).
/// </summary>
/// <remarks>
/// Run:
/// <code>
/// dotnet run --project src/FinanceApp.McpServer
/// dotnet test tests/FinanceApp.McpServer.Tests --filter ManualDevTokenTool --logger "console;verbosity=detailed"
/// </code>
/// then copy the "Bearer ..." line from the test's "Standard Output" section. Kept as a real, always-green
/// xunit <see cref="Fact"/> (no forced-failure trick to surface output) — the token is read from
/// <see cref="ITestOutputHelper"/>, matching this project's "real passing tests only" bar.
/// </remarks>
public sealed class ManualDevTokenTool(ITestOutputHelper output)
{
    // Matches src/FinanceApp.McpServer/appsettings.json's checked-in dev placeholder exactly — this tool is
    // only useful against a server running with that file unmodified. If you've overridden Mcp:SigningKey
    // (e.g. via user-secrets/env var, as that file's own comment recommends for anything beyond local dev),
    // change this constant to match before running.
    private const string DevSigningKey = "dev-only-shared-secret-CHANGE-ME-32-bytes-min";

    [Fact]
    public void PrintDevMcpBearerToken()
    {
        var userId = Guid.NewGuid();
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(DevSigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "FinanceApp.Web",
            audience: "FinanceApp.McpServer",
            claims: [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            expires: DateTime.UtcNow.AddMinutes(60), // generous vs. the app's real 10-minute tokens, for a manual Inspector session
            signingCredentials: credentials);
        var jwt = new JwtSecurityTokenHandler().WriteToken(token);

        output.WriteLine($"userId (random, for isolation testing — mint twice to compare two \"users\"): {userId}");
        output.WriteLine("Authorization header value:");
        output.WriteLine($"Bearer {jwt}");

        Assert.NotNull(jwt); // keeps this a real, green test — the token is read from the output above, not from an assertion-failure message
    }
}
