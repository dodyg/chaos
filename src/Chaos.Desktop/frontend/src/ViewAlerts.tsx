import React from "react";
import { useActivity } from "./activity";
import { viewRpc, type ViewAlertDto } from "#neoastra";
import { ExternalLink, Funnel, X } from "lucide-react";
import { Button } from "@/components/ui/button";

export type ViewAlertApi = Pick<typeof viewRpc, "alerts" | "acknowledgeAlert">;

/**
 * Alerts from custom views (PLAN2 §11.2): "3 new tasks in My open bugs". The host evaluates each
 * alerting view and delivers what is due after quiet hours; it learns of delivered ones from the activity stream, like the
 * reminder alerts, and shows them inside the app. Open goes to the view; dismissing acknowledges it.
 */
export function ViewAlertStack({ api = viewRpc, onOpen }: { api?: ViewAlertApi; onOpen: (viewId: string) => void }) {
  const [alerts, setAlerts] = React.useState<ViewAlertDto[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [working, setWorking] = React.useState<string | null>(null);

  const load = React.useCallback(async () => {
    try {
      const result = await api.alerts({});
      if (result.ok) setAlerts([...result.alerts]);
    } catch {
      /* the next poll retries; alerts never block anything */
    }
  }, [api]);

  React.useEffect(() => {
    void load();
    function wake() {
      if (document.visibilityState === "visible") void load();
    }
    window.addEventListener("focus", wake);
    document.addEventListener("visibilitychange", wake);
    return () => {
      window.removeEventListener("focus", wake);
      document.removeEventListener("visibilitychange", wake);
    };
  }, [load]);
  useActivity(["view_alert"], () => void load());

  async function acknowledge(alert: ViewAlertDto, open: boolean) {
    if (working) return;
    setWorking(alert.id);
    setError(null);
    try {
      const result = await api.acknowledgeAlert({ alertId: alert.id });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not dismiss the alert.");
      if (open && alert.viewAvailable) onOpen(alert.viewId);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not dismiss the alert.");
    } finally {
      setWorking(null);
    }
  }

  if (alerts.length === 0 && !error) return null;
  return (
    <section aria-label="View alerts" className="fixed top-4 right-4 z-30 grid w-[min(24rem,calc(100vw-2rem))] gap-2">
      {error && (
        <div role="alert" className="flex items-center gap-2 rounded-xl border bg-popover px-3 py-2 text-sm shadow-lg">
          {error}
          <Button
            type="button"
            variant="ghost"
            size="icon-sm"
            className="ml-auto"
            aria-label="Dismiss message"
            onClick={() => setError(null)}
          >
            <X />
          </Button>
        </div>
      )}
      {alerts.slice(0, 3).map((alert) => (
        <article
          key={alert.id}
          aria-label={`View alert: ${alert.viewName}`}
          className="grid gap-2 rounded-xl border bg-popover p-3 text-sm shadow-lg"
        >
          <div className="flex items-start gap-2">
            <Funnel className="mt-0.5 size-4 shrink-0 text-primary" />
            <div className="grid min-w-0 flex-1 gap-0.5">
              <span className="font-medium [overflow-wrap:anywhere]">
                {alert.taskCount} new {alert.taskCount === 1 ? "task" : "tasks"} in <bdi>{alert.viewName}</bdi>
              </span>
              {alert.firstTaskTitle && (
                <span className="text-xs text-muted-foreground [overflow-wrap:anywhere]">
                  <bdi>{alert.firstTaskTitle}</bdi>
                  {alert.taskCount > 1 ? ` and ${alert.taskCount - 1} more` : ""}
                </span>
              )}
              {!alert.viewAvailable && <span className="text-xs text-muted-foreground">This view was deleted.</span>}
            </div>
          </div>
          <div className="flex flex-wrap gap-1">
            {alert.viewAvailable && (
              <Button type="button" size="xs" disabled={working !== null} onClick={() => void acknowledge(alert, true)}>
                <ExternalLink />
                Open view
              </Button>
            )}
            <Button
              type="button"
              size="xs"
              variant="outline"
              disabled={working !== null}
              aria-label={`Dismiss alert for ${alert.viewName}`}
              onClick={() => void acknowledge(alert, false)}
            >
              Dismiss
            </Button>
          </div>
        </article>
      ))}
      {alerts.length > 3 && (
        <p className="rounded-xl border bg-popover px-3 py-1.5 text-xs shadow-lg">{alerts.length - 3} more view alerts</p>
      )}
    </section>
  );
}
