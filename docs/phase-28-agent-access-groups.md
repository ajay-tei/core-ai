# Phase 28: Agent Access Groups (Per-User / Per-Role Authorization)

> **Status:** `[x]` Complete — 2026-06-12
> **Depends on:** [phase-03-oauth-tenant.md](phase-03-oauth-tenant.md), [phase-04-database.md](phase-04-database.md), [phase-06-tenant-admin.md](phase-06-tenant-admin.md), [phase-10-api-host.md](phase-10-api-host.md)
> **Blocks:** Nothing (additive)
> **Projects:** `Diva.Core`, `Diva.Infrastructure`, `Diva.TenantAdmin`, `Diva.Host`, `Diva.Sso`, `admin-portal`
> **Tests:** `tests/Diva.TenantAdmin.Tests`, `tests/Diva.Agents.Tests`

---

## Goal

Group a tenant's agents into named **Agent Groups** and grant **invoke access** to selected
users and/or roles. Access is enforced for **both** authentication paths:

- **SSO (JWT) users** — matched by user id / email / role / SSO group.
- **Platform API key** callers — matched by explicit group grants on the key.

**Backward compatible:** an agent that is not a member of any *restricted* group stays open to
all tenant users (no behavior change). A group is "restricted" only when it has a non-empty
allow-list (users or roles).

> ⚠️ **Naming:** these are **Agent Groups** — distinct from the existing **Tenant Groups**
> ([phase-18](docs/INDEX.md)) which group *tenants* to share agent templates. Do not conflate.

---

## Design Summary

### Access model

```
Agent invoke request
  → TenantContextMiddleware builds TenantContext (JWT or X-API-Key)
  → AgentsController.Invoke / InvokeStream
     1. agent.IsEnabled check
     2. tenant.CanInvokeAgent(agent.AgentType)         ← existing (agent TYPE / "*")
     3. agentGroupService.CanInvokeAgentAsync(...)     ← NEW (per-agent-ID group ACL)
  → 403 if either check fails
```

The new check is **additive (AND semantics)** with the existing `CanInvokeAgent(agentType)`
check. They operate on different axes (agent *type* allow-list vs per-agent-*ID* group ACL) and
are intentionally left independent.

### `CanInvokeAgentAsync(agentId, tenant, ct)` evaluation

1. Look up *restricted* groups (for this tenant) that contain `agentId` — from an
   `IMemoryCache` (5-min TTL) per-tenant map.
2. **No restricted group contains the agent → allow** (backward compatible).
3. Otherwise allow if **any** matching group grants access via:
   - `tenant.IsAdmin` / `tenant.IsMasterAdmin` → always allow,
   - `tenant.GroupAccess` contains the group id (API-key grant or optional SSO claim),
   - `tenant.UserId` **or** `tenant.UserEmail` ∈ group `AllowedUserIds`,
   - any of (`tenant.UserRoles` ∪ `tenant.UserGroups`) ∈ group `AllowedRoles` (case-insensitive).
4. Else → **403 Forbidden**.

### Why `IMemoryCache` (not `IDistributedCache`)

All tenant-config services (`TenantGroupService`, `UserProfileService`, `RulePackService`,
`GroupMembershipCache`) use `IMemoryCache` with a 5-minute TTL and are Singleton-safe via
`IDatabaseProviderFactory`. The `IDistributedCache` rule in the repo instructions applies only
to **session-scoped rules**. The access check runs on the hot invoke path, so caching the
restricted-group map is required. Known limitation (accepted, consistent with
`GroupMembershipCache`): in a multi-instance deployment a write invalidates only the local
cache; staleness is bounded by the 5-minute TTL.

---

## SSO Claim Mapping Fix — Roles vs Groups

**Problem (pre-existing):** `ClaimMappingsOptions` had a `Roles` mapping but **no `Groups`
mapping**. `TenantClaimsExtractor.Extract` read only `mappings.Roles` into `UserRoles`. The SSO
callback folded `groups` into roles only as a *fallback* — if the IdP returned both a `roles`
**and** a `groups` claim, the groups claim was silently dropped. Group-based ACLs would
therefore fail for Azure AD / Okta / Keycloak (which send a dedicated `groups` claim).

**Fix (additive, backward compatible):**

- `ClaimMappingsOptions.Groups` (default `"groups"`).
- `TenantContext.UserGroups` (`string[]`) — first-class, separate from `UserRoles`; copied in
  `WithSession`; defaulted in `System` / `DevMasterAdmin`.
- `TenantClaimsExtractor` populates `UserGroups` from `mappings.Groups`.
- SSO callback reads `groups` **separately** (keeps the roles fallback for back-compat) and
  passes it to `IssueSsoJwt`, which now emits a `groups` claim.
- `SsoTokenValidator` opaque/introspection path surfaces `groups`.
- `appsettings.json` + `appsettings.Development.json` add `"Groups": "groups"`.
- Access evaluation matches `AllowedRoles` against the **union** of `UserRoles` and
  `UserGroups`.

