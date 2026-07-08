# Diva AI — Claude Code Instructions

## Session Context

**Before making any changes, read [`docs/agents.md`](docs/agents.md).**
It is the single shared dev cycle guide — current platform state, key files, ReAct loop patterns,
SSE event master list, shared state map, context window injection sites, Anthropic SDK gotchas,
and all active deferred items. Both Claude Code and GitHub Copilot are pointed at this file.

Also check [`docs/changelog.md`](docs/changelog.md) for completed features and the pending/deferred item list.

---

## Project Overview

Multi-tenant enterprise AI agent platform built on .NET 10 + Semantic Kernel.
Agents are tenant-aware, business-rule-driven, and dynamically configurable via admin UI.

---

## Quick Commands

### .NET API

```bash
# Build entire solution
dotnet build

# Run API host (dev)
dotnet run --project src/Diva.Host

# Run all tests
dotnet test

# Run specific test project
dotnet test tests/Diva.Agents.Tests
dotnet test tests/Diva.TenantAdmin.Tests
dotnet test tests/Diva.Tools.Tests

# EF migrations (run from solution root)
dotnet ef migrations add <Name> --project src/Diva.Infrastructure --startup-project src/Diva.Host -- --provider SQLite
dotnet ef database update --project src/Diva.Infrastructure --startup-project src/Diva.Host -- --provider SQLite
```

### Admin Portal (React)

```bash
cd admin-portal
npm install
npm run dev        # starts on http://localhost:5173
npm run build
npm run lint

# Run with MSW sandbox (no real API needed)
VITE_MOCK=true npm run dev
```

### Docker

```bash
docker compose up -d                                              # SQLite (dev)
docker compose -f docker-compose.yml -f docker-compose.enterprise.yml up -d  # Enterprise
```

---

## Current Status

Check [docs/INDEX.md](docs/INDEX.md) for full phase status.
Update status in INDEX.md when moving a phase from `[ ]` → `[~]` → `[x]`.

**All three tiers and Phases 1–18 + Phase 22 are complete.** Auth/SSO login flow and multi-tenant isolation are now active (2026-03-25). Two items remain deferred:

### Remaining Work
- **Phase 19** (foundation complete [2026-05-06]): Coordinator Sub-Agent Routing — foundation (7-gap SOLID refactor, `LlmDecompositionStrategy`, `AgentContextStage`, `IReadableAgentRegistry`, `ICapabilityScoringService`, `ResponseSynthesizer`, `IToolSelectionStrategy`/`LlmToolSelector`) is complete. **Remaining work is enhancement-only** [2026-05-10]: Phase 14 Agents-as-Tools (`DelegateAgentIdsJson`) already covers most coordinator use cases via UI-configurable peer delegation. The remaining pieces (`OrchestratorAgent`, `ScopedAgentRegistry`, `SubAgentIdsJson` DB field, `SubAgentSelector.tsx`) add hard agent isolation + true pipeline-level parallelism — implement when 5+ sub-agents or auditable decomposition is required. See [docs/phase-19-coordinator-sub-agent-routing.md](docs/phase-19-coordinator-sub-agent-routing.md).
- **Phase 5** (deferred): `AnalyticsMcpServer`, `ReservationMcpServer` — domain MCP tool servers. Deferred until real data backends are available.

