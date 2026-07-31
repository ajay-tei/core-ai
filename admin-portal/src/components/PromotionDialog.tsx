import { useEffect, useState } from "react";
import { api, type BulkPromoteResultItem, type PromotionPreview } from "@/api";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogDescription } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { AlertTriangle, ArrowRight, CheckCircle2, Loader2, XCircle } from "lucide-react";
import { toast } from "sonner";
import { useEnvironment } from "@/hooks/useEnvironment";

interface PromotionDialogProps
{
  open: boolean;
  onOpenChange: (open: boolean) => void;
  objectType: string;
  logicalId: string;
  displayName: string;
  fromEnvironmentId: number;
  onPromoted?: () => void;
}

/**
 * Dependency-preview + confirm promotion dialog (Phase F steps 25 + 27a), extended to support
 * promoting into several targets at once (e.g. rolling an agent out to every client's Play
 * environment in one action). Shared across all 4 promotable object types — callers just supply
 * objectType/logicalId/displayName/fromEnvironmentId.
 *
 * Single-target selection preserves the original detailed dependency preview. Multi-target
 * selection skips the up-front preview (each target is validated independently server-side) and
 * instead shows a per-target success/error result list after promoting, since different targets
 * (e.g. different clients) can legitimately succeed or fail independently — one client missing an
 * LLM config shouldn't block the others.
 */
