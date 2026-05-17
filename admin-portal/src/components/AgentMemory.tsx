import { useEffect, useState } from "react";
import { api, listAgentMemories, deleteAgentMemory, clearAgentMemoryType } from "@/api";
import type { AgentMemoryItem, AgentSummary } from "@/api";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Badge } from "@/components/ui/badge";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Trash2, RefreshCw, Archive } from "lucide-react";
import { toast } from "sonner";

const MEMORY_TYPES = ["all", "working", "episodic", "semantic"] as const;

export function AgentMemory() {
  const [memories, setMemories] = useState<AgentMemoryItem[]>([]);
  const [agents, setAgents] = useState<AgentSummary[]>([]);
  const [selectedAgent, setSelectedAgent] = useState<string>("_all");
  const [selectedType, setSelectedType] = useState<string>("all");
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    api.listAgents().then(setAgents).catch(() => {});
  }, []);

  const loadMemories = async () => {
    setLoading(true);
    try {
      const result = await listAgentMemories(
        selectedAgent === "_all" ? undefined : selectedAgent,
        selectedType === "all" ? undefined : selectedType,
      );
      setMemories(result);
    } catch (e: unknown) {
      const msg = e && typeof e === "object" && "error" in e ? (e as { error: string }).error : String(e);
      toast.error("Failed to load memories", { description: msg });
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => { loadMemories(); }, [selectedAgent, selectedType]);

  const handleDelete = async (id: string) => {
    try {
      await deleteAgentMemory(id);
      toast.success("Memory deleted");
      setMemories((prev) => prev.filter((m) => m.id !== id));
    } catch (e: unknown) {
      toast.error("Delete failed", { description: String(e) });
    }
  };

  const handleClearType = async () => {
    if (selectedAgent === "_all" || selectedType === "all") {
      toast.error("Select a specific agent and memory type to clear");
      return;
    }
    try {
      const result = await clearAgentMemoryType(selectedAgent, selectedType);
      toast.success(`Cleared ${result.deleted} ${selectedType} memories`);
      loadMemories();
    } catch (e: unknown) {
      toast.error("Clear failed", { description: String(e) });
    }
  };

  const typeBadgeVariant = (type: string) => {
    switch (type) {
      case "working": return "secondary" as const;
      case "episodic": return "default" as const;
      case "semantic": return "outline" as const;
      default: return "secondary" as const;
    }
  };

  return (
    <div className="space-y-6 max-w-6xl">
      <div>
        <h1 className="text-2xl font-semibold tracking-tight">Agent Memory</h1>
        <p className="text-sm text-muted-foreground">
          View and manage agent memory across working, episodic, and semantic tiers.
        </p>
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Filters</CardTitle>
          <CardDescription>Filter memories by agent and type</CardDescription>
        </CardHeader>
        <CardContent>
          <div className="flex gap-4 items-end">
            <div className="space-y-1.5 w-64">
              <label className="text-sm font-medium">Agent</label>
              <Select value={selectedAgent} onValueChange={setSelectedAgent}>
                <SelectTrigger>
                  <SelectValue placeholder="All agents" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value="_all">All agents</SelectItem>
                  {agents.map((a) => (
                    <SelectItem key={a.id} value={a.id}>
                      {a.displayName || a.name}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            <div className="space-y-1.5 w-48">
              <label className="text-sm font-medium">Memory Type</label>
              <Select value={selectedType} onValueChange={setSelectedType}>
                <SelectTrigger>
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {MEMORY_TYPES.map((t) => (
                    <SelectItem key={t} value={t}>
                      {t === "all" ? "All types" : t.charAt(0).toUpperCase() + t.slice(1)}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>

            <Button variant="outline" size="sm" onClick={loadMemories} disabled={loading}>
              <RefreshCw className={`mr-2 size-4 ${loading ? "animate-spin" : ""}`} />
              Refresh
            </Button>

            {selectedAgent !== "_all" && selectedType !== "all" && (
              <Button variant="destructive" size="sm" onClick={handleClearType}>
                <Archive className="mr-2 size-4" />
                Clear All {selectedType}
              </Button>
            )}
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Memories ({memories.length})</CardTitle>
        </CardHeader>
        <CardContent>
          {memories.length === 0 ? (
            <p className="text-sm text-muted-foreground py-8 text-center">
              No memories found. Agents create memories during conversations using the save_memory tool.
            </p>
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-16">Type</TableHead>
                  <TableHead>Content</TableHead>
                  <TableHead className="w-28">Tags</TableHead>
                  <TableHead className="w-36">Created</TableHead>
                  <TableHead className="w-36">Expires</TableHead>
                  <TableHead className="w-12" />
                </TableRow>
              </TableHeader>
              <TableBody>
                {memories.map((m) => (
                  <TableRow key={m.id}>
                    <TableCell>
                      <Badge variant={typeBadgeVariant(m.memoryType)}>
                        {m.memoryType}
                      </Badge>
                    </TableCell>
                    <TableCell className="max-w-md">
                      <p className="text-sm truncate" title={m.content}>
                        {m.content.length > 200 ? m.content.slice(0, 200) + "…" : m.content}
                      </p>
                      {m.sessionId && (
                        <p className="text-xs text-muted-foreground mt-1">
                          Session: {m.sessionId.slice(0, 8)}…
                        </p>
                      )}
                    </TableCell>
                    <TableCell>
                      <div className="flex flex-wrap gap-1">
                        {m.tags.map((t) => (
                          <Badge key={t} variant="outline" className="text-xs">{t}</Badge>
                        ))}
                      </div>
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {new Date(m.createdAt).toLocaleString()}
                    </TableCell>
                    <TableCell className="text-xs text-muted-foreground">
                      {m.expiresAt ? new Date(m.expiresAt).toLocaleString() : "—"}
                    </TableCell>
                    <TableCell>
                      <Button
                        variant="ghost"
                        size="icon"
                        onClick={() => handleDelete(m.id)}
                        title="Delete memory"
                      >
                        <Trash2 className="size-4 text-destructive" />
                      </Button>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>
    </div>
  );
}
