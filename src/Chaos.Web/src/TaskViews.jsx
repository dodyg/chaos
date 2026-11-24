// React is imported for the component tests, which run under tsx's classic JSX transform.
import React from "react";
import { CalendarDays, Check, ChevronLeft, ChevronRight, Inbox, List, RefreshCw, Repeat2, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { MarkdownNotes } from "./MarkdownNotes.jsx";

// The server's roles in a shared space (WebSpace.role): owner, editor, viewer. Personal has none.
export const OWNER = 1;
export const EDITOR = 2;
export const VIEWER = 3;

const sentences = {
  [OWNER]: "You are an owner of this space.",
  [EDITOR]: "You are an editor of this space.",
  [VIEWER]: "You are a viewer of this space. You can read everything here; ask an owner for editor access to change tasks.",
};

/** The caller's role in a shared space, in plain words (shown once per screen). */
export const roleSentence = (role) => sentences[role] ?? "You are a member of this space.";

/** The space a list or task belongs to, by the server's space id. */
export const spaceOf = (spaces, spaceId) => spaces.find((space) => space.id === spaceId) ?? null;

/** Whether the person may change tasks here: Personal, or a space where they are an owner or an editor. */
export const canChangeIn = (spaces, spaceId) => spaceOf(spaces, spaceId)?.canChange === true;

/** Assignee names as one line; the signed-in person is "You". */
export const namesOf = (people) => (people ?? []).map((person) => (person.isMe ? "You" : person.name)).join(", ");

const statuses = ["Not started", "In progress", "Waiting on someone", "Needs review", "Done", "Cancelled"];
const readDate = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", timeZone: "UTC" });
const timestamp = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

function schedule(date, time, zone) {
  if (!date) return "—";
  return [readDate.format(new Date(date + "T00:00:00Z")), time?.slice(0, 5), time && zone].filter(Boolean).join(" · ");
}

/** The list picker: Personal first, then each shared space with the caller's role there. */
export function ListPicker({ spaces }) {
  const count = spaces.reduce((total, space) => total + space.lists.length, 0);
  return (
    <>
      <div className="screen-heading">
        <h1>Lists</h1>
        <Badge variant="secondary" className="count">
          {count} {count === 1 ? "list" : "lists"}
        </Badge>
      </div>
      {count === 0 ? (
        <div className="empty">
          <div className="empty-icon">
            <List />
          </div>
          <h2>No synced lists yet</h2>
          <p>Connect Chaos on your desktop and sync your Inbox to get started.</p>
        </div>
      ) : (
        spaces
          .filter((space) => space.lists.length > 0)
          .map((space) => (
            <section className="page-section" key={space.id} aria-labelledby={`lists-${space.id}`}>
              <h2 id={`lists-${space.id}`}>
                {space.name} <span>{space.lists.length}</span>
              </h2>
              {space.isShared && <p className="reading-meta space-role">Shared space · {roleSentence(space.role)}</p>}
              <ul className="rows">
                {space.lists.map((list) => (
                  <li key={list.id}>
                    <a className="list-row" href={`#list/${list.id}`}>
                      <span className="list-icon">{list.isInbox ? <Inbox /> : <List />}</span>
                      <span className="row-title">
                        {list.name}
                        {list.isArchived && <small>Archived</small>}
                      </span>
                      <ChevronRight className="row-arrow" size={18} />
                    </a>
                  </li>
                ))}
              </ul>
            </section>
          ))
      )}
    </>
  );
}

/** Tasks of a list or of My Day. The check box is disabled where the person may not change the task. */
export function TaskList({ tasks, route, canWrite, act }) {
  return (
    <ul className="rows">
      {tasks.map((task) => {
        const readOnly = task.canChange === false;
        return (
          <li className={`task-row ${task.isTerminal ? "terminal" : ""}`} key={task.id}>
            <Button
              variant="ghost"
              size="icon"
              className="task-check"
              disabled={!canWrite || readOnly || task.isTerminal || task.isRecurring}
              aria-label={
                task.isTerminal
                  ? statuses[task.status]
                  : task.isRecurring
                    ? "Complete on desktop"
                    : readOnly
                      ? "Read only"
                      : `Complete ${task.title}`
              }
              title={task.isRecurring ? "Complete on desktop" : readOnly ? "Viewers cannot change tasks" : undefined}
              onClick={() => act(task, "complete")}
            >
              <span>{task.status === 4 ? <Check size={15} /> : task.status === 5 ? <X size={14} /> : null}</span>
            </Button>
            <a className="task-link" href={`#task/${task.id}?from=${encodeURIComponent(route.href)}`}>
              <span className="row-title">
                {task.title}
                {(task.plannedDate || task.dueDate || task.isRecurring || task.assignees?.length > 0) && (
                  <small>
                    {task.assignees?.length > 0 && <span>{namesOf(task.assignees)}</span>}
                    {task.isRecurring && (
                      <span>
                        <Repeat2 size={14} />
                        Recurring
                      </span>
                    )}
                    {task.plannedDate && <span>Planned {readDate.format(new Date(task.plannedDate + "T00:00:00Z"))}</span>}
                    {task.dueDate && <span className="due-date">Due {readDate.format(new Date(task.dueDate + "T00:00:00Z"))}</span>}
                  </small>
                )}
              </span>
              <ChevronRight className="row-arrow" size={17} />
            </a>
          </li>
        );
      })}
    </ul>
  );
}

/** One task: its fields, notes, updates with their authors, and the actions the person's role allows. */
export function TaskDetail({ task, list, space, back, canWrite, offline, act }) {
  const shared = Boolean(space?.isShared);
  const viewer = shared && space.role === VIEWER;
  const readOnly = task.canChange === false || (shared && space.canChange === false);
  const updates = task.updates ?? [];
  const backLabel = back === "#my-day" ? "My Day" : back.startsWith("#search") ? "Search" : (list?.name ?? "Lists");
  return (
    <>
      <a className="back" href={back}>
        <ChevronLeft size={18} />
        {backLabel}
      </a>
      <div className="screen-heading task-heading">
        <h1>{task.title}</h1>
      </div>
      {viewer && (
        <p className="notice" role="status">
          {roleSentence(VIEWER)}
        </p>
      )}
      <dl className="task-fields">
        <div>
          <dt>Status</dt>
          <dd>{statuses[task.status] ?? "Open"}</dd>
        </div>
        <div>
          <dt>List</dt>
          <dd>{list?.name ?? "—"}</dd>
        </div>
        {shared && (
          <>
            <div>
              <dt>Space</dt>
              <dd>{space.name}</dd>
            </div>
            <div>
              <dt>Assignees</dt>
              <dd>{task.assignees?.length ? namesOf(task.assignees) : "No one yet"}</dd>
            </div>
          </>
        )}
        <div>
          <dt>Planned</dt>
          <dd>{schedule(task.plannedDate, task.plannedTime, task.plannedZoneId)}</dd>
        </div>
        <div>
          <dt>Due</dt>
          <dd>{schedule(task.dueDate, task.dueTime, task.dueZoneId)}</dd>
        </div>
      </dl>
      <section className="task-notes">
        <h2>Notes</h2>
        {offline ? <p>Notes are available online.</p> : task.notes?.trim() ? <MarkdownNotes>{task.notes}</MarkdownNotes> : <p>No notes.</p>}
      </section>
      <section className="task-updates" aria-labelledby="updates-heading">
        <h2 id="updates-heading">
          Updates <span>{updates.length}</span>
        </h2>
        {updates.length === 0 ? (
          <p className="reading-meta">{offline ? "Updates are available online." : "No updates yet."}</p>
        ) : (
          <ol>
            {updates.map((update) => (
              <li key={update.id} className={update.removedByOwner ? "removed" : undefined}>
                <header>
                  <strong>{update.author}</strong>
                  <time dateTime={update.at}>{timestamp.format(new Date(update.at))}</time>
                </header>
                {update.removedByOwner ? (
                  <p className="reading-meta">Removed by an owner</p>
                ) : (
                  <MarkdownNotes>{update.markdown}</MarkdownNotes>
                )}
              </li>
            ))}
          </ol>
        )}
      </section>
      <div className="task-actions">
        {task.isRecurring ? (
          <p className="inline-hint">
            <Repeat2 size={17} />
            {task.isTerminal ? "Reopen on desktop" : "Complete on desktop"}
          </p>
        ) : (
          <Button
            className="mobile-action"
            disabled={!canWrite || readOnly}
            title={readOnly ? "Viewers cannot change tasks" : undefined}
            onClick={() => act(task, task.isTerminal ? "reopen" : "complete")}
          >
            {task.isTerminal ? <RefreshCw size={17} /> : <Check size={19} />}
            {task.isTerminal ? "Reopen task" : "Complete task"}
          </Button>
        )}
        {!task.isTerminal && (
          <Button
            variant="outline"
            className="mobile-action"
            disabled={!canWrite}
            onClick={() => act(task, task.isOnMyDay ? "remove-from-my-day" : "add-to-my-day")}
          >
            <CalendarDays size={18} />
            {task.isOnMyDay ? "Remove from My Day" : "Add to My Day"}
          </Button>
        )}
      </div>
    </>
  );
}
