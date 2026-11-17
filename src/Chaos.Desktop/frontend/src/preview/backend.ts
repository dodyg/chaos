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
// Board settings per container (PLAN2 P2): version 0 means "defaults, no row yet".
type PreviewBoardSettings = { groupBy: string; swimlaneBy: string; columnTagId: string | null; swimlaneTagId: string | null; wipLimitsJson: string; hiddenColumnsJson: string; version: number };
const boardSettings = new Map<string, PreviewBoardSettings>();
/** The container's manual task order; board moves reorder inside it. */
const taskOrder: string[] = [];
const subtasks: SubtaskResponse[] = [
  { id: "sub-1", taskId: "task-2", title: "Design the blocklist table", notes: "", status: "Done", version: "1" },
  { id: "sub-2", taskId: "task-2", title: "Add the admin screen", notes: "", status: "NotStarted", version: "1" },
];
const dayPlans = new Map<string, string[]>([[today, ["task-1", "task-2", "task-3", "task-4"]]]);
const undo: UndoEntryResponse[] = [];
/** A long trash, so the bounded deleted-notes list can be seen. */
const deletedPadNotes: PadItemResponse[] = Array.from({ length: 64 }, (_, i) => ({
  id: `pad-deleted-${i}`, markdown: i % 9 === 0 ? "" : ["Meeting notes", "Grocery list", "Idea: offline sync", "Reading list", "Draft reply to Sam"][i % 5] + ` ${i + 1}`,
  version: "2", draftVersion: "1", archived: false, archivedAtUtc: null, deleted: true, createdAtUtc: now(), updatedAtUtc: now(), tagIds: [], tasks: [],
}) as PadItemResponse);
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
    availableMinutes: 480, workAllowanceMinutes: 360, workMinutes: open.filter(t => t.category === "Work").reduce((sum, t) => sum + (t.estimateMinutes ?? 0), 0),
    busyMinutes: dayMeetings(date).filter(m => m.busy).length * 60, freeMinutes: Math.max(0, 480 - dayMeetings(date).filter(m => m.busy).length * 60), meetingsDontFit: false, meetings: dayMeetings(date) };
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
      return { date, availableMinutes: weekend ? 0 : 480, workAllowanceMinutes: weekend ? null : 360, isOverride: false, plannedMinutes: planned, workMinutes: work, unknownEstimates: 0, openCount: 0, overTotal: planned > (weekend ? 0 : 480), overWork: !weekend && work > 360, busyMinutes: dayMeetings(date).filter(m => m.busy).length * 60 }; }),
    pool: { plannedMinutes: 0, workMinutes: 0, unknownEstimates: 0, openCount: 0 } };
  return { weekStart: start, weekEnd: days[6], today, firstWeekday: "Monday", planId: "plan", state: "Confirmed", confirmedRevision: "1", confirmedAtUtc: now(),
    editable: true, availabilityConfigured: true, confirmed: board, draft: null, draftRevision: "0", draftBoard: null,
    lookBack: { weekStart: addDays(start, -7), weekEnd: addDays(start, -1), hadPlan: false, completedTaskIds: [], completedCount: 0, assignedCount: 0, assignedDone: 0, assignedCancelled: 0, unfinishedTaskIds: [], goals: [], summaryId: null, summaryCompleted: [] },
    candidates: [], goalSuggestions: [], pendingReconciliation: [], checkIn: null,
    tasks: tasks.map(t => ({ task: t, containerName: containerName(t.containerId), containerArchived: false })) };
}

/** The container's tasks in the current manual order, picking up tasks created later. */
function orderedTasks(containerId: string) {
  for (const row of tasks) if (row.containerId === containerId && !taskOrder.includes(row.id)) taskOrder.push(row.id);
  return tasks.filter(t => t.containerId === containerId).sort((a, b) => taskOrder.indexOf(a.id) - taskOrder.indexOf(b.id));
}
function boardDefaults(): PreviewBoardSettings { return { groupBy: "Status", swimlaneBy: "None", columnTagId: null, swimlaneTagId: null, wipLimitsJson: "{}", hiddenColumnsJson: "[]", version: 0 }; }
const settingsDto = (s: PreviewBoardSettings) => ({ groupBy: s.groupBy, swimlaneBy: s.swimlaneBy, columnTagId: s.columnTagId, swimlaneTagId: s.swimlaneTagId, wipLimitsJson: s.wipLimitsJson, hiddenColumnsJson: s.hiddenColumnsJson, version: String(s.version), isDefault: s.version === 0 });
const priorities = ["Urgent", "High", "Normal", "Low", "None"];
function boardColumns(s: PreviewBoardSettings, containerId: string, showCancelled: boolean) {
  if (s.groupBy === "Section") return [...(sections.get(containerId) ?? []).map(section => ({ key: section.id, title: section.title, isNone: false })), { key: "", title: "No section", isNone: true }];
  if (s.groupBy === "Priority") return [...priorities.map(p => ({ key: p, title: p, isNone: p === "None" }))];
  if (s.groupBy === "TagGroup") {
    const children = s.columnTagId ? tags.filter(t => t.parentId === s.columnTagId) : [];
    return [...children.map(t => ({ key: t.id, title: t.name, isNone: false })), { key: "", title: "None", isNone: true }];
  }
  const status = ["NotStarted", "InProgress", "WaitingOnSomeone", "NeedsReview", "Done"];
  const statusTitles: Record<string, string> = { NotStarted: "Not started", InProgress: "In progress", WaitingOnSomeone: "Waiting on someone", NeedsReview: "Needs review", Done: "Done" };
  return [...status.map(k => ({ key: k, title: statusTitles[k], isNone: false })), ...(showCancelled ? [{ key: "Cancelled", title: "Cancelled", isNone: false }] : [])];
}
function boardLanes(s: PreviewBoardSettings) {
  if (s.swimlaneBy === "Priority") return priorities.map(p => ({ key: p, title: p, isNone: p === "None" }));
  if (s.swimlaneBy === "TagGroup") {
    const children = s.swimlaneTagId ? tags.filter(t => t.parentId === s.swimlaneTagId) : [];
    return [...children.map(t => ({ key: t.id, title: t.name, isNone: false })), { key: "", title: "None", isNone: true }];
  }
  if (s.swimlaneBy === "Category") return [{ key: "Work", title: "Work", isNone: false }, { key: "Personal", title: "Personal", isNone: false }];
  return [{ key: "", title: "All tasks", isNone: true }];
}
function columnKeyOf(s: PreviewBoardSettings, t: TaskResponse): string {
  if (s.groupBy === "Section") return t.sectionId ?? "";
  if (s.groupBy === "Priority") return t.priority;
  if (s.groupBy === "TagGroup") { const ids = taskTags.get(t.id) ?? []; return tags.find(tag => tag.parentId === s.columnTagId && ids.includes(tag.id))?.id ?? ""; }
  return t.status;
}
function laneKeyOf(s: PreviewBoardSettings, t: TaskResponse): string {
  if (s.swimlaneBy === "Priority") return t.priority;
  if (s.swimlaneBy === "Category") return t.category;
  if (s.swimlaneBy === "TagGroup") { const ids = taskTags.get(t.id) ?? []; return tags.find(tag => tag.parentId === s.swimlaneTagId && ids.includes(tag.id))?.id ?? ""; }
  return "";
}

