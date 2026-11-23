import React from "react";
import {
  delegationRpc,
  type delegationRpc as DelegationRpc,
  peopleRpc,
  type DelegationDto,
  type DelegationItemDto,
  type FollowUpDto,
  type NudgeDto,
  type PersonResponse,
  type TaskResponse,
} from "#neoastra";
import { Handshake, MessageSquareWarning } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Field, FieldGroup, InlineError } from "@/components/common";
import { Modal } from "./Sharing";
import { delegationMoves, describeDelegation, isDelegationActive, recentNudges, type DelegationSide } from "./delegation";

const operationId = () => crypto.randomUUID();
const nudgeLimit = 3;
/** A yyyy-MM-dd date in the person's locale (no time zone involved). */
export function dayText(date: string) {
  const [year, month, day] = date.split("-").map(Number);
  return new Date(Date.UTC(year, month - 1, day)).toLocaleDateString(undefined, {
    timeZone: "UTC",
    day: "numeric",
    month: "short",
    year: "numeric",
  });
}

export type DelegationApi = Pick<
  typeof DelegationRpc,
  "get" | "list" | "nudgeTemplate" | "delegate" | "respond" | "done" | "close" | "reopen" | "withdraw" | "nudge"
>;

/** Which side of a delegation the local person is on, read from the directory's `isMe` flags. */
export function sideOf(delegation: DelegationDto, people: readonly PersonResponse[]): DelegationSide {
  const from = people.find((person) => person.actorId === `person:${delegation.fromMemberId}`);
  return from?.isMe ? "From" : "To";
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

function FollowUpLine({ followUp, dueAtUtc, overdue }: { followUp: FollowUpDto | null; dueAtUtc: string | null; overdue: boolean }) {
  if (!followUp) return null;
  const unit = (followUp.unit ?? "Days").toLowerCase();
  const when =
    followUp.kind === "Fixed"
      ? followUp.fixedAtUtc
        ? new Date(followUp.fixedAtUtc).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })
        : ""
      : followUp.kind === "AfterInactivity"
        ? `after ${followUp.amount} ${followUp.amount === 1 ? "day" : "days"} without activity`
        : `${followUp.amount} ${unit} ${followUp.kind === "BeforeDue" ? "before the due date" : "after waiting started"}`;
  return (
    <span className={overdue ? "text-xs text-destructive" : "text-xs text-muted-foreground"}>
      Your follow-up: {when}
      {dueAtUtc && followUp.kind !== "Fixed"
        ? ` (due ${new Date(dueAtUtc).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })})`
        : ""}
      {overdue ? " · overdue" : ""}
    </span>
  );
}

