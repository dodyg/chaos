const shell = "/app/index.html";
const files = new Set(FILES);

self.addEventListener("install", event => {
  event.waitUntil((async () => {
    const cache = await caches.open(CACHE);
    await cache.addAll(FILES.map(url => new Request(url, { cache: "reload" })));
  })());
});

self.addEventListener("activate", event => {
  event.waitUntil((async () => {
    const keys = await caches.keys();
    await Promise.all(keys.filter(key => key.startsWith("chaos-shell-") && key !== CACHE).map(key => caches.delete(key)));
    await self.clients.claim();
  })());
});

self.addEventListener("message", event => {
  if (event.data === "activate-update") {
    event.waitUntil(self.skipWaiting());
  }
});

self.addEventListener("fetch", event => {
  if (event.request.method !== "GET") return;
  const url = new URL(event.request.url);
  if (url.origin !== self.location.origin) return;
  const key = event.request.mode === "navigate" && (url.pathname === "/app/" || url.pathname === shell)
    ? shell : url.pathname;
  if (!files.has(key)) return;

  // The worker serves only public build files. Auth and task requests always go to the server.
  event.respondWith((async () => {
    const cache = await caches.open(CACHE);
    return (await cache.match(key)) ?? fetch(event.request);
  })());
});
