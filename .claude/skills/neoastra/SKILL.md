---
name: neoastra
description: How to build .NET desktop apps with NeoAstra, the framework that hosts a web UI (React, Vue or plain TypeScript) in the platform webview (WebView2, WKWebView, WebKitGTK) and connects it to C# through source-generated typed RPC. Use when a project references the `NeoAstra` NuGet package or has a `neoastra.json`; when adding or changing an RPC service, method, DTO or push event (`[NeoRpcService]`, `[NeoRpcMethod]`, `[NeoRpcEvent]`); when calling the backend from the frontend through the generated `#neoastra` module or `@neoastra/client`; when using `NeoApp.Run`, windows, tray, menus, global shortcuts, launch events or webview permissions; when the frontend build, `obj/neoastra`, the contract hash, CSP or `dotnet neoastra` tooling misbehaves; or when someone asks "how does the desktop app talk to the UI".
---

# NeoAstra

NeoAstra runs a .NET app with a web UI on the platform's own webview: WebView2 on Windows, WKWebView on
macOS and iOS, WebKitGTK on Linux, the Android System WebView on Android. It bundles no browser engine and
runs no localhost server. The built frontend is served from the custom origin `app://neoastra`, and the
page talks to C# only through **typed RPC that a source generator emits from C# attributes**.

**The package's own docs are the authority.** They ship inside the NuGet packages:
`~/.nuget/packages/neoastra/<version>/docs/` and `~/.nuget/packages/neoastra.core/<version>/docs/`
(on Windows, `%USERPROFILE%\.nuget\packages\...`). Read the docs for the version the project pins, not an
older one that is also in the cache. [references/package-docs.md](references/package-docs.md) says which
file answers which question.

## Orient yourself in a NeoAstra project

| Piece | Where to look | Notes |
| --- | --- | --- |
| Version | `Directory.Packages.props` or the `.csproj`; `.config/dotnet-tools.json` | The `NeoAstra` package, `NeoAstra.Tool` and `NeoAstra.Templates` must be on the **same** version |
| Host entry | `Program.cs` | `NeoApp.Run(args, app => …)` (desktop) or `NeoApp.AttachAsync` (iOS/Android preview) |
| RPC services | classes marked `[NeoRpcService]` | Search for `NeoRpcService(` to list them |
| JSON metadata | the class named in `[assembly: NeoRpcJsonContext(typeof(…))]` | Every DTO needs a `[JsonSerializable]` line there |
| Project config | `neoastra.json` beside the `.csproj` (optional) | Dev server URL, build command, asset origin, **CSP** |
| Generated contract | `obj/neoastra/neoastra.ts`, `neoastra.manifest.json`, `client/` | Build output: never edit or commit |
| Frontend | `frontend/` | Imports the generated module as `#neoastra` (Vite alias in the templates) |

Run `dotnet neoastra inspect` to see the resolved configuration and `dotnet neoastra doctor` to check the toolchain.

## Starting a project

```sh
dotnet new install NeoAstra.Templates::<VERSION>
dotnet new neoastra-react -n DesktopApp --neoAstraVersion <VERSION>   # or neoastra (vanilla TS), neoastra-vue
cd DesktopApp
dotnet new tool-manifest
dotnet tool install NeoAstra.Tool --version <VERSION>
dotnet build
dotnet run --no-build      # or: dotnet neoastra dev
```

Commit the generated source, `frontend/package-lock.json` and the tool manifest. Do not commit
`frontend/dist` or anything under `obj/`.

## The host

```csharp
return NeoApp.Run(args, app =>
{
    app.Title = "My App";
    app.AllowPermissions(NeoPermissionKind.Microphone);           // only if the page needs it
    app.UsePlugins((application, plugins) => { /* desktop services, see below */ });
    app.ConfigureMainWindow((application, window) =>
    {
        window.CloseRequested += OnCloseRequestedAsync;
        application.LaunchReceived += OnLaunchAsync;
    });
    app.UseRpc(rpc => rpc.AddDocumentsService(new DocumentsService(/* deps */)));
});
```

- Build your object graph (storage, services) **before** `NeoApp.Run` and pass instances into the
  generated `rpc.AddXxx(...)` calls. Keep construction in one composition root, not inside services.
- `ConfigureMainWindow` runs once, on the UI thread, before navigation. It must not block or be
  `async void`; put async work in the typed handlers it registers.
