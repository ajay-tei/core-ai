# Diva AI — Agent Dev Cycle Guide

> Maintained for Claude Code sessions. Covers current state, active patterns, test conventions, and session-to-session continuity notes. Update when patterns change or new conventions are established.

---

## Current Platform State (as of 2026-05-17)

All implementation phases complete. Phase 5 (domain MCP tool servers) is partially deferred pending real data backends. Phase 19 foundation is complete — remaining work (`OrchestratorAgent`, `ScopedAgentRegistry`, `SubAgentIdsJson`) is an **optional enhancement** for advanced parallel-dispatch scenarios; most coordinator use cases are already served by Phase 14 Agents-as-Tools (`DelegateAgentIdsJson`). The primary execution engine is `AnthropicAgentRunner` — the planned `LlmClientFactory`/`LiteLLMClient` were superseded by the strategy pattern.

**What's live in `AnthropicAgentRunner`:**
- Single unified `ExecuteReActLoopAsync` shared by all providers via `ILlmProviderStrategy`
- Two strategies: `AnthropicProviderStrategy` (Anthropic SDK) and `OpenAiProviderStrategy` (raw IChatClient, no `UseFunctionInvocation`)
- `RunAsync` materialises `InvokeStreamAsync` — no separate non-streaming paths
- Context window management (Point A + Point B)
- Tool error retry with `hadToolErrors` flag
- max_tokens handling: `hookCtx.WasTruncated = true`, OnError hook invoked, if Abort or `maxTokensNudgeRetries <= 0` the partial response is accepted (`completedNaturally = true`); otherwise one nudge via `MaxTokensNudgePrompt` is sent
- Continuation windows with globally-unique iteration numbering
- Response verification (Off / ToolGrounded / LlmVerifier / Strict / Auto)
- Parallel tool execution + deduplication
- LLM retry with exponential backoff (`CallWithRetryAsync` — logs error when retries are exhausted)
- Info-level structured logging for each ReAct stage in the shared loop (`continuation_start`, `iteration_start`, `plan`, `thinking`, `tool_call`, `tool_result`, `plan_revised`, `correction`, `final_response`, `verification`, `done`)
- Per-agent config: `MaxContinuations`, `MaxOutputTokens`, `MaxToolResultChars`, `ToolFilterJson` (allow/deny), `PipelineStagesJson`, `StageInstructionsJson`, `ModelSwitchingJson`
- Per-iteration model switching: model (and provider) can change between iterations driven by three hook layers in priority order — Rule Pack `model_switch` rule (Order=2), `StaticModelSwitcherHook` JSON config (Order=3), `ModelRouterHook` Variables heuristics (Order=4). Cross-provider switches use `ExportHistory`/`ImportHistory` on `ILlmProviderStrategy`. SSE event `model_switch` is emitted on each switch.
- Instruction flow: `AgentRequest.Instructions` → supervisor pipeline → system prompt
- `effectiveMaxOutputTokens = definition.MaxOutputTokens ?? _agentOpts.MaxOutputTokens` — passed to both provider strategies
- **Semantic tool pre-filter (2026-05-06):** `IToolSelectionStrategy?` optional dep. When registered (`LlmToolSelector`), fires one lightweight LLM call after `ApplyExecutionModeFilter` to narrow the tool set before the ReAct loop. Only fires when `allMcpTools.Count > AgentOptions.SemanticToolFilterThreshold` (default 8). Keeps at most `SemanticToolFilterMaxTools` (default 6). Multi-agent: each worker independently filters against its own sub-task description. Safe fallback to full list on any error.
- **Agent memory auto-recall (2026-05-17):** When `enableMemory + memoryAutoRecall` are set in `KnowledgeProfileJson`, the runner resolves `IAgentMemoryService` via reflection at `InvokeStreamAsync` time and injects recalled memories into the system prompt before the ReAct loop. Uses composite scoring (`cosine × TypeWeight`) and `TopK = maxResults * 2` candidates for reranking. Reflection call param order: `tenantId, agentId, query, memoryType, sessionId, userId, currentSessionOnly, maxResults, ct`.
- **Auto-checkpoint (2026-05-17):** When `enableMemory + saveTaskCheckpoint` are both true in `KnowledgeProfileJson`, a fire-and-forget `Task.Run` saves an episodic memory at the end of each successful `ExecuteReActLoopAsync` call — tagged `["checkpoint","task-state"]`. Combined with auto-recall this provides cross-session continuity. Never blocks the SSE stream. Works for both Anthropic and OpenAI-compatible providers.

**Credential forwarding (2026-04-16):**
 - MCP tool calls now use only the resolved `CredentialRef` for outbound authentication. The inbound Diva platform API key (`X-API-Key`) is never forwarded to MCP servers. This fixes a prior security bug where the inbound API key could be leaked to external tool servers.
 - A2A agent calls (remote agent-to-agent) still use the inbound API key for platform authentication, but it is not used for MCP tool calls.
 
**Agent delegation auth chain:** `TenantContext` (including `AccessToken`) flows through `AgentToolExecutor` → `DelegationAgentResolver` → sub-agent → `McpConnectionManager.ConnectAsync(fallbackTenant)`. `ForwardSsoToMcp` is propagated from parent request to delegated request. `HttpContext` is also available via `AsyncLocal` since delegation runs within the same HTTP pipeline.

---

## Anthropic API Key — Docker Compose Environment

When running the platform in Docker, ensure that `ANTHROPIC_API_KEY` (and any other LLM provider API keys) are present in the main `.env` file. Docker Compose loads `.env` by default, not `.env.development`. If you use `.env.development`, specify it explicitly with `docker compose --env-file .env.development up -d`.

If you see `"x-api-key header is required"` errors from the Anthropic SDK, check that the API key is set in the correct environment file for your deployment mode.