// Custom views (PLAN2 P3): an in-memory list and a small evaluator for the common fields, so the
// view screens render in the preview. The desktop host is the real implementation.
type PreviewView = { id: string; name: string; icon: string; color: string | null; pinned: boolean; position: number; definitionJson: string; layoutJson: string; alertsEnabled: boolean; version: string; deleted: boolean };
const savedViews: PreviewView[] = [{
  id: "view-1", name: "Open high priority", icon: "flag", color: "red", pinned: true, position: 0, alertsEnabled: false, version: "1", deleted: false,
  definitionJson: JSON.stringify({ v: 1, match: "all", items: [{ field: "status", op: "notIn", value: ["Done", "Cancelled"] }, { field: "priority", op: "atLeast", value: "High" }] }),
  layoutJson: JSON.stringify({ v: 1, kind: "table", group: "none", sort: { field: "due", dir: "asc" }, columns: ["title", "status", "priority", "due", "estimate"] }),
}];
const priorityOrder = ["None", "Low", "Normal", "High", "Urgent"];
function relativeRange(value: any): [string | null, string | null] {
  if (typeof value === "string") return [value, value];
  if (value && typeof value === "object" && typeof value.relative === "string") {
    switch (value.relative) {
      case "today": return [today, today];
      case "tomorrow": return [addDays(today, 1), addDays(today, 1)];
      case "yesterday": return [addDays(today, -1), addDays(today, -1)];
      case "next": return [today, addDays(today, value.days - 1)];
      case "last": return [addDays(today, -(value.days - 1)), today];
      case "overdue": return [null, addDays(today, -1)];
      default: return [today, addDays(today, 6)];
    }
  }
  return [value?.from ?? null, value?.to ?? null];
}
function previewMatch(t: TaskResponse, node: any): boolean {
  if (Array.isArray(node.items)) {
    const results = node.items.map((item: any) => previewMatch(t, item));
    return results.length === 0 || (node.match === "any" ? results.some(Boolean) : results.every(Boolean));
  }
  const date = (v: string | null) => {
    if (node.op === "isEmpty") return v === null;
    if (node.op === "isNotEmpty") return v !== null;
    if (v === null) return false;
    const [from, to] = relativeRange(node.value);
    if (node.op === "before") return from !== null && v < from;
    if (node.op === "after") return to !== null && v > to;
    return (from === null || v >= from) && (to === null || v <= to);
  };
  switch (node.field) {
    case "status": return node.op === "in" ? node.value.includes(t.status) : !node.value.includes(t.status);
    case "priority": { const a = priorityOrder.indexOf(t.priority), b = priorityOrder.indexOf(node.value); return node.op === "atLeast" ? a >= b : node.op === "atMost" ? a <= b : a === b; }
    case "container": return node.op === "isNot" ? t.containerId !== node.value : ([] as string[]).concat(node.value).includes(t.containerId);
    case "category": return t.category === node.value;
    case "due": return date(t.dueDate);
    case "planned": return date(t.plannedDate);
    case "estimate": return node.op === "isEmpty" ? t.estimateMinutes === null : t.estimateMinutes !== null && (node.op === "lessThan" ? t.estimateMinutes < node.value : t.estimateMinutes > node.value);
    case "text": return `${t.title} ${t.notes}`.toLowerCase().includes(String(node.value).toLowerCase());
    case "tag": { const ids = taskTags.get(t.id) ?? []; const wanted: string[] = node.value; return node.op === "hasAllOf" ? wanted.every(id => ids.includes(id)) : node.op === "hasNone" ? !wanted.some(id => ids.includes(id)) : wanted.some(id => ids.includes(id)); }
    default: return true;
  }
}
function viewRows(definitionJson: string, category: string | null) {
  const definition = JSON.parse(definitionJson);
  return tasks.filter(t => (!category || t.category === category) && previewMatch(t, definition)).map(t => row(t));
}
function viewHandlers(): Record<string, Handler> {
  const find = (id: string) => savedViews.find(v => v.id === id);
  const missing = { ...fail("That view no longer exists."), view: null };
  return {
    "views.list": (r: { includeDeleted: boolean }) => ok({ views: savedViews.filter(v => r.includeDeleted || !v.deleted).sort((a, b) => a.position - b.position), icons: ["list", "star", "flag", "bug", "calendar", "clock", "inbox", "briefcase", "home", "user", "users", "tag", "bookmark", "heart", "target", "zap", "bell", "square-check", "folder", "sun"] }),
    "views.get": (r: { viewId: string }) => { const v = find(r.viewId); return v ? ok({ view: v }) : missing; },
    "views.save": (r: { viewId: string | null; name: string; icon: string | null; color: string | null; definitionJson: string; layoutJson: string | null; pinned: boolean; alertsEnabled: boolean }) => {
      if (!r.name.trim()) return { ...fail("Give the view a name of 1 to 80 characters."), view: null };
      try { JSON.parse(r.definitionJson); } catch { return { ...fail("The view definition is not valid JSON."), view: null }; }
      const existing = r.viewId ? find(r.viewId) : undefined;
      if (r.viewId && !existing) return missing;
      const next: PreviewView = { id: existing?.id ?? newId("view"), name: r.name.trim(), icon: r.icon ?? "list", color: r.color, pinned: r.pinned, position: existing?.position ?? savedViews.length, definitionJson: r.definitionJson, layoutJson: r.layoutJson ?? "{\"v\":1}", alertsEnabled: r.alertsEnabled, version: existing ? bump(existing.version) : "1", deleted: false };
      if (existing) savedViews[savedViews.indexOf(existing)] = next; else savedViews.push(next);
      return ok({ view: next });
    },
    "views.duplicate": (r: { viewId: string }) => { const v = find(r.viewId); if (!v) return missing; const copy = { ...v, id: newId("view"), name: `${v.name} (copy)`, position: savedViews.length, alertsEnabled: false, version: "1" }; savedViews.push(copy); return ok({ view: copy }); },
    "views.setPinned": (r: { viewId: string; pinned: boolean }) => { const v = find(r.viewId); if (!v) return missing; v.pinned = r.pinned; v.version = bump(v.version); return ok({ view: v }); },
    "views.delete": (r: { viewId: string }) => { const v = find(r.viewId); if (!v) return missing; v.deleted = true; v.version = bump(v.version); return ok({ view: v }); },
    "views.restore": (r: { viewId: string }) => { const v = find(r.viewId); if (!v) return missing; v.deleted = false; v.version = bump(v.version); return ok({ view: v }); },
    "views.reorder": (r: { viewIds: string[] }) => { r.viewIds.forEach((id, index) => { const v = find(id); if (v) v.position = index; }); return ok({ views: [...savedViews].sort((a, b) => a.position - b.position), icons: [] }); },
    "views.run": (r: { viewId: string; category: string | null }) => { const v = find(r.viewId); if (!v || v.deleted) return { ...fail("That view no longer exists."), rows: [], totalCount: 0, nextCursor: null, layoutJson: null }; const rows = viewRows(v.definitionJson, r.category); return ok({ rows, totalCount: rows.length, nextCursor: null, layoutJson: v.layoutJson }); },
    "views.preview": (r: { definitionJson: string; layoutJson: string | null; category: string | null; limit: number | null }) => {
      try { const rows = viewRows(r.definitionJson, r.category); return ok({ rows: rows.slice(0, r.limit ?? 5), totalCount: rows.length, nextCursor: null, layoutJson: r.layoutJson }); }
      catch { return { ...fail("This view cannot be previewed."), rows: [], totalCount: 0, nextCursor: null, layoutJson: null }; }
    },
    "views.counts": (r: { category: string | null }) => ok({ counts: savedViews.filter(v => v.pinned && !v.deleted).map(v => ({ viewId: v.id, count: viewRows(v.definitionJson, r.category).length })) }),
    "views.contextDefaults": () => ok({ containerId: null, status: null, priority: null, category: null, tagIds: [], plannedDate: null, dueDate: null }),
    "views.export": (r: { viewId: string }) => { const v = find(r.viewId); return v ? ok({ fileName: `${v.name} preview.csv`, rows: viewRows(v.definitionJson, null).length }) : { ...fail("That view no longer exists."), fileName: null, rows: 0 }; },
    "views.alerts": () => ok({ alerts: [] }),
    "views.acknowledgeAlert": () => ok({}),
    "views.githubOptions": () => ok({ repositories: [{ id: "repo-1", fullName: "person/chaos" }] }),
    "views.fromSearch": (r: { search: { text: string | null } }) => ok({ definitionJson: JSON.stringify({ v: 1, match: "all", items: [{ field: "text", op: "contains", value: r.search.text ?? "" }, { field: "status", op: "notIn", value: ["Done", "Cancelled"] }] }) }),
  };
}
// Calendar feeds (PLAN2 P5): one sample feed, kept in memory. The address is never kept after adding.
type PreviewSource = { id: string; provider: string; name: string; color: string; countsAsBusy: boolean; enabled: boolean; ownerEmail: string | null; version: string; lastAttemptUtc: string | null; lastSuccessUtc: string | null; lastError: string | null };
const calendarSources: PreviewSource[] = [
  { id: "cal-team", provider: "Feed", name: "Team calendar", color: "#3b82f6", countsAsBusy: true, enabled: true, ownerEmail: null, version: "1", lastAttemptUtc: now(), lastSuccessUtc: now(), lastError: null },
];
type PreviewMeeting = { sourceId: string; eventId: string; title: string; offset: number; start: string | null; end: string | null; location: string | null; busy: boolean };
const meetingSamples: PreviewMeeting[] = [
  { sourceId: "cal-team", eventId: "m1", title: "Stand-up", offset: 0, start: "10:00", end: "11:00", location: "Room 2", busy: true },
  { sourceId: "cal-team", eventId: "m2", title: "Design review", offset: 0, start: "14:00", end: "15:30", location: null, busy: true },
  { sourceId: "cal-team", eventId: "m3", title: "Planning", offset: 1, start: "11:00", end: "12:00", location: null, busy: true },
  { sourceId: "cal-team", eventId: "m4", title: "Company holiday", offset: 3, start: null, end: null, location: null, busy: false },
];
function dayMeetings(date: string) {
  return meetingSamples.flatMap(m => {
    const source = calendarSources.find(s => s.id === m.sourceId);
    if (!source || !source.enabled || addDays(today, m.offset) !== date) return [];
    return [{ id: `${m.sourceId}|${m.eventId}|${date}`, sourceId: m.sourceId, eventId: m.eventId, sourceName: source.name, title: m.title, date, start: m.start, end: m.end, allDay: m.start === null,
      color: source.color, busy: m.busy && source.countsAsBusy, location: m.location, videoUrl: null, webUrl: m.eventId === "m1" ? "https://example.com/standup" : null }];
  });
}
const meetingTaskLinks = new Map<string, { taskId: string; eventTitle: string; eventStartUtc: string; eventWebUrl: string | null }>();
function calendarsHandlers(): Record<string, Handler> {
  return {
    "calendars.list": () => ok({ sources: calendarSources }),
    "calendars.addFeed": (r: { address: string; name: string; color: string; countsAsBusy: boolean; ownerEmail: string | null }) => {
      if (/^http:\/\//i.test(r.address.trim())) return { ok: false, source: null, refreshError: null, error: { kind: "Validation", message: "Calendar feeds must use https://. Plain http:// is not allowed.", fields: [{ fieldPath: "address", message: "Calendar feeds must use https://. Plain http:// is not allowed." }], correlationId: "preview" } };
      const source: PreviewSource = { id: newId("cal"), provider: "Feed", name: r.name, color: r.color, countsAsBusy: r.countsAsBusy, enabled: true, ownerEmail: r.ownerEmail, version: "1", lastAttemptUtc: now(), lastSuccessUtc: now(), lastError: null };
      calendarSources.push(source);
      return ok({ source, refreshError: null });
    },
    "calendars.update": (r: { sourceId: string; expectedVersion: string; name: string; color: string; countsAsBusy: boolean; enabled: boolean; ownerEmail: string | null }) => {
      const index = calendarSources.findIndex(s => s.id === r.sourceId);
      if (index < 0) return { ...fail("That calendar was removed. Refresh and try again."), source: null, refreshError: null };
      const next = { ...calendarSources[index], name: r.name, color: r.color, countsAsBusy: r.countsAsBusy, enabled: r.enabled, ownerEmail: r.ownerEmail, version: String(Number(calendarSources[index].version) + 1) };
      calendarSources[index] = next;
      return ok({ source: next, refreshError: null });
    },
    "calendars.remove": (r: { sourceId: string }) => { const index = calendarSources.findIndex(s => s.id === r.sourceId); if (index >= 0) calendarSources.splice(index, 1); return ok({}); },
    "calendars.activity": () => ok({ google: false, feed: false }),
    "calendars.refresh": () => { for (const s of calendarSources) { s.lastAttemptUtc = now(); s.lastSuccessUtc = now(); s.lastError = null; } return ok({ results: calendarSources.map(s => ({ sourceId: s.id, ok: true, changed: false, eventCount: 0, error: null })), sources: calendarSources }); },
    "calendars.makeTask": (r: { sourceId: string; eventId: string }) => {
      const sample = meetingSamples.find(m => m.sourceId === r.sourceId && m.eventId === r.eventId);
      const t = task(newId("task"), `Prepare: ${sample?.title ?? "meeting"}`, { plannedDate: addDays(today, (sample?.offset ?? 0) - 1) });
      tasks.push(t); record(`Make task “${t.title}”`);
      meetingTaskLinks.set(t.id, { taskId: t.id, eventTitle: sample?.title ?? "meeting", eventStartUtc: now(), eventWebUrl: null });
      return ok({ task: t, replayed: false });
    },
    "calendars.taskLinks": (r: { taskIds: string[] }) => ok({ links: r.taskIds.flatMap(id => meetingTaskLinks.get(id) ?? []) }),
  };
}
// Google Calendar (PLAN2 P6): a fake account and two calendars; nothing leaves the page. Sources of the account are CalendarSources with provider Google.
type PreviewGoogleAccount = { id: string; email: string; signedInHere: boolean; needsSignIn: boolean; error: string | null; lastSuccessUtc: string | null };
const googleAccounts: PreviewGoogleAccount[] = [];
const googleCalendarList = [
  { calendarId: "you@example.com", name: "you@example.com", color: "#16a765", primary: true },
  { calendarId: "team@example.com", name: "Team (shared)", color: "#9a9cff", primary: false },
];
function googleAccountView(account: PreviewGoogleAccount) {
  return { ...account, calendars: calendarSources.filter(s => s.provider === "Google") };
}
let googleConfigured = false;
function googleHandlers(): Record<string, Handler> {
  return {
    "googleCalendar.setup": () => { googleConfigured = true; return ok({ revoked: true }); },
    "googleCalendar.status": () => ok({ configured: googleConfigured, accounts: googleAccounts.map(googleAccountView) }),
    "googleCalendar.connect": () => {
      let account = googleAccounts[0];
      if (!account) { account = { id: "acct-1", email: "you@example.com", signedInHere: true, needsSignIn: false, error: null, lastSuccessUtc: now() }; googleAccounts.push(account); }
      account.signedInHere = true; account.needsSignIn = false; account.error = null;
      return ok({ account: googleAccountView(account) });
    },
    "googleCalendar.calendars": () => ok({ calendars: googleCalendarList.map(c => {
      const source = calendarSources.find(s => s.id === `g-${c.calendarId}`) ?? null;
      return { ...c, chosen: !!source?.enabled, source };
    }) }),
    "googleCalendar.choose": (r: { accountId: string; calendarId: string; name: string; color: string | null; chosen: boolean }) => {
      const id = `g-${r.calendarId}`;
      const index = calendarSources.findIndex(s => s.id === id);
      const next: PreviewSource = index >= 0
        ? { ...calendarSources[index], enabled: r.chosen, version: String(Number(calendarSources[index].version) + 1) }
        : { id, provider: "Google", name: r.name, color: r.color ?? "#3b82f6", countsAsBusy: true, enabled: r.chosen, ownerEmail: null, version: "1", lastAttemptUtc: now(), lastSuccessUtc: now(), lastError: null };
      if (index >= 0) calendarSources[index] = next; else calendarSources.push(next);
      return ok({ source: next, refreshError: null });
    },
    "googleCalendar.disconnect": (r: { accountId: string }) => {
      const index = googleAccounts.findIndex(a => a.id === r.accountId);
      if (index >= 0) googleAccounts.splice(index, 1);
      for (let i = calendarSources.length - 1; i >= 0; i--) if (calendarSources[i].provider === "Google") calendarSources.splice(i, 1);
      return ok({ revoked: true });
    },
  };
}
function calendarHandlers(): Record<string, Handler> {
  type Filters = { category?: string | null; tagId?: string | null; containerId?: string | null; viewId?: string | null; showPlanned?: boolean; showDue?: boolean; showMeetings?: boolean; showCompleted?: boolean } | null;
  const matches = (t: TaskResponse, f: Filters) => (!f?.category || t.category === f.category) && (!f?.containerId || t.containerId === f.containerId) && (!f?.tagId || (taskTags.get(t.id)?.includes(f.tagId) ?? false));
  const endOf = (time: string, estimate: number | null) => { const [h, m] = time.split(":").map(Number); const total = Math.min(h * 60 + m + (estimate && estimate > 0 ? estimate : 30), 24 * 60 - 1); return `${String(Math.floor(total / 60)).padStart(2, "0")}:${String(total % 60).padStart(2, "0")}`; };
  const entry = (kind: string, t: TaskResponse, date: string, time: string | null) => ({ kind, taskId: t.id, title: t.title, status: t.status, priority: t.priority, containerName: containerName(t.containerId), date, time,
    endTime: kind === "Planned" && time ? endOf(time, t.estimateMinutes) : null, isBlock: kind === "Planned" && !!time && t.estimateMinutes !== null, isEstimated: t.estimateMinutes !== null, estimateMinutes: t.estimateMinutes, category: t.category });
  return {
    "calendar.range": (r: { from: string; to: string; filters: Filters }) => {
      const f = r.filters; const finished = (t: TaskResponse) => t.status === "Done" || t.status === "Cancelled";
      const inRange = (d: string | null) => d !== null && d >= r.from && d <= r.to;
      const entries = [
        ...(f?.showPlanned === false ? [] : tasks.filter(t => matches(t, f) && inRange(t.plannedDate) && (f?.showCompleted ? t.status !== "Cancelled" : !finished(t))).map(t => entry("Planned", t, t.plannedDate!, t.plannedTime))),
        ...(f?.showDue === false ? [] : tasks.filter(t => matches(t, f) && inRange(t.dueDate) && (f?.showCompleted ? t.status !== "Cancelled" : !finished(t))).map(t => entry("Due", t, t.dueDate!, t.dueTime))),
      ].sort((a, b) => a.date.localeCompare(b.date) || (a.time ?? "").localeCompare(b.time ?? ""));
      const days: unknown[] = [];
      for (let d = r.from; d <= r.to; d = new Date(Date.parse(`${d}T00:00:00Z`) + 86_400_000).toISOString().slice(0, 10)) {
        const weekend = [0, 6].includes(new Date(`${d}T00:00:00Z`).getUTCDay()); const available = weekend ? 0 : 480;
        const planned = tasks.filter(t => (!f?.category || t.category === f.category) && t.plannedDate === d && !finished(t));
        const minutes = planned.reduce((sum, t) => sum + (t.estimateMinutes ?? 0), 0);
        days.push({ date: d, availableMinutes: available, workAllowanceMinutes: null, busyMinutes: dayMeetings(d).filter(m => m.busy).length * 60, plannedMinutes: minutes, workMinutes: 0, unknownEstimates: planned.filter(t => t.estimateMinutes === null).length, openCount: planned.length, overTotal: minutes > available || (available === 0 && planned.length > 0), overWork: false });
      }
      return ok({ from: r.from, to: r.to, today, zoneId: "UTC", entries, meetings: f?.showMeetings === false ? [] : (() => { const all: ReturnType<typeof dayMeetings> = []; for (let d = r.from; d <= r.to; d = addDays(d, 1)) all.push(...dayMeetings(d)); return all; })(), days, truncated: false });
    },
    "calendar.unscheduled": (r: { filters: Filters; text: string | null }) => {
      const rows = tasks.filter(t => matches(t, r.filters) && t.plannedDate === null && t.status !== "Done" && t.status !== "Cancelled" && (!r.text || t.title.toLowerCase().includes(r.text.toLowerCase())))
        .map(t => ({ task: t, containerName: containerName(t.containerId), containerArchived: false, tagIds: [...(taskTags.get(t.id) ?? [])], matches: [], groupDate: null, waitingPerson: null, nextFollowUpAtUtc: null }));
      return ok({ rows, totalCount: rows.length, nextCursor: null });
    },
  };
}
function boardHandlers(): Record<string, Handler> {
  return {
    "board.get": (r: { containerId: string; category: string | null; showCancelled: boolean }) => {
      const container = containers.find(c => c.id === r.containerId);
      if (!container) return { ...fail("That list or project no longer exists."), settings: settingsDto(boardDefaults()), columns: [], lanes: [], cards: [], totalCount: 0, truncated: false };
      const s = boardSettings.get(r.containerId) ?? boardDefaults();
      const visible = orderedTasks(r.containerId)
        .filter(t => !r.category || t.category === r.category)
        .filter(t => r.showCancelled || t.status !== "Cancelled");
      const cards = visible.map(t => {
        const subs = subtasks.filter(sub => sub.taskId === t.id);
        return { task: t, tagIds: [...(taskTags.get(t.id) ?? [])], subtasksTotal: subs.length, subtasksDone: subs.filter(sub => sub.status === "Done").length,
          hasIssueLink: t.id === "task-2", columnKey: columnKeyOf(s, t), laneKey: laneKeyOf(s, t) };
      });
      return ok({ settings: settingsDto(s), columns: boardColumns(s, r.containerId, r.showCancelled), lanes: boardLanes(s), cards, totalCount: cards.length, truncated: false });
    },
    "board.getView": (r: { viewId: string; category: string | null; showCancelled: boolean }) => {
      const view = savedViews.find(v => v.id === r.viewId && !v.deleted);
      if (!view) return { ...fail("That view no longer exists."), settings: settingsDto(boardDefaults()), columns: [], lanes: [], cards: [], totalCount: 0, truncated: false };
      const found = boardSettings.get(r.viewId) ?? boardDefaults();
      const s = found.groupBy === "Section" ? { ...found, groupBy: "Status" } : found;
      const visible = viewRows(view.definitionJson, r.category).map(row => row.task).filter(t => r.showCancelled || t.status !== "Cancelled");
      const lanes = s.swimlaneBy === "Container"
        ? [...new Set(visible.map(t => t.containerId))].map(id => ({ key: id, title: containerName(id), isNone: false }))
        : boardLanes(s);
      const cards = visible.map(t => {
        const subs = subtasks.filter(sub => sub.taskId === t.id);
        return { task: t, tagIds: [...(taskTags.get(t.id) ?? [])], subtasksTotal: subs.length, subtasksDone: subs.filter(sub => sub.status === "Done").length,
          hasIssueLink: t.id === "task-2", columnKey: columnKeyOf(s, t), laneKey: s.swimlaneBy === "Container" ? t.containerId : laneKeyOf(s, t) };
      });
      return ok({ settings: settingsDto(found), columns: boardColumns(s, "", r.showCancelled), lanes, cards, totalCount: cards.length, truncated: false });
    },
    "board.updateSettings": (r: { operationId: string; targetId: string; expectedVersion: string; groupBy: string; swimlaneBy: string; columnTagId: string | null; swimlaneTagId: string | null; wipLimitsJson: string; hiddenColumnsJson: string }) => {
      const existing = boardSettings.get(r.targetId);
      if ((existing ?? boardDefaults()).version !== Number(r.expectedVersion)) return { ...fail("This board's settings changed elsewhere. Refresh and try again."), settings: null };
      const saved: PreviewBoardSettings = { groupBy: r.groupBy, swimlaneBy: r.swimlaneBy, columnTagId: r.columnTagId || null, swimlaneTagId: r.swimlaneTagId || null,
        wipLimitsJson: r.wipLimitsJson || "{}", hiddenColumnsJson: r.hiddenColumnsJson || "[]", version: (existing?.version ?? 0) + 1 };
      boardSettings.set(r.targetId, saved);
      record("Change board settings");
      return ok({ settings: settingsDto(saved) });
    },
    "board.move": (r: { taskId: string; expectedVersion: string; groupBy: string; fromColumn: string | null; toColumn: string | null; beforeTaskId: string | null; afterTaskId: string | null; subtaskChoice: string | null }) => {
      const t = find(r.taskId);
      if (!t) return { ...fail("That task no longer exists."), task: null, warning: null };
      if (t.version !== r.expectedVersion) return { ...fail("This task changed elsewhere while you were working. Refresh and try again."), task: null, warning: null };
      // The card lands next to the anchor card the person dropped it beside (as BoardCommands).
      const anchor = r.beforeTaskId ?? r.afterTaskId;
      if (anchor && !orderedTasks(t.containerId).some(row => row.id === anchor && row.id !== t.id)) {
        return { ...fail("The card it was placed next to is no longer in this list or project. Refresh and try again."), task: null, warning: null };
      }
      if (r.groupBy === "Status" && r.toColumn === "Done" && r.subtaskChoice === null
        && subtasks.some(sub => sub.taskId === t.id && sub.status !== "Done" && sub.status !== "Cancelled")) {
        return { ok: false, task: null, warning: null, error: { kind: "Validation", message: "Open subtasks", fields: [{ fieldPath: "openSubtaskChoice", message: "Choose how to handle the open subtasks." }], correlationId: "preview" } };
      }
      // One field change, one reorder, one history entry for the whole move (as BoardCommands).
      const patch: { -readonly [K in keyof TaskResponse]?: TaskResponse[K] } = {};
      if (r.groupBy === "Status") {
        if (!boardColumns(boardDefaults(), t.containerId, true).some(column => column.key === r.toColumn)) return { ...fail("That column is not a status."), task: null, warning: null };
        if (r.toColumn !== t.status) patch.status = r.toColumn!;
        if (r.toColumn === "Done" && r.subtaskChoice === "CompleteAll") {
          subtasks.forEach((sub, index) => {
            if (sub.taskId === t.id && sub.status !== "Done" && sub.status !== "Cancelled") subtasks[index] = { ...sub, status: "Done", version: bump(sub.version) };
          });
        }
      } else if (r.groupBy === "Section") {
        if ((r.toColumn || null) !== t.sectionId) patch.sectionId = r.toColumn || null;
      } else if (r.groupBy === "Priority") {
        if (r.toColumn && r.toColumn !== t.priority) patch.priority = r.toColumn;
      }
      let retagged = false;
      if (r.groupBy === "TagGroup" && r.fromColumn !== r.toColumn) {
        const links = (taskTags.get(r.taskId) ?? []).filter(id => id !== r.fromColumn);
        if (r.toColumn) links.push(r.toColumn);
        taskTags.set(r.taskId, links);
        retagged = true;
      }
      const before = orderedTasks(t.containerId).map(row => row.id);
      if (anchor) {
        taskOrder.splice(taskOrder.indexOf(t.id), 1);
        taskOrder.splice(taskOrder.indexOf(anchor) + (r.afterTaskId ? 1 : 0), 0, t.id);
      }
      const after = orderedTasks(t.containerId).map(row => row.id);
      // Every reranked card gets a new version, as the real reorder bumps its neighbours.
      const reranked = new Set(after.filter((id, index) => before[index] !== id));
      const changed = Object.keys(patch).length > 0 || retagged || reranked.has(t.id);
      if (!changed) return ok({ task: t, warning: null });
      for (const id of new Set([...reranked, t.id])) {
        const index = tasks.findIndex(row => row.id === id);
        tasks[index] = { ...tasks[index], ...(id === t.id ? patch : {}), version: bump(tasks[index].version), updatedAtUtc: now() };
      }
      record(`Move “${t.title}”`);
      return ok({ task: find(r.taskId), warning: null });
    },
  };
}

function previewDraft(text: string, containerId: string | null): CaptureDraftDto {
  const category = /(^|\s)~(work|w)\b/i.test(text) ? "Work" : /(^|\s)~(personal|p|home)\b/i.test(text) ? "Personal" : null;
  const tagPaths = [...text.matchAll(/(^|\s)#([\w/-]+)/g)].map(m => m[2]);
  const title = text.replace(/(^|\s)[~#][\w/-]+/g, " ").replace(/\s+/g, " ").trim();
  return { title, notes: null, containerId, priority: "Normal", estimateMinutes: null, plannedDate: null, plannedTime: null, dueDate: null, dueTime: null, zoneId: "UTC",
    recurrence: null, reminders: [], tagIds: [], tagPaths, waitingOn: null, followUpDate: null, followUpTime: null, subtasks: [], category };
}

/** Pages in the preview: a small tree, a couple of daily pages, versions and backlinks. */
type PreviewPage = { id: string; parentPageId: string | null; title: string; markdown: string; position: number; dailyDate: string | null; version: number; draftVersion: number;
  deleted: boolean; createdAtUtc: string; updatedAtUtc: string; revisions: { number: number; title: string; markdown: string; createdAtUtc: string; updatedAtUtc: string }[] };
const pageId = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, "0")}`;
const pages: PreviewPage[] = [];
function seedPage(id: string, title: string, markdown: string, patch: Partial<PreviewPage> = {}) {
  const at = now();
  pages.push({ id, parentPageId: null, title, markdown, position: pages.length * 1024, dailyDate: null, version: 1, draftVersion: 0, deleted: false, createdAtUtc: at, updatedAtUtc: at,
    revisions: [{ number: 1, title, markdown, createdAtUtc: at, updatedAtUtc: at }], ...patch });
}
seedPage(pageId(1), "Kitchen renovation", "---\nicon: 🛠️\nbackground: lighthouse\ntags: [ideas]\naliases: [Kitchen]\n---\n\n# Plan\n\nNew tiles and a sink. Ask the [[Plumbers]] for quotes.\n\n## Budget\n\n- Tiles\n  - Floor\n  - Wall\n- Sink\n- Labour\n\n## Notes\n\nLink to the task: [Water the plants](chaos:task/task-4).");
seedPage(pageId(2), "Quotes", "Three quotes so far.", { parentPageId: pageId(1) });
seedPage(pageId(3), "Plumbers", "- Ahmed: Thursday\n- Rossi & Sons: next week", { parentPageId: pageId(2) });
seedPage(pageId(4), "Recipes", "---\nicon: 🍲\n---\nSoups and breads.");
seedPage(pageId(5), addDays(today, -1), "Good day. Finished the proposal draft.", { dailyDate: addDays(today, -1) });

function pageIcon(markdown: string) {
  const match = /^---\r?\n[\s\S]*?^icon:\s*"?([^"\n]+?)"?\s*$[\s\S]*?^---/m.exec(markdown);
  return match && markdown.startsWith("---") ? match[1] : null;
}
function pageAliases(markdown: string) {
  const match = /^aliases:\s*\[([^\]]*)\]/m.exec(markdown.startsWith("---") ? markdown.split(/\n---/)[0] : "");
  return match ? match[1].split(",").map(item => item.trim()).filter(Boolean) : [];
}
function pageSummary(page: PreviewPage) {
  return { id: page.id, parentPageId: page.parentPageId, title: page.title, position: String(page.position), dailyDate: page.dailyDate, icon: pageIcon(page.markdown), aliases: pageAliases(page.markdown),
    version: String(page.version), updatedAtUtc: page.updatedAtUtc, deleted: page.deleted, wordCount: (page.markdown.match(/[\p{L}\p{N}]+/gu) ?? []).length };
}
function pageView(page: PreviewPage) {
  const needles = [`chaos:page/${page.id}`, ...[page.title, ...pageAliases(page.markdown)].map(name => `[[${name.toLowerCase()}]]`)];
  const line = (text: string) => text.split("\n").find(row => needles.some(needle => row.toLowerCase().includes(needle))) ?? "";
  const backlinks = [
    ...pages.filter(other => other.id !== page.id && !other.deleted && needles.some(needle => other.markdown.toLowerCase().includes(needle))).map(other => ({ kind: "page", id: other.id, title: other.title, line: line(other.markdown) })),
    ...tasks.filter(item => needles.some(needle => item.notes.toLowerCase().includes(needle))).map(item => ({ kind: "task", id: item.id, title: item.title, line: line(item.notes) })),
  ];
  return { ...pageSummary(page), markdown: page.markdown, draftVersion: String(page.draftVersion), createdAtUtc: page.createdAtUtc, tagIds: [], tasks: [], backlinks };
}
function addPage(title: string, markdown: string, parentPageId: string | null, dailyDate: string | null) {
  const id = pageId(++sequence);
  seedPage(id, title, markdown, { parentPageId, dailyDate, position: (Math.max(-1024, ...pages.filter(p => p.parentPageId === parentPageId).map(p => p.position)) + 1024) });
  record(dailyDate ? "Start daily page" : `Add page “${title}”`);
  return pages.find(p => p.id === id)!;
}
function pageHandlers(): Record<string, Handler> {
  const live = () => pages.filter(p => !p.deleted);
  const find = (id: string) => pages.find(p => p.id === id);
  return {
    "page.list": () => ok({ pages: live().filter(p => !p.dailyDate).map(pageSummary) }),
    "page.listDaily": (r: { from: string | null; to: string | null }) => ok({ pages: live().filter(p => p.dailyDate && (!r.from || p.dailyDate >= r.from) && (!r.to || p.dailyDate <= r.to)).sort((a, b) => b.dailyDate!.localeCompare(a.dailyDate!)).map(pageSummary) }),
    "page.listDeleted": () => ok({ pages: pages.filter(p => p.deleted).map(pageSummary) }),
    "page.get": (r: { pageId: string }) => { const page = find(r.pageId); return page ? ok({ page: pageView(page) }) : { ...fail("That page no longer exists."), page: null }; },
    "page.getDaily": (r: { date: string }) => { const page = live().find(p => p.dailyDate === r.date); return ok({ page: page ? pageView(page) : null }); },
    "page.create": (r: { title: string; markdown: string; parentPageId: string | null }) => r.title.trim() ? ok({ pages: [pageView(addPage(r.title.trim(), r.markdown, r.parentPageId, null))] }) : { ...fail("Give the page a title."), pages: [] },
    "page.createDaily": (r: { date: string; markdown: string }) => ok({ pages: [pageView(live().find(p => p.dailyDate === r.date) ?? addPage(r.date, r.markdown, null, r.date))] }),
    "page.save": (r: { pageId: string; clientRevision: string; title: string; markdown: string }) => {
      const page = find(r.pageId)!;
      if (Number(r.clientRevision) <= page.draftVersion) return ok({ page: pageView(page), applied: false });
      Object.assign(page, { title: page.dailyDate ? page.title : r.title, markdown: r.markdown, draftVersion: Number(r.clientRevision), version: page.version + 1, updatedAtUtc: now() });
      const latest = page.revisions[0];
      if (Date.now() - Date.parse(latest.updatedAtUtc) < 10 * 60_000) Object.assign(latest, { title: page.title, markdown: page.markdown, updatedAtUtc: now() });
      else page.revisions.unshift({ number: latest.number + 1, title: page.title, markdown: page.markdown, createdAtUtc: now(), updatedAtUtc: now() });
      return ok({ page: pageView(page), applied: true });
    },
    "page.move": (r: { pageId: string; parentPageId: string | null; insertionIndex: number }) => {
      const page = find(r.pageId)!;
      page.parentPageId = r.parentPageId;
      const siblings = live().filter(p => !p.dailyDate && p.parentPageId === r.parentPageId && p.id !== page.id).sort((a, b) => a.position - b.position);
      siblings.splice(Math.max(0, Math.min(r.insertionIndex, siblings.length)), 0, page);
      siblings.forEach((p, index) => { p.position = index * 1024; p.version++; });
      record(`Move page “${page.title}”`);
      return ok({ pages: [pageView(page)] });
    },
    "page.delete": (r: { pageId: string }) => {
      const ids = new Set([r.pageId]);
      for (let grew = true; grew;) { grew = false; for (const p of pages) if (p.parentPageId && ids.has(p.parentPageId) && !ids.has(p.id)) { ids.add(p.id); grew = true; } }
      for (const p of pages) if (ids.has(p.id)) { p.deleted = true; p.version++; }
      record(`Delete page “${find(r.pageId)!.title}”`);
      return ok({ pages: pages.filter(p => ids.has(p.id)).map(pageView) });
    },
    "page.restore": (r: { pageId: string }) => { const page = find(r.pageId)!; page.deleted = false; page.version++; if (page.parentPageId && find(page.parentPageId)?.deleted) page.parentPageId = null; return ok({ pages: [pageView(page)] }); },
    "page.revisions": (r: { pageId: string }) => ok({ revisions: (find(r.pageId)?.revisions ?? []).map(v => ({ ...v, number: String(v.number) })) }),
    "page.restoreVersion": (r: { pageId: string; number: string }) => {
      const page = find(r.pageId)!;
      const version = page.revisions.find(v => String(v.number) === r.number)!;
      Object.assign(page, { title: page.dailyDate ? page.title : version.title, markdown: version.markdown, draftVersion: page.draftVersion + 1, version: page.version + 1, updatedAtUtc: now() });
      page.revisions.unshift({ number: page.revisions[0].number + 1, title: page.title, markdown: page.markdown, createdAtUtc: now(), updatedAtUtc: now() });
      record(`Restore an earlier version of “${page.title}”`);
      return ok({ pages: [pageView(page)] });
    },
    "page.search": (r: { text: string }) => { const key = r.text.toLowerCase(); return ok({ pages: live().filter(p => p.title.toLowerCase().includes(key) || p.markdown.toLowerCase().includes(key) || pageAliases(p.markdown).some(a => a.toLowerCase().includes(key))).map(pageSummary) }); },
    "page.byTag": (r: { tagId: string }) => ok({ pages: live().filter(p => r.tagId === "t-ideas" && /tags:.*ideas/.test(p.markdown)).map(pageSummary) }),
    "page.resolveLinks": (r: { pageIds: string[] }) => ok({ targets: pages.filter(p => r.pageIds.some(id => id.toLowerCase() === p.id)).map(p => ({ id: p.id, title: p.title, icon: pageIcon(p.markdown), deleted: p.deleted, dailyDate: p.dailyDate, name: null })) }),
    "page.resolveNames": (r: { names: string[] }) => ok({ targets: r.names.flatMap(name => { const page = live().find(p => p.title.toLowerCase() === name.toLowerCase() || pageAliases(p.markdown).some(a => a.toLowerCase() === name.toLowerCase()));
      return page ? [{ id: page.id, title: page.title, icon: pageIcon(page.markdown), deleted: false, dailyDate: page.dailyDate, name }] : []; }) }),
    "page.mentions": (r: { taskId: string }) => ok({ pages: live().filter(p => p.markdown.includes(`chaos:task/${r.taskId}`)).map(p => ({ kind: "page", id: p.id, title: p.title, line: p.markdown.split("\n").find(row => row.includes(`chaos:task/${r.taskId}`)) ?? "" })) }),
    "page.locate": () => ok({ anchor: null }),
    "page.makeTask": (r: { draft: CaptureDraftDto }) => { const t = task(newId("task"), r.draft.title, { containerId: r.draft.containerId ?? "inbox", plannedDate: r.draft.plannedDate }); tasks.push(t); record(`Make task “${t.title}”`); return ok({ task: t }); },
    "page.append": (r: { date: string; markdown: string }) => {
      const page = live().find(p => p.dailyDate === r.date) ?? addPage(r.date, "", null, r.date);
      Object.assign(page, { markdown: page.markdown.trim() ? `${page.markdown.trimEnd()}\n\n${r.markdown.trim()}\n` : `${r.markdown.trim()}\n`, draftVersion: page.draftVersion + 1, version: page.version + 1 });
      return ok({ pages: [pageView(page)] });
    },
    "page.fromPad": (r: { padItemId: string }) => {
      const index = padNotes.findIndex(n => n.id === r.padItemId);
      const note = padNotes[index];
      const first = note.markdown.split("\n").find(line => line.trim()) ?? "Untitled";
      const heading = /^#{1,6}\s+(.+)$/.exec(first.trim());
      const page = addPage(heading ? heading[1] : first.replace(/^[-*+]\s+(\[[ xX]\]\s+)?/, "").slice(0, 200), heading ? note.markdown.split("\n").slice(note.markdown.split("\n").indexOf(first) + 1).join("\n").trimStart() : note.markdown, null, null);
      deletedPadNotes.push(padNotes.splice(index, 1)[0]);
      return ok({ pages: [pageView(page)] });
    },
    "page.appendPad": (r: { padItemId: string; date: string }) => {
      const note = padNotes.find(n => n.id === r.padItemId)!;
      const page = live().find(p => p.dailyDate === r.date) ?? addPage(r.date, "", null, r.date);
      Object.assign(page, { markdown: page.markdown.trim() ? `${page.markdown.trimEnd()}\n\n${note.markdown.trim()}\n` : `${note.markdown.trim()}\n`, draftVersion: page.draftVersion + 1, version: page.version + 1 });
      padNotes[padNotes.indexOf(note)] = { ...note, archived: true };
      return ok({ pages: [pageView(page)] });
    },
  };
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
  "pad.listDeleted": () => ok({ items: deletedPadNotes }),
  "pad.reviewStatus": () => ok({ due: false, lastReviewedAtUtc: null, suggestions: [] }),
  "pad.get": (r: { itemId: string }) => ok({ items: padNotes.filter(n => n.id === r.itemId) }),
  "pad.search": (r: { text: string }) => ok({ items: padNotes.filter(n => n.markdown.toLowerCase().includes(r.text.toLowerCase())) }),
  "pad.create": (r: { markdown: string }) => { const created: PadItemResponse = { ...padNotes[0], id: newId("pad"), markdown: r.markdown, version: "1", draftVersion: "0", tasks: [], tagIds: [] }; padNotes.push(created); return ok({ items: [created] }); },
  "pad.save": (r: { itemId: string; clientRevision: string; markdown: string }) => { const i = padNotes.findIndex(n => n.id === r.itemId); padNotes[i] = { ...padNotes[i], markdown: r.markdown, draftVersion: r.clientRevision, version: bump(padNotes[i].version) }; return ok({ item: padNotes[i], applied: true }); },
  ...boardHandlers(),
  ...calendarHandlers(),
  ...calendarsHandlers(),
  ...googleHandlers(),
  ...viewHandlers(),
  ...pageHandlers(),
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
