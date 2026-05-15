import { useEffect, useState } from "react";
import { Link, useNavigate } from "react-router";
import { toast } from "sonner";
import {
  Bot,
  Download,
  Edit,
  MessageSquare,
  MoreHorizontal,
  Plus,
  RefreshCw,
  Search,
  Settings2,
  Share2,
  Trash2,
  ToggleLeft,
  ToggleRight,
  Upload,
} from "lucide-react";
import { api, type AgentSummary, type AgentImportResult } from "@/api";
import { triggerJsonDownload } from "@/lib/download";
import { AgentImportDialog } from "@/components/AgentImportDialog";
import { Label } from "@/components/ui/label";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Table,
  TableBody,
  TableCell,
  TableHead,
  TableHeader,
  TableRow,
} from "@/components/ui/table";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import {
  Card,
  CardContent,
  CardHeader,
} from "@/components/ui/card";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";

function StatusBadge({ agent }: { agent: AgentSummary }) {
  if (!agent.isEnabled) return <Badge variant="destructive">Disabled</Badge>;
  if (agent.status.toLowerCase() === "published")
    return <Badge className="bg-emerald-500/15 text-emerald-600 border-emerald-500/30 dark:text-emerald-400">Published</Badge>;
  return <Badge variant="secondary">Draft</Badge>;
}

function SharedBadge({ agent }: { agent: AgentSummary }) {
  if (!agent.isShared) return null;
  if (agent.isActivated) {
    return (
      <Badge className="bg-emerald-500/15 text-emerald-600 border-emerald-500/30 dark:text-emerald-400 ml-1.5">
        Activated
      </Badge>
    );
  }
  return (
    <Badge className="bg-slate-500/15 text-slate-600 border-slate-500/30 dark:text-slate-400 ml-1.5">
      Available
    </Badge>
  );
}

