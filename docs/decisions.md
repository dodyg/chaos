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
  *Superseded in package 13:* the GA SDK refuses that switch while lock files exist (NU1005), so
  the RID is now restored up front instead (see 2026-11-10, package 13).
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
  list names of the moment, so a later rename, move or deletion never rewrites a stored summary.
  Container names are taken at generation time (containers have no history of their own).
- **Three kinds, one table.** Morning (the day ahead), Daily (evening: how the day went) and
  Weekly (seven configured local dates). Period boundaries are half-open local days in the
  profile zone, so a fall-back day is 25 hours.
- **Idempotent generation.** Scheduled and catch-up runs never add a revision to a period
  that has one. "Make it again" adds the next revision and keeps the earlier ones; the
  Summaries page shows the newest. A summary made before its period ends is labelled "through
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
- **Look-back reads the stored summary.** When a complete weekly summary of last week exists, My
  Week's look-back takes the completed tasks (with their captured titles) and planned-task
  counts from it. Unfinished tasks and goals stay live because the ritual acts on them now.

## 2026-11-10 (package 12)

- **The backups folder is the source of truth.** Each backup has a `.db.json` description
  beside it, written last. The `BackupCatalog` table is kept only as a log, because it lives
  in the database a restore replaces. Retention and the restore list read the folder. Files
  without a description are listed as "not checked yet" and are never deleted by retention.
- **Online backup API instead of `VACUUM INTO`.** PLAN §14 names
  `SqliteConnection.BackupDatabase`. The copy is switched to rollback-journal mode, so a
  backup is one self-contained file with no WAL beside it.
- **Retention uses UTC days and Monday weeks.** It keeps the newest backup, the newest of each
  of the last 7 days and of the last 4 weeks, and the 3 newest safety backups. It runs only
  after a backup succeeds. Backup files do not depend on the profile's zone or week start.
- **Restore swaps files under the live store.** `SqliteApplicationStore.RunExclusiveAsync`
  holds writes at the gate, holds new reads, waits for reads in flight, closes the writer and
  clears the pool. `DatabaseReplacement` then moves the old files aside (never deletes them),
  moves the new file in and records each step in `replace-pending.json`. Startup finishes or
  undoes an interrupted replacement before opening the database. All backup, restore, reset,
  import and purge work runs one operation at a time, so a scheduled backup cannot open the
  file during a swap.
- **The JSON archive is a table dump, not a parallel object model.** Every user-data table is
  exported with its columns and typed values, so features added later are included without
  exporter changes. A restore builds the database at the archive's schema version, inserts
  the rows with deferred foreign keys (`INSERT OR REPLACE`, because the recurrence trigger
  pre-creates revision rows), then runs the newer migrations. Rebuildable or device-specific
  data is excluded and listed in the manifest.
- **Imports write through the stores, not the recorder.** An import can create thousands of
  tasks. It writes a `task.imported` history entry per task but no undo entry. It is undone by
  restoring the backup made just before it, which the import record names.
- **Imported tasks get new IDs, except in a JSON merge.** Org and CSV keep their source IDs
  only as fingerprints. A source ID that equals a local task's ID marks the item as already
  present. A JSON merge keeps free IDs and reports taken ones as conflicts, keeping the local
  task.
- **A commit is bound to its preview.** The import token hashes the file bytes, the format
  and the CSV column mapping, and a restore needs the checksum shown in its review. A file
  changed between preview and confirmation is refused.
- **Export goes to a folder the user names.** NeoAstra has no save dialog. Settings proposes
  `Documents\Chaos exports` and can open it, or the backups folder, and no other path.
  Imports use the WebView's file picker; the file content is sent over RPC.
- **Purge removes dependents explicitly.** Most foreign keys have no `ON DELETE CASCADE`, so
  the purge deletes each dependent table in one transaction. It replaces the captured titles
  of purged tasks in stored summary facts and drops undo entries that mention them. It then runs
  `VACUUM` with the store exclusive.

## 2026-11-10 (package 13)

- **SDK 11.0.100 (GA) replaces the rc.3 pin.** The pinned 11.0.100-rc.3.26522.148 SDK was no
  longer installed; the user chose to pin the GA SDK instead of reinstalling the release
  candidate. `global.json` still pins one exact version with roll-forward disabled.
- **Every project restores the win-x64 RID.** `Directory.Build.props` sets
  `RuntimeIdentifiers=win-x64`, so the committed lock files cover the self-contained publish
  and `eng/publish-windows.ps1` runs in locked mode. Portable projects still build portable
  output; the RID only adds a restore group.
- **Launch routing uses Chaos's own named pipe, not `NeoSingleInstance`.** NeoAstra's
  single-instance service needs a `NeoApplication` before the window exists, which the simple
  `NeoApp.Run` host does not offer, and Chaos must decide before storage opens anyway (the data
  root is locked). `SingleInstanceGuard` (a per-user, per-data-root mutex) decides first; a
  secondary process sends its arguments over a `CurrentUserOnly` named pipe
  (`LaunchRouter`: length-prefixed, at most 8 arguments of 512 characters, one-byte
  acknowledgement) and exits without touching storage. The primary re-parses the arguments
  with `LaunchIntents`; nothing but arguments travels.
- **Notification buttons are `chaos:` protocol activations.** The Windows adapter uses
  `Windows.UI.Notifications` (`AddToSchedule`/`RemoveFromSchedule`) from the Windows SDK
  projection that the TFM already includes, so no Windows App SDK or COM activator is needed.
  Each button launches `chaos:reminder/<job>/<generation>/<action>`; the running instance gets
  it through the pipe, or a fresh start handles it. The reminder command checks the generation,
  and the OperationId is derived from the button, so a repeated click is a replay. Done and
  Snooze act without bringing the window forward; a Done on a task with open subtasks opens the
  task instead, because that choice is the person's. The adapter is used only with package
  identity; an unpackaged run keeps the in-app presenter.
- **The page takes activations; the host does not push them.** Shortcuts, the tray, other
  launches and notification buttons queue activations in the host (newest 32 kept). The page
  takes them on focus, on becoming visible and once a second with `shell.take`, which is cheap
  and needs no event contract. A page without the shell service stops polling.
- **Desktop settings live on the profile.** Migration 013 adds `CaptureShortcut`,
  `PadShortcut` (NULL = off) and `KeepRunningInTray` to `Profiles`, with defaults Ctrl+Alt+N,
  Ctrl+Alt+P and on. They travel with backups and the JSON archive like every other setting.
  Shortcuts are normalized (Ctrl, Alt, Shift, then a letter, digit or F1–F24; Ctrl or Alt
  required) and registered through NeoAstra's `RegisterHotKey` service; a conflict is reported
  in Settings and the in-app shortcuts keep working.
- **Settings collect general and desktop settings only.** Name, time zone, numeric date order,
  shortcuts and close-to-tray are on the new Settings page. Reminder times, summary times,
  week start and backups stay on the pages they affect, linked from Settings. A time zone change
  recomputes every desired notification in the same transaction and then resynchronizes the
  engine; task dates are not rewritten.
- **Close keeps Chaos in the tray only when a tray icon exists.** The close request is cancelled
  and the window hidden only for the person's own close, only while the tray icon was created,
  and never during Quit or Windows shutdown. Quit (tray menu or Settings) ends the process.
- **NeoAstra desktop services get minimal, unused opener scopes.**
  `NeoDesktopServices.CreateSystem` refuses empty URL scopes, file roots and file intents. Chaos
  passes `AnyWebAddress`, its own private `desktop` folder and plain text; the opener is never
  called and no desktop renderer handlers are registered, so the page gains nothing.
