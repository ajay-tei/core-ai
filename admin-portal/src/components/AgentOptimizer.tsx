import { useEffect, useState } from "react";
import { useParams, useSearchParams, Link } from "react-router";
import type {
  OptimizationRunSummary, OptimizationRunDetail, OptimizationSuggestion,
  OptimizationScheduleConfig, SessionSummary, OptimizationRunListParams,
} from "@/api";
import {
  triggerOptimizationRun, triggerSessionOptimization,
  getOptimizationRunsPaged, getOptimizationRunsBySession, getOptimizationRunDetail,
  getOptimizationSuggestions, reviewSuggestion,
  getOptimizationSchedule, saveOptimizationSchedule,
  api,
} from "@/api";
import { usePagedList } from "@/hooks/usePagedList";
import { ListPagination } from "@/components/ui/list-toolbar";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Badge } from "@/components/ui/badge";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from "@/components/ui/table";
const QUALITY_THRESHOLD = 0.7;
const TRUNCATE_CHARS = 300;

function ExpandableText({ text }: { text: string })
{
  const [expanded, setExpanded] = useState(false);
  const long = text.length > TRUNCATE_CHARS;
  const shown = expanded || !long ? text : text.slice(0, TRUNCATE_CHARS) + "…";
  return (
    <div>
      <code className="text-xs bg-muted px-1.5 py-1 rounded block whitespace-pre-wrap break-words font-mono leading-relaxed text-foreground">
        {shown}
      </code>
      {long && (
        <button onClick={() => setExpanded(e => !e)}
          className="mt-1 text-xs text-primary hover:underline">
          {expanded ? "Show less" : `Show more (${text.length - TRUNCATE_CHARS} more chars)`}
        </button>
      )}
    </div>
  );
}

function SuggestionCard({
  s, onApprove, onReject,
}: {
  s: OptimizationSuggestion;
  onApprove: () => void;
  onReject: () => void;
})
{
  return (
    <div className="rounded-md border p-3 space-y-2">
      <div className="flex justify-between items-start gap-2">
        <div className="flex items-center gap-2 flex-wrap">
          <Badge variant="outline">{s.type}</Badge>
          <ConfidenceBadge value={s.confidence} />
          <Badge variant={s.status === "Approved" ? "default" : "secondary"}>{s.status}</Badge>
          <span className="text-xs text-muted-foreground">{s.fieldName}</span>
        </div>
        <div className="flex gap-1.5 shrink-0">
          {s.status === "Pending" && (
            <Button size="sm" variant="secondary" onClick={onApprove}>Approve</Button>
          )}
          <Button size="sm" variant="outline" onClick={onReject}>Reject</Button>
        </div>
      </div>
      {s.currentValue && (
        <div className="text-xs text-muted-foreground">
          Current: <code className="bg-muted px-1 rounded">{s.currentValue.slice(0, 80)}{s.currentValue.length > 80 ? "…" : ""}</code>
        </div>
      )}
      <div className="text-xs font-medium text-muted-foreground">Suggested:</div>
      <ExpandableText text={s.suggestedValue} />
      <div className="text-xs text-muted-foreground pt-1">{s.reasoning}</div>
    </div>
  );
}