### What's Complete
- **All foundation phases** (1–4, 7, 8) ✓
- **Phase 13** — Response Verification (Off/ToolGrounded/LlmVerifier/Strict/Auto), VerifyStage, correction retry, per-agent override, UI badges ✓
- **Phase 14** — A2A Protocol: AgentCard endpoint, task lifecycle, A2AAgentClient, DispatchStage routing, hardening (resilience, rate limiting, cleanup), agents-as-tools peer delegation (`AgentDelegationTool`, `AgentToolProvider`/`AgentToolExecutor`, `DelegateAgentSelector` UI), 49 tests ✓
- **Phase 15** — Custom Pluggable Agents: archetypes, lifecycle hooks, AgentHookPipeline, BaseCustomAgent, A2A integration, 6 built-in hooks ✓
- **Phase 16** — Rule Packs: DB-driven configurable hook rule bundles (9 types), RulePackService + Engine + ConflictAnalyzer, TenantRulePackHook, 15-endpoint REST API, RulePackManager + PackEditor UI, 54 tests ✓
- **Phase 22** — Embeddable Chat Widget: `WidgetConfigEntity` + migration, `WidgetTheme` (Light/Dark presets, 18 tokens), `WidgetConfigService` CRUD, `IssueWidgetAnonymousJwt`, `WidgetController` (init/auth/session), widget admin CRUD, `Widget` CORS policy, `widget.js` vanilla embed script, widget SPA (11.5 kB, SSE streaming, postMessage SSO, system dark mode), `WidgetManager` + `WidgetEditor` admin UI with live theme preview, 10 tests ✓
- **Tier 1** — Rule learning, session-scoped rules, feedback learning, PendingRules UI ✓
- **Tier 2** — Tenant business rules, prompt builder, AdminController CRUD, BusinessRules/PromptEditor/Dashboard UI ✓
- **Tier 3** — SSE iteration streaming (Anthropic + OpenAI-compatible), live tool call trace in UI, SignalR hub, Serilog, OTel, Prometheus, Dockerfile, docker-compose, Diva.slnx ✓
- **MCP multi-server** — all agent tool bindings connected in parallel, tool-to-client routing map, Docker MCP Gateway panel in AgentBuilder, `/api/agents/mcp-probe` endpoint ✓
- **Context Window Management** — Point A + Point B compaction, LLM-based summarisation, per-agent override ✓
- **Continuation Windows** — outer loop wraps unified `ExecuteReActLoopAsync`, `MaxContinuations` config (global + per-agent), `continuation_start` SSE event ✓
- **Tool Error Retry** — `hadToolErrors` flag, acknowledgment loop fix, JSON error detection ✓
- **MCP Credential Vault + Platform API Keys** — AES-256-GCM encrypted credential store, `CredentialRef` per MCP binding, 3-tier auth (SSO → credential → tenant headers), platform API keys (`diva_` prefix, SHA-256 hashed), `X-API-Key` middleware, scheduled task fallback tenant flow, admin UI (`/settings/credentials`, `/settings/api-keys`), structured MCP call logging ✓
- **Phase 19 Foundation** — 7-gap SOLID refactor: `AgentContextStage`, `IReadableAgentRegistry`, `ICapabilityScoringService`, `LlmDecompositionStrategy` + `SingleTaskStrategy` + `DecompositionStrategySelector`, `ResponseSynthesizer`, `SupervisorLlmOverride`, `VerifyStage` multi-agent fix ✓; `IToolSelectionStrategy` / `LlmToolSelector` semantic tool pre-filter (single-agent+tools optimization, multi-agent compatible) ✓

**Note on Phase 9:** `LlmClientFactory`/`LiteLLMClient` were never built — design shifted to `AnthropicAgentRunner` (direct provider). LiteLLM is supported by pointing `DirectProvider` at the LiteLLM proxy URL. This is not a gap.

---

## Project Structure

```
src/
  Diva.Core/          → Models, DTOs, config interfaces (no dependencies)
  Diva.Infrastructure/ → DB, Auth, LiteLLM, Sessions, Learning
  Diva.Agents/        → SK ChatCompletionAgent wrappers, Supervisor pipeline, Registry
  Diva.Tools/         → MCP tool infrastructure + domain tool servers
  Diva.TenantAdmin/   → Business rules service, TenantAwarePromptBuilder
  Diva.Host/          → ASP.NET Core 10 entry point, controllers, SignalR hub
admin-portal/         → React + Vite + TypeScript
docs/                 → Phase docs, arch docs, this file
tests/                → One test project per src project
```

**Always follow the dependency order:** Core → Infrastructure → Tools → TenantAdmin → Agents → Host

---

## Hard Rules

- **Never modify** `IMPLEMENTATION_PLAN.md` — it is the archived source. All active work is in `docs/`.
- **Never mock the database** in integration tests — use real SQLite (in-memory or temp file). See [docs/testing.md](docs/testing.md).
- **Never commit secrets** — use `.env` (gitignored). See `.env.example` for all vars.
- **Always suppress SK experimental warnings** with `#pragma warning disable SKEXP0110` at file level in `Diva.Agents/`.
- **TenantContext must be injected** — never construct it manually in business logic; always flow from `TenantContextMiddleware`.
- **Every DB entity must implement `ITenantEntity`** — EF query filters depend on this.
- **Cache invalidation after writes** — call `InvalidateCacheAsync` after any business rule or prompt override update.
- **Agent system prompts are stored in the database** (set via Agent Builder). `TenantAwarePromptBuilder` augments them at runtime with business rules, session rules, and prompt overrides — no file-based templates.
- **`Credentials:MasterKey` must be stable in production** — AES-256-GCM key for encrypting MCP credential secrets. If empty, an ephemeral key is generated per startup (dev only). Credentials encrypted with an ephemeral key are unrecoverable after restart. Set a base64-encoded 32-byte key.
- **Re-save credentials after changing `MasterKey`** — existing encrypted values become undecryptable if the key changes. Use `PUT /api/admin/credentials/{id}` with `newApiKey` to re-encrypt.
- **`X-API-Key` is checked before Bearer JWT** in `TenantContextMiddleware` — platform API keys take precedence over JWT when both are present.

