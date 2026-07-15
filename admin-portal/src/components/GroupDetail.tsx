import { useState, useEffect } from "react";
import { useParams, useNavigate } from "react-router";
import {
  api,
  type TenantGroup, type Tenant, type GroupMember,
  type GroupAgentTemplate, type GroupBusinessRule, type GroupPromptOverride,
  type GroupScheduledTask, type GroupLlmConfig, type PlatformLlmConfig,
  type ArchetypeSummary, type CreateGroupRuleDto,
  type CreateGroupPromptOverrideDto, type CreateGroupTaskDto,
  type UpsertLlmConfigDto, type CreateNamedLlmConfigDto,
  type GroupScheduledTaskExport, type ScheduleExportEnvelope,
} from "@/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter } from "@/components/ui/dialog";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { ArrowLeft, Plus, Trash2, Pencil, Layers, Copy, Download, Upload } from "lucide-react";
import { toast } from "sonner";
import { LlmForm } from "@/components/PlatformLlmConfig";

// ── Members Tab ────────────────────────────────────────────────────────────────

function MembersTab({ groupId }: { groupId: number }) {
  const [members,  setMembers]  = useState<GroupMember[]>([]);
  const [tenants,  setTenants]  = useState<Tenant[]>([]);
  const [loading,  setLoading]  = useState(true);
  const [addId,    setAddId]    = useState<string>("");
  const [adding,   setAdding]   = useState(false);

  async function load() {
    try {
      const [m, t] = await Promise.all([api.listGroupMembers(groupId), api.listTenants()]);
      setMembers(m);
      setTenants(t);
    } catch (e) {
      toast.error(`Failed to load members: ${e}`);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, [groupId]);

  const memberTenantIds = new Set(members.map(m => m.tenantId));
  const available = tenants.filter(t => !memberTenantIds.has(t.id));

  async function addMember() {
    if (!addId) return;
    setAdding(true);
    try {
      await api.addGroupMember(groupId, Number(addId));
      toast.success("Member added");
      setAddId("");
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    } finally {
      setAdding(false);
    }
  }

  async function removeMember(tenantId: number) {
    const tenant = tenants.find(t => t.id === tenantId);
    if (!confirm(`Remove "${tenant?.name ?? tenantId}" from this group?`)) return;
    try {
      await api.removeGroupMember(groupId, tenantId);
      toast.success("Member removed");
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  if (loading) return <div className="py-8 text-center text-muted-foreground">Loading…</div>;

  return (
    <div className="space-y-4">
      {/* Add member */}
      <div className="flex gap-2 items-end">
        <div className="flex-1 space-y-1">
          <Label>Add Tenant</Label>
          <Select value={addId} onValueChange={setAddId}>
            <SelectTrigger>
              <SelectValue placeholder="Select a tenant to add…" />
            </SelectTrigger>
            <SelectContent>
              {available.length === 0
                ? <SelectItem value="__none" disabled>All tenants are already members</SelectItem>
                : available.map(t => (
                  <SelectItem key={t.id} value={String(t.id)}>{t.name}</SelectItem>
                ))}
            </SelectContent>
          </Select>
        </div>
        <Button onClick={addMember} disabled={!addId || adding}>
          <Plus className="size-4 mr-2" />{adding ? "Adding…" : "Add"}
        </Button>
      </div>

      {/* Members list */}
      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Tenant</TableHead>
                <TableHead>Tenant ID</TableHead>
                <TableHead>Joined</TableHead>
                <TableHead className="w-16"></TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {members.length === 0 ? (
                <TableRow><TableCell colSpan={4} className="text-center text-muted-foreground py-8">No members yet.</TableCell></TableRow>
              ) : members.map(m => {
                const tenant = tenants.find(t => t.id === m.tenantId);
                return (
                  <TableRow key={m.id}>
                    <TableCell className="font-medium">{tenant?.name ?? "—"}</TableCell>
                    <TableCell className="text-xs font-mono text-muted-foreground">{m.tenantId}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">{new Date(m.joinedAt).toLocaleDateString()}</TableCell>
                    <TableCell>
                      <Button size="icon" variant="ghost" className="text-destructive"
                        onClick={() => removeMember(m.tenantId)}>
                        <Trash2 className="size-4" />
                      </Button>
                    </TableCell>
                  </TableRow>
                );
              })}
            </TableBody>
          </Table>
        </CardContent>
      </Card>
    </div>
  );
}

// ── Shared Agents Tab ──────────────────────────────────────────────────────────

function AgentsTab({ groupId }: { groupId: number }) {
  const navigate = useNavigate();
  const [agents,  setAgents]  = useState<GroupAgentTemplate[]>([]);
  const [loading, setLoading] = useState(true);

  async function load() {
    try {
      setAgents(await api.listGroupAgents(groupId));
    } catch (e) {
      toast.error(`Failed to load agents: ${e}`);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, [groupId]);

  async function deleteAgent(a: GroupAgentTemplate) {
    if (!confirm(`Delete agent template "${a.displayName}"?`)) return;
    try {
      await api.deleteGroupAgent(groupId, a.id);
      toast.success("Deleted");
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  if (loading) return <div className="py-8 text-center text-muted-foreground">Loading…</div>;

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={() => navigate(`/platform/groups/${groupId}/agents/new`)}>
          <Plus className="size-4 mr-2" /> New Agent Template
        </Button>
      </div>

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Type</TableHead>
                <TableHead>Model</TableHead>
                <TableHead>Status</TableHead>
                <TableHead>Enabled</TableHead>
                <TableHead className="w-20">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {agents.length === 0 ? (
                <TableRow>
                  <TableCell colSpan={6} className="text-center text-muted-foreground py-8">
                    No shared agent templates yet. Click "New Agent Template" to create one.
                  </TableCell>
                </TableRow>
              ) : agents.map(a => (
                <TableRow key={a.id}>
                  <TableCell>
                    <div className="font-medium">{a.displayName}</div>
                    <div className="text-xs text-muted-foreground font-mono">{a.name}</div>
                  </TableCell>
                  <TableCell><Badge variant="outline">{a.agentType}</Badge></TableCell>
                  <TableCell className="text-xs text-muted-foreground">{a.modelId ?? "global default"}</TableCell>
                  <TableCell>
                    <Badge variant={a.status === "Published" ? "default" : "secondary"} className="text-xs">
                      {a.status || "Published"}
                    </Badge>
                  </TableCell>
                  <TableCell>
                    <Switch checked={a.isEnabled} onCheckedChange={async (v) => {
                      try { await api.updateGroupAgent(groupId, a.id, { isEnabled: v }); load(); }
                      catch (e) { toast.error(`Failed: ${e}`); }
                    }} />
                  </TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      <Button
                        size="icon" variant="ghost"
                        onClick={() => navigate(`/platform/groups/${groupId}/agents/${a.id}/edit`)}
                      >
                        <Pencil className="size-4" />
                      </Button>
                      <Button size="icon" variant="ghost" className="text-destructive" onClick={() => deleteAgent(a)}>
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
    </div>
  );
}

// ── Shared Rules Tab ────────────────────────────────────────────────────────────

const emptyRuleForm: CreateGroupRuleDto = {
  agentType: "*", ruleCategory: "", ruleKey: "", promptInjection: "", priority: 50,
  isTemplate: true, hookPoint: "OnInit", hookRuleType: "inject_prompt",
  pattern: undefined, replacement: undefined, toolName: undefined,
  orderInPack: 0, stopOnMatch: false, maxEvaluationMs: 100,
};

function RulesTab({ groupId }: { groupId: number }) {
  const [rules,      setRules]     = useState<GroupBusinessRule[]>([]);
  const [loading,    setLoading]   = useState(true);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editRule,   setEditRule]  = useState<GroupBusinessRule | null>(null);
  const [form,       setForm]      = useState<CreateGroupRuleDto>(emptyRuleForm);
  const [saving,     setSaving]    = useState(false);
  const [archetypes, setArchetypes] = useState<ArchetypeSummary[]>([]);

  useEffect(() => { api.listArchetypes().then(setArchetypes).catch(() => {}); }, []);

  async function load() {
    try {
      setRules(await api.listGroupRules(groupId));
    } catch (e) {
      toast.error(`Failed to load rules: ${e}`);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, [groupId]);

  function openCreate() {
    setEditRule(null);
    setForm(emptyRuleForm);
    setDialogOpen(true);
  }

  function openEdit(r: GroupBusinessRule) {
    setEditRule(r);
    setForm({
      agentType: r.agentType, ruleCategory: r.ruleCategory, ruleKey: r.ruleKey,
      promptInjection: r.promptInjection ?? "", priority: r.priority,
      isTemplate: r.isTemplate, hookPoint: r.hookPoint ?? "OnInit",
      hookRuleType: r.hookRuleType ?? "inject_prompt",
      pattern: r.pattern, replacement: r.replacement, toolName: r.toolName,
      orderInPack: r.orderInPack ?? 0, stopOnMatch: r.stopOnMatch ?? false,
      maxEvaluationMs: r.maxEvaluationMs ?? 100,
    });
    setDialogOpen(true);
  }

  async function save() {
    setSaving(true);
    try {
      if (editRule) {
        await api.updateGroupRule(groupId, editRule.id, form);
        toast.success("Rule updated");
      } else {
        await api.createGroupRule(groupId, form);
        toast.success("Rule created");
      }
      setDialogOpen(false);
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    } finally {
      setSaving(false);
    }
  }

  async function deleteRule(r: GroupBusinessRule) {
    if (!confirm("Delete this rule?")) return;
    try {
      await api.deleteGroupRule(groupId, r.id);
      toast.success("Deleted");
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  const p = (field: keyof CreateGroupRuleDto, v: unknown) =>
    setForm(f => ({ ...f, [field]: v }));

  if (loading) return <div className="py-8 text-center text-muted-foreground">Loading…</div>;

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={openCreate}><Plus className="size-4 mr-2" /> New Rule</Button>
      </div>

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Agent Type</TableHead>
                <TableHead>Category / Key</TableHead>
                <TableHead>Priority</TableHead>
                <TableHead>Template</TableHead>
                <TableHead>Active</TableHead>
                <TableHead className="w-20">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {rules.length === 0 ? (
                <TableRow><TableCell colSpan={6} className="text-center text-muted-foreground py-8">No shared rules yet.</TableCell></TableRow>
              ) : rules.map(r => (
                <TableRow key={r.id}>
                  <TableCell><Badge variant="outline">{r.agentType}</Badge></TableCell>
                  <TableCell>
                    <div className="text-sm">{r.ruleCategory}</div>
                    <div className="text-xs text-muted-foreground font-mono">{r.ruleKey}</div>
                  </TableCell>
                  <TableCell className="text-sm">{r.priority}</TableCell>
                  <TableCell>
                    {r.isTemplate
                      ? <Badge variant="secondary">Template</Badge>
                      : <span className="text-xs text-muted-foreground">Auto</span>}
                  </TableCell>
                  <TableCell>
                    <Switch checked={r.isActive} onCheckedChange={async v => {
                      try { await api.updateGroupRule(groupId, r.id, { ...r, isActive: v }); load(); }
                      catch (e) { toast.error(`Failed: ${e}`); }
                    }} />
                  </TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      <Button size="icon" variant="ghost" onClick={() => openEdit(r)}><Pencil className="size-4" /></Button>
                      <Button size="icon" variant="ghost" className="text-destructive" onClick={() => deleteRule(r)}><Trash2 className="size-4" /></Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Dialog open={dialogOpen} onOpenChange={setDialogOpen}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>{editRule ? "Edit Rule" : "New Shared Rule"}</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 py-2">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label>Agent Type <span className="text-xs text-muted-foreground">(* = all)</span></Label>
                <Select value={form.agentType} onValueChange={v => p("agentType", v)}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value="*">* (all archetypes)</SelectItem>
                    {archetypes.map(a => (
                      <SelectItem key={a.id} value={a.id}>{a.displayName}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div className="space-y-1">
                <Label>Priority</Label>
                <Input type="number" value={form.priority} onChange={e => p("priority", Number(e.target.value))} />
              </div>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label>Category</Label>
                <Input value={form.ruleCategory} onChange={e => p("ruleCategory", e.target.value)} placeholder="e.g. tone" />
              </div>
              <div className="space-y-1">
                <Label>Key</Label>
                <Input value={form.ruleKey} onChange={e => p("ruleKey", e.target.value)} placeholder="e.g. formal_tone" />
              </div>
            </div>
            <div className="space-y-1">
              <Label>Prompt Injection</Label>
              <textarea
                className="w-full min-h-[80px] rounded-md border border-input bg-background px-3 py-2 text-sm resize-y"
                value={form.promptInjection ?? ""}
                onChange={e => p("promptInjection", e.target.value)}
                placeholder="Always respond in a formal, professional tone."
              />
            </div>
            <div className="flex items-center gap-3 rounded-md border p-3">
              <Switch checked={form.isTemplate ?? false} onCheckedChange={v => p("isTemplate", v)} id="is-template-toggle" />
              <div>
                <Label htmlFor="is-template-toggle" className="cursor-pointer">Opt-in Template</Label>
                <p className="text-xs text-muted-foreground mt-0.5">
                  When enabled, member tenants must explicitly activate this rule — it is not auto-injected into their prompts.
                </p>
              </div>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setDialogOpen(false)}>Cancel</Button>
            <Button onClick={save} disabled={saving || !form.ruleCategory || !form.ruleKey}>
              {saving ? "Saving…" : "Save"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

// ── Shared Prompts Tab ─────────────────────────────────────────────────────────

const MERGE_MODES = ["Append", "Prepend", "Replace"];
const SECTIONS    = ["introduction", "guidelines", "tools_context", "output_format", "closing"];

const emptyOverrideForm: CreateGroupPromptOverrideDto = {
  agentType: "*", section: "guidelines", customText: "", mergeMode: "Append", isTemplate: false,
};

function PromptsTab({ groupId }: { groupId: number }) {
  const [overrides,  setOverrides]  = useState<GroupPromptOverride[]>([]);
  const [loading,    setLoading]    = useState(true);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editItem,   setEditItem]   = useState<GroupPromptOverride | null>(null);
  const [form,       setForm]       = useState<CreateGroupPromptOverrideDto>(emptyOverrideForm);
  const [saving,     setSaving]     = useState(false);
  const [archetypes, setArchetypes] = useState<ArchetypeSummary[]>([]);
  useEffect(() => { api.listArchetypes().then(setArchetypes).catch(() => {}); }, []);

  async function load() {
    try {
      setOverrides(await api.listGroupPromptOverrides(groupId));
    } catch (e) {
      toast.error(`Failed to load overrides: ${e}`);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, [groupId]);

  function openCreate() {
    setEditItem(null);
    setForm(emptyOverrideForm);
    setDialogOpen(true);
  }

  function openEdit(o: GroupPromptOverride) {
    setEditItem(o);
    setForm({ agentType: o.agentType, section: o.section, customText: o.customText, mergeMode: o.mergeMode, isTemplate: o.isTemplate ?? false });
    setDialogOpen(true);
  }

  async function save() {
    setSaving(true);
    try {
      if (editItem) {
        await api.updateGroupPromptOverride(groupId, editItem.id, { ...form, isActive: editItem.isActive });
        toast.success("Override updated");
      } else {
        await api.createGroupPromptOverride(groupId, form);
        toast.success("Override created");
      }
      setDialogOpen(false);
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    } finally {
      setSaving(false);
    }
  }

  async function deleteOverride(o: GroupPromptOverride) {
    if (!confirm("Delete this override?")) return;
    try {
      await api.deleteGroupPromptOverride(groupId, o.id);
      toast.success("Deleted");
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  const p = (field: keyof CreateGroupPromptOverrideDto, v: string | boolean) =>
    setForm(f => ({ ...f, [field]: v }));

  if (loading) return <div className="py-8 text-center text-muted-foreground">Loading…</div>;

  return (
    <div className="space-y-4">
      <div className="flex justify-end">
        <Button onClick={openCreate}><Plus className="size-4 mr-2" /> New Override</Button>
      </div>

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Agent Type</TableHead>
                <TableHead>Section</TableHead>
                <TableHead>Mode</TableHead>
                <TableHead>Template</TableHead>
                <TableHead>Active</TableHead>
                <TableHead className="w-20">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {overrides.length === 0 ? (
                <TableRow><TableCell colSpan={6} className="text-center text-muted-foreground py-8">No shared prompt overrides yet.</TableCell></TableRow>
              ) : overrides.map(o => (
                <TableRow key={o.id}>
                  <TableCell><Badge variant="outline">{o.agentType}</Badge></TableCell>
                  <TableCell className="text-sm font-mono">{o.section}</TableCell>
                  <TableCell><Badge variant="secondary">{o.mergeMode}</Badge></TableCell>
                  <TableCell>
                    {o.isTemplate && <Badge variant="outline" className="text-xs text-amber-400 border-amber-500/40">template</Badge>}
                  </TableCell>
                  <TableCell>
                    <Switch checked={o.isActive} onCheckedChange={async v => {
                      try { await api.updateGroupPromptOverride(groupId, o.id, { ...o, isActive: v }); load(); }
                      catch (e) { toast.error(`Failed: ${e}`); }
                    }} />
                  </TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      <Button size="icon" variant="ghost" onClick={() => openEdit(o)}><Pencil className="size-4" /></Button>
                      <Button size="icon" variant="ghost" className="text-destructive" onClick={() => deleteOverride(o)}><Trash2 className="size-4" /></Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Dialog open={dialogOpen} onOpenChange={setDialogOpen}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>{editItem ? "Edit Override" : "New Shared Prompt Override"}</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 py-2">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label>Agent Type <span className="text-xs text-muted-foreground">(* = all)</span></Label>
                <Select value={form.agentType} onValueChange={v => p("agentType", v)}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value="*">* (all archetypes)</SelectItem>
                    {archetypes.map(a => (
                      <SelectItem key={a.id} value={a.id}>{a.displayName}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
              <div className="space-y-1">
                <Label>Merge Mode</Label>
                <Select value={form.mergeMode} onValueChange={v => p("mergeMode", v)}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {MERGE_MODES.map(m => <SelectItem key={m} value={m}>{m}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
            </div>
            <div className="space-y-1">
              <Label>Section</Label>
              <Select value={form.section} onValueChange={v => p("section", v)}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  {SECTIONS.map(s => <SelectItem key={s} value={s}>{s}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1">
              <Label>Custom Text</Label>
              <textarea
                className="w-full min-h-[100px] rounded-md border border-input bg-background px-3 py-2 text-sm resize-y"
                value={form.customText}
                onChange={e => p("customText", e.target.value)}
                placeholder="Additional instructions for this section…"
              />
            </div>
            <div className="flex items-center gap-2 pt-1">
              <Switch checked={form.isTemplate ?? false} onCheckedChange={v => p("isTemplate", v)} id="prompt-template-toggle" />
              <Label htmlFor="prompt-template-toggle" className="text-sm">
                Offer as opt-in template to member tenants
              </Label>
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setDialogOpen(false)}>Cancel</Button>
            <Button onClick={save} disabled={saving || !form.customText}>
              {saving ? "Saving…" : "Save"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

// ── Schedules Tab ──────────────────────────────────────────────────────────────

const SCHEDULE_TYPES = ["once", "daily", "weekly"];
const DAYS_OF_WEEK   = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

const emptyTaskForm: CreateGroupTaskDto = {
  agentType: "", name: "", description: "",
  scheduleType: "daily", runAtTime: "09:00",
  timeZoneId: "UTC", payloadType: "prompt", promptText: "", isEnabled: true,
};

function SchedulesTab({ groupId }: { groupId: number }) {
  const [tasks,      setTasks]      = useState<GroupScheduledTask[]>([]);
  const [loading,    setLoading]    = useState(true);
  const [dialogOpen, setDialogOpen] = useState(false);
  const [editTask,   setEditTask]   = useState<GroupScheduledTask | null>(null);
  const [isCloning,  setIsCloning]  = useState(false);
  const [form,       setForm]       = useState<CreateGroupTaskDto>(emptyTaskForm);
  const [saving,     setSaving]     = useState(false);

  // Import state
  const [importOpen,      setImportOpen]      = useState(false);
  const [importRaw,       setImportRaw]       = useState("");
  const [importParsed,    setImportParsed]    = useState<GroupScheduledTaskExport[] | null>(null);
  const [importConflicts, setImportConflicts] = useState<string[]>([]);
  const [importSkip,      setImportSkip]      = useState(true);
  const [importing,       setImporting]       = useState(false);

  async function load() {
    try {
      setTasks(await api.listGroupSchedules(groupId));
    } catch (e) {
      toast.error(`Failed to load schedules: ${e}`);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, [groupId]);

  function openCreate() {
    setEditTask(null);
    setIsCloning(false);
    setForm(emptyTaskForm);
    setDialogOpen(true);
  }

  function openEdit(t: GroupScheduledTask) {
    setEditTask(t);
    setIsCloning(false);
    setForm({
      agentType: t.agentType, name: t.name, description: t.description ?? "",
      scheduleType: t.scheduleType, scheduledAtUtc: t.scheduledAtUtc,
      runAtTime: t.runAtTime ?? "09:00", dayOfWeek: t.dayOfWeek,
      timeZoneId: t.timeZoneId, payloadType: t.payloadType,
      promptText: t.promptText, isEnabled: t.isEnabled,
    });
    setDialogOpen(true);
  }

  function openClone(t: GroupScheduledTask) {
    setEditTask(null);
    setIsCloning(true);
    setForm({
      agentType: t.agentType,
      name: `${t.name} (copy)`,
      description: t.description ?? "",
      scheduleType: t.scheduleType,
      // Gap 2 fix: never carry a past one-time date into a clone
      scheduledAtUtc: t.scheduleType === "once" ? undefined : t.scheduledAtUtc,
      runAtTime: t.runAtTime ?? "09:00",
      dayOfWeek: t.dayOfWeek,
      timeZoneId: t.timeZoneId,
      payloadType: t.payloadType,
      promptText: t.promptText,
      // Clone always starts disabled
      isEnabled: false,
    });
    setDialogOpen(true);
  }

  async function save() {
    setSaving(true);
    try {
      const dto = { ...form, description: form.description || undefined };
      if (editTask) {
        await api.updateGroupSchedule(groupId, editTask.id, dto);
        toast.success("Schedule updated");
      } else {
        await api.createGroupSchedule(groupId, dto);
        toast.success(isCloning ? "Schedule cloned" : "Schedule created");
      }
      setDialogOpen(false);
      setIsCloning(false);
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    } finally {
      setSaving(false);
    }
  }

  async function deleteTask(t: GroupScheduledTask) {
    if (!confirm(`Delete schedule "${t.name}"?`)) return;
    try {
      await api.deleteGroupSchedule(groupId, t.id);
      toast.success("Deleted");
      load();
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  const p = (field: keyof CreateGroupTaskDto, v: unknown) =>
    setForm(f => ({ ...f, [field]: v }));

  // ── Export helpers ──────────────────────────────────────────────────────
  const toExportTask = (t: GroupScheduledTask): GroupScheduledTaskExport => ({
    agentType: t.agentType, name: t.name, description: t.description,
    scheduleType: t.scheduleType, scheduledAtUtc: t.scheduledAtUtc,
    runAtTime: t.runAtTime, dayOfWeek: t.dayOfWeek, timeZoneId: t.timeZoneId,
    payloadType: t.payloadType, promptText: t.promptText,
    parametersJson: t.parametersJson, isEnabled: t.isEnabled,
  });

  const triggerDownload = (json: string, filename: string) => {
    const blob = new Blob([json], { type: "application/json" });
    const url  = URL.createObjectURL(blob);
    const a    = document.createElement("a");
    a.href = url; a.download = filename; a.click();
    URL.revokeObjectURL(url);
  };

  const dateSlug = () => new Date().toISOString().slice(0, 10);

  const handleExportAll = () => {
    const envelope: ScheduleExportEnvelope = {
      version: "1", exportedAt: new Date().toISOString(),
      type: "group-schedules",
      tasks: tasks.map(toExportTask) as unknown as ScheduleExportEnvelope["tasks"],
    };
    triggerDownload(JSON.stringify(envelope, null, 2), `group-${groupId}-schedules-${dateSlug()}.json`);
  };

  const handleExportOne = (t: GroupScheduledTask) => {
    const envelope: ScheduleExportEnvelope = {
      version: "1", exportedAt: new Date().toISOString(),
      type: "group-schedules",
      tasks: [toExportTask(t)] as unknown as ScheduleExportEnvelope["tasks"],
    };
    const slug = t.name.replace(/\s+/g, "-").toLowerCase();
    triggerDownload(JSON.stringify(envelope, null, 2), `group-schedule-${slug}-${dateSlug()}.json`);
  };

  // ── Import helpers ──────────────────────────────────────────────────────
  const closeImport = () => {
    setImportOpen(false); setImportRaw("");
    setImportParsed(null); setImportConflicts([]); setImportSkip(true);
  };

  const handleImportParse = () => {
    let parsed: ScheduleExportEnvelope;
    try { parsed = JSON.parse(importRaw); }
    catch { toast.error("Invalid JSON — check the file format."); return; }
    if (!parsed?.tasks || !Array.isArray(parsed.tasks)) {
      toast.error("Invalid format: missing \"tasks\" array."); return;
    }
    if (parsed.type && parsed.type !== "group-schedules") {
      toast.error(`Wrong file type: expected "group-schedules", got "${parsed.type}".`); return;
    }
    const existing = new Set(tasks.map(t => t.name.toLowerCase()));
    const conflicts = parsed.tasks
      .filter(t => existing.has((t.name ?? "").toLowerCase()))
      .map(t => t.name);
    setImportParsed(parsed.tasks as unknown as GroupScheduledTaskExport[]);
    setImportConflicts(conflicts);
  };

  const handleImportSubmit = async () => {
    if (!importParsed?.length) return;
    setImporting(true);
    try {
      const result = await api.importGroupSchedules(groupId, { tasks: importParsed, skipConflicts: importSkip });
      toast.success(`Imported: ${result.created} created, ${result.skipped} skipped.`);
      closeImport();
      load();
    } catch (e) { toast.error(`Import failed: ${e}`); }
    finally { setImporting(false); }
  };

  if (loading) return <div className="py-8 text-center text-muted-foreground">Loading…</div>;

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div />
        <div className="flex items-center gap-2">
          <Button variant="outline" onClick={handleExportAll} disabled={tasks.length === 0}>
            <Download className="size-4 mr-2" /> Export All
          </Button>
          <Button variant="outline" onClick={() => setImportOpen(true)}>
            <Upload className="size-4 mr-2" /> Import
          </Button>
          <Button onClick={openCreate}><Plus className="size-4 mr-2" /> New Schedule</Button>
        </div>
      </div>

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Agent Type</TableHead>
                <TableHead>Schedule</TableHead>
                <TableHead>Next Run</TableHead>
                <TableHead>Enabled</TableHead>
                <TableHead className="w-20">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {tasks.length === 0 ? (
                <TableRow><TableCell colSpan={6} className="text-center text-muted-foreground py-8">No shared schedules yet.</TableCell></TableRow>
              ) : tasks.map(t => (
                <TableRow key={t.id}>
                  <TableCell className="font-medium">{t.name}</TableCell>
                  <TableCell><Badge variant="outline">{t.agentType}</Badge></TableCell>
                  <TableCell className="text-sm">
                    {t.scheduleType === "once" ? "Once" :
                     t.scheduleType === "daily" ? `Daily ${t.runAtTime}` :
                     `${DAYS_OF_WEEK[t.dayOfWeek ?? 0]} ${t.runAtTime}`}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground">
                    {t.nextRunUtc ? new Date(t.nextRunUtc).toLocaleString() : "—"}
                  </TableCell>
                  <TableCell>
                    <Switch checked={t.isEnabled} onCheckedChange={async v => {
                      try { await api.setGroupScheduleEnabled(groupId, t.id, v); load(); }
                      catch (e) { toast.error(`Failed: ${e}`); }
                    }} />
                  </TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      <Button size="icon" variant="ghost" onClick={() => openEdit(t)}><Pencil className="size-4" /></Button>
                      <Button size="icon" variant="ghost" onClick={() => openClone(t)} title="Clone"><Copy className="size-4" /></Button>
                      <Button size="icon" variant="ghost" onClick={() => handleExportOne(t)} title="Export"><Download className="size-4" /></Button>
                      <Button size="icon" variant="ghost" className="text-destructive" onClick={() => deleteTask(t)}><Trash2 className="size-4" /></Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      <Dialog open={dialogOpen} onOpenChange={v => { setDialogOpen(v); if (!v) setIsCloning(false); }}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>{editTask ? "Edit Schedule" : isCloning ? "Clone Group Schedule" : "New Group Schedule"}</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 py-2">
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label>Name</Label>
                <Input value={form.name} onChange={e => p("name", e.target.value)} placeholder="Weekly Report" />
              </div>
              <div className="space-y-1">
                <Label>Agent Type</Label>
                <Input value={form.agentType} onChange={e => p("agentType", e.target.value)} placeholder="ReportAgent" />
              </div>
            </div>
            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label>Schedule Type</Label>
                <Select value={form.scheduleType} onValueChange={v => p("scheduleType", v)}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {SCHEDULE_TYPES.map(s => <SelectItem key={s} value={s}>{s}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
              {form.scheduleType === "once" ? (
                <div className="space-y-1">
                  <Label>Scheduled At (UTC)</Label>
                  <Input type="datetime-local" value={form.scheduledAtUtc?.slice(0, 16) ?? ""} onChange={e => p("scheduledAtUtc", e.target.value + ":00Z")} />
                </div>
              ) : (
                <div className="space-y-1">
                  <Label>Run At (HH:mm)</Label>
                  <Input type="time" value={form.runAtTime ?? "09:00"} onChange={e => p("runAtTime", e.target.value)} />
                </div>
              )}
            </div>
            {form.scheduleType === "weekly" && (
              <div className="space-y-1">
                <Label>Day of Week</Label>
                <Select value={String(form.dayOfWeek ?? 1)} onValueChange={v => p("dayOfWeek", Number(v))}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {DAYS_OF_WEEK.map((d, i) => <SelectItem key={i} value={String(i)}>{d}</SelectItem>)}
                  </SelectContent>
                </Select>
              </div>
            )}
            <div className="space-y-1">
              <Label>Prompt Text</Label>
              <textarea
                className="w-full min-h-[80px] rounded-md border border-input bg-background px-3 py-2 text-sm resize-y"
                value={form.promptText}
                onChange={e => p("promptText", e.target.value)}
                placeholder="Generate a weekly summary report…"
              />
            </div>
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setDialogOpen(false)}>Cancel</Button>
            <Button onClick={save} disabled={saving || !form.name || !form.agentType || !form.promptText}>
              {saving ? "Saving…" : editTask ? "Save Changes" : isCloning ? "Clone Schedule" : "Save"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* ── Import dialog ─────────────────────────────────────────────────── */}
      <Dialog open={importOpen} onOpenChange={v => { if (!v) closeImport(); else setImportOpen(true); }}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>Import Group Schedules</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 py-1">
            <div className="space-y-1">
              <Label>JSON file</Label>
              <input
                type="file" accept=".json,application/json"
                className="block w-full text-sm text-muted-foreground file:mr-3 file:py-1 file:px-3 file:rounded file:border file:border-input file:text-sm file:font-medium cursor-pointer"
                onChange={e => {
                  const f = e.target.files?.[0];
                  if (!f) return;
                  const reader = new FileReader();
                  reader.onload = ev => { setImportRaw(ev.target?.result as string ?? ""); setImportParsed(null); setImportConflicts([]); };
                  reader.readAsText(f);
                }}
              />
            </div>
            <div className="space-y-1">
              <Label>Or paste JSON</Label>
              <textarea
                className="w-full min-h-[80px] rounded-md border border-input bg-background px-3 py-2 font-mono text-xs resize-y"
                placeholder='{"version":"1","type":"group-schedules","tasks":[...]}'
                value={importRaw}
                onChange={e => { setImportRaw(e.target.value); setImportParsed(null); setImportConflicts([]); }}
              />
            </div>
            {importParsed === null ? (
              <Button size="sm" variant="outline" onClick={handleImportParse} disabled={!importRaw.trim()}>
                Preview
              </Button>
            ) : (
              <div className="space-y-2">
                <p className="text-sm text-muted-foreground">
                  <span className="font-medium text-foreground">{importParsed.length}</span> task{importParsed.length !== 1 ? "s" : ""} found.
                </p>
                {importConflicts.length > 0 && (
                  <div className="rounded-md border border-amber-500/30 bg-amber-500/5 p-3 space-y-1">
                    <p className="text-xs font-medium text-amber-500">{importConflicts.length} name conflict{importConflicts.length !== 1 ? "s" : ""}:</p>
                    <ul className="text-xs text-muted-foreground list-disc list-inside">
                      {importConflicts.map(n => <li key={n}>{n}</li>)}
                    </ul>
                    <div className="flex items-center gap-2 pt-1">
                      <Switch checked={importSkip} onCheckedChange={setImportSkip} />
                      <span className="text-xs">
                        {importSkip ? `Skip ${importConflicts.length} conflicting task${importConflicts.length !== 1 ? "s" : ""}` : "Create anyway (duplicates allowed)"}
                      </span>
                    </div>
                  </div>
                )}
              </div>
            )}
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={closeImport}>Cancel</Button>
            <Button
              onClick={handleImportSubmit}
              disabled={!importParsed?.length || importing}
            >
              {importing ? "Importing…" : importParsed
                ? `Import (${importSkip ? importParsed.length - importConflicts.length : importParsed.length} new)`
                : "Import"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

// ── LLM Config Tab ─────────────────────────────────────────────────────────────

function LlmConfigTab({ groupId }: { groupId: number }) {
  const [groupConfigs,    setGroupConfigs]    = useState<GroupLlmConfig[]>([]);
  const [platformConfigs, setPlatformConfigs] = useState<PlatformLlmConfig[]>([]);
  const [loading,         setLoading]         = useState(true);

  // New own config form
  const [showAdd,      setShowAdd]      = useState(false);
  const [newName,      setNewName]      = useState("");
  const [newForm,      setNewForm]      = useState<UpsertLlmConfigDto>({});
  const [addingSaving, setAddingSaving] = useState(false);

  async function load() {
    try {
      const [all, plat] = await Promise.all([
        api.listGroupLlmConfigs(groupId),
        api.listPlatformLlmConfigs(),
      ]);
      setGroupConfigs(all);
      setPlatformConfigs(plat);
    } catch (e) {
      toast.error(`Failed to load LLM config: ${e}`);
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, [groupId]);

  async function addPlatformRef(platformConfig: PlatformLlmConfig) {
    try {
      const created = await api.addGroupPlatformRef(groupId, { platformConfigId: platformConfig.id });
      setGroupConfigs(l => [...l, created]);
      toast.success(`"${platformConfig.name}" added to group`);
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

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
      };
      const created = await api.createGroupLlmConfig(groupId, dto);
      setGroupConfigs(l => [...l, created]);
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

  async function deleteConfig(id: number, name?: string) {
    if (!confirm(`Remove config "${name}" from group?`)) return;
    try {
      await api.deleteGroupLlmConfigById(groupId, id);
      setGroupConfigs(l => l.filter(x => x.id !== id));
      toast.success("Config removed");
    } catch (e) {
      toast.error(`Failed: ${e}`);
    }
  }

  if (loading) return <div className="py-8 text-center text-muted-foreground">Loading…</div>;

  const addedPlatformIds = new Set(groupConfigs.filter(c => c.platformConfigRef).map(c => c.platformConfigRef!));

  return (
    <div className="space-y-8 max-w-2xl">

      {/* ── Section 1: Platform Configs ── */}
      <div className="space-y-3">
        <div>
          <h4 className="text-sm font-medium">Platform Configs</h4>
          <p className="text-xs text-muted-foreground mt-0.5">
            Add platform-level configs to this group. Tenant agents will see them in the LLM config picker without credential re-entry.
          </p>
        </div>
        {platformConfigs.length === 0 ? (
          <p className="text-xs text-muted-foreground italic">No platform configs defined yet — create them in Platform Settings → LLM Configs.</p>
        ) : (
          platformConfigs.map(pc => {
            const alreadyAdded = addedPlatformIds.has(pc.id);
            return (
              <div key={pc.id} className="flex items-center justify-between rounded border px-3 py-2">
                <div>
                  <span className="text-sm font-medium">{pc.name}</span>
                  <span className="ml-2 text-xs text-muted-foreground">{pc.provider} · {pc.model}</span>
                </div>
                <Button
                  size="sm" variant={alreadyAdded ? "secondary" : "outline"}
                  disabled={alreadyAdded}
                  onClick={() => !alreadyAdded && addPlatformRef(pc)}
                >
                  {alreadyAdded ? "Added" : "Add to Group"}
                </Button>
              </div>
            );
          })
        )}
      </div>

      {/* ── Section 2: Group-owned Configs ── */}
      <div className="space-y-3">
        <div className="flex items-center justify-between">
          <div>
            <h4 className="text-sm font-medium">Group-owned Configs</h4>
            <p className="text-xs text-muted-foreground mt-0.5">
              Configs with their own credentials, scoped to this group.
            </p>
          </div>
          <Button size="sm" variant="outline" onClick={() => setShowAdd(s => !s)}>
            <Plus className="size-4 mr-1" />Add
          </Button>
        </div>

        {showAdd && (
          <Card className="border-dashed">
            <CardHeader><CardTitle className="text-sm">New Group Config</CardTitle></CardHeader>
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

        {groupConfigs.length === 0 && !showAdd && (
          <p className="text-xs text-muted-foreground italic">No group configs yet.</p>
        )}

        {groupConfigs.map(c => (
          <div key={c.id} className="flex items-center justify-between rounded border px-3 py-2">
            <div>
              <span className="text-sm font-medium">{c.name}</span>
              {c.platformConfigRef ? (
                <Badge variant="secondary" className="ml-2 text-xs">via Platform</Badge>
              ) : (
                <span className="ml-2 text-xs text-muted-foreground">
                  {[c.provider, c.model].filter(Boolean).join(" · ") || "no credentials set"}
                </span>
              )}
            </div>
            <div className="flex items-center gap-1">
              <span className="text-xs text-muted-foreground">ID {c.id}</span>
              <Button size="sm" variant="ghost" className="text-destructive h-7 px-2" onClick={() => deleteConfig(c.id, c.name)}>
                <Trash2 className="size-3" />
              </Button>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}

// ── GroupDetail ────────────────────────────────────────────────────────────────

export function GroupDetail() {
  const { id }   = useParams<{ id: string }>();
  const navigate = useNavigate();
  const groupId  = Number(id);

  const [group,   setGroup]   = useState<TenantGroup | null>(null);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.getGroup(groupId)
      .then(g => setGroup(g))
      .catch(() => toast.error("Failed to load group"))
      .finally(() => setLoading(false));
  }, [groupId]);

  if (loading) return <div className="p-6 text-muted-foreground">Loading…</div>;

  if (!group) {
    return (
      <div className="p-6 space-y-4">
        <p className="text-destructive">Group not found.</p>
        <Button variant="outline" onClick={() => navigate("/platform/groups")}>
          <ArrowLeft className="size-4 mr-2" /> Back to Groups
        </Button>
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6">
      {/* Header */}
      <div className="space-y-2">
        <Button variant="ghost" size="sm" className="-ml-2 text-muted-foreground"
          onClick={() => navigate("/platform/groups")}>
          <ArrowLeft className="size-4 mr-1" /> Groups
        </Button>
        <div className="flex items-center gap-3">
          <Layers className="size-6 text-muted-foreground" />
          <h1 className="text-2xl font-semibold">{group.name}</h1>
          <Badge variant={group.isActive ? "default" : "secondary"}>
            {group.isActive ? "Active" : "Inactive"}
          </Badge>
          <Badge variant="outline" className="ml-1">{group.memberCount} tenant{group.memberCount !== 1 ? "s" : ""}</Badge>
          <span className="text-xs text-muted-foreground ml-auto">Group ID: {group.id}</span>
        </div>
        {group.description && (
          <p className="text-sm text-muted-foreground">{group.description}</p>
        )}
      </div>

      {/* Tabs */}
      <Tabs defaultValue="members">
        <TabsList>
          <TabsTrigger value="members">Members</TabsTrigger>
          <TabsTrigger value="agents">Shared Agents</TabsTrigger>
          <TabsTrigger value="rules">Shared Rules</TabsTrigger>
          <TabsTrigger value="prompts">Shared Prompts</TabsTrigger>
          <TabsTrigger value="schedules">Schedules</TabsTrigger>
          <TabsTrigger value="llm">LLM Config</TabsTrigger>
        </TabsList>

        <TabsContent value="members" className="mt-4">
          <MembersTab groupId={groupId} />
        </TabsContent>

        <TabsContent value="agents" className="mt-4">
          <AgentsTab groupId={groupId} />
        </TabsContent>

        <TabsContent value="rules" className="mt-4">
          <RulesTab groupId={groupId} />
        </TabsContent>

        <TabsContent value="prompts" className="mt-4">
          <PromptsTab groupId={groupId} />
        </TabsContent>

        <TabsContent value="schedules" className="mt-4">
          <SchedulesTab groupId={groupId} />
        </TabsContent>

        <TabsContent value="llm" className="mt-4">
          <LlmConfigTab groupId={groupId} />
        </TabsContent>
      </Tabs>
    </div>
  );
}
