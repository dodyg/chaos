/**
 * An in-memory stand-in for the desktop host, used only by the browser preview
 * (`npm run preview:ui`). It answers the RPC commands the main screens need with seeded
 * sample data and keeps changes until the page reloads. Anything it does not know answers
 * with an empty, successful read so screens render instead of failing.
 */
import type {
  CaptureDraftDto, ContainerResponse, PadItemResponse, DaySummaryResponse, QueryRowResponse, SectionResponse, SubtaskResponse, TagResponse, TaskQueryRequest, TaskResponse,
  UndoEntryResponse, WeekBoardDto, WeekOverviewDto,
} from "#neoastra";

type Handler = (request: any) => unknown;

const pad = (n: number) => String(n).padStart(2, "0");
const localDate = (date: Date) => `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
export function addDays(date: string, days: number) {
  const [y, m, d] = date.split("-").map(Number);
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10);
}
const today = localDate(new Date());
const now = () => new Date().toISOString();
let sequence = 100;
let previewIssueAssignees = ["octocat"];
const newId = (prefix: string) => `${prefix}-${++sequence}`;

const containers: ContainerResponse[] = [
  { id: "inbox", name: "Inbox", kind: "Inbox", version: "1", archived: false, deadlineDate: null, defaultCategory: null },
  { id: "sprint", name: "Sprint 21", kind: "Project", version: "1", archived: false, deadlineDate: addDays(today, 17), defaultCategory: "Work" },
  { id: "chaos", name: "Chaos", kind: "Project", version: "1", archived: false, deadlineDate: null, defaultCategory: "Work" },
  { id: "social", name: "Social", kind: "List", version: "1", archived: false, deadlineDate: null, defaultCategory: "Personal" },
  { id: "home", name: "Home", kind: "List", version: "1", archived: false, deadlineDate: null, defaultCategory: null },
];
const sections = new Map<string, SectionResponse[]>([["sprint", [{ id: "s-review", title: "In review", version: "1" }]]]);
const tags: TagResponse[] = [
  { id: "t-ai", name: "ai", path: "ai", parentId: null, depth: 0, color: null, version: "1" },
  { id: "t-ideas", name: "ideas", path: "ideas", parentId: null, depth: 0, color: null, version: "1" },
];
const taskTags = new Map<string, string[]>([["task-2", ["t-ai"]]]);

function task(id: string, title: string, patch: Partial<TaskResponse> = {}): TaskResponse {
  return { id, title, spaceId: "personal", containerId: "inbox", sectionId: null, notes: "", status: "NotStarted", priority: "Normal", estimateMinutes: null, category: "Personal",
    plannedDate: null, plannedTime: null, plannedZoneId: null, dueDate: null, dueTime: null, dueZoneId: null, version: "1", createdAtUtc: now(), updatedAtUtc: now(), ...patch };
}
const tasks: TaskResponse[] = [
  task("task-1", "Business Chat GPT implementation", { status: "WaitingOnSomeone", priority: "High", category: "Work", plannedDate: addDays(today, -1), estimateMinutes: 120, notes: "Waiting on API keys from the vendor.\n\n- [ ] Draft integration plan\n- [x] Collect requirements" }),
  task("task-2", "Pre-Registration User Blocklist", { containerId: "sprint", category: "Work", dueDate: addDays(today, 17), estimateMinutes: 90 }),
  task("task-3", "Finish Amideast proposal", { status: "InProgress", category: "Work", plannedDate: addDays(today, -2), estimateMinutes: 60 }),
  task("task-4", "Water the plants", { containerId: "home", plannedDate: today, estimateMinutes: 15 }),
  task("task-5", "Call mum", { containerId: "social", dueDate: today }),
  task("task-6", "Review pull requests", { containerId: "sprint", sectionId: "s-review", category: "Work", estimateMinutes: 45 }),
  task("task-7", "Book dentist appointment", { dueDate: addDays(today, -1) }),
  task("task-8", "Write release notes", { containerId: "chaos", category: "Work", status: "Done" }),
];
const deletedTasks: TaskResponse[] = [];
const subtasks: SubtaskResponse[] = [
  { id: "sub-1", taskId: "task-2", title: "Design the blocklist table", notes: "", status: "Done", version: "1" },
  { id: "sub-2", taskId: "task-2", title: "Add the admin screen", notes: "", status: "NotStarted", version: "1" },
];
const dayPlans = new Map<string, string[]>([[today, ["task-1", "task-2", "task-3", "task-4"]]]);
const undo: UndoEntryResponse[] = [];
const padNotes: PadItemResponse[] = [
  { id: "pad-1", markdown: "React Server Components\n\n- read the RFC\n- try it in a side project", version: "1", draftVersion: "1", archived: false, archivedAtUtc: null, deleted: false, createdAtUtc: now(), updatedAtUtc: now(), tagIds: [], tasks: [] },
];

const ok = <T extends object>(value: T) => ({ ok: true, error: null, ...value });
const fail = (message: string) => ({ ok: false, error: { kind: "Validation", message, fields: [], correlationId: "preview" } });
const isFinished = (t: TaskResponse) => t.status === "Done" || t.status === "Cancelled";
const containerName = (id: string) => containers.find(c => c.id === id)?.name ?? "Inbox";
const row = (t: TaskResponse, groupDate: string | null = null): QueryRowResponse => ({ task: t, containerName: containerName(t.containerId), containerArchived: false, tagIds: taskTags.get(t.id) ?? [], matches: [], groupDate, waitingPerson: null, nextFollowUpAtUtc: null });
const find = (id: string) => tasks.find(t => t.id === id);
const bump = (version: string) => String(Number(version) + 1);
function record(label: string) { undo.unshift({ operationId: newId("op"), label, appliedAtUtc: now() }); }
function update(id: string, patch: Partial<TaskResponse>, label: string) {
  const index = tasks.findIndex(t => t.id === id);
  if (index < 0) return { ...fail("That task is not available."), operationId: "x", task: null, warning: null };
  tasks[index] = { ...tasks[index], ...patch, version: bump(tasks[index].version), updatedAtUtc: now() };
  record(`${label} “${tasks[index].title}”`);
  return ok({ operationId: newId("op"), task: tasks[index], warning: null });
}

function matches(t: TaskResponse, request: TaskQueryRequest) {
  if (request.category && t.category !== request.category) return false;
  if (request.statuses?.length) { if (!request.statuses.includes(t.status)) return false; }
  else if (!request.includeCompleted && isFinished(t)) return false;
  if (request.tagId && !(taskTags.get(t.id) ?? []).includes(request.tagId)) return false;
  if (request.text && !`${t.title} ${t.notes}`.toLowerCase().includes(request.text.toLowerCase())) return false;
  switch (request.view) {
    case "Today": return !isFinished(t) && (t.plannedDate === today || t.dueDate === today);
    case "Upcoming": { const date = t.plannedDate ?? t.dueDate; return !isFinished(t) && !!date && date > today && date <= addDays(today, 14); }
    case "Overdue": return !isFinished(t) && !!t.dueDate && t.dueDate < today;
    case "Waiting": return t.status === "WaitingOnSomeone";
    case "RecentlyCompleted": return t.status === "Done";
    case "MyDay": return (dayPlans.get(request.day ?? today) ?? []).includes(t.id);
    default: return true;
  }
}
function runQuery(request: TaskQueryRequest) {
  let rows = tasks.filter(t => matches(t, request));
  if (request.view === "MyDay") { const order = dayPlans.get(request.day ?? today) ?? []; rows = rows.sort((a, b) => order.indexOf(a.id) - order.indexOf(b.id)); }
  return rows;
}

function daySummary(date: string): DaySummaryResponse {
  const chosen = (dayPlans.get(date) ?? []).map(find).filter((t): t is TaskResponse => !!t);
  const open = chosen.filter(t => !isFinished(t));
  return { date, clock: clock(), exists: dayPlans.has(date), editable: date >= today, openCount: open.length, finishedCount: chosen.length - open.length,
    remainingMinutes: open.reduce((sum, t) => sum + (t.estimateMinutes ?? 0), 0), unknownEstimates: open.filter(t => t.estimateMinutes === null).length,
    availableMinutes: 480, workAllowanceMinutes: 360, workMinutes: open.filter(t => t.category === "Work").reduce((sum, t) => sum + (t.estimateMinutes ?? 0), 0) };
}
function clock() { return { today, nextDayStartsAtUtc: `${addDays(today, 1)}T00:00:00Z`, zoneId: "UTC" }; }
function dayChange(date: string, changed = 1) { return ok({ day: daySummary(date), changed, replayed: false }); }

function week(): WeekOverviewDto {
  const start = addDays(today, -((new Date(`${today}T00:00:00Z`).getUTCDay() + 6) % 7));
  const days = Array.from({ length: 7 }, (_, i) => addDays(start, i));
  const placements = days.flatMap(date => (dayPlans.get(date) ?? []).map(taskId => ({ taskId, day: date, origin: "Planned" })));
  const minutes = (date: string, work: boolean) => (dayPlans.get(date) ?? []).map(find).filter((t): t is TaskResponse => !!t && (!work || t.category === "Work")).reduce((sum, t) => sum + (t.estimateMinutes ?? 0), 0);
  const board: WeekBoardDto = { goals: [], placements,
    days: days.map(date => { const planned = minutes(date, false), work = minutes(date, true), weekend = days.indexOf(date) >= 5;
      return { date, availableMinutes: weekend ? 0 : 480, workAllowanceMinutes: weekend ? null : 360, isOverride: false, plannedMinutes: planned, workMinutes: work, unknownEstimates: 0, openCount: 0, overTotal: planned > (weekend ? 0 : 480), overWork: !weekend && work > 360 }; }),
    pool: { plannedMinutes: 0, workMinutes: 0, unknownEstimates: 0, openCount: 0 } };
  return { weekStart: start, weekEnd: days[6], today, firstWeekday: "Monday", planId: "plan", state: "Confirmed", confirmedRevision: "1", confirmedAtUtc: now(),
    editable: true, availabilityConfigured: true, confirmed: board, draft: null, draftRevision: "0", draftBoard: null,
    lookBack: { weekStart: addDays(start, -7), weekEnd: addDays(start, -1), hadPlan: false, completedTaskIds: [], completedCount: 0, assignedCount: 0, assignedDone: 0, assignedCancelled: 0, unfinishedTaskIds: [], goals: [], journalSummaryId: null, journalCompleted: [] },
    candidates: [], goalSuggestions: [], pendingReconciliation: [], checkIn: null,
    tasks: tasks.map(t => ({ task: t, containerName: containerName(t.containerId), containerArchived: false })) };
}

function previewDraft(text: string, containerId: string | null): CaptureDraftDto {
  const category = /(^|\s)~(work|w)\b/i.test(text) ? "Work" : /(^|\s)~(personal|p|home)\b/i.test(text) ? "Personal" : null;
  const tagPaths = [...text.matchAll(/(^|\s)#([\w/-]+)/g)].map(m => m[2]);
  const title = text.replace(/(^|\s)[~#][\w/-]+/g, " ").replace(/\s+/g, " ").trim();
  return { title, notes: null, containerId, priority: "Normal", estimateMinutes: null, plannedDate: null, plannedTime: null, dueDate: null, dueTime: null, zoneId: "UTC",
    recurrence: null, reminders: [], tagIds: [], tagPaths, waitingOn: null, followUpDate: null, followUpTime: null, subtasks: [], category };
}

const handlers: Record<string, Handler> = {
  "shell.take": () => ({ ok: true, activations: [] }),
  "shell.zones": () => ({ ok: true, zones: ["UTC", "Africa/Cairo", "Europe/London", "America/New_York"] }),
  "shell.status": () => ({ ok: true, appVersion: "preview", keepRunningInTray: false, packaged: false, shortcuts: [], trayAvailable: false }),
  "shell.quit": () => ({ ok: true }),

  "tasks.listInbox": () => ok({ tasks: tasks.filter(t => t.containerId === "inbox") }),
  "tasks.listDeleted": (r: { containerId: string | null }) => ok({ tasks: deletedTasks.filter(t => t.containerId === (r.containerId ?? "inbox")) }),
  "tasks.get": (r: { taskId: string }) => { const t = find(r.taskId); return t ? ok({ task: t }) : { ...fail("That task is not available."), task: null }; },
  "tasks.edit": (r: Record<string, unknown> & { taskId: string; expectedVersion: string }) => {
    const t = find(r.taskId);
    if (t && t.version !== r.expectedVersion) return { ...fail("This task changed elsewhere while you were working. Refresh and try again."), operationId: "x", task: null, warning: null };
    const { operationId: _o, taskId: _t, expectedVersion: _v, ...fields } = r;
    return update(r.taskId, fields as Partial<TaskResponse>, "Edit");
  },
  "tasks.setStatus": (r: { taskId: string; status: string }) => update(r.taskId, { status: r.status }, "Change status of"),
  "tasks.reorder": (r: { taskId: string }) => update(r.taskId, {}, "Reorder"),
  "tasks.delete": (r: { taskId: string }) => {
    const index = tasks.findIndex(t => t.id === r.taskId);
    if (index < 0) return { ...fail("That task is not available."), operationId: "x", task: null, warning: null };
    const [removed] = tasks.splice(index, 1); deletedTasks.push(removed); record(`Delete “${removed.title}”`);
    return ok({ operationId: newId("op"), task: removed, warning: null });
  },
  "tasks.restore": (r: { taskId: string }) => {
    const index = deletedTasks.findIndex(t => t.id === r.taskId);
    if (index < 0) return { ...fail("That task is not available."), operationId: "x", task: null, warning: null };
    const [restored] = deletedTasks.splice(index, 1); tasks.push(restored);
    return ok({ operationId: newId("op"), task: restored, warning: null });
  },

  "containers.list": () => ok({ containers }),
  "containers.listDeleted": () => ok({ containers: [] }),
  "containers.tasks": (r: { containerId: string }) => ok({ tasks: tasks.filter(t => t.containerId === r.containerId) }),
  "containers.sections": (r: { containerId: string }) => ok({ sections: sections.get(r.containerId) ?? [] }),
  "containers.deletedSections": () => ok({ sections: [] }),
  "containers.progress": (r: { containerId: string }) => { const rows = tasks.filter(t => t.containerId === r.containerId && t.status !== "Cancelled");
    return ok({ doneCount: rows.filter(t => t.status === "Done").length, totalCount: rows.length, doneSubtaskCount: 0, totalSubtaskCount: 0 }); },
  "containers.create": (r: { name: string; kind: string }) => { const c: ContainerResponse = { id: newId("list"), name: r.name, kind: r.kind, version: "1", archived: false, deadlineDate: null, defaultCategory: null }; containers.push(c); return ok({ container: c }); },
  "containers.createSection": (r: { containerId: string; title: string }) => { const s = { id: newId("section"), title: r.title, version: "1" }; sections.set(r.containerId, [...(sections.get(r.containerId) ?? []), s]); return ok({ section: s }); },
  "containers.moveTask": (r: { taskId: string; containerId: string; sectionId: string | null }) => update(r.taskId, { containerId: r.containerId, sectionId: r.sectionId }, "Move"),
  "containers.setDefaultCategory": (r: { containerId: string; defaultCategory: string | null }) => {
    const index = containers.findIndex(c => c.id === r.containerId);
    containers[index] = { ...containers[index], defaultCategory: r.defaultCategory, version: bump(containers[index].version) };
    return ok({ container: containers[index] });
  },

  "queries.run": (r: TaskQueryRequest) => { const rows = runQuery(r).map(t => row(t, r.view === "Upcoming" ? t.plannedDate ?? t.dueDate : null)); return ok({ rows, totalCount: rows.length, nextCursor: null }); },
  "queries.counts": (r: { category: string | null }) => ok({
    views: ["MyDay", "Today", "Upcoming", "Overdue", "Waiting", "RecentlyCompleted", "AssignedToMe"].map(view => ({ view, count: runQuery({ view, category: r.category, statuses: [], includeCompleted: false, day: null } as unknown as TaskQueryRequest).filter(t => view === "MyDay" ? !isFinished(t) : true).length })),
    tags: tags.map(tag => ({ tagId: tag.id, count: tasks.filter(t => (taskTags.get(t.id) ?? []).includes(tag.id) && (!r.category || t.category === r.category)).length })),
    containers: containers.map(c => ({ containerId: c.id, count: tasks.filter(t => t.containerId === c.id && !isFinished(t) && (!r.category || t.category === r.category)).length })),
  }),

  "myDay.clock": () => ok({ clock: clock() }),
  "myDay.open": (r: { date: string | null }) => { const date = r.date ?? today; if (date >= today && !dayPlans.has(date)) dayPlans.set(date, []); return ok({ day: daySummary(date) }); },
  "myDay.suggestions": () => ok({ suggestions: tasks.filter(t => !isFinished(t) && !(dayPlans.get(today) ?? []).includes(t.id) && (t.dueDate !== null && t.dueDate <= addDays(today, 2) || t.plannedDate === today))
    .map(t => ({ task: t, containerName: containerName(t.containerId), reason: t.dueDate && t.dueDate < today ? "Overdue" : t.dueDate === today ? "DueToday" : t.plannedDate === today ? "PlannedToday" : "DueSoon" })) }),
  "myDay.add": (r: { date: string; taskId: string }) => { const plan = dayPlans.get(r.date) ?? []; if (plan.includes(r.taskId)) return dayChange(r.date, 0); dayPlans.set(r.date, [...plan, r.taskId]); record("Add to My Day"); return dayChange(r.date); },
  "myDay.remove": (r: { date: string; taskId: string }) => { dayPlans.set(r.date, (dayPlans.get(r.date) ?? []).filter(id => id !== r.taskId)); return dayChange(r.date); },
  "myDay.move": (r: { date: string; taskId: string; index: number }) => { const plan = (dayPlans.get(r.date) ?? []).filter(id => id !== r.taskId); plan.splice(r.index, 0, r.taskId); dayPlans.set(r.date, plan); return dayChange(r.date); },
  "myDay.carryOver": (r: { fromDate: string }) => { const next = addDays(r.fromDate, 1); const open = (dayPlans.get(r.fromDate) ?? []).filter(id => { const t = find(id); return t && !isFinished(t); }); dayPlans.set(next, [...new Set([...(dayPlans.get(next) ?? []), ...open])]); return dayChange(r.fromDate, open.length); },
  "myWeek.open": () => ok({ week: week() }),

  "subtasks.list": (r: { taskId: string }) => ok({ subtasks: subtasks.filter(s => s.taskId === r.taskId) }),
  "subtasks.listDeleted": () => ok({ subtasks: [] }),
  "subtasks.add": (r: { taskId: string; title: string; notes: string }) => { const s = { id: newId("sub"), taskId: r.taskId, title: r.title, notes: r.notes, status: "NotStarted", version: "1" }; subtasks.push(s); return ok({ subtask: s }); },
  "subtasks.edit": (r: { subtaskId: string; title: string; notes: string }) => { const i = subtasks.findIndex(s => s.id === r.subtaskId); subtasks[i] = { ...subtasks[i], title: r.title, notes: r.notes, version: bump(subtasks[i].version) }; record(`Edit subtask “${r.title}”`); return ok({ subtask: subtasks[i] }); },
  "subtasks.setStatus": (r: { subtaskId: string; status: string }) => { const i = subtasks.findIndex(s => s.id === r.subtaskId); subtasks[i] = { ...subtasks[i], status: r.status, version: bump(subtasks[i].version) }; return ok({ subtask: subtasks[i] }); },
  "subtasks.delete": (r: { subtaskId: string }) => { const i = subtasks.findIndex(s => s.id === r.subtaskId); const [s] = subtasks.splice(i, 1); return ok({ subtask: s }); },

  "tags.list": () => ok({ tags }),
  "tags.listDeleted": () => ok({ tags: [] }),
  "tags.targetTags": (r: { targetId: string }) => ok({ tagIds: taskTags.get(r.targetId) ?? [] }),
  "tags.suggest": () => ok({ tags }),

  "undo.list": (r: { limit: number }) => ok({ entries: undo.slice(0, r.limit) }),
  "undo.undo": () => { const entry = undo.shift(); return entry ? ok({ label: entry.label }) : { ...fail("There is nothing to undo."), label: null }; },

  "capture.preview": (r: { text: string; defaultContainerId: string | null }) => {
    const draft = previewDraft(r.text, r.defaultContainerId);
    return ok({ isBatch: false, errors: [], capturedAtUtc: now(), items: [{ lineNumber: 1, sourceStart: 0, text: r.text, draft, tokens: [], warnings: [], tags: [], containerCandidates: [], languages: [], needsTitle: !draft.title, firstOccurrence: null }] });
  },
  "capture.create": (r: { drafts: CaptureDraftDto[] }) => {
    const created = r.drafts.map(d => { const container = containers.find(c => c.id === (d.containerId ?? "inbox"));
      const t = task(newId("task"), d.title, { containerId: d.containerId ?? "inbox", category: d.category ?? container?.defaultCategory ?? "Personal", priority: d.priority, estimateMinutes: d.estimateMinutes, plannedDate: d.plannedDate, dueDate: d.dueDate });
      tasks.push(t); return t; });
    record(created.length === 1 ? `Add “${created[0].title}”` : `Add ${created.length} tasks`);
    return ok({ tasks: created });
  },

  "pad.list": (r: { archived: boolean }) => ok({ items: padNotes.filter(n => n.archived === r.archived) }),
  "pad.listDeleted": () => ok({ items: [] }),
  "pad.reviewStatus": () => ok({ due: false, lastReviewedAtUtc: null, suggestions: [] }),
  "pad.get": (r: { itemId: string }) => ok({ items: padNotes.filter(n => n.id === r.itemId) }),
  "pad.search": (r: { text: string }) => ok({ items: padNotes.filter(n => n.markdown.toLowerCase().includes(r.text.toLowerCase())) }),
  "pad.create": (r: { markdown: string }) => { const created: PadItemResponse = { ...padNotes[0], id: newId("pad"), markdown: r.markdown, version: "1", draftVersion: "0", tasks: [], tagIds: [] }; padNotes.push(created); return ok({ items: [created] }); },
  "pad.save": (r: { itemId: string; clientRevision: string; markdown: string }) => { const i = padNotes.findIndex(n => n.id === r.itemId); padNotes[i] = { ...padNotes[i], markdown: r.markdown, draftVersion: r.clientRevision, version: bump(padNotes[i].version) }; return ok({ item: padNotes[i], applied: true }); },
  "markdown.resolveLinks": () => ok({ targets: [] }),
  "pad.resolveLinks": () => ok({ targets: [] }),
  // One sample task was made from a pad note whose quote is a long link, like real ones often are.
  "pad.locate": (r: { taskId: string }) => ok({ anchor: r.taskId !== "task-1" ? null : { padItemId: "pad-1", taskId: "task-1", state: "Exact", start: 0, end: 80, archived: false, deleted: false,
    quote: "https://x.com/OpenAI/status/2120216592395174382?a=ryqqhK9R05Hs5Zv4HK0&s=20" } }),
  // One sample task is linked to a GitHub issue; "Assign me" adds the preview's own login to its assignees.
  "github.taskSync": (r: { taskId: string }) => { const linked = r.taskId === "task-2"; return { ok: true, error: null, connected: linked,
    assignedToMe: linked ? previewIssueAssignees.includes("preview-user") : null, dueDateManaged: false, hasConflict: false, issueUrl: linked ? "https://github.com/example/chaos/issues/42" : null,
    linkVersion: linked ? "1" : null, pending: false, postDiaryByDefault: false, repository: linked ? "example/chaos" : null, state: linked ? "Linked" : null, syncError: null, syncing: false,
    assignees: linked ? previewIssueAssignees : null }; },
  "github.setSelfAssignment": (r: { assigned: boolean }) => { previewIssueAssignees = r.assigned ? ["preview-user", ...previewIssueAssignees.filter(a => a !== "preview-user")] : previewIssueAssignees.filter(a => a !== "preview-user"); return ok({}); },
  "recurrence.get": () => ok({ rule: null, ended: false, occurrences: [] }),
  "waiting.get": () => ({ ok: true, error: null, task: null, person: null, startedAtUtc: null, followUp: null, followUpDueAtUtc: null, followUpSnoozedUntilUtc: null, followUpSuspension: "None", lastActivityAtUtc: null, replayed: false }),
};

/** Answers for reads the preview does not model: every list empty, every single item absent. */
const emptyRead = {
  ok: true, error: null, items: [], entries: [], tasks: [], rows: [], rules: [], prompts: [], targets: [], tagIds: [], templates: [], suggestions: [], subtasks: [], sections: [],
  containers: [], tags: [], views: [], activations: [], zones: [], occurrences: [], backups: [], imports: [], repositories: [], revisions: [], totalCount: 0, nextCursor: null,
  task: null, rule: null, anchor: null, settings: null, connection: null, sync: null, capabilities: null, version: null,
};

export async function handle(command: string, request: unknown): Promise<unknown> {
  await new Promise(resolve => setTimeout(resolve, 40)); // a little latency, like the real host
  const handler = handlers[command];
  if (!handler) { console.info(`[preview] ${command} is not modelled; answering with an empty read.`); return structuredClone(emptyRead); }
  return structuredClone(handler(request));
}
