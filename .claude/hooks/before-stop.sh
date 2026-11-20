#!/usr/bin/env bash
# Stop: if F# or project files changed, require layer check + build + unit tests to pass.
set -uo pipefail
cd "${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel)}"

input=$(cat)
session=$(printf '%s' "$input" | sed -nE 's/.*"session_id"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' | head -n1)
state="${TMPDIR:-/tmp}/eclair-stop-${session:-default}"

base=$(git merge-base HEAD origin/main 2>/dev/null || git rev-parse HEAD)
paths=(':(glob)**/*.fs' ':(glob)**/*.fsi' ':(glob)**/*.fsproj' 'Directory.Build.props' 'ChessPlatform.slnx')
changes=$( { git diff "$base" -- "${paths[@]}"; git ls-files --others --exclude-standard -- "${paths[@]}" | xargs cat 2>/dev/null; } )
[ -z "$changes" ] && exit 0

hash=$(printf '%s' "$changes" | git hash-object --stdin)
[ "$(cat "$state.green" 2>/dev/null)" = "$hash" ] && exit 0

if ! command -v dotnet >/dev/null; then
  echo "before-stop: .NET SDK not installed here; build and tests were NOT run. CI will run them." >&2
  exit 0
fi

log=$(mktemp)
if scripts/check-layers.sh >"$log" 2>&1 && dotnet build ChessPlatform.slnx >>"$log" 2>&1 && make test-backend >>"$log" 2>&1; then
  echo "$hash" > "$state.green"; rm -f "$state.tries" "$log"; exit 0
fi

tries=$(( $(cat "$state.tries" 2>/dev/null || echo 0) + 1 ))
echo "$tries" > "$state.tries"
if [ "$tries" -ge 3 ]; then
  rm -f "$state.tries"
  echo "before-stop: still failing after 3 attempts; stopping anyway. Tell the user the build or tests are red." >&2
  tail -n 40 "$log" >&2; rm -f "$log"; exit 0
fi
echo "Build, layer check or unit tests failed (attempt $tries of 3). Fix this before finishing:" >&2
grep -E "error|Failed|FAIL|check-layers" "$log" | head -n 40 >&2 || tail -n 40 "$log" >&2
rm -f "$log"; exit 2
