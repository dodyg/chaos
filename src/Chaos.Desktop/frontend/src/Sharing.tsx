import React from "react";
import {
  agentRpc,
  collaborationRpc,
  gitHubRpc,
  spaceRpc,
  spaceUpdateRpc,
  tagRpc,
  type agentRpc as AgentRpc,
  type ContainerResponse,
  type SpaceAgentDto,
  type SpaceInviteResponseItem,
  type SpaceMemberResponse,
  type SpaceResponse,
  type SpaceNotificationPreference,
  type SpaceStateDto,
  type SpaceUpdateDto,
  type TagResponse,
} from "#neoastra";
import { Bot, ChevronRight, FolderKanban, Home, ListTodo, Users } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { EmptyState, Field, InlineError, PageHeader } from "@/components/common";
import { MarkdownEditor } from "./MarkdownEditor";
import { MarkdownPreview } from "./markdown";
import { GitHubSettings } from "./GitHubSettings";

/** Roles as the service numbers them (PLAN3 §8.3). */
export const OWNER = 1;
export const EDITOR = 2;
export const VIEWER = 3;
/** The member kind of an agent, as the service numbers member kinds (its `SpaceMember.AgentKind`). */
export const AGENT_KIND = 2;
const roleNames: Record<number, string> = { [OWNER]: "Owner", [EDITOR]: "Editor", [VIEWER]: "Viewer" };
export const roleName = (role: number | null | undefined) => (role ? (roleNames[role] ?? "Member") : "Member");
const roleSentence: Record<number, string> = {
  [OWNER]: "You are an owner",
  [EDITOR]: "You are an editor",
  [VIEWER]: "You are a viewer",
};

export type SpaceApi = Pick<
  typeof spaceRpc,
  | "list"
  | "create"
  | "join"
  | "leave"
  | "move"
  | "shareList"
  | "delete"
  | "members"
  | "changeRole"
  | "removeMember"
  | "setMyName"
  | "setMyGitHubLogin"
  | "invites"
  | "invite"
  | "revokeInvite"
>;

/** What this person may do in one space. Personal behaves as an owned space and has no roles to show. */
export type SpaceAccess = {
  shared: boolean;
  role: number | null;
  isOwner: boolean;
  isViewer: boolean;
  /** May create and change tasks, lists, pages and notes there. */
  canEdit: boolean;
  /** One plain sentence, or null for Personal. */
  sentence: string | null;
};

const personalAccess: SpaceAccess = { shared: false, role: null, isOwner: true, isViewer: false, canEdit: true, sentence: null };

const SpacesContext = React.createContext<readonly SpaceResponse[]>([]);

export function SpacesProvider({ spaces, children }: { spaces: readonly SpaceResponse[]; children: React.ReactNode }) {
  return <SpacesContext.Provider value={spaces}>{children}</SpacesContext.Provider>;
}

export function useAvailableSpaces() {
  return React.useContext(SpacesContext);
}

/** The one place a screen asks what the current person may do in a space (PLAN3 Q5 step 3). */
export function useSpaceRole(spaceId: string | null | undefined): SpaceAccess {
  const spaces = React.useContext(SpacesContext);
  return React.useMemo(() => accessIn(spaces, spaceId), [spaces, spaceId]);
}

export function accessIn(spaces: readonly SpaceResponse[], spaceId: string | null | undefined): SpaceAccess {
  if (!spaceId) return personalAccess;
  const space = spaces.find((entry) => entry.spaceId.toLowerCase() === spaceId.toLowerCase());
  if (!space || space.kind === "Personal") return space ? personalAccess : spaces.length === 0 ? personalAccess : denied;
  const role = space.role ?? VIEWER;
  return {
    shared: true,
    role,
    isOwner: role === OWNER,
    isViewer: role === VIEWER,
    canEdit: role === OWNER || role === EDITOR,
    sentence: roleSentence[role] ?? null,
  };
}

const denied: SpaceAccess = { shared: true, role: VIEWER, isOwner: false, isViewer: true, canEdit: false, sentence: roleSentence[VIEWER] };

/** Loads the spaces this device holds. Call `reload` after anything that changes them. */
export function useSpaces(api: Pick<SpaceApi, "list"> = spaceRpc, revision = 0) {
  const [spaces, setSpaces] = React.useState<SpaceResponse[]>([]);
  const reload = React.useCallback(async () => {
    const result = await api.list({});
    if (result.ok) setSpaces([...result.spaces]);
  }, [api]);
  React.useEffect(() => {
    void reload().catch(() => undefined);
  }, [reload, revision]);
  return { spaces, reload };
}

export function Modal({ title, onClose, children }: { title: string; onClose: () => void; children: React.ReactNode }) {
  const titleId = React.useId();
  React.useEffect(() => {
    function key(event: KeyboardEvent) {
      if (event.key === "Escape") onClose();
    }
    document.addEventListener("keydown", key);
    return () => document.removeEventListener("keydown", key);
  }, [onClose]);
  return (
    <div className="fixed inset-0 z-40 grid place-items-center bg-black/40 p-4">
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="grid w-full max-w-md gap-3 rounded-xl border bg-card p-5 shadow-lg"
      >
        <h2 id={titleId} className="font-semibold">
          {title}
        </h2>
        {children}
      </div>
    </div>
  );
}

function useAction() {
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  async function run<T extends { ok: boolean; error: { message: string } | null }>(action: () => Promise<T>): Promise<T | null> {
    setBusy(true);
    setError(null);
    try {
      const result = await action();
      if (!result.ok) {
        setError(result.error?.message ?? "That did not work.");
        return null;
      }
      return result;
    } catch (e) {
      setError(e instanceof Error ? e.message : "That did not work.");
      return null;
    } finally {
      setBusy(false);
    }
  }
  return { busy, error, setError, run };
}

export function NewSpaceDialog({
  onClose,
  onCreated,
  api = spaceRpc,
}: {
  onClose: () => void;
  onCreated: (space: SpaceResponse) => void;
  api?: Pick<SpaceApi, "create">;
}) {
  const [name, setName] = React.useState("");
  const { busy, error, run } = useAction();
  return (
    <Modal title="New shared space" onClose={onClose}>
      <p className="text-sm text-muted-foreground">
        A shared space holds lists and projects that other people can see. Creating one needs a connection to the online service.
      </p>
      <form
        className="grid gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          void run(() => api.create({ name })).then((result) => {
            if (result?.space) onCreated(result.space);
          });
        }}
      >
        <Field label="Space name">
          <Input autoFocus value={name} maxLength={100} onChange={(e) => setName(e.target.value)} autoComplete="off" />
        </Field>
        {error && <InlineError>{error}</InlineError>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button disabled={busy || !name.trim()}>Create space</Button>
        </div>
      </form>
    </Modal>
  );
}

export function JoinSpaceDialog({
  onClose,
  onJoined,
  api = spaceRpc,
}: {
  onClose: () => void;
  onJoined: (space: SpaceResponse) => void;
  api?: Pick<SpaceApi, "join">;
}) {
  const [link, setLink] = React.useState("");
  const { busy, error, run } = useAction();
  return (
    <Modal title="Join with a link" onClose={onClose}>
      <p className="text-sm text-muted-foreground">
        Paste the invitation link you were sent. Joining needs a connection to the online service.
      </p>
      <form
        className="grid gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          void run(() => api.join({ link })).then((result) => {
            if (result?.space) onJoined(result.space);
          });
        }}
      >
        <Field label="Invitation link">
          <Input autoFocus value={link} onChange={(e) => setLink(e.target.value)} autoComplete="off" placeholder="https://…/invite/…" />
        </Field>
        {error && <InlineError>{error}</InlineError>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button disabled={busy || !link.trim()}>Join</Button>
        </div>
      </form>
    </Modal>
  );
}

