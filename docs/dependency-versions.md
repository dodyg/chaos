# Dependency versions — recorded at implementation kickoff

Recorded 2026-11-09 during PLAN.md work package 0. Update this file whenever a pinned
version changes; PLAN.md §1 pins are the authority.

## Toolchain

| Concern | Version | Evidence |
| --- | --- | --- |
| .NET SDK | 11.0.100 | Pinned exactly in `global.json` (`rollForward: disable`). Was 11.0.100-rc.3.26522.148 until package 13, when that SDK was no longer installed and the user chose the GA SDK (decisions 2026-11-10, package 13). Runtime 11.0.0 |
| C# language | 15.0 | `<LangVersion>15.0</LangVersion>` in `Directory.Build.props`; project code compiles with it |
| MSBuild test runner | Microsoft.Testing.Platform | Selected in `global.json` (`test.runner`); `dotnet test` runs in MTP mode (no VSTest) |
| Node | 26.12.1 | `node --version` on the development machine |
| npm | 11.20.0 | `npm --version` |

## NuGet packages (central in `Directory.Packages.props`)

| Package | Version | Note |
| --- | --- | --- |
| NeoAstra | 1.12.0 | Desktop framework; template/tool aligned (below) |
| Microsoft.Data.Sqlite | 10.0.14 | Bundled native binary includes FTS5 (proven by test, below) |
| TUnit | 1.81.16 | Latest stable at kickoff; queried NuGet V3 flat container on 2026-11-09 (`1.81.16` is newest stable). Meta-package; assertions included; no Test SDK/VSTest adapter/Coverlet |

## Local .NET tools (`.config/dotnet-tools.json`)

| Tool | Version |
| --- | --- |
| NeoAstra.Tool | 1.12.0 |
| slopwatch.cmd | 0.4.2 |

## Frontend (template-provided graph, locked in `frontend/package-lock.json`)

