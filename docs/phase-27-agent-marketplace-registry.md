# Phase 27 — Central Agent Registry / Marketplace (Cross-Tenant & Multi-Instance Config Propagation)

> **Status:** `[ ]` Not Started — captured for future implementation, not scheduled.
> **Depends on:** [phase-04-database.md](phase-04-database.md), [phase-08-agents.md](phase-08-agents.md), [phase-10-api-host.md](phase-10-api-host.md), [phase-18 (Group-Level Agents)](INDEX.md), Agent Export/Import (`AgentExportService`)
> **Projects:** `Diva.Core`, `Diva.Infrastructure`, `Diva.Agents`, `Diva.TenantAdmin`, `Diva.Host`, `admin-portal`
> **Sub-phases:** 27.1 Marketplace Catalog → 27.2 Field-Lock + 5-Tier Resolver → 27.3 Pull/Subscribe/Pin → 27.4 Multi-Instance Infrastructure → 27.5 Cross-Deployment Sync

## Context

Agent configuration must be maintained across **multiple tenants, tenant groups, multiple server
instances (horizontal scaling), and separate deployments (per-region / per-customer / on-prem)**.
Some parameters are common to all consumers; some are tenant-specific. When an agent is updated,
that revision must propagate to other tenants, groups, and servers in a controlled way.

The platform already has the right foundations:

- **`AgentDefinitionEntity`** — tenant-scoped, 30+ config fields, has `Version` / `Status` / `PublishedAt`
  ([src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs](../src/Diva.Infrastructure/Data/Entities/AgentDefinitionEntity.cs))
- **`GroupAgentTemplateEntity`** + **`TenantGroupAgentOverlayEntity`** — group-shared blueprint + per-tenant override (`null` = inherit)
  ([src/Diva.Infrastructure/Data/Entities/TenantGroupEntities.cs](../src/Diva.Infrastructure/Data/Entities/TenantGroupEntities.cs))
- **`GroupAgentOverlayMerger`** — merges template + overlay at runtime
- **`LlmConfigResolver`** — existing platform → group → tenant cascade (the model to mirror)
- **`AgentExportService`** + **`AgentExportBundle`** — export/import a full agent (definition + linked rules)
  ([src/Diva.Infrastructure/AgentExport/AgentExportService.cs](../src/Diva.Infrastructure/AgentExport/AgentExportService.cs))
- **`AgentPromptHistoryEntity`** — append-only revision/restore pattern to mirror
- **`DynamicAgentRegistry.GetAgentsForTenantAsync`** — DI singleton, uses `IDatabaseProviderFactory`, merges static + DB + group templates
  ([src/Diva.Agents/Registry/DynamicAgentRegistry.cs](../src/Diva.Agents/Registry/DynamicAgentRegistry.cs))

### Gaps this phase closes

| Gap | Current state | Impact |
|-----|---------------|--------|
| No catalog / marketplace concept | "registry" in code = runtime DI registries only | No authoritative source of truth for shareable agents |
| Multi-instance unsupported | `IMemoryCache` everywhere; `AddDistributedMemoryCache()` is in-memory only; no Redis; no SignalR backplane; no cross-node invalidation | 2nd node serves stale config up to 5-min TTL; SSE events don't cross nodes |
| Agent `Version` not bumped on update | `PUT /api/agents/{id}` never increments `Version` ([AgentsController.cs](../src/Diva.Host/Controllers/AgentsController.cs)) | No real revisioning on tenant-owned agents |
| Group template field parity | `GroupAgentTemplateEntity` missing `DelegateAgentIdsJson`, `OptimizationOverrideJson` vs `AgentDefinitionEntity` | Templates can't carry delegation/optimization config |
| No cross-deployment propagation | Group→tenant overlay invalidation is local-process only | Separate deployments drift |

## Chosen Model — Central Registry / Marketplace (hub-and-spoke)

Publish a versioned agent **once** to a central catalog; tenants, groups, and other deployments
**pull / subscribe**. This replaces a peer-to-peer replication design with a single authoritative
catalog and explicit publish/pull semantics.

