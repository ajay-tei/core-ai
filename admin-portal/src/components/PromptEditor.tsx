import { useState, useEffect, useCallback } from "react";
import { api, type AgentSummary, type ArchetypeSummary, type PromptOverride, type GroupPromptTemplateItem } from "@/api";
import { toast } from "sonner";
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from "@/components/ui/table";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogClose,
} from "@/components/ui/dialog";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent,
  AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/ui/empty-state";
import { MoreHorizontal, Plus, FileText, Pencil, Trash2, RefreshCw } from "lucide-react";

const SECTIONS    = ["system", "react-agent", "text-to-sql", "output-format", "security-constraints"];
const MERGE_MODES = ["Append", "Prepend", "Replace"];

const MERGE_MODE_COLORS: Record<string, string> = {
  Append:  "bg-blue-500/10 text-blue-400 border-blue-500/20",
  Prepend: "bg-amber-500/10 text-amber-400 border-amber-500/20",
  Replace: "bg-red-500/10 text-red-400 border-red-500/20",
};

function MergeModeBadge({ mode }: { mode: string }) {
  const cls = MERGE_MODE_COLORS[mode] ?? "bg-muted text-muted-foreground";
  return (
    <Badge variant="outline" className={`text-xs font-medium ${cls}`}>
      {mode}
    </Badge>
  );
}

