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
builder.Services.AddScoped<MonthlySummaryResourceHandlers>();

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
    .WithListResourcesHandler(MonthlySummaryResourceHandlers.ListResourcesAsync)
    .WithReadResourceHandler((context, cancellationToken) =>
        context.Services!.GetRequiredService<MonthlySummaryResourceHandlers>()
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
