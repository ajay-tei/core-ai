import { useEffect, useState } from "react";
import { useParams, Link, useNavigate } from "react-router";
import { api, markTurnAsExample } from "../api";
import type { SessionDetail as SessionDetailDto, TurnSummary, IterationDetail, SessionTreeNode } from "../api";
import SessionToolCallCard from "./SessionToolCallCard";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { Separator } from "@/components/ui/separator";
import { ChevronDown, ChevronRight, ArrowLeft, Copy, Download, Trash2, Maximize2, MessageSquare } from "lucide-react";

const MSG_PREVIEW_THRESHOLD = 300;

export default function SessionDetail() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const [session, setSession] = useState<SessionDetailDto | null>(null);
  const [selectedTurn, setSelectedTurn] = useState<TurnSummary | null>(null);
  const [iterations, setIterations] = useState<IterationDetail[]>([]);
  const [loadingSession, setLoadingSession] = useState(true);
  const [loadingIterations, setLoadingIterations] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [fullTextDialog, setFullTextDialog] = useState<{ title: string; content: string } | null>(null);

  useEffect(() => {
    if (!id) return;
    setLoadingSession(true);
    api.getSession(id)
      .then(s => {
        setSession(s);
        if (s.turns.length > 0) selectTurn(s.turns[0]);
      })
      .catch(e => setError(String(e)))
      .finally(() => setLoadingSession(false));
  }, [id]);

  const selectTurn = (turn: TurnSummary) => {
    if (!id) return;
    setSelectedTurn(turn);
    setIterations([]);
    setLoadingIterations(true);
    api.getTurnIterations(id, turn.turnNumber)
      .then(setIterations)
      .catch(() => setIterations([]))
      .finally(() => setLoadingIterations(false));
  };

  const handleExport = async () => {
    if (!id) return;
    const blob = await api.exportSession(id);
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `session-${id.slice(0, 8)}.json`;
    a.click();
    URL.revokeObjectURL(url);
  };

  const handleDelete = async () => {
    if (!id || !session) return;
    if (!confirm("Soft-delete this session trace?")) return;
    await api.deleteSession(id);
    navigate(-1);
  };

  const handleAnalyzeSession = () => {
    if (!id || !session) return;
    navigate(`/agents/${session.agentId}/optimize?sessionId=${id}`);
  };

  const handleContinue = async () => {
    if (!id || !session) return;
    try {
      const res = await api.continueSession(id);
      navigate(`/agents/${res.agentId}/chat?sessionId=${encodeURIComponent(res.sessionId)}`);
    } catch (e) {
      setError((e as { error?: string })?.error ?? "Failed to continue session");
    }
  };

  const handleMarkAsExample = async (turnNumber: number) => {
    if (!id) return;
    const desc = prompt("Add a description for this example (optional):");
    if (desc === null) return; // cancelled
    try {
      await markTurnAsExample(id, turnNumber, desc || undefined);
      alert("Turn marked as example.");
    } catch (e: any) { setError(e?.error ?? "Failed to mark as example"); }
  };

  if (loadingSession) return (
    <div className="p-6 text-sm text-muted-foreground">Loading session…</div>
  );
  if (error) return (
    <div className="p-6 space-y-3">
      <Button variant="ghost" size="sm" onClick={() => navigate("/sessions")} className="gap-1">
        <ArrowLeft className="size-4" /> Back
      </Button>
      <p className="text-sm text-destructive">{error}</p>
    </div>
  );
  if (!session) return (
    <div className="p-6 text-sm text-muted-foreground">Session not found.</div>
  );

  const durationMs = new Date(session.lastActivityAt).getTime() - new Date(session.createdAt).getTime();
  const durationStr = durationMs > 60000
    ? `${Math.floor(durationMs / 60000)}m ${Math.floor((durationMs % 60000) / 1000)}s`
    : `${Math.floor(durationMs / 1000)}s`;

  return (
    <div className="flex h-full overflow-hidden">
      {/* ── Left panel ── */}
      <div className="w-72 flex-shrink-0 border-r overflow-y-auto flex flex-col gap-3 p-4">
        <Button variant="ghost" size="sm" onClick={() => navigate("/sessions")} className="gap-1 self-start -ml-1">
          <ArrowLeft className="size-4" /> Back
        </Button>

        {/* Session metadata */}
        <div className="space-y-2">
          <div className="flex items-center gap-1">
            <code className="text-xs text-muted-foreground truncate flex-1">{session.sessionId.slice(0, 16)}…</code>
            <Button variant="ghost" size="icon" className="size-6 shrink-0"
              onClick={() => navigator.clipboard.writeText(session.sessionId)}>
              <Copy className="size-3" />
            </Button>
          </div>
          <div className="flex flex-wrap gap-1">
            <StatusBadge status={session.status} />
            {session.isSupervisor && <Badge variant="secondary">Supervisor</Badge>}
          </div>
        </div>

        <Separator />

        <dl className="text-sm space-y-1.5">
          <Row label="Agent" value={session.agentName} />
          {session.parentSessionId && (
            <div className="flex justify-between gap-2">
              <dt className="text-muted-foreground shrink-0">Parent</dt>
              <dd>
                <Link to={`/sessions/${session.parentSessionId}`}
                  className="text-primary hover:underline font-mono text-xs">
                  {session.parentSessionId.slice(0, 8)}…
                </Link>
              </dd>
            </div>
          )}
          <Row label="Created" value={fmtDate(session.createdAt)} />
          <Row label="Duration" value={durationStr} />
          <Row label="Turns" value={session.totalTurns} />
          <Row label="Iterations" value={session.totalIterations} />
          <Row label="Tool Calls" value={session.totalToolCalls} />
          {session.totalDelegations > 0 && <Row label="Delegations" value={session.totalDelegations} />}
          {(() => {
            const cache = session.totalCacheReadTokens + session.totalCacheCreationTokens;
            const effIn = session.totalInputTokens + cache;
            if (effIn === 0 && session.totalOutputTokens === 0) return null;
            const rollupCache = session.rollupCacheReadTokens + session.rollupCacheCreationTokens;
            const rollupEffIn = session.rollupInputTokens + rollupCache;
            return (
              <>
                <Row
                  label="Tokens"
                  title={`Input ${fmtNum(effIn)} = ${fmtNum(session.totalInputTokens)} fresh + ${fmtNum(cache)} cached · Output ${fmtNum(session.totalOutputTokens)}`}
                  value={`↑ ${fmtNum(effIn)} / ↓ ${fmtNum(session.totalOutputTokens)}`}
                />
                {session.subAgentSessionCount > 0 && (
                  <Row
                    label="Incl. sub-agents"
                    title={`This session + ${session.subAgentSessionCount} delegated sub-agent session(s). Each sub-agent keeps its own totals on its session page.`}
                    value={`↑ ${fmtNum(rollupEffIn)} / ↓ ${fmtNum(session.rollupOutputTokens)} · +${session.subAgentSessionCount}`}
                  />
                )}
              </>
            );
          })()}
        </dl>

        <div className="flex flex-wrap gap-2">
          <Button variant="outline" size="sm" onClick={handleExport} className="gap-1">
            <Download className="size-3" /> Export
          </Button>
          <Button variant="outline" size="sm" onClick={handleDelete}
            className="gap-1 text-destructive border-destructive/40 hover:bg-destructive/10">
            <Trash2 className="size-3" /> Delete
          </Button>
          <Button variant="outline" size="sm" onClick={handleAnalyzeSession}
            className="gap-1 text-blue-600 border-blue-300 hover:bg-blue-50">
            Analyze Session
          </Button>
          <Button variant="outline" size="sm" onClick={handleContinue}
            className="gap-1 text-emerald-600 border-emerald-300 hover:bg-emerald-50">
            <MessageSquare className="size-3" /> Continue in Chat
          </Button>
        </div>

        <Separator />

        {/* Delegation chain */}
        {id && <DelegationChainPanel sessionId={id} />}

        {/* Turn list */}
        <div>
          <p className="text-xs font-medium text-muted-foreground uppercase tracking-wide mb-2">Turns</p>
          <div className="space-y-1">
            {session.turns.map(turn => (
              <button
                key={turn.turnNumber}
                onClick={() => selectTurn(turn)}
                className={`w-full text-left px-2 py-2 rounded-md text-sm transition-colors hover:bg-accent
                  ${selectedTurn?.turnNumber === turn.turnNumber ? "bg-accent" : ""}`}
              >
                <div className="flex items-center justify-between mb-0.5">
                  <span className="font-medium">Turn {turn.turnNumber}</span>
                  <span className="text-xs text-muted-foreground">{fmtDate(turn.createdAt, true)}</span>
                </div>
                <p className="text-xs text-muted-foreground truncate">{turn.userMessagePreview}</p>
                <div className="flex gap-2 mt-0.5 text-xs text-muted-foreground">
                  <span>{turn.totalIterations} iter</span>
                  <span>{turn.totalToolCalls} tools</span>
                  {turn.verificationPassed != null && (
                    <span>{turn.verificationPassed ? "✓" : "✗"}</span>
                  )}
                  {turn.totalInputTokens > 0 && <span>↑{fmtNum(turn.totalInputTokens)}</span>}
                </div>
              </button>
            ))}
          </div>
        </div>
      </div>

      {/* Full text dialog */}
      <Dialog open={!!fullTextDialog} onOpenChange={open => { if (!open) setFullTextDialog(null); }}>
        <DialogContent className="max-w-4xl max-h-[85vh] flex flex-col">
          <DialogHeader>
            <DialogTitle className="text-sm">{fullTextDialog?.title}</DialogTitle>
          </DialogHeader>
          <pre className="flex-1 overflow-auto text-xs bg-muted rounded p-4 whitespace-pre-wrap break-words">
            {fullTextDialog?.content}
          </pre>
        </DialogContent>
      </Dialog>

      {/* ── Right panel — iteration timeline ── */}
      <div className="flex-1 overflow-y-auto p-4 space-y-3">
        {!selectedTurn && (
          <p className="text-sm text-muted-foreground">Select a turn to view iterations.</p>
        )}
        {selectedTurn && (
          <>
            {/* User message */}
            <Card className="border-primary/30 bg-primary/5">
              <CardContent className="p-3">
                <div className="flex items-center justify-between mb-1">
                  <p className="text-xs font-medium text-primary">User — Turn {selectedTurn.turnNumber}</p>
                  <div className="flex gap-1">
                    <Button variant="ghost" size="sm" className="h-5 px-1 text-xs text-yellow-600 gap-1"
                      onClick={() => handleMarkAsExample(selectedTurn.turnNumber)}>
                      ★ Example
                    </Button>
                    {selectedTurn.userMessage && selectedTurn.userMessage.length > MSG_PREVIEW_THRESHOLD && (
                      <Button variant="ghost" size="sm" className="h-5 px-1 text-xs text-muted-foreground gap-1"
                        onClick={() => setFullTextDialog({ title: `Turn ${selectedTurn.turnNumber} — User message`, content: selectedTurn.userMessage! })}>
                        <Maximize2 className="size-3" /> Full
                      </Button>
                    )}
                  </div>
                </div>
                <p className="text-sm whitespace-pre-wrap">{selectedTurn.userMessage ?? selectedTurn.userMessagePreview}</p>
              </CardContent>
            </Card>

            {loadingIterations && (
              <p className="text-sm text-muted-foreground">Loading iterations…</p>
            )}

            {iterations.map(iter => (
              <IterationCard key={iter.iterationNumber} iteration={iter} />
            ))}

            {/* Assistant response */}
            {selectedTurn.assistantMessagePreview && (
              <Card className="border-emerald-500/30 bg-emerald-500/5">
                <CardContent className="p-3">
                  <div className="flex items-center justify-between mb-1">
                    <p className="text-xs font-medium text-emerald-500">Assistant response</p>
                    {selectedTurn.assistantMessage && selectedTurn.assistantMessage.length > MSG_PREVIEW_THRESHOLD && (
                      <Button variant="ghost" size="sm" className="h-5 px-1 text-xs text-muted-foreground gap-1"
                        onClick={() => setFullTextDialog({ title: `Turn ${selectedTurn.turnNumber} — Assistant response`, content: selectedTurn.assistantMessage! })}>
                        <Maximize2 className="size-3" /> Full
                      </Button>
                    )}
                  </div>
                  <p className="text-sm whitespace-pre-wrap">{selectedTurn.assistantMessage ?? selectedTurn.assistantMessagePreview}</p>
                </CardContent>
              </Card>
            )}
          </>
        )}
      </div>
    </div>
  );
}

