# Dependency versions — recorded at implementation kickoff

Recorded 2026-11-09 during PLAN.md work package 0. Update this file whenever a pinned
version changes; PLAN.md §1 pins are the authority.

## Toolchain

| Concern | Version | Evidence |
| --- | --- | --- |
| .NET SDK | 11.0.100-rc.3.26522.148 | `dotnet --list-sdks`; pinned exactly in `global.json` (`allowPrerelease: true`, `rollForward: disable`) |
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

## Sandbox/CI notes

- `dotnet test` in MTP solution mode needs one-time download of the dotnet-test extension
  from `builds.dotnet.microsoft.com`; direct TUnit executable runs (e.g.
  `dotnet tests/Chaos.Domain.Tests/bin/Release/net11.0/Chaos.Domain.Tests.dll`) are the
  documented fallback in restricted environments and are encoded in `eng/verify.ps1`.
- `frontend/.npmrc` sets `install-links=true`; see above.
