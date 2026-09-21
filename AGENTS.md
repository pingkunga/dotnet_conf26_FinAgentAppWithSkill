# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status

**Read `docs/spec.md` before doing any implementation work here** — it is the source of truth for the
solution layout, data model, and every design decision below. This CLAUDE.md is a condensed orientation,
not a replacement for it.

Solution is scaffolded: `.sln`, all 5 `src/` projects exist with the reference graph below wired up.
`FinanceApp.Core`'s entities + `FinanceDbContext` (now `IdentityDbContext<...>`, spec §2a) were verified
end-to-end against a local Postgres container (migration reset and reapplied, seed data confirmed via
psql). **ASP.NET Core Identity is implemented** (register/login/reset/profile, MudBlazor-styled,
tested via real POST round-trips — not just `dotnet build`) — 2FA/Passkeys/External Login were deliberately
removed (spec.md "Future improvements"), not deferred-but-broken. **`ChatClientFactory` (multi-provider
`IChatClient`, spec §3.1) and the `IChatClient`→`AIAgent` boundary (`AgentFactory`, spec §3.3) are both
implemented**, wired as `IChatClient`/`IAgentFactory` singletons in `Web/Program.cs` — construction is
universal across all 5 providers (Azure/OpenAI/Ollama/Gemini/Anthropic), no per-provider fallback needed.
**`BudgetSkill` (class-based skill, spec §4.1) is implemented and unit-tested** —
`tests/FinanceApp.Skills.Tests` (xUnit + EF Core InMemory, Docker-free) including a cross-user
isolation test; this also produced `BudgetRepository` (Core) and `FixedCurrentUserAccessor` (Core, the
pattern non-HTTP contexts need to get a correctly-scoped `FinanceDbContext` — see spec §3.4's addendum).
**`ChatSessionService` + `Chat.razor` + `AgentActivityLog.razor` are implemented** (spec §3.4/§7) — one
`AIAgent`/`AgentSession` per Blazor circuit, skill-tool-call activity surfaced via `FinanceApp.AI/SkillActivity.cs`
(`SkillActivityExtractor`, unit-tested in `tests/FinanceApp.AI.Tests` against a scripted fake `IChatClient`
driving a real `AgentClassSkill`). Finance CRUD pages `Transactions.razor`/`Budgets.razor`
are also implemented (plain repository calls, no agent involved — proves the agent is assistive, not the
only way to enter data). **The savings-goals file-based skill (spec §4.3) is implemented as two
cooperating skills** — `skills/savings-goals` (guidance only) and `skills/savings-calculator`
(script-backed exact math via a ported `SubprocessScriptRunner`), both wired into `ChatSessionService` and
covered by real end-to-end tests (`FileSkillTests`, `SubprocessScriptRunnerTests`) that discover the actual
`skills/` content rather than fakes — Docker/python3-free per the project's testing bar; the script path
itself is untested end-to-end in this environment (no working `python3` here, see spec §4.3's dev-machine
note). **`Goals.razor` is implemented** — CRUD over `SavingsGoal` plus a "Get guidance" button that hands
off to `Chat.razor` via `?goalId=<guid>` (loaded server-side and query-filtered to the current user, not a
raw prompt string through the URL — see spec §7), pre-filling but not auto-sending the chat input.
**Receipt OCR (inline skill, spec §4.2) is implemented** — `ReceiptOcrSkillFactory` builds a fresh
`AgentInlineSkill` per upload, registered into an *already-running* chat session via
`ChatSessionService.RegisterReceiptSkill` (a mutable skills source + `.DisableCaching()`, confirmed via a
spike to make the framework re-discover skills every turn instead of once — the mechanism that lets a
skill join a conversation already in progress without losing its history). `ReceiptUpload.razor` (routed
`/Receipts`) uploads, previews, and — **redesigned 2026-08-17** — runs "Extract with AI" **inline on the
page itself** (no more handing off to `Chat.razor` via `?receiptId=<guid>`; that query-param path was
removed from `Chat.razor` entirely), driving `ChatSessionService.SendAsync`/`ResumeWithApprovalAsync`
directly and rendering the streaming/approval-card/activity-log UI in place; also gained Edit/Delete on
every receipt row (Edit reuses the generalized manual-fields form — now always available, not just the
non-vision fallback — to correct any receipt's extracted fields and sync the resulting `Transaction`;
Delete removes only the `Receipt` row, relying on `Transaction.ReceiptId`'s existing `SetNull` FK so a
resulting `Transaction` is detached, not deleted). Graceful non-vision fallback to manual entry is
implemented (folded into the same generalized form). Covered by
`ReceiptOcrSkillFactoryTests` (non-vision path, category-match/fallback, malformed-response handling) —
the actual multimodal call against a real vision-capable provider is untested in this environment, same
ceiling as `savings-calculator`'s python3 gap. **The MCP-based monthly-summary skill (spec §4.4) is
implemented** — `FinanceApp.McpServer` serves `skill://index.json` + a live-computed
`summary-<year>-<month>` resource. **Transport migrated from stdio to HTTP 2026-08-14 (spec §4.4/§5, Step 1
of a 2-step plan)**: `FinanceApp.McpServer` is now a **standing ASP.NET Core service**, not a subprocess
spawned per chat session — a deliberate reversal of the original design, made so the same server can later
also serve external OAuth 2.1 clients (Step 2, not yet started) without running two different transports.
User isolation now comes from a short-lived JWT `McpAccessTokenIssuer` mints per session (`FinanceApp.Web`)
and `HttpUserContextAccessor` resolves per HTTP request (`FinanceApp.McpServer`) — never a `userId` value
the LLM supplies, per the firm user-isolation rule — replacing the old "one subprocess per user, env var at
startup" model, which no longer applies. `McpServerLauncher` attaches the token as a bearer header with
graceful degradation if the server is unreachable or the token is rejected. Confirmed via a live spike (no
LLM) that `Microsoft.Agents.AI.Mcp`'s `.UseMcpSkills(...)` is a skill-*distribution* channel, not a
tool-calling bridge — this corrected the original design (see spec §4.4's rewritten section) but the
`load_skill`/`read_skill_resource` tool-call names it surfaces are exactly what `SkillActivityExtractor`
already matches, so the activity-log verification story needed no changes. Covered by
`tests/FinanceApp.McpServer.Tests` (resource-handler logic against EF Core InMemory; a real
`WebApplicationFactory<Program>`-based concurrent multi-user isolation test through the actual
JwtBearer+DI pipeline, added because isolation is now request-scoped instead of process-scoped — this is
the one that exercises that property, not the InMemory handler test's own same-named case, which only
proves the repository query itself separates users correctly; plus a real subprocess-over-HTTP round trip
asserting a 401 specifically for missing/invalid/expired tokens — no Docker/Postgres/LLM needed for any of
it) and `MonthlySummaryRepositoryTests` (`tests/FinanceApp.Skills.Tests`).
**`FinanceApp.McpServer` now hosts 4 skills behind a pluggable registry (added 2026-09-05, spec §4.4)** —
`IMcpSkillResourceHandler` (one per skill: `IndexEntry`/`ListableResources`/`CanHandle`/`ReadResourceAsync`)
+ `McpSkillRegistry` (aggregates every registered handler into one shared `skill://index.json` and one
`resources/read` dispatch) sit between `Program.cs` and each skill, replacing the original
one-class-hardcodes-everything shape that had no way to add a second skill without editing
`MonthlySummaryResourceHandlers`'s own index array/switch. New: `goals-progress` (`skill-md`, live
`SavingsGoalRepository` data — the first skill able to read `SavingsGoal` rows at all) and two `archive`-type
skills, `emergency-fund`/`debt-payoff-strategies` (guidance-only, zipped from `SKILL.md` + `references/*.md`
via one reusable `ArchiveSkillResourceHandler`) — the first use of the Agent Skills MCP binding's `archive`
entry type in this project, which required working around a real wire-format quirk in the pinned
`ModelContextProtocol.Core 0.4.0-preview.3`: `BlobResourceContents.Blob` doesn't auto-base64 on either side
of the wire (found via a raw-curl spike, not assumed) — the server pre-encodes, any reader must decode.
Whether `Microsoft.Agents.AI.Mcp`'s own archive-downloading client-side path handles this the same way is
**not verified in this environment** (no live LLM/agent here) — only that `ModelContextProtocol.Client.McpClient`
round-trips it correctly over real HTTP, same ceiling as this project's other python3/vision-model gaps.
**All 4 original skills, plus these 3 additions, are implemented and tested — a `HarnessAgent`-based
monthly-report enhancement and Step 2 (OAuth 2.1 for external clients) are both deliberately deferred as
future work, see spec §4.4.**
Markdig (`.UseAdvancedExtensions().DisableHtml()`) renders assistant messages as sanitized markdown/tables
in `Chat.razor`; `.DisableHtml()` is load-bearing, not cosmetic — a spike found raw HTML in a response
passes through unescaped without it. **Not yet done**: Docker/Dockerfile work (once it exists, needs
`python3` installed for `savings-calculator`, per spec §4.3, and a new `mcpserver` compose service now that
it's a standing HTTP service rather than a subprocess — deliberately not folded into the HTTP-migration
pass, stays scoped to the existing Docker gap). **Not verified in this environment**: the actual chat loop
through a real LLM — no Ollama/Postgres available here, so only the framework mechanics (via unit tests
against a fake `IChatClient`, and real subprocess-over-HTTP tests for the MCP server) and `dotnet build`/
`dotnet test` are confirmed; a real browser + running Ollama is needed to confirm Chat.razor's UI end to
end, including whether the model actually picks `savings-goals` vs `savings-calculator` correctly from
their descriptions, and whether it correctly derives `summary-<year>-<month>` resource names for the
monthly-summary skill.

## Workflow rules

- **Never run `git commit` or `git push`** without the user explicitly asking for that specific action in
  that moment. Stage/leave changes for the user to review and commit themselves — this is a firm rule.
- **Every agent/skill call must be strictly scoped to the requesting user, always.** A skill invocation for
  User A must never resolve, read, or return User B/C's data, no matter how it's phrased or what the LLM
  decides to do. Enforce this structurally via `ICurrentUserAccessor`/`ChatSessionService` (spec §2a point
  7, §3.4) — never trust a user-identifier-shaped value that arrives as an LLM-provided argument. This is a
  firm rule, not a nice-to-have; see spec §8 verification item 6.

## What this project is

A personal-finance web app whose primary purpose is to demonstrate Microsoft Agent Framework's **Skills**
feature end-to-end (https://learn.microsoft.com/en-us/agent-framework/agents/skills?pivots=programming-language-csharp).
Each of its 4 finance features deliberately uses a *different* Agent Framework skill source type — this
mapping is a firm product requirement, not an implementation detail to optimize away:

| Feature | Skill type |
|---|---|
| Transactions & budgeting | Class-based (`AgentClassSkill<T>`) |
| Receipt OCR | Code-defined / inline (`AgentInlineSkill`, built per-request) |
| Savings/investment goals | File-based (`SKILL.md` + `references/`) |
| Monthly summary & advice | MCP-based (standing HTTP MCP server, JWT bearer auth — spec §5) |

Multi-LLM-provider support (Azure OpenAI / OpenAI / Ollama / Gemini / Anthropic, switched by config) is
ported from a sister project, `github.com/pingkunga/gitea-aihook`'s `ChatClientFactory` pattern.

## Commands

Not yet runnable (no code scaffolded). Once the solution exists, per `docs/spec.md` §8/§6:

```bash
# local dev inner loop
docker compose up postgres -d
dotnet run --project src/FinanceApp.McpServer  # standing HTTP service — start this FIRST, no longer auto-spawned
dotnet run --project src/FinanceApp.Web        # auto-migrates DB, connects to McpServer over HTTP
dotnet watch --project src/FinanceApp.Web      # hot reload

# tests
dotnet test                                     # FinanceApp.Skills.Tests calls BudgetSkill scripts directly, no LLM

# full stack
docker compose up --build

# EF Core migrations (FinanceApp.Web is the design-time startup project, not Core)
dotnet ef migrations add <Name> --project src/FinanceApp.Core --startup-project src/FinanceApp.Web
```

## Architecture

**Solution layout** (5 projects, see spec §1 for the full tree):
- `FinanceApp.Core` — domain entities (including `ApplicationUser : IdentityUser<Guid>`, spec §2a) + EF
  Core `DbContext` (`IdentityDbContext<...>`) + repositories. Shared by `Web` and `McpServer`. Nothing
  references back up into it.
- `FinanceApp.AI` — the multi-provider `ChatClientFactory` (→ `IChatClient`) and `AgentFactory`
  (→ `AIAgent`). Deliberately isolated from ASP.NET Core so `Skills` can depend on it too.
- `FinanceApp.Skills` — `BudgetSkill` (class-based) and `ReceiptOcrSkillFactory` (inline). No ASP.NET
  dependency, so `BudgetSkill`'s `[AgentSkillScript]` methods are unit-testable as plain async calls.
- `FinanceApp.McpServer` — standing ASP.NET Core service (HTTP transport, spec §5, Step 1 of a 2-step
  HTTP-migration), JWT-bearer-authenticated, hosting the monthly-summary skill.
- `FinanceApp.Web` — Blazor Server host (Interactive Server render mode). Owns all EF migrations.

**The `IChatClient`→`AIAgent` construction, resolved**: `IChatClient.AsAIAgent(ChatClientAgentOptions, ...)`
(`Microsoft.Extensions.AI.ChatClientExtensions`) is a universal extension method — works identically for
Ollama/Gemini/Anthropic as for Azure's Responses-capable client, confirmed by an actual spike (not just
docs-reading). No per-provider "chat unavailable" fallback is needed at the `AgentFactory` layer; see spec
§3.3 for the verified details, including the one API surprise (`ChatClientAgentOptions.ChatOptions.Instructions`,
not a top-level `Instructions` property).

**DI lifetimes matter here**: `IChatClient` is a singleton; `McpClient` is now built **per chat session**
(an HTTP connection with that session's own bearer token, not a singleton — spec §5); `AIAgent` is built
**per conversation** by a scoped `ChatSessionService`, because `BudgetSkill`/`ReceiptOcrSkillFactory`
close over DB-backed services via an injected `IServiceScopeFactory` (not a captured live scope — a
skill script can fire after the originating Blazor circuit's scope is gone). See spec §3.4 — **and its
addendum**: a script must never resolve `FinanceDbContext` directly from that scope (the real
`ICurrentUserAccessor` has nothing to derive a user from outside HTTP and silently zeroes every query
instead). Resolve only `DbContextOptions<FinanceDbContext>` and construct the context manually with a
`FixedCurrentUserAccessor` bound to the skill's captured `userId` — see `BudgetSkill.CreateDbContext` for
the reference implementation. `FinanceApp.McpServer` no longer needs this workaround (spec §5) — it's a
real ASP.NET Core app now, so `FinanceDbContext` is registered via plain `AddDbContext` and resolved
through the normal per-HTTP-request DI scope, with a claims-based `ICurrentUserAccessor`
(`HttpUserContextAccessor`) instead of a captured `FixedCurrentUserAccessor`.

**MCP server transport**: HTTP now, not stdio (spec §5, migrated 2026-08-14) — the old "never write to
`Console.Out`, stdout is the JSON-RPC transport" constraint no longer applies; normal console logging is
fine. What *does* matter now: `FinanceApp.McpServer` is a **standing service shared across every user**,
not a subprocess with one user baked in at startup — every request must resolve its own user from a
validated JWT bearer token (`AddAuthentication().AddJwtBearer(...)`, `RequireAuthorization()` on the MCP
endpoint), never from a request argument. See spec §5 and §4.4's user-isolation rewrite.

**Migration ownership**: only `FinanceApp.Web` runs `Database.MigrateAsync()` (gated by `AUTO_MIGRATE` env
var). `FinanceApp.McpServer` is read-only against the same Postgres database — never migrates.

**Skill combination point**: all four skill sources are merged into one `AgentSkillsProvider` via
`AgentSkillsProviderBuilder` — see spec §4.4 for the exact composition, including the `.UseMcpSkills(...)`
being conditionally omitted if the local MCP server failed to start (graceful degradation).

**Verifying a skill actually fired**: don't judge by the agent's prose — inspect the streamed
`AgentRunResponseUpdate` sequence for `FunctionCallContent` named `load_skill` / `read_skill_resource` /
`run_skill_script`. This is both the mechanism behind the `Chat.razor` activity-log UI and the required
method for asserting skill invocation in tests. See spec §7/§8.

## Open items to resolve during implementation

All previously-open package-API questions are now resolved: `AgentFactory.cs` (verified 2026-08-10),
`Microsoft.Agents.AI.Anthropic` (resolved — it's agent-level only, so `ChatClientFactory` uses
`Anthropic.SDK` 5.10.0 instead, see spec §3.1), and `Microsoft.Agents.AI.Mcp`/`ModelContextProtocol`
(resolved 2026-08-13 — see spec §4.4/§4.4's "Verify at implementation time" entries). Nothing currently
open; only remaining work is Docker/Dockerfile (spec §6) and a possible future `HarnessAgent`-based
monthly-report enhancement (spec §4.4's future-idea note, deliberately deferred).
