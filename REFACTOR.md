# Architecture and maintainability review

Reviewed 2026-11-19 on `feature/p12-online-service` (HEAD `732db3a`). Covers all of `src/` (about 61k lines of C#,
21k of TypeScript/JSX) and `tests/`. The findings on the server, the sync/storage layer and the frontends were
reviewed in depth; bugs 1–5 were checked against the code.

**Summary.** The overall design is sound, but the branch is not: HEAD does not build, two tests fail, and
nothing runs CI on pushes or pull requests. Several background loops can stop for good on an unexpected
exception, and the server has a blob race that can leave a blob unreadable.

## Status (2026-11-19)

Done on `feature/p12-online-service`: build break, failing GitHub tests, CI (including lint), bugs 1–18, server options validation,
the shared LISTEN connection, `Rpc.Run`/`RpcParse`, the explicit pause pipeline, the undo-coverage guard, pinned frontend
dependencies, ESLint (both apps), `useRpcQuery` and `useAutoClear`, the server's `CallerIdentity`, `ApiErrors`, `CalendarCacheService`,
`DeviceRegistration` and `AccountDeletion`, `IDeviceInfo`, `IUploadStaging`/`IThumbnailCache`, lazy session stores, SQL built from
constants and parameters in `AttachmentTransfer`, a loud preview backend, and these file splits: `SqliteStores.cs`, `SyncApplier.cs`,
`MyWeekCommands.cs`, and the records of `PageCommands`/`PadCommands`/`TagCommands`. New tests cover a malformed pull change,
attachment download faults and cancellation, and the sync loop surviving unexpected exceptions.

The desktop composition root is now `src/Chaos.Desktop/DesktopComposition.cs` (shared clock and ID generator, one owned-disposable stack, workers started last, one ordered pause pipeline, `Availability` passed through the constructor; `ActivityNotifier` is still set once there because storage is built first). `Program.cs` only handles the single-instance guard, the shell and the window; the JSON context moved to `AppJsonContext.cs`.

Also done: `session.Connection()` (one `SqliteSessionExtensions` accessor) replaces the 47 `(SqliteDataSession)` casts, and `AuthSetup` loads its key certificates once for both OpenIddict builders. Several small items below were already finished earlier and are stale (catalog dictionary lookup, `SyncEngine` gate, lazy session stores, cached undo wrappers, security-header extension, side effects out of `AttachmentCommands`).

Still open: narrower session
interfaces, `SELECT *` mappers, one source for `SyncKinds`/partial-index predicates, splitting the command classes themselves (queries
from commands), splitting `App.tsx` beyond `useAutoClear`, a typed Chaos.Web REST client, the shared markdown package, a typed preview
backend, Prettier/Biome, and the server test gaps (they need PostgreSQL).

## 0. Build and test status

| Test project | Result |
|---|---|
| Release build | **Fails**: `tests/Chaos.Application.Tests/Testing/Fakes.cs:186` (`FakeNotificationJobStore`) is missing the 5 `INotificationJobStore` members added in `f1be064` |
| Chaos.Domain.Tests | 273 passed |
| Chaos.Application.Tests | 114 passed, but against a stale DLL because the project doesn't compile |
| Chaos.Infrastructure.Tests | **2 of 708 fail** (see below) |
| Chaos.Sync.Tests | 28 passed |
| Chaos.Desktop.Tests | 97 passed |
| Chaos.Server.Tests | Not run (needs PostgreSQL) |

Failing tests:
- `GitHubInboundCommandsTests.InvalidOrStaleBatch_CommitsNothing` (`GitHubInboundCommandsTests.cs:233`): it
  expects an `AppException` for a 257-character issue title, and none is thrown.
- `GitHubOutboundCommandsTests.IssueFeed_InvalidLinkedIssue_DoesNotRollBackOtherIssues`
  (`GitHubOutboundCommandsTests.cs:668`): it expects `LastErrorKind == Validation`.

