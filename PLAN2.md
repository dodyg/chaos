# Phase 2 implementation plan — sync, richer views and AI help

Prepared 15 November 2026 from `SPEC.md` (release plan, phase 2 row, and every "arrives in phase 2"
sentence) and the code at commit `a9496e3`. This is a handoff for implementing agents. It does not
claim any of this exists yet. Work through the packages in section 15 in order. Keep their
checkboxes and progress notes current, and meet each package's exit criteria before you start the
next one.

`SPEC.md` is the product authority. This plan makes the choices the spec leaves open; they are in
sections 4 and 5. Record each one in `docs/decisions.md` when you implement it. A later entry in
`docs/decisions.md` overrides this plan, just as it overrode the Phase 1 plan.

The Phase 1 plan (`PLAN.md`) has been deleted. The rules from it that still bind are restated in
section 2. To read the original, run `git show a9496e3:PLAN.md`. Older entries in
`docs/decisions.md` and `GITHUB.md` refer to "PLAN §n". Those references point to that file.

---

## 0. How to use this plan

Read this section before every package.

1. **Read first.** Read sections 1–14 of this file once. Then, at the start of each package, read:
   - the package's own text in section 15
   - the design section it points to
   - every source file it names
   - the newest entries in `docs/decisions.md`
   
   Do not start coding from the step list alone.
2. **One package at a time, in order.** Packages list what they depend on. If you find that a
   package needs something from a later package, stop. Record the problem in the progress note
   and ask the owner. Do not quietly reorder.
3. **Progress notes.** Under each package heading, add a dated progress note in the style used in
   `git show a9496e3:PLAN.md` §15. Say what was done, the real test counts, what was not done and
   why, and the smallest next action. Tick a package's checkbox only when its exit criteria have
   actually been demonstrated. An untested assumption is not done.
4. **Gate after every batch of changes.**
   - `pwsh -File eng/verify.ps1` runs locked restore, the Release build (which includes the
     frontend build), every TUnit project and Slopwatch.
   - Then run `npm test` in `src/Chaos.Desktop/frontend`.
   - From package P12 onwards, `eng/verify.ps1` also runs the server tests (see P12).
   - Record the real test counts. A test run that discovers zero tests is a failure.
5. **When blocked,** write down:
   - the exact API, behaviour or dependency that blocked you
   - what you tried
   - the smallest next action
   
   Then continue with independent work in the same package if there is any. Never replace a
   blocked native capability with something that only looks like it works. For example, do not
   show a "synced" badge when nothing was sent.
6. **Never commit secrets.** That includes OAuth client secrets, API keys, test account passwords
   and signing certificates. Configuration that holds them is git-ignored. Each package that adds
   one gives the file name and an `.example` file to commit instead.
7. **Ask the owner** only about the items in section 4, or when the spec and this plan genuinely
   contradict each other. Everything else has a stated default here. Use it.

---

## 1. Starting point

### 1.1 What Phase 1 delivered

The Windows desktop app is built on .NET 11, C# 15, NeoAstra 1.12.0 with React and TypeScript, and
SQLite. It has:

- tasks, subtasks, templates, tags, Markdown notes, the diary, history and undo
- the personal scratch pad (typed notes)
- Pages and daily pages, with front matter and scene/colour backgrounds
- recurrence and six-language quick add
- My Day and My Week, with Work/Personal focus and capacity
- reminders and waiting/follow-ups
- plain summaries (the Summaries page)
- backups, JSON archive and Markdown export, and CSV/JSON import
- packaging (MSIX)

`docs/decisions.md` records every choice made along the way.

### 1.2 Built ahead of schedule

Work that the spec places in phase 3 already exists:

- **GitHub Issues connection** (`GITHUB.md`, migrations 014–021). It is personal and uses the
  GitHub CLI login for credentials. It has two-way sync, a background engine and device-local
  checkpoints. Phase 2 must keep it working when sync arrives (section 7.11).

### 1.3 Phase 1 items still open

These Phase 1 work packages were never ticked:

- **WP0** — the installed-MSIX scheduled notification spike.
- **WP10** — reminders after exit, shown in the installed package.
- **WP13** — the 16-step installed-desktop checklist in `docs/acceptance.md`.

The spec says each phase must prove itself in real use before the next starts. Package **P0**
closes these items, or records the owner's explicit waiver.

### 1.4 Code landmarks used throughout this plan

| What | Where |
| --- | --- |
| Composition root, RPC registration, background engines | `src/Chaos.Desktop/Program.cs` |
| RPC service pattern (records + `[NeoRpcService]`, errors as data) | `src/Chaos.Desktop/Services/TagRpcService.cs` |
| Storage ports | `src/Chaos.Application/Abstractions/DataStores.cs` |
| Serialized writer, read connections, exclusive file work | `src/Chaos.Infrastructure/Sqlite/SqliteApplicationStore.cs` |
| Operation receipts, history and undo recorder | `src/Chaos.Application/History/ChangeRecording.cs`, `src/Chaos.Application/Tasks/TaskMutationSupport.cs` |
| Migrations (embedded, checksummed, numbered) | `src/Chaos.Infrastructure/Migrations/NNN_name.sql`, `SqliteMigrationRunner.cs` |
| Query specification (rows and counts from one spec) | `src/Chaos.Application/Queries/TaskQueries.cs`, `src/Chaos.Infrastructure/Search/SqliteTaskQueryStore.cs` |
| Capacity and week calendar | `src/Chaos.Domain/Planning/Capacity.cs`, `WeekCalendar.cs` |
| Summary facts | `src/Chaos.Application/Summaries/SummaryFactsBuilder.cs` |
| Notification jobs and engine | `src/Chaos.Application/Reminders/*`, `src/Chaos.Desktop/Platform/ReminderEngine.cs`, `src/Chaos.Desktop.Windows/Notifications/WindowsToastAdapter.cs` |
| JSON archive (exports every table unless excluded) | `src/Chaos.Infrastructure/Transfer/JsonArchive.cs` (`ExcludedTables`) |
| Backups and restore | `src/Chaos.Infrastructure/DataProtection/*` |
| Frontend shell, sidebar, view switch | `src/Chaos.Desktop/frontend/src/App.tsx` |
| Browser preview backend (no desktop host) | `src/Chaos.Desktop/frontend/src/preview/backend.ts`, `npm run preview:ui` |
| Markdown rendering and sanitizing | `src/Chaos.Desktop/frontend/src/markdown.tsx` |
| Help page | `src/Chaos.Desktop/frontend/src/Help.tsx` |

---

## 2. Inherited rules (binding)

These rules carry over from Phase 1. Unless a later `docs/decisions.md` entry changes one, they
bind every package.

### 2.1 Stack and build

- **Language and SDK.** C# **15.0**, set explicitly in `Directory.Build.props`. SDK pinned exactly
  in `global.json` (currently 11.0.100 GA, roll-forward disabled).
- **Target frameworks.** Portable projects target `net11.0`. Windows integrations target
  `net11.0-windows10.0.19041.0`.
- **Desktop framework.** **NeoAstra 1.12.0**. Package, tool and templates stay aligned. Do not add
  another desktop UI framework.
- **Frontend.** React and strict TypeScript with Vite. shadcn/ui on Tailwind v4 (see
  `docs/decisions.md`, 2026-11-10). Exact npm versions are committed in `package-lock.json`.
- **Client database.** SQLite through Microsoft.Data.Sqlite, with explicit numbered SQL
  migrations and parameterized SQL. **No EF Core.**
- **Package versions.** Central versions in `Directory.Packages.props`. Lock files are committed,
  and restores run in locked mode. No `VersionOverride`. No suppressed warnings. Warnings are
  errors.
- **Tests.** TUnit under Microsoft.Testing.Platform. Do not add `Microsoft.NET.Test.Sdk`, VSTest
  adapters or Coverlet. Name tests `Method_Scenario_ExpectedBehavior`.
- **Editing and checks.** Roslynk is skipped by user direction. Use normal file tools. Run
  Slopwatch after every C# batch.
- **New dependencies.** Resolve the latest stable version when the package that needs it starts.
  Check it builds on .NET 11, pin it centrally, and record it in `docs/dependency-versions.md`
  with the reason. Appendix B lists the dependencies this plan expects.

### 2.2 Layering

Dependencies point inward:

- `Domain` uses only the BCL.
- `Application` depends on `Domain`.
- `Infrastructure` depends on `Application` and `Domain`.
- `Desktop.Windows` depends on `Application`.
- `Desktop` is the composition root.

The entity is named `TaskItem`, never `Task`.

Application code never references NeoAstra, SQL, HTTP clients or React. New outside systems are
reached through a narrow port defined in `Application/Abstractions`:

- AI providers
- Google
- calendar feeds
- the Chaos online service
- the secret store
- Windows Hello

The adapter for each port lives in `Infrastructure` (portable) or `Desktop.Windows`
(Windows-only).

### 2.3 Command envelope and storage rules

- **Mutation requests.** Every mutation carries an `OperationId`, the `ExpectedVersion` of each
  entity it edits, and a typed payload. It writes its receipt in the same transaction. A
  duplicate ID with the same payload replays the stored result. A duplicate ID with a different
  payload is a conflict.
- **Errors.** Errors are stable kinds returned as data (`AppErrorKind`). They never contain SQL,
  paths or stack traces.
- **Storage formats.**

  | Kind of value | Stored as |
  | --- | --- |
  | ID | canonical GUID string |
  | Instant | UTC fixed-width ISO 8601 text |
  | Local date | `yyyy-MM-dd` |
  | Local time | `HH:mm:ss` |
  | Duration | minutes |
  | Enum | integer, with a `CHECK` constraint |

- **Versions.** `Version` is a local optimistic-concurrency counter, incremented once per update.
- **Deletion.** Reversible deletion uses `DeletedAtUtc`. Permanent deletion is the purge
  path only.
- **Database work.** One serialized writer and short-lived read connections, on a background
  worker. Never run database work on the UI thread.
- **Network calls.** **Never make a network call inside a write transaction.** Read what you
  need, call the network outside the transaction, then write the result in a new transaction.
  Use expected versions to detect anything that changed in between.
- **Change events.** Publish change events only after commit. Change events come from the activity
  log (6.4, built in P6a), written in the same transaction and pushed after commit.
- **Migrations.** Never edit an applied migration; its checksum is enforced. Add a new numbered
  file, and raise the schema ceiling in the code that checks it (search for the current highest
  number, 23, to find it).

### 2.4 RPC and frontend rules

- **Contracts.** Feature-sized RPC services with explicit request and response records. IDs,
  dates and instants travel as strings. Page sizes and inputs are bounded in C#. Generated
  bindings are never edited by hand.
- **No raw paths.** The renderer never sends arbitrary filesystem paths. File access goes through
  user-chosen handles or host-owned folders.
- **Untrusted content.** Markdown renders as sanitized React elements. No raw HTML, no
  `javascript:`, no remote images, no unsanitized `dangerouslySetInnerHTML`. External links open
  only through the host opener after a click.
- **Accessibility.** Every drag has a keyboard alternative. Use semantic controls, visible focus,
  `dir="auto"` on user text and logical CSS properties, and respect reduced motion and dark mode.
- **Browser preview.** `npm run preview:ui` must keep working. When you add an RPC service, give
  `preview/backend.ts` a minimal in-memory model of it, or at least empty successful reads. That
  way the preview shows the new screen.
- **Help page.** When a package adds user-visible behaviour, update `Help.tsx`.

### 2.5 Privacy and offline promise

The app must still start and work fully with **no account and no network**. Chaos makes a network
request only after the person explicitly sets up the thing that needs it. That means:

- signing in to an account
- connecting Google Calendar
- adding a calendar feed URL
- saving an AI key and using an AI feature
- connecting GitHub (already built)

With none of these configured, the Phase 1 "no network traffic" acceptance step must still pass.
Every package that adds a network call adds a test proving the call cannot happen while the
feature is unconfigured.

### 2.6 Tests

- **Domain tests** need no SQL and no NeoAstra.
- **Infrastructure tests** use **real file-backed SQLite** in a unique temp folder per test.
- **Clocks, IDs, HTTP handlers and OS presenters** are injectable.
- **No timing tests that rely on sleeps.** No disabled tests. No retries that hide flakiness.
- **Frontend tests** use the existing Vitest, jsdom and React Testing Library setup.
- **Server tests** (from P12) use a real PostgreSQL database (section 8.8).

---

## 3. Scope and traceability

### 3.1 In scope

| Spec feature (phase 2 parts) | Package | Acceptance evidence |
| --- | --- | --- |
| Kanban board on any list or project: columns by status, section, priority or tag; swimlanes; WIP limits; cards with essentials; drag to change the field; Done completes | P2 | Board service tests, jsdom drag/keyboard tests, native drag check |
| Custom views: conditions on any attribute, nested any/all groups, relative dates, list/table/board/calendar layouts, pinned with icon/colour/count, add tasks in context, table inline edit, alerts, export to a spreadsheet file, save a search as a view | P3 | Condition compiler matrix, count/row parity, alert dedupe, CSV fixtures |
| Calendar: day, week, month and agenda views; planned and due marked differently; recurrence on every date; time blocks; drag to replan; unscheduled side panel; overload per day; filters | P4 | Range query tests, recurrence projection tests, drag/keyboard replan |
| Other calendars' meetings, read-only: calendar feed (ICS) URLs and Google Calendar; busy time taken off My Week and My Day hours; My Day lists meetings and warns when tasks don't fit; task from event | P5, P6 | Feed parser fixtures, busy-minute calculations, Google fake transport, live check |
| AI with your own key (DeepSeek, OpenAI, Anthropic, Google, or a local model): AI-written summaries in your language; privacy controls showing which service receives data | P7 | Provider adapter tests with fake HTTP, privacy filter tests, fallback to plain |
| AI-drafted week plans | P8 | Draft validation tests (unknown IDs, out-of-week dates), no auto-confirm |
| Attachments on task notes, diary updates, pad notes and pages; images in place; PDF/video/audio previews; other files as name/type/size; export with attachments; page image backgrounds | P9 | Storage, reference and purge tests; rendering tests; export fixtures |
| Pad: drawing, photos and voice notes; Make task from part of a drawing | P10 | Drawing persistence, region backlink, recording capability tests |
| Optional app lock | P11 | Lock state machine tests, Windows Hello live check |
| Accounts: email, Microsoft, Google, Apple and GitHub sign-in; data already on the device moves into the account; stay offline-only forever if preferred | P12, P14 | Server auth tests, adopt/merge tests |
| Sync: changes appear on other devices within seconds; offline changes queued; field-level merge, latest wins, replaced value kept in history; always shows sync status; never blocks | P1, P13, P15 | Shared merge tests, two-client end-to-end tests, status UI tests |
| History keeps values replaced when sync merges | P13 | Remote-apply history tests |
| Attachments sync: large files download on first open; chosen lists kept fully offline; encrypted on the server; checked for known malware | P16 | Upload/download/resume tests, scanner fake plus live ClamAV test |
| Reminders clear on every device when dismissed | P17 | Acknowledgement sync tests |
| Calendar subscription link (served by the online service; needs an account; can be turned off or replaced) | P18 | ICS output fixtures, token rotation tests |
| Place reminders on phones | P19 (gated on D1) | Only if a phone client is in Phase 2 |
| Deleting the account deletes data from the online service | P14 | Server deletion test |

### 3.2 Explicitly outside Phase 2

These belong to later phases, or the spec says "later":

- sharing, roles, comments and mentions
- shared tags, pads, pages and boards
- live co-editing of pads and pages
- rotating repeating tasks and delegating to members
- space and project summaries
- GitHub *shared* custom views
- Google Calendar time blocking (writing to Google)
- AI agents and MCP
- the timeline and dependencies
- time tracking
- outside dashboards
- organisation sign-in, admin, controls, data region and org-managed AI keys
- handwriting search
- Outlook and Apple Calendar *connections*. A calendar feed URL from Outlook or Apple works
  through P5, but there is no account connection.
- AI quick add (turning a pasted email into a task; the spec calls it "Later")
- importing directly from other to-do apps (arrives with their connections)

Do not build a working-looking placeholder for any of these.

### 3.3 Platform

Phase 2 ships on the **Windows desktop** and the new **Chaos online service**. Sync is proven with
two Windows devices, or two data roots on one machine (`--data-dir`). A phone app is an open
question in the spec (D1). Until the owner decides, no phone client is built. The sync protocol
and server are designed so that a later client can join without changes.

---

## 4. Owner decisions needed

Each item has a default. The default lets work continue, but **the package named in the "Blocks"
column must not be ticked until the owner has confirmed or replaced the default**. Record each
answer in `docs/decisions.md`.

| ID | Question | Default used by this plan | Blocks |
| --- | --- | --- | --- |
| D1 | Is a phone client part of Phase 2? (spec open question "Which devices follow the Windows desktop") | No. Phase 2 is desktop plus service; the phone client gets its own plan. | P19 (place reminders) |
| D2 | Where is the online service hosted? | Any Linux container host with managed PostgreSQL, S3-compatible object storage and a key vault. Code depends only on ports, so the choice affects deployment (P20), not code. | P20 |
| D3 | Service domain name and the email sender for sign-in codes | `https://sync.<owner-domain>`; email through an SMTP relay the owner provides | P12 exit (live), P20 |
| D4 | Which sign-in providers at launch? Each needs a registration: Microsoft Entra app, Google OAuth client, Apple Developer account, GitHub OAuth App. The user earlier declined a GitHub *App* for the Issues connection; a GitHub *OAuth App* for sign-in is a different registration. | Email code and Google first, because the audience is SilverKey (Google Workspace, Internal consent). Microsoft, Apple and GitHub use the same code path and are turned on when registered. | P14 exit for each provider |
| D5 | Attachment size limits and storage per account (spec open question) | 100 MiB per file and 5 GiB per account, enforced by the server and configurable | P16 |
| D6 | Before P1: has Phase 1 proven itself, i.e. the installed acceptance checklist passed or been waived? | Must be answered; no default | P1 start |
| D7 | Include GitHub fields (repository, labels, milestone, open/closed, linked PRs) in personal custom views now? The connection already exists. The spec puts GitHub fields in phase 3 only because GitHub arrives then. | Yes, personal views only; shared views stay phase 3 | P3 (the GitHub field group only) |
| D8 | Calendar view library: FullCalendar standard (MIT) or a hand-built grid? | FullCalendar standard packages only (never the premium ones), pinned exactly | P4 |

---

## 5. Planning decisions the implementer must follow

1. **Local first, server second.** Every Phase 2 feature that does not need the server works
   offline-only and without an account:
   - Kanban
   - custom views and alerts
   - the calendar view
   - calendar feeds and Google Calendar meetings (the device talks to Google directly, as the
     spec says)
   - AI with your own key
   - attachments and pad media
   - app lock

   Only sync, attachment sync, cross-device reminder clearing and the subscription link need an
   account.
2. **Sync is prepared before the local features, and built after them.** P1 makes the local
   database sync-ready: a catalog of every table, change capture, clocks and deterministic IDs.
   It adds no network code. Every later package that adds a table must classify it in the sync
   catalog, and a guard test enforces this. The server and protocol come in P12–P15.
3. **One account, one profile, one Personal space.** An account owns exactly one profile, its
   Personal space and its Inbox. Shared spaces are phase 3, but IDs and the server schema keep
   `SpaceId` everywhere so they can be added.
