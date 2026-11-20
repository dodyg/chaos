# Phase 3 Q0: integration evidence

Results as of 2026-11-20. A partial probe is not a completed spike.

| Spike | Status | Evidence and next action |
| --- | --- | --- |
| MCP stdio | **Works.** | `spikes/phase3-mcp-stdio` builds with SDK 2.4.0. A raw JSON-RPC probe initialized at protocol revision `2025-03-26`, listed `greet`, and called it successfully. The owner removed the Claude client checks from Q0 on 2026-11-20. |
| MCP HTTP and OAuth | **Works with a workaround.** | `spikes/phase3-mcp-http` pins MCP ASP.NET Core 2.4.0 and OpenIddict 7.8.0. A public test client, fixed in the server's validation handler, completed authorization code with PKCE, token exchange, MCP initialization and a tool call; an unauthenticated call returned 401. The chosen spike approach is a preconfigured public client, not dynamic registration. OpenIddict degraded mode requires custom client and redirect validation; this disposable sample lacks persistent registration, consent, TLS and production security and is not a Q19 implementation. |
| Named-pipe relay | **Works with limitations; cross-user check blocked.** | The desktop's `--mcp` starts an SDK-backed `CurrentUserOnly` pipe host or connects to an existing desktop listener. A raw probe against the real unpackaged executable initialized MCP, listed zero tools and exited cleanly; the desktop test also exercised the listener over a real pipe. When the desktop is absent, the fallback is a one-session headless protocol host, not the fully initialized desktop. Independent window-state observation and denial under a second Windows user remain unverified; on 2026-11-20 the owner confirmed another signed-in account was unavailable and directed that the cross-user check remain blocked. The owner removed the installed-MSIX check from Q0 on 2026-11-20. |
| diff3 | **Works with limitations.** | `spikes/phase3-diff3` passed 20 fixtures including CRLF, final newline, Arabic text and front matter. A 1,048,576-byte page with edits at opposite ends merged in 7.0 ms (Release, .NET 11.0.100). The prototype rejects diffs whose trimmed LCS matrix exceeds 4,000,000 cells rather than exhausting memory. Conflict markers use local/remote ordering and therefore do not yet guarantee identical markers when the two devices swap those roles. Q11 must address and test both limitations before adopting it. |
| PostgreSQL migration rehearsal | **Works with a workaround.** | Generated S003 SQL from the existing EF migrations, seeded two accounts with entities, changes, blobs and different sequence/horizon values, cloned that synthetic development database, and ran `spikes/phase3-postgres/backfill.sql` on the copy in PostgreSQL 18.7 via `wslc`. All four assertions in `check.sql` passed. This exercises a copy of a freshly seeded database, not a copy of an existing development database; repeat against a suitable real development copy before relying on production migration evidence. |

## Phase 2 open boxes at the PLAN3 baseline

These are outstanding items in `git show 5566277^:PLAN2.md`, not a claim that the owner has not confirmed E1. Some have subsequent decisions or delivered code; the named checks still need evidence or an explicit decision.

| Package | Remaining work recorded in the plan |
| --- | --- |
| P0 / D6 | Installed phase 1 checklist and the incomplete native integration spikes in `docs/desktop-support.md`; record individual results. |
| P1 | D6 was the stated reason the box remained open. |
| P2 | Native board pointer-drag check and the open D6 check. |
| P3 | Native desktop check of drag, table, board and alert; P2/D6 checks. |
| P6a | Native pushed refresh and GitHub sync indicator check. |
| P7, P8 | Deferred by owner on 2026-11-17; do not implement as part of Q0. |
| P10 | Native drawing, task from region, voice recording and restart; verify WebView microphone and fallback if needed. |
| P11 | App lock implementation and tests, including native Windows Hello, idle and Windows lock checks. |
| P12 | Live provider sign-in and real SMTP, provider callback and installed loopback checks; consult its package steps for complete exit evidence. |
| P13 | Editor changed-on-another-device notice and replaced-value history display; demonstrate the full two-client exit suite. SSE was addressed by P15. |
| P14 | Demonstrate join, adoption, sign-out, revocation and deletion scenarios and configured provider sign-ins. |
| P16 | Vault key wrapper, byte-offset download resume, offline-list sidebar indication, live malware and real-network checks. Less3 and ClamAV AppHost adapters were subsequently recorded in `docs/decisions.md`. |
| P17 | Demonstrate cross-device reminder and view alert acknowledgement, single GitHub runner, calendar credential prompts and deterministic summaries. |
| P18 | Calendar subscription token endpoint, settings, tests and live checks in two calendar apps. |
| P19 | Conditional on the phase 2 phone decision; record applicable outcome. |
| P20 | Security review, offline/privacy checks, installed update, deployment and restore, representative performance, two-device acceptance and documentation. |

P9 was ticked in the baseline but its note also records outstanding native attachment and PDF checks. P15 was ticked with a real-network check still noted as outstanding. See the source plan and later decision records for details.
