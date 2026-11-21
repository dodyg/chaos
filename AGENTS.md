# Agent notes

## Containers

Aspire 17 now runs containers through `wslc`, so there is no need for Docker. Do not install, start or
require Docker (Docker Desktop, the Docker daemon or `docker` CLI) to run the AppHost or the tests that
depend on its containers (PostgreSQL, Mailpit, ClamAV, Less3).

## Verification workflow

During development, build affected projects and run focused tests, including real PostgreSQL tests for relevant
server changes. Before handing off a coherent change or completing a package, run the full gate described in
`PLAN3.md` section 0; rerun affected checks after later changes. Report skipped checks and actual test counts.
`eng/verify.ps1` reports step and project timings and stops on test failures without rerunning the suite.
