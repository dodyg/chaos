import React from "react";
import { historyRpc, type TaskResponse } from "#neoastra";
import { Clock } from "lucide-react";
import { cn } from "@/lib/utils";

/** A query row can supply activity directly; other task surfaces read the task's history. */
export function TaskStaleness({ task, lastActivityAtUtc, revision = 0 }: { task: TaskResponse; lastActivityAtUtc?: string | null; revision?: number }) {
  const [history, setHistory] = React.useState<{ id: string; at: number } | null>(null);
  React.useEffect(() => {
    if (task.status !== "InProgress" || lastActivityAtUtc !== undefined) return;
    let current = true;
    setHistory(null);
    void historyRpc.list({ taskId: task.id }).then(result => {
      if (current && result.ok) setHistory({ id: task.id, at: Math.max(0, ...result.entries.map(entry => Date.parse(entry.effectiveAtUtc) || 0)) });
    }).catch(() => { if (current) setHistory(null); });
    return () => { current = false; };
  }, [task.id, task.status, task.version, lastActivityAtUtc, revision]);
  const activity = Math.max(Date.parse(task.updatedAtUtc) || 0, Date.parse(lastActivityAtUtc ?? "") || 0, history?.id === task.id ? history.at : 0);
  if (task.status !== "InProgress" || !activity) return null;
  const days = Math.max(0, Math.floor((Date.now() - activity) / 86_400_000));
  const description = days === 0 ? "Active today" : `Inactive ${days} ${days === 1 ? "day" : "days"}`;
  return <span role="img" aria-label={description} title={`${description} · Last activity: ${new Date(activity).toLocaleString()}`} className={cn("inline-flex items-center", days >= 3 ? "text-red-600 dark:text-red-400" : days >= 1 ? "text-amber-600 dark:text-amber-400" : "text-muted-foreground")}><Clock aria-hidden="true" className="size-3.5"/></span>;
}
