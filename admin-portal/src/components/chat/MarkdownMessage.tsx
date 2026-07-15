import { memo, isValidElement, type ReactNode } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { SqlBlock } from "./SqlBlock";
import { ChartRenderer, parseChartSpec } from "./ChartRenderer";
import { cn } from "@/lib/utils";

// ─────────────────────────────────────────────────────────────────────────────
// Renders an agent answer as GitHub-flavoured Markdown. react-markdown does not
// emit raw HTML (no rehype-raw), so the output is XSS-safe by construction.
//
// Code fences are routed by language:
//   ```sql    → SqlBlock  (syntax-highlighted, copyable)
//   ```chart  → ChartRenderer (recharts, from a JSON spec)
// GFM tables render through the shadcn Table primitives.
// ─────────────────────────────────────────────────────────────────────────────

function codeClassName(child: ReactNode): string {
  if (isValidElement(child)) {
    const props = child.props as { className?: string } | undefined;
    return props?.className ?? "";
  }
  return "";
}

const components: Components = {
  table: ({ children }) => (
    <div className="my-2 rounded-md border">
      <Table>{children}</Table>
    </div>
  ),
  thead: ({ children }) => <TableHeader>{children}</TableHeader>,
  tbody: ({ children }) => <TableBody>{children}</TableBody>,
  tr: ({ children }) => <TableRow>{children}</TableRow>,
  th: ({ children }) => <TableHead className="h-8 px-2 text-[12px]">{children}</TableHead>,
  td: ({ children }) => <TableCell className="px-2 py-1.5 text-[12px]">{children}</TableCell>,

  // Pass through the container for special blocks (SqlBlock/ChartRenderer render
  // their own wrapper); style generic code blocks.
  pre: ({ children }) => {
    const cls = codeClassName(Array.isArray(children) ? children[0] : children);
    if (/language-(sql|chart)/.test(cls)) return <>{children}</>;
    return (
      <pre className="my-2 overflow-x-auto rounded-md border bg-background/60 p-2.5 text-[12px] leading-relaxed font-mono">
        {children}
      </pre>
    );
  },
  code: ({ className, children }) => {
    const match = /language-(\w+)/.exec(className ?? "");
    const lang = match?.[1]?.toLowerCase();
    const text = String(children).replace(/\n$/, "");

    if (lang === "sql") return <SqlBlock sql={text} />;
    if (lang === "chart") {
      const spec = parseChartSpec(text);
      if (spec) return <ChartRenderer spec={spec} />;
    }
    if (!className) {
      return (
        <code className="rounded bg-muted px-1 py-0.5 text-[0.85em] font-mono">{children}</code>
      );
    }
    return <code className="font-mono">{children}</code>;
  },

  a: ({ href, children }) => (
    <a
      href={href}
      target="_blank"
      rel="noopener noreferrer"
      className="text-primary underline underline-offset-2 hover:text-primary/80"
    >
      {children}
    </a>
  ),
  h1: ({ children }) => <h1 className="mb-2 mt-3 text-base font-semibold">{children}</h1>,
  h2: ({ children }) => <h2 className="mb-2 mt-3 text-sm font-semibold">{children}</h2>,
  h3: ({ children }) => <h3 className="mb-1 mt-2 text-sm font-semibold">{children}</h3>,
  p: ({ children }) => <p className="mb-2 last:mb-0 leading-relaxed">{children}</p>,
  ul: ({ children }) => <ul className="mb-2 ml-4 list-disc space-y-0.5">{children}</ul>,
  ol: ({ children }) => <ol className="mb-2 ml-4 list-decimal space-y-0.5">{children}</ol>,
  li: ({ children }) => <li className="leading-relaxed">{children}</li>,
  blockquote: ({ children }) => (
    <blockquote className="my-2 border-l-2 border-muted-foreground/30 pl-3 italic text-muted-foreground">
      {children}
    </blockquote>
  ),
  hr: () => <hr className="my-3 border-border" />,
};

export const MarkdownMessage = memo(function MarkdownMessage({
  content,
  className,
}: {
  content: string;
  className?: string;
}) {
  return (
    <div className={cn("min-w-0 max-w-full break-words text-sm leading-relaxed [&>*:first-child]:mt-0 [&>*:last-child]:mb-0", className)}>
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {content}
      </ReactMarkdown>
    </div>
  );
});
