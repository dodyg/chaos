import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { containerRpc, shellRpc, taskRpc, type ProfileSettingsResponse, type ProfileSettingsUpdateRequest, type ShellStatusResponse, type TaskResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
});
const { render, screen, waitFor, cleanup, within } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { Settings } = await import("./Settings");
const { App } = await import("./App");

const saved: ProfileSettingsResponse = {
  ok: true, displayName: "Me", zoneId: "Africa/Cairo", dateFormat: "DMY", captureShortcut: "Ctrl+Alt+N", padShortcut: "Ctrl+Alt+P",
  keepRunningInTray: true, version: "3", zoneChanged: false, error: null,
};
const status: ShellStatusResponse = {
  ok: true, appVersion: "0.1.0", packaged: false, trayAvailable: true, keepRunningInTray: true,
  shortcuts: [
    { id: "capture", accelerator: "Ctrl+Alt+N", state: "Conflict", message: "Ctrl+Alt+N is already used by another app. Choose another shortcut." },
    { id: "pad", accelerator: "Ctrl+Alt+P", state: "Registered", message: "Ctrl+Alt+P works from any app." },
  ],
};

function shellApi(patch: Partial<typeof shellRpc> = {}) {
  return { ...shellRpc, settings: async () => saved, zones: async () => ({ ok: true, zones: ["Africa/Cairo", "Asia/Jakarta", "UTC"] }), status: async () => status, take: async () => ({ ok: true, activations: [] }), ...patch } as typeof shellRpc;
}

test("settings show a shortcut conflict and save the changed shortcut with the loaded version", async () => {
  const updates: ProfileSettingsUpdateRequest[] = [];
  let savedZoneChange: boolean | null = null;
  const api = shellApi({
    updateSettings: async (request: ProfileSettingsUpdateRequest) => {
      updates.push(request);
      return { ...saved, captureShortcut: "Ctrl+Alt+K", zoneId: request.zoneId, version: "4", zoneChanged: true };
    },
  });
  const user = userEvent.setup({ document: dom.window.document });
  render(<Settings api={api} onSaved={changed => { savedZoneChange = changed; }} onNavigate={() => {}}/>, { container: dom.window.document.body });

  await user.click(await screen.findByRole("tab", { name: "Shortcuts" }));
  assert.match((await screen.findByRole("alert")).textContent ?? "", /already used by another app/);
  const save = screen.getByRole("button", { name: "Save settings" }) as HTMLButtonElement;
  assert.equal(save.disabled, true);

  const capture = screen.getByLabelText("Quick add");
  await user.clear(capture);
  await user.type(capture, "Ctrl+Alt+K");
  await user.click(screen.getByRole("tab", { name: "General" }));
  await user.selectOptions(screen.getByLabelText("Time zone"), "Asia/Jakarta");
  await user.click(screen.getByRole("button", { name: "Save settings" }));

  await waitFor(() => assert.equal(updates.length, 1));
  assert.equal(updates[0].expectedVersion, "3");
  assert.equal(updates[0].captureShortcut, "Ctrl+Alt+K");
  assert.equal(updates[0].zoneId, "Asia/Jakarta");
  assert.equal(updates[0].padShortcut, "Ctrl+Alt+P");
  await screen.findByText(/Reminders were rescheduled/);
  assert.equal(savedZoneChange, true);
  cleanup();
});

test("a refused shortcut shows its message next to the field and an empty shortcut is sent as off", async () => {
  const updates: ProfileSettingsUpdateRequest[] = [];
  const api = shellApi({
    updateSettings: async (request: ProfileSettingsUpdateRequest) => {
      updates.push(request);
      return { ...saved, ok: false, error: { kind: "validation", message: "A shortcut needs Ctrl or Alt.", fields: [{ fieldPath: "captureShortcut", message: "A shortcut needs Ctrl or Alt." }], correlationId: "x" } };
    },
  });
  const user = userEvent.setup({ document: dom.window.document });
  render(<Settings api={api} onSaved={() => {}} onNavigate={() => {}}/>, { container: dom.window.document.body });

  await user.click(await screen.findByRole("tab", { name: "Shortcuts" }));
  await screen.findByRole("alert");
  await user.clear(screen.getByLabelText("New pad note"));
  await user.clear(screen.getByLabelText("Quick add"));
  await user.type(screen.getByLabelText("Quick add"), "Shift+K");
  await user.click(screen.getByRole("button", { name: "Save settings" }));

  await waitFor(() => assert.equal(updates.length, 1));
  assert.equal(updates[0].padShortcut, null);
  assert.equal(screen.getAllByText("A shortcut needs Ctrl or Alt.").length >= 1, true);
  assert.equal(screen.getByLabelText("Quick add").getAttribute("aria-invalid"), "true");
  cleanup();
});

test("the sidebar quit button asks the host to end Chaos", async () => {
  let quits = 0;
  const api = shellApi({ quit: async () => { quits++; return { ok: true }; } });
  const user = userEvent.setup({ document: dom.window.document });
  render(<App initialView="settings" api={{ taskRpc, containerRpc, shellRpc: api }} templates={<></>}/>, { container: dom.window.document.body });
  await user.click(await screen.findByRole("button", { name: /Quit Chaos/ }));
  assert.equal(quits, 0, "the first click only asks");
  await user.click(screen.getByRole("button", { name: "Confirm quit" }));

  assert.equal(quits, 1);
  cleanup();
});

test("an activation from a notification opens its task and shows what happened", async () => {
  const task: TaskResponse = { id: "t1", title: "Pay rent", spaceId: "space", containerId: "inbox", notes: "", status: "NotStarted", plannedDate: null, dueDate: null, version: "1", createdAtUtc: "2026-01-01T00:00:00Z", updatedAtUtc: "2026-01-01T00:00:00Z", priority: "Normal", estimateMinutes: null, category: "Personal", plannedTime: null, plannedZoneId: null, dueTime: null, dueZoneId: null, sectionId: null };
  let taken = 0;
  const api = {
    taskRpc: { ...taskRpc, listInbox: async () => ({ ok: true, tasks: [], error: null }), listDeleted: async () => ({ ok: true, tasks: [], error: null }), get: async () => ({ ok: true, task, error: null }) },
    containerRpc: { ...containerRpc, list: async () => ({ ok: true, containers: [], error: null }), listDeleted: async () => ({ ok: true, containers: [], error: null }) },
    shellRpc: shellApi({ take: async () => (taken++ === 0
      ? { ok: true, activations: [{ sequence: "1", kind: "OpenTask", taskId: "t1", message: "This task has open subtasks." }] }
      : { ok: true, activations: [] }) }),
  };
  render(<App initialView="tasks" api={api} templates={<></>}/>, { container: dom.window.document.body });

  const editor = await screen.findByRole("complementary", { name: "Edit task" });
  assert.equal((within(editor).getByLabelText("Title") as HTMLInputElement).value, "Pay rent");
  assert.ok(screen.getByText("This task has open subtasks."));
  cleanup();
});
