import { useState, useRef, useEffect } from "react";
import { Link, useNavigate, useParams, useLocation, useSearchParams } from "react-router";
import { toast } from "sonner";
import {
  ArrowLeft,
  Bot,
  ChevronDown,
  ChevronRight,
  History,
  Mic,
  RotateCcw,
  Send,
  SlidersHorizontal,
  User,
  Wrench,
} from "lucide-react";
import {
  api,
  type AgentSummary,
  type AgentStreamChunk,
  type LlmConfig,
  type AvailableLlmConfig,
  type VerificationResult,
  type FollowUpQuestion,
  type TurnSummary,
  type IterationDetail,
  type CredentialGroupOption,
} from "@/api";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Label } from "@/components/ui/label";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import { Separator } from "@/components/ui/separator";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import { useIsMobile } from "@/hooks/use-mobile";
import { MarkdownMessage } from "@/components/chat/MarkdownMessage";
import { ToolResultTable } from "@/components/chat/ToolResultTable";
import { SqlBlock } from "@/components/chat/SqlBlock";
import { auth } from "@/lib/auth";
import { cn } from "@/lib/utils";

// ─────────────────────────────────────────────────────────────────────────────
// Types
// ─────────────────────────────────────────────────────────────────────────────

interface ToolCall {
  name: string;
  input: string;
  output?: string;
  isDelegation?: boolean;
  delegatedAgentName?: string;
  childSessionId?: string;
}

interface Iteration {
  number: number;
  thinking?: string;
  toolCalls: ToolCall[];
}

interface TimelineEntry {
  time: number;
  type: string;
  iteration?: number;
  detail: string;
}

interface Message {
  role: "user" | "agent";
  text: string;
  iterations?: Iteration[];
  toolsUsed?: string[];
  executionTime?: string;
  verification?: VerificationResult;
  followUpQuestions?: FollowUpQuestion[];
  error?: boolean;
}

// Minimal Web Speech API typings (webkitSpeechRecognition is not in lib.dom).
interface SpeechAlternativeLike {
  transcript: string;
}
interface SpeechResultLike {
  isFinal: boolean;
  length: number;
  0: SpeechAlternativeLike;
}
interface SpeechRecognitionEventLike {
  resultIndex: number;
  results: { length: number; [i: number]: SpeechResultLike };
}
interface SpeechRecognitionLike {
  continuous: boolean;
  interimResults: boolean;
  lang: string;
  start(): void;
  stop(): void;
  onresult: ((e: SpeechRecognitionEventLike) => void) | null;
  onend: (() => void) | null;
  onerror: (() => void) | null;
}

// Maps a stored turn's iteration detail into the chat's iteration trace,
// preserving sub-agent (delegation) calls with a link to the child session.
function iterationsToTrace(iters: IterationDetail[]): Iteration[] {
  return iters.map((it) => ({
    number: it.iterationNumber,
    thinking: it.thinkingText ?? undefined,
    toolCalls: it.toolCalls.map((tc) => ({
      name: tc.isAgentDelegation && tc.delegatedAgentName ? tc.delegatedAgentName : tc.toolName,
      input: tc.toolInput ?? "",
      output: tc.toolOutput ?? "",
      isDelegation: tc.isAgentDelegation,
      delegatedAgentName: tc.delegatedAgentName,
      childSessionId: tc.childSessionId,
    })),
  }));
}

// Builds the resumed chat history. Loads each turn's full iteration trace in
// parallel so historical sub-agent calls (delegations) are visible — falling
// back to a lightweight text bubble if a turn's trace can't be loaded.
async function buildResumedMessages(sessionId: string, turns: TurnSummary[]): Promise<Message[]> {
  const iterLists = await Promise.all(
    turns.map((t) =>
      api.getTurnIterations(sessionId, t.turnNumber).catch(() => [] as IterationDetail[]),
    ),
  );

  const msgs: Message[] = [];
  turns.forEach((t, idx) => {
    msgs.push({ role: "user", text: t.userMessage ?? t.userMessagePreview ?? "" });

    const trace = iterationsToTrace(iterLists[idx]);
    const toolsUsed = trace.flatMap((i) => i.toolCalls.map((c) => c.name));
    msgs.push({
      role: "agent",
      text: t.assistantMessage ?? t.assistantMessagePreview ?? "(no response)",
      iterations: trace.length > 0 ? trace : undefined,
      toolsUsed: toolsUsed.length > 0 ? toolsUsed : undefined,
      executionTime: t.executionTimeMs > 0 ? `${(t.executionTimeMs / 1000).toFixed(1)}s` : undefined,
    });
  });
  return msgs;
}

// ─────────────────────────────────────────────────────────────────────────────
// Verification Badge
// ─────────────────────────────────────────────────────────────────────────────