const expiryChoices: [string, number | null][] = [
  ["1 day", 1],
  ["7 days", 7],
  ["30 days", 30],
  ["Never", null],
];
const useChoices: [string, number | null][] = [
  ["1 use", 1],
  ["10 uses", 10],
  ["Unlimited", null],
];

const describeInvite = (invite: SpaceInviteResponseItem) =>
  `${invite.email ?? "Link"} · ${roleName(invite.role)} · ${invite.expiresAt ? `expires ${new Date(invite.expiresAt).toLocaleDateString()}` : "never expires"}${
    invite.maxUses ? ` · ${invite.uses} of ${invite.maxUses} used` : ` · ${invite.uses} used`
  }`;

/** Invite by email or by link, and the list of open invitations with a way to turn each off (§8.5). */
export function InviteDialog({
  space,
  onClose,
  api = spaceRpc,
  githubApi = gitHubRpc,
}: {
  space: { spaceId: string; name: string };
  onClose: () => void;
  api?: Pick<SpaceApi, "invite" | "invites" | "revokeInvite">;
  /** The space's GitHub connection, for the private-repository warning (PLAN3 10.5). */
  githubApi?: Pick<typeof gitHubRpc, "status">;
}) {
  const [mode, setMode] = React.useState<"email" | "link">("email");
  // A private repository connected to the space would become visible to whoever accepts the invitation.
  const [privateRepos, setPrivateRepos] = React.useState<readonly string[] | null>(null);
  const [acknowledged, setAcknowledged] = React.useState(false);
  const [email, setEmail] = React.useState("");
  const [role, setRole] = React.useState(EDITOR);
  const [expiry, setExpiry] = React.useState("7");
  const [uses, setUses] = React.useState("1");
  const [link, setLink] = React.useState<string | null>(null);
  const [copied, setCopied] = React.useState(false);
  const [sent, setSent] = React.useState<string | null>(null);
  const [invites, setInvites] = React.useState<SpaceInviteResponseItem[]>([]);
  const { busy, error, setError, run } = useAction();

  const load = React.useCallback(async () => {
    const result = await api.invites({ spaceId: space.spaceId });
    if (result.ok) setInvites([...result.invites]);
  }, [api, space.spaceId]);
  React.useEffect(() => {
    void load().catch(() => undefined);
  }, [load]);
  React.useEffect(() => {
    let active = true;
    setPrivateRepos(null);
    setAcknowledged(false);
    void githubApi
      .status({ spaceId: space.spaceId })
      .then((result) => {
        if (active) setPrivateRepos(result.ok ? result.repositories.filter((r) => r.isPrivate).map((r) => r.fullName) : []);
      })
      .catch(() => {
        if (active) setPrivateRepos([]);
      });
    return () => {
      active = false;
    };
  }, [githubApi, space.spaceId]);

  async function create() {
    setLink(null);
    setSent(null);
    setCopied(false);
    const result = await run(() =>
      api.invite({
        spaceId: space.spaceId,
        email: mode === "email" ? email : null,
        role,
        expiresInDays: mode === "link" && expiry !== "never" ? Number(expiry) : null,
        maxUses: mode === "link" && uses !== "unlimited" ? Number(uses) : null,
      }),
    );
    if (!result) return;
    if (mode === "link") setLink(result.link ?? null);
    else setSent(email.trim());
    setEmail("");
    await load().catch(() => undefined);
  }

  return (
    <Modal title={`Invite people to ${space.name}`} onClose={onClose}>
      <div role="group" aria-label="How to invite" className="flex gap-1">
        {(["email", "link"] as const).map((choice) => (
          <Button
            key={choice}
            type="button"
            size="sm"
            variant={mode === choice ? "secondary" : "outline"}
            aria-pressed={mode === choice}
            onClick={() => {
              setMode(choice);
              setError(null);
            }}
          >
            {choice === "email" ? "By email" : "By link"}
          </Button>
        ))}
      </div>
      <form
        className="grid gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          void create();
        }}
      >
        {mode === "email" && (
          <Field label="Email address">
            <Input type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="off" />
          </Field>
        )}
        <Field label="Role">
          <NativeSelect value={String(role)} onChange={(e) => setRole(Number(e.target.value))}>
            <NativeSelectOption value={String(EDITOR)}>Editor: can change everything except the space</NativeSelectOption>
            <NativeSelectOption value={String(VIEWER)}>Viewer: can read and comment</NativeSelectOption>
          </NativeSelect>
        </Field>
        {mode === "link" && (
          <div className="grid grid-cols-2 gap-3">
            <Field label="Link expires">
              <NativeSelect value={expiry} onChange={(e) => setExpiry(e.target.value)}>
                {expiryChoices.map(([text, days]) => (
                  <NativeSelectOption key={text} value={days === null ? "never" : String(days)}>
                    {text}
                  </NativeSelectOption>
                ))}
              </NativeSelect>
            </Field>
            <Field label="Link can be used">
              <NativeSelect value={uses} onChange={(e) => setUses(e.target.value)}>
                {useChoices.map(([text, count]) => (
                  <NativeSelectOption key={text} value={count === null ? "unlimited" : String(count)}>
                    {text}
                  </NativeSelectOption>
                ))}
              </NativeSelect>
            </Field>
          </div>
        )}
        {privateRepos && privateRepos.length > 0 && (
          <div role="alert" className="grid gap-2 rounded-lg border border-amber-500/50 p-3 text-sm">
            <p>
              Everyone in {space.name} will see issues from {privateRepos.join(", ")}, including people without access to{" "}
              {privateRepos.length === 1 ? "it" : "them"} on GitHub.
            </p>
            <label className="flex items-start gap-2">
              <input type="checkbox" className="mt-0.5" checked={acknowledged} onChange={(e) => setAcknowledged(e.target.checked)} />I
              understand; invite anyway
            </label>
          </div>
        )}
        {error && <InlineError>{error}</InlineError>}
        {sent && (
          <p role="status" className="text-sm">
            Invitation sent to {sent}.
          </p>
        )}
        {link && (
          <div role="status" className="grid gap-2 rounded-lg border border-dashed p-3 text-sm">
            <p className="font-medium">Copy this link now. It is shown only once.</p>
            <code className="break-all text-xs">{link}</code>
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="w-fit"
              onClick={() => {
                void navigator.clipboard?.writeText(link).then(() => setCopied(true));
              }}
            >
              {copied ? "Copied" : "Copy link"}
            </Button>
          </div>
        )}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Close
          </Button>
          <Button disabled={busy || (mode === "email" && !email.trim()) || ((privateRepos?.length ?? 0) > 0 && !acknowledged)}>
            {mode === "email" ? "Send invitation" : "Create link"}
          </Button>
        </div>
      </form>
      <section aria-label="Open invitations" className="grid gap-1 border-t pt-3">
        <h3 className="text-xs font-semibold tracking-wider text-muted-foreground uppercase">Open invitations</h3>
        {invites.length === 0 ? (
          <p className="text-sm text-muted-foreground">None.</p>
        ) : (
          <ul className="grid gap-1">
            {invites.map((invite) => (
              <li key={invite.id} className="flex items-center justify-between gap-2 text-sm">
                <span className="min-w-0 truncate">{describeInvite(invite)}</span>
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  disabled={busy}
                  aria-label={`Turn off invitation ${invite.email ?? "link"}`}
                  onClick={() => void run(() => api.revokeInvite({ spaceId: space.spaceId, inviteId: invite.id })).then(() => load())}
                >
                  Turn off
                </Button>
              </li>
            ))}
          </ul>
        )}
      </section>
    </Modal>
  );
}

