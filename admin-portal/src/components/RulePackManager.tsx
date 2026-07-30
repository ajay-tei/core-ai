import { useState } from "react";
import { useNavigate } from "react-router";
import { api, type RulePack, type RulePackListParams, type PagedResult } from "@/api";
import { usePagedList } from "@/hooks/usePagedList";
import { ListToolbar, ListPagination } from "@/components/ui/list-toolbar";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";
import { Input } from "@/components/ui/input";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Copy, Download, Edit, Lock, Package, Plus, Trash2, Upload } from "lucide-react";

const RULE_TYPE_COLORS: Record<string, string> = {
  inject_prompt:   "bg-blue-500/15 text-blue-700 dark:text-blue-400",
  tool_require:    "bg-purple-500/15 text-purple-700 dark:text-purple-400",
  format_response: "bg-green-500/15 text-green-700 dark:text-green-400",
  format_enforce:  "bg-emerald-500/15 text-emerald-700 dark:text-emerald-400",
  regex_redact:    "bg-red-500/15 text-red-700 dark:text-red-400",
  append_text:     "bg-yellow-500/15 text-yellow-700 dark:text-yellow-400",
  block_pattern:   "bg-red-500/20 text-red-800 dark:text-red-400",
  require_keyword: "bg-orange-500/15 text-orange-700 dark:text-orange-400",
  tool_transform:  "bg-purple-500/20 text-purple-800 dark:text-purple-400",
  model_switch:    "bg-sky-500/15 text-sky-700 dark:text-sky-400",
};

const MAX_BADGES = 6;

type PackRow = RulePack & { _isStarter: boolean };

/** Fetches a page of merged tenant + starter packs, tagging each row's `_isStarter` from `tenantId`. */
async function fetchPacksPaged(params: RulePackListParams): Promise<PagedResult<PackRow>> {
  const result = await api.getRulePacksPaged(params);
  return { ...result, items: result.items.map((p) => ({ ...p, _isStarter: p.tenantId === 0 })) };
}