export function PromptEditor() {
  const [overrides, setOverrides]       = useState<PromptOverride[]>([]);
  const [archetypes, setArchetypes]     = useState<ArchetypeSummary[]>([]);
  const [agents, setAgents]             = useState<AgentSummary[]>([]);
  const [loading, setLoading]           = useState(true);
  const [agentFilter, setFilter]        = useState<string | undefined>(undefined);
  const [agentIdFilter, setAgentIdFilter] = useState<string | undefined>(undefined);
  const [dialogOpen, setDialogOpen]     = useState(false);
  const [editOverride, setEditOverride] = useState<PromptOverride | null>(null);
  const [deleteId, setDeleteId]         = useState<number | null>(null);

  useEffect(() => {
    api.listArchetypes().then(setArchetypes).catch(() => {});
    api.listAgents().then(setAgents).catch(() => {});
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    try   { setOverrides(await api.getPromptOverrides(1, agentFilter, agentIdFilter)); }
    catch (e: unknown) { toast.error(String(e)); }
    finally { setLoading(false); }
  }, [agentFilter, agentIdFilter]);

  useEffect(() => { load(); }, [load]);

  const handleDelete = async () => {
    if (!deleteId) return;
    try {
      await api.deletePromptOverride(deleteId, 1);
      setOverrides(o => o.filter(x => x.id !== deleteId));
      toast.success("Override deleted.");
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setDeleteId(null); }
  };

  const handleToggleActive = async (o: PromptOverride) => {
    try {
      await api.updatePromptOverride(o.id, { ...o, isActive: !o.isActive }, 1);
      setOverrides(prev => prev.map(x => x.id === o.id ? { ...x, isActive: !x.isActive } : x));
    } catch (e: unknown) { toast.error(String(e)); }
  };

  const openCreate = () => { setEditOverride(null); setDialogOpen(true); };
  const openEdit   = (o: PromptOverride) => { setEditOverride(o); setDialogOpen(true); };

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-xl font-semibold">Prompt Overrides</h2>
        <p className="text-sm text-muted-foreground">
          Customise or extend base prompt templates per agent type and section.
        </p>
      </div>

      <Tabs defaultValue="my-overrides">
        <TabsList>
          <TabsTrigger value="my-overrides">My Overrides</TabsTrigger>
          <TabsTrigger value="group-templates">Group Templates</TabsTrigger>
        </TabsList>
        <TabsContent value="my-overrides" className="space-y-4">
          <div className="flex items-center gap-2 justify-end">
            <Select value={agentFilter ?? "*"} onValueChange={v => setFilter(v === "*" ? undefined : v)}>
              <SelectTrigger className="w-44 h-8 text-xs">
                <SelectValue placeholder="All archetypes" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="*">All archetypes</SelectItem>
                {archetypes.map(a => (
                  <SelectItem key={a.id} value={a.id}>{a.displayName}</SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Select value={agentIdFilter ?? "*"} onValueChange={v => setAgentIdFilter(v === "*" ? undefined : v)}>
              <SelectTrigger className="w-44 h-8 text-xs">
                <SelectValue placeholder="All agents" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="*">All agents</SelectItem>
                {agents.map(a => (
                  <SelectItem key={a.id} value={a.id}>{a.displayName || a.name}</SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Button size="sm" variant="outline" onClick={load} className="h-8">
              <RefreshCw className="h-3.5 w-3.5" />
            </Button>
            <Button size="sm" onClick={openCreate} className="h-8">
              <Plus className="h-3.5 w-3.5 mr-1" /> Add Override
            </Button>
          </div>

          {loading ? (
        <div className="rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Archetype / Section</TableHead>
                <TableHead>Agent</TableHead>
                <TableHead>Merge Mode</TableHead>
                <TableHead>Preview</TableHead>
                <TableHead>Active</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {Array.from({ length: 3 }).map((_, i) => (
                <TableRow key={i}>
                  <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                  <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                  <TableCell><Skeleton className="h-5 w-16" /></TableCell>
                  <TableCell><Skeleton className="h-4 w-48" /></TableCell>
                  <TableCell><Skeleton className="h-5 w-9" /></TableCell>
                  <TableCell><Skeleton className="h-5 w-5" /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : overrides.length === 0 ? (
        <EmptyState
          icon={FileText}
          title="No prompt overrides"
          description="Add overrides to customise base prompt sections for this tenant."
          action={{ label: "Add Override", onClick: openCreate }}
        />
      ) : (
        <div className="rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Archetype / Section</TableHead>
                <TableHead>Agent</TableHead>
                <TableHead>Merge Mode</TableHead>
                <TableHead>Preview</TableHead>
                <TableHead className="w-20 text-center">Active</TableHead>
                <TableHead className="w-10" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {overrides.map(o => (
                <TableRow key={o.id}>
                  <TableCell>
                    <div className="text-sm font-medium">
                      {o.agentType === "*" ? "All archetypes" : o.agentType}
                    </div>
                    <div className="text-xs font-mono text-muted-foreground mt-0.5">{o.section}</div>
                  </TableCell>
                  <TableCell>
                    {o.agentId
                      ? <span className="text-xs font-mono">{agents.find(a => a.id === o.agentId)?.displayName ?? o.agentId}</span>
                      : <span className="text-xs text-muted-foreground">—</span>}
                  </TableCell>
                  <TableCell><MergeModeBadge mode={o.mergeMode} /></TableCell>
                  <TableCell>
                    <p className="text-xs text-muted-foreground line-clamp-2 max-w-xs">{o.customText}</p>
                  </TableCell>
                  <TableCell className="text-center">
                    <Switch
                      checked={o.isActive}
                      onCheckedChange={() => handleToggleActive(o)}
                      className="data-[state=checked]:bg-emerald-500"
                    />
                  </TableCell>
                  <TableCell>
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button variant="ghost" size="icon" className="h-7 w-7">
                          <MoreHorizontal className="h-4 w-4" />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        <DropdownMenuItem onClick={() => openEdit(o)}>
                          <Pencil className="h-3.5 w-3.5 mr-2" /> Edit
                        </DropdownMenuItem>
                        <DropdownMenuItem
                          onClick={() => setDeleteId(o.id)}
                          className="text-destructive focus:text-destructive"
                        >
                          <Trash2 className="h-3.5 w-3.5 mr-2" /> Delete
                        </DropdownMenuItem>
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <OverrideDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        initial={editOverride}
        archetypes={archetypes}
        agents={agents}
        onSaved={() => { setDialogOpen(false); load(); }}
      />

      <AlertDialog open={deleteId !== null} onOpenChange={(open: boolean) => !open && setDeleteId(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete override?</AlertDialogTitle>
            <AlertDialogDescription>
              This prompt override will be permanently removed.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={handleDelete}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
        </TabsContent>

        <TabsContent value="group-templates">
          <GroupPromptTemplates />
        </TabsContent>
      </Tabs>
    </div>
  );
}

// ── Override form dialog ──────────────────────────────────────────────────────

interface OverrideDialogProps {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  initial: PromptOverride | null;
  archetypes: ArchetypeSummary[];
  agents: AgentSummary[];
  onSaved: () => void;
}

function OverrideDialog({ open, onOpenChange, initial, archetypes, agents, onSaved }: OverrideDialogProps) {
  const [agentType,   setAgent]     = useState(initial?.agentType ?? "*");
  const [agentId,     setAgentId]   = useState<string>(initial?.agentId ?? "");
  const [section,     setSection]   = useState(initial?.section ?? "system");
  const [customText,  setText]      = useState(initial?.customText ?? "");
  const [mergeMode,   setMerge]     = useState(initial?.mergeMode ?? "Append");
  const [isActive,    setActive]    = useState(initial?.isActive ?? true);
  const [saving,      setSaving]    = useState(false);

  useEffect(() => {
    if (open) {
      setAgent(initial?.agentType ?? "*");
      setAgentId(initial?.agentId ?? "");
      setSection(initial?.section ?? "system");
      setText(initial?.customText ?? "");
      setMerge(initial?.mergeMode ?? "Append");
      setActive(initial?.isActive ?? true);
    }
  }, [open, initial]);

  const mergeModeHint =
    mergeMode === "Replace" ? "replaces the entire section"
    : mergeMode === "Prepend" ? "added before base template"
    : "added after base template";

  const save = async () => {
    if (!customText.trim()) { toast.error("Custom text is required."); return; }
    setSaving(true);
    try {
      if (initial) {
        await api.updatePromptOverride(initial.id, { customText, mergeMode, isActive }, 1);
      } else {
        await api.createPromptOverride({ agentType, agentId: agentId || undefined, section, customText, mergeMode }, 1);
      }
      toast.success(initial ? "Override updated." : "Override created.");
      onSaved();
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setSaving(false); }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>{initial ? "Edit Override" : "New Prompt Override"}</DialogTitle>
        </DialogHeader>

        <div className="grid gap-4 py-2">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label>Archetype</Label>
              <Select value={agentType} onValueChange={setAgent} disabled={!!initial}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="*">* (all archetypes)</SelectItem>
                  {archetypes.map(a => (
                    <SelectItem key={a.id} value={a.id}>{a.displayName}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label>Specific Agent <span className="text-xs text-muted-foreground">(optional)</span></Label>
              <Select value={agentId || "_all"} onValueChange={v => setAgentId(v === "_all" ? "" : v)} disabled={!!initial}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="_all">All agents</SelectItem>
                  {agents.map(a => (
                    <SelectItem key={a.id} value={a.id}>{a.displayName || a.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label>Section</Label>
              <Select value={section} onValueChange={setSection} disabled={!!initial}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  {SECTIONS.map(s => <SelectItem key={s} value={s}>{s}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label>Merge Mode</Label>
              <Select value={mergeMode} onValueChange={setMerge}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  {MERGE_MODES.map(m => <SelectItem key={m} value={m}>{m}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
          </div>

          <div className="space-y-1.5">
            <Label>
              Custom Text *{" "}
              <span className="text-xs text-muted-foreground">({mergeModeHint})</span>
            </Label>
            <Textarea
              value={customText}
              onChange={e => setText(e.target.value)}
              rows={7}
              placeholder="Enter your custom prompt text…"
              className="font-mono text-sm resize-y"
            />
          </div>

          {initial && (
            <div className="flex items-center gap-2">
              <Switch id="override-active" checked={isActive} onCheckedChange={setActive} />
              <Label htmlFor="override-active">Active</Label>
            </div>
          )}
        </div>

        <DialogFooter>
          <DialogClose asChild>
            <Button variant="outline" disabled={saving}>Cancel</Button>
          </DialogClose>
          <Button onClick={save} disabled={saving}>
            {saving ? "Saving…" : "Save Override"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ── Group Prompt Templates ────────────────────────────────────────────────────

function GroupPromptTemplates({ tenantId = 1 }: { tenantId?: number }) {
  const [templates, setTemplates] = useState<GroupPromptTemplateItem[]>([]);
  const [loading, setLoading]     = useState(true);
  const [toggling, setToggling]   = useState<number | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    try   { setTemplates(await api.getGroupPromptTemplates(tenantId)); }
    catch (e) { toast.error(String(e)); }
    finally { setLoading(false); }
  }, [tenantId]);

  useEffect(() => { load(); }, [load]);

  const toggle = async (t: GroupPromptTemplateItem) => {
    setToggling(t.id);
    try {
      if (t.isActivated) {
        await api.deactivateGroupPromptTemplate(t.id, tenantId);
        toast.success("Template deactivated.");
      } else {
        await api.activateGroupPromptTemplate(t.id, tenantId);
        toast.success("Template activated — prompt override added to your tenant.");
      }
      load();
    } catch (e) { toast.error(String(e)); }
    finally { setToggling(null); }
  };

  if (loading) return (
    <div className="space-y-2 mt-4">
      {Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-12 w-full" />)}
    </div>
  );

  if (templates.length === 0) return (
    <EmptyState
      icon={FileText}
      title="No group templates available"
      description="Your tenant is not a member of any group, or no groups have published prompt templates."
    />
  );

  return (
    <div className="space-y-4 mt-4">
      <p className="text-sm text-muted-foreground">
        Prompt override templates shared by your tenant groups. Activate the ones that apply — each creates a tenant prompt override you can manage independently.
      </p>
      <div className="rounded-md border overflow-hidden">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Preview</TableHead>
              <TableHead>Group</TableHead>
              <TableHead>Archetype</TableHead>
              <TableHead>Section</TableHead>
              <TableHead>Mode</TableHead>
              <TableHead className="w-24 text-center">Active</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {templates.map(t => (
              <TableRow key={t.id} className={t.isActivated ? "bg-emerald-500/5" : ""}>
                <TableCell>
                  <p className="text-xs text-muted-foreground line-clamp-2 max-w-xs">{t.customText}</p>
                </TableCell>
                <TableCell>
                  <Badge variant="outline" className="text-xs">{t.groupName}</Badge>
                </TableCell>
                <TableCell>
                  <span className="text-xs font-mono text-muted-foreground">
                    {t.agentType === "*" ? "all archetypes" : t.agentType}
                  </span>
                </TableCell>
                <TableCell>
                  <span className="text-xs font-mono">{t.section}</span>
                </TableCell>
                <TableCell>
                  <MergeModeBadge mode={t.mergeMode} />
                </TableCell>
                <TableCell className="text-center">
                  <Switch
                    checked={t.isActivated}
                    disabled={toggling === t.id}
                    onCheckedChange={() => toggle(t)}
                    className="data-[state=checked]:bg-emerald-500"
                  />
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}