/** Members → Add agent (PLAN3 12.3): an owner picks an agent of their own or of a live member, and the lists it may work in. */
export function AddAgentDialog({
  space,
  lists,
  onClose,
  onAdded,
  api = agentRpc,
}: {
  space: { spaceId: string; name: string };
  /** The space's lists; the agent works in all of them unless the owner picks some. */
  lists: readonly ContainerResponse[];
  onClose: () => void;
  onAdded: () => void;
  api?: Pick<typeof AgentRpc, "spaceAgents" | "addToSpace">;
}) {
  const [candidates, setCandidates] = React.useState<readonly SpaceAgentDto[] | null>(null);
  const [agentId, setAgentId] = React.useState("");
  const [allLists, setAllLists] = React.useState(true);
  const [chosen, setChosen] = React.useState<ReadonlySet<string>>(new Set());
  const { busy, error, setError, run } = useAction();
  // The latest client, read inside the effect, so an inline object does not make every render load again.
  const apiRef = React.useRef(api);
  apiRef.current = api;

  React.useEffect(() => {
    let active = true;
    void apiRef.current
      .spaceAgents({ spaceId: space.spaceId })
      .then((result) => {
        if (!active) return;
        if (result.ok) {
          const open = result.agents.filter((entry) => !entry.member);
          setCandidates(open);
          setAgentId(open[0]?.agent.id ?? "");
        } else setError(result.error?.message ?? "Could not load the agents you could add.");
      })
      .catch(() => {
        if (active) setError("Could not load the agents you could add.");
      });
    return () => {
      active = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [space.spaceId]);

  return (
    <Modal title={`Add an agent to ${space.name}`} onClose={onClose}>
      {error && <InlineError>{error}</InlineError>}
      {candidates === null ? (
        !error && <p className="text-sm text-muted-foreground">Loading agents…</p>
      ) : candidates.length === 0 ? (
        <p className="text-sm">
          There is no agent to add. Create one under Settings → Agents, or ask a member of this space to create one.
        </p>
      ) : (
        <form
          className="grid gap-3"
          onSubmit={(event) => {
            event.preventDefault();
            const agent = candidates.find((entry) => entry.agent.id === agentId);
            if (!agent) return;
            void run(() =>
              api.addToSpace({
                spaceId: space.spaceId,
                agentId: agent.agent.id,
                allowedContainerIds: allLists ? null : [...chosen].sort(),
              }),
            ).then((result) => {
              if (!result) return;
              onAdded();
            });
          }}
        >
          <Field label="Agent">
            <NativeSelect value={agentId} disabled={busy} onChange={(e) => setAgentId(e.target.value)}>
              {candidates.map((entry) => (
                <NativeSelectOption key={entry.agent.id} value={entry.agent.id}>
                  {entry.agent.name} ({entry.ownerName}){entry.agent.pausedAtUtc ? " · paused" : ""}
                </NativeSelectOption>
              ))}
            </NativeSelect>
          </Field>
          <fieldset className="grid gap-1" disabled={busy}>
            <legend className="text-sm font-medium">Lists it may work in</legend>
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" className="accent-primary" checked={allLists} onChange={(e) => setAllLists(e.target.checked)} />
              All lists
            </label>
            {!allLists &&
              lists.map((list) => (
                <label key={list.id} className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    className="accent-primary"
                    checked={chosen.has(list.id)}
                    onChange={(e) =>
                      setChosen((current) => {
                        const next = new Set(current);
                        if (e.target.checked) next.add(list.id);
                        else next.delete(list.id);
                        return next;
                      })
                    }
                  />
                  {list.name}
                </label>
              ))}
          </fieldset>
          <p className="text-xs text-muted-foreground">
            The agent joins with the Agent role. Members see its changes under its name, and an owner can pause or remove it here.
          </p>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" disabled={busy} onClick={onClose}>
              Cancel
            </Button>
            <Button disabled={busy || !agentId || (!allLists && chosen.size === 0)}>Add agent</Button>
          </div>
        </form>
      )}
    </Modal>
  );
}

/** Move to space… (§9.2): pick a shared space where this person can edit, confirm in plain words, then move. */
export function MoveToSpaceDialog({
  list,
  spaces,
  onClose,
  onMoved,
  api = spaceRpc,
}: {
  list: ContainerResponse;
  spaces: readonly SpaceResponse[];
  onClose: () => void;
  onMoved: (spaceId: string) => void;
  api?: Pick<SpaceApi, "move">;
}) {
  const targets = spaces.filter((space) => space.kind === "Shared" && (space.role === OWNER || space.role === EDITOR));
  const [spaceId, setSpaceId] = React.useState(targets[0]?.spaceId ?? "");
  const [personalTags, setPersonalTags] = React.useState<readonly string[]>([]);
  const [shareTags, setShareTags] = React.useState(false);
  const [moved, setMoved] = React.useState(false);
  const [tagError, setTagError] = React.useState<string | null>(null);
  React.useEffect(() => {
    void tagRpc
      .listPersonalOnList({ containerId: list.id })
      .then((result) => {
        if (result.ok) setPersonalTags(result.tags.map((tag) => tag.path));
      })
      .catch(() => undefined);
  }, [list.id]);
  const target = targets.find((space) => space.spaceId === spaceId);
  const { busy, error, run } = useAction();
  return (
    <Modal title={`Move “${list.name}” to a space`} onClose={onClose}>
      {targets.length === 0 ? (
        <>
          <p className="text-sm">You are not an owner or editor of any shared space yet. Create one first, or use Share list….</p>
          <div className="flex justify-end">
            <Button type="button" variant="ghost" onClick={onClose}>
              Close
            </Button>
          </div>
        </>
      ) : (
        <form
          className="grid gap-3"
          onSubmit={(event) => {
            event.preventDefault();
            void (async () => {
              if (!moved) {
                const result = await run(() => api.move({ containerId: list.id, spaceId }));
                if (!result) return;
                setMoved(true);
              }
              if (shareTags) {
                try {
                  const result = await tagRpc.shareList({ operationId: crypto.randomUUID(), containerId: list.id });
                  if (!result.ok) throw new Error(result.error?.message ?? "Could not share tags.");
                } catch (cause) {
                  setTagError(`The list moved, but its tags were not shared: ${String(cause)}`);
                  return;
                }
              }
              onMoved(spaceId);
            })();
          }}
        >
          <Field label="Shared space">
            <NativeSelect value={spaceId} disabled={moved} onChange={(e) => setSpaceId(e.target.value)}>
              {targets.map((space) => (
                <NativeSelectOption key={space.spaceId} value={space.spaceId}>
                  {space.name}
                </NativeSelectOption>
              ))}
            </NativeSelect>
          </Field>
          <p className="text-sm">
            All tasks in this {list.kind === "Project" ? "project" : "list"}, their subtasks, notes, updates and attachments move to{" "}
            <strong>{target?.name}</strong>. Its members will see them. Your My Day, reminders and personal tags stay yours.
          </p>
          <p className="text-xs text-muted-foreground">Moving needs a connection to the online service.</p>
          {personalTags.length > 0 && (
            <label className="text-sm">
              <input type="checkbox" checked={shareTags} onChange={(event) => setShareTags(event.target.checked)} /> Also share these tags
              with the space: {personalTags.map((tag) => `#${tag}`).join(", ")}
            </label>
          )}
          {error && <InlineError>{error}</InlineError>}
          {tagError && <InlineError>{tagError}</InlineError>}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={() => (moved ? onMoved(spaceId) : onClose())}>
              {moved ? "Done" : "Cancel"}
            </Button>
            <Button disabled={busy || !spaceId}>{moved ? "Retry sharing tags" : "Move"}</Button>
          </div>
        </form>
      )}
    </Modal>
  );
}

/** Share list… (E7): sharing happens per space, so this makes a new shared space named after the list and moves the list into it. */
export function ShareListDialog({
  list,
  onClose,
  onShared,
  api = spaceRpc,
}: {
  list: ContainerResponse;
  onClose: () => void;
  onShared: (spaceId: string, name: string) => void;
  api?: Pick<SpaceApi, "shareList">;
}) {
  const [name, setName] = React.useState(list.name);
  const { busy, error, run } = useAction();
  return (
    <Modal title={`Share “${list.name}”`} onClose={onClose}>
      <form
        className="grid gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          void run(() => api.shareList({ containerId: list.id, name })).then((result) => {
            if (result?.spaceId) onShared(result.spaceId, name.trim());
          });
        }}
      >
        <p className="text-sm">
          Chaos shares whole spaces, not single lists. This creates a new shared space and moves the list, with its tasks, subtasks, notes
          and updates, into it. Then you can invite people. Your My Day, reminders and personal tags stay yours.
        </p>
        <Field label="Name of the new shared space">
          <Input autoFocus value={name} maxLength={100} onChange={(e) => setName(e.target.value)} autoComplete="off" />
        </Field>
        <p className="text-xs text-muted-foreground">Sharing needs a connection to the online service.</p>
        {error && <InlineError>{error}</InlineError>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button disabled={busy || !name.trim()}>Create space and move list</Button>
        </div>
      </form>
    </Modal>
  );
}

