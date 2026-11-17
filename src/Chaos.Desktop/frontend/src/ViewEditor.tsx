import React from "react";
import { viewRpc, type ContainerResponse, type TagResponse, type ViewDto } from "#neoastra";
import { Plus, Trash2 } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Field, InlineError, label, priorities, statuses } from "@/components/common";
import { createLatestRequest } from "./latestRequest";
import { tagColors } from "./tags";
import {
  defaultLayout, fromDefinitionJson, groupOptions, isComplete, layoutFromJson, layoutToJson, maxConditions, maxDepth, newCondition, newGroup, relativeDates,
  sortOptions, tableColumns, toDefinitionJson, viewField, viewFields, withField, countConditions, isGitHubField,
  type ConditionNode, type DateValue, type GroupNode, type Layout, type Node,
} from "./viewModel";

export type ViewsApi = Pick<typeof viewRpc, "save" | "preview"> & Partial<Pick<typeof viewRpc, "gitHubOptions">>;

/** The subscribed GitHub repositories; the GitHub condition group is offered only when there are some (or a view already uses it). */
const GitHubOptions = React.createContext<{ repositories: readonly { id: string; fullName: string }[]; offered: boolean }>({ repositories: [], offered: false });

type Props = {
  /** The view being edited; null makes a new one. */
  view: ViewDto | null;
  /** A starting definition for a new view (Save as view on search). */
  initialDefinitionJson?: string;
  initialName?: string;
  icons: readonly string[];
  tags: readonly TagResponse[];
  containers: readonly ContainerResponse[];
  focus?: string | null;
  api?: ViewsApi;
  onSaved: (view: ViewDto) => void;
  onCancel: () => void;
};

const operationId = () => crypto.randomUUID();

/**
 * The custom-view editor (PLAN2 §11.2): condition rows with nested all/any groups, a layout, and a
 * live preview count from the host, which runs the same query the saved view will. Nothing is
 * saved until Save; the host validates the definition again and its message is shown as is.
 */
