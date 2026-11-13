import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { dataRpc, type BackupDto, type DataStatusResponse, type ImportCommitRequest, type ImportPreviewRequest, type ImportPreviewResponse, type ResetRequest, type BackupRestoreRequest } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
});
const { render, screen, waitFor, cleanup, fireEvent } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { DataSettings, fileToBase64, countsText } = await import("./DataSettings");

const counts = { tasks: 3, openTasks: 2, lists: 2, subtasks: 1, diaryEntries: 1, padItems: 0, tags: 1 };
const backup: BackupDto = { fileName: "chaos-1.db", createdAtUtc: "2026-11-10T09:00:00Z", reason: "Scheduled", appVersion: "0.1.0", schemaVersion: 12, sizeBytes: 40960, described: true, problem: null, counts };
const status: DataStatusResponse = {
  ok: true, dataDirectory: "C:\\data", backupsDirectory: "C:\\backups", defaultExportDirectory: "C:\\Users\\me\\Documents\\Chaos exports",
  latestBackup: backup, lastBackupError: null, lastBackupErrorAtUtc: null, recoveryNotice: null, backups: [backup], imports: [], dailyKept: 7, weeklyKept: 4, error: null,
};

function preview(patch: Partial<ImportPreviewResponse> = {}): ImportPreviewResponse {
  return {
    ok: true, token: "t1", canCommit: true, newTasks: 2, duplicates: 1, conflicts: 0, subtasks: 0, diaryEntries: 0,
    lists: [{ sourceName: "Admin", taskCount: 2, existingContainerId: null, existingName: null }],
    items: [{ line: 2, title: "Renew passport", listName: "Admin", status: "NotStarted", subtasks: 0, outcome: "New", detail: null }],
    errors: [], warnings: [], csvHeaders: ["Task", "Where"], csvMapping: [{ field: "title", column: 0 }], csvFields: ["title", "list"],
    archive: null, archiveCounts: null, error: null, ...patch,
  };
}

test("counts read in plain words and files become base64", async () => {
  assert.equal(countsText(counts), "3 tasks (2 open) · 2 lists · 1 diary entry · 0 pad notes");
  assert.equal(await fileToBase64(new Blob(["hi"])), "aGk=");
});

test("a backup is restored only after reviewing it and ticking the confirmation, with the reviewed checksum", async () => {
  const restored: BackupRestoreRequest[] = [];
  let replaced = 0;
  const api = {
    ...dataRpc,
    status: async () => status,
    inspectBackup: async () => ({ ok: true, canRestore: true, problem: null, checksum: "abc", createdAtUtc: backup.createdAtUtc, reason: "Scheduled", schemaVersion: 12, counts, current: { ...counts, tasks: 5 }, error: null }),
    restoreBackup: async (request: BackupRestoreRequest) => { restored.push(request); return { ok: true, restoredFrom: request.fileName, safetyBackupFileName: "chaos-safety.db", counts, error: null }; },
  } as unknown as typeof dataRpc;
  render(<DataSettings api={api} containers={[]} onImported={() => {}} onReplaced={() => { replaced++; }}/>);

  await screen.findByText(/Latest backup:/);
  await userEvent.click(screen.getByText(/All backups/));
  await userEvent.click(screen.getByRole("button", { name: /Review restore/ }));
  const restoreButton = await screen.findByRole("button", { name: "Restore this backup" });
  assert.equal((restoreButton as HTMLButtonElement).disabled, true);
  assert.ok(screen.getByText(/Now: 5 tasks/));

  await userEvent.click(screen.getByRole("checkbox", { name: /Replace everything in Chaos with this backup/ }));
  await userEvent.click(restoreButton);

  await waitFor(() => assert.equal(restored.length, 1));
  assert.deepEqual(restored[0], { fileName: "chaos-1.db", checksum: "abc" });
  assert.equal(replaced, 1);
  await screen.findByText(/chaos-safety\.db/);
  cleanup();
});