export function DeleteSpaceDialog({
  space,
  onClose,
  onDeleted,
  api = spaceRpc,
}: {
  space: { spaceId: string; name: string };
  onClose: () => void;
  onDeleted: () => void;
  api?: Pick<SpaceApi, "delete">;
}) {
  const [typed, setTyped] = React.useState("");
  const { busy, error, run } = useAction();
  return (
    <Modal title={`Delete ${space.name}`} onClose={onClose}>
      <form
        className="grid gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          void run(() => api.delete({ spaceId: space.spaceId, typedName: typed })).then((result) => {
            if (result) onDeleted();
          });
        }}
      >
        <p className="text-sm">
          Everyone loses access and the space disappears from every computer. You can restore it within 30 days; after that it is deleted
          for good.
        </p>
        <Field label={`Type “${space.name}” to confirm`}>
          <Input autoFocus value={typed} onChange={(e) => setTyped(e.target.value)} autoComplete="off" />
        </Field>
        {error && <InlineError>{error}</InlineError>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button variant="destructive" disabled={busy || typed.trim() !== space.name}>
            Delete space
          </Button>
        </div>
      </form>
    </Modal>
  );
}

type HomeTab = "overview" | "members" | "notifications" | "tags" | "settings" | "connections";

function SpaceNotifications({ spaceId }: { spaceId: string }) {
  const [settings, setSettings] = React.useState<SpaceNotificationPreference | null>(null);
  const [message, setMessage] = React.useState("");
  const [saving, setSaving] = React.useState(false);
  React.useEffect(() => {
    let active = true;
    void collaborationRpc
      .settings({ spaceId })
      .then((result) => {
        if (!active) return;
        if (result.ok && result.settings) setSettings(result.settings);
        else setMessage(result.error?.message ?? "Could not load notification settings.");
      })
      .catch(() => {
        if (active) setMessage("Could not load notification settings.");
      });
    return () => {
      active = false;
    };
  }, [spaceId]);
  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (!settings || saving) return;
    setSaving(true);
    try {
      const result = await collaborationRpc.setSettings({ spaceId, settings });
      if (!result.ok) throw new Error(result.error?.message ?? "Could not save notification settings.");
      setMessage("Notification settings saved.");
    } catch (error) {
      setMessage(error instanceof Error ? error.message : "Could not save notification settings.");
    } finally {
      setSaving(false);
    }
  }
  return (
    <form role="tabpanel" aria-label="Notifications" className="grid max-w-lg gap-3" onSubmit={(event) => void save(event)}>
      <h3 className="font-semibold">Your notifications</h3>
      {message && (
        <p role="status" className="text-sm">
          {message}
        </p>
      )}
      {settings && (
        <>
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={settings.assigned}
              disabled={settings.nothing || saving}
              onChange={(event) => setSettings({ ...settings, assigned: event.target.checked })}
            />{" "}
            When a task is assigned to me
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={settings.mentioned}
              disabled={settings.nothing || saving}
              onChange={(event) => setSettings({ ...settings, mentioned: event.target.checked })}
            />{" "}
            When someone mentions me
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={settings.dueSoon}
              disabled={settings.nothing || saving}
              onChange={(event) => setSettings({ ...settings, dueSoon: event.target.checked })}
            />{" "}
            When a task is due within a day
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={settings.delegations}
              disabled={settings.nothing || saving}
              onChange={(event) => setSettings({ ...settings, delegations: event.target.checked })}
            />{" "}
            Delegation updates
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={settings.ownerDefaultReminder}
              disabled={saving}
              onChange={(event) => setSettings({ ...settings, ownerDefaultReminder: event.target.checked })}
            />{" "}
            Use this space’s default deadline reminder
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input
              type="checkbox"
              checked={settings.nothing}
              disabled={saving}
              onChange={(event) => setSettings({ ...settings, nothing: event.target.checked })}
            />{" "}
            Nothing from this space
          </label>
          <Button className="w-fit" disabled={saving}>
            Save notifications
          </Button>
        </>
      )}
    </form>
  );
}

function SpaceDefaultReminder({ spaceId }: { spaceId: string }) {
  const [offset, setOffset] = React.useState<string>("none");
  const [ready, setReady] = React.useState(false);
  const [message, setMessage] = React.useState("");
  React.useEffect(() => {
    let active = true;
    void collaborationRpc
      .defaultDeadline({ spaceId })
      .then((result) => {
        if (!active) return;
        if (!result.ok) throw new Error(result.error?.message ?? "Could not load the default reminder.");
        setOffset(result.offsetMinutes === null ? "none" : String(result.offsetMinutes));
        setReady(true);
      })
      .catch((error: unknown) => {
        if (active) setMessage(error instanceof Error ? error.message : "Could not load the default reminder.");
      });
    return () => {
      active = false;
    };
  }, [spaceId]);
  return (
    <form
      className="grid gap-2 border-b pb-4"
      onSubmit={(event) => {
        event.preventDefault();
        void collaborationRpc
          .setDefaultDeadline({ spaceId, offsetMinutes: offset === "none" ? null : Number(offset) })
          .then((result) => {
            setMessage(result.ok ? "Default reminder saved." : (result.error?.message ?? "Could not save the default reminder."));
          })
          .catch(() => setMessage("Could not save the default reminder."));
      }}
    >
      <h3 className="font-semibold">Default deadline reminder</h3>
      <p className="text-sm text-muted-foreground">
        Members can turn this off for themselves. It applies to tasks assigned to them and unassigned tasks.
      </p>
      <label className="grid gap-1 text-sm">
        Remind before the deadline
        <NativeSelect value={offset} onChange={(event) => setOffset(event.target.value)} disabled={!ready}>
          <NativeSelectOption value="none">No default</NativeSelectOption>
          <NativeSelectOption value="0">At the deadline</NativeSelectOption>
          <NativeSelectOption value="-60">1 hour before</NativeSelectOption>
          <NativeSelectOption value="-1440">1 day before</NativeSelectOption>
          <NativeSelectOption value="-10080">1 week before</NativeSelectOption>
        </NativeSelect>
      </label>
      {message && (
        <p role="status" className="text-sm">
          {message}
        </p>
      )}
      <Button className="w-fit" disabled={!ready}>
        Save default reminder
      </Button>
    </form>
  );
}

