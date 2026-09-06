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
  4. Monthly summary & advice → **MCP-based skill** (standing HTTP MCP server in the same solution, JWT
     bearer auth — migrated from an earlier stdio-subprocess-per-session design, see §4.4/§5)

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
│   ├── savings-goals/                 # guidance only, no scripts/ (§4.3)
│   │   ├── SKILL.md
│   │   └── references/{compound-interest.md, fifty-thirty-twenty.md}
│   └── savings-calculator/            # script-backed exact math (§4.3)
│       ├── SKILL.md
│       ├── references/formula.md
│       └── scripts/{project-savings.py, project-debt-payoff.py}
├── src/
│   ├── FinanceApp.Core/               # domain entities + EF Core DbContext + repositories (shared by Web & McpServer)
│   ├── FinanceApp.AI/                 # ChatClientFactory + AgentFactory (isolated LLM-provider risk boundary)
│   ├── FinanceApp.Skills/             # BudgetSkill (class-based) + ReceiptOcrSkillFactory (inline) — testable, no ASP.NET dep
│   ├── FinanceApp.McpServer/          # standing HTTP MCP server (JWT bearer auth) hosting the monthly-summary skill
│   └── FinanceApp.Web/                # Blazor Server host: Program.cs, Components/Pages/*, Services/*
├── docs/
│   └── spec.md                        # this file
├── tests/
│   ├── FinanceApp.Skills.Tests/       # xUnit — BudgetSkill/ReceiptOcr scripts, SubprocessScriptRunner, MonthlySummaryRepository, no LLM/python3
│   ├── FinanceApp.AI.Tests/           # xUnit — ChatClientFactory, SkillActivityExtractor, file-skill round trips
│   └── FinanceApp.McpServer.Tests/    # xUnit — resource-handler logic (InMemory), a real WebApplicationFactory-based concurrent multi-user isolation test, + real HTTP subprocess round trip, no LLM/Postgres
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
w- **Receipt** — `Id, UserId, ImageBytes(bytea), ContentType, UploadedAtUtc, OcrStatus(Pending/Succeeded/Failed/Manual), OcrRawResponse?, ExtractedVendor/Amount/Date/CategoryId, ResultingTransactionId?`

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

5. **DbContext registration: plain `AddDbContext<FinanceDbContext>` (scoped) only** — required by
   `AddEntityFrameworkStores<FinanceDbContext>()`, which Identity's stores expect to be scoped.
   **Correction from an earlier draft of this point**: `AddDbContextFactory<FinanceDbContext>` *alongside*
   `AddDbContext<FinanceDbContext>` was tried and found to conflict — DI validation throws "Cannot consume
   scoped service `DbContextOptions<FinanceDbContext>` from singleton `IDbContextFactory<FinanceDbContext>`"
   once the context has an extra scoped constructor dependency (`ICurrentUserAccessor`, point 3 below) — the
   two registrations fight over `DbContextOptions<FinanceDbContext>`. Skills solve the "one circuit, many
   interleaved operations" problem differently instead (§3.4's addendum): each script resolves only
   `DbContextOptions<FinanceDbContext>` from its own `IServiceScopeFactory.CreateScope()` (safe — plain
   `AddDbContext` still registers that standalone) and constructs `FinanceDbContext` manually with a
   `FixedCurrentUserAccessor` bound to the skill's captured `userId`, rather than resolving the context
   itself from DI. No `AddDbContextFactory` needed anywhere in this app.

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

### 3.1 `ChatClientFactory.cs` (ported from gitea-aihook) — **implemented 2026-08-11**
Fetched the real reference source (`GiteaAiSummarizerNET/Services/ChatClientFactory.cs`, not assumed from
memory) to confirm exact package IDs/types before writing this. Same shape: static
`ChatClientFactory.CreateChatClient(AiOptions options)` switches on engine type
(`Azure/OpenAI/Ollama/Gemini/Anthropic` constants) and returns `IChatClient`:
- Azure → `OpenAI.Chat.ChatClient(credential, model, options: new OpenAIClientOptions{Endpoint=...}).AsIChatClient()`
  (package `OpenAI` 2.12.0 + `Microsoft.Extensions.AI.OpenAI` 10.8.3 for `.AsIChatClient()`)
- OpenAI → `new OpenAIClient(key).GetChatClient(model).AsIChatClient()` (same two packages). `Endpoint` is
  optional here (unlike Azure, where it's required) — when set, overrides the target via
  `OpenAIClientOptions.Endpoint` same as the Azure branch, so this engine type also covers any
  OpenAI-API-compatible local/self-hosted server (LM Studio, vLLM, llama.cpp's server, ...); those don't
  check the API key server-side, so `ApiKey` can be any non-empty placeholder. **Found missing, fixed
  2026-08-12**: this branch originally ignored `Endpoint` entirely and always hit the real OpenAI API
  regardless of config — surfaced as a real 401 testing against a local LM Studio instance.
- Ollama → `new OllamaApiClient(httpClientWith40MinTimeout){ SelectedModel = model }` (package `OllamaSharp` 5.4.30)
- Gemini → `new GenerativeAIChatClient(key, model)` (package `Google_GenerativeAI.Microsoft` 3.6.7 — note the
  underscore in the *package id*; its namespace is `GenerativeAI.Microsoft`, no underscore)
- **Anthropic — resolved, differently than planned** (see verify-item 2 below): `Microsoft.Agents.AI.Anthropic`
  turned out to be agent-level only (`AnthropicClient.AsAIAgent(model:, name:, instructions:)`, confirmed via
  Microsoft Learn's own Anthropic-provider page — no `IChatClient` in its public surface). Used
  `Anthropic.SDK` 5.10.0 (tghamm, community) instead: `new AnthropicClient(key).Messages` implements
  `IChatClient` directly (confirmed via that repo's README + `AnthropicClient.cs`/`APIAuthentication.cs`
  source). Unlike every other provider, its model isn't set at client construction — `Messages` takes it
  per-call via `ChatOptions.ModelId` — so the default from `AiOptions.ModelName` is baked in via
  `new ChatClientBuilder(client).ConfigureOptions(o => o.ModelId ??= options.ModelName).Build()`.
- Unknown type (or a required field missing for the selected type) → `NotSupportedException` /
  `ArgumentException` respectively — no provider construction touches the network, confirmed by
  `tests/FinanceApp.AI.Tests` constructing all 5 with bogus (but shaped) credentials and asserting no throw.
  One deliberate deviation from gitea-aihook's validation: it didn't require `ModelName` for OpenAI or
  Anthropic (only Azure/Ollama/Gemini); this port requires it for those two as well, so a missing model
  name fails fast with a clear `ArgumentException` instead of a confusing error from the provider SDK later.

**`docker-compose.yml`'s commented `web:` placeholder was fixed to match** (found in the same pass,
2026-08-11): it originally guessed PascalCase container-side names (`AI__EngineType`) fed from
single-underscore `.env` names (`AI_ENGINE_TYPE`) — a plausible-looking guess written before
`ChatClientFactory` existed, under the same false assumption §3.2 originally made. Updated so `.env`'s
`AI__*` names and the container-side names are identical (`AI__ENGINE_TYPE: ${AI__ENGINE_TYPE:-Ollama}`,
...) — no renaming in between, one convention end to end.

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
`AI__MODEL_NAME`, `AI__API_KEY`, `AI__SUPPORTS_VISION`) — Docker-friendly. **Correction, found while wiring
`Program.cs` (2026-08-11)**: "no extra binding code needed" was wrong as originally written. The `AI` section's
keys are `ALL_CAPS_WITH_UNDERSCORES` (matching gitea-aihook's own `appsettings.example.json` — it uses the
same raw indexer reads, not `IOptions` binding either), and `ConfigurationBinder`'s property matching is
case-insensitive but **not** underscore-insensitive: `ENGINE_TYPE`/`MODEL_NAME`/`API_KEY`/`SUPPORTS_VISION`
silently fail to bind to `AiOptions.EngineType`/`ModelName`/`ApiKey`/`SupportsVision` via a plain
`section.Bind(options)` (only `ENDPOINT`, which happens to contain no underscore, binds by accident) —
confirmed empirically in `FinanceApp.AI.Tests.ChatClientFactoryTests.ConfigurationBinder_DoesNotMatchUnderscoredKeysToPascalCaseProperties`.
`Program.cs` therefore builds `AiOptions` by reading `configuration.GetSection("AI")["ENGINE_TYPE"]` etc.
directly via the indexer (same as gitea-aihook), not `AddOptions<AiOptions>().BindConfiguration("AI")`.
Also found and fixed: the checked-in `.env.example` had single-underscore `AI_ENGINE_TYPE` etc., which
ASP.NET Core's environment-variable provider never nests under section `AI` at all (only `__` double
underscore maps to `:` — a single `_` stays a literal flat key) — corrected to `AI__ENGINE_TYPE` and so on.
Dev default in `appsettings.json`: Ollama (no key required, lowest local friction). `appsettings.example.json`
(`src/FinanceApp.Web/`) documents all five provider shapes with placeholders; real secrets only via
`.env`/environment.

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

