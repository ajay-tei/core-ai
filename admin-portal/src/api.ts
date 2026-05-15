import { storageKey } from "@/lib/brand";

const BASE = import.meta.env.VITE_API_URL ?? "http://localhost:5000";

// Fallback tenant ID from env — used when localStorage has no stored tenant yet
// (e.g. first request after SSO redirect before AuthCallback has run).
const DEFAULT_TENANT_ID = import.meta.env.VITE_TENANT_ID ?? "1";

/** Returns the stored SSO access token, or null when auth is disabled / not yet logged in. */
export function getStoredToken(): string | null
{
  return localStorage.getItem(storageKey("token"));
}

/** Returns auth headers to attach to every API request.
 *  Always includes X-Tenant-ID so opaque token validation works even when
 *  the provider doesn't include tenant context in its GetUserInfo response. */
function authHeaders(): Record<string, string>
{
  const token = getStoredToken();
  const tenantId = localStorage.getItem(storageKey("tenant_id")) ?? DEFAULT_TENANT_ID;
  const headers: Record<string, string> = {};
  if (token) headers["Authorization"] = `Bearer ${ token }`;
  headers["X-Tenant-ID"] = tenantId;   // always sent — required for opaque token routing
  return headers;
}

export interface AgentSummary
{
  id: string;
  name: string;
  displayName: string;
  agentType: string;
  status: string;
  isEnabled: boolean;
  createdAt: string;
  llmConfigId?: number;
  // Shared from a tenant group (read-only)
  isShared?: boolean;
  groupId?: number;
  groupName?: string;
  // Phase 18: overlay activation state for shared group templates
  isActivated?: boolean;
  overlayGuid?: string;
}

export interface AgentDefinition
{
  id?: string;
  tenantId?: number;
  name: string;
  displayName: string;
  description: string;
  agentType: string;
  systemPrompt?: string;
  modelId?: string;        // null/undefined = use global default
  temperature: number;
  maxIterations: number;
  capabilities?: string;   // JSON array string
  toolBindings?: string;   // JSON array string
  verificationMode?: string;       // Off|ToolGrounded|LlmVerifier|Strict|Auto
  contextWindowJson?: string;      // JSON ContextWindowOverrideOptions
  optimizationOverrideJson?: string; // JSON OptimizationOverrideOptions e.g. {"MergeMaxTokens":16384}
  customVariablesJson?: string;    // JSON Dictionary<string,string>
  maxContinuations?: number;       // null = global default
  maxToolResultChars?: number;     // null = global default (AgentOptions.MaxToolResultChars)
  maxOutputTokens?: number;        // null = global default (AgentOptions.MaxOutputTokens)
  enableHistoryCaching?: boolean;  // null = global default (AgentOptions.EnableHistoryCaching); Anthropic only
  pipelineStagesJson?: string;     // JSON {"Decompose":true,"Verify":false,...}
  toolFilterJson?: string;         // JSON {"mode":"allow","tools":[...]}
  stageInstructionsJson?: string;  // JSON {"Decompose":"...","Integrate":"..."}
  llmConfigId?: number;            // null = platform→group→tenant hierarchy; set = use named config by ID
  archetypeId?: string;            // archetype this agent was created from
  hooksJson?: string;              // JSON hook config
  a2aEndpoint?: string;            // remote A2A agent URL
  a2aAuthScheme?: string;          // "Bearer" | "ApiKey" | "None"
  a2aSecretRef?: string;           // vault key reference
  a2aRemoteAgentId?: string;       // ID of the specific agent on the remote Diva instance
  executionMode?: string;          // "Full" | "ChatOnly" | "ReadOnly" | "Supervised"
  modelSwitchingJson?: string;     // JSON per-iteration model switching config
  delegateAgentIdsJson?: string;   // JSON array of agent IDs for peer delegation
  isEnabled: boolean;
  status: string;
  version?: number;
  createdAt?: string;
  publishedAt?: string;
}

export interface LlmConfig
{
  availableModels: string[];
  currentProvider: string;
  defaultModel: string;
}

export interface AgentDefaults
{
  maxIterations: number;
  maxContinuations: number;
  defaultTemperature: number;
  maxToolResultChars: number;
  maxOutputTokens: number;
  enableHistoryCaching: boolean;
  injectToolStrategy: boolean;
  verificationMode: string;
  confidenceThreshold: number;
  maxVerificationRetries: number;
  contextWindow: {
    budgetTokens: number;
    compactionThreshold: number;
    keepLastRawMessages: number;
    maxHistoryTurns: number;
  };
  retry: {
    maxRetries: number;
    baseDelayMs: number;
  };
}

export interface McpToolBinding
{
  name: string;
  command: string;                       // e.g. "docker" or "npx"
  args: string[];                        // e.g. ["run", "-i", "--rm", "-e", "OWM_API_KEY", "mcp/openweather"]
  env: Record<string, string>;           // e.g. { "OWM_API_KEY": "abc123" }
  endpoint: string;                      // http: "http://localhost:8080/sse"
  transport: string;                     // "stdio" | "http" | "sse"
  passSsoToken: boolean;                 // inject Bearer + tenant headers into HTTP/SSE calls
  passTenantHeaders: boolean;            // inject tenant headers only (no Bearer)
  access?: string;                       // "ReadOnly" | "ReadWrite" | "Destructive"
  credentialRef?: string;                // references a named credential in the tenant vault
}

// ── Archetypes (Phase 15) ──────────────────────────────────────────────────────

export interface ArchetypeSummary
{
  id: string;
  displayName: string;
  description: string;
  icon: string;
  category: string;
}

export interface AgentArchetype extends ArchetypeSummary
{
  systemPromptTemplate: string;
  defaultCapabilities: string[];
  suggestedTools: string[];
  defaultHooks: Record<string, string>;
  defaultTemperature: number;
  defaultMaxIterations: number;
  defaultVerificationMode?: string;
  pipelineStageDefaults?: Record<string, boolean>;
  defaultExecutionMode: string;
}

// ── Tenants (platform admin) ───────────────────────────────────────────────────

// ── Rule Packs (Phase 16) ──────────────────────────────────────────────────────

export interface HookRule
{
  id: number;
  packId: number;
  hookPoint: string;
  ruleType: string;
  pattern?: string;
  instruction?: string;
  replacement?: string;
  toolName?: string;
  orderInPack: number;
  stopOnMatch: boolean;
  isEnabled: boolean;
  overridesParentRuleId?: number;
  maxEvaluationMs: number;
  matchTarget?: "query" | "response";
}

export interface RulePack
{
  id: number;
  tenantId: number;
  groupId?: number;
  name: string;
  description?: string;
  version: string;
  priority: number;
  isEnabled: boolean;
  isMandatory: boolean;
  appliesToJson?: string;
  activationCondition?: string;
  parentPackId?: number;
  maxEvaluationMs: number;
  createdBy?: string;
  createdAt: string;
  modifiedAt?: string;
  rules: HookRule[];
}

export interface CreateRulePackDto
{
  name: string;
  description?: string;
  groupId?: number;
  priority?: number;
  isMandatory?: boolean;
  appliesToJson?: string;
  activationCondition?: string;
  parentPackId?: number;
  maxEvaluationMs?: number;
}

export interface UpdateRulePackDto
{
  name: string;
  description?: string;
  version: string;
  priority: number;
  isEnabled: boolean;
  isMandatory: boolean;
  appliesToJson?: string;
  activationCondition?: string;
  maxEvaluationMs: number;
}

export interface CreateHookRuleDto
{
  hookPoint: string;
  ruleType: string;
  pattern?: string;
  instruction?: string;
  replacement?: string;
  toolName?: string;
  orderInPack?: number;
  stopOnMatch?: boolean;
  maxEvaluationMs?: number;
  matchTarget?: "query" | "response";
}

export interface UpdateHookRuleDto
{
  hookPoint: string;
  ruleType: string;
  pattern?: string;
  instruction?: string;
  replacement?: string;
  toolName?: string;
  orderInPack: number;
  isEnabled: boolean;
  stopOnMatch: boolean;
  maxEvaluationMs: number;
  matchTarget?: "query" | "response";
}

export interface ConflictWarning
{
  severity: "Info" | "Warning" | "Error";
  message: string;
}

export interface ConflictAnalysis
{
  packId: number;
  internal: ConflictWarning[];
  crossPack: ConflictWarning[];
}

export interface RulePackTestResult
{
  modifiedPrompt: string;
  modifiedResponse: string;
  triggeredRules: { ruleId: number; ruleType: string; action: string; }[];
  blocked: boolean;
  modelSwitchRequest?: { modelId?: string; llmConfigId?: number; maxTokens?: number; } | null;
}

export interface RulePackExport
{
  name: string;
  description?: string;
  version: string;
  priority: number;
  isMandatory: boolean;
  appliesToJson?: string;
  activationCondition?: string;
  maxEvaluationMs: number;
  rules: {
    orderInPack: number;
    hookPoint: string;
    ruleType: string;
    pattern?: string;
    instruction?: string;
    replacement?: string;
    toolName?: string;
    stopOnMatch: boolean;
    maxEvaluationMs: number;
  }[];
}

// ── Agent Export / Import ──────────────────────────────────────────────────────

export interface AgentExportRule
{
  agentType: string;
  ruleCategory: string;
  ruleKey: string;
  ruleValueJson?: string;
  promptInjection?: string;
  isActive: boolean;
  priority: number;
  hookPoint: string;
  hookRuleType: string;
  pattern?: string;
  replacement?: string;
  toolName?: string;
  orderInPack: number;
  stopOnMatch: boolean;
  maxEvaluationMs: number;
}

