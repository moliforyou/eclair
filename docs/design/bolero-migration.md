# eclair: Bolero frontend, `eclair` CLI and repo cleanup

Status: approved by the owner, 2026-11-20. This file is the source of truth for the migration; start any session on this work by reading it.

## Intent

eclair is the first target for an agent-driven "code factory" (a separate, later project). Before building the factory, make the repo as **unified and simple** as possible:

1. **One language, one toolchain.** Replace the React/TypeScript/Bun frontend with an F# Bolero (Blazor WebAssembly) client. Afterwards every build, test, format, lint and verification step is .NET.
2. **The compiler checks the whole stack.** Today the F# ↔ TypeScript seam (HTTP DTOs in PascalCase, SignalR payloads in camelCase, `Item1`/`item1` tuple shapes) is unchecked. Afterwards client and server share F# types, so contract drift is a compile error.
3. **Client-side speed.** Move legality and highlighting run in the browser with no round trip, using the *same compiled Domain assembly* the server uses (replacing chess.js).
4. **One project CLI.** A single `eclair` command that agents, hooks, CI and humans all use for environment, build, test, gameplay and verification tasks.
5. **Strip what modern models don't need.** Keep only knowledge the code can't express.

Why: agents should ultimately answer to the compiler, the type system and deterministic tooling, which makes reviewing their changes much easier. The project is low stakes and open source.

## Already done

| PR | What |
|---|---|
| #1 | pstack enabled for this repo only (`.claude/settings.json`), superpowers off here, SessionStart hook starts sessions under `/pstack:poteto-mode` |
| #2 | Testcontainers 4.17.0 (patched SSH.NET; NuGet audit is a build error) |
| #3 | Backend cleanup: 303 narration comment lines removed (pstack `no-comments`), root README removed, short `CLAUDE.md`; CI prints the Fantomas diff |
| #4 | Claude hooks: Fantomas after each F# edit, layer check after `.fsproj` edits, build + backend unit tests before finishing (`.claude/hooks/`, `scripts/check-layers.sh`) |

## Goals

- Bolero WASM client with feature parity with today's React app (list below).
- Shared, compile-checked contracts between client and server.
- Client references **Domain + Contracts only**.
- `eclair` CLI replaces the Makefile and shell scripts as the single entry point.
- A pstack-format verification skill (`verify-eclair`) driven by the CLI, plus screenshot baselines, run in CI on every PR.
- Node/Bun/npm/Vite/ESLint/TypeScript/Tailwind/chess.js fully removed from the repo and CI.
- Open-source contributor docs at the end: README, CONTRIBUTING, code of conduct, LICENSE.

## Non-goals

- The code factory itself (agent roles, risk-tiered auto-merge, separate GitHub App identities for author and reviewer). Next project.
- Sound (removed by owner decision; may return later).
- New gameplay features, auth, deployment, API redesign beyond what shared contracts require.
- Pixel-perfect reproduction of the current UI.

## Anti-goals (failure even if it "works")

- Any Node/JS package toolchain survives or is reintroduced (`package.json`, `bun.lock`, `node_modules`, a bundler).
- The client references Application or Infrastructure, or duplicates chess rules instead of using Domain.
- Gameplay regressions: illegal moves accepted, legal moves rejected, opponent moves not arriving live, promotion/castling/en passant broken.
- Two entry points survive (Makefile *and* CLI), or agents go back to writing throwaway scripts to drive the app.
- Removing comments that carry a non-obvious *why* about an external constraint.

## Constraints