4. **Field-level merge, latest wins, by field group.** Columns that must stay consistent change
   together as a *field group*. Examples:
   - status, completed instant and cancelled instant
   - planned date, time and zone
   
   Each group carries a hybrid logical clock (HLC). For one group, the newer clock wins. Groups
   merge independently. The losing value is written to history (spec: "the earlier value stays
   in the task's history"). Section 7 has the details.
5. **Text fields are not merged character by character.** Notes, page text, pad text and diary
   text are whole-field latest-wins, with the losing text kept in history or versions. Live
   co-editing is phase 3.
6. **Device-local state stays device-local.** Never synced:
   - operation receipts, undo entries and the search index
   - backups and their catalog
   - notification jobs (they are derived)
   - GitHub checkpoints, backoff and run diagnostics
   - the calendar event cache
   - secrets
   - app-lock settings
   - offline-pin choices
   - per-device shortcut and tray settings
7. **Secrets live in the Windows credential store, never in SQLite.** That includes the Chaos
   refresh token, AI keys and the Google refresh token, kept behind an `ISecretStore` port. They
   are never in SQLite, backups, exports, sync, logs, RPC responses or the renderer. The
   renderer only learns "a key is saved". This does not change the GitHub rule: GitHub tokens
   stay with the GitHub CLI and are never stored by Chaos.
8. **The server is a generic, permission-checked store for synced entities.** It does not run
   task business rules. It needs only enough understanding of the data to:
   - merge field groups
   - enforce ownership, limits and schema version
   - build the ICS subscription feed (P18)
   - delete an account
9. **AI never acts by itself.** AI output is either text the person reads (summaries) or a draft
   the person reviews (week plans). It never confirms, completes, deletes or sends anything. Task
   text sent to a model is data, never instructions. Model output is rendered as passive
   Markdown.
10. **No new background network traffic without consent.** Background engines start only when
    their feature is configured. They stop at once when it is removed. They back off on errors
    and respect rate limits.

---

## 6. Architecture overview

### 6.1 New projects

```text
src/
  Chaos.Sync/                 net11.0. Shared by client and server: HLC, field-group catalog types,
                              change-set records, the merge function, UUIDv5, protocol DTOs.
                              BCL only.
  Chaos.Server/               ASP.NET Core on net11.0: accounts, sign-in, sync API, blobs, malware
                              scanning, ICS feed, account deletion. PostgreSQL through Npgsql.
    Migrations/S001_*.sql     Server migrations, same rules as the client ones.
tests/
  Chaos.Sync.Tests/           TUnit: HLC, merge, UUIDv5 vectors, protocol serialization.
  Chaos.Server.Tests/         TUnit: server integration against real PostgreSQL, plus
                              two-client end-to-end sync tests.
deploy/
  docker-compose.dev.yml      PostgreSQL, MinIO (S3-compatible), ClamAV, Mailpit
                              (development only).
  Chaos.Server.Dockerfile
```

Dependency rules for the new projects:

- `Chaos.Domain` gains no dependency.
- `Chaos.Application` may reference `Chaos.Sync`, which uses only the BCL.
- `Chaos.Infrastructure` implements the client sync engine. It talks to the server over HTTP
  through the `IChaosServiceClient` port.
- `Chaos.Server` references `Chaos.Sync` only. It never references client projects.

### 6.2 New client ports (in `Chaos.Application/Abstractions`)

| Port | Responsibility | Adapter |
| --- | --- | --- |
| `ISecretStore` | Save, read and delete a named secret for the current Windows user | `Desktop.Windows`: `Windows.Security.Credentials.PasswordVault`, verified in P0. Tests use an in-memory fake. |
| `IUserPresenceVerifier` | Ask Windows Hello to verify the user, and report whether it is available | `Desktop.Windows`: `UserConsentVerifier` via its window-handle interop, verified in P0 |
| `IAttachmentFileStore` | Content-addressed local file store: put a stream (returns SHA-256 and size), open for read, exists, delete | `Infrastructure` (filesystem) |
| `ICalendarFeedClient` | Fetch an ICS URL with ETag/Last-Modified support and a size limit | `Infrastructure` (`HttpClient`) |
| `IGoogleCalendarClient` | Sign-in with loopback and PKCE, list calendars, list events in a window | `Infrastructure` (Google API client library, per `docs/decisions.md` 2026-11-15) |
| `IAiTextService` | Send a prompt and get text or validated JSON back, for a configured provider | `Infrastructure` (one `HttpClient` adapter per provider family) |
| `IChaosServiceClient` | Sign-in (system browser and loopback), token refresh, sync push/pull/events, blob upload/download, account calls | `Infrastructure` (`HttpClient`) |
| `ISystemBrowser` | Open a URL in the default browser and run a one-shot loopback listener on `127.0.0.1` | `Desktop` |

### 6.3 Where each kind of data lives

| Data | Client SQLite | Synced | Server | Notes |
| --- | --- | --- | --- | --- |
| Tasks, subtasks, containers, sections, tags and links, templates, diary, pages, pad, recurrence, reminder rules, waiting/follow-ups, day/week plans, availability, summaries, profile settings, saved views, board settings, attachment metadata, calendar connection settings | yes | yes | yes | Field-group merge |
| Task history, diary revisions, page revisions, plan and recurrence revisions | yes | yes, append-only | yes | Never merged; inserted once |
| Attachment bytes | file store | yes (P16) | encrypted object storage | Downloaded on demand |
| Receipts, undo, search index, backups, notification jobs, GitHub checkpoints and runs, calendar event cache, sync bookkeeping, device settings | yes | **no** | no | Rebuilt or derived per device |
| Secrets (AI keys, Google and Chaos refresh tokens) | **no** (credential store) | no | no | Chaos tokens are issued by the server; their hashes are kept there |
| Accounts, devices, sessions, subscription-link tokens | no | no | yes | Server only |
| Activity log, activity cursors, job runs (6.4) | yes | **no** | no | Device-local system event stream; not exported |

### 6.4 Activity log (the system event stream)

Added 2026-11-17 by the owner. Implemented by package **P6a**, which is the next package to
implement (see section 15).

**What it is.** A durable, device-local, machine-readable record of every committed change and
every background job run. It is **for the system, not for the person**. In-process consumers read
it in order and act on it. It has no prose and no UI of its own. It replaces the `IChangePublisher`
of section 2.3, which was declared but was never implemented or called.

**Why.** Today the only shared signal is the database itself. Writes come from user commands and
also from background work on its own timers: `ReminderEngine` (30 s), `CalendarRefreshScheduler`,
`BackupScheduler` and `GitHubSyncScheduler`/`GitHubSyncEngine`. None of these tell anyone what they
did, so the frontend polls: `SyncLogo` every 4 s, `GitHubSettings` and `GitHubTaskNotice` every
1 s while syncing, `Reminders` and `Summaries` on intervals, and many views reload on window focus.

**Consumers, in the order they arrive:**
1. UI refresh (P6a)
2. reminder resync for changed tasks
3. view-alert evaluation on change (11.2)
4. GitHub outbound wake-up
5. sync status (P15)
6. the open-editor rule in 7.10

Clock-driven work keeps its timers: reminders coming due, calendar refresh intervals and backups.
The log replaces polling for **data changes**, not for schedules.

**Tables** (migration `030_activity_log.sql`, all `LocalOnly`, never in the archive):

- `ActivityLog(Seq INTEGER PRIMARY KEY AUTOINCREMENT, OperationId TEXT NOT NULL, OccurredAtUtc TEXT NOT NULL, Source TEXT NOT NULL CHECK (Source IN ('user','schedule','sync','import','undo','system')), Action TEXT NOT NULL, Outcome TEXT NOT NULL CHECK (Outcome IN ('ok','failed')), JobRunId TEXT NULL, DetailJson TEXT NULL, DetailVersion INTEGER NOT NULL DEFAULT 1)`
  - one row per write transaction that changed something
  - an operation spanning several transactions repeats its `OperationId`
- `ActivityLogEntities(Seq INTEGER NOT NULL REFERENCES ActivityLog(Seq), Kind TEXT NOT NULL, EntityKey TEXT NOT NULL, Op TEXT NOT NULL CHECK (Op IN ('upsert','delete')), Version INTEGER NULL, PRIMARY KEY (Seq, Kind, EntityKey))`
  - `Kind` and `EntityKey` use the sync catalog's names and key format (7.2, 7.3)
  - `Version` is the row's version after the change, when the table has one
  - consumers use `Version` to skip changes they have already seen or that are already superseded, so processing an entry twice is harmless
- `ActivityCursors(Consumer TEXT PRIMARY KEY, Seq INTEGER NOT NULL, UpdatedAtUtc TEXT NOT NULL)`
  - for host-side consumers that must survive a restart
  - the UI does not register here: each window starts from the current head
- `JobRuns(Id TEXT PRIMARY KEY, Job TEXT NOT NULL, StartedAtUtc TEXT NOT NULL, FinishedAtUtc TEXT NULL, Outcome TEXT NOT NULL CHECK (Outcome IN ('running','ok','noop','failed')), Error TEXT NULL, ActivityCount INTEGER NOT NULL DEFAULT 0)`
  - every scheduled pass, including passes that do nothing
  - the last 200 runs per job are kept
  - this is diagnostics plus "is X running / when did X last run". It is kept out of the event stream on purpose, so a 30 s tick does not add 2,880 log rows a day.

**Capture rules.**

1. **Operation context.** An ambient `ActivityScope` (`AsyncLocal`) carries `Source`, `Action`,
   `OperationId` and `JobRunId`. It is opened at the entry points:
   - every RPC method (`Source=user`, `Action=<service>.<method>`). NeoAstra 1.12 has no RPC
     middleware, so each method opens it through one helper.
   - every scheduler or engine pass (`Source=schedule`, or `sync` for GitHub)
   - undo, import and the remote-change applier (7.4)
   
   A write with no scope throws in tests and Debug builds. In Release it is recorded as
   `Source=system`, `Action=unscoped`, so nothing is ever lost.
2. **Synced tables: from the triggers.** `ActivityCapture` runs inside
   `SqliteApplicationStore.ExecuteAsync`, immediately before `ISyncCapture.Flush`, which deletes
   `SyncPending` while sync is off.
   - It reads the distinct `(Kind, EntityKey)` pairs in `SyncPending`. A `__purge` group becomes
     `Op=delete`.
   - It reads `Version` by key where the table has one.
   - It writes one `ActivityLog` row and its entity rows.
   
   Commands do not change for this.
3. **Device-only tables: explicit notes.** `LocalOnly` tables have no triggers. Their writers call
   `IActivityRecorder.Note(kind, key, op, version)` inside their transaction:
   - notification jobs
   - view matches and alerts
   - the calendar event cache and source state
   - Google account state
   
   Churning caches note one **summary entity** rather than every row. For example, a calendar
   refresh notes `calendar_source/<id>`, not each `CalendarEvents` row.
4. **Remote apply.** Triggers are suppressed while applying (7.3), so the applier notes every
   entity it applied, with `Source=sync`.
5. **Rollback and failure.**
   - A rolled-back transaction leaves no activity row. This comes free from writing in the same
     transaction.
   - A failed job pass records `Outcome=failed` in `JobRuns`, plus an `ActivityLog` row with
     `Outcome=failed` and no entities, in its own small transaction.
   - User validation rejections are not logged.
6. **Action names are a closed list in code** (`ActivityActions`). A contract test fails on any
   action written that is not in the list. `DetailJson` is versioned by `DetailVersion`.

**Ordering.** All writes go through the one serialized writer, and `Seq` is assigned inside the
writing transaction. So `Seq` order is commit order. A consumer that has read everything up to
`Seq = N` can never later see a newly committed row with a smaller `Seq`. Consumers must not
assume the numbers are contiguous.

**Delivery to the UI.**
1. After commit, the store reports the new head `Seq` to `IActivityNotifier`. The Desktop host
   coalesces these reports, about every 100 ms.
2. It publishes the NeoAstra RPC event `activity.committed { seq }` (`NeoRpcEvent<T>.PublishAsync`).
3. The frontend calls `activity.since({ after, limit })`. This returns entries with their
   entities, or `reset: true` when `after` is older than the oldest retained row; the window then
   reloads everything.
4. `activity.jobs` returns each job's current state (running, last run, outcome) for the sync
   indicators.

**Retention.** The backup check's timer prunes the log. It deletes rows below the lowest
`ActivityCursors.Seq`, but always keeps at least 7 days. A hard cap of 30 days or 200,000 rows,
whichever is reached first, stops a stalled consumer from growing the log forever. A consumer whose
cursor is pruned past gets a reset.

---

## 7. Sync design

This is the hardest part of Phase 2. Read all of it before P1, and again before P13.

### 7.1 Vocabulary

- **Entity**: one row of a synced table, identified by `(Kind, EntityId)`. `Kind` is a stable
  name such as `task` or `subtask`, never the SQL table name. This lets tables be renamed later.
- **Field group**: a named set of columns that always travel and merge together.
- **Change**: one entity, an operation (`upsert`, `purge`), and the field groups it carries. Each
  group carries its values and its HLC.
- **HLC (hybrid logical clock)**: a sortable value `(wallMillis, counter, deviceId)`. It is
  encoded as a fixed-width string: 13-digit milliseconds, `-`, 5-digit counter, `-`, the 32-hex
  device ID. String order is clock order. On a local change:
  `wall = max(now, last.wall)`. If `wall == last.wall`, increment `counter`; otherwise reset
  it to 0. On receiving a remote clock, set
  `last = max(last, remote)` and then tick. This is the standard HLC algorithm; implement it in
  `Chaos.Sync/Hlc.cs` with an injectable `TimeProvider`.
- **Device ID**: a random GUID created once per data root, stored in `DeviceSettings`.
  Restoring a backup **keeps** the current device ID; the backup's value is never used. A copied
  data root must call "Reset device identity" (P14 adds it to Settings) before signing in.
- **Cursor**: the highest server sequence number this device has applied.

### 7.2 The sync catalog

`src/Chaos.Infrastructure/Sync/SyncCatalog.cs` is a single, hand-written, reviewed table. It is
the only place that says how each SQLite table syncs.

```csharp
// Shape only. The exact names follow repository conventions.
public sealed record SyncTable(
    string Table,                 // SQL table name
    string Kind,                  // stable sync name
    SyncMode Mode,                // Merge | AppendOnly | LocalOnly
    IReadOnlyList<string> Key,    // primary-key columns (composite allowed for link tables)
    IReadOnlyList<SyncFieldGroup> Groups,
    IReadOnlyList<string> LocalColumns,   // columns never synced (Version, DraftVersion, ...)
    IReadOnlyList<SyncUniqueRule> UniqueRules, // how each unique index resolves duplicates (7.6)
    int ApplyOrder);              // parents before children
public sealed record SyncFieldGroup(string Name, IReadOnlyList<string> Columns);
```

Rules:

1. **Every** table in the client database appears in the catalog, including `LocalOnly` tables.
   The guard test `SyncCatalog_EveryTableAndColumnClassified` opens a freshly migrated database
   and compares `sqlite_master`/`pragma_table_info` against the catalog. It fails on:
   - any unclassified table or column
   - any unique index that has no `SyncUniqueRule` and is not made impossible by a
     deterministic ID (7.5)
   
   Every later package that adds a migration therefore has to update the catalog.
2. **Columns that are never synced:**
   - `Version` (a local optimistic counter)
   - `DraftVersion` (an editor revision)
   - OS schedule columns
   - per-device columns such as `Profiles.CaptureShortcut`, `PadShortcut` and
     `KeepRunningInTray`
3. **`UpdatedAtUtc` is not in any group.** On a local change it is set as today. On a remote
   apply it becomes `max(local UpdatedAtUtc, change wall time)`.
4. **`CreatedAtUtc` and immutable identity columns** (for example `Tasks.SpaceId`) belong to a
   group named `create`. That group is sent with the first upsert and never changes afterwards.
5. **`DeletedAtUtc` is always its own group, named `deleted`.** Deleting and restoring are
   therefore ordinary latest-wins changes. An edit to another group on another device does not
   undelete the entity.
6. **Link tables with composite keys** (`TaskTags`, `SubtaskTags`, `ScratchPadTags`, `PageTags`,
   `WeekGoalTasks` and others) use the composite key as the entity key and have only a `deleted`
   group. If a link table has no `DeletedAtUtc`, P1 adds one, so that removing a link is a
   tombstone and not a hard delete. The query code must then filter `DeletedAtUtc IS NULL` on
   these tables everywhere; search for every read.

Field groups for the core tables:

| Kind | Groups (columns) |
| --- | --- |
| `task` | `create` (SpaceId, CreatedAtUtc, RecurrenceOccurrenceKey); `title` (Title); `notes` (Notes); `status` (Status, CompletedAtUtc, CancelledAtUtc); `place` (ContainerId, SectionId); `position` (Position); `priority` (Priority); `estimate` (EstimateMinutes); `category` (CapacityCategory); `planned` (PlannedDate, PlannedTime, PlannedZoneId); `due` (DueDate, DueTime, DueZoneId); `deleted` (DeletedAtUtc) |
| `subtask` | `create` (TaskId, CreatedAtUtc); `title`; `notes`; `status` (Status, CompletedAtUtc, CancelledAtUtc); `position`; `deleted` |
| `container` | `create` (SpaceId, Kind, IsInbox, CreatedAtUtc); `name` (Name, NormalizedName); `position`; `deadline` (DeadlineDate); `archived` (ArchivedAtUtc); `category` (DefaultCategory); `deleted` |
| `tag` | `create`; `name` (DisplayName, NormalizedName); `parent` (ParentTagId); `color` (ColorKey); `position`; `deleted` |
| `page` | `create` (SpaceId, DailyDate, CreatedAtUtc); `title`; `text` (RawMarkdown); `tree` (ParentPageId); `position`; `deleted` |
| `pad` | `create`; `text` (RawMarkdown); `position`; `archived`; `deleted` |
| `profile` | `create`; `name`; `zone` (ZoneId); `locale`; `dates` (DateFormat, FirstWeekday); `planning` (PlanningTime, SummaryTime, MorningTime, WeeklySummaryDay); `reminders` (DefaultReminderTime); `quiet` (QuietHoursEnabled, QuietStart, QuietEnd) |

For every other table, P1 defines the groups using the same thinking. Ask: "which columns must
change together for a `CHECK` constraint or a meaning to hold?" Write the answer as a comment
next to the table's entry.

### 7.3 Change capture (triggers, then a flush in the same transaction)

Not every write goes through the history recorder. Pad and page autosave, plan writes and
settings all bypass it. Change capture is therefore done by **SQLite triggers** generated from
the catalog.

1. **Bookkeeping tables (migration 024):**
   - `SyncPending(Kind TEXT, EntityKey TEXT, GroupName TEXT, PRIMARY KEY (Kind, EntityKey, GroupName))`
   - `SyncApplyState(Id INTEGER PRIMARY KEY CHECK (Id = 1), Applying INTEGER NOT NULL)`
   - `SyncState`: one row holding `Enabled`, `AccountId`, `ProfileBinding`, `Cursor`,
     `LastHlc`, `TriggerHash`, `LastPushUtc`, `LastPullUtc` and `LastError`
   - `SyncClocks(Kind, EntityKey, GroupName, Hlc, PRIMARY KEY (Kind, EntityKey, GroupName))`
   - `SyncOutbox(Seq INTEGER PRIMARY KEY AUTOINCREMENT, ChangeId TEXT UNIQUE, Kind, EntityKey, Op, PayloadJson, CreatedAtUtc)`
   - `SyncInboxParked`, for remote changes that cannot be applied yet (7.8)
   - `SyncAttention`, for conflicts that need the person (7.9)
   - `DeviceSettings(Key TEXT PRIMARY KEY, Value TEXT)`, holding `DeviceId` and later
     device-only settings
2. **Installing the triggers.** `SyncTriggerInstaller` runs at startup after migrations. It
   computes a hash of the catalog. If the hash differs from `SyncState.TriggerHash`, it drops
   every `TR_Sync_%` trigger and recreates them in one transaction. For each `Merge` or
   `AppendOnly` table it creates:
   - `AFTER INSERT`: one `INSERT OR IGNORE INTO SyncPending` per group.
   - `AFTER UPDATE`: for each group, an `INSERT OR IGNORE` guarded by
     `WHEN (OLD.c1 IS NOT NEW.c1 OR OLD.c2 IS NOT NEW.c2 ...)`.
   - `AFTER DELETE`: a pending row with group `__purge`.
   
   Every trigger has
   `WHEN (SELECT Applying FROM SyncApplyState WHERE Id = 1) = 0`. Triggers in the main schema
   may not reference temp tables, which is why this is a real table. The entity key is the
   primary key, or the composite key joined with `|`.
3. **Flushing.** `SqliteApplicationStore` gains one hook. Every write transaction calls
   `ISyncCapture.Flush(connection)` immediately before `COMMIT`. Flush does this:
   - If `SyncState.Enabled = 0`, it deletes `SyncPending` and returns. Offline-only devices pay
     almost nothing.
   - Otherwise it groups pending rows by entity, reads each entity's current row by key, and
     ticks the HLC once per entity. It writes one `SyncOutbox` row: the changed groups with
     their current values and the new clock (`upsert`), or `purge`. It upserts `SyncClocks`,
     stores `LastHlc` and deletes the pending rows.
   - All of this happens in the same transaction as the user's change. A rolled-back change
     produces no outbox row.
4. **No per-command code.** Commands do not change. The guard test plus a test per catalog entry
   (`SyncCapture_<Kind>_ChangeProducesExpectedGroups`) prove capture works.

### 7.4 Applying remote changes

Code lives in `src/Chaos.Infrastructure/Sync/SyncApplier.cs`. It runs in the serialized writer, in
batches of at most 200 changes per transaction, so local writes interleave and the UI never
blocks.

For each transaction:

1. Set `SyncApplyState.Applying = 1`, run `PRAGMA defer_foreign_keys = ON`, and **flush pending
   local changes first** (7.3). This ensures local clocks are known.
2. For each incoming change, in server order:
   1. **Purge.** Hard-delete the entity and its dependents, exactly as `DataPurge` does for that
      kind. Delete its `SyncClocks` rows. Write nothing to the outbox.
   2. **Upsert.** For each incoming group, compare its clock with the local `SyncClocks` value
      for that group. If there is no local clock, the incoming one wins.
      - **Incoming is newer:** write the group's columns. If the entity does not exist yet,
        insert it with all incoming groups. Store the incoming clock. If the old local value
        differed and the entity has history, record the replaced value (7.7).
      - **Local is newer or equal:** keep the local value. It is already in the outbox, or it
        already came from the server.
   3. Increment the local `Version` once per changed entity. This is a local counter and it
      makes stale local undo entries and open editors detect the change (7.10).
   4. If writing the row breaks a unique index, run that index's `SyncUniqueRule` (7.6) in the
      same transaction. If a foreign key target is missing, move the change to
      `SyncInboxParked` (7.8).
3. Update the search documents for the changed entities (call the same `SearchIndexWriter`
   methods the commands use), and advance `SyncState.Cursor` to the batch's last sequence.
4. Set `Applying = 0`, then commit.
5. After commit, publish change events for the affected IDs through `IChangePublisher`. Views
   refresh as they do for local edits. Then tell the reminder engine to resync the affected
   tasks; notification jobs are derived per device.

### 7.5 Deterministic IDs for natural keys

Two devices that are offline must not create two different rows for the same real thing. From
P1 onwards, these rows get **name-based UUIDs** (RFC 9562 version 5, SHA-1).
`Chaos.Sync/NameBasedId.cs` implements them and is tested against the RFC test vectors. The
namespace is a fixed GUID committed in code.

| Row | Name hashed |
| --- | --- |
| DayPlans | `dayplan:{profileId}:{localDate}` |
| DayPlanItems | `dayplanitem:{dayPlanId}:{taskId}` |
| WeekPlans | `weekplan:{profileId}:{weekStart}` |
| WeekAssignments | `weekassign:{weekPlanId}:{taskId}` |
| Daily pages | `dailypage:{spaceId}:{date}` |
| Recurrence occurrence row and its task | `occurrence:{seriesId}:{nominalKey}` and `occurrencetask:{seriesId}:{nominalKey}` |
| Subtasks copied into an occurrence | `occurrencesubtask:{occurrenceTaskId}:{templateIndex}` |
| Summaries | `summary:{profileId}:{kind}:{periodStart}:{revision}` |
| AvailabilityRules | `availability:{profileId}:{weekday}` |
| WeekAvailabilityOverrides | `availabilityoverride:{profileId}:{date}` |
| Reminder acknowledgements (P17) | `reminderack:{sourceKind}:{sourceId}:{originalTargetUtc}` |
| GitHub-imported tasks | `githubtask:{connectionId}:{repositoryId}:{issueNumber}` |

Change each code path that creates these rows to use `NameBasedId` instead of
`IIdGenerator.NewId()`:

- `DayPlanning`, `MyDayCommands`, `MyWeekCommands` and their stores
- `PageCommands` (daily pages)
- `RecurrenceCommands` (successor materialization)
- `SummaryCommands`
- the availability commands
- GitHub inbound creation

Existing rows keep their random IDs. Duplicates created before P1 are handled by the unique rules
when a device joins (7.6).

### 7.6 Unique-index rules (duplicates created on two devices)

Every unique index in the client schema needs a `SyncUniqueRule`, unless a deterministic ID makes
a duplicate impossible. The rules are deterministic, so **every device makes the same choice**:
the winner is the row with the **lowest ID** (ordinal string comparison).

| Rule | Used for | What happens |
| --- | --- | --- |
| `RepointChildren` | DayPlans, WeekPlans, availability rows and other legacy duplicates of a natural key | Child rows of the loser are moved to the winner. Where a child would itself collide, its own rule applies. Then the loser is tombstoned, or purged if its table has no `DeletedAtUtc`. |
| `MergeTag` | Sibling tags with the same normalized name | Reuse the data-level part of `TagCommands` merge: links deduplicated, children merged recursively, loser tombstoned. |
| `RenameLoser` | Containers with the same normalized name | The loser is renamed `Name (2)`, `Name (3)` and so on, choosing the first free name. Nothing is merged; two lists that happen to share a name stay separate. |
| `AppendText` | Daily pages for the same date (legacy rows) | The loser's text is appended to the winner under a line `From another device, <date>`. The loser is tombstoned and its links rewritten to the winner's ID. |
| `KeepLowestRetireOther` | One active occurrence per series; anything where combining makes no sense | The loser is tombstoned (task and occurrence). A `SyncAttention` notice tells the person what was retired, so they can restore it. |
| `Adopt` | Profiles, the Personal space and the Inbox | Handled only by device join (P14), never during ordinary apply. |

The changes a rule makes on the device are local changes. They are captured by the triggers and
pushed like any other change. Because every device computes the same result, the outcome
converges.

### 7.7 Replaced values in history

When a remote group replaces a different local value on an entity that has history (task,
subtask, page, pad), the applier writes a history entry. Its event kind is `sync.merged`, with:

- the field names
- the before and after values
- the winning device's ID

For pages, the replaced text becomes a page revision, so "Versions" shows it.

These entries are local bookkeeping. They are `LocalOnly` and not pushed, because every device
writes its own entry from its own point of view. The task's history viewer shows them as
"Changed on another device; your earlier value was …".

### 7.8 Ordering, parking and retries

- **Ordering.** The server returns changes in sequence order. A device pushes in outbox order,
  which follows its local transaction order, so a parent's insert normally arrives before its
  children.
- **Parking.** If a change still references a missing parent, the applier parks it in
  `SyncInboxParked` with the reason. After each batch it retries parked changes. A change parked
  for more than 10 pull cycles becomes a `SyncAttention` item: "Some changes could not be
  applied". Never drop it silently.
- **Validation failures.** A change whose values fail a `CHECK` constraint is a bug, or comes
  from a newer client. Park it and raise attention, keeping the raw payload so it can be reported.

### 7.9 Attention items

`SyncAttention` rows are shown on the sync status page (P15), each with a plain explanation and a
suggested action. Examples:

- retired duplicate occurrence
- renamed list
- parked change
- server rejected change
- storage quota reached
- client too old

Conflicts that latest-wins resolves are **not** attention items; history covers them.

### 7.10 Undo, editors and drafts during sync

- **Undo.** Undo entries stay local. Because remote apply increments `Version`, an undo entry for
  an entity changed by another device fails its version check and is refused with the existing
  conflict message. This is the safe behaviour. Do not try to undo across devices.
- **Open editors with autosave** (task fields, subtask fields, pad, pages). When a change event
  arrives for the entity being edited:
  - If the local draft has no unsaved changes, reload silently.
  - If it has unsaved changes, keep the draft and show "Changed on another device — Reload or Keep
    mine". "Keep mine" saves with the new expected version, so it becomes the newest change.
- **Pad and page `DraftVersion`.** It is device-local. A remote text change does not touch it.

### 7.11 GitHub and sync

GitHub credentials stay on the device, managed by the GitHub CLI. To keep two devices from both
talking to GitHub:

- `GitHubConnections` and the issue-link and mapping tables are synced. Checkpoints, backoff,
  runs and the pending-write queue (`GitHubIssueWrites`) are `LocalOnly`.
- A new synced column, `GitHubConnections.RunnerDeviceId` (P17), says which device runs the
  GitHub engine. The device that created the connection is the runner. `GitHubSyncScheduler`
  and `GitHubSyncEngine` run only when `RunnerDeviceId` equals the local device ID.
- **Edits on other devices.** When the runner's applier changes a task that has an issue link, it
  calls the existing "mark linked task dirty" path. The runner then pushes the change to GitHub.
  Non-runner devices never queue GitHub writes.
- **Moving the runner.** Settings shows "GitHub syncs from <device name>" with **Run GitHub sync
  on this device**. That action requires GitHub CLI on this device to verify the same numeric
  account ID (the existing setup check), and it resets the local checkpoints.

### 7.12 Wire protocol (version 1)

All endpoints are under `/v1`. They need a bearer access token, and every request sends:

- `Chaos-Protocol: 1`
- `Chaos-Client-Schema: <client schema version>`
- `Chaos-Device: <device id>`

| Endpoint | Purpose |
| --- | --- |
| `POST /v1/sync/push` | Body: `{ changes: Change[] }`, at most 500 changes and 4 MiB. Each change has a `changeId` (GUID), `kind`, `key`, `op` and `groups: { name: { hlc, values: { column: value } } }`. The server applies each one idempotently by `changeId` and returns `{ accepted: [changeId], rejected: [{ changeId, reason }], serverHlc }`. |
| `GET /v1/sync/pull?after=<cursor>&limit=<≤1000>` | Returns `{ changes: ServerChange[], next: cursor, more: bool, snapshotRequired: bool }`. A `ServerChange` is a change plus its `seq`, carrying only the groups that won on the server. |
| `GET /v1/sync/snapshot?kind=&after=<key>&limit=` | Pages through current entity states. Used when a device first joins, or when its cursor is older than the server's change-log horizon (`snapshotRequired`). |
| `GET /v1/sync/events` | Server-Sent Events stream sending `{"seq":N}` whenever the account's sequence advances, plus a keep-alive comment every 25 s. The client reacts by pulling (debounced 300 ms). |

Rules for both sides:

- **Values in JSON.** Values are JSON strings, numbers, booleans or null, exactly as stored in
  SQLite. The server never parses dates, except for the ICS feed (P18).
- **Rejections.** Possible reasons: `unknown-kind`, `too-large`, `quota`, `schema-too-old`,
  `not-owner` and `invalid`. A rejected change stays in the outbox and raises `SyncAttention`.
  `schema-too-old` stops pushing and shows "Update Chaos to keep syncing".
- **Forward compatibility.** A client pushes only groups it changed. The server keeps groups and
  kinds that an older client does not know, and simply does not send them to clients whose schema
  is too old for them. The catalog records the schema version in which each group appeared. An
  older client therefore never erases newer data.
- **Change log horizon.** The server keeps the change log for 90 days. Devices that have not
  pulled for longer re-sync from the snapshot.

### 7.13 First sync, joining and leaving

There are three cases, all implemented in P14.

1. **First device of a new account.**
   1. The device signs in. The server creates the account and binds this device's profile ID,
      Personal space ID and Inbox ID as the account's canonical ones.
   2. The device sets `SyncState.Enabled = 1` and uploads every synced entity. It does this by
      generating an `upsert` for all groups of each row, in catalog `ApplyOrder`, with fresh
      clocks, in batches.
   3. It then pulls.
2. **Device with local data joining an existing account.** The person chooses one of two
   options:
   - **Merge this device's data into the account** (default when the device has any tasks):
     1. Make a safety backup.
     2. **Adopt**: in one transaction, rewrite the local profile, Personal space and Inbox IDs to
        the account's canonical IDs. Update every foreign key column. The list of columns comes
        from the catalog. Then run `PRAGMA foreign_key_check`.
     3. Pull the account snapshot and apply it. The unique rules resolve duplicate tags, lists,
        daily pages and plans.
     4. Upload this device's remaining entities.
   - **Replace this device's data with the account's:**
     1. Make a safety backup.
     2. Reset the local data, using the same path as "Delete all local data".
     3. Pull the snapshot.
3. **Signing out.** The person chooses **Keep data on this device** (the device becomes
   offline-only, `SyncState.Enabled = 0`, and the outbox is cleared) or **Remove the account's
   data from this device** (reset). Either way, the device's refresh token is revoked on the
   server and removed from `ISecretStore`.

---

## 8. Online service design (Chaos.Server)

### 8.1 Technology

- ASP.NET Core minimal APIs on .NET 11.
- PostgreSQL through Npgsql, with explicit numbered SQL migrations in `src/Chaos.Server/Migrations`
  and a small migration runner modelled on the client's (checksums, refuse edited migrations,
  refuse newer schema). No EF Core, matching the client rule.
