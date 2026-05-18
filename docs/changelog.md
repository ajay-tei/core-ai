# Diva AI Platform — Changelog

> All changes are in reverse chronological order. Completed items include file paths. Pending items include rationale for deferral.

---

## [2026-05-18] Scheduler — Email Notifications, Run Status Tracking & SuccessKeywords Validation

Three related features shipped together: (1) HTML email notifications for scheduled task run
outcomes, (2) last-run status columns on scheduled tasks and groups for at-a-glance dashboard
visibility, and (3) `SuccessKeywords` response validation — a positive-assertion mechanism that
marks a run as failed if none of the configured keywords appear in the agent's final response.

### Overview

| Feature | Summary |
|---------|---------|
| **Email notifications** | Per-tenant SMTP settings; HTML email with token usage, run duration, agent name, and white-label branding. Sent on run success, failure, or both (configurable). |
| **Last-run status** | `LastRunStatus` / `LastRunAt` / `LastRunError` columns on `ScheduledTasks` and `TenantGroups` — updated after every run; surfaced in Dashboard widget. |
| **SuccessKeywords** | Comma-separated phrases that must appear in the agent's final response text to confirm success. If configured and none match, the run is marked failed with a clear error message. Stored in DB as `FailureKeywords` column (backward-compatible via `[Column("FailureKeywords")]`). |

### New files

| File | Purpose |
|------|---------|
| `src/Diva.Core/Configuration/SmtpOptions.cs` | `SmtpOptions` config class (`Host`, `Port`, `Username`, `Password`, `FromAddress`, `FromName`, `UseSsl`) |
| `src/Diva.Infrastructure/Data/Entities/TenantNotificationSettingsEntity.cs` | EF entity for per-tenant notification settings (`SmtpHost`, `NotifyOnSuccess`, `NotifyOnFailure`, `RecipientEmails`, etc.) |
| `src/Diva.Infrastructure/Notifications/SmtpEmailNotifier.cs` | `IEmailNotifier` + `SmtpEmailNotifier` — builds HTML run-result email; resolves white-label brand name via `ITenantBrandingService`; sends via `System.Net.Mail.SmtpClient` |
| `src/Diva.Infrastructure/Data/Migrations/20260516204503_AddSchedulerNotifications.*` | EF migration: `TenantNotificationSettings` table + `LastRunStatus` / `LastRunAt` / `LastRunError` columns on `ScheduledTasks` and `TenantGroups` |
| `src/Diva.Infrastructure/Data/Migrations/20260517011915_AddLastRunStatus.*` | EF migration: additional last-run columns / index |
| `src/Diva.Infrastructure/Data/Migrations/20260518121520_AddFailureKeywords.*` | EF migration: `FailureKeywords TEXT` column (used as `SuccessKeywords` via EF column attribute) |
| `src/Diva.Tools/Email/` | MCP Email tool server stub |
| `src/Diva.Tools/Scheduler/` | MCP Scheduler tool server stub |

### Modified files

| File | Change |
|------|--------|
| `src/Diva.Core/Models/AgentResponse.cs` | Added `Content` property (final iteration response text) for keyword matching |
| `src/Diva.Infrastructure/Data/DivaDbContext.cs` | `TenantNotificationSettings` DbSet; EF query filter for tenant isolation |
| `src/Diva.Infrastructure/Data/Entities/ScheduledTaskEntity.cs` | `SuccessKeywords` property (mapped to `FailureKeywords` DB column); `LastRunStatus` / `LastRunAt` / `LastRunError` |
| `src/Diva.Infrastructure/Data/Entities/ScheduledTaskRunEntity.cs` | Additional run result fields |
| `src/Diva.Infrastructure/Data/Entities/TenantGroupEntities.cs` | `SuccessKeywords` (same column mapping); `LastRunStatus` / `LastRunAt` / `LastRunError` on group entity |
| `src/Diva.Infrastructure/Data/Migrations/DivaDbContextModelSnapshot.cs` | Updated model snapshot |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Populates `AgentResponse.Content` with final iteration response |
| `src/Diva.Infrastructure/Scheduler/IScheduledTaskService.cs` | `CreateScheduledTaskRequest` / `UpdateScheduledTaskRequest` records: `SuccessKeywords` field |
| `src/Diva.Infrastructure/Scheduler/ScheduledTaskService.cs` | CRUD maps `SuccessKeywords`; updates `LastRunStatus` / `LastRunAt` / `LastRunError` after each run |
| `src/Diva.Infrastructure/Scheduler/SchedulerHostedService.cs` | **SuccessKeywords logic**: captures `lastIterationResponse = response.Content`; after run succeeds, checks each comma-separated keyword (case-insensitive) against the response; marks failed if none match. Same logic in `ExecuteRunAsync` and `ExecuteGroupRunAsync`. Emits `SmtpEmailNotifier` call on run completion. |
| `src/Diva.Host/Controllers/SchedulerController.cs` | `CreateScheduledTaskDto` / `UpdateScheduledTaskDto` / `ScheduledTaskExport`: `SuccessKeywords` field |
| `src/Diva.Host/Program.cs` | Registers `IEmailNotifier` / `SmtpEmailNotifier`; idempotent SQL fallback adds `SuccessKeywords` column and copies from legacy `FailureKeywords` if present |
| `admin-portal/src/api.ts` | `successKeywords?: string` on `ScheduledTask` interface |
| `admin-portal/src/components/ScheduledTasks.tsx` | `successKeywords` state; "Success confirmation keywords" field with help text |
| `admin-portal/src/components/Dashboard.tsx` | Last-run status badges on scheduled task and group cards |
| `docker-compose.yml` | SMTP environment variable pass-through |
| `.env.example` | SMTP env var documentation |
| `tests/Diva.Agents.Tests/SchedulerTests.cs` | Tests for SuccessKeywords pass/fail, email dispatch, last-run status update |

### Behaviour details — SuccessKeywords

```
SuccessKeywords = "email sent, sent successfully, completed"

Agent response contains "email sent" → run marked SUCCESS
Agent response does not contain any keyword → run marked FAILED
  error: "Response did not contain any expected success keyword."

SuccessKeywords = ""  (or null) → keyword check is skipped; run outcome
                                  is determined solely by agent execution result
```

The DB column remains named `FailureKeywords` for backward compatibility. The EF property is
`SuccessKeywords` with `[Column("FailureKeywords")]`. No data loss on upgrade — `Program.cs`
copies any existing `FailureKeywords` values into `SuccessKeywords` via idempotent SQL.

---

## [2026-05-14] Agent Export / Import

Full round-trip portable agent configuration — export an agent (definition + linked business rules)
to a JSON bundle and re-import it into any tenant, with delegate-agent name resolution, overwrite
toggle, and selective rule import.

### New files

| File | Purpose |
|------|---------|
| `src/Diva.Core/Models/AgentExport.cs` | DTOs: `AgentExportBundle`, `AgentExportDefinition`, `AgentExportRule`, `AgentImportOptions`, `AgentImportResult` |
| `src/Diva.Core/Models/IAgentExportService.cs` | Service interface (`ExportAsync` / `ImportAsync`) |
| `src/Diva.Infrastructure/AgentExport/AgentExportService.cs` | Concrete implementation — resolves `DelegateAgentIdsJson` → names on export, names → IDs on import (warns on misses), optional rule import, overwrite support |
| `admin-portal/src/lib/download.ts` | `triggerJsonDownload()` + `readJsonFile<T>()` utilities |
| `admin-portal/src/components/AgentImportDialog.tsx` | File-upload dialog with bundle preview, overwrite/rule toggles, warnings display |
| `tests/Diva.Agents.Tests/AgentExportServiceTests.cs` | 7 xunit tests covering export, delegate resolution, create, overwrite, rule skip, and missing-delegate warnings |

### Modified files

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/AgentsController.cs` | `GET /api/agents/{id}/export` → file download; `POST /api/agents/import` → 201 with `AgentImportResult` |
| `src/Diva.Host/Program.cs` | `AddScoped<IAgentExportService, AgentExportService>()` |
| `admin-portal/src/api.ts` | `AgentExportBundle`, `AgentExportDefinition`, `AgentExportRule`, `AgentImportResult`, `AgentImportOptions` interfaces; `exportAgent()` + `importAgent()` methods |
| `admin-portal/src/components/AgentList.tsx` | Export in row dropdown; Import button near "New Agent" |
| `admin-portal/src/components/AgentBuilder.tsx` | Export button in edit toolbar; Import button in new-agent mode |
| `admin-portal/src/mocks/handlers.ts` | MSW mock handlers for both endpoints |

---

## [2026-05-14] Vision Support for Local LLMs + User Attachments + Agent-Scoped Rule Learning

### Overview

Three related features shipped together: (1) end-to-end vision pipeline for LM Studio / llama.cpp
local endpoints, (2) image/document attachments on the agent invocation request, and (3) rule
learning that uses the calling agent's LLM config instead of the global platform endpoint.

---

### 1. Vision Pipeline — Local LLM Endpoints (LM Studio / llama.cpp)

Local OpenAI-compatible endpoints cannot process images in follow-up tool-result messages
(llama.cpp returns 400 for multi-turn image injection). The fix is a clean single-turn vision
call that summarises the image as text before injecting tool results.

#### New types

| File | Change |
|------|--------|
| `src/Diva.Core/Models/ContentPart.cs` | **New** — abstract `ContentPart` base + `ImageContentPart` (`MediaType`, `Data`, `Url`), `TextContentPart`, `DocumentContentPart` |
| `src/Diva.Infrastructure/LiteLLM/ToolExecutorResult.cs` | **New** — structured `ToolExecutorResult` record (`Output`, `ContentParts`, `Failed`, `Error`); replaces the old value-tuple return |
| `src/Diva.Infrastructure/LiteLLM/LmStudioVisionPolicy.cs` | **New** — `PipelinePolicy` (BeforeTransport) that percent-decodes base64 payloads in data URIs. .NET's `Uri` class encodes `+→%2B`, `=→%3D`, `/→%2F` when `DataContent` is constructed from bytes, making the base64 undecodable. Policy finds `;base64,PAYLOAD"` segments and applies `Uri.UnescapeDataString`. Preserves the `data:TYPE;base64,` prefix — LM Studio requires it. |

#### Tool executor — image extraction

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/ToolExecutor.cs` | Return type changed from `(string, bool, Exception?)` tuple to `ToolExecutorResult`. Added two image extraction paths: (a) MCP native `ImageContentBlock` blocks captured and promoted to `ImageContentPart`; (b) `ExtractEmbeddedImageParts` detects `imageBase64` + `imageMediaType` fields in JSON tool output (produced by `view_image` / `read_image`) and promotes them to `ImageContentPart`, replacing the raw base64 with a placeholder. `UnifiedToolResult` extended with `IReadOnlyList<ContentPart>? ContentParts`. |

#### Strategy interface changes

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/ILlmProviderStrategy.cs` | Added `bool UseVisionSummarization` (default `false`) — true for local endpoints. Added `Task<string> SummarizeImageAsync(ImageContentPart, CancellationToken)` (default returns empty). `Initialize` signature gains `IReadOnlyList<ContentPart>? attachments` optional parameter. `UnifiedToolResult` record extended with `ContentParts` field. |
| `src/Diva.Infrastructure/LiteLLM/OpenAiProviderStrategy.cs` | `UseVisionSummarization` returns `true` when `_currentEndpoint` is set (i.e. custom local endpoint). `SummarizeImageAsync` uses raw `HttpClient` (not ME.AI `DataContent`) to avoid .NET Uri percent-encoding — makes two focused passes: pass 1 "Objects & Inventory" prompt, pass 2 "Text & Labels" prompt; concatenates as `[Objects & Inventory]\n...\n\n[Text & Labels]\n...`. Max tokens 2048 per pass, 90 s timeout. |
| `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs` | Handles `ContentParts` in tool results — builds multi-block content for Anthropic SDK (`ImageContent` + `TextContent`). `Initialize` accepts attachments — prepends image/document blocks before the user text block per Anthropic best practice. |

#### Agent runner changes

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | After each tool iteration: counts image parts across all `UnifiedToolResult` objects. If `UseVisionSummarization && totalImages > 0`: calls `SummarizeImageAsync` per image, combines description with text output as `{output}\n\nVisual content:\n{desc}`; logs description preview (first 800 chars) at Info level. Falls back gracefully on failure (warning log with full HTTP status + body). `ExecuteReActLoopAsync` gains `ResolvedLlmConfig? resolvedLlmConfig` param — threaded from `InvokeStreamAsync` through to rule extraction background task. |

#### FileSystem MCP tool changes

| File | Change |
|------|--------|
| `src/Diva.Tools/FileSystem/FileSystemMcpTools.cs` | `view_image` tool added — reads image from path, applies `ImageOptions` (resize, quality metrics, EXIF, base64 encode), returns JSON with `imageBase64` + `imageMediaType` fields that `ToolExecutor.ExtractEmbeddedImageParts` recognises and promotes. Accepts optional `maxDimensionOverride` parameter. |
| `src/Diva.Tools/FileSystem/Readers/ImageReader.cs` | Added base64 encode path: reads image, optionally resizes to `Base64MaxDimensionPx` max, saves as PNG (if PNG) or JPEG Q92 (all others including WebP/BMP/TIFF — llama.cpp rejects WebP). Stores result in `imageBase64` + `imageMediaType` on `ImageInfoResult`. |
| `src/Diva.Tools/FileSystem/FileSystemOptions.cs` | `Base64MaxDimensionPx` default raised 1568 → 2048. |
| `src/Diva.Host/appsettings.json` | Added `Image.Base64MaxDimensionPx: 2048` to Image config block (now configurable without recompile). |

---

### 2. User Attachments on Agent Invocation

Users can now attach images or documents to the initial agent request. Attachments are injected
into the first user message before the ReAct loop starts.

| File | Change |
|------|--------|
| `src/Diva.Core/Models/AgentRequest.cs` | Added `IReadOnlyList<ContentPart> Attachments { get; init; } = []` |
| `src/Diva.Host/Controllers/AgentsController.cs` | `AgentInvokeRequest` record gains `List<ContentPart>? Attachments`; passed through to `AgentRequest` |
| `src/Diva.Infrastructure/LiteLLM/ILlmProviderStrategy.cs` | `Initialize` gains `attachments` parameter |
| `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs` | Injects attachment content blocks (images, documents) before the user text message |
| `src/Diva.Infrastructure/LiteLLM/OpenAiProviderStrategy.cs` | Attachment images serialised as `image_url` content parts in the OpenAI message format |

---

### 3. Rule Learning — Agent-Scoped LLM Config

Rule extraction previously called the global platform endpoint (`ILlmConfigResolver.ResolveAsync(tenantId:0)`).
It now uses the calling agent's resolved LLM config so extraction uses the same provider/model/endpoint as the agent.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Learning/IRuleLearningService.cs` | `ExtractRulesFromConversationAsync` gains optional `ResolvedLlmConfig? agentConfig` |
| `src/Diva.Infrastructure/Learning/RuleLearningService.cs` | Threads `agentConfig` through to extractor |
| `src/Diva.Infrastructure/Learning/LlmRuleExtractor.cs` | `ExtractAsync` accepts optional `agentConfig`; uses it when non-null, falls back to global resolver when null |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Captures `resolvedLlmConfig` at start of `InvokeStreamAsync`; passes to `ExecuteReActLoopAsync`; background rule extraction task receives the agent's config |

---

### 4. Diagnostic Tools (temporary, delete once vision confirmed stable)

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/VisionProbeController.cs` | **New** — `GET /api/debug/vision-probe` tests five image formats against any endpoint/model (text-only, PNG data URI, PNG raw, JPEG data URI, JPEG raw). `POST /api/debug/vision-probe/summarize` replicates `SummarizeImageAsync` two-pass logic with a caller-supplied base64 image; returns `objects`, `text`, and `combined` fields separately. Both endpoints are `[AllowAnonymous]`. |
| `src/Diva.Infrastructure/Auth/TenantContextMiddleware.cs` | Added `/api/debug/vision-probe` and `/api/debug/vision-probe/summarize` to `BypassPaths` |
| `tools/test-lmstudio-vision.ps1` | New — direct LM Studio vision format probe via PowerShell `Invoke-RestMethod` |
| `tools/test-vision-curl.ps1` | New — same probe via `curl.exe` (useful when Invoke-RestMethod mangles encoding) |
| `tools/test-vision-summarize.ps1` | New — end-to-end test: reads a local image file, runs the format probe, then calls `/api/debug/vision-probe/summarize` and displays the two-pass description |

---

## [2026-05-12] Runtime Bug Fixes — LLM Config Resolution & ToolBindings Deserialization

### Root Cause

`docker-compose.yml` changed the API key env var from `ANTHROPIC_API_KEY` to `LLM_DIRECT_API_KEY`
(with `:-no-key` fallback). Services that read `LlmOptions.DirectProvider.ApiKey` directly received
`"no-key"` at runtime instead of the DB-stored platform API key. Additionally, `ToolBindings` in the
DB stores full `McpToolBinding` JSON objects but agents were attempting to deserialize them as `string[]`.

### Fixes

| File | Change |
|------|--------|
| `src/Diva.Agents/Registry/DynamicReActAgent.cs` | Fixed `GetCapability()` ToolBindings deserialization — `ToolBindings` column stores `McpToolBinding` objects `[{"name":"…","command":"…",…}]`; now uses `JsonNode.Parse().AsArray()` to extract the `name` property instead of `JsonSerializer.Deserialize<string[]>()` which threw `JsonException`. Added `using System.Text.Json.Nodes;` |
| `src/Diva.Agents/Workers/RemoteA2AAgent.cs` | Same ToolBindings deserialization fix as `DynamicReActAgent` |
| `src/Diva.Infrastructure/Optimization/TurnScoringService.cs` | Injected `ILlmConfigResolver`; added `ResolveLlmConfigAsync(agentId, ct)` that looks up the agent's `TenantId`/`LlmConfigId`/`ModelId` from DB and calls `_resolver.ResolveAsync()`. Both `CallAnthropicAsync` and `CallOpenAiCompatibleAsync` now accept `ResolvedLlmConfig` param instead of reading `LlmOptions.DirectProvider` directly. Falls back to global `DirectProvider` only if DB lookup fails |
| `src/Diva.Infrastructure/Learning/LlmRuleExtractor.cs` | Removed `IOptions<LlmOptions>` dependency; injected `ILlmConfigResolver`; `ExtractAsync` now calls `_resolver.ResolveAsync(tenantId: 0, null, null, ct)` to resolve the platform-level API key from DB. Both `CallAnthropicAsync` and `CallOpenAiCompatibleAsync` accept `ResolvedLlmConfig` param |

---

## [2026-05-10] Phase 24 Addendum — Quick Prompt Fix, Per-Agent Optimization Overrides, LLM Merge Improvements

### Quick Prompt Fix (`POST /api/agents/{id}/prompt/improve`)

Admin-facing AI assistant that applies a free-text instruction to the current system prompt and returns
a revised version for review before saving. Reuses the optimization LLM pipeline and the same token
budget as Smart Merge (`MergeMaxTokens`).

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Optimization/IOptimizationLlmAnalyzer.cs` | Added `QuickImprovePromptAsync(currentPrompt, instruction, agentDef, ct)` to interface |
| `src/Diva.Infrastructure/Optimization/OptimizationLlmAnalyzer.cs` | Implemented `QuickImprovePromptAsync`; added `QuickImproveSystemMessage` constant; added `BuildQuickImprovePrompt` (injection-safe delimited framing); added `ResolveMergeMaxTokens(agentDef)` for per-agent token budget override |
| `src/Diva.Host/Controllers/AgentsController.cs` | Added `POST /api/agents/{id}/prompt/improve` endpoint (`ImprovePromptRequest` record); injects `IOptimizationLlmAnalyzer` |
| `admin-portal/src/components/PromptQuickFixDialog.tsx` | **New file** — fullscreen two-column dialog: instruction textarea + current prompt reference (left) | AI-revised prompt (editable before accept, right); phase machine (`input → loading → preview`); ⌘↵/Ctrl+↵ shortcut; char-count diff display; real backend error surfacing |
| `admin-portal/src/components/AgentBuilder.tsx` | Added "Quick Fix" amber-Sparkles button in System Prompt card header (only when agent exists); wires `onAccept` to `set("systemPrompt", improved)` |
| `admin-portal/src/api.ts` | Added `improvePrompt(id, instruction)` → `POST /api/agents/{id}/prompt/improve` |

### Per-Agent Optimization Token Override

