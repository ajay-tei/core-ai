import { useCallback, useEffect, useState } from "react";
import {
  api,
  type AgentSummary,
  type CreateWidgetRequest,
  type SsoConfig,
  type WidgetConfigDto,
  type WidgetThemeDto,
} from "@/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Textarea } from "@/components/ui/textarea";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { toast } from "sonner";
import { X } from "lucide-react";

// ── Default presets ────────────────────────────────────────────────────────────

const LIGHT_PRESET: WidgetThemeDto = {
  background: "#ffffff", surface: "#f9fafb", border: "#e5e7eb",
  primary: "#6366f1", primaryText: "#ffffff",
  text: "#111827", textMuted: "#6b7280",
  fontFamily: "system-ui, sans-serif", fontSize: "14px",
  agentBubbleBg: "#f3f4f6", agentBubbleText: "#111827",
  headerBg: "#6366f1", headerText: "#ffffff",
  inputBg: "#ffffff", inputBorder: "#d1d5db", inputText: "#111827",
  launcherSize: 56, preset: "light",
};

const DARK_PRESET: WidgetThemeDto = {
  background: "#1f2937", surface: "#111827", border: "#374151",
  primary: "#818cf8", primaryText: "#ffffff",
  text: "#f9fafb", textMuted: "#9ca3af",
  fontFamily: "system-ui, sans-serif", fontSize: "14px",
  agentBubbleBg: "#374151", agentBubbleText: "#f9fafb",
  headerBg: "#111827", headerText: "#f9fafb",
  inputBg: "#1f2937", inputBorder: "#4b5563", inputText: "#f9fafb",
  launcherSize: 56, preset: "dark",
};

function emptyRequest(): CreateWidgetRequest {
  return {
    agentId: "", name: "", allowedOrigins: [],
    ssoConfigId: undefined, allowAnonymous: true,
    welcomeMessage: "", placeholderText: "",
    theme: { ...LIGHT_PRESET },
    respectSystemTheme: true, showBranding: true,
  };
}

interface Props {
  tenantId: number;
  widgetId?: string;
  onClose: () => void;
  onSaved: (dto: WidgetConfigDto) => void;
}

type PresetName = "light" | "dark" | "custom";