- For several windows, custom application options, or single-instance handling before a window exists,
  use explicit `NeoApplication` composition instead of `NeoApp` (see the lifecycle doc). The simple
  `NeoApp` host does not let you acquire `NeoSingleInstance` before the window; apps that must decide
  earlier (for example to lock a data folder) often run their own mutex + named pipe before `NeoApp.Run`.

## Adding an RPC method

1. **DTOs.** Use `sealed record`s: one request and one response per method. Declare a request record even
   when it has no fields, so it can gain fields later without breaking the shape.
2. **Wire rules.** The generator rejects or mangles anything else:
   - `long`/`ulong` are refused unless a property carries `[NeoRpcInt64(NeoRpcInt64Policy.String)]` and
     `[JsonConverter(typeof(NeoRpcInt64JsonConverter))]` (TypeScript gets `string`). There is no `bigint`
     mode. A simpler convention that works well: send ids and versions as `string` and parse them at the
     edge, and clamp counts to `int`.
   - Enums need `[JsonSourceGenerationOptions(UseStringEnumConverter = true)]` on the JSON context, or send
     a `string` and parse it in C#.
   - `Guid` is the hyphenated string, `DateTime`/`DateTimeOffset` are ISO 8601, and `TimeSpan` uses the
     invariant `c` format. `byte[]` is base64, so keep it small.
   - No non-string dictionary keys, cycles, indexers or duplicate JSON names. Use `T?` for optional
     values; TypeScript gets `T | null`.
3. **The service**:
   ```csharp
   [NeoRpcService("documents", Version = 1)]
   public sealed class DocumentsService(DocumentStore store)
   {
       [NeoRpcMethod("open")]
       public async ValueTask<OpenDocumentResponse> OpenAsync(OpenDocumentRequest request, CancellationToken ct)
       {
           try { return new(true, Map(await store.OpenAsync(request.Id, ct)), null); }
           catch (DomainException e) { return new(false, null, new ErrorDto(e.Kind, e.Message)); }
       }
   }
   ```
   - The wire command is `service.method` (camelCase by convention). The generated frontend object is
     `documentsRpc` (service name + `Rpc`), with one function per method.
   - The parameters are the request, optionally `NeoRpcContext context` (caller view and session), then
     `CancellationToken`. Return `ValueTask<T>` (or `Task<T>`).
   - **Errors.** For expected failures (validation, not found, conflict), return them as data
     (`Ok` + `Error`) or throw `NeoRpcException` with a safe code and message. Any other exception
     reaches the page as a redacted error: release builds hide types, stack traces and messages. Put the
     exception-to-response mapping in one shared helper, not in every method.
   - Map domain objects to DTOs inside the service, and keep domain types out of the contract.
   - Optional attribute properties: `Permission = "documents:open"` for capability-gated methods
     (see Security), `Dispatch`, `MaximumConcurrency = N`, `TimeoutMilliseconds = N`.
   - `NeoRpcException(code, message)` takes a lowercase, colon-separated code (e.g. `documents:not_found`).
4. **Register every DTO** (requests, responses and nested types) with `[JsonSerializable(typeof(…))]` on
   the context that `[assembly: NeoRpcJsonContext]` names. A missing one is compile error `NEORPC009`.
5. **Register the service.** In `UseRpc`, call `rpc.AddDocumentsService(new DocumentsService(...))`.
   The generator creates this extension. Factory overloads support the `ApplicationSingleton`, `PerView`,
   `PerDocumentSession` and `PerInvocation` lifetimes.
6. **Build** the C# project. This regenerates `obj/neoastra/neoastra.ts`; the frontend only sees new
   methods after a build.
7. **Tests.** Most service logic can be tested by constructing the service class directly. The webview
   is not needed. On the frontend, `@neoastra/client/testing` provides `createMockRpcHarness` for
   transport-level tests.

### Threading

Methods run on the **thread pool** by default (`NeoRpcDispatchMode.Background`). They run concurrently,
and their results can come back in any order. State they share must be thread-safe. Code that touches
`NeoWindow`, `NeoApplication` or other UI objects must either declare
`[NeoRpcMethod("name", Dispatch = NeoRpcDispatchMode.UiThread)]` or post to `application.Dispatcher.Post(...)`. Long work never belongs on the UI thread.

### Streams and large data

- **Channels** (a method that returns an async sequence) stream items with back-pressure. The frontend
  reads them with `for await`.
- **Resources** are opaque handles owned by the session, for large or binary data.
- A single huge result can fail with `payload_too_large`. Use a channel, chunks or a resource instead.

## Push events (backend → page)

