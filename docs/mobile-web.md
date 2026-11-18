# Mobile web (PWA) plan

Status: implemented on Chaos.Server as a follow-on to P16, 2026-11-18. Physical iPhone and Android acceptance is pending.

## Run and install

Build or publish `src/Chaos.Server` with the .NET SDK and Node.js 22.12 or newer available on the
build machine. The server build restores the locked Vite/React dependencies and bundles the app
under `wwwroot/app`. The published server serves those files directly.

Join the account from Chaos desktop and let it sync the Inbox. On your phone, open `/app` on the
same trusted HTTPS origin and sign in with the same account. Use **Add to Home Screen** from the
browser menu. Open My Day or a list while connected to save it for offline viewing.

Use the existing Chaos.Server deployment. Persist its ASP.NET Core Data Protection key ring across
releases and share it across replicas so they can validate the same web session cookies. The web
app refreshes the active view every ten seconds while visible. Desktop clients receive sync
events through the existing service. The app keeps My Day, the list picker and the last opened
list in the browser. Task notes are fetched while connected. Signing out clears the saved views.

## Goal

Open your tasks on a phone through a URL, with no app store. Installable with "Add to Home Screen".
Online actions only. The last-read list is cached so the app opens with no signal, but edits need a
connection.

## Decisions this rests on

- **No offline edits.** The server is the only place changes are merged, so the browser needs no
  merge code. That is why no WASM (NetWasm) and no TypeScript port of `Chaos.Sync` are needed.
- **Same origin.** `Chaos.Server` serves the PWA's static files, so CORS stays off (PLAN2 8.5).
- **A web change is a sync change.** The server turns each web action into a normal change and
  pushes it through the existing `SyncService` push path. The desktop receives it by pull and SSE
  like any other device's change. Conflict rules are unchanged.
- **Cookie session, not tokens in JS.** The web app uses an HttpOnly, SameSite=Lax cookie issued
  after the existing sign-in pages. No access or refresh token reaches the page. Writes need an
  anti-forgery token. (Alternative: OpenIddict public client with PKCE. More code, no benefit here.)

## Decisions

| ID | Question | Decision |
| --- | --- | --- |
| D1 | Where does mobile web fit in PLAN2? | A follow-on work package after P16, recorded in `docs/decisions.md`. |
| D2 | How does a phone reach the server? | The server's trusted HTTPS URL. Deployment can use a public host or private HTTPS through Tailscale. |
| W1 | Which screens are in the first release? | My Day, list picker, lists and task detail, with task capture, complete, reopen and My Day membership. |

## Sync mapping

- Capture writes the task's `create`, `title`, `notes`, `status`, `place`, `position`, `priority`,
  `estimate`, `category`, `planned`, `due` and `deleted` groups. The account's Inbox is the default.
- Complete and reopen write `status` and its terminal timestamps. Reopen resets the status to
  **Not started**. Complete leaves subtasks in their current state. Recurring task completion and
  reopening use the desktop recurrence controls.
- My Day shows open and finished tasks from the synced day plan in its saved order. It uses the
  synced profile time zone, with UTC as the fallback before a profile arrives.
- If the day plan has not been initialized, My Day shows open tasks assigned to the date in the
  confirmed week. Editing membership or completing a task from that initial plan saves the plan.
- Adding to or removing from My Day writes day-plan membership through sync. Planned and due dates
  belong to the task and are independent of membership.

## Work packages, in priority order

### W0 — Spike and decisions (half a day)

- Record D1, D2 and W1.
- Read `SyncKinds.All` and write down the task kind's groups (title, status, planned date, due
  date, list, notes) and which fields My Day uses to decide "today".
- Prove one thing end to end in a test: the server builds a change for an existing task, pushes it
  through `SyncService`, and a second client pulls it.
- Exit: a short note listing the exact groups a web write touches.

### W1 — Web sign-in (1 to 2 days)

- Cookie auth scheme beside OpenIddict. After the existing email-code or provider sign-in, issue the
  cookie and redirect to `/app`.
- Register a `web` device row (`Platform = "web"`) so it shows up in Settings → Account → Devices
  and can be revoked. Revoking invalidates the cookie.
