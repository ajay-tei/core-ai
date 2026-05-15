import { useRef, useState } from "react";
import { Upload, AlertTriangle, CheckCircle2, FileJson } from "lucide-react";
import { api } from "@/api";
import type { AgentExportBundle, AgentImportResult } from "@/api";
import { readJsonFile } from "@/lib/download";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogDescription } from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";

interface AgentImportDialogProps
{
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onSuccess: (result: AgentImportResult) => void;
}

export function AgentImportDialog({ open, onOpenChange, onSuccess }: AgentImportDialogProps)
{
  const fileInputRef = useRef<HTMLInputElement>(null);
  const [bundle, setBundle] = useState<AgentExportBundle | null>(null);
  const [parseError, setParseError] = useState<string | null>(null);
  const [overwrite, setOverwrite] = useState(false);
  const [importRules, setImportRules] = useState(true);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  function reset()
  {
    setBundle(null);
    setParseError(null);
    setOverwrite(false);
    setImportRules(true);
    setLoading(false);
    setError(null);
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  function handleOpenChange(next: boolean)
  {
    if (!next) reset();
    onOpenChange(next);
  }

  async function handleFileChange(e: React.ChangeEvent<HTMLInputElement>)
  {
    const file = e.target.files?.[0];
    if (!file) return;
    setParseError(null);
    setBundle(null);
    try
    {
      const parsed = await readJsonFile<AgentExportBundle>(file);
      if (!parsed?.agent?.name)
        throw new Error("File does not appear to be a valid Diva agent export.");
      setBundle(parsed);
    }
    catch (err: unknown)
    {
      setParseError(err instanceof Error ? err.message : "Invalid file.");
    }
  }

  async function handleImport()
  {
    if (!bundle) return;
    setLoading(true);
    setError(null);
    try
    {
      const result = await api.importAgent(bundle, { overwriteExisting: overwrite, importRules });
      onSuccess(result);
      handleOpenChange(false);
    }
    catch (err: unknown)
    {
      setError(err instanceof Error ? err.message : "Import failed.");
    }
    finally
    {
      setLoading(false);
    }
  }

  return (
    <Dialog open={open} onOpenChange={handleOpenChange}>
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Import Agent</DialogTitle>
          <DialogDescription>
            Import an agent configuration bundle exported from Diva.
          </DialogDescription>
        </DialogHeader>

        <div className="space-y-4 py-2">
          {/* File picker */}
          <div
            className="border-2 border-dashed rounded-lg p-6 text-center cursor-pointer hover:border-primary/60 transition-colors"
            onClick={() => fileInputRef.current?.click()}
          >
            <FileJson className="mx-auto size-8 text-muted-foreground mb-2" />
            <p className="text-sm text-muted-foreground">
              {bundle ? bundle.agent.name : "Click to select a .json export file"}
            </p>
            <input
              ref={fileInputRef}
              type="file"
              accept=".json,application/json"
              className="hidden"
              onChange={handleFileChange}
            />
          </div>

          {parseError && (
            <div className="flex items-start gap-2 text-destructive text-sm">
              <AlertTriangle className="size-4 mt-0.5 shrink-0" />
              <span>{parseError}</span>
            </div>
          )}

          {/* Bundle preview */}
          {bundle && (
            <div className="rounded-md border bg-muted/40 p-3 space-y-1 text-sm">
              <div className="flex items-center gap-2">
                <CheckCircle2 className="size-4 text-green-500 shrink-0" />
                <span className="font-medium">{bundle.agent.displayName || bundle.agent.name}</span>
              </div>
              {bundle.agent.description && (
                <p className="text-muted-foreground pl-6 line-clamp-2">{bundle.agent.description}</p>
              )}
              <p className="text-muted-foreground pl-6">
                {bundle.rules.length} rule{bundle.rules.length !== 1 ? "s" : ""} · exported{" "}
                {new Date(bundle.exportedAt).toLocaleDateString()}
              </p>
            </div>
          )}

          {/* Options */}
          {bundle && (
            <div className="space-y-3">
              <div className="flex items-center justify-between">
                <Label htmlFor="import-overwrite" className="text-sm">
                  Overwrite existing agent with the same name
                </Label>
                <Switch
                  id="import-overwrite"
                  checked={overwrite}
                  onCheckedChange={setOverwrite}
                />
              </div>
              <div className="flex items-center justify-between">
                <Label htmlFor="import-rules" className="text-sm">
                  Import linked business rules
                </Label>
                <Switch
                  id="import-rules"
                  checked={importRules}
                  onCheckedChange={setImportRules}
                />
              </div>
            </div>
          )}

          {error && (
            <div className="flex items-start gap-2 text-destructive text-sm">
              <AlertTriangle className="size-4 mt-0.5 shrink-0" />
              <span>{error}</span>
            </div>
          )}
        </div>

        <DialogFooter>
          <Button variant="outline" onClick={() => handleOpenChange(false)} disabled={loading}>
            Cancel
          </Button>
          <Button
            onClick={handleImport}
            disabled={!bundle || loading}
          >
            <Upload className="size-4 mr-2" />
            {loading ? "Importing…" : "Import"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