```csharp
public sealed record DocumentChanged(string Id);              // + [JsonSerializable]
public sealed class DocumentEvents                            // declaration only
{
    [NeoRpcEvent("documents.changed")]
    public DocumentChanged Changed => throw new NotSupportedException("Declaration only.");
}

// in UseRpc:
var changed = rpc.AddDocumentEventsChangedEvent();            // Add{Class}{Property}Event
await changed.PublishAsync(new DocumentChanged(id));
```

The frontend calls `const unsubscribe = await documentsRpc.onChanged(payload => …)`. When the event name's
prefix matches a service, the subscription appears on that service's object.

Events are ordered per subscription, but under overflow they can be dropped or coalesced (`DropOldest`,
`DropNewest`, `Coalesce`, `Fail`). **They are not a lossless log.** A robust pattern is a small "something
changed, here is the new head" event that the page answers by pulling the changes with a normal RPC call.
Coalesce bursts on the backend, for example to one event per ~100 ms.

## Calling RPC from the frontend

```tsx
import { documentsRpc, type DocumentDto } from "#neoastra";

type Api = Pick<typeof documentsRpc, "open" | "save">;
export function Editor({ api = documentsRpc }: { api?: Api }) { … }
```

- Always use the generated functions. They send the contract hash, so do not hand-write `invoke("x.y", …)`.
- Taking the generated object as a defaulted prop or parameter lets tests pass a fake without a host.
- Transport failures reject with a `NeoRpcError` (`code`, `message`, `retryable`). Errors you return as
  data come back as normal results.
- **Never auto-retry a mutation.** A `timeout` or `connection_closed` does not prove the backend did not
  run it. Use idempotency keys or optimistic concurrency (an `expectedVersion` field) instead.
- Pass `{ signal }` to cancel and `{ timeoutMilliseconds }` to bound a call; there is no default timeout.
- Guard against stale responses: when a newer load has started, drop the older result.

### Running the UI in a plain browser

The generated module imports only `invoke`, `invokeChannel` and `subscribe` from `@neoastra/client`.
A second Vite config can alias `@neoastra/client` to a local stub. The stub answers commands from an
in-memory fake backend, so the real UI runs in a browser without the desktop host. If you do this, add a
test that every stubbed command name exists in `obj/neoastra/neoastra.manifest.json`, so a renamed method
does not silently stop working in the preview.

## Native desktop services

Desktop services (tray, menus, dialogs, clipboard, notifications, global shortcuts, opener, safe
storage) come from `NeoDesktopServices`:

```csharp
app.UsePlugins((application, plugins) =>
{
    desktop = NeoDesktopServices.CreateSystem(appId, "My App", version, dataDirectory,
        urlScopes, fileRoots, fileRoots, fileIntents, application.Dispatcher);
    plugins.AddNeoAstraDesktop(desktop);
});
```

- `CreateSystem` refuses empty URL scopes, file roots and file intents. Make them as narrow as the app
  allows.
- **Backend-only vs. page-reachable.** Calling `desktop.Tray`, `desktop.Menus` or `desktop.GlobalShortcuts`
  from C# does not expose them to the page. The page reaches desktop commands only after you call
  `rpc.AddNeoAstraDesktopHandlers(desktop, …)` and grant the permissions with `app.UseCapabilities(json)`.
  If the page needs only one thing, such as opening a link, prefer a narrow app RPC method.
- **Tray and menus.** Register `desktop.Menus.Commands.Register("app.open", handler)`, then call
  `desktop.Tray.SetAsync(new NeoTrayItemOptions { Id, ToolTip, IconPath, Menu = [NeoMenuItem.Command(...), NeoMenuItem.Separator(...)] })`.
  Check `desktop.Tray.Support.IsSupported` first and tolerate failure.
- **Global shortcuts.** `desktop.GlobalShortcuts.RegisterAsync(id, "Ctrl+Alt+N", ct)` returns a
  `NeoDesktopStatus` (`Success`, `Conflict`, `Unsupported`, …). An `ArgumentException` means a reserved
  or invalid key. Show the status to the user instead of throwing.
- **Window close.** A `CloseRequested` handler can `request.Cancel()` only when `request.CanCancel`.
  Check `request.Reason` (`User`, `ApplicationQuit`, `SessionEnd`, `Automated`, …). Close-to-tray should
  cancel only a `User` close. Windows session end cannot wait for async work, so save incrementally.
  Quit with `application.RequestQuitAsync()`.
- **Bring the window forward:** inside `Dispatcher.Post`, call `window.Show()`, then `Restore()` if it is
  minimized, then `Activate()`. Windows may still refuse the foreground.
