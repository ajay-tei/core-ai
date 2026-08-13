# Diva AI Platform — Changelog

> All changes are in reverse chronological order. Completed items include file paths. Pending items include rationale for deferral.

---

## [2026-08-13] Bug fix: scheduled "Run as User" tasks didn't fully carry the user's identity for MCP credential/environment resolution

**Problem**: investigated whether a scheduled task configured to "Run as User" correctly identifies
the right MCP server and credential. Found two independent gaps:

1. `TenantContext.RunAsUser(...)` hardcoded `UserRoles = ["system"]` and left `UserGroups` (SSO
   groups) empty, only ever populating the real `UserId`/`UserEmail`. `UserGroupMembershipCache.GetGroupIdsForUserAsync`
   matches a user into a group three ways: explicit UserId, explicit Email, or a role/SSO-group
   auto-include rule. Only the first two worked for a "run as user" execution — a group whose
   membership is defined via a role-based auto-include rule (not an explicit per-user listing)
   never matched, so that user's MCP credential (mapped to the group) was never selected, even
   though the original feature was explicitly designed to carry "that user's identity **and group
   membership**" (per the original changelog entry). `UserProfileEntity.Roles` (persisted from the
   user's last login) already has the data needed — the scheduler just never looked it up.
2. `SchedulerHostedService.ExecuteRunAsync` never set `TenantContext.EnvironmentId` for **any**
   scheduled task (Run-as-User or plain) — a documented-but-never-completed gap from the Phase E
   environment-routing work ("Scheduler task executor's agent lookup" was explicitly listed as
   deferred). Downstream MCP server lookup (`McpCredentialSelector.ResolveSharedBindingsAsync`)
   treats `EnvironmentId == 0` as "match any environment", so a scheduled task could resolve an
   MCP server/credential from the **wrong** environment (or ambiguously, whichever of several
   same-named rows a `Task.WhenAll` connection race happened to finish last) whenever the same
   server name exists in more than one environment — the normal case for shared infrastructure.

**Fix**:
- `TenantContext.RunAsUser` gained an optional `roles` parameter (falls back to `["system"]` when
  not supplied, preserving prior behavior for any other caller).
- `SchedulerHostedService.ExecuteRunAsync` now looks up the run-as-user's `UserProfileEntity.Roles`
  and passes them through, and calls `.WithEnvironment(scheduledTask.EnvironmentId ?? 0)` on the
  constructed context (both the System and RunAsUser branches) so MCP server/credential and
  LLM-config resolution correctly scope to the task's own environment.
- SSO-group-based auto-include still cannot match for a "run as user" execution — `UserProfileEntity`
  has no persisted SSO-group field to source it from. Noted as a known, accepted limitation (would
  need a schema change to fix), not addressed in this pass.
(`src/Diva.Core/Models/TenantContext.cs`, `src/Diva.Infrastructure/Scheduler/SchedulerHostedService.cs`)

