import { useEffect, useState } from "react";
import { useNavigate, useParams, useLocation } from "react-router";
import { toast } from "sonner";
import {
  Bot, ChevronDown, ChevronRight, Plus, Save, Server, Settings2, Sliders, Cpu, X, Download,
} from "lucide-react";
import {
  api,
  type AgentDefinition,
  type AgentDefaults,
  type AgentSummary,
  type AvailableLlmConfig,
  type CreateGroupAgentDto,
  type GroupAgentTemplate,
  type LlmConfig,
  type McpToolBinding,
} from "@/api";
import { ArchetypeSelector } from "@/components/ArchetypeSelector";
import { HookEditor } from "@/components/HookEditor";
import { A2AConfigPanel } from "@/components/A2AConfigPanel";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Slider } from "@/components/ui/slider";
import { Separator } from "@/components/ui/separator";
import { Badge } from "@/components/ui/badge";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import {
  Card, CardContent, CardDescription, CardHeader, CardTitle,
} from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  Collapsible, CollapsibleContent, CollapsibleTrigger,
} from "@/components/ui/collapsible";
import {
  Dialog, DialogContent, DialogDescription, DialogFooter,
  DialogHeader, DialogTitle,
} from "@/components/ui/dialog";

// ─────────────────────────────────────────────────────────────────────────────
// Constants
// ─────────────────────────────────────────────────────────────────────────────

const EMPTY_BINDING: McpToolBinding = {
  name: "", command: "docker", args: ["run", "-i", "--rm"], env: {},
  endpoint: "", transport: "stdio", passSsoToken: false, passTenantHeaders: false,
};

const AGENT_TYPES = [
  "GeneralAssistant", "AnalyticsAgent", "ReservationAgent",
  "ContentAgent", "SupportAgent", "general",
];

const VERIFICATION_MODES = ["Off", "ToolGrounded", "LlmVerifier", "Strict", "Auto"] as const;

const DEFAULT_FORM: CreateGroupAgentDto = {
  name: "", displayName: "", description: "", agentType: "",
  systemPrompt: "", modelId: undefined, temperature: 0.7, maxIterations: 10,
  isEnabled: true, status: "Published", executionMode: "Full",
};

function parseJson<T>(json: string | undefined, fallback: T): T {
  if (!json) return fallback;
  try { return JSON.parse(json); }
  catch { return fallback; }
}

// ─────────────────────────────────────────────────────────────────────────────
// MCP Binding Editor (inline — mirrors AgentBuilder version)
// ─────────────────────────────────────────────────────────────────────────────