**Superseded 2026-08-16** by the approval-toggle work: `AgentFactory.CreateAgent` now builds via
`Microsoft.Agents.AI.Harness`'s `chatClient.AsHarnessAgent(HarnessAgentOptions, loggerFactory)` instead of
`AsAIAgent(ChatClientAgentOptions, ...)` — `HarnessAgent : DelegatingAIAgent : AIAgent`, so the return type
and every finding above (`AIContextProviders` accepting the `AgentSkillsProvider` directly, instructions via
`ChatOptions.Instructions` rather than a top-level property) still hold unchanged; only the options type
changed. Every `HarnessAgentOptions` capability besides Tool Approval (`ToolApprovalAgentOptions`) and this
app's own skills (still `AIContextProviders`, **not** `HarnessAgentOptions.AgentSkillsSource` — that's a
single raw `AgentSkillsSource`, a different type from this app's composite `AgentSkillsProvider`) is
explicitly disabled (`DisableCompaction`/`DisableFileMemory`/`DisableWebSearch`/`DisableTodoProvider`/
`DisableAgentModeProvider`/`DisableAgentSkillsProvider`/`DisableOpenTelemetry = true`), keeping the running
feature set identical to the pre-Harness design. Confirmed via a real streaming round-trip spike (not just
XML docs) that the stream still surfaces `FunctionCallContent`/`ToolApprovalRequestContent` in the same
shape `SkillActivityExtractor`/`Chat.razor` already parse, that `AutoApprovalRules` is actually evaluated,
that resuming after approval still works, and — the highest-risk untested surface — that a skill registered
mid-session into a dynamic `AgentSkillsSource` (receipt-OCR, §4.2) is still discoverable on the next turn.
One build-time gotcha: `HarnessAgentOptions` ships marked `[Experimental("MAAI001")]` in package 1.17.0
despite Harness itself being announced GA — constructing it is a compile *error* without a narrowly-scoped
`#pragma warning disable MAAI001` around the construction (see `AgentFactory.cs`'s remarks).

`src/FinanceApp.AI/IAgentFactory.cs` + `AgentFactory.cs` are implemented (the single seam allowed to
construct an `AIAgent`):
```csharp
public interface IAgentFactory {
    AIAgent CreateAgent(AgentSkillsProvider skillsProvider, string? instructions = null, ToolApprovalAgentOptions? toolApprovalOptions = null);
}
```
Since construction is now confirmed universal, the originally-planned graceful-degradation fallback ("log a
startup warning and mark that provider's chat as unavailable if no generic path exists") is **not needed at
this layer** — every `IChatClient` `ChatClientFactory` (§3.1) can produce works here unchanged. Runtime
failures from a specific provider (bad API key, network down, etc.) are an ordinary error-handling concern
for `ChatSessionService`/`Chat.razor`, not something `AgentFactory` needs to special-case.

### 3.4 Scoped vs. singleton
- **Singleton**: `IChatClient`, `AgentFileSkillsSource` (just reads files), `McpServerLauncher` and
  `McpAccessTokenIssuer` (both stateless per-session factory methods, §4.4/§5 — **not** the MCP `McpClient`
  connection itself, which is per-session, held on `ChatSessionService`; nor `FinanceApp.McpServer` itself,
  which is now a standing HTTP service shared across every session — see §5's HTTP-migration note).
- **`AIAgent` built per conversation**, not as a singleton — via a **scoped** `ChatSessionService`
  (`Web/Services/ChatSessionService.cs`), because `BudgetSkill` and the inline OCR skill close over
  DB-backed services.
- `BudgetSkill` and `ReceiptOcrSkillFactory` are given an injected `IServiceScopeFactory` (not a live scope)
  and open `using var scope = _scopeFactory.CreateScope();` **inside each script invocation** — a script can
  fire after the original Blazor circuit's DI scope is gone, so capturing a live scope risks
  `ObjectDisposedException` on the DbContext.
- `ChatSessionService` resolves the real authenticated user through `ICurrentUserAccessor` (§2a) when it
  builds the per-conversation `AIAgent`, and passes that `Guid` explicitly into `BudgetSkill`'s and
  `ReceiptOcrSkillFactory`'s construction — every skill script then operates against that specific user,
  never the old hardcoded default. This is what makes the §2a point 7 isolation rule concrete: a skill has
  no other source of "which user" than what `ChatSessionService` gave it at construction time.

**How a script actually gets a correctly-scoped `FinanceDbContext` (found + confirmed while implementing
`BudgetSkill`, 2026-08-10)**: resolving `FinanceDbContext` normally from the script's scope would run it
through the *real* `ICurrentUserAccessor` (`AuthStateCurrentUserAccessor`, Web) — which has no
request/circuit to derive a user from outside HTTP, hits the `InvalidOperationException` catch already
built into it, and returns `UserId = null`. That would make `FinanceDbContext`'s own query filters
(`x.UserId == _currentUserId`) silently return **zero rows for every query**, regardless of the `userId`
the skill was constructed with — the opposite failure mode from a leak, but still wrong, and still a
concrete instance of "the scoping must be enforced structurally" (§2a point 7). Fix, now the standard
pattern for any non-HTTP skill script — `FinanceApp.McpServer` no longer needs it (§5): now a real
ASP.NET Core app, it resolves `FinanceDbContext` through the normal per-HTTP-request DI scope with a
claims-based `ICurrentUserAccessor`, not a captured `IServiceScopeFactory` scope. Resolve only
`DbContextOptions<FinanceDbContext>` from the scope (registered scoped by plain `AddDbContext`, §2a point
5 — resolvable standalone without needing `ICurrentUserAccessor`), then construct
`new FinanceDbContext(options, new FixedCurrentUserAccessor(userId))` manually, where
`FixedCurrentUserAccessor` (`FinanceApp.Core/Abstractions/FixedCurrentUserAccessor.cs`) is a trivial
`ICurrentUserAccessor` that always returns the fixed `userId` it was built with. See `BudgetSkill.cs`'s
`CreateDbContext` helper for the reference implementation. `AddDbContextFactory` (mentioned in an earlier
draft of this section) is **not used** — it was tried and found to conflict with `AddDbContext` once
`FinanceDbContext` has this extra scoped constructor dependency (see §2a point 5's correction note).

---

## 4. The Four Skills

### 4.1 Budgeting → Class-based skill — **implemented and unit-tested 2026-08-10**
`src/FinanceApp.Skills/Budgeting/BudgetSkill.cs`, deriving `AgentClassSkill<BudgetSkill>`:
- `[AgentSkillScript("add_transaction")]`, `list_transactions`, `set_budget`, `check_budget_status`
  (returns per-category spent/limit/percent; flags Near ≥80%, Over >100%, computed by the shared
  `FinanceApp.Core.Repositories.BudgetRepository.GetBudgetStatusAsync`, factored out so the future Budgets
  CRUD page and this skill can't drift apart)
- `[AgentSkillResource("budgeting-policy")]` — the near/over-budget house rules
- Constructed with `IServiceScopeFactory` **and the current authenticated `Guid userId`** (from
  `ChatSessionService` via `ICurrentUserAccessor`, §2a/§3.4), registered via
  `.UseSkill(new BudgetSkill(scopeFactory, userId))` — every script method filters/writes against that
  specific user only, never a hardcoded default and never a user id taken from an LLM-provided argument;
  see §3.4's addendum for exactly how each script gets a correctly-scoped `FinanceDbContext`
  (`FixedCurrentUserAccessor`, not a DI-resolved one).
- **API details confirmed empirically** (reflection + a constructed/invoked test subclass, not just docs) —
  `AgentClassSkill<T>`'s one constructor takes `Func<JsonElement?, AIFunctionArguments>? argumentMarshaler`;
  `null` is fine, default JSON→parameter binding just works. `Frontmatter` and `Instructions` are both
  **abstract** — a subclass must override them explicitly, there's no auto-derivation from the class name.
  `[AgentSkillScript]`/`[AgentSkillResource]`-attributed members are auto-discovered via reflection at
  construction time, no manual registration. `skill.Resources` is `null` (not empty) when a skill declares
  no resources.
- **Directly unit-testable** (`tests/FinanceApp.Skills.Tests/BudgetSkillTests.cs`) — calls the
  `[AgentSkillScript]` methods as plain async methods against an EF Core InMemory-backed `FinanceDbContext`
  (not Postgres — keeps `dotnet test` Docker-free per §8), no LLM/agent involved. This is the concrete
  payoff of choosing class-based here. Includes a cross-user isolation test (two `BudgetSkill` instances,
  same in-memory "database", different `userId`s) as the concrete instance of the §2a point 7 check for
  this skill.

### 4.2 Receipt OCR → Code-defined inline skill (implemented 2026-08-12)
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

**API surface, verified via live spikes against `Microsoft.Agents.AI` 1.17.0 (spec's own "verify at
implementation time" item, now closed)**:
- `AgentInlineSkill`'s real shape: `new AgentInlineSkill(name, description, instructions, license,
  compatibility, allowedTools, metadata, serializerOptions, argumentMarshaler)`, then
  `.AddScript(name, Delegate, description, serializerOptions)` (fluent) to register callable scripts.
  **Unlike a file skill**, both the script's description and a real JSON Schema derived from the delegate's
  parameters are surfaced in `load_skill`'s `<available_scripts>` block — confirmed via a round trip
  (`{"type":"object","properties":{...},"required":[...]}`, plus the description string, both present;
  file skills only ever showed a generic array-of-strings schema and no description at all). Arguments
  arrive as a JSON **object** keyed by parameter name, not a positional array.
- Calling a script's `RunAsync` directly (bypassing the normal tool-call pipeline, as
  `ReceiptOcrSkillFactoryTests` does) returns the result **JSON-serialized as a `JsonElement`**, not the
  raw C# value the delegate returned — found because a test asserting `IsType<string>` failed and had to
  be corrected to unwrap `JsonElement.GetString()` instead.
- **Extended 2026-08-30** with a dynamic resource (`AddResource(name, Delegate, description,
  serializerOptions)` — a second `AgentInlineSkill` overload, distinct from the static `AddResource(name,
  object value, description)` form): `receipt_status`, reading the receipt's current status/extracted
  fields without re-running OCR. Confirmed via reflection against the pinned assembly: the delegate's
  parameters/return type are marshaled the same way `AddScript`'s are (a zero-arg `async Task<string> ()`
  works identically); `AgentSkill.GetResourceAsync(name, ct)` returns an `AgentSkillResource` exposing
  `ReadAsync(IServiceProvider, CancellationToken)` — the read-side counterpart to `AgentSkillScript.
  RunAsync`, minus an `arguments` parameter since a resource takes no LLM-supplied input. Payoff:
  `read_skill_resource` is never approval-gated (unlike `extract_receipt`, which is `Write`-classified), so
  a status question no longer costs an unnecessary approval round-trip.

**Dynamic mid-session skill registration** — the real design problem this skill exposed: it must be built
*after* `ChatSessionService`'s agent/session already exist (one receipt upload can happen well into an
ongoing chat), but rebuilding the agent to add it would discard the running `AgentSession`'s history.
Verified via a live spike: `AgentSkillsProviderBuilder.UseSource(...)` combined with `.DisableCaching()`
makes the framework re-invoke `AgentSkillsSource.GetSkillsAsync(...)` **on every turn**, not once at
agent-build time — a skill appended to a plain mutable `List<AgentSkill>` mid-conversation was visible to
`load_skill` on the very next turn, no agent/session rebuild needed. `ChatSessionService.RegisterReceiptSkill`
(not "…Async" — nothing in it actually awaits; the OCR work happens later, inside the script, when the
agent calls it) appends to that list; a private `DynamicInlineSkillsSource : AgentSkillsSource` reads it
live. **Accepted cost**: `.DisableCaching()` is builder-wide, so `savings-goals`/`savings-calculator`'s file
discovery loses its cache too and re-scans disk every turn instead of once — trivial at this app's scale.

**Disambiguating multiple pending receipts**: each upload gets its own skill, named
`receipt-ocr-{receiptId:N}`, with an identical description across all of them (from
`ReceiptOcrSkillFactory.Create`) — if more than one receipt is `Pending` in the same session, the model has
no way to tell them apart from the skill list alone. `ReceiptUpload.razor`'s inline extraction message
(below) names the exact skill, not just "the receipt I uploaded", to remove that ambiguity structurally
rather than relying on the model guessing right.

**Inline extraction, no `/Chat` handoff (redesigned 2026-08-17)**: "Extract with AI" originally navigated
to `/Chat?receiptId={id}` (see the now-superseded §7 paragraph history) — reworked so the whole
`load_skill`/`run_skill_script`/approval round trip runs and renders directly on `/Receipts`, driving the
same `ChatSessionService.SendAsync`/`ResumeWithApprovalAsync` stream `Chat.razor` uses (the service is
scoped per Blazor circuit, so both pages already share one `AIAgent`/`AgentSession` — no new backend
mechanism, only `ReceiptUpload.razor`'s own drain/approval-card UI, mirroring `Chat.razor`'s). Rationale:
uploading a receipt is a single-purpose task that should resolve on the page it started on, not hand off to
a general chat surface — the activity-log/approval-card visibility that motivated the original `/Chat`
handoff is preserved (both are rendered inline now), just without leaving the page.

**Extraction/category logic**: the multimodal prompt asks for `{"vendor", "amount", "date", "category"}` as
a single JSON object; parsing is deliberately tolerant (`TryParseExtraction`) — a missing/malformed field
just stays `null` rather than throwing, so a bad OCR read degrades to `ReceiptOcrStatus.Failed` with
`OcrRawResponse` preserved for debugging, not a skill-script exception. Category matching reuses
`BudgetSkill.FindCategoryAsync`'s exact case-insensitive-name shape (not a new algorithm), falling back to
`SeedData.OtherCategoryId` when the model's guessed category doesn't match any of the user's real
categories.

### 4.3 Savings/Investment Goals → File-based skill (implemented 2026-08-12)

**Two cooperating file-based skills**, not one — `skills/savings-goals/` (guidance only) and
`skills/savings-calculator/` (script-backed exact math), both serving the same feature. The table above
fixes the *skill type* per feature, not skill count; splitting this way demonstrates both halves of the
file-based mechanism (this app's whole point is showing off Agent Framework Skills end-to-end) and keeps
"give advice" and "compute an exact number" as separately-loadable concerns.

- `skills/savings-goals/SKILL.md` (YAML frontmatter: `name`, `description`, `license`, `compatibility`)
  plus `references/compound-interest.md` and `references/fifty-thirty-twenty.md` holding real guidance
  prose — editable without recompiling, the stated reason for file-based here. **No `scripts/` folder** —
  this skill is static reference content with nothing to compute.
- `skills/savings-calculator/SKILL.md` plus `scripts/project-savings.py`, a deterministic
  monthly-compounding projector — the instructions tell the agent to call the script for any exact number
  instead of estimating the math itself. **Extended 2026-08-30** with a sibling script,
  `scripts/project-debt-payoff.py` (same amortization math, run in reverse — a fixed-payment schedule
  paying a balance down to zero instead of growing one, erroring if the payment doesn't even cover
  interest), and `references/formula.md` (plain-language explanation of both scripts' math, mirroring
  `savings-goals/references/compound-interest.md`'s tone — read by the agent when a user asks *how* a
  number was derived). Both scripts share the same `SkillActionClassifier` trust bucket
  (`SkillActionKind.ExecuteScript`, gated by `AutoApproveExecuteScript`) — no new toggle.

**Correction (2026-08-12): the original "no `scripts/` folder, keeps `SubprocessScriptRunner` out of the
Docker image" rationale was wrong about why that type doesn't appear here.** `SubprocessScriptRunner` is
**not** a type shipped in any `Microsoft.Agents.AI*` NuGet package (confirmed absent via reflection over
every loaded assembly) — but it *is* real, genuine Microsoft sample source, already present in the sister
project `github.com/pingkunga/gitea-aihook`'s `feature/add_skill` branch at
`GiteaAiSummarizerNET/Services/SubprocessScriptRunner.cs`, and now ported near-verbatim into
`FinanceApp.Skills/SubprocessScriptRunner.cs` the same way `ChatClientFactory` was ported from the same
project. Its `RunAsync` signature matches `AgentFileSkillScriptRunner` exactly: picks an interpreter by
extension (`.py`→python3, `.js`→node, `.sh`→bash, `.ps1`→pwsh), shells out via `Process.Start`, captures
stdout/stderr. `savings-goals` avoids `scripts/` because it has nothing to compute, not because the runner
doesn't exist — `savings-calculator` uses it directly and accepts the resulting Python/interpreter runtime
dependency.

**Trust boundary, then later superseded by the approval-toggle work (§3.3)**: an earlier
`Microsoft.Agents.AI.Harness` 1.17.0 spike (prompted by a "should built-in skills be allowed to run scripts
while a hypothetical user-uploaded skill never should" question) found `ToolApprovalAgentOptions.
AutoApprovalRules` is a real per-call gate, but concluded at the time that anything not auto-approved would
stall the stream on a `ToolApprovalRequestContent` this app had no UI to resolve, so the policy stayed
structural (`savings-goals`'s runner always throws, `savings-calculator` gets `SubprocessScriptRunner.
RunAsync` directly — trust per registration, no shared gate). **That conclusion no longer holds**: the
approval-toggle work (2026-08-16) added exactly that missing UI (`Chat.razor`'s Approve/Reject cards, §7)
plus a real per-action-kind gate (`SkillApprovalPolicy`/`SkillActionClassifier`,
`AutoApproveWrites`/`AutoApproveExecuteScript` on `ApplicationUser`) — `savings-calculator`'s script is now
actually gated behind `AutoApproveExecuteScript` when a user turns it off, and `AgentFactory` now builds via
`AsHarnessAgent` (§3.3). The structural trust boundary (which runner a `.UseFileSkill(...)` registration
gets) still matters independently — it's what stops an approved script from running *anything other than*
its own registered interpreter — but it's no longer the *only* gate. If a future user-upload feature ever
registers many dynamic paths through one runner instead of two static ones, `AgentFileSkill.Path` (the real
on-disk directory, not attacker-controllable via an uploaded `SKILL.md`) is still the mechanism to check
against a trusted-root allowlist — not built, just the reach-for-this-when-needed note.

**Two more gotchas found only by live spikes, not documented anywhere upstream**:
1. `.UseFileSkill(path, options, scriptRunner)` throws `InvalidOperationException` at `.Build()` if
   `scriptRunner` is null — **even for a skill with no `scripts/` folder at all**, contradicting an
   official Microsoft devblog that claims omitting it is fine in that case. Every registration needs a real
   runner; `savings-goals`'s exists purely to satisfy this and always throws if actually invoked.
2. **A file skill's frontmatter `name` must exactly equal its containing folder's own name, or discovery
   silently returns zero skills** — no exception, no log, `load_skill` just can't find it. Both
   `skills/savings-goals` and `skills/savings-calculator` already follow this by naming convention, but it
   cost a debugging cycle to isolate (a temp test skill folder named e.g. `vanish-skill` with frontmatter
   `name: temp` discovered nothing, until the two were made to match).
3. A discovered script's tool-call name is its **relative path including the `scripts/` prefix and file
   extension** — e.g. `scripts/project-savings.py`, not `project-savings` — confirmed via a live round trip;
   the bare stem is rejected with "Script '…' not found in skill '…'". `load_skill`'s
   `<available_scripts>` listing shows the exact string to use; a script's description is never surfaced to
   the model this way (only its name and a generic `{"type":"array","items":{"type":"string"}}` parameters
   schema) — all argument-order/meaning documentation has to live in the skill's own prose instructions.

**Dev-machine note**: `python3` was not resolvable on the machine this was implemented on (Windows; `python`
works via a real 3.12 install, but `python3.exe` only exists as a non-functional Windows-Store
app-execution-alias stub) — `savings-calculator` is therefore untested end-to-end against a real
interpreter in this environment. `FileSkillTests`/`SubprocessScriptRunnerTests` (below) stay Docker/
python3-free by design; only a real run against a live LLM + a machine with `python3` on `PATH` (or the
container, once it has one, per the note below) exercises the actual subprocess path.

**Packaging into Docker (not yet built — no Dockerfile exists in this repo yet)**: `skills/` is added to
`FinanceApp.Web.csproj` (and both test projects that discover real skill content) as `Content` items with
**both** `CopyToOutputDirectory` and `CopyToPublishDirectory` — not `CopyToPublishDirectory` alone as this
section previously said. `AppContext.BaseDirectory` at run time is `bin/Debug/net10.0/…`, which
`CopyToPublishDirectory` never populates — found via a spike where `load_skill` silently came back "not
found" under plain `dotnet run`/`dotnet watch`, indistinguishable from the LLM simply not using the skill.
Whenever the Dockerfile is written (§6), it must install `python3` for `savings-calculator` to work in the
container — a real, accepted cost this design no longer avoids.

### 4.4 Monthly Summary & Advice → MCP-based skill (implemented 2026-08-13, HTTP transport 2026-08-14)

**This section originally assumed `.UseMcpSkills(mcpClient)` turns an MCP server's tools into agent
function calls. That assumption was wrong** — confirmed via a live spike (a real stdio MCP server + a real
`McpClient` + a scripted fake `IChatClient`, no LLM involved, deleted after). `Microsoft.Agents.AI.Mcp`'s
`.UseMcpSkills(...)` is a **skill-distribution channel**, not a tool bridge:

1. At `load_skill` discovery time, the framework fetches a well-known `skill://index.json` resource from
   the MCP server — a JSON list of `{ name, type, description, url, digest }` entries.
2. For `type: "skill-md"` entries, `load_skill("<name>")` fetches the entry's `url` **live** via
   `ReadResourceAsync` and parses it as a normal `SKILL.md` (same frontmatter shape as the file-based
   skills, §4.3).
3. `read_skill_resource("<name>", "<resourceName>")` resolves to `<skill-root>/<resourceName>` and reads
   it **live, per call** — not a prefetched/cached blob. This is what makes the skill actually useful:
   `FinanceApp.McpServer` computes real numbers from Postgres on every `read_skill_resource` call.

Spike proof (server-side request log, one call each):
```
ReadResource: skill://index.json                    ← load_skill discovery
ReadResource: skill://monthly-summary/SKILL.md       ← load_skill("monthly-summary")
ReadResource: skill://monthly-summary/summary-2026-08 ← read_skill_resource("monthly-summary","summary-2026-08")
```
And the tool-call names hitting the (fake) `IChatClient` were exactly `load_skill`/`read_skill_resource` —
the **same names `SkillActivityExtractor` already matches** (§3.4/§7/§8), so the activity-log/skill-fired
verification story needed **no changes** for this 4th source. This also makes MCP a genuinely distinct
`AgentSkillsSource` (`AgentMcpSkillsSource`, alongside `UseSkill`/`UseFileSkill`/`UseSource`) — the
alternative considered and rejected (wrap `McpClient.CallToolAsync` inside a *class-based* skill) would
have silently reused a slot already spent on `BudgetSkill`, failing the "4 different skill source types"
firm requirement even though it would have looked identical in the activity log.

**Resource design** (`FinanceApp.Skills/../MonthlySummaryResourceHandlers.cs` in `FinanceApp.McpServer`):
- `skill://index.json` — one entry: `monthly-summary`, `type: "skill-md"`.
- `skill://monthly-summary/SKILL.md` — static guidance (checked into `src/FinanceApp.McpServer/skills/
  monthly-summary/SKILL.md`, same spirit as the file-based skills' content, just served over MCP instead
  of discovered from local disk). Tells the agent to call `read_skill_resource` with `resourceName` =
  `summary-<year>-<month>` (e.g. `summary-2026-08`) — MCP resource reads don't carry free-form structured
  arguments the way tool calls do, so month/year travel encoded in the resource name itself, the same
  "spell out the exact convention" approach §4.3 already used for `savings-calculator`'s script-path
  gotcha.
- `skill://monthly-summary/summary-<year>-<month>` — computed live via
  `MonthlySummaryRepository.GetMonthlySummaryAsync` (`FinanceApp.Core/Repositories/
  MonthlySummaryRepository.cs`): total income/expense for the month, per-category totals, and
  budget-vs-actual status reusing `BudgetRepository.GetBudgetStatusAsync` (§4.1) so the two never drift
  apart. Returned as JSON.

**Package versions — deliberately pinned, not "latest"**: `Microsoft.Agents.AI.Mcp` is alpha-only
(`1.17.0-alpha.260804.1`) and its own nuspec pins `ModelContextProtocol`/`ModelContextProtocol.Core` to
**1.2.0**, not the latest stable — confirmed by mixing versions and reproducing a `TypeLoadException`
(`McpTask` didn't exist in 1.2.0-era `ModelContextProtocol.Core`, which the newer generation's
`Microsoft.Agents.AI.Mcp` build referenced). This pin now applies specifically to `FinanceApp.Web`'s MCP
**client** side. `FinanceApp.McpServer` (§5) is pinned differently — see there.

**User isolation — HTTP-migration Step 1 rewrite (2026-08-14).** The original stdio design (§4.4/§5 as
implemented 2026-08-13) spawned one `FinanceApp.McpServer` subprocess **per chat session**, with `userId`
passed via `StdioClientTransportOptions.EnvironmentVariables["FINANCEAPP_USER_ID"]` and bound to a
`FixedCurrentUserAccessor` for that process's whole lifetime — isolation came from OS process boundaries.
**That design is gone.** `FinanceApp.McpServer` is now a standing HTTP service, shared across every user's
requests — a deliberate architecture decision (Option A: unify on one HTTP transport for both the in-app
Web client and, later, external OAuth 2.1 clients — see the deferred-concern discussion this plan grew
from) with a real trade-off: isolation no longer comes for free from "which process this is." It now
depends on every request resolving its **own** scoped `FinanceDbContext`/`ICurrentUserAccessor` correctly.
The mechanism (§2a point 7's firm rule still applies — MCP resource/tool requests are LLM-driven, so
`userId` must never be a value the LLM supplies):
1. `FinanceApp.Web`'s `McpAccessTokenIssuer` mints a short-lived JWT per chat session (`sub` claim = the
   session's userId, minted as `ClaimTypes.NameIdentifier` — see the claim-mapping gotcha below), signed
   with a shared secret (`Mcp:SigningKey`, configured identically on both sides — dev-only placeholder
   value checked into both `appsettings.json`s, same posture as the AI provider API keys elsewhere in this
   project; production needs a real secret via user-secrets/env var).
2. `McpServerLauncher` attaches that token as an `Authorization: Bearer` header on every request to the
   now-HTTP `FinanceApp.McpServer` (`Mcp:BaseUrl` config).
3. `FinanceApp.McpServer`'s `AddAuthentication().AddJwtBearer(...)` validates the token per request;
   `app.MapMcp().RequireAuthorization()` rejects anything unauthenticated before it reaches a handler.
4. A new per-request `ICurrentUserAccessor` (`HttpUserContextAccessor`) reads the validated
   `ClaimsPrincipal`'s `ClaimTypes.NameIdentifier` claim, resolved fresh via `IHttpContextAccessor` on
   every call — replacing the old `FixedCurrentUserAccessor`-bound-at-startup pattern for this project.
5. `MonthlySummaryResourceHandlers` is registered **scoped** in DI (not constructed once with captured
   `dbOptions`/`userId`) — a fresh instance, with a fresh `FinanceDbContext`, resolves per HTTP request via
   `RequestContext<T>.Services` (confirmed via a spike that this exposes the request's own DI scope).

**One gotcha, found via a spike and worth calling out explicitly**: `JwtSecurityTokenHandler`'s default
inbound claim-type mapping remaps a minted `"sub"` claim to the long `ClaimTypes.NameIdentifier` XML-
namespace URI before any handler ever sees it — a plain `"sub"` lookup on the server side silently returns
nothing. Both `McpAccessTokenIssuer` (mint side) and `HttpUserContextAccessor` (read side) use
`ClaimTypes.NameIdentifier` explicitly, which also matches the exact claim type
`AuthStateCurrentUserAccessor` already uses elsewhere in this app (§2a point 3) — one consistent
convention for "where does userId live in a `ClaimsPrincipal`" across the whole codebase.

**Forward-compatible with Step 2 (not this pass) by design**: a future OAuth 2.1 layer (OpenIddict, for
external clients like ChatGPT/Claude Desktop connecting directly) only needs to change what issues and
signs the token — swapping `TokenValidationParameters.IssuerSigningKey` for an `Authority`-based
validation against OpenIddict. The `AddJwtBearer(...)` middleware itself, and everything downstream of it
(`HttpUserContextAccessor`, the scoped-DI resource handlers), doesn't need to change.

**Graceful degradation** (unchanged from the original design intent, just a different failure mode):
`McpServerLauncher.TryStartAsync` catches any connect/token-rejection failure and returns `null`;
`ChatSessionService` skips `.UseMcpSkills(...)` entirely for that session when the client is null. All four
sources combine in one place (`ChatSessionService.GetOrCreateAgentAsync`):
```csharp
var skillsBuilder = new AgentSkillsProviderBuilder()
    .UseSkill(budgetSkill)
    .UseFileSkill(savingsGoalsPath, scriptRunner: NoScriptsRunner)
    .UseFileSkill(savingsCalculatorPath, scriptRunner: SubprocessScriptRunner.RunAsync)
    .UseSource(_ => new DynamicInlineSkillsSource(_dynamicSkills))  // receipt-OCR, §4.2
    .DisableCaching();

if (_mcpClient is not null)
{
    skillsBuilder = skillsBuilder.UseMcpSkills(_mcpClient, new AgentMcpSkillsSourceOptions());
}
```
Building the agent is now async (`GetOrCreateAgentAsync`) because spawning/connecting the MCP subprocess
is async — the previous synchronous `GetOrCreateAgent()` couldn't await `McpServerLauncher.TryStartAsync`.
`ChatSessionService` also now implements `IAsyncDisposable`, disposing `_mcpClient` (which ends the
subprocess) when the owning Blazor circuit's scoped DI container is torn down.

**Dev-time launch, now standing HTTP service, not a per-session subprocess**: `FinanceApp.McpServer` must
be running on its own (`dotnet run --project src/FinanceApp.McpServer`, or the published binary) before
`FinanceApp.Web` starts — `McpServerLauncher` connects to it at `Mcp:BaseUrl`, it no longer spawns it.
This is a real dev-loop change from the stdio design (CLAUDE.md's documented commands need updating
accordingly) — not yet folded into Docker/Compose wiring (§6), which stays a separate, already-pending
piece of work by explicit choice when this HTTP migration was planned.

**Tests** (`tests/FinanceApp.McpServer.Tests`), three files:
- `MonthlySummaryResourceHandlersTests` calls the resource handlers' logic directly (via `*CoreAsync`
  methods that take plain arguments — `ModelContextProtocol.Server.RequestContext<T>`'s only constructor
  needs a real `McpServer` + `JsonRpcRequest`, too heavy for a unit test) against EF Core InMemory, no MCP
  transport. Its own "concurrent" test constructs two handler instances by hand, each with its own
  `FixedCurrentUserAccessor` — this proves the **repository query** correctly separates two users' rows,
  but that construction is structurally incapable of leaking regardless of whether real per-request DI
  scoping works, so it does **not** exercise the new risk the HTTP migration introduced. Caught during
  review — the first version of this doc claimed it did; corrected here.
- `McpServerConcurrentIsolationTests` is the one that actually does: a real `WebApplicationFactory<Program>`
  in-process host, two concurrent HTTP requests carrying two different users' bearer tokens, run through
  the **real** `HttpUserContextAccessor` + JwtBearer + scoped-DI pipeline (`Task.WhenAll`), asserting
  neither ever sees the other's numbers. Needs `Program.cs`'s `Testing:InMemoryDatabaseName` config seam
  (an EF Core InMemory branch, gated behind that key, never set outside tests — added specifically because
  a real subprocess can't share an InMemory database with the test process, and no real Postgres is
  available here) and pulls in `Microsoft.EntityFrameworkCore.InMemory` as a direct (not just test-project)
  dependency of `FinanceApp.McpServer` as a result.
- `McpServerProcessTests` spawns the **real** built `FinanceApp.McpServer.dll` (picking a free TCP port
  itself, passed via `--urls` — command-line config beats `appsettings.json` unconditionally, which an
  `ASPNETCORE_URLS` env var empirically did not in this environment) and talks to it over a real HTTP
  `McpClient` — two genuine .NET processes, needing neither Docker nor an LLM, same bar as
  `FileSkillTests`/`SubprocessScriptRunnerTests` — confirming `skill://index.json` and `SKILL.md` delivery
  actually round-trip over HTTP, plus the JwtBearer auth path itself: valid token succeeds; missing,
  wrong-signature, and expired tokens are all rejected **with a 401** specifically (asserted on
  `HttpRequestException.StatusCode`, not just "some exception was thrown" — a connection-refused or an
  unrelated bug would also satisfy a bare not-null check without proving the auth boundary itself works).

The DB-touching summary resource itself is still covered directly (`MonthlySummaryRepositoryTests` in
`tests/FinanceApp.Skills.Tests`, and `MonthlySummaryResourceHandlersTests`'s InMemory-backed cases), not
through the subprocess test, since no
real Postgres is available in this environment (same shape of ceiling as §4.3's python3 gap and §4.2's
vision-model gap).

**Pluggable multi-skill registry + 3 more skills on this server (added 2026-09-05).** `MonthlySummaryResourceHandlers`
originally hardcoded the *entire* `skill://index.json` array and `resources/read` dispatch switch for one
skill, with `Program.cs` wiring `AddMcpServer()` directly to that one class — there was no way to add a
second skill without editing that class's own index array/switch. `IMcpSkillResourceHandler` (one
`IndexEntry`/`ListableResources`/`CanHandle`/`ReadResourceAsync` per skill) plus `McpSkillRegistry` (an
aggregator, `IEnumerable<IMcpSkillResourceHandler>` injected, merges every handler's index entry and routes
`resources/read` by `CanHandle`) now sit between `Program.cs` and every skill handler — `Program.cs` wires
`AddMcpServer()` to the registry once, and adding a skill is one more `AddScoped<IMcpSkillResourceHandler, …>()`
line, not a `Program.cs`/index-array/switch edit. `MonthlySummaryResourceHandlers` implements the interface
as thin adapters over its pre-existing `*CoreAsync` methods — unchanged internally, so
`MonthlySummaryResourceHandlersTests`'s direct calls to them needed no changes at all.

Three more skills were added as the first real exercise of this registry, two via each of the schema's
other `McpSkillIndexEntry.Type` values (confirmed from `Microsoft.Agents.AI.Mcp`'s own XML doc comments,
not guessed: the schema defines exactly `skill-md`, `archive`, and `mcp-resource-template`):

- **`goals-progress`** (`skill-md`, `GoalsProgressResourceHandlers`) — fills a real gap: no skill source in
  this app could read `SavingsGoal` rows at all before this (`savings-goals`/`savings-calculator` are
  guidance/calculation-only with no DB access; `BudgetSkill.ContributeToGoalAsync` only does a by-name
  lookup for contribution, never a listing). New `SavingsGoalRepository.GetGoalsProgressAsync` (`FinanceApp.Core/
  Repositories`, same static-class/record style as `BudgetRepository`) computes `percentComplete` and a
  linear (not compound-interest) `projectedMonthsRemaining` per goal. No time dimension like
  `summary-<year>-<month>` — the one live resource is always `resourceName: current`.
- **`emergency-fund` / `debt-payoff-strategies`** (`archive`, both served by one reusable
  `ArchiveSkillResourceHandler(skillName, skillFolderPath, description)`) — the first skills in this project
  to use the `archive` distribution type: the index entry's `url` points at a zip (built once from
  `src/FinanceApp.McpServer/skills/<name>/{SKILL.md,references/*.md}` via `ZipArchive`, cached in a static
  in-process dictionary) instead of a bare `SKILL.md` resource. Per `Microsoft.Agents.AI.Mcp`'s own doc
  comment on `ArchiveEntryLoader`, scripts bundled in an archive are "surfaced as readable resources only;
  never discovered as executable scripts" — so this mechanism is only used for guidance-only, script-free
  skills (same shape as `savings-goals`'s own files), never for anything like `savings-calculator`. Content:
  general emergency-fund sizing/placement guidance, and qualitative snowball-vs-avalanche debt-payoff
  guidance (pairs with `savings-calculator`'s `project-debt-payoff.py` for the exact numbers, the same
  "guidance skill defers to the calculator skill for real math" pattern `savings-goals` already established).

**A real wire-format quirk found and worked around, not a design choice**: `BlobResourceContents.Blob`
(`ReadOnlyMemory<byte>`) does **not** auto-base64 on this pinned package version
(`ModelContextProtocol.Core 0.4.0-preview.3`) — confirmed by a raw-`curl` JSON-RPC round-trip plus
`McpServerProcessTests`. Handing it raw zip bytes writes them into the JSON string as literal (mostly
invalid-UTF8) text — the client then throws decoding it. The workaround, in `ArchiveSkillResourceHandler`:
set `Blob` to the **UTF8 bytes of an already-base64-encoded string**; any reader (this project's tests
included) must `Convert.FromBase64String(Encoding.UTF8.GetString(blob.Blob.ToArray()))` to get the real
bytes back — neither side of the wire decodes/encodes it for you. This almost certainly matches what
`Microsoft.Agents.AI.Mcp`'s own `ArchiveEntryLoader.DownloadSkillBytesAsync` ("downloads and decodes")
expects, since it's written against the same underlying client — but that specific path is **not verified
in this environment** (no live LLM/agent here, same ceiling as this section's other gaps below). Only that
`ModelContextProtocol.Client.McpClient` round-trips this exact representation correctly, over a real HTTP
subprocess, is confirmed. Revisit this workaround (and the comment explaining it, in
`ArchiveSkillResourceHandler.ReadResourceAsync`) if a future package version fixes the asymmetry — a fixed
writer would need the pre-encoding undone, not left in place.

**Tests added alongside**: `GoalsProgressResourceHandlersTests` and `SavingsGoalRepositoryTests` mirror
their `monthly-summary`/`MonthlySummaryRepository` counterparts exactly (including a cross-user isolation
case). `McpSkillRegistryTests` covers the aggregation logic itself (index merge, URI-based routing, unknown
URI) against small fake handlers, independent of any one skill's own computation. `ArchiveSkillResourceHandlerTests`
builds a real zip from a real temp directory and reads it back (`IndexEntry`/`ListableResources`/`CanHandle`
plus the base64 round-trip). `McpServerProcessTests` gained real-HTTP cases for all of the above: listing
now asserts both skill-md skills' resources are present, the index includes both archive-type entries with
`type: "archive"`, and reading `skill://emergency-fund/archive.zip` over real HTTP decodes to a valid zip
containing the real `SKILL.md`.

**Future idea, not decided/scheduled (2026-08-12), and deliberately not bundled into this pass**: this
skill is the one place in the app where
`Microsoft.Agents.AI.Harness`'s `HarnessAgent` (evaluated and *not* adopted for the file-skill trust-boundary
question, see §4.3) might actually earn its keep. "Analyze this month's spending vs. last month, flag
anomalies, suggest budget changes" is genuinely multi-step (gather via MCP → analyze → compare →
recommend) — exactly what Harness's default **Plan/Execute mode + todo tracking** exist for, unlike the
quick single-turn exchanges `Chat.razor`'s main agent handles. If this is ever tried: build it as a
**second, separate `HarnessAgent` instance** dedicated to a "generate monthly report" action (its own
button, not the shared chat input box) — don't swap the main `ChatSessionService` agent over to Harness,
since none of its other capabilities (web search, background agents, file memory, shell execution) have a
motivated use here, and `Microsoft.Agents.AI.Tools.Shell` specifically should stay unused in an app handling
people's financial data. Background-agent delegation could also let "generate my report" run without
blocking the main chat, if that's ever wanted. None of this is required for §4.4 to work — `.UseMcpSkills`
above is sufficient on its own — this is an optional enhancement idea, parked here for whenever the MCP
skill itself gets built.

---

## 5. The MCP Server Project (`FinanceApp.McpServer`) — implemented 2026-08-13, HTTP transport 2026-08-14

**HTTP-migration Step 1 (2026-08-14): `FinanceApp.McpServer` is now a standing ASP.NET Core service**
(`Microsoft.NET.Sdk.Web`, `WebApplication.CreateBuilder`), not a console app spawned fresh per chat
session. This is Step 1 of a deliberately 2-step plan (see the deferred-concern discussion this grew from):
Step 1 unifies on HTTP for both the in-app `FinanceApp.Web` client and (Step 2, not this pass) future
external OAuth 2.1 clients like ChatGPT/Claude Desktop, with lightweight JWT-bearer auth for now. The
original stdio design (below, superseded) is kept here in spirit only where the underlying resource logic
is unchanged.

References `FinanceApp.Core` (reads transactions/budgets via `MonthlySummaryRepository`/`BudgetRepository`,
§4.4), `ModelContextProtocol.AspNetCore`, and `Microsoft.AspNetCore.Authentication.JwtBearer`. Deliberately
does **not** reference the base `ModelContextProtocol` package directly — that package version is pinned to
**1.2.0** centrally (still required by `FinanceApp.Web`'s client side via `Microsoft.Agents.AI.Mcp`'s alpha
nuspec), and an explicit 1.2.0 reference here conflicts with `ModelContextProtocol.AspNetCore`'s own
dependency on a newer `ModelContextProtocol` generation (confirmed directly: NU1605 package-downgrade
error). Instead `ModelContextProtocol`/`.Core` resolve transitively through `ModelContextProtocol.
AspNetCore` at whatever version *it* needs — confirmed via a spike that a 1.2.0-generation
`HttpClientTransport` client still talks the wire protocol fine against a server hosted on this newer
generation; the two sides just can't be mixed **in the same process**, which this project structure avoids
by construction (client and server are always separate processes now). No dependency on
`Microsoft.Agents.AI.Mcp` — that package is a *client*-side skill-discovery helper (§4.4), the server side
just implements plain MCP resource handlers by hand (`skill://index.json` is hand-written JSON, not a
library-generated shape).

**The old stdout constraint is gone.** stdio is no longer the transport (HTTP is), so the "never
`Console.WriteLine`" discipline the original stdio design required (`builder.Logging.ClearProviders()`,
`Console.Error`-only logging) no longer applies — normal ASP.NET Core console logging works fine now. This
was previously called out as "the single most likely silent-failure bug in this subsystem"; it's simply
not a risk category that exists anymore under HTTP.

The server's own logic (`MonthlySummaryResourceHandlers`) stays simple by design: `ReadResourceCoreAsync`
for a `summary-<year>-<month>` resource calls `MonthlySummaryRepository.GetMonthlySummaryAsync` and
**returns structured JSON data** — the narrative summary + recommendations are generated by the *calling*
agent's LLM (per `skill://monthly-summary/SKILL.md`'s guidance), not by the MCP server. This part is
unchanged by the HTTP migration.

**Auth and user isolation — see §4.4's rewritten isolation section for the full mechanism** (JWT bearer,
`HttpUserContextAccessor`, per-request scoped `MonthlySummaryResourceHandlers`). Summary: `app.MapMcp()
.RequireAuthorization()` rejects any unauthenticated request before it reaches a handler;
`AddAuthentication().AddJwtBearer(...)` validates against a shared signing key (`Mcp:SigningKey`) for now
(Step 1), with `ValidIssuer = "FinanceApp.Web"` / `ValidAudience = "FinanceApp.McpServer"` also checked.
There is no longer a "which process is this" trust boundary — the trust boundary is "does this request
carry a token this server's configured key actually signed," per request, forever, for as long as the
service runs.

**Migration ownership**: `FinanceApp.Web` owns all migrations (`Database.MigrateAsync()` at startup, gated
by `AUTO_MIGRATE` env var, default true). `McpServer` only ever issues read queries against the same
connection string — never migrates — avoiding two processes racing on schema changes. Unchanged by the
HTTP migration; `FinanceDbContext` is now registered via `AddDbContext` (standard ASP.NET Core DI,
resolved per HTTP request) instead of manually constructed from `DbContextOptions<FinanceDbContext>` once
at startup.

**Lifecycle** (`src/FinanceApp.Web/Services/McpServerLauncher.cs`, a plain singleton service — it holds no
state of its own between calls, it's a stateless per-session factory method):
- `TryStartAsync(userId, ct)`: mints a bearer token via `McpAccessTokenIssuer.IssueToken(userId)`, builds
  an `HttpClientTransport` (`Endpoint = Mcp:BaseUrl`, `AdditionalHeaders["Authorization"] = "Bearer
  <token>"`). Any exception (connection refused, token rejected) is caught and logged as a warning; returns
  `null` in that case — same graceful-degradation shape as before, different failure mode.
- Called once per session, from `ChatSessionService.GetOrCreateAgentAsync` (not at `Web` startup) — the
  *token*'s lifetime matches the chat session's (10-minute expiry, §4.4), not the server process's, which
  now outlives every individual session.
- `ChatSessionService.DisposeAsync()` disposes the `McpClient` (just an HTTP connection now, no subprocess
  to reap) when the owning Blazor circuit's scope tears down — the server itself is unaffected.

**Superseded stdio design (2026-08-13, kept for historical context only)**: previously spawned as a
subprocess per chat session via `StdioClientTransport`/`dotnet run --project <dir>`, `userId` passed via
`StdioClientTransportOptions.EnvironmentVariables["FINANCEAPP_USER_ID"]` and bound to a
`FixedCurrentUserAccessor` for that one process's whole lifetime — isolation came from OS process
boundaries, not request-level auth. Replaced outright by the design above, not run alongside it.

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

**Render mode — per-page + per-provider, NOT global on `<Routes>` (corrected 2026-08-12; an earlier version
of this note, added 2026-08-10, recommended the opposite and was wrong — left below for the record since
the failure mode it describes is real and worth knowing).**

**What actually works**: `Components/App.razor`'s `<Routes />` has **no** `@rendermode` — no global default.
Each page that uses a popover-based MudBlazor component (`MudSelect`, `MudDatePicker`, `MudMenu`, ...)
declares its own `@rendermode InteractiveServer` as the line right after `@page` (`Transactions.razor`,
`Budgets.razor`, `Chat.razor`). `MainLayout.razor`'s three providers each carry their own explicit
rendermode instead of inheriting one:
```razor
<MudPopoverProvider @rendermode="InteractiveServer" />
<MudDialogProvider @rendermode="InteractiveServer" />
<MudSnackbarProvider @rendermode="InteractiveServer" />
```
This is the pattern a MudBlazor maintainer gives directly for this exact combination (`MudBlazor/MudBlazor`
issue #12192): *"you need to specify pages yourself that you want to be interactive, also the providers go
on individual pages rather than in the main layout."* Each provider becomes its own interactive island,
available to whichever page opts in, without requiring the whole app (including Identity's static pages) to
share one interactive boundary. `Components/Account/Pages/_Imports.razor`'s `[ExcludeFromInteractiveRouting]`
(scaffold default, §2a point 6) is kept as documentation of intent but isn't what's doing the work here —
Identity pages are static simply because nothing gives them a rendermode, same as the framework default.

**What the earlier "global on `<Routes>`" version of this note got wrong, and why it looked verified at the
time**: setting `@rendermode="InteractiveServer"` globally on `<Routes>` *does* fix the popover-provider
problem (that half of the original diagnosis was correct) — but it also breaks every static-SSR page sharing
that same `<Routes>` tree, **including the Identity Login/Register pages**, in a way that is invisible to
`curl`-based testing. Symptom (confirmed live, matches `dotnet/aspnetcore` issue #58944 exactly): the page's
*initial* response renders correctly — a plain HTTP GET/POST (curl, no JS) always sees the right content,
which is exactly what made the earlier fix look confirmed. But once a real browser's `blazor.web.js` boots
and the SignalR circuit actually connects (a couple of seconds after page load, or immediately for a
same-tab navigation), the circuit re-evaluates routing and — because these pages are `[ExcludeFromInteractiveRouting]`
but the *whole* `<Routes>` tree is now interactive by default — flips the rendered content to the app's own
`NotFound.razor` fallback. Reported by the user as "Login/Register ใช้ไม่ได้"; reproduced even in a fresh
Incognito window with a hard refresh (ruling out cache/stale-circuit causes), and confirmed via direct curl
GET/POST to the user's own running process returning correct 200/302 the whole time — proving the break was
real but specifically **not observable by any test this session had been using**.

**Standing lesson, not just about this one bug**: curl/HTTP-only testing (used throughout this project to
"verify" UI changes when no browser tool was available) cannot detect any bug whose symptom only appears
*after* a SignalR circuit connects — it can confirm the static/prerendered response is correct and nothing
more. Any future render-mode-adjacent change needs either a real browser test or an explicit caveat that the
circuit-connect behavior is unverified, not a claim of "confirmed working" based on curl alone.

`src/FinanceApp.Web/Components/Pages/`:
- **`Transactions.razor`** — plain CRUD grid over `Transaction`, direct repository calls, no agent
  involved. Proves the agent is assistive, not the only way to enter data.
- **`Budgets.razor`** — CRUD over `Budget` + per-category progress bars, computed via the *same* shared
  method `BudgetSkill.CheckBudgetStatusAsync` uses (factored onto `BudgetRepository` in `Core` so the UI
  and the skill can never drift apart).
- **`Goals.razor`** — **implemented 2026-08-12.** Plain CRUD over `SavingsGoal` (direct `Db.SavingsGoals`
  access, no repository — nothing here is shared with a skill the way `BudgetRepository` is shared with
  `BudgetSkill`), plus a per-row "Get guidance" button. `savings-goals`/`savings-calculator` are file-based
  skills with **no DB access of their own** (unlike `BudgetSkill`'s `scopeFactory`+`userId`), so the button
  navigates to `/Chat?goalId={id}` rather than passing a raw prompt string through the URL — `Chat.razor`
  loads the real goal server-side and builds the prompt from it, so `FinanceDbContext`'s existing
  `HasQueryFilter(g => g.UserId == _currentUserId)` is what actually enforces isolation (a hand-edited guid
  for someone else's goal just resolves to nothing) rather than any new logic on this page. Pre-fills
  `Chat.razor`'s input; does not auto-send (see below).
- **`ReceiptUpload.razor`** (routed `/Receipts`) — **implemented 2026-08-12, redesigned 2026-08-17 for
  inline extraction + Edit/Delete.** `InputFile` with an **explicit, generous `maxAllowedSize`** override
  (10 MB — Blazor's default `OpenReadStream` cap is 512 KB and will throw/truncate without this), image
  preview, a table of the user's past receipts with status/vendor/amount/category. On upload: saves the
  `Receipt` row, then calls `ChatSessionService.RegisterReceiptSkill(receiptId)` (§4.2) so the receipt-OCR
  skill exists before extraction is ever triggered.
  - **"Extract with AI"** — shown whenever the selected receipt's `OcrStatus` is still `Pending` and
    `AiOptions.SupportsVision` is true (not only right after upload — clicking **Edit** on an old still-
    `Pending` row surfaces it too). Runs **inline on this page** — no more navigating to `/Chat` (see §4.2's
    "Inline extraction, no `/Chat` handoff" note): drives `ChatSessionService.SendAsync`/
    `ResumeWithApprovalAsync` directly, rendering a streaming indicator, an `AgentActivityLog` panel, an
    Approve/Reject card per pending `ToolApprovalRequestContent` (same markup `Chat.razor` uses), and the
    final result text, all in place. Once the stream settles, the page re-queries the `Receipt`
    (`AsNoTracking`, since the skill script wrote it through a *different* `FinanceDbContext` instance —
    `ReceiptOcrSkillFactory.CreateDbContext` — so this page's own tracked/cached copy would otherwise be
    stale) and refreshes the table.
  - **Edit / Delete** — every row now has both. Edit reuses the (now-unconditional, previously
    no-vision-only) manual-fields form: vendor/amount/category/**date** (new field), pre-filled from the
    receipt's `Extracted*` columns; Save either updates the already-linked `Transaction` in place (status
    unchanged — a human correcting one field of an AI-`Succeeded` extraction doesn't retroactively make it
    "not AI's doing") or, if none exists yet, creates one and sets `OcrStatus = Manual` (same semantics
    the original no-vision-only manual entry had). Delete removes only the `Receipt` row; `Transaction.
    ReceiptId` is `DeleteBehavior.SetNull` (§2a), so a resulting `Transaction` is detached, not deleted —
    intentional, matches the existing schema's own intent that a receipt is source material, not the
    financial record itself. Neither action has a confirmation dialog, matching `Transactions.razor`/
    `Goals.razor`'s existing convention.
  - **Known limitation, not solved this pass**: `ChatSessionService`'s `AIAgent`/`AgentSession` is one
    instance per Blazor circuit, shared by both `/Receipts` and `/Chat`. Starting an extraction here and
    leaving its approval card unresolved, then sending a message from `/Chat`, leaves that outstanding
    tool-approval turn in an untested state — narrow edge case, same spirit as this project's other small
    accepted gaps (mid-session Profile-preference changes; MudSwitch's unverified checkbox-posting
    behavior).
- **`Chat.razor`** — **implemented 2026-08-11, extended 2026-08-12 for receipt handoff (removed
  2026-08-17 — see `ReceiptUpload.razor` above), 2026-08-13 for Markdig rendering**, the demo centerpiece:
  freeform chat wired to `ChatSessionService` (builds one `AIAgent` + one `AgentSession` per Blazor
  circuit — all 4 skill sources are wired now: `BudgetSkill` (§4.1), the
  `savings-goals`/`savings-calculator` file skills (§4.3), dynamically-registered receipt-OCR skills
  (§4.2, joinable mid-conversation even though this page itself no longer triggers one directly), and the
  MCP-based monthly-summary skill when the per-session server starts successfully (§4.4)), streams the
  response, and renders an **`AgentActivityLog`** side panel via `FinanceApp.AI.SkillActivityExtractor`.
  Assistant messages render as sanitized Markdown (`Markdig`, `.UseAdvancedExtensions().DisableHtml()` —
  the latter is load-bearing: a spike found raw HTML in a response passes through unescaped without it, a
  real XSS path since this renders LLM-generated, potentially prompt-injected text); user input stays plain
  text. Still accepts `?goalId=<guid>` from `Goals.razor`'s handoff via `[SupplyParameterFromQuery]`, read
  in `OnInitializedAsync` to pre-fill (not auto-send) the input box with a prompt built from the real goal —
  deliberately not auto-sent, since `OnInitializedAsync` can run twice on an interactive page with
  prerendering and there's no LLM available in this environment to verify an auto-sent round trip against.
  (The equivalent `?receiptId=<guid>` handoff no longer exists — receipt extraction is entirely
  `ReceiptUpload.razor`'s concern now.)

  **Markdown rendering (added 2026-08-12)**: assistant messages render through
  [Markdig](https://github.com/xoofx/markdig) (`Markdown.ToHtml(text, pipeline)` → `MarkupString`) so
  tables/code blocks/emphasis in model output actually display instead of showing raw `**`/`|` characters;
  user-typed input stays plain text. The pipeline is
  `new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build()`, built once
  (`static readonly`), and **both calls matter**, confirmed via a spike:
  `UseAdvancedExtensions()` is required for pipe tables to parse at all (Markdig's bare default pipeline
  leaves `| A | B |` as a literal paragraph, not a `<table>`) and `DisableHtml()` is a real security
  requirement, not a nice-to-have — without it, raw HTML embedded in the model's response (e.g. a
  prompt-injected `<script>...`) passes straight through into the rendered page unescaped; the spike
  confirmed a literal `<script>` tag survives to the output HTML on Markdig's default settings.

  **API correction, found via a reflection dump + a hand-rolled `IChatClient` round-trip spike against the
  real package (2026-08-11, not from spec's original guess)**: the run/streaming surface is
  `agent.CreateSessionAsync()` → `AgentSession` and `agent.RunStreamingAsync(message, session, ...)` →
  `IAsyncEnumerable<AgentResponseUpdate>` — **not** `AgentThread`/`AgentRunResponseUpdate` as originally
  written here. The activity log works by inspecting each `AgentResponseUpdate.Contents` for
  `FunctionCallContent` items named `load_skill` / `read_skill_resource` / `run_skill_script` (the three
  static `AgentSkillsProvider.LoadSkillToolName`/`ReadSkillResourceToolName`/`RunSkillScriptToolName`
  constants — values confirmed by construction, exactly those strings) — `SkillActivityExtractor.Extract`
  (`FinanceApp.AI/SkillActivity.cs`) does this, unit-tested end-to-end against a real `AgentClassSkill`
  driven through a scripted fake `IChatClient` (`tests/FinanceApp.AI.Tests/SkillActivityExtractorTests.cs`)
  — confirms both `FunctionCallContent` and its matching `FunctionResultContent` are surfaced in the stream
  (neither is `InformationalOnly`-suppressed), so the streaming design here is sound.

  **Real gap the original design missed, found by the same spike**: skill tool calls
  (`load_skill`/`read_skill_resource`/`run_skill_script`) require **approval by default** — without
  disabling it, the stream silently emits a `ToolApprovalRequestContent` and just stops (no exception; a
  chat that looks permanently "stuck," not a crash). This app has no approval UI, so
  `ChatSessionService` disables all three via
  `AgentSkillsProviderBuilder.UseOptions(o => { o.DisableLoadSkillApproval = true;
  o.DisableReadSkillResourceApproval = true; o.DisableRunSkillScriptApproval = true; })` — every script is
  already hard-scoped to the `userId` captured at agent-build time regardless (§2a point 7), so the
  approval gate would only ever have been a no-op confirmation, not a real security boundary.

  Implementation: pushes entries into a plain `List<SkillActivityEntry>` (not `ObservableCollection` — the
  list is only ever mutated from the `await foreach` loop, which already routes every mutation through
  `InvokeAsync(StateHasChanged)` since it runs off the Blazor sync context; `ObservableCollection` would add
  nothing here). Each send allocates a per-circuit `CancellationTokenSource` held by the **component**
  (not the service), cancelled on new-message-sent and on `DisposeAsync`, so a stale streaming response
  can't mutate a disposed component's state.

---

## 8. Verification

1. **Unit tests** (`FinanceApp.Skills.Tests`) — call `BudgetSkill`'s script methods directly, no LLM. The
   one skill type fully testable without any model call, by design.
2. **Discriminating end-to-end check, per skill** — send a prompt that specifically requires that skill,
   then assert on the **tool-call trace** (`FunctionCallContent` named `load_skill` with the right skill
   name, then `run_skill_script`/`read_skill_resource`) rather than eyeballing the prose — narrative output
   alone doesn't prove the skill was actually invoked (the LLM could answer from general knowledge). This
   reuses the exact mechanism built for `AgentActivityLog` in §7.
3. **Graceful-degradation check** — deliberately break the MCP connection (don't start
   `FinanceApp.McpServer`, or point `Mcp:BaseUrl` at a bad address/port) and confirm the app still starts,
   a chat session still starts, the other three skills still work, and the monthly-summary skill is simply
   absent from the trace (no crash) — `McpServerLauncher.TryStartAsync`'s catch-and-return-null path is
   exactly this.
4. **Local dev loop**: `docker compose up postgres -d`, then `dotnet run --project src/FinanceApp.McpServer`
   (standing HTTP service — must be started explicitly now, no longer auto-spawned by `Web`), then
   `dotnet run --project src/FinanceApp.Web` (auto-migrates; connects to `McpServer` over HTTP via
   `Mcp:BaseUrl`); `dotnet watch` for UI hot reload.
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
7. `FinanceApp.McpServer` runs as a standing HTTP service (Step 1 of a 2-step HTTP-migration plan, §4.4/§5)
   — internal auth only (JWT bearer, shared signing key) for now; OAuth 2.1 for external clients
   (ChatGPT/Claude Desktop) is Step 2, deliberately not started. Not yet wired into Docker Compose (§6) —
   folded into the pre-existing Docker gap rather than this pass.
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
2. ~~**Anthropic provider**~~ **RESOLVED 2026-08-11** — `Microsoft.Agents.AI.Anthropic` (preview) is
   agent-level only (`AnthropicClient.AsAIAgent(...)`, per Microsoft Learn's Anthropic-provider doc page —
   no `IChatClient` exposed), so `ChatClientFactory` uses the documented fallback, `Anthropic.SDK` 5.10.0
   (tghamm, community): `new AnthropicClient(key).Messages` implements `IChatClient` directly. See §3.1.
3. ~~**`Microsoft.Agents.AI.Mcp`** (alpha)~~ **RESOLVED 2026-08-13** — see §4.4. It is **not** a
   tool-calling bridge; it's a skill-*distribution* mechanism (`skill://index.json` + live-fetched
   `SKILL.md`/resources). `FinanceApp.McpServer` itself doesn't reference this package at all — it
   hand-writes the `skill://index.json` JSON shape directly (`MonthlySummaryResourceHandlers.
   BuildIndexJson`), confirmed correct via a live client round trip, not by matching a library type.
4. ~~**`ModelContextProtocol` vs `ModelContextProtocol.Core`**~~ **RESOLVED 2026-08-13** —
   `McpClient`/`StdioClientTransport`/`StdioClientTransportOptions` live in `ModelContextProtocol.Client`
   (in the `ModelContextProtocol.Core` assembly, referenced transitively via the `ModelContextProtocol`
   meta-package); `McpException`/`RequestContext<T>` live in `ModelContextProtocol`/
   `ModelContextProtocol.Server` respectively. Pinned to **1.2.0** on both client and server — see §4.4.
5. **`AgentSkillsProviderBuilder`** fluent method signatures — **resolved 2026-08-10/2026-08-13** via
   reflection dumps: `.UseFileSkill(skillPath, options, scriptRunner)`, `.UseFileSkills(...)`,
   `.UseSkill(AgentSkill)`, `.UseSkills(...)`, `.UseSource(...)`, `.UseFilter(Func<...>)`,
   `.UseMcpSkills(McpClient, AgentMcpSkillsSourceOptions)` (from `Microsoft.Agents.AI.Mcp`, item 3 above),
   `.Build()` all confirmed to exist with the expected shapes. `.UseSkill` takes a plain already-constructed
   `AgentSkill` instance, not a factory delegate — matches the plan (§4.2's per-request inline OCR skill is
   built via `ReceiptOcrSkillFactory.Create(...)` *before* being passed to `.UseSkill(...)`, not deferred to
   the builder).
6. Blazor `InputFile` max-size override syntax against the current .NET 10 API.
7. Confirm `AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies()` vs
   `AddIdentity<ApplicationUser, IdentityRole<Guid>>()` cookie-scheme interaction against the installed
   ASP.NET Core 10 Identity APIs before wiring `Program.cs` (§2a point 6) — the scaffolded
   `-au Individual` template's `Program.cs` is the reference to copy from, not assumed from memory.

## Critical files to create first
- ~~`src/FinanceApp.Core/Entities/ApplicationUser.cs`~~ — **done** (§2a)
- ~~`ICurrentUserAccessor`~~ — **done**: interface in `FinanceApp.Core/Abstractions/` (not Web — see §2a
  point 3's resolved ambiguity), `AuthStateCurrentUserAccessor` impl in `FinanceApp.Web/Services/`, plus
  `FixedCurrentUserAccessor` (`Core/Abstractions/`) for non-HTTP contexts (§3.4 addendum)
- ~~`src/FinanceApp.Web/Components/Account/**`~~ — **done**, copied from a `-au Individual` scaffold (§2a point 6)
- ~~`src/FinanceApp.AI/AgentFactory.cs`~~ — **done**, verified against the real package (§3.3)
- ~~`src/FinanceApp.Skills/Budgeting/BudgetSkill.cs`~~ — **done**, + `BudgetRepository.cs` (Core) + unit tests (§4.1)
- ~~`src/FinanceApp.AI/ChatClientFactory.cs`~~ — **done**, ported multi-provider factory + `AiOptions.cs`,
  unit-tested (`tests/FinanceApp.AI.Tests`), wired as `IChatClient`/`IAgentFactory` singletons in
  `Web/Program.cs` (§3.1/§3.2)
- ~~`src/FinanceApp.Web/Services/ChatSessionService.cs`~~ — **done**, + `Chat.razor`/`AgentActivityLog.razor`
  + `FinanceApp.AI/SkillActivity.cs`, unit-tested (§3.4/§7)
- ~~`src/FinanceApp.McpServer/Program.cs`~~ — **done** (§4.4/§5), **HTTP-migrated 2026-08-14** — standing
  ASP.NET Core service, JWT bearer auth, serves `skill://index.json` + live-computed monthly-summary
  resources (superseded the original stdio-per-session design)
- ~~`src/FinanceApp.Web/Services/McpServerLauncher.cs`~~ — **done** (§4.4/§5), **HTTP-migrated
  2026-08-14** — connects to the standing HTTP service with a per-session bearer token (`McpAccessTokenIssuer`)
  + graceful degradation
- ~~`src/FinanceApp.McpServer/HttpUserContextAccessor.cs`~~ — **done** (§4.4/§5) — per-request claims-based
  `ICurrentUserAccessor`, the mechanism that keeps user isolation correct now that `McpServer` is shared
- ~~`src/FinanceApp.Web/Services/McpAccessTokenIssuer.cs`~~ — **done** (§4.4/§5) — mints the per-session JWT
- ~~`src/FinanceApp.Core/FinanceDbContext.cs`~~ — **done** (§2/§2a/§5)
- ~~`src/FinanceApp.Core/Repositories/MonthlySummaryRepository.cs`~~ — **done** (§4.4)
- **All 4 skills are now fully implemented and tested; the MCP skill's HTTP-migration Step 1 is also done.**
  Remaining items: `docker-compose.yml`/`src/FinanceApp.Web/Dockerfile` (§6, including a new `mcpserver`
  compose service now that it's a standing HTTP service), and Step 2 (OAuth 2.1 for external MCP clients,
  deliberately deferred, not started).
