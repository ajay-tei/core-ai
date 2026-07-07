import { useEffect, useState } from "react";
import { Server, X } from "lucide-react";
import { api, type McpServer } from "@/api";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";

interface McpServerSelectorProps {
  /** JSON string array of shared server names, e.g. '["weather-server"]' */
  value?: string;
  /** Called with updated JSON string (or undefined when empty) */
  onChange: (json: string | undefined) => void;
}

/**
 * Multi-select for attaching tenant-shared MCP servers to an agent by name.
 * Saves to the agent's mcpServerRefsJson. The actual credential is chosen at
 * runtime based on the platform API key used to invoke the agent.
 */
export function McpServerSelector({ value, onChange }: McpServerSelectorProps) {
  const [servers, setServers] = useState<McpServer[]>([]);

  useEffect(() => {
    api.listMcpServers().then(setServers).catch(() => {});
  }, []);

  const selectedNames: string[] = (() => {
    try {
      const parsed = value ? JSON.parse(value) : [];
      return parsed
        .map((x: unknown) => (x == null ? "" : String(x)))
        .filter((s: string) => s.length > 0);
    } catch {
      return [];
    }
  })();

  const toggle = (name: string) => {
    const next = selectedNames.includes(name)
      ? selectedNames.filter((x) => x !== name)
      : [...selectedNames, name];
    onChange(next.length > 0 ? JSON.stringify(next) : undefined);
  };

  const selected = servers.filter((s) => selectedNames.includes(s.name));
  const unselected = servers.filter((s) => !selectedNames.includes(s.name));
  // Names referenced but no longer existing (e.g. server deleted) — surface so the user can clean up.
  const missing = selectedNames.filter((n) => !servers.some((s) => s.name === n));

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base flex items-center gap-2">
          <Server className="size-4" />
          Shared MCP Servers
        </CardTitle>
        <CardDescription>
          Attach reusable tool servers managed under Settings → MCP Servers. Each server's
          credential is selected dynamically from the platform API key used to invoke this agent.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        {selected.length > 0 && (
          <div className="flex flex-wrap gap-2">
            {selected.map((s) => (
              <Badge
                key={s.id}
                variant="secondary"
                className="gap-1 cursor-pointer hover:bg-destructive/10"
                onClick={() => toggle(s.name)}
              >
                {s.name}
                <X className="size-3" />
              </Badge>
            ))}
          </div>
        )}

        {missing.length > 0 && (
          <div className="flex flex-wrap gap-2">
            {missing.map((n) => (
              <Badge
                key={n}
                variant="outline"
                className="gap-1 cursor-pointer border-amber-500 text-amber-600 hover:bg-destructive/10"
                onClick={() => toggle(n)}
                title="This server no longer exists — click to remove"
              >
                {n} (missing)
                <X className="size-3" />
              </Badge>
            ))}
          </div>
        )}

        {unselected.length > 0 ? (
          <div className="flex flex-wrap gap-2">
            {unselected.map((s) => (
              <Button
                key={s.id}
                variant="outline"
                size="sm"
                className="h-7 text-xs"
                onClick={() => toggle(s.name)}
              >
                + {s.name}
              </Button>
            ))}
          </div>
        ) : servers.length === 0 ? (
          <p className="text-xs text-muted-foreground">
            No shared MCP servers configured. Create them under Settings → MCP Servers.
          </p>
        ) : null}

        {selected.length > 0 && (
          <p className="text-xs text-muted-foreground">
            {selected.length} server{selected.length !== 1 ? "s" : ""} attached
          </p>
        )}
      </CardContent>
    </Card>
  );
}
