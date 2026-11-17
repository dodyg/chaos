import { test } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";
import { attachmentRpc, type AttachmentDto } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  MutationObserver: { value: dom.window.MutationObserver, configurable: true },
  Event: { value: dom.window.Event, configurable: true },
  NodeFilter: { value: dom.window.NodeFilter, configurable: true },
  CustomEvent: { value: dom.window.CustomEvent, configurable: true },
  localStorage: { value: dom.window.localStorage, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
});
// jsdom has no object URLs; each Blob gets a distinct fake one so the tests can see what was shown.
const made: Blob[] = [];
(URL as unknown as { createObjectURL: (blob: Blob) => string }).createObjectURL = blob => { made.push(blob); return `blob:test/${made.length}`; };
(URL as unknown as { revokeObjectURL: (url: string) => void }).revokeObjectURL = () => undefined;

const { render, screen, waitFor, cleanup, fireEvent } = await import("@testing-library/react");
const { uploadFile, attachmentBackgroundName, attachmentMarkdown, clearAttachmentUrls, internalAttachmentId, usePageImage, AttachmentPanel } = await import("./attachments");
const { MarkdownPreview, attachmentIds } = await import("./markdown");
const { ImageEditor, rotatedSize, clampCrop, dragToCrop, resultSize } = await import("./ImageEditor");
const { MarkdownEditor } = await import("./MarkdownEditor");

type Api = Parameters<typeof uploadFile>[0];

const imageId = "00000000-0000-4000-8000-0000000000a1";
const pdfId = "00000000-0000-4000-8000-0000000000a2";
const exeId = "00000000-0000-4000-8000-0000000000a3";
const goneId = "00000000-0000-4000-8000-0000000000a4";

function item(id: string, fileName: string, mediaType: string, preview: string, patch: Partial<AttachmentDto> = {}): AttachmentDto {
  return { id, ownerKind: "task", ownerId: "t1", fileName, mediaType, byteSize: "12", sha256: id.replace(/-/g, "").padEnd(64, "0"), width: preview === "image" ? 40 : null, height: preview === "image" ? 30 : null,
    version: "1", createdAtUtc: "2026-11-17T08:00:00Z", bytes: "present", preview, canOpen: !/\.exe$/i.test(fileName), ...patch };
}

const files = new Map<string, AttachmentDto>([
  [imageId, item(imageId, "shot.png", "image/png", "image")],
  [pdfId, item(pdfId, "plan.pdf", "application/pdf", "pdf")],
  [exeId, item(exeId, "setup.exe", "application/octet-stream", "none")],
]);

function api(overrides: Record<string, unknown> = {}) {
  const calls: { name: string; request: unknown }[] = [];
  const added: AttachmentDto[] = [];
  const fake = {
    ...attachmentRpc,
    resolve: async (request: { ids: string[] }) => { calls.push({ name: "resolve", request }); return { ok: true, attachments: request.ids.map(id => files.get(id)).filter((row): row is AttachmentDto => !!row), error: null }; },
    list: async () => ({ ok: true, attachments: [...files.values(), ...added], error: null }),
    read: async (request: { attachmentId: string; offset: string }) => { calls.push({ name: "read", request }); return { ok: true, base64: btoa("twelve bytes"), totalSize: "12", error: null }; },
    begin: async (request: unknown) => { calls.push({ name: "begin", request }); return { ok: true, uploadId: "u1", chunkBytes: 4, maxFileBytes: "104857600", error: null }; },
    chunk: async (request: { index: number; base64: string }) => { calls.push({ name: "chunk", request }); return { ok: true, error: null }; },
    commit: async (request: unknown) => {
      calls.push({ name: "commit", request });
      const row = item("00000000-0000-4000-8000-0000000000b1", "pasted.png", "image/png", "image");
      added.push(row);
      return { ok: true, attachment: row, error: null };
    },
    cancel: async (request: unknown) => { calls.push({ name: "cancel", request }); return { ok: true, error: null }; },
    open: async (request: unknown) => { calls.push({ name: "open", request }); return { ok: true, path: null, error: null }; },
    showInFolder: async (request: unknown) => { calls.push({ name: "showInFolder", request }); return { ok: true, path: null, error: null }; },
    saveCopy: async (request: unknown) => { calls.push({ name: "saveCopy", request }); return { ok: true, path: "C:\\Exports\\setup.exe", error: null }; },
    thumbnail: async (request: unknown) => { calls.push({ name: "thumbnail", request }); return { ok: true, base64: btoa("jpeg"), totalSize: "4", error: null }; },
    editImage: async (request: unknown) => { calls.push({ name: "editImage", request }); return { ok: true, attachment: item(imageId, "shot.png", "image/png", "image", { width: 30, height: 60, version: "2" }), error: null }; },
    pageImage: async (request: { name: string }) => ({ ok: true, attachment: request.name === "shot.png" ? files.get(imageId)! : null, error: null }),
    ...overrides,
  } as unknown as Api;
  return { fake, calls, added };
}

