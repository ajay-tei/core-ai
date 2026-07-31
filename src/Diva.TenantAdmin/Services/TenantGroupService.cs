using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Groups;
using Diva.Infrastructure.LiteLLM;
using Diva.Infrastructure.Scheduler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Diva.TenantAdmin.Services;

/// <summary>
/// Manages tenant groups, shared resources, and LLM configuration at all three levels
/// (platform, group, tenant). Singleton-safe — creates a new DbContext per call.
/// </summary>
public sealed class TenantGroupService : ITenantGroupService
{
    private readonly IDatabaseProviderFactory _db;
    private readonly IGroupMembershipCache _membershipCache;
    private readonly ILlmConfigResolver _llmResolver;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TenantGroupService> _logger;
    private IGroupAgentOverlayService? _overlayService;  // set after DI construction to avoid circular dependency

    public TenantGroupService(
        IDatabaseProviderFactory db,
        IGroupMembershipCache membershipCache,
        ILlmConfigResolver llmResolver,
        IMemoryCache cache,
        ILogger<TenantGroupService> logger)
    {
        _db = db;
        _membershipCache = membershipCache;
        _llmResolver = llmResolver;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Called by Program.cs after DI is built to wire the overlay service without a circular ctor dependency.
    /// </summary>
    public void SetOverlayService(IGroupAgentOverlayService overlayService)
        => _overlayService = overlayService;

    // ── Groups ────────────────────────────────────────────────────────────────

    public async Task<List<TenantGroupEntity>> GetAllGroupsAsync(CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantGroups
            .Include(g => g.Members)
            .OrderBy(g => g.Name)
            .ToListAsync(ct);
    }

    public async Task<TenantGroupEntity?> GetGroupByIdAsync(int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantGroups
            .Include(g => g.Members)
            .Include(g => g.LlmConfigs)
            .FirstOrDefaultAsync(g => g.Id == groupId, ct);
    }

    public async Task<TenantGroupEntity> CreateGroupAsync(CreateGroupDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var group = new TenantGroupEntity
        {
            Name = dto.Name,
            Description = dto.Description,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.TenantGroups.Add(group);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Created tenant group '{Name}' (Id={Id})", group.Name, group.Id);
        return group;
    }

    public async Task<TenantGroupEntity> UpdateGroupAsync(int groupId, UpdateGroupDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var group = await db.TenantGroups.FindAsync([groupId], ct)
            ?? throw new KeyNotFoundException($"Group {groupId} not found.");

        group.Name = dto.Name;
        group.Description = dto.Description;
        group.IsActive = dto.IsActive;

        await db.SaveChangesAsync(ct);
        await _membershipCache.InvalidateForGroupAsync(groupId, ct);
        return group;
    }

    public async Task DeleteGroupAsync(int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var group = await db.TenantGroups.FindAsync([groupId], ct)
            ?? throw new KeyNotFoundException($"Group {groupId} not found.");

        db.TenantGroups.Remove(group);
        await db.SaveChangesAsync(ct);
        await _membershipCache.InvalidateForGroupAsync(groupId, ct);
        InvalidateGroupCache(groupId);
        _logger.LogInformation("Deleted tenant group {GroupId}", groupId);
    }

    // ── Members ───────────────────────────────────────────────────────────────

    public async Task<List<TenantGroupMemberEntity>> GetMembersAsync(int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantGroupMembers
            .Where(m => m.GroupId == groupId)
            .OrderBy(m => m.JoinedAt)
            .ToListAsync(ct);
    }

    public async Task<TenantGroupMemberEntity> AddMemberAsync(int groupId, int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var exists = await db.TenantGroupMembers
            .AnyAsync(m => m.GroupId == groupId && m.TenantId == tenantId, ct);
        if (exists) throw new InvalidOperationException($"Tenant {tenantId} is already a member of group {groupId}.");

        var member = new TenantGroupMemberEntity
        {
            GroupId = groupId,
            TenantId = tenantId,
            JoinedAt = DateTime.UtcNow,
        };
        db.TenantGroupMembers.Add(member);
        await db.SaveChangesAsync(ct);
        _membershipCache.InvalidateForTenant(tenantId);
        _llmResolver.InvalidateForTenant(tenantId);
        _logger.LogInformation("Added tenant {TenantId} to group {GroupId}", tenantId, groupId);
        return member;
    }

    public async Task RemoveMemberAsync(int groupId, int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var member = await db.TenantGroupMembers
            .FirstOrDefaultAsync(m => m.GroupId == groupId && m.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException($"Tenant {tenantId} is not a member of group {groupId}.");

        db.TenantGroupMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        _membershipCache.InvalidateForTenant(tenantId);
        _llmResolver.InvalidateForTenant(tenantId);
    }

    // ── Agent templates ───────────────────────────────────────────────────────

    public async Task<List<GroupAgentTemplateEntity>> GetAgentTemplatesAsync(int groupId, CancellationToken ct)
    {
        var key = AgentsCacheKey(groupId);
        if (_cache.TryGetValue(key, out List<GroupAgentTemplateEntity>? cached) && cached is not null)
            return cached;

        using var db = _db.CreateDbContext();
        var templates = await db.GroupAgentTemplates
            .Where(a => a.GroupId == groupId)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);

        _cache.Set(key, templates, TimeSpan.FromMinutes(5));
        return templates;
    }

    public async Task<GroupAgentTemplateEntity?> GetAgentTemplateAsync(int groupId, string templateId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.GroupAgentTemplates
            .FirstOrDefaultAsync(a => a.GroupId == groupId && a.Id == templateId, ct);
    }

    public async Task<GroupAgentTemplateEntity> CreateAgentTemplateAsync(int groupId, CreateGroupAgentDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = new GroupAgentTemplateEntity
        {
            Id = Guid.NewGuid().ToString(),
            GroupId = groupId,
            Name = dto.Name,
            DisplayName = dto.DisplayName,
            Description = dto.Description,
            AgentType = dto.AgentType,
            SystemPrompt = dto.SystemPrompt,
            ModelId = dto.ModelId,
            Temperature = dto.Temperature,
            MaxIterations = dto.MaxIterations,
            Capabilities = dto.Capabilities,
            ToolBindings = dto.ToolBindings,
            VerificationMode = dto.VerificationMode,
            ContextWindowJson = dto.ContextWindowJson,
            CustomVariablesJson = dto.CustomVariablesJson,
            MaxContinuations = dto.MaxContinuations,
            MaxToolResultChars = dto.MaxToolResultChars,
            MaxOutputTokens = dto.MaxOutputTokens,
            EnableHistoryCaching = dto.EnableHistoryCaching,
            PipelineStagesJson = dto.PipelineStagesJson,
            ToolFilterJson = dto.ToolFilterJson,
            StageInstructionsJson = dto.StageInstructionsJson,
            IsEnabled = dto.IsEnabled,
            Status = dto.Status,
            ArchetypeId = dto.ArchetypeId,
            HooksJson = dto.HooksJson,
            A2AEndpoint = dto.A2AEndpoint,
            A2AAuthScheme = dto.A2AAuthScheme,
            A2ASecretRef = dto.A2ASecretRef,
            ExecutionMode = dto.ExecutionMode,
            ModelSwitchingJson = dto.ModelSwitchingJson,
            LlmConfigId = dto.LlmConfigId,
        };
        db.GroupAgentTemplates.Add(entity);
        await db.SaveChangesAsync(ct);
        _cache.Remove(AgentsCacheKey(groupId));
        return entity;
    }

    public async Task<GroupAgentTemplateEntity> UpdateAgentTemplateAsync(int groupId, string templateId, UpdateGroupAgentDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.GroupAgentTemplates
            .FirstOrDefaultAsync(a => a.GroupId == groupId && a.Id == templateId, ct)
            ?? throw new KeyNotFoundException($"Agent template '{templateId}' not found in group {groupId}.");

        entity.Name = dto.Name;
        entity.DisplayName = dto.DisplayName;
        entity.Description = dto.Description;
        entity.SystemPrompt = dto.SystemPrompt;
        entity.ModelId = dto.ModelId;
        entity.Temperature = dto.Temperature;
        entity.MaxIterations = dto.MaxIterations;
        entity.Capabilities = dto.Capabilities;
        entity.ToolBindings = dto.ToolBindings;
        entity.VerificationMode = dto.VerificationMode;
        entity.ContextWindowJson = dto.ContextWindowJson;
        entity.CustomVariablesJson = dto.CustomVariablesJson;
        entity.MaxContinuations = dto.MaxContinuations;
        entity.MaxToolResultChars = dto.MaxToolResultChars;
        entity.MaxOutputTokens = dto.MaxOutputTokens;
        entity.EnableHistoryCaching = dto.EnableHistoryCaching;
        entity.PipelineStagesJson = dto.PipelineStagesJson;
        entity.ToolFilterJson = dto.ToolFilterJson;
        entity.StageInstructionsJson = dto.StageInstructionsJson;
        entity.IsEnabled = dto.IsEnabled;
        entity.Status = dto.Status;
        entity.ArchetypeId = dto.ArchetypeId;
        entity.HooksJson = dto.HooksJson;
        entity.A2AEndpoint = dto.A2AEndpoint;
        entity.A2AAuthScheme = dto.A2AAuthScheme;
        entity.A2ASecretRef = dto.A2ASecretRef;
        entity.ExecutionMode = dto.ExecutionMode;
        entity.ModelSwitchingJson = dto.ModelSwitchingJson;
        entity.LlmConfigId = dto.LlmConfigId;

        await db.SaveChangesAsync(ct);
        _cache.Remove(AgentsCacheKey(groupId));

        // Propagate: flush overlay caches for all member tenants so merged prompts are refreshed
        if (_overlayService is not null)
        {
            var memberIds = await GetMemberTenantIdsAsync(groupId, ct);
            foreach (var memberId in memberIds)
                _overlayService.InvalidateCache(memberId);
        }

        return entity;
    }

    public async Task DeleteAgentTemplateAsync(int groupId, string templateId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.GroupAgentTemplates
            .FirstOrDefaultAsync(a => a.GroupId == groupId && a.Id == templateId, ct)
            ?? throw new KeyNotFoundException($"Agent template '{templateId}' not found in group {groupId}.");

        db.GroupAgentTemplates.Remove(entity);
        await db.SaveChangesAsync(ct);
        _cache.Remove(AgentsCacheKey(groupId));
    }

    // ── Business rules ────────────────────────────────────────────────────────

    public async Task<List<GroupBusinessRuleEntity>> GetBusinessRulesAsync(int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.GroupBusinessRules
            .Where(r => r.GroupId == groupId)
            .OrderBy(r => r.Priority).ThenBy(r => r.RuleCategory)
            .ToListAsync(ct);
    }

    public async Task<GroupBusinessRuleEntity> CreateBusinessRuleAsync(int groupId, CreateGroupRuleDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = new GroupBusinessRuleEntity
        {
            GroupId = groupId,
            AgentType = dto.AgentType,
            RuleCategory = dto.RuleCategory,
            RuleKey = dto.RuleKey,
            PromptInjection = dto.PromptInjection,
            RuleValueJson = dto.RuleValueJson,
            Priority = dto.Priority,
            IsActive = true,
            IsTemplate = dto.IsTemplate,
            HookPoint = dto.HookPoint,
            HookRuleType = dto.HookRuleType,
            Pattern = dto.Pattern,
            Replacement = dto.Replacement,
            ToolName = dto.ToolName,
            OrderInPack = dto.OrderInPack,
            StopOnMatch = dto.StopOnMatch,
            MaxEvaluationMs = dto.MaxEvaluationMs,
        };
        db.GroupBusinessRules.Add(entity);
        await db.SaveChangesAsync(ct);
        InvalidateGroupCache(groupId, dto.AgentType);
        return entity;
    }

    public async Task<GroupBusinessRuleEntity> UpdateBusinessRuleAsync(int groupId, int ruleId, UpdateGroupRuleDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.GroupBusinessRules
            .FirstOrDefaultAsync(r => r.GroupId == groupId && r.Id == ruleId, ct)
            ?? throw new KeyNotFoundException($"Rule {ruleId} not found in group {groupId}.");

        entity.RuleCategory = dto.RuleCategory;
        entity.RuleKey = dto.RuleKey;
        entity.PromptInjection = dto.PromptInjection;
        entity.RuleValueJson = dto.RuleValueJson;
        entity.IsActive = dto.IsActive;
        entity.Priority = dto.Priority;
        entity.IsTemplate = dto.IsTemplate;
        entity.HookPoint = dto.HookPoint;
        entity.HookRuleType = dto.HookRuleType;
        entity.Pattern = dto.Pattern;
        entity.Replacement = dto.Replacement;
        entity.ToolName = dto.ToolName;
        entity.OrderInPack = dto.OrderInPack;
        entity.StopOnMatch = dto.StopOnMatch;
        entity.MaxEvaluationMs = dto.MaxEvaluationMs;

        await db.SaveChangesAsync(ct);
        InvalidateGroupCache(groupId, entity.AgentType);
        return entity;
    }

    public async Task DeleteBusinessRuleAsync(int groupId, int ruleId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.GroupBusinessRules
            .FirstOrDefaultAsync(r => r.GroupId == groupId && r.Id == ruleId, ct)
            ?? throw new KeyNotFoundException($"Rule {ruleId} not found in group {groupId}.");

        db.GroupBusinessRules.Remove(entity);
        await db.SaveChangesAsync(ct);
        InvalidateGroupCache(groupId, entity.AgentType);
    }

    // ── Prompt overrides ──────────────────────────────────────────────────────

    public async Task<List<GroupPromptOverrideEntity>> GetPromptOverridesAsync(int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.GroupPromptOverrides
            .Where(o => o.GroupId == groupId)
            .OrderBy(o => o.Section)
            .ToListAsync(ct);
    }

    public async Task<GroupPromptOverrideEntity> CreatePromptOverrideAsync(int groupId, CreateGroupPromptOverrideDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = new GroupPromptOverrideEntity
        {
            GroupId = groupId,
            AgentType = dto.AgentType,
            Section = dto.Section,
            CustomText = dto.CustomText,
            MergeMode = dto.MergeMode,
            IsActive = true,
            IsTemplate = dto.IsTemplate,
        };
        db.GroupPromptOverrides.Add(entity);
        await db.SaveChangesAsync(ct);
        InvalidateGroupCache(groupId, dto.AgentType);
        return entity;
    }

    public async Task<GroupPromptOverrideEntity> UpdatePromptOverrideAsync(int groupId, int overrideId, UpdateGroupPromptOverrideDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.GroupPromptOverrides
            .FirstOrDefaultAsync(o => o.GroupId == groupId && o.Id == overrideId, ct)
            ?? throw new KeyNotFoundException($"Override {overrideId} not found in group {groupId}.");

        entity.CustomText = dto.CustomText;
        entity.MergeMode = dto.MergeMode;
        entity.IsTemplate = dto.IsTemplate;
        if (dto.IsActive.HasValue)
            entity.IsActive = dto.IsActive.Value;

        await db.SaveChangesAsync(ct);
        InvalidateGroupCache(groupId, entity.AgentType);
        return entity;
    }

    public async Task DeletePromptOverrideAsync(int groupId, int overrideId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.GroupPromptOverrides
            .FirstOrDefaultAsync(o => o.GroupId == groupId && o.Id == overrideId, ct)
            ?? throw new KeyNotFoundException($"Override {overrideId} not found in group {groupId}.");

        db.GroupPromptOverrides.Remove(entity);
        await db.SaveChangesAsync(ct);
        InvalidateGroupCache(groupId, entity.AgentType);
    }

    // ── Scheduled tasks ───────────────────────────────────────────────────────

    public async Task<List<GroupScheduledTaskEntity>> GetScheduledTasksAsync(int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.GroupScheduledTasks
            .Where(t => t.GroupId == groupId)
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<GroupScheduledTaskEntity?> GetScheduledTaskAsync(int groupId, string taskId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.GroupScheduledTasks
            .FirstOrDefaultAsync(t => t.GroupId == groupId && t.Id == taskId, ct);
    }

    public async Task<GroupScheduledTaskEntity> CreateScheduledTaskAsync(int groupId, CreateGroupTaskDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = new GroupScheduledTaskEntity
        {
            Id = Guid.NewGuid().ToString(),
            GroupId = groupId,
            AgentType = dto.AgentType,
            Name = dto.Name,
            Description = dto.Description,
            ScheduleType = dto.ScheduleType,
            ScheduledAtUtc = dto.ScheduledAtUtc,
            RunAtTime = dto.RunAtTime,
            DayOfWeek = dto.DayOfWeek,
            TimeZoneId = dto.TimeZoneId,
            PayloadType = dto.PayloadType,
            PromptText = dto.PromptText,
            ParametersJson = dto.ParametersJson,
            IsEnabled = dto.IsEnabled,
        };
        if (dto.IsEnabled)
            entity.NextRunUtc = ScheduledTaskService.ComputeNextRunUtc(
                dto.ScheduleType, dto.ScheduledAtUtc, dto.RunAtTime,
                dto.DayOfWeek, dto.TimeZoneId, DateTime.UtcNow);

        db.GroupScheduledTasks.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<GroupScheduledTaskEntity> UpdateScheduledTaskAsync(int groupId, string taskId, UpdateGroupTaskDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.GroupScheduledTasks
            .FirstOrDefaultAsync(t => t.GroupId == groupId && t.Id == taskId, ct)
            ?? throw new KeyNotFoundException($"Group task '{taskId}' not found in group {groupId}.");

        if (dto.AgentType is not null) entity.AgentType = dto.AgentType;
        if (dto.Name is not null) entity.Name = dto.Name;
        if (dto.Description is not null) entity.Description = dto.Description;
        if (dto.ScheduleType is not null) entity.ScheduleType = dto.ScheduleType;
        if (dto.ScheduledAtUtc is not null) entity.ScheduledAtUtc = dto.ScheduledAtUtc;
        if (dto.RunAtTime is not null) entity.RunAtTime = dto.RunAtTime;
        if (dto.DayOfWeek is not null) entity.DayOfWeek = dto.DayOfWeek;
        if (dto.TimeZoneId is not null) entity.TimeZoneId = dto.TimeZoneId;
        if (dto.PayloadType is not null) entity.PayloadType = dto.PayloadType;
        if (dto.PromptText is not null) entity.PromptText = dto.PromptText;
        if (dto.ParametersJson is not null) entity.ParametersJson = dto.ParametersJson;
        if (dto.IsEnabled is not null) entity.IsEnabled = dto.IsEnabled.Value;

        entity.NextRunUtc = entity.IsEnabled
            ? ScheduledTaskService.ComputeNextRunUtc(
                entity.ScheduleType, entity.ScheduledAtUtc, entity.RunAtTime,
                entity.DayOfWeek, entity.TimeZoneId, DateTime.UtcNow)
            : null;

        await db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task DeleteScheduledTaskAsync(int groupId, string taskId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.GroupScheduledTasks
            .FirstOrDefaultAsync(t => t.GroupId == groupId && t.Id == taskId, ct)
            ?? throw new KeyNotFoundException($"Group task '{taskId}' not found in group {groupId}.");

        db.GroupScheduledTasks.Remove(entity);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetScheduledTaskEnabledAsync(int groupId, string taskId, bool isEnabled, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var entity = await db.GroupScheduledTasks
            .FirstOrDefaultAsync(t => t.GroupId == groupId && t.Id == taskId, ct)
            ?? throw new KeyNotFoundException($"Group task '{taskId}' not found in group {groupId}.");

        entity.IsEnabled = isEnabled;
        entity.NextRunUtc = isEnabled
            ? ScheduledTaskService.ComputeNextRunUtc(
                entity.ScheduleType, entity.ScheduledAtUtc, entity.RunAtTime,
                entity.DayOfWeek, entity.TimeZoneId, DateTime.UtcNow)
            : null;

        await db.SaveChangesAsync(ct);
    }

    // ── Group LLM config ──────────────────────────────────────────────────────

    public async Task<GroupLlmConfigEntity?> GetGroupLlmConfigAsync(int groupId, CancellationToken ct)
    {
        var key = GroupLlmCacheKey(groupId);
        if (_cache.TryGetValue(key, out GroupLlmConfigEntity? cached)) return cached;

        using var db = _db.CreateDbContext();
        var config = await db.GroupLlmConfigs.FirstOrDefaultAsync(c => c.GroupId == groupId && c.Name == null, ct);
        if (config is not null) _cache.Set(key, config, TimeSpan.FromMinutes(5));
        return config;
    }

    public async Task<GroupLlmConfigEntity> UpsertGroupLlmConfigAsync(int groupId, UpsertLlmConfigDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.GroupLlmConfigs.FirstOrDefaultAsync(c => c.GroupId == groupId && c.Name == null, ct);
        if (config is null)
        {
            config = new GroupLlmConfigEntity { GroupId = groupId, Name = null };
            db.GroupLlmConfigs.Add(config);
        }

        ApplyLlmConfigDto(config, dto);
        await db.SaveChangesAsync(ct);
        _cache.Remove(GroupLlmCacheKey(groupId));
        await InvalidateGroupMemberResolversAsync(groupId, db, ct);
        return config;
    }

    public async Task<List<GroupLlmConfigEntity>> ListGroupLlmConfigsAsync(int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.GroupLlmConfigs
            .Where(c => c.GroupId == groupId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<GroupLlmConfigEntity?> GetGroupLlmConfigByIdAsync(int configId, int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.GroupLlmConfigs.FirstOrDefaultAsync(c => c.Id == configId && c.GroupId == groupId, ct);
    }

    public async Task<GroupLlmConfigEntity> CreateGroupLlmConfigAsync(int groupId, CreateNamedLlmConfigDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = new GroupLlmConfigEntity
        {
            GroupId = groupId,
            Name = dto.Name,
            Provider = dto.Provider,
            ApiKey = dto.ApiKey,
            Model = dto.Model,
            Endpoint = dto.Endpoint,
            DeploymentName = dto.DeploymentName,
            AvailableModelsJson = dto.AvailableModelsJson,
            EnvironmentId = dto.EnvironmentId,
        };
        db.GroupLlmConfigs.Add(config);
        await db.SaveChangesAsync(ct);
        if (dto.Name is null) _cache.Remove(GroupLlmCacheKey(groupId));
        await InvalidateGroupMemberResolversAsync(groupId, db, ct);
        return config;
    }

    public async Task<GroupLlmConfigEntity> UpdateGroupLlmConfigByIdAsync(int configId, int groupId, UpsertLlmConfigDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.GroupLlmConfigs.FirstOrDefaultAsync(c => c.Id == configId && c.GroupId == groupId, ct)
            ?? throw new KeyNotFoundException($"Group LLM config {configId} not found in group {groupId}.");

        ApplyLlmConfigDto(config, dto);
        await db.SaveChangesAsync(ct);
        if (config.Name is null) _cache.Remove(GroupLlmCacheKey(groupId));
        await InvalidateGroupMemberResolversAsync(groupId, db, ct);
        return config;
    }

    public async Task DeleteGroupLlmConfigByIdAsync(int configId, int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.GroupLlmConfigs.FirstOrDefaultAsync(c => c.Id == configId && c.GroupId == groupId, ct)
            ?? throw new KeyNotFoundException($"Group LLM config {configId} not found in group {groupId}.");

        db.GroupLlmConfigs.Remove(config);
        await db.SaveChangesAsync(ct);
        if (config.Name is null) _cache.Remove(GroupLlmCacheKey(groupId));
        await InvalidateGroupMemberResolversAsync(groupId, db, ct);
    }

    // ── Platform LLM config ───────────────────────────────────────────────────

    public async Task<PlatformLlmConfigEntity?> GetPlatformLlmConfigAsync(CancellationToken ct)
    {
        const string key = "platform_llm";
        if (_cache.TryGetValue(key, out PlatformLlmConfigEntity? cached)) return cached;

        using var db = _db.CreateDbContext();
        var config = await db.PlatformLlmConfigs.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        if (config is not null) _cache.Set(key, config, TimeSpan.FromMinutes(5));
        return config;
    }

    public async Task<PlatformLlmConfigEntity> UpsertPlatformLlmConfigAsync(UpsertLlmConfigDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.PlatformLlmConfigs.OrderBy(p => p.Id).FirstOrDefaultAsync(ct);
        if (config is null)
        {
            config = new PlatformLlmConfigEntity { Name = "Default" };
            db.PlatformLlmConfigs.Add(config);
        }

        if (dto.Provider is not null) config.Provider = dto.Provider;
        if (dto.ApiKey is not null) config.ApiKey = dto.ApiKey;
        if (dto.Model is not null) config.Model = dto.Model;
        if (dto.Endpoint is not null) config.Endpoint = dto.Endpoint;
        if (dto.DeploymentName is not null) config.DeploymentName = dto.DeploymentName;
        if (dto.AvailableModelsJson is not null) config.AvailableModelsJson = dto.AvailableModelsJson;

        await db.SaveChangesAsync(ct);
        _cache.Remove("platform_llm");
        _llmResolver.InvalidatePlatform();
        return config;
    }

    public async Task<List<PlatformLlmConfigEntity>> ListPlatformLlmConfigsAsync(CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.PlatformLlmConfigs.OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<PlatformLlmConfigEntity> CreatePlatformLlmConfigAsync(CreatePlatformLlmConfigDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = new PlatformLlmConfigEntity
        {
            Name = dto.Name,
            Provider = dto.Provider ?? "Anthropic",
            ApiKey = dto.ApiKey ?? string.Empty,
            Model = dto.Model ?? "claude-sonnet-4-20250514",
            Endpoint = dto.Endpoint,
            DeploymentName = dto.DeploymentName,
            AvailableModelsJson = dto.AvailableModelsJson,
        };
        db.PlatformLlmConfigs.Add(config);
        await db.SaveChangesAsync(ct);
        _cache.Remove("platform_llm");
        _llmResolver.InvalidatePlatform();
        _logger.LogInformation("Created platform LLM config '{Name}' (Id={Id})", config.Name, config.Id);
        return config;
    }

    public async Task<PlatformLlmConfigEntity> UpdatePlatformLlmConfigAsync(int id, UpsertLlmConfigDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.PlatformLlmConfigs.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Platform LLM config {id} not found.");

        if (dto.Provider is not null) config.Provider = dto.Provider;
        if (dto.ApiKey is not null) config.ApiKey = dto.ApiKey;
        if (dto.Model is not null) config.Model = dto.Model;
        if (dto.Endpoint is not null) config.Endpoint = dto.Endpoint;
        if (dto.DeploymentName is not null) config.DeploymentName = dto.DeploymentName;
        if (dto.AvailableModelsJson is not null) config.AvailableModelsJson = dto.AvailableModelsJson;

        await db.SaveChangesAsync(ct);
        _cache.Remove("platform_llm");
        _llmResolver.InvalidatePlatform();
        return config;
    }

    public async Task DeletePlatformLlmConfigAsync(int id, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.PlatformLlmConfigs.FindAsync([id], ct)
            ?? throw new KeyNotFoundException($"Platform LLM config {id} not found.");

        db.PlatformLlmConfigs.Remove(config);
        await db.SaveChangesAsync(ct);
        _cache.Remove("platform_llm");
        _llmResolver.InvalidatePlatform();
        _logger.LogInformation("Deleted platform LLM config {Id}", id);
    }

    public async Task<GroupLlmConfigEntity> AddGroupPlatformRefAsync(int groupId, AddGroupPlatformRefDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var platform = await db.PlatformLlmConfigs.FindAsync([dto.PlatformConfigId], ct)
            ?? throw new KeyNotFoundException($"Platform LLM config {dto.PlatformConfigId} not found.");

        var name = dto.NameOverride ?? platform.Name;
        var config = new GroupLlmConfigEntity
        {
            GroupId = groupId,
            Name = name,
            PlatformConfigRef = dto.PlatformConfigId,
        };
        db.GroupLlmConfigs.Add(config);
        await db.SaveChangesAsync(ct);
        _cache.Remove(GroupLlmCacheKey(groupId));
        await InvalidateGroupMemberResolversAsync(groupId, db, ct);
        _logger.LogInformation("Group {GroupId} added platform ref to config {PlatformId} as '{Name}'", groupId, dto.PlatformConfigId, name);
        return config;
    }

    // ── Tenant LLM config ─────────────────────────────────────────────────────

    public async Task<TenantLlmConfigEntity?> GetTenantLlmConfigAsync(int tenantId, CancellationToken ct)
    {
        var key = TenantLlmCacheKey(tenantId);
        if (_cache.TryGetValue(key, out TenantLlmConfigEntity? cached)) return cached;

        using var db = _db.CreateDbContext();
        var config = await db.TenantLlmConfigs.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name == null, ct);
        if (config is not null) _cache.Set(key, config, TimeSpan.FromMinutes(5));
        return config;
    }

    public async Task<TenantLlmConfigEntity> UpsertTenantLlmConfigAsync(int tenantId, UpsertLlmConfigDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.TenantLlmConfigs.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name == null, ct);
        if (config is null)
        {
            config = new TenantLlmConfigEntity { TenantId = tenantId, Name = null };
            db.TenantLlmConfigs.Add(config);
        }

        ApplyLlmConfigDto(config, dto);
        await db.SaveChangesAsync(ct);
        _cache.Remove(TenantLlmCacheKey(tenantId));
        _llmResolver.InvalidateForTenant(tenantId);
        return config;
    }

    public async Task DeleteTenantLlmConfigAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.TenantLlmConfigs.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Name == null, ct);
        if (config is null) return;

        db.TenantLlmConfigs.Remove(config);
        await db.SaveChangesAsync(ct);
        _cache.Remove(TenantLlmCacheKey(tenantId));
        _llmResolver.InvalidateForTenant(tenantId);
    }

    public async Task<List<TenantLlmConfigEntity>> ListTenantLlmConfigsAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantLlmConfigs
            .Where(c => c.TenantId == tenantId)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);
    }

    public async Task<TenantLlmConfigEntity?> GetTenantLlmConfigByIdAsync(int configId, int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantLlmConfigs.FirstOrDefaultAsync(c => c.Id == configId && c.TenantId == tenantId, ct);
    }

    public async Task<TenantLlmConfigEntity> CreateTenantLlmConfigAsync(int tenantId, CreateNamedLlmConfigDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = new TenantLlmConfigEntity
        {
            TenantId = tenantId,
            Name = dto.Name,
            Provider = dto.Provider,
            ApiKey = dto.ApiKey,
            Model = dto.Model,
            Endpoint = dto.Endpoint,
            DeploymentName = dto.DeploymentName,
            AvailableModelsJson = dto.AvailableModelsJson,
            EnvironmentId = dto.EnvironmentId,
        };
        db.TenantLlmConfigs.Add(config);
        await db.SaveChangesAsync(ct);
        if (dto.Name is null) _cache.Remove(TenantLlmCacheKey(tenantId));
        _llmResolver.InvalidateForTenant(tenantId);
        return config;
    }

    public async Task<TenantLlmConfigEntity> UpdateTenantLlmConfigByIdAsync(int configId, int tenantId, UpsertLlmConfigDto dto, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.TenantLlmConfigs.FirstOrDefaultAsync(c => c.Id == configId && c.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException($"Tenant LLM config {configId} not found for tenant {tenantId}.");

        ApplyLlmConfigDto(config, dto);
        await db.SaveChangesAsync(ct);
        if (config.Name is null) _cache.Remove(TenantLlmCacheKey(tenantId));
        _llmResolver.InvalidateForTenant(tenantId);
        return config;
    }

    public async Task DeleteTenantLlmConfigByIdAsync(int configId, int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var config = await db.TenantLlmConfigs.FirstOrDefaultAsync(c => c.Id == configId && c.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException($"Tenant LLM config {configId} not found for tenant {tenantId}.");

        db.TenantLlmConfigs.Remove(config);
        await db.SaveChangesAsync(ct);
        if (config.Name is null) _cache.Remove(TenantLlmCacheKey(tenantId));
        _llmResolver.InvalidateForTenant(tenantId);
    }

    public async Task<List<AvailableLlmConfigDto>> ListAvailableLlmConfigsForTenantAsync(int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        // Tenant's own named configs (unnamed default is auto-applied via hierarchy, not pinnable)
        var tenantConfigs = await db.TenantLlmConfigs
            .Where(c => c.TenantId == tenantId && c.Name != null)
            .ToListAsync(ct);

        var result = tenantConfigs
            .Select(c => new AvailableLlmConfigDto(
                c.Id, "tenant", c.Name,
                DisplayName: c.Name!,
                c.Provider, c.Model,
                AvailableModels: ParseModels(c.AvailableModelsJson)))
            .ToList();

        // Group-level configs (both named AND the unnamed default) for all active groups this tenant belongs to
        var groupIds = await _membershipCache.GetGroupIdsForTenantAsync(tenantId, ct);
        if (groupIds.Count > 0)
        {
            var groupConfigs = await db.GroupLlmConfigs
                .Where(c => groupIds.Contains(c.GroupId))
                .Include(c => c.Group)
                .Include(c => c.PlatformConfig)
                .ToListAsync(ct);

            result.AddRange(groupConfigs.Select(c =>
            {
                var isRef = c.PlatformConfig is not null;
                // When it's a reference, show the platform config's provider/model; otherwise use own fields
                var provider = isRef ? c.PlatformConfig!.Provider : c.Provider;
                var model = isRef ? c.PlatformConfig!.Model : c.Model;
                var models = isRef
                    ? ParseModels(c.PlatformConfig!.AvailableModelsJson)
                    : ParseModels(c.AvailableModelsJson);

                return new AvailableLlmConfigDto(
                    c.Id, $"group:{c.GroupId}", c.Name,
                    DisplayName: c.Name ?? $"Default — {c.Group.Name}",
                    provider, model, models,
                    IsRef: isRef, RefSource: isRef ? "platform" : null);
            }));
        }

        return result;
    }

    // ── Runtime helpers ───────────────────────────────────────────────────────

    public async Task<List<GroupBusinessRuleEntity>> GetActiveRulesForTenantAsync(int tenantId, string agentType, CancellationToken ct)
    {
        var key = RulesCacheKey(tenantId, agentType);
        if (_cache.TryGetValue(key, out List<GroupBusinessRuleEntity>? cached) && cached is not null)
            return cached;

        var groupIds = await _membershipCache.GetGroupIdsForTenantAsync(tenantId, ct);
        if (groupIds.Count == 0) return [];

        using var db = _db.CreateDbContext();
        var rules = await db.GroupBusinessRules
            .Where(r => groupIds.Contains(r.GroupId) && r.IsActive)
            .Where(r => r.AgentType == agentType || r.AgentType == "*")
            .OrderBy(r => r.Priority).ThenBy(r => r.RuleCategory)
            .ToListAsync(ct);

        _cache.Set(key, rules, TimeSpan.FromMinutes(5));
        return rules;
    }

    public async Task<List<GroupPromptOverrideEntity>> GetActiveOverridesForTenantAsync(int tenantId, string agentType, CancellationToken ct)
    {
        var key = OverridesCacheKey(tenantId, agentType);
        if (_cache.TryGetValue(key, out List<GroupPromptOverrideEntity>? cached) && cached is not null)
            return cached;

        var groupIds = await _membershipCache.GetGroupIdsForTenantAsync(tenantId, ct);
        if (groupIds.Count == 0) return [];

        using var db = _db.CreateDbContext();
        var overrides = await db.GroupPromptOverrides
            .Where(o => groupIds.Contains(o.GroupId) && o.IsActive && !o.IsTemplate)
            .Where(o => o.AgentType == agentType || o.AgentType == "*")
            .OrderBy(o => o.Section)
            .ToListAsync(ct);

        _cache.Set(key, overrides, TimeSpan.FromMinutes(5));
        return overrides;
    }

    public async Task<List<GroupAgentTemplateEntity>> GetAgentTemplatesForTenantAsync(int tenantId, CancellationToken ct)
    {
        var groupIds = await _membershipCache.GetGroupIdsForTenantAsync(tenantId, ct);
        if (groupIds.Count == 0) return [];

        using var db = _db.CreateDbContext();
        return await db.GroupAgentTemplates
            .Where(a => groupIds.Contains(a.GroupId) && a.IsEnabled)
            .Include(a => a.Group)
            .OrderBy(a => a.Name)
            .ToListAsync(ct);
    }

    public async Task<List<int>> GetMemberTenantIdsAsync(int groupId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.TenantGroupMembers
            .Where(m => m.GroupId == groupId)
            .Select(m => m.TenantId)
            .ToListAsync(ct);
    }

    public async Task<List<GroupScheduledTaskEntity>> GetDueGroupTasksAsync(DateTime utcNow, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        return await db.GroupScheduledTasks
            .Where(t => t.IsEnabled && t.NextRunUtc != null && t.NextRunUtc <= utcNow)
            .Include(t => t.Group)
                .ThenInclude(g => g.Members)
            .ToListAsync(ct);
    }

    public void InvalidateGroupCache(int groupId, string? agentType = null)
    {
        _cache.Remove(AgentsCacheKey(groupId));
        _cache.Remove(GroupLlmCacheKey(groupId));

        if (agentType is not null)
        {
            // Invalidate per-tenant merged caches that include this group
            // We don't track per-tenant here, but the per-group caches are the source
            _cache.Remove($"grp_rules_{groupId}_{agentType}");
            _cache.Remove($"grp_rules_{groupId}_*");
            _cache.Remove($"grp_overrides_{groupId}_{agentType}");
            _cache.Remove($"grp_overrides_{groupId}_*");
        }
    }

    // ── Group prompt templates ────────────────────────────────────────────────

    public async Task<List<GroupPromptTemplateDto>> GetAvailableGroupPromptTemplatesAsync(
        int tenantId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var groupIds = await db.TenantGroupMembers
            .Where(m => m.TenantId == tenantId)
            .Select(m => m.GroupId)
            .ToListAsync(ct);

        if (groupIds.Count == 0)
            return [];

        var templates = await db.GroupPromptOverrides
            .Include(o => o.Group)
            .Where(o => groupIds.Contains(o.GroupId) && o.IsTemplate && o.IsActive)
            .OrderBy(o => o.GroupId).ThenBy(o => o.Section)
            .ToListAsync(ct);

        if (templates.Count == 0)
            return [];

        var templateIds = templates.Select(t => t.Id).ToList();
        var activated = await db.PromptOverrides
            .Where(o => o.TenantId == tenantId && o.SourceGroupOverrideId != null
                        && templateIds.Contains(o.SourceGroupOverrideId!.Value) && o.IsActive)
            .Select(o => new { o.SourceGroupOverrideId, o.Id })
            .ToListAsync(ct);

        var activatedMap = activated.ToDictionary(a => a.SourceGroupOverrideId!.Value, a => a.Id);

        return templates.Select(t => new GroupPromptTemplateDto(
            t.Id, t.GroupId, t.Group.Name, t.AgentType,
            t.Section, t.CustomText, t.MergeMode,
            activatedMap.ContainsKey(t.Id), activatedMap.TryGetValue(t.Id, out var rid) ? rid : null
        )).ToList();
    }

    public async Task<TenantPromptOverrideEntity> ActivateGroupPromptTemplateAsync(
        int tenantId, int groupOverrideId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var groupOverride = await db.GroupPromptOverrides
            .Include(o => o.Group)
            .FirstOrDefaultAsync(o => o.Id == groupOverrideId, ct)
            ?? throw new InvalidOperationException($"Group prompt override {groupOverrideId} not found.");

        if (!groupOverride.IsTemplate)
            throw new InvalidOperationException($"Group prompt override {groupOverrideId} is not a template.");

        var isMember = await db.TenantGroupMembers
            .AnyAsync(m => m.GroupId == groupOverride.GroupId && m.TenantId == tenantId, ct);
        if (!isMember)
            throw new InvalidOperationException("Tenant is not a member of the group owning this template.");

        var existing = await db.PromptOverrides
            .FirstOrDefaultAsync(o => o.TenantId == tenantId
                && o.SourceGroupOverrideId == groupOverrideId && o.IsActive, ct);
        if (existing is not null)
            return existing;

        var entity = new TenantPromptOverrideEntity
        {
            TenantId = tenantId,
            AgentType = groupOverride.AgentType,
            Section = groupOverride.Section,
            CustomText = groupOverride.CustomText,
            MergeMode = groupOverride.MergeMode,
            IsActive = true,
            SourceGroupOverrideId = groupOverrideId,
        };
        db.PromptOverrides.Add(entity);
        await db.SaveChangesAsync(ct);

        // Invalidate prompt override caches for this tenant
        _cache.Remove(OverridesCacheKey(tenantId, groupOverride.AgentType));
        _cache.Remove(OverridesCacheKey(tenantId, "*"));
        _logger.LogInformation("Tenant {TenantId} activated group prompt template {OverrideId}", tenantId, groupOverrideId);
        return entity;
    }

    public async Task DeactivateGroupPromptTemplateAsync(
        int tenantId, int groupOverrideId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();

        var overrides = await db.PromptOverrides
            .Where(o => o.TenantId == tenantId && o.SourceGroupOverrideId == groupOverrideId && o.IsActive)
            .ToListAsync(ct);

        if (overrides.Count == 0)
            return;

        foreach (var o in overrides)
            o.IsActive = false;

        await db.SaveChangesAsync(ct);

        foreach (var agentType in overrides.Select(o => o.AgentType).Distinct())
        {
            _cache.Remove(OverridesCacheKey(tenantId, agentType));
            _cache.Remove(OverridesCacheKey(tenantId, "*"));
        }
        _logger.LogInformation("Tenant {TenantId} deactivated group prompt template {OverrideId}", tenantId, groupOverrideId);
    }

    // ── Cache key helpers ─────────────────────────────────────────────────────

    private static string AgentsCacheKey(int groupId) => $"grp_agents_{groupId}";
    private static string GroupLlmCacheKey(int groupId) => $"grp_llm_{groupId}";
    private static string TenantLlmCacheKey(int tenantId) => $"tenant_llm_{tenantId}";
    private static string RulesCacheKey(int tenantId, string at) => $"grp_rules_{tenantId}_{at}";
    private static string OverridesCacheKey(int tenantId, string at) => $"grp_overrides_{tenantId}_{at}";

    // ── Private helpers ───────────────────────────────────────────────────────

    private static IReadOnlyList<string> ParseModels(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch { return []; }
    }

    private static void ApplyLlmConfigDto(GroupLlmConfigEntity config, UpsertLlmConfigDto dto)
    {
        if (dto.Provider is not null) config.Provider = dto.Provider;
        if (dto.ApiKey is not null) config.ApiKey = dto.ApiKey;
        if (dto.Model is not null) config.Model = dto.Model;
        if (dto.Endpoint is not null) config.Endpoint = dto.Endpoint;
        if (dto.DeploymentName is not null) config.DeploymentName = dto.DeploymentName;
        if (dto.AvailableModelsJson is not null) config.AvailableModelsJson = dto.AvailableModelsJson;
        if (dto.EnvironmentId.HasValue) config.EnvironmentId = dto.EnvironmentId;
    }

    private static void ApplyLlmConfigDto(TenantLlmConfigEntity config, UpsertLlmConfigDto dto)
    {
        if (dto.Provider is not null) config.Provider = dto.Provider;
        if (dto.ApiKey is not null) config.ApiKey = dto.ApiKey;
        if (dto.Model is not null) config.Model = dto.Model;
        if (dto.Endpoint is not null) config.Endpoint = dto.Endpoint;
        if (dto.DeploymentName is not null) config.DeploymentName = dto.DeploymentName;
        if (dto.AvailableModelsJson is not null) config.AvailableModelsJson = dto.AvailableModelsJson;
        if (dto.EnvironmentId.HasValue) config.EnvironmentId = dto.EnvironmentId;
    }

    private async Task InvalidateGroupMemberResolversAsync(int groupId, DivaDbContext db, CancellationToken ct)
    {
        var members = await db.TenantGroupMembers
            .Where(m => m.GroupId == groupId)
            .Select(m => m.TenantId)
            .ToListAsync(ct);
        foreach (var tenantId in members)
            _llmResolver.InvalidateForTenant(tenantId);
    }
}
