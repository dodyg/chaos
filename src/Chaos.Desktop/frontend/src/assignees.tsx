import React from "react";
import { peopleRpc, taskRpc, type TaskResponse } from "#neoastra";

type Chip = { id: string; name: string; initials: string; colorIndex: number; completed: boolean };
export const avatarClass = (index: number) =>
  [
    "bg-blue-100 text-blue-900",
    "bg-emerald-100 text-emerald-900",
    "bg-amber-100 text-amber-900",
    "bg-rose-100 text-rose-900",
    "bg-violet-100 text-violet-900",
    "bg-cyan-100 text-cyan-900",
    "bg-lime-100 text-lime-900",
    "bg-orange-100 text-orange-900",
  ][index % 8] ?? "bg-muted";

/** One batched RPC for visible tasks, followed by one directory read per space. */
export function useAssigneeChips(
  tasks: readonly TaskResponse[],
  revision: number,
  api: Pick<typeof taskRpc, "assignmentSummaries"> = taskRpc,
  directoryApi: Pick<typeof peopleRpc, "list"> = peopleRpc,
) {
  const [chips, setChips] = React.useState<Record<string, readonly Chip[]>>({});
  const [error, setError] = React.useState(false);
  const key = JSON.stringify([...new Set(tasks.map((task) => `${task.id}|${task.version}`))].sort());
  React.useEffect(() => {
    let active = true;
    const ids = (JSON.parse(key) as string[]).map((entry) => entry.split("|")[0]);
    if (!ids.length) {
      setChips({});
      setError(false);
      return () => {
        active = false;
      };
    }
    if (typeof api.assignmentSummaries !== "function") {
      setChips({});
      setError(true);
      return () => {
        active = false;
      };
    }
    const chunks: string[][] = [];
    for (let offset = 0; offset < ids.length; offset += 500) chunks.push(ids.slice(offset, offset + 500));
    void Promise.all(chunks.map((taskIds) => api.assignmentSummaries({ taskIds })))
      .then(async (results) => {
        if (results.some((result) => !result.ok)) throw new Error("Could not load assignees.");
        const summaries = results.flatMap((result) => result.tasks);
        const spaces = [...new Set(summaries.map((summary) => summary.spaceId))];
        const directories = await Promise.all(spaces.map((spaceId) => directoryApi.list({ spaceId })));
        if (directories.some((result) => !result.ok)) throw new Error("Could not load people.");
        const bySpace = new Map(spaces.map((space, index) => [space, directories[index].people]));
        const next: Record<string, Chip[]> = {};
        for (const summary of summaries) {
          const people = bySpace.get(summary.spaceId) ?? [];
          next[summary.taskId] = summary.assignees.map((assignment) => {
            const person = people.find((entry) => entry.actorId === `person:${assignment.memberId}`);
            return {
              id: assignment.memberId,
              name: person?.displayName ?? "Unknown member",
              initials: person?.initials ?? "?",
              colorIndex: person?.colorIndex ?? 0,
              completed: !!assignment.completedAtUtc,
            };
          });
        }
        if (active) {
          setChips(next);
          setError(false);
        }
      })
      .catch(() => {
        if (active) {
          setChips({});
          setError(true);
        }
      });
    return () => {
      active = false;
    };
  }, [api, directoryApi, key, revision]);
  return { chips, error };
}

export function AssigneeChips({ people }: { people: readonly Chip[] | undefined }) {
  if (!people?.length) return null;
  return (
    <span className="inline-flex flex-wrap items-center gap-1" aria-label="Assignees">
      {people.map((person) => (
        <span
          key={person.id}
          className="inline-flex items-center gap-1 rounded-full border px-1.5 py-0.5"
          title={person.completed ? `${person.name} completed` : person.name}
        >
          <span aria-hidden="true" className={`grid size-4 place-items-center rounded-full text-[9px] ${avatarClass(person.colorIndex)}`}>
            {person.initials}
          </span>
          {person.name}
          {person.completed && <span aria-label="completed">✓</span>}
        </span>
      ))}
    </span>
  );
}