export function AgentList() {
  const navigate = useNavigate();
  const [agents, setAgents] = useState<AgentSummary[]>([]);
  const [loading, setLoading] = useState(true);
  const [search, setSearch] = useState("");
  const [deleteTarget, setDeleteTarget] = useState<AgentSummary | null>(null);
  const [deleting, setDeleting] = useState(false);
  const [publishTarget, setPublishTarget] = useState<AgentSummary | null>(null);
  const [groups, setGroups] = useState<{ id: number; name: string; description?: string }[]>([]);
  const [groupsLoading, setGroupsLoading] = useState(false);
  const [selectedGroupId, setSelectedGroupId] = useState<string>("");
  const [publishing, setPublishing] = useState(false);
  const [importOpen, setImportOpen] = useState(false);

  const load = async () => {
    setLoading(true);
    try {
      setAgents(await api.listAgents());
    } catch (e: unknown) {
      toast.error("Failed to load agents", { description: String(e) });
    } finally {
      setLoading(false);
    }
  };

  const handleActivate = async (agent: AgentSummary) => {
    try {
      await api.applyOverlay(agent.id, { isEnabled: true });
      toast.success(`"${agent.displayName || agent.name}" activated`);
      load();
    } catch (e: unknown) {
      toast.error("Failed to activate agent", { description: String(e) });
    }
  };

  const handleDeactivate = async (agent: AgentSummary) => {
    try {
      await api.setOverlayEnabled(agent.id, false);
      toast.success(`"${agent.displayName || agent.name}" deactivated`);
      load();
    } catch (e: unknown) {
      toast.error("Failed to deactivate agent", { description: String(e) });
    }
  };

  const handleOpenPublish = async (agent: AgentSummary) => {
    setPublishTarget(agent);
    setSelectedGroupId("");
    setGroups([]);
    setGroupsLoading(true);
    try {
      const list = await api.listMyGroups();
      setGroups(list);
    } catch (e: unknown) {
      const msg = String(e);
      if (msg.includes("403")) {
        toast.error("Platform admin access required to publish to a group");
      } else {
        toast.error("Failed to load groups", { description: msg });
      }
    } finally {
      setGroupsLoading(false);
    }
  };

  const handlePublish = async () => {
    if (!publishTarget || !selectedGroupId) return;
    setPublishing(true);
    try {
      const fullAgent = await api.getAgent(publishTarget.id);
      setPublishTarget(null);
      navigate(`/platform/groups/${selectedGroupId}/agents/new`, { state: { importAgent: fullAgent } });
    } catch { toast.error("Failed to load agent details"); }
    finally { setPublishing(false); }
  };

  const handleExport = async (agent: AgentSummary) =>
  {
    try
    {
      const bundle = await api.exportAgent(agent.id);
      const filename = `${(agent.name || "agent").replace(/\s+/g, "-").toLowerCase()}-export.json`;
      triggerJsonDownload(bundle, filename);
      toast.success(`Exported "${agent.displayName || agent.name}"`);
    }
    catch (e: unknown)
    {
      toast.error("Export failed", { description: String(e) });
    }
  };

  const handleImportSuccess = (result: AgentImportResult) =>
  {
    const msg = result.warnings.length > 0
      ? result.warnings.join(" ")
      : undefined;
    toast.success(`Imported "${result.agentName}" (${result.rulesImported} rule${result.rulesImported !== 1 ? "s" : ""})`, {
      description: msg,
    });
    load();
  };

  useEffect(() => { load(); }, []);

  const handleDelete = async () => {
    if (!deleteTarget) return;
    setDeleting(true);
    try {
      await api.deleteAgent(deleteTarget.id);
      setAgents((prev) => prev.filter((a) => a.id !== deleteTarget.id));
      toast.success(`Agent "${deleteTarget.displayName || deleteTarget.name}" deleted`);
      setDeleteTarget(null);
    } catch (e: unknown) {
      toast.error("Failed to delete agent", { description: String(e) });
    } finally {
      setDeleting(false);
    }
  };

  const filtered = agents.filter((a) => {
    const q = search.toLowerCase();
    return !q || a.name.toLowerCase().includes(q) || (a.displayName ?? "").toLowerCase().includes(q);
  });

  return (
    <div className="space-y-4">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Agents</h1>
          <p className="text-sm text-muted-foreground">Manage your AI agent configurations</p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="outline" onClick={() => setImportOpen(true)}>
            <Upload className="mr-2 size-4" />
            Import
          </Button>
          <Button asChild>
            <Link to="/agents/new">
              <Plus className="mr-2 size-4" />
              New Agent
            </Link>
          </Button>
        </div>
      </div>

      <Card>
        <CardHeader className="pb-3">
          <div className="flex items-center gap-3">
            <div className="relative flex-1 max-w-sm">
              <Search className="absolute left-2.5 top-2.5 size-4 text-muted-foreground" />
              <Input
                placeholder="Search agents…"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                className="pl-9"
              />
            </div>
            <Button variant="ghost" size="icon" onClick={load} disabled={loading} className="size-9">
              <RefreshCw className={`size-4 ${loading ? "animate-spin" : ""}`} />
            </Button>
            <span className="text-sm text-muted-foreground">{agents.length} agent{agents.length !== 1 ? "s" : ""}</span>
          </div>
        </CardHeader>
        <CardContent className="p-0">
          {loading ? (
            <div className="space-y-0">
              {Array.from({ length: 4 }).map((_, i) => (
                <div key={i} className="flex items-center gap-4 border-b px-6 py-4 last:border-0">
                  <Skeleton className="size-9 rounded-lg" />
                  <div className="flex-1 space-y-1.5">
                    <Skeleton className="h-4 w-40" />
                    <Skeleton className="h-3 w-24" />
                  </div>
                  <Skeleton className="h-6 w-16 rounded-full" />
                  <Skeleton className="h-4 w-24" />
                  <Skeleton className="size-8 rounded-md" />
                </div>
              ))}
            </div>
          ) : filtered.length === 0 ? (
            <EmptyState
              icon={Bot}
              title={search ? "No agents match your search" : "No agents yet"}
              description={search ? "Try a different search term" : "Create your first AI agent to get started"}
              action={
                !search ? (
                  <Button asChild>
                    <Link to="/agents/new"><Plus className="mr-2 size-4" />Create Agent</Link>
                  </Button>
                ) : undefined
              }
            />
          ) : (
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead className="w-12"></TableHead>
                  <TableHead>Name</TableHead>
                  <TableHead>Status</TableHead>
                  <TableHead>Created</TableHead>
                  <TableHead className="w-12"></TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {filtered.map((agent) => (
                  <TableRow
                    key={agent.id}
                    className={agent.isShared ? "opacity-80" : "cursor-pointer"}
                    onClick={() => !agent.isShared && navigate(`/agents/${agent.id}/edit`)}
                  >
                    <TableCell>
                      <div className="flex size-9 items-center justify-center rounded-lg bg-primary/10">
                        <Bot className="size-4 text-primary" />
                      </div>
                    </TableCell>
                    <TableCell>
                      <div className="font-medium flex items-center">
                        {agent.displayName || agent.name}
                        <SharedBadge agent={agent} />
                      </div>
                      <div className="text-xs text-muted-foreground">
                        {agent.name}
                        {agent.isShared && agent.groupName && (
                          <span className="ml-1 text-blue-500/70">· {agent.groupName}</span>
                        )}
                      </div>
                    </TableCell>
                    <TableCell>
                      <StatusBadge agent={agent} />
                    </TableCell>
                    <TableCell className="text-sm text-muted-foreground">
                      {new Date(agent.createdAt).toLocaleDateString(undefined, {
                        year: "numeric", month: "short", day: "numeric",
                      })}
                    </TableCell>
                    <TableCell onClick={(e) => e.stopPropagation()}>
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button variant="ghost" size="icon" className="size-8">
                            <MoreHorizontal className="size-4" />
                            <span className="sr-only">Actions</span>
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="end">
                          {!agent.isShared && (
                            <>
                              <DropdownMenuItem onClick={() => navigate(`/agents/${agent.id}/edit`)}>
                                <Edit className="mr-2 size-4" />
                                Edit
                              </DropdownMenuItem>
                              <DropdownMenuItem onClick={() => handleOpenPublish(agent)}>
                                <Share2 className="mr-2 size-4" />
                                Publish to Group
                              </DropdownMenuItem>
                              <DropdownMenuItem onClick={() => handleExport(agent)}>
                                <Download className="mr-2 size-4" />
                                Export
                              </DropdownMenuItem>
                            </>
                          )}
                          {agent.isShared && !agent.isActivated && (
                            <DropdownMenuItem onClick={() => handleActivate(agent)}>
                              <ToggleRight className="mr-2 size-4" />
                              Activate
                            </DropdownMenuItem>
                          )}
                          {agent.isShared && agent.isActivated && (
                            <>
                              <DropdownMenuItem onClick={() => navigate(`/agents/group/${agent.id}/overlay`)}>
                                <Settings2 className="mr-2 size-4" />
                                Customize
                              </DropdownMenuItem>
                              <DropdownMenuItem onClick={() => handleDeactivate(agent)}>
                                <ToggleLeft className="mr-2 size-4" />
                                Deactivate
                              </DropdownMenuItem>
                            </>
                          )}
                          <DropdownMenuItem onClick={() => navigate(`/agents/${agent.id}/chat`, { state: { agent } })}>
                            <MessageSquare className="mr-2 size-4" />
                            Test Agent
                          </DropdownMenuItem>
                          {!agent.isShared && (
                            <>
                              <DropdownMenuSeparator />
                              <DropdownMenuItem
                                className="text-destructive focus:text-destructive"
                                onClick={() => setDeleteTarget(agent)}
                              >
                                <Trash2 className="mr-2 size-4" />
                                Delete
                              </DropdownMenuItem>
                            </>
                          )}
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          )}
        </CardContent>
      </Card>

      {/* Publish to Group dialog */}
      <Dialog open={!!publishTarget} onOpenChange={(open) => !open && setPublishTarget(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Publish to Group</DialogTitle>
            <DialogDescription>
              Create a shared agent template based on{" "}
              <strong>{publishTarget?.displayName || publishTarget?.name}</strong>. Select the target group.
            </DialogDescription>
          </DialogHeader>
          <div className="space-y-2 py-2">
            <Label>Target Group</Label>
            {groupsLoading ? (
              <p className="text-sm text-muted-foreground py-2">Loading groups…</p>
            ) : groups.length === 0 ? (
              <p className="text-sm text-muted-foreground py-2">
                Your tenant is not a member of any group. Ask a platform admin to add you to a group first.
              </p>
            ) : (
              <Select value={selectedGroupId} onValueChange={setSelectedGroupId}>
                <SelectTrigger>
                  <SelectValue placeholder="Select a group…" />
                </SelectTrigger>
                <SelectContent>
                  {groups.map((g) => (
                    <SelectItem key={g.id} value={String(g.id)}>{g.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            )}
          </div>
          <DialogFooter>
            <Button variant="outline" onClick={() => setPublishTarget(null)}>Cancel</Button>
            <Button onClick={handlePublish} disabled={!selectedGroupId || publishing || groupsLoading}>
              {publishing ? "Loading…" : "Open in Builder"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      {/* Agent Import dialog */}
      <AgentImportDialog
        open={importOpen}
        onOpenChange={setImportOpen}
        onSuccess={handleImportSuccess}
      />

      {/* Delete confirmation dialog */}
      <Dialog open={!!deleteTarget} onOpenChange={(open) => !open && setDeleteTarget(null)}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Delete Agent</DialogTitle>
            <DialogDescription>
              Are you sure you want to delete{" "}
              <strong>{deleteTarget?.displayName || deleteTarget?.name}</strong>? This action cannot be undone.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setDeleteTarget(null)}>Cancel</Button>
            <Button variant="destructive" onClick={handleDelete} disabled={deleting}>
              {deleting ? "Deleting…" : "Delete Agent"}
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}

