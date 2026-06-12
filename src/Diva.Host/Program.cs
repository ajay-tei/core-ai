using System.Threading.RateLimiting;
using Serilog;
using Microsoft.Extensions.Http.Resilience;
using Diva.Agents.Archetypes;
using Diva.Agents.Hooks;
using Diva.Agents.Registry;
using Diva.Agents.Supervisor;
using Diva.Agents.Supervisor.Stages;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Core.Optimization;
using Diva.Host.Hubs;
using Diva.Infrastructure.A2A;
using Diva.Infrastructure.AgentExport;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Groups;
using Diva.Infrastructure.Learning;
using Diva.Infrastructure.Context;
using Diva.Infrastructure.LiteLLM;
using Diva.Infrastructure.Scheduler;
using Diva.Infrastructure.Notifications;
using Diva.Infrastructure.Sessions;
using Diva.Infrastructure.Synthesis;
using Diva.Infrastructure.Verification;
using Diva.Agents.Supervisor.Decompose;
using Diva.Sso;
using Diva.TenantAdmin.Prompts;
using Diva.TenantAdmin.Services;
using Diva.TenantAdmin.Services.Enrichers;
using Diva.Infrastructure.Optimization;
using Diva.Tools.Core;
using Diva.Tools.FileSystem;
using Diva.Tools.Optimization;
using Diva.Tools.Scheduler;
using Diva.Tools.Email;
using Diva.Tools.FileSystem.Abstractions;
using Diva.Tools.FileSystem.Readers;
using Diva.Tools.FileSystem.Writers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Prometheus;

