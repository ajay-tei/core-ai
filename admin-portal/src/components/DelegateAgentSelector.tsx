import { useEffect, useState } from "react";
import { Users, X } from "lucide-react";
import { api, type AgentSummary } from "@/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

interface DelegateAgentSelectorProps {
  /** Current agent ID (excluded from picker to prevent self-delegation) */
  currentAgentId?: string;
  /** JSON string of agent IDs, e.g. '["id-1","id-2"]' */
  value?: string;
  /** Called with updated JSON string */
  onChange: (json: string | undefined) => void;
  /** Only offer agents belonging to this environment — delegation cannot cross environments
   *  (a delegate ID tagged to a different environment can never actually resolve at runtime,
   *  see DynamicAgentRegistry.GetByIdAsync's environment-scoped lookup). */
  environmentId?: number;
  /** Display name of environmentId, used only for the empty-state message. */
  environmentName?: string;
}

export function DelegateAgentSelector({
  currentAgentId,
  value,
  onChange,
  environmentId,
  environmentName,
}: DelegateAgentSelectorProps) {
  const [agents, setAgents] = useState<AgentSummary[]>([]);

  useEffect(() => {
    api.listAgents(environmentId).then(setAgents).catch(() => {});
  }, [environmentId]);

  const selectedIds: string[] = (() => {
    try {
      const parsed = value ? JSON.parse(value) : [];
      // Normalize: handle both ["id","id"] and legacy [1,2] formats
      // Filter out null/undefined/"null"/"NaN" (legacy bad data from number-based code)
      return parsed
        .map((x: unknown) => (x == null ? "" : String(x)))
        .filter((s: string) => s.length > 0 && s !== "null" && s !== "NaN" && s !== "undefined");
    } catch {
      return [];
    }
  })();

  // Auto-clear corrupted data (e.g. legacy [null] from number-based code)
  useEffect(() => {
    if (value && selectedIds.length === 0) {
      onChange(undefined);
    }
  }, [value, selectedIds.length, onChange]);

  const available = agents.filter(
    (a) => a.id !== currentAgentId && a.isEnabled
  );

  const toggle = (id: string) => {
    const next = selectedIds.includes(id)
      ? selectedIds.filter((x) => x !== id)
      : [...selectedIds, id];
    onChange(next.length > 0 ? JSON.stringify(next) : undefined);
  };

  const selectedAgents = available.filter((a) =>
    selectedIds.includes(a.id)
  );
  const unselectedAgents = available.filter(
    (a) => !selectedIds.includes(a.id)
  );

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base flex items-center gap-2">
          <Users className="size-4" />
          Delegated Agents
        </CardTitle>
        <CardDescription>
          Allow this agent to delegate sub-tasks to other agents via tool calls.
          Selected agents appear as callable tools during execution.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {selectedAgents.length > 0 && (
          <div className="flex flex-wrap gap-2">
            {selectedAgents.map((a) => (
              <Badge
                key={a.id}
                variant="secondary"
                className="gap-1 cursor-pointer hover:bg-destructive/10"
                onClick={() => toggle(a.id)}
              >
                {a.displayName || a.name}
                <X className="size-3" />
              </Badge>
            ))}
          </div>
        )}

        {unselectedAgents.length > 0 ? (
          <div className="flex flex-wrap gap-2">
            {unselectedAgents.map((a) => (
              <Button
                key={a.id}
                variant="outline"
                size="sm"
                className="h-7 text-xs"
                onClick={() => toggle(a.id)}
              >
                + {a.displayName || a.name}
              </Button>
            ))}
          </div>
        ) : available.length === 0 ? (
          <p className="text-xs text-muted-foreground">
            No other enabled agents available for delegation{environmentName ? ` in ${environmentName}` : ""}.
          </p>
        ) : null}

        {selectedIds.length > 0 && (
          <p className="text-xs text-muted-foreground">
            {selectedAgents.length} agent{selectedAgents.length !== 1 ? "s" : ""} selected
            {selectedAgents.length < selectedIds.length && (
              <span className="text-amber-500 ml-1">
                ({selectedIds.length - selectedAgents.length} ID{selectedIds.length - selectedAgents.length !== 1 ? "s" : ""} unresolved — agent may have been deleted or disabled)
              </span>
            )}
          </p>
        )}
      </CardContent>
    </Card>
  );
}
