import { useState, useEffect, useCallback } from "react";
import { useNavigate } from "react-router";
import { api, type AgentSummary, type ArchetypeSummary, type BusinessRule, type RulePack, type GroupRuleTemplateItem } from "@/api";
import { toast } from "sonner";
import {
  Table, TableBody, TableCell, TableHead, TableHeader, TableRow,
} from "@/components/ui/table";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogFooter, DialogClose,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  AlertDialog, AlertDialogAction, AlertDialogCancel, AlertDialogContent,
  AlertDialogDescription, AlertDialogFooter, AlertDialogHeader, AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { Skeleton } from "@/components/ui/skeleton";
import { EmptyState } from "@/components/ui/empty-state";
import { MoreHorizontal, Plus, BookOpen, Pencil, Trash2, RefreshCw, Zap } from "lucide-react";

const CATEGORIES = ["general", "tone", "response_format", "safety", "terminology", "seasonal"];

const HOOK_POINTS = [
  "OnInit", "OnBeforeIteration", "OnToolFilter",
  "OnAfterToolCall", "OnBeforeResponse", "OnAfterResponse", "OnError",
] as const;

const HOOK_RULE_TYPES_BY_POINT: Record<string, string[]> = {
  OnInit:              ["inject_prompt", "block_pattern", "require_keyword"],
  OnBeforeIteration:   ["inject_prompt"],
  OnToolFilter:        ["tool_require", "tool_block"],
  OnAfterToolCall:     ["inject_prompt", "block_pattern", "regex_redact", "regex_replace"],
  OnBeforeResponse:    ["block_pattern", "regex_redact", "regex_replace", "require_keyword", "format_response", "format_enforce"],
  OnAfterResponse:     ["block_pattern", "regex_redact", "regex_replace", "require_keyword"],
  OnError:             ["inject_prompt"],
};

const NON_DEFAULT_HOOK = (hp: string, ht: string) =>
  hp !== "OnInit" || ht !== "inject_prompt";

const CATEGORY_COLORS: Record<string, string> = {
  general:         "bg-blue-500/10 text-blue-400 border-blue-500/20",
  tone:            "bg-purple-500/10 text-purple-400 border-purple-500/20",
  response_format: "bg-cyan-500/10 text-cyan-400 border-cyan-500/20",
  safety:          "bg-red-500/10 text-red-400 border-red-500/20",
  terminology:     "bg-amber-500/10 text-amber-400 border-amber-500/20",
  seasonal:        "bg-green-500/10 text-green-400 border-green-500/20",
};

function CategoryBadge({ category }: { category: string }) {
  const cls = CATEGORY_COLORS[category] ?? "bg-muted text-muted-foreground";
  return (
    <Badge variant="outline" className={`text-xs font-medium ${cls}`}>
      {category}
    </Badge>
  );
}

