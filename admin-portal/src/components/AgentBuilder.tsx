import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { toast } from "sonner";
import {
  Bot,
  ChevronDown,
  ChevronRight,
  Code2,
  Cpu,
  Download,
  History,
  Info,
  Plus,
  Save,
  Server,
  Settings2,
  Sliders,
  Sparkles,
  Upload,
  X,
} from "lucide-react";
import { AgentAssistantDrawer } from "@/components/AgentAssistantDrawer";
import { AgentImportDialog } from "@/components/AgentImportDialog";
import { PromptQuickFixDialog } from "@/components/PromptQuickFixDialog";
import {
  api,
  type AgentArchetype,
  type AgentDefaults,
  type AgentDefinition,
  type AgentImportResult,
  type AgentPromptHistoryEntry,
  type AvailableLlmConfig,
  type LlmConfig,
  type McpCredential,
  type McpToolBinding,
  type McpToolInfo,
} from "@/api";
import { triggerJsonDownload } from "@/lib/download";
import { ArchetypeSelector } from "@/components/ArchetypeSelector";
import { HookEditor } from "@/components/HookEditor";
import { A2AConfigPanel } from "@/components/A2AConfigPanel";
import { DelegateAgentSelector } from "@/components/DelegateAgentSelector";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Slider } from "@/components/ui/slider";
import { Separator } from "@/components/ui/separator";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import { Alert, AlertDescription } from "@/components/ui/alert";

// ─────────────────────────────────────────────────────────────────────────────
// Constants
// ─────────────────────────────────────────────────────────────────────────────

const EMPTY_BINDING: McpToolBinding = {
  name: "",
  command: "docker",
  args: ["run", "-i", "--rm"],
  env: {},
  endpoint: "",
  transport: "stdio",
  passSsoToken: false,
  passTenantHeaders: false,
};

const DEFAULT_AGENT: AgentDefinition = {
  name: "", displayName: "", description: "", agentType: "general",
  systemPrompt: "", temperature: 0.7, maxIterations: 10, isEnabled: true, status: "Draft",
};

const PROMPT_VARIABLES = [
  { name: "user_id",       description: "Logged-in user's ID (JWT sub claim)" },
  { name: "user_email",    description: "Logged-in user's email address" },
  { name: "user_name",     description: "Logged-in user's display name" },
  { name: "tenant_id",     description: "Current tenant ID" },
  { name: "tenant_name",   description: "Current tenant name" },
  { name: "current_date",  description: "Today's date (yyyy-MM-dd UTC)" },
  { name: "current_time",  description: "Current time (HH:mm UTC)" },
  { name: "current_datetime", description: "Date and time (yyyy-MM-dd HH:mm UTC)" },
];

