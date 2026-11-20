# React reference: screenshots and perf baseline

These are the React client's reference images and perf numbers. P8 and P11 compare the Bolero client against them, and P15 deletes this folder. Everything was measured on 2026-11-20 against trunk `eac7504`, on the owner's Mac with 10 cores and a load average of 1.5 to 2.3.

## Screenshots

Each image is a full-page capture at 1280 px wide, taken from White's page in headless Chromium 1243 with CSS animations finished. Two browser contexts, `alice` and `bob`, queue with **Find Opponent** and play by typing SAN into the command bar. Landing and queued come from `alice`'s page before the match.

| File | State |
|---|---|
| `landing.png` | Landing, connected |
| `queued.png` | `alice` in the matchmaking queue |
| `mid-game.png` | After `e4 e5 Nf3 Nc6` |
| `piece-selected.png` | Start position, g1 clicked, f3 and h3 highlighted |
| `promotion-picker.png` | After `a4 b5 axb5 a6 bxa6 Bb7 axb7 Nc6`, b7 then a8 clicked |
| `check.png` | After `e4 f5 Qh5+` |
| `game-over.png` | After `f3 e5 g4 Qh4#` |

React shows no last-move highlight for moves typed into the command bar, and the latency readout shows `—` until its first 3 s ping. The images record that behaviour as it is.

## Perf setup

- Build. `bunx vite build`, served by `bunx vite preview`, which gzips JS and CSS and proxies `/api` and `/hubs` to `ChessPlatform.Runner` on :5000. The dev server was not measured, because it serves unbundled debug modules.
- Probe. The prototype's Microsoft.Playwright 1.63.0 probe from finding 7, with React selectors. Each run uses a fresh browser context, so the cache is cold.
- Bytes. Response body plus header bytes for every request that finished before network idle.
- Time to interactive. From `page.GotoAsync` until **Find Opponent** is visible.
- Click to highlight. After **VS Neural Engine**, click g1 and wait until f3 is a legal target. Each run times 20 clicks and deselects between them without timing it.
- In-page click. A capture-phase listener takes the click event's `timeStamp`. A `MutationObserver` records when f3 becomes a legal target, and a `requestAnimationFrame` callback records the next frame.

## Results

| Metric | Result | Runs |
|---|---|---|
| Cold-load bytes | 219,280 B over 8 requests | 11 runs, range 219,278 to 219,282 B |
| Of which Google Fonts, 3 woff2 files and 1 CSS file | 104,503 B | Same in every run |
| Same-origin HTML, JS and CSS at brotli quality 11 | 96,404 B | Computed from the build output |
| Time to interactive | Median 132.5 ms, range 121 to 163 ms | 10 runs, plus one outlier at 510 ms |
| Click to highlight, Playwright | p50 33 ms, p95 34 to 35 ms | 11 runs of 20 clicks |
| Click to highlight, in page, DOM updated | p50 0.5 to 0.6 ms, p95 0.8 to 1.8 ms | 6 runs of 20 clicks |
| Click to highlight, in page, next frame | p50 15.4 to 15.8 ms, p95 15.8 to 16.1 ms | 6 runs of 20 clicks |

No request failed and no response had a status of 400 or above in any run.

## Reading the numbers

- The Playwright click p50 of 33 ms is the probe's floor, not React's cost. React updates the DOM in under 1 ms, and the highlight paints on the next frame at 60 Hz. The bare Bolero prototype also measured 33 ms. A P11 gate of "at or below React's p50" with this probe compares two floors, so P11 should gate on the in-page numbers.
- The Runner serves Bolero assets as brotli, and `vite preview` serves React as gzip. For a like-for-like byte comparison, use React's 96,404 B of brotli same-origin assets. Add the 104,503 B of fonts if the Bolero client keeps Google Fonts. The bare prototype's 3.61 to 3.77 MB is about 17 times React's 219,280 B.
- The time-to-interactive limiter was not profiled. The 510 ms outlier is unexplained, and it did not recur in the 10 other runs.