// ── Serilog bootstrap logger (captures startup errors before host is built) ───
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Serilog full configuration (reads from appsettings + Seq sink) ────────────
builder.Host.UseSerilog((ctx, services, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.Seq(ctx.Configuration["Seq:ServerUrl"] ?? "http://localhost:5341"));

// ── Configuration binding ──────────────────────────────────────────────────
builder.Services.Configure<LlmOptions>(builder.Configuration.GetSection(LlmOptions.SectionName));
builder.Services.Configure<DatabaseOptions>(builder.Configuration.GetSection(DatabaseOptions.SectionName));
builder.Services.Configure<VerificationOptions>(builder.Configuration.GetSection(VerificationOptions.SectionName));
builder.Services.Configure<AgentOptions>(builder.Configuration.GetSection(AgentOptions.SectionName));
builder.Services.Configure<TaskSchedulerOptions>(builder.Configuration.GetSection(TaskSchedulerOptions.SectionName));
builder.Services.Configure<OAuthOptions>(builder.Configuration.GetSection(OAuthOptions.SectionName));
builder.Services.Configure<LocalAuthOptions>(builder.Configuration.GetSection(LocalAuthOptions.SectionName));
builder.Services.Configure<A2AOptions>(builder.Configuration.GetSection(A2AOptions.SectionName));
builder.Services.Configure<CredentialOptions>(builder.Configuration.GetSection(CredentialOptions.SectionName));
builder.Services.Configure<AppBrandingOptions>(builder.Configuration.GetSection(AppBrandingOptions.SectionName));

// ── Credential Vault & Platform API Keys ──────────────────────────────────
builder.Services.AddSingleton<ICredentialEncryptor, AesCredentialEncryptor>();
builder.Services.AddSingleton<ICredentialResolver, CredentialResolver>();
builder.Services.AddSingleton<IPlatformApiKeyService, PlatformApiKeyService>();

// ── Database ───────────────────────────────────────────────────────────────
var dbOptions = builder.Configuration
    .GetSection(DatabaseOptions.SectionName)
    .Get<DatabaseOptions>() ?? new DatabaseOptions();

builder.Services.AddDbContext<DivaDbContext>(opt =>
{
    if (dbOptions.Provider == "SqlServer")
        opt.UseSqlServer(dbOptions.SqlServer.ConnectionString, o => o.EnableRetryOnFailure());
    else
        opt.UseSqlite(dbOptions.SQLite.ConnectionString);
}, ServiceLifetime.Scoped);

builder.Services.AddSingleton<IDatabaseProviderFactory, DatabaseProviderFactory>();

// ── Session Trace DB (separate SQLite file) ────────────────────────────────
var traceConnStr = builder.Configuration.GetConnectionString("SessionTrace") ?? "Data Source=sessions-trace.db";
builder.Services.AddDbContext<SessionTraceDbContext>(opt =>
    opt.UseSqlite(traceConnStr), ServiceLifetime.Scoped);
builder.Services.AddScoped<SessionTraceWriter>();

var traceCleanupOpts = builder.Configuration
    .GetSection("SessionTrace")
    .Get<TraceCleanupOptions>() ?? new TraceCleanupOptions();
builder.Services.AddSingleton(traceCleanupOpts);
builder.Services.AddHostedService<TraceCleanupService>();

// ── Main conversation session cleanup ─────────────────────────────────────
var sessionCleanupOpts = builder.Configuration
    .GetSection("Sessions")
    .Get<SessionCleanupOptions>() ?? new SessionCleanupOptions();
builder.Services.AddSingleton(sessionCleanupOpts);
builder.Services.AddHostedService<SessionCleanupService>();

// ── LLM Provider wrappers (injectable — enable unit testing without real API keys) ──
// Register named HttpClient for Anthropic with configured timeout, then register
// AnthropicProvider as singleton using IHttpClientFactory (avoids captive dependency).
var llmTimeoutSec = builder.Configuration.GetSection(LlmOptions.SectionName)
    .GetValue<int?>("HttpTimeoutSeconds") ?? 600;
builder.Services.AddHttpClient("AnthropicProvider", client =>
    client.Timeout = TimeSpan.FromSeconds(llmTimeoutSec));
builder.Services.AddSingleton<IAnthropicProvider>(sp =>
{
    var httpFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpFactory.CreateClient("AnthropicProvider");
    var opts = sp.GetRequiredService<IOptions<LlmOptions>>();
    return new AnthropicProvider(opts, httpClient);
});
builder.Services.AddSingleton<IOpenAiProvider, OpenAiProvider>();

// ── Context window management ─────────────────────────────────────────────
builder.Services.AddSingleton<IContextWindowManager, ContextWindowManager>();

// ── Verification (Phase 13) ────────────────────────────────────────────────
builder.Services.AddSingleton<ResponseVerifier>();

// ── Rule Learning (Phase 11) ───────────────────────────────────────────────
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSingleton<LlmRuleExtractor>();
builder.Services.AddSingleton<ISessionRuleManager, SessionRuleManager>();
builder.Services.AddSingleton<IRuleLearningService, RuleLearningService>();
builder.Services.AddSingleton<FeedbackLearningService>();

// ── Tenant Admin — Phase 6 ────────────────────────────────────────────────
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<ITenantBusinessRulesService, TenantBusinessRulesService>();
builder.Services.AddSingleton<IPromptBuilder, TenantAwarePromptBuilder>();

// ── Rule Packs (Phase 16) ─────────────────────────────────────────────────
builder.Services.AddSingleton<IRulePackService, RulePackService>();
builder.Services.AddSingleton<RulePackEngine>();
builder.Services.AddSingleton<RulePackConflictAnalyzer>();

// ── Agent Export / Import ─────────────────────────────────────────────────
builder.Services.AddScoped<IAgentExportService, AgentExportService>();

// ── Phase 17: Agent Setup Assistant ──────────────────────────────────────
builder.Services.AddSingleton<PromptTemplateStore>();
builder.Services.AddSingleton<ISetupAssistantContextEnricher, ArchetypeContextEnricher>();
builder.Services.AddSingleton<ISetupAssistantContextEnricher, LlmConfigContextEnricher>();
builder.Services.AddSingleton<IAgentToolDiscoveryService, AgentToolDiscoveryService>();
builder.Services.AddSingleton<ISetupAssistantContextEnricher, AgentToolsContextEnricher>();
builder.Services.AddSingleton<IAgentSetupAssistant, AgentSetupAssistant>();

// ── Platform tenant management (master admin) ─────────────────────────────
builder.Services.AddSingleton<ITenantManagementService, TenantManagementService>();

// ── Local auth ────────────────────────────────────────────────────────────
builder.Services.AddScoped<ILocalAuthService, LocalAuthService>();

// ── Widget Config ─────────────────────────────────────────────────────────
builder.Services.AddScoped<IWidgetConfigService, WidgetConfigService>();

// ── Auth (TenantContextMiddleware) ────────────────────────────────────────
// SsoTokenValidator (Diva.Sso) — validates JWT and opaque tokens per-provider config
builder.Services.AddSsoValidation();
// Per-tenant SSO config service (also implements ISsoConfigResolver for Diva.Sso)
builder.Services.AddSingleton<TenantSsoConfigService>();
builder.Services.AddSingleton<ITenantSsoConfigService>(sp => sp.GetRequiredService<TenantSsoConfigService>());
builder.Services.AddSingleton<ISsoConfigResolver>(sp => sp.GetRequiredService<TenantSsoConfigService>());
// OAuthTokenValidator kept as concrete fallback for issuers without per-tenant config
builder.Services.AddScoped<OAuthTokenValidator>();
// MultiTenantOAuthTokenValidator: uses per-tenant config when available, falls back to OAuthTokenValidator
builder.Services.AddScoped<IOAuthTokenValidator, MultiTenantOAuthTokenValidator>();
builder.Services.AddScoped<ITenantClaimsExtractor, TenantClaimsExtractor>();
// User profiles (Phase 3) — upsert on login + active check in TenantContextMiddleware
builder.Services.AddSingleton<UserProfileService>();
builder.Services.AddSingleton<IUserProfileService>(sp => sp.GetRequiredService<UserProfileService>());
builder.Services.AddSingleton<IUserLoginTracker>(sp => sp.GetRequiredService<UserProfileService>());

// ── MCP Tools — Phase 5 ───────────────────────────────────────────────────
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<McpHeaderPropagator>();
builder.Services.AddScoped<TenantAwareMcpClient>();

// ── MCP Server — Phase 23 ─────────────────────────────────────────────────
builder.Services.Configure<FileSystemOptions>(
    builder.Configuration.GetSection(FileSystemOptions.SectionName));
builder.Services.AddSingleton<IValidateOptions<FileSystemOptions>, FileSystemOptionsValidator>();
builder.Services.AddScoped<IFileSystemPathGuard, FileSystemPathGuard>();
builder.Services.AddScoped<IToolFilter, ToolFilter>();
builder.Services.AddScoped<IPdfReader, PdfReader>();
builder.Services.AddScoped<IImageReader, ImageReader>();
builder.Services.AddScoped<IOfficeReader, OfficeReader>();
builder.Services.AddScoped<IOfficeWriter, OfficeWriter>();
builder.Services.AddSingleton<FileWriteLock>();
builder.Services.AddSingleton<ScriptThrottle>();
builder.Services
    .AddMcpServer(opts => opts.ServerInfo = new() { Name = "diva-mcp", Version = "1.0" })
    .WithHttpTransport()
    .WithDivaMcpTools<FileSystemMcpTools>()
    .WithDivaMcpTools<AgentOptimizationMcpTools>()
    .WithDivaMcpTools<SchedulerMcpTools>()
    .WithDivaMcpTools<EmailMcpTools>();
// Phase 5 future: .WithDivaMcpTools<AnalyticsMcpTools>()

// ── Agents ─────────────────────────────────────────────────────────────────
builder.Services.AddSingleton<AgentSessionService>();
builder.Services.AddSingleton<McpClientCache>();
builder.Services.AddSingleton<IMcpConnectionManager, McpConnectionManager>();
builder.Services.AddSingleton<ToolExecutor>();
builder.Services.AddSingleton<AgentToolProvider>();
builder.Services.AddSingleton<AgentToolExecutor>();
builder.Services.AddSingleton<AnthropicAgentRunner>();
builder.Services.AddSingleton<IAgentRunner>(sp => sp.GetRequiredService<AnthropicAgentRunner>());

// ── Phase 15: Hooks, Archetypes, A2A ──────────────────────────────────────
builder.Services.AddSingleton(HookTypeRegistry.BuildFromAssemblies(typeof(AgentHookPipeline).Assembly));
builder.Services.AddSingleton<IAgentHookPipeline, AgentHookPipeline>();
builder.Services.AddSingleton<IReActHookCoordinator, ReActHookCoordinator>();
builder.Services.AddSingleton<IArchetypeRegistry, ArchetypeRegistry>();
builder.Services.AddSingleton<IAgentCardBuilder, AgentCardBuilder>();
builder.Services.AddHttpClient<IA2AAgentClient, A2AAgentClient>()
    .AddStandardResilienceHandler();

// ── Phase 8: Supervisor pipeline ───────────────────────────────────────────

// Registry — one DynamicAgentRegistry singleton satisfies both interfaces
builder.Services.AddSingleton<IAgentRegistry, DynamicAgentRegistry>();
builder.Services.AddSingleton<IReadableAgentRegistry>(
    sp => sp.GetRequiredService<IAgentRegistry>());
builder.Services.AddSingleton<ICapabilityScoringService, CapabilityScoringService>();

// Decomposition strategies — SingleTaskStrategy registered twice so both the selector
// (via IEnumerable<IDecompositionStrategy>) and LlmDecompositionStrategy (direct ctor
// injection as fallback) resolve the same singleton instance.
builder.Services.AddSingleton<SingleTaskStrategy>();
builder.Services.AddSingleton<IDecompositionStrategy>(
    sp => sp.GetRequiredService<SingleTaskStrategy>());           // Priority = 0
builder.Services.AddSingleton<IDecompositionStrategy, LlmDecompositionStrategy>(); // Priority = 10
builder.Services.AddSingleton<DecompositionStrategySelector>();

// Synthesis
builder.Services.AddSingleton<IResponseSynthesizer, ResponseSynthesizer>();

// Semantic tool pre-filtering — narrows tool list to query-relevant subset before ReAct loop
builder.Services.AddSingleton<IToolSelectionStrategy, LlmToolSelector>();

builder.Services.AddSingleton<IAgentDelegationResolver, DelegationAgentResolver>();

// Pipeline stages in execution order (all Singleton — no scoped deps)
builder.Services.AddSingleton<ISupervisorPipelineStage, AgentContextStage>();  // pre-fetch agents
builder.Services.AddSingleton<ISupervisorPipelineStage, DecomposeStage>();
builder.Services.AddSingleton<ISupervisorPipelineStage, CapabilityMatchStage>();
builder.Services.AddSingleton<ISupervisorPipelineStage, DispatchStage>();
builder.Services.AddSingleton<ISupervisorPipelineStage, MonitorStage>();
builder.Services.AddSingleton<ISupervisorPipelineStage, IntegrateStage>();
builder.Services.AddSingleton<ISupervisorPipelineStage, VerifyStage>();
builder.Services.AddSingleton<ISupervisorPipelineStage, DeliverStage>();

builder.Services.AddSingleton<ISupervisorAgent, SupervisorAgent>();

// ── Task Scheduler (Phase 15) ────────────────────────────────────────────────
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.SectionName));
builder.Services.AddSingleton<IEmailNotifier, SmtpEmailNotifier>();
builder.Services.AddSingleton<IScheduledTaskService, ScheduledTaskService>();
builder.Services.AddHostedService<SchedulerHostedService>();
// Feedback token service is singleton (pure crypto, no DB/tenant context)
builder.Services.AddSingleton<ISchedulerFeedbackTokenService, SchedulerFeedbackTokenService>();
builder.Services.AddScoped<ISchedulerFeedbackService, SchedulerFeedbackService>();
builder.Services.AddHostedService<AgentTaskCleanupService>();