New `OptimizationOverrideOptions` JSON blob on `AgentDefinitionEntity` allows per-agent override of
`MergeMaxTokens` (covers both Smart Merge and Quick Prompt Fix) and `AnalyzerMaxTokens`.

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/AgentOptions.cs` | Added `MergeMaxTokens = 8192` to `OptimizationOptions`; added `OptimizationOverrideOptions { MergeMaxTokens?, AnalyzerMaxTokens? }` |
| `src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs` | Added `OptimizationOverrideJson` nullable string column |
| `src/Diva.Infrastructure/Data/Migrations/20260510150000_AddOptimizationOverrideJson.cs` | EF migration — `AddColumn<string>("OptimizationOverrideJson", "AgentDefinitions", nullable: true)` |
| `src/Diva.Infrastructure/Data/Migrations/20260510150000_AddOptimizationOverrideJson.Designer.cs` | Migration Designer.cs snapshot |
| `src/Diva.Infrastructure/Data/Migrations/DivaDbContextModelSnapshot.cs` | Added `OptimizationOverrideJson` property to `AgentDefinitionEntity` block |
| `src/Diva.Host/appsettings.json` | Added `Agent.Optimization.MergeMaxTokens: 8192` |
| `src/Diva.Host/Controllers/AgentsController.cs` | Reads/writes `OptimizationOverrideJson` in agent save and load paths |
| `admin-portal/src/api.ts` | Added `optimizationOverrideJson?` to `AgentDefinition` interface |
| `admin-portal/src/components/AgentBuilder.tsx` | Added "Optimization Override" section in Advanced Config — Merge Token Limit + Analyzer Token Limit inputs |

### Optimization Suggestions — Two-Tab Redesign + LLM Merge Apply Path

| File | Change |
|------|--------|
| `admin-portal/src/components/AgentOptimizationSuggestions.tsx` | **New file** — replaces flat table with two independent tabs: "System Prompt" (LLM merge via `mergePrompt` → preview → `applyMerged`) and "Agent Configuration" (bulk `applySuggestion`); per-tab checkbox selection; single-row Apply routes through the correct path per type; filter bar (status / run / confidence) shared across tabs |
| `src/Diva.Infrastructure/Optimization/OptimizationApplicator.cs` | `ApplyPromptAsync` now uses `IOptimizationLlmAnalyzer.MergePromptAsync` with append fallback on LLM error; `IOptimizationLlmAnalyzer` injected in constructor |
| `tests/Diva.Agents.Tests/Optimization/OptimizationApplicatorTests.cs` | Added `IOptimizationLlmAnalyzer` mock with default merge simulation; updated prompt assertions; added `LlmMergeFails_FallsBackToAppend` test |

### Optimizer Session Mode — UX + Bug Fixes

| File | Change |
|------|--------|
| `admin-portal/src/components/AgentOptimizer.tsx` | **New file** — fixed `?sessionId=` URL navigation (uses `getOptimizationRunsBySession` to avoid agentId mismatch); replaced subtle banner with full `SessionAnalysisPanel` card (loading / no-prior-run with inline trigger / in-progress / completed / failed states); hid generic "Run Analysis" card in session mode; fixed in-progress banner for aggregate mode only |
| `admin-portal/src/components/AgentOptimizer.tsx` | Fixed `QualityBar` null crash (`null.toFixed()`) — JSON `null` from API passed `!== undefined` guard; changed type to `number \| null \| undefined`, normalized with `v = value ?? null`, guards now use `!= null` |
| `admin-portal/src/components/AgentOptimizer.tsx` | Fixed `verificationFailureRate` and `toolErrorRate` with `?? 0` fallback |

### LLM Timeout Fix

`CallAnthropicAsync` and `CallOpenAiCompatibleAsync` in `OptimizationLlmAnalyzer` previously used the
SDK default network timeout (100 s), causing timeouts when generating long system prompts. Both now
use `LlmOptions.HttpTimeoutSeconds` (600 s).

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Optimization/OptimizationLlmAnalyzer.cs` | `CallAnthropicAsync` — passes `HttpClient { Timeout = HttpTimeoutSeconds }` to `AnthropicClient`; `CallOpenAiCompatibleAsync` — sets `OpenAIClientOptions.NetworkTimeout = HttpTimeoutSeconds` |

---

## [2026-05-06] Scheduled Tasks — Clone, Export, and Import

Full-stack feature across both tenant-scoped and group-scoped scheduled tasks.

### Clone

- **Tenant (`ScheduledTasks.tsx`)**: dropdown "Clone" item pre-fills the create dialog with all fields from the selected task (name prefixed with "Copy of ").
- **Group (`GroupDetail.tsx` → `SchedulesTab`)**: dedicated icon button in the task row opens the create dialog in clone mode.

### Export (JSON download)

- **Export All** toolbar button downloads all visible tasks as a structured JSON envelope:
  ```json
  { "version": "1", "exportedAt": "...", "type": "tenant-schedules"|"group-schedules", "tasks": [...] }
  ```
- **Export** per-row dropdown/icon exports a single task.
- File is named `{tenant|group-id}-schedules-{date}.json` or `{task-slug}-{date}.json`.

### Import (JSON upload with conflict detection)

- **Import** toolbar button opens a dialog accepting pasted JSON or a file upload.
- **Preview** step shows task count and highlights any name conflicts with existing tasks.
- **Skip / Allow** toggle: skip conflicts (default) or overwrite by creating duplicates.
- Backend bulk endpoint validates all tasks and returns `{ created, skipped, skippedNames }`.

### Files changed

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/SchedulerController.cs` | `POST /api/schedules/import` endpoint + `ScheduledTaskExport`, `ScheduleImportRequest`, `ScheduleImportResult` DTOs |
| `src/Diva.Host/Controllers/GroupsController.cs` | `POST /api/platform/groups/{id}/schedules/import` endpoint + group-scoped DTOs |
| `admin-portal/src/api.ts` | `ScheduledTaskExport`, `ScheduleExportEnvelope`, `ScheduleImportRequest/Result`, group variants; `importSchedules()`, `importGroupSchedules()` methods |
| `admin-portal/src/components/ScheduledTasks.tsx` | Clone mode in `TaskDialog`; Export All + per-row Export; Import dialog with preview + conflict UI |
| `admin-portal/src/components/GroupDetail.tsx` | Clone icon button; Export All + per-row Export icon; Import dialog (same UX pattern) |

---

## [2026-05-06] Phase 19 Foundation — Supervisor Pipeline SOLID Refactor + Semantic Tool Pre-Filter

Seven-gap analysis of the Phase 19 supervisor pipeline foundation. All gaps resolved with SOLID-aligned
new components. Semantic tool pre-filtering added for single-agent+tools scenarios (mirrors Phase 19
`LlmDecompositionStrategy` at the intra-agent tool level).

### New files

| File | Description |
|------|-------------|
| `src/Diva.Agents/Supervisor/Stages/AgentContextStage.cs` | New pipeline stage: loads available agents from `IReadableAgentRegistry` into `SupervisorState`; replaces ad-hoc inline loading (SRP fix) |
| `src/Diva.Agents/Registry/IReadableAgentRegistry.cs` | Read-only registry interface for supervisor stages (ISP / DIP fix) |
| `src/Diva.Agents/Registry/ICapabilityScoringService.cs` | Capability scoring interface (OCP fix — swappable scorers) |
| `src/Diva.Agents/Registry/CapabilityScoringService.cs` | Default implementation: cosine-style keyword intersection scorer |
| `src/Diva.Agents/Supervisor/Decompose/` | Decomposition strategy directory: `IDecompositionStrategy`, `SingleTaskStrategy`, `LlmDecompositionStrategy`, `DecompositionStrategySelector` |
| `src/Diva.Core/Models/SupervisorLlmOverride.cs` | `record SupervisorLlmOverride(Provider, Model, Endpoint?)` — shared LLM config carrier for supervisor + tool selector |
| `src/Diva.Infrastructure/Synthesis/ResponseSynthesizer.cs` | `IResponseSynthesizer` + `ResponseSynthesizer` — multi-agent result synthesis extracted from `IntegrateStage` (SRP fix) |
| `src/Diva.Infrastructure/LiteLLM/IToolSelectionStrategy.cs` | Strategy interface for semantic tool pre-filtering |
| `src/Diva.Infrastructure/LiteLLM/LlmToolSelector.cs` | LLM-based tool pre-filter — fires one lightweight LLM call (name+description, no schemas) when tool count exceeds `SemanticToolFilterThreshold`; safe fallback on any error |

### Modified files

| File | Change |
|------|--------|
| `src/Diva.Agents/Registry/DynamicAgentRegistry.cs` | Implements `IReadableAgentRegistry` alongside existing `IAgentRegistry` |
| `src/Diva.Agents/Registry/IAgentRegistry.cs` | Extended: `IReadableAgentRegistry` base interface separation |
| `src/Diva.Agents/Registry/DynamicReActAgent.cs` | Uses capability scoring via `ICapabilityScoringService` |
| `src/Diva.Agents/Workers/AgentCapability.cs` | `AgentCapability` model updated to carry `AgentType` for multi-agent routing |
| `src/Diva.Agents/Supervisor/Stages/DecomposeStage.cs` | Uses `DecompositionStrategySelector` (OCP: open to new strategies without touching stage); adds `LogInformation` before calling strategy |
| `src/Diva.Agents/Supervisor/Stages/CapabilityMatchStage.cs` | Uses `ICapabilityScoringService` via DI; promoted to `LogInformation` with agent+type+caps detail |
| `src/Diva.Agents/Supervisor/Stages/IntegrateStage.cs` | Delegates synthesis to `IResponseSynthesizer` (SRP fix) |
| `src/Diva.Agents/Supervisor/Stages/VerifyStage.cs` | Multi-agent fix: verifies integrated result, not raw parallel sub-task outputs |
| `src/Diva.Agents/Supervisor/SupervisorState.cs` | Added `AvailableAgents` property populated by `AgentContextStage` |
| `src/Diva.Agents/Workers/RemoteA2AAgent.cs` | Fixed pre-existing test failure: raw `A2ASecretRef` used as literal token when no credential resolver present (dev/simple deployments) |
| `src/Diva.Core/Configuration/AgentOptions.cs` | Added `SemanticToolFilterThreshold` (default 8) and `SemanticToolFilterMaxTools` (default 6) |
| `src/Diva.Host/Program.cs` | Registered all Phase 19 foundation services + Phase 23 DI fix (IOfficeReader, IOfficeWriter, FileWriteLock, ScriptThrottle) + `IToolSelectionStrategy`/`LlmToolSelector` |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Optional `IToolSelectionStrategy?` constructor param; injected after `ApplyExecutionModeFilter`, before `strategy.Initialize()` |
| `tests/Diva.Tools.Tests/Helpers/McpToolsTestFixtures.cs` | Fixed Phase 23 constructor — added OfficeReader, OfficeWriter, FileWriteLock, ScriptThrottle params |

### Key design decisions

- `IToolSelectionStrategy` optional param in `AnthropicAgentRunner` (last in optional list) — 3 test files that construct it directly compile unchanged
- `SupervisorLlmOverride` record reused from `Diva.Core.Models` for both `LlmDecompositionStrategy` and `LlmToolSelector`
- Multi-agent compatibility: `DispatchStage` sets `AgentRequest.Query = task.Description` (sub-task), so each worker agent independently filters its own tools against its specific sub-task
- `SemanticToolFilterThreshold = 0` disables the filter; tool count ≤ threshold skips LLM call with no-op
- Logging: `LlmToolSelector: N tools — skipped`, `LlmToolSelector: N tools → calling {Provider} model={Model}`, `LlmToolSelector: selected N/M tools in Xms`

---

## [2026-04-30] Phase 23.1 — Standalone MCP Server JWT Authentication

Upgraded `DivaFsMcpServer` from plaintext static-key comparison to a proper JWT client-credentials
flow. The static `StandaloneApiKey` becomes a one-time master credential used to obtain short-lived
JWTs (default 60 min, HMAC-SHA256). All MCP calls use `Authorization: Bearer <jwt>`. Static key
fallback preserved for backward compatibility and for Diva platform agents that inject credentials
directly via `CredentialRef`. 14 new tests (8 unit, 5 integration via WebApplicationFactory).

### New files

| File | Description |
|------|-------------|
| `tools/DivaFsMcpServer/Auth/StandaloneJwtOptions.cs` | JWT config: SigningKey, Issuer, Audience, TokenExpiryMinutes |
| `tools/DivaFsMcpServer/Auth/StandaloneTokenService.cs` | Issues + validates HMAC-SHA256 JWTs (follows LocalAuthService pattern) |
| `tools/DivaFsMcpServer/StandaloneAuthMiddleware.cs` | Replaces StandaloneApiKeyMiddleware; accepts JWT or static key |
| `tests/DivaFsMcpServer.Tests/Auth/StandaloneTokenServiceTests.cs` | 9 unit tests |
| `tests/DivaFsMcpServer.Tests/Auth/AuthEndpointTests.cs` | 5 integration tests via WebApplicationFactory |

### Modified files

| File | Change |
|------|--------|
| `tools/DivaFsMcpServer/Program.cs` | Register StandaloneJwtOptions + StandaloneTokenService; add `POST /auth/token` endpoint; expose `public partial class Program` for tests |
| `tools/DivaFsMcpServer/DivaFsMcpServer.csproj` | Add `System.IdentityModel.Tokens.Jwt 8.*` |
| `tools/DivaFsMcpServer/appsettings.json` | Add `Jwt` section with defaults |
| `Diva.slnx` | Add `tests/DivaFsMcpServer.Tests` |

### Auth flow

```
# Get JWT (once)
POST /auth/token  {"apiKey": "master-secret"}
→ {"access_token":"eyJ...","expires_in":3600,"token_type":"Bearer"}

# Use JWT on all MCP calls
POST /mcp  Authorization: Bearer eyJ...
```

JWT disabled by default (`Jwt:SigningKey` empty) — static key auth unchanged until opt-in.

---

## [2026-04-30] Phase 23 — MCP Server Framework + FileSystem MCP Server

Reusable MCP server development framework and FileSystem MCP server with 12 tools (text, PDF,
image), SOLID-compliant design (4 interfaces, DIP throughout), two hosting modes (embedded + standalone),
and 61 passing tests.

### Architecture

`IDivaMcpToolType` + `WithDivaMcpTools<T>()` extension pair provides a zero-boilerplate pattern for
adding new MCP tool groups to Diva's shared MCP server (or standalone server). Each tool class is
Scoped in DI so it can access request context. `McpServerContext.FromHttpContext` returns
`Anonymous` when no HttpContext is present (stdio transport) — both embedded and standalone mode
work transparently.

`FileSystemMcpTools` is a thin MCP facade only — all logic delegates to interfaces:
`IFileSystemPathGuard` (security), `IToolFilter` (availability), `IPdfReader` (PdfPig),
`IImageReader` (ImageSharp blur/exposure/EXIF).

### New files

| File | Description |
|------|-------------|
| `src/Diva.Tools/Core/McpServerContext.cs` | Framework: tenant extractor + Anonymous fallback |
| `src/Diva.Tools/Core/McpServerRegistration.cs` | `IDivaMcpToolType`, `WithDivaMcpTools<T>()` |
| `src/Diva.Tools/FileSystem/FileSystemOptions.cs` | Config with per-tool/type flags, limits, image opts |
| `src/Diva.Tools/FileSystem/FileSystemOptionsValidator.cs` | `IValidateOptions<FileSystemOptions>` startup check |
| `src/Diva.Tools/FileSystem/Abstractions/IFileSystemPathGuard.cs` | Path validation contract |
| `src/Diva.Tools/FileSystem/Abstractions/IToolFilter.cs` | Tool availability contract |
| `src/Diva.Tools/FileSystem/Abstractions/IPdfReader.cs` | PDF extraction contract |
| `src/Diva.Tools/FileSystem/Abstractions/IImageReader.cs` | Image analysis contract |
| `src/Diva.Tools/FileSystem/FileSystemPathGuard.cs` | Path canonicalization, deny-list glob, symlink guard |
| `src/Diva.Tools/FileSystem/ToolFilter.cs` | Enabled-tools list, case-insensitive |
| `src/Diva.Tools/FileSystem/Readers/PdfReader.cs` | PdfPig text + metadata extraction |
| `src/Diva.Tools/FileSystem/Readers/ImageReader.cs` | ImageSharp Laplacian blur + exposure + EXIF |
| `src/Diva.Tools/FileSystem/Models/DirectoryEntry.cs` | list_directory DTO |
| `src/Diva.Tools/FileSystem/Models/FileInfoResult.cs` | get_file_info DTO |
| `src/Diva.Tools/FileSystem/Models/ImageInfoResult.cs` | read_image / get_image_info DTO |
| `src/Diva.Tools/FileSystem/FileSystemMcpTools.cs` | 12 MCP tools — thin facade over interfaces |
| `tools/DivaFsMcpServer/DivaFsMcpServer.csproj` | Standalone exe project (net10, self-contained) |
| `tools/DivaFsMcpServer/Program.cs` | stdio + --http modes, Windows Service support |
| `tools/DivaFsMcpServer/StandaloneApiKeyMiddleware.cs` | Bearer/X-Api-Key auth for HTTP mode |
| `tools/DivaFsMcpServer/appsettings.json` | Standalone config defaults |

### Modified files

| File | Change |
|------|--------|
| `src/Diva.Tools/Diva.Tools.csproj` | Added PdfPig 0.*, SixLabors.ImageSharp 3.* |
| `src/Diva.Host/Program.cs` | MCP server registration + `app.MapMcp("/mcp/diva").RequireAuthorization()` |
| `src/Diva.Host/appsettings.json` | Added `FileSystem` config section |
| `src/Diva.Host/Diva.Host.csproj` | Added `Microsoft.Extensions.Hosting.WindowsServices 10.*` |
| `tests/Diva.Tools.Tests/Diva.Tools.Tests.csproj` | Added NSubstitute, logging abstractions, EmbeddedResource |
| `Diva.slnx` | Added `tools/DivaFsMcpServer` folder |
| `docs/INDEX.md` | Added Phase 23 row |
| `docs/agents.md` | Added MCP Server (Phase 23) section |

### Tests — 61 passing

| Suite | Count |
|-------|-------|
| `FileSystemPathGuardTests` | 15 |
| `ToolFilterTests` | 6 |
| `FileSystemMcpToolsTests` | 12 |
| `PdfReaderTests` | 6 |
| `ImageReaderTests` | 10 |
| **Total** | **61** |

### Blur detection (Laplacian variance)

Grayscale pixel array → manual `[0,-1,0; -1,4,-1; 0,-1,0]` kernel on interior pixels → variance
of response values. `BlurThreshold = 100.0` separates sharp from blurry (configurable).
JPEG compression slightly softens images; lower threshold in low-quality scenarios.

### Phase 24 preview

`ImageOptions.ClassificationEnabled = false` placeholder. Phase 24 will add `classify_image` tool
calling Anthropic vision API (`claude-haiku-4-5-20251001`) with configurable categories + prompt.

---

## [2026-04-22] Dev Docker Environment, Auth & UI Fixes

### Docker / Deployment

Established a complete dev Docker environment with proper port configuration, branding, and auth support.

| File | Change |
|------|--------|
| `.env` | New — dev secrets: `ANTHROPIC_API_KEY`, `CREDENTIALS_MASTER_KEY`, `PORTAL_ORIGIN`, `AppBranding__*`, `VITE_APP_NAME/SLUG`, `OAUTH_ENABLED=true`, `LOCAL_AUTH_SIGNING_KEY` |
| `.env.prod` | New — production template (gitignored); SQL Server provider, real OAuth endpoints, stable key placeholders |
| `docker-compose.yml` | Fixed ports (`6032` API, `6010` portal); added `ASPNETCORE_URLS: http://+:6032`; portal `build.args` for Vite branding vars; added `OAuth__Enabled`, `LocalAuth__SigningKey`, and `AppBranding__*` env vars wired from `.env` |
| `admin-portal/Dockerfile` | Added `ARG VITE_APP_NAME` / `ARG VITE_APP_SLUG`; passes them to `npm run build` so branding is baked in at image build time |
| `admin-portal/nginx.conf` | Changed `listen` to `6010`; `proxy_pass` to `http://diva-api:6032`; added SSE proxy with `proxy_read_timeout 600s`; SPA fallback |

### Auth

| File | Change |
|------|--------|
| `admin-portal/src/lib/auth.ts` | `AUTH_ENABLED` default flipped from opt-in (`=== "true"`) to opt-out (`!== "false"`) — unauthenticated Docker builds now enforce login |
| `admin-portal/src/api.ts` | Added 401 interceptor in `request()` — on 401 clears localStorage token and redirects to `/login` |

### UI Bug Fixes

| File | Change |
|------|--------|
| `admin-portal/src/components/SsoConfig.tsx` | Fixed Add Provider button: changed from relative `navigate("new")` to absolute path `/settings/sso/new?tenantId=N` so it resolves correctly from `/platform/tenants/:id` |
| `admin-portal/src/components/SsoConfigEditor.tsx` | Added `useSearchParams`; reads `effectiveTenantId` from `?tenantId` query param so platform admins can configure SSO for any tenant |
| `admin-portal/src/components/GroupList.tsx` | Fixed focus loss in Create/Edit dialogs: removed `FormFields` arrow function component (caused React to unmount inputs on every render); JSX inlined directly into dialog bodies |
| `admin-portal/src/components/TenantList.tsx` | Same focus-loss fix as GroupList |

### User Profiles

| File | Change |
|------|--------|
| `admin-portal/src/components/UserProfiles.tsx` | Replaced hardcoded `TENANT_ID = 1` with `auth.getTenantId()` read per render; improved empty state message explaining that profiles are auto-created on first login |
| `src/Diva.Infrastructure/Auth/TenantContextMiddleware.cs` | `UpsertOnLoginAsync` catch block elevated from `LogWarning` to `LogError` with tenant/user context so profile creation failures are visible in production logs |

---

## [2026-04-20] Embeddable Chat Widget

Full embeddable chat widget feature spanning backend, widget SPA, admin UI, and tests.

### Architecture

The widget SPA is served from the API origin (`GET /widget-ui`) inside an `<iframe>`, so all `/api/agents/{id}/invoke/stream` calls are same-origin and require no additional CORS configuration. Only the public `/api/widget/*` endpoints need CORS, handled via the new `Widget` CORS policy.

### DB Entity + Migration

