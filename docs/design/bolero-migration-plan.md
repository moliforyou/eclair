# Bolero migration: execution plan

Goal: carry out `docs/design/bolero-migration.md`. Afterwards a Bolero client shares compiled Domain and Contracts with the server, `./eclair` is the only entry point, and no Node toolchain is left. This file is the checklist. The design doc remains the source of truth for intent and constraints.

## What changes, in landing order

| PR | Branch | Depends on | Gated on owner review |
|---|---|---|---|
| P1 | `migration/react-reference-shots` | trunk | no |
| P2 | `migration/delete-react` | P1 | **yes** |
| P3 | `migration/eclair-core` | P2 | **yes** |
| P4 | `migration/eclair-env` | P3 | no |
| P5 | `migration/eclair-game` | P4 | no |
| P6 | `migration/domain-san` | trunk | no |
| F1 | `fix/atomic-matchmaking-dequeue` | trunk | no |
| P7 | `migration/contracts` | P5 | no |
| P8 | `migration/client-skeleton` | P7 | **yes** |
| P9 | `migration/eclair-verify` | P8 | no |
| P10 | `migration/client-live-game` | P9, P6 | **yes** |
| P11 | `migration/client-moves` | P10 | **yes** |
| P12 | `migration/client-matchmaking` | P11, F1 | **yes** |
| P13 | `migration/verify-eclair-skill` | P12 | no |
| P14 | `migration/baselines-ci-gate` | P13 | **yes** |
| P15 | `migration/open-source-docs` | P14 | **yes** |

```
P1 ── P2 ── P3 ── P4 ── P5 ── P7 ── P8 ── P9 ── P10 ── P11 ── P12 ── P13 ── P14 ── P15
P6  (from trunk, any time) ───────────────────────┘
F1  (from trunk, any time) ───────────────────────────────────────┘
```

P6 and F1 branch from trunk and can run alongside P1 to P5. Everything else stacks. Each PR touches only the files its section lists. A file that two PRs touch belongs to the earlier one first, and the later one rebases.

## Prototype findings (2026-11-20)

The prototype lives in the session scratchpad and is throwaway. It used a Bolero client that references `ChessPlatform.Domain`, a Falco host that mimics `WebApi`, and a runner that mimics `ChessPlatform.Runner`. Everything was built under the repo's `Directory.Build.props`.

1. **Bolero 0.25.65 builds cleanly** with `TreatWarningsAsErrors` and `AnalysisLevel latest-all` on .NET SDK 10.0.300, using `Microsoft.AspNetCore.Components.WebAssembly` 10.0.15. Domain compiles to WASM unchanged. In headless Chromium the client listed 20 legal moves at the start position and the targets f3 and h3 for g1. Measured.
2. **Hosting needs the Web SDK.** With `Sdk="Microsoft.NET.Sdk"`, the runner gets no `Runner.staticwebassets.endpoints.json`, and `MapStaticAssets` throws at startup. Switching `WebApi` and `Runner` to `Microsoft.NET.Sdk.Web` produces the manifest. Measured.
3. **The `index.html` fallback needs `builder.WebHost.UseStaticWebAssets ()`** in every environment. Without it, Production serves `/_framework/*` but returns 404 for `/` and for deep links. With it, both return 200. Measured.
4. **A bare fallback swallows unknown API routes.** `/api/nope` returned 200 with `index.html`. The fallback must exclude `/api` and `/hubs`. Measured.
5. **`WebApplicationFactory` still works** against a Web SDK host that references the client. `/`, `/api/ping` and `/_framework/blazor.webassembly.js` all returned 200 in the test host. Measured.
6. **Microsoft.Playwright 1.63.0 matches the cached `chromium-1243`.** It launched with no browser download, no PowerShell, and no `node` on `PATH`. The `verify` skill's note about `chromium-1217` is out of date. The revisions were read from Playwright's `browsers.json` per tag: 1.62 uses 1234, 1.63 uses 1243, 1.64 uses 1248.
7. **First load is about 3.7 MB, not about 1 MB.** For a Release publish served by the runner, Chromium reported 3.77 MB transferred over 56 requests. With `InvariantGlobalization` it reported 3.61 MB over 55 requests. The largest files are FSharp.Core (995 KB brotli), `dotnet.native.wasm` (977 KB) and CoreLib (538 KB). Time to interactive on localhost was 245 to 320 ms. Click to highlight had a p50 of 33 ms over 20 clicks, including Playwright overhead. Each figure comes from one run. The browser caches these files after the first visit.

