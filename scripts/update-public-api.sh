#!/usr/bin/env bash
# Appends every public symbol the build reports as undeclared (RS0016) to the project's
# PublicAPI.Unshipped.txt. Run it after adding public API, then review the diff.
#   scripts/update-public-api.sh src/Sonde.Probe/Sonde.Probe.csproj
set -euo pipefail
project="${1:?usage: $0 <path/to/project.csproj>}"
dir="$(dirname "$project")"
file="$dir/PublicAPI.Unshipped.txt"
[ -f "$file" ] || printf '#nullable enable\n' > "$file"
dotnet build "$project" --nologo -v q 2>&1 \
  | grep -o "RS0016: Symbol '[^']*' is not part of the declared public API" \
  | sed "s/RS0016: Symbol '//; s/' is not part of the declared public API//" \
  | sort -u > "$file.new" || true
if [ -s "$file.new" ]; then
  while IFS= read -r line; do
    grep -qxF -- "$line" "$file" || printf '%s\n' "$line" >> "$file"
  done < "$file.new"
fi
rm -f "$file.new"
echo "$file: $(($(wc -l < "$file") - 1)) symbols"
