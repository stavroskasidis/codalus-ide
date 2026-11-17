#!/usr/bin/env bash
# What the pipeline's jobs would otherwise fetch from nuget.org on every run, restored into ~/.nuget/packages before
# develop's nightly saves it as the NuGet cache (pipeline.yml): restoring Codalus.slnx alone left out
#   - the smoke job's Native AOT publish for this runner's RID (its runtime packs), and
#   - the local tools of the samples the tests open (samples/Scaffolding's dotnet-aspnet-codegenerator,
#     samples/Data's dotnet-ef), which `dotnet tool restore` finds in the packages folder without a source.
# A macOS runner that couldn't reach nuget.org ("No route to host (api.nuget.org:443)") failed the smoke's publish
# (run 40886959558) and WebToolsShellTests' tool restore (run 41606373671) (docs/ci-flakes.md).
# Usage: eng/warm-nuget-cache.sh   (from the repository's root)
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/telemetry-optout.sh"

rid="$(dotnet --info | awk '/RID:/ {print $2; exit}')"
echo "== the shell's Native AOT publish for $rid"
dotnet restore src/Codalus.Shell -r "$rid"
# The restore for the RID rewrote the projects' assets files: the job's tests run on the solution's own restore.
dotnet restore Codalus.slnx

for manifest in samples/*/.config/dotnet-tools.json; do
  folder="$(dirname "$(dirname "$manifest")")"
  echo "== the local tools of $folder"
  (cd "$folder" && dotnet tool restore)
done