function SpaceStateSettings({ spaceId }: { spaceId: string }) {
  const [states, setStates] = React.useState<SpaceStateDto[]>([]);
  const [name, setName] = React.useState("");
  const [baseStatus, setBaseStatus] = React.useState("NeedsReview");
  const [error, setError] = React.useState<string | null>(null);
  const [ownerOnly, setOwnerOnly] = React.useState(false);
  const [agentSettings, setAgentSettings] = React.useState<{
    agentsAllowed: boolean;
    claimMinutes: number;
    allowAgentsToClose: boolean;
  } | null>(null);
  const [claimMinutes, setClaimMinutes] = React.useState("120");
  const refresh = React.useCallback(async () => {
    const result = await spaceRpc.states({ spaceId });
    if (!result.ok) throw new Error(result.error?.message ?? "Could not load states.");
    setStates([...result.states]);
  }, [spaceId]);
  React.useEffect(() => {
    void refresh().catch((e: unknown) => setError(String(e)));
  }, [refresh]);
  React.useEffect(() => {
    void spaceRpc
      .tagPolicy({ spaceId })
      .then((result) => {
        if (result.ok) setOwnerOnly(result.ownerOnly);
      })
      .catch((e: unknown) => setError(String(e)));
    void spaceRpc
      .agentSettings({ spaceId })
      .then((result) => {
        if (result.ok) {
          setAgentSettings({
            agentsAllowed: result.agentsAllowed,
            claimMinutes: result.claimMinutes,
            allowAgentsToClose: result.allowAgentsToClose,
          });
          setClaimMinutes(String(result.claimMinutes));
        }
      })
      .catch((e: unknown) => setError(String(e)));
  }, [spaceId]);
  const saveAgentSettings = (change: { agentsAllowed?: boolean; claimMinutes?: number; allowAgentsToClose?: boolean }) =>
    spaceRpc
      .setAgentSettings({
        spaceId,
        agentsAllowed: change.agentsAllowed ?? null,
        claimMinutes: change.claimMinutes ?? null,
        allowAgentsToClose: change.allowAgentsToClose ?? null,
      })
      .then((result) => {
        if (!result.ok) throw new Error(result.error?.message ?? "Could not update the agent settings.");
        setAgentSettings({
          agentsAllowed: result.agentsAllowed,
          claimMinutes: result.claimMinutes,
          allowAgentsToClose: result.allowAgentsToClose,
        });
        setClaimMinutes(String(result.claimMinutes));
      })
      .catch((e: unknown) => setError(String(e)));
  return (
    <section className="grid gap-2" aria-label="Space states">
      <h3 className="font-semibold">Space states</h3>
      <ul>
        {states.map((state) => (
          <li key={state.id} className="flex items-center gap-2 text-sm">
            <span className="flex-1">
              {state.name} ({state.baseStatus})
            </span>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => {
                if (!window.confirm(`Delete ${state.name}? Tasks return to its base status.`)) return;
                void spaceRpc
                  .deleteState({ operationId: crypto.randomUUID(), stateId: state.id, expectedVersion: state.version })
                  .then((result) => {
                    if (!result.ok) throw new Error(result.error?.message ?? "Could not delete state.");
                    return refresh();
                  })
                  .catch((e: unknown) => setError(String(e)));
              }}
            >
              Delete
            </Button>
          </li>
        ))}
      </ul>
      <form
        className="grid gap-2"
        onSubmit={(event) => {
          event.preventDefault();
          void spaceRpc
            .createState({ operationId: crypto.randomUUID(), spaceId, name, baseStatus })
            .then((result) => {
              if (!result.ok) throw new Error(result.error?.message ?? "Could not create state.");
              setName("");
              return refresh();
            })
            .catch((e: unknown) => setError(String(e)));
        }}
      >
        <Field label="New state">
          <Input value={name} onChange={(event) => setName(event.target.value)} />
        </Field>
        <Field label="Base status">
          <NativeSelect value={baseStatus} onChange={(event) => setBaseStatus(event.target.value)}>
            <NativeSelectOption value="NotStarted">Not started</NativeSelectOption>
            <NativeSelectOption value="InProgress">In progress</NativeSelectOption>
            <NativeSelectOption value="WaitingOnSomeone">Waiting on someone</NativeSelectOption>
            <NativeSelectOption value="NeedsReview">Needs review</NativeSelectOption>
          </NativeSelect>
        </Field>
        <Button className="w-fit" disabled={!name.trim()}>
          Add state
        </Button>
      </form>
      <label className="text-sm">
        <input
          type="checkbox"
          checked={ownerOnly}
          onChange={(event) => {
            const next = event.target.checked;
            void spaceRpc
              .setTagPolicy({ spaceId, ownerOnly: next })
              .then((result) => {
                if (!result.ok) throw new Error(result.error?.message ?? "Could not update tag settings.");
                setOwnerOnly(next);
              })
              .catch((e: unknown) => setError(String(e)));
          }}
        />{" "}
        Only owners can change the space tag list
      </label>
      {agentSettings && (
        <div className="grid gap-2 rounded-md border p-3 text-sm" aria-label="Agent settings">
          <h4 className="font-medium">Agents</h4>
          <label>
            <input
              type="checkbox"
              checked={agentSettings.agentsAllowed}
              onChange={(event) => void saveAgentSettings({ agentsAllowed: event.target.checked })}
            />{" "}
            Allow agents in this space
          </label>
          <Field label="Claim time in minutes (15 to 1440)">
            <Input
              type="number"
              min={15}
              max={1440}
              value={claimMinutes}
              onChange={(event) => setClaimMinutes(event.target.value)}
              onBlur={() => {
                const minutes = Number(claimMinutes);
                if (agentSettings && Number.isFinite(minutes) && minutes !== agentSettings.claimMinutes && minutes >= 15 && minutes <= 1440)
                  void saveAgentSettings({ claimMinutes: minutes });
                else setClaimMinutes(String(agentSettings?.claimMinutes ?? 120));
              }}
            />
          </Field>
          <label>
            <input
              type="checkbox"
              checked={agentSettings.allowAgentsToClose}
              onChange={(event) => void saveAgentSettings({ allowAgentsToClose: event.target.checked })}
            />{" "}
            Agents may close tasks themselves
          </label>
          <p className="text-muted-foreground">
            A claim expires after this time without activity and the task returns to Ready for agent. Members → Add agent needs agents
            allowed here.
          </p>
        </div>
      )}
      {error && <InlineError>{error}</InlineError>}
    </section>
  );
}

