using FinanceApp.AI;
using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.Core.Entities;
using FinanceApp.Web.Components;
using FinanceApp.Web.Components.Account;
using FinanceApp.Web.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddMudServices();

// --- EF Core / current-user (docs/spec.md §2a points 3, 5) ---
builder.Services.AddScoped<ICurrentUserAccessor, AuthStateCurrentUserAccessor>();

var connectionString = builder.Configuration.GetConnectionString("Finance");
// AddDbContext (scoped) — required by AddEntityFrameworkStores<FinanceDbContext>() below.
//
// docs/spec.md §2a point 5 also calls for AddDbContextFactory<FinanceDbContext> for skill scripts'
// per-invocation scopes (§3.4). Deliberately NOT registered yet: calling both AddDbContext and
// AddDbContextFactory for the same context type conflicts once the context has an extra
// scoped-lifetime constructor dependency (ICurrentUserAccessor here) — `dotnet ef migrations` failed
// DI validation with "Cannot consume scoped service 'DbContextOptions<FinanceDbContext>' from
// singleton 'IDbContextFactory<FinanceDbContext>'" when both were registered together. When skills are
// implemented, follow the documented Blazor+EF Core pattern instead (learn.microsoft.com/aspnet/core/blazor/blazor-ef-core):
// register only AddDbContextFactory<FinanceDbContext>, inject IDbContextFactory<FinanceDbContext>
// directly into skill code, and call CreateDbContext()/CreateDbContextAsync() per script invocation —
// do not also add a derived scoped FinanceDbContext registration alongside AddDbContext.
builder.Services.AddDbContext<FinanceDbContext>(options => options.UseNpgsql(connectionString));

// --- ASP.NET Core Identity (docs/spec.md §2a) ---
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        // No real email sender is configured (IdentityNoOpEmailSender below) — requiring confirmation
        // would lock every newly registered user out. Deliberate deviation from the `-au Individual`
        // template default (docs/spec.md §2a).
        options.SignIn.RequireConfirmedAccount = false;
        options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<FinanceDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddSingleton<IEmailSender<ApplicationUser>, IdentityNoOpEmailSender>();

// --- ChatClientFactory (docs/spec.md §3.1/§3.2) ---
// Read via GetSection(...)[...] indexer, not config.Bind(aiOptions)/AddOptions<AiOptions>().BindConfiguration:
// the "AI" section's keys follow gitea-aihook's ALL_CAPS_WITH_UNDERSCORES convention (ENGINE_TYPE,
// MODEL_NAME, ...) so that AI__ENGINE_TYPE-style env vars (.env.example) work Docker-side without extra
// code. ConfigurationBinder's property matching is case-insensitive but NOT underscore-insensitive, so
// .Bind() silently leaves EngineType/ModelName/ApiKey/SupportsVision at their defaults — confirmed via
// FinanceApp.AI.Tests' ConfigurationBinder_DoesNotMatchUnderscoredKeysToPascalCaseProperties (only
// ENDPOINT, which has no underscore, binds correctly by accident). IChatClient is a singleton — thread-safe
// and relatively expensive to construct, no per-request state (docs/spec.md §3.2).
// Registered as its own singleton (not just built-and-discarded inside the IChatClient factory below) so
// AiOptions.SupportsVision is resolvable elsewhere — ReceiptUpload.razor gates its "Extract with AI" button
// on it, and ChatSessionService.RegisterReceiptSkillAsync passes it to ReceiptOcrSkillFactory.Create
// (docs/spec.md §4.2).
builder.Services.AddSingleton(sp =>
{
    var aiSection = builder.Configuration.GetSection(AiOptions.SectionName);
    return new AiOptions
    {
        EngineType = aiSection["ENGINE_TYPE"] ?? "",
        Endpoint = aiSection["ENDPOINT"],
        ModelName = aiSection["MODEL_NAME"] ?? "",
        ApiKey = aiSection["API_KEY"],
        SupportsVision = bool.TryParse(aiSection["SUPPORTS_VISION"], out var supportsVision) && supportsVision,
    };
});
builder.Services.AddSingleton<IChatClient>(sp => ChatClientFactory.CreateChatClient(sp.GetRequiredService<AiOptions>()));
builder.Services.AddSingleton<IAgentFactory, AgentFactory>();

// ChatSessionService is scoped, not singleton — one AIAgent/AgentSession per Blazor circuit, because the
// skills it wires (BudgetSkill) close over DB-backed services (docs/spec.md §3.4).
builder.Services.AddScoped<ChatSessionService>();

var app = builder.Build();

// docs/spec.md §6.4 — Web owns all migrations; McpServer is read-only against the same DB.
// Gated by AUTO_MIGRATE so it can be disabled later without a code change (e.g. multi-instance deploy).
if (app.Environment.IsDevelopment() || Environment.GetEnvironmentVariable("AUTO_MIGRATE") != "false")
{
    using var migrationScope = app.Services.CreateScope();
    await migrationScope.ServiceProvider.GetRequiredService<FinanceDbContext>().Database.MigrateAsync();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Login/Register/Manage/Logout endpoints for the Components/Account/** pages (docs/spec.md §2a point 6).
app.MapAdditionalIdentityEndpoints();

app.Run();
