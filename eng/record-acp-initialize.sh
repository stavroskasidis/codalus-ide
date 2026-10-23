#!/usr/bin/env bash
# Records an agent template's answers for the tests (ADR 0083): what the pinned ACP agent answers to `initialize` with
# Codalus's exact client capabilities, and to `session/new` while signed out, into
# tests/Shared/AcpRecordings/<template>-<version>-{initialize,session-new-signed-out}.json.
#
# Manual only, never in CI: it downloads the agent with npx and runs it. It runs signed out and without touching your
# own sign-ins: HOME, USERPROFILE, XDG_CONFIG_HOME, CODEX_HOME, CLAUDE_CONFIG_DIR and GEMINI_CLI_HOME point at an empty
# temporary folder, and SSH_*, NO_BROWSER and the providers' key variables are unset (--ssh sets SSH_CONNECTION, to
# record Claude's remote sign-in). Nothing it records holds a secret; read the files before committing them.
#
# Usage: eng/record-acp-initialize.sh <claude|codex|gemini|copilot|pi> [--ssh]
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
template="${1:?usage: eng/record-acp-initialize.sh <claude|codex|gemini|copilot|pi> [--ssh]}"
ssh="${2:-}"

# The pinned packages, as src/Codalus.Workbench/Ai/Acp/AcpAgentCatalog.cs has them.
case "$template" in
  claude) package="@agentclientprotocol/claude-agent-acp"; version="0.94.0"; args=() ;;
  codex) package="@agentclientprotocol/codex-acp"; version="2.2.2"; args=() ;;
  gemini) package="@google/gemini-cli"; version="0.65.0"; args=(--acp) ;;
  copilot) package="@github/copilot"; version="1.0.103"; args=(--acp) ;;
  pi) package="pi-acp"; version="0.0.34"; args=() ;;
  *) echo "Unknown template: $template" >&2; exit 2 ;;
esac

command -v node >/dev/null || { echo "node is needed on the PATH" >&2; exit 1; }
command -v npx >/dev/null || { echo "npx is needed on the PATH" >&2; exit 1; }

home="$(mktemp -d)"
trap 'rm -rf "$home"' EXIT
mkdir -p "$home/work"
out="$repo/tests/Shared/AcpRecordings"
mkdir -p "$out"
suffix=""
remote=()
if [ "$ssh" = "--ssh" ]; then
  suffix="-ssh"
  remote=("SSH_CONNECTION=10.0.0.1 22 10.0.0.2 22")
fi

# Codalus's client capabilities (AcpClient.Capabilities): files, terminals, compaction, notices, terminal sign-in.
env -i PATH="$PATH" HOME="$home" USERPROFILE="$home" XDG_CONFIG_HOME="$home/.config" CODEX_HOME="$home/.codex" \
  CLAUDE_CONFIG_DIR="$home/.claude" GEMINI_CLI_HOME="$home" ${remote[@]+"${remote[@]}"} \
  RECORD_INIT="$out/$template-$version-initialize$suffix.json" RECORD_NEW="$out/$template-$version-session-new-signed-out$suffix.json" \
  RECORD_CWD="$home/work" \
  node -e '
const { spawn } = require("child_process");
const fs = require("fs");
const agent = spawn("npx", ["-y", process.argv[1], ...process.argv.slice(2)], { cwd: process.env.RECORD_CWD, stdio: ["pipe", "pipe", "inherit"] });
let buffer = "", next = 1;
const pending = new Map();
agent.stdout.on("data", chunk => {
  buffer += chunk;
  let at;
  while ((at = buffer.indexOf("\n")) >= 0) {
    const line = buffer.slice(0, at).trim(); buffer = buffer.slice(at + 1);
    if (!line) continue;
    const message = JSON.parse(line);
    if (message.id !== undefined && pending.has(message.id)) { pending.get(message.id)(message); pending.delete(message.id); }
    else if (message.method) { process.stderr.write("push: " + message.method + "\n"); }
  }
});
const call = (method, params) => new Promise(resolve => { const id = next++; pending.set(id, resolve); agent.stdin.write(JSON.stringify({ jsonrpc: "2.0", id, method, params }) + "\n"); });
(async () => {
  const init = await call("initialize", {
    protocolVersion: 1,
    clientCapabilities: { fs: { readTextFile: true, writeTextFile: true }, terminal: true, session: { compaction: {}, notices: {} }, auth: { terminal: true } },
    clientInfo: { name: "codalus", title: "Codalus IDE", version: "0" },
  });
  fs.writeFileSync(process.env.RECORD_INIT, JSON.stringify({ _source: "Recorded by eng/record-acp-initialize.sh on " + new Date().toISOString().slice(0, 10), ...(init.result ?? { error: init.error }) }, null, 2) + "\n");
  const created = await call("session/new", { cwd: process.env.RECORD_CWD, mcpServers: [] });
  fs.writeFileSync(process.env.RECORD_NEW, JSON.stringify({ _source: "Recorded signed out by eng/record-acp-initialize.sh on " + new Date().toISOString().slice(0, 10), ...(created.error ? { error: created.error } : { result: { sessionId: "<recorded>" } }) }, null, 2) + "\n");
  agent.stdin.end();
  setTimeout(() => process.exit(0), 2000);
})();
' "$package@$version" ${args[@]+"${args[@]}"}

echo "Wrote $out/$template-$version-initialize$suffix.json and …-session-new-signed-out$suffix.json: read them before committing."