function SpaceTagSettings({ spaceId }: { spaceId: string }) {
  const [tags, setTags] = React.useState<readonly TagResponse[]>([]);
  const [name, setName] = React.useState("");
  const [error, setError] = React.useState<string | null>(null);
  const refresh = React.useCallback(async () => {
    const result = await tagRpc.listSpace({ spaceId });
    if (!result.ok) throw new Error(result.error?.message ?? "Could not load space tags.");
    setTags(result.tags);
  }, [spaceId]);
  React.useEffect(() => {
    void refresh().catch((cause: unknown) => setError(String(cause)));
  }, [refresh]);
  const perform = (action: () => Promise<{ ok: boolean; error: { message: string } | null }>) => {
    void action()
      .then((result) => {
        if (!result.ok) throw new Error(result.error?.message ?? "Could not change tag.");
        return refresh();
      })
      .catch((cause: unknown) => setError(String(cause)));
  };
  return (
    <section className="grid gap-2" aria-label="Space tags">
      <h3 className="font-semibold">Space tags</h3>
      <ul>
        {tags.map((tag) => (
          <li key={tag.id} className="flex items-center gap-1 text-sm">
            <span className="flex-1">#{tag.path}</span>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => {
                const next = window.prompt("Rename tag", tag.name);
                if (next?.trim())
                  perform(() =>
                    tagRpc.rename({ operationId: crypto.randomUUID(), tagId: tag.id, expectedVersion: tag.version, name: next }),
                  );
              }}
            >
              Rename
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => {
                const targetPath = window.prompt("Merge into which space tag? Enter its full path.");
                const target = tags.find((row) => row.path.toLocaleLowerCase() === targetPath?.trim().toLocaleLowerCase());
                if (target)
                  perform(() =>
                    tagRpc.merge({
                      operationId: crypto.randomUUID(),
                      sourceId: tag.id,
                      sourceVersion: tag.version,
                      targetId: target.id,
                      targetVersion: target.version,
                    }),
                  );
                else if (targetPath) setError("Choose an existing space tag path.");
              }}
            >
              Merge
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              onClick={() => {
                if (window.confirm(`Delete #${tag.path}?`))
                  perform(() => tagRpc.delete({ operationId: crypto.randomUUID(), tagId: tag.id, expectedVersion: tag.version }));
              }}
            >
              Delete
            </Button>
          </li>
        ))}
      </ul>
      <form
        className="flex gap-2"
        onSubmit={(event) => {
          event.preventDefault();
          perform(async () => {
            const result = await tagRpc.createSpace({ operationId: crypto.randomUUID(), spaceId, path: name });
            if (result.ok) setName("");
            return result;
          });
        }}
      >
        <Input aria-label="New space tag" value={name} onChange={(event) => setName(event.target.value)} />
        <Button disabled={!name.trim()}>Add tag</Button>
      </form>
      {error && <InlineError>{error}</InlineError>}
    </section>
  );
}

