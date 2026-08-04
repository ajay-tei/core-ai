import { Cpu } from "lucide-react";
import { TenantLlmConfigPanel } from "@/components/TenantDetail";

/**
 * Tenant self-service "LLM Config" Settings page. Reuses the same TenantLlmConfigPanel a master
 * admin sees from Platform → Tenants → [tenant] → LLM Config tab, but reachable directly by a
 * tenant admin from their own Settings nav (previously the panel existed only on the master-admin
 * -only /platform/tenants/:id route, so a tenant admin had no way to reach it at all).
 *
 * tenantId defaults to 1 like every other tenant-scoped api.ts call — EffectiveTenantId on the
 * backend overrides it with the caller's own JWT-derived TenantId for regular tenant users.
 */
export function TenantLlmConfigSettings() {
  return (
    <div className="p-6 space-y-6">
      <div>
        <h2 className="text-2xl font-bold flex items-center gap-2"><Cpu className="h-6 w-6" /> LLM Config</h2>
        <p className="text-sm text-muted-foreground">
          Named LLM configurations your agents can pin to — each can carry its own provider, model,
          API key, and environment tag.
        </p>
      </div>
      <TenantLlmConfigPanel tenantId={1} />
    </div>
  );
}
