# GitHub Issues connection — implementation plan

> **Authentication decision — latest, 2026-11-11.** The user does not want a GitHub App
> registration and explicitly authorized Chaos to obtain an account-specific token from the
> existing GitHub CLI login and use it in memory. This decision supersedes the GitHub App proposal and the earlier ban on `gh auth token` and tokens in Chaos.
> GitHub CLI owns persistent credentials; Chaos must never store tokens in Credential Manager,
> SQLite, backups, exports, logs, exceptions, RPC, renderer, command lines, URLs or environment.
> Invoke trusted `gh auth token --hostname github.com --user <login> --no-refresh` directly without a shell,
> capture bounded stdout privately, and discard all diagnostic output after sanitization. Clear
> inherited token/debug/host overrides; never pass credentials to another process. Resolve one
> token snapshot for a request, verify `/user` with that exact token against the connected numeric
> account ID, and use that same snapshot for the API call. Fail closed on any mismatch, ambiguous
> credential, expired/revoked token, lookup error or failed identity check. Never retry a mutation
> blindly. Allow credential rotation only by resolving and verifying a fresh snapshot. GitHub CLI
> account switching cannot change an already captured snapshot; a stale or incorrect credential
> can only fail the identity check. Do not log the token, captured stdout, response body, headers,
> issue text or raw process errors. Apply the guard to reads and writes. Discourage retaining
> tokens longer than necessary. Disconnect leaves GitHub CLI signed in.
>
> **Revised G0:** verify the account-specific CLI lookup and active-account switch race with two
> throwaway accounts, invalid/expired and environment-overridden credentials, Windows process
> output bounds and timeout cleanup, REST/GraphQL, conditional requests, limits, redirects,
> proxy and installed-MSIX access. Confirm numeric identity checks and no credential leakage.
> The G3 adapter uses `HttpClient` with redirect handling disabled and destination validation;
> `gh` is used for credential retrieval only. G4 detects the CLI and helps users sign in outside
> Chaos. GitHub App installation and registration are not required. G1 remains independently
> implementable. Per user approval, outstanding live G0 checks are release gates; offline-tested
> G2 and later work may proceed. Keep live evidence incomplete until actually verified.

> **G3 transport detail:** CLI children only read local configuration/credentials: `gh config
> get user --host github.com` for setup metadata and account-specific `gh auth token` with
> `--no-refresh`. Expired authorization requires the user to refresh/sign in externally.
> CLI active-account selection is used during setup; existing connections use their saved login
> and verified numeric ID, so switching the CLI's active account does not silently change the
> Chaos account. Current support targets the standard Program Files installation and baseline
> CLI version 2.103.3. Direct API requests own host/redirect, timeout, rate and size controls.
Prepared 11 November 2026 from `SPEC.md` (Connecting to other systems → GitHub Issues), `PLAN.md`
and the Phase 1 code as it stands at commit `2ddc3fe`. This is a handoff for implementing agents,
not a claim that the feature exists. Work through the packages in section 12 in order, keep their
checkboxes and progress notes current in the same style as `PLAN.md` §15, and meet each exit
criterion before starting the next package.

`SPEC.md` stays the product authority. Where this plan has to choose (because the spec describes
GitHub inside shared spaces, which do not exist yet), the choice is written down in section 2 and
must also be recorded in `docs/decisions.md` when it is implemented.

---

## 0. How to use this plan

- **Read first, every package:** this file's sections 1–11, `PLAN.md` §3–§5 and §13 (rules,
  structure, SQLite design, RPC/frontend rules), `docs/decisions.md` (later decisions override
  `PLAN.md`), and the source files the package names.
- **Repository conventions still apply.** C# 15, `net11.0` / `net11.0-windows10.0.19041.0`,
  warnings as errors, central package versions with locked restore, TUnit with
  `Method_Scenario_ExpectedBehavior` names, file-backed SQLite in infrastructure tests, no
  in-memory "mock persistence" counted as done. Roslynk is currently skipped by user direction
  (`docs/decisions.md`, 2026-11-09); follow whatever that file says at the time you start.
- **Gate after each batch:** `pwsh -File eng/verify.ps1` (restore, Release build including the
  frontend, every TUnit project, Slopwatch) and `npm test` in `src/Chaos.Desktop/frontend`.
  Record real counts in the package's progress note.
- **Never** retrieve or handle a GitHub token in Chaos, SQLite, backups, exports, logs, exceptions,
  RPC or the renderer. GitHub CLI owns authentication. Never make a network call
  inside a database write transaction.
- **Never** let Chaos start a GitHub request unless the user explicitly initiates connection
  setup or has connected GitHub. Otherwise Chaos stays as offline as Phase 1 (acceptance step 16).
- If a GitHub API behaviour below turns out different in reality (section 12, package G0 checks
  the risky ones), record the real behaviour in `docs/decisions.md`, adjust the affected package,
  and say so in the progress note. Do not code against an assumed API.

---

## 1. What we are building

A personal, opt-in GitHub Issues connection for the desktop app:

1. In Settings → Connections → GitHub, the user connects an existing GitHub CLI installation and
   adds repositories its active account can access.
2. For each repository the user chooses what to bring in (all open issues, or narrowed by labels,
   milestone or "assigned to me") and the list or project the tasks land in.
3. Matching issues appear as tasks, marked with a GitHub icon and a link back to the issue.
4. Changes flow both ways on a background schedule, offline-tolerant, with GitHub as the source of
   truth on conflicting changes and the losing local value kept in task history.
5. Personal extras (My Day, My Week, reminders, waiting/follow-ups, planned date, estimate,
   priority, container, section, order) stay local and never touch GitHub.
6. The user can pause, sync now, remove a repository or disconnect. Removal keeps the tasks as
   ordinary tasks with a note of where they came from.

### Field mapping (from the spec, made concrete)

| GitHub | Chaos | Direction | Notes |
| --- | --- | --- | --- |
| Issue title | Task title | Both ways | GitHub limit applies to connected tasks (section 6.4) |
| Issue body | Task notes (Markdown, verbatim) | Both ways | GFM is already the notes format |
| Top-level task list items in the body (`- [ ]`, `- [x]`) | Subtasks | Both ways | Lines in the body stay the source; section 6.2 |
| Open / closed + `state_reason` | Status | Both ways | Section 6.3 |
| Labels | Tags | Both ways (set merge) | Section 6.5 |
| Assignees | Assignee logins shown on the task; "Assign to me" | GitHub → app, plus self-assign both ways | No member model yet; section 2 decision 4 |
| Milestone | Optional project container | GitHub → app | Section 6.6 |
| Milestone due date | Task due date | GitHub → app | Read-only in the editor while it comes from a milestone |
| Comments | Diary entries | Both ways | Posting a diary entry is a per-entry choice; section 6.7 |
| Pull requests that close the issue | Links on the task, with state and review decision | GitHub → app | GraphQL; package G9 |
| Issue type, author, `html_url`, number | Stored connection fields | GitHub → app | For display now and custom views later |

Not mapped (app-only): planned date/time, estimate, capacity category, priority, container and
section (except the milestone placement rule), order, My Day/My Week, reminders, waiting and
follow-ups, recurrence (not allowed on connected tasks), pad links, templates.

---

## 2. Decisions this plan makes

Each decision goes into `docs/decisions.md` when its package lands. Items marked **(confirm)**
are also listed in section 13 for the product owner.

1. **Ahead of Phase 3, scoped to the Personal space (confirm).** The spec puts GitHub in Phase 3
   with shared spaces. Phase 1 has exactly one local profile and one Personal space. This plan
   ships the connection for the Personal space now, but every row is keyed by `SpaceId` and the
   spec's rule "one connection per space" is enforced by a unique index, so shared spaces can
   reuse the model unchanged. Owner/member roles, member-linked GitHub accounts and the "private
   repository in a space with outsiders" warning arrive with sharing.
2. **One connection, many repositories.** A connection is one GitHub account signed in on this
   device for one space. Under it, each added repository is a *repository subscription* with its
   own filter and target container. This matches "approving which repositories the space can
   reach".
3. **GitHub CLI authentication.** The user installs `gh` and signs in independently. Chaos invokes
   `gh auth token --hostname github.com --user=<login> --no-refresh` privately and uses the
   captured credential only in memory for direct API calls, bound to the numeric user ID. GitHub CLI
   authorizations can grant broader access than an individual repository, and organization policy
   may restrict them. Chaos subscriptions determine what Chaos syncs, not what `gh` can access.
4. **Assignees are shown, and only self-assignment is pushed.** Without members there is nobody to
   match a GitHub login to except the signed-in user. The task shows assignee logins; the user can
   "Assign to me" / "Unassign me"; "assigned to me" works as a filter. Assigning other people
   arrives with members.
5. **State-based sync, not an operation log.** Each linked issue keeps a *base* snapshot: the
   GitHub values both sides last agreed on. A sync computes the local projection, fetches the
   remote state and does a three-way merge per field (section 6.1). This is robust with undo,
   restore, imports and edits made while offline, because whatever path changed the task, the
   difference from the base is what gets sent. A SQLite trigger marks a link dirty when a synced
   column changes, so the engine knows what to look at without scanning everything.
6. **GitHub wins conflicting changes, per field.** The spec's rule. Labels and assignees are
   sets and merge element by element, so they never conflict. Title, body and open/closed state
   conflict when both sides changed since the base; GitHub's value is applied and the local value
   is written to task history as a `github.conflict` event, with a visible notice on the task.