**Tests**: `Resolver_MatchesRoleBasedGroup_ForSchedulerRunAsUserContext` — constructs a context via
`TenantContext.RunAsUser(...)` exactly as the scheduler does and confirms it now matches a
role-based auto-include group rule. (`tests/Diva.TenantAdmin.Tests/UserGroupServiceTests.cs`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
326/326 (325 + 1 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests`
355/356 (same single pre-existing unrelated `ContextWindowTests` failure tolerated). No dedicated
test added for the environment-scoping fix itself — `SchedulerHostedService.ExecuteRunAsync` is
private with no existing mock harness for its concrete `AnthropicAgentRunner` dependency; the fix
is a single-line, low-risk use of the already-tested `TenantContext.WithEnvironment`.

---

## [2026-08-12] Feature: optional, excludable scheduled-task cascade on Agent promotion + standalone task promotion

**Problem**: promoting an Agent never brought its scheduled tasks along (by original design —
auto-cascade only ever flowed toward dependencies, never dependents), and there was no way to
promote a scheduled task on its own from the Scheduled Tasks page at all (the backend already
supported it via a generic `objectType`, but no UI entry point existed).

**Design wrinkle**: a ScheduledTask *depends on* its Agent (needs the Agent's row to already exist,
to resolve `AgentId` by name) — the opposite direction from existing cascade dependencies (MCP
servers/delegate agents, which the Agent needs and which are materialized *before* it via the
existing `closure.Reverse()`). Modeling it as an ordinary cascade dependency would materialize it
in the wrong order. Introduced a distinct **optional dependent** concept instead, resolved and
materialized in a second pass *after* the root closure, so the Agent already exists in the target
by the time its tasks are promoted.

**Also fixed (found during this work, directly relevant)**: `ScheduledTaskSnapshotSerializer.MaterializeAsync`
resolved the task's agent by `Name` with **no `EnvironmentId` filter** — with same-named agents
across environments (the normal case, since a promoted agent keeps its name), a promoted task could
silently wire itself to the wrong environment's agent. Now filters by `EnvironmentId` too.

**Fix**:
- `IPromotionDependencyResolver` gained `GetOptionalDependentsAsync` (default-empty interface
  implementation — zero change needed for McpServer/ScheduledTask/AgentGroup resolvers);
  `AgentPromotionDependencyResolver` overrides it to return the agent's own scheduled tasks.
- `PromotableDependency` gained `IsOptional` (false for the root/hard cascade items).
- `PromotionOrchestrationService.PreviewAsync` now also lists optional dependents (marked
  `IsOptional: true`) alongside the hard closure, for every item in the closure (so a cascaded
  delegate agent's own tasks are offered too, not just the root's).
- `PromotionOrchestrationService.PromoteAsync` refactored to extract a reusable per-item
  materialize helper, gained a trailing `excludedLogicalIds` parameter, and now processes optional
  dependents in a second pass after the hard closure, skipping anything the caller excluded.
  Simplified `WasSkipped` reporting for excluded/optional items to reflect whether materialization
  actually ran, consistent with the existing rule that reaching `MaterializeAsync` is never
  "skipped" regardless of the ledger's own version-reuse decision.
- `PromoteRequest`/`BulkPromoteRequest` (and their TS equivalents) gained `ExcludedLogicalIds`.
- `PromotionDialog.tsx` now splits dependencies into hard (always included) vs. optional
  (checkbox per row, included by default, excludable) and passes the excluded set through on
  submit; generalized the "Main agent" label per object type so it reads correctly for a
  ScheduledTask-rooted promotion too.
- `ScheduledTasks.tsx` gained a "Promote" row action (mirrors `AgentBuilder.tsx`'s existing wiring)
  opening the same `PromotionDialog` with `objectType="ScheduledTask"`.
- Version numbering needed no new code: the existing `allowNewVersion`/default-environment policy
  is object-type-agnostic and already applies to scheduled tasks, cascaded or standalone.
(`src/Diva.Core/Models/IPromotionDependencyResolver.cs`, `IPromotionOrchestrationService.cs`,
`src/Diva.Infrastructure/Promotion/PromotionDependencyResolvers.cs`, `PromotionOrchestrationService.cs`,
`ScheduledTaskSnapshotSerializer.cs`, `src/Diva.Host/Controllers/PromotionsController.cs`,
`admin-portal/src/api.ts`, `admin-portal/src/components/PromotionDialog.tsx`,
`admin-portal/src/components/ScheduledTasks.tsx`)

**Tests**: `PreviewAsync_Agent_MarksItsScheduledTaskAsOptionalDependent`,
`PromoteAsync_Agent_CascadesScheduledTask_AgentIdResolvesToNewlyPromotedRow` (the ordering
regression test), `PromoteAsync_Agent_ExcludedScheduledTask_IsNotMaterialized`,
`PromoteAsync_ScheduledTask_Standalone_SucceedsOnceAgentAlreadyPromoted`,
`PromoteAsync_ScheduledTask_FromNonDefaultEnvironment_ReusesVersionNotMinting`
(`tests/Diva.TenantAdmin.Tests/PromotionOrchestrationServiceTests.cs`); updated
`MaterializeAsync_ReResolvesAgentNameToAgentId_InTargetTenant` (now seeds a target-environment
agent, matching real usage) and added
`MaterializeAsync_SameAgentNameInMultipleEnvironments_ResolvesToTargetEnvironmentsOwnAgent`
(`tests/Diva.TenantAdmin.Tests/PromotionSnapshotSerializerTests.cs`).

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
325/325 (319 + 6 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests`
355/356 (same single pre-existing unrelated `ContextWindowTests` failure tolerated). `tsc -b` clean;
eslint unchanged at the established baseline (36 problems, 26 errors/10 warnings) — zero new issues.

---

## [2026-08-12] Behavior change: version numbers now only increase when promoted from the default environment

**Problem**: promoting an agent between two non-default environments (e.g. COT Play → COT Live)
could mint a brand-new version number even though nothing meaningful changed — `RecordVersionAsync`
only compares the freshly-serialized source content's hash against the single global latest
recorded version for that logical object, and mints a new version whenever they don't match
(always true for `source: "promotion"`, by design, so promotion is a distinct auditable
checkpoint). But version numbers are meant to represent releases cut from the tenant's default
environment — every other promotion should just carry the existing version forward to one more
environment, never mint a new one, regardless of why the hash happened to differ (e.g. content
that drifted in a non-default environment without ever being explicitly re-published).

**Fix**: `IPromotionLedgerService.RecordVersionAsync` gained a trailing `bool allowNewVersion =
true` parameter — when false, a content mismatch against the latest recorded version reuses that
version's identity instead of minting a new one. `PromotionOrchestrationService.PromoteAsync` now
looks up the source environment's `IsDefault` flag once per call and passes
`allowNewVersion: isFromDefaultEnvironment` to the ledger. Every other existing caller (the 4
publish-flow controllers, `RollbackAsync`, all existing tests) is unaffected by the default value.
Also simplified `PromotedObjectResult.WasSkipped` computation in `PromoteAsync`: it's now `false`
whenever `MaterializeAsync` actually ran (the target's live row was genuinely written), instead of
being derived from the ledger's own `WasNew` flag — which would have misreported "skipped" for a
freshly-materialized non-default-environment promotion that happens to reuse a version number.
(`src/Diva.Core/Models/PromotionModels.cs`, `src/Diva.Infrastructure/Promotion/PromotionLedgerService.cs`,
`src/Diva.Infrastructure/Promotion/PromotionOrchestrationService.cs`)

**Tests**: `RecordVersionAsync_PromotionSource_AllowNewVersionFalse_ReusesLatestInsteadOfMinting`
(ledger-level: `allowNewVersion: false` reuses the existing row/number despite changed content),
`PromoteAsync_FromNonDefaultEnvironment_ContentDiffersFromRecordedVersion_ReusesVersionInsteadOfMinting`
(staging's content drifts post-promotion, then staging→prod reuses v1 instead of minting v2, and is
reported as not-skipped since prod's row was genuinely created), and
`PromoteAsync_FromDefaultEnvironment_ContentDiffers_StillMintsNewVersion` (confirms the existing,
correct behavior is preserved when the source IS the default environment).
(`tests/Diva.TenantAdmin.Tests/PromotionLedgerServiceTests.cs`, `tests/Diva.TenantAdmin.Tests/PromotionOrchestrationServiceTests.cs`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
319/319 (316 + 3 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests`
355/356 (same single pre-existing unrelated `ContextWindowTests` failure tolerated).

---

## [2026-08-12] Observability: log a masked tail of the actual credential value at resolve/inject time

**Problem**: after the COT Live "Weather Server - Global" tool calls turned out to fail for a
reason external to this repo (the tool's own upstream auth — see the trace-DB finding recorded in
memory), the natural follow-up question was "are we even sure the right key reaches the request?".
The answer was: not directly provable from logs. `McpCredentialSelector` already logs a masked tail
when picking *which* credential name to use, but that's a separate decrypt call for a different
purpose. Neither `CredentialResolver.ResolveAsync` (the actual decrypt-and-return step) nor
`McpConnectionManager.CreateClientAsync` (the actual header/env-var injection point) logged
anything about the resolved *value* — only the credential's name. So there was no way to confirm,
from logs alone, that the value injected into a specific live HTTP request matched the credential's
current DB value.

**Fix**: log a masked tail (last 4 chars, `key ****xxxx` — same convention already used by
`McpCredentialSelector` and the admin UI's credential hint) at both points: right after
`CredentialResolver.ResolveAsync` decrypts the key, and at every injection site in
`McpConnectionManager.CreateClientAsync` (Bearer, X-API-Key, custom header, unknown-scheme
fallback, and the stdio `MCP_API_KEY` env var). The full key is never logged. This lets a live
auth failure be cross-checked end-to-end: compare the masked tail in the admin UI's credential
list against the masked tail logged for that exact tool call.
(`src/Diva.Infrastructure/Auth/CredentialResolver.cs`, `src/Diva.Infrastructure/LiteLLM/McpConnectionManager.cs`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
316/316, `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests` 355/356 (same
single pre-existing unrelated `ContextWindowTests` failure tolerated). No behavior change, logging only.

---

## [2026-08-12] Bug fix: re-promoting after manually deleting a target-environment agent silently promoted nothing

**Problem**: user deleted all agents directly from COT Live, then re-promoted "Analytics - COT"
(with its cascade of sub-agents and MCP servers) from COT Play — the promotion call returned
success but recreated nothing (`"Promotion run 23: ... promoted 0 object(s)"`, every entry in
`PromotedVersionsJson` had `WasSkipped: true`). Traced via `EnvironmentDeployments`/`PromotableVersions`:
`AgentDefinitions` had 0 rows for the deleted agent in COT Live, yet its `EnvironmentDeployments`
ledger row still pointed at a `PromotableVersions` snapshot. `PromotionOrchestrationService.PromoteAsync`'s
"idempotent skip" check only compared that ledger snapshot's content against the current source
content — it never verified the target row still physically existed, so deleting an agent
out-of-band (rather than through promotion/rollback tooling) left a stale ledger pointer that made
every future re-promotion of unchanged content a silent no-op, forever, until the content actually
changed. A second, independent no-op existed one level down: even after fixing the orchestrator to
call `MaterializeAsync` (which recreates the row), `IPromotionLedgerService.RecordVersionAsync`'s own
content-hash dedup still reported `WasSkipped: true` for unchanged content, which would have kept
the promotion dialog/logs misleadingly reporting "0 promoted" even though the row was in fact
just recreated.

**Fix**: the skip check in `PromoteAsync` now also calls the object's own
`IPromotableSnapshotSerializer.SerializeAsync` against the target environment and only treats it as
an idempotent skip when that ALSO returns non-null (row still exists) — otherwise it falls through
to `MaterializeAsync` to recreate the row, and the result is explicitly reported as not-skipped
(via a `recreatingMissingRow` flag overriding the ledger's own content-based `WasNew` dedup) so the
promotion result accurately reflects that something was actually created.
(`src/Diva.Infrastructure/Promotion/PromotionOrchestrationService.cs`)

**Tests**: `PromoteAsync_TargetRowDeletedDirectly_RecreatesInsteadOfSkipping` (promote once, delete
the target row directly, re-promote unchanged content — target row must be recreated and reported
as not skipped, not silently no-op'd).
(`tests/Diva.TenantAdmin.Tests/PromotionOrchestrationServiceTests.cs`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
316/316 (315 + 1 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests`
355/356 (same single pre-existing unrelated `ContextWindowTests` failure tolerated).

---

## [2026-08-12] Bug fix: an already-connected MCP client kept using a stale credential for up to 30 minutes, even after cache invalidation

**Problem** (follow-up to the same-day credential-cache-invalidation and Edit-UI fixes — a third,
distinct root cause in the same "COT Live Prod" investigation): even with the Edit UI genuinely
saving a new key, and with logs confirming `CredentialResolver: invalidated cache for 'COT Live
Prod'` firing correctly on every save, the agent kept failing with an authentication error. Traced
live: repeated failed tool calls showed no intervening "Connected to MCP server..." log line,
proving the same MCP client connection was being reused, not reconnected. Reading
`McpConnectionManager.CreateClientAsync` confirmed why: it calls `ICredentialResolver.ResolveAsync`
**once**, at connect time, and that resolved value is captured by the header-injection closure
passed into `SsoAwareHttpMessageHandler` — fixed for the lifetime of that MCP client object. Because
`McpClientCache` caches connected clients for a 30-minute TTL with no invalidation hook, and the
credential-value cache fix only affects the *next* `ResolveAsync` call (which never happens for an
already-connected client), a corrected credential could silently fail to take effect for up to 30
minutes, or effectively indefinitely if the connection kept getting reused within that window. The
SSO/tenant-context portion of header-building *is* re-evaluated per request (reads
`IHttpContextAccessor` fresh); only the credential *value* is resolved once and re-injected (not
re-resolved) afterward.

**Fix**: added `McpClientCache.EvictAllAsync()` — evicts and disposes every cached MCP client across
all agents and tenants (there's no cheap way to know in advance which cached connections reference
a given credential name, since it isn't part of the cache key, so a credential write forces every
agent to reconnect rather than risk continuing to serve a stale value). Wired into
`CredentialsController`'s `Update`, `Rotate`, and `Delete` actions, right alongside the existing
`ICredentialResolver.InvalidateAsync` call.
(`src/Diva.Infrastructure/LiteLLM/McpClientCache.cs`, `src/Diva.Host/Controllers/CredentialsController.cs`)

**Tests**: `EvictAllAsync_RemovesEveryAgent_NextCallsAllReconnect` (two different agents cached,
evict-all, both reconnect on next call) and `EvictAllAsync_EmptyCache_IsNoOp`.
(`tests/Diva.Agents.Tests/McpClientCacheTests.cs`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
315/315, `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests` 355/356 (353 +
2 new, same single pre-existing unrelated `ContextWindowTests` failure tolerated).

---

## [2026-08-12] Bug fix: admin portal had no way to actually change an existing MCP credential's key

**Problem** (follow-up to the same-day cache-invalidation fix — different root cause entirely):
even after that fix deployed, the "COT Live Prod" credential's authentication error persisted.
Traced live: the masked key hint in `McpCredentialSelector`'s own logging (`key ****2ccc`) — which
reads straight from the database on every call, no caching involved — was **identical** before and
after the user believed they'd entered a new key, and the DB row's `EncryptedApiKey` length and
`CreatedAt` hadn't changed either. The backend `PUT .../credentials/{id}` (with `newApiKey`) and
`POST .../credentials/{id}/rotate` endpoints both already existed and both already call the new
`InvalidateAsync` — but `CredentialManager.tsx` never called either of them: its only per-credential
actions were Activate/Deactivate and Delete. There was no Edit button, no key-rotation field, no way
at all to change a credential's key short of deleting and recreating it under a decoy identical
name — so whatever the user did in the UI never actually reached the backend.

**Fix**: added a real Edit flow to `CredentialManager.tsx` — a pencil-icon button per row opens an
inline form (name, auth scheme, custom header, description, environment, all pre-filled) plus an
optional **New API Key** field ("leave blank to keep the existing key"), calling the existing
`PUT /api/admin/credentials/{id}` endpoint. (`admin-portal/src/components/CredentialManager.tsx`)

**Verification**: `tsc -b` clean; eslint clean (36 problems, matching the established baseline
exactly, zero from the touched file). Deployed `diva-portal` only; confirmed the new bundle
(`main-edOT80mZ.js`, changed from `main-9DTQIKrw.js`) contains the new edit-form text.

---

## [2026-08-12] Bug fix: rotating/editing an MCP credential kept serving the OLD key for up to 2 minutes

**Problem**: user updated "COT Live Prod" with a new, verified-working key (confirmed valid via an
external MCP testing tool), but agents in COT Live kept failing with an authentication error.
Traced live: `CredentialResolver.ResolveAsync` caches the **decrypted** credential in `IMemoryCache`
for 2 minutes, keyed by `cred:{tenantId}:{name}:{environmentId}` — but nothing ever evicted that
entry when the credential's `EncryptedApiKey` was changed. `CredentialsController`'s `Update`,
`Rotate`, and `Delete` actions all write straight to the DB with no cache invalidation at all, so
any resolution that happened before the edit (e.g. the admin's own earlier test) kept being served
back, silently, for up to 2 minutes after the key was corrected — long enough that a same-session
retest could easily still hit the stale value. Not the environment-scoping/credential-group bugs
fixed earlier today — this is a distinct cache-invalidation gap.

**Fix**: added `ICredentialResolver.InvalidateAsync(tenantId, credentialName, ct)` — since the cache
key includes the *calling* environment (not just the credential's own tag), it sweeps every
environment the tenant has (not just the credential's own one) plus the unscoped/wildcard slot.
Called from `CredentialsController`'s `Update`, `Rotate`, and `Delete` actions right after
`SaveChangesAsync`, matching the established "always invalidate after writes" convention already
used for `ILlmConfigResolver`/`IGroupMembershipCache`.
(`src/Diva.Core/Configuration/ICredentialResolver.cs`, `src/Diva.Infrastructure/Auth/CredentialResolver.cs`,
`src/Diva.Host/Controllers/CredentialsController.cs`)

**Tests**: `InvalidateAsync_ForcesFreshResolution_AfterKeyIsUpdated` (update → invalidate → next
resolve sees the new value, not the cached old one) and
`InvalidateAsync_SweepsEveryTenantEnvironment_NotJustTheUnscopedSlot` (a resolution cached under a
specific environment id is also cleared, not just the unscoped "0" slot).
(`tests/Diva.Agents.Tests/CredentialResolverTests.cs`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
315/315, `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests` 353/354 (352 +
2 new, same single pre-existing unrelated failure tolerated). Deployed `diva-api` only; confirmed
`InvalidateAsync` present in the deployed `Diva.Core.dll` and the new log message string via
`Select-String -Encoding Unicode` on the deployed `Diva.Infrastructure.dll`, ~1 minute old.

---

## [2026-08-12] Bug fix: LLM config picker could show two entries as "selected" for the same value

**Problem**: opening "Weather Agent - Global" (COT Play) and picking "COT" in the LLM config
dropdown appeared to select two entries at once. Root cause: `TenantLlmConfigs` and
`GroupLlmConfigs` are separate tables with **independent** Id sequences, so a tenant-scoped config
("COT", `TenantLlmConfigs.Id=3`) and an unrelated group-scoped config ("Claude DEV",
`GroupLlmConfigs.Id=3`, inherited via a group this tenant belongs to) can share the same numeric
Id. `ListAvailableLlmConfigsForTenantAsync` returned both as separate list entries with that same
raw Id as `AvailableLlmConfigDto.Id` — the `<SelectItem value={c.id}>` picker then had two options
bound to the identical value, so selecting one visually highlighted both. Confirmed live via SQL:
tenant 1 is a member of the group owning "Claude DEV", and both configs are Id 3 in their own
tables.

**Why hiding the group entry is correct, not just a workaround**: `LlmConfigResolver.
ResolveNamedConfigAsync` already always tries `TenantLlmConfigs` by Id **first** and returns
immediately on a match — a colliding group config could never actually be resolved/selected for
this tenant in the first place, so listing it was always misleading, independent of this bug.

**Fix**: `TenantGroupService.ListAvailableLlmConfigsForTenantAsync` now excludes any group config
whose Id collides with an already-included tenant config's Id, for every caller of this shared
endpoint (AgentBuilder, PromotionDialog's override picker, GroupAgentTemplateBuilder, PackEditor,
HookRuleForm, AgentChat, TenantDetail). (`src/Diva.TenantAdmin/Services/TenantGroupService.cs`)

**Tests**: `ListAvailableLlmConfigsForTenantAsync_ExcludesGroupConfig_WhenIdCollidesWithTenantConfig`
— seeds one tenant config and one group config (naturally colliding on Id via SQLite's independent
per-table autoincrement), asserts only the tenant-scoped entry is returned for that Id.
(`tests/Diva.TenantAdmin.Tests/TenantGroupServiceTests.cs`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
315/315 (314 + 1 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests`
351/352 (same single pre-existing unrelated failure tolerated). Deployed `diva-api` only; confirmed
`tenantConfigIds` present in the deployed `Diva.TenantAdmin.dll`, ~1 minute old.

---

## [2026-08-12] Bug fix: promotion blocking errors didn't say WHICH object in a cascade needed fixing

**Problem** (follow-up to the same-day LLM config override fix — different root cause, same
confusing symptom to a user testing it): promoting "Analytics - COT" from COT Play to COT Live
still showed *"LlmConfig 'COT' has no key configured for environment 'COT Live'"* even with a valid
override chosen, and none of the new version-info UI. Confirmed live via DB inspection this was
**not** the same bug, and **not** a regression — the override correctly exempts the *root* agent
("Analytics - COT" has no pinned LlmConfig at all, so its own check was already a no-op), but its
cascaded delegate "Weather Agent - Global" has its **own** pinned LlmConfig ("COT", tagged only for
COT Play) — which has no override mechanism in this flow, so it correctly still blocks. The error
message just never said *which* object needed it, so it looked identical to the earlier (actually
fixed) root-agent case. Since `preview.canPromote` is false for the whole cascade, the dialog's new
Main-agent/Sub-agents version UI (which only renders when `canPromote` is true) also never appeared
— looking exactly like "the same dialog as before."

**Fix**: `BuildClosureAsync`'s draft, forward-dependency, and blocking-secret error messages now
all prefix the specific object's label (e.g. `"Agent 'Weather Agent - Global': LlmConfig 'COT' has
no key configured..."`), reusing the object-name lookup that was previously only used for the draft
message. (`src/Diva.Infrastructure/Promotion/PromotionOrchestrationService.cs`)

**Tests**: `PreviewAsync_DelegateAgentsOwnMissingLlmConfig_StillBlocks_WithObjectLabelInError`
reproduces the exact scenario — root agent has no pinned config (or gets an override), a cascaded
delegate has its own separately-pinned, environment-mismatched config — asserting the error names
the delegate, not the root. (`tests/Diva.TenantAdmin.Tests/PromotionOrchestrationServiceTests.cs`)

**What the user still needs to do**: this is a real, correct block, not a bug to route around —
"Weather Agent - Global" needs its own valid LLM config for COT Live before "Analytics - COT" can
be promoted there (e.g. a `TenantLlmConfig` named "COT" tagged to COT Live, or repoint the agent's
`LlmConfigId` via `PUT /api/agents/{id}/model-config` on its COT Play copy to a config that already
has one).

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
314/314 (313 + 1 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests`
351/352 (same single pre-existing unrelated failure tolerated). Deployed `diva-api` only via `docker
compose -f docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d --build diva-api`; confirmed
the updated error message string in the deployed `Diva.Infrastructure.dll` via `Select-String
-Encoding Unicode` (per the established lesson that `grep -a` cannot find UTF-16 string literals).

---

## [2026-08-12] Bug fix: admin portal could serve a stale JS bundle after every deploy

**Problem**: after deploying the promote-dialog version display + LLM config override fix, a user
still saw the old dialog and the old blocking error. Confirmed via `docker logs` that the browser's
actual network request to `/api/admin/promotions/preview` never included `targetLlmConfigId` at
all — proof the browser was still running the previous JS bundle, even though the server's built
files were already up to date (verified directly: the new bundle, containing the "Main agent"/
"Sub-agents & dependencies" strings, was already on disk in the portal container). Root cause:
`nginx.conf` had no `Cache-Control` headers at all, so `index.html` (which references the current
build's content-hashed asset filenames) could be cached indefinitely by the browser or an
intermediate proxy — a fresh deploy produces new asset files, but a stale cached `index.html` keeps
pointing at the old ones forever.

**Fix**: `index.html` (and the SPA catch-all route) now gets `Cache-Control: no-cache, no-store,
must-revalidate` — always revalidated, never served stale. `/assets/` (Vite's content-hashed JS/CSS
output) gets `Cache-Control: public, max-age=31536000, immutable` — safe to cache forever, since a
new build always produces a new filename. (`admin-portal/nginx.conf`)

**Verification**: rebuilt and redeployed `diva-portal` only; confirmed via `curl -I` against the
running container that `/index.html` returns `no-cache, no-store, must-revalidate` and
`/assets/main-*.js` returns `public, max-age=31536000, immutable`.

**Note**: this does not touch the 2026-08-12 promote-dialog fix itself (already correctly deployed
server-side) — it fixes the delivery mechanism so a hard refresh (or, going forward, even a normal
refresh) actually picks up the new bundle instead of silently continuing to run the old one.

---

## [2026-08-12] Feature + fix: promote dialog shows current/promoting versions; LLM config override no longer falsely blocked

**Feature**: the Promote dialog's dependency list showed names only, with no version info, and
conflated the main agent being promoted with its cascaded sub-agents/dependencies in one
undifferentiated list — making it unclear exactly what content (which version) was about to ship.

**Backend**: `PromotableDependency` gains `CurrentVersion` (live version in the target environment,
null = not live there yet) and `PromotingVersion` (source environment's live version about to be
promoted, null = never recorded yet). `PromotionOrchestrationService.PreviewAsync` populates both
per closure item via the existing `IPromotionLedgerService.GetLiveVersionAsync`.
(`src/Diva.Core/Models/IPromotionDependencyResolver.cs`, `src/Diva.Infrastructure/Promotion/PromotionOrchestrationService.cs`)

**Frontend**: `PromotionDialog.tsx` now splits the preview into a "Main agent" row (the root object)
and a separate "Sub-agents & dependencies" list, each showing `v{current} → v{promoting}` (or
"New → vN" for a first-time promotion into that environment, or "vN (up to date)" for a no-op
re-promote). (`admin-portal/src/api.ts`, `admin-portal/src/components/PromotionDialog.tsx`)

**Bug fix**: picking an LLM config override in the dialog still showed *"LlmConfig 'X' has no key
configured for environment 'Y' — configure it before promoting"* and blocked the Promote button.
Root cause: the blocking-secret check (`GetBlockingSecretDependenciesAsync`) validates the agent's
**own pinned** `LlmConfigId`, with no awareness that `targetLlmConfigId` (the override) would
actually be applied instead — and this wasn't just a preview display bug, `PromoteAsync` ran the
same check internally, so the promotion would have failed server-side even if the button hadn't
been disabled. Fixed: `PreviewAsync` and `BuildClosureAsync` (used by both `PreviewAsync` and
`PromoteAsync`) now accept the caller's `targetLlmConfigId` and skip the LlmConfig check for the
*root* agent specifically when an override is supplied — sub-agents' own pinned configs (which have
no override mechanism) are still validated normally. The dialog now re-runs the preview whenever
the LLM config picker selection changes, so the block clears as soon as a valid override is chosen.
(`src/Diva.Core/Models/IPromotionOrchestrationService.cs`, `src/Diva.Infrastructure/Promotion/PromotionOrchestrationService.cs`,
`src/Diva.Host/Controllers/PromotionsController.cs`, `admin-portal/src/components/PromotionDialog.tsx`)

**Tests**: `PreviewAndPromoteAsync_TargetLlmConfigIdOverride_SkipsFalseBlockOnAgentsOwnMissingConfig`
(reproduces the exact reported bug — blocked without an override, promotes successfully with one)
and `PreviewAsync_PopulatesCurrentAndPromotingVersions` (first promotion shows null/null, then a
diverged re-promotion shows the target's still-live version vs. the source's newer one).
(`tests/Diva.TenantAdmin.Tests/PromotionOrchestrationServiceTests.cs`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
313/313 (311 + 2 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests`
351/352 (same single pre-existing unrelated failure tolerated). `tsc -b` clean; eslint clean (36
problems, matching the established pre-existing baseline exactly, zero from touched files).
Deployed via `docker compose -f docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d
--build`; confirmed `PromotingVersion`/`targetLlmConfigId` present in the deployed
`Diva.Core.dll`/`Diva.Host.dll` and `Diva.Infrastructure.dll` was ~1 minute old.

---

## [2026-08-12] Bug fix: delegated sub-agent auth failure — inherited credential group didn't apply to the child's own MCP server

**Problem** (follow-up to the 2026-08-11 delegate-environment-scoping fix — same user-reported
incident, deeper root cause): even after "Analytics - COT" was re-promoted to COT Play and
correctly delegated to COT Play's own "Weather Agent - Global" row, the delegated call still failed
MCP authentication — while calling Weather Agent directly still worked fine. Confirmed live via
`docker logs` + DB inspection: `AnthropicAgentRunner` propagates the *effective credential
user-group* a parent agent's own MCP server resolved to onto any agents it delegates to (so a
delegation chain stays credential-consistent) — "Analytics - COT" resolved its own "BI Query PROD"
server via user-group 1 and propagated `PreferredUserGroupId=1` to its delegate. But "Weather
Server - Global" (COT Play) only has a credential mapped for user-group 5, not 1.
`McpCredentialSelector.SelectCredential` treated an inherited group with no mapping on a given
server as "resolve to no credential" (SSO passthrough) rather than falling back to the caller's own
valid mapping — and a server-to-server delegation call has no real SSO token to fall back on
in the first place, so that fallback was a **guaranteed** authentication failure, not a safety net.

**Fix**: when the selected/inherited user-group has no credential mapping for a given server,
`SelectCredential` now falls back to resolving independently from the caller's own group
memberships (the same logic used when no group is preferred at all) instead of forcing "no
credential". (`src/Diva.Infrastructure/LiteLLM/McpCredentialSelector.cs`)

**Tests**: updated `PreferredUserGroup_Unmatched_FallsBackToCallersOwnMapping` and
`PreferredUserGroup_UnmappedOnServer_FallsBackToCallersOwnMapping` (renamed from
`*_ResolvesToNoCredential` — they now assert the caller's own valid mapping is used instead of
`null`), reproducing the exact incident shape: a preferred/inherited group with no mapping on this
specific server, while the caller has a different, valid, unambiguous mapping of their own.
(`tests/Diva.Agents.Tests/McpCredentialSelectorTests.cs`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
311/311, `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests` 351/352 (same
single pre-existing unrelated failure tolerated; McpCredentialSelectorTests 22/22). Deployed via
`docker compose -f docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d --build`; confirmed
the deployed `Diva.Infrastructure.dll` contains the new fallback log message (verified with
`Select-String -Encoding Unicode` on a copied-out DLL, per the established lesson that `grep -a`
cannot find UTF-16 string literal values) and the DLL was ~1 minute old.

---

## [2026-08-11] Feature: Custom Variables editable directly on a promoted agent

A promoted (non-default-environment) agent is otherwise entirely read-only in AgentBuilder — but
Custom Variables (used for `{{variable_name}}` substitution in the system prompt, e.g.
`company_name`/`disclaimer_text`) are legitimately environment-specific, like `LlmConfigId` already
was (`PUT /api/agents/{id}/model-config`, added earlier). Extended the same carve-out to Custom
Variables.

**Backend**: `AgentExportService.ApplyAgentFields` now only applies the bundle's
`CustomVariablesJson` to a brand-new row (first promotion) — an already-existing row keeps its own
current value, so a later re-promotion/re-import from source no longer silently resets it. New
narrow `PUT /api/agents/{id}/custom-variables` endpoint (`UpdateAgentCustomVariablesDto`), same
shape as the existing model-config endpoint — deliberately bypasses the read-only lock and touches
only this one field. (`src/Diva.Infrastructure/AgentExport/AgentExportService.cs`,
`src/Diva.Host/Controllers/AgentsController.cs`)

**Frontend**: extracted the Custom Variables key/value editor out of the (fully locked)
`AdvancedConfigPanel` collapsible into its own standalone `CustomVariablesEditor`, rendered outside
the Advanced tab's `<fieldset disabled={isReadOnly}>`. Shows the same "rest of this agent is
locked, but X can still be tuned per-environment" note + a dedicated "Save custom variables"
button (calling the new endpoint) when read-only — otherwise flows through the normal Save/Draft/
Publish actions unchanged. (`admin-portal/src/api.ts`, `admin-portal/src/components/AgentBuilder.tsx`)

**Tests**: `ImportAsync_CreatesNewAgent_InheritsSourceCustomVariables` (first promotion still
carries the source's value onto a brand-new row) and
`ImportAsync_OverwritesExistingAgent_PreservesExistingCustomVariables` (re-promotion/re-import
keeps the existing row's value instead of overwriting it with source's) —
(`tests/Diva.Agents.Tests/AgentExportServiceTests.cs`).

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
311/311, `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests` 351/352 (349 +
2 new, same single pre-existing unrelated failure tolerated). `tsc -b` clean; eslint clean (no new
issues in touched files, pre-existing baseline unchanged). Deployed via `docker compose -f
docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d --build`; confirmed the deployed
`Diva.Host.dll` contains `UpdateCustomVariables`/`custom-variables` and `Diva.Infrastructure.dll`'s
mtime was ~2 minutes old.

---

## [2026-08-11] Bug fix: promoted parent agents could delegate to the WRONG environment's sub-agent

**Problem** (real production incident): "Analytics - COT" promoted to COT Play delegated to its
sub-agent "Weather Agent - Global" using a bad MCP credential — even though calling that same
sub-agent directly worked fine. Root cause: `AgentExportService.ResolveDelegateIdsJsonAsync`
(invoked during promotion materialization to re-link a parent's `DelegateAgentNames` back to
`DelegateAgentIdsJson`) matched by Name **tenant-wide**, with no environment filter. Once a delegate
agent's Name exists as more than one physical row (normal after that delegate has itself been
promoted to any environment), the parent's re-materialized `DelegateAgentIdsJson` could end up
containing another environment's row — carrying that row's own (wrong, for this environment) MCP
server/credential configuration. `AgentImportOptions.TargetAgentId` already had a doc comment
acknowledging this exact ambiguity class for the *parent* agent's own row match — it was never
extended to delegate name resolution.

**Fix**: `AgentImportOptions` gains `DelegateEnvironmentId`. `ResolveDelegateIdsJsonAsync` now
filters candidate rows to `EnvironmentId == DelegateEnvironmentId || EnvironmentId == null` when
set, and de-duplicates per Name (preferring an exact environment match) even when unset — so a
Name is never resolved to more than one Id.
`AgentSnapshotSerializer.MaterializeAsync` (promotion path) passes its own target `environmentId`
through. `AgentsController.Import` (plain "Import Agent" feature) passes the tenant's default
environment, matching where `ImportAsync` already lands newly-created imported agents.
(`src/Diva.Core/Models/AgentExport.cs`, `src/Diva.Infrastructure/AgentExport/AgentExportService.cs`,
`src/Diva.Infrastructure/Promotion/AgentSnapshotSerializer.cs`, `src/Diva.Host/Controllers/AgentsController.cs`)

**Note**: this fixes the bug for future promotions. An agent already promoted with a corrupted
multi-ID `DelegateAgentIdsJson` (e.g. the live "Analytics - COT" in COT Play) needs to be
re-promoted (Save Changes on the parent, then re-promote to COT Play) to pick up the corrected,
single-Id, environment-scoped value — the code fix alone does not retroactively repair already-
written rows.

**Tests**: added `ImportAsync_ScopesDelegateResolution_ToTargetEnvironment_WhenSameNameExistsInMultipleEnvironments`
and `ImportAsync_FallsBackToTenantWideDelegateResolution_WhenEnvironmentNotSpecified`
(`tests/Diva.Agents.Tests/AgentExportServiceTests.cs`), plus an end-to-end promotion-level test
`MaterializeAsync_ReResolvesDelegateAgent_ScopedToTargetEnvironment`
(`tests/Diva.TenantAdmin.Tests/PromotionSnapshotSerializerTests.cs`) reproducing the exact incident:
a delegate Name existing in both the source and target environments, asserting the promoted
parent's `DelegateAgentIdsJson` links to the target environment's own copy.

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
311/311 (310 + 1 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests`
349/350 (348 + 2 new, same single pre-existing unrelated failure tolerated). Deployed via
`docker compose -f docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d --build`; confirmed
the deployed `Diva.Core.dll` contains the new `DelegateEnvironmentId` symbol and `Diva.Infrastructure.dll`'s
mtime was ~3 minutes old.

---

## [2026-08-11] Feature: "mutable HEAD until shipped" — editing no longer burns a new version per save

**Problem**: every distinct-content Save Changes/Publish minted a new global version number, so
routine iteration on a prompt (10 tweaks before ever promoting) produced v1→v10 with no signal about
what was actually meaningful. Precedent already existed in this codebase for "don't version every
edit" (the separate `AgentPromptHistoryEntity` only records on AI-Optimizer-applied changes, never
on manual edits) — this generalizes that principle to the promotion ledger.

**New rule**: a version becomes immutable only once something else actually depends on it — i.e.
once it's live in a *second* environment (via promotion or rollback). Until then, `"manual"`
(Save Changes) and `"publish"` edits in the object's own environment update that same version's
content in place — no new number. `"promotion"` and `"rollback"` are unaffected and always behave as
before (reuse-if-identical-hash, else create a new, distinctly-numbered, auditable checkpoint) —
shipping content to another environment is inherently a meaningful event worth its own version
regardless of how the previous promotion into that same target looked.
(`src/Diva.Infrastructure/Promotion/PromotionLedgerService.cs`)

**Safety**: mutation is gated on `EnvironmentDeployments` — a version is only mutable if no
*other* environment currently has it as `LiveVersionId`. The instant a version is promoted/rolled
back to elsewhere, it freezes; the next same-environment edit creates a new version instead of
silently changing what the other environment is serving.

**Tests**: updated 3 existing `PromotionLedgerServiceTests` (source changed `"manual"` →
`"promotion"` to keep testing the always-increment invariant that still applies to that source).
Added `RecordVersionAsync_ManualSource_ChangedContent_NotYetShipped_MutatesInPlace` (3 consecutive
manual/publish edits with different content all land on version 1, same row, fields reflect the
latest edit) and `RecordVersionAsync_ManualSource_VersionAlreadyLiveInAnotherEnvironment_CreatesNewVersionInstead`
(once a version is live in a second environment, the next edit creates v2, and the other
environment's deployment still points at the original, unmutated content).

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
310/310 (308 + 2 new, explicitly re-confirmed by name), `Diva.Tools.Tests` 78/78,
`DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests` 347/348 (pre-existing unrelated failure). Deployed
via `docker compose -f docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d --build`;
confirmed the deployed `Diva.Infrastructure.dll`'s mtime was ~1 minute old.

---

## [2026-08-11] Feature: Agent List grid shows each agent's live ledger version

`AgentList.tsx` had no visibility into which ledger version (Phase B/D) an agent is currently on —
only the per-agent `AgentBuilder.tsx` page (via the Version History panel) showed it.

**Backend**: `AgentsController.ListPaged` (backing the Agent List grid specifically — the unpaged
`GET /api/agents` used by ~9 dropdown/selector components is unchanged) now resolves each own agent's
live version in one bulk query — `EnvironmentDeployments` joined to `PromotableVersions` keyed by
`(LogicalId, EnvironmentId)` — instead of a per-row lookup. `AgentSummaryDto` gained an optional
trailing `Version` field (backward compatible with existing positional call sites). Shared group
templates are a separate, unrelated concept (`TenantGroupEntity`) with no ledger version, so they
render with `Version = null`. (`src/Diva.Host/Controllers/AgentsController.cs`)

**Frontend**: `AgentSummary.version` (optional) + a new "Version" column in the grid rendering a
`v{N}` outline badge, or "—" for agents/templates with no recorded version yet.
(`admin-portal/src/api.ts`, `admin-portal/src/components/AgentList.tsx`)

**Verification**: `dotnet build Diva.slnx` 0 errors, `dotnet test tests/Diva.TenantAdmin.Tests`
308/308 (no regressions). `tsc -b` clean, eslint clean on both touched frontend files. Deployed via
`docker compose -f docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d --build`;
grep-verified the new `EnvironmentDeployments` join in the deployed `Diva.Host.dll` and confirmed the
DLL's mtime was ~1.5 minutes old (freshly built).

---

## [2026-08-11] Bugfix: promoting unchanged agent content to a new environment always minted a new version number

**Bug reported**: "why does promoting to every environment create a new version number? I'd expect a
new version only when content actually changes in the default environment — other environments
should just sync to that same version." Confirmed as a real bug, not by design.

**Root cause**: `AgentExportBundle` (the wrapper used both by the standalone "download as JSON"
export feature and, via `AgentSnapshotSerializer`, by the promotion ledger's content snapshot)
stamps `ExportedAt = DateTime.UtcNow` fresh on every single call. The ledger's content-hash dedup
(`PromotionLedgerService.RecordVersionAsync`) hashes the *entire* snapshot JSON, so two calls could
never produce a matching hash even when the agent's actual configuration was byte-for-byte
identical — promoting the same unchanged agent to a second environment always looked like "new
content" and minted a new version number. Agent-specific: the other 3 promotable types' snapshot
DTOs (`McpServerSnapshot`/`ScheduledTaskSnapshot`/`AgentGroupSnapshot`) have no timestamp field.

**Fix**: normalize the volatile `ExportedAt`/`SourceTenantId` fields to fixed values before hashing/
storing in `AgentSnapshotSerializer.SerializeAsync` (`bundle with { ExportedAt = default,
SourceTenantId = 0 }`). The standalone JSON-download export feature is unaffected — it calls
`IAgentExportService.ExportAsync` directly and still gets a real timestamp.
(`src/Diva.Infrastructure/Promotion/AgentSnapshotSerializer.cs`)

**Tests**: `SerializeAsync_CalledTwiceWithNoChanges_ProducesIdenticalSnapshotJson` (pins the exact
mechanism) and `PromoteAsync_SameUnchangedAgentContent_ToDifferentEnvironments_ReusesTheSameVersionNumber`
(end-to-end: promotes to Staging then Production, asserts both get the same version number and the
ledger has only one recorded entry, not two).

**Unrelated but required to verify this**: nuget.org was mid-rollout of the .NET 10 August patch
(`10.0.11`) — `Microsoft.Extensions.*`/`Microsoft.Data.Sqlite.*` had already published it but
`Microsoft.EntityFrameworkCore.*` hadn't yet, and this repo's floating `Version="10.0.*"` package
references (14 across 6 `.csproj` files) jumped straight to the incomplete patch, breaking every
build with `NU1103`. Pinned all 14 to the last confirmed-consistent `10.0.10` (verified directly
against nuget.org's index for every affected package first) to unblock verification — safe to float
again (or bump to `10.0.11`+) once Microsoft finishes publishing the full family.
(`src/Diva.Core/Diva.Core.csproj`, `src/Diva.Host/Diva.Host.csproj`,
`src/Diva.Infrastructure/Diva.Infrastructure.csproj`,
`src/Diva.Infrastructure.SqlServer/Diva.Infrastructure.SqlServer.csproj`,
`src/Diva.Sso/Diva.Sso.csproj`, `tools/DbFix/DbFix.csproj`)

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
308/308 (306 + 2 new, both explicitly re-run by name and confirmed passing), `Diva.Tools.Tests`
78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests` 347/348 (pre-existing unrelated
`ContextWindowTests` failure). Deployed via `docker compose -f docker-compose.tei.yml -f
docker-compose.sqlserver.yml up -d --build`; confirmed the deployed `Diva.Infrastructure.dll`'s
mtime was ~2 minutes old (freshly built, not stale).

---

## [2026-08-11] Bugfix + Feature: direct "Save Changes" left the source environment's version stale; drafts now block promotion

**Bug reported**: after publishing an agent's latest edits and promoting to another environment, the
*source* (e.g. Dev) environment's own version pointer still showed a previous version instead of the
one that was actually live. Root cause: `AgentsController.Update` ("Save Changes" — the direct-write
path used outside the Save Draft/Publish flow) applied the edit straight onto the live row but never
called `IPromotionLedgerService.RecordVersionAsync`, unlike `Publish`. The live row's content was
always current, but the version ledger (and therefore Version History / the `v{N}` badge) silently
fell behind it — `PromotableVersionEntity.Source` already listed `"manual"` as a valid value for
exactly this case, it was simply never wired up.

**Fix**: `Update` now records a ledger version (`Source="manual"`) the same way `Publish` does,
whenever the agent has environment/logical identity. (`src/Diva.Host/Controllers/AgentsController.cs`)

**Feature**: "if [an] agent is in draft mode, it should not be promoted to any environment" — an
unpublished draft means the live row is not what was last edited; promoting it would silently ship
stale content. `PromotionOrchestrationService.BuildClosureAsync` (shared by both `PreviewAsync` and
`PromoteAsync`) now checks every object in the promotion's dependency closure for a pending
`IEntityDraftService` draft in the source environment and hard-blocks with a clear per-object error
if found — publishing or discarding the draft unblocks it again. No frontend changes were needed:
`PromotionDialog.tsx` already renders `preview.blockingError` and disables the confirm button when
`canPromote` is false, so this surfaces automatically for single-target promotions. Bulk promote
(which skips the up-front preview) still gets the same hard block per-target from the server, shown
in the existing per-target result list.
(`src/Diva.Infrastructure/Promotion/PromotionOrchestrationService.cs`)

**Tests**: `PreviewAndPromoteAsync_UnpublishedDraftInSourceEnvironment_Blocked` (both endpoints reject
with a "draft" error while a draft exists; promotion succeeds normally once the draft is cleared).

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
306/306 (305 + 1 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14, `Diva.Agents.Tests`
347/348 (pre-existing unrelated `ContextWindowTests` failure). Deployed via `docker compose -f
docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d --build`; grep-verified `GetDraftAsync`
present in both deployed DLLs (a method-name check — a literal error-message substring gave a false
"stale deploy" signal first since .NET string literals are UTF-16 and a plain ASCII grep can't see
them; see repo memory for the corrected technique).

---

## [2026-08-11] Feature: promote dialog — explicitly override or keep the target's LLM config

**Context**: `LlmConfigId` is deliberately excluded from the portable agent snapshot (Phase G design —
LLM configs are environment-specific infrastructure, never promoted content), so re-promoting an agent
already leaves the target environment's own `LlmConfigId` untouched by default — this was previously
only true "by accident of omission," never an explicit, visible choice.

**Change**: `PromoteAsync` (single-target promotion only — a single config Id isn't portable across
several different target environments in a bulk promote) gained an optional `targetLlmConfigId`. When
provided, the just-promoted agent's row in the target environment is explicitly set to that config Id
(via a new `ApplyLlmConfigOverrideAsync` helper, applied whether or not the promotion's content itself
was an idempotent skip). When omitted (default), the target's existing `LlmConfigId` is left exactly as
it was — explicit, not incidental.
(`src/Diva.Core/Models/IPromotionOrchestrationService.cs`,
`src/Diva.Infrastructure/Promotion/PromotionOrchestrationService.cs`)

**API/UI**: `PromotionsController`'s `PromoteRequest` gained `TargetLlmConfigId`; `BulkPromoteRequest`
intentionally did not. `PromotionDialog.tsx` shows an "LLM config in {target}" dropdown — "Keep the
target's existing config" (default) or any config available in that specific target environment (fetched
via the existing `listAvailableLlmConfigs(environmentId)`) — only for single-target Agent promotions.
(`src/Diva.Host/Controllers/PromotionsController.cs`, `admin-portal/src/api.ts`,
`admin-portal/src/components/PromotionDialog.tsx`)

**Tests**: `PromoteAsync_TargetLlmConfigId_OverridesThePromotedAgentsConfigInTargetEnvironment` and
`PromoteAsync_NoTargetLlmConfigId_KeepsTheTargetsExistingConfigUntouched` (re-promotes changed content
over a target whose `LlmConfigId` was manually set post-promotion, confirms the config survives while
the content itself updates).

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test tests/Diva.TenantAdmin.Tests` 305/305
(303 + 2 new). `tsc -b` clean, eslint clean on both touched frontend files. Deployed via
`docker compose -f docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d --build`; grep-verified
`ApplyLlmConfigOverrideAsync`/`TargetLlmConfigId` in the deployed DLLs and "existing config" in the
deployed portal JS bundle.

---

## [2026-08-11] Bugfix + Feature: promotion read the wrong environment's content; added change-summary comments

**Bug found during a design review of the promotion/versioning system**: `IPromotableSnapshotSerializer.
SerializeAsync` and all 4 `IPromotionDependencyResolver` implementations never filtered by environment —
`SerializeAsync(tenantId, logicalId, ct)` had no `environmentId` parameter at all, so every read of "what
content should this promotion push" just grabbed whichever physical row matched `(TenantId, LogicalId)`
first (no `ORDER BY` — in practice usually the oldest/lowest-id row). This "accidentally" looked correct
as long as promotions always originated from the single oldest/default environment, but promoting from
any OTHER environment (e.g. Staging→Production, which the UI already allows) could silently push the
wrong environment's content once that environment's row had diverged from the oldest one.

**Fix**: `SerializeAsync` now takes `(tenantId, environmentId, logicalId, ct)` and filters by
`EnvironmentId` in all 4 serializers (`AgentSnapshotSerializer`, `McpServerSnapshotSerializer`,
`ScheduledTaskSnapshotSerializer`, `AgentGroupSnapshotSerializer`). All 5 real-query
`GetCascadeDependenciesAsync`/`GetForwardDependenciesAsync`/`GetBlockingSecretDependenciesAsync`
implementations in `PromotionDependencyResolvers.cs` now actually use the `environmentId` parameter they
already received instead of ignoring it. `PromotionOrchestrationService` threads `fromEnvironmentId`
through both `PreviewAsync` and `PromoteAsync`'s serializer calls. The 4 Publish-flow controllers
(`AgentsController`, `AgentGroupsController`, `McpServersController`, `SchedulerController`) — which also
call `SerializeAsync` to record a ledger version on in-place publish — were fixed the same way, using the
`environmentId` already resolved from the entity being published.
(`src/Diva.Core/Models/PromotionModels.cs`, `src/Diva.Infrastructure/Promotion/*SnapshotSerializer.cs`,
`src/Diva.Infrastructure/Promotion/PromotionDependencyResolvers.cs`,
`src/Diva.Infrastructure/Promotion/PromotionOrchestrationService.cs`,
`src/Diva.Host/Controllers/{Agents,AgentGroups,McpServers,Scheduler}Controller.cs`)

**Feature**: promoting now accepts an optional `changeNote` (a user-typed summary of what changed),
recorded on the resulting ledger version(s). `IPromotionOrchestrationService.PromoteAsync` gained a
`string? changeNote` parameter; `PromotionsController`'s `PromoteRequest`/`BulkPromoteRequest` gained a
matching `ChangeNote` field. `PromotionDialog.tsx` gained a "Change summary (optional)" textarea shown
before confirming, wired into both the single-target and bulk-promote calls.
(`src/Diva.Core/Models/IPromotionOrchestrationService.cs`, `src/Diva.Host/Controllers/PromotionsController.cs`,
`admin-portal/src/api.ts`, `admin-portal/src/components/PromotionDialog.tsx`)

**Tests**: fixed all 21 existing call sites across `PromotionSnapshotSerializerTests.cs` (15) and
`PromotionOrchestrationServiceTests.cs` (6) for the new signatures. Added 2 new regression tests:
`PromoteAsync_FromNonDefaultEnvironment_ReadsThatEnvironmentsOwnContent_NotAnUnrelatedRow` (promotes
Dev→Staging, mutates Dev further, then promotes Staging→Production and asserts Production gets Staging's
original content, not Dev's later change — fails without the fix) and
`PromoteAsync_ChangeNote_IsRecordedOnTheLedgerVersion`.

**Verification**: `dotnet build Diva.slnx` 0 errors; `dotnet test Diva.slnx` — `Diva.TenantAdmin.Tests`
303/303 (301 existing + 2 new), `Diva.Tools.Tests` 78/78, `DivaFsMcpServer.Tests` 14/14,
`Diva.Agents.Tests` 347/348 (the 1 failure is the documented pre-existing
`ContextWindowTests.RunAsync_CallsMaybeCompactAnthropicBeforeLlmCall`, unrelated). `tsc -b` clean, eslint
clean on both touched frontend files. Deployed via `docker compose -f docker-compose.tei.yml -f
docker-compose.sqlserver.yml up -d --build`; grep-verified `GetLiveVersionAsync`/`ChangeNote` present in
the deployed `Diva.Host.dll` and "Change summary" present in the deployed portal JS bundle.

---

## [2026-08-11] Feature: Agent Builder — Version History panel (view + diff + rollback)

Phase D/B's promotion ledger (`PromotableObjectEntity`/`PromotableVersionEntity`/
`EnvironmentDeploymentEntity`) and its `GET /promotions/history`/`/diff` + `POST /promotions/rollback`
endpoints have existed since 2026-07-30, with `api.ts` client methods already in place — but zero
admin-portal UI ever called them, and the agent's live ledger version was never surfaced anywhere in
the UI (confirmed by review: only a narrower, prompt-text-only "System Prompt History" existed).

**Backend**: added `IPromotionLedgerService.GetLiveVersionAsync(tenantId, logicalId, environmentId, ct)`
(+ `PromotionLedgerService` implementation, + `LiveVersionInfo(VersionId, Version)` record in
`PromotionModels.cs`) — resolves which ledger version is currently live in a specific environment via
`EnvironmentDeploymentEntity.LiveVersionId`, since a single `PromotableVersionEntity` has no
environment column of its own (content can be identical/deduped across environments). New endpoint
`GET /api/admin/promotions/live-version?logicalId=&environmentId=&tenantId=`.
(`src/Diva.Core/Models/PromotionModels.cs`, `src/Diva.Infrastructure/Promotion/PromotionLedgerService.cs`,
`src/Diva.Host/Controllers/PromotionsController.cs`)

**Frontend**: new `VersionHistoryDialog.tsx` — lists the full ledger (newest first), each row shows a
`v{N}` badge, a color-coded Source badge (Published/Promoted/Rolled back), a "Live here" badge for
whichever version matches the current environment's live pointer, and an expandable "Changes" panel
diffing that version against the immediately-previous one (`getPromotionDiff`, a classic changelog
reading — not diffed against "live", which uses a separate confirm-gated "Rollback to this version"
action instead). Rollback re-confirms via a nested dialog before calling `rollbackPromotion`, then
refreshes both the history list and the parent's agent form/live-version badge.
`AgentBuilder.tsx` gained a "Version History" button (next to "Promote") and a small `v{N}` badge next
to the page title, sourced from the new `getLiveVersion` call and refreshed after Publish/Rollback.
Named/iconed distinctly from the pre-existing "History" (prompt-only) button to avoid confusion.
(`admin-portal/src/components/VersionHistoryDialog.tsx` — new,
`admin-portal/src/components/AgentBuilder.tsx`, `admin-portal/src/api.ts`)

**Scope note**: only wired into `AgentBuilder.tsx` (Agents). MCP Servers/Scheduled Tasks/Agent Groups
already have the same draft/publish backend endpoints but still have zero draft/publish/promote/history
UI at all — unchanged, pre-existing gap, not addressed by this change.

**Verification**: `dotnet build Diva.slnx` 0 errors, `dotnet test tests/Diva.TenantAdmin.Tests` 301/301,
`tsc -b` clean, eslint clean on all 3 touched files (confirmed via `Select-String -SimpleMatch` finding
zero references to any of them in the full lint output). Deployed via
`docker compose -f docker-compose.tei.yml -f docker-compose.sqlserver.yml up -d --build`; grep-verified
`GetLiveVersionAsync` present in `Diva.Host.dll`/`Diva.Infrastructure.dll`/`Diva.Core.dll` and
"Version History" present in the deployed portal JS bundle; `GET .../live-version` returns 401
unauthenticated (route reachable, auth-gated as expected).

---

## [2026-08-11] Feature: Agent List — filter by Agent Access Group

`AgentList.tsx` had no way to narrow the list to agents belonging to a specific Agent Access Group
(`AgentGroupEntity`, Phase 28 — restricts which users may invoke a set of agents; distinct from
`TenantGroupEntity`'s "shared/publish to group" concept already used elsewhere on this page).

**Backend**: `AgentsController.List`/`ListPaged` gained an optional `accessGroupId` query param.
When present, a new private helper `ResolveAccessGroupMemberIdsAsync` loads the `AgentGroupEntity`
via the existing `IAgentGroupService.GetAsync`, deserializes its `AgentIdsJson` member list, and
the result set is filtered to just those agent IDs. Filtering happens server-side (after the
existing own/shared-template merge and non-admin denied-agent filtering) so pagination stays
correct. (`src/Diva.Host/Controllers/AgentsController.cs`)

**Frontend**: `api.ts` — `AgentListParams` gained `accessGroupId`; `listAgentsPaged` appends it to
the query string. `AgentList.tsx` fetches the tenant's Agent Access Groups (scoped to the current
environment via `api.listAgentGroups`) and renders a "All access groups" `Select` filter in the
`ListToolbar`'s filter slot, wired to `usePagedList`'s `update()`.

Files: `src/Diva.Host/Controllers/AgentsController.cs`, `admin-portal/src/api.ts`,
`admin-portal/src/components/AgentList.tsx`.

---

## [2026-08-10] Feature: tenant admins can edit local users' roles

`LocalUsersPanel.tsx` (Settings → local username/password accounts) could set roles at *creation*
time only — `ILocalAuthService` had no update path, only `CreateUserAsync` (roles), `DeleteUserAsync`,
`ResetPasswordAsync`, and `SetActiveAsync`. Roles were shown as read-only badges with no way to
change them for an existing user.

**Backend**: `ILocalAuthService.UpdateRolesAsync(tenantId, id, roles, ct)` + `PUT
/api/auth/local-users/{id}/roles` (`[RequireTenantAdmin]`), mirroring the existing
`reset-password`/`SetActiveAsync` pattern. Local users are the right place to start: their `Roles`
are the actual source of truth (unlike SSO users — see note below), so this takes effect immediately
on the user's next login.

**Frontend**: new "Edit roles" icon button per row (shield icon, alongside reset-password/delete) opening a small
checkbox dialog reusing the same `availableRoles` prop as the Create form, calling
`api.updateLocalUserRoles`.

**Investigated but deliberately NOT changed**: the SSO-facing `UserProfiles.tsx` page
(`settings/users`) also shows roles as read-only, sourced from `UserProfileEntity.Roles` — but that
field is *unconditionally overwritten with fresh JWT claims on every login*
(`UserProfileService.UpsertOnLoginAsync`: "Mirror latest claims from JWT on every login"). Adding an
editable field there today would silently revert the next time that user signs in, and — unlike the
existing `AgentAccessOverrides` field on the same entity, which *looks* like a precedent for this —
confirmed via full-codebase search that `AgentAccessOverrides` is **only ever read by the admin UI's
own display logic**, never consulted by `TenantContextMiddleware` when building the request's actual
`TenantContext.AgentAccess`. So it would not be a real, enforced override either; it's a
foundation-laid-but-never-wired field. Flagging this for the user rather than shipping a
same-looking but non-functional "role override" for SSO accounts.

**Verification**: `dotnet build` 0 errors, `dotnet test` 301/301 in `Diva.TenantAdmin.Tests` (only
the known pre-existing `Diva.Agents.Tests.ContextWindowTests` failure remains), `tsc -b`/`eslint`
clean. Per the prior entry's stale-build lesson, verified the deployed artifacts directly this time:
`docker exec` grep on the API DLL confirms `UpdateRolesAsync`/`local-users` are present, and on the
portal's JS bundle confirms the "Edit Roles" dialog string is present, before considering this done.

---

## [2026-08-10] Deployment fix: "Save model config" 404'd — the API container was running a stale build

Not a code bug — the new `PUT /api/agents/{id}/model-config` endpoint from the previous entry
genuinely wasn't present in the deployed container. `docker logs` confirmed it: "Request reached
the end of the middleware pipeline without being handled by application code" (a true route-not-
registered 404, not an auth/business-logic one). Verified directly by grepping the running
container's compiled DLL — `docker exec core-ai-diva-api-1 sh -c "grep -a -c 'UpdateModelConfig'
/app/Diva.Host.dll"` returned `0`, despite `dotnet build`/`dotnet test`/`docker compose up -d
--build` all having reported success and the container showing healthy.

**Fix**: rebuilt with `docker compose build --no-cache diva-api` + `up -d --force-recreate
diva-api`. Re-grepped the DLL afterward and confirmed the strings are now present (count 1 for
`model-config`, 2 for `UpdateModelConfig`).

**Process change going forward**: for any brand-new backend route (not just edits to existing
ones), grep the deployed container's DLL for a distinctive new symbol name before declaring the
fix verified — "Built"/"Recreated"/"healthy" logs are not sufficient proof the new code is actually
running. Saved to repo memory (`sqlserver-migration.md`) alongside the existing frontend-bundle
verification lesson.

---

## [2026-08-10] Refinement: environment-specific agents can still tune their own LLM config/model

Amends the same-day non-default-environment edit lock. Which LLM config/model an agent uses is
environment-specific infrastructure (e.g. Staging pointed at a cheaper/local model, Production at
the real provider) rather than "agent config" that should stay pinned to whatever was promoted — so
it needs its own carve-out from the read-only lock.

**Backend**: new `PUT /api/agents/{id}/model-config` endpoint (`UpdateAgentModelConfigDto`,
`LlmConfigId`/`ModelId` only) that intentionally bypasses `IsLockedForEditingAsync` — deliberately
narrow (only these two fields, applied directly, no merge with a client-supplied full entity) so it
can never be used as a backdoor to edit anything else on a locked agent.

**Frontend** (`AgentBuilder.tsx`): the LLM Config and Model `<Select>` fields are no longer inside
any disabled `<fieldset>`, so they stay interactive even on a read-only agent. A small inline banner
+ "Save model config" button appears next to them (only when read-only) that calls the new endpoint
directly via `api.updateAgentModelConfig`, independent of the main Save/Draft/Publish actions.
Restructured the single form-wide fieldset into five narrower ones (Identity tab; Temperature/Max
Iterations; System Prompt onward; Tools tab; Advanced tab) so only the LLM Config/Model exception
falls outside a lock — everything else in the "Model & Prompt" tab remains fully read-only.

**Verification**: `dotnet build` 0 errors, `dotnet test` 301/301 in `Diva.TenantAdmin.Tests` (only
the known pre-existing `Diva.Agents.Tests.ContextWindowTests` failure remains), `tsc -b`/`eslint`
clean, API and admin-portal both rebuilt and redeployed (API force-recreated to confirm the fresh
build was actually running, since compose reported it unchanged on the first pass).

---

## [2026-08-10] Fix-up: read-only agents could no longer switch tabs to view other settings

Follow-up to the same-day non-default-environment edit lock. The `<fieldset disabled>` wrapper was
placed around the entire `<Tabs>` element, including `<TabsList>` — since `TabsTrigger` renders as
a native `<button>`, the fieldset's disabled-cascade also disabled tab switching itself, so a
read-only agent got stuck on whichever tab it opened to instead of just losing the ability to edit.

**Fix**: moved the `<fieldset disabled={isReadOnly}>` wrapper to start *after* `</TabsList>`,
wrapping only the four `<TabsContent>` blocks. Tab navigation is now always clickable; only the
actual form fields within each tab are disabled for a non-default-environment agent.

**Verification**: `tsc -b` and `eslint` clean (only the pre-existing unrelated `AgentBuilder.tsx:759`
warning remains), admin-portal rebuilt and redeployed.

---

## [2026-08-10] Feature: agents outside the tenant's default environment can no longer be edited directly

Per explicit request — agents are meant to be authored in the default environment and pushed
outward via Promotion; editing a non-default copy directly would let it drift from whatever was
actually promoted, defeating the point of environment-scoped promotion.

**Enforced server-side, not just hidden in the UI** (a UI-only lock is trivially bypassed via a
direct API call): `AgentsController.Update`/`SaveDraft`/`Publish` now resolve the tenant's default
environment (`IEnvironmentService.GetDefaultAsync`) and return `403 Forbidden` when the target
agent's `EnvironmentId` is set and differs from it. Untagged (legacy/pre-Phase-E) agents remain
editable everywhere, unaffected.

**Confirmed zero impact on Promotion**: `AgentSnapshotSerializer.MaterializeAsync` (the promotion
code path) calls `AgentExportService.ImportAsync` directly — a service method, not this HTTP
controller action — so promoting into a non-default environment is completely unaffected by this
guard, by construction.

**Frontend** (`AgentBuilder.tsx`): computes `isReadOnly` from the loaded agent's own `environmentId`
vs. the tenant's default (`GET /api/agents/{id}` returns the raw entity with no narrowing DTO, so
this field is reliably populated — see the 2026-08-04 DTO-omission gotcha for why that check
mattered here). When read-only: shows a banner naming the agent's environment, disables the Save
Draft/Publish/Save Changes buttons (all of them — there were two separate "Save" buttons plus a
draft-banner "Publish now" shortcut), and wraps the entire tabbed form in
`<fieldset disabled className="contents">` so every native input/select/textarea/button inside is
inert without needing to touch each one individually. Only `Update`/`SaveDraft`/`Publish` are
guarded — viewing, exporting, and Promoting *from* a non-default agent are all still allowed;
deletion was intentionally left alone since the request was specifically about editing.

**Verification**: `dotnet build` 0 errors, `dotnet test` 301/301 in `Diva.TenantAdmin.Tests` (only
the known pre-existing `Diva.Agents.Tests.ContextWindowTests` failure remains), `tsc -b`/`eslint`
clean (only the pre-existing unrelated `AgentBuilder.tsx:759` warning), API and admin-portal both
rebuilt and redeployed.

---

## [2026-08-10] Bugfix: Agent Builder's Delegated Agents picker offered agents from every environment

One of the 8 `api.listAgents()` callers flagged (but not fixed) back on 2026-08-04 as having the
same gap as the Member Agents picker. `DelegateAgentSelector.tsx` fetched the full unfiltered agent
list once on mount with no environment awareness, so an agent's delegate picker offered every
agent in the tenant regardless of environment.

Confirmed this is also a deliberate design point, not just a UI nicety — traced the actual runtime
delegation path (`DelegationAgentResolver.ExecuteAgentAsync` → `DynamicAgentRegistry.GetByIdAsync`)
and found it already passes `tenant.EnvironmentId` and matches
`d.EnvironmentId == null || d.EnvironmentId == environmentId`. So a delegate ID pointing at a
different, *explicitly tagged* environment's agent already fails to resolve at runtime ("Agent not
found") — cross-environment delegation was already a no-op, just configurable through the UI in a
way that silently never worked. Only a delegate pointing at an *untagged* agent would still resolve
today (untagged agents are rare now that Create/Import always tag an environment).

**Fix**: `DelegateAgentSelector` gained an `environmentId` prop, filtering the picker to the current
environment (`AgentBuilder.tsx` passes `currentEnvironmentId` — safe here specifically because the
agent list you navigate from is already environment-filtered, so the agent being edited always
belongs to the topbar's current environment; unlike Agent Groups/MCP Servers, there's no separate
"entity's own environment when editing" to track). Empty-state message now names the environment.

**Verification**: `tsc -b` and `eslint` clean (only the pre-existing unrelated `AgentBuilder.tsx:759`
warning remains), admin-portal rebuilt and redeployed.

---

## [2026-08-07] Bugfix: importing an agent never tagged it with an environment — it showed up in every environment

`AgentExportService.ImportAsync`'s "create new agent" branch (the path used by the admin portal's
"Import Agent" feature) never set `EnvironmentId`/`LogicalId` on the newly-created row at all — the
same class of bug fixed for `AgentsController.Create` back in the original Create-path fix
(`d8e9909`), but the *Import* path was never updated with the same logic. An imported agent stayed
permanently untagged (`EnvironmentId = null`), which matches every environment's "untagged fallback"
filter (`a.EnvironmentId == environmentId || a.EnvironmentId == null`) — so it appeared identically
in Development, Staging, and Demo Play instead of belonging to just one.

**Fix**: imported agents (the genuine-create case only — overwriting an existing agent by name, or
promotion's own `TargetAgentId`-driven overwrite, are untouched) are now tagged with `LogicalId =
Guid.NewGuid()` and `EnvironmentId` resolved to the tenant's **default** environment, per the user's
explicit request — not whichever environment the importing admin currently has selected, since an
imported bundle carries no environment context of its own and landing it somewhere predictable (for
review/promotion afterward) is safer than silently inheriting the caller's current tab.

**Verified safe for promotion**: `AgentSnapshotSerializer.MaterializeAsync` (the promotion-driven
caller of the same `ImportAsync` method) already overwrites `EnvironmentId`/`LogicalId` itself
immediately after the call returns, specifically because it anticipated `ImportAsync` not handling
these columns — so this fix has zero effect on promotion behavior, confirmed by all 301
`Diva.TenantAdmin.Tests` (including the full promotion suite) still passing unchanged.

**No retroactive data fix needed**: checked directly against the database — zero agents currently
have `EnvironmentId = NULL` for tenant 1. The pre-existing idempotent Program.cs startup backfill
sweep already self-healed any previously-imported untagged agent(s) on an earlier restart; this fix
just stops new imports from needing that safety net going forward.

**Verification**: `dotnet build` 0 errors, `dotnet test` 301/301 in `Diva.TenantAdmin.Tests` (only
the known pre-existing `Diva.Agents.Tests.ContextWindowTests` failure remains), API and admin-portal
both rebuilt and redeployed.

---

## [2026-08-04] Refinement: viewing a non-default environment no longer shows untagged ("default environment") MCP credentials

Applied the same tenant-default-aware null-matching fix used for Platform API Keys (`2b325e9`) to
`CredentialsController.List`/`ListPaged`. Previously, an untagged credential matched *every*
environment filter (`c.EnvironmentId == environmentId || c.EnvironmentId == null`) — so switching
to Staging still showed all of the tenant's untagged credentials alongside any Staging-specific
ones, which read as "default environment credentials leaking into Staging."

**Fix**: untagged credentials now only appear while viewing the tenant's **default** environment,
resolved via the existing `IEnvironmentService.GetDefaultAsync` — mirroring exactly how untagged
Platform API Keys are already filtered. Affects both the `McpServerManager.tsx`/`AgentBuilder.tsx`
credential pickers and the standalone Credentials admin list page (`CredentialManager.tsx`), since
all three share this one backend endpoint.

**Important tradeoff to flag**: this only changes what's *offered in the admin UI* for a given
environment. It does **not** change actual runtime resolution — `CredentialResolver.ResolveAsync`
still looks up a credential by name for the caller's own environment first, falling back to an
untagged row of the same name, regardless of environment. So an untagged credential that's no
longer selectable while viewing Staging will still be used automatically at runtime if referenced
by name and no Staging-specific override exists — it just can no longer be *picked from this
particular dropdown* while viewing Staging. To reference it from a Staging context going forward,
either switch to the default environment to configure the mapping, or tag a credential explicitly
to Staging.

**Verification**: `dotnet build` 0 errors, `dotnet test` 301/301 in `Diva.TenantAdmin.Tests` (only
the known pre-existing `Diva.Agents.Tests.ContextWindowTests` failure remains), API and admin-portal
both rebuilt and redeployed.

---

## [2026-08-04] Correction: MCP credentials list environment filtering restored (previous revert was a misread)

Briefly reverted the environment filtering on `McpServerManager.tsx`'s and `AgentBuilder.tsx`'s
credential lists based on a misread of an ambiguous negative question ("can you not filter...?" was
intended as "why isn't this filtered, please make it so," not "please remove the filtering"). Used
`git revert` on that revert commit to restore the filtering exactly as it was after the original
fix: the server's own `environmentId` when editing, the topbar's current environment when creating.
No new code changes beyond restoring the prior state — see the "Shared MCP Server credential
dropdowns" entry below for the original rationale.

**Verification**: `tsc -b` and `eslint` clean (only the pre-existing unrelated `AgentBuilder.tsx:759`
warning remains), admin-portal rebuilt and redeployed.

---

## [2026-08-04] Bugfix: `McpServerDto` and `AgentGroupResponse` never actually sent `EnvironmentId` — silently defeating every edit-case environment filter built on top of them

Root cause of "I see it still as same before" after the previous two MCP Server credential-dropdown
fixes. Both response DTOs were missing the field entirely:

```csharp
// McpServersController.ToDto — EnvironmentId never included
private static McpServerDto ToDto(TenantMcpServerEntity s) => new(
    s.Id, s.Name, ..., s.CreatedAt, s.UpdatedAt, s.CreatedByUserId);   // <- no EnvironmentId

// AgentGroupsController.ToDto — same gap
private static AgentGroupResponse ToDto(AgentGroupEntity e) => new(
    e.Id, e.Name, ..., e.CreatedAt, e.UpdatedAt);                     // <- no EnvironmentId
```

So `McpServer.environmentId` and `AgentGroup.environmentId` were always `undefined` on the frontend,
no matter what the actual row was tagged with. This silently defeated the **edit-case** logic in
two previous fixes that read the entity's own environment client-side:
- `McpServerManager.tsx`'s credential/API-key dropdown fixes (`b182d0d`, `7bbf88d`) — `openEdit`
  always computed `editingServerEnvironmentId = null`, so `credentialsEnvironmentId` fell through to
  `null` and every filter silently no-opped back to "show everything," exactly matching the reported
  symptom.
- `AgentGroups.tsx`'s Member Agents picker fix (`c81445f`) — same gap, `g.environmentId` was always
  `undefined` in `openEdit`, so editing an existing group always showed unfiltered agents too
  (the create-path was unaffected, since it derives its environment from the topbar directly rather
  than reading a fetched entity's field).

**Audited every other client-side `.environmentId` read this session touched** to check for the
same class of bug: `PlatformApiKeyInfo` (`ApiKeysController`), `CredentialRow`/`ToListItem`
(`CredentialsController`), and the LLM config response records (`LlmConfigController`) all correctly
include `EnvironmentId` already — confirmed via source read, not just assumption. `WidgetConfigEntity`
is serialized directly with no narrowing DTO, so it's unaffected by this class of bug entirely.

**Fix**: added `EnvironmentId` to both `McpServerDto` and `AgentGroupResponse`, populated from the
entity in each controller's `ToDto`.

**Verification**: `dotnet build` 0 errors, `dotnet test` 301/301 in `Diva.TenantAdmin.Tests` (only
the known pre-existing `Diva.Agents.Tests.ContextWindowTests` failure remains), API and admin-portal
both rebuilt and redeployed (backend DTO change requires both).

---

## [2026-08-04] Refinement: Shared MCP Server's "Select API key" dropdown now excludes keys that could never actually use the mapping rule

Follow-up to the previous credential-dropdown fix. Traced the full runtime resolution chain to
confirm this precisely:
- `TenantContextMiddleware` resolves an untagged (`EnvironmentId = null`) API key's effective
  environment as the tenant's **default** environment — never "every environment".
- `McpCredentialSelector.ResolveSharedBindingsAsync` selects which physical MCP server row to use
  by matching `s.EnvironmentId == null || s.EnvironmentId == <caller's resolved environment>`.
- Combined, a per-API-key mapping rule on a server tagged to a *non-default* environment can never
  fire for an untagged key, because that key's traffic never reaches that server row in the first
  place — it always resolves to the default-environment row instead. The same is true in reverse:
  a key explicitly tagged to Staging can never reach the *default* server row either.

**Fix**: the "Select API key" dropdown now only lists keys whose *effective* environment (explicit
tag, or the tenant's default when untagged) matches the server being edited/created — so it's no
longer possible to build a dead-on-arrival mapping rule. Pre-existing mapping rows keep their
current selection visible (with a stale rule harmless, just never matched) even if the referenced
key no longer qualifies, so nothing silently disappears. Verified against live data first: all 4
existing per-key rules are on Development-tagged (the tenant's default) servers referencing untagged
keys — both resolve to Development, so none are affected by this change; a `QA` key tagged to
Staging is now correctly the only option offered when editing a Staging-tagged server.

**Deliberately NOT applied to the credential dropdown itself** (the "→ credential" picker): traced
`CredentialResolver.ResolveAsync` and confirmed an untagged credential is a genuine, permanent,
by-name fallback for *any* environment that reaches it — "prefer a row tagged to the caller's own
environment; fall back to an untagged row." Once an API key's traffic legitimately reaches a given
server row, an untagged credential is always a valid, reachable choice regardless of that row's
environment. Excluding untagged credentials there would incorrectly hide legitimate options.

**Verification**: `tsc -b` and `eslint` clean, admin-portal rebuilt and redeployed.

---

## [2026-08-04] Bugfix: Shared MCP Server's credential dropdowns (default / per-API-key / per-user-group) listed credentials from every environment

`GET /api/admin/credentials` already supports `?environmentId=` filtering, but `api.listCredentials()`
took no parameter and `McpServerManager.tsx` fetched it once on mount with zero reactivity — all
three credential-selection dropdowns (Default credential, the "→ credential" column in per-API-key
rules, and the "→ credential" column in per-user-group rules) share the same unfiltered list.

Shared MCP Servers have no explicit Environment field of their own in this UI (tagged automatically
from context at creation, like Agent Groups) — so the fix uses the **topbar's current environment**
when creating a new server (what it'll be tagged to on save), and the **server's own existing
`environmentId`** when editing (can't be changed here, so credential choices must stay scoped to
wherever the server already lives). Also fixed the same gap in `AgentBuilder.tsx`'s credential list
— its effect already depended on `currentEnvironmentId` for the LLM Config dropdown but never passed
it to `listCredentials`.

**Important data-reality note, verified via DB query**: unlike Platform API Keys, a credential's
`EnvironmentId == null` is an intentionally-permanent "universal" designation, not a rollout
artifact — confirmed in `CredentialResolver.ResolveAsync`'s own comment: "Prefer a row tagged to the
caller's own environment; fall back to an untagged row... never a DIFFERENT tagged environment's
row." All 8 of tenant 1's existing credentials are untagged (`EnvironmentId = NULL`), so they will
correctly continue to appear in **every** environment's dropdown after this fix — that's by design,
not a leftover bug. The fix only becomes visibly different once an environment-*specific* credential
is created (e.g. a Staging-only credential will no longer appear while editing a Development-tagged
server).

**Verification**: `tsc -b` and `eslint` clean (only the pre-existing, unrelated `AgentBuilder.tsx:759`
warning remains), admin-portal rebuilt and redeployed.

---

## [2026-08-04] Bugfix: Agent Access Group's "Member Agents" picker listed agents from every environment

Same root-cause pattern as the Tool Servers/LLM Config/Allowed Agent Groups pickers:
`GET /api/agents` already supports `?environmentId=` filtering, but `api.listAgents()` took no
parameter and `AgentGroups.tsx` called it once on mount with zero reactivity. Confirmed via DB
query: tenant 1 has 18 agents in Development, 1 in Staging, 1 in Demo Play — so the picker was
showing 20 agents everywhere, 18 of which aren't valid picks for a Staging/Demo Play group (a
group's `AgentIdsJson` holds literal per-environment Agent row IDs, same constraint as Allowed
Agent Groups from the earlier fix).

Agent Groups have no Environment field of their own in this UI (a group is tagged automatically
from the tenant context at creation, per the earlier Create-path fix) — so the picker uses:
- the **topbar's current environment** when creating a new group (that's what it will be tagged to
  on save), and
- the **group's own existing `environmentId`** when editing (its environment can't be changed here,
  so member-agent choices must stay scoped to wherever it already lives, regardless of what the
  topbar switches to mid-edit).

**Fix**: `listAgents` gained an optional `environmentId` parameter; `AgentGroups.tsx` computes an
`agentsEnvironmentId` (create vs. edit as above) and re-fetches whenever it changes. The picker's
empty-state text now also names the relevant environment instead of a generic "No agents available."

**Verification**: `tsc -b` and `eslint` clean, admin-portal rebuilt and redeployed.

**Note**: at least 8 other pickers call `api.listAgents()` with no environment filter
(`DelegateAgentSelector.tsx`, `ScheduleTaskEditor.tsx`, `BusinessRuleEditor.tsx`, `BusinessRules.tsx`,
`PromptEditor.tsx`, `GroupAgentTemplateBuilder.tsx`, `WidgetEditor.tsx`, `AgentAssistantDrawer.tsx`)
and likely have the same gap — not fixed here since they weren't reported; flagged for a follow-up
pass if confirmed.

---

## [2026-08-04] Bugfix: Platform API Keys list page's top environment filter had no visible effect

Root cause was different from every prior "picker not wired up" bug this session — verified
directly against the database: all 5 existing Platform API Keys have `EnvironmentId = NULL`. The
List/ListPaged filter used the same "untagged fallback" pattern as every other environment-scoped
entity (`k.EnvironmentId == environmentId || k.EnvironmentId == null`), which made null keys match
**every** environment filter — so switching Development/Staging/Demo Play always showed the exact
same 5 keys, making the filter look completely inert.

That fallback pattern is actually wrong specifically for API keys. `PlatformApiKeyEntity`'s own doc
comment says a null `EnvironmentId` "resolves as if using the tenant's IsDefault environment" — and
`TenantContextMiddleware` confirms it: `validatedKey.EnvironmentId ?? ResolveDefaultEnvironmentIdAsync(...)`.
So an untagged key is never actually usable in every environment at runtime — it always resolves to
the tenant's default. Unlike Agents/MCP Servers/Scheduled Tasks/Agent Groups, Platform API Keys were
never covered by Program.cs's startup backfill sweep (they're not one of the 4 promotable types), so
they were left permanently null instead of being tagged to the default environment on first boot.

**Fix**: `ApiKeysController.List`/`ListPaged` now resolve the tenant's actual default environment
(via the existing `IEnvironmentService.GetDefaultAsync`) and only let a null-tagged key match when
the filter *is* that default environment — matching the documented runtime resolution behavior
instead of matching every environment. Also improved the list's empty-state message to name the
current environment instead of a generic "No API keys created" (which was misleading once keys
correctly stopped appearing outside the default environment).

**Verification**: `dotnet build` 0 errors, `dotnet test` 301/301 in `Diva.TenantAdmin.Tests` (only
the known pre-existing `Diva.Agents.Tests.ContextWindowTests` failure remains), `tsc -b`/`eslint`
clean, API and admin-portal rebuilt and redeployed.

---

## [2026-08-04] Bugfix: Allowed Agent Groups didn't refresh when changing the per-key Environment dropdown

`ApiKeyManager.tsx` has two independent environment selectors: the topbar switcher
(`currentEnvironmentId`) and a per-form "Environment" field (`form.environmentId` /
`editForm.environmentId`) that tags which environment the key itself will be scoped to. The
Allowed Agent Groups list was wired only to the topbar switcher, so changing the Environment
dropdown *inside* the Create or Edit form had no effect on which groups were shown — it kept
showing groups for whatever environment the topbar happened to be on, not the environment the key
was actually about to be saved with.

**Fix**: the groups-loading effect now uses the active form's own `environmentId` (falling back to
the topbar's current environment when the form hasn't set one explicitly), and re-runs whenever
that value or the open form (`editingId`) changes. The empty-state hint text (added in the prior
fix) now also names the correct environment per form instead of always naming the topbar's.

**Verification**: `tsc -b` and `eslint` clean, admin-portal rebuilt and redeployed.

---

## [2026-08-04] UX fix: "Allowed Agent Groups" section vanished silently when the current environment has no groups yet

Follow-up to the prior fix that scoped the Allowed Agent Groups picker to the selected environment.
That fix is behaving correctly — verified directly against the database: all 12 existing Agent
Groups for tenant 1 have `EnvironmentId = 1` (Development), because they existed before
multi-environment promotion was used and were swept there by the startup default-environment
backfill. Switching the top environment switcher to Staging or Demo Play (which have 0 groups each)
correctly returns an empty list — but the UI hid the whole "Allowed Agent Groups" section whenever
`groups.length === 0`, which is indistinguishable from a bug.

This is expected behavior, not a regression: an Agent Group's `AgentIdsJson` holds literal
per-environment Agent row IDs, so a group can't be meaningfully shared across environments without
being promoted (creating an independent, remapped copy in the target environment).

**Fix**: the section now always renders. When no groups exist for the current environment, it shows
an inline hint ("No agent groups exist in `<Environment>` yet. Promote a group from another
environment or create one first.") instead of disappearing. Applied to both the Create and Edit
forms in `ApiKeyManager.tsx`.

**Verification**: `tsc -b` and `eslint` clean, admin-portal rebuilt and redeployed.

---

## [2026-08-04] Bugfix: Platform API Key's "Allowed Agent Groups" picker showed every environment's groups

Same class of bug as the Tool Servers and LLM Config pickers: `api.listAgentGroups()` had no
`environmentId` parameter, and `ApiKeyManager.tsx` called it once on mount with no reactivity to the
top switcher — even though `AgentGroupsController.List` already supported `?environmentId=`
filtering. Confirmed by direct user question — yes, Agent (Access) Groups are one of the 4
environment-scoped/promotable object types from this session's earlier work, so this dropdown
should scope to the currently-selected environment like every other one.

**Fix**: `listAgentGroups` gained an optional `environmentId` parameter; `ApiKeyManager.tsx`'s
groups-loading effect now passes `currentEnvironmentId` and re-runs when it changes. Only one call
site existed, so extended in place rather than adding a second endpoint.

**Verification**: `tsc -b` and `eslint` clean, admin-portal rebuilt and redeployed.

---

## [2026-08-04] Bugfix: race condition in `usePagedList` could let a stale unfiltered response overwrite a correctly-filtered one

Investigated a report that the top environment dropdown "wasn't working" for API Key filtering.
Checked live server logs (`docker logs core-ai-diva-api-1`) for actual `/api/admin/api-keys/paged`
request patterns and found every environment switch fires **two** sequential requests: one without
`environmentId` and one with it (e.g. `...&pageSize=25` immediately followed by
`...&pageSize=25&environmentId=2`). `usePagedList`'s `load()` had no protection against out-of-order
responses — network timing does not guarantee the later-fired (filtered) request's response arrives
last, so if the unfiltered one happened to resolve after it, it silently overwrote the correct,
filtered `result` state with the unfiltered one. This is foundational, shared infrastructure
(`admin-portal/src/hooks/usePagedList.ts`) used by all 16+ paginated admin-portal list pages, not
just API Keys — any of them could hit the same race under the right timing, even though it was only
reported for this one.

**Fix**: `load()` now tags each fetch with an incrementing request id and only applies a response
(`setResult`/`setError`/`setLoading`) if it's still the most recently issued request, discarding any
stale one — a standard React async-race guard.

**Not fully root-caused**: why exactly two requests fire per switch (one omitting `environmentId`)
wasn't conclusively pinned down through static analysis — likely an interaction between
`usePagedList`'s own mount/param-change effect and the consumer's separate
`useEffect(() => update({ environmentId }), [currentEnvironmentId])` pattern. The redundant request
is now harmless (its response is always discarded if superseded) but still a minor efficiency
cost — flagged for a closer look if it turns out to matter in practice.

**Verification**: `tsc -b` and `eslint` clean, admin-portal rebuilt and redeployed.

---

## [2026-08-04] Bugfix: Platform API Key edit form couldn't change the Environment tag

Investigated in response to a design question ("should platform API keys be environment-wise?").
Answer: yes, and this is mostly already built — `ApiKeysController.Create` already sets `EnvironmentId`
from the request, `List`/`ListPaged` already filter by it, `ApiKeyManager.tsx` already reacts to the
top switcher and has an Environment dropdown on the **create** form. `TenantContextMiddleware`
already resolves a request's environment from the invoking key's own tag (Phase E) — this is the
foundational mechanism, not a gap. One real gap found while verifying: the **edit** form's
`startEdit`/`editForm` never included `environmentId` at all, so an existing key's environment tag
could only ever be set at creation — never changed afterward, even though the backend
`UpdateApiKeyRequest` already supported it.

**Fix**: `startEdit` now populates `environmentId`; the edit form gained the same Environment
`Select` used by the create form.

**Verification**: `tsc -b` and `eslint` clean, admin-portal rebuilt and redeployed.

---

## [2026-08-04] Feature: edit existing tenant-owned LLM configs (previously create/delete only)

`TenantLlmConfigPanel`'s "Tenant-owned Configs" section only had Create and Delete — changing a
config's provider, model, API key, endpoint, or environment tag required deleting and recreating it
(losing its `Id`, which any agent's `LlmConfigId` pin would then dangle against). The backend
`PUT /api/admin/llm-configs/{id}` already supported updating everything except `Name` (immutable —
it's the stable identifier agents pin to and the resolver matches across environments by). Found via
direct user report.

**Fix**: added inline Edit — a pencil button per config row swaps it for an edit form (reusing the
same `LlmForm` + Environment `Select` used by the Create form), pre-populated from the existing row.
API key uses the established masked-placeholder convention (`maskedApiKey` prop — "leave blank to
keep", already used by `PlatformLlmConfig.tsx`'s own edit flow) so re-saving without touching the key
field doesn't clear it. Save calls `api.updateTenantLlmConfigById`.

**Verification**: `tsc -b` and `eslint` clean, admin-portal rebuilt and redeployed. Pure frontend
change — no backend edits needed, the update endpoint already existed.

---

## [2026-08-04] Bugfix: Agent Builder's "LLM Config" dropdown showed every environment's named configs

Same class of bug as the Tool Servers picker fixed earlier today: `ListAvailableLlmConfigsForTenantAsync`
(backing Agent Builder's "LLM Config" dropdown) had no environment filter anywhere in its query —
neither the tenant's own named configs nor group-inherited ones — so a config name tagged to Dev and
its same-named counterpart tagged to Prod both showed up side by side with no way to tell them apart.
Confirmed not intentional — LLM configs are meant to resolve per-environment exactly like MCP servers
(`ILlmConfigResolver.ResolveAsync` already re-resolves by `(Name, environmentId)` at runtime), the
admin-authoring dropdown just never got the same filter applied.

**Fix**:

| File | Change |
|------|--------|
| `ITenantGroupService.cs` / `TenantGroupService.cs` | `ListAvailableLlmConfigsForTenantAsync` gained a required `int? environmentId` parameter; filters both the tenant's own named configs and group-inherited configs by `(EnvironmentId == environmentId \|\| EnvironmentId == null)` |
| `LlmConfigController.cs` | `ListAvailableLlmConfigs` action gained `[FromQuery] int? environmentId` |
| `api.ts` | `listAvailableLlmConfigs` gained an optional `environmentId` parameter |
| `AgentBuilder.tsx` | Already had `useEnvironment()` wired in for the agent's own environment tagging — the LLM-config-loading `useEffect` now also passes `currentEnvironmentId` and re-runs when it changes |

**Verification**: build 0 errors, full test suite 301/301 in `Diva.TenantAdmin.Tests` (only the known
pre-existing `ContextWindowTests` failure elsewhere), `tsc -b`/`eslint` clean on touched frontend
files, both API and admin-portal rebuilt and redeployed.

---

## [2026-08-04] Bugfix: Agent Builder's "Tool Servers" picker showed every environment's MCP servers, duplicated by name

`McpServerSelector.tsx` (the "Shared MCP Servers" multi-select in Agent Builder → Tool Servers)
called `api.listMcpServers()` with no environment filter at all, and the underlying `listMcpServers`
helper in `api.ts` didn't even accept an `environmentId` parameter — even though the backend
`McpServersController.List` action already supported `?environmentId=` filtering. This was
latent/invisible before the 2026-07-31 promotion fix (a tenant could only ever have ONE physical
row per server Name, so there was nothing to duplicate) but became visible as soon as promotion
started correctly creating independent per-environment copies: the same server name now
legitimately exists as multiple rows, and the picker listed all of them side by side with no way
to tell them apart. Found via direct user report.

Runtime tool execution was **not** affected — `McpCredentialSelector.ResolveSharedBindingsAsync`
(the resolver actually used when an agent runs) already filters `TenantMcpServers` by the caller's
environment (confirmed while investigating this report), so an agent always connects to its own
environment's server. Only the admin-authoring picker was unfiltered.

**Fix**: `api.listMcpServers` gained an optional `environmentId` parameter; `McpServerSelector.tsx`
now calls `useEnvironment()` and passes `currentEnvironmentId`, reacting to the top switcher like
every other environment-filtered list/picker.

**Also confirmed, not a bug**: promoting an Agent cascades to promote its referenced MCP servers
too — this is the documented, intentional cascade-dependency behavior
(`AgentPromotionDependencyResolver.GetCascadeDependenciesAsync`), not something to fix.

**Verification**: `tsc -b` and `eslint` clean on both touched files, admin-portal rebuilt and
redeployed.

---

## [2026-08-04] Bugfix: tenant-owned LLM configs list didn't react to the top environment dropdown

`TenantLlmConfigPanel`'s "Tenant-owned Configs" list rendered `ownConfigs` unfiltered — showing
every environment's configs at once (each with its own `EnvironmentBadge`) regardless of the top
switcher's selection, unlike every other environment-filtered list page (Agents, MCP Servers,
Scheduled Tasks, Agent Groups, MCP Credentials, API Keys). The panel already fetched its own
`environments` list (for the create-form's environment picker), but never consulted the global
switcher's `currentEnvironmentId` at all. Found via direct user report.

**Fix**: `TenantLlmConfigPanel` now calls `useEnvironment()` and filters the rendered list to
`c.environmentId === currentEnvironmentId || c.environmentId == null` when a switcher selection
exists (same untagged-fallback pattern used everywhere else). This works correctly in **both**
places the panel is used without any extra branching: master admins (`TenantDetail.tsx`) already
get `currentEnvironmentId = null` from the 2026-08-04 master-admin switcher fix, which naturally
means "no filter" — preserving the existing "show everything with badges" master-admin behavior —
while a real tenant admin (`TenantLlmConfigSettings.tsx`) gets real filtering reacting to their own
top switcher.

**Verification**: `tsc -b` clean; `eslint` shows only the pre-existing unrelated
`react-hooks/exhaustive-deps` warning on this file (present before this change too), admin-portal
rebuilt and redeployed.

---

## [2026-08-04] Bugfix: master/platform admins saw a meaningless environment switcher — and it leaked into every request

`Topbar.tsx` rendered `<EnvironmentSwitcher />` unconditionally, with no `auth.isMasterAdmin()`
check — unlike `AppSidebar`, which already picks an entirely different nav (`platformNavGroups`)
for master admins specifically because tenant-scoped concepts like environments don't apply to a
cross-tenant super-user. Worse than a cosmetic issue: `EnvironmentProvider.load()` called
`api.listEnvironments()` with no `tenantId` argument (defaulting to `1`), auto-selected the
lowest-rank environment, and **persisted its ID to `localStorage`** — which `authHeaders()` then
attached as `X-Environment` on *every* subsequent request, including ones managing a completely
unrelated tenant (e.g. `/platform/tenants/47`). Since `TenantContextMiddleware` honors
`X-Environment` for admin callers (Phase E), a master admin's browser was silently sending Tenant
1's environment ID while operating on any other tenant's data. Found via direct user report ("why
I see environment selection dropdown when I login as platform admin").

**Fix**:

| File | Change |
|------|--------|
| `hooks/useEnvironment.tsx` | `EnvironmentProvider.load()` now checks `auth.isMasterAdmin()` first — clears any stored environment ID, sets `environments`/`currentEnvironmentId` empty/null, and skips the `api.listEnvironments()` fetch entirely for master admins |
| `components/layout/topbar.tsx` | `<EnvironmentSwitcher />` now wrapped in `{!auth.isMasterAdmin() && ...}`, matching the sidebar's existing `isMaster` distinction |

**Verification**: `tsc -b` and `eslint` clean on both touched files, admin-portal rebuilt and
redeployed.

---

## [2026-08-04] Bugfix: tenant admins had no way to reach the environment-aware LLM Config UI

The environment-tagged named-LLM-config panel (create a config, pick provider/model/API key, tag
it to an environment) existed only inside `TenantDetail.tsx`, reachable exclusively via
`/platform/tenants/:id` — a master-admin-only route (Platform → Tenants → [pick a tenant]). A
regular tenant admin logged into their own tenant had no sidebar link or route to it at all — the
tenant-scoped "Settings" nav group has entries for Environments/Users/SSO/MCP
Credentials/Servers/API Keys/A2A/Widgets, but no "LLM Config" — the only "LLM Config" sidebar entry
pointed at `/platform/llm-config`, the *global platform-wide* single config, not the per-tenant
named-configs-with-environment-tags feature. Found via direct user report ("I dont see any option
to set environment wise key within tenant").

**Fix**: exported `TenantLlmConfigPanel` from `TenantDetail.tsx` (unchanged otherwise) and reused it
in a new `TenantLlmConfigSettings.tsx` self-service page (`tenantId` defaults to `1` like every
other tenant-scoped `api.ts` call — the backend's `EffectiveTenantId` pattern overrides it with the
caller's own JWT-derived `TenantId`). Added route `settings/llm-config` and a sidebar link (icon
`Cpu`, "Configuration" group, next to "Environments") in `app-sidebar.tsx`.

**Verification**: `tsc -b` and `eslint` clean on all touched files, admin-portal container rebuilt
and redeployed.

---

## [2026-07-31] Bugfix: promotion silently relocated objects instead of copying them (all 4 promotable types)

Writing a new test suite for the Promotion subsystem (`PromotionSnapshotSerializerTests.cs`,
`PromotionLedgerServiceTests.cs`, `PromotionOrchestrationServiceTests.cs` — 35 tests total, none
existed before) surfaced a severe, previously-undiscovered bug: every `IPromotableSnapshotSerializer.
MaterializeAsync` implementation decided create-vs-update by looking up the existing row by
`(TenantId, Name)` only — never by environment or `LogicalId`. On the first promotion of anything
(the common case, since "first promotion" by definition means only the source row exists so far),
this found and repurposed the **source row itself**, silently changing its `EnvironmentId` to the
target — the object *disappeared from the source environment* instead of an independent target copy
being created alongside it.

MCP Servers had an additional, permanent variant: `TenantMcpServerEntity`'s unique index was
`(TenantId, Name)` only (missing `EnvironmentId`, unlike `McpCredentialEntity`'s correctly-scoped
`(TenantId, Name, EnvironmentId)`), making it structurally impossible to ever have the same server
Name live in two environments simultaneously.

**Fix:**

| File | Change |
|------|--------|
| `DivaDbContext.cs` | `TenantMcpServerEntity`'s unique index widened to `(TenantId, Name, EnvironmentId)` |
| `McpServerSnapshotSerializer.cs` / `ScheduledTaskSnapshotSerializer.cs` / `AgentGroupSnapshotSerializer.cs` | `MaterializeAsync` now matches the existing row by `(TenantId, EnvironmentId, LogicalId)` instead of `(TenantId, Name)` |
| `AgentImportOptions` (`Diva.Core/Models/AgentExport.cs`) | Gained `string? TargetAgentId` — when set, `AgentExportService.ImportAsync` overwrites that exact row instead of searching tenant-wide by Name (additive, backward compatible — the general bundle-import feature is unaffected since it never sets this) |
| `AgentSnapshotSerializer.cs` | Resolves the existing row (if any) for `(tenantId, environmentId, logicalId)` itself and passes it via `TargetAgentId`; forces `OverwriteExisting = false` when none exists, so `ImportAsync` never falls back to its tenant-wide by-Name search and accidentally overwrites another environment's same-named agent |
| `AddMcpServerEnvironmentToUniqueIndex` migration | Both providers (SQLite: `src/Diva.Infrastructure/Data/Migrations`, SQL Server: `src/Diva.Infrastructure.SqlServer/Migrations`) |

**New tests**: `tests/Diva.TenantAdmin.Tests/PromotionSnapshotSerializerTests.cs` (18),
`PromotionLedgerServiceTests.cs` (6), `PromotionOrchestrationServiceTests.cs` (8) — the Promotion
subsystem had zero test coverage before this. Several tests directly confirm the fix (e.g.
`MaterializeAsync_PromotingToNewEnvironment_CreatesIndependentCopy_PreservesSource`,
`MaterializeAsync_SameNameDifferentLogicalId_CreatesIndependentRow_DoesNotRepurposeExisting`).

**Verification**: build 0 errors, `dotnet test Diva.slnx` — 301/301 passed in `Diva.TenantAdmin.Tests`
(only the known pre-existing `ContextWindowTests` failure elsewhere), `has-pending-model-changes`
clean both providers, deployed via `docker-compose.sqlserver.yml` — migration
`20260731222335_AddMcpServerEnvironmentToUniqueIndex` applied cleanly at startup, no errors in logs.

**Still open**: Agent Group promotion still intentionally drops `AllowedUserIdsJson`/`UserGroupLinks`
(explicit user/user-group grants) — documented as deliberate in `PromotableSnapshotDtos.cs` (avoids
leaking Dev-only test-user access into Production on promotion), but the promotion preview UI
doesn't currently warn about this silent drop.

---

## [2026-07-31] Bugfix: Create endpoints never tagged new objects with EnvironmentId/LogicalId

All 4 promotable object types' Create paths (`AgentsController.Create`, `McpServersController.
Create`, `ScheduledTaskService.CreateAsync`, `AgentGroupService.CreateAsync`) never set
`EnvironmentId`/`LogicalId` on new rows — only List/Filter paths were wired when environment
scoping shipped (Phase A/F). New objects were therefore both (a) visible from every environment
(untagged rows fall back to matching any environment) and (b) unpromotable (Promotion requires a
`LogicalId`). Root cause found via a user question about a scheduled task appearing under the
default environment when created under a different one.

**Fix**: all 4 Create paths now set `LogicalId = Guid.NewGuid()` and `EnvironmentId` from the
caller's current `TenantContext.EnvironmentId` (or `null` if unset/system). `IScheduledTaskService.
CreateScheduledTaskRequest` gained a trailing `int? EnvironmentId = null` field (additive);
`IAgentGroupService.CreateAsync` gained a new **required** `int? environmentId` parameter (breaking,
by design — forces every call site to make a deliberate choice), updated at its one production call
site and all 5 test call sites in `AgentGroupServiceTests.cs`.

**Backfill**: not needed — an existing idempotent `Program.cs` startup fixup (runs on every restart)
already sweeps any row with `EnvironmentId == null` and tags it to the tenant's default environment,
so previously-created untagged rows self-heal on the next deploy.

**Verification**: build 0 errors, full test suite same pre-existing-only failure, deployed via
`docker-compose.sqlserver.yml`.

---

## [2026-07-31] Bugfix: Platform API Keys list didn't filter by environment switcher

`ApiKeyManager.tsx` never actually reacted to the environment switcher — unlike every other
environment-filtered list page (Agents, MCP Servers, Scheduled Tasks, Agent Groups, MCP Credentials),
which all got the filter wired in the same pass, API Keys was missed entirely: no `environmentId`
field on `PlatformApiKeyListParams`, no query-string wiring in `api.ts`'s `listApiKeysPaged`, no
`environmentId` query parameter on `ApiKeysController`'s `List`/`ListPaged`, and no `useEffect`
reacting to `currentEnvironmentId` in the component. All 4 gaps closed:

| Area | Change |
|------|--------|
| `admin-portal/src/api.ts` | `PlatformApiKeyListParams` gained `environmentId?: number`; `listApiKeysPaged` appends it to the query string |
| `ApiKeysController.cs` | `List`/`ListPaged` gained `[FromQuery] int? environmentId` — filters the already-fetched `PlatformApiKeyInfo` list to `EnvironmentId == environmentId \|\| EnvironmentId == null` (same untagged-fallback pattern as every other environment-filtered list), matching the `AgentGroupsController`/`SchedulerController` in-controller-filter convention (the service layer has no filter param) |
| `ApiKeyManager.tsx` | Added the same `useEffect(() => { if (currentEnvironmentId) update({ environmentId: currentEnvironmentId }); }, [currentEnvironmentId])` pattern already present on every other list page |

**MCP Credentials note**: audited `CredentialManager.tsx`/`CredentialsController.cs`/`api.ts` — all 4
layers were already correctly wired from the original pass. If the Credentials list still appears
not to change when switching environments, the likely cause is that none of the existing credentials
have actually been tagged to a specific environment yet — untagged (`EnvironmentId == null`) rows are
visible from every environment by design (the fallback for not-yet-migrated data), so switching
environments legitimately returns the same rows until at least one credential is explicitly tagged.

**Verification**: build 0 errors, full test suite same pre-existing-only `ContextWindowTests`
failure, `tsc -b` clean. Deployed; `/api/admin/api-keys/paged?environmentId=1` reachable (401,
auth-gated as expected).

---

## [2026-07-31] Environment-based agent management — multi-client fan-out enhancements (post-Phase F)

Closes the 4 gaps flagged when explaining how to model "Dev → QA → per-client Play/Live" on top of
Track 2's flat-rank environment list: a `ClientGroup` label for grouping/searching and blocking
cross-client promotion, environment tagging on Platform API Keys and Widgets, and a bulk-promote
action so rolling an agent out to N clients doesn't require N trips through the Promote dialog.

| Area | Change |
|------|--------|
| `ClientGroup` label | New nullable `ClientGroup` column on `TenantEnvironmentEntity` — purely a label (Rank still governs promotion order); lets a shared upstream tier (Dev/QA, `ClientGroup=null`) fan out into multiple per-client environment pairs sharing the same rank tier (e.g. "Acme-Play"/"Acme-Live" at rank 2/3, "Globex-Play"/"Globex-Live" also at rank 2/3) |
| Lineage-aware promotion guard | `PromotionOrchestrationService`'s rank check (renamed `CheckPromotionAllowedAsync`) now also blocks promoting between two environments that both carry a *different* non-null `ClientGroup` — e.g. `Acme-Play → Globex-Live` is rejected even though rank alone would allow it. Promoting from/to a shared (`ClientGroup=null`) environment is unaffected |
| Bulk promote | New `POST /api/admin/promotions/bulk` (`BulkPromoteRequest`/`BulkPromoteResultItem`) — promotes the same object into several target environments in one call; each target is validated and promoted independently (one client's failure, e.g. a missing LLM config, doesn't block the others) |
| Environment tagging — API Keys | `CreateApiKeyRequest`/`UpdateApiKeyRequest`/`PlatformApiKeyInfo` (Core) and `CreateApiKeyDto`/`UpdateApiKeyDto` (Host) gained `EnvironmentId` — the `PlatformApiKeyEntity` column already existed from Phase E, only the DTOs/service wiring were missing. `ApiKeyManager.tsx` gained an Environment dropdown (create + edit) and a badge per row |
| Environment tagging — Widgets | `CreateWidgetRequest`/`WidgetConfigDto` (Core) gained `EnvironmentId`, wired through `WidgetConfigService`. `WidgetEditor.tsx` gained an Environment dropdown; `WidgetManager.tsx` gained a badge per row |
| Environment switcher grouping | `EnvironmentSwitcher` now groups options by `ClientGroup` using `SelectGroup`/`SelectLabel` (shared tier first, then one labeled group per client, alphabetical) instead of one long flat list — addresses the "many clients" clutter concern raised when this topology was discussed. `EnvironmentManager.tsx` gained a `ClientGroup` text field and groups its list display the same way |
| Bulk-target promotion UI | `PromotionDialog.tsx` reworked from a single-select dropdown to a multi-select badge picker with a "Select all" toggle. Single-target selection keeps the original detailed dependency preview; multi-target selection skips the up-front preview (each target validates independently server-side) and shows a per-target success/error result list after promoting instead of a single toast |

**Migration**: `AddEnvironmentClientGroup` (both providers — `ClientGroup nvarchar(max) NULL` on `TenantEnvironments`), `has-pending-model-changes` clean on both. No other schema changes — API key/widget `EnvironmentId` columns already existed from Phase E.

**Verification**: full solution build 0 errors, full test suite passes except the same pre-existing `ContextWindowTests` failure. Frontend `tsc -b` 0 errors, production `vite build` succeeds, `eslint` shows no new issues in any touched file (one pre-existing `react-hooks/set-state-in-effect` warning in `EnvironmentManager.tsx`, already present from when that file was first created in Phase F, matches the same widespread pre-existing pattern across the codebase — not introduced by this pass). Deployed via `docker-compose.tei.yml + docker-compose.sqlserver.yml`; migration confirmed applied via container logs (`ALTER TABLE [TenantEnvironments] ADD [ClientGroup] nvarchar(max) NULL`), health check 200, `POST /api/admin/promotions/bulk` returns 401 (auth-gated as expected), admin portal 200.

**Deferred (unchanged from Phase F)**: draft/publish + promote UI for MCP Servers/Scheduled Tasks/Agent Groups (still Agent-only), version history/rollback UI, `GroupDetail.tsx`'s LLM environment dropdown (cross-tenant ambiguity), dashboard drift widget, step-by-step promotion progress feed, empty-state CTAs.

---

## [2026-07-31] Environment-based agent management — Phase F (admin UI, v1 slice)

Seventh phase: the first admin-portal UI for everything Phases A–I built server-side. This is the
answer to "why don't I see any option related to environment?" — a global environment switcher,
an Environments management page, environment-aware list filtering, draft/publish + promotion UI on
Agents, and environment tagging on tenant LLM configs and MCP credentials. **Scoped as a v1 slice**
(mirrors Phase E's approach): the highest-value, most-visible pieces are built and wired end-to-end;
lower-priority polish and the remaining 3 object types' draft/publish+promote UI are deferred (see
below), not silently skipped.

| Area | Change |
|------|--------|
| Environment switcher | New `useEnvironment()` context (`admin-portal/src/hooks/useEnvironment.tsx`) — loads the tenant's environments, resolves the active one via `?env=` URL slug → localStorage → lowest-rank (never defaults to Production on first visit), persists both. New `EnvironmentSwitcher` (Topbar, global) and shared `EnvironmentBadge` (color-coded: highest-rank = destructive/red, lowest-rank = green, mid = amber) components |
| Environment management | New `EnvironmentManager.tsx` page (`/settings/environments`, sidebar nav link) — full CRUD for `TenantEnvironmentEntity` (already had a backend `EnvironmentsController` from Phase A with no UI consumer until now) |
| `X-Environment` header | `admin-portal/src/api.ts`'s `authHeaders()` now sends `X-Environment` from the switcher's selection on every request — `TenantContextMiddleware` (Phase E) already honored this header for admin callers, it just had no UI to set it |
| List filtering | `AgentsController`/`McpServersController`/`SchedulerController`/`AgentGroupsController` list + paged endpoints gained an optional `?environmentId=` filter (matches the requested environment OR untagged rows, same pattern as Phase E's registry filter); wired into `AgentList.tsx`, `McpServerManager.tsx`, `ScheduledTasks.tsx`, `AgentGroups.tsx`, `CredentialManager.tsx` via a `useEffect` that re-queries whenever the switcher's environment changes |
| Draft/Publish UI | `AgentBuilder.tsx` gained Save Draft / Publish buttons + an "unpublished draft changes" banner, wired to Phase C's existing `PUT/GET/DELETE .../draft` + `POST .../publish` endpoints |
| Promotion UI | New shared `PromotionDialog.tsx` — dependency-preview (`GET /api/admin/promotions/preview`) + promote (`POST /api/admin/promotions`), with an extra confirmation toggle when the target is the top-of-pipeline (highest-rank) environment. Wired into `AgentBuilder.tsx` via a "Promote" button |
| LLM config / credential environment tagging | `UpsertLlmConfigDto`/`CreateNamedLlmConfigDto` (`ITenantGroupService.cs`) and `CreateCredentialDto`/`UpdateCredentialDto` (`CredentialsController.cs`) gained `EnvironmentId` — closes the CRUD gaps Phases G and I deliberately deferred. `TenantDetail.tsx`'s tenant-owned LLM config form and `CredentialManager.tsx`'s create form both gained an Environment dropdown + badge on each row |

**No schema changes** — every `EnvironmentId` column used here already existed from Phases A/E/G/I; this phase only added DTO fields, controller query params, and frontend UI. No migration needed.

**Deferred (documented, not silently skipped)**:
- Draft/Publish + Promote UI for MCP Servers, Scheduled Tasks, and Agent Groups (same pattern as Agents — `PromotionDialog` and the draft-status/banner logic are already generic/reusable, just not wired into those 3 components' pages yet).
- Version history + rollback panel UI (`GET /api/admin/promotions/history` + `/diff`, `POST /api/admin/promotions/rollback` — all already exist server-side from Phase D, just no UI list/diff view yet).
- `GroupDetail.tsx`'s LLM config environment dropdown — deliberately **not** built: a Group is cross-tenant, and `GroupLlmConfigEntity.EnvironmentId` is a raw numeric FK scoped to ONE tenant's `TenantEnvironmentEntity` row (confirmed against `LlmConfigResolver.ResolveGroupConfigByNameAsync`'s existing Phase G matching logic) — there's no single "which tenant's environment list" to source the dropdown from for a multi-tenant group without a larger design decision. The backend field/plumbing exists (`MapLlmConfig` returns `EnvironmentId`); only the ambiguous UI piece is deferred.
- `ApiKeyManager.tsx` / `WidgetEditor.tsx` environment tagging (Phase E's own deferred item) — no DTO/UI changes yet.
- Dashboard drift widget (draft/promotion-lag counts), extra step-by-step promotion progress feed (beyond the toast summary), empty-state CTAs on environment-filtered lists — UX polish items from the original plan (steps 27b–27d), not required for the core feature to function.

**Verification**: full solution build 0 errors, full test suite passes except the same pre-existing `ContextWindowTests` failure. Frontend: `tsc -b` 0 errors, production `vite build` succeeds (2745 modules). Deployed via `docker-compose.tei.yml + docker-compose.sqlserver.yml`; health check 200, `/api/admin/environments` and `/api/admin/promotions/preview` both 401 (auth-gated as expected), admin portal itself 200.

**Tooling note**: `admin-portal`'s `node_modules` has broken `bin/` folders for `typescript`/`eslint`/`vite` (present in `package.json` but missing on disk) — verified via direct entry-point invocation instead of `npx`/`npm run`; see `/memories/repo/sqlserver-migration.md` for the exact workaround commands.

---

## [2026-07-31] Environment-based agent management — Phase I (environment-scoped MCP credentials)

Seventh phase: MCP credential vault secrets are now resolved per-environment, mirroring Phase G's
LLM API key approach. Promoting an MCP server Dev→Staging→Production never carries a Dev credential
value into Prod — resolution prefers a row tagged to the caller's own environment, falls back to an
untagged row (pre-Phase-I data), and never falls back to a *different* tagged environment's row.
Promotion is hard-blocked if the target environment has no matching credential configured.

| Area | Change |
|------|--------|
| Schema | Nullable `EnvironmentId` (FK, Restrict) added to `McpCredentialEntity`. Unique index changed from `(TenantId, Name)` to `(TenantId, Name, EnvironmentId)` |
| Resolver | `ICredentialResolver.ResolveAsync` gains a **required** `environmentId` parameter (same self-auditing rationale as Phase G — a missed call site is a compile error). Cache key now includes the environment dimension (`cred:{tenantId}:{name}:{environmentId}`). Query logic: exact `(TenantId, Name, EnvironmentId)` match tried first when `environmentId > 0`, falling back to an untagged (`EnvironmentId == null`) row — never a different tagged environment's row |
| Call sites fixed | All 3 real call sites — `RemoteA2AAgent.cs` (A2A secret ref resolution, uses the agent's own `TenantContext.EnvironmentId`), `AgentsController.cs` (MCP-probe endpoint, same), `McpConnectionManager.cs` (`CreateClientAsync`, uses `fallbackTenant?.EnvironmentId ?? 0`) — plus 13 test call sites (10 in `CredentialResolverTests.cs`, 3 NSubstitute mock setups in `RemoteA2AAgentTests.cs`), all passing `0`/wildcard or `Arg.Any<int>()` where no specific environment is under test |
| Promotion integration | `McpServerPromotionDependencyResolver` now overrides `GetBlockingSecretDependenciesAsync`: looks up the `TenantMcpServerEntity` by `(TenantId, LogicalId)` and, if `DefaultCredentialRef` is set, returns it as a `BlockingSecretDependency("McpCredential", ...)`. `PromotionOrchestrationService.BuildClosureAsync`'s blocking-secret switch gained a `"McpCredential"` case (previously only `"LlmConfig"` was handled, `_ => true` unknown-kind default) — checks `McpCredentials` for a row tagged to the target environment or untagged |
| Confirmed pre-existing scope decision (not a new gap) | `ApiKeyCredentialMappingsJson` and user-group credential mappings are excluded from `McpServerSnapshot` entirely (Phase B decision, documented in `PromotableSnapshotDtos.cs`) — they reference tenant-specific `PlatformApiKeyId`/`UserGroup` rows that aren't portable across environments. Promotion of MCP servers today doesn't touch these mappings at all, so no additional translation work was needed for Phase I |

**Migrations**: `AddMcpCredentialEnvironment` in both providers, `has-pending-model-changes` clean on both. Full build 0 errors, full test suite passes except the same pre-existing `ContextWindowTests` failure. Deployed; migration confirmed applied via container logs, health check 200.

**Deferred**: CRUD/controller support for admins to actually *set* an `EnvironmentId` when creating/updating an MCP credential — no admin UI to use it yet (Phase F), left for that phase alongside the environment dropdown it would need.

---

## [2026-07-31] Environment-based agent management — Phase G (environment-scoped LLM API keys)

Sixth phase: an agent's LLM API key is now resolved per-environment. Promoting an agent Dev→Staging→
Production automatically picks up *that environment's own* key — never carries a Dev key into Prod —
and promotion is hard-blocked if the target environment has no key configured for a referenced named
config, rather than silently reusing the wrong one.

| Area | Change |
|------|--------|
| Schema | Nullable `EnvironmentId` (FK, Restrict) added to `TenantLlmConfigEntity` and `GroupLlmConfigEntity` only — `PlatformLlmConfigEntity` stays environment-agnostic (the platform-tier fallback). Unique indexes changed to `(TenantId, Name, EnvironmentId)` (still filtered `WHERE Name IS NOT NULL`) and `(GroupId, Name, EnvironmentId)` |
| Resolver | `ILlmConfigResolver.ResolveAsync` gains a **required** (not optional — deliberately, so a missed call site is a compile error, not a silent wrong-environment resolution) `environmentId` parameter. When a named config resolves by Id, the row's `Name` is used to re-look-up the effective row scoped to `(Name, environmentId)` — falling back to an *untagged* (`EnvironmentId == null`) row, never a different tagged environment's row, and finally to the original by-Id row as a last resort. Cache key now includes the environment dimension |
| Call sites fixed | All 9 real call sites (one more than the plan's original list of 8 — `AnthropicAgentRunner.cs` has a second call in its re-plan flow) plus 18 test call sites (14 in `LlmConfigResolverTests.cs`, 4 NSubstitute mock setups in `AnthropicAgentRunnerTests.cs`) updated to pass the caller's own resolved `TenantContext.EnvironmentId` (or `0`/wildcard where no environment context exists yet — `AgentSetupAssistant`'s agent-authoring flow, `LlmRuleExtractor`'s platform-baseline branch) |
| Promotion integration | `IPromotionDependencyResolver` gained a `GetBlockingSecretDependenciesAsync` method (default-empty via a C# default interface method, so only `AgentPromotionDependencyResolver` needed a real implementation) — an Agent's `LlmConfigId` resolves to its `Name`, and `PromotionOrchestrationService` hard-blocks promotion if no row (tagged-to-target or untagged) exists for that name in the destination environment |

**Migrations**: `AddLlmConfigEnvironment` in both providers, `has-pending-model-changes` clean on both. Full build 0 errors, full test suite passes except the same pre-existing `ContextWindowTests` failure (all 269 TenantAdmin tests, including the 14 updated resolver tests, pass). Deployed; migration confirmed applied, health check 200.

**Deferred**: CRUD/controller support for admins to actually *set* an `EnvironmentId` when creating/updating a named LLM config (`LlmConfigController`/`GroupsController`'s `UpsertLlmConfigDto`/`CreateNamedLlmConfigDto`) — there's no admin UI to use it yet (Phase F), so this is left for that phase alongside the environment dropdown it would need.

---

## [2026-07-30] Environment-based agent management — Phase E (runtime environment routing, partial)

Fifth phase: makes `TenantContext.EnvironmentId` a real, correctly-resolved per-request value, and
threads it through the highest-value read paths. **Scoped down from the full plan** given how many
call sites this phase touches platform-wide — this pass covers resolution + the core agent
invocation/MCP-credential paths; a few lower-priority call sites are explicitly deferred (see below).

| Area | Change |
|------|--------|
| `TenantContext` | New `EnvironmentId` property (mirrors the existing `CurrentSiteId` pattern) + `WithEnvironment(int)` copy method; `WithSession`/`WithPreferredUserGroup` updated to preserve it |
| Schema | Nullable `EnvironmentId` (FK, Restrict) added to `PlatformApiKeyEntity` and `WidgetConfigEntity` |
| Resolution | `TenantContextMiddleware`: API-key path uses the key's own tagged environment, falling back to the tenant's `IsDefault` environment if untagged; JWT/SSO path honors an admin-only `X-Environment` header, otherwise falls back to `IsDefault`. Resolved via a direct EF query (not `IEnvironmentService`, to avoid a circular `Diva.Infrastructure` → `Diva.TenantAdmin` project reference) |
| Agent registry | `IReadableAgentRegistry.GetAgentsForTenantAsync`/`GetByIdAsync`/`FindBestMatchAsync` gain an **optional** `environmentId = 0` parameter (0 = wildcard/no filter — fully backward compatible with every existing caller); `DynamicAgentRegistry` applies the filter when non-zero, matching rows with a null `EnvironmentId` too (not-yet-backfilled safety net) |
| Wired call sites | `AgentContextStage`, `CapabilityMatchStage` (supervisor pipeline), `DelegationAgentResolver.ExecuteAgentAsync` (peer-agent delegation execution), `McpCredentialSelector.ResolveSharedBindingsAsync` (MCP server/credential resolution — now filters `TenantMcpServers` by the caller's environment) |

**Deliberately deferred in this pass** (documented gap, not an oversight): `AgentsController`'s own direct `_registry.GetByIdAsync` call in the invoke/stream path (left unscoped since its `agent` is resolved via a separate, not-yet-environment-aware `ResolveAgentAsync` helper — threading environment filtering into only one of the two lookups would create an inconsistency); Scheduler task executor's agent lookup; Widget init's `EnvironmentId` propagation into the session; `SessionTrace` recording which environment served a request; `AgentGroupService`'s restriction-map (`AgentIdsJson`) environment scoping. These are lower-risk, self-contained follow-ups — same additive pattern, smaller blast radius each.

**Migrations**: `AddApiKeyWidgetEnvironment` in both providers, `has-pending-model-changes` clean on both. Full build 0 errors, full test suite passes except the same pre-existing `ContextWindowTests` failure. Deployed; migration confirmed applied, health check 200.

---

## [2026-07-30] Environment-based agent management — Phase D (dependency-aware promotion engine)

Fourth phase: promotes a promotable object (and everything it needs to function) from one
environment to a strictly-higher-ranked one within the same tenant, plus rollback to any earlier
recorded version. Builds directly on Phase B's ledger/serializers and Phase A's environments —
no new schema on the 4 promotable entity types themselves.

| Area | Change |
|------|--------|
| New entity | `PromotionRunEntity` — audit record per promotion action: root object, from/to environment, `PromotedVersionsJson` (every object actually promoted or skipped in the run, dependencies included) |
| Dependency resolution | `IPromotionDependencyResolver` + 4 implementations. **Cascade dependencies** (auto-promoted alongside): Agent → its MCP server refs (`McpServerRefsJson`, name-resolved) + delegate agents (`DelegateAgentIdsJson`). **Forward dependencies** (validated, never auto-cascaded, per the "auto-cascade only flows toward dependencies, never dependents" rule): ScheduledTask → its Agent; AgentGroup → its member agents. MCP Server is a leaf (no dependencies either direction) |
| Orchestration | `IPromotionOrchestrationService`/`PromotionOrchestrationService` — `PreviewAsync` (dry-run: rank check + full dependency closure, for the admin confirmation dialog), `PromoteAsync` (rank guard, BFS closure computation, forward-dependency validation with an actionable "X does not exist in {target} yet" error, leaves-first materialization via Phase B's `MaterializeAsync`, content-hash idempotent skip against the target's *own* current live version, ledger recording with `Source="promotion"` + `PromotedFromVersionId` linkage, one `PromotionRunEntity` per run), `RollbackAsync` (re-materializes an older recorded version, `Source="rollback"`) |
| API | New `PromotionsController` — `GET /api/admin/promotions/preview`, `POST /api/admin/promotions`, `GET /api/admin/promotions/history`, `GET /api/admin/promotions/diff`, `POST /api/admin/promotions/rollback` |

**Migrations**: `AddPromotionRuns` in both providers, `has-pending-model-changes` clean on both. Full build 0 errors, full test suite passes except the same pre-existing `ContextWindowTests` failure. Deployed; new endpoints verified reachable (401).

---

## [2026-07-30] Environment-based agent management — Phase C (draft isolation, additive)

Third phase of the environment-based agent management effort: edits to any of the 4 promotable
object types can now be staged as a draft and applied via an explicit Publish action, without
touching the live row until publish — closing the "editing must not affect live traffic" gap.
Implemented **additively**: the existing `PUT /{id}` endpoints on all 4 controllers are completely
unchanged and keep applying immediately as before; the new draft/publish endpoints are a separate,
opt-in path. No frontend changes in this pass — wiring the admin UI to actually use Save-Draft/Publish
instead of the existing single Save button is a deliberate follow-up, not done here.

| Area | Change |
|------|--------|
| New entity | `EntityDraftEntity` (`Id`, `TenantId`, `ObjectType`, `LogicalId`, `EnvironmentId`, `DraftJson`, `UpdatedAt`, `UpdatedBy`) — one row per (TenantId, ObjectType, LogicalId, EnvironmentId), content-agnostic (DraftJson is whatever DTO shape that object type's own PUT already accepts) |
| Service | `IEntityDraftService`/`EntityDraftService` (`Diva.Core.Models` / `Diva.Infrastructure.Promotion`) — `GetDraftAsync`/`SaveDraftAsync`/`ClearDraftAsync`, singleton-safe, no knowledge of any specific object type |
| New endpoints (all 4 controllers) | `PUT {route}/{id}/draft` (save, never touches live row), `GET {route}/{id}/draft` (fetch pending draft for the edit UI), `DELETE {route}/{id}/draft` (discard), `POST {route}/{id}/publish` (applies the draft via the SAME update logic the existing PUT already uses, then records a ledger version via the matching Phase B `IPromotableSnapshotSerializer` with `Source="publish"`, then clears the draft) |
| Reused, not duplicated | `AgentsController`'s field-copy logic extracted into `ApplyAgentUpdate` (shared by `Update` and `Publish`); `McpServersController`'s into `ApplyMcpServerUpdateAsync`; `SchedulerController`/`AgentGroupsController` call their existing `IScheduledTaskService.UpdateAsync`/`IAgentGroupService.UpdateAsync` directly from `Publish` — no new update logic invented anywhere |

**Migrations**: `AddEntityDrafts` in both `src/Diva.Infrastructure` (SQLite) and `src/Diva.Infrastructure.SqlServer` (SQL Server), verified `has-pending-model-changes` clean on both providers. Full solution build 0 errors, full test suite passes except the same pre-existing (confirmed unrelated) `ContextWindowTests` failure. Deployed; new endpoints verified reachable (401, auth-gated, not 404/500).

---

## [2026-07-30] Environment-based agent management — Phase B (generic versioning ledger)

Second phase of the environment-based agent management effort: an append-only, content-hash-deduped
version history engine shared by all 4 promotable object types (agents, MCP servers, scheduled tasks,
agent groups), plus per-type snapshot serializers that produce/consume the portable JSON stored in
each version. This phase is backend infrastructure only — nothing calls it yet (Phase C's draft/publish
flow and Phase D's promotion flow are the first real callers); verified via build/test/migration checks
and a clean deploy, not a new UI or controller.

| Area | Change |
|------|--------|
| New entities | `PromotableObjectEntity` (`LogicalId` PK, `TenantId`, `ObjectType`, `Name`, `OriginEnvironmentId`, `CreatedAt`) — one row per logical object regardless of how many per-environment copies exist. `PromotableVersionEntity` (append-only, `Version` monotonic per `LogicalId`, `ContentHash` SHA-256, `SnapshotJson`, `Source`, `PromotedFromVersionId` self-ref). `EnvironmentDeploymentEntity` (one row per `(LogicalId, EnvironmentId)`, tracks `LiveVersionId`) |
| Schema | All new FKs (`PromotableVersionEntity.LogicalId`, `EnvironmentDeploymentEntity.LogicalId`/`LiveVersionId`, `PromotableVersionEntity.PromotedFromVersionId` self-ref) use `DeleteBehavior.Restrict` uniformly — avoids SQL Server's "multiple cascade paths" error (the object is reachable via 2 paths) and keeps the ledger's history from being silently mass-deleted as a side effect. Unique indexes on `(LogicalId, Version)` and `(LogicalId, EnvironmentId)` |
| Service | `IPromotionLedgerService`/`PromotionLedgerService` (`Diva.Core.Models` / `Diva.Infrastructure.Promotion`) — `RecordVersionAsync` upserts the object row, hashes the snapshot, and only appends a new version if the hash differs from the latest on record (dedup — republishing unchanged content is a no-op), then updates the environment's live-version pointer. Also `GetHistoryAsync`, `GetVersionAsync`, `DiffVersionsAsync` (generic recursive JSON field-diff via `SnapshotJsonDiffer`, arrays compared atomically) |
| Serializers | `IPromotableSnapshotSerializer` + 4 implementations, DI-registered as `IEnumerable<IPromotableSnapshotSerializer>` for future dispatch-by-`ObjectType`: `AgentSnapshotSerializer` (thin wrapper reusing the existing `IAgentExportService` bundle/import logic rather than reimplementing it), `McpServerSnapshotSerializer`, `ScheduledTaskSnapshotSerializer`, `AgentGroupSnapshotSerializer` (built directly against their entities, stripping non-portable fields — e.g. `ApiKeyCredentialMappingsJson`, `RunAsUserId`, `AllowedUserIdsJson` — and resolving cross-entity references by Name, mirroring `AgentExportService`'s delegate-name resolution pattern) |
| Known limitation | `MaterializeAsync` matches the target row by `Name` within the tenant only (not yet environment-scoped) — safe today since every tenant still has exactly one (Phase A backfilled) environment; Phase D/E's promotion orchestration is expected to refine this to match by `(EnvironmentId, LogicalId)` once environment-scoped routing ships |

**Migrations**: `AddPromotionLedger` in both `src/Diva.Infrastructure` (SQLite) and `src/Diva.Infrastructure.SqlServer` (SQL Server), verified `has-pending-model-changes` clean on both providers. Full solution build 0 errors, full test suite passes except the pre-existing (unrelated, confirmed via `git worktree` baseline comparison during Phase A) `ContextWindowTests.RunAsync_CallsMaybeCompactAnthropicBeforeLlmCall` failure. Deployed and verified the migration applies cleanly with no startup errors (new tables start empty — no backfill needed).

---

## [2026-07-30] Environment-based agent management — Phase A foundation (environments & logical identity)

First phase of a larger effort to let each tenant define its own environment pipeline (e.g.
Production/Staging/Dev) so agents, MCP servers, scheduled tasks, and agent groups can eventually be
versioned and promoted between environments at runtime. This phase lays the schema foundation only —
no promotion engine, draft isolation, or runtime routing yet (those are later phases).

| Area | Change |
|------|--------|
| New entity | `TenantEnvironmentEntity` (`Id`, `TenantId`, `Slug`, `DisplayName`, `Rank`, `IsDefault`, `CreatedAt`) — a tenant-scoped, ordered list of deployment environments |
| Schema | Nullable `EnvironmentId` (FK) + `LogicalId` (GUID) added to the 4 "promotable" entity types: `AgentDefinitionEntity`, `TenantMcpServerEntity`, `ScheduledTaskEntity`, `AgentGroupEntity`. Non-unique composite index on `(TenantId, EnvironmentId, LogicalId)` on each, preparing for later enforcement |
| Backfill | Idempotent startup routine (`Program.cs`, both SQLite and SQL Server, plain EF LINQ — no raw SQL) seeds exactly one "Production" (`IsDefault=true`) environment per existing tenant and tags all pre-existing rows of the 4 entity types with it, so this ships with zero manual migration steps and no behavior change for any existing tenant |
| API | New `EnvironmentsController` (`GET/POST/PUT/DELETE /api/admin/environments`) + `IEnvironmentService`, with basic guardrails (unique slug per tenant, must always have one default environment, can't delete an environment still referenced by other rows) |
| Frontend | `TenantEnvironment`/`EnvironmentRequest` types + CRUD methods in `api.ts` (API surface only — no admin UI page yet, that's a later phase) |

**Migrations**: `AddEnvironments` in both `src/Diva.Infrastructure` (SQLite) and `src/Diva.Infrastructure.SqlServer` (SQL Server), verified `has-pending-model-changes` clean on both providers. Verified end-to-end against an existing populated SQL Server database (backfill correctly tagged 18 agents, 10 MCP servers, 15 scheduled tasks, and 12 agent groups on first startup after migrating).

---


## [2026-07-30] Server-side pagination/search: Rule Packs + Optimization Suggestions/Runs

Continuation of the admin-portal pagination retrofit (Track 1 Phase 3): converts `RulePackManager.tsx`
from client-side slicing to real server-side paging, and wires up `AgentOptimizationSuggestions.tsx`
+ `AgentOptimizer.tsx` to server-side filtering/pagination (the status/type/runId/minConfidence
filters already existed end-to-end but were never actually passed by the frontend until now).

| Area | Change |
|------|--------|
| Rule Packs | `RulePackManager.tsx` fetched ALL tenant packs + ALL starter packs, merged, filtered, and `Array.slice`'d client-side for "pages". New `GET /api/admin/rule-packs/paged` merges both (cached) sources server-side via `.Concat()`, applies `search`/`status`/`type`, then paginates with `EnumerablePagingExtensions.ToPagedResult`. Dual endpoint — `GET /api/admin/rule-packs` kept unbounded for `BusinessRuleEditor.tsx`/`BusinessRules.tsx` pack-selector dropdowns |
| Optimization Suggestions | `GetSuggestionsAsync`/`getOptimizationSuggestions` converted in place (only 2 callers, both compatible with the new shape) from `List<T>` → `PagedResult<T>`; now accepts `page`/`pageSize` alongside the pre-existing `status`/`type`/`runId`/`minConfidence` filters, which the frontend now actually sends server-side instead of filtering an unbounded fetch client-side. Backend paginates the raw entity query first, then maps only the current page (mirrors the MCP Credentials decrypt-only-current-page pattern) |
| Optimization Runs | Dual endpoint — kept `GetRunsAsync`/`getOptimizationRuns` unbounded (used by `AgentOptimizationSuggestions.tsx`'s "Run" filter dropdown, and by an existing unit test that calls it directly), added new `GetRunsPagedAsync`/`getOptimizationRunsPaged` + `GET .../optimize/runs/paged` for `AgentOptimizer.tsx`'s Run History table |
| Few-Shot Examples | Deliberately left unpaginated — `AgentFewShotExamples.tsx` uses a manual drag/move-up-down reorder that requires the full list in memory; pagination would break the reorder UX. Per plan guidance, this list is expected to stay small |

**Files**: `src/Diva.Host/Controllers/RulePackController.cs`, `src/Diva.Host/Controllers/AgentOptimizationController.cs`, `src/Diva.Infrastructure/Optimization/{IAgentOptimizationService,AgentOptimizationService}.cs`, `admin-portal/src/api.ts`, `admin-portal/src/components/{RulePackManager,AgentOptimizer,AgentOptimizationSuggestions}.tsx`.

---

## [2026-07-30] Server-side pagination/search across 16 admin-portal list pages + Scheduled Task full-page editor

Two related pieces of work: a reusable server-side pagination + search framework retrofitted across
16 admin-portal list pages (previously unbounded `GET` endpoints returning full arrays), and a
conversion of the Scheduled Task create/edit/clone flow from a centered modal `Dialog` to a
full-page route matching the existing `AgentBuilder`/`SsoConfigEditor` house style.

### Pagination framework (new shared infrastructure)

`PagedResult<T>` (already used by Sessions) is now the standard envelope for every list endpoint in
scope. Two backend paging helpers cover both query shapes actually used across controllers — a true
EF `IQueryable` path and a sync in-memory path for lists that merge/decrypt/project before paging —
plus one frontend hook + toolbar pair that every retrofitted page now shares.

| File | Change |
|------|--------|
| `src/Diva.Core/Models/PagedResult.cs` | Relocated from `Session/SessionDtos.cs` (was oddly namespaced under `Diva.Core.Models.Session` despite being fully generic) |
| `src/Diva.Infrastructure/Extensions/QueryablePagingExtensions.cs` | `ToPagedResultAsync<T>(page, pageSize, ct)` — EF `CountAsync` + `Skip/Take`, mirrors `SessionsController`'s original logic |
| `src/Diva.Core/Extensions/EnumerablePagingExtensions.cs` | `ToPagedResult<T>(page, pageSize)` (sync, in-memory) + `MapItems<T,TDto>(selector)` — for controllers that merge/decrypt/project before paging (no EF dependency, keeps `Diva.Core` dependency-free) |
| `admin-portal/src/hooks/usePagedList.ts` | Generic `usePagedList<T, TParams>(fetchFn, initialParams)` → `{ result, loading, error, params, update, updateDebounced, setPage, reload }`; `updateDebounced` for free-text search (~300ms) |
| `admin-portal/src/components/ui/list-toolbar.tsx` | `ListToolbar` (search + filter slot + page-size select) + `ListPagination` (Prev/Next + "Page X of Y · N total") |
| `admin-portal/src/components/SessionBrowser.tsx` | Refactored to consume the new hook/toolbar (proves the pattern; was already the only page with real server-side paging) |

**Dual-endpoint pattern**: any list method also used by an unrelated dropdown/selector keeps its
original unbounded route/method untouched and gets a new `xxxPaged` method + `/paged` sub-route
instead — never a breaking change to a shared method's shape. Endpoints with exactly one caller were
converted to accept `search`/`page`/`pageSize` in place.

### Retrofitted pages (16 total)

| Entity | Component | Endpoint(s) | Notes |
|---|---|---|---|
| Agents | `AgentList.tsx` | `GET /api/agents/paged` (new; unbounded kept for 9 dropdown callers) | |
| Business Rules | `BusinessRules.tsx` | `GET /api/admin/business-rules` (in place) | |
| Prompt Overrides | `PromptEditor.tsx` | `GET /api/admin/prompt-overrides` (in place) | |
| User Profiles | `UserProfiles.tsx` | `GET /api/admin/user-profiles/paged` (new) | |
| Pending/Learned Rules | `PendingRules.tsx` | `GET /api/learned-rules` (in place) | Also fixed a latent bug: `SuggestedRule` never carried a real `Id` (frontend synthesized `i+1`), which pagination would have turned into wrong-rule approve/reject across pages |
| MCP Credentials | `CredentialManager.tsx` | `GET /api/admin/credentials/paged` (new) | Search applied before the decrypt step — only current-page rows are decrypted |
| Platform API Keys | `ApiKeyManager.tsx` | `GET /api/admin/api-keys/paged` (new) | |
| Shared MCP Servers | `McpServerManager.tsx` | `GET /api/admin/mcp-servers/paged` (new) | |
| User Groups | `UserGroups.tsx` | `GET /api/user-groups/paged` (new) | |
| Widget Configs | `WidgetManager.tsx` | `GET /api/admin/widgets/paged` (new) | Card-grid layout, not a Table |
| SSO Configs | `SsoConfig.tsx` | `GET /api/admin/sso-configs/paged` (new) | `tenantId` prop-change reactivity handled via `key={tenantId}` at the `TenantDetail.tsx` call site |
| Scheduled Tasks (list) | `ScheduledTasks.tsx` | `GET /api/schedules` (in place) | "Export All"/import-conflict-check now fetch the full set via a one-off `pageSize:10000` call rather than relying on the current page |
| Scheduled Tasks (run history) | `ScheduledTasks.tsx` (`RunHistorySheet`) | `GET /api/schedules/{id}/runs` (in place, `limit` → `page`/`pageSize`) | Reactivity to the selected task via `key={runsTask?.id}` remount |
| Scheduler Feedback | `SchedulerFeedbackReview.tsx` | `GET /api/scheduler-feedback` (in place) | |
| Local Users | `LocalUsersPanel.tsx` | `GET /api/auth/local-users` (in place) | `tenantId` prop-change reactivity via `key={tenantId}` at `TenantDetail.tsx` |
| Agent Access Groups | `AgentGroups.tsx` | `GET /api/agent-groups/paged` (new) | Verified no route collision between literal `/paged` and unconstrained-string `{id}` — ASP.NET Core ranks literal segments above parameter segments unconditionally |

### Scheduled Task editor: modal → full-page route

The Scheduled Task create/edit/clone form was a centered `Dialog` modal (`TaskDialog`, ~350 lines
inline in `ScheduledTasks.tsx`); converted to a dedicated full-page route matching the established
`AgentBuilder`/`SsoConfigEditor` pattern (`ArrowLeft`-back header, Card-sectioned form, bottom-right
Cancel/Save bar) instead of inventing a new layout style.

| File | Change |
|------|--------|
| `admin-portal/src/components/ScheduleTaskEditor.tsx` (new) | Full-page form ported from `TaskDialog`, reorganized into Cards (Agent & Name / Timing / Prompt / Run As User / Notifications). Fetches by `id` directly via `api.getSchedule` (deep-link/refresh-safe, unlike the old dialog's in-memory `source` prop) |
| `admin-portal/src/lib/scheduleConstants.ts` (new) | `TIMEZONES`/`DAY_NAMES` extracted out of `ScheduledTasks.tsx` — a component file exporting plain constants breaks Vite Fast Refresh (`react-refresh/only-export-components`) |
| `admin-portal/src/components/ScheduledTasks.tsx` | Removed the `TaskDialog` modal entirely; `openCreate`/`openEdit`/`openClone` now `navigate()` to the new routes |
| `admin-portal/src/App.tsx` | New routes: `schedules/new`, `schedules/:id/edit`, `schedules/:id/clone` (clone mode passed as a boolean prop on the `Route` element, not parsed from the URL or `location.state`) |

---


## [2026-07-15] Responsive/mobile UI pass + chat content overflow fix + session token accounting (cache + sub-agent roll-up)

Three areas: a responsive/mobile-friendly pass across the admin portal (chat-first), a fix for wide
chat content (tables/charts/SQL) overflowing and clipping instead of scrolling, and corrected token
accounting so session totals reflect true input (fresh + cached) and roll up delegated sub-agents.

### Responsive & mobile-friendly UI

Chat is the primary surface: the header controls (credential-group / LLM-config / model / Detailed)
now collapse into a settings **popover** on mobile via `useIsMobile`, and the root height uses
dynamic viewport units so the on-screen keyboard/address-bar no longer clips the panel. Shell padding
is responsive, form grids stack on narrow screens, raw tables scroll, and page-header button rows wrap.
The embeddable widget stack was also hardened (dvh, responsive theme grids, capped launcher iframe height).

| File | Change |
|------|--------|
| `admin-portal/src/components/AgentChat.tsx` | Header controls → mobile settings popover (`SlidersHorizontal`); selects `w-full md:w-XX`; root `h-[calc(100dvh-7rem)] md:8rem`; message column `min-w-0`, bubble `min-w-0 max-w-full` |
| `admin-portal/src/components/layout/root-layout.tsx` | `<main>` padding `p-4 md:p-6` |
| `admin-portal/src/components/UserProfiles.tsx` | Sheet `w-full sm:max-w-[480px]` |
| `admin-portal/src/widget/WidgetChat.tsx`, `WidgetApp.tsx` | `100vh` → `100dvh` |
| `src/Diva.Host/wwwroot/widget.js` | Launcher iframe height capped `min(600, innerHeight-106)` (init + resize) |
| `admin-portal/src/components/WidgetEditor.tsx` | Theme/form grids `grid-cols-1 sm:grid-cols-2` / color pickers `grid-cols-2 sm:grid-cols-3` |
| `admin-portal/src/components/*` (config/rules/group/dialog forms, 21 files) | `grid grid-cols-2/3` → responsive `grid-cols-1 sm:grid-cols-2` / `grid-cols-2 sm:grid-cols-3` |
| `admin-portal/src/components/AgentBuilder.tsx`, `SsoConfigEditor.tsx` | Raw `<table>` wrapped in `overflow-x-auto` + `min-w-*` |
| `admin-portal/src/components/AgentList.tsx`, `AgentBuilder.tsx`, `WidgetManager.tsx` | Page-header button rows `flex-wrap gap-3` |

### Chat response content no longer clips — horizontal scroll works

Root cause was a flexbox `min-width` issue plus the Radix `ScrollArea` viewport wrapping content in a
`display:table; min-width:100%` element that grew to content width, defeating `max-w-full` and clipping
wide tables/charts/SQL with no scrollbar. Fixed by constraining the message column/bubble (`min-w-0`)
and forcing the ScrollArea viewport's inner wrapper to `block` so inner `overflow-x-auto` containers
(shadcn table, SQL block, chart `ResponsiveContainer`) finally scroll within the chat width.

| File | Change |
|------|--------|
| `admin-portal/src/components/ui/scroll-area.tsx` | Viewport `[&>div]:!block [&>div]:!min-w-0` (constrains Radix inner wrapper to viewport width) |
| `admin-portal/src/components/chat/MarkdownMessage.tsx` | Root `min-w-0 max-w-full break-words` |
| `admin-portal/src/components/AgentChat.tsx` | Message column `min-w-0`, bubble `min-w-0 max-w-full` |

### Session token accounting — effective input (fresh + cached) + sub-agent roll-up

Displayed input tokens counted only the fresh (non-cached) portion, so with Anthropic prompt caching
the bulk (system prompt, tool schemas, prior tool results) was billed as `cache_read_input_tokens` and
never shown — making input look tiny. Session aggregates also omitted cache tokens entirely and did not
include delegated child-agent sessions. Now the session aggregate carries cache columns, the UI shows
**effective input = fresh + cached**, and the detail endpoint walks the `ParentSessionId` delegation tree
to report an **"Incl. sub-agents"** roll-up while preserving each child session's own per-agent totals.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Data/Entities/SessionTraceEntities.cs` | `TraceSessionEntity.TotalCacheReadTokens` / `TotalCacheCreationTokens` |
| `src/Diva.Infrastructure/Sessions/SessionTraceWriter.cs` | Roll cache tokens into the session aggregate per turn |
| `src/Diva.Host/Program.cs` | Idempotent trace-DB cache-column repair (SQLite + SQL Server; `EnsureCreated` path) |
| `src/Diva.Core/Models/Session/SessionDtos.cs` | Cache fields on `SessionSummary`/`TurnSummary`; `Rollup*Tokens` + `SubAgentSessionCount` on `SessionDetail` |
| `src/Diva.Host/Controllers/SessionsController.cs` | Map cache columns; BFS `ParentSessionId` descendant roll-up (cycle-guarded) |
| `admin-portal/src/api.ts` | Cache + roll-up fields on session types |
| `admin-portal/src/components/SessionDetail.tsx` | Effective-input Tokens row + "Incl. sub-agents" roll-up row |
| `admin-portal/src/components/SessionBrowser.tsx` | "Tokens In" shows effective input with fresh/cached tooltip |

> Note: existing trace rows show `cached = 0` (the split was not recorded before); cache/roll-up populate
> going forward. OpenAI-compatible **streaming** still reports 0 tokens (ME.AI 10.4.1 SDK limitation).

---

## [2026-07-13] LLM blank-key resolution fix + editable API-key group grants + child-agent credential-group propagation

Three related fixes: a resolver bug that could send an empty `x-api-key` to the LLM provider,
editable access-group grants on platform API keys, and propagation of a delegating agent's
effective credential group to its child agents.

### LLM config resolution — blank override must not clobber inherited key

A tenant/group `LlmConfig` row with a **blank** `ApiKey` (a partially-filled row) overwrote the
valid inherited platform key with an empty string, so the provider received an empty `x-api-key`
and rejected every call with `401 invalid x-api-key` — regardless of the (valid) platform key. The
resolver now treats blank/whitespace `ApiKey` and `Model` overrides as "inherit", and the runner
falls back to the configured key when the resolved key is blank (not only when null). LLM keys are
stored plaintext and passed verbatim (no decryption on this path), so this was never a decrypt
issue. Also added `LLM_DIRECT_API_KEY` to `.env` as the `TenantId=0` fallback (compose default was
literal `no-key`).

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/LlmConfigResolver.cs` | `Overlay` ignores blank/whitespace `ApiKey`/`Model` overrides (no clobber) |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | `resolvedApiKey` falls back to configured key when resolved key is null **or** blank |
| `tests/Diva.TenantAdmin.Tests/LlmConfigResolverTests.cs` | Regression tests: blank `ApiKey` keeps inherited platform key; blank `Model` inherits platform model |

### Editable API-key access-group grants (+ rotate fix)

Platform API keys were create-only, so a restricted-access-group agent could not be granted to an
existing invoke/readonly key, and `RotateAsync` silently dropped group grants. Added a full-replace
`UpdateAsync` (`PUT /api/admin/api-keys/{id}`) and preserved group grants on rotation.

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/IPlatformApiKeyService.cs` | `UpdateApiKeyRequest` record + `UpdateAsync` interface method |
| `src/Diva.Infrastructure/Auth/PlatformApiKeyService.cs` | `UpdateAsync` (name/scope/agent+group grants/expiry); `RotateAsync` now carries `AllowedGroupIdsJson` |
| `src/Diva.Host/Controllers/ApiKeysController.cs` | `PUT {id}` update endpoint + `UpdateApiKeyDto` |
| `admin-portal/src/api.ts`, `admin-portal/src/components/ApiKeyManager.tsx` | `updateApiKey` + inline edit UI with "Allowed Agent Groups" selector |

### Child-agent credential-group propagation

When a delegating agent resolves its shared-MCP servers to a single unambiguous user-group, it now
propagates that group to delegated child agents so they inherit the same credential rather than
independently re-resolving to a possibly different group.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/McpCredentialSelector.cs` | `ResolveSharedBindingsAsync` + `SharedBindingResult(Bindings, EffectiveUserGroupId)`; per-agent + per-API-key credential mapping |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Propagate effective credential user-group to child agents (when caller didn't pick one) |
| `tests/Diva.Agents.Tests/McpCredentialSelectorTests.cs` | Shared-binding + effective-group + agent-scoped tests |

---

## [2026-07-10] User Groups + per-group MCP credentials + chat credential-group picker + scheduled-task run-as-user

A tenant-scoped **User Groups** capability landed, wiring group membership into three places:
shared-MCP-server credential selection, agent access-group restrictions, and scheduled-task
execution identity. When a user belongs to more than one eligible group, the agent chat now lets
them explicitly choose which group's shared-MCP credentials to use for the session.

### User Groups (tenant-scoped membership)

Groups map users (by id/email) to a named group; membership drives credential selection and agent
access. Membership resolution is cached per-request.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Data/Entities/UserGroupEntities.cs` | `UserGroupEntity` + membership entities (`ITenantEntity`) |
| `src/Diva.Infrastructure/Data/Migrations/20260709221720_AddUserGroups.*` + `src/Diva.Infrastructure.SqlServer/Migrations/20260709221024_AddUserGroups.*` | Schema for both providers |
| `src/Diva.Core/Configuration/IUserGroupResolver.cs`, `src/Diva.Infrastructure/Auth/UserGroupMembershipCache.cs` | `GetGroupIdsForUserAsync` resolver + per-request cache |
| `src/Diva.TenantAdmin/Services/UserGroupService.cs`, `IUserGroupService.cs` | CRUD service |
| `src/Diva.Host/Controllers/UserGroupsController.cs` | REST API |
| `admin-portal/src/components/UserGroups.tsx`, `App.tsx`, `layout/app-sidebar.tsx` | Admin UI + routing |
| `tests/Diva.TenantAdmin.Tests/UserGroupServiceTests.cs` | Service tests |

### Chat credential-group picker (per-group shared-MCP credentials)

`McpCredentialSelector` now honors a caller-chosen `PreferredUserGroupId` in the user-group
credential tier (falling back to the existing lowest-`UserGroupId` default). The chat UI surfaces a
picker only when the caller belongs to >1 eligible group; the selection is **locked once the
session has started** and resets on clear. Passing an ineligible group id is a no-op (credential
resolution is pre-filtered to the caller's actual groups — no privilege escalation).

| File | Change |
|------|--------|
| `src/Diva.Core/Models/TenantContext.cs` | Transient `PreferredUserGroupId` + `WithPreferredUserGroup(...)` |
| `src/Diva.Infrastructure/LiteLLM/McpCredentialSelector.cs` | Honor preferred group; `GetSelectableGroupsAsync` (intersection of caller groups, credential-mapped groups, agent-allowed groups) |
| `src/Diva.TenantAdmin/Services/AgentGroupService.cs`, `IAgentGroupService.cs` | `GetAllowedUserGroupIdsForAgentAsync` |
| `src/Diva.Host/Controllers/AgentsController.cs` | `GET {id}/credential-groups`; apply `PreferredUserGroupId` in Invoke/InvokeStream |
| `admin-portal/src/api.ts`, `components/AgentChat.tsx` | `CredentialGroupOption` type, `streamAgent` param, locking group picker |
| `tests/Diva.Agents.Tests/McpCredentialSelectorTests.cs` | Preferred-group + selectable-group tests |

### Scheduled-task run-as-user

Scheduled tasks can run under a stored user profile so agent execution carries that user's tenant
identity and group membership (e.g. for per-group credential resolution).

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Data/Entities/ScheduledTaskEntity.cs` | Run-as-user fields |
| `src/Diva.Infrastructure/Data/Migrations/20260710062938_AddScheduledTaskRunAsUser.*` + `src/Diva.Infrastructure.SqlServer/Migrations/20260710063036_AddScheduledTaskRunAsUser.*` | Schema for both providers |
| `src/Diva.Infrastructure/Scheduler/ScheduledTaskService.cs`, `IScheduledTaskService.cs`, `SchedulerHostedService.cs` | Run-as-user execution flow |
| `src/Diva.Host/Controllers/SchedulerController.cs`, `admin-portal/src/components/ScheduledTasks.tsx` | API + UI wiring |
| `src/Diva.TenantAdmin/Services/UserProfileService.cs` | Profile lookup for run-as-user |

---

## [2026-07-09] Per-agent extended thinking + streaming block reconstruction + rich chat rendering

Three features landed together: per-agent extended ("chain-of-thought") thinking with automatic
adaptive-mode fallback for newer models, a rewrite of the Anthropic streaming assistant-turn
reconstruction that faithfully preserves thinking/tool_use block order, and a richer chat UI
(markdown, charts, SQL blocks, tool-result tables) plus configurable conversation starters.

### Extended thinking (per-agent, adaptive-aware)

Extended thinking is now a per-agent toggle with an optional token budget. Because newer models
(e.g. `claude-sonnet-5`) reject budget-based thinking, the strategy self-heals: on the
`"thinking.type.enabled" is not supported` error it switches to `adaptive` thinking + an
`output_config.effort` level mapped from the configured budget, and retries once. Interleaved
thinking (`interleaved-thinking-2025-05-14` beta) is enabled on thinking requests via a dedicated
beta client so non-thinking agents are never affected.

> Note: `claude-sonnet-5` adaptive/effort thinking returns an **encrypted, signature-only**
> thinking block — no plaintext reasoning is streamed. The block is still preserved (with its
> signature) in message history so follow-up tool-result turns are accepted; there is simply no
> plaintext to display for that model.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs` | New `EnableExtendedThinking` (bool?) + `ThinkingBudgetTokens` (int?) fields |
| `src/Diva.Infrastructure/Data/Migrations/20260708211559_AddAgentExtendedThinking.*` + `src/Diva.Infrastructure.SqlServer/Migrations/20260708211643_AddAgentExtendedThinking.*` | Schema for both providers |
| `src/Diva.Core/Configuration/AgentOptions.cs` | Global `ThinkingBudgetTokens` default (8000) |
| `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs` | `ApplyThinking` (budget vs adaptive + `UseInterleavedThinking`), `MapBudgetToEffort`, adaptive self-heal, sticky `SuppressThinkingForRun`, `LastThinkingText` reasoning fallback, `StripThinkingBlocks` |
| `src/Diva.Infrastructure/LiteLLM/AnthropicProvider.cs` | Dedicated `_betaClient` (interleaved-thinking beta header) selected only when `UseInterleavedThinking` is set |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs`, `ILlmProviderStrategy.cs` | Strategy wiring, per-agent thinking config, empty-response safety net |
| `src/Diva.Host/Controllers/AgentsController.cs`, `ConfigController.cs`, `src/Diva.Core/Models/AgentExport.cs`, `src/Diva.Infrastructure/AgentExport/AgentExportService.cs` | Field wiring through API + agent export/import |
| `admin-portal/src/api.ts`, `AgentBuilder.tsx` | Extended-thinking toggle + budget input in Agent Builder |

### Streaming assistant-turn reconstruction (ordered, signature-preserving)

The Anthropic streaming path previously flattened the assistant turn into a fixed
`[thinking, text, tool_use…]` shape using a single merged thinking block. That corrupts history
when a model interleaves **multiple** signed thinking blocks with tool calls (the SDK reports
`stopReason=end_turn` with an empty `ToolCalls` list on those turns). The loop now rebuilds
content blocks in exact stream order (`content_block_start → *_delta* → content_block_stop`),
preserving each thinking block with its own signature and each `tool_use` in position, and
reconstructs tool calls directly from `input_json_delta` events (with an SDK-`ToolCalls` fallback).

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/LiteLLM/AnthropicProviderStrategy.cs` | Ordered `FlushBlock` reconstruction of thinking/redacted/text/tool_use blocks; `stopReason` override to `tool_use` when tools are captured; diagnostic logging |

### Rich chat rendering + conversation starters

| File | Change |
|------|--------|
| `admin-portal/src/components/chat/` | New `MarkdownMessage`, `ChartRenderer`, `SqlBlock`, `ToolResultTable` renderers |
| `admin-portal/src/widget/markdown.ts`, `WidgetChat.tsx` | Markdown rendering in the embeddable widget |
| `admin-portal/src/components/AgentChat.tsx`, `AgentList.tsx`, `SessionBrowser.tsx`, `SessionToolCallCard.tsx`, `layout/topbar.tsx` | Chat + session UI enhancements |
| `AgentDefinitionEntity.cs` + `20260708182506_AddConversationStarters.*` (both providers) | `ConversationStartersJson` field + migrations |

---

## [2026-07-07] External SQL Server support + scheduler leader election

Two deployment features landed together: full external SQL Server support (multi-provider
migrations + one-time data migration tool) and config-based scheduler leader election so the
platform can be scaled to multiple API instances without double-executing scheduled tasks.

### External SQL Server (multi-provider) + data migration

SQL Server migrations live in a separate assembly (EF scans a whole assembly for `Migration`
classes, so one assembly cannot hold two provider sets). Any schema change must be added to
BOTH providers. Cascade-path differences are branched on `Database.IsSqlite()` in
`OnModelCreating`.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure.SqlServer/` | New assembly — squashed `20260707195344_InitialCreate` migration (+ Designer + snapshot) for the SQL Server provider |
| `src/Diva.Infrastructure/Data/DivaDbContext.cs` | `isSqlite` branch: group-overlay `GroupId` and self-ref hook-rule `OverridesParentRuleId` use `NoAction` on SQL Server (avoids multiple-cascade-path / self-ref cascade errors); Email filtered index syntax branched |
| `src/Diva.Infrastructure/Data/DivaDbContextFactory.cs`, `DatabaseProviderFactory.cs`, `SessionTraceDbContextFactory.cs` | Provider selection via `-- --provider SqlServer`; `MigrationsAssembly` set to SqlServer assembly when `Database:Provider == "SqlServer"`; trace DB derived as `<Database>Trace` |
| `tools/DbMigrate/` | New one-time SQLite → SQL Server data copy tool — system-tenant read, `SqlBulkCopy(KeepIdentity)` + `SET IDENTITY_INSERT`, single transaction with FK disable/re-enable, per-table row-count validation + rollback, `DBCC CHECKIDENT` reseed |
| `Dockerfile` | `migrate` stage rebased on `aspnet:10.0` (DbMigrate transitively needs `Microsoft.AspNetCore.App`); placed before runtime stage so `build: .` still yields the API image |
| `docker-compose.sqlserver.yml` | New overlay for an external SQL Server (no bundled DB container); profile-gated `dbmigrate` service |
| `.env.example`, `docs/phase-04-database.md`, `docs/ref-config.md` | External SQL Server config + data-migration runbook |

### Scheduler leader election (multi-instance safe) + manual run anywhere

`TaskScheduler:AutoPollEnabled` (default `true`) controls whether an instance auto-polls due
tasks and runs stuck-run recovery. Set `false` on all-but-one API replica so scheduled tasks
fire exactly once cluster-wide. Manual "Run now" always executes on whichever instance
receives the request, regardless of the flag.

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/TaskSchedulerOptions.cs` | New `AutoPollEnabled` flag (separate from `IsEnabled` master switch) |
| `src/Diva.Infrastructure/Scheduler/ISchedulerManualDispatch.cs` | New interface — immediate in-process dispatch of a manual run on the local instance |
| `src/Diva.Infrastructure/Scheduler/SchedulerHostedService.cs` | Implements `ISchedulerManualDispatch`; semaphore initialised in ctor; non-leader mode skips polling + startup/timeout stuck-run recovery but stays alive for manual dispatch; `RequestManualDispatch` activates+dispatches the pending run locally (marking it `running` prevents the leader's orphaned-pending sweep from double-dispatching) |
| `src/Diva.Host/Program.cs` | `SchedulerHostedService` registered once as singleton + `ISchedulerManualDispatch` + hosted service (shared instance) |
| `src/Diva.Host/Controllers/SchedulerController.cs` | `TriggerNow` calls `RequestManualDispatch` after `TriggerNowAsync` (unless run `skipped`) |
| `src/Diva.Host/appsettings.json`, `.env.example` | `AutoPollEnabled` default + `TaskScheduler__AutoPollEnabled` env var |

---

## [2026-06-12] RBAC enforcement + Agent Access Groups + SSO claim mapping

Three related authorization features landed together: role-based access control on admin
endpoints, per-agent access groups, and SSO claim → role/access-group mapping with a safe
default-user fallback.

### Role-based access control (admin / user / viewer)

Admin-only mutations are now guarded so a plain `user`/`viewer` cannot create, update, delete,
or configure platform resources. Invoke endpoints remain open to all authenticated users
(scoped per-agent by the access-group check below).

| File | Change |
|------|--------|
| `src/Diva.Host/Auth/RequireTenantAdminAttribute.cs` | New authorization filter — 403 unless `TenantContext.IsAdmin`/`IsMasterAdmin` |
| `src/Diva.Host/Controllers/*.cs` | `[RequireTenantAdmin]` applied to admin mutations across Admin, Agents, ApiKeys, Credentials, LearnedRules, RulePack, Scheduler, Supervisor, AgentOptimization controllers |
| `src/Diva.Host/Controllers/AgentsController.cs` | List/Get filter agents to those the caller may invoke; create/update/delete/prompt-improve admin-gated |
| `src/Diva.Host/Controllers/SessionsController.cs` | User-scoped sessions (`IsRestrictedUser` helper); purge admin-gated |
| `admin-portal/src/lib/auth.ts`, `App.tsx`, `components/layout/app-sidebar.tsx`, `AgentList.tsx`, `LoginPage.tsx`, `AuthCallback.tsx` | Frontend `isAdmin()` gating: `AdminGuard` routes, `DefaultRedirect`, chat-user nav group, hidden admin actions, `is_admin` stored from login/SSO |

### Agent Access Groups (per-user / per-role authorization)

See [phase-28-agent-access-groups.md](phase-28-agent-access-groups.md). Group a tenant's agents
and grant invoke access to selected users/roles. Backward compatible — agents not in any
restricted group stay open to all tenant users.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Data/Entities/AgentGroupEntity.cs` | New entity (`ITenantEntity`) — name, agent IDs, allowed user IDs/roles |
| `src/Diva.Infrastructure/Data/Migrations/20260612181544_AddAgentGroups*.cs` | EF migration (+ Designer + snapshot) |
| `src/Diva.TenantAdmin/Services/AgentGroupService.cs`, `IAgentGroupService.cs` | `CanInvokeAgentAsync`/`GetDeniedAgentIdsAsync`/`IsGranted`, 5-min `IMemoryCache` per-tenant map |
| `src/Diva.Host/Controllers/AgentGroupsController.cs` | CRUD REST API |
| `src/Diva.Host/Controllers/AgentsController.cs` | Invoke + InvokeStream call `CanInvokeAgentAsync` → 403 on denial |
| `admin-portal/src/components/AgentGroups.tsx` | Admin UI |
| `tests/Diva.TenantAdmin.Tests/AgentGroupServiceTests.cs` | Service tests |

### SSO claim mapping (RoleMap / AccessGroupMap) + default-user fallback

SSO providers rarely emit Diva's internal role names. A per-tenant mapping layer translates raw
IdP role/group values into canonical Diva roles and access-group IDs. A user with no mapped
roles defaults to the `user` role and (with no resolved access groups) can only invoke agents
that are not group-restricted.

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/OAuthOptions.cs` | `ClaimMappingsOptions` gains `Groups`, `GroupAccess`, `RoleMap` (`Dictionary<string,string>`), `AccessGroupMap` (`Dictionary<string,string[]>`) |
| `src/Diva.Infrastructure/Auth/SsoClaimMapper.cs` | New helper — role normalization (when `UseRoleMappings` on), access-group resolution, dedupe, default `user` fallback |
| `src/Diva.Host/Controllers/AuthController.cs` | Callback extracts `agent_access`/`group_access`, runs `SsoClaimMapper.Map`, passes results to `IssueSsoJwt` |
| `src/Diva.Infrastructure/Auth/LocalAuthService.cs` | `IssueSsoJwt` emits `agent_access`/`group_access` claims |
| `src/Diva.Infrastructure/Auth/TenantClaimsExtractor.cs` | Reads `group_access` claim into `TenantContext.GroupAccess` |
| `admin-portal/src/components/SsoConfigEditor.tsx` | Structured Role Mapping + Access Group Mapping row editors (merge into `claimMappingsJson`) |
| `tests/Diva.TenantAdmin.Tests/SsoClaimMapperTests.cs`, `TenantClaimsExtractorGroupTests.cs` | Mapper + fallback + claim-extraction tests |

---

## [2026-06-11] Verification Auto-mode escalation + token-cost optimisations

### Auto mode now escalates low-confidence responses to an LLM cross-check

Previously `Auto` always terminated in the cheap `ToolGrounded` heuristic and never made a second LLM call, so action/delivery claims (e.g. "email sent", "CC'd", "delivered", "rendered") were trusted at a flat confidence even though tool *data* evidence could not prove them — the same false-positive class seen in Strict mode. `ToolGroundedCheck` now returns **variable confidence**, and `Auto` escalates to a non-blocking LLM verifier only when confidence falls below a configurable threshold and evidence exists. The common case (plain tool-grounded data) stays zero-cost.

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/VerificationOptions.cs` | Added `AutoEscalateThreshold` (default `0.7`; `0` disables escalation) |
| `src/Diva.Infrastructure/Verification/ResponseVerifier.cs` | New `ActionClaimPattern` regex; `ToolGroundedCheck` lowers confidence to `0.6` on action/delivery claims; `AutoVerifyAsync` rewritten to run heuristic first, then escalate to a non-blocking `LlmVerifier` (relabelled `Mode="Auto"`) below threshold when evidence is present |
| `src/Diva.Host/appsettings.json` | Added `Verification:AutoEscalateThreshold: 0.7` |
| `docker-compose.yml` | Dev stack `Verification__Mode` switched `LlmVerifier` → `Auto`; added `Verification__AutoEscalateThreshold: "0.7"` |
| `tests/Diva.Agents.Tests/ResponseVerifierTests.cs` | +4 tests: action-claim confidence drop, plain-data baseline, escalation with evidence, no-escalation without evidence, escalation disabled (14 total, all green) |
| `docs/arch-response-verification.md` | Updated Auto decision tree, per-agent table, and config example |

### Context-window token-cost optimisations

| File | Change |
|------|--------|
| `src/Diva.Core/Configuration/AgentOptions.cs` | Added `ContextWindow:SummarizerMaxTokens` (default 512) |
| `src/Diva.Infrastructure/Context/ContextWindowManager.cs` | Point B summariser max tokens now configurable via `SummarizerMaxTokens` (was hardcoded 512) for both Anthropic and OpenAI strategies |
| `src/Diva.TenantAdmin/Prompts/TenantAwarePromptBuilder.cs` | Few-shot examples (Phase 24) moved from the per-session dynamic block to the static (cached) block — they are stable per agent+tenant, so this maximises the Anthropic BP1 prompt-cache hit rate |

---

## [2026-06-10] ADR-13683 — AI Session & Context Management gaps resolved

Closes the three critical gaps identified against ADR-13683 (_Implement AI Session & Context Management Framework_).

### GAP 2 — Supervisor conversation history forwarded to Worker Agents

Previously `DispatchStage` created worker `AgentRequest`s with no session context; workers started blind even when the user had already provided parameters (date, party size, site) to the supervisor.

| File | Change |
|------|--------|
| `src/Diva.Core/Models/AgentRequest.cs` | Added optional `ConversationContext` property — condensed supervisor history injected by `DispatchStage` |
| `src/Diva.Agents/Supervisor/Stages/DispatchStage.cs` | Populates `ConversationContext` from `state.SessionHistory` (last 10 messages) via new `BuildConversationContext()` helper |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | Injects `ConversationContext` into the worker system prompt (respects Anthropic cache split — goes into the volatile dynamic block) |

### GAP 4 (security) — SessionsController IDOR fixed

Non-master users could read or delete another tenant's session data via direct ID on five endpoints.

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/SessionsController.cs` | Added `effectiveTenantId` ownership check to `GetSession`, `GetTurnIterations`, `GetSessionTree`, `ExportSession`, and `DeleteSession` — same pattern already used by list and continue endpoints |

### GAP 1 — Session expiry now enforced at runtime

`ExpiresAt` was modelled but never enforced — active-session lookup ignored it, and no background worker marked sessions expired.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Sessions/AgentSessionService.cs` | `GetOrCreateAsync` now filters by `s.ExpiresAt > now` in addition to `Status == "active"` |
| `src/Diva.Infrastructure/Sessions/SessionCleanupService.cs` | **New** — `BackgroundService` that marks active sessions with past `ExpiresAt` as `"expired"` and hard-deletes sessions inactive for `HardDeleteAfterDays` (default 90); runs every `IntervalHours` (default 6) |
| `src/Diva.Host/Program.cs` | Registers `SessionCleanupService` and binds `SessionCleanupOptions` from `Sessions` config section |
| `src/Diva.Host/appsettings.json` | Added `Sessions` config section: `CleanupEnabled: true`, `IntervalHours: 6`, `HardDeleteAfterDays: 90` |

### Backward compatibility

All changes are additive or narrowly scoped. No DB migrations needed. No agent reconfiguration required. Workers that receive `null` for `ConversationContext` behave exactly as before.

---

## [2026-06-02] Platform Administrators management

Multiple platform admins can now be created and managed from the admin portal.
Previously only a single master admin existed (created during first-time setup).

### New files

| File | Purpose |
|------|---------|
| `admin-portal/src/components/PlatformAdminsPage.tsx` | Platform admin management page — wraps `LocalUsersPanel` scoped to `tenantId=0` with `master_admin` role only |

### Modified files

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/AuthController.cs` | `DeleteLocalUser`: guard prevents deleting the last active platform admin (returns `400`) |
| `admin-portal/src/components/LocalUsersPanel.tsx` | Added `availableRoles` and `defaultRoles` props (backward-compatible; existing tenant usage unchanged) |
| `admin-portal/src/App.tsx` | Route `/platform/admins` added |
| `admin-portal/src/components/layout/app-sidebar.tsx` | "Admins" nav item added to Platform Admin group (`ShieldAlert` icon) |

### Behaviour

- Master admin navigates to **Platform Admin → Admins**
- **Add User** creates a new `TenantId=0` user with role `master_admin` via the existing `POST /api/auth/local-users?tenantId=0` endpoint
- New platform admins appear on the **Platform administrator? Sign in here** login form and have full access to all tenants and settings
- Deleting is blocked if it would leave zero active platform admins

---

## [2026-06-02] Bug fixes — session_id template variable, logout redirect, reverse-proxy deployment, change password & feedback page branding

Five independent fixes and one new feature shipped together.

### 1. `{{session_id}}` template variable resolved blank

`TenantContext.SessionId` was null at prompt-build time because the session was created *after*
`TenantAwarePromptBuilder` ran. Fixed by enriching `TenantContext` with the resolved session ID
immediately after `GetOrCreateAsync` returns.

| File | Change |
|------|--------|
| `src/Diva.Core/Models/TenantContext.cs` | Added `WithSession(string? sessionId)` helper — copies all properties and sets `SessionId` |
| `src/Diva.Infrastructure/LiteLLM/AnthropicAgentRunner.cs` | `tenant = tenant.WithSession(sessionId)` after `GetOrCreateAsync` |
| `src/Diva.TenantAdmin/Prompts/TenantAwarePromptBuilder.cs` | `BuildRuntimeVariables` includes `["session_id"] = tenant.SessionId ?? ""` |
| `admin-portal/src/components/AgentBuilder.tsx` | Added `session_id` to the `PROMPT_VARIABLES` help list |

### 2. Prompt Quick Fix — wrong prompt text sent to LLM

`POST /api/agents/{id}/prompt/improve` was ignoring the in-editor prompt and loading
`agent.SystemPrompt` from the DB. Fixed by adding an optional `CurrentPrompt` field to the
request body which takes precedence when supplied.

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/AuthController.cs` | `ImprovePromptRequest` record: added optional `CurrentPrompt` field with `[JsonPropertyName("currentPrompt")]` |
| `src/Diva.Host/Controllers/AgentsController.cs` | Uses `req.CurrentPrompt ?? agent.SystemPrompt` |
| `admin-portal/src/components/PromptQuickFixDialog.tsx` | Generalised: removed `agentId` prop; added `onImprove: (instruction) => Promise<string>` callback + `currentPrompt` prop |
| `admin-portal/src/components/AgentBuilder.tsx` | Passes `onImprove` callback with live `form.systemPrompt` |
| `admin-portal/src/components/ScheduledTasks.tsx` | Added Quick Fix button + `PromptQuickFixDialog` to the Task dialog form |
| `admin-portal/src/api.ts` | `improvePrompt()` accepts optional `currentPrompt` arg |

### 3. Reverse-proxy / subpath deployment (`VITE_BASE_PATH`)

The portal is served at `https://proxy-internal.totaleintegrated.com/beta/tei-ai/` via a corporate
reverse proxy. Static assets and API calls were using wrong base URLs.

| File | Change |
|------|--------|
| `admin-portal/vite.config.ts` | Reads `VITE_BASE_PATH` env var at build time; sets Vite `base`; all output under `/assets/` |
| `admin-portal/src/App.tsx` | `BrowserRouter` uses `basename={import.meta.env.BASE_URL}` |
| `admin-portal/src/api.ts` | `BASE` uses `import.meta.env.VITE_API_URL ?? import.meta.env.BASE_URL.replace(/\/$/, '')` |
| `admin-portal/src/lib/auth.ts` | Same `API_BASE` pattern |
| `admin-portal/src/components/LoginPage.tsx` | Same `API_BASE` pattern (was last remaining `localhost:5062` hardcode) |
| `admin-portal/src/components/WidgetManager.tsx` | Same `BASE_URL` pattern |
| `admin-portal/nginx.conf` | Added `listen 80;` (reverse proxies default to port 80) |
| `admin-portal/Dockerfile` | `VITE_BASE_PATH` ARG passed to `npx vite build` |
| `docker-compose.yml` | `VITE_BASE_PATH` build-arg + comma-separated `PORTAL_ORIGIN` comment |
| `.env` | `VITE_BASE_PATH=/beta/tei-ai` |
| `src/Diva.Host/Program.cs` | CORS `CorsOrigin` split on `,` to support multiple allowed origins |

### 4. Logout redirect producing malformed URL

`AdminPortal:CorsOrigin` is a comma-separated list for multi-origin support. `_portalOrigin` was
used verbatim, producing broken redirect URLs like
`https://proxy.../beta/tei-ai,http://localhost:6010/login`.

| File | Change |
|------|--------|
| `src/Diva.Host/Controllers/AuthController.cs` | `_portalOrigin` now takes `Split(',')[0]` — first origin only for redirect URLs |
| `admin-portal/src/lib/auth.ts` | Non-SSO logout fallback changed from `"/login"` to `` `${import.meta.env.BASE_URL}login` `` so subpath deployments redirect correctly |

### 5. Change password for local-auth users

Self-service password change for users who log in with a local username/password account (not SSO).
Admin users can also change their own master-admin password via the same endpoint.

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Auth/LocalAuthService.cs` | Added `ChangePasswordAsync` to `ILocalAuthService` interface and implementation — verifies current password (PBKDF2-SHA256) before updating hash |
| `src/Diva.Host/Controllers/AuthController.cs` | `ChangePasswordRequest` record; `POST /api/auth/change-password` endpoint — validates `int.TryParse(userId)` to reject SSO users; min 8-char validation |
| `admin-portal/src/api.ts` | `changePassword(currentPassword, newPassword)` method |
| `admin-portal/src/components/ChangePasswordDialog.tsx` | **New file** — dialog with current / new / confirm fields; client-side match + min-length validation; `toast.success` on success |
| `admin-portal/src/components/layout/topbar.tsx` | `KeyRound` icon button — shown **only** for local-auth users (numeric `userId`); wires `ChangePasswordDialog` |

### 6. Scheduler Feedback Page — branded header/footer

The public feedback form (emailed link, no auth required) had no header or footer, making it
appear unbranded.

| File | Change |
|------|--------|
| `admin-portal/src/components/SchedulerFeedbackPage.tsx` | Added `FeedbackShell` layout component: sticky header with `Shield` icon + `APP_NAME`, footer with copyright year; all states (loading, submitted, error, form) wrapped in `FeedbackShell` |

---

## [2026-05-29] Scheduler — Run Feedback Collection

Allows external recipients (from notification emails) to submit feedback on scheduler run results
via a tokenised public link. Admins can review, approve, or reject submitted feedback in the portal.

### Overview

| Feature | Summary |
|---------|---------|
| **Public feedback form** | Tokenised one-time link in notification emails; shows run context; collects thumbs up/down, star rating, category, correction text, and optional submitter identity |
| **Feedback token service** | HMAC-signed, expiry-bounded tokens; tokens are single-use (marked consumed on submit) |
| **Admin review panel** | `/schedules/feedback` page; table of pending/reviewed feedback; approve (trigger rule suggestion) or reject; filter by status |
| **Feedback settings** | Per-tenant opt-in/opt-out; configurable expiry days |

### New files

| File | Purpose |
|------|---------|
| `src/Diva.Infrastructure/Scheduler/ISchedulerFeedbackService.cs` | CRUD + approve/reject interface |
| `src/Diva.Infrastructure/Scheduler/SchedulerFeedbackService.cs` | EF implementation |
| `src/Diva.Infrastructure/Scheduler/ISchedulerFeedbackTokenService.cs` | Token generate/validate interface |
| `src/Diva.Infrastructure/Scheduler/SchedulerFeedbackTokenService.cs` | HMAC-SHA256 signed tokens with expiry |
| `src/Diva.Infrastructure/Data/Entities/SchedulerFeedbackEntity.cs` | `SchedulerFeedbackEntity` EF entity |
| `src/Diva.Infrastructure/Data/Entities/TenantFeedbackSettingsEntity.cs` | Per-tenant feedback settings entity |
| `src/Diva.Infrastructure/Data/Migrations/20260529161658_AddSchedulerFeedback.*` | EF migration — `SchedulerFeedback` table |
| `src/Diva.Infrastructure/Data/Migrations/20260529210822_AddTenantFeedbackSettings.*` | EF migration — `TenantFeedbackSettings` table |
| `src/Diva.Host/Controllers/SchedulerFeedbackController.cs` | Public submit + admin CRUD endpoints |
| `admin-portal/src/components/SchedulerFeedbackPage.tsx` | Public tokenised feedback form (no auth) |
| `admin-portal/src/components/SchedulerFeedbackReview.tsx` | Admin review table with approve/reject dialogs |

### Modified files

| File | Change |
|------|--------|
| `src/Diva.Infrastructure/Data/DivaDbContext.cs` | `SchedulerFeedback` + `TenantFeedbackSettings` DbSets; EF query filters |
| `src/Diva.Infrastructure/Data/Migrations/DivaDbContextModelSnapshot.cs` | Updated model snapshot |
| `src/Diva.Infrastructure/Scheduler/IScheduledTaskService.cs` | Feedback token injection point |
| `src/Diva.Infrastructure/Scheduler/ScheduledTaskService.cs` | Generates feedback token; appended to notification email link |
| `src/Diva.Infrastructure/Scheduler/SchedulerHostedService.cs` | Passes token to notification email on run completion |
| `src/Diva.Core/Configuration/TaskSchedulerOptions.cs` | `FeedbackLinkBaseUrl` + `FeedbackTokenExpiryDays` options |
| `src/Diva.Host/appsettings.json` | `TaskScheduler.FeedbackLinkBaseUrl` default |
| `admin-portal/src/api.ts` | `SchedulerFeedbackContext`, `SchedulerFeedbackItem` interfaces; `getSchedulerFeedbackContext`, `submitSchedulerFeedback`, `listSchedulerFeedback`, `approveSchedulerFeedback`, `rejectSchedulerFeedback` methods |
| `admin-portal/src/components/layout/app-sidebar.tsx` | Added "Schedule Feedback" nav item (`/schedules/feedback`, `Star` icon) |
| `admin-portal/src/mocks/handlers.ts` | MSW mock handlers for feedback endpoints |

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
