# eclair

`make help` lists every command. `make up` starts Postgres + Redis; `make run-api` runs `ChessPlatform.Runner`, which hosts WebApi **and** BackgroundWorkers. Running WebApi alone gives no matchmaking or computer opponent.

Active work: the Bolero/CLI migration in `docs/design/bolero-migration.md`. Read it before starting any part of it. `docs/design/bolero-migration-plan.md` is the checklist, one section per PR.

React is deleted, and the Bolero client arrives in P10, so there is no browser UI until then. Check gameplay with `tests/WebApi.Tests` or by calling the HTTP API on :5000.

Hooks in `.claude/hooks` format F# after each edit, check layer rules on `.fsproj` edits, and run the build plus backend unit tests before you finish. Don't run those yourself; fix what the hooks report.

## Rules

- Warnings are errors, including NuGet audit (NU1903). A newly published advisory can break `main` overnight; fix it by bumping the package, not by suppressing the warning.

## Gotchas

- `tests/Infra.Tests` needs Docker running (Testcontainers); the hooks skip it.