/** A shared space's home (§9.1): Overview (with the standups), Members and, for an owner, Settings. */
export function SpaceHome({
  space,
  lists,
  onOpenList,
  onLeft,
  onChanged,
  api = spaceRpc,
  updatesApi = spaceUpdateRpc,
  connectionsApi = gitHubRpc,
  agentsApi = agentRpc,
  revision = 0,
}: {
  space: SpaceResponse;
  lists: readonly ContainerResponse[];
  onOpenList: (id: string) => void;
  onLeft: () => void;
  onChanged: () => void;
  api?: SpaceApi;
  /** Standups of the space (Q14); the real client unless a test answers. */
  updatesApi?: typeof spaceUpdateRpc;
  /** The space's GitHub connection (Q15); the real client unless a test answers. */
  connectionsApi?: typeof gitHubRpc;
  /** The space's agents (PLAN3 12.3): who could be added, and pausing or removing one; the real client unless a test answers. */
  agentsApi?: Pick<typeof AgentRpc, "spaceAgents" | "addToSpace" | "pauseInSpace" | "removeFromSpace">;
  /** Bumps when anything on this computer changed, so standups posted by other members appear. */
  revision?: number;
}) {
  const access = accessIn([space], space.spaceId);
  const [tab, setTab] = React.useState<HomeTab>("overview");
  const [members, setMembers] = React.useState<SpaceMemberResponse[]>([]);
  /** The agents of this space with their state; empty while loading or offline, when the member list still names them. */
  const [agents, setAgents] = React.useState<readonly SpaceAgentDto[]>([]);
  const [inviting, setInviting] = React.useState(false);
  const [addingAgent, setAddingAgent] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const [myName, setMyName] = React.useState("");
  const [myGitHubLogin, setMyGitHubLogin] = React.useState("");
  const [notice, setNotice] = React.useState<string | null>(null);
  const { busy, error, run } = useAction();
  // The latest agent client, read inside the callback, so an inline object does not make every render reload the members.
  const agentsApiRef = React.useRef(agentsApi);
  agentsApiRef.current = agentsApi;

  const loadMembers = React.useCallback(async () => {
    const result = await api.members({ spaceId: space.spaceId });
    if (result.ok) {
      setMembers([...result.members]);
      setMyName((current) => current || (result.members.find((m) => m.isMe)?.name ?? ""));
      setMyGitHubLogin((current) => current || (result.members.find((m) => m.isMe)?.gitHubLogin ?? ""));
    }
    // Offline, the agents' state is simply unknown; the member list still shows them.
    const spaceAgents = await agentsApiRef.current.spaceAgents({ spaceId: space.spaceId }).catch(() => null);
    if (spaceAgents?.ok) setAgents(spaceAgents.agents);
  }, [api, space.spaceId]);
  React.useEffect(() => {
    setTab("overview");
    setMembers([]);
    setMyName("");
    setMyGitHubLogin("");
  }, [space.spaceId]);
  React.useEffect(() => {
    if (tab === "members") void loadMembers().catch(() => undefined);
  }, [tab, loadMembers]);

  const tabs: [HomeTab, string][] = [
    ["overview", "Overview"],
    ["members", "Members"],
    ["notifications", "Notifications"],
    ...(access.canEdit ? ([["tags", "Tags"]] as [HomeTab, string][]) : []),
    ...(access.isOwner
      ? ([
          ["settings", "Settings"],
          ["connections", "Connections"],
        ] as [HomeTab, string][])
      : []),
  ];
  const projects = lists.filter((list) => list.kind === "Project");
  const plain = lists.filter((list) => list.kind !== "Project");

  return (
    <section aria-label={`${space.name} home`} className="grid gap-4">
      <PageHeader
        eyebrow="Shared space"
        title={space.name}
        subtitle={`${access.sentence ?? ""} · ${space.memberCount} ${space.memberCount === 1 ? "person" : "people"}`}
        actions={
          access.isOwner && (
            <Button type="button" onClick={() => setInviting(true)}>
              <Users />
              Invite people
            </Button>
          )
        }
      />
      <div role="tablist" aria-label="Space" className="flex gap-1 border-b">
        {tabs.map(([id, text]) => (
          <button
            key={id}
            role="tab"
            type="button"
            aria-selected={tab === id}
            className={cn(
              "-mb-px border-b-2 px-3 py-1.5 text-sm outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50",
              tab === id ? "border-primary font-medium" : "border-transparent text-muted-foreground hover:text-foreground",
            )}
            onClick={() => setTab(id)}
          >
            {text}
          </button>
        ))}
      </div>
      {notice && (
        <p role="status" className="text-sm">
          {notice}
        </p>
      )}
      {error && <InlineError>{error}</InlineError>}

      {tab === "overview" && (
        <div role="tabpanel" aria-label="Overview" className="grid gap-3">
          <SpaceStandups spaceId={space.spaceId} access={access} api={updatesApi} revision={revision} onChanged={onChanged} />
          {lists.length === 0 ? (
            <EmptyState title="No lists in this space yet.">
              Open a Personal list and choose Move to space… to bring it here
              {access.canEdit ? "." : ", or ask an editor to add one."}
            </EmptyState>
          ) : (
            <>
              {projects.length > 0 && <ListLinks title="Projects" lists={projects} onOpen={onOpenList} />}
              {plain.length > 0 && <ListLinks title="Lists" lists={plain} onOpen={onOpenList} />}
            </>
          )}
          <p className="text-xs text-muted-foreground">Recent changes by other people show in this space’s lists as they sync.</p>
        </div>
      )}

      {tab === "members" && (
        <div role="tabpanel" aria-label="Members" className="grid gap-4">
          {access.isOwner && (
            <div>
              <Button type="button" variant="outline" size="sm" disabled={busy} onClick={() => setAddingAgent(true)}>
                <Bot />
                Add agent
              </Button>
            </div>
          )}
          <ul className="grid gap-1" aria-label="People in this space">
            {members.map((member) => {
              const agent =
                member.kind === AGENT_KIND
                  ? agents.find((entry) => entry.agent.id.toLowerCase() === member.memberId.toLowerCase())
                  : undefined;
              const paused = !!agent?.agent.pausedAtUtc;
              return (
                <li key={member.memberId} className="flex items-center gap-2 rounded-md border px-3 py-2 text-sm">
                  <span className="min-w-0 flex-1 truncate">
                    {member.name}
                    {member.kind === AGENT_KIND && <span className="text-muted-foreground"> (agent)</span>}
                    {member.isMe && <span className="text-muted-foreground"> (you)</span>}
                    {member.gitHubLogin && <span className="text-xs text-muted-foreground"> · @{member.gitHubLogin}</span>}
                  </span>
                  {member.kind === AGENT_KIND ? (
                    <>
                      <span className="text-xs text-muted-foreground">{paused ? "Agent · paused" : "Agent"}</span>
                      {access.isOwner && (
                        <>
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            disabled={busy}
                            aria-label={`${paused ? "Resume" : "Pause"} ${member.name}`}
                            onClick={() =>
                              void run(() =>
                                agentsApi.pauseInSpace({ spaceId: space.spaceId, agentId: member.memberId, paused: !paused }),
                              ).then(async (result) => {
                                if (!result) return;
                                setNotice(`${member.name} was ${paused ? "resumed" : "paused"}.`);
                                await loadMembers();
                                onChanged();
                              })
                            }
                          >
                            {paused ? "Resume" : "Pause"}
                          </Button>
                          <Button
                            type="button"
                            variant="ghost"
                            size="sm"
                            disabled={busy}
                            aria-label={`Remove ${member.name}`}
                            onClick={() =>
                              void run(() => agentsApi.removeFromSpace({ spaceId: space.spaceId, agentId: member.memberId })).then(
                                async (result) => {
                                  if (!result) return;
                                  setNotice(`${member.name} was removed.`);
                                  await loadMembers();
                                  onChanged();
                                },
                              )
                            }
                          >
                            Remove
                          </Button>
                        </>
                      )}
                    </>
                  ) : access.isOwner ? (
                    <NativeSelect
                      aria-label={`Role of ${member.name}`}
                      value={String(member.role)}
                      disabled={busy}
                      onChange={(e) =>
                        void run(() =>
                          api.changeRole({ spaceId: space.spaceId, memberId: member.memberId, role: Number(e.target.value) }),
                        ).then(async () => {
                          await loadMembers();
                          onChanged();
                        })
                      }
                    >
                      <NativeSelectOption value={String(OWNER)}>Owner</NativeSelectOption>
                      <NativeSelectOption value={String(EDITOR)}>Editor</NativeSelectOption>
                      <NativeSelectOption value={String(VIEWER)}>Viewer</NativeSelectOption>
                    </NativeSelect>
                  ) : (
                    <span className="text-xs text-muted-foreground">{roleName(member.role)}</span>
                  )}
                  {member.kind !== AGENT_KIND && access.isOwner && !member.isMe && (
                    <Button
                      type="button"
                      variant="ghost"
                      size="sm"
                      disabled={busy}
                      aria-label={`Remove ${member.name}`}
                      onClick={() =>
                        void run(() => api.removeMember({ spaceId: space.spaceId, memberId: member.memberId })).then(async (result) => {
                          if (!result) return;
                          setNotice(`${member.name} was removed.`);
                          await loadMembers();
                          onChanged();
                        })
                      }
                    >
                      Remove
                    </Button>
                  )}
                </li>
              );
            })}
          </ul>
          <form
            className="grid max-w-sm gap-2"
            onSubmit={(event) => {
              event.preventDefault();
              void run(() => api.setMyName({ spaceId: space.spaceId, displayName: myName })).then(async (result) => {
                if (!result) return;
                setNotice("Your name in this space was updated.");
                await loadMembers();
              });
            }}
          >
            <Field label="My name in this space">
              <Input value={myName} maxLength={80} onChange={(e) => setMyName(e.target.value)} autoComplete="off" />
            </Field>
            <p className="text-xs text-muted-foreground">Only people in this space see it.</p>
            <Button className="w-fit" disabled={busy || !myName.trim()}>
              Save my name
            </Button>
          </form>
          <form
            className="grid max-w-sm gap-2"
            onSubmit={(event) => {
              event.preventDefault();
              void run(() => api.setMyGitHubLogin({ spaceId: space.spaceId, gitHubLogin: myGitHubLogin.trim() || null })).then(
                async (result) => {
                  if (!result) return;
                  setNotice(myGitHubLogin.trim() ? "Your GitHub login was saved." : "Your GitHub login was cleared.");
                  await loadMembers();
                },
              );
            }}
          >
            <Field label="My GitHub login">
              <Input
                value={myGitHubLogin}
                maxLength={40}
                onChange={(e) => setMyGitHubLogin(e.target.value)}
                autoComplete="off"
                placeholder="octocat"
              />
            </Field>
            <p className="text-xs text-muted-foreground">Used to match GitHub assignees to you. It is not verified against GitHub.</p>
            <Button className="w-fit" disabled={busy}>
              {myGitHubLogin.trim() ? "Save GitHub login" : "Clear GitHub login"}
            </Button>
          </form>
          <div className="border-t pt-3">
            <Button
              type="button"
              variant="outline"
              disabled={busy}
              onClick={() =>
                void run(() => api.leave({ spaceId: space.spaceId })).then((result) => {
                  if (result) onLeft();
                })
              }
            >
              Leave this space
            </Button>
            <p className="mt-1 text-xs text-muted-foreground">
              The space’s lists leave this computer. Your own My Day entries and reminders for them are cleared. The last owner cannot
              leave.
            </p>
          </div>
        </div>
      )}

      {tab === "notifications" && <SpaceNotifications spaceId={space.spaceId} />}
      {tab === "tags" && access.canEdit && (
        <div role="tabpanel" aria-label="Tags">
          <SpaceTagSettings spaceId={space.spaceId} />
        </div>
      )}
      {tab === "connections" && access.isOwner && (
        <div role="tabpanel" aria-label="Connections" className="grid gap-4">
          <GitHubSettings api={connectionsApi} spaceId={space.spaceId} onSynced={onChanged} />
        </div>
      )}
      {tab === "settings" && access.isOwner && (
        <div role="tabpanel" aria-label="Settings" className="grid max-w-lg gap-3">
          <SpaceDefaultReminder spaceId={space.spaceId} />
          <SpaceStateSettings spaceId={space.spaceId} />
          <h3 className="font-semibold">Delete this space</h3>
          <p className="text-sm">
            Deleting removes the space for everyone. You can restore it within 30 days. After that it and everything in it is deleted for
            good.
          </p>
          <Button type="button" variant="destructive" className="w-fit" onClick={() => setDeleting(true)}>
            Delete space…
          </Button>
        </div>
      )}

      {inviting && <InviteDialog space={space} api={api} githubApi={connectionsApi} onClose={() => setInviting(false)} />}
      {addingAgent && (
        <AddAgentDialog
          space={space}
          lists={lists}
          api={agentsApi}
          onClose={() => setAddingAgent(false)}
          onAdded={() => {
            setAddingAgent(false);
            setNotice("Agent added.");
            void loadMembers();
            onChanged();
          }}
        />
      )}
      {deleting && (
        <DeleteSpaceDialog
          space={space}
          api={api}
          onClose={() => setDeleting(false)}
          onDeleted={() => {
            setDeleting(false);
            onLeft();
          }}
        />
      )}
    </section>
  );
}

