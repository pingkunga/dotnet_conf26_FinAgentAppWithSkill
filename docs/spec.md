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
│   ├── savings-goals/                 # guidance only, no scripts/ (§4.3)
│   │   ├── SKILL.md
│   │   └── references/{compound-interest.md, fifty-thirty-twenty.md}
│   └── savings-calculator/            # script-backed exact math (§4.3)
│       ├── SKILL.md
│       └── scripts/project-savings.py
├── src/
│   ├── FinanceApp.Core/               # domain entities + EF Core DbContext + repositories (shared by Web & McpServer)
│   ├── FinanceApp.AI/                 # ChatClientFactory + AgentFactory (isolated LLM-provider risk boundary)
│   ├── FinanceApp.Skills/             # BudgetSkill (class-based) + ReceiptOcrSkillFactory (inline) — testable, no ASP.NET dep
│   ├── FinanceApp.McpServer/          # stdio MCP server hosting the monthly-summary skill
│   └── FinanceApp.Web/                # Blazor Server host: Program.cs, Components/Pages/*, Services/*
├── docs/
│   └── spec.md                        # this file
├── tests/
│   ├── FinanceApp.Skills.Tests/       # xUnit — BudgetSkill scripts + SubprocessScriptRunner, no LLM/python3
│   └── FinanceApp.AI.Tests/           # xUnit — ChatClientFactory, SkillActivityExtractor, file-skill round trips
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
pattern for any skill (and `FinanceApp.McpServer`, §5, which has the identical problem): resolve only
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
no way to tell them apart from the skill list alone. `Chat.razor`'s pre-filled prompt (below) names the
exact skill, not just "the receipt I uploaded", to remove that ambiguity structurally rather than relying
on the model guessing right.

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
  instead of estimating the math itself.

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

**Trust boundary, not Harness**: a `Microsoft.Agents.AI.Harness` 1.17.0 spike (reflection + live checks,
prompted by a "should built-in skills be allowed to run scripts while a hypothetical user-uploaded skill
never should" question) confirmed `HarnessAgent`'s `ToolApprovalAgentOptions.AutoApprovalRules` is a real
per-call gate, but only a gate — it decides whether to prompt, not what a script can touch, and anything
not auto-approved stalls the stream on a `ToolApprovalRequestContent` this app has no UI to resolve (the
same stall found building `Chat.razor`, §7 below). The policy doesn't need Harness: each
`.UseFileSkill(...)` registration in `ChatSessionService` already takes its own runner delegate, so
`savings-goals` gets one that always throws and `savings-calculator` gets `SubprocessScriptRunner.RunAsync`
directly — trust is structural per registration, no shared gate required. If a future user-upload feature
ever registers many dynamic paths through one runner instead of two static ones, `AgentFileSkill.Path` (the
real on-disk directory, not attacker-controllable via an uploaded `SKILL.md`) is the mechanism to check
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

**Future idea, not decided/scheduled (2026-08-12)**: this skill is the one place in the app where
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
- **`ReceiptUpload.razor`** (routed `/Receipts`) — **implemented 2026-08-12.** `InputFile` with an
  **explicit, generous `maxAllowedSize`** override (10 MB — Blazor's default `OpenReadStream` cap is
  512 KB and will throw/truncate without this), image preview, a table of the user's past receipts with
  status. On upload: saves the `Receipt` row, then calls
  `ChatSessionService.RegisterReceiptSkill(receiptId)` (§4.2) so the receipt-OCR skill exists before the
  user ever navigates to `Chat.razor`. "Extract with AI" button — disabled with explanatory text when
  `AiOptions.SupportsVision` is false — navigates to `/Chat?receiptId={id}` (same handoff shape as
  `Goals.razor`'s `?goalId=`). When vision isn't supported, manual entry fields (vendor/amount/category)
  are shown instead, submitted as a plain `Transaction` write with `Source = Manual` and
  `Receipt.OcrStatus = Unsupported` — no agent involved, matching `Transactions.razor`'s precedent.
- **`Chat.razor`** — **implemented 2026-08-11, extended 2026-08-12 for receipt handoff**, the demo
  centerpiece: freeform chat wired to `ChatSessionService` (builds one `AIAgent` + one `AgentSession` per
  Blazor circuit — `BudgetSkill`, the `savings-goals`/`savings-calculator` file skills, and dynamically-
  registered receipt-OCR skills are all wired now, §4.1/§4.3/§4.2; only the MCP monthly-summary skill still
  doesn't exist, see §4.4), streams the response, and renders an **`AgentActivityLog`** side panel via
  `FinanceApp.AI.SkillActivityExtractor`. Also accepts `?goalId=<guid>` from `Goals.razor`'s handoff and
  `?receiptId=<guid>` from `ReceiptUpload.razor`'s (above) via `[SupplyParameterFromQuery]`, read in
  `OnInitializedAsync` to pre-fill (not auto-send) the input box with a prompt built from the real
  goal/receipt — deliberately not auto-sent, since `OnInitializedAsync` can run twice on an interactive page
  with prerendering and there's no LLM available in this environment to verify an auto-sent round trip
  against. The receipt prompt names the exact dynamically-registered skill (`receipt-ocr-{id:N}`), not just
  "the receipt I uploaded" — necessary because every such skill shares the same description, so if more
  than one receipt is pending in the same session only the exact name disambiguates which one to load.

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
2. ~~**Anthropic provider**~~ **RESOLVED 2026-08-11** — `Microsoft.Agents.AI.Anthropic` (preview) is
   agent-level only (`AnthropicClient.AsAIAgent(...)`, per Microsoft Learn's Anthropic-provider doc page —
   no `IChatClient` exposed), so `ChatClientFactory` uses the documented fallback, `Anthropic.SDK` 5.10.0
   (tghamm, community): `new AnthropicClient(key).Messages` implements `IChatClient` directly. See §3.1.
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
  + `FinanceApp.AI/SkillActivity.cs`, unit-tested (§3.4/§7) — **next up**: the other 3 skills, MCP server
- `src/FinanceApp.McpServer/Program.cs` — stdio MCP server; must never write to stdout (§5)
- `src/FinanceApp.Web/Services/McpServerLauncher.cs` — subprocess lifecycle + graceful degradation (§5)
- ~~`src/FinanceApp.Core/FinanceDbContext.cs`~~ — **done** (§2/§2a/§5)
- `docker-compose.yml`, `src/FinanceApp.Web/Dockerfile` — multi-stage build bundling both apps (§6)