export interface AgentExportDefinition extends Omit<AgentDefinition, "id" | "tenantId" | "createdAt" | "publishedAt" | "version">
{
  delegateAgentNames: string[];
}

export interface AgentExportBundle
{
  schemaVersion: string;
  exportedAt: string;
  sourceTenantId: number;
  agent: AgentExportDefinition;
  rules: AgentExportRule[];
}

export interface AgentImportResult
{
  agentId: string;
  agentName: string;
  rulesImported: number;
  warnings: string[];
}

export interface AgentImportOptions
{
  overwriteExisting?: boolean;
  importRules?: boolean;
  newAgentName?: string;
}

export interface Tenant
{
  id: number;
  name: string;
  isActive: boolean;
  createdAt: string;
  liteLLMTeamId?: string;
  siteCount: number;
}

export interface CreateTenantDto
{
  name: string;
  liteLLMTeamId?: string;
  liteLLMTeamKey?: string;
}

export interface UpdateTenantDto
{
  name: string;
  isActive: boolean;
  liteLLMTeamId?: string;
  liteLLMTeamKey?: string;
}

// ── Local Users ────────────────────────────────────────────────────────────────

export interface LocalUser
{
  id: number;
  username: string;
  email: string;
  displayName: string;
  roles: string[];
  isActive: boolean;
  createdAt: string;
  lastLoginAt?: string;
}

export interface CreateLocalUserDto
{
  username: string;
  email: string;
  password: string;
  displayName: string;
  roles: string[];
}

// ── SSO Configuration ──────────────────────────────────────────────────────────

/** Public provider entry returned by GET /api/auth/providers — no secrets. */
export interface SsoProvider
{
  id: number;
  tenantId: number;
  providerName: string;
  tenantName: string;
}

export interface SsoConfig
{
  id: number;
  tenantId: number;
  providerName: string;                  // "google" | "azure" | "okta" | "generic"
  issuer: string;
  clientId: string;
  clientSecret: string;                  // masked in UI
  tokenType: string;                     // "jwt" | "opaque"
  authority?: string;
  authorizationEndpoint?: string;
  tokenEndpoint?: string;
  userinfoEndpoint?: string;
  introspectionEndpoint?: string;
  audience: string;
  proxyBaseUrl: string;
  proxyAdminEmail?: string;
  useRoleMappings: boolean;
  useTeamMappings: boolean;
  claimMappingsJson?: string;
  logoutUrl?: string;
  emailDomains?: string;   // comma-separated: "contoso.com,contoso.onmicrosoft.com"
  isActive: boolean;
  createdAt: string;
  updatedAt?: string;
}

export type CreateSsoConfigDto = Omit<SsoConfig, "id" | "tenantId" | "isActive" | "createdAt" | "updatedAt">;
export type UpdateSsoConfigDto = Partial<Omit<SsoConfig, "id" | "tenantId" | "createdAt">> & { isActive: boolean; };

// ── Widget Config ──────────────────────────────────────────────────────────────

export interface WidgetThemeDto
{
  background: string;
  surface: string;
  border: string;
  primary: string;
  primaryText: string;
  text: string;
  textMuted: string;
  fontFamily: string;
  fontSize: string;
  agentBubbleBg: string;
  agentBubbleText: string;
  headerBg: string;
  headerText: string;
  inputBg: string;
  inputBorder: string;
  inputText: string;
  launcherSize: number;
  preset?: string;
}

export interface WidgetConfigDto
{
  id: string;
  tenantId: number;
  agentId: string;
  name: string;
  allowedOrigins: string[];
  ssoConfigId?: number;
  allowAnonymous: boolean;
  welcomeMessage?: string;
  placeholderText?: string;
  theme: WidgetThemeDto;
  respectSystemTheme: boolean;
  showBranding: boolean;
  isActive: boolean;
  createdAt: string;
  expiresAt?: string;
}

export interface CreateWidgetRequest
{
  agentId: string;
  name: string;
  allowedOrigins: string[];
  ssoConfigId?: number;
  allowAnonymous: boolean;
  welcomeMessage?: string;
  placeholderText?: string;
  theme?: WidgetThemeDto;
  respectSystemTheme: boolean;
  showBranding: boolean;
  expiresAt?: string;
}

// ── User Profiles ──────────────────────────────────────────────────────────────

export interface UserProfile
{
  id: number;
  tenantId: number;
  userId: string;
  email: string;
  displayName: string;
  avatarUrl?: string;
  roles: string[];
  agentAccess: string[];
  agentAccessOverrides: string[];
  isActive: boolean;
  createdAt: string;
  lastLoginAt: string;
  metadataJson?: string;
}

export interface UpdateUserProfileDto
{
  displayName: string;
  avatarUrl?: string;
  agentAccessOverrides: string[];
  metadataJson?: string;
}

export interface VerificationResult
{
  isVerified: boolean;
  confidence: number;         // 0.0–1.0
  mode: string;               // "Off" | "ToolGrounded" | "LlmVerifier" | "Strict"
  ungroundedClaims: string[];
  wasBlocked: boolean;
  reasoning?: string;
}

export interface FollowUpQuestion
{
  type: string;          // "rule_confirmation" | "clarification"
  text: string;
  options: string[];
  metadata?: SuggestedRule;
}

export interface SuggestedRule
{
  agentType?: string;
  ruleCategory: string;
  ruleKey: string;
  promptInjection: string;
  sourceSessionId: string;
  confidence: number;
  suggestedAt: string;
}

export interface BusinessRule
{
  id: number;
  guid: string;
  tenantId: number;
  agentType: string;
  /** When set, rule applies only to this specific agent ID. When absent, applies to all agents of agentType. */
  agentId?: string;
  ruleCategory: string;
  ruleKey: string;
  promptInjection?: string;
  ruleValueJson?: string;
  isActive: boolean;
  priority: number;
  createdAt: string;
  // Hook integration fields
  rulePackId?: number;
  hookPoint: string;
  hookRuleType: string;
  pattern?: string;
  replacement?: string;
  toolName?: string;
  orderInPack: number;
  stopOnMatch: boolean;
  maxEvaluationMs: number;
  /** When set, this rule was activated from a group rule template. */
  sourceGroupRuleId?: number;
}

export interface PromptOverride
{
  id: number;
  tenantId: number;
  agentType: string;
  agentId?: string;
  section: string;
  customText: string;
  mergeMode: string;
  isActive: boolean;
  version: number;
  createdAt: string;
}

export interface DashboardStats
{
  agentCount: number;
  activeRuleCount: number;
  pendingRuleCount: number;
  sessionCount: number;
  asOf: string;
}

// ── Tenant Groups (Phase 15.5) ────────────────────────────────────────────────

export interface TenantGroup
{
  id: number;
  name: string;
  description?: string;
  isActive: boolean;
  createdAt: string;
  memberCount: number;
}

export interface TenantGroupDetail extends TenantGroup
{
  llmConfig?: GroupLlmConfig;
}

export interface GroupMember
{
  id: number;
  groupId: number;
  tenantId: number;
  joinedAt: string;
}

export interface GroupAgentTemplate
{
  id: string;
  groupId: number;
  name: string;
  displayName: string;
  agentType: string;
  description?: string;
  systemPrompt?: string;
  modelId?: string;
  temperature: number;
  maxIterations: number;
  capabilities?: string;
  toolBindings?: string;
  verificationMode?: string;
  contextWindowJson?: string;
  customVariablesJson?: string;
  maxContinuations?: number;
  maxToolResultChars?: number;
  maxOutputTokens?: number;
  pipelineStagesJson?: string;
  toolFilterJson?: string;
  stageInstructionsJson?: string;
  llmConfigId?: number;
  // Phase-15 fields (mirrored from AgentDefinition)
  archetypeId?: string;
  hooksJson?: string;
  a2aEndpoint?: string;
  a2aAuthScheme?: string;
  a2aSecretRef?: string;
  a2aRemoteAgentId?: string;
  executionMode?: string;
  modelSwitchingJson?: string;
  isEnabled: boolean;
  status: string;
  version?: number;
  createdAt: string;
  updatedAt?: string;
}

export interface GroupBusinessRule
{
  id: number;
  groupId: number;
  agentType: string;
  ruleCategory: string;
  ruleKey: string;
  promptInjection?: string;
  ruleValueJson?: string;
  isActive: boolean;
  priority: number;
  createdAt: string;
  // Hook pipeline fields
  hookPoint: string;
  hookRuleType: string;
  pattern?: string;
  replacement?: string;
  toolName?: string;
  orderInPack: number;
  stopOnMatch: boolean;
  maxEvaluationMs: number;
  /** When true, this rule is offered as opt-in template to member tenants rather than auto-injected. */
  isTemplate: boolean;
}

/** A group rule template with tenant activation status. */
export interface GroupRuleTemplateItem
{
  id: number;
  groupId: number;
  groupName: string;
  agentType: string;
  ruleCategory: string;
  ruleKey: string;
  promptInjection?: string;
  priority: number;
  hookPoint: string;
  hookRuleType: string;
  pattern?: string;
  replacement?: string;
  toolName?: string;
  orderInPack: number;
  stopOnMatch: boolean;
  maxEvaluationMs: number;
  isActivated: boolean;
  activatedRuleId?: number;
}

export interface GroupPromptOverride
{
  id: number;
  groupId: number;
  agentType: string;
  section: string;
  customText: string;
  mergeMode: string;
  isActive: boolean;
  isTemplate: boolean;
  version: number;
  createdAt: string;
}

export interface GroupPromptTemplateItem
{
  id: number;
  groupId: number;
  groupName: string;
  agentType: string;
  section: string;
  customText: string;
  mergeMode: string;
  isActivated: boolean;
  activatedOverrideId?: number;
}

