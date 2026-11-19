import { test, mock } from "node:test";
import assert from "node:assert/strict";
import { JSDOM } from "jsdom";
import React from "react";

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
const { render, screen, waitFor, cleanup, fireEvent, act } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { VoiceRecorder } = await import("./VoiceRecorder");
type Engine = import("./VoiceRecorder").RecorderEngine;
const { maxVoiceMs } = await import("./voice");

/** A fake microphone: records nothing, reports a level, and hands back a few bytes when stopped. */
function fakeEngine(options: { supported?: boolean; fail?: string } = {}) {
  const log = { started: 0, stopped: 0, cancelled: 0 };
  let emit: (level: number) => void = () => {};
  const engine: Engine = {
    supported: () => options.supported ?? true,
    start: async () => {
      if (options.fail) throw Object.assign(new Error("mic"), { name: options.fail });
      log.started++;
      return {
        onLevel: (listener) => {
          emit = listener;
        },
        stop: async () => {
          log.stopped++;
          return { blob: new Blob(["abc"], { type: "audio/webm" }), mimeType: "audio/webm" };
        },
        cancel: () => {
          log.cancelled++;
        },
      };
    },
  };
  return { engine, log, level: (value: number) => emit(value) };
}

function renderRecorder(
  engine: Engine,
  handlers: { onRecorded?: (file: File, at: Date) => Promise<void>; onAttachFile?: (file: File) => Promise<void> } = {},
  errors: string[] = [],
) {
  return render(
    <VoiceRecorder
      engine={engine}
      onRecorded={handlers.onRecorded ?? (async () => {})}
      onAttachFile={handlers.onAttachFile ?? (async () => {})}
      onError={(message) => {
        if (message) errors.push(message);
      }}
    />,
    { container: dom.window.document.body },
  );
}

test("recording shows a timer and a level meter, and Stop and save hands back a webm file", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { engine, log, level } = fakeEngine();
  const recorded: File[] = [];
  renderRecorder(engine, {
    onRecorded: async (file) => {
      recorded.push(file);
    },
  });
  await user.click(screen.getByRole("button", { name: /Voice note/ }));
  assert.ok(await screen.findByRole("timer", { name: "Recording time" }));
  const meter = screen.getByRole("meter", { name: "Microphone level" });
  act(() => level(0.5));
  assert.equal(meter.getAttribute("aria-valuenow"), "50");
  await user.click(screen.getByRole("button", { name: /Stop and save/ }));
  await waitFor(() => assert.equal(recorded.length, 1));
  assert.equal(recorded[0].name, "voice-note.webm");
  assert.equal(recorded[0].type, "audio/webm");
  assert.equal(log.stopped, 1);
  await screen.findByRole("button", { name: /Voice note/ });
  cleanup();
});

test("Discard throws the recording away without saving", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { engine, log } = fakeEngine();
  let saved = 0;
  renderRecorder(engine, {
    onRecorded: async () => {
      saved++;
    },
  });
  await user.click(screen.getByRole("button", { name: /Voice note/ }));
  await user.click(await screen.findByRole("button", { name: /Discard/ }));
  assert.equal(log.cancelled, 1);
  assert.equal(saved, 0);
  await screen.findByRole("button", { name: /Voice note/ });
  cleanup();
});

test("a denied, missing or unsupported microphone is shown plainly with an audio file fallback", async () => {
  for (const [options, expected] of [
    [{ fail: "NotAllowedError" }, /denied/],
    [{ fail: "NotFoundError" }, /No microphone/],
    [{ supported: false }, /cannot record/],
  ] as const) {
    const user = userEvent.setup({ document: dom.window.document });
    const attached: File[] = [];
    renderRecorder(fakeEngine(options).engine, {
      onAttachFile: async (file) => {
        attached.push(file);
      },
    });
    await user.click(screen.getByRole("button", { name: /Voice note/ }));
    const alert = await screen.findByRole("alert");
    assert.match(alert.textContent ?? "", expected);
    assert.match(alert.textContent ?? "", /Attach an audio file/);
    const input = screen.getByLabelText("Audio file") as HTMLInputElement;
    fireEvent.change(input, { target: { files: [new File(["x"], "memo.m4a", { type: "audio/mp4" })] } });
    await waitFor(() => assert.equal(attached.length, 1));
    assert.equal(attached[0].name, "memo.m4a");
    cleanup();
  }
});

test("a failure while storing the recording is reported and the button comes back", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const errors: string[] = [];
  renderRecorder(
    fakeEngine().engine,
    {
      onRecorded: async () => {
        throw new Error("The disk is full.");
      },
    },
    errors,
  );
  await user.click(screen.getByRole("button", { name: /Voice note/ }));
  await user.click(await screen.findByRole("button", { name: /Stop and save/ }));
  await waitFor(() => assert.deepEqual(errors, ["The disk is full."]));
  await screen.findByRole("button", { name: /Voice note/ });
  cleanup();
});

test("the recording stops by itself at 30 minutes", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const { engine, log } = fakeEngine();
  const recorded: File[] = [];
  renderRecorder(engine, {
    onRecorded: async (file) => {
      recorded.push(file);
    },
  });
  await user.click(screen.getByRole("button", { name: /Voice note/ }));
  await screen.findByRole("timer", { name: "Recording time" });
  const realNow = Date.now;
  Date.now = () => realNow() + maxVoiceMs + 1000; // jump the clock; the 250 ms ticker notices
  try {
    await waitFor(() => assert.equal(recorded.length, 1), { timeout: 3000 });
  } finally {
    Date.now = realNow;
  }
  assert.equal(log.stopped, 1);
  cleanup();
});

void mock;
