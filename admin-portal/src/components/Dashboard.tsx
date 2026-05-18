import { useEffect, useState } from "react";
import { Link } from "react-router";
import { toast } from "sonner";
import {
  AlertTriangle,
  ArrowRight,
  Bot,
  Brain,
  Calendar,
  CheckCircle2,
  Clock,
  MessagesSquare,
  Plus,
  RefreshCw,
  Shield,
  SkipForward,
  XCircle,
} from "lucide-react";
import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  PieChart,
  Pie,
  Cell,
  Legend,
} from "recharts";
import { api, type DashboardStats, type SchedulerStats } from "@/api";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";

// Placeholder sparkline data (would come from real analytics endpoint)
const activityData = [
  { day: "Mon", sessions: 12 },
  { day: "Tue", sessions: 18 },
  { day: "Wed", sessions: 9 },
  { day: "Thu", sessions: 25 },
  { day: "Fri", sessions: 22 },
  { day: "Sat", sessions: 8 },
  { day: "Sun", sessions: 14 },
];

const ruleCategories = [
  { name: "General", value: 35, color: "var(--color-chart-1)" },
  { name: "Tone", value: 20, color: "var(--color-chart-2)" },
  { name: "Format", value: 18, color: "var(--color-chart-3)" },
  { name: "Safety", value: 15, color: "var(--color-chart-4)" },
  { name: "Terminology", value: 12, color: "var(--color-chart-5)" },
];

interface StatCardProps {
  label: string;
  value: number | undefined;
  icon: React.ElementType;
  description: string;
  loading?: boolean;
  variant?: "default" | "warning" | "success";
}

function StatCard({ label, value, icon: Icon, description, loading, variant = "default" }: StatCardProps) {
  const variantClasses = {
    default: "text-primary",
    warning: "text-amber-500",
    success: "text-emerald-500",
  };

  return (
    <Card>
      <CardHeader className="flex flex-row items-center justify-between space-y-0 pb-2">
        <CardTitle className="text-sm font-medium text-muted-foreground">{label}</CardTitle>
        <div className={`rounded-md p-1.5 ${variant === "default" ? "bg-primary/10" : variant === "warning" ? "bg-amber-500/10" : "bg-emerald-500/10"}`}>
          <Icon className={`size-4 ${variantClasses[variant]}`} />
        </div>
      </CardHeader>
      <CardContent>
        {loading ? (
          <Skeleton className="h-8 w-20" />
        ) : (
          <div className={`text-3xl font-bold tabular-nums ${variantClasses[variant]}`}>
            {value ?? 0}
          </div>
        )}
        <p className="mt-1 text-xs text-muted-foreground">{description}</p>
      </CardContent>
    </Card>
  );
}