export function PromotionDialog({ open, onOpenChange, objectType, logicalId, displayName, fromEnvironmentId, onPromoted }: PromotionDialogProps)
{
  const { environments } = useEnvironment();
  const fromEnv = environments.find((e) => e.id === fromEnvironmentId);
  const targets = [...environments]
    .filter((e) => fromEnv && e.rank > fromEnv.rank)
    .sort((a, b) => a.rank - b.rank);
  const maxRank = environments.length > 0 ? Math.max(...environments.map((e) => e.rank)) : 0;

  const [selectedIds, setSelectedIds] = useState<number[]>([]);
  const [preview, setPreview] = useState<PromotionPreview | null>(null);
  const [loadingPreview, setLoadingPreview] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [promoting, setPromoting] = useState(false);
  const [bulkResults, setBulkResults] = useState<BulkPromoteResultItem[] | null>(null);

  useEffect(() =>
  {
    if (!open) { setSelectedIds([]); setPreview(null); setConfirmed(false); setBulkResults(null); }
  }, [open]);

  useEffect(() =>
  {
    if (open && targets.length > 0 && selectedIds.length === 0) setSelectedIds([targets[0].id]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, targets.length]);

  useEffect(() =>
  {
    if (!open || selectedIds.length !== 1) { setPreview(null); return; }
    setLoadingPreview(true);
    setConfirmed(false);
    api.previewPromotion(objectType, logicalId, fromEnvironmentId, selectedIds[0])
      .then(setPreview)
      .catch(() => setPreview({ canPromote: false, blockingError: "Failed to load promotion preview.", willPromote: [] }))
      .finally(() => setLoadingPreview(false));
  }, [open, selectedIds, objectType, logicalId, fromEnvironmentId]);

  const toggleTarget = (id: number) =>
  {
    setSelectedIds((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]));
    setConfirmed(false);
  };

  const isBulk = selectedIds.length > 1;
  const selectedEnvs = targets.filter((e) => selectedIds.includes(e.id));
  const needsConfirmCheckbox = selectedEnvs.some((e) => e.rank === maxRank);
  const canConfirm = selectedIds.length > 0
    && (!needsConfirmCheckbox || confirmed)
    && (isBulk || !!preview?.canPromote);

  const handlePromote = async () =>
  {
    if (selectedIds.length === 0) return;
    setPromoting(true);
    try
    {
      if (!isBulk)
      {
        const result = await api.promote({ objectType, logicalId, fromEnvironmentId, toEnvironmentId: selectedIds[0] });
        if (result.success)
        {
          toast.success(`Promoted "${displayName}" — ${result.promotedObjects.length} object(s) updated`);
          onOpenChange(false);
          onPromoted?.();
        }
        else
        {
          toast.error(result.error ?? "Promotion failed");
        }
      }
      else
      {
        const results = await api.bulkPromote({ objectType, logicalId, fromEnvironmentId, toEnvironmentIds: selectedIds });
        setBulkResults(results);
        const succeeded = results.filter((r) => r.result.success).length;
        if (succeeded === results.length) toast.success(`Promoted "${displayName}" to all ${results.length} targets`);
        else toast.error(`Promoted to ${succeeded} of ${results.length} targets — see details below`);
        onPromoted?.();
      }
    }
    catch (e)
    {
      toast.error(e instanceof Error ? e.message : "Promotion failed");
    }
    finally
    {
      setPromoting(false);
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-lg">
        <DialogHeader>
          <DialogTitle>Promote "{displayName}"</DialogTitle>
          <DialogDescription>
            Promote from {fromEnv?.displayName ?? "the current environment"} to one or more higher-ranked environments.
          </DialogDescription>
        </DialogHeader>

        {targets.length === 0 ? (
          <p className="text-sm text-muted-foreground py-4">
            No higher-ranked environment exists to promote into. Add one in Environments settings first.
          </p>
        ) : bulkResults ? (
          <div className="space-y-2">
            {bulkResults.map((item) =>
            {
              const env = targets.find((e) => e.id === item.toEnvironmentId);
              return (
                <div key={item.toEnvironmentId} className="flex items-start gap-2 text-sm rounded border px-3 py-2">
                  {item.result.success
                    ? <CheckCircle2 className="size-4 text-green-600 shrink-0 mt-0.5" />
                    : <XCircle className="size-4 text-destructive shrink-0 mt-0.5" />}
                  <div>
                    <div className="font-medium">{env?.displayName ?? `Environment #${item.toEnvironmentId}`}</div>
                    <div className="text-xs text-muted-foreground">
                      {item.result.success
                        ? `${item.result.promotedObjects.length} object(s) updated`
                        : item.result.error}
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        ) : (
          <div className="space-y-4">
            <div className="space-y-1.5">
              <div className="flex items-center justify-between">
                <Label>Target environment(s)</Label>
                {targets.length > 1 && (
                  <button
                    type="button"
                    className="text-xs text-muted-foreground hover:text-foreground underline"
                    onClick={() => setSelectedIds(selectedIds.length === targets.length ? [] : targets.map((e) => e.id))}
                  >
                    {selectedIds.length === targets.length ? "Clear all" : "Select all"}
                  </button>
                )}
              </div>
              <div className="flex flex-wrap gap-2">
                {targets.map((e) => (
                  <Badge
                    key={e.id}
                    variant={selectedIds.includes(e.id) ? "default" : "outline"}
                    className="cursor-pointer"
                    onClick={() => toggleTarget(e.id)}
                  >
                    {e.displayName}
                  </Badge>
                ))}
              </div>
              {isBulk && (
                <p className="text-xs text-muted-foreground">
                  Multiple targets selected — each is promoted and validated independently (one
                  target failing, e.g. a missing config, does not block the others).
                </p>
              )}
            </div>

            {!isBulk && (loadingPreview ? (
              <div className="flex items-center gap-2 text-sm text-muted-foreground py-2">
                <Loader2 className="size-4 animate-spin" /> Checking dependencies…
              </div>
            ) : preview && (
              <>
                {!preview.canPromote && preview.blockingError && (
                  <div className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive flex gap-2">
                    <AlertTriangle className="size-4 shrink-0 mt-0.5" />
                    <span>{preview.blockingError}</span>
                  </div>
                )}
                {preview.canPromote && preview.willPromote.length > 0 && (
                  <div className="space-y-1.5">
                    <Label className="text-xs text-muted-foreground">This will also promote:</Label>
                    <div className="space-y-1">
                      {preview.willPromote.map((dep) => (
                        <div key={`${dep.objectType}-${dep.logicalId}`} className="flex items-center gap-2 text-sm rounded border px-2 py-1">
                          <Badge variant="outline" className="text-xs">{dep.objectType}</Badge>
                          {dep.displayName}
                        </div>
                      ))}
                    </div>
                  </div>
                )}
                {preview.canPromote && preview.willPromote.length === 0 && (
                  <div className="flex items-center gap-2 text-sm text-muted-foreground">
                    <CheckCircle2 className="size-4 text-green-600" /> No additional dependencies to promote.
                  </div>
                )}
              </>
            ))}

            {needsConfirmCheckbox && (isBulk || preview?.canPromote) && (
              <div className="flex items-center gap-2 rounded-md border border-amber-600/40 bg-amber-500/10 px-3 py-2">
                <Switch checked={confirmed} onCheckedChange={setConfirmed} />
                <Label className="text-sm">
                  I understand this affects live traffic in {selectedEnvs.filter((e) => e.rank === maxRank).map((e) => e.displayName).join(", ")}.
                </Label>
              </div>
            )}
          </div>
        )}

        <DialogFooter>
          {bulkResults ? (
            <Button onClick={() => onOpenChange(false)}>Done</Button>
          ) : (
            <>
              <Button variant="outline" onClick={() => onOpenChange(false)}>Cancel</Button>
              <Button onClick={handlePromote} disabled={!canConfirm || promoting || targets.length === 0}>
                {promoting
                  ? <><Loader2 className="mr-2 size-4 animate-spin" /> Promoting…</>
                  : <><ArrowRight className="mr-2 size-4" /> {isBulk ? `Promote to ${selectedIds.length} targets` : "Promote"}</>}
              </Button>
            </>
          )}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
