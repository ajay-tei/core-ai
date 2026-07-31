// react-refresh/only-export-components: this file intentionally colocates the context provider
// component with its companion hook (same pattern already used by components/ui/sidebar.tsx) —
// splitting a small context+hook pair into two files for Fast Refresh purity isn't worth the
// indirection here.
/* eslint-disable react-refresh/only-export-components */
import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { useSearchParams } from "react-router";
import { api, getStoredEnvironmentId, setStoredEnvironmentId, type TenantEnvironment } from "@/api";

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
    setLoading(true);
    api.listEnvironments()
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
