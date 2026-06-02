import { delay, http, HttpResponse } from "msw";

const BASE = "http://localhost:5000";

// ─── Mock data ────────────────────────────────────────────────────────────────

const AGENTS = [
  {
    id: "agent-weather-01",
    name: "weather-agent",
    displayName: "Weather Assistant",
    description: "Answers weather queries using live tool calls.",
    agentType: "weather-agent",
    systemPrompt: "You are a helpful weather assistant. Always cite your sources.",
    temperature: 0.3,
    maxIterations: 5,
    capabilities: JSON.stringify(["weather", "forecast"]),
    toolBindings: JSON.stringify([
      { name: "OpenWeather", command: "docker", args: ["run", "-i", "--rm", "mcp/openweather"], env: { OWM_API_KEY: "demo" }, endpoint: "", transport: "stdio" },
    ]),
    isEnabled: true,
    status: "active",
    version: 2,
    tenantId: 1,
    createdAt: "2026-02-15T09:00:00Z",
    publishedAt: "2026-02-16T10:00:00Z",
  },
  {
    id: "agent-booking-02",
    name: "booking-agent",
    displayName: "Reservation Agent",
    description: "Handles hotel and restaurant reservations.",
    agentType: "booking-agent",
    systemPrompt: "You help customers make reservations. Always confirm details before booking.",
    temperature: 0.2,
    maxIterations: 8,
    capabilities: JSON.stringify(["reservations", "availability"]),
    toolBindings: JSON.stringify([]),
    isEnabled: true,
    status: "active",
    version: 1,
    tenantId: 1,
    createdAt: "2026-03-01T11:00:00Z",
    publishedAt: "2026-03-01T12:00:00Z",
  },
  {
    id: "agent-analytics-03",
    name: "analytics-agent",
    displayName: "Analytics Assistant",
    description: "Answers business intelligence questions.",
    agentType: "analytics-agent",
    systemPrompt: "You are a business analytics expert.",
    temperature: 0.5,
    maxIterations: 6,
    capabilities: JSON.stringify(["analytics", "reporting", "sql"]),
    toolBindings: JSON.stringify([]),
    isEnabled: false,
    status: "draft",
    version: 1,
    tenantId: 1,
    createdAt: "2026-03-10T14:00:00Z",
  },
];

// ─── Invoke response scenarios ────────────────────────────────────────────────
// Different agents get different response flavours to showcase all UI states.

let weatherTurnCount = 0;

function buildWeatherResponse(query: string)
{
  weatherTurnCount++;
  const session = "sess-wx-sandbox-001";

  if (query.toLowerCase().includes("error") || query.toLowerCase().includes("fail"))
  {
    return {
      success: false,
      errorMessage: "Weather API rate limit exceeded. Please try again in 60 seconds.",
      sessionId: session,
    };
  }

  const toolEvidence = [
    `[Tool: get_current_weather]
Input: {"location":"London","units":"metric"}
Output: {"temperature":14,"feels_like":11,"humidity":72,"condition":"Overcast clouds","wind_speed":5.2}`,

    `[Tool: get_forecast]
Input: {"location":"London","days":3}
Output: [{"date":"2026-03-23","high":16,"low":9,"condition":"Light rain"},{"date":"2026-03-24","high":13,"low":8,"condition":"Heavy rain"},{"date":"2026-03-25","high":15,"low":10,"condition":"Partly cloudy"}]`,
  ].join("\n\n");

  // First turn: verified. Third turn: show rule suggestion.
  const baseResponse = {
    success: true,
    content: `The current weather in London is **14°C** (feels like 11°C) with overcast clouds and 72% humidity. Wind is light at 5.2 m/s.\n\nForecast:\n• Tomorrow: Light rain, 9–16°C\n• Wednesday: Heavy rain, 8–13°C\n• Thursday: Partly cloudy, 10–15°C`,
    agentName: "weather-agent",
    sessionId: session,
    toolsUsed: ["get_current_weather", "get_forecast"],
    executionTime: "1.24s",
    toolEvidence,
    verification: {
      isVerified: true,
      confidence: 0.94,
      mode: "ToolGrounded",
      ungroundedClaims: [],
      wasBlocked: false,
      reasoning: "All claims are directly supported by tool output.",
    },
    followUpQuestions: [] as object[],
  };

  if (weatherTurnCount % 3 === 0)
  {
    baseResponse.followUpQuestions = [
      {
        type: "rule_confirmation",
        text: 'I noticed a pattern: "Always include a 3-day forecast when answering weather questions." Would you like to save this as a business rule?',
        options: ["Yes, save it", "Just this session", "No, ignore"],
        metadata: {
          agentType: "weather-agent",
          ruleCategory: "response_format",
          ruleKey: "weather.always_include_forecast",
          promptInjection: "Always include a 3-day forecast when answering weather questions.",
          sourceSessionId: session,
          confidence: 0.87,
          suggestedAt: new Date().toISOString(),
        },
      },
    ];
  }

  return baseResponse;
}

function buildBookingResponse(query: string)
{
  const session = "sess-bk-sandbox-002";

  if (query.toLowerCase().includes("cancel"))
  {
    return {
      success: true,
      content: "I've cancelled reservation REF-88821 for John Smith on 24 March 2026. A confirmation email has been sent.",
      agentName: "booking-agent",
      sessionId: session,
      toolsUsed: ["cancel_reservation"],
      executionTime: "0.88s",
      toolEvidence: `[Tool: cancel_reservation]\nInput: {"ref":"REF-88821"}\nOutput: {"status":"cancelled","refund":"full","email_sent":true}`,
      verification: {
        isVerified: false,
        confidence: 0.41,
        mode: "LlmVerifier",
        ungroundedClaims: ["A confirmation email has been sent"],
        wasBlocked: false,
        reasoning: "The tool output confirms cancellation but does not confirm email delivery.",
      },
      followUpQuestions: [],
    };
  }

  return {
    success: true,
    content: "I can help with your reservation. Could you please provide:\n1. Your preferred date and time\n2. Number of guests\n3. Any special requirements",
    agentName: "booking-agent",
    sessionId: session,
    toolsUsed: [],
    executionTime: "0.42s",
    toolEvidence: undefined,
    verification: {
      isVerified: true,
      confidence: 0.99,
      mode: "ToolGrounded",
      ungroundedClaims: [],
      wasBlocked: false,
    },
    followUpQuestions: [],
  };
}