- **Icons are not in `Assets`.** NeoAstra publishes the web bundle to `assets\`, which is the
  same folder as `Assets\` on Windows. The tray icon is `Icons\chaos.ico` and the MSIX logos are
  `Images\*.png`, generated by `eng/make-assets.ps1`.
- **The package does not virtualize file writes.** `desktop6:FileSystemWriteVirtualization` is
  disabled (with the `unvirtualizedResources` capability), so the installed app keeps its data in
  `%LOCALAPPDATA%\Chaos` exactly like an unpackaged run: updates keep it and uninstalling does not
  delete it. Removing data stays the explicit Settings action.
- **My Day is the start page.** A fresh launch opens My Day (PLAN §13); component tests that
  exercise lists start on the Inbox explicitly.
- **Text fields default to `dir="auto"`.** The shared `Input` and `Textarea` components set it,
  and task-row titles use `unicode-bidi: plaintext`, so Arabic and Latin titles each take their
  own direction. Animations and transitions are cut to nothing under `prefers-reduced-motion`.

## 2026-11-11 (GitHub prerequisites, in progress)

- **Use GitHub CLI as an external dependency for the first GitHub connection.** This replaces
  the earlier token-entry decision at the user's direction. Chaos calls `gh api` and does not
  manage credentials; disconnect leaves CLI authentication intact. GitHub CLI's active account
  and its permissions govern access, which can be broader than the repositories Chaos syncs.
  GitHub CLI may fall back to plaintext credential storage; see the
  [GitHub CLI authentication documentation](https://cli.github.com/manual/gh_auth_login).
  CLI capability, safe account binding and installed-MSIX behavior remain G0 prerequisites.

## 2026-11-11 (GitHub G0 account-binding blocker)

- **The proposed CLI adapter cannot yet guarantee the connected account for a write.**
  `gh api` has no documented per-request account selector; `gh auth switch` changes the active
  account used by subsequent commands. A separate `/user` check and write therefore have a
  time-of-check/time-of-use gap if another process switches accounts. The documented
  `gh auth token --user` workaround would make Chaos handle credentials, contrary to GITHUB.md.
  Stop G0 and dependent implementation until the account-binding design is resolved. This is
  an assessment of the documented interface, not a completed live concurrency test. See
  [gh api](https://cli.github.com/manual/gh_api),
  [gh auth switch](https://cli.github.com/manual/gh_auth_switch), and
  [CLI multiple-account design](https://github.com/cli/cli/blob/trunk/docs/multiple-accounts.md).

## 2026-11-11 (GitHub G0 guarded CLI candidate)

- **Investigate an account-bound GitHub CLI fork.** A separately installed, pinned fork could
  resolve its credential once inside the CLI, verify the numeric `/user` identity and issue the
  API request using the same credential snapshot. Chaos would never receive credentials. The
  fork must fail closed on refresh, identity or keyring ambiguity and enforce destination and
  redirect restrictions. This is a candidate only: no implementation, two-account race test,
  network-boundary test or installed-MSIX test has established it yet. Isolating `GH_CONFIG_DIR`
  alone is insufficient because the CLI has an unkeyed keyring fallback. G0 remains blocked
  until the candidate and the other prerequisites are proven. The setup and maintenance cost
  of a separate CLI fork must be documented; stock `gh` is not a safe fallback. See
  [gh api](https://cli.github.com/manual/gh_api),
  [account switching](https://cli.github.com/manual/gh_auth_switch), and
  [keyring fallback analysis](https://github.com/cli/cli/issues/14370).

## 2026-11-11 (GitHub App user authorization)

- **Use a GitHub App acting for the user, with credentials in Windows Credential Manager.**
  The user explicitly authorized Chaos to handle these credentials, superseding the earlier
  no-token-in-Chaos and GitHub CLI decisions. Credentials remain outside SQLite, backups,
  exports, RPC, renderer, logs, exception text, command lines and URLs. Per-request immutable
  credential snapshots are tied to a verified numeric user ID; serialized refresh and failure
  recovery must be proven. App installation and user authorization are separate. No app private
  key or client secret ships in the desktop client. G0 remains open for live auth, permissions,
  API and installed-MSIX evidence; credential-independent G1 work may proceed alongside it.
  See [the evaluation](github-auth-evaluation.md).

## 2026-11-11 (existing GitHub CLI credentials)

- **Use the existing GitHub CLI login, without registering a GitHub App.** The user explicitly
  declined app registration and authorized Chaos to obtain an account-specific token from `gh`
  and use it in memory. This supersedes the GitHub App decision above and the prior prohibition
  on token handling and `gh auth token`. GitHub CLI retains persistent credential ownership;
  Chaos never persists tokens or places them in logs, process arguments, environment, SQLite,
  backups, exports, RPC or the renderer. For each operation, bind one token snapshot to a
  successful `/user` numeric-ID check and use that same snapshot for the API request. Fail
  closed on mismatches and credential errors; an active-account switch cannot affect the
  snapshot. The two-account live race, stale credentials, network boundary, and installed-MSIX
  tests are still required in G0. See `GITHUB.md` for the revised transport and gate.

## 2026-11-11 (GitHub G2 schema stage)

- **Live prerequisites are release gates.** The user authorized offline-tested storage, API
  and UI implementation while the G0 live credential and installed-MSIX checks remain open.
- **Migration 014 stores connection metadata and sync bookkeeping only.** The database never
  stores credentials. A composite foreign key binds each issue link to its subscription's
  connection, preventing inconsistent denormalized connection IDs. Partial indexes enforce
  active connection/repository uniqueness; purged issues retain their stable identity.
- **Archives reset GitHub machine state; purge retains an ignored issue tombstone.** Sync-run
  diagnostics are excluded from JSON archives. Task purge removes dependent mappings before
  deleting subtasks/diary and clears the task reference without losing the issue identity.
  Container/section rows still referenced by subscriptions or milestone mappings are retained.
  The typed store and merge-import handling remain outstanding G2 work.

## 2026-11-11 (GitHub G2 typed persistence and merge)

- **GitHub metadata uses typed, transaction-scoped stores.** Connections, subscriptions and
  issue links use optimistic versions; checklist/comment and label/milestone mappings are
  bookkeeping upserts. The recorder passes the store through unchanged. Dirty/retry and task
  batching queries are bounded at 200; sync diagnostics retain the newest 200 per connection.
- **A merge never creates a GitHub connection.** A link is kept only under an existing local
  connection/subscription belonging to this profile and space, without a conflicting issue or
  link ID. Otherwise the task is ordinary, with an origin diary note and preview warning.
  Comment diary IDs are preserved when links are kept so pending-post markers remain stable;
  a colliding comment diary ID also detaches the imported task. Imported links are dirty for
  reconciliation, and device retry/ETag state is cleared. Full restore is still device-gated.

## 2026-11-11 (GitHub G3 transport)

- **CLI only reads local metadata and credentials.** Setup reads the configured login using
  `gh config get user --host github.com`; token lookup names that account and uses `--no-refresh`.
  Login/refresh stays outside Chaos. This avoids letting a CLI refresh path or configured API
  host bypass Chaos's direct-HTTP destination policy. Switching the active CLI account does
  not change an existing connection while its saved account credential still verifies by ID.
- **Each API operation verifies one immutable credential snapshot.** `/user` and the operation
  use the same captured credential; no issue body is sent on a mismatch. Only setup establishes
  the initial account identity. Credentials never cross the application port or RPC, persist,
  appear in URLs/arguments/environment, or enter error/log text. Redirects and cookies are off.
- **The adapter returns typed REST pages and GraphQL references.** Conditional requests preserve
  server pagination cursors and ETags; cursors are restricted to the API origin. Writes are
  serialized/spaced, rate-limit resets pause further requests, and the last 100 requests are
  reserved for essential work. Native counters include identity verification requests, so a
  no-change sync may have 200 identity responses as well as 304 list responses. Dot-segment
  label names use the documented GraphQL ID mutation rather than a normalized REST URL path.
  See [GitHub Issues GraphQL reference](https://docs.github.com/en/graphql/reference/issues#removelabelsfromlabelable).
- **G3 has deterministic process and HTTP seams.** Tests use synthetic credentials, fake child
  processes/HTTP handlers, and an in-memory remote GitHub API with real SQLite in integration
  tests. This does not substitute for the live credential, permissions, proxy or installed-MSIX
  release gates approved by the user. Machine CLI discovery currently targets Program Files.

G3 follow-up (2026-11-12): GraphQL pull-request references use `fullDatabaseId` and retain
64-bit numeric identity; dot-only label names are removed through GraphQL node IDs. Read
redirects resolve against their original URI, are validated to the API origin, and are followed
at most once. Mutations never redirect automatically. Both headers and response-body buffering
share a 30-second deadline. These details have offline regression tests; live checks remain
release gates.

## 2026-11-12 (GitHub G4 connection management)

GitHub settings opens from its own Settings tab and reads only local connection metadata until
an explicit Connect, Browse or Find action. Account verification and repository lookup happen
outside write transactions. Mutations carry operation receipts and optimistic versions; the UI
retains an operation ID when a response is lost. Native RPC exposes numeric IDs as strings,
including filter milestone numbers, and returns fixed actionable transport errors as data.

An active connection reuses its identity only for the same numeric GitHub account. Disconnect
atomically detaches all subscribed links, clears checklist/comment synchronization mappings,
retains tasks and diary text, and adds a private origin note. Purged issue tombstones stay ignored.
Reconnecting after an explicit disconnect creates a new connection ID so historical detached
issue identities cannot collide with the new subscription. GitHub CLI authentication is untouched.
Filters reset inbound checkpoints only when their scope changes; destination edits affect new
tasks. G4 does not run synchronization or report fictitious sync counts.

Offline verification uses real SQLite, fake GitHub and RPC/UI tests. Live Connect in a window and
installed-MSIX checks remain part of the credential release gate. A running development window
locked the ordinary Release directory, so verification used bin/GitHubVerification without
closing the user's app. The same locked restore, full Release solution build and test gates ran.

## 2026-11-12 (GitHub G5 comment fetch and manual sync)

Sync now is an explicit, inbound-only background pass. It returns immediately, coalesces requests
for the same connection, and exposes local status for UI polling. A queued job carries the
connection ID selected by the RPC and validates it before any API request; activity is scoped to
that connection and cleared after data replacement. Opening the app or Settings starts no GitHub
request. A pass fetches at most 20 issue pages and 20 comment pages per repository, retaining
continuations for the next explicit pass. G7's automatic scheduling is still separate.

Initial comment import follows validated last/previous Link relations to select the newest 100.
The repository-wide updated feed handles subsequent edits and additions. During a pass, issues
with checks older than one day, or a decreased comment count, receive bounded full-list checks.
Only a completed check can mark mapped comments remotely deleted; diary text remains. Each
check tracks at most 200 mapped comment IDs, continuing larger sets in later checks. Metadata
contains cursors, timestamps and IDs, never comment bodies. Migration 016 adds per-issue device
checkpoints, omitted from archives and cleared on restore or subscription scope changes; the
planned pull-request migration moves to 017.

The manual engine cancels and drains a pass before restore/reset, repository removal or
disconnect. Completed batches remain committed after cancellation, and subsequent passes replay
safely. A real pre-import backup is supplied by the desktop composition. Completion refreshes
query revisions, tags, counts and milestone containers. Transport errors use fixed messages;
rate limits block subsequent reads, authorization failures require verification, and 404/410
mark subscriptions unavailable. Error-run counts cover completed subpasses; the UI shows counts
only for successful completed passes. It explicitly says local edits are not sent yet.

Offline verification uses real SQLite, fake GitHub, actual backup creation, RPC integration,
engine cancellation/coalescing tests and UI polling tests. Live credentials and installed-MSIX
acceptance remain release gates; this work does not claim a live GitHub acceptance run.

Final validation for this stage: locked restore, clean separate-output Release build, all 643
.NET tests and all 86 frontend tests passed; Slopwatch reported no findings.

## 2026-11-12 (GitHub G5 completion)

First import reads open issues before the optional recent-closed phase. Both phases share the
page budget and persist their phase/cursor across restart; completion publishes a watermark no
earlier than the scan start so old closed history is not replayed on the incremental pass.
Repository canonical metadata is checked against its saved numeric ID. Rename handling clears
URL caches while retaining timestamp watermarks and the original pre-import backup reference.

Issue payloads supply current label and milestone definitions while the initial import remains
protected by its pre-import backup. Catalog refresh follows that guarded import, then a bounded
known-link verification pass applies catalog-only changes, missing issues and transfers. Each
catalog page has its own ETag and remembered next link, with bounded metadata storage and daily
unconditional revalidation. Verification cursors survive restart and use conditional issue reads;
a daily full check and catalog changes force fresh issue reads. No issue text enters checkpoints.

Transfers preserve the task and its checklist/comment mappings. Old repository tags remain local
when they no longer map to the new repository. Managed placement follows the new subscription;
any local container/section move disables it, while an inbound move restores the flag it read in
the same transaction. The baseline records its repository so transfers that are temporarily out
of scope can resume the correct placement even if both repositories use the same milestone
number. Non-subscribed destinations stay unavailable, with a validated GitHub URL, until added.
404 is confirmed before hiding a link; 410 is immediately unavailable, and later reads recover
its existing task. Milestone title clashes and user-edited project names, deadlines and task due
dates are preserved. The task editor's local status notice explicitly identifies pending edits
as not sent; it makes no GitHub call. G6 still owns outbound writes and their validation.

Migration 017 extends device-local checkpoint kinds and adds local-placement detection. Applied
migrations are unchanged, upgrade backups precede the migration, and the planned PR migration
moves to 018. G5's offline implementation exit is distinct from its live throwaway-repository and
installed-MSIX release acceptance, as authorized by the existing live-check deferral.

Final G5 validation: locked restore and the separate-output Release build passed with no warnings
or errors; all 662 .NET tests and all 89 frontend tests passed; Slopwatch reported no findings.
G5 is marked complete for offline implementation. Live credential, throwaway-repository and
installed-MSIX evidence remains unclaimed and required at the release gate.

## 2026-11-12 (GitHub G6 lifecycle, task controls and write revalidation)

Migration 019 handles trash/restore at the database boundary so container deletion, undo and
imports follow the same rule as task commands: trash ignores a link without changing GitHub;
restore queues a fresh merge. Purge retains the issue identity. Recovery leaves ignored links
alone until restoration. Detach retains task content and a provenance diary entry, clears
checklist/comment mappings and pending write intents, and prevents future imports from relinking
that task. Repository removal also clears its write intents. Desktop pause, retry and detach
cancel and drain active sync work before changing local configuration.

Task status remains a local read. Its RPC exposes link versions, conflict/error notices, a
validated issue URL, repository identity and milestone due-date ownership. Retry and conflict
dismissal use optimistic link versions and operation receipts. The task UI provides those actions
and detach, restricts connected title lengths and recurrence, and locks milestone-owned due
fields. Journal metadata refresh preserves the user's current posting choice, including opt-out.
Tag settings warn that removing mapped tags removes issue labels on the next sync; detailed
per-label affected-issue counts remain an enhancement.

GitHub's REST documentation explicitly excludes conditional unsafe-method requests unless an
endpoint documents support. Issue PATCH and comment mutations therefore cannot be made atomic
with a preceding GET by adding If-Match. See
[GitHub REST best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api).
Chaos now checks the remote snapshot again after preparing a write and checks local task/link
versions and active configuration before sending it. A changed snapshot discards the unsent
intent and defers to inbound merging. Comment edits/deletes similarly revalidate remote content,
and comments recheck local eligibility before mutations. These are best-effort race mitigations:
a GitHub edit after the final read can still race the write. Recovery records continue to cover
uncertain mutation outcomes; they do not provide server-side compare-and-swap.

Offline verification: locked restore and Release build passed; all 691 .NET tests and 93 frontend
tests passed; Slopwatch found zero issues. No live credential or repository operation was run.
Live G6 acceptance and installed-MSIX verification remain required. Automatic scheduling, full
repository views and pull-request links remain G7–G9 work.

## 2026-11-12 (GitHub issue sync after local actions)

Native RPCs queue a task ID only after a successful local mutation. Task edits/status/restores,
subtasks and checklist conversion, task tag links/unlinks, diary mutations, history field restore
and self-assignment use this path. Retry resets the link error and queues the same issue; it no
longer cancels unrelated sync work. Ordinary or ineligible tasks are checked locally and cause
no GitHub request. Failed local commands never queue work. The RPC returns without waiting for
GitHub, and dirty flags and existing write intents remain the recovery authority.

The background engine serializes these passes with manual repository/full passes. Queued task IDs
coalesce, while an action arriving during that task's active pass queues one follow-up. A FIFO
queue prevents a frequently edited task from starving others. Full-sync activity is retained
separately so subsequent task passes cannot hide its result in Settings. Restore/reset/pause,
removal and detach retain cancellation/drain behavior; queued work is cleared before replacement.

Issue-scoped passes refresh repository identity, recover uncertain writes, merge the one current
issue, refresh that issue's comments with its existing bounded checkpoint logic, and push only
its pending fields and selected diary entries. They do not read repository-wide issue/comment
feeds, create an import backup or advance shared repository scan cursors. The existing account,
pause, sign-in, rate-limit, link-backoff and read/write revalidation checks still apply.

The task notice shows a reduced-motion-aware spinner while queued/running and polls local status
until completion. A changed settled link version also refreshes the editor if a fast sync finished
before the first poll. Refreshing a clean editor reloads its current version; unsaved task fields
are retained. Opening a task does not initiate a GitHub request. Settings copy now describes
two-way sync and issue actions instead of claiming local edits are never sent.

This increment does not introduce periodic scheduling, a connectivity trigger, automatic retry
timers or an initial dirty scan. Offline and failed writes remain dirty and can be retried by a
later action or Retry. Global tag delete/merge and operation-level undo still rely on a later
issue action or explicit full sync. Live GitHub and installed-MSIX acceptance remain release gates.

Validation: locked restore and Release build passed with no .NET warnings/errors; all 701 .NET
tests and 96 frontend tests passed, with zero Slopwatch findings. Final desktop verification
passed all 76 tests. A frontend run under concurrent build/test load timed out on the unchanged
initial Settings loading assertion; its focused run and the complete isolated rerun passed.
No test timeout was increased and no assertion was disabled. Live GitHub requests were not used.

## 2026-11-13 (Work/Personal focus)

The capacity category now organizes the UI instead of only feeding My Week capacity. A sidebar
Focus switch (All, Work, Personal) filters My Day, lists, built-in views, tag views and search,
and every sidebar count. The filter is a server-side `Category` on the task query spec, so counts
and rows still come from the same criteria. The focus is a per-viewer preference kept in local
storage, not profile data. Projected repetitions in Upcoming are hidden under a focus because
they carry no category.

When a day mixes both categories, My Day splits into Work and Personal lanes, each with open
count and estimate totals. The day keeps one manual order, and reordering inside a lane maps
back onto it. Dragging a row onto the other lane, or using the row context menu, switches its
category through the ordinary task edit, so it is undoable. Task rows show a coloured category
edge.

Lists and projects have an optional default category (migration 022, `Containers.DefaultCategory`,
NULL = none). The Inbox has none. A captured task takes an explicit category first, then the
destination list's default, then the current focus, then Personal. Existing tasks never change
when a default changes. The CSV/archive merge import carries list names only, so it does not
carry list defaults (as with project deadlines).

## 2026-11-13 (Focus follow-ups, autosave and App split)

My Day's summary now carries the day's available time and work allowance from My Week (a
confirmed override, else the weekday rule, else the default) plus the work estimate. The Work
lane and the Work focus compare against the allowance, and the whole day against available
time. Over is flagged, never acted on, matching My Week.

Quick add accepts `~work`/`~w` and `~personal`/`~p`/`~home` for the category. `@` was already the
space sigil (`@personal` means the Personal space), so it was not reused. An unknown `~word`
stays in the title with a warning, like other kept tokens. The preview also has a Category
select. The precedence is unchanged: explicit, then list default, then focus.

My Week's confirmed board follows the focus: other-category tasks are hidden and counted, and
day headings show work or personal time. Planning (the ritual) always shows the whole week.

The My Day background chooser moved behind a header icon. An empty Suggestions box collapses
to one line.

Task fields now save themselves 600 ms after the last change, one save at a time, through the
same task edit (so each save is undoable). Closing the editor or switching tasks saves at once.
A failure keeps the draft and offers Retry. The Save/Cancel footer became a save status and a
Done button, so there is no discard; Undo replaces it.

`App.tsx` was split: the side panel is `TaskEditor.tsx` (it now owns subtask, checklist,
GitHub-notice and pad-anchor state), the list page is `ListPage.tsx`, and the shared row style
is `taskRow.ts`. Subtasks reload when the shared subtask revision changes, instead of each
caller setting them directly.

## 2026-11-13 (Browser preview, undo merging, subtask autosave, import metadata)

`npm run preview:ui` (in `src/Chaos.Desktop/frontend`, port 5174; also the `ui-preview` entry in
`.claude/launch.json`) serves the real UI in a browser without the desktop host.
`vite.preview.config.ts` swaps `@neoastra/client` for `src/preview/client.ts`, so the generated
RPC objects stay unchanged and every call goes to an in-memory backend (`src/preview/backend.ts`)
seeded with sample tasks and lists. It models tasks, lists, sections, queries, counts, My Day,
My Week, subtasks, quick add and undo; any other command answers with an empty successful read.
It is a development aid only: the production build never imports it.

Back-to-back detail edits of one task share one undo entry. The merge happens when the newest
open entry is a detail edit of that task, nothing else changed it in between (the version is
consecutive), and that entry is under two minutes old. The merged entry keeps the first
before-image, so one undo restores the task as it was before the burst. Task history still
records every save.

Subtask editing saves itself like task fields. Both now use one hook (`autosave.ts`): one save
at a time, the newest known version, the draft kept on failure, and a save on switch or close.

Ctrl+1, Ctrl+2 and Ctrl+3 set the focus to All, Work and Personal. The list quick-add
placeholder shows `~personal`.

Imports now create lists with their original kind, project deadline and default category. This
covers Chaos JSON archives (from the Containers row; older archives have no default-category
column) and Org files (`CHAOS_KIND`, `CHAOS_DEADLINE`, and the new `CHAOS_DEFAULT_CATEGORY` that
plain-text export now writes). A list that already exists keeps its own settings.

## 2026-11-13 (UX polish: dates, editor, list page, sidebar, keyboard, palette)

Dates read as people say them (`dates.ts`): Today, Tomorrow, Yesterday, a weekday within the
coming week, otherwise "Mon 30 Nov", adding the year only when it is not this year. The exact
date and "in N days" go in the tooltip. Due dates before today are red and due today or tomorrow
amber; planned dates are never coloured. The profile's today comes from `TodayContext`, set by
the shell from the day clock.

Editor: planned and due show a time zone only once a time is set; clearing a date clears its
time and zone; Today, Tomorrow and Next week (Monday) are quick buttons. Inactive tabs show only
their icon (the name stays in the tooltip and accessible name) so all six fit. Below the xl
breakpoint the panel lays over the list instead of squeezing it.

List page: the category hint appears once, under quick add. The completion filter is an
All/Open/Finished switch. In All, finished tasks sit in one folded Finished group after the
sections. Projects show done-of-total progress under the title.

Sidebar: lists and the Inbox show open counts under the current focus. These come from a new
`containers` part of the view counts, built from the same Container view query. The bottom bar
icons have short labels. Quit asks once ("Sure?") before ending the app, since that also stops
reminders.

Keyboard: in any task list, J/K or the arrow keys move between rows (folded rows are skipped),
Enter opens, V views, X toggles done and M adds to My Day. Ctrl+K opens a command palette
(views, lists, projects, tags, actions, focus, and task search from two characters). Inside a
text field Ctrl+K stays the editor's link shortcut, and the palette also opens from the
"Ctrl K" button in the sidebar search box.

## 2026-11-14 (Org files removed)

Org support is gone from the product. The spec no longer has an Org files connection, and the
app no longer exports an `.org` file or imports one: `OrgImport`, `PlainTextExport.WriteOrg`,
`ExportFormat.Org` and the Org choices under Data & backups are removed, along with the
status-change reading in `ExportSnapshot` that only the Org LOGBOOK used. The RPC refuses
`"org"` for both export and import. `ImportSourceKind.Org = 1` stays so import records made
earlier still read; nothing creates it any more. The Org parts of PLAN.md and GITHUB.md
describe what was built at the time and are superseded by this entry. Markdown files are now
the only plain-text export; the JSON archive remains the lossless format.

## 2026-11-14 (Pages)

Pages are one table, `Pages` (migration 023), for both tree pages and daily pages. A daily page
holds `DailyDate` and never a parent; one active daily page per date is a unique index, so a
deleted day does not block writing that day again. In the app they are two separate views:
Pages shows only the page tree, and Daily page shows only the daily pages, so the two are not
mixed in one list. A link from one kind to the other switches to the other view. Autosave works like the pad: title and text
save together with an editor revision, and a late or repeated save never replaces newer text.
Typing is not an undo step; creating, moving, deleting, restoring, restoring a version, Make task
and the pad-to-page moves are recorded operations with undo (`ChangeKind.Page`).

Versions (`PageRevisions`) follow sittings of writing rather than keystrokes: a save within ten
minutes of the newest version updates it, a longer gap (or an hour of continuous writing) starts
a new one. Restoring a version is itself a new version. The frontend compares versions with a
line diff.

Links are stored in the text only. `[label](chaos:page/<id>)` links by ID, so renames and moves
keep them; `[[Name]]` finds a page by title or alias and creates one when followed. A page
followed from a `[[Name]]` link is created at the top level and the link in the open page is
rewritten to the ID form. `[label](chaos:list/<id>)` links a list or project. Backlinks and
"Mentioned in" are found by searching page text and task notes for these forms when a page or
task opens, instead of keeping a link table in step with every edit.

Front matter is read by a small parser (in C# and TypeScript, kept in step by tests) that
covers `key: value`, quoted values, `[flow, lists]`, `- item` lists and comments; anything else
is reported with its line and every property is ignored until fixed. The editor shows only the
body; the panel edits one key at a time and never rewrites other lines. `PageTags` is derived
from the `tags` key whenever the page row is written (undo included), linking only tags that
exist; the panel offers to create missing ones. Image backgrounds wait for attachments; scenes
and colours work now. The starting background for new daily pages is a per-computer setting,
like the My Day background, and is written into the new page's front matter.

Outline editing works on the Markdown source: Tab/Shift+Tab and Alt+Up/Down act on the list
item with its children, and Enter continues a list. Folding happens in read mode, where headings
start foldable sections and list items with children fold.

Search indexes pages as kind 5; migration 023 recreates the rebuildable search tables with the
wider check and resets the normalization version so the next start rebuilds the index.

## 2026-11-15 (Google Calendar access for Phase 2)

- **Internal OAuth app first.** The current audience is SilverKey, so Google Calendar access
  will use an OAuth client in a Google Cloud project owned by the SilverKey Google Workspace
  organisation, with the consent screen's user type set to **Internal**. Internal apps skip
  Google's verification, show no "unverified app" warning and have no 100-user cap. Only
  accounts in the SilverKey domain can sign in. The project must sit under the organisation, so
  a Workspace admin creates it, and an admin policy restricting internal or third-party apps
  could require allowing it.
- **Desktop flow, narrowest scope.** Use a Desktop-type OAuth client with a loopback redirect
  and the narrowest read-only calendar scope that serves My Week and the calendar view. The
  Google API client library (`Google.Apis.Calendar.v3`) is only a transport and does not
  affect review.
- **Public release needs verification.** Calendar scopes are sensitive, so a public release
  needs Google's OAuth verification (brand, privacy policy, domain, scope justification),
  which the spec warns can take weeks. Calendar is not a restricted scope, but confirm
  against Google's current scope table. Switching from Internal to External is a consent-screen
  change, so the sign-in code stays the same. Start the submission well before any public
  release (SPEC open question).
- **ICS subscription link needs no OAuth.** Importing a calendar's secret ICS address gives
  read-only meetings with no Google review and works for any user, so it can be built first
  and independently of the Google connection.

## 2026-11-15 (Phase 2 owner decisions — status after P0)

PLAN2 §4 lists eight decisions (D1–D8). Status after package P0; defaults are in use where the
plan provides one, and each blocking package stays unticked until the owner confirms or replaces:

- **D1 (phone client in Phase 2)** — default adopted: no; Phase 2 is desktop plus service. P19
  will be recorded as not applicable unless the owner says otherwise.
- **D2 (hosting)** — default stands (any Linux container host with managed PostgreSQL, S3-compatible
  storage, key vault). Affects P20 only; nothing in code depends on it.
- **D3 (domain, email sender)** — `https://sync.<owner-domain>` placeholder; affects P12's live
  exit and P20, not P1–P11.