// ── Tenant Groups + DB-backed LLM Config (Phase 15.5) ─────────────────────────────────
builder.Services.AddSingleton<IGroupMembershipCache, GroupMembershipCache>();
builder.Services.AddSingleton<ILlmConfigResolver, LlmConfigResolver>();
builder.Services.AddSingleton<ITenantGroupService, TenantGroupService>();

// ── Phase 28: Agent Access Groups ─────────────────────────────────────────────────────
builder.Services.AddSingleton<IAgentGroupService, AgentGroupService>();

// ── Phase 18: Group Agent Overlays ────────────────────────────────────────────────────
builder.Services.AddSingleton<IGroupAgentOverlayService, GroupAgentOverlayService>();

// ── Phase 24: Agent Optimization ─────────────────────────────────────────────────────
builder.Services.AddSingleton<IOptimizationRulePackAccessor, OptimizationRulePackAccessor>();
builder.Services.AddSingleton<ITurnScoringService, TurnScoringService>();
builder.Services.AddSingleton<ISessionAnalyzer, SessionAnalyzer>();
builder.Services.AddSingleton<IOptimizationLlmAnalyzer, OptimizationLlmAnalyzer>();
builder.Services.AddScoped<OptimizationApplicator>();
builder.Services.AddSingleton<IAgentOptimizationService, AgentOptimizationService>();
builder.Services.AddHostedService<OptimizationSchedulerHostedService>();

