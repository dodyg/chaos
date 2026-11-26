import React from "react";
import {
  agentRpc,
  type agentRpc as AgentRpc,
  type AgentTokenResponse,
  type AgentUndoResponse,
  type AgentViewDto,
  type LocalAgentDto,
  type RevertAgentResponse,
  type ServerAgentDto,
} from "#neoastra";
import { Bot, Pause, Pencil, Play, Plus, RotateCcw, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Badge } from "@/components/ui/badge";
import { Banner, Field, InlineError } from "@/components/common";
import { Modal, useAvailableSpaces } from "./Sharing";
import { ago } from "./SyncStatus";

export type AgentsApi = Pick<
  typeof AgentRpc,
  | "list"
  | "create"
  | "rename"
  | "setPaused"
  | "remove"
  | "createToken"
  | "revokeToken"
  | "setAccess"
  | "undo"
  | "serverList"
  | "serverCreate"
  | "serverUpdate"
  | "serverRemove"
  | "revert"
>;

/** Agent and token names are limited to this many characters (the host's `AgentLimits.MaxNameLength`). */
const maxNameLength = 100;
const operationId = () => crypto.randomUUID();
const plural = (count: number, singular: string) => `${count} ${count === 1 ? singular : `${singular}s`}`;
const undoText = (agent: string, result: AgentUndoResponse) =>
  result.conflict
    ? `Undid ${plural(result.undone, "change")} by ${agent}, then stopped at “${result.stoppedAt}”: ${result.conflict}`
    : result.undone === 0
      ? `There was nothing to undo by ${agent}.`
      : `Undid ${plural(result.undone, "change")} by ${agent}.`;
const revertText = (result: RevertAgentResponse) =>
  `Restored ${plural(result.restoredGroups, "field")}, trashed ${plural(result.trashed, "task")}${
    result.skipped.length ? `; skipped ${plural(result.skipped.length, "task")} changed since by someone else` : ""
  }.`;

type Confirm = { kind: "remove" | "undo"; agent: LocalAgentDto } | { kind: "serverRemove"; agent: ServerAgentDto };
const okResult = { ok: true, error: null };

/**
 * Settings → Agents (PLAN3 12.3, 12.7): the person's local agents with the Personal lists each may work in and their
 * tokens (a token's secret is shown once and kept nowhere), undo of an agent's recent changes, and the online
 * account's server agents with the spaces they are members of and a revert of their recent work.
 */