**Auth / Multi-tenant (activated 2026-03-25):**
- SSO login flow: `GET /api/auth/login?tenantId=` → IdP → `GET /api/auth/callback` → local JWT issued via `LocalAuthService.IssueSsoJwt`
- Local JWT is always used after SSO — never the provider's raw token; validated by our local HMAC key
- Logout: portal → `GET /api/auth/logout?logoutUrl=` → IdP logout → `GET /api/auth/logout-callback` → portal `/login`
- `EffectiveTenantId` pattern: JWT tenant wins for non-master-admin; query param only honoured for TenantId=0 (master admin)
- **Platform API key auth:** `X-API-Key` header → `IPlatformApiKeyService.ValidateAsync` → builds `TenantContext` with scope-mapped role (before Bearer check in middleware)
- EF query filter `WHERE TenantId = @currentTenantId` active on all entities; pass `TenantContext` to `CreateDbContext()` — passing null gives `currentTenantId=0` and bypasses the filter
- **Session trace storage (updated 2026-04-13):** `SessionTraceWriter` stores tool inputs and outputs in full — no truncation caps. `TurnSummary` DTO includes both `*Preview` (200 chars, used in turn list) and `UserMessage`/`AssistantMessage` (full, used in turn detail view). The `MaxSuggestionTokens` default was raised to 4096 to prevent prompt builder JSON truncation.
- **SSO identity extraction (updated 2026-04-01):** callback reads `ClaimMappingsJson` from `TenantSsoConfigEntity` to resolve per-tenant field names (`UserId`, `Email`, `DisplayName`, `Roles`) before touching any claim. Identity priority: `id_token` JWT payload (parsed without verification) → userinfo endpoint (wins on conflict). Login fails with 400 if neither source yields an identifier — the `"sso-user"` fallback has been removed.
- **User profile one-to-one mapping:** `UserProfileService.UpsertOnLoginAsync` uses two-phase lookup — first by `sub` (fast, indexed), then by email (handles SSO provider migrations). If found by email with a different sub, updates `UserId` to the new sub. Unique index `(TenantId, Email)` with filter `Email != ''` enforces one email → one profile per tenant at the DB level.

---

## Key Files — Quick Reference

