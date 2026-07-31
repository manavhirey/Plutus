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
needed on the host. The development container receives only the solution,
`src/`, and `tests/`; the project-root `.env` file is never mounted. NuGet
packages are retained in the `plutus-nuget` Docker volume.

Before starting the app or production container, create or retain the
project-root `.env` file. It is ignored by Git. This command preserves an
existing file, applies owner-only permissions, and opens it in an editor rather
than putting a key in shell history:

```bash
(umask 077 && touch .env) && chmod 600 .env && "${EDITOR:-vi}" .env
```

In that editor, add the following line and save it (do not commit the file):

```env
OPENAI_API_KEY=...
```

```bash
# Restore, build, and run the complete test suite
docker compose -f docker-compose.dev.yml run --rm dotnet

# Build without an API key
docker compose -f docker-compose.dev.yml run --rm dotnet dotnet build Plutus.slnx

# Start the app with hot reload at http://localhost:8080
docker compose -f docker-compose.dev.yml -f docker-compose.dev.app.yml run --rm --service-ports dotnet \
  dotnet watch --project src/Plutus.Web run --no-launch-profile
```

The default development Compose file deliberately does not inject
`OPENAI_API_KEY`, so ordinary builds and tests do not require or receive it. The
hot-reload command adds `docker-compose.dev.app.yml`, which reads the protected
root `.env` file and passes only the key to the app process; the file itself is
not visible in the container. The key is never written to a project file, app
configuration, or the database. The app's local SQLite database and
data-protection keys are written to the working tree and are ignored by Git.

## Run locally without Docker

```bash
# Load the protected file without placing its key in shell history.
set -a
. ./.env
set +a
dotnet run --project src/Plutus.Web
```

When the app exits, run `unset OPENAI_API_KEY` or close that shell.

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

For Docker Compose, put the key in the gitignored, owner-only project-root
`.env` file as `OPENAI_API_KEY=...`. The hot-reload override and production
Compose configuration supply it to the app's process environment. The app reads
the key only from that process environment — never from app config or the
database — and the `.env` file must never be committed.

## Containerize

No Dockerfile — Plutus uses the .NET 10 SDK's built-in container publishing
(chiseled, non-root image):

```bash
# Build the image into your local Docker daemon
dotnet publish src/Plutus.Web -c Release /t:PublishContainer

# Docker Compose reads OPENAI_API_KEY from the protected project-root .env file
# and injects it into the app container; it does not mount the .env file.
docker compose up -d
```

The `plutus-data` volume holds both the SQLite DB and the Data Protection key ring,
so the encrypted SimpleFIN access URL stays decryptable across restarts. The app
listens on port 8080 inside the container.

## Test

```bash
docker compose -f docker-compose.dev.yml run --rm dotnet
```
