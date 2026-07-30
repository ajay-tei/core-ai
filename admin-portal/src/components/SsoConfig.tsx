import { useNavigate } from "react-router";
import { api, type SsoConfig, type SsoConfigListParams } from "@/api";
import { usePagedList } from "@/hooks/usePagedList";
import { ListToolbar, ListPagination } from "@/components/ui/list-toolbar";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Switch } from "@/components/ui/switch";
import { Card, CardContent } from "@/components/ui/card";
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table";
import { Plus, Pencil, Trash2, Shield } from "lucide-react";
import { toast } from "sonner";

export function SsoConfig({ tenantId = 1 }: { tenantId?: number }) {
  const navigate = useNavigate();
  const { result, loading, params, update, updateDebounced, setPage, reload } =
    usePagedList<SsoConfig, SsoConfigListParams>(api.listSsoConfigsPaged, { tenantId, page: 1, pageSize: 25 });

  async function toggleActive(c: SsoConfig) {
    try {
      await api.updateSsoConfig(c.id, { ...c, isActive: !c.isActive }, tenantId);
      reload();
    } catch (e) {
      toast.error(`Update failed: ${e}`);
    }
  }

  async function del(c: SsoConfig) {
    if (!confirm(`Delete SSO config for "${c.providerName}" (${c.issuer})?`)) return;
    try {
      await api.deleteSsoConfig(c.id, tenantId);
      toast.success("Deleted");
      reload();
    } catch (e) {
      toast.error(`Delete failed: ${e}`);
    }
  }

  return (
    <div className="p-6 space-y-4">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Shield className="size-5" />
          <h1 className="text-2xl font-semibold">SSO Configuration</h1>
        </div>
        <Button onClick={() => navigate(`/settings/sso/new${tenantId !== 1 ? `?tenantId=${tenantId}` : ""}`)}><Plus className="size-4 mr-2" /> Add Provider</Button>
      </div>

      <ListToolbar
        searchValue={params.search}
        onSearchChange={v => updateDebounced({ search: v || undefined })}
        searchPlaceholder="Search SSO providers…"
        pageSize={params.pageSize}
        onPageSizeChange={pageSize => update({ pageSize })}
        pageSizeOptions={[25, 50, 100]}
      />

      <Card>
        <CardContent className="p-0">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Provider</TableHead>
                <TableHead>Issuer</TableHead>
                <TableHead>Token Type</TableHead>
                <TableHead>Active</TableHead>
                <TableHead className="w-24">Actions</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {loading ? (
                <TableRow><TableCell colSpan={5} className="text-center text-muted-foreground py-8">Loading…</TableCell></TableRow>
              ) : (result?.items.length ?? 0) === 0 ? (
                <TableRow><TableCell colSpan={5} className="text-center text-muted-foreground py-8">No SSO providers configured yet.</TableCell></TableRow>
              ) : (result?.items ?? []).map(c => (
                <TableRow key={c.id}>
                  <TableCell className="font-medium capitalize">{c.providerName}</TableCell>
                  <TableCell className="font-mono text-sm truncate max-w-xs">{c.issuer}</TableCell>
                  <TableCell>
                    <Badge variant={c.tokenType === "jwt" ? "default" : "secondary"}>{c.tokenType.toUpperCase()}</Badge>
                  </TableCell>
                  <TableCell>
                    <Switch checked={c.isActive} onCheckedChange={() => toggleActive(c)} />
                  </TableCell>
                  <TableCell>
                    <div className="flex gap-1">
                      <Button size="icon" variant="ghost" onClick={() => navigate(`/settings/sso/${c.id}/edit${tenantId !== 1 ? `?tenantId=${tenantId}` : ""}`)}><Pencil className="size-4" /></Button>
                      <Button size="icon" variant="ghost" className="text-destructive" onClick={() => del(c)}><Trash2 className="size-4" /></Button>
                    </div>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </CardContent>
      </Card>

      {result && (
        <ListPagination
          page={result.page}
          totalPages={result.totalPages}
          totalCount={result.totalCount}
          onPageChange={setPage}
          itemLabel="total"
        />
      )}
    </div>
  );
}