**No CI on push or PR.** The only workflow, `.github/workflows/release-windows.yml`, runs on `v*` tags.
That is how the build break and the failing tests reached the branch.

## 1. What's good (keep it)

- **The layers point the right way.** Domain → Application → Infrastructure → Desktop. The Server depends only
  on `Chaos.Sync` and `Chaos.ServiceDefaults`, and nothing in Domain uses I/O.
- **Writes all follow one model.** Each change is one transaction with an idempotent `OperationId`
  (`TaskMutationSupport.ReplayOrRunAsync`, used in 33 command files). History and undo are recorded in the same
  transaction through `RecordingDataSession`.
- **RPC types are generated, not hand-written.** NeoAstra generates the TypeScript types from the C# contracts,
  and the build type-checks the frontend with TypeScript `strict` on.
- **Client sync is driven by one catalog.** `SyncCatalog` generates the triggers and is checked against the
  migrated schema. Each change applies inside its own savepoint, with parking, and backoff has jitter.
- **The server is careful about auth and migrations.** PKCE, refresh-token rotation, device and account checks
  on every call, an advisory-lock migration gate with a no-pending-model-changes test, and blob encryption per
  chunk with AES-GCM.

## 2. Bugs and risks

| # | Sev | Location | Problem | Fix |
|---|---|---|---|---|
| 1 | High | `src/Chaos.Infrastructure/Sync/SyncEngine.cs:192` | `RunAsync` only catches cancellation, and `SyncNowAsync` only catches `ChaosServiceException`. A `JsonException`, `KeyNotFoundException`, `AppException` or `IOException` (the response body read at `ChaosServiceClient.cs:335` sits outside the error wrapping) ends the loop for good, and nobody sees it because the task is never awaited. | Catch any non-cancellation exception in `RunAsync`, report it as a problem and back off. Wrap the body read in `CallAsync`. |
| 2 | High | `src/Chaos.Infrastructure/Sync/SyncApplier.cs:45`, `:208` | A malformed change (a `FormatException` from `ParseChange`/`ParseKey`, or an unclassified `SqliteException`) rolls back the whole chunk. The cursor never advances, so sync is blocked permanently. | Parse and apply each change on its own. On any per-change exception, roll back to its savepoint and park the change. |
| 3 | High | `src/Chaos.Infrastructure/Attachments/AttachmentTransfer.cs:341` | `ContinueWith(..., OnlyOnRanToCompletion)` makes the returned task cancelled when `EnsureAsync` faults. Callers (`AttachmentCommands.cs:99`) see a cancel instead of the Offline/SignInAgain error, and the original exception goes unobserved. | `async` + `return await EnsureAsync(...) ?? false;` |
| 4 | High | `src/Chaos.Server/Blobs/BlobService.cs:216-264` | Two `CompleteAsync` calls for the same (account, sha), such as a client retry, each encrypt with their own data key into the same deterministic `ObjectKey`. The row can hold key A while the stored object was encrypted with key B, so the blob can never be read. The quota-failure path at `:242` can also delete the other call's object. | Claim the upload atomically first (a conditional update or delete of `BlobUploads` that only one caller wins), or give each completion its own storage key. |
| 5 | High | `src/Chaos.Server/Sync/SyncEvents.cs:94-117`, `Web/WebEvents.cs:28` | Each open event stream opens and keeps its own `NpgsqlConnection` for LISTEN. About 100 idle clients would use up the pool and starve every other request. | One process-wide LISTEN connection in a hosted service, fanned out through `SyncBroadcaster`. |
| 6 | Med-high | `src/Chaos.Infrastructure/Attachments/AttachmentTransfer.cs:366` | A shared download runs on the first caller's token. If a UI caller cancels, the background pass waiting on the same task gets an `OperationCanceledException`, the transfer loop dies (`:151-160`), and the row stays in `Downloading`. | Run downloads on the engine's own token. Each caller stops waiting with `WaitAsync(ct)`. Reset the state on cancellation. |
| 7 | Med | `src/Chaos.Server/Sync/SyncEvents.cs:126` | When the LISTEN connection drops, the loop ends with nothing logged. The stream keeps sending keep-alives but never hears pushes from other instances. | Log the error, call `into.TryComplete(error)`, and end the stream on `ChannelClosedException`. |
| 8 | Med | `src/Chaos.Infrastructure/Sqlite/SqliteApplicationStore.cs:155-159` | If `_source.Open()` fails after a restore, `_writer` stays disposed, and every later write throws `ObjectDisposedException` instead of `StorageUnavailable`. | Mark the store faulted and map the error. |
| 9 | Med | `src/Chaos.Infrastructure/Sqlite/SqliteApplicationStore.cs:218-233` | If the 10 s drain in `Dispose` times out, `_writer` is disposed while a write is still running on it. | Set `_closed` and skip the dispose (or log) when the drain times out. |
| 10 | Med | `src/Chaos.Server/Auth/AuthEndpoints.cs:50,75`, `Api/ServiceEndpoints.cs:49` | The sign-in throttle and the device rate-limit fallback key on `RemoteIpAddress`, and forwarded headers are not set up in code. Behind a proxy, every user shares one bucket. | `UseForwardedHeaders` with known proxies, and fail at startup if it is missing outside development. |
| 11 | Med | `src/Chaos.Server` (all options) | No options validation. For example, `Blob:PartSize=0` causes a divide-by-zero at `BlobService.cs:81`. | `AddOptions<T>().Validate(...).ValidateOnStart()`. |
| 12 | Low | `src/Chaos.Server/Auth/AccountService.cs:46-55` | The retry after a `DbUpdateException` calls itself with no limit. | Retry once. |
| 13 | Low | `src/Chaos.Server/Blobs/BlobService.cs:316-319` | The sweep deletes the stored object before the database row. A failure in between leaves a row pointing at nothing. | Delete the row first, then the object. |
| 14 | Low | `src/Chaos.Server/Sync/SyncService.cs:63`, `:166` | A push for a deleted account returns 500. `NotifyAsync` uses the request's token after the commit, so a cancelled request returns 500 even though the push landed. | Return a 401 with `account-deleted`. Use `CancellationToken.None` for the notify. |
| 15 | Low | `src/Chaos.Server/Program.cs` | There is no ProblemDetails or exception handler, so unhandled errors return an empty 500. | `AddProblemDetails()` + `UseExceptionHandler()`. |
| 16 | Low | `src/Chaos.Infrastructure/Attachments/AttachmentTransfer.cs:154-157` | A `Wake()` that arrives between `WhenAny` and the reset of `_wake` is dropped. | Swap the TCS the way `SyncEngine.WaitForWakeAsync` does. |
| 17 | Low | `src/Chaos.Infrastructure/Attachments/AttachmentTransfer.cs:443` | `Stream.Read` blocks on `ReadAsync()` (sync-over-async). This is latent today because nothing calls `Read`. | Forward to `inner.Read`. |
| 18 | Low | `src/Chaos.Application/Sites/SiteNoteCommands.cs:75` | Background work is started with `Task.Run` and nothing tracks it or stops it on shutdown. | Hand it to a hosted background queue owned by the shell. |

