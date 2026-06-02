import { useRef, useState } from "react";
import { Wand2, X } from "lucide-react";
import { Dialog as DialogPrimitive } from "radix-ui";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/textarea";
import { Label } from "@/components/ui/label";
import { DialogOverlay, DialogPortal } from "@/components/ui/dialog";

interface Props {
  /** Async function that calls the backend to improve the prompt. Receives the instruction and returns the improved prompt string. */
  onImprove: (instruction: string) => Promise<string>;
  currentPrompt: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  onAccept: (improvedPrompt: string) => void;
}

type Phase = "input" | "loading" | "preview";

export function PromptQuickFixDialog({ onImprove, currentPrompt, open, onOpenChange, onAccept }: Props) {
  const [phase, setPhase]             = useState<Phase>("input");
  const [instruction, setInstruction] = useState("");
  const [improved, setImproved]       = useState("");
  const [error, setError]             = useState<string | null>(null);
  const instructionRef = useRef<HTMLTextAreaElement>(null);

  function handleOpenChange(next: boolean) {
    if (!next) reset();
    onOpenChange(next);
  }

  function reset() {
    setPhase("input");
    setInstruction("");
    setImproved("");
    setError(null);
  }

  async function generate() {
    const trimmed = instruction.trim();
    if (!trimmed) return;
    setError(null);
    setPhase("loading");
    try {
      const improvedPrompt = await onImprove(trimmed);
      setImproved(improvedPrompt);
      setPhase("preview");
    } catch (e: unknown) {
      let msg = "LLM call failed — check agent API key configuration.";
      if (e instanceof Error) {
        const jsonMatch = e.message.match(/\{[\s\S]*\}/);
        if (jsonMatch) {
          try { msg = JSON.parse(jsonMatch[0])?.error ?? e.message; } catch { msg = e.message; }
        } else {
          msg = e.message;
        }
      } else {
        msg = (e as { error?: string })?.error ?? msg;
      }
      setError(msg);
      setPhase("input");
    }
  }

  function accept() {
    onAccept(improved);
    handleOpenChange(false);
  }

  const origLen     = currentPrompt.length;
  const improvedLen = improved.length;
  const delta       = improvedLen - origLen;
  const deltaLabel  = delta === 0 ? "no length change"
    : delta > 0 ? `+${delta} chars`
    : `${delta} chars`;

  return (
    <DialogPrimitive.Root open={open} onOpenChange={handleOpenChange}>
      <DialogPortal>
        <DialogOverlay />
        <DialogPrimitive.Content
          className="bg-background"
          style={{
            position: "fixed",
            top: "50%",
            left: "50%",
            transform: "translate(-50%, -50%)",
            width: "95vw",
            height: "90vh",
            maxWidth: "none",
            maxHeight: "none",
            zIndex: 50,
            opacity: 1,
            display: "flex",
            flexDirection: "column",
            overflow: "hidden",
            borderRadius: "0.75rem",
            border: "1px solid hsl(var(--border))",
            boxShadow: "0 25px 50px -12px rgba(0,0,0,0.25)",
            outline: "none",
          }}
        >
          {/* Close button */}
          <DialogPrimitive.Close
            style={{ position: "absolute", top: "1rem", right: "1rem", zIndex: 10 }}
            className="rounded-sm opacity-70 hover:opacity-100 transition-opacity"
          >
            <X className="size-4" />
            <span className="sr-only">Close</span>
          </DialogPrimitive.Close>

          {/* Header */}
          <div className="px-6 pt-5 pb-3 border-b shrink-0">
            <h2 className="text-lg font-semibold flex items-center gap-2">
              <Wand2 className="size-4 text-violet-500" />
              Quick Prompt Fix
            </h2>
            <p className="text-sm text-muted-foreground mt-1">
              Describe what to change and the AI will revise the system prompt accordingly.
              Accept to apply the result to the editor — you can still tweak it before saving.
            </p>
          </div>

          {/* Body */}
          <div className="flex-1 min-h-0 flex flex-col px-6 py-4 gap-4 overflow-hidden">
            {error && (
              <div className="rounded-md border border-destructive/40 bg-destructive/10 text-destructive px-3 py-2 text-sm shrink-0">
                {error}
              </div>
            )}

            <div className="flex-1 min-h-0 grid grid-cols-2 gap-4">
              {/* Left: instruction + current prompt */}
              <div className="flex flex-col gap-3 min-h-0">
                <div className="space-y-1.5 shrink-0">
                  <Label>Your instruction</Label>
                  <Textarea
                    ref={instructionRef}
                    value={instruction}
                    onChange={e => setInstruction(e.target.value)}
                    placeholder='e.g. "Make it more concise", "Add a section on tone", "Handle edge cases more explicitly"'
                    rows={4}
                    className="resize-none text-sm"
                    disabled={phase === "loading"}
                    onKeyDown={e => {
                      if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) void generate();
                    }}
                    autoFocus
                  />
                  <p className="text-[11px] text-muted-foreground">⌘↵ / Ctrl+↵ to generate</p>
                </div>

                <div className="flex flex-col flex-1 min-h-0 space-y-1.5">
                  <Label className="text-xs text-muted-foreground shrink-0">Current prompt</Label>
                  <div className="flex-1 min-h-0 rounded-md border bg-muted/40 p-2.5 overflow-y-auto">
                    <pre className="text-[11px] whitespace-pre-wrap break-words font-mono leading-relaxed text-muted-foreground">
                      {currentPrompt || "(empty)"}
                    </pre>
                  </div>
                </div>
              </div>

              {/* Right: revised prompt */}
              <div className="flex flex-col min-h-0 gap-1.5">
                <div className="flex items-center justify-between shrink-0">
                  <Label className="text-xs text-muted-foreground">
                    {phase === "preview" ? `Revised prompt (${deltaLabel})` : "Revised prompt"}
                  </Label>
                  {phase === "preview" && (
                    <span className="text-[11px] text-muted-foreground">
                      {origLen} → {improvedLen} chars
                    </span>
                  )}
                </div>

                {phase === "loading" ? (
                  <div className="flex-1 rounded-md border bg-muted/40 flex items-center justify-center gap-2 text-sm text-muted-foreground">
                    <span className="inline-block h-3 w-3 rounded-full bg-primary animate-pulse" />
                    Generating…
                  </div>
                ) : phase === "preview" ? (
                  <Textarea
                    value={improved}
                    onChange={e => setImproved(e.target.value)}
                    className="flex-1 font-mono text-xs resize-none min-h-0"
                  />
                ) : (
                  <div className="flex-1 rounded-md border border-dashed bg-muted/20 flex items-center justify-center text-xs text-muted-foreground">
                    Revised prompt appears here after generation
                  </div>
                )}
              </div>
            </div>
          </div>

          {/* Footer */}
          <div className="px-6 py-4 border-t shrink-0 flex items-center justify-between gap-2">
            <div>
              {phase === "preview" && (
                <Button variant="outline" size="sm"
                  onClick={() => { setPhase("input"); setImproved(""); }}
                >
                  Try Again
                </Button>
              )}
            </div>
            <div className="flex gap-2">
              <Button variant="ghost" size="sm" onClick={() => handleOpenChange(false)}>
                Cancel
              </Button>
              {phase !== "preview" ? (
                <Button size="sm"
                  onClick={() => void generate()}
                  disabled={phase === "loading" || !instruction.trim()}
                >
                  <Wand2 className="size-3.5 mr-1.5" />
                  {phase === "loading" ? "Generating…" : "Generate"}
                </Button>
              ) : (
                <Button size="sm" onClick={accept}>
                  Accept & Apply
                </Button>
              )}
            </div>
          </div>
        </DialogPrimitive.Content>
      </DialogPortal>
    </DialogPrimitive.Root>
  );
}
