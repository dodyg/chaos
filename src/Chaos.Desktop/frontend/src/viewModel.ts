/**
 * The custom-view condition tree as the editor holds it (PLAN2 §11.2), and its conversion to and
 * from the version-1 definition JSON the host stores. The host validates every definition again;
 * this model only keeps the editor honest (a field's operators, a value of the right shape).
 */

export type ViewFieldType = "id" | "status" | "ordinal" | "tags" | "category" | "date" | "minutes" | "text" | "source" | "flag" | "choice";

export type ViewFieldInfo = {
  name: string;
  label: string;
  type: ViewFieldType;
  operators: readonly { op: string; label: string }[];
  choices?: readonly string[];
};

const dateOps = [
  { op: "on", label: "is on" },
  { op: "before", label: "is before" },
  { op: "after", label: "is after" },
  { op: "inRange", label: "is within" },
  { op: "isEmpty", label: "is empty" },
  { op: "isNotEmpty", label: "is not empty" },
] as const;

export const viewFields: readonly ViewFieldInfo[] = [
  {
    name: "space",
    label: "Space",
    type: "id",
    operators: [
      { op: "is", label: "is" },
      { op: "isNot", label: "is not" },
      { op: "in", label: "is one of" },
    ],
  },
  {
    name: "assignee",
    label: "Assignee",
    type: "id",
    operators: [
      { op: "isMe", label: "is me" },
      { op: "is", label: "is" },
      { op: "in", label: "is one of" },
      { op: "isEmpty", label: "is none" },
    ],
  },
  {
    name: "container",
    label: "List or project",
    type: "id",
    operators: [
      { op: "is", label: "is" },
      { op: "isNot", label: "is not" },
      { op: "in", label: "is one of" },
    ],
  },
  {
    name: "status",
    label: "Status",
    type: "status",
    operators: [
      { op: "in", label: "is one of" },
      { op: "notIn", label: "is none of" },
    ],
  },
  {
    name: "priority",
    label: "Priority",
    type: "ordinal",
    operators: [
      { op: "is", label: "is" },
      { op: "atLeast", label: "is at least" },
      { op: "atMost", label: "is at most" },
    ],
  },
  {
    name: "tag",
    label: "Tag",
    type: "tags",
    operators: [
      { op: "hasAnyOf", label: "has any of" },
      { op: "hasAllOf", label: "has all of" },
      { op: "hasNone", label: "has none of" },
    ],
  },
  { name: "category", label: "Work or Personal", type: "category", operators: [{ op: "is", label: "is" }] },
  { name: "planned", label: "Planned date", type: "date", operators: dateOps },
  { name: "due", label: "Due date", type: "date", operators: dateOps },
  { name: "created", label: "Created", type: "date", operators: dateOps },
  { name: "changed", label: "Changed", type: "date", operators: dateOps },
  { name: "completed", label: "Completed", type: "date", operators: dateOps },
  {
    name: "estimate",
    label: "Estimate",
    type: "minutes",
    operators: [
      { op: "lessThan", label: "is less than" },
      { op: "moreThan", label: "is more than" },
      { op: "isEmpty", label: "is empty" },
    ],
  },
  { name: "text", label: "Title or notes", type: "text", operators: [{ op: "contains", label: "contains" }] },
  {
    name: "waiting",
    label: "Waiting on",
    type: "text",
    operators: [
      { op: "contains", label: "contains" },
      { op: "isEmpty", label: "is empty" },
    ],
  },
  { name: "source", label: "Source", type: "source", operators: [{ op: "is", label: "is" }] },
  { name: "hasPadLink", label: "Made from a pad note", type: "flag", operators: [{ op: "is", label: "is" }] },
  { name: "hasPageMention", label: "Made from a page", type: "flag", operators: [{ op: "is", label: "is" }] },
  { name: "recurring", label: "Repeats", type: "flag", operators: [{ op: "is", label: "is" }] },
  // GitHub group (personal views). Answered from what the app already stores about each issue; the
  // issue author and linked pull requests are not stored, so they are not offered.
  {
    name: "githubRepository",
    label: "GitHub repository",
    type: "id",
    operators: [
      { op: "is", label: "is" },
      { op: "isNot", label: "is not" },
      { op: "in", label: "is one of" },
    ],
  },
  { name: "githubState", label: "GitHub issue", type: "choice", choices: ["Open", "Closed"], operators: [{ op: "is", label: "is" }] },
  {
    name: "githubLabel",
    label: "GitHub label",
    type: "text",
    operators: [
      { op: "contains", label: "contains" },
      { op: "isEmpty", label: "has none" },
    ],
  },
  {
    name: "githubMilestone",
    label: "GitHub milestone",
    type: "text",
    operators: [
      { op: "contains", label: "contains" },
      { op: "isEmpty", label: "has none" },
    ],
  },
  {
    name: "githubAssignee",
    label: "GitHub assignee",
    type: "text",
    operators: [
      { op: "contains", label: "contains" },
      { op: "isEmpty", label: "has none" },
    ],
  },
];

