import type { ReactNode } from "react";
import { ChevronLeft, ChevronRight, Search } from "lucide-react";
import { Card, CardContent } from "@/components/ui/card";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";

export interface ListToolbarProps {
  searchValue?: string;
  onSearchChange?: (value: string) => void;
  searchPlaceholder?: string;
  pageSize?: number;
  onPageSizeChange?: (pageSize: number) => void;
  pageSizeOptions?: number[];
  /**
   * Extra filter controls (Selects, checkboxes, etc.) rendered between the search box and the
   * page-size selector. This is also the slot the environment filter/switcher plugs into.
   */
  children?: ReactNode;
}

/**
 * Shared search + filter toolbar for admin-portal list pages. Extracted from the filter bar
 * originally duplicated ad hoc in SessionBrowser.tsx. Place above the list/table; pair with
 * `ListPagination` below it.
 */
export function ListToolbar({
  searchValue,
  onSearchChange,
  searchPlaceholder = "Search…",
  pageSize,
  onPageSizeChange,
  pageSizeOptions = [25, 50, 100],
  children,
}: ListToolbarProps) {
  return (
    <Card>
      <CardContent className="p-4 flex flex-wrap gap-3 items-center">
        {onSearchChange && (
          <div className="relative w-64">
            <Search className="absolute left-2.5 top-2.5 size-4 text-muted-foreground" />
            <Input
              placeholder={searchPlaceholder}
              className="pl-9"
              defaultValue={searchValue}
              onChange={e => onSearchChange(e.target.value)}
            />
          </div>
        )}
        {children}
        {onPageSizeChange && pageSize !== undefined && (
          <Select value={String(pageSize)} onValueChange={v => onPageSizeChange(Number(v))}>
            <SelectTrigger className="w-28 ml-auto">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {pageSizeOptions.map(n => (
                <SelectItem key={n} value={String(n)}>{n} / page</SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
      </CardContent>
    </Card>
  );
}

export interface ListPaginationProps {
  page: number;
  totalPages: number;
  totalCount: number;
  onPageChange: (page: number) => void;
  /** Plural noun shown next to the total count, e.g. "sessions", "agents". Defaults to "items". */
  itemLabel?: string;
}

/**
 * Shared Prev/Next + "Page X of Y · N total" pagination bar. Extracted from the pagination
 * controls originally duplicated ad hoc in SessionBrowser.tsx. Place below the list/table.
 */
export function ListPagination({ page, totalPages, totalCount, onPageChange, itemLabel = "items" }: ListPaginationProps) {
  if (totalCount <= 0) return null;
  return (
    <div className="flex items-center gap-2 text-sm">
      <Button variant="outline" size="sm" disabled={page <= 1} onClick={() => onPageChange(page - 1)}>
        <ChevronLeft className="size-4" /> Prev
      </Button>
      <span className="text-muted-foreground">
        Page {page} of {totalPages || 1} &nbsp;·&nbsp; {totalCount} {itemLabel}
      </span>
      <Button variant="outline" size="sm" disabled={page >= (totalPages || 1)} onClick={() => onPageChange(page + 1)}>
        Next <ChevronRight className="size-4" />
      </Button>
    </div>
  );
}