- **OpenIddict** as the server's own OAuth 2.0 / OpenID Connect authorization server. Its client
  web-integration providers handle Google, Microsoft, Apple and GitHub sign-in. Verify in P12
  that the chosen OpenIddict version supports .NET 11. If it does not, record the blocker; do
  not hand-roll token issuance.
- Object storage behind an `IBlobStore` port, with a filesystem adapter for tests and
  development, and an S3-compatible adapter for production.
- Malware scanning behind an `IMalwareScanner` port, with a ClamAV `clamd` adapter using its
  TCP `INSTREAM` command, and a fake for tests.
- Email behind an `IEmailSender` port, with an SMTP adapter. Development uses Mailpit.

### 8.2 Server schema (migration S001 onwards)

| Table | Contents |
| --- | --- |
| `Accounts` | `Id uuid`, `CreatedAt`, `PrimaryEmail` (lower-cased, unique), `DisplayName`, `ProfileId`, `PersonalSpaceId`, `InboxId`, `NextSeq bigint`, `StorageBytes bigint`, `DeletedAt` |
| `AccountLogins` | `AccountId`, `Provider` (email, google, microsoft, apple, github), `Subject`, `Email`, `LinkedAt`; unique `(Provider, Subject)` |
| `Devices` | `Id uuid` (the client's device ID), `AccountId`, `Name`, `Platform`, `ClientVersion`, `ClientSchema`, `CreatedAt`, `LastSeenAt`, `RevokedAt` |
| OpenIddict tables | Created from OpenIddict's documented schema for Npgsql. Refresh tokens rotate and are bound to the device. |
| `EmailCodes` | `Email`, `CodeHash` (PBKDF2), `ExpiresAt` (10 min), `Attempts` (max 5), `ConsumedAt` |
| `SyncEntities` | `AccountId`, `Kind`, `EntityKey`, `Groups jsonb` (name → `{hlc, values}`), `PurgedAt`, `LastSeq`, `UpdatedAt`; PK `(AccountId, Kind, EntityKey)` |
| `SyncChanges` | `AccountId`, `Seq bigint`, `ChangeId uuid`, `Kind`, `EntityKey`, `Op`, `Groups jsonb` (winning groups only), `OriginDevice`, `CreatedAt`; PK `(AccountId, Seq)`; unique `(AccountId, ChangeId)` |
| `Blobs` | `AccountId`, `Sha256`, `ByteSize`, `MediaType`, `StorageKey`, `WrappedKey`, `Nonce`, `ScanState` (Pending, Clean, Blocked, Failed), `ScannedAt`, `CreatedAt`, `RefCount`; PK `(AccountId, Sha256)` |
| `UploadSessions` | Resumable uploads: `Id`, `AccountId`, `Sha256`, `ByteSize`, `ReceivedBytes`, `TempKey`, `ExpiresAt` |
| `CalendarFeedTokens` | `AccountId`, `TokenHash`, `Settings jsonb`, `CreatedAt`, `RevokedAt` (P18) |
| `AuditEvents` | `AccountId`, `Kind` (sign-in, device added/revoked, account deleted, quota), `At`, `DeviceId`. **No content.** |

### 8.3 Push transaction (server)

Each change runs in a short PostgreSQL transaction (or a few changes together):

1. `SELECT ... FROM Accounts WHERE Id = @a FOR UPDATE`. This serializes sequence assignment per
   account.
2. If `(AccountId, ChangeId)` already exists in `SyncChanges`, the change is accepted as a replay.
3. Validate the change:
   - The `kind` is known.
   - Each group is known for that kind. Unknown groups are accepted only from clients with a
     schema at least as new as the group's.
   - Values are within the size limits. Text fields are capped at 1 MiB UTF-8 each, matching the
     client limits.
   - The entity's `create` group places it in the account's own space: its `SpaceId` must equal
     `Accounts.PersonalSpaceId`. Phase 3 replaces this check with space membership.
4. Merge into `SyncEntities` using `Chaos.Sync.Merge`, the same function the client uses. Collect
   the groups that won.
5. If any group won, take `Seq = NextSeq`, increment `NextSeq`, and insert `SyncChanges` with only
   the winning groups.
6. For a purge: mark `SyncEntities.PurgedAt` and keep the row as a tombstone, so late upserts for
   that key are rejected as `purged`. Also log a purge change.
7. Commit, then notify the account's open SSE connections through an in-process broadcaster.
   With more than one server instance, use PostgreSQL `LISTEN/NOTIFY` on channel
   `chaos_seq_<account>`. Write this from the start; it is a few lines.

### 8.4 Authentication flow for the desktop app

This follows RFC 8252 (native apps):

1. The desktop opens the system browser at the server's `/connect/authorize`, using
   authorization code + PKCE (S256), a `state` value, and `redirect_uri=http://127.0.0.1:<random port>/callback`.
2. The server's sign-in page offers **Email code** and the enabled providers. Email code sends a
   6-digit code that is valid for 10 minutes. Codes are stored hashed, with 5 attempts allowed and
   rate limits per email and per IP.
3. After sign-in, the browser redirects to the loopback listener (`ISystemBrowser`). The desktop
   checks `state` and exchanges the code at `/connect/token`.
4. The access token lives 15 minutes and stays in memory only. The refresh token rotates on use
   and is stored in `ISecretStore` under `chaos/service/refresh`. A refresh token that is used
   twice revokes the whole family (OpenIddict's default reuse detection; confirm it is on).
5. **Account linking.** A provider login whose verified email matches an existing account's email
   asks the person to also confirm with an email code before linking. This prevents takeover
   through a provider that does not verify email.
6. The device registers itself on first token use (`POST /v1/devices` with its device ID, name
   and platform). It shows up in Settings → Account → Devices, where any device can be revoked.

### 8.5 Encryption and security

- **In transit:** TLS only. HSTS is on, and HTTP is redirected to HTTPS. The client refuses
  non-HTTPS service URLs except `http://127.0.0.1` and `http://localhost` in development builds.
- **At rest:** PostgreSQL storage and its backups must be encrypted at rest by the host (a D2
  requirement). Attachment bytes are additionally encrypted by the application:
  - Each blob gets a random 256-bit data key and is encrypted with AES-256-GCM in 1 MiB chunks,
    each with its own nonce, plus an authentication tag per chunk.
  - The data key is wrapped by a per-deployment master key held in the host's key vault, behind
    an `IKeyWrapper` port. Development uses a local file key.
- **Authorization:** every query is filtered by the `AccountId` taken from the token, never from
  the request body. Tests try to read and write another account's entities, blobs and feeds.
- **Limits:**
  - push at most 500 changes and 4 MiB
  - pull at most 1,000 changes
  - 600 requests per minute per device
  - 20 sign-in attempts per hour per IP
  - storage quota per account (D5)
- **Logging:** request IDs, account IDs, kinds and counts. **Never** log task text, file names,
  email codes, tokens or request bodies.
- **CORS:** off. The ICS feed is a plain GET and needs none.

### 8.6 Account deletion

`DELETE /v1/account` requires a fresh sign-in, meaning a token issued within the last 10 minutes.
It deletes:

- `SyncEntities`, `SyncChanges` and `Blobs` rows
- the objects in storage
- feed tokens, devices, logins and OpenIddict tokens and authorizations
- the account row

It writes one `AuditEvents` row with no content. Devices learn about it the next time they call
the server (401 `account-deleted`). They then switch to offline-only and keep their local data,
which is what "Deleting your account deletes your data from the online service" means. The
Settings dialog explains this.

### 8.7 Deployment shape

The service is one stateless container (`deploy/Chaos.Server.Dockerfile`). Its configuration
comes from environment variables and the host's secret store:

- `ConnectionStrings__Chaos`
- `Blob__*`
- `Clam__Host`
- `Smtp__*`
- `Auth__Providers__*`
- `KeyVault__*`

Health endpoints:

- `/health/live` — always OK while the process runs.
- `/health/ready` — checks the database, blob store and scanner.

There is a migration command (`Chaos.Server migrate`), and the app refuses to start against an
unmigrated or newer database. The client's service URL comes from a build-time
`chaos.service.json` (committed with the production URL once D3 is known), and the
`--service-url` launch argument overrides it for development.

### 8.8 Server tests

- `tests/Chaos.Server.Tests` uses `WebApplicationFactory` with a **real PostgreSQL** database.
  The connection string comes from the environment variable `CHAOS_TEST_PG`. If that is unset,
  the tests start PostgreSQL through Testcontainers, which needs Docker (checked in P0).
- If neither is available, the tests **fail** with a message explaining how to provide one. Never
  skip them silently.
- `eng/verify.ps1` gains `-NoServer`, which skips the server test project only, prints a warning,
  and records "server tests not run" in the output. Progress notes must say when it was used.

---

## 9. Attachments design

### 9.1 Local model (P9)

**Files.** Attachment bytes live in the data root at `attachments/<first 2 hex>/<sha256>`, written
through `IAttachmentFileStore`:

1. Write to a `.partial` file and hash it while writing.
2. fsync, then rename into place.
3. Identical bytes are stored once.

**Table (migration 030).** `Attachments` has these columns:

- `Id`, `SpaceId`
- `OwnerKind` (Task, Diary, Pad, Page)
- `OwnerId`
- `FileName` (sanitized display name; the original kept as typed)
- `MediaType` (sniffed from the bytes' magic numbers, never trusted from the extension alone)
- `ByteSize`, `Sha256`
- `Width`, `Height` (images), `DurationMs` (audio/video)
- `Version`, `CreatedAtUtc`, `UpdatedAtUtc`, `DeletedAtUtc`

A second table, `AttachmentLocalState`, holds `Sha256`, `State` (Present, Missing, Downloading,
Failed) and `PinnedReason`. It is `LocalOnly`, because whether bytes are present differs per
device.

**In Markdown.** Attachments are referenced as:

- `![alt](chaos:attachment/<id>)` for images, shown in place
- `[name](chaos:attachment/<id>)` for any other file, shown as a file card with a preview where
  possible

Attaching a file inserts the reference at the caret. Deleting the reference does not delete the
attachment; the attachment panel lists it.

**Limits.** At most 100 MiB per file locally (configurable, matching D5). The total is not capped
locally.

**Adding files.** Drag and drop onto an editor or the attachment panel, paste (images and files
from the clipboard), or the file picker. The renderer reads the file and sends it in chunks
through RPC:

1. `attachments.begin(ownerKind, ownerId, fileName, size)` returns an upload ID.
2. `attachments.chunk(uploadId, index, base64)` sends chunks of 1 MiB each.
3. `attachments.commit(uploadId, operationId)` hashes the file, sniffs its type, stores it and
   inserts the row in one transaction, with an undo entry.

No filesystem path ever crosses RPC.

**Viewing.** P0 decides between a NeoAstra custom-scheme handler and RPC reads. If a safe,
host-validated custom scheme exists, prefer it. Otherwise use
`attachments.read(id, offset, length)`, which returns base64 chunks; the frontend builds a `Blob`
and an object URL.

- Images and audio/video play from blob URLs.
- PDFs show in an `<iframe>` of a blob URL, using the WebView's built-in viewer.
- The CSP gains `blob:` for `img-src`, `media-src` and `frame-src` only.
- Thumbnails for images over 2 MiB are generated in C# once, cached as local files and rebuilt on
  demand. They are not synced.

**Opening in another app.** "Open" copies the file to `%LOCALAPPDATA%\Chaos\opened\<id>\<safe name>`
and opens it through the shell after a click.

- Files whose type or extension is executable or script-like never open; they offer "Show in
  folder" and "Save a copy" only. Blocked extensions: `.exe .com .bat .cmd .ps1 .psm1 .vbs .vbe
  .js .jse .wsf .wsh .msi .msp .scr .hta .cpl .jar .lnk .url .reg .dll .appx .msix`. The list
  lives in one place and has a test.
- "Save a copy" writes to the export folder setting, as data export does.

**Permanence.** Deleting the owner tombstones its attachments. Undo restores them. Purge removes
rows, and removes a file when no attachment row references its hash.

**Backups.** A backup now consists of the database snapshot plus the attachment files it
references. Attachments are immutable, so backups share one content-addressed store,
`backups/attachments/<hash>`. Each backup's `.db.json` description lists the hashes it needs.
Retention deletes a file from the shared store only when no remaining backup lists it. Restore
copies missing files back into the data root. `BackupManager` and `DataProtectionService` change
accordingly. Test that pruning keeps shared files.

**Export.**
- Markdown export writes `attachments/<id>-<safe name>` beside the notes and rewrites
  `chaos:attachment/<id>` references to relative paths.
- The JSON archive adds an `attachments/` folder, and its manifest gains a checksum per file.
  Import verifies every checksum before writing anything.

**Page image backgrounds.** A page's front matter `background: attachments/plan.jpg` resolves to
the page's own attachment with file name `plan.jpg`. If several match, use the newest. If none
match, the panel shows "No attachment named plan.jpg on this page". Never resolve a web address;
`http` and `https` values are refused with a message. Daily-page defaults may name a scene or
colour only.

### 9.2 Synced attachments (P16)

- The attachment metadata row syncs like any entity. The bytes go through
  `/v1/blobs/{sha256}`:
  - `HEAD` checks whether the blob exists.
  - `POST` starts a resumable upload session.
  - `PUT` with a chunk index uploads a part.
  - `POST .../complete` finishes the upload; the server verifies the SHA-256, scans and encrypts.
  - `GET` downloads with range support.
- **Upload.** The client uploads after the metadata change is pushed, in the background, one file
  at a time, resuming after interruption.
- **Malware.** The server scans before marking a blob `Clean`. A `Blocked` blob is never served.
  The uploader's device marks the attachment "Blocked by malware check" (an attention item), and
  other devices show the same state without downloading.
- **Download.**
  - Files up to 5 MiB download automatically after their metadata arrives.
  - Larger files download when first opened ("Downloading… 34%").
  - Files in lists or projects the person marked **Keep available offline** (a device-local
    setting per container) are prefetched whatever their size. The pad and pages are always
    treated as pinned.
- **Quota.** The server rejects uploads over quota with `quota`. The client keeps the file locally
  and shows an attention item.

---

## 10. Calendar and meetings design

### 10.1 The calendar view (P4)

**Range query.** `calendar.range(from, to, filters)` returns entries:

- **Task on its planned date.** Timed if it has a planned time. It is a *time block* when it also
  has an estimate. The block ends at start plus estimate, or 30 minutes when there is no
  estimate, drawn with a dashed edge.
- **Task on its due date.** Shown as a deadline marker, visually distinct (flag icon and red
  edge), never as a block.
- **Recurrence projections.** One per future occurrence date in the range, labelled "repeats",
  read-only. Clicking opens the series' active task. They use the existing projection code,
  extended to any requested range of at most 62 days, with the cap enforced in C#.
- **Meetings** from P5/P6, read-only, in their calendar's colour.
- **Per-day totals:** estimated minutes of open tasks planned that day, available minutes from
  My Week availability (rule, override or default) minus busy meeting minutes, and an overload
  flag. Reuse `Capacity` and `WeekCalendar`; do not write a second capacity formula.

**Views.** Day, Week, Month and Agenda (the next 14 days as a list). Tasks without a time sit in
an all-day row at the top. A side panel lists **Unscheduled** open tasks, those with no planned
date (paged, filterable).

**Dragging** changes only the planned date (and time, when dropped onto a time slot) through the
ordinary task edit, so it is undoable:

- Dropping onto the all-day row clears the time.
- Due dates are never moved by drag. The task editor changes them.
- Dragging an unscheduled task in sets its planned date.

**Keyboard alternative.** Each entry has a menu with "Move to date…" (a date and time picker).
The grid supports arrow keys between days and Enter to open.

**Filters.** All of these compose with the Work/Personal focus:

- spaces (only Personal today, but the control exists for the data model)
- tags (including descendants)
- lists and projects
- show planned, show due, show meetings, show completed

**Library.** Use FullCalendar standard packages (`@fullcalendar/core`, `react`, `daygrid`,
`timegrid`, `list`, `interaction`) if D8 stays at the default. Pin exact versions and confirm the
MIT license for each package in `docs/dependency-versions.md`. Do not add premium packages.
Style it through CSS variables to match the theme tokens in `style.css`, including dark mode.

### 10.2 Calendar feeds (P5)

**Settings → Calendars → Add calendar feed.** The person pastes an `https://` (or `webcal://`)
ICS address and chooses a name, a colour and "counts as busy time".

- `webcal://` is rewritten to `https://`.
- Plain `http` is refused.
- The address is stored **in `ISecretStore`**, because a secret ICS address is a credential. The
  database keeps only a feed ID, name, colour, busy flag and the last fetch state.

**Fetching.** Every 15 minutes while the app runs, and on demand:

- conditional GET with ETag and If-Modified-Since
- 10 MiB response cap, 30 s timeout, at most 3 redirects, HTTPS only

**Parsing.** Parse with Ical.Net (pin it). Expand recurrences only inside the cache window, from
7 days back to 56 days ahead.

| ICS data | How it is handled |
| --- | --- |
| `TRANSP:TRANSPARENT` | free |
| `STATUS:CANCELLED` | hidden |
| The person's own `PARTSTAT=DECLINED` | hidden, when the feed's owner can be identified; otherwise shown |
| All-day events | shown at the top of the day; busy only if `TRANSP:OPAQUE` is explicit |
| Time zones | from `VTIMEZONE`/`TZID`, mapped to IANA |

Floating times use the profile zone.

**Cache.** Results go into `CalendarEvents`, which is `LocalOnly` because each device fetches for
itself. Each fetch replaces a feed's window in one transaction.

### 10.3 Google Calendar (P6)

This follows `docs/decisions.md` 2026-11-15:

- an **Internal** OAuth consent screen in a SilverKey-owned Google Cloud project
- a **Desktop**-type client with a loopback redirect and PKCE
- the **narrowest read-only scopes** that list calendars and read events

Before writing code, confirm the exact scope names in Google's current scope table. The
candidates are `calendar.calendarlist.readonly` and `calendar.events.readonly`. Record the result.

- **Client ID and secret.** They are not committed. They come from git-ignored
  `src/Chaos.Desktop/google-oauth.json`, copied into the build output. Commit
  `google-oauth.example.json`. Google treats installed-app secrets as non-confidential, but the
  repository is still not the place for them.
- **Several accounts.** The person can connect several Google accounts. Each refresh token is
  stored in `ISecretStore` under `chaos/google/<accountId>`.
- **Choosing calendars.** The person picks calendars, including ones shared with them, and gives
  each a colour and a busy-time choice.
- **Fetching.** Every 5 minutes and on window focus, fetch `events.list` for each chosen calendar
  over the window (−7 to +56 days) with `singleEvents=true`, and replace that calendar's cached
  rows. The windowed refresh is deliberate: a sync token cannot be combined with a time window.
  Verify this in P6 and record it.

| Google data | Meaning in Chaos |
| --- | --- |
| `transparency == "transparent"` | free |
| `status == "cancelled"` | hidden |
| An attendee with `self == true` and `responseStatus == "declined"` | hidden |

The cache stores title, start, end, all-day flag, location, video link (`hangoutLink` or
`conferenceData` entry point), and an HTML link back to Google.

- **Settings that follow the account.** With an account, connection settings (calendar choices,
  colours, busy flags) are synced. Each device still signs in to Google once, with one click from
  the synced settings, because the refresh token never leaves the device. Record this as how
  Phase 2 meets "the connection follows you".
- **Disconnecting** deletes the cached events and settings rows (synced, so every device drops
  them), revokes the token at Google, and removes it from `ISecretStore`.
- **Failure states.** An expired or revoked token, a policy block by a Workspace admin, or
  offline. Each shows plainly in Settings and on the calendar ("Google Calendar: sign in again"),
  together with the last updated time.

### 10.4 Meetings in planning (P5, used by P6)

- **Busy time.** `BusyMinutes(day)` is the union of busy meeting intervals, clipped to that local
  day. Overlapping meetings are not counted twice.
- **My Week.** Day capacity becomes `available − busy`, floored at 0, and the column shows "2 h
  meetings". Capacity warnings use the reduced figure. `Capacity` in Domain gains a pure function
  for this; test overlaps, all-day events, DST days and meetings that cross midnight.
- **My Day.** It shows "Meetings today" in time order, beside the plan. It warns when the
  remaining estimates of the day's open tasks exceed the free time between now and the end of
  the available hours, minus meetings. Warnings are never acted on.
- **Task from an event.** "Make a task" creates a task through capture, titled "Prepare: <event
  title>", planned for the day before the event (or the same day if the event starts after
  noon). It is linked through `CalendarEventLinks(TaskId, Provider, CalendarId, EventId,
  EventStartUtc)`, which is synced. The task shows "From meeting <title>, <time>", with a link
  that opens the event in the browser.
- **Privacy.** Meetings never appear in exports of other people's data (there are none yet),
  dashboards (phase 4) or shared calendars (phase 3). Daily summaries include meetings only when
  "Include meetings in summaries" is on (default off). The AI receives meetings under the same
  switch.

---

## 11. Board and custom view design

### 11.1 Kanban (P2)

**What can be a board.** A list or project (and, from P3, a custom view) can be shown as a board.

**Board settings (migration 025).** `BoardSettings(Id, TargetKind (Container | View), TargetId,
GroupBy, SwimlaneBy, WipLimitsJson, HiddenColumnsJson, Version, timestamps, DeletedAtUtc)`, one
per target. They are synced.

**Columns**, chosen by `GroupBy`:

| GroupBy | Columns |
| --- | --- |
| Status (default) | Not started, In progress, Waiting on someone, Needs review, Done. Cancelled tasks are hidden, with a "Show cancelled" toggle. |
| Section | The container's sections in order, plus "No section" |
| Priority | Urgent, High, Normal, Low, None |
| Tag group | The person picks a parent tag; columns are its direct children, plus "None" |

Assignee columns arrive with sharing in phase 3.

**Swimlanes,** chosen by `SwimlaneBy`: none, priority, tag group, category (Work/Personal), and
list/project (for custom views that span containers).

**Moving a card** runs the ordinary command for that field:

- **Status:** `SetStatus`. Moving to Done asks the open-subtask question, exactly as the list
  does.
- **Section:** `Move`.
- **Priority:** an edit.
- **Tag column:** remove the old column's tag and add the new one, as one operation (one undo).
- **Within a column on a container board:** reorder through the existing rank rules, among the
  container's tasks.
- **Custom-view boards:** sorted by the view's sort; there is no manual order.

**WIP limits.** Per column, with a header showing "4 / 3" in the warning colour when over. It is a
warning only.

**Cards** show the title, due date (coloured as in lists), category edge, tags, subtask progress
"2/5", the GitHub mark for linked tasks, and the estimate.

**Keyboard.** Arrow keys move between cards. Enter opens. Each card has a menu with "Move to
column…" and "Move up/down". Tests cover it.

**Switching layout** between list, board and calendar is a per-target, device-local setting, and
never changes tasks.

### 11.2 Custom views (P3)

**Table (migration 026).** `SavedViews(Id, SpaceId, Name, Icon, Color, Pinned, Position,
DefinitionJson, LayoutJson, AlertsEnabled, Version, timestamps, DeletedAtUtc)`, synced. Name,
icon and colour come from the existing palette and a fixed icon set (lucide names, validated).

**Definition JSON, version 1:**

```json
{
  "v": 1,
  "match": "all",
  "items": [
    { "field": "status", "op": "notIn", "value": ["Done", "Cancelled"] },
    { "match": "any", "items": [
      { "field": "tag", "op": "hasAnyOf", "value": ["<tagId>"], "descendants": true },
      { "field": "priority", "op": "atLeast", "value": "High" }
    ]},
    { "field": "due", "op": "inRange", "value": { "relative": "next", "days": 7 } }
  ]
}
```

Limits: groups nest at most 4 deep, with at most 50 conditions in total.

**Field registry.** `src/Chaos.Application/Queries/ViewFields.cs` lists every field with its type,
allowed operators and SQL compiler. Each field compiles to a parameterized predicate inside the
existing query specification, so counts and rows stay identical.

| Field | Type | Operators |
| --- | --- | --- |
| space, list/project, section | id | is, isNot, in |
| status | enum set | in, notIn |
| priority | ordinal | is, atLeast, atMost |
| tag | id set (+descendants) | hasAnyOf, hasAllOf, hasNone |
| category | enum | is |
| planned, due, created, changed, completed | date/instant | before, after, on, inRange (absolute or relative: today, tomorrow, yesterday, this/next/last week, this/next month, next/last N days, overdue), isEmpty, isNotEmpty |
| estimate | minutes | lessThan, moreThan, isEmpty |
| text (title + notes) | text | contains (FTS prefix terms, as search does) |
| waiting person | text | contains, isEmpty |
| source | enum | is: App, GitHub (Agent arrives in phase 3) |
| has pad link / page mention / attachment | bool | is |
| recurring | bool | is |
| GitHub (D7) | repository, label, milestone, issue state, author, has linked PR, PR review state | as for id/text/bool |

Relative dates are resolved with the profile zone and first weekday at query time, so "this week"
moves on by itself.

**Layouts:**

- **List:** grouping (none, list, status, priority, tag group, due date bucket), sort (due,
  planned, priority, created, changed, title, manual if one container), sort direction.
- **Table:** chosen columns from the same registry, sortable headers, inline edit of title,
  status, priority, planned, due, estimate and tags. Each edit is the ordinary command with a
  version.
- **Board:** P2 board settings on the view.
- **Calendar:** P4 calendar filtered by the view.

**Sidebar.** Pinned views appear under "Views" with icon, colour and count. The count uses the
same specification, refreshed with the other counts. A view can be opened, edited, duplicated,
deleted (undoable) and reordered.

**Add task in context.** Quick add inside a view pre-fills the fields that the view's top-level
`all` conditions fix to one value:

- list (`is`)
- status (`in` with one value)
- priority (`is`)
- category (`is`)
- tags (`hasAnyOf` or `hasAllOf`: add all)
- planned/due (`on` an absolute or relative single day)

Fields under `any` groups are not pre-filled. The preview shows what was filled in.

**Save a search.** The search page's current text and temporary filters become a definition
through "Save as view".

**Alerts:**
- Device-local table `SavedViewMatches(ViewId, TaskId, FirstSeenUtc)`.
- When alerts are switched on, the current matches are stored without notifying.
- A background evaluator runs 5 s after relevant change events (coalesced), and every 15 minutes
  for relative-date views. It computes the current matches. Every new task ID creates a
  notification job with a new source kind `ViewAlert`, which respects quiet hours. Notifications
  are grouped: at most one per view per evaluation, "3 new tasks in My open bugs". Matches that
  left the view are removed.
- With sync, each device evaluates for itself. Acknowledging the notification syncs through P17's
  acknowledgement table, so it clears everywhere.

**Export.** "Export as spreadsheet" writes a CSV (UTF-8 with BOM, RFC 4180 quoting) of the
layout's columns to the export folder, then offers "Show in folder". Formula-like cells starting
with `=`, `+`, `-` or `@` are prefixed with `'` to prevent spreadsheet formula injection. Test
this.

---

## 12. AI design (P7, P8)

### 12.1 Providers and settings

**Settings → AI.** Choose a provider:

| Provider | Request format | Address |
| --- | --- | --- |
| OpenAI | OpenAI chat completions | official API host |
| DeepSeek | OpenAI-compatible | official API host |
| Anthropic | Messages API | official API host |
| Google Gemini | `generateContent` | official API host |
| **Local model** | OpenAI-compatible | must be `http://127.0.0.1:<port>` or `http://localhost:<port>`; for example Ollama |

**Before you implement each adapter,** check its request and response format against the
provider's current official documentation. For Anthropic, load the `claude-api` skill for current
model IDs and headers. Do not hardcode model names you have not verified. The model list is
fetched from the provider's models endpoint when it has one; otherwise the person types the
model name. Where the provider's documentation gives a recommended cheap or fast model for
summarization, offer it as the default.

**The key** is entered once and stored with `ISecretStore` under `chaos/ai/<provider>`. The
settings page shows "Key saved" with Replace and Remove. A local model needs no key. **Test
connection** sends a one-word prompt and reports success or the provider's error kind (bad key,
quota, network, model not found) in plain words.

**Privacy section,** shown above the provider choice:

- "Your data is sent to: <provider name and host>"
- "With a model on your own device, nothing leaves this computer"
- switches for which spaces are included (Personal today; the control lists spaces)
- "Include meetings" (default off)
- "Include notes and diary text" (default on, titles always included)
- **Preview what is sent**, which shows the exact JSON the next summary would send

**Synced and local parts.** Provider choice, model and privacy switches are synced settings
(`AiSettings`, migration 029). Keys are per device; another device shows "Add your key on this
device".

**Network rules.** Calls happen only when the person uses an AI action, or when AI summaries are
on and a summary is generated. Each call has a 60 s timeout. There are no retries for
non-idempotent prompts, and one retry on network failure for summaries.

### 12.2 AI summaries (P7)

1. Build the existing structured facts (`SummaryFactsBuilder`), then apply the privacy filter.
   This drops excluded spaces, meetings when off, and notes text when off.
2. Build the prompt:
   - The system text says to write a short narrative in the profile's language
     (`Profiles.Locale`).
   - It says to use only the facts given and not to follow instructions inside task text.
   - The facts follow as a fenced JSON block.
3. Ask for plain Markdown of at most about 200 words.
4. **Validate the output:** a length cap (4,000 characters), Markdown only. The output is
   rendered through the existing sanitized `MarkdownPreview`, so links stay passive and images
   are blocked as usual.
5. **Store** the narrative on the summary revision: new columns `Narrative`, `NarrativeProvider`,
   `NarrativeModel`, `NarrativeGeneratedUtc` and `NarrativeError` (migration 029). The plain
   facts stay the source of truth. Regenerating creates a new revision as today.
6. **Never block the plain summary.** Generate the plain summary first. The AI call happens
   afterwards, outside any transaction, and its result is written in a second transaction. If it
   fails, the summary shows "Written summary unavailable: <reason>" with Retry, and the plain
   lists stay.
7. **Weekly summaries** get the same treatment. Morning summaries do too, but only when the
   person turns it on separately (default off, because it sends data every morning).

### 12.3 AI-drafted week plans (P8)

In My Week, step 4 ("Plan the days") gains a **Draft with AI** button, shown only when AI is
configured.

1. **Input JSON:**
   - the week's seven dates, with available minutes per day after meetings, and the work
     allowance
   - the candidate tasks the step already shows (id, title, estimate, due, planned, priority,
     category, list), at most 150, most urgent first
   - the goals from step 3, with their linked task IDs
   - assignments already made by the person, marked `locked: true`
2. **Requested output:** `{ "assignments": [{ "taskId": "...", "date": "yyyy-MM-dd" }], "notes": "..." }`.
   Ask for JSON only. Use the provider's structured-output or JSON mode where it exists.
3. **Validate in C#, never trusting the model:**
   - Parse strictly.
   - Drop unknown task IDs, dates outside the week, past dates, and changes to locked
     assignments.
   - Drop duplicates, keeping the first.
   - Count everything dropped and show the count: "3 suggestions were ignored".
   - Never change a due or planned date.
4. **Apply** the result to the **draft** only (the existing `saveDraft`), highlighted as "AI
   suggestion" with **Keep all**, **Undo AI suggestions** (restores the draft as it was before),
   and per-task drag as usual. Confirming the week remains the person's step 5.
5. **Notes** are shown as plain text under the board.

---

## 13. App lock design (P11)

**Settings → Security → App lock**, off by default. Options:

- **Unlock with Windows Hello**, when `IUserPresenceVerifier` says it is available.
- Otherwise a **Chaos PIN** of 6–12 digits, stored as PBKDF2-SHA256 with 600,000 iterations and
  a random salt in `DeviceSettings`. After 5 wrong attempts, wait 30 s, doubling up to 15 minutes.
- **Lock when:** the app starts, after N minutes of no input (1, 5, 15, 30, 60; default 5), when
  minimized to the tray, and when Windows locks. Use NeoAstra window/visibility events where
  available, otherwise `visibilitychange` plus a host idle timer.

**What locking does.** The page renders a full-window lock screen and nothing else. The task tree
is unmounted, so the DOM holds no task text. Background engines keep running. While locked:

- Notifications show only "Chaos reminder" (no title), when the option **Hide reminder details
  while locked** is on (default on).
- The global capture shortcut opens the unlock prompt first, and capture continues after
  unlocking.

**Honest limits.** The lock is device-local and never synced. Settings states plainly that the
lock hides Chaos from people using this Windows session. It does not encrypt files; Windows
account security and BitLocker protect data on disk. This matches SPEC "protected by the
device's own security, with an optional app lock".

**Forgotten PIN.** Turning the lock off requires Windows Hello or the PIN. If both are
unavailable, the person can reset the lock from Settings only after confirming their Windows
account password through Windows Hello's fallback. If even that is impossible, the data folder
remains accessible to the Windows user (the lock is not encryption), and documentation says how
to remove the setting. Record this.

---

## 14. Place reminders (P19, gated on D1)

The spec describes place reminders on phones. Build P19 only if the owner decides that a phone
client is part of Phase 2.

If it is:
- Add `ReminderRules.Anchor = Place` with `PlaceName`, `Latitude`, `Longitude`, `RadiusMeters`
  (100–1,000), and `Trigger` (Arrive or Leave). This is synced.
- The desktop lets you create and edit them, with a map-less place picker: a name plus
  coordinates pasted or picked from recent places, and states "Fires on your phone".
- Delivery is entirely the phone client's job.

If D1 stays "no phone in Phase 2":
- Record that place reminders move with the phone client.
- Build nothing. A rule that cannot fire anywhere is a working-looking placeholder.

---

## 15. Ordered work packages

Every package follows the same pattern:

1. Read the listed design sections and files.
2. Write the migration, if there is one.
3. Update the sync catalog (from P1 on).
4. Add Domain logic with tests.
5. Add Application commands with tests.
6. Add Infrastructure stores and adapters with real-SQLite tests.
7. Add RPC and Desktop tests.
8. Add the frontend and its tests, plus the preview backend.
9. Update Help and docs.
10. Run the gate, write the progress note, and record decisions.

The steps below add only what is specific to each package.

> **Priority override (owner, 2026-11-17).** **P6a — Activity log and pushed UI refresh** is
> high priority and is the next package to implement, ahead of every other open package. It is
> placed after P6 so the numbering of later packages does not change.

### [ ] P0 — Close Phase 1 and prove the Phase 2 integrations

**Depends on:** nothing. **Migration:** none.

**Progress note 2026-11-15.** All eight spikes recorded in `docs/desktop-support.md` ("Phase 2
spikes"): #7 (FullCalendar registry facts, MIT, v7 restructure) verified; #1 documented the
NeoAstra custom-scheme capability without a running app (P9 uses the RPC chunk path); #2–#5 and
#8 are blocked — they need an interactive Windows desktop, an installed MSIX or an elevated
Docker daemon, none of which exists in the implementing environment. D6 is therefore PENDING
(no default): the Phase 1 installed checklist could not be run, so WP0/WP10/WP13 stay open and
this package is not ticked. The owner's instruction "implement PLAN2 up to P11" authorized
starting P1 while D6 is outstanding; decisions D1–D8 status recorded in `docs/decisions.md`
(2026-11-15 entry). No product code changed. Gate not applicable (docs only).

1. Run the installed-desktop checklist in `docs/acceptance.md` on a real Windows desktop, with an
   installed MSIX package. This closes Phase 1 WP0, WP10 and WP13. Record the results. If the
   owner waives any step, record the waiver with the owner's words and date (D6).
2. Write throwaway spikes under `spikes/phase2-<name>/`. Do not put spikes in product projects.
   Each one proves a capability and records the result in `docs/desktop-support.md`.
   1. **Local file viewing in the WebView.** Does NeoAstra 1.12.0 offer a safe custom-scheme or
      resource handler? If not, prove the RPC chunk → `Blob` → object URL path for:
      - a 50 MiB video
      - a PDF in an `<iframe>`
      - an image
      
      Measure memory. Record the chosen path, and the CSP changes it needs.
   2. **Microphone in the WebView.** Can `getUserMedia({audio:true})` and `MediaRecorder` run
      inside NeoAstra's WebView2? Does NeoAstra expose the permission request? If not, prove a
      C# fallback: `Windows.Media.Capture.MediaCapture` audio-only recording to an `.m4a` in a
      temporary folder, packaged and unpackaged. Record which one P10 uses.
   3. **Credential store.** Save, read and delete a secret with `PasswordVault`, both from the
      installed MSIX and from an unpackaged run. Confirm the secret survives an app update. If
      `PasswordVault` fails in either mode, try `CredWrite`/`CredRead` (advapi32) and record the
      choice.
   4. **Windows Hello.** Call `UserConsentVerifier` through
      `IUserConsentVerifierInterop.RequestVerificationForWindowAsync` with the NeoAstra window
      handle. Confirm the prompt appears in front of the Chaos window.
   5. **Loopback sign-in from the installed package.** Open the system browser and receive the
      redirect on `http://127.0.0.1:<random>/callback` from the installed MSIX. Check that the
      firewall does not prompt. Close the listener after one request or after 5 minutes.
   6. **Docker or PostgreSQL** for server tests on this machine. Record which one works for
      `CHAOS_TEST_PG` and Testcontainers.
   7. **FullCalendar** (D8). Render the week and month views inside the app with the theme
      tokens and dark mode. Check keyboard focus and RTL. Record the versions and licenses.
   8. **ClamAV.** Run `clamd` in Docker and scan the EICAR test string through `INSTREAM`.
3. Record the Phase 2 owner decisions answered so far (section 4) in `docs/decisions.md`.

**Exit:**
- The Phase 1 checklist has passed or been waived in writing.
- Each spike has a recorded result: works, works with this workaround, or blocked with the exact
  reason.
- No product code has changed.

### [ ] P1 — Sync-ready local database (no network)

**Depends on:** P0 and D6. **Migration:** `024_sync_foundation.sql`. **Read:** section 7, all of
it.

1. **Project setup.** Create `src/Chaos.Sync` (net11.0, BCL only) and `tests/Chaos.Sync.Tests`.
   Add both to `Chaos.slnx` (hand-authored; see `docs/decisions.md` 2026-11-09) and to
   `eng/verify.ps1`'s fallback project list.
2. **HLC.** Implement `Hlc` in `Chaos.Sync` with parse, format, compare, `TickLocal(now)` and
   `Receive(remote, now)`. Test:
   - ordering
   - the counter on the same millisecond
   - the clock going backwards
   - receiving a clock from the future (keep it, but cap accepted skew at 24 h and report
     anything beyond)
   - round-trip formatting
3. **Name-based IDs.** Implement `NameBasedId.Create(namespace, name)` (UUIDv5) with the RFC 9562
   test vectors.
4. **Merge function.** Implement `Merge.Apply(stored, incoming)` for field-group maps. It returns
   the merged groups, the winning incoming groups and the replaced values. The client applier
   and the server both use it later. Test:
   - newer wins
   - older loses
   - equal clocks (same device): idempotent
   - unknown groups kept
   - purge tombstone
5. **Migration 024.** It contains:
   - the tables from 7.3: `SyncPending`, `SyncApplyState` seeded with `(1, 0)`, `SyncState`
     seeded disabled, `SyncClocks`, `SyncOutbox`, `SyncInboxParked`, `SyncAttention` and
     `DeviceSettings`
   - `DeletedAtUtc` for link tables that lack it (`TaskTags`, `SubtaskTags`, `ScratchPadTags`,
     `PageTags`, `WeekGoalTasks` and any other found by the guard test), with their unique
     indexes changed to partial `WHERE DeletedAtUtc IS NULL`
   
   Then update every read and write of those link tables: queries filter tombstones, and removal
   becomes a tombstone. **Search the whole solution for each table name.** The tag merge, purge
   and archive code all touch them. Update `DataPurge` so a purge removes tombstoned links too.
   Raise the schema ceiling.
6. **Device ID.** Seed `DeviceSettings.DeviceId` at startup if it is missing. Make sure backup
   restore and archive import keep the current device's `DeviceSettings` and never import
   another device's. Add `DeviceSettings` and all `Sync*` tables to `JsonArchive.ExcludedTables`
   with reasons.
7. **Catalog.** Write `SyncCatalog.cs` covering **every** table (section 7.2), and the guard test
   `SyncCatalog_EveryTableAndColumnClassified`. Classify the existing GitHub tables as 7.11 says.
   Record any judgment call as a comment.
8. **Triggers.** Write `SyncTriggerInstaller` with its hash check. Call it from `StorageBootstrap`
   after migrations and after the search index check. Test:
   - triggers exist after startup
   - recreation when the catalog changes
   - no recreation when it does not
9. **Flush hook.** Add the `ISyncCapture.Flush` hook to the write transaction in
   `SqliteApplicationStore`. It runs before every commit, and is a no-op that deletes pending rows
   while sync is disabled. Test:
   - a rolled-back write leaves no outbox row
   - disabled sync leaves `SyncPending` empty after commit
   - enabled sync writes one outbox row per entity, with exactly the changed groups
   
   Add one test per catalog kind, generated from the catalog, that performs a representative
   change through the real commands.
10. **Deterministic IDs.** Switch to `NameBasedId` in every code path listed in 7.5. Tests:
   - creating the same day plan or daily page on two fresh data roots gives the same ID
   - completing the same occurrence on two data roots gives the same successor task ID
11. **Applier.** Write `SyncApplier` (7.4) and the unique rules (7.6), callable from tests with
   change lists. There is no server yet. The core test harness is "two data roots, A's outbox
   applied to B and B's to A, in either order", and it must converge to identical synced
   columns. Write these tests:
   - edits to different groups both survive
   - same group: newer wins, and a `sync.merged` history entry holds the replaced value
   - delete vs edit
   - restore after delete
   - purge
   - child before parent (parking, then success)
   - duplicate tags created offline (`MergeTag`)
   - duplicate list names (`RenameLoser`)
   - a legacy duplicate daily page (`AppendText`)
   - an occurrence completed on one device and skipped on the other (`KeepLowestRetireOther`
     plus attention)
   - a stale undo refused after a remote change
12. **Performance.** Measure the overhead of the triggers and flush on the Phase 1 representative
   dataset, if it exists; otherwise on 10,000 tasks. Ordinary task save must stay within the
   existing p95 target of 150 ms. Record the numbers.

**Exit:**
- Two data roots exchanging outboxes converge in every listed scenario.
- The guard test is green.
- Offline-only behaviour is unchanged (all existing tests pass).
- No network code exists.

**Progress note 2026-11-15.** Items 1–4 landed in commits 6f4154d..603b33f: Chaos.Sync (Hlc,
NameBasedId, Merge) with their tests, migration 024 with the link tombstones and the rebuilt
WeekGoalTasks, DeviceSettings seeding with archive exclusions and restore-safe device identity,
the SyncCatalog over every table with its guard test, and the trigger installer with the catalog
hash. Item 9's flush hook landed in b42e5b1: one outbox change per entity with exactly the pended
groups and fresh HLCs, a purge for entities removed in the transaction, nothing for rolled-back
writes, and only a SyncPending clear while sync is off. One test per synced catalog kind (added
in 6760afd) drives a real command and asserts the outbox caught it; the one kind no command can
reach (space) is documented in a guard test. Item 10 landed in b8023d4: name-based UUIDs for day
plans and items, week plans and assignments, daily pages, recurrence occurrences with their
successor tasks and copied subtasks, summaries and GitHub-imported tasks; existing rows keep
their random ids, so the duplicate rules still cover them. Items 11–12 landed in 53493a6:
SyncApplier merges per field group by HLC, applies purges as hard deletes with their dependents,
resolves unique breaks with the catalog's rules (RenameLoser, MergeTag, AppendText,
KeepLowestRetireOther, RepointChildren), parks children whose parent has not arrived in
SyncInboxParked and retries them after the batch, keeps replaced values as sync.merged history
(task, subtask) and page revisions, bumps the local Version so a stale undo refuses, refreshes
search documents, and receives remote clocks into SyncState.LastHlc. A purged or removed
duplicate keeps a clock watermark so a replayed change cannot resurrect it, and a missing parent
is detected before the write because deferred foreign keys only fail at commit. The two-data-root
harness covers all eleven scenarios and settles to identical synced columns and clocks; the
legacy-duplicate scenarios (daily page, occurrence) construct pre-P1 rows with random ids, since
the deterministic ids prevent new ones. Performance (Release, 10,000 tasks, 60 ordinary saves):
p50 4 ms, p95 6 ms, max 16 ms per save — the capture overhead is invisible against the 150 ms
budget. Gate: Domain 192/192, Application 47/47, Infrastructure 487/487, Sync 26/26,
Desktop 88/89 in the sandbox — the one failure (LaunchRouting named-pipe IPC) passes 89/89
outside it; the sandbox denies named-pipe connections, as it also does for `dotnet test`'s own
IPC, so verify.ps1's direct-run fallback is used. Frontend npm test 131/131. Slopwatch clean.
Offline-only behaviour is unchanged (sync seeds disabled; every pre-existing suite passes) and no
network code exists. D6 is still pending per the P0 note, so the package box stays unticked until
the owner answers it.

### [ ] P2 — Kanban board

**Depends on:** P1. **Migration:** `025_board_settings.sql`. **Read:** section 11.1.

1. Add the `BoardSettings` table and its catalog entry (groups `create`, `grouping` (GroupBy,
   SwimlaneBy), `limits` (WipLimitsJson, HiddenColumnsJson), `deleted`).
2. In Application, add `BoardCommands`:
   - get or create settings
   - update grouping and limits
   - `MoveCard(taskId, expectedVersion, toColumn, beforeTaskId | afterTaskId, operationId)`
   
   `MoveCard` translates the move into the existing commands, using **one** OperationId, so it
   produces one receipt and one undo entry. A status move to Done with open subtasks returns the
   existing "choose subtask handling" error, and the UI asks.
3. Add the board query `BoardQuery(target, focus)`. It returns columns, swimlanes, card DTOs and
   counts, using the existing query specification for rows. Cap it at 500 cards, and report "500
   of N shown, filter to see more".
4. Add RPC service `board` (`get`, `updateSettings`, `move`).
5. Frontend:
   - `Board.tsx`, with a view switch (List / Board) on list and project pages. The choice is
     remembered per container in local storage.
   - Native drag between columns and within a column, with a drop marker.
   - The card menu with "Move to column…".
   - WIP header warnings and a settings popover.
   - Extend the preview backend.
6. Tests:
   - Application: each column type maps to the right command; a tag column move is one undo
     entry.
   - Infrastructure: real-SQLite move, restart and undo.
   - jsdom: drag and keyboard moves.
   - Native check: drag a card to Done and see the task completed.

**Exit:** a project board grouped by status and then by section moves tasks with both pointer and
keyboard. Every move is undoable and survives restart.

**Progress note 2026-11-15.** Implemented in two commits on `phase-2-plan` (backend, then
frontend). Migration 025 adds `BoardSettings` with a deterministic name-based id
(`boardsetting:{targetKind}:{targetId}`) plus `ColumnTagId`/`SwimlaneTagId` for the tag-group
choices (recorded in `docs/decisions.md`, 2026-11-15 P2 entry, with the other calls: one
composed operation per card move, board rows from the board store rather than the query
specification, defaults returned without writing, and the accepted gap that purging a
container leaves an inert settings row). The sync catalog classifies the table (Merge,
grouping/limits groups, deterministic-index exemption) and the flush-coverage suite gained a
`board` scenario, so the guard tests enforce it like every other kind. `BoardCommands.MoveAsync`
composes the column's field change (status via the extracted `TaskCommands.ApplyStatusAsync`,
section, priority, tag swap) with the within-column rank reorder in one transaction — one
receipt, one undo entry ("Move task on board"), the open-subtask question surfacing exactly as
the list's. `BoardQueries` builds columns, swimlanes and cards (tags, subtask progress, GitHub
mark, 500-card cap with truncation flag); `board` RPC exposes get/updateSettings/move.
Frontend: `Board.tsx` (columns, swimlanes, WIP warnings, hidden columns, show-cancelled,
settings popover, open-subtask dialog), native drag and drop with the whole card as the source,
each column-in-lane cell as a drop zone with a drop marker, a List/Board switch on list and project pages remembered per container,
roving-tabindex keyboard moves with a card menu, a preview-backend model, and a Help update.
Gate (Release): Domain 192/192, Application 57/57, Infrastructure 492/492, Sync 26/26, Desktop
88/89 in the sandbox — the one failure (LaunchRouting named-pipe IPC) is the same known
sandbox denial P1 recorded; it passes outside. Frontend `npm test` 144/144 (131 pre-existing +
13 new). Slopwatch clean. Not done: the native pointer-drag check on a running desktop and
the installed-package checks need an interactive Windows session this environment does not
have (as in P0); a card keeps its swimlane — other lanes refuse the drop, since changing a lane
field is a task edit and `board.move` changes one grouping field. A move places the card just
before or after the neighbouring card the person saw (`beforeTaskId`/`afterTaskId`), not at an
index, so cards the board hides (focus, hidden columns, other tag columns) never shift where
it lands. The box stays unticked until the native check runs.

