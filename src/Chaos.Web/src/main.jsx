import { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  BookOpen,
  CalendarDays,
  Check,
  ChevronLeft,
  ChevronRight,
  Download,
  Inbox,
  List,
  LogOut,
  Plus,
  RefreshCw,
  Repeat2,
  Search,
  StickyNote,
  WifiOff,
  X,
} from "lucide-react";
import { forgetOfflineSession, useMobileData, writeTask } from "./mobileData.js";
import { usePwa } from "./usePwa.js";
import { MarkdownNotes } from "./MarkdownNotes.jsx";
import { SearchScreen } from "./SearchScreen.jsx";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { applyTheme, defaultTheme, parseTheme } from "../../Chaos.Desktop/frontend/src/appearance";
import { dayBackgroundStyle, sceneBackgroundStyle } from "./appearance.js";
import { pageContent } from "./pageContent.js";
import "./styles.css";

const statuses = ["Not started", "In progress", "Waiting on someone", "Needs review", "Done", "Cancelled"];
const guid = /^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i;
const dateFormat = new Intl.DateTimeFormat(undefined, { weekday: "long", month: "long", day: "numeric", timeZone: "UTC" });
const shortDate = new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", timeZone: "UTC" });
const timestamp = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });
const readDate = (date, format = shortDate) => format.format(new Date(date + "T00:00:00Z"));

function schedule(date, time, zone) {
  if (!date) return "—";
  return [readDate(date), time?.slice(0, 5), time && zone].filter(Boolean).join(" · ");
}

function getRoute(hash) {
  const [path, query] = hash.slice(1).split("?");
  const [kind, value] = path.split("/");
  const id = value?.toLowerCase();
  if (kind === "search") return { kind, path: "/today", query: new URLSearchParams(query).get("q") ?? "", tab: "search" };
  if (kind === "list" && guid.test(id)) return { kind, id, path: `/lists/${id}/tasks`, href: `#list/${id}`, tab: "lists" };
  if (kind === "task" && guid.test(id)) {
    const from = new URLSearchParams(query).get("from");
    const back =
      from === "#lists" || from?.startsWith("#search?") || from === "#search" || (from?.startsWith("#list/") && guid.test(from.slice(6)))
        ? from
        : "#my-day";
    return { kind, id, path: `/tasks/${id}`, back, tab: back === "#my-day" ? "today" : "lists" };
  }
  if (kind === "pad" || kind === "pages") {
    const from = new URLSearchParams(query).get("from");
    return {
      kind,
      id: guid.test(id) ? id : null,
      path: `/${kind}`,
      tab: kind,
      back: from === "#search" || from?.startsWith("#search?") ? from : null,
    };
  }
  if (kind === "lists") return { kind, path: "/lists", href: "#lists", tab: kind };
  return { kind: "today", path: "/today", href: "#my-day", tab: "today" };
}

function useRoute() {
  const [hash, setHash] = useState(() => location.hash);
  useEffect(() => {
    const changed = () => {
      setHash(location.hash);
      window.scrollTo(0, 0);
    };
    addEventListener("hashchange", changed);
    return () => removeEventListener("hashchange", changed);
  }, []);
  return getRoute(hash);
}