export function BusinessRules() {
  const navigate = useNavigate();
  const [rules, setRules]             = useState<BusinessRule[]>([]);
  const [agents, setAgents]           = useState<AgentSummary[]>([]);
  const [archetypes, setArchetypes]   = useState<ArchetypeSummary[]>([]);
  const [loading, setLoading]         = useState(true);
  const [agentFilter, setFilter]      = useState("*");
  const [agentIdFilter, setAgentIdFilter] = useState<string | undefined>(undefined);
  const [deleteId, setDeleteId]       = useState<number | null>(null);

  useEffect(() => {
    api.listAgents().then(setAgents).catch(() => {});
    api.listArchetypes().then(setArchetypes).catch(() => {});
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    try   { setRules(await api.getBusinessRules(1, agentFilter, agentIdFilter)); }
    catch (e: unknown) { toast.error(String(e)); }
    finally { setLoading(false); }
  }, [agentFilter, agentIdFilter]);

  useEffect(() => { load(); }, [load]);

  const handleDelete = async () => {
    if (!deleteId) return;
    try {
      await api.deleteBusinessRule(deleteId, 1);
      setRules(r => r.filter(x => x.id !== deleteId));
      toast.success("Rule deleted.");
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setDeleteId(null); }
  };

  const handleToggleActive = async (rule: BusinessRule) => {
    try {
      await api.updateBusinessRule(rule.id, { ...rule, isActive: !rule.isActive }, 1);
      setRules(r => r.map(x => x.id === rule.id ? { ...x, isActive: !x.isActive } : x));
    } catch (e: unknown) { toast.error(String(e)); }
  };

  const agentName = (agentId: string) =>
    agents.find(a => a.id === agentId)?.displayName ?? agentId;

  const archetypeLabel = (id: string) =>
    id === "*" ? <span className="text-foreground/40">all archetypes</span>
               : <span>{archetypes.find(a => a.id === id)?.displayName ?? id}</span>;

  return (
    <div className="space-y-4">
      <div>
        <h2 className="text-xl font-semibold">Business Rules</h2>
        <p className="text-sm text-muted-foreground">
          Rules injected into agent system prompts at runtime.
        </p>
      </div>

      <Tabs defaultValue="my-rules">
        <TabsList>
          <TabsTrigger value="my-rules">My Rules</TabsTrigger>
          <TabsTrigger value="group-templates">Group Templates</TabsTrigger>
        </TabsList>

        <TabsContent value="my-rules">
        <div className="space-y-4 pt-2">
          <div className="flex items-center gap-2 justify-end">
            {/* Filter by archetype */}
            <Select value={agentFilter} onValueChange={v => { setFilter(v); setAgentIdFilter(undefined); }}>
              <SelectTrigger className="w-44 h-8 text-xs">
                <SelectValue placeholder="All archetypes" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="*">All archetypes</SelectItem>
                {archetypes.map(a => (
                  <SelectItem key={a.id} value={a.id}>{a.displayName}</SelectItem>
                ))}
              </SelectContent>
            </Select>
            {/* Filter by specific agent */}
            <Select
              value={agentIdFilter ?? "__all__"}
              onValueChange={v => setAgentIdFilter(v === "__all__" ? undefined : v)}
            >
              <SelectTrigger className="w-40 h-8 text-xs">
                <SelectValue placeholder="Any agent" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="__all__">Any agent</SelectItem>
                {agents.map(a => (
                  <SelectItem key={a.id} value={a.id}>{a.displayName || a.name}</SelectItem>
                ))}
              </SelectContent>
            </Select>
            <Button size="sm" variant="outline" onClick={load} className="h-8">
              <RefreshCw className="h-3.5 w-3.5" />
            </Button>
            <Button size="sm" onClick={() => navigate("/rules/business/new")} className="h-8">
              <Plus className="h-3.5 w-3.5 mr-1" /> Add Rule
            </Button>
          </div>

          {loading ? (
        <div className="rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Key / Prompt</TableHead>
                <TableHead>Category</TableHead>
                <TableHead>Scope</TableHead>
                <TableHead>Priority</TableHead>
                <TableHead>Active</TableHead>
                <TableHead />
              </TableRow>
            </TableHeader>
            <TableBody>
              {Array.from({ length: 3 }).map((_, i) => (
                <TableRow key={i}>
                  <TableCell><Skeleton className="h-4 w-48" /></TableCell>
                  <TableCell><Skeleton className="h-5 w-20" /></TableCell>
                  <TableCell><Skeleton className="h-4 w-28" /></TableCell>
                  <TableCell><Skeleton className="h-4 w-8" /></TableCell>
                  <TableCell><Skeleton className="h-5 w-9" /></TableCell>
                  <TableCell><Skeleton className="h-5 w-5" /></TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      ) : rules.length === 0 ? (
        <EmptyState
          icon={BookOpen}
          title="No business rules"
          description="Add rules to customise agent behaviour for this tenant."
          action={{ label: "Add Rule", onClick: () => navigate("/rules/business/new") }}
        />
      ) : (
        <div className="rounded-md border">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Key / Prompt</TableHead>
                <TableHead>Category</TableHead>
                <TableHead>Scope</TableHead>
                <TableHead>Hook</TableHead>
                <TableHead className="w-20 text-center">Priority</TableHead>
                <TableHead className="w-20 text-center">Active</TableHead>
                <TableHead className="w-10" />
              </TableRow>
            </TableHeader>
            <TableBody>
              {rules.map(rule => (
                <TableRow key={rule.id}>
                  <TableCell>
                    <div className="font-mono text-xs text-muted-foreground">{rule.ruleKey}</div>
                    <div className="text-sm mt-0.5 line-clamp-2 text-foreground/80">
                      {rule.promptInjection}
                    </div>
                  </TableCell>
                  <TableCell><CategoryBadge category={rule.ruleCategory} /></TableCell>
                  <TableCell>
                    <div className="text-xs text-muted-foreground leading-snug">
                      {archetypeLabel(rule.agentType)}
                      {rule.agentId && (
                        <div className="text-amber-400 mt-0.5">
                          → {agentName(rule.agentId)}
                        </div>
                      )}
                    </div>
                  </TableCell>
                  <TableCell>
                    {NON_DEFAULT_HOOK(rule.hookPoint, rule.hookRuleType) && (
                      <div className="flex items-center gap-1">
                        <Zap className="h-3 w-3 text-amber-400" />
                        <span className="text-xs font-mono text-amber-400">
                          {rule.hookPoint} / {rule.hookRuleType}
                        </span>
                      </div>
                    )}
                  </TableCell>
                  <TableCell className="text-center text-sm">{rule.priority}</TableCell>
                  <TableCell className="text-center">
                    <Switch
                      checked={rule.isActive}
                      onCheckedChange={() => handleToggleActive(rule)}
                      className="data-[state=checked]:bg-emerald-500"
                    />
                  </TableCell>
                  <TableCell>
                    <DropdownMenu>
                      <DropdownMenuTrigger asChild>
                        <Button variant="ghost" size="icon" className="h-7 w-7">
                          <MoreHorizontal className="h-4 w-4" />
                        </Button>
                      </DropdownMenuTrigger>
                      <DropdownMenuContent align="end">
                        <DropdownMenuItem onClick={() => navigate(`/rules/business/${rule.id}/edit`)}>
                          <Pencil className="h-3.5 w-3.5 mr-2" /> Edit
                        </DropdownMenuItem>
                        <DropdownMenuItem
                          onClick={() => setDeleteId(rule.id)}
                          className="text-destructive focus:text-destructive"
                        >
                          <Trash2 className="h-3.5 w-3.5 mr-2" /> Delete
                        </DropdownMenuItem>
                      </DropdownMenuContent>
                    </DropdownMenu>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}

      <AlertDialog open={deleteId !== null} onOpenChange={(open: boolean) => !open && setDeleteId(null)}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Delete rule?</AlertDialogTitle>
            <AlertDialogDescription>
              This rule will be removed and no longer injected into agent prompts.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Cancel</AlertDialogCancel>
            <AlertDialogAction
              onClick={handleDelete}
              className="bg-destructive text-destructive-foreground hover:bg-destructive/90"
            >
              Delete
            </AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
        </div>
        </TabsContent>

        <TabsContent value="group-templates">
          <GroupRuleTemplates />
        </TabsContent>
      </Tabs>
    </div>
  );
}

// ── Rule form dialog ───────────────────────────────────────────────────────────

interface RuleDialogProps {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  initial: BusinessRule | null;
  agents?: AgentSummary[];
  archetypes?: ArchetypeSummary[];
  onSaved: () => void;
  /** When set, the rule will be pre-assigned to this pack on save. Pack selector is hidden. */
  packId?: number;
}

export function RuleDialog({ open, onOpenChange, initial, agents = [], archetypes: archetypesProp, onSaved, packId }: RuleDialogProps) {
  const [agentType,       setAgentType]    = useState(initial?.agentType ?? "*");
  const [ruleCategory,    setCategory]     = useState(initial?.ruleCategory ?? "general");
  const [ruleKey,         setKey]          = useState(initial?.ruleKey ?? "");
  const [promptInjection, setInjection]    = useState(initial?.promptInjection ?? "");
  const [priority,        setPriority]     = useState(initial?.priority ?? 100);
  const [isActive,        setActive]       = useState(initial?.isActive ?? true);
  const [scopeToAgent,    setScopeToAgent] = useState(!!initial?.agentId);
  const [agentId,         setAgentId]      = useState<string | undefined>(initial?.agentId);
  const [saving,          setSaving]       = useState(false);
  // Hook fields
  const [hookPoint,       setHookPoint]    = useState(initial?.hookPoint ?? "OnInit");
  const [hookRuleType,    setHookRuleType] = useState(initial?.hookRuleType ?? "inject_prompt");
  const [pattern,         setPattern]      = useState(initial?.pattern ?? "");
  const [replacement,     setReplacement]  = useState(initial?.replacement ?? "");
  const [toolName,        setToolName]     = useState(initial?.toolName ?? "");
  const [orderInPack,     setOrderInPack]  = useState(initial?.orderInPack ?? 0);
  const [stopOnMatch,     setStopOnMatch]  = useState(initial?.stopOnMatch ?? false);
  const [maxEvaluationMs, setMaxEvalMs]    = useState(initial?.maxEvaluationMs ?? 100);
  const [showHookFields,  setShowHookFields] = useState(
    NON_DEFAULT_HOOK(initial?.hookPoint ?? "OnInit", initial?.hookRuleType ?? "inject_prompt"));
  // Pack association
  const [rulePackId, setRulePackId] = useState<number | undefined>(packId ?? initial?.rulePackId);
  const [packs, setPacks] = useState<RulePack[]>([]);
  const [packsLoading, setPacksLoading] = useState(false);
  // Archetypes — use prop if provided, otherwise fetch
  const [archetypesFetched, setArchetypesFetched] = useState<ArchetypeSummary[]>([]);
  const archetypes = archetypesProp ?? archetypesFetched;

  const validHookRuleTypes = HOOK_RULE_TYPES_BY_POINT[hookPoint] ?? ["inject_prompt"];

  useEffect(() => {
    if (open) {
      setAgentType(initial?.agentType ?? "*");
      setCategory(initial?.ruleCategory ?? "general");
      setKey(initial?.ruleKey ?? "");
      setInjection(initial?.promptInjection ?? "");
      setPriority(initial?.priority ?? 100);
      setActive(initial?.isActive ?? true);
      setScopeToAgent(!!initial?.agentId);
      setAgentId(initial?.agentId);
      setHookPoint(initial?.hookPoint ?? "OnInit");
      setHookRuleType(initial?.hookRuleType ?? "inject_prompt");
      setPattern(initial?.pattern ?? "");
      setReplacement(initial?.replacement ?? "");
      setToolName(initial?.toolName ?? "");
      setOrderInPack(initial?.orderInPack ?? 0);
      setStopOnMatch(initial?.stopOnMatch ?? false);
      setMaxEvalMs(initial?.maxEvaluationMs ?? 100);
      setShowHookFields(NON_DEFAULT_HOOK(initial?.hookPoint ?? "OnInit", initial?.hookRuleType ?? "inject_prompt"));
      setRulePackId(packId ?? initial?.rulePackId);
    }
  }, [open, initial, packId]);

  // Load available packs (only when pack selector is visible, i.e. packId not pre-set)
  useEffect(() => {
    if (!open || packId !== undefined) return;
    setPacksLoading(true);
    api.getRulePacks(1)
      .then(ps => setPacks(ps))
      .catch(() => {})
      .finally(() => setPacksLoading(false));
  }, [open, packId]);

  // Fetch archetypes only when not supplied by parent
  useEffect(() => {
    if (!open || archetypesProp !== undefined) return;
    api.listArchetypes().then(setArchetypesFetched).catch(() => {});
  }, [open, archetypesProp]);

  const save = async () => {
    if (!ruleKey.trim() || !promptInjection.trim()) {
      toast.error("Rule key and prompt injection text are required.");
      return;
    }
    if (scopeToAgent && !agentId) {
      toast.error("Select a specific agent or uncheck 'Scope to specific agent'.");
      return;
    }
    setSaving(true);
    try {
      const effectiveAgentId = scopeToAgent ? agentId : undefined;
      const hookFields = {
        hookPoint:       showHookFields ? hookPoint    : "OnInit",
        hookRuleType:    showHookFields ? hookRuleType : "inject_prompt",
        pattern:         showHookFields ? (pattern || undefined)      : undefined,
        replacement:     showHookFields ? (replacement || undefined)  : undefined,
        toolName:        showHookFields ? (toolName || undefined)     : undefined,
        orderInPack:     showHookFields ? orderInPack     : 0,
        stopOnMatch:     showHookFields ? stopOnMatch     : false,
        maxEvaluationMs: showHookFields ? maxEvaluationMs : 100,
      };
      if (initial) {
        await api.updateBusinessRule(initial.id, {
          ruleCategory, ruleKey, promptInjection, isActive, priority,
          agentId: effectiveAgentId, rulePackId, ...hookFields,
        }, 1);
      } else {
        await api.createBusinessRule({
          guid: crypto.randomUUID(),
          agentType, ruleCategory, ruleKey, promptInjection, priority,
          agentId: effectiveAgentId, rulePackId, ...hookFields,
        }, 1);
      }
      toast.success(initial ? "Rule updated." : "Rule created.");
      onSaved();
    } catch (e: unknown) { toast.error(String(e)); }
    finally { setSaving(false); }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-xl">
        <DialogHeader>
          <DialogTitle>{initial ? "Edit Rule" : "New Business Rule"}</DialogTitle>
        </DialogHeader>

        <div className="grid gap-4 py-2">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label>Archetype</Label>
              <Select value={agentType} onValueChange={setAgentType} disabled={!!initial}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="*">* (all archetypes)</SelectItem>
                  {archetypes.map(a => (
                    <SelectItem key={a.id} value={a.id}>{a.displayName}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label>Category</Label>
              <Select value={ruleCategory} onValueChange={setCategory}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  {CATEGORIES.map(c => <SelectItem key={c} value={c}>{c}</SelectItem>)}
                </SelectContent>
              </Select>
            </div>
          </div>

          {/* Scope to specific agent */}
          <div className="space-y-2">
            <div className="flex items-center gap-2">
              <Switch
                id="scope-agent"
                checked={scopeToAgent}
                onCheckedChange={v => { setScopeToAgent(v); if (!v) setAgentId(undefined); }}
              />
              <Label htmlFor="scope-agent" className="cursor-pointer">
                Scope to a specific agent
              </Label>
            </div>
            {scopeToAgent && (
              <Select value={agentId ?? ""} onValueChange={setAgentId}>
                <SelectTrigger className="text-sm">
                  <SelectValue placeholder="Select agent…" />
                </SelectTrigger>
                <SelectContent>
                  {agents.map(a => (
                    <SelectItem key={a.id} value={a.id}>{a.displayName || a.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label>Rule Key *</Label>
              <Input
                value={ruleKey}
                onChange={e => setKey(e.target.value)}
                placeholder="e.g. weather.always_celsius"
                className="font-mono text-sm"
              />
            </div>
            <div className="space-y-1.5">
              <Label>
                Priority{" "}
                <span className="text-muted-foreground text-xs">(lower = first)</span>
              </Label>
              <Input
                type="number"
                value={priority}
                onChange={e => setPriority(Number(e.target.value))}
              />
            </div>
          </div>

          <div className="space-y-1.5">
            <Label>Prompt Injection Text *</Label>
            <Textarea
              value={promptInjection}
              onChange={e => setInjection(e.target.value)}
              rows={4}
              placeholder="Text injected into the agent's system prompt…"
              className="resize-y"
            />
          </div>

          {/* Hook fields toggle */}
          <div className="space-y-2 border-t pt-3">
            <div className="flex items-center gap-2">
              <Switch
                id="hook-advanced"
                checked={showHookFields}
                onCheckedChange={v => { setShowHookFields(v); if (!v) { setHookPoint("OnInit"); setHookRuleType("inject_prompt"); } }}
              />
              <Label htmlFor="hook-advanced" className="cursor-pointer flex items-center gap-1">
                <Zap className="h-3.5 w-3.5 text-amber-400" />
                Advanced hook configuration
              </Label>
            </div>

            {showHookFields && (
              <div className="grid gap-3 pt-1">
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                  <div className="space-y-1.5">
                    <Label>Hook Point</Label>
                    <Select value={hookPoint} onValueChange={v => {
                      setHookPoint(v);
                      const types = HOOK_RULE_TYPES_BY_POINT[v] ?? ["inject_prompt"];
                      if (!types.includes(hookRuleType)) setHookRuleType(types[0]);
                    }}>
                      <SelectTrigger><SelectValue /></SelectTrigger>
                      <SelectContent>
                        {HOOK_POINTS.map(h => <SelectItem key={h} value={h}>{h}</SelectItem>)}
                      </SelectContent>
                    </Select>
                  </div>
                  <div className="space-y-1.5">
                    <Label>Rule Type</Label>
                    <Select value={hookRuleType} onValueChange={setHookRuleType}>
                      <SelectTrigger><SelectValue /></SelectTrigger>
                      <SelectContent>
                        {validHookRuleTypes.map(t => <SelectItem key={t} value={t}>{t}</SelectItem>)}
                      </SelectContent>
                    </Select>
                  </div>
                </div>

                {["regex_redact", "regex_replace", "block_pattern", "require_keyword", "tool_require", "tool_block"].includes(hookRuleType) && (
                  <div className="space-y-1.5">
                    <Label>Pattern</Label>
                    <Input value={pattern} onChange={e => setPattern(e.target.value)} placeholder="Regex pattern…" className="font-mono text-sm" />
                  </div>
                )}

                {hookRuleType === "regex_replace" && (
                  <div className="space-y-1.5">
                    <Label>Replacement</Label>
                    <Input value={replacement} onChange={e => setReplacement(e.target.value)} placeholder="Replacement string (use $1 for groups)" className="font-mono text-sm" />
                  </div>
                )}

                {["tool_require", "tool_block"].includes(hookRuleType) && (
                  <div className="space-y-1.5">
                    <Label>Tool Name</Label>
                    <Input value={toolName} onChange={e => setToolName(e.target.value)} placeholder="e.g. search" className="font-mono text-sm" />
                  </div>
                )}

                <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
                  <div className="space-y-1.5">
                    <Label>Order in Pack</Label>
                    <Input type="number" value={orderInPack} onChange={e => setOrderInPack(Number(e.target.value))} />
                  </div>
                  <div className="space-y-1.5">
                    <Label>Max Eval (ms)</Label>
                    <Input type="number" value={maxEvaluationMs} onChange={e => setMaxEvalMs(Number(e.target.value))} />
                  </div>
                  <div className="flex flex-col justify-end pb-1">
                    <div className="flex items-center gap-2">
                      <Switch id="stop-on-match" checked={stopOnMatch} onCheckedChange={setStopOnMatch} />
                      <Label htmlFor="stop-on-match" className="text-xs">Stop on match</Label>
                    </div>
                  </div>
                </div>
              </div>
            )}
          </div>

          {/* Rule Pack selector — hidden when packId is pre-set by the caller */}
          {packId === undefined && (
            <div className="space-y-1.5 border-t pt-3">
              <Label>Link to Rule Pack <span className="text-muted-foreground text-xs font-normal">(optional)</span></Label>
              <Select
                value={rulePackId !== undefined ? String(rulePackId) : "__none__"}
                onValueChange={v => setRulePackId(v === "__none__" ? undefined : Number(v))}
                disabled={packsLoading}
              >
                <SelectTrigger className="text-sm">
                  <SelectValue placeholder={packsLoading ? "Loading packs…" : "No pack"} />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="__none__">— No pack —</SelectItem>
                  {packs.map(p => (
                    <SelectItem key={p.id} value={String(p.id)}>{p.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <p className="text-xs text-muted-foreground">
                Assign this rule to a rule pack so it runs alongside the pack's hook rules.
              </p>
            </div>
          )}

          {initial && (
            <div className="flex items-center gap-2">
              <Switch id="rule-active" checked={isActive} onCheckedChange={setActive} />
              <Label htmlFor="rule-active">Active</Label>
            </div>
          )}
        </div>

        <DialogFooter>
          <DialogClose asChild>
            <Button variant="outline" disabled={saving}>Cancel</Button>
          </DialogClose>
          <Button onClick={save} disabled={saving}>
            {saving ? "Saving…" : "Save Rule"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

// ── Group Rule Templates ───────────────────────────────────────────────────────

export function GroupRuleTemplates({ tenantId = 1 }: { tenantId?: number }) {
  const [templates, setTemplates]   = useState<GroupRuleTemplateItem[]>([]);
  const [archetypes, setArchetypes] = useState<ArchetypeSummary[]>([]);
  const [loading, setLoading]       = useState(true);
  const [toggling, setToggling]     = useState<number | null>(null);

  const archetypeLabel = (id: string) =>
    id === "*" ? "All archetypes" : (archetypes.find(a => a.id === id)?.displayName ?? id);

  const load = useCallback(async () => {
    setLoading(true);
    try   { setTemplates(await api.getGroupRuleTemplates(tenantId)); }
    catch (e) { toast.error(String(e)); }
    finally { setLoading(false); }
  }, [tenantId]);

  useEffect(() => { load(); }, [load]);
  useEffect(() => { api.listArchetypes().then(setArchetypes).catch(() => {}); }, []);

  const toggle = async (t: GroupRuleTemplateItem) => {
    setToggling(t.id);
    try {
      if (t.isActivated) {
        await api.deactivateGroupRuleTemplate(t.id, tenantId);
        toast.success("Template deactivated.");
      } else {
        await api.activateGroupRuleTemplate(t.id, tenantId);
        toast.success("Template activated — rule added to your tenant.");
      }
      load();
    } catch (e) { toast.error(String(e)); }
    finally { setToggling(null); }
  };

  if (loading) return (
    <div className="space-y-2">
      {Array.from({ length: 3 }).map((_, i) => <Skeleton key={i} className="h-12 w-full" />)}
    </div>
  );

  if (templates.length === 0) return (
    <EmptyState
      icon={BookOpen}
      title="No group templates available"
      description="Your tenant is not a member of any group, or no groups have published rule templates."
    />
  );

  return (
    <div className="space-y-4">
      <p className="text-sm text-muted-foreground">
        Rule templates shared by your tenant groups. Activate the ones that apply to you — each activated template creates a tenant business rule you can manage independently.
      </p>
      <div className="rounded-md border overflow-hidden">
        <Table>
          <TableHeader>
            <TableRow>
              <TableHead>Rule</TableHead>
              <TableHead>Group</TableHead>
              <TableHead>Archetype</TableHead>
              <TableHead>Hook</TableHead>
              <TableHead className="w-24 text-center">Active</TableHead>
            </TableRow>
          </TableHeader>
          <TableBody>
            {templates.map(t => (
              <TableRow key={t.id} className={t.isActivated ? "bg-emerald-500/5" : ""}>
                <TableCell>
                  <div className="font-mono text-xs text-muted-foreground">{t.ruleKey}</div>
                  <div className="text-sm mt-0.5 line-clamp-2 text-foreground/80">{t.promptInjection}</div>
                </TableCell>
                <TableCell>
                  <Badge variant="outline" className="text-xs">{t.groupName}</Badge>
                </TableCell>
                <TableCell>
                  <span className="text-xs text-muted-foreground">
                    {archetypeLabel(t.agentType)}
                  </span>
                </TableCell>
                <TableCell>
                  {NON_DEFAULT_HOOK(t.hookPoint, t.hookRuleType) && (
                    <div className="flex items-center gap-1">
                      <Zap className="h-3 w-3 text-amber-400" />
                      <span className="text-xs font-mono text-amber-400">
                        {t.hookPoint} / {t.hookRuleType}
                      </span>
                    </div>
                  )}
                </TableCell>
                <TableCell className="text-center">
                  <Switch
                    checked={t.isActivated}
                    disabled={toggling === t.id}
                    onCheckedChange={() => toggle(t)}
                    className="data-[state=checked]:bg-emerald-500"
                  />
                </TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </div>
    </div>
  );
}
