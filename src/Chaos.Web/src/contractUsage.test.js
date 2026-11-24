import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { serverContract } from "./contractSupport.js";

// The variable names the PWA uses for server records, and the records they can hold. A property read on one of them that no
// such record has is a field the server no longer sends (or never did): the screen would show nothing, silently.
const holders = {
  task: ["WebTask"],
  list: ["WebList"],
  space: ["WebSpace"],
  update: ["WebUpdate"],
  person: ["WebPerson"],
  meeting: ["WebMeeting"],
  item: ["WebSearchItem", "WebPage", "WebNote", "WebList"],
  page: ["WebPage"],
  note: ["WebNote"],
  selected: ["WebPage", "WebNote"],
  parent: ["WebPage"],
  appearance: ["WebAppearance"],
};
const files = ["main.jsx", "TaskViews.jsx", "SearchScreen.jsx", "mobileData.js", "pageContent.js"];

test("every property the PWA reads from a server record exists in the server contract", () => {
  const unknown = [];
  let reads = 0;
  for (const file of files) {
    const source = readFileSync(new URL(`./${file}`, import.meta.url), "utf8");
    for (const [name, records] of Object.entries(holders)) {
      for (const match of source.matchAll(new RegExp("(?<![A-Za-z0-9_./])" + name + "[?]?[.]([a-zA-Z]+)", "g"))) {
        reads++;
        if (!records.some((record) => match[1] in serverContract[record])) unknown.push(`${file}: ${name}.${match[1]}`);
      }
    }
  }
  assert.ok(reads > 30, `only ${reads} reads were checked; the patterns no longer match the code`);
  assert.deepEqual(unknown, []);
});
