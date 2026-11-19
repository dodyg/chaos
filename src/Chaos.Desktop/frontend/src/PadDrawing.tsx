import React from "react";
import {
  attachmentRpc,
  padRpc,
  type AttachmentDto,
  type CaptureDraftDto,
  type ContainerResponse,
  type PadDrawingData,
  type PadItemResponse,
} from "#neoastra";
import { ListPlus, RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { QuickAdd, type CapturePreviewApi } from "./QuickAdd";
import { uploadFile, useAttachmentUrl, type AttachmentApi } from "./attachments";
import { DrawingCanvas, renderDrawingPng } from "./DrawingCanvas";
import { emptyDrawing, parseDrawing, serializeDrawing, type Drawing, type Region } from "./drawing";

type PadApi = Pick<typeof padRpc, "getDrawing" | "saveDrawing" | "makeTaskFromRegion">;

/** Strokes are saved this long after the last change; the PNG preview waits for a longer pause. */
export const strokeSaveDelayMs = 700;
export const previewDelayMs = 4000;

type SaveState = { status: "saved" | "pending" | "saving" | "failed"; error: string | null };

type Props = {
  api?: PadApi;
  attachmentApi?: AttachmentApi;
  item: PadItemResponse;
  containers: ContainerResponse[];
  captureApi?: CapturePreviewApi;
  /** The backlink region of a task being opened in the pad, outlined on the drawing. */
  highlight?: Region | null;
  /** A saved change reached the store; the pad reloads its list (preview thumbnails). */
  onSaved: () => void;
  onTaskMade: (title: string) => void;
  onError: (message: string | null) => void;
};

const operationId = () => crypto.randomUUID();

/**
 * One open drawing note (PLAN2 P10): loads the strokes, saves them as they change with the
 * editor-revision rule of the pad text (a delayed save can never replace newer strokes), renders
 * a PNG preview into an attachment when editing pauses or the note closes, and makes tasks from
 * a selected rectangle.
 */
export function DrawingNote({
  api = padRpc,
  attachmentApi = attachmentRpc,
  item,
  containers,
  captureApi,
  highlight,
  onSaved,
  onTaskMade,
  onError,
}: Props) {
  const [data, setData] = React.useState<PadDrawingData | null>(null);
  const [drawing, setDrawing] = React.useState<Drawing>(emptyDrawing);
  const [state, setState] = React.useState<SaveState>({ status: "saved", error: null });
  const [region, setRegion] = React.useState<Region | null>(null);
  const [makingTask, setMakingTask] = React.useState(false);
  const [loadError, setLoadError] = React.useState<string | null>(null);

  const drawingRef = React.useRef<Drawing>(emptyDrawing);
  const revision = React.useRef(0);
  const dirty = React.useRef(false);
  const previewDirty = React.useRef(false);
  const saving = React.useRef<Promise<boolean> | null>(null);
  const strokeTimer = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const previewTimer = React.useRef<ReturnType<typeof setTimeout> | null>(null);
  const size = React.useRef({ width: 0, height: 0 });
  const mounted = React.useRef(true);

  React.useEffect(() => {
    mounted.current = true;
    let current = true;
    void api
      .getDrawing({ itemId: item.id })
      .then((result) => {
        if (!current) return;
        if (!result.ok || !result.drawing) {
          setLoadError(result.error?.message ?? "The drawing could not be opened.");
          return;
        }
        const loaded = parseDrawing(result.drawing.strokesJson);
        drawingRef.current = loaded;
        revision.current = Number(result.drawing.revision);
        size.current = { width: result.drawing.width, height: result.drawing.height };
        setData(result.drawing);
        setDrawing(loaded);
      })
      .catch((e: unknown) => {
        if (current) setLoadError(e instanceof Error ? e.message : "The drawing could not be opened.");
      });
    return () => {
      current = false;
      mounted.current = false;
      void finish();
    };
  }, [item.id]);

  /** Saves the strokes now; resolves false (and keeps them on the canvas) when the store refused. */
  async function saveNow(): Promise<boolean> {
    if (strokeTimer.current) {
      clearTimeout(strokeTimer.current);
      strokeTimer.current = null;
    }
    if (saving.current) await saving.current;
    if (!dirty.current) return true;
    const sent = revision.current;
    const run = (async () => {
      if (mounted.current) setState({ status: "saving", error: null });
      try {
        const result = await api.saveDrawing({
          itemId: item.id,
          clientRevision: String(sent),
          strokesJson: serializeDrawing(drawingRef.current),
          previewAttachmentId: null,
        });
        if (!result.ok)
          throw new Error(
            result.error?.fields?.map((field) => field.message).join("; ") || result.error?.message || "The drawing could not be saved.",
          );
        if (revision.current === sent) dirty.current = false;
        if (mounted.current) setState({ status: dirty.current ? "pending" : "saved", error: null });
        previewDirty.current = true;
        onSaved();
        return true;
      } catch (e) {
        if (mounted.current) setState({ status: "failed", error: e instanceof Error ? e.message : "The drawing could not be saved." });
        return false;
      }
    })();
    saving.current = run;
    const ok = await run;
    saving.current = null;
    return ok;
  }

  /** Renders the PNG preview and attaches it, once the strokes are stored. Best effort: a failure never loses strokes. */
  async function savePreview() {
    if (previewTimer.current) {
      clearTimeout(previewTimer.current);
      previewTimer.current = null;
    }
    if (!previewDirty.current || revision.current < 1) return;
    if (!(await saveNow())) return;
    try {
      const blob = await renderDrawingPng(drawingRef.current, size.current.width, size.current.height);
      if (!blob) return;
      previewDirty.current = false;
      const attachment = await uploadFile(attachmentApi, "pad", item.id, new File([blob], "drawing-preview.png", { type: "image/png" }));
      const result = await api.saveDrawing({
        itemId: item.id,
        clientRevision: String(revision.current),
        strokesJson: serializeDrawing(drawingRef.current),
        previewAttachmentId: attachment.id,
      });
      if (!result.ok) {
        previewDirty.current = true;
        return;
      }
      onSaved();
    } catch {
      previewDirty.current = true;
    }
  }

  async function finish() {
    await saveNow();
    await savePreview();
  }

  function changed(next: Drawing) {
    drawingRef.current = next;
    revision.current += 1;
    dirty.current = true;
    previewDirty.current = true;
    setDrawing(next);
    setState({ status: "pending", error: null });
    if (strokeTimer.current) clearTimeout(strokeTimer.current);
    strokeTimer.current = setTimeout(() => {
      void saveNow();
    }, strokeSaveDelayMs);
    if (previewTimer.current) clearTimeout(previewTimer.current);
    previewTimer.current = setTimeout(() => {
      void savePreview();
    }, previewDelayMs);
  }

  async function submitTask(drafts: CaptureDraftDto[], operation: string): Promise<string | null> {
    const draft = drafts[0];
    if (!draft || !region) return "Select a part of the drawing first.";
    if (!(await saveNow())) return "The drawing could not be saved, so the task was not made. Retry saving first.";
    const result = await api.makeTaskFromRegion({
      operationId: operation,
      padItemId: item.id,
      sourceRevision: String(revision.current),
      region,
      title: draft.title,
      notes: null,
      containerId: draft.containerId,
      plannedDate: draft.plannedDate,
      dueDate: draft.dueDate,
      tagIds: [],
      draft,
    });
    if (!result.ok)
      return result.error?.fields.map((field) => field.message).join("; ") || result.error?.message || "Could not make the task.";
    setMakingTask(false);
    setRegion(null);
    onTaskMade(result.task?.title ?? draft.title);
    return null;
  }

  if (loadError)
    return (
      <p role="alert" className="text-sm text-destructive">
        {loadError}
      </p>
    );
  if (!data)
    return (
      <p role="status" className="text-sm text-muted-foreground">
        Opening the drawing…
      </p>
    );

  return (
    <div className="grid gap-2">
      <DrawingCanvas
        width={data.width}
        height={data.height}
        value={drawing}
        onChange={changed}
        highlight={highlight ?? null}
        onSelectRegion={(chosen) => {
          setRegion(chosen);
          if (!chosen) setMakingTask(false);
        }}
        label="Drawing note"
      />
      {state.status === "failed" ? (
        <div role="alert" className="flex flex-wrap items-center gap-2 text-xs text-destructive">
          <span>Not saved: {state.error} Your strokes are kept on the canvas.</span>
          <Button type="button" size="xs" variant="outline" onClick={() => void saveNow()}>
            <RotateCcw />
            Retry
          </Button>
        </div>
      ) : (
        <p role="status" aria-live="polite" className="text-xs text-muted-foreground">
          {state.status === "saving" ? "Saving…" : state.status === "pending" ? "Unsaved changes" : "Saved"}
        </p>
      )}
      {region && !makingTask && (
        <div className="flex flex-wrap items-center gap-2">
          <Button type="button" size="sm" variant="outline" onClick={() => setMakingTask(true)}>
            <ListPlus />
            Make task
          </Button>
          <span className="text-xs text-muted-foreground">The task links back to this part of the drawing.</span>
        </div>
      )}
      {region && makingTask && (
        <div className="grid gap-2 rounded-lg border p-3">
          <QuickAdd
            api={captureApi}
            containers={containers}
            defaultContainerId=""
            label="Make task from the selected part"
            initialText=""
            placeholder="Type the task. Chaos cannot read handwriting yet."
            allowBatch={false}
            submitLabel="Create task"
            autoFocus
            onSubmit={submitTask}
            onCancel={() => setMakingTask(false)}
          />
          <p className="text-xs text-muted-foreground">The drawing stays as it is; the task links back to the selected part.</p>
        </div>
      )}
    </div>
  );
}

/** The stored PNG preview of a drawing note, shown when the note is closed. */
export function DrawingPreview({
  attachmentApi = attachmentRpc,
  attachmentId,
  label,
}: {
  attachmentApi?: AttachmentApi;
  attachmentId: string | null;
  label: string;
}) {
  const [item, setItem] = React.useState<AttachmentDto | undefined>(undefined);
  React.useEffect(() => {
    if (!attachmentId) {
      setItem(undefined);
      return;
    }
    let current = true;
    void attachmentApi
      .resolve({ ids: [attachmentId] })
      .then((result) => {
        if (current) setItem(result.ok ? result.attachments[0] : undefined);
      })
      .catch(() => {
        if (current) setItem(undefined);
      });
    return () => {
      current = false;
    };
  }, [attachmentApi, attachmentId]);
  const { url } = useAttachmentUrl(attachmentApi, item);
  return url ? (
    <img src={url} alt={label} className="pointer-events-none max-h-48 max-w-full rounded-md border bg-white object-contain" />
  ) : (
    <span className="text-muted-foreground italic">{attachmentId ? "Loading the drawing…" : "Drawing (nothing drawn yet)"}</span>
  );
}
