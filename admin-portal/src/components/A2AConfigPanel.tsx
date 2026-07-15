import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

interface A2AConfigPanelProps {
  endpoint?: string;
  authScheme?: string;
  secretRef?: string;
  remoteAgentId?: string;
  onEndpointChange: (v: string) => void;
  onAuthSchemeChange: (v: string) => void;
  onSecretRefChange: (v: string) => void;
  onRemoteAgentIdChange: (v: string) => void;
}

export function A2AConfigPanel({
  endpoint,
  authScheme,
  secretRef,
  remoteAgentId,
  onEndpointChange,
  onAuthSchemeChange,
  onSecretRefChange,
  onRemoteAgentIdChange,
}: A2AConfigPanelProps) {
  return (
    <Card>
      <CardHeader className="pb-3">
        <CardTitle className="text-sm">A2A Remote Agent</CardTitle>
        <CardDescription className="text-xs">
          Connect to a remote agent via the Agent-to-Agent protocol.
          When an endpoint is set, execution is delegated to the remote agent.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-3">
        <div className="space-y-1">
          <Label className="text-xs">A2A Endpoint URL</Label>
          <Input
            className="h-8 text-xs"
            placeholder="https://remote-agent.example.com"
            value={endpoint ?? ""}
            onChange={(e) => onEndpointChange(e.target.value)}
          />
        </div>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          <div className="space-y-1">
            <Label className="text-xs">Auth Scheme</Label>
            <Select value={authScheme || "None"} onValueChange={onAuthSchemeChange}>
              <SelectTrigger className="h-8 text-xs">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="None">None</SelectItem>
                <SelectItem value="Bearer">Bearer Token</SelectItem>
                <SelectItem value="ApiKey">API Key</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-1">
            <Label className="text-xs">Secret Reference</Label>
            <Input
              className="h-8 text-xs"
              placeholder="Name from Settings → MCP Credentials"
              value={secretRef ?? ""}
              onChange={(e) => onSecretRefChange(e.target.value)}
              disabled={authScheme === "None" || !authScheme}
            />
          </div>
        </div>
        <div className="space-y-1">
          <Label className="text-xs">Remote Agent ID</Label>
          <Input
            className="h-8 text-xs font-mono"
            placeholder="Agent ID on the remote Diva instance (e.g. ec8618bd-61fe-…)"
            value={remoteAgentId ?? ""}
            onChange={(e) => onRemoteAgentIdChange(e.target.value)}
          />
          <p className="text-xs text-muted-foreground">
            Found in AgentBuilder on the remote machine, or via its <code>/.well-known/agents.json</code>.
          </p>
        </div>
      </CardContent>
    </Card>
  );
}
