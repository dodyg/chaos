import React from "react";
import { Dialog } from "radix-ui";
import { Crop, FlipHorizontal2, FlipVertical2, RotateCcw, RotateCw, X } from "lucide-react";
import { cn } from "@/lib/utils";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { notifyAttachmentsChanged, useAttachmentUrl, type AttachmentApi, type AttachmentInfo } from "./attachments";

/** A crop rectangle in pixels of the image after flipping and rotating (the order the host applies them). */
export type CropBox = { x: number; y: number; width: number; height: number };

export const maxSides = [3200, 2048, 1600, 1024, 800] as const;
const previewLimit = 520;

/** The size of an image once it is rotated clockwise by 0, 90, 180 or 270 degrees. */
export function rotatedSize(width: number, height: number, rotation: number): { width: number; height: number } {
  return rotation === 90 || rotation === 270 ? { width: height, height: width } : { width, height };
}

/** Keeps a crop inside the image and at least one pixel big. */
export function clampCrop(box: CropBox, width: number, height: number): CropBox {
  const x = Math.min(Math.max(0, Math.round(box.x)), width - 1);
  const y = Math.min(Math.max(0, Math.round(box.y)), height - 1);
  return {
    x,
    y,
    width: Math.min(Math.max(1, Math.round(box.width)), width - x),
    height: Math.min(Math.max(1, Math.round(box.height)), height - y),
  };
}

/** The crop a drag from one point to another makes, given the preview scale (points are in preview pixels). */
export function dragToCrop(
  from: { x: number; y: number },
  to: { x: number; y: number },
  scale: number,
  width: number,
  height: number,
): CropBox | null {
  const left = Math.min(from.x, to.x) / scale,
    top = Math.min(from.y, to.y) / scale;
  const wide = Math.abs(to.x - from.x) / scale,
    high = Math.abs(to.y - from.y) / scale;
  if (wide < 2 || high < 2) return null; // a click, not a drag
  return clampCrop({ x: left, y: top, width: wide, height: high }, width, height);
}

/** The size the host will produce for these settings. */
export function resultSize(
  width: number,
  height: number,
  rotation: number,
  crop: CropBox | null,
  maxSide: number | null,
): { width: number; height: number } {
  const rotated = rotatedSize(width, height, rotation);
  let w = crop?.width ?? rotated.width,
    h = crop?.height ?? rotated.height;
  if (maxSide && Math.max(w, h) > maxSide) {
    const k = maxSide / Math.max(w, h);
    w = Math.max(1, Math.round(w * k));
    h = Math.max(1, Math.round(h * k));
  }
  return { width: w, height: h };
}

/**
 * Crop, rotate, flip and shrink an attached image. The preview is the real image under a CSS
 * transform (nothing is drawn or re-encoded here); Save sends the settings to the host, which
 * replaces the attachment with the edited image. Undo brings the original back.
 */
