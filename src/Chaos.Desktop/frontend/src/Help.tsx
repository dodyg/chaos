import React from "react";
import { ArrowRight, BookOpen, Database, Keyboard, ListTodo, Rocket, Search, Sparkles, StickyNote, Sun, Bell } from "lucide-react";
import { Button } from "@/components/ui/button";
import { PageHeader } from "@/components/common";
import { listShortcuts } from "./listKeyboard";

export type HelpLink = "myday" | "myweek" | "tasks" | "pad" | "summaries" | "notifications" | "templates" | "data" | "settings";

/** Shortcuts that work anywhere in the main window (App's keydown handler). */
export const appShortcuts: readonly [string, string][] = [
  ["Ctrl+N", "New task (quick add)"],
  ["Ctrl+Shift+P", "New Personal pad note"],
  ["Ctrl+K", "Command palette: jump anywhere or run a command"],
  ["Ctrl+Z", "Undo the last change (outside text fields)"],
  ["Ctrl+1 / 2 / 3", "Show all tasks, focus on Work, focus on Personal"],
  ["F1", "Open this help"],
];

const editorShortcuts: readonly [string, string][] = [
  ["Ctrl+B / Ctrl+I", "Bold and italic in notes"],
  ["Ctrl+K", "Insert a link while typing in a note"],
  ["[[", "Link to another task or pad note"],
];

/** Quick-add examples; each part shown here is recognised by the English language pack. */
const quickAddExamples: readonly [string, string][] = [
  ["Call plumber tomorrow 3pm", "Planned for tomorrow at 15:00"],
  ["Submit report by Friday #work !high", "Due Friday, tagged work, high priority"],
  ["Water plants every Monday", "Repeats weekly on Monday"],
  ["Dentist next Tue 10:30 remind me 1h before", "Planned next Tuesday at 10:30 with a reminder at 9:30"],
  ["Review PR for 30 min /Sprint ~work", "Estimated 30 minutes, in the Sprint list, Work category"],
  ["Contract draft waiting on Sam follow up Thursday", "Waiting on Sam, follow-up reminder on Thursday"],
];

const quickAddParts: readonly [string, string][] = [
  ["Dates", "today, tomorrow, next Friday, in 3 days, 15 Dec"],
  ["Due vs planned", "“by”, “due” or “before” sets the due date; any other date is the planned date"],
  ["Times", "3pm, 15:30, noon"],
  ["Repeats", "every Monday, every other week, weekdays, monthly, every 2 weeks after done"],
  ["Reminders", "remind me 1h before"],
  ["Estimates", "for 30 min, takes 2h"],
  ["#tag", "Adds a tag; #home/garden makes a nested tag"],
  ["!priority", "!low, !normal, !high, !urgent"],
  ["/list", "Puts the task in a list or project; use /\"Two words\" for names with spaces"],
  ["~work / ~personal", "Sets the Work or Personal category"],
  ["waiting on …", "Marks the task as waiting on someone; “follow up …” adds a follow-up date"],
];

type Topic = { id: string; title: string; icon: React.ReactNode; keywords: string; body: React.ReactNode };

function Keys({ rows }: { rows: readonly [string, string][] }) {
  return <dl className="grid grid-cols-[minmax(7rem,auto)_1fr] gap-x-4 gap-y-1.5 text-sm">
    {rows.map(([keys, action]) => <React.Fragment key={keys + action}>
      <dt><kbd className="rounded border bg-muted px-1.5 py-0.5 font-sans text-xs font-semibold">{keys}</kbd></dt>
      <dd className="text-muted-foreground">{action}</dd>
    </React.Fragment>)}
  </dl>;
}

function Go({ to, onNavigate, children }: { to: HelpLink; onNavigate: (view: HelpLink) => void; children: React.ReactNode }) {
  return <Button type="button" size="sm" variant="outline" className="justify-self-start" onClick={() => onNavigate(to)}>{children}<ArrowRight/></Button>;
}