- **D4 (sign-in providers)** — default adopted: email code + Google first; Microsoft, Apple and
  GitHub share the code path and stay off until registered. Affects P14 exits only.
- **D5 (attachment limits)** — default adopted: 100 MiB per file, 5 GiB per account, enforced by
  the server, configurable. The local client cap (P9) is 100 MiB.
- **D6 (has Phase 1 proven itself?)** — **PENDING; no default exists.** The installed-desktop
  checklist (`docs/acceptance.md`) and the WP0/WP10 interactive items need a person at a Windows
  desktop with an installed MSIX; the implementing environment is headless. The owner instructed
  "implement PLAN2 up to P11", which P1–P11 implementation proceeded on; the P0 and P1 checkboxes
  stay unticked until D6 is answered (pass or written waiver).
- **D7 (GitHub fields in personal custom views)** — default adopted: yes, personal views only;
  shared views remain phase 3.
- **D8 (calendar library)** — default adopted: FullCalendar standard (MIT) only. Spike note (P0,
  `docs/desktop-support.md`): v7 restructured the packages; P4 pins `@fullcalendar/react@7.1.5`
  and verifies the v7 API before use. No premium packages exist on npm (`@fullcalendar/premium`
  is 404), so "standard only" is currently the whole registry offering.

## 2026-11-15 (P1 sync foundation — implementation decisions)

- **Link tombstones without partial unique indexes.** PLAN2 §7.2 asks for tombstoned link
  tables to carry partial `WHERE DeletedAtUtc IS NULL` unique indexes. For the five link
  tables (TaskTags, SubtaskTags, ScratchPadTags, PageTags, WeekGoalTasks) uniqueness is the
  composite **primary key**, which SQLite cannot make partial. Instead the row stays and a
  re-added link **un-tombstones** it (`ON CONFLICT … DO UPDATE SET DeletedAtUtc = NULL`),
  which preserves the same guarantee: a removed link never blocks re-adding it. The catalog
  records the classification comment; no unique rule is needed for a primary key.
- **WeekGoalTasks loses its `ON DELETE CASCADE`.** Goals are replaced wholesale when a week
  is re-confirmed, so a cascade would erase even tombstoned links without the store seeing
  the removal. Migration 024 rebuilds the table in place (rows and index carried over).
  Content replacement removes a goal's links physically with the goal — the removal travels
  as a purge, like the goal's own; `DeletedAtUtc` remains for removals that keep the parent
  and for tombstones the sync applier writes. Tag links (TaskTags, SubtaskTags,
  ScratchPadTags, PageTags) do tombstone: their removal is a user-facing change on a living
  entity.
- **Purge takes tombstoned links of purged owners.** A purged tag or task removes its link
  rows entirely (they are dependents), which the link tables' foreign keys require anyway;
  a **live** task's tombstoned link survives, because the removal it records still has to
  sync (`DataPurge` collects deletable tags per pass and removes their links first).
- **Restore keeps this device's identity.** Every restore rewrites `DeviceSettings` with the
  live database's rows (`OpenAndPrepareAsync`), so neither a backup nor an archive can import
  another device's identity; archives exclude `DeviceSettings` and every `Sync*` table, and
  a reset starts a fresh identity. Seeding (`INSERT OR IGNORE`) only fills a database that
  has none.
- **Capture triggers deduplicate with `WHERE NOT EXISTS`, not `INSERT OR IGNORE`.** SQLite
  gives statements inside a trigger program the conflict-resolution algorithm of the
  statement that fired the trigger, so an ordinary write's default ABORT overrides the
  trigger's `OR IGNORE`, and a field group whose pending row already existed (captured by an
  earlier write) failed the SyncPending key. The pending inserts therefore carry an explicit
  `WHERE NOT EXISTS` probe, and multi-column group comparisons are parenthesized (`AND`
  binds tighter than `OR` — the first generated version silently dropped the guard for all
  but the last column of a group).

## 2026-11-15 (P2 Kanban board - implementation decisions)

- **BoardSettings rows use a deterministic id** (`boardsetting:{targetKind}:{targetId}`,
  UUIDv5) rather than a random one, so two devices that configure the same target offline
  converge on one row and the `UX_BoardSettings_Target` unique index needs no duplicate rule
  (a catalog exemption, like Sections). The plan left the id open; the natural key is stable.
- **The tag-group choice lives in two extra columns** (`ColumnTagId`, `SwimlaneTagId`) that
  the plan's column list did not name: grouping by a tag group is meaningless without saying
  which parent tag, and columns and lanes can pick different parents. They travel in the
  `grouping` field group.
- **A card move is one composed operation, not a chain of commands.** `board.move` performs
  the column's field change and the within-column reorder inside one transaction with one
  OperationId, so it produces exactly one receipt and one undo entry ("Move task on board").
  The status-change core was extracted from `TaskCommands.SetStatusAsync` into
  `TaskCommands.ApplyStatusAsync` and reused; behaviour of the existing command is unchanged.
