import { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  CalendarDays, Check, ChevronLeft, ChevronRight, Download, Inbox, List, LogOut, Plus, RefreshCw, Repeat2, WifiOff, X,
} from "lucide-react";
import { forgetOfflineSession, useMobileData, writeTask } from "./mobileData.js";
import { usePwa } from "./usePwa.js";
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
  if (kind === "list" && guid.test(id)) return { kind, id, path: `/lists/${id}/tasks`, href: `#list/${id}`, tab: "lists" };
  if (kind === "task" && guid.test(id)) {
    const from = new URLSearchParams(query).get("from");
    const back = from === "#lists" || (from?.startsWith("#list/") && guid.test(from.slice(6))) ? from : "#today";
    return { kind, id, path: `/tasks/${id}`, back, tab: back === "#today" ? "today" : "lists" };
  }
  if (kind === "lists") return { kind, path: "/lists", href: "#lists", tab: kind };
  return { kind: "today", path: "/today", href: "#today", tab: "today" };
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
  const [composer, setComposer] = useState(false);
  const [busy, setBusy] = useState(false);
  const [feedback, setFeedback] = useState(null);
  const inFlight = useRef(false);
  const canWrite = view.mode === "ready" && view.connected;
  const canAdd = canWrite && view.lists.some(list => !list.isArchived);

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
    const message = { complete: "Task completed.", reopen: "Task reopened.", "plan-today": "Task planned for today." }[action];
    void save(`/tasks/${task.id}/${action}`, null, message);
  }

  const hasNavigation = view.mode !== "signed-out";
  const busyLabel = busy ? "Saving…" : feedback?.text;
  return (
    <>
      <a className="skip" href="#content" onClick={event => {
        event.preventDefault();
        document.getElementById("content").focus();
      }}>Skip to content</a>
      <header className="topbar">
        <div className="topbar-inner">
          <a className="brand" href="#today"><img src="/app/icons/icon-192.png" alt="" />Chaos</a>
          <div className="header-actions">
            {pwa.offerInstall && <button className="icon-button" onClick={pwa.offerInstall} aria-label="Install Chaos" title="Install Chaos"><Download /></button>}
            {hasNavigation && <button className="icon-button" onClick={view.refresh} aria-label="Refresh tasks" title="Refresh tasks" disabled={busy}><RefreshCw /></button>}
            {view.session && <button className="icon-button" onClick={() => void save("/signout")} aria-label="Sign out" title="Sign out" disabled={!canWrite || busy}><LogOut /></button>}
          </div>
        </div>
      </header>

      <main id="content" className="content" tabIndex={-1}>
        {pwa.reloadForUpdate && <div className="notice update">
          <span>A new version of Chaos is ready.</span>
          <button className="text-button" onClick={pwa.reloadForUpdate} disabled={busy}>Reload for the new version</button>
        </div>}
        {view.mode === "offline" && <div className="notice offline" role="status">
          <WifiOff size={18} /><div><strong>Offline · Read only</strong>
            {view.updatedAt && <small>Last updated {timestamp.format(new Date(view.updatedAt))}</small>}</div>
        </div>}
        <div className={busyLabel ? `feedback ${feedback?.error ? "error" : ""}` : "sr-only"} role="status">{busyLabel}</div>
        {view.mode === "signed-out" ? (
          <section className="sign-in">
            <img src="/app/icons/icon-192.png" alt="" />
            <h1>Your tasks, on the go</h1>
            <p>Sign in with the account you use on Chaos desktop.</p>
            <a className="primary-button" href="/signin?ReturnUrl=%2Fapp">Sign in <ChevronRight size={18} /></a>
          </section>
        ) : (
          <Screen route={route} view={view} canWrite={canWrite && !busy} act={act} />
        )}
      </main>

      {hasNavigation && (
        <footer className="dock">
          <div className="dock-inner">
            <nav aria-label="Main navigation">
              <a href="#today" aria-current={route.tab === "today" ? "page" : undefined}><CalendarDays />Today</a>
              <a href="#lists" aria-current={route.tab === "lists" ? "page" : undefined}><List />Lists</a>
            </nav>
            <button className="primary-button add-button" disabled={!canAdd || busy} onClick={() => setComposer(true)}>
              <Plus size={21} />Add task
            </button>
          </div>
        </footer>
      )}

      {composer && <Capture
        lists={view.lists.filter(list => !list.isArchived)}
        listId={route.kind === "list" ? route.id : undefined}
        disabled={!canWrite || busy}
        onCancel={() => setComposer(false)}
        onSubmit={async body => {
          const saved = await save("/tasks", body, "Task added.");
          if (saved) setComposer(false);
          return saved;
        }}
      />}
    </>
  );
}

