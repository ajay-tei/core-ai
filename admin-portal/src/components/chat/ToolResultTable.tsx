import { useMemo, useState } from "react";
import { AlertTriangle, BarChart3, Database, Table2 } from "lucide-react";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { ChartRenderer, type ChartSpec } from "./ChartRenderer";
import { cn } from "@/lib/utils";

// ─────────────────────────────────────────────────────────────────────────────
// Renders a tool's output. When the output is a complete JSON result set
// ({ rows, columns } or an array of objects) it is shown as a data grid with an
// optional chart toggle. When the JSON is truncated (the backend appends a
// "[truncated …]" marker once output exceeds MaxToolResultChars) or cannot be
// parsed, it degrades gracefully to the raw text with a hint.
// ─────────────────────────────────────────────────────────────────────────────

const MAX_ROWS = 50;
const TRUNCATION_MARKER = "[truncated";

interface ParsedTable {
  columns: string[];
  rows: Array<Record<string, unknown>>;
  fromCache: boolean;
}

function coerceRow(row: unknown, columns: string[]): Record<string, unknown> {
  if (Array.isArray(row)) {
    const obj: Record<string, unknown> = {};
    columns.forEach((c, i) => (obj[c] = row[i]));
    return obj;
  }
  return (row ?? {}) as Record<string, unknown>;
}

function parseTable(text: string): ParsedTable | null {
  let obj: unknown;
  try {
    obj = JSON.parse(text);
  } catch {
    return null;
  }

  // Array of row objects
  if (Array.isArray(obj)) {
    if (obj.length === 0 || typeof obj[0] !== "object" || obj[0] === null) return null;
    const columns = Object.keys(obj[0] as Record<string, unknown>);
    return { columns, rows: obj as Array<Record<string, unknown>>, fromCache: false };
  }

  if (obj && typeof obj === "object") {
    const rec = obj as Record<string, unknown>;
    const fromCache = rec.from_cache === true || rec.fromCache === true;
    const rawRows = (rec.rows ?? rec.data ?? rec.results) as unknown;

    if (Array.isArray(rawRows) && rawRows.length > 0) {
      let columns: string[];
      if (Array.isArray(rec.columns)) {
        columns = (rec.columns as unknown[]).map((c) =>
          typeof c === "string" ? c : String((c as { name?: unknown })?.name ?? c),
        );
      } else if (typeof rawRows[0] === "object" && rawRows[0] !== null && !Array.isArray(rawRows[0])) {
        columns = Object.keys(rawRows[0] as Record<string, unknown>);
      } else {
        return null;
      }
      const rows = rawRows.map((r) => coerceRow(r, columns));
      return { columns, rows, fromCache };
    }
  }

  return null;
}

function isNumeric(v: unknown): boolean {
  return typeof v === "number" || (typeof v === "string" && v.trim() !== "" && !isNaN(Number(v)));
}

function fmtCell(v: unknown): string {
  if (v === null || v === undefined) return "—";
  if (typeof v === "object") return JSON.stringify(v);
  return String(v);
}

/** Builds a bar-chart spec from a table: first non-numeric column → x, numeric columns → series. */
function deriveChartSpec(table: ParsedTable): ChartSpec | null {
  const sample = table.rows[0];
  const numericCols = table.columns.filter((c) => isNumeric(sample[c]));
  const categoryCol = table.columns.find((c) => !isNumeric(sample[c])) ?? table.columns[0];
  if (numericCols.length === 0 || !categoryCol) return null;
  return {
    type: "bar",
    x: categoryCol,
    series: numericCols.slice(0, 4),
    data: table.rows.slice(0, MAX_ROWS).map((r) => {
      const o: Record<string, unknown> = { [categoryCol]: fmtCell(r[categoryCol]) };
      numericCols.forEach((c) => (o[c] = Number(r[c])));
      return o;
    }),
  };
}

export function ToolResultTable({ output, className }: { output: string; className?: string }) {
  const [view, setView] = useState<"table" | "chart">("table");
  const truncated = output.includes(TRUNCATION_MARKER);
  const table = useMemo(() => (truncated ? null : parseTable(output)), [output, truncated]);
  const chartSpec = useMemo(() => (table ? deriveChartSpec(table) : null), [table]);

  // Fallback: raw text (parse failed or truncated JSON)
  if (!table) {
    return (
      <div className={className}>
        {truncated && (
          <div className="mb-1 flex items-start gap-1.5 rounded border border-amber-500/30 bg-amber-500/5 px-2 py-1 text-[10px] text-amber-400">
            <AlertTriangle className="mt-0.5 size-3 shrink-0" />
            <span>
              Output was truncated. Raise the agent&apos;s <span className="font-mono">MaxToolResultChars</span> to
              see the full result, or ask the agent to narrow the query.
            </span>
          </div>
        )}
        <pre className="whitespace-pre-wrap break-words rounded bg-background/50 p-1.5 text-[11px] text-emerald-400 overflow-auto max-h-28">
          {output}
        </pre>
      </div>
    );
  }

  const shown = table.rows.slice(0, MAX_ROWS);
  const hiddenCount = table.rows.length - shown.length;

  return (
    <div className={cn("my-1 rounded-md border bg-background/50", className)}>
      <div className="flex items-center gap-2 border-b px-2 py-1">
        <span className="text-[10px] font-medium text-muted-foreground">
          {table.rows.length} row{table.rows.length !== 1 ? "s" : ""} · {table.columns.length} col
          {table.columns.length !== 1 ? "s" : ""}
        </span>
        {table.fromCache && (
          <Badge variant="secondary" className="h-4 gap-1 px-1.5 text-[9px]">
            <Database className="size-2.5" /> cached
          </Badge>
        )}
        {chartSpec && (
          <div className="ml-auto flex items-center gap-0.5">
            <Button
              variant={view === "table" ? "secondary" : "ghost"}
              size="sm"
              className="h-5 gap-1 px-1.5 text-[10px]"
              onClick={() => setView("table")}
            >
              <Table2 className="size-3" /> Table
            </Button>
            <Button
              variant={view === "chart" ? "secondary" : "ghost"}
              size="sm"
              className="h-5 gap-1 px-1.5 text-[10px]"
              onClick={() => setView("chart")}
            >
              <BarChart3 className="size-3" /> Chart
            </Button>
          </div>
        )}
      </div>

      {view === "chart" && chartSpec ? (
        <ChartRenderer spec={chartSpec} />
      ) : (
        <div className="max-h-72 overflow-auto">
          <Table>
            <TableHeader>
              <TableRow>
                {table.columns.map((c) => (
                  <TableHead key={c} className="h-7 px-2 text-[11px]">
                    {c}
                  </TableHead>
                ))}
              </TableRow>
            </TableHeader>
            <TableBody>
              {shown.map((row, ri) => (
                <TableRow key={ri}>
                  {table.columns.map((c) => (
                    <TableCell key={c} className="px-2 py-1 text-[11px] font-mono">
                      {fmtCell(row[c])}
                    </TableCell>
                  ))}
                </TableRow>
              ))}
            </TableBody>
          </Table>
          {hiddenCount > 0 && (
            <p className="border-t px-2 py-1 text-[10px] text-muted-foreground">
              Showing first {MAX_ROWS} of {table.rows.length} rows.
            </p>
          )}
        </div>
      )}
    </div>
  );
}