export function Dashboard() {
  const [stats, setStats] = useState<DashboardStats | null>(null);
  const [schedulerStats, setSchedulerStats] = useState<SchedulerStats | null>(null);
  const [loading, setLoading] = useState(true);

  const load = async () => {
    setLoading(true);
    try {
      const [s, ss] = await Promise.all([
        api.getDashboard(1),
        api.getSchedulerStats(1).catch(() => null),
      ]);
      setStats(s);
      setSchedulerStats(ss);
    } catch (e: unknown) {
      toast.error("Failed to load dashboard stats", { description: String(e) });
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { load(); }, []);

  return (
    <div className="space-y-6">
      {/* Header */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Dashboard</h1>
          <p className="text-sm text-muted-foreground">
            {stats ? `Updated ${new Date(stats.asOf).toLocaleTimeString()}` : "Platform overview"}
          </p>
        </div>
        <Button variant="outline" size="sm" onClick={load} disabled={loading}>
          <RefreshCw className={`mr-2 size-4 ${loading ? "animate-spin" : ""}`} />
          Refresh
        </Button>
      </div>

      {/* Pending Rules Alert */}
      {stats && stats.pendingRuleCount > 0 && (
        <Card className="border-amber-500/50 bg-amber-500/5">
          <CardContent className="flex items-center gap-3 py-4">
            <AlertTriangle className="size-5 text-amber-500 shrink-0" />
            <div className="flex-1">
              <p className="text-sm font-medium">
                {stats.pendingRuleCount} learned rule{stats.pendingRuleCount > 1 ? "s" : ""} awaiting review
              </p>
              <p className="text-xs text-muted-foreground">Review and approve agent-learned rules to improve quality</p>
            </div>
            <Button asChild variant="outline" size="sm" className="shrink-0 text-amber-600 border-amber-500/50">
              <Link to="/rules/learned">
                Review <ArrowRight className="ml-1 size-3" />
              </Link>
            </Button>
          </CardContent>
        </Card>
      )}

      {/* Stat Cards — 4 columns */}
      <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
        <StatCard
          label="Total Agents"
          value={stats?.agentCount}
          icon={Bot}
          description="Configured AI agents"
          loading={loading}
        />
        <StatCard
          label="Active Rules"
          value={stats?.activeRuleCount}
          icon={Shield}
          description="Business rules applied"
          loading={loading}
        />
        <StatCard
          label="Pending Rules"
          value={stats?.pendingRuleCount}
          icon={Brain}
          description="Awaiting approval"
          loading={loading}
          variant={stats && stats.pendingRuleCount > 0 ? "warning" : "success"}
        />
        <StatCard
          label="Sessions"
          value={stats?.sessionCount}
          icon={MessagesSquare}
          description="Active agent sessions"
          loading={loading}
        />
      </div>

      {/* Charts row */}
      <div className="grid gap-4 lg:grid-cols-3">
        {/* Activity chart — takes 2 of 3 columns */}
        <Card className="lg:col-span-2">
          <CardHeader>
            <CardTitle className="text-base">Agent Activity</CardTitle>
            <CardDescription>Sessions over the past 7 days</CardDescription>
          </CardHeader>
          <CardContent>
            <ResponsiveContainer width="100%" height={200}>
              <AreaChart data={activityData} margin={{ top: 4, right: 4, bottom: 0, left: -20 }}>
                <defs>
                  <linearGradient id="sessionGrad" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor="var(--color-chart-1)" stopOpacity={0.3} />
                    <stop offset="95%" stopColor="var(--color-chart-1)" stopOpacity={0} />
                  </linearGradient>
                </defs>
                <CartesianGrid strokeDasharray="3 3" className="stroke-border" />
                <XAxis dataKey="day" tick={{ fontSize: 12 }} className="fill-muted-foreground" />
                <YAxis tick={{ fontSize: 12 }} className="fill-muted-foreground" />
                <Tooltip
                  contentStyle={{
                    background: "hsl(var(--card))",
                    borderColor: "hsl(var(--border))",
                    borderRadius: "var(--radius)",
                    fontSize: 12,
                  }}
                />
                <Area
                  type="monotone"
                  dataKey="sessions"
                  stroke="var(--color-chart-1)"
                  fill="url(#sessionGrad)"
                  strokeWidth={2}
                />
              </AreaChart>
            </ResponsiveContainer>
          </CardContent>
        </Card>

        {/* Rules by category */}
        <Card>
          <CardHeader>
            <CardTitle className="text-base">Rules by Category</CardTitle>
            <CardDescription>Distribution of active rules</CardDescription>
          </CardHeader>
          <CardContent>
            <ResponsiveContainer width="100%" height={200}>
              <PieChart>
                <Pie
                  data={ruleCategories}
                  cx="50%"
                  cy="45%"
                  innerRadius={52}
                  outerRadius={72}
                  paddingAngle={2}
                  dataKey="value"
                >
                  {ruleCategories.map((entry, i) => (
                    <Cell key={i} fill={entry.color} />
                  ))}
                </Pie>
                <Legend
                  iconType="circle"
                  iconSize={8}
                  wrapperStyle={{ fontSize: 11 }}
                />
              </PieChart>
            </ResponsiveContainer>
          </CardContent>
        </Card>
      </div>

      {/* Quick Actions */}
      {/* Scheduled Jobs */}
      <Card>
        <CardHeader className="flex flex-row items-center justify-between pb-2">
          <div>
            <CardTitle className="text-base">Scheduled Jobs</CardTitle>
            <CardDescription>Today&apos;s run summary</CardDescription>
          </div>
          <Button asChild variant="ghost" size="sm" className="text-xs text-muted-foreground">
            <Link to="/schedules">View all <ArrowRight className="ml-1 size-3" /></Link>
          </Button>
        </CardHeader>
        <CardContent>
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-4 mb-4">
            <div className="flex items-center gap-3 rounded-lg border p-3">
              <Calendar className="size-5 text-primary shrink-0" />
              <div>
                <div className="text-xl font-bold tabular-nums">{schedulerStats?.enabledTasks ?? "—"}</div>
                <div className="text-xs text-muted-foreground">Active tasks</div>
              </div>
            </div>
            <div className="flex items-center gap-3 rounded-lg border p-3">
              <CheckCircle2 className="size-5 text-emerald-500 shrink-0" />
              <div>
                <div className="text-xl font-bold tabular-nums text-emerald-600">{schedulerStats?.todaySucceeded ?? "—"}</div>
                <div className="text-xs text-muted-foreground">Succeeded today</div>
              </div>
            </div>
            <div className="flex items-center gap-3 rounded-lg border p-3">
              <XCircle className="size-5 text-red-500 shrink-0" />
              <div>
                <div className="text-xl font-bold tabular-nums text-red-600">{schedulerStats?.todayFailed ?? "—"}</div>
                <div className="text-xs text-muted-foreground">Failed today</div>
              </div>
            </div>
            <div className="flex items-center gap-3 rounded-lg border p-3">
              <SkipForward className="size-5 text-yellow-500 shrink-0" />
              <div>
                <div className="text-xl font-bold tabular-nums text-yellow-600">{schedulerStats?.todaySkipped ?? "—"}</div>
                <div className="text-xs text-muted-foreground">Skipped today</div>
              </div>
            </div>
          </div>
          {schedulerStats && schedulerStats.recentFailures.length > 0 && (
            <div>
              <p className="text-xs font-medium text-muted-foreground mb-2">Recent failures &amp; skips</p>
              <div className="space-y-1">
                {schedulerStats.recentFailures.slice(0, 5).map((f, i) => (
                  <div key={i} className="flex items-center justify-between text-xs py-1 border-b last:border-0">
                    <span className="font-medium truncate max-w-[200px]">{f.taskName}</span>
                    <div className="flex items-center gap-2 shrink-0">
                      <span className={f.status === "skipped" ? "text-yellow-600" : "text-red-600"}>{f.status}</span>
                      <span className="text-muted-foreground">{new Date(f.scheduledForUtc).toLocaleString()}</span>
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Quick Actions */}
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Quick Actions</CardTitle>
          <CardDescription>Common tasks to get started</CardDescription>
        </CardHeader>
        <CardContent>
          <div className="grid gap-3 sm:grid-cols-3">
            <Button asChild variant="outline" className="h-auto flex-col gap-1.5 py-4">
              <Link to="/agents/new">
                <Plus className="size-5 text-primary" />
                <span className="text-sm font-medium">Create Agent</span>
                <span className="text-xs text-muted-foreground">Configure a new AI agent</span>
              </Link>
            </Button>
            <Button asChild variant="outline" className="h-auto flex-col gap-1.5 py-4">
              <Link to="/rules/business">
                <Shield className="size-5 text-primary" />
                <span className="text-sm font-medium">Add Business Rule</span>
                <span className="text-xs text-muted-foreground">Define behavior guidelines</span>
              </Link>
            </Button>
            <Button asChild variant="outline" className="h-auto flex-col gap-1.5 py-4">
              <Link to="/schedules">
                <Clock className="size-5 text-primary" />
                <span className="text-sm font-medium">Schedule Task</span>
                <span className="text-xs text-muted-foreground">Automate agent runs</span>
              </Link>
            </Button>
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

