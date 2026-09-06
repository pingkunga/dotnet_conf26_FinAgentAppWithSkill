using System.Text;
using FinanceApp.Core;
using FinanceApp.Core.Abstractions;
using FinanceApp.McpServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Same connection string key as FinanceApp.Web
var connectionString = builder.Configuration.GetConnectionString("Finance")
    ?? throw new InvalidOperationException("Missing ConnectionStrings:Finance configuration.");

// Test-only seam
var testInMemoryDatabaseName = builder.Configuration["Testing:InMemoryDatabaseName"];

// Shared secret FinanceApp.Web signs its per-session access tokens with (McpAccessTokenIssuer) —
// Now lightweight internal auth,  forward-compatible with OAuth 2.1/OpenIddict
// swaps this validation for an Authority-based one; the JwtBearer middleware itself doesn't change).
var signingKey = builder.Configuration["Mcp:SigningKey"]
    ?? throw new InvalidOperationException("Missing Mcp:SigningKey configuration.");

builder.Services.AddHttpContextAccessor();

// request's validated JWT claims (HttpUserContextAccessor), never from an MCP request
builder.Services.AddScoped<ICurrentUserAccessor, HttpUserContextAccessor>();
if (testInMemoryDatabaseName is not null)
{
    builder.Services.AddDbContext<FinanceDbContext>(options => options.UseInMemoryDatabase(testInMemoryDatabaseName));
}
else
{
    builder.Services.AddDbContext<FinanceDbContext>(options => options.UseNpgsql(connectionString));
}
// Each IMcpSkillResourceHandler contributes one skill to the shared skill://index.json via
// McpSkillRegistry (docs/spec.md §4.4) — adding a skill is registering one more line here, not editing
// Program.cs's AddMcpServer() wiring below or any other handler's code.
builder.Services.AddScoped<IMcpSkillResourceHandler, MonthlySummaryResourceHandlers>();
builder.Services.AddScoped<IMcpSkillResourceHandler, GoalsProgressResourceHandlers>();

// Archive-type skills (ArchiveSkillResourceHandler, docs/spec.md §4.4) — guidance-only, no scripts (see
// that class's remarks for why). Each needs different constructor args, so registered via factory rather
// than resolved by concrete type.
var skillsRoot = Path.Combine(AppContext.BaseDirectory, "skills");
builder.Services.AddScoped<IMcpSkillResourceHandler>(sp => new ArchiveSkillResourceHandler(
    "emergency-fund",
    Path.Combine(skillsRoot, "emergency-fund"),
    "How to size and where to keep an emergency fund — general education, not personalized advice.",
    sp.GetRequiredService<ILogger<ArchiveSkillResourceHandler>>()));
builder.Services.AddScoped<IMcpSkillResourceHandler>(sp => new ArchiveSkillResourceHandler(
    "debt-payoff-strategies",
    Path.Combine(skillsRoot, "debt-payoff-strategies"),
    "Qualitative guidance on which debt to pay off first (snowball vs avalanche) — pairs with " +
    "savings-calculator's exact project-debt-payoff.py numbers.",
    sp.GetRequiredService<ILogger<ArchiveSkillResourceHandler>>()));

builder.Services.AddScoped<McpSkillRegistry>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "FinanceApp.Web",
            ValidateAudience = true,
            ValidAudience = "FinanceApp.McpServer",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithListResourcesHandler((context, cancellationToken) =>
        context.Services!.GetRequiredService<McpSkillRegistry>()
            .ListResourcesAsync(context, cancellationToken))
    .WithReadResourceHandler((context, cancellationToken) =>
        context.Services!.GetRequiredService<McpSkillRegistry>()
            .ReadResourceAsync(context, cancellationToken));

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

app.MapMcp().RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();

app.Run();

// Exposes the top-level Program class for WebApplicationFactory<Program> in
// tests/FinanceApp.McpServer.Tests 
public partial class Program;