```
                   ┌─────────────────────────────┐
   publish ───────▶│   Central Marketplace        │
   (version)       │   CatalogItem + CatalogVersion│
                   │   (immutable, ContentHash)    │
                   └──────────────┬───────────────┘
            pull / subscribe / pin │ poll / webhook
        ┌───────────────┬──────────┼───────────────┬────────────────┐
        ▼               ▼          ▼                ▼                ▼
  Platform tier    Tenant group   Individual tenant   Other deployment (separate DB)
        └───────────────┴──────────┴───────────────┘
                        │  5-tier LayeredAgentResolver
                        ▼
                DynamicAgentRegistry → running agent
```

### Resolution cascade (common → specific)

`null` = inherit; locked fields = governance (tenant cannot override).

```
marketplace version → platform template → group template → tenant overlay → tenant-owned agent
```

Each catalog version is an **immutable `AgentExportBundle`** keyed by a **content hash (SHA-256 of
canonical JSON)**. The hash is the backbone for both revision dedup and idempotent cross-deployment sync.

---

## Sub-Phase Overview

| Sub-phase | Deliverable | Depends on |
|-----------|-------------|-----------|
| **27.1 — Marketplace Catalog (hub)** | `CatalogItemEntity` + append-only `CatalogVersionEntity`, `MarketplaceService` (publish/list/get/deprecate), `MarketplaceController`, embedded-or-standalone toggle | — |
| **27.2 — Field-Lock + 5-Tier Resolver** | `PlatformAgentTemplateEntity`, `FieldLocksJson`, group template field parity, `LayeredAgentResolver` | 27.1 |
| **27.3 — Pull / Subscribe / Pin** | `InstalledAgentEntity`, install/subscribe/pin, version diff + rollback, fix PUT `Version` bump | 27.1, 27.2 |
| **27.4 — Multi-Instance Infrastructure** | `IConfigChangeBus` (Redis pub/sub + no-op), real `IDistributedCache`, SignalR backplane, `docker-compose.scale.yml` | parallel w/ 27.3 |
| **27.5 — Cross-Deployment Sync** | Marketplace clients (pull `sinceVersion`), idempotent `ContentHash` upsert, polling `BackgroundService` + optional webhook | 27.1 |

---

## 27.1 — Marketplace Catalog (the hub)

**New entities** (in `TenantGroupEntities.cs` or a new `MarketplaceEntities.cs`; **not** `ITenantEntity` —
catalog is platform/global-scoped):

```csharp
public class CatalogItemEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "private";   // public | group | private
    public int? OwnerGroupId { get; set; }                 // when Visibility == group
    public string OwnerScope { get; set; } = string.Empty; // who may publish new versions
    public string LatestStableVersionId { get; set; } = string.Empty;
    public bool IsDeprecated { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class CatalogVersionEntity   // append-only, immutable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string CatalogItemId { get; set; } = string.Empty;
    public int Version { get; set; }
    public string Channel { get; set; } = "stable";        // stable | beta
    public string ContentHash { get; set; } = string.Empty; // SHA-256 of canonical bundle JSON
    public string SnapshotJson { get; set; } = string.Empty; // serialized AgentExportBundle
    public string? PublishedBy { get; set; }
    public string? ChangeNote { get; set; }
    public bool IsYanked { get; set; }
    public DateTime PublishedAt { get; set; } = DateTime.UtcNow;
}
```

**Service / API:**

- `IMarketplaceService` / `MarketplaceService` — `PublishAsync` (serialize agent → `AgentExportBundle` →
  compute `ContentHash` → dedup → create version + bump), `ListCatalogAsync`, `SearchAsync`,
  `GetVersionAsync`, `DeprecateAsync` / `YankAsync`.
- `MarketplaceController` — publish, list/search, list versions, get a specific version.
- **Embedded vs standalone:** config toggle (`Marketplace:Mode = Embedded | Hub | Client`). Start embedded
  in the primary deployment; promote to a standalone registry service when needed.

Reuse `AgentExportBundle` / `AgentExportService` as the unit stored per version.

## 27.2 — Field-Lock + 5-Tier Resolver