### [ ] P3 — Custom views, alerts and spreadsheet export

**Depends on:** P2. **Migration:** `026_saved_views.sql`. **Read:** section 11.2.

1. Add the `SavedViews` table (synced) and `SavedViewMatches` (`LocalOnly`), with catalog
   entries.
2. Domain: definition parsing and validation (`ViewDefinition`, version 1): depth, count, known
   fields, operator and type agreement, relative date resolution. This is pure code with a large
   test matrix.
3. Application: the field registry `ViewFields`. Then extend the query specification with a
   `Definition` criterion compiled to parameterized SQL. **Do not build SQL by string
   concatenation of values.** Write a parity test that runs a set of definitions against the same
   seeded data and compares them with built-in views where they overlap; for example, a
   definition equal to "Overdue" must return exactly the Overdue rows and count.
4. Add `SavedViewCommands`: create, update, duplicate, delete, restore, reorder, pin, and
   `ContextDefaults(viewId)` for add-in-context.
5. Add the GitHub field group, only if D7 is confirmed. It joins the existing GitHub link and
   mapping tables.
6. Alerts: `ViewAlertEvaluator` in Application, plus a host timer in `Program.cs` beside the
   reminder engine. Use the new notification source kind `ViewAlert`. Notification jobs currently
   require a task (see `docs/decisions.md` package 11); add a nullable `SavedViewId` and a check
   constraint in this migration, rather than storing alerts elsewhere. Record this. Test:
   - enabling stores the baseline silently
   - a new match notifies once
   - matches that leave and return notify again only after 24 h
   - quiet hours apply
