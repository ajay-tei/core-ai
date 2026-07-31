import { useEffect, useState } from "react";
import { api, type TenantEnvironment, type EnvironmentRequest } from "@/api";
import { useEnvironment } from "@/hooks/useEnvironment";
import { EnvironmentBadge } from "@/components/ui/environment-badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Plus, Trash2, Pencil, Save, X, Layers } from "lucide-react";
import { toast } from "sonner";

const EMPTY_FORM: EnvironmentRequest = { slug: "", displayName: "", rank: 0, isDefault: false, clientGroup: "" };

/** Groups environments by ClientGroup (shared/untagged tier first, then one section per client,
 *  alphabetical), each internally sorted by Rank — keeps the list scannable as clients are added. */
function groupEnvironments(environments: TenantEnvironment[]): [string, TenantEnvironment[]][]
{
  const byGroup = new Map<string, TenantEnvironment[]>();
  for (const env of environments)
  {
    const key = env.clientGroup?.trim() || "Shared";
    if (!byGroup.has(key)) byGroup.set(key, []);
    byGroup.get(key)!.push(env);
  }
  const entries = [...byGroup.entries()].map(([k, v]) => [k, [...v].sort((a, b) => a.rank - b.rank)] as [string, TenantEnvironment[]]);
  entries.sort(([a], [b]) => (a === "Shared" ? -1 : b === "Shared" ? 1 : a.localeCompare(b)));
  return entries;
}