7. **Inbound changes are history, not undo.** Like imports (`docs/decisions.md`, package 12),
   changes pulled from GitHub write `TaskHistory` rows with actor `github:<login>` and no
   `UndoEntries`. Undoing a local change that was already sent simply makes a new local change,
   which is sent again. Stale undo after an inbound change is already rejected by version checks.
8. **No network inside a transaction; short transactions around it.** Each sync step reads a
   snapshot, talks to GitHub with no transaction open, then commits a short write transaction
   that re-checks entity versions. If the task changed in between, the step is abandoned and the
   link stays dirty for the next pass.
9. **CLI authentication and direct HTTP.** Implement `IGitHubApi` with native-only credential
   acquisition, a single credential snapshot for identity check and API operation, source-generated
   JSON, destination validation and no automatic redirects. Test with fake processes and HTTP handlers.
10. **Stored credentials belong to GitHub CLI.** Chaos reads an account-specific credential into
    memory only for native API calls; it never persists, exports or revokes it.
    GitHub CLI normally uses the system credential store but can fall back to plaintext storage;
    document this and do not promise otherwise. Disconnect and reset leave `gh` signed in.
11. **Deleting a connected task never closes or deletes the issue.** The link becomes *Ignored*:
    no sync while the task is in the trash; restoring the task re-links and re-merges. Purging it
    keeps a link row without a task so the issue is not brought in again.
12. **Issues that leave the filter stay as tasks** (link state *Out of scope*, no more sync, shown
    plainly). If they match again later, the same task is re-linked. Issues that become
    unreachable (deleted, transferred without access, access removed) become *Unavailable* and the
    task stays.
13. **Milestone placement is "managed" only while the task is where the connection put it.** If
    the user moves a connected task to another list or project, Chaos stops moving it.
14. **Diary posting is per entry.** On a connected task, the diary composer shows "Also post to
    GitHub", defaulting to the connection setting (default **on**, confirm). Private notes stay
    private unless the box is ticked, matching "app-only details stay in the app unless the user
    chooses to send them".
15. **No label creation from tags by default.** Adding a local tag that matches an existing
    repository label (by GitHub's case-insensitive name) adds the label. Other tags stay local
    unless the connection setting "Create GitHub labels for new tags" is on (default off).
16. **github.com only at first.** `ApiBaseUrl` and `WebBaseUrl` are stored per connection so
    GitHub Enterprise Server can be added later, but the UI offers github.com only.

---

## 3. Architecture

Dependencies keep pointing inward (`PLAN.md` §4). New code:

```text
src/Chaos.Domain/GitHub/
  IssueBody.cs              checklist scan + rewrite, line-ending and byte preserving
  IssueStateMapping.cs      open/closed/state_reason <-> TaskItemStatus
  LabelNames.cs             label <-> tag name rules (case folding, nested segments)
  SyncMerge.cs              three-way merge per field, set merge, conflict report
  IssueProjection.cs        the comparable "GitHub view" of a task (title, body, state, labels, assignees)
src/Chaos.Application/GitHub/
  GitHubConnectionCommands.cs   sign-in, repositories, settings, pause/resume, remove, disconnect
  GitHubSyncCommands.cs         one sync pass: pull, merge, push, record
  GitHubTaskCommands.cs         per-task actions: assign to me, retry, keep as ordinary task
  GitHubQueries.cs              status, links for task IDs, repository task lists
src/Chaos.Application/Abstractions/
  GitHubApi.cs              IGitHubApi port + transport records (no HTTP types)
  (DataStores.cs)           IDataSession gains IGitHubStore GitHub { get; }
src/Chaos.Application/Contracts/GitHubRecords.cs
src/Chaos.Infrastructure/GitHub/
  GitHubCliClient.cs        IGitHubApi with CLI login and direct REST + GraphQL
  GitHubCliRunner.cs        bounded process execution and executable discovery
  GitHubJsonContext.cs      source-generated JSON for GitHub payloads
  GitHubRateLimiter.cs      rate-limit headers, Retry-After, write spacing
src/Chaos.Infrastructure/Sqlite/SqliteGitHubStore.cs
src/Chaos.Infrastructure/Migrations/014_github.sql, 015_github_pull_requests.sql
src/Chaos.Desktop/Platform/GitHubSyncEngine.cs
src/Chaos.Desktop/Services/GitHubRpcService.cs
src/Chaos.Desktop/frontend/src/GitHubSettings.tsx (+ .test.tsx)
src/Chaos.Desktop/frontend/src/GitHubTask.tsx (+ .test.tsx)
tests/*/GitHub/…           per layer, plus tests/Chaos.Infrastructure.Tests/Testing/FakeGitHub.cs
docs/github.md             user-facing behaviour, limits and privacy
```

Create files only when the package that needs them starts (`PLAN.md` §4: no empty placeholders).

### Ports

`IGitHubApi` accepts no credential and returns transport
records defined in `Application/Abstractions/GitHubApi.cs`: `GitHubUser`,
`GitHubRepository`, `GitHubIssue`, `GitHubComment`, `GitHubLabel`, `GitHubMilestone`,
`GitHubPullRequestRef`, `GitHubPage<T>` (items, next-page cursor, ETag, not-modified flag) and
`GitHubIssueUpdate` (only the fields being changed). Failures throw `GitHubApiException` with a
`GitHubErrorKind`:

| Kind | Typical cause | Engine reaction |
| --- | --- | --- |
| `Unauthorized` | `gh` signed out, revoked or expired authorization | Connection → *Needs sign-in*; stop |
| `CliUnavailable` | `gh` missing or unsupported | Stop; show install/update instructions |
| `AccountMismatch` | active account differs from the connected account | Stop; ask the user to switch back |
| `Forbidden` | 403 without rate-limit headers: token permissions, org policy, locked issue | Subscription or link error, plain message |
| `NotFound` | 404 | Link → *Unavailable* (after a confirming re-read) |
| `Gone` | 410 deleted issue | Link → *Unavailable* |
| `Moved` | 301 transferred issue | Follow once; if the new repo is not subscribed, link → *Unavailable* with the new URL |
| `RateLimited` | 403/429 with rate-limit or `Retry-After` headers | Pause the connection until the reset instant |
| `Validation` | 422 (title too long, invalid label) | Link error naming the field; no retry until the field changes |
| `Network` | DNS, TLS, timeout, offline | Back off; status "Waiting for a connection" |
| `Server` | 5xx | Back off |

Messages shown to the user are written in the application layer, never copied from GitHub
responses verbatim (they can contain URLs or request IDs; those go to the trace log only).

### Background engine

`GitHubSyncEngine` mirrors `ReminderEngine` (`src/Chaos.Desktop/Platform/ReminderEngine.cs`):
a `TimeProvider` timer, a `SemaphoreSlim` gate so passes never overlap, `RunPausedAsync` for
restore/reset/import quiescing, `LastError` for diagnostics. It is constructed in
`src/Chaos.Desktop/Program.cs` after storage, declared so it disposes before storage closes, and
started only when a connection exists (the engine itself checks; no timer runs without one).

---

## 4. Storage

All rules from `PLAN.md` §5 apply: canonical GUID text IDs, fixed-width ISO UTC instants,
integer enums with `CHECK`s, `Version` optimistic concurrency, partial unique indexes for nullable
uniqueness, one trigger per line (the migration runner splits on lines ending in `;`, see
`003_tags_search.sql`). GitHub numeric IDs are stored as `INTEGER` (they fit in 64 bits).

Raise the bootstrap's supported schema ceiling with each migration (it is 13 today; see the
package 13 note in `docs/decisions.md` and `StorageBootstrap`).

### Migration 014 — `014_github.sql`

**`GitHubConnections`** — one active per space.

| Column | Notes |
| --- | --- |
| `Id` TEXT PK | |
| `SpaceId` TEXT FK Spaces | `UX_GitHubConnections_ActivePerSpace (SpaceId) WHERE DeletedAtUtc IS NULL` |
| `ProfileId` TEXT FK Profiles | who connected it |
| `WebBaseUrl`, `ApiBaseUrl` TEXT | `https://github.com`, `https://api.github.com` |
| `AccountId` INTEGER, `AccountLogin` TEXT | from `GET /user`; the login is display only, the ID is identity |
| `State` INTEGER | 0 Active, 1 Paused, 2 NeedsSignIn |
| `PostDiaryByDefault` INTEGER 0/1 | default 1 |
| `CreateLabelsForTags` INTEGER 0/1 | default 0 |
| `RateLimitResetUtc` TEXT NULL | set while rate limited |
| `LastSyncStartedUtc`, `LastSyncCompletedUtc` TEXT NULL | |
| `LastErrorKind` INTEGER NULL, `LastErrorMessage` TEXT NULL | plain, user-facing |
| `Version`, `CreatedAtUtc`, `UpdatedAtUtc`, `DeletedAtUtc` | |

**`GitHubRepositories`** — repository subscriptions.

| Column | Notes |
| --- | --- |
| `Id` TEXT PK, `ConnectionId` FK | |
| `RepositoryId` INTEGER | stable across renames/transfers; `UX (ConnectionId, RepositoryId) WHERE DeletedAtUtc IS NULL` |
| `Owner`, `Name`, `FullName`, `HtmlUrl` TEXT, `IsPrivate` 0/1 | refreshed on each pass |
| `TargetContainerId` TEXT FK Containers, `TargetSectionId` TEXT NULL | composite FK to Sections like Tasks |
| `FilterJson` TEXT | versioned: `{ "v":1, "labels":[…], "labelMode":"any"|"all", "assignee":"any"|"me"|"none", "milestone":null|number|"none", "includeClosedDays":0..365 }` |
| `MilestonesAsProjects` 0/1 | default 0 |
| `State` INTEGER | 0 Active, 1 Paused, 2 Unavailable |
| `IssuesCursorUtc` TEXT NULL | newest `updated_at` fully processed |
| `CommentsCursorUtc` TEXT NULL | newest comment `updated_at` processed |
| `IssuesEtag`, `CommentsEtag`, `LabelsEtag`, `MilestonesEtag` TEXT NULL | machine state |
| `InitialImportDoneUtc` TEXT NULL | |
| `LastSyncedUtc`, `LastErrorKind`, `LastErrorMessage` | |
| `Version`, `CreatedAtUtc`, `UpdatedAtUtc`, `DeletedAtUtc` | |

