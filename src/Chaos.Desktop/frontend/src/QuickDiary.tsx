import React from "react";
import { ContextMenu } from "radix-ui";
import { diaryRpc, gitHubRpc, type TaskResponse } from "#neoastra";
import { Button } from "@/components/ui/button";
import { MarkdownEditor } from "./MarkdownEditor";
import { Modal } from "./Sharing";

/** The "Add Diary" entry in a task row's context menu. */
export function QuickDiaryMenuItem({ busy, onSelect, separator = true }: { busy: boolean; onSelect: () => void; separator?: boolean }) {
  return (
    <>
      <ContextMenu.Item
        disabled={busy}
        className="cursor-default rounded-sm px-2 py-1.5 outline-none data-[highlighted]:bg-accent data-[disabled]:opacity-50"
        onSelect={onSelect}
      >
        Add Diary
      </ContextMenu.Item>
      {separator && <ContextMenu.Separator className="my-1 h-px bg-border" />}
    </>
  );
}

/**
 * A quick way to add a diary entry without opening the task. It adds the entry the same way the
 * task's Diary & history section does, including the "post to GitHub on sync" choice for linked tasks.
 */
export function QuickDiaryDialog({
  task,
  canChangeTask = true,
  api = diaryRpc,
  githubApi = gitHubRpc,
  onClose,
  onAdded,
}: {
  task: TaskResponse;
  canChangeTask?: boolean;
  api?: Pick<typeof diaryRpc, "add">;
  githubApi?: Pick<typeof gitHubRpc, "taskSync">;
  onClose: () => void;
  onAdded?: () => void;
}) {
  const [draft, setDraft] = React.useState("");
  const [connected, setConnected] = React.useState(false);
  const [postToGitHub, setPostToGitHub] = React.useState(false);
  const [working, setWorking] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  React.useEffect(() => {
    let active = true;
    void githubApi
      .taskSync({ taskId: task.id })
      .then((result) => {
        if (!active) return;
        const enabled = result.ok && result.connected && result.state === "Linked";
        setConnected(enabled);
        setPostToGitHub(enabled && result.postDiaryByDefault);
      })
      .catch(() => {
        if (active) setConnected(false);
      });
    return () => {
      active = false;
    };
  }, [githubApi, task.id]);

  async function submit(event: React.FormEvent) {
    event.preventDefault();
    if (working || !draft.trim()) return;
    setWorking(true);
    setError(null);
    try {
      const result = await api.add({
        operationId: crypto.randomUUID(),
        taskId: task.id,
        markdown: draft,
        entryAtUtc: null,
        postToGitHub: canChangeTask && connected && postToGitHub,
      });
      if (!result.ok) {
        setError(
          result.error?.fields.map((field) => field.message).join("; ") || result.error?.message || "Could not add the diary entry.",
        );
        return;
      }
      onAdded?.();
      onClose();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not add the diary entry.");
    } finally {
      setWorking(false);
    }
  }

  return (
    <Modal title={`Add diary entry to “${task.title}”`} onClose={onClose}>
      <form className="grid gap-3" onSubmit={(event) => void submit(event)}>
        <MarkdownEditor
          id="diary-quick"
          label="New diary entry"
          rows={4}
          value={draft}
          onChange={setDraft}
          disabled={working}
          placeholder="What happened? Markdown works here."
        />
        {connected && canChangeTask && (
          <label className="flex items-center gap-2 text-sm">
            <input type="checkbox" checked={postToGitHub} onChange={(e) => setPostToGitHub(e.target.checked)} disabled={working} />
            Also post to GitHub on sync
          </label>
        )}
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose} disabled={working}>
            Cancel
          </Button>
          <Button disabled={working || !draft.trim()}>Add entry</Button>
        </div>
      </form>
    </Modal>
  );
}