- **Launch events.** `application.LaunchReceived` delivers the initial activation, later activations and
  opened files and URLs, one at a time, after the app is ready. Do one-time setup such as the tray on
  `NeoLaunchReason.Initial`.
- **Browser permissions.** The main view denies every request (`getUserMedia` fails with
  `NotAllowedError`) until `app.AllowPermissions(...)` names the kind, or `app.OnPermissionRequest(...)`
  decides per request. The OS privacy settings still apply on top.
- **File inputs** in `NeoApp` are canceled unless you call `app.UseSystemFileChooser()` or handle
  `OnFileChooser`.

## Security model

- **Trusted local default.** Registered RPC methods **without** a `Permission` are callable by the app's
  own local view, with no capability file needed. A capability manifest does **not** hide permissionless
  methods registered on the same host.
- **Opt-in boundary.** Set `Permission` on a method, declare the permissions in a catalog, and grant
  them in a capability document (`neoastra-capabilities-v1.schema.json`) by view label and platform.
  If the authorization service is missing, permissioned methods are denied.
- **Untrusted content** (remote pages, user previews) gets a separate view with **no bridge**, or a
  separate RPC host that registers only what that view needs. Never bind it to the main registry.
- Capabilities do not check ownership. Validate in the backend, for every call, that the caller may
  touch the object it names.

## Frontend build, dev loop and assets

- **`dotnet build` does the whole build:** compile C# → generate `obj/neoastra/neoastra.ts` and the manifest
  → stage `@neoastra/client` into `obj/neoastra/client` → locked `npm ci` (only when the manifest or
  lockfile changed) → the frontend build command → validate and copy the hashed assets to
  `bin/.../assets` with `neoastra-assets.json`. A frontend build error fails the .NET build.
- **`@neoastra/client` is a dependency** with the value `file:../obj/neoastra/client`. On Windows without
  symlink rights, set `install-links=true` in `frontend/.npmrc`. A missing lockfile is an error, never an
  unlocked install. To create the first lockfile, run
  `dotnet build -t:NeoAstraStageFrontendClient` and then `npm install` once.
- **`dotnet neoastra dev`** runs the contract build, starts the dev server, waits for `devUrl`, then
  runs `dotnet watch`. The URL must be `127.0.0.1` or `::1`; **`localhost` is rejected**. Keep Vite's
  `strictPort` on.
- **Vite needs `base: "./"`**, so assets resolve against the custom scheme. Service workers are not
  supported.
- **The CSP comes from `neoastra.json` → `assets.csp`**, not from `index.html`. New resource kinds such as
  `blob:` media, frames or workers need a change there. Permissive values and remote origins fail closed.
  With `connect-src 'self'`, the page cannot reach the internet, so network calls belong in C#.
- On Windows, the assets publish to `assets\`, which is the same folder as `Assets\` on a
  case-insensitive filesystem. Keep native icons and images somewhere else.
- **Publish:** `dotnet publish -c Release -r <RID> --self-contained`. CI can verify the contract with
  `dotnet neoastra contract check --typescript obj/neoastra/neoastra.ts --manifest obj/neoastra/neoastra.manifest.json`.

## Troubleshooting

| Symptom | Cause / fix |
| --- | --- |
| `NEORPC009` | A DTO is missing `[JsonSerializable]` on the RPC JSON context |
| Other `NEORPC###` diagnostics | Duplicate or invalid names, an unsupported type (`long` without the converter, non-string dictionary keys, cycles) or a name collision in the generated TypeScript. The message names the member |
| A new method is missing in TypeScript | Build the C# project; only the C# build regenerates the TS module |
| `protocol_mismatch` | The page and the host were built from different contracts. Rebuild, then restart or reload |
| `connection_closed` / `rpc_session_closed` | The host closed the session. The page must reload; calls are never replayed |
| `too_many_requests` | A per-command, per-session or channel-buffer limit was hit; check `NeoRpcOptions` |
| `getUserMedia` → `NotAllowedError` | The permission kind is not allowed in the host, or an OS privacy setting blocks it |
| Build cannot overwrite files in `bin/` | The app is still running; WebView2 locks `<exe>.WebView2\`. Quit it first |
| `npm ci` fails on `@neoastra/client` | The staged client is stale or missing. Run `dotnet build` (or `-t:NeoAstraStageFrontendClient`), and never install unlocked |
| The dev server is not picked up | The `devUrl` in `neoastra.json` must match the Vite host and port exactly, on `127.0.0.1` |