/** One delegation card: who, where it stands, the brief, and the moves this person may make. */
export function DelegationCard({
  item,
  side,
  people,
  api,
  onChanged,
  onOpenNudge,
}: {
  item: DelegationItemDto;
  side: DelegationSide;
  people: readonly PersonResponse[];
  api: DelegationApi;
  onChanged: (task: TaskResponse | null) => void;
  onOpenNudge: (item: DelegationItemDto) => void;
}) {
  const { busy, error, run } = useAction();
  const delegation = item.delegation;
  const other = side === "From" ? item.toName : item.fromName;
  const moves = delegationMoves(delegation.state, side);
  const nudges = recentNudges(item, Date.now());
  const canNudge = nudges < nudgeLimit;
  const [ask, setAsk] = React.useState<"Question" | "Decline" | null>(null);
  const [askText, setAskText] = React.useState("");

  async function act(action: () => Promise<{ ok: boolean; error: { message: string } | null; task: TaskResponse | null }>) {
    const result = await run(action);
    if (result) onChanged(result.task);
    return result;
  }
  const action = (kind: "close" | "reopen" | "withdraw") =>
    act(() => api[kind]({ operationId: operationId(), delegationId: delegation.id, expectedVersion: delegation.version }));

  return (
    <div className="grid gap-1 rounded-lg border p-3" data-delegation-state={delegation.state}>
      <div className="flex flex-wrap items-center gap-2 text-sm">
        <Handshake className="size-4 text-muted-foreground" aria-hidden />
        <span>
          {side === "From" ? (
            <>
              Delegated to <bdi>{other}</bdi>
            </>
          ) : (
            <>
              From <bdi>{other}</bdi>
            </>
          )}
          <span className="text-muted-foreground"> · {describeDelegation(delegation.state, side)}</span>
        </span>
        {delegation.dueDate && <span className="text-xs text-muted-foreground">due {dayText(delegation.dueDate)}</span>}
        {delegation.reviewRequired && <span className="text-xs text-muted-foreground">review needed</span>}
      </div>
      {delegation.message && (
        <p className="text-sm whitespace-pre-wrap" dir="auto">
          {delegation.message}
        </p>
      )}
      {delegation.doneDefinition && (
        <p className="text-sm text-muted-foreground" dir="auto">
          Done looks like: {delegation.doneDefinition}
        </p>
      )}
      {delegation.response && (
        <p className="text-sm whitespace-pre-wrap" dir="auto">
          <MessageSquareWarning className="inline size-3.5 text-muted-foreground" aria-hidden />{" "}
          {delegation.state === "Question" ? "Question" : "Reason"}: {delegation.response}
        </p>
      )}
      {side === "From" && <FollowUpLine followUp={item.followUp} dueAtUtc={item.followUpDueAtUtc} overdue={item.followUpOverdue} />}
      {item.nudges.length > 0 && (
        <p className="text-xs text-muted-foreground">
          Nudged {item.nudges.length === 1 ? "once" : `${item.nudges.length} times`}
          {nudges > 0 ? ` (${nudges} in the last 24 hours)` : ""}: “{item.nudges[item.nudges.length - 1].text}”
        </p>
      )}
      {ask && (
        <form
          className="grid gap-2"
          onSubmit={(event) => {
            event.preventDefault();
            const text = askText.trim();
            if (!text) return;
            void act(() =>
              api.respond({
                operationId: operationId(),
                delegationId: delegation.id,
                expectedVersion: delegation.version,
                response: ask,
                text,
              }),
            ).then((result) => {
              if (result) setAsk(null);
            });
          }}
        >
          <Field label={ask === "Question" ? "Your question" : "Why are you declining?"}>
            <textarea
              name="text"
              maxLength={4000}
              rows={3}
              autoFocus
              required
              value={askText}
              onChange={(event) => setAskText(event.target.value)}
              className="w-full rounded-md border bg-background px-2 py-1 text-sm"
              dir="auto"
            />
          </Field>
          <div className="flex gap-2">
            <Button type="submit" size="sm" disabled={busy}>
              {ask === "Question" ? "Ask" : "Decline"}
            </Button>
            <Button type="button" size="sm" variant="ghost" onClick={() => setAsk(null)}>
              Cancel
            </Button>
          </div>
        </form>
      )}
      {error && <InlineError>{error}</InlineError>}
      {!ask && moves.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {moves.includes("Accept") && (
            <Button
              size="sm"
              variant="outline"
              disabled={busy}
              onClick={() =>
                void act(() =>
                  api.respond({
                    operationId: operationId(),
                    delegationId: delegation.id,
                    expectedVersion: delegation.version,
                    response: "Accept",
                    text: null,
                  }),
                )
              }
            >
              Accept
            </Button>
          )}
          {moves.includes("Question") && (
            <Button size="sm" variant="outline" disabled={busy} onClick={() => setAsk("Question")}>
              Ask
            </Button>
          )}
          {moves.includes("Decline") && (
            <Button size="sm" variant="outline" disabled={busy} onClick={() => setAsk("Decline")}>
              Decline
            </Button>
          )}
          {moves.includes("Done") && (
            <Button
              size="sm"
              disabled={busy}
              onClick={() =>
                void act(() =>
                  api.done({
                    operationId: operationId(),
                    delegationId: delegation.id,
                    expectedVersion: delegation.version,
                    summary: null,
                    openSubtaskChoice: null,
                  }),
                )
              }
            >
              Mark done
            </Button>
          )}
          {moves.includes("Close") && (
            <Button size="sm" disabled={busy} onClick={() => void action("close")}>
              Accept result
            </Button>
          )}
          {moves.includes("Reopen") && (
            <Button size="sm" variant="outline" disabled={busy} onClick={() => void action("reopen")}>
              Send back
            </Button>
          )}
          {moves.includes("Withdraw") && (
            <Button size="sm" variant="outline" disabled={busy} onClick={() => void action("withdraw")}>
              Take back
            </Button>
          )}
          {moves.includes("Nudge") && (
            <Button
              size="sm"
              variant="outline"
              disabled={busy || !canNudge}
              title={canNudge ? undefined : `You have nudged ${nudges} times in the last 24 hours; the limit is ${nudgeLimit}.`}
              onClick={() => onOpenNudge(item)}
            >
              Nudge
            </Button>
          )}
        </div>
      )}
    </div>
  );
}

