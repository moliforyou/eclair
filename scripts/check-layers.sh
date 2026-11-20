#!/usr/bin/env bash
# Allowed project references per layer. Test projects are exempt.
set -euo pipefail
cd "$(dirname "$0")/.."

allowed_refs() {
  case "$1" in
    Domain) echo "" ;;
    Application) echo "Domain" ;;
    Infrastructure) echo "Domain Application" ;;
    WebApi) echo "Domain Application Infrastructure" ;;
    BackgroundWorkers) echo "Domain Application Infrastructure" ;;
    Runner) echo "WebApi BackgroundWorkers" ;;
    *) echo "UNKNOWN" ;;
  esac
}

package_free="Domain Application"
status=0
[ $# -eq 0 ] && set -- $(git ls-files 'src/*.fsproj')

for proj in "$@"; do
  case "$proj" in tests/*|*/tests/*) continue ;; esac
  layer=$(basename "$proj" .fsproj); layer=${layer#ChessPlatform.}
  allowed=$(allowed_refs "$layer")
  if [ "$allowed" = "UNKNOWN" ]; then
    echo "check-layers: $proj is not in scripts/check-layers.sh; add its allowed references there." >&2
    status=1; continue
  fi
  while read -r ref; do
    [ -z "$ref" ] && continue
    target=$(basename "${ref//\\//}" .fsproj); target=${target#ChessPlatform.}
    case " $allowed " in *" $target "*) ;; *)
      echo "check-layers: $layer may not reference $target ($proj)." >&2; status=1 ;;
    esac
  done < <(grep -oE 'ProjectReference Include="[^"]+"' "$proj" | sed -E 's/.*Include="([^"]+)"/\1/')
  case " $package_free " in *" $layer "*)
    if grep -q '<PackageReference' "$proj"; then
      echo "check-layers: $layer must stay free of NuGet packages ($proj)." >&2; status=1
    fi ;;
  esac
done
exit $status
