import React from "react";
import { tagRpc, type TagResponse } from "#neoastra";
import { Archive, ArchiveRestore, ChevronRight, Pin, PinOff, Settings2 } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { InlineError } from "@/components/common";
import { TagDot } from "./tags";
import { listTags, tagForest, visibleRows, type TagSort } from "./tagList";

type TagsApi = Pick<typeof tagRpc, "setPinned" | "setArchived">;

const operationId = () => crypto.randomUUID();

/**
 * Every tag in one place (the sidebar shows only pinned and busy ones): filter, sort, pin to the
 * sidebar, archive out of it, and open a tag's settings to rename, merge or delete it. Pinned and
 * archived travel with the tag, so every device agrees.
 */
export function TagsPage({
  tags,
  counts,
  api = tagRpc,
  onOpen,
  onManage,
  onChanged,
}: {
  tags: readonly TagResponse[];
  counts: ReadonlyMap<string, number>;
  api?: TagsApi;
  onOpen: (tag: TagResponse) => void;
  onManage: (tag: TagResponse) => void;
  onChanged: () => Promise<void>;
}) {
  const [filter, setFilter] = React.useState("");
  const [sort, setSort] = React.useState<TagSort>("name");
  const [showArchived, setShowArchived] = React.useState(false);
  const [busy, setBusy] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);
  const archivedCount = tags.filter((tag) => tag.archived).length;
  const [collapsed, setCollapsed] = React.useState<ReadonlySet<string>>(new Set());
  // Browsing shows the nesting; searching and the archive list are flat, each tag under its full path.
  const nested = !filter.trim() && !showArchived;
  const rows = nested
    ? visibleRows(
        tagForest(
          tags.filter((tag) => !tag.archived),
          counts,
          sort,
        ),
        collapsed,
      )
    : listTags(tags, counts, { filter, sort, showArchived }).map((tag) => ({ tag, depth: 0, hasChildren: false }));
  function toggle(id: string) {
    setCollapsed((current) => {
      const next = new Set(current);
      if (!next.delete(id)) next.add(id);
      return next;
    });
  }

  async function change(action: () => ReturnType<TagsApi["setPinned"]>, failure: string) {
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const result = await action();
      if (!result.ok) throw new Error(result.error?.message ?? failure);
      await onChanged();
    } catch (e) {
      setError(e instanceof Error ? e.message : failure);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section aria-label="All tags" className="mx-auto grid w-full max-w-3xl content-start gap-4 px-[clamp(1rem,4vw,3rem)] py-8">
      <header className="grid gap-1">
        <h2 className="text-xl font-semibold">Tags</h2>
        <p className="text-sm text-muted-foreground">
          The sidebar shows your pinned tags and the busiest few. Pin the ones you use every day; archive the ones you no longer need. An
          archived tag keeps working on tasks.
        </p>
      </header>
      <div className="flex flex-wrap items-center gap-2">
        <label className="sr-only" htmlFor="tag-filter">
          Filter tags
        </label>
        <Input
          id="tag-filter"
          className="min-w-48 flex-1"
          placeholder="Filter tags"
          autoComplete="off"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
        />
        <label className="sr-only" htmlFor="tag-sort">
          Sort tags
        </label>
        <NativeSelect id="tag-sort" value={sort} onChange={(e) => setSort(e.target.value as TagSort)}>
          <NativeSelectOption value="name">Sort by name</NativeSelectOption>
          <NativeSelectOption value="usage">Sort by open tasks</NativeSelectOption>
        </NativeSelect>
        <Button
          type="button"
          variant={showArchived ? "secondary" : "outline"}
          aria-pressed={showArchived}
          onClick={() => setShowArchived((current) => !current)}
        >
          <Archive />
          {showArchived ? "Showing archived" : `Archived (${archivedCount})`}
        </Button>
      </div>
      {error && <InlineError>{error}</InlineError>}
      {rows.length === 0 ? (
        <p className="py-6 text-center text-muted-foreground">
          {filter.trim() ? "No tag matches." : showArchived ? "No archived tags." : "No tags yet."}
        </p>
      ) : (
        <ul className="grid gap-px rounded-xl border bg-card p-1" aria-label={showArchived ? "Archived tags" : "Tags"}>
          {rows.map(({ tag, depth, hasChildren }) => {
            const count = counts.get(tag.id) ?? 0;
            return (
              <li
                key={tag.id}
                className="group/row flex items-center gap-1 rounded-md hover:bg-accent/50"
                style={{ paddingLeft: `${depth * 1.25}rem` }}
              >
                {nested &&
                  (hasChildren ? (
                    <button
                      type="button"
                      className="inline-flex size-6 shrink-0 items-center justify-center rounded text-muted-foreground outline-none hover:text-foreground focus-visible:ring-[3px] focus-visible:ring-ring/50"
                      aria-expanded={!collapsed.has(tag.id)}
                      aria-label={`${collapsed.has(tag.id) ? "Expand" : "Collapse"} ${tag.path}`}
                      onClick={() => toggle(tag.id)}
                    >
                      <ChevronRight
                        className={cn("size-4 transition-transform", !collapsed.has(tag.id) && "rotate-90")}
                        aria-hidden="true"
                      />
                    </button>
                  ) : (
                    <span className="inline-block size-6 shrink-0" aria-hidden="true" />
                  ))}
                <button
                  type="button"
                  className="flex min-w-0 flex-1 items-center gap-2 rounded-md px-2 py-1.5 text-left outline-none focus-visible:ring-[3px] focus-visible:ring-ring/50"
                  onClick={() => onOpen(tag)}
                >
                  <TagDot color={tag.color} />
                  <span className={cn("truncate", tag.archived && "text-muted-foreground")}>{nested ? tag.name : tag.path}</span>
                  <span className={cn("ml-auto text-xs tabular-nums", count > 0 ? "text-muted-foreground" : "text-muted-foreground/50")}>
                    {count > 0 ? `${count} open` : "no open tasks"}
                  </span>
                </button>
                {!tag.archived && (
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon-sm"
                    disabled={busy}
                    aria-pressed={tag.pinned}
                    aria-label={tag.pinned ? `Unpin ${tag.path}` : `Pin ${tag.path} to the sidebar`}
                    title={tag.pinned ? "Unpin from the sidebar" : "Pin to the sidebar"}
                    className={tag.pinned ? "text-primary" : "text-muted-foreground"}
                    onClick={() =>
                      void change(
                        () =>
                          api.setPinned({ operationId: operationId(), tagId: tag.id, expectedVersion: tag.version, pinned: !tag.pinned }),
                        "Could not pin the tag.",
                      )
                    }
                  >
                    {tag.pinned ? <PinOff /> : <Pin />}
                  </Button>
                )}
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-sm"
                  disabled={busy}
                  aria-label={tag.archived ? `Restore ${tag.path}` : `Archive ${tag.path}`}
                  title={tag.archived ? "Bring back to the sidebar's tags" : "Archive: hide from the sidebar"}
                  className="text-muted-foreground"
                  onClick={() =>
                    void change(
                      () =>
                        api.setArchived({
                          operationId: operationId(),
                          tagId: tag.id,
                          expectedVersion: tag.version,
                          archived: !tag.archived,
                        }),
                      "Could not archive the tag.",
                    )
                  }
                >
                  {tag.archived ? <ArchiveRestore /> : <Archive />}
                </Button>
                <Button
                  type="button"
                  variant="ghost"
                  size="icon-sm"
                  aria-label={`Manage ${tag.path}`}
                  title="Rename, recolor, merge or delete"
                  className="text-muted-foreground"
                  onClick={() => onManage(tag)}
                >
                  <Settings2 />
                </Button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
