import { test } from "node:test";
import assert from "node:assert/strict";
import { handle } from "./preview/backend";

type Any = Record<string, any>;

const get = async (request: Any) => (await handle("board.get", request)) as Any;
const move = async (request: Any) =>
  (await handle("board.move", {
    operationId: crypto.randomUUID(),
    subtaskChoice: null,
    fromColumn: "NotStarted",
    beforeTaskId: null,
    afterTaskId: null,
    ...request,
  })) as Any;
const updateSettings = async (request: Any) =>
  (await handle("board.updateSettings", {
    operationId: crypto.randomUUID(),
    columnTagId: null,
    swimlaneTagId: null,
    wipLimitsJson: "{}",
    hiddenColumnsJson: "[]",
    ...request,
  })) as Any;

test("the preview board serves the sprint container with status columns and card details", async () => {
  const board = await get({ containerId: "sprint", category: null, showCancelled: false });
  assert.equal(board.ok, true);
  assert.deepEqual(
    board.columns.map((c: Any) => c.title),
    ["Not started", "In progress", "Waiting on someone", "Needs review", "Done"],
  );
  const blocklist = board.cards.find((c: Any) => c.task.id === "task-2");
  assert.equal(blocklist.columnKey, "NotStarted");
  assert.equal(blocklist.hasIssueLink, true, "the sample linked issue is marked");
  assert.equal(blocklist.subtasksTotal, 2);
  assert.equal(blocklist.subtasksDone, 1);
});

test("the preview board moves a card and the change shows on the next read", async () => {
  const result = await move({ taskId: "task-2", expectedVersion: "1", groupBy: "Status", toColumn: "InProgress" });
  assert.equal(result.ok, true);
  assert.equal(result.task.status, "InProgress");
  const board = await get({ containerId: "sprint", category: null, showCancelled: false });
  assert.equal(board.cards.find((c: Any) => c.task.id === "task-2").columnKey, "InProgress");

  const stale = await move({ taskId: "task-2", expectedVersion: "1", groupBy: "Status", toColumn: "Done" });
  assert.equal(stale.ok, false, "an old task version is refused");

  const question = await move({ taskId: "task-2", expectedVersion: "2", groupBy: "Status", toColumn: "Done" });
  assert.equal(question.ok, false);
  assert.ok(
    question.error.fields.some((f: Any) => f.fieldPath === "openSubtaskChoice"),
    "moving to Done with open subtasks asks",
  );
  const answered = await move({ taskId: "task-2", expectedVersion: "2", groupBy: "Status", toColumn: "Done", subtaskChoice: "KeepOpen" });
  assert.equal(answered.ok, true);
  assert.equal(answered.task.status, "Done");
});

test("the preview board keeps settings per container, versioned from zero", async () => {
  const saved = await updateSettings({ targetId: "sprint", expectedVersion: "0", groupBy: "Section", swimlaneBy: "Priority" });
  assert.equal(saved.ok, true);
  assert.equal(saved.settings.groupBy, "Section");
  assert.equal(saved.settings.version, "1");

  const conflict = await updateSettings({ targetId: "sprint", expectedVersion: "0", groupBy: "Status", swimlaneBy: "None" });
  assert.equal(conflict.ok, false, "version 0 no longer matches after the row exists");

  const board = await get({ containerId: "sprint", category: null, showCancelled: false });
  assert.deepEqual(
    board.columns.map((c: Any) => c.title),
    ["In review", "No section"],
    "section columns come from the container's sections",
  );
  assert.ok(board.lanes.length > 1, "priority swimlanes are served");
  const review = board.cards.find((c: Any) => c.task.id === "task-6");
  assert.equal(review.columnKey, "s-review");
  assert.equal(review.laneKey, "Normal");

  const other = await get({ containerId: "home", category: null, showCancelled: false });
  assert.equal(other.settings.groupBy, "Status", "another container keeps the defaults");
});

test("a preview move writes one history entry and bumps the cards it reranks, like the real command", async () => {
  const board = await get({ containerId: "sprint", category: null, showCancelled: false });
  const [first, second] = board.cards.map((c: Any) => c.task);
  assert.ok(first && second, "the sprint container has two cards to reorder");
  const undoBefore = ((await handle("undo.list", { limit: 100 })) as Any).entries.length;
  const result = await move({
    taskId: second.id,
    expectedVersion: second.version,
    groupBy: "Status",
    fromColumn: second.status,
    toColumn: second.status,
    beforeTaskId: first.id,
  });
  assert.equal(result.ok, true);
  assert.equal(((await handle("undo.list", { limit: 100 })) as Any).entries.length, undoBefore + 1, "one entry for the whole move");
  const after = await get({ containerId: "sprint", category: null, showCancelled: false });
  assert.notEqual(
    after.cards.find((c: Any) => c.task.id === first.id).task.version,
    first.version,
    "the card it was reranked past has a new version",
  );

  const unknown = await move({ taskId: second.id, expectedVersion: result.task.version, groupBy: "Status", toColumn: "Nowhere" });
  assert.equal(unknown.ok, false, "a column that is not a status is refused");
});