1. **`PlatformAgentTemplateEntity`** — top in-deployment tier (mirror `GroupAgentTemplateEntity`, **no** `GroupId`).
2. **`FieldLocksJson`** (`string[]` of locked field names) on platform + group templates. A locked field
   cannot be overridden by lower tiers (governance).
3. **Group template field parity** — add `DelegateAgentIdsJson`, `OptimizationOverrideJson` to
   `GroupAgentTemplateEntity` (currently missing vs `AgentDefinitionEntity`).
4. **`LayeredAgentResolver`** — generalize `GroupAgentOverlayMerger` into a 5-tier merge
   (`marketplace → platform → group → overlay → tenant agent`) honoring field locks and existing
   `null` = inherit semantics. Wire into `DynamicAgentRegistry.GetAgentsForTenantAsync`.

## 27.3 — Pull / Subscribe / Pin

```csharp
public class InstalledAgentEntity   // a scope's binding to a catalog item
{
    public int Id { get; set; }
    public string CatalogItemId { get; set; } = string.Empty;
    public int InstalledVersion { get; set; }
    public int? PinnedVersion { get; set; }   // null + AutoUpdate => always-latest on channel
    public string Scope { get; set; } = string.Empty; // platform | group:{id} | tenant:{id}
    public bool AutoUpdate { get; set; } = true;
    public string Channel { get; set; } = "stable";
    public DateTime InstalledAt { get; set; } = DateTime.UtcNow;
}
```

- **Install** = materialize a catalog version into a scope as a (platform/group) template.
- **Subscribe** = `AutoUpdate = true` → pull new versions on the chosen channel.
- **Pin** (`PinnedVersion`) = controlled rollout; pinned scopes stay on a fixed version.
- **Diff + rollback** — list versions, diff two versions, restore a prior version (reuse the
  prompt-history restore pattern from `AgentSetupAssistant`).
- **Fix the `Version`-bump bug** on tenant-owned agents at `PUT /api/agents/{id}`.

## 27.4 — Multi-Instance Infrastructure (same DB, N nodes)

1. **`IConfigChangeBus`** abstraction — Redis pub/sub implementation + in-memory **no-op** (Redis optional).
   On publish/install/template write → publish `{kind, scopeId, key}` → every node evicts its local
   `IMemoryCache`. Keeps fast in-process reads while fixing cross-node staleness.
2. **Real `IDistributedCache`** — register `AddStackExchangeRedisCache` when `Redis:ConnectionString` is set,
   replacing `AddDistributedMemoryCache()` so `SessionRuleManager` sessions are shared
   ([Program.cs](../src/Diva.Host/Program.cs)).
3. **SignalR Redis backplane** — `AddSignalR().AddStackExchangeRedis(...)` so SSE / stream chunks reach
   clients connected to any node.
4. **`docker-compose.scale.yml`** (new) — Redis service + `replicas: N` + nginx/traefik upstream.
   Document that **SQL Server is required** (SQLite cannot be shared across processes).

## 27.5 — Cross-Deployment Sync (separate DBs, hub-and-spoke)

- Downstream deployments run in `Marketplace:Mode = Client`.
- **Pull:** `GET /api/marketplace/catalog?sinceVersion={n}` (authenticated via existing platform API key
  `X-API-Key`) → receive bundles → **idempotent upsert by `ContentHash`** (skip if unchanged).
- **Poller:** a hosted `BackgroundService` polls the hub on an interval.
- **Push (optional):** hub fires a webhook to registered downstream URLs on publish → downstream triggers
  an immediate pull.
- **Conflict policy:** catalog items are **read-only downstream** (locked); local tenant-owned agents
  remain editable. Surface drift / "update available" in the admin UI.

---

## Key Files

