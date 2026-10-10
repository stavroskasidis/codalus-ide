#!/usr/bin/env bash
# Runs the programs of samples/TargetFrameworks on .NET Core 2.0 to .NET 5 in containers of the Linux distributions
# those runtimes supported (M33, ADR 0030), and on a current distribution to show what it lacks.
#
# Usage: eng/compat-containers.sh            (Linux, with docker or podman; WSL works with rootless podman)
#
# Current distributions (Ubuntu 22.04 and later) have OpenSSL 3 and an ICU newer than these runtimes know. Daedalus
# Studio sets CLR_ICU_VERSION_OVERRIDE for .NET Core 2.x to 3.1 when it runs them (the test suite covers that on the
# host); OpenSSL 1.x, which .NET Core 2.x to .NET 5 need for cryptography and HTTPS, has no such switch. This script proves the
# programs themselves on the distributions of their time, with what those distributions bring:
#   - Ubuntu 18.04 (ICU 60, OpenSSL 1.1): .NET Core 2.0 to 3.1 and ASP.NET Core 2.1 and 3.1
#   - Ubuntu 20.04 (ICU 66, OpenSSL 1.1): .NET Core 3.1 and .NET 5
#   - Ubuntu 24.04 (ICU 74, OpenSSL 3): what fails without help (ICU, OpenSSL), and the ICU override
# It builds the fixture on the host with the repository's SDK, installs the old runtimes into
# .work/compat-containers/dotnet (their own root: the current host needs a newer glibc than Ubuntu 18.04 has), and
# mounts both read-only. Results go to stdout and artifacts/compat-containers.md; it fails when an expectation fails.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
work="$repo/.work/compat-containers"
results="$repo/artifacts/compat-containers.md"
engine="${CONTAINER_ENGINE:-$(command -v podman || command -v docker || true)}"
[ -n "$engine" ] || { echo "Neither podman nor docker is installed." >&2; exit 2; }

runtimes=(5.0.17 3.1.32 3.0.3 2.2.8 2.1.30 2.0.9)
aspnetcore=(3.1.32 2.1.30)

mkdir -p "$work" "$(dirname "$results")"
export DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1

echo "== runtimes for the containers ($work/dotnet)"
[ -f "$work/dotnet-install.sh" ] || curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$work/dotnet-install.sh"
for version in "${runtimes[@]}"; do
  [ -d "$work/dotnet/shared/Microsoft.NETCore.App/$version" ] || bash "$work/dotnet-install.sh" --runtime dotnet --version "$version" --install-dir "$work/dotnet" --no-path $( [ -x "$work/dotnet/dotnet" ] && echo --skip-non-versioned-files ) > /dev/null
done
for version in "${aspnetcore[@]}"; do
  [ -d "$work/dotnet/shared/Microsoft.AspNetCore.App/$version" ] || bash "$work/dotnet-install.sh" --runtime aspnetcore --version "$version" --install-dir "$work/dotnet" --no-path --skip-non-versioned-files > /dev/null
done

