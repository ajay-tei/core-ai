import { useState, useEffect, useCallback } from "react";
import { api, type McpCredential, type CreateCredentialDto, type UpdateCredentialDto } from "@/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { Plus, Trash2, Save, KeyRound } from "lucide-react";
import { toast } from "sonner";

const AUTH_SCHEMES = ["Bearer", "ApiKey", "Custom"] as const;

export function CredentialManager() {
  const [credentials, setCredentials] = useState<McpCredential[]>([]);
  const [loading, setLoading] = useState(true);
  const [showCreate, setShowCreate] = useState(false);
  const [form, setForm] = useState<CreateCredentialDto>({ name: "", apiKey: "" });

  const load = useCallback(async () => {
    setLoading(true);
    try { setCredentials(await api.listCredentials()); }
    catch { toast.error("Failed to load credentials"); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleCreate = async () => {
    if (!form.name.trim() || !form.apiKey.trim()) {
      toast.error("Name and API Key are required");
      return;
    }
    try {
      await api.createCredential(form);
      toast.success(`Credential "${form.name}" created`);
      setForm({ name: "", apiKey: "" });
      setShowCreate(false);
      load();
    } catch { toast.error("Failed to create credential"); }
  };

  const handleDelete = async (id: number, name: string) => {
    if (!confirm(`Delete credential "${name}"? This cannot be undone.`)) return;
    try {
      await api.deleteCredential(id);
      toast.success(`Credential "${name}" deleted`);
      load();
    } catch { toast.error("Failed to delete credential"); }
  };

  const handleToggleActive = async (cred: McpCredential) => {
    try {
      await api.updateCredential(cred.id, { isActive: !cred.isActive } as UpdateCredentialDto);
      toast.success(`Credential "${cred.name}" ${cred.isActive ? "deactivated" : "activated"}`);
      load();
    } catch { toast.error("Failed to update credential"); }
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-bold flex items-center gap-2"><KeyRound className="h-6 w-6" /> MCP Credentials</h2>
          <p className="text-sm text-muted-foreground">Manage API keys for MCP tool server authentication</p>
        </div>
        <Button onClick={() => setShowCreate(!showCreate)}>
          <Plus className="h-4 w-4 mr-1" />{showCreate ? "Cancel" : "Add Credential"}
        </Button>
      </div>

      {showCreate && (
        <Card>
          <CardHeader><CardTitle>New Credential</CardTitle></CardHeader>
          <CardContent className="grid gap-4">
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label>Name</Label>
                <Input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="e.g. weather-api-prod" />
              </div>
              <div className="space-y-1.5">
                <Label>Auth Scheme</Label>
                <Select value={form.authScheme ?? "Bearer"} onValueChange={(v) => setForm({ ...form, authScheme: v })}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {AUTH_SCHEMES.map((s) => <SelectItem key={s} value={s}>{s}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
            </div>
            <div className="space-y-1.5">
              <Label>API Key</Label>
              <Input type="password" value={form.apiKey} onChange={(e) => setForm({ ...form, apiKey: e.target.value })} placeholder="Enter API key…" autoComplete="new-password" />
            </div>
            {form.authScheme === "Custom" && (
              <div className="space-y-1.5">
                <Label>Custom Header Name</Label>
                <Input value={form.customHeaderName ?? ""} onChange={(e) => setForm({ ...form, customHeaderName: e.target.value })} placeholder="e.g. X-Service-Key" />
              </div>
            )}
            <div className="space-y-1.5">
              <Label>Description</Label>
              <Input value={form.description ?? ""} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder="Optional description" />
            </div>
            <Button onClick={handleCreate}><Save className="h-4 w-4 mr-1" /> Create</Button>
          </CardContent>
        </Card>
      )}

      {loading ? (
        <div className="space-y-3">{[1,2,3].map(i => <Skeleton key={i} className="h-16 w-full" />)}</div>
      ) : credentials.length === 0 ? (
        <Card><CardContent className="py-8 text-center text-muted-foreground">No credentials configured. Click "Add Credential" to create one.</CardContent></Card>
      ) : (
        <div className="space-y-3">
          {credentials.map((c) => (
            <Card key={c.id}>
              <CardContent className="flex items-center justify-between py-4">
                <div className="space-y-1">
                  <div className="flex items-center gap-2">
                    <span className="font-medium">{c.name}</span>
                    <Badge variant={c.isActive ? "default" : "secondary"}>{c.isActive ? "Active" : "Inactive"}</Badge>
                    <Badge variant="outline">{c.authScheme}</Badge>
                    {c.customHeaderName && <Badge variant="outline">{c.customHeaderName}</Badge>}
                    {c.apiKeyHint && (
                      <Badge variant="outline" className="font-mono" title="Last 4 characters of the stored key">
                        {c.apiKeyHint}
                      </Badge>
                    )}
                  </div>
                  <div className="text-xs text-muted-foreground">
                    {c.description && <span>{c.description} · </span>}
                    Created {new Date(c.createdAt).toLocaleDateString()}
                    {c.expiresAt && <span> · Expires {new Date(c.expiresAt).toLocaleDateString()}</span>}
                    {c.lastUsedAt && <span> · Last used {new Date(c.lastUsedAt).toLocaleDateString()}</span>}
                  </div>
                </div>
                <div className="flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => handleToggleActive(c)}>
                    {c.isActive ? "Deactivate" : "Activate"}
                  </Button>
                  <Button variant="destructive" size="sm" onClick={() => handleDelete(c.id, c.name)}>
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>
              </CardContent>
            </Card>
          ))}
        </div>
      )}
    </div>
  );
}
