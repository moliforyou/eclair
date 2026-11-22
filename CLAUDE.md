# eclair

`./eclair --help` lists every command. `./eclair env up` starts Postgres + Redis; `./eclair run` runs `ChessPlatform.Runner`, which hosts WebApi **and** BackgroundWorkers. Running WebApi alone gives no matchmaking or computer opponent.

Active work: the Bolero/CLI migration in `docs/design/bolero-migration.md`. Read it before starting any part of it. `docs/design/bolero-migration-plan.md` is the checklist, one section per PR.

React is deleted, and the Bolero client arrives in P10, so there is no browser UI until then. Check gameplay with `tests/WebApi.Tests` or by calling the HTTP API on :5000.

Hooks in `.claude/hooks` format F# after each edit, check layer rules on `.fsproj` edits, and run the build, backend unit tests and `./eclair analyze` before you finish. Don't run those yourself; fix what the hooks report.

## Rules

- Warnings are errors, including NuGet audit (NU1903) and the opt-in warnings listed in `Directory.Build.props`. A newly published advisory can break `main` overnight; fix it by bumping the package, not by suppressing the warning.
- Don't silence a warning or analyzer finding with `#nowarn` or an ignore comment unless the owner agrees. Fix the code.
- Package versions live only in `Directory.Packages.props`. Project files name packages without a version.
- Commit every `packages.lock.json` that a restore changes. CI restores in locked mode and fails on drift.
- Domain stays pure: no `mutable` and nothing that throws (`failwith`, `raise`, `invalidArg`...). The repo's own analyzer in `tools/analyzers` enforces this as ECLAIR-001 and ECLAIR-002. Use recursion or a fold, and return `Result` or `Option`.
- Analyzer packages must match the FSharp.Analyzers.SDK of the `fsharp-analyzers` tool in `.config/dotnet-tools.json`. Bump the tool and the packages together.

## Gotchas

- `tests/Infra.Tests` needs Docker running (Testcontainers); the hooks skip it.
