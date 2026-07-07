import { useState, useEffect, useCallback } from "react";
import {
  api,
  type McpServer,
  type CreateMcpServerDto,
  type McpCredential,
  type PlatformApiKey,
  type ApiKeyCredentialMapping,
} from "@/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Skeleton } from "@/components/ui/skeleton";
import { Plus, Trash2, Save, Server, X, Pencil } from "lucide-react";
import { toast } from "sonner";

const TRANSPORTS = ["stdio", "http", "sse"] as const;

const NO_CREDENTIAL = "__none__";  // sentinel — Radix Select cannot use an empty-string value

interface ServerForm {
  id?: number;
  name: string;
  description: string;
  transport: string;
  command: string;
  argsText: string;          // one arg per line → argsJson
  envText: string;           // KEY=VALUE per line → envJson
  endpoint: string;
  passSsoToken: boolean;
  passTenantHeaders: boolean;
  defaultCredentialRef: string;
  mappings: ApiKeyCredentialMapping[];
}

const EMPTY_FORM: ServerForm = {
  name: "", description: "", transport: "stdio", command: "", argsText: "",
  envText: "", endpoint: "", passSsoToken: false, passTenantHeaders: false,
  defaultCredentialRef: "", mappings: [],
};

// ── JSON ⇄ text helpers ───────────────────────────────────────────────────────
function parseArgs(text: string): string[] {
  return text.split("\n").map(l => l.trim()).filter(Boolean);
}
function parseEnv(text: string): Record<string, string> {
  const env: Record<string, string> = {};
  for (const line of text.split("\n")) {
    const t = line.trim();
    if (!t) continue;
    const eq = t.indexOf("=");
    if (eq <= 0) continue;
    env[t.slice(0, eq).trim()] = t.slice(eq + 1).trim();
  }
  return env;
}
function argsToText(json?: string): string {
  if (!json) return "";
  try { const a = JSON.parse(json) as string[]; return Array.isArray(a) ? a.join("\n") : ""; }
  catch { return ""; }
}
function envToText(json?: string): string {
  if (!json) return "";
  try {
    const e = JSON.parse(json) as Record<string, string>;
    return Object.entries(e).map(([k, v]) => `${k}=${v}`).join("\n");
  } catch { return ""; }
}
function parseMappings(json?: string): ApiKeyCredentialMapping[] {
  if (!json) return [];
  try {
    const m = JSON.parse(json) as ApiKeyCredentialMapping[];
    return Array.isArray(m) ? m : [];
  } catch { return []; }
}

