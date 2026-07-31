import { useState, useEffect } from "react";
import { useParams, useNavigate } from "react-router";
import { api, type TenantLlmConfig, type AvailableLlmConfig, type UpsertLlmConfigDto, type CreateNamedLlmConfigDto, type TenantEnvironment } from "@/api";
import { SsoConfig } from "@/components/SsoConfig";
import { LocalUsersPanel } from "@/components/LocalUsersPanel";
import { LlmForm } from "@/components/PlatformLlmConfig";
import { EnvironmentBadge } from "@/components/ui/environment-badge";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { ArrowLeft, Building2, Plus, Trash2 } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { toast } from "sonner";

type TenantInfo = {
  id: number;
  name: string;
  isActive: boolean;
  liteLLMTeamId?: string;
  sites: { id: number; name: string; isActive: boolean }[];
};

export function TenantDetail() {
  const { id }     = useParams<{ id: string }>();
  const navigate   = useNavigate();
  const tenantId   = Number(id);

  const [tenant,  setTenant]  = useState<TenantInfo | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.getTenant(tenantId)
      .then(setTenant)
      .catch(() => toast.error("Failed to load tenant"))
      .finally(() => setLoading(false));
  }, [tenantId]);

  if (loading) {
    return <div className="p-6 text-muted-foreground">Loading…</div>;
  }

  if (!tenant) {
    return (
      <div className="p-6 space-y-4">
        <p className="text-destructive">Tenant not found.</p>
        <Button variant="outline" onClick={() => navigate("/platform/tenants")}>
          <ArrowLeft className="size-4 mr-2" /> Back to Tenants
        </Button>
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6">
      {/* Header */}
      <div className="space-y-2">
        <Button variant="ghost" size="sm" className="-ml-2 text-muted-foreground"
          onClick={() => navigate("/platform/tenants")}>
          <ArrowLeft className="size-4 mr-1" /> Tenants
        </Button>
        <div className="flex items-center gap-3">
          <Building2 className="size-6 text-muted-foreground" />
          <h1 className="text-2xl font-semibold">{tenant.name}</h1>
          <Badge variant={tenant.isActive ? "default" : "secondary"}>
            {tenant.isActive ? "Active" : "Inactive"}
          </Badge>
          <span className="text-xs text-muted-foreground ml-auto">Tenant ID: {tenant.id}</span>
        </div>
        {tenant.sites.length > 0 && (
          <div className="flex gap-2 text-xs text-muted-foreground">
            Sites: {tenant.sites.map(s => s.name).join(", ")}
          </div>
        )}
      </div>

      {/* Tabs */}
      <Tabs defaultValue="sso">
        <TabsList>
          <TabsTrigger value="sso">SSO Configuration</TabsTrigger>
          <TabsTrigger value="users">Local Users</TabsTrigger>
          <TabsTrigger value="llm">LLM Config</TabsTrigger>
        </TabsList>

        <TabsContent value="sso" className="mt-4">
          <SsoConfig key={tenantId} tenantId={tenantId} />
        </TabsContent>

        <TabsContent value="users" className="mt-4">
          <LocalUsersPanel key={tenantId} tenantId={tenantId} />
        </TabsContent>

        <TabsContent value="llm" className="mt-4">
          <TenantLlmConfigPanel tenantId={tenantId} />
        </TabsContent>
      </Tabs>
    </div>
  );
}

// ── Tenant LLM Config Panel ────────────────────────────────────────────────────