7. CSV export: `ViewExport` writes to the export folder with formula-injection protection.
8. RPC service `views` (`list`, `get`, `save`, `delete`, `restore`, `reorder`, `run`, `counts`,
   `contextDefaults`, `export`, `fromSearch`).
9. Frontend:
   - `ViewEditor.tsx`: condition rows, nested groups, field/operator/value pickers, and a live
     preview count.
   - Sidebar "Views" section.
   - A table layout with column chooser and inline edit.
   - Board and calendar layouts reuse P2/P4 components (calendar after P4: if P4 is not done,
     hide the calendar layout option and add it in P4).
   - "Save as view" on search.
   - Quick add pre-fill inside a view.
10. Tests:
    - the definition matrix
    - parity
    - alerts
    - export quoting and injection
    - jsdom editor, table inline edit and pin

**Exit:** the spec's example views that do not need sharing can be built (for example "Waiting on
others", grouped by person; "Book club this month" as a calendar once P4 exists; "My open GitHub
bugs" if D7). Their sidebar counts equal their rows, and an alert fires once for a newly matching
task.

**Progress note 2026-11-16 (backend slice, commit 38e5ae8).** Done: migration 026
(`SavedViews` synced, `SavedViewMatches` local-only, both in the catalog and the task purge
paths; flush-coverage scenario for `saved_view`); `Chaos.Domain.Views` (`ViewDefinition` parse,
validate and canonical JSON, depth 4 / 50 conditions, `ViewFieldCatalog`, relative-date
resolution, `ViewLayout`); `ViewFields` resolver and `ViewPredicateCompiler` (parameterized SQL
in the existing candidate query, custom sorts with undated tasks last, `TaskView.Custom`);
`SavedViewCommands` (save, duplicate, pin, delete, restore, reorder, `ContextDefaults`) with
silent alert baselines on enable. Tests: Domain 248/248 (56 new), Infrastructure view tests
21/21 including parity with the built-in Overdue, Today, list, tag, Waiting and Recently
completed views, a per-field matrix, nested any/all, paging and hostile-value binding; full
Infrastructure run 520/520 after the catalog fixes, Application 61/61. Decisions are in
`docs/decisions.md` (2026-11-16). **Not done yet** (items 5–10): the D7 GitHub field group;
`ViewAlertEvaluator`, the `ViewAlert` notification kind (needs the nullable `SavedViewId` and a
rebuild of `NotificationJobs` in migration 026) and the host timer; `ViewExport` CSV; the
`views` RPC service, preview backend and Help text; every frontend item (`ViewEditor`, sidebar
Views, table layout, board layout on a view, Save as view on search, quick-add pre-fill); and
the editor/table/pin jsdom tests. Gate (`eng/verify.ps1`, Slopwatch, `npm test`) not yet run on
this slice. Smallest next action: the `views` RPC service, so the frontend can start.

