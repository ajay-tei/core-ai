import { useEffect, useState, useMemo } from "react";
import { useParams, Link } from "react-router";
import type { OptimizationSuggestion, OptimizationRunSummary, OptimizationSuggestionListParams } from "@/api";
import {
  getOptimizationSuggestions, getOptimizationRuns,
  reviewSuggestion, applySuggestion,
  mergePrompt, applyMerged,
} from "@/api";
import { usePagedList } from "@/hooks/usePagedList";
import { ListPagination } from "@/components/ui/list-toolbar";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter,
} from "@/components/ui/dialog";
import { Slider } from "@/components/ui/slider";

// ── Constants ─────────────────────────────────────────────────────────────────

const PROMPT_TYPES = new Set(["SystemPromptImprovement", "ToolStrategyHint"]);

const TYPE_COLORS: Record<string, string> = {
  SystemPromptImprovement:    "bg-blue-100 text-blue-800 border-blue-200",
  ToolStrategyHint:           "bg-blue-100 text-blue-800 border-blue-200",
  TemperatureAdjustment:      "bg-orange-100 text-orange-800 border-orange-200",
  MaxIterationsAdjustment:    "bg-orange-100 text-orange-800 border-orange-200",
  MaxContinuationsAdjustment: "bg-orange-100 text-orange-800 border-orange-200",
  VerificationModeUpgrade:    "bg-purple-100 text-purple-800 border-purple-200",
  ModelSwitch:                "bg-purple-100 text-purple-800 border-purple-200",
  ContextWindowAdjustment:    "bg-orange-100 text-orange-800 border-orange-200",
  RulePackSuggestion:         "bg-green-100 text-green-800 border-green-200",
};

// ── Badges ────────────────────────────────────────────────────────────────────

