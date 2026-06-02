import { useState, useEffect, useCallback } from "react";
import {
  api, generateSchedulerFeedbackLink, getFeedbackSettings, upsertFeedbackSettings,
  type AgentSummary, type ScheduledTask, type ScheduledTaskRun, type CreateScheduleDto,
  type ScheduledTaskExport, type ScheduleExportEnvelope, type TenantFeedbackSettings,
} from "@/api";
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
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogClose,
} from "@/components/ui/dialog";
import {
  Sheet, SheetContent, SheetHeader, SheetTitle, SheetDescription,
} from "@/components/ui/sheet";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem,
  DropdownMenuSeparator, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent,
  AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/ui/empty-state";
import { ScrollArea } from "@/components/ui/scroll-area";
import {
  MoreHorizontal, Plus, CalendarClock, Pencil, Trash2,
  Play, History, RefreshCw, ChevronDown, ChevronRight, Copy, Download, Upload, Sparkles,
} from "lucide-react";
import { PromptQuickFixDialog } from "@/components/PromptQuickFixDialog";

const TIMEZONES = [
  "UTC", "America/New_York", "America/Chicago", "America/Denver", "America/Los_Angeles",
  "America/Sao_Paulo", "Europe/London", "Europe/Paris", "Europe/Berlin", "Europe/Moscow",
  "Asia/Dubai", "Asia/Kolkata", "Asia/Singapore", "Asia/Tokyo", "Australia/Sydney",
];

const DAY_NAMES = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

function formatUtc(iso?: string) {
  if (!iso) return "—";
  return new Date(iso).toLocaleString();
}

function StatusBadge({ status }: { status: string }) {
  const colors: Record<string, string> = {
    running: "bg-blue-500/10 text-blue-400 border-blue-500/20",
    success: "bg-emerald-500/10 text-emerald-400 border-emerald-500/20",
    failed:  "bg-red-500/10 text-red-400 border-red-500/20",
    pending: "bg-amber-500/10 text-amber-400 border-amber-500/20",
    skipped: "bg-muted text-muted-foreground",
  };
  return (
    <Badge variant="outline" className={`text-xs font-medium ${colors[status] ?? "bg-muted text-muted-foreground"}`}>
      {status}
    </Badge>
  );
}

function scheduleLabel(task: ScheduledTask) {
  switch (task.scheduleType) {
    case "once":   return `Once · ${task.scheduledAtUtc ? formatUtc(task.scheduledAtUtc) : "—"}`;
    case "hourly": return "Hourly";
    case "daily":  return `Daily · ${task.runAtTime ?? ""}`;
    case "weekly": return `Weekly · ${DAY_NAMES[task.dayOfWeek ?? 0]} ${task.runAtTime ?? ""}`;
    default:       return task.scheduleType;
  }
}

// ── Main component ───────────────────────────────────────────────────────────