New `WidgetConfigs` table with 15 columns: `Id` (string GUID PK), `TenantId`, `AgentId`, `Name`, `AllowedOriginsJson`, `SsoConfigId` (nullable FK), `AllowAnonymous`, `WelcomeMessage`, `PlaceholderText`, `ThemeJson`, `RespectSystemTheme`, `ShowBranding`, `IsActive`, `CreatedAt`, `ExpiresAt`. EF query filter applies tenant isolation; `GetByIdAsync` bypasses it via `tenantId=0` context for public widget endpoints.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Data/Entities/WidgetConfigEntity.cs` | New — implements `ITenantEntity` |
| `src/Diva.Infrastructure/Data/DivaDbContext.cs` | Added `DbSet<WidgetConfigEntity>`, `HasKey`, `HasQueryFilter`, `HasIndex` |
| `src/Diva.Infrastructure/Data/Migrations/20260418000000_AddWidgetConfigs.cs` | New migration |
| `src/Diva.Infrastructure/Data/Migrations/20260418000000_AddWidgetConfigs.Designer.cs` | New migration designer |
| `src/Diva.Infrastructure/Data/Migrations/DivaDbContextModelSnapshot.cs` | Added `WidgetConfigEntity` block |

### Theme System

`WidgetTheme` record with 18 properties covering all color surfaces plus typography and launcher size. Two built-in static presets: `WidgetTheme.Light` (default) and `WidgetTheme.Dark`. The `Preset` field is informational — actual colors are always stored so per-tenant customisation from a preset baseline is supported. `RespectSystemTheme` flag triggers auto-swap to Dark when `prefers-color-scheme: dark` is detected in the widget SPA.

| File | Change |
|------|--------|
| `src/Diva.Core/Models/Widgets/WidgetTheme.cs` | New — `WidgetTheme` record with `Light` and `Dark` static presets |
| `src/Diva.Core/Models/Widgets/WidgetDtos.cs` | New — `WidgetConfigDto`, `CreateWidgetRequest`, `WidgetInitResponse`, `WidgetAuthRequest`, `WidgetAuthResponse`, `WidgetSessionResponse` |

### Service Layer

| File | Change |
|------|--------|
| `src/Diva.TenantAdmin/Services/IWidgetConfigService.cs` | New interface |
| `src/Diva.TenantAdmin/Services/WidgetConfigService.cs` | New implementation — `GetByIdAsync` bypasses tenant filter for public endpoints; `ThemeJson=null` falls back to `WidgetTheme.Light` |

### Backend API

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Auth/LocalAuthService.cs` | Added `IssueWidgetAnonymousJwt(tenantId, userId, agentId, ttl)` — issues short-lived JWT with `agent_access` claim scoped to single agent |
| `src/Diva.Host/Controllers/WidgetController.cs` | New — `[AllowAnonymous][EnableCors("Widget")]`; `GET /widget-ui`, `GET /api/widget/{id}/init`, `POST /api/widget/{id}/auth` (SSO token exchange), `POST /api/widget/{id}/session` (anonymous session) |
| `src/Diva.Host/Controllers/AdminController.cs` | Added widget admin region: `GET/POST /api/admin/widgets`, `PUT/DELETE /api/admin/widgets/{id}` |
| `src/Diva.Host/Program.cs` | Added `Widget` CORS policy (`SetIsOriginAllowed(_ => true)`, controller validates `Origin` against DB `AllowedOriginsJson`); registered `IWidgetConfigService` as scoped |

### Embed Script

Vanilla JS, no framework. Runs on the host website. Creates a launcher button and hidden iframe, handles open/close with CSS transitions, listens for `DIVA_SSO_REQUEST` / `DIVA_SSO_TOKEN` postMessage protocol, and adjusts iframe width for narrow viewports (`< 480px`).

| File | Change |
|------|--------|
| `src/Diva.Host/wwwroot/widget.js` | New — self-contained embed script |
| `src/Diva.Host/wwwroot/widget/` | Directory for built widget SPA (populated by `npm run build`) |

### Widget SPA

Lightweight React SPA built as a separate Vite entry point. Loads widget config, applies CSS custom properties from `WidgetTheme`, detects system dark mode via `matchMedia`, runs SSO/anonymous auth flow, and renders a streaming chat UI. Bundle: **11.5 kB** gzipped **4 kB**.

| File | Change |
|------|--------|
| `admin-portal/vite.config.ts` | Added `widget` entry in `rollupOptions.input`; separate output dirs per entry |
| `admin-portal/widget.html` | New Vite entry HTML for widget SPA |
| `admin-portal/src/widget/main.tsx` | New — mounts `WidgetApp` from `?id=` query param |
| `admin-portal/src/widget/types.ts` | New — `WidgetTheme`, `WidgetInitResponse`, `AgentStreamChunk`, `ChatMessage`, `LIGHT_PRESET`, `DARK_PRESET` |
| `admin-portal/src/widget/WidgetApp.tsx` | New — config load, theme application, SSO/anonymous auth flow with 3 s timeout, stored-session reuse, expiry check |
| `admin-portal/src/widget/WidgetChat.tsx` | New — SSE streaming chat UI; user + agent bubbles; typing indicator; `Enter` to send; postMessage close |
| `admin-portal/src/api.ts` | Added `WidgetThemeDto`, `WidgetConfigDto`, `CreateWidgetRequest` types; `listWidgets`, `createWidget`, `updateWidget`, `deleteWidget` API functions |

### Admin UI

| File | Change |
|------|--------|
| `admin-portal/src/components/WidgetManager.tsx` | New — list view: name, agent, origins, SSO/anon badges, copy embed code, edit, delete |
| `admin-portal/src/components/WidgetEditor.tsx` | New — create/edit form; Light / Dark / Custom preset switcher; 12 color pickers with live preview panel; font family + size |
| `admin-portal/src/App.tsx` | Added `/settings/widgets` route |
| `admin-portal/src/components/layout/app-sidebar.tsx` | Added "Chat Widgets" nav item under Settings |

### Tests

| File | Change |
|------|--------|
| `tests/Diva.TenantAdmin.Tests/WidgetConfigServiceTests.cs` | New — 10 passing tests: create, custom theme round-trip, tenant isolation, `GetByIdAsync` filter bypass, update, update-not-found, delete, delete-idempotent, null-theme defaults |

---

## [2026-04-18] Admin Portal — Agent Config UI Cleanup & Rule Pack List Redesign

### Lifecycle Hooks — named behavior toggles replace free-text class input

The `HookEditor` component previously showed a free-text `<Input>` asking admins to type raw C# class names (e.g. `CitationEnforcerHook`). This leaked implementation internals, failed silently on typos, and was non-discoverable.

Replaced with a fixed list of six named toggle rows, one per built-in hook, each showing a human-readable label, a short description, and a lifecycle point badge.

**How it works:** `HooksJson` stores a JSON dict keyed by arbitrary strings. User-configurable hooks now use synthetic keys (`__prompt_guard__`, `__pii_redaction__`, etc.) — the same pattern already used by the platform always-on hooks (`__rule_packs__`, `__static_model_switcher__`, `__model_router__`). Archetype-sourced entries (e.g. `"OnInit":"PromptInjectionGuardHook"`) are detected by scanning dict *values*, so archetype defaults correctly show as ON. Toggle-off removes both the synthetic key and any matching value entry (handles archetype-sourced hooks).

Two additional bug fixes:
- `useEffect` added to sync internal `rawJson` state when parent updates `hooksJson` (e.g. on archetype selection)
- `serializeToggle` on disable now scans all dict values for the class name, not only the synthetic key — previously toggling off an archetype-sourced hook had no effect

| File | Change |
|------|--------|
| `admin-portal/src/components/HookEditor.tsx` | Full rewrite — 6 named toggle rows; `parseEnabledClasses` (value scan), `serializeToggle` (synthetic keys + value scan on disable); `useEffect` value sync |

### Archetype selector — compact dropdown replaces card grid

The archetype card grid occupied ~350 px of the Identity tab (8 cards × 2 rows). Replaced with a single `<Select>` dropdown (categories shown as `SelectGroup` labels) plus a small description panel that appears below when an archetype is selected (~80 px total).

| File | Change |
|------|--------|
| `admin-portal/src/components/ArchetypeSelector.tsx` | Rewritten — `Select` + `SelectGroup`/`SelectLabel` per category; selected archetype info panel (icon + name + description) |

### Tool Filter bug fix — input never appeared after mode selection

`setToolFilter` cleared `toolFilterJson` whenever `tools.length === 0`, which fired immediately on mode selection (before any tools were typed). The tools input was therefore never visible. Fixed by removing the `tools.length === 0` guard — the field now only clears when mode is deselected (`__none__`).

| File | Change |
|------|--------|
| `admin-portal/src/components/AgentBuilder.tsx` | `setToolFilter` — removed `|| tools.length === 0` from clear condition |
| `admin-portal/src/components/GroupAgentTemplateBuilder.tsx` | Same fix |

### Pipeline Stages and Stage Instructions UI removed

Both sections were fully wired in the UI (switches for 7 pipeline stages, textareas for Decompose / Integrate / Verify instructions) and stored to `pipelineStagesJson` / `stageInstructionsJson` in the database — but the backend `SupervisorAgent` never consumed either field. All 7 stages always ran unconditionally regardless of config. Removed from both `AgentBuilder` and `GroupAgentTemplateBuilder` to eliminate dead UI. DB columns and DTO fields are preserved for Phase 19 (Coordinator Sub-Agent Routing).

| File | Change |
|------|--------|
| `admin-portal/src/components/AgentBuilder.tsx` | Removed `PIPELINE_STAGES` constant, `pipelineStages` state, `setPipelineStage` setter, Pipeline Stages section, `stageInstructions` state, `setStageInstruction` setter, Stage Instructions section |
| `admin-portal/src/components/GroupAgentTemplateBuilder.tsx` | Same removals |

### Rule Pack list — filter toolbar, pagination, and badge truncation

The rule pack list had no search, no filtering, and no pagination — all packs rendered as a single unbounded card list.

**Changes:**
- Filter toolbar: name search input, Status select (All / Enabled / Disabled), Type select (All / Mandatory / Group / Starters)
- Live result count (`Showing 1–25 of 48`)
- Client-side pagination — prev/next with page number display; per-page selector (10 / 25 / 50)
- Starters merged into the unified filtered list with `_isStarter` flag (dashed border, Starter badge); old separate hardcoded section removed
- Rule type badges capped at 6 visible; overflow shown as `+N more` outline badge — prevents tall cards on packs with many rules
- `PackCard` extracted as a local sub-component

All filtering and pagination is client-side — backend returns everything at once (dataset stays small).

| File | Change |
|------|--------|
| `admin-portal/src/components/RulePackManager.tsx` | Full rewrite — filter state, `PackRow` type, `filtered`/`visible` derived lists, pagination bar, `PackCard` component |

---

## [2026-04-13] A2A — Agent Discovery Fixes & Multi-Agent Listing Endpoint

### `/.well-known/agent.json` returning 401 (missing auth bypass)

External A2A clients hitting `GET /.well-known/agent.json` received a 401 because `TenantContextMiddleware` validated JWT before the request reached the controller. The `[AllowAnonymous]` attribute on `AgentCardController` was insufficient alone — it only suppresses ASP.NET Core's built-in auth middleware, not the custom `TenantContextMiddleware` which runs earlier in the pipeline.

**Two-pronged fix:**
1. `TenantContextMiddleware` — added `/.well-known` to the bypass check so the path is allowed through before any token validation
2. `AgentCardController` — added `[AllowAnonymous]` + `using Microsoft.AspNetCore.Authorization` as defence-in-depth

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Auth/TenantContextMiddleware.cs` | Added `context.Request.Path.StartsWithSegments("/.well-known")` to bypass condition |
| `src/Diva.Host/Controllers/AgentCardController.cs` | Added `[AllowAnonymous]` attribute + `using Microsoft.AspNetCore.Authorization` |

### `/.well-known/agents.json` — list all published agents

The A2A spec mandates `agent.json` (singular) as the standard single-agent discovery URL. A new `agents.json` endpoint returns all published agents as an array for portal tooling and multi-agent discovery.

| Endpoint | Behaviour |
|----------|----------|
| `GET /.well-known/agent.json` | Single AgentCard — first published agent (A2A spec) |
| `GET /.well-known/agent.json?agentId={id}` | AgentCard for specific agent |
| `GET /.well-known/agents.json` | Array of all published agents, ordered by `DisplayName` |
| `GET /.well-known/agents.json?tenantId={id}` | Same, scoped to a specific tenant (master admin) |

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/AgentCardController.cs` | Added `GET /.well-known/agents.json` action — queries all `IsEnabled && Status == "Published"` agents, returns array of AgentCards |

---

## [2026-04-13] UI Improvements — AI Prompt Builder Enrichment, SSO Config Page, Session Trace Full-Text

### AI Agent Prompt Builder — MCP tool + delegate context enrichment

The agent setup assistant now discovers actual MCP tool names/descriptions and delegate sub-agent details when generating system prompts, resulting in prompts that reference real tool functions and explain delegation behaviour.

**New reusable interfaces:**
- `IAgentToolDiscoveryService` (Core) — discovers MCP tools for an agent via `ConnectAsync` + `BuildToolDataAsync` with 8 s bounded timeout; returns empty list on failure (best-effort, never blocks save)
- `ISetupAssistantContextEnricher` pipeline already existed (`LlmConfigContextEnricher`); `AgentToolsContextEnricher` added as second enricher — populates `McpTools` and `DelegateAgents` fields on `AgentSetupContext`

**Backend changes:**

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/IAgentToolDiscoveryService.cs` | **New** — `DiscoverToolsAsync(agentId, tenantId, ct)` interface |
| `src/Diva.Core/Models/AgentSetupDtos.cs` | Added `AgentId`, `DelegateAgentIds`, `McpTools`, `DelegateAgents` to `AgentSetupContext`; new records `McpToolDetail`, `DelegateAgentDetail` |
| `src/Diva.Infrastructure/LiteLLM/AgentToolDiscoveryService.cs` | **New** — implements `IAgentToolDiscoveryService`; loads agent entity, calls `IMcpConnectionManager.ConnectAsync` + `BuildToolDataAsync`, 8 s timeout |
| `src/Diva.TenantAdmin/Services/Enrichers/AgentToolsContextEnricher.cs` | **New** — thin `ISetupAssistantContextEnricher` adapter; populates MCP tools + delegate agent details; returns early if `AgentId` is null (unsaved agent) |
| `src/Diva.TenantAdmin/Services/AgentSetupAssistant.cs` | Added `{{mcp_tools_section}}` and `{{delegate_agents_section}}` substitutions + helper methods `BuildMcpToolsSection`, `BuildDelegateAgentsSection` |
| `prompts/agent-setup/system-prompt-generator.txt` | Replaced generic `{{tool_names}}` with `## Available MCP Tools` and `## Delegate Sub-Agents` sections |
| `src/Diva.Host/Program.cs` | Registered `IAgentToolDiscoveryService` (Singleton), `AgentToolsContextEnricher` (Singleton) |

**Frontend changes:**

| File | Change |
|------|--------|
| `admin-portal/src/api.ts` | Added `agentId`, `delegateAgentIds`, `mcpTools`, `delegateAgents` fields to `AgentSetupContext` |
| `admin-portal/src/components/AgentBuilder.tsx` | Passes `delegateAgentIds` (parsed from form JSON) to `AgentAssistantDrawer` |
| `admin-portal/src/components/AgentAssistantDrawer.tsx` | Added `agentId` + `delegateAgentIds` props; fetches agent names on mount; includes both in `buildContext()`; shows "Delegate Sub-Agents" badges in Context step; shows unsaved-agent hint when agent not yet saved |

### AI Prompt Builder — truncation fix (`MaxSuggestionTokens`)

Generated system prompts were being cut off mid-JSON, causing `JsonException` on parse. Two fixes:
1. Default `MaxSuggestionTokens` raised from 1024 → 4096 (matches `MaxRulePackSuggestionTokens`)
2. Added truncation recovery in `ParsePromptSuggestion` — extracts whatever `system_prompt` content was generated before truncation and returns it with a rationale note rather than returning empty

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/AgentOptions.cs` | `MaxSuggestionTokens` default 1024 → 4096 |
| `src/Diva.TenantAdmin/Services/AgentSetupAssistant.cs` | `ParsePromptSuggestion` — truncation recovery via `TryExtractTruncatedStringField`; `JsonException` handled separately from general `Exception` |

### SSO Configuration — full-page editor

The SSO provider dialog (modal) has been converted to a full-page form for better usability and field visibility.

| File | Change |
|------|--------|
| `admin-portal/src/components/SsoConfigEditor.tsx` | **New** — full-page form at `/settings/sso/new` and `/settings/sso/:id/edit`; sections: Provider, Endpoints, Proxy, Mappings; includes claim mappings reference table |
| `admin-portal/src/components/SsoConfig.tsx` | Stripped to list-only view; Add/Edit buttons navigate to routes instead of opening dialog |
| `admin-portal/src/App.tsx` | Added routes `settings/sso/new` and `settings/sso/:id/edit` |

### SSO Configuration — Claim Mappings help text

Added inline reference panel on the Claim Mappings JSON field showing all 9 mappable fields, their default claim names, and descriptions. Toggled via "Available fields" button.

**Confirmed actively used:** `AuthController` deserializes `ClaimMappingsJson` on every SSO callback; `TenantClaimsExtractor` uses all 9 fields to build `TenantContext`.

| File | Change |
|------|--------|
| `admin-portal/src/components/SsoConfigEditor.tsx` | `CLAIM_FIELDS` constant; collapsible reference table with field/default/description columns |

### Login page — SSO organization dropdown with search

The SSO provider list on the login page was a growing stack of buttons. Replaced with a searchable combobox for multi-provider deployments.

| File | Change |
|------|--------|
| `admin-portal/src/components/LoginPage.tsx` | Single provider: keeps plain button. 2+ providers: `Popover` + `Input` filter + scrollable list (max 60 results visible) + checkmark on selected org + "No organizations found" empty state; sign-in button disabled until selection made; search input auto-focuses on open |

### Session trace — full-text storage and viewer

Tool inputs and outputs were being truncated at write time (8 KB / 4 KB caps), permanently losing data. Turn messages were stored in full but only 200-char previews were returned and displayed.

**Backend fixes:**

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Sessions/SessionTraceWriter.cs` | Removed `Truncate(chunk.ToolInput, 8192)` and `Truncate(chunk.ToolOutput, 4096)` — tool data now stored in full |
| `src/Diva.Core/Models/Session/SessionDtos.cs` | Added `UserMessage` and `AssistantMessage` (full text) fields to `TurnSummary` alongside existing `*Preview` fields |
| `src/Diva.Host/Controllers/SessionsController.cs` | `GetSession` now populates `UserMessage` and `AssistantMessage` full text on each `TurnSummary` |

**Frontend fixes:**

| File | Change |
|------|--------|
| `admin-portal/src/api.ts` | Added `userMessage?` and `assistantMessage?` to `TurnSummary` |
| `admin-portal/src/components/SessionDetail.tsx` | Turn message cards show full text; "Full" button (with `Maximize2` icon) appears when content exceeds 300 chars, opens scrollable `Dialog`; tool call expanded view capped at `max-h-64` to prevent page overflow |
| `admin-portal/src/components/SessionToolCallCard.tsx` | "Full" button appears on tool input (> 500 chars) and output (> 500 chars); opens `Dialog` with full formatted content; expanded inline view capped at `max-h-64` |

### Session Detail — back button

The back button used `navigate(-1)` (browser history), which navigated back to a delegation child session instead of the list when following "View Session" links. Fixed to always navigate to `/sessions`.

| File | Change |
|------|--------|
| `admin-portal/src/components/SessionDetail.tsx` | Both back buttons: `navigate(-1)` → `navigate("/sessions")` |

---

## [2026-04-11] Bug Fixes — Sub-Agent Timeout, LLM Idle Timeout, Cache Marker Orphan, Mid-Stream Retry

Three runtime issues discovered in production logs + one defense-in-depth improvement. Timeout defaults tuned after production testing.

### Sub-agent delegation using wrong timeout (root cause of SocketException 995)

`AgentToolExecutor` used `ToolTimeoutSeconds` (30 s) for sub-agent delegation calls. Sub-agents run a full ReAct loop (multiple LLM iterations + tool calls) that routinely exceeds 30 s, causing the CancellationToken to fire mid-stream and abort the Anthropic socket connection. `SubAgentTimeoutSeconds` already existed and was used by `DispatchStage` but was never wired into the agents-as-tools path. Default increased from 120 → 300 s after production testing showed complex sub-agents need up to 5 minutes.

**Fix:** Use `SubAgentTimeoutSeconds` instead of `ToolTimeoutSeconds` in `AgentToolExecutor`. Added zero-guard (`timeout > 0`) matching `DispatchStage` pattern. Distinguished parent cancellation from local timeout in `OperationCanceledException` catch using `when (!ct.IsCancellationRequested)`.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AgentToolExecutor.cs` | `ToolTimeoutSeconds` → `SubAgentTimeoutSeconds`, zero-guard, parent vs local cancellation |
| `src/Diva.Core/Configuration/AgentOptions.cs` | `SubAgentTimeoutSeconds` default 120 → 300 |
| `src/Diva.Host/appsettings.json` | `SubAgentTimeoutSeconds` 120 → 300 |
| `tests/Diva.Agents.Tests/AgentToolExecutorTests.cs` | Updated fixture, added `ExecuteAsync_UsesSubAgentTimeout_NotToolTimeout` test |

### Per-iteration LLM timeouts (new)

No per-iteration timeout existed. The only protection was the outer `HttpTimeoutSeconds` (600 s) on the HttpClient — too coarse for detecting stalled LLM calls.

**Fix:** Added `LlmTimeoutSeconds` (120 s, absolute) for buffered calls and `LlmStreamIdleTimeoutSeconds` (120 s, resets per chunk) for streaming. Both applied inside `CallLlmForIterationAsync` via linked `CancellationTokenSource`. Initial `LlmStreamIdleTimeoutSeconds` was 60 s but increased to 120 s after production testing — Claude can take >60 s to process large tool results (20 K+ chars) before emitting the first text token.

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/AgentOptions.cs` | Added `LlmTimeoutSeconds`, `LlmStreamIdleTimeoutSeconds` |
| `src/Diva.Host/appsettings.json` | Added both to `Agent` section |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Rewrote `CallLlmForIterationAsync` with idle/absolute timeouts |

### Cache_control marker orphan after context compaction

`CompactHistory()` nulled `_slidingCacheBoundary` without clearing `CacheControl` properties on messages kept in the tail. Subsequent `AddToolResults()` couldn't clear the old markers, accumulating past Anthropic's 4-block limit → API error: "A maximum of 4 blocks with cache_control may be provided."

