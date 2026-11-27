import assert from "node:assert/strict";
import test from "node:test";
import { nonWorkingDays } from "./workWeek";

test("a Sunday-start five-day week rests on Friday and Saturday", () => {
  assert.deepEqual(nonWorkingDays({ start: 0, days: 5 }), [5, 6]);
});

test("a Monday-start five-day week rests on the weekend", () => {
  assert.deepEqual(nonWorkingDays({ start: 1, days: 5 }), [6, 0]);
});

test("a seven-day week rests on no day", () => {
  assert.deepEqual(nonWorkingDays({ start: 3, days: 7 }), []);
});