function PromptVariableReference() {
  const [open, setOpen] = useState(false);
  return (
    <div className="rounded-md border bg-muted/30">
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        className="flex w-full items-center gap-1.5 px-3 py-2 text-xs text-muted-foreground hover:text-foreground transition-colors"
      >
        <Info className="size-3.5 shrink-0" />
        <span className="font-medium">Available template variables</span>
        {open ? <ChevronDown className="ml-auto size-3.5" /> : <ChevronRight className="ml-auto size-3.5" />}
      </button>
      {open && (
        <div className="border-t px-3 pb-3 pt-2">
          <p className="mb-2 text-xs text-muted-foreground">
            Use <code className="rounded bg-muted px-1 font-mono">{"{{variable_name}}"}</code> in your system prompt. Values are resolved at runtime per request.
          </p>
          <table className="w-full text-xs">
            <thead>
              <tr className="border-b text-left text-muted-foreground">
                <th className="pb-1 pr-4 font-medium">Variable</th>
                <th className="pb-1 font-medium">Description</th>
              </tr>
            </thead>
            <tbody>
              {PROMPT_VARIABLES.map((v) => (
                <tr key={v.name} className="border-b border-dashed last:border-0">
                  <td className="py-1 pr-4">
                    <code className="rounded bg-muted px-1 font-mono text-violet-600 dark:text-violet-400">
                      {`{{${v.name}}}`}
                    </code>
                  </td>
                  <td className="py-1 text-muted-foreground">{v.description}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="mt-2 text-xs text-muted-foreground">
            Custom variables defined in <span className="font-medium">Custom Variables</span> (Advanced tab) take precedence over all built-ins.
          </p>
        </div>
      )}
    </div>
  );
}

const VERIFICATION_MODES = ["", "Off", "ToolGrounded", "LlmVerifier", "Strict", "Auto"] as const;

function parseJson<T>(json: string | undefined, fallback: T): T {
  if (!json) return fallback;
  try { return JSON.parse(json); }
  catch { return fallback; }
}

// ─────────────────────────────────────────────────────────────────────────────
// MCP Binding Editor
// ─────────────────────────────────────────────────────────────────────────────

function McpBindingEditor({
  binding, index, onChange, onRemove, canRemove, credentials,
}: {
  binding: McpToolBinding;
  index: number;
  onChange: (b: McpToolBinding) => void;
  onRemove: () => void;
  canRemove: boolean;
  credentials: McpCredential[];
}) {
  const [jsonMode, setJsonMode] = useState(false);
  const [jsonText, setJsonText] = useState("");
  const [jsonError, setJsonError] = useState<string | null>(null);

  const enterJsonMode = () => {
    setJsonText(JSON.stringify({ command: binding.command, args: binding.args, env: binding.env }, null, 2));
    setJsonError(null);
    setJsonMode(true);
  };

  const applyJson = () => {
    try {
      const parsed = JSON.parse(jsonText);
      onChange({
        ...binding,
        command: parsed.command ?? binding.command,
        args: Array.isArray(parsed.args) ? parsed.args : binding.args,
        env: (parsed.env && typeof parsed.env === "object") ? parsed.env : binding.env,
      });
      setJsonError(null);
      setJsonMode(false);
    } catch (e) {
      setJsonError(String(e));
    }
  };

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
  const setEnvVal = (key: string, val: string) => onChange({ ...binding, env: { ...binding.env, [key]: val } });
  const addEnv = () => onChange({ ...binding, env: { ...binding.env, "": "" } });
  const removeEnv = (key: string) => {
    const next = { ...binding.env }; delete next[key];
    onChange({ ...binding, env: next });
  };

  return (
    <Card className="border-dashed">
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <Server className="size-4 text-muted-foreground" />
            <span className="text-sm font-medium">Server #{index + 1}</span>
            {binding.name && <Badge variant="secondary">{binding.name}</Badge>}
          </div>
          <div className="flex items-center gap-2">
            <Button
              variant="ghost"
              size="sm"
              onClick={jsonMode ? applyJson : enterJsonMode}
            >
              <Code2 className="mr-1.5 size-3.5" />
              {jsonMode ? "Apply JSON" : "JSON Mode"}
            </Button>
            {jsonMode && (
              <Button variant="ghost" size="sm" onClick={() => setJsonMode(false)}>Cancel</Button>
            )}
            {canRemove && (
              <Button variant="ghost" size="sm" className="text-destructive hover:text-destructive" onClick={onRemove}>
                <X className="size-3.5" />
              </Button>
            )}
          </div>
        </div>
      </CardHeader>
      <CardContent className="space-y-4">
        <div className="grid grid-cols-2 gap-4">
          <div className="space-y-1.5">
            <Label>Server Name</Label>
            <Input
              value={binding.name}
              onChange={(e) => onChange({ ...binding, name: e.target.value })}
              placeholder="openweather"
            />
          </div>
          <div className="space-y-1.5">
            <Label>Transport</Label>
            <Select
              value={binding.transport}
              onValueChange={(v) => onChange({ ...binding, transport: v })}
            >
              <SelectTrigger>
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="stdio">stdio (docker / npx)</SelectItem>
                <SelectItem value="http">HTTP/SSE (container port)</SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>

        {binding.transport === "http" ? (
          <div className="space-y-2">
            <div className="space-y-1.5">
              <Label>SSE Endpoint URL</Label>
              <Input
                value={binding.endpoint}
                onChange={(e) => onChange({ ...binding, endpoint: e.target.value })}
                placeholder="http://localhost:8080/sse"
                className="font-mono text-sm"
              />
            </div>
            <div className="flex gap-4 text-sm">
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={binding.passSsoToken ?? false}
                  onChange={e => onChange({ ...binding, passSsoToken: e.target.checked })}
                />
                Pass SSO token to this MCP server
              </label>
              <label className="flex items-center gap-2 cursor-pointer">
                <input
                  type="checkbox"
                  checked={binding.passTenantHeaders ?? false}
                  onChange={e => onChange({ ...binding, passTenantHeaders: e.target.checked })}
                />
                Pass tenant headers only (no Bearer)
              </label>
            </div>
            {credentials.length > 0 && (
              <div className="space-y-1.5">
                <Label>Credential (fallback when no SSO token)</Label>
                <Select
                  value={binding.credentialRef ?? "__none__"}
                  onValueChange={(v) => onChange({ ...binding, credentialRef: v === "__none__" ? undefined : v })}
                >
                  <SelectTrigger>
                    <SelectValue placeholder="None — no credential" />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__none__">None</SelectItem>
                    {credentials.filter(c => c.isActive).map(c => (
                      <SelectItem key={c.id} value={c.name}>
                        {c.name} ({c.authScheme})
                      </SelectItem>
                    ))}
                  </SelectContent>
                </Select>
                <p className="text-xs text-muted-foreground">
                  Injects the credential API key when no SSO token is available (e.g. scheduled tasks)
                </p>
              </div>
            )}
          </div>
        ) : jsonMode ? (
          <div className="space-y-1.5">
            <Label>Paste Claude Desktop config (command + args + env)</Label>
            <Textarea
              rows={8}
              value={jsonText}
              onChange={(e) => setJsonText(e.target.value)}
              className="font-mono text-sm"
            />
            {jsonError && (
              <Alert variant="destructive">
                <AlertDescription>{jsonError}</AlertDescription>
              </Alert>
            )}
            <p className="text-xs text-muted-foreground">
              Accepts the inner object from <code className="font-mono">mcpServers["server-name"]</code>
            </p>
          </div>
        ) : (
          <>
            <div className="space-y-1.5">
              <Label>Command (executable)</Label>
              <Input
                value={binding.command}
                onChange={(e) => onChange({ ...binding, command: e.target.value })}
                placeholder="docker"
                className="font-mono text-sm"
              />
            </div>

            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <Label>Arguments</Label>
                <Button variant="ghost" size="sm" onClick={addArg}>
                  <Plus className="mr-1 size-3" /> Add arg
                </Button>
              </div>
              <div className="space-y-1.5">
                {binding.args.map((arg, i) => (
                  <div key={i} className="flex items-center gap-2">
                    <span className="w-6 text-right text-xs text-muted-foreground">{i}</span>
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
                {binding.args.length === 0 && (
                  <p className="text-sm text-muted-foreground">No arguments</p>
                )}
              </div>
              <p className="text-xs text-muted-foreground">
                Docker example: run -i --rm -e OWM_API_KEY mcp/openweather
              </p>
            </div>

            <div className="space-y-2">
              <div className="flex items-center justify-between">
                <Label>Environment Variables</Label>
                <Button variant="ghost" size="sm" onClick={addEnv}>
                  <Plus className="mr-1 size-3" /> Add var
                </Button>
              </div>
              <div className="space-y-1.5">
                {Object.entries(binding.env).map(([key, val]) => (
                  <div key={key} className="flex items-center gap-2">
                    <Input
                      value={key}
                      onChange={(e) => setEnvKey(key, e.target.value)}
                      placeholder="KEY"
                      className="font-mono text-sm w-2/5"
                    />
                    <span className="text-muted-foreground">=</span>
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
                {Object.keys(binding.env).length === 0 && (
                  <p className="text-sm text-muted-foreground">No environment variables</p>
                )}
              </div>
            </div>
          </>
        )}
      </CardContent>
    </Card>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Import JSON Panel (Dialog)
// ─────────────────────────────────────────────────────────────────────────────

function ImportJsonDialog({ onImport }: { onImport: (bindings: McpToolBinding[]) => void }) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState("");
  const [error, setError] = useState<string | null>(null);

  const apply = () => {
    try {
      const root = JSON.parse(text);
      const servers = root.mcpServers ?? root;
      const result: McpToolBinding[] = Object.entries(servers).map(([name, cfg]: [string, unknown]) => {
        const c = cfg as Record<string, unknown>;
        return {
          name,
          command: String(c.command ?? ""),
          args: Array.isArray(c.args) ? (c.args as string[]) : [],
          env: (c.env && typeof c.env === "object") ? c.env as Record<string, string> : {},
          endpoint: "",
          transport: "stdio",
          passSsoToken: false,
          passTenantHeaders: false,
        };
      });
      onImport(result);
      setOpen(false);
      setText("");
      setError(null);
      toast.success(`Imported ${result.length} server binding${result.length !== 1 ? "s" : ""}`);
    } catch (e) {
      setError(String(e));
    }
  };

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>
        <Button variant="outline" size="sm">
          Import JSON Config
        </Button>
      </DialogTrigger>
      <DialogContent className="max-w-2xl">
        <DialogHeader>
          <DialogTitle>Import MCP Config JSON</DialogTitle>
          <DialogDescription>
            Paste your Claude Desktop or MCP config JSON to import server bindings
          </DialogDescription>
        </DialogHeader>
        <Textarea
          rows={14}
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder="{&#10;  &quot;mcpServers&quot;: {&#10;    &quot;openweather&quot;: { ... }&#10;  }&#10;}"
          className="font-mono text-sm"
        />
        {error && (
          <Alert variant="destructive">
            <AlertDescription>{error}</AlertDescription>
          </Alert>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={() => setOpen(false)}>Cancel</Button>
          <Button onClick={apply}>Import</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Docker MCP Gateway Panel
// ─────────────────────────────────────────────────────────────────────────────

function DockerGatewayPanel({ onUse }: { onUse: (binding: McpToolBinding) => void }) {
  const [mode, setMode] = useState<"stdio" | "http">("stdio");
  const [port, setPort] = useState("8811");
  const [probing, setProbing] = useState(false);
  const [tools, setTools] = useState<McpToolInfo[] | null>(null);
  const [probeError, setProbeError] = useState<string | null>(null);

  const reset = () => { setTools(null); setProbeError(null); };

  const probe = async () => {
    setProbing(true); reset();
    try {
      const opts = mode === "http"
        ? { endpoint: `http://localhost:${port}/sse` }
        : { command: "docker", args: ["mcp", "gateway", "run"] };
      const result = await api.probeMcp(opts);
      if (result.success) setTools(result.tools);
      else setProbeError(result.error ?? "Connection failed");
    } catch (e) {
      setProbeError(String(e));
    } finally {
      setProbing(false);
    }
  };

  const useGateway = () => {
    if (mode === "http") {
      onUse({ name: "docker-mcp-gateway", command: "", args: [], env: {}, endpoint: `http://localhost:${port}/sse`, transport: "http", passSsoToken: false, passTenantHeaders: false });
    } else {
      onUse({ name: "docker-mcp-gateway", command: "docker", args: ["mcp", "gateway", "run"], env: {}, endpoint: "", transport: "stdio", passSsoToken: false, passTenantHeaders: false });
    }
    toast.success("Docker MCP Gateway binding applied");
  };

  return (
    <Card className="border-blue-500/30 bg-blue-500/5">
      <CardHeader className="pb-3">
        <div className="flex items-center justify-between">
          <div className="flex items-center gap-2">
            <span className="text-lg">🐳</span>
            <div>
              <CardTitle className="text-sm text-blue-600 dark:text-blue-400">Docker MCP Gateway</CardTitle>
              <CardDescription className="text-xs">
                Routes all Docker MCP Catalog servers through one gateway
              </CardDescription>
            </div>
          </div>
          <div className="flex rounded-md border border-blue-500/30 overflow-hidden">
            {(["stdio", "http"] as const).map((m) => (
              <button
                key={m}
                onClick={() => { setMode(m); reset(); }}
                className={`px-3 py-1 text-xs transition-colors ${
                  mode === m
                    ? "bg-blue-600 text-white"
                    : "bg-transparent text-muted-foreground hover:bg-muted"
                }`}
              >
                {m === "stdio" ? "stdio (default)" : "HTTP/SSE"}
              </button>
            ))}
          </div>
        </div>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="flex items-center gap-3">
          {mode === "stdio" ? (
            <code className="rounded bg-muted px-3 py-1.5 text-sm font-mono text-muted-foreground">
              docker mcp gateway run
            </code>
          ) : (
            <div className="flex items-center gap-1.5 rounded bg-muted px-3 py-1.5">
              <span className="text-sm text-muted-foreground font-mono">http://localhost:</span>
              <input
                type="number"
                value={port}
                onChange={(e) => { setPort(e.target.value); reset(); }}
                className="w-16 bg-transparent font-mono text-sm focus:outline-none"
              />
              <span className="text-sm text-muted-foreground font-mono">/sse</span>
            </div>
          )}
          <Button variant="outline" size="sm" onClick={probe} disabled={probing}>
            {probing ? "Testing..." : "Test Connection"}
          </Button>
          {tools !== null && (
            <Button size="sm" onClick={useGateway}>
              Use This Gateway
            </Button>
          )}
        </div>

        {probeError && (
          <Alert variant="destructive">
            <AlertDescription>{probeError}</AlertDescription>
          </Alert>
        )}

        {tools && (
          <div>
            {tools.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                Connected — no tools enabled. Enable servers in Docker Desktop MCP Toolkit.
              </p>
            ) : (
              <div className="space-y-1.5">
                <p className="text-sm text-emerald-600 dark:text-emerald-400">
                  Connected — {tools.length} tool{tools.length !== 1 ? "s" : ""} available
                </p>
                <div className="flex flex-wrap gap-1.5">
                  {tools.map((t) => (
                    <Badge key={t.name} variant="secondary" className="text-xs font-mono" title={t.description}>
                      {t.name}
                    </Badge>
                  ))}
                </div>
              </div>
            )}
          </div>
        )}
      </CardContent>
    </Card>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Advanced Configuration (Collapsible)
// ─────────────────────────────────────────────────────────────────────────────

function AdvancedConfigPanel({
  form, set, defaults, isEditing,
}: {
  form: AgentDefinition;
  set: (field: keyof AgentDefinition, value: unknown) => void;
  defaults: AgentDefaults | null;
  isEditing?: boolean;
}) {
  const [open, setOpen] = useState(false);
  const [newVarKey, setNewVarKey] = useState("");

  // Auto-expand when editing an agent that already has advanced config values
  const hasAdvancedConfig = !!(
    form.verificationMode ||
    form.maxContinuations != null ||
    form.maxToolResultChars != null ||
    form.maxOutputTokens != null ||
    form.enableHistoryCaching != null ||
    form.contextWindowJson ||
    form.customVariablesJson ||
    form.pipelineStagesJson ||
    form.toolFilterJson ||
    form.stageInstructionsJson
  );
  useEffect(() => { if (hasAdvancedConfig || isEditing) setOpen(true); }, [hasAdvancedConfig, isEditing]);

  const contextWindow = parseJson<Record<string, number>>(form.contextWindowJson, {});
  const optimizationOverride = parseJson<Record<string, number>>(form.optimizationOverrideJson, {});
  const customVars = parseJson<Record<string, string>>(form.customVariablesJson, {});
  const toolFilter = parseJson<{ mode?: string; tools?: string[] }>(form.toolFilterJson, {});

  const setContextWindow = (key: string, val: number | undefined) => {
    const next = { ...contextWindow };
    if (val === undefined || isNaN(val)) delete next[key];
    else next[key] = val;
    set("contextWindowJson", Object.keys(next).length > 0 ? JSON.stringify(next) : undefined);
  };

  const setOptimizationOverride = (key: string, val: number | undefined) => {
    const next = { ...optimizationOverride };
    if (val === undefined || isNaN(val)) delete next[key];
    else next[key] = val;
    set("optimizationOverrideJson", Object.keys(next).length > 0 ? JSON.stringify(next) : undefined);
  };

  const setCustomVar = (key: string, val: string) => {
    set("customVariablesJson", JSON.stringify({ ...customVars, [key]: val }));
  };
  const removeCustomVar = (key: string) => {
    const next = { ...customVars }; delete next[key];
    set("customVariablesJson", Object.keys(next).length > 0 ? JSON.stringify(next) : undefined);
  };
  const setToolFilter = (mode: string, tools: string[]) => {
    if (!mode) { set("toolFilterJson", undefined); return; }
    set("toolFilterJson", JSON.stringify({ mode, tools }));
  };

  const contextFields: Array<{ key: string; label: string; defaultKey: keyof AgentDefaults["contextWindow"] }> = [
    { key: "BudgetTokens", label: "Budget Tokens", defaultKey: "budgetTokens" },
    { key: "CompactionThreshold", label: "Compaction %", defaultKey: "compactionThreshold" },
    { key: "KeepLastRaw", label: "Keep Last Raw", defaultKey: "keepLastRawMessages" },
    { key: "MaxHistoryTurns", label: "Max History Turns", defaultKey: "maxHistoryTurns" },
  ];

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
            <SelectTrigger className="w-64">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="__default__">
                {defaults ? `Default (${defaults.verificationMode})` : "Default (global config)"}
              </SelectItem>
              {VERIFICATION_MODES.filter(Boolean).map((m) => (
                <SelectItem key={m} value={m}>{m}</SelectItem>
              ))}
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
          <p className="text-xs text-muted-foreground">Number of continuation windows (0-10)</p>
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
          <p className="text-xs text-muted-foreground">Max characters per tool response before truncation</p>
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
          <p className="text-xs text-muted-foreground">Max tokens the LLM may generate per call. Lower for fast agents; raise for agents producing long reports.</p>
        </div>

        <div className="flex items-start justify-between gap-4 rounded-lg border p-4">
          <div className="space-y-1">
            <Label htmlFor="enableHistoryCaching">Prompt Caching (Anthropic)</Label>
            <p className="text-xs text-muted-foreground">
              Splits the system prompt into stable/volatile blocks and adds Anthropic cache breakpoints
              on the static block, prior-session history, and tool-result exchanges. Reduces input token
              costs on repeated calls. Only applies when the Anthropic provider is active.
              {defaults && (
                <span className="ml-1 text-muted-foreground/70">
                  Global default: {defaults.enableHistoryCaching ? "enabled" : "disabled"}.
                </span>
              )}
            </p>
          </div>
          <Switch
            id="enableHistoryCaching"
            checked={form.enableHistoryCaching ?? (defaults?.enableHistoryCaching ?? true)}
            onCheckedChange={(checked) =>
              set("enableHistoryCaching", checked === (defaults?.enableHistoryCaching ?? true) ? undefined : checked)
            }
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
          <Label>Optimization Override</Label>
          <p className="text-xs text-muted-foreground">
            Override token limits for the LLM calls that power optimization analysis and smart merge.
            Increase <strong>Merge Token Limit</strong> for agents with very long system prompts.
          </p>
          <div className="grid grid-cols-2 gap-3">
            {[
              { key: "MergeMaxTokens",    label: "Merge Token Limit",    hint: "Default: 8192" },
              { key: "AnalyzerMaxTokens", label: "Analyzer Token Limit", hint: "Default: 2048" },
            ].map(({ key, label, hint }) => (
              <div key={key} className="space-y-1.5">
                <Label className="text-xs text-muted-foreground">{label}</Label>
                <Input
                  type="number"
                  value={optimizationOverride[key] ?? ""}
                  onChange={(e) => setOptimizationOverride(key, e.target.value ? parseInt(e.target.value) : undefined)}
                  placeholder={hint}
                  className="w-40"
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
                <Input
                  value={val}
                  onChange={(e) => setCustomVar(key, e.target.value)}
                  className="font-mono text-sm"
                />
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
                    setCustomVar(newVarKey.trim(), "");
                    setNewVarKey("");
                  }
                }}
              />
              <Button
                variant="outline"
                size="sm"
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
              <SelectTrigger className="w-40">
                <SelectValue placeholder="Allow All" />
              </SelectTrigger>
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
          <p className="text-xs text-muted-foreground">Comma-separated tool names</p>
        </div>

      </CollapsibleContent>
    </Collapsible>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Main AgentBuilder component
// ─────────────────────────────────────────────────────────────────────────────

export function AgentBuilder() {
  const { id: agentId } = useParams<{ id: string }>();
  const navigate = useNavigate();

  const [form, setForm] = useState<AgentDefinition>(DEFAULT_AGENT);
  const [bindings, setBindings] = useState<McpToolBinding[]>([{ ...EMPTY_BINDING }]);
  const [saving, setSaving] = useState(false);
  const [llmConfig, setLlmConfig] = useState<LlmConfig>({ availableModels: [], currentProvider: "", defaultModel: "" });
  const [agentDefaults, setAgentDefaults] = useState<AgentDefaults | null>(null);
  const [availableLlmConfigs, setAvailableLlmConfigs] = useState<AvailableLlmConfig[]>([]);
  const [assistantOpen, setAssistantOpen]   = useState(false);
  const [quickFixOpen, setQuickFixOpen]     = useState(false);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [promptHistory, setPromptHistory] = useState<AgentPromptHistoryEntry[]>([]);
  const [credentials, setCredentials] = useState<McpCredential[]>([]);
  const [importOpen, setImportOpen] = useState(false);

  useEffect(() => {
    api.getLlmConfig().then(setLlmConfig).catch(() => {});
    api.getAgentDefaults().then(setAgentDefaults).catch(() => {});
    api.listAvailableLlmConfigs().then(setAvailableLlmConfigs).catch(() => {});
    api.listCredentials().then(setCredentials).catch(() => {});
  }, []);

  // When the agent's selected LLM config changes (user picks a config, or an agent with
  // a saved llmConfigId is loaded), refresh the model list from that config.
  // availableLlmConfigs already carries availableModels, so no extra API call needed.
  useEffect(() => {
    if (!form.llmConfigId) return;
    const cfg = availableLlmConfigs.find((c) => c.id === form.llmConfigId);
    if (cfg) {
      setLlmConfig((prev) => ({
        ...prev,
        availableModels: cfg.availableModels,
        defaultModel:    cfg.model ?? prev.defaultModel,
        currentProvider: cfg.provider ?? prev.currentProvider,
      }));
    } else {
      // Config not yet in list (race on initial load) — fetch directly
      api.getLlmConfig(form.llmConfigId).then(setLlmConfig).catch(() => {});
    }
  }, [form.llmConfigId, availableLlmConfigs]);

  useEffect(() => {
    if (!agentId) { setForm(DEFAULT_AGENT); setBindings([{ ...EMPTY_BINDING }]); return; }
    api.getAgent(agentId).then((a) => {
      setForm(a);
      try {
        const parsed = a.toolBindings ? JSON.parse(a.toolBindings) : [];
        setBindings(parsed.length > 0 ? parsed : [{ ...EMPTY_BINDING }]);
      } catch { setBindings([{ ...EMPTY_BINDING }]); }
    }).catch((e: Error) => toast.error("Failed to load agent", { description: e.message }));
  }, [agentId]);

  const set = (field: keyof AgentDefinition, value: unknown) =>
    setForm((f) => ({ ...f, [field]: value }));

  const loadPromptHistory = async () => {
    if (!agentId) return;
    try {
      const entries = await api.getPromptHistory(agentId);
      setPromptHistory(entries);
      setHistoryOpen(true);
    } catch {
      toast.error("Failed to load prompt history");
    }
  };

  const updateBinding = (i: number, b: McpToolBinding) =>
    setBindings((bs) => bs.map((x, j) => j === i ? b : x));

  const handleSave = async () => {
    if (!form.name.trim()) {
      toast.error("Agent name is required");
      return;
    }
    try {
      const hasBindings = bindings.some((b) => b.name.trim() !== "" && (b.command.trim() !== "" || b.endpoint.trim() !== ""));
      const dto: AgentDefinition = {
        ...form,
        toolBindings: hasBindings ? JSON.stringify(bindings) : undefined,
        // Ensure non-nullable string fields have defaults (C# entity requires these as non-null)
        executionMode: form.executionMode || "Full",
        status: form.status || "Draft",
      };
      if (agentId) {
        await api.updateAgent(agentId, dto);
        toast.success("Agent updated successfully");
      } else {
        await api.createAgent(dto);
        toast.success("Agent created successfully");
      }
      navigate("/agents");
    } catch (e: unknown) {
      toast.error("Failed to save agent", { description: String(e) });
    } finally {
      setSaving(false);
    }
  };

  const handleExport = async () => {
    if (!agentId) return;
    try {
      const bundle = await api.exportAgent(agentId);
      const filename = `${(form.name || "agent").replace(/\s+/g, "-").toLowerCase()}-export.json`;
      triggerJsonDownload(bundle, filename);
      toast.success("Agent exported");
    } catch (e: unknown) {
      toast.error("Export failed", { description: String(e) });
    }
  };

  const handleImportSuccess = (result: AgentImportResult) => {
    toast.success(`Imported "${result.agentName}"`, {
      description: result.warnings.length > 0 ? result.warnings.join(" ") : undefined,
    });
    navigate(`/agents/${result.agentId}/edit`);
  };

  return (
    <div className="space-y-6 max-w-4xl">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">
            {agentId ? "Edit Agent" : "New Agent"}
          </h1>
          <p className="text-sm text-muted-foreground">
            {agentId ? `Editing: ${form.displayName || form.name || agentId}` : "Configure a new AI agent"}
          </p>
        </div>
        <div className="flex gap-3">
          {agentId && (
            <Button variant="outline" size="sm" onClick={handleExport}>
              <Download className="mr-2 size-4" />
              Export
            </Button>
          )}
          {!agentId && (
            <Button variant="outline" size="sm" onClick={() => setImportOpen(true)}>
              <Upload className="mr-2 size-4" />
              Import
            </Button>
          )}
          <Button variant="outline" onClick={() => navigate("/agents")}>
            Cancel
          </Button>
          <Button onClick={handleSave} disabled={saving || !form.name}>
            <Save className="mr-2 size-4" />
            {saving ? "Saving..." : agentId ? "Save Changes" : "Create Agent"}
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
            {(form.verificationMode || form.pipelineStagesJson || form.hooksJson || form.delegateAgentIdsJson || form.a2aEndpoint) && (
              <span className="ml-1 size-2 rounded-full bg-primary inline-block" title="Has advanced configuration" />
            )}
          </TabsTrigger>
        </TabsList>

        <TabsContent value="identity" className="mt-6 space-y-6">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Agent Identity</CardTitle>
              <CardDescription>Basic information about this agent</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="grid grid-cols-2 gap-4">
                <div className="space-y-1.5">
                  <Label htmlFor="name">Name (slug) <span className="text-destructive">*</span></Label>
                  <Input
                    id="name"
                    value={form.name}
                    onChange={(e) => set("name", e.target.value)}
                    placeholder="openweather-agent"
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
                    placeholder="OpenWeather Agent"
                  />
                </div>
              </div>

              <div className="space-y-1.5">
                <Label htmlFor="description">Description</Label>
                <Textarea
                  id="description"
                  value={form.description}
                  onChange={(e) => set("description", e.target.value)}
                  placeholder="What this agent does and when to use it"
                  rows={3}
                />
              </div>

              <Separator />

              <div className="flex items-center gap-6">
                <div className="space-y-1.5">
                  <Label>Status</Label>
                  <Select value={form.status} onValueChange={(v) => set("status", v)}>
                    <SelectTrigger className="w-36">
                      <SelectValue />
                    </SelectTrigger>
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
                onSelect={(arch: AgentArchetype) => {
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

        <TabsContent value="model" className="mt-6 space-y-6">
          <Card>
            <CardHeader>
              <CardTitle className="text-base">Model Configuration</CardTitle>
              <CardDescription>LLM model and behaviour settings</CardDescription>
            </CardHeader>
            <CardContent className="space-y-4">
              <div className="space-y-1.5">
                  <Label>LLM Config</Label>
                  <Select
                    value={form.llmConfigId?.toString() ?? "__default__"}
                    onValueChange={(v) => set("llmConfigId", v === "__default__" ? undefined : parseInt(v))}
                  >
                    <SelectTrigger className="w-80">
                      <SelectValue />
                    </SelectTrigger>
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
                    Pin this agent to a specific LLM configuration, overriding the platform→group→tenant hierarchy.
                  </p>
                </div>
              <div className="space-y-1.5">
                <Label>Model</Label>
                <Select
                  value={form.modelId ?? "__default__"}
                  onValueChange={(v) => set("modelId", v === "__default__" ? undefined : v)}
                >
                  <SelectTrigger className="w-64">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    <SelectItem value="__default__">
                      Default ({
                        form.llmConfigId
                          ? (availableLlmConfigs.find((c) => c.id === form.llmConfigId)?.model ?? llmConfig.defaultModel ?? "config default")
                          : (llmConfig.defaultModel || "global config")
                      })
                    </SelectItem>
                    {llmConfig.availableModels.map((m) => (
                      <SelectItem key={m} value={m}>{m}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>

              <div className="grid grid-cols-2 gap-6">
                <div className="space-y-2">
                  <Label>Temperature: {form.temperature.toFixed(1)}</Label>
                  <Slider
                    min={0} max={1} step={0.1}
                    value={[form.temperature]}
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
                    value={form.maxIterations}
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
              <div className="flex items-start justify-between gap-4">
                <div>
                  <CardTitle className="text-base">System Prompt</CardTitle>
                  <CardDescription>
                    Base instructions for the agent. Augmented at runtime with business rules and prompt overrides.
                  </CardDescription>
                </div>
                <div className="flex shrink-0 gap-2">
                  {agentId && (
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => { void loadPromptHistory(); }}
                      className="gap-1.5"
                    >
                      <History className="size-3.5" />
                      History
                    </Button>
                  )}
                  {agentId && (
                    <Button
                      variant="outline"
                      size="sm"
                      onClick={() => setQuickFixOpen(true)}
                      className="gap-1.5"
                    >
                      <Sparkles className="size-3.5 text-amber-500" />
                      Quick Fix
                    </Button>
                  )}
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => setAssistantOpen(true)}
                    className="gap-1.5"
                  >
                    <Sparkles className="size-3.5 text-violet-500" />
                    AI Setup Assistant
                  </Button>
                </div>
              </div>
            </CardHeader>
            <CardContent className="space-y-3">
              <PromptVariableReference />
              <Textarea
                rows={12}
                value={form.systemPrompt ?? ""}
                onChange={(e) => set("systemPrompt", e.target.value)}
                placeholder="You are a helpful assistant specialising in..."
                className="font-mono text-sm resize-y"
              />
            </CardContent>
          </Card>

          {agentId && (
            <PromptQuickFixDialog
              agentId={agentId}
              currentPrompt={form.systemPrompt ?? ""}
              open={quickFixOpen}
              onOpenChange={setQuickFixOpen}
              onAccept={(improved) => set("systemPrompt", improved)}
            />
          )}

          <AgentAssistantDrawer
            open={assistantOpen}
            onOpenChange={setAssistantOpen}
            agentId={agentId}
            agentName={form.name}
            agentDescription={form.description ?? ""}
            archetypeId={form.archetypeId}
            toolNames={bindings
              .filter((b) => b.name.trim() !== "")
              .map((b) => b.name)}
            delegateAgentIds={(() => {
              try { return JSON.parse(form.delegateAgentIdsJson ?? "[]") as string[]; }
              catch { return []; }
            })()}
            currentSystemPrompt={form.systemPrompt ?? undefined}
            onApplyPrompt={(prompt) => {
              set("systemPrompt", prompt);
              setAssistantOpen(false);
              toast.success("System prompt applied from AI assistant");
            }}
            onApplyRulePacks={async (packs) => {
              const tid = form.tenantId ?? 1;
              let created = 0;
              for (const pack of packs) {
                if (pack.operation === "delete") continue;
                try {
                  const newPack = await api.createRulePack({
                    name: pack.name,
                    description: pack.description,
                    appliesToJson: form.archetypeId
                      ? JSON.stringify([form.archetypeId])
                      : undefined,
                  }, tid);
                  await Promise.all(
                    pack.rules.map((rule, idx) =>
                      api.addHookRule(newPack.id, {
                        hookPoint: rule.hookPoint,
                        ruleType: rule.ruleType,
                        pattern: rule.pattern,
                        instruction: rule.instruction,
                        replacement: rule.replacement,
                        toolName: rule.toolName,
                        orderInPack: rule.order ?? idx,
                        stopOnMatch: rule.stopOnMatch,
                      }, tid)
                    )
                  );
                  created++;
                } catch {
                  toast.error(`Failed to create rule pack "${pack.name}"`);
                }
              }
              if (created > 0) {
                toast.success(`${created} rule pack${created !== 1 ? "s" : ""} created`);
              }
            }}
          />

          <Dialog open={historyOpen} onOpenChange={setHistoryOpen}>
            <DialogContent className="max-w-2xl max-h-[70vh] overflow-y-auto">
              <DialogHeader>
                <DialogTitle>System Prompt History</DialogTitle>
                <DialogDescription>
                  Previous versions of this agent’s system prompt. Click “Use” to restore a version into the editor.
                </DialogDescription>
              </DialogHeader>
              <div className="space-y-3 mt-2">
                {promptHistory.length === 0 ? (
                  <p className="text-sm text-muted-foreground">No prompt history recorded yet.</p>
                ) : (
                  promptHistory.map((entry) => (
                    <div key={entry.version} className="border rounded-lg p-3 space-y-2">
                      <div className="flex items-start justify-between gap-2">
                        <div className="space-y-1">
                          <div className="flex items-center gap-2 flex-wrap">
                            <Badge variant="outline">v{entry.version}</Badge>
                            <Badge variant="secondary" className="capitalize">
                              {entry.source.replace(/_/g, " ")}
                            </Badge>
                            <span className="text-xs text-muted-foreground">
                              {new Date(entry.createdAtUtc).toLocaleString()} &middot; {entry.createdBy}
                            </span>
                          </div>
                          {entry.reason && (
                            <p className="text-xs text-muted-foreground italic">{entry.reason}</p>
                          )}
                        </div>
                        <Button
                          size="sm"
                          variant="outline"
                          className="shrink-0"
                          onClick={() => {
                            set("systemPrompt", entry.systemPrompt);
                            setHistoryOpen(false);
                            toast.success(`Restored prompt v${entry.version}`);
                          }}
                        >
                          Use
                        </Button>
                      </div>
                      <pre className="text-xs bg-muted rounded p-2 overflow-auto max-h-28 whitespace-pre-wrap font-mono">
                        {entry.systemPrompt.slice(0, 300)}{entry.systemPrompt.length > 300 ? "…" : ""}
                      </pre>
                    </div>
                  ))
                )}
              </div>
            </DialogContent>
          </Dialog>
        </TabsContent>

        <TabsContent value="tools" className="mt-6 space-y-6">
          <DockerGatewayPanel onUse={(binding) => setBindings((bs) => {
            // Replace existing docker-mcp-gateway entry if present, otherwise append.
            // Always remove empty placeholder rows (name == "") so they don't clutter.
            const filtered = bs.filter((b) => b.name.trim() !== "" && b.name !== binding.name);
            return [...filtered, binding];
          })} />

          <div className="space-y-3">
            <div className="flex items-center justify-between">
              <div>
                <h3 className="text-sm font-medium">MCP Tool Servers</h3>
                <p className="text-xs text-muted-foreground">{bindings.length} server{bindings.length !== 1 ? "s" : ""} configured</p>
              </div>
              <div className="flex gap-2">
                <ImportJsonDialog onImport={setBindings} />
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => setBindings((bs) => [...bs, { ...EMPTY_BINDING }])}
                >
                  <Plus className="mr-1.5 size-3.5" />
                  Add Server
                </Button>
              </div>
            </div>

            <div className="space-y-3">
              {bindings.map((b, i) => (
                <McpBindingEditor
                  key={i}
                  index={i}
                  binding={b}
                  credentials={credentials}
                  onChange={(updated) => updateBinding(i, updated)}
                  onRemove={() => setBindings((bs) => bs.filter((_, j) => j !== i))}
                  canRemove={bindings.length > 1}
                />
              ))}
            </div>
          </div>
        </TabsContent>

        <TabsContent value="advanced" className="mt-6 space-y-4">
          <AdvancedConfigPanel form={form} set={set} defaults={agentDefaults} isEditing={!!agentId} />

          <Card>
            <CardHeader>
              <CardTitle className="text-base">Execution Mode</CardTitle>
              <CardDescription>Controls what the agent is allowed to do at runtime</CardDescription>
            </CardHeader>
            <CardContent>
              <Select value={form.executionMode || "Full"} onValueChange={(v) => set("executionMode", v)}>
                <SelectTrigger className="w-48">
                  <SelectValue />
                </SelectTrigger>
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

          <HookEditor
            value={form.hooksJson}
            onChange={(json) => set("hooksJson", json)}
          />

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

          <DelegateAgentSelector
            currentAgentId={agentId}
            value={form.delegateAgentIdsJson}
            onChange={(json) => set("delegateAgentIdsJson", json)}
          />
        </TabsContent>
      </Tabs>

      <div className="flex items-center justify-end gap-3 border-t pt-4">
        <Button variant="outline" onClick={() => navigate("/agents")}>
          Cancel
        </Button>
        <Button onClick={handleSave} disabled={saving || !form.name}>
          <Save className="mr-2 size-4" />
          {saving ? "Saving..." : agentId ? "Save Changes" : "Create Agent"}
        </Button>
      </div>

      <AgentImportDialog
        open={importOpen}
        onOpenChange={setImportOpen}
        onSuccess={handleImportSuccess}
      />
    </div>
  );
}