**Fix:** Added `ResetCacheMarkersAfterCompaction()` — strips all `CacheControl` from every content block in `_messages`, then re-sets BP3 on the last message with content. Called from both `CompactHistory()` and `PrepareNewWindow()`.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs` | Added `ResetCacheMarkersAfterCompaction()`, `CountCacheControlMarkers()` test accessor |
| `tests/Diva.Agents.Tests/AnthropicProviderStrategyCacheTests.cs` | 3 new tests for orphan marker prevention |

### Mid-stream streaming failure now falls back to buffered call (defense-in-depth)

Previously, when streaming started successfully but failed mid-transfer (connection drop, idle timeout), the error propagated to the ReAct loop because the buffered fallback only fired when streaming failed at start. Now mid-stream failures also trigger the buffered fallback with partial text discarded. `CallLlmAsync` already uses `CallWithRetryAsync` (3 retries, exponential backoff), so transient failures are automatically retried.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | `CallLlmForIterationAsync`: `needBufferedFallback` flag replaces `streamEnumerator is null` guard |

---

## [2026-04-12] Bug Fixes — Corrupted Delegation IDs, Pipeline Visibility, Save Error, SSO Propagation

Four runtime issues discovered during end-to-end delegation testing.

### Corrupted `DelegateAgentIdsJson` — `[null]` in database

Legacy `DelegateAgentSelector` code called `Number(agent.id)` on UUID strings — `Number("uuid")` returns `NaN`, and `JSON.stringify([NaN])` produces `[null]`. The database had `DelegateAgentIdsJson = '[null]'`, which parsed to `["null"]` — matching no agent IDs and causing the UI to show "1 agent selected" without displaying the agent name.

**Fix (2 layers):**
1. **Frontend**: Added filter for `null`/`"null"`/`"NaN"`/`"undefined"` in parsed IDs; auto-clear `useEffect` resets corrupted values to `undefined`; counter now shows resolved agent count (not raw ID count) with amber warning for unresolved IDs
2. **MigFix**: Added cleanup query — `UPDATE AgentDefinitions SET DelegateAgentIdsJson = NULL WHERE DelegateAgentIdsJson IN ('[null]', '[NaN]', '[]', '[undefined]')`

| File | Change |
|------|--------|
| `admin-portal/src/components/DelegateAgentSelector.tsx` | Null/NaN filtering, auto-clear useEffect, resolved count display with orphan warning |
| `tools/MigFix/Program.cs` | Added corrupted `DelegateAgentIdsJson` cleanup query |

### Pipeline stages not visible in agent configuration

`AdvancedConfigPanel` in AgentBuilder was collapsed by default. For agents without advanced config already set, the panel stayed collapsed — hiding pipeline stages, hooks, and verification settings when editing existing agents.

**Fix:**
1. Auto-expand the panel when editing an existing agent (`isEditing` prop + `useEffect`)
2. Added dot indicator on the "Advanced" tab when any advanced config is set (verification, pipeline stages, hooks, delegation, A2A endpoint)

| File | Change |
|------|--------|
| `admin-portal/src/components/AgentBuilder.tsx` | `AdvancedConfigPanel` accepts `isEditing` prop; auto-expand on edit; dot indicator on Advanced tab |

### Agent save returning 400 Bad Request

`AgentDefinitionEntity.ExecutionMode` is a non-nullable `string` (default `"Full"`), but the frontend form started with `executionMode: undefined`. JSON serialization turned `undefined` into `null`, which `System.Text.Json` cannot convert to `string` — causing model binding to fail entirely (`"The dto field is required."`).

**Fix:** Set defaults in `handleSave`: `executionMode: form.executionMode || "Full"` and `status: form.status || "Draft"`.

| File | Change |
|------|--------|
| `admin-portal/src/components/AgentBuilder.tsx` | Added `executionMode` and `status` defaults in save handler |

### `ForwardSsoToMcp` not propagated to delegated sub-agents

When a parent agent delegates to a sub-agent, `AgentToolExecutor` built a new `AgentRequest` without copying the `ForwardSsoToMcp` flag. If the parent request had `ForwardSsoToMcp = true` (the per-request SSO override), the sub-agent defaulted to `false` and did not force SSO token forwarding to its MCP tool servers.

**Auth chain verification:** `TenantContext` (including `AccessToken`) is correctly propagated through the full delegation chain — `AnthropicAgentRunner` → `AgentToolExecutor` → `DelegationAgentResolver` → sub-agent → `McpConnectionManager.ConnectAsync(fallbackTenant)`. The `HttpContext` is also available via `AsyncLocal` since delegation runs within the same HTTP request. Only `ForwardSsoToMcp` was missing.

**Fix:** Added `bool forwardSsoToMcp` parameter to `AgentToolExecutor.ExecuteAsync`; set it on the delegated `AgentRequest`; both call sites in `AnthropicAgentRunner` now pass `hookCtx.Request.ForwardSsoToMcp`.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AgentToolExecutor.cs` | Added `forwardSsoToMcp` parameter, sets `ForwardSsoToMcp` on delegated request |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Both delegation call sites pass `hookCtx.Request.ForwardSsoToMcp` |
| `tests/Diva.Agents.Tests/AgentToolExecutorTests.cs` | 2 new tests — propagation when true + default false (243 total) |

---

## [2026-04-11] Bug Fix — Tool-Call Final Response Not Displayed

After any MCP tool call the agent's actual answer was never shown. The UI and scheduler both received a "task complete" stub instead of the real response (weather data, search results, email confirmation, etc.).

### Root cause

`AnthropicAgentRunner.InvokeStreamAsync` maintained a `lastIterationHadToolCalls` flag that was set to `true` after every successful tool execution. On the next iteration — when the model produced the real answer — the flag triggered a "post-tool nudge": a user message falsely telling the model "you described an action but did not call the tool." The model, confused by the false accusation, replied with a throwaway "task complete" sentence, which became `finalResponse`.

The original comment claimed the nudge was for "model described action without calling tool," but that scenario would require `HasToolCalls=false` — a path that never set the flag. The nudge fired exclusively and incorrectly after successful tool execution, every time, for every agent and provider.

**Fix**: removed the `lastIterationHadToolCalls` flag, its `PostToolNudgePrompt` constant, and the entire nudge block. The model's first text-only response after tool results are returned is now accepted directly as the final answer.

Covers all tool types, all agents, both providers (Anthropic and OpenAI-compatible share the same outer ReAct loop). Scheduler path unaffected — it calls `RunAsync` which materialises the same stream.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Removed `PostToolNudgePrompt` constant, `lastIterationHadToolCalls` flag (declaration + 2 references), and the 11-line nudge block |

---

## [2026-04-12] Bug Fixes — Delegation ID Type Mismatch, A2A Config, Partial Migration Recovery

Three runtime issues discovered during agents-as-tools integration testing.

### Agent delegation not invoking — ID type mismatch

`DelegateAgentSelector` (frontend) stored agent IDs as JSON numbers (`[1,2]`), but `AgentToolProvider` deserialized as `List<string>` — `System.Text.Json` rejects JSON numbers in a string array, causing a silent `JsonException` and zero delegation tools injected.

Additionally, `AgentDelegationTool.AgentId` was `int`, losing fidelity for GUID-based agent IDs when passed through `GetHashCode()`. The full `AgentToolExecutor` → `IAgentDelegationResolver` round-trip used `ToString()` on the int, which didn't match the original ID.

**Fix (3 layers):**
1. **Frontend**: `DelegateAgentSelector` now stores string IDs (`["id-1","id-2"]`), with backwards-compatible parsing for legacy number arrays
2. **Backend parser**: `AgentToolProvider` uses `JsonNode.Parse` → `AsArray()` → `ToString()` per element to handle both `["id"]` and `[1]` formats
3. **Type alignment**: `AgentDelegationTool.AgentId` changed from `int` to `string`; `AgentToolExecutor` no longer calls `.ToString()`; `DelegationAgentResolver.GetAgentInfoAsync` returns `AgentType` as the name (not `AgentId` duplicated)

| File | Change |
|------|--------|
| `admin-portal/src/components/DelegateAgentSelector.tsx` | `selectedIds: number[]` → `string[]`; `toggle(id: number)` → `toggle(id: string)`; all `Number(a.id)` → `a.id` |
| `src/Diva.Infrastructure/LiteLLM/AgentToolProvider.cs` | `JsonSerializer.Deserialize<List<string>>` → `JsonNode.Parse().AsArray().Select(n => n.ToString())` |
| `src/Diva.Infrastructure/LiteLLM/AgentDelegationTool.cs` | `AgentId: int` → `string`; constructor + `Name` pattern updated |
| `src/Diva.Infrastructure/LiteLLM/AgentToolExecutor.cs` | Removed `.ToString()` on `tool.AgentId` |
| `src/Diva.Agents/Registry/DelegationAgentResolver.cs` | Returns `cap.AgentType` as name (was duplicating `cap.AgentId`) |
| `tests/Diva.Agents.Tests/AgentDelegationToolTests.cs` | All constructors: `int` → `string` |
| `tests/Diva.Agents.Tests/AgentToolExecutorTests.cs` | `MakeTool(int)` → `MakeTool(string)` |

### A2A settings showing disabled

`appsettings.json` had `"A2A": { "Enabled": false }` — changed to `true`. The A2A settings page and cleanup service now reflect correct status.

| File | Change |
|------|--------|
| `src/Diva.Host/appsettings.json` | `A2A.Enabled: false` → `true` |

### Missing `TenantLlmConfigs.Name` and `PlatformLlmConfigs.Name` columns

Migration `20260326195152_AddLlmConfigCatalog` was recorded as applied in `__EFMigrationsHistory` but partially failed at runtime (likely the `DropIndex` on a non-existent index caused a rollback). The `Name` column was never added to `TenantLlmConfigs` or `PlatformLlmConfigs`, causing `SQLite Error 1: 'no such column: t.Name'` on any query touching those tables.

**Fix**: Added ALTER TABLE statements to `tools/MigFix/Program.cs` to add missing columns and ran the tool.

| File | Change |
|------|--------|
| `tools/MigFix/Program.cs` | Enhanced — lists all tables, checks Name columns across LLM config tables, adds missing Name/PlatformConfigRef/AvailableModelsJson/DeploymentName columns, generic `FixMissingColumn` helper |

---

## [2026-04-12] Phase B — Agents-as-Tools (Peer-to-Peer Delegation)

Local agent delegation via the tool pipeline. An agent can call other agents as tools during its ReAct loop, with depth guards, timeout, and truncation.

### AgentDelegationTool — synthetic AIFunction

`AIFunction` subclass that represents a peer agent as a callable tool. Static JSON schema (`query` + `context`), tool name pattern `call_agent_{sanitizedName}_{id}`.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AgentDelegationTool.cs` | **New** — AIFunction subclass, schema, `IsAgentDelegationTool()` helper |

### AgentToolProvider / AgentToolExecutor

Provider builds `AgentDelegationTool` list from `DelegateAgentIdsJson`. Executor handles depth guard, JSON parsing, timeout, truncation.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AgentToolProvider.cs` | **New** — resolves agent IDs → delegation tools, self-delegation prevention |
| `src/Diva.Infrastructure/LiteLLM/AgentToolExecutor.cs` | **New** — executes `call_agent_*` calls, depth ≥ `MaxDelegationDepth` blocked |

### IAgentDelegationResolver — cross-project abstraction

Interface in Core to avoid circular dependency Infrastructure↔Agents. Implemented by `DelegationAgentResolver` in Agents.

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/IAgentDelegationResolver.cs` | **New** — `GetAgentInfoAsync` + `ExecuteAgentAsync` + `DelegateAgentInfo` record |
| `src/Diva.Agents/Registry/DelegationAgentResolver.cs` | **New** — bridges `IAgentRegistry` → `IAgentDelegationResolver` |

### Provider strategy — AddExtraTools

Both strategies accept extra `AIFunction` instances alongside MCP tools.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/ILlmProviderStrategy.cs` | Added `AddExtraTools(IReadOnlyList<AIFunction>)` |
| `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs` | `ToAnthropicTool` widened to `AIFunction`, `AddExtraTools` impl |
| `src/Diva.Infrastructure/LiteLLM/OpenAiProviderStrategy.cs` | `AddExtraTools` adds to `_chatOptions.Tools` |

### AnthropicAgentRunner — delegation routing

Agent tools injected after `strategy.Initialize()`. Tool calls routed to `AgentToolExecutor` instead of MCP pipeline.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Constructor +2 optional params, delegation tool injection, routing in `ExecuteToolPipelineAsync` |

### Database — DelegateAgentIdsJson column

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs` | Added `DelegateAgentIdsJson` property |
| `src/Diva.Infrastructure/Data/Migrations/20260412000000_AgentDelegation.cs` | **New** — migration + Designer.cs |

### API & DI registration

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/AgentsController.cs` | PUT mapping includes `DelegateAgentIdsJson` |
| `src/Diva.Host/Program.cs` | Registered `AgentToolProvider`, `AgentToolExecutor`, `IAgentDelegationResolver` |

### Admin portal — DelegateAgentSelector UI

| File | Change |
|------|--------|
| `admin-portal/src/api.ts` | Added `delegateAgentIdsJson` to `AgentDefinition` |
| `admin-portal/src/components/DelegateAgentSelector.tsx` | **New** — multi-select agent picker (badge toggle) |
| `admin-portal/src/components/AgentBuilder.tsx` | Integrated `DelegateAgentSelector` in advanced tab |

### Tests — 26 new tests

| File | Tests |
|------|-------|
| `tests/Diva.Agents.Tests/AgentDelegationToolTests.cs` | 8 — naming, sanitization, schema, description, capabilities, `InvokeAsync` throws |
| `tests/Diva.Agents.Tests/AgentToolProviderTests.cs` | 6 — valid IDs, self-delegation skip, missing agents, invalid/empty/null JSON |
| `tests/Diva.Agents.Tests/AgentToolExecutorTests.cs` | 12 — depth guard, input parsing, delegation success/failure, context propagation, truncation |

---

## [2026-04-11] Phase A — A2A Hardening & Test Coverage

Completed Phase 14 (A2A Protocol) hardening: scalability guards, resilience, background cleanup, rate limiting, admin UI, and test coverage.

### A2A Options expansion

Added `MaxConcurrentTasks` (10), `TaskRetentionDays` (7), `RateLimitPerMinute` (10) to `A2AOptions`.

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/A2AOptions.cs` | Added 3 new properties |
| `src/Diva.Host/appsettings.json` | Expanded A2A section with new fields |

### Concurrent task limit

POST `/tasks/send` returns 429 when `MaxConcurrentTasks` in-flight tasks are already running.

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/AgentTaskController.cs` | Added concurrent limit guard + `[EnableRateLimiting("a2a")]` |

### HttpClient resilience

Standard resilience handler (retry, circuit breaker, timeout) on the A2A HttpClient.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Diva.Infrastructure.csproj` | Added `Microsoft.Extensions.Http.Resilience` 9.5.0 |
| `src/Diva.Host/Program.cs` | Chained `.AddStandardResilienceHandler()` on A2A HttpClient |

### Task cleanup background service

`AgentTaskCleanupService` (IHostedService) runs hourly, purges completed/failed/canceled tasks older than `TaskRetentionDays`.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/A2A/AgentTaskCleanupService.cs` | New — BackgroundService with EF `ExecuteDeleteAsync` |
| `src/Diva.Infrastructure/Data/DivaDbContext.cs` | Added `IX_AgentTasks_Status_CreatedAt` index |
| `src/Diva.Infrastructure/Data/Migrations/20260411003535_A2A_TaskCleanupIndex.cs` | New migration |
| `src/Diva.Host/Program.cs` | Registered `AgentTaskCleanupService` hosted service |

### Rate limiting on A2A endpoints

Sliding window rate limiter (per `RateLimitPerMinute`) on all `/tasks/*` endpoints.

| File | Change |
|------|--------|
| `src/Diva.Host/Program.cs` | Added `AddRateLimiter` with `a2a` policy + `app.UseRateLimiter()` |
| `src/Diva.Host/Controllers/AgentTaskController.cs` | Added `[EnableRateLimiting("a2a")]` attribute |

### A2A Settings admin page

Read-only platform A2A config dashboard at `/settings/a2a`.

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/AdminController.cs` | Added `A2AConfigController` — `GET /api/admin/a2a-config` |
| `admin-portal/src/components/A2ASettings.tsx` | New settings page component |
| `admin-portal/src/App.tsx` | Added `/settings/a2a` route |
| `admin-portal/src/components/layout/app-sidebar.tsx` | Added "A2A Protocol" nav item |
| `admin-portal/src/api.ts` | Added `getA2AConfig()` + `A2AConfig` type + 429 rate limit handling |

### A2A test suite (23 tests)

| File | Change |
|------|--------|
| `tests/Diva.Agents.Tests/A2AAgentClientTests.cs` | New — SSE parsing, auth header routing, depth increment |
| `tests/Diva.Agents.Tests/AgentCardBuilderTests.cs` | New — card schema, archetype mapping, URL fallback |
| `tests/Diva.Agents.Tests/RemoteA2AAgentTests.cs` | New — credential resolution, streaming delegation, auth schemes |

### Pre-existing fix: SchedulerTests

Fixed 4 compile errors in `SchedulerTests.cs` — `BuildPrompt` was changed to instance method (takes `runId`) but test calls were not updated.

| File | Change |
|------|--------|
| `tests/Diva.Agents.Tests/SchedulerTests.cs` | Updated 4 `BuildPrompt` calls to use instance + `runId` parameter |

### Docs

| File | Change |
|------|--------|
| `docs/INDEX.md` | Phase 14 status updated from `[ ]` to `[x]` |

---

## [2026-04-10] Bug fixes — agent LLM config save, scheduled task template resolution

### Agent editor: LLM config and model not saved on update

`PUT /api/agents/{id}` was missing `ModelId` and `LlmConfigId` from the property mapping — both fields were silently dropped on every save.

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/AgentsController.cs` | Added `existing.ModelId` and `existing.LlmConfigId` assignments in `Update()` |

### Scheduled task template: built-in variables not resolved, array values crash

Two bugs in the scheduled task prompt builder:

1. **Built-in variables** (`{{current_date}}`, `{{current_time}}`, `{{current_datetime}}`) were never substituted — `BuildPrompt`/`BuildGroupPrompt` ran a manual replace loop that only handled user-defined `ParametersJson` variables.
2. **Array/object values** in `ParametersJson` (e.g. `"location": ["Don valley"]`) caused a `JsonException` and skipped all substitution because `ParseJson` deserialized to `Dictionary<string, string>`.
3. **Fixed-prompt tasks** (`payloadType = "prompt"`) also skipped built-in resolution since the early-return guard checked `payloadType != "template"`.

Fix: moved `PromptVariableResolver` from `Diva.TenantAdmin.Prompts` → `Diva.Core.Prompts` so `Diva.Infrastructure` can consume it; rewrote `ParseJson` to deserialize to `Dictionary<string, JsonElement>` and stringify all value types (arrays join with `", "`); rewrote both build methods to always call the resolver; converted them to instance methods so `_logger` is available.

| File | Change |
|------|--------|
| `src/Diva.Core/Prompts/PromptVariableResolver.cs` | New location — moved from TenantAdmin; `ParseJson` now accepts any JSON value type |
| `src/Diva.Core/Diva.Core.csproj` | Added `Microsoft.Extensions.Logging.Abstractions` package reference |
| `src/Diva.TenantAdmin/Prompts/PromptVariableResolver.cs` | Deleted — replaced by `Diva.Core` version |
| `src/Diva.TenantAdmin/Prompts/TenantAwarePromptBuilder.cs` | Updated `using` to `Diva.Core.Prompts` |
| `src/Diva.Infrastructure/Scheduler/SchedulerHostedService.cs` | `BuildPrompt`/`BuildGroupPrompt` rewritten as instance methods; use `PromptVariableResolver`; debug logging added |
| `tests/Diva.TenantAdmin.Tests/PromptVariableResolverTests.cs` | Updated `using` to `Diva.Core.Prompts` |

---

## [2026-04-10] Public Architecture Documentation Site

Created a standalone MkDocs Material documentation site in `agent-docs/` for publishing the Diva AI agent architecture as a GitHub Pages site. The site is explanation-only (no code), targeting both internal engineers and external platform evaluators.

### Site structure

12 documentation pages organized into 5 sections:

| Section | Pages |
|---------|-------|
| Getting Started | Platform Overview |
| Core Concepts | ReAct Loop, Supervisor Pipeline, Archetypes, Lifecycle Hooks |
| Tool Integration | MCP Integration, Parallel Tool Execution |
| Quality & Reliability | Response Verification, Context Management |
| Streaming | SSE Events & Real-Time Streaming |
| Multi-Tenancy | Tenant-Aware Agents |

### Features

- **MkDocs Material 9.7.6** — dark/light theme toggle, search, navigation tabs
- **Mermaid diagrams** — architecture diagrams, sequence diagrams, flowcharts throughout all pages via `pymdownx.superfences`
- **GitHub Actions workflow** — auto-deploys on push to `main` when `agent-docs/**` changes (`peaceiris/actions-gh-pages@v4`, `gh-pages` branch)
- **`python -m mkdocs serve --config-file agent-docs/mkdocs.yml`** — local preview (mkdocs not on system PATH by default; use `python -m mkdocs` or add `C:\Users\Admin\AppData\Roaming\Python\Python311\Scripts` to PowerShell profile)

### Content sync (as of 2026-04-10)

Documentation reflects all changelog entries through 2026-04-10:
- Per-iteration model switching (3-priority-layer table, provider strategy pattern, `model_switch` SSE event)
- `MatchTarget` on Rule Pack `model_switch` rules (`query` vs `response` target)
- `WasTruncated` and `LastIterationResponse` flags on `AgentHookContext`
- Max-tokens nudge-once behavior and post-tool nudge
- Per-agent `MaxOutputTokens` override
- Platform API Keys (`diva_` prefix, SHA-256, `X-API-Key` header, `FullAccess`/`AgentInvoke` scopes)
- MCP Credential Vault (AES-256-GCM, 3-tier auth, `CredentialRef`)
- Per-agent business rules (global vs. agent-scoped rules, stacking behavior)
- Group-level agents (templates, overlays, activation model)


