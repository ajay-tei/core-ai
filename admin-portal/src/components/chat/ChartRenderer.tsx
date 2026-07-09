import { useMemo } from "react";
import {
  Bar,
  BarChart,
  Line,
  LineChart,
  Area,
  AreaChart,
  Pie,
  PieChart,
  Cell,
  CartesianGrid,
  XAxis,
  YAxis,
  Tooltip,
  Legend,
  ResponsiveContainer,
} from "recharts";

// ─────────────────────────────────────────────────────────────────────────────
// Chart spec — emitted by an agent inside a ```chart fenced JSON block, or
// derived from a complete tool-result table via the "Chart" toggle.
//
//   {
//     "type": "bar" | "line" | "pie" | "area",
//     "x": "month",                       // category / x-axis key (name key for pie)
//     "series": ["revenue", "cost"],      // numeric value keys (single value for pie)
//     "data": [{ "month": "Jan", "revenue": 100, "cost": 50 }, ...],
//     "title": "Revenue vs Cost"          // optional
//   }
// ─────────────────────────────────────────────────────────────────────────────

export interface ChartSpec {
  type: "bar" | "line" | "pie" | "area";
  x: string;
  series: string[];
  data: Array<Record<string, unknown>>;
  title?: string;
}

const PALETTE = [
  "#6366f1", // indigo
  "#10b981", // emerald
  "#f59e0b", // amber
  "#ef4444", // red
  "#06b6d4", // cyan
  "#a855f7", // violet
  "#ec4899", // pink
  "#84cc16", // lime
];

/** Parses a chart spec from a raw JSON string. Returns null when invalid. */
export function parseChartSpec(raw: string): ChartSpec | null {
  try {
    const obj = JSON.parse(raw) as Partial<ChartSpec>;
    if (
      obj &&
      typeof obj.type === "string" &&
      typeof obj.x === "string" &&
      Array.isArray(obj.series) &&
      Array.isArray(obj.data) &&
      ["bar", "line", "pie", "area"].includes(obj.type)
    ) {
      return {
        type: obj.type,
        x: obj.x,
        series: obj.series.map(String),
        data: obj.data as Array<Record<string, unknown>>,
        title: typeof obj.title === "string" ? obj.title : undefined,
      };
    }
  } catch {
    /* fall through */
  }
  return null;
}

export function ChartRenderer({ spec }: { spec: ChartSpec }) {
  const { type, x, series, data, title } = spec;

  const chart = useMemo(() => {
    // Colours are driven by `currentColor` so they inherit the readable
    // foreground colour set on the wrapper (theme-aware for light/dark). The
    // CSS theme tokens are oklch(...) values, so they must NOT be wrapped in
    // hsl() — doing so yields an invalid colour that recharts silently drops.
    const grid = <CartesianGrid strokeDasharray="3 3" stroke="currentColor" opacity={0.15} />;
    const axisProps = {
      tick: { fill: "currentColor", fontSize: 11, opacity: 0.85 },
      tickLine: { stroke: "currentColor", opacity: 0.3 },
      axisLine: { stroke: "currentColor", opacity: 0.3 },
    } as const;
    const tooltip = (
      <Tooltip
        contentStyle={{
          background: "var(--popover)",
          border: "1px solid var(--border)",
          color: "var(--popover-foreground)",
          borderRadius: 8,
          fontSize: 12,
        }}
        labelStyle={{ color: "var(--popover-foreground)" }}
        itemStyle={{ color: "var(--popover-foreground)" }}
      />
    );
    const legend = (
      <Legend
        wrapperStyle={{ fontSize: 12 }}
        formatter={(value) => <span className="text-foreground">{value}</span>}
      />
    );

    switch (type) {
      case "line":
        return (
          <LineChart data={data}>
            {grid}
            <XAxis dataKey={x} {...axisProps} />
            <YAxis {...axisProps} />
            {tooltip}
            {legend}
            {series.map((s, i) => (
              <Line key={s} type="monotone" dataKey={s} stroke={PALETTE[i % PALETTE.length]} strokeWidth={2} dot={false} />
            ))}
          </LineChart>
        );
      case "area":
        return (
          <AreaChart data={data}>
            {grid}
            <XAxis dataKey={x} {...axisProps} />
            <YAxis {...axisProps} />
            {tooltip}
            {legend}
            {series.map((s, i) => (
              <Area
                key={s}
                type="monotone"
                dataKey={s}
                stroke={PALETTE[i % PALETTE.length]}
                fill={PALETTE[i % PALETTE.length]}
                fillOpacity={0.2}
                strokeWidth={2}
              />
            ))}
          </AreaChart>
        );
      case "pie":
        return (
          <PieChart>
            {tooltip}
            {legend}
            <Pie data={data} dataKey={series[0]} nameKey={x} cx="50%" cy="50%" outerRadius={90} label={{ fill: "currentColor", fontSize: 11 }}>
              {data.map((_, i) => (
                <Cell key={i} fill={PALETTE[i % PALETTE.length]} />
              ))}
            </Pie>
          </PieChart>
        );
      case "bar":
      default:
        return (
          <BarChart data={data}>
            {grid}
            <XAxis dataKey={x} {...axisProps} />
            <YAxis {...axisProps} />
            {tooltip}
            {legend}
            {series.map((s, i) => (
              <Bar key={s} dataKey={s} fill={PALETTE[i % PALETTE.length]} radius={[3, 3, 0, 0]} />
            ))}
          </BarChart>
        );
    }
  }, [type, x, series, data]);

  return (
    <div className="my-2 rounded-md border bg-card p-3 text-foreground">
      {title && <p className="mb-2 text-xs font-semibold text-muted-foreground">{title}</p>}
      <ResponsiveContainer width="100%" height={260}>
        {chart}
      </ResponsiveContainer>
    </div>
  );
}
