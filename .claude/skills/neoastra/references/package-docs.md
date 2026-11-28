# NeoAstra package docs: where to look

The NuGet packages ship their documentation. Read the version the project pins (check
`Directory.Packages.props` or the `.csproj`); older versions may also be in the cache. `<v>` below is that version.

Root: `%USERPROFILE%\.nuget\packages\` (Git Bash: `~/.nuget/packages/`).

| Question | File (under the root) | Useful sections |
| --- | --- | --- |
| RPC attributes, generator diagnostics, limits, threading, error codes, wire formats, frontend client | `neoastra/<v>/docs/rpc-and-bindings.md` | Backend contract; Default limits; Dispatch, errors, and streams; Frontend; Wire formats |
| `NeoApp.Run`, `ConfigureMainWindow`, permissions, window close/quit, launch events, single instance, Generic Host | `neoastra/<v>/docs/application-lifecycle-and-hosting.md` | Lifecycle policy in the simple host; Browser permissions; Managed ordering; Launch events and single instance |
| Capability documents, permissioned methods, restricted views, external links, other-origin frames, custom schemes | `neoastra/<v>/docs/capabilities-and-security.md` | Permission catalog; Frames and content of other origins; Links that open outside the web view |
| Desktop services: tray, menus, dialogs, clipboard, notifications, global shortcuts, opener, safe storage | `neoastra/<v>/docs/desktop-services.md` | Composition and lifecycle; Platform support; Persistence and secrets; Testing |
| `neoastra.json`, `dotnet neoastra dev/inspect/doctor`, MSBuild properties, publish, prebuilt assets | `neoastra/<v>/docs/frontend-tooling-and-assets.md` | Configuration and inspection; Development; Build, run, publish, and MSBuild properties |
| Installers, signing, authenticated updates | `neoastra/<v>/docs/delivery-and-updates.md` | |
| Transport handshake, message size, JSON depth, CSP checks | `neoastra.core/<v>/docs/frontend-transport.md` | Lifecycle and limits; Package and CSP checks |
| Platform differences, permission prompts and capture, browser data folders, custom-scheme limits | `neoastra.core/<v>/docs/known-limitations.md` | Permission requests and capture; Browser data and user-data roots; Custom schemes |
| Native and managed API reference (XML doc comments) | `neoastra/<v>/lib/net10.0/NeoAstra.xml`, `neoastra.core/<v>/lib/net10.0/NeoAstra.Core.xml` | grep for a type name, e.g. `NeoTrayItemOptions` |
| JSON schemas (`neoastra.json`, capabilities) | `neoastra/<v>/schemas/*.schema.json` | |
| Desktop plugin commands and the permission each needs | `neoastra/<v>/plugins/neoastra.desktop.essentials/neoastra.plugin.json` | |

Search tip: `grep -rn "<Term>" ~/.nuget/packages/neoastra/<v>/docs ~/.nuget/packages/neoastra.core/<v>/docs`.

## Facts from those docs that are easy to miss

- Registered RPC methods without a `Permission` can be called by the app's own local view. A capability
  manifest does **not** hide them. Restrict a view by giving it its own RPC host or by gating every method
  it can reach with a permission.
- The default limits are set for a trusted desktop app:
  - no request rate limit;
  - a 30 s invocation timeout;
  - 1,024 concurrent calls per session;
  - an inbound JSON depth of 32.

  `MaximumResultBytes` is unbounded, but the transport message size still applies. All of these are set
  through `NeoRpcOptions` before the host is built.
- Error codes are lowercase, for example `invalid_request`, `serialization_failed`, `payload_too_large`,
  `too_many_requests`, `timeout`, `operation_canceled`, `connection_closed` and `protocol_mismatch`.
  Release builds redact exception types, stack traces and messages. `IncludeDevelopmentErrorDetails`
  is a development-only switch.
- The request-ID replay window is bounded, so delivery is **not** exactly-once. Mutations that must not
  run twice need their own idempotency key or version check.
- After the host closes a session (`rpc_session_closed`), the page must reload or navigate to get a new
  one. Calls are never replayed.
- On Linux (WebKitGTK), RPC cannot authenticate the source origin. Views that enable RPC must hold
  controlled local content only.
- `NeoPermissionDecision.Default` hands a permission request to the engine's own prompt. Use
  `AllowPermissions` or `OnPermissionRequest` to keep the decision in code.
- `dotnet neoastra dev` rejects `localhost`. Use `127.0.0.1`.
