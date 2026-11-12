import React from "react";
import { Eye } from "lucide-react";
import type { TaskResponse } from "#neoastra";
import { Button } from "@/components/ui/button";

export const TaskViewContext = React.createContext<((task: TaskResponse | string) => void) | null>(null);

export function TaskViewAction({ task, title, disabled = false }: { task: TaskResponse | string; title: string; disabled?: boolean }) {
  const view = React.useContext(TaskViewContext);
  if (!view) return null;
  return <Button type="button" variant="ghost" size="icon-sm" className="text-muted-foreground" disabled={disabled} aria-label={`View ${title}`} title="View" onClick={() => view(task)}><Eye/></Button>;
}