---

## Key Architectural Decisions

Full log in [docs/decisions.md](docs/decisions.md). Short version:

| Decision | Choice | Doc |
|----------|--------|-----|
| Agent framework | Semantic Kernel 1.74.0 — reaffirmed 2026-03-23 (ADR-014) | [arch-overview.md](docs/arch-overview.md) |
| TenantContext | Rich model (all claims + headers) | [arch-multi-tenant.md](docs/arch-multi-tenant.md) |
| Supervisor | Custom `ISupervisorPipelineStage` pipeline + SK `AgentGroupChat` for dispatch | [arch-supervisor.md](docs/arch-supervisor.md) |
| Database default | SQLite (EF query filters), opt-in SQL Server (RLS) | [phase-04-database.md](docs/phase-04-database.md) |
| LLM routing | Direct provider default, LiteLLM opt-in via `UseLiteLLM: true` | [phase-09-llm-client.md](docs/phase-09-llm-client.md) |
| Anthropic provider | No official SK Anthropic connector exists — use `IAnthropicProvider` / `AnthropicProvider` wrappers (ADR-015) | [decisions.md](docs/decisions.md) |
| MCP client | `ModelContextProtocol` 1.1.0 SDK + custom `McpClientCache` — SK has no built-in MCP client package | [decisions.md](docs/decisions.md) |
| Rule learning approval | Three modes: AutoApprove / RequireAdmin / SessionOnly | [phase-11-rule-learning.md](docs/phase-11-rule-learning.md) |
| SSE streaming | `InvokeStreamAsync` yields `AgentStreamChunk` per ReAct event; unified `ExecuteReActLoopAsync` via `ILlmProviderStrategy` for both providers (no `UseFunctionInvocation` in any path) | [phase-09-llm-client.md](docs/phase-09-llm-client.md) |
| Streaming UI sandbox | MSW (`VITE_MOCK=true`) intercepts `/invoke/stream` and returns a `ReadableStream` with per-chunk delays | [phase-12-admin-portal.md](docs/phase-12-admin-portal.md) |

---

## Current Package Versions (verified 2026-03-23)

Do not downgrade these without an ADR. Upgrade only when there is a concrete reason.

| Package | Version | Notes |
|---------|---------|-------|
| `Microsoft.SemanticKernel` | `1.*` → `1.74.0` | Latest stable. No SK 2.0 exists. |
| `Microsoft.SemanticKernel.Agents.Core` | `1.*` → `1.74.0` | |
| `ModelContextProtocol` | `1.1.0` | SK has no built-in MCP package — this is correct. |
| `ModelContextProtocol.AspNetCore` | `1.1.0` | |
| `Anthropic.SDK` | `5.10.0` | No official SK Anthropic connector on NuGet. |
| `Microsoft.Extensions.AI` | `10.4.1` | ME.AI abstraction layer (`IChatClient`). |
| `Microsoft.Extensions.AI.OpenAI` | `10.4.1` | |
| `OpenAI` | `2.9.1` | |
| `NSubstitute` | `5.3.0` | Test mocking library. |
| **Do NOT use** `Microsoft.Agents.*` | `1.4.83` | **Microsoft 365 Agents SDK** — Teams/Bot Framework successor (`github.com/microsoft/Agents`). Wrong scenario for this platform. |
| **Not yet adopted** `Microsoft.Agents.AI.*` | `1.0.0` | **Microsoft Agent Framework 1.0** — separate product (`github.com/microsoft/agent-framework`); SK's forward direction; GA April 3, 2026. Native Anthropic + MCP + A2A. Evaluated in ADR-017: too new to migrate stable platform; `Microsoft.Agents.AI.A2A` nominated for Phase 14 evaluation. |

---

