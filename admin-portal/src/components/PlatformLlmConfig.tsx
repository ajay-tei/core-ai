import { useState, useEffect } from "react";
import { api, type PlatformLlmConfig as PlatformLlmConfigType, type UpsertLlmConfigDto, type CreatePlatformLlmConfigDto } from "@/api";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent, CardHeader, CardTitle, CardDescription } from "@/components/ui/card";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { Badge } from "@/components/ui/badge";
import { Save, Trash2, Plus, ChevronDown, ChevronUp } from "lucide-react";
import { toast } from "sonner";

const PROVIDERS = ["Anthropic", "OpenAI", "AzureOpenAI", "Ollama", "LiteLLM"];

export interface LlmFormProps {
  value: UpsertLlmConfigDto;
  onChange: (patch: Partial<UpsertLlmConfigDto>) => void;
  maskedApiKey?: string;
}

export function LlmForm({ value, onChange, maskedApiKey }: LlmFormProps) {
  return (
    <div className="grid gap-4">
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div className="space-y-1.5">
          <Label>Provider</Label>
          <Select value={value.provider ?? ""} onValueChange={(v) => onChange({ provider: v })}>
            <SelectTrigger>
              <SelectValue placeholder="Select provider…" />
            </SelectTrigger>
            <SelectContent>
              {PROVIDERS.map((p) => (
                <SelectItem key={p} value={p}>{p}</SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        <div className="space-y-1.5">
          <Label>Default Model</Label>
          <Input
            value={value.model ?? ""}
            onChange={(e) => onChange({ model: e.target.value })}
            placeholder="e.g. claude-sonnet-4-6"
          />
        </div>
      </div>

      <div className="space-y-1.5">
        <Label>API Key {maskedApiKey && <span className="text-xs text-muted-foreground ml-1">(currently set — leave blank to keep)</span>}</Label>
        <Input
          type="password"
          value={value.apiKey ?? ""}
          onChange={(e) => onChange({ apiKey: e.target.value })}
          placeholder={maskedApiKey ?? "Enter API key…"}
          autoComplete="new-password"
        />
      </div>

      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        <div className="space-y-1.5">
          <Label>Endpoint <span className="text-xs text-muted-foreground">(optional)</span></Label>
          <Input
            value={value.endpoint ?? ""}
            onChange={(e) => onChange({ endpoint: e.target.value })}
            placeholder="https://…"
          />
        </div>
        <div className="space-y-1.5">
          <Label>Deployment Name <span className="text-xs text-muted-foreground">(Azure)</span></Label>
          <Input
            value={value.deploymentName ?? ""}
            onChange={(e) => onChange({ deploymentName: e.target.value })}
            placeholder="e.g. gpt-4o"
          />
        </div>
      </div>

      <div className="space-y-1.5">
        <Label>Available Models JSON <span className="text-xs text-muted-foreground">(optional — drives model picker in UI)</span></Label>
        <Input
          value={value.availableModelsJson ?? ""}
          onChange={(e) => onChange({ availableModelsJson: e.target.value })}
          placeholder='["model-a","model-b"]'
        />
      </div>
    </div>
  );
}

// ── Per-config card ────────────────────────────────────────────────────────────

interface ConfigCardProps {
  config: PlatformLlmConfigType;
  onSaved: (updated: PlatformLlmConfigType) => void;
  onDeleted: (id: number) => void;
}

function ConfigCard({ config, onSaved, onDeleted }: ConfigCardProps) {
  const [open,    setOpen]    = useState(false);
  const [name,    setName]    = useState(config.name);
  const [form,    setForm]    = useState<UpsertLlmConfigDto>({
    provider:            config.provider,
    apiKey:              "",
    model:               config.model,
    endpoint:            config.endpoint            ?? "",
    deploymentName:      config.deploymentName      ?? "",
    availableModelsJson: config.availableModelsJson ?? "",
  });
  const [saving,  setSaving]  = useState(false);
  const [deleting, setDeleting] = useState(false);

  async function save() {
    setSaving(true);
    const dto: UpsertLlmConfigDto = { ...form, provider: form.provider || undefined };
    if (!dto.apiKey)             delete dto.apiKey;
    if (!dto.endpoint)           delete dto.endpoint;
    if (!dto.deploymentName)     delete dto.deploymentName;
    if (!dto.availableModelsJson) delete dto.availableModelsJson;
    try {
      const updated = await api.updatePlatformLlmConfig(config.id, dto);
      onSaved(updated);
      setForm((f) => ({ ...f, apiKey: "" }));
      toast.success(`"${name}" saved`);
    } catch (e) {
      toast.error(`Save failed: ${e}`);
    } finally {
      setSaving(false);
    }
  }

  async function remove() {
    if (!confirm(`Delete platform config "${config.name}"? Groups referencing it will lose access.`)) return;
    setDeleting(true);
    try {
      await api.deletePlatformLlmConfig(config.id);
      onDeleted(config.id);
      toast.success(`"${config.name}" deleted`);
    } catch (e) {
      toast.error(`Delete failed: ${e}`);
    } finally {
      setDeleting(false);
    }
  }

  return (
    <Card>
      <CardHeader
        className="cursor-pointer select-none flex flex-row items-center justify-between pb-2"
        onClick={() => setOpen((o) => !o)}
      >
        <div className="flex items-center gap-3">
          <CardTitle className="text-base">{config.name}</CardTitle>
          <Badge variant="outline" className="text-xs">{config.provider} · {config.model}</Badge>
          {config.apiKey && <Badge variant="secondary" className="text-xs">API key set</Badge>}
        </div>
        {open ? <ChevronUp className="size-4 text-muted-foreground" /> : <ChevronDown className="size-4 text-muted-foreground" />}
      </CardHeader>
      {open && (
        <CardContent className="pt-0 space-y-4">
          <div className="space-y-1.5">
            <Label>Config Name</Label>
            <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Anthropic Production" />
          </div>
          <LlmForm value={form} onChange={(p) => setForm((f) => ({ ...f, ...p }))} maskedApiKey={config.apiKey} />
          <div className="flex items-center gap-2 pt-1">
            <Button size="sm" onClick={save} disabled={saving || !form.provider || !form.model}>
              <Save className="size-3.5 mr-1.5" />
              {saving ? "Saving…" : "Save"}
            </Button>
            <Button size="sm" variant="destructive" onClick={remove} disabled={deleting}>
              <Trash2 className="size-3.5 mr-1.5" />
              {deleting ? "Deleting…" : "Delete"}
            </Button>
            <CardDescription className="ml-auto text-xs">
              Updated {new Date(config.updatedAt).toLocaleString()}
            </CardDescription>
          </div>
        </CardContent>
      )}
    </Card>
  );
}

// ── New config form ────────────────────────────────────────────────────────────

interface NewConfigFormProps {
  onCreated: (config: PlatformLlmConfigType) => void;
  onCancel: () => void;
}

function NewConfigForm({ onCreated, onCancel }: NewConfigFormProps) {
  const [name,   setName]   = useState("");
  const [form,   setForm]   = useState<UpsertLlmConfigDto>({ provider: "", apiKey: "", model: "", endpoint: "", deploymentName: "", availableModelsJson: "" });
  const [saving, setSaving] = useState(false);

  async function create() {
    if (!name.trim()) { toast.error("Name is required"); return; }
    setSaving(true);
    const dto: CreatePlatformLlmConfigDto = {
      name: name.trim(),
      provider:            form.provider            || undefined,
      apiKey:              form.apiKey              || undefined,
      model:               form.model               || undefined,
      endpoint:            form.endpoint            || undefined,
      deploymentName:      form.deploymentName      || undefined,
      availableModelsJson: form.availableModelsJson || undefined,
    };
    try {
      const created = await api.createPlatformLlmConfig(dto);
      onCreated(created);
      toast.success(`"${name}" created`);
    } catch (e) {
      toast.error(`Create failed: ${e}`);
    } finally {
      setSaving(false);
    }
  }

  return (
    <Card className="border-dashed">
      <CardHeader><CardTitle className="text-base">New Platform Config</CardTitle></CardHeader>
      <CardContent className="space-y-4">
        <div className="space-y-1.5">
          <Label>Config Name <span className="text-destructive">*</span></Label>
          <Input value={name} onChange={(e) => setName(e.target.value)} placeholder="e.g. Anthropic Production" />
        </div>
        <LlmForm value={form} onChange={(p) => setForm((f) => ({ ...f, ...p }))} />
        <div className="flex items-center gap-2">
          <Button size="sm" onClick={create} disabled={saving || !name.trim()}>
            <Save className="size-3.5 mr-1.5" />
            {saving ? "Creating…" : "Create"}
          </Button>
          <Button size="sm" variant="ghost" onClick={onCancel}>Cancel</Button>
        </div>
      </CardContent>
    </Card>
  );
}

// ── Page ───────────────────────────────────────────────────────────────────────

export function PlatformLlmConfig() {
  const [configs,  setConfigs]  = useState<PlatformLlmConfigType[]>([]);
  const [loading,  setLoading]  = useState(true);
  const [creating, setCreating] = useState(false);

  async function load() {
    try {
      setConfigs(await api.listPlatformLlmConfigs());
    } catch {
      toast.error("Failed to load platform LLM configs");
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => { load(); }, []);

  if (loading) {
    return (
      <div className="p-6 space-y-4">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-24 w-full" />
        <Skeleton className="h-24 w-full" />
      </div>
    );
  }

  return (
    <div className="p-6 space-y-6 max-w-2xl">
      <div className="flex items-start justify-between">
        <div>
          <h1 className="text-2xl font-semibold">Platform LLM Configs</h1>
          <p className="text-sm text-muted-foreground mt-1">
            Named provider configurations available to all groups. Groups can reference these without re-entering credentials.
          </p>
        </div>
        {!creating && (
          <Button size="sm" onClick={() => setCreating(true)}>
            <Plus className="size-4 mr-1.5" />
            New Config
          </Button>
        )}
      </div>

      {creating && (
        <NewConfigForm
          onCreated={(c) => { setConfigs((cs) => [...cs, c]); setCreating(false); }}
          onCancel={() => setCreating(false)}
        />
      )}

      {configs.length === 0 && !creating && (
        <p className="text-sm text-muted-foreground">
          No platform configs yet. Click "New Config" to add one.
        </p>
      )}

      {configs.map((c) => (
        <ConfigCard
          key={c.id}
          config={c}
          onSaved={(updated) => setConfigs((cs) => cs.map((x) => x.id === updated.id ? updated : x))}
          onDeleted={(id) => setConfigs((cs) => cs.filter((x) => x.id !== id))}
        />
      ))}
    </div>
  );
}
