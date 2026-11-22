import React from "react";
import {
  attachmentRpc,
  pageRpc,
  summaryRpc,
  type CaptureDraftDto,
  type CaptureSubtaskDto,
  type ContainerResponse,
  type PageResponse,
  type PageRevisionResponse,
  type PageSummaryResponse,
  type SummaryDetailDto,
  type TagResponse,
} from "#neoastra";
import {
  BookOpen,
  CalendarDays,
  ChevronRight,
  Copy,
  FileText,
  Focus,
  History,
  Link2,
  ListPlus,
  ListTree,
  Minimize2,
  MoveRight,
  PenLine,
  Plus,
  RotateCcw,
  Settings2,
  Trash2,
  X,
} from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Textarea } from "@/components/ui/textarea";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Banner, DeletedItems, EmptyState } from "@/components/common";
import { MarkdownEditor, type LinkCandidate } from "./MarkdownEditor";
import { MarkdownPreview, headingAnchor, type MarkdownLinks } from "./markdown";
import { QuickAdd, type CapturePreviewApi } from "./QuickAdd";
import { DraftRegistry, type DraftSaver, type DraftSnapshot } from "./padAutosave";
import { joinPage, knownKeys, pageProperties, parseFrontMatter, setProperty, splitPage, withYaml } from "./frontMatter";
import { headingsOf, wordCount } from "./outline";
import { lineDiff, diffStats } from "./lineDiff";
import { backgroundColors, backgroundScenes } from "./MyDayBackground";
import { attachmentBackgroundName, isWebBackground, usePageImage } from "./attachments";
import { titleFromSelection } from "./ScratchPad";

export type PageApi = typeof pageRpc;

/** What the app asks the Pages view to show. */
export type PageRequest =
  | { kind: "page"; pageId: string; anchor?: { state: string; start: number | null; end: number | null; quote: string } | null }
  | { kind: "daily"; date: string }
  | { kind: "name"; name: string };
export type PageFocus = { key: number } & PageRequest;

const operationId = () => crypto.randomUUID();
const dailyBackgroundKey = "chaos.pages.dailyBackground.v1";

/** The background new daily pages start with (Settings), or null. */
export function loadDailyBackground(): string | null {
  try {
    return localStorage.getItem(dailyBackgroundKey) || null;
  } catch {
    return null;
  }
}

export function saveDailyBackground(value: string | null) {
  try {
    if (value) localStorage.setItem(dailyBackgroundKey, value);
    else localStorage.removeItem(dailyBackgroundKey);
  } catch {
    /* storage unavailable */
  }
}

/** A page's title and text travel through one autosave as "title\ntext" (a title is one line). */
export function encodeDraft(title: string, markdown: string) {
  return `${title.replace(/[\r\n]+/g, " ")}\n${markdown}`;
}
export function decodeDraft(text: string) {
  const newline = text.indexOf("\n");
  return newline < 0 ? { title: text, markdown: "" } : { title: text.slice(0, newline), markdown: text.slice(newline + 1) };
}

/** A daily page's title: its date written out, in the viewer's language. */
export function dailyTitle(date: string, today?: string | null) {
  const [year, month, day] = date.split("-").map(Number);
  const value = new Date(year, month - 1, day);
  const written = value.toLocaleDateString(undefined, { weekday: "long", day: "numeric", month: "long", year: "numeric" });
  return today === date ? `Today · ${written}` : written;
}

/** Subtasks for Make task: lines indented under the first selected line, without list markers. */
export function selectionSubtasks(text: string): CaptureSubtaskDto[] {
  const lines = text.split("\n").filter((line) => line.trim());
  if (lines.length < 2) return [];
  const indent = (line: string) => /^\s*/.exec(line)![0].replace(/\t/g, "    ").length;
  const first = indent(lines[0]);
  return lines
    .slice(1)
    .filter((line) => indent(line) > first)
    .map((line) => {
      const match = /^\s*(?:[-*+]|\d+[.)])\s+(\[([ xX])\]\s+)?(.*)$/.exec(line);
      return { title: (match ? match[3] : line).trim(), done: !!match?.[2] && match[2] !== " " };
    })
    .filter((subtask) => subtask.title);
}

/**
 * Page background from front matter: a My Day scene, a colour, or an image attached to the page
 * (`attachments/<name>`, resolved by `image`: undefined while loading, `{ url: null }` when missing).
 * A web address is never loaded.
 */