- Sliding expiry, sign-out, anti-forgery on every write.
- Tests: another account's data is unreachable, a revoked device gets 401, no cookie means no data.

### W2 — Read model (2 days)

Endpoints under `/v1/web`, filtered by `AccountId` from the cookie, never from the request:

- `GET /today`: today’s My Day tasks, in the desktop’s saved order.
- `GET /lists` and `GET /lists/{id}/tasks`.
- `GET /tasks/{id}`: title, notes, dates, status.
- Build DTOs from `SyncEntities.Groups` (skip tombstones and purged rows). Keep this in one
  `WebReadModel` class so the field mapping lives in one place.
- Tests: rows with tombstones, rows missing groups, ordering matches the desktop for a fixture set.

### W3 — App shell and screens (3 days)

- New `src/Chaos.Web/` (Vite and React, mobile first), built into `Chaos.Server/wwwroot/app`. Do not
  fork the desktop frontend; copy only small presentational pieces.
- Screens: sign-in redirect, My Day, list picker, list view, task detail.
- Manifest, icons, `display: standalone`, theme colour. Light and dark.
- Phone-width checks in the browser preview at 375px.

### W4 — Writes (2 days)

- `POST /tasks` (title, optional list and planned date). Goes to the Inbox by default.
- `POST /tasks/{id}/complete` and `/reopen`.
- `POST /tasks/{id}/add-to-my-day` and `/remove-from-my-day`.
- Each builds a change, gives it the server's HLC and `OriginDevice` = the web device, and calls
  the push path. Return the new task state.
- **Recurring tasks:** completing one creates the next occurrence on the desktop (client logic in
  `Chaos.Application`). Do not reimplement it here. Disable Complete on recurring tasks with the
  text "Complete on desktop" until W7.
- Tests: two-client test where the desktop edits the same task concurrently and the merge is
  field-level, last write wins, replaced value kept in history (PLAN2 7.7).

### W5 — Cache and install (1 day)

- Service worker caches the app shell and the last My Day and list responses. Offline shows the cached
  data with a "Last updated" line and disables writes.
- Updates: new service worker waits, then shows "Reload for the new version".
- iOS notes: realtime (SSE) only while the app is open; no push.

### W6 — Hardening and deploy (2 days)

- Rate limits on `/v1/web`, security headers (CSP, frame-ancestors none), cookie flags tested.
- Security review pass on the new endpoints (extends P20).
- Deployment: static files served by the existing container, no new service.
- Acceptance: install on a real iPhone and Android phone, add and complete a task, see it on the
  desktop within seconds, and the reverse.

### Later (not scheduled)

- W7: complete recurring tasks. Needs the recurrence rules callable from the server, either by
  moving the rule into `Chaos.Sync` or `Chaos.Domain` (referenced by both) or by queueing the
  completion for the next desktop sync. Decide when it is needed.
- Edit title, notes and dates.
- Diary and pages, read only.
- Attachment viewing (blobs are already served over HTTPS).
- Live updates via SSE while the app is open.
- Offline edits: only then does a shared-merge approach (NetWasm or a TypeScript port) matter.

## Effort and order

| Order | Package | Days | Delivers |
| --- | --- | --- | --- |
| 1 | W0 | 0.5 | Decisions, exact groups |
| 2 | W1 | 1 to 2 | Sign-in from a phone |
| 3 | W2 | 2 | Read API |
| 4 | W3 | 3 | **Read-only app on your phone** (first useful release) |
| 5 | W4 | 2 | Add and complete tasks |
| 6 | W5 | 1 | Installable, opens offline (read only) |
| 7 | W6 | 2 | Safe to expose |

About 12 to 13 days. W3 is the first point where you can look at tasks on a phone. W6 must be done
before the server is reachable from the internet, whatever else is skipped.

## Risks

- The read model duplicates "what counts as today". Mitigation: W0 writes it down and W2 tests it
  against desktop fixtures.
- Recurring tasks and any completion side effects (history entries, reminders cleared) are client
  logic. Mitigation: W4 limits writes to what the sync groups carry, and disables the rest.
- Exposing the server publicly raises the stakes of every existing endpoint. Mitigation: W6, and
  Tailscale if you want to stay private.