export function ScheduledTasks() {
  const [tasks, setTasks]       = useState<ScheduledTask[]>([]);
  const [agents, setAgents]     = useState<AgentSummary[]>([]);
  const [loading, setLoading]   = useState(true);
  const [dialogOpen,  setDialogOpen]  = useState(false);
  const [dialogMode,   setDialogMode]   = useState<"create" | "edit" | "clone">("create");
  const [editTask,     setEditTask]     = useState<ScheduledTask | null>(null);
  const [runsTask,     setRunsTask]     = useState<ScheduledTask | null>(null);
  const [deleteId,     setDeleteId]     = useState<string | null>(null);

  // Import state
  const [importOpen,      setImportOpen]      = useState(false);
  const [importRaw,       setImportRaw]       = useState("");
  const [importParsed,    setImportParsed]    = useState<ScheduledTaskExport[] | null>(null);
  const [importConflicts, setImportConflicts] = useState<string[]>([]);
  const [importSkip,      setImportSkip]      = useState(true);
  const [importing,       setImporting]       = useState(false);

  useEffect(() => {
    api.listAgents().then(setAgents).catch(() => {});
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    try   { setTasks(await api.listSchedules(1)); }
    catch (e: unknown) { toast.error(String(e)); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleDelete = async () => {
    if (!deleteId) return;
    try {
      await api.deleteSchedule(deleteId, 1);
      setTasks(t => t.filter(x => x.id !== deleteId));
      toast.success("Schedule deleted.");
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setDeleteId(null); }
  };

  const handleToggle = async (task: ScheduledTask) => {
    try {
      const updated = await api.setScheduleEnabled(task.id, !task.isEnabled, 1);
      setTasks(t => t.map(x => x.id === task.id ? updated : x));
    } catch (e: unknown) { toast.error(String(e)); }
  };

  const handleTrigger = async (task: ScheduledTask) => {
    try {
      await api.triggerSchedule(task.id, 1);
      toast.success(`Run queued for "${task.name}".`);
    } catch (e: unknown) { toast.error(String(e)); }
  };

  const agentName = (agentId: string) => {
    const a = agents.find(x => x.id === agentId);
    return a ? (a.displayName || a.name) : agentId;
  };

  const openCreate = () => { setDialogMode("create"); setEditTask(null); setDialogOpen(true); };
  const openEdit   = (task: ScheduledTask) => { setDialogMode("edit"); setEditTask(task); setDialogOpen(true); };
  const openClone  = (task: ScheduledTask) => { setDialogMode("clone"); setEditTask(task); setDialogOpen(true); };

  // ── Export helpers ──────────────────────────────────────────────────────
  const toExportTask = (t: ScheduledTask): ScheduledTaskExport => ({
    agentId: t.agentId, name: t.name, description: t.description,
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
      type: "tenant-schedules", tasks: tasks.map(toExportTask),
    };
    triggerDownload(JSON.stringify(envelope, null, 2), `schedules-all-${dateSlug()}.json`);
  };

  const handleExportOne = (task: ScheduledTask) => {
    const envelope: ScheduleExportEnvelope = {
      version: "1", exportedAt: new Date().toISOString(),
      type: "tenant-schedules", tasks: [toExportTask(task)],
    };
    const slug = task.name.replace(/\s+/g, "-").toLowerCase();
    triggerDownload(JSON.stringify(envelope, null, 2), `schedule-${slug}-${dateSlug()}.json`);
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
    if (parsed.type && parsed.type !== "tenant-schedules") {
      toast.error(`Wrong file type: expected "tenant-schedules", got "${parsed.type}".`); return;
    }
    const existing = new Set(tasks.map(t => t.name.toLowerCase()));
    const conflicts = parsed.tasks
      .filter(t => existing.has((t.name ?? "").toLowerCase()))
      .map(t => t.name);
    setImportParsed(parsed.tasks as ScheduledTaskExport[]);
    setImportConflicts(conflicts);
  };

  const handleImportSubmit = async () => {
    if (!importParsed?.length) return;
    setImporting(true);
    try {
      const result = await api.importSchedules({ tasks: importParsed, skipConflicts: importSkip }, 1);
      toast.success(`Imported: ${result.created} created, ${result.skipped} skipped.`);
      closeImport();
      load();
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setImporting(false); }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-xl font-semibold">Scheduled Tasks</h2>
          <p className="text-sm text-muted-foreground">
            Schedule agents to run tasks on a recurring or one-time basis.
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button size="sm" variant="outline" onClick={load} className="h-8">
            <RefreshCw className="h-3.5 w-3.5" />
          </Button>
          <Button size="sm" variant="outline" onClick={handleExportAll} className="h-8" disabled={tasks.length === 0}>
            <Download className="h-3.5 w-3.5 mr-1" /> Export All
          </Button>
          <Button size="sm" variant="outline" onClick={() => setImportOpen(true)} className="h-8">
            <Upload className="h-3.5 w-3.5 mr-1" /> Import
          </Button>
          <Button size="sm" onClick={openCreate} className="h-8">
            <Plus className="h-3.5 w-3.5 mr-1" /> New Schedule
          </Button>
        </div>
      </div>

      {loading ? (
        <div className="rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Agent</TableHead>
                <TableHead>Schedule</TableHead>
                <TableHead>Next Run</TableHead>
                <TableHead>Enabled</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {Array.from({ length: 3 }).map((_, i) => (
                <TableRow key={i}>
                  <TableCell><Skeleton className="h-4 w-32" /></TableCell>
                  <TableCell><Skeleton className="h-4 w-24" /></TableCell>
                  <TableCell><Skeleton className="h-4 w-28" /></TableCell>
                  <TableCell><Skeleton className="h-4 w-28" /></TableCell>
                  <TableCell><Skeleton className="h-5 w-9" /></TableCell>
                  <TableCell><Skeleton className="h-5 w-5" /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : tasks.length === 0 ? (
        <EmptyState
          icon={CalendarClock}
          title="No schedules"
          description="Create a schedule to automate agent tasks."
          action={{ label: "New Schedule", onClick: openCreate }}
        />
      ) : (
        <div className="rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Agent</TableHead>
                <TableHead>Schedule</TableHead>
                <TableHead>Next Run</TableHead>
                <TableHead className="w-20 text-center">Enabled</TableHead>
                <TableHead className="w-10" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {tasks.map(task => (
                <TableRow key={task.id}>
                  <TableCell>
                    <div className="flex items-center gap-2">
                      <div className="font-medium text-sm">{task.name}</div>
                      {task.lastRunStatus === "success" && (
                        <Badge variant="outline" className="text-[10px] px-1 py-0 border-emerald-500 text-emerald-600">ok</Badge>
                      )}
                      {task.lastRunStatus === "failed" && (
                        <Badge variant="outline" className="text-[10px] px-1 py-0 border-red-500 text-red-600">failed</Badge>
                      )}
                      {task.lastRunStatus === "skipped" && (
                        <Badge variant="outline" className="text-[10px] px-1 py-0 border-yellow-500 text-yellow-600">skipped</Badge>
                      )}
                    </div>
                    {task.description && (
                      <div className="text-xs text-muted-foreground mt-0.5 line-clamp-1">
                        {task.description}
                      </div>
                    )}
                  </TableCell>
                  <TableCell className="text-sm">{agentName(task.agentId)}</TableCell>
                  <TableCell>
                    <div className="text-xs text-foreground/80">{scheduleLabel(task)}</div>
                    <div className="text-xs text-muted-foreground/60">{task.timeZoneId}</div>
                  </TableCell>
                  <TableCell>
                    {task.isEnabled && task.nextRunUtc ? (
                      <span className="text-xs text-emerald-500">{formatUtc(task.nextRunUtc)}</span>
                    ) : (
                      <span className="text-xs text-muted-foreground">—</span>
                    )}
                  </TableCell>
                  <TableCell className="text-center">
                    <Switch
                      checked={task.isEnabled}
                      onCheckedChange={() => handleToggle(task)}
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
                        <DropdownMenuItem onClick={() => openEdit(task)}>
                          <Pencil className="h-3.5 w-3.5 mr-2" /> Edit
                        </DropdownMenuItem>
                        <DropdownMenuItem onClick={() => openClone(task)}>
                          <Copy className="h-3.5 w-3.5 mr-2" /> Clone
                        </DropdownMenuItem>
                        <DropdownMenuItem onClick={() => handleExportOne(task)}>
                          <Download className="h-3.5 w-3.5 mr-2" /> Export
                        </DropdownMenuItem>
                        <DropdownMenuItem onClick={() => handleTrigger(task)}>
                          <Play className="h-3.5 w-3.5 mr-2" /> Run Now
                        </DropdownMenuItem>
                        <DropdownMenuItem onClick={() => setRunsTask(task)}>
                          <History className="h-3.5 w-3.5 mr-2" /> Run History
                        </DropdownMenuItem>
                        <DropdownMenuSeparator />
                        <DropdownMenuItem
                          onClick={() => setDeleteId(task.id)}
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

      <TaskDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        mode={dialogMode}
        source={editTask}
        agents={agents}
        onSaved={() => { setDialogOpen(false); load(); }}
      />

      <RunHistorySheet
        task={runsTask}
        agentName={runsTask ? agentName(runsTask.agentId) : ""}
        onClose={() => setRunsTask(null)}
      />

      {/* ── Import dialog ───────────────────────────────────────────────── */}
      <Dialog open={importOpen} onOpenChange={v => { if (!v) closeImport(); else setImportOpen(true); }}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>Import Schedules</DialogTitle>
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
              <Textarea
                className="font-mono text-xs h-32 resize-y"
                placeholder='{"version":"1","type":"tenant-schedules","tasks":[...]}'
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
            <DialogClose asChild><Button variant="outline">Cancel</Button></DialogClose>
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

      <AlertDialog open={deleteId !== null} onOpenChange={(open: boolean) => !open && setDeleteId(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete schedule?</AlertDialogTitle>
            <AlertDialogDescription>
              This schedule and all run history will be permanently removed.
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

      {/* ── Feedback link settings ──────────────────────────────────────── */}
      <FeedbackSettingsPanel />
    </div>
  );
}

// ── Feedback settings panel ───────────────────────────────────────────────────

function FeedbackSettingsPanel() {
  const [expanded, setExpanded] = useState(false);
  const [settings, setSettings] = useState<TenantFeedbackSettings>({
    enableFeedbackLinks: true,
    feedbackLinkBaseUrl: "",
    expiryDays: 30,
  });
  const [loading, setLoading]   = useState(false);
  const [saving,  setSaving]    = useState(false);

  useEffect(() => {
    if (!expanded) return;
    setLoading(true);
    getFeedbackSettings(1)
      .then(setSettings)
      .catch(() => {})
      .finally(() => setLoading(false));
  }, [expanded]);

  const save = async () => {
    setSaving(true);
    try {
      await upsertFeedbackSettings(settings, 1);
      toast.success("Feedback settings saved.");
    } catch (e: unknown) {
      toast.error(String(e));
    } finally {
      setSaving(false);
    }
  };

  return (
    <div className="rounded-md border">
      <button
        type="button"
        className="w-full flex items-center justify-between px-4 py-3 text-sm font-medium hover:bg-muted/40 transition-colors"
        onClick={() => setExpanded(v => !v)}
      >
        <span>Feedback Link Settings</span>
        {expanded ? <ChevronDown className="h-4 w-4 text-muted-foreground" /> : <ChevronRight className="h-4 w-4 text-muted-foreground" />}
      </button>

      {expanded && (
        <div className="border-t px-4 py-4 space-y-4">
          <p className="text-xs text-muted-foreground">
            Feedback links let email recipients rate and correct scheduler run outputs.
            The portal base URL is embedded in notification emails — it must be reachable by recipients.
          </p>

          {loading ? (
            <div className="space-y-3">
              <Skeleton className="h-5 w-48" />
              <Skeleton className="h-9 w-full" />
              <Skeleton className="h-9 w-32" />
            </div>
          ) : (
            <>
              <div className="flex items-center gap-3">
                <Switch
                  id="feedback-enabled"
                  checked={settings.enableFeedbackLinks}
                  onCheckedChange={v => setSettings(s => ({ ...s, enableFeedbackLinks: v }))}
                />
                <Label htmlFor="feedback-enabled">Enable feedback links in notification emails</Label>
              </div>

              <div className="space-y-1.5">
                <Label htmlFor="feedback-base-url">Portal Base URL</Label>
                <Input
                  id="feedback-base-url"
                  value={settings.feedbackLinkBaseUrl}
                  onChange={e => setSettings(s => ({ ...s, feedbackLinkBaseUrl: e.target.value }))}
                  placeholder="https://app.example.com"
                  disabled={!settings.enableFeedbackLinks}
                />
                <p className="text-xs text-muted-foreground">
                  The public URL of this portal, e.g. <code>https://app.example.com</code>.
                  Used to build the <code>&#123;&#123;feedback_url&#125;&#125;</code> link inserted in emails.
                </p>
              </div>

              <div className="space-y-1.5 w-40">
                <Label htmlFor="feedback-expiry">Link Expiry (days)</Label>
                <Input
                  id="feedback-expiry"
                  type="number"
                  min={1}
                  max={365}
                  value={settings.expiryDays}
                  onChange={e => setSettings(s => ({ ...s, expiryDays: Number(e.target.value) }))}
                  disabled={!settings.enableFeedbackLinks}
                />
              </div>

              <div className="flex justify-end">
                <Button size="sm" onClick={save} disabled={saving}>
                  {saving ? "Saving…" : "Save"}
                </Button>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}

// ── Task form dialog ──────────────────────────────────────────────────────────

interface TaskDialogProps {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  mode: "create" | "edit" | "clone";
  source: ScheduledTask | null;
  agents: AgentSummary[];
  onSaved: () => void;
}

function TaskDialog({ open, onOpenChange, mode, source, agents, onSaved }: TaskDialogProps) {
  const [agentId,       setAgentId]       = useState("");
  const [name,          setName]          = useState("");
  const [description,   setDescription]   = useState("");
  const [scheduleType,  setScheduleType]  = useState("once");
  const [scheduledAt,   setScheduledAt]   = useState("");
  const [runAtTime,     setRunAtTime]     = useState("09:00");
  const [dayOfWeek,     setDayOfWeek]     = useState<number>(1);
  const [timeZoneId,    setTimeZoneId]    = useState("UTC");
  const [payloadType,   setPayloadType]   = useState("prompt");
  const [promptText,    setPromptText]    = useState("");
  const [parametersRaw, setParametersRaw] = useState('{\n  "variable": "value"\n}');
  const [isEnabled,     setIsEnabled]     = useState(true);
  const [notifyEmails,  setNotifyEmails]  = useState("");
  const [notifyOn,      setNotifyOn]      = useState<string | undefined>(undefined);
  const [successKeywords, setSuccessKeywords] = useState("");
  const [saving,        setSaving]        = useState(false);
  const [quickFixOpen,  setQuickFixOpen]  = useState(false);

  useEffect(() => {
    if (!open) return;
    const isClone = mode === "clone";
    setAgentId(source?.agentId ?? (agents[0]?.id ?? ""));
    // Clone: append suffix and clear one-time date; Edit/Create: use source as-is
    setName(source ? (isClone ? `${source.name} (copy)` : source.name) : "");
    setDescription(source?.description ?? "");
    setScheduleType(source?.scheduleType ?? "once");
    // Gap 2 fix: never carry a past one-time date into a clone
    setScheduledAt(
      !isClone && source?.scheduledAtUtc ? source.scheduledAtUtc.slice(0, 16) : ""
    );
    setRunAtTime(source?.runAtTime ?? "09:00");
    setDayOfWeek(source?.dayOfWeek ?? 1);
    setTimeZoneId(source?.timeZoneId ?? "UTC");
    setPayloadType(source?.payloadType ?? "prompt");
    setPromptText(source?.promptText ?? "");
    setParametersRaw(
      source?.parametersJson
        ? (() => { try { return JSON.stringify(JSON.parse(source.parametersJson), null, 2); } catch { return source.parametersJson; } })()
        : '{\n  "variable": "value"\n}'
    );
    // Gap 1 fix: clone always starts disabled
    setIsEnabled(isClone ? false : (source?.isEnabled ?? true));
    setNotifyEmails(source?.notifyEmails ?? "");
    setNotifyOn(source?.notifyOn ?? undefined);
    setSuccessKeywords(source?.successKeywords ?? "");
  }, [open, mode, source, agents]);

  const save = async () => {
    if (!agentId)            { toast.error("Select an agent."); return; }
    if (!name.trim())        { toast.error("Name is required."); return; }
    if (!promptText.trim())  { toast.error("Prompt text is required."); return; }
    if (scheduleType === "once" && !scheduledAt) { toast.error("Select a date/time."); return; }

    let parsedParams: string | undefined;
    if (payloadType === "template") {
      try { JSON.parse(parametersRaw); parsedParams = parametersRaw; }
      catch { toast.error("Parameters JSON is not valid."); return; }
    }

    setSaving(true);
    try {
      const dto: CreateScheduleDto = {
        agentId,
        name:           name.trim(),
        description:    description.trim() || undefined,
        scheduleType,
        scheduledAtUtc: scheduleType === "once" ? new Date(scheduledAt).toISOString() : undefined,
        runAtTime:      (scheduleType === "daily" || scheduleType === "weekly") ? runAtTime : undefined,
        dayOfWeek:      scheduleType === "weekly" ? dayOfWeek : undefined,
        timeZoneId,
        payloadType,
        promptText:     promptText.trim(),
        parametersJson: parsedParams,
        isEnabled,
        notifyEmails: notifyEmails.trim() || undefined,
        notifyOn:     notifyOn || undefined,
        successKeywords: successKeywords.trim() || undefined,
      };
      if (mode === "edit") {
        await api.updateSchedule(source!.id, dto, 1);
      } else {
        await api.createSchedule(dto, 1);
      }
      toast.success(mode === "edit" ? "Schedule updated." : mode === "clone" ? "Schedule cloned." : "Schedule created.");
      onSaved();
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setSaving(false); }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-2xl max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{mode === "edit" ? "Edit Schedule" : mode === "clone" ? "Clone Schedule" : "New Schedule"}</DialogTitle>
        </DialogHeader>

        <div className="grid gap-4 py-2">
          {/* Agent & name */}
          <div className="grid grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label>Agent *</Label>
              <Select value={agentId} onValueChange={setAgentId}>
                <SelectTrigger><SelectValue placeholder="Select agent" /></SelectTrigger>
                <SelectContent>
                  {agents.map(a => (
                    <SelectItem key={a.id} value={a.id}>{a.displayName || a.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label>Schedule Name *</Label>
              <Input value={name} onChange={e => setName(e.target.value)} placeholder="Daily report" />
            </div>
          </div>

          <div className="space-y-1.5">
            <Label>Description</Label>
            <Input
              value={description}
              onChange={e => setDescription(e.target.value)}
              placeholder="Optional description"
            />
          </div>

          {/* Schedule type */}
          <div className="grid grid-cols-2 gap-3 sm:grid-cols-4">
            <div className="space-y-1.5">
              <Label>Schedule Type</Label>
              <Select value={scheduleType} onValueChange={setScheduleType}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="once">Once</SelectItem>
                  <SelectItem value="hourly">Hourly</SelectItem>
                  <SelectItem value="daily">Daily</SelectItem>
                  <SelectItem value="weekly">Weekly</SelectItem>
                </SelectContent>
              </Select>
            </div>

            {scheduleType === "once" && (
              <div className="space-y-1.5 col-span-2">
                <Label>Run At (local) *</Label>
                <Input
                  type="datetime-local"
                  value={scheduledAt}
                  onChange={e => setScheduledAt(e.target.value)}
                />
              </div>
            )}

            {(scheduleType === "daily" || scheduleType === "weekly") && (
              <div className="space-y-1.5">
                <Label>Time of Day *</Label>
                <Input type="time" value={runAtTime} onChange={e => setRunAtTime(e.target.value)} />
              </div>
            )}

            {scheduleType === "weekly" && (
              <div className="space-y-1.5">
                <Label>Day of Week</Label>
                <Select value={String(dayOfWeek)} onValueChange={v => setDayOfWeek(Number(v))}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    {DAY_NAMES.map((d, i) => (
                      <SelectItem key={i} value={String(i)}>{d}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            )}

            <div className="space-y-1.5">
              <Label>Timezone</Label>
              <Select value={timeZoneId} onValueChange={setTimeZoneId}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  {TIMEZONES.map(tz => <SelectItem key={tz} value={tz}>{tz}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
          </div>

          {/* Payload */}
          <div className="space-y-1.5">
            <Label>Payload Type</Label>
            <Select value={payloadType} onValueChange={setPayloadType}>
              <SelectTrigger className="w-48"><SelectValue /></SelectTrigger>
              <SelectContent>
                <SelectItem value="prompt">Fixed Prompt</SelectItem>
                <SelectItem value="template">Template (&#123;&#123;var&#125;&#125;)</SelectItem>
              </SelectContent>
            </Select>
          </div>

          <div className="space-y-1.5">
            <div className="flex items-center justify-between">
              <Label>
                {payloadType === "template"
                  ? "Prompt Template * (use {{variable}} for substitutions)"
                  : "Prompt Text *"}
              </Label>
              {agentId && (
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  onClick={() => setQuickFixOpen(true)}
                  className="gap-1.5 h-7 text-xs"
                >
                  <Sparkles className="size-3 text-amber-500" />
                  Quick Fix
                </Button>
              )}
            </div>
            <Textarea
              value={promptText}
              onChange={e => setPromptText(e.target.value)}
              rows={4}
              className="resize-y"
              placeholder={
                payloadType === "template"
                  ? "Generate a {{reportType}} summary for {{period}}."
                  : "Summarise today's key events and flag any anomalies."
              }
            />
          </div>

          <PromptQuickFixDialog
            onImprove={(instruction) =>
              api.improvePrompt(agentId, instruction, promptText).then((r) => r.improvedPrompt)
            }
            currentPrompt={promptText}
            open={quickFixOpen}
            onOpenChange={setQuickFixOpen}
            onAccept={(improved) => setPromptText(improved)}
          />

          {payloadType === "template" && (
            <div className="space-y-1.5">
              <Label>Template Parameters (JSON)</Label>
              <Textarea
                value={parametersRaw}
                onChange={e => setParametersRaw(e.target.value)}
                rows={4}
                className="font-mono text-sm resize-y"
                placeholder={'{\n  "reportType": "weekly"\n}'}
              />
            </div>
          )}

          <div className="flex items-center gap-2">
            <Switch id="task-enabled" checked={isEnabled} onCheckedChange={setIsEnabled} />
            <Label htmlFor="task-enabled">Enabled (run according to schedule)</Label>
          </div>

          <div className="space-y-3 rounded-md border p-3">
            <Label className="text-sm font-medium">Notification</Label>
            <div>
              <Label className="text-xs text-muted-foreground">Notify emails (comma-separated)</Label>
              <Input
                value={notifyEmails}
                onChange={e => setNotifyEmails(e.target.value)}
                placeholder="user@example.com, ops@example.com"
              />
            </div>
            <div>
              <Label className="text-xs text-muted-foreground">Notify when</Label>
              <select
                className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm"
                value={notifyOn ?? ""}
                onChange={e => setNotifyOn(e.target.value || undefined)}
              >
                <option value="">— disabled —</option>
                <option value="failure">On failure</option>
                <option value="success">On success</option>
                <option value="always">Always</option>
              </select>
            </div>
            <div>
              <Label className="text-xs text-muted-foreground">Success confirmation keywords (comma-separated)</Label>
              <Input
                value={successKeywords}
                onChange={e => setSuccessKeywords(e.target.value)}
                placeholder="email sent, sent successfully, completed"
              />
              <p className="text-xs text-muted-foreground mt-1">If set, at least one phrase must appear in the final agent response; otherwise the run is marked as failed.</p>
            </div>
          </div>
        </div>

        <DialogFooter>
          <DialogClose asChild>
            <Button variant="outline" disabled={saving}>Cancel</Button>
          </DialogClose>
          <Button onClick={save} disabled={saving}>
            {saving ? "Saving…" : mode === "edit" ? "Save Changes" : mode === "clone" ? "Clone Schedule" : "Create Schedule"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ── Run history sheet ─────────────────────────────────────────────────────────

interface RunHistorySheetProps {
  task: ScheduledTask | null;
  agentName: string;
  onClose: () => void;
}

function RunHistorySheet({ task, agentName, onClose }: RunHistorySheetProps) {
  const [runs, setRuns]       = useState<ScheduledTaskRun[]>([]);
  const [copyingLink, setCopyingLink] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  const load = useCallback(async () => {
    if (!task) return;
    setLoading(true);
    try   { setRuns(await api.getScheduleRuns(task.id, 1, 50)); }
    catch (e: unknown) { toast.error(String(e)); }
    finally { setLoading(false); }
  }, [task]);

  useEffect(() => { if (task) load(); }, [task, load]);

  const toggleExpand = (id: string) =>
    setExpanded(s => { const n = new Set(s); n.has(id) ? n.delete(id) : n.add(id); return n; });

  return (
    <Sheet open={task !== null} onOpenChange={(open: boolean) => !open && onClose()}>
      <SheetContent className="w-full sm:max-w-2xl overflow-y-auto">
        <SheetHeader className="mb-4">
          <SheetTitle>Run History — {task?.name}</SheetTitle>
          <SheetDescription>
            {agentName} · {task?.scheduleType} · {task?.timeZoneId}
          </SheetDescription>
        </SheetHeader>

        <div className="flex justify-end mb-3">
          <Button size="sm" variant="outline" onClick={load} disabled={loading} className="h-8">
            <RefreshCw className={`h-3.5 w-3.5 mr-1.5 ${loading ? "animate-spin" : ""}`} />
            Refresh
          </Button>
        </div>

        {loading ? (
          <div className="space-y-2">
            {Array.from({ length: 4 }).map((_, i) => (
              <Skeleton key={i} className="h-14 w-full" />
            ))}
          </div>
        ) : runs.length === 0 ? (
          <p className="text-sm text-muted-foreground text-center py-12">No runs yet.</p>
        ) : (
          <ScrollArea className="h-[calc(100vh-220px)]">
            <div className="space-y-2 pr-1">
              {runs.map(run => (
                <div key={run.id} className="rounded-md border bg-card p-3">
                  <div className="flex items-center gap-2 flex-wrap">
                    <StatusBadge status={run.status} />
                    <span className="text-xs text-muted-foreground">
                      #{run.attemptNumber} · Due {formatUtc(run.scheduledForUtc)}
                    </span>
                    {run.startedAtUtc && (
                      <span className="text-xs text-muted-foreground">
                        Started {formatUtc(run.startedAtUtc)}
                      </span>
                    )}
                    {run.durationMs !== undefined && run.durationMs !== null && (
                      <span className="text-xs text-muted-foreground ml-auto">
                        {run.durationMs < 1000
                          ? `${run.durationMs}ms`
                          : `${(run.durationMs / 1000).toFixed(1)}s`}
                      </span>
                    )}
                    {(run.responseText || run.errorMessage) && (
                      <Button
                        variant="ghost"
                        size="sm"
                        className="h-6 px-2 text-xs ml-auto"
                        onClick={() => toggleExpand(run.id)}
                      >
                        {expanded.has(run.id)
                          ? <><ChevronDown className="h-3 w-3 mr-1" /> Collapse</>
                          : <><ChevronRight className="h-3 w-3 mr-1" /> Expand</>}
                      </Button>
                    )}
                  </div>

                  {expanded.has(run.id) && (
                    <div className="mt-2 space-y-2">
                      {run.errorMessage && (
                        <pre className="rounded bg-destructive/10 border border-destructive/20 text-destructive px-3 py-2 text-xs whitespace-pre-wrap break-words">
                          {run.errorMessage}
                        </pre>
                      )}
                      {run.responseText && (
                        <pre className="rounded bg-muted px-3 py-2 text-xs whitespace-pre-wrap break-words text-foreground">
                          {run.responseText}
                        </pre>
                      )}
                      {run.status === "success" && (
                        <Button
                          size="sm"
                          variant="outline"
                          className="text-xs h-7"
                          disabled={copyingLink === run.id}
                          onClick={async () => {
                            setCopyingLink(run.id);
                            try {
                              const url = await generateSchedulerFeedbackLink(
                                run.id, task.id, task.tenantId
                              );
                              await navigator.clipboard.writeText(url);
                              toast.success("Feedback link copied to clipboard");
                            } catch {
                              toast.error("Could not generate feedback link");
                            } finally {
                              setCopyingLink(null);
                            }
                          }}
                        >
                          {copyingLink === run.id ? "Copying…" : "📋 Copy Feedback Link"}
                        </Button>
                      )}
                    </div>
                  )}
                </div>
              ))}
            </div>
          </ScrollArea>
        )}
      </SheetContent>
    </Sheet>
  );
}