echo "== build samples/TargetFrameworks with $(dotnet --version)"
rm -rf "$work/samples"
mkdir -p "$work/samples"
cp "$repo/samples/Directory.Build.props" "$repo/samples/Directory.Packages.props" "$work/samples/"
sdk="$(sed -n 's/.*"version": *"\([^"]*\)".*/\1/p' "$repo/global.json" | head -1)"
printf '{ "sdk": { "version": "%s", "rollForward": "latestFeature" } }\n' "$sdk" > "$work/samples/global.json"
cp -r "$repo/samples/TargetFrameworks" "$work/samples/"
rm -rf "$work/samples/TargetFrameworks/Sdks"
dotnet build "$work/samples/TargetFrameworks/TargetFrameworks.sln" -c Release -nologo -v q

out="/fixture/TargetFrameworks"
app() { echo "$out/$1/$1.App/bin/Release/$2/$1.App.dll"; }

# One container run: image, then a shell script run inside with /dotnet and /fixture mounted.
run() {
  local image="$1" script="$2"
  "$engine" run --rm --pull=missing -v "$work/dotnet:/dotnet:ro" -v "$work/samples:/fixture:ro" -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    -e TARGETFRAMEWORKS_CRYPTO=1 "$image" bash -c "$script" 2>&1
}

failures=0
{
  echo "# Old runtimes in containers"
  echo
  echo "$(date -u +%Y-%m-%dT%H:%MZ), $("$engine" --version), samples/TargetFrameworks built with the .NET SDK $(dotnet --version)."
  echo
  echo "| Distribution | Program | Expected | Result |"
  echo "|---|---|---|---|"
} > "$results"

# expect <distribution> <image> <label> <expected text> <command>
expect() {
  local distribution="$1" image="$2" label="$3" expected="$4" command="$5" output result
  output="$(run "$image" "$command" || true)"
  if grep -qF -- "$expected" <<< "$output"; then
    result=pass
  else
    result=FAIL
    failures=$((failures + 1))
  fi

  echo "[$result] $distribution: $label (expected '$expected')"
  [ "$result" = pass ] || sed 's/^/    /' <<< "$output" | head -20
  echo "| $distribution | $label | \`$expected\` | $result |" >> "$results"
}

# A web app: start it, request / through bash's /dev/tcp (the runtime-deps images have no curl), stop it.
web() {
  local dll="$1" port="$2"
  echo "cd \$(dirname $dll) && ASPNETCORE_URLS=http://localhost:$port /dotnet/dotnet $dll > /tmp/web.log 2>&1 & for i in \$(seq 1 60); do grep -q 'Now listening' /tmp/web.log && break; sleep 0.5; done; exec 3<>/dev/tcp/localhost/$port && printf 'GET / HTTP/1.0\r\nHost: localhost\r\n\r\n' >&3 && cat <&3; cat /tmp/web.log | head -3"
}

bionic=mcr.microsoft.com/dotnet/runtime-deps:3.1-bionic
focal=mcr.microsoft.com/dotnet/runtime-deps:5.0-focal
noble=mcr.microsoft.com/dotnet/runtime-deps:8.0-noble

for pair in Core20:netcoreapp2.0:2.0 Core21:netcoreapp2.1:2.1 Core22:netcoreapp2.2:2.2 Core30:netcoreapp3.0:3.0 Core31:netcoreapp3.1:3.1; do
  IFS=: read -r id tfm version <<< "$pair"
  expect "Ubuntu 18.04" "$bionic" "$id.App" "runtime=$version." "/dotnet/dotnet $(app "$id" "$tfm")"
  expect "Ubuntu 18.04" "$bionic" "$id.App, SHA-256 (OpenSSL 1.1)" "sha256=32" "/dotnet/dotnet $(app "$id" "$tfm")"
done
expect "Ubuntu 18.04" "$bionic" "Web21 (ASP.NET Core 2.1)" "Web21 on 2.1." "$(web "$out/Web21/bin/Release/netcoreapp2.1/Web21.dll" 5021)"
expect "Ubuntu 18.04" "$bionic" "Web31 (ASP.NET Core 3.1)" "Web31 on 3.1." "$(web "$out/Web31/bin/Release/netcoreapp3.1/Web31.dll" 5031)"

for pair in Core31:netcoreapp3.1:3.1 Net50:net5.0:5.0; do
  IFS=: read -r id tfm version <<< "$pair"
  expect "Ubuntu 20.04" "$focal" "$id.App, SHA-256" "sha256=32" "/dotnet/dotnet $(app "$id" "$tfm")"
done

# A current distribution: ICU 74 and OpenSSL 3.
expect "Ubuntu 24.04" "$noble" "Core21.App without help" "Couldn't find a valid ICU package" "/dotnet/dotnet $(app Core21 netcoreapp2.1)"
expect "Ubuntu 24.04" "$noble" "Core21.App, CLR_ICU_VERSION_OVERRIDE=74" "runtime=2.1." "CLR_ICU_VERSION_OVERRIDE=74 TARGETFRAMEWORKS_CRYPTO=0 /dotnet/dotnet $(app Core21 netcoreapp2.1)"
expect "Ubuntu 24.04" "$noble" "Core21.App, SHA-256 (no OpenSSL 1.x)" "No usable version of" "CLR_ICU_VERSION_OVERRIDE=74 /dotnet/dotnet $(app Core21 netcoreapp2.1)"
expect "Ubuntu 24.04" "$noble" "Core31.App, CLR_ICU_VERSION_OVERRIDE=74" "runtime=3.1." "CLR_ICU_VERSION_OVERRIDE=74 TARGETFRAMEWORKS_CRYPTO=0 /dotnet/dotnet $(app Core31 netcoreapp3.1)"
expect "Ubuntu 24.04" "$noble" "Core31.App, SHA-256 (no OpenSSL 1.x)" "No usable version of" "CLR_ICU_VERSION_OVERRIDE=74 /dotnet/dotnet $(app Core31 netcoreapp3.1)"
expect "Ubuntu 24.04" "$noble" "Net50.App" "runtime=5.0." "TARGETFRAMEWORKS_CRYPTO=0 /dotnet/dotnet $(app Net50 net5.0)"
expect "Ubuntu 24.04" "$noble" "Net50.App, SHA-256 (no OpenSSL 1.x)" "No usable version of" "/dotnet/dotnet $(app Net50 net5.0)"

echo
echo "== $results"
[ "$failures" -eq 0 ] || { echo "$failures expectation(s) failed." >&2; exit 1; }
