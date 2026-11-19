import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import {
  pageRpc,
  summaryRpc,
  type PageCreateDailyRequest,
  type PageResponse,
  type PageSaveRequest,
  type PageSummaryResponse,
} from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  localStorage: { value: dom.window.localStorage, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
});
const { render, screen, waitFor, cleanup, fireEvent } = await import("@testing-library/react");
const { Pages, decodeDraft, encodeDraft, selectionSubtasks, pageBackgroundStyle } = await import("./Pages");
const { MarkdownPreview } = await import("./markdown");
const { DraftRegistry } = await import("./padAutosave");

const kitchenId = "00000000-0000-4000-8000-000000000001";
const quotesId = "00000000-0000-4000-8000-000000000002";

function page(id: string, title: string, markdown: string, patch: Partial<PageResponse> = {}): PageResponse {
  return {
    id,
    parentPageId: null,
    title,
    markdown,
    version: "1",
    draftVersion: "0",
    dailyDate: null,
    deleted: false,
    createdAtUtc: "2026-11-14T08:00:00Z",
    updatedAtUtc: "2026-11-14T08:00:00Z",
    icon: null,
    tagIds: [],
    tasks: [],
    backlinks: [],
    ...patch,
  };
}
function summary(row: PageResponse): PageSummaryResponse {
  return {
    id: row.id,
    parentPageId: row.parentPageId,
    title: row.title,
    position: "0",
    dailyDate: row.dailyDate,
    icon: row.icon,
    aliases: [],
    version: row.version,
    updatedAtUtc: row.updatedAtUtc,
    deleted: row.deleted,
    wordCount: 0,
  };
}

function pageApi(rows: PageResponse[]) {
  const saves: PageSaveRequest[] = [];
  const dailyCreates: PageCreateDailyRequest[] = [];
  const api = {
    ...pageRpc,
    list: async () => ({ ok: true, pages: rows.filter((row) => !row.dailyDate && !row.deleted).map(summary), error: null }),
    listDaily: async () => ({ ok: true, pages: rows.filter((row) => row.dailyDate).map(summary), error: null }),
    listDeleted: async () => ({ ok: true, pages: [], error: null }),
    get: async ({ pageId }: { pageId: string }) => ({ ok: true, page: rows.find((row) => row.id === pageId) ?? null, error: null }),
    getDaily: async ({ date }: { date: string }) => ({ ok: true, page: rows.find((row) => row.dailyDate === date) ?? null, error: null }),
    save: async (request: PageSaveRequest) => {
      saves.push(request);
      const index = rows.findIndex((row) => row.id === request.pageId);
      rows[index] = {
        ...rows[index],
        title: request.title,
        markdown: request.markdown,
        draftVersion: request.clientRevision,
        version: String(Number(rows[index].version) + 1),
      };
      return { ok: true, page: rows[index], applied: true, error: null };
    },
    createDaily: async (request: PageCreateDailyRequest) => {
      dailyCreates.push(request);
      const created = page("00000000-0000-4000-8000-000000000099", request.date, request.markdown, { dailyDate: request.date });
      rows.push(created);
      return { ok: true, pages: [created], error: null };
    },
  } as unknown as typeof pageRpc;
  return { api, saves, dailyCreates };
}

const summaryApi = { ...summaryRpc, find: async () => ({ ok: true, summary: null, error: null }) } as unknown as typeof summaryRpc;

function renderPages(
  api: typeof pageRpc,
  focus: React.ComponentProps<typeof Pages>["focus"] = null,
  extra: Partial<React.ComponentProps<typeof Pages>> = {},
) {
  return render(
    <Pages
      {...extra}
      api={api}
      summaryApi={summaryApi}
      registry={new DraftRegistry()}
      containers={[]}
      tags={[]}
      revision={0}
      today="2026-11-14"
      focus={focus}
      links={{}}
      onChanged={() => {}}
      onOpenTask={() => {}}
      onOpenSummary={() => {}}
      onCreateTag={async () => true}
      onError={() => {}}
    />,
    { container: dom.window.document.body },
  );
}

