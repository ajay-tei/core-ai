import { useEffect, useState } from "react";
import { useSearchParams } from "react-router";
import {
  getSchedulerFeedbackContext,
  submitSchedulerFeedback,
  type SchedulerFeedbackContext,
} from "../api";
import { Button } from "./ui/button";
import { Textarea } from "./ui/textarea";
import { Input } from "./ui/input";
import { Label } from "./ui/label";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "./ui/card";
import { Badge } from "./ui/badge";
import { Shield } from "lucide-react";
import { APP_NAME } from "@/lib/brand";

const CATEGORIES = ["Accuracy", "Completeness", "Tone", "Format", "Other"];

export function SchedulerFeedbackPage() {
  const [searchParams] = useSearchParams();
  const token = searchParams.get("token") ?? "";

  const [context, setContext] = useState<SchedulerFeedbackContext | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [submitted, setSubmitted] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  // Form state
  const [thumbs, setThumbs] = useState<1 | -1 | null>(null);
  const [stars, setStars] = useState<number | null>(null);
  const [category, setCategory] = useState("");
  const [correctionText, setCorrectionText] = useState("");
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");

  useEffect(() => {
    if (!token) {
      setError("No feedback token found in the link. Please use the link from your notification email.");
      setLoading(false);
      return;
    }
    getSchedulerFeedbackContext(token)
      .then(setContext)
      .catch((err: unknown) => {
        const msg = (err as { error?: string })?.error;
        setError(msg ?? "This feedback link is invalid or has expired.");
      })
      .finally(() => setLoading(false));
  }, [token]);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    if (thumbs === null && stars === null && !correctionText.trim()) {
      setError("Please provide at least one piece of feedback.");
      return;
    }
    setSubmitting(true);
    setError(null);
    try {
      await submitSchedulerFeedback({
        token,
        thumbsRating: thumbs ?? undefined,
        starRating: stars ?? undefined,
        category: category || undefined,
        correctionText: correctionText.trim() || undefined,
        submitterName: name.trim() || undefined,
        submitterEmail: email.trim() || undefined,
      });
      setSubmitted(true);
    } catch (err: unknown) {
      const msg = (err as { error?: string })?.error ?? "Submission failed. Please try again.";
      setError(msg);
    } finally {
      setSubmitting(false);
    }
  }

  if (loading) {
    return (
      <FeedbackShell>
        <div className="flex items-center justify-center py-20">
          <p className="text-muted-foreground text-sm">Loading feedback form…</p>
      </div>
      </FeedbackShell>
    );
  }

  if (submitted) {
    return (
      <FeedbackShell>
        <Card className="w-full max-w-md text-center mx-auto">
          <CardHeader>
            <div className="text-4xl mb-2">✅</div>
            <CardTitle>Thank you!</CardTitle>
            <CardDescription>Your feedback has been submitted and will be reviewed by the admin team.</CardDescription>
          </CardHeader>
        </Card>
      </FeedbackShell>
    );
  }

  if (error && !context) {
    return (
      <FeedbackShell>
        <Card className="w-full max-w-md text-center mx-auto">
          <CardHeader>
            <div className="text-4xl mb-2">⚠️</div>
            <CardTitle>Link Unavailable</CardTitle>
            <CardDescription>{error}</CardDescription>
          </CardHeader>
        </Card>
      </FeedbackShell>
    );
  }

  return (
    <FeedbackShell>
      <Card className="w-full max-w-xl">
        <CardHeader>
          <CardTitle>Scheduler Run Feedback</CardTitle>
          {context && (
            <CardDescription>
              Provide feedback on the AI response for the scheduled task below.
            </CardDescription>
          )}
        </CardHeader>
        <CardContent className="space-y-6">
          {/* Run context summary */}
          {context && (
            <div className="rounded-lg border bg-muted/40 p-4 space-y-1 text-sm">
              <div className="flex items-center gap-2 flex-wrap">
                <span className="font-semibold">{context.taskName}</span>
                {context.taskType === "group" && (
                  <Badge variant="secondary">Group Task</Badge>
                )}
              </div>
              {context.agentDisplayName && (
                <p className="text-muted-foreground">Agent: {context.agentDisplayName}</p>
              )}
              {context.runCompletedAt && (
                <p className="text-muted-foreground text-xs">
                  Completed: {new Date(context.runCompletedAt).toLocaleString()}
                </p>
              )}
              {context.runOutcome && (
                <p className="text-xs">
                  Status:{" "}
                  <span className={context.runOutcome === "success" ? "text-green-600" : "text-red-600"}>
                    {context.runOutcome}
                  </span>
                </p>
              )}
            </div>
          )}

          <form onSubmit={handleSubmit} className="space-y-5">
            {/* Thumbs */}
            <div className="space-y-1">
              <Label>Was this response helpful?</Label>
              <div className="flex gap-2 mt-1">
                <button
                  type="button"
                  onClick={() => setThumbs(thumbs === 1 ? null : 1)}
                  className={`px-4 py-2 rounded-md border text-sm font-medium transition-colors ${
                    thumbs === 1
                      ? "bg-green-600 text-white border-green-600"
                      : "bg-background text-foreground border-input hover:bg-muted"
                  }`}
                >
                  👍 Yes
                </button>
                <button
                  type="button"
                  onClick={() => setThumbs(thumbs === -1 ? null : -1)}
                  className={`px-4 py-2 rounded-md border text-sm font-medium transition-colors ${
                    thumbs === -1
                      ? "bg-red-600 text-white border-red-600"
                      : "bg-background text-foreground border-input hover:bg-muted"
                  }`}
                >
                  👎 No
                </button>
              </div>
            </div>

            {/* Star rating */}
            <div className="space-y-1">
              <Label>Rating (optional)</Label>
              <div className="flex gap-1 mt-1">
                {[1, 2, 3, 4, 5].map((s) => (
                  <button
                    key={s}
                    type="button"
                    onClick={() => setStars(stars === s ? null : s)}
                    className={`text-2xl transition-transform hover:scale-110 ${
                      stars !== null && s <= stars ? "text-yellow-400" : "text-muted-foreground/30"
                    }`}
                  >
                    ★
                  </button>
                ))}
              </div>
            </div>

            {/* Category */}
            <div className="space-y-1">
              <Label>Category (optional)</Label>
              <div className="flex gap-2 flex-wrap mt-1">
                {CATEGORIES.map((c) => (
                  <button
                    key={c}
                    type="button"
                    onClick={() => setCategory(category === c ? "" : c)}
                    className={`px-3 py-1 rounded-full text-xs border transition-colors ${
                      category === c
                        ? "bg-primary text-primary-foreground border-primary"
                        : "bg-background text-muted-foreground border-input hover:bg-muted"
                    }`}
                  >
                    {c}
                  </button>
                ))}
              </div>
            </div>

            {/* Correction / comment */}
            <div className="space-y-1">
              <Label htmlFor="correction">Correction or comment (optional)</Label>
              <Textarea
                id="correction"
                rows={4}
                placeholder="What should the response have said? Or any other feedback…"
                value={correctionText}
                onChange={(e) => setCorrectionText(e.target.value)}
              />
            </div>

            {/* Submitter identity (optional) */}
            <div className="grid grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label htmlFor="name">Your name (optional)</Label>
                <Input
                  id="name"
                  placeholder="Jane Smith"
                  value={name}
                  onChange={(e) => setName(e.target.value)}
                />
              </div>
              <div className="space-y-1">
                <Label htmlFor="email">Email (optional)</Label>
                <Input
                  id="email"
                  type="email"
                  placeholder="jane@example.com"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                />
              </div>
            </div>

            {error && (
              <p className="text-sm text-red-600">{error}</p>
            )}

            <Button type="submit" disabled={submitting} className="w-full">
              {submitting ? "Submitting…" : "Submit Feedback"}
            </Button>
          </form>
        </CardContent>
      </Card>
    </FeedbackShell>
  );
}

function FeedbackShell({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen bg-background flex flex-col">
      {/* Header */}
      <header className="border-b bg-background/95 backdrop-blur supports-[backdrop-filter]:bg-background/60">
        <div className="mx-auto max-w-2xl px-4 py-3 flex items-center gap-2">
          <Shield className="size-6 text-primary" />
          <span className="font-semibold tracking-tight">{APP_NAME}</span>
        </div>
      </header>

      {/* Page content */}
      <main className="flex-1 flex flex-col items-center justify-start p-4 pt-10">
        <div className="w-full max-w-xl">
          {children}
        </div>
      </main>

      {/* Footer */}
      <footer className="border-t">
        <div className="mx-auto max-w-2xl px-4 py-3 text-center text-xs text-muted-foreground">
          © {new Date().getFullYear()} {APP_NAME}. All rights reserved.
        </div>
      </footer>
    </div>
  );
}