test("a CSV import previews, lets the user map a column and choose a list, then commits with the latest token", async () => {
  const previews: ImportPreviewRequest[] = [];
  const commits: ImportCommitRequest[] = [];
  let imported = 0;
  const api = {
    ...dataRpc,
    status: async () => status,
    importPreview: async (request: ImportPreviewRequest) => {
      previews.push(request);
      return request.mapping ? preview({ token: "t2", csvMapping: [...request.mapping] }) : preview();
    },
    importCommit: async (request: ImportCommitRequest) => { commits.push(request); return { ok: true, batchId: "b", created: 2, duplicates: 1, conflicts: 0, createdLists: [], backupFileName: "chaos-pre.db", restore: null, error: null }; },
  } as unknown as typeof dataRpc;
  const containers = [{ id: "c1", name: "Paperwork", kind: "List", archived: false, deadlineDate: null, defaultCategory: null, version: "1" }];
  render(<DataSettings api={api} containers={containers} onImported={() => { imported++; }} onReplaced={() => {}}/>);
  await screen.findByText(/Latest backup:/);

  const file = new dom.window.File(["Task,Where\nRenew passport,Admin\n"], "todo.csv", { type: "text/csv" });
  fireEvent.change(screen.getByLabelText("File"), { target: { files: [file] } });
  await screen.findByRole("region", { name: "Import preview" });
  assert.equal(previews[0].format, "csv");
  assert.equal(previews[0].mapping, null);

  await userEvent.selectOptions(screen.getByLabelText("List"), "1");
  await waitFor(() => assert.equal(previews.length, 2));
  assert.deepEqual(previews[1].mapping, [{ field: "title", column: 0 }, { field: "list", column: 1 }]);

  await userEvent.selectOptions(screen.getByLabelText(/“Admin”/), "c1");
  await userEvent.click(screen.getByRole("button", { name: "Import 2 tasks" }));

  await waitFor(() => assert.equal(commits.length, 1));
  assert.equal(commits[0].token, "t2");
  assert.deepEqual(commits[0].listTargets, [{ sourceName: "Admin", containerId: "c1" }]);
  assert.equal(imported, 1);
  await screen.findByText(/Imported 2 tasks, skipped 1 already here/);
  cleanup();
});

test("an import with problems cannot be committed and lists them by line", async () => {
  const api = {
    ...dataRpc,
    status: async () => status,
    importPreview: async () => preview({ canCommit: false, errors: [{ line: 4, field: "due", message: "\"next tuesday\" is not a date Chaos can read." }] }),
  } as unknown as typeof dataRpc;
  render(<DataSettings api={api} containers={[]} onImported={() => {}} onReplaced={() => {}}/>);
  await screen.findByText(/Latest backup:/);

  fireEvent.change(screen.getByLabelText("File"), { target: { files: [new dom.window.File(["x"], "bad.csv")] } });
  await screen.findByText(/Line 4: "next tuesday"/);
  assert.equal((screen.getByRole("button", { name: "Import 2 tasks" }) as HTMLButtonElement).disabled, true);
  cleanup();
});

test("deleting all local data needs the typed phrase", async () => {
  const resets: ResetRequest[] = [];
  const api = {
    ...dataRpc,
    status: async () => status,
    reset: async (request: ResetRequest) => { resets.push(request); return { ok: true, backupFilesDeleted: 0, error: null }; },
  } as unknown as typeof dataRpc;
  render(<DataSettings api={api} containers={[]} onImported={() => {}} onReplaced={() => {}}/>);
  await screen.findByText(/Latest backup:/);

  const button = screen.getByRole("button", { name: "Delete all local data" }) as HTMLButtonElement;
  await userEvent.type(screen.getByLabelText(/Type DELETE to confirm/), "delete");
  assert.equal(button.disabled, true);
  await userEvent.clear(screen.getByLabelText(/Type DELETE to confirm/));
  await userEvent.type(screen.getByLabelText(/Type DELETE to confirm/), "DELETE");
  await userEvent.click(button);

  await waitFor(() => assert.equal(resets.length, 1));
  assert.deepEqual(resets[0], { confirmation: "DELETE", deleteBackups: false });
  await screen.findByText(/Backups were kept/);
  cleanup();
});
