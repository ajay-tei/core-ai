import { useEffect, useState } from "react";
import { api, type PromotableVersion, type SnapshotFieldDiff, type LiveVersionInfo } from "@/api";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { ScrollArea } from "@/components/ui/scroll-area";
import { EnvironmentBadge } from "@/components/ui/environment-badge";
import { useEnvironment } from "@/hooks/useEnvironment";
import { ChevronDown, History, Loader2, RotateCcw } from "lucide-react";
import { toast } from "sonner";

interface VersionHistoryDialogProps
{
  open: boolean;
  onOpenChange: (open: boolean) => void;
  objectType: string;
  logicalId: string;
  displayName: string;
  /** Which environment's live pointer/rollback target this dialog operates on. */
  environmentId: number;
  /** Called after a successful rollback so the caller can refetch its own entity state. */
  onRolledBack?: () => void;
}

function SourceBadge({ source }: { source: string })
{
  switch (source)
  {
    case "publish":
      return <Badge className="bg-blue-500/15 text-blue-600 border-blue-500/30 dark:text-blue-400">Published</Badge>;
    case "promotion":
      return <Badge className="bg-purple-500/15 text-purple-600 border-purple-500/30 dark:text-purple-400">Promoted</Badge>;
    case "rollback":
      return <Badge className="bg-amber-500/15 text-amber-600 border-amber-500/30 dark:text-amber-400">Rolled back</Badge>;
    default:
      return <Badge variant="secondary">{source}</Badge>;
  }
}

/**
 * Read-only ledger viewer + rollback action for one promotable object's version history
 * (`PromotionLedgerService`, Phase D/B). Shared across all 4 promotable types — callers just
 * supply objectType/logicalId/displayName/environmentId (the environment whose live pointer
 * "Rollback" targets). Each row's "Changes" toggle diffs it against the PREVIOUS ledger entry
 * (a classic changelog reading), not against whatever is currently live.
 */
