/**
 * BusinessRuleEditor — full page form for creating and editing business rules.
 *
 * Routes:
 *   /rules/business/new              — create a new rule
 *   /rules/business/:id/edit         — edit an existing rule
 *
 * Query params:
 *   packId    — pre-assign the rule to this rule pack (hides the pack selector)
 *   returnTo  — path to navigate to after save (defaults to /rules/business)
 */
import { useCallback, useEffect, useState } from "react";
import { useNavigate, useParams, useSearchParams } from "react-router";
import {
  api,
  type AgentSummary,
  type ArchetypeSummary,
  type BusinessRule,
  type RulePack,
} from "@/api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Switch } from "@/components/ui/switch";
import {
  Select, SelectContent, SelectItem, SelectTrigger, SelectValue,
} from "@/components/ui/select";
import { ArrowLeft, Save, Zap } from "lucide-react";
import { toast } from "sonner";
import {
  HookRuleForm,
  type HookRuleData,
  emptyHookRule,
  normalizeRuleForHookPoint,
} from "@/components/HookRuleForm";

const CATEGORIES = ["general", "tone", "response_format", "safety", "terminology", "seasonal"];

function ruleToHookData(rule: BusinessRule): HookRuleData {
  return {
    id:              rule.id,
    hookPoint:       rule.hookPoint,
    ruleType:        rule.hookRuleType,
    pattern:         rule.pattern ?? "",
    instruction:     rule.promptInjection ?? "",
    replacement:     rule.replacement ?? "",
    toolName:        rule.toolName ?? "",
    orderInPack:     rule.orderInPack,
    stopOnMatch:     rule.stopOnMatch,
    maxEvaluationMs: rule.maxEvaluationMs,
  };
}