## 3. Maintainability

### Desktop composition root (`src/Chaos.Desktop/Program.cs:60-200`)
- It builds everything by hand and calls `new GuidIdGenerator()` 31 times.
- `protection.Quiesce` is rewrapped 4 times (reminders → GitHub → scheduler wake → calendars), so the pause
  order depends on which line comes first.
- Some dependencies are set as properties after construction (`attachmentCommands.Availability`,
  `storage.Store.ActivityNotifier`), so an object can be used before it is fully wired.
- Startup blocks on async calls with `.GetAwaiter().GetResult()` in several places.

**Fix:** a small `DesktopComposition` builder, or the `Microsoft.Extensions.DependencyInjection` container,
with shared singletons for the clock and ID generator and one explicit, ordered pause pipeline
(`IQuiescable[]`).

### Application layer
- **`IDataSession` has about 30 stores** (`Abstractions/DataStores.cs`). Every interface change breaks the
  hand-written fakes, which is exactly how the current build break happened. Consider narrower per-feature
  session interfaces, or fakes that inherit a generated "not supported" base.
- **Which stores take part in undo is only implied** by `History/ChangeRecording.cs:172-215`. Ten stores are
  wrapped; the rest pass straight through. `Recurrence`, `ReminderRules` and `Attachments` allocate a new wrapper
  on every property access while the others are created once. Make the list explicit and add a test that fails
  when a new mutating store is not wrapped or excluded on purpose.
