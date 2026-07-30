/**
 * ScheduleTaskEditor — full-page form for creating, editing, and cloning scheduled tasks.
 *
 * Routes:
 *   /schedules/new        — create a new schedule
 *   /schedules/:id/edit   — edit an existing schedule
 *   /schedules/:id/clone  — clone an existing schedule (pass `cloneMode`)
 */
import { useEffect, useState } from "react";
import { useNavigate, useParams } from "react-router";
import { toast } from "sonner";
import { ArrowLeft, Save, Sparkles } from "lucide-react";
import {
  api,
  type AgentSummary, type ScheduledTask, type CreateScheduleDto, type UserProfile,
} from "@/api";
import { TIMEZONES, DAY_NAMES } from "@/lib/scheduleConstants";
import { PromptQuickFixDialog } from "@/components/PromptQuickFixDialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

export function ScheduleTaskEditor({ cloneMode = false }: { cloneMode?: boolean }) {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const mode: "create" | "edit" | "clone" = !id ? "create" : cloneMode ? "clone" : "edit";

  const [agents, setAgents]     = useState<AgentSummary[]>([]);
  const [users,  setUsers]      = useState<UserProfile[]>([]);
  const [source, setSource]     = useState<ScheduledTask | null>(null);
  const [loading, setLoading]   = useState(!!id);

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
  const [runAsUserId,   setRunAsUserId]   = useState("");
  const [saving,        setSaving]        = useState(false);
  const [quickFixOpen,  setQuickFixOpen]  = useState(false);

  useEffect(() => {
    api.listAgents().then(setAgents).catch(() => setAgents([]));
    api.listUserProfiles(1).then(setUsers).catch(() => setUsers([]));
  }, []);

  useEffect(() => {
    if (!id) { setLoading(false); return; }
    setLoading(true);
    api.getSchedule(id, 1)
      .then(setSource)
      .catch((e: unknown) => {
        toast.error(String(e));
        navigate("/schedules");
      })
      .finally(() => setLoading(false));
  }, [id, navigate]);

  useEffect(() => {
    if (loading) return;
    const isClone = mode === "clone";
    setAgentId(source?.agentId ?? (agents[0]?.id ?? ""));
    // Clone: append suffix and clear one-time date; Edit/Create: use source as-is
    setName(source ? (isClone ? `${source.name} (copy)` : source.name) : "");
    setDescription(source?.description ?? "");
    setScheduleType(source?.scheduleType ?? "once");
    // Never carry a past one-time date into a clone
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
    // Clone always starts disabled
    setIsEnabled(isClone ? false : (source?.isEnabled ?? true));
    setNotifyEmails(source?.notifyEmails ?? "");
    setNotifyOn(source?.notifyOn ?? undefined);
    setSuccessKeywords(source?.successKeywords ?? "");
    setRunAsUserId(source?.runAsUserId ?? "");
  }, [loading, mode, source, agents]);

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
      const runAsUser = runAsUserId ? users.find(u => u.userId === runAsUserId) : undefined;
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
        runAsUserId:    runAsUserId || "",
        runAsUserEmail: runAsUser?.email || undefined,
        runAsUserLabel: runAsUser ? (runAsUser.displayName || runAsUser.email || runAsUser.userId) : undefined,
      };
      if (mode === "edit") {
        await api.updateSchedule(source!.id, dto, 1);
      } else {
        await api.createSchedule(dto, 1);
      }
      toast.success(mode === "edit" ? "Schedule updated." : mode === "clone" ? "Schedule cloned." : "Schedule created.");
      navigate("/schedules");
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setSaving(false); }
  };

  if (loading) {
    return <div className="p-8 text-sm text-muted-foreground">Loading…</div>;
  }

  return (
    <div className="p-6 max-w-4xl mx-auto space-y-6">
      {/* Header */}
      <div className="flex items-center gap-4">
        <Button variant="ghost" size="sm" onClick={() => navigate("/schedules")} className="gap-1 -ml-1">
          <ArrowLeft className="size-4" /> Back
        </Button>
        <div>
          <h1 className="text-2xl font-semibold">
            {mode === "edit" ? "Edit Schedule" : mode === "clone" ? "Clone Schedule" : "New Schedule"}
          </h1>
          <p className="text-sm text-muted-foreground mt-0.5">
            Schedule an agent to run automatically on a recurring or one-time basis.
          </p>
        </div>
      </div>

      {/* Agent & name */}
      <Card>
        <CardHeader>
          <CardTitle>Agent & Name</CardTitle>
          <CardDescription>Which agent runs, and what to call this schedule.</CardDescription>
        </CardHeader>
        <CardContent className="grid grid-cols-1 sm:grid-cols-2 gap-4">
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
          <div className="space-y-1.5 sm:col-span-2">
            <Label>Description</Label>
            <Input
              value={description}
              onChange={e => setDescription(e.target.value)}
              placeholder="Optional description"
            />
          </div>
        </CardContent>
      </Card>

      {/* Timing */}
      <Card>
        <CardHeader>
          <CardTitle>Timing</CardTitle>
          <CardDescription>When and how often this schedule runs.</CardDescription>
        </CardHeader>
        <CardContent className="grid grid-cols-2 gap-4 sm:grid-cols-4">
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

          <div className="flex items-center gap-2 sm:col-span-4">
            <Switch id="task-enabled" checked={isEnabled} onCheckedChange={setIsEnabled} />
            <Label htmlFor="task-enabled">Enabled (run according to schedule)</Label>
          </div>
        </CardContent>
      </Card>

      {/* Prompt */}
      <Card>
        <CardHeader>
          <CardTitle>Prompt</CardTitle>
          <CardDescription>What the agent should do when it runs.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
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
              rows={6}
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
        </CardContent>
      </Card>

      {/* Run as user */}
      <Card>
        <CardHeader>
          <CardTitle>Run As User</CardTitle>
          <CardDescription>
            Optionally run this schedule under a specific user's identity for shared MCP credential selection.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-2">
          <select
            className="flex h-9 w-full rounded-md border border-input bg-background px-3 py-1 text-sm"
            value={runAsUserId}
            onChange={e => setRunAsUserId(e.target.value)}
          >
            <option value="">System (default — no user-group credentials)</option>
            {users.map(u => (
              <option key={u.userId} value={u.userId}>
                {(u.displayName || u.email || u.userId)}{u.email && u.email !== (u.displayName || "") ? ` · ${u.email}` : ""}
              </option>
            ))}
          </select>
          <p className="text-xs text-muted-foreground">
            When set, the task runs under this user's identity so shared MCP servers use that user's
            user-group credentials. Leave as System to run without user-group credential selection.
          </p>
        </CardContent>
      </Card>

      {/* Notifications */}
      <Card>
        <CardHeader>
          <CardTitle>Notifications</CardTitle>
          <CardDescription>Get emailed about run outcomes.</CardDescription>
        </CardHeader>
        <CardContent className="space-y-3">
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
        </CardContent>
      </Card>

      {/* Actions */}
      <div className="flex justify-end gap-3 pb-8">
        <Button variant="outline" onClick={() => navigate("/schedules")} disabled={saving}>Cancel</Button>
        <Button onClick={save} disabled={saving} className="gap-2">
          <Save className="size-4" />
          {saving ? "Saving…" : mode === "edit" ? "Save Changes" : mode === "clone" ? "Clone Schedule" : "Create Schedule"}
        </Button>
      </div>
    </div>
  );
}
