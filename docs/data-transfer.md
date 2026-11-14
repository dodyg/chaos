# Backups, export and import

This page lists what each format holds and what it leaves out (PLAN §14). Only the Chaos
JSON archive is a complete, lossless copy. The Markdown and Org exports are for reading and
for other tools, and they keep the original Markdown notes unchanged.

## Backups

- **Where.** `%LOCALAPPDATA%\Chaos\backups`, or `<data-dir>\backups` with `--data-dir`.
  Each backup is a SQLite file `chaos-<UTC time>-<id>.db` with a description file `….db.json`
  beside it. The description holds the format, data (schema) version, app version, reason,
  SHA-256 checksum, size and counts.
- **How.** SQLite's online backup API copies the live database, so the copy is consistent
  even while the WAL holds changes. The copy is written to a `.partial` file and switched to
  a single self-contained file. It must pass `integrity_check` and `foreign_key_check` and
  have a data version this build can open. Only then is it checksummed, renamed into place
  and described. A failed backup leaves no file, and Settings shows the error. A full disk is
  reported as such.
- **When.** Before a database update (migration), an import and a restore. Also on request,
  and once a day when something has changed since the newest backup.
- **Retention.** Applied only after a new backup succeeds. Chaos keeps:
  - the newest backup;
  - the newest backup of each of the last 7 days that have one;
  - the newest of each of the last 4 weeks (weeks start on Monday, UTC);
  - the 3 newest safety backups (made before an update, import or restore).

  Files without a description are never deleted by retention.
- **Not encrypted.** Backups are ordinary files protected by your Windows account, like the
  database itself.

## Restore

Settings → Data and backups → Backups → Review restore.

1. Choose a backup in the backups folder. To restore a backup copied from elsewhere, put the
   `.db` file (and its `.json` if you have it) into the backups folder first.
2. Chaos checks it: the checksum against its description, SQLite integrity, foreign keys and
   the data version. It then shows the backup's counts next to the current ones.
3. After you confirm, Chaos makes a safety backup of the current data. It then pauses
   reminders, takes back scheduled Windows notifications and closes every database
   connection. The current database files are moved aside, not deleted, and the backup is
   moved into place. A marker file records each step.
4. The restored file is opened and upgraded to this version's schema, the search index is
   rebuilt, and notification schedules are recreated. If any of this fails, the original
   files are put back.

If Chaos stops in the middle of a restore, the next start either finishes it (the new file is
fully in place and passes its checks) or puts the previous data back. Settings shows which.

A Chaos JSON archive can also replace all data: Import → "Chaos archive: replace all data".
Chaos builds a new database from the archive, checks it, and swaps it in the same way.

## Chaos JSON archive (complete)

A ZIP with two entries:

- `manifest.json`: format `chaos-archive` version 1, data version, app version, export time,
  import policy, SHA-256 and size of `data.json`, row count per table, and what was left out.
- `data.json`: every row of every user-data table, with stable IDs, including deleted
  (tombstoned) rows. Values keep their SQLite types: integers stay numbers, text stays
  strings, and reals and binary values are tagged.

Every table is included except the following, which are rebuilt or belong to this device:

- schema migration bookkeeping
- the backup log
- operation receipts (retry bookkeeping)
- the search index

Notification jobs keep their delivery history, but their Windows schedule ID and retry state
are reset. A restore schedules them again.

Restoring an archive from an older version builds the database at the archive's version,
loads the rows, then applies the newer migrations. Archives from a newer version are refused.

**Merge** (Import → "Chaos archive: add its tasks") adds the archive's active tasks with their
IDs, list, tags (by full path), subtasks and diary entries. If a task ID is already used by a
different local task, it is a conflict: the preview names the local task and the local task
is kept. A task with the same ID and title counts as already imported. Recurrence, reminders,
plans, the Personal pad and history come back only with a full restore.

## Markdown export (readable)

A ZIP:

- `README.md` explains the layout.
- `tasks/<NN> <list>/<title> (<id8>).md` holds one file per active task. The front matter
  records:
  - `id`, `list`, `list_id` and `section`
  - `status` and `priority`
  - `planned` and `due` (date, time and time zone)
  - `completed` and `cancelled` instants
  - `estimate_minutes` and `category`
  - `tags` (full paths)
  - `recurrence` (the rule as JSON)
  - `created` and `updated`

  The body holds the title, the original Markdown notes, the subtasks as a checklist (each
  with its ID in an HTML comment) and the diary entries.
- `pad/<NNN> <first line> (<id8>).md` holds one file per Personal pad note, with its tags and
  archived flag.

File names never contain path separators, reserved characters or Windows device names. They
are capped in length and made unique.

Deleted items, history, plans, reminders and settings are not included.

## Org export (readable)

One `.org` file:

- `#+TODO: TODO STARTED WAITING REVIEW | DONE CANCELLED` maps the six statuses.
  `#+PRIORITIES: A D C` maps the priorities: A = Urgent, B = High, C = Normal, D = Low.
  Tasks with no priority get no cookie.
- Each list is a level-1 heading with its ID and kind as properties. Tasks are level-2 TODO
  headings and subtasks are level 3.
- `SCHEDULED` is the planned date, `DEADLINE` is the due date, and `CLOSED` is the completion
  or cancellation time (UTC).
- Org tags are a simplified form of the tag names. The exact paths are in `:CHAOS_TAGS:`
  (JSON).
- The properties drawer holds the stable `:ID:` and the `CHAOS_` properties:
  - `CHAOS_TITLE`: the exact title, when the heading would be ambiguous
  - `CHAOS_SECTION`, `CHAOS_ESTIMATE` and `CHAOS_CATEGORY`
  - `CHAOS_PLANNED_ZONE` and `CHAOS_DUE_ZONE`
  - `CHAOS_RECURRENCE`: the rule as JSON
  - `CHAOS_CREATED`
