import React from "react";
import { Mic, Paperclip, Square, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { classifyMicError, extensionFor, formatElapsed, levelFromSamples, maxVoiceMs, micMessage, pickMimeType, recorderSupported, type MicProblem } from "./voice";

export type RecordedAudio = { blob: Blob; mimeType: string };

/** A live recording: levels while it runs, the audio when it stops. */
export type RecordingSession = {
  onLevel: (listener: (level: number) => void) => void;
  stop: () => Promise<RecordedAudio>;
  cancel: () => void;
};

/** How audio gets recorded; the real one uses the WebView's microphone, tests pass a fake. */
export type RecorderEngine = {
  supported: () => boolean;
  start: () => Promise<RecordingSession>;
};

/** MediaRecorder over getUserMedia, with an analyser for the level meter. */
export const browserRecorder: RecorderEngine = {
  supported: () => typeof navigator !== "undefined" && recorderSupported({ mediaDevices: navigator.mediaDevices, MediaRecorder: typeof MediaRecorder === "undefined" ? undefined : MediaRecorder }),
  start: async () => {
    const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
    const mimeType = pickMimeType(type => MediaRecorder.isTypeSupported(type));
    const recorder = mimeType ? new MediaRecorder(stream, { mimeType }) : new MediaRecorder(stream);
    const chunks: Blob[] = [];
    recorder.ondataavailable = event => { if (event.data.size > 0) chunks.push(event.data); };
    let listener: ((level: number) => void) | null = null;
    let frame = 0;
    let context: AudioContext | null = null;
    try {
      context = new AudioContext();
      const analyser = context.createAnalyser();
      analyser.fftSize = 512;
      context.createMediaStreamSource(stream).connect(analyser);
      const samples = new Uint8Array(analyser.fftSize);
      const tick = () => { analyser.getByteTimeDomainData(samples); listener?.(levelFromSamples(samples)); frame = requestAnimationFrame(tick); };
      frame = requestAnimationFrame(tick);
    } catch { /* the meter is optional; recording still works */ }
    const release = () => { cancelAnimationFrame(frame); stream.getTracks().forEach(track => track.stop()); void context?.close().catch(() => undefined); };
    recorder.start(1000);
    return {
      onLevel: next => { listener = next; },
      stop: () => new Promise<RecordedAudio>((resolve, reject) => {
        recorder.onstop = () => { release(); resolve({ blob: new Blob(chunks, { type: recorder.mimeType || mimeType || "audio/webm" }), mimeType: recorder.mimeType || mimeType || "audio/webm" }); };
        recorder.onerror = () => { release(); reject(new Error("The recording failed.")); };
        if (recorder.state === "inactive") recorder.onstop?.(new Event("stop")); else recorder.stop();
      }),
      cancel: () => { recorder.onstop = null; try { if (recorder.state !== "inactive") recorder.stop(); } catch { /* already stopped */ } release(); },
    };
  },
};

type Props = {
  engine?: RecorderEngine;
  disabled?: boolean;
  /** Called with the finished recording as a file and when it began; resolves once it is stored. Rejects with a message to show. */
  onRecorded: (file: File, startedAt: Date) => Promise<void>;
  /** The fallback: an audio file the person chose instead of recording. */
  onAttachFile: (file: File) => Promise<void>;
  onError: (message: string | null) => void;
};

type State = { kind: "idle" } | { kind: "starting" } | { kind: "recording"; startedAt: number } | { kind: "saving" } | { kind: "problem"; problem: MicProblem };

/**
 * Voice notes (PLAN2 P10): a visible timer and level meter while recording, a 30-minute cap,
 * and plain messages when the microphone is denied, missing or busy, with "Attach an audio
 * file" as the way out.
 */
export function VoiceRecorder({ engine = browserRecorder, disabled, onRecorded, onAttachFile, onError }: Props) {
  const [state, setState] = React.useState<State>({ kind: "idle" });
  const [elapsed, setElapsed] = React.useState(0);
  const [level, setLevel] = React.useState(0);
  const session = React.useRef<RecordingSession | null>(null);
  const startedAt = React.useRef<Date | null>(null);
  const fileInput = React.useRef<HTMLInputElement | null>(null);
  const finishing = React.useRef(false);

  React.useEffect(() => () => { session.current?.cancel(); session.current = null; }, []);

  const recordingSince = state.kind === "recording" ? state.startedAt : null;
  React.useEffect(() => {
    if (recordingSince === null) return;
    const handle = setInterval(() => {
      const ms = Date.now() - recordingSince;
      setElapsed(ms);
      if (ms >= maxVoiceMs) void stop();
    }, 250);
    return () => clearInterval(handle);
  }, [recordingSince]);

  async function start() {
    onError(null);
    if (!engine.supported()) { setState({ kind: "problem", problem: "unsupported" }); return; }
    setState({ kind: "starting" });
    try {
      const started = await engine.start();
      session.current = started;
      started.onLevel(setLevel);
      startedAt.current = new Date();
      finishing.current = false;
      setElapsed(0); setLevel(0);
      setState({ kind: "recording", startedAt: Date.now() });
    } catch (error) {
      setState({ kind: "problem", problem: classifyMicError(error) });
    }
  }

  async function stop() {
    const active = session.current;
    if (!active || finishing.current) return;
    finishing.current = true;
    session.current = null;
    setState({ kind: "saving" });
    try {
      const audio = await active.stop();
      const began = startedAt.current ?? new Date();
      await onRecorded(new File([audio.blob], `voice-note.${extensionFor(audio.mimeType)}`, { type: audio.mimeType }), began);
      setState({ kind: "idle" });
    } catch (error) {
      onError(error instanceof Error ? error.message : "The voice note could not be saved.");
      setState({ kind: "idle" });
    }
  }

  function cancel() {
    session.current?.cancel();
    session.current = null;
    setState({ kind: "idle" });
  }

  async function attach(file: File | undefined) {
    if (!file) return;
    setState({ kind: "saving" });
    try { await onAttachFile(file); } catch (error) { onError(error instanceof Error ? error.message : "The audio file could not be added."); }
    setState({ kind: "idle" });
  }

  if (state.kind === "recording") {
    return <div role="group" aria-label="Recording a voice note" className="flex flex-wrap items-center gap-2 rounded-lg border border-destructive/40 bg-destructive/5 px-3 py-1.5">
      <span className="size-2.5 animate-pulse rounded-full bg-destructive motion-reduce:animate-none" aria-hidden="true"/>
      <span role="timer" aria-label="Recording time" className="text-sm tabular-nums">{formatElapsed(elapsed)} <span className="text-xs text-muted-foreground">of {formatElapsed(maxVoiceMs)}</span></span>
      <div role="meter" aria-label="Microphone level" aria-valuemin={0} aria-valuemax={100} aria-valuenow={Math.round(level * 100)} className="h-2 w-24 overflow-hidden rounded-full bg-muted">
        <div className="h-full bg-destructive transition-[width] duration-75 motion-reduce:transition-none" style={{ width: `${Math.round(level * 100)}%` }}/>
      </div>
      <Button type="button" size="sm" onClick={() => void stop()}><Square/>Stop and save</Button>
      <Button type="button" size="sm" variant="ghost" onClick={cancel}><X/>Discard</Button>
    </div>;
  }

  return <div className="flex flex-wrap items-center gap-2">
    <Button type="button" variant="outline" disabled={disabled || state.kind === "starting" || state.kind === "saving"} onClick={() => void start()} title="Record a voice note">
      <Mic/>{state.kind === "starting" ? "Starting…" : state.kind === "saving" ? "Saving…" : "Voice note"}
    </Button>
    {state.kind === "problem" && <div role="alert" className="flex flex-wrap items-center gap-2 text-xs text-destructive">
      <span>{micMessage(state.problem)}</span>
      <Button type="button" size="xs" variant="outline" onClick={() => fileInput.current?.click()}><Paperclip/>Attach an audio file</Button>
      <Button type="button" size="xs" variant="ghost" onClick={() => setState({ kind: "idle" })}>Dismiss</Button>
    </div>}
    <input ref={fileInput} type="file" accept="audio/*,.m4a,.mp3,.wav,.ogg,.webm" className="sr-only" tabIndex={-1} aria-label="Audio file" onChange={event => { const file = event.target.files?.[0]; event.target.value = ""; void attach(file); }}/>
  </div>;
}
