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

**Single-user v1**: no auth/login system — out of scope for this app's purpose. Entities carry a `UserId`
(Guid) column against one seeded default user, so multi-user is a small future diff rather than a rewrite.

---

## 2. Data Model (EF Core + PostgreSQL)

`src/FinanceApp.Core/Entities/`:
- **User** — `Id, DisplayName, Email, CreatedAtUtc` (single seeded row for v1)
- **Category** — `Id, UserId, Name, Kind(Expense/Income), IsSystemDefault` — seeded defaults (Groceries, Dining, Transport, Utilities, Entertainment, Income, Other)
- **Transaction** — `Id, UserId, CategoryId?, Amount(decimal 18,2), Currency, OccurredOn, Description, Source(Manual/Agent/ReceiptOcr), ReceiptId?, CreatedAtUtc`
- **Budget** — `Id, UserId, CategoryId, PeriodMonth, LimitAmount` — unique index `(UserId, CategoryId, PeriodMonth)`
- **SavingsGoal** — `Id, UserId, Name, TargetAmount, CurrentAmount, TargetDate?, MonthlyContribution?, CreatedAtUtc`
- **Receipt** — `Id, UserId, ImageBytes(bytea), ContentType, UploadedAtUtc, OcrStatus(Pending/Succeeded/Failed/Unsupported), OcrRawResponse?, ExtractedVendor/Amount/Date/CategoryId, ResultingTransactionId?`

Design choices: receipt images stored as `bytea` directly in Postgres (no object storage needed at this
scale); **monthly summaries are computed on the fly, not persisted** (cheap aggregation, avoids a
cache-invalidation problem for no benefit yet). `FinanceDbContext` uses `HasConversion<string>()` for
enums (readable in psql) and a defensive `HasQueryFilter` scoped to the seeded user id.

