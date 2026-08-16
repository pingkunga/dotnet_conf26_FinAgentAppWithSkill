using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace FinanceApp.Web.Services;

/// <summary>
/// Mints the short-lived bearer token <see cref="McpServerLauncher"/> attaches to every request against
/// the now-HTTP-hosted <c>FinanceApp.McpServer</c> (docs/spec.md §5, Step 1 of the HTTP-migration plan).
/// This is Step 1's deliberately lightweight internal auth — a shared symmetric signing key configured
/// identically on both sides (<c>Mcp:SigningKey</c>), not OAuth 2.1 — chosen specifically because swapping
/// the *issuer* later (Step 2: OpenIddict) doesn't require touching McpServer's JwtBearer validation
/// middleware, only its `TokenValidationParameters` (see the deferred-concern memory note this plan grew
/// from). Registered singleton — stateless, just config + a signing call per invocation.
/// </summary>
/// <remarks>
/// The userId claim is minted as <see cref="ClaimTypes.NameIdentifier"/>, not a raw <c>"sub"</c> — a spike
/// found <c>JwtSecurityTokenHandler</c>'s default inbound claim-type mapping remaps <c>"sub"</c> to this
/// long XML-namespace URI before the server ever sees it, and this also matches the exact claim type
/// <c>AuthStateCurrentUserAccessor</c> already uses elsewhere in this app.
/// </remarks>
public sealed class McpAccessTokenIssuer(IConfiguration configuration)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);

    public string IssueToken(Guid userId)
    {
        var signingKey = configuration["Mcp:SigningKey"]
            ?? throw new InvalidOperationException("Missing Mcp:SigningKey configuration.");

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: "FinanceApp.Web",
            audience: "FinanceApp.McpServer",
            claims: [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            expires: DateTime.UtcNow.Add(TokenLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