- **Side effects sit in the Application layer.** `AttachmentCommands` deletes files in its constructor and does
  file I/O directly (`:67-71`, `:228`, `:508-530`). `GitHubConnectionCommands` reads `Environment.MachineName`.
  Move these behind `IAttachmentFileStore` or a small device-info abstraction.
- **Large files:** `Planning/MyWeekCommands.cs` (1,399 lines, 68 members), `Pages/PageCommands.cs`,
  `Pad/PadCommands.cs` and `Tags/TagCommands.cs`. Split queries from commands, and move the request and result
  records into their own files.

### Desktop RPC services (`src/Chaos.Desktop/Services`)
- There are 167 `catch (AppException)` blocks that each map the error by hand, and the `Version`/`Date` parsers
  are copied across 8 services. **Fix:** one shared `Rpc.Run(...)` wrapper and a shared `RpcParse` helper.

### Infrastructure
- **The session abstraction leaks.** `((SqliteDataSession)session).Connection` is cast 46 times. Add a
  `RawQueryAsync(Func<SqliteConnection, …>)` on the store.
- **SQL repeats C# constants.** `AttachmentTransfer.cs:207,220` hard-codes enum values (`Remote IN (0,3,4)`,
  `OwnerKind IN (3,4)`), and `:360` and `:548` concatenate `sha` into the SQL string. Use parameters built from
  the constants.
