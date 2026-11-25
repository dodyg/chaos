import React from "react";
import { agentRpc, claimRpc, peopleRpc, type AgentViewDto, type ClaimResponse, type PersonResponse, type TaskResponse } from "#neoastra";
import { Bot, UserRound } from "lucide-react";
import { Button } from "@/components/ui/button";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Field, InlineError, isFinished } from "@/components/common";
import { ago } from "./SyncStatus";

/** Whether a task's claim still holds (PLAN3 12.4): one past its expiry counts as released until the sweep writes that. */
export function claimLive(task: Pick<TaskResponse, "claimedById" | "claimExpiresAtUtc">, now = Date.now()): boolean {
  if (!task.claimedById) return false;
  const expires = Date.parse(task.claimExpiresAtUtc ?? "");
  return !Number.isFinite(expires) || expires > now;
}

/**
 * The person or agent holding a task's claim, read from the space's directory (agent ids resolve there too);
 * null while it loads, when the lookup fails, and for a task nobody holds.
 */
export function useClaimant(task: TaskResponse, peopleApi: Pick<typeof peopleRpc, "get"> = peopleRpc) {
  const [state, setState] = React.useState<{ key: string; person: PersonResponse | null }>({ key: "", person: null });
  // The latest client, read inside the effect: a caller passing an inline object must not make every render fetch again.
  const peopleApiRef = React.useRef(peopleApi);
  peopleApiRef.current = peopleApi;
  const actorId = task.claimedById;
  const key = actorId ? `${task.spaceId}|${actorId}` : "";
  React.useEffect(() => {
    if (!key || !actorId) return;
    let active = true;
    void peopleApiRef.current
      .get({ spaceId: task.spaceId, actorId })
      .then((result) => {
        if (active && result.ok && result.person) setState({ key, person: result.person });
      })
      .catch(() => undefined);
    return () => {
      active = false;
    };
    // The directory entry of one claim: it is read again when the claim changes hands, not on every edit.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);
  return state.key === key ? state.person : null;
}

/** "Claimed by Build bot · 12 minutes ago", or that the claim ran out; nothing for a task nobody holds. */
export function ClaimedLine({ task, peopleApi }: { task: TaskResponse; peopleApi?: Pick<typeof peopleRpc, "get"> }) {
  const claimant = useClaimant(task, peopleApi);
  if (!task.claimedById) return null;
  const name = claimant ? (claimant.isMe ? "you" : claimant.displayName) : "someone";
  const when = task.claimedAtUtc ? ago(task.claimedAtUtc) : null;
  return (
    <span className="inline-flex items-center gap-1">
      {claimant?.kind === "agent" ? <Bot className="size-3.5" aria-hidden="true" /> : <UserRound className="size-3.5" aria-hidden="true" />}
      {claimLive(task) ? (
        <>
          Claimed by <bdi>{name}</bdi>
          {when && ` · ${when}`}
        </>
      ) : (
        <>
          Claim by <bdi>{name}</bdi> expired
        </>
      )}
    </span>
  );
}

/**
 * The task's agent controls (PLAN3 12.4, 12.8): who holds the claim, Ready for agent (optionally pointed at one
 * local agent), and Claim / Release / Take over. A Personal task comes back changed from each call; a shared task is
 * claimed by the service, which answers without the task, and the change arrives with sync.
 */
export function AgentSection({
  task,
  canEdit,
  shared,
  onTaskChanged,
  onChanged,
  claimApi = claimRpc,
  agentsApi = agentRpc,
  peopleApi = peopleRpc,
}: {
  task: TaskResponse;
  canEdit: boolean;
  /** A shared space's task: its claims go through the service, and local agents never work there. */
  shared: boolean;
  onTaskChanged: (task: TaskResponse) => void;
  onChanged: () => void;
  claimApi?: typeof claimRpc;
  agentsApi?: Pick<typeof agentRpc, "list">;
  peopleApi?: Pick<typeof peopleRpc, "get">;
}) {
  const [agents, setAgents] = React.useState<readonly AgentViewDto[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const claimant = useClaimant(task, peopleApi);
  const choosesAgent = canEdit && !shared;
  // Only an agent that could take the task: not paused, and given this task's list.
  const eligible = agents.filter(
    (entry) => !entry.agent.pausedAtUtc && entry.lists.some((list) => list.allowed && list.containerId === task.containerId),
  );
  const agentsApiRef = React.useRef(agentsApi);
  agentsApiRef.current = agentsApi;
  React.useEffect(() => {
    let active = true;
    setAgents([]);
    if (!choosesAgent) return;
    void agentsApiRef.current
      .list({})
      .then((result) => {
        if (active && result.ok) setAgents(result.agents);
      })
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, [choosesAgent]);

  const live = claimLive(task);
  const open = !isFinished(task.status);
  const request = () => ({ operationId: crypto.randomUUID(), taskId: task.id, expectedVersion: task.version });

  async function change(action: () => Promise<ClaimResponse>, synced: string) {
    if (busy) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.message ?? "The task's claim could not be changed.");
      if (result.task) onTaskChanged(result.task);
      else setNotice(synced);
      onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : "The task's claim could not be changed.");
    } finally {
      setBusy(false);
    }
  }
  const claim = (method: typeof claimRpc.claim, done: string) => change(() => method(request()), done);
  const setReady = (ready: boolean, agentId: string | null) =>
    change(() => claimApi.setReady({ ...request(), ready, agentId: ready ? agentId : null }), "Marked. The task updates as it syncs.");

  if (!task.claimedById && !canEdit) return null;
  return (
    <fieldset className="grid gap-2 rounded-lg border p-3" data-testid="agent-section">
      <legend className="px-1 text-sm font-medium">Agent</legend>
      {task.claimedById && (
        <p className="text-sm text-muted-foreground">
          <ClaimedLine task={task} peopleApi={peopleApi} />
        </p>
      )}
      {canEdit && (
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            role="switch"
            className="accent-primary"
            checked={task.agentReady}
            disabled={busy}
            onChange={(e) => void setReady(e.target.checked, task.agentAssigneeId)}
          />
          Ready for agent
        </label>
      )}
      {choosesAgent && task.agentReady && (eligible.length > 1 || task.agentAssigneeId) && (
        <Field label="Agent">
          <NativeSelect value={task.agentAssigneeId ?? ""} disabled={busy} onChange={(e) => void setReady(true, e.target.value || null)}>
            <NativeSelectOption value="">None</NativeSelectOption>
            {task.agentAssigneeId && !eligible.some((entry) => entry.agent.id === task.agentAssigneeId) && (
              <NativeSelectOption value={task.agentAssigneeId} disabled>
                {agents.find((entry) => entry.agent.id === task.agentAssigneeId)?.agent.name ?? "An agent no longer available"}
              </NativeSelectOption>
            )}
            {eligible.map((entry) => (
              <NativeSelectOption key={entry.agent.id} value={entry.agent.id}>
                {entry.agent.name}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </Field>
      )}
      {canEdit && open && (
        <div className="flex flex-wrap gap-2">
          {!live && (
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={busy}
              onClick={() => void claim(claimApi.claim, "Claimed. The task updates as it syncs.")}
            >
              Claim
            </Button>
          )}
          {live && claimant?.isMe && (
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={busy}
              onClick={() => void claim(claimApi.release, "Released. The task updates as it syncs.")}
            >
              Release
            </Button>
          )}
          {live && !claimant?.isMe && (
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={busy}
              onClick={() => void claim(claimApi.takeOver, "Taken over. The task updates as it syncs.")}
            >
              Take over
            </Button>
          )}
        </div>
      )}
      {notice && (
        <p role="status" className="text-xs text-muted-foreground">
          {notice}
        </p>
      )}
      {error && <InlineError>{error}</InlineError>}
    </fieldset>
  );
}