## Phase Dependencies (Reorganized by Tier)

```
FOUNDATION [complete]
  1 [x] → 2 [x] → 3 [x] → 4 [x] → 7 [x] → 8 [x] → 13 [x]

TIER 1 — Core Agentic [complete]
  8 [x] → 11 [x] Rule Learning → 12 [x] PendingRules.tsx

TIER 2 — Tenant Integration [complete, domain tools deferred]
  4 [x] → 6 [x]  Tenant Admin ✓
               └── 10 [x] AdminController ✓
                       └── 12 [x] BusinessRules/PromptEditor/Dashboard ✓
  4 [x] → 5 [~]  MCP Domain Tools (deferred — need real backends)

TIER 3 — Infrastructure & Observability [complete, LiteLLM deferred]
  9 [~]  SSE streaming ✓ — LlmClientFactory + LiteLLMClient pending
  10 [x] Serilog ✓ + OTel ✓ + Prometheus ✓ + AgentStreamHub ✓
  1 [x]  Diva.slnx ✓ + Dockerfile ✓ + docker-compose.yml ✓
  12 [x] Live streaming UI + live tool call trace ✓
```

---

## Coding Conventions

Full details in [docs/conventions.md](docs/conventions.md). Key points:
- C# namespaces match folder path: `Diva.Infrastructure.Learning`
- Interfaces prefixed `I`, async methods suffixed `Async`
- DTOs in `Diva.Core`, entities in `Diva.Infrastructure/Data`
- React components: PascalCase files, named exports, co-located types

---

## Gotchas & Known Issues

