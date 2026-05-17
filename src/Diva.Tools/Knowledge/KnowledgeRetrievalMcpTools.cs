using System.ComponentModel;
using System.Text.Json;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Rag;
using Diva.Rag.Abstractions;
using Diva.Rag.Services;
using Diva.Tools.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace Diva.Tools.Knowledge;

[McpServerToolType]
public sealed class KnowledgeRetrievalMcpTools(
    IHttpContextAccessor http,
    IServiceScopeFactory scopeFactory,
    IOptions<RagOptions> opts,
    ILogger<KnowledgeRetrievalMcpTools> logger) : IDivaMcpToolType
{
    private readonly RagOptions _opts = opts.Value;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private string Error(string message) =>
        JsonSerializer.Serialize(new { error = "Error", message }, _json);

    private string Disabled() =>
        JsonSerializer.Serialize(new { error = "Disabled", message = "RAG is not enabled on this platform." }, _json);

    // ─────────────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "search_knowledge"), Description(
        "Search the knowledge base using semantic similarity. Returns relevant document chunks " +
        "ranked by relevance score. Use this to find information across all indexed documents.")]
    public async Task<string> SearchKnowledge(
        [Description("Natural language search query")] string query,
        [Description("Max results to return (default: 5)")] int? topK = null,
        [Description("Minimum relevance score 0.0-1.0 (default: 0.3)")] float? minScore = null,
        [Description("Filter by domain (e.g. 'golf', 'hospitality')")] string? domain = null,
        [Description("Filter by product")] string? product = null,
        [Description("Filter by content type (e.g. 'markdown', 'code', 'html')")] string? contentType = null,
        [Description("Filter by specific source ID")] string? sourceId = null)
    {
        if (!_opts.Enabled) return Disabled();
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var retriever = scope.ServiceProvider.GetRequiredService<IKnowledgeRetriever>();

            var filter = new KnowledgeFilter
            {
                TenantId = ctx.TenantId,
                TopK = topK ?? _opts.DefaultTopK,
                MinScore = minScore ?? _opts.DefaultMinScore,
                Domain = domain,
                Product = product,
                ContentType = contentType,
                SourceId = sourceId,
            };

            var result = await retriever.RetrieveAsync(query, filter, CancellationToken.None);

            return JsonSerializer.Serialize(new
            {
                query,
                totalResults = result.Chunks.Count,
                chunks = result.Chunks.Select(c => new
                {
                    c.Title,
                    c.SourceUri,
                    text = c.Text.Length > 2000 ? c.Text[..2000] + "…" : c.Text,
                    c.Score,
                    c.Domain,
                    c.Product,
                    c.ContentType,
                    c.DocumentId,
                    c.ChunkIndex,
                }),
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "search_knowledge failed for query '{Query}'", query);
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "list_knowledge_sources"), Description(
        "List all knowledge sources visible to the current tenant. " +
        "Shows source name, type, document count, and last ingestion time.")]
    public async Task<string> ListKnowledgeSources()
    {
        if (!_opts.Enabled) return Disabled();
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sourceService = scope.ServiceProvider.GetRequiredService<KnowledgeSourceService>();
            var sources = await sourceService.ListAsync(ctx.TenantId, ct: CancellationToken.None);

            return JsonSerializer.Serialize(new
            {
                totalSources = sources.Count,
                sources = sources.Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.SourceType,
                    s.ScopeType,
                    s.Status,
                    s.DocumentCount,
                    s.ChunkCount,
                    s.LastIngestedAt,
                }),
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "list_knowledge_sources failed");
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "get_document"), Description(
        "Get details and all chunks for a specific document by its document ID.")]
    public async Task<string> GetDocument(
        [Description("The document ID")] string documentId)
    {
        if (!_opts.Enabled) return Disabled();
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<IDatabaseProviderFactory>().CreateDbContext();

            var doc = await db.KnowledgeDocuments
                .FirstOrDefaultAsync(d => d.DocumentId == documentId);

            if (doc is null)
                return JsonSerializer.Serialize(new { error = "NotFound", message = $"Document '{documentId}' not found" }, _json);

            var chunks = await db.KnowledgeChunks
                .Where(c => c.DocumentId == documentId && !c.IsStale)
                .OrderBy(c => c.ChunkIndex)
                .ToListAsync();

            return JsonSerializer.Serialize(new
            {
                doc.DocumentId,
                doc.Title,
                doc.Uri,
                doc.CurrentVersion,
                doc.IsActive,
                doc.ContentHash,
                doc.LastIndexedAt,
                totalChunks = chunks.Count,
                chunks = chunks.Select(c => new
                {
                    c.Id,
                    c.ChunkIndex,
                    c.TokenCount,
                    c.IsPinned,
                }),
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "get_document failed for '{DocumentId}'", documentId);
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "index_file_to_knowledge"), Description(
        "Index a file or URL into the knowledge base for the current agent. " +
        "Creates an agent-scoped knowledge source if needed, then ingests the content. " +
        "Use this when you discover useful reference material during a conversation.")]
    public async Task<string> IndexFileToKnowledge(
        [Description("File path or URL to index")] string uri,
        [Description("Document title")] string title,
        [Description("Source type: 'file' or 'http'")] string sourceType = "file",
        [Description("Agent ID to scope the knowledge to")] string? agentId = null)
    {
        if (!_opts.Enabled) return Disabled();
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");

        if (string.IsNullOrWhiteSpace(agentId))
            return Error("agentId is required for agent-scoped indexing");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sourceService = scope.ServiceProvider.GetRequiredService<KnowledgeSourceService>();
            var pipeline = scope.ServiceProvider.GetRequiredService<IDocumentIngestionPipeline>();

            var source = await sourceService.GetOrCreateAgentSourceAsync(
                agentId, ctx.TenantId, sourceType, CancellationToken.None);

            // Update config with the target URI
            source.ConfigJson = sourceType == "http"
                ? JsonSerializer.Serialize(new { urls = new[] { uri } })
                : JsonSerializer.Serialize(new { paths = new[] { System.IO.Path.GetDirectoryName(uri) }, includePatterns = new[] { System.IO.Path.GetFileName(uri) } });
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDatabaseProviderFactory>();
            using var db = dbFactory.CreateDbContext();
            db.KnowledgeSources.Update(source);
            await db.SaveChangesAsync();

            await pipeline.IngestAsync(source.Id, ctx.TenantId, uri, ct: CancellationToken.None);

            return JsonSerializer.Serialize(new
            {
                status = "indexed",
                sourceId = source.Id,
                uri,
                title,
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "index_file_to_knowledge failed for '{Uri}'", uri);
            return Error(ex.Message);
        }
    }

    // ── Agent Memory Tools (Phase 26.2) ──────────────────────────────────────

    private string MemoryDisabled() =>
        JsonSerializer.Serialize(new { error = "Disabled", message = "Agent memory is not enabled." }, _json);

    [McpServerTool(Name = "save_memory"), Description(
        "Save information to agent memory for later recall. " +
        "TIER RULES: " +
        "'working' = facts only relevant to THIS session (e.g. 'user wants JSON output', 'table has 2.4M rows'). " +
        "'episodic' = learnings useful in FUTURE sessions (e.g. 'always run VACUUM after migration', 'repo uses NSubstitute not Moq'). " +
        "'semantic' = permanent knowledge — prefer summarize_and_archive instead of direct save. " +
        "Tags help with recall filtering.")]
    public async Task<string> SaveMemory(
        [Description("The text content to remember")] string content,
        [Description("Memory type: 'working' (session), 'episodic' (long-term), 'semantic' (permanent)")] string memoryType = "working",
        [Description("Semantic tags for filtering on recall (comma-separated)")] string? tags = null,
        [Description("Override TTL in minutes (working memory only)")] int? expiresInMinutes = null)
    {
        if (!_opts.EnableAgentMemory) return MemoryDisabled();
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");
        if (string.IsNullOrEmpty(ctx.AgentId)) return Error("Agent ID required — X-Agent-Id header missing");

        if (memoryType is not ("working" or "episodic" or "semantic"))
            return Error("memoryType must be 'working', 'episodic', or 'semantic'");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var memService = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();

            var tagArray = string.IsNullOrWhiteSpace(tags) ? null
                : tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var memoryId = await memService.SaveMemoryAsync(
                ctx.TenantId, ctx.AgentId, content, memoryType,
                sessionId: ctx.SessionId, userId: ctx.UserId,
                tags: tagArray, expiresInMinutes: expiresInMinutes);

            return JsonSerializer.Serialize(new
            {
                status = "saved",
                memoryId = memoryId.ToString(),
                memoryType,
                tags = tagArray ?? Array.Empty<string>(),
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "save_memory failed");
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "recall_memory"), Description(
        "Semantically search stored memories. Returns agent-scoped domain memories AND " +
        "user-scoped preferences/facts for the current user. " +
        "Use to recall past discoveries, task outcomes, learned patterns, or user preferences.")]
    public async Task<string> RecallMemory(
        [Description("Natural language search query")] string query,
        [Description("Filter to specific memory type: 'working', 'episodic', 'semantic', or null for all")] string? memoryType = null,
        [Description("Restrict to current session's working memories only")] bool currentSessionOnly = false,
        [Description("Max results to return (1-10, default 5)")] int maxResults = 5)
    {
        if (!_opts.EnableAgentMemory) return MemoryDisabled();
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");
        if (string.IsNullOrEmpty(ctx.AgentId)) return Error("Agent ID required — X-Agent-Id header missing");

        maxResults = Math.Clamp(maxResults, 1, 10);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var memService = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();

            var memories = await memService.RecallMemoryAsync(
                ctx.TenantId, ctx.AgentId, query,
                memoryType: memoryType, sessionId: ctx.SessionId, userId: ctx.UserId,
                currentSessionOnly: currentSessionOnly, maxResults: maxResults);

            return JsonSerializer.Serialize(new
            {
                query,
                totalResults = memories.Count,
                memories = memories.Select(m => new
                {
                    id = m.Id.ToString(),
                    m.Content,
                    m.MemoryType,
                    m.Tags,
                    m.Score,
                    m.CreatedAt,
                }),
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "recall_memory failed for query '{Query}'", query);
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "forget_memory"), Description(
        "Delete a specific memory by its ID (returned by save_memory). " +
        "Use to correct outdated facts or clear sensitive information.")]
    public async Task<string> ForgetMemory(
        [Description("The memory ID to delete")] string memoryId)
    {
        if (!_opts.EnableAgentMemory) return MemoryDisabled();
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");

        if (!Guid.TryParse(memoryId, out var id))
            return Error("Invalid memory ID format");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var memService = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();

            var deleted = await memService.ForgetMemoryAsync(id);
            return JsonSerializer.Serialize(new
            {
                status = deleted ? "deleted" : "not_found",
                memoryId,
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "forget_memory failed for '{MemoryId}'", memoryId);
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "summarize_and_archive"), Description(
        "Condense all working memories for the current session into a single episodic memory, " +
        "then delete the working memories. Call at end of long-running tasks to distil learnings.")]
    public async Task<string> SummarizeAndArchive(
        [Description("Optional title for the archived memory")] string? archiveTitle = null)
    {
        if (!_opts.EnableAgentMemory) return MemoryDisabled();
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");
        if (string.IsNullOrEmpty(ctx.AgentId)) return Error("Agent ID required");
        if (string.IsNullOrEmpty(ctx.SessionId)) return Error("Session ID required for summarize_and_archive");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var memService = scope.ServiceProvider.GetRequiredService<IAgentMemoryService>();

            // Recall all working memories for this session
            var workingMemories = await memService.RecallMemoryAsync(
                ctx.TenantId, ctx.AgentId, "",
                memoryType: "working", sessionId: ctx.SessionId,
                currentSessionOnly: true, maxResults: 50);

            if (workingMemories.Count == 0)
                return JsonSerializer.Serialize(new { status = "no_memories", message = "No working memories to archive" }, _json);

            // Build a combined text for archiving
            var combined = string.Join("\n- ", workingMemories.Select(m => m.Content));
            var archiveContent = string.IsNullOrWhiteSpace(archiveTitle)
                ? $"Session summary:\n- {combined}"
                : $"{archiveTitle}:\n- {combined}";

            // Save as episodic memory
            var episodicId = await memService.SaveMemoryAsync(
                ctx.TenantId, ctx.AgentId, archiveContent, "episodic",
                tags: ["archived", "session-summary"]);

            // Clean up working memories for this session
            var cleaned = await memService.CleanupSessionAsync(ctx.SessionId);

            return JsonSerializer.Serialize(new
            {
                status = "archived",
                episodicMemoryId = episodicId.ToString(),
                workingMemoriesArchived = workingMemories.Count,
                workingMemoriesDeleted = cleaned,
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "summarize_and_archive failed");
            return Error(ex.Message);
        }
    }

    // ── User Preference Tools ────────────────────────────────────────────────

    [McpServerTool(Name = "save_user_preference"), Description(
        "Save a structured preference about the current user. " +
        "Use for facts about the PERSON (not the domain): coding style, communication preferences, " +
        "team/role info, output format preferences. These are visible to ALL agents for this user. " +
        "Examples: category='coding_style' key='language' value='TypeScript', " +
        "category='communication' key='verbosity' value='concise', " +
        "category='domain' key='team' value='payments'.")]
    public async Task<string> SaveUserPreference(
        [Description("Category: 'coding_style', 'communication', 'domain', 'output', or custom")] string category,
        [Description("Preference key (e.g. 'language', 'verbosity', 'team')")] string key,
        [Description("Preference value")] string value)
    {
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");
        if (string.IsNullOrEmpty(ctx.UserId)) return Error("User ID required — not available in current auth context");

        if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            return Error("category, key, and value are all required");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var prefService = scope.ServiceProvider.GetRequiredService<IUserPreferenceService>();

            var pref = await prefService.SaveAsync(ctx.TenantId, ctx.UserId, category.Trim(), key.Trim(), value.Trim());

            return JsonSerializer.Serialize(new
            {
                status = "saved",
                preference = new
                {
                    pref.Id,
                    pref.Category,
                    pref.Key,
                    pref.Value,
                },
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "save_user_preference failed");
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "get_user_preferences"), Description(
        "Get all stored preferences for the current user. Returns structured key-value pairs " +
        "grouped by category. Use at the start of a conversation to personalize responses, " +
        "or when the user asks 'what do you know about me?'.")]
    public async Task<string> GetUserPreferences(
        [Description("Optional category filter: 'coding_style', 'communication', 'domain', etc.")] string? category = null)
    {
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");
        if (string.IsNullOrEmpty(ctx.UserId)) return Error("User ID required — not available in current auth context");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var prefService = scope.ServiceProvider.GetRequiredService<IUserPreferenceService>();

            var prefs = await prefService.GetAsync(ctx.TenantId, ctx.UserId, category);

            return JsonSerializer.Serialize(new
            {
                userId = ctx.UserId,
                totalPreferences = prefs.Count,
                preferences = prefs.Select(p => new
                {
                    p.Id,
                    p.Category,
                    p.Key,
                    p.Value,
                    p.UpdatedAt,
                }),
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "get_user_preferences failed");
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "delete_user_preference"), Description(
        "Delete a specific user preference by its ID (returned by get_user_preferences or save_user_preference).")]
    public async Task<string> DeleteUserPreference(
        [Description("The preference ID to delete")] int preferenceId)
    {
        var ctx = McpServerContext.FromHttpContext(http);
        if (!ctx.IsAuthenticated) return Error("Unauthenticated");

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var prefService = scope.ServiceProvider.GetRequiredService<IUserPreferenceService>();

            var deleted = await prefService.DeleteAsync(preferenceId);
            return JsonSerializer.Serialize(new
            {
                status = deleted ? "deleted" : "not_found",
                preferenceId,
            }, _json);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "delete_user_preference failed for ID {Id}", preferenceId);
            return Error(ex.Message);
        }
    }
}