function VerificationBadge({ v }: { v: VerificationResult }) {
  if (v.mode === "Off") return null;
  if (v.wasBlocked) return (
    <Badge variant="destructive" className="text-xs">Blocked — low confidence</Badge>
  );
  if (v.isVerified) return (
    <Badge className="text-xs bg-emerald-600 hover:bg-emerald-600">
      Verified {Math.round(v.confidence * 100)}%
    </Badge>
  );
  return (
    <Badge variant="outline" className="text-xs border-amber-500 text-amber-500">
      Unverified{v.ungroundedClaims.length > 0 ? ` · ${v.ungroundedClaims.length} unsupported claim(s)` : ""}
    </Badge>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Tool I/O rendering helpers
// ─────────────────────────────────────────────────────────────────────────────

// Extracts a SQL string from an execute_sql_query tool input (accepts several
// common parameter names). Returns null when the input isn't SQL-shaped.
function extractSql(name: string, input: string): string | null {
  if (!/sql|query/i.test(name)) return null;
  try {
    const obj = JSON.parse(input) as Record<string, unknown>;
    const sql = obj.query ?? obj.sql ?? obj.statement;
    return typeof sql === "string" ? sql : null;
  } catch {
    return null;
  }
}

function ToolInput({ name, input }: { name: string; input: string }) {
  const sql = extractSql(name, input);
  if (sql) return <SqlBlock sql={sql} />;
  return (
    <pre className="text-[11px] text-foreground/70 whitespace-pre-wrap break-words bg-background/50 rounded p-1.5 overflow-auto max-h-24">
      {input}
    </pre>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Iteration Trace
// ─────────────────────────────────────────────────────────────────────────────

function IterationTrace({ iterations, detailed }: { iterations: Iteration[]; detailed: boolean }) {
  const [open, setOpen] = useState(false);

  return (
    <Collapsible open={open} onOpenChange={setOpen} className="mt-2">
      <CollapsibleTrigger className="flex items-center gap-1.5 text-xs text-muted-foreground hover:text-foreground transition-colors">
        {open ? <ChevronDown className="size-3" /> : <ChevronRight className="size-3" />}
        <Wrench className="size-3" />
        {iterations.length} iteration{iterations.length !== 1 ? "s" : ""}
      </CollapsibleTrigger>
      <CollapsibleContent>
        <div className="mt-2 rounded-md border bg-muted/30 p-3 space-y-4 text-xs max-h-96 overflow-y-auto">
          {iterations.map((iter) => (
            <div key={iter.number} className="space-y-2">
              <div className="font-semibold text-indigo-400">Iteration {iter.number}</div>
              {iter.thinking && (
                <p className="italic text-muted-foreground pl-2 border-l-2 border-muted">
                  {detailed ? iter.thinking : (iter.thinking.length > 200 ? iter.thinking.slice(0, 200) + "…" : iter.thinking)}
                </p>
              )}
              {iter.toolCalls.map((tc, ti) => (
                <div key={ti} className={cn("pl-2 border-l-2 space-y-1", tc.isDelegation ? "border-violet-500/50" : "border-amber-600/50")}>
                  <div className={cn("font-medium flex items-center gap-1", tc.isDelegation ? "text-violet-400" : "text-amber-400")}>
                    {tc.isDelegation ? <Bot className="size-3" /> : <Wrench className="size-3" />}
                    {tc.isDelegation ? `Sub-agent: ${tc.delegatedAgentName ?? tc.name}` : tc.name}
                    {tc.output === undefined && <span className="text-muted-foreground ml-1">⏳</span>}
                    {tc.isDelegation && tc.childSessionId && (
                      <Link
                        to={`/sessions/${tc.childSessionId}`}
                        className="ml-1 text-[10px] text-primary hover:underline"
                      >
                        view session
                      </Link>
                    )}
                  </div>
                  <ToolInput name={tc.name} input={tc.input} />
                  {tc.output !== undefined && (
                    <ToolResultTable output={tc.output} />
                  )}
                </div>
              ))}
            </div>
          ))}
        </div>
      </CollapsibleContent>
    </Collapsible>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Live streaming indicator
// ─────────────────────────────────────────────────────────────────────────────

// Animated three-dot "typing" indicator, like a standard chat agent thinking.
function TypingDots() {
  return (
    <span className="inline-flex items-center gap-1" role="status" aria-label="Thinking">
      <span className="size-1.5 rounded-full bg-muted-foreground/60 animate-bounce [animation-delay:-0.3s]" />
      <span className="size-1.5 rounded-full bg-muted-foreground/60 animate-bounce [animation-delay:-0.15s]" />
      <span className="size-1.5 rounded-full bg-muted-foreground/60 animate-bounce" />
    </span>
  );
}

function LiveFeed({
  iterations,
  status,
  plan,
  timeline,
  detailed,
  answer,
}: {
  iterations: Iteration[];
  status: string;
  plan: { steps: string[]; revised: boolean } | null;
  timeline: TimelineEntry[];
  detailed: boolean;
  answer: string;
}) {
  return (
    <div className="flex flex-col items-start gap-3">
      <div className="flex gap-2.5">
        <Avatar className="size-7 shrink-0 mt-0.5">
          <AvatarFallback className="bg-primary/10">
            <Bot className="size-3.5 text-primary" />
          </AvatarFallback>
        </Avatar>
        <div className="rounded-2xl rounded-tl-sm bg-muted px-4 py-3 max-w-[85%] min-w-64 space-y-2">
          {plan && plan.steps.length > 0 && (
            <div className="rounded border border-indigo-500/30 bg-indigo-500/5 p-2.5 space-y-1">
              <p className="text-[11px] font-semibold text-indigo-400">
                {plan.revised ? "Revised Plan" : "Plan"}
              </p>
              {plan.steps.map((step, i) => (
                <p key={i} className="text-[11px] text-muted-foreground pl-2">{step}</p>
              ))}
            </div>
          )}

          {/* Full per-iteration thinking + tool I/O only in Detailed mode. When
              off, the running `status` line already conveys the current action
              (e.g. "Calling execute_sql_query…") without exposing tool details. */}
          {detailed && iterations.map((iter) => (
            <div key={iter.number} className="space-y-1.5">
              <p className="text-[11px] font-semibold text-indigo-400">Iteration {iter.number}</p>
              {iter.thinking && (
                <p className="text-[11px] italic text-muted-foreground pl-2 border-l-2 border-muted-foreground/30">
                  {iter.thinking}
                </p>
              )}
              {iter.toolCalls.map((tc, ti) => (
                <div key={ti} className="pl-2 border-l-2 border-amber-600/50 space-y-1">
                  <p className="text-[11px] font-medium text-amber-400 flex items-center gap-1">
                    <Wrench className="size-2.5" />{tc.name} {tc.output === undefined ? "⏳" : "✓"}
                  </p>
                  <ToolInput name={tc.name} input={tc.input} />
                  {tc.output !== undefined && (
                    <ToolResultTable output={tc.output} />
                  )}
                </div>
              ))}
            </div>
          ))}

          {answer && (
            <div className="pt-1">
              <MarkdownMessage content={answer} />
            </div>
          )}

          {/* Persistent activity indicator while streaming. Sits below any
              streamed text so the current action (e.g. "Calling execute_sql_query…")
              stays visible during tool execution. Generic phases show dots only. */}
          {(() => {
            const generic = !status || ["thinking...", "analyzing…", "analyzing...", "connecting..."].includes(status.toLowerCase());
            return (
              <div className="flex items-center gap-2 pt-0.5 text-sm text-muted-foreground">
                <TypingDots />
                {!generic && <span>{status}</span>}
              </div>
            );
          })()}
        </div>
      </div>

      {detailed && timeline.length > 0 && (
        <div className="ml-9 rounded-md border bg-muted/20 p-2 max-h-56 overflow-y-auto w-full max-w-[85%]">
          <p className="text-[11px] font-semibold text-indigo-400 mb-2">Event Log ({timeline.length})</p>
          <div className="mb-2 rounded border border-sky-500/20 bg-sky-500/5 px-2 py-1.5 text-[10px] text-muted-foreground">
            <span className="font-semibold text-sky-400">Hook Detail Legend:</span>{" "}
            <span className="font-mono">triggered</span>=rules fired, {" "}
            <span className="font-mono">filtered</span>=tool calls suppressed, {" "}
            <span className="font-mono">blocked</span>=policy blocked, {" "}
            <span className="font-mono">errorAction</span>=continue/retry/abort
          </div>
          {timeline.map((e, i) => {
            const colors: Record<string, string> = {
              tools_available: "text-emerald-400", plan: "text-indigo-400",
              plan_revised: "text-violet-400", iteration_start: "text-indigo-400",
              thinking: "text-muted-foreground", tool_call: "text-amber-400",
              tool_result: "text-emerald-400", final_response: "text-cyan-400",
              hook_executed: "text-sky-400",
              verification: "text-emerald-400", continuation_start: "text-fuchsia-400",
              correction: "text-red-400", error: "text-destructive", done: "text-emerald-400",
            };
            return (
              <div key={i} className="flex gap-1.5 text-[10px] font-mono mb-0.5">
                <span className="text-muted-foreground/60 shrink-0 w-10 text-right">
                  {(e.time / 1000).toFixed(1)}s
                </span>
                <span className={cn("shrink-0 w-28", colors[e.type] ?? "text-muted-foreground")}>
                  {e.type}
                </span>
                {e.iteration !== undefined && (
                  <span className="text-muted-foreground/60">#{e.iteration}</span>
                )}
                <span className="text-foreground/60 truncate">{e.detail}</span>
              </div>
            );
          })}
        </div>
      )}
    </div>
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Main AgentChat component
// ─────────────────────────────────────────────────────────────────────────────

// Parses the agent's conversationStartersJson (a JSON string[]) into a clean,
// de-duplicated list of non-empty example questions shown as clickable chips.
function parseStarters(json?: string): string[] {
  if (!json) return [];
  try {
    const v = JSON.parse(json);
    if (!Array.isArray(v)) return [];
    const seen = new Set<string>();
    const out: string[] = [];
    for (const x of v) {
      if (typeof x !== "string") continue;
      const t = x.trim();
      if (!t || seen.has(t)) continue;
      seen.add(t);
      out.push(t);
      if (out.length >= 8) break;
    }
    return out;
  } catch {
    return [];
  }
}

export function AgentChat() {
  const { id: agentId } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const location = useLocation();
  const [searchParams, setSearchParams] = useSearchParams();
  const agentFromState = (location.state as { agent?: AgentSummary } | null)?.agent;

  // Viewer (non-admin) users get a simplified chat: no LLM config/model picker
  // and no Detailed trace toggle — those are authoring/debug controls.
  const isAdmin = auth.isAdmin();

  // On small screens the header controls collapse into a settings popover so the
  // title + clear button stay visible without horizontal overflow.
  const isMobile = useIsMobile();

  const [agent, setAgent] = useState<AgentSummary | undefined>(agentFromState);
  const [messages, setMessages] = useState<Message[]>([]);
  const [input, setInput] = useState("");
  const [loading, setLoading] = useState(false);
  const [sessionId, setSessionId] = useState<string | undefined>(undefined);
  const [resumedTurns, setResumedTurns] = useState<number | null>(null);
  const [llmConfig, setLlmConfig] = useState<LlmConfig>({ availableModels: [], currentProvider: "", defaultModel: "" });
  const [selectedModel, setSelectedModel] = useState<string>("");
  const [availableLlmConfigs, setAvailableLlmConfigs] = useState<AvailableLlmConfig[]>([]);
  const [selectedConfigId, setSelectedConfigId] = useState<number | undefined>(undefined);
  const [detailedMode, setDetailedMode] = useState(false);
  const [starters, setStarters] = useState<string[]>([]);
  const [credentialGroups, setCredentialGroups] = useState<CredentialGroupOption[]>([]);
  const [selectedGroupId, setSelectedGroupId] = useState<number | undefined>(undefined);

  const [liveIterations, setLiveIterations] = useState<Iteration[]>([]);
  const [liveStatus, setLiveStatus] = useState<string>("");
  const [livePlan, setLivePlan] = useState<{ steps: string[]; revised: boolean } | null>(null);
  const [liveTimeline, setLiveTimeline] = useState<TimelineEntry[]>([]);
  const [liveAnswer, setLiveAnswer] = useState<string>("");

  const bottomRef = useRef<HTMLDivElement>(null);
  const abortRef = useRef<AbortController | null>(null);
  const hydratedRef = useRef<string | null>(null);

  const [listening, setListening] = useState(false);
  const recognitionRef = useRef<SpeechRecognitionLike | null>(null);
  const voiceBaseRef = useRef("");
  const speechSupported =
    typeof window !== "undefined" &&
    ("webkitSpeechRecognition" in window || "SpeechRecognition" in window);

  // Toggles browser speech-to-text, appending the transcript to the input box.
  const toggleVoice = () => {
    if (listening) {
      recognitionRef.current?.stop();
      return;
    }
    const w = window as unknown as {
      webkitSpeechRecognition?: new () => SpeechRecognitionLike;
      SpeechRecognition?: new () => SpeechRecognitionLike;
    };
    const Ctor = w.SpeechRecognition ?? w.webkitSpeechRecognition;
    if (!Ctor) return;
    const rec = new Ctor();
    rec.continuous = true;
    rec.interimResults = true;
    rec.lang = "en-US";
    voiceBaseRef.current = input ? input.trimEnd() + " " : "";
    let finalText = "";
    rec.onresult = (e) => {
      let interim = "";
      for (let i = e.resultIndex; i < e.results.length; i++) {
        const res = e.results[i];
        if (res.isFinal) finalText += res[0].transcript;
        else interim += res[0].transcript;
      }
      setInput(voiceBaseRef.current + finalText + interim);
    };
    rec.onerror = () => setListening(false);
    rec.onend = () => {
      setListening(false);
      recognitionRef.current = null;
    };
    recognitionRef.current = rec;
    rec.start();
    setListening(true);
  };

  // Load agent definition (needed for llmConfigId) and available configs for the tenant
  useEffect(() => {
    if (!agentFromState && agentId) {
      api.getAgent(agentId)
        .then((a) => {
          const agentSummary = { id: a.id!, name: a.name, displayName: a.displayName ?? a.name, isEnabled: a.isEnabled, status: a.status ?? "Draft", agentType: a.agentType ?? "general", createdAt: "", llmConfigId: a.llmConfigId };
          setAgent(agentSummary);
          // Initialize config selector to agent's pinned config
          setSelectedConfigId(a.llmConfigId ?? undefined);
          setStarters(parseStarters(a.conversationStartersJson));
        })
        .catch((e: Error) => toast.error("Failed to load agent", { description: e.message }));
    } else if (agentFromState) {
      setSelectedConfigId(agentFromState.llmConfigId ?? undefined);
      // The navigation state only carries a summary; fetch the definition for starters.
      if (agentId) {
        api.getAgent(agentId)
          .then((a) => setStarters(parseStarters(a.conversationStartersJson)))
          .catch(() => {});
      }
    }
  }, [agentId, agentFromState]);

  // Load all available LLM configs for the tenant (for the config picker)
  useEffect(() => {
    api.listAvailableLlmConfigs().then(setAvailableLlmConfigs).catch(() => {});
  }, []);

  // Load the user groups the caller may pick from to drive shared-MCP credential selection.
  // Only surfaced when the caller belongs to more than one eligible (credential-mapped) group.
  useEffect(() => {
    if (!agentId) return;
    api.getAgentCredentialGroups(agentId)
      .then((r) => setCredentialGroups(r.groups ?? []))
      .catch(() => setCredentialGroups([]));
  }, [agentId]);

  // Re-resolve the LLM config whenever the selected config changes.
  // Uses the resolver endpoint so the model list matches the correct provider.
  useEffect(() => {
    api.getLlmConfig(selectedConfigId).then(setLlmConfig).catch(() => {});
    setSelectedModel(""); // reset model when config changes
  }, [selectedConfigId]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages, liveIterations, liveStatus]);

  // Resume a stored session: when ?sessionId= is present, hydrate the chat with
  // the prior turns and bind subsequent messages to the same session. The backend
  // /continue endpoint has already reactivated the conversation memory so the LLM
  // will replay full context on the next turn.
  useEffect(() => {
    const resumeId = searchParams.get("sessionId");
    if (!resumeId || hydratedRef.current === resumeId) return;
    hydratedRef.current = resumeId;

    api.getSession(resumeId)
      .then(async (detail) => {
        setSessionId(resumeId);
        setResumedTurns(detail.turns?.length ?? 0);
        const msgs = await buildResumedMessages(resumeId, detail.turns ?? []);
        setMessages(msgs);
      })
      .catch((e: Error) => {
        toast.error("Could not load session to resume", { description: e.message });
        hydratedRef.current = null;
        setSearchParams((p) => { p.delete("sessionId"); return p; }, { replace: true });
      });
  }, [searchParams, setSearchParams]);

  const clearChat = () => {
    abortRef.current?.abort();
    hydratedRef.current = null;
    setMessages([]);
    setSessionId(undefined);
    setSelectedGroupId(undefined);
    setResumedTurns(null);
    setLiveIterations([]);
    setLiveTimeline([]);
    setLiveStatus("");
    setLivePlan(null);
    setLiveAnswer("");
    if (searchParams.has("sessionId")) {
      setSearchParams((p) => { p.delete("sessionId"); return p; }, { replace: true });
    }
  };

  const send = async () => {
    const query = input.trim();
    if (!query || loading || !agent) return;
    await sendQuery(query);
  };

  const sendQuery = async (query: string) => {
    if (!query || loading || !agent) return;
    if (listening) recognitionRef.current?.stop();
    setInput("");
    setMessages((m) => [...m, { role: "user", text: query }]);
    setLoading(true);
    setLiveIterations([]);
    setLiveTimeline([]);
    setLiveAnswer("");
    setLiveStatus("Connecting...");

    const abort = new AbortController();
    abortRef.current = abort;

    const itersRef: Iteration[] = [];
    const timelineRef: TimelineEntry[] = [];
    const streamStart = Date.now();
    let pendingMsg: Message | null = null;
    let answerAccum = "";
    // Tracks whether the current iteration streamed token-level text_delta
    // chunks. If not (buffered/non-streaming providers), the `thinking` chunk
    // carries the authoritative full text and we surface it in the live bubble.
    let iterationHadDelta = false;

    const logEvent = (type: string, detail: string, iteration?: number) => {
      timelineRef.push({ time: Date.now() - streamStart, type, iteration, detail });
      setLiveTimeline([...timelineRef]);
    };

    const handleChunk = (chunk: AgentStreamChunk) => {
      switch (chunk.type) {
        case "tools_available":
          logEvent("tools_available", chunk.toolCount ? `${chunk.toolCount} tools: ${(chunk.toolNames ?? []).join(", ")}` : "No tools");
          setLiveStatus(chunk.toolCount ? `${chunk.toolCount} tool${chunk.toolCount > 1 ? "s" : ""} connected` : "No tools connected");
          break;
        case "plan":
          logEvent("plan", (chunk.planSteps ?? []).join(" → "));
          setLivePlan({ steps: chunk.planSteps ?? [], revised: false });
          setLiveStatus("Planning...");
          break;
        case "plan_revised":
          logEvent("plan_revised", (chunk.planSteps ?? []).join(" → "));
          setLivePlan({ steps: chunk.planSteps ?? [], revised: true });
          setLiveStatus("Replanning...");
          break;
        case "iteration_start":
          logEvent("iteration_start", `Iteration ${chunk.iteration}`, chunk.iteration);
          itersRef.push({ number: chunk.iteration!, toolCalls: [] });
          setLiveIterations([...itersRef]);
          iterationHadDelta = false;
          // In Detailed mode each iteration's live text renders under its own
          // iteration block, so reset the bottom bubble per iteration. In the
          // simplified (non-detailed) view the streamed text is the only thing
          // shown, so preserve it across iterations — otherwise prior thinking
          // text is wiped when the next iteration starts (flicker / "comes and
          // goes"). A blank-line separator keeps iterations visually distinct.
          if (detailedMode) {
            answerAccum = "";
            setLiveAnswer("");
          } else if (answerAccum && !answerAccum.endsWith("\n\n")) {
            answerAccum += "\n\n";
            setLiveAnswer(answerAccum);
          }
          setLiveStatus("Analyzing…");
          break;
        case "text_delta": {
          // Accumulate streaming tokens into the live answer bubble. The full
          // per-iteration text is confirmed later by the `thinking` chunk (trace).
          iterationHadDelta = true;
          answerAccum += chunk.content ?? "";
          setLiveAnswer(answerAccum);
          break;
        }
        case "thinking": {
          logEvent("thinking", chunk.content ?? "", chunk.iteration);
          const iter = itersRef.find((i) => i.number === chunk.iteration);
          // Replace with full text from server (confirms accumulated text_delta content)
          if (iter) { iter.thinking = chunk.content; setLiveIterations([...itersRef]); }
          // Non-streaming providers don't emit text_delta — the `thinking` chunk
          // carries the iteration's full text. In the simplified view surface it
          // in the live bubble so the user actually sees the response building up.
          if (!detailedMode && !iterationHadDelta && chunk.content) {
            answerAccum += chunk.content;
            setLiveAnswer(answerAccum);
          }
          break;
        }
        case "tool_call": {
          logEvent("tool_call", `${chunk.toolName}(${(chunk.toolInput ?? "").slice(0, 120)})`, chunk.iteration);
          const iter = itersRef.find((i) => i.number === chunk.iteration);
          if (iter) {
            iter.toolCalls.push({ name: chunk.toolName!, input: chunk.toolInput ?? "" });
            setLiveIterations([...itersRef]);
            setLiveStatus(`Calling ${chunk.toolName}...`);
          }
          break;
        }
        case "tool_result": {
          logEvent("tool_result", `${chunk.toolName} → ${(chunk.toolOutput ?? "").slice(0, 120)}`, chunk.iteration);
          const iter = itersRef.find((i) => i.number === chunk.iteration);
          if (iter) {
            const call = [...iter.toolCalls].reverse().find((c) => c.name === chunk.toolName && c.output === undefined);
            if (call) { call.output = chunk.toolOutput; setLiveIterations([...itersRef]); }
          }
          setLiveStatus("Reviewing results…");
          break;
        }
        case "final_response":
          logEvent("final_response", `${(chunk.content ?? "").slice(0, 100)}...`);
          if (chunk.sessionId) setSessionId(chunk.sessionId);
          pendingMsg = {
            role: "agent",
            text: chunk.content ?? "(no response)",
            iterations: itersRef.length > 0 ? [...itersRef] : undefined,
            toolsUsed: itersRef.flatMap((i) => i.toolCalls.map((t) => t.name)),
          };
          setLiveStatus("Verifying...");
          break;
        case "verification":
          logEvent("verification", chunk.verification?.isVerified ? `Verified (${Math.round((chunk.verification.confidence ?? 0) * 100)}%)` : "Unverified");
          if (pendingMsg) pendingMsg.verification = chunk.verification;
          break;
        case "rule_suggestion":
          logEvent("rule_suggestion", `${chunk.followUpQuestions?.length ?? 0} suggestion(s)`);
          if (pendingMsg) pendingMsg.followUpQuestions = chunk.followUpQuestions;
          break;
        case "continuation_start":
          logEvent("continuation_start", `Window ${chunk.continuationWindow}`);
          setLiveStatus(`Continuing (window ${chunk.continuationWindow})...`);
          break;
        case "correction":
          logEvent("correction", chunk.content ?? "Self-correcting");
          setLiveStatus("Self-correcting...");
          break;
        case "hook_executed": {
          const parts = [chunk.hookName ?? "hook"];
          if (chunk.rulePackTriggeredCount !== undefined)
            parts.push(`triggered=${chunk.rulePackTriggeredCount}`);
          if (chunk.rulePackFilteredCount !== undefined)
            parts.push(`filtered=${chunk.rulePackFilteredCount}`);
          if (chunk.rulePackErrorAction)
            parts.push(`errorAction=${chunk.rulePackErrorAction}`);
          if (chunk.rulePackBlocked)
            parts.push("blocked=true");
          if ((chunk.rulePackTriggeredRules ?? []).length > 0)
            parts.push((chunk.rulePackTriggeredRules ?? []).join(", "));

          logEvent("hook_executed", parts.join(" | "), chunk.iteration);
          break;
        }
        case "error":
          logEvent("error", chunk.errorMessage ?? "Unknown error");
          pendingMsg = { role: "agent", text: chunk.errorMessage ?? "Unknown error", error: true };
          break;
        case "done":
          logEvent("done", chunk.executionTime ? `Completed in ${chunk.executionTime}` : "Done");
          if (chunk.sessionId) setSessionId(chunk.sessionId);
          if (pendingMsg) {
            if (pendingMsg.toolsUsed?.length === 0) delete pendingMsg.toolsUsed;
            pendingMsg.executionTime = chunk.executionTime;
          }
          break;
      }
    };

    try {
      await api.streamAgent(agent.id, query, sessionId, handleChunk, abort.signal, selectedModel || undefined, selectedConfigId, true, selectedGroupId);
      if (pendingMsg) {
        setMessages((m) => [...m, pendingMsg!]);
      }
    } catch (e: unknown) {
      if ((e as Error).name !== "AbortError") {
        setMessages((m) => [...m, { role: "agent", text: String(e), error: true }]);
      }
    } finally {
      setLoading(false);
      setLiveIterations([]);
      setLiveStatus("");
      setLivePlan(null);
      setLiveAnswer("");
    }
  };

  const renderContent = (message: Message) => {
    if (message.role !== "agent" || message.error) return message.text;
    return <MarkdownMessage content={message.text} />;
  };

  // Header controls (credential group + admin config/model/detailed toggle).
  // Rendered inline on desktop and inside a settings popover on mobile.
  const hasHeaderControls = credentialGroups.length > 1 || isAdmin;
  const headerControls = (
    <>
      {/* Credential group picker — shown to all users when the caller belongs to more than
          one eligible user group, letting them choose which group's shared-MCP credentials
          this chat uses. */}
      {credentialGroups.length > 1 && (
        <Select
          value={selectedGroupId?.toString() ?? "__default__"}
          onValueChange={(v) => setSelectedGroupId(v === "__default__" ? undefined : parseInt(v))}
          disabled={loading || messages.length > 0}
        >
          <SelectTrigger
            className="w-full md:w-48 h-8 text-xs"
            title={messages.length > 0
              ? "Credential group is locked for this session. Clear the chat to change it."
              : "Credential group for shared MCP tools"}
          >
            <SelectValue placeholder="Credential group" />
          </SelectTrigger>
          <SelectContent>
            <SelectItem value="__default__" className="text-xs">Default credential group</SelectItem>
            {credentialGroups.map((g) => (
              <SelectItem key={g.id} value={g.id.toString()} className="text-xs">{g.name}</SelectItem>
            ))}
          </SelectContent>
        </Select>
      )}
      {/* LLM config/model pickers and Detailed toggle are admin-only. */}
      {isAdmin && (
        <>
          {/* LLM Config picker — lets user test agent against different providers */}
          {availableLlmConfigs.length > 0 && (
            <Select
              value={selectedConfigId?.toString() ?? "__default__"}
              onValueChange={(v) => setSelectedConfigId(v === "__default__" ? undefined : parseInt(v))}
              disabled={loading}
            >
              <SelectTrigger className="w-full md:w-44 h-8 text-xs">
                <SelectValue placeholder="Config" />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="__default__" className="text-xs">Platform default</SelectItem>
                {availableLlmConfigs.map((c) => (
                  <SelectItem key={c.id} value={c.id.toString()} className="text-xs">
                    {c.displayName}{c.isRef ? " · via Platform" : ""}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          )}
          <Select value={selectedModel || "__default__"} onValueChange={(v) => setSelectedModel(v === "__default__" ? "" : v)} disabled={loading}>
            <SelectTrigger className="w-full md:w-52 h-8 text-xs">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="__default__">
                {llmConfig.defaultModel
                  ? `Default (${llmConfig.defaultModel})`
                  : "Default model"}
              </SelectItem>
              {llmConfig.availableModels.map((m) => (
                <SelectItem key={m} value={m} className="text-xs">{m}</SelectItem>
              ))}
            </SelectContent>
          </Select>
          <div className="flex items-center gap-1.5">
            <Switch id="detailed" checked={detailedMode} onCheckedChange={setDetailedMode} />
            <Label htmlFor="detailed" className="text-xs cursor-pointer">Detailed</Label>
          </div>
        </>
      )}
    </>
  );

  return (
    <div className="flex flex-col h-[calc(100dvh-7rem)] md:h-[calc(100dvh-8rem)]">
      {/* Header */}
      <div className="flex items-center gap-3 mb-4 shrink-0">
        <Button variant="ghost" size="icon" onClick={() => navigate("/agents")}>
          <ArrowLeft className="size-4" />
        </Button>
        <div className="flex-1 min-w-0">
          <h1 className="text-lg font-semibold truncate">
            {agent ? (agent.displayName || agent.name) : "Loading..."}
          </h1>
          <p className="text-xs text-muted-foreground">
            {sessionId ? `Session: ${sessionId.slice(0, 8)}...` : "No active session"}
          </p>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {hasHeaderControls && (
            isMobile ? (
              <Popover>
                <PopoverTrigger asChild>
                  <Button variant="ghost" size="icon" title="Chat settings" aria-label="Chat settings">
                    <SlidersHorizontal className="size-4" />
                  </Button>
                </PopoverTrigger>
                <PopoverContent align="end" className="w-72 flex flex-col gap-3">
                  {headerControls}
                </PopoverContent>
              </Popover>
            ) : (
              <div className="flex items-center gap-3">{headerControls}</div>
            )
          )}
          <Button variant="ghost" size="icon" onClick={clearChat} title="Clear chat">
            <RotateCcw className="size-4" />
          </Button>
        </div>
      </div>


      <Separator className="mb-4 shrink-0" />

      {/* Messages */}
      <ScrollArea className="flex-1 pr-4">
        <div className="space-y-6 pb-4">
          {resumedTurns !== null && (
            <div className="flex items-center gap-2 rounded-md border border-sky-500/30 bg-sky-500/5 px-3 py-2 text-xs text-muted-foreground">
              <History className="size-3.5 text-sky-400 shrink-0" />
              <span>
                Resumed session{resumedTurns > 0 ? ` — ${resumedTurns} prior turn${resumedTurns !== 1 ? "s" : ""} loaded` : ""}.
                New messages continue this conversation. Use <span className="font-medium text-foreground">Clear</span> to start fresh.
              </span>
            </div>
          )}

          {messages.length === 0 && !loading && (
            <div className="flex flex-col items-center justify-center pt-20 gap-3">
              <div className="rounded-full bg-muted p-4">
                <Bot className="size-8 text-muted-foreground" />
              </div>
              <p className="text-sm text-muted-foreground">Send a message to start the conversation</p>
              {starters.length > 0 && (
                <div className="mt-2 flex flex-wrap justify-center gap-2 max-w-2xl px-4">
                  {starters.map((s, i) => (
                    <button
                      key={i}
                      type="button"
                      onClick={() => { void sendQuery(s); }}
                      className="rounded-full border border-border bg-background px-3.5 py-1.5 text-sm text-foreground/80 transition-colors hover:bg-muted hover:text-foreground"
                    >
                      {s}
                    </button>
                  ))}
                </div>
              )}
            </div>
          )}

          {messages.map((m, i) => (
            <div key={i} className={cn("flex gap-2.5", m.role === "user" ? "flex-row-reverse" : "flex-row")}>
              <Avatar className="size-7 shrink-0 mt-0.5">
                <AvatarFallback className={m.role === "user" ? "bg-primary text-primary-foreground" : "bg-primary/10"}>
                  {m.role === "user"
                    ? <User className="size-3.5" />
                    : <Bot className="size-3.5 text-primary" />}
                </AvatarFallback>
              </Avatar>

              <div className={cn("flex flex-col max-w-[85%] min-w-0", m.role === "user" ? "items-end" : "items-start")}>
                {/* Bubble */}
                <div className={cn(
                  "rounded-2xl px-4 py-3 text-sm leading-relaxed min-w-0 max-w-full",
                  m.role === "user"
                    ? "rounded-tr-sm bg-primary text-primary-foreground whitespace-pre-wrap"
                    : m.error
                      ? "rounded-tl-sm bg-destructive/10 text-destructive border border-destructive/20 whitespace-pre-wrap"
                      : "rounded-tl-sm bg-muted"
                )}>
                  {renderContent(m)}
                </div>

                {/* Meta */}
                {m.role === "agent" && !m.error && (
                  <div className="flex flex-wrap items-center gap-1.5 mt-1.5">
                    {m.toolsUsed && m.toolsUsed.length > 0 && m.toolsUsed.map((t) => (
                      <Badge key={t} variant="secondary" className="text-[10px] h-4 px-1.5 font-mono">
                        <Wrench className="size-2.5 mr-1" />{t}
                      </Badge>
                    ))}
                    {m.executionTime && (
                      <span className="text-[10px] text-muted-foreground">{m.executionTime}</span>
                    )}
                    {m.verification && <VerificationBadge v={m.verification} />}
                  </div>
                )}

                {/* Iteration trace (tool call details) — only in Detailed mode */}
                {m.role === "agent" && m.iterations && m.iterations.length > 0 && detailedMode && (
                  <IterationTrace iterations={m.iterations} detailed={detailedMode} />
                )}

                {/* Rule suggestions */}
                {m.role === "agent" && m.followUpQuestions?.filter((q) => q.type === "rule_confirmation").map((q, qi) => (
                  <div key={qi} className="mt-2 rounded-xl border border-indigo-500/30 bg-indigo-500/5 p-3 max-w-sm space-y-2">
                    <p className="text-xs text-indigo-400">{q.text}</p>
                    <div className="flex flex-wrap gap-1.5">
                      {q.options.map((opt) => (
                        <Button
                          key={opt}
                          variant="outline"
                          size="sm"
                          className="h-6 text-xs border-indigo-500/30"
                          onClick={() => setInput(opt)}
                        >
                          {opt}
                        </Button>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            </div>
          ))}

          {/* Live feed */}
          {loading && (
            <LiveFeed
              iterations={liveIterations}
              status={liveStatus}
              plan={livePlan}
              timeline={liveTimeline}
              detailed={detailedMode}
              answer={liveAnswer}
            />
          )}

          <div ref={bottomRef} />
        </div>
      </ScrollArea>

      {/* Input */}
      <div className="mt-4 shrink-0">
        <Separator className="mb-4" />
        <div className="flex gap-3 items-end">
          <Textarea
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                send();
              }
            }}
            placeholder="Type a message... (Enter to send, Shift+Enter for new line)"
            disabled={loading}
            rows={3}
            className="resize-none flex-1"
          />
          <div className="flex flex-col gap-2 shrink-0">
            {speechSupported && (
              <Button
                onClick={toggleVoice}
                disabled={loading}
                size="icon"
                variant={listening ? "default" : "outline"}
                className={cn("w-11", listening && "animate-pulse bg-red-600 hover:bg-red-600")}
                title={listening ? "Stop dictation" : "Voice input"}
                aria-label={listening ? "Stop dictation" : "Voice input"}
              >
                <Mic className="size-4" />
              </Button>
            )}
            <Button
              onClick={send}
              disabled={loading || !input.trim() || !agent}
              size="icon"
              className={cn("w-11 shrink-0", speechSupported ? "h-11 flex-1" : "h-[88px]")}
            >
              <Send className="size-4" />
            </Button>
          </div>
        </div>
        <p className="text-[11px] text-muted-foreground mt-1.5">
          Enter to send · Shift+Enter for new line
          {loading && " · "}
          {loading && (
            <button
              className="underline text-muted-foreground hover:text-foreground"
              onClick={() => abortRef.current?.abort()}
            >
              Cancel
            </button>
          )}
        </p>
      </div>
    </div>
  );
}