function McpBindingEditor({
  binding, index, onChange, onRemove, canRemove,
}: {
  binding: McpToolBinding; index: number;
  onChange: (b: McpToolBinding) => void; onRemove: () => void; canRemove: boolean;
}) {
  const setArg = (i: number, val: string) => {
    const next = [...binding.args]; next[i] = val;
    onChange({ ...binding, args: next });
  };
  const addArg = () => onChange({ ...binding, args: [...binding.args, ""] });
  const removeArg = (i: number) => onChange({ ...binding, args: binding.args.filter((_, j) => j !== i) });

  const setEnvKey = (oldKey: string, newKey: string) => {
    const next: Record<string, string> = {};
    for (const [k, v] of Object.entries(binding.env)) next[k === oldKey ? newKey : k] = v;
    onChange({ ...binding, env: next });
  };
  const setEnvVal = (key: string, val: string) =>
    onChange({ ...binding, env: { ...binding.env, [key]: val } });
  const addEnv = () => onChange({ ...binding, env: { ...binding.env, "": "" } });
  const removeEnv = (key: string) => {
    const next = { ...binding.env }; delete next[key];
    onChange({ ...binding, env: next });
  };

  return (
    <Card className="relative">
      <CardContent className="pt-4 space-y-3">
        <div className="flex items-start gap-3">
          <div className="flex-1 space-y-1.5">
            <Label className="text-xs text-muted-foreground">Server Name</Label>
            <Input
              value={binding.name}
              onChange={(e) => onChange({ ...binding, name: e.target.value })}
              placeholder="my-mcp-server"
              className="font-mono text-sm"
            />
          </div>
          <div className="flex-1 space-y-1.5">
            <Label className="text-xs text-muted-foreground">Transport</Label>
            <Select
              value={binding.transport || "stdio"}
              onValueChange={(v) => onChange({ ...binding, transport: v as "stdio" | "sse" | "http" })}
            >
              <SelectTrigger className="text-sm"><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="stdio">stdio</SelectItem>
                <SelectItem value="sse">SSE</SelectItem>
                <SelectItem value="http">HTTP</SelectItem>
              </SelectContent>
            </Select>
          </div>
          {canRemove && (
            <Button variant="ghost" size="icon" className="mt-5 size-8 shrink-0 text-destructive" onClick={onRemove}>
              <X className="size-3.5" />
            </Button>
          )}
        </div>

        {(binding.transport === "stdio" || !binding.transport) && (
          <>
            <div className="space-y-1.5">
              <Label className="text-xs text-muted-foreground">Command</Label>
              <Input
                value={binding.command}
                onChange={(e) => onChange({ ...binding, command: e.target.value })}
                placeholder="docker"
                className="font-mono text-sm"
              />
            </div>
            <div className="space-y-1.5">
              <Label className="text-xs text-muted-foreground">Args</Label>
              <div className="space-y-1.5">
                {binding.args.map((arg, i) => (
                  <div key={i} className="flex items-center gap-2">
                    <Input
                      value={arg}
                      onChange={(e) => setArg(i, e.target.value)}
                      className="font-mono text-sm"
                    />
                    <Button variant="ghost" size="icon" className="size-8 shrink-0" onClick={() => removeArg(i)}>
                      <X className="size-3" />
                    </Button>
                  </div>
                ))}
                <Button variant="outline" size="sm" onClick={addArg}>
                  <Plus className="mr-1.5 size-3" /> Add Arg
                </Button>
              </div>
            </div>
            <div className="space-y-1.5">
              <Label className="text-xs text-muted-foreground">Environment Variables</Label>
              <div className="space-y-1.5">
                {Object.entries(binding.env).map(([key, val]) => (
                  <div key={key} className="flex items-center gap-2">
                    <Input
                      value={key}
                      onChange={(e) => setEnvKey(key, e.target.value)}
                      placeholder="KEY"
                      className="font-mono text-sm w-40"
                    />
                    <Input
                      value={val}
                      onChange={(e) => setEnvVal(key, e.target.value)}
                      placeholder="value"
                      className="font-mono text-sm"
                    />
                    <Button variant="ghost" size="icon" className="size-8 shrink-0" onClick={() => removeEnv(key)}>
                      <X className="size-3" />
                    </Button>
                  </div>
                ))}
                <Button variant="outline" size="sm" onClick={addEnv}>
                  <Plus className="mr-1.5 size-3" /> Add Env Var
                </Button>
              </div>
            </div>
          </>
        )}

        {(binding.transport === "sse" || binding.transport === "http") && (
          <div className="space-y-1.5">
            <Label className="text-xs text-muted-foreground">Endpoint URL</Label>
            <Input
              value={binding.endpoint || ""}
              onChange={(e) => onChange({ ...binding, endpoint: e.target.value })}
              placeholder="http://localhost:8811/sse"
              className="font-mono text-sm"
            />
          </div>
        )}

        <div className="flex gap-6 pt-1">
          <div className="flex items-center gap-2">
            <Switch
              id={`sso-${index}`}
              checked={binding.passSsoToken}
              onCheckedChange={(v) => onChange({ ...binding, passSsoToken: v })}
            />
            <Label htmlFor={`sso-${index}`} className="text-xs font-normal cursor-pointer">Pass SSO Token</Label>
          </div>
          <div className="flex items-center gap-2">
            <Switch
              id={`tenant-${index}`}
              checked={binding.passTenantHeaders}
              onCheckedChange={(v) => onChange({ ...binding, passTenantHeaders: v })}
            />
            <Label htmlFor={`tenant-${index}`} className="text-xs font-normal cursor-pointer">Pass Tenant Headers</Label>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Advanced Config Panel (inline — mirrors AgentBuilder version)
// ─────────────────────────────────────────────────────────────────────────────

function AdvancedConfigPanel({
  form, set, defaults,
}: {
  form: CreateGroupAgentDto;
  set: (field: keyof CreateGroupAgentDto, value: unknown) => void;
  defaults: AgentDefaults | null;
}) {
  const [open, setOpen] = useState(false);
  const [newVarKey, setNewVarKey] = useState("");

  const hasAdvancedConfig = !!(
    form.verificationMode || form.maxContinuations != null ||
    form.maxToolResultChars != null || form.maxOutputTokens != null ||
    form.contextWindowJson || form.customVariablesJson ||
    form.pipelineStagesJson || form.toolFilterJson || form.stageInstructionsJson
  );
  useEffect(() => { if (hasAdvancedConfig) setOpen(true); }, [hasAdvancedConfig]);

  const contextWindow = parseJson<Record<string, number>>(form.contextWindowJson, {});
  const customVars = parseJson<Record<string, string>>(form.customVariablesJson, {});
  const toolFilter = parseJson<{ mode?: string; tools?: string[] }>(form.toolFilterJson, {});

  const setContextWindow = (key: string, val: number | undefined) => {
    const next = { ...contextWindow };
    if (val === undefined || isNaN(val)) delete next[key];
    else next[key] = val;
    set("contextWindowJson", Object.keys(next).length > 0 ? JSON.stringify(next) : undefined);
  };
  const setCustomVar = (key: string, val: string) =>
    set("customVariablesJson", JSON.stringify({ ...customVars, [key]: val }));
  const removeCustomVar = (key: string) => {
    const next = { ...customVars }; delete next[key];
    set("customVariablesJson", Object.keys(next).length > 0 ? JSON.stringify(next) : undefined);
  };
  const setToolFilter = (mode: string, tools: string[]) => {
    if (!mode) { set("toolFilterJson", undefined); return; }
    set("toolFilterJson", JSON.stringify({ mode, tools }));
  };

  const contextFields = [
    { key: "BudgetTokens", label: "Budget Tokens", defaultKey: "budgetTokens" },
    { key: "CompactionThreshold", label: "Compaction %", defaultKey: "compactionThreshold" },
    { key: "KeepLastRaw", label: "Keep Last Raw", defaultKey: "keepLastRawMessages" },
    { key: "MaxHistoryTurns", label: "Max History Turns", defaultKey: "maxHistoryTurns" },
  ] as const;

  return (
    <Collapsible open={open} onOpenChange={setOpen}>
      <CollapsibleTrigger asChild>
        <Button variant="ghost" className="w-full justify-between px-4 py-3 h-auto rounded-lg border border-dashed">
          <div className="flex items-center gap-2">
            <Settings2 className="size-4 text-muted-foreground" />
            <span className="text-sm font-medium">Advanced Configuration</span>
          </div>
          {open ? <ChevronDown className="size-4" /> : <ChevronRight className="size-4" />}
        </Button>
      </CollapsibleTrigger>
      <CollapsibleContent className="mt-4 space-y-6">

        <div className="space-y-2">
          <Label>Verification Mode</Label>
          <Select
            value={form.verificationMode || "__default__"}
            onValueChange={(v) => set("verificationMode", v === "__default__" ? undefined : v)}
          >
            <SelectTrigger className="w-64"><SelectValue /></SelectTrigger>
            <SelectContent>
              <SelectItem value="__default__">
                {defaults ? `Default (${defaults.verificationMode})` : "Default (global config)"}
              </SelectItem>
              {VERIFICATION_MODES.map((m) => <SelectItem key={m} value={m}>{m}</SelectItem>)}
            </SelectContent>
          </Select>
        </div>

        <div className="space-y-2">
          <Label>Max Continuations</Label>
          <Input
            type="number" min={0} max={10}
            value={form.maxContinuations ?? ""}
            onChange={(e) => set("maxContinuations", e.target.value ? parseInt(e.target.value) : undefined)}
            placeholder={defaults ? `Default: ${defaults.maxContinuations}` : "Default (global)"}
            className="w-40"
          />
          <p className="text-xs text-muted-foreground">Number of continuation windows (0–10)</p>
        </div>

        <div className="space-y-2">
          <Label>Tool Result Char Limit</Label>
          <Input
            type="number" min={100}
            value={form.maxToolResultChars ?? ""}
            onChange={(e) => set("maxToolResultChars", e.target.value ? parseInt(e.target.value) : undefined)}
            placeholder={defaults ? `Default: ${defaults.maxToolResultChars}` : "Default (global)"}
            className="w-40"
          />
        </div>

        <div className="space-y-2">
          <Label>Max Output Tokens</Label>
          <Input
            type="number" min={256} max={65536}
            value={form.maxOutputTokens ?? ""}
            onChange={(e) => set("maxOutputTokens", e.target.value ? parseInt(e.target.value) : undefined)}
            placeholder={defaults ? `Default: ${defaults.maxOutputTokens}` : "Default (global)"}
            className="w-40"
          />
        </div>

        <div className="space-y-2">
          <Label>Context Window Override</Label>
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            {contextFields.map(({ key, label, defaultKey }) => (
              <div key={key} className="space-y-1.5">
                <Label className="text-xs text-muted-foreground">{label}</Label>
                <Input
                  type="number"
                  value={contextWindow[key] ?? ""}
                  onChange={(e) => setContextWindow(key, e.target.value ? parseInt(e.target.value) : undefined)}
                  placeholder={defaults ? String(defaults.contextWindow[defaultKey]) : "Default"}
                />
              </div>
            ))}
          </div>
        </div>

        <div className="space-y-2">
          <Label>Custom Variables</Label>
          <div className="space-y-2">
            {Object.entries(customVars).map(([key, val]) => (
              <div key={key} className="flex items-center gap-2">
                <span className="w-32 font-mono text-sm text-muted-foreground shrink-0">{key}</span>
                <Input value={val} onChange={(e) => setCustomVar(key, e.target.value)} className="font-mono text-sm" />
                <Button variant="ghost" size="icon" className="size-8 shrink-0" onClick={() => removeCustomVar(key)}>
                  <X className="size-3" />
                </Button>
              </div>
            ))}
            <div className="flex gap-2">
              <Input
                value={newVarKey}
                onChange={(e) => setNewVarKey(e.target.value)}
                placeholder="variable_name"
                className="font-mono text-sm w-48"
                onKeyDown={(e) => {
                  if (e.key === "Enter" && newVarKey.trim()) {
                    setCustomVar(newVarKey.trim(), ""); setNewVarKey("");
                  }
                }}
              />
              <Button
                variant="outline" size="sm"
                onClick={() => { if (newVarKey.trim()) { setCustomVar(newVarKey.trim(), ""); setNewVarKey(""); } }}
              >
                <Plus className="mr-1 size-3" /> Add
              </Button>
            </div>
          </div>
        </div>

        <div className="space-y-2">
          <Label>Tool Filter</Label>
          <div className="flex items-center gap-3">
            <Select
              value={toolFilter.mode || "__none__"}
              onValueChange={(v) => setToolFilter(v === "__none__" ? "" : v, toolFilter.tools ?? [])}
            >
              <SelectTrigger className="w-40"><SelectValue placeholder="Allow All" /></SelectTrigger>
              <SelectContent>
                <SelectItem value="__none__">Allow All</SelectItem>
                <SelectItem value="allow">Allow List</SelectItem>
                <SelectItem value="deny">Deny List</SelectItem>
              </SelectContent>
            </Select>
            {toolFilter.mode && (
              <Input
                value={(toolFilter.tools ?? []).join(", ")}
                onChange={(e) => setToolFilter(
                  toolFilter.mode ?? "allow",
                  e.target.value.split(",").map((s) => s.trim()).filter(Boolean)
                )}
                placeholder="tool1, tool2, tool3"
                className="font-mono text-sm"
              />
            )}
          </div>
        </div>

      </CollapsibleContent>
    </Collapsible>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Import from Agent Dialog
// ─────────────────────────────────────────────────────────────────────────────

function ImportAgentDialog({ onImport }: { onImport: (agent: AgentDefinition) => void }) {
  const [open, setOpen] = useState(false);
  const [agents, setAgents] = useState<AgentSummary[]>([]);
  const [loading, setLoading] = useState(false);
  const [selectedId, setSelectedId] = useState<string>("");

  const load = async () => {
    setLoading(true);
    try {
      const list = await api.listAgents();
      setAgents(list.filter((a) => !a.isShared));
    } catch { toast.error("Failed to load agents"); }
    finally { setLoading(false); }
  };

  const handleOpen = (v: boolean) => {
    setOpen(v);
    if (v) { setSelectedId(""); void load(); }
  };

  const handleImport = async () => {
    if (!selectedId) return;
    try {
      const agent = await api.getAgent(selectedId);
      onImport(agent);
      setOpen(false);
      toast.success("Agent configuration imported");
    } catch { toast.error("Failed to load agent"); }
  };

  return (
    <>
      <Button variant="outline" size="sm" onClick={() => handleOpen(true)} className="gap-1.5">
        <Download className="size-3.5" /> Import from Agent
      </Button>
      <Dialog open={open} onOpenChange={handleOpen}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Import from Existing Agent</DialogTitle>
            <DialogDescription>
              Select an agent to pre-populate this template with its configuration. You can modify any field before saving.
            </DialogDescription>
          </DialogHeader>
          {loading ? (
            <div className="py-6 text-center text-muted-foreground text-sm">Loading agents…</div>
          ) : (
            <Select value={selectedId} onValueChange={setSelectedId}>
              <SelectTrigger><SelectValue placeholder="Select an agent…" /></SelectTrigger>
              <SelectContent>
                {agents.map((a) => (
                  <SelectItem key={a.id} value={a.id}>
                    <span className="font-medium">{a.displayName || a.name}</span>
                    <span className="ml-2 text-xs text-muted-foreground">{a.agentType}</span>
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
          <DialogFooter>
            <Button variant="outline" onClick={() => setOpen(false)}>Cancel</Button>
            <Button onClick={handleImport} disabled={!selectedId}>Import</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Main GroupAgentTemplateBuilder component
// ─────────────────────────────────────────────────────────────────────────────

function agentToForm(agent: AgentDefinition): CreateGroupAgentDto {
  return {
    name: agent.name,
    displayName: agent.displayName,
    description: agent.description,
    agentType: agent.agentType,
    systemPrompt: agent.systemPrompt,
    modelId: agent.modelId,
    temperature: agent.temperature,
    maxIterations: agent.maxIterations,
    capabilities: agent.capabilities,
    toolBindings: agent.toolBindings,
    verificationMode: agent.verificationMode,
    contextWindowJson: agent.contextWindowJson,
    customVariablesJson: agent.customVariablesJson,
    maxContinuations: agent.maxContinuations,
    maxToolResultChars: agent.maxToolResultChars,
    maxOutputTokens: agent.maxOutputTokens,
    pipelineStagesJson: agent.pipelineStagesJson,
    toolFilterJson: agent.toolFilterJson,
    stageInstructionsJson: agent.stageInstructionsJson,
    llmConfigId: agent.llmConfigId,
    archetypeId: agent.archetypeId,
    hooksJson: agent.hooksJson,
    a2aEndpoint: agent.a2aEndpoint,
    a2aAuthScheme: agent.a2aAuthScheme,
    a2aSecretRef: agent.a2aSecretRef,
    executionMode: agent.executionMode || "Full",
    modelSwitchingJson: agent.modelSwitchingJson,
    isEnabled: agent.isEnabled,
    status: agent.status || "Published",
  };
}

function templateToForm(template: GroupAgentTemplate): CreateGroupAgentDto {
  return {
    name: template.name,
    displayName: template.displayName,
    description: template.description,
    agentType: template.agentType,
    systemPrompt: template.systemPrompt,
    modelId: template.modelId,
    temperature: template.temperature,
    maxIterations: template.maxIterations,
    toolBindings: template.toolBindings,
    verificationMode: template.verificationMode,
    contextWindowJson: template.contextWindowJson,
    customVariablesJson: template.customVariablesJson,
    maxContinuations: template.maxContinuations,
    maxToolResultChars: template.maxToolResultChars,
    maxOutputTokens: template.maxOutputTokens,
    pipelineStagesJson: template.pipelineStagesJson,
    toolFilterJson: template.toolFilterJson,
    stageInstructionsJson: template.stageInstructionsJson,
    llmConfigId: template.llmConfigId,
    archetypeId: template.archetypeId,
    hooksJson: template.hooksJson,
    a2aEndpoint: template.a2aEndpoint,
    a2aAuthScheme: template.a2aAuthScheme,
    a2aSecretRef: template.a2aSecretRef,
    executionMode: template.executionMode || "Full",
    modelSwitchingJson: template.modelSwitchingJson,
    isEnabled: template.isEnabled,
    status: template.status || "Published",
  };
}

export function GroupAgentTemplateBuilder() {
  const { groupId: groupIdStr, templateId } = useParams<{ groupId: string; templateId: string }>();
  const groupId = Number(groupIdStr);
  const navigate = useNavigate();
  const location = useLocation();

  const [form, setForm] = useState<CreateGroupAgentDto>(DEFAULT_FORM);
  const [bindings, setBindings] = useState<McpToolBinding[]>([{ ...EMPTY_BINDING }]);
  const [saving, setSaving] = useState(false);
  const [llmConfig, setLlmConfig] = useState<LlmConfig>({ availableModels: [], currentProvider: "", defaultModel: "" });
  const [agentDefaults, setAgentDefaults] = useState<AgentDefaults | null>(null);
  const [availableLlmConfigs, setAvailableLlmConfigs] = useState<AvailableLlmConfig[]>([]);

  // Load static data on mount
  useEffect(() => {
    api.getLlmConfig().then(setLlmConfig).catch(() => {});
    api.getAgentDefaults().then(setAgentDefaults).catch(() => {});
    api.listAvailableLlmConfigs().then(setAvailableLlmConfigs).catch(() => {});
  }, []);

  // Load template for edit mode; or pre-populate from importAgent state
  useEffect(() => {
    const importAgent = (location.state as { importAgent?: AgentDefinition } | null)?.importAgent;
    if (templateId) {
      api.getGroupAgent(groupId, templateId)
        .then((t) => {
          setForm(templateToForm(t));
          try {
            const parsed = t.toolBindings ? JSON.parse(t.toolBindings) : [];
            setBindings(parsed.length > 0 ? parsed : [{ ...EMPTY_BINDING }]);
          } catch { setBindings([{ ...EMPTY_BINDING }]); }
        })
        .catch((e: Error) => toast.error("Failed to load template", { description: e.message }));
    } else if (importAgent) {
      setForm(agentToForm(importAgent));
      try {
        const parsed = importAgent.toolBindings ? JSON.parse(importAgent.toolBindings) : [];
        setBindings(parsed.length > 0 ? parsed : [{ ...EMPTY_BINDING }]);
      } catch { setBindings([{ ...EMPTY_BINDING }]); }
    }
  }, [groupId, templateId, location.state]);

  const set = (field: keyof CreateGroupAgentDto, value: unknown) =>
    setForm((f) => ({ ...f, [field]: value }));

  const updateBinding = (i: number, b: McpToolBinding) =>
    setBindings((bs) => bs.map((x, j) => (j === i ? b : x)));

  const handleImportAgent = (agent: AgentDefinition) => {
    setForm(agentToForm(agent));
    try {
      const parsed = agent.toolBindings ? JSON.parse(agent.toolBindings) : [];
      setBindings(parsed.length > 0 ? parsed : [{ ...EMPTY_BINDING }]);
    } catch { setBindings([{ ...EMPTY_BINDING }]); }
  };

  const handleSave = async () => {
    if (!form.name.trim()) { toast.error("Agent name is required"); return; }
    if (!form.agentType.trim()) { toast.error("Agent type is required"); return; }
    setSaving(true);
    try {
      const hasBindings = bindings.some(
        (b) => b.name.trim() !== "" && (b.command.trim() !== "" || (b.endpoint ?? "").trim() !== "")
      );
      const dto: CreateGroupAgentDto = {
        ...form,
        toolBindings: hasBindings ? JSON.stringify(bindings) : undefined,
      };
      if (templateId) {
        await api.updateGroupAgent(groupId, templateId, dto);
        toast.success("Template updated");
      } else {
        await api.createGroupAgent(groupId, dto);
        toast.success("Template created");
      }
      navigate(`/platform/groups/${groupId}`);
    } catch (e: unknown) {
      toast.error("Failed to save template", { description: String(e) });
    } finally {
      setSaving(false);
    }
  };

  const isCreate = !templateId;

  return (
    <div className="space-y-6 max-w-4xl">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">
            {isCreate ? "New Agent Template" : "Edit Agent Template"}
          </h1>
          <p className="text-sm text-muted-foreground">
            {isCreate
              ? "Create a shared agent template for this group"
              : `Editing: ${form.displayName || form.name || templateId}`}
          </p>
        </div>
        <div className="flex gap-3">
          {isCreate && <ImportAgentDialog onImport={handleImportAgent} />}
          <Button variant="outline" onClick={() => navigate(`/platform/groups/${groupId}`)}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving || !form.name || !form.agentType}>
            <Save className="mr-2 size-4" />
            {saving ? "Saving…" : isCreate ? "Create Template" : "Save Changes"}
          </Button>
        </div>
      </div>

      <Tabs defaultValue="identity">
        <TabsList className="grid w-full grid-cols-4">
          <TabsTrigger value="identity" className="gap-1.5">
            <Bot className="size-3.5" />Identity
          </TabsTrigger>
          <TabsTrigger value="model" className="gap-1.5">
            <Cpu className="size-3.5" />Model & Prompt
          </TabsTrigger>
          <TabsTrigger value="tools" className="gap-1.5">
            <Server className="size-3.5" />Tool Servers
          </TabsTrigger>
          <TabsTrigger value="advanced" className="gap-1.5">
            <Sliders className="size-3.5" />Advanced
          </TabsTrigger>
        </TabsList>

        {/* ── Identity Tab ───────────────────────────────────────────────── */}
        <TabsContent value="identity" className="mt-6 space-y-6">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Template Identity</CardTitle>
              <CardDescription>Basic information about this shared agent template</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                <div className="space-y-1.5">
                  <Label htmlFor="name">Name (slug) <span className="text-destructive">*</span></Label>
                  <Input
                    id="name"
                    value={form.name}
                    onChange={(e) => set("name", e.target.value)}
                    placeholder="group-support-agent"
                    className="font-mono"
                  />
                  <p className="text-xs text-muted-foreground">Unique identifier, lowercase with hyphens</p>
                </div>
                <div className="space-y-1.5">
                  <Label htmlFor="displayName">Display Name</Label>
                  <Input
                    id="displayName"
                    value={form.displayName}
                    onChange={(e) => set("displayName", e.target.value)}
                    placeholder="Group Support Agent"
                  />
                </div>
              </div>

              <div className="space-y-1.5">
                <Label htmlFor="description">Description</Label>
                <Textarea
                  id="description"
                  value={form.description ?? ""}
                  onChange={(e) => set("description", e.target.value)}
                  placeholder="What this agent does and when to use it"
                  rows={3}
                />
              </div>

              <div className="space-y-1.5">
                <Label>Agent Type <span className="text-destructive">*</span></Label>
                <Select value={form.agentType} onValueChange={(v) => set("agentType", v)}>
                  <SelectTrigger className="w-64">
                    <SelectValue placeholder="Select type…" />
                  </SelectTrigger>
                  <SelectContent>
                    {AGENT_TYPES.map((t) => <SelectItem key={t} value={t}>{t}</SelectItem>)}
                  </SelectContent>
                </Select>
                <p className="text-xs text-muted-foreground">
                  Used for business rule routing and prompt override matching
                </p>
              </div>

              <Separator />

              <div className="flex items-center gap-6">
                <div className="space-y-1.5">
                  <Label>Status</Label>
                  <Select value={form.status || "Published"} onValueChange={(v) => set("status", v)}>
                    <SelectTrigger className="w-36"><SelectValue /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="Draft">Draft</SelectItem>
                      <SelectItem value="Published">Published</SelectItem>
                    </SelectContent>
                  </Select>
                </div>
                <div className="flex items-center gap-2 pt-5">
                  <Switch
                    id="isEnabled"
                    checked={form.isEnabled}
                    onCheckedChange={(v) => set("isEnabled", v)}
                  />
                  <Label htmlFor="isEnabled" className="cursor-pointer">Enabled</Label>
                </div>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle className="text-base">Archetype</CardTitle>
              <CardDescription>Start from a pre-built template to configure defaults automatically</CardDescription>
            </CardHeader>
            <CardContent>
              <ArchetypeSelector
                selected={form.archetypeId}
                onSelect={(arch) => {
                  set("archetypeId", arch.id);
                  if (!form.systemPrompt) set("systemPrompt", arch.systemPromptTemplate);
                  if (!form.capabilities) set("capabilities", JSON.stringify(arch.defaultCapabilities));
                  set("temperature", arch.defaultTemperature);
                  set("maxIterations", arch.defaultMaxIterations);
                  if (arch.defaultVerificationMode) set("verificationMode", arch.defaultVerificationMode);
                  if (arch.defaultExecutionMode && arch.defaultExecutionMode !== "Full") set("executionMode", arch.defaultExecutionMode);
                  if (Object.keys(arch.defaultHooks).length > 0) set("hooksJson", JSON.stringify(arch.defaultHooks));
                }}
              />
            </CardContent>
          </Card>
        </TabsContent>

        {/* ── Model & Prompt Tab ─────────────────────────────────────────── */}
        <TabsContent value="model" className="mt-6 space-y-6">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Model Configuration</CardTitle>
              <CardDescription>LLM model and behaviour settings for this template</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-1.5">
                <Label>LLM Config</Label>
                <Select
                  value={form.llmConfigId?.toString() ?? "__default__"}
                  onValueChange={(v) => set("llmConfigId", v === "__default__" ? undefined : parseInt(v))}
                >
                  <SelectTrigger className="w-80"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__default__">Platform default (hierarchy)</SelectItem>
                    {availableLlmConfigs.map((c) => (
                      <SelectItem key={c.id} value={c.id.toString()}>
                        {c.displayName}{c.provider ? ` (${c.provider})` : ""}
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <p className="text-xs text-muted-foreground">
                  Pin this template to a specific LLM config, overriding the platform→group→tenant hierarchy.
                </p>
              </div>

              <div className="space-y-1.5">
                <Label>Model</Label>
                <Select
                  value={form.modelId ?? "__default__"}
                  onValueChange={(v) => set("modelId", v === "__default__" ? undefined : v)}
                >
                  <SelectTrigger className="w-64"><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__default__">
                      Default ({llmConfig.defaultModel || "global config"})
                    </SelectItem>
                    {llmConfig.availableModels.map((m) => (
                      <SelectItem key={m} value={m}>{m}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <p className="text-xs text-muted-foreground">
                  Tenants can override this via their overlay configuration.
                </p>
              </div>

              <div className="grid grid-cols-1 sm:grid-cols-2 gap-6">
                <div className="space-y-2">
                  <Label>Temperature: {(form.temperature ?? 0.7).toFixed(1)}</Label>
                  <Slider
                    min={0} max={1} step={0.1}
                    value={[form.temperature ?? 0.7]}
                    onValueChange={([v]) => set("temperature", v)}
                    className="w-full"
                  />
                  <p className="text-xs text-muted-foreground">0 = deterministic, 1 = creative</p>
                </div>
                <div className="space-y-1.5">
                  <Label htmlFor="maxIterations">Max Iterations</Label>
                  <Input
                    id="maxIterations"
                    type="number" min={1} max={50}
                    value={form.maxIterations ?? 10}
                    onChange={(e) => set("maxIterations", parseInt(e.target.value))}
                    className="w-32"
                  />
                  <p className="text-xs text-muted-foreground">ReAct loop iterations</p>
                </div>
              </div>
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle className="text-base">System Prompt</CardTitle>
              <CardDescription>
                Base instructions for the agent. Tenants may append addenda via their overlay.
                Augmented at runtime with group/tenant business rules and prompt overrides.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <Textarea
                rows={14}
                value={form.systemPrompt ?? ""}
                onChange={(e) => set("systemPrompt", e.target.value)}
                placeholder="You are a helpful assistant specialising in…"
                className="font-mono text-sm resize-y"
              />
            </CardContent>
          </Card>
        </TabsContent>

        {/* ── Tool Servers Tab ───────────────────────────────────────────── */}
        <TabsContent value="tools" className="mt-6 space-y-6">
          <div className="flex items-center justify-between">
            <div>
              <h3 className="text-sm font-medium">MCP Tool Servers</h3>
              <p className="text-xs text-muted-foreground">
                {bindings.filter((b) => b.name.trim()).length} server
                {bindings.filter((b) => b.name.trim()).length !== 1 ? "s" : ""} configured.
                Tenants may append additional tools via their overlay.
              </p>
            </div>
            <Button
              variant="outline" size="sm"
              onClick={() => setBindings((bs) => [...bs, { ...EMPTY_BINDING }])}
            >
              <Plus className="mr-1.5 size-3.5" /> Add Server
            </Button>
          </div>

          <div className="space-y-3">
            {bindings.map((b, i) => (
              <McpBindingEditor
                key={i}
                index={i}
                binding={b}
                onChange={(updated) => updateBinding(i, updated)}
                onRemove={() => setBindings((bs) => bs.filter((_, j) => j !== i))}
                canRemove={bindings.length > 1}
              />
            ))}
          </div>
        </TabsContent>

        {/* ── Advanced Tab ───────────────────────────────────────────────── */}
        <TabsContent value="advanced" className="mt-6 space-y-4">
          <AdvancedConfigPanel form={form} set={set} defaults={agentDefaults} />

          <Card>
            <CardHeader>
              <CardTitle className="text-base">Execution Mode</CardTitle>
              <CardDescription>Controls what the agent is allowed to do at runtime</CardDescription>
            </CardHeader>
            <CardContent>
              <Select
                value={form.executionMode || "Full"}
                onValueChange={(v) => set("executionMode", v)}
              >
                <SelectTrigger className="w-48"><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="Full">Full (default)</SelectItem>
                  <SelectItem value="ChatOnly">Chat Only</SelectItem>
                  <SelectItem value="ReadOnly">Read Only</SelectItem>
                  <SelectItem value="Supervised">Supervised</SelectItem>
                </SelectContent>
              </Select>
              <p className="text-xs text-muted-foreground mt-1.5">
                ChatOnly removes all tools. ReadOnly keeps only read-level tools. Supervised requires approval.
              </p>
            </CardContent>
          </Card>

          <HookEditor value={form.hooksJson} onChange={(json) => set("hooksJson", json)} />

          <A2AConfigPanel
            endpoint={form.a2aEndpoint}
            authScheme={form.a2aAuthScheme}
            secretRef={form.a2aSecretRef}
            remoteAgentId={form.a2aRemoteAgentId}
            onEndpointChange={(v) => set("a2aEndpoint", v)}
            onAuthSchemeChange={(v) => set("a2aAuthScheme", v)}
            onSecretRefChange={(v) => set("a2aSecretRef", v)}
            onRemoteAgentIdChange={(v) => set("a2aRemoteAgentId", v)}
          />

          {/* Model Switching (JSON textarea for advanced users) */}
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Model Switching</CardTitle>
              <CardDescription>
                Optional per-iteration model switching rules (JSON). Leave empty to disable.
              </CardDescription>
            </CardHeader>
            <CardContent>
              <Textarea
                rows={4}
                value={form.modelSwitchingJson ?? ""}
                onChange={(e) => set("modelSwitchingJson", e.target.value || undefined)}
                placeholder='[{"iteration": 3, "modelId": "claude-opus-4-6", "reason": "complex reasoning"}]'
                className="font-mono text-xs resize-y"
              />
            </CardContent>
          </Card>
        </TabsContent>
      </Tabs>

      {/* Footer actions */}
      <div className="flex items-center justify-between border-t pt-4">
        <div className="flex items-center gap-2 text-xs text-muted-foreground">
          <Badge variant="outline">Group Template</Badge>
          <span>Tenants activate and customise this via overlay configuration</span>
        </div>
        <div className="flex gap-3">
          <Button variant="outline" onClick={() => navigate(`/platform/groups/${groupId}`)}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving || !form.name || !form.agentType}>
            <Save className="mr-2 size-4" />
            {saving ? "Saving…" : isCreate ? "Create Template" : "Save Changes"}
          </Button>
        </div>
      </div>
    </div>
  );
}