export function WidgetEditor({ tenantId, widgetId, onClose, onSaved }: Props) {
  const isEdit = Boolean(widgetId);
  const [form, setForm] = useState<CreateWidgetRequest>(emptyRequest());
  const [preset, setPreset] = useState<PresetName>("light");
  const [originsText, setOriginsText] = useState("");
  const [agents, setAgents] = useState<AgentSummary[]>([]);
  const [ssoConfigs, setSsoConfigs] = useState<SsoConfig[]>([]);
  const [saving, setSaving] = useState(false);

  const load = useCallback(async () => {
    const [agentList, ssoList] = await Promise.all([
      api.listAgents().catch(() => [] as AgentSummary[]),
      api.listSsoConfigs(tenantId).catch(() => [] as SsoConfig[]),
    ]);
    setAgents(agentList);
    setSsoConfigs(ssoList);

    if (isEdit && widgetId) {
      const widgets = await api.listWidgets(tenantId);
      const w = widgets.find(x => x.id === widgetId);
      if (w) {
        setForm({
          agentId: w.agentId,
          name: w.name,
          allowedOrigins: w.allowedOrigins,
          ssoConfigId: w.ssoConfigId,
          allowAnonymous: w.allowAnonymous,
          welcomeMessage: w.welcomeMessage ?? "",
          placeholderText: w.placeholderText ?? "",
          theme: { ...w.theme },
          respectSystemTheme: w.respectSystemTheme,
          showBranding: w.showBranding,
          expiresAt: w.expiresAt,
        });
        setOriginsText(w.allowedOrigins.join("\n"));
        setPreset((w.theme.preset as PresetName) ?? "light");
      }
    }
  }, [isEdit, tenantId, widgetId]);

  useEffect(() => { load(); }, [load]);

  function setThemeField(field: keyof WidgetThemeDto, value: string | number) {
    setForm(f => ({
      ...f,
      theme: { ...f.theme!, [field]: value, preset: "custom" },
    }));
    setPreset("custom");
  }

  function applyPreset(p: PresetName) {
    setPreset(p);
    if (p === "light") setForm(f => ({ ...f, theme: { ...LIGHT_PRESET } }));
    else if (p === "dark") setForm(f => ({ ...f, theme: { ...DARK_PRESET } }));
    // custom: keep existing values
  }

  function parseOrigins(text: string): string[] {
    return text.split("\n").map(s => s.trim()).filter(Boolean);
  }

  async function handleSave() {
    if (!form.agentId) { toast.error("Agent is required"); return; }
    if (!form.name.trim()) { toast.error("Name is required"); return; }
    const origins = parseOrigins(originsText);
    if (origins.length === 0) { toast.error("At least one allowed origin is required"); return; }

    const dto: CreateWidgetRequest = { ...form, allowedOrigins: origins };
    setSaving(true);
    try {
      const result = isEdit && widgetId
        ? await api.updateWidget(widgetId, dto, tenantId)
        : await api.createWidget(dto, tenantId);
      toast.success(isEdit ? "Widget updated" : "Widget created");
      onSaved(result);
    } catch {
      toast.error("Failed to save widget");
    } finally {
      setSaving(false);
    }
  }

  const theme = form.theme ?? LIGHT_PRESET;

  return (
    <div className="fixed inset-0 z-50 bg-black/50 flex items-center justify-center p-4">
      <div className="bg-background rounded-xl shadow-2xl w-full max-w-3xl max-h-[90vh] flex flex-col">
        {/* Header */}
        <div className="flex items-center justify-between px-6 py-4 border-b">
          <h2 className="text-lg font-semibold">{isEdit ? "Edit Widget" : "New Widget"}</h2>
          <button onClick={onClose} className="text-muted-foreground hover:text-foreground">
            <X className="w-5 h-5" />
          </button>
        </div>

        {/* Scrollable body */}
        <div className="overflow-y-auto flex-1 p-6 space-y-5">
          {/* Basic fields */}
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div className="space-y-1">
              <Label>Name *</Label>
              <Input
                value={form.name}
                onChange={e => setForm(f => ({ ...f, name: e.target.value }))}
                placeholder="My Widget"
              />
            </div>
            <div className="space-y-1">
              <Label>Agent *</Label>
              <Select value={form.agentId} onValueChange={v => setForm(f => ({ ...f, agentId: v }))}>
                <SelectTrigger><SelectValue placeholder="Select agent…" /></SelectTrigger>
                <SelectContent>
                  {agents.map(a => (
                    <SelectItem key={a.id} value={a.id}>{a.displayName || a.name}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
          </div>

          <div className="space-y-1">
            <Label>Allowed Origins * (one per line, e.g. https://myapp.com)</Label>
            <Textarea
              value={originsText}
              onChange={e => setOriginsText(e.target.value)}
              placeholder={"https://myapp.com\nhttps://staging.myapp.com"}
              rows={3}
            />
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div className="space-y-1">
              <Label>SSO Config (optional)</Label>
              <Select
                value={form.ssoConfigId?.toString() ?? "none"}
                onValueChange={v => setForm(f => ({ ...f, ssoConfigId: v === "none" ? undefined : Number(v) }))}
              >
                <SelectTrigger><SelectValue placeholder="None" /></SelectTrigger>
                <SelectContent>
                  <SelectItem value="none">None (no SSO)</SelectItem>
                  {ssoConfigs.map(s => (
                    <SelectItem key={s.id} value={s.id.toString()}>{s.providerName} — {s.issuer}</SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </div>
            <div className="space-y-1">
              <Label>Expires At (optional)</Label>
              <Input
                type="date"
                value={form.expiresAt ? form.expiresAt.split("T")[0] : ""}
                onChange={e => setForm(f => ({ ...f, expiresAt: e.target.value ? `${e.target.value}T00:00:00Z` : undefined }))}
              />
            </div>
          </div>

          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            <div className="space-y-1">
              <Label>Welcome Message</Label>
              <Textarea
                value={form.welcomeMessage ?? ""}
                onChange={e => setForm(f => ({ ...f, welcomeMessage: e.target.value }))}
                placeholder="Hi! How can I help you today?"
                rows={2}
              />
            </div>
            <div className="space-y-1">
              <Label>Placeholder Text</Label>
              <Input
                value={form.placeholderText ?? ""}
                onChange={e => setForm(f => ({ ...f, placeholderText: e.target.value }))}
                placeholder="Type a message…"
              />
            </div>
          </div>

          <div className="flex gap-6 flex-wrap">
            <label className="flex items-center gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={form.allowAnonymous}
                onChange={e => setForm(f => ({ ...f, allowAnonymous: e.target.checked }))}
              />
              Allow anonymous users
            </label>
            <label className="flex items-center gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={form.respectSystemTheme}
                onChange={e => setForm(f => ({ ...f, respectSystemTheme: e.target.checked }))}
              />
              Respect system dark mode
            </label>
            <label className="flex items-center gap-2 cursor-pointer">
              <input
                type="checkbox"
                checked={form.showBranding}
                onChange={e => setForm(f => ({ ...f, showBranding: e.target.checked }))}
              />
              Show "Powered by Diva AI"
            </label>
          </div>

          {/* Theme section */}
          <div className="border rounded-lg p-4 space-y-4">
            <div className="flex items-center justify-between">
              <h3 className="font-semibold">Theme</h3>
              <div className="flex gap-2">
                {(["light", "dark", "custom"] as PresetName[]).map(p => (
                  <button
                    key={p}
                    onClick={() => applyPreset(p)}
                    className={`px-3 py-1 rounded-full text-sm border transition-colors ${
                      preset === p
                        ? "bg-primary text-primary-foreground border-primary"
                        : "border-border hover:bg-muted"
                    }`}
                  >
                    {p.charAt(0).toUpperCase() + p.slice(1)}
                  </button>
                ))}
              </div>
            </div>

            <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
              <ColorField label="Primary" value={theme.primary} onChange={v => setThemeField("primary", v)} />
              <ColorField label="Header BG" value={theme.headerBg} onChange={v => setThemeField("headerBg", v)} />
              <ColorField label="Header Text" value={theme.headerText} onChange={v => setThemeField("headerText", v)} />
              <ColorField label="Background" value={theme.background} onChange={v => setThemeField("background", v)} />
              <ColorField label="Surface" value={theme.surface} onChange={v => setThemeField("surface", v)} />
              <ColorField label="Border" value={theme.border} onChange={v => setThemeField("border", v)} />
              <ColorField label="Agent Bubble BG" value={theme.agentBubbleBg} onChange={v => setThemeField("agentBubbleBg", v)} />
              <ColorField label="Agent Bubble Text" value={theme.agentBubbleText} onChange={v => setThemeField("agentBubbleText", v)} />
              <ColorField label="Body Text" value={theme.text} onChange={v => setThemeField("text", v)} />
              <ColorField label="Muted Text" value={theme.textMuted} onChange={v => setThemeField("textMuted", v)} />
              <ColorField label="Input BG" value={theme.inputBg} onChange={v => setThemeField("inputBg", v)} />
              <ColorField label="Input Border" value={theme.inputBorder} onChange={v => setThemeField("inputBorder", v)} />
            </div>

            <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
              <div className="space-y-1">
                <Label className="text-xs">Font Family</Label>
                <Input
                  value={theme.fontFamily}
                  onChange={e => setThemeField("fontFamily", e.target.value)}
                  placeholder="system-ui, sans-serif"
                />
              </div>
              <div className="space-y-1">
                <Label className="text-xs">Font Size</Label>
                <Select value={theme.fontSize} onValueChange={v => setThemeField("fontSize", v)}>
                  <SelectTrigger><SelectValue /></SelectTrigger>
                  <SelectContent>
                    <SelectItem value="12px">12px</SelectItem>
                    <SelectItem value="14px">14px (default)</SelectItem>
                    <SelectItem value="16px">16px</SelectItem>
                  </SelectContent>
                </Select>
              </div>
            </div>

            {/* Live preview */}
            <div className="mt-2">
              <Label className="text-xs mb-1 block">Preview</Label>
              <WidgetPreview theme={theme} />
            </div>
          </div>
        </div>

        {/* Footer */}
        <div className="flex justify-end gap-3 px-6 py-4 border-t">
          <Button variant="outline" onClick={onClose}>Cancel</Button>
          <Button onClick={handleSave} disabled={saving}>
            {saving ? "Saving…" : isEdit ? "Save Changes" : "Create Widget"}
          </Button>
        </div>
      </div>
    </div>
  );
}

function ColorField({ label, value, onChange }: { label: string; value: string; onChange: (v: string) => void }) {
  return (
    <div className="space-y-1">
      <Label className="text-xs">{label}</Label>
      <div className="flex gap-2 items-center">
        <input
          type="color"
          value={value}
          onChange={e => onChange(e.target.value)}
          className="w-8 h-8 rounded cursor-pointer border"
        />
        <Input
          value={value}
          onChange={e => onChange(e.target.value)}
          className="font-mono text-xs h-8"
          maxLength={9}
        />
      </div>
    </div>
  );
}

function WidgetPreview({ theme }: { theme: WidgetThemeDto }) {
  return (
    <div
      style={{
        width: 260,
        height: 340,
        borderRadius: 12,
        border: `1px solid ${theme.border}`,
        background: theme.background,
        fontFamily: theme.fontFamily,
        fontSize: 12,
        display: "flex",
        flexDirection: "column",
        overflow: "hidden",
        boxShadow: "0 4px 16px rgba(0,0,0,.12)",
      }}
    >
      {/* Header */}
      <div style={{
        background: theme.headerBg,
        color: theme.headerText,
        padding: "10px 14px",
        fontWeight: 600,
        fontSize: 13,
        display: "flex",
        alignItems: "center",
        gap: 6,
      }}>
        <div style={{ width: 7, height: 7, borderRadius: "50%", background: "#4ade80" }} />
        Agent Name
      </div>

      {/* Messages */}
      <div style={{ flex: 1, padding: 10, display: "flex", flexDirection: "column", gap: 8, overflowY: "hidden" }}>
        <div style={{
          alignSelf: "flex-start",
          background: theme.agentBubbleBg,
          color: theme.agentBubbleText,
          borderRadius: "12px 12px 12px 4px",
          padding: "6px 10px",
          maxWidth: "80%",
        }}>
          Hi! How can I help?
        </div>
        <div style={{
          alignSelf: "flex-end",
          background: theme.primary,
          color: theme.primaryText,
          borderRadius: "12px 12px 4px 12px",
          padding: "6px 10px",
          maxWidth: "80%",
        }}>
          Tell me about pricing.
        </div>
        <div style={{
          alignSelf: "flex-start",
          background: theme.agentBubbleBg,
          color: theme.agentBubbleText,
          borderRadius: "12px 12px 12px 4px",
          padding: "6px 10px",
          maxWidth: "80%",
        }}>
          Our plans start at…
        </div>
      </div>

      {/* Input */}
      <div style={{
        borderTop: `1px solid ${theme.border}`,
        background: theme.inputBg,
        padding: "8px 10px",
        display: "flex",
        gap: 6,
        alignItems: "center",
      }}>
        <div style={{
          flex: 1,
          border: `1px solid ${theme.inputBorder}`,
          borderRadius: 8,
          padding: "5px 8px",
          fontSize: 11,
          color: theme.textMuted,
          background: theme.inputBg,
        }}>
          Type a message…
        </div>
        <div style={{
          width: 28,
          height: 28,
          borderRadius: "50%",
          background: theme.primary,
          display: "flex",
          alignItems: "center",
          justifyContent: "center",
        }}>
          <svg width="13" height="13" viewBox="0 0 24 24" fill={theme.primaryText}>
            <path d="M3.478 2.405a.75.75 0 0 0-.926.94l2.432 7.905H13.5a.75.75 0 0 1 0 1.5H4.984l-2.432 7.905a.75.75 0 0 0 .926.94 60.519 60.519 0 0 0 18.445-8.986.75.75 0 0 0 0-1.218A60.517 60.517 0 0 0 3.478 2.405Z" />
          </svg>
        </div>
      </div>
    </div>
  );
}