function QualityBar({ value }: { value: number | null | undefined })
{
  const v    = value ?? null;
  const pct  = ((v ?? 0) * 100).toFixed(0);
  const good = v != null && v >= QUALITY_THRESHOLD;
  return (
    <div className="text-center">
      <div className={`text-2xl font-bold ${good ? "text-green-600" : v != null ? "text-red-600" : "text-muted-foreground"}`}>
        {v != null ? v.toFixed(2) : "—"}
      </div>
      <div className="mt-1 h-1.5 rounded-full bg-muted">
        <div className={`h-1.5 rounded-full ${good ? "bg-green-500" : "bg-red-500"}`}
          style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}

function ConfidenceBadge({ value }: { value: number })
{
  const variant = value >= 0.8 ? "default" : "secondary";
  return <Badge variant={variant}>{Math.round(value * 100)}%</Badge>;
}

function StatusBadge({ status }: { status: string })
{
  const variant = status === "completed" ? "default"
    : status === "failed" ? "destructive"
    : "secondary";
  return <Badge variant={variant}>{status}</Badge>;
}

export default function AgentOptimizer()
{
  const { id: agentId }  = useParams<{ id: string }>();
  const [searchParams]   = useSearchParams();

  const [detail, setDetail]           = useState<OptimizationRunDetail | null>(null);
  const [suggestions, setSuggestions] = useState<OptimizationSuggestion[]>([]);
  const [schedule, setSchedule]       = useState<OptimizationScheduleConfig>({
    scheduleType: "manual", timezone: "UTC", isEnabled: true,
  });
  const [recentSessions, setRecentSessions] = useState<SessionSummary[]>([]);

  const [fromDate, setFromDate]   = useState(() => new Date(Date.now() - 30 * 864e5).toISOString().slice(0, 10));
  const [toDate, setToDate]       = useState(() => new Date().toISOString().slice(0, 10));
  const [mode, setMode]           = useState<"aggregate" | "session">("aggregate");
  const [sessionId, setSessionId] = useState("");
  const [sessionHasRun, setSessionHasRun] = useState<boolean | null>(null);
  const [userContext, setUserContext] = useState("");
  const [running, setRunning]     = useState(false);
  const [pollingRunId, setPollRun]  = useState<number | null>(null);
  const [pollingAgentId, setPollAid] = useState<string>("");
  const [schedSaving, setSchedSave] = useState(false);
  const [loading, setLoading]       = useState(true);
  const [error, setError]           = useState<string | null>(null);

  const aid = agentId!;

  const { result: runsResult, setPage: setRunsPage, reload: reloadRuns } =
    usePagedList<OptimizationRunSummary, OptimizationRunListParams>(
      (params) => getOptimizationRunsPaged(aid, params),
      { page: 1, pageSize: 25 }
    );

  async function loadDetail(agId: string, runId: number): Promise<OptimizationRunDetail | null>
  {
    const d = await getOptimizationRunDetail(agId, runId).catch(() => null);
    if (!d) return null;
    setDetail(d);
    if (d.suggestions?.length) setSuggestions(prev =>
    {
      const ids = new Set(d.suggestions.map((s: OptimizationSuggestion) => s.id));
      return [...d.suggestions, ...prev.filter(s => !ids.has(s.id))];
    });
    return d;
  }

  async function loadData()
  {
    try
    {
      const [s, sc, sess] = await Promise.all([
        getOptimizationSuggestions(aid, { pageSize: 100 }).then(r => r.items).catch(() => [] as OptimizationSuggestion[]),
        getOptimizationSchedule(aid).catch(() => ({ scheduleType: "manual", timezone: "UTC", isEnabled: true } as OptimizationScheduleConfig)),
        api.getSessions({ agentId: aid, pageSize: 30 }).catch(() => ({ items: [] as SessionSummary[] })),
      ]);
      setSuggestions(s);
      setSchedule(sc);
      setRecentSessions(sess?.items ?? []);

      const qRunId  = searchParams.get("runId");
      const qSessId = searchParams.get("sessionId");

      if (qSessId)
      {
        setMode("session");
        setSessionId(qSessId);
        // Look up runs directly by sessionId — avoids agentId mismatch issues
        let sessionRuns: OptimizationRunSummary[] = [];
        try { sessionRuns = await getOptimizationRunsBySession(qSessId); }
        catch (e: unknown) { setError(`Failed to load session runs: ${(e as {error?:string})?.error ?? String(e)}`); }
        const sessRun = sessionRuns.sort((a, b) => b.id - a.id)[0];
        if (sessRun)
        {
          setSessionHasRun(true);
          const d = await loadDetail(sessRun.agentId, sessRun.id);
          if (!d)
            setError(`Run #${sessRun.id} found but detail could not be loaded — check API logs`);
          else if (d.status === "running")
          {
            setRunning(true);
            setPollAid(sessRun.agentId);
            setPollRun(sessRun.id);
          }
        }
        else
        {
          setSessionHasRun(false);
        }
      }
      else if (qRunId)
      {
        const runIdNum = parseInt(qRunId);
        const d = await loadDetail(aid, runIdNum);
        if (d?.status === "running")
        {
          setRunning(true);
          setPollAid(aid);
          setPollRun(runIdNum);
        }
      }
    }
    catch (e: unknown)
    {
      const msg = (e as { message?: string })?.message ?? String(e);
      setError(`Failed to load optimizer data: ${msg}`);
    }
    finally
    {
      setLoading(false);
    }
  }

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => { loadData(); }, [aid]);

  useEffect(() =>
  {
    if (!pollingRunId) return;
    const agId = pollingAgentId || aid;
    const timer = setInterval(async () =>
    {
      const d = await getOptimizationRunDetail(agId, pollingRunId).catch(() => null);
      if (d && (d.status === "completed" || d.status === "failed"))
      {
        clearInterval(timer);
        setPollRun(null);
        setRunning(false);
        setDetail(d);
        reloadRuns();
        setSuggestions(await getOptimizationSuggestions(aid, { pageSize: 100 }).then(r => r.items).catch(() => suggestions));
      }
    }, 3000);
    return () => clearInterval(timer);
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [pollingRunId, pollingAgentId]);

  async function startRun()
  {
    setError(null);
    setRunning(true);
    try
    {
      if (mode === "session")
      {
        const sid = sessionId.trim();
        if (!sid) { setError("Enter a session ID"); setRunning(false); return; }
        const { runId, agentId: returnedAid } = await triggerSessionOptimization(sid);
        setPollAid(returnedAid);
        setPollRun(runId);
      }
      else
      {
        const { runId } = await triggerOptimizationRun(aid, {
          from: fromDate, to: toDate,
          userContext: userContext.trim() || undefined,
        });
        setPollAid(aid);
        setPollRun(runId);
      }
      reloadRuns();
      setSessionHasRun(true);
    }
    catch (e: unknown)
    {
      const err = e as { error?: string; message?: string; runId?: number; agentId?: string };
      if (err?.runId)
      {
        // A run is already in progress — attach to it transparently
        setPollAid(err.agentId ?? aid);
        setPollRun(err.runId);
        setSessionHasRun(true);
      }
      else
      {
        setError(err?.error ?? err?.message ?? "Failed to start run");
        setRunning(false);
      }
    }
  }

  async function handleReview(id: number, action: "approve" | "reject")
  {
    try { await reviewSuggestion(aid, id, action); }
    catch (e: unknown) { setError((e as { error?: string })?.error ?? "Failed"); }
    setSuggestions(await getOptimizationSuggestions(aid, { pageSize: 100 }).then(r => r.items).catch(() => suggestions));
  }

  async function handleSaveSchedule()
  {
    setSchedSave(true);
    await saveOptimizationSchedule(aid, schedule).catch((e: unknown) =>
      setError((e as { error?: string })?.error ?? "Failed"));
    setSchedSave(false);
  }

  const actionableSuggestions = suggestions.filter(s => s.status === "Pending" || s.status === "Approved");

  if (loading)
    return (
      <div className="p-6 flex items-center gap-3 text-muted-foreground text-sm">
        <span className="inline-block h-3 w-3 rounded-full bg-primary animate-pulse" />
        Loading optimizer…
      </div>
    );

  return (
    <div className="p-6 max-w-5xl mx-auto space-y-6">
      <h1 className="text-2xl font-bold">Agent Optimizer</h1>

      {error && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 text-destructive px-4 py-3 text-sm">
          {error}
        </div>
      )}

      {/* Session Analysis Panel — shown when navigated here from a session */}
      {mode === "session" && sessionId && (
        <Card className="border-primary/20">
          <CardHeader className="pb-3">
            <CardTitle className="text-base flex items-center gap-2">
              Session Analysis
              {running && <span className="inline-block h-2.5 w-2.5 rounded-full bg-primary animate-pulse" />}
            </CardTitle>
            <p className="text-xs text-muted-foreground font-mono break-all">
              Session: {sessionId}
            </p>
          </CardHeader>
          <CardContent className="space-y-3">
            {/* Loading state */}
            {sessionHasRun === null && !error && (
              <p className="text-sm text-muted-foreground flex items-center gap-2">
                <span className="inline-block h-3 w-3 rounded-full bg-primary animate-pulse" />
                Checking for previous analysis…
              </p>
            )}

            {/* In-progress */}
            {running && pollingRunId && (
              <p className="text-sm text-muted-foreground">
                Analysis run <strong>#{pollingRunId}</strong> is in progress — results will appear automatically when complete.
              </p>
            )}

            {/* No prior run — show trigger inline */}
            {sessionHasRun === false && !running && (
              <div className="space-y-3">
                <p className="text-sm text-muted-foreground">
                  This session has not been analyzed yet. Optionally describe what's going wrong, then click <strong>Analyze Session</strong>.
                </p>
                <div className="space-y-1">
                  <label className="text-xs text-muted-foreground">
                    Problem context <span className="text-muted-foreground/60">(optional)</span>
                  </label>
                  <Textarea
                    value={userContext}
                    onChange={e => setUserContext(e.target.value)}
                    placeholder='e.g. "Agent gives partial answers on multi-part questions"'
                    className="max-w-lg resize-none text-sm"
                    rows={2}
                  />
                </div>
                <Button onClick={startRun} disabled={running}>
                  Analyze Session
                </Button>
              </div>
            )}

            {/* Has a completed run — show re-analyze option */}
            {sessionHasRun === true && !running && detail && detail.status === "completed" && (
              <div className="flex items-center gap-3 flex-wrap">
                <p className="text-sm text-muted-foreground">
                  Previous analysis (Run #{detail.id}) loaded. See results below.
                </p>
                <Button size="sm" variant="outline" onClick={startRun} disabled={running}>
                  Re-analyze
                </Button>
              </div>
            )}

            {/* Has a run that failed */}
            {sessionHasRun === true && !running && detail && detail.status === "failed" && (
              <div className="flex items-center gap-3 flex-wrap">
                <p className="text-sm text-destructive">
                  Previous run (#{detail.id}) failed. {detail.errorMessage ?? ""}
                </p>
                <Button size="sm" variant="outline" onClick={startRun} disabled={running}>
                  Retry Analysis
                </Button>
              </div>
            )}
          </CardContent>
        </Card>
      )}

      {/* In-progress banner for aggregate mode */}
      {mode !== "session" && running && pollingRunId && (
        <div className="rounded-md border border-primary/30 bg-primary/5 px-4 py-3 flex items-center gap-3 text-sm">
          <span className="inline-block h-3 w-3 rounded-full bg-primary animate-pulse shrink-0" />
          <span>
            Analysis run <strong>#{pollingRunId}</strong> is in progress — results will appear automatically when complete.
          </span>
        </div>
      )}

      {/* Quality Score Summary */}
      {detail && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">
              {detail.status === "completed" ? `Analysis Results — Run #${detail.id}` : `Run #${detail.id} — ${detail.status}`}
            </CardTitle>
          </CardHeader>
          <CardContent>
            {detail.status === "failed" && (
              <p className="text-sm text-destructive">{detail.errorMessage ?? "Run failed with no error message."}</p>
            )}
            {detail.status === "running" && (
              <p className="text-sm text-muted-foreground">Analysis in progress…</p>
            )}
            {detail.status === "completed" && detail.report && detail.report.totalTurns === 0 && (
              <div className="rounded-md border border-amber-200 bg-amber-50 dark:bg-amber-950/20 dark:border-amber-800 px-4 py-3 text-sm text-amber-800 dark:text-amber-200">
                No conversation turns were found for this session. Make sure the session has completed conversations before running analysis.
              </div>
            )}
            {detail.status === "completed" && detail.report && detail.report.totalTurns > 0 && (
              <>
                <div className="grid grid-cols-2 md:grid-cols-4 gap-6 mb-4">
                  {[
                    { key: "avgFaithfulness",    label: "Faithfulness" },
                    { key: "avgCompleteness",    label: "Completeness" },
                    { key: "avgToolEfficiency",  label: "Tool Efficiency" },
                    { key: "avgCoherence",       label: "Coherence" },
                  ].map(({ key, label }) => (
                    <div key={key}>
                      <div className="text-xs text-muted-foreground mb-1 text-center">{label}</div>
                      <QualityBar value={(detail.report as unknown as Record<string, number | null>)[key]} />
                    </div>
                  ))}
                </div>
                <div className="flex flex-wrap gap-4 text-xs text-muted-foreground pt-3 border-t">
                  <span>Sessions: {detail.report.totalSessions ?? 0}</span>
                  <span>Turns: {detail.report.totalTurns ?? 0}</span>
                  <span>Scored: {detail.report.scoredTurns ?? 0}</span>
                  <span>Verification fail: {((detail.report.verificationFailureRate ?? 0) * 100).toFixed(1)}%</span>
                  <span>Tool errors: {((detail.report.toolErrorRate ?? 0) * 100).toFixed(1)}%</span>
                </div>
              </>
            )}
            {detail.status === "completed" && !detail.report && (
              <p className="text-sm text-muted-foreground">Analysis completed with no report data.</p>
            )}
          </CardContent>
        </Card>
      )}

      {/* Analysis Trigger — shown only when NOT pre-loaded from a session URL */}
      {!(mode === "session" && sessionId) && (
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Run Analysis</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            <div className="flex gap-2">
              <Button size="sm" variant={mode === "aggregate" ? "default" : "outline"}
                onClick={() => setMode("aggregate")}>
                Date Range
              </Button>
              <Button size="sm" variant={mode === "session" ? "default" : "outline"}
                onClick={() => setMode("session")}>
                Specific Session
              </Button>
            </div>

            {mode === "aggregate" ? (
              <div className="flex gap-3 items-end">
                <div className="space-y-1">
                  <label className="text-xs text-muted-foreground">From</label>
                  <Input type="date" value={fromDate} onChange={e => setFromDate(e.target.value)}
                    className="w-36" />
                </div>
                <div className="space-y-1">
                  <label className="text-xs text-muted-foreground">To</label>
                  <Input type="date" value={toDate} onChange={e => setToDate(e.target.value)}
                    className="w-36" />
                </div>
              </div>
            ) : (
              <div className="space-y-3">
                {recentSessions.length > 0 && (
                  <div className="space-y-1">
                    <label className="text-xs text-muted-foreground">Recent Sessions</label>
                    <Select value={sessionId} onValueChange={setSessionId}>
                      <SelectTrigger className="max-w-sm">
                        <SelectValue placeholder="Select a session..." />
                      </SelectTrigger>
                      <SelectContent>
                        {recentSessions.map(s => (
                          <SelectItem key={s.sessionId} value={s.sessionId}>
                            {new Date(s.createdAt).toLocaleString()} — {s.totalTurns} turns
                            {s.status !== "active" && ` (${s.status})`}
                          </SelectItem>
                        ))}
                      </SelectContent>
                    </Select>
                  </div>
                )}
                <div className="space-y-1">
                  <label className="text-xs text-muted-foreground">
                    {recentSessions.length > 0 ? "Or enter Session ID manually" : "Session ID"}
                  </label>
                  <Input value={sessionId} onChange={e => setSessionId(e.target.value)}
                    placeholder="Enter session ID..." className="max-w-sm" />
                </div>
              </div>
            )}

            <div className="space-y-1">
              <label className="text-xs text-muted-foreground">
                Problem Context <span className="text-muted-foreground/60">(optional — describe what's going wrong)</span>
              </label>
              <Textarea
                value={userContext}
                onChange={e => setUserContext(e.target.value)}
                placeholder='e.g. "Agent gives partial answers on multi-part questions" — treated as highest-priority signal'
                className="max-w-lg resize-none text-sm"
                rows={2}
              />
            </div>

            <Button onClick={startRun} disabled={running}>
              {running ? (pollingRunId ? "Analysing…" : "Starting…") : "Run Analysis"}
            </Button>
          </CardContent>
        </Card>
      )}

      {/* Actionable Suggestions */}
      {actionableSuggestions.length > 0 && (
        <Card>
          <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
            <CardTitle className="text-base">Suggestions ({actionableSuggestions.length})</CardTitle>
            <Link
              to={`/agents/${aid}/optimize/suggestions`}
              className="text-xs text-primary hover:underline"
            >
              View All →
            </Link>
          </CardHeader>
          <CardContent className="space-y-3">
            {actionableSuggestions.map(s => (
              <SuggestionCard
                key={s.id}
                s={s}
                onApprove={() => handleReview(s.id, "approve")}
                onReject={() => handleReview(s.id, "reject")}
              />
            ))}
          </CardContent>
        </Card>
      )}

      {/* Schedule */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Schedule</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="flex flex-wrap gap-4 items-end">
            <div className="space-y-1">
              <label className="text-xs text-muted-foreground">Type</label>
              <Select value={schedule.scheduleType}
                onValueChange={v => setSchedule(s => ({ ...s, scheduleType: v }))}>
                <SelectTrigger className="w-32">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="manual">Manual</SelectItem>
                  <SelectItem value="daily">Daily</SelectItem>
                  <SelectItem value="weekly">Weekly</SelectItem>
                </SelectContent>
              </Select>
            </div>
            {schedule.scheduleType !== "manual" && (
              <div className="space-y-1">
                <label className="text-xs text-muted-foreground">Run At (HH:mm)</label>
                <Input type="time" value={schedule.runAtTime ?? ""}
                  onChange={e => setSchedule(s => ({ ...s, runAtTime: e.target.value }))}
                  className="w-28" />
              </div>
            )}
            {schedule.scheduleType === "weekly" && (
              <div className="space-y-1">
                <label className="text-xs text-muted-foreground">Day of Week</label>
                <Select value={String(schedule.runOnDayOfWeek ?? 1)}
                  onValueChange={v => setSchedule(s => ({ ...s, runOnDayOfWeek: parseInt(v) }))}>
                  <SelectTrigger className="w-24">
                    <SelectValue />
                  </SelectTrigger>
                  <SelectContent>
                    {["Sun","Mon","Tue","Wed","Thu","Fri","Sat"].map((d, i) => (
                      <SelectItem key={i} value={String(i)}>{d}</SelectItem>
                    ))}
                  </SelectContent>
                </Select>
              </div>
            )}
            <div className="flex items-center gap-2 pb-0.5">
              <input type="checkbox" id="sched-enabled" checked={schedule.isEnabled}
                onChange={e => setSchedule(s => ({ ...s, isEnabled: e.target.checked }))}
                className="rounded" />
              <label htmlFor="sched-enabled" className="text-sm cursor-pointer">Enabled</label>
            </div>
            <Button onClick={handleSaveSchedule} disabled={schedSaving} variant="secondary">
              {schedSaving ? "Saving…" : "Save Schedule"}
            </Button>
          </div>
          {schedule.nextRunAt && (
            <p className="mt-2 text-xs text-muted-foreground">
              Next run: {new Date(schedule.nextRunAt).toLocaleString()}
            </p>
          )}
        </CardContent>
      </Card>

      {/* Run History */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Run History</CardTitle>
        </CardHeader>
        <CardContent>
          {(runsResult?.items.length ?? 0) === 0 ? (
            <p className="text-sm text-muted-foreground">No runs yet.</p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-12">ID</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Trigger</TableHead>
                  <TableHead className="text-right">Sessions</TableHead>
                  <TableHead className="text-right">Turns</TableHead>
                  <TableHead className="text-right">Suggestions</TableHead>
                  <TableHead>Started</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {(runsResult?.items ?? []).map(r => (
                  <TableRow key={r.id} className="cursor-pointer" onClick={() => loadDetail(aid, r.id)}>
                    <TableCell>{r.id}</TableCell>
                    <TableCell><StatusBadge status={r.status} /></TableCell>
                    <TableCell>
                      {r.triggerSource}
                      {r.sessionId && <span className="ml-1 text-xs text-muted-foreground">(session)</span>}
                    </TableCell>
                    <TableCell className="text-right">{r.sessionsAnalyzed}</TableCell>
                    <TableCell className="text-right">{r.turnsAnalyzed}</TableCell>
                    <TableCell className="text-right">{r.suggestionCount}</TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {new Date(r.startedAt).toLocaleString()}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
          {runsResult && (
            <ListPagination
              page={runsResult.page}
              totalPages={runsResult.totalPages}
              totalCount={runsResult.totalCount}
              onPageChange={setRunsPage}
              itemLabel="total"
            />
          )}
        </CardContent>
      </Card>
    </div>
  );
}
