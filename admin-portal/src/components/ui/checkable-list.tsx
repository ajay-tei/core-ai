import { useState, useMemo } from "react";
import { Input } from "@/components/ui/input";
import { ScrollArea } from "@/components/ui/scroll-area";
import { Search, Check } from "lucide-react";

export interface PickerOption
{
  value: string;
  primary: string;
  secondary?: string;
}

export interface CheckableListProps
{
  options: PickerOption[];
  selected: string[];
  onToggle: (value: string) => void;
  onSelectAll: (values: string[]) => void;
  onClear: () => void;
  searchPlaceholder: string;
  emptyText: string;
}

/** Searchable, multi-select checkbox list with select-all/clear — shared by any ACL editor
 *  that grants access via a picked set of ids (agent groups, environments, ...). */
export function CheckableList({
  options,
  selected,
  onToggle,
  onSelectAll,
  onClear,
  searchPlaceholder,
  emptyText,
}: CheckableListProps)
{
  const [query, setQuery] = useState("");

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return options;
    return options.filter(
      (o) =>
        o.primary.toLowerCase().includes(q) ||
        (o.secondary?.toLowerCase().includes(q) ?? false) ||
        o.value.toLowerCase().includes(q),
    );
  }, [options, query]);

  const selectedSet = useMemo(() => new Set(selected), [selected]);

  if (options.length === 0) {
    return <span className="text-xs text-muted-foreground">{emptyText}</span>;
  }

  return (
    <div className="rounded-md border">
      <div className="flex items-center gap-2 border-b p-2">
        <div className="relative flex-1">
          <Search className="absolute left-2 top-1/2 h-3.5 w-3.5 -translate-y-1/2 text-muted-foreground" />
          <Input
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder={searchPlaceholder}
            className="h-8 pl-7"
          />
        </div>
        <span className="whitespace-nowrap text-xs text-muted-foreground">{selected.length} selected</span>
      </div>
      <div className="flex items-center gap-2 border-b px-2 py-1.5 text-xs">
        <button
          type="button"
          className="text-primary hover:underline disabled:opacity-50"
          disabled={filtered.length === 0}
          onClick={() => onSelectAll(filtered.map((o) => o.value))}
        >
          Select all{query.trim() ? " filtered" : ""} ({filtered.length})
        </button>
        <span className="text-muted-foreground">·</span>
        <button
          type="button"
          className="text-primary hover:underline disabled:opacity-50"
          disabled={selected.length === 0}
          onClick={onClear}
        >
          Clear
        </button>
      </div>
      <ScrollArea className="h-52">
        <div className="p-1">
          {filtered.length === 0 ? (
            <div className="px-2 py-6 text-center text-xs text-muted-foreground">No matches.</div>
          ) : (
            filtered.map((o) => {
              const isSelected = selectedSet.has(o.value);
              return (
                <button
                  key={o.value}
                  type="button"
                  onClick={() => onToggle(o.value)}
                  className="flex w-full items-center gap-2 rounded-sm px-2 py-1.5 text-left text-sm hover:bg-accent"
                >
                  <span
                    className={`flex h-4 w-4 shrink-0 items-center justify-center rounded border ${
                      isSelected ? "border-primary bg-primary text-primary-foreground" : "border-input"
                    }`}
                  >
                    {isSelected && <Check className="h-3 w-3" />}
                  </span>
                  <span className="min-w-0 flex-1 truncate">{o.primary}</span>
                  {o.secondary && (
                    <span className="max-w-[45%] truncate text-xs text-muted-foreground">{o.secondary}</span>
                  )}
                </button>
              );
            })
          )}
        </div>
      </ScrollArea>
    </div>
  );
}