**Progress note 2026-11-16 (RPC and frontend slice).** Done: `ViewRpcService` (`views`: list, get, save,
duplicate, setPinned, delete, restore, reorder, run, preview, counts, contextDefaults, export,
fromSearch); `SavedViewQueries` (run, draft preview, pinned counts from the same specification,
CSV build); `ViewExport` (UTF-8 BOM, RFC 4180, formula neutralizing, safe file names);
`ViewFromSearch`; the preview backend model; Help text. Frontend: `viewModel.ts`, `ViewEditor`
(condition rows, nested all/any groups to depth 4, relative dates, layout, pin, alerts flag, live
preview count), `ViewTable` (sortable headers over the loaded rows, inline edit of title, status,
priority, planned, due and estimate through the ordinary commands with the row version), the
sidebar Views section (pinned views with icon, colour and count, New view), the view header (Edit,
Duplicate, Export, Delete with Undo), list grouping, Save as view on search, and quick add inside a
view that pre-fills what the top-level all conditions fix (status is applied by a follow-up
set-status call, since capture drafts carry no status). Checked in the browser preview.
Tests: solution 963/963 (Domain 248, Application 74, Infrastructure 546 incl. 24 view tests,
Sync 26, Desktop 89), frontend `npm test` 163/163 (10 new: model round trips, editor build and
save, host refusal keeps text and reuses the operation, table inline edit and sort, refused edit,
pinned views in the sidebar), Slopwatch clean. **Still not done:** the D7 GitHub field group;
alerts (`ViewAlertEvaluator`, the `ViewAlert` notification kind with the `NotificationJobs` rebuild,
the host timer); the board layout on a view; inline tag editing in the table; table header sort is
client-side over loaded rows; the section field is not offered in the editor; the native desktop
run was not done. The package stays unticked. Smallest next action: alerts.

**Progress note 2026-11-16 (GitHub fields, D7).** Added the GitHub condition group: repository,
issue state, label, milestone and assignee, compiled to parameterized SQL over the issue link,
its stored baseline and the label and milestone mappings (no network). The editor offers the group
when a repository is subscribed (`views.githubOptions`). Author, linked pull requests and review
state are NOT built: the sync does not store them (see decisions, 2026-11-16 D7). Tests: Domain
258/258, GitHub view tests 7/7 against real SQLite (including an unreadable baseline and a "My open
GitHub bugs" view with a matching sidebar count), frontend views 12/12. Still open in P3: alerts,
board layout on a view, inline tag editing in the table, the native desktop run.

**Progress note 2026-11-16 (board layout on a view).** A view can be shown as a board: `board.getView`
(`ViewBoardQueries`), settings stored against the view, no section grouping, lanes by list, drops change
the column only (decisions, 2026-11-16). Tests: 7 real-SQLite view-board tests (cards across lists,
settings across restart, lane assignment, move to Done, subtask progress, deleted view, focus) and
4 jsdom tests (getView with the focus, card menu without Move up/down, a drop sends no position,
settings save against the view). Still open in P3: alerts, inline tag editing in the table, the
native desktop run.

**Progress note 2026-11-16 (view alerts, inline tags).** `ViewAlertService` (evaluate, deliver after quiet
hours, list, acknowledge) over a device-local `ViewAlerts` table (migration 027, LocalOnly in the catalog),
run from the reminder engine tick; `views.alerts` and `views.acknowledgeAlert`; `ViewAlertStack` in the
app. Deviations from the plan, all recorded in decisions (2026-11-16): own table instead of
`NotificationJobs.SavedViewId`, in-app presentation only, a 30-second cadence. Table tags are now
editable inline with the task editor's picker. Tests: 9 real-SQLite alert tests (silent baseline, one
alert per new match and no repeat, grouping across evaluations, 24 h return rule both ways, quiet hours,
cancel on disable, acknowledge idempotence, replacement, restart) and 3 jsdom tests (alert card, dismiss,
table tag cell). **P3 items now all built;** what remains before the box can be ticked is the native check
on a running desktop (drag, the table, the board, an alert arriving) and D6/P2's open native checks.

### [x] P4 — Calendar view

**Depends on:** P1 (P3 for the view layout). **Migration:** none. **Read:** section 10.1.

1. Application: `CalendarQuery.Range(from, to, filters, focus)` returns task entries, due markers,
   projections and day totals. It caps the range at 62 days and the entries at 2,000. Meetings
   are an empty list until P5. Reuse `Capacity` for totals.
2. Extend the recurrence projection to accept an arbitrary bounded range. Test every rule kind
   over a month that crosses a DST change.
3. RPC service `calendar` (`range`, `unscheduled`).
4. Frontend:
   - `Calendar.tsx`, built on FullCalendar (D8), with Day, Week, Month and Agenda.
   - The unscheduled panel with drag in.
   - Drag to change planned date and time.
   - Entry menu with "Move to date…".
   - Filters, and a sidebar entry "Calendar".
   - Due markers distinct from planned blocks.
   - Overload badges on day headers.
   - The custom-view calendar layout.
   - Preview backend.