---

## [2026-04-16] Credential Forwarding Fix & Anthropic API Key Docker Clarification

### MCP tool credential forwarding bug (security)

MCP tool calls were incorrectly forwarding the inbound Diva platform API key (`X-API-Key`) to MCP servers. This was a security risk, as only the configured `CredentialRef` should be used for outbound MCP authentication.

**Fix:**
- Removed all code in `McpConnectionManager` that forwarded the inbound API key to MCP servers. Now, only the resolved `CredentialRef` is used for MCP tool calls.
- Confirmed that A2A agent calls still use the inbound API key for platform authentication, but it is never forwarded to MCP servers.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/McpConnectionManager.cs` | Removed InboundApiKey forwarding logic |

### Anthropic SDK "x-api-key header is required" error (Docker)

When running in Docker, the Anthropic SDK failed with an authentication error if the `ANTHROPIC_API_KEY` was only set in `.env.development` and not in the main `.env` file. Docker Compose loads `.env` by default, not `.env.development`.

**Resolution:**
- Ensure `ANTHROPIC_API_KEY` is present in the main `.env` file (or use `--env-file .env.development` with Docker Compose).
- Documented this requirement for all LLM provider API keys in Docker environments.

| File | Change |
|------|--------|
| `docs/agents.md` | Clarified Anthropic API key Docker env usage |
| `src/Diva.Infrastructure/LiteLLM/McpConnectionManager.cs` | Credential forwarding fix |
| `.env.example` | Added comment about Docker Compose env loading |
| `docs/changelog.md` | This entry |

---

## [2026-04-10] Rule Pack: Response-Text-Triggered model_switch (MatchTarget)

Extends the `model_switch` rule type at `OnBeforeIteration` to match its `Pattern` against the **previous iteration's response text** in addition to the original user query. Allows rules like "switch to a stronger model when the agent announces it is about to send an email."

### How it works

- New `MatchTarget` field on `HookRuleEntity` — `"query"` (default, no behaviour change) or `"response"`
- When `MatchTarget = "response"`, pattern is matched against `AgentHookContext.LastIterationResponse` (the LLM's text output from the most recent iteration that made tool calls)
- On iteration 1 (no prior response), rules with `MatchTarget = "response"` and a non-blank pattern are skipped automatically
- Blank pattern with `MatchTarget = "response"` fires from iteration 2 onward (same as blank pattern + `"query"` behaviour)
- Both Anthropic and OpenAI-compatible paths covered — both share `ExecuteReActLoopAsync`

### UI

- PackEditor: `model_switch` rule at `OnBeforeIteration` now shows a **"Match Pattern Against"** select: "User query" or "Previous iteration response"
- Pattern field label and placeholder update to reflect the selected target
- Help text updated on the `OnBeforeIteration` hook point and `model_switch` rule type

### DB Migration

- `AddMatchTargetToHookRules` — adds `MatchTarget TEXT NOT NULL DEFAULT 'query'` to `HookRules`

| File | Change |
|------|--------|
| `src/Diva.Core/Models/IAgentLifecycleHook.cs` | Added `LastIterationResponse` to `AgentHookContext` |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Sets `hookCtx.LastIterationResponse = finalResponse` after each tool-call iteration |
| `src/Diva.Infrastructure/Data/Entities/RulePackEntities.cs` | Added `MatchTarget` property to `HookRuleEntity` |
| `src/Diva.Infrastructure/Data/Migrations/20260410135851_AddMatchTargetToHookRules.cs` | **New** — migration |
| `src/Diva.TenantAdmin/Services/RulePackService.cs` | Added `MatchTarget` to `CreateHookRuleDto`, `UpdateHookRuleDto`; wired in `AddRuleAsync`, `UpdateRuleAsync`, `ClonePackAsync` |
| `src/Diva.TenantAdmin/Services/RulePackEngine.cs` | `EvaluateOnBeforeIteration` + `EvaluateAtHookPoint` + `EvaluateRule` + `EvalModelSwitch` — thread `lastIterationResponse`; match against it when `MatchTarget == "response"` |
| `src/Diva.Agents/Hooks/BuiltIn/TenantRulePackHook.cs` | Passes `context.LastIterationResponse` to `EvaluateOnBeforeIteration` |
| `src/Diva.Host/Controllers/RulePackController.cs` | `RulePackExport.RuleExport` + export/import paths include `MatchTarget` |
| `admin-portal/src/api.ts` | Added `matchTarget` to `HookRule`, `CreateHookRuleDto`, `UpdateHookRuleDto` |
| `admin-portal/src/components/HookRuleForm.tsx` | "Match Against" select for `model_switch` + `OnBeforeIteration`; label/placeholder/help updates |
| `admin-portal/src/components/PackEditor.tsx` | `matchTarget` copied on edit open; included in create/update DTO payloads |
| `tests/Diva.TenantAdmin.Tests/RulePackEngineTests.cs` | 4 new tests (query match, response match, first-iteration skip, blank-pattern fire) |

---

## [2026-04-08] MCP Credential Vault + Platform API Keys

Full-stack implementation of credential vault for MCP tool call authentication and platform API keys for non-SSO access. Enables non-SSO users (service accounts, CI pipelines, scheduled tasks) to authenticate both to the platform and to external MCP tool servers.

### Credential Vault (MCP tool auth)

- **AES-256-GCM encryption** for credential secrets stored in DB (`AesCredentialEncryptor`)
- Encrypted layout: `[12-byte nonce][ciphertext][16-byte tag]` → base64
- `Credentials:MasterKey` config (base64, 32 bytes); ephemeral random key fallback for dev (logs warning)
- `ICredentialResolver` with 2-minute in-memory cache, singleton-safe via `IDatabaseProviderFactory`
- `McpToolBinding.CredentialRef` links a binding to a named credential
- HTTP/SSE: 3-tier auth priority — SSO Bearer → credential vault → tenant headers only
- Stdio: credential injected as `MCP_API_KEY` environment variable
- Auth schemes: `Bearer`, `ApiKey` (X-API-Key header), `Custom` (configurable header name)
- Admin UI at `/settings/credentials` (CRUD)

### Platform API Keys (non-SSO platform access)

- `diva_` prefix + 32-byte random; stored as SHA-256 hash only (raw key shown once)
- `X-API-Key` header validated in `TenantContextMiddleware` before Bearer JWT check
- Scope: `FullAccess` or `AgentInvoke`; optional `AllowedAgentIds` restriction
- Create/validate/list/revoke/rotate via `IPlatformApiKeyService`
- Admin UI at `/settings/api-keys`

### Scheduled Task Credential Flow

- `IMcpConnectionManager.ConnectAsync` accepts `TenantContext? fallbackTenant`
- `AnthropicAgentRunner` passes `tenant` at both connect call sites (initial + stale reconnect)
- Headers factory: `ctx?.TryGetTenantContext() ?? fallbackTenant` — ensures tenant headers + credential auth work when `HttpContext` is null

### MCP Call Logging

- Structured logging at all decision points in `McpConnectionManager`
- Debug: credential resolution, headers factory source, tenant-only injection
- Information: credential resolved (scheme), SSO/credential/custom injection
- Warning: credential not resolved, no resolver, no auth, unknown scheme fallback

### DB Migration

- `McpCredentials` table (ITenantEntity): Id, TenantId, Name, EncryptedApiKey, AuthScheme, CustomHeaderName, Description, CreatedAt, ExpiresAt, IsActive, LastUsedAt, CreatedByUserId
- `PlatformApiKeys` table (ITenantEntity): Id, TenantId, Name, KeyHash, KeyPrefix, Scope, AllowedAgentIdsJson, CreatedAt, ExpiresAt, IsActive, LastUsedAt, CreatedByUserId

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/CredentialOptions.cs` | **New** — `MasterKey` config class |
| `src/Diva.Core/Configuration/ICredentialEncryptor.cs` | **New** — Encrypt/Decrypt interface |
| `src/Diva.Core/Configuration/ICredentialResolver.cs` | **New** — ResolveAsync + `ResolvedCredential` record |
| `src/Diva.Core/Configuration/IPlatformApiKeyService.cs` | **New** — Platform API key interface + DTOs |
| `src/Diva.Infrastructure/Auth/AesCredentialEncryptor.cs` | **New** — AES-256-GCM with ephemeral key fallback |
| `src/Diva.Infrastructure/Auth/CredentialResolver.cs` | **New** — 2-min cache, fire-and-forget LastUsedAt |
| `src/Diva.Infrastructure/Auth/PlatformApiKeyService.cs` | **New** — SHA-256 hash, `diva_` prefix keys |
| `src/Diva.Infrastructure/Auth/TenantContextMiddleware.cs` | X-API-Key branch before Bearer check |
| `src/Diva.Infrastructure/Data/Entities/McpCredentialEntity.cs` | **New** — ITenantEntity |
| `src/Diva.Infrastructure/Data/Entities/PlatformApiKeyEntity.cs` | **New** — ITenantEntity |
| `src/Diva.Infrastructure/Data/DivaDbContext.cs` | DbSets, query filters, unique indexes |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | `CredentialRef` on McpToolBinding; passes `ICredentialResolver` + `tenant` to MCP connector |
| `src/Diva.Infrastructure/LiteLLM/IMcpConnectionManager.cs` | `TenantContext? fallbackTenant` param |
| `src/Diva.Infrastructure/LiteLLM/McpConnectionManager.cs` | Credential resolution, 3-tier auth, structured logging |
| `src/Diva.Infrastructure/A2A/A2AAgentClient.cs` | Bearer/ApiKey/Custom auth schemes |
| `src/Diva.Agents/Workers/RemoteA2AAgent.cs` | Resolves `A2ASecretRef` via credential vault |
| `src/Diva.Agents/Registry/DynamicAgentRegistry.cs` | Injects `ICredentialResolver` for RemoteA2AAgent |
| `src/Diva.Host/Controllers/CredentialsController.cs` | **New** — `/api/admin/credentials` CRUD |
| `src/Diva.Host/Controllers/ApiKeysController.cs` | **New** — `/api/admin/api-keys` CRUD |
| `src/Diva.Host/Program.cs` | DI registrations |
| `src/Diva.Host/appsettings.json` | `Credentials` section with `MasterKey` |
| `admin-portal/src/api.ts` | Credential + API key types and methods |
| `admin-portal/src/components/CredentialManager.tsx` | **New** — MCP credentials UI |
| `admin-portal/src/components/ApiKeyManager.tsx` | **New** — Platform API keys UI |
| `admin-portal/src/components/AgentBuilder.tsx` | Credential selector per HTTP/SSE binding |
| `admin-portal/src/components/layout/app-sidebar.tsx` | Nav items for credentials and API keys |
| `admin-portal/src/App.tsx` | Routes for `/settings/credentials` and `/settings/api-keys` |
| `tests/Diva.Agents.Tests/AesCredentialEncryptorTests.cs` | **New** — 8 tests |
| `tests/Diva.Agents.Tests/CredentialResolverTests.cs` | **New** — 9 tests |
| `tests/Diva.Agents.Tests/PlatformApiKeyServiceTests.cs` | **New** — 10 tests |

---

## [2026-04-06] Phase 18 Addendum — Group Agent Template Builder + "Publish to Group"

Extends Phase 18 with a full-featured template builder for platform admins and a one-click "Publish to Group" flow for tenant admins.

### GroupAgentTemplateBuilder (new component)

Full-page 4-tab builder mirroring `AgentBuilder.tsx`, targeting `/api/platform/groups/:groupId/agents`.

- **Identity tab**: name (slug), displayName, description, agentType (Select), status, isEnabled
- **Model & Prompt tab**: llmConfigId, modelId, temperature (slider), maxIterations, systemPrompt
- **Tool Servers tab**: full MCP binding editor (stdio/SSE/HTTP), same pattern as AgentBuilder
- **Advanced tab**: verificationMode, maxContinuations, maxToolResultChars, maxOutputTokens, contextWindowJson, customVariablesJson, pipelineStagesJson, toolFilterJson, stageInstructionsJson, executionMode, hooksJson, a2aEndpoint/authScheme/secretRef
- **Import from Agent** (create mode): dialog lists tenant's own agents; on selection fetches full `AgentDefinition` and maps all 26 fields into form state
- **Pre-population via router state**: `location.state?.importAgent` — used by "Publish to Group" flow; builder reads state on mount and pre-fills form
- Routes: `GET /platform/groups/:groupId/agents/new` (create) and `/platform/groups/:groupId/agents/:templateId/edit` (edit)

### "Publish to Group" flow (AgentList)

Tenant admin can now publish any own agent as a group template in one action:
- Dropdown item "Publish to Group" on non-shared agents
- Opens inline `PublishToGroupDialog` populated from `GET /api/agents/my-groups` (tenant-scoped endpoint — only groups the current tenant belongs to)
- On confirm: fetches full `AgentDefinition` via `api.getAgent()` then navigates to `GroupAgentTemplateBuilder` with `state: { importAgent }` pre-populated

### GroupDetail — inline dialog replaced with navigation

- "New Agent Template" → `navigate('/platform/groups/${groupId}/agents/new')`
- Edit (pencil) → `navigate('/platform/groups/${groupId}/agents/${a.id}/edit')`
- Delete confirm remains inline; Status column added to agents table

### Backend fixes

- **`GET /api/agents/my-groups`** — new endpoint in `AgentsController`; queries `TenantGroupMembers` filtered by `TenantId` (entity has no `ITenantEntity`, must filter manually); returns `{ id, name, description }[]`
- **`GET /api/platform/groups/{id}/agents/{templateId}`** — projection was missing all Phase-15 fields (`ArchetypeId`, `HooksJson`, `A2AEndpoint`, `A2AAuthScheme`, `A2ASecretRef`, `ExecutionMode`, `ModelSwitchingJson`, `MaxToolResultChars`, `MaxOutputTokens`, `LlmConfigId`); fixed to include all fields — caused archetype and hooks to be lost on edit load
- **`TenantContext.DevMasterAdmin()`** — new factory (`TenantId=0`, roles `["master_admin","admin","system"]`); dev bypass in `TenantContextMiddleware` now uses this instead of `System(tenantId:1)`; fixes all platform-level endpoints returning 403 in dev mode

### MSW sandbox

- `GET /api/agents/my-groups` → returns fixture `[{ id: 1, name: "Platform Group" }]`
- `GET/POST/PUT/DELETE /api/platform/groups/:groupId/agents` — 5 CRUD handlers over mutable `MOCK_TEMPLATES` array
- All placed before `GET /api/agents/:id` wildcard to prevent route capture

| File | Change |
|------|--------|
| `admin-portal/src/components/GroupAgentTemplateBuilder.tsx` | **New** — full 4-tab builder with inline McpBindingEditor, AdvancedConfigPanel, ImportAgentDialog |
| `admin-portal/src/components/GroupDetail.tsx` | Replaced inline 5-field dialog with navigation; Status column in agents table |
| `admin-portal/src/components/AgentList.tsx` | "Publish to Group" dropdown + PublishToGroupDialog; `handleOpenPublish` using `api.listMyGroups()` |
| `admin-portal/src/App.tsx` | Routes for `GroupAgentTemplateBuilder` (new + edit) |
| `admin-portal/src/api.ts` | `listMyGroups()` — calls `GET /api/agents/my-groups` |
| `admin-portal/src/mocks/handlers.ts` | 5 new group-agent CRUD handlers; `my-groups` handler; mutable `MOCK_TEMPLATES` |
| `src/Diva.Core/Models/TenantContext.cs` | `DevMasterAdmin()` static factory |
| `src/Diva.Infrastructure/Auth/TenantContextMiddleware.cs` | Dev bypass uses `DevMasterAdmin()` instead of `System(1)` |
| `src/Diva.Host/Controllers/AgentsController.cs` | `GET /api/agents/my-groups` endpoint |
| `src/Diva.Host/Controllers/GroupsController.cs` | Expanded GET projection to include all Phase-15 fields |

---

## [2026-04-06] AnthropicAgentRunner Refactor — Testable Interfaces + Method Extraction

Refactored `AnthropicAgentRunner` and companion infrastructure for testability and maintainability. All changes are behaviorally equivalent — no logic was altered.

### Interface extraction (testability)

- **`IMcpConnectionManager`** — new public interface; `McpConnectionManager` implements it. Tests can now `Substitute.For<IMcpConnectionManager>()` without spawning real child processes. Registered as `AddSingleton<IMcpConnectionManager, McpConnectionManager>()` in `Program.cs`.
- **`IReActHookCoordinator`** — new public interface; `ReActHookCoordinator` implements it. Tests can substitute the full hook coordinator without a real `IAgentHookPipeline`. Registered as `AddSingleton<IReActHookCoordinator, ReActHookCoordinator>()` in `Program.cs`.
- Both are injected into `AnthropicAgentRunner` as optional last constructor parameters (backward-compatible; existing tests pass `null` / omit).
- `HookInvocationResult` made `public` (required because `IReActHookCoordinator` is public and returns it).
- `ILogger` → `ILogger<T>` on both `McpConnectionManager` and `ReActHookCoordinator` (plain `ILogger` cannot be resolved by DI).

### Method extraction (line count reduction ~1427 → ~1200)

Previous session extracted `McpConnectionManager` (MCP lifecycle) and added `ApplyExecutionModeFilter` to `ReActToolHelper`. This session extracted two more large inline blocks:

- **`CallLlmForIterationAsync`** — extracts the 55-line streaming + buffered LLM call block; returns `LlmCallResult(Response, Error, TextDeltas)`. The caller yields text-delta chunks and handles `continue`/`break` on error (control flow that cannot be inside an extracted method).
- **`ExecuteToolPipelineAsync`** — extracts the ~200-line tool pipeline (Phase 1 announce, Phase 2 dedup/execute/hook/retry, Phase 3 results/history/replan); returns `ToolPipelineOutcome(StreamError, ConsecutiveFailures, HadToolErrors, Chunks)`. Mutable `toolsUsed`, `toolEvidence`, `executionLog` lists are passed by reference and mutated in place.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/IMcpConnectionManager.cs` | New public interface (`ConnectAsync`, `BuildToolDataAsync`) |
| `src/Diva.Infrastructure/LiteLLM/McpConnectionManager.cs` | Implements `IMcpConnectionManager`; `public sealed`; `static BuildToolDataAsync` → instance; `ILogger<McpConnectionManager>` |
| `src/Diva.Infrastructure/LiteLLM/IReActHookCoordinator.cs` | New public interface (7 lifecycle methods) |
| `src/Diva.Infrastructure/LiteLLM/ReActHookCoordinator.cs` | Implements `IReActHookCoordinator`; `public sealed`; `ILogger<ReActHookCoordinator>`; `HookInvocationResult` made `public` |
| `src/Diva.Infrastructure/LiteLLM/ReActToolHelper.cs` | Added `ApplyExecutionModeFilter` static method (ChatOnly/ReadOnly/Supervised switch) |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Fields `IMcpConnectionManager`, `IReActHookCoordinator`; optional ctor params; `CallLlmForIterationAsync`; `ExecuteToolPipelineAsync`; `LlmCallResult` + `ToolPipelineOutcome` records; `using Microsoft.Extensions.Logging.Abstractions` |
| `src/Diva.Host/Program.cs` | `AddSingleton<IMcpConnectionManager, McpConnectionManager>()`, `AddSingleton<IReActHookCoordinator, ReActHookCoordinator>()` |
| `tests/Diva.Agents.Tests/HookCoordinatorTests.cs` | `NullLogger.Instance` → `NullLogger<ReActHookCoordinator>.Instance` |

---

## [2026-04-01] Phase 18 — Group-Level Agents + Per-Agent Business Rules

### Feature 1: Group Agent Overlay

Agents can now be defined once at the tenant-group level (`GroupAgentTemplateEntity`) and activated per-tenant without copying the canonical definition. Tenants store only deltas in a new `TenantGroupAgentOverlayEntity`; `GroupAgentOverlayMerger.Merge(template, overlay, tenantId)` produces a synthetic `AgentDefinitionEntity` at runtime.

- **Explicit activation required**: a group template only appears in `DynamicAgentRegistry` if the tenant has an overlay with `IsEnabled=true`.
- **DynamicAgentRegistry** now loads group templates + overlay map after own agents; own agents always take precedence by ID.
- **Cache propagation**: `TenantGroupService.UpdateAgentTemplateAsync` calls `IGroupAgentOverlayService.InvalidateCache` for all member tenants.

**API endpoints** (`/api/agents/group-templates`): list, get, GET/POST/PUT/DELETE/PATCH overlay.

### Feature 2: Per-Agent Business Rules

`TenantBusinessRuleEntity` gains a nullable `AgentId` (soft FK, no cascade). Rules with `AgentId=null` remain global (backward-compatible). Rules with `AgentId` set filter to that agent only.

- `IPromptBuilder.BuildAsync` now accepts optional `agentId` (6th param); `AnthropicAgentRunner` passes `definition.Id`.
- Cache key extended: `rules_{tenantId}_{agentType}_{agentId}` when scoped.
- `BusinessRules.tsx` UI: "Scope to specific agent" checkbox + agent Select; agent name shown in table.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Data/Entities/TenantGroupEntities.cs` | Added `TenantGroupAgentOverlayEntity` (int PK + string Guid) |
| `src/Diva.Infrastructure/Data/Entities/BusinessRuleEntity.cs` | Added `Guid` + `AgentId` to `TenantBusinessRuleEntity` |
| `src/Diva.Infrastructure/Data/DivaDbContext.cs` | DbSet, query filter, indexes, UpdatedAt hook |
| `src/Diva.Infrastructure/Data/Migrations/20260406121811_AddBusinessRuleAgentId.cs` | Migration: Guid + AgentId columns |
| `src/Diva.Infrastructure/Data/Migrations/20260406122432_AddGroupAgentOverlay.cs` | Migration: GroupAgentOverlays table |
| `src/Diva.Infrastructure/Groups/GroupAgentOverlayMerger.cs` | New public static merge helper |
| `src/Diva.TenantAdmin/Services/IGroupAgentOverlayService.cs` | New interface + DTOs |
| `src/Diva.TenantAdmin/Services/GroupAgentOverlayService.cs` | New Singleton-safe implementation |
| `src/Diva.TenantAdmin/Services/ITenantGroupService.cs` | Added `GetMemberTenantIdsAsync` |
| `src/Diva.TenantAdmin/Services/TenantGroupService.cs` | Cache propagation on template update; `SetOverlayService` injection |
| `src/Diva.TenantAdmin/Services/ITenantBusinessRulesService.cs` | Optional `agentId` on query/cache methods; DTO fields |
| `src/Diva.TenantAdmin/Services/TenantBusinessRulesService.cs` | Scoped query + extended cache key |
| `src/Diva.Core/Models/IPromptBuilder.cs` | Added optional `agentId` param |
| `src/Diva.TenantAdmin/Prompts/TenantAwarePromptBuilder.cs` | Pass `agentId` to rules fetch |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Pass `definition.Id` as `agentId` to builder |
| `src/Diva.Agents/Registry/DynamicAgentRegistry.cs` | Inject overlay+group services; include activated overlays |
| `src/Diva.Host/Controllers/AgentsController.cs` | 7 overlay endpoints; inject `IGroupAgentOverlayService` |
| `src/Diva.Host/Program.cs` | Register `IGroupAgentOverlayService`; post-DI `SetOverlayService` wire |
| `admin-portal/src/api.ts` | `GroupAgentOverlay`, `GroupTemplateSummary`, overlay API functions |
| `admin-portal/src/components/AgentList.tsx` | Activate/Deactivate/Customize actions for shared templates |
| `admin-portal/src/components/GroupAgentOverlayEditor.tsx` | New overlay editor (template read-only + editable overlay) |
| `admin-portal/src/App.tsx` | Route `/agents/group/:templateId/overlay` |
| `admin-portal/src/mocks/handlers.ts` | MSW stubs for all overlay endpoints |