- **Adding a synced entity touches up to 8 places:** a migration, `SyncCatalog`, `Chaos.Sync/SyncKinds.cs`,
  `SyncPurgeSql`, `SyncRepointSql`, `PartialIndexPredicate` (`SyncApplier.cs:808`, which repeats the
  migrations' WHERE clauses by hand), the kind switches at `SyncApplier.cs:862/922/937`, and
  `MaxKnownSchemaVersion`. Generate `SyncKinds` from one source, and read the partial-index predicates from
  `sqlite_master`.
- **Large files:**
  - Split `Sqlite/SqliteStores.cs` (1,131 lines, 10 stores) into one file per store (done).
  - Split `Sync/SyncApplier.cs` (1,044 lines) into upsert/merge, unique-rule resolution, parking, and
    search/revision side effects.
- **Small items:**
  - 138 uses of `SELECT *` tie the mappers to column order.
  - `SqliteDataSession` creates about 30 store objects for every read; create them lazily.
  - `SyncApplier.cs:224` searches the catalog with a linear `FirstOrDefault` for every change; use a dictionary.
  - `SyncEngine.Dispose` disposes `_cycleGate` while an outside `SyncNowAsync` may still be running.
  - `SyncEngine.UploadCalendarCachesAsync` contains calendar mapping that belongs in `SqliteCalendarStore`.

### Server
- **Logic sits inside endpoint handlers:** the calendar cache in `Sync/SyncEndpoints.cs:71-109` (raw upsert
  SQL), and `DeleteAccount`/`RegisterDevice` in `Api/ServiceEndpoints.cs:73-256`. Move them into services.
- **The caller is parsed from the token in 5 places** (`ServiceEndpoints.cs:68`, `BlobEndpoints.cs:74`,
  `SyncEndpoints.cs:41`, `ActiveDeviceFilter`, and `WebSession.cs:15`, which uses `Guid.Parse` and throws).
  Use one shared accessor.
- **Error shapes are inconsistent.** The `Problem()` helpers are duplicated, some responses have no `code`, and
  the calendar cache returns a bare `BadRequest()`. Use one error factory.
- `Auth/AuthSetup.cs` repeats `ConfigureKeys` (`:153`, `:170`) and binds the providers section twice
  (`:62-63`).
- The security-header middleware is written inline in `Program.cs:63-92`; move it to an extension method.

### Frontends
- **`App.tsx` does too much.** It is one ~850-line component (`:64-912`) with 49 `useState` and 17 `useEffect`.
  Split it into a shell/navigation context plus per-domain hooks (`useContainers`, `useTags`, `useSavedViews`,
  `useNotice`).
- **Every screen hand-rolls the same fetch code** (effect → load → setState → ok/error). Add one
  `useRpcQuery(fn, deps, activityKinds)` hook that also guards against stale responses. `useActivity("*")` at
  `App.tsx:448` reloads everything on any commit.
- **Chaos.Web's REST client is untyped** (`src/Chaos.Web/src/mobileData.js:107`). A server DTO rename breaks the
  PWA silently at runtime. Move it to TypeScript with generated types (OpenAPI), or at least add a contract test.
- **The preview backend can drift without errors.** `frontend/src/preview/backend.ts` has `any`-typed
  handlers, a catch-all that guesses the response shape for unknown commands (`:808-819`), and a reimplemented
  view filter evaluator (`:199-237`). Type the handlers from the NeoAstra manifest and make unknown commands
  fail loudly.
- **Markdown and mermaid rendering are duplicated** between the desktop app (`markdown.tsx`, unified/remark) and
  Chaos.Web (`MarkdownNotes.jsx`, react-markdown), so the two will render differently. Extract a shared package.
- **Dependencies are not pinned.** `react`, `react-dom`, `vite`, `tsx`, `@types/react` and
  `@vitejs/plugin-react` are set to `latest` in `frontend/package.json`, against `docs/dependency-versions.md`.
  `cn` is unused; remove it.
- **No lint or format tooling.** The existing `eslint-disable` comments are not enforced by anything. Add ESLint
  (with `react-hooks`) and Prettier, or Biome, to both apps.

### Repository housekeeping
- The untracked root `package.json`, `package-lock.json` and `node_modules` (just `tsrs ^0.25`, while the
  frontend pins 0.23) look like an accidental `npm i` at the repo root. Delete them.
- Commit `.artifacts/`, `src/Chaos.Desktop/.agents/`, `.slopwatch/` and `skills-lock.json`, or add them to
  `.gitignore`.

## 4. Test gaps
- Server: concurrent blob completion, the LISTEN connection failing, many concurrent event streams, options
  misconfiguration, the throttle behind a proxy, and a push racing an account deletion.
- Sync: a malformed change in a pull chunk, and an unexpected exception type in the sync loop.
- Attachments: an `EnsureAvailableAsync` fault reaching the caller, and a caller cancelling a shared download.
- Undo: a guard that every mutating store is either recorded or excluded on purpose.

## 5. Suggested order
1. Fix the build break (`Fakes.cs`) and the two failing GitHub tests.
2. Add CI on push and PR that runs `eng/verify.ps1 -NoServer` plus `npm test` for both frontends, with the server
   tests in a job that has a PostgreSQL service.
3. Fix bugs 1–4 and 6 (sync and attachment loops, the blob race).
4. Fix the server's connection per stream (5, 7) and add options validation (11).
5. Maintainability, in this order: the composition root, the RPC error wrapper, the undo-coverage guard,
   `App.tsx` and `useRpcQuery`, pinning the frontend dependencies and adding lint, then splitting the large files.