test("a file goes up as begin, ordered chunks and one commit, and a refusal cancels the upload", async () => {
  const { fake, calls } = api();
  const result = await uploadFile(fake, "task", "t1", new File(["0123456789"], "ten.txt"));
  assert.equal(result.fileName, "pasted.png");
  assert.deepEqual(calls.map(call => call.name), ["begin", "chunk", "chunk", "chunk", "commit"]);
  assert.deepEqual(calls.filter(call => call.name === "chunk").map(call => (call.request as { index: number }).index), [0, 1, 2]);
  assert.deepEqual(calls.filter(call => call.name === "chunk").map(call => atob((call.request as { base64: string }).base64)), ["0123", "4567", "89"]);
  assert.equal((calls[0].request as { size: string; fileName: string }).size, "10");

  const failing = api({ chunk: async () => ({ ok: false, error: { kind: "validation", message: "More data arrived than the file said.", fields: [], correlationId: "x" } }) });
  await assert.rejects(uploadFile(failing.fake, "task", "t1", new File(["abc"], "a.txt")), /More data arrived/);
  assert.ok(failing.calls.some(call => call.name === "cancel"));
});

test("references are built and read back by ID, and only attachment links count", () => {
  assert.equal(attachmentMarkdown(files.get(imageId)!), `![shot.png](chaos:attachment/${imageId})`);
  assert.equal(attachmentMarkdown(files.get(pdfId)!), `[plan.pdf](chaos:attachment/${pdfId})`);
  assert.equal(attachmentMarkdown({ id: pdfId, fileName: "a [b].pdf", preview: "pdf" }), `[a \\[b\\].pdf](chaos:attachment/${pdfId})`);
  assert.equal(internalAttachmentId(`chaos:attachment/${imageId}`), imageId);
  assert.equal(internalAttachmentId("https://example.com/chaos:attachment/x"), null);
  assert.deepEqual(attachmentIds(`![a](chaos:attachment/${imageId}) and [b](chaos:attachment/${pdfId}) and \`![c](chaos:attachment/${goneId})\``).sort(), [imageId, pdfId].sort());
});

test("an attached image shows in place from this computer's bytes, and web images stay blocked", async () => {
  clearAttachmentUrls(); made.length = 0;
  const { fake, calls } = api();
  const { container } = render(<MarkdownPreview source={`Look: ![shot](chaos:attachment/${imageId})\n\n![remote](https://example.com/x.png)`} links={{ attachmentApi: fake }}/>);
  await waitFor(() => assert.ok(container.querySelector('img[src^="blob:test/"]')));
  assert.equal(container.querySelectorAll("img").length, 1, "only the attached image is an <img>");
  assert.equal(container.querySelector("img")!.getAttribute("src")?.startsWith("blob:"), true);
  assert.ok(screen.getByRole("img", { name: /Image not loaded: remote/ }), "the web image is a placeholder");
  assert.match(container.textContent ?? "", /images from the web are not loaded/);
  assert.equal(calls.filter(call => call.name === "resolve").length, 1, "one batched resolve");
  assert.equal(made[0].type, "image/png");
  cleanup();
});

