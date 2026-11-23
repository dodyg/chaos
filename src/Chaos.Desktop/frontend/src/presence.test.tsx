import test from "node:test";
import assert from "node:assert/strict";
import { presenceText } from "./presence";

const member = (displayName: string, editing: boolean) => ({ memberId: displayName, displayName, editing });

test("presence names who is editing and who is viewing", () => {
  assert.equal(presenceText([]), "");
  assert.equal(presenceText([member("Omar", true)]), "Omar is editing");
  assert.equal(presenceText([member("Omar", false), member("Sara", false)]), "Omar and Sara are viewing");
  assert.equal(presenceText([member("Omar", true), member("Sara", false)]), "Omar is editing; Sara is viewing");
  assert.equal(presenceText([member("A", false), member("B", false), member("C", false)]), "A, B and C are viewing");
});
