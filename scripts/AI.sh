#!/usr/bin/env bash
# Dump the repo as one markdown blob for a bot with no filesystem access.
#
#   ./scripts/AI.sh                                   # whole repo
#   ./scripts/AI.sh src/Industrial.Sensor.EdgeGateway # just one project
#   ./scripts/AI.sh src/Protos docs                   # several paths
#
# Writes to AI.md (gitignored) and copies to the clipboard if one is available.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"   # works from any subdirectory
OUT="AI.md"

get_syntax() {
  case "$1" in
    infra/*|*.tf|*.tfvars)             echo "hcl" ;;
    chart/*|monitoring/*|*.yaml|*.yml) echo "yaml" ;;
    *.cs)                              echo "csharp" ;;
    *.tsx|*.ts)                        echo "typescript" ;;
    *.jsx|*.js)                        echo "javascript" ;;
    *.css)                             echo "css" ;;
    *.json|.*rc)                       echo "json" ;;
    *.csproj|*.slnx|*.xml)             echo "xml" ;;
    *.md)                              echo "markdown" ;;
    *Dockerfile*)                      echo "dockerfile" ;;
    Makefile|*.mk)                     echo "makefile" ;;
    *Tiltfile*)                        echo "starlark" ;;
    *.http)                            echo "http" ;;
    *.sh|*.bash)                       echo "bash" ;;
    *.sql)                             echo "sql" ;;
    *.proto)                           echo "proto" ;;
    *)                                 echo "text" ;;
  esac
}

# --others includes untracked files so work-in-progress is visible too.
# --exclude-standard still honours .gitignore.
files=$(git ls-files --cached --others --exclude-standard -- "${@:-.}" \
  | grep -E '\.(cs|csproj|slnx|ts|tsx|js|jsx|css|json|yaml|yml|tf|tfvars|http|proto|sql|md|sh)$|Dockerfile|Makefile|Tiltfile' \
  | grep -vE 'package-lock.json|pnpm-lock.yaml|yarn.lock|node_modules/|\.lscache$|Migrations/.*Designer\.cs$|ModelSnapshot\.cs$' \
  | sort)

[ -z "$files" ] && { echo "No matching files."; exit 1; }

{
  # A manifest first. A model reading a flat dump orients far better when it can see
  # the shape of the repo before the contents scroll past.
  printf '# %s\n\n' "$(basename "$PWD")"
  printf '_%s files, generated %s_\n\n' "$(echo "$files" | wc -l | tr -d ' ')" "$(date -u '+%Y-%m-%dT%H:%MZ')"
  printf '## Manifest\n\n```\n%s\n```\n' "$files"

  echo "$files" | while read -r file; do
    [ -f "$file" ] || continue
    printf '\n### File: %s\n```%s\n' "$file" "$(get_syntax "$file")"
    cat "$file"
    printf '\n```\n'
  done
} > "$OUT"

bytes=$(wc -c < "$OUT" | tr -d ' ')

# ~4 bytes per token is close enough to know whether this will fit in a context window.
printf 'Wrote %s (%s KB, roughly %s tokens)\n' \
  "$OUT" "$((bytes / 1024))" "$((bytes / 4))"

if   command -v pbcopy  >/dev/null 2>&1; then pbcopy  < "$OUT"; echo "Copied to clipboard."
elif command -v wl-copy >/dev/null 2>&1; then wl-copy < "$OUT"; echo "Copied to clipboard."
elif command -v xclip   >/dev/null 2>&1; then xclip -selection clipboard < "$OUT"; echo "Copied to clipboard."
else echo "No clipboard tool found -- attach $OUT directly."
fi

# Clipboards silently truncate large payloads. Past ~200 KB, attach the file instead.
[ "$bytes" -gt 200000 ] && echo "WARNING: large dump. Attach $OUT as a file rather than pasting, or scope it: ./scripts/AI.sh src/Industrial.Sensor.EdgeGateway"