test("a file link is a card with a preview for PDFs, no Open for programs, and a missing file says so", async () => {
  clearAttachmentUrls(); made.length = 0;
  const { fake, calls } = api();
  const source = `[plan](chaos:attachment/${pdfId})\n\n[installer](chaos:attachment/${exeId})\n\n[old](chaos:attachment/${goneId})`;
  const { container } = render(<MarkdownPreview source={source} links={{ attachmentApi: fake }}/>);
  await waitFor(() => assert.equal(container.querySelectorAll('[data-slot="attachment-card"]').length, 2));
  assert.match(container.textContent ?? "", /missing file/);

  const cards = [...container.querySelectorAll('[data-slot="attachment-card"]')];
  const pdf = cards.find(card => card.textContent?.includes("plan"))!;
  const exe = cards.find(card => card.textContent?.includes("installer"))!;
  assert.ok([...pdf.querySelectorAll("button")].some(button => button.textContent === "Open"));
  assert.equal([...exe.querySelectorAll("button")].some(button => button.textContent === "Open"), false, "a program has no Open");
  assert.ok([...exe.querySelectorAll("button")].some(button => button.textContent === "Show in folder"));
  assert.ok([...exe.querySelectorAll("button")].some(button => button.textContent === "Save a copy"));

  fireEvent.click([...exe.querySelectorAll("button")].find(button => button.textContent === "Save a copy")!);
  await waitFor(() => assert.match(exe.textContent ?? "", /Saved a copy to C:\\Exports\\setup\.exe/));
  assert.deepEqual(calls.find(call => call.name === "saveCopy")?.request, { attachmentId: exeId });

  fireEvent.click([...pdf.querySelectorAll("button")].find(button => button.textContent === "Preview")!);
  await waitFor(() => assert.equal(pdf.querySelector("iframe")?.getAttribute("src")?.startsWith("blob:"), true));
  assert.equal(made.at(-1)?.type, "application/pdf");
  cleanup();
});

test("bytes that are not on this computer show as such instead of loading", async () => {
  clearAttachmentUrls();
  const away = item(imageId, "shot.png", "image/png", "image", { bytes: "missing" });
  const { fake } = api({ resolve: async () => ({ ok: true, attachments: [away], error: null }) });
  const { container } = render(<MarkdownPreview source={`![shot](chaos:attachment/${imageId})`} links={{ attachmentApi: fake }}/>);
  await waitFor(() => assert.match(container.textContent ?? "", /not on this computer/));
  assert.equal(container.querySelector("img"), null);
  cleanup();
});

test("pasting a screenshot into the editor uploads it and inserts the reference at the caret", async () => {
  clearAttachmentUrls();
  const { fake, calls } = api();
  function Host() {
    const [value, setValue] = React.useState("Before\nAfter");
    return <MarkdownEditor id="notes" label="Notes" value={value} onChange={setValue} attachmentOwner={{ kind: "task", id: "t1" }} attachmentApi={fake}/>;
  }
  render(<Host/>);
  const area = screen.getByLabelText("Notes") as HTMLTextAreaElement;
  area.focus(); area.setSelectionRange(7, 7);
  fireEvent.paste(area, { clipboardData: { files: [new File(["png-bytes"], "image.png", { type: "image/png" })], getData: () => "", types: ["Files"] } });
  await waitFor(() => assert.match(area.value, /!\[pasted\.png\]\(chaos:attachment\/00000000-0000-4000-8000-0000000000b1\)/));
  assert.equal(area.value, "Before\n![pasted.png](chaos:attachment/00000000-0000-4000-8000-0000000000b1)\nAfter");
  assert.equal((calls.find(call => call.name === "begin")!.request as { ownerKind: string }).ownerKind, "task");
  await waitFor(() => assert.ok(screen.getAllByText("pasted.png").length > 0), { timeout: 2000 }); // the panel lists it
  cleanup();
});

test("dropping a file on the editor attaches it, and a file drop without an owner does nothing", async () => {
  clearAttachmentUrls();
  const { fake, calls } = api();
  const { unmount } = render(<MarkdownEditor id="drop" label="Drop notes" value="text" onChange={() => undefined} attachmentOwner={{ kind: "page", id: "p1" }} attachmentApi={fake}/>);
  const area = screen.getByLabelText("Drop notes") as HTMLTextAreaElement;
  fireEvent.drop(area, { dataTransfer: { files: [new File(["x"], "a.pdf", { type: "application/pdf" })], types: ["Files"] } });
  await waitFor(() => assert.ok(calls.some(call => call.name === "commit")));
  assert.equal((calls.find(call => call.name === "begin")!.request as { ownerKind: string }).ownerKind, "page");
  unmount();

  const none = api();
  render(<MarkdownEditor id="plain" label="Plain" value="text" onChange={() => undefined} attachmentApi={none.fake}/>);
  fireEvent.drop(screen.getByLabelText("Plain"), { dataTransfer: { files: [new File(["x"], "a.pdf")], types: ["Files"] } });
  await new Promise(resolve => setTimeout(resolve, 30));
  assert.equal(none.calls.length, 0);
  cleanup();
});

