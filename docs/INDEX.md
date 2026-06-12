# Diva AI Platform — Implementation Index

> **Master tracker for all implementation phases.**
> Update status as work progresses. Each phase file contains full context, code samples, and file lists.

---

## Status Legend

| Symbol | Meaning |
|--------|---------|
| `[ ]` | Not Started |
| `[~]` | In Progress |
| `[x]` | Done |
| `[-]` | Deferred |

---

## Architecture Docs (read-only reference)

| File | Description |
|------|-------------|
| [arch-overview.md](arch-overview.md) | Platform overview, library strategy (SK + AutoGen), solution structure |
| [arch-oauth-flow.md](arch-oauth-flow.md) | OAuth flow diagram, MCP header injection pipeline |
| [arch-security.md](arch-security.md) | 5-layer security architecture |
| [arch-multi-tenant.md](arch-multi-tenant.md) | Tenant/Site hierarchy, canonical TenantContext model |
| [arch-supervisor.md](arch-supervisor.md) | Supervisor pipeline stages, ReAct loop, AgentGroupChat |
| [arch-response-verification.md](arch-response-verification.md) | Hallucination detection, claim grounding, VerifyStage, confidence scoring |

---

## Public Documentation Site

MkDocs Material site in `agent-docs/` — explanation-only architecture docs for GitHub Pages.

| Item | Detail |
|------|--------|
| **Local preview** | `python -m mkdocs serve --config-file agent-docs/mkdocs.yml` → http://127.0.0.1:8000/diva-ai/ |
| **Deploy** | Push to `main` → GitHub Actions (`.github/workflows/deploy-docs.yml`) → `gh-pages` branch |
| **Pages** | 12 pages across 5 sections (Core, Tools, Quality, Streaming, Tenancy) |
| **Synced to** | Changelog through 2026-04-12 |

---

## Implementation Phases