function App() {
  const route = useRoute();
  const view = useMobileData(route.path);
  const pwa = usePwa();
  useEffect(() => {
    try {
      applyTheme(view.appearance?.theme ? parseTheme(JSON.parse(view.appearance.theme)) : defaultTheme);
    } catch {
      applyTheme(defaultTheme);
    }
  }, [view.appearance?.theme]);
  const [composer, setComposer] = useState(false);
  const [busy, setBusy] = useState(false);
  const [feedback, setFeedback] = useState(null);
  const inFlight = useRef(false);
  const canWrite = view.mode === "ready" && view.connected;
  const canAdd = canWrite && view.lists.some((list) => !list.isArchived);

  async function save(path, body, message) {
    if (!canWrite || inFlight.current) return false;
    inFlight.current = true;
    setBusy(true);
    setFeedback(null);
    try {
      await writeTask(view.session, path, body);
      if (path === "/signout") {
        await forgetOfflineSession();
        location.replace("/app/");
      } else {
        setFeedback({ text: message });
        view.refresh();
      }
      return true;
    } catch (error) {
      setFeedback({ text: error.message, error: true });
      view.refresh();
      return false;
    } finally {
      inFlight.current = false;
      setBusy(false);
    }
  }

  function act(task, action) {
    const message = {
      complete: "Task completed.",
      reopen: "Task reopened.",
      "add-to-my-day": "Task added to My Day.",
      "remove-from-my-day": "Task removed from My Day.",
    }[action];
    void save(`/tasks/${task.id}/${action}`, null, message);
  }

  const hasNavigation = view.mode !== "signed-out";
  const busyLabel = busy ? "Saving…" : feedback?.text;
  const page = route.kind === "pages" && route.id && Array.isArray(view.data) ? view.data.find((item) => item.id === route.id) : null;
  const pageBackground = page ? sceneBackgroundStyle(pageContent(page.markdown).background) : undefined;
  const background = route.kind === "today" ? dayBackgroundStyle(view.appearance?.background) : pageBackground;
  return (
    <>
      <a
        className="skip"
        href="#content"
        onClick={(event) => {
          event.preventDefault();
          document.getElementById("content").focus();
        }}
      >
        Skip to content
      </a>
      <header className="topbar">
        <div className="topbar-inner">
          <a className="brand" href="#my-day">
            <img src="/app/icons/icon-192.png" alt="" />
            Chaos
          </a>
          <div className="header-actions">
            {pwa.offerInstall && (
              <Button
                variant="ghost"
                size="icon"
                className="icon-button"
                onClick={pwa.offerInstall}
                aria-label="Install Chaos"
                title="Install Chaos"
              >
                <Download />
              </Button>
            )}
            {hasNavigation && (
              <Button asChild variant="ghost" size="icon" className="icon-button">
                <a href="#search" aria-label="Search" title="Search">
                  <Search />
                </a>
              </Button>
            )}
            {hasNavigation && (
              <Button
                variant="ghost"
                size="icon"
                className="icon-button"
                onClick={view.refresh}
                aria-label="Refresh"
                title="Refresh"
                disabled={busy}
              >
                <RefreshCw />
              </Button>
            )}
            {view.session && (
              <Button
                variant="ghost"
                size="icon"
                className="icon-button"
                onClick={() => void save("/signout")}
                aria-label="Sign out"
                title="Sign out"
                disabled={!canWrite || busy}
              >
                <LogOut />
              </Button>
            )}
          </div>
        </div>
      </header>

      <main
        id="content"
        className={`content ${background ? (page ? "page-content" : "day-content") : ""}`}
        style={background}
        tabIndex={-1}
      >
        {pwa.reloadForUpdate && (
          <div className="notice update">
            <span>A new version of Chaos is ready.</span>
            <Button variant="link" className="text-button" onClick={pwa.reloadForUpdate} disabled={busy}>
              Reload for the new version
            </Button>
          </div>
        )}
        {view.mode === "offline" && (
          <div className="notice offline" role="status">
            <WifiOff size={18} />
            <div>
              <strong>Offline · Read only</strong>
              {view.updatedAt && <small>Last updated {timestamp.format(new Date(view.updatedAt))}</small>}
            </div>
          </div>
        )}
        <div className={busyLabel ? `feedback ${feedback?.error ? "error" : ""}` : "sr-only"} role="status">
          {busyLabel}
        </div>
        {view.mode === "signed-out" ? (
          <section className="sign-in">
            <img src="/app/icons/icon-192.png" alt="" />
            <h1>Your tasks, on the go</h1>
            <p>Sign in with the account you use on Chaos desktop.</p>
            <Button asChild className="mobile-action">
              <a href="/signin?ReturnUrl=%2Fapp">
                Sign in <ChevronRight size={18} />
              </a>
            </Button>
          </section>
        ) : (
          <Screen route={route} view={view} canWrite={canWrite && !busy} act={act} />
        )}
      </main>

      {hasNavigation && (
        <footer className="dock">
          <div className="dock-inner">
            <nav aria-label="Main navigation">
              <a href="#my-day" aria-current={route.tab === "today" ? "page" : undefined}>
                <CalendarDays />
                My Day
              </a>
              <a href="#lists" aria-current={route.tab === "lists" ? "page" : undefined}>
                <List />
                Lists
              </a>
              <a href="#pad" aria-current={route.tab === "pad" ? "page" : undefined}>
                <StickyNote />
                Pad
              </a>
              <a href="#pages" aria-current={route.tab === "pages" ? "page" : undefined}>
                <BookOpen />
                Pages
              </a>
            </nav>
            <Button className="mobile-action" disabled={!canAdd || busy} onClick={() => setComposer(true)}>
              <Plus size={21} />
              Add task
            </Button>
          </div>
        </footer>
      )}

      {composer && (
        <Capture
          lists={view.lists.filter((list) => !list.isArchived)}
          listId={route.kind === "list" ? route.id : undefined}
          disabled={!canWrite || busy}
          onCancel={() => setComposer(false)}
          onSubmit={async (body) => {
            const saved = await save("/tasks", body, "Task added.");
            if (saved) setComposer(false);
            return saved;
          }}
        />
      )}
    </>
  );
}