export function ViewEditor({ view, initialDefinitionJson, initialName = "", icons, tags, containers, focus = null, api = viewRpc, onSaved, onCancel }: Props) {
  const [name, setName] = React.useState(view?.name ?? initialName);
  const [icon, setIcon] = React.useState(view?.icon ?? "list");
  const [color, setColor] = React.useState(view?.color ?? "");
  const [pinned, setPinned] = React.useState(view?.pinned ?? true);
  const [alerts, setAlerts] = React.useState(view?.alertsEnabled ?? false);
  const [root, setRoot] = React.useState<GroupNode>(() => fromDefinitionJson(view?.definitionJson ?? initialDefinitionJson ?? "{\"v\":1,\"match\":\"all\",\"items\":[]}"));
  const [layout, setLayout] = React.useState<Layout>(() => view ? layoutFromJson(view.layoutJson) : defaultLayout);
  const [error, setError] = React.useState<string | null>(null);
  const [saving, setSaving] = React.useState(false);
  const [preview, setPreview] = React.useState<{ count: number; titles: string[] } | null>(null);
  const [previewError, setPreviewError] = React.useState<string | null>(null);
  const [repositories, setRepositories] = React.useState<readonly { id: string; fullName: string }[]>([]);
  React.useEffect(() => {
    let current = true;
    void api.gitHubOptions?.({}).then(result => { if (current && result.ok) setRepositories(result.repositories); }).catch(() => undefined);
    return () => { current = false; };
  }, [api]);
  const requests = React.useRef(createLatestRequest());
  const operation = React.useRef<string | null>(null);
  const definitionJson = React.useMemo(() => toDefinitionJson(root), [root]);
  const complete = isComplete(root);

  React.useEffect(() => { operation.current = null; }, [name, icon, color, pinned, alerts, definitionJson, layout]);

  // The live preview asks the host for the exact count, debounced; a stale answer is dropped.
  React.useEffect(() => {
    if (!complete) { requests.current.invalidate(); setPreview(null); setPreviewError(null); return; }
    const isCurrent = requests.current.begin();
    const handle = window.setTimeout(() => {
      void api.preview({ definitionJson, layoutJson: layoutToJson(layout), category: focus, limit: 5 }).then(result => {
        if (!isCurrent()) return;
        if (result.ok) { setPreview({ count: result.totalCount, titles: result.rows.map(row => row.task.title) }); setPreviewError(null); }
        else { setPreview(null); setPreviewError(result.error?.message ?? "This view cannot be previewed."); }
      }).catch((e: unknown) => { if (isCurrent()) setPreviewError(String(e)); });
    }, 300);
    return () => window.clearTimeout(handle);
  }, [definitionJson, layout.sortField, layout.sortDescending, complete, focus, api]);

  async function save(event: React.FormEvent) {
    event.preventDefault();
    if (saving) return;
    setSaving(true); setError(null);
    operation.current ??= operationId();
    try {
      const result = await api.save({
        operationId: operation.current, viewId: view?.id ?? null, expectedVersion: view?.version ?? "0", name, icon, color: color || null,
        definitionJson, layoutJson: layoutToJson(layout), pinned, alertsEnabled: alerts,
      });
      if (!result.ok || !result.view) { setError(result.error?.fields.map(field => field.message).join("; ") || result.error?.message || "The view could not be saved."); return; }
      onSaved(result.view);
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setSaving(false); }
  }

  const total = countConditions(root);
  return <form className="grid gap-4 rounded-xl border bg-muted/30 p-4" aria-label={view ? "Edit view" : "New view"} onSubmit={event => void save(event)}>
    <div className="flex flex-wrap items-end gap-3">
      <Field label="Name" className="min-w-56 flex-1"><Input value={name} maxLength={80} required onChange={event => setName(event.target.value)}/></Field>
      <Field label="Icon" className="w-40"><NativeSelect value={icon} onChange={event => setIcon(event.target.value)}>{icons.map(item => <NativeSelectOption key={item} value={item}>{item}</NativeSelectOption>)}</NativeSelect></Field>
      <Field label="Colour" className="w-36"><NativeSelect value={color} onChange={event => setColor(event.target.value)}>
        <NativeSelectOption value="">None</NativeSelectOption>{tagColors.map(item => <NativeSelectOption key={item} value={item}>{item[0].toUpperCase() + item.slice(1)}</NativeSelectOption>)}
      </NativeSelect></Field>
    </div>

    <fieldset className="grid gap-2"><legend className="mb-1 text-sm font-semibold">Show tasks that match</legend>
      <GitHubOptions.Provider value={{ repositories, offered: repositories.length > 0 || usesGitHub(root) }}>
      <GroupEditor group={root} depth={1} tags={tags} containers={containers} total={total} onChange={next => setRoot(next)} onRemove={null}/>
      </GitHubOptions.Provider>
    </fieldset>

    <fieldset className="grid gap-2"><legend className="mb-1 text-sm font-semibold">Layout</legend>
      <div className="flex flex-wrap items-end gap-3">
        <Field label="Show as" className="w-36"><NativeSelect value={layout.kind} onChange={event => setLayout({ ...layout, kind: event.target.value as Layout["kind"] })}>
          <NativeSelectOption value="list">List</NativeSelectOption><NativeSelectOption value="table">Table</NativeSelectOption><NativeSelectOption value="board">Board</NativeSelectOption><NativeSelectOption value="calendar">Calendar</NativeSelectOption></NativeSelect></Field>
        {layout.kind === "list" && <Field label="Group by" className="w-40"><NativeSelect value={layout.group} onChange={event => setLayout({ ...layout, group: event.target.value })}>
          {groupOptions.map(option => <NativeSelectOption key={option.value} value={option.value}>{option.label}</NativeSelectOption>)}</NativeSelect></Field>}
        <Field label="Sort by" className="w-40"><NativeSelect value={layout.sortField} onChange={event => setLayout({ ...layout, sortField: event.target.value })}>
          {sortOptions.map(option => <NativeSelectOption key={option.value} value={option.value}>{option.label}</NativeSelectOption>)}</NativeSelect></Field>
        <Field label="Order" className="w-36"><NativeSelect value={layout.sortDescending ? "desc" : "asc"} onChange={event => setLayout({ ...layout, sortDescending: event.target.value === "desc" })}>
          <NativeSelectOption value="asc">Ascending</NativeSelectOption><NativeSelectOption value="desc">Descending</NativeSelectOption></NativeSelect></Field>
      </div>
      {layout.kind === "table" && <div className="flex flex-wrap gap-x-4 gap-y-1" role="group" aria-label="Table columns">
        {tableColumns.map(column => <label key={column.name} className="flex items-center gap-1.5 text-sm">
          <input type="checkbox" checked={layout.columns.includes(column.name)} disabled={column.name === "title"}
            onChange={event => setLayout({ ...layout, columns: tableColumns.map(item => item.name).filter(item => item === column.name ? event.target.checked : layout.columns.includes(item)) })}/>{column.label}</label>)}
      </div>}
    </fieldset>

    <div className="flex flex-wrap gap-x-6 gap-y-2">
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={pinned} onChange={event => setPinned(event.target.checked)}/>Pin to the sidebar</label>
      <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={alerts} onChange={event => setAlerts(event.target.checked)}/>Tell me when a new task matches</label>
    </div>

    <div className="rounded-lg border bg-card px-3 py-2 text-sm" role="status" aria-live="polite" aria-label="Preview">
      {!complete ? "Finish the conditions to see how many tasks match."
        : previewError ? <span className="text-destructive">{previewError}</span>
        : preview === null ? "Counting…"
        : <><strong>{preview.count}</strong> {preview.count === 1 ? "task matches" : "tasks match"}{preview.titles.length > 0 && <span className="text-muted-foreground">: {preview.titles.map((title, index) => <bdi key={index}>{index > 0 ? ", " : ""}{title}</bdi>)}{preview.count > preview.titles.length && ", …"}</span>}</>}
    </div>

    {error && <InlineError>{error}</InlineError>}
    <div className="flex gap-2">
      <Button type="submit" disabled={saving || !name.trim()}>{saving ? "Saving…" : "Save view"}</Button>
      <Button type="button" variant="ghost" onClick={onCancel}>Cancel</Button>
    </div>
  </form>;
}

