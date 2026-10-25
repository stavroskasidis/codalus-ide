#!/usr/bin/env bash
# Lighthouse on every page of an exported website (M50): performance, accessibility, best practices and SEO must each
# score at least the minimum (95 by default) on Lighthouse's mobile defaults, or this fails, listing what each page got.
# Usage: eng/lighthouse.sh <exported-site-dir> [minimum]
# Needs Node.js (npx) and Chrome or Chromium (CHROME_PATH, else the one Lighthouse finds). The site is served from the
# folder on a local port, so it must be exported with --base-url at its root ("/" as its base path).
set -euo pipefail
site="$(cd "${1:?the folder of the exported site}" && pwd)"
minimum="${2:-95}"
version=12.8.2
port="${LIGHTHOUSE_PORT:-8765}"
out="$(mktemp -d)"
cd "$(dirname "$0")/.."

python3 -m http.server "$port" --bind 127.0.0.1 --directory "$site" > "$out/server.log" 2>&1 &
server=$!
trap 'kill "$server" 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do curl -fs -o /dev/null "http://127.0.0.1:$port/" && break; sleep 0.2; done

# Every page is a folder's index.html (404.html is the not-found page).
pages=()
while IFS= read -r file; do
  page="${file#"$site"/}"
  page="${page%index.html}"
  pages+=("$page")
done < <(find "$site" -name index.html | sort)

failed=0
printf '%-40s %5s %5s %5s %5s\n' page perf a11y best seo
for page in "${pages[@]}"; do
  report="$out/$(echo "${page:-home}" | tr '/' '_').json"
  npx --yes "lighthouse@$version" "http://127.0.0.1:$port/$page" --quiet --output=json --output-path="$report" \
    --only-categories=performance,accessibility,best-practices,seo --chrome-flags="--headless=new --no-sandbox" > /dev/null 2>&1 || true
  line="$(python3 - "$report" "$minimum" "${page:-/}" <<'PY'
import json, sys
path, minimum, page = sys.argv[1], int(sys.argv[2]), sys.argv[3]
try:
    categories = json.load(open(path))["categories"]
except (OSError, ValueError, KeyError):
    print(f"{page:<40} no report"); sys.exit(1)
scores = [round((categories[c]["score"] or 0) * 100) for c in ("performance", "accessibility", "best-practices", "seo")]
print(f"{page:<40} " + " ".join(f"{s:5d}" for s in scores))
sys.exit(1 if min(scores) < minimum else 0)
PY
)" || failed=1
  echo "$line"
done

if [[ "$failed" -ne 0 ]]; then
  echo "A page scored under $minimum in Lighthouse (reports: $out)." >&2
  exit 1
fi
echo "Every page scored $minimum or more."
