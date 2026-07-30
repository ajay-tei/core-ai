import { useState } from "react";
import { api, type SuggestedRule, type PendingRulesListParams } from "@/api";
import { usePagedList } from "@/hooks/usePagedList";
import { ListToolbar, ListPagination } from "@/components/ui/list-toolbar";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Progress } from "@/components/ui/progress";
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogClose,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardFooter, CardHeader } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/ui/empty-state";
import { RefreshCw, CheckCircle, XCircle, Brain } from "lucide-react";

const CATEGORY_COLORS: Record<string, string> = {
  general:         "bg-blue-500/10 text-blue-400 border-blue-500/20",
  tone:            "bg-purple-500/10 text-purple-400 border-purple-500/20",
  response_format: "bg-cyan-500/10 text-cyan-400 border-cyan-500/20",
  safety:          "bg-red-500/10 text-red-400 border-red-500/20",
  terminology:     "bg-amber-500/10 text-amber-400 border-amber-500/20",
  seasonal:        "bg-green-500/10 text-green-400 border-green-500/20",
};

export function PendingRules() {
  const { result, loading, params, update, updateDebounced, setPage, reload } =
    usePagedList<SuggestedRule, PendingRulesListParams>(api.getPendingRules, { page: 1, pageSize: 25 });
  const [busy, setBusy]                 = useState<Set<number>>(new Set());
  const [rejectDialogId, setRejectDialogId] = useState<number | null>(null);
  const [rejectNote, setRejectNote]     = useState("");

  const approve = async (rule: SuggestedRule) => {
    setBusy(prev => new Set(prev).add(rule.id));
    try {
      await api.approveRule(rule.id);
      toast.success("Rule approved and promoted to business rules.");
      reload();
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setBusy(prev => { const s = new Set(prev); s.delete(rule.id); return s; }); }
  };

  const openReject  = (id: number) => { setRejectDialogId(id); setRejectNote(""); };

  const confirmReject = async () => {
    if (!rejectDialogId) return;
    const rule = (result?.items ?? []).find(r => r.id === rejectDialogId);
    if (!rule) return;
    setBusy(prev => new Set(prev).add(rule.id));
    setRejectDialogId(null);
    try {
      await api.rejectRule(rule.id, rejectNote);
      toast.success("Rule rejected.");
      reload();
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setBusy(prev => { const s = new Set(prev); s.delete(rule.id); return s; }); }
  };

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h2 className="text-xl font-semibold">Learned Rules — Pending Approval</h2>
          <p className="text-sm text-muted-foreground">
            Rules detected from agent conversations. Approve to promote into active business rules.
          </p>
        </div>
        <Button size="sm" variant="outline" onClick={reload} disabled={loading} className="h-8">
          <RefreshCw className={`h-3.5 w-3.5 mr-1.5 ${loading ? "animate-spin" : ""}`} />
          Refresh
        </Button>
      </div>

      <ListToolbar
        searchValue={params.search}
        onSearchChange={v => updateDebounced({ search: v || undefined })}
        searchPlaceholder="Search rules…"
        pageSize={params.pageSize}
        onPageSizeChange={pageSize => update({ pageSize })}
        pageSizeOptions={[25, 50, 100]}
      />

      {loading ? (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {Array.from({ length: 3 }).map((_, i) => (
            <Card key={i}>
              <CardHeader className="pb-2 space-y-2">
                <Skeleton className="h-4 w-24" />
                <Skeleton className="h-3 w-40" />
              </CardHeader>
              <CardContent><Skeleton className="h-16 w-full" /></CardContent>
              <CardFooter><Skeleton className="h-8 w-32" /></CardFooter>
            </Card>
          ))}
        </div>
      ) : (result?.items.length ?? 0) === 0 ? (
        <EmptyState
          icon={Brain}
          title="No pending rules"
          description="Agents will suggest rules as they learn from conversations."
          action={{ label: "Refresh", onClick: reload }}
        />
      ) : (
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
          {(result?.items ?? []).map(rule => {
            const catCls    = CATEGORY_COLORS[rule.ruleCategory || "general"] ?? "bg-muted text-muted-foreground";
            const confidence = Math.round(rule.confidence * 100);
            const confColor  = confidence >= 80
              ? "[&>div]:bg-emerald-500"
              : confidence >= 60
              ? "[&>div]:bg-amber-500"
              : "";
            return (
              <Card key={rule.id} className="flex flex-col">
                <CardHeader className="pb-3">
                  <div className="flex items-center justify-between gap-2 flex-wrap">
                    <Badge variant="outline" className={`text-xs ${catCls}`}>
                      {rule.ruleCategory || "general"}
                    </Badge>
                    {rule.agentType && rule.agentType !== "*" && (
                      <Badge variant="secondary" className="text-xs font-mono">
                        {rule.agentType}
                      </Badge>
                    )}
                  </div>
                  <div className="font-mono text-xs text-muted-foreground mt-1">{rule.ruleKey}</div>
                  <div className="space-y-1 mt-1">
                    <div className="flex items-center justify-between text-xs text-muted-foreground">
                      <span>Confidence</span>
                      <span className={
                        confidence >= 80 ? "text-emerald-500"
                        : confidence >= 60 ? "text-amber-500"
                        : ""
                      }>
                        {confidence}%
                      </span>
                    </div>
                    <Progress value={confidence} className={`h-1.5 ${confColor}`} />
                  </div>
                </CardHeader>

                <CardContent className="flex-1 pb-3">
                  <div className="rounded-md bg-muted/50 border px-3 py-2 text-sm leading-relaxed">
                    {rule.promptInjection}
                  </div>
                  <div className="text-xs text-muted-foreground mt-2">
                    From session:{" "}
                    <span className="font-mono">{rule.sourceSessionId.slice(0, 12)}…</span>
                    <span className="mx-1">·</span>
                    {new Date(rule.suggestedAt).toLocaleDateString()}
                  </div>
                </CardContent>

                <CardFooter className="gap-2">
                  <Button
                    size="sm"
                    className="flex-1 bg-emerald-600 hover:bg-emerald-700 text-white"
                    onClick={() => approve(rule)}
                    disabled={busy.has(rule.id)}
                  >
                    <CheckCircle className="h-3.5 w-3.5 mr-1.5" />
                    {busy.has(rule.id) ? "…" : "Approve"}
                  </Button>
                  <Button
                    size="sm"
                    variant="outline"
                    className="flex-1 text-destructive hover:text-destructive border-destructive/30 hover:bg-destructive/10"
                    onClick={() => openReject(rule.id)}
                    disabled={busy.has(rule.id)}
                  >
                    <XCircle className="h-3.5 w-3.5 mr-1.5" />
                    Reject
                  </Button>
                </CardFooter>
              </Card>
            );
          })}
        </div>
      )}

      {result && (
        <ListPagination
          page={result.page}
          totalPages={result.totalPages}
          totalCount={result.totalCount}
          onPageChange={setPage}
          itemLabel="total"
        />
      )}

      <Dialog
        open={rejectDialogId !== null}
        onOpenChange={(open: boolean) => !open && setRejectDialogId(null)}
      >
        <DialogContent className="sm:max-w-sm">
          <DialogHeader>
            <DialogTitle>Reject Rule</DialogTitle>
          </DialogHeader>
          <div className="space-y-3 py-2">
            <p className="text-sm text-muted-foreground">
              Optionally provide a reason for rejecting this rule.
            </p>
            <div className="space-y-1.5">
              <Label>Rejection reason</Label>
              <Input
                value={rejectNote}
                onChange={e => setRejectNote(e.target.value)}
                placeholder="Optional notes…"
              />
            </div>
          </div>
          <DialogFooter>
            <DialogClose asChild>
              <Button variant="outline">Cancel</Button>
            </DialogClose>
            <Button variant="destructive" onClick={confirmReject}>
              Confirm Reject
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
