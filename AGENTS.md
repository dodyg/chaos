# Agent notes

## Containers

Aspire 17 now runs containers through `wslc`, so there is no need for Docker. Do not install, start or
require Docker (Docker Desktop, the Docker daemon or `docker` CLI) to run the AppHost or the tests that
depend on its containers (PostgreSQL, Mailpit, ClamAV, Less3).

## Infrastructure tests run one at a time (do not change this)

`tests/Chaos.Infrastructure.Tests` must run its tests **one at a time**. The project enforces it itself: `Parallelism.cs` declares an
assembly-level TUnit `ParallelLimiter` with a limit of 1. Run it as `dotnet test tests/Chaos.Infrastructure.Tests -c Release --no-build -- --minimum-expected-tests 1`
(or through `eng/verify.ps1`) and add no parallel flag. Never remove or raise the limiter, and never pass `--maximum-parallel-tests` greater
than 1 or set `TUNIT_MAX_PARALLEL_TESTS` for this project. Its several hundred file-backed SQLite tests take every core and the disk at
TUnit's default of one test per logical processor: on a 12-thread desktop the machine stops responding, the suite takes 14 minutes
instead of 4, and the host has failed after the last test. The same applies when a test run is started in the background: nothing else
should be saturating the machine at the same time.

## Verification workflow

During development, build affected projects and run focused tests, including real PostgreSQL tests for relevant
server changes. Before handing off a coherent change or completing a package, run the full gate described in
`PLAN3.md` section 0; rerun affected checks after later changes. Report skipped checks and actual test counts.
`eng/verify.ps1` reports step and project timings and stops on test failures without rerunning the suite.

## Server test PostgreSQL speed

Server tests create a database per test, which is very slow (10-90 s per test, over an hour for the suite) on a
default PostgreSQL container under `wslc`. For a disposable test instance, turn durability off and connect through
`127.0.0.1` (`localhost` adds a ~40 ms IPv6 fallback per connection). This is test-only; never use these flags for real data:

```powershell
wslc run -d --name chaos-test-pg -e POSTGRES_PASSWORD=<test password> -p 55432:5432 postgres:18.7 -c fsync=off -c synchronous_commit=off -c full_page_writes=off -c max_connections=500
$env:CHAOS_TEST_PG = "Host=127.0.0.1;Port=55432;Username=postgres;Password=<test password>"
```

With this the 216 server tests take about 12 minutes instead of well over an hour. The harness migrates one
template database per session and copies it for each test (`PostgresServer.CreateMigratedDatabaseAsync`); the rest of
the per-test time is mostly starting a test server. Remove the container
(`wslc rm -f chaos-test-pg`) when finished.