export function RulePackManager() {
  const navigate = useNavigate();
  const { result, loading, params, update, updateDebounced, setPage, reload } =
    usePagedList<PackRow, RulePackListParams>(fetchPacksPaged, { page: 1, pageSize: 25 });

  const [cloneDialog, setCloneDialog] = useState<{ packId: number; name: string } | null>(null);
  const [cloneName, setCloneName]     = useState("");
  const [importDialog, setImportDialog] = useState(false);
  const [importJson, setImportJson]     = useState("");

  async function handleDelete(id: number) {
    if (!confirm("Delete this rule pack and all its rules?")) return;
    await api.deleteRulePack(id);
    reload();
  }

  async function handleClone() {
    if (!cloneDialog || !cloneName.trim()) return;
    await api.cloneRulePack(cloneDialog.packId, cloneName.trim());
    setCloneDialog(null);
    setCloneName("");
    reload();
  }

  async function handleExport(packId: number) {
    const data = await api.exportRulePack(packId);
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: "application/json" });
    const url  = URL.createObjectURL(blob);
    const a    = document.createElement("a");
    a.href     = url;
    a.download = `rule-pack-${data.name.replace(/\s+/g, "-").toLowerCase()}.json`;
    a.click();
    URL.revokeObjectURL(url);
  }

  async function handleImport() {
    if (!importJson.trim()) return;
    try {
      const data = JSON.parse(importJson);
      await api.importRulePack(data);
      setImportDialog(false);
      setImportJson("");
      reload();
    } catch (e) {
      alert("Invalid JSON: " + (e instanceof Error ? e.message : "Parse error"));
    }
  }

  if (loading && !result) return <div className="text-muted-foreground">Loading rule packs…</div>;

  return (
    <div className="space-y-4">

      {/* ── Header ────────────────────────────────────────────────────────── */}
      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="text-2xl font-bold">Rule Packs</h1>
          <p className="text-sm text-muted-foreground mt-1">
            Named bundles of hook rules. Edit a pack to configure which agents it applies to and add rules.
            Rules apply automatically at runtime based on agent archetype and activation conditions.
          </p>
        </div>
        <div className="flex shrink-0 gap-2">
          <Button variant="outline" onClick={() => setImportDialog(true)}>
            <Upload className="mr-2 size-4" /> Import
          </Button>
          <Button onClick={() => navigate("/rules/packs/new")}>
            <Plus className="mr-2 size-4" /> New Pack
          </Button>
        </div>
      </div>

      {/* ── Filter bar ────────────────────────────────────────────────────── */}
      <ListToolbar
        searchValue={params.search}
        onSearchChange={(v) => updateDebounced({ search: v || undefined })}
        searchPlaceholder="Search by name…"
        pageSize={params.pageSize}
        onPageSizeChange={(pageSize) => update({ pageSize })}
        pageSizeOptions={[10, 25, 50]}
      >
        <Select value={params.status ?? "all"} onValueChange={(v) => update({ status: v as RulePackListParams["status"] })}>
          <SelectTrigger className="h-8 w-32 text-sm">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All statuses</SelectItem>
            <SelectItem value="enabled">Enabled</SelectItem>
            <SelectItem value="disabled">Disabled</SelectItem>
          </SelectContent>
        </Select>
        <Select value={params.type ?? "all"} onValueChange={(v) => update({ type: v as RulePackListParams["type"] })}>
          <SelectTrigger className="h-8 w-32 text-sm">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="all">All types</SelectItem>
            <SelectItem value="mandatory">Mandatory</SelectItem>
            <SelectItem value="group">Group</SelectItem>
            <SelectItem value="starter">Starters</SelectItem>
          </SelectContent>
        </Select>
      </ListToolbar>

      {/* ── Pack list ─────────────────────────────────────────────────────── */}
      <div className="space-y-3">
        {(result?.items.length ?? 0) === 0 && (
          <Card>
            <CardContent className="py-8 text-center text-muted-foreground">
              {(result?.totalCount ?? 0) === 0
                ? "No rule packs configured. Create one or use the Starters filter to browse templates."
                : "No packs match the current filters."}
            </CardContent>
          </Card>
        )}

        {(result?.items ?? []).map((pack) => (
          <PackCard
            key={`${pack._isStarter ? "s" : "p"}-${pack.id}`}
            pack={pack}
            onEdit={() => navigate(`/rules/packs/${pack.id}`)}
            onClone={() => { setCloneDialog({ packId: pack.id, name: pack.name }); setCloneName(`${pack.name} (copy)`); }}
            onExport={() => handleExport(pack.id)}
            onDelete={() => handleDelete(pack.id)}
          />
        ))}
      </div>

      {/* ── Pagination ────────────────────────────────────────────────────── */}
      {result && (
        <ListPagination
          page={result.page}
          totalPages={result.totalPages}
          totalCount={result.totalCount}
          onPageChange={setPage}
          itemLabel="total"
        />
      )}

      {/* ── Clone Dialog ──────────────────────────────────────────────────── */}
      <Dialog open={!!cloneDialog} onOpenChange={(open) => !open && setCloneDialog(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Clone Rule Pack</DialogTitle>
            <DialogDescription>
              Create a copy of "{cloneDialog?.name}" with a new name.
            </DialogDescription>
          </DialogHeader>
          <Input
            placeholder="New pack name"
            value={cloneName}
            onChange={(e) => setCloneName(e.target.value)}
          />
          <DialogFooter>
            <Button variant="outline" onClick={() => setCloneDialog(null)}>Cancel</Button>
            <Button onClick={handleClone} disabled={!cloneName.trim()}>Clone</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* ── Import Dialog ─────────────────────────────────────────────────── */}
      <Dialog open={importDialog} onOpenChange={setImportDialog}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Import Rule Pack</DialogTitle>
            <DialogDescription>Paste the exported JSON to import a rule pack.</DialogDescription>
          </DialogHeader>
          <textarea
            className="h-48 w-full rounded border border-input bg-background p-2 font-mono text-sm text-foreground"
            placeholder='{"name": "...", "rules": [...]}'
            value={importJson}
            onChange={(e) => setImportJson(e.target.value)}
          />
          <DialogFooter>
            <Button variant="outline" onClick={() => setImportDialog(false)}>Cancel</Button>
            <Button onClick={handleImport}>Import</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

// ── Pack card ──────────────────────────────────────────────────────────────────

interface PackCardProps {
  pack: PackRow;
  onEdit: () => void;
  onClone: () => void;
  onExport: () => void;
  onDelete: () => void;
}

function PackCard({ pack, onEdit, onClone, onExport, onDelete }: PackCardProps) {
  const rules   = pack.rules ?? [];
  const shown   = rules.slice(0, MAX_BADGES);
  const extra   = rules.length - MAX_BADGES;

  return (
    <Card className={`group ${pack._isStarter ? "border-dashed" : ""}`}>
      <CardHeader className="pb-3">
        <div className="flex items-start justify-between">
          <div className="flex flex-wrap items-center gap-2">
            {pack._isStarter ? (
              <Package className="size-4 text-blue-500 shrink-0" />
            ) : pack.isMandatory ? (
              <Lock className="size-4 text-amber-600 shrink-0" />
            ) : (
              <Package className="size-4 text-muted-foreground shrink-0" />
            )}
            <CardTitle
              className="cursor-pointer hover:underline"
              onClick={onEdit}
            >
              {pack.name}
            </CardTitle>
            {pack._isStarter ? (
              <Badge variant="outline" className="border-blue-400 text-blue-600 dark:border-blue-600 dark:text-blue-400">
                Starter
              </Badge>
            ) : (
              <Badge variant={pack.isEnabled ? "default" : "secondary"}>
                {pack.isEnabled ? "Enabled" : "Disabled"}
              </Badge>
            )}
            {pack.isMandatory && (
              <Badge variant="outline" className="border-amber-400 text-amber-700 dark:border-amber-600 dark:text-amber-400">
                Mandatory
              </Badge>
            )}
            {pack.groupId && <Badge variant="outline">Group</Badge>}
          </div>

          <div className="flex gap-1 opacity-0 transition-opacity group-hover:opacity-100 shrink-0">
            {!pack._isStarter && (
              <Button variant="ghost" size="icon" title="Edit" onClick={onEdit}>
                <Edit className="size-4" />
              </Button>
            )}
            <Button variant="ghost" size="icon" title="Clone" onClick={onClone}>
              <Copy className="size-4" />
            </Button>
            {!pack._isStarter && (
              <Button variant="ghost" size="icon" title="Export" onClick={onExport}>
                <Download className="size-4" />
              </Button>
            )}
            {!pack._isStarter && !pack.isMandatory && (
              <Button
                variant="ghost"
                size="icon"
                title="Delete"
                className="text-destructive"
                onClick={onDelete}
              >
                <Trash2 className="size-4" />
              </Button>
            )}
          </div>
        </div>

        <CardDescription>
          {pack.description || "No description"}
          {!pack._isStarter && ` · v${pack.version} · Priority ${pack.priority}`}
          {pack.activationCondition && ` · Activates on: ${pack.activationCondition}`}
          {pack._isStarter && rules.length > 0 && ` · ${rules.length} rules`}
        </CardDescription>
      </CardHeader>

      {rules.length > 0 && (
        <CardContent>
          <div className="flex flex-wrap gap-1.5">
            {shown.map((r) => (
              <Badge
                key={r.id}
                variant="secondary"
                className={RULE_TYPE_COLORS[r.ruleType] ?? ""}
              >
                #{r.orderInPack} {r.ruleType}
              </Badge>
            ))}
            {extra > 0 && (
              <Badge variant="outline" className="text-muted-foreground">
                +{extra} more
              </Badge>
            )}
          </div>
        </CardContent>
      )}

      {rules.length === 0 && !pack._isStarter && (
        <CardContent>
          <span className="text-sm text-muted-foreground">No rules</span>
        </CardContent>
      )}
    </Card>
  );
}