- `:LOGBOOK:` lists status changes from the task history (UTC).
- Notes are the original Markdown inside `#+BEGIN_SRC markdown` blocks. Lines that Org would
  read as structure get Org's comma escape. Each diary entry is its own block, tagged
  `:chaos-diary <UTC time>`.

Markdown is not converted to Org markup.

## Import

Every import is read and checked completely before anything is written. The preview shows:

- new tasks, subtasks and diary entries;
- items already imported or already present (skipped);
- ID conflicts;
- the target list for each source list (an existing list or a new one);
- problems by line, and notes about anything left out.

After you confirm, Chaos makes a backup and writes everything in one transaction. If any row
fails, nothing is written. To undo an import, restore the backup made just before it (listed
under Recent imports).

Each source item has a fingerprint: its stable ID when the file has one, otherwise its
content. Importing the same file again skips what was already imported, as long as those
tasks still exist. An item whose ID is already a local task's ID is skipped as already
present.

Nothing in an imported file is run or opened. Links, HTML and file paths stay plain text in
the notes. ZIP archives are checked before they are read:

- at most 10,000 entries;
- at most 256 MB per entry and 512 MB in total;
- no absolute paths, drive letters, backslashes or `..` segments.

Text files must be UTF-8. The largest file one import accepts is 100 MB.

### CSV

- RFC 4180: quoted fields, doubled quotes and line breaks inside quotes are supported. The
  delimiter (comma, semicolon or tab) is detected from the header line.
- **Columns:** `title` (required), `notes`, `list`, `status`, `planned`, `due`, `priority`,
  `tags` (separated by `,` or `;`), `id` and `parent_id`. Common header names are recognized,
  such as Name/Task/Content, Description, Project, Start date, Due date/Deadline and Labels.
  Any column can be chosen by hand in the preview.
- **Status:** words such as todo/open, in progress/started, waiting/blocked, review,
  done/completed/x/true/yes, and cancelled/canceled. Anything else is imported as not started,
  with a note.
- **Priority:** none, low, normal/medium, high, urgent, or the Chaos values 0–4. Anything
  else becomes Normal, with a note.
- **Dates:** `yyyy-MM-dd`, optionally followed by a time (`yyyy-MM-dd HH:mm`). A time uses
  your profile's time zone. Other formats are refused rather than guessed.
- **Subtasks:** a row whose `parent_id` names another row's `id` becomes that row's subtask.
  Subtasks keep their title, notes and status. A subtask cannot have subtasks of its own.
- Proprietary to-do app exports (such as Todoist or Microsoft To Do) are not specifically
  supported. Their CSV files can be imported through the column mapping.

### Org (the subset Chaos writes)

- A heading with a TODO keyword is a task. Keyword headings below a task become its
  subtasks; deeper levels are flattened, with a note. A heading without a keyword is a list.
- **Keywords:** the six Chaos keywords plus NEXT and CANCELED. Custom keywords declared with
  `#+TODO:` also work: those before `|` are open, those after are done.
- Supported: priority cookies A–D, heading tags (or `CHAOS_TAGS`), SCHEDULED, DEADLINE and
  CLOSED, the properties above, plain-text notes, Markdown source blocks and `:chaos-diary`
  blocks.
- Repeaters and `CHAOS_RECURRENCE` are imported as a single date, with a note. Recurrence
  comes back only from a JSON restore. LOGBOOK drawers are skipped.

## Removing data

- **Removing deleted items permanently** (Settings → Deleted items) affects only items
  already in the trash. Everything that belongs only to them goes too:
  - subtasks, tag links and diary entries with their revisions
  - history and search entries
  - reminders, follow-ups and scheduled notifications (taken back from Windows)
  - My Day and week-plan membership, pad links and import records
  - undo entries that mention them

  Stored summaries keep their counts, but the task's captured title becomes "Permanently
  deleted task" and its diary excerpts are cleared. Deleted lists, sections, tags, templates
  and recurrence series go once nothing refers to them. The database is then compacted, so
  the removed text is not left in free space. Backups made earlier still contain these items
  until retention removes them.
- **Delete all local data** needs you to type `DELETE`. It pauses reminders, takes back
  scheduled Windows notifications, deletes the database and, if you choose, Chaos's own
  backup files, and then starts an empty database. Exports saved in folders you chose are
  never touched.

  Windows keeps notifications that were already shown in its own notification history until
  you clear them there. The window's embedded browser keeps a cache managed by WebView2
  outside the Chaos data folder. It holds no task data and is not removed.

GitHub connection data (schema 14) is included in complete JSON archives and database backups,
including issue and comment text from private repositories. GitHub credentials are never part
of either format. JSON exports omit sync-run diagnostics and reset ETags, incremental cursors,
rate-limit/reset times, retry times and attempt counts. Markdown and Org task exports retain
the issue's source URL. Permanently purging a linked task removes checklist/comment mappings
and keeps an ignored issue identity so synchronization cannot import that issue again.

GitHub links in a JSON merge are retained only when the same connection and repository
subscription exist locally and no local issue/link identity collides. Otherwise the preview
warns that the task will be ordinary, and an origin note is added to its diary. Retained
checklist mappings follow the imported subtask IDs; retained comment mappings keep their diary
IDs so a pending comment keeps the same deduplication marker. Missing connections are never
created or authorized by a merge. A complete restore retains the connection metadata but still
requires this device's CLI account verification before any synchronization.
