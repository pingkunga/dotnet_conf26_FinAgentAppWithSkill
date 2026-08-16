using System.Security.Claims;
using FinanceApp.Core.Abstractions;
using Microsoft.AspNetCore.Http;

namespace FinanceApp.McpServer;

/// <summary>
/// Per-request <see cref="ICurrentUserAccessor"/> for the HTTP-hosted MCP server (docs/spec.md §5, Step 1
/// of the HTTP-migration plan). Resolves userId from the current request's authenticated
/// <see cref="ClaimsPrincipal"/> — populated by the JwtBearer middleware validating the short-lived token
/// <c>FinanceApp.Web</c> mints per chat session (<c>McpAccessTokenIssuer</c>). Registered scoped, so a
/// fresh instance (reading a fresh <see cref="HttpContext"/>) resolves per HTTP request — replacing the old
/// stdio design's <c>FixedCurrentUserAccessor</c> bound once for a whole subprocess's lifetime, now that
/// one server process serves every user's requests instead of one dedicated process per user.
/// </summary>
/// <remarks>
/// Reads <see cref="ClaimTypes.NameIdentifier"/>, not a raw <c>"sub"</c> claim — confirmed via a spike that
/// <c>JwtSecurityTokenHandler</c>'s default inbound claim-type mapping remaps a minted <c>"sub"</c> claim to
/// this long XML-namespace URI before any handler ever sees it. This also matches the exact lookup
/// <c>FinanceApp.Web</c>'s own <c>AuthStateCurrentUserAccessor</c> already uses, just sourced from
/// <see cref="IHttpContextAccessor"/> instead of Blazor's <c>AuthenticationStateProvider</c>.
/// </remarks>
public sealed class HttpUserContextAccessor(IHttpContextAccessor httpContextAccessor) : ICurrentUserAccessor
{
    public Guid? UserId
    {
        get
        {
            var value = httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(value, out var userId) ? userId : null;
        }
    }
}
