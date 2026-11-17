/**
 * Voice note helpers (PLAN2 P10): pure logic for the recorder, kept apart from the DOM so the
 * capability states and limits are tested without a microphone.
 */

/** A recording stops by itself at this length (PLAN2 P10). */
export const maxVoiceMs = 30 * 60 * 1000;

export type MicProblem = "unsupported" | "denied" | "missing" | "busy" | "failed";

/** Whether this WebView can record at all. The check is about APIs, not about permission. */
export function recorderSupported(env: { mediaDevices?: unknown; MediaRecorder?: unknown }): boolean {
  const devices = env.mediaDevices as { getUserMedia?: unknown } | undefined;
  return typeof devices?.getUserMedia === "function" && typeof env.MediaRecorder === "function";
}

/** Maps a getUserMedia failure to something the person can act on. */
export function classifyMicError(error: unknown): MicProblem {
  const name = typeof error === "object" && error !== null && "name" in error ? String((error as { name: unknown }).name) : "";
  if (name === "NotAllowedError" || name === "SecurityError" || name === "PermissionDeniedError") return "denied";
  if (name === "NotFoundError" || name === "DevicesNotFoundError" || name === "OverconstrainedError") return "missing";
  if (name === "NotReadableError" || name === "TrackStartError" || name === "AbortError") return "busy";
  return "failed";
}

export function micMessage(problem: MicProblem): string {
  switch (problem) {
    case "unsupported": return "This version of Chaos cannot record audio. Attach an audio file instead.";
    case "denied": return "Microphone access was denied. Allow it for Chaos in Windows privacy settings, or attach an audio file instead.";
    case "missing": return "No microphone was found. Plug one in, or attach an audio file instead.";
    case "busy": return "The microphone is in use by another app, or could not be started. Close the other app and try again, or attach an audio file instead.";
    default: return "Recording could not start. Try again, or attach an audio file instead.";
  }
}

/** The first container the WebView can record, in the order Chaos can play and sniff them back (WebM, then MP4/M4A, then Ogg). */
export function pickMimeType(isTypeSupported: (type: string) => boolean): string | null {
  for (const type of ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/ogg;codecs=opus"]) {
    if (isTypeSupported(type)) return type;
  }
  return null;
}

export function extensionFor(mimeType: string): string {
  if (mimeType.startsWith("audio/mp4")) return "m4a";
  if (mimeType.startsWith("audio/ogg")) return "ogg";
  return "webm";
}

/** m:ss, or h:mm:ss past an hour. */
export function formatElapsed(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000));
  const hours = Math.floor(total / 3600);
  const minutes = Math.floor((total % 3600) / 60);
  const seconds = total % 60;
  const pad = (n: number) => String(n).padStart(2, "0");
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${minutes}:${pad(seconds)}`;
}

/** The note's first line: `🎙️ Voice note, <time>` with the local 24-hour time. */
export function voiceTitle(at: Date): string {
  const pad = (n: number) => String(n).padStart(2, "0");
  return `🎙️ Voice note, ${pad(at.getHours())}:${pad(at.getMinutes())}`;
}

/** A 0..1 level from time-domain samples (bytes centred on 128), for the meter. */
export function levelFromSamples(samples: ArrayLike<number>): number {
  if (samples.length === 0) return 0;
  let sum = 0;
  for (let i = 0; i < samples.length; i++) {
    const centred = (samples[i] - 128) / 128;
    sum += centred * centred;
  }
  return Math.min(1, Math.sqrt(sum / samples.length) * 3);
}