function Screen({ route, view, canWrite, act }) {
  const { data, mode, lists } = view;
  if (route.kind === "search" && mode !== "loading" && mode !== "error")
    return <SearchScreen initialQuery={route.query} session={view.session} offline={mode === "offline" || !view.connected} />;
  if (mode === "loading")
    return (
      <p className="placeholder" role="status">
        Loading…
      </p>
    );
  if (mode === "error")
    return (
      <div className="empty">
        <h1>Could not open this view</h1>
        <p>{view.error}</p>
        <Button variant="outline" className="mobile-action" onClick={view.refresh}>
          Try again
        </Button>
      </div>
    );
  if (!data)
    return (
      <div className="empty">
        <h1>
          {route.kind === "today"
            ? "My Day"
            : route.kind === "task"
              ? "Task"
              : route.kind === "pad"
                ? "Personal pad"
                : route.kind === "pages"
                  ? "Pages"
                  : "Lists"}
        </h1>
        <p>This view has not been saved on this device. Reconnect to open it.</p>
        <Button asChild variant="link" className="text-button">
          <a href="#my-day">Back to My Day</a>
        </Button>
      </div>
    );
  if (route.kind === "task") {
    return (
      <TaskDetail
        task={data}
        list={lists.find((list) => list.id === data.listId)}
        back={route.back}
        canWrite={canWrite}
        offline={mode === "offline"}
        act={act}
      />
    );
  }
  if (route.kind === "lists") return <ListPicker lists={data} />;
  if (route.kind === "pad" || route.kind === "pages") return <ReadingView route={route} items={data} />;

  const today = route.kind === "today";
  const tasks = data.tasks;
  const open = tasks.filter((task) => !task.isTerminal);
  const closed = tasks.filter((task) => task.isTerminal);
  return (
    <>
      {today ? (
        <p className="date-label">
          <CalendarDays size={17} />
          {readDate(data.date, dateFormat)}
        </p>
      ) : (
        <a className="back" href="#lists">
          <ChevronLeft size={18} />
          Lists
        </a>
      )}
      <div className="screen-heading">
        <h1>{today ? "My Day" : data.list.name}</h1>
        <Badge variant="secondary" className="count">
          {open.length} open
        </Badge>
      </div>
      {today && data.meetings?.length > 0 && (
        <section className="meetings" aria-label="Meetings today">
          <h2>Meetings today</h2>
          <ol>
            {data.meetings.map((meeting) => {
              const url = [meeting.videoUrl, meeting.webUrl].find((value) => value?.startsWith("https://"));
              const time = meeting.allDay ? "All day" : `${meeting.start}–${meeting.end}`;
              return (
                <li key={meeting.id} style={{ borderLeftColor: meeting.color || undefined, opacity: meeting.busy ? 1 : 0.7 }}>
                  <span className="meeting-time">{time}</span>
                  <span className="meeting-title">{meeting.title}</span>
                  {url && (
                    <a href={url} target="_blank" rel="noopener noreferrer">
                      {meeting.videoUrl === url ? "Join" : "Open"}
                    </a>
                  )}
                </li>
              );
            })}
          </ol>
        </section>
      )}
      {!tasks.length && (
        <div className="empty">
          <div className="empty-icon">{today ? <CalendarDays /> : <List />}</div>
          <h2>{today ? "My Day is empty" : "This list is empty"}</h2>
          <p>
            {!lists.length && mode !== "offline"
              ? "Connect Chaos on your desktop and sync your Inbox to get started."
              : today
                ? "Open a task from Lists to add it to My Day."
                : "Tasks you add to this list will appear here."}
          </p>
        </div>
      )}
      {open.length > 0 && <TaskList tasks={open} route={route} canWrite={canWrite} act={act} />}
      {closed.length > 0 && (
        <section className="closed-tasks">
          <h2>
            {today ? "Finished" : "Closed"} <span>{closed.length}</span>
          </h2>
          <TaskList tasks={closed} route={route} canWrite={canWrite} act={act} />
        </section>
      )}
    </>
  );
}

