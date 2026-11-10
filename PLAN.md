# Phase 1 implementation plan — desktop personal planner

Prepared 9 November 2026 from `SPEC.md`. This document is an implementation handoff, not a claim that the application exists. Implement the numbered work packages in order, keep their checkboxes current, and satisfy their exit criteria before proceeding. `SPEC.md` remains the product authority; explicit planning decisions below resolve omissions and contradictions without changing it.

## 1. Deliverable and decisions

Deliver a useful, installed desktop application that one person can open without an account or network, capture a task immediately, plan a week, choose today's work, record progress, and recover their data. Use the working name **Chaos** until the product name is decided.

### Non-negotiable stack

| Concern | Decision |
| --- | --- |
| Language | C# **15.0**, explicitly pinned; no downgrade to C# 14 and no floating `latest` language version. |
| SDK | .NET SDK **11.0.100-rc.3.26522.148**, available on this development machine. Target `net11.0`; the Windows host and Windows integrations target `net11.0-windows10.0.19041.0`. |
| Desktop framework | **NeoAstra 1.12.0**, with matching `NeoAstra.Tool` and `NeoAstra.Templates` versions. |
| UI | NeoAstra React template: React, TypeScript in strict mode, Vite, semantic HTML and CSS. C# owns business rules and persistence; TypeScript owns presentation and editor interactions. |
| Persistence | SQLite through **Microsoft.Data.Sqlite 10.0.14**, explicit SQL migrations and parameterized queries. No EF Core or database server. Verify this stable package on the selected SDK in work package 0. |
| .NET tests | **TUnit 1.81.16**, the latest stable version verified during planning. At implementation kickoff, query NuGet again and use the latest stable TUnit if it has advanced; record and pin the result. |
| C# editing | **Roslynk** for all application and test `.cs` edits, including semantic refactors. Follow `.agents/skills/roslynk/SKILL.md` and the server's actual tool schemas. |
| Quality | Repository TUnit and Slopwatch skills. Run Slopwatch after each batch modifying C#, project files, props or tests. |
| Build reproducibility | Central NuGet package versions, committed NuGet/npm lockfiles, pinned tools/SDK, CI locked restores. Resolve frontend versions once during bootstrap and commit exact versions. |

Microsoft documents C# 15 as the default for `net11.0`; this SDK is still prerelease. Set `<LangVersion>15.0</LangVersion>` rather than enabling unrelated preview features. See [C# 15](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-15) and the [.NET 11 SDK download](https://dotnet.microsoft.com/en-us/download/dotnet/11.0). Stable package versions were checked against the [NuGet V3 index](https://api.nuget.org/v3/index.json), including [TUnit](https://www.nuget.org/packages/TUnit/1.81.16), [NeoAstra](https://www.nuget.org/packages/NeoAstra/1.12.0), and [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite/10.0.14). Do not assume .NET 11 GA has already shipped.

### Platform assumption

Windows 11 x64 is the **first supported desktop deliverable**, based on the current Windows workspace and the spec's unresolved device order. An installed Windows package and real Windows desktop acceptance are mandatory. Keep domain, application and SQLite projects portable. macOS and Linux are future desktop targets using the same NeoAstra frontend and C# services, but are not represented as supported releases until their native integration and packaging tests run on those operating systems. Do not start mobile, a standalone website, or a backend service in this phase. This is a planning assumption, not an explicit platform decision in `SPEC.md`.

### Product decisions an implementer must follow

1. Phase 1 has one local profile and one Personal space, created automatically, plus an Inbox list. No account screen. Preserve space and profile IDs in the model for later phases.
2. Include all six quick-add languages named by the spec: English, French, Italian, Arabic, Indonesian and Spanish. Their rollout order is an open question; implement English first internally, then the other five before Phase 1 acceptance. Do not ship an English-only parser under the Phase 1 label.
3. The initial UI can be English. Keep UI strings in resources and use locale-aware dates/numbers; support mixed-direction text and Arabic input immediately. Parser language is independent of UI language. Plain summaries initially use the UI resource language; do not claim six fully translated interfaces.
4. My Day starts with no **manually selected** tasks on a new local date. Confirmed My Week assignments for that date seed it once. Display suggested tasks in a separate panel; never silently add overdue or yesterday's unfinished tasks. This reconciles the spec's empty-day wording with its weekly-plan seeding requirement.
5. A task's planned/due fields are task data. My Week assignments, My Day membership, reminders and follow-ups are profile-owned records. A weekly assignment does not silently change the task's planned or due date. The editor offers an explicit action to change planned date.
6. Lists and projects are alternative task containers in Phase 1: every top-level task belongs to exactly one list **or** project within Personal. A project does not also own lists. This removes ambiguous double counting; extend later only with a migration and a product decision.
7. Use six built-in statuses: Not started, In progress, Waiting on someone, Needs review, Done, Cancelled. Custom per-space statuses wait for shared spaces. Done and Cancelled are terminal, but only Done counts as completed.
8. Implement one level of subtasks. They support title, notes, status, tags and manual order; top-level tasks carry dates, estimates, recurrence, reminders and container membership. Indented paste deeper than one level is rejected with line-specific guidance, never silently flattened.
9. Templates, local diary/history, undo, search, backups, export and external-person waiting/follow-up are included: none has an explicit later-phase exclusion in the spec.
10. Manual archive/deletion is reversible. Nothing archives automatically on a weekly prompt. Permanent deletion and a full local-data reset require an explicit confirmation with a backup/export option.
11. No arbitrary scripting for custom recurrence. A custom pattern means combinations of supported interval/day/month/end fields exposed by an advanced rule editor.

## 2. Scope and traceability

| Spec feature | Phase 1 implementation | Acceptance evidence |
| --- | --- | --- |
| Instant offline start | Local profile, Personal, Inbox; no login/network prerequisite | Fresh install captures and restarts offline |
| Tasks and subtasks | Create/edit/move/reorder/delete/restore, six statuses, priority, dates, estimates, subtasks, reusable templates | Task and template service tests plus desktop workflow |
| Markdown | GFM source editor, toolbar, preview, rich-text-to-Markdown paste, checklist conversion, internal links | Markdown fixtures and rendered UI checks |
| Tags | Suggestions, colors, nested groups, task/subtask/pad tagging, rename/merge/delete, sidebar filters and drag-to-tag | Tag integrity and subtree-filter tests |
| Repeating tasks | Schedule/completion modes, patterns, ending, exceptions, no pile-up, history | Deterministic recurrence matrix |
| Quick add | Six languages, highlights, preview, disable-token action, multiline/indented paste | Per-language fixtures and editor interaction tests |
| Scratch pad | Personal typed Markdown items, autosave, tags, archive, selected-text-to-task with backlink | Restart and concurrent-edit tests |
| Diary and history | Dated Markdown updates, field-change feed, old snapshots, undo including deletion | Transaction/history/undo tests |
| Projects and lists | CRUD, sections, task order, project deadline/progress, archive | Container tests and real drag/drop |
| My Day | Date-scoped selection, order, explicit suggestions, completed items and carry-over | Midnight/restart/seed tests |
| My Week | Five-step review, 2–3 suggested goals, per-day assignments/capacity, confirmation, check-in | Draft/confirm/review/capacity tests |
| Plain summaries | Morning plan, evening daily and weekly summaries, journal, carry-over action | Historical fact and idempotency tests |
| Finding things | Search title/notes/subtasks/diary/pad, temporary filters, built-in views | FTS and filter parity tests |
| Reminders | Multiple absolute/relative reminders, snooze, actions, quiet hours, insistent option, local scheduling | Fake-clock tests and installed Windows notifications |
| Waiting on outside people | Name, optional acceptance criteria in notes, fixed/relative/inactivity follow-up, private My Day action | No communication/network; follow-up tests |
| Data ownership | Local backups, restore, Markdown/Org export, versioned JSON round-trip, documented initial importers | Round-trip and recovery fixtures |

**Explicitly outside Phase 1:** sync/accounts; cloud database/API; attachment storage and remote image loading; drawing/photo/voice capture; full calendar/day/month/time-block views or calendar connections; Kanban/table/custom saved views and alerts; AI calls, keys or drafting; comments/mentions/sharing/permissions UI; member delegation/rotation; GitHub/Org folder synchronization or file watching; MCP task-agent server; dashboards; timeline/dependencies; time tracking; organisation controls. The seven-column My Week planner is in scope, the separate calendar is not. Org **export/import** is a file operation, not the later bidirectional Org **connection**.

The desktop capture surface is quick add, a global shortcut, clipboard paste and a Personal-pad shortcut. Mobile camera/share sheets are excluded. A Windows share-target extension is not assumed; use clipboard capture in Phase 1 and document this platform boundary.

## 3. Implementation rules and dependency gates

### Roslynk workflow

The repository currently has no solution or C# application. Work package 0 establishes a real solution before semantic editing begins.