/** The GitHub fields offered only when a repository is connected. */
export const isGitHubField = (name: string) => name.startsWith("github");

export const viewField = (name: string) => viewFields.find((field) => field.name === name);

/** Relative date phrases the host resolves at query time, so "this week" moves on by itself. */
export const relativeDates = [
  { value: "today", label: "today" },
  { value: "tomorrow", label: "tomorrow" },
  { value: "yesterday", label: "yesterday" },
  { value: "thisWeek", label: "this week" },
  { value: "nextWeek", label: "next week" },
  { value: "lastWeek", label: "last week" },
  { value: "thisMonth", label: "this month" },
  { value: "nextMonth", label: "next month" },
  { value: "lastMonth", label: "last month" },
  { value: "next", label: "the next N days" },
  { value: "last", label: "the last N days" },
  { value: "overdue", label: "overdue" },
] as const;

export const maxDepth = 4;
export const maxConditions = 50;

/** A date value: one day, a range with open ends, or a relative phrase (with days for next/last). */
export type DateValue =
  { mode: "day"; day: string } | { mode: "range"; from: string; to: string } | { mode: "relative"; relative: string; days: number };

export type ConditionNode = {
  kind: "condition";
  key: string;
  field: string;
  op: string;
  /** ids, statuses, priority, category, source: string list; flags: ["true"|"false"]; minutes: [number text]; text: [text] */
  values: string[];
  date: DateValue;
  descendants: boolean;
};
export type GroupNode = { kind: "group"; key: string; match: "all" | "any"; items: Node[] };
export type Node = ConditionNode | GroupNode;

let counter = 0;
const nextKey = () => `n${++counter}`;

export const newGroup = (match: "all" | "any" = "all", items: Node[] = []): GroupNode => ({ kind: "group", key: nextKey(), match, items });

export function newCondition(field = "status"): ConditionNode {
  const info = viewField(field) ?? viewFields[0];
  return {
    kind: "condition",
    key: nextKey(),
    field: info.name,
    op: info.operators[0].op,
    descendants: false,
    values: defaultValues(info),
    date: { mode: "relative", relative: "today", days: 7 },
  };
}

function defaultValues(info: ViewFieldInfo): string[] {
  switch (info.type) {
    case "status":
      return ["Done", "Cancelled"];
    case "ordinal":
      return ["High"];
    case "category":
      return ["Work"];
    case "source":
      return ["App"];
    case "choice":
      return [info.choices?.[0] ?? ""];
    case "flag":
      return ["true"];
    default:
      return [];
  }
}

/** Switches a condition's field, keeping nothing that no longer fits (its operator and value reset). */
export function withField(condition: ConditionNode, field: string): ConditionNode {
  const next = newCondition(field);
  return { ...next, key: condition.key };
}

export const countConditions = (node: Node): number =>
  node.kind === "condition" ? 1 : node.items.reduce((sum, item) => sum + countConditions(item), 0);
export const depthOf = (node: Node): number => (node.kind === "condition" ? 0 : 1 + Math.max(0, ...node.items.map(depthOf)));

// ---- JSON conversion ---------------------------------------------------------------------------

function dateToJson(date: DateValue): unknown {
  if (date.mode === "day") return date.day;
  if (date.mode === "range") return { ...(date.from ? { from: date.from } : {}), ...(date.to ? { to: date.to } : {}) };
  return date.relative === "next" || date.relative === "last" ? { relative: date.relative, days: date.days } : { relative: date.relative };
}

function conditionToJson(node: ConditionNode): Record<string, unknown> {
  const info = viewField(node.field)!;
  const out: Record<string, unknown> = { field: node.field, op: node.op };
  const empty = node.op === "isEmpty" || node.op === "isNotEmpty" || node.op === "isMe";
  if (!empty) {
    switch (info.type) {
      case "id":
        out.value = node.op === "in" ? node.values : (node.values[0] ?? "");
        break;
      case "status":
      case "tags":
        out.value = node.values;
        break;
      case "ordinal":
      case "category":
      case "source":
      case "choice":
        out.value = node.values[0] ?? "";
        break;
      case "date":
        out.value = dateToJson(node.date);
        break;
      case "minutes":
        out.value = Number(node.values[0] ?? "");
        break;
      case "text":
        out.value = node.values[0] ?? "";
        break;
      case "flag":
        out.value = node.values[0] !== "false";
        break;
    }
  }
  if (node.descendants && info.type === "tags") out.descendants = true;
  return out;
}

function groupToJson(group: GroupNode): Record<string, unknown> {
  return { match: group.match, items: group.items.map((item) => (item.kind === "group" ? groupToJson(item) : conditionToJson(item))) };
}

/** The definition JSON text the host stores and validates. */
export function toDefinitionJson(root: GroupNode): string {
  return JSON.stringify({ v: 1, ...groupToJson(root) });
}