/**
 * The standups of a space (PLAN3 9.12), newest first. A member posts one from an evening summary after reviewing the
 * suggested text; the author edits it, the author or an owner deletes it. Other people's text is rendered as Markdown.
 */
export function SpaceStandups({
  spaceId,
  access,
  api,
  revision,
  onChanged,
}: {
  spaceId: string;
  access: SpaceAccess;
  api: typeof spaceUpdateRpc;
  revision: number;
  onChanged: () => void;
}) {
  const [items, setItems] = React.useState<SpaceUpdateDto[]>([]);
  const [loadError, setLoadError] = React.useState<string | null>(null);
  const [editing, setEditing] = React.useState<{ id: string; version: string; markdown: string } | null>(null);
  const [confirming, setConfirming] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const { busy, error, run } = useAction();

  // The space on screen: a list that answers after the person moved to another space is dropped, not shown there.
  const currentSpace = React.useRef(spaceId);
  currentSpace.current = spaceId;
  const load = React.useCallback(async () => {
    const result = await api.list({ spaceId });
    if (currentSpace.current !== spaceId) return;
    if (result.ok) {
      setItems([...result.updates]);
      setLoadError(null);
    } else setLoadError(result.error?.message ?? "Could not load the standups.");
  }, [api, spaceId]);
  React.useEffect(() => {
    void load().catch((e: unknown) => setLoadError(String(e)));
  }, [load, revision]);
  React.useEffect(() => {
    setEditing(null);
    setConfirming(null);
    setNotice(null);
    setItems([]);
  }, [spaceId]);

  async function save() {
    if (!editing) return;
    const result = await run(() =>
      api.edit({ operationId: crypto.randomUUID(), updateId: editing.id, expectedVersion: editing.version, markdown: editing.markdown }),
    );
    if (!result) return;
    setEditing(null);
    setNotice("Standup updated.");
    await load();
    onChanged();
  }

  async function remove(id: string, version: string) {
    const result = await run(() => api.delete({ operationId: crypto.randomUUID(), updateId: id, expectedVersion: version }));
    if (!result) return;
    setConfirming(null);
    setNotice("Standup deleted.");
    await load();
    onChanged();
  }

  return (
    <section aria-label="Standups" className="grid gap-1.5">
      <h3 className="text-xs font-semibold tracking-wider text-muted-foreground uppercase">Standups</h3>
      {items.length === 0 && !loadError && (
        <p className="text-sm text-muted-foreground">No standups yet. Post one from an evening summary.</p>
      )}
      {(loadError ?? error) && <InlineError>{loadError ?? error}</InlineError>}
      {notice && (
        <p role="status" className="text-sm">
          {notice}
        </p>
      )}
      <ol className="grid gap-2">
        {items.map((item) => (
          <li key={item.id} className="grid gap-1 rounded-lg border bg-card px-3 py-2 text-sm" aria-label={`Standup by ${item.authorName}`}>
            <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
              <bdi className="font-medium text-foreground">{item.authorName}</bdi>
              <span>{dayLabel(item.forDate)}</span>
              {item.editedAtUtc && <span>edited</span>}
              <span className="ml-auto flex gap-1">
                {item.isMine && (
                  <Button
                    type="button"
                    size="xs"
                    variant="ghost"
                    onClick={() => setEditing({ id: item.id, version: item.version, markdown: item.rawMarkdown })}
                  >
                    Edit
                  </Button>
                )}
                {(item.isMine || access.isOwner) &&
                  (confirming === item.id ? (
                    <>
                      <Button
                        type="button"
                        size="xs"
                        variant="destructive"
                        disabled={busy}
                        onClick={() => void remove(item.id, item.version)}
                      >
                        Delete
                      </Button>
                      <Button type="button" size="xs" variant="ghost" onClick={() => setConfirming(null)}>
                        Keep
                      </Button>
                    </>
                  ) : (
                    <Button
                      type="button"
                      size="xs"
                      variant="ghost"
                      aria-label={`Delete standup by ${item.authorName}`}
                      onClick={() => setConfirming(item.id)}
                    >
                      Delete
                    </Button>
                  ))}
              </span>
            </div>
            {editing?.id === item.id ? (
              <form
                className="grid gap-2"
                onSubmit={(event) => {
                  event.preventDefault();
                  void save();
                }}
              >
                <MarkdownEditor
                  id={`standup-${item.id}`}
                  label="Standup"
                  rows={6}
                  value={editing.markdown}
                  onChange={(markdown) => setEditing({ ...editing, markdown })}
                  spaceId={spaceId}
                />
                <div className="flex gap-2">
                  <Button size="sm" disabled={busy || !editing.markdown.trim()}>
                    Save standup
                  </Button>
                  <Button type="button" size="sm" variant="outline" onClick={() => setEditing(null)}>
                    Cancel
                  </Button>
                </div>
              </form>
            ) : (
              <MarkdownPreview source={item.rawMarkdown} compact className="text-sm" />
            )}
          </li>
        ))}
      </ol>
    </section>
  );
}

/** A yyyy-MM-dd date as a short day (no time zone involved). */
function dayLabel(date: string) {
  const [year, month, day] = date.split("-").map(Number);
  return new Date(Date.UTC(year, month - 1, day)).toLocaleDateString(undefined, {
    timeZone: "UTC",
    day: "numeric",
    month: "short",
    year: "numeric",
  });
}

function ListLinks({ title, lists, onOpen }: { title: string; lists: readonly ContainerResponse[]; onOpen: (id: string) => void }) {
  return (
    <section aria-label={title} className="grid gap-1">
      <h3 className="text-xs font-semibold tracking-wider text-muted-foreground uppercase">{title}</h3>
      <ul className="grid gap-1">
        {lists.map((list) => (
          <li key={list.id}>
            <Button type="button" variant="ghost" className="w-full justify-start" onClick={() => onOpen(list.id)}>
              {list.kind === "Project" ? <FolderKanban /> : <ListTodo />}
              {list.name}
            </Button>
          </li>
        ))}
      </ul>
    </section>
  );
}

/** One shared space in the sidebar (§9.1): collapsible, with its home and its lists under it. */
export function SpaceSidebarSection({
  space,
  lists,
  activeListId,
  homeActive,
  onOpenHome,
  onOpenList,
  navItem,
}: {
  space: SpaceResponse;
  lists: readonly ContainerResponse[];
  activeListId: string | null;
  homeActive: boolean;
  onOpenHome: () => void;
  onOpenList: (id: string) => void;
  navItem: string;
}) {
  const [open, setOpen] = React.useState(true);
  return (
    <section aria-label={`${space.name} space`} className="grid gap-px pt-2">
      <button
        type="button"
        aria-expanded={open}
        className="flex items-center gap-1 px-2 py-1 text-left text-xs font-semibold tracking-wider text-muted-foreground uppercase outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
        onClick={() => setOpen((v) => !v)}
      >
        <ChevronRight className={cn("size-3.5 transition-transform", open && "rotate-90")} />
        <span className="truncate">{space.name}</span>
      </button>
      {open && (
        <>
          <Button type="button" variant="ghost" className={navItem} aria-current={homeActive ? "page" : undefined} onClick={onOpenHome}>
            <Home />
            <span className="truncate">Space home</span>
          </Button>
          {lists.map((list) => (
            <Button
              key={list.id}
              type="button"
              variant="ghost"
              className={navItem}
              aria-current={activeListId === list.id ? "page" : undefined}
              onClick={() => onOpenList(list.id)}
            >
              {list.kind === "Project" ? <FolderKanban /> : <ListTodo />}
              <span className="truncate">{list.name}</span>
            </Button>
          ))}
        </>
      )}
    </section>
  );
}
