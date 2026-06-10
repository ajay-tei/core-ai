# Phase 7: Session Management

> **Status:** `[x]` Done — updated 2026-06-10 (ADR-13683 gap fixes)
> **Depends on:** [phase-04-database.md](phase-04-database.md)
> **Blocks:** [phase-08-agents.md](phase-08-agents.md)
> **Project:** `Diva.Infrastructure`

---

## Goal

Persist conversation history so agents support multi-turn interactions. A user can ask "Compare that to South Campus" and the agent understands "that" refers to data from the previous message.

---

## Multi-Turn Example

```
User:  "How did East Campus do yesterday?"
Agent: "Revenue was $24,500, up 8% from last week..."

User:  "Compare that to South Campus"
Agent: [understands "that" = East Campus yesterday]
    "South Campus had $28,200, 15% higher than East Campus..."

User:  "Show me the trend for both"
Agent: [remembers both sites from session history]
```

---

## Files to Create

```
src/Diva.Infrastructure/Sessions/
├── ISessionService.cs
└── SessionService.cs
```

DB entities `AgentSessionEntity` and `AgentSessionMessageEntity` are defined in [phase-04-database.md](phase-04-database.md).

---

## DB Entity Schema

```csharp
// Already in DivaDbContext (Phase 4)

public class AgentSessionEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }
    public int SiteId { get; set; }
    public string UserId { get; set; } = "";
    public string? AgentType { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastActivityAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string Status { get; set; } = "active";  // "active" | "expired" | "closed"
    public string? Metadata { get; set; }   // JSON
    public List<AgentSessionMessageEntity> Messages { get; set; } = [];
}

public class AgentSessionMessageEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SessionId { get; set; } = "";
    public string Role { get; set; } = "";    // "user" | "assistant"
    public string Content { get; set; } = "";
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string? Metadata { get; set; }     // JSON: { tokensUsed, agentName, toolsUsed[] }
    public AgentSessionEntity Session { get; set; } = null!;
}
```

---

## ISessionService.cs

```csharp
namespace Diva.Infrastructure.Sessions;

public interface ISessionService
{
    Task<AgentSessionEntity> CreateSessionAsync(TenantContext tenant, CancellationToken ct);
    Task<AgentSessionEntity?> GetSessionAsync(string sessionId, CancellationToken ct);
    Task AddMessageAsync(string sessionId, string role, string content, SessionMessageMetadata? metadata, CancellationToken ct);
    Task<List<AgentSessionMessageEntity>> GetHistoryAsync(string sessionId, int limit, CancellationToken ct);
    Task TouchSessionAsync(string sessionId, CancellationToken ct);
    Task ExpireSessionAsync(string sessionId, CancellationToken ct);
}

public sealed class SessionMessageMetadata
{
    public int TokensUsed { get; init; }
    public string? AgentName { get; init; }
    public string[] ToolsUsed { get; init; } = [];
    public TimeSpan? ExecutionTime { get; init; }
}
```

---

## SessionService.cs

```csharp
namespace Diva.Infrastructure.Sessions;

public class SessionService : ISessionService
{
    private readonly DivaDbContext _db;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromHours(8);

    public async Task<AgentSessionEntity> CreateSessionAsync(TenantContext tenant, CancellationToken ct)
    {
        var session = new AgentSessionEntity
        {
            TenantId  = tenant.TenantId,
            SiteId    = tenant.CurrentSiteId,
            UserId    = tenant.UserId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(SessionTimeout)
        };

        _db.Sessions.Add(session);
        await _db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<AgentSessionEntity?> GetSessionAsync(string sessionId, CancellationToken ct)
    {
        var session = await _db.Sessions
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        // Auto-expire if past expiry
        if (session != null && session.ExpiresAt < DateTimeOffset.UtcNow)
        {
            session.Status = "expired";
            await _db.SaveChangesAsync(ct);
            return null;
        }

        return session;
    }

    public async Task AddMessageAsync(
        string sessionId, string role, string content,
        SessionMessageMetadata? metadata, CancellationToken ct)
    {
        var msg = new AgentSessionMessageEntity
        {
            SessionId = sessionId,
            Role      = role,
            Content   = content,
            Metadata  = metadata != null ? JsonSerializer.Serialize(metadata) : null
        };

        _db.SessionMessages.Add(msg);

        // Update last activity
        var session = await _db.Sessions.FindAsync([sessionId], ct);
        if (session != null)
            session.LastActivityAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);
    }

    public async Task<List<AgentSessionMessageEntity>> GetHistoryAsync(
        string sessionId, int limit, CancellationToken ct)
    {
        // Return last N messages in chronological order
        return await _db.SessionMessages
            .Where(m => m.SessionId == sessionId)
            .OrderByDescending(m => m.Timestamp)
            .Take(limit)
            .OrderBy(m => m.Timestamp)
            .ToListAsync(ct);
    }

    public async Task TouchSessionAsync(string sessionId, CancellationToken ct)
    {
        var session = await _db.Sessions.FindAsync([sessionId], ct);
        if (session != null)
        {
            session.LastActivityAt = DateTimeOffset.UtcNow;
            session.ExpiresAt      = DateTimeOffset.UtcNow.Add(SessionTimeout);
            await _db.SaveChangesAsync(ct);
        }
    }
}
```

---

## SupervisorAgent Session Integration