5. Tests:
   - range query boundaries, timed vs all-day, projections, totals
   - jsdom: keyboard move dialog (FullCalendar's own drag is checked natively)
   - native check: drag a task between days and into a time slot, restart, confirm

**Exit:** a week of tasks shows planned blocks, due markers and repeats. Dragging replans without
touching due dates and can be undone. Overloaded days are flagged.

**Progress note 2026-11-16 (calendar view).** `CalendarQueries` (range, unscheduled), `calendar.range` and
`calendar.unscheduled`, `RecurrenceCalculator.ProjectRange`, the `Calendar` component on FullCalendar 6.1.22
(Day, Week, Month, Agenda; planned blocks, due markers, repeats; overload badges; unscheduled panel with drag;
entry menu with "Move to date…"; filters; arrow-key day cursor), a sidebar entry and palette command, and
the calendar layout of a custom view. Decisions are recorded in `docs/decisions.md` (2026-11-16, P4). Tests:
8 real-SQLite calendar tests (boundaries, timed vs all-day, blocks, 62-day cap, filters, totals, repeats,
unscheduled paging, view layout), 6 recurrence projection cases (every rule kind over a daylight-saving
month), 7 jsdom tests (move dialog, refused move, due marker, Plan…, filters, grid keys). Native desktop
check (drag between days and into a time slot) confirmed by the owner on 2026-11-16. Meetings are an
empty list until P5.

### [x] P5 — Calendar feeds and meetings in planning

**Depends on:** P4. **Migration:** `027_calendars.sql`. **Read:** sections 10.2 and 10.4.

1. Tables:
   - `CalendarSources(Id, Provider (Feed | Google), AccountRef, ExternalId, Name, Color, CountsAsBusy, Enabled, Position, Version, timestamps, DeletedAtUtc)` — synced
   - `CalendarEvents(SourceId, ExternalId, StartUtc, EndUtc, AllDay, StartDate, EndDate, Title, Location, VideoUrl, WebUrl, Busy, FetchedUtc)` — `LocalOnly`
   - `CalendarSourceState(SourceId, LastSuccessUtc, LastError, ETag, LastModified)` — `LocalOnly`
   - `CalendarEventLinks` — synced
2. Domain: `MeetingTime.BusyMinutes(day, intervals, zone)` and the free-time calculation for My
   Day. Test overlaps, all-day, midnight crossing and DST.
3. Infrastructure:
   - `CalendarFeedClient` (`ICalendarFeedClient`): HTTPS only, size and time limits, redirects
     capped and kept on HTTPS, conditional GET.
   - `IcsParser` on Ical.Net, with the event rules in 10.2.
   - Fixtures: a Google export, an Outlook export, an Apple export, recurring with exceptions, a
     cancelled instance, a declined event, floating times, all-day, and a malformed file.
4. Application:
   - `CalendarCommands` (add feed: store the URL in `ISecretStore` and the row in SQLite; rename;
     colour; busy; remove; refresh now).
   - `CalendarRefresher`, which fetches outside transactions and replaces the window in one
     transaction.
   - Background refresh every 15 minutes in the host, only while at least one source exists.
5. Integrate:
   - `CalendarQuery` returns meetings.
   - My Week capacity subtracts busy time.
   - My Day shows "Meetings today" and the fit warning.
   - "Make a task" from an event (10.4).
   - Summaries include meetings only when enabled.
6. RPC service `calendars` (`list`, `addFeed`, `update`, `remove`, `refresh`, `makeTask`).
   Frontend: Settings → Calendars, plus the meeting rendering in the calendar, My Day and My Week.
7. Tests:
   - parser fixtures
   - busy maths
   - capacity with meetings
   - no network while no source exists (fake handler asserts zero calls)
   - URL never written to SQLite or the archive (search the database file bytes for the URL in a
     test)
   - refresh failure keeps the old cache and shows the error

**Exit:** a real Google or Outlook secret ICS address shows meetings in the calendar and My Day. My
Week's hours drop by the busy time, and the feed URL is not in the database.

**Progress note 2026-11-16 (calendar feeds).** Migration 028 (027 was taken), `ISecretStore` over DPAPI, `CalendarFeedClient`, `IcsParser` (Ical.Net 5.2.4), `CalendarCommands`/`CalendarRefresher`, `MeetingTime` busy maths, meetings in the calendar, My Week capacity and My Day (with the fit warning), Make a task, the summary switch, `calendars` RPC and Settings → Calendars. Decisions are in `docs/decisions.md` (2026-11-16, P5). Tests: 10 domain busy-maths cases, 9 parser fixtures, 8 feed-client cases, 17 real-SQLite calendar cases (including a byte scan for the feed address) and 9 jsdom tests. **Still open:** the native check with a real secret ICS address.

### [x] P6 — Google Calendar connection

**Depends on:** P5. **Migration:** `028_google_calendar.sql` (`GoogleAccounts(Id, Email, Subject,
Version, timestamps, DeletedAtUtc)`, synced). **Read:** section 10.3 and `docs/decisions.md`
2026-11-15.

1. Confirm the scopes and the windowed-refresh behaviour against current Google documentation.
   Record both.
2. Infrastructure: `GoogleCalendarClient` using `Google.Apis.Calendar.v3` and the matching auth
   library:
   - the installed-app loopback flow through `ISystemBrowser`
   - a token store adapter that writes **only** to `ISecretStore`
   - calendar list
   - event list over the window
   
   Make sure the library's default file-based token store is **not** used.
3. Application: connect (sign-in, then read profile email), choose calendars (they become
   `CalendarSources` with Provider Google), refresh every 5 minutes and on focus, disconnect
   (revoke at Google, remove secrets, delete synced rows), and failure states.
4. Frontend: Settings → Calendars gains **Connect Google Calendar**. It lists accounts and their
   calendars, with toggles, colours and busy choices, last updated time, and errors in plain
   words.
5. Tests:
   - fake Google transport: list, events, declined/transparent/cancelled, token expiry → "sign
     in again"
   - revoke on disconnect
   - no requests when not connected
   - the token never appears in SQLite, logs or RPC
6. **Live check (release gate):** connect a SilverKey account in the installed app, see meetings,
   decline one in Google and see it disappear within 5 minutes, then disconnect.

**Exit:** the live check has passed, or is recorded as blocked with the reason (for example,
waiting on a Workspace admin to create the project).

**Progress note 2026-11-16 (Google Calendar).** Migration 029 (028 was taken), `GoogleAccounts` (synced) and `GoogleAccountState` (device-local); `GoogleCalendarClient` over plain REST (no Google SDK, so no file token store; decision recorded), installed-app loopback flow with PKCE through `ISystemBrowser`; `GoogleCalendarCommands` (connect, list calendars, choose, disconnect with revoke) and Google sources in `CalendarRefresher` (5-minute tick, refresh on window focus, only what is due); `googleCalendar` RPC and Settings → Calendars → Google Calendar; `google-oauth.json` git-ignored with an example. Scopes and the windowed-refresh rule were checked against Google's current documentation and are in `docs/decisions.md` (2026-11-16, P6). Tests: 10 client cases over a fake transport (list, events, declined/cancelled/transparent, paging, expiry, 401 retry, policy block, offline, revoke, PKCE sign-in, cancelled sign-in), 12 real-SQLite cases (no requests when nothing is connected, token only in the secret store and in no file or export, stable source ids, replace-the-window, due-only, expiry keeps the cache, blocked/offline not hammered, sign in on this device, disconnect and failed revoke, not configured) and 8 jsdom tests. **Not done: the live check (release gate).** It needs the SilverKey Internal OAuth client in `google-oauth.json`, an interactive desktop and a real account; the box stays unticked until it has passed or is recorded as blocked. Smallest next action: create the Desktop OAuth client in the SilverKey Google Cloud project, drop its `google-oauth.json` beside the app, then connect, decline a meeting in Google, see it vanish within 5 minutes, and disconnect.

**Live check (2026-11-16).** Passed on a real Google account in the app: set up the client in Settings, connect, meetings show in the calendar, and a meeting added in Google appeared automatically on the 5-minute refresh. A meeting assigned in Google also appeared on its own, and after it was declined/cancelled in Google it disappeared from Chaos. After Disconnect every Google meeting was gone from the calendar. The live check has passed, so P6 is ticked. Not looked at: Google's own permissions page to confirm that the access was revoked there (the revoke call is best effort and Chaos says so when Google cannot be reached).

### [ ] P6a — Activity log and pushed UI refresh (implemented 2026-11-17; native check pending)

**Progress note 2026-11-17.** Implemented on branch `event-stream`. Backend, host and frontend are done and tested (Infrastructure 13 new tests, Desktop 5, jsdom 6; the existing suites pass). Decisions and deviations (strict mode per async flow, summary entities, retention numbers, `IChangePublisher` deleted) are in `docs/decisions.md` (2026-11-17). **Not done:** the native check in step 6 (My Day updating within about 1 s from the reminder engine or a GitHub sync, and the logo animating during a GitHub sync with no status poll), which needs the desktop app. The box stays unticked until the native check passes, as with P6.

**Priority.** Added by the owner on 2026-11-17. This is the next package to implement. It goes
ahead of every other open package, including the open items of P0–P3 and P7 onwards. Later
consumers (reminder resync, view alerts, GitHub wake-up, P15, the 7.10 editor rule) build on it.

**Depends on:** P1 (triggers and `SyncPending`). **Migration:** `030_activity_log.sql` (the next
free number when written; update Appendix A if it is taken). **Read:** section 6.4, 7.3,
`SqliteApplicationStore.cs`, `ChangePublisher.cs`, `SyncFlush.cs`, `SyncCatalog.cs`, the four
schedulers in `src/Chaos.Desktop/Platform/`, and the frontend files listed in step 5.

1. **Migration and catalog.**
   - Create `ActivityLog`, `ActivityLogEntities`, `ActivityCursors` and `JobRuns` (6.4).
   - Add them to the sync catalog as `LocalOnly`.
   - Exclude them from the JSON archive and from restore merges.
2. **Application.**
   - `ActivityScope` (Source, Action, OperationId, JobRunId).
   - `IActivityRecorder` (`Note` for device-only writes).
   - `IJobRuns` (`StartAsync`, `FinishAsync` with outcome and error).
   - `ActivityActions`, the closed list of action names.
   - Delete the unused `IChangePublisher` and `ChangeCollector`, or re-point them at the notifier.
     Record which in `docs/decisions.md`.
3. **Infrastructure.**
   - `ActivityCapture` in `SqliteApplicationStore.ExecuteAsync`, before the sync flush (6.4 rule 2).
   - Head notification after commit.
   - The `since` / `head` / `jobs` reads, and pruning.
   - Real-SQLite tests:
     - a commit writes exactly one row with its entities and versions
     - a rollback writes none
     - a transaction with no changes writes none
     - a delete becomes `Op=delete`
     - capture works with `SyncState.Enabled = 0` and with it on (the outbox is unchanged)
     - device-only notes are recorded
     - `Seq` follows commit order across interleaved callers
     - pruning respects cursors, the 7-day floor and the hard cap, and a pruned cursor gets `reset`
4. **Desktop host.**
   - Open the `ActivityScope` in every RPC method through one helper.
   - Wrap each pass of `ReminderEngine`, `CalendarRefreshScheduler`, `BackupScheduler` and
     `GitHubSyncScheduler`/`GitHubSyncEngine` in a `JobRuns` entry and a `schedule`/`sync` scope.
   - Add explicit notes where those passes write device-only tables (6.4 rule 3).
   - `ActivityRpcService` (`activity`: `since`, `head`, `jobs`) and the event
     `activity.committed`, coalesced to about 100 ms.
   - Tests:
     - a guard test that every `[NeoRpcMethod]` that writes runs inside a scope (unscoped writes
       throw under test)
     - a scheduler pass records a job run and, when it changed data, an activity row with its
       `JobRunId`
5. **Frontend.**
   - `activity.ts`: subscribe to `activity.committed`, coalesce, call `activity.since`, and
     dispatch to `useActivity(kinds, handler)` and `useJobs()`. On `reset`, reload everything.
     If the subscription fails, fall back to today's focus reload.
   - Migrate off polling:
     - `SyncLogo` (4 s), with `syncActivity.ts` observing job state from `useJobs()`
     - `GitHubSettings` and `GitHubTaskNotice` (1 s while syncing)
     - `Reminders` and `Summaries` (interval loads)
   - Refresh on relevant entity kinds, so host-made changes appear without a focus change:
     - My Day, My Week, the lists, Board, Calendar, saved views and the task editor
   - Out of scope: `App.tsx`'s 1 s shell-activation poll (not a data change; it moves only if
     shell activations later become an event).
   - Preview backend: an in-memory activity feed with the same shape.
6. **Tests.**
   - jsdom:
     - a pushed `activity.committed` reloads the affected view and not unrelated ones
     - `reset` reloads everything
     - the migrated components start no interval timers (fake timers assert none are pending)
     - focus reload is still the fallback when subscribing fails
   - Desktop: a change made by a scheduler pass reaches a subscribed client through `since`.
   - **Native check:**
     - With My Day open and focused, let the reminder engine or a GitHub sync change a task. It
       updates within about 1 s with no focus change.
     - The sidebar logo animates during a GitHub sync with no status poll running.
7. **Docs.**
   - Record in `docs/decisions.md`: the scope helper, the summary-entity choice per device-only
     table, the retention numbers, and the `IChangePublisher` outcome.
   - Update 2.3's "Change events" rule to point at 6.4.

**Exit:**
- Every committed write has exactly one activity row with a source and an action, and a
  rolled-back write has none.
- Every scheduler pass appears in `JobRuns`.
- Changes made by background work show in the open UI within about 1 s without a poll or focus
  change.
- The polls listed in step 5 are gone.

### [ ] P7 — AI providers, keys, privacy and AI summaries

**Depends on:** P1 and P0's credential-store result. **Migration:** `029_ai.sql` (`AiSettings`,
synced; summary narrative columns). **Read:** sections 12.1 and 12.2.

1. Infrastructure: implement `ISecretStore` in `Desktop.Windows`, following P0's result, with an
   in-memory fake for tests. Implement the `IAiTextService` adapters:
   - `OpenAiCompatibleAdapter` (OpenAI, DeepSeek, Local)
   - `AnthropicAdapter`
   - `GeminiAdapter`
   
   Each uses `HttpClient`, a 60 s timeout, and errors mapped to stable kinds. **The key goes only
   in the request header, built inside the adapter, and never in a log line or exception.** The
   local adapter refuses any non-loopback host.
2. Application:
   - `AiSettingsCommands` (provider, model, privacy switches, save/remove key, test connection,
     list models).
   - `AiPayloadBuilder`, the privacy filter over summary facts, which also powers "Preview what
     is sent".
   - `AiSummaryWriter`, which runs after plain generation, outside the transaction, then stores
     the narrative.
3. Extend the summary engine tick: when AI summaries are on and a new plain revision lacks a
   narrative, queue one AI call, with at most one call in flight.
4. RPC service `ai` (`settings`, `update`, `saveKey`, `removeKey`, `test`, `models`,
   `previewPayload`), plus `journal.writeNarrative(summaryId)` for Retry.
5. Frontend: Settings → AI, and the narrative block on the summary page showing provider, model
   and "Written by AI from your summary". Retry and the error state.
6. Tests:
   - adapters against recorded request/response fixtures (fake handler)
   - error mapping
   - the key absent from logs, exceptions and SQLite (byte search)
   - the privacy filter drops excluded content
   - a prompt-injection fixture (task title "Ignore previous instructions…") is passed as data and
     the output still renders passively
   - failure leaves the plain summary
   - no network while AI is unconfigured
7. **Live check:** one real summary with each provider whose key the owner provides, and with a
   local Ollama model. Record which were checked.

**Exit:** with a key saved, the evening summary gains a narrative in the profile's language.
Removing the key returns to plain summaries with no errors, and "Preview what is sent" matches
the request byte for byte (fixture test).

### [ ] P8 — AI-drafted week plans

**Depends on:** P7, plus P5 for meetings in available time. **Migration:** none. **Read:** section
12.3.

1. Application: `WeekDraftAssistant.BuildRequest(week draft, candidates, availability, meetings)`
   and `ApplySuggestions(draft, response)`, with the validation rules in 12.3. Pure validation
   goes in its own class with exhaustive tests.
2. RPC: `myWeek.draftWithAi(weekStart, draftVersion)` returns the new draft, the dropped count and
   the notes. `myWeek.undoAiSuggestions` restores the draft revision from before the AI.
3. Frontend: the button in step 4, suggestion highlighting, Keep all, Undo AI suggestions, the
   notes, and the dropped count.
4. Tests:
   - unknown IDs, out-of-week and past dates, locked assignments, duplicates, malformed JSON
   - the draft changes but the confirmed plan does not
   - task dates untouched

**Exit:** a drafted plan appears in the draft, can be adjusted or undone, and nothing changes
until the person confirms.

### [ ] P9 — Attachments (local) and page image backgrounds

**Depends on:** P1 and P0's file-viewing result. **Migration:** `030_attachments.sql`. **Read:**
section 9.1.

1. Add `Attachments` (synced) and `AttachmentLocalState` (`LocalOnly`), with catalog entries.
2. Infrastructure:
   - `FileAttachmentStore` (`IAttachmentFileStore`), content-addressed, writing atomically.
   - Media sniffing by magic numbers: PNG, JPEG, GIF, WebP, HEIC, PDF, MP4/MOV, WebM, MP3, M4A,
     OGG, WAV, ZIP-based Office files, plain text. Anything else is `application/octet-stream`.
   - Image dimensions and thumbnails using `System.Drawing` alternatives that work on .NET 11
     Windows. Choose a maintained library or Windows imaging APIs, pin it and record it.
3. Application: `AttachmentCommands` (begin/chunk/commit upload, rename, delete, restore, list by
   owner). Upload sessions live in memory and in a temp folder, expire after 1 hour, and are
   bounded to 4 concurrent sessions.
4. Purge, backup, restore and export changes as 9.1 describes. **This is the riskiest step; test
   it thoroughly:**
   - shared backup store pruning
   - restore brings files back
   - a JSON archive round trip with attachments and checksums
   - an archive with a tampered file is refused
   - Markdown export rewrites references
5. Frontend:
   - Attachment panel on the task editor, the diary entry editor, pad notes and pages.
   - Drag/drop and paste in `MarkdownEditor` inserting references.
   - `MarkdownPreview` renders `chaos:attachment/` images inline and file cards for others.
     Remote images stay blocked.
   - PDF, video and audio previews.
   - Open, "Show in folder" and "Save a copy", with the executable block.
6. Pages: resolve `background:` attachment names (front matter parsers in C# and TS stay in step,
   per the existing tests). Show the image behind the page as scenes are shown today.
7. Tests:
   - storage, dedupe, sniffing, undo, purge reference counting
   - the executable list
   - sanitizer: `chaos:attachment` allowed, `https` images still blocked
   - background resolution and the refusal of web addresses
   - jsdom paste/drop
8. Native check: paste a screenshot into task notes, attach a PDF to a page and preview it, set a
   page background from an attached image, back up, restore, and see all of them.

**Exit:** attachments work fully offline, survive backup/restore and archive round trips, and
never load anything from the internet.

### [ ] P10 — Pad drawing, photos and voice notes

**Depends on:** P9. **Migration:** `031_pad_media.sql`. **Read:** sections 9.1 and 3.1 (pad row).

1. Schema:
   - `ScratchPadItems.Kind` (0 Text, 1 Drawing).
   - `PadDrawings(PadItemId, StrokesJson, Width, Height, PreviewAttachmentId, Version, ...)`,
     synced. The `strokes` group is merged whole, latest wins.
   - `ScratchTaskLinks` gains a nullable `RegionJson` (`{x, y, w, h}` in drawing units) for
     tasks made from part of a drawing.
2. Drawing:
   - A canvas component that takes pointer events (mouse, pen with pressure, touch).
   - Strokes are stored as point arrays, smoothed with `perfect-freehand` (pin it; MIT).
   - Tools: pen, highlighter, eraser (stroke-level), colour (palette), undo/redo within the
     editing session.
   - Autosave works like pad text (the revision rules of `padAutosave.ts`).
   - When editing stops, a PNG preview is rendered and stored as an attachment, so export and
     search results can show it.
3. Make task from part of a drawing:
   - Select a rectangle, then choose Make task.
   - The quick-add box opens with an empty title, because there is no handwriting recognition
     until phase 4.
   - The task links back to the region. "Open in pad" scrolls to the drawing and outlines the
     region.
4. Photos: a pad note can hold images through P9 (paste, drop, file picker). "New photo note"
   creates a note with the chosen image at the top. Camera capture on the desktop is not
   included; the spec's camera capture is a phone feature.
5. Voice notes:
   - Record using P0's chosen path, with a 30-minute cap and a visible timer and level meter.
   - Stopping saves an audio attachment in a new note `🎙️ Voice note, <time>`, with a player.
   - If the microphone is denied or missing, show it plainly and offer "Attach an audio file"
     instead.
6. Export: drawings export as PNG plus the strokes JSON beside the Markdown. Archive round trip.
7. Tests:
   - stroke persistence and latest-wins merge
   - region link relocation (regions are fixed coordinates, so a deleted drawing gives "Missing")
   - autosave revisions
   - recording capability states (fake)
   - jsdom canvas interaction with pointer events
   - native check: draw with a pen or mouse, make a task from a region, record a voice note,
     restart

**Exit:** the pad holds drawings, photos and voice notes that persist, export and link to tasks.

### [ ] P11 — App lock

**Depends on:** P7 (`ISecretStore`) and P0's Windows Hello result. **Migration:** none. Settings
go in `DeviceSettings`. **Read:** section 13.

1. `Desktop.Windows`: implement `IUserPresenceVerifier`.
2. Application:
   - `AppLockCommands`: enable/disable, choose method, set/verify PIN with backoff, lock policy.
   - `AppLockState`: Locked/Unlocked, with transitions driven by start, idle, tray, Windows lock
     and unlock.
   
   The PIN hash uses PBKDF2-SHA256 (`Rfc2898DeriveBytes.Pbkdf2`) with 600,000 iterations.
3. Host:
   - The idle timer and Windows session-lock notification (`WTSRegisterSessionNotification`, or
     `SystemEvents.SessionSwitch` if available).
   - The tray and minimize hooks.
   - Notification content redaction while locked.
   - The capture shortcut routed through unlock.
4. Frontend: lock screen (unlock button, PIN pad), with the app tree unmounted while locked.
   Settings → Security.
5. Tests:
   - state machine transitions
   - PIN backoff timing with a fake clock
   - redacted toast content
   - the DOM contains no task text while locked (jsdom)
   - native check: lock on start, unlock with Windows Hello, idle relock, Windows lock relock

**Exit:** with app lock on, Chaos cannot be viewed without Windows Hello or the PIN, reminders
still fire without details, and the limits are stated in Settings.

### [ ] P12 — Online service foundation and accounts

**Depends on:** P1 and P0 (Docker/PostgreSQL). **Server migration:** `S001_accounts.sql`.
**Read:** section 8.

1. Project setup:
   - `src/Chaos.Server` (ASP.NET Core, `net11.0`) and `tests/Chaos.Server.Tests`, added to the
     solution and to `eng/verify.ps1`, with the `-NoServer` switch (8.8).
   - `deploy/docker-compose.dev.yml` with PostgreSQL, MinIO, ClamAV and Mailpit.
   - Resolve and pin Npgsql, OpenIddict and Testcontainers.
2. The server migration runner and S001 (Accounts, AccountLogins, Devices, EmailCodes,
   AuditEvents, plus the OpenIddict tables).
3. Authentication:
   - OpenIddict server with authorization code + PKCE for the public desktop client, refresh
     token rotation, and 15-minute access tokens.
   - The sign-in page: plain server-rendered HTML with no third-party scripts, accessible, and in
     English for now.
   - Email code sign-in with rate limits.
   - External providers configured from settings, enabled only when their settings exist.
   - Account linking with email confirmation (8.4).
4. Endpoints:
   - `POST /v1/devices` (register)
   - `GET /v1/account` (email, devices, storage used)
   - `DELETE /v1/devices/{id}` (revoke)
   - health endpoints
5. Client:
   - `IChaosServiceClient` with sign-in through `ISystemBrowser` (loopback, PKCE, state), token
     refresh with single-flight locking, and the refresh token in `ISecretStore`.
   - Settings → Account: "Create an account or sign in", with the reason shown: "Sync with your
     other devices". There is no prompt anywhere else, except one dismissible hint shown when the
     person opens Settings → Data & backups for the first time after P15 ships.