test("the panel lists files, inserts a reference, removes with an undo and renames", async () => {
  clearAttachmentUrls();
  const inserted: string[] = [];
  const deletions: unknown[] = [];
  const { fake } = api({
    delete: async (request: unknown) => { deletions.push(request); return { ok: true, attachment: { ...files.get(pdfId)!, version: "2" }, error: null }; },
    restore: async () => ({ ok: true, attachment: files.get(pdfId)!, error: null }),
    rename: async (request: { fileName: string }) => ({ ok: true, attachment: { ...files.get(pdfId)!, fileName: request.fileName, version: "2" }, error: null }),
  });
  const { container } = render(<AttachmentPanel api={fake} ownerKind="task" ownerId="t1" onInsert={markdown => inserted.push(markdown)}/>);
  const buttons = (label: RegExp) => [...container.querySelectorAll("button")].filter(button => label.test(button.textContent ?? ""));
  await screen.findByText("plan.pdf");
  // Images preview by default (the first file is a PNG); a PDF starts closed.
  await waitFor(() => assert.ok(container.querySelector('img[src^="blob:test/"]')));
  assert.equal(container.querySelector("iframe"), null);
  fireEvent.click(buttons(/^Hide preview$/)[0]);
  await waitFor(() => assert.equal(container.querySelector("img"), null));
  fireEvent.click(buttons(/^Insert$/)[1]);
  assert.deepEqual(inserted, [`[plan.pdf](chaos:attachment/${pdfId})`]);

  fireEvent.click(buttons(/^Remove$/)[1]);
  await screen.findByText("Removed plan.pdf.");
  assert.equal((deletions[0] as { expectedVersion: string }).expectedVersion, "1");
  fireEvent.click(buttons(/^Undo$/)[0]);
  await screen.findByText("Restored plan.pdf.");

  fireEvent.click(buttons(/^Rename$/)[1]);
  const input = screen.getByLabelText("New name for plan.pdf") as HTMLInputElement;
  fireEvent.change(input, { target: { value: "budget.pdf" } });
  fireEvent.submit(input.closest("form")!);
  await new Promise(resolve => setTimeout(resolve, 100));
  assert.equal(container.querySelector('input[aria-label^="New name for"]'), null);
  cleanup();
});

// The same table runs in tests/Chaos.Application.Tests/Pages/BackgroundNameTests.cs against PageFrontMatter.AttachmentBackgroundName.
test("which background values name an attached image matches the host's parser", () => {
  const names: [string, string][] = [["attachments/plan.jpg", "plan.jpg"], ["Attachments/Plan Final.PNG", "Plan Final.PNG"], ["  attachments/a.png  ", "a.png"], ["attachments/photo (1).webp", "photo (1).webp"]];
  for (const [value, expected] of names) assert.equal(attachmentBackgroundName(value), expected, value);
  for (const value of ["attachments/", "attachments/../x.png", "attachments/sub/x.png", "attachments/sub\\x.png", "attachments/a:b.png", "attachments/x.png.", "https://example.com/x.png", "//example.com/x.png", "C:/x.png", "sunset", "#fff", ""]) {
    assert.equal(attachmentBackgroundName(value), null, value);
  }
  assert.equal(attachmentBackgroundName(null), null);
});