- **A move names its neighbour card, not an index.** `board.move` takes `beforeTaskId` or
  `afterTaskId` (neither keeps the card's place in the container order). An index counted
  among the cards the board shows disagrees with the server's count whenever the board hides
  tasks — the Work/Personal focus, hidden columns, tag columns sharing the container's rank
  space — and the card landed in the wrong place. The neighbour is what the person saw.
- **A dragged card keeps its swimlane.** Other lanes refuse the drop instead of accepting it
  and leaving the card in its old lane: the lane is a task field `board.move` does not change.
- **Board rows come from the board store, not the query specification.** A board's within-
  column order is the container's manual rank order, which the query store does not return;
  `IBoardStore.ListCardsAsync` joins tasks with subtask progress, tag links and the GitHub
  mark in three statements. The Work/Personal focus filter is applied in the read model.
- **`get` returns defaults without writing.** A target with no settings row gets the
  defaults (Status columns, no lanes, version 0); the row is created only by the first
  `updateSettings`. This keeps opening a board a pure read.
- **Known gap, accepted for P2:** a container's board-settings row has no foreign key and
  `DataPurge` does not remove it, so purging a list leaves an inert settings row that still
  syncs. Harmless (it is keyed by target id and re-created lazily); P3 should decide whether
  view settings share the fate of their target.

## 2026-11-16 (PLAN2 P3, backend slice)

- **The condition compiler is split across three layers.** `ViewFieldCatalog` (Domain) holds each
  field's type and legal operators, because validation lives in the domain; `ViewFields`
  (Application) resolves a validated tree into `ResolvedViewGroup` (names to stored integers,
  relative dates to dates or UTC instants in the profile zone and first weekday); the store's
  `ViewPredicateCompiler` turns that into parameterized SQL inside the same candidate CTE the
  built-in views use. The plan's single "ViewFields with an SQL compiler" would have put SQL
  in Application. Counts and rows therefore come from one specification.
- **Relative dates.** "Next N days" is today through N−1 days ahead and "last N days" is N−1
  days back through today (today counts); weeks start on the profile's first weekday. `before`,
  `after` and `on` accept any relative phrase and use its start, end or whole range. `overdue`
  is valid only as `inRange` on the due field; it shares one SQL builder
  (`ViewPredicateCompiler.Overdue`) with the built-in Overdue view, and parity tests pin it.
  A definition equal to the built-in Overdue also needs the open-status condition, as that view
  adds it implicitly.
- **Fields not offered yet.** `space` (there is one personal space), `has attachment` (P9) and
  the GitHub group (D7 default yes; not built in this slice) are absent from the registry rather
  than listed and unanswerable. "Page mention" means a task made from a page selection
  (`PageTaskLinks`), "pad link" one made from a pad selection (`ScratchTaskLinks`), "recurring"
  a task that is an occurrence of a series. `text` matches the task's own title and notes (search
  document kind 1), not its subtasks or diary.
- **Custom views include archived lists' tasks only when asked** (as built-in views do) and do
  not imply "open": the definition's own status condition decides.
- **Layouts.** `ViewLayout` v1 allows list, table and board; calendar is refused until P4.
  "Manual" sort needs exactly one container fixed by a top-level `all` condition. A custom
  view's board will reuse `BoardSettings` with `TargetKind = View`.
- **Delete is a tombstone and Undo is Restore.** There is no operation-history entry for views;
  the toast's Undo calls `restore` with the delete's version. Restoring a view with alerts
  re-baselines it, as does enabling alerts or changing the definition of an alerting view, so a
  change never notifies about tasks that already matched.
- **Alert baseline rows stay for 24 h after a task leaves** (`SavedViewMatches.LeftAtUtc`) so the
  "leaves and returns notifies only after 24 h" rule is a lookup, not a timer.
- **Purge.** Task purge deletes the task's `SavedViewMatches` rows in both purge paths. Saved
  views themselves have no purge path yet (like board settings).

## 2026-11-16 (PLAN2 P3, GitHub fields, D7)

- **D7 is taken as yes, for personal views only**, as the plan defaults. The group is answered purely from what the sync already stores: the issue link, its baseline JSON (state, label ids, milestone number, assignees) and the label and milestone mappings. No GitHub call is made and the conditions work offline.
- **Offered:** repository (is, is not, one of; picked from the subscribed repositories), issue state (open or closed), label (name contains, or has none), milestone (title contains, or has none) and assignee (login contains, or has none). Positive conditions need an issue link; "repository is not" also matches tasks that are not from GitHub. Label and assignee text is a case-insensitive substring; milestone titles are known only for repositories that map milestones to projects, while "has none" works for every repository.
- **Not offered, because the data is not stored:** the issue author, linked pull requests and pull request review state. The inbound sync keeps no author and fetches no timeline or pull request data, so a condition on them could only look like it worked. They need the sync to fetch and store them first; that is a change to the GitHub sync, not to views.
- **The editor shows the group only when a repository is subscribed** (or the view already uses it), so people without GitHub never see fields that cannot match. An older baseline that stored only assignee ids has no logins, so "assignee contains" does not match it until the next sync refreshes it.

## 2026-11-16 (PLAN2 P3, board layout on a view)

- **A view's board is the P2 board with the view as its target.** Its settings are a `BoardSettings` row with `TargetKind = View` (the table already allowed it), so grouping, swimlanes, WIP limits and hidden columns are saved, synced and versioned exactly like a list board's. The cards are the view's own rows from the same specification as the list and table layouts, read in pages until 500 and flagged as truncated beyond that; nothing is read twice or differently.
- **A view cannot group its board by section** (sections belong to one list), and `board.updateSettings` refuses it for a view target; a stale row that says Section reads as Status. Instead a view board may use one swimlane per list or project (`Container`), which a list board does not offer.
- **A drop changes only the column.** Within a column the view's saved sort decides the order, and a rank is a per-list notion, so `board.move` from a view board sends no neighbour card and the board offers no Move up or Move down. Moving to Done, the open-subtask question and undo (one operation, one undo entry) are the existing `board.move` behaviour.
- **`board.getView`** is a separate read method; `IBoardStore.ListCardExtrasAsync` supplies subtask progress and the GitHub mark for cards from many lists.

## 2026-11-16 (PLAN2 P3, view alerts)

- **View alerts have their own table, not a column on NotificationJobs.** The plan (item 6) asked for a nullable `SavedViewId` on `NotificationJobs`. Doing that means making `TaskId` optional and branching every task-bound path that reads a job: delivery, OS reconciliation, the notification center, snooze and Done, the toast adapter and its activation. Summaries took the same way out (package 11). `ViewAlerts` (migration 027, device-local, no task foreign key) holds one grouped alert per view with a pending, delivered, acknowledged or cancelled state, so the reminder delivery path is untouched and a bug in alerts cannot hold back a reminder. The plan's "record this" is this entry.
- **Alerts are shown in the app, not by Windows.** An alert exists only because the running app found a match, so there is nothing to pre-schedule with the OS, and the toast adapter's activation opens a task, not a view. The in-app stack (`views.alerts`, polled like reminder alerts) has Open view and Dismiss. A Windows toast for view alerts waits for the installed adapter to support non-task activation; the delivered row records `Presentation = InApp`.
- **Evaluation runs on the reminder engine's 30-second tick,** not "5 s after change events and every 15 minutes". The tick already runs, an evaluation is one candidate query per alerting view, and a relative-date view ("due in the next 7 days") then also moves on by itself. It is a cadence difference, not a behaviour one.
- **The rules, as implemented.** Switching alerts on, restoring a view, or changing the definition of an alerting view re-baselines silently (`ViewAlertBaseline`). A task never in the baseline is new. A task that left and returns sooner than 24 hours is re-marked present without a notice; later, it is new. Baseline rows of tasks that left are kept 7 days (`LeftAtUtc`), then forgotten, so a much later return counts as new. New tasks found before a pending alert is delivered join it, so there is at most one pending alert per view; a newer delivered alert replaces an older one of the same view. Delivery is held until the profile's quiet hours end. Deleting a view or switching its alerts off cancels what is pending.
- **Not done: clearing an alert on every device.** That is P17's acknowledgement sync; alerts are per device until then.

## 2026-11-16 (PLAN2 P4, calendar view)

