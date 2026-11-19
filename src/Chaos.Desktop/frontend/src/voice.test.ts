import test from "node:test";
import assert from "node:assert/strict";
import {
  classifyMicError,
  extensionFor,
  formatElapsed,
  levelFromSamples,
  maxVoiceMs,
  micMessage,
  pickMimeType,
  recorderSupported,
  voiceTitle,
} from "./voice";

test("recording needs both getUserMedia and MediaRecorder", () => {
  assert.equal(recorderSupported({ mediaDevices: { getUserMedia: () => {} }, MediaRecorder: function Recorder() {} }), true);
  assert.equal(recorderSupported({ mediaDevices: undefined, MediaRecorder: function Recorder() {} }), false);
  assert.equal(recorderSupported({ mediaDevices: { getUserMedia: () => {} } }), false);
  assert.equal(recorderSupported({}), false);
});

test("microphone failures map to plain, actionable states", () => {
  assert.equal(classifyMicError({ name: "NotAllowedError" }), "denied");
  assert.equal(classifyMicError({ name: "SecurityError" }), "denied");
  assert.equal(classifyMicError({ name: "NotFoundError" }), "missing");
  assert.equal(classifyMicError({ name: "NotReadableError" }), "busy");
  assert.equal(classifyMicError(new Error("boom")), "failed");
  assert.equal(classifyMicError(null), "failed");
  for (const problem of ["unsupported", "denied", "missing", "busy", "failed"] as const) {
    assert.match(micMessage(problem), /audio file/, `${problem} offers the file fallback`);
  }
});

test("the recording format is the first one the WebView supports, in playable order", () => {
  assert.equal(
    pickMimeType((type) => type.startsWith("audio/webm")),
    "audio/webm;codecs=opus",
  );
  assert.equal(
    pickMimeType((type) => type === "audio/mp4"),
    "audio/mp4",
  );
  assert.equal(
    pickMimeType(() => false),
    null,
  );
  assert.equal(extensionFor("audio/webm;codecs=opus"), "webm");
  assert.equal(extensionFor("audio/mp4"), "m4a");
  assert.equal(extensionFor("audio/ogg;codecs=opus"), "ogg");
});

test("elapsed time and the note title read naturally", () => {
  assert.equal(formatElapsed(0), "0:00");
  assert.equal(formatElapsed(65_400), "1:05");
  assert.equal(formatElapsed(maxVoiceMs), "30:00");
  assert.equal(formatElapsed(3_725_000), "1:02:05");
  assert.equal(formatElapsed(-5), "0:00");
  assert.equal(voiceTitle(new Date(2026, 10, 17, 9, 5)), "🎙️ Voice note, 09:05");
  assert.equal(maxVoiceMs, 1_800_000);
});

test("the level meter is silent for silence and rises with loudness", () => {
  assert.equal(levelFromSamples([]), 0);
  assert.equal(levelFromSamples(new Uint8Array(64).fill(128)), 0);
  const quiet = levelFromSamples(Uint8Array.from({ length: 64 }, (_, i) => 128 + (i % 2 ? 4 : -4)));
  const loud = levelFromSamples(Uint8Array.from({ length: 64 }, (_, i) => 128 + (i % 2 ? 90 : -90)));
  assert.ok(quiet > 0 && quiet < loud && loud <= 1);
});