export function pageBackgroundStyle(value: string | null, image?: { url: string | null }): { style?: React.CSSProperties; note?: string } {
  if (!value) return {};
  if (isWebBackground(value))
    return { note: "A web address is not used as a background. Attach the image to this page and write attachments/<file name> instead." };
  const file = attachmentBackgroundName(value);
  if (file) {
    if (image === undefined) return {};
    return image.url
      ? { style: { backgroundImage: `url("${image.url}")`, backgroundSize: "cover", backgroundPosition: "center" } }
      : { note: `No image named “${file}” is attached to this page, so it has no background.` };
  }
  const scene = backgroundScenes.find((item) => item.id === value.toLowerCase());
  if (scene) return { style: { backgroundImage: `url("${scene.url}")`, backgroundSize: "cover", backgroundPosition: "center" } };
  if (/^#[0-9a-f]{3}([0-9a-f]{3})?$/i.test(value)) return { style: { backgroundColor: value } };
  if (/^attachments\//i.test(value)) return { note: "Write attachments/ followed by the file name of an image attached to this page." };
  return { note: `“${value}” is not a scene or a colour, so the page has no background.` };
}

function saverFor(registry: DraftRegistry, api: PageApi, page: PageResponse): DraftSaver {
  return registry.getOrCreate({
    itemId: page.id,
    text: encodeDraft(page.title, page.markdown),
    revision: Number(page.draftVersion),
    version: page.version,
    save: async (revision, text) => {
      const { title, markdown } = decodeDraft(text);
      const result = await api.save({ pageId: page.id, clientRevision: String(revision), title, markdown });
      if (!result.ok || !result.page)
        return {
          ok: false,
          message: result.error?.fields.map((field) => field.message).join("; ") || result.error?.message || "The page could not be saved.",
        };
      return {
        ok: true,
        draftVersion: Number(result.page.draftVersion),
        version: result.page.version,
        markdown: encodeDraft(result.page.title, result.page.markdown),
      };
    },
  });
}

function useSnapshot(saver: DraftSaver | null): DraftSnapshot | null {
  const [snapshot, setSnapshot] = React.useState(saver?.snapshot ?? null);
  React.useEffect(() => {
    setSnapshot(saver?.snapshot ?? null);
    return saver?.subscribe(setSnapshot);
  }, [saver]);
  return snapshot;
}

type Selected = { kind: "page"; pageId: string } | { kind: "daily"; date: string } | null;

type Props = {
  api?: PageApi;
  summaryApi?: typeof summaryRpc;
  captureApi?: CapturePreviewApi;
  registry: DraftRegistry;
  containers: ContainerResponse[];
  tags: TagResponse[];
  revision: number;
  today: string;
  focus: PageFocus | null;
  links: MarkdownLinks;
  searchLinks?: (query: string) => Promise<LinkCandidate[]>;
  onChanged: () => void;
  onOpenTask: (taskId: string) => void;
  onOpenSummary: (summaryId: string) => void;
  onCreateTag: (path: string) => Promise<boolean>;
  onError: (message: string | null) => void;
  /** "pages" shows the page tree; "daily" shows the daily pages. The two are separate views. */
  mode?: "pages" | "daily";
  /** Opens a page of the other kind (a daily page from Pages, or a page from a daily page) in its own view. */
  onRoute?: (request: PageRequest) => void;
};

/**
 * Pages (SPEC Pages): a tree of titled Markdown pages, or (mode "daily") one daily page per
 * date. Pages save as you type and keep versions; front matter shows as a properties panel;
 * lists work as an outline; links and backlinks join pages, tasks and lists; Make task turns
 * lines into tasks.
 */
export function Pages({
  api = pageRpc,
  summaryApi = summaryRpc,
  captureApi,
  registry,
  containers,
  tags,
  revision,
  today,
  focus,
  links,
  searchLinks,
  onChanged,
  onOpenTask,
  onOpenSummary,
  onCreateTag,
  onError,
  mode = "pages",
  onRoute,
}: Props) {
  const isDailyMode = mode === "daily";
  const [tree, setTree] = React.useState<PageSummaryResponse[]>([]);
  const [daily, setDaily] = React.useState<PageSummaryResponse[]>([]);
  const [deleted, setDeleted] = React.useState<PageSummaryResponse[]>([]);
  const [loaded, setLoaded] = React.useState(false);
  const [selected, setSelected] = React.useState<Selected>(() => {
    if (isDailyMode) return { kind: "daily", date: today };
    try {
      const id = localStorage.getItem("chaos.pages.open");
      return id ? { kind: "page", pageId: id } : null;
    } catch {
      return null;
    }
  });
  const [busy, setBusy] = React.useState(false);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [adding, setAdding] = React.useState<{ parentId: string | null } | null>(null);
  const [anchor, setAnchor] = React.useState<{ start: number; end: number; key: number } | null>(null);
  const [missingQuote, setMissingQuote] = React.useState<string | null>(null);
  const [allDaily, setAllDaily] = React.useState(false);
  const [pageBackground, setPageBackground] = React.useState<{ key: string; style: React.CSSProperties | undefined } | null>(null);
  const selectedKey = selected ? (selected.kind === "page" ? selected.pageId : `daily-${selected.date}`) : null;
  const sharedBackground = pageBackground?.key === selectedKey ? pageBackground.style : undefined;
  const pendingRewrite = React.useRef<{ name: string; pageId: string } | null>(null);

  async function load() {
    const [list, days, removed] = await Promise.all([api.list({}), api.listDaily({ from: null, to: null }), api.listDeleted({})]);
    if (!list.ok) throw new Error(list.error?.message ?? "Could not load pages.");
    setTree([...list.pages]);
    if (days.ok) setDaily([...days.pages]);
    if (removed.ok) setDeleted([...removed.pages]);
    setLoaded(true);
  }
  const loadRef = React.useRef(load);
  loadRef.current = load;
  React.useEffect(() => {
    void loadRef.current().catch((e: unknown) => onError(e instanceof Error ? e.message : String(e)));
  }, [revision]);

  function open(next: Selected) {
    if (next && (next.kind === "daily") !== isDailyMode) {
      onRoute?.(next.kind === "daily" ? { kind: "daily", date: next.date } : { kind: "page", pageId: next.pageId });
      return;
    }
    const current = selected?.kind === "page" ? registry.get(selected.pageId) : null;
    if (current) void current.flush();
    setSelected(next);
    setAnchor(null);
    setMissingQuote(null);
    try {
      if (next?.kind === "page") localStorage.setItem("chaos.pages.open", next.pageId);
    } catch {
      /* storage unavailable */
    }
  }

  React.useEffect(() => {
    if (!focus) return;
    if (focus.kind === "page") {
      open({ kind: "page", pageId: focus.pageId });
      const wanted = focus.anchor;
      if (wanted && wanted.state !== "Missing" && wanted.start !== null && wanted.end !== null)
        setAnchor({ start: wanted.start, end: wanted.end, key: focus.key });
      else if (wanted) setMissingQuote(wanted.quote);
    } else if (focus.kind === "daily") open({ kind: "daily", date: focus.date });
    else if (focus.kind === "name") void followName(focus.name);
  }, [focus?.key]);

  /** Follows a `[[Name]]` link: the page with that title or alias, or a new page created now. */
  async function followName(name: string) {
    try {
      const found = await api.resolveNames({ names: [name] });
      const target = found.ok ? found.targets[0] : undefined;
      if (target) {
        open(target.dailyDate ? { kind: "daily", date: target.dailyDate } : { kind: "page", pageId: target.id });
        return;
      }
      const created = await api.create({ operationId: operationId(), parentPageId: null, title: name, markdown: "" });
      const page = created.ok ? created.pages[0] : undefined;
      if (!page)
        throw new Error(
          created.error?.fields.map((field) => field.message).join("; ") || created.error?.message || "Could not create the page.",
        );
      // The page the link was followed from now points at the new page by ID, so a rename keeps it.
      const source = selected?.kind === "page" ? registry.get(selected.pageId) : null;
      if (source) pendingRewrite.current = { name, pageId: page.id };
      setNotice(`Created the page “${name}”.`);
      await load();
      onChanged();
      if (source) rewriteLinks(source, name, page.id);
      open({ kind: "page", pageId: page.id });
    } catch (e) {
      onError(e instanceof Error ? e.message : String(e));
    }
  }

  function rewriteLinks(saver: DraftSaver, name: string, pageId: string) {
    const { title, markdown } = decodeDraft(saver.snapshot.text);
    const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
    const pattern = new RegExp(`\\[\\[\\s*${escaped}\\s*(?:\\|([^\\]\\n]+))?\\]\\]`, "gi");
    const label = (text: string) => text.replace(/[[\]\\]/g, (character) => `\\${character}`);
    const next = markdown.replace(
      pattern,
      (_match, alias: string | undefined) => `[${label(alias?.trim() || name)}](chaos:page/${pageId})`,
    );
    if (next !== markdown) saver.edit(encodeDraft(title, next));
    pendingRewrite.current = null;
  }

  async function run<T extends { ok: boolean; error: { message: string; fields?: readonly { message: string }[] } | null }>(
    action: () => Promise<T>,
    failure: string,
    after?: (result: T) => void,
  ) {
    if (busy) return;
    setBusy(true);
    onError(null);
    setNotice(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.fields?.map((field) => field.message).join("; ") || result.error?.message || failure);
      after?.(result);
      await load();
      onChanged();
    } catch (e) {
      onError(e instanceof Error ? e.message : failure);
    } finally {
      setBusy(false);
    }
  }

  async function createPage(title: string, parentId: string | null) {
    await run(
      () => api.create({ operationId: operationId(), parentPageId: parentId, title, markdown: "" }),
      "Could not create the page.",
      (result) => {
        setAdding(null);
        const page = result.pages[0];
        if (page) {
          if (parentId) expand(parentId, true);
          open({ kind: "page", pageId: page.id });
        }
      },
    );
  }

  const [expanded, setExpanded] = React.useState<ReadonlySet<string>>(() => {
    try {
      return new Set(JSON.parse(localStorage.getItem("chaos.pages.expanded") ?? "[]") as string[]);
    } catch {
      return new Set();
    }
  });
  function expand(id: string, value?: boolean) {
    setExpanded((current) => {
      const next = new Set(current);
      if (value ?? !next.has(id)) next.add(id);
      else next.delete(id);
      try {
        localStorage.setItem("chaos.pages.expanded", JSON.stringify([...next]));
      } catch {
        /* storage unavailable */
      }
      return next;
    });
  }

  const children = React.useMemo(() => {
    const map = new Map<string | null, PageSummaryResponse[]>();
    const ids = new Set(tree.map((page) => page.id));
    for (const page of tree) {
      const parent = page.parentPageId && ids.has(page.parentPageId) ? page.parentPageId : null;
      map.set(parent, [...(map.get(parent) ?? []), page]);
    }
    for (const list of map.values()) list.sort((a, b) => Number(a.position) - Number(b.position) || a.id.localeCompare(b.id));
    return map;
  }, [tree]);

  // Ancestors of the open page stay expanded so it is visible in the tree.
  const openPageId = selected?.kind === "page" ? selected.pageId : null;
  React.useEffect(() => {
    if (!openPageId) return;
    const byId = new Map(tree.map((page) => [page.id, page]));
    for (let parent = byId.get(openPageId)?.parentPageId; parent; parent = byId.get(parent)?.parentPageId)
      if (!expanded.has(parent)) expand(parent, true);
  }, [openPageId, tree]);

  function renderTree(parent: string | null, depth: number): React.ReactNode {
    const rows = children.get(parent) ?? [];
    if (rows.length === 0 && !(adding && adding.parentId === parent)) return null;
    return (
      <ul className="grid gap-px" role={depth === 0 ? "tree" : "group"} aria-label={depth === 0 ? "Pages" : undefined}>
        {rows.map((page) => {
          const kids = children.get(page.id) ?? [];
          const isOpen = expanded.has(page.id);
          const current = openPageId === page.id;
          return (
            <li key={page.id} role="treeitem" aria-expanded={kids.length > 0 ? isOpen : undefined} aria-selected={current}>
              <div
                className={cn("group/page flex items-center rounded-md pr-1 hover:bg-accent", current && "bg-brand-soft text-primary")}
                style={{ paddingLeft: `${depth * 0.85}rem` }}
              >
                <button
                  type="button"
                  className={cn("grid size-5 shrink-0 place-items-center rounded text-muted-foreground", kids.length === 0 && "invisible")}
                  aria-label={isOpen ? `Collapse ${page.title}` : `Expand ${page.title}`}
                  onClick={() => expand(page.id)}
                >
                  <ChevronRight className={cn("size-3.5 transition-transform", isOpen && "rotate-90")} />
                </button>
                <button
                  type="button"
                  className={cn("flex min-w-0 flex-1 items-center gap-1.5 py-1 text-left text-sm", current && "font-semibold")}
                  onClick={() => open({ kind: "page", pageId: page.id })}
                >
                  {page.icon ? (
                    <span aria-hidden="true" className="w-4 shrink-0 text-center">
                      {page.icon}
                    </span>
                  ) : (
                    <FileText className="size-3.5 shrink-0 text-muted-foreground" />
                  )}
                  <span className="truncate">{page.title}</span>
                </button>
                <button
                  type="button"
                  className="rounded p-0.5 text-muted-foreground opacity-0 group-hover/page:opacity-100 focus-visible:opacity-100 hover:text-foreground"
                  aria-label={`Add a page under ${page.title}`}
                  title="Add a page under this one"
                  onClick={() => {
                    expand(page.id, true);
                    setAdding({ parentId: page.id });
                  }}
                >
                  <Plus className="size-3.5" />
                </button>
              </div>
              {isOpen && renderTree(page.id, depth + 1)}
              {!isOpen && adding?.parentId === page.id && (
                <NewPageForm
                  depth={depth + 1}
                  busy={busy}
                  onCreate={(title) => void createPage(title, page.id)}
                  onCancel={() => setAdding(null)}
                />
              )}
            </li>
          );
        })}
        {adding && adding.parentId === parent && (
          <li>
            <NewPageForm depth={depth} busy={busy} onCreate={(title) => void createPage(title, parent)} onCancel={() => setAdding(null)} />
          </li>
        )}
      </ul>
    );
  }

  const dailyShown = allDaily ? daily : daily.slice(0, 7);
  const todayWritten = daily.some((page) => page.dailyDate === today);

  return (
    <div
      className="grid min-h-[calc(100vh-40px)] content-start items-start gap-5 px-[clamp(1rem,4vw,3rem)] pt-8 pb-16 lg:grid-cols-[14rem_minmax(0,1fr)]"
      style={sharedBackground}
    >
      <nav
        aria-label={isDailyMode ? "Daily pages" : "Pages"}
        className={cn(
          "grid content-start gap-4 lg:sticky lg:top-[56px] lg:max-h-[calc(100vh-72px)] lg:overflow-y-auto",
          sharedBackground && "rounded-xl bg-background/92 p-4 shadow-lg backdrop-blur-sm",
        )}
      >
        {isDailyMode && (
          <section className="grid gap-1">
            <h2 className="flex items-center justify-between px-1 text-[0.7rem] font-semibold tracking-wider text-muted-foreground uppercase">
              <span>Daily pages</span>
            </h2>
            <Button
              type="button"
              variant={selected?.kind === "daily" && selected.date === today ? "secondary" : "outline"}
              size="sm"
              className="justify-start"
              onClick={() => open({ kind: "daily", date: today })}
            >
              <CalendarDays />
              Today's page{!todayWritten && <span className="ml-auto text-xs font-normal text-muted-foreground">empty</span>}
            </Button>
            <ul className="grid gap-px" aria-label="Daily pages by date">
              {dailyShown
                .filter((page) => page.dailyDate !== today)
                .map((page) => (
                  <li key={page.id}>
                    <button
                      type="button"
                      className={cn(
                        "flex w-full items-center gap-1.5 rounded-md px-2 py-1 text-left text-sm hover:bg-accent",
                        selected?.kind === "daily" && selected.date === page.dailyDate && "bg-brand-soft font-semibold text-primary",
                      )}
                      onClick={() => open({ kind: "daily", date: page.dailyDate! })}
                    >
                      <span className="truncate">{dailyTitle(page.dailyDate!)}</span>
                      <span className="ml-auto text-[0.7rem] text-muted-foreground tabular-nums">{page.wordCount}w</span>
                    </button>
                  </li>
                ))}
            </ul>
            {daily.length > 7 && (
              <Button
                type="button"
                variant="ghost"
                size="xs"
                className="justify-start text-muted-foreground"
                onClick={() => setAllDaily((value) => !value)}
              >
                {allDaily ? "Show recent days" : `Show all ${daily.length} days`}
              </Button>
            )}
          </section>
        )}
        {!isDailyMode && (
          <section className="grid gap-1">
            <h2 className="flex items-center justify-between px-1 text-[0.7rem] font-semibold tracking-wider text-muted-foreground uppercase">
              <span>Pages</span>
              <Button
                type="button"
                variant="ghost"
                size="icon-xs"
                aria-label="New page"
                title="New page"
                onClick={() => setAdding({ parentId: null })}
              >
                <Plus />
              </Button>
            </h2>
            {loaded && tree.length === 0 && !adding && (
              <p className="px-1 text-sm text-muted-foreground">No pages yet. Add one to start writing.</p>
            )}
            {renderTree(null, 0)}
          </section>
        )}
        <DeletedItems
          noun={isDailyMode ? "daily pages" : "pages"}
          items={deleted.filter((page) =>
            isDailyMode ? !!page.dailyDate && !daily.some((day) => day.dailyDate === page.dailyDate) : !page.dailyDate,
          )}
          name={(page) => (page.dailyDate ? dailyTitle(page.dailyDate) : page.title)}
          busy={busy}
          onRestore={(page) =>
            void run(
              () => api.restore({ operationId: operationId(), pageId: page.id, expectedVersion: page.version }),
              "Could not restore the page.",
              () => open(page.dailyDate ? { kind: "daily", date: page.dailyDate } : { kind: "page", pageId: page.id }),
            )
          }
        />
      </nav>

      <div className="grid min-w-0 content-start gap-4">
        {notice && (
          <Banner tone="warning" onDismiss={() => setNotice(null)}>
            {notice}
          </Banner>
        )}
        {missingQuote !== null && (
          <Banner tone="warning" onDismiss={() => setMissingQuote(null)}>
            The text this task came from was changed or removed. It said: “{missingQuote}”
          </Banner>
        )}
        {selected === null ? (
          <EmptyState title="Pages are for writing you keep.">
            Add a page for meeting notes, a project write-up or a recipe collection.
          </EmptyState>
        ) : (
          <PageEditor
            key={selected.kind === "page" ? selected.pageId : `daily-${selected.date}`}
            selected={selected}
            api={api}
            summaryApi={summaryApi}
            captureApi={captureApi}
            registry={registry}
            containers={containers}
            tags={tags}
            tree={tree}
            revision={revision}
            today={today}
            anchor={anchor}
            links={links}
            searchLinks={searchLinks}
            busy={busy}
            onBackground={(style) => setPageBackground({ key: selectedKey!, style })}
            onOpen={open}
            onChanged={async () => {
              await load();
              onChanged();
            }}
            onOpenTask={onOpenTask}
            onOpenSummary={onOpenSummary}
            onCreateTag={onCreateTag}
            onError={onError}
            onNotice={setNotice}
          />
        )}
      </div>
    </div>
  );
}

function NewPageForm({
  depth,
  busy,
  onCreate,
  onCancel,
}: {
  depth: number;
  busy: boolean;
  onCreate: (title: string) => void;
  onCancel: () => void;
}) {
  const [title, setTitle] = React.useState("");
  return (
    <form
      className="flex items-center gap-1 py-0.5"
      style={{ paddingLeft: `${depth * 0.85 + 1.25}rem` }}
      onSubmit={(event) => {
        event.preventDefault();
        if (title.trim()) onCreate(title.trim());
      }}
    >
      <label className="sr-only" htmlFor="new-page-title">
        New page title
      </label>
      <Input
        id="new-page-title"
        autoFocus
        className="h-7 text-sm"
        placeholder="Page title"
        value={title}
        maxLength={200}
        onChange={(event) => setTitle(event.target.value)}
        onKeyDown={(event) => {
          if (event.key === "Escape") onCancel();
        }}
        autoComplete="off"
      />
      <Button type="submit" size="xs" disabled={busy || !title.trim()}>
        Add
      </Button>
    </form>
  );
}

type EditorProps = {
  selected: NonNullable<Selected>;
  api: PageApi;
  summaryApi: typeof summaryRpc;
  captureApi?: CapturePreviewApi;
  registry: DraftRegistry;
  containers: ContainerResponse[];
  tags: TagResponse[];
  tree: PageSummaryResponse[];
  revision: number;
  today: string;
  anchor: { start: number; end: number; key: number } | null;
  links: MarkdownLinks;
  searchLinks?: (query: string) => Promise<LinkCandidate[]>;
  busy: boolean;
  onOpen: (next: Selected) => void;
  onBackground: (style: React.CSSProperties | undefined) => void;
  onChanged: () => Promise<void>;
  onOpenTask: (taskId: string) => void;
  onOpenSummary: (summaryId: string) => void;
  onCreateTag: (path: string) => Promise<boolean>;
  onError: (message: string | null) => void;
  onNotice: (message: string | null) => void;
};

/** One page: title, properties, the editor, contents, backlinks, tasks and history. */
function PageEditor({
  selected,
  api,
  summaryApi,
  captureApi,
  registry,
  containers,
  tags,
  tree,
  revision,
  today,
  anchor,
  links,
  searchLinks,
  busy: treeBusy,
  onOpen,
  onBackground,
  onChanged,
  onOpenTask,
  onOpenSummary,
  onCreateTag,
  onError,
  onNotice,
}: EditorProps) {
  const [page, setPage] = React.useState<PageResponse | null>(null);
  const [loading, setLoading] = React.useState(true);
  const [busy, setBusy] = React.useState(false);
  const [draft, setDraft] = React.useState(""); // an unwritten daily page's text, until it is stored
  const draftRef = React.useRef("");
  const creating = React.useRef(false);
  const [selection, setSelection] = React.useState({ start: 0, end: 0 });
  const [makeTask, setMakeTask] = React.useState<{ start: number; end: number; text: string; subtasks: CaptureSubtaskDto[] } | null>(null);
  const [selectRange, setSelectRange] = React.useState<{ start: number; end: number; key: number } | null>(null);
  const [focusMode, setFocusMode] = React.useState(false);
  const [editing, setEditing] = React.useState(false);
  const [showContents, setShowContents] = React.useState(() => {
    try {
      return localStorage.getItem("chaos.pages.contents") !== "0";
    } catch {
      return true;
    }
  });
  const [showHistory, setShowHistory] = React.useState(false);
  const [showMove, setShowMove] = React.useState(false);
  const [rawYaml, setRawYaml] = React.useState(false);
  const [summary, setSummary] = React.useState<SummaryDetailDto | null>(null);
  const isDaily = selected.kind === "daily";

  async function reload() {
    const result = selected.kind === "page" ? await api.get({ pageId: selected.pageId }) : await api.getDaily({ date: selected.date });
    if (!result.ok) throw new Error(result.error?.message ?? "Could not open the page.");
    const loaded = result.page ?? null;
    if (loaded) {
      const existing = registry.get(loaded.id);
      if (existing && !existing.dirty && existing.snapshot.status !== "saving") {
        if (decodeDraft(existing.snapshot.text).markdown !== loaded.markdown || decodeDraft(existing.snapshot.text).title !== loaded.title)
          registry.forget(loaded.id);
        else existing.updateVersion(loaded.version);
      }
    }
    setPage(loaded);
    setLoading(false);
  }
  React.useEffect(() => {
    void reload().catch((e: unknown) => {
      setLoading(false);
      onError(e instanceof Error ? e.message : String(e));
    });
  }, [revision]);

  // A daily page shows that day's evening summary beside it.
  React.useEffect(() => {
    if (selected.kind !== "daily") {
      setSummary(null);
      return;
    }
    let current = true;
    void summaryApi
      .find({ kind: "Daily", date: selected.date })
      .then((result) => {
        if (current) setSummary(result.ok ? result.summary : null);
      })
      .catch(() => {
        if (current) setSummary(null);
      });
    return () => {
      current = false;
    };
  }, [selected.kind === "daily" ? selected.date : null, revision]);

  const saver = React.useMemo(
    () => (page && !page.deleted ? saverFor(registry, api, page) : null),
    [page?.id, page?.markdown, page?.title, page?.deleted, registry, api],
  );
  const snapshot = useSnapshot(saver);
  const decoded = snapshot ? decodeDraft(snapshot.text) : { title: page?.title ?? "", markdown: page ? page.markdown : draft };
  const title = isDaily ? dailyTitle(selected.date, today) : decoded.title;
  const markdown = decoded.markdown;
  const { head, body, frontMatter } = splitPage(markdown);
  const properties = pageProperties(frontMatter);
  const prefix = joinPage(head, "").length;
  const attachmentApi = links.attachmentApi ?? attachmentRpc;
  const backgroundImage = usePageImage(attachmentApi, page?.id ?? null, attachmentBackgroundName(properties.background));
  const background = pageBackgroundStyle(properties.background, backgroundImage);
  React.useEffect(() => {
    onBackground(background.style);
  }, [background.style?.backgroundImage, background.style?.backgroundColor]);
  const headings = React.useMemo(() => headingsOf(body), [body]);
  const words = React.useMemo(() => wordCount(body), [body]);

  React.useEffect(() => {
    if (anchor && saver) {
      setEditing(true);
      setSelectRange({ start: Math.max(0, anchor.start - prefix), end: Math.max(0, anchor.end - prefix), key: anchor.key });
    }
  }, [anchor?.key, saver]);

  // Esc leaves focus mode.
  React.useEffect(() => {
    if (!focusMode) return;
    function escape(event: KeyboardEvent) {
      if (event.key === "Escape" && !event.defaultPrevented) {
        event.preventDefault();
        setFocusMode(false);
      }
    }
    window.addEventListener("keydown", escape);
    return () => window.removeEventListener("keydown", escape);
  }, [focusMode]);

  /** All text changes go through here: the stored page's autosave, or the first write of a daily page. */
  function setMarkdown(next: string, nextTitle = decoded.title) {
    if (saver) {
      saver.edit(encodeDraft(nextTitle, next));
      return;
    }
    if (selected.kind !== "daily") return;
    draftRef.current = next;
    setDraft(next);
    if (creating.current || !next.trim()) return;
    creating.current = true;
    const date = selected.date;
    void (async () => {
      try {
        const start = loadDailyBackground();
        const initial = start && !parseFrontMatter(next).present ? setProperty(next, "background", start) : next;
        const result = await api.createDaily({ operationId: operationId(), date, markdown: initial });
        const created = result.ok ? result.pages[0] : undefined;
        if (!created) throw new Error(result.error?.message ?? "Could not start today's page.");
        const createdSaver = saverFor(registry, api, created);
        const latest = draftRef.current;
        const wanted = start && !parseFrontMatter(latest).present ? setProperty(latest, "background", start) : latest;
        if (decodeDraft(createdSaver.snapshot.text).markdown !== wanted) createdSaver.edit(encodeDraft(created.title, wanted));
        await reload();
        await onChanged();
      } catch (e) {
        creating.current = false;
        onError(e instanceof Error ? e.message : "Could not start the daily page.");
      }
    })();
  }

  function editBody(next: string) {
    // Typing --- on the first line of a page opens the properties panel.
    if (!head && /^---\r?\n/.test(next) && !body.startsWith("---") && !parseFrontMatter(next).present) {
      setMarkdown(joinPage(withYaml(""), next.replace(/^---\r?\n/, "")));
      return;
    }
    setMarkdown(joinPage(head, next));
  }

  function setProp(key: string, value: string | string[] | null) {
    setMarkdown(setProperty(markdown, key, value));
  }

  async function runPage<T extends { ok: boolean; error: { message: string; fields?: readonly { message: string }[] } | null }>(
    action: () => Promise<T>,
    failure: string,
    after?: (result: T) => void,
  ) {
    if (busy) return;
    setBusy(true);
    onError(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.fields?.map((field) => field.message).join("; ") || result.error?.message || failure);
      after?.(result);
      await onChanged();
    } catch (e) {
      onError(e instanceof Error ? e.message : failure);
    } finally {
      setBusy(false);
    }
  }

  /** Structural changes need the page's latest row version: unsaved text goes first. */
  async function settledVersion(): Promise<string | null> {
    if (!page) return null;
    if (!saver) return page.version;
    if (!(await saver.flush())) {
      onError("Save the page first: the latest change could not be saved.");
      return null;
    }
    return saver.snapshot.version;
  }

  async function remove() {
    if (!page) return;
    const under = tree.filter((row) => row.parentPageId === page.id).length;
    if (
      !window.confirm(
        under > 0
          ? `Delete “${page.title}” and the pages under it? You can restore them from Deleted pages.`
          : `Delete “${isDaily ? title : page.title}”? You can restore it from Deleted pages.`,
      )
    )
      return;
    const version = await settledVersion();
    if (version === null) return;
    await runPage(
      () => api.delete({ operationId: operationId(), pageId: page.id, expectedVersion: version }),
      "Could not delete the page.",
      () => {
        registry.forget(page.id);
        if (!isDaily) onOpen(null);
        else void reload();
      },
    );
  }

  async function move(parentId: string | null, insertionIndex: number) {
    if (!page) return;
    const version = await settledVersion();
    if (version === null) return;
    await runPage(
      () => api.move({ operationId: operationId(), pageId: page.id, expectedVersion: version, parentPageId: parentId, insertionIndex }),
      "Could not move the page.",
      () => setShowMove(false),
    );
  }

  function startMakeTask() {
    if (selection.end <= selection.start) return;
    const text = body.slice(selection.start, selection.end);
    setMakeTask({ start: selection.start, end: selection.end, text: titleFromSelection(text), subtasks: selectionSubtasks(text) });
  }

  async function submitMakeTask(drafts: CaptureDraftDto[], operation: string): Promise<string | null> {
    const first = drafts[0];
    if (!makeTask || !saver || !page || !first) return "Select text on the page first.";
    if (!(await saver.flush())) return "The page could not be saved, so the task was not made. Retry saving first.";
    const current = saver.snapshot;
    const currentText = decodeDraft(current.text).markdown;
    const offset = joinPage(splitPage(currentText).head, "").length;
    const start = offset + makeTask.start;
    const end = offset + makeTask.end;
    const result = await api.makeTask({
      operationId: operation,
      pageId: page.id,
      sourceRevision: String(current.revision),
      start,
      end,
      quote: currentText.slice(start, end),
      draft: { ...first, subtasks: [...first.subtasks, ...makeTask.subtasks] },
    });
    if (!result.ok)
      return result.error?.fields.map((field) => field.message).join("; ") || result.error?.message || "Could not make the task.";
    setMakeTask(null);
    onNotice(`Made task “${result.task?.title ?? first.title}”. It links back to this page.`);
    await reload();
    await onChanged();
    return null;
  }

  async function createTag(path: string) {
    if (await onCreateTag(path)) await saver?.resave();
  }

  async function copyDraft() {
    try {
      await navigator.clipboard.writeText(markdown);
      onNotice("Copied the unsaved text to the clipboard.");
    } catch {
      onError("Could not copy the text. Select it in the editor and copy it by hand.");
    }
  }

  function jumpTo(index: number) {
    const target = document.getElementById(headingAnchor(index));
    if (target) {
      target.scrollIntoView({ block: "start", behavior: "smooth" });
      return;
    }
    const heading = headings[index];
    if (heading) setSelectRange({ start: heading.start, end: heading.start, key: Date.now() });
  }

  if (loading)
    return (
      <p className="text-sm text-muted-foreground" role="status">
        Opening the page…
      </p>
    );
  if (!page && !isDaily)
    return (
      <EmptyState title="This page is not available.">
        It may have been deleted. Restore it from Deleted pages, or choose another page.
      </EmptyState>
    );
  if (page?.deleted)
    return <EmptyState title="This page is deleted.">Restore it from Deleted pages in the list on the left to keep writing.</EmptyState>;

  const byId = new Map(tree.map((row) => [row.id, row]));
  const ancestors: PageSummaryResponse[] = [];
  for (
    let parent = page?.parentPageId ? byId.get(page.parentPageId) : undefined;
    parent && ancestors.length < 32;
    parent = parent.parentPageId ? byId.get(parent.parentPageId) : undefined
  )
    ancestors.unshift(parent);
  const siblings =
    page && !isDaily
      ? tree
          .filter((row) => (row.parentPageId ?? null) === (page.parentPageId ?? null))
          .sort((a, b) => Number(a.position) - Number(b.position))
      : [];
  const siblingIndex = page ? siblings.findIndex((row) => row.id === page.id) : -1;
  const descendants = new Set<string>(page ? [page.id] : []);
  for (let grew = true; grew;) {
    grew = false;
    for (const row of tree)
      if (row.parentPageId && descendants.has(row.parentPageId) && !descendants.has(row.id)) {
        descendants.add(row.id);
        grew = true;
      }
  }
  const tagByPath = new Map(tags.map((tag) => [tag.path.toLowerCase(), tag]));
  const pageLinks: MarkdownLinks = { ...links, outline: true };

  const editorArea = (
    <div className="grid gap-2">
      {editing ? (
        <MarkdownEditor
          key={isDaily ? "daily" : page?.id}
          id={isDaily ? "page-daily" : `page-${page?.id}`}
          label={isDaily ? "Daily page" : "Page"}
          spaceId={page?.spaceId}
          attachmentOwner={page ? { kind: "page", id: page.id } : undefined}
          attachmentApi={links.attachmentApi}
          value={body}
          onChange={editBody}
          onBlur={() => void saver?.flush()}
          onSelectionChange={(start, end) => setSelection({ start, end })}
          selectRange={selectRange}
          outline
          grow
          links={pageLinks}
          searchLinks={searchLinks}
          rows={focusMode ? 24 : 16}
          areaClassName="min-h-80"
          placeholder={
            isDaily
              ? "How did today go? This page is kept once you write in it."
              : "Write anything. Lists work as an outline: Tab, Shift+Tab, Alt+↑ and Alt+↓."
          }
          hint={
            <p className="text-[0.7rem] text-muted-foreground">
              Markdown. <kbd className="font-mono">[[</kbd> links a page, task or list · Tab/Shift+Tab indent list items · Alt+↑/↓ move them
              · Read mode folds headings and lists.
            </p>
          }
        />
      ) : body.trim() ? (
        <div className="min-h-24 py-2 text-sm">
          <MarkdownPreview source={body} links={pageLinks} />
        </div>
      ) : (
        <p className="py-2 text-sm text-muted-foreground">This page is empty. Select Edit to start writing.</p>
      )}
      <SaveLine snapshot={snapshot} unwritten={!page} words={words} onRetry={() => void saver?.flush()} onCopy={() => void copyDraft()} />
    </div>
  );

  const titleBlock = (
    <div className="grid gap-1">
      {!isDaily && ancestors.length > 0 && (
        <nav aria-label="Page path" className="flex flex-wrap items-center gap-1 text-xs text-muted-foreground">
          {ancestors.map((row) => (
            <React.Fragment key={row.id}>
              <button
                type="button"
                className="hover:text-foreground hover:underline"
                onClick={() => onOpen({ kind: "page", pageId: row.id })}
              >
                {row.icon ? `${row.icon} ` : ""}
                {row.title}
              </button>
              <ChevronRight className="size-3" />
            </React.Fragment>
          ))}
        </nav>
      )}
      {isDaily || !editing ? (
        <h1 className="flex items-center gap-2 text-2xl font-bold tracking-tight">
          {properties.icon && <span aria-hidden="true">{properties.icon}</span>}
          {title}
        </h1>
      ) : (
        <div className="flex items-center gap-2">
          {properties.icon && (
            <span aria-hidden="true" className="text-2xl">
              {properties.icon}
            </span>
          )}
          <label htmlFor="page-title" className="sr-only">
            Page title
          </label>
          <input
            id="page-title"
            className="min-w-0 flex-1 rounded-md bg-transparent px-1 text-2xl font-bold tracking-tight outline-none focus-visible:ring-[3px] focus-visible:ring-ring/40"
            value={title}
            maxLength={200}
            dir="auto"
            onChange={(event) => setMarkdown(markdown, event.target.value)}
            onBlur={() => {
              if (!title.trim() && page) setMarkdown(markdown, page.title);
              void saver?.flush();
            }}
            onKeyDown={(event) => {
              if (event.key === "Enter") {
                event.preventDefault();
                (document.getElementById(`page-${page?.id}`) as HTMLTextAreaElement | null)?.focus();
              }
            }}
          />
        </div>
      )}
    </div>
  );

  const editButton = (
    <Button
      type="button"
      variant={editing ? "secondary" : "outline"}
      size="sm"
      onClick={() => {
        if (editing) {
          void saver?.flush();
          setSelection({ start: 0, end: 0 });
          setMakeTask(null);
        }
        setEditing(!editing);
      }}
    >
      <PenLine />
      {editing ? "Done editing" : "Edit"}
    </Button>
  );

  const panel = (
    <PropertiesPanel
      frontMatter={frontMatter}
      head={head}
      body={body}
      raw={rawYaml}
      setRaw={setRawYaml}
      tagByPath={tagByPath}
      properties={properties}
      backgroundNote={background.note ?? null}
      onSet={setProp}
      onRaw={(yaml) => setMarkdown(joinPage(withYaml(yaml), body))}
      onRemoveAll={() => setMarkdown(body)}
      onCreateTag={(path) => void createTag(path)}
    />
  );

  if (focusMode) {
    return (
      <div
        role="dialog"
        aria-modal="true"
        aria-label={`${title} (focus mode)`}
        className="fixed inset-x-0 top-[40px] bottom-0 z-40 overflow-y-auto bg-background"
        style={background.style}
      >
        <div
          className={cn(
            "mx-auto grid min-h-full max-w-[74ch] content-start gap-4 px-6 py-10",
            background.style && "bg-background/92 shadow-xl",
          )}
        >
          <div className="flex justify-end gap-2">
            {editButton}
            <Button type="button" variant="outline" size="sm" onClick={() => setFocusMode(false)}>
              <Minimize2 />
              Leave focus mode<kbd className="ml-1 text-xs text-muted-foreground">Esc</kbd>
            </Button>
          </div>
          {titleBlock}
          {editorArea}
        </div>
      </div>
    );
  }

  return (
    <article aria-label={title} className="grid gap-4 rounded-xl">
      <div className={cn("grid gap-4", background.style && "rounded-xl bg-background/92 p-4 shadow-lg backdrop-blur-sm sm:p-6")}>
        <div className="flex flex-wrap items-start justify-between gap-3">
          {titleBlock}
          <div className="flex flex-wrap items-center gap-1">
            {editButton}
            <Button
              type="button"
              variant={showContents ? "secondary" : "ghost"}
              size="sm"
              aria-pressed={showContents}
              title="Contents from the page's headings"
              onClick={() =>
                setShowContents((value) => {
                  try {
                    localStorage.setItem("chaos.pages.contents", value ? "0" : "1");
                  } catch {
                    /* storage unavailable */
                  }
                  return !value;
                })
              }
            >
              <ListTree />
              Contents
            </Button>
            <Button
              type="button"
              variant="ghost"
              size="sm"
              title="Write at a comfortable width with nothing else on screen (Esc leaves)"
              onClick={() => setFocusMode(true)}
            >
              <Focus />
              Focus
            </Button>
            {page && (
              <Button
                type="button"
                variant={showHistory ? "secondary" : "ghost"}
                size="sm"
                aria-pressed={showHistory}
                onClick={() => setShowHistory((value) => !value)}
              >
                <History />
                Versions
              </Button>
            )}
            {editing && !frontMatter.present && !frontMatter.error && (
              <Button
                type="button"
                variant="ghost"
                size="sm"
                title="Add front matter: icon, background, tags, aliases"
                onClick={() => setMarkdown(joinPage(withYaml(""), body))}
              >
                <Settings2 />
                Properties
              </Button>
            )}
            {page && !isDaily && (
              <Button
                type="button"
                variant={showMove ? "secondary" : "ghost"}
                size="sm"
                aria-pressed={showMove}
                onClick={() => setShowMove((value) => !value)}
              >
                <MoveRight />
                Move
              </Button>
            )}
            {page && (
              <Button
                type="button"
                variant="ghost"
                size="icon-sm"
                className="text-muted-foreground hover:bg-destructive/10 hover:text-destructive"
                aria-label="Delete page"
                title="Delete page"
                disabled={busy || treeBusy}
                onClick={() => void remove()}
              >
                <Trash2 />
              </Button>
            )}
          </div>
        </div>

        {showMove && page && (
          <div className="flex flex-wrap items-center gap-2 rounded-lg border bg-muted/40 p-3 text-sm">
            <label htmlFor="page-parent">Put under</label>
            <NativeSelect
              id="page-parent"
              value={page.parentPageId ?? ""}
              disabled={busy}
              onChange={(event) => {
                const parent = event.target.value || null;
                void move(parent, tree.filter((row) => (row.parentPageId ?? null) === parent).length);
              }}
            >
              <NativeSelectOption value="">Top level</NativeSelectOption>
              {tree
                .filter((row) => !descendants.has(row.id))
                .map((row) => (
                  <NativeSelectOption key={row.id} value={row.id}>
                    {pathOf(row, byId)}
                  </NativeSelectOption>
                ))}
            </NativeSelect>
            <span className="text-muted-foreground">Order:</span>
            <Button
              type="button"
              size="xs"
              variant="outline"
              disabled={busy || siblingIndex <= 0}
              onClick={() => void move(page.parentPageId, siblingIndex - 1)}
            >
              Up
            </Button>
            <Button
              type="button"
              size="xs"
              variant="outline"
              disabled={busy || siblingIndex < 0 || siblingIndex >= siblings.length - 1}
              onClick={() => void move(page.parentPageId, siblingIndex + 1)}
            >
              Down
            </Button>
            <Button
              type="button"
              size="icon-xs"
              variant="ghost"
              className="ml-auto"
              aria-label="Close move"
              onClick={() => setShowMove(false)}
            >
              <X />
            </Button>
          </div>
        )}

        {editing && (frontMatter.present || frontMatter.error) && panel}

        {isDaily && summary && (
          <aside
            aria-label="That day's summary"
            className="flex flex-wrap items-center gap-3 rounded-lg border bg-muted/40 px-3 py-2 text-sm"
          >
            <BookOpen className="size-4 text-muted-foreground" />
            <span>
              Evening summary: {summary.header.counts.completionEvents} done, {summary.header.counts.unfinished} unfinished
              {summary.header.counts.waiting > 0 ? `, ${summary.header.counts.waiting} waiting` : ""}.
            </span>
            <Button type="button" size="xs" variant="outline" className="ml-auto" onClick={() => onOpenSummary(summary.header.id)}>
              Open summary
            </Button>
          </aside>
        )}

        <div className={cn("grid gap-4", showContents && headings.length > 0 && "xl:grid-cols-[minmax(0,1fr)_12rem]")}>
          {editorArea}
          {showContents && headings.length > 0 && (
            <nav
              aria-label="Contents"
              className="order-first grid content-start gap-1 rounded-lg border p-3 text-sm xl:order-none xl:sticky xl:top-[56px]"
            >
              <h2 className="text-[0.7rem] font-semibold tracking-wider text-muted-foreground uppercase">Contents</h2>
              <ol className="grid gap-0.5">
                {headings.map((heading, index) => (
                  <li
                    key={`${heading.start}`}
                    style={{ paddingLeft: `${(heading.level - Math.min(...headings.map((item) => item.level))) * 0.75}rem` }}
                  >
                    <button
                      type="button"
                      className="w-full truncate rounded px-1 py-0.5 text-left hover:bg-accent"
                      title={heading.text}
                      onClick={() => jumpTo(index)}
                    >
                      {heading.text}
                    </button>
                  </li>
                ))}
              </ol>
            </nav>
          )}
        </div>

        {editing && page && (
          <div className="flex flex-wrap items-center gap-2 border-t pt-3">
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={busy || selection.end <= selection.start}
              title="Select a line or a few lines first"
              onClick={startMakeTask}
            >
              <ListPlus />
              Make task
            </Button>
            {selection.end <= selection.start && (
              <span className="text-xs text-muted-foreground">
                Select a line or a few lines to make a task. Lines indented under the first one become subtasks.
              </span>
            )}
          </div>
        )}
        {makeTask && (
          <div className="grid gap-2 rounded-lg border p-3">
            <blockquote className="border-l-2 pl-3 text-xs whitespace-pre-wrap text-muted-foreground [overflow-wrap:anywhere]">
              {body.slice(makeTask.start, makeTask.end)}
            </blockquote>
            <QuickAdd
              key={`${makeTask.start}-${makeTask.end}`}
              api={captureApi}
              containers={containers}
              defaultContainerId=""
              label="Make task from selection"
              initialText={makeTask.text}
              allowBatch={false}
              submitLabel="Create task"
              autoFocus
              onSubmit={submitMakeTask}
              onCancel={() => setMakeTask(null)}
            />
            {makeTask.subtasks.length > 0 && (
              <p className="text-xs text-muted-foreground">Subtasks: {makeTask.subtasks.map((subtask) => subtask.title).join(" · ")}</p>
            )}
            <p className="text-xs text-muted-foreground">The page stays as it is; the task links back to this spot.</p>
          </div>
        )}

        {page && page.tasks.length > 0 && (
          <div className="flex flex-wrap items-center gap-1.5 text-xs" aria-label="Tasks made from this page">
            <span className="text-muted-foreground">Tasks from this page:</span>
            {page.tasks.map((task) => (
              <Button
                key={task.taskId}
                type="button"
                variant="outline"
                size="xs"
                disabled={task.deleted}
                className={cn(task.deleted && "line-through")}
                title={task.deleted ? "This task is deleted" : `From “${task.quote}”`}
                onClick={() => onOpenTask(task.taskId)}
              >
                {task.title}
              </Button>
            ))}
          </div>
        )}

        {page && (
          <section aria-label="Linked from" className="grid gap-2 border-t pt-3">
            <h2 className="flex items-center gap-1.5 text-xs font-semibold tracking-wider text-muted-foreground uppercase">
              <Link2 className="size-3.5" />
              Linked from<span className="font-normal normal-case tracking-normal">({page.backlinks.length})</span>
            </h2>
            {page.backlinks.length === 0 ? (
              <p className="text-sm text-muted-foreground">
                No pages or tasks link here yet. Type <kbd className="font-mono">[[</kbd> in another page or a task's notes to link to this
                page.
              </p>
            ) : (
              <ul className="grid gap-1.5">
                {page.backlinks.map((link) => (
                  <li key={`${link.kind}-${link.id}`}>
                    <button
                      type="button"
                      className="grid w-full gap-0.5 rounded-lg border bg-card px-3 py-2 text-left text-sm hover:border-input"
                      onClick={() => (link.kind === "task" ? onOpenTask(link.id) : onOpen({ kind: "page", pageId: link.id }))}
                    >
                      <span className="flex items-center gap-1.5 font-medium">
                        {link.kind === "task" ? (
                          <ListPlus className="size-3.5 text-muted-foreground" />
                        ) : (
                          <FileText className="size-3.5 text-muted-foreground" />
                        )}
                        {link.title}
                        <span className="text-xs font-normal text-muted-foreground">{link.kind === "task" ? "task" : "page"}</span>
                      </span>
                      {link.line && <span className="truncate text-xs text-muted-foreground">{link.line}</span>}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>
        )}

        {showHistory && page && (
          <VersionHistory
            api={api}
            page={page}
            current={encodeDraft(isDaily ? page.title : title, markdown)}
            busy={busy}
            onRestore={async (number) => {
              const version = await settledVersion();
              if (version === null) return;
              await runPage(
                () => api.restoreVersion({ operationId: operationId(), pageId: page.id, expectedVersion: version, number }),
                "Could not restore that version.",
                () => {
                  registry.forget(page.id);
                  setShowHistory(false);
                  onNotice("Restored an earlier version. Undo brings back the text from before.");
                },
              );
              await reload();
            }}
            onClose={() => setShowHistory(false)}
          />
        )}
      </div>
    </article>
  );
}

function pathOf(row: PageSummaryResponse, byId: Map<string, PageSummaryResponse>) {
  const names = [row.title];
  for (
    let parent = row.parentPageId ? byId.get(row.parentPageId) : undefined;
    parent && names.length < 32;
    parent = parent.parentPageId ? byId.get(parent.parentPageId) : undefined
  )
    names.unshift(parent.title);
  return names.join(" › ");
}

function SaveLine({
  snapshot,
  unwritten,
  words,
  onRetry,
  onCopy,
}: {
  snapshot: DraftSnapshot | null;
  unwritten: boolean;
  words: number;
  onRetry: () => void;
  onCopy: () => void;
}) {
  const count = (
    <span className="tabular-nums">
      {words} {words === 1 ? "word" : "words"}
    </span>
  );
  if (snapshot?.status === "failed") {
    return (
      <div role="alert" className="flex flex-wrap items-center gap-2 text-xs text-destructive">
        <span>Not saved: {snapshot.error ?? "the page could not be saved."} Your text is kept here.</span>
        <Button type="button" size="xs" variant="outline" onClick={onRetry}>
          <RotateCcw />
          Retry
        </Button>
        <Button type="button" size="xs" variant="ghost" onClick={onCopy}>
          <Copy />
          Copy text
        </Button>
      </div>
    );
  }
  const label = unwritten
    ? "Not started: it is kept once you write"
    : snapshot?.status === "saving"
      ? "Saving…"
      : snapshot?.status === "pending"
        ? "Unsaved changes"
        : "Saved";
  return (
    <p role="status" aria-live="polite" className="flex gap-2 text-xs text-muted-foreground">
      <span>{label}</span>
      <span aria-hidden="true">·</span>
      {count}
    </p>
  );
}

type PanelProps = {
  frontMatter: ReturnType<typeof parseFrontMatter>;
  head: string;
  body: string;
  raw: boolean;
  setRaw: (value: boolean) => void;
  tagByPath: Map<string, TagResponse>;
  properties: ReturnType<typeof pageProperties>;
  backgroundNote: string | null;
  onSet: (key: string, value: string | string[] | null) => void;
  onRaw: (yaml: string) => void;
  onRemoveAll: () => void;
  onCreateTag: (path: string) => void;
};

/** Front matter as a small properties panel; the raw YAML is one switch away (and shown when it cannot be read). */
function PropertiesPanel({
  frontMatter,
  raw,
  setRaw,
  tagByPath,
  properties,
  backgroundNote,
  onSet,
  onRaw,
  onRemoveAll,
  onCreateTag,
}: PanelProps) {
  const [newKey, setNewKey] = React.useState("");
  const [newValue, setNewValue] = React.useState("");
  const [tagText, setTagText] = React.useState("");
  const [aliasText, setAliasText] = React.useState("");
  const showRaw = raw || frontMatter.error !== null;
  const others = frontMatter.properties.filter((property) => !(knownKeys as readonly string[]).includes(property.key));
  const sceneValue =
    properties.background && backgroundScenes.some((scene) => scene.id === properties.background)
      ? properties.background
      : properties.background && /^#/.test(properties.background)
        ? "color"
        : properties.background
          ? "other"
          : "";
  return (
    <section aria-label="Page properties" className="grid gap-3 rounded-lg border bg-muted/30 p-3 text-sm">
      <div className="flex items-center justify-between gap-2">
        <h2 className="text-[0.7rem] font-semibold tracking-wider text-muted-foreground uppercase">Properties</h2>
        <div className="flex items-center gap-1">
          <label className="flex items-center gap-1.5 text-xs text-muted-foreground">
            <input
              type="checkbox"
              className="accent-primary"
              checked={showRaw}
              disabled={frontMatter.error !== null}
              onChange={(event) => setRaw(event.target.checked)}
            />
            Edit as YAML
          </label>
          <Button
            type="button"
            variant="ghost"
            size="icon-xs"
            aria-label="Remove all properties"
            title="Remove the properties block"
            onClick={() => {
              if (frontMatter.properties.length === 0 || window.confirm("Remove every property from this page?")) onRemoveAll();
            }}
          >
            <X />
          </Button>
        </div>
      </div>
      {frontMatter.error && (
        <p role="alert" className="text-xs text-destructive">
          The properties cannot be read, so they are ignored until fixed. {frontMatter.error}
        </p>
      )}
      {showRaw ? (
        <>
          <label htmlFor="page-yaml" className="sr-only">
            Front matter YAML
          </label>
          <Textarea
            id="page-yaml"
            className="font-mono text-xs [field-sizing:content]"
            rows={3}
            spellCheck={false}
            value={frontMatter.yaml}
            onChange={(event) => onRaw(event.target.value)}
          />
        </>
      ) : (
        <div className="grid gap-2 sm:grid-cols-[6rem_minmax(0,1fr)] sm:items-center">
          <label htmlFor="page-icon" className="text-muted-foreground">
            Icon
          </label>
          <Input
            id="page-icon"
            className="h-8 w-24"
            placeholder="🙂"
            maxLength={16}
            value={properties.icon ?? ""}
            onChange={(event) => onSet("icon", event.target.value.trim() ? event.target.value.trim() : null)}
          />

          <label htmlFor="page-background" className="text-muted-foreground">
            Background
          </label>
          <div className="flex flex-wrap items-center gap-2">
            <NativeSelect
              id="page-background"
              value={sceneValue}
              onChange={(event) => {
                const value = event.target.value;
                onSet(
                  "background",
                  value === "" ? null : value === "color" ? backgroundColors[0].value : value === "other" ? properties.background : value,
                );
              }}
            >
              <NativeSelectOption value="">None</NativeSelectOption>
              {backgroundScenes.map((scene) => (
                <NativeSelectOption key={scene.id} value={scene.id}>
                  {scene.name}
                </NativeSelectOption>
              ))}
              <NativeSelectOption value="color">A colour</NativeSelectOption>
              {sceneValue === "other" && <NativeSelectOption value="other">{properties.background}</NativeSelectOption>}
            </NativeSelect>
            {sceneValue === "color" && (
              <div className="flex flex-wrap gap-1" role="group" aria-label="Background colour">
                {backgroundColors.map((color) => (
                  <button
                    key={color.value}
                    type="button"
                    className={cn(
                      "size-5 rounded-full border",
                      properties.background?.toLowerCase() === color.value && "ring-2 ring-ring ring-offset-1",
                    )}
                    style={{ backgroundColor: color.value }}
                    aria-label={color.name}
                    title={color.name}
                    onClick={() => onSet("background", color.value)}
                  />
                ))}
              </div>
            )}
            {backgroundNote && <span className="text-xs text-muted-foreground">{backgroundNote}</span>}
          </div>

          <span className="text-muted-foreground">Tags</span>
          <div className="flex flex-wrap items-center gap-1.5">
            {properties.tags.map((path) => {
              const known = tagByPath.get(path.toLowerCase());
              return (
                <span
                  key={path}
                  className={cn(
                    "inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs",
                    !known && "border-dashed text-muted-foreground",
                  )}
                  title={known ? undefined : "No tag with this name yet"}
                >
                  #{path}
                  {!known && (
                    <button type="button" className="text-primary hover:underline" onClick={() => onCreateTag(path)}>
                      create
                    </button>
                  )}
                  <button
                    type="button"
                    aria-label={`Remove tag ${path}`}
                    onClick={() =>
                      onSet(
                        "tags",
                        properties.tags.filter((item) => item !== path),
                      )
                    }
                  >
                    <X className="size-3" />
                  </button>
                </span>
              );
            })}
            <form
              className="flex items-center gap-1"
              onSubmit={(event) => {
                event.preventDefault();
                const value = tagText.trim().replace(/^#/, "");
                if (value && !properties.tags.includes(value)) onSet("tags", [...properties.tags, value]);
                setTagText("");
              }}
            >
              <label htmlFor="page-tag-add" className="sr-only">
                Add tag
              </label>
              <Input
                id="page-tag-add"
                list="page-tag-options"
                className="h-7 w-36 text-xs"
                placeholder="Add tag"
                value={tagText}
                onChange={(event) => setTagText(event.target.value)}
              />
              <datalist id="page-tag-options">
                {[...tagByPath.values()].map((tag) => (
                  <option key={tag.id} value={tag.path} />
                ))}
              </datalist>
            </form>
          </div>

          <span className="text-muted-foreground">Aliases</span>
          <div className="flex flex-wrap items-center gap-1.5">
            {properties.aliases.map((alias) => (
              <span key={alias} className="inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs">
                {alias}
                <button
                  type="button"
                  aria-label={`Remove alias ${alias}`}
                  onClick={() =>
                    onSet(
                      "aliases",
                      properties.aliases.filter((item) => item !== alias),
                    )
                  }
                >
                  <X className="size-3" />
                </button>
              </span>
            ))}
            <form
              className="flex items-center gap-1"
              onSubmit={(event) => {
                event.preventDefault();
                const value = aliasText.trim();
                if (value && !properties.aliases.includes(value)) onSet("aliases", [...properties.aliases, value]);
                setAliasText("");
              }}
            >
              <label htmlFor="page-alias-add" className="sr-only">
                Add alias
              </label>
              <Input
                id="page-alias-add"
                className="h-7 w-36 text-xs"
                placeholder="Other name for [[ links"
                value={aliasText}
                onChange={(event) => setAliasText(event.target.value)}
              />
            </form>
          </div>

          {others.map((property) => (
            <React.Fragment key={property.key}>
              <label htmlFor={`page-prop-${property.key}`} className="truncate text-muted-foreground" title={property.key}>
                {property.key}
              </label>
              <div className="flex items-center gap-1">
                <Input
                  id={`page-prop-${property.key}`}
                  className="h-8 font-mono text-xs"
                  value={property.list ? property.values.join(", ") : (property.values[0] ?? "")}
                  readOnly={property.list}
                  title={property.list ? "A list: edit it as YAML" : undefined}
                  onChange={(event) => onSet(property.key, event.target.value)}
                />
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-xs"
                  aria-label={`Remove ${property.key}`}
                  onClick={() => onSet(property.key, null)}
                >
                  <X />
                </Button>
              </div>
            </React.Fragment>
          ))}

          <form
            className="col-span-full flex flex-wrap items-center gap-1 pt-1"
            onSubmit={(event) => {
              event.preventDefault();
              const key = newKey.trim();
              if (!key || frontMatter.properties.some((property) => property.key === key) || !/^[\p{L}\p{N}_.-]+$/u.test(key)) return;
              onSet(key, newValue);
              setNewKey("");
              setNewValue("");
            }}
          >
            <label htmlFor="page-prop-key" className="sr-only">
              New property name
            </label>
            <Input
              id="page-prop-key"
              className="h-7 w-32 text-xs"
              placeholder="New property"
              value={newKey}
              onChange={(event) => setNewKey(event.target.value)}
            />
            <label htmlFor="page-prop-value" className="sr-only">
              New property value
            </label>
            <Input
              id="page-prop-value"
              className="h-7 w-48 text-xs"
              placeholder="Value"
              value={newValue}
              onChange={(event) => setNewValue(event.target.value)}
            />
            <Button type="submit" size="xs" variant="outline" disabled={!newKey.trim()}>
              <Plus />
              Add
            </Button>
          </form>
        </div>
      )}
    </section>
  );
}

/** Earlier versions of a page: pick one to compare with the current text, then restore it if wanted. */
function VersionHistory({
  api,
  page,
  current,
  busy,
  onRestore,
  onClose,
}: {
  api: PageApi;
  page: PageResponse;
  current: string;
  busy: boolean;
  onRestore: (number: string) => Promise<void>;
  onClose: () => void;
}) {
  const [versions, setVersions] = React.useState<PageRevisionResponse[] | null>(null);
  const [chosen, setChosen] = React.useState<string | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  React.useEffect(() => {
    let active = true;
    void api
      .revisions({ pageId: page.id })
      .then((result) => {
        if (!active) return;
        if (!result.ok) {
          setError(result.error?.message ?? "Could not load the versions.");
          return;
        }
        setVersions([...result.revisions]);
        setChosen(result.revisions[1]?.number ?? result.revisions[0]?.number ?? null);
      })
      .catch((e: unknown) => {
        if (active) setError(String(e));
      });
    return () => {
      active = false;
    };
  }, [page.id, page.version]);
  const version = versions?.find((item) => item.number === chosen) ?? null;
  const diff = React.useMemo(() => (version ? lineDiff(encodeDraft(version.title, version.markdown), current) : []), [version, current]);
  const stats = diffStats(diff);
  return (
    <section aria-label="Versions" className="grid gap-3 rounded-lg border p-3">
      <div className="flex items-center justify-between">
        <h2 className="flex items-center gap-1.5 text-sm font-semibold">
          <History className="size-4" />
          Versions
        </h2>
        <Button type="button" variant="ghost" size="icon-xs" aria-label="Close versions" onClick={onClose}>
          <X />
        </Button>
      </div>
      {error && (
        <p role="alert" className="text-sm text-destructive">
          {error}
        </p>
      )}
      {versions && versions.length === 0 && <p className="text-sm text-muted-foreground">No earlier versions yet.</p>}
      {versions && versions.length > 0 && (
        <div className="grid gap-3 md:grid-cols-[14rem_minmax(0,1fr)]">
          <ol className="grid max-h-80 content-start gap-1 overflow-y-auto" aria-label="Saved versions">
            {versions.map((item, index) => (
              <li key={item.number}>
                <button
                  type="button"
                  aria-pressed={item.number === chosen}
                  className={cn(
                    "grid w-full gap-0.5 rounded-md border px-2 py-1.5 text-left text-xs hover:border-input",
                    item.number === chosen && "border-primary bg-brand-soft",
                  )}
                  onClick={() => setChosen(item.number)}
                >
                  <span className="font-medium">
                    {new Date(item.updatedAtUtc).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" })}
                    {index === 0 && <span className="ml-1 text-muted-foreground">(latest)</span>}
                  </span>
                  <span className="text-muted-foreground">
                    {wordCount(item.markdown)} words · {item.title}
                  </span>
                </button>
              </li>
            ))}
          </ol>
          {version && (
            <div className="grid min-w-0 content-start gap-2">
              <div className="flex flex-wrap items-center gap-2 text-xs text-muted-foreground">
                <span>
                  Compared with the page now: <span className="text-emerald-700 dark:text-emerald-400">+{stats.added}</span>{" "}
                  <span className="text-destructive">−{stats.removed}</span> lines
                </span>
                <Button
                  type="button"
                  size="xs"
                  className="ml-auto"
                  disabled={busy || stats.added + stats.removed === 0}
                  onClick={() => void onRestore(version.number)}
                >
                  <RotateCcw />
                  Restore this version
                </Button>
              </div>
              <pre
                aria-label="Differences"
                className="max-h-96 overflow-auto rounded-md border bg-muted/30 p-2 font-mono text-xs leading-relaxed whitespace-pre-wrap [overflow-wrap:anywhere]"
              >
                {diff.map((line, index) => (
                  <div
                    key={index}
                    className={cn(
                      line.kind === "added" && "bg-emerald-500/15 text-emerald-900 dark:text-emerald-200",
                      line.kind === "removed" && "bg-destructive/10 text-destructive line-through decoration-destructive/40",
                    )}
                  >
                    <span aria-hidden="true" className="mr-2 select-none text-muted-foreground">
                      {line.kind === "added" ? "+" : line.kind === "removed" ? "−" : " "}
                    </span>
                    {line.text || " "}
                    <span className="sr-only">
                      {line.kind === "added" ? " (only in the page now)" : line.kind === "removed" ? " (only in this version)" : ""}
                    </span>
                  </div>
                ))}
              </pre>
              <p className="text-xs text-muted-foreground">
                Green lines are only in the page now; struck lines are only in the chosen version. Restoring keeps the current text as a
                version too.
              </p>
            </div>
          )}
        </div>
      )}
    </section>
  );
}
