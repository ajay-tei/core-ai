import { useEffect, useState, useCallback } from "react";
import { Link, useNavigate } from "react-router";
import { api } from "../api";
import type { SessionSummary, PagedResult, SessionListParams } from "../api";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogDescription } from "@/components/ui/dialog";
import { ChevronLeft, ChevronRight, Trash2, MessageSquare } from "lucide-react";

export default function SessionBrowser() {
  const navigate = useNavigate();
  const [result, setResult] = useState<PagedResult<SessionSummary> | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [params, setParams] = useState<SessionListParams>({ page: 1, pageSize: 50 });
  const [purgeOpen, setPurgeOpen] = useState(false);
  const [purgeDays, setPurgeDays] = useState(30);
  const [purgeStatus, setPurgeStatus] = useState("all");
  const [purging, setPurging] = useState(false);
  const [purgeResult, setPurgeResult] = useState<string | null>(null);

  const load = useCallback((p: SessionListParams) => {
    setLoading(true);
    setError(null);
    api.getSessions(p)
      .then(setResult)
      .catch(e => setError(String(e)))
      .finally(() => setLoading(false));
  }, []);

  useEffect(() => { load(params); }, [params]);

  const update = (patch: Partial<SessionListParams>) =>
    setParams(prev => ({ ...prev, ...patch, page: 1 }));

  const setPage = (page: number) =>
    setParams(prev => ({ ...prev, page }));

  const handleContinue = async (sessionId: string) => {
    try {
      const res = await api.continueSession(sessionId);
      navigate(`/agents/${res.agentId}/chat?sessionId=${encodeURIComponent(res.sessionId)}`);
    } catch (e) {
      setError(String(e));
    }
  };

  const handlePurge = async () => {
    setPurging(true);
    setPurgeResult(null);
    try {
      const res = await api.purgeSessions(purgeDays, purgeStatus === "all" ? undefined : purgeStatus);
      setPurgeResult(`Deleted ${res.deleted} session${res.deleted !== 1 ? "s" : ""}.`);
      load(params); // refresh list
    } catch (e) {
      setPurgeResult(`Error: ${e}`);
    } finally {
      setPurging(false);
    }
  };

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Sessions</h1>
          <p className="text-sm text-muted-foreground">Browse agent execution trace history</p>
        </div>
        <Button variant="outline" size="sm" onClick={() => { setPurgeResult(null); setPurgeOpen(true); }}
          className="gap-1 text-destructive border-destructive/40 hover:bg-destructive/10">
          <Trash2 className="size-4" /> Purge Old Sessions
        </Button>
      </div>

      {/* Purge dialog */}
      <Dialog open={purgeOpen} onOpenChange={open => { setPurgeOpen(open); if (!open) setPurgeResult(null); }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Purge Old Sessions</DialogTitle>
            <DialogDescription>
              Permanently delete session trace data. This cannot be undone.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-4 py-2">
            <div className="flex items-center gap-3">
              <label className="text-sm w-32 shrink-0">Older than</label>
              <Input
                type="number"
                min={1}
                value={purgeDays}
                onChange={e => setPurgeDays(Number(e.target.value))}
                className="w-24"
              />
              <span className="text-sm text-muted-foreground">days</span>
            </div>
            <div className="flex items-center gap-3">
              <label className="text-sm w-32 shrink-0">Status</label>
              <Select value={purgeStatus} onValueChange={setPurgeStatus}>
                <SelectTrigger className="w-40">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="all">All statuses</SelectItem>
                  <SelectItem value="completed">Completed only</SelectItem>
                  <SelectItem value="failed">Failed only</SelectItem>
                </SelectContent>
              </Select>
            </div>
            {purgeResult && (
              <p className={`text-sm ${purgeResult.startsWith("Error") ? "text-destructive" : "text-emerald-500"}`}>
                {purgeResult}
              </p>
            )}
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setPurgeOpen(false)}>Cancel</Button>
            <Button variant="destructive" onClick={handlePurge} disabled={purging}>
              {purging ? "Purging…" : `Purge sessions older than ${purgeDays} days`}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Filter bar */}
      <Card>
        <CardContent className="p-4 flex flex-wrap gap-3 items-center">
          <Input
            placeholder="Search session ID or agent…"
            className="w-64"
            onChange={e => update({ q: e.target.value || undefined })}
          />
          <Select onValueChange={v => update({ status: v === "all" ? undefined : v })}>
            <SelectTrigger className="w-36">
              <SelectValue placeholder="All statuses" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All statuses</SelectItem>
              <SelectItem value="active">Active</SelectItem>
              <SelectItem value="completed">Completed</SelectItem>
              <SelectItem value="failed">Failed</SelectItem>
            </SelectContent>
          </Select>
          <Select defaultValue="50" onValueChange={v => update({ pageSize: Number(v) })}>
            <SelectTrigger className="w-24">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="25">25 / page</SelectItem>
              <SelectItem value="50">50 / page</SelectItem>
              <SelectItem value="100">100 / page</SelectItem>
            </SelectContent>
          </Select>
          <label className="flex items-center gap-2 text-sm cursor-pointer select-none">
            <input type="checkbox" className="rounded"
              onChange={e => update({ supervisorOnly: e.target.checked || undefined })} />
            Supervisors only
          </label>
          <label className="flex items-center gap-2 text-sm cursor-pointer select-none">
            <input type="checkbox" className="rounded"
              onChange={e => update({ hasErrors: e.target.checked || undefined })} />
            Errors only
          </label>
        </CardContent>
      </Card>

      {error && <p className="text-sm text-destructive">{error}</p>}

      <Card>
        <CardHeader className="px-4 py-3 flex flex-row items-center justify-between">
          <CardTitle className="text-sm font-medium">
            {result ? `${result.totalCount} sessions` : "Loading…"}
          </CardTitle>
          {loading && <span className="text-xs text-muted-foreground">Refreshing…</span>}
        </CardHeader>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Session</TableHead>
                <TableHead>Agent</TableHead>
                <TableHead>Status</TableHead>
                <TableHead className="text-right">Turns</TableHead>
                <TableHead className="text-right">Iterations</TableHead>
                <TableHead className="text-right">Tools</TableHead>
                <TableHead className="text-right">Tokens In</TableHead>
                <TableHead className="text-right">Tokens Out</TableHead>
                <TableHead>Created</TableHead>
                <TableHead className="text-right">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {result?.items.length === 0 && (
                <TableRow>
                  <TableCell colSpan={10} className="text-center text-muted-foreground py-8">
                    No sessions found
                  </TableCell>
                </TableRow>
              )}
              {result?.items.map(s => (
                <TableRow key={s.sessionId}>
                  <TableCell>
                    <div className="flex flex-col gap-0.5 max-w-[280px]">
                      <div className="flex items-center gap-1.5">
                        <Link to={`/sessions/${s.sessionId}`}
                          className="text-primary hover:underline text-sm font-medium truncate"
                          title={s.title?.trim() || undefined}>
                          {s.title?.trim() || "Untitled session"}
                        </Link>
                        {s.isSupervisor && <Badge variant="secondary" className="text-xs px-1">sup</Badge>}
                        {s.parentSessionId && (
                          <span className="text-xs text-muted-foreground" title={`Child of ${s.parentSessionId.slice(0, 8)}`}>↳</span>
                        )}
                      </div>
                      <span className="font-mono text-[11px] text-muted-foreground">{s.sessionId.slice(0, 8)}…</span>
                    </div>
                  </TableCell>
                  <TableCell className="text-sm">{s.agentName}</TableCell>
                  <TableCell><StatusBadge status={s.status} /></TableCell>
                  <TableCell className="text-right">{s.totalTurns}</TableCell>
                  <TableCell className="text-right">{s.totalIterations}</TableCell>
                  <TableCell className="text-right">{s.totalToolCalls}</TableCell>
                  <TableCell className="text-right text-muted-foreground text-sm" title={`${fmtNum(s.totalInputTokens)} fresh + ${fmtNum(s.totalCacheReadTokens + s.totalCacheCreationTokens)} cached`}>{fmtNum(s.totalInputTokens + s.totalCacheReadTokens + s.totalCacheCreationTokens)}</TableCell>
                  <TableCell className="text-right text-muted-foreground text-sm">{fmtNum(s.totalOutputTokens)}</TableCell>
                  <TableCell className="text-muted-foreground text-xs">{fmtDate(s.createdAt)}</TableCell>
                  <TableCell className="text-right">
                    <Button variant="ghost" size="sm" className="gap-1 text-emerald-600 hover:text-emerald-700"
                      title="Continue this session in Agent Test"
                      onClick={() => handleContinue(s.sessionId)}>
                      <MessageSquare className="size-3.5" /> Continue
                    </Button>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      {/* Pagination — always shown when there are results */}
      {result && result.totalCount > 0 && (
        <div className="flex items-center gap-2 text-sm">
          <Button variant="outline" size="sm" disabled={result.page <= 1}
            onClick={() => setPage(result.page - 1)}>
            <ChevronLeft className="size-4" /> Prev
          </Button>
          <span className="text-muted-foreground">
            Page {result.page} of {result.totalPages || 1} &nbsp;·&nbsp; {result.totalCount} total
          </span>
          <Button variant="outline" size="sm" disabled={result.page >= (result.totalPages || 1)}
            onClick={() => setPage(result.page + 1)}>
            Next <ChevronRight className="size-4" />
          </Button>
        </div>
      )}
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const cls: Record<string, string> = {
    completed: "bg-emerald-500/10 text-emerald-500 border-emerald-500/30",
    failed: "bg-destructive/10 text-destructive border-destructive/30",
    active: "bg-primary/10 text-primary border-primary/30",
  };
  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs border font-medium ${cls[status] ?? "bg-muted text-muted-foreground"}`}>
      {status}
    </span>
  );
}

function fmtDate(iso: string) {
  return new Date(iso).toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

function fmtNum(n: number) {
  if (n === 0) return "—";
  return n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n);
}
