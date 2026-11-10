/** Prevents an older query from replacing state after a newer query or navigation. */
export function createLatestRequest() {
  let generation = 0;
  return {
    begin() { const request = ++generation; return () => request === generation; },
    invalidate() { generation++; },
  };
}
