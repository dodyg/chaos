import { test } from "node:test";
import assert from "node:assert/strict";
import { dropInsertionIndex } from "./dragReorder";

test("drop insertion index matches the keyboard move semantics", () => {
  // Rows: A(0) B(1) C(2) D(3)
  assert.equal(dropInsertionIndex(0, 2, true), 2, "A dropped after C ends at index 2");
  assert.equal(dropInsertionIndex(0, 2, false), 1, "A dropped before C ends at index 1");
  assert.equal(dropInsertionIndex(3, 1, false), 1, "D dropped before B ends at index 1");
  assert.equal(dropInsertionIndex(3, 1, true), 2, "D dropped after B ends at index 2");
  assert.equal(dropInsertionIndex(1, 0, true), 1, "B dropped after A stays at index 1");
  assert.equal(dropInsertionIndex(0, 3, true), 3, "A dropped after the last row moves to the end");
});
