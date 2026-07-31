import { Layers } from "lucide-react";
import { Select, SelectContent, SelectGroup, SelectItem, SelectLabel, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useEnvironment } from "@/hooks/useEnvironment";

/**
 * Global environment switcher (Phase F step 23) — scopes which environment's Agents/MCP Servers/
 * Scheduled Tasks/Agent Groups are shown and edited. Selection persists to localStorage + the
 * `?env=` URL query param (deep-linkable, survives refresh) via useEnvironment(). Grouped by
 * ClientGroup (shared Dev/QA tier first, then one labeled group per client) so the list stays
 * scannable once a tenant has several client-specific environment pairs.
 */
export function EnvironmentSwitcher()
{
  const { environments, currentEnvironmentId, setCurrentEnvironmentId, loading } = useEnvironment();

  if (loading || environments.length === 0) return null;

  const byGroup = new Map<string, typeof environments>();
  for (const env of environments)
  {
    const key = env.clientGroup?.trim() || "Shared";
    if (!byGroup.has(key)) byGroup.set(key, []);
    byGroup.get(key)!.push(env);
  }
  const groups = [...byGroup.entries()]
    .map(([k, v]) => [k, [...v].sort((a, b) => a.rank - b.rank)] as const)
    .sort(([a], [b]) => (a === "Shared" ? -1 : b === "Shared" ? 1 : a.localeCompare(b)));

  return (
    <Select
      value={currentEnvironmentId ? String(currentEnvironmentId) : undefined}
      onValueChange={(v) => setCurrentEnvironmentId(Number(v))}
    >
      <SelectTrigger size="sm" className="w-[170px]" title="Switch environment">
        <Layers className="size-3.5 text-muted-foreground" />
        <SelectValue placeholder="Environment" />
      </SelectTrigger>
      <SelectContent>
        {groups.map(([groupName, envs]) => (
          <SelectGroup key={groupName}>
            <SelectLabel>{groupName}</SelectLabel>
            {envs.map((env) => (
              <SelectItem key={env.id} value={String(env.id)}>
                {env.displayName}
                {env.isDefault && <span className="ml-1 text-xs text-muted-foreground">(default)</span>}
              </SelectItem>
            ))}
          </SelectGroup>
        ))}
      </SelectContent>
    </Select>
  );
}
