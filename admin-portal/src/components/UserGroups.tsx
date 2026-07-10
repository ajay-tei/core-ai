import { useState, useEffect, useCallback, useMemo } from "react";
import {
  api,
  type UserGroup,
  type UserGroupRequest,
  type UserProfile,
} from "@/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Plus, Trash2, Pencil, Users, X, Search, Check } from "lucide-react";
import { toast } from "sonner";

const EMPTY: UserGroupRequest = {
  name: "",
  description: "",
  members: [],
  roles: [],
};

interface UserPickerProps {
  users: UserProfile[];
  selected: string[];   // selected userIds
  onToggle: (userId: string) => void;
  onClear: () => void;
}

/** Checkable list of tenant user profiles for selecting explicit group members. */
function UserPicker({ users, selected, onToggle, onClear }: UserPickerProps) {
  const [query, setQuery] = useState("");
  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return users;
    return users.filter(
      (u) =>
        (u.displayName?.toLowerCase().includes(q) ?? false) ||
        (u.email?.toLowerCase().includes(q) ?? false) ||
        u.userId.toLowerCase().includes(q),
    );
  }, [users, query]);
  const selectedSet = useMemo(() => new Set(selected), [selected]);

  if (users.length === 0) {
    return <span className="text-xs text-muted-foreground">No user profiles available.</span>;
  }

  return (
    <div className="rounded-md border">
      <div className="flex items-center gap-2 border-b p-2">
        <div className="relative flex-1">
          <Search className="absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search users by name or email…"
            className="h-8 pl-7"
          />
        </div>
        <span className="whitespace-nowrap text-xs text-muted-foreground">{selected.length} selected</span>
      </div>
      <div className="flex items-center gap-2 border-b px-2 py-1.5 text-xs">
        <button type="button" className="text-primary hover:underline disabled:opacity-50" disabled={selected.length === 0} onClick={onClear}>
          Clear
        </button>
      </div>
      <ScrollArea className="h-52">
        <div className="p-1">
          {filtered.length === 0 ? (
            <div className="px-2 py-6 text-center text-xs text-muted-foreground">No matches.</div>
          ) : (
            filtered.map((u) => {
              const isSelected = selectedSet.has(u.userId);
              // Prefer the friendly name; fall back to email, then the raw id.
              const label = u.displayName || u.email || u.userId;
              // Show the email as a secondary hint whenever it exists and differs from the label.
              const secondary = u.email && u.email !== label ? u.email : null;
              return (
                <button
                  key={u.userId}
                  type="button"
                  onClick={() => onToggle(u.userId)}
                  className="flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm hover:bg-accent"
                >
                  <span
                    className={`flex h-4 w-4 shrink-0 items-center justify-center rounded border ${
                      isSelected ? "border-primary bg-primary text-primary-foreground" : "border-input"
                    }`}
                  >
                    {isSelected && <Check className="h-3 w-3" />}
                  </span>
                  <span className="min-w-0 flex-1 truncate">{label}</span>
                  {secondary && (
                    <span className="max-w-[45%] truncate text-xs text-muted-foreground">{secondary}</span>
                  )}
                </button>
              );
            })
          )}
        </div>
      </ScrollArea>
    </div>
  );
}

