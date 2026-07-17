---
name: verify
description: Build, launch, and drive the eclair chess app (F# backend + React frontend) to verify changes end-to-end with a headless two-player browser session.
---

# Verifying eclair end-to-end

## Launch the stack

```bash
make up                                      # Postgres + Redis (docker compose --wait)
dotnet run --project src/backend/ChessPlatform.Runner   # API + workers on :5000 (background)
cd src/frontend && bun run dev               # Vite on :5173 (background)
# readiness: curl -s -o /dev/null -w "%{http_code}" http://localhost:5000/api/games/x  -> 404 means up
```

`node` is NOT installed on this machine — use `bun` for everything (scripts, dev server).

## Drive it headless

Playwright works with the locally cached browsers: install `playwright@1.59` (matches cached chromium-1217) in a scratch dir and run scripts with `bun`. Two isolated `browser.newContext()`s = two players; seed distinct IDs with `ctx.addInitScript(() => localStorage.setItem('chess_player_id', 'e2e_alice'))` before `page.goto`.

Flow: wait for `LINK: CONNECTED` in body text → click "Find Opponent" on both pages → wait for `MOVE TELEMETRY LEDGER` → read `PLAYING AS WHITE/BLACK` (case-insensitively: CSS uppercases innerText!) to find who moves first → type SAN into the input with placeholder `ENTER ALGEBRAIC MOVE...` and press Enter → assert the SAN shows up in the opponent's body text.

## Gotchas learned the hard way

- **Vite-under-Bun cannot proxy WebSocket upgrades** — the `/hubs` WS attempt hangs ~20s then SignalR falls back to SSE. The app forces SSE in dev (`App.tsx`, `HttpTransportType.ServerSentEvents`); don't "fix" WS timeouts by touching the proxy.
- **Killing the API**: use `lsof -ti tcp:5000 -sTCP:LISTEN | xargs kill -9`. Plain `lsof -ti:5000` also matches the Vite proxy's client sockets and kills the dev server.
- Disconnect detection over SSE takes ~30s (SignalR server-timeout), so waits for "noticed disconnect" need a 45s budget.
- Page-text assertions must be case-insensitive; the UI CSS-uppercases most labels.
- HTTP API is PascalCase (`Fen`, `Moves`, `From.Item1`); SignalR payloads are camelCase (`fen`, `moves`, `from.item1`). Move POST body: `{FromRow, FromCol, ToRow, ToCol, Promotion}` + `X-User-Id` header.

## Gates

`make build` (dotnet + tsc), `make lint` (fantomas --check + eslint), `make test-unit` (Expecto suites + `bun test`). Run `make format` after editing F#.
