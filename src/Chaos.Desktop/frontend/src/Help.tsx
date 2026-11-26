import React from "react";
import {
  ArrowRight,
  Bot,
  BookOpen,
  CalendarDays,
  Database,
  FileText,
  Keyboard,
  ListTodo,
  GitPullRequest,
  Rocket,
  Search,
  Smartphone,
  Sparkles,
  StickyNote,
  Sun,
  Bell,
  Users,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { PageHeader } from "@/components/common";
import { listShortcuts } from "./listKeyboard";

export type HelpLink =
  "myday" | "myweek" | "calendar" | "tasks" | "pad" | "pages" | "summaries" | "notifications" | "templates" | "data" | "settings";

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
  ["[[", "Link to a page, task, list or pad note"],
  ["Tab / Shift+Tab", "In a page's list: indent or outdent an item with its children"],
  ["Alt+↑ / Alt+↓", "In a page's list: move an item with its children"],
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
  ["/list", 'Puts the task in a list or project; use /"Two words" for names with spaces'],
  ["~work / ~personal", "Sets the Work or Personal category"],
  ["waiting on …", "Marks the task as waiting on someone; “follow up …” adds a follow-up date"],
];

type Topic = { id: string; title: string; icon: React.ReactNode; keywords: string; body: React.ReactNode };

function Keys({ rows }: { rows: readonly [string, string][] }) {
  return (
    <dl className="grid grid-cols-[minmax(7rem,auto)_1fr] gap-x-4 gap-y-1.5 text-sm">
      {rows.map(([keys, action]) => (
        <React.Fragment key={keys + action}>
          <dt>
            <kbd className="rounded border bg-muted px-1.5 py-0.5 font-sans text-xs font-semibold">{keys}</kbd>
          </dt>
          <dd className="text-muted-foreground">{action}</dd>
        </React.Fragment>
      ))}
    </dl>
  );
}

function Go({ to, onNavigate, children }: { to: HelpLink; onNavigate: (view: HelpLink) => void; children: React.ReactNode }) {
  return (
    <Button type="button" size="sm" variant="outline" className="justify-self-start" onClick={() => onNavigate(to)}>
      {children}
      <ArrowRight />
    </Button>
  );
}

