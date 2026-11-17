import { CheckCheck, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { label, priorities } from "@/components/common";
import { categories, categoryIcon, type Category } from "./category";

/** Actions for the tasks ticked in a list's select mode; each applies to every ticked task in one go. */
export function BulkBar({ count, shown, busy, notice, onCategory, onPriority, onDone, onSelectAll, onClear, onExit }: {
  count: number; shown: number; busy: boolean; notice: string | null;
  onCategory: (category: Category) => void; onPriority: (priority: string) => void; onDone: () => void;
  onSelectAll: () => void; onClear: () => void; onExit: () => void;
}) {
  const none = count === 0 || busy;
  return <div role="toolbar" aria-label="Bulk actions" className="sticky top-0 z-10 flex flex-wrap items-center gap-2 rounded-xl border bg-card/95 px-3 py-2 shadow-sm backdrop-blur">
    <span className="text-sm font-medium tabular-nums" role="status" aria-live="polite">{count} selected</span>
    <Button type="button" variant="ghost" size="sm" disabled={busy || count === shown} onClick={onSelectAll}>Select all {shown}</Button>
    <Button type="button" variant="ghost" size="sm" disabled={busy || count === 0} onClick={onClear}>Clear</Button>
    <span className="mx-1 hidden h-5 w-px bg-border sm:block" aria-hidden="true"/>
    {categories.map(category => <Button key={category} type="button" variant="outline" size="sm" disabled={none} onClick={() => onCategory(category)} aria-label={`Make selected tasks ${category}`}>{categoryIcon[category]}{category}</Button>)}
    <NativeSelect aria-label="Set priority of selected tasks" disabled={none} value="" onChange={event => { if (event.target.value) onPriority(event.target.value); }}>
      <NativeSelectOption value="">Set priority…</NativeSelectOption>
      {priorities.map(priority => <NativeSelectOption key={priority} value={priority}>{label(priority)}</NativeSelectOption>)}
    </NativeSelect>
    <Button type="button" variant="outline" size="sm" disabled={none} onClick={onDone}><CheckCheck/>Mark done</Button>
    {notice && <span className="text-xs text-muted-foreground" role="status">{notice}</span>}
    <Button type="button" variant="ghost" size="sm" className="ml-auto" onClick={onExit}><X/>Done selecting</Button>
  </div>;
}
