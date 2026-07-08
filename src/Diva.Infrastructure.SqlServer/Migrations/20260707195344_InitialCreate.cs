using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Diva.Infrastructure.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AgentDefinitions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AgentType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LlmConfigId = table.Column<int>(type: "int", nullable: true),
                    Temperature = table.Column<double>(type: "float", nullable: false),
                    MaxIterations = table.Column<int>(type: "int", nullable: false),
                    Capabilities = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolBindings = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    VerificationMode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContextWindowJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OptimizationOverrideJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomVariablesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MaxContinuations = table.Column<int>(type: "int", nullable: true),
                    MaxToolResultChars = table.Column<int>(type: "int", nullable: true),
                    MaxOutputTokens = table.Column<int>(type: "int", nullable: true),
                    EnableHistoryCaching = table.Column<bool>(type: "bit", nullable: true),
                    PipelineStagesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolFilterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StageInstructionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublishedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ArchetypeId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HooksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    A2AEndpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    A2AAuthScheme = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    A2ASecretRef = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    A2ARemoteAgentId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExecutionMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModelSwitchingJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DelegateAgentIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    McpServerRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDefinitions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentGroups",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AgentIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AllowedUserIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AllowedRolesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentPromptHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPromptHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    InputJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OutputText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FewShotExamples",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SourceSessionId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourceTurnNumber = table.Column<int>(type: "int", nullable: true),
                    UserMessage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AssistantMessage = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FewShotExamples", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LearnedRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RuleCategory = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RuleKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PromptInjection = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Confidence = table.Column<double>(type: "float", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SourceSessionId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LearnedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LearnedRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LocalUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Username = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Roles = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LocalUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "McpCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    EncryptedApiKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AuthScheme = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomHeaderName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OptimizationConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ScheduleType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RunAtTime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RunOnDayOfWeek = table.Column<int>(type: "int", nullable: true),
                    Timezone = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    NextRunAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastScheduledRunAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptimizationConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OptimizationRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SessionId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TriggerSource = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SessionsAnalyzed = table.Column<int>(type: "int", nullable: false),
                    TurnsAnalyzed = table.Column<int>(type: "int", nullable: false),
                    ReportJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FromDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ToDate = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptimizationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    KeyHash = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    KeyPrefix = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Scope = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowedAgentIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AllowedGroupIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformLlmConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ApiKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Endpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeploymentName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AvailableModelsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformLlmConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PromptOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Section = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MergeMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SourceGroupOverrideId = table.Column<int>(type: "int", nullable: true),
                    AgentId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PromptOverrides", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleExecutionLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PackId = table.Column<int>(type: "int", nullable: false),
                    RuleId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Triggered = table.Column<bool>(type: "bit", nullable: false),
                    Action = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ElapsedMs = table.Column<int>(type: "int", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "datetime2", nullable: false),
                    BusinessRuleId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleExecutionLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RulePackHistory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    PackId = table.Column<int>(type: "int", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    RulesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Source = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RulePackHistory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScheduleType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScheduledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RunAtTime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DayOfWeek = table.Column<int>(type: "int", nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PayloadType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PromptText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextRunUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotifyEmails = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NotifyOn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastRunStatus = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureKeywords = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SchedulerFeedbacks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ScheduledTaskId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TaskType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ThumbsRating = table.Column<int>(type: "int", nullable: true),
                    StarRating = table.Column<int>(type: "int", nullable: true),
                    Category = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CorrectionText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SubmitterName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SubmitterEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    ReviewNotes = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SchedulerFeedbacks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Sessions",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    SiteId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    CurrentAgentType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SsoConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    ProviderName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Issuer = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClientId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ClientSecret = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AuthorizationEndpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    TokenEndpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserinfoEndpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Authority = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ProxyBaseUrl = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ProxyAdminEmail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UseRoleMappings = table.Column<bool>(type: "bit", nullable: false),
                    UseTeamMappings = table.Column<bool>(type: "bit", nullable: false),
                    TokenType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IntrospectionEndpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Audience = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ClaimMappingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LogoutUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EmailDomains = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SsoForwardHeadersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SsoConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantFeedbackSettings",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    EnableFeedbackLinks = table.Column<bool>(type: "bit", nullable: false),
                    FeedbackLinkBaseUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpiryDays = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantFeedbackSettings", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "TenantGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantLlmConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Endpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeploymentName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AvailableModelsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantLlmConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantMcpServers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Transport = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Command = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ArgsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    EnvJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Endpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PassSsoToken = table.Column<bool>(type: "bit", nullable: false),
                    PassTenantHeaders = table.Column<bool>(type: "bit", nullable: false),
                    DefaultCredentialRef = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApiKeyCredentialMappingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantMcpServers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantNotificationSettings",
                columns: table => new
                {
                    TenantId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GlobalNotifyEmails = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    GlobalNotifyOn = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantNotificationSettings", x => x.TenantId);
                });

            migrationBuilder.CreateTable(
                name: "Tenants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    LiteLLMTeamId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LiteLLMTeamKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tenants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AvatarUrl = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Roles = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AgentAccess = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AgentAccessOverrides = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    LastLoginAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WidgetConfigs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AllowedOriginsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SsoConfigId = table.Column<int>(type: "int", nullable: true),
                    AllowAnonymous = table.Column<bool>(type: "bit", nullable: false),
                    WelcomeMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PlaceholderText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ThemeJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    RespectSystemTheme = table.Column<bool>(type: "bit", nullable: false),
                    ShowBranding = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WidgetConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OptimizationSuggestions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    RunId = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Type = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    FieldName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CurrentValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SuggestedValue = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Confidence = table.Column<float>(type: "real", nullable: false),
                    Reasoning = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ReviewedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewNotes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OptimizationSuggestions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OptimizationSuggestions_OptimizationRuns_RunId",
                        column: x => x.RunId,
                        principalTable: "OptimizationRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTaskRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    ScheduledTaskId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ScheduledForUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ResponseText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AttemptNumber = table.Column<int>(type: "int", nullable: false),
                    InputTokens = table.Column<int>(type: "int", nullable: true),
                    OutputTokens = table.Column<int>(type: "int", nullable: true),
                    IterationCount = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTaskRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledTaskRuns_ScheduledTasks_ScheduledTaskId",
                        column: x => x.ScheduledTaskId,
                        principalTable: "ScheduledTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SessionMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SessionId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Role = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Content = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MetadataJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SessionMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SessionMessages_Sessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "Sessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupAgentTemplates",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AgentType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Temperature = table.Column<double>(type: "float", nullable: false),
                    MaxIterations = table.Column<int>(type: "int", nullable: false),
                    Capabilities = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolBindings = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    VerificationMode = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ContextWindowJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomVariablesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MaxContinuations = table.Column<int>(type: "int", nullable: true),
                    MaxToolResultChars = table.Column<int>(type: "int", nullable: true),
                    MaxOutputTokens = table.Column<int>(type: "int", nullable: true),
                    EnableHistoryCaching = table.Column<bool>(type: "bit", nullable: true),
                    PipelineStagesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolFilterJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StageInstructionsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LlmConfigId = table.Column<int>(type: "int", nullable: true),
                    ArchetypeId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    HooksJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    A2AEndpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    A2AAuthScheme = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    A2ASecretRef = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    A2ARemoteAgentId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExecutionMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ModelSwitchingJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    McpServerRefsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAgentTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupAgentTemplates_TenantGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TenantGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupBusinessRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    AgentType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RuleCategory = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuleKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuleValueJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PromptInjection = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    HookPoint = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HookRuleType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Pattern = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Replacement = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OrderInPack = table.Column<int>(type: "int", nullable: false),
                    StopOnMatch = table.Column<bool>(type: "bit", nullable: false),
                    MaxEvaluationMs = table.Column<int>(type: "int", nullable: false),
                    IsTemplate = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBusinessRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupBusinessRules_TenantGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TenantGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupLlmConfigs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    PlatformConfigRef = table.Column<int>(type: "int", nullable: true),
                    Provider = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ApiKey = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Model = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Endpoint = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DeploymentName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AvailableModelsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupLlmConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupLlmConfigs_PlatformLlmConfigs_PlatformConfigRef",
                        column: x => x.PlatformConfigRef,
                        principalTable: "PlatformLlmConfigs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_GroupLlmConfigs_TenantGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TenantGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupPromptOverrides",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    AgentType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Section = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CustomText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    MergeMode = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    IsTemplate = table.Column<bool>(type: "bit", nullable: false),
                    Version = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupPromptOverrides", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupPromptOverrides_TenantGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TenantGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupScheduledTasks",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    AgentType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ScheduleType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ScheduledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RunAtTime = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DayOfWeek = table.Column<int>(type: "int", nullable: true),
                    TimeZoneId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PayloadType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PromptText = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ParametersJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NextRunUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    NotifyEmails = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    NotifyOn = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    FailureKeywords = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupScheduledTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupScheduledTasks_TenantGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TenantGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RulePacks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<int>(type: "int", nullable: true),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Description = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Version = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    IsMandatory = table.Column<bool>(type: "bit", nullable: false),
                    AppliesToJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ActivationCondition = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParentPackId = table.Column<int>(type: "int", nullable: true),
                    MaxEvaluationMs = table.Column<int>(type: "int", nullable: false),
                    CreatedBy = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ModifiedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RulePacks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RulePacks_RulePacks_ParentPackId",
                        column: x => x.ParentPackId,
                        principalTable: "RulePacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RulePacks_TenantGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TenantGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "TenantGroupMembers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantGroupMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TenantGroupMembers_TenantGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TenantGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Sites",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    TimeZone = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sites", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sites_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupAgentOverlays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Guid = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    GroupTemplateId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    SystemPromptAddendum = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ModelId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Temperature = table.Column<double>(type: "float", nullable: true),
                    ExtraToolBindingsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CustomVariablesJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LlmConfigId = table.Column<int>(type: "int", nullable: true),
                    MaxOutputTokens = table.Column<int>(type: "int", nullable: true),
                    ActivatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupAgentOverlays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupAgentOverlays_GroupAgentTemplates_GroupTemplateId",
                        column: x => x.GroupTemplateId,
                        principalTable: "GroupAgentTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupAgentOverlays_TenantGroups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "TenantGroups",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "GroupScheduledTaskRuns",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    GroupTaskId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    GroupId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ScheduledForUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    ResponseText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ErrorMessage = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SessionId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    InputTokens = table.Column<int>(type: "int", nullable: true),
                    OutputTokens = table.Column<int>(type: "int", nullable: true),
                    IterationCount = table.Column<int>(type: "int", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupScheduledTaskRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupScheduledTaskRuns_GroupScheduledTasks_GroupTaskId",
                        column: x => x.GroupTaskId,
                        principalTable: "GroupScheduledTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BusinessRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Guid = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    TenantId = table.Column<int>(type: "int", nullable: false),
                    AgentType = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    AgentId = table.Column<string>(type: "nvarchar(450)", nullable: true),
                    RuleCategory = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuleKey = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuleValueJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PromptInjection = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    Priority = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RulePackId = table.Column<int>(type: "int", nullable: true),
                    HookPoint = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    HookRuleType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Pattern = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Replacement = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    OrderInPack = table.Column<int>(type: "int", nullable: false),
                    StopOnMatch = table.Column<bool>(type: "bit", nullable: false),
                    MaxEvaluationMs = table.Column<int>(type: "int", nullable: false),
                    SourceGroupRuleId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BusinessRules_RulePacks_RulePackId",
                        column: x => x.RulePackId,
                        principalTable: "RulePacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "HookRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PackId = table.Column<int>(type: "int", nullable: false),
                    HookPoint = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RuleType = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Pattern = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Instruction = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Replacement = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ToolName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MatchTarget = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    OrderInPack = table.Column<int>(type: "int", nullable: false),
                    StopOnMatch = table.Column<bool>(type: "bit", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    OverridesParentRuleId = table.Column<int>(type: "int", nullable: true),
                    MaxEvaluationMs = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HookRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HookRules_HookRules_OverridesParentRuleId",
                        column: x => x.OverridesParentRuleId,
                        principalTable: "HookRules",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_HookRules_RulePacks_PackId",
                        column: x => x.PackId,
                        principalTable: "RulePacks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgentGroups_TenantId",
                table: "AgentGroups",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentPromptHistory_TenantId_AgentId_Version",
                table: "AgentPromptHistory",
                columns: new[] { "TenantId", "AgentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_Status_CreatedAt",
                table: "AgentTasks",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTasks_TenantId_Status",
                table: "AgentTasks",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessRules_Guid",
                table: "BusinessRules",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessRules_RulePackId",
                table: "BusinessRules",
                column: "RulePackId");

            migrationBuilder.CreateIndex(
                name: "IX_BusinessRules_TenantId_AgentId_IsActive",
                table: "BusinessRules",
                columns: new[] { "TenantId", "AgentId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessRules_TenantId_AgentType_IsActive",
                table: "BusinessRules",
                columns: new[] { "TenantId", "AgentType", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_BusinessRules_TenantId_RulePackId",
                table: "BusinessRules",
                columns: new[] { "TenantId", "RulePackId" });

            migrationBuilder.CreateIndex(
                name: "IX_FewShotExamples_TenantId_AgentId_SortOrder",
                table: "FewShotExamples",
                columns: new[] { "TenantId", "AgentId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupAgentOverlays_GroupId",
                table: "GroupAgentOverlays",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupAgentOverlays_GroupTemplateId",
                table: "GroupAgentOverlays",
                column: "GroupTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupAgentOverlays_Guid",
                table: "GroupAgentOverlays",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupAgentOverlays_TenantId_GroupTemplateId",
                table: "GroupAgentOverlays",
                columns: new[] { "TenantId", "GroupTemplateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupAgentTemplates_GroupId_IsEnabled",
                table: "GroupAgentTemplates",
                columns: new[] { "GroupId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupBusinessRules_GroupId_AgentType_IsActive",
                table: "GroupBusinessRules",
                columns: new[] { "GroupId", "AgentType", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupLlmConfigs_GroupId_Name",
                table: "GroupLlmConfigs",
                columns: new[] { "GroupId", "Name" },
                unique: true,
                filter: "[Name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_GroupLlmConfigs_PlatformConfigRef",
                table: "GroupLlmConfigs",
                column: "PlatformConfigRef");

            migrationBuilder.CreateIndex(
                name: "IX_GroupPromptOverrides_GroupId_AgentType_IsActive",
                table: "GroupPromptOverrides",
                columns: new[] { "GroupId", "AgentType", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupScheduledTaskRuns_GroupTaskId_TenantId_Status",
                table: "GroupScheduledTaskRuns",
                columns: new[] { "GroupTaskId", "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_GroupScheduledTasks_GroupId_IsEnabled_NextRunUtc",
                table: "GroupScheduledTasks",
                columns: new[] { "GroupId", "IsEnabled", "NextRunUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HookRules_OverridesParentRuleId",
                table: "HookRules",
                column: "OverridesParentRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_HookRules_PackId_OrderInPack",
                table: "HookRules",
                columns: new[] { "PackId", "OrderInPack" });

            migrationBuilder.CreateIndex(
                name: "IX_LearnedRules_TenantId_Status",
                table: "LearnedRules",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LocalUsers_TenantId_Username",
                table: "LocalUsers",
                columns: new[] { "TenantId", "Username" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_McpCredentials_TenantId_Name",
                table: "McpCredentials",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationConfigs_TenantId_AgentId",
                table: "OptimizationConfigs",
                columns: new[] { "TenantId", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationRuns_TenantId_AgentId_StartedAt",
                table: "OptimizationRuns",
                columns: new[] { "TenantId", "AgentId", "StartedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationSuggestions_RunId",
                table: "OptimizationSuggestions",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_OptimizationSuggestions_TenantId_AgentId_Status",
                table: "OptimizationSuggestions",
                columns: new[] { "TenantId", "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformApiKeys_KeyHash",
                table: "PlatformApiKeys",
                column: "KeyHash");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformLlmConfigs_Name",
                table: "PlatformLlmConfigs",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuleExecutionLogs_PackId_RuleId_Timestamp",
                table: "RuleExecutionLogs",
                columns: new[] { "PackId", "RuleId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_RuleExecutionLogs_TenantId_Timestamp",
                table: "RuleExecutionLogs",
                columns: new[] { "TenantId", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_RulePackHistory_TenantId_PackId_Version",
                table: "RulePackHistory",
                columns: new[] { "TenantId", "PackId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RulePacks_GroupId",
                table: "RulePacks",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_RulePacks_ParentPackId",
                table: "RulePacks",
                column: "ParentPackId");

            migrationBuilder.CreateIndex(
                name: "IX_RulePacks_TenantId_IsEnabled_Priority",
                table: "RulePacks",
                columns: new[] { "TenantId", "IsEnabled", "Priority" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTaskRuns_ScheduledTaskId_Status",
                table: "ScheduledTaskRuns",
                columns: new[] { "ScheduledTaskId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTaskRuns_TenantId_CreatedAt",
                table: "ScheduledTaskRuns",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTasks_TenantId_IsEnabled_NextRunUtc",
                table: "ScheduledTasks",
                columns: new[] { "TenantId", "IsEnabled", "NextRunUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SchedulerFeedbacks_RunId",
                table: "SchedulerFeedbacks",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_SchedulerFeedbacks_TenantId_Status_SubmittedAt",
                table: "SchedulerFeedbacks",
                columns: new[] { "TenantId", "Status", "SubmittedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SessionMessages_SessionId",
                table: "SessionMessages",
                column: "SessionId");

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_TenantId_UserId_Status",
                table: "Sessions",
                columns: new[] { "TenantId", "UserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Sites_TenantId",
                table: "Sites",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_SsoConfigs_TenantId_Issuer",
                table: "SsoConfigs",
                columns: new[] { "TenantId", "Issuer" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantGroupMembers_GroupId_TenantId",
                table: "TenantGroupMembers",
                columns: new[] { "GroupId", "TenantId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantGroupMembers_TenantId",
                table: "TenantGroupMembers",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantLlmConfigs_TenantId_Name",
                table: "TenantLlmConfigs",
                columns: new[] { "TenantId", "Name" },
                unique: true,
                filter: "[Name] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TenantMcpServers_TenantId_Name",
                table: "TenantMcpServers",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_TenantId_Email",
                table: "UserProfiles",
                columns: new[] { "TenantId", "Email" },
                unique: true,
                filter: "[Email] <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_UserProfiles_TenantId_UserId",
                table: "UserProfiles",
                columns: new[] { "TenantId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WidgetConfigs_TenantId_IsActive",
                table: "WidgetConfigs",
                columns: new[] { "TenantId", "IsActive" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentDefinitions");

            migrationBuilder.DropTable(
                name: "AgentGroups");

            migrationBuilder.DropTable(
                name: "AgentPromptHistory");

            migrationBuilder.DropTable(
                name: "AgentTasks");

            migrationBuilder.DropTable(
                name: "BusinessRules");

            migrationBuilder.DropTable(
                name: "FewShotExamples");

            migrationBuilder.DropTable(
                name: "GroupAgentOverlays");

            migrationBuilder.DropTable(
                name: "GroupBusinessRules");

            migrationBuilder.DropTable(
                name: "GroupLlmConfigs");

            migrationBuilder.DropTable(
                name: "GroupPromptOverrides");

            migrationBuilder.DropTable(
                name: "GroupScheduledTaskRuns");

            migrationBuilder.DropTable(
                name: "HookRules");

            migrationBuilder.DropTable(
                name: "LearnedRules");

            migrationBuilder.DropTable(
                name: "LocalUsers");

            migrationBuilder.DropTable(
                name: "McpCredentials");

            migrationBuilder.DropTable(
                name: "OptimizationConfigs");

            migrationBuilder.DropTable(
                name: "OptimizationSuggestions");

            migrationBuilder.DropTable(
                name: "PlatformApiKeys");

            migrationBuilder.DropTable(
                name: "PromptOverrides");

            migrationBuilder.DropTable(
                name: "RuleExecutionLogs");

            migrationBuilder.DropTable(
                name: "RulePackHistory");

            migrationBuilder.DropTable(
                name: "ScheduledTaskRuns");

            migrationBuilder.DropTable(
                name: "SchedulerFeedbacks");

            migrationBuilder.DropTable(
                name: "SessionMessages");

            migrationBuilder.DropTable(
                name: "Sites");

            migrationBuilder.DropTable(
                name: "SsoConfigs");

            migrationBuilder.DropTable(
                name: "TenantFeedbackSettings");

            migrationBuilder.DropTable(
                name: "TenantGroupMembers");

            migrationBuilder.DropTable(
                name: "TenantLlmConfigs");

            migrationBuilder.DropTable(
                name: "TenantMcpServers");

            migrationBuilder.DropTable(
                name: "TenantNotificationSettings");

            migrationBuilder.DropTable(
                name: "UserProfiles");

            migrationBuilder.DropTable(
                name: "WidgetConfigs");

            migrationBuilder.DropTable(
                name: "GroupAgentTemplates");

            migrationBuilder.DropTable(
                name: "PlatformLlmConfigs");

            migrationBuilder.DropTable(
                name: "GroupScheduledTasks");

            migrationBuilder.DropTable(
                name: "RulePacks");

            migrationBuilder.DropTable(
                name: "OptimizationRuns");

            migrationBuilder.DropTable(
                name: "ScheduledTasks");

            migrationBuilder.DropTable(
                name: "Sessions");

            migrationBuilder.DropTable(
                name: "Tenants");

            migrationBuilder.DropTable(
                name: "TenantGroups");
        }
    }
}
