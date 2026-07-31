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

    // ── Shared MCP Tool Servers ───────────────────────────────────────────────
    public DbSet<TenantMcpServerEntity> TenantMcpServers => Set<TenantMcpServerEntity>();

    // ── Embeddable Chat Widgets ───────────────────────────────────────────────
    public DbSet<WidgetConfigEntity> WidgetConfigs => Set<WidgetConfigEntity>();

    // ── Agent Access Groups (Phase 28) ────────────────────────────────────────
    public DbSet<AgentGroupEntity> AgentGroups => Set<AgentGroupEntity>();

    // ── Environment-based agent management (foundation) ───────────────────────
    public DbSet<TenantEnvironmentEntity> TenantEnvironments => Set<TenantEnvironmentEntity>();

    // ── Generic Versioning Ledger (Track 2 Phase B) ────────────────────────────
    public DbSet<PromotableObjectEntity> PromotableObjects => Set<PromotableObjectEntity>();
    public DbSet<PromotableVersionEntity> PromotableVersions => Set<PromotableVersionEntity>();
    public DbSet<EnvironmentDeploymentEntity> EnvironmentDeployments => Set<EnvironmentDeploymentEntity>();
    public DbSet<PromotionRunEntity> PromotionRuns => Set<PromotionRunEntity>();

    // ── Draft Isolation (Track 2 Phase C) ──────────────────────────────────────
    public DbSet<EntityDraftEntity> EntityDrafts => Set<EntityDraftEntity>();

    // ── User Groups (group users; grant agent access + shared-MCP credentials) ─
    public DbSet<UserGroupEntity> UserGroups => Set<UserGroupEntity>();
    public DbSet<UserGroupMemberEntity> UserGroupMembers => Set<UserGroupMemberEntity>();
    public DbSet<UserGroupRoleEntity> UserGroupRoles => Set<UserGroupRoleEntity>();
    public DbSet<AgentGroupUserGroupEntity> AgentGroupUserGroups => Set<AgentGroupUserGroupEntity>();
    public DbSet<McpServerUserGroupCredentialEntity> McpServerUserGroupCredentials => Set<McpServerUserGroupCredentialEntity>();

    // ── Phase 24: Agent Optimization ──────────────────────────────────────────
    public DbSet<AgentOptimizationRunEntity> OptimizationRuns => Set<AgentOptimizationRunEntity>();
    public DbSet<AgentOptimizationSuggestionEntity> OptimizationSuggestions => Set<AgentOptimizationSuggestionEntity>();
    public DbSet<AgentOptimizationConfigEntity> OptimizationConfigs => Set<AgentOptimizationConfigEntity>();
    public DbSet<FewShotExampleEntity> FewShotExamples => Set<FewShotExampleEntity>();

    // ── Scheduler Feedback ────────────────────────────────────────────────────
    public DbSet<SchedulerFeedbackEntity> SchedulerFeedbacks => Set<SchedulerFeedbackEntity>();
    public DbSet<TenantFeedbackSettingsEntity> TenantFeedbackSettings => Set<TenantFeedbackSettingsEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Provider-specific SQL differs for filtered-index predicates (identifier quoting).
        // SQLite uses double-quote/bracket identifiers; SQL Server uses brackets and <> for inequality.
        var isSqlite = Database.IsSqlite();

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
            .HasFilter(isSqlite ? "\"Email\" != ''" : "[Email] <> ''");
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

        modelBuilder.Entity<TenantFeedbackSettingsEntity>()
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
        // All configs must be named; unique per (GroupId, Name, EnvironmentId) — Phase G adds the
        // environment dimension so the same named config can have an independent key per environment.
        modelBuilder.Entity<GroupLlmConfigEntity>()
            .HasIndex(e => new { e.GroupId, e.Name, e.EnvironmentId })
            .IsUnique();
        modelBuilder.Entity<GroupLlmConfigEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // ── Group Agent Overlays (Phase 18) ──────────────────
        modelBuilder.Entity<TenantGroupAgentOverlayEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<TenantGroupAgentOverlayEntity>()
            .HasOne(o => o.Group).WithMany()
            .HasForeignKey(o => o.GroupId)
            // SQL Server rejects multiple cascade paths: TenantGroups already cascades to this
            // table via GroupAgentTemplates (GroupTemplateId). Keep the direct Group FK as a
            // no-op delete on SQL Server; overlays are still removed via the template cascade.
            // SQLite tolerates multiple cascade paths, so keep Cascade there to avoid snapshot drift.
            .OnDelete(isSqlite ? DeleteBehavior.Cascade : DeleteBehavior.NoAction);
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
            .HasIndex(e => new { e.TenantId, e.Name, e.EnvironmentId })
            .IsUnique()
            .HasFilter("[Name] IS NOT NULL");
        modelBuilder.Entity<TenantLlmConfigEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);

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
            // Self-referencing FK: SQL Server forbids SET NULL / CASCADE on a table that
            // references itself (cycle). Use NO ACTION there; SQLite keeps SET NULL to avoid
            // snapshot drift. The parent ref is cleared in code before deletes.
            .OnDelete(isSqlite ? DeleteBehavior.SetNull : DeleteBehavior.NoAction);
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
            .HasIndex(e => new { e.TenantId, e.Name, e.EnvironmentId }).IsUnique();
        modelBuilder.Entity<McpCredentialEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);

        // ── Platform API Keys ─────────────────────────────────
        modelBuilder.Entity<PlatformApiKeyEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<PlatformApiKeyEntity>()
            .HasIndex(e => e.KeyHash); modelBuilder.Entity<PlatformApiKeyEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);
        // ── Shared MCP Tool Servers ───────────────────────────
        modelBuilder.Entity<TenantMcpServerEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<TenantMcpServerEntity>()
            .HasIndex(e => new { e.TenantId, e.Name }).IsUnique();

        // ── Widget Configs ────────────────────────────────────
        modelBuilder.Entity<WidgetConfigEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<WidgetConfigEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<WidgetConfigEntity>()
            .HasIndex(e => new { e.TenantId, e.IsActive }); modelBuilder.Entity<WidgetConfigEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);
        // ── Agent Access Groups (Phase 28) ────────────────────
        modelBuilder.Entity<AgentGroupEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<AgentGroupEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<AgentGroupEntity>()
            .HasIndex(e => e.TenantId);

        // ── Environment-based agent management (foundation) ──
        modelBuilder.Entity<TenantEnvironmentEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<TenantEnvironmentEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<TenantEnvironmentEntity>()
            .HasIndex(e => new { e.TenantId, e.Slug }).IsUnique();

        // EnvironmentId is a nullable FK on the 4 promotable entity types (inert until
        // environment-scoped runtime routing ships — see EnvironmentEntities.cs). Restrict
        // delete so removing an environment can't silently orphan/cascade-delete live agents,
        // MCP servers, schedules, or agent groups still tagged to it.
        modelBuilder.Entity<AgentDefinitionEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AgentDefinitionEntity>()
            .HasIndex(e => new { e.TenantId, e.EnvironmentId, e.LogicalId });

        modelBuilder.Entity<TenantMcpServerEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<TenantMcpServerEntity>()
            .HasIndex(e => new { e.TenantId, e.EnvironmentId, e.LogicalId });

        modelBuilder.Entity<ScheduledTaskEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<ScheduledTaskEntity>()
            .HasIndex(e => new { e.TenantId, e.EnvironmentId, e.LogicalId });

        modelBuilder.Entity<AgentGroupEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<AgentGroupEntity>()
            .HasIndex(e => new { e.TenantId, e.EnvironmentId, e.LogicalId });

        // ── Generic Versioning Ledger (Track 2 Phase B) ───────
        // All Restrict (no cascade anywhere in this subsystem) — deliberate: (1) an immutable,
        // append-only ledger should never silently mass-delete history as a side effect of
        // deleting something else; (2) PromotableVersionEntity/EnvironmentDeploymentEntity are
        // both reachable from PromotableObjectEntity AND (for EnvironmentDeploymentEntity) also
        // via PromotableVersionEntity.LiveVersionId — cascading either path would hit SQL
        // Server's "multiple cascade paths" restriction, so Restrict avoids that entirely on
        // both providers with no isSqlite branching needed.
        modelBuilder.Entity<PromotableObjectEntity>()
            .HasKey(e => e.LogicalId);
        modelBuilder.Entity<PromotableObjectEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<PromotableObjectEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.OriginEnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PromotableObjectEntity>()
            .HasIndex(e => new { e.TenantId, e.ObjectType });

        modelBuilder.Entity<PromotableVersionEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<PromotableVersionEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<PromotableVersionEntity>()
            .HasOne<PromotableObjectEntity>()
            .WithMany()
            .HasForeignKey(e => e.LogicalId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PromotableVersionEntity>()
            .HasOne<PromotableVersionEntity>()
            .WithMany()
            .HasForeignKey(e => e.PromotedFromVersionId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<PromotableVersionEntity>()
            .HasIndex(e => new { e.LogicalId, e.Version }).IsUnique();

        modelBuilder.Entity<EnvironmentDeploymentEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<EnvironmentDeploymentEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<EnvironmentDeploymentEntity>()
            .HasOne<PromotableObjectEntity>()
            .WithMany()
            .HasForeignKey(e => e.LogicalId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<EnvironmentDeploymentEntity>()
            .HasOne<TenantEnvironmentEntity>()
            .WithMany()
            .HasForeignKey(e => e.EnvironmentId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<EnvironmentDeploymentEntity>()
            .HasOne<PromotableVersionEntity>()
            .WithMany()
            .HasForeignKey(e => e.LiveVersionId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<EnvironmentDeploymentEntity>()
            .HasIndex(e => new { e.LogicalId, e.EnvironmentId }).IsUnique();

        modelBuilder.Entity<PromotionRunEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<PromotionRunEntity>()
            .HasIndex(e => new { e.TenantId, e.RootLogicalId });

        // ── Draft Isolation (Track 2 Phase C) ──────────────────
        modelBuilder.Entity<EntityDraftEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<EntityDraftEntity>()
            .HasIndex(e => new { e.TenantId, e.ObjectType, e.LogicalId, e.EnvironmentId }).IsUnique();

        // ── User Groups ───────────────────────────────────────
        modelBuilder.Entity<UserGroupEntity>()
            .HasKey(e => e.Id);
        modelBuilder.Entity<UserGroupEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<UserGroupEntity>()
            .HasIndex(e => new { e.TenantId, e.Name }).IsUnique();

        modelBuilder.Entity<UserGroupMemberEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<UserGroupMemberEntity>()
            .HasOne(m => m.Group).WithMany(g => g.Members).HasForeignKey(m => m.UserGroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserGroupMemberEntity>()
            .HasIndex(e => new { e.UserGroupId, e.UserId }).IsUnique();
        modelBuilder.Entity<UserGroupMemberEntity>()
            .HasIndex(e => new { e.TenantId, e.UserId });
        modelBuilder.Entity<UserGroupMemberEntity>()
            .HasIndex(e => new { e.TenantId, e.Email });

        modelBuilder.Entity<UserGroupRoleEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<UserGroupRoleEntity>()
            .HasOne(r => r.Group).WithMany(g => g.Roles).HasForeignKey(r => r.UserGroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<UserGroupRoleEntity>()
            .HasIndex(e => new { e.UserGroupId, e.Role }).IsUnique();
        modelBuilder.Entity<UserGroupRoleEntity>()
            .HasIndex(e => e.TenantId);

        modelBuilder.Entity<AgentGroupUserGroupEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<AgentGroupUserGroupEntity>()
            .HasOne(j => j.AgentGroup).WithMany(a => a.UserGroupLinks).HasForeignKey(j => j.AgentGroupId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<AgentGroupUserGroupEntity>()
            .HasOne(j => j.UserGroup).WithMany().HasForeignKey(j => j.UserGroupId)
            // SQL Server rejects multiple cascade paths converging on this junction; keep the
            // UserGroup FK a no-op delete there. SQLite tolerates it, so keep Cascade to avoid
            // snapshot drift. Rows are still removed via the AgentGroup cascade.
            .OnDelete(isSqlite ? DeleteBehavior.Cascade : DeleteBehavior.NoAction);
        modelBuilder.Entity<AgentGroupUserGroupEntity>()
            .HasIndex(e => new { e.AgentGroupId, e.UserGroupId }).IsUnique();
        modelBuilder.Entity<AgentGroupUserGroupEntity>()
            .HasIndex(e => e.TenantId);

        modelBuilder.Entity<McpServerUserGroupCredentialEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<McpServerUserGroupCredentialEntity>()
            .HasOne(c => c.McpServer).WithMany(s => s.UserGroupCredentials).HasForeignKey(c => c.McpServerId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<McpServerUserGroupCredentialEntity>()
            .HasOne(c => c.UserGroup).WithMany().HasForeignKey(c => c.UserGroupId)
            // Multiple cascade paths guard (same rationale as AgentGroupUserGroup).
            .OnDelete(isSqlite ? DeleteBehavior.Cascade : DeleteBehavior.NoAction);
        modelBuilder.Entity<McpServerUserGroupCredentialEntity>()
            .HasIndex(e => new { e.McpServerId, e.UserGroupId }).IsUnique();
        modelBuilder.Entity<McpServerUserGroupCredentialEntity>()
            .HasIndex(e => e.TenantId);

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

        // ── Scheduler Feedback ────────────────────────────────
        modelBuilder.Entity<SchedulerFeedbackEntity>()
            .HasQueryFilter(e => _currentTenantId == 0 || e.TenantId == _currentTenantId);
        modelBuilder.Entity<SchedulerFeedbackEntity>()
            .HasIndex(e => new { e.TenantId, e.Status, e.SubmittedAt });
        modelBuilder.Entity<SchedulerFeedbackEntity>()
            .HasIndex(e => e.RunId);
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
