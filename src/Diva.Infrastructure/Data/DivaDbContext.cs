using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Diva.Infrastructure.Data;

public class DivaDbContext : DbContext
{
    private readonly int _currentTenantId;

    public DivaDbContext(DbContextOptions<DivaDbContext> options, int currentTenantId = 0)
        : base(options)
    {
        _currentTenantId = currentTenantId;
    }

    // ── DbSets ────────────────────────────────────────────────
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<SiteEntity> Sites => Set<SiteEntity>();
    public DbSet<TenantBusinessRuleEntity> BusinessRules => Set<TenantBusinessRuleEntity>();
    public DbSet<TenantPromptOverrideEntity> PromptOverrides => Set<TenantPromptOverrideEntity>();
    public DbSet<AgentDefinitionEntity> AgentDefinitions => Set<AgentDefinitionEntity>();
    public DbSet<AgentSessionEntity> Sessions => Set<AgentSessionEntity>();
    public DbSet<AgentSessionMessageEntity> SessionMessages => Set<AgentSessionMessageEntity>();
    public DbSet<LearnedRuleEntity> LearnedRules => Set<LearnedRuleEntity>();
    public DbSet<ScheduledTaskEntity> ScheduledTasks => Set<ScheduledTaskEntity>();
    public DbSet<ScheduledTaskRunEntity> ScheduledTaskRuns => Set<ScheduledTaskRunEntity>();
    public DbSet<TenantSsoConfigEntity> SsoConfigs => Set<TenantSsoConfigEntity>();
    public DbSet<UserProfileEntity> UserProfiles => Set<UserProfileEntity>();
    public DbSet<LocalUserEntity> LocalUsers => Set<LocalUserEntity>();

    // ── Tenant Groups ─────────────────────────────────────────────────────────
    public DbSet<TenantGroupEntity> TenantGroups => Set<TenantGroupEntity>();
    public DbSet<TenantGroupMemberEntity> TenantGroupMembers => Set<TenantGroupMemberEntity>();
    public DbSet<GroupAgentTemplateEntity> GroupAgentTemplates => Set<GroupAgentTemplateEntity>();
    public DbSet<GroupBusinessRuleEntity> GroupBusinessRules => Set<GroupBusinessRuleEntity>();
    public DbSet<GroupPromptOverrideEntity> GroupPromptOverrides => Set<GroupPromptOverrideEntity>();
    public DbSet<GroupScheduledTaskEntity> GroupScheduledTasks => Set<GroupScheduledTaskEntity>();
    public DbSet<GroupScheduledTaskRunEntity> GroupScheduledTaskRuns => Set<GroupScheduledTaskRunEntity>();
    public DbSet<TenantNotificationSettingsEntity> TenantNotificationSettings => Set<TenantNotificationSettingsEntity>();
    public DbSet<GroupLlmConfigEntity> GroupLlmConfigs => Set<GroupLlmConfigEntity>();
    public DbSet<TenantGroupAgentOverlayEntity> GroupAgentOverlays => Set<TenantGroupAgentOverlayEntity>();

    // ── A2A Task Tracking ─────────────────────────────────────────────────────
    public DbSet<AgentTaskEntity> AgentTasks => Set<AgentTaskEntity>();

    // ── Rule Packs (Phase 16) ─────────────────────────────────────────────────
    public DbSet<HookRulePackEntity> RulePacks => Set<HookRulePackEntity>();
    public DbSet<HookRuleEntity> HookRules => Set<HookRuleEntity>();
    public DbSet<RuleExecutionLogEntity> RuleExecutionLogs => Set<RuleExecutionLogEntity>();

    // ── LLM Config (DB-backed, replaces appsettings.json LLM section) ────────
    public DbSet<PlatformLlmConfigEntity> PlatformLlmConfigs => Set<PlatformLlmConfigEntity>();
    public DbSet<TenantLlmConfigEntity> TenantLlmConfigs => Set<TenantLlmConfigEntity>();

    // ── Phase 17: Agent Setup Assistant History ───────────────────────────────
    public DbSet<AgentPromptHistoryEntity> AgentPromptHistory => Set<AgentPromptHistoryEntity>();
    public DbSet<RulePackHistoryEntity> RulePackHistory => Set<RulePackHistoryEntity>();

    // ── API Key & Credential Vault ────────────────────────────────────────────
    public DbSet<McpCredentialEntity> McpCredentials => Set<McpCredentialEntity>();
    public DbSet<PlatformApiKeyEntity> PlatformApiKeys => Set<PlatformApiKeyEntity>();

    // ── Embeddable Chat Widgets ───────────────────────────────────────────────
    public DbSet<WidgetConfigEntity> WidgetConfigs => Set<WidgetConfigEntity>();

