# To-Do App — Product Specification

Nov 9, 2026 · @Dody Gunawinata

## Overview and vision

One to-do app for your whole life: personal errands, a book club, and your team at work, all in one place but kept properly apart.

**Guiding principle.** The most important job of the app is to help one person manage their day and their week. Sharing, connections, dashboards and agents exist to feed that personal plan, not to replace it. When features compete for time or space, the one that better helps someone plan and finish their day and week wins.

Most to-do apps make you choose. Personal apps are simple but weak at sharing; work tools are powerful but heavy and owned by your employer. This app starts as a simple personal list that works instantly, with no account and no internet, and grows into sharing with friends and colleagues when you need it.

Three promises define the product:

- **Start in seconds.** Open the app and add a task. No sign-up, no server, no waiting.
- **One plan for your day and week.** My Week and My Day pull together what matters this week and today from every part of your life, without mixing up who owns what.
- **Fits the tools you already use.** Tasks from systems like GitHub Issues appear alongside your own and stay in step with them, and meetings from Google Calendar show the time you really have.

## Who it is for

The app serves one person across three kinds of life, rather than three different kinds of customer.

| Context | Example user | What they need |
| --- | --- | --- |
| Personal | Sara, managing groceries, bills and a home renovation | Fast capture, reminders, private by default, works offline |
| Social | Omar, organising a monthly book club of 12 people | Shared lists, assigning who brings what, simple invitations, no company accounts |
| Work | Lina, a developer on a team of 8 | Projects, progress updates, links to GitHub Issues, employer-approved sign-in |

The same person is often all three. Sara may run the book club and also work on Lina's team, and she should see all of it in one app.

## Core concepts

Everything in the app is organised as spaces, which hold projects and lists, which hold tasks. My Day sits across all of them.

| Concept | What it is | Example |
| --- | --- | --- |
| Space | A separate area of your life with its own members and rules. Every user has a private Personal space. | Personal; Book Club; Acme Corp |
| Project | A goal with a start and an end, grouping related tasks. | Renovate kitchen; Q1 release |
| List | An ongoing collection with no end date. | Groceries; Reading list |
| Task | One thing to do, with an optional planned date, due date, status, priority, tags, notes and assignee. | Book the plumber |
| Subtask | A smaller step inside a task. A task can show progress such as 2 of 5 done. | Get three quotes |
| Update (diary) | A dated note on a task recording what happened, kept as a running log. | Called plumber, visit on Thursday |
| My Day | Your personal plan for today, picked from any space. Only you see it. | 5 tasks from three spaces |
| Connection | A link to an outside system whose items appear as tasks. | GitHub repository acme/web |

Two rules keep this clear. A task always belongs to exactly one space. Personal choices, such as adding a task to My Day, snoozing it or setting your own reminder, belong to you and never change the task for anyone else.

## Features

The first release covers capturing, organising and completing tasks well; sharing and connections build on top of that.

### Tasks and subtasks

- Add a task in one step from anywhere in the app, or with a keyboard shortcut on desktop.
- Give tasks a planned date (when you intend to work on it) and a due date (when it must be done), plus a reminder, priority, tags, notes and attachments.
- Move tasks through states: Not started, In progress, Waiting on someone, Needs review, Done or Cancelled. A space can add its own states, such as In review.
- Break tasks into subtasks, and see progress on the parent task.
- Repeat tasks on a schedule, such as every Monday or the first of each month (see Repeating tasks).
- Use templates to create a task with its usual subtasks in one step, such as a monthly book club meeting.
- Mark tasks done, undo it, and find completed tasks later.

Tasks, subtasks, states and templates are part of phase 1. A space's own states arrive with sharing in phase 3.

### Notes, Markdown and attachments

Task notes, diary updates, comments and scratch pad notes are all written in Markdown, and any of them can hold files and images.