| File | Role |
|------|------|
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Primary agent runner — unified ReAct loop via strategy pattern |
| `src/Diva.Infrastructure/LiteLLM/IMcpConnectionManager.cs` | Public interface for MCP lifecycle (testable via NSubstitute) |
| `src/Diva.Infrastructure/LiteLLM/McpConnectionManager.cs` | Implements IMcpConnectionManager: connect + enumerate tools |
| `src/Diva.Infrastructure/LiteLLM/IReActHookCoordinator.cs` | Public interface for agent lifecycle hook dispatch (testable via NSubstitute) |
| `src/Diva.Infrastructure/LiteLLM/ReActHookCoordinator.cs` | Implements IReActHookCoordinator: all 7 lifecycle hook points |
| `src/Diva.Infrastructure/LiteLLM/ILlmProviderStrategy.cs` | Strategy interface + unified types (UnifiedLlmResponse, UnifiedToolCall, UnifiedToolResult) |
| `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs` | Anthropic SDK strategy implementation |
| `src/Diva.Infrastructure/LiteLLM/OpenAiProviderStrategy.cs` | OpenAI-compatible strategy (raw IChatClient, no UseFunctionInvocation) |
| `src/Diva.Core/Configuration/AgentOptions.cs` | All agent config (MaxIterations, MaxContinuations, Retry, ContextWindow, SemanticToolFilter, ...) |
| `src/Diva.Core/Models/AgentStreamChunk.cs` | SSE event model — add fields and type docs here when adding new events |
| `src/Diva.Infrastructure/Context/ContextWindowManager.cs` | Point A + Point B compaction |
| `src/Diva.Infrastructure/Verification/ResponseVerifier.cs` | All verification modes |
| `admin-portal/src/components/AgentChat.tsx` | Streaming event handler (`handleChunk` switch), detailed execution timeline, and sanitized HTML rendering for agent responses |
| `admin-portal/src/api.ts` | `AgentStreamChunk` TS interface — mirror C# model fields here |
| `tests/Diva.Agents.Tests/ToolOptimizationTests.cs` | Unit tests for `internal static` helpers |
| `tests/Diva.Agents.Tests/ProviderStrategyTests.cs` | Strategy + FilterTools + instruction flow tests |
| `tests/Diva.Agents.Tests/ContextWindowTests.cs` | Context window unit + integration tests |
| `tests/Diva.Agents.Tests/Helpers/AgentTestFixtures.cs` | Shared test helpers (BasicAgent, BasicRequest, Opts, ...) |
| `src/Diva.Agents/Registry/IReadableAgentRegistry.cs` | Read-only registry interface used by supervisor stages (ISP/DIP) |
| `src/Diva.Agents/Registry/ICapabilityScoringService.cs` | Swappable capability scorer interface |
| `src/Diva.Agents/Registry/CapabilityScoringService.cs` | Default keyword intersection scorer |
| `src/Diva.Agents/Supervisor/Decompose/` | Decomposition strategies: `IDecompositionStrategy`, `SingleTaskStrategy`, `LlmDecompositionStrategy`, `DecompositionStrategySelector` |
| `src/Diva.Infrastructure/LiteLLM/IToolSelectionStrategy.cs` | Tool pre-filter strategy interface |
| `src/Diva.Infrastructure/LiteLLM/LlmToolSelector.cs` | LLM-based tool pre-filter (fires when tools > `SemanticToolFilterThreshold`) |
| `src/Diva.Infrastructure/Synthesis/ResponseSynthesizer.cs` | `IResponseSynthesizer` — multi-agent result synthesis |
| `tests/Diva.Agents.Tests/Helpers/ContextWindowTestHelpers.cs` | `NoOpCtx()` NSubstitute mock for `IContextWindowManager` |
| `src/Diva.Host/Controllers/AuthController.cs` | SSO login, callback (issues local JWT), logout routing, `/api/auth/me` |
| `src/Diva.Infrastructure/Auth/LocalAuthService.cs` | `IssueJwt` + `IssueSsoJwt` — local JWT issuance for both local and SSO users |
| `src/Diva.Infrastructure/Auth/TenantContextMiddleware.cs` | Validates Bearer + `X-API-Key`, populates `HttpContext.Items["TenantContext"]`; bypass list for `/api/auth/*` |
| `src/Diva.Infrastructure/Auth/AesCredentialEncryptor.cs` | AES-256-GCM encryption for MCP credential secrets; ephemeral key fallback for dev |
| `src/Diva.Infrastructure/Auth/CredentialResolver.cs` | Resolves `CredentialRef` → decrypted API key; 2-min memory cache; singleton-safe via `IDatabaseProviderFactory` |
| `src/Diva.Infrastructure/Auth/PlatformApiKeyService.cs` | SHA-256 hashed platform API keys (`diva_` prefix); create/validate/revoke/rotate |
| `src/Diva.Core/Configuration/ICredentialEncryptor.cs` | Encrypt/Decrypt interface for credential vault |
| `src/Diva.Core/Configuration/ICredentialResolver.cs` | ResolveAsync interface + `ResolvedCredential` record (ApiKey, AuthScheme, CustomHeaderName) |
| `src/Diva.Core/Configuration/IPlatformApiKeyService.cs` | Platform API key management interface + DTOs |
| `src/Diva.Host/Controllers/CredentialsController.cs` | `/api/admin/credentials` CRUD (create, update with re-encrypt, delete) |
| `src/Diva.Host/Controllers/ApiKeysController.cs` | `/api/admin/api-keys` CRUD (create, list, revoke, rotate) |
| `src/Diva.Infrastructure/LiteLLM/AgentDelegationTool.cs` | `AIFunction` subclass — synthetic tool representing a peer agent |
| `src/Diva.Infrastructure/LiteLLM/AgentToolProvider.cs` | Builds `AgentDelegationTool` list from `DelegateAgentIdsJson` |
| `src/Diva.Infrastructure/LiteLLM/AgentToolExecutor.cs` | Executes `call_agent_*` tool calls with depth guard + `SubAgentTimeoutSeconds` timeout + SSO propagation + parent-vs-local cancellation distinction |
| `src/Diva.Core/Configuration/IAgentDelegationResolver.cs` | Cross-project abstraction for agent lookup + execution |
| `src/Diva.Agents/Registry/DelegationAgentResolver.cs` | Bridges `IAgentDelegationResolver` → `IAgentRegistry` (lazy via `IServiceProvider`) |
| `admin-portal/src/components/DelegateAgentSelector.tsx` | Multi-select agent picker for delegation config |
| `admin-portal/src/components/HookEditor.tsx` | Named behavior toggles for 6 built-in lifecycle hooks; uses synthetic dict keys (`__prompt_guard__` etc.); value-scan detects archetype-sourced entries |
| `admin-portal/src/components/ArchetypeSelector.tsx` | Compact `<Select>` dropdown with category `SelectGroup`s; selected archetype description panel shown inline |
| `admin-portal/src/components/RulePackManager.tsx` | Rule pack list with name search, status/type filters, client-side pagination (10/25/50), badge truncation (+N more), starters unified into filtered list |
| `src/Diva.Core/Configuration/IAgentToolDiscoveryService.cs` | Reusable interface — discovers MCP tools for an agent (used by prompt builder; available for future tool inspection features) |
| `src/Diva.Infrastructure/LiteLLM/AgentToolDiscoveryService.cs` | Implements `IAgentToolDiscoveryService`; 8 s bounded timeout; best-effort (never throws) |
| `src/Diva.TenantAdmin/Services/Enrichers/AgentToolsContextEnricher.cs` | `ISetupAssistantContextEnricher` — injects MCP tools + delegate agent details into prompt builder context |
| `admin-portal/src/components/SsoConfigEditor.tsx` | Full-page SSO provider form at `/settings/sso/new` and `/settings/sso/:id/edit` |
| `src/Diva.Infrastructure/Sessions/SessionTraceWriter.cs` | Captures SSE chunks to DB — tool input/output now stored in full (no truncation caps) |
| `src/Diva.Core/Models/Session/SessionDtos.cs` | Session DTOs — `TurnSummary` now includes `UserMessage`/`AssistantMessage` full text alongside `*Preview` fields |
| `src/Diva.TenantAdmin/Services/TenantSsoConfigService.cs` | Per-tenant SSO config CRUD; `GetAllActiveAsync` returns `(Config, TenantName)` tuples |
| `src/Diva.TenantAdmin/Services/RulePackService.cs` | Rule Pack CRUD + clone + reorder + starter packs + cache |
| `src/Diva.TenantAdmin/Services/RulePackEngine.cs` | Runtime rule evaluation (9 types), regex cache, batched logging, DryRun |
| `src/Diva.TenantAdmin/Services/RulePackConflictAnalyzer.cs` | Internal + cross-pack conflict detection (inject/block, ReDoS, etc.) |
| `src/Diva.Agents/Hooks/BuiltIn/TenantRulePackHook.cs` | Integrates Rule Packs into all supported lifecycle stages, including tool filtering and error recovery |
| `src/Diva.Agents/Hooks/BuiltIn/StaticModelSwitcherHook.cs` | Order=3 hook — applies per-agent `ModelSwitchingJson` config (tool/final/failure/replan phases) |
| `src/Diva.Agents/Hooks/BuiltIn/ModelRouterHook.cs` | Order=4 hook — smart/heuristic model routing via agent Variables (`model_router_mode`) |
| `src/Diva.Core/Configuration/ModelSwitchingOptions.cs` | DTO for `ModelSwitchingJson` — LlmConfigId and model-string fields per iteration phase |
| `src/Diva.Infrastructure/LiteLLM/UnifiedHistoryEntry.cs` | Provider-agnostic history format for cross-provider model switches |
| `src/Diva.Host/Controllers/RulePackController.cs` | 15 REST endpoints for Rule Pack management |