```csharp
// In SupervisorAgent.InvokeAsync (Phase 8)
public async Task<AgentResponse> InvokeAsync(AgentRequest request, TenantContext tenant, CancellationToken ct)
{
    // Load or create session
    AgentSessionEntity session;
    if (request.SessionId != null)
    {
        session = await _sessions.GetSessionAsync(request.SessionId, ct)
            ?? await _sessions.CreateSessionAsync(tenant, ct);
    }
    else
    {
        session = await _sessions.CreateSessionAsync(tenant, ct);
    }

    // Load conversation history for context
    var history = await _sessions.GetHistoryAsync(session.Id, limit: 10, ct);

    // Add user message
    await _sessions.AddMessageAsync(session.Id, "user", request.Query, null, ct);

    // Build SK ChatHistory from session history
    var chatHistory = new ChatHistory();
    foreach (var msg in history)
        chatHistory.Add(new ChatMessageContent(
            msg.Role == "user" ? AuthorRole.User : AuthorRole.Assistant,
            msg.Content));

    // Execute agent with history context ...
    var result = await ExecuteWithHistoryAsync(chatHistory, request.Query, tenant, ct);

    // Save assistant response

---

## 2026-06-10 — ADR-13683 Gap Fixes (implemented)

### Session Expiry Enforcement

`ExpiresAt` was modelled but never enforced at runtime. Two changes were made:

1. **`AgentSessionService.GetOrCreateAsync`** — active-session lookup now includes `&& s.ExpiresAt > now`; expired sessions fall through to create a new one.

2. **`SessionCleanupService`** (new — `src/Diva.Infrastructure/Sessions/SessionCleanupService.cs`) — `BackgroundService` that runs every `Sessions:IntervalHours` (default 6h):
   - Marks `Status = "expired"` on any active session where `ExpiresAt <= now`
   - Hard-deletes sessions whose `LastActivityAt` is older than `Sessions:HardDeleteAfterDays` (default 90)
   - Registered in `Program.cs`; configured via `appsettings.json` `Sessions` section

### Supervisor → Worker Context Handoff

`DispatchStage` previously created worker `AgentRequest`s with no session context. Three changes were made:

1. **`AgentRequest.ConversationContext`** (new nullable property in `Diva.Core`) — condensed text summary of the supervisor's conversation history.

2. **`DispatchStage.BuildConversationContext()`** — static helper that takes the last 10 messages from `state.SessionHistory` and formats them as a markdown block. Assigned to `subRequest.ConversationContext`.

3. **`AnthropicAgentRunner`** — injects `ConversationContext` into the worker system prompt immediately after the `Instructions` block. Respects Anthropic history-caching split (goes into the dynamic volatile block).

### SessionsController IDOR Security Fix

`GetSession`, `GetTurnIterations`, `GetSessionTree`, `ExportSession`, and `DeleteSession` now apply the same `effectiveTenantId` ownership check already present on the list and continue endpoints. Non-master users receive `404` if the session belongs to a different tenant.

    await _sessions.AddMessageAsync(session.Id, "assistant", result.Content,
        new SessionMessageMetadata
        {
            TokensUsed     = result.TotalTokensUsed,
            AgentName      = "supervisor",
            ToolsUsed      = [.. result.ToolsUsed],
            ExecutionTime  = result.ExecutionTime
        }, ct);

    return result with { SessionId = session.Id };
}
```

---

## Service Registration

```csharp
builder.Services.AddScoped<ISessionService, SessionService>();
```

---

## Verification

- [x] `GetOrCreateAsync` creates a new session when `sessionId` is null
- [x] `GetOrCreateAsync` loads and returns full message history for existing session
- [x] `SaveTurnAsync` persists both user and assistant messages in one call
- [x] Multi-turn test: "My name is Alex" → "What is my name?" → agent replies "Your name is Alex"
- [x] Multi-turn test: "What was the first thing I told you?" → agent correctly quotes first message
- [x] `sessionId` is returned in every `AgentResponse` and threaded through admin portal
- [ ] Session messages are scoped to tenant (EF query filter applied)
- [ ] Session expiry / cleanup (not yet implemented)

---

## As Built — Deviations from Plan

The planned `ISessionService` interface with 5 methods was simplified into a concrete `AgentSessionService` with 2 focused methods. The interface was not needed yet (no other consumers).

| Plan | Actual |
|---|---|
| `ISessionService` interface | Not implemented — concrete `AgentSessionService` only |
| Scoped `SessionService` with injected `DivaDbContext` | Singleton `AgentSessionService` using `IDatabaseProviderFactory` (safe from singleton) |
| `CreateSessionAsync`, `GetSessionAsync`, `AddMessageAsync`, `GetHistoryAsync`, `TouchSessionAsync` | `GetOrCreateAsync` (create+load combined), `SaveTurnAsync` (save both messages at once) |
| Session history via `List<AgentSessionMessageEntity>` | `List<ConversationTurn>` record — simpler, decoupled from entity |
| `SessionMessageMetadata` (tokens, agent name, tools) | Not persisted yet — `MetadataJson` column exists in DB but is unused |
| `ExpireSessionAsync` | Not implemented |

**Key files actually created:**
```
src/Diva.Infrastructure/Sessions/
└── AgentSessionService.cs    ✓ implemented (GetOrCreateAsync + SaveTurnAsync)
```

**Integration:**
- `AnthropicAgentRunner` injects `AgentSessionService` and calls it in `RunAsync`
- `AgentChat.tsx` stores `sessionId` state and passes it on every subsequent request
- "Clear" button resets `sessionId` to start a fresh session
