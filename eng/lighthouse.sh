#!/usr/bin/env bash
# Lighthouse on every page of an exported website (M50): performance, accessibility, best practices and SEO must each
# score at least the minimum (95 by default) on Lighthouse's mobile defaults, or this fails, listing what each page got
# (a page under it is judged by the median of three runs).
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

# The folder served as GitHub Pages serves it: text compressed with gzip when the browser asks for it.
cat > "$out/serve.py" <<'PY'
import gzip, http.server, os, sys

TEXT = (".html", ".css", ".js", ".json", ".svg", ".xml", ".txt")

class Handler(http.server.SimpleHTTPRequestHandler):
    def do_GET(self):
        path = self.translate_path(self.path)
        if os.path.isdir(path) and self.path.split("?")[0].endswith("/"):
            path = os.path.join(path, "index.html")
        if not path.endswith(TEXT) or not os.path.isfile(path) or "gzip" not in self.headers.get("Accept-Encoding", ""):
            return super().do_GET()
        with open(path, "rb") as f:
            body = gzip.compress(f.read())
        self.send_response(200)
        self.send_header("Content-Type", self.guess_type(path))
        self.send_header("Content-Encoding", "gzip")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *args):
        pass

port, root = int(sys.argv[1]), sys.argv[2]
http.server.ThreadingHTTPServer(("127.0.0.1", port), lambda *a: Handler(*a, directory=root)).serve_forever()
PY
python3 -I "$out/serve.py" "$port" "$site" > "$out/server.log" 2>&1 &
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

# Lighthouse's scores vary from run to run (its own advice: take the median of several). A page under the minimum is
# run twice more and judged by each category's median of the three.
judge() {
  python3 - "$minimum" "$1" "$@" <<'PY'
import json, statistics, sys
minimum, page, reports = int(sys.argv[1]), sys.argv[2], sys.argv[4:]
runs = []
for path in reports:
    try:
        categories = json.load(open(path))["categories"]
        runs.append([round((categories[c]["score"] or 0) * 100) for c in ("performance", "accessibility", "best-practices", "seo")])
    except (OSError, ValueError, KeyError):
        pass
if not runs:
    print(f"{page:<40} no report"); sys.exit(1)
scores = [round(statistics.median(column)) for column in zip(*runs)]
note = f"  (median of {len(runs)} runs)" if len(runs) > 1 else ""
print(f"{page:<40} " + " ".join(f"{s:5d}" for s in scores) + note)
sys.exit(1 if min(scores) < minimum else 0)
PY
}

run() {
  npx --yes "lighthouse@$version" "http://127.0.0.1:$port/$1" --quiet --output=json --output-path="$2" \
    --only-categories=performance,accessibility,best-practices,seo --chrome-flags="--headless=new --no-sandbox" > /dev/null 2>&1 || true
}

failed=0
printf '%-40s %5s %5s %5s %5s\n' page perf a11y best seo
for page in "${pages[@]}"; do
  name="$out/$(echo "${page:-home}" | tr '/' '_')"
  run "$page" "$name.json"
  if ! line="$(judge "${page:-/}" "$name.json")"; then
    run "$page" "$name.2.json"
    run "$page" "$name.3.json"
    line="$(judge "${page:-/}" "$name.json" "$name.2.json" "$name.3.json")" || failed=1
  fi
  echo "$line"
done

if [[ "$failed" -ne 0 ]]; then
  echo "A page scored under $minimum in Lighthouse (reports: $out)." >&2
  exit 1
fi
echo "Every page scored $minimum or more."
