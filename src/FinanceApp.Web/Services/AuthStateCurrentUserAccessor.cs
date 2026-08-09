using System.Security.Claims;
using FinanceApp.Core.Abstractions;
using Microsoft.AspNetCore.Components.Authorization;

namespace FinanceApp.Web.Services;

/// <summary>
/// <see cref="ICurrentUserAccessor"/> implementation for Blazor Server — resolves the authenticated
/// user's id from the current circuit's <see cref="AuthenticationStateProvider"/> (docs/spec.md §2a
/// point 3). Scoped: each Blazor circuit/HTTP request gets its own instance.
/// </summary>
public sealed class AuthStateCurrentUserAccessor(AuthenticationStateProvider authenticationStateProvider)
    : ICurrentUserAccessor
{
    public Guid? UserId
    {
        get
        {
            // AuthenticationStateProvider.GetAuthenticationStateAsync() is safe to call synchronously
            // here (.GetAwaiter().GetResult()) during an actual request/circuit — the state is already
            // resolved by the time DI constructs a scoped FinanceDbContext, and this interface is
            // intentionally synchronous so it can be captured in a DbContext constructor (docs/spec.md
            // §2a point 4). Outside a request/circuit (e.g. the startup migration scope in Program.cs,
            // which resolves FinanceDbContext before any HTTP request exists),
            // ServerAuthenticationStateProvider.GetAuthenticationStateAsync() throws
            // InvalidOperationException because SetAuthenticationState was never called — treat that,
            // like any other failure here, as "no authenticated user" (fail-closed: query filters then
            // return zero rows) rather than letting it crash DbContext construction.
            try
            {
                var principal = authenticationStateProvider.GetAuthenticationStateAsync()
                    .GetAwaiter().GetResult().User;

                var idClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier);
                return Guid.TryParse(idClaim, out var id) ? id : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }
}