export function BusinessRuleEditor() {
  const { id } = useParams<{ id: string }>();
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();

  const isNew   = !id;
  const packIdParam = searchParams.get("packId");
  const returnTo    = searchParams.get("returnTo") ?? "/rules/business";
  const presetPackId = packIdParam ? Number(packIdParam) : undefined;

  // ── Loading ────────────────────────────────────────────────────────────────
  const [loading, setLoading] = useState(!isNew);
  const [saving, setSaving]   = useState(false);

  // ── Basic rule fields ──────────────────────────────────────────────────────
  const [agentType,    setAgentType]  = useState("*");
  const [ruleCategory, setCategory]   = useState("general");
  const [ruleKey,      setKey]        = useState("");
  const [priority,     setPriority]   = useState(100);
  const [isActive,     setActive]     = useState(true);
  const [scopeToAgent, setScopeToAgent] = useState(false);
  const [agentId,      setAgentId]    = useState<string | undefined>();
  const [rulePackId,   setRulePackId] = useState<number | undefined>(presetPackId);

  // ── Hook configuration ─────────────────────────────────────────────────────
  const [hookData, setHookData] = useState<HookRuleData>(() => ({
    ...emptyHookRule(0),
    hookPoint: "OnInit",
    ruleType:  "inject_prompt",
  }));

  // ── Supporting data ────────────────────────────────────────────────────────
  const [agents, setAgents]         = useState<AgentSummary[]>([]);
  const [archetypes, setArchetypes] = useState<ArchetypeSummary[]>([]);
  const [packs, setPacks]           = useState<RulePack[]>([]);
  const [packsLoading, setPacksLoading] = useState(false);

  // Load agents, archetypes + packs on mount
  useEffect(() => {
    api.listAgents().then(setAgents).catch(() => {});
    api.listArchetypes().then(setArchetypes).catch(() => {});
    if (presetPackId === undefined) {
      setPacksLoading(true);
      api.getRulePacks(1).then(setPacks).catch(() => {}).finally(() => setPacksLoading(false));
    }
  }, [presetPackId]);

  // Load existing rule when editing
  const loadRule = useCallback(async () => {
    if (isNew || !id) return;
    setLoading(true);
    try {
      // getBusinessRules returns the list — find by id.
      // Alternatively use a direct GET if available. We load all and find ours.
      const rules = await api.getBusinessRules(1, "*");
      const rule = rules.find((r) => r.id === Number(id));
      if (!rule) { toast.error("Rule not found"); navigate(returnTo); return; }

      setAgentType(rule.agentType);
      setCategory(rule.ruleCategory);
      setKey(rule.ruleKey);
      setPriority(rule.priority);
      setActive(rule.isActive);
      setScopeToAgent(!!rule.agentId);
      setAgentId(rule.agentId);
      setRulePackId(rule.rulePackId ?? presetPackId);
      setHookData(ruleToHookData(rule));
    } catch (e) {
      toast.error(String(e));
    } finally {
      setLoading(false);
    }
  }, [id, isNew, navigate, returnTo, presetPackId]);

  useEffect(() => { loadRule(); }, [loadRule]);

  // ── Save ──────────────────────────────────────────────────────────────────
  const save = async () => {
    if (!ruleKey.trim()) {
      toast.error("Rule Key is required.");
      return;
    }
    if (!hookData.instruction?.trim() && hookData.ruleType !== "model_switch") {
      toast.error("Instruction / Prompt text is required.");
      return;
    }
    if (scopeToAgent && !agentId) {
      toast.error("Select a specific agent or uncheck 'Scope to a specific agent'.");
      return;
    }
    setSaving(true);
    try {
      const effectiveAgentId = scopeToAgent ? agentId : undefined;
      const dto = {
        guid: crypto.randomUUID(),
        agentType,
        ruleCategory,
        ruleKey,
        promptInjection: hookData.instruction || undefined,
        priority,
        isActive,
        agentId: effectiveAgentId,
        rulePackId,
        // hook fields
        hookPoint:       hookData.hookPoint,
        hookRuleType:    hookData.ruleType,
        pattern:         hookData.pattern || undefined,
        replacement:     hookData.replacement || undefined,
        toolName:        hookData.toolName || undefined,
        orderInPack:     hookData.orderInPack,
        stopOnMatch:     hookData.stopOnMatch,
        maxEvaluationMs: hookData.maxEvaluationMs,
      };
      if (isNew) {
        await api.createBusinessRule(dto, 1);
        toast.success("Rule created.");
      } else {
        await api.updateBusinessRule(Number(id), dto, 1);
        toast.success("Rule updated.");
      }
      navigate(returnTo);
    } catch (e: unknown) {
      toast.error(String(e));
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div className="text-muted-foreground p-6">Loading…</div>;

  return (
    <div className="space-y-6 max-w-2xl">
      {/* Header */}
      <div className="flex items-center gap-3">
        <Button variant="ghost" size="sm" onClick={() => navigate(returnTo)}>
          <ArrowLeft className="mr-1 size-4" /> Back
        </Button>
        <h1 className="text-2xl font-bold">
          {isNew ? "New Business Rule" : "Edit Business Rule"}
        </h1>
      </div>

      {/* Basic rule fields */}
      <Card>
        <CardHeader>
          <CardTitle>Rule Details</CardTitle>
          <CardDescription>
            Identify the rule and define when it applies to agents.
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label>Archetype</Label>
              <Select value={agentType} onValueChange={setAgentType} disabled={!isNew}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="*">* (all archetypes)</SelectItem>
                  {archetypes.map((a) => (
                    <SelectItem key={a.id} value={a.id}>
                      {a.displayName}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1.5">
              <Label>Category</Label>
              <Select value={ruleCategory} onValueChange={setCategory}>
                <SelectTrigger><SelectValue /></SelectTrigger>
                <SelectContent>
                  {CATEGORIES.map((c) => (
                    <SelectItem key={c} value={c}>{c}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            <div className="space-y-1.5">
              <Label>Rule Key *</Label>
              <Input
                value={ruleKey}
                onChange={(e) => setKey(e.target.value)}
                placeholder="e.g. weather.always_celsius"
                className="font-mono text-sm"
              />
            </div>
            <div className="space-y-1.5">
              <Label>
                Priority{" "}
                <span className="text-muted-foreground text-xs font-normal">(lower = first)</span>
              </Label>
              <Input
                type="number"
                value={priority}
                onChange={(e) => setPriority(Number(e.target.value))}
              />
            </div>
          </div>

          {/* Scope to specific agent */}
          <div className="space-y-2">
            <div className="flex items-center gap-2">
              <Switch
                id="scope-agent"
                checked={scopeToAgent}
                onCheckedChange={(v) => { setScopeToAgent(v); if (!v) setAgentId(undefined); }}
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
                  {agents.map((a) => (
                    <SelectItem key={a.id} value={a.id}>
                      {a.displayName || a.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </div>

          {/* Rule Pack — hidden when packId was passed in the URL */}
          {presetPackId === undefined && (
            <div className="space-y-1.5">
              <Label>
                Link to Rule Pack{" "}
                <span className="text-muted-foreground text-xs font-normal">(optional)</span>
              </Label>
              <Select
                value={rulePackId !== undefined ? String(rulePackId) : "__none__"}
                onValueChange={(v) =>
                  setRulePackId(v === "__none__" ? undefined : Number(v))
                }
                disabled={packsLoading}
              >
                <SelectTrigger className="text-sm">
                  <SelectValue placeholder={packsLoading ? "Loading packs…" : "No pack"} />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="__none__">— No pack —</SelectItem>
                  {packs.map((p) => (
                    <SelectItem key={p.id} value={String(p.id)}>{p.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
              <p className="text-xs text-muted-foreground">
                Assign this rule to a rule pack so it runs alongside the pack's hook rules.
              </p>
            </div>
          )}
          {presetPackId !== undefined && (
            <p className="text-xs text-muted-foreground">
              This rule will be linked to rule pack #{presetPackId}.
            </p>
          )}

          {/* Active toggle — only for editing */}
          {!isNew && (
            <div className="flex items-center gap-2">
              <Switch id="rule-active" checked={isActive} onCheckedChange={setActive} />
              <Label htmlFor="rule-active">Active</Label>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Hook Configuration */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <Zap className="size-4 text-amber-400" />
            Hook Configuration
          </CardTitle>
          <CardDescription>
            Define when and how this rule fires in the agent ReAct lifecycle.
            The "Instruction / Text" field is the prompt text injected or appended
            at the selected hook point.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <HookRuleForm
            value={hookData}
            onChange={(patch) =>
              setHookData((prev) =>
                "hookPoint" in patch
                  ? normalizeRuleForHookPoint({ ...prev, ...patch }, patch.hookPoint!)
                  : { ...prev, ...patch }
              )
            }
          />
        </CardContent>
      </Card>

      {/* Save */}
      <div className="flex items-center gap-3">
        <Button onClick={save} disabled={saving}>
          <Save className="mr-2 size-4" />
          {saving ? "Saving…" : isNew ? "Create Rule" : "Save Changes"}
        </Button>
        <Button variant="outline" onClick={() => navigate(returnTo)} disabled={saving}>
          Cancel
        </Button>
      </div>
    </div>
  );
}
