import { useEffect, useState } from "react";
import {
  listSchedulerFeedback,
  approveSchedulerFeedback,
  rejectSchedulerFeedback,
  type SchedulerFeedbackItem,
} from "../api";
import { Button } from "./ui/button";
import { Badge } from "./ui/badge";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogFooter,
} from "./ui/dialog";
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from "./ui/table";
import { Textarea } from "./ui/textarea";
import { Label } from "./ui/label";
import { Skeleton } from "./ui/skeleton";
import { ScrollArea } from "./ui/scroll-area";
import { RefreshCw, ExternalLink } from "lucide-react";

// ── Helpers ───────────────────────────────────────────────────────────────────

function Stars({ n }: { n?: number | null }) {
  if (!n) return null;
  return (
    <span className="text-yellow-400 tracking-tight">
      {"★".repeat(n)}
      <span className="text-muted-foreground/40">{"★".repeat(5 - n)}</span>
    </span>
  );
}

function ThumbsBadge({ v }: { v?: number | null }) {
  if (v == null) return null;
  return v === 1
    ? <span className="text-emerald-500 font-medium">👍 Up</span>
    : <span className="text-red-500 font-medium">👎 Down</span>;
}

function StatusBadge({ status }: { status: string }) {
  const map: Record<string, string> = {
    pending:  "bg-amber-500/10 text-amber-500 border-amber-500/20",
    approved: "bg-emerald-500/10 text-emerald-500 border-emerald-500/20",
    rejected: "bg-red-500/10 text-red-500 border-red-500/20",
  };
  return (
    <Badge variant="outline" className={`text-xs capitalize ${map[status] ?? ""}`}>
      {status}
    </Badge>
  );
}

// ── Main component ────────────────────────────────────────────────────────────