**`GitHubIssueLinks`** — one row per issue brought in.

| Column | Notes |
| --- | --- |
| `Id` TEXT PK | |
| `RepositorySubscriptionId` FK GitHubRepositories | |
| `ConnectionId` FK | denormalized for the unique index |
| `TaskId` TEXT NULL FK Tasks | `UX (TaskId) WHERE TaskId IS NOT NULL`; NULL only after purge |
| `IssueId` INTEGER, `IssueNodeId` TEXT, `IssueNumber` INTEGER, `HtmlUrl` TEXT | `UX (ConnectionId, IssueId)` |
| `State` INTEGER | 0 Linked, 1 OutOfScope, 2 Unavailable, 3 Ignored (task deleted/purged), 4 Detached (connection or repo removed) |
| `BaseJson` TEXT | versioned base snapshot: title, body, open/closed, state reason, sorted label IDs+names, sorted assignee IDs+logins, milestone number/title/due, issue type, author login |
| `BaseRemoteUpdatedUtc` TEXT | GitHub `updated_at` of the base |
| `IssueEtag` TEXT NULL | machine state |
| `LocalAssigneeIntent` INTEGER NULL | NULL no change, 1 assign me, 0 unassign me |
| `PlacementManaged` 0/1 | decision 13 |
| `LocalDirty` 0/1, `LocalDirtySinceUtc` TEXT NULL | set by triggers below |
| `LastSyncedUtc` TEXT NULL | "last in step" |
| `ConflictNoticeUtc` TEXT NULL | shown until the user dismisses it |
| `LastErrorKind`, `LastErrorMessage`, `ErrorAttempts`, `NextAttemptUtc` | per-link backoff |
| `Version`, `CreatedAtUtc`, `UpdatedAtUtc` | |

Indexes: `(RepositorySubscriptionId, State)`, `(LocalDirty) WHERE LocalDirty = 1`,
`(NextAttemptUtc) WHERE NextAttemptUtc IS NOT NULL`.

**`GitHubChecklistItems`** — the base for body checklist ↔ subtasks.

| Column | Notes |
| --- | --- |
| `SubtaskId` TEXT PK FK Subtasks | |
| `IssueLinkId` FK | |
| `BaseOrdinal` INTEGER | position among top-level checklist items in the base body |
| `BaseText` TEXT, `BaseChecked` 0/1 | |

**`GitHubCommentLinks`** — diary entries mirrored to/from comments.

| Column | Notes |
| --- | --- |
| `DiaryEntryId` TEXT PK FK DiaryEntries | |
| `IssueLinkId` FK | |
| `CommentId` INTEGER NULL | `UX (IssueLinkId, CommentId) WHERE CommentId IS NOT NULL`; NULL while waiting to post |
| `AuthorLogin` TEXT NULL, `HtmlUrl` TEXT NULL | |
| `Origin` INTEGER | 0 FromGitHub, 1 PostedFromChaos |
| `State` INTEGER | 0 InStep, 1 PendingPost, 2 PendingEdit, 3 PendingDelete, 4 RemoteDeleted, 5 HiddenLocally |
| `BaseBody` TEXT NULL, `BaseRemoteUpdatedUtc` TEXT NULL | |
| `LastErrorKind`, `LastErrorMessage`, `ErrorAttempts`, `NextAttemptUtc` | |

**`GitHubLabelTags`** — `(RepositorySubscriptionId, LabelId)` PK, `LabelName`, `LabelColor`,
`TagId` FK Tags. Mapping is by label ID, so a rename on either side does not break it.

**`GitHubMilestoneProjects`** — `(RepositorySubscriptionId, MilestoneNumber)` PK, `MilestoneId`,
`ContainerId` FK Containers, `BaseTitle`, `BaseDueOn`.

**`GitHubSyncRuns`** — diagnostics ring buffer (keep the newest 200 per connection): `Id`,
`ConnectionId`, started/finished instants, request count, not-modified count, created, updated,
pushed, conflicts, error kind. Excluded from the archive.

**Dirty triggers** (one line each). They set `LocalDirty = 1` and `LocalDirtySinceUtc` (if NULL)
on the link whose `TaskId` matches, when the link `State = 0`:

- `AFTER UPDATE OF Title, Notes, Status, DeletedAtUtc ON Tasks`
- `AFTER INSERT ON Subtasks`, `AFTER UPDATE OF Title, Status, DeletedAtUtc ON Subtasks`
- `AFTER INSERT ON TaskTags`, `AFTER DELETE ON TaskTags`
- `AFTER INSERT/UPDATE ON GitHubCommentLinks` when the new state is pending
- `AFTER UPDATE OF RawMarkdown, DeletedAtUtc ON DiaryEntries` when a comment link exists (marks the comment link `PendingEdit`/`PendingDelete` too)

The sync engine's own write transaction ends by setting `LocalDirty` to the value it computed, so
triggers firing during inbound writes do not leave a false dirty flag. Write a test for exactly
that.

### Migration 016 — `016_github_pull_requests.sql` (package G9)

**`GitHubPullRequestLinks`** — `(IssueLinkId, PullRequestId)` PK, `RepositoryFullName`, `Number`,
`Title`, `HtmlUrl`, `State` (0 Open, 1 Draft, 2 Merged, 3 Closed), `ReviewDecision` (NULL,
0 ReviewRequired, 1 Approved, 2 ChangesRequested), `UpdatedAtUtc`.

### Store

`IGitHubStore` (in `DataStores.cs`, added to `IDataSession` and to `RecordingDataSession` as a
pass-through: GitHub rows are bookkeeping, not undoable) with narrow typed methods only, e.g.
`GetConnectionForSpaceAsync`, `ListSubscriptionsAsync`, `FindLinkByIssueAsync`,
`FindLinkByTaskAsync`, `ListDirtyLinksAsync(limit)`, `ListLinksDueForRetryAsync(now, limit)`,
`UpdateLinkAsync(row, expectedVersion)`, `ListChecklistItemsAsync`, `ReplaceChecklistItemsAsync`,
`ListCommentLinksAsync`, `ListLabelTagsAsync`, `UpsertLabelTagAsync`, `ListLinksForTasksAsync(ids)`
(batched, for task rows), `AddSyncRunAsync` (trims to 200). Implement in
`SqliteGitHubStore.cs`, following `SqliteReminderStores.cs` style.

### Data protection integration

- **JSON archive** (`JsonArchive.cs`): GitHub tables are user data and are exported, except
  `GitHubSyncRuns` (add to `ExcludedTables`). Add machine columns reset on export: every `*Etag`,
  `IssuesCursorUtc`, `CommentsCursorUtc`, `RateLimitResetUtc`, `NextAttemptUtc`, `ErrorAttempts`.
  A restored connection must verify the local `gh` installation and active account before any
  sync. If unavailable or mismatched, show the relevant setup state and do not sync.
- **JSON merge import** (`ArchiveMerge.cs`): a merged task whose link would collide with an
  existing `(ConnectionId, IssueId)` is imported as an ordinary task, its link dropped, and the
  merge report says so. Links pointing at a connection that does not exist locally are dropped
  the same way, keeping the origin URL in a diary entry "Came from <url>".
- **Restore, reset and import** quiesce the sync engine (`GitHubSyncEngine.RunPausedAsync`) the
  same way `Program.cs` quiesces the reminder engine for `DataProtectionService.Quiesce`.
- **Purge** (`DataPurge.cs`): delete dependent GitHub rows explicitly (no cascades exist), keep a
  link row with `TaskId = NULL, State = Ignored` so the issue is not re-imported.
- **Full reset**: removes Chaos connection data but does not change GitHub CLI authentication.
- **Markdown/Org export** (`PlainTextExport.cs`): add a `Source: <html_url>` line (Markdown) and a
  `:GITHUB_URL:` property (Org) for linked tasks.
- **Backups** contain issue text from private repositories. Say so in `docs/github.md` and
  `docs/data-transfer.md`.

---

## 5. GitHub API usage

Pin `X-GitHub-Api-Version` to the newest version listed in the GitHub REST docs at G0 time and
record it in `docs/dependency-versions.md`. Use direct HTTP with the API version and Accept headers; CLI-owned credentials are captured
in memory and bound to the verified numeric user ID. G0 must verify response headers and status, conditional requests,
pagination, proxy behavior, redirect handling, and REST/GraphQL error classification before the
adapter is designed. If a required property cannot be enforced, record the limitation and amend
the affected rules before proceeding; do not silently claim the original HTTP guarantees.

