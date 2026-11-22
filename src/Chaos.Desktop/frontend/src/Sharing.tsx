import React from "react";
import {
  collaborationRpc,
  spaceRpc,
  type ContainerResponse,
  type SpaceInviteResponseItem,
  type SpaceMemberResponse,
  type SpaceResponse,
  type SpaceNotificationPreference,
} from "#neoastra";
import { ChevronRight, FolderKanban, Home, ListTodo, Users } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { EmptyState, Field, InlineError, PageHeader } from "@/components/common";

/** Roles as the service numbers them (PLAN3 §8.3). */
export const OWNER = 1;
export const EDITOR = 2;
export const VIEWER = 3;
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
}: {
  space: { spaceId: string; name: string };
  onClose: () => void;
  api?: Pick<SpaceApi, "invite" | "invites" | "revokeInvite">;
}) {
  const [mode, setMode] = React.useState<"email" | "link">("email");
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
          <Button disabled={busy || (mode === "email" && !email.trim())}>{mode === "email" ? "Send invitation" : "Create link"}</Button>
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
            void run(() => api.move({ containerId: list.id, spaceId })).then((result) => {
              if (result) onMoved(spaceId);
            });
          }}
        >
          <Field label="Shared space">
            <NativeSelect value={spaceId} onChange={(e) => setSpaceId(e.target.value)}>
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
          {error && <InlineError>{error}</InlineError>}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button disabled={busy || !spaceId}>Move</Button>
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

type HomeTab = "overview" | "members" | "notifications" | "settings";

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

/** A shared space's home (§9.1): Overview, Members and, for an owner, Settings. */
export function SpaceHome({
  space,
  lists,
  onOpenList,
  onLeft,
  onChanged,
  api = spaceRpc,
}: {
  space: SpaceResponse;
  lists: readonly ContainerResponse[];
  onOpenList: (id: string) => void;
  onLeft: () => void;
  onChanged: () => void;
  api?: SpaceApi;
}) {
  const access = accessIn([space], space.spaceId);
  const [tab, setTab] = React.useState<HomeTab>("overview");
  const [members, setMembers] = React.useState<SpaceMemberResponse[]>([]);
  const [inviting, setInviting] = React.useState(false);
  const [deleting, setDeleting] = React.useState(false);
  const [myName, setMyName] = React.useState("");
  const [notice, setNotice] = React.useState<string | null>(null);
  const { busy, error, run } = useAction();

  const loadMembers = React.useCallback(async () => {
    const result = await api.members({ spaceId: space.spaceId });
    if (result.ok) {
      setMembers([...result.members]);
      setMyName((current) => current || (result.members.find((m) => m.isMe)?.name ?? ""));
    }
  }, [api, space.spaceId]);
  React.useEffect(() => {
    setTab("overview");
    setMembers([]);
    setMyName("");
  }, [space.spaceId]);
  React.useEffect(() => {
    if (tab === "members") void loadMembers().catch(() => undefined);
  }, [tab, loadMembers]);

  const tabs: [HomeTab, string][] = [
    ["overview", "Overview"],
    ["members", "Members"],
    ["notifications", "Notifications"],
    ...(access.isOwner ? ([["settings", "Settings"]] as [HomeTab, string][]) : []),
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
          <ul className="grid gap-1" aria-label="People in this space">
            {members.map((member) => (
              <li key={member.memberId} className="flex items-center gap-2 rounded-md border px-3 py-2 text-sm">
                <span className="min-w-0 flex-1 truncate">
                  {member.name}
                  {member.isMe && <span className="text-muted-foreground"> (you)</span>}
                </span>
                {access.isOwner ? (
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
                {access.isOwner && !member.isMe && (
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
            ))}
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
      {tab === "settings" && access.isOwner && (
        <div role="tabpanel" aria-label="Settings" className="grid max-w-lg gap-3">
          <SpaceDefaultReminder spaceId={space.spaceId} />
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

      {inviting && <InviteDialog space={space} api={api} onClose={() => setInviting(false)} />}
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