/** The delegate form: who, the message, what done looks like, the due date, review and the delegator's follow-up. */
export function DelegateDialog({
  task,
  people,
  onClose,
  onDelegated,
  api,
}: {
  task: TaskResponse;
  people: readonly PersonResponse[];
  onClose: () => void;
  onDelegated: (task: TaskResponse | null) => void;
  api: DelegationApi;
}) {
  const { busy, error, run } = useAction();
  const [member, setMember] = React.useState("");
  const [message, setMessage] = React.useState("");
  const [doneDefinition, setDoneDefinition] = React.useState("");
  const [dueDate, setDueDate] = React.useState("");
  const [review, setReview] = React.useState(true);
  const [followUpKind, setFollowUpKind] = React.useState("None");
  const [followUpAt, setFollowUpAt] = React.useState("");
  const [followUpAmount, setFollowUpAmount] = React.useState("");
  const [followUpUnit, setFollowUpUnit] = React.useState("Days");
  const eligible = people.filter(
    (person) => person.kind === "person" && !person.former && !person.isMe && (person.role === 1 || person.role === 2),
  );
  return (
    <Modal title="Delegate" onClose={onClose}>
      <p className="text-sm text-muted-foreground">
        The task is assigned to the person you name. They can accept, ask or decline; you get a follow-up in My Day. Nothing is sent outside
        this space.
      </p>
      <form
        className="grid gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          let followUp: FollowUpDto | null = null;
          if (followUpKind === "Fixed") {
            followUp = followUpAt ? { kind: followUpKind, fixedAtUtc: new Date(followUpAt).toISOString(), amount: null, unit: null } : null;
          } else if (followUpKind === "AfterWaiting" || followUpKind === "BeforeDue" || followUpKind === "AfterInactivity") {
            const amount = Number(followUpAmount);
            followUp = {
              kind: followUpKind,
              fixedAtUtc: null,
              amount: amount > 0 ? amount : null,
              unit: followUpKind === "AfterInactivity" ? "Days" : followUpUnit,
            };
          }
          void run(() =>
            api.delegate({
              operationId: operationId(),
              taskId: task.id,
              expectedVersion: task.version,
              toMemberId: member,
              message: message.trim() || null,
              doneDefinition: doneDefinition.trim() || null,
              dueDate: dueDate || null,
              reviewRequired: review,
              followUp,
            }),
          ).then((result) => {
            if (result) onDelegated(result.task);
          });
        }}
      >
        <Field label="Delegate to">
          <NativeSelect value={member} required onChange={(event) => setMember(event.target.value)}>
            <NativeSelectOption value="" disabled>
              Choose a member
            </NativeSelectOption>
            {eligible.map((person) => (
              <NativeSelectOption key={person.actorId} value={person.actorId.slice("person:".length)}>
                {person.displayName}
              </NativeSelectOption>
            ))}
          </NativeSelect>
        </Field>
        <Field label="Message (optional)">
          <textarea
            value={message}
            onChange={(event) => setMessage(event.target.value)}
            rows={3}
            maxLength={4000}
            className="w-full rounded-md border bg-background px-2 py-1 text-sm"
            dir="auto"
          />
        </Field>
        <Field label="Done looks like (optional)">
          <textarea
            value={doneDefinition}
            onChange={(event) => setDoneDefinition(event.target.value)}
            rows={2}
            maxLength={4000}
            className="w-full rounded-md border bg-background px-2 py-1 text-sm"
            dir="auto"
          />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Due date (optional)">
            <Input type="date" value={dueDate} onChange={(event) => setDueDate(event.target.value)} />
          </Field>
          <Field label="Review">
            <label className="flex items-center gap-2 text-sm">
              <input type="checkbox" checked={review} onChange={(event) => setReview(event.target.checked)} /> I review before it counts as
              done
            </label>
          </Field>
        </div>
        <FieldGroup legend="Follow up">
          <NativeSelect value={followUpKind} onChange={(event) => setFollowUpKind(event.target.value)} aria-label="Follow up">
            <NativeSelectOption value="None">No follow-up</NativeSelectOption>
            <NativeSelectOption value="Fixed">At a time</NativeSelectOption>
            <NativeSelectOption value="AfterWaiting">After delegation</NativeSelectOption>
            <NativeSelectOption value="BeforeDue">Before the due date</NativeSelectOption>
            <NativeSelectOption value="AfterInactivity">After days without activity</NativeSelectOption>
          </NativeSelect>
        </FieldGroup>
        <Field label="Follow-up time (for “At a time”)">
          <Input type="datetime-local" value={followUpAt} onChange={(event) => setFollowUpAt(event.target.value)} />
        </Field>
        <div className="grid grid-cols-2 gap-3">
          <Field label="Amount (for the other kinds)">
            <Input type="number" min={1} max={999} value={followUpAmount} onChange={(event) => setFollowUpAmount(event.target.value)} />
          </Field>
          <Field label="Unit (before the due date)">
            <NativeSelect value={followUpUnit} onChange={(event) => setFollowUpUnit(event.target.value)} aria-label="Follow-up unit">
              <NativeSelectOption value="Minutes">Minutes</NativeSelectOption>
              <NativeSelectOption value="Hours">Hours</NativeSelectOption>
              <NativeSelectOption value="Days">Days</NativeSelectOption>
              <NativeSelectOption value="Weeks">Weeks</NativeSelectOption>
            </NativeSelect>
          </Field>
        </div>
        {error && <InlineError>{error}</InlineError>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" disabled={busy}>
            Delegate
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/** The nudge form: the template in the profile's language, editable, with the 24-hour count. */
export function NudgeDialog({
  delegationId,
  version,
  toName,
  nudges,
  onClose,
  onSent,
  api,
}: {
  delegationId: string;
  version: string;
  toName: string;
  nudges: ReadonlyArray<NudgeDto>;
  onClose: () => void;
  onSent: () => void;
  api: DelegationApi;
}) {
  const { busy, error, run } = useAction();
  const [template, setTemplate] = React.useState<string | null>(null);
  const [text, setText] = React.useState<string | null>(null);
  React.useEffect(() => {
    let active = true;
    void api.nudgeTemplate({ delegationId }).then((result) => {
      if (active) {
        const loaded = result.ok ? (result.text ?? "") : "";
        setTemplate(loaded);
        setText(loaded);
      }
    });
    return () => {
      active = false;
    };
  }, [api, delegationId]);
  const recent = nudges.filter((nudge) => Date.now() - Date.parse(nudge.sentAtUtc) < 24 * 60 * 60 * 1000).length;
  return (
    <Modal title={`Nudge ${toName}`} onClose={onClose}>
      <p className="text-sm text-muted-foreground">
        {recent >= nudgeLimit
          ? `You have nudged ${toName} ${recent} times in the last 24 hours; the limit is ${nudgeLimit}.`
          : `${nudgeLimit - recent} of ${nudgeLimit} nudges left for today. ${toName} sees it in their inbox, held by their quiet hours.`}
      </p>
      <form
        className="grid gap-3"
        onSubmit={(event) => {
          event.preventDefault();
          const sent = (text ?? "").trim();
          void run(() =>
            api.nudge({
              operationId: operationId(),
              delegationId,
              expectedVersion: version,
              text: sent && sent !== template ? sent : null,
            }),
          ).then((result) => {
            if (result) onSent();
          });
        }}
      >
        <Field label="Message">
          <textarea
            name="text"
            rows={3}
            maxLength={2000}
            value={text ?? ""}
            onChange={(event) => setText(event.target.value)}
            className="w-full rounded-md border bg-background px-2 py-1 text-sm"
            dir="auto"
          />
        </Field>
        {error && <InlineError>{error}</InlineError>}
        <div className="flex justify-end gap-2">
          <Button type="button" variant="ghost" onClick={onClose}>
            Cancel
          </Button>
          <Button type="submit" disabled={busy || recent >= nudgeLimit}>
            Send nudge
          </Button>
        </div>
      </form>
    </Modal>
  );
}

/** The task's delegations: the live one with its moves, the earlier ones, and the Delegate form (shared spaces). */
export function DelegationSection({
  task,
  canEdit,
  onTaskChanged,
  api = delegationRpc,
  directoryApi = peopleRpc,
}: {
  task: TaskResponse;
  canEdit: boolean;
  onTaskChanged: (task: TaskResponse | null) => void;
  api?: DelegationApi;
  directoryApi?: Pick<typeof peopleRpc, "list">;
}) {
  const [view, setView] = React.useState<DelegationItemDto[]>([]);
  const [people, setPeople] = React.useState<readonly PersonResponse[]>([]);
  const [loadFailed, setLoadFailed] = React.useState(false);
  const [dialog, setDialog] = React.useState<"delegate" | null>(null);
  const [nudge, setNudge] = React.useState<DelegationItemDto | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const version = task.version;

  const reload = React.useCallback(async () => {
    const [directory, current] = await Promise.all([directoryApi.list({ spaceId: task.spaceId }), api.get({ taskId: task.id })]);
    if (!directory.ok || !current.ok) throw new Error(directory.error?.message ?? current.error?.message ?? "Could not load delegations.");
    setPeople(directory.people);
    setView([...current.live, ...current.history]);
  }, [api, directoryApi, task.id, task.spaceId]);
  React.useEffect(() => {
    let active = true;
    setLoadFailed(false);
    void reload().catch((e: unknown) => {
      if (active) {
        setLoadFailed(true);
        setError(e instanceof Error ? e.message : String(e));
      }
    });
    return () => {
      active = false;
    };
  }, [reload, version, setError]);

  if (loadFailed) return <p className="text-sm text-muted-foreground">Delegations could not be loaded.</p>;
  if (view.length === 0 && !canEdit) return null;
  const live = view.filter((item) => isDelegationActive(item.delegation.state));
  const history = view.filter((item) => !isDelegationActive(item.delegation.state));
  return (
    <fieldset className="grid gap-2 rounded-lg border p-3" data-testid="delegation-section">
      <legend className="px-1 text-sm font-medium">Delegation</legend>
      {live.map((item) => (
        <DelegationCard
          key={item.delegation.id}
          item={item}
          side={sideOf(item.delegation, people)}
          people={people}
          api={api}
          onChanged={onTaskChanged}
          onOpenNudge={(target) => setNudge(target)}
        />
      ))}
      {history.length > 0 && (
        <ul className="grid gap-1 text-xs text-muted-foreground">
          {history.map((item) => (
            <li key={item.delegation.id}>
              {sideOf(item.delegation, people) === "From" ? "Delegated to" : "From"} <bdi>{item.toName}</bdi> ·{" "}
              {describeDelegation(item.delegation.state, sideOf(item.delegation, people))} ·{" "}
              {dayText(item.delegation.stateChangedAtUtc.slice(0, 10))}
            </li>
          ))}
        </ul>
      )}
      {error && <InlineError>{error}</InlineError>}
      {canEdit && (
        <div>
          <Button size="sm" variant="outline" onClick={() => setDialog("delegate")}>
            {live.length === 0 ? "Delegate…" : "Hand to someone else…"}
          </Button>
        </div>
      )}
      {dialog === "delegate" && (
        <DelegateDialog
          task={task}
          people={people}
          api={api}
          onClose={() => setDialog(null)}
          onDelegated={(updated) => {
            setDialog(null);
            onTaskChanged(updated);
          }}
        />
      )}
      {nudge && (
        <NudgeDialog
          delegationId={nudge.delegation.id}
          version={nudge.delegation.version}
          toName={nudge.toName}
          nudges={nudge.nudges}
          api={api}
          onClose={() => setNudge(null)}
          onSent={() => {
            setNudge(null);
            void reload();
          }}
        />
      )}
    </fieldset>
  );
}