function GroupEditor({ group, depth, tags, containers, total, onChange, onRemove }: {
  group: GroupNode; depth: number; tags: readonly TagResponse[]; containers: readonly ContainerResponse[]; total: number;
  onChange: (group: GroupNode) => void; onRemove: (() => void) | null;
}) {
  const replace = (key: string, next: Node | null) => onChange({ ...group, items: group.items.flatMap(item => item.key === key ? (next ? [next] : []) : [item]) });
  return <div className={depth > 1 ? "grid gap-2 rounded-lg border bg-background/60 p-3" : "grid gap-2"} role="group" aria-label={depth > 1 ? `Group, level ${depth}` : "Conditions"}>
    <div className="flex flex-wrap items-center gap-2 text-sm">
      Match
      <NativeSelect aria-label={`Match mode, level ${depth}`} className="w-24" value={group.match} onChange={event => onChange({ ...group, match: event.target.value as "all" | "any" })}>
        <NativeSelectOption value="all">all</NativeSelectOption><NativeSelectOption value="any">any</NativeSelectOption></NativeSelect>
      of these
      {onRemove && <Button type="button" variant="ghost" size="sm" className="ml-auto" onClick={onRemove}><Trash2/>Remove group</Button>}
    </div>
    {group.items.length === 0 && <p className="text-sm text-muted-foreground">No conditions yet: every task matches.</p>}
    {group.items.map(item => item.kind === "group"
      ? <GroupEditor key={item.key} group={item} depth={depth + 1} tags={tags} containers={containers} total={total} onChange={next => replace(item.key, next)} onRemove={() => replace(item.key, null)}/>
      : <ConditionRow key={item.key} condition={item} tags={tags} containers={containers} onChange={next => replace(item.key, next)} onRemove={() => replace(item.key, null)}/>)}
    <div className="flex gap-2">
      <Button type="button" variant="outline" size="sm" disabled={total >= maxConditions} onClick={() => onChange({ ...group, items: [...group.items, newCondition()] })}><Plus/>Add condition</Button>
      <Button type="button" variant="outline" size="sm" disabled={depth >= maxDepth || total >= maxConditions} onClick={() => onChange({ ...group, items: [...group.items, newGroup("any", [newCondition()])] })}><Plus/>Add group</Button>
    </div>
  </div>;
}

function ConditionRow({ condition, tags, containers, onChange, onRemove }: {
  condition: ConditionNode; tags: readonly TagResponse[]; containers: readonly ContainerResponse[];
  onChange: (condition: ConditionNode) => void; onRemove: () => void;
}) {
  const github = React.useContext(GitHubOptions);
  const info = viewField(condition.field) ?? viewFields[0];
  const noValue = condition.op === "isEmpty" || condition.op === "isNotEmpty";
  const set = (patch: Partial<ConditionNode>) => onChange({ ...condition, ...patch });
  return <div className="flex flex-wrap items-center gap-2" role="group" aria-label={`Condition on ${info.label}`}>
    <NativeSelect aria-label="Field" className="w-48" value={condition.field} onChange={event => onChange(withField(condition, event.target.value))}>
      {viewFields.filter(field => !isGitHubField(field.name) || github.offered || field.name === condition.field).map(field => <NativeSelectOption key={field.name} value={field.name}>{field.label}</NativeSelectOption>)}</NativeSelect>
    <NativeSelect aria-label="Condition" className="w-40" value={condition.op} onChange={event => set({ op: event.target.value, values: info.type === "id" && event.target.value !== "in" ? condition.values.slice(0, 1) : condition.values,
      // Overdue only makes sense as a range on the due date, and a range needs the range operator.
      date: condition.date.mode === "relative" && condition.date.relative === "overdue" && event.target.value !== "inRange" ? { mode: "relative", relative: "today", days: 7 } : condition.date.mode === "range" && event.target.value !== "inRange" ? { mode: "day", day: condition.date.from } : condition.date })}>
      {info.operators.map(operator => <NativeSelectOption key={operator.op} value={operator.op}>{operator.label}</NativeSelectOption>)}</NativeSelect>
    {!noValue && <ValueEditor condition={condition} info={info} tags={tags} containers={containers} onChange={onChange}/>}
    <Button type="button" variant="ghost" size="icon-sm" aria-label={`Remove condition on ${info.label}`} onClick={onRemove}><Trash2/></Button>
  </div>;
}