## Decisions

The owner confirmed these on 2026-11-20: delete React early, MIT license, and a first load of about 3.7 MB is acceptable for now.

- **React is deleted right after the reference screenshots (P2).** The design doc deletes it near the end. P7 changes the wire format, which breaks React anyway. Keeping it alive means patching code that will be deleted. Reference images cover visual comparison, and git history keeps the code. Between P2 and P10 there is no browser UI. Gameplay stays covered by `WebApi.Tests` and by `./eclair game`. This follows the **subtract before you add** and **outcome-oriented execution** principles.
- **The CLI reads and writes JSON without types.** The design doc says the CLI references no app project, but also that it uses the shared JSON options. Those conflict, because Contracts is an app project. The CLI uses `System.Text.Json.Nodes`. P7's live check proves that the CLI and the server agree on the wire format.
- **No separate `E2E.Tests` project.** The journeys and screenshot diffs live in the CLI's verify engine. `./eclair test --e2e` runs them. A second test project would run the same journeys twice.
- **No `db seed` command.** `./eclair game new --moves "e4 e5 ..."` builds any position through the real API, so a seed command adds nothing.
- **`./eclair game watch` is added.** "Opponent moves arrive live" is an anti-goal regression, and only a SignalR subscriber can check it without a browser.
- **`InvariantGlobalization` is on in the client.** It saves 160 KB, and the UI has no culture-specific formatting.

## Perf baselines measured on trunk

These are probes on the owner's Mac, 2026-11-20. Each PR remeasures its own baseline before it changes anything.

| Probe | Result |
|---|---|
| `scripts/check-layers.sh`, warm, 4 runs | 0.04 s each |
| `make test-backend` (four unit suites, 74 tests) | 6.50 s wall |
| `dotnet build ChessPlatform.slnx` with nothing changed | 0.81 s |

---

## P1: React reference screenshots

- [ ] Depends on: trunk.
- [ ] Files: `docs/design/reference/react/*.png` and `docs/design/reference/react/perf.md`. The driving script stays in the scratchpad and is not committed.
- [ ] Change: capture the seven states from the design doc using the current stack (`make up`, `make run-api`, `bun run dev`). Use Playwright for Bun 1.63, which matches the cached `chromium-1243`. Use a 1280×800 viewport with two browser contexts.
  - Landing.
  - Queued.
  - Mid-game after `e4 e5 Nf3 Nc6`.
  - Piece selected with legal targets: click g1 at the start position.
  - Promotion picker: `a4 b5 axb5 a6 bxa6 Bb7 axb7 Nc6`, then click b7 and a8.
  - Check: `e4 f5 Qh5+`.
  - Game over: `f3 e5 g4 Qh4#`.
- [ ] Result: seven PNGs, plus `perf.md` with the React cold-load bytes transferred, the time to interactive, and the p50 and p95 click-to-highlight times over 20 clicks.
- [ ] Unit: N/A. This PR adds no code.
- [ ] Live: each PNG shows its named state. Pass when the owner can name each state from the image alone.
- [ ] Perf: this PR records the React baseline that P8 and P11 compare against. Use the same probe as prototype finding 7.

## P2: Delete React and its tooling

- [ ] Depends on: P1.
- [ ] Files: delete `src/frontend/` and `.claude/skills/verify/`. Edit `.githooks/pre-commit` to remove the ESLint block. Edit `.github/workflows/ci.yml` to remove the `frontend` job. Edit `.gitignore` to remove the Node and Bun entries. Edit `Makefile` to remove `install`'s Bun line, the `tsc`, `bun test` and `eslint` lines, and `run-web`. Edit `CLAUDE.md` to remove the proxy-socket gotcha and the `verify` pointer, and to say "no browser UI until P10".
- [ ] Change: one subtraction.
- [ ] Result: the repo has no Node toolchain. `make build`, `make test-backend` and `make lint` still pass.
- [ ] Unit: `make test-backend` is green.
- [ ] Live: `git ls-files | rg -i 'package\.json|bun\.lock|node_modules|\.tsx?$|vite|eslint'` prints nothing. `make run-api` serves `GET /api/games/x` with a 404.
- [ ] Perf: N/A. This PR only removes code, and CI loses a job.

## P3: `eclair` CLI core, replacing the Makefile

