# Plutus — development guide

Single-user, self-hosted personal finance app (.NET 10, Blazor): connects to a bank
via SimpleFIN Bridge, pulls daily transactions, categorizes them with the OpenAI API.
Full design: `docs/superpowers/specs/2026-06-07-plutus-design.md`. User-facing
build/run/config docs live in `README.md` — this file covers only what's non-obvious.

## Environment gotchas (read first)
- **The .NET 10 SDK is at `~/.dotnet`, not on PATH.** Run `export PATH="$HOME/.dotnet:$PATH"`
  (or call `~/.dotnet/dotnet`) before any dotnet command, or it's "command not found".
- **Docker only via `sg docker -c "..."`** — the user is in the `docker` group but the
  session predates it, and `sudo` needs a password/TTY. e.g. `sg docker -c "docker compose up -d"`.
- **Compose secrets live only in the project-root `.env` file.** It is gitignored and must
  be mode `0600`. The default development Compose file neither mounts it nor injects
  `OPENAI_API_KEY`; only `docker-compose.dev.app.yml` supplies the key to the hot-reload app
  process. Never commit it or store the key in app configuration or the database.
- **Solution is `Plutus.slnx`** (XML solution format), not a `.sln`.

## Commands
```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build                                # whole solution
dotnet test                                 # xUnit (tests/Plutus.Core.Tests)
dotnet run --project src/Plutus.Web         # local dev; needs OPENAI_API_KEY in env
# Docker hot reload: docker compose -f docker-compose.dev.yml -f docker-compose.dev.app.yml run --rm --service-ports dotnet dotnet watch --project src/Plutus.Web run --no-launch-profile

dotnet tool restore                         # restore the dotnet-ef local tool first
dotnet ef migrations add <Name> --project src/Plutus.Core
```

## Architecture
- `src/Plutus.Core` — domain models, EF Core (SQLite) + migrations, SimpleFIN client,
  OpenAI categorizer, sync service; wired in `DependencyInjection.cs`.
- `src/Plutus.Web` — Blazor Web App (InteractiveServer), pages in `Components/Pages`,
  `DailySyncScheduler` background service.
- Categorization calls the OpenAI Responses API with **structured outputs** (fixed category enum);
  default model `gpt-5.6-luna` (`Plutus:OpenAI:Model`).

## Security / secrets
- `OPENAI_API_KEY` and `PLUTUS_AUTH_PASSWORD_HASH` are process-environment-only deployment
  values. The latter is an ASP.NET password hash for Plutus's one administrator account, never
  a plaintext password. Generate it interactively with
  `dotnet run --project src/Plutus.Web -- --create-password-hash`; it exits before normal app
  startup. Keep both in the gitignored, mode-`0600` project-root `.env` file. Production Compose
  and the separate hot-reload override provide them only through the app process environment;
  default build/test containers receive neither and cannot see `.env`. **Never commit `.env`**
  or put either value in app config/DB.
- `Program.cs` requires a valid `PLUTUS_AUTH_PASSWORD_HASH` before migrations or external-service
  registration. It protects all application endpoints by default; only login and static assets
  are anonymous. Production cookies (including antiforgery) use host-safe names, `Secure`,
  `HttpOnly`, same-site strict, and an eight-hour hard maximum lifetime. SQLite stores a session
  record per login; logout/expiry/hash rotation invalidates it and InteractiveServer revalidates
  every ten seconds. All Blazor state-changing handlers use the same guard before writes. The
  `Development`-only loopback hot-reload workflow uses request-matched cookies so local HTTP works;
  never use that environment on the server. Keep Caddy's HTTPS forwarding intact.
- The SimpleFIN access URL is stored **encrypted in the DB** via ASP.NET Data Protection;
  the key ring lives on the `plutus-data` volume — lose it and the connection can't decrypt.
- `Program.cs` trusts `X-Forwarded-*` only from RFC1918 peers with `ForwardLimit = 1`;
  preserve this if the proxy topology changes.

## Deployment (this VPS)
- Live: **https://plutus.kunigami.cloud** — TLS by the **host Caddy** (systemd,
  `/etc/caddy/Caddyfile`, auto Let's Encrypt) → `127.0.0.1:8080` → container. No Traefik.
- The Caddyfile also serves `kunigami.cloud → :3201` — preserve it on edits. Caddy
  edits/reloads need a real terminal (sudo has no TTY under the chat `!` prefix).
- Image build (no Dockerfile): the publish itself shells out to docker, so it **also** needs `sg`
  (else `Cannot find docker/podman executable`):
  `sg docker -c 'export PATH="$HOME/.dotnet:$PATH" && dotnet publish src/Plutus.Web -c Release /t:PublishContainer'`,
  then `sg docker -c "docker compose up -d"`. EF migrations auto-apply on container startup.
- Authentication cutover: generate the hash with the app's interactive generator (minimum 16
  characters), put only the generated variable in the protected server environment, then deploy.
  The new administrator-session migration auto-applies and signs out legacy browser cookies.
  Rotating the hash requires a container restart and invalidates existing sessions.

## One-off data jobs (backfills & diagnostics)
Config-gated `BackgroundService`s in `src/Plutus.Web/BackgroundServices` run once on startup when their
flag is set, else log "disabled" and no-op — so they're inert in prod by default. Toggled via `.env` →
compose env: `PLUTUS_BACKFILL_NOTES`, `PLUTUS_BACKFILL_TRANSFERS`, `PLUTUS_BACKFILL_MERGE_ACCOUNTS`,
`PLUTUS_DIAG_SYNC` (all default false). Run-once flow: set the flag `true` in `.env` → rebuild + deploy →
`sg docker -c "docker logs plutus"` for the "complete" line → set it back to absent/false → redeploy so
it's inert again. Add new one-offs by copying this pattern (and wire the compose env line).

## Inspecting the prod database
SQLite lives at `/data/plutus.db` in the `plutus` container (`plutus-data` volume), in **WAL mode**:
- Copy **all three** files together or you read stale data: `plutus.db`, `plutus.db-wal`, `plutus.db-shm`
  (e.g. `sg docker -c "docker cp plutus:/data/plutus.db /tmp/x.db"` plus the `-wal`/`-shm`).
- No `sqlite3` on the host — use `python3` (built-in `sqlite3` module).
- `Transaction.Amount` is stored as **text** (decimal converter), so numeric SQL like `WHERE Amount=2.40`
  silently misses — compare as text or filter in Python.
