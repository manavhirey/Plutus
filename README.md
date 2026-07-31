# Plutus

A single-user, self-hosted personal finance app (Blazor, .NET 10). It connects to
your bank through **SimpleFIN Bridge**, pulls each day's transactions automatically,
classifies expenses with the **OpenAI API**, and lets you refine categories with a
note per expense.

See [`docs/superpowers/specs/2026-06-07-plutus-design.md`](docs/superpowers/specs/2026-06-07-plutus-design.md)
for the full design.

## Project layout

```
src/Plutus.Core   Domain, EF Core data, SimpleFIN client, OpenAI categorizer, sync
src/Plutus.Web    Blazor Web App (InteractiveServer), pages, daily sync scheduler
tests/            xUnit tests for Plutus.Core
```

## Develop with Docker

All development commands can run in the .NET 10 SDK container; no .NET SDK is
needed on the host. The source tree is mounted into the container and NuGet
packages are retained in the `plutus-nuget` Docker volume.

```bash
# Restore, build, and run the complete test suite
docker compose -f docker-compose.dev.yml run --rm dotnet

# The app requires an API key; export it before starting hot reload.
export OPENAI_API_KEY=...

# Start the app with hot reload at http://localhost:8080
docker compose -f docker-compose.dev.yml run --rm --service-ports dotnet \
  dotnet watch --project src/Plutus.Web run --no-launch-profile
```

The test command does not need an API key. Docker passes the exported key into
the development container for the app, but it is never written to a project
file. The app's local SQLite database and data-protection keys are written to
the working tree and are ignored by Git.

## Run locally without Docker

```bash
export OPENAI_API_KEY=...
dotnet run --project src/Plutus.Web
```

The SQLite database and Data Protection keys are created under the app content root
on first run; the schema migrates automatically at startup.

## Configuration

Non-secret settings live in `src/Plutus.Web/appsettings.json` (override with
`Plutus__*` environment variables):

| Setting | Default | Meaning |
| --- | --- | --- |
| `Plutus:Database:Path` | `plutus.db` | SQLite file path |
| `Plutus:DataProtectionKeysPath` | `keys` | Key-ring directory (encrypts the SimpleFIN access URL) |
| `Plutus:Sync:DailyTime` | `06:00` | Local time for the daily sync |
| `Plutus:Sync:LookBackDays` | `30` | First-run look-back window |
| `Plutus:Sync:OverlapDays` | `3` | Re-fetch window on later syncs (deduped) |
| `Plutus:OpenAI:Model` | `gpt-5.6-luna` | Categorization model |

The OpenAI API key comes only from the `OPENAI_API_KEY` process environment
variable — never from config or the database.

## Containerize

No Dockerfile — Plutus uses the .NET 10 SDK's built-in container publishing
(chiseled, non-root image):

```bash
# Build the image into your local Docker daemon
dotnet publish src/Plutus.Web -c Release /t:PublishContainer

# Run with persistence + your API key
OPENAI_API_KEY=... docker compose up -d
```

The `plutus-data` volume holds both the SQLite DB and the Data Protection key ring,
so the encrypted SimpleFIN access URL stays decryptable across restarts. The app
listens on port 8080 inside the container.

## Test

```bash
docker compose -f docker-compose.dev.yml run --rm dotnet
```