| Area | File |
|------|------|
| Entities | [src/Diva.Infrastructure/Data/Entities/TenantGroupEntities.cs](../src/Diva.Infrastructure/Data/Entities/TenantGroupEntities.cs) — add fields/locks; new `CatalogItemEntity`, `CatalogVersionEntity`, `PlatformAgentTemplateEntity`, `InstalledAgentEntity` |
| Resolver | `src/Diva.Infrastructure/Groups/GroupAgentOverlayMerger.cs` → `LayeredAgentResolver` |
| Registry | [src/Diva.Agents/Registry/DynamicAgentRegistry.cs](../src/Diva.Agents/Registry/DynamicAgentRegistry.cs) — extend merge path; resolve pinned vs latest |
| Export/wire format | [src/Diva.Infrastructure/AgentExport/AgentExportService.cs](../src/Diva.Infrastructure/AgentExport/AgentExportService.cs) — reuse bundle; content-hash idempotent upsert |
| Controllers | [src/Diva.Host/Controllers/AgentsController.cs](../src/Diva.Host/Controllers/AgentsController.cs) (`Version` bump + revision endpoints); new `MarketplaceController`; cross-deployment sync `BackgroundService` |
| DI / infra | [src/Diva.Host/Program.cs](../src/Diva.Host/Program.cs) — Redis `IDistributedCache`, SignalR backplane, `IConfigChangeBus`, hosted poller |
| DbContext | [src/Diva.Infrastructure/Data/DivaDbContext.cs](../src/Diva.Infrastructure/Data/DivaDbContext.cs) — new DbSets + query filters (catalog/platform tables stay non-tenant-scoped); migration + `.Designer.cs` |
| Compose | `docker-compose.scale.yml` (new), [docker-compose.enterprise.yml](../docker-compose.enterprise.yml) — Redis, replicas, proxy |
| Admin UI | `admin-portal` — Marketplace browse/install/update-available pages, version diff, pin/subscribe controls |

---

## Verification

1. `dotnet build Diva.slnx` and `dotnet test` green.
2. EF migration applies cleanly (verify `.Designer.cs` present so `MigrateAsync()` discovers it — see repo migration gotcha).
3. **Unit:** `LayeredAgentResolver` precedence (platform → group → overlay → tenant agent) + field-lock rejects overlay override of a locked field.
4. **Unit:** publish dedup by `ContentHash` (identical content = no new version).
5. **Integration:** pin a scope to v1, publish v2 → pinned scope resolves v1, auto-update scope resolves v2.
6. **Multi-instance:** two `diva-api` nodes + Redis + SQL Server — update/publish on node A, assert node B
   serves new config within the pub/sub window (not the 5-min TTL); SSE stream from node B reaches a client
   connected to node A.
7. **Cross-deployment:** publish on the hub, run downstream pull, assert idempotent upsert by content hash
   (second pull is a no-op).

---

## Decisions

- **Marketplace (hub-and-spoke) replaces peer-to-peer replication** — one authoritative source of truth.
- **Reuse `AgentExportBundle`** as the catalog version + cross-deployment wire format.
- **`ContentHash` idempotency** is the backbone for both revision dedup and sync.
- **Hybrid cache strategy** — keep fast in-process `IMemoryCache` reads + add a Redis pub/sub invalidation
  bus, rather than routing every read through Redis.
- **Redis is optional** — in-memory no-op bus when `Redis:ConnectionString` is unset; multi-node features
  activate only when configured (single-node / dev unaffected).
- **Marketplace is embeddable or standalone** via config (`Marketplace:Mode`) — start embedded.

## Open Considerations

1. **Hub placement** — embedded in the primary deployment vs a standalone registry service. Recommend a
   config toggle; start embedded.
2. **Cross-deployment trust** — `X-API-Key` only (intra-org), `+HMAC` bundle signing (crossing trust
   boundaries), or mTLS at the proxy. Recommend bundle signing if bundles leave the network.
3. **Default adoption** — auto-latest-on-channel (recommended, hands-off) vs pin-required (safer, explicit
   per-revision adoption).
4. **Visibility model** — public marketplace vs private per-group catalogs. Recommend supporting both via
   the `Visibility` field.

## Excluded (flag for later)

- Automatic semantic merge of conflicting concurrent edits.
- Per-field audit UI beyond revision diff.
- Signing/encryption of cross-deployment bundles beyond existing API-key auth (revisit if bundles cross trust boundaries).