function TenantLlmConfigPanel({ tenantId }: { tenantId: number }) {
  const [environments, setEnvironments] = useState<TenantEnvironment[]>([]);
  const [groupConfigs, setGroupConfigs] = useState<AvailableLlmConfig[]>([]);
  const [ownConfigs,   setOwnConfigs]   = useState<TenantLlmConfig[]>([]);
  const [loading,      setLoading]      = useState(true);

  // New own config form
  const [showAdd,      setShowAdd]      = useState(false);
  const [newName,      setNewName]      = useState("");
  const [newForm,      setNewForm]      = useState<UpsertLlmConfigDto>({});
  const [addingSaving, setAddingSaving] = useState(false);

  async function load() {
    try {
      const [available, own, envs] = await Promise.all([
        api.listAvailableLlmConfigs(tenantId),
        api.listTenantLlmConfigs(tenantId),
        api.listEnvironments(tenantId),
      ]);
      setGroupConfigs(available.filter(c => c.source.startsWith("group:")));
      setOwnConfigs(own.filter(c => c.name));   // only named tenant configs
      setEnvironments(envs);
    } catch (e) {
      toast.error(`Failed to load LLM config: ${e}`);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, [tenantId]);

  async function addOwnConfig() {
    if (!newName.trim()) return;
    setAddingSaving(true);
    try {
      const dto: CreateNamedLlmConfigDto = {
        name:               newName.trim(),
        provider:           newForm.provider           || undefined,
        apiKey:             newForm.apiKey             || undefined,
        model:              newForm.model              || undefined,
        endpoint:           newForm.endpoint           || undefined,
        deploymentName:     newForm.deploymentName     || undefined,
        availableModelsJson: newForm.availableModelsJson || undefined,
        environmentId:      newForm.environmentId,
      };
      const created = await api.createTenantLlmConfig(dto, tenantId);
      setOwnConfigs(l => [...l, created]);
      setNewName("");
      setNewForm({});
      setShowAdd(false);
      toast.success(`"${created.name}" created`);
    } catch (e) {
      toast.error(`Failed: ${e}`);
    } finally {
      setAddingSaving(false);
    }
  }

  async function deleteOwnConfig(id: number, name?: string) {
    if (!confirm(`Delete config "${name}"?`)) return;
    try {
      await api.deleteTenantLlmConfigById(id, tenantId);
      setOwnConfigs(l => l.filter(x => x.id !== id));
      toast.success("Config deleted");
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  if (loading) return <div className="py-8 text-center text-muted-foreground">Loading…</div>;

  return (
    <div className="space-y-8 max-w-2xl">

      {/* ── Section 1: Group-inherited configs (read-only) ── */}
      <div className="space-y-3">
        <div>
          <h4 className="text-sm font-medium">Available from Group</h4>
          <p className="text-xs text-muted-foreground mt-0.5">
            Configs shared by the group. Read-only — managed at the group level. Agents can pin to these.
          </p>
        </div>
        {groupConfigs.length === 0 ? (
          <p className="text-xs text-muted-foreground italic">
            No group memberships or no group configs — contact your platform admin.
          </p>
        ) : (
          groupConfigs.map(c => (
            <div key={c.id} className="flex items-center justify-between rounded border px-3 py-2 bg-muted/30">
              <div>
                <span className="text-sm font-medium">{c.displayName}</span>
                {c.isRef && <Badge variant="secondary" className="ml-2 text-xs">via Platform</Badge>}
                <span className="ml-2 text-xs text-muted-foreground">
                  {[c.provider, c.model].filter(Boolean).join(" · ")}
                </span>
              </div>
              <span className="text-xs text-muted-foreground">ID {c.id}</span>
            </div>
          ))
        )}
      </div>

      {/* ── Section 2: Tenant-owned configs ── */}
      <div className="space-y-3">
        <div className="flex items-center justify-between">
          <div>
            <h4 className="text-sm font-medium">Tenant-owned Configs</h4>
            <p className="text-xs text-muted-foreground mt-0.5">
              Named configs with own credentials that tenant agents can pin to.
            </p>
          </div>
          <Button size="sm" variant="outline" onClick={() => setShowAdd(s => !s)}>
            <Plus className="size-4 mr-1" />Add
          </Button>
        </div>

        {showAdd && (
          <Card className="border-dashed">
            <CardHeader><CardTitle className="text-sm">New Tenant Config</CardTitle></CardHeader>
            <CardContent className="space-y-3">
              <div className="space-y-1">
                <Label className="text-xs">Config Name <span className="text-destructive">*</span></Label>
                <Input
                  className="h-8"
                  placeholder="e.g. OpenAI Production"
                  value={newName}
                  onChange={e => setNewName(e.target.value)}
                />
              </div>
              <LlmForm value={newForm} onChange={p => setNewForm(f => ({ ...f, ...p }))} />
              {environments.length > 0 && (
                <div className="space-y-1">
                  <Label className="text-xs">Environment</Label>
                  <Select
                    value={newForm.environmentId ? String(newForm.environmentId) : "none"}
                    onValueChange={v => setNewForm(f => ({ ...f, environmentId: v === "none" ? undefined : Number(v) }))}
                  >
                    <SelectTrigger className="h-8"><SelectValue /></SelectTrigger>
                    <SelectContent>
                      <SelectItem value="none">All environments (untagged)</SelectItem>
                      {environments.map(e => <SelectItem key={e.id} value={String(e.id)}>{e.displayName}</SelectItem>)}
                    </SelectContent>
                  </Select>
                </div>
              )}
              <div className="flex items-center gap-2">
                <Button size="sm" onClick={addOwnConfig} disabled={addingSaving || !newName.trim()}>
                  {addingSaving ? "Creating…" : "Create"}
                </Button>
                <Button size="sm" variant="ghost" onClick={() => { setShowAdd(false); setNewName(""); setNewForm({}); }}>
                  Cancel
                </Button>
              </div>
            </CardContent>
          </Card>
        )}

        {ownConfigs.length === 0 && !showAdd && (
          <p className="text-xs text-muted-foreground italic">No tenant-owned configs yet.</p>
        )}

        {ownConfigs.map(c => (
          <div key={c.id} className="flex items-center justify-between rounded border px-3 py-2">
            <div className="flex items-center gap-2">
              <span className="text-sm font-medium">{c.name}</span>
              <span className="text-xs text-muted-foreground">
                {[c.provider, c.model].filter(Boolean).join(" · ") || "no credentials set"}
              </span>
              {c.environmentId && (
                <EnvironmentBadge environment={environments.find(e => e.id === c.environmentId)} allEnvironments={environments} />
              )}
            </div>
            <div className="flex items-center gap-1">
              <span className="text-xs text-muted-foreground">ID {c.id}</span>
              <Button size="sm" variant="ghost" className="text-destructive h-7 px-2" onClick={() => deleteOwnConfig(c.id, c.name)}>
                <Trash2 className="size-3" />
              </Button>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
