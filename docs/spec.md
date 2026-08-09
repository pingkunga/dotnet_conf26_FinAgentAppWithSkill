# MyFinanceWithAgentSkill — Technical Specification

## Context

Personal Finance web app whose main purpose is to **demonstrate Microsoft Agent Framework's Skills
feature** (https://learn.microsoft.com/en-us/agent-framework/agents/skills?pivots=programming-language-csharp)
end-to-end — deliberately exercising all four skill source types (class-based, code-defined/inline,
file-based, MCP-based), one per real finance feature, rather than picking whichever is easiest.

Multi-LLM-provider support is modeled directly on the reference project
[`github.com/pingkunga/gitea-aihook`](https://github.com/pingkunga/gitea-aihook), specifically its
`Services/ChatClientFactory.cs` — a provider-switching factory built on `Microsoft.Extensions.AI`'s
`IChatClient` abstraction, selected at runtime by config rather than by picking one vendor SDK.

### Decisions
- **Runtime**: .NET 10
- **UI**: Blazor Server (Interactive Server render mode), single ASP.NET Core host — no separate SPA/Node stack
- **Database**: PostgreSQL via EF Core + Npgsql
- **Auth**: ASP.NET Core Identity, full register/login (see §2a) — not single-user/no-auth
- **Deployment**: Docker Compose (app container + postgres container)
- **LLM providers**: Azure OpenAI / OpenAI / Ollama / Gemini / Anthropic — selectable via config, ported from
  `ChatClientFactory` (finishing the Anthropic branch gitea-aihook left commented out)
- **4 features, each on a different skill source type (firm requirement)**:
  1. Transactions & budgeting → **Class-based skill** (`AgentClassSkill<T>`)
  2. Receipt OCR → **Code-defined inline skill** (`AgentInlineSkill`, built per-request to close over DI-scoped services)
  3. Savings/investment goals → **File-based skill** (`SKILL.md`, editable without recompiling)
  4. Monthly summary & advice → **MCP-based skill** (local MCP server in the same solution, stdio transport)

Package versions below were confirmed live against nuget.org on 2026-08-07
(`Microsoft.Agents.AI` 1.17.0 stable — this is where `AgentSkillsProvider`/`AgentSkillsProviderBuilder`
actually ship; `Microsoft.Agents.AI.Mcp` 1.17.0-alpha; `Microsoft.Agents.AI.Anthropic` 1.17.0-preview).
A handful of exact API shapes (listed in "Verify at implementation time") are for preview/alpha packages
whose public surface wasn't inspected line-by-line — these are isolated to specific files so uncertainty
can't leak into the rest of the design.

---

## 1. Solution & Project Layout

```
MyFinanceWithAgentSkill/
├── MyFinanceWithAgentSkill.sln
├── Directory.Build.props              # net10.0, Nullable, ImplicitUsings, InvariantGlobalization
├── Directory.Packages.props           # central package version management
├── .editorconfig  .gitignore  .env.example
├── docker-compose.yml  docker-compose.override.yml
├── skills/                            # file-based skill content (ships inside Web's publish output)
│   └── savings-goals/
│       ├── SKILL.md
│       └── references/{compound-interest.md, fifty-thirty-twenty.md}
├── src/
│   ├── FinanceApp.Core/               # domain entities + EF Core DbContext + repositories (shared by Web & McpServer)
│   ├── FinanceApp.AI/                 # ChatClientFactory + AgentFactory (isolated LLM-provider risk boundary)
│   ├── FinanceApp.Skills/             # BudgetSkill (class-based) + ReceiptOcrSkillFactory (inline) — testable, no ASP.NET dep
│   ├── FinanceApp.McpServer/          # stdio MCP server hosting the monthly-summary skill
│   └── FinanceApp.Web/                # Blazor Server host: Program.cs, Components/Pages/*, Services/*
├── docs/
│   └── spec.md                        # this file
├── tests/
│   ├── FinanceApp.Skills.Tests/       # xUnit — calls BudgetSkill's [AgentSkillScript] methods directly, no LLM
│   └── FinanceApp.Core.Tests/         # repository/EF Core tests
```

**Project references**: `Web → Core, AI, Skills` · `McpServer → Core` · `Skills → Core, AI` · nothing
references back up into `Core` — it stays a clean shared library between `Web` and `McpServer`.

**Auth**: full **ASP.NET Core Identity** (register/login, not a stub) — every entity's `UserId` (Guid)
references a real authenticated `ApplicationUser`, not a hardcoded seed. See §2a for the full design and
the hard "agent/skill calls must never leak across users" isolation rule.

---

## 2. Data Model (EF Core + PostgreSQL)

`src/FinanceApp.Core/Entities/`:
- **ApplicationUser** — Identity-backed (`IdentityUser<Guid>`, table `AspNetUsers`), not a hand-rolled
  entity — see §2a. Adds one custom property, `DisplayName`; `Email`/`UserName` come from Identity itself.
- **Category** — `Id, UserId?, Name, Kind(Expense/Income), IsSystemDefault` — **`UserId` is nullable**:
  `null` + `IsSystemDefault = true` marks the global starter set (Groceries, Dining, Transport, Utilities,
  Entertainment, Income, Other), still seeded via `HasData` with no FK target required; user-created
  categories carry a real `ApplicationUser.Id`. (This nullable-FK shape is deliberate — see §2a point 2 for
  why a non-nullable `UserId` here breaks under real per-user rows.)
- **Transaction** — `Id, UserId, CategoryId?, Amount(decimal 18,2), Currency, OccurredOn, Description, Source(Manual/Agent/ReceiptOcr), ReceiptId?, CreatedAtUtc`
- **Budget** — `Id, UserId, CategoryId, PeriodMonth, LimitAmount` — unique index `(UserId, CategoryId, PeriodMonth)`
- **SavingsGoal** — `Id, UserId, Name, TargetAmount, CurrentAmount, TargetDate?, MonthlyContribution?, CreatedAtUtc`
- **Receipt** — `Id, UserId, ImageBytes(bytea), ContentType, UploadedAtUtc, OcrStatus(Pending/Succeeded/Failed/Unsupported), OcrRawResponse?, ExtractedVendor/Amount/Date/CategoryId, ResultingTransactionId?`

Design choices: receipt images stored as `bytea` directly in Postgres (no object storage needed at this
scale); **monthly summaries are computed on the fly, not persisted** (cheap aggregation, avoids a
cache-invalidation problem for no benefit yet). `FinanceDbContext` uses `HasConversion<string>()` for
enums (readable in psql) and a defensive `HasQueryFilter` scoped to the **current authenticated user**
(§2a) on every entity — not a hardcoded seed id anymore.

Migrations live in `FinanceApp.Core/Migrations/`, generated with `FinanceApp.Web` as the EF Core
design-time startup project (`Microsoft.EntityFrameworkCore.Design` referenced by `Web`, not `Core`, so
that design-time dependency doesn't leak into `McpServer`).

**Migration reset required**: an `InitialCreate` migration was already generated and applied against the
pre-Identity shape (plain `User` entity, non-nullable `Category.UserId`) during earlier scaffolding.
Changing `FinanceDbContext`'s base class to `IdentityDbContext<...>` and making `Category.UserId` nullable
doesn't diff cleanly against that — at implementation time: `dotnet ef migrations remove`, then
`docker compose down -v` (drops the local `pgdata` volume; no real data exists yet, so this is free now and
expensive later), then regenerate `InitialCreate` against the new Identity + domain model.

---

## 2a. Authentication (ASP.NET Core Identity)

Full-featured Identity (register/login), not a stub — replaces the earlier "single-user, no auth" v1
assumption entirely.

**Entities/DbContext**: new `ApplicationUser : IdentityUser<Guid>` (in `FinanceApp.Core/Entities/`) replaces
the hand-rolled `User` entity. `FinanceDbContext` changes base class from `DbContext` to
`IdentityDbContext<ApplicationUser, IdentityRole<Guid>, Guid>` (still calls `base.OnModelCreating` — already
does). This pulls in `AspNetUsers`/`AspNetRoles`/`AspNetUserClaims`/etc. automatically; the old `Users`
table goes away. Package: `Microsoft.AspNetCore.Identity.EntityFrameworkCore`, referenced by `Core`.

1. **`Category.UserId` is nullable** — see §2. Query filter becomes
   `c => c.UserId == null || c.UserId == _currentUserId`.

2. **Why nullable, not a straight FK swap**: the seeded starter-category `HasData` rows currently point at
   a constant `SeedData.DefaultUserId`. Once users are created at runtime via `UserManager` (not `HasData`),
   that constant matches no real row — a non-nullable `Category.UserId` with a real FK to `AspNetUsers`
   turns the seed into an FK violation at migration time. Nullable + `IsSystemDefault` sidesteps this
   cleanly and is the intended permanent shape, not a workaround to revisit.

3. **`ICurrentUserAccessor`** (new interface + implementation, `FinanceApp.Web/Services/` — needs
   `AuthenticationStateProvider`, so it lives in `Web`, not `Core`) resolves the authenticated user's `Guid`
   id from the current Blazor circuit's auth state.

4. **Query filters must capture a `FinanceDbContext` field, not call the accessor inside the LINQ
   expression.** The constructor takes `ICurrentUserAccessor` and assigns `_currentUserId` to a **readonly
   field**; `HasQueryFilter` lambdas reference that field (`u => u.Id == _currentUserId`), not
   `_accessor.UserId` inline — referencing a live service call inside the filter expression interacts badly
   with EF Core's model cache.

5. **DbContext registration is split, not one `AddDbContext` for everything**:
   - `AddDbContext<FinanceDbContext>` (scoped) — required by `AddEntityFrameworkStores<FinanceDbContext>()`,
     which Identity's stores expect to be scoped.
   - `AddDbContextFactory<FinanceDbContext>` — for component/skill code that isn't one single short-lived
     operation. Blazor Server keeps one circuit alive across many interleaved operations (page loads *and*
     agent skill scripts firing mid-conversation); a single scoped context shared across all of that hits
     the classic "A second operation was started on this context before a previous operation completed"
     failure. This refines, not replaces, §3.4's existing `IServiceScopeFactory`-per-script-invocation rule
     for skills — the factory is what those per-script scopes resolve their `FinanceDbContext` from.

6. **Blazor Identity UI — scaffold into a temp directory and copy the pieces out; do not re-scaffold
   `FinanceApp.Web` in place and do not hand-write the Identity pages.** Re-scaffolding in place would
   clobber the already-written `Program.cs`, `appsettings.json`, and `Components/`. Instead: run
   `dotnet new blazor -au Individual` into a throwaway directory, then copy
   `Components/Account/**` plus the Identity-specific lines out of *that* project's generated `Program.cs`
   into the real one — `IdentityRedirectManager`, `IdentityUserAccessor`, the revalidating
   `AuthenticationStateProvider`, and
   `AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies()`. Use that exact call, **not**
   `AddIdentity<TUser, TRole>()`, which wires a conflicting cookie scheme in this combination.

   **MudBlazor trap on these pages (found via actual POST testing, not just `dotnet build`):** the
   `Components/Account/Pages/**` pages are static SSR (`[ExcludeFromInteractiveRouting]`, see
   `Pages/_Imports.razor`) and bind via `[SupplyParameterFromForm]`, which requires each field's rendered
   `<input>` to carry a real `name="Input.X"` HTML attribute. Blazor's built-in `InputText`/`InputCheckbox`
   emit this automatically from the bound `FieldIdentifier`; **`MudTextField`/`MudCheckBox` do not** — a
   naive swap compiles cleanly and looks right in the browser, but the underlying `<input>` has no `name`,
   so a real form submission sends nothing for that field and the page just re-renders "field is required."
   `dotnet build` cannot catch this — only an actual POST (or a real browser) does. Fix: splat an explicit
   `name="Input.X"` attribute onto every `MudTextField`/`MudCheckBox` bound to a `[SupplyParameterFromForm]`
   model on these pages (confirmed working — MudBlazor forwards unmatched attributes to the native input).
   This does **not** apply to pages using `@rendermode InteractiveServer` with `@bind-Value` alone (no
   `[SupplyParameterFromForm]`), e.g. the future finance CRUD pages — only to Identity's static-SSR forms.

7. **Firm requirement — agent/skill calls must be strictly scoped to the requesting user, always.** A skill
   invocation made on behalf of User A must never resolve, read, or return User B/C's data, regardless of
   how the request is phrased or what the LLM internally decides to do. This is stronger than "the database
   happens to filter by user" — the scoping must be enforced structurally by the service/skill layer using
   the authenticated user id from `ICurrentUserAccessor`/`ChatSessionService` (§3.4), never by trusting a
   user-identifier-shaped value that arrives as a free-form LLM-provided argument. This applies to every
   skill (§4) and, with extra force, to the MCP server (§5), which has no ambient auth of its own. See §8
   for the required verification check.

---

## 3. ChatClientFactory Port + IChatClient → AIAgent Boundary

**Location**: new project `src/FinanceApp.AI` — isolates all LLM-provider code so `Skills` (needed for the
receipt-OCR vision call) doesn't need an ASP.NET Core dependency, and `Web/Program.cs` stays thin.

### 3.1 `ChatClientFactory.cs` (ported from gitea-aihook)
Same shape as the reference implementation: `CreateChatClient(AiOptions options)` switches on engine type
(`Azure/OpenAI/Ollama/Gemini/Anthropic` constants) and returns `IChatClient`:
- Azure → `OpenAI.Chat.ChatClient(credential, model, options: new OpenAIClientOptions{Endpoint=...}).AsIChatClient()`
- OpenAI → `new OpenAIClient(key).GetChatClient(model).AsIChatClient()`
- Ollama → `new OllamaApiClient(httpClientWith40MinTimeout){ SelectedModel = model }`
- Gemini → `new GenerativeAIChatClient(key, model)`
- **Anthropic (finishing what gitea-aihook left incomplete)**: try `Microsoft.Agents.AI.Anthropic`
  (1.17.0-preview) first; fall back to `Anthropic.SDK` (5.10.0) if that package's shape doesn't fit the
  uniform `IChatClient` pattern — both isolated behind one `case` branch (see "Verify" below)
- Unknown type → `NotSupportedException`

### 3.2 Config — `AiOptions` (strongly typed, bound from `"AI"` section)
```csharp
public sealed class AiOptions {
    public const string SectionName = "AI";
    public string EngineType { get; set; } = "";   // Azure|OpenAI|Ollama|Gemini|Anthropic
    public string? Endpoint { get; set; }
    public string ModelName { get; set; } = "";
    public string? ApiKey { get; set; }
    public bool SupportsVision { get; set; }
}
```
Env-var override convention preserved from gitea-aihook (`AI__ENGINE_TYPE`, `AI__ENDPOINT`,
`AI__MODEL_NAME`, `AI__API_KEY`, `AI__SUPPORTS_VISION`) — Docker-friendly, no extra binding code needed.
Dev default in `appsettings.json`: Ollama (no key required, lowest local friction). `appsettings.example.json`
documents all five provider shapes with placeholders; real secrets only via `.env`/environment.

`IChatClient` is registered as a **singleton** (thread-safe, expensive-ish to construct, no per-request state).

### 3.3 `IChatClient` → `AIAgent` — resolved (was the one genuinely open question), quarantined to one file
**Verified 2026-08-10** via a reflection dump + a runnable round-trip test against the real
`Microsoft.Agents.AI` 1.17.0 / `Microsoft.Extensions.AI` 10.8.3 packages (a hand-rolled `IChatClient` test
double, no network dependency — construct → attach an `AgentSkillsProvider` → run a prompt → got a
response back). Findings, favorable — **no per-provider fallback needed**:
- `Microsoft.Extensions.AI.ChatClientExtensions.AsAIAgent(this IChatClient, ChatClientAgentOptions, ILoggerFactory?, IServiceProvider?)`
  → `ChatClientAgent : AIAgent` is a **universal** extension method. It works identically for *any*
  `IChatClient` — Azure/OpenAI, Ollama, Gemini, Anthropic all reach `AIAgent` through this one call; none
  of them need to be Responses-API-capable. The doc's Azure Responses-client example is one way in, not
  the only way in.
- `ChatClientAgentOptions.AIContextProviders` (an `IEnumerable<AIContextProvider>`) accepts an
  `AgentSkillsProvider` directly — confirmed `agent.AIContextProviders[0]` round-trips to the exact
  `AgentSkillsProvider` instance passed in.
- **One detail that didn't match the original guess**: `ChatClientAgentOptions` has no `Instructions`
  property of its own — set instructions via `ChatClientAgentOptions.ChatOptions.Instructions`
  (`ChatOptions` being `Microsoft.Extensions.AI.ChatOptions`) instead; confirmed it round-trips to
  `AIAgent.Instructions` after construction.

`src/FinanceApp.AI/IAgentFactory.cs` + `AgentFactory.cs` are implemented (the single seam allowed to
construct an `AIAgent`):
```csharp
public interface IAgentFactory {
    AIAgent CreateAgent(AgentSkillsProvider skillsProvider, string? instructions = null);
}
```
Since construction is now confirmed universal, the originally-planned graceful-degradation fallback ("log a
startup warning and mark that provider's chat as unavailable if no generic path exists") is **not needed at
this layer** — every `IChatClient` `ChatClientFactory` (§3.1) can produce works here unchanged. Runtime
failures from a specific provider (bad API key, network down, etc.) are an ordinary error-handling concern
for `ChatSessionService`/`Chat.razor`, not something `AgentFactory` needs to special-case.

### 3.4 Scoped vs. singleton
- **Singleton**: `IChatClient`, `AgentFileSkillsSource` (just reads files), the MCP `McpClient` connection.
- **`AIAgent` built per conversation**, not as a singleton — via a **scoped** `ChatSessionService`
  (`Web/Services/ChatSessionService.cs`), because `BudgetSkill` and the inline OCR skill close over
  DB-backed services.
- `BudgetSkill` and `ReceiptOcrSkillFactory` are given an injected `IServiceScopeFactory` (not a live scope)
  and open `using var scope = _scopeFactory.CreateScope();` **inside each script invocation** — a script can
  fire after the original Blazor circuit's DI scope is gone, so capturing a live scope risks
  `ObjectDisposedException` on the DbContext. Each such scope resolves `FinanceDbContext` via
  `AddDbContextFactory` (§2a point 5), not the request-scoped `AddDbContext` registration.
- `ChatSessionService` resolves the real authenticated user through `ICurrentUserAccessor` (§2a) when it
  builds the per-conversation `AIAgent`, and passes that `Guid` explicitly into `BudgetSkill`'s and
  `ReceiptOcrSkillFactory`'s construction — every skill script then operates against that specific user,
  never the old hardcoded default. This is what makes the §2a point 7 isolation rule concrete: a skill has
  no other source of "which user" than what `ChatSessionService` gave it at construction time.

---

## 4. The Four Skills

### 4.1 Budgeting → Class-based skill
`src/FinanceApp.Skills/Budgeting/BudgetSkill.cs`, deriving `AgentClassSkill<BudgetSkill>`:
- `[AgentSkillScript("add_transaction")]`, `list_transactions`, `set_budget`, `check_budget_status`
  (returns per-category spent/limit/percent; flags Near ≥80%, Over >100%)
- `[AgentSkillResource("budgeting-policy")]` — the near/over-budget house rules
- Constructed with `IServiceScopeFactory` **and the current authenticated `Guid userId`** (from
  `ChatSessionService` via `ICurrentUserAccessor`, §2a/§3.4), registered via
  `.UseSkill(new BudgetSkill(scopeFactory, userId))` — every script method filters/writes against that
  specific user only, never a hardcoded default and never a user id taken from an LLM-provided argument
- **Directly unit-testable** (`tests/FinanceApp.Skills.Tests/BudgetSkillTests.cs`) — call the
  `[AgentSkillScript]` methods as plain async methods against a seeded `FinanceDbContext`, no LLM/agent
  involved. This is the concrete payoff of choosing class-based here.

### 4.2 Receipt OCR → Code-defined inline skill
`src/FinanceApp.Skills/ReceiptOcr/ReceiptOcrSkillFactory.cs` — **not** a static instance; built per
request/upload so it can close over the specific `Receipt` and the current DI-scoped `IChatClient`:
```csharp
AgentInlineSkill Create(IChatClient chatClient, IServiceScopeFactory scopeFactory, Guid userId, Guid receiptId, bool supportsVision)
```
`userId` comes from `ChatSessionService`/`ICurrentUserAccessor` (§2a/§3.4) — the factory's script closes
over it directly, so the receipt lookup and the resulting `Transaction` write are always scoped to the
uploading user, not derived from the receipt row alone (a receipt id could otherwise be guessed/reused
across users). OCR is done via a **multimodal call on the same `IChatClient`** already configured for the app (avoids a
second OCR-specific dependency/API key). `supportsVision` (from `AiOptions.SupportsVision`) defaults **true**
for Azure/OpenAI/Gemini/Anthropic and **false** for Ollama (most local models aren't vision-capable) —
operator can flip it if they've pulled a vision model like `llama3.2-vision`. When false, the skill's
script returns a "please enter manually" message and `ReceiptUpload.razor` disables the AI-extract button
in favor of manual entry fields — graceful degradation, not a crash.

### 4.3 Savings/Investment Goals → File-based skill
`skills/savings-goals/SKILL.md` (YAML frontmatter: `name`, `description`, `license`, `compatibility`) plus
`references/compound-interest.md` and `references/fifty-thirty-twenty.md` holding the actual guidance text
— editable without recompiling, which is the stated reason for choosing file-based here.

**Deliberately no `scripts/` subfolder** for this skill — keeps `SubprocessScriptRunner` (and its
Python/pwsh runtime requirement) out of the Docker image entirely. If chat-driven goal CRUD is needed
later, add it as a small class-based-skill method instead of a subprocess script.

**Packaging into Docker**: `skills/` is added to `FinanceApp.Web.csproj` as `Content` items
(`CopyToPublishDirectory`) pointed at the repo-root `skills/` folder, so both `dotnet publish` and the
Docker build's `dotnet publish` step carry it into the output automatically — no separate `COPY` line in
the Dockerfile, and the same relative path (`Skills:FilePath`, default `"skills"` under
`AppContext.BaseDirectory`) works identically in dev and in the container.

### 4.4 Monthly Summary & Advice → MCP-based skill
Registered via `AgentSkillsProviderBuilder().UseMcpSkills(mcpClient)`, where `mcpClient` comes from the
singleton `McpServerLauncher` (§5). **If the MCP server failed to start, `.UseMcpSkills(...)` is skipped
entirely for that agent build** — the other three skills keep working; this is the "optional at startup,
fail gracefully" behavior required for this piece.

All four sources combine in one place:
```csharp
new AgentSkillsProviderBuilder()
    .UseFileSkill(skillsPath)
    .UseSkill(budgetSkill)
    .UseSkill(receiptOcrInlineSkill)   // built per-request, see §4.2/§3.4
    .UseMcpSkills(mcpClient)           // only if mcpClient is non-null
    .Build();
```

---

## 5. The MCP Server Project (`FinanceApp.McpServer`)

Console app (`OutputType=Exe`), references `FinanceApp.Core` (reads transactions/budgets/goals) plus
`ModelContextProtocol`/`ModelContextProtocol.Core` and `Microsoft.Agents.AI.Mcp` (alpha — for
`skill://index.json`/`skill-md` server-side helpers; verify these exist in that package vs. hand-rolled
JSON at implementation time). Launched with `--server` arg for stdio-MCP mode.

**Critical constraint, called out explicitly because it's the single most likely silent-failure bug in
this subsystem: stdout is the JSON-RPC transport channel for stdio MCP.** No `Console.WriteLine`, no
default console logger — all diagnostics go to a file sink (`Serilog.Sinks.File`) or `Console.Error` only.

The server's own logic (`MonthlySummaryTool`) stays simple by design: query `FinanceDbContext` for the
month's transactions/budgets/goals, compute totals/adherence/progress, and **return structured data** — the
narrative summary + recommendations are generated by the *calling* agent's LLM, not by the MCP server.

**No ambient auth — `userId` must be an explicit tool argument.** `FinanceApp.McpServer` is a singleton
subprocess launched once at `Web` startup and shared across every authenticated user's conversations — it
has no HTTP request, no cookie, no `AuthenticationStateProvider`, nothing to derive "current user" from on
its own. The monthly-summary tool call therefore takes `userId` as an explicit argument supplied by the
calling agent (which got it from `ChatSessionService`/`ICurrentUserAccessor`, §2a/§3.4), and the trust
boundary is: same host, stdio transport, the `Web` process is the trusted caller — the server does not
re-authenticate the id it's given. This is the MCP-specific instance of the §2a point 7 firm rule: getting
this argument wrong is the one place in the whole design where a bug would leak one user's monthly summary
to another.

**Migration ownership**: `FinanceApp.Web` owns all migrations (`Database.MigrateAsync()` at startup, gated
by `AUTO_MIGRATE` env var, default true). `McpServer` only ever issues read queries against the same
connection string — never migrates — avoiding two processes racing on schema changes.

**Lifecycle** (`src/FinanceApp.Web/Services/McpServerLauncher.cs`, an `IHostedService`):
- `StartAsync`: launches `dotnet <path-to-McpServer.dll> --server` via `StdioClientTransport`, with the
  connection string passed explicitly through `EnvironmentVariables` (not relied on via process
  inheritance), bounded by a 10s startup timeout. On any failure, logs a warning and leaves its `Client`
  property `null` — `AgentFactory` checks this and omits `.UseMcpSkills(...)` for that null case.
- `StopAsync`: disposes the `McpClient`/subprocess cleanly.
- DLL path resolved via config (`Mcp:ServerDllPath`) — dev default relative to `Web`'s `bin/Debug`
  output, Docker default `/app/mcpserver/FinanceApp.McpServer.dll` (see §6).

---

## 6. Docker / Compose

**`src/FinanceApp.Web/Dockerfile`** — multi-stage build that publishes **both** `Web` and `McpServer` from
one image (`dotnet publish` each into `/app/web` and `/app/mcpserver` in the build stage, then `COPY
--from=build` both into the final `aspnet:10.0` runtime image). `skills/` needs no separate `COPY` — it's
already part of `Web`'s publish output per §4.3. Base runtime image does **not** need Python/pwsh installed,
specifically because §4.3 avoided giving the file skill a `scripts/` folder — flagged so nobody adds a
script-driven skill later without also updating the Dockerfile.

**`docker-compose.yml`**: `postgres` (image `postgres:17`, healthcheck, named volume) + `web` (built from
the Dockerfile above, `depends_on: postgres healthy`, all config — connection string, `AI__*` provider
settings — supplied via environment, defaulting to Ollama at `http://host.docker.internal:11434` for
zero-friction local start). Ollama itself is **not** containerized by default (assumed to run on the host);
a commented-out `ollama:` service is included in the compose file for anyone who wants it dockerized too.

**`.env.example`**: `POSTGRES_DB/USER/PASSWORD`, `AI_ENGINE_TYPE`, `AI_ENDPOINT`, `AI_MODEL_NAME`,
`AI_API_KEY`, `AI_SUPPORTS_VISION` — mirrors the `.env.example` convention from gitea-aihook; real secrets
never committed.

**Migrations**: applied automatically on `Web` startup (§5), guarded by `AUTO_MIGRATE` so it can be
disabled later without a code change if a multi-instance deployment is ever needed.

---

## 7. Blazor UI Pages

**Auth**: `Components/Account/**` (copied in per §2a point 6 — Login, Register, Logout, Manage, etc.),
`MainLayout` shows a login/logout link based on `AuthenticationStateProvider`. The Razor Components
endpoint (or each finance page individually) is protected via `[Authorize]` /
`.RequireAuthorization()` — every page below except the Account pages themselves requires an
authenticated user.

`src/FinanceApp.Web/Components/Pages/`:
- **`Transactions.razor`** — plain CRUD grid over `Transaction`, direct repository calls, no agent
  involved. Proves the agent is assistive, not the only way to enter data.
- **`Budgets.razor`** — CRUD over `Budget` + per-category progress bars, computed via the *same* shared
  method `BudgetSkill.CheckBudgetStatusAsync` uses (factored onto `BudgetRepository` in `Core` so the UI
  and the skill can never drift apart).
- **`Goals.razor`** — CRUD over `SavingsGoal`, plus a "get guidance" action that routes into `Chat.razor`
  with a pre-filled prompt targeting the savings-goals skill.
- **`ReceiptUpload.razor`** — `InputFile` with an **explicit, generous `maxAllowedSize`** override (Blazor's
  default `OpenReadStream` cap is 512 KB and will throw/truncate without this), image preview, "Extract
  with AI" button disabled with a tooltip when `AI:SupportsVision` is false (manual entry fields shown
  instead).
- **`Chat.razor`** — the demo centerpiece: freeform chat wired to `ChatSessionService` (builds one
  `AIAgent` per Blazor circuit), streams the response, and renders an **`AgentActivityLog`** side panel.
  The activity log works by inspecting the streamed `AgentRunResponseUpdate` sequence for
  `FunctionCallContent` items named `load_skill` / `read_skill_resource` / `run_skill_script` (the three
  tools `AgentSkillsProvider` registers) and pushing them into an `ObservableCollection<SkillActivityEntry>`
  that the `AgentActivityLog.razor` component renders, using `InvokeAsync(StateHasChanged)` since the
  stream is consumed off the Blazor sync context. Each send allocates a per-circuit
  `CancellationTokenSource`, cancelled on new-message-sent or component dispose, so a stale streaming
  response can't mutate UI state after navigation away.

---

## 8. Verification

1. **Unit tests** (`FinanceApp.Skills.Tests`) — call `BudgetSkill`'s script methods directly, no LLM. The
   one skill type fully testable without any model call, by design.
2. **Discriminating end-to-end check, per skill** — send a prompt that specifically requires that skill,
   then assert on the **tool-call trace** (`FunctionCallContent` named `load_skill` with the right skill
   name, then `run_skill_script`/`read_skill_resource`) rather than eyeballing the prose — narrative output
   alone doesn't prove the skill was actually invoked (the LLM could answer from general knowledge). This
   reuses the exact mechanism built for `AgentActivityLog` in §7.
3. **Graceful-degradation check** — deliberately break `Mcp:ServerDllPath` and confirm the app still
   starts, the other three skills still work, and the monthly-summary skill is simply absent from the
   trace (no crash).
4. **Local dev loop**: `docker compose up postgres -d`, then `dotnet run --project src/FinanceApp.Web`
   (auto-migrates, launches `McpServer` from its `bin/Debug` output); `dotnet watch` for UI hot reload.
5. **Full stack**: `docker compose up --build`; check `docker compose logs web` for the absence of the "MCP
   skills server unavailable" warning, and `docker compose exec postgres psql ... -c '\dt'` to confirm
   migrations applied.
6. **Data isolation (§2a point 7 — required, not optional)**: register two separate users, add distinct
   transactions/categories/goals under each, and confirm: (a) the CRUD pages for User A never show User B's
   rows; (b) a chat prompt under User A's session cannot be made to return User B's transactions, budgets,
   goals, or receipts under any phrasing — including prompts that directly reference User B's data by
   guessed/observed identifiers; (c) the monthly-summary MCP skill, exercised from both sessions, returns
   only each respective user's figures.

---

## Assumptions
1. Receipt images stored as Postgres `bytea`, not external object storage.
2. Monthly summaries computed on the fly, never persisted.
3. Local dev defaults to Ollama (no API key needed); `SupportsVision` defaults false for Ollama, true for
   the four cloud providers.
4. Savings-goals file skill ships with no `scripts/` folder, specifically to keep Python/pwsh out of the
   Docker image — revisit the Dockerfile if that ever changes.
5. Migrations owned exclusively by `Web`; `McpServer` is read-only against the same database.
6. Ollama itself isn't containerized by default in Compose (assumed to run on the host).

## Future improvements (deliberately out of scope for now)
The `-au Individual` scaffold's 2FA, Passkey, and External Login pages were copied in along with the rest
of `Components/Account/**` (§2a point 6) but have since been **removed entirely** — code, nav links, and
the endpoints in `IdentityComponentsEndpointRouteBuilderExtensions.cs` that backed them
(`PerformExternalLogin`, `PasskeyCreationOptions`, `PasskeyRequestOptions`, `Manage/LinkExternalLogin`) —
because this demo app only needs password-based register/login/reset/profile to prove out the Skills
feature; that surface area was unused complexity, not a security decision. If ever revisited:
- **Two-factor authentication** (`Manage/EnableAuthenticator`, `Disable2fa`, `ResetAuthenticator`,
  `TwoFactorAuthentication`, `GenerateRecoveryCodes`, `Shared/ShowRecoveryCodes`, `Pages/LoginWith2fa`,
  `Pages/LoginWithRecoveryCode`) — re-scaffold a fresh `-au Individual` template and re-copy these files;
  `Login.razor`'s `LoginUser()` would need its `result.RequiresTwoFactor` branch (removed) restored.
- **Passkeys/WebAuthn** (`Manage/Passkeys`, `RenamePasskey`, `Shared/PasskeySubmit(.razor.js)`,
  `PasskeyInputModel.cs`, `PasskeyOperation.cs`, plus the `PasskeyCreationOptions`/`PasskeyRequestOptions`
  endpoints) — same re-copy approach; note `AspNetUserPasskeys` stays in the schema regardless (it's part
  of `IdentityDbContext`'s standard model, unaffected by removing the UI).
- **External login providers** (`Manage/ExternalLogins`, `Pages/ExternalLogin`, `Shared/ExternalLoginPicker`,
  the `PerformExternalLogin`/`Manage/LinkExternalLogin` endpoints) — same re-copy approach; would also need
  actual provider registration (Google/Microsoft/etc. `AddAuthentication().Add...()` calls) in `Program.cs`,
  which was never wired up even before removal.

## Verify at implementation time (isolated uncertainty, preview/alpha package surfaces)
1. ~~**`IChatClient` → `AIAgent` construction`~~ **RESOLVED 2026-08-10** — see §3.3;
   `IAgentFactory`/`AgentFactory.cs` implemented and building against the real package.
2. **Anthropic provider** — whether `Microsoft.Agents.AI.Anthropic` (preview) exposes a plain `IChatClient`
   factory, or is actually agent-level and needs a documented exception in `ChatClientFactory`'s otherwise
   uniform shape; `Anthropic.SDK` 5.10.0 is the fallback.
3. **`Microsoft.Agents.AI.Mcp`** (alpha) — exact API for building `skill://index.json` server-side content
   in `FinanceApp.McpServer`, and the experimental-usage diagnostic ID to suppress. Still open — the spike
   only installed core `Microsoft.Agents.AI`; `.UseMcpSkills(...)` (§4.4) wasn't in that package's exported
   types, so it must come from this alpha package and remains unverified.
4. **`ModelContextProtocol` vs `ModelContextProtocol.Core`** — confirm which package/namespace
   `McpClient`/`StdioClientTransport` actually live in.
5. **`AgentSkillsProviderBuilder`** fluent method signatures — **partially resolved 2026-08-10** via the
   same reflection dump: `.UseFileSkill(skillPath, options, scriptRunner)`, `.UseFileSkills(...)`,
   `.UseSkill(AgentSkill)`, `.UseSkills(...)`, `.UseSource(...)`, `.UseFilter(Func<...>)`, `.Build()` all
   confirmed to exist with roughly the expected shapes. `.UseSkill` takes a plain already-constructed
   `AgentSkill` instance, not a factory delegate — matches the plan (§4.2's per-request inline OCR skill is
   built via `ReceiptOcrSkillFactory.Create(...)` *before* being passed to `.UseSkill(...)`, not deferred to
   the builder). `.UseMcpSkills` was **not** among core `Microsoft.Agents.AI`'s exported types — still
   unverified, tracked under item 3 above (comes from the alpha Mcp package).
6. Blazor `InputFile` max-size override syntax against the current .NET 10 API.
7. Confirm `AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies()` vs
   `AddIdentity<ApplicationUser, IdentityRole<Guid>>()` cookie-scheme interaction against the installed
   ASP.NET Core 10 Identity APIs before wiring `Program.cs` (§2a point 6) — the scaffolded
   `-au Individual` template's `Program.cs` is the reference to copy from, not assumed from memory.

## Critical files to create first
- `src/FinanceApp.Core/Entities/ApplicationUser.cs` — Identity-backed user entity (§2a)
- `src/FinanceApp.Web/Services/ICurrentUserAccessor.cs` — resolves the authenticated user id from
  `AuthenticationStateProvider`; everything else's user-scoping depends on this (§2a points 3-4)
- `src/FinanceApp.Web/Components/Account/**` — copied from a `-au Individual` scaffold, not hand-written (§2a point 6)
- `src/FinanceApp.AI/AgentFactory.cs` — the isolated `IChatClient`→`AIAgent` risk boundary (§3.3)
- `src/FinanceApp.AI/ChatClientFactory.cs` — ported multi-provider factory (§3.1)
- `src/FinanceApp.Skills/Budgeting/BudgetSkill.cs` — class-based skill + its unit tests (§4.1)
- `src/FinanceApp.McpServer/Program.cs` — stdio MCP server; must never write to stdout (§5)
- `src/FinanceApp.Web/Services/McpServerLauncher.cs` — subprocess lifecycle + graceful degradation (§5)
- `src/FinanceApp.Core/FinanceDbContext.cs` — schema shared by `Web` and `McpServer` (§2/§2a/§5)
- `docker-compose.yml`, `src/FinanceApp.Web/Dockerfile` — multi-stage build bundling both apps (§6)
