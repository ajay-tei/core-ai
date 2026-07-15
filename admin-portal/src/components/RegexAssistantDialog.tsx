import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter } from "@/components/ui/dialog";
import { Textarea } from "@/components/ui/textarea";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Alert, AlertDescription } from "@/components/ui/alert";
import { Loader2, Sparkles, Check, AlertTriangle, Plus, X, RefreshCw } from "lucide-react";
import { api, type RegexSuggestionRequest, type RegexSuggestion } from "@/api";

// ── Types ─────────────────────────────────────────────────────────────────────

interface Props {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  ruleType?: string;
  hookPoint?: string;
  tenantId?: number;
  onApply?: (pattern: string) => void;
}

// ── Component ─────────────────────────────────────────────────────────────────

export function RegexAssistantDialog({
  open,
  onOpenChange,
  ruleType,
  hookPoint,
  tenantId = 1,
  onApply,
}: Props) {
  const [intent, setIntent] = useState("");
  const [positives, setPositives] = useState<string[]>([""]);
  const [negatives, setNegatives] = useState<string[]>([""]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [suggestion, setSuggestion] = useState<RegexSuggestion | null>(null);
  const [editedPattern, setEditedPattern] = useState("");

  const handleSuggest = async () => {
    if (!intent.trim()) {
      setError("Please describe what the regex should match.");
      return;
    }
    setLoading(true);
    setError(null);
    setSuggestion(null);

    const req: RegexSuggestionRequest = {
      intentDescription: intent.trim().slice(0, 500),
      sampleMatches: positives.filter(s => s.trim()),
      sampleNonMatches: negatives.filter(s => s.trim()),
      ruleType,
      hookPoint,
    };

    try {
      const result = await api.suggestRegex(req, tenantId);
      setSuggestion(result);
      setEditedPattern(result.pattern ?? "");
    } catch (e: unknown) {
      setError(e instanceof Error ? e.message : "Regex suggestion failed.");
    } finally {
      setLoading(false);
    }
  };

  const addSample = (list: string[], setter: (v: string[]) => void) => {
    if (list.length < 10) setter([...list, ""]);
  };

  const updateSample = (list: string[], setter: (v: string[]) => void, i: number, val: string) => {
    const next = [...list];
    next[i] = val;
    setter(next);
  };

  const removeSample = (list: string[], setter: (v: string[]) => void, i: number) => {
    setter(list.filter((_, idx) => idx !== i));
  };

  const handleApply = () => {
    if (editedPattern.trim()) {
      onApply?.(editedPattern.trim());
      onOpenChange(false);
    }
  };

  const reset = () => {
    setSuggestion(null);
    setEditedPattern("");
    setError(null);
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-2xl max-h-[90vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Sparkles className="w-5 h-5 text-violet-500" />
            AI Regex Builder
          </DialogTitle>
          <DialogDescription>
            Describe what your regex should match and provide examples. The AI will generate and validate a pattern.
            {ruleType && <span className="ml-2"><Badge variant="outline">{ruleType}</Badge></span>}
            {hookPoint && <span className="ml-1"><Badge variant="secondary">{hookPoint}</Badge></span>}
          </DialogDescription>
        </DialogHeader>

        <div className="flex flex-col gap-5 py-2">
          {/* Intent */}
          <div className="space-y-1">
            <Label>What should the regex match? *</Label>
            <Textarea
              placeholder="e.g. Match any email address ending in @example.com or @acme.org"
              value={intent}
              onChange={e => setIntent(e.target.value.slice(0, 500))}
              rows={3}
            />
            <p className="text-xs text-muted-foreground text-right">{intent.length}/500</p>
          </div>

          {/* Positive samples */}
          <div className="space-y-2">
            <Label>Examples that SHOULD match</Label>
            <div className="flex flex-col gap-2">
              {positives.map((s, i) => (
                <div key={i} className="flex gap-2">
                  <Input
                    placeholder="e.g. user@example.com"
                    value={s}
                    onChange={e => updateSample(positives, setPositives, i, e.target.value)}
                    className="flex-1 font-mono text-sm"
                  />
                  <Button
                    variant="ghost"
                    size="icon"
                    onClick={() => removeSample(positives, setPositives, i)}
                    disabled={positives.length <= 1}
                  >
                    <X className="w-4 h-4" />
                  </Button>
                </div>
              ))}
              <Button
                variant="outline"
                size="sm"
                onClick={() => addSample(positives, setPositives)}
                disabled={positives.length >= 10}
              >
                <Plus className="w-4 h-4 mr-1" /> Add Example
              </Button>
            </div>
          </div>

          {/* Negative samples */}
          <div className="space-y-2">
            <Label>Examples that should NOT match</Label>
            <div className="flex flex-col gap-2">
              {negatives.map((s, i) => (
                <div key={i} className="flex gap-2">
                  <Input
                    placeholder="e.g. user@gmail.com"
                    value={s}
                    onChange={e => updateSample(negatives, setNegatives, i, e.target.value)}
                    className="flex-1 font-mono text-sm"
                  />
                  <Button
                    variant="ghost"
                    size="icon"
                    onClick={() => removeSample(negatives, setNegatives, i)}
                    disabled={negatives.length <= 1}
                  >
                    <X className="w-4 h-4" />
                  </Button>
                </div>
              ))}
              <Button
                variant="outline"
                size="sm"
                onClick={() => addSample(negatives, setNegatives)}
                disabled={negatives.length >= 10}
              >
                <Plus className="w-4 h-4 mr-1" /> Add Counter-Example
              </Button>
            </div>
          </div>

          {/* Error */}
          {error && (
            <Alert variant="destructive">
              <AlertTriangle className="h-4 w-4" />
              <AlertDescription>{error}</AlertDescription>
            </Alert>
          )}

          {/* Suggestion result */}
          {suggestion && (
            <div className="flex flex-col gap-3 border rounded-lg p-4 bg-muted/30">
              {suggestion.warnings.length > 0 && (
                <Alert>
                  <AlertTriangle className="h-4 w-4" />
                  <AlertDescription>
                    <ul className="list-disc list-inside">
                      {suggestion.warnings.map((w, i) => <li key={i}>{w}</li>)}
                    </ul>
                  </AlertDescription>
                </Alert>
              )}

              <div className="space-y-1">
                <Label>Suggested Pattern</Label>
                <Input
                  value={editedPattern}
                  onChange={e => setEditedPattern(e.target.value)}
                  className="font-mono text-sm"
                  placeholder="Pattern..."
                />
              </div>

              {suggestion.explanation && (
                <p className="text-sm text-muted-foreground">{suggestion.explanation}</p>
              )}

              {/* Preview */}
              {(suggestion.previewMatches.length > 0 || suggestion.previewNonMatches.length > 0) && (
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 text-xs">
                  {suggestion.previewMatches.length > 0 && (
                    <div>
                      <p className="font-medium text-green-600 mb-1">✓ Matches</p>
                      <div className="flex flex-col gap-1">
                        {suggestion.previewMatches.map((m, i) => (
                          <code key={i} className="bg-green-50 dark:bg-green-950/20 rounded px-1.5 py-0.5 text-green-700 dark:text-green-400">{m}</code>
                        ))}
                      </div>
                    </div>
                  )}
                  {suggestion.previewNonMatches.length > 0 && (
                    <div>
                      <p className="font-medium text-red-600 mb-1">✗ Non-matches</p>
                      <div className="flex flex-col gap-1">
                        {suggestion.previewNonMatches.map((m, i) => (
                          <code key={i} className="bg-red-50 dark:bg-red-950/20 rounded px-1.5 py-0.5 text-red-700 dark:text-red-400">{m}</code>
                        ))}
                      </div>
                    </div>
                  )}
                </div>
              )}

              <Button
                variant="ghost"
                size="sm"
                onClick={reset}
              >
                <RefreshCw className="w-3 h-3 mr-1" /> Try Again
              </Button>
            </div>
          )}
        </div>

        <DialogFooter className="gap-2">
          <Button
            variant="outline"
            onClick={handleSuggest}
            disabled={loading || !intent.trim()}
          >
            {loading ? <Loader2 className="w-4 h-4 mr-2 animate-spin" /> : <Sparkles className="w-4 h-4 mr-2" />}
            {suggestion ? "Regenerate" : "Generate Regex"}
          </Button>
          <Button
            onClick={handleApply}
            disabled={!editedPattern.trim() || suggestion?.warnings.some(w => w.toLowerCase().includes("invalid") || w.toLowerCase().includes("catastrophic"))}
          >
            <Check className="w-4 h-4 mr-2" />
            Use This Pattern
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
