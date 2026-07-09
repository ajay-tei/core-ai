import { useState, type ReactNode } from "react";
import { Check, Copy } from "lucide-react";
import { cn } from "@/lib/utils";

// ─────────────────────────────────────────────────────────────────────────────
// Minimal, dependency-free SQL highlighter. Deliberately hand-rolled to avoid
// pulling a full syntax-highlighting library (and its bundle cost) into the
// admin portal. Tokenises on keywords, strings, numbers, and comments.
// ─────────────────────────────────────────────────────────────────────────────

const KEYWORDS = new Set([
  "select", "from", "where", "and", "or", "not", "in", "is", "null", "as",
  "join", "inner", "left", "right", "outer", "full", "cross", "on", "using",
  "group", "by", "order", "having", "limit", "offset", "top", "distinct",
  "insert", "into", "values", "update", "set", "delete", "create", "table",
  "alter", "drop", "index", "view", "with", "union", "all", "case", "when",
  "then", "else", "end", "between", "like", "asc", "desc", "count", "sum",
  "avg", "min", "max", "over", "partition", "cast", "convert", "coalesce",
  "exists", "any", "some", "inner", "primary", "key", "foreign", "references",
]);

interface Token {
  text: string;
  cls: string;
}

function tokenize(sql: string): Token[] {
  const tokens: Token[] = [];
  // Match: line comments, block comments, single-quoted strings, [bracketed]
  // identifiers, numbers, words, whitespace, and any other single char.
  const re =
    /(--[^\n]*|\/\*[\s\S]*?\*\/)|('(?:[^']|'')*')|(\[[^\]]*\])|(\b\d+(?:\.\d+)?\b)|(\b\w+\b)|(\s+)|([^\s\w])/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(sql)) !== null) {
    if (m[1] !== undefined) tokens.push({ text: m[1], cls: "text-muted-foreground italic" });
    else if (m[2] !== undefined) tokens.push({ text: m[2], cls: "text-emerald-400" });
    else if (m[3] !== undefined) tokens.push({ text: m[3], cls: "text-sky-300" });
    else if (m[4] !== undefined) tokens.push({ text: m[4], cls: "text-amber-400" });
    else if (m[5] !== undefined)
      tokens.push({
        text: m[5],
        cls: KEYWORDS.has(m[5].toLowerCase()) ? "text-indigo-400 font-semibold" : "text-foreground/90",
      });
    else if (m[6] !== undefined) tokens.push({ text: m[6], cls: "" });
    else tokens.push({ text: m[7] ?? "", cls: "text-fuchsia-400" });
  }
  return tokens;
}

export function SqlBlock({ sql, className }: { sql: string; className?: string }) {
  const [copied, setCopied] = useState(false);
  const trimmed = sql.trim();

  const copy = async () => {
    try {
      await navigator.clipboard.writeText(trimmed);
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    } catch {
      /* clipboard unavailable */
    }
  };

  const nodes: ReactNode[] = tokenize(trimmed).map((t, i) =>
    t.cls ? (
      <span key={i} className={t.cls}>
        {t.text}
      </span>
    ) : (
      <span key={i}>{t.text}</span>
    ),
  );

  return (
    <div className={cn("group relative my-2 rounded-md border bg-background/60", className)}>
      <div className="flex items-center justify-between border-b px-2.5 py-1">
        <span className="text-[10px] font-medium uppercase tracking-wide text-muted-foreground">SQL</span>
        <button
          onClick={copy}
          className="flex items-center gap-1 text-[10px] text-muted-foreground opacity-0 transition-opacity hover:text-foreground group-hover:opacity-100"
          title="Copy SQL"
        >
          {copied ? <Check className="size-3 text-emerald-400" /> : <Copy className="size-3" />}
          {copied ? "Copied" : "Copy"}
        </button>
      </div>
      <pre className="overflow-x-auto p-2.5 text-[12px] leading-relaxed font-mono">
        <code>{nodes}</code>
      </pre>
    </div>
  );
}
