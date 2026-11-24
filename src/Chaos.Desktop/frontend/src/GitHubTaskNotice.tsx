import React from "react";
import { ChevronRight, RefreshCw } from "lucide-react";
import { gitHubRpc, markdownRpc, type GitHubTaskSyncResponse } from "#neoastra";
import { useActivity, useJobChange } from "./activity";

/** Local metadata only: opening a task never starts authentication or a sync pass. */
export function GitHubTaskNotice({
  taskId,
  version,
  revision,
  api = gitHubRpc,
  onStatus,
  onChanged,
  onSynced,
}: {
  taskId: string;
  version: string;
  revision: number;
  api?: typeof gitHubRpc;
  onStatus?: (taskId: string, info: GitHubTaskSyncResponse | null) => void;
  onChanged?: () => void;
  onSynced?: () => void;
}) {
  const [info, setInfo] = React.useState<GitHubTaskSyncResponse | null>(null);
  const [failed, setFailed] = React.useState(false);
  const [working, setWorking] = React.useState(false);
  const [message, setMessage] = React.useState("");
  const selectedTask = React.useRef(taskId);
  const onSyncedRef = React.useRef(onSynced);
  onSyncedRef.current = onSynced;
  const previousInfo = React.useRef<{ taskId: string; info: GitHubTaskSyncResponse } | null>(null);
  selectedTask.current = taskId;
  React.useEffect(() => {
    let current = true;
    setInfo(null);
    setFailed(false);
    setWorking(false);
    setMessage("");
    void api
      .taskSync({ taskId })
      .then((result) => {
        if (current) {
          setInfo(result);
          setFailed(!result.ok);
        }
      })
      .catch(() => {
        if (current) setFailed(true);
      });
    return () => {
      current = false;
    };
  }, [api, taskId, version, revision]);
  // Background sync passes (pushed by the host) refresh this task's status; no polling while it syncs.
  const refreshStatus = React.useCallback(() => {
    void api
      .taskSync({ taskId: selectedTask.current })
      .then((result) => {
        setInfo(result);
        setFailed(!result.ok);
      })
      .catch(() => setFailed(true));
  }, [api]);
  useJobChange("github.sync", refreshStatus);
  useActivity([], refreshStatus);
  React.useEffect(() => {
    if (!info?.ok) return;
    const previous = previousInfo.current;
    previousInfo.current = { taskId, info };
    if (
      previous?.taskId === taskId &&
      !info.syncing &&
      (previous.info.syncing || (!info.pending && (previous.info.pending || info.linkVersion !== previous.info.linkVersion)))
    )
      onSyncedRef.current?.();
  }, [taskId, info]);
  React.useEffect(() => {
    onStatus?.(taskId, info);
  }, [onStatus, taskId, info]);
  if (failed)
    return (
      <p role="status" className="text-xs text-muted-foreground">
        Could not check GitHub sync status.
      </p>
    );
  if (!info?.connected) return null;
  function openExternal(url: string) {
    void markdownRpc
      .openExternal({ url })
      .then((result) => {
        if (!result.ok && selectedTask.current === taskId) setMessage(result.error?.message ?? "Could not open the link.");
      })
      .catch(() => {
        if (selectedTask.current === taskId) setMessage("Could not open the link.");
      });
  }
  async function action(kind: "retryTask" | "dismissConflict" | "keepAsOrdinaryTask") {
    if (!info?.linkVersion) return;
    setWorking(true);
    setMessage("");
    try {
      const result = await api[kind]({ operationId: crypto.randomUUID(), taskId, expectedVersion: info.linkVersion });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not update GitHub sync.");
      const refreshed = await api.taskSync({ taskId });
      if (selectedTask.current === taskId) {
        setInfo(refreshed);
        setFailed(!refreshed.ok);
        if (kind === "retryTask") setMessage("Issue queued for background sync.");
        onChanged?.();
      }
    } catch (error) {
      if (selectedTask.current === taskId) setMessage(error instanceof Error ? error.message : "Could not update GitHub sync.");
    } finally {
      if (selectedTask.current === taskId) setWorking(false);
    }
  }
  async function assign() {
    if (!info || info.assignedToMe === null) return;
    setWorking(true);
    setMessage("");
    try {
      const assigned = !info.assignedToMe;
      const result = await api.setSelfAssignment({ operationId: crypto.randomUUID(), taskId, assigned });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not change assignment.");
      const refreshed = await api.taskSync({ taskId });
      if (selectedTask.current === taskId) {
        setInfo(refreshed);
        setFailed(!refreshed.ok);
      }
    } catch (error) {
      if (selectedTask.current === taskId) setMessage(error instanceof Error ? error.message : "Could not change assignment.");
    } finally {
      if (selectedTask.current === taskId) setWorking(false);
    }
  }
  return (
    <div role="status" aria-label="GitHub sync status" className="grid gap-1 rounded-lg border bg-muted/50 px-3 py-2 text-xs">
      <details className="group/gh grid gap-1">
        <summary className="flex cursor-pointer list-none items-center gap-2 select-none [&::-webkit-details-marker]:hidden">
          <ChevronRight aria-hidden="true" className="size-3.5 text-muted-foreground transition-transform group-open/gh:rotate-90" />
          <span>GitHub · {info.state}</span>
          {info.syncing && (
            <strong className="flex items-center gap-2">
              <RefreshCw aria-hidden="true" className="size-3.5 motion-safe:animate-spin" />
              Syncing this issue…
            </strong>
          )}
        </summary>
        <div className="mt-1 grid gap-1">
          {info.repository && <span dir="auto">{info.repository}</span>}
          {info.assignees && <span dir="auto">{info.assignees.length ? `Assigned to ${info.assignees.join(", ")}` : "Unassigned"}</span>}
          {info.issueUrl && (
            <button type="button" className="w-fit underline" onClick={() => openExternal(info.issueUrl!)}>
              Open issue on GitHub
            </button>
          )}
          {info.pullRequests && info.pullRequests.length > 0 && (
            <ul aria-label="Linked pull requests" className="grid gap-0.5">
              {info.pullRequests.map((pull) => (
                <li key={pull.repository + "#" + pull.number} className="flex flex-wrap gap-x-1">
                  <button type="button" className="w-fit underline" onClick={() => openExternal(pull.url)}>
                    {pull.repository}#{pull.number} · {pull.state}
                    {pull.review !== "None" ? ` · ${pull.review}` : ""}
                  </button>
                  <span dir="auto">{pull.title}</span>
                </li>
              ))}
            </ul>
          )}
          {info.assignedToMe !== null && info.state === "Linked" && (
            <button type="button" className="w-fit underline" disabled={working} onClick={() => void assign()}>
              {info.assignedToMe ? "Unassign me" : "Assign me"}
            </button>
          )}
          {info.unsentAssignees && info.unsentAssignees.length > 0 && (
            <span>
              Not sent to GitHub: {info.unsentAssignees.join(", ")} {info.unsentAssignees.length === 1 ? "has" : "have"} no GitHub login
              set.
            </span>
          )}
          {info.commentsSigned && info.postedAs && (
            <span className="text-muted-foreground">Comments you post go to GitHub as @{info.postedAs}, signed with your name.</span>
          )}
          {info.canManage && (
            <button type="button" className="w-fit underline" disabled={working} onClick={() => void action("keepAsOrdinaryTask")}>
              Keep as ordinary task
            </button>
          )}
          <span className="text-muted-foreground">Sync sends issue changes and selected diary entries.</span>
        </div>
      </details>
      {info.pending && <strong>Not sent to GitHub yet.</strong>}
      {info.state === "Out of scope" && <span>This issue no longer matches the repository filter. Sync resumes if it matches again.</span>}
      {info.state === "Unavailable" && <span>The issue cannot be reached. Your task is kept here.</span>}
      {info.syncError && <span>{info.syncError}</span>}
      {info.hasConflict && (
        <div>
          <p>GitHub’s value was kept. Your local value is saved in task history.</p>
          <button type="button" className="underline" disabled={working} onClick={() => void action("dismissConflict")}>
            Dismiss conflict
          </button>
        </div>
      )}
      {(info.syncError || info.state === "Unavailable") && (
        <button type="button" className="w-fit underline" disabled={working} onClick={() => void action("retryTask")}>
          Retry
        </button>
      )}
      {message && <span>{message}</span>}
    </div>
  );
}
