/** One line of a comparison: kept in both, only in the older text, or only in the newer one. */
export type DiffLine = { kind: "same" | "removed" | "added"; text: string };

/** Line comparisons stay exact up to this many cells of the LCS table; beyond it a simpler pass is used. */
const maxCells = 4_000_000;

/**
 * A line-by-line comparison of two texts (version history "compare"): the longest common
 * subsequence of lines, with removals listed before additions at each change.
 */
export function lineDiff(before: string, after: string): DiffLine[] {
  const a = before.split("\n");
  const b = after.split("\n");
  // Trim the common start and end, which is most of a typical edit.
  let head = 0;
  while (head < a.length && head < b.length && a[head] === b[head]) head++;
  let tail = 0;
  while (tail < a.length - head && tail < b.length - head && a[a.length - 1 - tail] === b[b.length - 1 - tail]) tail++;
  const middleA = a.slice(head, a.length - tail);
  const middleB = b.slice(head, b.length - tail);
  const result: DiffLine[] = a.slice(0, head).map((text) => ({ kind: "same", text }));
  if ((middleA.length + 1) * (middleB.length + 1) > maxCells) {
    result.push(
      ...middleA.map((text) => ({ kind: "removed" as const, text })),
      ...middleB.map((text) => ({ kind: "added" as const, text })),
    );
  } else {
    const n = middleA.length;
    const m = middleB.length;
    const table = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
    for (let i = n - 1; i >= 0; i--) {
      for (let j = m - 1; j >= 0; j--) {
        table[i][j] = middleA[i] === middleB[j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1]);
      }
    }
    let i = 0;
    let j = 0;
    while (i < n || j < m) {
      if (i < n && j < m && middleA[i] === middleB[j]) {
        result.push({ kind: "same", text: middleA[i] });
        i++;
        j++;
      } else if (j >= m || (i < n && table[i + 1][j] >= table[i][j + 1])) {
        result.push({ kind: "removed", text: middleA[i] });
        i++;
      } else {
        result.push({ kind: "added", text: middleB[j] });
        j++;
      }
    }
  }
  result.push(...a.slice(a.length - tail).map((text) => ({ kind: "same" as const, text })));
  return result;
}

/** How many lines were added and removed. */
export function diffStats(lines: DiffLine[]): { added: number; removed: number } {
  return { added: lines.filter((line) => line.kind === "added").length, removed: lines.filter((line) => line.kind === "removed").length };
}