function topics(onNavigate: (view: HelpLink) => void): Topic[] {
  return [
    { id: "start", title: "Getting started", icon: <Rocket/>, keywords: "begin first start routine daily workflow overview", body: <>
      <p>Chaos keeps everything on this computer: no account and no network needed. A simple routine that works well:</p>
      <ol className="grid list-decimal gap-1 pl-5">
        <li><strong>Capture</strong> anything the moment it comes up with <kbd>Ctrl+N</kbd>. It lands in the Inbox unless you name a list.</li>
        <li><strong>Plan the week</strong> once, in My Week: carry over what slipped and spread tasks across the days.</li>
        <li><strong>Choose today's work</strong> each morning in My Day. Suggestions show what is planned, due or left over from yesterday.</li>
        <li><strong>Review</strong> in Summaries: each evening shows what you finished and what slipped.</li>
      </ol>
      <Go to="myday" onNavigate={onNavigate}>Open My Day</Go>
    </> },
    { id: "quick-add", title: "Quick add in plain language", icon: <Sparkles/>, keywords: "capture new task syntax date time tag priority repeat reminder estimate language parse", body: <>
      <p>Type a task the way you would say it. Recognised parts appear as chips with a preview of the task; click a chip to turn it back into plain text (for example when “May” is a person, not a month). Paste several lines to create one task per line.</p>
      <div className="overflow-x-auto rounded-lg border"><table className="w-full text-sm">
        <thead className="bg-muted/60 text-left text-xs text-muted-foreground"><tr><th className="px-3 py-1.5 font-medium">You type</th><th className="px-3 py-1.5 font-medium">You get</th></tr></thead>
        <tbody>{quickAddExamples.map(([typed, result]) => <tr key={typed} className="border-t"><td className="px-3 py-1.5 font-mono text-xs">{typed}</td><td className="px-3 py-1.5 text-muted-foreground">{result}</td></tr>)}</tbody>
      </table></div>
      <Keys rows={quickAddParts}/>
      <p>Quick add also understands French, Italian, Spanish, Indonesian and Arabic, such as demain, domani, mañana, besok and غدًا, whatever language you start in.</p>
    </> },
    { id: "planning", title: "My Day and My Week", icon: <Sun/>, keywords: "plan today week capacity hours suggestions myday myweek", body: <>
      <p><strong>My Week</strong> turns everything on your plate into a plan: look back at last week, carry over or drop unfinished work, set a few goals and drag tasks onto days. Each day shows its estimated work against your available hours.</p>
      <p><strong>My Day</strong> starts each morning with what your week plan put on today. Add more from the suggestions, or press <kbd>M</kbd> on any task in a list to add it. Adding a task to My Day never changes its planned or due date.</p>
      <p>Use the Work / Personal switch (<kbd>Ctrl+2</kbd>, <kbd>Ctrl+3</kbd>) to see one side of your life at a time; <kbd>Ctrl+1</kbd> shows both.</p>
      <div className="flex flex-wrap gap-2"><Go to="myweek" onNavigate={onNavigate}>Open My Week</Go><Go to="myday" onNavigate={onNavigate}>Open My Day</Go></div>
    </> },
    { id: "organise", title: "Lists, projects, tags and views", icon: <ListTodo/>, keywords: "list project section tag view today upcoming overdue waiting subtasks templates drag reorder", body: <>
      <p><strong>Lists</strong> hold open-ended tasks; <strong>projects</strong> have an end and show progress. Create them with the + next to “Lists &amp; projects” in the sidebar, drag to reorder, and group tasks into sections.</p>
      <p><strong>Tags</strong> cut across lists. Nest them with a slash (#home/garden), and drag a task onto a tag in the sidebar to tag it without moving it.</p>
      <p><strong>Views</strong> collect tasks from everywhere: Today, Upcoming, Overdue, Waiting, Recently completed and Assigned to me.</p>
      <p>Open a task to add subtasks, a checklist, notes in Markdown, a repeat rule, reminders and dated diary updates. Save task shapes you reuse as <strong>templates</strong>.</p>
      <div className="flex flex-wrap gap-2"><Go to="tasks" onNavigate={onNavigate}>Open the Inbox</Go><Go to="templates" onNavigate={onNavigate}>Open Templates</Go></div>
    </> },
    { id: "find", title: "Finding things", icon: <Search/>, keywords: "search find palette jump command", body: <>
      <p>The sidebar search box matches words in titles, notes, subtasks, diary entries and pad notes. <kbd>Ctrl+K</kbd> opens the command palette: type a few letters to jump to any view, list, tag or task, or to run an action.</p>
    </> },
    { id: "pad", title: "Personal pad and summaries", icon: <StickyNote/>, keywords: "pad notes scratch summaries summary review morning evening journal diary make task", body: <>
      <p>The <strong>Personal pad</strong> is for thoughts that are not tasks yet. Notes save as you type. Select some text and choose <em>Make task</em> to turn it into a task that links back to its spot on the pad.</p>
      <p><strong>Summaries</strong> are built from your own lists and history: a morning plan, an evening summary and a weekly look back. From an evening summary you can carry unfinished tasks over to tomorrow.</p>
      <div className="flex flex-wrap gap-2"><Go to="pad" onNavigate={onNavigate}>Open the pad</Go><Go to="summaries" onNavigate={onNavigate}>Open Summaries</Go></div>
    </> },
    { id: "reminders", title: "Reminders and waiting", icon: <Bell/>, keywords: "reminder notification alert waiting follow up delegate", body: <>
      <p>Add reminders in quick add (“remind me 1h before”) or from the task. Mark a task as waiting on someone and set a follow-up date so it comes back to you. Everything that fired is listed under Alerts.</p>
      <p>Windows notifications after Chaos closes need the installed app; you can keep Chaos running in the notification area from Settings.</p>
      <Go to="notifications" onNavigate={onNavigate}>Open Notifications</Go>
    </> },
    { id: "shortcuts", title: "Keyboard shortcuts", icon: <Keyboard/>, keywords: "keyboard shortcut keys hotkey", body: <>
      <h4 className="text-sm font-semibold">Anywhere in Chaos</h4><Keys rows={appShortcuts}/>
      <h4 className="text-sm font-semibold">In task lists</h4><Keys rows={listShortcuts}/>
      <h4 className="text-sm font-semibold">While writing notes</h4><Keys rows={editorShortcuts}/>
      <p>Shortcuts that work from any app, for quick add and a new pad note, are set under Settings → Shortcuts.</p>
      <Go to="settings" onNavigate={onNavigate}>Open Settings</Go>
    </> },
    { id: "data", title: "Undo, backups and your data", icon: <Database/>, keywords: "undo backup export import restore delete trash data privacy", body: <>
      <p>Almost every change can be undone with <kbd>Ctrl+Z</kbd> or the Undo button that appears after it. Deleted tasks, lists and tags can be restored from their “Deleted” section.</p>
      <p>Your data lives in a local database. Back it up, export it, or import from CSV and Org files under Data &amp; backups. Backups are ordinary local files and are not encrypted.</p>
      <Go to="data" onNavigate={onNavigate}>Open Data &amp; backups</Go>
    </> },
  ];
}

/** Searches a topic's title, keywords and visible text. */
export function topicMatches(topic: Pick<Topic, "title" | "keywords">, text: string, query: string) {
  const haystack = `${topic.title} ${topic.keywords} ${text}`.toLowerCase();
  return query.toLowerCase().split(/\s+/).filter(Boolean).every(word => haystack.includes(word));
}

/** In-app guide: how to capture, plan, organise and find work, plus every keyboard shortcut. */
export function Help({ onNavigate }: { onNavigate: (view: HelpLink) => void }) {
  const [query, setQuery] = React.useState("");
  const all = topics(onNavigate);
  const bodies = React.useRef<Record<string, string>>({});
  const shown = query.trim() ? all.filter(topic => topicMatches(topic, bodies.current[topic.id] ?? "", query)) : all;
  return <div className="grid gap-5">
    <PageHeader eyebrow="Help" title="Using Chaos" subtitle="How to capture, plan and find your work. Press F1 to come back here."/>
    <div className="flex items-center gap-1.5 rounded-md border border-input bg-background px-2 focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/30">
      <Search className="size-4 shrink-0 text-muted-foreground"/>
      <label htmlFor="help-search" className="sr-only">Search help</label>
      <input id="help-search" type="search" className="h-9 min-w-0 flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground" placeholder="Search help, e.g. repeat, backup, shortcut" value={query} onChange={e => setQuery(e.target.value)} autoComplete="off"/>
    </div>
    {!query.trim() && <nav aria-label="Help topics" className="flex flex-wrap gap-1.5">
      {all.map(topic => <a key={topic.id} href={`#help-${topic.id}`} className="inline-flex items-center gap-1.5 rounded-full border bg-card px-3 py-1 text-xs hover:border-input hover:bg-accent [&_svg]:size-3.5 [&_svg]:text-muted-foreground"
        onClick={event => { event.preventDefault(); document.getElementById(`help-${topic.id}`)?.scrollIntoView?.({ block: "start", behavior: "smooth" }); }}>{topic.icon}{topic.title}</a>)}
    </nav>}
    {shown.length === 0 && <p className="text-sm text-muted-foreground">No help topic mentions “{query.trim()}”.</p>}
    {shown.map(topic => <section key={topic.id} id={`help-${topic.id}`} aria-labelledby={`help-${topic.id}-title`} className="grid scroll-mt-12 gap-3 rounded-xl border bg-card p-4 text-sm leading-relaxed [&_kbd]:rounded [&_kbd]:border [&_kbd]:bg-muted [&_kbd]:px-1 [&_kbd]:font-sans [&_kbd]:text-xs"
      ref={element => { if (element) bodies.current[topic.id] = element.textContent ?? ""; }}>
      <h3 id={`help-${topic.id}-title`} className="flex items-center gap-2 text-base font-semibold [&_svg]:size-4 [&_svg]:text-muted-foreground">{topic.icon}{topic.title}</h3>
      {topic.body}
    </section>)}
    <p className="flex items-center gap-1.5 text-xs text-muted-foreground"><BookOpen className="size-3.5"/>Tip: hover over a button to see what it does, or press Ctrl+K and type what you are looking for.</p>
  </div>;
}