test("a page background resolves to the image attached to that page, or to nothing", async () => {
  clearAttachmentUrls();
  const { fake } = api();
  function Probe({ name }: { name: string | null }) {
    const image = usePageImage(fake, "p1", name);
    return <p data-testid="probe">{image === undefined ? "loading" : image.url ?? "none"}</p>;
  }
  const { rerender } = render(<Probe name="shot.png"/>);
  await waitFor(() => assert.match(screen.getByTestId("probe").textContent ?? "", /^blob:test\//));
  rerender(<Probe name="missing.png"/>);
  await waitFor(() => assert.equal(screen.getByTestId("probe").textContent, "none"));
  rerender(<Probe name={null}/>);
  assert.equal(screen.getByTestId("probe").textContent, "none");
  cleanup();
});

test("crop geometry stays inside the image and a click is not a crop", () => {
  assert.deepEqual(rotatedSize(200, 100, 90), { width: 100, height: 200 });
  assert.deepEqual(rotatedSize(200, 100, 180), { width: 200, height: 100 });
  assert.deepEqual(clampCrop({ x: -5, y: 90, width: 500, height: 50 }, 200, 100), { x: 0, y: 90, width: 200, height: 10 });
  assert.deepEqual(clampCrop({ x: 500, y: 0, width: 0, height: 0 }, 200, 100), { x: 199, y: 0, width: 1, height: 1 });
  assert.deepEqual(dragToCrop({ x: 50, y: 40 }, { x: 10, y: 10 }, 0.5, 200, 100), { x: 20, y: 20, width: 80, height: 60 });
  assert.equal(dragToCrop({ x: 5, y: 5 }, { x: 6, y: 6 }, 1, 200, 100), null);
  assert.deepEqual(resultSize(200, 100, 90, { x: 0, y: 0, width: 60, height: 120 }, 60), { width: 30, height: 60 });
  assert.deepEqual(resultSize(200, 100, 0, null, 3200), { width: 200, height: 100 });
});

test("the image editor sends rotation, crop and size to the host and reports the edited image", async () => {
  clearAttachmentUrls();
  const { fake, calls } = api();
  const saved: AttachmentDto[] = [];
  let closed = false;
  const photo = item(imageId, "shot.png", "image/png", "image", { width: 200, height: 100 });
  const { container } = render(<ImageEditor api={fake} item={photo} onClose={() => { closed = true; }} onSaved={row => saved.push(row)}/>);
  const buttons = (label: RegExp) => [...document.querySelectorAll("button")].filter(button => label.test(button.textContent ?? ""));
  const save = () => buttons(/^Save image$/)[0] as HTMLButtonElement;
  assert.equal(save().disabled, true, "nothing changed yet");

  fireEvent.click(buttons(/Rotate right/)[0]);
  assert.equal(save().disabled, false);
  const field = (label: string) => [...document.querySelectorAll("label")].find(node => node.textContent?.startsWith(label))!.querySelector("input") as HTMLInputElement;
  fireEvent.change(field("Width"), { target: { value: "60" } });
  fireEvent.change(field("Height"), { target: { value: "120" } });
  fireEvent.change(document.querySelector('select[aria-label="Longest side"]')!, { target: { value: "800" } });
  assert.match(document.querySelector('[role="status"]')?.textContent ?? "", /200×100 px → 60×120 px/);
  fireEvent.click(buttons(/Flip horizontally/)[0]); // a flip clears the crop because its coordinates change meaning
  assert.match(document.querySelector('[role="status"]')?.textContent ?? "", /→ 100×200 px/);
  fireEvent.change(field("Width"), { target: { value: "60" } });
  fireEvent.change(field("Height"), { target: { value: "120" } });

  fireEvent.click(save());
  await waitFor(() => assert.equal(closed, true));
  const request = calls.find(call => call.name === "editImage")!.request as Record<string, unknown>;
  assert.equal(request.rotateClockwise, 90);
  assert.equal(request.flipHorizontal, true);
  assert.equal(request.flipVertical, false);
  assert.deepEqual(request.crop, { x: 0, y: 0, width: 60, height: 120 });
  assert.equal(request.maxSide, 800);
  assert.equal(request.attachmentId, imageId);
  assert.equal(request.expectedVersion, "1");
  assert.equal(saved[0].width, 30);
  void container;
  cleanup();
});

test("the image editor shows the host's refusal and stays open", async () => {
  clearAttachmentUrls();
  const { fake } = api({ editImage: async () => ({ ok: false, attachment: null, error: { kind: "validation", message: "Animated images cannot be edited.", fields: [], correlationId: "x" } }) });
  let closed = false;
  render(<ImageEditor api={fake} item={item(imageId, "a.gif", "image/gif", "image", { width: 10, height: 10 })} onClose={() => { closed = true; }} onSaved={() => undefined}/>);
  fireEvent.click([...document.querySelectorAll("button")].find(button => /Rotate left/.test(button.textContent ?? ""))!);
  fireEvent.click([...document.querySelectorAll("button")].find(button => button.textContent === "Save image")!);
  await waitFor(() => assert.match(document.querySelector('[role="alert"]')?.textContent ?? "", /Animated/));
  assert.equal(closed, false);
  cleanup();
});

test("a large image shows its thumbnail and does not read the whole file", async () => {
  clearAttachmentUrls(); made.length = 0;
  const large = item(imageId, "big.png", "image/png", "image", { byteSize: String(3 * 1024 * 1024), width: 4000, height: 3000 });
  const { fake, calls } = api({ resolve: async () => ({ ok: true, attachments: [large], error: null }) });
  const { container } = render(<MarkdownPreview source={`![big](chaos:attachment/${imageId})`} links={{ attachmentApi: fake }}/>);
  await waitFor(() => assert.ok(container.querySelector('img[src^="blob:test/"]')));
  assert.equal(made[0].type, "image/jpeg");
  assert.equal(calls.some(call => call.name === "read"), false);
  assert.ok(calls.some(call => call.name === "thumbnail"));
  cleanup();
});
