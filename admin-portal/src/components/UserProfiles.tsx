import { useState, useEffect } from "react";
import { api, type UserProfile, type UpdateUserProfileDto } from "@/api";
import { auth } from "@/lib/auth";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Card, CardContent } from "@/components/ui/card";
import { Sheet, SheetContent, SheetHeader, SheetTitle, SheetFooter } from "@/components/ui/sheet";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Avatar, AvatarFallback, AvatarImage } from "@/components/ui/avatar";
import { Textarea } from "@/components/ui/textarea";
import { Users, Search } from "lucide-react";
import { toast } from "sonner";

function initials(name: string) {
  return name.split(" ").map(p => p[0]).join("").toUpperCase().slice(0, 2);
}

export function UserProfiles() {
  // Re-read per render so master admin navigating between tenants gets the right scope
  const tenantId = auth.getTenantId() || 1;
  const [profiles, setProfiles] = useState<UserProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [editing, setEditing] = useState<UserProfile | null>(null);
  const [form, setForm] = useState<UpdateUserProfileDto>({ displayName: "", avatarUrl: "", agentAccessOverrides: [], metadataJson: "" });
  const [saving, setSaving] = useState(false);

  async function load() {
    try {
      setProfiles(await api.listUserProfiles(tenantId, search || undefined));
    } catch (e) {
      toast.error(`Failed to load profiles: ${e}`);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, []);
  useEffect(() => {
    const t = setTimeout(() => load(), 300);
    return () => clearTimeout(t);
  }, [search]);

  function openEdit(p: UserProfile) {
    setEditing(p);
    setForm({
      displayName: p.displayName,
      avatarUrl: p.avatarUrl ?? "",
      agentAccessOverrides: p.agentAccessOverrides,
      metadataJson: p.metadataJson ?? "",
    });
  }

  async function save() {
    if (!editing) return;
    setSaving(true);
    try {
      await api.updateUserProfile(editing.id, {
        ...form,
        avatarUrl: form.avatarUrl || undefined,
        metadataJson: form.metadataJson || undefined,
      }, tenantId);
      toast.success("Profile updated");
      setEditing(null);
      load();
    } catch (e) {
      toast.error(`Save failed: ${e}`);
    } finally {
      setSaving(false);
    }
  }

  async function toggleActive(p: UserProfile) {
    try {
      if (p.isActive) await api.disableUser(p.id, tenantId);
      else await api.enableUser(p.id, tenantId);
      load();
    } catch (e) {
      toast.error(`Update failed: ${e}`);
    }
  }

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Users className="size-5" />
          <h1 className="text-2xl font-semibold">User Profiles</h1>
        </div>
        <div className="relative">
          <Search className="absolute left-2.5 top-2.5 size-4 text-muted-foreground" />
          <Input
            className="pl-8 w-64"
            placeholder="Search by name or email…"
            value={search}
            onChange={e => setSearch(e.target.value)}
          />
        </div>
      </div>

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>User</TableHead>
                <TableHead>Roles</TableHead>
                <TableHead>Agent Access</TableHead>
                <TableHead>Last Login</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="w-24">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading ? (
                <TableRow><TableCell colSpan={6} className="text-center text-muted-foreground py-8">Loading…</TableCell></TableRow>
              ) : profiles.length === 0 ? (
                <TableRow><TableCell colSpan={6} className="text-center text-muted-foreground py-8">No users have logged in yet.</TableCell></TableRow>
              ) : profiles.map(p => (
                <TableRow key={p.id}>
                  <TableCell>
                    <div className="flex items-center gap-2">
                      <Avatar className="size-8">
                        <AvatarImage src={p.avatarUrl} />
                        <AvatarFallback className="text-xs">{initials(p.displayName || p.email)}</AvatarFallback>
                      </Avatar>
                      <div>
                        <div className="font-medium text-sm">{p.displayName || p.email}</div>
                        <div className="text-xs text-muted-foreground">{p.email}</div>
                      </div>
                    </div>
                  </TableCell>
                  <TableCell>
                    <div className="flex flex-wrap gap-1">
                      {p.roles.map(r => <Badge key={r} variant="outline" className="text-xs">{r}</Badge>)}
                      {p.roles.length === 0 && <span className="text-xs text-muted-foreground">–</span>}
                    </div>
                  </TableCell>
                  <TableCell>
                    <div className="flex flex-wrap gap-1">
                      {(p.agentAccessOverrides.length > 0 ? p.agentAccessOverrides : p.agentAccess).map(a =>
                        <Badge key={a} variant="secondary" className="text-xs">{a}</Badge>
                      )}
                      {p.agentAccess.length === 0 && p.agentAccessOverrides.length === 0 && <span className="text-xs text-muted-foreground">–</span>}
                    </div>
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">
                    {new Date(p.lastLoginAt).toLocaleDateString()}
                  </TableCell>
                  <TableCell>
                    <Badge variant={p.isActive ? "default" : "destructive"}>{p.isActive ? "Active" : "Disabled"}</Badge>
                  </TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      <Button size="sm" variant="ghost" onClick={() => openEdit(p)}>Edit</Button>
                      <Switch
                        checked={p.isActive}
                        onCheckedChange={() => toggleActive(p)}
                        aria-label={p.isActive ? "Disable user" : "Enable user"}
                      />
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      {/* Edit drawer */}
      <Sheet open={!!editing} onOpenChange={open => !open && setEditing(null)}>
        <SheetContent className="w-full sm:max-w-[480px] overflow-y-auto">
          <SheetHeader>
            <SheetTitle>Edit User Profile</SheetTitle>
          </SheetHeader>

          {editing && (
            <div className="space-y-4 py-4">
              {/* Read-only info */}
              <div className="rounded-lg bg-muted p-3 text-sm space-y-1">
                <div><span className="text-muted-foreground">User ID:</span> <code className="font-mono text-xs">{editing.userId}</code></div>
                <div><span className="text-muted-foreground">Email:</span> {editing.email}</div>
                <div><span className="text-muted-foreground">Created:</span> {new Date(editing.createdAt).toLocaleDateString()}</div>
                <div><span className="text-muted-foreground">Last Login:</span> {new Date(editing.lastLoginAt).toLocaleString()}</div>
              </div>

              <div className="space-y-1">
                <Label>Display Name</Label>
                <Input value={form.displayName} onChange={e => setForm(f => ({ ...f, displayName: e.target.value }))} />
              </div>

              <div className="space-y-1">
                <Label>Avatar URL</Label>
                <Input value={form.avatarUrl} onChange={e => setForm(f => ({ ...f, avatarUrl: e.target.value }))} />
              </div>

              <div className="space-y-2">
                <Label>Roles <span className="text-muted-foreground text-xs">(from SSO — read-only)</span></Label>
                <div className="flex flex-wrap gap-1">
                  {editing.roles.map(r => <Badge key={r} variant="outline">{r}</Badge>)}
                  {editing.roles.length === 0 && <span className="text-sm text-muted-foreground">None</span>}
                </div>
              </div>

              <div className="space-y-2">
                <Label>Agent Access <span className="text-muted-foreground text-xs">(from SSO — read-only)</span></Label>
                <div className="flex flex-wrap gap-1">
                  {editing.agentAccess.map(a => <Badge key={a} variant="secondary">{a}</Badge>)}
                  {editing.agentAccess.length === 0 && <span className="text-sm text-muted-foreground">None</span>}
                </div>
              </div>

              <div className="space-y-1">
                <Label>Agent Access Overrides <span className="text-muted-foreground text-xs">(comma-separated, overrides SSO grants when non-empty)</span></Label>
                <Input
                  value={form.agentAccessOverrides.join(", ")}
                  onChange={e => setForm(f => ({
                    ...f,
                    agentAccessOverrides: e.target.value.split(",").map(s => s.trim()).filter(Boolean)
                  }))}
                  placeholder="* or agent-type-1, agent-type-2"
                />
              </div>

              <div className="space-y-1">
                <Label>Metadata JSON <span className="text-muted-foreground text-xs">(freeform key-value attributes)</span></Label>
                <Textarea
                  value={form.metadataJson}
                  onChange={e => setForm(f => ({ ...f, metadataJson: e.target.value }))}
                  className="font-mono text-xs"
                  rows={3}
                  placeholder='{"department": "engineering"}'
                />
              </div>

              <div className="flex items-center gap-2 pt-2">
                <Switch
                  checked={editing.isActive}
                  onCheckedChange={() => { toggleActive(editing); setEditing(null); }}
                />
                <Label>{editing.isActive ? "Account active" : "Account disabled"}</Label>
              </div>
            </div>
          )}

          <SheetFooter className="mt-4">
            <Button variant="outline" onClick={() => setEditing(null)}>Cancel</Button>
            <Button onClick={save} disabled={saving}>{saving ? "Saving…" : "Save"}</Button>
          </SheetFooter>
        </SheetContent>
      </Sheet>
    </div>
  );
}
