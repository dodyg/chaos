import React from "react";
import { gitHubRpc, markdownRpc, type GitHubTaskSyncResponse } from "#neoastra";

/** Local metadata only: opening a task never starts authentication or a sync pass. */
export function GitHubTaskNotice({ taskId, version, revision, api = gitHubRpc, onStatus, onChanged }: { taskId: string; version: string; revision: number; api?: typeof gitHubRpc; onStatus?: (taskId: string, info: GitHubTaskSyncResponse | null) => void; onChanged?: () => void }) {
  const [info, setInfo] = React.useState<GitHubTaskSyncResponse | null>(null);
  const [failed, setFailed] = React.useState(false);
  const [working, setWorking] = React.useState(false);
  const [message, setMessage] = React.useState("");
  const selectedTask = React.useRef(taskId);
  selectedTask.current = taskId;
  React.useEffect(() => {
    let current = true;
    setInfo(null); setFailed(false); setWorking(false); setMessage("");
    void api.taskSync({ taskId }).then(result => { if (current) { setInfo(result); setFailed(!result.ok); } })
      .catch(() => { if (current) setFailed(true); });
    return () => { current = false; };
  }, [api, taskId, version, revision]);
  React.useEffect(() => { onStatus?.(taskId, info); }, [onStatus, taskId, info]);
  if (failed) return <p role="status" className="text-xs text-muted-foreground">Could not check GitHub sync status.</p>;
  if (!info?.connected) return null;
  async function action(kind: "retryTask" | "dismissConflict" | "keepAsOrdinaryTask") {
    if (!info?.linkVersion) return;
    setWorking(true); setMessage("");
    try {
      const result = await api[kind]({ operationId: crypto.randomUUID(), taskId, expectedVersion: info.linkVersion });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not update GitHub sync.");
      const refreshed = await api.taskSync({ taskId });
      if (selectedTask.current === taskId) {
        setInfo(refreshed); setFailed(!refreshed.ok);
        if (kind === "retryTask") setMessage("Ready to retry. Use Sync now in GitHub settings.");
        onChanged?.();
      }
    } catch (error) { if (selectedTask.current === taskId) setMessage(error instanceof Error ? error.message : "Could not update GitHub sync."); }
    finally { if (selectedTask.current === taskId) setWorking(false); }
  }
  async function assign() {
    if (!info || info.assignedToMe === null) return;
    setWorking(true); setMessage("");
    try {
      const assigned = !info.assignedToMe;
      const result = await api.setSelfAssignment({ operationId: crypto.randomUUID(), taskId, assigned });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not change assignment.");
      const refreshed = await api.taskSync({ taskId });
      if (selectedTask.current === taskId) { setInfo(refreshed); setFailed(!refreshed.ok); }
    } catch (error) { if (selectedTask.current === taskId) setMessage(error instanceof Error ? error.message : "Could not change assignment."); }
    finally { if (selectedTask.current === taskId) setWorking(false); }
  }
  return <div role="status" aria-label="GitHub sync status" className="grid gap-1 rounded-lg border bg-muted/50 px-3 py-2 text-xs">
    <span>GitHub · {info.state}</span>
    {info.repository && <span dir="auto">{info.repository}</span>}
    {info.issueUrl && <button type="button" className="w-fit underline" onClick={() => {
      void markdownRpc.openExternal({ url: info.issueUrl! }).then(result => { if (!result.ok && selectedTask.current === taskId) setMessage(result.error?.message ?? "Could not open the issue."); })
        .catch(() => { if (selectedTask.current === taskId) setMessage("Could not open the issue."); });
    }}>Open issue on GitHub</button>}
    {info.pending && <strong>Not sent to GitHub yet.</strong>}
    {info.state === "Out of scope" && <span>This issue no longer matches the repository filter. Sync resumes if it matches again.</span>}
    {info.state === "Unavailable" && <span>The issue cannot be reached. Your task is kept here.</span>}
    {info.syncError && <span>{info.syncError}</span>}
    {info.hasConflict && <div><p>GitHub’s value was kept. Your local value is saved in task history.</p><button type="button" className="underline" disabled={working} onClick={() => void action("dismissConflict")}>Dismiss conflict</button></div>}
    {(info.syncError || info.state === "Unavailable") && <button type="button" className="w-fit underline" disabled={working} onClick={() => void action("retryTask")}>Retry</button>}
    {info.assignedToMe !== null && info.state === "Linked" && <button type="button" className="w-fit underline" disabled={working} onClick={() => void assign()}>{info.assignedToMe ? "Unassign me" : "Assign me"}</button>}
    {message && <span>{message}</span>}
    <button type="button" className="w-fit underline" disabled={working} onClick={() => void action("keepAsOrdinaryTask")}>Keep as ordinary task</button>
    <span className="text-muted-foreground">Sync sends issue changes and selected diary entries.</span>
  </div>;
}