export interface GroupScheduledTask
{
  id: string;
  groupId: number;
  agentType: string;          // resolved to first matching enabled agent per member tenant
  name: string;
  description?: string;
  scheduleType: string;       // "once" | "daily" | "weekly"
  scheduledAtUtc?: string;
  runAtTime?: string;
  dayOfWeek?: number;
  timeZoneId: string;
  payloadType: string;
  promptText: string;
  parametersJson?: string;
  isEnabled: boolean;
  nextRunUtc?: string;
  createdAt: string;
  updatedAt?: string;
}

export interface GroupLlmConfig
{
  id: number;
  groupId: number;
  name?: string;
  provider?: string;
  apiKey?: string;            // "••••••••" if set
  model?: string;
  endpoint?: string;
  deploymentName?: string;
  availableModelsJson?: string;
  updatedAt: string;
  /** Set when this group config is a reference to a platform config (no own credentials). */
  platformConfigRef?: number;
}

// ── Platform & Tenant LLM Config (DB-backed) ──────────────────────────────────

export interface PlatformLlmConfig
{
  id: number;
  name: string;               // required unique display name
  provider: string;
  apiKey?: string;            // "••••••••" if set
  model: string;
  endpoint?: string;
  deploymentName?: string;
  availableModelsJson?: string;
  updatedAt: string;
  seededFromAppSettings?: boolean;
}

export interface CreatePlatformLlmConfigDto
{
  name: string;
  provider?: string;
  apiKey?: string;
  model?: string;
  endpoint?: string;
  deploymentName?: string;
  availableModelsJson?: string;
}

export interface AddGroupPlatformRefDto
{
  platformConfigId: number;
  nameOverride?: string;
}

export interface TenantLlmConfig
{
  id: number;
  tenantId: number;
  name?: string;              // null = default unnamed config; non-null = named config
  provider?: string;
  apiKey?: string;            // "••••••••" if set
  model?: string;
  endpoint?: string;
  deploymentName?: string;
  availableModelsJson?: string;
  updatedAt: string;
}

export interface UpsertLlmConfigDto
{
  provider?: string;
  apiKey?: string;
  model?: string;
  endpoint?: string;
  deploymentName?: string;
  availableModelsJson?: string;
}

export interface CreateNamedLlmConfigDto
{
  name?: string;
  provider?: string;
  apiKey?: string;
  model?: string;
  endpoint?: string;
  deploymentName?: string;
  availableModelsJson?: string;
}

/** Lightweight summary for the agent-builder LLM config picker. */
export interface AvailableLlmConfig
{
  id: number;
  /** "tenant" or "group:{groupId}" */
  source: string;
  name?: string;
  /** Human-readable label for the picker, e.g. "OpenAI Production" or "Default — Acme Group" */
  displayName: string;
  provider?: string;
  model?: string;
  availableModels: string[];
  /** True when this is a group config that references a platform config (no own credentials). */
  isRef?: boolean;
  /** "platform" when isRef is true. */
  refSource?: string;
}

export type CreateGroupAgentDto = Omit<GroupAgentTemplate, "id" | "groupId" | "version" | "createdAt" | "updatedAt">;
export type UpdateGroupAgentDto = Partial<CreateGroupAgentDto>;
export type CreateGroupRuleDto = Omit<GroupBusinessRule, "id" | "groupId" | "isActive" | "createdAt">;
export type UpdateGroupRuleDto = Partial<CreateGroupRuleDto> & { isActive?: boolean; };
export type CreateGroupPromptOverrideDto = Omit<GroupPromptOverride, "id" | "groupId" | "isActive" | "version" | "createdAt">;
export type UpdateGroupPromptOverrideDto = Partial<CreateGroupPromptOverrideDto> & { isActive?: boolean; };
export type CreateGroupTaskDto = Omit<GroupScheduledTask, "id" | "groupId" | "nextRunUtc" | "createdAt" | "updatedAt">;
export type UpdateGroupTaskDto = Partial<CreateGroupTaskDto>;

// ── MCP Credentials (tenant-scoped vault) ─────────────────────────────────────

export interface McpCredential
{
  id: number;
  name: string;
  authScheme: string;          // "Bearer" | "ApiKey" | "Custom"
  customHeaderName?: string;
  description?: string;
  createdAt: string;
  expiresAt?: string;
  isActive: boolean;
  lastUsedAt?: string;
  createdByUserId?: string;
}

export interface CreateCredentialDto
{
  name: string;
  apiKey: string;
  authScheme?: string;
  customHeaderName?: string;
  description?: string;
  expiresAt?: string;
  tenantId?: number;
}

export interface UpdateCredentialDto
{
  name?: string;
  authScheme?: string;
  customHeaderName?: string;
  description?: string;
  expiresAt?: string;
  isActive?: boolean;
  newApiKey?: string;
  tenantId?: number;
}

// ── Platform API Keys ─────────────────────────────────────────────────────────

export interface PlatformApiKey
{
  id: number;
  name: string;
  keyPrefix: string;
  scope: string;               // "admin" | "invoke" | "readonly"
  allowedAgentIds?: string[];
  createdAt: string;
  expiresAt?: string;
  isActive: boolean;
  lastUsedAt?: string;
  createdByUserId?: string;
}

export interface ApiKeyCreatedResult
{
  id: number;
  name: string;
  keyPrefix: string;
  rawKey: string;              // shown once
  scope: string;
  expiresAt?: string;
}

export interface CreateApiKeyDto
{
  name: string;
  scope?: string;
  allowedAgentIds?: string[];
  expiresAt?: string;
  tenantId?: number;
}

// ── Scheduler types ───────────────────────────────────────────────────────────

export interface ScheduledTask
{
  id: string;
  tenantId: number;
  agentId: string;
  name: string;
  description?: string;
  scheduleType: string;              // "once" | "daily" | "weekly"
  scheduledAtUtc?: string;           // ISO — for "once"
  runAtTime?: string;                // "HH:mm" — for daily/weekly
  dayOfWeek?: number;                // 0–6 (Sunday=0) — for weekly
  timeZoneId: string;
  payloadType: string;               // "prompt" | "template"
  promptText: string;
  parametersJson?: string;           // JSON dict for template substitution
  isEnabled: boolean;
  lastRunAtUtc?: string;
  nextRunUtc?: string;
  createdAt: string;
  updatedAt?: string;
}

export interface ScheduledTaskRun
{
  id: string;
  tenantId: number;
  scheduledTaskId: string;
  status: string;                    // "pending" | "running" | "success" | "failed" | "skipped"
  scheduledForUtc: string;
  startedAtUtc?: string;
  completedAtUtc?: string;
  durationMs?: number;
  responseText?: string;
  errorMessage?: string;
  sessionId?: string;
  attemptNumber: number;
  createdAt: string;
}

export type CreateScheduleDto = Omit<ScheduledTask, "id" | "tenantId" | "lastRunAtUtc" | "nextRunUtc" | "createdAt" | "updatedAt">;
export type UpdateScheduleDto = Partial<CreateScheduleDto>;

// Export / Import types
export interface ScheduledTaskExport
{
  agentId: string;
  name: string;
  description?: string;
  scheduleType: string;
  scheduledAtUtc?: string;
  runAtTime?: string;
  dayOfWeek?: number;
  timeZoneId: string;
  payloadType: string;
  promptText: string;
  parametersJson?: string;
  isEnabled: boolean;
}

export interface ScheduleExportEnvelope
{
  version: "1";
  exportedAt: string;
  type: "tenant-schedules" | "group-schedules";
  tasks: ScheduledTaskExport[];
}

export interface ScheduleImportRequest
{
  tasks: ScheduledTaskExport[];
  skipConflicts: boolean;
}

export interface ScheduleImportResult
{
  created: number;
  skipped: number;
  skippedNames: string[];
}

// Group schedule export types (agentType instead of agentId)
export type GroupScheduledTaskExport = Omit<ScheduledTaskExport, "agentId"> & { agentType: string; };
export type GroupScheduleImportRequest = { tasks: GroupScheduledTaskExport[]; skipConflicts: boolean; };
export type GroupScheduleImportResult = ScheduleImportResult;

export interface AgentResponse
{
  success: boolean;
  content?: string;
  errorMessage?: string;
  agentName?: string;
  sessionId?: string;
  toolsUsed?: string[];
  executionTime?: string;
  toolEvidence?: string;
  verification?: VerificationResult;
  followUpQuestions?: FollowUpQuestion[];
}

// SSE stream chunk — mirrors AgentStreamChunk.cs
export interface AgentStreamChunk
{
  type: string;               // tools_available|plan|plan_revised|iteration_start|text_delta|thinking|tool_call|tool_result|continuation_start|final_response|verification|rule_suggestion|hook_executed|token_usage|a2a_delegation_start|error|done
  iteration?: number;
  content?: string;
  toolName?: string;
  toolInput?: string;         // JSON string
  toolOutput?: string;
  verification?: VerificationResult;
  followUpQuestions?: FollowUpQuestion[];
  sessionId?: string;
  executionTime?: string;
  errorMessage?: string;
  toolCount?: number;
  toolNames?: string[];
  planSteps?: string[];
  planText?: string;
  continuationWindow?: number; // 1-based window number; present on continuation_start events
  hookName?: string;
  hookPoint?: string;
  hookDurationMs?: number;
  rulePackTriggeredCount?: number;
  rulePackTriggeredRules?: string[];
  rulePackFilteredCount?: number;
  rulePackErrorAction?: string;
  rulePackBlocked?: boolean;
  // a2a_delegation_start fields
  a2aTaskId?: string;
  delegatedAgentId?: string;
  delegatedAgentName?: string;
  // token_usage fields (replaces cache_stats)
  iterationInputTokens?: number;
  iterationOutputTokens?: number;
  totalInputTokens?: number;
  totalOutputTokens?: number;
  iterationCacheRead?: number;
  iterationCacheCreation?: number;
  totalCacheRead?: number;
  totalCacheCreation?: number;
}