export function VersionHistoryDialog({ open, onOpenChange, objectType, logicalId, displayName, environmentId, onRolledBack }: VersionHistoryDialogProps)
{
  const { environments } = useEnvironment();
  const currentEnv = environments.find((e) => e.id === environmentId);

  const [versions, setVersions] = useState<PromotableVersion[]>([]);
  const [liveVersion, setLiveVersion] = useState<LiveVersionInfo | null>(null);
  const [loading, setLoading] = useState(false);
  const [expandedId, setExpandedId] = useState<number | null>(null);
  const [diffs, setDiffs] = useState<Record<number, SnapshotFieldDiff[] | "loading" | "error">>({});
  const [rollbackTarget, setRollbackTarget] = useState<PromotableVersion | null>(null);
  const [rollingBack, setRollingBack] = useState(false);

  const load = () =>
  {
    setLoading(true);
    Promise.all([api.getPromotionHistory(logicalId), api.getLiveVersion(logicalId, environmentId)])
      .then(([history, live]) => { setVersions(history); setLiveVersion(live); })
      .catch(() => toast.error("Failed to load version history"))
      .finally(() => setLoading(false));
  };

  useEffect(() =>
  {
    if (open) { load(); setExpandedId(null); setDiffs({}); }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, logicalId, environmentId]);

  const toggleExpand = (version: PromotableVersion, previousId: number | undefined) =>
  {
    const next = expandedId === version.id ? null : version.id;
    setExpandedId(next);
    if (next !== null && previousId !== undefined && !diffs[version.id])
    {
      setDiffs((d) => ({ ...d, [version.id]: "loading" }));
      api.getPromotionDiff(previousId, version.id)
        .then((diff) => setDiffs((d) => ({ ...d, [version.id]: diff })))
        .catch(() => setDiffs((d) => ({ ...d, [version.id]: "error" })));
    }
  };

  const handleRollback = async () =>
  {
    if (!rollbackTarget) return;
    setRollingBack(true);
    try
    {
      const result = await api.rollbackPromotion({ objectType, logicalId, environmentId, toVersionId: rollbackTarget.id });
      if (result.success)
      {
        toast.success(`Rolled back "${displayName}" to v${rollbackTarget.version}`);
        setRollbackTarget(null);
        load();
        onRolledBack?.();
      }
      else
      {
        toast.error(result.error ?? "Rollback failed");
      }
    }
    catch (e)
    {
      toast.error(e instanceof Error ? e.message : "Rollback failed");
    }
    finally
    {
      setRollingBack(false);
    }
  };

  return (
    <>
      <Dialog open={open} onOpenChange={onOpenChange}>
        <DialogContent className="sm:max-w-2xl">
          <DialogHeader>
            <DialogTitle className="flex items-center gap-2">
              <History className="size-4" /> Version History — {displayName}
            </DialogTitle>
            <DialogDescription className="flex items-center gap-1.5 flex-wrap">
              <span>Full change ledger for this object</span>
              {currentEnv && (
                <>
                  <span>· currently viewing</span>
                  <EnvironmentBadge environment={currentEnv} allEnvironments={environments} />
                </>
              )}
            </DialogDescription>
          </DialogHeader>

          {loading ? (
            <div className="flex items-center gap-2 text-sm text-muted-foreground py-6 justify-center">
              <Loader2 className="size-4 animate-spin" /> Loading history…
            </div>
          ) : versions.length === 0 ? (
            <p className="text-sm text-muted-foreground py-4">
              No recorded versions yet — publish or promote this object to start its history.
            </p>
          ) : (
            <ScrollArea className="max-h-[60vh] pr-3">
              <div className="space-y-2">
                {versions.map((v, i) =>
                {
                  const previous = versions[i + 1]; // list is newest-first
                  const isLive = liveVersion?.versionId === v.id;
                  const diff = diffs[v.id];
                  return (
                    <Collapsible key={v.id} open={expandedId === v.id} onOpenChange={() => toggleExpand(v, previous?.id)}>
                      <div className="rounded-md border px-3 py-2">
                        <div className="flex items-center gap-2 flex-wrap">
                          <Badge variant="outline">v{v.version}</Badge>
                          <SourceBadge source={v.source} />
                          {isLive && (
                            <Badge className="bg-emerald-500/15 text-emerald-600 border-emerald-500/30 dark:text-emerald-400">
                              Live here
                            </Badge>
                          )}
                          <span className="text-xs text-muted-foreground">
                            {new Date(v.createdAt).toLocaleString()}{v.createdBy && ` · ${v.createdBy}`}
                          </span>
                          <div className="ml-auto flex items-center gap-1.5">
                            {previous && (
                              <CollapsibleTrigger asChild>
                                <Button variant="ghost" size="sm" className="h-7 text-xs">
                                  <ChevronDown className={`size-3.5 mr-1 transition-transform ${expandedId === v.id ? "rotate-180" : ""}`} />
                                  Changes
                                </Button>
                              </CollapsibleTrigger>
                            )}
                            {!isLive && (
                              <Button variant="outline" size="sm" className="h-7 text-xs" onClick={() => setRollbackTarget(v)}>
                                <RotateCcw className="size-3.5 mr-1" /> Rollback
                              </Button>
                            )}
                          </div>
                        </div>
                        {v.changeNote && <p className="text-xs text-muted-foreground mt-1">{v.changeNote}</p>}
                        {previous && (
                          <CollapsibleContent className="mt-2 pt-2 border-t space-y-1">
                            {diff === "loading" || diff === undefined ? (
                              <div className="flex items-center gap-2 text-xs text-muted-foreground py-1">
                                <Loader2 className="size-3 animate-spin" /> Loading changes…
                              </div>
                            ) : diff === "error" ? (
                              <p className="text-xs text-destructive">Failed to load diff.</p>
                            ) : diff.length === 0 ? (
                              <p className="text-xs text-muted-foreground">No field-level changes detected.</p>
                            ) : (
                              diff.map((d) => (
                                <div key={d.fieldPath} className="text-xs grid grid-cols-[minmax(0,140px)_1fr] gap-2">
                                  <span className="font-mono text-muted-foreground truncate" title={d.fieldPath}>{d.fieldPath}</span>
                                  <span>
                                    <span className="text-destructive/80 line-through">{d.oldValue ?? "∅"}</span>
                                    {" → "}
                                    <span className="text-emerald-600 dark:text-emerald-400">{d.newValue ?? "∅"}</span>
                                  </span>
                                </div>
                              ))
                            )}
                          </CollapsibleContent>
                        )}
                      </div>
                    </Collapsible>
                  );
                })}
              </div>
            </ScrollArea>
          )}

          <DialogFooter>
            <Button variant="outline" onClick={() => onOpenChange(false)}>Close</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={!!rollbackTarget} onOpenChange={(o) => !o && setRollbackTarget(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Rollback to v{rollbackTarget?.version}</DialogTitle>
            <DialogDescription>
              This makes v{rollbackTarget?.version}'s content live in <strong>{currentEnv?.displayName ?? "this environment"}</strong> immediately
              (recorded as a new version — nothing is deleted, you can roll forward again later).
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setRollbackTarget(null)}>Cancel</Button>
            <Button variant="destructive" onClick={handleRollback} disabled={rollingBack}>
              {rollingBack ? "Rolling back…" : `Rollback to v${rollbackTarget?.version}`}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </>
  );
}
