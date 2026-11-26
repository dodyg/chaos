import React from "react";
import { proposalRpc, type ContainerResponse, type PadItemResponse, type ProposalDto } from "#neoastra";
import { Bot, Check, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";

export type ProposalApi = typeof proposalRpc;

const operationId = () => crypto.randomUUID();

/**
 * Tasks an agent suggested from the pad (PLAN3 12.6): "Suggested by Build bot", with Accept and Dismiss. Nothing is a task
 * until a person accepts; accepting makes it the way quick add would (the agent's quick-add text is parsed), in the chosen
 * list or the Inbox, and links it to the note. A list that cannot load shows nothing: suggestions are never in the way.
 */
export function PadSuggestions({
  api = proposalRpc,
  notes,
  noteLabel,
  containers,
  revision,
  onChanged,
  onError,
}: {
  api?: ProposalApi;
  /** The pad's notes, to say which note a suggestion came from. */
  notes: readonly PadItemResponse[];
  /** A short label for a note (its first line). */
  noteLabel: (note: PadItemResponse) => string;
  containers: readonly ContainerResponse[];
  /** Bumped by the app when synced data changes: the list reloads. */
  revision: number;
  onChanged: () => void;
  onError: (message: string | null) => void;
}) {
  const [items, setItems] = React.useState<readonly ProposalDto[]>([]);
  const [busy, setBusy] = React.useState<string | null>(null);
  const [lists, setLists] = React.useState<Readonly<Record<string, string>>>({});

  const load = React.useCallback(async () => {
    try {
      const result = await api.list({ padItemId: null });
      if (result.ok) setItems(result.items);
    } catch {
      // Suggestions are optional: a failure to load them leaves the pad as it was.
    }
  }, [api]);
  React.useEffect(() => {
    void load();
  }, [load, revision]);

  async function decide(item: ProposalDto, accept: boolean) {
    if (busy) return;
    setBusy(item.id);
    onError(null);
    try {
      const result = accept
        ? await api.accept({
            operationId: operationId(),
            proposalId: item.id,
            expectedVersion: item.version,
            containerId: lists[item.id] || null,
          })
        : await api.dismiss({ operationId: operationId(), proposalId: item.id, expectedVersion: item.version });
      if (!result.ok)
        throw new Error(
          result.error?.fields?.map((field) => field.message).join("; ") || result.error?.message || "Could not update the suggestion.",
        );
      await load();
      onChanged();
    } catch (e) {
      onError(e instanceof Error ? e.message : "Could not update the suggestion.");
    } finally {
      setBusy(null);
    }
  }

  if (items.length === 0) return null;
  const targets = containers.filter((list) => list.kind === "List" && !list.archived);
  return (
    <section className="grid gap-2 rounded-xl border bg-card p-4" aria-label="Suggested tasks">
      <h2 className="flex items-center gap-2 text-sm font-semibold">
        <Bot className="size-4" aria-hidden="true" />
        Suggested tasks
      </h2>
      <p className="text-xs text-muted-foreground">Nothing becomes a task until you accept it.</p>
      <ul className="grid gap-2">
        {items.map((item) => {
          const source = notes.find((note) => note.id === item.padItemId);
          return (
            <li key={item.id} className="grid gap-1 rounded-lg border p-3" aria-label={`Suggestion: ${item.title}`}>
              <div className="text-xs text-muted-foreground">
                Suggested by {item.agentName}
                {source ? ` · from “${noteLabel(source)}”` : ""}
              </div>
              <div className="font-medium" dir="auto">
                {item.title}
              </div>
              {item.notes && (
                <p className="whitespace-pre-wrap text-sm text-muted-foreground" dir="auto">
                  {item.notes}
                </p>
              )}
              {item.quickAddText && <p className="text-xs text-muted-foreground">Quick add: {item.quickAddText}</p>}
              <div className="flex flex-wrap items-center gap-2">
                {targets.length > 0 && (
                  <NativeSelect
                    aria-label={`List for ${item.title}`}
                    value={lists[item.id] ?? ""}
                    onChange={(event) => setLists((current) => ({ ...current, [item.id]: event.target.value }))}
                  >
                    <NativeSelectOption value="">Inbox</NativeSelectOption>
                    {targets.map((list) => (
                      <NativeSelectOption key={list.id} value={list.id}>
                        {list.name}
                      </NativeSelectOption>
                    ))}
                  </NativeSelect>
                )}
                <Button type="button" size="sm" disabled={busy !== null} onClick={() => void decide(item, true)}>
                  <Check />
                  Accept
                </Button>
                <Button type="button" size="sm" variant="outline" disabled={busy !== null} onClick={() => void decide(item, false)}>
                  <X />
                  Dismiss
                </Button>
              </div>
            </li>
          );
        })}
      </ul>
    </section>
  );
}
