import { test } from "node:test";
import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import { JSDOM } from "jsdom";
import React from "react";
import { containerRpc, type ContainerResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  crypto: { value: webcrypto, configurable: true },
});

const { render, screen, waitFor, cleanup, within, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { ContainerManagement } = await import("./ContainerManagement");

test("deletion sends the selected contents choice and a conflict stays visible", async () => {
  const container: ContainerResponse = { id: "project", name: "Project", kind: "List", version: "3", archived: false, deadlineDate: null };
  let choice = "";
  let deletedCallback = false;
  const api: typeof containerRpc = {
    ...containerRpc,
    deletedSections: async () => ({ ok: true, sections: [], error: null }),
    delete: async request => { choice = request.contentsChoice; return { ok: false, container: null, error: { kind: "versionConflict", message: "The list changed.", fields: [], correlationId: "test" } }; },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<ContainerManagement container={container} containers={[container]} sections={[]} taskRevision="" api={api} onChanged={async () => {}} onDeleted={async () => { deletedCallback = true; }}/>, { container: dom.window.document.body });
  await user.selectOptions(screen.getByLabelText("When deleting this list"), "DeleteContents");
  await user.click(screen.getByRole("button", { name: "Delete list or project" }));
  await waitFor(() => assert.equal(screen.getByRole("alert").textContent, "The list changed."));
  assert.equal(choice, "DeleteContents");
  assert.equal(deletedCallback, false);
  cleanup();
});

test("dragging a section above another sends the dragged section's reorder", async () => {
  const container: ContainerResponse = { id: "project", name: "Project", kind: "Project", version: "1", archived: false, deadlineDate: null };
  const sections = [{ id: "s1", title: "Plan", version: "4" }, { id: "s2", title: "Build", version: "7" }];
  const calls: Parameters<typeof containerRpc.reorderSection>[0][] = [];
  let changed = false;
  const api: typeof containerRpc = {
    ...containerRpc,
    progress: async () => ({ ok: true, doneCount: 0, totalCount: 0, doneSubtaskCount: 0, totalSubtaskCount: 0, error: null }),
    deletedSections: async () => ({ ok: true, sections: [], error: null }),
    reorderSection: async request => { calls.push(request); return { ok: true, section: sections[1], error: null }; },
  };
  render(<ContainerManagement container={container} containers={[container]} sections={sections} taskRevision="" api={api} onChanged={async () => { changed = true; }} onDeleted={async () => {}}/>, { container: dom.window.document.body });
  const rows = within(screen.getByRole("list", { name: "Sections" })).getAllByRole("listitem");
  const dataTransfer = { setData() {}, effectAllowed: "", dropEffect: "" };
  fireEvent.dragStart(within(rows[1]).getByTitle("Drag to reorder"), { dataTransfer });
  fireEvent.dragOver(rows[0], { dataTransfer, clientY: 0 });
  fireEvent.drop(rows[0], { dataTransfer, clientY: 0 });
  await waitFor(() => assert.equal(changed, true));
  assert.deepEqual(calls.map(r => ({ sectionId: r.sectionId, expectedVersion: r.expectedVersion, title: r.title, insertionIndex: r.insertionIndex })), [{ sectionId: "s2", expectedVersion: "7", title: "Build", insertionIndex: 0 }]);
  cleanup();
});