export function AgentsSettings({ api = agentRpc, onChanged }: { api?: AgentsApi; onChanged?: () => void }) {
  const spaces = useAvailableSpaces();
  const [agents, setAgents] = React.useState<readonly AgentViewDto[] | null>(null);
  const [serverAgents, setServerAgents] = React.useState<readonly ServerAgentDto[] | null>(null);
  const [serverError, setServerError] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [newName, setNewName] = React.useState("");
  const [newServerName, setNewServerName] = React.useState("");
  const [renaming, setRenaming] = React.useState<{ id: string; name: string } | null>(null);
  /** The agent the New token form is open for. */
  const [tokenFor, setTokenFor] = React.useState<string | null>(null);
  const [tokenName, setTokenName] = React.useState("");
  // The secret of a token just made: it exists only here, until the dialog closes.
  const [secret, setSecret] = React.useState<{
    agent: string;
    token: string;
    secret: string;
    claudeCode: string;
    claudeDesktop: string;
    copied: string | null;
  } | null>(null);
  const [confirm, setConfirm] = React.useState<Confirm | null>(null);
  const [hours, setHours] = React.useState<Record<string, string>>({});
  const [reverts, setReverts] = React.useState<Record<string, string>>({});

  async function load() {
    // Local agents work offline and with no account (12.1): the server list failing must not hide them.
    const [local, server] = await Promise.allSettled([api.list({}), api.serverList({})]);
    if (local.status === "rejected") throw local.reason;
    if (!local.value.ok) throw new Error(local.value.error?.message ?? "Could not load agents.");
    setAgents([...local.value.agents]);
    const answer = server.status === "fulfilled" ? server.value : null;
    setServerAgents(answer?.ok ? [...answer.agents] : []);
    setServerError(answer?.ok ? null : (answer?.error?.message ?? "Could not load the server agents."));
  }
  React.useEffect(() => {
    void load().catch((e: unknown) => setError(e instanceof Error ? e.message : String(e)));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api]);

  /** One write at a time; a refused change shows the service's own words. True when it worked, so a form clears only then. */
  async function run(action: () => Promise<void>): Promise<boolean> {
    if (busy) return false;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      await action();
      return true;
    } catch (e) {
      setError(e instanceof Error ? e.message : "That did not work.");
      return false;
    } finally {
      setBusy(false);
    }
  }
  /** A change to the agents: a refusal becomes an error with its message, otherwise the lists load again. */
  async function change(action: () => Promise<{ ok: boolean; error: { message: string } | null }>, fallback: string) {
    const result = await action();
    if (!result.ok) throw new Error(result.error?.message ?? fallback);
    await load();
    onChanged?.();
  }
  function showToken(result: AgentTokenResponse, agent: string) {
    if (!result.ok || !result.secret || !result.token) throw new Error(result.error?.message ?? "The token could not be made.");
    setTokenFor(null);
    setTokenName("");
    setSecret({
      agent,
      token: result.token.name,
      secret: result.secret,
      claudeCode: result.claudeCode ?? "",
      claudeDesktop: result.claudeDesktop ?? "",
      copied: null,
    });
  }

  const spaceName = (spaceId: string) => spaces.find((space) => space.spaceId.toLowerCase() === spaceId.toLowerCase())?.name ?? spaceId;
  const validName = (name: string) => !!name.trim() && name.trim().length <= maxNameLength;

  return (
    <section aria-label="Agents" className="grid gap-4">
      <div>
        <h2 className="text-lg font-semibold">Agents</h2>
        <p className="text-sm text-muted-foreground">
          Agents reach Chaos through an MCP client. A local agent works only in the Personal lists you tick below; in a shared space it
          works through a server agent that an owner adds under Members.
        </p>
      </div>
      {error && (
        <Banner tone="error" onDismiss={() => setError(null)}>
          {error}
        </Banner>
      )}
      {notice && (
        <p role="status" className="text-sm">
          {notice}
        </p>
      )}
      <section aria-label="Local agents" className="grid gap-3">
        <h3 className="font-semibold">Local agents</h3>
        {agents === null ? (
          <p className="text-sm text-muted-foreground">Loading agents…</p>
        ) : agents.length === 0 ? (
          <p className="text-sm text-muted-foreground">No local agents yet.</p>
        ) : (
          agents.map(({ agent, lists, tokens }) => (
            <div key={agent.id} role="region" aria-label={`Agent ${agent.name}`} className="grid gap-3 rounded-xl border p-4">
              <div className="flex flex-wrap items-center gap-2">
                <Bot className="size-4 text-muted-foreground" aria-hidden="true" />
                <strong dir="auto">{agent.name}</strong>
                {agent.pausedAtUtc && <Badge variant="secondary">Paused</Badge>}
                <span className="ml-auto flex flex-wrap gap-1">
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={busy}
                    aria-label={`Rename ${agent.name}`}
                    onClick={() => setRenaming({ id: agent.id, name: agent.name })}
                  >
                    <Pencil />
                    Rename
                  </Button>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={busy}
                    onClick={() =>
                      void run(() =>
                        change(
                          () =>
                            api.setPaused({
                              operationId: operationId(),
                              agentId: agent.id,
                              expectedVersion: agent.version,
                              paused: !agent.pausedAtUtc,
                            }),
                          "The agent could not be changed.",
                        ),
                      )
                    }
                  >
                    {agent.pausedAtUtc ? <Play /> : <Pause />}
                    {agent.pausedAtUtc ? "Resume" : "Pause"}
                  </Button>
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    disabled={busy}
                    aria-label={`Remove ${agent.name}`}
                    onClick={() => setConfirm({ kind: "remove", agent })}
                  >
                    <Trash2 />
                    Remove
                  </Button>
                </span>
              </div>
              {renaming?.id === agent.id && (
                <form
                  className="flex flex-wrap items-end gap-2"
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (!validName(renaming.name)) return;
                    void run(() =>
                      change(
                        () =>
                          api.rename({
                            operationId: operationId(),
                            agentId: agent.id,
                            expectedVersion: agent.version,
                            name: renaming.name,
                          }),
                        "The agent could not be renamed.",
                      ),
                    ).then((ok) => ok && setRenaming(null));
                  }}
                >
                  <Field label="Agent name">
                    <Input
                      value={renaming.name}
                      maxLength={maxNameLength}
                      onChange={(e) => setRenaming({ id: agent.id, name: e.target.value })}
                    />
                  </Field>
                  <Button disabled={busy || !validName(renaming.name)}>Save name</Button>
                  <Button type="button" variant="outline" disabled={busy} onClick={() => setRenaming(null)}>
                    Cancel
                  </Button>
                </form>
              )}
              <fieldset className="grid gap-1" disabled={busy}>
                <legend className="text-xs font-medium text-muted-foreground">Personal lists this agent may work in</legend>
                {lists.length === 0 && <p className="text-xs text-muted-foreground">Your Personal space has no lists.</p>}
                {lists.map((list) => (
                  <label key={list.containerId} className="flex items-center gap-2 text-sm">
                    <input
                      type="checkbox"
                      className="accent-primary"
                      checked={list.allowed}
                      onChange={(e) =>
                        void run(() =>
                          change(
                            () =>
                              api.setAccess({
                                operationId: operationId(),
                                agentId: agent.id,
                                containerId: list.containerId,
                                allowed: e.target.checked,
                              }),
                            "The agent's lists could not be changed.",
                          ),
                        )
                      }
                    />
                    {list.containerName}
                  </label>
                ))}
              </fieldset>
              <div className="grid gap-1">
                <h4 className="text-xs font-medium text-muted-foreground">Tokens</h4>
                {tokens.length === 0 && (
                  <p className="text-xs text-muted-foreground">No tokens. An MCP client needs one to act as this agent.</p>
                )}
                <ul className="grid gap-1 text-sm">
                  {tokens.map((token) => (
                    <li key={token.id} className="flex flex-wrap items-center gap-2">
                      <span dir="auto">{token.name}</span>
                      <span className="text-xs text-muted-foreground">
                        · created <time dateTime={token.createdAtUtc}>{new Date(token.createdAtUtc).toLocaleDateString()}</time>
                        {token.lastUsedAtUtc ? ` · used ${ago(token.lastUsedAtUtc)}` : " · never used"}
                      </span>
                      <Button
                        type="button"
                        variant="ghost"
                        size="xs"
                        disabled={busy}
                        aria-label={`Revoke ${token.name}`}
                        className="ml-auto hover:text-destructive"
                        onClick={() =>
                          void run(() =>
                            change(
                              () => api.revokeToken({ operationId: operationId(), agentId: agent.id, tokenId: token.id }),
                              "The token could not be revoked.",
                            ),
                          )
                        }
                      >
                        Revoke
                      </Button>
                    </li>
                  ))}
                </ul>
                {tokenFor === agent.id ? (
                  <form
                    className="flex flex-wrap items-end gap-2"
                    onSubmit={(e) => {
                      e.preventDefault();
                      if (!validName(tokenName)) return;
                      const name = tokenName.trim();
                      void run(() =>
                        change(
                          () =>
                            api
                              .createToken({ operationId: operationId(), agentId: agent.id, expectedVersion: agent.version, name })
                              .then((result) => {
                                showToken(result, agent.name);
                                return okResult;
                              }),
                          "The token could not be made.",
                        ),
                      );
                    }}
                  >
                    <Field label="Token name">
                      <Input
                        value={tokenName}
                        maxLength={maxNameLength}
                        placeholder="Laptop MCP client"
                        onChange={(e) => setTokenName(e.target.value)}
                      />
                    </Field>
                    <Button disabled={busy || !validName(tokenName)}>Create token</Button>
                    <Button type="button" variant="outline" disabled={busy} onClick={() => setTokenFor(null)}>
                      Cancel
                    </Button>
                  </form>
                ) : (
                  <div>
                    <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => setTokenFor(agent.id)}>
                      <Plus />
                      New token
                    </Button>
                  </div>
                )}
              </div>
              <div>
                <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => setConfirm({ kind: "undo", agent })}>
                  <RotateCcw />
                  Undo recent changes by {agent.name}
                </Button>
              </div>
            </div>
          ))
        )}
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (!validName(newName)) return;
            void run(() => change(() => api.create({ operationId: operationId(), name: newName }), "The agent could not be added.")).then(
              (ok) => {
                if (ok) setNewName("");
              },
            );
          }}
        >
          <Field label="New agent name">
            <Input value={newName} maxLength={maxNameLength} placeholder="Build bot" onChange={(e) => setNewName(e.target.value)} />
          </Field>
          <Button disabled={busy || !validName(newName)}>
            <Plus />
            Add agent
          </Button>
        </form>
      </section>

      <section aria-label="Server agents" className="grid gap-3 border-t pt-4">
        <div>
          <h3 className="font-semibold">Server agents</h3>
          <p className="text-sm text-muted-foreground">
            Agents of your online account. An owner adds one to a shared space under Members → Add agent; it then works in that space's
            lists through the service. Reverting undoes an agent's changes of the last hours in one space, unless someone changed the same
            thing since.
          </p>
        </div>
        {serverError && <InlineError>{serverError}</InlineError>}
        {serverAgents === null && !serverError && <p className="text-sm text-muted-foreground">Loading server agents…</p>}
        {serverAgents?.length === 0 && <p className="text-sm text-muted-foreground">No server agents yet.</p>}
        {serverAgents?.map((agent) => (
          <div key={agent.id} role="region" aria-label={`Server agent ${agent.name}`} className="grid gap-3 rounded-xl border p-4">
            <div className="flex flex-wrap items-center gap-2">
              <Bot className="size-4 text-muted-foreground" aria-hidden="true" />
              <strong dir="auto">{agent.name}</strong>
              {agent.pausedAtUtc && <Badge variant="secondary">Paused</Badge>}
              <span className="ml-auto flex flex-wrap gap-1">
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={busy}
                  aria-label={`Rename ${agent.name}`}
                  onClick={() => setRenaming({ id: agent.id, name: agent.name })}
                >
                  <Pencil />
                  Rename
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={busy}
                  onClick={() =>
                    void run(() =>
                      change(
                        () => api.serverUpdate({ agentId: agent.id, name: null, paused: !agent.pausedAtUtc }),
                        "The agent could not be changed.",
                      ),
                    )
                  }
                >
                  {agent.pausedAtUtc ? <Play /> : <Pause />}
                  {agent.pausedAtUtc ? "Resume" : "Pause"}
                </Button>
                <Button
                  type="button"
                  variant="outline"
                  size="sm"
                  disabled={busy}
                  aria-label={`Remove ${agent.name}`}
                  onClick={() => setConfirm({ kind: "serverRemove", agent })}
                >
                  <Trash2 />
                  Remove
                </Button>
              </span>
            </div>
            {renaming?.id === agent.id && (
              <form
                className="flex flex-wrap items-end gap-2"
                onSubmit={(e) => {
                  e.preventDefault();
                  if (!validName(renaming.name)) return;
                  void run(() =>
                    change(
                      () => api.serverUpdate({ agentId: agent.id, name: renaming.name, paused: null }),
                      "The agent could not be renamed.",
                    ),
                  ).then((ok) => ok && setRenaming(null));
                }}
              >
                <Field label="Agent name">
                  <Input
                    value={renaming.name}
                    maxLength={maxNameLength}
                    onChange={(e) => setRenaming({ id: agent.id, name: e.target.value })}
                  />
                </Field>
                <Button disabled={busy || !validName(renaming.name)}>Save name</Button>
                <Button type="button" variant="outline" disabled={busy} onClick={() => setRenaming(null)}>
                  Cancel
                </Button>
              </form>
            )}
            {agent.memberships.length === 0 ? (
              <p className="text-xs text-muted-foreground">Not in any shared space.</p>
            ) : (
              <ul className="grid gap-2 text-sm">
                {agent.memberships.map((membership) => {
                  const key = `${agent.id}|${membership.spaceId}`;
                  const value = hours[key] ?? "24";
                  const span = Number(value);
                  return (
                    <li key={membership.spaceId} className="grid gap-1 rounded-lg border bg-muted/20 p-3">
                      <span>
                        In <strong>{spaceName(membership.spaceId)}</strong>
                        {membership.allowedContainerIds ? ` · ${plural(membership.allowedContainerIds.length, "list")}` : " · All lists"}
                      </span>
                      <form
                        className="flex flex-wrap items-center gap-2"
                        aria-label={`Revert ${agent.name} in ${spaceName(membership.spaceId)}`}
                        onSubmit={(e) => {
                          e.preventDefault();
                          void run(() =>
                            api.revert({ agentId: agent.id, spaceId: membership.spaceId, hours: span }).then((result) => {
                              if (!result.ok) throw new Error(result.error?.message ?? "The agent's changes could not be reverted.");
                              setReverts((current) => ({ ...current, [key]: revertText(result) }));
                              onChanged?.();
                            }),
                          );
                        }}
                      >
                        <label className="flex items-center gap-1">
                          Revert the last
                          <Input
                            type="number"
                            min="1"
                            step="1"
                            className="w-20"
                            value={value}
                            onChange={(e) => setHours((current) => ({ ...current, [key]: e.target.value }))}
                          />
                          hours
                        </label>
                        <Button type="submit" variant="outline" size="sm" disabled={busy || !Number.isInteger(span) || span < 1}>
                          Revert
                        </Button>
                      </form>
                      {reverts[key] !== undefined && (
                        <p role="status" className="text-xs">
                          {reverts[key]}
                        </p>
                      )}
                    </li>
                  );
                })}
              </ul>
            )}
          </div>
        ))}
        <form
          className="flex flex-wrap items-end gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            if (!validName(newServerName)) return;
            void run(() =>
              change(() => api.serverCreate({ agentId: "", name: newServerName, paused: null }), "The server agent could not be added."),
            ).then((ok) => ok && setNewServerName(""));
          }}
        >
          <Field label="New server agent name">
            <Input
              value={newServerName}
              maxLength={maxNameLength}
              placeholder="Build bot"
              onChange={(e) => setNewServerName(e.target.value)}
            />
          </Field>
          <Button disabled={busy || !validName(newServerName)}>
            <Plus />
            Add server agent
          </Button>
        </form>
      </section>

      {secret && (
        <Modal title={`Token for ${secret.agent} · ${secret.token}`} onClose={() => setSecret(null)}>
          <div role="status" className="grid gap-2 text-sm">
            <p className="font-medium">Copy this token now. It is shown only once.</p>
            <code className="break-all text-xs">{secret.secret}</code>
            <p className="text-muted-foreground">
              Add Chaos to the assistant's MCP client with one of these. Both carry the token in the <code>CHAOS_AGENT_TOKEN</code>{" "}
              environment variable. The assistant then works offline, in the lists you tick for this agent.
            </p>
            <p className="font-medium">Claude Code</p>
            <code className="break-all text-xs">{secret.claudeCode}</code>
            <p className="font-medium">Claude Desktop (claude_desktop_config.json)</p>
            <pre className="overflow-x-auto text-xs">{secret.claudeDesktop}</pre>
          </div>
          <div className="flex flex-wrap justify-end gap-2">
            {(
              [
                ["token", "Copy token", secret.secret],
                ["claudeCode", "Copy Claude Code command", secret.claudeCode],
                ["claudeDesktop", "Copy Claude Desktop config", secret.claudeDesktop],
              ] as const
            ).map(([key, label, text]) => (
              <Button
                key={key}
                type="button"
                variant="outline"
                onClick={() => void navigator.clipboard?.writeText(text).then(() => setSecret({ ...secret, copied: key }))}
              >
                {secret.copied === key ? "Copied" : label}
              </Button>
            ))}
            <Button type="button" onClick={() => setSecret(null)}>
              Done
            </Button>
          </div>
        </Modal>
      )}
      {confirm && (
        <div
          role="alertdialog"
          aria-label={confirm.kind === "undo" ? "Undo the agent's changes?" : "Remove agent?"}
          className="grid gap-3 rounded-xl border p-4"
        >
          <p>
            {confirm.kind === "undo"
              ? `Undo the recent changes of ${confirm.agent.name}? Its newest changes are undone one by one until a later change conflicts with one.`
              : confirm.kind === "remove"
                ? `Remove ${confirm.agent.name}? Its tokens stop working at once and its claims are released. Its tasks stay.`
                : `Remove ${confirm.agent.name}? It leaves every space it is a member of and its claims are released. Its tasks stay.`}
          </p>
          <div className="flex gap-2">
            <Button
              disabled={busy}
              onClick={() => {
                const chosen = confirm;
                setConfirm(null);
                if (chosen.kind === "undo")
                  void run(() =>
                    api.undo({ actorId: `agent:${chosen.agent.id}`, maxOperations: null }).then((result) => {
                      if (!result.ok) throw new Error(result.error?.message ?? "The changes could not be undone.");
                      setNotice(undoText(chosen.agent.name, result));
                      onChanged?.();
                    }),
                  );
                else if (chosen.kind === "remove")
                  void run(() =>
                    change(
                      () => api.remove({ operationId: operationId(), agentId: chosen.agent.id, expectedVersion: chosen.agent.version }),
                      "The agent could not be removed.",
                    ),
                  );
                else
                  void run(() =>
                    change(
                      () => api.serverRemove({ agentId: chosen.agent.id, name: null, paused: null }),
                      "The agent could not be removed.",
                    ),
                  );
              }}
            >
              {confirm.kind === "undo" ? "Undo changes" : "Remove agent"}
            </Button>
            {confirm.kind === "remove" && (
              // Once removed, the agent's card (and its Undo button) is gone: offer the undo here too (12.7).
              <Button
                variant="outline"
                disabled={busy}
                onClick={() => {
                  const chosen = confirm;
                  setConfirm(null);
                  void run(async () => {
                    const undone = await api.undo({ actorId: `agent:${chosen.agent.id}`, maxOperations: null });
                    if (!undone.ok) throw new Error(undone.error?.message ?? "The changes could not be undone.");
                    setNotice(undoText(chosen.agent.name, undone));
                    onChanged?.();
                    // The undo moved nothing of the agent itself, so its version still holds.
                    await change(
                      () => api.remove({ operationId: operationId(), agentId: chosen.agent.id, expectedVersion: chosen.agent.version }),
                      "The agent could not be removed.",
                    );
                  });
                }}
              >
                Undo its changes and remove
              </Button>
            )}
            <Button type="button" variant="outline" disabled={busy} onClick={() => setConfirm(null)}>
              Cancel
            </Button>
          </div>
        </div>
      )}
    </section>
  );
}
