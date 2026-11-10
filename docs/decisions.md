# Architecture/implementation decisions

Date-ordered decisions that resolve PLAN/SPEC omissions during implementation. Product
authority stays with `SPEC.md`; PLAN.md pins stay binding.

## 2026-11-09

- **Roslynk is not used for C# editing.** The user directed ("Skip using roslynk") that the
  Roslynk MCP/CLI editing workflow (PLAN §3) be skipped in favor of this session's normal
  file-edit tools. All other PLAN §3 rules still apply: diagnostics via builds/tests,
  Slopwatch after C# batches, full builds for release gates.
- **`frontend/.npmrc` sets `install-links=true`.** The template declares the generated
  `@neoastra/client` (staged under `obj/neoastra/client`) as a `file:` dependency; npm's
  symlink installation requires Windows symlink privileges that restricted environments do
  not grant. Packed-copy installation keeps the plan-required build order and version
  alignment unchanged.
- **`Chaos.slnx` is hand-authored.** The .NET 11 rc SDK's `dotnet sln add` against a
  `.slnx` mis-detects filename collisions across solution folders ("Solution folder 'src'
  already contains..."), so the solution file is maintained directly in the simple
  `<Project Path="..." />` form.
- **Solution-level `dotnet test` fallback.** MTP solution mode downloads a dotnet-test
  extension from `builds.dotnet.microsoft.com` on first use and talks to test
  executables over named pipes; restricted environments may refuse both. `eng/verify.ps1`
  tries the solution run first and falls back to direct TUnit executable runs (each an
  MTP runner), failing on any nonzero-exit or zero-test module (PLAN §16).
- **Task title limits.** Title normalization enforces 500 Unicode scalar values (not
  UTF-16 units) with the limit stated in the error text (PLAN §6).
- **Publishing never touches NuGet lock files.** `eng/publish-windows.ps1` passes
  `-p:RestorePackagesWithLockFile=false`; a `-r win-x64` restore would otherwise write a
  RID group into every `packages.lock.json` and break later `--locked-mode` restores
  (NU1004). Lock files stay stable for the plain/locked restore workflow.
- **Storage failures keep their cause as InnerException.** `SqliteErrorMapper` maps
  SQLite errors to stable `AppErrorKind`s whose messages never contain SQL, paths or
  stack traces; the original exception is attached as `InnerException` so logs and test
  output can still diagnose. Renderer-facing strings are unchanged.
- **Task Position starts at 0 and grows by gaps of 1024.** The SQLite store computes
  `COALESCE(MAX(Position), -1024) + 1024` (first task in a container gets 0); the
  in-memory test fake mirrors this so Application and Infrastructure tests agree.

## 2026-11-10

- **shadcn/ui is the UI foundation.** The user directed that https://ui.shadcn.com/ be
  adopted. The frontend uses Tailwind CSS v4 (`@tailwindcss/vite`) with shadcn/ui
  components copied into `frontend/src/components/ui` (configured by
  `frontend/components.json`, new-york style, lucide icons). Theme tokens live in
  `frontend/src/style.css` (stone base, Chaos orange as `--primary`); dark mode follows
  `prefers-color-scheme`. Form selects use shadcn's `NativeSelect` so pickers stay native,
  keyboard-accessible and testable in jsdom. App-level building blocks shared across views
  (field, badges, banners, deleted-items list, page header) live in
  `frontend/src/components/common.tsx`. Add components with
  `npx shadcn@latest add <name>` from `frontend/`, then check the `cn` import resolves to
  `@/lib/utils`.

## 2026-11-10 (package 3)

- **Tag identity key.** `TagName.Normalize` applies NFKC and then invariant upper- and
  lower-casing, so `Work`, `work` and fullwidth `ｗｏｒｋ` are one identity. The display
  spelling is kept as typed. A single leading `#` is dropped, `/` separates nested
  segments, and nesting stops at 8 levels. Near duplicates such as errand and errands are
  only ever suggested, never merged.
- **Tag links do not bump the task version.** Links are a set: adding one that exists, or
  removing one that does not, changes nothing. The OperationId receipt still guards
  retries. Tag history and undo come with package 4, which wires history into every
  mutation.
- **Search index holds active rows only.** A tombstoned task or subtask loses its search
  document in the same transaction, and restoring it adds the document back. Queries still
  filter deletion and archive state in SQL; finding a match never decides visibility.
  Index text is normalized: NFKD, combining marks removed, tatweel dropped, Arabic-Indic and
  Persian digits made ASCII, then NFKC. Changing that normalization means raising
  `SearchText.NormalizationVersion`; startup then rebuilds the index.
- **One trigger per line in migrations.** The migration runner ends a statement at any line
  that ends in `;`. Trigger bodies (`BEGIN … END;`) are therefore written on one line
  rather than changing how the runner splits statements.
- **Sidebar counts count open tasks.** Every query-service view except Recently completed
  shows open tasks by default. "Include completed" is a temporary filter. That way a
  sidebar count always equals the row total of the view it opens.
- **Counts cross the RPC as 32-bit integers.** NeoAstra RPC refuses `long` unless it is
  serialized as a string. Counts and totals are clamped to `int` at the RPC edge;
  application and store code keeps `long`.

## 2026-11-10 (package 4)

- **History and undo are recorded at the transaction boundary, not per command.**
  `TaskMutationSupport.InWriteTransactionAsync` wraps every write in a
  `RecordingDataSession`. It captures row before-images, tag-link changes and the
  before-aggregate of every task an operation reaches. When the body returns, it writes the
  history entries and the undo entry in the same transaction. New stores or commands get
  history and undo by writing through the session; nothing has to remember to write an
  inverse. A command's `WriteTaskHistoryAsync` call now only declares the event kind and
  fields.
- **Undo restores before-images as new versions.** Version numbers only grow. Undo checks
  every affected row against the version the operation left first, then writes. A diary
  undo appends a revision rather than rewinding the trail. Because restored rows get new
  version numbers, a successful undo re-points older open entries that expected a restored
  row's before-version. That keeps repeated Ctrl+Z exact while any genuinely later edit is
  still a conflict. Link changes carry no versions: undo applies the opposite set operation
  and treats "nothing changed" as a conflict.
- **Snapshots now include tag links.** `TaskAggregateSnapshot` gained `TagIds` and
  `SubtaskTagIds`. Snapshots written before package 4 read back with those as null. A tag
  rename or recolor touches linked tasks, but because snapshots hold tag IDs it produces no
  task history entry. Tag deletion and merges do.
- **Internal links are Markdown links with a `chaos:task/<id>` target.** They resolve by ID
  at render time, so renames keep working and a deleted target renders as a recoverable
  broken link. They stay readable in any Markdown export. The `ContentLinks` table is not
  used yet.
- **External links open through the host.** Rendered Markdown contains no anchors; a link
  is a button that calls `markdown.openExternal`. That call accepts only absolute
  http/https/mailto URLs up to 2,048 characters and opens them in the system browser.
- **Checklist markers are keyed by item text and occurrence.** The editor sends
  `<item text>#<n>` (the nth item with that text in the document) and the host stores a
  SHA-256 of task ID plus key. Editing an item's text makes it a new item. A marker whose
  subtask was deleted (or undone) is replaced on the next conversion.

## 2026-11-10 (package 7)

- **One pure parser, six table-driven packs.** `QuickAddParser` (Domain) matches every
  pack at once over a folded comparison string (lower case, diacritics and Arabic harakat
  removed, tatweel dropped, Arabic letter variants unified, Arabic-Indic/Persian digits
  folded). Each folded character keeps the UTF-16 range it came from, so tokens report
  offsets into the original text, including after emoji and combining marks. Vocabulary is
  written naturally in `LanguagePack` and folded at startup. Mixed-language input works
  because tokens are matched independently and the leftmost, longest match wins; pack order
  (en, fr, it, es, id, ar) only breaks exact ties, such as `!alta` (Italian and Spanish).
- **Phrase precedence.** Recurrence, reminders, follow-up and waiting, due phrases,
  estimates, planned date/time, then sigils. Estimates run before planned times so
  Indonesian "selama 2 jam 15 menit" is never read as 15:00.
- **Conservative recognition.** Month names need a day number (plain "May" stays text).
  Abbreviated weekdays (Tue, mar, dom) need a marker such as next, on, by or every, so
  "sun cream" stays text. Indonesian "minggu" means week; Sunday is "hari Minggu" or
  "Ahad". An hour without am/pm or a marker is not a time; with a marker but no am/pm it is
  read literally on the 24-hour clock and shown in the preview.
- **Nothing recognized is dropped silently.** A token is Applied (fills a field, leaves
  the title) or Kept (stays in the title with a warning): conflicts, unknown lists,
  shared spaces, `+member` delegation, reminders without an anchor.
- **The confirmed draft is saved, not the text.** `capture.create` takes explicit fields
  and never reparses, so a preview made before midnight saves the dates it showed. All
  drafts of a paste commit in one transaction with one receipt and one undo entry; field
  errors carry `drafts[i].field` paths.
- **Waiting and follow-up until package 10.** "waiting on Sam" sets the Waiting status and
  the person and follow-up date are written into the task notes, with a preview note
  saying so. Package 10 adds the `WaitingDetails`/`FollowUps` records.
- **Pad Make task reuses quick add.** The selection's first line becomes the quick-add
  text; the confirmed draft travels in `pad.makeTask` and is created by the same
  `CaptureCommands.CreateInSessionAsync` as quick add, next to the backlink.

## 2026-11-10 (package 10)

- **Notifications are derived rows, synced by the recorder.** Rules, waiting details and
  follow-ups are versioned and undoable. `NotificationJobs` are a projection that is not
  part of undo. After every recorded operation, the jobs of each touched task are
  recomputed in the same transaction, and delivery resyncs a task again before showing
  anything. No command needs its own reminder code, and undo restores the right future
  deliveries simply by restoring the rules and dates they depend on.
- **One table for jobs and deliveries.** The plan's `ReminderDeliveries` and
  `NotificationJobs` map 1:1, so one row holds the desired schedule, the delivery history
  and the OS schedule state. `Generation` changes whenever a source's target or delivery
  instant changes, which is how stale OS callbacks are recognized. `Sequence` counts snoozes
  and insistent repeats of the same target.
- **Offsets are signed.** `ReminderRules.OffsetMinutes` keeps the convention quick add
  already stored: negative is before the anchor (-60 = one hour before).
- **Waiting ends with the Waiting status.** When a task leaves Waiting on someone
  (another status, done, cancelled), its waiting details end and its follow-up is resolved
  inside the same recorded operation, so history keeps both and undo restores them.
  Deletion leaves them as they are; a deleted task simply has no deliveries.
- **Inactivity is computed from task history.** The latest history entry that changed a
  field other than position, reminders, waiting or follow-up counts as activity. Viewing,
  reminders firing and My Day membership never write task history, so they never reset it.
  A logged follow-up is a diary update, which does.
- **Logging a follow-up resolves a one-off rule.** Fixed, after-waiting and before-due
  follow-ups are done once logged. An inactivity follow-up stays and restarts from the log.
- **A follow-up date without a person is a reminder.** Quick add's "follow up Friday"
  with no "waiting on" creates an absolute reminder at the default time rather than an
  anonymous waiting record.
- **Follow-ups in My Day are computed.** They appear as their own section for the day,
  removed by resolving or snoozing, instead of `DayPlanItems` of kind 1, which would need a
  table rebuild for the existing CHECK and unique task index.
- **In-app presenter until the installed package.** `InAppNotificationAdapter` shows due
  notifications inside the running app and reports that it cannot schedule after exit. The
  Windows adapter (MSIX, scheduled toasts, activation) implements the same port once the
  package 0 spike passes on a real desktop. The engine never claims OS delivery it did not get.

## 2026-11-10 (package 11)

- **Facts come from history, not current state.** A task's state at an instant is the
  "before" snapshot of its first history entry at or after that instant, or its current row
  when nothing changed since. Completion events are transitions into Done; restoring a
  deleted finished task is not one. A completion that was later reopened stays an event,
  annotated, and "still done" is counted separately.
- **Facts are captured once.** Each revision stores versioned JSON with the task titles and
  list names of the moment, so a later rename, move or deletion never rewrites the journal.
  Container names are taken at generation time (containers have no history of their own).
- **Three kinds, one table.** Morning (the day ahead), Daily (evening: how the day went) and
  Weekly (seven configured local dates). Period boundaries are half-open local days in the
  profile zone, so a fall-back day is 25 hours.
- **Idempotent generation.** Scheduled and catch-up runs never add a revision to a period
  that has one. "Make it again" adds the next revision and keeps the earlier ones; the
  journal shows the newest. A summary made before its period ends is labelled "through
  HH:mm"; it is not regenerated by itself.
- **Missed periods.** At the next start, missing evening summaries are made for at most the
  seven previous dates (never before the profile existed), plus the latest due weekly
  summary and today's morning summary when its time has passed. Only the newest new summary
  of each kind gets a prompt.
- **Weekly summary day.** It is due at the evening time on the first chosen weekday on or
  after the week's last day: the last day summarizes the week as it closes, a later weekday
  summarizes the finished week. New and existing profiles default to the last day of the
  week, morning 08:00 and evening 18:00. An empty time or day turns that summary off.
- **Prompts live on the summary row, not in NotificationJobs.** `NotificationJobs.TaskId` is
  a required foreign key to a task, and using it for summaries would mean rebuilding that
  table and branching every task-bound delivery path. A summary instead stores when its
  "ready" prompt may show (the first instant outside quiet hours, using the reminder rule)
  and when it was seen. The prompt is shown in the app; an OS notification for it waits for
  the same installed-package Windows adapter as reminders.
- **Carry-over creates personal records only.** From a daily summary, chosen unfinished tasks
  go onto the next date's My Day (today when that date has passed), once each. From a weekly
  summary they go into the next week's plan draft pool, to be confirmed in My Week. Task
  planned and due dates never change. Morning summaries look ahead and have no carry-over.
- **Look-back reads the journal.** When a complete weekly summary of last week exists, My
  Week's look-back takes the completed tasks (with their captured titles) and planned-task
  counts from it. Unfinished tasks and goals stay live because the ritual acts on them now.
