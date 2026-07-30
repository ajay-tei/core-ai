import { useCallback, useEffect, useRef, useState } from "react";
import type { PagedResult } from "../api";

export interface UsePagedListResult<T, TParams>
{
    /** Latest successfully-loaded page, or null before the first response arrives. */
    result: PagedResult<T> | null;
    loading: boolean;
    error: string | null;
    /** Current query params (page/pageSize + whatever filters the caller defined). */
    params: TParams;
    /** Apply a filter/search patch immediately and reset to page 1. */
    update: (patch: Partial<TParams>) => void;
    /** Same as `update`, but debounced (~300ms default) — use for free-text search inputs. */
    updateDebounced: (patch: Partial<TParams>) => void;
    /** Change page only (does not reset to page 1). */
    setPage: (page: number) => void;
    /** Re-run the fetch with the current params (e.g. after a mutation/delete). */
    reload: () => void;
}

/**
 * Shared server-side pagination + search state, modeled on SessionBrowser.tsx's original
 * params/load/update/setPage pattern. Generic over the entity type `T` and the params shape
 * `TParams`, which must at minimum carry `page`/`pageSize`.
 */
export function usePagedList<T, TParams extends { page?: number; pageSize?: number; }>(
    fetchFn: (params: TParams) => Promise<PagedResult<T>>,
    initialParams: TParams,
    debounceMs = 300,
): UsePagedListResult<T, TParams>
{
    const [result, setResult] = useState<PagedResult<T> | null>(null);
    const [loading, setLoading] = useState(true);
    const [error, setError] = useState<string | null>(null);
    const [params, setParams] = useState<TParams>(initialParams);
    const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
    const fetchFnRef = useRef(fetchFn);
    fetchFnRef.current = fetchFn;

    const load = useCallback((p: TParams) =>
    {
        setLoading(true);
        setError(null);
        fetchFnRef.current(p)
            .then(setResult)
            .catch(e => setError(String(e)))
            .finally(() => setLoading(false));
    }, []);

    useEffect(() =>
    {
        load(params);
        // Only re-fetch when params actually change; `load`/`fetchFn` identity is stable via refs.
        // eslint-disable-next-line react-hooks/exhaustive-deps
    }, [params]);

    useEffect(() => () =>
    {
        if (debounceRef.current) clearTimeout(debounceRef.current);
    }, []);

    const update = useCallback((patch: Partial<TParams>) =>
    {
        setParams(prev => ({ ...prev, ...patch, page: 1 }));
    }, []);

    const updateDebounced = useCallback((patch: Partial<TParams>) =>
    {
        if (debounceRef.current) clearTimeout(debounceRef.current);
        debounceRef.current = setTimeout(() => update(patch), debounceMs);
    }, [update, debounceMs]);

    const setPage = useCallback((page: number) =>
    {
        setParams(prev => ({ ...prev, page }));
    }, []);

    const reload = useCallback(() => load(params), [load, params]);

    return { result, loading, error, params, update, updateDebounced, setPage, reload };
}