function ReadingView({ route, items }) {
  const isPad = route.kind === "pad";
  const label = isPad ? "Personal pad" : "Pages";
  const selected = items.find((item) => item.id === route.id);
  const markdown = selected ? (isPad ? selected.markdown : pageContent(selected.markdown).body) : "";
  const title = (item) =>
    isPad
      ? item.kind === 1
        ? "Drawing"
        : item.markdown
            .split("\n")
            .find((line) => line.trim())
            ?.replace(/^\s*#+\s*/, "")
            .slice(0, 100) || "Empty note"
      : item.title || "Untitled";
  if (route.id)
    return (
      <>
        <a className="back" href={route.back ?? `#${route.kind}`}>
          <ChevronLeft size={18} />
          {route.back ? "Search" : label}
        </a>
        {selected ? (
          <>
            <div className="screen-heading task-heading">
              <h1>{title(selected)}</h1>
            </div>
            {isPad && selected.isArchived && <p className="reading-meta">Archived</p>}
            {isPad && selected.kind === 1 ? (
              <p className="reading-meta">Open this drawing on desktop to view it.</p>
            ) : markdown.trim() ? (
              <MarkdownNotes>{markdown}</MarkdownNotes>
            ) : (
              <p className="reading-meta">No content.</p>
            )}
          </>
        ) : (
          <div className="empty">
            <h1>Item unavailable</h1>
            <p>It may have been deleted or is no longer synced.</p>
          </div>
        )}
      </>
    );
  const byId = new Map(items.map((item) => [item.id, item]));
  const location = (item) => {
    if (item.dailyDate) return `Daily page - ${readDate(item.dailyDate)}`;
    const parents = [],
      seen = new Set([item.id]);
    let parent = byId.get(item.parentId);
    while (parent && !seen.has(parent.id)) {
      seen.add(parent.id);
      parents.unshift(parent.title || "Untitled");
      parent = byId.get(parent.parentId);
    }
    return parents.join(" / ");
  };
  const rows = (entries) => (
    <ul className="rows">
      {entries.map((item) => (
        <li key={item.id}>
          <a className="list-row" href={`#${route.kind}/${item.id}`}>
            <span className="list-icon">{isPad ? <StickyNote /> : item.dailyDate ? <CalendarDays /> : <BookOpen />}</span>
            <span className="row-title">
              {title(item)}
              {(isPad ? item.isArchived : location(item)) && <small>{isPad ? "Archived" : location(item)}</small>}
            </span>
            <ChevronRight className="row-arrow" size={18} />
          </a>
        </li>
      ))}
    </ul>
  );
  const pages = isPad ? [] : items.filter((item) => !item.dailyDate);
  const dailyPages = isPad
    ? []
    : items.filter((item) => item.dailyDate).sort((a, b) => b.dailyDate.localeCompare(a.dailyDate) || a.id.localeCompare(b.id));
  return (
    <>
      <div className="screen-heading">
        <h1>{label}</h1>
        <Badge variant="secondary" className="count">
          {items.length}
        </Badge>
      </div>
      {!items.length ? (
        <div className="empty">
          <div className="empty-icon">{isPad ? <StickyNote /> : <BookOpen />}</div>
          <h2>No synced {isPad ? "notes" : "pages"} yet</h2>
          <p>Notes and pages synced from your desktop will appear here.</p>
        </div>
      ) : isPad ? (
        rows(items)
      ) : (
        <>
          {pages.length > 0 && (
            <section className="page-section" aria-labelledby="pages-heading">
              <h2 id="pages-heading">
                Pages <span>{pages.length}</span>
              </h2>
              {rows(pages)}
            </section>
          )}
          {dailyPages.length > 0 && (
            <section className="page-section" aria-labelledby="daily-pages-heading">
              <h2 id="daily-pages-heading">
                Daily pages <span>{dailyPages.length}</span>
              </h2>
              {rows(dailyPages)}
            </section>
          )}
        </>
      )}
    </>
  );
}

function ListPicker({ lists }) {
  return (
    <>
      <div className="screen-heading">
        <h1>Lists</h1>
        <Badge variant="secondary" className="count">
          {lists.length} {lists.length === 1 ? "list" : "lists"}
        </Badge>
      </div>
      {lists.length === 0 ? (
        <div className="empty">
          <div className="empty-icon">
            <List />
          </div>
          <h2>No synced lists yet</h2>
          <p>Connect Chaos on your desktop and sync your Inbox to get started.</p>
        </div>
      ) : (
        <ul className="rows">
          {lists.map((list) => (
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
      )}
    </>
  );
}

function TaskList({ tasks, route, canWrite, act }) {
  return (
    <ul className="rows">
      {tasks.map((task) => (
        <li className={`task-row ${task.isTerminal ? "terminal" : ""}`} key={task.id}>
          <Button
            variant="ghost"
            size="icon"
            className="task-check"
            disabled={!canWrite || task.isTerminal || task.isRecurring}
            aria-label={task.isTerminal ? statuses[task.status] : task.isRecurring ? "Complete on desktop" : `Complete ${task.title}`}
            title={task.isRecurring ? "Complete on desktop" : undefined}
            onClick={() => act(task, "complete")}
          >
            <span>{task.status === 4 ? <Check size={15} /> : task.status === 5 ? <X size={14} /> : null}</span>
          </Button>
          <a className="task-link" href={`#task/${task.id}?from=${encodeURIComponent(route.href)}`}>
            <span className="row-title">
              {task.title}
              {(task.plannedDate || task.dueDate || task.isRecurring) && (
                <small>
                  {task.isRecurring && (
                    <span>
                      <Repeat2 size={14} />
                      Recurring
                    </span>
                  )}
                  {task.plannedDate && <span>Planned {readDate(task.plannedDate)}</span>}
                  {task.dueDate && <span className="due-date">Due {readDate(task.dueDate)}</span>}
                </small>
              )}
            </span>
            <ChevronRight className="row-arrow" size={17} />
          </a>
        </li>
      ))}
    </ul>
  );
}

function TaskDetail({ task, list, back, canWrite, offline, act }) {
  return (
    <>
      <a className="back" href={back}>
        <ChevronLeft size={18} />
        {back === "#my-day" ? "My Day" : back.startsWith("#search") ? "Search" : (list?.name ?? "Lists")}
      </a>
      <div className="screen-heading task-heading">
        <h1>{task.title}</h1>
      </div>
      <dl className="task-fields">
        <div>
          <dt>Status</dt>
          <dd>{statuses[task.status] ?? "Open"}</dd>
        </div>
        <div>
          <dt>List</dt>
          <dd>{list?.name ?? "—"}</dd>
        </div>
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
      <div className="task-actions">
        {task.isRecurring ? (
          <p className="inline-hint">
            <Repeat2 size={17} />
            {task.isTerminal ? "Reopen on desktop" : "Complete on desktop"}
          </p>
        ) : (
          <Button className="mobile-action" disabled={!canWrite} onClick={() => act(task, task.isTerminal ? "reopen" : "complete")}>
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

function Capture({ lists, listId, disabled, onCancel, onSubmit }) {
  const dialog = useRef(null);
  const request = useRef(null);
  const [error, setError] = useState(null);
  const [title, setTitle] = useState("");
  const [list, setList] = useState(lists.find((item) => item.id === listId)?.id ?? lists.find((item) => item.isInbox)?.id ?? "");
  const [planned, setPlanned] = useState("");
  useEffect(() => {
    dialog.current.showModal();
  }, []);

  async function submit(event) {
    event.preventDefault();
    const cleaned = title.trim();
    if (!cleaned || Array.from(cleaned).length > 500) {
      setError("Enter a task title of up to 500 characters.");
      return;
    }
    setError(null);
    const input = { title: cleaned, listId: list || null, plannedDate: planned || null };
    const signature = JSON.stringify(input);
    if (request.current?.signature !== signature) request.current = { signature, id: crypto.randomUUID() };
    const saved = await onSubmit({ ...input, requestId: request.current.id });
    if (!saved) setError("Could not confirm the save. Try again when connected.");
  }

  return (
    <dialog ref={dialog} className="composer" aria-labelledby="capture-heading" onCancel={onCancel}>
      <form onSubmit={submit}>
        <div className="composer-heading">
          <h2 id="capture-heading">Add task</h2>
          <Button type="button" variant="ghost" size="icon" className="icon-button" aria-label="Close" onClick={onCancel}>
            <X />
          </Button>
        </div>
        <Label htmlFor="title">Title</Label>
        <Input
          id="title"
          autoFocus
          required
          value={title}
          onChange={(event) => setTitle(event.target.value)}
          placeholder="What needs doing?"
        />
        <div className="form-fields">
          <div>
            <Label htmlFor="list">List</Label>
            <NativeSelect id="list" value={list} onChange={(event) => setList(event.target.value)}>
              <NativeSelectOption value="" disabled>
                Choose a list
              </NativeSelectOption>
              {lists.map((item) => (
                <NativeSelectOption key={item.id} value={item.id}>
                  {item.name}
                </NativeSelectOption>
              ))}
            </NativeSelect>
          </div>
          <div>
            <Label htmlFor="planned">Planned date</Label>
            <Input
              id="planned"
              type="date"
              min="0001-01-01"
              max="9999-12-31"
              value={planned}
              onChange={(event) => setPlanned(event.target.value)}
            />
          </div>
        </div>
        {error && (
          <p className="form-error" role="alert">
            {error}
          </p>
        )}
        <Button className="mobile-action" disabled={disabled || !title.trim() || !list}>
          <Plus size={20} />
          Add task
        </Button>
      </form>
    </dialog>
  );
}

applyTheme(defaultTheme);
createRoot(document.getElementById("root")).render(<App />);