function buildAnalyticsResponse(query: string)
{
  const session = "sess-an-sandbox-003";

  if (query.toLowerCase().includes("revenue"))
  {
    return {
      success: true,
      content: "Q1 2026 revenue was £2.4M, up 18% YoY. Top performing segment: Enterprise (£1.1M, +32%).",
      agentName: "analytics-agent",
      sessionId: session,
      toolsUsed: ["run_query"],
      executionTime: "2.11s",
      toolEvidence: `[Tool: run_query]\nInput: {"sql":"SELECT segment, SUM(revenue) FROM sales WHERE quarter='Q1-2026' GROUP BY segment"}\nOutput: [{"segment":"Enterprise","revenue":1100000},{"segment":"SMB","revenue":900000},{"segment":"Consumer","revenue":400000}]`,
      verification: {
        isVerified: false,
        confidence: 0.58,
        mode: "LlmVerifier",
        ungroundedClaims: ["up 18% YoY"],
        wasBlocked: false,
        reasoning: "YoY comparison requires Q1 2025 data which was not queried.",
      },
      followUpQuestions: [],
    };
  }

  if (query.toLowerCase().includes("block") || query.toLowerCase().includes("risk"))
  {
    return {
      success: false,
      errorMessage: "Response blocked — confidence too low (0.21). The query requires cross-referencing multiple data sources that are unavailable.",
      agentName: "analytics-agent",
      sessionId: session,
      verification: {
        isVerified: false,
        confidence: 0.21,
        mode: "Strict",
        ungroundedClaims: ["multiple data sources"],
        wasBlocked: true,
        reasoning: "Strict mode: confidence below 0.5 threshold.",
      },
    };
  }

  return {
    success: true,
    content: "I can help with analytics queries. What metrics are you interested in? (Try asking about revenue, customer count, or churn rate.)",
    agentName: "analytics-agent",
    sessionId: session,
    toolsUsed: [],
    executionTime: "0.31s",
    verification: { isVerified: true, confidence: 0.99, mode: "Off", ungroundedClaims: [], wasBlocked: false },
    followUpQuestions: [],
  };
}

// ─── Pending rules ─────────────────────────────────────────────────────────────

const PENDING_RULES = [
  {
    id: 1,
    agentType: "weather-agent",
    ruleCategory: "response_format",
    ruleKey: "weather.always_include_forecast",
    promptInjection: "Always include a 3-day forecast when answering weather questions.",
    sourceSessionId: "sess-wx-abc123def456",
    confidence: 0.87,
    suggestedAt: "2026-03-22T10:15:00Z",
  },
  {
    id: 2,
    agentType: "*",
    ruleCategory: "tone",
    ruleKey: "general.formal_english",
    promptInjection: "Always respond in formal British English. Avoid contractions and colloquialisms.",
    sourceSessionId: "sess-bk-xyz789uvw012",
    confidence: 0.73,
    suggestedAt: "2026-03-22T09:45:00Z",
  },
  {
    id: 3,
    agentType: "booking-agent",
    ruleCategory: "safety",
    ruleKey: "booking.confirm_before_cancel",
    promptInjection: "Always ask for explicit confirmation before cancelling any reservation. State the reservation reference and guest name in the confirmation prompt.",
    sourceSessionId: "sess-bk-pqr345stu678",
    confidence: 0.91,
    suggestedAt: "2026-03-21T16:30:00Z",
  },
];

let pendingRules = [...PENDING_RULES];

// ─── Group rule templates (shared across handlers) ────────────────────────────

const GROUP_RULE_TEMPLATES = [
  {
    id: 101, groupId: 1, groupName: "Platform Group",
    agentType: "*", ruleCategory: "tone", ruleKey: "formal_tone",
    promptInjection: "Always respond in a formal, professional tone.",
    priority: 50, hookPoint: "OnInit", hookRuleType: "inject_prompt",
    pattern: null, replacement: null, toolName: null,
    orderInPack: 0, stopOnMatch: false, maxEvaluationMs: 100,
  },
  {
    id: 102, groupId: 1, groupName: "Platform Group",
    agentType: "conversational", ruleCategory: "safety", ruleKey: "no_pii",
    promptInjection: "Never include PII (names, emails, phone numbers) in responses.",
    priority: 90, hookPoint: "OnInit", hookRuleType: "inject_prompt",
    pattern: null, replacement: null, toolName: null,
    orderInPack: 0, stopOnMatch: false, maxEvaluationMs: 100,
  },
];

// ─── Business rules mock data ──────────────────────────────────────────────────

let BUSINESS_RULES = [
  { id: 1, guid: "11111111-0001-0001-0001-000000000001", tenantId: 1, agentType: "*", agentId: null, ruleCategory: "tone", ruleKey: "formal_english", promptInjection: "Always respond in formal British English. Avoid contractions.", isActive: true, priority: 10, hookPoint: "OnInit", hookRuleType: "inject_prompt", orderInPack: 0, stopOnMatch: false, maxEvaluationMs: 100, createdAt: "2026-03-01T09:00:00Z" },
  { id: 2, guid: "22222222-0002-0002-0002-000000000002", tenantId: 1, agentType: "rag", agentId: null, ruleCategory: "response_format", ruleKey: "include_sources", promptInjection: "Always cite document sources when answering from retrieved content.", isActive: true, priority: 100, hookPoint: "OnInit", hookRuleType: "inject_prompt", orderInPack: 0, stopOnMatch: false, maxEvaluationMs: 100, createdAt: "2026-03-15T10:00:00Z" },
  { id: 3, guid: "33333333-0003-0003-0003-000000000003", tenantId: 1, agentType: "data-analyst", agentId: null, ruleCategory: "terminology", ruleKey: "revenue_definition", promptInjection: "Revenue includes Sales, Services, Retail, Subscriptions. DEPOSITS are NOT revenue.", isActive: true, priority: 100, hookPoint: "OnInit", hookRuleType: "inject_prompt", orderInPack: 0, stopOnMatch: false, maxEvaluationMs: 100, createdAt: "2026-03-18T14:00:00Z" },
  { id: 4, guid: "44444444-0004-0004-0004-000000000004", tenantId: 1, agentType: "conversational", agentId: null, ruleCategory: "safety", ruleKey: "confirm_before_cancel", promptInjection: "Always ask for explicit confirmation before cancelling any reservation or action on the user's behalf.", isActive: true, priority: 50, hookPoint: "OnInit", hookRuleType: "inject_prompt", orderInPack: 0, stopOnMatch: false, maxEvaluationMs: 100, createdAt: "2026-03-20T11:00:00Z" },
];
let ruleNextId = 5;