export function UserGroups() {
  const [groups, setGroups] = useState<UserGroup[]>([]);
  const [users, setUsers] = useState<UserProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [editingId, setEditingId] = useState<number | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState<UserGroupRequest>(EMPTY);
  const [roleInput, setRoleInput] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [g, u] = await Promise.all([
        api.listUserGroups(),
        api.listUserProfiles().catch(() => []),
      ]);
      setGroups(g);
      setUsers(u);
    } catch {
      toast.error("Failed to load user groups");
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  const openCreate = () => {
    setEditingId(null);
    setForm(EMPTY);
    setRoleInput("");
    setShowForm(true);
  };

  const openEdit = (g: UserGroup) => {
    setEditingId(g.id);
    setForm({
      name: g.name,
      description: g.description ?? "",
      members: g.members,
      roles: g.roles,
    });
    setRoleInput("");
    setShowForm(true);
  };

  const memberIds = useMemo(() => (form.members ?? []).map((m) => m.userId), [form.members]);

  const toggleMember = (userId: string) => {
    const current = form.members ?? [];
    if (current.some((m) => m.userId === userId)) {
      setForm({ ...form, members: current.filter((m) => m.userId !== userId) });
    } else {
      const u = users.find((x) => x.userId === userId);
      setForm({ ...form, members: [...current, { userId, email: u?.email }] });
    }
  };

  const clearMembers = () => setForm({ ...form, members: [] });

  const addRole = () => {
    const r = roleInput.trim();
    if (!r) return;
    if (!(form.roles ?? []).includes(r)) {
      setForm({ ...form, roles: [...(form.roles ?? []), r] });
    }
    setRoleInput("");
  };

  const removeRole = (r: string) =>
    setForm({ ...form, roles: (form.roles ?? []).filter((x) => x !== r) });

  const handleSave = async () => {
    if (!form.name.trim()) { toast.error("Name is required"); return; }
    try {
      if (editingId) {
        await api.updateUserGroup(editingId, form);
        toast.success("User group updated");
      } else {
        await api.createUserGroup(form);
        toast.success("User group created");
      }
      setShowForm(false);
      setEditingId(null);
      load();
    } catch {
      toast.error("Failed to save user group");
    }
  };

  const handleDelete = async (g: UserGroup) => {
    if (!confirm(`Delete user group "${g.name}"? Any agent-access or MCP credential grants to this group are removed.`)) return;
    try {
      await api.deleteUserGroup(g.id);
      toast.success(`User group "${g.name}" deleted`);
      load();
    } catch {
      toast.error("Failed to delete user group");
    }
  };

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-bold flex items-center gap-2"><Users className="h-6 w-6" /> User Groups</h2>
          <p className="text-sm text-muted-foreground">
            Group users so they can be granted agent access and shared MCP credentials together. A user may belong to many groups.
          </p>
        </div>
        <Button onClick={() => (showForm && editingId === null ? setShowForm(false) : openCreate())}>
          <Plus className="h-4 w-4 mr-1" />{showForm && editingId === null ? "Cancel" : "New Group"}
        </Button>
      </div>

      {showForm && (
        <Card>
          <CardHeader><CardTitle>{editingId ? "Edit User Group" : "New User Group"}</CardTitle></CardHeader>
          <CardContent className="grid gap-4">
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label>Name</Label>
                <Input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="e.g. Finance Team" />
              </div>
              <div className="space-y-1.5">
                <Label>Description</Label>
                <Input value={form.description ?? ""} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder="Optional" />
              </div>
            </div>

            <div className="space-y-1.5">
              <Label>Members</Label>
              <UserPicker users={users} selected={memberIds} onToggle={toggleMember} onClear={clearMembers} />
            </div>

            <div className="space-y-1.5">
              <Label>Auto-include Roles / SSO Groups</Label>
              <p className="text-xs text-muted-foreground">Users carrying any of these roles or SSO groups are treated as members automatically.</p>
              <div className="flex gap-2">
                <Input
                  value={roleInput}
                  onChange={(e) => setRoleInput(e.target.value)}
                  onKeyDown={(e) => { if (e.key === "Enter") { e.preventDefault(); addRole(); } }}
                  placeholder="Type a role or SSO group name and press Enter"
                />
                <Button type="button" variant="outline" onClick={addRole}>Add</Button>
              </div>
              <div className="flex flex-wrap gap-2 pt-1">
                {(form.roles ?? []).map((r) => (
                  <Badge key={r} variant="secondary" className="gap-1">
                    {r}
                    <X className="h-3 w-3 cursor-pointer" onClick={() => removeRole(r)} />
                  </Badge>
                ))}
              </div>
            </div>

            <div className="flex gap-2">
              <Button onClick={handleSave}>{editingId ? "Save Changes" : "Create Group"}</Button>
              <Button variant="outline" onClick={() => { setShowForm(false); setEditingId(null); }}>Cancel</Button>
            </div>
          </CardContent>
        </Card>
      )}

      {loading ? (
        <div className="space-y-3">{[1, 2, 3].map((i) => <Skeleton key={i} className="h-20 w-full" />)}</div>
      ) : groups.length === 0 ? (
        <Card><CardContent className="py-8 text-center text-muted-foreground">No user groups yet. Click "New Group" to create one.</CardContent></Card>
      ) : (
        <div className="space-y-3">
          {groups.map((g) => (
            <Card key={g.id}>
              <CardContent className="flex items-start justify-between py-4">
                <div className="space-y-1.5">
                  <span className="font-medium">{g.name}</span>
                  {g.description && <p className="text-sm text-muted-foreground">{g.description}</p>}
                  <div className="text-xs text-muted-foreground">
                    {g.members.length} member(s)
                    {g.roles.length > 0 && <span> · auto-include roles: {g.roles.join(", ")}</span>}
                  </div>
                </div>
                <div className="flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => openEdit(g)}><Pencil className="h-4 w-4" /></Button>
                  <Button variant="destructive" size="sm" onClick={() => handleDelete(g)}><Trash2 className="h-4 w-4" /></Button>
                </div>
              </CardContent>
            </Card>
          ))}
        </div>
      )}
    </div>
  );
}
