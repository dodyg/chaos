import { cn } from "@/lib/utils";
import { isFinished } from "@/components/common";

/** The shared look of a task or subtask row, selected and finished states included. */
export function rowClass(selected: boolean, status: string, compact = false) {
  return cn("group/row flex items-center gap-2 rounded-lg border bg-card py-1 pr-1.5 pl-1 shadow-xs transition-colors hover:border-input",
    compact ? "min-h-9" : "min-h-11",
    selected && "border-primary ring-1 ring-primary hover:border-primary",
    isFinished(status) && "[&_[data-slot=row-title]]:text-muted-foreground [&_[data-slot=row-title]]:line-through");
}