// ── OpenTelemetry ──────────────────────────────────────────────────────────
var otelEndpoint = builder.Configuration["OTel:Endpoint"] ?? "http://localhost:4317";
var brandingForOtel = builder.Configuration.GetSection(AppBrandingOptions.SectionName).Get<AppBrandingOptions>() ?? new AppBrandingOptions();
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(brandingForOtel.ApiAudience))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(o => o.Endpoint = new Uri(otelEndpoint)))
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());

// ── Controllers + SignalR ──────────────────────────────────────────────────
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles;
    });
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Rate Limiting (A2A endpoints) ──────────────────────────────────────────
var a2aRateLimit = builder.Configuration.GetSection(A2AOptions.SectionName).Get<A2AOptions>()?.RateLimitPerMinute ?? 10;
builder.Services.AddRateLimiter(limiterOptions =>
{
    limiterOptions.RejectionStatusCode = 429;
    limiterOptions.AddPolicy("a2a", _ =>
        RateLimitPartition.GetSlidingWindowLimiter("a2a",
            _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = a2aRateLimit,
                Window = TimeSpan.FromMinutes(1),
                SegmentsPerWindow = 4,
                QueueLimit = 0,
            }));
    // Anonymous scheduler feedback endpoints (context lookup + submission)
    limiterOptions.AddPolicy("scheduler_feedback", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

// ── CORS ──────────────────────────────────────────────────────────────────────
// Supports comma-separated origins: PORTAL_ORIGIN=https://app.example.com,http://localhost:6010
var corsOrigins = (builder.Configuration["AdminPortal:CorsOrigin"] ?? "http://localhost:5173")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options =>
{
    // Admin portal — configured origin(s) with credentials
    options.AddPolicy("AdminPortal", policy =>
        policy.WithOrigins(corsOrigins)
              .AllowAnyHeader().AllowAnyMethod().AllowCredentials());

    // Widget endpoints — origin validation done inside the controller against AllowedOrigins
    options.AddPolicy("Widget", policy =>
        policy.SetIsOriginAllowed(_ => true)
              .AllowAnyHeader().AllowAnyMethod().AllowCredentials());
});

// ── Health Checks ──────────────────────────────────────────────────────────
builder.Services.AddHealthChecks();

var app = builder.Build();

// ── Post-DI wiring: break circular dependency between TenantGroupService ←→ GroupAgentOverlayService
// TenantGroupService needs the overlay service to invalidate caches when templates are updated,
// but both are Singletons — inject via setter after the container is built.
var tenantGroupSvc = app.Services.GetRequiredService<ITenantGroupService>() as TenantGroupService;
var overlaySvc = app.Services.GetRequiredService<IGroupAgentOverlayService>();
tenantGroupSvc?.SetOverlayService(overlaySvc);

// ── Auto-migrate on startup ────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
    await db.Database.MigrateAsync();

    // ── Idempotent column additions for migrations that ran as no-ops ─────────
    // AddLastRunStatus migration was generated empty due to a corrupted model snapshot.
    // Apply the column directly if it doesn't exist yet.
    {
        var mainConn = db.Database.GetDbConnection();
        if (mainConn.State != System.Data.ConnectionState.Open)
            await mainConn.OpenAsync();
        await using var checkCol = mainConn.CreateCommand();
        checkCol.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ScheduledTasks') WHERE name='LastRunStatus'";
        var colExists = await checkCol.ExecuteScalarAsync();
        if (colExists is long colCount && colCount == 0)
        {
            await using var alterCmd = mainConn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE ScheduledTasks ADD COLUMN LastRunStatus TEXT";
            await alterCmd.ExecuteNonQueryAsync();
            Log.Information("Idempotent fix: added LastRunStatus column to ScheduledTasks");
        }
        // SuccessKeywords column (fallback for environments that missed migration updates)
        await using var checkSuccess = mainConn.CreateCommand();
        checkSuccess.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ScheduledTasks') WHERE name='SuccessKeywords'";
        var successExists = await checkSuccess.ExecuteScalarAsync();
        if (successExists is long successCount && successCount == 0)
        {
            await using var alterSuccess = mainConn.CreateCommand();
            alterSuccess.CommandText = "ALTER TABLE ScheduledTasks ADD COLUMN SuccessKeywords TEXT";
            await alterSuccess.ExecuteNonQueryAsync();
            Log.Information("Idempotent fix: added SuccessKeywords column to ScheduledTasks");
        }

        // One-time copy from legacy FailureKeywords column if both columns exist.
        await using var checkLegacy = mainConn.CreateCommand();
        checkLegacy.CommandText = "SELECT COUNT(*) FROM pragma_table_info('ScheduledTasks') WHERE name='FailureKeywords'";
        var legacyExists = await checkLegacy.ExecuteScalarAsync();
        if (legacyExists is long legacyCount && legacyCount > 0)
        {
            await using var copyLegacy = mainConn.CreateCommand();
            copyLegacy.CommandText = "UPDATE ScheduledTasks SET SuccessKeywords = COALESCE(SuccessKeywords, FailureKeywords) WHERE FailureKeywords IS NOT NULL";
            var copied = await copyLegacy.ExecuteNonQueryAsync();
            if (copied > 0)
                Log.Information("Idempotent fix: copied {Count} legacy FailureKeywords value(s) to SuccessKeywords", copied);
        }

        if (mainConn.State == System.Data.ConnectionState.Open)
            await mainConn.CloseAsync();
    }

    var traceDb = scope.ServiceProvider.GetRequiredService<SessionTraceDbContext>();
    // EnsureCreated creates tables if the DB is new; if the file already exists but
    // tables are missing (e.g. from a prior failed MigrateAsync), recreate.
    var created = await traceDb.Database.EnsureCreatedAsync();
    if (!created)
    {
        // File existed but may be empty (e.g. prior failed MigrateAsync).
        // Use raw ADO.NET — no EF pipeline wrapping, no error logging.
        var conn = traceDb.Database.GetDbConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='TraceSessions'";
        var result = await cmd.ExecuteScalarAsync();
        await conn.CloseAsync();
        if (result is long count && count == 0)
        {
            await traceDb.Database.EnsureDeletedAsync();
            await traceDb.Database.EnsureCreatedAsync();
            Log.Information("Session trace DB recreated (tables were missing)");
        }
    }

    // ── Phase 24: Idempotent score column additions (EnsureCreated path) ──────
    {
        var conn = traceDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync();
        var scoreColumns = new[] { "FaithfulnessScore", "CompletenessScore", "ToolEfficiencyScore", "CoherenceScore" };
        foreach (var col in scoreColumns)
        {
            await using var check = conn.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('TraceSessionTurns') WHERE name='{col}'";
            var exists = await check.ExecuteScalarAsync();
            if (exists is long c && c == 0)
            {
                await using var alter = conn.CreateCommand();
                alter.CommandText = $"ALTER TABLE TraceSessionTurns ADD COLUMN {col} REAL";
                await alter.ExecuteNonQueryAsync();
            }
        }
        // ScoresAvailable column (INTEGER/bool)
        await using var checkBool = conn.CreateCommand();
        checkBool.CommandText = "SELECT COUNT(*) FROM pragma_table_info('TraceSessionTurns') WHERE name='ScoresAvailable'";
        var boolExists = await checkBool.ExecuteScalarAsync();
        if (boolExists is long bc && bc == 0)
        {
            await using var alterBool = conn.CreateCommand();
            alterBool.CommandText = "ALTER TABLE TraceSessionTurns ADD COLUMN ScoresAvailable INTEGER NOT NULL DEFAULT 0";
            await alterBool.ExecuteNonQueryAsync();
        }
        await conn.CloseAsync();
        Log.Information("Phase 24: Turn scoring columns verified/added to TraceSessionTurns");
    }

    // ── Seed/sync platform LLM config from env vars ───────────────────────────
    // Creates the row on first startup; updates Provider/Model/Endpoint/ApiKey
    // when env vars are changed (so updating docker-compose.yml takes effect on restart).
    // ApiKey is only overwritten from env if the env value differs — preserves manual
    // admin-panel updates when the env key is unchanged.
    {
        var llmOpts = scope.ServiceProvider.GetRequiredService<IOptions<LlmOptions>>().Value;
        var dp = llmOpts.DirectProvider;
        var existing = await db.PlatformLlmConfigs.FirstOrDefaultAsync(p => p.Id == 1);
        if (existing is null)
        {
            db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
            {
                Id = 1,
                Provider = dp.Provider,
                ApiKey = dp.ApiKey,
                Model = dp.Model,
                Endpoint = dp.Endpoint,
                DeploymentName = dp.DeploymentName,
                AvailableModelsJson = llmOpts.AvailableModels.Count > 0
                    ? System.Text.Json.JsonSerializer.Serialize(llmOpts.AvailableModels) : null,
            });
            await db.SaveChangesAsync();
            Log.Information("Seeded PlatformLlmConfig: provider={Provider} model={Model}", dp.Provider, dp.Model);
        }
        else if (!existing.Provider.Equals(dp.Provider, StringComparison.OrdinalIgnoreCase)
                 || existing.Model != dp.Model
                 || existing.Endpoint != dp.Endpoint)
        {
            // Env vars changed — sync structural fields; update ApiKey only if it changed too
            existing.Provider = dp.Provider;
            existing.Model = dp.Model;
            existing.Endpoint = dp.Endpoint;
            if (existing.ApiKey != dp.ApiKey)
                existing.ApiKey = dp.ApiKey;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            Log.Information("Updated PlatformLlmConfig from env: provider={Provider} model={Model}", dp.Provider, dp.Model);
        }
    }
}

// ── Middleware Pipeline ────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("AdminPortal");
app.UseRateLimiter();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

// ── Endpoints ─────────────────────────────────────────────────────────────
app.MapControllers();
app.MapHub<AgentStreamHub>("/hubs/agent");
app.MapMcp("/mcp/diva").RequireAuthorization();

app.MapHealthChecks("/health/live", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions());

// Prometheus /metrics endpoint (prometheus-net.AspNetCore)
app.UseHttpMetrics();
app.MapMetrics();

app.Run();

// Make Program accessible for integration tests
public partial class Program { }