function ValueEditor({ condition, info, tags, containers, onChange }: {
  condition: ConditionNode; info: NonNullable<ReturnType<typeof viewField>>; tags: readonly TagResponse[]; containers: readonly ContainerResponse[];
  onChange: (condition: ConditionNode) => void;
}) {
  const set = (patch: Partial<ConditionNode>) => onChange({ ...condition, ...patch });
  const toggle = (value: string) => set({ values: condition.values.includes(value) ? condition.values.filter(item => item !== value) : [...condition.values, value] });
  switch (info.type) {
    case "id": {
      if (condition.field === "githubRepository") return <GitHubRepositoryPicker condition={condition} onChange={onChange}/>;
      const many = condition.op === "in";
      return many
        ? <div className="flex flex-wrap gap-x-3 gap-y-1" role="group" aria-label="Lists and projects">{containers.map(container => <label key={container.id} className="flex items-center gap-1.5 text-sm">
            <input type="checkbox" checked={condition.values.includes(container.id)} onChange={() => toggle(container.id)}/>{container.name}</label>)}</div>
        : <NativeSelect aria-label="List or project" className="w-48" value={condition.values[0] ?? ""} onChange={event => set({ values: event.target.value ? [event.target.value] : [] })}>
            <NativeSelectOption value="">Choose…</NativeSelectOption>{containers.map(container => <NativeSelectOption key={container.id} value={container.id}>{container.name}</NativeSelectOption>)}</NativeSelect>;
    }
    case "status": return <div className="flex flex-wrap gap-x-3 gap-y-1" role="group" aria-label="Statuses">{statuses.map(status => <label key={status} className="flex items-center gap-1.5 text-sm">
      <input type="checkbox" checked={condition.values.includes(status)} onChange={() => toggle(status)}/>{label(status)}</label>)}</div>;
    case "ordinal": return <NativeSelect aria-label="Priority" className="w-36" value={condition.values[0] ?? "High"} onChange={event => set({ values: [event.target.value] })}>
      {priorities.map(priority => <NativeSelectOption key={priority} value={priority}>{priority}</NativeSelectOption>)}</NativeSelect>;
    case "tags": return <div className="flex flex-wrap items-center gap-x-3 gap-y-1" role="group" aria-label="Tags">
      {tags.length === 0 && <span className="text-sm text-muted-foreground">No tags yet.</span>}
      {tags.map(tag => <label key={tag.id} className="flex items-center gap-1.5 text-sm"><input type="checkbox" checked={condition.values.includes(tag.id)} onChange={() => toggle(tag.id)}/>#{tag.path}</label>)}
      <label className="flex items-center gap-1.5 text-sm"><input type="checkbox" checked={condition.descendants} onChange={event => set({ descendants: event.target.checked })}/>Include nested tags</label></div>;
    case "category": return <NativeSelect aria-label="Category" className="w-36" value={condition.values[0] ?? "Work"} onChange={event => set({ values: [event.target.value] })}>
      <NativeSelectOption value="Work">Work</NativeSelectOption><NativeSelectOption value="Personal">Personal</NativeSelectOption></NativeSelect>;
    case "choice": return <NativeSelect aria-label={info.label} className="w-36" value={condition.values[0] ?? info.choices?.[0] ?? ""} onChange={event => set({ values: [event.target.value] })}>
      {(info.choices ?? []).map(choice => <NativeSelectOption key={choice} value={choice}>{choice}</NativeSelectOption>)}</NativeSelect>;
    case "source": return <NativeSelect aria-label="Source" className="w-36" value={condition.values[0] ?? "App"} onChange={event => set({ values: [event.target.value] })}>
      <NativeSelectOption value="App">Created in Chaos</NativeSelectOption><NativeSelectOption value="GitHub">From GitHub</NativeSelectOption></NativeSelect>;
    case "flag": return <NativeSelect aria-label="Yes or no" className="w-28" value={condition.values[0] ?? "true"} onChange={event => set({ values: [event.target.value] })}>
      <NativeSelectOption value="true">yes</NativeSelectOption><NativeSelectOption value="false">no</NativeSelectOption></NativeSelect>;
    case "minutes": return <label className="flex items-center gap-1.5 text-sm"><Input aria-label="Minutes" type="number" min={0} max={10080} className="w-24" value={condition.values[0] ?? ""} onChange={event => set({ values: [event.target.value] })}/>minutes</label>;
    case "text": return <Input aria-label="Text" className="w-56" dir="auto" maxLength={200} value={condition.values[0] ?? ""} onChange={event => set({ values: [event.target.value] })}/>;
    case "date": return <DateEditor condition={condition} date={condition.date} onChange={date => set({ date })}/>;
  }
}

function DateEditor({ condition, date, onChange }: { condition: ConditionNode; date: DateValue; onChange: (date: DateValue) => void }) {
  const canOverdue = condition.field === "due" && condition.op === "inRange";
  const mode = date.mode;
  return <div className="flex flex-wrap items-center gap-2">
    <NativeSelect aria-label="Kind of date" className="w-36" value={mode} onChange={event => onChange(event.target.value === "day" ? { mode: "day", day: "" } : event.target.value === "range" ? { mode: "range", from: "", to: "" } : { mode: "relative", relative: "today", days: 7 })}>
      <NativeSelectOption value="relative">Relative</NativeSelectOption><NativeSelectOption value="day">A date</NativeSelectOption>{condition.op === "inRange" && <NativeSelectOption value="range">Between dates</NativeSelectOption>}</NativeSelect>
    {date.mode === "day" && <Input aria-label="Date" type="date" className="w-40" value={date.day} onChange={event => onChange({ mode: "day", day: event.target.value })}/>}
    {date.mode === "range" && <>
      <Input aria-label="From" type="date" className="w-40" value={date.from} onChange={event => onChange({ ...date, from: event.target.value })}/>
      <Input aria-label="To" type="date" className="w-40" value={date.to} onChange={event => onChange({ ...date, to: event.target.value })}/></>}
    {date.mode === "relative" && <>
      <NativeSelect aria-label="Relative date" className="w-44" value={date.relative} onChange={event => onChange({ ...date, relative: event.target.value })}>
        {relativeDates.filter(item => item.value !== "overdue" || canOverdue).map(item => <NativeSelectOption key={item.value} value={item.value}>{item.label}</NativeSelectOption>)}</NativeSelect>
      {(date.relative === "next" || date.relative === "last") && <Input aria-label="Number of days" type="number" min={1} max={3660} className="w-20" value={date.days} onChange={event => onChange({ ...date, days: Number(event.target.value) })}/>}</>}
  </div>;
}

const usesGitHub = (node: Node): boolean => node.kind === "group" ? node.items.some(usesGitHub) : isGitHubField(node.field);

function GitHubRepositoryPicker({ condition, onChange }: { condition: ConditionNode; onChange: (condition: ConditionNode) => void }) {
  const { repositories } = React.useContext(GitHubOptions);
  const many = condition.op === "in";
  const toggle = (id: string) => onChange({ ...condition, values: condition.values.includes(id) ? condition.values.filter(item => item !== id) : [...condition.values, id] });
  if (repositories.length === 0) return <span className="text-sm text-muted-foreground">No GitHub repositories are connected.</span>;
  return many
    ? <div className="flex flex-wrap gap-x-3 gap-y-1" role="group" aria-label="GitHub repositories">{repositories.map(repository => <label key={repository.id} className="flex items-center gap-1.5 text-sm">
        <input type="checkbox" checked={condition.values.includes(repository.id)} onChange={() => toggle(repository.id)}/>{repository.fullName}</label>)}</div>
    : <NativeSelect aria-label="GitHub repository" className="w-56" value={condition.values[0] ?? ""} onChange={event => onChange({ ...condition, values: event.target.value ? [event.target.value] : [] })}>
        <NativeSelectOption value="">Choose…</NativeSelectOption>{repositories.map(repository => <NativeSelectOption key={repository.id} value={repository.id}>{repository.fullName}</NativeSelectOption>)}</NativeSelect>;
}
