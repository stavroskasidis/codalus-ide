#!/usr/bin/env bash
# Rewrites Velopack's macOS installer so the app it installs belongs to the user who installs it (eng/package.sh).
#
# The .pkg runs as root, so an install in /Applications leaves `Codalus IDE.app` owned by root. Velopack's updater
# replaces the bundle by moving it, which needs write access to the bundle: it then asks for an administrator's
# password (once; the bundle that replaces it is the user's). Adding a chown to vpk's postinstall (vpk has no option for
# it) makes even the first update silent. The rewritten .pkg is unsigned: eng/package.sh signs and notarizes it.
#
# Usage: eng/packaging/macos/user-owned-pkg.sh <vpk's Setup.pkg> <output .pkg> <app bundle name>
set -euo pipefail

source="$1"
output="$2"
bundle="$3"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

pkgutil --expand "$source" "$work/pkg"
scripts=("$work"/pkg/*.pkg/Scripts/postinstall)
[ "${#scripts[@]}" -eq 1 ] && [ -f "${scripts[0]}" ] || { echo "error: expected one component postinstall in $source" >&2; exit 1; }
postinstall="${scripts[0]}"
[ "$(head -n 1 "$postinstall")" = '#!/bin/sh' ] || { echo "error: $postinstall doesn't start with #!/bin/sh" >&2; exit 1; }
grep -q "open \"\$2/$bundle/\"" "$postinstall" || { echo "error: $postinstall doesn't start $bundle: vpk changed it" >&2; exit 1; }

# $2 is the install location: /Applications or ~/Applications. The installing user is $USER in Installer.app; with
# `sudo installer` it is root, and SUDO_USER (or the console's user) is the one who asked. Nobody's (root, or no one
# signed in): the app stays as the installer left it, and its first update asks for a password as before.
{
  echo '#!/bin/sh'
  cat << EOF
# Codalus: the app belongs to the user who installs it, so its updates need no administrator (eng/packaging/macos/user-owned-pkg.sh).
owner="\$USER"
if [ -z "\$owner" ] || [ "\$owner" = root ]; then owner="\${SUDO_USER:-\$(stat -f %Su /dev/console)}"; fi
if [ -n "\$owner" ] && [ "\$owner" != root ] && id "\$owner" > /dev/null 2>&1; then
  /usr/sbin/chown -R "\$owner:\$(id -gn "\$owner")" "\$2/$bundle" || true
fi
EOF
  tail -n +2 "$postinstall"
} > "$work/postinstall"
cat "$work/postinstall" > "$postinstall" # keeps the file's mode
rm -f "$output"
pkgutil --flatten "$work/pkg" "$output"
