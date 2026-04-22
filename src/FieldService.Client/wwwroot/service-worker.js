// Produced by the Blazor PWA template; trimmed to show the caching strategy.
// - Precaches the app shell (Blazor runtime + framework DLLs + app assembly) from the asset manifest.
// - Network-first for /sync/* (we only care about fresh data, and we serve offline from local SQLite).
// - Cache-first for everything else (fonts, icons, JS shims).

self.importScripts("./service-worker-assets.js");
self.addEventListener("install", event => event.waitUntil(onInstall(event)));
self.addEventListener("activate", event => event.waitUntil(onActivate(event)));
self.addEventListener("fetch", event => event.respondWith(onFetch(event)));

const cacheNamePrefix = "offline-cache-";
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;

async function onInstall(event) {
    const assets = self.assetsManifest.assets.map(a => new Request(a.url, { integrity: a.hash }));
    await caches.open(cacheName).then(cache => cache.addAll(assets));
}

async function onActivate(event) {
    const keys = await caches.keys();
    await Promise.all(keys.filter(k => k.startsWith(cacheNamePrefix) && k !== cacheName).map(k => caches.delete(k)));
}

async function onFetch(event) {
    const url = new URL(event.request.url);
    if (url.pathname.startsWith("/sync/")) {
        // Never cache sync traffic; fall through to the network. When offline the fetch fails
        // and the sync service catches the exception and raises SyncState.Offline.
        return fetch(event.request);
    }
    const cached = await caches.match(event.request);
    return cached || fetch(event.request);
}