- **Full Markdown:** headings, bold, italic, strikethrough, bullet and numbered lists, checklists, links, quotes, inline code, code blocks with highlighting, and tables. The app follows GitHub-flavoured Markdown, so text moves cleanly to and from GitHub Issues.
- **Write your way:** type Markdown directly, or use the formatting bar and keyboard shortcuts without knowing any Markdown. Switch between editing and a formatted preview. Rich text pasted from a web page or a document is converted to Markdown.
- **Checklists to subtasks:** turn a checklist in the notes into subtasks in one step.
- **Link things together:** type \[\[ to link to another task or a scratch pad item.
- **Attach any file:** drag and drop, paste, use the share menu, or take a photo or scan a document with the phone camera.
- **See it in place:** images appear inside the notes where they are placed. PDFs, videos and audio show a preview, and other files show their name, type and size.
- **Offline:** attachments are stored on the device. In synced and shared spaces, large files download when first opened, and you can keep chosen lists fully available offline.
- **Shared safely:** attachments follow the permissions of their task or pad. Files sent to the online service are encrypted and checked for known malware.
- **Across connections:** Markdown and images in GitHub issue descriptions and comments stay intact in both directions. Notes are converted to Org markup for the Org files connection.
- **Agents:** agents can read and write Markdown notes and attach files, such as logs or screenshots, within their permissions.
- **Export:** exports keep notes as Markdown files, with attachments in a folder beside them.

Markdown notes are part of phase 1. Attachments arrive in phase 2, together with syncing them across devices, and sharing them with phase 3.

### Tags

Any task can carry as many tags as you like, so you can group tasks across lists, projects and spaces by context, such as errands, phone, waiting or urgent.

- **Add tags anywhere:** type # in quick add, pick from the task's tag field, or drag a task onto a tag in the sidebar. Subtasks and scratch pad items can be tagged too.
- **Suggestions:** as you type, the app suggests tags you already use, so errand and errands don't both appear.
- **Colours and groups:** give a tag a colour, and nest tags in groups, such as home/garden and home/kitchen. Filtering by a group includes everything inside it.
- **Personal and shared tags:** tags in a shared space are visible to its members, and an owner can set the space's list of tags. Your Personal tags stay private, even on shared tasks you can see.
- **Manage in one place:** rename, merge or delete a tag once, and every task that uses it updates.
- **Use tags everywhere:** filter any view by tags, build saved filters such as #work and #urgent, group the Kanban board by tag, and see each tag as a list of its own in the sidebar.
- **Kept in step with other systems:** tags match GitHub labels and Org mode tags through their connections.
- **Agents can tag:** an agent can add tags to tasks it creates or works on, within the tags its space allows.

Tags are part of phase 1. Shared tags arrive with sharing in phase 3.

### Repeating tasks

A repeating task comes back on a schedule, so regular chores, bills and meetings never need to be entered twice.

- **Any pattern:** daily, weekdays, weekly on chosen days, every few days, weeks or months, monthly on a date or on a day such as the last Friday, yearly, or a custom pattern.
- **From the schedule or from completion:** repeat on fixed dates, such as rent on the 1st, or a set time after you last finished it, such as a haircut 6 weeks after the last one.
- **What carries over:** the next occurrence keeps the title, notes, tags, subtasks (all unticked), reminders and assignee. Each occurrence has its own diary updates.
- **When it ends:** never, after a number of times, or on a date.
- **Change one or all:** skip or move a single occurrence without changing the series, or edit this and all future occurrences.
- **No pile-up:** if you miss occurrences, only the next one is shown, marked as overdue, rather than a stack of copies.
- **See what's coming:** future occurrences appear in the calendar, and My Day suggests the ones due today.
- **History:** the task shows when each past occurrence was completed or skipped.
- **Take turns:** in a shared space, a repeating task can rotate between members, such as a different book club host each month.
- **Quick add and Org mode:** type every Monday, every other week or every 3 months after done in quick add. Org mode repeaters map to the same patterns.

Repeating tasks are part of phase 1. Rotating between members arrives with sharing in phase 3.

### Quick add in plain language

Type a task the way you would say it, and the app fills in the dates, times and details for you.

| You type | The app creates |
| --- | --- |
| Call plumber tomorrow 3pm | "Call plumber", planned tomorrow at 15:00 |
| Submit report by Friday #work !high | "Submit report", due Friday, tag work, high priority |
| Water plants every Monday | "Water plants", repeating weekly on Monday |
| Read chapter 4 by 15 Dec @book-club /Reading | "Read chapter 4", due 15 December, in the Book Club space, Reading list |
| Dentist next Tue 10:30 remind me 1h before | "Dentist", planned next Tuesday at 10:30, reminder at 9:30 |
| Review PR for 30 min | "Review PR", estimated 30 minutes |

- **What it understands:** dates (today, tomorrow, next Friday, in 3 days, 15 Dec), times (3pm, 15:30), repeats (every Monday, monthly), reminders, time estimates, tags (#), priority (!), space (@), list or project (/), and people to assign in shared spaces (+name).
- **Planned or due:** "by" or "due" sets the due date. Any other date sets the planned date.
- **See it as you type:** recognised parts are highlighted, with a preview of the task. Tap a highlight to turn it back into plain text, for example when "May" is a person and not a month.
- **Paste a list:** pasting several lines creates one task per line, and indented lines become subtasks.
- **Works everywhere:** in the app, in the system share menu, and from a desktop keyboard shortcut. It runs on the device and works offline.
- **Respects your settings:** your time zone, date format and first day of the week.
- **Later:** optional AI help to turn a longer description, such as a pasted email, into a task with subtasks. The user reviews the result before saving.

**Languages.** Quick add understands English, French, Italian, Arabic, Indonesian and Spanish, including each language's date and time words, such as demain, domani, غدًا, besok and mañana. Users can type in any of these languages whatever language the app is set to. Arabic input works right to left and accepts both Arabic-Indic and Western digits.

### Scratch pad

The scratch pad is a free space to drop thoughts, sketches and bits of information without turning them into tasks first.

- **Drop anything:** notes written in Markdown, pasted text, links, photos, screenshots, files of any type and voice notes. Images show in place on the pad.
- **Doodle:** draw with a finger, a pen or a mouse, such as a quick floor plan or a diagram from a meeting.
- **No forms:** nothing on the pad needs a title, date or list. It is saved as soon as you type or draw.
- **Turn ideas into tasks when ready:** select a line, a few lines or part of a drawing and choose Make task. Quick add reads the selected text for dates, tags and people, and the task keeps a link back to its spot on the pad.
- **Several pads:** every space has its own pad, so a book club brainstorm stays separate from work notes. Your Personal pad is always one tap away, including from the share menu and a desktop shortcut.
- **Shared pads:** in a shared space, members can write and draw on the pad together and see each other's changes live.
- **Keep it tidy:** a weekly prompt suggests reviewing the pad, and old items can be archived in one step. Nothing is deleted without asking.
- **Find it later:** search covers typed text on the pad. Recognising handwriting comes later.
- **Agents:** with permission, an agent can read a pad and propose tasks from it. The proposals wait for a person to accept them.
- **Works offline**, and exports with everything else, with notes as Org or Markdown and drawings as images.

Typed notes on the personal scratch pad are part of phase 1. Drawing, photos and voice notes arrive in phase 2, shared pads in phase 3, and handwriting search in phase 4.

### Updates and diary

- Add dated updates to any task to record progress, decisions or blockers.
- See the full story of a task in time order: who changed what, and every update.
- In shared spaces, everyone with access sees the updates, so nobody has to ask what happened.

Updates and the diary are part of phase 1. Seeing other members' updates arrives with sharing in phase 3.

### Projects and lists

- Group tasks into projects with an optional deadline and a progress view.
- Keep open-ended lists for things that never finish.
- Reorder tasks by dragging, and group them into sections such as To do, Doing and Done.

Projects and lists are part of phase 1.

### My Day

- Each morning, start with an empty My Day and pick what to work on.
- Get suggestions: tasks planned for today, tasks due soon or overdue, and tasks you planned but did not finish yesterday.
- Add tasks from any space, including work and connected systems, into one plan.
- My Day is private. Adding a shared task to it does not notify or affect anyone else.

My Day is part of phase 1.

### My Week

My Week is a short weekly planning ritual that turns everything on your plate into a realistic plan for the coming days, which My Day then follows each morning.

1. **Look back.** See last week's summary: what you finished, what slipped, and how your week goals went.
2. **Carry over or let go.** For each unfinished task, move it into this week, reschedule it for later, or drop it.
3. **Set the week's goals.** Choose two or three goals, optionally linked to projects, that would make the week a success.
4. **Plan the days.** Drag tasks onto days. Each day shows its estimated work against the hours you have, with meetings from connected calendars taken off, and warns when a day is overloaded.
5. **Confirm.** The plan is saved, and each day's tasks are ready for My Day.

- **Your rhythm:** choose the planning day and time, such as Sunday evening or Monday morning. A guided session takes about 10 minutes.
- **Week view:** the week's goals at the top, one column per day below, and progress towards each goal.
- **Everything in one plan:** tasks from every space, shared deadlines and connected GitHub issues sit in the same week. The plan itself is private.
- **Work and personal hours:** set your available hours per day, and optionally separate work hours, so work tasks land in work time.
- **During the week:** each morning My Day starts from that day's planned tasks. Unfinished tasks are offered for the next day, and a midweek check-in suggests adjustments if the week is falling behind.
- **Closing the loop:** the daily summary shows progress against the week, and the weekly summary feeds next week's look back.
- **AI help:** with your own AI key, the app can draft a week plan from your deadlines, estimates and goals for you to adjust.

My Week is part of phase 1. Meetings from connected calendars and AI-drafted plans arrive with phase 2.

### Daily summary

At the end of each day, the app writes a short summary of how your day went, both for you and for the spaces and projects you belong to, using an AI service you connect with your own key.

**Your day**

- What you completed, grouped by space and project, and how far each project moved.
- What you planned but did not finish, with one tap to carry it over to tomorrow.
- What is waiting on someone, and what is due tomorrow.
- Updates you wrote in task diaries, work agents finished for you, and time tracked.
- A suggested start for tomorrow's My Day.

**Your spaces and projects**

- For each space and project you belong to: what others completed, what is new, what became blocked, and deadlines coming up.
- Only tasks you are allowed to see are included.

**How you get it**

- A notification at a time you choose, opening a summary page. Past summaries are kept as a journal you can scroll back through.
- A weekly summary on a day you choose.
- In a work or social space, you can post your summary as a standup update. You review and edit it first; nothing is posted automatically.
- Summaries are written in your language.

**Bring your own AI key**

- Connect an AI service of your choice by entering your own key, for example DeepSeek, OpenAI, Anthropic or Google, or use an AI model running on your own device.
- Without a key, you still get a plain summary built from lists and counts, without the written narrative.
- The key is stored securely on your device and is never shared with other members. The AI service bills its usage to the key's owner.
- The same key powers other AI features, such as AI task drafting.

**Privacy**

- The app shows which AI service receives your data, and you choose which spaces are included in summaries.
- In work spaces, the organisation decides which AI services are allowed, and can supply its own key. If it blocks personal keys, work tasks are left out of AI summaries written with them.
- With a model on your own device, nothing leaves the device.

The plain summary is part of phase 1. AI-written summaries with your own key arrive with phase 2, space and project summaries with phase 3, and organisation-managed keys with phase 4.

### Calendar

The calendar shows everything you have to do laid out over days, weeks and months, so you can see busy and free periods at a glance and plan ahead.

- **Views:** day, week, month, and an agenda list of upcoming days.
- **What it shows:** tasks on their planned date and on their due date, marked differently so a deadline stands out from a plan. Repeating tasks appear on every date they recur.
- **Time blocks:** tasks with a time and an estimate fill that slot, as in a diary. Tasks without a time sit at the top of the day.
- **Plan by dragging:** drag a task to another day or time to change its planned date. A side panel lists unscheduled tasks to drag into the calendar.
- **Spot overload:** each day shows the total estimated time of its tasks and warns when it is more than the hours you have set as available.
- **Filter:** show all spaces together, or only some, and filter by tag, list, project or person.
- **Shared calendars:** every shared space has its own calendar, such as book club meetings or a team's release deadlines. Members see the same dates; each person's own plans stay private.
- **Your other calendars:** with a calendar connection, meetings from Google Calendar (see Google Calendar), and later Outlook and Apple Calendar, appear alongside tasks, read-only, so you plan around them.
- **Subscribe from elsewhere:** a private link lets you see your tasks inside Outlook, Google Calendar or Apple Calendar. The link is served by the online service, so it needs an account. You can turn the link off or replace it at any time.
- **Works offline:** the calendar uses the data on the device, like the rest of the app.

The calendar arrives in phase 2, together with showing other calendars and subscribing from them.

### Kanban board

The board shows a list or project as cards in columns, so you can see at a glance what is waiting, moving and finished.

- **Columns by choice:** status by default (Not started, In progress, Waiting on someone, Needs review, Done), or sections, assignee, priority or tag.
- **Move by dragging:** dragging a card to another column changes that field. Moving a card to Done completes the task.
- **Cards show the essentials:** title, due date, assignee, tags, subtask progress, and an icon for tasks from GitHub Issues or agents.
- **Swimlanes:** split the board into rows, for example one per person or per project.
- **Work-in-progress limits:** a column can warn when it holds more cards than its limit, such as no more than 3 in progress.
- **Shared boards:** in shared spaces everyone sees cards move live. Moving a connected task updates the GitHub issue.
- **Any list, any time:** the board is another way to view the same tasks. Switching between list, board, calendar and timeline changes nothing about the tasks.

The Kanban board arrives in phase 2. Shared boards arrive with sharing in phase 3.

### Timeline (Gantt chart)

The timeline shows a project's tasks as bars across time, so you can see how long work takes, what depends on what, and whether the project will finish on time.

- **Bars from dates:** each bar runs from a task's planned date to its due date. Tasks with only a due date show as a milestone.
- **Dependencies:** link tasks so one must finish before another starts, such as get quotes before book the plumber. Links show as arrows.
- **Plan by dragging:** move or stretch a bar to change its dates. Tasks that depend on it can shift with it, after the app shows what will move.
- **Warnings:** the timeline flags tasks that would start before the work they depend on finishes, and tasks that push the project past its deadline.
- **Zoom and group:** view by day, week, month or quarter, and group by project, section or person, with a line marking today.
- **Progress:** each bar fills as its subtasks are completed.
- **Share and export:** shared projects show the same timeline to all members, and it can be exported as an image or PDF for a meeting.

The timeline, with dependencies, arrives in phase 4.

### Finding things

- Search across all spaces by words, tags, people and dates.
- Built-in views: Today, Upcoming, Overdue, Assigned to me and Recently completed.
- Save any search or filter as a custom view, such as all high-priority work tasks due this week.

Search and the built-in views are part of phase 1. Saving a search as a custom view arrives in phase 2 (see Custom views).

### Custom views

A custom view is a saved, live list of exactly the tasks you want to follow, chosen by any of their attributes, including fields that come from GitHub Issues and other connections.

| Example view | Conditions | Shown as |
| --- | --- | --- |
| My open GitHub bugs | From GitHub, repository acme/web, label bug, assigned to me, not done | List, sorted by priority |
| Release 2.1 features | From GitHub, milestone 2.1, label feature | Board, columns by status |
| Waiting on others | Status Waiting on someone, in any space | List, grouped by person |
| Ready for agents | Marked Ready for agent, label bug, no linked pull request | List, oldest first |
| Book club this month | Space Book Club, due this month | Calendar |

- **Any attribute:** space, list, project, status, tags, priority, assignee, planned and due dates (including relative ones such as next 7 days), created or changed dates, estimate, scratch pad links, and where a task came from (the app, GitHub, Org files or an agent).
- **Connection fields:** for GitHub, repository, labels, milestone, issue type, open or closed, author, linked pull requests and their review state. Each future connection adds its own fields the same way.
- **Combine conditions:** match all or any conditions, and nest groups, such as label bug or label regression, and assigned to me. Words in the title or notes can be a condition too.
- **Choose the layout:** list, table, board, calendar or timeline, with grouping, sorting and, for tables, the columns to show. Tasks can be edited directly in the table.
- **Always up to date:** a view changes as tasks change, including when a GitHub issue is labelled, closed or reassigned.
- **Keep it handy:** pin views to the sidebar with a name, icon and colour, see a count of matching tasks, and add a view's tasks to My Day.
- **Get alerted:** optionally be notified when a new task enters a view, such as any issue labelled urgent.
- **Add tasks in context:** a task created inside a view gets the view's attributes filled in, such as the label feature and milestone 2.1.
- **Share or keep private:** views are private unless shared with a space, where members see the same view.
- **Work queues for agents:** an agent can be pointed at a view and pick its tasks from there.
- **Export:** download a view's tasks as a spreadsheet file.

Phase 1 has the built-in views, such as Today, Upcoming and Overdue. Custom views and their alerts arrive in phase 2, and GitHub fields and shared views with phase 3.

### History and undo

- Undo recent changes, including accidental deletion.
- See how a task looked at an earlier date.

Undo is part of phase 1. Seeing a task at an earlier date arrives in phase 2, together with sync, which keeps the history of merged changes.

### Reminders and notifications

- **Flexible timing:** set one or more reminders per task, at a fixed time or relative to its planned or due date, such as 1 day before and 1 hour before.
- **Place reminders:** on phones, remind me when I arrive at or leave a place, such as the supermarket. Arrives in phase 2.
- **Act from the notification:** mark the task done, snooze it (10 minutes, 1 hour, tomorrow, or a chosen time), or open it.
- **Every device, once:** reminders appear on all your devices, and dismissing one clears it everywhere.
- **Insistent reminders:** for important tasks, the reminder repeats until you respond.
- **Daily summary:** a morning summary of what is planned and due today, at a time you choose, and an evening summary of how the day went (see Daily summary).
- **Reminders are personal:** a reminder on a shared task alerts only you. A space owner can set a default reminder for deadlines, which members can turn off.
- **Works offline:** reminders fire from the device, without internet.
- **Shared-space notifications:** choose which events notify you: assigned to me, mentioned, due soon, or nothing.
- **Quiet hours,** so work notifications stay out of your evenings.

Reminders on the device, insistent reminders and quiet hours are part of phase 1. Place reminders and clearing a reminder on every device arrive with sync in phase 2. Shared-space notifications and default reminders set by an owner arrive with sharing in phase 3.

### Time tracking

- Start and stop a timer on any task, or log time afterwards.
- See time spent per task, project and week.
- Off by default. In work spaces, the organisation decides whether it is available.

Time tracking arrives in phase 4. Until then, the daily summary and the Org files connection leave time tracked out.

## How it works

The app keeps all your data on your own device first, and only uses the online service when you choose to sync or share.

### Starting without an account

- Install the app and start adding tasks immediately. No email, no password, no internet needed.
- Everything is saved on the device and works fully offline.
- The app suggests creating an account only when it adds value: syncing a second device, or sharing a list.

### Creating an account later

- Sign in with an email, or with an existing Microsoft, Google, Apple or GitHub account.
- Everything already on the device moves into the account. Nothing is lost or retyped.
- Users can stay offline-only forever if they prefer.

### Syncing devices

- Changes made on one device appear on the others within seconds when online.
- Offline changes are kept and sent when the connection returns.
- If two devices change the same task while offline, the app merges the changes automatically. Different fields simply combine; for the same field, the latest change wins and the earlier value stays in the task's history.
- The app always shows whether everything is synced, and never blocks you while it syncs.

### Backups and moving data

- The app keeps automatic local backups.
- Export everything at any time to standard formats, including Org and Markdown plain text, and import from common to-do apps.
- Deleting your account deletes your data from the online service.

Using the app without an account, local backups and export are part of phase 1. Accounts and syncing devices arrive in phase 2, and importing from other to-do apps arrives with their connections.

## Sharing and collaboration

You share a whole space, project or list with people, and each person gets a role that decides what they can do.

| Role | Can view | Can add and edit tasks | Can invite people | Can change settings and delete |
| --- | --- | --- | --- | --- |
| Owner | Yes | Yes | Yes | Yes |
| Editor | Yes | Yes | No | No |
| Viewer | Yes | No, but can comment | No | No |

- **Inviting people.** Send an invite by email or as a link. Links can expire and can be turned off at any time.
- **Moving private work into a shared space.** Move a list or project from your Personal space into a shared one. It keeps all its tasks, subtasks and updates.
- **Assigning work.** Assign a task to one person, or to several when each must complete it (for example, everyone reads chapter 4).
- **Comments and mentions.** Discuss a task in its updates and mention people to notify them.
- **Live changes.** When someone else edits a shared list, you see the change appear without refreshing.
- **Leaving.** Members can leave a space at any time. Their past updates stay so the history makes sense, under their name.

Sharing arrives in phase 3.

### Delegating tasks and following up

You can hand a task in a project to another member, keep track of it without doing it yourself, and get reminded to follow up so nothing you delegated is forgotten.

**Delegating**

1. Choose Delegate on a task in a shared project and pick a project member.
2. Add a short message, a due date, and what done looks like.
3. The task appears in the other person's lists and notifications. They can accept it, ask a question, or decline with a reason, which returns it to you.
4. You stay on the task as the person who delegated it, see every update in its diary, and are told when it is done. You can review the result before closing it, or let it close directly.

**Following up**

- **Set a follow-up when you delegate:** on a date, a set time later (in 3 days), before the due date (2 days before), or when nothing has happened for a while (no update in 5 days).
- **Follow-ups land in My Day** as their own items, with one-tap actions: send a nudge, open the task, take it back, or hand it to someone else.
- **Friendly nudges:** a nudge is a short prewritten message you can edit before sending. It reaches the other person as a notification and is recorded in the task's diary. Nudges respect their quiet hours and are limited to avoid nagging.
- **Delegated by me view:** every task you have handed off, grouped by person, showing status, due date and the next follow-up. Overdue items are highlighted.
- **In your planning:** delegated tasks and upcoming follow-ups appear in the My Week review and in your daily summary.

**Waiting on people outside the app**

You can also mark any task, even in your Personal space, as Waiting on a named person who doesn't use the app, such as the plumber, and set the same follow-up reminders. Nothing is sent to them; the reminder is only for you.

**Quick add:** type +name to delegate and follow up Friday to set a follow-up, for example Send venue quote +Omar follow up Friday.

Waiting on outside people, with follow-up reminders, is part of phase 1. Delegating to members arrives with sharing in phase 3. Delegating to an AI agent works the same way and arrives with agents in phase 3.

Shared data works offline too. You can edit a shared list on a plane, and your changes join everyone else's when you land.

### Shared dashboards for outside people

A project owner can publish a read-only dashboard at a web link, protected by a simple password, so clients, partners or family can follow progress without an account or the app.

1. Open a project, list or custom view and choose Share dashboard.
2. Pick what the dashboard shows and set a password.
3. Send the link, and give the password separately, for example by phone or message.
4. Visitors open the link in any browser, enter the password, and see the dashboard.

- **What it can show:** overall progress (percentage done, tasks by status), milestones, the board, list, calendar or timeline, and recent updates from the diary.
- **Private by default:** notes, comments, attachments and people's names are hidden unless the owner turns them on. Personal items, such as My Day and private tags, never appear.
- **Ad hoc and live:** create a dashboard in a minute for a single meeting or client. It updates as tasks change and shows when it was last updated.
- **Read-only:** visitors can look but cannot change anything.
- **Simple protection:** the link is long and hard to guess, and the password is required on top. After several wrong passwords, the link pauses for a while. Search engines never list dashboards.
- **Owner control:** set an expiry date, change the password, or turn the link off at any time. The owner sees how often the dashboard was opened and when.
- **Organisation rules:** organisations can block outside dashboards in work spaces, or require a password and a maximum expiry.
- **Clear limits:** the app explains that a shared password is weaker than accounts, and suggests inviting people as viewers for anything sensitive.

Shared dashboards are mostly used with clients and partners, so they arrive with the work features in phase 4.

## AI agents

AI agents, such as coding assistants or personal assistants, can add tasks, pick up tasks to work on, report progress and hand results back, under the same rules as people.

### What an agent can do

1. **Add tasks.** An assistant turns a meeting summary or an email into tasks in the right list.
2. **Find work.** It lists tasks marked Ready for agent, or assigned to it, that it has access to.
3. **Claim a task.** The task shows as in progress and claimed by that agent, so no person or other agent starts it at the same time. If the agent goes quiet for longer than the space allows, the claim expires and the task returns to the queue.
4. **Report progress.** It writes updates to the task's diary and can add subtasks for its own steps.
5. **Ask for help.** It sets the task to Waiting on someone, asks its question, and mentions the person who can answer.
6. **Hand back.** It sets the task to Needs review with a summary and links to its results, such as a pull request. A person then marks it done. A space can let agents close simple tasks themselves.

### Handing work to agents

- Mark a task Ready for agent, or assign it to a specific agent.
- Write what done looks like in the task notes, so the agent and the reviewer judge the result the same way.
- An Agent work view shows tasks waiting for agents, in progress, and waiting for review.
- Tasks waiting for your review can appear in My Day.
- Agent work combines with connections: an agent can pick up a task that came from GitHub Issues, and its progress flows back to the issue.

### Agents as members

- An agent joins a space like a person, with a name, an owner who is responsible for it, and a role.
- The Agent role can add, claim, update and hand back tasks. It cannot invite people, change settings, delete anything permanently, or change its own access.
- Access can be limited to chosen lists or projects.
- Every change an agent makes is labelled with the agent and its owner in the task history.
- The owner can pause or remove an agent at once, and undo its recent changes.
- In the Personal space, agent access is off until the user turns it on for specific lists.
- Organisations decide whether agents are allowed in their work spaces, and which ones.

### Keeping agents safe

- Task text written by other people is information for the agent, never instructions that widen what it may do.
- Limits on how many tasks an agent can add or claim per hour stop runaway agents.
- Agents cannot connect outside systems or see sign-ins to them.

### How agents connect

- Agents connect through the Model Context Protocol (MCP), an open standard that many AI assistants support, including Claude.
- An agent on the same device can work with the offline app, with no account or internet needed.
- Agents in shared and work spaces connect through the online service, with the same permissions checks as people.

Agents arrive in phase 3, both on the same device and in shared spaces. Organisation controls for agents arrive in phase 4.

## Connecting to other systems

Connections show items from outside systems, starting with GitHub Issues, as tasks in the app, and keep both sides in step so nobody has to update two places.

### How a connection works

1. A space owner connects the space to GitHub once, approving which repositories the space can reach. Members need no setup of their own.
2. The owner chooses what to bring in, for example all issues in the repository acme/web, or only issues with the label book-club. Each member can then narrow it down with custom views, such as issues assigned to me.
3. Matching items appear as tasks in a list or project they choose, marked with the source's icon and a link back to the original.
4. Changes flow both ways. Closing the task closes the issue; a new comment on the issue appears as an update on the task.
5. A space owner can pause or remove the connection at any time. Removing it keeps the tasks as ordinary tasks, with a note of where they came from.

### GitHub Issues: what maps to what

| In GitHub | In the app | Direction |
| --- | --- | --- |
| Issue title and description | Task title and notes | Both ways |
| Open or closed | Not done or done | Both ways |
| Assignees | Assignees, matched by GitHub account | Both ways |
| Labels | Tags | Both ways |
| Milestone and due date | Project and due date | GitHub to app |
| Comments | Updates in the task's diary | Both ways |
| Task lists inside the issue (checkboxes) | Subtasks | Both ways |
| Pull requests linked to the issue | Links shown on the task | GitHub to app |

### Rules for connected tasks

- **The outside system stays the source of truth.** If both sides change the same field at once, the outside system's value wins and the app keeps the other value in the task's history.
- **Personal extras stay personal.** Adding a connected task to My Day, setting your own reminder or snoozing it never changes the GitHub issue.
- **App-only details stay in the app** unless the user chooses to send them, such as private notes or a personal priority.
- **One connection per space.** Everyone in the space sees the issues the connection brings in, so owners choose repositories with the members in mind. The app warns when a private repository is connected to a space that includes people outside the organisation. Only owners manage the connection, and members never see its sign-in details. Changes made in the app show in GitHub under the member's own name if they have linked their GitHub account, and otherwise as made through the app on their behalf.
- **Works offline.** Changes to connected tasks made offline are sent to GitHub when the device reconnects.
- **Clear status.** A connected task shows when it was last in step, and explains plainly if something failed, such as an expired sign-in.

The GitHub Issues connection arrives in phase 3.

### Org mode files

A connection to a folder of Org files lets Emacs users keep their plain-text files as the source of truth, while using the app on mobile and for sharing.

| In Org mode | In the app | Direction |
| --- | --- | --- |
| Heading with a TODO state | Task, with matching status | Both ways |
| Nested headings | Subtasks | Both ways |
| SCHEDULED date | Planned date | Both ways |
| DEADLINE date | Due date | Both ways |
| Tags | Tags | Both ways |
| LOGBOOK notes | Updates in the task's diary | Both ways |
| Clock entries | Time tracked | Both ways |

- Changes made in the app are written back into the files, keeping the user's formatting and any content the app does not understand.
- The folder can be anywhere the device can read, such as a git repository or a synced folder.
- This connection needs no account or server, so it works in the offline-only app.

The Org files connection arrives in phase 3, with the other connections.

### Google Calendar

A connection to Google Calendar puts your meetings next to your tasks, so My Week and My Day plan around the time you actually have. Unlike GitHub, it is a personal connection: it belongs to you, not to a space, and nobody else sees your calendar through it.

1. Choose Connect Google Calendar in settings and sign in with Google. You can connect several Google accounts, such as a personal Gmail and a work Google Workspace account.
2. Pick which calendars to show, including calendars shared with you such as a family calendar, and give each a colour.
3. Choose which calendars count as busy time when the app works out the hours you have each day.

| In Google Calendar | In the app | Direction |
| --- | --- | --- |
| Event title, time, location and video link | Meeting in the calendar, My Day and My Week, read-only | Google to app |
| Busy or free | Taken off, or not, from the day's available hours | Google to app |
| All-day event | Shown at the top of the day; reduces available hours only if marked busy | Google to app |
| Repeating event | Every occurrence | Google to app |
| Declined event | Hidden | Google to app |
| Task with a planned time and estimate | Time block event in the app's own calendar (optional) | Both ways |

- **Planning around meetings:** My Week takes busy time off each day's hours. My Day lists today's meetings in order alongside tasks, and warns when the planned tasks don't fit the free time between them.
- **Tasks from events:** turn an event into a task, such as prepare slides for Thursday's review, planned before the event and linked back to it.
- **Time blocking (optional):** send tasks with a planned time and estimate to a separate calendar the app creates, so others see you as busy. Moving the event in Google moves the task's planned time. You choose whether the event shows the task title or only Busy. The app never writes to any other calendar.
- **Private to you:** events never appear in shared space calendars, outside dashboards or other people's summaries. Your own daily summary includes meetings only if you allow it, and event details go to an AI service under the same choices as your tasks.
- **No account needed:** the device talks to Google directly, so the connection works in the offline-only app. The coming weeks of events are kept on the device, so the calendar still shows them offline. With an account, the connection follows you to your other devices.
- **Kept fresh:** changes in Google appear within a few minutes while online. The app shows when the calendar was last updated, and explains plainly if something failed, such as an expired sign-in.
- **Least access:** the app asks only to read events, plus to manage its own calendar when time blocking is on. Disconnecting removes stored events from all your devices and offers to delete the app's calendar.
- **Work accounts:** organisations can allow or block calendar connections for work spaces, and a Google Workspace admin's rules for outside apps still apply.

Showing Google Calendar meetings arrives in phase 2, with the calendar view and meetings in My Week. Time blocking arrives in phase 3, with the other two-way connections.

### Other systems planned

The same connection model extends to other tools. Likely next candidates, in order of expected demand:

- Work trackers: Jira, Azure DevOps, Linear, GitLab Issues.
- Personal to-do apps: Microsoft To Do, Google Tasks, Todoist, mainly for moving in.
- Calendars: Outlook and Apple Calendar, working the same way as Google Calendar.

An open connection kit lets other developers add connections for tools the team doesn't build.

## Privacy, security and separation

Your personal data is yours, your employer's data is your employer's, and the app never mixes them up.

### Keeping work and personal life apart

- A work space belongs to the organisation, not to the person. If someone leaves the company, the organisation keeps its tasks and the person keeps their personal ones.
- The organisation can see and manage its own work spaces only. It can never see a member's Personal space, social spaces or My Day.
- Moving or copying tasks out of a work space can be blocked by the organisation.
- My Day shows work and personal tasks together on your screen, but each task stays stored in its own space.

### Protecting data

- Data on the device is protected by the device's own security, with an optional app lock.
- Data sent to and stored on the online service is encrypted.
- Every shared space checks permissions on the server, not only in the app.
- Sign-ins to outside systems such as GitHub are stored securely and can be revoked by the user at any time.

### For organisations

- Sign in with the company's existing identity system, such as Microsoft Entra ID or Google Workspace.
- Admins manage members, roles and which connections are allowed.
- An activity log of who changed what in work spaces.
- Choice of the region where the organisation's data is stored, where needed.

Organisation features arrive in phase 4.

### Choices for users

- Use the app without an account and without sending anything to the online service.
- Export or delete all your data at any time.
- No advertising, and no selling of user data.

## Release plan

The app ships in four phases, each usable on its own, so real use shapes the next phase.

| Phase | Focus | What it includes |
| --- | --- | --- |
| 1 | One person's day and week, on one device | Tasks, subtasks, templates and repeating tasks; Markdown notes; tags; quick add; typed notes on the personal scratch pad; projects and lists; My Day and My Week; the plain daily summary; search and built-in views; reminders on the device; waiting on outside people with follow-ups; undo; local backups and export; no account needed |
| 2 | Sync, richer views and AI help | Accounts and sync; attachments; drawing, photos and voice notes on the pad; the calendar, subscription link and Google Calendar meetings; the Kanban board; custom views and alerts; AI summaries and drafted week plans with your own key; place reminders; a task's earlier versions |
| 3 | Other people and other systems | Sharing, roles and comments; shared tags, pads and boards; rotating repeating tasks; delegating to members; space and project summaries; GitHub Issues, including GitHub fields and shared custom views; Org files; Google Calendar time blocking; AI agents |
| 4 | Organisations and their clients | The timeline with dependencies; time tracking; shared dashboards for outside people; organisation sign-in, admin and activity log; data region; organisation-managed AI keys; organisation controls for agents; handwriting search |

Each phase must prove itself in real use before the next one starts; the exact gate for each move is still to be agreed (see Open questions).

Phase 1 follows the guiding principle: it holds exactly what one person needs to plan and finish their day and week, so it reaches real use quickly. Phase 2 adds sync, richer views, attachments and AI help for when tasks pile up, plus Google Calendar meetings so My Week can plan around them. Phase 3 brings everything else that involves other people or other systems: sharing, delegation, GitHub Issues and AI agents. Phase 4 holds features mainly wanted by organisations and their clients, such as the timeline, time tracking and outside dashboards. Dates will be set once phase 1 scope is confirmed.

## Open questions

- [ ] Product name and brand.
- [ ] Which devices come first: Windows, macOS, Linux, iOS, Android, or the web?
- [ ] Business model: free personal use with paid sync and sharing, a paid work tier, or both?
- [ ] What must each phase show in real use before the next one starts?
- [ ] Should shared social spaces support end-to-end encryption, given that it limits search and smart notifications?
- [ ] Which work tracker comes after GitHub Issues?
- [ ] Does My Day suggest tasks automatically, or only on request?
- [ ] Which Org features must survive a round trip without loss: properties, drawers, links, tables?
- [ ] In what order should the quick add languages ship after English?
- [ ] Can agents hand tasks to other agents, and how long can such a chain run before a person must review it?
- [ ] Which AI assistants should be tested first with the agent features?
- [ ] What size limits apply to attachments, and how much storage does each plan include?
- [ ] Should dashboard visitors be able to leave comments or subscribe to email updates?
- [ ] Google reviews apps before the public can grant them calendar access, which can take weeks. When does that review start so phase 2 is not held up?
- [ ] Should the app also offer a built-in AI option, for example in a paid plan, for people who don't have their own key?

### Decided

- [x] GitHub connects per space (see Connecting to other systems).
- [x] Google Calendar connects per person, not per space (see Google Calendar).