Migrations live in `FinanceApp.Core/Migrations/`, generated with `FinanceApp.Web` as the EF Core
design-time startup project (`Microsoft.EntityFrameworkCore.Design` referenced by `Web`, not `Core`, so
that design-time dependency doesn't leak into `McpServer`).

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

### 3.3 `IChatClient` → `AIAgent` — the one genuinely open question, quarantined to one file
The doc's example wraps a Responses-capable Azure client (`.GetResponsesClient().AsAIAgent(...)`), but this
app must also support Ollama/Gemini/Anthropic, which aren't Responses clients. `src/FinanceApp.AI/IAgentFactory.cs`
is declared as **the single seam** allowed to construct an `AIAgent`:
```csharp
public interface IAgentFactory {
    AIAgent CreateAgent(AgentSkillsProvider skillsProvider, string? instructions = null);
}
```
At implementation time, check the installed `Microsoft.Agents.AI` 1.17.0 API for a generic
`IChatClient`-based construction path (a `ChatClientAgent(IChatClient, ChatClientAgentOptions)` type is the
likely candidate, given `ChatClientAgentOptions` is already named in the docs). If no generic path exists
for a given provider, degrade gracefully — log a startup warning and mark that provider's chat as
unavailable (CRUD pages keep working; `Chat.razor` shows "agent unavailable for this provider") rather than
crashing. Nothing outside `AgentFactory.cs` needs to change if the construction approach changes.

### 3.4 Scoped vs. singleton
- **Singleton**: `IChatClient`, `AgentFileSkillsSource` (just reads files), the MCP `McpClient` connection.
- **`AIAgent` built per conversation**, not as a singleton — via a **scoped** `ChatSessionService`
  (`Web/Services/ChatSessionService.cs`), because `BudgetSkill` and the inline OCR skill close over
  DB-backed services.
- `BudgetSkill` and `ReceiptOcrSkillFactory` are given an injected `IServiceScopeFactory` (not a live scope)
  and open `using var scope = _scopeFactory.CreateScope();` **inside each script invocation** — a script can
  fire after the original Blazor circuit's DI scope is gone, so capturing a live scope risks
  `ObjectDisposedException` on the DbContext.

---

## 4. The Four Skills

### 4.1 Budgeting → Class-based skill
`src/FinanceApp.Skills/Budgeting/BudgetSkill.cs`, deriving `AgentClassSkill<BudgetSkill>`:
- `[AgentSkillScript("add_transaction")]`, `list_transactions`, `set_budget`, `check_budget_status`
  (returns per-category spent/limit/percent; flags Near ≥80%, Over >100%)
- `[AgentSkillResource("budgeting-policy")]` — the near/over-budget house rules
- Constructed with `IServiceScopeFactory`, registered via `.UseSkill(new BudgetSkill(scopeFactory))`
- **Directly unit-testable** (`tests/FinanceApp.Skills.Tests/BudgetSkillTests.cs`) — call the
  `[AgentSkillScript]` methods as plain async methods against a seeded `FinanceDbContext`, no LLM/agent
  involved. This is the concrete payoff of choosing class-based here.

### 4.2 Receipt OCR → Code-defined inline skill
`src/FinanceApp.Skills/ReceiptOcr/ReceiptOcrSkillFactory.cs` — **not** a static instance; built per
request/upload so it can close over the specific `Receipt` and the current DI-scoped `IChatClient`:
```csharp
AgentInlineSkill Create(IChatClient chatClient, IServiceScopeFactory scopeFactory, Guid receiptId, bool supportsVision)
```
OCR is done via a **multimodal call on the same `IChatClient`** already configured for the app (avoids a
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

---

## Assumptions
1. Single-user v1, no auth — `UserId` seam present for future multi-user support.
2. Receipt images stored as Postgres `bytea`, not external object storage.
3. Monthly summaries computed on the fly, never persisted.
4. Local dev defaults to Ollama (no API key needed); `SupportsVision` defaults false for Ollama, true for
   the four cloud providers.
5. Savings-goals file skill ships with no `scripts/` folder, specifically to keep Python/pwsh out of the
   Docker image — revisit the Dockerfile if that ever changes.
6. Migrations owned exclusively by `Web`; `McpServer` is read-only against the same database.
7. Ollama itself isn't containerized by default in Compose (assumed to run on the host).

## Verify at implementation time (isolated uncertainty, preview/alpha package surfaces)
1. **`IChatClient` → `AIAgent` construction** (§3.3) — exact type/extension method on `Microsoft.Agents.AI`
   1.17.0; contained entirely to `FinanceApp.AI/AgentFactory.cs`.
2. **Anthropic provider** — whether `Microsoft.Agents.AI.Anthropic` (preview) exposes a plain `IChatClient`
   factory, or is actually agent-level and needs a documented exception in `ChatClientFactory`'s otherwise
   uniform shape; `Anthropic.SDK` 5.10.0 is the fallback.
3. **`Microsoft.Agents.AI.Mcp`** (alpha) — exact API for building `skill://index.json` server-side content
   in `FinanceApp.McpServer`, and the experimental-usage diagnostic ID to suppress.
4. **`ModelContextProtocol` vs `ModelContextProtocol.Core`** — confirm which package/namespace
   `McpClient`/`StdioClientTransport` actually live in.
5. **`AgentSkillsProviderBuilder`** fluent method signatures (`.UseFileSkill`, `.UseSkill`, `.UseMcpSkills`,
   `.UseFilter`) — confirm exact overloads (e.g. does `.UseSkill` accept a factory delegate, needed for the
   per-request inline OCR skill in §4.2) against the installed package before writing `AgentFactory`.
6. Blazor `InputFile` max-size override syntax against the current .NET 10 API.

## Critical files to create first
- `src/FinanceApp.AI/AgentFactory.cs` — the isolated `IChatClient`→`AIAgent` risk boundary (§3.3)
- `src/FinanceApp.AI/ChatClientFactory.cs` — ported multi-provider factory (§3.1)
- `src/FinanceApp.Skills/Budgeting/BudgetSkill.cs` — class-based skill + its unit tests (§4.1)
- `src/FinanceApp.McpServer/Program.cs` — stdio MCP server; must never write to stdout (§5)
- `src/FinanceApp.Web/Services/McpServerLauncher.cs` — subprocess lifecycle + graceful degradation (§5)
- `src/FinanceApp.Core/FinanceDbContext.cs` — schema shared by `Web` and `McpServer` (§2/§5)
- `docker-compose.yml`, `src/FinanceApp.Web/Dockerfile` — multi-stage build bundling both apps (§6)
