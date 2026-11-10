import React from "react";
import { ChevronRight, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Label } from "@/components/ui/label";
import { Alert, AlertDescription } from "@/components/ui/alert";

export const statuses = ["NotStarted", "InProgress", "WaitingOnSomeone", "NeedsReview", "Done", "Cancelled"];
export const priorities = ["None", "Low", "Normal", "High", "Urgent"];
export const label = (value: string) => value.replace(/([a-z])([A-Z])/g, "$1 $2");
export const isFinished = (status: string) => status === "Done" || status === "Cancelled";

/** A labelled form control: the label wraps the control so it keeps its accessible name. */
export function Field({ label: text, className, children }: { label: React.ReactNode; className?: string; children: React.ReactNode }) {
  return <Label className={cn("grid min-w-0 gap-1.5 text-xs font-medium text-muted-foreground [&_[data-slot=native-select-wrapper]]:w-full", className)}>{text}{children}</Label>;
}

/** Grouped date/time fields with a small uppercase caption. */
export function FieldGroup({ legend, className, children }: { legend: string; className?: string; children: React.ReactNode }) {
  return <fieldset className={cn("grid gap-3 rounded-lg border p-3 pt-2", className)}>
    <legend className="px-1 text-[0.7rem] font-semibold tracking-wider text-muted-foreground uppercase">{legend}</legend>
    {children}
  </fieldset>;
}

const statusTone: Record<string, string> = {
  InProgress: "bg-blue-500/15 text-blue-700 dark:text-blue-300",
  WaitingOnSomeone: "bg-amber-500/15 text-amber-700 dark:text-amber-300",
  NeedsReview: "bg-violet-500/15 text-violet-700 dark:text-violet-300",
  Done: "bg-emerald-500/15 text-emerald-700 dark:text-emerald-300",
  Cancelled: "bg-muted text-muted-foreground line-through",
};

export function StatusBadge({ status }: { status: string }) {
  if (status === "NotStarted") return null;
  return <Badge variant="secondary" className={statusTone[status]}>{label(status)}</Badge>;
}

export function PriorityBadge({ priority }: { priority: string }) {
  if (priority !== "High" && priority !== "Urgent") return null;
  return <Badge variant="secondary" className={priority === "Urgent" ? "bg-destructive/15 text-destructive" : "bg-orange-500/15 text-orange-700 dark:text-orange-300"}>{priority}</Badge>;
}

/** Count chip used beside headings. */
export function Count({ children }: { children: React.ReactNode }) {
  return <Badge variant="secondary" className="ml-2 text-muted-foreground tabular-nums">{children}</Badge>;
}

/** Dismissible error (role=alert) or warning (role=status) message. */
export function Banner({ tone, children, onDismiss }: { tone: "error" | "warning"; children: React.ReactNode; onDismiss?: () => void }) {
  return <Alert variant={tone === "error" ? "destructive" : "default"} role={tone === "error" ? "alert" : "status"}
    className={cn("flex items-center justify-between gap-3 py-2 pr-2", tone === "error" ? "border-destructive/40 bg-destructive/10" : "border-warning-foreground/30 bg-warning text-warning-foreground")}>
    <span>{children}</span>
    {onDismiss && <Button type="button" variant="ghost" size="icon-sm" className="text-current hover:bg-black/5 dark:hover:bg-white/10" aria-label={`Dismiss ${tone}`} onClick={onDismiss}><X/></Button>}
  </Alert>;
}

/** Inline error message for embedded panels. */
export function InlineError({ children }: { children: React.ReactNode }) {
  return <Alert variant="destructive" className="border-destructive/40 bg-destructive/10"><AlertDescription>{children}</AlertDescription></Alert>;
}

/** Collapsible list of soft-deleted items with a Restore action each. */
export function DeletedItems<T extends { id: string }>({ noun, items, name, busy, onRestore, className }: { noun: string; items: T[]; name: (item: T) => string; busy: boolean; onRestore: (item: T) => void; className?: string }) {
  if (items.length === 0) return null;
  return <details className={cn("group/trash", className)}>
    <summary className="flex cursor-pointer list-none items-center gap-1 rounded-md px-1 py-1.5 text-xs text-muted-foreground select-none hover:text-foreground [&::-webkit-details-marker]:hidden">
      <ChevronRight className="size-3.5 transition-transform group-open/trash:rotate-90"/>Deleted {noun} ({items.length})
    </summary>
    <ul className="grid gap-0.5 pt-1 pl-4">{items.map(item => <li key={item.id} className="flex items-center justify-between gap-2 rounded-md px-1.5 py-0.5 text-muted-foreground hover:bg-accent">
      <span className="truncate">{name(item)}</span>
      <Button type="button" variant="link" size="xs" disabled={busy} onClick={() => onRestore(item)}>Restore</Button>
    </li>)}</ul>
  </details>;
}

export function EmptyState({ title, children }: { title: string; children: React.ReactNode }) {
  return <div className="grid gap-1 rounded-xl border border-dashed px-4 py-10 text-center">
    <p className="font-medium">{title}</p>
    <p className="text-muted-foreground">{children}</p>
  </div>;
}

/** Page title block shared by the task list and templates views. */
export function PageHeader({ eyebrow, title, titleId, subtitle, actions }: { eyebrow: string; title: string; titleId?: string; subtitle: React.ReactNode; actions?: React.ReactNode }) {
  return <header className="flex flex-wrap items-end justify-between gap-4">
    <div className="grid gap-1">
      <p className="text-[0.7rem] font-semibold tracking-wider text-primary uppercase">{eyebrow}</p>
      <h2 id={titleId} className="text-3xl font-bold tracking-tight">{title}</h2>
      <p className="text-muted-foreground">{subtitle}</p>
    </div>
    {actions && <div className="flex gap-2">{actions}</div>}
  </header>;
}