---

## [2026-04-01] SSO One-to-One User Mapping Fix

**Problem:** Multiple different SSO users could collapse into the same `UserProfileEntity` row, and the same user could accumulate duplicate rows after an SSO provider change. Five root causes were identified and resolved.

### RC-1 fix — Removed `"sso-user"` fallback (AuthController)
The `userId ?? userEmail ?? "sso-user"` chain meant every user whose SSO provider returned no `sub` or email shared a single profile row per tenant. Replaced with an explicit 400 response: login now fails cleanly if neither identifier is returned.

### RC-2 fix — ID token parsed before userinfo call (AuthController)
The OIDC `id_token` JWT (present in every standard token endpoint response) was ignored. The callback now decodes it (without signature verification — claims only) to extract `sub`/email/name as a reliable fallback when the userinfo endpoint is absent or fails.

### RC-3 fix — Two-phase upsert lookup (UserProfileService)
`UpsertOnLoginAsync` previously looked up only by `UserId` (sub). Now uses a two-phase strategy: Phase 1 exact match by `UserId` (indexed), Phase 2 case-insensitive email match. If Phase 2 finds a row with a different sub, it updates `UserId` to the new value — linking the SSO identity to the existing profile (handles SSO provider migrations).

### RC-4 fix — Unique `(TenantId, Email)` index (DivaDbContext + migration)
Added a filtered unique index on `(TenantId, Email)` where `Email != ''` to enforce one email → one profile at the DB level. Migration `20260401120000_AddUserProfileEmailIndex`.

### RC-5 fix — Per-tenant `ClaimMappingsJson` applied in callback (AuthController)
`TenantSsoConfigEntity.ClaimMappingsJson` was used only during local JWT validation but **never** during the initial extraction from the external provider's userinfo/id_token responses. The callback now deserializes `ClaimMappingsJson` and uses the configured field names (`UserId`, `Email`, `DisplayName`, `Roles`) with fallback to OIDC defaults. This is critical for providers like Azure AD (uses `oid` not `sub`) or Exchange (uses `mail` not `email`).