function topics(onNavigate: (view: HelpLink) => void): Topic[] {
  return [
    {
      id: "start",
      title: "Getting started",
      icon: <Rocket />,
      keywords: "begin first start routine daily workflow overview",
      body: (
        <>
          <p>
            Chaos keeps everything on this computer: no account and no network needed. Connecting Google Calendar or GitHub is optional (see
            Calendar and meetings, and GitHub issues). A simple routine that works well:
          </p>
          <ol className="grid list-decimal gap-1 pl-5">
            <li>
              <strong>Capture</strong> anything the moment it comes up with <kbd>Ctrl+N</kbd>. It lands in the Inbox unless you name a list.
            </li>
            <li>
              <strong>Plan the week</strong> once, in My Week: carry over what slipped and spread tasks across the days.
            </li>
            <li>
              <strong>Choose today's work</strong> each morning in My Day. Suggestions show what is planned, due or left over from
              yesterday.
            </li>
            <li>
              <strong>Review</strong> in Summaries: each evening shows what you finished and what slipped.
            </li>
          </ol>
          <Go to="myday" onNavigate={onNavigate}>
            Open My Day
          </Go>
        </>
      ),
    },
    {
      id: "quick-add",
      title: "Quick add in plain language",
      icon: <Sparkles />,
      keywords: "capture new task syntax date time tag priority repeat reminder estimate language parse",
      body: (
        <>
          <p>
            Type a task the way you would say it. Recognised parts appear as chips with a preview of the task; click a chip to turn it back
            into plain text (for example when “May” is a person, not a month). Paste several lines to create one task per line.
          </p>
          <div className="overflow-x-auto rounded-lg border">
            <table className="w-full text-sm">
              <thead className="bg-muted/60 text-left text-xs text-muted-foreground">
                <tr>
                  <th className="px-3 py-1.5 font-medium">You type</th>
                  <th className="px-3 py-1.5 font-medium">You get</th>
                </tr>
              </thead>
              <tbody>
                {quickAddExamples.map(([typed, result]) => (
                  <tr key={typed} className="border-t">
                    <td className="px-3 py-1.5 font-mono text-xs">{typed}</td>
                    <td className="px-3 py-1.5 text-muted-foreground">{result}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <Keys rows={quickAddParts} />
          <p>
            Quick add also understands French, Italian, Spanish, Indonesian and Arabic, such as demain, domani, mañana, besok and غدًا,
            whatever language you start in.
          </p>
        </>
      ),
    },
    {
      id: "planning",
      title: "My Day and My Week",
      icon: <Sun />,
      keywords: "plan today week capacity hours suggestions myday myweek meetings busy confirm planned date",
      body: (
        <>
          <p>
            <strong>My Week</strong> turns everything on your plate into a plan: look back at last week, carry over or drop unfinished work,
            set a few goals and drag tasks onto days. Each day shows its estimated work against your available hours, less any busy meetings
            (“2 h meetings”). Confirming the plan sets each task's planned date to the day you put it on, so the calendar and My Week agree;
            Undo reverses it.
          </p>
          <p>
            <strong>My Day</strong> starts each morning with what your week plan put on today. Add more from the suggestions, or press{" "}
            <kbd>M</kbd> on any task in a list to add it. Adding a task to My Day never changes its planned or due date. Today's meetings
            are listed above the plan, and a warning appears when the day's open estimates will not fit in the time left after meetings. It
            is only a warning; nothing is moved for you.
          </p>
          <p>
            Use the Work / Personal switch (<kbd>Ctrl+2</kbd>, <kbd>Ctrl+3</kbd>) to see one side of your life at a time; <kbd>Ctrl+1</kbd>{" "}
            shows both.
          </p>
          <div className="flex flex-wrap gap-2">
            <Go to="myweek" onNavigate={onNavigate}>
              Open My Week
            </Go>
            <Go to="myday" onNavigate={onNavigate}>
              Open My Day
            </Go>
          </div>
        </>
      ),
    },
    {
      id: "calendar",
      title: "Calendar and meetings",
      icon: <CalendarDays />,
      keywords:
        "calendar month week day agenda drag reschedule planned due repeat unscheduled meetings google ics feed webcal busy sign in connect account decline refresh focus time blocking block chaos calendar titles busy",
      body: (
        <>
          <p>
            The <strong>Calendar</strong> shows your tasks by month, week, day or as an agenda. Planned tasks are blocks, due dates are
            markers, and repeating tasks show their next occurrences. Each day shows its estimated work against your available hours, and
            overload stays visible whatever you filter. Filter by Work or Personal, tag or list, or show a custom view's calendar.
          </p>
          <p>
            Drag a planned task to another day or time slot to reschedule it (Undo reverses it); drop it on the all-day row to clear its
            time. From the keyboard, use a task's menu: <em>Move to date…</em>, or <em>Plan…</em> for tasks in the Unscheduled panel. Arrow
            keys move between days and Enter opens one.
          </p>
          <p>
            <strong>Meetings</strong> from other calendars appear read-only in their own colour, and busy meetings take time off your
            available hours in My Week and My Day. Chaos never changes those calendars. On a meeting, <em>Make a task</em> creates “Prepare:
            …”, planned for the day before (or the same day if the meeting starts after noon) and linked back to the meeting.
          </p>
          <p>Add calendars under Settings → Calendars:</p>
          <ul className="grid list-disc gap-1 pl-5">
            <li>
              <strong>Calendar feeds.</strong> Paste a calendar's secret ICS address (https:// or webcal://). The address is kept in
              protected storage on this computer, not in your data, and is entered again on each device. Feeds refresh every 15 minutes.
            </li>
            <li>
              <strong>Google Calendar.</strong> Connect one or more Google accounts (sign-in happens in your web browser; Chaos can only
              read). The first time, paste the client ID and secret of a Desktop OAuth client from Google Cloud. Then choose which calendars
              to show, with a colour and a busy-time choice each. Google calendars refresh every 5 minutes and when you return to the
              window; <em>Refresh now</em> fetches straight away, and a meeting you decline in Google disappears on the next refresh.
            </li>
            <li>
              <strong>Time blocking.</strong> Turn it on under Settings → Calendars to show tasks with a planned time and an estimate as
              blocks in a separate Google calendar named <em>Chaos</em>, which the app creates. Choose whether blocks show task titles or
              just Busy, and which spaces count (a shared task only when it is assigned to you). Move a block in Google and the task's
              planned time moves with it; delete the block and the task keeps its date but loses its time. If the task also changed here,
              the newer change wins and the other one is kept in the task's history. Chaos never writes to any other calendar. One device of
              yours keeps the calendar up to date every few minutes; turn time blocking off there, or move it to this device.
            </li>
          </ul>
          <p>
            If Google says <em>sign in again</em> (your access expired or was revoked), the last meetings stay and a Sign in again button
            appears. On another device that learns of the account, press <em>Sign in on this device</em>. <em>Disconnect</em> removes the
            account everywhere and revokes Chaos's access at Google.
          </p>
          <p>
            Meetings appear in daily and morning summaries only when <em>Include meetings in summaries</em> is on (in the summary settings
            on the Summaries page); it is off by default.
          </p>
          <div className="flex flex-wrap gap-2">
            <Go to="calendar" onNavigate={onNavigate}>
              Open Calendar
            </Go>
            <Go to="settings" onNavigate={onNavigate}>
              Open Settings
            </Go>
          </div>
        </>
      ),
    },
    {
      id: "github",
      title: "GitHub issues",
      icon: <GitPullRequest />,
      keywords:
        "github issues sync repository gh cli connect label milestone assignee comments diary pull request conflict pause disconnect",
      body: (
        <>
          <p>
            Chaos can bring the issues of your GitHub repositories in as tasks and keep them in step with GitHub. It is off until you
            connect, and nothing is sent to GitHub before that.
          </p>
          <p>
            <strong>Connect.</strong> Install GitHub CLI (2.103.3 or newer) and sign in once in a terminal with{" "}
            <code>gh auth login --hostname github.com</code>. Then open Settings → GitHub and press <em>Connect GitHub</em>. Chaos never
            asks for or stores your password or token; GitHub CLI keeps your login, and disconnecting leaves it signed in. In a shared
            space, an owner connects it from the space's <em>Connections</em> tab; members need no setup, and only owners manage the
            connection. One owner's device does the syncing; if that owner leaves or is demoted, another owner chooses{" "}
            <em>Run GitHub sync on this device</em>.
          </p>
          <p>
            <strong>Add repositories.</strong> Under <em>Add a repository</em>, pick one and choose the list or project new tasks land in,
            whether they count as Personal or Work, and a filter: labels, milestone, assigned to you, and how many days of closed issues to
            include. Turn on <em>Use milestones as projects</em> to give each milestone its own project. Changes to the destination only
            affect issues imported later. In a shared space, adding a <em>private</em> repository asks you to confirm first: everyone in the
            space will see its issues, including people without access to it on GitHub.
          </p>
          <p>
            <strong>What syncs.</strong> A linked task carries a GitHub mark and a link to its issue. These stay in step both ways, on a
            schedule and shortly after you change a linked task, or straight away with <em>Sync now</em>:
          </p>
          <ul className="grid list-disc gap-1 pl-5">
            <li>
              Title, notes (the issue text) and open or closed state. Done matches a completed issue and Cancelled a closed “not planned”
              one.
            </li>
            <li>Checklist items in the issue text and the task's subtasks.</li>
            <li>Labels and tags. Local tags only become labels that already exist, unless you allow creating labels in the settings.</li>
            <li>
              Comments and diary updates. Comments arrive as diary updates; tick <em>Also post to GitHub</em> on a new update to send it
              (the Settings default controls the tick). In a shared space the comment goes out under the owner's GitHub login, signed with
              your name (<em>— Your name (via Chaos)</em>).
            </li>
            <li>
              Assignees are shown on the task; use <em>Assign to me</em> and <em>Unassign me</em> to change your own. In a shared space, set
              your GitHub login on the <em>Members</em> tab (it is used to match GitHub assignees to you, and is not verified); assigning a
              member with a login puts that login on the issue, and the task's GitHub notice says when someone has no login set.
            </li>
            <li>Pull requests that close the issue are listed on the task with their state and review decision.</li>
          </ul>
          <p>
            Planned date, estimate, priority, My Day, reminders and waiting stay in Chaos and never go to GitHub. Recurrence is not
            available on linked tasks.
          </p>
          <p>
            <strong>Conflicts.</strong> If you and GitHub change the same title, notes or state, GitHub's version wins. The value you
            replaced is kept in the task's history and a notice on the task offers to dismiss it.
          </p>
          <p>
            <strong>Leaving things.</strong> Deleting a task never changes the issue. An issue that stops matching the filter stays as a
            task that no longer syncs. <em>Keep as an ordinary task</em> detaches one task; removing a repository or disconnecting keeps all
            its tasks. Pause stops syncing for the connection or one repository. If GitHub CLI is missing, signed out or on another account,
            syncing stops with a message and a recheck button. Only an owner can <em>Keep as an ordinary task</em> in a shared space.
          </p>
          <p>
            Backups and exports include issue text, including from private repositories. Custom views can filter by repository, issue state,
            labels, milestone, assignee, author, issue type, and by linked pull requests and their review decision, using the last synced
            state, so they work offline.
          </p>
          <Go to="settings" onNavigate={onNavigate}>
            Open Settings
          </Go>
        </>
      ),
    },
    {
      id: "organise",
      title: "Lists, projects, tags and views",
      icon: <ListTodo />,
      keywords:
        "list project section tag view today upcoming overdue waiting subtasks templates drag reorder board kanban column swimlane wip custom view saved search condition filter export csv spreadsheet",
      body: (
        <>
          <p>
            <strong>Lists</strong> hold open-ended tasks; <strong>projects</strong> have an end and show progress. Create them with the +
            next to “Lists &amp; projects” in the sidebar, drag to reorder, and group tasks into sections.
          </p>
          <p>
            <strong>Boards</strong> show a list or project as a Kanban board: switch with the List / Board control above the tasks (each
            list remembers its own). Drag cards between and within columns (a card stays in its swimlane), or use the arrow keys and each
            card's menu. Columns group by status, section, priority or a tag's children, with optional swimlanes, WIP limits and hidden
            columns in the board settings. Switching layout never changes the tasks. On a shared list, board settings and card moves are
            shared and updates from other members appear while the board is open.
          </p>
          <p>
            <strong>Shared calendars</strong> can be narrowed to a space with the Space filter. Planned and due tasks and future repeats in
            that space appear there; your meetings and week plans remain private to your account.
          </p>
          <p>
            <strong>Custom views</strong> keep a search you want again. Under Views choose New view, then add conditions on status,
            priority, tags, dates (including relative ones such as this week or the next 7 days), estimate, text and more; group conditions
            with all or any. The count under the editor is the exact number of tasks that match. Show the result as a list (optionally
            grouped), as a table where title, status, priority, dates and estimate can be edited in place, or as a board (columns by status,
            priority or a tag group, with swimlanes by priority, tag group, Work or Personal, or list; dragging a card changes its column,
            and the view's sort keeps ordering the cards), and pin the view to the sidebar to see its count. A view can be edited,
            duplicated, deleted (with Undo) and exported as a spreadsheet (CSV) to your exports folder. On a search page, Save as view turns
            the words and filters into a view. Adding a task inside a view fills in the fields its conditions fix. Switch on "Tell me when a
            new task matches" and Chaos tells you, inside the app while it is open, when new tasks join the view (one notice per view, such
            as "3 new tasks in My open bugs", held until your quiet hours end). Tasks already in the view when you switch it on stay quiet,
            and a task that leaves and comes back is announced again only after 24 hours. When GitHub is connected, views can also use the
            GitHub repository, issue state (open or closed), labels, milestone and assignee of a task that came from an issue; these read
            the last state Chaos synced, so they work offline. Choose Share with space to copy a personal view into a shared space; the
            original stays personal and the copy only matches that space. Each person controls their own alerts for a shared view.
          </p>
          <p>
            <strong>Tags</strong> cut across lists. Nest them with a slash (#home/garden), and drag a task onto a tag in the sidebar to tag
            it without moving it.
          </p>
          <p>
            <strong>Views</strong> collect tasks from everywhere: Today, Upcoming, Overdue, Waiting, Recently completed, Assigned to me,
            Delegated by me and Agent work.
          </p>
          <p>
            Open a task to add subtasks, a checklist, notes in Markdown, a repeat rule, reminders and dated diary updates. The history names
            members whose identities are available, including people who have left a space. Save task shapes you reuse as{" "}
            <strong>templates</strong>.
          </p>
          <div className="flex flex-wrap gap-2">
            <Go to="tasks" onNavigate={onNavigate}>
              Open the Inbox
            </Go>
            <Go to="templates" onNavigate={onNavigate}>
              Open Templates
            </Go>
          </div>
        </>
      ),
    },
    {
      id: "find",
      title: "Finding things",
      icon: <Search />,
      keywords: "search find palette jump command",
      body: (
        <>
          <p>
            The sidebar search box matches words in titles, notes, subtasks, diary entries, pages and pad notes. <kbd>Ctrl+K</kbd> opens the
            command palette: type a few letters to jump to any view, list, tag or task, or to run an action.
          </p>
        </>
      ),
    },
    {
      id: "pages",
      title: "Pages and daily pages",
      icon: <FileText />,
      keywords:
        "pages page wiki writing notes daily journal diary today outline backlinks links versions history front matter properties background icon focus",
      body: (
        <>
          <p>
            <strong>Pages</strong> are for writing you keep: meeting notes, a project write-up, a recipe collection. Put a page under
            another to build a tree, and type <kbd>[[</kbd> to link a page, a task or a list. Every page shows what links to it, and links
            keep working when a page is renamed or moved. Linking to a page that does not exist yet creates it when you follow the link.
          </p>
          <p>
            Pages save as you type and keep earlier <strong>versions</strong> you can compare and restore. In lists, <kbd>Tab</kbd>,{" "}
            <kbd>Shift+Tab</kbd> and <kbd>Alt+↑/↓</kbd> work as an outline, and read mode folds headings and list items. <em>Focus</em>{" "}
            hides everything else; <em>Contents</em> lists the headings.
          </p>
          <p>
            A page can start with <strong>properties</strong> between two <code>---</code> lines: an icon, a background (a My Day scene or a
            colour), tags and aliases. Other keys are kept as written.
          </p>
          <p>
            <strong>Daily pages</strong> have their own view, <em>Daily page</em> in the sidebar, apart from your pages: today's page and
            the days you wrote before. Today's page is also one step from My Day and the command palette. It is only kept once you write in
            it, and it shows that day's evening summary beside it. Select lines on any page and choose <em>Make task</em>; lines indented
            under the first become subtasks.
          </p>
          <Go to="pages" onNavigate={onNavigate}>
            Open Pages
          </Go>
        </>
      ),
    },
    {
      id: "pad",
      title: "Personal pad and summaries",
      icon: <StickyNote />,
      keywords:
        "pad notes scratch summaries summary review morning evening journal diary make task drawing draw sketch photo voice audio record microphone",
      body: (
        <>
          <p>
            The <strong>Personal pad</strong> is for thoughts that are not tasks yet. Notes save as you type. Select some text and choose{" "}
            <em>Make task</em> to turn it into a task that links back to its spot on the pad. When a note grows into something to keep, move
            it to a page or add it to today's page.
          </p>
          <p>
            The pad also holds more than typed text. <em>New drawing</em> opens a canvas for a mouse, pen or touch: choose a pen,
            highlighter or eraser (the eraser removes whole strokes), pick a colour, and undo or redo with <kbd>Ctrl+Z</kbd> and{" "}
            <kbd>Ctrl+Y</kbd>. Drawings save as you draw, and a picture of the drawing is kept for search and export. Choose{" "}
            <em>Select a part</em> and drag over the drawing, then <em>Make task</em>: the task link opens the pad with that part outlined.
            Chaos cannot read handwriting yet, so you type the task's title. <em>New photo note</em> makes a note from a picture file.{" "}
            <em>Voice note</em> records up to 30 minutes with a timer and level meter; if the microphone is blocked or missing, you can
            attach an audio file instead. Drawing needs a pointing device: the keyboard can pick tools and undo, but it cannot draw.
          </p>
          <p>
            <strong>Summaries</strong> are built from your own lists and history: a morning plan, an evening summary and a weekly look back.
            From an evening summary you can carry unfinished tasks over to tomorrow, and post a standup to one of your shared spaces from
            the <em>Your spaces</em> section. Meetings are listed only if you switch on <em>Include meetings in summaries</em>.
          </p>
          <p>
            An AI agent you have connected (see Agents) can read the pad and <em>suggest</em> tasks. They appear on the pad as{" "}
            <em>Suggested by</em> the agent's name, each with <strong>Accept</strong> and <strong>Dismiss</strong>. Nothing becomes a task
            until you accept it, and accepting links the new task back to the note it came from. You choose the list it lands in.
          </p>
          <div className="flex flex-wrap gap-2">
            <Go to="pad" onNavigate={onNavigate}>
              Open the pad
            </Go>
            <Go to="summaries" onNavigate={onNavigate}>
              Open Summaries
            </Go>
          </div>
        </>
      ),
    },
    {
      id: "reminders",
      title: "Reminders and waiting",
      icon: <Bell />,
      keywords: "reminder notification alert waiting follow up delegate delegation nudge",
      body: (
        <>
          <p>
            Add reminders in quick add (“remind me 1h before”) or from the task. Mark a task as waiting on someone and set a follow-up date
            so it comes back to you. Follow-ups for tasks you delegated to a member offer a nudge and a way to take the task back.
            Everything that fired is listed under Alerts. Alerts, summaries and changes made by background work (reminders, calendar and
            GitHub sync) appear on their own, without switching windows.
          </p>
          <p>
            Windows notifications after Chaos closes need the installed app; you can keep Chaos running in the notification area from
            Settings.
          </p>
          <Go to="notifications" onNavigate={onNavigate}>
            Open Notifications
          </Go>
        </>
      ),
    },
    {
      id: "shortcuts",
      title: "Keyboard shortcuts",
      icon: <Keyboard />,
      keywords: "keyboard shortcut keys hotkey",
      body: (
        <>
          <h4 className="text-sm font-semibold">Anywhere in Chaos</h4>
          <Keys rows={appShortcuts} />
          <h4 className="text-sm font-semibold">In task lists</h4>
          <Keys rows={listShortcuts} />
          <h4 className="text-sm font-semibold">While writing notes</h4>
          <Keys rows={editorShortcuts} />
          <p>Shortcuts that work from any app, for quick add and a new pad note, are set under Settings → Shortcuts.</p>
          <Go to="settings" onNavigate={onNavigate}>
            Open Settings
          </Go>
        </>
      ),
    },
    {
      id: "sharing",
      title: "Sharing",
      icon: <Users />,
      keywords:
        "share shared space invite member role owner editor viewer leave remove move list team collaborate link email offline delegate delegation nudge review follow up assigned standup summary rotation rotate rotating host agent",
      body: (
        <>
          <p>
            Everything is Personal until you share it. Sharing happens per space: a <strong>shared space</strong> holds lists and projects
            that the people you invite can see and, depending on their role, change. Your Personal lists, My Day, reminders and personal
            tags are never visible to anyone else.
          </p>
          <h3 className="font-semibold">Roles</h3>
          <ul className="grid list-disc gap-1 pl-5">
            <li>
              <strong>Owner</strong>: everything, including inviting people, changing roles, removing people and deleting the space.
            </li>
            <li>
              <strong>Editor</strong>: creates and changes tasks, lists, pages and notes, and adds comments.
            </li>
            <li>
              <strong>Viewer</strong>: reads everything, adds comments and posts standups. Controls that would change something are turned
              off, and a note says you are a viewer.
            </li>
          </ul>
          <h3 className="font-semibold">Start sharing</h3>
          <ul className="grid list-disc gap-1 pl-5">
            <li>
              <strong>New shared space…</strong> in the sidebar creates a space. <strong>Share list…</strong> on a Personal list creates a
              new space named after the list and moves the list into it. <strong>Move to space…</strong> moves a list or project into a
              space you already belong to.
            </li>
            <li>
              A move takes the list with all its tasks, subtasks, notes, updates and attachments. What stays yours: My Day, reminders and
              personal tags on those tasks.
            </li>
          </ul>
          <p>
            Members can open attachments in a shared space. Owners and Editors can add them; Viewers can attach files to their own diary
            entries. Shared files count against the uploader’s storage allowance and remain in the space if that person leaves.
          </p>
          <h3 className="font-semibold">Writing together</h3>
          <p>
            Notes and pages in a shared space appear on other members’ screens within seconds. When two people change the same note or page
            at once, Chaos combines the changes line by line, so edits to different paragraphs both stay. Where the same lines changed in
            two places, both versions are kept between marker lines (<code>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</code>, <code>=======</code>,{" "}
            <code>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</code>) with a notice; edit the text and delete the marker lines. A page keeps the text it
            replaced under Versions. On a shared drawing, everyone’s strokes are kept, and a stroke one person erases stays erased. A line
            under the text says who else has it open (“Omar is editing”). It is never shown for Personal notes.
          </p>
          <h3 className="font-semibold">Invites</h3>
          <p>
            An owner can invite by email or make a link. A link can expire (1, 7 or 30 days, or never) and can be limited to 1 or 10 uses.
            The link is shown once when you create it, so copy it then; open invitations can be turned off at any time. The person opens the
            link in a browser, signs in, and joins. On a computer that already has Chaos, choose <strong>Join with a link…</strong> and
            paste it.
          </p>
          <h3 className="font-semibold">Delegating</h3>
          <p>
            Owners and Editors can hand a task to a member: open the task and choose <strong>Delegate…</strong> in the Delegation section.
            Name the person, say what done looks like, and optionally a due date and whether you want to review the result. The task is
            assigned to them. In quick add, <code>+Omar follow up Friday</code> in a shared space does the same and sets your follow-up.
          </p>
          <p>
            The other person can <strong>Accept</strong>, <strong>Ask</strong> (the question goes on the task) or <strong>Decline</strong>{" "}
            with a reason; a declined task comes back to you. When they mark it done, you either review it (the task waits as{" "}
            <em>Needs review</em>
            until you <strong>Accept result</strong> or <strong>Send back</strong>) or it is simply completed. You can{" "}
            <strong>Take back</strong> a task while it is still open, or hand it to someone else. Everything that happened is written in the
            task’s updates.
          </p>
          <p>
            Your follow-up lands in My Day with <strong>Nudge</strong>, <strong>Open</strong> and <strong>Take back</strong>. A nudge is a
            short note the other person sees in their inbox (quiet hours apply); the text is prewritten in your language and you can change
            it. Three nudges per task per 24 hours. The <strong>Delegated by me</strong> view lists what you handed out, by person; the
            evening and weekly summaries list delegations waiting for an answer or your review.
          </p>
          <h3 className="font-semibold">Rotating repeating tasks</h3>
          <p>
            In a shared space, a repeating task can rotate its host: in the repeat editor's <strong>Rotation</strong> section, tick the
            members who take turns and choose who starts. Each new occurrence goes to the next person in the order, skipping anyone who has
            left the space. The editor shows who is next. Changing the rotation applies from the next occurrence; occurrences already
            created keep their host.
          </p>
          <p>
            A task that must be completed by <em>each</em> assignee is completed from the desktop; the phone web app disables its check box
            and says “Complete on desktop”.
          </p>
          <h3 className="font-semibold">Standups and what your team did</h3>
          <p>
            The evening and weekly summaries have a <strong>Your spaces</strong> section: what other members completed, added and what
            became blocked in each space you belong to, what is due there in the next seven days, and how its projects moved. Your own work
            stays in the sections above, and meetings never appear there.
          </p>
          <p>
            <strong>Post as standup to …</strong> under a space opens an editor with your own completed, planned and blocked tasks in that
            space. Change the text as you like; nothing is posted until you choose <strong>Post standup</strong>. Standups show on the
            space’s Overview, newest first. You can edit your own; you or an owner can delete it.
          </p>
          <h3 className="font-semibold">Leaving and removal</h3>
          <p>
            Anyone can leave a space from its Members tab, except the last owner. When you leave or are removed, the space’s lists leave
            this computer, and your My Day entries and reminders for its tasks are cleared. Unsent changes are counted in a notice so
            nothing vanishes silently. What you wrote stays in the space under your name.
          </p>
          <h3 className="font-semibold">Offline</h3>
          <p>
            Creating a space, inviting, joining, leaving and moving need a connection; Chaos says “Connect to …” when it cannot reach the
            service. Editing a shared list offline works like editing a Personal one, and your changes merge when the connection returns.
          </p>
        </>
      ),
    },
    {
      id: "agents",
      title: "Agents (AI assistants)",
      icon: <Bot />,
      keywords:
        "agent ai assistant mcp claude token ready for agent claim release take over proposal suggest suggestion delegate revert undo pause remove server agent oauth connector",
      body: (
        <>
          <p>
            An <strong>agent</strong> is an AI assistant, such as Claude Code or Claude Desktop, that you let work on your tasks. It has its
            own name, shows in task history as an agent, and can only do what you allow. You decide which work it sees, it takes one task at
            a time, and you can undo what it did.
          </p>
          <h3 className="font-semibold">Local agents (Personal lists)</h3>
          <ul className="grid list-disc gap-1 pl-5">
            <li>
              Open <strong>Settings → Agents</strong>, add an agent and tick the Personal lists it may work in. With nothing ticked it sees
              nothing.
            </li>
            <li>
              Choose <strong>New token</strong>. The token is shown once, with a ready-made <em>Claude Code</em> command and{" "}
              <em>Claude Desktop</em> configuration. Run or paste it, then restart that app. It works offline and needs no account.
            </li>
            <li>
              <strong>Rename</strong>, <strong>Pause</strong> (the agent stops and lets go of its tasks) and <strong>Remove</strong> are on
              the agent. Remove can also undo its changes.
            </li>
          </ul>
          <h3 className="font-semibold">Server agents (shared spaces)</h3>
          <p>
            In a shared space the agent works through your online account. Add one under <strong>Settings → Agents → Server agents</strong>,
            then an owner adds it to a space with <strong>Members → Add agent</strong> and picks the lists it may work in. An assistant that
            supports remote connectors connects to the service's <code>/mcp</code> address and signs in; the consent page asks which agent
            it acts as.
          </p>
          <h3 className="font-semibold">Handing work to an agent</h3>
          <ul className="grid list-disc gap-1 pl-5">
            <li>
              On a task, switch on <strong>Ready for agent</strong>, optionally naming one agent. The task joins the agent's queue and the{" "}
              <strong>Agent work</strong> view.
            </li>
            <li>
              In a shared space you can <strong>Delegate…</strong> a task to an agent member like to a person. Delegating to an agent always
              asks for your review unless the space lets agents close tasks. Agents cannot be nudged.
            </li>
          </ul>
          <h3 className="font-semibold">While it works</h3>
          <p>
            An agent <strong>claims</strong> a task before changing it, and the task shows who holds it and until when. A claim runs out if
            the agent goes quiet and the task returns to Ready for agent. You can <strong>Take over</strong> or <strong>Release</strong> a
            claim yourself. Agents can add subtasks and notes, <strong>ask for help</strong> (the task becomes waiting with their question)
            and <strong>hand back</strong> when finished, which puts the task in <em>Needs review</em> and under “Waiting for your review”
            in My Day. On a task linked to a GitHub issue, their updates are posted signed “— name (agent, via Chaos)”. An agent cannot
            cancel tasks, delete things or change members, and it has hourly limits on claims and additions.
          </p>
          <h3 className="font-semibold">Undoing an agent</h3>
          <p>
            <strong>Undo recent changes by …</strong> in Settings → Agents puts back the agent's changes, newest first. Anything you changed
            afterward is listed and left alone.
          </p>
          <Go to="settings" onNavigate={onNavigate}>
            Open Settings
          </Go>
        </>
      ),
    },
    {
      id: "phone",
      title: "On your phone",
      icon: <Smartphone />,
      keywords: "phone mobile web app pwa browser install home screen offline shared space role viewer",
      body: (
        <>
          <p>
            Once you sign in to the online service, open its <code>/app</code> address on your phone and use <em>Add to Home Screen</em>.
            The web app shows My Day and your lists, lets you capture, complete and reopen tasks, and plan tasks onto My Day. It opens
            offline with what it last read, but changes need a connection.
          </p>
          <p>
            Lists of your shared spaces appear under Personal, grouped by space, with your role stated once per screen. Tasks show their
            assignees and who wrote each update. Owners and Editors can change shared tasks; as a Viewer the controls are disabled, though
            you can still plan a task on your own My Day. Capture offers only lists you can write to. If you are removed from a space it
            disappears from your phone straight away.
          </p>
        </>
      ),
    },
    {
      id: "data",
      title: "Undo, backups and your data",
      icon: <Database />,
      keywords: "undo backup export import restore delete trash data privacy",
      body: (
        <>
          <p>
            Almost every change can be undone with <kbd>Ctrl+Z</kbd> or the Undo button that appears after it. Deleted tasks, lists and tags
            can be restored from their “Deleted” section.
          </p>
          <p>
            Your data lives in a local database. Back it up, export it, or import from a CSV file under Data &amp; backups. Backups are
            ordinary local files and are not encrypted.
          </p>
          <Go to="data" onNavigate={onNavigate}>
            Open Data &amp; backups
          </Go>
        </>
      ),
    },
  ];
}

/** Searches a topic's title, keywords and visible text. */
export function topicMatches(topic: Pick<Topic, "title" | "keywords">, text: string, query: string) {
  const haystack = `${topic.title} ${topic.keywords} ${text}`.toLowerCase();
  return query
    .toLowerCase()
    .split(/\s+/)
    .filter(Boolean)
    .every((word) => haystack.includes(word));
}

/** In-app guide: how to capture, plan, organise and find work, plus every keyboard shortcut. */
export function Help({ onNavigate }: { onNavigate: (view: HelpLink) => void }) {
  const [query, setQuery] = React.useState("");
  const all = topics(onNavigate);
  const bodies = React.useRef<Record<string, string>>({});
  const shown = query.trim() ? all.filter((topic) => topicMatches(topic, bodies.current[topic.id] ?? "", query)) : all;
  return (
    <div className="grid gap-5">
      <PageHeader eyebrow="Help" title="Using Chaos" subtitle="How to capture, plan and find your work. Press F1 to come back here." />
      <div className="flex items-center gap-1.5 rounded-md border border-input bg-background px-2 focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/30">
        <Search className="size-4 shrink-0 text-muted-foreground" />
        <label htmlFor="help-search" className="sr-only">
          Search help
        </label>
        <input
          id="help-search"
          type="search"
          className="h-9 min-w-0 flex-1 bg-transparent text-sm outline-none placeholder:text-muted-foreground"
          placeholder="Search help, e.g. repeat, backup, shortcut"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          autoComplete="off"
        />
      </div>
      {!query.trim() && (
        <nav aria-label="Help topics" className="flex flex-wrap gap-1.5">
          {all.map((topic) => (
            <a
              key={topic.id}
              href={`#help-${topic.id}`}
              className="inline-flex items-center gap-1.5 rounded-full border bg-card px-3 py-1 text-xs hover:border-input hover:bg-accent [&_svg]:size-3.5 [&_svg]:text-muted-foreground"
              onClick={(event) => {
                event.preventDefault();
                document.getElementById(`help-${topic.id}`)?.scrollIntoView?.({ block: "start", behavior: "smooth" });
              }}
            >
              {topic.icon}
              {topic.title}
            </a>
          ))}
        </nav>
      )}
      {shown.length === 0 && <p className="text-sm text-muted-foreground">No help topic mentions “{query.trim()}”.</p>}
      {shown.map((topic) => (
        <section
          key={topic.id}
          id={`help-${topic.id}`}
          aria-labelledby={`help-${topic.id}-title`}
          className="grid scroll-mt-12 gap-3 rounded-xl border bg-card p-4 text-sm leading-relaxed [&_kbd]:rounded [&_kbd]:border [&_kbd]:bg-muted [&_kbd]:px-1 [&_kbd]:font-sans [&_kbd]:text-xs"
          ref={(element) => {
            if (element) bodies.current[topic.id] = element.textContent ?? "";
          }}
        >
          <h3
            id={`help-${topic.id}-title`}
            className="flex items-center gap-2 text-base font-semibold [&_svg]:size-4 [&_svg]:text-muted-foreground"
          >
            {topic.icon}
            {topic.title}
          </h3>
          {topic.body}
        </section>
      ))}
      <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
        <BookOpen className="size-3.5" />
        Tip: hover over a button to see what it does, or press Ctrl+K and type what you are looking for.
      </p>
    </div>
  );
}