| Need | Call | Notes |
| --- | --- | --- |
| Who am I | `GET /user` | `id`, `login` |
| Repository candidates | `GET /user/repos` and explicit `GET /repos/{owner}/{repo}` | paginated; candidate visibility alone does not prove issue write permission |
| Authentication setup | user runs `gh auth login --hostname github.com` | outside Chaos; no automatic logout |
| Issues (incremental) | `GET /repos/{o}/{r}/issues?state=all&sort=updated&direction=asc&since=<cursor>&per_page=100` | conditional with `If-None-Match`; skip items with a `pull_request` key; filter client-side |
| Issues (first import) | same with `state=open` (plus a closed pass with `since=now-includeClosedDays`); server-side `labels`/`milestone`/`assignee` params may narrow the first import only | |
| One issue | `GET /repos/{o}/{r}/issues/{n}` | before pushing (fresh compare), and to confirm 404/410 |
| Update issue | `PATCH /repos/{o}/{r}/issues/{n}` with only changed `title`, `body`, `state`, `state_reason` | |
| Labels | `GET /repos/{o}/{r}/labels`; `POST …/issues/{n}/labels`; `DELETE …/issues/{n}/labels/{name}`; `POST /repos/{o}/{r}/labels` (only with decision 15 setting) | element-wise, race-safe |
| Assignees | `POST`/`DELETE /repos/{o}/{r}/issues/{n}/assignees` with the user's login | |
| Milestones | `GET /repos/{o}/{r}/milestones?state=all` | conditional |
| Comments (incremental) | `GET /repos/{o}/{r}/issues/comments?sort=updated&direction=asc&since=<cursor>` | repository-wide; apply to linked issues only |
| Comments (one issue) | `GET /repos/{o}/{r}/issues/{n}/comments` | first import and deletion check (below) |
| Post / edit / delete comment | `POST …/issues/{n}/comments`, `PATCH`/`DELETE /repos/{o}/{r}/issues/comments/{id}` | |
| Closing pull requests | `POST /graphql`: `nodes(ids:[…]) { ... on Issue { closedByPullRequestsReferences(first:10, includeClosedPrs:true) { nodes { id number title url state isDraft merged reviewDecision repository { nameWithOwner } } } } }` | batches of ≤50 issue node IDs; package G9 |

**Pagination** follows the `Link: rel="next"` header, never page arithmetic. Cap a single pass at
a configurable number of pages per repository (default 20 × 100 issues) and continue next pass
from the cursor, so a 10,000-issue first import never blocks the engine for long.

**Deleted comments** do not appear in the incremental feed. When a linked issue's `comments`
count is lower than the number of linked comment rows, or once a day per issue that has comment
links, list that issue's comments and mark missing ones `RemoteDeleted` (diary entry is kept and
labelled "Deleted on GitHub").

**Rate limits.** Read `x-ratelimit-remaining` / `x-ratelimit-reset` on every response. Below 100
remaining, stop non-essential reads until reset. On 403/429 with `retry-after` or a zero
remaining count, pause the connection until the stated time. Requests are strictly sequential per
connection, and mutating requests are spaced at least 1 s apart (GitHub's secondary-limit
guidance). A `304 Not Modified` is free; use conditional requests for every list call.

**Cadence.** First pass 10 s after start; then every 5 minutes; a debounced push 10 s after a
local change to a connected task; immediately on "Sync now"; again when
`NetworkChange.NetworkAvailabilityChanged` reports connectivity. Failures back off per
connection and per link: 1, 2, 4, 8, 16, 30 minutes (cap), reset on success.

---

## 6. Sync algorithm

### 6.1 One pass, per repository subscription

1. **Preflight (read session).** Load connection and subscription; skip if paused, needs
   sign-in, rate limited or backing off. Check `gh` availability and account identity; stop if
   missing, signed out or mismatched. Refresh labels and milestones (conditional).
2. **Fetch remote changes (no transaction).** Issues since `IssuesCursorUtc` (or first import),
   comments since `CommentsCursorUtc`.
3. **Apply inbound, in batches of ≤50 issues per write transaction.** For each issue:
   - No link and the issue matches the filter and is not a pull request → create the task
     (section 6.8), link, checklist items, label tags and comments; `PlacementManaged = 1`.
   - No link and no match → ignore.
   - Link `Ignored` / `Detached` → update nothing but `BaseRemoteUpdatedUtc` (so a later restore
     merges against the newest remote).
   - Link exists and the issue no longer matches → `OutOfScope`; matches again → back to
     `Linked` and merge.
   - Link `Linked` → merge (6.1 step 5) using the fetched issue as *remote*.
   Advance `IssuesCursorUtc` only after the batch commits, to the newest `updated_at` in it.