- **FullCalendar 6.1.22, standard packages only (D8).** `@fullcalendar/core`, `react`, `daygrid`, `timegrid`, `list` and `interaction`, all MIT, pinned exactly in `package.json`. The v7 line exists but the v6 API is the one this component is written against; moving up is a separate, deliberate change. No premium packages.
- **The calendar reads through the ordinary task query.** `CalendarQueries.RangeAsync` runs `TaskQueries` for the planned range and the due range, so tags, lists, the Work/Personal focus and a custom view's conditions behave exactly as in lists. `TaskQuerySpec.Unscheduled` (planned date is null) was added for the side panel. Planned and due entries come from separate queries, so a task planned and due on the same day shows twice (a block and a marker).
- **Day totals ignore every filter except the focus.** Overload is a fact about the day, so a tag or list filter must not hide it. Totals use `Capacity.Measure` and My Week's availability (a confirmed override, else the weekday rule, else the default), the same precedence My Day uses. Busy meeting minutes are 0 until P5.
- **Repeats start tomorrow.** Missed slots of a fixed-schedule series are suppressed, so nothing is projected for today or earlier; the series' own active task is the entry for its current occurrence. `RecurrenceCalculator.ProjectRange` is the bounded-range projection. Slots are local dates and times, so a daylight-saving change cannot move or drop one (tested across 2026-11-01).
- **Caps.** 62 days and 2,000 entries (with a truncated flag), enforced in C#. A tag or custom-view filter reads at most 5,000 task ids to decide which repeating tasks match.
- **Entries show the dates as stored.** A planned time keeps its own zone; the calendar does not convert it to the profile zone. Dragging onto a time slot stores the task's existing planned zone, or the profile zone when it had none.
- **Drag is a task edit, so it is undoable** with the app-wide undo. Dropping on the all-day row clears the time. Only planned entries move: due markers and repeats never do.
- **Keyboard path.** FullCalendar's own drag is pointer-only, so a planned entry's menu offers "Move to date…" and each unscheduled task offers "Plan…". Arrow keys on the focused grid move a highlighted day (up and down by a week), Enter opens that day. Due markers and repeats open their task on click.
- **Calendar layout of a custom view** is `kind: "calendar"` in the layout JSON. The calendar is filtered by the view's definition; the unscheduled panel is filtered by it too.
- **Space filter.** The control exists but only offers Personal today (the plan's data-model placeholder).
- **Confirming a week plan sets planned dates (owner decision, 2026-11-16).** So the calendar and My Week agree, `MyWeekCommands.ConfirmAsync` makes each task's day assignment its planned date: one `task.updated` history entry per changed task under the confirm operation (so it is undoable), the time and the due date untouched. Finished or cancelled tasks, tasks already on that day, and unassigned pool tasks are left alone. This overrides the earlier "a week assignment never changes the task's dates"; My Day's own rule (adding to My Day never changes dates) is unchanged.

## 2026-11-16 (PLAN2 P5, calendar feeds and meetings in planning)

- **Migration 028, not 027.** The plan names `027_calendars.sql`, but 027 was taken by view alerts. The calendar tables are `028_calendars.sql`. `MaxKnownSchemaVersion` is 28.
- **The feed address lives in `ISecretStore`, never in SQLite.** `CalendarSources` has no URL column; the key is `chaos/calendar-feed/{sourceId}`. The desktop implementation is `FileSecretStore` over `DpapiSecretProtector` (Windows data protection, current user): one file per key under `<data root>/secrets`, named by a hash of the key, outside the database, backups and exports. A file copied to another user or machine cannot be decrypted and reads as absent, and the source then says "this device does not have the calendar's address". A test scans every byte under the data root, including a JSON export, for the address.
- **Each device holds its own address.** `CalendarSources` syncs (name, colour, busy flag, enabled, order, owner email) but the address does not, so a synced source on a second device shows the "address missing" error until it is added there. P6's per-device sign-in plays the same role for Google. Removing a calendar tombstones the row everywhere and deletes the local cache and address.
- **Owner email (extra column).** The plan hides the person's own declined invitations "when the feed's owner can be identified". Feeds do not say whose they are, so a source has an optional "your email" (`OwnerEmail`, synced); without it a declined invitation is shown. A feed whose attendees are all unknown is never guessed at.
- **Ical.Net 5.2.4 (MIT), pinned.** It brings NodaTime 3.2.2. Window: 7 days back to 56 days ahead, expanded lazily, capped at 5,000 meetings per feed. Zone ids that are not IANA (Outlook's "W. Europe Standard Time") are read through the system zone table; a zone that cannot be resolved falls back to Ical.Net's own UTC conversion. A parse failure keeps the old cache and shows "the address did not return a calendar Chaos can read".
- **All-day meetings.** Shown at the top of the day; busy only with an explicit `TRANSP:OPAQUE`. A busy all-day meeting fills the whole day (24 h, or 23 or 25 on a daylight-saving day), so the day's capacity floors at zero. A multi-day meeting appears once per day.
- **Busy maths.** `MeetingTime.BusyMinutes` is the union of busy intervals clipped to the local day (so overlaps count once, a meeting across midnight counts its share on each day, and the day is as long as it really is on a daylight-saving change). A meeting is busy only if its feed says so AND its calendar "counts as busy time". The meetings filter on the calendar hides them from view but never from the load.
- **Capacity.** `DayAvailability.AfterMeetings` reduces the total (floored at 0) and caps the work allowance at what remains. The calendar, My Week and My Day measure against the reduced figure, but My Week keeps the configured availability beside it (`BusyMinutes` is a separate field) so editing availability never writes a net figure.
- **My Day's fit warning.** Free time is `min(available − busy, time from now to the end of the day − meetings still to come)` for today, and `available − busy` for any other day. The plan says "end of the available hours", but availability is a total, not a window, so the end of the day stands in. The warning shows only on a day that has busy meetings (otherwise the evening would always warn), when the open tasks' estimates exceed the free time. It never changes anything.
- **Make a task.** "Prepare: <title>", planned the day before the meeting, or the same day when it starts after 12:00 local; an all-day meeting plans the day before. It goes through the ordinary capture command (so the operation id makes it idempotent) and then a link row with a deterministic id `calendareventlink:{taskId}`, so two devices cannot double-link. The link keeps the meeting's title, start and link, so another device can show "From meeting …" without the cache. Task and link are two transactions: a crash between them leaves a task without its link, and repeating the same operation completes it.
- **Refresh.** A 15-minute timer, first tick 20 s after start, runs only while at least one enabled feed exists (a tick with none makes no request: tested with a fake client that counts calls). Network and parsing happen outside any transaction; the window is replaced and the state recorded in one. A failure records the error and keeps the cache. A source removed while its fetch was running is not resurrected. Restore and reset wait for a running refresh (`RunPausedAsync`).
- **HTTP client.** HTTPS only, conditional GET (`If-None-Match`, `If-Modified-Since`), 10 MiB cap while reading, 30 s timeout, at most 3 redirects with every hop checked for HTTPS; automatic redirects are off. Error messages never contain the address.
- **Summaries.** "Include meetings in summaries" is a synced profile column (`IncludeMeetingsInSummaries`, default off, in the `planning` field group). Morning summaries list today's meetings and daily summaries tomorrow's, title and time only. The AI summary (phase 4) must read the same switch.
- **Not done in P5.** Google (P6). Meetings in the weekly summary. A "link to the event" for a feed event whose URL is only in the description is limited to known video hosts.
- **Frontend placement (P5).** Meetings today sits above the My Day plan, not beside it. My Week and the calendar show hours net of meetings while availability editing keeps the configured hours. Join/Open links appear only for https and open through the external-link RPC. Removing a calendar is confirmed inline. The add form sends no owner email; it is set on the calendar row afterwards. The "From meeting" line shows in the task editor only.

## 2026-11-16 (PLAN2 P6, Google Calendar)

- **Scopes confirmed against Google's scope table (2026-11-16).** `https://www.googleapis.com/auth/calendar.calendarlist.readonly` lists the calendars and `https://www.googleapis.com/auth/calendar.events.readonly` reads their events; `events.list` accepts the second. `openid email` is added so the sign-in's id token says which account signed in (subject and address), which avoids a third calendar scope or a profile call. No write scope is requested anywhere.
- **The windowed refresh is confirmed.** The `events.list` reference says `timeMin`, `timeMax`, `orderBy`, `q`, `updatedMin`, `iCalUID` and the extended-property filters cannot be combined with `syncToken`. A window of −7 to +56 days therefore cannot be synced incrementally, so every refresh asks for the whole window with `singleEvents=true` and replaces the calendar's cached rows in one transaction. That also makes a declined or cancelled event disappear on the next fetch with no delete handling.
- **Plain REST over `HttpClient`, not `Google.Apis.Calendar.v3`.** The plan names the Google client libraries; this build does not use them. The calls are four small endpoints, the installed-app flow is a few dozen lines, a fake `HttpMessageHandler` makes the failure states testable without a library's internals, and above all no library token store (the default writes refresh tokens to a file) can be in the picture. No new package was added. `GoogleCalendarClient` returns the refresh token to the caller and never stores it; the caller puts it in `ISecretStore`.
- **Sign-in.** System browser, a `TcpListener` on `127.0.0.1` with an OS-chosen port, PKCE (S256), a random `state` checked in constant time, `access_type=offline` and `prompt=consent select_account` so Google always returns a refresh token and lets the person pick the account. Three minutes to finish, then the attempt is abandoned. Favicon and stray requests to the listener get a 404 and are ignored.
- **Client id and secret** come from git-ignored `src/Chaos.Desktop/google-oauth.json`, copied beside the exe when present (`google-oauth.example.json` is committed). Without it `IsConfigured` is false, Settings explains instead of offering Connect, and no request is ever made.
- **Migration 029, not 028.** `GoogleAccounts` (synced; id `googleaccount:{subject}` so two devices that connect the same account offline converge) and `GoogleAccountState` (device-local: needs sign-in, last attempt, last success, last error). The chosen calendars are the P5 `CalendarSources` rows with Provider Google, `AccountRef` the account id and `ExternalId` Google's calendar id; their ids are `calendarsource:google:{accountId}:{calendarId}` for the same reason.
- **Choosing is switching on.** The first choice creates the source; un-choosing sets `Enabled = false` and drops the cached rows, so the colour and busy choice survive. Only disconnecting tombstones sources.
- **One click per device.** The account row syncs, the token does not. A device that knows the account but has no token shows "Google Calendar: sign in on this device." with a Sign in button; one sign-in there fills the same account.
- **Timing.** The scheduler ticks every 5 minutes (first tick 20 s after start) and refreshes only what is due: Google after 4½ minutes, a feed after 14½ (a 30 s slack so a tick never just misses). Window focus calls `calendars.refresh` with `onlyDue`, so focus and the timer cannot double the requests. A manual "Refresh now" always fetches.
- **Failure states.** `invalid_grant` or a 401 that survives one fresh access token is "Google Calendar: sign in again." (the account is flagged and the cache kept); an admin-policy refusal or a 403 that is not a rate limit is "blocked"; network errors are "offline"; a rate limit or 5xx is a plain "will try again". When an account is signed out, blocked or offline, its other calendars in the same run are marked without another request. Messages never contain a token or the client secret (tested).
- **Disconnect** tombstones the account and its sources (synced, so every device drops them), deletes the cached rows, revokes the refresh token (a 400 from Google counts as already revoked) and deletes it from the secret store. A revoke that cannot reach Google does not stop the disconnect; Settings then says so and points at the person's Google account permissions page.
- **Declined, cancelled, transparent.** Mapped as in the plan: `status == cancelled` and an attendee with `self == true` and `responseStatus == declined` are dropped, `transparency == transparent` is free time. The video link is `hangoutLink` or the first `video` entry point; links that are not https are not kept.
- **Settings.** The feed list shows feeds only; Google accounts and their calendars have their own section under Settings → Calendars (colour, busy flag, last updated, plain errors, Choose calendars, Disconnect with an inline confirm).
- **Not done: the live check (release gate).** Connecting a SilverKey account in the installed app needs the Internal OAuth client from the SilverKey Google Cloud project, an interactive desktop and a real Google account. See the progress note under P6.

## 2026-11-17 (PLAN2 P6a, activity log and pushed UI refresh)

- **Migration 030.** `ActivityLog`, `ActivityLogEntities`, `ActivityCursors` and `JobRuns`, all `LocalOnly` in the catalog and listed in `JsonArchive.ExcludedTables`. `MaxKnownSchemaVersion` is 30. Device-only writers need no extra table: their notes live on the write session (`SqliteDataSession.Activity`) and are read by the capture inside the same transaction, so a rollback discards them.
- **Capture.** `ActivityCapture` runs in `SqliteApplicationStore.ExecuteAsync` before `ISyncCapture.Flush`. It reads the distinct `(Kind, EntityKey)` pairs from `SyncPending` (the triggers fire whether or not sync is enabled), looks each row up for its `Version` (null when the table has none), and writes one row plus entities. A pended row that no longer exists is `Op=delete`; a row deleted and re-inserted under its key is an upsert. A transaction with no changes writes nothing. The store tells `IActivityNotifier` the new head after commit.
- **The scope helper.** `ActivityScope.Rpc("service.method")` is the first line of every `[NeoRpcMethod]` (240 methods plus the three `activity.*` ones), added mechanically. A guard test reads the service sources and fails on a method without it, and a reflection test fails when an RPC method is missing from `ActivityActions` (or the list names a method that is gone). Undo and import open their own source (`undo`, `import`) in place of `user`. Scheduler passes use `JobPass.RunAsync(runs, source, action, pass, ct)`: a `JobRuns` row, a `schedule` (or `sync`, for GitHub) scope that stamps `JobRunId`, `Failed` plus a failure `ActivityLog` row with no entities on an exception, `Noop` when the pass wrote nothing.
- **Strict mode is per async flow, not process-wide.** The plan says an unscoped write throws in tests and Debug builds. Hundreds of existing tests write through commands with no scope, so a global switch would break them without testing anything new. `ActivityScope.UseStrict()` turns it on for the current async flow (new tests, the scheduler test), `ActivityScope.StrictByDefault` is the process-wide switch for a later cleanup, and Release always records an unscoped write as `system` / `unscoped`. Strict mode also rejects an action name outside `ActivityActions`.
- **Summary entities of device-only tables.** `calendar_source/<id>` for a refresh, a state save or a cache delete (not one entity per `CalendarEvents` row); `google_account_state/<accountId>`; `notification_job/<id>`; `saved_view_match/<viewId>` for any change to a view's baseline; `view_alert/<id>`. Remote apply (7.4) will note every applied entity with `Source=sync` when P13 lands; there is no applier yet.
- **Retention.** The backup check's timer (every 30 minutes, first check after 2) prunes. Rows go only when they are older than 7 days and no registered cursor still needs them; the hard cap is 30 days or 200,000 rows, whichever comes first, and wins over a stalled cursor. The pruned-through position is kept in `DeviceSettings` (`ActivityPrunedThrough`) so `since` can answer `reset`. `JobRuns` keeps the last 200 runs per job.
- **Delivery.** `ActivityPublisher` coalesces head and job notices into one `activity.committed { seq }` event per 100 ms. The event carries no data. A job start or finish also pushes it (no data changed) so the sync indicators learn about running jobs; a pass that starts and ends inside one window is never seen as running, so a 5-minute calendar tick with no calendars does not flicker the logo. The event is declared with `[NeoRpcEvent]` on `ActivityEvents.Committed`; the generated names are `AddActivityEventsCommittedEvent` (host) and `activityRpc.onCommitted` (frontend).
- **`IChangePublisher` is deleted.** It was declared and never implemented or called. `ChangeNotice` and `ChangeCollector` went with it. The activity log replaces it.
- **Frontend.** `activity.ts` holds one hub per window: it subscribes, reads the head, and on each push pulls `since` until a short page. `useActivity(kinds, handler)` receives the entries that touch its kinds, and every subscriber on `reset`. `useJobs()` and `useJobChange(job, handler)` follow job state. While subscribing fails (outside the host) the handlers run on window focus instead. `App` subscribes to every kind and reloads the open view, lists, counts and undo offer for entries not written by this window (`Source` other than `user`), because its own edits already call `changed()`.
- **Polls removed:** `SyncLogo` (4 s), `GitHubSettings` and `GitHubTaskNotice` (1 s while syncing), `AlertStack` (reminders), `SummaryPrompts` and `ViewAlertStack`. `App`'s 1 s shell-activation poll stays (not a data change).
- **Known edge.** A summary prompt that becomes visible only because quiet hours end, with no data change, now shows on the next push or focus rather than within a minute.
- **Not done here:** the native check (My Day updating from the reminder engine or a GitHub sync with no focus change, the logo animating with no status poll) needs the desktop app; see the progress note under P6a.

## 2026-11-17 (PLAN2 P7 and P8 deferred)

- **P7 (AI providers, keys, privacy, summaries) and P8 (AI-drafted week plans) are deferred by the owner.** AI is optional, sits against the offline, local-first promise (section 2.5), and the cost is mostly plumbing (adapters, key storage, privacy screens) for a narrative summary. Skip both when working through the packages; migration 029 is not used for AI settings (029 is already taken by Google accounts, so a revived P7 needs the next free number).
- **P11 no longer waits on P7.** `ISecretStore` was delivered in P5 over DPAPI, which is all the app lock needed from P7.

## 2026-11-17 (PLAN2 P10, pad drawings, photos and voice notes)

- **Migration 032, not 031.** The plan numbered it 031, but 030 went to the activity log and 031 to attachments. `ScratchPadItems.Kind` (0 text, 1 drawing), `PadDrawings` (synced, keyed by the pad item, no tombstone: it lives and is purged with its item), and `ScratchTaskLinks.RegionJson`. `MaxKnownSchemaVersion` is 32.
- **Stroke format.** `{"v":1,"strokes":[{"tool":"pen|highlighter","color":"#rrggbb","size":n,"points":[[x,y,pressure],...]}]}` in drawing units (default canvas 1600x1200, 1..20000 per side). The server checks the shape and limits only (4 MiB, 20,000 strokes, 1,000,000 points); the renderer owns how ink looks. `perfect-freehand` 1.2.2 (MIT) turns points into outlines.
- **Sync groups.** `strokes` (StrokesJson, Width, Height, CreatedAtUtc) merges whole, newest clock wins, as the plan says; `preview` (PreviewAttachmentId) is its own group so a peer can show the picture first. `Revision` (the autosave counter) and `Version` stay local, like `DraftVersion` on text notes. `Kind` is in the item's `create` group.
- **Autosave** follows `padAutosave.ts` in spirit: a save applies only when its editor revision is newer than the stored one, so a delayed or repeated save never replaces newer strokes. Strokes save 0.7 s after the last change; the PNG preview is rendered and uploaded after a 4 s pause and when the note closes. A failed save keeps the strokes on the canvas with a Retry.
- **The preview is an ordinary pad attachment** (`drawing-preview.png`, owner kind 3). A new preview tombstones the previous one in the same transaction (no undo entry for that), so backup, purge, sync and export need no special case. The commit of each preview is still a recorded attachment operation, so an undo list can show a "add attachment" step for it; accepted.
- **Regions** are fixed rectangles in drawing units (`{"x","y","w","h"}`), so a drawing never relocates them. `locate` returns `Exact` while the drawing exists and `Missing` once it is deleted, with the region. A region link stores an empty `Quote` and zero offsets. "Make task" from a region opens quick add with an empty title (no handwriting recognition until phase 4).
- **Undo inside the canvas** is session-only and covers strokes, erases, undo and redo; it does not touch the app's undo stack. One erase gesture is one step.
- **The eraser is stroke-level** (the plan's wording): it removes every stroke within 10 units of the pointer.
- **White paper.** The canvas is always white so the palette reads in dark mode; the PNG preview matches.
- **Voice notes.** P0 spike 2 (microphone in the WebView) was never run (it needs an interactive desktop), so there is no proven path. The recorder uses `getUserMedia` and `MediaRecorder` behind a small engine interface, with plain messages for unsupported, denied, missing and busy microphones and "Attach an audio file" as the way out. If the native check shows the WebView cannot record, the C# `MediaCapture` fallback from the plan is the next step. Format: WebM/Opus where available, else MP4 (`.m4a`) or Ogg; all three are sniffed and previewed by P9.
- **Photo notes** come from a file picker (no desktop camera). The note is made, the file attached, then the text set; if the file cannot be stored the empty note is deleted again.
- **Export.** Markdown writes `kind: "drawing"` front matter, the preview image link and `<note>.strokes.json` beside it. The JSON archive needs no change: it round-trips every table, so `PadDrawings` comes back with the item and its preview.
- **Not done here:** the native check (draw with a pen, make a task from a region, record a voice note, restart) and the audio fallback if recording is blocked in the WebView.


## 2026-11-17

- **"New site" on the Personal pad uses a local Ollama model.** `SiteNoteCommands` fetches a page
  (`HttpSitePageFetcher`: http(s), 5 MiB, 30 s), reduces it to text (`HtmlText`), and asks a small
  open model through Ollama's `/api/chat` with a JSON schema (`OllamaSiteSummarizer`, default
  `qwen2.5:3b` at `http://localhost:11434`; override with `CHAOS_LLM_URL` / `CHAOS_LLM_MODEL`).
  The note is stored at once with the address and a "Summarizing in the background" placeholder; the pad polls until the background reader replaces it (or leaves the address with a short failure note and `reading/unread` when the site refuses). It never overwrites a note the person edited meanwhile. The answer (summary, read/skim/skip verdict, reason, up to 3 topic tags) becomes one pad note;
  tags are `reading/worth-it`, `reading/skim` or `reading/skip` plus the topics. No model is
  bundled: Ollama is a separate install.
  When Ollama is not running, the model is missing or it gives no usable answer, `FallbackSiteSummarizer`
  falls back to `ExtractiveSiteSummarizer`, which needs no model: the top 3-4 sentences by word
  frequency (in original order), the most frequent content words as tags, and a verdict from length
  and promotional wording. Its reason says it is a quick estimate; it cannot judge originality.

## 2026-11-18 (PLAN2 P12, online service foundation: Aspire from the start)

- **Aspire orchestrates local development, replacing `deploy/docker-compose.dev.yml`.** By owner direction, the online service is built with Aspire from its first commit. `src/Chaos.AppHost` declares PostgreSQL (`postgres:18.7`, persistent container, named volume `chaos-dev-postgres`, database `chaos`), Mailpit (`axllent/mailpit:v1.34.0`, SMTP sink for sign-in codes), a one-shot `chaos-migrate` resource (`Chaos.Server migrate`) and `chaos-server`, which waits for the migrator to complete and is health-checked on `/health/ready`. MinIO and ClamAV join the AppHost in P16, when attachment sync needs them. The compose file in §6.1 is not created.
- **Aspire 17 shape.** `Aspire.AppHost.Sdk` is obsolete in 17.0 (ASPIRE012), so the AppHost is a plain `Microsoft.NET.Sdk` project with explicit `Aspire.Hosting.AppHost` and `Aspire.Hosting.Dotnet` references, and projects are added with `AddDotnetProject(name, path)` rather than `ProjectReference` + `AddProject<T>`. The AppHost therefore does not build the server; `dotnet run` of each resource does.
- **ARM64 Windows: bypass the Aspire CLI run hook.** In 17.0, `dotnet run` of an AppHost hands off to a bundled Aspire CLI. On this ARM64 machine that CLI is the win-x64 build, and it reports "C# AppHost requires .NET SDK version 10.0.100 or later. Detected: (not found)" because it cannot see the arm64 SDK. Setting `ASPIRE_SUPPRESS_CLI_RUN_HOOK=true` runs the AppHost directly and works (verified: containers up, migrator completed, server ready on :5080). `src/Chaos.AppHost/aspire.config.json` was written by that CLI on first run and is committed.
- **Aspire stays out of production code paths.** `Chaos.Server` uses `Aspire.Npgsql` only to register the `NpgsqlDataSource` (with its health check and telemetry) from `ConnectionStrings:chaos`; it runs unchanged without an AppHost, from `ConnectionStrings__Chaos` in the container host (§8.7). `Chaos.ServiceDefaults` holds OpenTelemetry (OTLP export only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set) and maps `/health/live` (process only) and `/health/ready` (every check, including PostgreSQL) in every environment, because the container host probes them in production too. Log scopes are not exported (§8.5).
- **Server tests do not go through Aspire.** They follow §8.8: `WebApplicationFactory` against real PostgreSQL from `CHAOS_TEST_PG`, otherwise Testcontainers `postgres:18.7`, one fresh database per test, failing (never skipping) when neither is available.
- **Server migrations** are embedded `SNNN_description.sql` scripts, checksummed over LF like the client's, each in its own transaction (PostgreSQL DDL is transactional), under a session advisory lock so two migrators cannot race. `Chaos.Server migrate` applies them; otherwise the server refuses to start unless every known migration is applied unedited and the database is not newer (`ServerMigrationRunner.EnsureCurrentAsync`).
- **S001 holds Accounts, AccountLogins, Devices, EmailCodes and AuditEvents** (§8.2), with lower-case email checks and closed value lists for providers and audit kinds. *(Superseded the same day by the EF Core entry below: the OpenIddict tables join S001 through OpenIddict's EF Core stores, and the S-numbers stay as planned.)*

## 2026-11-18 (EF Core on the server, banned on the client)

- **Owner direction: the online service uses EF Core; the desktop side must not.** PLAN2 §2.1, §6.1, §8.1–8.3, §8.7, §8.8, P12–P18 and Appendices A and B are updated. The client keeps Microsoft.Data.Sqlite with numbered SQL migrations, unchanged.
- **What "client side" covers.** `Chaos.Domain`, `Chaos.Application`, `Chaos.Infrastructure`, `Chaos.Sync` (shared with the server, so it stays BCL-only), `Chaos.Desktop`, `Chaos.Desktop.Windows` and their test projects. No `Microsoft.EntityFrameworkCore*` assembly may reach them, directly or transitively.
- **Enforced by the build, not by review.** `Directory.Build.targets` checks every project's resolved references after `ResolveAssemblyReferences` and fails with `CHAOS001` when one is an EF Core assembly, unless the project sets `ChaosAllowEfCore=true`. Only `Chaos.Server` and `Chaos.Server.Tests` set it.
- **Why the server gets EF Core.** OpenIddict ships EF Core and MongoDB stores only, so the no-EF-Core rule would have meant hand-writing and securing its token stores. EF Core also gives the server typed queries, `ExecuteUpdate`/`ExecuteDelete` for account deletion and pruning, and migrations generated from one model.
- **Server rules** (PLAN2 §8.1): one `ChaosDbContext` registered with Aspire's `AddNpgsqlDbContext`; explicit `IEntityTypeConfiguration<T>` mapping to the §8.2 names; code migrations named after the S-numbers (`S001_Accounts`, ...), reviewed as SQL before commit and never edited once applied; `Chaos.Server migrate` applies them and web startup never migrates; startup refuses pending or unknown migrations; a guard test on `HasPendingModelChanges()`; no lazy loading; no InMemory or SQLite provider, tests use real PostgreSQL with the real migrations; push transactions run inside the execution strategy, with the account row locked through `FromSql ... FOR UPDATE`.
- **Versions: EF Core follows the newest stable Npgsql provider.** On 2026-11-18 Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3 (stable) needs EF Core `[10.0.4, 11.0.0)`, and the 11.0 line is only at rc.3, which pins EF Core 11 rc.3 exactly. OpenIddict.EntityFrameworkCore 7.9.0's net11.0 asset needs EF Core Relational `>= 11.0.0`, so 7.9.0 with either provider fails restore with NU1107 (checked in a scratch project). OpenIddict 7.8.0 (net10.0 asset, EF Core 10) with Npgsql EF 10.0.3 and Aspire.Npgsql.EntityFrameworkCore.PostgreSQL 13.6.5 restores cleanly on net11.0 with warnings as errors. That set is pinned when the code moves to EF Core; all move to the 11 line together once Npgsql 11 is stable.
- **The existing hand-written runner goes.** `ServerMigrationRunner`, `Migrations/S001_accounts.sql` and `Aspire.Npgsql` are replaced by `ChaosDbContext`, the EF Core `S001_Accounts` migration (now including the OpenIddict tables) and `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL`. Nothing was deployed, so S001 is regenerated rather than amended.

## 2026-11-18 (Mobile web after P16)

- **Chaos.Server serves the mobile PWA at `/app`.** This is the phone-client follow-on to P16.
  Use the existing server on a trusted HTTPS origin, public or private.
- **Today and lists are the first release.** Task detail shows synced notes, dates and status.
  Online add, complete, reopen and plan-today produce normal sync changes. Recurring task completion
  and reopening use the desktop. The mobile app can read its saved views offline.
- **Web sessions appear as revocable devices.** The existing sign-in pages issue an HttpOnly web
  cookie with sliding expiry. Web writes require antiforgery validation. The service worker caches
  only public assets. Offline views live in browser storage and are tied to the session's device ID.
  The readable offline device marker has no authentication authority.
- **Less3 is the S3-compatible blob store for the sync server.** `S3BlobStore` (AWSSDK.S3 4.0.21) implements `IBlobStore`
  and is chosen when `Blob:S3:ServiceUrl` is set; otherwise the folder store stays. Settings: `Blob:S3:{ServiceUrl, Bucket,
  AccessKey, SecretKey, Region, ForcePathStyle, Prefix}`. The SDK's default CRC32 trailers are switched off
  (`WHEN_REQUIRED`) because S3-compatible stores do not all accept them. The AppHost runs `jchristn77/less3:v4.1.0` with
  `src/Chaos.AppHost/less3/system.json` (SQLite, signature checks off, volume `chaos-dev-less3`), waits for it, and passes the
  server its endpoint and the seeded `default` bucket and `default`/`default` keys. Development values only. Verified against a live
  Less3 container (put, ranged read, delete, prefix delete).
- **ClamAV runs in the AppHost as `clamav/clamav:1.5.5-debian`.** Only the `-debian` tags are multi-arch; the plain `1.5`
  and `1.4` tags have no arm64 image. The server gets `Clam__Host` and `Clam__Port`; until clamd has loaded its signatures
  `/health/ready` reports unhealthy and uploads are retried by the device (a scanner that is down is never "clean"). The clamd
  client is checked against a real daemon by `ClamAvLiveTests`, opt-in through `CHAOS_TEST_CLAM=host:port`.

[Mobile web plan and deployment notes](mobile-web.md)

## 2026-11-20 (PLAN3 E1)

- Asked whether phase 2 had passed the installed phase 1 checklist (P0/D6) and phase 2 release gate (P20), or whether phase 3 could start before those were complete. The owner answered “yes”; on clarification, the owner selected **“Phase 2 has passed”**. This is the owner's confirmation, not an individual record of the checklist results. The existing acceptance document still contains unrun entries; no waiver was stated.

## 2026-11-20 (PLAN3 Q0 live-check scope)

- Asked whether Claude Code, Claude Desktop, the SilverKey Internal Google OAuth client and an installed Chaos MSIX were available for Q0's live checks. The owner replied: **“remove this part from the check. They are not relevant.”** Q0 no longer requires those Claude client checks, the Google account spike, or the installed-MSIX execution-alias check. The MCP SDK, test-client OAuth, unpackaged pipe relay, diff3 and PostgreSQL spikes remain in Q0. This decision is scoped to Q0; later package acceptance criteria are not asserted as passed.

- For the Q0 HTTP spike, use a preconfigured public OAuth client with authorization code and PKCE. OpenIddict 7.8 in degraded mode can issue the tokens with custom client/redirect validation. This proves the local protocol path only; Q19 must implement persistent registration, consent, transport security and authorization rather than reuse the experiment's validation handler.

- Q0 originally required both no product changes and a working desktop `--mcp` relay. Asked how to resolve that conflict, the owner chose **“Allow the desktop MCP relay to be implemented in Q0”** on 2026-11-20.

- The Q0 relay uses MCP SDK 1.4.1 over a separate `CurrentUserOnly` named pipe per data root. The desktop `--mcp` shim relays stdio, and starts a windowless, one-session protocol host when no listener is available. The host advertises an empty tool list and reads or writes no Chaos data; agent identities, token authentication and actual tools remain Q17/Q18 work. The fallback is a headless protocol host, not a fully initialized desktop application; opening the desktop after a fallback does not reuse its application state. Cross-user denial still needs a native check under another Windows account.
- Asked whether another signed-in Windows account was available for the cross-user ACL check, the owner answered **“No, leave the cross-user check blocked”** on 2026-11-20.

## 2026-11-20 (MCP SDK update)

- At the owner's direction, update `ModelContextProtocol` and the Q0 MCP experiments from 1.4.1 to 2.4.0. The ASP.NET Core experiment uses matching `ModelContextProtocol.AspNetCore` 2.4.0. This supersedes the 1.4.1 pin in the preceding Q0 entry; the relay's scope and remaining limitations are unchanged.

## 2026-11-20 (Q0 waiver and Q1 start)

- The owner waived the cross-user pipe check and directed work to continue with Q1. The check has not been performed; this waiver does not establish that another user is denied. The owner also directed that the recorded web contract snapshot failure be disregarded. The previously reported intermittent desktop test remains recorded in PLAN3.
- Q1 routing metadata may identify a parent in a named field group, including `github_issue_link.state.TaskId`. The owner chose this on 2026-11-20 because the existing link stores the task in `state`, not `create` or its key. Q3 must handle a first change without that group as `parent-unknown`; existing location wins for subsequent changes, including edits to `state`.
- When an offline data root adopts an account's canonical profile, its pre-binding `person:<localProfileId>` history actors are rewritten to the canonical profile ID with the other adopted references. Existing `local` rows are not rewritten. Q1 displays person actors as the local person; shared-member name resolution belongs to Q4.

## 2026-11-20 (PLAN3 Q2 account streams)

- S005 keeps the existing sync rows and keys by renaming their database `AccountId` column to `StreamId`, and copies each account's sequence and pruning horizon into a stream whose ID is the account ID. New accounts create their stream in the same save. The existing EF row property remains named `AccountId` for Q2's unchanged account-only callers and tests; it maps to the `StreamId` column and must be renamed when Q3 adds space streams.
- Q2 routes every accepted change to the account stream and retains the existing Personal-space ownership check. Account rows remain locked as well as stream rows to preserve synchronization with account deletion and calendar cache writes. Blobs remain account-keyed until Q6.

## 2026-11-20 (PLAN3 Q3 spaces on the server)

- S006 adds a `MembershipVersion` counter to `Accounts` as the monotonic account-level version used by `/v2/streams` and membership SSE events. Canonical profile IDs are unique while non-null; a second account cannot bind an already-owned actor identity and an invite never reassigns an existing member row to another account.
- `Chaos.Sync.SyncKinds.All` remains the client-catalog contract. Q3 adds the server-only `space_member` kind and the schema-4 `space` groups through the server's supplemental catalog, so Q3 can generate and serve membership data without declaring the Q4 client schema available.
- PLAN3 E8 values are server configuration under `Sharing`: 50 people and 10 agents per space, 100 owned spaces per account, 50 open invites per space, 20 invite emails per account per day, and 20 invite accepts per hour per account and per IP.
- Account deletion now scrubs every shared-space member row for that account, including rows already removed: the display name becomes “Former member” and the GitHub login is cleared. A member row keeps its actor and removed timestamp while `AccountId` becomes null. A last owner with other members must transfer ownership; a space with no peers is deleted with the account.
- When an account has no display name, invitations and member rows use the neutral text “A member”; the person's email is never exposed to another member or placed in an invite body as a fallback.
- Shared attachment bytes remain owned and quota-counted by the uploader, but Q3 authorizes a read by a live
  reference in any readable account or space stream and treats references in every live stream as blob-sweep
  references. Space purge immediately removes newly unreferenced blobs and objects while retaining blobs still
  named elsewhere. This preserves P16 account storage keys without exposing another account's data.
- Space membership mutation lock order is stream, then all members by member ID; account deletion uses
  affected streams, all their members, then the account row. This serializes last-owner checks and prevents
  concurrent demotion/removal or invite acceptance from producing an ownerless space.
- For an existing `diary_entry`, stored authorship is authoritative; an incoming `create` group cannot rewrite
  `AuthorProfileId`. A `HasSpace` entity's `create.SpaceId` must equal its current route and can change only
  through the server move operation.

## 2026-11-20 (PLAN3 E10 and Q3 matrix scope)

- **E10 confirmed.** The owner confirmed the plan's default: a deleted account's contributions stay in each shared space, because they belong to the space. The member row's display name becomes "Former member", and the account's email and logins are deleted as before.
- **Deferred 8.4 matrix evidence.** The owner directed that the 8.4 cells that need later packages are proven in those packages, not in Q3. The Agent column and the "Claim a task" row are proven in Q17–Q19, which add agent members, tokens and the claim operation. The "change ... states" part of the space-settings row is proven in Q9, which adds `space_state`. Until then the server refuses those kinds and groups from clients. Q3 is complete when every other cell passes, and each of those packages must add its rows to `PermissionMatrixTests` before it can be ticked.

## 2026-11-20 (PLAN3 Q3 attachment blob custody)

- A shared attachment reads the blob of a server-recorded custodian, never "any blob with this hash". `S007_AttachmentBlobBindings` keys `(StreamId, AttachmentId)` to `Sha256` and `CustodianAccountId`. The custodian is the account whose push won the attachment's `file` group in a space stream, or the account that moved the attachment in from its own stream. Account streams have no bindings, because only the account's own devices write them. Bindings cascade with their stream and with the custodian account.
- An attachment push is not refused because another account holds the same hash. Such a refusal would break the desktop, which pushes metadata before uploading the bytes, and it would tell any user whether a file exists on the server.
- The migration binds an existing space-stream attachment only from the actor of its latest logged change that wrote the current hash. Where that change was pruned, the attachment stays unbound and unreadable to others until its `file` group is written again.
- Moving custody when a member leaves or deletes their account, and the matching quota rules, belong to Q6 under E9. They are not decided here. PLAN3 Appendix A shifts Q6 to `S008` and Q17 to `S009`.

## 2026-11-20 (PLAN3 Q3 security review fixes)

- Invite links come from `Sharing:PublicBaseUrl`, never from the request's Host header, which the caller chooses. Outside development the server refuses to start without it. A development host without it still uses the request address. The value must be an absolute http or https URL with no query, fragment or credentials.
- In a space stream, `task_history` is append-only and names its writer. `record.Actor` must be the pusher's own `person:<profileId>`. Only an owner may record a `github:<login>` actor, because only owners manage the GitHub connection. `local`, `sync` and `agent:` actors are refused until Q17 adds agent members. A stored record cannot be changed; an identical re-send is accepted. Account streams keep their existing rules.

## 2026-11-20 (PLAN3 Q4 migration mechanics)

- Migration 038 rebuilds tables referenced by other tables. SQLite retains deferred violations after dropping such a parent even when the replacement has the same rows and `foreign_key_check` is clean. The migration runner disables foreign-key enforcement before this migration's transaction, checks the entire graph before commit, and restores the previous connection setting even on failure. The migration retains the GitHub diary trigger; sync capture triggers are reinstalled from the changed catalog. Server-written membership rows do not produce local outbox changes.
- Personal records can refer to work that later moves into a shared space. Their server tombstones and other personal history must survive eviction even after the shared target is removed, so 038 also removes only those target foreign keys from personal overlays, imported-item references and week-goal project references. It preserves their other constraints, indexes, rows and hand-written triggers. This permits a local account-stream row to retain its reference to an absent shared entity; application commands and sync routing remain responsible for resolving live targets.

## 2026-11-20 (PLAN3 Q4 client sharing engine)

- The desktop now speaks only protocol 2 and advertises client schema 4. Every push goes to `/v2/sync/push`, and the service routes it. The account stream's cursor lives in its `SyncStreams` row; `SyncState.Cursor` is left in place but unused. Joining or leaving an account clears `SyncStreams`, so the next discovery starts every stream over.
- Discovery (`GET /v2/streams`) runs on the first cycle, after a `membership` event whose version differs from the last discovery, after a `not-member` rejection or a 403 on a stream, and otherwise every 10 minutes. Streams are pulled account stream first, then each space stream in ID order.
- Eviction deletes the space and everything that routes to it, found by walking the routing table from the space. It skips every `SyncPurgeSql` statement that targets an account-stream or tag-link table: those rows are personal overlays, and the service's tombstones remove them on every device of the account. Clocks, parked changes, pending captures and unsent changes for the evicted keys are deleted in the same transaction, and the attention item counts the unsent changes.
- A `moved` change whose target this device cannot read evicts that entity and its dependents locally (§7.6). If the target is a space the device has not discovered yet, the later snapshot brings the work back.
- Rejections: `forbidden` and `server-only` drop the change and raise attention. The client does not yet force the server's value back; the next remote change to that group corrects it. `not-member` triggers discovery and eviction. `parent-unknown` and `moved` stay in the outbox and are retried next cycle; `parent-unknown` raises attention after 10 refusals in one app session. `rate-limited` ends the cycle with the normal backoff.
- My Day accepts tasks from shared spaces this device holds, because the day item is a personal overlay.
- Restoring a JSON archive turns shared spaces into Personal copies with new IDs and no membership (see `docs/data-transfer.md`). IDs are rewritten in every TEXT column of every ordinary table; IDs inside JSON payloads (undo entries, saved filters) are not rewritten.
- Released (protocol 1) desktops on an account that uses sharing (owner chose option C). A protocol 1 pull receives each `moved` row as a `purge` with the same change ID and sequence, so the moved work leaves that desktop and it stays personal-only. A protocol 1 push to moved work, or to anything that routes to a space stream, is refused with `schema-too-old`, which the released build shows as "Update Chaos to keep syncing." The released build then stops pushing for the rest of that session; its pulls continue.

## 2026-11-20 (PLAN3 E9 shared attachment quota)

- The owner confirmed the plan's default: each shared attachment blob counts against the uploader's quota.

## 2026-11-21 (Q6 viewer diary attachments)

- A Viewer may write attachment metadata only for their own live diary entry. Existing attachment ownership cannot be changed by the Viewer. An explicitly scoped Viewer upload supplies the attachment ID; each request checks the live membership, stored diary author, current hash and server-recorded custodian. Stream-keyed storage and desktop routing remain Q6 work.

## 2026-11-21 (Q6 attachment downloads)

- Desktop downloads select stream IDs from live local attachments: Personal uses the account stream and shared attachments use their space stream. When a hash is attached in several streams, a 404 advances to the next stream; other errors retain their existing behavior. The device-local bytes are still cached by hash. Upload state and storage remain account-scoped pending S008.

## 2026-11-21 (Q6 device-local stream state)

- Client migration 039 adds `AttachmentStreamState` keyed by stream and hash. The legacy hash-only remote status may have come from a different stream, so migration seeds account-stream rows as unknown and does not infer shared-stream status. Local byte state remains keyed by hash. Account disconnect clears these rows; space eviction deletes that space's rows. The upload engine does not yet use the new table; S008 and stream-aware uploads remain unfinished. Later provisional client migrations move up one number.

## 2026-11-21 (Q6 upload-session migration)

- Split the provisional S008 into `S008_StreamUploadSessions` and `S009_StreamBlobs` so each data transformation can be reviewed and tested without editing an applied migration. Agent migration moves to S010. S008 rekeys upload sessions and parts by stream and hash, preserves existing Personal upload part paths, and restricts each session to its uploader. Shared-upload paths remain under the uploader's part prefix, so account deletion can remove them. Finished blobs remain account-keyed until S009.

## 2026-11-21 (Q6 stream blobs and object lifecycle)

- S008 keys upload sessions and parts by stream and hash, retaining the uploader for ownership and quota. S009 keys blob references by stream and hash and introduces separately owned objects. The migration retains Personal objects and copies shared references only when recorded live custody identifies them. Moving a blob adds a reference to the same object; its uploader is charged once. Shared references survive the uploader's account deletion.
- S010 and S011 add a durable deletion queue with a deadline. Removing the final reference releases quota and queues the object in the same transaction; failed object-store deletions are retried. A staged write is queued before storage begins and removed from the queue when its blob is committed. Sweep and purge serialize reference removal with stream changes.
- Desktop uploads select the attachment's stream and keep remote upload status per stream and hash. Viewer uploads require the Viewer's own live diary attachment; the service checks membership again when committing an upload. New attachments inherit their parent entity's space. Q17's provisional server migration moves to S012.

## 2026-11-21 (Q7 assignment eligibility)

- Shared task assignments are restricted to live human Owners and Editors. Viewers cannot be assigned work; agent assignment remains Q17. The service rejects attempts to create or restore an ineligible assignment.

## 2026-11-21 (Q7 delegation sequencing)

- The owner chose to leave shared-space quick-add `+name` combined with `follow up` as text until Q13 supplies delegation. Q7 may implement ordinary member assignment and Personal waiting without advancing Q13.
## 2026-11-22 (Q8 shared notifications)

- Due-soon notices are offered within 24 hours of the due instant for tasks assigned to the member or without assignees; they are off by default.
- The owner default deadline reminder stores an offset in minutes in `DefaultDeadlineReminderJson`. Derived jobs use a separate local notification source and never create reminder rules.

## 2026-11-22 (Q9 state and tag routing)

- Client schema 7 adds `space_state` and makes a tag's space nullable. A status group from a pre-Q9 client clears the custom state because it selects a base status; a received custom state uses that state's base status. Deleting a state clears its task references on each applying device, including when a concurrent task change arrives after the deletion.
- A shared list's optional tag-sharing step runs after the server move in one local operation. It copies matching personal tag paths to the destination space, relinks task and subtask tags, and is undoable. If the space list is locked, an editor can reuse an existing space tag but cannot create a missing one. The move dialog reports a failed tag-sharing step separately because the server move has already succeeded.
- When a list moves to another space, a task's original custom state is cleared while its base status is preserved. The server writes a status clock newer than the original so the mover's desktop and the destination members receive the same result.
- The server validates that a personal tag names the caller's profile and a shared tag names the target space, with exactly one scope. GitHub label mappings for shared tasks use space tags; a personal tag on a moved task is not treated as a shared label.

## 2026-11-22 (Q10 shared view fields and alerts)

- D7's Personal-only restriction is lifted: GitHub fields can be used in a shared view, based on the GitHub data available in that space.
- Migration 043 moves each member's view-alert preference to an account-stream overlay. Shared views retain a disabled legacy alert field; per-device matches and delivered alerts remain local. Existing Personal view settings are seeded into overlays.

## 2026-11-23 (Q11 shared pads and pages)

- **Merge base.** `TextBase` (migration 044, device-local) holds the text last agreed with the server for a pad's text, a page's text and a task's notes. It advances when a remote text group is applied and when the service acknowledges a pushed local change, using the text in the pushed payload. A remote text is merged with the local text when the local text differs from the base. That includes a remote change with an older clock than the local one, provided its clock is newer than the base: two people edited at once, and last-clock-wins would drop one edit. A remote clock not newer than the base is a replay and is ignored. A row with no base (written before this version) still merges by last clock.
- **Result and labels.** The merge result replaces the winner's value and queues a normal pending change, so it travels under a fresh clock. The page's replaced local text stays in Versions and the remote text is added as a version. The two sides are ordered by clock, and a conflict block is labelled with each author's display name (a short device mark if unknown): `<<<<<<< Ada` / `=======` / `>>>>>>> Bo`. PLAN3 9.9 wrote `yours`/name, which would differ between the two devices and make them rewrite each other, so labels are the same on both. The renderer uses `yours`/`remote` because it has no author names.
- **Diff3.** `Chaos.Domain.Text.Diff3`: line based, CRLF and a missing final newline preserved. A comparison too large for the exact line diff (over 4 million cells after trimming) becomes one block that conflicts instead of throwing. The renderer calls it through `text.merge3`.
- **Drawings.** Version 2 is `{"v":2,"strokes":[{"id","createdAt",…}],"erased":[ids]}`. The merge is a union by stroke ID minus the union of erased IDs, ordered by `(createdAt, id)`, and needs no base. A v1 drawing is read with IDs `legacy-0`, `legacy-1`, … so both sides agree on them; the renderer upgrades a v1 drawing on its first edit. The applier merges when both sides differ and queues the union as a new change unless one side already holds everything. Undo records the removed stroke as erased and redo clears it.
- **Presence.** `POST /v2/presence` and `DELETE /v2/presence`, in memory only. Reports expire after 45 seconds, a sweep every 5 seconds tells the room, and a change goes as `{"presence":{spaceId,entityKind,entityId,members,editing}}` on the event stream of the space's connected members. Personal entities are never recorded, sent or shown, on the service or the client. Presence is per service process: it is not shared between instances.
- **Not done in Q11.** The task notes editor does not merge unsaved typing in the renderer, and shows no presence line; task notes are still merged by the applier. The same-line labels in a renderer merge are generic.


## 2026-11-23 (Q12 rotating repeating tasks)

- **Model.** `RecurrenceSeries.RotationJson` is `{"members":[ids],"start":n}` (sync group `rotation`, client schema 9). `RecurrenceOccurrences.RotationIndex` is in the `create` group. The first occurrence takes `start`; a successor takes the predecessor index plus 1, falling back to `start` when the predecessor has none. The host is `members[index % count]`, moving on to the next current owner or editor when that member has left. The index itself always advances by one, so a skipped member does not shift the order for others. Skipping an occurrence advances the rotation too.
- **Editing.** Any change to the rotation (members or start) clears the active occurrence's index, because indexes refer to the old member list. The active occurrence keeps its host; the next one uses the new `start`, which is also what the editor shows as "Next: Omar, then Sara".
- **Eligibility.** Rotation members must be current human owners or editors of a shared space (the Q7 assignment rule). Rotation is refused in a Personal space. The successor's only live assignee is the host; stale assignee rows on a revived tombstone are removed.
- **Quick add.** Quick add gets no rotation syntax. Rotation is set in the task editor's Repeat tab.

## 2026-11-23 (Q14 space summaries and standups)

- **Spaces section.** The evening and weekly summaries carry one entry per shared space the person belongs to, built from the space's own history on the device: completions, new tasks and newly blocked work (status moved to Waiting, or a delegation declined) by other people and agents, the open tasks due in the seven days after the period with their assignees, and how each project moved. The person's own work stays in the personal sections and meetings never appear. A space with nothing to say is left out. Summary rows stay personal.
- **Standup.** `summaries.standupDraft` builds Markdown from the person's own work in one space for the summary's period: completed in the period, planned (the open tasks of the space assigned to them) and blocked (their Waiting tasks, with whom they wait on), under headings in the profile's language. Drafting posts nothing; `spaceUpdates.post` sends the text the person kept, and the service checks that the author is the caller. `SpaceUpdates` (migration 047, kind `space_update`, client schema 11): the author alone edits, the author or an owner deletes, and a viewer may post. Standups are not undoable; they leave the device with their space on eviction.

## 2026-11-23 (Q15 GitHub in shared spaces)

- **Owners manage, owners run.** Every GitHub command names a space (null is the Personal space). Creating, changing, pausing or removing a connection or a repository, choosing the runner device and disconnecting require an owner of that space, on the client ("Only owners of <space> manage its GitHub connection") and on the service, where every `github_*` group already needed the Owner role. The engine runs only on a device of a current owner: the service clears the connection's `runner` group when the runner's owner is demoted or removed, so no device of a former member keeps syncing until an owner chooses "Run GitHub sync on this device" again. The scheduler serves every connection whose runner is this device, each with its own cadence.
- **Members' own posts.** The one exception to the Owner rule is a member queueing their own diary entry as a GitHub post: the service accepts a `github_comment_link` from a non-owner only for a diary entry whose stored author is the caller, and only as a post from Chaos (`Origin` 1). The engine's mirrored comments and baselines stay with the runner.
- **Assignees are logins, derived from the task's assignees.** The issue projection compares assignee logins instead of numeric IDs, because a member's login is all the app knows until GitHub reports the ID; the write intent may name any login (`AssigneeLogin`; `SelfAssigned` is the pre-schema-12 form for the connected account). In the Personal space the link's own "assign me" intent remains. In a shared space there is no stored intent set (PLAN3 10.3 wrote of generalizing `assignee_intent`): the runner derives it from the task's `task_assignee` rows, because those rows already sync and are what the member changed. A current owner or editor with a GitHub login who is assigned to the task gets that login on the issue; one whose assignment was removed here (a tombstoned row) leaves it. A member whose login was set after the issue already carried it is left alone until the inbound pass has created their assignee row. Inbound, a merged login that matches such a member creates their assignee row, and a login the merge drops (GitHub removed it, nobody re-assigned them here) tombstones it. Viewers are never matched (the assignment rule), members without a login stay app-only, and logins nobody claims stay visible from the baseline. Migration 048 adds triggers that mark the link dirty when a task's assignee rows change, as tag changes do.
- **Attribution (decision E4).** A comment posted for a diary entry whose author is not the runner's profile ends with `— <member name> (via Chaos)`. The exact line is stored on the comment link (`GitHubCommentLinks.Attribution`, synced) before the post is sent, so a crash between the post and its record still strips it on recovery; every read-back and every compare strips it with the marker, so the diary never gains the line. The owner's own entries are posted unsigned.
- **G9 completed.** `GitHubPullRequestLinks` (migration 048, kind `github_pull_request_link`, Parent `github_issue_link`, Owner write, client schema 12) keeps the pull requests that close each linked issue, with state (open, draft, merged, closed) and review (none, approved, changes requested, review required), refreshed from GraphQL in batches of 50 while issues are fetched and once a day for open pull requests; a failed read leaves the rows and tries again next pass. The baseline gains `authorLogin` and `issueType`. Views gain `githubAuthor`, `githubLinkedPr`, `githubPrReview` ("None" means no linked pull request has a review decision) and `githubIssueType`. A merge import does not carry pull request rows; the runner refreshes them.
- **Privacy warning.** Adding a private repository to a space with more than one member, and inviting someone into a space with a private repository, show "Everyone in <space> will see issues from <repo>, including people without access to it on GitHub." and need a confirmation. Nothing is stored for it.
- **Connections reach members' devices.** `GitHubConnections.ProfileId` loses its foreign key to `Profiles` (migration 048 rebuilds the table; the column and every other constraint stay). It names the owner who connected, who has no profile row on another member's device, and the key parked the connection, its repositories and its links there, so members saw no GitHub fields.
- **Not done in Q15.** Writing under each member's own GitHub identity; verifying a member's login against GitHub (the token belongs to the runner).

## 2026-11-24 (Q16 Google Calendar time blocking)

- **Scope check (PLAN3 §11).** Google's reference for the Calendar API (pages updated 2025-10-28) lists `https://www.googleapis.com/auth/calendar.app.created` as an accepted scope for `calendars.insert`, `calendars.delete`, `events.insert`, `events.patch`, `events.delete` and `events.list`, and `events.list` supports `syncToken` incremental sync (a 410 GONE means "clear your storage and do a full sync without a syncToken"). Q16 therefore asks for that one scope on top of the read-only ones and never for `calendar` or `calendar.events`. The sign-in for it is an incremental consent (`include_granted_scopes=true`, the new scope added to the existing ones); a device without a token signs in with it when time blocking is turned on, and "sign in again" for time blocking uses the same sign-in. The Internal OAuth client (2026-11-15) needs no verification; a public release adds the scope to the consent-screen submission (Q22).
- **Settings.** The choice is the profile's `timeblock` group (migration 049, client schema 13): `TimeBlockEnabled`, `TimeBlockAccountId`, `TimeBlockShowTitles`, `TimeBlockRunnerDeviceId`, `TimeBlockCalendarId` and `TimeBlockSpacesJson` (null: every space; otherwise the space ids). It travels in the account stream, so every device shows the setting; only the runner device makes requests.
- **Event rows.** `TimeBlockEvents` (kind `time_block_event`, Account) is keyed by task: `EventId` is `cb` plus the base32hex encoding of 16 bytes (a GUID task id gives its own bytes, any other id the first 16 bytes of its SHA-256), so every device derives the same id and Google refuses a second insert with 409; `ETag`, `PushedHash` (title, start, end and visibility), `LastSyncedAtUtc` and `RemoteMovedAtUtc` say what was pushed and what Google last answered. A row has no tombstone: turning time blocking off removes the rows, and a removal travels as a purge, as for a label mapping. A task purged here keeps its row until the runner has deleted the event at Google. `TimeBlockState` (device-local) keeps this device's sync token and page token and its last problem.
- **Passes.** The runner runs a pass when a task or assignment changed since the last pass (read from the activity log, excluding the pass's own writes) or every five minutes, and on Sync now. Inbound comes first: a change already in Google is applied to its task, or loses under the conflict rule, before the outbound half would push the task's older time over it; the outbound plan is then read again. An event the app wrote is recognised by its etag, and a change older than the row's last sync is skipped, so a full re-read after an expired token applies nothing of the app's own. Deleting the event in Google clears the task's planned time and zone, keeps the date and writes history `google.cleared`.
- **Conflict rule.** When the task's planned group changed here since the last push (its hash differs from `PushedHash`), the newer of Google's `updated` and the task's change wins; the task's time is the planned group's HLC wall time in `SyncClocks`, or the row's `UpdatedAtUtc` when the device never synced. Google's move writes the task and history `google.conflict` with the local time as the loser in the before snapshot; a newer local change keeps the task, writes `google.conflict` with Google's move as the loser, and the outbound half pushes the task's time back. Every such edit is a normal task update with `Source=sync`, actor `google` and action `timeblock.apply`; the history actor is `google`.
- **Eligible tasks.** Open, not cancelled tasks with a planned date, a planned time and an estimate, in the chosen spaces; a shared task only when a live assignee row names this person. Busy blocks carry no summary (Google shows them as busy); the transparency is opaque either way.
- **Turning off.** Offers "Delete the Chaos calendar in Google", yes by default. Deleting happens at Google before the settings clear, so a Google that cannot be reached leaves the setting as it was. A kept calendar is remembered with its account so a later turn-on for that account reuses it instead of making a second one.
- **Not done in Q16.** The live check with the SilverKey Internal client: no interactive desktop was available to the implementing agent, so it is a release gate (Q22).

## 2026-11-24 (Review of Q14, Q15 and Q16)

- **Q14 completions are transitions into Done.** The spaces section and the standup draft count any history row that moves a task into Done (completed, a delegation reported done or closed), as the personal completions already did, not only `task.completed`. The person's own completions in shared spaces join the personal completions, as the UI and the Q14 entry promised. A shared space stays in the summary when the person completed something there or has open tasks assigned to them, even with nothing from others, so "Post as standup" is always reachable; the entry then says "Nothing from other members in this period." Restoring an archive drops standups with the shared space they belonged to, since the Personal copy cannot hold them.
- **Q15 comment links from non-owners.** The service accepts a member's `github_comment_link` only as the member's own moves: a create from Chaos with no comment id, author, URL yet (or exactly what is stored), and a state of Queued (only while not yet handled), Delete or Detach with the base body, base time and attribution exactly as stored. A column left out counts as null. A member can no longer aim their link at someone else's comment, ask for an overwrite, or post without the attribution line.
- **Q15 assignees and logins.** Within one pass, each assignee or label change is sent at most once, so a login GitHub silently ignores (no access, a typo) no longer repeats a hundred times or blocks the other assignees. Two current members of a space cannot save the same GitHub login. Deleting an account clears the runner of a connection that ran on one of its devices, as demotion and leaving do. Repository listing, lookup and attachment reads refuse non-owners before any GitHub CLI call; an attachment then tells the member to open the issue. A pull-request read that answers NotFound, Gone or a network failure leaves the rows and lets the issue pass finish. "No linked pull request" in views now needs a task from GitHub, like the other GitHub fields, and detaching or removing a repository drops the pull-request rows with the other issue mappings.
- **Q16 turning on and the write scope.** The token from connecting the account is read-only, so turning on retries the calendar creation once after a sign-in for `calendar.app.created` when Google refuses it. The first pass after turning on or signing in again runs through the scheduler's gate, never beside a scheduled pass or a restore.
- **Q16 passes.** A pass whose read of Google's changes fails writes nothing to Google; the next pass does both halves. App events in the calendar with no row and no eligible task (a calendar kept while turned off, a write whose row was never saved) are deleted. A patch carries `"status":"confirmed"`, so an event Google keeps as cancelled under the task's id comes back. A planned time inside a spring-forward gap is placed just after the gap. Time block writes and the change feed retry once with a fresh access token on a 401, like the reads.
- **Q16 turning off and disconnecting.** Turning off checks the version before anything is deleted at Google, and refuses "Delete the Chaos calendar" on a device without the token instead of forgetting a calendar it could not delete. Disconnecting the account that time blocking uses turns time blocking off and deletes the Chaos calendar while the token still works (best effort, like the revoke); the disconnect RPC does not yet offer keeping it.
- **Not done in the review.** Q15: an old tombstoned assignee row still takes a login off an issue when that member sets the login later; a safe fix needs to know when the baseline moved past the unassignment, and a timestamp rule would drop real removals synced from other devices. Q16: changes from Google at or before the row's last sync are skipped by comparing Google's clock with this device's, so a large clock skew can hide a move.

## 2026-11-24 (PLAN3 Q21 mobile web with shared spaces)

- **A scope per request.** `WebReadModel` builds a `ReadScope` for each request from the account's membership version (`SpaceMembershipCache`): the account stream plus every live member space stream with the caller's role, so a removed member's next request no longer sees the space. `GET /v1/web/lists` returns `WebLists`: Personal first, then each shared space by name with its role (`WebSpace`), and every list carries its `spaceId`.
- **Web writes use protocol 2.** The PWA's pushes go through `SyncService.PushPreparedAsync` with protocol 2, so a change to a shared task is routed to its space stream and checked against the caller's role like a desktop's change (8.4). `forbidden` answers 403 with "You are a viewer of this space, so you can read it but not change it. Ask an owner for editor access."; `not-member` answers 404. The web device still registers client schema 1: the PWA only writes groups that exist in schema 1, and `MinClientSchema` applies to it like to any device.
- **Viewer screens reflect the server.** Tasks carry `canChange` (false in a space where the caller is a viewer) and spaces carry `canChange` from the role; the app disables the check box and the complete button with "Viewers cannot change tasks", offers only lists the person may change in the capture dialog, and says the role once per screen ("You are a viewer of this space"). My Day membership stays available to a viewer because it is a personal overlay in the account stream.
- **Events.** `/v1/web/events` now wakes the app on any readable stream (the account's or a member space's) and on a membership change, and sends an ordered tick instead of the account sequence; the app only refreshes, so the sequence carried nothing it used. Sequence numbers of different streams are not comparable, which is why the payload is a tick.
- **Assignees and authors.** Shared tasks carry their assignees (`WebPerson`, names from the server's member rows; the signed-in person is "You"). The task detail carries the task's diary with each author's display name (`WebUpdate`); an entry an owner removed keeps a "Removed by an owner" placeholder, the author's own deletion removes it. Task history rows (field changes) are not shown on the phone.
- **Search and attachments** cover every readable stream for tasks and task images (blob reads in a space go through `FindReadableInStreamAsync`); pad notes and pages stay the Personal ones because the PWA has no shared pad.
- **Known limit.** Complete on the phone writes the `status` group directly; for a task whose assignees must each complete it, this bypasses the desktop's rule that every live assignee completes first. As with recurring tasks, the server does not run client logic; a later change can refuse such tasks or add the assignee `done` write.
- **Contract.** `web-dto.json` is regenerated and `WebContractTests` is green again (the owner had allowed earlier packages to disregard its failure); the test now maps `short` to `number`. `src/Chaos.Web/tsconfig.json` exists only so `tsx --test` resolves the same aliases as `vite.config.js` for the component tests; the Vite build does not read it, and `TaskViews.jsx` imports React because that runner uses the classic JSX transform.