| # | Status | Tier | Phase | Doc | Key Deliverables |
|---|--------|------|-------|-----|-----------------|
| 1 | `[x]` | 3 | Solution Scaffold | [phase-01-setup.md](phase-01-setup.md) | All 6 .csproj + 3 test .csproj ✓, appsettings.json ✓, Diva.slnx ✓, Dockerfile ✓, docker-compose.yml ✓, docker-compose.enterprise.yml ✓ |
| 2 | `[x]` | — | Core Models | [phase-02-core-models.md](phase-02-core-models.md) | TenantContext ✓, McpRequestContext ✓, AgentRequest ✓, AgentResponse ✓, AgentStreamChunk ✓, OAuthOptions ✓, DatabaseOptions ✓, AgentOptions ✓, LlmOptions ✓ |
| 3 | `[x]` | — | OAuth + Tenant Middleware | [phase-03-oauth-tenant.md](phase-03-oauth-tenant.md) | OAuthTokenValidator ✓, TenantContextMiddleware ✓, TenantClaimsExtractor ✓, HeaderPropagationHandler ✓ |
| 4 | `[x]` | — | Database & EF Core | [phase-04-database.md](phase-04-database.md) | DivaDbContext ✓, ITenantEntity ✓, all entities ✓ (Agent, BusinessRule, Session, LearnedRule), migrations ✓, DatabaseProviderFactory ✓ |
| 5 | `[~]` | 2 | MCP Tool Infrastructure | [phase-05-mcp-tools.md](phase-05-mcp-tools.md) | `McpHeaderPropagator` ✓, `TenantAwareMcpClient` ✓, `ConnectMcpClientsAsync` ✓ (all bindings), `BuildToolClientMapAsync` ✓, `POST /api/agents/mcp-probe` ✓ — **domain tools (Analytics, Reservation) deferred** (require real data backends) |
| 6 | `[x]` | 2 | Tenant Admin Services | [phase-06-tenant-admin.md](phase-06-tenant-admin.md) | `ITenantBusinessRulesService` ✓, `TenantBusinessRulesService` ✓, `TenantAwarePromptBuilder` ✓, `IPromptBuilder` in Core ✓, wired into `AnthropicAgentRunner` ✓ |
| 7 | `[x]` | — | Session Management | [phase-07-sessions.md](phase-07-sessions.md) | AgentSessionService ✓, multi-turn conversation history ✓ |
| 8 | `[x]` | — | SK Agents + Supervisor | [phase-08-agents.md](phase-08-agents.md) | IWorkerAgent ✓, DynamicReActAgent ✓, DynamicAgentRegistry ✓, ISupervisorAgent ✓, SupervisorAgent ✓, ISupervisorPipelineStage ✓, all 7 stages ✓, SupervisorController ✓ |
| 9 | `[x]` | 3 | LLM Client Factory | [phase-09-llm-client.md](phase-09-llm-client.md) | AnthropicAgentRunner ✓, LlmOptions ✓, `InvokeStreamAsync` ✓ (Anthropic + OpenAI-compat), parallel tool execution via `Task.WhenAll` ✓, per-tool 30s timeout ✓, plan detection + `plan`/`plan_revised` SSE chunks ✓, adaptive re-planning ✓, LiteLLM via DirectProvider config ✓, `litellm_config.yaml` ✓ |
| 10 | `[x]` | 2+3 | API Host + Observability | [phase-10-api-host.md](phase-10-api-host.md) | Program.cs ✓, AgentsController ✓, `POST /invoke/stream` SSE ✓, AgentStreamHub ✓ (`/hubs/agent`), AdminController ✓, OTel tracing ✓, Prometheus `/metrics` ✓, Serilog → Console + Seq ✓ |
| 11 | `[x]` | 1 | Dynamic Rule Learning | [phase-11-rule-learning.md](phase-11-rule-learning.md) | IRuleLearningService ✓, RuleLearningService ✓, LlmRuleExtractor ✓, ISessionRuleManager ✓, SessionRuleManager ✓, FeedbackLearningService ✓, wired into AnthropicAgentRunner ✓ |
| 12 | `[x]` | 1+2+3 | Admin Portal UI | [phase-12-admin-portal.md](phase-12-admin-portal.md) | AgentList ✓, AgentBuilder ✓, AgentChat (live SSE streaming + live tool trace with input/output) ✓, `tools_available` / `plan` / `plan_revised` chunk handling ✓, plan card UI in live feed ✓, PendingRules ✓, BusinessRules ✓, PromptEditor ✓, ScheduledTasks ✓, Dashboard ✓, api.ts (streamAgent, probeMcp) ✓, MSW streaming mock ✓, DockerGatewayPanel ✓, auto-expand iteration trace ✓ — **Full UI revamp [2026-03-24]:** shadcn/ui + Tailwind v4 + react-router v7 + next-themes + sonner + recharts; sidebar layout; all 9 pages rewritten ✓ |
| 13 | `[x]` | — | Response Verification | [phase-13-verification.md](phase-13-verification.md) | VerificationResult ✓, VerificationOptions ✓, ResponseVerifier (Off/ToolGrounded/LlmVerifier/Strict/**Auto**) ✓, VerifyStage ✓, ToolEvidence trail ✓, verification badges in AgentChat ✓, inline ReAct correction retry with tool access ✓, per-agent `VerificationMode` override ✓ |
| 14 | `[x]` | 3 | A2A Protocol | [phase-14-a2a.md](phase-14-a2a.md) | AgentCard endpoint, task lifecycle, A2AAgentClient, DispatchStage A2A routing ✓ — **Hardening [2026-04-11]:** concurrent task limits, HttpClient resilience, task cleanup service, rate limiting, A2A Settings UI, 23 tests ✓ — **Agents-as-Tools [2026-04-12]:** `AgentDelegationTool` AIFunction subclass, `AgentToolProvider`/`AgentToolExecutor`, `IAgentDelegationResolver` cross-project abstraction, `AddExtraTools` on provider strategies, routing in `AnthropicAgentRunner`, `DelegateAgentIdsJson` DB column + migration, `DelegateAgentSelector` UI, 26 tests ✓ — **Bug Fixes [2026-04-12]:** delegation ID type mismatch (int→string), A2A Enabled config, partial migration recovery (TenantLlmConfigs.Name) ✓ |
| 15 | `[x]` | 1+3 | Custom Pluggable Agents | [phase-15-custom-agents.md](phase-15-custom-agents.md) | Agent archetypes (8 built-in), lifecycle hooks (7 hook points incl. OnError), AgentHookPipeline, BaseCustomAgent (IStreamableWorkerAgent), A2A integration (task cancellation, depth protection), group agent template loading, ExecutionMode (Full/ChatOnly/ReadOnly/Supervised), ToolAccessLevel per binding, ArchetypeSelector UI, HookEditor UI, RemoteA2AAgent — 27 gaps identified & resolved ✓ |
| 16 | `[x]` | 1+2 | Rule Packs | — | DB-driven configurable hook rule bundles ✓, 9 rule types ✓, RulePackService CRUD ✓, RulePackEngine (runtime eval + regex cache + batched logging) ✓, ConflictAnalyzer (internal + cross-pack) ✓, TenantRulePackHook ✓, RulePackController (15 endpoints) ✓, RulePackManager + PackEditor UI ✓, 54 tests ✓ |
| 17 | `[x]` | 1+2 | Agent Setup Assistant | [phase-17-agent-setup-assistant.md](phase-17-agent-setup-assistant.md) | AI-assisted system prompt + rule pack suggestion, create/refine modes, model_switch-aware suggestions using LlmConfigId, prompt/rule pack history timelines, compare + restore flows, security guardrails, MSW mocks — 189 TenantAdmin + 110 Agent tests pass ✓ |
| 18 | `[x]` | 1+2 | Group-Level Agents + Per-Agent Business Rules | — | Thin overlay pattern: `TenantGroupAgentOverlayEntity` stores tenant deltas; `GroupAgentOverlayMerger` merges template+overlay at runtime. Explicit activation required. Per-agent scoping: nullable `AgentId` on `TenantBusinessRuleEntity`; cache key extended; `IPromptBuilder.BuildAsync` passes `agentId`. 7 overlay API endpoints, `GroupAgentOverlayEditor.tsx`, AgentList activation UX, MSW stubs ✓ — **Addendum [2026-04-06]:** Full `GroupAgentTemplateBuilder` (4-tab, all 26 fields), "Publish to Group" flow for tenant admins, `GET /api/agents/my-groups`, `DevMasterAdmin()` dev context, GroupsController GET projection fix ✓ |

| 19 | `[~]` | 1+3 | Coordinator Sub-Agent Routing | [phase-19-coordinator-sub-agent-routing.md](phase-19-coordinator-sub-agent-routing.md) | **Foundation complete [2026-05-06]:** 7-gap SOLID refactor — `AgentContextStage` ✓, `IReadableAgentRegistry` ✓, `ICapabilityScoringService` ✓, `LlmDecompositionStrategy` + `SingleTaskStrategy` + `DecompositionStrategySelector` ✓, `ResponseSynthesizer` ✓, `SupervisorLlmOverride` ✓, `VerifyStage` multi-agent fix ✓, `IToolSelectionStrategy` / `LlmToolSelector` semantic tool pre-filter ✓ — **Remaining work is enhancement-only** [2026-05-10]: Phase 14 Agents-as-Tools (`DelegateAgentIdsJson`) already covers most coordinator use cases. Remaining (`OrchestratorAgent`, `ScopedAgentRegistry`, `SubAgentIdsJson` DB field, `SubAgentSelector.tsx`) adds hard agent isolation + true pipeline-level parallelism — implement when 5+ sub-agents or auditable decomposition is required |
| 20 | `[x]` | — | OSS Release + White-Label | [phase-20-oss-release.md](phase-20-oss-release.md) | Security cleanup (key rotation, git history rewrite) ✓, Apache 2.0 license ✓, GitHub community files ✓, CI/CD workflows ✓, config-driven branding (`AppBrandingOptions`) ✓, rebrand scripts ✓, white-labeling docs ✓ |
| 21 | `[ ]` | 1+2 | AI Session Prompt Advisor | [phase-21-session-prompt-advisor.md](phase-21-session-prompt-advisor.md) | `IPromptAdvisorService`, LLM-powered session trace analysis, structured issues + suggestions + rule surfacing, `PromptAdvisorController` (analyze + apply endpoints), `PromptAdvisor.tsx` full-page review UI, diff view, accept/reject flow wired to prompt history + PendingRules |
| 22 | `[x]` | 2+3 | Embeddable Chat Widget | [phase-22-embeddable-widget.md](phase-22-embeddable-widget.md) | `WidgetConfigEntity` + EF migration ✓, `WidgetTheme` record (Light/Dark presets, 18 tokens) ✓, `WidgetConfigService` CRUD ✓, `IssueWidgetAnonymousJwt` ✓, `WidgetController` (init/auth/session endpoints) ✓, widget admin CRUD in `AdminController` ✓, `Widget` CORS policy ✓, `widget.js` vanilla embed script ✓, widget SPA (11.5 kB bundle, SSE streaming chat, postMessage SSO, system dark mode) ✓, `WidgetManager` + `WidgetEditor` admin UI with live theme preview ✓, 10 tests ✓ |
| 23 | `[x]` | 2+3 | MCP Server Framework + FileSystem | [phase-23-mcp-server-framework.md](phase-23-mcp-server-framework.md) | `McpServerContext` ✓, `McpServerRegistration`/`IDivaMcpToolType` ✓, `IFileSystemPathGuard` ✓, `IToolFilter` ✓, `IPdfReader` ✓, `IImageReader` ✓, `IOfficeReader` ✓, `IOfficeWriter` ✓, `FileSystemPathGuard` ✓, `ToolFilter` ✓, `PdfReader` (PdfPig) ✓, `ImageReader` (ImageSharp + blur/exposure/thumbnail/base64 + vision resize) ✓, `OfficeReader` (DocumentFormat.OpenXml v3 — .docx/.xlsx/.pptx, summaryOnly + pagination + search) ✓, `OfficeWriter` (Word inline formatting + pipe tables, Excel formulas, pivot summaries, PowerPoint slides, LibreOffice PDF export) ✓, `FileSystemMcpTools` (33 tools incl. `view_image`) ✓, embedded `/mcp/diva` ✓, standalone exe (stdio + Windows Service) ✓, ~47 tests ✓ — **Vision addendum [2026-05-14]:** `view_image` tool ✓, `ContentPart`/`ImageContentPart`/`DocumentContentPart` ✓, `ToolExecutor` image extraction ✓, `UseVisionSummarization`/`SummarizeImageAsync` on `ILlmProviderStrategy` ✓, `LmStudioVisionPolicy` ✓, two-pass vision (objects + text) ✓, user attachments on `AgentRequest` ✓, agent-scoped rule learning config ✓ |
| 24 | `[x]` | 3 | Agent Optimization from Historical Sessions | — | LLM-as-Judge per-turn scoring (4 dimensions: Faithfulness/Completeness/ToolEfficiency/Coherence) ✓, `TurnScoringService` fire-and-forget from `SessionTraceWriter` ✓, `SessionAnalyzer` (single-session + aggregate, worst-turn sampling) ✓, `OptimizationLlmAnalyzer` (dimension-aware prompt) ✓, `AgentOptimizationService` (concurrent-run guard, background pipeline, few-shot CRUD) ✓, `OptimizationApplicator` (8 suggestion types, LLM merge apply, `SavePromptVersionAsync` integration) ✓, `OptimizationSchedulerHostedService` (daily/weekly, all-tenant poll) ✓, few-shot injection in `TenantAwarePromptBuilder` ✓, `AgentOptimizationMcpTools` (5 MCP tools, cross-tenant safe) ✓, `AgentOptimizationController` (17 REST endpoints) ✓, EF migrations (`AddAgentOptimizationTables` + `AddTurnScoringColumns` + `AddOptimizationOverrideJson`) ✓, 60 tests ✓ — **Addendum [2026-05-10]:** Quick Prompt Fix (`POST /api/agents/{id}/prompt/improve`, `PromptQuickFixDialog`) ✓, per-agent `OptimizationOverrideOptions` (`MergeMaxTokens`, `AnalyzerMaxTokens`) ✓, `AgentOptimizationSuggestions` two-tab redesign (System Prompt LLM merge / Agent Config bulk apply) ✓, `AgentOptimizer` session-mode UX + `QualityBar` null crash fix ✓, LLM network timeout fix (600 s) ✓ |
| 25 | `[ ]` | 3 | Observability & Telemetry | [phase-24-observability-telemetry.md](phase-24-observability-telemetry.md) | OTel GenAI semantic conventions, `ILlmSpanEmitter`/`ILlmMetricEmitter`/`IAgentEventLogger` interfaces + Null-objects, `DivaActivitySources`, `DivaMeter` (12 instruments), Serilog↔OTel trace correlation, DB-backed `ObservabilityConfigEntity` (platform-wide, no `ITenantEntity`), `HeliconeHttpHandler` (Transient `DelegatingHandler` + `AsyncLocal` context), `ModelCostTable`, `PrometheusMetricsSummaryReader`, `ObservabilityController` (4 endpoints), `ObservabilitySettings.tsx` admin page (Helicone toggle, metrics 24h summary), `docker/otel-collector-config.yaml` (new), Grafana + Tempo + opt-in Helicone in enterprise compose, ~15 tests |
| 26 | `[ ]` | 1+2+3 | AI-Native RAG Pipeline | [phase-26-rag-pipeline.md](phase-26-rag-pipeline.md) | `Diva.Rag` project (new), `IDocumentConnector`/`IDocumentChunker`/`IEmbeddingService`/`IVectorRepository`/`IKnowledgeRetriever` abstractions, 7 connectors (File, HTTP, Confluence, Jira, GitLab, SQL Server, SharePoint), `RecursiveTextChunker` + `CodeChunker`, `OpenAiEmbeddingService` + `OllamaEmbeddingService`, Qdrant vector store (dense + BM25 hybrid, gRPC 6334), 3-tier diff versioning (ExternalVersion → ContentHash → ChunkHash), Platform→Group→Tenant scope hierarchy, `MetadataTaxonomy` org-structure tags, `EntityLinkExtractor` + `MultiHopRetriever` (lightweight knowledge graph), `AgentKnowledgeProfile` per-agent defaults, `RagContextStage` supervisor injection, `search_knowledge` MCP tool, `IngestionWorkerService` (SSE progress), `RagController` + `WebhooksController` (Confluence/GitLab/Jira HMAC-SHA256), 5 new DB entities + migration, admin portal RAG pages, Docker Qdrant service, ~60 tests — **6 sub-phases: 25.1 Foundation → 25.2 Enterprise Connectors → 25.3 Advanced Retrieval → 25.4 Dev Workflow Agents → 25.5 Business Workflow Agents → 25.6 Quality & Scale** |
| 27 | `[ ]` | 1+2+3 | Central Agent Registry / Marketplace | [phase-27-agent-marketplace-registry.md](phase-27-agent-marketplace-registry.md) | Hub-and-spoke catalog for cross-tenant / multi-instance / cross-deployment agent config propagation. `CatalogItemEntity` + append-only `CatalogVersionEntity` (immutable, `ContentHash`-keyed, reuses `AgentExportBundle`), `MarketplaceService` (publish/list/get/deprecate/yank), `MarketplaceController`, embedded-or-standalone toggle (`Marketplace:Mode`); `PlatformAgentTemplateEntity` (top tier) + `FieldLocksJson` governance + group-template field parity; `LayeredAgentResolver` (5-tier: marketplace → platform → group → overlay → tenant agent); `InstalledAgentEntity` install/subscribe/pin + version diff/rollback + PUT `Version`-bump fix; `IConfigChangeBus` (Redis pub/sub + no-op), real `IDistributedCache` + SignalR Redis backplane, `docker-compose.scale.yml`; cross-deployment pull (`sinceVersion`, idempotent `ContentHash` upsert) + polling `BackgroundService` + optional webhook — **5 sub-phases: 27.1 Marketplace Catalog → 27.2 Field-Lock + 5-Tier Resolver → 27.3 Pull/Subscribe/Pin → 27.4 Multi-Instance → 27.5 Cross-Deployment Sync** — *captured for future implementation, not scheduled* |
| 28 | `[x]` | 2+3 | Agent Access Groups | [phase-28-agent-access-groups.md](phase-28-agent-access-groups.md) | Per-user / per-role agent authorization. `AgentGroupEntity` (member agent IDs + `AllowedUserIdsJson` + `AllowedRolesJson`), `IAgentGroupService`/`AgentGroupService` (`IMemoryCache` 5-min TTL + `CanInvokeAgentAsync`), additive enforcement in `AgentsController.Invoke`/`InvokeStream`, `AgentGroupsController` admin CRUD; API-key group grants (`PlatformApiKeyEntity.AllowedGroupIdsJson` → `TenantContext.GroupAccess`); **SSO claim mapping fix** — first-class `ClaimMappingsOptions.Groups` + `TenantContext.UserGroups` (groups no longer dropped when a `roles` claim is present); `AgentGroups` admin UI + API-key allowed-groups selector; backward compatible (unrestricted agents stay open) |

---

## Reorganized Implementation Order

### Tier 1 — Core Agentic Functionality ✅ Complete

| Work | Status | Phase | Files |
|------|--------|-------|-------|
| Rule extraction from conversations | ✅ | 11 | `LlmRuleExtractor`, `IRuleLearningService`, `RuleLearningService` |
| Session-scoped rule storage | ✅ | 11 | `SessionRuleManager` (distributed cache) |
| Feedback-driven learning | ✅ | 11 | `FeedbackLearningService` |
| Wire into agent post-response | ✅ | 11 | `AnthropicAgentRunner` calls extractor after each turn |
| Pending rules review UI | ✅ | 12 | `PendingRules.tsx` |

---

### Tier 2 — Tenant Integration ✅ Complete (domain tools deferred)

| Work | Status | Phase | Files |
|------|--------|-------|-------|
| Per-tenant business rules service | ✅ | 6 | `TenantBusinessRulesService`, `ITenantBusinessRulesService` |
| Tenant-aware prompt builder | ✅ | 6 | `TenantAwarePromptBuilder` (augments DB system prompts with business rules + session rules) |
| Multi-server MCP client support | ✅ | 5 | `ConnectMcpClientsAsync`, `BuildToolClientMapAsync`, `POST /api/agents/mcp-probe` |
| MCP domain tool servers | ⏳ deferred | 5 | `AnalyticsMcpServer`, `ReservationMcpServer` — require real data backends |
| Admin REST API | ✅ | 10 | `AdminController` — business rules CRUD, prompt overrides, learned rule approve/reject |
| Business rules UI | ✅ | 12 | `BusinessRules.tsx`, `PromptEditor.tsx` |
| Dashboard UI | ✅ | 12 | `Dashboard.tsx` |

---

### Tier 3 — Infrastructure & Observability ✅ Complete (LiteLLM routing deferred)

| Work | Status | Phase | Files |
|------|--------|-------|-------|
| SSE iteration streaming | ✅ | 9+10+12 | `InvokeStreamAsync` (Anthropic + OpenAI-compat), `/invoke/stream`, `AgentChat.tsx` live feed, parallel tool execution (`Task.WhenAll`), plan detection + re-planning |
| Live tool call trace in UI | ✅ | 12 | `AgentChat.tsx` — input/output shown inline as chunks arrive; auto-expands on completion |
| Docker MCP Gateway panel | ✅ | 12 | `DockerGatewayPanel` in `AgentBuilder.tsx` — auto-detect stdio/HTTP, test connection, discover tools |
| MSW streaming mock | ✅ | 12 | `handlers.ts` — `ReadableStream` per-chunk SSE simulation |
| SignalR hub | ✅ | 10 | `AgentStreamHub` at `/hubs/agent` |
| Structured logging | ✅ | 10 | Serilog → Console + Seq |
| Distributed tracing | ✅ | 10 | OpenTelemetry → OTLP collector |
| Prometheus metrics | ✅ | 10 | `/metrics` via prometheus-net.AspNetCore |
| Solution file | ✅ | 1 | `Diva.slnx` (9 projects) |
| Containerisation | ✅ | 1 | `Dockerfile`, `docker-compose.yml`, `docker-compose.enterprise.yml` |
| LiteLLM proxy client | ✅ | 9 | Configure `DirectProvider` to point at LiteLLM URL — OpenAI-compatible path handles it natively. `litellm_config.yaml` defines model aliases. |
| MCP Credential Vault | ✅ | — | AES-256-GCM encrypted credential store (`McpCredentialEntity`), `CredentialRef` per binding, 3-tier auth (SSO → credential → tenant headers), `/api/admin/credentials` CRUD, `/settings/credentials` UI |
| Platform API Keys | ✅ | — | `diva_` prefix, SHA-256 hashed, `X-API-Key` middleware (before Bearer), scope (FullAccess/AgentInvoke), `/api/admin/api-keys` CRUD, `/settings/api-keys` UI |
| Structured MCP call logging | ✅ | — | Credential resolution, auth method selection, tenant context source tracing in `McpConnectionManager` |

---

## Reference & Dev Guides

| File | Description |
|------|-------------|
| [ref-config.md](ref-config.md) | appsettings.json, Docker Compose, Kubernetes, CI/CD, LiteLLM config |
| [conventions.md](conventions.md) | C# and TypeScript coding standards, naming rules, project boundaries |
| [decisions.md](decisions.md) | Architecture Decision Log — why key choices were made |
| [testing.md](testing.md) | Test strategy, SQLite setup, per-phase coverage, what to mock |
| [perf-improvements.md](perf-improvements.md) | Agent response latency fixes: MCP client cache, single-pass tool listing, fire-and-forget rule extraction, parallel prompt builder |
| [changelog.md](changelog.md) | Completed features and fixes (reverse chronological); pending / deferred items |
| [agents.md](agents.md) | Dev cycle guide — ReAct loop patterns, key files, test conventions, SSE event types, deferred items |
| [rule-packs-guide.md](rule-packs-guide.md) | Complete Rule Pack usage guide: hook points, compatibility matrix, parameter reference, API payload examples, troubleshooting |
| [hardening-backlog.md](hardening-backlog.md) | Agentic flow robustness backlog — 18 tracked items (P0/P1/P2/Tests/Docs); check off when fixed |
| [phase-22-embeddable-widget.md](phase-22-embeddable-widget.md) | Phase 22 — Embeddable Chat Widget: architecture, all implemented components, security notes, verification checklist, file index |

## AI Assistant Instructions

| File | Tool | Description |
|------|------|-------------|
| [../CLAUDE.md](../CLAUDE.md) | Claude Code | Build commands, hard rules, active phase, gotchas |
| [../.github/copilot-instructions.md](../.github/copilot-instructions.md) | GitHub Copilot | Code generation context, conventions, patterns |

---

## Dependency Order

```
── FOUNDATION (complete) ──────────────────────────────────────────────────
Phase 1 [x] → Phase 2 [x] → Phase 3 [x] → Phase 4 [x] → Phase 7 [x]
                                                                └── Phase 8 [x] → Phase 13 [x]

── TIER 1: Core Agentic (complete) ────────────────────────────────────────
Phase 8 [x] → Phase 11 [x] Rule Learning → Phase 12 [x] PendingRules.tsx

── TIER 2: Tenant Integration (complete, domain tools deferred) ───────────
Phase 4 [x]
    ├── Phase 6 [x]  Tenant Admin Services ✓
    │       └── Phase 10 [x] AdminController ✓
    │               └── Phase 12 [x] BusinessRules.tsx, PromptEditor.tsx, Dashboard.tsx ✓
    └── Phase 5 [~]  MCP Domain Tools (Analytics, Reservation — deferred, need real backends)

── TIER 3: Infrastructure & Observability (complete, LiteLLM deferred) ────
Phase 9 [x]  SSE streaming ✓ — LiteLLM via DirectProvider config ✓
Phase 10 [x] Serilog ✓ + OpenTelemetry ✓ + Prometheus ✓ + AgentStreamHub ✓
Phase 1 [x]  Diva.slnx ✓ + Dockerfile ✓ + docker-compose.yml ✓
Phase 12 [x] Live streaming UI + live tool call trace ✓
Phase 14 [x] A2A Protocol — AgentCard + task lifecycle + remote delegation + hardening + agents-as-tools peer delegation ✓

── RAG PIPELINE (not started) ─────────────────────────────────────────────
Phase 4 [x] + Phase 5 [~] + Phase 8 [x] + Phase 19 [~]
    └── Phase 26 [ ]  AI-Native RAG Pipeline
            ├── Diva.Rag project (connectors, chunking, embeddings, Qdrant)
            ├── RagContextStage (supervisor) + search_knowledge MCP tool
            ├── 3-level scope (Platform → Group → Tenant)
            └── Blocks: Phase 27 Dev Workflow Agents, Phase 28 Business Workflow Agents
```

---

## Solution Structure (Quick Reference)

```
Diva/
├── src/
│   ├── Diva.Core/            # Models, DTOs, config interfaces
│   ├── Diva.Infrastructure/  # DB, Auth, LLM, Sessions, Learning
│   ├── Diva.Agents/          # SK ChatCompletionAgent wrappers, Supervisor, Registry
│   ├── Diva.Tools/           # MCP tool infrastructure + domain tool servers
│   ├── Diva.TenantAdmin/     # Business rules service, prompt builder
│   └── Diva.Host/            # ASP.NET Core 10 API host
├── admin-portal/             # React + Vite + TypeScript
├── docs/                     # ← YOU ARE HERE
└── tests/
    ├── Diva.Agents.Tests/
    ├── Diva.Tools.Tests/
    └── Diva.TenantAdmin.Tests/
```