    // ── Phase 24: Agent Optimization ──────────────────────────────────────────
    public DbSet<AgentOptimizationRunEntity> OptimizationRuns => Set<AgentOptimizationRunEntity>();
    public DbSet<AgentOptimizationSuggestionEntity> OptimizationSuggestions => Set<AgentOptimizationSuggestionEntity>();
    public DbSet<AgentOptimizationConfigEntity> OptimizationConfigs => Set<AgentOptimizationConfigEntity>();
    public DbSet<FewShotExampleEntity> FewShotExamples => Set<FewShotExampleEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── Global query filters (tenant isolation) ───────────
        // Applied when _currentTenantId > 0; bypassed when 0 (system/admin context)
        modelBuilder.Entity<TenantBusinessRuleEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);

        modelBuilder.Entity<TenantPromptOverrideEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);

        modelBuilder.Entity<AgentDefinitionEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);

        modelBuilder.Entity<AgentSessionEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);

        modelBuilder.Entity<LearnedRuleEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<ScheduledTaskEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);

        modelBuilder.Entity<AgentTaskEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);

        modelBuilder.Entity<ScheduledTaskRunEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);

        // ── Phase 17: History ────────────────────────────────
        modelBuilder.Entity<AgentPromptHistoryEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<AgentPromptHistoryEntity>()
            .HasIndex(e => new { e.TenantId, e.AgentId, e.Version }).IsUnique();

        modelBuilder.Entity<RulePackHistoryEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<RulePackHistoryEntity>()
            .HasIndex(e => new { e.TenantId, e.PackId, e.Version }).IsUnique();
        // ── Relationships ─────────────────────────────────────
        modelBuilder.Entity<SiteEntity>()
            .HasOne(s => s.Tenant)
            .WithMany(t => t.Sites)
            .HasForeignKey(s => s.TenantId);

        modelBuilder.Entity<AgentSessionMessageEntity>()
            .HasOne(m => m.Session)
            .WithMany(s => s.Messages)
            .HasForeignKey(m => m.SessionId);
        modelBuilder.Entity<ScheduledTaskRunEntity>()
            .HasOne(r => r.ScheduledTask)
            .WithMany()
            .HasForeignKey(r => r.ScheduledTaskId)
            .OnDelete(DeleteBehavior.Cascade);
        // ── Indexes ───────────────────────────────────────────
        modelBuilder.Entity<TenantBusinessRuleEntity>()
            .HasIndex(e => new { e.TenantId, e.AgentType, e.IsActive });
        modelBuilder.Entity<TenantBusinessRuleEntity>()
            .HasIndex(e => e.Guid).IsUnique();
        modelBuilder.Entity<TenantBusinessRuleEntity>()
            .HasIndex(e => new { e.TenantId, e.AgentId, e.IsActive });
        // Composite index covering pack-scoped queries (S3)
        modelBuilder.Entity<TenantBusinessRuleEntity>()
            .HasIndex(e => new { e.TenantId, e.RulePackId });
        modelBuilder.Entity<TenantBusinessRuleEntity>()
            .HasOne(e => e.RulePack)
            .WithMany(p => p.LinkedBusinessRules)
            .HasForeignKey(e => e.RulePackId)
            .OnDelete(DeleteBehavior.SetNull);
        // SQLite forbids function expressions as DEFAULT — GUID is always provided by C# entity initializer.

        modelBuilder.Entity<AgentSessionEntity>()
            .HasIndex(e => new { e.TenantId, e.UserId, e.Status });

        modelBuilder.Entity<LearnedRuleEntity>()
            .HasIndex(e => new { e.TenantId, e.Status });
        // ── Scheduler indexes ─────────────────────────────────────────────────
        modelBuilder.Entity<ScheduledTaskEntity>()
            .HasIndex(e => new { e.TenantId, e.IsEnabled, e.NextRunUtc });

        modelBuilder.Entity<ScheduledTaskRunEntity>()
            .HasIndex(e => new { e.ScheduledTaskId, e.Status });

        modelBuilder.Entity<ScheduledTaskRunEntity>()
            .HasIndex(e => new { e.TenantId, e.CreatedAt });

        // ── SSO Configs ───────────────────────────────────────
        modelBuilder.Entity<TenantSsoConfigEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<TenantSsoConfigEntity>()
            .HasIndex(e => new { e.TenantId, e.Issuer }); // unique per tenant, not globally

        // ── User Profiles ─────────────────────────────────────
        modelBuilder.Entity<UserProfileEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<UserProfileEntity>()
            .HasIndex(e => new { e.TenantId, e.UserId })
            .IsUnique();
        modelBuilder.Entity<UserProfileEntity>()
            .HasIndex(e => new { e.TenantId, e.Email })
            .IsUnique()
            .HasFilter("\"Email\" != ''");
        modelBuilder.Entity<UserProfileEntity>()
            .Property(e => e.Roles)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? Array.Empty<string>());
        modelBuilder.Entity<UserProfileEntity>()
            .Property(e => e.AgentAccess)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? Array.Empty<string>());
        modelBuilder.Entity<UserProfileEntity>()
            .Property(e => e.AgentAccessOverrides)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? Array.Empty<string>());

        // ── AgentDefinition primary key is string ─────────────
        modelBuilder.Entity<AgentDefinitionEntity>()
            .HasKey(e => e.Id);

        // ── TenantGroups ──────────────────────────────────────
        modelBuilder.Entity<TenantGroupMemberEntity>()
            .HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TenantGroupMemberEntity>()
            .HasIndex(e => new { e.GroupId, e.TenantId }).IsUnique();
        modelBuilder.Entity<TenantGroupMemberEntity>()
            .HasIndex(e => e.TenantId);

        modelBuilder.Entity<GroupAgentTemplateEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<GroupAgentTemplateEntity>()
            .HasOne(a => a.Group).WithMany().HasForeignKey(a => a.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GroupAgentTemplateEntity>()
            .HasIndex(e => new { e.GroupId, e.IsEnabled });

        modelBuilder.Entity<GroupBusinessRuleEntity>()
            .HasOne(r => r.Group).WithMany().HasForeignKey(r => r.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GroupBusinessRuleEntity>()
            .HasIndex(e => new { e.GroupId, e.AgentType, e.IsActive });

        modelBuilder.Entity<GroupPromptOverrideEntity>()
            .HasOne(o => o.Group).WithMany().HasForeignKey(o => o.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GroupPromptOverrideEntity>()
            .HasIndex(e => new { e.GroupId, e.AgentType, e.IsActive });

        modelBuilder.Entity<GroupScheduledTaskEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<GroupScheduledTaskEntity>()
            .HasOne(s => s.Group).WithMany().HasForeignKey(s => s.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GroupScheduledTaskEntity>()
            .HasIndex(e => new { e.GroupId, e.IsEnabled, e.NextRunUtc });

        modelBuilder.Entity<GroupScheduledTaskRunEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<GroupScheduledTaskRunEntity>()
            .HasOne(r => r.GroupTask).WithMany().HasForeignKey(r => r.GroupTaskId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GroupScheduledTaskRunEntity>()
            .HasIndex(e => new { e.GroupTaskId, e.TenantId, e.Status });

        modelBuilder.Entity<TenantNotificationSettingsEntity>()
            .HasKey(e => e.TenantId);

        // GroupLlmConfig: 1:many per group; optional FK reference to PlatformLlmConfigs
        modelBuilder.Entity<GroupLlmConfigEntity>()
            .HasOne(c => c.Group).WithMany(g => g.LlmConfigs)
            .HasForeignKey(c => c.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<GroupLlmConfigEntity>()
            .HasOne(c => c.PlatformConfig).WithMany()
            .HasForeignKey(c => c.PlatformConfigRef)
            .OnDelete(DeleteBehavior.SetNull);
        // All configs must be named; unique per (GroupId, Name)
        modelBuilder.Entity<GroupLlmConfigEntity>()
            .HasIndex(e => new { e.GroupId, e.Name })
            .IsUnique();

        // ── Group Agent Overlays (Phase 18) ──────────────────
        modelBuilder.Entity<TenantGroupAgentOverlayEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<TenantGroupAgentOverlayEntity>()
            .HasOne(o => o.Group).WithMany()
            .HasForeignKey(o => o.GroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TenantGroupAgentOverlayEntity>()
            .HasOne(o => o.Template).WithMany()
            .HasForeignKey(o => o.GroupTemplateId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<TenantGroupAgentOverlayEntity>()
            .HasIndex(e => new { e.TenantId, e.GroupTemplateId }).IsUnique();
        modelBuilder.Entity<TenantGroupAgentOverlayEntity>()
            .HasIndex(e => e.Guid).IsUnique();
        // SQLite forbids function expressions as DEFAULT — GUID is always provided by C# entity initializer.

        // ── Platform LLM Config ───────────────────────────────
        modelBuilder.Entity<PlatformLlmConfigEntity>()
            .HasIndex(e => e.Name)
            .IsUnique();

        // ── Tenant LLM Config ─────────────────────────────────
        modelBuilder.Entity<TenantLlmConfigEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<TenantLlmConfigEntity>()
            .HasIndex(e => new { e.TenantId, e.Name })
            .IsUnique()
            .HasFilter("[Name] IS NOT NULL");

        // ── Local Users ───────────────────────────────────────
        modelBuilder.Entity<LocalUserEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<LocalUserEntity>()
            .HasIndex(e => new { e.TenantId, e.Username })
            .IsUnique();
        modelBuilder.Entity<LocalUserEntity>()
            .Property(e => e.Roles)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? Array.Empty<string>());

        // ── A2A Tasks ─────────────────────────────────────────
        modelBuilder.Entity<AgentTaskEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<AgentTaskEntity>()
            .HasIndex(e => new { e.TenantId, e.Status });
        modelBuilder.Entity<AgentTaskEntity>()
            .HasIndex(e => new { e.Status, e.CreatedAt })
            .HasDatabaseName("IX_AgentTasks_Status_CreatedAt");

        // ── Rule Packs (Phase 16) ─────────────────────────────
        modelBuilder.Entity<HookRulePackEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<HookRulePackEntity>()
            .HasOne(p => p.ParentPack)
            .WithMany(p => p.ChildPacks)
            .HasForeignKey(p => p.ParentPackId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<HookRulePackEntity>()
            .HasOne(p => p.Group)
            .WithMany()
            .HasForeignKey(p => p.GroupId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<HookRulePackEntity>()
            .HasIndex(e => new { e.TenantId, e.IsEnabled, e.Priority });
        modelBuilder.Entity<HookRulePackEntity>()
            .HasIndex(e => e.GroupId);

        modelBuilder.Entity<HookRuleEntity>()
            .HasOne(r => r.Pack)
            .WithMany(p => p.Rules)
            .HasForeignKey(r => r.PackId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<HookRuleEntity>()
            .HasOne(r => r.OverridesParentRule)
            .WithMany()
            .HasForeignKey(r => r.OverridesParentRuleId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<HookRuleEntity>()
            .HasIndex(e => new { e.PackId, e.OrderInPack });

        modelBuilder.Entity<RuleExecutionLogEntity>()
            .HasIndex(e => new { e.TenantId, e.Timestamp });
        modelBuilder.Entity<RuleExecutionLogEntity>()
            .HasIndex(e => new { e.PackId, e.RuleId, e.Timestamp });

        // ── MCP Credentials ───────────────────────────────────
        modelBuilder.Entity<McpCredentialEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<McpCredentialEntity>()
            .HasIndex(e => new { e.TenantId, e.Name }).IsUnique();

        // ── Platform API Keys ─────────────────────────────────
        modelBuilder.Entity<PlatformApiKeyEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<PlatformApiKeyEntity>()
            .HasIndex(e => e.KeyHash);

        // ── Widget Configs ────────────────────────────────────
        modelBuilder.Entity<WidgetConfigEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<WidgetConfigEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<WidgetConfigEntity>()
            .HasIndex(e => new { e.TenantId, e.IsActive });

        // ── Phase 24: Agent Optimization ─────────────────────
        modelBuilder.Entity<AgentOptimizationRunEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<AgentOptimizationRunEntity>()
            .HasIndex(e => new { e.TenantId, e.AgentId, e.StartedAt });

        modelBuilder.Entity<AgentOptimizationSuggestionEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<AgentOptimizationSuggestionEntity>()
            .HasOne(s => s.Run)
            .WithMany(r => r.Suggestions)
            .HasForeignKey(s => s.RunId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AgentOptimizationSuggestionEntity>()
            .HasIndex(e => new { e.TenantId, e.AgentId, e.Status });

        modelBuilder.Entity<AgentOptimizationConfigEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<AgentOptimizationConfigEntity>()
            .HasIndex(e => new { e.TenantId, e.AgentId }).IsUnique();

        modelBuilder.Entity<FewShotExampleEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<FewShotExampleEntity>()
            .HasIndex(e => new { e.TenantId, e.AgentId, e.SortOrder });
    }

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        // Auto-set UpdatedAt for business rules
        foreach (var entry in ChangeTracker.Entries<TenantBusinessRuleEntity>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = DateTime.UtcNow;
        }

        // Auto-set UpdatedAt for group entities
        foreach (var entry in ChangeTracker.Entries<GroupBusinessRuleEntity>())
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<GroupAgentTemplateEntity>())
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<GroupScheduledTaskEntity>())
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<PlatformLlmConfigEntity>())
            if (entry.State is EntityState.Added or EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<TenantLlmConfigEntity>())
            if (entry.State is EntityState.Added or EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<GroupLlmConfigEntity>())
            if (entry.State is EntityState.Added or EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<TenantGroupAgentOverlayEntity>())
            if (entry.State == EntityState.Modified) entry.Entity.UpdatedAt = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<HookRulePackEntity>())
            if (entry.State == EntityState.Modified) entry.Entity.ModifiedAt = DateTime.UtcNow;

        // Auto-touch session LastActivityAt
        foreach (var entry in ChangeTracker.Entries<AgentSessionEntity>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.LastActivityAt = DateTime.UtcNow;
        }

        return base.SaveChangesAsync(ct);
    }
}
