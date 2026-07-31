import { useEffect, useState } from "react";
import { api, type PromotionPreview } from "@/api";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogDescription } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { AlertTriangle, ArrowRight, CheckCircle2, Loader2 } from "lucide-react";
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
 * Dependency-preview + confirm promotion dialog (Phase F steps 25 + 27a). Shared across all 4
 * promotable object types — callers just supply objectType/logicalId/displayName/fromEnvironmentId.
 */
export function PromotionDialog({ open, onOpenChange, objectType, logicalId, displayName, fromEnvironmentId, onPromoted }: PromotionDialogProps)
{
  const { environments } = useEnvironment();
  const fromEnv = environments.find((e) => e.id === fromEnvironmentId);
  const targets = [...environments]
    .filter((e) => fromEnv && e.rank > fromEnv.rank)
    .sort((a, b) => a.rank - b.rank);
  const maxRank = environments.length > 0 ? Math.max(...environments.map((e) => e.rank)) : 0;

  const [toEnvironmentId, setToEnvironmentId] = useState<number | null>(null);
  const [preview, setPreview] = useState<PromotionPreview | null>(null);
  const [loadingPreview, setLoadingPreview] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [promoting, setPromoting] = useState(false);

  useEffect(() =>
  {
    if (!open) { setToEnvironmentId(null); setPreview(null); setConfirmed(false); }
  }, [open]);

  useEffect(() =>
  {
    if (open && targets.length > 0 && toEnvironmentId === null) setToEnvironmentId(targets[0].id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open, targets.length]);

  useEffect(() =>
  {
    if (!open || toEnvironmentId === null) return;
    setLoadingPreview(true);
    setConfirmed(false);
    api.previewPromotion(objectType, logicalId, fromEnvironmentId, toEnvironmentId)
      .then(setPreview)
      .catch(() => setPreview({ canPromote: false, blockingError: "Failed to load promotion preview.", willPromote: [] }))
      .finally(() => setLoadingPreview(false));
  }, [open, toEnvironmentId, objectType, logicalId, fromEnvironmentId]);

  const targetEnv = environments.find((e) => e.id === toEnvironmentId);
  const needsConfirmCheckbox = targetEnv?.rank === maxRank;
  const canConfirm = !!preview?.canPromote && (!needsConfirmCheckbox || confirmed);

  const handlePromote = async () =>
  {
    if (toEnvironmentId === null) return;
    setPromoting(true);
    try
    {
      const result = await api.promote({ objectType, logicalId, fromEnvironmentId, toEnvironmentId });
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
            Promote from {fromEnv?.displayName ?? "the current environment"} to a higher-ranked environment.
          </DialogDescription>
        </DialogHeader>

        {targets.length === 0 ? (
          <p className="text-sm text-muted-foreground py-4">
            No higher-ranked environment exists to promote into. Add one in Environments settings first.
          </p>
        ) : (
          <div className="space-y-4">
            <div className="space-y-1.5">
              <Label>Target environment</Label>
              <Select value={toEnvironmentId ? String(toEnvironmentId) : undefined} onValueChange={(v) => setToEnvironmentId(Number(v))}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  {targets.map((e) => <SelectItem key={e.id} value={String(e.id)}>{e.displayName}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>

            {loadingPreview ? (
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
            )}

            {needsConfirmCheckbox && preview?.canPromote && (
              <div className="flex items-center gap-2 rounded-md border border-amber-600/40 bg-amber-500/10 px-3 py-2">
                <Switch checked={confirmed} onCheckedChange={setConfirmed} />
                <Label className="text-sm">I understand this affects live traffic in {targetEnv?.displayName}.</Label>
              </div>
            )}
          </div>
        )}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button onClick={handlePromote} disabled={!canConfirm || promoting || targets.length === 0}>
            {promoting ? <><Loader2 className="mr-2 size-4 animate-spin" /> Promoting…</> : <><ArrowRight className="mr-2 size-4" /> Promote</>}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