export function SchedulerFeedbackReview() {
  const [items, setItems]               = useState<SchedulerFeedbackItem[]>([]);
  const [loading, setLoading]           = useState(true);
  const [notice, setNotice]             = useState<{ type: "error" | "success"; msg: string } | null>(null);
  const [selected, setSelected]         = useState<SchedulerFeedbackItem | null>(null);
  const [rejectMode, setRejectMode]     = useState(false);
  const [rejectNotes, setRejectNotes]   = useState("");
  const [actionLoading, setActionLoading] = useState(false);

  async function load() {
    setLoading(true);
    setNotice(null);
    try {
      const data = await listSchedulerFeedback();
      setItems(data);
    } catch {
      setNotice({ type: "error", msg: "Failed to load feedback items." });
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, []);

  function openDetail(item: SchedulerFeedbackItem) {
    setSelected(item);
    setRejectMode(false);
    setRejectNotes("");
  }

  function closeDetail() {
    setSelected(null);
    setRejectMode(false);
    setRejectNotes("");
  }

  async function handleApprove() {
    if (!selected) return;
    setActionLoading(true);
    try {
      await approveSchedulerFeedback(selected.id, selected.tenantId);
      setItems(prev => prev.filter(i => i.id !== selected.id));
      setNotice({
        type: "success",
        msg: selected.correctionText
          ? "Feedback approved — correction queued for rule learning."
          : "Feedback approved.",
      });
      closeDetail();
    } catch {
      setNotice({ type: "error", msg: "Approval failed." });
    } finally {
      setActionLoading(false);
    }
  }

  async function handleReject() {
    if (!selected) return;
    setActionLoading(true);
    try {
      await rejectSchedulerFeedback(selected.id, rejectNotes || undefined, selected.tenantId);
      setItems(prev => prev.filter(i => i.id !== selected.id));
      setNotice({ type: "success", msg: "Feedback rejected." });
      closeDetail();
    } catch {
      setNotice({ type: "error", msg: "Rejection failed." });
    } finally {
      setActionLoading(false);
    }
  }

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-xl font-semibold">Schedule Feedback</h2>
          <p className="text-sm text-muted-foreground mt-0.5">
            Review and act on feedback submitted by recipients of scheduler run emails.
          </p>
        </div>
        <Button variant="outline" size="sm" onClick={load} disabled={loading} className="h-8">
          <RefreshCw className="h-3.5 w-3.5 mr-1.5" />
          Refresh
        </Button>
      </div>

      {/* Notice banner */}
      {notice && (
        <div className={`rounded-md border px-3 py-2 text-sm ${
          notice.type === "success"
            ? "bg-emerald-500/10 border-emerald-500/20 text-emerald-600"
            : "bg-destructive/10 border-destructive/20 text-destructive"
        }`}>
          {notice.msg}
        </div>
      )}

      {/* Table */}
      {loading ? (
        <div className="rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                {["Task", "Agent", "Rating", "Category", "Submitter", "Submitted", "Status"].map(h => (
                  <TableHead key={h}><Skeleton className="h-4 w-20" /></TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {Array.from({ length: 4 }).map((_, i) => (
                <TableRow key={i}>
                  {Array.from({ length: 7 }).map((_, j) => (
                    <TableCell key={j}><Skeleton className="h-4 w-full" /></TableCell>
                  ))}
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : items.length === 0 ? (
        <div className="rounded-md border py-16 text-center text-sm text-muted-foreground">
          No pending feedback items.
        </div>
      ) : (
        <div className="rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Task</TableHead>
                <TableHead>Agent</TableHead>
                <TableHead>Rating</TableHead>
                <TableHead>Category</TableHead>
                <TableHead>Submitter</TableHead>
                <TableHead>Submitted</TableHead>
                <TableHead>Status</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map(item => (
                <TableRow
                  key={item.id}
                  className="cursor-pointer hover:bg-muted/50"
                  onClick={() => openDetail(item)}
                >
                  <TableCell>
                    <div className="font-medium text-sm">{item.taskName ?? item.scheduledTaskId}</div>
                    {item.taskType === "group" && (
                      <Badge variant="secondary" className="text-[10px] mt-0.5">Group</Badge>
                    )}
                  </TableCell>
                  <TableCell className="text-sm text-muted-foreground">
                    {item.agentDisplayName ?? "—"}
                  </TableCell>
                  <TableCell>
                    <div className="flex flex-col gap-0.5">
                      <ThumbsBadge v={item.thumbsRating} />
                      <Stars n={item.starRating} />
                    </div>
                  </TableCell>
                  <TableCell>
                    {item.category
                      ? <Badge variant="outline" className="text-xs">{item.category}</Badge>
                      : <span className="text-muted-foreground text-sm">—</span>}
                  </TableCell>
                  <TableCell className="text-sm text-muted-foreground">
                    {item.submitterName ?? item.submitterEmail ?? "—"}
                  </TableCell>
                  <TableCell className="text-xs text-muted-foreground whitespace-nowrap">
                    {new Date(item.submittedAt).toLocaleString()}
                  </TableCell>
                  <TableCell>
                    <StatusBadge status={item.status} />
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      {/* Detail popup */}
      <Dialog open={!!selected} onOpenChange={open => { if (!open) closeDetail(); }}>
        {selected && (
          <DialogContent className="max-w-2xl">
            <DialogHeader>
              <DialogTitle className="flex items-center gap-2 flex-wrap">
                {selected.taskName ?? selected.scheduledTaskId}
                <StatusBadge status={selected.status} />
                {selected.taskType === "group" && (
                  <Badge variant="secondary" className="text-xs">Group</Badge>
                )}
              </DialogTitle>
            </DialogHeader>

            <ScrollArea className="max-h-[60vh]">
              <div className="space-y-4 pr-1">

                {/* Run / session info */}
                <div className="rounded-md border bg-muted/30 p-3 space-y-1.5 text-sm">
                  <div className="flex flex-wrap gap-x-6 gap-y-1 text-muted-foreground">
                    {selected.agentDisplayName && (
                      <span>
                        <span className="text-foreground font-medium">Agent:</span>{" "}
                        {selected.agentDisplayName}
                      </span>
                    )}
                    <span>
                      <span className="text-foreground font-medium">Run ID:</span>{" "}
                      <span className="font-mono text-xs">{selected.runId}</span>
                    </span>
                  </div>
                  {selected.sessionId && (
                    <div className="flex items-center gap-2">
                      <span className="text-muted-foreground">
                        <span className="text-foreground font-medium">Session:</span>{" "}
                        <span className="font-mono text-xs">{selected.sessionId}</span>
                      </span>
                      <a
                        href={`${import.meta.env.BASE_URL.replace(/\/$/, '')}/sessions/${encodeURIComponent(selected.sessionId)}`}
                        target="_blank"
                        rel="noreferrer"
                        className="inline-flex items-center gap-1 text-xs text-blue-500 hover:underline"
                        onClick={e => e.stopPropagation()}
                      >
                        <ExternalLink className="h-3 w-3" /> View session
                      </a>
                    </div>
                  )}
                  <div className="text-xs text-muted-foreground">
                    Submitted: {new Date(selected.submittedAt).toLocaleString()}
                    {selected.submitterName && ` · ${selected.submitterName}`}
                    {selected.submitterEmail && ` (${selected.submitterEmail})`}
                  </div>
                </div>

                {/* Ratings */}
                <div className="flex items-center gap-5 text-sm flex-wrap">
                  {selected.thumbsRating != null && (
                    <div>
                      <span className="text-muted-foreground text-xs uppercase tracking-wide mr-1">Thumbs</span>
                      <ThumbsBadge v={selected.thumbsRating} />
                    </div>
                  )}
                  {selected.starRating != null && (
                    <div>
                      <span className="text-muted-foreground text-xs uppercase tracking-wide mr-1">Stars</span>
                      <Stars n={selected.starRating} />
                    </div>
                  )}
                  {selected.category && (
                    <div>
                      <span className="text-muted-foreground text-xs uppercase tracking-wide mr-1">Category</span>
                      <Badge variant="outline" className="text-xs">{selected.category}</Badge>
                    </div>
                  )}
                </div>

                {/* Correction text */}
                {selected.correctionText && (
                  <div className="rounded-md border border-amber-500/30 bg-amber-500/5 p-3 text-sm">
                    <p className="text-xs font-semibold text-amber-500 mb-1.5 uppercase tracking-wide">
                      Correction / Comment
                    </p>
                    <p className="text-foreground whitespace-pre-wrap leading-relaxed">
                      {selected.correctionText}
                    </p>
                  </div>
                )}

                {/* Existing review notes (for already-reviewed items) */}
                {selected.reviewNotes && (
                  <div className="rounded-md border bg-muted/30 p-3 text-sm">
                    <p className="text-xs font-medium text-muted-foreground mb-1">Admin notes</p>
                    <p className="text-foreground">{selected.reviewNotes}</p>
                  </div>
                )}

                {/* Reject reason input */}
                {rejectMode && (
                  <div className="space-y-1.5">
                    <Label htmlFor="reject-notes">Rejection notes (optional)</Label>
                    <Textarea
                      id="reject-notes"
                      rows={3}
                      placeholder="Reason for rejection…"
                      value={rejectNotes}
                      onChange={e => setRejectNotes(e.target.value)}
                    />
                  </div>
                )}
              </div>
            </ScrollArea>

            <DialogFooter className="gap-2">
              {selected.status === "pending" && !rejectMode && (
                <>
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => setRejectMode(true)}
                    disabled={actionLoading}
                  >
                    Reject
                  </Button>
                  <Button size="sm" onClick={handleApprove} disabled={actionLoading}>
                    {actionLoading ? "Approving…" : selected.correctionText ? "Approve & Learn" : "Approve"}
                  </Button>
                </>
              )}
              {rejectMode && (
                <>
                  <Button variant="ghost" size="sm" onClick={() => setRejectMode(false)}>
                    Back
                  </Button>
                  <Button
                    variant="destructive"
                    size="sm"
                    onClick={handleReject}
                    disabled={actionLoading}
                  >
                    {actionLoading ? "Rejecting…" : "Confirm Reject"}
                  </Button>
                </>
              )}
              {selected.status !== "pending" && (
                <Button variant="outline" size="sm" onClick={closeDetail}>Close</Button>
              )}
            </DialogFooter>
          </DialogContent>
        )}
      </Dialog>
    </div>
  );
}

