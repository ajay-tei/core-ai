import { useCallback, useEffect, useState } from "react";
import { api, type WidgetConfigDto } from "@/api";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { Plus, Pencil, Trash2, Copy, Code2 } from "lucide-react";
import { toast } from "sonner";
import { WidgetEditor } from "@/components/WidgetEditor";
import { storageKey } from "@/lib/brand";

const BASE_URL = import.meta.env.VITE_API_URL ?? import.meta.env.BASE_URL.replace(/\/$/, '');

const TENANT_ID = Number(localStorage.getItem(storageKey("tenant_id")) ?? "1");

export function WidgetManager() {
  const [widgets, setWidgets] = useState<WidgetConfigDto[]>([]);
  const [loading, setLoading] = useState(true);
  const [editorOpen, setEditorOpen] = useState(false);
  const [editingId, setEditingId] = useState<string | undefined>(undefined);

  const load = useCallback(async () => {
    setLoading(true);
    try { setWidgets(await api.listWidgets(TENANT_ID)); }
    catch { toast.error("Failed to load widgets"); }
    finally { setLoading(false); }
  }, []);

  useEffect(() => { load(); }, [load]);

  const handleDelete = async (w: WidgetConfigDto) => {
    if (!confirm(`Delete widget "${w.name}"? This cannot be undone.`)) return;
    try {
      await api.deleteWidget(w.id, TENANT_ID);
      toast.success(`Widget "${w.name}" deleted`);
      load();
    } catch { toast.error("Failed to delete widget"); }
  };

  const copyEmbed = (w: WidgetConfigDto) => {
    const code = w.ssoConfigId
      ? `<script>\n  window.__divaSsoProvider = async () => {\n    return await YOUR_AUTH_SDK.getToken();\n  };\n</script>\n<script src="${BASE_URL}/widget.js" data-widget-id="${w.id}"></script>`
      : `<script src="${BASE_URL}/widget.js" data-widget-id="${w.id}"></script>`;
    navigator.clipboard.writeText(code).then(
      () => toast.success("Embed code copied"),
      () => toast.error("Failed to copy")
    );
  };

  return (
    <div className="p-6 space-y-6 max-w-5xl mx-auto">
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-2xl font-bold flex items-center gap-2">
            <Code2 className="w-6 h-6" />
            Chat Widgets
          </h1>
          <p className="text-muted-foreground text-sm mt-1">
            Embed Diva AI chat into any website via a script tag.
          </p>
        </div>
        <Button onClick={() => { setEditingId(undefined); setEditorOpen(true); }}>
          <Plus className="w-4 h-4 mr-1" /> New Widget
        </Button>
      </div>

      {loading ? (
        <div className="space-y-3">
          {[1, 2].map(i => <Skeleton key={i} className="h-24 w-full" />)}
        </div>
      ) : widgets.length === 0 ? (
        <div className="border rounded-lg p-12 text-center text-muted-foreground">
          <Code2 className="w-10 h-10 mx-auto mb-3 opacity-30" />
          <p>No widgets yet. Create one to generate an embed code.</p>
        </div>
      ) : (
        <div className="space-y-3">
          {widgets.map(w => (
            <Card key={w.id}>
              <CardHeader className="pb-2">
                <CardTitle className="text-base flex items-center justify-between">
                  <span className="flex items-center gap-2">
                    {w.name}
                    {w.isActive ? (
                      <Badge variant="default" className="text-xs">Active</Badge>
                    ) : (
                      <Badge variant="secondary" className="text-xs">Inactive</Badge>
                    )}
                    {w.ssoConfigId && <Badge variant="outline" className="text-xs">SSO</Badge>}
                    {w.allowAnonymous && <Badge variant="outline" className="text-xs">Anon</Badge>}
                  </span>
                  <span className="flex gap-2">
                    <Button variant="ghost" size="sm" onClick={() => copyEmbed(w)} title="Copy embed code">
                      <Copy className="w-4 h-4" />
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => { setEditingId(w.id); setEditorOpen(true); }}>
                      <Pencil className="w-4 h-4" />
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => handleDelete(w)}>
                      <Trash2 className="w-4 h-4 text-destructive" />
                    </Button>
                  </span>
                </CardTitle>
              </CardHeader>
              <CardContent className="text-sm text-muted-foreground space-y-1">
                <div>Agent: <span className="font-mono text-foreground">{w.agentId}</span></div>
                {w.allowedOrigins.length > 0 && (
                  <div>Origins: {w.allowedOrigins.join(', ')}</div>
                )}
                {w.expiresAt && (
                  <div>Expires: {new Date(w.expiresAt).toLocaleDateString()}</div>
                )}
              </CardContent>
            </Card>
          ))}
        </div>
      )}

      {editorOpen && (
        <WidgetEditor
          tenantId={TENANT_ID}
          widgetId={editingId}
          onClose={() => setEditorOpen(false)}
          onSaved={() => { setEditorOpen(false); load(); }}
        />
      )}
    </div>
  );
}