---

## Adding a New Feature to the ReAct Loop

### Checklist

1. **New `AgentOptions` property** → `src/Diva.Core/Configuration/AgentOptions.cs`
2. **Update appsettings** → `src/Diva.Host/appsettings.json` + `appsettings.Development.json`
3. **Implement in `AnthropicAgentRunner`** — all providers share the single `ExecuteReActLoopAsync` loop via `ILlmProviderStrategy`. Provider-specific logic goes in:
   - `AnthropicProviderStrategy` — Anthropic SDK message format
   - `OpenAiProviderStrategy` — IChatClient message format
   The unified ReAct loop handles: plan detection, parallel tool execution, dedup, adaptive re-planning, tool error retry, verification+correction, continuation windows.
4. **Add `internal static` helper** for any testable logic (follow `TruncateResult` / `DeduplicateCalls` / `BuildContinuationContext` pattern)
5. **Unit tests** → `ToolOptimizationTests.cs` or new file in `Diva.Agents.Tests/`
6. **New SSE event type**: add to `AgentStreamChunk.cs` (C#), `api.ts` (TS interface), `AgentChat.tsx` switch
7. **Update `phase-09-llm-client.md`** with new section
8. **Update `changelog.md`**

### `internal static` Helper Pattern

All reusable, testable logic in `AnthropicAgentRunner` is extracted as `internal static` methods at the bottom of the class. `[assembly: InternalsVisibleTo("Diva.Agents.Tests")]` is already set in `Diva.Infrastructure.csproj`. No mocking or infrastructure needed — call directly from xUnit facts.

```csharp
// Pattern: pure function, no dependencies
internal static ReturnType HelperName(InputType input, ...) => ...;

// Tests call directly:
var result = AnthropicAgentRunner.HelperName(input);
Assert.Equal(expected, result);
```

Existing helpers: `TruncateResult`, `DeduplicateCalls`, `IsToolOutputError`, `FilterTools`, `BuildContinuationContext`, `ApplyExecutionModeFilter`.

**Extracted private instance methods (not static, have dependencies on `_logger` / `_agentOpts`):**
- `CallLlmForIterationAsync` — one LLM call (streaming + mid-stream fallback to buffered); returns `LlmCallResult(Response, Error, TextDeltas)`. Applies `LlmStreamIdleTimeoutSeconds` (resets per chunk) for streaming, `LlmTimeoutSeconds` (absolute) for buffered fallback.
- `ExecuteToolPipelineAsync` — full tool pipeline for one iteration; returns `ToolPipelineOutcome(StreamError, ConsecutiveFailures, HadToolErrors, Chunks)`

**DI-injectable interfaces for testing:**
- `IMcpConnectionManager` / `IReActHookCoordinator` — both registered as `Singleton`; optional last ctor params on `AnthropicAgentRunner`; use `Substitute.For<>()` in tests

---

## Test Conventions

### Never Mock the Database

All tests that touch `DivaDbContext` use a real in-memory SQLite connection. See `AnthropicAgentRunnerTests.cs` and `ContextWindowTests.cs` for the pattern:

```csharp
_connection = new SqliteConnection("DataSource=:memory:");
_connection.Open();
_dbOptions = new DbContextOptionsBuilder<DivaDbContext>().UseSqlite(_connection).Options;
using var db = new DivaDbContext(_dbOptions);
db.Database.EnsureCreated();
```

Keep the connection alive (shared) — use a custom `IDatabaseProviderFactory` that returns `new DivaDbContext(_options)` per call so services can use `using var db = factory.CreateDbContext()` without disposing the shared connection.

### Mocking LLM Providers

Use `NSubstitute` for `IAnthropicProvider` and `IOpenAiProvider`. See `AgentTestFixtures.cs` for `BasicAgent()`, `BasicRequest()`, `BasicTenant()`, `AnthropicLlm()`, and `Opts<T>()` helpers.

```csharp
var anthropic = Substitute.For<IAnthropicProvider>();
anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
    .Returns(_ => Task.FromResult(MakeTextResponse("Hello!")));
```

### `NoOpCtx()` for Context Window

Integration tests that don't care about context window compaction use `ContextWindowTestHelpers.NoOpCtx()` — a NSubstitute mock that returns inputs unchanged and records calls. Inject it as the `IContextWindowManager` parameter in `AnthropicAgentRunner`.

### `IAsyncDisposable` for Tests with In-Memory DBs

Always implement `IAsyncDisposable` and dispose the `SqliteConnection` in `DisposeAsync`. xUnit calls this automatically.

---

## SSE Event Types — Master List

All event types emitted by `InvokeStreamAsync`. Maintain this when adding new types.

| Type | When | Key fields |
|------|------|-----------|
| `tools_available` | Once, before iter 1 | `ToolCount`, `ToolNames` |
| `plan` | Iter 1 if ≥2 numbered steps detected | `PlanText`, `PlanSteps` |
| `plan_revised` | After 2 consecutive tool failures | `PlanText`, `PlanSteps` |
| `iteration_start` | Start of each inner iteration | `Iteration` (globally unique) |
| `model_switch` | Model changed between iterations | `Iteration`, `FromModel`, `ToModel`, `FromProvider`, `ToProvider`, `Reason` |
| `thinking` | LLM text output in an iteration | `Iteration`, `Content` |
| `tool_call` | Before tool execution | `Iteration`, `ToolName`, `ToolInput` |
| `tool_result` | After tool execution | `Iteration`, `ToolName`, `ToolOutput` |
| `continuation_start` | Window > 0 start | `ContinuationWindow` (1-based) |
| `correction` | Verification triggers re-iteration | — |
| `final_response` | Accepted response | `Content`, `SessionId` |
| `verification` | After verification | `Verification` |
| `rule_suggestion` | After rule extraction | `FollowUpQuestions` |
| `error` | LLM / stream error | `ErrorMessage` |
| `done` | Always last | `ExecutionTime`, `SessionId` |

**Important:** `Iteration` uses a globally-unique counter (`iterationBase + i + 1`) that never resets across continuation windows. The frontend (`itersRef.find(i => i.number === chunk.iteration)`) depends on uniqueness to match chunks to the correct iteration slot.

## Agent Test UI Notes

- `AgentChat.tsx` now has a `Detailed` toggle that exposes a live timeline of SSE events during streaming.
- In detailed mode, thinking text is not truncated, tool input/output panels are not height-limited, and completed iteration traces are shown expanded.
- Agent responses are rendered as sanitized HTML using `DOMPurify`; user messages and error messages remain plain text.
- When adding new response content that relies on markup, ensure it still degrades acceptably if rendered as plain text elsewhere in the platform.

---

## Shared State in the ReAct Loop

All state lives in `ExecuteReActLoopAsync`. Provider-specific message history (`List<Message>` or `List<ChatMessage>`) is encapsulated inside the `ILlmProviderStrategy` implementation.

```
toolsUsed, toolEvidence    — preserved cross-window (evidence accumulation)
finalResponse              — last non-empty LLM text response seen
streamError                — set on LLM call failure; stops the loop
maxIterations              — from definition or 10
maxContinuations           — from definition or AgentOptions
completedNaturally         — true when inner loop breaks via natural exit
iterationBase              — advances by maxIterations on each continuation

planEmitted, consecutiveFailures, hadToolErrors, executionLog   — reset per window
verificationRetries, lastVerification                           — reset per window
lastIterationHadToolCalls  — reset per window; set true after AddToolResults, false after nudge fires
maxTokensNudgeRetries      — reset per window to 1; decremented on each max_tokens nudge; 0 = accept partial

currentModel, currentProvider, currentEndpoint  — mutable; updated on each model switch
fallbackModel, fallbackProvider, fallbackEndpoint  — reset per window; tracks pre-switch model for Scenario 3 recovery
```

**Hook context state signals (set by runner, read by model-switching hooks):**
```
hookCtx.IsFinalIteration         — bool; true at start of no-tool-calls branch
hookCtx.LastHadToolCalls         — bool; true after AddToolResults in tool branch
hookCtx.LastIterationResponse    — string; LLM text from the most recent tool-calling iteration; "" on iter 1
hookCtx.ModelSwitchReason        — string; set by hooks ("rule_pack" | "static_config" | "smart_router" | "failure_upgrade")
hookCtx.ReplanConfigId           — int?; LlmConfigId for replan call (set by StaticModelSwitcherHook)
hookCtx.ReplanModel              — string?; model ID for replan call (set by StaticModelSwitcherHook)
```

The strategy instance holds the internal message list, tools, and last LLM response. It is created once per call in `InvokeStreamAsync` and passed to `ExecuteReActLoopAsync`. `SetModel` mutates the active model in-place (same provider). `ExportHistory`/`ImportHistory` transfer message history across providers.

---

## Context Window Integration Points

| Site | Method | Location |
|------|--------|----------|
| Point B — after history load | `CompactHistoryAsync` | `InvokeStreamAsync` (once, before creating strategy) |
| Point A — main loop top | `strategy.CompactHistory()` | `ExecuteReActLoopAsync` inner loop start |
| Point A — re-plan | `strategy.CompactHistory()` | `ExecuteReActLoopAsync` re-plan block (≥2 consecutive failures) |
| Point A — continuation boundary | `strategy.PrepareNewWindow()` | `ExecuteReActLoopAsync` continuation window > 0 setup |

The strategy delegates to `MaybeCompactAnthropicMessages` or `MaybeCompactChatMessages` depending on provider. When adding a new LLM call site, call `strategy.CompactHistory()` before it.

---

## Anthropic SDK Gotchas

| Pitfall | Correct approach |
|---------|-----------------|
| `TextContent` ambiguous | Qualify as `Anthropic.SDK.Messaging.TextContent` |
| `MCP TextContent` | `ModelContextProtocol.Protocol.TextContentBlock` |
| `ToolResultContent.Content` | `List<ContentBase>` not `string` |
| `StopReason` | Plain string `"tool_use"` / `"end_turn"` — not an enum |
| `MessageParameters.Tools` | `IList<Anthropic.SDK.Common.Tool>` — not `List<Anthropic.SDK.Messaging.Tool>` |
| `callResult.IsError` | `bool?` — compare with `== true` not directly in `||` |

---

---

## max_tokens Handling

When `UnifiedLlmResponse.StopReason == "max_tokens"`:

1. `hookCtx.WasTruncated = true`
2. `OnError` hook pipeline is invoked with `InvalidOperationException("LLM output truncated at max_tokens …")`
3. If hook returns `Abort` **or** `maxTokensNudgeRetries <= 0` → accept partial response (`completedNaturally = true; break`)
4. Otherwise: `maxTokensNudgeRetries--`, inject `MaxTokensNudgePrompt` as user turn, continue

`maxTokensNudgeRetries` starts at 1 and resets at each continuation window boundary. This means at most **1 nudge per window** before partial output is accepted.

`hookCtx.WasTruncated` is reset to `false` on the accept path and on every normal (`end_turn`) iteration. `OnBeforeIteration` hooks can read it on the *next* iteration to inject "be concise" instructions into `SystemPrompt`.

---

## MCP Server (Phase 23)

Diva exposes a built-in MCP server at `/mcp/diva` (Diva.Host embedded mode) or as a standalone
`diva-fs-mcp.exe` (stdio or HTTP, optionally a Windows Service).

**Embedded endpoint:** `http://localhost:5062/mcp/diva` — requires JWT or `X-API-Key` auth.
`TenantContext` is injected automatically via `McpServerContext.FromHttpContext`.

**Standalone modes:**
- `diva-fs-mcp.exe` — stdio transport (default, for Claude Desktop / agent bindings)
- `diva-fs-mcp.exe --http` — HTTP transport at configurable port (Windows Service / IIS)
- `DIVA_FS_MCP_PORT` env var — alternative to `--http` flag

**Tools (12):** `read_file`, `read_pdf`, `get_image_info`, `read_image`, `list_directory`,
`get_file_info`, `search_files`, `get_allowed_roots`, `write_file`, `create_directory`,
`delete_file`, `move_item`

**Agent binding — embedded:**
```json
{ "name": "fs", "endpoint": "http://localhost:5062/mcp/diva", "transport": "http", "passTenantHeaders": true }
```

**Agent binding — standalone stdio:**
```json
{ "name": "fs", "command": "C:\\DivaFsMcp\\diva-fs-mcp.exe", "transport": "stdio" }
```

**Key config (`appsettings.json → FileSystem:`):**
- `AllowedBasePaths` — **must be set in production** (empty = all paths in dev)
- `EnabledTools` — empty = all tools; list names to restrict
- `PdfEnabled`, `ImagesEnabled`, `TextEnabled` — per-type feature flags
- `AllowWrites` — must be `true` to enable write/delete/move tools
- `StandaloneApiKey` — HTTP mode auth key (empty = no auth)

**New agent tool type pattern:** Implement `IDivaMcpToolType` + mark class with
`[McpServerToolType]`, register via `.WithDivaMcpTools<T>()`. Each call to `WithDivaMcpTools`
adds a new tool group to the single shared MCP server.

---

## Vision Pipeline (Local LLM Endpoints)

Local OpenAI-compatible endpoints (LM Studio, Ollama / llama.cpp) **cannot** process images
in follow-up tool-result messages — llama.cpp returns 400 for multi-turn image injection.

### How it works

1. `view_image` MCP tool reads the image, resizes to `Base64MaxDimensionPx` (default 2048 px),
   re-encodes as JPEG Q92 (PNG kept as-is), returns JSON with `imageBase64` + `imageMediaType`.
2. `ToolExecutor.ExtractEmbeddedImageParts` detects these fields and promotes them to
   `ImageContentPart` objects (stored on `UnifiedToolResult.ContentParts`).
3. After each tool iteration, `AnthropicAgentRunner` checks `strategy.UseVisionSummarization`.
   - **`OpenAiProviderStrategy`** — `UseVisionSummarization = true` when `_currentEndpoint` is set
     (i.e. any custom local endpoint). Makes two focused `HttpClient` calls per image:
     - Pass 1: "Objects & Inventory" — every physical item with type, brand, color, position
     - Pass 2: "Text & Labels" — all visible text, prices, signs, transcribed exactly
   - **`AnthropicProviderStrategy`** — `UseVisionSummarization = false`; images injected natively
     as `ImageContent` blocks (Anthropic handles multi-turn images correctly).
4. The combined text description replaces the image in the tool result so the main LLM receives
   prose, not pixels.

### LmStudioVisionPolicy

`PipelinePolicy` (BeforeTransport) that percent-decodes base64 payloads in data URIs.
.NET's `Uri` class encodes `+→%2B`, `=→%3D`, `/→%2F` when `DataContent` is built from bytes —
LM Studio returns `{"error":"Invalid url."}` on percent-encoded payloads.
The policy finds `;base64,PAYLOAD"` segments and applies `Uri.UnescapeDataString`.
The `data:TYPE;base64,` prefix is preserved — LM Studio requires it.

### Key gotchas

| Pitfall | Detail |
|---------|--------|
| Raw base64 without prefix | LM Studio returns `{"error":"Invalid url."}`. Always use `data:TYPE;base64,...` format. |
| Percent-encoded base64 | `+`, `=`, `/` must NOT be percent-encoded. `LmStudioVisionPolicy` fixes this for the ME.AI path; `SummarizeImageAsync` uses raw `HttpClient` to avoid it entirely. |
| 1×1 pixel images | Model returns `"Invalid image detected at index 0"` — use real images for testing. |
| `UseVisionSummarization` guard | Returns `false` when `_currentEndpoint` is null. If the agent's LLM config has no endpoint (Anthropic native), summarization is skipped and images are injected directly — correct for Anthropic, wrong for a misconfigured local endpoint. |
| JPEG re-encoding | `ImageReader` converts WebP/BMP/TIFF/GIF → JPEG. llama.cpp rejects WebP natively with `400 url must be a base64 encoded image`. |
| max_tokens per pass | Each vision pass allows 2048 output tokens. Dense scenes may be truncated. The two-pass approach doubles effective coverage (objects + text separately). |

### Config

```json
"Image": {
  "Base64MaxDimensionPx": 2048,   // resize cap before vision encoding (0 = no resize)
  "ImagesEnabled": true
}
```

Override per `view_image` call via `maxDimensionOverride` parameter (agent can pass higher value for dense scenes).

### Diagnostic endpoints (temporary)

```
GET  /api/debug/vision-probe?endpoint=&model=   # five-format probe (text, PNG/JPEG × data-URI/raw)
POST /api/debug/vision-probe/summarize           # two-pass summarize with caller-supplied base64
```

Both are `[AllowAnonymous]` and bypass `TenantContextMiddleware`. Delete once vision is confirmed stable.

### Test script

```powershell
.\tools\test-vision-summarize.ps1 -ImagePath "C:\path\to\photo.jpg" `
    -LlmEndpoint "http://10.0.0.172:1234" -Model "google/gemma-4-e4b"
```

---

## MCP Credential Vault & Tool Call Authentication

MCP tool bindings support three authentication methods, resolved per-request in `McpConnectionManager`:

### Auth flow priority (HTTP/SSE transport)

1. **SSO Bearer token** — if `binding.PassSsoToken=true` AND `TenantContext.AccessToken` is present → `Authorization: Bearer <sso-token>` + tenant headers
2. **Credential vault fallback** — if `binding.CredentialRef` is set AND no SSO Authorization header was injected → resolve from `McpCredentialEntity` via `ICredentialResolver` → inject based on `AuthScheme`:
   - `Bearer` → `Authorization: Bearer <key>`
   - `ApiKey` → `X-API-Key: <key>`
   - `Custom` → `<CustomHeaderName>: <key>`
3. **Tenant headers only** — if `binding.PassTenantHeaders=true` but no token/credential → `X-Tenant-Id`, `X-Site-ID`, `X-Correlation-ID`

### Stdio transport credential injection

For stdio MCP servers, the resolved credential API key is injected as `MCP_API_KEY` environment variable.

### Scheduled task / background runs

`HttpContext` is null during scheduled execution. `McpConnectionManager.ConnectAsync` accepts a `TenantContext? fallbackTenant` parameter — `AnthropicAgentRunner` passes `tenant` at both call sites (initial connect + stale session reconnect). The headers factory uses `ctx?.TryGetTenantContext() ?? fallbackTenant` so tenant headers and credential auth flow correctly even without an HTTP request.

### Credential storage

- Secrets encrypted at rest with AES-256-GCM (`AesCredentialEncryptor`)
- Layout: `[12-byte nonce][ciphertext][16-byte tag]` → base64
- Master key: `Credentials:MasterKey` in appsettings (base64-encoded, 32 bytes)
- **If `MasterKey` is empty**, an ephemeral random key is generated per startup (dev only) — credentials encrypted with it are lost on restart
- 2-minute in-memory cache in `CredentialResolver` (singleton-safe via `IDatabaseProviderFactory`)

### Platform API keys

For non-SSO users (service accounts, CI pipelines, scheduled tasks):
- Keys have `diva_` prefix + 32-byte random, stored as SHA-256 hash (one-way)
- `X-API-Key` header → `TenantContextMiddleware` validates before Bearer check
- Scope: `FullAccess` or `AgentInvoke`; optional `AllowedAgentIds` restriction
- Admin UI: `/settings/api-keys` (raw key shown once on creation)
- Credential vault UI: `/settings/credentials`

### McpToolBinding fields

| Field | Type | Purpose |
|-------|------|---------|
| `CredentialRef` | `string?` | Name of `McpCredentialEntity` to resolve for this binding |
| `PassSsoToken` | `bool` | Forward caller's SSO Bearer token to the MCP server |
| `PassTenantHeaders` | `bool` | Forward X-Tenant-Id, X-Site-ID headers |

### Logging

`McpConnectionManager` emits structured logs at each decision point:
- `Debug`: credential resolution start, headers factory invocation (source: HttpContext/fallbackTenant/none), tenant-only injection, SSO auth already present (skipping credential)
- `Information`: credential resolved (scheme), SSO Bearer token used, credential injected (Bearer/ApiKey/Custom/MCP_API_KEY env)
- `Warning`: credential not resolved (missing/inactive/expired), no resolver registered, no auth at all, unknown scheme fallback

---

## Per-Iteration Model Switching

The ReAct loop supports switching LLM model (and optionally provider) between iterations to reduce token cost. Three hook layers apply in priority order — first hook to set either override wins; subsequent hooks skip via `HasOverrideAlready` guard.

| Priority | Source | Hook | How to configure |
|---|---|---|---|
| 1 (highest) | Rule Pack `model_switch` rule | `TenantRulePackHook` (Order=2) | Admin portal → Rule Packs |
| 2 | Per-agent `ModelSwitchingOptions` | `StaticModelSwitcherHook` (Order=3) | Agent definition → `ModelSwitchingJson` |
| 3 | Smart auto-router | `ModelRouterHook` (Order=4) | Agent Variables: `model_router_mode` |

### ModelSwitchingOptions (per-agent JSON)

Stored in `AgentDefinitionEntity.ModelSwitchingJson`. LlmConfigId fields (preferred) reference `TenantLlmConfigEntity` or `GroupLlmConfigEntity` and support cross-provider switches. Model string fields are same-provider shorthand.

| Field | Phase |
|---|---|
| `ToolIterationLlmConfigId` / `ToolIterationModel` | Iterations that call tools |
| `FinalResponseLlmConfigId` / `FinalResponseModel` | Final synthesis iteration |
| `ReplanLlmConfigId` / `ReplanModel` | Adaptive re-planning calls |
| `UpgradeOnFailuresLlmConfigId` + `UpgradeAfterFailures` | Escalate after N consecutive failures (default 2) |
| `FallbackToOriginalOnError` | Restore original model if API call fails after switch (default `true`) |

### Rule Pack `model_switch` rule

`HookPoint: OnBeforeIteration`. Field mapping:
- `Instruction` — target model ID (same-provider)
- `ToolName` — target LlmConfigId integer string (cross-provider; takes precedence over Instruction)
- `Replacement` — optional max_tokens integer string
- `Pattern` — optional regex; matched against `MatchTarget` text (see below); rule inactive if no match
- `MatchTarget` — `"query"` (default) or `"response"` (see below)

**`MatchTarget` field:**
- `"query"` — Pattern matched against the original user query (existing behaviour, default).
- `"response"` — Pattern matched against `AgentHookContext.LastIterationResponse`, i.e. the LLM's text output from the previous iteration. Empty on iteration 1 — rules with a non-blank pattern are silently skipped on the first iteration.
- Blank Pattern with `MatchTarget = "response"` fires on every iteration from iteration 2 onward.
- Use case: switch to a stronger model when the agent announces it is about to perform a specific action (e.g. `"I will send the email"`) without needing that intent to be in the original query.

**`LastIterationResponse` on `AgentHookContext`:**
Set by the runner immediately after any iteration that returned tool calls. Available to all `OnBeforeIteration` hooks on the following iteration. Empty string on iteration 1 and on every final (no-tool-calls) iteration.

Rule Pack rules override agent config. Two model_switch rules in the same pack without `StopOnMatch=true` produce a `ConflictWarning`.

### ModelRouterHook Variables

| Variable | Values |
|---|---|
| `model_router_mode` | `smart` \| `tool_downgrade_only` \| `off` (default `off`) |
| `model_router_fast_model` / `model_router_fast_config_id` | Cheap model for tool-calling iterations |
| `model_router_strong_model` / `model_router_strong_config_id` | Quality model for final/recovery iterations |

`smart` routing table: `ConsecutiveFailures >= 2` → strong (`failure_upgrade`); `__is_final_iteration` → strong (`smart_router`); `__last_had_tool_calls` → fast (`smart_router`); `WasTruncated` → fast (`smart_router`). `tool_downgrade_only` only applies the tool-call→fast rule.

### Cross-provider switching

When `LlmConfigIdOverride` resolves to a different `Provider` or `Endpoint`:
1. `strategy.ExportHistory()` → `List<UnifiedHistoryEntry>` (text, tool calls, tool results)
2. New strategy of the target provider type is created
3. `newStrategy.ImportHistory(history, systemPrompt, allMcpTools)` — history and tools transferred

### Failure fallback scenarios

| Scenario | Behaviour |
|---|---|
| Config resolution null/error | Skip switch, keep current model, log warning |
| ExportHistory/ImportHistory throws | Abort swap, keep current strategy, log warning |
| API call fails on switched model | If `FallbackToOriginalOnError=true`: `strategy.SetModel(fallbackModel)`, continue |

---

## Active Deferred Items

| Item | Why deferred | How to undefer |
|------|-------------|----------------|
| Domain MCP tools (Analytics, Reservation) | Need real data backends | Add DB connection + queries in `AnalyticsMcpServer`, `ReservationMcpServer` — see [phase-05-mcp-tools.md](phase-05-mcp-tools.md) |
| Phase 19 Coordinator Sub-Agent Routing | Foundation laid by agents-as-tools; not yet started | See [phase-19-coordinator-sub-agent-routing.md](phase-19-coordinator-sub-agent-routing.md) — `OrchestratorAgent`, `ScopedAgentRegistry`, LLM-based `DecomposeStage` |
| `pipelineStagesJson` / `stageInstructionsJson` UI | DB columns and DTO fields exist but `SupervisorAgent` does not read them — all stages run unconditionally. UI sections removed from `AgentBuilder` and `GroupAgentTemplateBuilder` 2026-04-18. | Wire in Phase 19: filter `_stages` in `SupervisorAgent.ExecuteAsync` by `pipelineStagesJson`; propagate `stageInstructionsJson` via `SupervisorState` to each stage |

**Resolved 2026-04-13 — A2A Discovery:**
- ~~`/.well-known/agent.json` returning 401~~ — `TenantContextMiddleware` now bypasses `/.well-known`; `AgentCardController` also has `[AllowAnonymous]`
- New `GET /.well-known/agents.json` endpoint returns all published agents as an array (spec-compliant `agent.json` singular still exists for default/single-agent lookup)

**Resolved 2026-04-12:**
- ~~Phase 14 A2A Protocol~~ — fully implemented: AgentCard, task lifecycle, hardening (resilience, rate limiting, cleanup), agents-as-tools peer delegation, 49 tests

**Resolved 2026-04-12 — Bug Fixes:**
- ~~Delegation not invoking~~ — `DelegateAgentSelector` stored number IDs, backend expected strings; `AgentDelegationTool.AgentId` was `int` not `string`
- ~~A2A settings showing disabled~~ — `appsettings.json` had `A2A.Enabled: false`
- ~~Missing `TenantLlmConfigs.Name` column~~ — partially-applied migration; fixed via MigFix ALTER TABLE

**Resolved 2026-03-25:**
- ~~SSO login 400 token exchange error~~ — added HTTP Basic Auth header (`client_secret_basic`) alongside form body
- ~~401 "invalid token" after SSO login~~ — local JWT now issued in callback; provider token never stored
- ~~Logout not redirecting to login page~~ — routed through API `/api/auth/logout` → `/api/auth/logout-callback`
- ~~Portal always showing tenant 1 data~~ — `AgentsController` now passes `TenantContext` to all `CreateDbContext()` calls
- ~~`?tenantId=1` ignored for non-tenant-1 users~~ — `EffectiveTenantId` helper enforces JWT tenant in all controllers
- ~~Agents invisible after tenant isolation (TenantId=0 orphans)~~ — `FixOrphanedTenantIds` data migration reassigns to tenant 1
- ~~Manual migrations not applied by `MigrateAsync()`~~ — added Designer.cs files with `[Migration]` attribute

**Resolved 2026-03-24:**
- ~~`RunOpenAiCompatibleAsync` tool error retry~~ — eliminated by unified `ExecuteReActLoopAsync`; `UseFunctionInvocation` removed entirely
- ~~Per-agent UI for context window override~~ — added to `AdvancedConfigPanel` in `AgentBuilder.tsx`
- ~~Per-agent `MaxContinuations` override~~ — added as `AgentDefinitionEntity.MaxContinuations`

---

## Dev Workflow

```bash
# Build
dotnet build Diva.slnx

# Test (all)
dotnet test

# Test (agents only, faster)
dotnet test tests/Diva.Agents.Tests

# EF migration
dotnet ef migrations add <Name> --project src/Diva.Infrastructure --startup-project src/Diva.Host -- --provider SQLite
dotnet ef database update --project src/Diva.Infrastructure --startup-project src/Diva.Host -- --provider SQLite

# Run API
dotnet run --project src/Diva.Host

# Admin portal
cd admin-portal && npm run dev
# or with mock API (no backend needed)
VITE_MOCK=true npm run dev
```

---

## Docs to Keep Updated

| Doc | When to update |
|-----|---------------|
| `docs/changelog.md` | Every completed feature or fix; every new deferred item |
| `docs/agents.md` | When a new pattern is established; when deferred items resolve |
| `docs/phase-09-llm-client.md` | When `AnthropicAgentRunner` gains a new capability |
| `docs/INDEX.md` | When phase status changes; when a new doc is added |
| `CLAUDE.md` | When a new hard rule or gotcha is discovered |