function dateFromJson(value: unknown): DateValue {
  if (typeof value === "string") return { mode: "day", day: value };
  if (value && typeof value === "object") {
    const record = value as Record<string, unknown>;
    if (typeof record.relative === "string")
      return { mode: "relative", relative: record.relative, days: typeof record.days === "number" ? record.days : 7 };
    return { mode: "range", from: typeof record.from === "string" ? record.from : "", to: typeof record.to === "string" ? record.to : "" };
  }
  return { mode: "relative", relative: "today", days: 7 };
}

function conditionFromJson(raw: Record<string, unknown>): ConditionNode {
  const base = newCondition(typeof raw.field === "string" && viewField(raw.field) ? raw.field : "status");
  const info = viewField(base.field)!;
  const value = raw.value;
  const strings = Array.isArray(value) ? value.map(String) : value === undefined || value === null ? [] : [String(value)];
  return {
    ...base,
    op: typeof raw.op === "string" ? raw.op : base.op,
    values: info.type === "date" ? [] : strings.length > 0 || info.type === "text" ? strings : base.values,
    date: info.type === "date" ? dateFromJson(value) : base.date,
    descendants: raw.descendants === true,
  };
}

function groupFromJson(raw: Record<string, unknown>): GroupNode {
  const items = Array.isArray(raw.items) ? raw.items : [];
  return newGroup(
    raw.match === "any" ? "any" : "all",
    items.map((item) => {
      const record = item as Record<string, unknown>;
      return Array.isArray(record.items) ? groupFromJson(record) : conditionFromJson(record);
    }),
  );
}

/** Reads a stored definition into editor nodes; an unreadable one yields an empty "all" group. */
export function fromDefinitionJson(json: string): GroupNode {
  try {
    const parsed = JSON.parse(json) as Record<string, unknown>;
    return groupFromJson(parsed);
  } catch {
    return newGroup();
  }
}

/** True when a condition has everything the host needs, so the live preview does not ask for half-made rows. */
export function isComplete(node: Node): boolean {
  if (node.kind === "group") return node.items.every(isComplete);
  const info = viewField(node.field)!;
  if (node.op === "isEmpty" || node.op === "isNotEmpty" || node.op === "isMe") return true;
  switch (info.type) {
    case "id":
    case "status":
    case "tags":
      return node.values.filter(Boolean).length > 0;
    case "text":
      return (node.values[0] ?? "").trim().length > 0;
    case "minutes":
      return node.values[0] !== undefined && node.values[0] !== "" && Number(node.values[0]) >= 0;
    case "date":
      if (node.date.mode === "day") return node.date.day !== "";
      if (node.date.mode === "range") return node.date.from !== "" || node.date.to !== "";
      return node.date.relative !== "next" && node.date.relative !== "last" ? true : node.date.days >= 1;
    default:
      return true;
  }
}

// ---- Layout ------------------------------------------------------------------------------------

export type LayoutKind = "list" | "table" | "board" | "calendar";
export type Layout = { kind: LayoutKind; group: string; sortField: string; sortDescending: boolean; columns: string[] };

export const tableColumns = [
  { name: "title", label: "Title" },
  { name: "status", label: "Status" },
  { name: "priority", label: "Priority" },
  { name: "container", label: "List" },
  { name: "planned", label: "Planned" },
  { name: "due", label: "Due" },
  { name: "estimate", label: "Estimate" },
  { name: "tags", label: "Tags" },
  { name: "created", label: "Created" },
  { name: "changed", label: "Changed" },
  { name: "completed", label: "Completed" },
] as const;

export const groupOptions = [
  { value: "none", label: "No grouping" },
  { value: "container", label: "List" },
  { value: "status", label: "Status" },
  { value: "priority", label: "Priority" },
  { value: "due", label: "Due date" },
] as const;
export const sortOptions = [
  { value: "due", label: "Due date" },
  { value: "planned", label: "Planned date" },
  { value: "priority", label: "Priority" },
  { value: "created", label: "Created" },
  { value: "changed", label: "Changed" },
  { value: "title", label: "Title" },
] as const;

export const defaultLayout: Layout = {
  kind: "list",
  group: "none",
  sortField: "due",
  sortDescending: false,
  columns: ["title", "status", "priority", "due"],
};

export function layoutFromJson(json: string | null | undefined): Layout {
  try {
    const raw = JSON.parse(json ?? "") as Record<string, unknown>;
    const sort = (raw.sort ?? {}) as Record<string, unknown>;
    return {
      kind: raw.kind === "table" ? "table" : raw.kind === "board" ? "board" : raw.kind === "calendar" ? "calendar" : "list",
      group: typeof raw.group === "string" && groupOptions.some((option) => option.value === raw.group) ? raw.group : "none",
      sortField: typeof sort.field === "string" ? sort.field : "due",
      sortDescending: sort.dir === "desc",
      columns: Array.isArray(raw.columns) && raw.columns.length > 0 ? raw.columns.map(String) : [...defaultLayout.columns],
    };
  } catch {
    return defaultLayout;
  }
}

export function layoutToJson(layout: Layout): string {
  return JSON.stringify({
    v: 1,
    kind: layout.kind,
    group: layout.group,
    sort: { field: layout.sortField, dir: layout.sortDescending ? "desc" : "asc" },
    columns: layout.columns,
  });
}
