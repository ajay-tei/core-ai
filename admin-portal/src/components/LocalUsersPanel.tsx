import { useState } from "react";
import { api, type LocalUser, type LocalUserListParams, type CreateLocalUserDto } from "@/api";
import { usePagedList } from "@/hooks/usePagedList";
import { ListToolbar, ListPagination } from "@/components/ui/list-toolbar";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from "@/components/ui/dialog";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Plus, Trash2, KeyRound } from "lucide-react";
import { toast } from "sonner";

// Note: if `tenantId` needs to change after mount (e.g. TenantDetail.tsx), render this
// component with `key={tenantId}` at the call site to force a clean remount — usePagedList
// only captures its initial params once.
export function LocalUsersPanel({
  tenantId,
  availableRoles = ["admin", "user", "viewer"],
  defaultRoles = ["user"],
}: {
  tenantId: number;
  availableRoles?: string[];
  defaultRoles?: string[];
}) {
  const emptyForm: CreateLocalUserDto = {
    username: "", email: "", password: "changemeonlogin",
    displayName: "", roles: defaultRoles,
  };

  const { result, loading, params, update, updateDebounced, setPage, reload } =
    usePagedList<LocalUser, LocalUserListParams>(api.listLocalUsers, { tenantId, page: 1, pageSize: 25 });
  const [addOpen,     setAddOpen]     = useState(false);
  const [form,        setForm]        = useState<CreateLocalUserDto>(emptyForm);
  const [saving,      setSaving]      = useState(false);
  const [resetUser,   setResetUser]   = useState<LocalUser | null>(null);
  const [newPassword, setNewPassword] = useState("");


  async function addUser() {
    setSaving(true);
    try {
      await api.createLocalUser(form, tenantId);
      toast.success("User created");
      setAddOpen(false);
      setForm(emptyForm);
      reload();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    } finally {
      setSaving(false);
    }
  }

  async function deleteUser(u: LocalUser) {
    if (!confirm(`Delete user "${u.username}"?`)) return;
    try {
      await api.deleteLocalUser(u.id, tenantId);
      toast.success("Deleted");
      reload();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  async function resetPassword() {
    if (!resetUser || !newPassword) return;
    try {
      await api.resetLocalUserPassword(resetUser.id, newPassword, tenantId);
      toast.success("Password reset");
      setResetUser(null);
      setNewPassword("");
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  function toggleRole(role: string) {
    setForm(f => ({
      ...f,
      roles: f.roles.includes(role) ? f.roles.filter(r => r !== role) : [...f.roles, role],
    }));
  }

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => { setForm(emptyForm); setAddOpen(true); }}>
          <Plus className="size-4 mr-2" /> Add User
        </Button>
      </div>

      <ListToolbar
        searchValue={params.search}
        onSearchChange={v => updateDebounced({ search: v || undefined })}
        searchPlaceholder="Search users…"
        pageSize={params.pageSize}
        onPageSizeChange={pageSize => update({ pageSize })}
        pageSizeOptions={[25, 50, 100]}
      />

      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>Username</TableHead>
            <TableHead>Email</TableHead>
            <TableHead>Display Name</TableHead>
            <TableHead>Roles</TableHead>
            <TableHead>Active</TableHead>
            <TableHead>Last Login</TableHead>
            <TableHead className="w-24">Actions</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {loading ? (
            <TableRow><TableCell colSpan={7} className="text-center text-muted-foreground py-8">Loading…</TableCell></TableRow>
          ) : (result?.items.length ?? 0) === 0 ? (
            <TableRow><TableCell colSpan={7} className="text-center text-muted-foreground py-8">No local users for this tenant.</TableCell></TableRow>
          ) : (result?.items ?? []).map(u => (
            <TableRow key={u.id}>
              <TableCell className="font-mono text-sm">{u.username}</TableCell>
              <TableCell className="text-sm">{u.email}</TableCell>
              <TableCell>{u.displayName}</TableCell>
              <TableCell>
                <div className="flex flex-wrap gap-1">
                  {u.roles.map(r => <Badge key={r} variant="secondary">{r}</Badge>)}
                </div>
              </TableCell>
              <TableCell>
                <Switch checked={u.isActive} disabled />
              </TableCell>
              <TableCell className="text-xs text-muted-foreground">
                {u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleDateString() : "—"}
              </TableCell>
              <TableCell>
                <div className="flex gap-1">
                  <Button size="icon" variant="ghost" title="Reset password"
                    onClick={() => { setResetUser(u); setNewPassword(""); }}>
                    <KeyRound className="size-4" />
                  </Button>
                  <Button size="icon" variant="ghost" className="text-destructive"
                    onClick={() => deleteUser(u)}>
                    <Trash2 className="size-4" />
                  </Button>
                </div>
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>

      {result && (
        <ListPagination
          page={result.page}
          totalPages={result.totalPages}
          totalCount={result.totalCount}
          onPageChange={setPage}
          itemLabel="total"
        />
      )}

      {/* Add user dialog */}
      <Dialog open={addOpen} onOpenChange={setAddOpen}>
        <DialogContent>
          <DialogHeader><DialogTitle>Add Local User</DialogTitle></DialogHeader>
          <div className="space-y-3 py-2">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label>Username</Label>
                <Input value={form.username} onChange={e => setForm(f => ({ ...f, username: e.target.value }))} />
              </div>
              <div className="space-y-1">
                <Label>Display Name</Label>
                <Input value={form.displayName} onChange={e => setForm(f => ({ ...f, displayName: e.target.value }))} />
              </div>
            </div>
            <div className="space-y-1">
              <Label>Email</Label>
              <Input type="email" value={form.email} onChange={e => setForm(f => ({ ...f, email: e.target.value }))} />
            </div>
            <div className="space-y-1">
              <Label>Password <span className="text-muted-foreground text-xs">(default: changemeonlogin)</span></Label>
              <Input type="password" placeholder="changemeonlogin"
                value={form.password} onChange={e => setForm(f => ({ ...f, password: e.target.value }))} />
            </div>
            <div className="space-y-1">
              <Label>Roles</Label>
              <div className="flex gap-3">
                {availableRoles.map(r => (
                  <label key={r} className="flex items-center gap-1.5 text-sm cursor-pointer">
                    <input type="checkbox" checked={form.roles.includes(r)} onChange={() => toggleRole(r)} />
                    {r}
                  </label>
                ))}
              </div>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setAddOpen(false)}>Cancel</Button>
            <Button onClick={addUser} disabled={saving}>{saving ? "Creating…" : "Create"}</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Reset password dialog */}
      <Dialog open={!!resetUser} onOpenChange={v => { if (!v) setResetUser(null); }}>
        <DialogContent>
          <DialogHeader><DialogTitle>Reset Password — {resetUser?.username}</DialogTitle></DialogHeader>
          <div className="space-y-3 py-2">
            <div className="space-y-1">
              <Label>New Password</Label>
              <Input type="password" value={newPassword}
                onChange={e => setNewPassword(e.target.value)}
                placeholder="Enter new password" />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setResetUser(null)}>Cancel</Button>
            <Button onClick={resetPassword} disabled={!newPassword}>Reset Password</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