test("a page's title and text travel through one autosave, and helpers read selections and backgrounds", () => {
  assert.deepEqual(decodeDraft(encodeDraft("Kitchen", "line 1\nline 2")), { title: "Kitchen", markdown: "line 1\nline 2" });
  assert.deepEqual(selectionSubtasks("- Order tiles\n  - choose colour\n  - [x] measure\n- Lunch"), [
    { title: "choose colour", done: false },
    { title: "measure", done: true },
  ]);
  assert.ok(pageBackgroundStyle("lighthouse").style?.backgroundImage);
  assert.equal(pageBackgroundStyle("#2f6f4e").style?.backgroundColor, "#2f6f4e");
  assert.match(pageBackgroundStyle("attachments/plan.jpg", { url: null }).note ?? "", /No image named/);
  assert.equal(pageBackgroundStyle("attachments/plan.jpg").note, undefined); // still loading: no note yet
  assert.equal(pageBackgroundStyle("attachments/plan.jpg", { url: "blob:x" }).style?.backgroundImage, 'url("blob:x")');
  assert.match(pageBackgroundStyle("https://example.com/x.png").note ?? "", /web address/i);
});

test("the tree nests pages; front matter shows as properties and the body alone is in the editor", async () => {
  const rows = [
    page(kitchenId, "Kitchen renovation", "---\nicon: 🛠️\nbackground: '#2f6f4e'\nfoo: bar\n---\n# Plan\nTiles", { icon: "🛠️" }),
    page(quotesId, "Quotes", "Three quotes", { parentPageId: kitchenId }),
  ];
  const { api, saves } = pageApi(rows);
  renderPages(api, { key: 1, kind: "page", pageId: kitchenId });

  assert.ok(await screen.findByText("Tiles"));
  await waitFor(() =>
    assert.equal(screen.getByRole("navigation", { name: "Pages" }).parentElement?.style.backgroundColor, "rgb(47, 111, 78)"),
  );
  assert.equal(screen.queryByLabelText("Page"), null);
  assert.equal(screen.queryByLabelText("Page title"), null);
  fireEvent.click(screen.getByRole("button", { name: "Edit" }));
  const editor = (await screen.findByLabelText("Page")) as HTMLTextAreaElement;
  assert.equal(editor.value, "# Plan\nTiles");
  assert.equal((screen.getByLabelText("Icon") as HTMLInputElement).value, "🛠️");
  assert.equal((screen.getByLabelText("foo") as HTMLInputElement).value, "bar");
  assert.ok(screen.getByRole("treeitem", { name: /Kitchen renovation/ }));
  assert.ok(screen.getByRole("navigation", { name: "Contents" }));
  assert.equal(screen.queryByRole("button", { name: /Today's page/ }), null, "Pages lists only pages; daily pages have their own view");

  fireEvent.change(editor, { target: { value: "# Plan\nTiles and a sink" } });
  fireEvent.change(screen.getByLabelText("Page title"), { target: { value: "Kitchen" } });
  fireEvent.blur(screen.getByLabelText("Page title"));
  await waitFor(() =>
    assert.equal(saves.at(-1)?.markdown, "---\nicon: 🛠️\nbackground: '#2f6f4e'\nfoo: bar\n---\n# Plan\nTiles and a sink"),
  );
  assert.equal(saves.at(-1)?.title, "Kitchen");
  fireEvent.click(screen.getByRole("button", { name: "Done editing" }));
  assert.equal(screen.queryByLabelText("Page"), null);
  assert.equal(screen.queryByLabelText("Page title"), null);
  assert.equal(screen.queryByLabelText("Icon"), null);
  assert.ok(screen.getByText("Tiles and a sink"));

  // Typing --- on the first line of a page opens the properties panel.
  cleanup();
  renderPages(api, { key: 2, kind: "page", pageId: quotesId });
  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  const quotes = (await screen.findByLabelText("Page")) as HTMLTextAreaElement;
  assert.equal(screen.getByRole("navigation", { name: "Pages" }).parentElement?.style.backgroundColor, "");
  fireEvent.change(quotes, { target: { value: "---\nThree quotes" } });
  await waitFor(() => assert.ok(screen.getByRole("region", { name: "Page properties" })));
  assert.equal((screen.getByLabelText("Page") as HTMLTextAreaElement).value, "Three quotes");
  cleanup();
});

test("today's page is stored only once something is written on it", async () => {
  const rows: PageResponse[] = [];
  const { api, dailyCreates, saves } = pageApi(rows);
  const routed: unknown[] = [];
  renderPages(api, { key: 1, kind: "daily", date: "2026-11-14" }, { mode: "daily", onRoute: (request) => routed.push(request) });
  fireEvent.click(await screen.findByRole("button", { name: "Edit" }));
  const editor = (await screen.findByLabelText("Daily page")) as HTMLTextAreaElement;
  assert.equal(screen.queryByRole("tree", { name: "Pages" }), null, "the daily view does not show the page tree");
  assert.match(screen.getByRole("status").textContent ?? "", /kept once you write/);
  assert.equal(dailyCreates.length, 0);

  fireEvent.change(editor, { target: { value: "G" } });
  await waitFor(() => assert.equal(dailyCreates.length, 1));
  assert.equal(dailyCreates[0].date, "2026-11-14");
  fireEvent.change(await screen.findByLabelText("Daily page"), { target: { value: "Good day" } });
  fireEvent.blur(screen.getByLabelText("Daily page"));
  await waitFor(() => assert.equal(saves.at(-1)?.markdown, "Good day"));
  assert.equal(dailyCreates.length, 1, "later typing saves the stored page");
  assert.equal(routed.length, 0);
  cleanup();

  // Pages asked to show a daily page hands it to the daily view instead.
  const sent: unknown[] = [];
  renderPages(api, { key: 1, kind: "daily", date: "2026-11-14" }, { onRoute: (request) => sent.push(request) });
  await waitFor(() => assert.deepEqual(sent, [{ kind: "daily", date: "2026-11-14" }]));
  cleanup();
});

test("read mode renders page links, [[names]] and folds headings", async () => {
  const opened: string[] = [];
  const created: string[] = [];
  render(
    <MarkdownPreview
      source={"# Plan\nSee [[Plumbers]] and [[Nobody]] and [Quotes](chaos:page/" + quotesId + ").\n## Detail\ntext"}
      links={{
        outline: true,
        resolvePages: async () => [{ id: quotesId, title: "Quotes (renamed)", icon: "💬", deleted: false, dailyDate: null }],
        resolvePageNames: async () => [{ id: kitchenId, title: "Plumbers", icon: null, deleted: false, dailyDate: null, name: "Plumbers" }],
        onOpenPage: (id) => opened.push(id),
        onOpenPageName: (name) => created.push(name),
      }}
    />,
    { container: dom.window.document.body },
  );

  fireEvent.click(await screen.findByRole("button", { name: "💬 Quotes (renamed)" }));
  assert.deepEqual(opened, [quotesId]);
  await waitFor(() => assert.ok(screen.getByTitle(/No page named “Nobody” yet/)));
  fireEvent.click(screen.getByTitle(/No page named “Nobody” yet/));
  assert.deepEqual(created, ["Nobody"]);
  fireEvent.click(screen.getByRole("button", { name: "Plumbers" }));
  assert.deepEqual(opened, [quotesId, kitchenId]);

  assert.ok(screen.getByText("text"));
  fireEvent.click(screen.getAllByRole("button", { name: "Fold section" })[0]);
  assert.equal(screen.queryByText("text"), null);
  cleanup();
});