1. Discover connected Roslynk tools. If no connection exists, connect the installed Roslynk MCP using the documented `roslynk stdio` transport; installation alone does not expose tools to the agent. Never invent CLI edit verbs. See [Roslynk connection and tooling](https://github.com/mrpmorris/roslynk#readme).
2. Read server instructions and available tool schemas, open `Chaos.slnx`, and wait for indexing. Prove a C# 15 file and generated RPC symbols load correctly. A tool built on an older Roslyn can reject C# 15 even when `dotnet build` succeeds: upgrade to a compatible Roslynk release if needed.
3. Use Roslynk's supported file creation/edit facility for new C# and its semantic tools for existing symbols. Template scaffolding is allowed, then inspect generated C# through Roslynk. Use preview/check-only for broad refactors and handle stale-file errors by rereading.
4. After each coherent batch, run Roslynk diagnostics; fix new errors/warnings, then Slopwatch and the relevant tests. A full build is still required for generated bindings, native assets and release gates.
5. If the connected version cannot load C# 15 or cannot perform a required C# edit, report that exact tooling blocker. Continue independent documentation/configuration work; do not silently replace Roslynk with shell/regex edits or downgrade the language.

Markdown, JSON, SQL, CSS and TypeScript can use normal file-edit tools. Do not modify generated RPC outputs by hand. Do not overwrite existing untracked skill files or `skills-lock.json`; they existed before this plan.

### Package/bootstrap decisions

Use the published NeoAstra packages, not a moving source checkout. The upstream source inspected during planning was `4242271d69d540609215978058400fbac0fc4ceb`; its docs may be newer than a package. Verify the 1.12.0 template actually produces every API used before relying on source examples.

Create a local .NET tool manifest, align NeoAstra package/tool/template versions, and use the documented template sequence:

```powershell
dotnet new install NeoAstra.Templates::1.12.0
dotnet new neoastra-react -n Chaos.Desktop -o src/Chaos.Desktop --neoAstraVersion 1.12.0
dotnet new sln -n Chaos --format slnx
dotnet new tool-manifest
dotnet tool install NeoAstra.Tool --version 1.12.0
```

Then create/add the projects below, move package versions into `Directory.Packages.props`, and configure the SDK before restoring. Templates initially target .NET 10 and enable NativeAOT: change the target via project configuration and set `PublishAot=false` for Phase 1. NativeAOT is not a release requirement; revisit only after every selected dependency and Windows integration is proven compatible. Do not install an unrelated desktop framework to fix a host issue.

`global.json` pins the SDK exactly, permits prerelease, disables roll-forward and selects Microsoft.Testing.Platform as the test runner. `Directory.Build.props` sets C# 15.0, nullable reference types, implicit usings, deterministic compilation, warnings as errors and package lockfile restore. Pin versions centrally and avoid `VersionOverride` or suppressing warnings. Source generators and analyzer packages still need compatibility validation.

Use `TUnit` as the test executable's meta-package; it supplies assertions. Add explicit `TUnit.Assertions` only if the resolved setup genuinely needs it, with the same version. Do **not** add `Microsoft.NET.Test.Sdk`, VSTest adapters or Coverlet. Use `OutputType=Exe` and remove template-generated test `Program.cs`. See [TUnit installation](https://tunit.dev/docs/getting-started/installation/) and [running tests](https://tunit.dev/docs/getting-started/running-your-tests/).

## 4. Solution structure and ownership

```text
Chaos.slnx
global.json
Directory.Build.props
Directory.Packages.props
.config/dotnet-tools.json
src/
  Chaos.Domain/
    Tasks/ Tags/ Recurrence/ Planning/ Time/ Validation/
  Chaos.Application/
    Tasks/ Capture/ Planning/ Notes/ History/ Reminders/ Summaries/ Transfer/
    Abstractions/ Contracts/
  Chaos.Infrastructure/
    Sqlite/ Migrations/ Search/ Backup/ ImportExport/ Logging/
  Chaos.Desktop.Windows/
    Notifications/ Packaging/ Activation/
  Chaos.Desktop/
    Program.cs  Composition/ Rpc/ Serialization/ Platform/
    frontend/
      src/app/ src/features/ src/components/ src/editor/ src/styles/
      src/test/ package.json package-lock.json vite.config.ts
tests/
  Chaos.Domain.Tests/
  Chaos.Application.Tests/
  Chaos.Infrastructure.Tests/
  Chaos.Desktop.Tests/
  Fixtures/quick-add/ Fixtures/markdown/ Fixtures/import-export/
docs/
  dependency-versions.md desktop-support.md decisions.md acceptance.md
eng/
  verify.ps1 publish-windows.ps1
```

Dependencies point inward:

- `Domain`: BCL only. Pure validation, recurrence, date and capacity calculations. Name the entity `TaskItem`, never `Task`.
- `Application` → `Domain`: use cases, DTOs and narrow persistence/platform ports. No NeoAstra, SQL or React references.
- `Infrastructure` → `Application` + `Domain`: repositories, unit-of-work, migrations, search, backups/import/export. Own connections here.
- `Desktop.Windows` → `Application`: Windows-only notification and activation adapter. Multi-targeting does not leak WinRT types into application contracts.
- `Desktop` → the other projects: composition root, NeoAstra lifecycle, RPC wrappers and platform wiring. Use conditional Windows project references/targeting where needed.
- Test projects reference only their required layer. `Chaos.Desktop.Tests` uses the Windows TFM for installed integration tests; ordinary application tests stay `net11.0` and headless.

Create feature folders as they become necessary. Do not generate dozens of empty repositories, services or placeholder files. Implement a vertical slice through every layer before expanding the model.

### Core ports and command envelope

Define narrow ports with these responsibilities; exact names may follow repository conventions once established:

| Port/service | Responsibility |
| --- | --- |
| `TimeProvider` + `ILocalCalendar` | UTC now, profile zone, today/week boundaries, local-to-instant resolution |
| `IApplicationStore` / `IUnitOfWork` | Execute a bounded use case atomically; typed repositories/query methods, not arbitrary SQL from UI |
| `IIdGenerator` | Production GUIDs and deterministic test IDs |
| `IQuickAddParser` | Pure preview; text + captured context → draft, recognized spans and warnings |
| `IRecurrenceCalculator` | Pure next occurrence/range projection; no writes |
| `IPlanner` | Date-scoped day/week queries, draft/confirm, capacity and suggestions |
| `INotificationAdapter` | Capability report, schedule/cancel/show, activation events; no task mutation rules |
| `IFileDialogService` | Native user-chosen import/export/restore paths, opaque selection handles |
| `IChangePublisher` | Publish committed entity IDs/versions, triggering UI query invalidation |
| `IBackupService` | Validated snapshot creation/restore with retention |

Each mutation request carries `OperationId` (GUID), entity `ExpectedVersion` when editing, and a typed payload. The local profile is selected by the host, not an arbitrary renderer-provided identity. Return `OperationId`, committed versions, affected IDs and optional undo token. Persist a successful operation receipt in the **same transaction** as its mutations. Duplicate ID + same canonical payload returns the original result; duplicate ID + different payload is a conflict. This covers double-clicks and response loss independently of NeoAstra's transient RPC IDs.

Return stable errors: validation with field paths, not-found, version-conflict, operation-conflict, storage-full, storage-unavailable, cancelled and unsupported-capability. Log a correlation ID; do not expose stack traces, SQL or full local paths to the renderer. Check cancellation before starting work; cancellation/timeout after commit must be resolved by querying the operation receipt, never by blindly replaying a new mutation.

## 5. SQLite design

### Storage and connection rules

- Windows database: `%LOCALAPPDATA%/Chaos/data/chaos.db`; backups under `%LOCALAPPDATA%/Chaos/backups`. Development/testing can override the whole data root with an explicit `--data-dir` argument. Never store data under the checkout or executable directory.
- Keep WebView cache/profile data in a separate `webview` directory, not in SQLite or backups. It is not authoritative task storage.
- Open with foreign keys enabled, `journal_mode=WAL`, `synchronous=FULL`, a bounded busy timeout and parameterized commands. Verify PRAGMAs on actual connections. Use one serialized write queue with one writer connection; use separate short-lived read connections. Do not share a connection across concurrent requests.
- Database work runs on a background worker, never the NeoAstra UI thread. SQLite provider async methods do not provide asynchronous I/O: use synchronous SQL inside the worker, with async dispatch at the application boundary. See [Microsoft.Data.Sqlite async limitations](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async).
- Publish change events only after commit. A failed transaction produces neither a partial history record nor UI success. Maintain a bounded write queue and show actionable busy/full-disk errors.
- IDs are canonical GUID strings in both SQL and RPC. Persist UTC instants in fixed-width ISO 8601 UTC text, local dates as `yyyy-MM-dd`, local times as `HH:mm:ss`, and time zones as validated canonical IDs. Use integer enum values with constraints and minutes for durations.
- Use integer `Version` for optimistic concurrency, incremented once per entity update; `UPDATE ... WHERE Id = @id AND Version = @expected` must affect one row.
- Date/time checks require a date whenever a time exists, a valid zone whenever a timed value exists, and non-null completion/cancellation timestamps exactly when the corresponding terminal status requires them. A planned date after a due date produces a visible warning and remains user-overridable; do not silently swap the fields.

### Schema map

Every mutable aggregate has `Id`, `CreatedAtUtc`, `UpdatedAtUtc`, `Version`, and nullable `DeletedAtUtc` where reversible deletion applies. Add foreign keys, nullability/check constraints and indexes in the migration, not only C# validation. Never use a JSON blob as the entire primary task database.

| Table | Required data and constraints |
| --- | --- |
| `SchemaMigrations` | Integer version PK, checksum, applied instant; refuse altered previously applied migrations |
| `Profiles` | Stable local identity, display name, zone, locale, date format, first-weekday, planning/summary times and quiet-hour settings |
| `Spaces` | Personal space, local owner profile, kind; unique Personal per owner |
| `Containers` | Space ID, kind List/Project, name, normalized name, manual position, optional project deadline, archived instant; stable Inbox flag |
| `Sections` | Container ID, title, position; unique `(Id, ContainerId)` for composite references |
| `Tasks` | Space, container, optional section, title, Markdown notes, status, priority, estimate minutes, personal/work capacity category, planned local date/time/zone, due local date/time/zone, completed instant, cancellation instant, manual position, optional recurrence occurrence key |
| `Subtasks` | Parent task, title, Markdown notes, status, position; no independent space/container or recurrence |
| `Tags` | Owner profile, display name, normalized segment name, parent tag, validated color, position; unique active sibling name including root handling |
| `TaskTags`, `SubtaskTags`, `ScratchPadTags` | Composite keys and real target FKs; avoid an unconstrained polymorphic target ID |
| `DiaryEntries` | Task ID, author profile, entry instant, raw Markdown, edited instant, version/tombstone |
| `TaskHistory` | Task ID, operation ID, effective instant, actor, event kind, changed fields and versioned before/after aggregate snapshots; append-only, including tombstoned entities |
| `UndoEntries` | Profile, operation ID, label, versioned inverse payload and expected post-operation entity versions, applied instant, undone instant |
| `OperationReceipts` | Profile + operation ID unique, command kind, canonical payload hash, serialized result, commit instant |
| `ScratchPadItems` | Space, raw Markdown, manual position, archived instant, draft version; no required title |
| `ScratchTaskLinks` | Pad item, created task, source revision, source UTF-16 start/end and selected-text quote; stale anchors fall back to quote + item |
| `ContentLinks` | Source owner kind/ID, destination kind/ID, token ID; validate real supported targets in service; referenced text also remains in Markdown |
| `WeekPlans` | Profile, local week-start date, Draft/Confirmed, revision; unique `(ProfileId, WeekStartDate)` |
| `WeekGoals` | Week plan, title, order, optional project, manual outcome Pending/Achieved/LetGo |
| `WeekAssignments` | Week plan, task, local day, position; unique task per plan; day must be within its seven-day range |
| `WeekGoalTasks` | Goal/task links, unique pair |
| `DayPlans` | Profile + local date unique, seeded-from-week revision, initialized instant |
| `DayPlanItems` | Day plan, item kind Task/FollowUp, corresponding non-null target FK, position, origin Manual/Week, removed instant; enforce exactly one target and unique active target |
| `AvailabilityRules` | Profile, weekday, total minutes, optional work minutes; work minutes cannot exceed total |
| `WeekAvailabilityOverrides` | Profile, local date, total/work minutes override |
| `ReminderRules` | Profile, task, anchor Absolute/Planned/Due, offset or absolute instant, optional insistent interval, enabled state; mutually exclusive fields enforced |
| `ReminderDeliveries` | Rule + occurrence/generation unique, target instant, snooze-until, scheduled ID, state, last attempt, acknowledged instant; do not confuse dismissed with completed task |
| `WaitingDetails` | Task unique, outside-person text, started instant; no contact/account foreign key |
| `FollowUps` | Task, profile, Fixed/AfterWaiting/BeforeDue/AfterInactivity rule, computed due instant, generation, resolved instant; one active rule per task initially |
| `NotificationJobs` | Persisted task/follow-up/summary/planning-prompt projection, stable key, schedule generation, target instant, OS ID and retry state |
| `RecurrenceSeries` | Space/container, revision, rule JSON with schema version, zone, anchor, mode, end condition, template snapshot and completed/skip counters |
| `RecurrenceOccurrences` | Series, nominal local schedule key unique, task ID nullable until materialized, effective moved date/time, Completed/Skipped/Active, completion instant, series revision |
| `RecurrenceExceptions` | Series + nominal key unique, Skip/Move/Edit exception data |
| `TaskTemplates` | Space, name, versioned template payload for task fields/subtasks/tags/reminder offsets/recurrence defaults |
| `Summaries` | Profile, Daily/Weekly, local period start/end, captured-through instant, structured facts JSON, resource version, generated instant; unique period/type |
| `BackupCatalog` | Backup filename, schema/app version, creation instant, checksum and validation status; optional rebuildable catalog |

Use composite foreign keys to prevent task/container/section space mismatches. Index active tasks by container+position, status+due date, planned date, completed instant; index history/diary by task+time, deliveries/jobs by state+target, assignments by plan+day and tag adjacency by parent. Nullable unique fields need partial/expression indexes: SQLite's ordinary `UNIQUE` allows multiple NULL values. Document each nontrivial constraint.

FTS5 uses one search document per task, subtask, diary entry or pad item, including raw textual Markdown and the target type/ID. Maintain it in the same transaction as source updates/tombstones, with a tested rebuild command. Use the bundled SQLite runtime with FTS5 and verify availability on startup. Ordinary filters remain SQL predicates; do not make an FTS match control access or deletion state. Native FTS is lexical, not Arabic semantic stemming; add Unicode and Arabic-digit normalization in indexed/search input and test actual fixtures.

### Migrations, bootstrap and recovery

Keep numbered embedded SQL files in `Infrastructure/Migrations`; no runtime ORM auto-create. On startup: acquire the application data-root lock; inspect database version without mutating; validate a supported version; back up before upgrades; run pending migrations transactionally; create the local profile/Personal/Inbox in one idempotent seed; start services; show the UI. A newer schema produces a readable recovery error, never a destructive downgrade.

Failures leave the original database intact. Do not delete/recreate a database after a parse or integrity error. Offer opening the data directory, export where safe and choosing a validated backup. Never emit success until committed and durable. The app lock prevents a second process migrating or restoring the same root; secondary launches route capture/activation to the running instance.

## 6. Time and task semantics

### Dates and zones

Use `DateOnly` for all-day planned/due dates and `TimeOnly?` for their optional wall times. Never encode an all-day date as midnight UTC. A timed value retains its local date/time and intended zone; derive the UTC instant for reminders. Date-only overdue begins after that local date ends; timed overdue begins after the instant. Terminal and deleted tasks never appear in overdue/suggestion calculations.

Default to the device zone and culture once, persist the profile choices, and offer settings. Resolve an invalid DST local time forward by the transition gap; resolve an ambiguous time to the earlier instant and show its offset in the preview. Tests must cover both transitions. Changing profile zone reinterprets floating personal planning dates but retains explicitly zoned task appointments; recompute dependent jobs with new generations. Use half-open local-period boundaries converted to UTC for history summaries. The first weekday is a profile setting; a week is exactly seven local dates, not a UTC duration of 168 hours.

### Task operations

- Create trims exterior whitespace, rejects a blank title and enforces explicit limits: title 500 Unicode scalar values, notes/diary/pad item 1 MiB UTF-8, at most 500 lines per batch capture, nonnegative estimate up to 10,080 minutes. Show the limits in errors. Preserve entered text otherwise.
- Priority is None/Low/Normal/High/Urgent. Status changes write history and update terminal timestamps atomically. Completing an incomplete parent with open subtasks offers **complete all** or **keep subtasks open**; no silent child changes. Reopening restores the previous nonterminal status when known, otherwise Not started.
- Project progress is Done top-level tasks / (all nondeleted, non-Cancelled top-level tasks). Zero denominator displays an empty progress state. Subtask progress is separate; cancelled subtasks are excluded from its denominator. Archived containers remain searchable through an explicit archive filter.
- Reorder uses integer ranks spaced by 1,024; insert between neighbors if possible and compact that sibling set in a single transaction when no gap remains. Ties always sort by ID. Keyboard Move up/down offers equivalent behavior to drag/drop.
- Moving a parent updates container/section, clears incompatible section and keeps subtasks/diary/tags/history. Deleting a container offers moving contents to Inbox or reversible deletion of its contents; protect the Inbox from deletion. Archived items are not active suggestions.
- Template instantiation creates new IDs, resets all completion state and copies notes/tags/subtask structure. Store relative date/reminder offsets explicitly rather than copying an old absolute timestamp. Never copy diary, personal plan membership, delivery acknowledgements or history.

### Tags

Normalize tag identity with Unicode normalization and invariant case folding; preserve the display spelling. Do not silently merge distinct singular/plural names: suggest existing tags and let the user choose. A slash-delimited quick-add tag selects/creates nested segments; store adjacency, not a single path string. Group filtering includes descendants using a bounded recursive CTE. Reject cycles, empty segments and moves below descendants.

Renaming updates identity without replacing all task links. Merging reassigns every task/subtask/pad link with deduplication and handles child-name collisions recursively; preview the affected counts and apply atomically. Delete removes links but not tasks, and is undoable. Colors use a fixed accessible palette initially. Drag-to-tag adds that tag without moving the task's container.

## 7. Recurrence algorithm

Implement recurrence before planning/notifications depend on it. A series describes the rule/template; each occurrence has its own task ID and diary. Do not repeatedly overwrite one task and lose history.

### Supported rule structure

Versioned rule fields: mode `FixedSchedule` or `AfterCompletion`; frequency Day/Week/Month/Year; positive interval; selected weekdays; optional month day or ordinal weekday (1st–5th/last); optional month-of-year; anchor local date/time and zone; end Never/Count/UntilDate inclusive. Validate contradictory fields. Maximum interval is 999; expansion always requires an explicit bounded date range and count cap.

- Daily, every N days, weekdays, weekly on one/multiple days, every N weeks, monthly day-of-month, monthly Nth/last weekday, every N months, yearly and every N years are required.
- Fixed monthly day 31 skips a month without day 31; yearly Feb 29 skips nonleap years. Completion-relative month/year arithmetic clamps to the last valid day. Expose this difference in the advanced-rule preview.
- `AfterCompletion` accepts interval arithmetic for Day/Week/Month/Year; weekday sets, ordinal weekdays and explicit calendar-day selectors belong to `FixedSchedule` and are rejected in that mode combination. This keeps the advanced editor and calculator consistent.
- Count means the maximum number of actionable occurrences, including deliberately skipped ones. Automatically suppressed missed schedule slots do not consume count. Until compares the nominal local schedule date; a moved exception retains its original identity/count.
- A planned/due pair keeps its stored local offset relationship across occurrences; reminder offsets are recomputed. If only one anchor exists use it. A recurrence without a starting date uses today's local date and the next matching slot shown in preview.

### Materialization and no pile-up

1. Create the first matching occurrence with the series in one transaction. Enforce at most one active materialized occurrence per series with a database constraint.
2. Time passing alone never creates another active task. If unfinished, the existing occurrence remains visible. A passed due anchor makes it overdue normally; a recurring task with only a passed schedule anchor gets a **missed recurrence** badge and enters Overdue through that explicit recurrence predicate. An ordinary task's past planned date alone does not make it overdue.
3. Completing or skipping the active occurrence records its terminal state, then calculates the next nominal slot after it. For a fixed rule, suppress intermediate missed slots and choose the first slot on/after today's local date, respecting the prior key and time. A timed slot already passed today is missed; an all-day slot today remains eligible.
4. For completion-relative rules, add the interval to the completion's local date/time, not the original schedule. Inactivity does not create more tasks. Skipping a completion-relative occurrence advances from the skip instant under the same rule.
5. Materialize the next occurrence, copied subtasks all open, in the same transaction as completing/skipping the previous one. A unique `(SeriesId, NominalKey)` and operation receipt prevent duplicates across restart/retry.
6. Bounded future projections may be returned for the seven-day planner/Upcoming preview, labelled **projected** and read-only. Do not allow assigning such a projection to My Day/My Week or promoting it while another occurrence is active. Once completion advances the series, the corresponding real task becomes selectable. Do not materialize hundreds of future tasks. Full recurrence display on a calendar remains Phase 2.

Moving one occurrence writes an exception; editing one changes its task and exception snapshot. “This and future” creates a new series revision from the selected boundary, preserves completed history, updates the active/future projection and recalculates reminders. Ending a series cancels future generation without deleting history.

Undo completion must also undo the generated successor when that successor is untouched. If later edits/plan links/diary exist on the successor, report an undo conflict; offer an explicit reconciliation dialog rather than deleting the later work. Deleting a recurring occurrence must ask whether to skip this one or end the series.

## 8. Quick add and text capture

### Parser contract

`Parse(input, context)` is a pure C# operation. Context contains captured `NowUtc`, profile zone/culture/date format/first weekday, current container, known tags/containers and excluded token spans. Result contains original text, cleaned title, typed field proposals, UTF-16 source spans, language evidence and warnings. No save, clock access, tag creation or database writes during preview.

Tokenize while preserving original indices; normalize Arabic-Indic digits and case/diacritics in a parallel comparison representation with an index map. Do not return indices into normalized text. Resolve long phrases before short ones: recurrence, relative reminders/follow-up, due-date phrase, planned-date/time, estimate, tags/priority/container. A time inside “remind me 1h before” is not an estimate or planned time. Return conflicting date proposals to review rather than silently discarding one.

Required grammar families, implemented through table-driven language packs:

| Intent | English baseline | Language-pack requirement |
| --- | --- | --- |
| Relative date | today, tomorrow, in 3 days | Translations, weekday/month names and common abbreviations for all six languages |
| Weekday date | Friday, next Tue | Same/next semantics shared across language packs |
| Absolute date | 15 Dec, locale numeric dates, ISO date | Local month names and digits; ambiguous numeric dates follow settings and show preview |
| Due marker | by, due | Translated markers select due date; unqualified date selects planned date |
| Time | 3pm, 15:30, 10:30 | 12/24-hour and language words; no floating UTC assumptions |
| Repeat | every Monday, every other week, every 3 months after done | Translated frequency/interval/completion phrases |
| Reminder | remind me 1h before | Translated marker and duration units; require a valid anchor |
| Estimate | for 30 min | Duration phrase distinct from reminder |
| Metadata | `#tag`, `!high`, `/Reading`, `@Personal` | Sigils work across languages; priority aliases localized |
| Waiting/follow-up | waiting on Sam, follow up Friday | Personal outside-person field, no delegation or message sending |

Bare weekday means the next occurrence including today if its specified time is still upcoming; `next <weekday>` means that weekday in the next configured week. Yearless dates choose the next valid occurrence on/after today. A time without a date means today when future, otherwise tomorrow, visibly previewed. Plain `May` alone is never removed as a date; require a day number or date marker. User-disabled recognition keeps its exact text in the title. If all text was metadata, ask for a title before saving.

Known `/container` names can be quoted when they contain spaces. An ambiguous name produces a picker, not a guess. Phase 1 recognizes only Personal for `@`; an unknown/shared space produces an actionable warning, not a new shared space. Preserve unsupported `+name` text with a “member delegation comes later” explanation; use the separate waiting-person control for outside people.

### UI flow and paste

Debounce preview requests by roughly 150 ms, cancel obsolete ones, and discard stale responses by input revision. Show a plain input/editor with recognized-token chips/highlights and a field preview. Clicking a token disables that recognition; explicit field edits override parsed fields. Saving submits the displayed draft and its reference date/context version; do not reparse against a newer midnight silently. A mutation OperationId survives retry/reconnect.

Pasting multiple nonblank lines opens a batch preview. One root line becomes one task; child indentation becomes subtasks, stripping a single bullet/checkbox marker while preserving text. Normalize tabs to four spaces for parsing; reject mixed/ambiguous indentation and more than one child level. Show line-specific errors, allow correction, and commit the valid whole preview atomically only after the user chooses Save. Never quietly save a partial subset. One batch is one undo operation.

Required English examples are every example from `SPEC.md`, adapted only where Book Club/member assignment is outside Phase 1. Add at least one fixture per grammar family per language, including `demain`, `domani`, `غدًا`, `besok`, `mañana`, Arabic-Indic digits and mixed-language input. These are required behavioral tests, not just vocabulary-file snapshots.

## 9. Notes, scratch pad, diary and history

### Markdown editor

Reuse one editor component for task notes, diary entries and pad items. Store raw Markdown. Use an established locally bundled GFM renderer/parser, e.g. unified/remark + `remark-gfm`, and locally bundled syntax highlighting; lock exact packages in work package 0. Use Turndown plus its GFM plugin for rich HTML paste. Verify selected package APIs against their official docs before implementation.

Implement source/preview switch, headings, emphasis, strike, lists, checklists, links, quotes, inline/fenced code, highlighting and tables. Toolbar actions operate on the actual selection and preserve undo/caret. Shortcuts include Ctrl+B/I/K. A source textarea is acceptable if selection, keyboard access and accessibility work correctly; a full WYSIWYG editor is not required.

Disable raw HTML execution. Render through sanitized nodes with a restrictive URL policy: no scripts, `javascript:`, event handlers, iframes, remote styles or automatic remote image/network loads. An external link opens through a validated host opener after user activation. Image Markdown stays as source with an “attachments arrive later” placeholder. Never give Markdown content a native bridge or use unsanitized `dangerouslySetInnerHTML`.

Checklist conversion parses the Markdown AST, lets the user choose checklist items, creates subtasks in document order and records a conversion marker/token so repeated clicks cannot duplicate them. Preserve checked state as Done. Leave source text unchanged by default; offer explicit replacement with internal links. One conversion is atomic and undoable.

`[[` opens an autocomplete over tasks and pad items. Insert stable ID-based link tokens with a readable label; render them using local navigation. A renamed target keeps working; a deleted target shows a recoverable broken-link state. Never resolve internal links by title alone.

### Autosave and scratch task conversion

Save after approximately 500 ms of idle typing and on blur/navigation/close. Track monotonically increasing editor revisions and flush outstanding saves on shutdown before disposing storage. Show Saving/Saved/Failed. A failed save retains text and supports retry/exporting the draft; navigation must not discard it. Do not put authoritative drafts exclusively in browser local storage.

Each pad item is untitled Markdown with order, tags and archive. “Make task” on selected UTF-16 text opens the same quick-add preview and writes task + backlink atomically. Leave the source text untouched. Record source revision, offsets and quote; after source edits use a valid current match or open the item with the old quote, never scroll to unrelated text. Weekly review suggests archive; the user selects items and confirms.

Diary entry author is the local profile. Capture an entry timestamp explicitly, store edits as revisions, and render in chronological order with field-history events. Personal plan membership/reminder changes belong to private planning history, not the future shared task diary.

### History and undo

Every user mutation writes its affected aggregate before/after states, change label and inverse action in the same transaction. A task snapshot includes its subtasks, tag links, notes and related changed records. Keep an initial creation snapshot and tombstones so “as of” can return a correct state, including before deletion. Expose a read-only history viewer with date/version selection; restoring a historical value is a new recorded mutation.

Keep the latest 100 reversible user operations per profile (plus full task history); no undo entries for typing previews, queries or derived notification attempts. Undo uses expected post-operation versions across every affected entity. On conflict do not apply a partial inverse. Ctrl+Z in a focused text editor first uses text undo; app-level undo is available outside an active edit or through the explicit action/toast. Redo is optional and must not delay Phase 1.

## 10. My Day, My Week and built-in views

### Built-in query definitions

Use one shared application query specification for counts and rows so sidebar counts match the view. Exclude deleted/archived-container tasks by default. Top-level tasks are rows; a matching subtask/diary result links to its parent detail.

| View | Predicate/order |
| --- | --- |
| My Day | Active DayPlanItems for profile's today, manual position; completed items in a collapsible section |
| Today | Open tasks planned today or due today; show distinct planned/due badges and deduplicate task IDs |
| Upcoming | Open tasks with planned/due dates after today within a user-selectable range, default 14 days, grouped by earliest relevant local date; include labelled recurrence projections |
| Overdue | Open tasks whose due date/time has passed, plus active recurring tasks with no due anchor whose schedule anchor has passed; ordinary planned-in-past alone is not overdue |
| Assigned to me | All active top-level Personal tasks in Phase 1, with a short explanation; no fake assignees |
| Recently completed | Done tasks completed in last 30 local days, newest completion first; date range can expand |
| Waiting | Open Waiting-on-someone tasks grouped by outside-person text, next follow-up badge |
| Inbox/list/project/tag | Active contents/filter, persisted manual order; tag groups include descendant tags |

Search supports words, tags, status, priority, container, planned/due/completed ranges and waiting-person text. Temporary filters compose with any built-in list; no saved custom-view builder. Parameterize SQL, escape/parse FTS input so punctuation does not crash, limit results to 100 with cursor paging and deterministic tie breaking. Search includes pad/diary text and can include completed/archive by explicit filters.

### Day initialization and suggestions

`EnsureDayPlan(profile, localDate)` creates the day once; a unique key protects midnight/restart races. In the same transaction seed eligible tasks from the confirmed weekly plan, recording that revision. Do not reinsert a task the user removed from that day's plan when opening the view again. If a later week confirmation changes today's assignments, present an explicit add/remove comparison and preserve manual choices.

Suggestions are a deterministic distinct list: overdue, planned today, due today/within next two days, yesterday's unfinished selected work, recurrence due today, follow-ups due. Exclude items already selected and terminal/deleted/archived tasks. Show a reason and sort by reason urgency, due date, priority then ID. Add/remove/order is profile-owned. Adding to My Day never edits planned/due fields. Tomorrow carry-over creates tomorrow's item once; it does not remove today's historical membership.

At midnight, on resume and on zone change, recalculate the local date, initialize the new day and refresh displayed queries. Never delete yesterday's plan. A selected terminal task stays in that day's completed/cancelled section and is excluded from remaining estimates.

### Weekly ritual and capacity

Persist a Draft separately from the active Confirmed plan, either as draft payload/revision or child draft rows; editing a draft cannot mutate the confirmed assignment set. Implement these five screens/steps:

1. **Look back:** last week's structured summary, achieved goals, unfinished assignments and cancelled/let-go counts.
2. **Decide leftovers:** select carry to current week, schedule later, remove from personal plan, or explicitly cancel task. “Drop from week” must not delete or cancel the task.
3. **Set goals:** suggest two or three text goals, optional project link and explicit task links. Allow fewer/more with a gentle hint rather than a hard minimum. Goal progress is Done linked tasks / noncancelled linked tasks; an unlinked goal is manually assessed.
4. **Plan days:** seven local date columns plus an unassigned pool, dragging or keyboard moves, estimates, total available minutes and optional work allowance. A task has at most one day in the week; missing estimates show an unknown count and never imply zero effort.
5. **Confirm:** atomically replace active assignments/goals with the reviewed draft revision, retain previous plan history, and seed today's day plan through the reconciliation flow above. Cancel returns to the prior confirmed plan without partial changes.

Capacity total = remaining estimates of assigned open tasks; work capacity additionally counts tasks labelled Work. Work is a personal capacity category, not a space or employer-owned data. Warn when total > daily total or work > work allowance, including zero availability; never auto-reschedule. Default availability is configurable, initially 8 hours weekdays and 0 weekends with an obvious first-use editor. No meeting subtraction in Phase 1. Goal/task estimates are counted once even when linked to multiple goals.

Planning-day/time and midweek prompts are local NotificationJobs. A deterministic midweek check-in lists overdue/slipped assignments and overloaded remaining days; changes require user action. Weekly plan dates never rewrite due dates.

Changing first weekday applies to newly created plans; historical plans retain their original week-start date. If a confirmed current/future plan would overlap a newly configured week boundary, show a migration preview and require an explicit remap of assignments before confirming the new plan. Never produce overlapping active week assignments by silently changing keys.

## 11. Reminders and outside-person follow-up

### Important NeoAstra integration fact

NeoAstra uses native platform services and reports capabilities. Its current Windows notification presenter returns `Unsupported` for future `DeliverAt` and requests with actions. It cannot satisfy this product's scheduled actionable reminders by itself. Use a dedicated Windows notification adapter behind the application port; keep NeoAstra as the UI framework. Verify this boundary in work package 0 rather than discovering it at release. See [NeoAstra desktop service support](https://github.com/NeoAstra/NeoAstra/blob/4242271d69d540609215978058400fbac0fc4ceb/doc/desktop-services.md).

### Windows implementation

Use a packaged Windows desktop identity (MSIX) and the supported Windows app-notification activation path. Build the small adapter in `Chaos.Desktop.Windows`; it may use Windows App SDK notification builders/activation and `Windows.UI.Notifications.ScheduledToastNotification` for OS scheduling. Pin the chosen Windows App SDK release after proving interoperability with NeoAstra/.NET 11 in work package 0. This is an OS integration dependency, not a second UI framework.

Microsoft documents scheduling with `AddToSchedule`, cancellation with `RemoveFromSchedule`, and a limited delivery window while the machine is off. Persisted application reconciliation is still necessary. Follow the official [scheduled app notifications guide](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-scheduled), its linked activation quickstart and sample; verify an actual installed package before committing to the adapter APIs.

The feasibility spike must demonstrate: installed launch; reminder scheduled while online/offline; app exited before delivery; Open/Done/Snooze buttons launching/routing to exactly one app instance; cancellation after changing a date; and permission-denied behavior. If this fails, treat Windows actionable scheduling as a release blocker, not an excuse to claim NeoAstra balloons provide it. Ordinary `dotnet run` may use an in-app presenter clearly identified as development behavior.

### Scheduling rules

- Rule forms: absolute UTC instant, offset from planned date/time, offset from due date/time. Date-only anchors use a configurable default reminder time, initially 09:00 local. If an anchor is removed, suspend that rule and show why; do not use midnight UTC.
- Mutation transaction writes updated rules and desired NotificationJobs. A background reconciler performs OS calls after commit and records success/failure. Generation IDs invalidate stale OS callbacks. On startup/resume reconcile desired jobs with persisted/OS schedules; OS calls are not part of the SQLite transaction.
- Actions: Open task; mark Done using the standard completion command; Snooze 10 minutes/1 hour/tomorrow at default time, or open a picker for a chosen time. Snoozing changes the delivery, not the due date. Every action has an idempotency key and validates current task/generation state before executing.
- Completing/cancelling/deleting a task cancels its pending deliveries. Undo restores appropriate future jobs and removes obsolete ones. Repeating tasks clone rules into the new occurrence, not acknowledgement/dismissal state.
- Quiet hours can cross midnight. Move ordinary deliveries to the next allowed instant, preserving the original target. No sound or balloon bypass when quiet. Insistent reminders repeat at a user-selected minimum of five minutes until acknowledged/snoozed/completed; bound schedules to a rolling horizon rather than unbounded pre-registration.
- Keep a local notification center as durable history of due reminders, including missed/denied notifications. On resume, coalesce missed repeats per rule into one latest reminder, preserve original due time and never flood the user with every missed repeat. OS focus/permission rules can suppress delivery; show capability status without blocking capture.
- Close window normally leaves a tray process when background reminders are enabled. Provide an explicit Quit. Scheduled Windows reminders should still fire after Quit where OS support allows; inactivity-derived rules and summaries are reconciled when the app resumes. Do not promise delivery while the computer is powered off.
- Summary/planning/scratch-review prompts use the same persisted scheduling infrastructure and quiet-hour handling. Do not send task content to any server.

### Waiting outside the app

Setting Waiting on a person stores a free-text name and waiting-start time; nothing is sent. Support a fixed follow-up date/time, N units after waiting started, N units before due, or N days without task activity. Qualifying activity is a task field edit, subtask status/title edit or new/edited diary update; opening a view, a reminder firing and My Day reorder do not reset it.

Recompute inactivity deadlines after relevant committed events. “Follow up” appears as a separate My Day item referencing the original task. Phase 1 actions: Open task, Log follow-up, Snooze and Resolve/clear waiting. A log writes a private diary update and resets an inactivity follow-up. There is no Send nudge or contact lookup. Clearing Waiting/completing the task resolves the active follow-up; historical records remain.

## 12. Plain summaries and journal

Build structured facts in C#, then render list/count-based summaries. No AI dependency, API key or generated narrative. Include completed tasks grouped by container, project progress change, planned-but-unfinished tasks, waiting/follow-ups, tomorrow's deadlines, diary updates and suggested tomorrow tasks. Omit nonexistent agents/time tracking rather than showing empty pseudo-features.

Use day/week membership and append-only completion/history events for the period, not only today's current task status. A task completed then reopened remains a recorded completion event with a reopened annotation; counts distinguish current completed state and completion events. Store task title/container labels as captured facts so a future rename/deletion does not rewrite a past journal entry.

Generate a summary at the configured local evening/planning time while running, or lazily once on next startup for a missed period. Unique period keys and an explicit captured-through instant make generation idempotent. If the current day's summary is generated early, label it “through HH:mm” and allow explicit regeneration; keep prior revisions in history. Weekly summaries cover seven configured local dates and feed the next review.

Morning summaries show that day's selected/weekly-planned and due tasks. Quiet-hour scheduling follows the common reminder rules. Every summary offers previewed carry-over of unfinished tasks, which only creates the chosen next-day/week personal records and is deduplicated. No automatic external posting or email.

## 13. NeoAstra host, RPC and frontend

### Host and generated contracts

Start from the actual React template. Its host uses `[STAThread]` and `NeoApp.Run(args, configure)`. Register application services through the template's `UseRpc` pattern; keep the host composition readable. Define `[NeoRpcService]`/`[NeoRpcMethod]` wrappers and an explicit source-generated JSON context covering every request, response and event root. NeoAstra's generator supplies typed bindings; missing metadata must fail the build rather than fall back to reflection. See [getting started](https://github.com/NeoAstra/NeoAstra/blob/4242271d69d540609215978058400fbac0fc4ceb/doc/getting-started.md) and [RPC/bindings](https://github.com/NeoAstra/NeoAstra/blob/4242271d69d540609215978058400fbac0fc4ceb/doc/rpc-and-bindings.md).

The template imports generated APIs through `#neoastra` and stages `@neoastra/client` under `obj/neoastra/client`. Preserve this version alignment and build order. A C# contract build precedes frontend typecheck/build; CI must reproduce a clean build without checked-in generated files. Production serves local bundled assets, no CDN or localhost server. Development alone uses the exact loopback Vite origin. Follow [NeoAstra frontend tooling](https://github.com/NeoAstra/NeoAstra/blob/4242271d69d540609215978058400fbac0fc4ceb/doc/frontend-tooling-and-assets.md).

Expose feature-sized contracts, not one generic SQL/command tunnel:

| RPC group | Minimum operations |
| --- | --- |
| Bootstrap/settings | Get profile/navigation/capabilities, update settings, data-status |
| Capture/tasks | Preview quick add, create batch, get detail, edit fields, status, move/reorder, delete/restore, template CRUD/instantiate |
| Tags | Suggest/list tree, create/rename/move/merge/delete, set links |
| Notes/pad | Save note, diary CRUD, pad CRUD/archive, convert checklist/selection, internal-link lookup |
| Views/search | Query built-in rows/counts, paged search and temporary filters |
| Planning | Get/init day, add/remove/order, get/draft/confirm week, carry-over, availability/goals |
| Recurrence | Preview rule, change one/future, skip/end, history |
| Reminders/waiting | Rule CRUD, snooze/acknowledge, waiting/follow-up, notification center |
| Journal/history | Summary list/detail/generate, task as-of/history, undo |
| Data transfer | Choose file/directory, preview/import, export, backup list/create/restore |

DTOs use strings for IDs/dates/instants, explicit enum representations, bounded arrays and nullable fields; never expose connections, paths, delegates or domain entities directly. Cap page size and input sizes in C#. File transfer uses user-selected opaque handles and native dialogs; no arbitrary renderer-supplied filesystem paths.

Use post-commit change events with IDs and versions. On subscription loss/reconnect, reload authoritative queries. Coalesce updates; unsubscribe on component disposal. Optimistic UI is limited to reversible presentation with rollback; dates, recurrence, bulk operations and destructive changes show success only after commit.

### Security boundary

Register only intended application RPC. NeoAstra's ordinary permissionless methods are trusted-local operations; a capability manifest alone does not restrict them. For native clipboard/opener/notification/file services use explicit permissioned adapters and bounded scope; do not expose process execution or arbitrary files. The packaged local app has restrictive CSP, controlled top-level navigation and no remote frames/scripts. Untrusted Markdown is passive sanitized content. Production rejects development origins and excludes debugging/MCP automation endpoints. See [NeoAstra capability semantics](https://github.com/NeoAstra/NeoAstra/blob/4242271d69d540609215978058400fbac0fc4ceb/doc/capabilities-and-security.md).

### Screens and interactions

Main window: sidebar (My Day, My Week, built-in views, Inbox, projects/lists, tags, Scratch pad, Journal, Settings), center content and optional task-detail pane. Quick add stays reachable in the toolbar. Default fresh launch opens My Day with a useful Add task affordance; no tutorial blocks input.

Task row: completion control, title, planned/due badges, priority/tag hints, estimate and subtask progress. Detail pane: title/status/container, planned and due fields visibly distinct, estimate/category, tags, Markdown, subtasks, repeat/reminders/waiting and diary/history. Avoid showing controls for deferred collaboration/AI features.

Shortcuts: Ctrl+N quick add, Ctrl+Shift+P Personal pad, Ctrl+F search, Ctrl+Enter submit in appropriate editors, Escape dismiss/back, standard tab navigation. Default global shortcuts Ctrl+Alt+N (capture) and Ctrl+Alt+P (pad), user-configurable; report registration conflicts and retain in-app alternatives. Route all launches to one local instance and recover focus to the intended field. Do not intercept editor shortcuts or IME composition.

Use semantic controls, visible focus, accessible labels, live status for save/error states and keyboard alternatives for every drag operation. Respect system light/dark preference and reduced motion; check contrast, high DPI at 100/150/200%, and narrow window layouts. Use `dir=auto` for user text/editors and logical CSS properties for mixed Arabic/Latin content. Test screen-reader navigation on Windows.

## 14. Backups, export, import and deletion

### Backups and restore

Use `SqliteConnection.BackupDatabase` on the storage worker to create a coherent snapshot while WAL is active; copying only `chaos.db` is incorrect. Microsoft documents the [online backup API and its write blocking](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup). Write a temporary destination, run `PRAGMA integrity_check`/foreign-key checks, compute checksum, then atomically rename and register it.

Create backups before migrations/imports/restores and at least daily after changes; keep seven daily and four weekly validated snapshots. Rotate only after a new backup succeeds. Show latest successful backup and disk errors in Settings. Backups are local device-protected files, not encryption or cloud sync; do not claim application-level database encryption.

Restore flow: select backup, validate checksum/schema/integrity, preview timestamp and counts, confirm replacement, make a pre-restore backup, quiesce writes/reminders, close every connection, replace database and handle obsolete WAL/SHM only while closed, reopen/validate and reconcile jobs. Do not overwrite the only valid copy. Test interrupted restore using staged files/recovery markers. Restart into a clear recoverable state after failure.

### Export

Offer:

1. **Versioned JSON archive**: lossless local data including IDs, tasks/subtasks, templates, tags, notes, history, plans/goals, settings, reminders and recurrence. Include schema version, manifest/checksums and import policy; exclude machine paths/cache/OS notification IDs. Do not include a live WAL copy as the portable format.
2. **Markdown directory/ZIP**: human-readable tasks and pad notes with stable ID metadata, checklists, diary and companion metadata for dates/tags/recurrence; avoid unsafe filenames/path traversal and collisions. Attachments directory is unnecessary until attachments exist.
3. **Org file**: headings/TODO states, nested subtasks, SCHEDULED/DEADLINE, tags, properties for stable IDs and supported recurrence, LOGBOOK updates. Unsupported rich Markdown retains the original source in an escaped block/metadata, rather than pretending full lossless markup conversion.

Publish the supported-field mapping and limitations. Full-fidelity round-trip guarantee is for the versioned JSON format; plain-text exports prioritize readability and preservation of original notes.

### Import

The spec leaves “common to-do apps” unspecified. Initial deterministic importers are Chaos JSON, the documented exported Org subset, and a mapped CSV (`title`, `notes`, `list`, `status`, `planned`, `due`, `priority`, `tags`, optional `parent_id`). Accept generic provider CSV through a column-mapping preview; do not claim proprietary Todoist/Microsoft To Do compatibility without provider fixtures.

Import stages and validates everything before writing, shows counts/warnings/duplicates, lets the user choose target containers, and commits atomically after confirmation. New external data receives new IDs with a mapping; JSON supports explicit restore/merge modes and stable-ID conflict preview. Reimport with the same import fingerprint must not duplicate tasks. No automatic execution of embedded HTML, file paths, links or scripts; cap archive expansion/entry counts and reject traversal. Back up first and test rollback on the last invalid row.

Permanent task purge and “Delete all local data” are explicit Settings actions, separate from reversible delete. Reset stops background services and cancels OS jobs, then confirms whether local backups/export files should also be removed. Never delete the user's separately chosen export directory. Document the remaining device-managed browser/cache and OS notification-history behavior accurately.

## 15. Ordered implementation work packages

For each package: inspect the relevant spec section, implement through Roslynk for C#, run diagnostics + Slopwatch + targeted tests, then update this checklist with actual evidence. Do not create a UI-only mock and mark a persistence feature complete.

### [ ] 0 — Prove the stack and desktop integrations

**Depends on:** nothing. **Files:** SDK/package/tool manifests, first host/test project, dependency/support docs, disposable spike code isolated from product code.

Progress note (2026-11-09): steps 1–4 demonstrated; step 5 partially (single-instance lock proven headlessly, interactive items pending a real desktop session); step 6 recorded. Header stays unchecked until the installed-MSIX notification spike runs on real Windows.

1. Scaffold the solution/React host, pin the versions above, prove Roslynk can load/edit a C# 15 symbol and build the generated RPC.
   — Done, with one user-directed deviation: **Roslynk is skipped** ("Skip using roslynk"); C# edits use the session's file tools (see docs/decisions.md). Solution scaffolded, versions pinned, generated RPC + frontend build green (0 warnings).
2. Add a minimal TUnit executable and assert one actual C# service result; prove `dotnet test` discovers and runs a nonzero count with Microsoft.Testing.Platform.
   — Done: 6 Domain + 1 Infrastructure + 2 Desktop tests green under MTP; solution-level `dotnet test` verified (extension downloaded once), direct-executable fallback encoded in `eng/verify.ps1`.
3. Prove SQLite persists Unicode through restart and FTS5 is available with bundled native binaries.
   — Done: `SqliteStackSpikeTests` stores Arabic+emoji text, WAL, FTS5 MATCH after close/reopen.
4. Run the actual NeoAstra window offline from a publish folder; check local React assets and typed RPC round-trip.
   — Partial: self-contained win-x64 publish contains bundled assets (`assets/index.html`, hashed bundles, `neoastra-assets.json`) and the exe starts and stays alive offline from the publish folder. Visual window + typed RPC round-trip through the running UI remain for interactive acceptance (docs/desktop-support.md).
5. Prove global shortcuts, tray/single-instance activation, and the installed Windows scheduled actionable notification spike described above. Establish MSIX identity/signing requirements and pin Windows dependency versions.
   — Partial: `SingleInstanceGuard` implemented and unit-tested (first/secondary/released instances). Global-shortcut registration, tray activation and the installed-MSIX notification spike require a real interactive Windows session — smallest next action: run the spike checklist in docs/desktop-support.md on the user's desktop.
6. Record exact Node/npm/frontend versions, installed SDK, NeoAstra APIs, Windows integration support and package-size/startup baseline in `docs/dependency-versions.md`/`desktop-support.md`.
   — Done for everything measurable headlessly; package-size/startup baseline numbers to be appended with the interactive acceptance run.

**Exit:** a real native window, generated typed call, persistent SQL row, discovered TUnit test, ~~Roslynk semantic edit~~ *(waived by user direction)*, and actionable scheduled notification all demonstrated. Resolve genuine blockers here; do not proceed with an assumed API.

**Exit status:** typed call ✓, persistent SQL row ✓, discovered TUnit tests ✓, native window start ✓ (visual confirmation pending), actionable scheduled notification ✗ pending interactive spike. Remaining step is sandbox-blocked, not API-blocked; later packages proceed and the spike is re-run when a desktop session is available.

### [x] 1 — Storage foundation and first durable task

**Depends on:** 0. **Files:** Domain Tasks/Time, Infrastructure Sqlite/Migrations, Application Tasks, Desktop composition/RPC, first frontend task form.

Progress note (2026-11-09): implemented and verified headlessly (Roslynk skipped per user direction; file tools used). The only open item is visual click-through of the task form inside a real window, folded into the interactive acceptance spike already tracked under package 0.

Implement migration runner, transaction worker, operation receipts, IDs/clock, profile/Personal/Inbox seed, error mapping and single-task create/get. Include the minimal validated SQLite backup primitive needed before every subsequent migration; package 12 adds scheduled retention, restore UI and transfer. Use a real UI input → typed RPC → transaction → row → restart vertical slice. Include tombstone/version/history scaffolding where future mutations need it; add later schema migrations as features arrive.

— Done: migration runner (content-hash tracking, newer-version and tamper refusal), `SqliteApplicationStore` serialized write transaction with rollback, `OperationReceipts` (same-id same-payload replay / different-payload conflict), deterministic IDs/clock, `StorageBootstrap` (data-root lock, pre-upgrade backup, migrate, idempotent profile/Personal/Inbox seed), `SqliteErrorMapper` (stable kinds, InnerException preserved for logs only), `VACUUM INTO` backup with integrity check + catalog entry in one step; `TaskRpcService` create/get wired in `Program.cs`; first frontend task form in `frontend/src/main.tsx` calling generated `tasks.create` binding. `frontend/dist` is no longer tracked (build output, gitignored).

**Tests:** fresh seed idempotence, upgrade checksum/version refusal, Unicode, duplicate OperationId same/different payload, transaction rollback and concurrent version conflict. **Exit:** capture a task while disconnected, restart and retrieve it; no mock/in-memory persistence.

— Done: 47 TUnit tests green headlessly (Domain 6, Application 11, Infrastructure 21, Desktop 9), including Unicode round-trip, seed idempotence across restarts, checksum tamper + newer-schema refusal, replay/conflict, rollback of a failed write, optimistic-concurrency stale-version null, busy/full/constraint error mapping, backup create/validate/catalog. The restart slice (`TaskRpcRestartTests`) creates through the real RPC on SQLite, disposes the storage session, reopens the same data root and reads the task back — no mocks. Frontend: `npm test` 1/1, `tsc --noEmit` + `vite build` green against the generated bindings; Slopwatch clean. Interactive window click-through of the form remains part of the real-desktop acceptance run.

### [ ] 2 — Task lifecycle, containers, subtasks and templates

**Depends on:** 1. Implement full task editor, statuses/completion, planned/due/estimate/category, lists/projects, sections/order/move/archive, subtask CRUD/progress, deletion/restore and template CRUD/instantiate. Add terminal timestamps and initial history snapshots.

Progress note (2026-11-10): lifecycle, container, subtask and template service/storage work is in progress, not yet connected to the full desktop workflow. Repaired the test doubles and schema-version assertions, preserved replay of package 1 capture receipts, and added a file-backed project completion/delete/restore/restart test. Release build and 49 TUnit tests pass; Slopwatch reports zero issues. The package 2 behavior matrix, RPC/editor integration and interactive keyboard workflow remain to be completed and verified.

Further progress: the Inbox now reloads persisted tasks and exposes task detail editing, status changes, reversible deletion and up/down keyboard-accessible ordering through typed RPC. Fixed SQLite persistence of task container/position and preserved pre-change subtask history; added file-backed date, move/section, reopen and RPC-order tests. Release build, 53 TUnit tests, the existing frontend test and Slopwatch pass. Still outstanding: container/section, subtask and template RPC and UI, complete behavior matrix, project deadline editing, frontend interaction tests and real desktop keyboard acceptance. Package remains unchecked.

Further progress (2026-11-10): added typed container and subtask RPC plus UI for creating/selecting lists and projects, creating/assigning sections, moving tasks, archive/unarchive, and editing/status/reordering subtasks. Fixed an inverted archive condition and rejected invalid container deletion choices. A Windows test window with an isolated data root verified keyboard capture and Inbox reorder; it did not verify the full project workflow. Release build, 55 TUnit tests, the existing frontend test and Slopwatch pass. Remaining: template RPC/UI; container and section rename/reorder/delete/restore UI and RPC; subtask delete/restore UI; project deadline/progress UI; full behavior matrix, frontend interaction tests, and project workflow keyboard/restart acceptance. Package remains unchecked.

Further progress: template create/edit/instantiate/delete and immediate restore are wired through RPC and UI, retaining relative dates and subtask structure. Added project deadline/progress, container rename/order and section rename/order UI, Inbox rename/order protection, and file-backed template/project tests. In a real Windows window using an isolated root, keyboard-created project and completed task survived restart and the task was reopened by keyboard. Release build, 58 TUnit tests, the existing frontend test and Slopwatch pass. Still outstanding: durable discovery/restoration of deleted templates, container and section delete/restore flows, subtask delete/restore UI, remaining behavior matrix and frontend interaction coverage; project keyboard acceptance did not cover sections or reorder across restart. Package remains unchecked.

Further progress: deleted tasks, subtasks and templates are discoverable after restart and can be restored through the UI. Task restoration now identifies children from the deletion snapshot instead of matching timestamps, so a separately deleted child is not accidentally restored. File-backed restart tests cover these cases; native keyboard testing verified task deletion and restoration. Release build, 60 TUnit tests, the existing frontend test and Slopwatch pass. Still outstanding: container and section deletion/restoration with operation-specific provenance and conflict protection, remaining behavior matrix, frontend interaction coverage, and section/project reorder keyboard acceptance across restart. Package remains unchecked.

Further progress: new container and section deletions store operation-specific recovery data in the transactional receipt. Restoration checks affected versions and rejects conflicting later edits atomically; both container contents choices and restart recovery have file-backed tests. The native Windows keyboard check deleted/restored a section and its list. Release build, 64 TUnit tests, the existing frontend test and Slopwatch pass. Deletions made before recovery receipts were introduced cannot be reconstructed safely and return guidance to restore a backup. Remaining: behavior matrix, meaningful frontend interaction tests, and section/project reorder keyboard acceptance across restart. Package remains unchecked.

Further progress: tests now cover the six statuses and terminal timestamps, explicit subtask choices and invalid choices, rank compaction, foreign-space moves, and all-day/timed date validation. A DOM interaction test verifies deletion choice and conflict feedback. Release build, 75 TUnit tests, two frontend tests and Slopwatch pass. Section/project reorder keyboard acceptance across restart remains unverified: the Windows automation provider twice failed to activate the visible test window. The existing development dependency graph has three high-severity npm audit findings; production dependencies have none, as recorded in `docs/dependency-versions.md`. Package remains unchecked pending the keyboard acceptance and remaining release-grade validation.

Further progress: fixed stale list responses after navigation and grouped tasks by section, with task reorder scoped to its section. React interaction and file-backed SQLite tests cover those behaviors. A native Windows keyboard test created and reordered project sections, restarted, and confirmed the order persisted. Release build, 76 TUnit tests, four frontend tests and Slopwatch pass. Package 2 remains unchecked: the plan also calls for real drag/drop, and recovery of container/section deletions made before recovery receipts still requires a backup. The recorded development dependency audit findings remain open.

Further progress: `App` was extracted from `main.tsx` and drag/drop reordering was factored into a shared `useDragReorder` helper with the same insertion-index semantics as the keyboard buttons. Tasks (scoped to their section), subtasks, sections and lists/projects (in a new list navigation) are now draggable, each keeping its keyboard Move up/down alternative; drops onto a different sibling set are refused. Container reorder now keeps the Inbox first even when a client requests index 0. Release build, 77 TUnit tests, nine frontend tests (including jsdom drag tests for tasks, sections and lists plus the insertion-index rule) and Slopwatch pass. Package remains unchecked until drag/drop is exercised with a real pointer in the native Windows window (smallest next action: drag a task, section and project in an isolated-root window, restart, confirm order). Pre-receipt container/section deletions still require a backup to recover.

**Tests:** planned vs due separation, all-day/timed boundaries, six statuses, completion/subtask choice, project denominator, rank compaction, foreign-space/section rejection, Inbox protection, template reset and container deletion choices. **Exit:** capture/organize/complete/reopen a project workflow survives restart, including keyboard reorder.

### [ ] 3 — Tags and built-in querying/search

**Depends on:** 2. Implement tag tree/sidebar, suggestions, links, rename/merge/delete and descendant filters. Add FTS maintenance/rebuild, built-in query/count service, paging and temporary filters. Tag pad links become available with package 5.

Progress note (2026-11-10): implemented end to end, but not yet accepted in a native window. Migration 003 does three things. It scopes sibling tag uniqueness to the owner (root tags included). It adds triggers that stop a tag from being its own parent. It creates the FTS5 index: `SearchDocuments` plus `SearchIndex`, with a `SearchIndexState` normalization version. Startup checks that FTS5 is available and rebuilds the index whenever that version is stale, which includes the first start after the upgrade.

Task and subtask search documents are written in the same transaction as their source rows, and tombstoned sources lose their document. Indexed text and search input are normalized the same way: NFKD, combining marks removed, tatweel dropped, Arabic-Indic and Persian digits folded, then NFKC. User text becomes quoted prefix terms joined with AND, so quotes, operators and punctuation can never break a query.

`TagCommands` covers:
- create a nested path, reusing existing segments
- rename, color (fixed palette) and move, with cycle and depth checks
- merge with a preview: links deduplicated, same-name children merged recursively, done atomically
- delete the whole subtree, removing its links but keeping the tasks, with recovery data stored in the receipt
- restore, refused if a tag changed after deletion or its name or parent is now in conflict
- tag or untag a task or subtask, by ID or by path
- suggestions, so errand and errands are offered instead of duplicated

`TaskQueries` builds one specification that produces both the rows and the count for every view. It covers Container, Tag (descendant tags and tagged subtasks included), Today, Upcoming (grouped by the earliest relevant date), Overdue (a timed due is judged in its own zone), Assigned to me, Recently completed, Waiting and Search. Temporary filters compose with any view: status, priority, planned/due/completed ranges, completed, and archived lists. Pages use keyset cursors with the task ID as the last tie-breaker, at most 100 rows, and a cursor from another view is rejected.

The desktop UI adds sidebar search, the built-in views with counts, a nested tag tree with counts that also accepts dropped tasks (drag-to-tag), and a tag view whose settings rename, recolor, move, merge (preview first) and delete. The task and subtask editors get a tag picker with suggestions, and deleted tags can be restored. A rebuild-index RPC also exists.

Results: Release build has 0 warnings; 118 TUnit tests pass (Domain 6, Application 19, Infrastructure 80, Desktop 13), 41 more than before. Frontend: `tsrs` type check and `vite build` pass, and 14 jsdom tests pass. Slopwatch is clean. The Release exe starts offline on a fresh isolated data folder.

The paging test caught a real bug, now fixed: Upcoming gave planned-only tasks a NULL group date, which broke their cursor.

Not done, deliberately, and mapped to later packages:
- Waiting grouped by outside person, and searching waiting-person text: package 10 (no WaitingDetails table yet).
- My Day: package 8.
- Pad-item tagging and pad search results: package 5. Pad and diary text is already included by the rebuild.
- Keeping diary documents current on each write: package 4, along with tag history and undo.
- Recurrence projections in Upcoming and Overdue: package 6.

Package remains unchecked until the exit is shown in a native window: search typed notes, filter by a tag group, reopen the result, and drag a task onto a tag with a real pointer.

**Tests:** normalization and sibling uniqueness, cycles, recursive merge collisions, deduplication, deleted-target exclusion, FTS quotes/punctuation/Arabic, count-row parity and cursor stability. **Exit:** search typed notes and task metadata offline, filter by tag group and reopen the result.

### [ ] 4 — Markdown, diary, full history and safe undo

**Depends on:** 2–3. Build shared Markdown source/preview/toolbar, paste conversion, safe links, checklist conversion, task diary/history-as-of and operation-level undo. Wire history/inverses into every existing mutation, including tags/containers.

Progress note (2026-11-10): implemented end to end, but not yet accepted in a native window. Migration 004 adds `DiaryRevisions`, `ChecklistConversions` and an index for open undo entries (`DiaryEntries`, `TaskHistory` and `UndoEntries` already existed).

History and undo come from one mechanism instead of per-command code. Every command already runs through `TaskMutationSupport.InWriteTransactionAsync`, which now hands the body a `RecordingDataSession`. That session wraps the task, subtask, container, section, template, tag and diary stores:
- each row write records the row's before-image (null when the operation created it) and the version it was left at
- each effective tag-link change records its direction
- each task the operation reaches, directly or through a subtask, a tag link, a tag row or a diary entry, has its aggregate captured before the first write

When the body returns, still inside the same transaction, the recorder writes one history entry per changed task and one undo entry for the operation, then prunes undo to the newest 100 per profile. Snapshots now include the task's tag links and each subtask's tag links. A command's explicit history call only declares the event kind and fields, and the recorder supplies the before/after aggregates. So tag, container, section, template and diary mutations all gained history and undo without command-specific inverse code. Replays write neither history nor undo.

`UndoCommands` first checks that every affected row is still at the version the operation left it. Only then does it reverse the link changes and restore each before-image as a new version, tombstoning what the operation created. Any later edit anywhere in the set refuses the whole undo with the conflicting item's name, and nothing is written. After an undo, older open entries that expected a restored row's before-version are re-pointed to the new version, so undoing several changes newest-first stays exact. The undo itself is recorded history (`undo`) but is not undoable (redo stays optional).

`TaskHistoryCommands` lists the append-only feed and returns the task as of an instant or entry: "did not exist" before creation, deleted after deletion. Restoring chosen fields goes through the ordinary editor command, so it is a new recorded, undoable edit. Task restore now finds the subtasks to bring back from the entry that produced the tombstoned version, which also covers container deletion with contents and an undone restore.

Other additions:
- **Diary.** `DiaryCommands` adds, edits, deletes and restores dated entries, keeps every saved text as a revision, keeps diary search documents current on each write, and offers a timeline merged with field events.
- **Checklist conversion.** `ChecklistCommands` turns chosen checklist items into subtasks in document order (checked → Done). It leaves the notes untouched and keeps a per-item marker, so a second conversion creates nothing while the subtask exists. One conversion is one undo.
- **Desktop RPC.** New services `history`, `diary`, `undo` and `markdown`. The `markdown` service converts checklists, resolves internal links by ID, and runs `openExternal`, which accepts only absolute http/https/mailto URLs and opens them through the system shell.
- **Frontend.** One `MarkdownEditor` (source textarea, selection-aware toolbar, Ctrl+B/I/K, rich-HTML paste via Turndown + GFM, `[[` task-link autocomplete inserting `[label](chaos:task/<id>)`, Write/Preview switch) replaces the task and subtask notes textareas and is used for diary entries. `MarkdownPreview` turns the remark/remark-gfm tree into React elements only, and fenced code is highlighted with locally bundled lowlight:
  - raw HTML shows as text
  - images show a placeholder
  - only http/https/mailto links are clickable, through the host opener
  - internal links show the current title, or a deleted/missing state
- **Editor panel.** It gains checklist conversion with per-item choice and a Diary & history section: timeline, add/edit/delete/restore entries, a read-only version viewer opened from any event or a chosen date and time, and "Restore selected" values.
- **Undo in the app.** A toast offers the newest change, and Ctrl+Z outside text fields undoes it. Inside a text field Ctrl+Z stays with the field.

Results: Release build has 0 warnings. 135 TUnit tests pass (Domain 6, Application 22, Infrastructure 88, Desktop 19), 17 more than before. Frontend: the `tsrs` type check and `vite build` pass, and 23 jsdom tests pass (9 new). Slopwatch is clean, and the Release exe starts offline on a fresh isolated data folder.

Two real bugs were caught by the new tests and fixed. Undoing two changes newest-first was refused, because the first undo's new version no longer matched the older entry; older entries are now re-pointed. And the undo label for list deletion named a moved task instead of the list.

Not done, deliberately:
- Replacing converted checklist lines with internal links. Links target tasks and pad items; subtask links need a target kind of their own.
- `[[` links to pad items: package 5.
- Pad-item history: package 5.
- Private planning history (My Day/My Week/reminders): packages 8–10.
- The `ContentLinks` table. Links live in the Markdown and are resolved by ID on render.

Package remains unchecked until the exit is shown in a native window: type and preview notes with a table, a checklist and a link, add a diary entry, delete a task and recover it with Ctrl+Z, then edit a task after an older change and see that change's undo refused.

**Tests:** GFM fixtures, script/link/image blocking, checklist conversion twice, as-of before create/edit/delete, atomic undo and conflict detection across aggregates. **Exit:** notes/diary render correctly offline, a mistaken deletion is recovered, and later edits are protected from stale undo.

### [ ] 5 — Personal scratch pad and reliable editor saving

**Depends on:** 4. Add untitled items/tags/archive, revision-aware autosave/flush, selected-text-to-task preview/backlink and weekly review affordance. Reuse the parser shell initially with explicit fields until package 7 completes grammar.

Progress note (2026-11-10): implemented end to end, but not yet accepted in a native window. Migration 005 adds `ScratchPadTags`, a unique task index on `ScratchTaskLinks`, and two context columns on it. `ScratchPadItems` and `ScratchTaskLinks` already existed. The schema ceiling is now 5.

`PadCommands` covers the Personal pad:
- **Notes.** Create untitled notes. Archive and unarchive several at once. Delete and restore. Reorder with the shared rank rules. Tag notes and filter by a tag group, with nested tags included. All of these are recorded operations: receipt, history and undo, through a new `RecordingPadStore` and pad tag links in the recorder.
- **Autosave.** Not an undo entry: typing keeps the editor's own text undo. Each save carries the editor's revision, and the store applies it only when it is newer than `DraftVersion`. A delayed, repeated or out-of-order save never replaces newer text. Undo restores a note's earlier state but never moves `DraftVersion` backwards.
- **Make task.** Creates the task and its backlink in one transaction and leaves the note untouched. The selection is checked in UTF-16 units: it must match the quote exactly and must not split a surrogate pair. If the note moved on since the editor's revision, the quote is found again or the command is refused. The link stores the quote, its offsets and 32 characters of context on each side.
- **Locating a backlink.** Returns Exact while the text is unchanged. Otherwise it returns Relocated, choosing the copy of the quote whose surroundings best match (nearest on a tie). If the quote is gone it returns Missing with the old quote, so it never points at unrelated text.
- **Weekly review.** Suggests notes untouched for 7 days. It is due when a week has passed since the last review, recorded as a `pad.review` receipt. Finishing it archives the chosen notes as one undoable change.
- **Search.** Pad text is indexed in the same transaction as each write. Archived notes stay searchable; deleted ones do not.
- **Tags.** Deleting, restoring and merging tags now carry pad links too. The merge preview counts them.

Shutdown: `SqliteApplicationStore.Dispose` now waits up to 10 s for a write in progress (an autosave) to commit before closing the writer. Later writes are refused as storage-unavailable instead of racing the close.

Desktop RPC: a new `pad` service with list/get/listDeleted/create/save/archive/unarchive/delete/restore/reorder/makeTask/locate/search/resolveLinks/reviewStatus/completeReview.

Frontend:
- **Autosave.** `padAutosave.ts` has `DraftSaver`, which saves 500 ms after typing stops or at once on flush, with one save in flight. Edits made meanwhile go out next with a newer revision. A failed save keeps the text, shows Failed, and is retried by the next edit or by Retry. `DraftRegistry` sits above the pad view, so a draft and a failed save survive view switches.
- **When saves happen.** Leaving the pad saves every note; a failure is reported and the text is kept. Hiding the window saves too. `beforeunload` saves and asks for confirmation only while text is unsaved.
- **Pad view.** `ScratchPad.tsx` reuses the shared Markdown editor and adds save status with Retry and Copy text. It also adds Make task on the selection (explicit title/notes/list/planned/due until package 7), task chips that open the task, a tag picker, archive/delete/restore, up/down and drag order, a tag filter, an archived view and the weekly review panel.
- **Elsewhere in the app.** The sidebar has a Personal pad entry, and Ctrl+Shift+P opens a new note from anywhere in the app. The task editor shows "Made from a pad note" with Open in pad, which selects the anchor or shows the old quote when it is gone. Search lists matching pad notes, `[[` offers notes as well as tasks, and the preview renders `chaos:pad/<id>` links with a deleted/missing state.

Results: Release build has 0 warnings. 149 TUnit tests pass (Domain 6, Application 22, Infrastructure 101, Desktop 20), 14 more than before. Frontend: the `tsrs` type check and `vite build` pass, and 33 tests pass (10 new). Slopwatch is clean, and the Release exe starts offline on a fresh data folder with migration 005 applied.

New tests caught three real problems, now fixed:
- The schema ceiling still said 4, so a migrated database was refused on restart as "newer".
- An empty note's undo label read "empty note".
- Relocation by nearest offset picked the wrong copy of a repeated phrase, which led to the context columns.

Not done, deliberately:
- A per-save revision trail for pad notes. Structural changes are in undo; typing is not.
- Waiting on the native window-close request. NeoAstra's renderer close handler needs capability authorization that the app does not enable yet (package 13). Closing relies on `beforeunload`, `visibilitychange` and the storage drain, so at most the last moment of typing before the title-bar close may be unsent; everything acknowledged is durable.
- Tags in the Make task form. The command accepts tag IDs, but the form does not offer them yet. Quick-add grammar: package 7.
- Global (system-wide) shortcut and clipboard capture: package 13.

Package remains unchecked until the exit is shown in a native window: type rapidly in a note, switch views, close and restart, and see the latest saved text; make a task from a selection, edit the note, and follow the backlink.

**Tests:** debounce/flush with fake time, overlapping saves, navigation with failed save, source revision mismatch, Unicode selections/backlink after edit, archive undo. **Exit:** rapidly type, switch views, close/restart and recover the latest acknowledged text.

### [x] 6 — Recurrence engine and occurrence persistence

**Depends on:** 2, 4. Implement typed rule validation/calculation first, then transactional series/occurrences/exceptions, advanced editor and preview, complete/skip/move/edit future/end/history. Integrate task templates/reminder-rule placeholders without delivery.

Results (2026-11-10): the user authorized proceeding without Roslynk. The bounded calculator covers fixed and completion-relative rules; migrations 006–007 persist series, revisions, occurrence identities, exceptions and reminder-rule placeholders. Transactional commands handle start, completion, skip, move, future revision, end, replay and successor-aware undo. The task editor provides rule editing, preview and history, and recurring deletion offers skip or end-and-delete. Upcoming shows read-only projections; Overdue labels missed recurrence schedules. Templates can create a series and carry relative reminder rules; notification delivery remains package 10. Task, subtask and tag edits are stored as occurrence exceptions.

Verification: `eng/verify.ps1` passed locked restore, Release build, 184 TUnit tests and Slopwatch; frontend build and 37 tests passed. File-backed tests prove late completion creates one successor and the original diary remains on its occurrence after restart, as well as retry, constraint, move, revision, count, reminder-copy and undo-conflict cases. In an isolated native Windows window, a daily rule was previewed and started and a diary entry was saved. The native status-menu completion step could not be finished because focus moved to another active application; the completion/restart exit was verified through real SQLite and RPC tests rather than visually. Installed-release acceptance remains in package 13.

**Tests:** the recurrence matrix in section 16, constrained single active occurrence, transaction retry, end conditions, exception identity, no pile-up and successor-aware undo. **Exit:** late completion never produces a backlog of copies, and past diaries stay attached to the correct occurrence.

### [ ] 7 — Six-language quick add and bulk capture

**Depends on:** 2–3, 5–6. Implement pure parser/token span mapping, English fixtures then the remaining language packs, metadata chips/overrides, batch indentation preview, parser reuse by pad conversion and global capture.

Progress note (2026-11-10): implemented end to end, but not yet accepted in a native window. No migration was needed.

- **Parser (Domain/Capture).** `QuickAddParser.Parse(text, context)` is pure: the context carries the captured instant, profile zone, date order, first weekday, default reminder time, known lists and tag paths, and excluded spans. Six table-driven `LanguagePack`s (English, French, Italian, Spanish, Indonesian, Arabic) are matched together over a folded comparison string whose characters map back to original UTF-16 ranges. It covers every grammar family in §8: relative, weekday (bare and next), absolute and numeric dates, due markers, 12/24-hour and word times, repeats (weekday sets, weekdays, intervals, every other, month day, after done), relative and absolute reminders, estimates, `#tag/nested`, `!priority` aliases, `/list` (quoted or a known name with spaces), `@Personal`, `+member` and waiting/follow-up. A time in a DST gap moves forward; an ambiguous one takes the earlier instant.
- **Results.** Tokens are Applied (fill a field) or Kept (stay in the title with a warning: conflicts, unknown or ambiguous lists, shared spaces, delegation, reminders without a date). Plain "May", abbreviated weekdays without a marker and dates inside `15/12` never become sigils. Metadata-only input asks for a title.
- **Batch.** `BatchCapture.Split` turns pasted lines into tasks with one level of subtasks, strips one bullet/number and checkbox (checked → Done), counts a tab as four spaces and rejects mixed indentation, a second level and more than 500 lines with line-specific messages.
- **Application.** `CaptureCommands.PreviewAsync` reads settings, lists and tags once and writes nothing. `CreateAsync` saves the confirmed drafts, never reparsing, in one transaction with one receipt and one undo entry: subtasks, tag paths (selected or created), reminder rules, a recurrence series and history. Any invalid draft rolls back the whole paste, with `drafts[i].field` error paths. Pad Make task now sends the parsed draft and uses the same creation code beside its backlink.
- **Desktop.** A new `capture` RPC service (`preview`, `create`); `pad.makeTask` accepts the draft.
- **Frontend.** `QuickAdd.tsx` replaces the plain capture form:
  - a 150 ms debounced preview with stale responses dropped
  - recognized parts as chips; clicking one keeps that text plain, and it can be turned back on
  - an editable field preview (title, list with candidate picker, planned/due date and time, priority, estimate, repeat, reminders, tags, waiting, follow-up)
  - the batch preview with subtasks and line errors
  - Enter saves one task, Ctrl+Enter saves a pasted list, and Ctrl+N focuses quick add from anywhere in the app
  - the OperationId is kept across a retry of the same drafts
  The pad's Make task form is the same component over the selection.

Results: Release build has 0 warnings. 304 TUnit tests pass (Domain 124, Application 22, Infrastructure 133, Desktop 25), 120 more than before. Frontend: the `tsrs` type check and `vite build` pass, and 43 tests pass (6 new). Slopwatch is clean.

Not done, deliberately:
- The system-wide capture shortcut and clipboard capture: package 13, with the other global shortcuts. Ctrl+N works inside the app.
- Dedicated waiting/follow-up records: package 10. Until then "waiting on" sets the Waiting status and the person and follow-up date go into the notes, as the preview says.
- In-place highlighting inside the text box. Recognized parts are shown as chips under it.
- Absolute "remind me at …" reminders are stored as rules; delivery is package 10.

Package remains unchecked until the exit is shown in a native window: type a task in each language offline, click a highlight to keep a word as text, paste an indented list, and see the tasks after a restart.

**Tests:** all spec examples and every grammar family/language, Arabic digits/RTL/UTF-16, ambiguity, excluded token, stale preview, midnight context and all-or-nothing batch failure. **Exit:** create real persisted tasks using each language offline and correct recognition without losing source text.

### [ ] 8 — My Day and date rollover

**Depends on:** 2–3, 6–7. Implement DayPlan tables/init/manual membership/order, suggestions, completed section, carry-over, midnight/resume/zone refresh. Install week seeding later via its explicit integration point.

**Tests:** unique day init across races, no duplicate carry-over, manual removal persists, planned-in-past vs due-overdue, DST/local dates and no automatic suggestion insertion. **Exit:** yesterday remains reviewable and today's view refreshes without a restart.

Progress note (2026-11-10): implemented and verified headlessly; not yet accepted in a native window.

- **Storage.** Migration 008 adds `DayPlans` (unique profile + local date, nullable week-seed revision for package 9) and `DayPlanItems` (Task kind only until package 10 adds follow-ups; origin Manual/Week/CarryOver; one row per task per day, removal keeps the row with `RemovedAtUtc`).
- **Application.** `MyDayCommands` opens a day (today and later are created once with `ON CONFLICT DO NOTHING`; earlier dates are read-only), adds, removes, moves (shared `RankOrdering`) and carries open items to the next date once. Every change has an OperationId receipt. Membership never edits planned/due dates. `EnsurePlanAsync` is the integration point for week seeding. Suggestions are computed on request from the shared query specifications (Overdue, Today, due within two days, active recurrence on today, yesterday's open selected items), deduplicated by most urgent reason and ordered by reason, due, priority, ID; selected, terminal, deleted and archived tasks are excluded.
- **Queries.** `TaskView.MyDay` (with an optional `Day`) runs through the same query store as every other view, ordered by item position; the sidebar count uses the same specification (open items).
- **Desktop.** New `myDay` RPC service (`clock`, `open`, `suggestions`, `add`, `remove`, `move`, `carryOver`); `queries.run` accepts `day`.
- **Frontend.** `MyDay.tsx`: chosen tasks with move up/down and remove, a collapsible Finished section, remaining estimate with an unknown-estimate count, a suggestions panel with explicit Add, previous/next day navigation (earlier days read-only) and carry-over. `useDayClock` re-reads the local date just after the profile's next midnight, every minute and on focus/visibility (resume), so views and counts refresh without a restart. The task editor has an Add to My Day action. Sidebar entry with count.

Not done, deliberately: app-level undo of My Day membership (remove/re-add is the reversal; membership is profile-owned, not task history); drag-and-drop reordering inside My Day (keyboard buttons cover it); follow-up items (package 10); weekly seeding and reconciliation (package 9).

Results: Release build 0 warnings; 316 TUnit tests pass (Infrastructure 144 with 11 new My Day tests, Desktop 26 with 1 new); frontend type check, `vite build` and 48 tests (5 new) pass; Slopwatch clean. Package remains unchecked until the exit is shown in a native window: choose tasks, see the view switch to the new date after midnight without restarting, and review yesterday.

### [ ] 9 — My Week planning ritual

**Depends on:** 8. Implement weekly drafts vs confirmed plans, goals/task links, look-back/leftover decisions, seven-day drag/keyboard assignments, availability overrides/work category, overload warnings, confirm/reconcile today and midweek check-in query. Implement the reusable historical-facts query for look-back here; package 11 adds persisted journal snapshots and scheduled summary generation, replacing live look-back with those snapshots when available.

**Tests:** cancel draft has no effects, atomic confirm, week-start changes, same task in multiple goals counted once, unknown estimates, zero capacity, carried items and seed revision reconciliation. **Exit:** finish the five-step ritual and see the intended tasks in My Day while original due/planned dates remain correct.

Progress note (2026-11-10): implemented and verified headlessly; not yet accepted in a native window.

- **Storage.** Migration 009 adds:
  - `WeekPlans`: unique profile + week-start date. State is Draft, Confirmed or Superseded. The confirmed revision is kept, and the draft is a versioned JSON payload stored apart from the confirmed rows.
  - `WeekPlanRevisions`: a snapshot of every confirmed revision.
  - `WeekGoals` (outcome Pending/Achieved/LetGo) and `WeekGoalTasks`.
  - `WeekAssignments`: one place per task per week, either a day or the unassigned pool. Triggers keep the day inside its own seven dates.
  - `AvailabilityRules` (weekday defaults) and `WeekAvailabilityOverrides` (one-date overrides). Work time can never exceed total time.
  - The schema ceiling is now 9.
- **Domain.** `WeekCalendar` handles seven local dates from the configured first weekday. `Capacity` sums remaining open estimates and work time. Missing estimates are counted separately. It warns on over-total (including zero availability) and over-work, and never reschedules. The default is 8 h on weekdays and 0 at weekends.
- **Application.** `MyWeekCommands` covers the ritual:
  - **Open.** Returns the week with look-back, candidates, goal ideas, pending day comparisons and check-in.
  - **Drafts.** Start a draft (from the confirmed plan when there is one), save it (validated, version-checked) or discard it. Discarding leaves the confirmed week untouched.
  - **Confirm.** One transaction does everything: applies the leftover decisions, replaces goals, assignments and the week's overrides, records the revision and marks already-opened days that match. A task deleted in the meantime refuses the whole confirmation.
  - **Leftover decisions.** Carry, later (an optional explicit planned date, the only path that edits a task), remove (task untouched) or cancel (with history).
  - **Other commands.** Goal outcome, weekday availability, first-weekday preview/change, and reconciliation.
  - Earlier days cannot receive new work. Goal progress is Done over noncancelled linked tasks; capacity counts each task once however many goals link it.
- **My Day integration.** `DayPlanning` (shared with My Day) seeds a newly created day from the confirmed week once and records the revision. Reconfirming a week that changes an already-opened day leaves an explicit add/remove comparison. Manual items are never offered for removal, and a task the person removed is never offered again.
- **Week-start changes.** Existing plans keep their dates. A current or future plan that crosses the new boundary needs an explicit remap. It is then superseded (its rows and revisions stay) and its remaining work moves by date into drafts to confirm again. Past days stay where they were.
- **Look-back facts.** The reusable historical-facts query: tasks completed during the seven local dates, plus the previous plan's assignments (done/cancelled/unfinished) and goals.
- **Desktop.** New `myWeek` RPC service (`open`, `startDraft`, `saveDraft`, `discardDraft`, `confirm`, `reconcile`, `setGoalOutcome`, `setAvailability`, `previewWeekStart`, `changeWeekStart`).
- **Frontend.** `MyWeek.tsx`:
  - the confirmed week: goals with progress and outcome, seven day columns with capacity and warnings, the pool, and the midweek check-in
  - the day comparison, a first-use availability editor and week settings (first weekday with preview/remap)
  - the five-step ritual: look back, leftovers, goals (with a gentle 2–3 hint and ideas), plan days (drag between columns or a per-task day menu, per-day time overrides, candidates) and confirm
  - the draft is saved at each step; Cancel discards it
  - a sidebar entry

Not done, deliberately:
- Scheduled planning-day and midweek prompts. They need NotificationJobs (package 10); the check-in is shown on demand.
- Look-back from stored journal snapshots: package 11.
- Undo of confirm, reconciliation or goal outcomes. Plans are profile-owned records with their own revision history; cancelled or replanned tasks keep task history and can be reopened.
- Adding an arbitrary task to the week from search. The week offers overdue, due-this-week, planned-this-week and today's My Day tasks, plus carried leftovers.

Results: Release build has 0 warnings. 337 TUnit tests pass (Domain 133 with 9 new, Application 22, Infrastructure 155 with 11 new My Week tests, Desktop 27 with 1 new). Frontend: type check, `vite build` and 52 tests (4 new) pass. Slopwatch is clean. The Release exe starts offline on a fresh data folder with migration 009 applied.

Package remains unchecked until the exit is shown in a native window: finish the five steps, see the tasks in My Day, and check that their planned and due dates are unchanged.

### [ ] 10 — Reminder engine and external-person follow-up

**Depends on:** 6, 8–9 and Windows spike from 0. Implement persisted rule/job/delivery services, adapter scheduling/cancel/activation, notification center, snooze/quiet/insistent behavior, waiting names and fixed/relative/inactivity follow-ups. Route notification completion through normal commands.

**Tests:** fake clock/delivery/action race matrix plus real packaged Windows delivery after exit, permission denied, stale callback, duplicate action, changed anchors and restart reconciliation. **Exit:** an offline reminder can open, complete or snooze the intended task exactly once; a follow-up appears privately and sends nothing.

Progress note (2026-11-10): implemented and verified headlessly with an in-app presenter. Not yet accepted in a native window, and the installed-package Windows notification adapter is still blocked on the package 0 spike.

- **Storage.** Migration 010 adds three tables, and the schema ceiling is now 10:
  - `WaitingDetails`: free-text person and start instant, with one active row per task.
  - `FollowUps`: Fixed, AfterWaiting, BeforeDue or AfterInactivity, plus snooze and resolved instants, with one unresolved rule per task. The due instant is a derived, unversioned column.
  - `NotificationJobs`: one row per desired or delivered notification, merging the plan's ReminderDeliveries and NotificationJobs. Each row has a source, generation and sequence, the original target, the delivery instant after quiet hours and snoozes, its state, how it was presented, and its OS schedule state with retries. A partial unique index allows only one pending delivery per source.
- **Domain.** `ReminderTiming` is pure and covers:
  - anchor instants: a date-only anchor uses the default reminder time in the profile zone, never midnight UTC
  - DST: a time in the gap moves forward, an ambiguous time takes the earlier instant
  - quiet hours that cross midnight
  - day and week arithmetic that keeps the wall time
  - follow-up deadlines and snooze presets
- **Sync in the same transaction.** Every recorded operation goes through the existing recorder, so no command needed its own reminder code. Before history is written, a task that left Waiting on someone ends its waiting details and resolves its follow-up; this is recorded, so history and undo include it. After history, the desired notifications of every touched task are recomputed: an unchanged target is left alone, and a changed target or delivery instant cancels the pending row and adds a new generation. Undo, recurrence successors, capture and templates are therefore covered too. Inactivity counts from the latest task history that changed a real field. Reminder, waiting and follow-up bookkeeping do not count, and viewing, reminders firing and My Day membership write no task history at all.
- **Delivery and actions.** `ReminderCommands` does the following:
  - **Delivery.** It resyncs each due task before marking anything delivered, so a stale job never fires.
  - **Missed repeats.** A source shows only its newest delivery. An insistent rule schedules only its next repeat, a rolling horizon with a 5-minute minimum.
  - **Actions.** Open, Done, Snooze (10 minutes, 1 hour, tomorrow at the default time, or a chosen time) and Dismiss. Each action checks the job's generation (stale callbacks are refused) and its state (a second click is AlreadyHandled). It runs under its own OperationId. Done goes through the standard `SetStatus` command with a derived idempotency key and asks for the open-subtask choice first. Snoozing never edits due dates.
  - **OS reconciliation.** Runs outside transactions: it schedules future rows, cancels replaced or acknowledged ones, and records failures for retry.
  - **Settings.** Changing the default time or quiet hours reschedules every pending row.
- **Waiting.** `WaitingCommands` offers set (person plus follow-up), clear, log, snooze, and the follow-ups due on a date. Logging writes a private diary update: it restarts an inactivity follow-up and resolves a one-off one. Nothing is sent and nothing is looked up. Quick add now creates real waiting and follow-up records; a follow-up date with no named person becomes an absolute reminder. The Waiting view groups by person (unnamed last) with the next follow-up, and search also matches the person's name.
- **Desktop.** New `reminders` RPC service (`list`, `add`, `update`, `delete`, `settings`, `updateSettings`, `alerts`, `center`, `act`) and `waiting` RPC service (`get`, `set`, `clear`, `log`, `snooze`, `due`). The background `ReminderEngine` resyncs at startup, then delivers due notifications and reconciles every 30 s and whenever alerts are requested; ticks never overlap. `InAppNotificationAdapter` is the development presenter allowed by §11, and it reports that it cannot schedule after exit.
- **Frontend.** `Reminders.tsx` adds:
  - the in-app alert stack, with Open, Done (asking about open subtasks), 10 minutes, 1 hour, Tomorrow and Dismiss, and a "missed while closed" note
  - a Notifications page: default reminder time, quiet hours, what this build can deliver, and the delivery history including missed and suppressed items
  - a Remind tab in the task editor: rules with their next delivery or why they are paused, insistent repeats, and waiting on someone with a follow-up rule, Log and Stop waiting
  - a Follow-ups section in My Day with Open, Log, Snooze and Stop waiting
  - person headings and follow-up badges in the Waiting view

Not done, deliberately:
- **Windows notification adapter.** Scheduled Windows notifications after exit, activation from toast buttons, tray and Quit need an installed MSIX and the package 0 spike on a real desktop. They plug into `INotificationAdapter` without changing the engine.
- **Planning and summary prompts.** The planning-day, midweek and summary prompts are packages 11 and 13; they will reuse `NotificationJobs` with a new source kind.
- **Follow-ups as stored My Day rows.** Follow-ups appear as a computed section rather than as `DayPlanItems` of kind 1. That avoids rebuilding a table, and they are removed by resolving or snoozing them.

Results: Release build has 0 warnings. 379 TUnit tests pass, 42 more than before:
- Domain 149 (12 new)
- Application 22
- Infrastructure 179 (23 new reminder/follow-up tests on real SQLite with a fake clock and presenter)
- Desktop 29 (2 new)

Frontend: type check and `vite build` pass, and 58 tests pass (6 new). Slopwatch is clean. The Release exe starts offline on a fresh data folder with migration 010 applied.

The new search test caught a real bug, now fixed: person matching was case-sensitive.

Package remains unchecked until the exit is shown in a native window and installed package: a reminder opens, completes or snoozes its task exactly once, also after Chaos was closed, and a follow-up appears in My Day without anything being sent.

### [ ] 11 — Daily/weekly plain summaries

**Depends on:** 4, 8–10. Implement structured historical facts, generation/idempotence/revisions, journal screens, morning/evening/weekly schedules and explicit carry-over. Connect weekly look-back to stored facts.

**Tests:** period boundaries/DST, reopened/deleted/renamed tasks, stable captured labels, duplicate generation, missed periods, project movement and carry-over dedupe. **Exit:** a week of deterministic fixture activity produces an understandable journal and next-week review without AI/network.

Progress note (2026-11-10): implemented and verified headlessly. Not yet accepted in a native window.

- **Storage.** Migration 011 adds `Summaries`, and the schema ceiling is now 11. Each row is one revision of one period: kind (Morning, Daily, Weekly), local dates and half-open UTC boundaries, captured-through instant, trigger (Scheduled, CatchUp, Manual), versioned facts JSON, resource version and prompt times. Profiles gain `MorningTime` and `WeeklySummaryDay`; existing and new profiles get 08:00, 18:00 and the week's last day. New indexes read history and diary entries by instant.
- **Domain.** `SummarySchedule` is pure: the weekly due date for a chosen weekday, local-time instants with the reminder DST rules, and which evening summaries are due or missed (at most seven earlier dates).
- **Facts.** `SummaryFactsBuilder` reconstructs period state from append-only task history:
  - completion events grouped by list, with a "reopened later" annotation and a separate still-done count
  - project progress at both ends of the period, including tasks moved out
  - planned but unfinished: My Day items (minus removed ones), confirmed week assignments and tasks planned in the period
  - waiting on someone, next period's deadlines, diary updates with excerpts, a suggested start for the next day or week, and week-plan progress with goals
  - morning: the day's selected and week-planned tasks, due today, overdue count and follow-ups due
- **Application.** `SummaryCommands` covers scheduled generation (safe on every tick), explicit regeneration as a new revision, the journal with cursor paging, prompts (the newest per kind, after quiet hours), dismissal, carry-over preview and apply, and the schedule settings. Carry-over puts chosen tasks on the next My Day or in next week's draft pool, once each, and never edits task dates. My Week's look-back reads the stored weekly summary when a complete one exists.
- **Desktop.** New `journal` RPC service (`list`, `get`, `find`, `generate`, `prompts`, `dismiss`, `previewCarryOver`, `carryOver`, `settings`, `updateSettings`). The background engine also generates due summaries on its 30-second tick.
- **Frontend.** `Journal.tsx` adds a Journal page: past summaries, one summary in detail (sections per kind, "through HH:mm", versions, Make it again), previewed carry-over with checkboxes, "make summary now" for any past date and the schedule settings. A "summary ready" banner opens or dismisses prompts. My Week's look-back shows the journal's captured titles.

Not done, deliberately:
- **OS notifications for summaries.** Prompts are shown in the app. A Windows notification needs the installed-package adapter that reminders are also waiting for (package 0 spike).
- **Prompts outside NotificationJobs.** That table requires a task; summary prompts are stored on the summary row with the same quiet-hour rule. See `docs/decisions.md`.
- **Translated summary text.** Summaries render in the UI resource language (English), as decided in §1.

Results: Release build has 0 warnings. 405 TUnit tests pass, 26 more than before:
- Domain 159 (10 new)
- Application 22
- Infrastructure 194 (15 new journal tests on real SQLite with a fake clock, including a week of fixture activity feeding the next review)
- Desktop 30 (1 new)

Frontend: type check and `vite build` pass, and 61 tests pass (3 new). Slopwatch is clean. The Release exe starts offline on a fresh data folder with migration 011 applied.

Package remains unchecked until the exit is shown in a native window: a week of activity produces a readable journal and the next week's look-back.

### [ ] 12 — Data protection and transfer

**Depends on:** 1–11. Implement scheduled/manual validated backups/retention, restore recovery, lossless JSON, Markdown/Org export, JSON/Org-subset/CSV import preview/mapping/rollback and permanent deletion/reset. Test every persisted feature, not tasks alone.

**Tests:** WAL-active backup/restore, corrupt/newer backup refusal, interrupted restore, no-space failure, export/import round-trip, duplicate import, hostile paths/archives and deleted/tombstoned relationships. **Exit:** restore to another clean data root and compare semantic contents, then recover from a simulated bad import using a local backup.

### [ ] 13 — Desktop polish, packaging and release gate

**Depends on:** all prior packages. Complete settings/global shortcuts/tray/activation, accessibility/RTL/dark/high-DPI, performance checks, privacy/offline verification, MSIX packaging/update preservation, README and acceptance evidence. Remove spike/template/demo UI and unused registrations.

**Exit:** all section 17 criteria pass on a fresh installed Windows environment, with explicit evidence and any honest platform limitations documented. A successful `dotnet publish` alone is insufficient.

## 16. Verification strategy

### TUnit conventions and isolation

Use `[Test]`, `[Arguments]` for compact cases, method/class data for larger fixtures and awaited fluent assertions (`await Assert.That(actual).IsEqualTo(expected)`). Use repository naming `Method_Scenario_ExpectedBehavior`, Arrange/Act/Assert and async lifecycle hooks for setup/cleanup. Tests are independent; do not assume same-class execution is serialized. Allocate a unique temp data root/database per integration test. Keep clocks, OS presenters and ID generation injectable; no sleep-based timing tests, retries masking flakes or disabled tests.

Domain tests require neither SQL nor NeoAstra. Application tests use recording/fake ports for policy and atomic-command intent. Infrastructure tests run **real file-backed SQLite**, because in-memory SQLite does not prove WAL/backup/restart behavior. Desktop tests exercise RPC serialization, mappings and activation with fake presenters; separate installed-package acceptance proves native scheduling, windows and shortcuts. Frontend component tests use the template runner plus React Testing Library/jsdom or another documented compatible DOM test setup pinned during bootstrap. .NET unit tests remain TUnit; frontend interaction checks complement them.

### Mandatory behavior matrix

| Area | Cases that must be demonstrated |
| --- | --- |
| Dates | Planned vs due; all-day date unchanged across zones; timed due; DST gap/overlap; midnight/resume; year boundary; configured week start |
| Recurrence | Every supported frequency/interval; weekday set; Jan 31; Feb 29; last Friday; fixed vs completion mode; late completion after many missed slots; count/until; skip/move/edit future; undo successor conflict |
| Capture | Six languages; mixed language; Arabic digits; original UTF-16 spans with emoji/combining marks; literal May; ambiguous date; unknown container; metadata-only input; stale preview; indented batch rollback |
| Task state | Complete/reopen/cancel; subtasks kept vs completed; soft delete/restore; list/project move; tag merge; rank compaction; template state reset |
| Planning | Empty manual day + confirmed seed; explicit suggestions; removal not reseeded; daily rollover; draft cancel; confirm failure; capacity unknown/overload; carry-over without task-date mutation |
| Reminders | Absolute/relative/date-only; removed anchor; two reminders same task; snooze; midnight quiet hours; insistent stop; stale activation; denied permission; outage/resume; duplicate delivery/action |
| Follow-up | Fixed/start/due/inactivity anchors; diary resets inactivity; viewing does not; clear waiting/complete resolves; no messages sent |
| Content | GFM tables/code/checklists; HTML/script/URL blocking; no remote images; rich-paste fidelity; autosave failure/restart; backlinks after source edit |
| History | As-of snapshots; deleted data; compound undo; stale inverse rejected; recurrence-generated child protected; summaries use period facts |
| Storage | All migrations; FK/check constraints; concurrent edits; command receipt replay; cancellation around commit; write rollback; WAL backup; restore after interruption; newer schema rejected |
| Transfer | Every persisted feature round-trips JSON; supported Org/CSV fixtures; invalid final row; duplicate import; unsafe filenames/archive traversal; schema version compatibility |
| Desktop | Fresh offline installed start; packaged assets; first capture; single-instance/global shortcut; minimized/background/exited notifications; DPI/RTL/keyboard/screen reader; upgrade retains data |

Add substantive regression cases when defects reveal a missing invariant. Do not test trivial auto-properties, mirror private helper implementation or use snapshots as a substitute for behavioral assertions.

### Repeatable commands and CI

After bootstrap the developer path is:

```powershell
dotnet tool restore
dotnet restore Chaos.slnx --locked-mode
dotnet build Chaos.slnx -c Release --no-restore
dotnet test --solution Chaos.slnx -c Release --no-build
dotnet run --project tests/Chaos.Domain.Tests -c Release --no-build
dotnet run --project tests/Chaos.Application.Tests -c Release --no-build
dotnet run --project tests/Chaos.Infrastructure.Tests -c Release --no-build
dotnet run --project tests/Chaos.Desktop.Tests -c Release --no-build
dotnet slopwatch analyze -d . --fail-on warning
```

Verify the selected MTP runner's exact `dotnet test --help` syntax in package 0; encode the proven commands in `eng/verify.ps1`. CI must fail if discovery unexpectedly yields zero tests. The direct TUnit executable runs are a diagnostic fallback/explicit project path, not permission to leave solution discovery broken. Pin the actual Slopwatch tool version and confirm its CLI rather than blindly copying a stale skill example.

The NeoAstra build restores its locked frontend graph and generates contracts before compiling the frontend. Once that initial host build has staged the local client, frontend checks run in `src/Chaos.Desktop/frontend`:

```powershell
npm ci
npm run build
npm test
```

Windows CI runs locked restore, Release compilation, TUnit domain/application/infrastructure/desktop contract tests, frontend checks, Slopwatch and self-contained `win-x64` publish + MSIX artifact generation. Installed desktop acceptance requires a real interactive Windows runner/manual checklist; never equate a headless publish with it. Avoid forcing NativeAOT/trimming to make an unrelated packaging benchmark look better.

Use a documented representative dataset: 10,000 top-level tasks, 20,000 subtasks, 20,000 diary entries, 1,000 tags, a year of plans and 1,000 recurrence series. Initial targets on recorded reference hardware: cold first usable window ≤3 seconds, ordinary task save/quick-add preview p95 ≤150 ms excluding debounce, first page search ≤250 ms, and no visible UI freeze during backup/import. These are product engineering targets chosen for this plan, not measured results. Page/virtualize long lists and capture timings before optimizing.

## 17. Definition of done and release evidence

Phase 1 is complete only when:

- A new installed Windows user can launch disconnected, enter a task immediately and retrieve it after restart, without an account or server.
- C# 15 is explicitly selected in all C# projects, NeoAstra remains the desktop framework, SQLite is authoritative, and test projects use the latest stable TUnit resolved at kickoff.
- Tasks/subtasks/templates/tags/Markdown/pad/diary/history/undo work through the real UI and survive restart, including failed-write handling.
- Six-language offline capture works with visible editable previews, Arabic digits/RTL and multiline subtasks.
- Recurrence supports the documented patterns and exceptions without pile-up or lost occurrence history.
- My Week supports the full ritual, goals, realistic capacity and explicit confirmation; My Day follows the confirmed plan while preserving daily personal choices.
- Plain daily/weekly journals reflect historical activity and offer reviewed carry-over.
- Offline Windows reminders and follow-ups support open/done/snooze with tested packaging/activation, durable reconciliation and quiet hours.
- Validated local backups restore successfully; lossless JSON round-trips all implemented data; Markdown/Org export and the documented import subset work.
- Core interaction is usable with keyboard/screen reader and high DPI; no app feature requires a network request, CDN or development server at runtime.
- Tests/checks and installed acceptance have recorded commands, versions, nonzero counts and results in `docs/acceptance.md`. Any unsupported future platform is clearly identified rather than represented as tested.
- No deferred feature appears as a working-looking placeholder, and no disabled tests, empty catches, global warning suppressions or replaced user skill files were introduced to pass a gate.

Leave each work package checked only with actual results. If blocked, record the exact dependency/API/behavior and the smallest next action; do not label an untested assumption complete.
