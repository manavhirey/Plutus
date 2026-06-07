# Plutus

A single-user, self-hosted personal finance app (Blazor, .NET 10). It connects to
your bank through **SimpleFIN Bridge**, pulls each day's transactions automatically,
classifies expenses with the **Claude API**, and lets you refine categories with a
note per expense.

See [`docs/superpowers/specs/2026-06-07-plutus-design.md`](docs/superpowers/specs/2026-06-07-plutus-design.md)
for the full design.

## Project layout

```
src/Plutus.Core   Domain, EF Core data, SimpleFIN client, Claude categorizer, sync
src/Plutus.Web    Blazor Web App (InteractiveServer), pages, daily sync scheduler
tests/            xUnit tests for Plutus.Core
```

## Run locally

```bash
export ANTHROPIC_API_KEY=sk-ant-...
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
| `Plutus:Claude:Model` | `claude-opus-4-8` | Categorization model (`claude-haiku-4-5` for low cost) |

The Anthropic API key comes only from `ANTHROPIC_API_KEY` (env / user-secrets) —
never from config or the database.

## Containerize

No Dockerfile — Plutus uses the .NET 10 SDK's built-in container publishing
(chiseled, non-root image):

```bash
# Build the image into your local Docker daemon
dotnet publish src/Plutus.Web -c Release /t:PublishContainer

# Run with persistence + your API key
ANTHROPIC_API_KEY=sk-ant-... docker compose up -d
```

The `plutus-data` volume holds both the SQLite DB and the Data Protection key ring,
so the encrypted SimpleFIN access URL stays decryptable across restarts. The app
listens on port 8080 inside the container.

## Test

```bash
dotnet test
```
