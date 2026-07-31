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

First create a password hash without typing a password into a command, shell
history, or project file. The command runs only the local hash generator and exits
before the app, database, or any external service starts:

```bash
docker compose -f docker-compose.dev.yml run --rm -it dotnet \
  dotnet run --project src/Plutus.Web -- --create-password-hash
```

Type and confirm a strong password of at least 16 characters at the hidden prompts. Copy the one generated
`PLUTUS_AUTH_PASSWORD_HASH=...` line into the protected environment file. It is a
password hash, not the password; still treat it as a sensitive deployment value.

In the editor, add the generated line plus the following API-key line and save (do
not commit the file):

```env
PLUTUS_AUTH_PASSWORD_HASH=...
OPENAI_API_KEY=...
```

The HTTPS hot-reload override also needs a local development certificate. Create
it once without placing its password in command history; the certificate directory
is ignored by Git and is mounted read-only into the container:

```bash
mkdir -p .dev-certs && chmod 700 .dev-certs
read -rs PLUTUS_DEV_CERT_PASSWORD
export PLUTUS_DEV_CERT_PASSWORD
dotnet dev-certs https --trust
dotnet dev-certs https -ep .dev-certs/plutus-dev.pfx -p "$PLUTUS_DEV_CERT_PASSWORD"
unset PLUTUS_DEV_CERT_PASSWORD
```

Then add `PLUTUS_DEV_CERT_PASSWORD=...` to the same protected `.env` file using
the editor. The PFX file and its password are local development secrets: never
commit either one.

```bash
# Restore, build, and run the complete test suite
docker compose -f docker-compose.dev.yml run --rm dotnet

# Build without an API key
docker compose -f docker-compose.dev.yml run --rm dotnet dotnet build Plutus.slnx

# Start the app with hot reload at https://localhost:8080
docker compose -f docker-compose.dev.yml -f docker-compose.dev.app.yml run --rm --service-ports dotnet \
  dotnet watch --project src/Plutus.Web run --no-launch-profile
```

The default development Compose file deliberately does not inject either
`OPENAI_API_KEY` or `PLUTUS_AUTH_PASSWORD_HASH`, so ordinary builds and tests do
not require or receive them. The hot-reload command adds
`docker-compose.dev.app.yml`, which reads the protected root `.env` file and
passes only the needed values to the app process; the file itself is not visible
in the container. Neither value is written to a project file, app configuration,
or the database. The app's local SQLite database and data-protection keys are
written to the working tree and are ignored by Git.

## Run locally without Docker

```bash
# Load the protected file without placing its key in shell history.
set -a
. ./.env
set +a
dotnet run --project src/Plutus.Web
```

When the app exits, run `unset OPENAI_API_KEY PLUTUS_AUTH_PASSWORD_HASH` or close
that shell. Authentication and antiforgery cookies are HTTPS-only in every
environment. Use the HTTPS launch profile (`https://localhost:7165`) locally, or
the HTTPS Docker hot-reload command above; the HTTP profile cannot sign in.

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

`PLUTUS_AUTH_PASSWORD_HASH` is a required process environment variable, not an
appsetting. Every normal app startup (including Development) fails closed before
database migration or external-service setup if it is missing or malformed. Create
it only with the interactive `--create-password-hash` command above. The app has
one administrator password; there is no registration, password reset, or recovery
endpoint. Login is rate-limited and protected by antiforgery; sessions use secure,
HTTP-only, same-site cookies and have a hard eight-hour maximum lifetime. Each
login also has a durable SQLite session record. Logout, session expiry, or a
password-hash rotation invalidates every tab that presents that session; the
InteractiveServer transport closes at ticket expiry and live circuits revalidate
every ten seconds. Each state-changing handler acquires a session operation lease
and holds it through all I/O and its database commit. Logout atomically blocks new
leases, cancels and drains existing leases, then persists revocation before its
response completes. This coordination is intentionally single-instance; deploying
multiple app replicas requires replacing it with a shared distributed lease store.

For Docker Compose, put `OPENAI_API_KEY=...` and the generated
`PLUTUS_AUTH_PASSWORD_HASH=...` in the gitignored, owner-only project-root `.env`
file. The hot-reload override and production Compose configuration supply them to
the app's process environment. The app reads both only from that process
environment — never from app config or the database — and the `.env` file must
never be committed. On the production host, add the hash to its protected
deployment environment before running the new image; the container will refuse to
start otherwise. Keep the existing HTTPS reverse proxy in front of the app. The
session-table migration applies automatically on startup. Existing browser cookies
from before this release cannot have a server session record and will be asked to
sign in again. To rotate the administrator password, generate a new hash, update
the protected environment, and restart the container; the hash fingerprint in
existing tickets ensures older sessions are rejected.

## Containerize

No Dockerfile — Plutus uses the .NET 10 SDK's built-in container publishing
(chiseled, non-root image):

```bash
# Build the image into your local Docker daemon
dotnet publish src/Plutus.Web -c Release /t:PublishContainer

# Docker Compose reads OPENAI_API_KEY and PLUTUS_AUTH_PASSWORD_HASH from the
# protected project-root .env file and injects them into the app container; it
# does not mount the .env file.
docker compose up -d
```

The `plutus-data` volume holds both the SQLite DB and the Data Protection key ring,
so the encrypted SimpleFIN access URL stays decryptable across restarts. The app
listens on port 8080 inside the container.

## Test

```bash
docker compose -f docker-compose.dev.yml run --rm dotnet
```
