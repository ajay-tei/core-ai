import { Badge } from "@/components/ui/badge";
import type { TenantEnvironment } from "@/api";
import { cn } from "@/lib/utils";

interface EnvironmentBadgeProps
{
  environment: TenantEnvironment | null | undefined;
  /** All environments in the tenant's pipeline — used to compute relative rank color.
   *  If omitted, falls back to a neutral color (can't determine relative position). */
  allEnvironments?: TenantEnvironment[];
  className?: string;
}

/**
 * Consistent color-coded environment badge used platform-wide (switcher, list-toolbar rows,
 * LLM config / credential environment tabs, promotion dialogs): highest-rank environment
 * (nothing ranked above it — usually "Production") renders as a destructive/red badge, the
 * lowest-rank environment (usually "Dev") renders green/neutral, anything in between renders amber.
 */
export function EnvironmentBadge({ environment, allEnvironments, className }: EnvironmentBadgeProps)
{
  if (!environment) return null;

  const ranks = allEnvironments?.map((e) => e.rank) ?? [environment.rank];
  const maxRank = Math.max(...ranks);
  const minRank = Math.min(...ranks);

  const isTop = environment.rank === maxRank;
  const isBottom = environment.rank === minRank && minRank !== maxRank;

  if (isTop)
  {
    return <Badge variant="destructive" className={className}>{environment.displayName}</Badge>;
  }
  if (isBottom)
  {
    return (
      <Badge
        variant="outline"
        className={cn("border-green-600/40 bg-green-500/10 text-green-700 dark:text-green-400", className)}
      >
        {environment.displayName}
      </Badge>
    );
  }
  return (
    <Badge
      variant="outline"
      className={cn("border-amber-600/40 bg-amber-500/10 text-amber-700 dark:text-amber-400", className)}
    >
      {environment.displayName}
    </Badge>
  );
}