Per-tenant `ClaimMappingsJson` (stored on `TenantSsoConfig`) round-trips `ClaimMappingsOptions`
as JSON, so the new `Groups` property is honored automatically.

---

## Components

### Phase 28a — Data model

#### `src/Diva.Infrastructure/Data/Entities/AgentGroupEntity.cs` (new)

```csharp
public class AgentGroupEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? AgentIdsJson { get; set; }        // JSON string[] of member agent IDs
    public string? AllowedUserIdsJson { get; set; }  // JSON string[] of user ids and/or emails
    public string? AllowedRolesJson { get; set; }    // JSON string[] of roles / SSO groups
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
```

JSON-string columns parsed in code (matches `AgentDefinitionEntity.DelegateAgentIdsJson`), not
EF value converters.

#### `src/Diva.Infrastructure/Data/DivaDbContext.cs` (modified)

- `DbSet<AgentGroupEntity> AgentGroups`
- `HasKey(e => e.Id)` (string PK), tenant query filter, `HasIndex(e => e.TenantId)`.

#### `src/Diva.Infrastructure/Data/Entities/PlatformApiKeyEntity.cs` (modified)

- Add `AllowedGroupIdsJson` (`string?`) — JSON `string[]` of granted agent-group ids.

#### Migration `*_AddAgentGroups`

`AgentGroups` table + `PlatformApiKeys.AllowedGroupIdsJson` column. Requires paired
`.Designer.cs` and a `DivaDbContextModelSnapshot` update.

### Phase 28b — Core model + auth plumbing

- `TenantContext`: add `GroupAccess` (`string[]`) + `UserGroups` (`string[]`).
- `ClaimMappingsOptions.Groups` (default `"groups"`).
- `TenantClaimsExtractor`: map `UserGroups`.
- `TenantContextMiddleware` (API-key branch): populate `GroupAccess` from the key's
  `AllowedGroupIds`.
- `AuthController` SSO callback + `LocalAuthService.IssueSsoJwt`: emit/propagate `groups`.
- `SsoTokenValidator`: surface `groups`.
- `ValidatedApiKey` + `IPlatformApiKeyService` + `PlatformApiKeyService`: `AllowedGroupIds`.

### Phase 28c — Service

#### `src/Diva.TenantAdmin/Services/IAgentGroupService.cs` + `AgentGroupService.cs` (new)

- CRUD (`ListAsync`, `GetAsync`, `CreateAsync`, `UpdateAsync`, `DeleteAsync`).
- `CanInvokeAgentAsync(string agentId, TenantContext tenant, CancellationToken ct)`.
- `IMemoryCache` per-tenant restricted-group map (5-min TTL) + `InvalidateForTenant`.
- Singleton-safe via `IDatabaseProviderFactory`.

### Phase 28d — API + enforcement

- `AgentsController`: inject `IAgentGroupService`; call `CanInvokeAgentAsync` in `Invoke` and
  `InvokeStream` after the existing `CanInvokeAgent` check → 403.
- `AgentGroupsController` (`api/agent-groups`): admin-only CRUD (`EffectiveTenantId` pattern).
- `ApiKeysController`: `allowedGroupIds` on create DTO + flow.
- `Program.cs`: register `IAgentGroupService`.

### Phase 28e — Admin portal

- `api.ts`: `AgentGroup` type + CRUD; `allowedGroupIds` on API-key types.
- `AgentGroups` page (list + editor) — clone `DelegateAgentSelector` for agent membership; user
  selector (`listUserProfiles`) + role multi-select for the allow-list. Route + sidebar nav.
- API key editor: allowed-groups multi-select.

### Phase 28f — Tests

- `AgentGroupService`: unrestricted→allow, user-id match, email match, role match, SSO-group
  match, API-key group grant, admin bypass, denied→403, cache invalidation.
- `TenantClaimsExtractor`: maps `groups` claim (JSON array + CSV); roles **and** groups both
  retained; back-compat (roles-only) still works.
- `AgentsController`: invoke returns 403 when not granted; 200 when granted.

---

## Resolved Open Items

| Item | Decision |
|------|----------|
| `SupervisorController.Invoke` (System context, no user identity) | **Skip** group enforcement — internal/system route, no user principal. |
| Widget anonymous sessions cannot satisfy user/role checks | Widget anonymous JWT already carries `agent_access` for its widget agent; additionally grant that widget agent's groups via `GroupAccess` so a group-restricted agent can still back a public widget. |
| Hide restricted agents from listings for non-allowed users | **Yes (secondary)** — filter `AgentsController.List` for non-admin users using the same service. |

---

## Verification

- `dotnet build Diva.slnx`
- `dotnet test tests/Diva.TenantAdmin.Tests tests/Diva.Agents.Tests`
- Fresh-SQLite migration applies via `MigrateAsync` (Designer.cs present).
- Manual: create a group restricted to user A → invoke as A (200), as B (403); API key with and
  without the group grant.
- `npm run build` + `npm run lint` in `admin-portal`.
