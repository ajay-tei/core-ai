import { useState, useEffect, useCallback } from "react";
import { api, type PlatformApiKey, type ApiKeyCreatedResult, type CreateApiKeyDto, type AgentGroup } from "@/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Plus, Trash2, RotateCw, Copy, KeySquare, AlertTriangle } from "lucide-react";
import { toast } from "sonner";

const SCOPES = ["invoke", "admin", "readonly"] as const;

export function ApiKeyManager() {
  const [keys, setKeys] = useState<PlatformApiKey[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [newKeyResult, setNewKeyResult] = useState<ApiKeyCreatedResult | null>(null);
  const [form, setForm] = useState<CreateApiKeyDto>({ name: "", scope: "invoke" });
  const [groups, setGroups] = useState<AgentGroup[]>([]);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [keyList, groupList] = await Promise.all([api.listApiKeys(), api.listAgentGroups().catch(() => [])]);
      setKeys(keyList);
      setGroups(groupList);
    }
    catch { toast.error("Failed to load API keys"); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleCreate = async () => {
    if (!form.name.trim()) { toast.error("Name is required"); return; }
    try {
      const result = await api.createApiKey(form);
      setNewKeyResult(result);
      setForm({ name: "", scope: "invoke" });
      setShowCreate(false);
      load();
    } catch { toast.error("Failed to create API key"); }
  };

  const handleRevoke = async (id: number, name: string) => {
    if (!confirm(`Revoke API key "${name}"? This cannot be undone.`)) return;
    try {
      await api.revokeApiKey(id);
      toast.success(`API key "${name}" revoked`);
      load();
    } catch { toast.error("Failed to revoke API key"); }
  };

  const handleRotate = async (id: number, name: string) => {
    if (!confirm(`Rotate API key "${name}"? The old key will stop working immediately.`)) return;
    try {
      const result = await api.rotateApiKey(id);
      setNewKeyResult(result);
      toast.success(`API key "${name}" rotated`);
      load();
    } catch { toast.error("Failed to rotate API key"); }
  };

  const copyKey = (key: string) => {
    navigator.clipboard.writeText(key);
    toast.success("Key copied to clipboard");
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-bold flex items-center gap-2"><KeySquare className="h-6 w-6" /> Platform API Keys</h2>
          <p className="text-sm text-muted-foreground">Create API keys for non-SSO access (external systems, scheduled tasks, CI/CD)</p>
        </div>
        <Button onClick={() => setShowCreate(!showCreate)}>
          <Plus className="h-4 w-4 mr-1" />{showCreate ? "Cancel" : "Create API Key"}
        </Button>
      </div>

      {/* New key display — shown once after create/rotate */}
      {newKeyResult && (
        <Card className="border-yellow-400 bg-yellow-50 dark:bg-yellow-950">
          <CardContent className="py-4 space-y-3">
            <div className="flex items-center gap-2 text-yellow-700 dark:text-yellow-300">
              <AlertTriangle className="h-5 w-5" />
              <span className="font-semibold">Save this API key now — it won&apos;t be shown again!</span>
            </div>
            <div className="flex items-center gap-2">
              <code className="flex-1 bg-background rounded px-3 py-2 text-sm font-mono border break-all">{newKeyResult.rawKey}</code>
              <Button variant="outline" size="sm" onClick={() => copyKey(newKeyResult.rawKey)}><Copy className="h-4 w-4" /></Button>
            </div>
            <div className="text-xs text-muted-foreground">
              Name: {newKeyResult.name} · Scope: {newKeyResult.scope} · Prefix: {newKeyResult.keyPrefix}
            </div>
            <Button variant="outline" size="sm" onClick={() => setNewKeyResult(null)}>Dismiss</Button>
          </CardContent>
        </Card>
      )}

      {showCreate && (
        <Card>
          <CardHeader><CardTitle>New API Key</CardTitle></CardHeader>
          <CardContent className="grid gap-4">
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label>Name</Label>
                <Input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="e.g. CI/CD Pipeline" />
              </div>
              <div className="space-y-1.5">
                <Label>Scope</Label>
                <Select value={form.scope ?? "invoke"} onValueChange={(v) => setForm({ ...form, scope: v })}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {SCOPES.map((s) => <SelectItem key={s} value={s}>{s === "invoke" ? "Invoke (agent calls)" : s === "admin" ? "Admin (full access)" : "Read-only"}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
            </div>
            {groups.length > 0 && (
              <div className="space-y-1.5">
                <Label>Allowed Agent Groups (optional)</Label>
                <p className="text-xs text-muted-foreground">Grant this key access to agents in restricted groups. Leave empty for no extra grants.</p>
                <div className="flex flex-wrap gap-2">
                  {groups.map((g) => {
                    const selected = (form.allowedGroupIds ?? []).includes(g.id);
                    return (
                      <Badge
                        key={g.id}
                        variant={selected ? "default" : "outline"}
                        className="cursor-pointer"
                        onClick={() => setForm({
                          ...form,
                          allowedGroupIds: selected
                            ? (form.allowedGroupIds ?? []).filter((id) => id !== g.id)
                            : [...(form.allowedGroupIds ?? []), g.id],
                        })}
                      >
                        {g.name}
                      </Badge>
                    );
                  })}
                </div>
              </div>
            )}
            <Button onClick={handleCreate}><Plus className="h-4 w-4 mr-1" /> Generate Key</Button>
          </CardContent>
        </Card>
      )}

      {loading ? (
        <div className="space-y-3">{[1,2,3].map(i => <Skeleton key={i} className="h-16 w-full" />)}</div>
      ) : keys.length === 0 ? (
        <Card><CardContent className="py-8 text-center text-muted-foreground">No API keys created. Click "Create API Key" to get started.</CardContent></Card>
      ) : (
        <div className="space-y-3">
          {keys.map((k) => (
            <Card key={k.id}>
              <CardContent className="flex items-center justify-between py-4">
                <div className="space-y-1">
                  <div className="flex items-center gap-2">
                    <span className="font-medium">{k.name}</span>
                    <Badge variant={k.isActive ? "default" : "secondary"}>{k.isActive ? "Active" : "Revoked"}</Badge>
                    <Badge variant="outline">{k.scope}</Badge>
                    <code className="text-xs bg-muted px-1.5 py-0.5 rounded">{k.keyPrefix}…</code>
                  </div>
                  <div className="text-xs text-muted-foreground">
                    Created {new Date(k.createdAt).toLocaleDateString()}
                    {k.expiresAt && <span> · Expires {new Date(k.expiresAt).toLocaleDateString()}</span>}
                    {k.lastUsedAt && <span> · Last used {new Date(k.lastUsedAt).toLocaleDateString()}</span>}
                    {k.allowedAgentIds && k.allowedAgentIds.length > 0 && <span> · Restricted to {k.allowedAgentIds.length} agent(s)</span>}
                    {k.allowedGroupIds && k.allowedGroupIds.length > 0 && <span> · Grants {k.allowedGroupIds.length} group(s)</span>}
                  </div>
                </div>
                <div className="flex gap-2">
                  {k.isActive && (
                    <Button variant="outline" size="sm" onClick={() => handleRotate(k.id, k.name)}>
                      <RotateCw className="h-4 w-4 mr-1" /> Rotate
                    </Button>
                  )}
                  {k.isActive && (
                    <Button variant="destructive" size="sm" onClick={() => handleRevoke(k.id, k.name)}>
                      <Trash2 className="h-4 w-4" />
                    </Button>
                  )}
                </div>
              </CardContent>
            </Card>
          ))}
        </div>
      )}
    </div>
  );
}
