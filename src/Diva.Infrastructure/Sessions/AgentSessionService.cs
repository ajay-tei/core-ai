using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.Infrastructure.Sessions;

public sealed class AgentSessionService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<AgentSessionService> _logger;

    public AgentSessionService(IDatabaseProviderFactory db, ILogger<AgentSessionService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Returns the session ID and ordered conversation history for the given session.
    /// Creates a new session if sessionId is null or not found.
    /// </summary>
    public async Task<(string SessionId, List<ConversationTurn> History)> GetOrCreateAsync(
        string? sessionId,
        string agentId,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(sessionId))
        {
            using var db = _db.CreateDbContext(tenant);
            var now = DateTime.UtcNow;
            var session = await db.Sessions
                .Include(s => s.Messages.OrderBy(m => m.CreatedAt))
                .FirstOrDefaultAsync(s => s.Id == sessionId && s.Status == "active" && s.ExpiresAt > now, ct);

            if (session is not null)
            {
                var history = session.Messages
                    .Select(m => new ConversationTurn(m.Role, m.Content))
                    .ToList();
                _logger.LogDebug("Loaded session {SessionId} with {Count} messages", sessionId, history.Count);
                return (sessionId, history);
            }

            _logger.LogWarning("Session {SessionId} not found or expired, creating new", sessionId);
        }

        // Create a new session
        using var createDb = _db.CreateDbContext(tenant);
        var newSession = new AgentSessionEntity
        {
            TenantId = tenant.TenantId,
            SiteId = tenant.CurrentSiteId,
            UserId = tenant.UserId ?? "anonymous",
            CurrentAgentType = agentId,
        };
        createDb.Sessions.Add(newSession);
        await createDb.SaveChangesAsync(ct);

        _logger.LogDebug("Created new session {SessionId}", newSession.Id);
        return (newSession.Id, []);
    }

    /// <summary>
    /// Persists the user message and assistant reply for a session turn.
    /// Returns the 1-based turn number (count of assistant messages before this save + 1)
    /// so the caller can pass it to SessionTraceWriter.FlushTurnAsync.
    /// </summary>
    public async Task<int> SaveTurnAsync(
        string sessionId,
        string userMessage,
        string assistantReply,
        CancellationToken ct)
    {
        using var db = _db.CreateDbContext();   // system context — no tenant filter needed

        // Count existing assistant messages to determine turn number
        var existingTurns = await db.SessionMessages
            .CountAsync(m => m.SessionId == sessionId && m.Role == "assistant", ct);
        var turnNumber = existingTurns + 1;

        db.SessionMessages.AddRange([
            new AgentSessionMessageEntity { SessionId = sessionId, Role = "user",      Content = userMessage },
            new AgentSessionMessageEntity { SessionId = sessionId, Role = "assistant", Content = assistantReply },
        ]);

        // Touch LastActivityAt via SaveChangesAsync hook
        var session = await db.Sessions.FindAsync([sessionId], ct);
        if (session is not null)
            session.LastActivityAt = DateTime.UtcNow;

        await db.SaveChangesAsync(ct);
        _logger.LogDebug("Saved turn {TurnNumber} for session {SessionId}", turnNumber, sessionId);
        return turnNumber;
    }

    /// <summary>
    /// Reactivates a stored conversation session so it can be resumed in chat.
    /// Sets Status back to "active" and extends ExpiresAt, since
    /// <see cref="GetOrCreateAsync"/> only replays history for active sessions.
    /// Tenant-scoped via the supplied <see cref="TenantContext"/>.
    /// Returns true if a matching session was found and reactivated.
    /// </summary>
    public async Task<bool> ReactivateAsync(
        string sessionId,
        TenantContext tenant,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(sessionId))
            return false;

        using var db = _db.CreateDbContext(tenant);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId, ct);
        if (session is null)
        {
            _logger.LogWarning("ReactivateAsync: session {SessionId} not found for tenant {TenantId}", sessionId, tenant.TenantId);
            return false;
        }

        session.Status = "active";
        session.ExpiresAt = DateTime.UtcNow.AddHours(24);
        session.LastActivityAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        _logger.LogDebug("Reactivated session {SessionId} for tenant {TenantId}", sessionId, tenant.TenantId);
        return true;
    }
}

/// <summary>A single turn in the conversation history.</summary>
public sealed record ConversationTurn(string Role, string Content);