export function EnvironmentManager()
{
  const { reload: reloadSwitcher } = useEnvironment();
  const [environments, setEnvironments] = useState<TenantEnvironment[]>([]);
  const [loading, setLoading] = useState(true);
  const [showForm, setShowForm] = useState(false);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [form, setForm] = useState<EnvironmentRequest>(EMPTY_FORM);

  const load = () =>
  {
    setLoading(true);
    api.listEnvironments()
      .then((envs) => setEnvironments([...envs].sort((a, b) => a.rank - b.rank)))
      .catch(() => toast.error("Failed to load environments"))
      .finally(() => setLoading(false));
  };

  useEffect(load, []);

  const openCreate = () =>
  {
    // Suggest the next rank above the current highest, so new environments default to
    // "above Production" rather than colliding with rank 0.
    const nextRank = environments.length > 0 ? Math.max(...environments.map((e) => e.rank)) + 1 : 0;
    setForm({ ...EMPTY_FORM, rank: nextRank });
    setEditingId(null);
    setShowForm(true);
  };

  const openEdit = (env: TenantEnvironment) =>
  {
    setForm({ slug: env.slug, displayName: env.displayName, rank: env.rank, isDefault: env.isDefault, clientGroup: env.clientGroup ?? "" });
    setEditingId(env.id);
    setShowForm(true);
  };

  const closeForm = () =>
  {
    setShowForm(false);
    setEditingId(null);
    setForm(EMPTY_FORM);
  };

  const handleSave = async () =>
  {
    if (!form.slug.trim() || !form.displayName.trim())
    {
      toast.error("Slug and Display Name are required");
      return;
    }
    try
    {
      if (editingId !== null)
      {
        await api.updateEnvironment(editingId, form);
        toast.success(`Environment "${form.displayName}" updated`);
      }
      else
      {
        await api.createEnvironment(form);
        toast.success(`Environment "${form.displayName}" created`);
      }
      closeForm();
      load();
      reloadSwitcher();
    }
    catch (e)
    {
      toast.error(e instanceof Error ? e.message : "Failed to save environment");
    }
  };

  const handleDelete = async (env: TenantEnvironment) =>
  {
    if (!confirm(`Delete environment "${env.displayName}"? Agents/MCP servers/schedules/groups already promoted into it are NOT deleted, but this environment can no longer be selected or promoted into.`)) return;
    try
    {
      await api.deleteEnvironment(env.id);
      toast.success(`Environment "${env.displayName}" deleted`);
      load();
      reloadSwitcher();
    }
    catch (e)
    {
      toast.error(e instanceof Error ? e.message : "Failed to delete environment");
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-bold flex items-center gap-2"><Layers className="h-6 w-6" /> Environments</h2>
          <p className="text-sm text-muted-foreground">
            Define your promotion pipeline (e.g. Dev → Staging → Production). Rank controls promotion
            order — objects can only be promoted to a strictly higher-ranked environment.
          </p>
        </div>
        <Button onClick={() => (showForm ? closeForm() : openCreate())}>
          {showForm ? <><X className="h-4 w-4 mr-1" /> Cancel</> : <><Plus className="h-4 w-4 mr-1" /> Add Environment</>}
        </Button>
      </div>

      {showForm && (
        <Card>
          <CardHeader><CardTitle>{editingId !== null ? "Edit Environment" : "New Environment"}</CardTitle></CardHeader>
          <CardContent className="grid gap-4">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label>Display Name</Label>
                <Input
                  value={form.displayName}
                  onChange={(e) => setForm({ ...form, displayName: e.target.value })}
                  placeholder="e.g. Staging"
                />
              </div>
              <div className="space-y-1.5">
                <Label>Slug</Label>
                <Input
                  value={form.slug}
                  onChange={(e) => setForm({ ...form, slug: e.target.value })}
                  placeholder="e.g. staging"
                />
              </div>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label>Rank</Label>
                <Input
                  type="number"
                  value={form.rank}
                  onChange={(e) => setForm({ ...form, rank: Number(e.target.value) })}
                />
                <p className="text-xs text-muted-foreground">Higher rank = later in the pipeline (promotion targets must have a strictly higher rank).</p>
              </div>
              <div className="flex items-center gap-2 pt-6">
                <Switch checked={form.isDefault} onCheckedChange={(v) => setForm({ ...form, isDefault: v })} />
                <Label>Default (untagged/legacy traffic resolves here)</Label>
              </div>
            </div>
            <div className="space-y-1.5">
              <Label>Client (optional)</Label>
              <Input
                value={form.clientGroup ?? ""}
                onChange={(e) => setForm({ ...form, clientGroup: e.target.value })}
                placeholder="e.g. Acme — leave blank for a shared tier like Dev/QA"
              />
              <p className="text-xs text-muted-foreground">
                Tag this environment to one client when you have several client-specific environment
                pairs (e.g. "Acme-Play"/"Acme-Live", "Globex-Play"/"Globex-Live") fanning out from a
                shared Dev/QA tier. Two environments tagged to different clients can never be promoted
                into each other directly.
              </p>
            </div>
            <div className="flex gap-2">
              <Button onClick={handleSave}><Save className="h-4 w-4 mr-1" /> {editingId !== null ? "Save" : "Create"}</Button>
              <Button variant="outline" onClick={closeForm}>Cancel</Button>
            </div>
          </CardContent>
        </Card>
      )}

      {loading ? (
        <div className="space-y-3">{[1, 2, 3].map((i) => <Skeleton key={i} className="h-16 w-full" />)}</div>
      ) : environments.length === 0 ? (
        <Card><CardContent className="py-8 text-center text-muted-foreground">No environments configured yet. Click "Add Environment" to create one.</CardContent></Card>
      ) : (
        <div className="space-y-6">
          {groupEnvironments(environments).map(([groupName, envs]) => (
            <div key={groupName} className="space-y-3">
              <h3 className="text-sm font-medium text-muted-foreground">{groupName}</h3>
              {envs.map((env) => (
                <Card key={env.id}>
                  <CardContent className="flex items-center justify-between py-4">
                    <div className="flex items-center gap-3">
                      <EnvironmentBadge environment={env} allEnvironments={environments} />
                      <div>
                        <div className="font-medium">{env.displayName}</div>
                        <div className="text-xs text-muted-foreground">slug: {env.slug} · rank: {env.rank}{env.isDefault && " · default"}</div>
                      </div>
                    </div>
                    <div className="flex gap-1">
                      <Button variant="ghost" size="icon" onClick={() => openEdit(env)} title="Edit">
                        <Pencil className="h-4 w-4" />
                      </Button>
                      <Button variant="ghost" size="icon" onClick={() => handleDelete(env)} title="Delete">
                        <Trash2 className="h-4 w-4" />
                      </Button>
                    </div>
                  </CardContent>
                </Card>
              ))}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
