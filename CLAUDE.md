# eclair

`make help` lists every command. `make up` starts Postgres + Redis; `make run-api` runs `ChessPlatform.Runner`, which hosts WebApi **and** BackgroundWorkers. Running WebApi alone gives no matchmaking or computer opponent.

Active work: the Bolero/CLI migration in `docs/design/bolero-migration.md`. Read it before starting any part of it.

Hooks in `.claude/hooks` format F# after each edit, check layer rules on `.fsproj` edits, and run the build plus backend unit tests before you finish. Don't run those yourself; fix what the hooks report.

## Rules

- Warnings are errors, including NuGet audit (NU1903). A newly published advisory can break `main` overnight; fix it by bumping the package, not by suppressing the warning.

## Gotchas

- `tests/Infra.Tests` needs Docker running (Testcontainers); the hooks skip it.
- Kill the API with `lsof -ti tcp:5000 -sTCP:LISTEN | xargs kill -9`. Plain `lsof -ti:5000` also matches the dev server's proxy sockets and kills it.
- End-to-end checks: see the `verify` skill.
