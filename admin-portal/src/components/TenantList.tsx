import { useState, useEffect } from "react";
import { useNavigate } from "react-router";
import { api, type Tenant, type CreateTenantDto, type UpdateTenantDto } from "@/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Card, CardContent } from "@/components/ui/card";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from "@/components/ui/dialog";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Plus, Pencil, Trash2, Settings2 } from "lucide-react";
import { toast } from "sonner";

const emptyCreate: CreateTenantDto = { name: "", liteLLMTeamId: "", liteLLMTeamKey: "" };

export function TenantList() {
  const navigate = useNavigate();
  const [tenants,   setTenants]   = useState<Tenant[]>([]);
  const [loading,   setLoading]   = useState(true);
  const [createOpen, setCreateOpen] = useState(false);
  const [editTenant, setEditTenant] = useState<Tenant | null>(null);
  const [form,      setForm]      = useState<CreateTenantDto>(emptyCreate);
  const [saving,    setSaving]    = useState(false);

  async function load() {
    try {
      setTenants(await api.listTenants());
    } catch (e) {
      toast.error(`Failed to load tenants: ${e}`);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, []);

  function openCreate() {
    setForm(emptyCreate);
    setCreateOpen(true);
  }

  function openEdit(t: Tenant) {
    setEditTenant(t);
    setForm({ name: t.name, liteLLMTeamId: t.liteLLMTeamId ?? "", liteLLMTeamKey: "" });
  }

  async function create() {
    setSaving(true);
    try {
      await api.createTenant(form);
      toast.success("Tenant created");
      setCreateOpen(false);
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    } finally {
      setSaving(false);
    }
  }

  async function update() {
    if (!editTenant) return;
    setSaving(true);
    try {
      const dto: UpdateTenantDto = {
        name: form.name,
        isActive: editTenant.isActive,
        liteLLMTeamId: form.liteLLMTeamId,
        liteLLMTeamKey: form.liteLLMTeamKey,
      };
      await api.updateTenant(editTenant.id, dto);
      toast.success("Tenant updated");
      setEditTenant(null);
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    } finally {
      setSaving(false);
    }
  }

  async function toggleActive(t: Tenant) {
    try {
      await api.updateTenant(t.id, { name: t.name, isActive: !t.isActive });
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  async function deleteTenant(t: Tenant) {
    if (!confirm(`Delete tenant "${t.name}"? This cannot be undone.`)) return;
    try {
      await api.deleteTenant(t.id);
      toast.success("Deleted");
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  const f = (field: keyof CreateTenantDto, v: string) =>
    setForm(prev => ({ ...prev, [field]: v }));

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold">Tenants</h1>
          <p className="text-sm text-muted-foreground">Manage organizations and their configurations</p>
        </div>
        <Button onClick={openCreate}><Plus className="size-4 mr-2" /> New Tenant</Button>
      </div>

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead className="w-12">ID</TableHead>
                <TableHead>Name</TableHead>
                <TableHead>Sites</TableHead>
                <TableHead>Active</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="w-32">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading ? (
                <TableRow><TableCell colSpan={6} className="text-center text-muted-foreground py-8">Loading…</TableCell></TableRow>
              ) : tenants.length === 0 ? (
                <TableRow><TableCell colSpan={6} className="text-center text-muted-foreground py-8">No tenants yet. Create one to get started.</TableCell></TableRow>
              ) : tenants.map(t => (
                <TableRow key={t.id}>
                  <TableCell className="text-muted-foreground font-mono text-xs">{t.id}</TableCell>
                  <TableCell className="font-medium">{t.name}</TableCell>
                  <TableCell>
                    <Badge variant="outline">{t.siteCount} site{t.siteCount !== 1 ? "s" : ""}</Badge>
                  </TableCell>
                  <TableCell>
                    <Switch checked={t.isActive} onCheckedChange={() => toggleActive(t)} />
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">
                    {new Date(t.createdAt).toLocaleDateString()}
                  </TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      <Button size="icon" variant="ghost" title="Manage SSO & users"
                        onClick={() => navigate(`/platform/tenants/${t.id}`)}>
                        <Settings2 className="size-4" />
                      </Button>
                      <Button size="icon" variant="ghost" onClick={() => openEdit(t)}>
                        <Pencil className="size-4" />
                      </Button>
                      <Button size="icon" variant="ghost" className="text-destructive"
                        onClick={() => deleteTenant(t)}>
                        <Trash2 className="size-4" />
                      </Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      {/* Create dialog */}
      <Dialog open={createOpen} onOpenChange={setCreateOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>New Tenant</DialogTitle></DialogHeader>
          <div className="space-y-3 py-2">
            <div className="space-y-1">
              <Label>Organization Name</Label>
              <Input value={form.name} onChange={e => f("name", e.target.value)} placeholder="Acme Corp" />
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label>LiteLLM Team ID <span className="text-muted-foreground text-xs">(optional)</span></Label>
                <Input value={form.liteLLMTeamId ?? ""} onChange={e => f("liteLLMTeamId", e.target.value)} />
              </div>
              <div className="space-y-1">
                <Label>LiteLLM Team Key <span className="text-muted-foreground text-xs">(optional)</span></Label>
                <Input type="password" value={form.liteLLMTeamKey ?? ""} onChange={e => f("liteLLMTeamKey", e.target.value)} />
              </div>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setCreateOpen(false)}>Cancel</Button>
            <Button onClick={create} disabled={saving || !form.name}>{saving ? "Creating…" : "Create"}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Edit dialog */}
      <Dialog open={!!editTenant} onOpenChange={v => { if (!v) setEditTenant(null); }}>
        <DialogContent>
          <DialogHeader><DialogTitle>Edit Tenant — {editTenant?.name}</DialogTitle></DialogHeader>
          <div className="space-y-3 py-2">
            <div className="space-y-1">
              <Label>Organization Name</Label>
              <Input value={form.name} onChange={e => f("name", e.target.value)} placeholder="Acme Corp" />
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label>LiteLLM Team ID <span className="text-muted-foreground text-xs">(optional)</span></Label>
                <Input value={form.liteLLMTeamId ?? ""} onChange={e => f("liteLLMTeamId", e.target.value)} />
              </div>
              <div className="space-y-1">
                <Label>LiteLLM Team Key <span className="text-muted-foreground text-xs">(optional)</span></Label>
                <Input type="password" value={form.liteLLMTeamKey ?? ""} onChange={e => f("liteLLMTeamKey", e.target.value)} />
              </div>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setEditTenant(null)}>Cancel</Button>
            <Button onClick={update} disabled={saving || !form.name}>{saving ? "Saving…" : "Save"}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