export function ImageEditor({
  api,
  item,
  onClose,
  onSaved,
}: {
  api: AttachmentApi;
  item: AttachmentInfo;
  onClose: () => void;
  onSaved: (item: AttachmentInfo) => void;
}) {
  const width = item.width ?? 0,
    height = item.height ?? 0;
  const [rotation, setRotation] = React.useState(0);
  const [flipH, setFlipH] = React.useState(false);
  const [flipV, setFlipV] = React.useState(false);
  const [crop, setCrop] = React.useState<CropBox | null>(null);
  const [maxSide, setMaxSide] = React.useState<number | null>(null);
  const [drag, setDrag] = React.useState<{ from: { x: number; y: number }; to: { x: number; y: number } } | null>(null);
  const [error, setError] = React.useState("");
  const [saving, setSaving] = React.useState(false);
  const { url } = useAttachmentUrl(api, item);
  const rotated = rotatedSize(width, height, rotation);
  const scale = Math.min(1, previewLimit / Math.max(rotated.width, rotated.height, 1));
  const box = React.useRef<HTMLDivElement | null>(null);
  const changed =
    rotation !== 0 || flipH || flipV || crop !== null || (maxSide !== null && Math.max(rotated.width, rotated.height) > maxSide);
  const outcome = resultSize(width, height, rotation, crop, maxSide);

  const point = (event: React.PointerEvent) => {
    const rect = box.current!.getBoundingClientRect();
    return {
      x: Math.min(Math.max(0, event.clientX - rect.left), rect.width),
      y: Math.min(Math.max(0, event.clientY - rect.top), rect.height),
    };
  };
  function turn(delta: number) {
    setRotation((value) => (value + delta + 360) % 360);
    setCrop(null);
  }
  function setField(key: keyof CropBox, text: string) {
    const value = Number(text);
    if (!Number.isFinite(value)) return;
    setCrop(
      clampCrop({ ...(crop ?? { x: 0, y: 0, width: rotated.width, height: rotated.height }), [key]: value }, rotated.width, rotated.height),
    );
  }

  async function save() {
    setSaving(true);
    setError("");
    const result = await api
      .editImage({
        operationId: crypto.randomUUID(),
        attachmentId: item.id,
        expectedVersion: item.version,
        flipHorizontal: flipH,
        flipVertical: flipV,
        rotateClockwise: rotation,
        crop: crop ? { x: crop.x, y: crop.y, width: crop.width, height: crop.height } : null,
        maxSide,
      })
      .catch(() => null);
    setSaving(false);
    if (!result) {
      setError("That did not work. Try again.");
      return;
    }
    if (!result.ok || !result.attachment) {
      setError(result.error?.message ?? "The image could not be edited.");
      return;
    }
    notifyAttachmentsChanged();
    onSaved(result.attachment);
    onClose();
  }

  const live = drag ? dragToCrop(drag.from, drag.to, scale, rotated.width, rotated.height) : crop;
  return (
    <Dialog.Root
      open
      onOpenChange={(open) => {
        if (!open) onClose();
      }}
    >
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-50 bg-black/70" />
        <Dialog.Content
          aria-describedby={undefined}
          className="fixed top-1/2 left-1/2 z-50 grid max-h-[94vh] w-[min(96vw,48rem)] -translate-x-1/2 -translate-y-1/2 gap-3 overflow-y-auto rounded-lg border bg-background p-4 shadow-xl focus:outline-none"
        >
          <div className="flex items-center justify-between gap-3">
            <Dialog.Title className="truncate text-sm font-medium">Edit {item.fileName}</Dialog.Title>
            <Dialog.Close asChild>
              <button type="button" className="rounded-md p-2 text-muted-foreground hover:bg-accent" aria-label="Close editor">
                <X className="size-4" />
              </button>
            </Dialog.Close>
          </div>
          <div className="flex flex-wrap items-center gap-1" role="toolbar" aria-label="Image tools">
            <Button type="button" variant="outline" size="sm" onClick={() => turn(-90)}>
              <RotateCcw />
              Rotate left
            </Button>
            <Button type="button" variant="outline" size="sm" onClick={() => turn(90)}>
              <RotateCw />
              Rotate right
            </Button>
            <Button
              type="button"
              variant={flipH ? "secondary" : "outline"}
              size="sm"
              aria-pressed={flipH}
              onClick={() => {
                setFlipH(!flipH);
                setCrop(null);
              }}
            >
              <FlipHorizontal2 />
              Flip horizontally
            </Button>
            <Button
              type="button"
              variant={flipV ? "secondary" : "outline"}
              size="sm"
              aria-pressed={flipV}
              onClick={() => {
                setFlipV(!flipV);
                setCrop(null);
              }}
            >
              <FlipVertical2 />
              Flip vertically
            </Button>
            <label className="flex items-center gap-1.5 text-sm">
              Longest side
              <NativeSelect
                aria-label="Longest side"
                value={maxSide ?? ""}
                onChange={(event) => setMaxSide(event.target.value ? Number(event.target.value) : null)}
              >
                <NativeSelectOption value="">Original</NativeSelectOption>
                {maxSides.map((side) => (
                  <NativeSelectOption key={side} value={side}>
                    {side} px
                  </NativeSelectOption>
                ))}
              </NativeSelect>
            </label>
          </div>
          <div className="grid place-items-center rounded-md bg-muted/40 p-2">
            <div
              ref={box}
              data-slot="crop-area"
              className="relative touch-none select-none overflow-hidden cursor-crosshair"
              style={{ width: rotated.width * scale, height: rotated.height * scale }}
              onPointerDown={(event) => {
                event.currentTarget.setPointerCapture?.(event.pointerId);
                const p = point(event);
                setDrag({ from: p, to: p });
              }}
              onPointerMove={(event) => {
                if (drag) setDrag({ from: drag.from, to: point(event) });
              }}
              onPointerUp={() => {
                if (drag) {
                  const made = dragToCrop(drag.from, drag.to, scale, rotated.width, rotated.height);
                  if (made) setCrop(made);
                  setDrag(null);
                }
              }}
            >
              {url ? (
                <img
                  src={url}
                  alt=""
                  draggable={false}
                  className="pointer-events-none absolute top-1/2 left-1/2 max-w-none"
                  style={{
                    width: width * scale,
                    height: height * scale,
                    transform: `translate(-50%, -50%) rotate(${rotation}deg) scale(${flipH ? -1 : 1}, ${flipV ? -1 : 1})`,
                  }}
                />
              ) : (
                <span className="grid h-full place-items-center text-sm text-muted-foreground">Loading…</span>
              )}
              {live && (
                <div
                  data-slot="crop-box"
                  className="pointer-events-none absolute border-2 border-primary shadow-[0_0_0_9999px_rgba(0,0,0,0.45)]"
                  style={{ left: live.x * scale, top: live.y * scale, width: live.width * scale, height: live.height * scale }}
                />
              )}
            </div>
          </div>
          <fieldset className="grid grid-cols-2 gap-2 sm:grid-cols-5">
            <legend className="mb-1 flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
              <Crop className="size-3.5" />
              Crop (drag on the image, or type pixels)
            </legend>
            {(["x", "y", "width", "height"] as const).map((key) => (
              <label key={key} className="grid gap-1 text-xs">
                {key === "x" ? "Left" : key === "y" ? "Top" : key === "width" ? "Width" : "Height"}
                <Input
                  type="number"
                  inputMode="numeric"
                  min={key === "width" || key === "height" ? 1 : 0}
                  value={crop ? crop[key] : key === "width" ? rotated.width : key === "height" ? rotated.height : 0}
                  onChange={(event) => setField(key, event.target.value)}
                />
              </label>
            ))}
            <Button type="button" variant="ghost" size="sm" className="self-end" disabled={!crop} onClick={() => setCrop(null)}>
              Clear crop
            </Button>
          </fieldset>
          <p className={cn("text-xs text-muted-foreground")} role="status">
            {width}×{height} px → {outcome.width}×{outcome.height} px. Saving replaces this attachment's image; Undo brings the original
            back.
          </p>
          {error && (
            <p role="alert" className="text-sm text-destructive">
              {error}
            </p>
          )}
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="button" disabled={!changed || saving} onClick={() => void save()}>
              {saving ? "Saving…" : "Save image"}
            </Button>
          </div>
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