export function McpServerManager() {
  const [servers, setServers] = useState<McpServer[]>([]);
  const [credentials, setCredentials] = useState<McpCredential[]>([]);
  const [apiKeys, setApiKeys] = useState<PlatformApiKey[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState<ServerForm>(EMPTY_FORM);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [s, c, k] = await Promise.all([
        api.listMcpServers(),
        api.listCredentials().catch(() => [] as McpCredential[]),
        api.listApiKeys().catch(() => [] as PlatformApiKey[]),
      ]);
      setServers(s);
      setCredentials(c);
      setApiKeys(k);
    } catch { toast.error("Failed to load MCP servers"); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const openCreate = () => { setForm(EMPTY_FORM); setShowForm(true); };

  const openEdit = (s: McpServer) => {
    setForm({
      id: s.id,
      name: s.name,
      description: s.description ?? "",
      transport: s.transport || "stdio",
      command: s.command ?? "",
      argsText: argsToText(s.argsJson),
      envText: envToText(s.envJson),
      endpoint: s.endpoint ?? "",
      passSsoToken: s.passSsoToken,
      passTenantHeaders: s.passTenantHeaders,
      defaultCredentialRef: s.defaultCredentialRef ?? "",
      mappings: parseMappings(s.apiKeyCredentialMappingsJson),
    });
    setShowForm(true);
  };

  const closeForm = () => { setShowForm(false); setForm(EMPTY_FORM); };

  const buildDto = (): CreateMcpServerDto => {
    const isHttp = form.transport === "http" || form.transport === "sse";
    const validMappings = form.mappings.filter(m => m.apiKeyId > 0 && m.credentialRef.trim());
    return {
      name: form.name.trim(),
      description: form.description.trim() || undefined,
      transport: form.transport,
      command: isHttp ? undefined : (form.command.trim() || undefined),
      argsJson: isHttp ? undefined : JSON.stringify(parseArgs(form.argsText)),
      envJson: isHttp ? undefined : JSON.stringify(parseEnv(form.envText)),
      endpoint: isHttp ? (form.endpoint.trim() || undefined) : undefined,
      passSsoToken: form.passSsoToken,
      passTenantHeaders: form.passTenantHeaders,
      defaultCredentialRef: form.defaultCredentialRef.trim() || undefined,
      apiKeyCredentialMappingsJson: validMappings.length ? JSON.stringify(validMappings) : undefined,
    };
  };

  const handleSave = async () => {
    if (!form.name.trim()) { toast.error("Name is required"); return; }
    const isHttp = form.transport === "http" || form.transport === "sse";
    if (isHttp && !form.endpoint.trim()) { toast.error("Endpoint is required for http/sse transport"); return; }
    if (!isHttp && !form.command.trim()) { toast.error("Command is required for stdio transport"); return; }

    const dto = buildDto();
    try {
      if (form.id) {
        await api.updateMcpServer(form.id, dto);
        toast.success(`MCP server "${dto.name}" updated`);
      } else {
        await api.createMcpServer(dto);
        toast.success(`MCP server "${dto.name}" created`);
      }
      closeForm();
      load();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : "Failed to save MCP server");
    }
  };

  const handleDelete = async (id: number, name: string) => {
    if (!confirm(`Delete MCP server "${name}"? Agents referencing it will lose this tool server.`)) return;
    try {
      await api.deleteMcpServer(id);
      toast.success(`MCP server "${name}" deleted`);
      load();
    } catch { toast.error("Failed to delete MCP server"); }
  };

  const addMapping = () =>
    setForm(f => ({ ...f, mappings: [...f.mappings, { apiKeyId: 0, credentialRef: "" }] }));
  const updateMapping = (i: number, patch: Partial<ApiKeyCredentialMapping>) =>
    setForm(f => ({ ...f, mappings: f.mappings.map((m, idx) => idx === i ? { ...m, ...patch } : m) }));
  const removeMapping = (i: number) =>
    setForm(f => ({ ...f, mappings: f.mappings.filter((_, idx) => idx !== i) }));

  const isHttp = form.transport === "http" || form.transport === "sse";
  const credName = (id: number) => apiKeys.find(k => k.id === id)?.name ?? `#${id}`;

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-bold flex items-center gap-2"><Server className="h-6 w-6" /> Shared MCP Servers</h2>
          <p className="text-sm text-muted-foreground">
            Reusable tool servers shared across all agents. Each server picks its credential dynamically based on the
            platform API key used to invoke the agent.
          </p>
        </div>
        {!showForm && (
          <Button onClick={openCreate}><Plus className="h-4 w-4 mr-1" /> Add Server</Button>
        )}
      </div>

      {showForm && (
        <Card>
          <CardHeader className="flex flex-row items-center justify-between">
            <CardTitle>{form.id ? "Edit MCP Server" : "New MCP Server"}</CardTitle>
            <Button variant="ghost" size="sm" onClick={closeForm}><X className="h-4 w-4" /></Button>
          </CardHeader>
          <CardContent className="grid gap-4">
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label>Name</Label>
                <Input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="e.g. weather-server" />
              </div>
              <div className="space-y-1.5">
                <Label>Transport</Label>
                <Select value={form.transport} onValueChange={(v) => setForm({ ...form, transport: v })}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {TRANSPORTS.map((t) => <SelectItem key={t} value={t}>{t}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
            </div>

            <div className="space-y-1.5">
              <Label>Description</Label>
              <Input value={form.description} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder="Optional description" />
            </div>

            {isHttp ? (
              <>
                <div className="space-y-1.5">
                  <Label>Endpoint URL</Label>
                  <Input value={form.endpoint} onChange={(e) => setForm({ ...form, endpoint: e.target.value })} placeholder="https://mcp.example.com/sse" />
                </div>
                <div className="flex items-center gap-6">
                  <label className="flex items-center gap-2 text-sm">
                    <Switch checked={form.passSsoToken} onCheckedChange={(v) => setForm({ ...form, passSsoToken: v })} />
                    Pass SSO token
                  </label>
                  <label className="flex items-center gap-2 text-sm">
                    <Switch checked={form.passTenantHeaders} onCheckedChange={(v) => setForm({ ...form, passTenantHeaders: v })} />
                    Pass tenant headers
                  </label>
                </div>
              </>
            ) : (
              <>
                <div className="space-y-1.5">
                  <Label>Command</Label>
                  <Input value={form.command} onChange={(e) => setForm({ ...form, command: e.target.value })} placeholder="e.g. npx or docker" />
                </div>
                <div className="grid grid-cols-2 gap-4">
                  <div className="space-y-1.5">
                    <Label>Arguments <span className="text-xs text-muted-foreground">(one per line)</span></Label>
                    <Textarea rows={3} value={form.argsText} onChange={(e) => setForm({ ...form, argsText: e.target.value })} placeholder={"-y\n@modelcontextprotocol/server-weather"} className="font-mono text-xs" />
                  </div>
                  <div className="space-y-1.5">
                    <Label>Environment <span className="text-xs text-muted-foreground">(KEY=VALUE per line)</span></Label>
                    <Textarea rows={3} value={form.envText} onChange={(e) => setForm({ ...form, envText: e.target.value })} placeholder={"LOG_LEVEL=info"} className="font-mono text-xs" />
                  </div>
                </div>
              </>
            )}

            <div className="space-y-1.5">
              <Label>Default credential <span className="text-xs text-muted-foreground">(used for JWT/SSO callers with no API-key mapping)</span></Label>
              <Select
                value={form.defaultCredentialRef || NO_CREDENTIAL}
                onValueChange={(v) => setForm({ ...form, defaultCredentialRef: v === NO_CREDENTIAL ? "" : v })}
              >
                <SelectTrigger><SelectValue placeholder="None (SSO passthrough / unauthenticated)" /></SelectTrigger>
                <SelectContent>
                  <SelectItem value={NO_CREDENTIAL}>None (SSO passthrough / unauthenticated)</SelectItem>
                  {credentials.map((c) => <SelectItem key={c.id} value={c.name}>{c.name}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>

            {/* ── Per-API-key credential routing ───────────────────────────── */}
            <div className="space-y-2 rounded-md border p-3">
              <div className="flex items-center justify-between">
                <div>
                  <Label>Per-API-key credentials</Label>
                  <p className="text-xs text-muted-foreground">When an agent is invoked with a platform API key, the matching credential below is used.</p>
                </div>
                <Button variant="outline" size="sm" onClick={addMapping}><Plus className="h-4 w-4 mr-1" /> Add rule</Button>
              </div>
              {form.mappings.length === 0 ? (
                <p className="text-xs text-muted-foreground py-1">No per-key rules — all API-key callers fall back to the default credential.</p>
              ) : (
                <div className="space-y-2">
                  {form.mappings.map((m, i) => (
                    <div key={i} className="flex items-center gap-2">
                      <Select value={m.apiKeyId ? String(m.apiKeyId) : ""} onValueChange={(v) => updateMapping(i, { apiKeyId: Number(v) })}>
                        <SelectTrigger className="flex-1"><SelectValue placeholder="Select API key…" /></SelectTrigger>
                        <SelectContent>
                          {apiKeys.map((k) => <SelectItem key={k.id} value={String(k.id)}>{k.name} <span className="text-muted-foreground">({k.keyPrefix})</span></SelectItem>)}
                        </SelectContent>
                      </Select>
                      <span className="text-muted-foreground text-sm">→</span>
                      <Select value={m.credentialRef || ""} onValueChange={(v) => updateMapping(i, { credentialRef: v })}>
                        <SelectTrigger className="flex-1"><SelectValue placeholder="Select credential…" /></SelectTrigger>
                        <SelectContent>
                          {credentials.map((c) => <SelectItem key={c.id} value={c.name}>{c.name}</SelectItem>)}
                        </SelectContent>
                      </Select>
                      <Button variant="ghost" size="sm" onClick={() => removeMapping(i)}><Trash2 className="h-4 w-4" /></Button>
                    </div>
                  ))}
                </div>
              )}
              {apiKeys.length === 0 && (
                <p className="text-xs text-amber-600">No platform API keys exist yet. Create them under Settings → API Keys.</p>
              )}
            </div>

            <div className="flex gap-2">
              <Button onClick={handleSave}><Save className="h-4 w-4 mr-1" /> {form.id ? "Save changes" : "Create"}</Button>
              <Button variant="outline" onClick={closeForm}>Cancel</Button>
            </div>
          </CardContent>
        </Card>
      )}

      {loading ? (
        <div className="space-y-3">{[1, 2, 3].map(i => <Skeleton key={i} className="h-16 w-full" />)}</div>
      ) : servers.length === 0 && !showForm ? (
        <Card><CardContent className="py-8 text-center text-muted-foreground">No shared MCP servers configured. Click "Add Server" to create one.</CardContent></Card>
      ) : (
        <div className="space-y-3">
          {servers.map((s) => {
            const mappings = parseMappings(s.apiKeyCredentialMappingsJson);
            return (
              <Card key={s.id}>
                <CardContent className="flex items-center justify-between py-4">
                  <div className="space-y-1">
                    <div className="flex items-center gap-2">
                      <span className="font-medium">{s.name}</span>
                      <Badge variant="outline">{s.transport}</Badge>
                      {s.defaultCredentialRef && <Badge variant="secondary">default: {s.defaultCredentialRef}</Badge>}
                      {mappings.length > 0 && <Badge variant="default">{mappings.length} key rule{mappings.length === 1 ? "" : "s"}</Badge>}
                      {s.passSsoToken && <Badge variant="outline">SSO</Badge>}
                    </div>
                    <div className="text-xs text-muted-foreground">
                      {s.description && <span>{s.description} · </span>}
                      {s.transport === "stdio" ? <span className="font-mono">{s.command}</span> : <span className="font-mono">{s.endpoint}</span>}
                      {mappings.length > 0 && (
                        <span> · {mappings.map(m => `${credName(m.apiKeyId)}→${m.credentialRef}`).join(", ")}</span>
                      )}
                    </div>
                  </div>
                  <div className="flex gap-2">
                    <Button variant="outline" size="sm" onClick={() => openEdit(s)}><Pencil className="h-4 w-4" /></Button>
                    <Button variant="destructive" size="sm" onClick={() => handleDelete(s.id, s.name)}><Trash2 className="h-4 w-4" /></Button>
                  </div>
                </CardContent>
              </Card>
            );
          })}
        </div>
      )}
    </div>
  );
}
