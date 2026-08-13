import { useEffect, useState } from "react";
import { AlertTriangle, ChevronDown, Loader2, Server } from "lucide-react";
import { api, type McpServer, type McpToolBinding, type McpToolInfo } from "@/api";
import {
  Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogFooter,
} from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";

interface ProbeOpts
{
  endpoint?: string;
  command?: string;
  args?: string[];
  passSsoToken?: boolean;
  credentialRef?: string;
}

interface ProbeResult
{
  key: string;
  label: string;
  kind: "shared" | "custom";
  loading: boolean;
  success?: boolean;
  tools?: McpToolInfo[];
  error?: string;
}

interface AgentToolsPreviewDialogProps
{
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Agent's mcpServerRefsJson — JSON string[] of shared TenantMcpServerEntity names. */
  sharedServerRefsJson?: string;
  /** Agent's inline McpToolBinding rows. Empty-name placeholder rows are skipped. */
  bindings: McpToolBinding[];
  environmentId?: number;
}

function sharedServerOpts(s: McpServer): ProbeOpts
{
  if (s.transport === "http" || s.transport === "sse")
  {
    return { endpoint: s.endpoint, passSsoToken: s.passSsoToken, credentialRef: s.defaultCredentialRef };
  }
  let args: string[] = [];
  try { args = s.argsJson ? JSON.parse(s.argsJson) : []; } catch { /* malformed args JSON — probe with none */ }
  return { command: s.command, args };
}

function bindingOpts(b: McpToolBinding): ProbeOpts
{
  if (b.transport === "http" || b.transport === "sse")
  {
    return { endpoint: b.endpoint, passSsoToken: b.passSsoToken, credentialRef: b.credentialRef };
  }
  return { command: b.command, args: b.args };
}

/**
 * Connects to every MCP server currently configured on the agent (shared refs + inline custom
 * bindings) and lists each one's available tools/endpoints in one place — a read-only "what can
 * this agent actually reach" view. Independent of DockerGatewayPanel's single-binding discovery
 * flow, which is for building a new binding rather than reviewing already-configured ones.
 */
export function AgentToolsPreviewDialog({ open, onOpenChange, sharedServerRefsJson, bindings, environmentId }: AgentToolsPreviewDialogProps)
{
  const [loading, setLoading] = useState(false);
  const [results, setResults] = useState<ProbeResult[]>([]);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());

  useEffect(() =>
  {
    if (!open) return;
    let cancelled = false;
    setLoading(true);
    setResults([]);
    setExpanded(new Set());

    (async () =>
    {
      let sharedNames: string[] = [];
      try { sharedNames = sharedServerRefsJson ? JSON.parse(sharedServerRefsJson) : []; } catch { /* malformed refs */ }

      const allServers = sharedNames.length > 0
        ? await api.listMcpServers(undefined, environmentId).catch(() => [])
        : [];

      const targets: (Omit<ProbeResult, "loading"> & { opts: ProbeOpts })[] = [
        ...sharedNames
          .map((name) => allServers.find((s) => s.name === name))
          .filter((s): s is McpServer => !!s)
          .map((s) => ({ key: `shared:${s.id}`, label: s.name, kind: "shared" as const, opts: sharedServerOpts(s) })),
        ...bindings
          .filter((b) => b.name.trim() !== "")
          .map((b, i) => ({ key: `custom:${i}:${b.name}`, label: b.name, kind: "custom" as const, opts: bindingOpts(b) })),
      ];

      if (cancelled) return;
      setResults(targets.map(({ key, label, kind }) => ({ key, label, kind, loading: true })));
      setLoading(false);

      await Promise.all(targets.map(async (t) =>
      {
        try
        {
          const result = await api.probeMcp(t.opts);
          if (cancelled) return;
          setResults((rs) => rs.map((r) => r.key === t.key
            ? { ...r, loading: false, success: result.success, tools: result.tools, error: result.error }
            : r));
        }
        catch (e)
        {
          if (cancelled) return;
          setResults((rs) => rs.map((r) => r.key === t.key ? { ...r, loading: false, success: false, error: String(e) } : r));
        }
      }));
    })();

    return () => { cancelled = true; };
    // Snapshot of the agent's current config at the moment the dialog opens — it's modal, so
    // bindings/refs can't change underneath it while it's open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [open]);

  const toggle = (key: string) => setExpanded((s) => {
    const n = new Set(s);
    if (n.has(key)) { n.delete(key); } else { n.add(key); }
    return n;
  });

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:max-w-2xl">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2">
            <Server className="size-4" /> Available Tools
          </DialogTitle>
          <DialogDescription>
            Connects to every server configured on this agent and lists what each one exposes.
          </DialogDescription>
        </DialogHeader>

        {loading ? (
          <div className="flex items-center gap-2 text-sm text-muted-foreground py-6 justify-center">
            <Loader2 className="size-4 animate-spin" /> Checking configured servers…
          </div>
        ) : results.length === 0 ? (
          <p className="text-sm text-muted-foreground py-4">No MCP servers configured on this agent yet.</p>
        ) : (
          <ScrollArea className="max-h-[60vh] pr-3">
            <div className="space-y-2">
              {results.map((r) => (
                <Collapsible key={r.key} open={expanded.has(r.key)} onOpenChange={() => toggle(r.key)}>
                  <div className="rounded-md border px-3 py-2">
                    <div className="flex items-center gap-2 flex-wrap">
                      <span className="text-sm font-medium">{r.label}</span>
                      <Badge variant="outline" className="text-[10px]">{r.kind === "shared" ? "shared" : "custom"}</Badge>
                      {r.loading ? (
                        <span className="flex items-center gap-1 text-xs text-muted-foreground ml-auto">
                          <Loader2 className="size-3 animate-spin" /> Connecting…
                        </span>
                      ) : r.success ? (
                        <>
                          <Badge className="bg-emerald-500/15 text-emerald-600 border-emerald-500/30 dark:text-emerald-400">
                            {r.tools?.length ?? 0} tool{r.tools?.length === 1 ? "" : "s"}
                          </Badge>
                          {(r.tools?.length ?? 0) > 0 && (
                            <CollapsibleTrigger asChild>
                              <Button variant="ghost" size="sm" className="h-7 text-xs ml-auto">
                                <ChevronDown className={`size-3.5 mr-1 transition-transform ${expanded.has(r.key) ? "rotate-180" : ""}`} />
                                {expanded.has(r.key) ? "Hide" : "Show"}
                              </Button>
                            </CollapsibleTrigger>
                          )}
                        </>
                      ) : (
                        <span className="flex items-center gap-1 text-xs text-destructive ml-auto">
                          <AlertTriangle className="size-3.5" /> Failed
                        </span>
                      )}
                    </div>
                    {!r.loading && !r.success && r.error && (
                      <p className="text-xs text-destructive mt-1.5">{r.error}</p>
                    )}
                    {!r.loading && r.success && (r.tools?.length ?? 0) > 0 && (
                      <CollapsibleContent className="mt-2 pt-2 border-t space-y-1.5">
                        {r.tools!.map((t) => (
                          <div key={t.name} className="text-xs">
                            <span className="font-mono font-medium">{t.name}</span>
                            {t.description && <span className="text-muted-foreground"> — {t.description}</span>}
                          </div>
                        ))}
                      </CollapsibleContent>
                    )}
                  </div>
                </Collapsible>
              ))}
            </div>
          </ScrollArea>
        )}

        <DialogFooter>
          <Button variant="outline" onClick={() => onOpenChange(false)}>Close</Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