4. **Apply inbound comments** to linked issues (6.7), advancing `CommentsCursorUtc` the same way.
5. **Merge (pure, in Domain).** For each field compute base *B* (from `BaseJson`), local *L* (the
   task's projection, 6.2) and remote *R*:

   | Case | Result | Push? |
   | --- | --- | --- |
   | L = R | in step | no |
   | L = B, R ≠ B | take R locally | no |
   | R = B, L ≠ B | keep L | yes |
   | L ≠ B, R ≠ B, L ≠ R | take R locally; record L in history as `github.conflict`; set `ConflictNoticeUtc` | no |

   Labels and assignees use set merge: result = R ∪ (L − B) − (B − L). Elements added or removed
   locally are pushed element by element. Status compares as the GitHub pair (open/closed,
   reason) after mapping (6.3).
6. **Push (no transaction).** For dirty links (`ListDirtyLinksAsync`) and the pushes found in
   step 5: re-read the issue (`GET …/issues/{n}`, conditional). If it changed since the merge,
   redo the merge with the fresh remote first. Then send only the changed fields: `PATCH` for
   title/body/state, label adds/removes, assignee add/remove, pending comment posts/edits/deletes.
7. **Record (write transaction).** Re-check the task `Version` read in step 1/3. If it moved, do
   nothing; the link stays dirty and the next pass repeats. Otherwise write the merged task
   fields (with `TaskHistory`, actor `github:<login>`, event `github.pulled` or
   `github.conflict`; no undo entry), the new base (= what GitHub now holds, from the push
   responses), checklist base, comment link states, `LastSyncedUtc`, and finally `LocalDirty`
   computed as "projection ≠ new base". Publish change notices after commit
   (`ChangeCollector`), so open views refresh.
8. **Errors** per link do not stop the pass: record kind, message, attempts, `NextAttemptUtc`, and
   continue with the next link. Connection-level errors (`Unauthorized`, `RateLimited`, `Network`)
   end the pass.

Inbound writes use deterministic `OperationId`s, e.g. a GUID made from SHA-256 of
`github|<issueId>|<remote updated_at>|<field set>`, so a pass interrupted after commit and
repeated is a replay, not a second change.

### 6.2 The local projection and the body ↔ checklist rule

The task's GitHub projection is: title; body; status mapped to (open/closed, reason); label set
(tags that map to repository labels, plus local tags whose name matches an existing label);
assignee set (base assignees adjusted by `LocalAssigneeIntent`).

The body is the hardest field because notes and subtasks both describe it:

1. Start from `Tasks.Notes`.
2. Find top-level task-list items with `IssueBody.ScanChecklist`: lines matching
   `^( {0,3})([-*+]|\d{1,9}[.)]) \[( |x|X)\] (.*)$`, outside fenced code blocks (``` and ~~~,
   tracking fence length), not inside an indented code block, and not nested under another list
   item. Nested items stay plain text (subtasks have one level, `PLAN.md` §1 decision 8).
3. Compare the subtasks with `GitHubChecklistItems` (the base): a subtask whose title or Done
   state changed rewrites its line (only the checkbox character and the text after it change;
   indentation, bullet and line ending are kept); a new subtask is appended as `- [ ] <title>`
   after the last checklist item, or at the end of the body after a blank line if there is none;
   a deleted subtask removes its line. Cancelled subtasks count as unchecked.
4. The result is the projected body *L*.

After the merge, the merged body is written to `Notes` and the subtasks are re-derived from its
checklist: match items to existing subtasks first by `BaseOrdinal` + `BaseText`, then by text
alone, then by position; create subtasks for unmatched items; tombstone subtasks whose items
disappeared (with history). A checked item becomes Done; unchecking a Done subtask reopens it.
Re-derivation never touches subtasks' notes or tags (app-only).

`IssueBody` must be byte-preserving: a body with no checklist change round-trips identically,
including `\r\n`, trailing whitespace, BOM-less start and a missing final newline. GitHub stores
`\r\n` from its web editor; never normalize line endings.

### 6.3 Status mapping

| GitHub | → Chaos |
| --- | --- |
| open | unchanged if the task is not terminal; otherwise the previous nonterminal status from history (`TaskMutationSupport.PreviousNonterminalStatusAsync`), else Not started |
| closed, `completed` (or no reason) | Done |
| closed, `not_planned` or `duplicate` | Cancelled |

| Chaos | → GitHub |
| --- | --- |
| Not started, In progress, Waiting on someone, Needs review | open (`state_reason: reopened` when it was closed) |
| Done | closed, `completed` |
| Cancelled | closed, `not_planned` (a remote `duplicate` stays `duplicate` if the task was already Cancelled) |

The four open statuses are app-only refinements of "open": changing between them is not a GitHub
change. Completing a connected task with open subtasks still asks the existing
complete-all/keep-open question (`OpenSubtaskChoice`); "complete all" checks the checklist items.
Inbound "closed" never silently completes open subtasks; it closes the task with
`OpenSubtaskChoice.KeepOpen` semantics, because the body's checkboxes already say what is checked.

### 6.4 Validation for connected tasks

Enforced in `TaskCommands` / `SubtaskCommands` (application layer, not only the UI), as
validation errors with field paths:

- Title at most 256 characters (verify GitHub's current limit in G0) and a body at most 65,536
  characters (`Notes`, after projection).
- Recurrence cannot be set on a connected task (`RecurrenceCommands`), and a recurring task
  cannot be linked.
- Due date is read-only while the issue's milestone has a due date.
- Subtask titles cannot contain line breaks (they become one checklist line).

### 6.5 Labels and tags

- Label → tag: find a mapped tag by label ID (`GitHubLabelTags`). Otherwise find an existing tag
  by the label's normalized name (Chaos tag normalization, `TagName`), where a `/` in the label
  name means nested segments (`area/ui` → tag `area` › `ui`, the quick-add rule). Otherwise create
  it. Record the mapping.
- Tag → label: a tag added to a connected task maps to a label if `GitHubLabelTags` has it, or if
  the repository has a label whose case-insensitive name equals the tag's full path joined with
  `/`. If neither and `CreateLabelsForTags` is on, create the label (default colour), then add it.
  Otherwise the tag is local only and the task shows it without the GitHub mark.
- Renames are never propagated in either direction (labels are shared by the whole repository;
  tags are the user's). Mappings follow IDs, so both keep working.
- Deleting or merging a tag (`TagCommands`) removes the corresponding labels from connected tasks
  on the next pass, because the projection loses them. Show this consequence in the tag delete
  and merge confirmation when the tag is mapped ("Also removes the label bug from 12 GitHub
  issues").

### 6.6 Milestones and placement

- Without `MilestonesAsProjects`, new tasks go to `TargetContainerId` / `TargetSectionId`.
- With it, an issue in milestone *M* goes to the project mapped in `GitHubMilestoneProjects`,
  created on first need with the milestone title (on a name clash, append ` (<repo name>)`, then
  a number) and the milestone due date as the project deadline. Milestone title and due changes
  update the project only if the project still has the base title/deadline (the user did not edit
  it).
- When an issue's milestone changes, the task moves to the new place only while
  `PlacementManaged = 1`. Any user move (`TaskCommands.MoveAsync`) on a connected task sets
  `PlacementManaged = 0`.
- Milestone due date → task `DueDate` (date part of `due_on` in UTC; date-only, no time or zone).
  Removing the milestone or its due date clears the due date only if it still equals the old
  milestone date.

### 6.7 Comments and the diary

- GitHub comment → diary entry with `EntryAtUtc = created_at`, `RawMarkdown` = comment body, a
  `GitHubCommentLinks` row (`Origin = FromGitHub`). The diary UI shows "@login on GitHub" and a
  link. The author in `DiaryEntries.AuthorProfileId` stays the local profile (the column is a
  required FK), and the GitHub author lives in the link row; document this.
- Remote edit → update the entry text, which records a diary revision (existing revisions). A
  local edit to the same entry in the meantime: GitHub wins, local text kept as a revision.
- Diary entry with "Also post to GitHub" → link row `PendingPost`; the pass posts it and stores
  the comment ID. Editing a posted entry → `PendingEdit` → `PATCH`. Entries from other people's
  comments are read-only in the diary (only the comment author can edit on GitHub).
- Deleting a mirrored entry asks: **Remove from Chaos only** (default; link → `HiddenLocally`, the
  entry is tombstoned and never re-created) or, for the user's own comments, **Delete on GitHub
  too** (`PendingDelete`).
- Diary entries never post retroactively when a task becomes connected; only new entries with the
  box ticked.
- First import brings in at most the newest 100 comments per issue and says "Older comments are
  on GitHub" with a link.

### 6.8 Creating a task from an issue

Through the stores (like imports, `docs/decisions.md` package 12), not the undo recorder: new
task with title, notes = body, status mapped (6.3), due date (6.6), `Priority = Normal`, no
estimate, container/section per 6.6, appended to the end of the container; subtasks from the
checklist; tags from labels; diary from comments; search documents maintained in the same
transaction (`SearchIndexWriter`); one `TaskHistory` row `github.imported` with actor
`github:<login>`. No undo entry. The first import of a repository takes a pre-import backup
(`BackupManager`, reason `pre-import`) when it will create more than 50 tasks, and records the
backup in the subscription so "undo this import" can be explained in `docs/github.md`.

### 6.9 Local deletion, restore and removal

- Task deleted (`DeletedAtUtc` set) → link `Ignored` on the next pass; nothing sent.
- Task restored → link `Linked`, `LocalDirty = 1`; the next pass merges with remote winning
  conflicts, so changes made on GitHub while the task was in the trash arrive.
- Task purged → link keeps `IssueId` with `TaskId = NULL`.
- "Keep as an ordinary task" on one task → link `Detached`; task keeps a diary entry "Came from
  <url>" and the GitHub mark is replaced by "Came from acme/web#12".
- Removing a repository → all its links `Detached` (same note), subscription tombstoned, ETags
  and cursors cleared, tasks untouched.
- Disconnecting → every subscription removed as above and connection tombstoned. Leave GitHub
  CLI authentication untouched; explain that it remains available to other programs.

---

## 7. Sign-in and connection management

`GitHubConnectionCommands` (Application) with `IGitHubApi`, the store, ids and
clock:

1. Explain the external dependency and offer installation and `gh auth login` instructions.
   The user handles sign-in independently; Chaos never starts an interactive login in the
   background or asks the user to paste credentials.
2. On explicit Connect, check `gh` availability, version and authentication, then `GET /user`;
   make no claims about repository write access based solely on repository visibility. Surface
   missing permissions when GitHub reports them.
3. On success create or reactivate the active connection (same `AccountId` → reuse; different account → refuse with "This
   space is connected to @other. Disconnect first."), state Active. After an explicit disconnect,
   a new connection ID is created; old detached links remain historical and cannot block new subscriptions.
4. A missing CLI, failed authorization or credential identity mismatch pauses sync with a clear fix.
   Check the account before each pass and before remote writes. Because external account switches
   may race with those checks, each operation uses the same immutable account-specific credential
   for its identity check and request. The current CLI active-account selection cannot redirect it.
5. `ListReachableRepositoriesAsync` lists candidates with "already added" flags and allows an
   explicit owner/repository lookup; candidates are not promised to be writable.
6. `AddRepositoryAsync(repositoryId, target container/section, filter, milestonesAsProjects)`;
   `UpdateRepositoryAsync` (changing the filter re-evaluates scope on the next pass; changing the
   target only affects new tasks); `RemoveRepositoryAsync`; `PauseAsync`/`ResumeAsync` (connection
   or repository); `UpdateSettingsAsync` (post-diary default, create-labels); `DisconnectAsync`.

All mutating commands use the standard envelope (`OperationId`, `ExpectedVersion`, receipts in the
same transaction; `PLAN.md` §4).

---

## 8. RPC contract

New `[NeoRpcService("github", Version = 1)]` in `src/Chaos.Desktop/Services/GitHubRpcService.cs`,
registered in `Program.cs` next to the others, every request/response type added to
`AppJsonContext`. DTO rules from `PLAN.md` §13: string IDs and instants, explicit enum strings,
bounded arrays, errors through `TaskErrorResponse`.

| Method | Purpose |
| --- | --- |
| `status` | connection state, account login, last sync, rate-limit pause, error; repositories with state, counts, last sync, error |
| `connect` | check GitHub CLI and bind the active account |
| `reachableRepositories` | repository candidates and explicit lookup |
| `addRepository`, `updateRepository`, `removeRepository` | subscriptions |
| `pause`, `resume` | connection or one repository |
| `updateSettings` | post-diary default, create labels |
| `syncNow` | asks the engine for a pass; returns immediately |
| `disconnect` | section 6.9 |
| `links` | batched `taskIds[] (≤500)` → compact source info for task rows: repo, number, URL, sync state |
| `taskDetail` | link state, last in step, error, conflict notice, labels, assignees, milestone, author, issue type, pull requests |
| `assignMe`, `unassignMe` | sets `LocalAssigneeIntent`, marks dirty |
| `retryTask` | clears backoff for one link |
| `dismissConflict` | clears `ConflictNoticeUtc` |
| `keepAsOrdinaryTask` | detaches one task |
| `repositoryTasks` | paged tasks of one subscription with filters `assignedToMe`, `open`, `label` |

Existing RPC changes: diary `add` gains `postToGitHub: boolean | null` (null = connection
default); diary `delete` gains `alsoDeleteOnGitHub: boolean`; task and subtask responses do **not**
grow (use `github.links` for rows, to keep every existing list untouched).

The renderer never receives or handles a credential. Raw GitHub error bodies are never exposed. External links
opened from GitHub UI go through the same `isSafeExternalUrl` check.

---

## 9. User interface

Follow the existing components (`@/components/common`, `ui/*`, lucide icons), `dir="auto"` on user
text, keyboard alternatives and live regions (`PLAN.md` §13).

- **Settings → Connections → GitHub** (`GitHubSettings.tsx`; add a "Connections" link to
  `Settings.tsx` alongside the existing links). States:
  - *Not connected*: one paragraph on what connecting does and what leaves the computer, and a
    **Connect GitHub** button.
  - *Setup needed*: install/sign-in instructions, the detected account when available, and a
    button to recheck. Explain that the CLI may be shared with other tools.
  - *Connected*: account, last synced, Sync now, Pause/Resume, settings toggles, repository list
    (name, private badge, target, filter summary, counts, state, error with a fix action), **Add
    repository** (picker from `reachableRepositories`, target container select, filter form,
    milestones-as-projects toggle, include-closed days), Disconnect (confirmation explaining
    tasks are kept and GitHub CLI stays signed in).
  - *Needs sign-in*, *CLI unavailable*, or *account changed*: a banner with a fix and recheck;
    nothing else is lost.
- **Sidebar**: when connected, a "GitHub" group lists repositories; each opens a view of that
  repository's tasks with filter chips Open / Assigned to me / label. Reuse `QueryView` layout.
- **Task row**: a GitHub mark (`aria-label="From GitHub acme/web#12"`) and a small sync-state dot
  for *waiting to send* / *error*.
- **Task detail** (`GitHubTask.tsx`, mounted in the existing detail pane): `acme/web#12` link,
  "In step 2 minutes ago" or the plain error with Retry, conflict notice with Dismiss and a link
  to history, labels, assignees with Assign to me / Unassign me, milestone, author, issue type,
  linked pull requests (G9), and **Keep as an ordinary task**. Title, notes, status, tags and
  subtasks stay editable as normal, with the 256-character title counter on connected tasks. Due
  date shows "From milestone 2.1" and is read-only when 6.6 says so. Recurrence controls are
  hidden with an explanation.
- **Diary composer** on connected tasks: "Also post to GitHub" checkbox; GitHub-origin entries
  show "@login on GitHub" with a link and are read-only unless they are the user's own.
- **History**: `github.pulled`, `github.imported` and `github.conflict` events render with the
  GitHub actor; conflicts show "GitHub's change replaced yours" with the replaced value.
- **Content from GitHub is untrusted.** It renders through the existing Markdown renderer (no raw
  HTML, images never load, only http/https/mailto links via the opener). `chaos:` links inside
  issue bodies resolve only to local tasks and pads, as today. Avatars are not loaded.

---

## 10. Security and privacy

- Credentials are managed by `gh`; Chaos never runs `gh auth token`, passes credentials via
  arguments or environment, or logs process output containing credentials. Document `gh`'s possible
  plaintext credential-store fallback and its independent authorization lifecycle.
- Invoke a discovered, trusted `gh` executable directly, never through a shell; use structured
  arguments and stdin for request bodies, bounded stdout/stderr, timeouts, cancellation and
  child-process cleanup. Never log issue text, request bodies or untrusted process output.
- Network: HTTPS github.com/API only is the target, including child processes. G0 must verify
  what `gh` can enforce for redirects, proxy and host selection; if it cannot enforce the
  required boundary, record the difference and resolve it before implementation proceeds.
- No GitHub request without explicit setup or a connection; amend `docs/acceptance.md` step 16 to "No connections
  from Chaos, its child processes or its WebView unless the user explicitly starts connection
  setup or GitHub is connected; during setup/connection only the documented GitHub endpoints
  through `gh`, never from the WebView".
- Renderer CSP is unchanged: the WebView never talks to GitHub.
- Logs and `GitHubSyncRuns` hold counts and error kinds, not issue text.
- `docs/github.md` states what is stored locally (issue text and comments of added repositories,
  including private ones), that backups and exports contain it, and how to disconnect.

---

## 11. Testing strategy

TUnit everywhere, following `PLAN.md` §16. No live GitHub in automated tests.

- **Domain** (`tests/Chaos.Domain.Tests/GitHub/`): `IssueBody` scan/rewrite fixtures (CRLF/LF,
  fences of different lengths, ordered and unordered lists, nested items ignored, indented code,
  emoji and combining marks, Arabic text, missing final newline, duplicate item texts, 500 items),
  byte-identical round trip when nothing changed; three-way merge truth table per field; set
  merge; status mapping both ways including `duplicate`; label name ↔ nested tag path.
- **Infrastructure — client** (`tests/Chaos.Infrastructure.Tests/GitHub/`): a fake process
  runner checks executable discovery, structured arguments and stdin, API version header, output
  limits, cancellation and timeout cleanup, missing/unsupported `gh`, signed-out and changed
  accounts, and sanitization. Exercise actual `gh` behavior established in G0 for pagination,
  ETags/304, rate-limit headers, redirects and error classification; never expose credentials.
- **Infrastructure — sync on real SQLite** with `FakeGitHub` (an in-memory GitHub implementing the
  application API: issues, comments, labels, milestones, ETags, `updated_at`, error injection).
  Mandatory cases:

  | Area | Cases |
  | --- | --- |
  | Import | first import with filters; >1 page; page cap continues next pass; pull requests ignored; closed-within-N-days; checklist → subtasks; labels → nested tags; comments → diary; pre-import backup over 50 |
  | Inbound | title/body/state/labels/milestone change; issue leaves filter → OutOfScope; returns → same task; 404/410 → Unavailable; transfer; remote comment edit/delete |
  | Outbound | each field; subtask add/rename/check/delete rewrites only its line; tag add maps to existing label; unmapped tag stays local; create-labels setting; self assign/unassign; diary post/edit/delete choices |
  | Merge | each truth-table row; set merge with concurrent add/remove; conflict writes `github.conflict` history and notice; remote changed between merge and push → re-merge |
  | Robustness | offline edits then reconnect; crash after push before record (replay with same OperationId; no duplicate comment — post is guarded by looking for the pending entry's marker before re-posting); task edited during a pass → version check abandons, stays dirty; trigger dirty flag cleared by the engine's own write; rate-limit pause and resume; Needs sign-in on 401 |
  | Local lifecycle | delete → Ignored, nothing sent; restore → re-merge; purge → no re-import; keep as ordinary task; remove repository; disconnect leaves CLI authentication untouched |
  | Data protection | JSON export excludes `GitHubSyncRuns` and resets machine columns; restore verifies CLI and account; merge import drops colliding links; purge removes dependents; Markdown/Org export carries the URL; restore quiesces the engine |
  | Validation | 256-char title; 65,536-char body; recurrence refused; milestone due read-only; multi-line subtask title refused |

  Avoiding a duplicate comment after a crash between `POST` and the record: before posting, list
  the issue's newest comments by the signed-in user and match an invisible marker
  `<!-- chaos:<diaryEntryId> -->` appended to posted bodies. Hidden HTML comments are not shown
  on GitHub. Record this in `docs/decisions.md`.
- **Application** tests with fakes for the merge orchestration and connection commands
  (CLI setup states, account mismatch refusal, receipts).
- **Desktop RPC** (`tests/Chaos.Desktop.Tests/Rpc/GitHubRpcTests.cs`): DTO round trips, that no
  response type has a token-shaped field (reflect over the response records), `links` batching
  cap, errors mapped.
- **Engine** with a fake `TimeProvider`: no timer without a connection, non-overlapping passes,
  debounce after local change, backoff schedule, `RunPausedAsync` blocks passes during restore.
- **Frontend** (`GitHubSettings.test.tsx`, `GitHubTask.test.tsx`): each settings state, the
  CLI setup flow with a fake API, repository add form validation, task panel states, diary post
  checkbox default, read-only due date, keyboard path through setup.
- **Manual acceptance** (append to `docs/acceptance.md`): against a throwaway repository on a real
  account: connect, add repo, import, edit both sides, conflict, offline edit then reconnect,
  comment both ways, pause, disconnect without changing CLI login, account switch and revoked
  authorization → stopped sync, network watch includes Chaos and its `gh` child processes.

---

## 12. Ordered work packages

Each package: read the listed files, implement, run the gate, update the checkbox and a dated
progress note with real results, and add decisions to `docs/decisions.md`.

### [ ] G0 — Prerequisites and API spike

**Depends on:** nothing. **Files:** `docs/dependency-versions.md`, `docs/decisions.md`, a throwaway
console or test project outside `src/` (deleted at the end), no product code.

1. Record the supported minimum `gh` version, executable discovery strategy, authentication
   status and active-account detection, and behavior when missing, signed out, expired or switched.
   Confirm documentation for credential storage and organization permissions. Never print tokens.
2. With a throwaway repository and `gh api`, verify REST and GraphQL, response headers and status,
   304/ETags, pagination, rate limits, error and redirect behavior, stdout/stderr, output bounds,
   timeouts and cancellation. Establish a safe way to bind each remote write to the connected
   account despite external account switches; if that cannot be guaranteed with `gh`, stop and
   document the limitation for a design decision.
3. Confirm the REST API version, issue title/body limits, `state_reason`, `since` semantics on
   both list endpoints, and `closedByPullRequestsReferences` with the CLI authorization.
4. Prove the installed MSIX can discover and run external `gh`, use its existing authentication,
   and reach GitHub through the system proxy. Verify actual hosts and redirect behavior.
   `packaging/AppxManifest.xml` declares only `runFullTrust` and `unvirtualizedResources`; a
   full-trust process normally needs no `internetClient` capability. Verify that, and add the
   capability only if the test shows it is required.

**Exit:** every item above recorded with evidence; decisions 3 and 10 confirmed or amended.

**Progress (2026-11-11):** User chose GitHub CLI as an external dependency, replacing the earlier
token-entry decision. GitHub documents REST version `2026-03-10`; local `gh auth status` showed
an active github.com account. CLI capability, account-binding and installed-MSIX checks remain
outstanding; no G0 exit criterion has been claimed as met.

**Progress (2026-11-11, account-binding investigation):** Installed `gh` is 2.103.3 at
`C:\Program Files\GitHub CLI\gh.exe`. Its documented `api` interface has no account selector;
`auth switch` changes the active account used for requests. A separate identity check before a
write cannot prevent an external switch between processes. The CLI's documented per-user token
workaround conflicts with the no-credential-handling requirement. G0 is blocked pending a design
decision; this is an interface analysis, not a completed live race test. The remaining live API,
network-boundary and installed-MSIX checks have not been completed. Evidence and the blocker are
recorded in `docs/decisions.md`.

**Progress (2026-11-11, guard experiment):** Added a standalone synthetic-credential Node
prototype in `spikes/github-account-guard`. Eight loopback tests pass, covering an account switch
between identity check and write, mismatches, failed identity checks, revocation, redirect refusal,
and destination validation. This exercises the proposed snapshot invariant only; it does not test
`gh`, real authentication, TLS/proxy behavior, GitHub, or installed MSIX. G0 remains incomplete. `eng/verify.ps1` failed in the Release build because a running Chaos.Desktop process locked output DLLs; `npm test` reported a summary test failure and was stopped after it stopped producing output.
### [x] G1 — Pure domain: body, mapping and merge

**Depends on:** no network for implementation; G0 (limits) for exit. **Files:** `src/Chaos.Domain/GitHub/*`,
`tests/Chaos.Domain.Tests/GitHub/*`. Read `Domain/Tags/TagName.cs`,
`Domain/Tasks/TaskItemStatus.cs`, `Domain/Validation/*`.

Implement `IssueBody` (scan, rewrite from subtask deltas, re-derive), `IssueStateMapping`,
`LabelNames`, `IssueProjection` and `SyncMerge` exactly as section 6, BCL only. **Tests:** the
domain list in section 11. **Exit:** all domain tests pass; a fuzz-style test over generated
bodies proves "no subtask change → identical bytes".

**Progress (2026-11-11):** Began the credential-independent domain work with status mapping and
scalar/set three-way merge. `dotnet test --project tests/Chaos.Domain.Tests/Chaos.Domain.Tests.csproj
-c Release --output Minimal` passed 171 tests; Slopwatch reported 0 issues. This is a partial
stage: checklist scanning/rewrite, label rules, projection, required fuzz coverage and the full
package gate remain outstanding. G0 limits remain unverified.
**Progress (2026-11-11, domain continuation):** Added checklist scanning, byte-preserving
rewrites, reconciliation, label paths and an issue projection. Domain suite: 182 passed;
`eng/verify.ps1`: Release build clean, 509 tests passed, Slopwatch 0 findings. `npm test` was stopped after the reminders test process stopped producing output; no complete frontend result. G1 remains open
pending the remaining edge-case review and G0 verification of limits. A live account-specific
credential lookup was rejected by automatic approval review (reason: blocked by policy); it was
not executed. Two-account race and installed-MSIX checks are still outstanding.
**Progress (2026-11-11, G1 review):** Fixed checklist sibling indentation and fence boundaries,
rejected backticks in backtick-fence info strings, and implemented projection equality/hash codes
using set contents. Added four regression tests; all 186 domain tests pass. Slopwatch: 0 findings.
A clean domain-test Release rebuild passed with 0 warnings/errors. The full `eng/verify.ps1`
gate failed because running Chaos.Desktop PID 3388 locked output DLLs. Full frontend runs in
parallel and sequential modes stalled in Reminders.test.tsx and were stopped; the isolated file
passed 6 tests. G0/G1 remain open; no later package has been marked complete. Removed the
superseded GitHub App revision from the plan header; the current CLI/direct-API decision stands.
**Progress (2026-11-11, projection merge):** Implemented complete issue-projection merging,
returning scalar pushes, element-wise label/assignee deltas, and exact losing local values for
conflict history. Added independent-edit, concurrent-set, empty-body and state-conflict tests.
`eng/verify.ps1` passed: clean Release build, 516 tests passed, Slopwatch 0 issues. `npm test`
passed all 79 tests. Domain suite now has 189 tests. G1 remains open for G0 limit verification;
G2 has not started. Moving the outstanding live G0 checks to release gates is awaiting the
user's decision; no gate has been waived.
**Gate decision (2026-11-11):** User approved moving outstanding live G0 checks to release gates. Offline-tested implementation may proceed through G2 and later packages. No live check is marked passed by this decision.

**Progress (2026-11-11, nested-fence regression):** Preserved list context across fenced code
nested under a parent list, so subsequent nested checkboxes remain notes rather than becoming
top-level subtasks. All 190 domain tests pass; Slopwatch reports 0 issues.
### [x] G2 — Storage and data protection

**Depends on:** G1. **Files:** `Migrations/014_github.sql`, `Sqlite/SqliteGitHubStore.cs`,
`Abstractions/DataStores.cs`, `Contracts/GitHubRecords.cs`, `History/ChangeRecording.cs`
(pass-through), `SqliteApplicationStore.cs` (session wiring), `Bootstrap/StorageBootstrap.cs`
(schema ceiling), `Transfer/JsonArchive.cs`, `Transfer/ArchiveMerge.cs`,
`Transfer/PlainTextExport.cs`, `DataProtection/DataPurge.cs`, `docs/data-transfer.md`.

Create the schema of section 4 (without migration 015), the store, triggers and data protection
changes. **Tests:** migration applies on a Phase 1 database; every constraint and partial unique
index; each trigger marks dirty and only for `State = Linked`; engine-style write clears it;
archive exclusions and machine-column resets; merge-import collision; purge; export URL lines;
existing migration tests updated for the new ceiling. **Exit:** `eng/verify.ps1` green; a Phase 1
database upgrades with a pre-migration backup.

**Progress (2026-11-11, G2 schema stage):** Added migration 014 with eight GitHub tables,
composite/partial uniqueness and dirty triggers; raised the schema ceiling to 14. JSON archives
exclude sync diagnostics and reset device fields; purge removes dependent mappings and retains
ignored issue identities; referenced destinations are retained. Markdown/Org exports include
issue URLs. File-backed tests cover migration from 13, constraints, dirty state, engine clearing,
archive resets, text exports and linked-task purge. `eng/verify.ps1` passed: clean Release build,
536 tests, Slopwatch 0 findings. Final infrastructure rerun after the purge-preview adjustment:
237 passed. `npm test` stalled again at the reminders file and was stopped; no frontend pass is
claimed for this batch. G2 is not complete: typed store/session wiring, merge-import rules,
additional constraints/trigger and pre-migration-backup coverage remain. No connection UI exists.
**Progress (2026-11-11, G2 completion):** Added typed records and transaction-scoped SQLite
stores for every GitHub table, session wiring and recorder pass-through, optimistic updates,
bounded dirty/retry/task-link queries, mapping upserts and 200-run retention. JSON merge keeps
links only under a matching existing local connection/repository without identity collisions;
otherwise it warns and preserves origin in the diary. Checklist/comment mappings travel with
kept links, retaining comment diary IDs for crash-safe posting. Added real-file store/merge
round trips, concurrency/retention tests and a schema-13 upgrade test that verifies the safety
backup predates migration 014. Final `eng/verify.ps1`: 544 passed, clean Release build, Slopwatch
0 findings. Frontend: all 79 passed with sequential file execution; a reminder test now waits
for the post-log action to become enabled. G1/G2 offline implementation gates are complete;
G0 live API limits, credential/account-switch and installed-MSIX checks remain release gates.
### [x] G3 — GitHub CLI authentication and direct API client

**Depends on:** G0. Can run in parallel with G1–G2 (different files). **Files:**
`Abstractions/GitHubApi.cs`, `Infrastructure/GitHub/*`, process-runner and API test fakes
(`tests/Chaos.Infrastructure.Tests/Testing/FakeGitHub.cs`).

Implement section 5 transport against the G0-verified CLI behavior: structured execution,
conditional requests, pagination, rate limiting, error classification, account checks, GraphQL,
size caps and sanitization. No sync logic. **Tests:** client list in section 11. **Exit:** client
tests green; no credentials or issue text in exception strings or captured trace output.

**Progress (2026-11-12, G3 completion):** Added account-bound CLI credential retrieval and direct
REST/GraphQL calls, strict destination validation, bounded processes and responses, pagination,
conditional reads, rate limits and sanitized failures. Fake-process and HTTP tests cover account
switching without reading live credentials. Final `eng/verify.ps1`: clean Release build,
585 passed, Slopwatch 0 findings. Frontend remains at 79 passing tests (unchanged in G3).
Live CLI credentials, permissions and installed-MSIX checks remain release gates.

### [x] G4 — Connect, repositories and settings UI

**Depends on:** G2, G3. **Files:** `Application/GitHub/GitHubConnectionCommands.cs`,
`GitHubQueries.cs`, `Desktop/Services/GitHubRpcService.cs` (status, sign-in, repositories,
settings, pause/resume, disconnect), `Program.cs`, `AppJsonContext`, `frontend/src/GitHubSettings.tsx`,
`Settings.tsx`, tests.

Section 7 and the Settings part of section 9. Repository add/remove works but no sync runs yet;
say so in the UI only by not showing sync counts (no fake "synced" state). **Tests:** CLI setup
states with the fake API, account mismatch, receipts, RPC round trips, frontend states.
**Exit:** in a real window, connect using `gh`, see repositories, add one; restart keeps the
connection; Disconnect leaves CLI authentication unchanged.

**Progress (2026-11-12, G4):** Implemented local-only status, explicit CLI connection and
repository discovery/lookup, validated subscription filters and destinations, settings, pause,
remove and disconnect. Operations use receipts and versions; network reads stay outside write
transactions. Removing/disconnecting preserves tasks and diary text, clears sync mappings and
adds origin notes across bounded link pages. RPC uses string numeric IDs; Settings has setup
instructions, repository options and confirmations. Locked restore and full Release build passed
using `-p:OutputPath=bin/GitHubVerification/` because the user's running app locks the normal
output; all 600 .NET tests passed, Slopwatch 0 findings. Frontend production build and all 84 tests passed; a focused reminder rerun passed all 6 tests.
Live Connect and installed-MSIX acceptance remain release gates; no sync engine exists yet.

### [ ] G5 — Inbound sync and "Sync now"

**Depends on:** G4. **Files:** `Application/GitHub/GitHubSyncCommands.cs` (pull half),
`Desktop/Platform/GitHubSyncEngine.cs` (manual pass only: `syncNow`), task creation via stores,
`SearchIndexWriter` use, `BackupManager` pre-import backup.

Steps 1–5 and 7 of section 6.1 for inbound only, plus 6.3, 6.5 (label → tag), 6.6, 6.7 (comment →
diary), 6.8, and the Out of scope / Unavailable rules. Local edits on connected tasks are allowed
but not sent yet; the task detail must not claim they were sent (show "Not sent to GitHub yet").
**Tests:** Import and Inbound rows of section 11. **Exit:** importing a real test repository via
Sync now creates the expected tasks, subtasks, tags and diary; an unchanged sync makes no writes and repeating the same conditional list request uses 304 responses; account-verification reads may return 200.

**Progress (2026-11-12, G5 inbound stage):** Added bounded issue-application transactions,
versioned remote baselines, filter matching, import/history without undo, field merges and
conflict snapshots, checklist/subtask reconciliation, label tags, milestone projects and due
dates, scope exit/return and deleted-task restoration. Personal task fields survive inbound
changes. Comment ingestion keeps the newest 100 new comments, revisions on remote edits,
hidden/deleted state and diary text after remote deletion. Blank/overlong checklist titles and
labels that cannot form a valid local tag path remain in notes/baselines; they never become
accidental remote removals. Aggregate versions advance for tag-only inbound changes.
Migration 015 adds device-local query/page checkpoints, excluded from archives and cleared on
restore or subscription scope changes. Schema 13/14 upgrade safety backups and checkpoint
restart/stale-version/archive/restore tests pass. Full Release verification: 617 tests passed,
Slopwatch 0 findings. G9's pull-request migration is consequently 016.
This is a local application component, not a completed G5: durable pagination checkpoints,
fetch orchestration, pre-import backups and manual Sync now remain to be wired and tested.
Final separate-output Release build passed with no warnings/errors; all 613 .NET tests passed,
Slopwatch 0 findings. Frontend remains at 84 passing tests, unchanged by this stage.
### [ ] G6 — Outbound sync, merge and conflicts

**Depends on:** G5. **Files:** `GitHubSyncCommands.cs` (push half), `GitHubTaskCommands.cs`,
`TaskCommands.cs`, `SubtaskCommands.cs`, `RecurrenceCommands.cs`, `DiaryCommands.cs`,
`TagCommands.cs` (delete/merge consequence text), diary RPC, `TaskJournal.tsx`.

Section 6.1 steps 5–8 both ways, 6.2 rewrite, 6.4 validation, 6.5 tag → label, 6.7 posting
choices, crash-safe comment posting marker, self-assign, `keepAsOrdinaryTask`, local delete /
restore / purge rules (6.9). **Tests:** Outbound, Merge, Robustness, Local lifecycle and
Validation rows. **Exit:** with the test repository, every mapped field round-trips both ways via
Sync now, a forced conflict shows GitHub's value plus the local value in history, and an edit made
offline is sent after reconnecting.

### [ ] G7 — Background engine and status

**Depends on:** G6. **Files:** `GitHubSyncEngine.cs`, `Program.cs` (construction, disposal order,
quiesce wiring alongside `protection.Quiesce`), `DataRpcService` paths that restore/reset,
`GitHubRpcService.status`, `GitHubSyncRuns` trimming.

Cadence, debounce, backoff, network-change trigger, rate-limit pause, Needs sign-in, per-link
retry, quiescing during restore/reset/import, startup with a restored database and missing or
mismatched GitHub CLI authentication.
**Tests:** Engine list in section 11 plus restore during a pass. **Exit:** left running for an
hour against the test repository with remote and local edits, everything converges; restore while
connected leaves no half-written pass.

### [ ] G8 — Task-level UI and repository views

**Depends on:** G6 (G7 for live status). **Files:** `frontend/src/GitHubTask.tsx`, `App.tsx`
(sidebar group, row mark, detail mount, `View` type), `QueryView.tsx` (repository view and chips),
`Reminders.tsx`/`MyDay.tsx` only if rows there need the mark, `github.links` / `taskDetail` /
`repositoryTasks` RPC, tests.

Section 9 except pull requests. **Tests:** frontend states, keyboard and screen-reader labels,
`dir="auto"` on GitHub text. **Exit:** a connected task can be understood and fixed (error →
Retry, conflict → Dismiss, Keep as ordinary task) using only the keyboard.

### [ ] G9 — Linked pull requests

**Depends on:** G7. **Files:** `Migrations/016_github_pull_requests.sql`, store methods,
`GitHubCliClient` GraphQL query, sync pass step (after inbound, only for issues updated this pass
plus a daily refresh of open PR links), `GitHubTask.tsx` list.

**Tests:** GraphQL batching ≤50 IDs, mapping of state/draft/merged/review decision, removal of a
PR that no longer references the issue, GraphQL errors classified. **Exit:** a PR with
"Fixes #n" shows on the task with its state and review decision, updating as it merges.

### [ ] G10 — Documentation and acceptance

**Depends on:** G1–G9. **Files:** `docs/github.md` (new), `docs/data-transfer.md`,
`docs/acceptance.md`, `docs/desktop-support.md`, `docs/decisions.md`, `README.md` (one
"Connect GitHub" paragraph), this file's checkboxes.

Write user-facing behaviour, limits (one level of checklist, self-assign only, polling cadence,
newest 100 comments on import), privacy, disconnect and revoke steps, and the manual acceptance
run of section 11 with results. **Exit:** the acceptance run passes on an installed MSIX, and every
package above has an honest progress note.

---

## 13. Open questions for the product owner

- [ ] Ship the GitHub connection for the Personal space before Phase 3 sharing (decision 1)? The
      Release plan in `SPEC.md` would need a note.
- [x] Authentication: use externally installed GitHub CLI (user decision, 2026-11-11).
- [ ] Default for "Also post to GitHub" on diary entries of connected tasks (plan: on).
- [ ] Should Chaos create GitHub labels for new tags by default (plan: off)?
- [ ] Should a local task be sendable to GitHub as a new issue? The spec does not ask for it, so
      this plan leaves it out.
- [ ] GitHub Enterprise Server support, and when.
- [ ] GitHub sub-issues and Projects (v2) fields: out of scope here; which, if any, matter for
      custom views in Phase 2/3?

## 14. Explicitly out of scope

Shared-space ownership and member roles; matching other GitHub users to Chaos members; assigning
anyone but yourself; webhooks or any server component; creating issues from tasks; GitHub
Projects (v2), sub-issues, reactions, issue templates and attachments upload; GitHub Enterprise
Server UI; custom views and alerts over GitHub fields (they arrive with custom views); agents
acting through the connection; other connections (Jira, GitLab, Org folders).

### Superseded G0 candidate: account-bound GitHub CLI fork

Historical candidate only. The user subsequently authorized existing-CLI credential retrieval
into memory and direct API calls (see the current decision at the top). No fork is required,
and the old stop instructions below do not apply; live G0 checks are release gates.

The stock `gh api` command cannot satisfy the account-binding requirement. Evaluate a separately
installed, version-pinned fork of GitHub CLI with a guarded API command. The fork, not Chaos,
resolves and owns credentials. For each invocation it resolves authentication once, holds that
credential inside the CLI process, calls `GET /user` with it, compares the returned numeric ID
with the expected connection ID, and sends the requested API call using that same credential.
A mismatch, missing identity, failed check, refresh requirement or ambiguous result fails closed.
A credential expiring or being revoked between the check and the call must fail rather than
trigger a lookup of another account. Apply this guard to reads as well as writes. Chaos supplies
only the expected numeric ID and API request, never a credential. Setup may use an identity-only
operation to select an account, without sending issue content.

The guarded client must disable automatic redirects (or validate each destination before any
request), prohibit non-HTTPS and hosts other than `api.github.com`, and sanitize inherited `gh`
authentication, host, debug and proxy settings according to the eventual verified network policy.
System proxy support requires its own explicit design: a proxy can see destinations, and TLS
interception changes the trust boundary. No issue data or credentials may appear in CLI diagnostics.
Chaos must discover and verify the dedicated binary/version rather than silently falling back to
stock `gh`. Its CLI remains responsible for credential storage and refresh; disconnect does not
sign it out. The user must explicitly install and authenticate this separate CLI. This changes the
original existing-CLI setup assumption and requires documenting the fork's source, release,
updates and maintenance before shipping.

This is a **candidate design, not a verified implementation or a completed G0**. Before G0 exits,
review and build the fork independently, prove that every guarded HTTP request uses the exact same
authentication snapshot as its successful identity check, and test account switches, token
refresh/revocation and keyring failure between check and request with two throwaway accounts.
Test redirect, proxy and destination enforcement, including installed MSIX execution. All remaining
G0 evidence and the normal gates still apply. Do not begin G1 or a product adapter until these
checks pass; if the fork cannot be maintained and verified, select a different authentication
design rather than weakening the guarantee. A `GH_CONFIG_DIR` alone is insufficient: the CLI's
keyring lookup has an unkeyed fallback. See
[CLI account switching](https://cli.github.com/manual/gh_auth_switch),
[gh api](https://cli.github.com/manual/gh_api), and
[CLI keyring fallback analysis](https://github.com/cli/cli/issues/14370).
