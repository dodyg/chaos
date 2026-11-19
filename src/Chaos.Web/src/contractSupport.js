import { readFileSync } from "node:fs";

// The server's JSON shapes (tests/Chaos.Server.Tests/WebContractTests.cs keeps this file in step with the C# records).
const contract = JSON.parse(readFileSync(new URL("../contract/web-dto.json", import.meta.url), "utf8"));

const scalarCheck = {
  string: (value) => typeof value === "string",
  guid: (value) => typeof value === "string" && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value),
  date: (value) => typeof value === "string" && /^\d{4}-\d{2}-\d{2}$/.test(value),
  time: (value) => typeof value === "string" && /^\d{2}:\d{2}(:\d{2})?/.test(value),
  datetime: (value) => typeof value === "string" && !Number.isNaN(Date.parse(value)),
  boolean: (value) => typeof value === "boolean",
  number: (value) => typeof value === "number",
};

function problems(type, value, where) {
  const nullable = type.endsWith("?");
  const base = nullable ? type.slice(0, -1) : type;
  if (value === null || value === undefined) return nullable ? [] : [`${where} is missing but the server never omits it`];
  if (base in scalarCheck) return scalarCheck[base](value) ? [] : [`${where} is not a ${base}`];
  if (base.startsWith("list<")) {
    if (!Array.isArray(value)) return [`${where} is not a list`];
    return value.flatMap((item, index) => problems(base.slice(5, -1), item, `${where}[${index}]`));
  }
  return base in contract ? shapeProblems(base, value, where) : [];
}

function shapeProblems(name, value, where) {
  const shape = contract[name];
  if (!shape) return [`${where}: the server has no ${name}`];
  const found = [];
  for (const key of Object.keys(value)) if (!(key in shape)) found.push(`${where}.${key} is not a field of ${name}`);
  for (const [key, type] of Object.entries(shape)) found.push(...problems(type, value[key], `${where}.${key}`));
  return found;
}

/**
 * Fails when a test fixture does not look like what the server sends for <paramref name="name"/> (a `Web*` record):
 * an unknown field, a missing required one, or a wrong type. Fixtures built by hand stay honest this way.
 */
export function assertServerShape(name, value) {
  const found = shapeProblems(name, value, name);
  if (found.length > 0) throw new Error(`The fixture does not match the server contract:\n${found.join("\n")}`);
}

export const serverContract = contract;