async function request<T>(path: string, init?: RequestInit): Promise<T>
{
  const res = await fetch(`${ BASE }${ path }`, {
    headers: { "Content-Type": "application/json", ...authHeaders() },
    ...init,
  });
  if (!res.ok)
  {
    if (res.status === 401)
    {
      // Token missing or expired — clear stale token and redirect to login
      localStorage.removeItem(storageKey("token"));
      window.location.replace("/login");
      return new Promise(() => { }); // halt execution while redirecting
    }
    const body = await res.text().catch(() => "");
    if (res.status === 429)
    {
      throw new Error("Rate limit exceeded. Please try again shortly.");
    }
    throw new Error(`${ res.status } ${ res.statusText }: ${ body }`);
  }
  if (res.status === 204) return undefined as T;
  return res.json();
}

export interface McpToolInfo
{
  name: string;
  description: string;
}

export interface McpProbeResult
{
  success: boolean;
  tools: McpToolInfo[];
  error?: string;
}

export const api = {
  listAgents: () => request<AgentSummary[]>("/api/agents"),
  getAgent: (id: string) => request<AgentDefinition>(`/api/agents/${ id }`),
  createAgent: (dto: AgentDefinition) => request<AgentDefinition>("/api/agents", { method: "POST", body: JSON.stringify(dto) }),
  updateAgent: (id: string, dto: AgentDefinition) => request<AgentDefinition>(`/api/agents/${ id }`, { method: "PUT", body: JSON.stringify(dto) }),
  improvePrompt: (id: string, instruction: string) =>
    request<{ improvedPrompt: string; }>(`/api/agents/${ id }/prompt/improve`, { method: "POST", body: JSON.stringify({ instruction }) }),
  deleteAgent: (id: string) => request<void>(`/api/agents/${ id }`, { method: "DELETE" }),
  exportAgent: (id: string) => request<AgentExportBundle>(`/api/agents/${ id }/export`),
  importAgent: (bundle: AgentExportBundle, opts?: AgentImportOptions) =>
  {
    const params = new URLSearchParams();
    if (opts?.overwriteExisting) params.set("overwrite", "true");
    if (opts?.importRules === false) params.set("importRules", "false");
    const qs = params.toString();
    return request<AgentImportResult>(`/api/agents/import${ qs ? `?${ qs }` : "" }`, { method: "POST", body: JSON.stringify(bundle) });
  },
  getLlmConfig: (llmConfigId?: number) =>
    request<LlmConfig>(llmConfigId ? `/api/config/llm?llmConfigId=${ llmConfigId }` : "/api/config/llm"),
  getAgentDefaults: () => request<AgentDefaults>("/api/config/agent-defaults"),
  probeMcp: (opts: { endpoint?: string; command?: string; args?: string[]; passSsoToken?: boolean; credentialRef?: string; }) =>
    request<McpProbeResult>("/api/agents/mcp-probe", { method: "POST", body: JSON.stringify(opts) }),
  invokeAgent: (id: string, query: string, sessionId?: string) =>
    request<AgentResponse>(`/api/agents/${ id }/invoke`, { method: "POST", body: JSON.stringify({ query, sessionId }) }),

  /** Stream agent execution — calls onChunk for each SSE event, resolves when done. */
  streamAgent: (
    id: string,
    query: string,
    sessionId: string | undefined,
    onChunk: (chunk: AgentStreamChunk) => void,
    signal?: AbortSignal,
    modelId?: string,
    llmConfigId?: number,
    forwardSsoToMcp = true,
  ): Promise<void> =>
  {
    return (async () =>
    {
      const res = await fetch(`${ BASE }/api/agents/${ id }/invoke/stream`, {
        method: "POST",
        headers: { "Content-Type": "application/json", ...authHeaders() },
        body: JSON.stringify({ query, sessionId, modelId: modelId || undefined, llmConfigId, forwardSsoToMcp }),
        signal,
      });
      if (!res.ok || !res.body) throw new Error(`${ res.status } ${ res.statusText }`);
      const reader = res.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";
      while (true)
      {
        const { done, value } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split("\n");
        buffer = lines.pop()!;
        for (const line of lines)
        {
          if (!line.startsWith("data: ")) continue;
          const json = line.slice(6).trim();
          if (!json) continue;
          onChunk(JSON.parse(json) as AgentStreamChunk);
        }
      }
    })();
  },

  // Learned rules (Phase 11)
  getPendingRules: (tenantId = 1) =>
    request<SuggestedRule[]>(`/api/learned-rules?tenantId=${ tenantId }`),
  approveRule: (id: number, tenantId = 1) =>
    request<void>(`/api/learned-rules/${ id }/approve?tenantId=${ tenantId }`, { method: "POST" }),
  rejectRule: (id: number, notes: string, tenantId = 1) =>
    request<void>(`/api/learned-rules/${ id }/reject?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify({ notes }) }),

  // Business rules (Phase 6 / Tier 2)
  getBusinessRules: (tenantId = 1, agentType = "*", agentId?: string) =>
    request<BusinessRule[]>(`/api/admin/business-rules?tenantId=${ tenantId }&agentType=${ encodeURIComponent(agentType) }${ agentId ? `&agentId=${ encodeURIComponent(agentId) }` : "" }`),
  createBusinessRule: (dto: Omit<BusinessRule, "id" | "tenantId" | "isActive" | "createdAt" | "ruleValueJson"> & { ruleValueJson?: string; }, tenantId = 1) =>
    request<BusinessRule>(`/api/admin/business-rules?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  updateBusinessRule: (id: number, dto: Partial<BusinessRule>, tenantId = 1) =>
    request<BusinessRule>(`/api/admin/business-rules/${ id }?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteBusinessRule: (id: number, tenantId = 1) =>
    request<void>(`/api/admin/business-rules/${ id }?tenantId=${ tenantId }`, { method: "DELETE" }),
  getBusinessRulesByPack: (packId: number, tenantId = 1) =>
    request<BusinessRule[]>(`/api/admin/business-rules/by-pack/${ packId }?tenantId=${ tenantId }`),
  assignBusinessRuleToPack: (id: number, rulePackId: number | null, tenantId = 1) =>
    request<void>(`/api/admin/business-rules/${ id }/assign-pack?tenantId=${ tenantId }`, {
      method: "POST",
      body: JSON.stringify({ rulePackId }),
    }),
  unassignBusinessRuleFromPack: (id: number, tenantId = 1) =>
    request<void>(`/api/admin/business-rules/${ id }/unassign-pack?tenantId=${ tenantId }`, { method: "POST" }),
  validateBusinessRuleHook: (hookPoint: string, hookRuleType: string) =>
    request<{ valid: boolean; allowedTypes: string[]; }>("/api/admin/business-rules/validate", {
      method: "POST",
      body: JSON.stringify({ hookPoint, hookRuleType }),
    }),

  // Group rule templates
  getGroupRuleTemplates: (tenantId = 1) =>
    request<GroupRuleTemplateItem[]>(`/api/admin/group-rule-templates?tenantId=${ tenantId }`),
  activateGroupRuleTemplate: (groupRuleId: number, tenantId = 1) =>
    request<BusinessRule>(`/api/admin/group-rule-templates/${ groupRuleId }/activate?tenantId=${ tenantId }`, { method: "POST" }),
  deactivateGroupRuleTemplate: (groupRuleId: number, tenantId = 1) =>
    request<void>(`/api/admin/group-rule-templates/${ groupRuleId }/activate?tenantId=${ tenantId }`, { method: "DELETE" }),

  // Group prompt templates
  getGroupPromptTemplates: (tenantId = 1) =>
    request<GroupPromptTemplateItem[]>(`/api/admin/group-prompt-templates?tenantId=${ tenantId }`),
  activateGroupPromptTemplate: (groupOverrideId: number, tenantId = 1) =>
    request<PromptOverride>(`/api/admin/group-prompt-templates/${ groupOverrideId }/activate?tenantId=${ tenantId }`, { method: "POST" }),
  deactivateGroupPromptTemplate: (groupOverrideId: number, tenantId = 1) =>
    request<void>(`/api/admin/group-prompt-templates/${ groupOverrideId }/activate?tenantId=${ tenantId }`, { method: "DELETE" }),

  // Prompt overrides (Phase 6 / Tier 2)
  getPromptOverrides: (tenantId = 1, agentType?: string, agentId?: string) =>
    request<PromptOverride[]>(`/api/admin/prompt-overrides?tenantId=${ tenantId }${ agentType && agentType !== '*' ? `&agentType=${ encodeURIComponent(agentType) }` : '' }${ agentId ? `&agentId=${ encodeURIComponent(agentId) }` : '' }`),
  createPromptOverride: (dto: Pick<PromptOverride, "agentType" | "agentId" | "section" | "customText" | "mergeMode">, tenantId = 1) =>
    request<PromptOverride>(`/api/admin/prompt-overrides?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  updatePromptOverride: (id: number, dto: Pick<PromptOverride, "customText" | "mergeMode" | "isActive">, tenantId = 1) =>
    request<PromptOverride>(`/api/admin/prompt-overrides/${ id }?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deletePromptOverride: (id: number, tenantId = 1) =>
    request<void>(`/api/admin/prompt-overrides/${ id }?tenantId=${ tenantId }`, { method: "DELETE" }),

  // Dashboard (Phase 10 / Tier 2)
  getDashboard: (tenantId = 1) =>
    request<DashboardStats>(`/api/admin/dashboard?tenantId=${ tenantId }`),

  // Public SSO provider list — used by login page, no auth required
  listSsoProviders: () =>
    fetch(`${ BASE }/api/auth/providers`).then(r => r.json() as Promise<SsoProvider[]>),

  // Tenants — platform admin only
  listTenants: () => request<Tenant[]>("/api/platform/tenants"),
  getTenant: (id: number) => request<Tenant & { sites: { id: number; name: string; timeZone?: string; isActive: boolean; }[]; }>(`/api/platform/tenants/${ id }`),
  createTenant: (dto: CreateTenantDto) =>
    request<Tenant>("/api/platform/tenants", { method: "POST", body: JSON.stringify(dto) }),
  updateTenant: (id: number, dto: UpdateTenantDto) =>
    request<Tenant>(`/api/platform/tenants/${ id }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteTenant: (id: number) =>
    request<void>(`/api/platform/tenants/${ id }`, { method: "DELETE" }),

  // Local users — managed per tenant
  listLocalUsers: (tenantId: number) =>
    request<LocalUser[]>(`/api/auth/local-users?tenantId=${ tenantId }`),
  createLocalUser: (dto: CreateLocalUserDto, tenantId: number) =>
    request<LocalUser>(`/api/auth/local-users?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  deleteLocalUser: (id: number, tenantId: number) =>
    request<void>(`/api/auth/local-users/${ id }?tenantId=${ tenantId }`, { method: "DELETE" }),
  resetLocalUserPassword: (id: number, newPassword: string, tenantId: number) =>
    request<void>(`/api/auth/local-users/${ id }/reset-password?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify({ newPassword }) }),

  // SSO configurations (Phase 2)
  listSsoConfigs: (tenantId = 1) =>
    request<SsoConfig[]>(`/api/admin/sso-configs?tenantId=${ tenantId }`),
  getSsoConfig: (id: number, tenantId = 1) =>
    request<SsoConfig>(`/api/admin/sso-configs/${ id }?tenantId=${ tenantId }`),
  createSsoConfig: (dto: CreateSsoConfigDto, tenantId = 1) =>
    request<SsoConfig>(`/api/admin/sso-configs?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  updateSsoConfig: (id: number, dto: UpdateSsoConfigDto, tenantId = 1) =>
    request<SsoConfig>(`/api/admin/sso-configs/${ id }?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteSsoConfig: (id: number, tenantId = 1) =>
    request<void>(`/api/admin/sso-configs/${ id }?tenantId=${ tenantId }`, { method: "DELETE" }),

  // User profiles (Phase 3)
  listUserProfiles: (tenantId = 1, search?: string, role?: string) =>
  {
    const params = new URLSearchParams({ tenantId: String(tenantId) });
    if (search) params.set("search", search);
    if (role) params.set("role", role);
    return request<UserProfile[]>(`/api/admin/user-profiles?${ params }`);
  },
  getUserProfile: (id: number, tenantId = 1) =>
    request<UserProfile>(`/api/admin/user-profiles/${ id }?tenantId=${ tenantId }`),
  updateUserProfile: (id: number, dto: UpdateUserProfileDto, tenantId = 1) =>
    request<void>(`/api/admin/user-profiles/${ id }?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  disableUser: (id: number, tenantId = 1) =>
    request<void>(`/api/admin/user-profiles/${ id }/disable?tenantId=${ tenantId }`, { method: "POST" }),
  enableUser: (id: number, tenantId = 1) =>
    request<void>(`/api/admin/user-profiles/${ id }/enable?tenantId=${ tenantId }`, { method: "POST" }),

  // Scheduler (Phase 15)
  listSchedules: (tenantId = 1) =>
    request<ScheduledTask[]>(`/api/schedules?tenantId=${ tenantId }`),
  getSchedule: (id: string, tenantId = 1) =>
    request<ScheduledTask>(`/api/schedules/${ id }?tenantId=${ tenantId }`),
  createSchedule: (dto: CreateScheduleDto, tenantId = 1) =>
    request<ScheduledTask>(`/api/schedules?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  updateSchedule: (id: string, dto: UpdateScheduleDto, tenantId = 1) =>
    request<ScheduledTask>(`/api/schedules/${ id }?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteSchedule: (id: string, tenantId = 1) =>
    request<void>(`/api/schedules/${ id }?tenantId=${ tenantId }`, { method: "DELETE" }),
  setScheduleEnabled: (id: string, isEnabled: boolean, tenantId = 1) =>
    request<ScheduledTask>(`/api/schedules/${ id }/enabled?tenantId=${ tenantId }`, { method: "PATCH", body: JSON.stringify({ isEnabled }) }),
  triggerSchedule: (id: string, tenantId = 1) =>
    request<ScheduledTaskRun>(`/api/schedules/${ id }/trigger?tenantId=${ tenantId }`, { method: "POST" }),
  getScheduleRuns: (id: string, tenantId = 1, limit = 50) =>
    request<ScheduledTaskRun[]>(`/api/schedules/${ id }/runs?tenantId=${ tenantId }&limit=${ limit }`),
  importSchedules: (req: ScheduleImportRequest, tenantId = 1) =>
    request<ScheduleImportResult>(`/api/schedules/import?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(req) }),

  // Platform LLM Config — backward-compat singleton accessor
  getPlatformLlmConfig: () =>
    request<PlatformLlmConfig>("/api/platform/llm-config"),
  upsertPlatformLlmConfig: (dto: UpsertLlmConfigDto) =>
    request<PlatformLlmConfig>("/api/platform/llm-config", { method: "PUT", body: JSON.stringify(dto) }),

  // Platform LLM Config catalog (multiple named configs)
  listPlatformLlmConfigs: () =>
    request<PlatformLlmConfig[]>("/api/platform/llm-configs"),
  createPlatformLlmConfig: (dto: CreatePlatformLlmConfigDto) =>
    request<PlatformLlmConfig>("/api/platform/llm-configs", { method: "POST", body: JSON.stringify(dto) }),
  updatePlatformLlmConfig: (id: number, dto: UpsertLlmConfigDto) =>
    request<PlatformLlmConfig>(`/api/platform/llm-configs/${ id }`, { method: "PUT", body: JSON.stringify(dto) }),
  deletePlatformLlmConfig: (id: number) =>
    request<void>(`/api/platform/llm-configs/${ id }`, { method: "DELETE" }),

  // Tenant LLM Config — default unnamed config (backward compat)
  getTenantLlmConfig: (tenantId = 1) =>
    request<TenantLlmConfig | null>(`/api/admin/llm-config?tenantId=${ tenantId }`),
  upsertTenantLlmConfig: (dto: UpsertLlmConfigDto, tenantId = 1) =>
    request<TenantLlmConfig>(`/api/admin/llm-config?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteTenantLlmConfig: (tenantId = 1) =>
    request<void>(`/api/admin/llm-config?tenantId=${ tenantId }`, { method: "DELETE" }),

  // Tenant LLM Configs — named config list (for agent picker)
  listTenantLlmConfigs: (tenantId = 1) =>
    request<TenantLlmConfig[]>(`/api/admin/llm-configs?tenantId=${ tenantId }`),
  createTenantLlmConfig: (dto: CreateNamedLlmConfigDto, tenantId = 1) =>
    request<TenantLlmConfig>(`/api/admin/llm-configs?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  updateTenantLlmConfigById: (id: number, dto: UpsertLlmConfigDto, tenantId = 1) =>
    request<TenantLlmConfig>(`/api/admin/llm-configs/${ id }?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteTenantLlmConfigById: (id: number, tenantId = 1) =>
    request<void>(`/api/admin/llm-configs/${ id }?tenantId=${ tenantId }`, { method: "DELETE" }),
  listAvailableLlmConfigs: (tenantId = 1) =>
    request<AvailableLlmConfig[]>(`/api/admin/llm-configs/available?tenantId=${ tenantId }`),
  fetchLlmConfigModels: (configId: number, tenantId = 1) =>
    request<string[]>(`/api/admin/llm-configs/${ configId }/models?tenantId=${ tenantId }`),

  // Tenant Groups — tenant-scoped (groups this tenant belongs to)
  listMyGroups: () =>
    request<Pick<TenantGroup, "id" | "name" | "description">[]>("/api/agents/my-groups"),

  // Tenant Groups — platform admin only
  listGroups: () =>
    request<TenantGroup[]>("/api/platform/groups"),
  getGroup: (id: number) =>
    request<TenantGroupDetail>(`/api/platform/groups/${ id }`),
  createGroup: (dto: { name: string; description?: string; }) =>
    request<TenantGroup>("/api/platform/groups", { method: "POST", body: JSON.stringify(dto) }),
  updateGroup: (id: number, dto: { name: string; description?: string; isActive: boolean; }) =>
    request<TenantGroup>(`/api/platform/groups/${ id }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteGroup: (id: number) =>
    request<void>(`/api/platform/groups/${ id }`, { method: "DELETE" }),

  // Group Members
  listGroupMembers: (groupId: number) =>
    request<GroupMember[]>(`/api/platform/groups/${ groupId }/members`),
  addGroupMember: (groupId: number, tenantId: number) =>
    request<GroupMember>(`/api/platform/groups/${ groupId }/members`, { method: "POST", body: JSON.stringify({ tenantId }) }),
  removeGroupMember: (groupId: number, tenantId: number) =>
    request<void>(`/api/platform/groups/${ groupId }/members/${ tenantId }`, { method: "DELETE" }),

  // Group Agent Templates
  listGroupAgents: (groupId: number) =>
    request<GroupAgentTemplate[]>(`/api/platform/groups/${ groupId }/agents`),
  getGroupAgent: (groupId: number, templateId: string) =>
    request<GroupAgentTemplate>(`/api/platform/groups/${ groupId }/agents/${ templateId }`),
  createGroupAgent: (groupId: number, dto: CreateGroupAgentDto) =>
    request<GroupAgentTemplate>(`/api/platform/groups/${ groupId }/agents`, { method: "POST", body: JSON.stringify(dto) }),
  updateGroupAgent: (groupId: number, templateId: string, dto: UpdateGroupAgentDto) =>
    request<GroupAgentTemplate>(`/api/platform/groups/${ groupId }/agents/${ templateId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteGroupAgent: (groupId: number, templateId: string) =>
    request<void>(`/api/platform/groups/${ groupId }/agents/${ templateId }`, { method: "DELETE" }),

  // Group Business Rules
  listGroupRules: (groupId: number) =>
    request<GroupBusinessRule[]>(`/api/platform/groups/${ groupId }/business-rules`),
  createGroupRule: (groupId: number, dto: CreateGroupRuleDto) =>
    request<GroupBusinessRule>(`/api/platform/groups/${ groupId }/business-rules`, { method: "POST", body: JSON.stringify(dto) }),
  updateGroupRule: (groupId: number, ruleId: number, dto: UpdateGroupRuleDto) =>
    request<GroupBusinessRule>(`/api/platform/groups/${ groupId }/business-rules/${ ruleId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteGroupRule: (groupId: number, ruleId: number) =>
    request<void>(`/api/platform/groups/${ groupId }/business-rules/${ ruleId }`, { method: "DELETE" }),

  // Group Prompt Overrides
  listGroupPromptOverrides: (groupId: number) =>
    request<GroupPromptOverride[]>(`/api/platform/groups/${ groupId }/prompt-overrides`),
  createGroupPromptOverride: (groupId: number, dto: CreateGroupPromptOverrideDto) =>
    request<GroupPromptOverride>(`/api/platform/groups/${ groupId }/prompt-overrides`, { method: "POST", body: JSON.stringify(dto) }),
  updateGroupPromptOverride: (groupId: number, overrideId: number, dto: UpdateGroupPromptOverrideDto) =>
    request<GroupPromptOverride>(`/api/platform/groups/${ groupId }/prompt-overrides/${ overrideId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteGroupPromptOverride: (groupId: number, overrideId: number) =>
    request<void>(`/api/platform/groups/${ groupId }/prompt-overrides/${ overrideId }`, { method: "DELETE" }),

  // Group Schedules
  listGroupSchedules: (groupId: number) =>
    request<GroupScheduledTask[]>(`/api/platform/groups/${ groupId }/schedules`),
  getGroupSchedule: (groupId: number, taskId: string) =>
    request<GroupScheduledTask>(`/api/platform/groups/${ groupId }/schedules/${ taskId }`),
  createGroupSchedule: (groupId: number, dto: CreateGroupTaskDto) =>
    request<GroupScheduledTask>(`/api/platform/groups/${ groupId }/schedules`, { method: "POST", body: JSON.stringify(dto) }),
  updateGroupSchedule: (groupId: number, taskId: string, dto: UpdateGroupTaskDto) =>
    request<GroupScheduledTask>(`/api/platform/groups/${ groupId }/schedules/${ taskId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteGroupSchedule: (groupId: number, taskId: string) =>
    request<void>(`/api/platform/groups/${ groupId }/schedules/${ taskId }`, { method: "DELETE" }),
  setGroupScheduleEnabled: (groupId: number, taskId: string, isEnabled: boolean) =>
    request<void>(`/api/platform/groups/${ groupId }/schedules/${ taskId }/enabled`, { method: "PATCH", body: JSON.stringify({ isEnabled }) }),
  importGroupSchedules: (groupId: number, req: GroupScheduleImportRequest) =>
    request<GroupScheduleImportResult>(`/api/platform/groups/${ groupId }/schedules/import`, { method: "POST", body: JSON.stringify(req) }),

  // Group LLM Config — default unnamed config (backward compat)
  getGroupLlmConfig: (groupId: number) =>
    request<GroupLlmConfig | null>(`/api/platform/groups/${ groupId }/llm-config`),
  upsertGroupLlmConfig: (groupId: number, dto: UpsertLlmConfigDto) =>
    request<GroupLlmConfig>(`/api/platform/groups/${ groupId }/llm-config`, { method: "PUT", body: JSON.stringify(dto) }),

  // Group LLM Configs — named config list
  listGroupLlmConfigs: (groupId: number) =>
    request<GroupLlmConfig[]>(`/api/platform/groups/${ groupId }/llm-configs`),
  createGroupLlmConfig: (groupId: number, dto: CreateNamedLlmConfigDto) =>
    request<GroupLlmConfig>(`/api/platform/groups/${ groupId }/llm-configs`, { method: "POST", body: JSON.stringify(dto) }),
  updateGroupLlmConfigById: (groupId: number, cfgId: number, dto: UpsertLlmConfigDto) =>
    request<GroupLlmConfig>(`/api/platform/groups/${ groupId }/llm-configs/${ cfgId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteGroupLlmConfigById: (groupId: number, cfgId: number) =>
    request<void>(`/api/platform/groups/${ groupId }/llm-configs/${ cfgId }`, { method: "DELETE" }),
  /** Add a platform config reference to a group (no credential re-entry). */
  addGroupPlatformRef: (groupId: number, dto: AddGroupPlatformRefDto) =>
    request<GroupLlmConfig>(`/api/platform/groups/${ groupId }/llm-configs/ref`, { method: "POST", body: JSON.stringify(dto) }),

  // Archetypes (Phase 15)
  listArchetypes: () =>
    request<ArchetypeSummary[]>("/api/agents/archetypes"),
  getArchetype: (id: string) =>
    request<AgentArchetype>(`/api/agents/archetypes/${ id }`),

  // Rule Packs (Phase 16)
  getRulePacks: (tenantId = 1) =>
    request<RulePack[]>(`/api/admin/rule-packs?tenantId=${ tenantId }`),
  getRulePack: (id: number, tenantId = 1) =>
    request<RulePack>(`/api/admin/rule-packs/${ id }?tenantId=${ tenantId }`),
  getStarterPacks: () =>
    request<RulePack[]>("/api/admin/rule-packs/starters"),
  createRulePack: (dto: CreateRulePackDto, tenantId = 1) =>
    request<RulePack>(`/api/admin/rule-packs?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  updateRulePack: (id: number, dto: UpdateRulePackDto, tenantId = 1) =>
    request<RulePack>(`/api/admin/rule-packs/${ id }?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteRulePack: (id: number, tenantId = 1) =>
    request<void>(`/api/admin/rule-packs/${ id }?tenantId=${ tenantId }`, { method: "DELETE" }),
  cloneRulePack: (sourceId: number, newName: string, tenantId = 1) =>
    request<RulePack>(`/api/admin/rule-packs/${ sourceId }/clone?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify({ newName }) }),
  addHookRule: (packId: number, dto: CreateHookRuleDto, tenantId = 1) =>
    request<HookRule>(`/api/admin/rule-packs/${ packId }/rules?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  updateHookRule: (packId: number, ruleId: number, dto: UpdateHookRuleDto, tenantId = 1) =>
    request<HookRule>(`/api/admin/rule-packs/${ packId }/rules/${ ruleId }?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteHookRule: (packId: number, ruleId: number, tenantId = 1) =>
    request<void>(`/api/admin/rule-packs/${ packId }/rules/${ ruleId }?tenantId=${ tenantId }`, { method: "DELETE" }),
  reorderHookRules: (packId: number, ruleIds: number[], tenantId = 1) =>
    request<void>(`/api/admin/rule-packs/${ packId }/reorder?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(ruleIds) }),
  analyzeConflicts: (packId: number, tenantId = 1) =>
    request<ConflictAnalysis>(`/api/admin/rule-packs/${ packId }/conflicts?tenantId=${ tenantId }`),
  testRulePack: (packId: number, dto: { sampleQuery: string; sampleResponse: string; }, tenantId = 1) =>
    request<RulePackTestResult>(`/api/admin/rule-packs/${ packId }/test?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  exportRulePack: (packId: number, tenantId = 1) =>
    request<RulePackExport>(`/api/admin/rule-packs/${ packId }/export?tenantId=${ tenantId }`),
  importRulePack: (data: RulePackExport, tenantId = 1) =>
    request<RulePack>(`/api/admin/rule-packs/import?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(data) }),

  // Phase 17: Agent Setup Assistant
  suggestPrompt: (ctx: AgentSetupContext) =>
    request<PromptSuggestion>("/api/agents/suggest-prompt", { method: "POST", body: JSON.stringify(ctx) }),
  suggestRulePacks: (ctx: AgentSetupContext) =>
    request<SuggestedRulePack[]>("/api/agents/suggest-rule-packs", { method: "POST", body: JSON.stringify(ctx) }),
  suggestRegex: (req: RegexSuggestionRequest, tenantId = 1) =>
    request<RegexSuggestion>(`/api/admin/rule-packs/suggest-regex?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(req) }),

  // Phase 17: History
  getPromptHistory: (agentId: string) =>
    request<AgentPromptHistoryEntry[]>(`/api/agents/${ agentId }/prompt-history`),
  restorePromptVersion: (agentId: string, version: number, reason?: string) =>
    request<AgentPromptHistoryEntry>(`/api/agents/${ agentId }/prompt-history/${ version }/restore`, { method: "POST", body: JSON.stringify({ reason }) }),
  getRulePackHistory: (packId: number, tenantId = 1) =>
    request<RulePackHistoryEntry[]>(`/api/admin/rule-packs/${ packId }/history?tenantId=${ tenantId }`),
  restoreRulePackVersion: (packId: number, version: number, tenantId = 1, reason?: string) =>
    request<RulePackHistoryEntry>(`/api/admin/rule-packs/${ packId }/history/${ version }/restore?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify({ reason }) }),

  // Rule Pack Meta (matrix for UI dropdowns)
  getRulePackMeta: () =>
    request<RulePackMeta>("/api/admin/rule-packs/meta"),

  // ── Phase 18: Group Agent Overlays ──────────────────────────────────────────
  listGroupTemplates: () =>
    request<GroupTemplateSummary[]>("/api/agents/group-templates"),
  getGroupTemplate: (templateId: string) =>
    request<GroupAgentTemplate>(`/api/agents/group-templates/${ templateId }`),
  getOverlay: (templateId: string) =>
    request<GroupAgentOverlay>(`/api/agents/group-templates/${ templateId }/overlay`),
  applyOverlay: (templateId: string, dto: ApplyOverlayDto) =>
    request<GroupAgentOverlay>(`/api/agents/group-templates/${ templateId }/overlay`, { method: "POST", body: JSON.stringify(dto) }),
  updateOverlay: (templateId: string, dto: UpdateOverlayDto) =>
    request<GroupAgentOverlay>(`/api/agents/group-templates/${ templateId }/overlay`, { method: "PUT", body: JSON.stringify(dto) }),
  removeOverlay: (templateId: string) =>
    request<void>(`/api/agents/group-templates/${ templateId }/overlay`, { method: "DELETE" }),
  setOverlayEnabled: (templateId: string, isEnabled: boolean) =>
    request<GroupAgentOverlay>(`/api/agents/group-templates/${ templateId }/overlay/enabled`, { method: "PATCH", body: JSON.stringify({ isEnabled }) }),

  // ── MCP Credentials ─────────────────────────────────────────────────────────
  listCredentials: (tenantId?: number) =>
    request<McpCredential[]>(`/api/admin/credentials${ tenantId ? `?tenantId=${ tenantId }` : "" }`),
  createCredential: (dto: CreateCredentialDto) =>
    request<{ id: number; name: string; authScheme: string; }>("/api/admin/credentials", { method: "POST", body: JSON.stringify(dto) }),
  updateCredential: (id: number, dto: UpdateCredentialDto) =>
    request<{ id: number; name: string; authScheme: string; }>(`/api/admin/credentials/${ id }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteCredential: (id: number, tenantId?: number) =>
    request<void>(`/api/admin/credentials/${ id }${ tenantId ? `?tenantId=${ tenantId }` : "" }`, { method: "DELETE" }),
  rotateCredential: (id: number, dto: { newApiKey: string; tenantId?: number; }) =>
    request<{ id: number; name: string; message: string; }>(`/api/admin/credentials/${ id }/rotate`, { method: "POST", body: JSON.stringify(dto) }),

  // ── Platform API Keys ───────────────────────────────────────────────────────
  listApiKeys: (tenantId?: number) =>
    request<PlatformApiKey[]>(`/api/admin/api-keys${ tenantId ? `?tenantId=${ tenantId }` : "" }`),
  createApiKey: (dto: CreateApiKeyDto) =>
    request<ApiKeyCreatedResult>("/api/admin/api-keys", { method: "POST", body: JSON.stringify(dto) }),
  revokeApiKey: (id: number, tenantId?: number) =>
    request<void>(`/api/admin/api-keys/${ id }${ tenantId ? `?tenantId=${ tenantId }` : "" }`, { method: "DELETE" }),
  rotateApiKey: (id: number, tenantId?: number) =>
    request<ApiKeyCreatedResult>(`/api/admin/api-keys/${ id }/rotate`, { method: "POST", body: JSON.stringify({ tenantId: tenantId ?? 1 }) }),

  // ── A2A Config ────────────────────────────────────────────────────────────
  getA2AConfig: () => request<A2AConfig>("/api/admin/a2a-config"),

  // ── Session Trace ─────────────────────────────────────────────────────────
  getSessions: (params: SessionListParams = {}) =>
  {
    const qs = new URLSearchParams();
    if (params.tenantId) qs.set("tenantId", String(params.tenantId));
    if (params.agentId) qs.set("agentId", params.agentId);
    if (params.userId) qs.set("userId", params.userId);
    if (params.status) qs.set("status", params.status);
    if (params.from) qs.set("from", params.from);
    if (params.to) qs.set("to", params.to);
    if (params.q) qs.set("q", params.q);
    if (params.supervisorOnly) qs.set("supervisorOnly", "true");
    if (params.hasErrors) qs.set("hasErrors", "true");
    if (params.page) qs.set("page", String(params.page));
    if (params.pageSize) qs.set("pageSize", String(params.pageSize));
    return request<PagedResult<SessionSummary>>(`/api/sessions?${ qs }`);
  },
  getSession: (id: string) => request<SessionDetail>(`/api/sessions/${ id }`),
  getTurnIterations: (sessionId: string, turnNumber: number) =>
    request<IterationDetail[]>(`/api/sessions/${ sessionId }/turns/${ turnNumber }/iterations`),
  getSessionTree: (id: string) => request<SessionTreeNode[]>(`/api/sessions/${ id }/tree`),
  exportSession: async (id: string): Promise<Blob> =>
  {
    const res = await fetch(`${ BASE }/api/sessions/${ id }/export`, {
      headers: { ...authHeaders() },
    });
    if (!res.ok) throw new Error(`${ res.status }`);
    return res.blob();
  },
  deleteSession: (id: string) => request<void>(`/api/sessions/${ id }`, { method: "DELETE" }),
  purgeSessions: (olderThanDays: number, status?: string) =>
  {
    const qs = new URLSearchParams({ olderThanDays: String(olderThanDays) });
    if (status) qs.set("status", status);
    return request<{ deleted: number; }>(`/api/sessions/purge?${ qs }`, { method: "DELETE" });
  },

  // ── Widget Config ────────────────────────────────────────────────────────────
  listWidgets: (tenantId = 1) =>
    request<WidgetConfigDto[]>(`/api/admin/widgets?tenantId=${ tenantId }`),
  createWidget: (dto: CreateWidgetRequest, tenantId = 1) =>
    request<WidgetConfigDto>(`/api/admin/widgets?tenantId=${ tenantId }`, { method: "POST", body: JSON.stringify(dto) }),
  updateWidget: (id: string, dto: CreateWidgetRequest, tenantId = 1) =>
    request<WidgetConfigDto>(`/api/admin/widgets/${ id }?tenantId=${ tenantId }`, { method: "PUT", body: JSON.stringify(dto) }),
  deleteWidget: (id: string, tenantId = 1) =>
    request<void>(`/api/admin/widgets/${ id }?tenantId=${ tenantId }`, { method: "DELETE" }),
};

// ── A2A Config Type ───────────────────────────────────────────────────────────

export interface A2AConfig
{
  enabled: boolean;
  taskTimeoutSeconds: number;
  baseUrl: string | null;
  maxDelegationDepth: number;
  maxConcurrentTasks: number;
  taskRetentionDays: number;
  rateLimitPerMinute: number;
}

// ── Phase 17 Types ────────────────────────────────────────────────────────────

export interface AvailableLlmConfigForSetup
{
  id: number;
  provider: string;
  model: string;
  label: string;
}

export interface AgentSetupContext
{
  tenantId?: number;
  agentId?: string;
  delegateAgentIds?: string[];
  agentName: string;
  agentDescription: string;
  archetypeId?: string;
  toolNames?: string[];
  additionalContext?: string;
  mode: "create" | "refine";
  currentSystemPrompt?: string;
  currentRulePacksJson?: string;
  availableLlmConfigs?: AvailableLlmConfigForSetup[];
}

export interface PromptSuggestion
{
  systemPrompt: string;
  rationale: string;
}

export interface SuggestedHookRule
{
  hookPoint: string;
  ruleType: string;
  pattern?: string;
  instruction?: string;
  replacement?: string;
  toolName?: string;
  order: number;
  stopOnMatch: boolean;
  llmConfigId?: number;
  modelOverride?: string;
}

export interface SuggestedRulePack
{
  name: string;
  description: string;
  rationale: string;
  operation: "add" | "update" | "delete" | "keep";
  existingPackId?: number;
  rules: SuggestedHookRule[];
}

export interface RegexSuggestionRequest
{
  intentDescription: string;
  sampleMatches: string[];
  sampleNonMatches: string[];
  ruleType?: string;
  hookPoint?: string;
}

export interface RegexSuggestion
{
  pattern: string;
  explanation: string;
  flags?: string;
  warnings: string[];
  previewMatches: string[];
  previewNonMatches: string[];
}

export interface AgentPromptHistoryEntry
{
  version: number;
  systemPrompt: string;
  createdAtUtc: string;
  createdBy: string;
  source: "manual" | "assistant_create" | "assistant_refine" | "restore";
  reason?: string;
}

export interface RulePackHistoryEntry
{
  version: number;
  rulesJson: string;
  createdAtUtc: string;
  createdBy: string;
  source: "manual" | "assistant_create" | "assistant_refine" | "restore";
  reason?: string;
}

export interface RulePackMeta
{
  hookPoints: Record<string, string[]>;
  matrixMarkdown: string;
}

// ── Phase 18 Types ────────────────────────────────────────────────────────────

export interface GroupTemplateSummary
{
  id: string;
  name: string;
  displayName: string;
  description?: string;
  agentType: string;
  groupId: number;
  groupName?: string;
  isEnabled: boolean;
  isActivated: boolean;
  overlayGuid?: string;
}

export interface GroupAgentOverlay
{
  id: number;
  guid: string;
  tenantId: number;
  groupTemplateId: string;
  groupId: number;
  isEnabled: boolean;
  systemPromptAddendum?: string;
  modelId?: string;
  temperature?: number;
  extraToolBindingsJson?: string;
  customVariablesJson?: string;
  llmConfigId?: number;
  maxOutputTokens?: number;
  activatedAt: string;
  updatedAt?: string;
}

export interface ApplyOverlayDto
{
  isEnabled?: boolean;
  systemPromptAddendum?: string;
  modelId?: string;
  temperature?: number;
  extraToolBindingsJson?: string;
  customVariablesJson?: string;
  llmConfigId?: number;
  maxOutputTokens?: number;
}

export type UpdateOverlayDto = ApplyOverlayDto & { isEnabled: boolean; };

// ── Session Trace API ─────────────────────────────────────────────────────

export interface SessionSummary
{
  sessionId: string;
  parentSessionId?: string;
  tenantId: number;
  userId?: string;
  agentId: string;
  agentName: string;
  isSupervisor: boolean;
  status: string;
  createdAt: string;
  lastActivityAt: string;
  totalTurns: number;
  totalIterations: number;
  totalToolCalls: number;
  totalDelegations: number;
  totalInputTokens: number;
  totalOutputTokens: number;
}

export interface PagedResult<T>
{
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
}

export interface TurnSummary
{
  turnNumber: number;
  userMessagePreview: string;
  assistantMessagePreview: string;
  userMessage?: string;
  assistantMessage?: string;
  totalIterations: number;
  totalToolCalls: number;
  continuationWindows: number;
  verificationMode?: string;
  verificationPassed?: boolean;
  executionTimeMs: number;
  modelId?: string;
  provider?: string;
  totalInputTokens: number;
  totalOutputTokens: number;
  createdAt: string;
}

export interface SessionDetail extends SessionSummary
{
  turns: TurnSummary[];
}

export interface ToolCallDetail
{
  sequence: number;
  toolName: string;
  toolInput?: string;
  toolOutput?: string;
  isAgentDelegation: boolean;
  delegatedAgentId?: string;
  delegatedAgentName?: string;
  linkedA2ATaskId?: string;
  childSessionId?: string;
}

export interface IterationDetail
{
  iterationNumber: number;
  continuationWindow: number;
  isCorrection: boolean;
  thinkingText?: string;
  planText?: string;
  modelId?: string;
  provider?: string;
  hadModelSwitch: boolean;
  fromModel?: string;
  toModel?: string;
  modelSwitchReason?: string;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  cacheCreationTokens: number;
  toolCalls: ToolCallDetail[];
}

export interface SessionTreeNode
{
  sessionId: string;
  agentName: string;
  isSupervisor: boolean;
  status: string;
  totalTurns: number;
  totalIterations: number;
  totalToolCalls: number;
  isCurrentSession: boolean;
  children: SessionTreeNode[];
}

export interface SessionListParams
{
  tenantId?: number;
  agentId?: string;
  userId?: string;
  status?: string;
  from?: string;
  to?: string;
  q?: string;
  supervisorOnly?: boolean;
  hasErrors?: boolean;
  page?: number;
  pageSize?: number;
}

// ── Phase 24: Agent Optimization ──────────────────────────────────────────────

export interface OptimizationRunSummary
{
  id: number;
  agentId: string;
  sessionId?: string;
  startedAt: string;
  completedAt?: string;
  status: string;
  triggerSource: string;
  sessionsAnalyzed: number;
  turnsAnalyzed: number;
  suggestionCount: number;
}

export interface SessionAnalysisReport
{
  agentId: string;
  sessionId?: string;
  totalSessions: number;
  totalTurns: number;
  scoredTurns: number;
  avgFaithfulness?: number;
  avgCompleteness?: number;
  avgToolEfficiency?: number;
  avgCoherence?: number;
  verificationFailureRate: number;
  correctionRetryRate: number;
  maxIterationsHitRate: number;
  toolErrorRate: number;
  averageIterationsPerTurn: number;
  frequentToolErrors: string[];
  sampleTurnContent: string[];
}

export interface OptimizationRunDetail extends OptimizationRunSummary
{
  report?: SessionAnalysisReport;
  suggestions: OptimizationSuggestion[];
  errorMessage?: string;
}

export interface OptimizationSuggestion
{
  id: number;
  runId: number;
  agentId: string;
  type: string;
  fieldName: string;
  currentValue?: string;
  suggestedValue: string;
  confidence: number;
  reasoning: string;
  status: string;
  reviewedBy?: string;
  reviewNotes?: string;
  reviewedAt?: string;
  createdAt: string;
}

export interface OptimizationScheduleConfig
{
  scheduleType: string;
  runAtTime?: string;
  runOnDayOfWeek?: number;
  timezone: string;
  isEnabled: boolean;
  nextRunAt?: string;
  lastScheduledRunAt?: string;
}

export interface FewShotExample
{
  id: number;
  agentId: string;
  sourceSessionId?: string;
  sourceTurnNumber?: number;
  userMessage: string;
  assistantMessage: string;
  description?: string;
  sortOrder: number;
  isEnabled: boolean;
  createdAt: string;
  createdBy?: string;
}

// ── API fetch functions ───────────────────────────────────────────────────────

export async function triggerOptimizationRun(
  agentId: string,
  opts: { from?: string; to?: string; sessionId?: string; userContext?: string; } = {}
): Promise<{ runId: number; }>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize`, {
    method: "POST",
    headers: { ...authHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify({ from: opts.from, to: opts.to, sessionId: opts.sessionId, userContext: opts.userContext || undefined })
  });
  if (!r.ok) { const e = await r.json().catch(() => ({ error: r.statusText })); throw e; }
  return r.json();
}

export async function getOptimizationRuns(agentId: string): Promise<OptimizationRunSummary[]>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize/runs`, { headers: authHeaders() });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

export async function getOptimizationRunsBySession(sessionId: string): Promise<OptimizationRunSummary[]>
{
  const r = await fetch(`${ BASE }/api/admin/sessions/${ sessionId }/optimize/runs`, { headers: authHeaders() });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

export async function getOptimizationRunDetail(agentId: string, runId: number): Promise<OptimizationRunDetail>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize/runs/${ runId }`, { headers: authHeaders() });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

export async function getOptimizationSuggestions(
  agentId: string,
  opts?: { status?: string; type?: string; runId?: number; minConfidence?: number; }
): Promise<OptimizationSuggestion[]>
{
  const params = new URLSearchParams();
  if (opts?.status) params.set("status", opts.status);
  if (opts?.type) params.set("type", opts.type);
  if (opts?.runId != null) params.set("runId", String(opts.runId));
  if (opts?.minConfidence != null && opts.minConfidence > 0) params.set("minConfidence", String(opts.minConfidence));
  const qs = params.toString();
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize/suggestions${ qs ? `?${ qs }` : "" }`, { headers: authHeaders() });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

export async function mergePrompt(agentId: string, suggestionIds: number[]): Promise<{ mergedPrompt: string; }>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize/suggestions/merge-prompt`, {
    method: "POST",
    headers: { ...authHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify({ suggestionIds })
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

export async function applyMerged(agentId: string, mergedPrompt: string, suggestionIds: number[]): Promise<void>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize/suggestions/apply-merged`, {
    method: "POST",
    headers: { ...authHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify({ mergedPrompt, suggestionIds })
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
}

export async function reviewSuggestion(
  agentId: string, id: number, action: "approve" | "reject", notes?: string
): Promise<void>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize/suggestions/${ id }/${ action }`, {
    method: "POST",
    headers: { ...authHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify({ notes })
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
}

export async function applySuggestion(
  agentId: string, id: number, applyMode = "append"
): Promise<void>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize/suggestions/${ id }/apply`, {
    method: "POST",
    headers: { ...authHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify({ applyMode })
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
}

export async function getOptimizationSchedule(agentId: string): Promise<OptimizationScheduleConfig>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize/schedule`, { headers: authHeaders() });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

export async function saveOptimizationSchedule(agentId: string, config: OptimizationScheduleConfig): Promise<void>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/optimize/schedule`, {
    method: "PUT",
    headers: { ...authHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify(config)
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
}

export async function getFewShotExamples(agentId: string): Promise<FewShotExample[]>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/examples`, { headers: authHeaders() });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

export async function addFewShotExample(agentId: string, example: Partial<FewShotExample>): Promise<{ id: number; }>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/examples`, {
    method: "POST",
    headers: { ...authHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify(example)
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

export async function deleteFewShotExample(agentId: string, id: number): Promise<void>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/examples/${ id }`, {
    method: "DELETE", headers: authHeaders()
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
}

export async function reorderFewShotExamples(agentId: string, orderedIds: number[]): Promise<void>
{
  const r = await fetch(`${ BASE }/api/admin/agents/${ agentId }/examples/reorder`, {
    method: "PUT",
    headers: { ...authHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify({ orderedIds })
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
}

export async function triggerSessionOptimization(
  sessionId: string
): Promise<{ runId: number; agentId: string; }>
{
  const r = await fetch(`${ BASE }/api/admin/sessions/${ sessionId }/optimize`, {
    method: "POST", headers: authHeaders()
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

export async function markTurnAsExample(
  sessionId: string, turnNumber: number, description?: string
): Promise<{ id: number; }>
{
  const r = await fetch(`${ BASE }/api/admin/sessions/${ sessionId }/turns/${ turnNumber }/examples`, {
    method: "POST",
    headers: { ...authHeaders(), "Content-Type": "application/json" },
    body: JSON.stringify({ description })
  });
  if (!r.ok) throw await r.json().catch(() => ({ error: r.statusText }));
  return r.json();
}

