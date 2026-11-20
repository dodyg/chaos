# S003 account-stream backfill rehearsal

This is throwaway SQL, not the Q2 production migration. Run it only against a disposable
PostgreSQL instance with a role allowed to create databases. Generate the S003 schema from the
repository's existing migrations, seed a source database, and clone it before applying the
backfill. For the `chaos-q0-postgres` disposable `wslc` container used for this run:

```powershell
dotnet tool run dotnet-ef migrations script 0 S003_Blobs --no-build --configuration Release --project src/Chaos.Server --output "$env:TEMP\chaos-q0-s003.sql"
wslc exec chaos-q0-postgres createdb -U postgres chaos_q0_source
Get-Content "$env:TEMP\chaos-q0-s003.sql" | wslc exec -i chaos-q0-postgres psql -U postgres -d chaos_q0_source -v ON_ERROR_STOP=1
Get-Content spikes/phase3-postgres/seed.sql | wslc exec -i chaos-q0-postgres psql -U postgres -d chaos_q0_source -v ON_ERROR_STOP=1
wslc exec chaos-q0-postgres createdb -U postgres -T chaos_q0_source chaos_q0_copy
Get-Content spikes/phase3-postgres/backfill.sql | wslc exec -i chaos-q0-postgres psql -U postgres -d chaos_q0_copy -v ON_ERROR_STOP=1
Get-Content spikes/phase3-postgres/check.sql | wslc exec -i chaos-q0-postgres psql -U postgres -d chaos_q0_copy -v ON_ERROR_STOP=1
```

The source and copy names must not already exist. The copy's assertions check both accounts'
sequence and pruning horizon, entities, pull positions and blobs. This is not a rehearsal on
an existing development database and does not replace Q2's EF migration and migration tests.