function IterationCard({ iteration }: { iteration: IterationDetail }) {
  const [thinkingOpen, setThinkingOpen] = useState(false);
  const [planOpen, setPlanOpen] = useState(false);

  return (
    <Card className={iteration.isCorrection ? "border-amber-500/50" : ""}>
      <CardHeader className="p-3 pb-2">
        <div className="flex items-center flex-wrap gap-2">
          <CardTitle className="text-sm">Iteration {iteration.iterationNumber}</CardTitle>
          {iteration.continuationWindow > 1 && (
            <span className="text-xs text-muted-foreground">Window {iteration.continuationWindow}</span>
          )}
          {iteration.modelId && (
            <Badge variant="outline" className="text-xs">{iteration.modelId}</Badge>
          )}
          {iteration.isCorrection && (
            <Badge variant="outline" className="text-xs border-amber-500/50 text-amber-500">Correction</Badge>
          )}
          {iteration.hadModelSwitch && (
            <Badge variant="outline" className="text-xs border-teal-500/50 text-teal-400">
              {iteration.fromModel} → {iteration.toModel}
            </Badge>
          )}
          {(iteration.inputTokens > 0 || iteration.outputTokens > 0) && (
            <span className="ml-auto text-xs text-muted-foreground">
              ↑ {fmtNum(iteration.inputTokens)} · ↓ {fmtNum(iteration.outputTokens)}
              {iteration.cacheReadTokens > 0 && ` · ${fmtNum(iteration.cacheReadTokens)} cached`}
            </span>
          )}
        </div>
      </CardHeader>
      <CardContent className="p-3 pt-0 space-y-2">
        {/* Thinking */}
        {iteration.thinkingText && (
          <div>
            <Button variant="ghost" size="sm" className="h-6 px-1 text-xs text-muted-foreground gap-1"
              onClick={() => setThinkingOpen(!thinkingOpen)}>
              {thinkingOpen ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
              THINKING
            </Button>
            {thinkingOpen && (
              <p className="text-xs text-muted-foreground bg-muted rounded p-2 mt-1 whitespace-pre-wrap">
                {iteration.thinkingText}
              </p>
            )}
          </div>
        )}

        {/* Plan */}
        {iteration.planText && (
          <div>
            <Button variant="ghost" size="sm" className="h-6 px-1 text-xs text-muted-foreground gap-1"
              onClick={() => setPlanOpen(!planOpen)}>
              {planOpen ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
              PLAN
            </Button>
            {planOpen && (
              <p className="text-xs text-muted-foreground bg-muted rounded p-2 mt-1 whitespace-pre-wrap">
                {iteration.planText}
              </p>
            )}
          </div>
        )}

        {/* Tool calls */}
        {iteration.toolCalls.length > 0 && (
          <div>
            <p className="text-xs font-medium text-muted-foreground uppercase tracking-wide mb-1">
              Tool Calls ({iteration.toolCalls.length})
            </p>
            {iteration.toolCalls.map(tc => (
              <SessionToolCallCard key={tc.sequence} toolCall={tc} />
            ))}
          </div>
        )}
      </CardContent>
    </Card>
  );
}

function DelegationChainPanel({ sessionId }: { sessionId: string }) {
  const [tree, setTree] = useState<SessionTreeNode[] | null>(null);

  useEffect(() => {
    api.getSessionTree(sessionId).then(setTree).catch(() => setTree([]));
  }, [sessionId]);

  if (!tree) return null;

  const totalNodes = countNodes(tree);
  if (totalNodes <= 1) return null;

  return (
    <div>
      <p className="text-xs font-medium text-muted-foreground uppercase tracking-wide mb-2">
        Delegation Chain
      </p>
      <div className="space-y-0.5">
        <TreeNodes nodes={tree} depth={0} />
      </div>
    </div>
  );
}

function countNodes(nodes: SessionTreeNode[]): number {
  return nodes.reduce((acc, n) => acc + 1 + countNodes(n.children), 0);
}

function TreeNodes({ nodes, depth }: { nodes: SessionTreeNode[]; depth: number }) {
  return (
    <>
      {nodes.map(node => (
        <div key={node.sessionId}>
          <div className={`flex items-center gap-1 rounded px-2 py-1 text-xs
            ${node.isCurrentSession ? "bg-primary/10 text-primary font-medium" : "text-muted-foreground hover:bg-accent hover:text-accent-foreground"}`}
            style={{ paddingLeft: `${8 + depth * 12}px` }}
          >
            {depth > 0 && <span className="shrink-0 text-muted-foreground/50">└</span>}
            <Link
              to={`/sessions/${node.sessionId}`}
              className={`flex-1 truncate ${node.isCurrentSession ? "pointer-events-none" : "hover:underline"}`}
            >
              {node.agentName || node.sessionId.slice(0, 8)}
            </Link>
            {node.isSupervisor && (
              <Badge variant="secondary" className="text-xs px-1 py-0 h-4 shrink-0">sup</Badge>
            )}
            <span className={`shrink-0 text-xs ml-1 ${
              node.status === "completed" ? "text-emerald-500" :
              node.status === "failed" ? "text-destructive" :
              node.status === "active" ? "text-primary" : "text-muted-foreground"
            }`}>
              {node.status === "completed" ? "✓" : node.status === "failed" ? "✗" : node.status === "active" ? "●" : node.status}
            </span>
          </div>
          {node.children.length > 0 && (
            <TreeNodes nodes={node.children} depth={depth + 1} />
          )}
        </div>
      ))}
    </>
  );
}

function Row({ label, value, title }: { label: string; value: React.ReactNode; title?: string }) {
  return (
    <div className="flex justify-between gap-2">
      <dt className="text-muted-foreground shrink-0">{label}</dt>
      <dd className="text-right truncate" title={title}>{value}</dd>
    </div>
  );
}

function StatusBadge({ status }: { status: string }) {
  const variants: Record<string, string> = {
    completed: "bg-emerald-500/10 text-emerald-500 border-emerald-500/30",
    failed: "bg-destructive/10 text-destructive border-destructive/30",
    active: "bg-primary/10 text-primary border-primary/30",
  };
  const cls = variants[status] ?? "bg-muted text-muted-foreground";
  return (
    <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs border font-medium ${cls}`}>
      {status}
    </span>
  );
}

function fmtDate(iso: string, timeOnly = false) {
  const d = new Date(iso);
  if (timeOnly) return d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
  return d.toLocaleString([], { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

function fmtNum(n: number) {
  return n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n);
}
