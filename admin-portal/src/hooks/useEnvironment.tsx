// react-refresh/only-export-components: this file intentionally colocates the context provider
// component with its companion hook (same pattern already used by components/ui/sidebar.tsx) —
// splitting a small context+hook pair into two files for Fast Refresh purity isn't worth the
// indirection here.
/* eslint-disable react-refresh/only-export-components */
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useSearchParams } from "react-router";
import { api, getStoredEnvironmentId, setStoredEnvironmentId, type TenantEnvironment } from "@/api";
import { auth } from "@/lib/auth";

interface EnvironmentContextValue
{
  environments: TenantEnvironment[];
  currentEnvironmentId: number | null;
  currentEnvironment: TenantEnvironment | null;
  /** True when currentEnvironment has the highest Rank among all of the tenant's environments
   *  (i.e. nothing ranked above it — "top of pipeline", usually Production). */
  isTopOfPipeline: boolean;
  setCurrentEnvironmentId: (id: number) => void;
  loading: boolean;
  reload: () => void;
}

const EnvironmentContext = createContext<EnvironmentContextValue | null>(null);

/** Reads/updates the currently-selected environment (Phase F environment switcher). Must be used
 *  within an <EnvironmentProvider> (mounted once, inside RootLayout, above Topbar + all pages). */
export function useEnvironment(): EnvironmentContextValue
{
  const ctx = useContext(EnvironmentContext);
  if (!ctx) throw new Error("useEnvironment must be used within an EnvironmentProvider.");
  return ctx;
}

export function EnvironmentProvider({ children }: { children: ReactNode; })
{
  const [environments, setEnvironments] = useState<TenantEnvironment[]>([]);
  const [currentEnvironmentId, setCurrentEnvironmentIdState] = useState<number | null>(null);
  const [loading, setLoading] = useState(true);
  const [searchParams, setSearchParams] = useSearchParams();

  const load = () =>
  {
    // Master admins have no single tenant context — TenantEnvironmentEntity rows are tenant-scoped,
    // so there's no coherent "current environment" for someone operating across every tenant.
    // Fetching here defaulted to Tenant 1's environments (api.listEnvironments()'s tenantId=1
    // default), which was both meaningless for a master admin and, worse, got persisted via
    // setStoredEnvironmentId and re-sent as X-Environment on every subsequent request — including
    // ones managing a COMPLETELY UNRELATED tenant. Clear any stale value and skip the fetch.
    if (auth.isMasterAdmin())
    {
      setStoredEnvironmentId(null);
      setEnvironments([]);
      setCurrentEnvironmentIdState(null);
      setLoading(false);
      return;
    }

    setLoading(true);
    // Non-admins can't read the admin-only environment list (would 403) — they get a narrower,
    // read-only endpoint that returns just their own server-resolved environment (always 0 or 1
    // item today). Sharing this same resolution/state logic below means the switcher needs zero
    // changes if a future multi-environment-access feature ever returns more than one.
    const fetchEnvironments = auth.isAdmin() ? api.listEnvironments() : api.getAccessibleEnvironments();
    fetchEnvironments
      .then((envs) =>
      {
        setEnvironments(envs);
        if (envs.length === 0) return;

        // Resolution order: ?env= URL slug (deep-linkable) > localStorage (last-used) >
        // lowest-rank environment (first-ever visit — never default to Production/highest-rank,
        // to avoid landing on live data by accident).
        const urlSlug = searchParams.get("env");
        const fromUrl = urlSlug ? envs.find((e) => e.slug === urlSlug) : undefined;
        const storedId = getStoredEnvironmentId();
        const fromStorage = storedId ? envs.find((e) => e.id === storedId) : undefined;
        const lowestRank = [...envs].sort((a, b) => a.rank - b.rank)[0];
        const resolved = fromUrl ?? fromStorage ?? lowestRank;

        setCurrentEnvironmentIdState(resolved.id);
        setStoredEnvironmentId(resolved.id);
      })
      .catch(() => { /* endpoint unreachable or tenant has no environments yet — fail soft */ })
      .finally(() => setLoading(false));
  };

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(load, []);

  const setCurrentEnvironmentId = (id: number) =>
  {
    setCurrentEnvironmentIdState(id);
    setStoredEnvironmentId(id);
    const env = environments.find((e) => e.id === id);
    const next = new URLSearchParams(searchParams);
    if (env) next.set("env", env.slug); else next.delete("env");
    setSearchParams(next, { replace: true });
  };

  const currentEnvironment = useMemo(
    () => environments.find((e) => e.id === currentEnvironmentId) ?? null,
    [environments, currentEnvironmentId],
  );

  const isTopOfPipeline = useMemo(() =>
  {
    if (!currentEnvironment || environments.length === 0) return false;
    const maxRank = Math.max(...environments.map((e) => e.rank));
    return currentEnvironment.rank === maxRank;
  }, [currentEnvironment, environments]);

  return (
    <EnvironmentContext.Provider
      value={{
        environments, currentEnvironmentId, currentEnvironment, isTopOfPipeline,
        setCurrentEnvironmentId, loading, reload: load,
      }}
    >
      {children}
    </EnvironmentContext.Provider>
  );
}