let PROMPT_OVERRIDES = [
  { id: 1, tenantId: 1, agentType: "analytics-agent", section: "output-format", customText: "When presenting financial data, always use £ (GBP) as the currency symbol and include comma separators for thousands.", mergeMode: "Append", isActive: true, version: 1, createdAt: "2026-03-10T09:00:00Z" },
];
let overrideNextId = 2;

const GROUP_PROMPT_TEMPLATES = [
  {
    id: 201, groupId: 1, groupName: "Platform Group",
    agentType: "*", section: "guidelines",
    customText: "Always respond in British English. Use 'colour' not 'color', 'organise' not 'organize'.",
    mergeMode: "Append", isActivated: false, activatedOverrideId: null,
  },
  {
    id: 202, groupId: 1, groupName: "Platform Group",
    agentType: "Support", section: "security-constraints",
    customText: "Never share internal ticket IDs or system error messages with end users.",
    mergeMode: "Append", isActivated: true, activatedOverrideId: 301,
  },
];

// ── Phase 18: Group template + overlay state (module-level so all handlers share it) ──
const MOCK_TEMPLATES = [
  {
    id: "tpl-group-001",
    name: "group-support-agent",
    displayName: "Group Support Agent",
    description: "Shared support agent from the platform group.",
    agentType: "support",
    groupId: 1,
    group: { name: "Platform Group" },
    systemPrompt: "You are a helpful support agent. Always be polite.",
    modelId: null as string | null,
    temperature: 0.7,
    maxIterations: 5,
    capabilities: null as string | null,
    toolBindings: null as string | null,
    isEnabled: true,
    status: "Published",
    createdAt: "2026-01-01T00:00:00Z",
    version: 1,
  },
];
const overlays: Record<string, Record<string, unknown>> = {};

// ─── Handlers ─────────────────────────────────────────────────────────────────