function Screen({ route, view, canWrite, act }) {
  const { data, mode, lists } = view;
  if (mode === "loading") return <p className="placeholder" role="status">Loading tasks…</p>;
  if (mode === "error") return <div className="empty">
    <h1>Could not open this view</h1><p>{view.error}</p>
    <button className="secondary-button" onClick={view.refresh}>Try again</button>
  </div>;
  if (!data) return <div className="empty">
    <h1>{route.kind === "today" ? "Today" : route.kind === "task" ? "Task" : "Lists"}</h1>
    <p>This view has not been saved on this device. Reconnect to open it.</p>
    <a href="#today" className="text-button">Back to Today</a>
  </div>;
  if (route.kind === "task") {
    return <TaskDetail task={data} list={lists.find(list => list.id === data.listId)}
      back={route.back} canWrite={canWrite} offline={mode === "offline"} act={act} />;
  }
  if (route.kind === "lists") return <ListPicker lists={data} />;

  const today = route.kind === "today";
  const tasks = data.tasks;
  const open = tasks.filter(task => !task.isTerminal);
  const closed = tasks.filter(task => task.isTerminal);
  return (
    <>
      {today ? <p className="date-label"><CalendarDays size={17} />{readDate(data.date, dateFormat)}</p>
        : <a className="back" href="#lists"><ChevronLeft size={18} />Lists</a>}
      <div className="screen-heading">
        <h1>{today ? "Today" : data.list.name}</h1>
        <span className="count">{open.length} open</span>
      </div>
      {!tasks.length && (
        <div className="empty">
          <div className="empty-icon">{today ? <CalendarDays /> : <List />}</div>
          <h2>{today ? "Nothing on the agenda" : "This list is empty"}</h2>
          <p>{!lists.length && mode !== "offline" ? "Connect Chaos on your desktop and sync your Inbox to get started."
            : today ? "Tasks planned for today or due by today will appear here." : "Tasks you add to this list will appear here."}</p>
        </div>
      )}
      {open.length > 0 && <TaskList tasks={open} route={route} canWrite={canWrite} act={act} />}
      {closed.length > 0 && <section className="closed-tasks">
        <h2>Closed <span>{closed.length}</span></h2>
        <TaskList tasks={closed} route={route} canWrite={canWrite} act={act} />
      </section>}
    </>
  );
}

function ListPicker({ lists }) {
  return (
    <>
      <div className="screen-heading"><h1>Lists</h1><span className="count">{lists.length} {lists.length === 1 ? "list" : "lists"}</span></div>
      {lists.length === 0 ? <div className="empty"><div className="empty-icon"><List /></div>
        <h2>No synced lists yet</h2><p>Connect Chaos on your desktop and sync your Inbox to get started.</p>
      </div> : <ul className="rows">
        {lists.map(list => <li key={list.id}>
          <a className="list-row" href={`#list/${list.id}`}>
            <span className="list-icon">{list.isInbox ? <Inbox /> : <List />}</span>
            <span className="row-title">{list.name}
              {list.isArchived && <small>Archived</small>}
            </span>
            <ChevronRight className="row-arrow" size={18} />
          </a>
        </li>)}
      </ul>}
    </>
  );
}