Versions resolve from the committed lockfile; exact resolved versions are recorded by
`npm ls` at CI time. Pinned toolchain: type checking by tsrs 0.23.0-ts7.1.0-beta (Rust port of the
TypeScript 7.1 checker; replaces the template's `typescript` ^5.9.3), Vite + React from
the locked graph. The generated `@neoastra/client` transport (0.1.0, staged by the C#
build under `obj/neoastra/client`) is installed as a packed copy via
`frontend/.npmrc` (`install-links=true`) because Windows symlink creation requires
privileges not present in all environments; build order and version alignment are unchanged.

For package 2 interaction tests, the lockfile adds `@testing-library/react` 16.3.3,
`@testing-library/user-event` 14.6.9, `jsdom` 31.0.0 and `@types/jsdom` 30.0.0
as development dependencies. On 2026-11-10, `npm audit` reported three high-severity
development-graph advisories involving the existing Vite, nanoid and source-map-js
versions; `npm audit --omit=dev` reported none at that point. Since the shadcn foundation made
`@tailwindcss/vite` a runtime dependency, the same Vite, nanoid and source-map-js advisories
also appear under `--omit=dev` (build tooling only; none ships in the bundle). Review and update the locked development
graph separately, including Vite compatibility, before release.

The shadcn/ui foundation (decision 2026-11-10) adds exact-pinned runtime dependencies
`tailwindcss` 4.3.5, `@tailwindcss/vite` 4.3.5, `radix-ui` 1.8.1,
`class-variance-authority` 0.7.1, `clsx` 2.1.1, `tailwind-merge` 3.7.0,
`lucide-react` 1.70.0 and `tw-animate-css` 1.4.0, plus `@types/node` 26.6.6 as a
development dependency. Component source is vendored; the `shadcn` CLI is not a dependency.

Package 4 (Markdown, diary, history, undo) adds exact-pinned runtime dependencies
`unified` 11.0.5, `remark-parse` 11.0.0, `remark-gfm` 4.0.1 (GFM parsing to mdast; rendering
is our own mdast-to-React walk, no HTML), `lowlight` 3.3.0 (locally bundled highlight.js
grammars, `common` set), `turndown` 7.2.4 and `turndown-plugin-gfm` 1.0.2 (rich-HTML paste to
Markdown), plus `@types/mdast` 4.0.4, `@types/hast` 3.0.4 and `@types/turndown` 5.0.5 as
development dependencies. `turndown` 7.2.5 was under a week old when checked and was not taken.
The larger bundle triggers Vite's chunk-size notice; it is informational.

## Templates

| Template | Version |
| --- | --- |
| NeoAstra.Templates | 1.12.0 (`dotnet new install NeoAstra.Templates::1.12.0`) |

Template output was retargeted: `net10.0` → `net11.0-windows10.0.19041.0`, `PublishAot=false`,
`IsAotCompatible=false` (PLAN §3: NativeAOT is not a Phase 1 release requirement).

## Proven behaviors (work package 0)

- `dotnet build Chaos.slnx` succeeds with 0 warnings / 0 errors (C# 15, all nine projects,
  generated RPC bindings and frontend bundle build ordered by the NeoAstra targets).
- `dotnet test` discovers and runs TUnit tests under Microsoft.Testing.Platform (nonzero counts).
- SQLite: file-backed database, WAL, FTS5 virtual table, Arabic + emoji text stored and
  retrieved after close/reopen (`SqliteStackSpikeTests`).
- Published self-contained win-x64 output contains the bundled React assets
  (`assets/index.html`, hashed JS/CSS, `neoastra-assets.json`); the published
  `Chaos.Desktop.exe` starts and hosts the NeoAstra app process offline from the publish folder.

## Windows packaging and desktop (package 13)

| Concern | Version | Evidence |
| --- | --- | --- |
| Windows SDK packaging tools | 10.0.26100.0 (`makeappx.exe`, `signtool.exe`, x64) | Found by `eng/package-msix.ps1` under `Windows Kits\10\bin` |
| Windows notification API | `Windows.UI.Notifications` from the `net11.0-windows10.0.19041.0` projection | No extra package; Windows App SDK is **not** used (decisions, package 13) |
| WebView2 Runtime (development machine) | 157.0.4341.42 | `EdgeUpdate` client registry key; Windows 11 Pro 10.0.26300 |
| MSIX minimum OS | 10.0.19041.0 | `packaging/AppxManifest.xml` (`desktop6:FileSystemWriteVirtualization` needs 2004+) |

Every project restores the `win-x64` RID (`Directory.Build.props`), so the lock files contain a
`net11.0…/win-x64` group and `eng/publish-windows.ps1` publishes in locked mode.

## Sandbox/CI notes

- `dotnet test` in MTP solution mode needs one-time download of the dotnet-test extension
  from `builds.dotnet.microsoft.com`; direct TUnit executable runs (e.g.
  `dotnet tests/Chaos.Domain.Tests/bin/Release/net11.0/Chaos.Domain.Tests.dll`) are the
  documented fallback in restricted environments and are encoded in `eng/verify.ps1`.
- `frontend/.npmrc` sets `install-links=true`; see above.

## GitHub transport baseline (G3, 2026-11-11)

- GitHub CLI minimum supported baseline: **2.103.3**, the installed version checked during G0.
  Discovery currently accepts the standard machine installation under `Program Files\GitHub
  CLI\gh.exe`; alternate/user-local installations are not yet supported.
- Setup metadata is read locally using `gh config get user --host github.com` (verified to return
  the configured login). Credential retrieval uses explicit hostname/user and `--no-refresh`;
  the user refreshes or signs in outside Chaos when authorization expires. See
  [config get](https://cli.github.com/manual/gh_config_get) and
  [auth token](https://cli.github.com/manual/gh_auth_token).
- REST API version remains `2026-03-10`. Authentication and issue requests use direct HTTPS
  to `api.github.com`, source-generated JSON, no redirects/cookies, 30-second request/body
  deadlines, a 16 MiB response cap and 1 MiB request cap. CLI streams are capped at 16,384
  characters, and child execution at 30 seconds. No new dependency was added.
- These are implementation baselines, not a claim that credential/account-switch or installed
  MSIX acceptance has passed; those remain live release gates.

G3 follow-up (2026-11-12): pull-request identity uses GraphQL `fullDatabaseId` (64-bit),
not the deprecated `databaseId`; the decoder accepts numeric or string BigInt values. The
[GitHub pull-request reference](https://docs.github.com/en/graphql/reference/pulls) documents
that distinction. No credential retrieval was run during the offline test gate.