function TypeBadge({ type }: { type: string }) {
  const cls = TYPE_COLORS[type] ?? "bg-muted text-muted-foreground border-border";
  const label = type.replace(/([A-Z])/g, " $1").trim();
  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-[10px] font-medium whitespace-nowrap ${cls}`}>
      {label}
    </span>
  );
}

function ConfidenceBadge({ value }: { value: number }) {
  const pct = Math.round(value * 100);
  const cls = value >= 0.8
    ? "bg-green-100 text-green-800 border-green-200"
    : value >= 0.6
    ? "bg-yellow-100 text-yellow-800 border-yellow-200"
    : "bg-red-100 text-red-800 border-red-200";
  return (
    <span className={`inline-flex items-center rounded-full border px-2 py-0.5 text-[10px] font-medium ${cls}`}>
      {pct}%
    </span>
  );
}

function StatusBadge({ status }: { status: string }) {
  const variant =
    status === "Applied"  ? "default"     :
    status === "Approved" ? "secondary"   :
    status === "Rejected" ? "destructive" : "outline";
  return <Badge variant={variant} className="text-xs">{status}</Badge>;
}

// ── Expandable text cell ──────────────────────────────────────────────────────

function Expandable({ text, maxLen = 120 }: { text: string; maxLen?: number }) {
  const [open, setOpen] = useState(false);
  if (text.length <= maxLen) return <span className="text-xs leading-relaxed">{text}</span>;
  return (
    <span className="text-xs leading-relaxed">
      {open ? text : text.slice(0, maxLen) + "…"}
      <button onClick={() => setOpen(o => !o)}
        className="ml-1 text-primary hover:underline text-[10px]">
        {open ? "less" : "more"}
      </button>
    </span>
  );
}

// ── Filter bar (shared between tabs) ─────────────────────────────────────────

interface FilterBarProps {
  filterStatus: string;
  setFilterStatus: (v: string) => void;
  filterRunId: number | "all";
  setFilterRunId: (v: number | "all") => void;
  minConfidence: number;
  setMinConfidence: (v: number) => void;
  runs: OptimizationRunSummary[];
  shownCount: number;
  totalCount: number;
}

function FilterBar({
  filterStatus, setFilterStatus,
  filterRunId, setFilterRunId,
  minConfidence, setMinConfidence,
  runs, shownCount, totalCount,
}: FilterBarProps) {
  return (
    <div className="flex flex-wrap gap-3 items-end px-1 py-3 border-b">
      {/* Status */}
      <div className="flex flex-col gap-1">
        <label className="text-xs text-muted-foreground font-medium">Status</label>
        <Select value={filterStatus} onValueChange={setFilterStatus}>
          <SelectTrigger className="h-8 w-32 text-xs">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All</SelectItem>
            <SelectItem value="Pending">Pending</SelectItem>
            <SelectItem value="Approved">Approved</SelectItem>
            <SelectItem value="Rejected">Rejected</SelectItem>
            <SelectItem value="Applied">Applied</SelectItem>
          </SelectContent>
        </Select>
      </div>

      {/* Run */}
      <div className="flex flex-col gap-1">
        <label className="text-xs text-muted-foreground font-medium">Run</label>
        <Select
          value={filterRunId === "all" ? "all" : String(filterRunId)}
          onValueChange={v => setFilterRunId(v === "all" ? "all" : parseInt(v))}>
          <SelectTrigger className="h-8 w-44 text-xs">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All Runs</SelectItem>
            {runs.map(r => (
              <SelectItem key={r.id} value={String(r.id)}>
                #{r.id} — {new Date(r.startedAt).toLocaleDateString()}
                {r.sessionId ? " (session)" : ""}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
      </div>

      {/* Confidence */}
      <div className="flex flex-col gap-1">
        <label className="text-xs text-muted-foreground font-medium">
          Min Confidence: {Math.round(minConfidence * 100)}%
        </label>
        <Slider
          min={0} max={1} step={0.05}
          value={[minConfidence]}
          onValueChange={([v]) => setMinConfidence(v)}
          className="w-32 mt-1"
        />
      </div>

      <span className="text-xs text-muted-foreground self-end pb-1 ml-auto">
        {shownCount} of {totalCount}
      </span>
    </div>
  );
}

// ── Merge preview dialog ──────────────────────────────────────────────────────

function MergePreviewModal({
  merged, onConfirm, onCancel, confirming,
}: {
  merged: string;
  onConfirm: () => void;
  onCancel: () => void;
  confirming: boolean;
}) {
  return (
    <Dialog open onOpenChange={onCancel}>
      <DialogContent className="max-w-2xl">
        <DialogHeader>
          <DialogTitle>Smart Merge Preview</DialogTitle>
        </DialogHeader>
        <p className="text-sm text-muted-foreground">
          Review the LLM-merged system prompt. All selected suggestions have been
          intelligently integrated — no redundant appends.
        </p>
        <div className="rounded-md border bg-muted/40 p-3 max-h-96 overflow-y-auto">
          <pre className="text-xs whitespace-pre-wrap break-words font-mono leading-relaxed">
            {merged}
          </pre>
        </div>
        <DialogFooter>
          <Button variant="outline" onClick={onCancel} disabled={confirming}>Cancel</Button>
          <Button onClick={onConfirm} disabled={confirming}>
            {confirming ? "Applying…" : "Confirm & Apply"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ── Prompt suggestions tab ────────────────────────────────────────────────────

interface PromptTabProps {
  items: OptimizationSuggestion[];
  selected: Set<number>;
  onToggleOne: (id: number, checked: boolean) => void;
  onToggleAll: (checked: boolean) => void;
  onApplySelected: () => void;
  onApplyRow: (s: OptimizationSuggestion) => void;
  onApprove: (id: number) => void;
  onReject: (id: number) => void;
  actioningIds: Set<number>;
  merging: boolean;
}

function PromptTab({
  items, selected, onToggleOne, onToggleAll,
  onApplySelected, onApplyRow, onApprove, onReject,
  actioningIds, merging,
}: PromptTabProps) {
  const allSelected = items.length > 0 && items.every(s => selected.has(s.id));
  const someSelected = items.some(s => selected.has(s.id)) && !allSelected;
  const selCount = items.filter(s => selected.has(s.id)).length;

  return (
    <div className="space-y-3">
      {/* Toolbar */}
      <div className="flex items-center gap-2 py-2">
        <span className="text-xs text-muted-foreground">{selCount} selected</span>
        <Button
          size="sm" className="ml-auto"
          disabled={selCount === 0 || merging}
          onClick={onApplySelected}
        >
          {merging ? "Merging…" : `Apply Merge (${selCount})`}
        </Button>
      </div>

      {items.length === 0 ? (
        <p className="text-sm text-muted-foreground py-8 text-center">
          No system prompt suggestions match the current filters.
        </p>
      ) : (
        <div className="overflow-x-auto rounded-md border">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b bg-muted/50">
                <th className="w-8 px-3 py-2 text-left">
                  <input type="checkbox"
                    checked={allSelected}
                    ref={el => { if (el) el.indeterminate = someSelected; }}
                    onChange={e => onToggleAll(e.target.checked)}
                    className="rounded" />
                </th>
                <th className="w-36 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Type</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-muted-foreground">Suggested Change</th>
                <th className="w-24 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Reasoning</th>
                <th className="w-16 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Conf</th>
                <th className="w-14 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Run</th>
                <th className="w-20 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Status</th>
                <th className="w-32 px-3 py-2 text-right text-xs font-medium text-muted-foreground">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y">
              {items.map(s => {
                const acting = actioningIds.has(s.id);
                const actionable = s.status !== "Applied" && s.status !== "Rejected";
                return (
                  <tr key={s.id} className={selected.has(s.id) ? "bg-blue-50/50" : "hover:bg-muted/20"}>
                    <td className="px-3 py-2.5">
                      <input type="checkbox"
                        checked={selected.has(s.id)}
                        onChange={e => onToggleOne(s.id, e.target.checked)}
                        className="rounded" />
                    </td>
                    <td className="px-3 py-2.5">
                      <TypeBadge type={s.type} />
                    </td>
                    <td className="px-3 py-2.5 max-w-xs">
                      <Expandable text={s.suggestedValue} maxLen={180} />
                    </td>
                    <td className="px-3 py-2.5 max-w-[200px]">
                      <Expandable text={s.reasoning} maxLen={80} />
                    </td>
                    <td className="px-3 py-2.5">
                      <ConfidenceBadge value={s.confidence} />
                    </td>
                    <td className="px-3 py-2.5 text-xs text-muted-foreground whitespace-nowrap">
                      #{s.runId}
                    </td>
                    <td className="px-3 py-2.5">
                      <StatusBadge status={s.status} />
                    </td>
                    <td className="px-3 py-2.5">
                      <div className="flex justify-end gap-1">
                        {actionable && (
                          <Button size="sm" disabled={acting} onClick={() => onApplyRow(s)}>
                            {acting ? "…" : "Apply"}
                          </Button>
                        )}
                        {s.status === "Pending" && (
                          <Button size="sm" variant="secondary" disabled={acting}
                            onClick={() => onApprove(s.id)}>
                            Approve
                          </Button>
                        )}
                        {actionable && (
                          <Button size="sm" variant="outline" disabled={acting}
                            onClick={() => onReject(s.id)}>
                            Reject
                          </Button>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Config suggestions tab ────────────────────────────────────────────────────

interface ConfigTabProps {
  items: OptimizationSuggestion[];
  selected: Set<number>;
  onToggleOne: (id: number, checked: boolean) => void;
  onToggleAll: (checked: boolean) => void;
  onApplySelected: () => void;
  onApplyRow: (s: OptimizationSuggestion) => void;
  onApprove: (id: number) => void;
  onReject: (id: number) => void;
  actioningIds: Set<number>;
  working: boolean;
}

function ConfigTab({
  items, selected, onToggleOne, onToggleAll,
  onApplySelected, onApplyRow, onApprove, onReject,
  actioningIds, working,
}: ConfigTabProps) {
  const allSelected = items.length > 0 && items.every(s => selected.has(s.id));
  const someSelected = items.some(s => selected.has(s.id)) && !allSelected;
  const selCount = items.filter(s => selected.has(s.id)).length;

  return (
    <div className="space-y-3">
      {/* Toolbar */}
      <div className="flex items-center gap-2 py-2">
        <span className="text-xs text-muted-foreground">{selCount} selected</span>
        <Button
          size="sm" className="ml-auto"
          disabled={selCount === 0 || working}
          onClick={onApplySelected}
        >
          {working ? "Applying…" : `Apply Selected (${selCount})`}
        </Button>
      </div>

      {items.length === 0 ? (
        <p className="text-sm text-muted-foreground py-8 text-center">
          No configuration suggestions match the current filters.
        </p>
      ) : (
        <div className="overflow-x-auto rounded-md border">
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b bg-muted/50">
                <th className="w-8 px-3 py-2 text-left">
                  <input type="checkbox"
                    checked={allSelected}
                    ref={el => { if (el) el.indeterminate = someSelected; }}
                    onChange={e => onToggleAll(e.target.checked)}
                    className="rounded" />
                </th>
                <th className="w-44 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Type</th>
                <th className="w-32 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Field</th>
                <th className="w-36 px-3 py-2 text-left text-xs font-medium text-muted-foreground">New Value</th>
                <th className="px-3 py-2 text-left text-xs font-medium text-muted-foreground">Reasoning</th>
                <th className="w-16 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Conf</th>
                <th className="w-14 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Run</th>
                <th className="w-20 px-3 py-2 text-left text-xs font-medium text-muted-foreground">Status</th>
                <th className="w-32 px-3 py-2 text-right text-xs font-medium text-muted-foreground">Actions</th>
              </tr>
            </thead>
            <tbody className="divide-y">
              {items.map(s => {
                const acting = actioningIds.has(s.id);
                const actionable = s.status !== "Applied" && s.status !== "Rejected";
                const isRulePack = s.type === "RulePackSuggestion";
                return (
                  <tr key={s.id} className={selected.has(s.id) ? "bg-blue-50/50" : "hover:bg-muted/20"}>
                    <td className="px-3 py-2.5">
                      <input type="checkbox"
                        checked={selected.has(s.id)}
                        onChange={e => onToggleOne(s.id, e.target.checked)}
                        className="rounded" />
                    </td>
                    <td className="px-3 py-2.5">
                      <TypeBadge type={s.type} />
                    </td>
                    <td className="px-3 py-2.5 text-xs text-muted-foreground">
                      {s.fieldName}
                    </td>
                    <td className="px-3 py-2.5">
                      {isRulePack ? (
                        <span className="text-xs text-amber-700 bg-amber-50 border border-amber-200 rounded px-1.5 py-0.5">
                          Enable rule pack
                        </span>
                      ) : (
                        <code className="text-xs bg-muted px-1.5 py-0.5 rounded font-mono">
                          {s.suggestedValue}
                        </code>
                      )}
                    </td>
                    <td className="px-3 py-2.5 min-w-[180px]">
                      <Expandable text={s.reasoning} maxLen={100} />
                    </td>
                    <td className="px-3 py-2.5">
                      <ConfidenceBadge value={s.confidence} />
                    </td>
                    <td className="px-3 py-2.5 text-xs text-muted-foreground whitespace-nowrap">
                      #{s.runId}
                    </td>
                    <td className="px-3 py-2.5">
                      <StatusBadge status={s.status} />
                    </td>
                    <td className="px-3 py-2.5">
                      <div className="flex justify-end gap-1">
                        {actionable && (
                          <Button size="sm" disabled={acting} onClick={() => onApplyRow(s)}>
                            {acting ? "…" : "Apply"}
                          </Button>
                        )}
                        {s.status === "Pending" && (
                          <Button size="sm" variant="secondary" disabled={acting}
                            onClick={() => onApprove(s.id)}>
                            Approve
                          </Button>
                        )}
                        {actionable && (
                          <Button size="sm" variant="outline" disabled={acting}
                            onClick={() => onReject(s.id)}>
                            Reject
                          </Button>
                        )}
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// ── Main component ────────────────────────────────────────────────────────────

export default function AgentOptimizationSuggestions() {
  const { id: agentId } = useParams<{ id: string }>();
  const aid = agentId!;

  const { result, loading, params, update, setPage, reload } =
    usePagedList<OptimizationSuggestion, OptimizationSuggestionListParams>(
      (p) => getOptimizationSuggestions(aid, p),
      { page: 1, pageSize: 25 }
    );
  const [suggestions, setSuggestions] = useState<OptimizationSuggestion[]>([]);
  const [runs, setRuns]               = useState<OptimizationRunSummary[]>([]);
  const [error, setError]             = useState<string | null>(null);

  // Mirror the current page's fetched suggestions into local state so row actions can
  // optimistically update status without a full re-fetch (existing behavior, preserved).
  useEffect(() => { if (result) setSuggestions(result.items); }, [result]);

  // Filters — now server-side (sent as query params via `params`/`update`) instead of
  // client-side; kept as local aliases so the existing FilterBar props stay unchanged.
  const filterStatus = params.status ?? "all";
  const setFilterStatus = (v: string) => update({ status: v === "all" ? undefined : v });
  const filterRunId = params.runId ?? "all";
  const setFilterRunId = (v: number | "all") => update({ runId: v === "all" ? undefined : v });
  const minConfidence = params.minConfidence ?? 0;
  const setMinConfidence = (v: number) => update({ minConfidence: v || undefined });

  // Per-tab selection
  const [promptSelected, setPromptSelected] = useState<Set<number>>(new Set());
  const [configSelected, setConfigSelected] = useState<Set<number>>(new Set());

  // Row-level action tracking
  const [actioningIds, setActioningIds] = useState<Set<number>>(new Set());

  // Merge state (prompt tab)
  const [merging, setMerging]       = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [mergePreview, setMergePreview] = useState<{
    mergedPrompt: string;
    suggestionIds: number[];
  } | null>(null);

  // Config tab working state
  const [configWorking, setConfigWorking] = useState(false);

  useEffect(() => {
    getOptimizationRuns(aid).then(setRuns).catch(() => setRuns([]));
  }, [aid]);

  const promptVisible = useMemo(() => suggestions.filter(s => PROMPT_TYPES.has(s.type)), [suggestions]);
  const configVisible = useMemo(() => suggestions.filter(s => !PROMPT_TYPES.has(s.type)), [suggestions]);

  // ── Prompt tab handlers ───────────────────────────────────────────────────

  function togglePromptOne(id: number, checked: boolean) {
    setPromptSelected(prev => { const n = new Set(prev); checked ? n.add(id) : n.delete(id); return n; });
  }
  function togglePromptAll(checked: boolean) {
    setPromptSelected(checked ? new Set(promptVisible.map(s => s.id)) : new Set());
  }

  async function applyPromptMerge(ids: number[]) {
    if (ids.length === 0) return;
    setMerging(true);
    setError(null);
    try {
      const pending = ids.filter(id => suggestions.find(s => s.id === id)?.status === "Pending");
      if (pending.length > 0) {
        await Promise.all(pending.map(id => reviewSuggestion(aid, id, "approve")));
        setSuggestions(prev => prev.map(s => pending.includes(s.id) ? { ...s, status: "Approved" } : s));
      }
      const { mergedPrompt } = await mergePrompt(aid, ids);
      setMergePreview({ mergedPrompt, suggestionIds: ids });
    } catch (e: unknown) {
      setError((e as { error?: string })?.error ?? "Merge failed");
    } finally {
      setMerging(false);
    }
  }

  async function confirmMerge() {
    if (!mergePreview) return;
    setConfirming(true);
    try {
      await applyMerged(aid, mergePreview.mergedPrompt, mergePreview.suggestionIds);
      setSuggestions(prev =>
        prev.map(s => mergePreview.suggestionIds.includes(s.id) ? { ...s, status: "Applied" } : s)
      );
      setPromptSelected(prev => {
        const n = new Set(prev);
        mergePreview.suggestionIds.forEach(id => n.delete(id));
        return n;
      });
      setMergePreview(null);
    } catch (e: unknown) {
      setError((e as { error?: string })?.error ?? "Apply merged failed");
    } finally {
      setConfirming(false);
    }
  }

  // ── Config tab handlers ───────────────────────────────────────────────────

  function toggleConfigOne(id: number, checked: boolean) {
    setConfigSelected(prev => { const n = new Set(prev); checked ? n.add(id) : n.delete(id); return n; });
  }
  function toggleConfigAll(checked: boolean) {
    setConfigSelected(checked ? new Set(configVisible.map(s => s.id)) : new Set());
  }

  async function applyConfig(ids: number[]) {
    if (ids.length === 0) return;
    setConfigWorking(true);
    setError(null);
    try {
      await Promise.all(ids.map(async id => {
        const s = suggestions.find(x => x.id === id)!;
        setActioningIds(prev => new Set(prev).add(id));
        if (s.status === "Pending") await reviewSuggestion(aid, id, "approve");
        await applySuggestion(aid, id, "append");
      }));
      setSuggestions(prev => prev.map(s => ids.includes(s.id) ? { ...s, status: "Applied" } : s));
      setConfigSelected(prev => { const n = new Set(prev); ids.forEach(id => n.delete(id)); return n; });
    } catch (e: unknown) {
      setError((e as { error?: string })?.error ?? "Failed to apply");
    } finally {
      setConfigWorking(false);
      setActioningIds(prev => { const n = new Set(prev); ids.forEach(id => n.delete(id)); return n; });
    }
  }

  // ── Shared handlers ───────────────────────────────────────────────────────

  async function handleReview(id: number, action: "approve" | "reject") {
    setActioningIds(prev => new Set(prev).add(id));
    try {
      await reviewSuggestion(aid, id, action);
      setSuggestions(prev => prev.map(s =>
        s.id === id ? { ...s, status: action === "approve" ? "Approved" : "Rejected" } : s
      ));
    } catch (e: unknown) {
      setError((e as { error?: string })?.error ?? "Failed");
    } finally {
      setActioningIds(prev => { const n = new Set(prev); n.delete(id); return n; });
    }
  }

  async function handleApplyPromptRow(s: OptimizationSuggestion) {
    setActioningIds(prev => new Set(prev).add(s.id));
    try { await applyPromptMerge([s.id]); }
    finally { setActioningIds(prev => { const n = new Set(prev); n.delete(s.id); return n; }); }
  }

  async function handleApplyConfigRow(s: OptimizationSuggestion) {
    setActioningIds(prev => new Set(prev).add(s.id));
    try {
      if (s.status === "Pending") {
        await reviewSuggestion(aid, s.id, "approve");
        setSuggestions(prev => prev.map(x => x.id === s.id ? { ...x, status: "Approved" } : x));
      }
      await applySuggestion(aid, s.id, "append");
      setSuggestions(prev => prev.map(x => x.id === s.id ? { ...x, status: "Applied" } : x));
    } catch (e: unknown) {
      setError((e as { error?: string })?.error ?? "Failed to apply");
    } finally {
      setActioningIds(prev => { const n = new Set(prev); n.delete(s.id); return n; });
    }
  }

  if (loading) {
    return (
      <div className="p-6 flex items-center gap-3 text-muted-foreground text-sm">
        <span className="inline-block h-3 w-3 rounded-full bg-primary animate-pulse" />
        Loading suggestions…
      </div>
    );
  }

  return (
    <div className="p-6 max-w-7xl mx-auto space-y-4">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <Link to={`/agents/${aid}/optimize`}
            className="text-sm text-muted-foreground hover:underline">
            ← Optimizer
          </Link>
          <h1 className="text-2xl font-bold mt-1">Optimization Suggestions</h1>
        </div>
        <Button variant="outline" size="sm" onClick={reload} disabled={loading}>
          Refresh
        </Button>
      </div>

      {error && (
        <div className="rounded-md border border-destructive/40 bg-destructive/10 text-destructive px-4 py-3 text-sm">
          {error}
        </div>
      )}

      {/* Tabbed layout */}
      <Card>
        <CardContent className="p-0">
          <Tabs defaultValue="prompt">
            <div className="px-4 pt-4 border-b">
              <TabsList className="mb-0">
                <TabsTrigger value="prompt">
                  System Prompt
                  <Badge variant="secondary" className="ml-2 text-[10px]">
                    {promptVisible.length}
                  </Badge>
                </TabsTrigger>
                <TabsTrigger value="config">
                  Agent Configuration
                  <Badge variant="secondary" className="ml-2 text-[10px]">
                    {configVisible.length}
                  </Badge>
                </TabsTrigger>
              </TabsList>
            </div>

            {/* Filter bar — inside card, below tabs */}
            <FilterBar
              filterStatus={filterStatus} setFilterStatus={setFilterStatus}
              filterRunId={filterRunId} setFilterRunId={setFilterRunId}
              minConfidence={minConfidence} setMinConfidence={setMinConfidence}
              runs={runs}
              shownCount={result?.items.length ?? 0}
              totalCount={result?.totalCount ?? 0}
            />

            <TabsContent value="prompt" className="px-4 pb-4 mt-0">
              <PromptTab
                items={promptVisible}
                selected={promptSelected}
                onToggleOne={togglePromptOne}
                onToggleAll={togglePromptAll}
                onApplySelected={() =>
                  applyPromptMerge(promptVisible.filter(s => promptSelected.has(s.id)).map(s => s.id))
                }
                onApplyRow={handleApplyPromptRow}
                onApprove={id => handleReview(id, "approve")}
                onReject={id => handleReview(id, "reject")}
                actioningIds={actioningIds}
                merging={merging}
              />
            </TabsContent>

            <TabsContent value="config" className="px-4 pb-4 mt-0">
              <ConfigTab
                items={configVisible}
                selected={configSelected}
                onToggleOne={toggleConfigOne}
                onToggleAll={toggleConfigAll}
                onApplySelected={() =>
                  applyConfig(configVisible.filter(s => configSelected.has(s.id)).map(s => s.id))
                }
                onApplyRow={handleApplyConfigRow}
                onApprove={id => handleReview(id, "approve")}
                onReject={id => handleReview(id, "reject")}
                actioningIds={actioningIds}
                working={configWorking}
              />
            </TabsContent>
          </Tabs>
        </CardContent>
      </Card>

      {result && (
        <ListPagination
          page={result.page}
          totalPages={result.totalPages}
          totalCount={result.totalCount}
          onPageChange={setPage}
          itemLabel="total"
        />
      )}

      {/* Merge preview dialog */}
      {mergePreview && (
        <MergePreviewModal
          merged={mergePreview.mergedPrompt}
          onConfirm={confirmMerge}
          onCancel={() => setMergePreview(null)}
          confirming={confirming}
        />
      )}
    </div>
  );
}
