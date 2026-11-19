# Agent notes

## Containers

Aspire 17 now runs containers through `wslc`, so there is no need for Docker. Do not install, start or
require Docker (Docker Desktop, the Docker daemon or `docker` CLI) to run the AppHost or the tests that
depend on its containers (PostgreSQL, Mailpit, ClamAV, Less3).
