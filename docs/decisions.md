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
  of purged tasks in journal facts and drops undo entries that mention them. It then runs
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
