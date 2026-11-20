# eclair

`make help` lists every command. `make up` starts Postgres + Redis; `make run-api` runs `ChessPlatform.Runner`, which hosts WebApi **and** BackgroundWorkers. Running WebApi alone gives no matchmaking or computer opponent.

## Rules

- Layers are enforced by project references: Domain depends on nothing; Application only on Domain; Infrastructure and WebApi implement Application's ports. Never add a `ProjectReference` or `PackageReference` that crosses a layer to make something compile.
- Warnings are errors, including NuGet audit (NU1903). A newly published advisory can break `main` overnight; fix it by bumping the package, not by suppressing the warning.
- Run `make format` after editing F#; CI fails on Fantomas drift.

## Gotchas

- `tests/Infra.Tests` needs Docker running (Testcontainers). `make test-unit` skips it.
- Kill the API with `lsof -ti tcp:5000 -sTCP:LISTEN | xargs kill -9`. Plain `lsof -ti:5000` also matches the dev server's proxy sockets and kills it.
- End-to-end checks: see the `verify` skill.
