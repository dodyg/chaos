import { useCallback, useEffect, useState } from "react";

const api = "/v1/web";
const deviceHeader = "Chaos-Web-Device";
const signedInChannel = typeof BroadcastChannel === "function" ? new BroadcastChannel("chaos-web-session") : null;
let database;

class ConnectionError extends Error {}

class ApiError extends Error {
  constructor(status, message) {
    super(message);
    this.status = status;
  }
}

function openDatabase() {
  database ??= new Promise((resolve, reject) => {
    const request = indexedDB.open("chaos-mobile", 1);
    request.onupgradeneeded = () => request.result.createObjectStore("offline");
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error);
    request.onblocked = () => reject(new Error("Offline storage is busy."));
  }).catch(error => {
    database = undefined;
    throw error;
  });
  return database;
}

async function cacheTransaction(update) {
  const db = await openDatabase();
  return new Promise((resolve, reject) => {
    const transaction = db.transaction("offline", update ? "readwrite" : "readonly");
    const store = transaction.objectStore("offline");
    const request = store.get("snapshot");
    let result;
    request.onsuccess = () => {
      const previous = request.result ?? {};
      result = update ? update(previous) : previous;
      if (update && result !== previous) store.put(result, "snapshot");
    };
    transaction.oncomplete = () => resolve(result);
    transaction.onabort = () => reject(transaction.error);
  });
}

async function bindOfflineSession(session) {
  const { deviceId, expiresAt } = session;
  let changed = false;
  await cacheTransaction(previous => {
    changed = previous.deviceId !== deviceId;
    if (!changed && previous.expiresAt === expiresAt) return previous;
    return { ...(changed ? {} : previous), deviceId, expiresAt };
  });
  if (changed) signedInChannel?.postMessage("changed");
}

export async function forgetOfflineSession() {
  let changed = false;
  try {
    await cacheTransaction(previous => {
      changed = Boolean(previous.deviceId);
      return changed ? {} : previous;
    });
  } finally {
    if (changed) signedInChannel?.postMessage("changed");
  }
}

function storeViews(deviceId, path, data, lists, appearance) {
  const updatedAt = new Date().toISOString();
  return cacheTransaction(previous => {
    if (previous.deviceId !== deviceId) return previous;
    const snapshot = { ...previous, lists: { data: lists, updatedAt }, appearance };
    delete snapshot.today;
    if (path === "/today") snapshot.myDay = { data, updatedAt };
    if (path.startsWith("/lists/")) snapshot.lastList = { path, data, updatedAt };
    return snapshot;
  });
}

async function readOffline(path) {
  const snapshot = await cacheTransaction();
  const marker = document.cookie.split(";").map(cookie => cookie.trim())
    .find(cookie => cookie.startsWith("chaos.web-offline="))?.slice("chaos.web-offline=".length);
  if (!snapshot.deviceId || snapshot.deviceId !== marker || !(Date.parse(snapshot.expiresAt) > Date.now())) {
    if (snapshot.deviceId) await forgetOfflineSession();
    return { data: null, lists: [] };
  }
  const { myDay, lists, lastList } = snapshot;
  let record;
  if (path === "/today") record = myDay;
  else if (path === "/lists") record = lists;
  else if (path === lastList?.path) record = lastList;
  else if (path.startsWith("/tasks/")) {
    const id = path.slice("/tasks/".length);
    const source = [myDay, lastList]
      .filter(Boolean)
      .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt))
      .find(entry => entry.data.tasks.some(task => task.id === id));
    if (source) record = { ...source, data: source.data.tasks.find(task => task.id === id) };
  }
  return { data: record?.data ?? null, updatedAt: record?.updatedAt, lists: lists?.data ?? [], appearance: snapshot.appearance ?? null };
}

