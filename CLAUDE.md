# Plutus — Claude Code guide

Single-user, self-hosted personal finance app (.NET 10, Blazor): connects to a bank
via SimpleFIN Bridge, pulls daily transactions, categorizes them with the Claude API.
Full design: `docs/superpowers/specs/2026-06-07-plutus-design.md`. User-facing
build/run/config docs live in `README.md` — this file covers only what's non-obvious.

## Environment gotchas (read first)
- **The .NET 10 SDK is at `~/.dotnet`, not on PATH.** Run `export PATH="$HOME/.dotnet:$PATH"`
  (or call `~/.dotnet/dotnet`) before any dotnet command, or it's "command not found".
- **Docker only via `sg docker -c "..."`** — the user is in the `docker` group but the
  session predates it, and `sudo` needs a password/TTY. e.g. `sg docker -c "docker compose up -d"`.
- **Solution is `Plutus.slnx`** (XML solution format), not a `.sln`.

## Commands
```bash
export PATH="$HOME/.dotnet:$PATH"
dotnet build                                # whole solution
dotnet test                                 # xUnit (tests/Plutus.Core.Tests)
dotnet run --project src/Plutus.Web         # local dev; needs ANTHROPIC_API_KEY in env

dotnet tool restore                         # restore the dotnet-ef local tool first
dotnet ef migrations add <Name> --project src/Plutus.Core
```

## Architecture
- `src/Plutus.Core` — domain models, EF Core (SQLite) + migrations, SimpleFIN client,
  Claude categorizer, sync service; wired in `DependencyInjection.cs`.
- `src/Plutus.Web` — Blazor Web App (InteractiveServer), pages in `Components/Pages`,
  `DailySyncScheduler` background service.
- Categorization calls the Claude API with **structured outputs** (fixed category enum);
  default model `claude-opus-4-8` (`Plutus:Claude:Model`).

## Security / secrets
- `ANTHROPIC_API_KEY` is the only secret — from env or the gitignored `.env`.
  **Never commit `.env`** or put the key in config/DB.
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