function TaskList({ tasks, route, canWrite, act }) {
  return <ul className="rows">
    {tasks.map(task => (
      <li className={`task-row ${task.isTerminal ? "terminal" : ""}`} key={task.id}>
        <button className="task-check"
          disabled={!canWrite || task.isTerminal || task.isRecurring}
          aria-label={task.isTerminal ? statuses[task.status] : task.isRecurring ? "Complete on desktop" : `Complete ${task.title}`}
          title={task.isRecurring ? "Complete on desktop" : undefined}
          onClick={() => act(task, "complete")}><span>{task.status === 4 ? <Check size={15} /> : task.status === 5 ? <X size={14} /> : null}</span>
        </button>
        <a className="task-link" href={`#task/${task.id}?from=${encodeURIComponent(route.href)}`}>
          <span className="row-title">
            {task.title}
            {(task.plannedDate || task.dueDate || task.isRecurring) && <small>
              {task.isRecurring && <span><Repeat2 size={14} />Recurring</span>}
              {task.plannedDate && <span>Planned {readDate(task.plannedDate)}</span>}
              {task.dueDate && <span className="due-date">Due {readDate(task.dueDate)}</span>}
            </small>}
          </span>
          <ChevronRight className="row-arrow" size={17} />
        </a>
      </li>
    ))}
  </ul>;
}

function TaskDetail({ task, list, back, canWrite, offline, act }) {
  return (
    <>
      <a className="back" href={back}><ChevronLeft size={18} />{back === "#today" ? "Today" : list?.name ?? "Lists"}</a>
      <div className="screen-heading task-heading"><h1>{task.title}</h1></div>
      <dl className="task-fields">
        <div><dt>Status</dt><dd>{statuses[task.status] ?? "Open"}</dd></div>
        <div><dt>List</dt><dd>{list?.name ?? "—"}</dd></div>
        <div><dt>Planned</dt><dd>{schedule(task.plannedDate, task.plannedTime, task.plannedZoneId)}</dd></div>
        <div><dt>Due</dt><dd>{schedule(task.dueDate, task.dueTime, task.dueZoneId)}</dd></div>
      </dl>
      <section className="task-notes">
        <h2>Notes</h2>
        <p>{offline ? "Notes are available online." : task.notes || "No notes."}</p>
      </section>
      <div className="task-actions">
        {task.isRecurring ? <p className="inline-hint"><Repeat2 size={17} />{task.isTerminal ? "Reopen on desktop" : "Complete on desktop"}</p>
          : <button className="primary-button" disabled={!canWrite} onClick={() => act(task, task.isTerminal ? "reopen" : "complete")}>
            {task.isTerminal ? <RefreshCw size={17} /> : <Check size={19} />}
            {task.isTerminal ? "Reopen task" : "Complete task"}
          </button>}
        {!task.isTerminal && <button className="secondary-button" disabled={!canWrite} onClick={() => act(task, "plan-today")}>
          <CalendarDays size={18} />Plan for today
        </button>}
      </div>
    </>
  );
}

function Capture({ lists, listId, disabled, onCancel, onSubmit }) {
  const dialog = useRef(null);
  const request = useRef(null);
  const [error, setError] = useState(null);
  const [title, setTitle] = useState("");
  const [list, setList] = useState(lists.find(item => item.id === listId)?.id ?? lists.find(item => item.isInbox)?.id ?? "");
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
          <button type="button" className="icon-button" aria-label="Close" onClick={onCancel}><X /></button>
        </div>
        <label htmlFor="title">Title</label>
        <input id="title" autoFocus required value={title} onChange={event => setTitle(event.target.value)} placeholder="What needs doing?" />
        <div className="form-fields">
          <div><label htmlFor="list">List</label>
            <select id="list" value={list} onChange={event => setList(event.target.value)}>
              <option value="" disabled>Choose a list</option>
              {lists.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}
            </select>
          </div>
          <div><label htmlFor="planned">Planned date</label>
            <input id="planned" type="date" min="0001-01-01" max="9999-12-31" value={planned} onChange={event => setPlanned(event.target.value)} />
          </div>
        </div>
        {error && <p className="form-error" role="alert">{error}</p>}
        <button className="primary-button" disabled={disabled || !title.trim() || !list}><Plus size={20} />Add task</button>
      </form>
    </dialog>
  );
}

createRoot(document.getElementById("root")).render(<App />);