6. Tests:
   - email code flow, expiry and attempts
   - PKCE required
   - refresh rotation and reuse detection
   - account isolation
   - linking requires email confirmation
   - rate limits
   - logs contain no codes or tokens (capture the logger)
   - client sign-in against the in-process server with a fake browser that follows redirects
7. Live check (needs D3/D4): email sign-in through a real SMTP relay, and Google sign-in.

**Exit:** a desktop can create an account and sign in and out against a local server. Tokens live
only in the credential store, and server tests run against real PostgreSQL.

### [ ] P13 — Sync protocol end to end

**Depends on:** P12. **Server migration:** `S002_sync.sql` (`SyncEntities`, `SyncChanges`).
**Read:** section 7 (again) and 8.3.

1. Server:
   - `POST /v1/sync/push`, `GET /v1/sync/pull`, `GET /v1/sync/snapshot`, exactly as 7.12, using
     `Chaos.Sync.Merge`.
   - Ownership validation (`SpaceId`), size limits, kind and group validation against a server
     copy of the catalog's kind/group list. Generate it from `Chaos.Sync` shared constants, not
     by hand.
   - The 90-day change-log horizon job and `snapshotRequired`.
2. Client `SyncEngine` (`src/Chaos.Infrastructure/Sync/SyncEngine.cs`, hosted in `Program.cs`), a
   loop that runs only while `SyncState.Enabled = 1`:
   1. Push the outbox in batches, oldest first. Delete acknowledged rows. Rejected changes go to
      attention.
   2. Pull until `more == false`, applying through `SyncApplier`.
   3. Wait for a local change event (debounced 1 s), an SSE notification (P15), or 60 s.
   4. On network errors, back off exponentially from 2 s to 5 min with jitter, and resume
      immediately when connectivity returns.
   
   Never block the UI. Never hold the writer while waiting on the network.
3. A `sync.merged` history display in the task history viewer, and a "Changed on another device"
   notice in the editors (7.10).
4. Tests: the **two-client end-to-end harness**, two real data roots with real SQLite and real
   `SyncEngine`, against the in-process server and real PostgreSQL. Scenarios:
   - create on A appears on B
   - edits to different groups on A and B both survive
   - same-group conflict (newest wins; history on the loser holds the replaced value)
   - offline queue on A while B edits, then A reconnects
   - delete vs edit
   - purge
   - recurrence completed on both
   - a page edited on both (versions hold the losing text)
   - pushing the same changeId twice
   - server restart mid-push
   - a client with an older schema does not erase newer groups (simulate with a reduced catalog)
   - snapshot catch-up after the horizon
   - 1,000-task initial upload completes in batches without blocking a concurrent local edit for
     more than 150 ms

**Exit:** two desktops (or two data roots) stay in step within seconds of a pull, offline edits
merge as the spec describes, and every scenario above passes.

### [ ] P14 — Joining, adopting, signing out and deleting the account

**Depends on:** P13. **Migration:** `033_account_binding.sql` (columns on `SyncState`, plus a
synced `Devices` table for display names). **Read:** section 7.13 and 8.6.

1. The first device of a new account: bind the canonical IDs on the server, then upload everything
   (7.13 case 1).
2. Joining with local data: the choice dialog, showing counts on both sides. Then:
   - **Merge:** backup, adopt (rewrite profile, space and Inbox IDs across every FK column listed
     by the catalog, then `foreign_key_check`), snapshot apply, upload.
   - **Replace:** backup, reset, snapshot.
   
   Both run with the store exclusive and the reminder and GitHub engines paused, the same way
   restore does. If interrupted, startup resumes or rolls back using a marker file, as
   `DatabaseReplacement` does.
3. Sign out (keep or remove local data) and device revocation (a revoked device gets 401
   `device-revoked` and becomes offline-only with its data kept). Add "Reset device identity" to
   Settings → Data & backups, for copied data roots.
4. Account deletion (8.6), with fresh-sign-in confirmation, a typed phrase `DELETE`, and an
   explanation that local data stays.
5. Enable each sign-in provider as D4 allows. Each provider needs one live sign-in check.
6. Tests:
   - adopt rewrites every FK and fails safely mid-way (fault injection)
   - merge with overlapping tags, lists and daily pages
   - replace
   - interrupted join (resume and rollback)
   - sign out in both modes
   - revoked device
   - account deletion removes every server row and blob, and devices go offline with data kept

**Exit:** a second desktop with its own existing tasks joins the account and ends up with the union
of both, without duplicate tags or lists. Signing out and deleting the account behave as the
dialogs promise.

### [ ] P15 — Realtime, sync status and attention

**Depends on:** P14. **Migration:** none.

1. Server: the SSE endpoint, with PostgreSQL `LISTEN/NOTIFY` fan-out and keep-alives.
2. Client: an SSE listener that reconnects with backoff and triggers a debounced pull.
3. A status model: **Synced** (with the last time), **Syncing…**, **Offline — N changes waiting**,
   **Needs attention (N)**, **Signed out**, **Update needed**.
   - Show it as a small indicator in the sidebar footer. It never blocks and is never modal.
   - Clicking opens Settings → Account → Sync: status, pending count, devices, attention items with
     actions, and "Sync now".
4. Tests:
   - B sees A's change within 3 s on localhost through SSE without polling
   - status transitions with a fake engine
   - attention items rendered with actions (jsdom)

**Exit:** changes appear on the other device within seconds while online, and the person can
always see whether everything is synced.

### [ ] P16 — Attachment sync, malware checks and offline lists

**Depends on:** P15 and P9. **Server migration:** `S003_blobs.sql`. **Read:** section 9.2 and 8.5.

1. Server:
   - Blob endpoints: resumable upload, verify the hash, scan through `IMalwareScanner`, encrypt
     through `IKeyWrapper` and AES-GCM chunking, store through `IBlobStore`, and range downloads.
   - Reference counting from synced attachment entities: a blob is deleted when no live entity in
     the account references its hash for 7 days.
   - Quota (D5).
2. Client:
   - `AttachmentTransfer`: a background upload queue after the metadata push, and download rules
     (automatic up to 5 MiB, on open, pinned containers).
   - Progress in the UI. Missing-bytes states ("Not downloaded yet", "Downloading 34%", "Blocked
     by malware check", "Upload failed — retry").
   - "Keep available offline" on lists and projects (device-local).
3. Tests:
   - resume after interruption
   - hash mismatch rejected
   - EICAR blocked (fake scanner in unit tests; live ClamAV in one integration test that needs
     Docker)
   - encryption round trip and a tampered chunk rejected
   - range download
   - quota
   - another account cannot read the blob
   - a large file is downloaded only on open
   - a pinned list prefetches

**Exit:** an image attached on device A appears on device B. A 50 MiB video downloads only when
opened. A file containing EICAR is blocked on both devices.

### [ ] P17 — Cross-device behaviour: reminders, GitHub runner, calendars, summaries

**Depends on:** P15. **Migration:** `034_cross_device.sql` (`ReminderAcknowledgements`, synced;
`GitHubConnections.RunnerDeviceId`). **Read:** sections 7.5 and 7.11.

1. **Reminder acknowledgements.** Every Done/Dismiss/Snooze action writes an acknowledgement row
   keyed by the deterministic ID. On apply, a device's reminder engine marks matching jobs
   acknowledged and removes the toast from Windows notification history (by tag), or reschedules
   a snooze. "Done" also syncs as the task's status change. Test: dismiss on A, and B's pending
   or delivered toast clears; snooze on A reschedules on B.
2. **GitHub runner device** (7.11). Settings shows the runner. "Run GitHub sync on this device"
   verifies the account and resets local checkpoints. Non-runner devices do not run the engine.
   The runner marks dirty the linked tasks changed remotely. Test with two data roots and the
   fake GitHub API: one importer only, and an edit on B is written to GitHub by A.
3. **Calendar settings follow the account.** Google sources arrive on B with "Sign in to Google
   on this device to show these calendars". Feed URLs (secrets) do not sync; B shows "Add the
   feed address on this device" with the feed's name. Record this as how feeds follow.
4. **Summaries and plans.** Test that scheduled summaries generated on two devices for the same
   period converge to one row (deterministic ID), and that My Day items added on both devices
   merge.
5. **View alerts** acknowledge across devices through the same acknowledgement table.

**Exit:** a reminder dismissed on one desktop disappears from the other, and GitHub is driven by
exactly one device.

### [ ] P18 — Calendar subscription link

**Depends on:** P15. **Server migration:** `S004_calendar_feed.sql`. **Read:** SPEC Calendar
"Subscribe from elsewhere".

1. Server: `GET /v1/calendar/{token}.ics` (no auth header; the token is the secret: 32 random
   bytes, base64url, stored as a SHA-256 hash). It builds a VCALENDAR from `SyncEntities` of kind
   `task`:
   - Tasks with a planned date become VEVENTs: timed ones with the estimate as duration, or 30
     minutes when there is none; all-day otherwise.
   - Due dates become separate all-day VEVENTs titled "Due: …", when included.
   - Completed and cancelled tasks are excluded unless chosen.
   
   Settings (stored with the token): include planned, include due, include completed, show titles
   or only "Busy", which lists or projects (none = all). Output time zones as UTC or with
   VTIMEZONE, verified with the fixture tests. Send `Cache-Control: private, max-age=900`. Add a
   rate limit per token.
2. Client: Settings → Calendar → "Subscribe from Outlook, Google or Apple Calendar":
   - Create the link: shown once, with Copy.
   - Replace it (revokes the old one).
   - Turn it off.
   - Settings above.
   - A note that the link needs an account and that anyone with the link can see what it shows.
3. Tests:
   - ICS validity (parse the output back with Ical.Net)
   - privacy settings respected
   - a revoked token returns 404
   - another account's token cannot read data
   - titles hidden when chosen
4. Live check: subscribe from Google Calendar and Outlook and see tasks appear (they refresh on
   their own schedule; record the observed delay).

**Exit:** the link works in at least two outside calendar apps and can be replaced or turned off.

### [ ] P19 — Place reminders (only if D1 = phone client in Phase 2)

**Depends on:** D1 and the phone client plan. **Migration:** `035_place_reminders.sql`. **Read:**
section 14.

If D1 is "no", write the decision record and tick this package as **not applicable** with the
owner's decision quoted. Otherwise implement section 14 together with the phone client plan's
delivery work.

### [ ] P20 — Release gate: security review, packaging, deployment and acceptance

**Depends on:** all earlier packages. **Read:** sections 16 and 17.

1. **Security review.** Run the repository's `/security-review` skill over the Phase 2 diff, and
   fix or record every finding. Then walk this checklist by hand:
   - token storage
   - secret storage (AI keys, Google, feed URLs)
   - logging redaction (client and server)
   - authorization on every server endpoint
   - CSP changes
   - attachment opening rules
   - sign-in page CSRF and clickjacking headers
   - rate limits
   - prompt-injection handling
2. **Privacy and offline verification.** On a fresh install with nothing configured, observe no
   network traffic (the Phase 1 acceptance step 16 method). Then, with each feature configured in
   turn, confirm that only the expected hosts are contacted.
3. **Packaging.** The MSIX update from a Phase 1 install keeps all data. Migrations 024 onwards
   apply, the sync triggers install, and the device ID is created. Check that the installed app
   reads `chaos.service.json`.
4. **Server deployment** (needs D2/D3):
   - container image
   - migration step
   - secrets in the host vault
   - encrypted database and backups
   - health checks
   - a restore drill of the server database
   - a documented runbook in `docs/server-operations.md` (deploy, rotate keys, restore, delete
     an account by request, incident contacts)
5. **Performance.** On the representative dataset, record:
   - first full upload time
   - pull of 10,000 changes
   - a calendar month with 1,000 entries
   - a board with 500 cards
   - custom view count queries
   - attachment thumbnail generation
   
   Targets: the existing p95 150 ms for saves and previews; board, calendar and views first paint
   ≤ 300 ms; no UI freeze during sync, upload or download.
6. **Acceptance.** Extend `docs/acceptance.md` with a Phase 2 checklist that covers every row of
   section 3.1, run on two installed Windows devices against the deployed service. Record the
   real results.
7. Update `README.md`, `Help.tsx`, `docs/data-transfer.md` (new tables, attachments, archive
   format version) and `docs/dependency-versions.md`.

**Exit:** section 17 is fully satisfied with recorded evidence.

---

## 16. Verification additions

### 16.1 Behaviour matrix (add to the Phase 1 matrix)

| Area | Cases that must be demonstrated |
| --- | --- |
| Sync capture | Every catalog kind produces exactly its changed groups; rollback produces nothing; disabled sync produces nothing; triggers reinstalled on catalog change |
| Merge | Different groups combine; same group newest wins; replaced value in history; delete vs edit; restore; purge; unknown groups preserved; equal-clock idempotence |
| Natural keys | Deterministic IDs for every row in 7.5; every unique rule in 7.6 converges on both devices in either order |
| Protocol | Replay by changeId; batch limits; parking and retry; schema-too-old; snapshot catch-up; server restart mid-push; account isolation |
| Join | Adopt rewrites every FK; merge with overlaps; replace; interrupted join resume/rollback; sign out keep/remove; revoke; account deletion |
| Attachments | Dedupe; sniffing; executable block; undo/purge refcount; backup share/prune; archive checksums; Markdown export rewrite; resumable upload; malware block; encryption tamper; on-open download; pinned prefetch; quota |
| Calendar | Planned vs due rendering; projections over DST; time blocks; drag replans planned only; overload with meetings; feed parsing fixtures; Google states; busy maths |
| Views | Definition validation; SQL parity with built-ins; relative dates at week/month/DST boundaries; context defaults; alerts once; CSV injection protection |
| Board | Each grouping maps to the right command; one undo per move; WIP warnings; keyboard moves |
| AI | Adapter fixtures; error mapping; key never logged or stored; privacy filter; injection fixture passive; plain fallback; draft validation; no auto-confirm |
| Lock | State machine; PIN backoff; redacted notifications; no task text in the DOM while locked |
| Offline promise | No network with nothing configured; each feature contacts only its hosts |

### 16.2 Commands

The Phase 1 commands stay. `eng/verify.ps1` gains `tests/Chaos.Sync.Tests` and
`tests/Chaos.Server.Tests`, and the `-NoServer` switch. The development server runs with:

```powershell
docker compose -f deploy/docker-compose.dev.yml up -d
dotnet run --project src/Chaos.Server -- migrate
dotnet run --project src/Chaos.Server
```

Two development clients against it:

```powershell
dotnet run --project src/Chaos.Desktop -- --data-dir $env:TEMP\chaos-a --service-url http://127.0.0.1:5080
dotnet run --project src/Chaos.Desktop -- --data-dir $env:TEMP\chaos-b --service-url http://127.0.0.1:5080
```

Add matching entries to `.claude/launch.json` only for the server (port 5080) if a browser preview
of the sign-in page is useful.

---

## 17. Definition of done for Phase 2

Phase 2 is complete only when all of the following are true:

- **Offline promise.** A person who never creates an account can use the board, custom views and
  alerts, the calendar, calendar feeds and Google meetings, AI with their own key, attachments,
  pad drawings, photos and voice notes, and app lock, offline except for the services they chose.
  A fresh install with nothing configured makes no network requests.
- **Accounts and sync.** A person can create an account with email or an enabled provider. Their
  existing data moves into it. A second device joins, merging or replacing. Changes appear on the
  other device within seconds. Offline edits merge by field group with the replaced values in
  history, and sync status is always visible and never blocking.
- **Attachments.** Attachments work in notes, the diary, the pad and pages. They sync encrypted,
  are malware-checked, download large files on open, and keep pinned lists offline. Backups,
  restore and exports include them.
- **Reminders.** A reminder dismissed on one device clears on the others. GitHub runs from exactly
  one device.
- **Calendar link.** The subscription link works in outside calendar apps and can be replaced or
  turned off. Deleting the account removes all server data and leaves local copies.
- **Owner decisions.** Every decision in section 4 is answered and recorded. Place reminders are
  either built with a phone client or recorded as moving with it.
- **Evidence.** Tests, live checks and installed two-device acceptance have recorded commands,
  versions, nonzero counts and results in `docs/acceptance.md`. The server runbook exists.
- **No shortcuts.** No deferred feature appears as a working-looking placeholder. No secrets are in
  the repository, SQLite, backups, exports, logs or RPC. No disabled tests, empty catches or
  global warning suppressions were introduced to pass a gate.

---

## Appendix A — Migration numbers (provisional)

If a number is already taken when you start, use the next free number and update this table.

| Number | Package | Contents |
| --- | --- | --- |
| 024 | P1 | Sync bookkeeping, device settings, link-table tombstones |
| 025 | P2 | Board settings |
| 026 | P3 | Saved views, view matches, notification job view source |
| 027 | P5 | Calendar sources, events cache, source state, event links |
| 028 | P6 | Google accounts |
| 029 | P7 | AI settings, summary narrative columns |
| 030 | P6a | Activity log, entities, cursors, job runs (028 and 029 were actually used by P5 and P6, so 030 is the next free number; later rows shift when they land) |
| 030 | P9 | Attachments, attachment local state |
| 031 | P10 | Pad item kind, drawings, region links |
| 032 | — | Reserved (P11 uses `DeviceSettings`; use this only if a table proves necessary) |
| 033 | P14 | Account binding, devices display table |
| 034 | P17 | Reminder acknowledgements, GitHub runner device |
| 035 | P19 | Place reminders (only if D1) |
| S001–S004 | P12, P13, P16, P18 | Server: accounts/auth, sync, blobs, calendar feed tokens |

## Appendix B — Dependencies to resolve and pin

For each one: choose the latest stable version when its package starts, confirm .NET 11 (or
browser) compatibility and the license, pin it centrally, and record it in
`docs/dependency-versions.md`.

| Dependency | Package | Purpose |
| --- | --- | --- |
| Ical.Net | P5, P18 | ICS parsing and generation |
| Google.Apis.Calendar.v3 and its auth package | P6 | Google Calendar transport (decided 2026-11-15) |
| An image library or the Windows imaging APIs | P9 | Dimensions and thumbnails |
| `@fullcalendar/*` standard packages | P4 | Calendar grid (D8) |
| `perfect-freehand` | P10 | Smooth pen strokes |
| Npgsql | P12 | PostgreSQL |
| OpenIddict (server, ASP.NET Core, client web providers) | P12 | OAuth/OIDC server and external sign-in |
| Testcontainers.PostgreSql | P12 | Server tests when `CHAOS_TEST_PG` is unset |
| An S3-compatible client | P16 | Production blob storage adapter |

Do **not** add EF Core, an AI vendor SDK (unless an adapter genuinely needs it and the decision is
recorded), a second UI framework, or any CDN-loaded asset.

## Appendix C — Glossary

| Term | Meaning |
| --- | --- |
| Field group | Columns that change and merge together (7.2) |
| HLC | Hybrid logical clock; orders changes across devices despite clock differences (7.1) |
| Outbox | Local changes waiting to be pushed (7.3) |
| Applier | Code that applies server changes locally (7.4) |
| Adopt | Rewriting a joining device's profile, space and Inbox IDs to the account's (7.13) |
| Runner device | The one device that talks to GitHub for a connection (7.11) |
| Attention item | A sync situation the person should look at (7.9) |
| Pinned (offline) | A list whose attachments are always downloaded on this device (9.2) |
