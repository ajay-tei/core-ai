import { Layers } from "lucide-react";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useEnvironment } from "@/hooks/useEnvironment";

/**
 * Global environment switcher (Phase F step 23) — scopes which environment's Agents/MCP Servers/
 * Scheduled Tasks/Agent Groups are shown and edited. Selection persists to localStorage + the
 * `?env=` URL query param (deep-linkable, survives refresh) via useEnvironment().
 */
export function EnvironmentSwitcher()
{
  const { environments, currentEnvironmentId, setCurrentEnvironmentId, loading } = useEnvironment();

  if (loading || environments.length === 0) return null;

  // Highest rank first visually makes no sense for a switcher — sort ascending (lowest/Dev first).
  const sorted = [...environments].sort((a, b) => a.rank - b.rank);

  return (
    <Select
      value={currentEnvironmentId ? String(currentEnvironmentId) : undefined}
      onValueChange={(v) => setCurrentEnvironmentId(Number(v))}
    >
      <SelectTrigger size="sm" className="w-[150px]" title="Switch environment">
        <Layers className="size-3.5 text-muted-foreground" />
        <SelectValue placeholder="Environment" />
      </SelectTrigger>
      <SelectContent>
        {sorted.map((env) => (
          <SelectItem key={env.id} value={String(env.id)}>
            {env.displayName}
            {env.isDefault && <span className="ml-1 text-xs text-muted-foreground">(default)</span>}
          </SelectItem>
        ))}
      </SelectContent>
    </Select>
  );
}
