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

const { render, screen, waitFor, cleanup } = await import("@testing-library/react");
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
