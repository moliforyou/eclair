#!/usr/bin/env bash
# PostToolUse(Edit|Write): format F# files, check layer rules on project files.
set -uo pipefail

input=$(cat)
file=$(printf '%s' "$input" | sed -nE 's/.*"file_path"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' | head -n1)
[ -z "$file" ] && exit 0

checkout=$(git -C "$(dirname "$file")" rev-parse --show-toplevel 2>/dev/null) || exit 0
[ -x "$checkout/eclair" ] || exit 0
command -v dotnet >/dev/null || exit 0
cd "$checkout"

case "$file" in
  *.fs|*.fsi|*.fsx)
    if ! out=$(dotnet fantomas "$file" 2>&1); then
      echo "Fantomas could not format $file (usually a syntax error):" >&2
      echo "$out" | tail -n 20 >&2
      exit 2
    fi ;;
  *.fsproj)
    ./eclair layers "$file" || exit 2 ;;
esac
exit 0
