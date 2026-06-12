import { useState, useEffect, useCallback, useMemo } from "react";
import {
  api,
  type AgentGroup,
  type AgentGroupRequest,
  type AgentSummary,
  type UserProfile,
} from "@/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Plus, Trash2, Pencil, ShieldCheck, X, Search, Check } from "lucide-react";
import { toast } from "sonner";

const EMPTY: AgentGroupRequest = {
  name: "",
  description: "",
  agentIds: [],
  allowedUserIds: [],
  allowedRoles: [],
};

interface PickerOption {
  value: string;
  primary: string;
  secondary?: string;
}

interface CheckableListProps {
  options: PickerOption[];
  selected: string[];
  onToggle: (value: string) => void;
  onSelectAll: (values: string[]) => void;
  onClear: () => void;
  searchPlaceholder: string;
  emptyText: string;
}

function CheckableList({
  options,
  selected,
  onToggle,
  onSelectAll,
  onClear,
  searchPlaceholder,
  emptyText,
}: CheckableListProps) {
  const [query, setQuery] = useState("");

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return options;
    return options.filter(
      (o) =>
        o.primary.toLowerCase().includes(q) ||
        (o.secondary?.toLowerCase().includes(q) ?? false) ||
        o.value.toLowerCase().includes(q),
    );
  }, [options, query]);

  const selectedSet = useMemo(() => new Set(selected), [selected]);

  if (options.length === 0) {
    return <span className="text-xs text-muted-foreground">{emptyText}</span>;
  }

  return (
    <div className="rounded-md border">
      <div className="flex items-center gap-2 border-b p-2">
        <div className="relative flex-1">
          <Search className="absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={searchPlaceholder}
            className="h-8 pl-7"
          />
        </div>
        <span className="whitespace-nowrap text-xs text-muted-foreground">{selected.length} selected</span>
      </div>
      <div className="flex items-center gap-2 border-b px-2 py-1.5 text-xs">
        <button
          type="button"
          className="text-primary hover:underline disabled:opacity-50"
          disabled={filtered.length === 0}
          onClick={() => onSelectAll(filtered.map((o) => o.value))}
        >
          Select all{query.trim() ? " filtered" : ""} ({filtered.length})
        </button>
        <span className="text-muted-foreground">·</span>
        <button
          type="button"
          className="text-primary hover:underline disabled:opacity-50"
          disabled={selected.length === 0}
          onClick={onClear}
        >
          Clear
        </button>
      </div>
      <ScrollArea className="h-52">
        <div className="p-1">
          {filtered.length === 0 ? (
            <div className="px-2 py-6 text-center text-xs text-muted-foreground">No matches.</div>
          ) : (
            filtered.map((o) => {
              const isSelected = selectedSet.has(o.value);
              return (
                <button
                  key={o.value}
                  type="button"
                  onClick={() => onToggle(o.value)}
                  className="flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm hover:bg-accent"
                >
                  <span
                    className={`flex h-4 w-4 shrink-0 items-center justify-center rounded border ${
                      isSelected ? "border-primary bg-primary text-primary-foreground" : "border-input"
                    }`}
                  >
                    {isSelected && <Check className="h-3 w-3" />}
                  </span>
                  <span className="min-w-0 flex-1 truncate">{o.primary}</span>
                  {o.secondary && (
                    <span className="max-w-[45%] truncate text-xs text-muted-foreground">{o.secondary}</span>
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

export function AgentGroups() {
  const [groups, setGroups] = useState<AgentGroup[]>([]);
  const [agents, setAgents] = useState<AgentSummary[]>([]);
  const [users, setUsers] = useState<UserProfile[]>([]);
  const [loading, setLoading] = useState(true);
  const [editingId, setEditingId] = useState<string | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState<AgentGroupRequest>(EMPTY);
  const [roleInput, setRoleInput] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const [g, a, u] = await Promise.all([
        api.listAgentGroups(),
        api.listAgents().catch(() => []),
        api.listUserProfiles().catch(() => []),
      ]);
      setGroups(g);
      setAgents(a);
      setUsers(u);
    } catch {
      toast.error("Failed to load agent groups");
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

  const openEdit = (g: AgentGroup) => {
    setEditingId(g.id);
    setForm({
      name: g.name,
      description: g.description ?? "",
      agentIds: g.agentIds,
      allowedUserIds: g.allowedUserIds,
      allowedRoles: g.allowedRoles,
    });
    setRoleInput("");
    setShowForm(true);
  };

  const toggle = (field: "agentIds" | "allowedUserIds", value: string) => {
    const current = form[field] ?? [];
    setForm({
      ...form,
      [field]: current.includes(value)
        ? current.filter((v) => v !== value)
        : [...current, value],
    });
  };

  const selectAll = (field: "agentIds" | "allowedUserIds", values: string[]) => {
    const current = form[field] ?? [];
    setForm({ ...form, [field]: Array.from(new Set([...current, ...values])) });
  };

  const clearField = (field: "agentIds" | "allowedUserIds") =>
    setForm({ ...form, [field]: [] });

  const addRole = () => {
    const r = roleInput.trim();
    if (!r) return;
    if (!(form.allowedRoles ?? []).includes(r)) {
      setForm({ ...form, allowedRoles: [...(form.allowedRoles ?? []), r] });
    }
    setRoleInput("");
  };

  const removeRole = (r: string) =>
    setForm({ ...form, allowedRoles: (form.allowedRoles ?? []).filter((x) => x !== r) });

  const handleSave = async () => {
    if (!form.name.trim()) { toast.error("Name is required"); return; }
    try {
      if (editingId) {
        await api.updateAgentGroup(editingId, form);
        toast.success("Group updated");
      } else {
        await api.createAgentGroup(form);
        toast.success("Group created");
      }
      setShowForm(false);
      setEditingId(null);
      load();
    } catch {
      toast.error("Failed to save group");
    }
  };

  const handleDelete = async (g: AgentGroup) => {
    if (!confirm(`Delete group "${g.name}"? Its agents become open to all tenant users.`)) return;
    try {
      await api.deleteAgentGroup(g.id);
      toast.success(`Group "${g.name}" deleted`);
      load();
    } catch {
      toast.error("Failed to delete group");
    }
  };

  const agentName = (id: string) => agents.find((a) => a.id === id)?.displayName ?? agents.find((a) => a.id === id)?.name ?? id;
  const isRestricted = (g: AgentGroup) => g.allowedUserIds.length > 0 || g.allowedRoles.length > 0;

  return (
    <div className="space-y-6">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-2xl font-bold flex items-center gap-2"><ShieldCheck className="h-6 w-6" /> Agent Access Groups</h2>
          <p className="text-sm text-muted-foreground">
            Group agents and restrict who can invoke them. A group with no allowed users or roles imposes no restriction.
          </p>
        </div>
        <Button onClick={() => (showForm && !editingId ? setShowForm(false) : openCreate())}>
          <Plus className="h-4 w-4 mr-1" />{showForm && !editingId ? "Cancel" : "New Group"}
        </Button>
      </div>

      {showForm && (
        <Card>
          <CardHeader><CardTitle>{editingId ? "Edit Group" : "New Group"}</CardTitle></CardHeader>
          <CardContent className="grid gap-4">
            <div className="grid grid-cols-2 gap-4">
              <div className="space-y-1.5">
                <Label>Name</Label>
                <Input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} placeholder="e.g. Finance Agents" />
              </div>
              <div className="space-y-1.5">
                <Label>Description</Label>
                <Input value={form.description ?? ""} onChange={(e) => setForm({ ...form, description: e.target.value })} placeholder="Optional" />
              </div>
            </div>

            <div className="space-y-1.5">
              <Label>Member Agents</Label>
              <CheckableList
                options={agents.map((a) => ({
                  value: a.id,
                  primary: a.displayName || a.name,
                  secondary: a.displayName && a.name && a.displayName !== a.name ? a.name : undefined,
                }))}
                selected={form.agentIds ?? []}
                onToggle={(v) => toggle("agentIds", v)}
                onSelectAll={(vals) => selectAll("agentIds", vals)}
                onClear={() => clearField("agentIds")}
                searchPlaceholder="Search agents…"
                emptyText="No agents available."
              />
            </div>

            <div className="space-y-1.5">
              <Label>Allowed Users</Label>
              <p className="text-xs text-muted-foreground">Leave both users and roles empty to keep the agents open to everyone in the tenant.</p>
              <CheckableList
                options={users.map((u) => ({
                  value: u.userId,
                  primary: u.displayName || u.email || u.userId,
                  secondary: u.displayName && u.email ? u.email : undefined,
                }))}
                selected={form.allowedUserIds ?? []}
                onToggle={(v) => toggle("allowedUserIds", v)}
                onSelectAll={(vals) => selectAll("allowedUserIds", vals)}
                onClear={() => clearField("allowedUserIds")}
                searchPlaceholder="Search users by name or email…"
                emptyText="No user profiles available."
              />
            </div>

            <div className="space-y-1.5">
              <Label>Allowed Roles / SSO Groups</Label>
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
                {(form.allowedRoles ?? []).map((r) => (
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
        <Card><CardContent className="py-8 text-center text-muted-foreground">No agent groups yet. Click "New Group" to create one.</CardContent></Card>
      ) : (
        <div className="space-y-3">
          {groups.map((g) => (
            <Card key={g.id}>
              <CardContent className="flex items-start justify-between py-4">
                <div className="space-y-1.5">
                  <div className="flex items-center gap-2">
                    <span className="font-medium">{g.name}</span>
                    <Badge variant={isRestricted(g) ? "default" : "secondary"}>{isRestricted(g) ? "Restricted" : "Open"}</Badge>
                  </div>
                  {g.description && <p className="text-sm text-muted-foreground">{g.description}</p>}
                  <div className="text-xs text-muted-foreground">
                    {g.agentIds.length} agent(s): {g.agentIds.slice(0, 3).map(agentName).join(", ")}{g.agentIds.length > 3 && "…"}
                  </div>
                  {isRestricted(g) && (
                    <div className="text-xs text-muted-foreground">
                      {g.allowedUserIds.length > 0 && <span>{g.allowedUserIds.length} user(s)</span>}
                      {g.allowedUserIds.length > 0 && g.allowedRoles.length > 0 && <span> · </span>}
                      {g.allowedRoles.length > 0 && <span>roles: {g.allowedRoles.join(", ")}</span>}
                    </div>
                  )}
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
