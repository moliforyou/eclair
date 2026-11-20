#!/usr/bin/env bash
# PostToolUse(Edit|Write): format F# files, check layer rules on project files.
set -uo pipefail
cd "${CLAUDE_PROJECT_DIR:-$(git rev-parse --show-toplevel)}"

input=$(cat)
file=$(printf '%s' "$input" | sed -nE 's/.*"file_path"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' | head -n1)
[ -z "$file" ] && exit 0

case "$file" in
  *.fs|*.fsi|*.fsx)
    command -v dotnet >/dev/null || exit 0
    if ! out=$(dotnet fantomas "$file" 2>&1); then
      echo "Fantomas could not format $file (usually a syntax error):" >&2
      echo "$out" | tail -n 20 >&2
      exit 2
    fi ;;
  *.fsproj)
    if ! scripts/check-layers.sh "$file"; then
      echo "Revert this reference. Layer rules live in scripts/check-layers.sh." >&2
      exit 2
    fi ;;
esac
exit 0