- [ ] Depends on: P2.
- [ ] Files: new `tools/eclair/` (an F# console app in `ChessPlatform.slnx` with no `ProjectReference`). New `eclair` wrapper at the repo root. New `tests/Eclair.Tests/`. Delete `Makefile` and `scripts/check-layers.sh`. Edit `.claude/hooks/before-stop.sh`, `.claude/hooks/after-edit.sh` (the layer call only, since Fantomas stays direct), `.githooks/pre-commit`, `.github/workflows/ci.yml`, and `CLAUDE.md`.
- [ ] Change: add `build`, `test [--unit|--infra]`, `format [--check]`, `lint`, `layers [fsproj…]`, `--json` and `--help`. The `layers` command keeps the rules as one table, and the table already lists `Contracts`, `Client` and the no-reference rule for `tools/eclair`. The wrapper rebuilds `tools/eclair` only when its sources change, then runs the built DLL.
- [ ] Result: every Makefile target has a matching `./eclair` command. The hooks, the pre-commit hook and CI each call `./eclair` in one line.
- [ ] Unit: `Eclair.Tests` runs `layers` against fixture `.fsproj` files. One fixture has an allowed reference, one has a forbidden reference, one is an unknown project, and one is a package-free layer that has a package. Each case gives the expected exit code and message.
- [ ] Live: add a forbidden `ProjectReference` from Domain to Application. `./eclair layers` exits 1 and names the rule. Revert it, and `./eclair layers` exits 0. Make a real `.fs` edit and finish a session, and confirm that the Stop hook runs `./eclair` and passes. CI is green on the PR.
- [ ] Perf: metric is wall time. On a warm wrapper, `./eclair layers` fails above 0.5 s, against a 0.04 s baseline for `scripts/check-layers.sh`. `./eclair test --unit` fails above 7.2 s, which is 6.50 s plus 10%.

## P4: `eclair env`, `run` and `db`

- [ ] Depends on: P3.
- [ ] Files: `tools/eclair/**`, `tests/Eclair.Tests/**`, `CLAUDE.md`.
- [ ] Change: add `env up`, `env down [--volumes --dry-run]`, `env doctor`, `run [--release]` and `db reset [--dry-run]`.
  - `doctor` checks five things: Docker is up, Postgres and Redis are healthy, port 5000 is owned by our Runner process, and the Runner build is newer than its sources.
  - `run` starts the Runner in the background, writes a log file, and waits until `GET /api/games/__probe` returns 404.
  - `db reset` truncates `Games` and flushes Redis.
- [ ] Result: one command takes a clean machine to a usable app. `doctor --json` explains each failure and says what to do about it.
- [ ] Unit: `Eclair.Tests` covers how `doctor` classifies faked probe results.
- [ ] Live: run `env down --volumes`, then `env up`, `run` and `doctor`. All checks pass. Kill the Runner, and `doctor` reports that port 5000 is free and names `./eclair run`. Start an unrelated listener on port 5000, and `doctor` reports a foreign owner. `db reset --dry-run` changes no rows.
- [ ] Perf: metric is the time from `run` until the app is usable. Measure the trunk baseline first, as `make run-api` until the first 404. Fail if `run` is more than 2 s slower.

## P5: `eclair game`, against today's API

- [ ] Depends on: P4.
- [ ] Files: `tools/eclair/**`, `tests/Eclair.Tests/**`.
- [ ] Change: add `game new [--vs-computer] [--white id --black id] [--moves "…"]`, `game move <id> <from><to>[promo] --as <player>`, `game show <id>` and `game watch <id>` (a SignalR subscriber that prints each `MovePlayed`). The CLI parses JSON without types.
- [ ] Result: gameplay can be debugged without a browser.
- [ ] Unit: `Eclair.Tests` covers move-argument parsing (`e2e4`, `e7e8q`, bad input).
- [ ] Live: in one shell run `game watch`, and in another run `game new --white a --black b`, then `game move e2e4 --as a`. The watcher prints the move within 2 s. `game new --vs-computer` followed by one move gets a computer reply in `game show`. An illegal move gets a 400 with the error text.
- [ ] Perf: N/A. This is a debugging tool, not on any hot path.

## P6: Domain SAN

- [ ] Depends on: trunk. It runs in parallel with P1 to P5.
- [ ] Files: new `src/backend/Domain/San.fs`, `src/backend/Domain/ChessPlatform.Domain.fsproj`, and `tests/Domain.Tests/**` (adds FsCheck).
- [ ] Change: add `San.format`, `San.parse` and `SanError`, as the design doc specifies. Add a `playMoves` test helper, and use it in place of the `play <| … |> unwrap` chains.
- [ ] Result: Domain can read and write the notation the UI shows.
- [ ] Unit: example tests cover disambiguation by file, by rank, and by both, plus `x`, `+`, `#`, both castles, `=Q` and en passant. A property test generates positions by random legal play of 0 to 80 plies and checks `parse s (format s m) = Ok m` for every legal move. FsCheck `classify` shows that each `Move` case occurs, and the test fails if any case never occurs.
- [ ] Live: replay three published games through `playMoves` and compare each final position with its published FEN: Morphy's Opera Game, Kasparov v Topalov 1999, and one game with an underpromotion.
- [ ] Perf: metric is replaying and formatting a 200-ply game. The baseline is replaying the same game with `Game.applyMove` alone. Fail if SAN adds more than 3x. P10 checks it again in WASM.

## F1: Atomic matchmaking dequeue

- [ ] Depends on: trunk. It runs in parallel.
- [ ] Files: `src/backend/Infrastructure/RedisBus.fs`, `src/backend/Application/Ports.fs` (the signature returns a pair option), `src/backend/BackgroundWorkers/MatchmakingWorker.fs`, `tests/Infra.Tests/**`, `tests/BackgroundWorkers.Tests/**`.
- [ ] Change: `DequeueMatchmakingPair` pops two distinct players in one Lua script, or none.
- [ ] Result: no lone or duplicate player is popped. P12's two-browser journey relies on this.
- [ ] Unit: worker tests cover the case with no pair.
- [ ] Live: an `Infra.Tests` case against real Redis enqueues 1, 2, 3 and duplicate players, then checks each outcome.
- [ ] Perf: N/A. One script call replaces two pops.

## P7: Contracts

- [ ] Depends on: P5.
- [ ] Files: new `src/backend/Contracts/`. Edit `src/backend/WebApi/{Handlers,Program,SignalRMoveBroadcaster,MatchmakingBroadcaster,GameHub}.fs` and the `.fsproj`. Edit `src/backend/Application/Ports.fs` to remove `MoveSummaryDto`. Edit `src/backend/Infrastructure/RedisBus.fs` to add its own message type. Edit `ChessPlatform.slnx`, `tests/WebApi.Tests/**`, `tests/Infra.Tests/**`, `tools/eclair/**` (JSON field paths only), and `tools/eclair` layers (if the table needs a change).
- [ ] Change: implement the design doc's Contracts section in full. Use `Square = { File; Rank }`, a `GameStatus` union on the wire, hub method names as constants, and one `JsonSerializerOptions`. Falco and `AddJsonProtocol` both use those options. Endpoint paths stay the same.
- [ ] Result: HTTP and SignalR use one casing and one shape, with no `Item1` and no `"Checkmate:White"`.
- [ ] Unit: `WebApi.Tests` reads every response with the Contracts types and the shared options. A SignalR test client in the test host receives `MovePlayed` and `MatchFound` as Contracts types. A test sends a malformed body and gets a 400.
- [ ] Live: rerun P5's live scenarios through `./eclair game` against the Runner, all passing. `game show` prints a `status` that is a union case, not a string with a colon in it.
- [ ] Perf: N/A. The payload shapes change, and so do the wire sizes, but no hot path does.

## P8: Client skeleton, hosting and CSS guardrails

- [ ] Depends on: P7.
- [ ] Files: new `src/client/` (Bolero 0.25.65, `InvariantGlobalization`, Elmish, HTML DSL, a `Css` module, `wwwroot/tokens.css`, `wwwroot/app.css`, `wwwroot/index.html`). New `tests/Client.Tests/`. Edit `src/backend/WebApi/ChessPlatform.WebApi.fsproj` (Web SDK, references Client and Contracts) and `src/backend/WebApi/Program.fs`. Edit `src/backend/ChessPlatform.Runner/ChessPlatform.Runner.fsproj` (Web SDK). Edit `tests/WebApi.Tests/**`, `ChessPlatform.slnx`, and `tools/eclair` layers.
- [ ] Change: the landing screen only, with an editable player id stored in `localStorage` and the board drawn from `Game.newGame ()`. Host it with `UseStaticWebAssets`, `MapStaticAssets`, and a fallback that excludes `/api` and `/hubs`.
- [ ] Result: `./eclair run` serves the Bolero landing page on :5000 in Production.
- [ ] Unit: `Client.Tests` adds the guardrail tests. One fails when markup uses a class that is not in `Css` or when the CSS defines a class that is unused. One fails on a raw colour or length outside `tokens.css`, with the allowlist from the design doc. `WebApi.Tests` asserts that `/` returns `index.html`, that `/api/unknown` returns 404, and that `/hubs/game/negotiate` is not the fallback.
- [ ] Live: in Production via `./eclair run`, `curl` returns 200 for `/` and for `/_framework/blazor.webassembly.js`, and 404 for `/api/unknown`. Reload the page, and an edited player id persists. Owner reviews a screenshot against `docs/design/reference/react/landing.png`.
- [ ] Perf: metric is cold-load bytes transferred and time to interactive, on a Release build with the prototype's probe. The baseline is React's numbers in P1's `perf.md`. Fail above 5 MB transferred, given 3.6 MB measured for the bare prototype.

## P9: `eclair verify` engine

- [ ] Depends on: P8.
- [ ] Files: `tools/eclair/**` (adds Microsoft.Playwright 1.63.0), `tests/Eclair.Tests/**`.
- [ ] Change: add `verify install` (calls Playwright's installer in-process, so no PowerShell is needed), `verify drive <journey>`, `verify screenshot <state>` and `verify diff`. Journeys and states are a table in the CLI. Each step finds elements by `data-testid`. `test --e2e` runs every journey and every diff.
- [ ] Result: agents and CI drive the browser through one command, and nobody writes a throwaway Playwright script.
- [ ] Unit: `Eclair.Tests` covers the pixel-diff function, with identical images, a one-pixel change, and different sizes.
- [ ] Live: `verify drive landing` passes against `./eclair run`. Change the landing heading by one character, and `verify diff landing` exits 1 and writes a diff image. Revert it, and the command exits 0. Run `verify install` on a machine with no browser cache.
- [ ] Perf: metric is the wall time of `verify drive landing`. No baseline exists. Record it, and fail later PRs that double it.

## P10: Client live game against the computer

- [ ] Depends on: P9 and P6.
- [ ] Files: `src/client/**`, `tests/Client.Tests/**`, `tools/eclair/**` (journeys only).
- [ ] Change: add Play Computer, a SignalR connection that reconnects automatically and rejoins its groups, a connection-status display, a latency readout through `Ping`, board sync from `GameSnapshot` and `MovePlayed`, a move ledger rebuilt by replay and `San.format`, the status banner, and Leave Game.
- [ ] Result: a player starts a game against the computer and watches it update live.
- [ ] Unit: `Client.Tests` checks that `update` applies a `MovePlayed` to the model, that the ledger shows SAN after replay, and that the status banner text matches each `GameStatus`.
- [ ] Live: `verify drive vs-computer` plays `e4` through the HTTP API and sees the computer's reply on the board and in the ledger within 5 s. Restart the Runner, and the page shows "reconnecting", then "connected", then receives the next move.
- [ ] Perf: metric is the ledger rebuild for a 200-ply game, measured in the browser. Fail above 50 ms.

## P11: Client moves

- [ ] Depends on: P10.
- [ ] Files: `src/client/**`, `tests/Client.Tests/**`, `tools/eclair/**` (journeys only).
- [ ] Change: add click-to-move with legal-target highlights from Domain, local rejection with no request, turn gating, the promotion picker with cancel, the typed SAN command with an error message, last-move and check highlights, and Flip Perspective.
- [ ] Result: every move interaction runs in the browser without a round trip.
- [ ] Unit: `Client.Tests` checks that selecting a piece yields Domain's legal targets, that a click on a target emits exactly one move request, that an illegal click emits none, that nothing is selectable when it is not your turn, and that the promotion flow and its cancel work. A typed `Nf3` emits the move, and a typed `Nf9` shows an error.
- [ ] Live: verify journeys cover castling on both sides, en passant, promotion to a knight, and check. A request counter shows that an illegal attempt sends zero requests.
- [ ] Perf: metric is click-to-highlight p50 over 20 clicks. The baseline is React's number in P1's `perf.md`. Fail above 100 ms or above React's p50, whichever is lower. The prototype measured 33 ms.

## P12: Client matchmaking and game end

- [ ] Depends on: P11 and F1.
- [ ] Files: `src/client/**`, `tests/Client.Tests/**`, `tools/eclair/**` (journeys only).
- [ ] Change: add Find Opponent with cancel, `MatchFound` leading into a game, the colour indicator, the game-over overlay, and Reset and Play Again against the computer.
- [ ] Result: the design doc's feature-parity checklist is complete.
- [ ] Unit: `Client.Tests` covers the queue state transitions and the game-over overlay's actions.
- [ ] Live: in a two-context verify journey, both players queue and get matched. White plays `f3`, Black `e5`, White `g4` and Black `Qh4#`, each move arriving live. Both players see the overlay, and Play Again starts a new game against the computer. Check each item on the parity checklist against a journey step.
- [ ] Perf: metric is the time from the second player queueing to both boards showing. Record it. Fail above 3 s, given that the worker polls.

## P13: `verify-eclair` skill

- [ ] Depends on: P12.
- [ ] Files: new `.claude/skills/verify-eclair/**`. Edit `CLAUDE.md`.
- [ ] Change: run `/pstack:create-verification-skill`. The skill needs sections for Launch, Doctor, Drive, Evidence, Cleanup and Helpers. Add a `features/` file for each item on the parity checklist. Every step is an `./eclair` command.
- [ ] Result: an agent verifies any feature from the skill alone.
- [ ] Unit: N/A. This PR is prose that drives the CLI.
- [ ] Live: a fresh subagent with only the skill verifies three features end to end and returns evidence, as `create-verification-skill` requires.
- [ ] Perf: N/A.

## P14: Screenshot baselines and the CI gate

- [ ] Depends on: P13.
- [ ] Files: new `tests/baselines/*.png`. Edit `.github/workflows/ci.yml` (new `e2e` job), `tools/eclair/**` if a CI fix is needed, and `CLAUDE.md`.
- [ ] Change: the owner approves the Bolero look against P1's references. The approved screenshots become the baselines. A CI job runs `env up`, `verify install`, `run` and `test --e2e`, and uploads diff images when it fails. The owner makes the job required in branch protection.
- [ ] Result: any visual or gameplay regression fails a PR.
- [ ] Unit: N/A.
- [ ] Live: a throwaway PR that changes one colour token fails the `e2e` job and shows the diff artifact. Closing it leaves `main` green.
- [ ] Perf: metric is the wall time of the `e2e` job. Record it. Fail above 10 minutes.

## P15: Open-source docs

- [ ] Depends on: P14.
- [ ] Files: new `README.md`, `CONTRIBUTING.md`, `CODE_OF_CONDUCT.md` and `LICENSE`. Edit `CLAUDE.md` to remove the active-work pointer. Delete `docs/design/reference/react/`, which git history keeps.
- [ ] Change: write the contributor docs from the design doc's last section. The license is MIT.
- [ ] Result: a stranger can clone the repo, run `./eclair env up && ./eclair run`, and play.
- [ ] Unit: N/A.
- [ ] Live: in a fresh clone on a clean checkout, follow only the README quickstart until a game against the computer is in progress.
- [ ] Perf: N/A.

## Follow-ups outside the critical path

Each gets its own section when scheduled. None blocks the migration.

- Make `Position` into File and Rank types in Domain. Contracts already uses `Square`.
- Make `HistoryJson.toHistory` store move kinds. The ledger replays from/to, so it does not need this.
- Replace `ex.SqlState = "23505"` with `PostgresErrorCodes.UniqueViolation`.
- Remove the dead `folder` in `FenParser.parsePiecePlacement`.
- Fix `isKingInCheck` treating no king as not in check.
- Fix the side-to-move naming in `detectGameStatus` and the `InProgress` placeholder.
- Remove the dead bindings in Domain tests, and add a real "cannot castle through check" test.
- Fix the flaky "RedisBus Pub/Sub" test, which uses a fixed sleep and an unsynchronized flag.
- Replace `RandomEngine`.

## Not yet proven

- The first-load size of the full client, as opposed to the bare prototype.
- SignalR reconnects in WASM after a Runner restart.
- How long the CI `e2e` job takes on GitHub runners, and how Playwright installs browsers there.
- Whether `UseStaticWebAssets` in Production behaves the same after `dotnet publish` as in a build output. The prototype checked both, but only for a minimal app.