**Bonus:** Roles are now extracted from both the id_token and userinfo endpoint and passed into `IssueSsoJwt` (was hardcoded to `[]`).

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/OAuthOptions.cs` | Added `Email` and `DisplayName` fields to `ClaimMappingsOptions` |
| `src/Diva.Host/Controllers/AuthController.cs` | Per-tenant claim field resolution, id_token parsing, `TryGetStringArray` helper, removed `"sso-user"` fallback, roles extraction |
| `src/Diva.TenantAdmin/Services/UserProfileService.cs` | Two-phase sub→email lookup with sub-linking on email match |
| `src/Diva.Infrastructure/Data/DivaDbContext.cs` | Filtered unique index `(TenantId, Email)` |
| `src/Diva.Infrastructure/Data/Migrations/20260401120000_AddUserProfileEmailIndex.cs` | New migration |

---

## [2026-03-28] Phase 17 Implemented — Agent Setup Assistant + Version History

### Overview

Full implementation of Phase 17 across backend (C#), frontend (React/TypeScript), prompt templates, EF migration, and tests. All 300 tests pass (189 TenantAdmin + 110 Agents + 1 Tools).

### New: AI Suggestion Endpoints

Three LLM-powered suggestion endpoints driven by `AgentSetupAssistant` service (registered as Singleton via `IDatabaseProviderFactory`). Uses the same `LlmRuleExtractor` calling pattern (Anthropic SDK for Anthropic provider, `IChatClient` for OpenAI-compatible).

- `POST /api/agents/{id}/suggest-prompt` — system prompt creation/refinement
- `POST /api/agents/{id}/suggest-rule-packs` — contextual rule pack recommendations  
- `POST /api/rule-packs/suggest-regex` — AI regex builder with sample-based testing

**Files:** `src/Diva.Core/Models/AgentSetupDtos.cs` (new), `src/Diva.TenantAdmin/Services/AgentSetupAssistant.cs` (new), `src/Diva.Host/Controllers/AgentsController.cs`, `src/Diva.Host/Controllers/RulePackController.cs`

### New: Context Enrichers

Two enrichers implement `ISetupAssistantContextEnricher` to populate `AgentSetupContext` before the LLM call:

- `ArchetypeContextEnricher` — validates/normalizes archetype ID from live `IArchetypeRegistry`
- `LlmConfigContextEnricher` — queries `TenantLlmConfigs` + `PlatformLlmConfigs` for model-switch-aware suggestions

**Files:** `src/Diva.TenantAdmin/Services/Enrichers/ArchetypeContextEnricher.cs` (new), `src/Diva.TenantAdmin/Services/Enrichers/LlmConfigContextEnricher.cs` (new)

### New: Append-only Version History

- `AgentPromptHistoryEntity` / `RulePackHistoryEntity` — EF entities with `Source` field (`"manual"`, `"assistant_create"`, `"assistant_refine"`, `"restore"`)
- 6 history/restore endpoints on `AgentsController` and `RulePackController`
- EF migration `20260328150000_AddAgentHistory` with unique indexes on `(TenantId, AgentId, Version)` and `(TenantId, PackId, Version)`

**Files:** `src/Diva.Infrastructure/Data/Entities/AgentHistoryEntities.cs` (new), `src/Diva.Infrastructure/Data/DivaDbContext.cs`, `src/Diva.Infrastructure/Data/Migrations/20260328150000_AddAgentHistory.cs` (new)

### New: `RulePackRuleCompatibility.AsMarkdownTable()`

Generates a markdown compatibility table for injection into LLM prompt context.

**File:** `src/Diva.TenantAdmin/Services/RulePackRuleCompatibility.cs`

### New: `AgentOptions.MaxSuggestionTokens`

Per-tenant cap on tokens used for setup assistant LLM calls. Default 1024.

**File:** `src/Diva.Core/Configuration/AgentOptions.cs`

### New: Prompt Templates

Three new versioned prompt templates for the setup assistant:

**Files:** `prompts/agent-setup/system-prompt-generator.txt` (new), `prompts/agent-setup/rule-pack-generator.txt` (new), `prompts/agent-setup/regex-generator.txt` (new)

### New: DI Registrations

`PromptTemplateStore`, `ArchetypeContextEnricher`, `LlmConfigContextEnricher`, `IAgentSetupAssistant` → `AgentSetupAssistant` registered in `Program.cs`.

**File:** `src/Diva.Host/Program.cs`

### New: Frontend Components

- `AgentAssistantDrawer.tsx` — 3-step Sheet wizard (Context → Prompt → Rule Packs) in AgentBuilder
- `RegexAssistantDialog.tsx` — AI regex builder dialog with sample arrays, preview match table, apply button
- All Phase 17 TypeScript types and API methods added to `api.ts`
- 8 MSW mock handlers added to `handlers.ts` for sandbox/mock mode

**Files:** `admin-portal/src/components/AgentAssistantDrawer.tsx` (new), `admin-portal/src/components/RegexAssistantDialog.tsx` (new), `admin-portal/src/api.ts`, `admin-portal/src/mocks/handlers.ts`

### New: Tests

- `RulePackCompatibilityMatrixTests.cs` — 15 tests covering compatibility matrix, `IsValid`, `ValidateOrThrow`, `AsMarkdownTable`
- `AgentSetupAssistantTests.cs` — history CRUD, JSON proxy validation, regex validation, context mutation, path traversal guard

**Files:** `tests/Diva.TenantAdmin.Tests/RulePackCompatibilityMatrixTests.cs` (new), `tests/Diva.TenantAdmin.Tests/AgentSetupAssistantTests.cs` (new)

### Fix: Test streaming flag for Agents.Tests

`AnthropicAgentRunnerTests` and `ToolOptimizationTests` now set `EnableResponseStreaming = false` in test `AgentOptions` so they use the buffered `CallLlmAsync` path (these tests were written before streaming existed and mock `GetClaudeMessageAsync`, not `StreamClaudeMessageAsync`).

**Files:** `tests/Diva.Agents.Tests/AnthropicAgentRunnerTests.cs`, `tests/Diva.Agents.Tests/ToolOptimizationTests.cs`

---

## [2026-03-28] Phase 17 Plan Finalized — Agent Setup Assistant + Version History

### Overview

Final implementation plan created for Phase 17 in `docs/phase-17-agent-setup-assistant.md`.
The plan now covers AI-assisted create/refine workflows for agent system prompts and rule packs,
plus explicit version history support for both prompt and rule packs (timeline, compare, restore).

### Planned capabilities captured in the phase doc

- Two assistant endpoints: prompt suggestion + rule pack suggestion
- Create and refine modes with edit-intent output (`add`/`update`/`delete`/`keep`)
- Dynamic prompt assembly from live compatibility matrix + archetype registry + prompt templates
- `model_switch`-aware rule suggestions using tenant `AvailableLlmConfigs` and `LlmConfigId`
- Per-agent prompt history and per-pack rule history with append-only restore semantics
- UI history tabs with compare and restore confirmation flows
- Security guardrails (tenant scoping, sanitization, length limits, rate limiting)
- Unit test plan for matrix alignment, suggestion quality, and history behavior

### Documentation sync

- Added Phase 17 row to `docs/INDEX.md` with status `[ ]` and link to
    `docs/phase-17-agent-setup-assistant.md`.

---

## [2026-03-28] Per-Iteration Smart Model Switching

### Overview

The ReAct loop now supports switching LLM model (and provider) between iterations to reduce token cost. Three configurable layers apply in priority order: Rule Pack `model_switch` rules (tenant-level), per-agent `ModelSwitchingOptions` JSON config, and a smart auto-router hook driven by agent Variables. Cross-provider switching (Anthropic ↔ OpenAI-compatible) is supported via portable history serialisation.

### New: `ILlmProviderStrategy` — `SetModel`, `ExportHistory`, `ImportHistory`

Three new methods on `ILlmProviderStrategy`. `SetModel` mutates the active model/key/endpoint in-place (same provider). `ExportHistory`/`ImportHistory` use `UnifiedHistoryEntry` (new provider-agnostic format) to transfer message history across providers.

**Files:** `src/Diva.Infrastructure/LiteLLM/ILlmProviderStrategy.cs`, `src/Diva.Infrastructure/LiteLLM/UnifiedHistoryEntry.cs` (new), `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs`, `src/Diva.Infrastructure/LiteLLM/OpenAiProviderStrategy.cs`

### New: Hook override signals on `AgentHookContext`

`LlmConfigIdOverride` (full cross-provider switch via resolver), `ModelOverride` (same-provider model-only), `MaxTokensOverride`, `ApiKeyOverride`. First hook to set either override wins; subsequent hooks skip via `HasOverrideAlready` guard.

**File:** `src/Diva.Core/Models/IAgentLifecycleHook.cs`

### New: `model_switch` SSE event

Emitted after each model switch with `FromModel`, `ToModel`, `FromProvider`, `ToProvider`, `Reason`.

**File:** `src/Diva.Core/Models/AgentStreamChunk.cs`

### New: `ModelSwitchingOptions` DTO + DB column

`ModelSwitchingJson` column on `AgentDefinitions`. Supports `ToolIterationLlmConfigId/Model`, `FinalResponseLlmConfigId/Model`, `ReplanLlmConfigId/Model`, `UpgradeOnFailuresLlmConfigId/Model`, `UpgradeAfterFailures` (default 2), `FallbackToOriginalOnError` (default `true`).

**Files:** `src/Diva.Core/Configuration/ModelSwitchingOptions.cs` (new), `src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs`, `src/Diva.Infrastructure/Data/Migrations/20260328120000_AddAgentModelSwitching.cs` (new + Designer.cs), `src/Diva.Infrastructure/Data/Migrations/DivaDbContextModelSnapshot.cs`

### New: Rule Pack `model_switch` rule type

`HookPoint: OnBeforeIteration`. `Instruction` = model ID, `ToolName` = LlmConfigId integer, `Replacement` = max_tokens, `Pattern` = optional userQuery regex. Two model_switch rules in the same pack without `StopOnMatch=true` produce a `ConflictWarning`. `ModelSwitchRequest` exposed on `RuleEvalResult` and `RulePackDryRunResult`.

**Files:** `src/Diva.TenantAdmin/Services/RulePackEngine.cs`, `src/Diva.Agents/Hooks/BuiltIn/TenantRulePackHook.cs`

### New: `StaticModelSwitcherHook` (Order=3)

Reads `ModelSwitchingJson` from agent Variables. Priority within hook: failure upgrade > final response > tool iteration. Also stores `__replan_config_id`/`__replan_model` in State for the replan block.

**File:** `src/Diva.Agents/Hooks/BuiltIn/StaticModelSwitcherHook.cs` (new)

### New: `ModelRouterHook` (Order=4)

Smart heuristic routing via agent Variables (`model_router_mode`: `smart`/`tool_downgrade_only`/`off`). Smart table: stuck(≥2 failures)→strong, isFinal→strong, hadTools→fast, wasTruncated→fast.

**File:** `src/Diva.Agents/Hooks/BuiltIn/ModelRouterHook.cs` (new)

### Updated: `AnthropicAgentRunner` wiring

- `ModelSwitchingJson` injected into `customVars` as `__model_switching_json`
- `StaticModelSwitcherHook` and `ModelRouterHook` always registered via `MergeHookConfig`
- `__is_final_iteration` / `__last_had_tool_calls` state signals set each iteration
- Model override block applied after `OnBeforeIteration` hooks — handles same-provider switch, same-endpoint config switch, and cross-provider history transfer with Scenario 1 (resolver null) and Scenario 2 (export/import failure) fallbacks
- Scenario 3 fallback: API call failure on switched model restores original model if `FallbackToOriginalOnError=true`
- Replan model block applies before `CallReplanAsync`
- `model_switch` SSE event emitted per switch; overrides cleared after each iteration
- `_verifier.VerifyAsync` now receives `currentModel` (live) instead of `effectiveModel` (initial)

**File:** `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs`

---

## [2026-03-27] ReAct Loop Fixes — max_tokens, post-tool nudge, stuck scheduler, per-agent MaxOutputTokens, HttpClient timeout

### Overview

Four independent bugs fixed and one new capability added. All changes are in the Anthropic/OpenAI provider strategies and the scheduler; no new phases, no migration beyond the MaxOutputTokens column.

### Bug: max_tokens infinite loop

**Symptom:** When the LLM hit its output token limit, the runner added a nudge prompt and looped indefinitely because the nudge never resolved the underlying size constraint.

**Fix:**
- Added `maxTokensNudgeRetries = 1` counter (reset per continuation window) — limits nudge to 1 attempt
- On the first `max_tokens` stop: `hookCtx.WasTruncated = true`, the `OnError` hook pipeline is invoked; if the hook returns `Abort` the partial response is accepted immediately
- When retries are exhausted or the hook returns `Abort`, `completedNaturally = true; break` exits the loop with the partial text as the final response
- The `WasTruncated` flag is available to `OnBeforeIteration` hooks so they can inject "be concise" instructions

**New constant:** `MaxTokensNudgePrompt` — injected as a user turn when nudging: *"Your previous response was cut off…"*

**Files:** `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs`, `src/Diva.Core/Models/IAgentLifecycleHook.cs` (`WasTruncated` property on `AgentHookContext`), `src/Diva.Infrastructure/LiteLLM/ILlmProviderStrategy.cs` (`StopReason` field on `UnifiedLlmResponse`), `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs`, `src/Diva.Infrastructure/LiteLLM/OpenAiProviderStrategy.cs`

### Bug: post-tool nudge — model narrates instead of calling tool

**Symptom:** After executing tools the model would say "Now I will send the email…" instead of calling the send-email tool, then complete without actually doing it.

**Fix:** Added `lastIterationHadToolCalls` flag. When the model produces a text-only response on the iteration after tool calls, `PostToolNudgePrompt` is injected and the loop continues once:
- *"You described an action but did not call the tool. If you still need to call a tool, call it now…"*

**New constant:** `PostToolNudgePrompt`.

**Files:** `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs`

### Bug: silent LLM failures (iterations show no log between start/start)

**Symptom:** Logs showed 10+ iterations, each ~100 s apart, with no content — silent retry loops from `IsTransientLlmError` treating `TaskCanceledException` as transient.

**Fix:** Added `_logger.LogError` in three previously-silent paths:
- When `llmEx is not null` after the retry loop exhausts
- When `CallWithRetryAsync` is out of retries
- When the `OnError` hook returns and iteration continues

**Files:** `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs`

### Bug: HttpClient default 100-second timeout

**Symptom:** LLM calls to long-running agents timed out with `HttpClient.Timeout of 100 seconds elapsed`.

**Fix:** `AnthropicProvider` now accepts an injected `HttpClient`. In `Program.cs`, switched from `AddSingleton` to `AddHttpClient<IAnthropicProvider, AnthropicProvider>` with `client.Timeout = TimeSpan.FromSeconds(llmTimeoutSec)` where `llmTimeoutSec` comes from `LlmOptions.HttpTimeoutSeconds` (default 600).

**Files:** `src/Diva.Infrastructure/LiteLLM/AnthropicProvider.cs`, `src/Diva.Host/Program.cs`, `src/Diva.Core/Configuration/LlmOptions.cs` (`HttpTimeoutSeconds` property), `src/Diva.Host/appsettings.json` + `appsettings.Development.json`

### Bug: stuck scheduler runs block pending runs

**Symptom:** When a "running" task run was orphaned (process restart, hung agent), subsequent runs stayed "pending" indefinitely.

**Fix:** `RecoverStuckRunsAsync(DateTime cutoffUtc)` added to `IScheduledTaskService` / `ScheduledTaskService`. Marks all "running" records with `StartedAtUtc < cutoffUtc` (or `null`) as "failed". Called in two places in `SchedulerHostedService`:
- **Startup:** immediately after semaphore init, with `DateTime.UtcNow` as cutoff (recovers all orphans from previous process)
- **Per-poll:** at the top of `PollAndDispatchAsync` with `now.AddMinutes(-StuckRunTimeoutMinutes)` as cutoff (catches genuinely stuck runs)

**New config:** `TaskSchedulerOptions.StuckRunTimeoutMinutes` (default 60 prod, 10 dev).

**Files:** `src/Diva.Infrastructure/Scheduler/ScheduledTaskService.cs`, `src/Diva.Infrastructure/Scheduler/IScheduledTaskService.cs`, `src/Diva.Infrastructure/Scheduler/SchedulerHostedService.cs`, `src/Diva.Core/Configuration/TaskSchedulerOptions.cs`, `appsettings.json` + `appsettings.Development.json`

### Feature: per-agent MaxOutputTokens override

**Purpose:** Agents that always produce short responses (e.g. data extractors, classifiers) can cap the token budget to avoid paying for unused capacity; long-running agents can raise it above the global default.

**How it works:** `AgentDefinitionEntity.MaxOutputTokens?` (and `GroupAgentTemplateEntity.MaxOutputTokens?`) — `null` means use global `AgentOptions.MaxOutputTokens` (default 8192). The runner computes `effectiveMaxOutputTokens = definition.MaxOutputTokens ?? _agentOpts.MaxOutputTokens` and passes it to both provider strategies.

**New EF migration:** `20260327120000_AddAgentToolResultCharsOverride` — adds nullable int `MaxOutputTokens` to `AgentDefinitions` and `GroupAgentTemplates`.

**UI:** New "Max Output Tokens" number input in AgentBuilder → Advanced Config.

**Files:** `src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs`, `src/Diva.Infrastructure/Data/Entities/TenantGroupEntities.cs`, `src/Diva.Core/Configuration/AgentOptions.cs` (`MaxOutputTokens = 8192`), `src/Diva.TenantAdmin/Services/ITenantGroupService.cs`, `src/Diva.TenantAdmin/Services/TenantGroupService.cs`, `src/Diva.Host/Controllers/AgentsController.cs`, `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs`, `admin-portal/src/api.ts`, `admin-portal/src/components/AgentBuilder.tsx`, `src/Diva.Infrastructure/Data/Migrations/20260327120000_AddAgentToolResultCharsOverride.cs` *(new)*, `src/Diva.Infrastructure/Data/Migrations/20260327120000_AddAgentToolResultCharsOverride.Designer.cs` *(new)*, `src/Diva.Infrastructure/Data/Migrations/DivaDbContextModelSnapshot.cs`

### Bug: ResponseVerifier / LlmRuleExtractor JsonReaderException

**Symptom:** `System.Text.Json.JsonReaderException` when the LLM appended explanation text after the JSON block (e.g. `[{...}] Here is what I found…`). Both the verifier and rule extractor silently failed to parse the response.

**Fix:** Added `firstBrace`/`lastBrace` (verifier) and `firstBracket`/`lastBracket` (rule extractor) extraction — slices out just the JSON object/array before attempting `JsonDocument.Parse`.

Additionally, the token caps for both calls were hardcoded at 512, causing truncation of large rule sets and verification payloads. Both are now configurable:
- `VerificationOptions.VerifierMaxTokens` (default 1024) — used by `ResponseVerifier`
- `RuleLearningOptions.ExtractorMaxTokens` (default 2048) — used by `LlmRuleExtractor`

**Files:** `src/Diva.Infrastructure/Verification/ResponseVerifier.cs`, `src/Diva.Infrastructure/Learning/LlmRuleExtractor.cs`, `src/Diva.Core/Configuration/VerificationOptions.cs` (`VerifierMaxTokens`), `src/Diva.Core/Configuration/AgentOptions.cs` (`RuleLearning.ExtractorMaxTokens`), `appsettings.json` + `appsettings.Development.json`

### Tests (10 new)

**`AnthropicAgentRunnerTests.cs` (6 new):**
- `RunAsync_MaxTokensStopReason_NudgesOnce_ThenAcceptsPartial` — LLM always returns max_tokens; asserts 2 LLM calls (initial + nudge) and success with partial content
- `RunAsync_MaxTokensWithAbortHook_AcceptsPartialImmediately` — OnError hook returns Abort; asserts only 1 LLM call and result contains partial content
- `RunAsync_MaxTokensStopReason_WasTruncatedSetInHookContext` — asserts `WasTruncated == true` at point of OnError hook invocation
- `RunAsync_PerAgentMaxOutputTokens_PassedToProvider` — agent.MaxOutputTokens = 512; asserts `MessageParameters.MaxTokens == 512`
- `RunAsync_NoPerAgentMaxOutputTokens_UsesGlobalDefault` — null override; asserts `MessageParameters.MaxTokens == AgentOptions.MaxOutputTokens` (8192)

**`SchedulerTests.cs` (4 new):**
- `RecoverStuckRunsAsync_MarksOldRunningRunAsFailed`
- `RecoverStuckRunsAsync_DoesNotAffectRecentRun`
- `RecoverStuckRunsAsync_NullStartedAtUtc_AlwaysRecovered`
- `RecoverStuckRunsAsync_ReturnsCountOfRecoveredRuns`

---

## [2026-03-26] Phase 16 — Rule Packs (DB-Driven Configurable Hook Rules)

### Addendum — Full Hook-Point Coverage + Validation + Timeline Details

- Implemented all 7 lifecycle stages for Rule Packs: `OnInit`, `OnBeforeIteration`, `OnToolFilter`, `OnAfterToolCall`, `OnBeforeResponse`, `OnAfterResponse`, `OnError`
- Wired previously-defined but non-invoked hook pipeline methods into `AnthropicAgentRunner` (`RunOnToolFilterAsync`, `RunOnAfterToolCallAsync`, `RunOnErrorAsync`)
- Added retry/abort handling for tool and LLM failures via Rule Pack `OnError` action resolution
- Added synthetic tool results for filtered tool calls so the ReAct loop remains message-history-consistent
- Added backend compatibility matrix (`RulePackRuleCompatibility`) and enforced validation on rule create/update
- Mirrored compatibility matrix in `PackEditor` so rule types are filtered by selected hook point
- Enriched `hook_executed` SSE payload with Rule Pack trigger metadata (`rulePackTriggeredCount`, `rulePackTriggeredRules`, `rulePackFilteredCount`, `rulePackErrorAction`, `rulePackBlocked`)
- Updated live execution timeline in `AgentChat` to display hook execution details

**Purpose:** Implemented tenant-scoped Rule Packs — named, versioned bundles of hook rules that tenants can configure via the admin UI without writing code. Includes all 7 design-gap fixes: conditional activation, conflict detection, dry-run testing, pack inheritance (clone), execution metrics, import/export, and per-rule timeout.

### Architecture

Rule Packs are DB-driven configurable rule bundles evaluated at agent lifecycle hook points (`OnInit`, `OnBeforeIteration`, `OnToolFilter`, `OnAfterToolCall`, `OnBeforeResponse`, `OnAfterResponse`, `OnError`). Each pack contains ordered rules of 9 types: `inject_prompt`, `tool_require`, `format_response`, `format_enforce`, `regex_redact`, `append_text`, `block_pattern`, `require_keyword`, `tool_transform`.

**Key design decisions:**
- Packs are tenant-isolated (`ITenantEntity` with EF query filters)
- Starter packs (TenantId=0) can be cloned by any tenant, tracking inheritance via `ParentPackId`
- Activation conditions support plain text match, `regex:` prefix, and `archetype:` prefix
- `AppliesToJson` filters packs by agent archetype (JSON array)
- Pre-compiled regex cache (max 500 entries, 200ms timeout per regex)
- Batched execution logging via `Channel<T>` (bounded 10K, flushes every 100 items or 5s)
- Conflict analyzer detects internal + cross-pack conflicts at save-time (inject/block, duplicate formats, ReDoS, keyword/block)

### Backend

- **Entities:** `HookRulePackEntity`, `HookRuleEntity`, `RuleExecutionLogEntity` with self-referencing FK (ParentPack), group FK, and cascade delete
- **RulePackService:** Full CRUD + clone + reorder + starter packs + IMemoryCache (5-min TTL)
- **RulePackEngine:** Runtime evaluation, 9 rule evaluators, per-pack Stopwatch timeout, dry-run mode
- **RulePackConflictAnalyzer:** Internal conflicts (inject↔block, duplicate formats, redundant redactions, keyword↔block, ReDoS patterns, long regex) + cross-pack conflicts (inject text matching block patterns, multiple format_response, conflicting tool_require)
- **TenantRulePackHook:** Built-in hook integrating packs across all supported lifecycle points, including tool-filtering, post-tool output processing, after-response side effects, and error recovery
- **RulePackController:** 15 REST endpoints (CRUD + clone + rule CRUD + reorder + conflicts + test + export + import)
- **Bug fix:** `RulePackEngine.EvaluateRule` now catches `ArgumentException` (covers `RegexParseException` for invalid patterns) in addition to `RegexMatchTimeoutException`

### Frontend

- **RulePackManager.tsx:** List view with pack cards, status badges, clone/delete/export actions, starter packs section, import dialog
- **PackEditor.tsx:** Detail editor — pack metadata form, inline rule list with add/edit/delete, conflict analysis panel, dry-run test dialog with result visualization
- **Routing:** `/rules/packs` and `/rules/packs/:id` routes, "Rule Packs" nav item under Configuration

### Tests (54 new tests)

- **RulePackServiceTests** (18 tests): Tenant isolation, CRUD, clone (own + starter), mandatory pack protection, rule CRUD, reorder, cache invalidation
- **RulePackEngineTests** (24 tests): All 9 rule types, StopOnMatch, multi-pack priority, DryRun, ResolvePacksAsync (tenant loading, disabled packs, disabled rules, archetype filter, activation filter), invalid regex resilience
- **RulePackConflictAnalyzerTests** (12 tests): Internal conflicts (inject/block, duplicate formats, redundant redactions, keyword/block, ReDoS, long patterns), cross-pack conflicts (inject/block, multiple formats, conflicting tool_require, disabled pack exclusion)

**Files created:**
- `src/Diva.Infrastructure/Data/Entities/RulePackEntities.cs` *(new)*
- `src/Diva.TenantAdmin/Services/RulePackService.cs` *(new)*
- `src/Diva.TenantAdmin/Services/RulePackEngine.cs` *(new)*
- `src/Diva.TenantAdmin/Services/RulePackConflictAnalyzer.cs` *(new)*
- `src/Diva.Agents/Hooks/BuiltIn/TenantRulePackHook.cs` *(new)*
- `src/Diva.Host/Controllers/RulePackController.cs` *(new)*
- `src/Diva.Infrastructure/Data/Migrations/20260327100000_Phase16_RulePacks.cs` *(new)*
- `src/Diva.Infrastructure/Data/Migrations/20260327100000_Phase16_RulePacks.Designer.cs` *(new)*
- `admin-portal/src/components/RulePackManager.tsx` *(new)*
- `admin-portal/src/components/PackEditor.tsx` *(new)*
- `tests/Diva.TenantAdmin.Tests/RulePackServiceTests.cs` *(new)*
- `tests/Diva.TenantAdmin.Tests/RulePackEngineTests.cs` *(new)*
- `tests/Diva.TenantAdmin.Tests/RulePackConflictAnalyzerTests.cs` *(new)*

**Files modified:**
- `src/Diva.Infrastructure/Data/DivaDbContext.cs` *(modified)* — DbSets, query filters, relationships, indexes, auto-touch ModifiedAt
- `src/Diva.Host/Program.cs` *(modified)* — DI registrations for RulePackService, RulePackEngine, RulePackConflictAnalyzer
- `admin-portal/src/api.ts` *(modified)* — 12 TypeScript interfaces + 15 API methods
- `admin-portal/src/App.tsx` *(modified)* — Route entries
- `admin-portal/src/components/layout/app-sidebar.tsx` *(modified)* — Nav item

---

## [2026-03-25] Multi-Tenant Auth, SSO Login Flow & Tenant Isolation Enforcement

**Purpose:** Activated the SSO login flow end-to-end for multiple tenants, enforced server-side tenant isolation across all controllers, and fixed data created before isolation was enforced.

### Auth / SSO Login Flow

**Problem 1 — Token exchange 400:** Provider required `client_secret_basic` (HTTP Basic Auth header) but code only sent credentials in the form body (`client_secret_post`).
**Fix:** `AuthController.SsoCallback` now sends `Authorization: Basic base64(clientId:clientSecret)` on the token exchange request in addition to the form fields.

**Problem 2 — 401 "invalid token" after login:** The portal stored the provider's raw access token in localStorage, but `TenantContextMiddleware` validated it with our local HMAC key — mismatch.
**Fix:** After a successful SSO callback, `AuthController` issues a local JWT via `LocalAuthService.IssueSsoJwt()` (new method) instead of returning the provider's token. This local JWT is always valid against our middleware regardless of which external IdP issued the original token.

**Problem 3 — Logout didn't redirect to login page:** The portal sent `post_logout_redirect_uri` pointing at the portal URL, but most IdPs only whitelist the API URL as a post-logout redirect target.
**Fix:** Logout now routes through two new API endpoints:
- `GET /api/auth/logout?logoutUrl=...` — redirects to IdP logout with `post_logout_redirect_uri` pointing at the API callback
- `GET /api/auth/logout-callback` — redirects browser to `{portalOrigin}/login`
Only the API URL needs to be whitelisted in the IdP.

**Problem 4 — Login page showed SSO provider name, not organization name:** Each SSO button showed "Sign in with Generic" instead of the organization name.
**Fix:** `GetAllActiveAsync` now joins with the `Tenants` table to return `tenantName`. Login buttons show `{p.tenantName}` (the org name). `SsoProvider.tenantName` added to API interface.

### SSO Config — Per-Tenant Issuer Index

**Problem:** Adding an SSO config for a second tenant failed with `UNIQUE constraint failed: SsoConfigs.Issuer` because two tenants sharing the same IdP have the same issuer URL.
**Fix:** Replaced the global unique index on `Issuer` with a per-tenant composite index `(TenantId, Issuer)`. Migration `20260325070000_FixSsoIssuerIndex.cs` handles the schema change.

### Tenant Isolation — Controller Layer

**Problem:** All controllers accepted `?tenantId=1` from the query string as the sole source of truth. Any user could pass `?tenantId=2` to read another tenant's data.
**Fix:** All controllers now use an `EffectiveTenantId` helper:
```csharp
private int EffectiveTenantId(int requestedTenantId)
{
    var ctx = HttpContext.TryGetTenantContext();
    return ctx is { TenantId: > 0 } ? ctx.TenantId : requestedTenantId;
}
```
Regular users always get their JWT tenant; master admin (TenantId=0) can still pass a query param to manage any tenant.

Files updated: `AdminController.cs`, `SsoConfigController`, `UserProfilesController`, `SchedulerController.cs`, `LearnedRulesController.cs`.

### Tenant Isolation — AgentsController DB Context

**Problem:** `AgentsController` called `_db.CreateDbContext()` with no `TenantContext`, so `currentTenantId=0` bypassed the EF query filter entirely — all tenants' agents were returned/modified.
**Fix:** Added `private TenantContext Tenant` property; all DB context creations now pass `Tenant`; `Create()` stamps `dto.TenantId = tenant.TenantId`.

### Data Migration — Orphaned TenantId=0 Records

**Problem:** Records created before tenant isolation was enforced on writes had `TenantId=0`. After enforcement, EF query filter `WHERE TenantId = 1` excluded them — agents/rules/sessions disappeared.
**Fix:** Migration `20260325080000_FixOrphanedTenantIds.cs` reassigns all `TenantId=0` rows to `TenantId=1` across `AgentDefinitions`, `BusinessRules`, `PromptOverrides`, `LearnedRules`, `Sessions`, `ScheduledTasks`, `ScheduledTaskRuns`. (`SessionMessages` has no `TenantId` column — tenant isolation is via `SessionId` FK.)

### Migration Infrastructure Fix

**Problem:** Manually created migration files (no `.Designer.cs`) were not discovered by `MigrateAsync()` — EF Core requires the `[Migration("timestamp_MigrationName")]` attribute which lives in the Designer.cs.
**Fix:** Created proper Designer.cs files for both manual migrations with `[DbContext]` + `[Migration]` attributes and full `BuildTargetModel` snapshots.

**Files changed:**
- `src/Diva.Host/Controllers/AuthController.cs` *(modified)* — HTTP Basic Auth on token exchange; `IssueSsoJwt` call after callback; `GET /api/auth/logout` + `GET /api/auth/logout-callback` endpoints
- `src/Diva.Infrastructure/Auth/LocalAuthService.cs` *(modified)* — `IssueSsoJwt(tenantId, userId, email, displayName, roles[])` added to interface + implementation
- `src/Diva.Infrastructure/Auth/TenantContextMiddleware.cs` *(modified)* — `/api/auth/logout` and `/api/auth/logout-callback` added to bypass paths
- `src/Diva.Host/Controllers/AgentsController.cs` *(modified)* — `Tenant` property; all DB calls pass `Tenant`; Create stamps `TenantId`
- `src/Diva.Host/Controllers/AdminController.cs` *(modified)* — `EffectiveTenantId` helper; `using Diva.Core.Models` added; dashboard uses scoped DB context
- `src/Diva.Host/Controllers/SchedulerController.cs` *(modified)* — `EffectiveTenantId` helper on all 8 endpoints
- `src/Diva.Host/Controllers/LearnedRulesController.cs` *(modified)* — `EffectiveTenantId` helper on all 3 endpoints
- `src/Diva.TenantAdmin/Services/ITenantSsoConfigService.cs` *(modified)* — `GetAllActiveAsync` returns `(Config, TenantName)` tuples
- `src/Diva.TenantAdmin/Services/TenantSsoConfigService.cs` *(modified)* — joins `Tenants` table to include org name
- `src/Diva.Infrastructure/Data/DivaDbContext.cs` *(modified)* — `SsoConfigs` index changed from unique-on-Issuer to composite (TenantId, Issuer)
- `src/Diva.Infrastructure/Data/Migrations/20260325070000_FixSsoIssuerIndex.cs` *(new, manual)* — drops old unique index, creates composite index
- `src/Diva.Infrastructure/Data/Migrations/20260325070000_FixSsoIssuerIndex.Designer.cs` *(new)* — EF migration descriptor with updated model snapshot
- `src/Diva.Infrastructure/Data/Migrations/20260325080000_FixOrphanedTenantIds.cs` *(new, manual)* — data migration: `UPDATE ... SET TenantId = 1 WHERE TenantId = 0`
- `src/Diva.Infrastructure/Data/Migrations/20260325080000_FixOrphanedTenantIds.Designer.cs` *(new)* — EF migration descriptor
- `src/Diva.Infrastructure/Data/Migrations/DivaDbContextModelSnapshot.cs` *(modified)* — SSO index updated
- `admin-portal/src/api.ts` *(modified)* — `tenantName: string` on `SsoProvider` interface
- `admin-portal/src/components/LoginPage.tsx` *(modified)* — buttons show `{p.tenantName}` instead of provider name
- `admin-portal/src/lib/auth.ts` *(modified)* — `logout()` routes through `/api/auth/logout?logoutUrl=...`
- `admin-portal/src/components/ui/label.tsx` *(modified)* — `cursor-default` class added to fix I-beam cursor on labels

---

## [2026-03-24] Admin Portal Full UI Revamp

**Purpose:** Replaced the entire admin portal UI with a professional-grade interface using shadcn/ui + Tailwind CSS v4. All 9 page components were rewritten. The app is now production-quality with a persistent sidebar, light/dark theme toggle, shadcn data tables, dialogs, sheets, toast notifications, and responsive layouts throughout.

**Stack added:**
- **Tailwind CSS v4** (`@tailwindcss/vite` plugin, `@theme inline` CSS vars, oklch color space)
- **shadcn/ui (new-york style, zinc base)** — 27+ components in `src/components/ui/`
- **react-router v7** — `BrowserRouter + Routes + Route`, all pages use `useParams` / `useNavigate` / `useLocation` (no callback prop pattern)
- **next-themes** — `ThemeProvider` with `attribute="class" defaultTheme="dark"`
- **sonner** — `<Toaster richColors />`, all errors/success via `toast.error()` / `toast.success()`
- **recharts** — AreaChart + PieChart in Dashboard
- **DOMPurify** — safe HTML rendering in AgentChat

**Files changed:**
- `admin-portal/vite.config.ts` *(modified)* — `tailwindcss()` plugin + `@/` path alias
- `admin-portal/tsconfig.app.json` + `tsconfig.json` *(modified)* — `@/*` path mappings
- `admin-portal/components.json` *(new)* — shadcn config (new-york, zinc, cssVariables: true)
- `admin-portal/src/index.css` *(rewritten)* — Tailwind v4 with full light/dark oklch theme
- `admin-portal/src/lib/utils.ts` *(new)* — `cn()` helper (clsx + tailwind-merge)
- `admin-portal/src/components/theme-provider.tsx` *(new)* — next-themes wrapper
- `admin-portal/src/components/layout/app-sidebar.tsx` *(new)* — sidebar with nav groups and active state
- `admin-portal/src/components/layout/topbar.tsx` *(new)* — breadcrumb + theme toggle
- `admin-portal/src/components/layout/root-layout.tsx` *(new)* — `SidebarProvider + AppSidebar + Outlet`
- `admin-portal/src/components/ui/` *(27 new components)* — full shadcn component set
- `admin-portal/src/components/ui/empty-state.tsx` *(new)* — custom reusable empty state with icon+action
- `admin-portal/src/App.tsx` *(rewritten)* — `BrowserRouter + Routes`, all routes defined
- `admin-portal/src/main.tsx` *(rewritten)* — `ThemeProvider + Toaster` wrapping
- `admin-portal/src/components/Dashboard.tsx` *(rewritten)* — stat cards, AreaChart, PieChart, quick actions
- `admin-portal/src/components/AgentList.tsx` *(rewritten)* — shadcn Table + search + DropdownMenu + AlertDialog
- `admin-portal/src/components/AgentBuilder.tsx` *(rewritten)* — 4-tab layout (Identity / Model & Prompt / Tool Servers / Advanced), uses `useParams` + `useNavigate`
- `admin-portal/src/components/AgentChat.tsx` *(rewritten)* — Avatar bubbles, ScrollArea, IterationTrace collapsible, LiveFeed, uses `useParams` + `useLocation`
- `admin-portal/src/components/BusinessRules.tsx` *(rewritten)* — Table + per-category color Badges + inline Switch + Dialog form + AlertDialog
- `admin-portal/src/components/PromptEditor.tsx` *(rewritten)* — Table + merge-mode color Badges + inline Switch + Dialog form
- `admin-portal/src/components/PendingRules.tsx` *(rewritten)* — responsive card grid, Progress bar confidence, approve/reject with Dialog
- `admin-portal/src/components/ScheduledTasks.tsx` *(rewritten)* — Table + DropdownMenu + Dialog form + Sheet run-history side panel

**Validation:**
- `npx tsc -p tsconfig.app.json --noEmit` — 0 errors
- `vite build` — dist generated, 0 TypeScript errors (chunk-size warnings only, non-blocking)

---

## [2026-03-24] Custom Prompt Variables

**Purpose:** Agent system prompts can now include `{{variable}}` placeholders that are resolved at runtime. Built-in variables (`{{current_date}}`, `{{current_time}}`, `{{current_datetime}}`) are always available. Per-agent custom variables are defined in the Agent Builder UI and stored as `CustomVariablesJson` on the agent definition.

**Files changed:**
- `src/Diva.TenantAdmin/Prompts/PromptVariableResolver.cs` *(new)* — static resolver; fast-path if no `{{`; built-ins captured once per call; custom vars override built-ins; unresolved placeholders left as-is; `ParseJson` wraps result in `OrdinalIgnoreCase` dictionary
- `src/Diva.TenantAdmin/Prompts/TenantAwarePromptBuilder.cs` *(modified)* — accepts `customVariablesJson?` parameter; calls `PromptVariableResolver.ParseJson` + `Resolve` on the fully-assembled prompt (after business rules + session rules + overrides)
- `src/Diva.Core/Models/IPromptBuilder.cs` *(modified)* — `BuildAsync` signature extended with optional `customVariablesJson` parameter
- `src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs` *(modified)* — `CustomVariablesJson` nullable column (`JSON Dictionary<string,string>`)
- `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` *(modified)* — passes `definition.CustomVariablesJson` to `BuildAsync`
- `src/Diva.Host/Controllers/AgentsController.cs` *(modified)* — PUT copies `CustomVariablesJson`
- Migration `20260324033835_AddScheduler.cs` — `CustomVariablesJson` column added to `AgentDefinitions`
- `admin-portal/src/api.ts` *(modified)* — `customVariablesJson?: string` on `AgentDefinition`
- `admin-portal/src/components/AgentBuilder.tsx` *(modified)* — `AdvancedConfigPanel` key-value editor for custom variables; serialised to/from `customVariablesJson`
- `tests/Diva.TenantAdmin.Tests/PromptVariableResolverTests.cs` *(new)* — 18 unit tests: fast path, built-in resolution, custom var resolution, precedence, case-insensitivity, `ParseJson` edge cases
- `tests/Diva.TenantAdmin.Tests/PromptBuilderTests.cs` *(modified)* — 5 new `BuildAsync` tests: built-in in base prompt, custom var from JSON, custom var in injected business rule, null JSON still resolves built-ins, unknown variable left as-is

**Behavior:**
- `{{current_date}}` → `yyyy-MM-dd` (UTC), `{{current_time}}` → `HH:mm UTC`, `{{current_datetime}}` → `yyyy-MM-dd HH:mm UTC`
- Custom variable keys are case-insensitive (`{{Company_Name}}` matches `"company_name"` key)
- Custom variables override built-ins (user can pin `{{current_date}}` to a fixed value)
- Variable resolution runs on the fully-assembled prompt so placeholders in business rules and session rules are also resolved
- Unresolved placeholders are left unchanged and visible in LLM output

**Bug fix:** `PromptVariableResolver.ParseJson` now wraps the deserialized dictionary in `StringComparer.OrdinalIgnoreCase` so custom variable lookups are case-insensitive, consistent with the built-ins dictionary.

**Validation:**
- `dotnet test tests/Diva.TenantAdmin.Tests` — 40 tests pass (was 15)
- `dotnet test tests/Diva.Agents.Tests` — 89 tests pass (unchanged)
- `dotnet test tests/Diva.Tools.Tests` — 1 test passes (unchanged)

---

## [2026-03-24] Agent Test UX + ReAct Step Logging

**Purpose:** Improved the agent test experience in the admin portal by exposing global agent defaults in the builder UI, adding a detailed execution mode with a live event timeline, rendering agent HTML responses safely in the test window, and emitting structured info-level logs for each unified ReAct loop stage.

**Files changed:**
- `src/Diva.Host/Controllers/ConfigController.cs` *(modified)* — added `GET /api/config/agent-defaults`; injects `AgentOptions` and `VerificationOptions` so frontend placeholders can reflect real configured defaults
- `admin-portal/src/api.ts` *(modified)* — added `AgentDefaults` interface and `getAgentDefaults()` API method
- `admin-portal/src/components/AgentBuilder.tsx` *(modified)* — loads global defaults on mount and uses them in advanced config placeholders
- `admin-portal/src/components/AgentChat.tsx` *(modified)* — added Detailed mode toggle, live timeline/event log, auto-expanded iteration traces in detailed mode, and sanitized HTML rendering for agent messages
- `admin-portal/package.json` *(modified)* — added `dompurify` dependency for safe HTML sanitization
- `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` *(modified)* — added `LogInformation` calls for `continuation_start`, `iteration_start`, `plan`, `thinking`, `tool_call`, `tool_result`, `plan_revised`, `correction`, `final_response`, `verification`, and `done`

**Behavior changes:**
- Agent builder placeholders now show server-side defaults instead of hardcoded fallback text
- Agent test chat can show a detailed execution trace with timestamps and per-event summaries for all SSE events
- Agent responses containing HTML now render as formatted content after sanitization instead of appearing as raw tags
- Unified ReAct loop progress is visible in application logs for both Anthropic and OpenAI-compatible providers because logging lives in the shared execution loop

**Validation:**
- `npx tsc --noEmit` passed in `admin-portal`
- `dotnet build src/Diva.Infrastructure` passed
- `dotnet test tests/Diva.Agents.Tests` passed (89 tests)

## [2026-03-24] Unified ReAct Loop + Per-Agent Advanced Configuration

**Purpose:** Eliminated code duplication across 4 execution paths by introducing a strategy abstraction (`ILlmProviderStrategy`), unified the ReAct loop into a single `ExecuteReActLoopAsync`, added per-agent configuration fields (MaxContinuations, tool filtering, pipeline stages, stage instructions, custom variables), and fixed the instruction chain break through the supervisor pipeline.

**Architecture:**
- `ILlmProviderStrategy` — interface abstracting Anthropic vs OpenAI message handling
- `AnthropicProviderStrategy` — Anthropic SDK implementation (tool conversion, message building)
- `OpenAiProviderStrategy` — Raw `IChatClient`, NO `UseFunctionInvocation()` — manual ReAct loop
- `ExecuteReActLoopAsync` — Single unified ReAct loop used by all providers: plan detection, parallel tool execution, dedup, adaptive re-planning, tool error retry, verification, continuation windows
- `FilterTools()` — Static helper with allow/deny list support per agent definition
- Instruction flow — `AgentRequest.Instructions` → `SupervisorState` → `DecomposeStage` → `SubTask` → `DispatchStage` → worker system prompt

**Files created:**
- `src/Diva.Infrastructure/LiteLLM/ILlmProviderStrategy.cs` — Interface + `UnifiedLlmResponse`, `UnifiedToolCall`, `UnifiedToolResult` records
- `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs` — Anthropic SDK strategy (~170 lines)
- `src/Diva.Infrastructure/LiteLLM/OpenAiProviderStrategy.cs` — OpenAI-compatible strategy (~100 lines)
- `tests/Diva.Agents.Tests/ProviderStrategyTests.cs` — 14 tests: strategy init, tool results, FilterTools (null/empty/allow/deny/invalid/case-insensitive), SubTask instruction propagation, AgentRequest.Instructions round-trip
- `src/Diva.Infrastructure/Data/Migrations/*_AddAgentConfigFields.cs` — EF migration for 4 new columns

**Files modified:**
- `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` — Major refactoring: ~1,440 → ~700 lines (50% reduction); deleted `RunAnthropicAsync`, `RunOpenAiCompatibleAsync`, `BuildToolErrorRetryMessages`, `BuildToolErrorRetryChatMessages`, `ToAnthropicTool`; added `ExecuteReActLoopAsync`, `FilterTools`, per-agent `MaxContinuations`, instructions append to system prompt
- `src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs` — Added `MaxContinuations`, `PipelineStagesJson`, `ToolFilterJson`, `StageInstructionsJson`
- `src/Diva.Host/Controllers/AgentsController.cs` — PUT copies all 7 config fields
- `src/Diva.Core/Models/AgentRequest.cs` — Added `Instructions` property
- `src/Diva.Agents/Supervisor/SupervisorState.cs` — Added `SupervisorInstructions`, extended `SubTask` with `Instructions`
- `src/Diva.Agents/Supervisor/SupervisorAgent.cs` — Captures `request.Instructions` into state
- `src/Diva.Agents/Supervisor/Stages/DecomposeStage.cs` — Propagates instructions to sub-tasks
- `src/Diva.Agents/Supervisor/Stages/DispatchStage.cs` — Passes instructions to worker agents
- `admin-portal/src/api.ts` — 7 new optional fields on `AgentDefinition` interface
- `admin-portal/src/components/AgentBuilder.tsx` — Added `AdvancedConfigPanel` component: Verification Mode dropdown, Max Continuations, Context Window override, Custom Variables key-value editor, Pipeline Stages checkboxes, Tool Filter allow/deny, Stage Instructions textareas
- `tests/Diva.Agents.Tests/ToolOptimizationTests.cs` — Removed 4 tests for deleted helpers

**Test results:** 105 tests pass (89 agents + 15 tenant admin + 1 tools)

---

## [2026-03-24] Scheduler — Hourly Interval + AgentChat Multiline Input

**Purpose:** Added `hourly` as a fourth schedule frequency; updated the admin Schedules form and list display to handle it. Replaced the single-line text input in the agent chat UI with a resizable multiline textarea (Shift+Enter for new line, Enter to send).

**Files changed:**
- `src/Diva.Infrastructure/Scheduler/ScheduledTaskService.cs` *(modified)* — `ComputeNextRunUtc` switch extended with `"hourly"` → `fromUtc.AddHours(1)`; `ComputeHourly` private helper; `Validate` rejects unknown schedule types
- `admin-portal/src/components/ScheduledTasks.tsx` *(modified)* — `<option value="hourly">Hourly</option>` in dropdown; "Every hour" label in list view; Time of Day field hidden for `hourly` (only shown for `daily`/`weekly`); `runAtTime` omitted from save payload for `hourly`
- `admin-portal/src/components/AgentChat.tsx` *(modified)* — `<input>` → `<textarea rows={3} style={{ resize: "vertical", minHeight: 72 }}`; placeholder updated to document Shift+Enter behaviour
- `tests/Diva.Agents.Tests/SchedulerTests.cs` *(modified)* — added `ComputeNextRunUtc_Hourly_ReturnsOneHourFromNow`; total scheduler tests now **23**

---

## [2026-03-23] Task Scheduler (Phase 15)

**Purpose:** Enables tenant-scoped scheduled execution of agent tasks — one-time, hourly, daily, and weekly — with per-schedule timezones, fixed or template-based payloads, overlap queueing, and a full admin UI.

**Architecture:**
- `SchedulerHostedService` (`BackgroundService`) polls every `PollIntervalSeconds` for due tasks
- `_runningTasks: ConcurrentDictionary<string,string>` tracks active runs; overlap creates a queued "pending" run instead of skipping
- `ActivateOldestPendingRunAsync` promotes queued runs after the current run completes
- `ComputeNextRunUtc` is a pure static helper — testable without DB
- `BuildPrompt` handles `{{variable}}` substitution for template payloads

**Files changed:**
- `src/Diva.Core/Configuration/TaskSchedulerOptions.cs` *(new)* — `IsEnabled`, `PollIntervalSeconds`, `MaxConcurrentRuns`, `MaxQueuedRunsPerTask`, `MaxResponseStorageChars`
- `src/Diva.Infrastructure/Data/Entities/ScheduledTaskEntity.cs` *(new)* — tenant-scoped schedule definition
- `src/Diva.Infrastructure/Data/Entities/ScheduledTaskRunEntity.cs` *(new)* — per-execution run record
- `src/Diva.Infrastructure/Scheduler/IScheduledTaskService.cs` *(new)* — CRUD + worker interface
- `src/Diva.Infrastructure/Scheduler/ScheduledTaskService.cs` *(new)* — full implementation; `internal static ComputeNextRunUtc` + `TryParseRunAtTime`
- `src/Diva.Infrastructure/Scheduler/SchedulerHostedService.cs` *(new)* — `BackgroundService` poll loop; `internal static BuildPrompt`
- `src/Diva.Infrastructure/Data/DivaDbContext.cs` *(modified)* — `ScheduledTasks`, `ScheduledTaskRuns` DbSets, query filters, FK, indexes
- `src/Diva.Host/Controllers/SchedulerController.cs` *(new)* — `GET/POST/PUT/DELETE /api/schedules` + `/enabled`, `/trigger`, `/runs`
- `src/Diva.Host/Program.cs` *(modified)* — DI: `IScheduledTaskService`, `SchedulerHostedService`, config binding
- `src/Diva.Host/appsettings.json` + `appsettings.Development.json` *(modified)* — `TaskScheduler` config section
- `admin-portal/src/api.ts` *(modified)* — `ScheduledTask`, `ScheduledTaskRun`, `CreateScheduleDto`, `UpdateScheduleDto`, 8 API methods
- `admin-portal/src/components/ScheduledTasks.tsx` *(new)* — Schedules list, `TaskForm`, `RunHistory` sub-components
- `admin-portal/src/App.tsx` *(modified)* — "Schedules" nav item + view case
- `src/Diva.Infrastructure/Migrations/*_AddScheduler.cs` *(generated)* — EF migration
- `tests/Diva.Agents.Tests/SchedulerTests.cs` *(new)* — 22 xUnit tests: pure helpers, service CRUD, tenant isolation, overlap queueing, queue promotion

**Deferred:**
- Cron expression editor (once/hourly/daily/weekly supported; arbitrary cron intervals not yet supported)
- Distributed lock for multi-instance deployments (current `ConcurrentDictionary` is per-process only)
- Recovery of "running" state runs on startup (currently left as orphaned — can be promoted to "failed" on boot)
- IANA timezone dropdown is a static list in the frontend; could be populated from the API in future

---

## [2026-03-23] Continuation Windows

**Purpose:** When `maxIterations` is exhausted without the agent completing its task, the platform now compacts accumulated context and starts a new iteration window — up to `MaxContinuations` times — instead of returning a partial answer.

**Files changed:**

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/AgentOptions.cs` | Added `MaxContinuations = 2` |
| `src/Diva.Core/Models/AgentStreamChunk.cs` | Added `ContinuationWindow` field + `continuation_start` event type doc |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Outer continuation loop wrapping all 3 manual ReAct loops; `iterationBase` global counter; `BuildContinuationContext` helper |
| `src/Diva.Host/appsettings.json` | `MaxContinuations: 2` |
| `src/Diva.Host/appsettings.Development.json` | `MaxContinuations: 2` |
| `admin-portal/src/api.ts` | `continuationWindow?: number` on `AgentStreamChunk` interface |
| `admin-portal/src/components/AgentChat.tsx` | `continuation_start` case → shows "Continuing (window N)…" status |
| `tests/Diva.Agents.Tests/ToolOptimizationTests.cs` | 2 new tests for `BuildContinuationContext` |

**Key design decisions:**
- Iteration numbers are globally unique across windows (`iterationBase + i + 1`) to avoid corrupting frontend iteration slots when `i` resets to 0
- History is compacted at each window boundary (proactive Point A compaction; uses LLM summarizer if `SummarizerModel` configured)
- `toolEvidence` and `messages` carry across windows; per-window state (`consecutiveFailures`, `hadToolErrors`, `executionLog`) is reset

---

## [2026-03-23] MCP Client Cache — Empty Result Not Cached

**Purpose:** When all MCP server connections fail on the initial request, the cache was storing the empty client map. Every subsequent request within the 30-minute TTL returned the cached empty map without retrying, leaving the agent permanently without tools until the TTL expired or bindings were edited.

**File changed:** `src/Diva.Infrastructure/LiteLLM/McpClientCache.cs`

**Fix:** `GetOrConnectAsync` now skips storing the result when `clients.Count == 0` and bindings are configured (`definition.ToolBindings` is non-empty). The next request will call `connectFactory` again and retry the connections.

| Scenario | Cached? |
|---|---|
| Bindings configured, all connected | ✅ Yes |
| Bindings configured, all connections failed | ❌ No — retries next request |
| Bindings configured, partial connect | ✅ Yes — at least some tools available |
| No bindings configured (intentional empty) | ✅ Yes |

---

## [2026-03-23] JSON Tool Error Detection

**Purpose:** Tool calls returning JSON error objects (`{"status":"error","error":"..."}`) were not being detected as errors by the retry mechanism, leaving the agent stuck in acknowledgment loops when using OpenAI-compatible models.

**Files changed:**

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | `IsToolOutputError` extended with JSON pattern detection; `failed` computation updated to use `IsToolOutputError` on all 3 manual-loop paths |

**Error patterns now detected:**
- `"Error: ..."` prefix
- `contains "timed out"`
- Empty / whitespace
- `{"status":"error",...}` (with or without space after colon)
- `callResult.IsError == true` (MCP SDK native flag)

**Tests added:** `IsToolOutputError_JsonStatusError_ReturnsTrue`, `IsToolOutputError_JsonStatusErrorWithSpace_ReturnsTrue`, `IsToolOutputError_SuccessJson_ReturnsFalse`, `IsToolOutputError_NormalText_ReturnsFalse`

---

## [2026-03-23] Tool Error Retry — Acknowledgment Loop Fix

**Purpose:** When a tool returns an error, the LLM often produces a text-only acknowledgment ("I'll fix this...") before issuing a corrected call. The `else` (no tool calls) branch was running verification and breaking the loop, returning the acknowledgment as the final answer.

**Files changed:**

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | `hadToolErrors` flag tracks tool failures per iteration; `BuildToolErrorRetryMessages` + `BuildToolErrorRetryChatMessages` internal static helpers inject acknowledgment + retry prompt when LLM produces text-only after a failure |
| `tests/Diva.Agents.Tests/ToolOptimizationTests.cs` | 4 new tests (`BuildToolErrorRetryMessages_*`, `BuildToolErrorRetryChatMessages_*`) |

**Limitation:** ~~`RunOpenAiCompatibleAsync` (non-streaming) uses `UseFunctionInvocation()` opaquely — cannot inject mid-loop.~~ **Resolved 2026-03-24** — unified `ExecuteReActLoopAsync` replaced all separate paths.

---

## [2026-03-23] Context Window Management

**Purpose:** Long sessions accumulated unbounded token usage in two places: cross-run history loaded from DB (Point B) and in-run message accumulation during ReAct iterations (Point A).

**Files changed:**

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/AgentOptions.cs` | `ContextWindowOptions` + `ContextWindowOverrideOptions` added |
| `src/Diva.Infrastructure/Context/IContextWindowManager.cs` | **New** — `CompactHistoryAsync`, `MaybeCompactAnthropicMessages`, `MaybeCompactChatMessages` interface |
| `src/Diva.Infrastructure/Context/ContextWindowManager.cs` | **New** — Singleton implementation; LLM + rule-based summarization; `ComputeCompactionPlan` generic core |
| `src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs` | `ContextWindowJson` nullable column (per-agent override) |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Point B at 2 history-load sites; Point A at 7 LLM-call sites (streaming + non-streaming) |
| `src/Diva.Host/Program.cs` | `AddSingleton<IContextWindowManager, ContextWindowManager>()` |
| `src/Diva.Host/appsettings.json` | `Agent:ContextWindow` block |
| `src/Diva.Host/appsettings.Development.json` | `Agent:ContextWindow` block |
| `tests/Diva.Agents.Tests/ContextWindowTests.cs` | **New** — 11 pure unit tests + 2 integration tests |
| `tests/Diva.Agents.Tests/Helpers/ContextWindowTestHelpers.cs` | **New** — `NoOpCtx()` shared NSubstitute mock |
| Migration: `AddAgentContextWindowConfig` | EF migration for `ContextWindowJson` column |

---

## [2026-03-23] Response Verification (Phase 13)

**Purpose:** Added hallucination detection and response grounding verification after every agent response.

**Files changed:**

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Verification/ResponseVerifier.cs` | **New** — `Off` / `ToolGrounded` / `LlmVerifier` / `Strict` / `Auto` modes |
| `src/Diva.Core/Configuration/VerificationOptions.cs` | **New** |
| `src/Diva.Core/Models/VerificationResult.cs` | **New** |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Verification + correction loop in all 4 paths; `WasBlocked` + `ToolGrounded` correction scope |
| `admin-portal/src/components/AgentChat.tsx` | Verification badge rendering |
| `docs/arch-response-verification.md` | **New** architecture doc |
| `docs/phase-13-verification.md` | **New** phase doc |

---

## Pending

### Supervisor Verification Propagation

**Status:** `[ ]` Not started
**File:** `src/Diva.Agents/Supervisor/SupervisorAgent.cs`
**Gap:** `SupervisorAgent` builds its final `AgentResponse` without including `Verification` or `ToolEvidence`. `VerifyStage` computes and attaches these to each worker's response but they are never aggregated into the supervisor's own response. Clients calling `POST /api/supervisor/invoke` receive no verification metadata.
**Fix:** In `SupervisorAgent.cs`, aggregate from worker results when constructing the response:
```csharp
Verification = state.WorkerResults.Select(r => r.Verification).FirstOrDefault(v => v is not null),
ToolEvidence = string.Join("\n\n", state.WorkerResults.Select(r => r.ToolEvidence).Where(e => !string.IsNullOrEmpty(e)))
```

---

### A2A Protocol (Phase 14)

**Status:** `[ ]` Not started
**Blocked by:** None (can be implemented independently)
**Scope:** Agent-to-Agent delegation — `AgentCard` endpoint (`/.well-known/agent.json`), task lifecycle (`/tasks/send`, `/tasks/{id}/get`, `/tasks/{id}/cancel`), `AgentTaskEntity`, `A2AAgentClient` for remote delegation, `DispatchStage` A2A routing
**Doc:** [phase-14-a2a.md](phase-14-a2a.md)

### Domain MCP Tool Servers (Phase 5)

**Status:** `[~]` Infrastructure complete, domain servers deferred
**Blocked by:** Real data backends (Analytics DB, Reservation system)
**Scope:** `AnalyticsMcpServer`, `ReservationMcpServer` — placeholder shells exist; need real DB schemas + queries
**Doc:** [phase-05-mcp-tools.md](phase-05-mcp-tools.md)

### ~~`RunOpenAiCompatibleAsync` Tool Error Retry~~

**Status:** **Resolved 2026-03-24** — eliminated by unified `ExecuteReActLoopAsync`; `UseFunctionInvocation` removed entirely from all paths.

### ~~Per-Agent Context Window Override UI~~

**Status:** **Resolved 2026-03-24** — `ContextWindowJson` exposed in `AgentsController` PUT + `AdvancedConfigPanel` in `AgentBuilder.tsx`.

### ~~Admin UI for Per-Agent Continuation Limit~~

**Status:** **Resolved 2026-03-24** — `MaxContinuations` added as per-agent field on `AgentDefinitionEntity` + exposed in `AdvancedConfigPanel`.