export const handlers = [

  // LLM config
  http.get(`${ BASE }/api/config/llm`, async () =>
  {
    await delay(100);
    return HttpResponse.json({
      availableModels: ["claude-sonnet-4-20250514", "claude-opus-4-20250514", "claude-haiku-4-5-20251001"],
      currentProvider: "Anthropic",
      defaultModel: "claude-sonnet-4-20250514",
    });
  }),

  // List agents (own + shared group templates, matching real API behaviour)
  http.get(`${ BASE }/api/agents`, async () =>
  {
    await delay(300);
    const own = AGENTS.map(({ id, name, displayName, agentType, isEnabled, status, createdAt }) =>
      ({ id, name, displayName, agentType, isEnabled, status, createdAt, isShared: false, isActivated: false })
    );
    const shared = MOCK_TEMPLATES
      .filter(t => own.every(o => o.id !== t.id))
      .map(t =>
      {
        const ov = overlays[t.id];
        return {
          id: t.id, name: t.name, displayName: t.displayName, agentType: t.agentType,
          isEnabled: t.isEnabled, status: t.status, createdAt: t.createdAt,
          isShared: true, groupId: t.groupId, groupName: t.group.name,
          isActivated: !!ov && ov.isEnabled !== false,
          overlayGuid: ov ? ov.guid : undefined,
        };
      });
    return HttpResponse.json([...own, ...shared]);
  }),

  // ── Agent Export / Import ─────────────────────────────────────────────────────
  http.get(`${ BASE }/api/agents/:id/export`, async ({ params }) =>
  {
    await delay(200);
    const agent = AGENTS.find((a) => a.id === params.id) ?? AGENTS[0];
    return HttpResponse.json({
      schemaVersion: "1.0",
      exportedAt: new Date().toISOString(),
      sourceTenantId: 1,
      agent: {
        name: agent.name,
        displayName: agent.displayName,
        description: agent.description ?? "",
        agentType: agent.agentType,
        systemPrompt: agent.systemPrompt ?? "You are a helpful AI assistant.",
        temperature: agent.temperature,
        maxIterations: agent.maxIterations,
        capabilities: agent.capabilities ?? null,
        toolBindings: agent.toolBindings ?? null,
        verificationMode: null,
        executionMode: "Full",
        isEnabled: agent.isEnabled,
        status: agent.status,
        delegateAgentNames: [],
      },
      rules: [
        {
          agentType: agent.agentType,
          ruleCategory: "Behaviour",
          ruleKey: "tone",
          ruleValueJson: null,
          promptInjection: "Always respond in a concise and professional tone.",
          isActive: true,
          priority: 100,
          hookPoint: "OnInit",
          hookRuleType: "inject_prompt",
          orderInPack: 0,
          stopOnMatch: false,
          maxEvaluationMs: 100,
        },
      ],
    });
  }),

  http.post(`${ BASE }/api/agents/import`, async ({ request }) =>
  {
    await delay(300);
    const body = await request.json() as { agent?: { name?: string; }; };
    const agentName = body?.agent?.name ?? "Imported Agent";
    const newId = `agent-imported-${ Date.now() }`;
    return HttpResponse.json(
      {
        agentId: newId,
        agentName,
        rulesImported: 1,
        warnings: [],
      },
      { status: 201 }
    );
  }),

  // ── Archetypes ────────────────────────────────────────────────────────────────
  http.get(`${ BASE }/api/agents/archetypes`, async () =>
  {
    await delay(150);
    return HttpResponse.json([
      { id: "general", displayName: "General Assistant", description: "Versatile agent for open-ended tasks.", icon: "bot", category: "General" },
      { id: "rag", displayName: "RAG Knowledge Agent", description: "Retrieval-Augmented Generation — grounds answers in docs.", icon: "database", category: "Knowledge" },
      { id: "code-analyst", displayName: "Code Analyst", description: "Code review, debugging, refactoring, and documentation.", icon: "code", category: "Engineering" },
      { id: "data-analyst", displayName: "Data Analyst", description: "Analyses datasets, SQL queries, and statistical findings.", icon: "chart", category: "Analytics" },
      { id: "researcher", displayName: "Research Agent", description: "Deep-dive research with multi-source synthesis and reports.", icon: "search", category: "Research" },
      { id: "coordinator", displayName: "Multi-Agent Coordinator", description: "Orchestrates sub-tasks across multiple specialised agents.", icon: "network", category: "Orchestration" },
      { id: "conversational", displayName: "Conversational Agent", description: "Multi-turn conversation with memory and guided workflows.", icon: "chat", category: "Communication" },
      { id: "remote-a2a", displayName: "Remote A2A Agent", description: "Proxy agent that delegates to an external A2A endpoint.", icon: "globe", category: "Federation" },
    ]);
  }),

  http.get(`${ BASE }/api/agents/archetypes/:id`, async ({ params }) =>
  {
    const archetypes: Record<string, object> = {
      "general": { id: "general", displayName: "General Assistant", description: "Versatile agent for open-ended tasks.", icon: "bot", category: "General", defaultTemperature: 0.7, defaultMaxIterations: 10, defaultCapabilities: ["general"], suggestedTools: [], defaultHooks: {}, defaultExecutionMode: "Full" },
      "rag": { id: "rag", displayName: "RAG Knowledge Agent", description: "Retrieval-Augmented Generation — grounds answers in docs.", icon: "database", category: "Knowledge", defaultTemperature: 0.3, defaultMaxIterations: 8, defaultCapabilities: ["rag"], suggestedTools: ["knowledge-search"], defaultHooks: {}, defaultVerificationMode: "ToolGrounded", defaultExecutionMode: "Full" },
      "code-analyst": { id: "code-analyst", displayName: "Code Analyst", description: "Code review, debugging, refactoring, and documentation.", icon: "code", category: "Engineering", defaultTemperature: 0.2, defaultMaxIterations: 15, defaultCapabilities: ["code-review"], suggestedTools: ["linter"], defaultHooks: {}, defaultVerificationMode: "LlmVerifier", defaultExecutionMode: "Full" },
      "data-analyst": { id: "data-analyst", displayName: "Data Analyst", description: "Analyses datasets, SQL queries, and statistical findings.", icon: "chart", category: "Analytics", defaultTemperature: 0.3, defaultMaxIterations: 12, defaultCapabilities: ["data-analysis"], suggestedTools: ["sql-query"], defaultHooks: {}, defaultVerificationMode: "Strict", defaultExecutionMode: "Full" },
      "researcher": { id: "researcher", displayName: "Research Agent", description: "Deep-dive research with multi-source synthesis and reports.", icon: "search", category: "Research", defaultTemperature: 0.5, defaultMaxIterations: 20, defaultCapabilities: ["research"], suggestedTools: ["web-search"], defaultHooks: {}, defaultVerificationMode: "LlmVerifier", defaultExecutionMode: "Full" },
      "coordinator": { id: "coordinator", displayName: "Multi-Agent Coordinator", description: "Orchestrates sub-tasks across multiple specialised agents.", icon: "network", category: "Orchestration", defaultTemperature: 0.4, defaultMaxIterations: 25, defaultCapabilities: ["orchestration"], suggestedTools: [], defaultHooks: {}, defaultExecutionMode: "Full" },
      "conversational": { id: "conversational", displayName: "Conversational Agent", description: "Multi-turn conversation with memory and guided workflows.", icon: "chat", category: "Communication", defaultTemperature: 0.8, defaultMaxIterations: 6, defaultCapabilities: ["conversation"], suggestedTools: [], defaultHooks: {}, defaultVerificationMode: "Off", defaultExecutionMode: "ChatOnly" },
      "remote-a2a": { id: "remote-a2a", displayName: "Remote A2A Agent", description: "Proxy agent that delegates to an external A2A endpoint.", icon: "globe", category: "Federation", defaultTemperature: 0, defaultMaxIterations: 1, defaultCapabilities: ["a2a"], suggestedTools: [], defaultHooks: {}, defaultExecutionMode: "Full" },
    };
    const arch = archetypes[params.id as string];
    return arch ? HttpResponse.json(arch) : new HttpResponse(null, { status: 404 });
  }),

  // ── My groups (tenant-scoped membership list) ────────────────────────────────
  http.get(`${ BASE }/api/agents/my-groups`, async () =>
  {
    await delay(200);
    // In sandbox, the mock tenant (id=1) is a member of Platform Group (id=1).
    return HttpResponse.json([
      { id: 1, name: "Platform Group", description: "Default platform group" },
    ]);
  }),

  // ── Group templates + overlays (must precede /:id to avoid param capture) ───
  http.get(`${ BASE }/api/agents/group-templates`, async () =>
  {
    await delay(300);
    return HttpResponse.json(
      MOCK_TEMPLATES.map((t) =>
      {
        const ov = overlays[t.id];
        return {
          id: t.id, name: t.name, displayName: t.displayName, description: t.description,
          agentType: t.agentType, groupId: t.groupId, groupName: t.group.name,
          isEnabled: t.isEnabled,
          isActivated: !!ov && ov.isEnabled !== false,
          overlayGuid: ov ? ov.guid : null,
        };
      })
    );
  }),

  http.get(`${ BASE }/api/agents/group-templates/:templateId`, async ({ params }) =>
  {
    await delay(200);
    const tmpl = MOCK_TEMPLATES.find((t) => t.id === params.templateId);
    if (!tmpl) return new HttpResponse(null, { status: 404 });
    return HttpResponse.json(tmpl);
  }),

  http.get(`${ BASE }/api/agents/group-templates/:templateId/overlay`, async ({ params }) =>
  {
    await delay(200);
    const ov = overlays[params.templateId as string];
    if (!ov) return new HttpResponse(null, { status: 404 });
    return HttpResponse.json(ov);
  }),

  http.post(`${ BASE }/api/agents/group-templates/:templateId/overlay`, async ({ request, params }) =>
  {
    await delay(400);
    const body = await request.json() as Record<string, unknown>;
    const id = params.templateId as string;
    overlays[id] = {
      guid: `ov-${ Date.now() }`,
      tenantId: 1, groupTemplateId: id, groupId: 1,
      isEnabled: body.isEnabled ?? true,
      systemPromptAddendum: body.systemPromptAddendum ?? null,
      modelId: body.modelId ?? null,
      temperature: body.temperature ?? null,
      maxOutputTokens: body.maxOutputTokens ?? null,
      activatedAt: new Date().toISOString(),
      updatedAt: null,
      ...body,
    };
    return HttpResponse.json(overlays[id]);
  }),

  http.put(`${ BASE }/api/agents/group-templates/:templateId/overlay`, async ({ request, params }) =>
  {
    await delay(400);
    const body = await request.json() as Record<string, unknown>;
    const id = params.templateId as string;
    overlays[id] = { ...(overlays[id] ?? {}), ...body, updatedAt: new Date().toISOString() };
    return HttpResponse.json(overlays[id]);
  }),

  http.delete(`${ BASE }/api/agents/group-templates/:templateId/overlay`, async ({ params }) =>
  {
    await delay(300);
    delete overlays[params.templateId as string];
    return new HttpResponse(null, { status: 204 });
  }),

  http.patch(`${ BASE }/api/agents/group-templates/:templateId/overlay/enabled`, async ({ request, params }) =>
  {
    await delay(300);
    const body = await request.json() as { isEnabled: boolean; };
    const id = params.templateId as string;
    if (overlays[id]) overlays[id].isEnabled = body.isEnabled;
    return HttpResponse.json(overlays[id]);
  }),

  // ── Platform group list + detail (must be before parameterized sub-routes) ───
  http.get(`${ BASE }/api/platform/groups`, async () =>
  {
    await delay(200);
    return HttpResponse.json([
      { id: 1, name: "Platform Group", description: "Default platform group", isActive: true, createdAt: "2026-01-01T00:00:00Z" },
    ]);
  }),

  http.get(`${ BASE }/api/platform/groups/:id`, async ({ params }) =>
  {
    await delay(200);
    if (String(params.id) !== "1") return new HttpResponse(null, { status: 404 });
    return HttpResponse.json({
      id: 1, name: "Platform Group", description: "Default platform group",
      isActive: true, createdAt: "2026-01-01T00:00:00Z",
      members: [{ id: 1, groupId: 1, tenantId: 1, joinedAt: "2026-01-01T00:00:00Z" }],
      llmConfigs: [],
    });
  }),

  // ── Platform group agent templates CRUD (/api/platform/groups/:groupId/agents) ──
  // Must be before /api/agents/:id to avoid param capture.
  http.get(`${ BASE }/api/platform/groups/:groupId/agents`, async ({ params }) =>
  {
    await delay(300);
    const gid = Number(params.groupId);
    return HttpResponse.json(MOCK_TEMPLATES.filter(t => t.groupId === gid));
  }),

  http.get(`${ BASE }/api/platform/groups/:groupId/agents/:id`, async ({ params }) =>
  {
    await delay(200);
    const tmpl = MOCK_TEMPLATES.find(t => t.id === params.id && t.groupId === Number(params.groupId));
    if (!tmpl) return new HttpResponse(null, { status: 404 });
    return HttpResponse.json(tmpl);
  }),

  http.post(`${ BASE }/api/platform/groups/:groupId/agents`, async ({ request, params }) =>
  {
    await delay(400);
    const body = await request.json() as Record<string, unknown>;
    const gid = Number(params.groupId);
    const newTpl = {
      ...body,
      id: `tpl-${ gid }-${ Date.now() }`,
      groupId: gid,
      group: { name: "Platform Group" },
      createdAt: new Date().toISOString(),
      version: 1,
    };
    (MOCK_TEMPLATES as typeof newTpl[]).push(newTpl as never);
    return HttpResponse.json(newTpl, { status: 201 });
  }),

  http.put(`${ BASE }/api/platform/groups/:groupId/agents/:id`, async ({ request, params }) =>
  {
    await delay(400);
    const body = await request.json() as Record<string, unknown>;
    const idx = MOCK_TEMPLATES.findIndex(t => t.id === params.id && t.groupId === Number(params.groupId));
    if (idx === -1) return new HttpResponse(null, { status: 404 });
    MOCK_TEMPLATES[idx] = { ...MOCK_TEMPLATES[idx], ...body, updatedAt: new Date().toISOString() } as never;
    return HttpResponse.json(MOCK_TEMPLATES[idx]);
  }),

  http.delete(`${ BASE }/api/platform/groups/:groupId/agents/:id`, async ({ params }) =>
  {
    await delay(300);
    const idx = MOCK_TEMPLATES.findIndex(t => t.id === params.id && t.groupId === Number(params.groupId));
    if (idx !== -1)
    {
      MOCK_TEMPLATES.splice(idx, 1);
      delete overlays[params.id as string];
    }
    return new HttpResponse(null, { status: 204 });
  }),

  // Get agent by id
  http.get(`${ BASE }/api/agents/:id`, async ({ params }) =>
  {
    await delay(200);
    const agent = AGENTS.find(a => a.id === params.id);
    if (!agent) return new HttpResponse(null, { status: 404 });
    return HttpResponse.json(agent);
  }),

  // Create agent
  http.post(`${ BASE }/api/agents`, async ({ request }) =>
  {
    await delay(400);
    const body = await request.json() as Record<string, unknown>;
    const newAgent = { ...body, id: `agent-${ Date.now() }`, version: 1, createdAt: new Date().toISOString() };
    return HttpResponse.json(newAgent, { status: 201 });
  }),

  // Update agent
  http.put(`${ BASE }/api/agents/:id`, async ({ request, params }) =>
  {
    await delay(400);
    const body = await request.json() as Record<string, unknown>;
    return HttpResponse.json({ ...body, id: params.id });
  }),

  // Delete agent
  http.delete(`${ BASE }/api/agents/:id`, async () =>
  {
    await delay(300);
    return new HttpResponse(null, { status: 204 });
  }),

  // Invoke agent (non-streaming)
  http.post(`${ BASE }/api/agents/:id/invoke`, async ({ request, params }) =>
  {
    await delay(800 + Math.random() * 800); // simulate LLM latency
    const body = await request.json() as { query: string; };
    const { query } = body;
    const agentId = params.id as string;

    let response: object;
    if (agentId.includes("weather")) response = buildWeatherResponse(query);
    else if (agentId.includes("booking")) response = buildBookingResponse(query);
    else response = buildAnalyticsResponse(query);

    return HttpResponse.json(response);
  }),

  // Invoke agent — SSE streaming
  http.post(`${ BASE }/api/agents/:id/invoke/stream`, async ({ request, params }) =>
  {
    const body = await request.json() as { query: string; };
    void body;
    const agentId = params.id as string;
    const sessionId = agentId.includes("weather") ? "sess-wx-sandbox-001"
      : agentId.includes("booking") ? "sess-bk-sandbox-002"
        : "sess-an-sandbox-003";

    // Build scenario-specific chunks
    const isWeather = agentId.includes("weather");
    const isBooking = agentId.includes("booking");
    const isAnalytics = !isWeather && !isBooking;

    const thinkingText1 = isWeather ? "The user is asking about weather. I should call the weather API."
      : isBooking ? "I'll help with the reservation. Let me clarify what's needed."
        : "This is an analytics query. I need to run a SQL query to retrieve the data.";
    // Simulate streaming: split thinking text into word-level text_delta events before the thinking event
    const words1 = thinkingText1.split(" ");
    const chunks: object[] = [
      { type: "iteration_start", iteration: 1 },
      ...words1.map((w, i) => ({ type: "text_delta", iteration: 1, content: (i > 0 ? " " : "") + w })),
      { type: "thinking", iteration: 1, content: thinkingText1 },
    ];

    if (isWeather)
    {
      chunks.push(
        { type: "tool_call", iteration: 1, toolName: "get_current_weather", toolInput: `{"location":"London","units":"metric"}` },
        { type: "tool_result", iteration: 1, toolName: "get_current_weather", toolOutput: `{"temperature":14,"feels_like":11,"humidity":72,"condition":"Overcast clouds","wind_speed":5.2}` },
        { type: "iteration_start", iteration: 2 },
        { type: "thinking", iteration: 2, content: "Got current conditions. Now I'll fetch the 3-day forecast." },
        { type: "tool_call", iteration: 2, toolName: "get_forecast", toolInput: `{"location":"London","days":3}` },
        { type: "tool_result", iteration: 2, toolName: "get_forecast", toolOutput: `[{"date":"2026-03-23","high":16,"low":9,"condition":"Light rain"},{"date":"2026-03-24","high":13,"low":8,"condition":"Heavy rain"},{"date":"2026-03-25","high":15,"low":10,"condition":"Partly cloudy"}]` },
      );
    } else if (isAnalytics)
    {
      chunks.push(
        { type: "tool_call", iteration: 1, toolName: "run_query", toolInput: `{"sql":"SELECT segment, SUM(revenue) FROM sales WHERE quarter='Q1-2026' GROUP BY segment"}` },
        { type: "tool_result", iteration: 1, toolName: "run_query", toolOutput: `[{"segment":"Enterprise","revenue":1100000},{"segment":"SMB","revenue":900000},{"segment":"Consumer","revenue":400000}]` },
      );
    }

    const finalChunk = isWeather
      ? { type: "final_response", sessionId, content: `The current weather in London is **14°C** (feels like 11°C) with overcast clouds and 72% humidity.\n\nForecast:\n• Tomorrow: Light rain, 9–16°C\n• Wednesday: Heavy rain, 8–13°C\n• Thursday: Partly cloudy, 10–15°C` }
      : isBooking
        ? { type: "final_response", sessionId, content: "I can help with your reservation. Could you please provide:\n1. Your preferred date and time\n2. Number of guests\n3. Any special requirements" }
        : { type: "final_response", sessionId, content: "Q1 2026 revenue was £2.4M, up 18% YoY. Top performing segment: Enterprise (£1.1M, +32%)." };

    chunks.push(finalChunk);

    if (isWeather)
    {
      chunks.push({ type: "verification", verification: { isVerified: true, confidence: 0.94, mode: "ToolGrounded", ungroundedClaims: [], wasBlocked: false, reasoning: "All claims are directly supported by tool output." } });
    } else if (isAnalytics)
    {
      chunks.push({ type: "verification", verification: { isVerified: false, confidence: 0.58, mode: "LlmVerifier", ungroundedClaims: ["up 18% YoY"], wasBlocked: false, reasoning: "YoY comparison requires Q1 2025 data which was not queried." } });
    }

    chunks.push({ type: "done", executionTime: `${ (0.8 + Math.random()).toFixed(1) }s` });

    const encoder = new TextEncoder();
    const stream = new ReadableStream({
      async start(controller)
      {
        for (const chunk of chunks)
        {
          controller.enqueue(encoder.encode(`data: ${ JSON.stringify(chunk) }\n\n`));
          await delay(350 + Math.random() * 200);
        }
        controller.close();
      },
    });

    return new Response(stream, {
      headers: { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" },
    });
  }),

  // List pending learned rules
  http.get(`${ BASE }/api/learned-rules`, async () =>
  {
    await delay(250);
    return HttpResponse.json(pendingRules);
  }),

  // Approve rule
  http.post(`${ BASE }/api/learned-rules/:id/approve`, async ({ params }) =>
  {
    await delay(300);
    const id = Number(params.id);
    pendingRules = pendingRules.filter(r => r.id !== id);
    return new HttpResponse(null, { status: 204 });
  }),

  // Reject rule
  http.post(`${ BASE }/api/learned-rules/:id/reject`, async ({ params }) =>
  {
    await delay(300);
    const id = Number(params.id);
    pendingRules = pendingRules.filter(r => r.id !== id);
    return new HttpResponse(null, { status: 204 });
  }),

  // ── Business rules ────────────────────────────────────────────────────────

  http.get(`${ BASE }/api/admin/business-rules`, async ({ request }) =>
  {
    await delay(200);
    const url = new URL(request.url);
    const agentType = url.searchParams.get("agentType") ?? "*";
    const agentId = url.searchParams.get("agentId") ?? null;
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    let filtered: any[] = agentType === "*"
      ? BUSINESS_RULES
      : BUSINESS_RULES.filter(r => r.agentType === agentType || r.agentType === "*");
    // When agentId: include global (no agentId) + that agent's scoped rules.
    // When not provided: only global rules (backward-compat).
    filtered = agentId
      ? filtered.filter(r => !r.agentId || r.agentId === agentId)
      : filtered.filter(r => !r.agentId);
    return HttpResponse.json(filtered);
  }),

  http.post(`${ BASE }/api/admin/business-rules`, async ({ request }) =>
  {
    await delay(300);
    const body = await request.json() as Record<string, unknown>;
    const entity = { ...body, id: ruleNextId++, guid: crypto.randomUUID(), tenantId: 1, isActive: true, createdAt: new Date().toISOString() };
    BUSINESS_RULES.push(entity as typeof BUSINESS_RULES[0]);
    return HttpResponse.json(entity, { status: 201 });
  }),

  http.put(`${ BASE }/api/admin/business-rules/:id`, async ({ request, params }) =>
  {
    await delay(300);
    const body = await request.json() as Record<string, unknown>;
    const id = Number(params.id);
    BUSINESS_RULES = BUSINESS_RULES.map(r => r.id === id ? { ...r, ...body } : r);
    return HttpResponse.json(BUSINESS_RULES.find(r => r.id === id));
  }),

  http.delete(`${ BASE }/api/admin/business-rules/:id`, async ({ params }) =>
  {
    await delay(250);
    BUSINESS_RULES = BUSINESS_RULES.filter(r => r.id !== Number(params.id));
    return new HttpResponse(null, { status: 204 });
  }),

  // ── Group rule templates (opt-in templates shared by tenant groups) ───────

  http.get(`${ BASE }/api/admin/group-rule-templates`, async () =>
  {
    await delay(300);
    return HttpResponse.json(
      GROUP_RULE_TEMPLATES.map(t =>
      {
        const activated = BUSINESS_RULES.find(r => (r as Record<string, unknown>).sourceGroupRuleId === t.id && r.isActive);
        return { ...t, isActivated: !!activated, activatedRuleId: activated?.id ?? null };
      })
    );
  }),

  http.post(`${ BASE }/api/admin/group-rule-templates/:groupRuleId/activate`, async ({ params }) =>
  {
    await delay(300);
    const templateId = Number(params.groupRuleId);
    const tpl = GROUP_RULE_TEMPLATES.find(t => t.id === templateId);
    if (!tpl) return new HttpResponse(null, { status: 404 });
    // Check not already activated
    const existing = BUSINESS_RULES.find(r => (r as Record<string, unknown>).sourceGroupRuleId === templateId && r.isActive);
    if (existing) return HttpResponse.json(existing);
    const entity = {
      id: ruleNextId++,
      guid: crypto.randomUUID(),
      tenantId: 1,
      agentType: tpl.agentType,
      ruleCategory: tpl.ruleCategory,
      ruleKey: tpl.ruleKey,
      promptInjection: tpl.promptInjection,
      priority: tpl.priority,
      isActive: true,
      hookPoint: tpl.hookPoint,
      hookRuleType: tpl.hookRuleType,
      pattern: tpl.pattern,
      replacement: tpl.replacement,
      toolName: tpl.toolName,
      orderInPack: tpl.orderInPack,
      stopOnMatch: tpl.stopOnMatch,
      maxEvaluationMs: tpl.maxEvaluationMs,
      sourceGroupRuleId: templateId,
      agentId: null,
      createdAt: new Date().toISOString(),
    };
    BUSINESS_RULES.push(entity as typeof BUSINESS_RULES[0]);
    return HttpResponse.json(entity, { status: 201 });
  }),

  http.delete(`${ BASE }/api/admin/group-rule-templates/:groupRuleId/activate`, async ({ params }) =>
  {
    await delay(300);
    const templateId = Number(params.groupRuleId);
    BUSINESS_RULES = BUSINESS_RULES.filter(r => (r as Record<string, unknown>).sourceGroupRuleId !== templateId);
    return new HttpResponse(null, { status: 204 });
  }),

  // ── Prompt overrides ──────────────────────────────────────────────────────

  http.get(`${ BASE }/api/admin/prompt-overrides`, async ({ request }) =>
  {
    await delay(200);
    const url = new URL(request.url);
    const agentType = url.searchParams.get("agentType") ?? "*";
    const filtered = agentType === "*"
      ? PROMPT_OVERRIDES
      : PROMPT_OVERRIDES.filter(o => o.agentType === agentType || o.agentType === "*");
    return HttpResponse.json(filtered);
  }),

  http.post(`${ BASE }/api/admin/prompt-overrides`, async ({ request }) =>
  {
    await delay(300);
    const body = await request.json() as Record<string, unknown>;
    const entity = { ...body, id: overrideNextId++, tenantId: 1, isActive: true, version: 1, createdAt: new Date().toISOString() };
    PROMPT_OVERRIDES.push(entity as typeof PROMPT_OVERRIDES[0]);
    return HttpResponse.json(entity, { status: 201 });
  }),

  http.put(`${ BASE }/api/admin/prompt-overrides/:id`, async ({ request, params }) =>
  {
    await delay(300);
    const body = await request.json() as Record<string, unknown>;
    const id = Number(params.id);
    PROMPT_OVERRIDES = PROMPT_OVERRIDES.map(o => o.id === id ? { ...o, ...body } : o);
    return HttpResponse.json(PROMPT_OVERRIDES.find(o => o.id === id));
  }),

  http.delete(`${ BASE }/api/admin/prompt-overrides/:id`, async ({ params }) =>
  {
    await delay(250);
    PROMPT_OVERRIDES = PROMPT_OVERRIDES.filter(o => o.id !== Number(params.id));
    return new HttpResponse(null, { status: 204 });
  }),

  // ── Group prompt templates (opt-in templates shared by tenant groups) ─────

  http.get(`${ BASE }/api/admin/group-prompt-templates`, async () =>
  {
    await delay(300);
    return HttpResponse.json(GROUP_PROMPT_TEMPLATES);
  }),

  http.post(`${ BASE }/api/admin/group-prompt-templates/:groupOverrideId/activate`, async ({ params }) =>
  {
    await delay(300);
    const id = Number(params.groupOverrideId);
    const tpl = GROUP_PROMPT_TEMPLATES.find(t => t.id === id);
    return HttpResponse.json({
      id: 300 + id, tenantId: 1,
      agentType: tpl?.agentType ?? "*",
      section: tpl?.section ?? "guidelines",
      customText: tpl?.customText ?? "",
      mergeMode: tpl?.mergeMode ?? "Append",
      isActive: true, version: 1, createdAt: new Date().toISOString(),
    });
  }),

  http.delete(`${ BASE }/api/admin/group-prompt-templates/:groupOverrideId/activate`, async () =>
  {
    await delay(300);
    return new HttpResponse(null, { status: 204 });
  }),

  // ── Dashboard ─────────────────────────────────────────────────────────────

  http.get(`${ BASE }/api/admin/dashboard`, async () =>
  {
    await delay(300);
    return HttpResponse.json({
      agentCount: AGENTS.length,
      activeRuleCount: BUSINESS_RULES.filter(r => r.isActive).length,
      pendingRuleCount: pendingRules.length,
      sessionCount: 42,
      asOf: new Date().toISOString(),
    });
  }),

  // ── Phase 17 — Agent Setup Assistant ──────────────────────────────────────

  http.post(`${ BASE }/api/agents/suggest-prompt`, async () =>
  {
    await delay(900);
    return HttpResponse.json({
      systemPrompt:
        "You are a helpful assistant specialising in weather forecasting. " +
        "When answering, always cite the data source and timestamp. " +
        "If the requested location is ambiguous, ask the user to clarify before calling any tools. " +
        "Prefer concise bullet-point summaries for multi-day forecasts.",
      rationale:
        "The prompt focuses on data provenance (users need to trust weather data), " +
        "controlled clarification flow, and output format guidance tailored to the weather archetype.",
    });
  }),

  http.post(`${ BASE }/api/agents/suggest-rule-packs`, async () =>
  {
    await delay(1100);
    return HttpResponse.json([
      {
        name: "Rate-limit guardrail",
        description: "Prevents the agent from hammering APIs with rapid sequential calls.",
        rationale: "Tool-heavy agents can exhaust third-party API quotas in a single session.",
        operation: "add",
        existingPackId: null,
        rules: [
          { hookPoint: "OnToolCall", ruleType: "rate_limit", value: "5/60s", description: "Max 5 tool calls per 60 seconds" },
          { hookPoint: "OnToolError", ruleType: "inject_prompt", value: "Retry after 30 s if you receive a 429 response.", description: null, llmConfigId: null, modelOverride: null },
        ],
      },
      {
        name: "Haiku cost model (fast queries)",
        description: "Uses a cheaper, faster model for simple weather look-ups.",
        rationale: "Single-city queries don't need a powerful model; switch to haiku to reduce cost.",
        operation: "add",
        existingPackId: null,
        rules: [
          { hookPoint: "OnInit", ruleType: "model_switch", value: null, description: "Use haiku for simple queries", llmConfigId: 2, modelOverride: null },
        ],
      },
    ]);
  }),

  http.get(`${ BASE }/api/agents/:agentId/prompt-history`, async () =>
  {
    await delay(300);
    return HttpResponse.json([
      { id: 1, agentId: "agent-weather-01", version: 1, systemPrompt: "Initial prompt v1.", createdAtUtc: "2026-02-15T09:00:00Z", createdBy: "admin", source: "manual", reason: null },
      { id: 2, agentId: "agent-weather-01", version: 2, systemPrompt: "You are a helpful weather assistant. Always cite your sources.", createdAtUtc: "2026-02-16T10:00:00Z", createdBy: "admin", source: "ai", reason: "Phase 17 suggestion" },
    ]);
  }),

  http.post(`${ BASE }/api/agents/:agentId/prompt-history/:version/restore`, async ({ params }) =>
  {
    await delay(400);
    return HttpResponse.json({
      id: 3,
      agentId: params.agentId,
      version: 3,
      systemPrompt: "Initial prompt v1.",
      createdAtUtc: new Date().toISOString(),
      createdBy: "admin",
      source: "restore",
      reason: "Restored from v1",
    });
  }),

  http.post(`${ BASE }/api/admin/rule-packs/suggest-regex`, async () =>
  {
    await delay(800);
    return HttpResponse.json({
      pattern: "^[a-zA-Z0-9._%+\\-]+@(example\\.com|acme\\.org)$",
      explanation: "Matches any valid email address in the example.com or acme.org domains. The pattern anchors at start/end to prevent partial matches.",
      flags: "i",
      warnings: [],
      previewMatches: ["user@example.com", "john.doe@acme.org"],
      previewNonMatches: ["user@gmail.com", "notanemail"],
    });
  }),

  http.get(`${ BASE }/api/admin/rule-packs/:packId/history`, async () =>
  {
    await delay(300);
    return HttpResponse.json([
      { id: 1, packId: 1, version: 1, rulesJson: "[]", createdAtUtc: "2026-03-01T10:00:00Z", createdBy: "admin", source: "manual", reason: null },
      { id: 2, packId: 1, version: 2, rulesJson: "[]", createdAtUtc: "2026-03-10T12:00:00Z", createdBy: "admin", source: "ai", reason: "Phase 17 suggestion" },
    ]);
  }),

  http.post(`${ BASE }/api/admin/rule-packs/:packId/history/:version/restore`, async ({ params }) =>
  {
    await delay(400);
    return HttpResponse.json({
      id: 3, packId: Number(params.packId), version: 3, rulesJson: "[]",
      createdAtUtc: new Date().toISOString(), createdBy: "admin", source: "restore", reason: "Restored",
    });
  }),

  http.get(`${ BASE }/api/admin/rule-packs/meta`, async () =>
  {
    await delay(200);
    return HttpResponse.json({
      hookPoints: ["OnInit", "OnToolCall", "OnToolResult", "OnToolError", "OnComplete", "OnError", "OnTokenLimit"],
      ruleTypes: ["inject_prompt", "rate_limit", "block_tool", "model_switch", "require_approval", "redact_output", "allow_tool", "log_event", "custom_action"],
      matrixMarkdown: "| Hook \\ Rule | inject_prompt | rate_limit | model_switch |\n|---|---|---|---|\n| OnInit | ✓ | | ✓ |\n| OnToolCall | ✓ | ✓ | |\n| OnComplete | ✓ | | |",
    });
  }),

  // ── Scheduler Feedback ─────────────────────────────────────────────────────

  http.get(`${ BASE }/api/scheduler-feedback/context`, async ({ request }) =>
  {
    const token = new URL(request.url).searchParams.get("token");
    if (!token || token === "invalid")
    {
      await delay(200);
      return HttpResponse.json({ error: "Invalid or expired token." }, { status: 400 });
    }
    await delay(300);
    return HttpResponse.json({
      taskName: "Daily GM Briefing",
      agentDisplayName: "Briefing Agent",
      runId: "run-mock-001",
      taskId: "task-mock-001",
      sessionId: "session-mock-001",
      taskType: "individual",
      runCompletedAt: new Date().toISOString(),
      runOutcome: "success",
      runSummary: "Daily briefing generated successfully.",
    });
  }),

  http.post(`${ BASE }/api/scheduler-feedback/submit`, async () =>
  {
    await delay(400);
    return HttpResponse.json({ message: "Thank you — your feedback has been submitted." });
  }),

  http.get(`${ BASE }/api/scheduler-feedback`, async () =>
  {
    await delay(300);
    return HttpResponse.json([
      {
        id: "fb-001",
        tenantId: 1,
        runId: "run-mock-001",
        scheduledTaskId: "task-mock-001",
        taskType: "individual",
        taskName: "Daily GM Briefing",
        agentDisplayName: "Briefing Agent",
        thumbsRating: -1,
        starRating: 2,
        category: "Accuracy",
        correctionText: "The figures for region West were incorrect.",
        submitterName: "Jane Smith",
        submitterEmail: "jane@example.com",
        status: "pending",
        submittedAt: new Date(Date.now() - 3600000).toISOString(),
      },
    ]);
  }),

  http.put(`${ BASE }/api/scheduler-feedback/:id/approve`, async () =>
  {
    await delay(300);
    return HttpResponse.json({ message: "Feedback approved." });
  }),

  http.put(`${ BASE }/api/scheduler-feedback/:id/reject`, async () =>
  {
    await delay(300);
    return HttpResponse.json({ message: "Feedback rejected." });
  }),

  http.get(`${ BASE }/api/scheduler-feedback/generate-link`, async ({ request }) =>
  {
    const params = new URL(request.url).searchParams;
    const token = "mock-token-" + params.get("runId");
    await delay(200);
    return HttpResponse.json({ url: `http://localhost:5173/scheduler-feedback?token=${ token }` });
  }),

  http.get(`${ BASE }/api/schedules/feedback-settings`, async () =>
  {
    await delay(200);
    return HttpResponse.json({ enableFeedbackLinks: true, feedbackLinkBaseUrl: "http://localhost:5173", expiryDays: 30 });
  }),

  http.put(`${ BASE }/api/schedules/feedback-settings`, async () =>
  {
    await delay(200);
    return new HttpResponse(null, { status: 204 });
  }),

];