- SK `ChatCompletionAgent` and `AgentGroupChat` are under `SKEXP0110` — add pragma suppress at file level
- EF SQLite does **not** support `sp_set_session_context` — RLS is implemented via query filters only for SQLite
- **Multi-provider migrations live in separate assemblies** — EF scans a whole assembly for `Migration` classes, so one assembly cannot hold two provider sets. SQLite migrations stay in `Diva.Infrastructure`; SQL Server migrations are a squashed `InitialCreate` in `Diva.Infrastructure.SqlServer`. Runtime + design-time both select the assembly via `MigrationsAssembly(DivaDbContextFactory.SqlServerMigrationsAssembly)` when `Database:Provider == "SqlServer"`; `DivaDbContextFactory` parses `-- --provider SqlServer`. Regenerate SQL Server migration: `dotnet dotnet-ef migrations add <Name> --project src/Diva.Infrastructure.SqlServer --startup-project src/Diva.Host --context DivaDbContext -- --provider SqlServer`. **Any schema change must be added to BOTH providers.**
- **Provider-conditional filtered indexes** — filtered unique indexes branch on `Database.IsSqlite()` in `OnModelCreating` (`[Email] <> ''` for SQL Server vs `"Email" != ''` for SQLite). Keep the SQLite filter strings byte-identical to the original or the SQLite snapshot drifts (`has-pending-model-changes` will flag it). `[Name] IS NOT NULL` is valid on both, so it is NOT branched.
- **SQLite-only startup fixes must be guarded** — the idempotent `pragma_table_info` / `ALTER TABLE` column-repair blocks in `Program.cs` (main + trace DB) are wrapped in `if (db.Database.IsSqlite())`. SQL Server gets those columns from the squashed `InitialCreate`.
- **SessionTrace must be a separate SQL Server DB** — `SessionTraceDbContext` uses `EnsureCreated` (both providers), which only provisions tables when the database is empty. In SQL Server mode point `ConnectionStrings:SessionTrace` at a distinct `DivaTrace` catalog (host auto-derives `<Database>Trace` if omitted). Trace **data** is never migrated.
- **SQLite → SQL Server data migration = `tools/DbMigrate`** — system-tenant read (`IgnoreQueryFilters`), `SqlBulkCopy(KeepIdentity)` + `SET IDENTITY_INSERT`, single transaction with FK disable/re-enable `WITH CHECK`, per-table row-count validation + rollback, `DBCC CHECKIDENT` reseed. Never copies `__EFMigrationsHistory`/`__EFMigrationsLock`; copies secret columns verbatim (keep `CREDENTIALS_MASTER_KEY`, `LOCAL_AUTH_SIGNING_KEY`, `SCHEDULER_FEEDBACK_TOKEN_SECRET` unchanged). Docker: `docker compose -f docker-compose.tei.yml -f docker-compose.enterprise.yml --profile migrate run --rm dbmigrate`. The Dockerfile `migrate` stage is deliberately NOT last so `build: .` still yields the API image.
- `DynamicAgentRegistry` is registered as `Singleton` but pulls `DivaDbContext` (scoped) — use `IServiceProvider` + `CreateScope()` for DB access inside it
- LiteLLM metadata fields (`tenant_id`, `site_id`) must be snake_case — LiteLLM rejects camelCase
- Admin portal runs on **port 5173** (Vite default, pinned in `vite.config.ts`) — CORS origin is set to match in `appsettings.json` and `appsettings.Development.json`
- `appsettings.Development.json` overrides provider to `"OpenAI"` pointing at LM Studio (`http://localhost:4141/`) — the unified ReAct loop handles both providers via `ILlmProviderStrategy`
- **`yield return` cannot appear inside `try/catch`** in C# async iterators — use temp-variable error capture pattern (`Exception? ex = null; try { ... } catch (Exception e) { ex = e; }`) then yield after the catch
- `InvokeStreamAsync` uses `response.Messages` (plural) from ME.AI `ChatResponse`, not `.Message` — and `FunctionResultContent` takes `(callId, result)` not 3 args
- Solution file is `Diva.slnx` (.NET 10 new format), not `Diva.sln` — use `dotnet build Diva.slnx`
- When adding a new phase doc, also update `docs/INDEX.md` table
- **Docker MCP Gateway runs as stdio, not HTTP** — `docker mcp gateway run` spawns a child process; use binding `{ command: "docker", args: ["mcp", "gateway", "run"] }`. HTTP/SSE mode requires explicit `--transport sse --port 8811` flag and is opt-in. Do NOT use `http://localhost:8811/sse` by default.
- **Multi-server MCP: all valid bindings are connected** — `ConnectMcpClientsAsync` iterates all bindings with non-empty `name` AND (`command` or `endpoint`). `BuildToolClientMapAsync` builds a `Dictionary<string, McpClient>` (tool-name → client) so calls are routed correctly. A binding is skipped (with a warning log) if connection fails.
- **`hasBindings` guard in AgentBuilder** — the default empty binding has `command: "docker"` (truthy), so always check `name.trim() !== ""` as well before treating a binding as valid.
- **`McpClientCache` `hasBindings` check** — `"[]"` (empty JSON array) means "no bindings configured" and must be treated the same as null/empty. The guard condition is `!string.IsNullOrEmpty(s) && s.Trim() != "[]"`. Empty-result is not cached only when real bindings are configured but all connections failed.
- **Manual EF migrations need a Designer.cs** — `MigrateAsync()` discovers migrations via `[Migration("timestamp_Name")]` attribute which lives in the `.Designer.cs` file. A migration class without a Designer.cs is invisible to EF and will never be applied. Always create a Designer.cs with `[DbContext]` + `[Migration]` attributes and a `BuildTargetModel` snapshot copied from the previous migration.
- **`SessionMessages` has no `TenantId` column** — `AgentSessionMessageEntity` is linked to a tenant via its parent session (`SessionId` FK). Never add `TenantId` SQL to `SessionMessages` in data migrations.
- **`CreateDbContext()` without TenantContext bypasses EF query filters** — `DatabaseProviderFactory.CreateDbContext(null)` sets `currentTenantId=0`, which disables the `WHERE TenantId = @id` filter and returns all tenants' data. Always pass `TenantContext` (or `TenantContext.System(tid)`) when creating a DB context in controllers and services.
- **`EffectiveTenantId` pattern is mandatory in all controllers** — never use a raw `[FromQuery] int tenantId` as the sole source of truth after auth is wired. Use `ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId` so regular users are always scoped to their JWT tenant and only master admin (TenantId=0) can pass a query param.
- **Credentials encrypted with ephemeral key are lost on restart** — if `Credentials:MasterKey` is empty, `AesCredentialEncryptor` generates a random key per startup. After restart, all previously-encrypted credentials fail with `AuthenticationTagMismatchException`. Re-save credentials via `PUT /api/admin/credentials/{id}` with `newApiKey` after setting a stable master key.
- **`McpToolBinding.CredentialRef` must match `McpCredentialEntity.Name` exactly** — credential resolution is case-sensitive per-tenant.
- **`McpConnectionManager` headers factory runs per-request, not per-connect** — credential and SSO decisions are made fresh on each outbound HTTP call, so token rotation is handled automatically.