async function request(path, { signal, session, method = "GET", body } = {}) {
  const attempt = new AbortController();
  const abort = () => attempt.abort();
  signal?.addEventListener("abort", abort, { once: true });
  if (signal?.aborted) abort();
  const timeout = setTimeout(abort, 8000);
  try {
    const headers = {};
    if (session) headers[deviceHeader] = session.deviceId;
    if (method === "POST") headers[session.antiforgeryHeader] = session.antiforgeryToken;
    if (body) headers["Content-Type"] = "application/json";
    let response;
    try {
      response = await fetch(api + path, {
        method, headers, signal: attempt.signal, cache: "no-store",
        body: body ? JSON.stringify(body) : undefined,
      });
    } catch (cause) {
      throw new ConnectionError("Could not reach Chaos.", { cause });
    }

    if ([502, 503, 504].includes(response.status)) throw new ConnectionError("Chaos is temporarily unavailable.");
    if (!response.ok) {
      const problem = await response.json().catch(() => ({}));
      throw new ApiError(response.status, problem.detail ?? ({
        401: "Sign in to continue.",
        404: "This item is no longer available.",
        429: "Too many requests. Try again in a minute.",
      })[response.status] ?? "Chaos could not complete the request.");
    }
    return response.status === 204 ? null : await response.json();
  } finally {
    clearTimeout(timeout);
    signal?.removeEventListener("abort", abort);
  }
}

const emptyView = { mode: "loading", data: null, lists: [], session: null };

export function useMobileData(path) {
  const [state, setState] = useState(emptyView);
  const [revision, setRevision] = useState(0);
  const [connected, setConnected] = useState(() => navigator.onLine);
  const refresh = useCallback(() => setRevision(value => value + 1), []);

  useEffect(() => {
    const networkChanged = () => {
      setConnected(navigator.onLine);
      refresh();
    };
    const checkIfVisible = () => {
      if (document.visibilityState === "visible") refresh();
    };
    const sessionChanged = () => {
      setState(emptyView);
      refresh();
    };
    addEventListener("online", networkChanged);
    addEventListener("offline", networkChanged);
    addEventListener("pageshow", checkIfVisible);
    document.addEventListener("visibilitychange", checkIfVisible);
    signedInChannel?.addEventListener("message", sessionChanged);
    return () => {
      removeEventListener("online", networkChanged);
      removeEventListener("offline", networkChanged);
      removeEventListener("pageshow", checkIfVisible);
      document.removeEventListener("visibilitychange", checkIfVisible);
      signedInChannel?.removeEventListener("message", sessionChanged);
    };
  }, [refresh]);

  useEffect(() => {
    if (state.mode !== "ready" && state.mode !== "offline") return;
    const poll = setInterval(() => {
      if (navigator.onLine && document.visibilityState === "visible") refresh();
    }, 10000);
    return () => clearInterval(poll);
  }, [state.mode, refresh]);

  useEffect(() => {
    const deviceId = state.session?.deviceId;
    if (state.mode !== "ready" || !connected || !deviceId || typeof EventSource !== "function") return;
    const events = new EventSource(`${api}/events?device=${encodeURIComponent(deviceId)}`);
    let timer;
    events.onmessage = () => {
      if (document.visibilityState !== "visible") return;
      clearTimeout(timer);
      timer = setTimeout(refresh, 300);
    };
    return () => {
      clearTimeout(timer);
      events.close();
    };
  }, [state.mode, state.session?.deviceId, connected, refresh]);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    async function load() {
      try {
        if (!navigator.onLine) throw new ConnectionError("You are offline.");
        const session = await request("/session", { signal });
        if (signal.aborted) return;
        await bindOfflineSession(session).catch(() => undefined);
        if (signal.aborted) return;
        const options = { signal, session };
        const listPicker = request("/lists", options);
        const [data, lists, appearance] = await Promise.all([path === "/lists" ? listPicker : request(path, options), listPicker, request("/appearance", options)]);
        if (signal.aborted) return;
        setState({ mode: "ready", path, data, lists, appearance, session });
        void storeViews(session.deviceId, path, data, lists, appearance).catch(() => undefined);
      } catch (error) {
        if (signal.aborted) return;
        if (error.status === 401) {
          await forgetOfflineSession().catch(() => undefined);
          if (!signal.aborted) setState({ ...emptyView, mode: "signed-out", path });
        } else if (error instanceof ConnectionError) {
          const cached = await readOffline(path).catch(() => ({ data: null, lists: [] }));
          if (!signal.aborted) setState({ ...emptyView, ...cached, mode: "offline", path });
        } else if (error.status === 409) {
          // The cookie changed between the session check and the read.
          setState(emptyView);
          refresh();
        } else {
          setState({ ...emptyView, mode: "error", path, error: error.message });
        }
      }
    }
    void load();
    return () => controller.abort();
  }, [path, refresh, revision]);

  const current = state.path === path ? state : { ...emptyView, lists: state.lists, session: state.session, appearance: state.appearance };
  return { ...current, connected, refresh };
}

export async function writeTask(session, path, body) {
  return request(path, { session, method: "POST", body });
}
