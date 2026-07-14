# Eclair — Chess Platform

Real-time chess platform: F# backend (Falco + SignalR), React frontend, Postgres + Redis.

## Prerequisites

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) (see `Directory.Build.props` — warnings are errors)
- [Bun](https://bun.sh) 1.3+ (frontend package manager and test runner)
- [Docker](https://www.docker.com/) (Postgres/Redis containers, Testcontainers-based tests)
- `make`

## Quickstart

```sh
make install   # restore dotnet tools + frontend deps
make up        # start Postgres + Redis (waits for healthy)
make run-api   # terminal 1: backend on http://localhost:5000
make run-web   # terminal 2: frontend on http://localhost:5173
```

Open http://localhost:5173. The Vite dev server proxies `/api` and `/hubs` to the backend, so no CORS setup is needed in development.

## Project layout

```
src/backend/
  Domain/               Pure chess rules engine (no dependencies)
  Application/          Use cases + port interfaces (IGameRepository, IMessageBus, ...)
  Infrastructure/       Postgres (Npgsql.FSharp) + Redis (StackExchange.Redis) adapters
  WebApi/               Falco HTTP handlers + SignalR hub + broadcasters
  BackgroundWorkers/    Matchmaking worker + computer player worker
  ChessPlatform.Runner/ Composite entry point — runs WebApi AND BackgroundWorkers. Run this one.
src/frontend/           React + TypeScript + Vite + Tailwind (chess.js, @microsoft/signalr)
tests/                  Expecto test projects mirroring the backend layers
```

`ChessPlatform.Runner` is the process to run for a working local stack; running `WebApi` alone gives you the HTTP API but no matchmaking or computer opponent.

## Ports

| Service        | Port |
|----------------|------|
| Backend API    | 5000 |
| Frontend (dev) | 5173 |
| Postgres       | 5432 |
| Redis          | 6379 |

## Configuration

Defaults live in `appsettings.json` (localhost, `postgres`/`postgres`) and match `docker-compose.yml`. Override via environment variables — see `.env.example`.

## Testing

```sh
make test        # full suite: all backend projects + frontend (Infra.Tests needs Docker running)
make test-unit   # fast path: everything except Infra.Tests — no Docker required
```

Backend tests use Expecto (+ FsCheck property tests in Domain.Tests). `tests/Infra.Tests` spins up real Postgres/Redis via Testcontainers. Frontend tests run with `bun test`.

## Formatting & linting

```sh
make format   # fantomas (F#) + eslint --fix (TS)
make lint     # check only — same commands CI runs
```

## Git hooks (optional)

```sh
make hooks    # sets core.hooksPath to .githooks (pre-commit: fantomas + eslint on staged files)
```

## CI

GitHub Actions (`.github/workflows/ci.yml`) runs on pushes/PRs to `main`: backend format check, build, full test suite (Docker is available on the runner for Testcontainers), and frontend lint, typecheck, test, build.