- .NET 10 SDK; `Directory.Build.props` keeps `TreatWarningsAsErrors`, `AnalysisLevel latest-all`, `Nullable`. NuGet audit warnings are errors.
- Fantomas formatting enforced (hook, CI, pre-commit).
- Bolero 0.25.x (targets net10.0).
- Postgres + Redis via docker compose; Testcontainers-based `Infra.Tests`.
- `ChessPlatform.Runner` remains the single process for WebApi + BackgroundWorkers, and also serves the client.
- Every new project must be added to the layer rules (today `scripts/check-layers.sh`, later the CLI's `layers` command) or the check fails.

## Approaches considered (frontend)

| Approach | Trade-off | Verdict |
|---|---|---|
| Falco + htmx (server-rendered) | Simplest, one toolchain; every move is a server round trip | Rejected: owner wants client-side speed |
| Fable + Feliz (F# → JS, React) | Client-side speed, small/fast startup; keeps a JS toolchain alongside .NET | Rejected: works against "unified" |
| **Bolero (Blazor WASM)** | Client-side speed, .NET-only, Domain runs unchanged in the browser; first-load runtime download (~1 MB, cached), smaller community | **Chosen** |

## Design

### Solution shape (after)

```
src/backend/
  Domain/               + SAN parse/format (pure)
  Contracts/            NEW: wire DTOs + JSON options, shared by WebApi and Client
  Application/
  Infrastructure/
  WebApi/               serves Client static assets; hub + Falco handlers use Contracts
  BackgroundWorkers/
  ChessPlatform.Runner/
src/client/             NEW: Bolero WASM project (replaces src/frontend/)
tools/eclair/           NEW: the eclair CLI (references no app project)
tests/
  Domain.Tests/         + SAN property tests
  Client.Tests/         NEW: Expecto tests of the Elmish update function + CSS guardrail tests
  (existing test projects updated for contract moves)
  E2E.Tests/            NEW: Playwright (.NET) journeys + screenshot diffs, built on the CLI's verify engine
eclair                  NEW: wrapper script so `./eclair <command>` works from the repo root
```

Layer rules (enforced):
- `Contracts` → `Domain` only (or nothing); no packages beyond JSON serialisation.
- `Client` → `Domain`, `Contracts`.
- `WebApi` → `Domain`, `Application`, `Infrastructure`, `Contracts`, `Client` (to host its static assets).
- `tools/eclair` → no app projects.

### Domain additions: SAN

Domain has no Standard Algebraic Notation. The UI needs it for the typed move command and the move ledger. Add, pure:
- `San.format : GameState -> Move -> string` (disambiguation, `x`, `+`/`#`, `O-O`/`O-O-O`, `=Q`).
- `San.parse : GameState -> string -> Result<Move, SanError>` (accepts what chess.js accepted: `e4`, `Nf3`, `exd5`, `O-O`, `e8=Q`, optional `+`/`#`).
- FsCheck property: for every legal move in generated positions, `parse s (format s m) = Ok m`.
- A `playMoves ["e4"; "d5"; ...]` test helper replaces the long `play <| … |> unwrap |> play` chains in Domain tests.

### Contracts

Move wire DTOs out of `WebApi/Handlers.fs` and `Application/Ports.fs` (`MoveSummaryDto`) into `Contracts`:
- HTTP: `MatchmakingRequest`, `MoveRequest` (from/to as explicit `{ File; Rank }` records, not tuples), `CreateGameRequest`, `GameSnapshot` (GET /api/games/{id}), error body.
- SignalR: `MovePlayed`, `MatchFound` payloads; hub method names as constants.
- One `JsonSerializerOptions` (FSharp.SystemTextJson for unions/options) used by Falco responses, SignalR (`AddJsonProtocol`), the client and the CLI. Ends the PascalCase/camelCase split.
- `Status` becomes a union instead of the `"Checkmate:White"` string.

Endpoint paths and hub groups stay the same. `MoveSummaryDto` is also used inside Infrastructure (`RedisBus` via `MoveExtensions.toDto`); Infrastructure must not reference Contracts, so Redis gets its own small internal message type.

### Client (Bolero)

- Elmish MVU (`Program.mkProgram`), Bolero HTML DSL (no template type provider).
- **Model:** screen (Landing | Game), player id, queue state, connection status, game id, players/colours, `GameState` (Domain), selection + legal targets, pending promotion, command text/error, move ledger, latency.
- **Board:** click-to-move; selecting a piece highlights legal targets from Domain; promotion picker; illegal attempts rejected locally with no request. Server stays authoritative; the client re-syncs from the response / `MovePlayed`.
- **Typed SAN command** via `San.parse`.
- **Live updates:** `Microsoft.AspNetCore.SignalR.Client` over WebSockets (same origin, so the Vite proxy problem and forced SSE disappear). Auto-reconnect; rejoin lobby/game groups on reconnect.
- **Latency** via the existing `Ping` hub method.
- **Player id** in `localStorage` via `IJSRuntime` (no custom JS file).
- **Move ledger:** persisted history keeps only from/to/promotion (`PostgresRepo` `HistoryJson`), so the client rebuilds the ledger by replaying moves from the starting position through Domain and formatting SAN.
- **Turn gating (new):** the board disables itself when it isn't the player's turn or the game is over.
- **Styling:** hand-written CSS in `wwwroot`, close to the current retro-terminal look; Tailwind removed. Guarded below.

Feature-parity checklist: landing screen with editable player id · Find Opponent / cancel queue · Play Computer · board, colour indicator, move ledger with SAN, status banner (check / checkmate / stalemate / draw) · click-to-move with legal highlights · promotion picker with cancel · typed SAN move with error message · connection status · latency readout · leave game · opponent moves arrive live · Reset Game / Play Again vs computer · Flip Perspective · game-over overlay · last-move and king-in-check highlighting.

### CSS guardrails

Owner's concerns: agents making a mess of the styles, and visual breakage nobody notices.

1. **Typed class names.** Every CSS class is an F# value in one `Css` module; markup uses only those. A test fails if markup references an undefined class or the CSS defines an unused one.
2. **Design tokens.** Colours, spacing, radii and fonts are CSS custom properties in one `tokens.css`. A test fails on raw hex/rgb colours or raw `px`/`rem` lengths elsewhere (allowlist: `0`, `1px` borders, `100%`).
3. **Screenshot diffs.** Fixed states (landing, queued, mid-game, piece selected with legal targets, promotion picker, check, game over) at a fixed viewport, compared to committed baselines. A nonzero diff fails; updating baselines is an explicit, reviewable change. Changes to CSS, the `Css` module or baselines are never "low risk" for the factory's future auto-merge.
   - Before React is deleted, capture the same states from the React app as **reference images** (not a gate). Once the owner approves the Bolero look, its screenshots become the baselines.

### Hosting

`WebApi` references the Client project and serves its framework files and static assets with an `index.html` fallback. Dev and prod both run from `ChessPlatform.Runner` on :5000; no separate dev server, proxy or CORS. The Runner has no `launchSettings.json`, so `dotnet run` starts in Production, where static web assets aren't served automatically; hosting must work in both environments (e.g. `MapStaticAssets`). `WebApi.Tests`' test host may need adjusting once WebApi references the client.

### The `eclair` CLI

One command for everything agents and humans do with the project, following pstack's "build the lever" principle: a few composable commands that do real work, JSON output (`--json`), `--dry-run` on anything destructive, errors that say what to do, rich `--help`.

- **Location and invocation:** a .NET console app in `tools/eclair/`, run from the repo root as `./eclair <command>` (a thin wrapper script that builds once and reuses the build). It references **no** app project and works through processes, HTTP and the database, so it still runs when the app doesn't compile.
- **Commands (initial set):**
  - `env up | down | doctor`: Postgres + Redis, readiness, "is this instance worth driving?" (process up, port owned by us, right build).
  - `build | test [--unit|--infra|--e2e] | format | lint | layers`: today's Makefile targets; `layers` absorbs `scripts/check-layers.sh`.
  - `run`: start the Runner and wait until usable.
  - `db reset | seed`.
  - `game new | move | show`: drive the real HTTP/SignalR API, for gameplay debugging without a browser.
  - `verify drive <feature> | screenshot | diff`: the browser harness (Microsoft.Playwright for .NET) used by `verify-eclair`, `E2E.Tests` and CI.
- **Replaces** the Makefile, `scripts/check-layers.sh` and the bash bodies of the Claude hooks (hooks become one-line calls). Exception: the after-edit hook keeps calling Fantomas directly to avoid CLI startup on every edit.
- Built early in the migration, so later phases (and their agents) already use it.

### Verification (pstack format)

Today's `.claude/skills/verify` is hand-written prose; each run the agent writes a throwaway Playwright script. Replace it with pstack's structure:

1. **Before React is removed:** use the current `verify` skill one last time to capture the reference screenshots (CSS guardrail 3).
2. **Build the engine:** `eclair verify …` and `eclair env doctor` against the Bolero app.
3. **Generate the skill:** run `/pstack:create-verification-skill` to produce `.claude/skills/verify-eclair/` (Launch, Doctor, Drive, Evidence, Cleanup, Helpers) with a `features/` map, one file per user-facing feature, every drive step being an `eclair` command. Prove it end to end as that skill requires, then delete the old `verify` skill.
4. **Keep it honest:** `/pstack:maintain-verification-skill` after feature work.
5. **Gate:** CI runs the full verification pass and screenshot diffs as a required check on every PR. Not a Claude hook: a full browser run with Postgres and Redis takes minutes.

### Tests

- `Domain.Tests`: SAN unit + property tests; `playMoves` helper.
- `Client.Tests` (Expecto, no browser): update-function tests (selection yields legal targets, click-to-move emits a move request, disabled when not your turn, external state sync, promotion flow + cancel) and the CSS guardrail tests.
- `E2E.Tests`: journeys and screenshot diffs via the CLI's verify engine. Needs Docker, a running app and browsers; excluded from the default `dotnet test` run. On the current verify machine (no Node, cached chromium-1217, possibly no PowerShell), pin Microsoft.Playwright to match the cached browser.
- `WebApi.Tests` updated for contract changes.

### Cleanup (remaining)

Backend cleanup is done (PR #3). Remaining, when React is deleted: `src/frontend/` (including its README), the ESLint part of `.githooks/pre-commit`, the CI `frontend` job, node/bun `.gitignore` entries, the Makefile, and gotchas in `CLAUDE.md` that stop being true (dev-server proxy sockets).

### Follow-ups found by the comment pass

Fold into the migration where they touch the same code; otherwise small separate PRs:
- `Position` is a bare `int * int` → File/Rank types (fits Contracts work).
- `HistoryJson.toHistory` rebuilds captures/castling/en passant as `Normal` → store the move kind or return a summary type (fits ledger work).
- `IMessageBus.DequeueMatchmakingPair` can pop a lone or duplicate player → atomic pair-or-nothing dequeue.
- `ex.SqlState = "23505"` → `PostgresErrorCodes.UniqueViolation`.
- Dead `folder` in `FenParser.parsePiecePlacement`.
- `isKingInCheck` treats "no king" as "not in check".
- `opponent` in `detectGameStatus` is the side to move; `Status = InProgress` placeholder in `applyMove`.
- `RandomEngine` is a declared stopgap.
- Dead bindings in two Domain tests; "cannot castle through check" only tests castling out of check.
- Infra "RedisBus Pub/Sub" test: fixed 500 ms sleep and unsynchronised cross-thread flag (flaky).
- `.githooks/pre-commit` relies on unquoted word-splitting (moot once the CLI replaces it).

### Open-source docs (final phase)

After the migration, written for contributors: `README.md` (what eclair is, quickstart via `./eclair`, architecture, link to this doc), `CONTRIBUTING.md` (setup, CLI commands, layer rules, hooks, how PRs are reviewed), `CODE_OF_CONDUCT.md`, and a `LICENSE`. **The repo has no license today, so it isn't legally open source; the owner chooses which (MIT is the common default).** Remove the "active work" pointer from `CLAUDE.md` when the migration ends.

## How the work runs

- Under `/pstack:poteto-mode` with its multi-phase plan playbook (stacked PRs). Skills the phases lean on: `architect` (Contracts, SAN, Elmish types before code), `principle-type-system-discipline`, `principle-model-the-domain`, `principle-boundary-discipline`, `tdd`, `principle-prove-it-works`, `create-verification-skill` / `maintain-verification-skill`, `no-comments`.
- Suggested phase order (the plan may refine it): reference screenshots → `eclair` CLI over today's stack → Domain SAN + Contracts → Bolero client at parity alongside React → verification skill + screenshot baselines + CI gate → delete React and remaining tooling → open-source docs.
- The Claude hooks run on every session with a .NET SDK: don't run format/build/tests by hand; fix what they report. Cloud sessions without .NET rely on CI.
- pstack loads from the repo's `.claude/settings.json` after the workspace-trust prompt. Cloud sessions don't add repo-declared marketplaces, so CI and cloud runs need another way to load pstack (factory project).

## Left to the builder

- Exact Elmish message/model types and file layout in `src/client`.
- Exact CSS.
- CLI internals (argument parsing library, wrapper script details) within the constraints above.
- Playwright browser install mechanism in CI.

## Owner decisions and calls to check

Owner decided: Bolero; merge-by-risk for the future factory; sound removed; CSS guardrails (typed classes, tokens, screenshot diffs); pstack scoped to this repo with poteto-mode as default; pstack-format verification driven by a script; one generic CLI rather than a verification-only one; contributor docs at the end.

Claude's calls: keep Reset / Play Again / Flip / game-over overlay / last-move and check highlighting · board disabled when it isn't your turn · Bolero HTML DSL + Elmish · keep HTTP endpoints + SignalR rather than Bolero remoting · React screenshots as reference, Bolero screenshots as baselines · CLI replaces the Makefile · after-edit hook calls Fantomas directly.
