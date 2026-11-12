#!/usr/bin/env bash
# The signing keychain of a macOS release job (D19, pipeline.yml's package job): `setup` builds a throwaway keychain
# with the Developer ID certificates and the notarytool profile from the secrets, and exports CODALUS_KEYCHAIN and
# CODALUS_NOTARY_PROFILE to the later steps (GITHUB_ENV) for eng/package.sh; `cleanup` deletes it.
#
# Usage: eng/macos-signing.sh setup|cleanup
# Environment (setup):
#   CODALUS_CODESIGN_IDENTITY   the app's identity ("Developer ID Application: …"); unset: nothing to do, the packages
#                                stay unsigned
#   CODALUS_INSTALLER_IDENTITY  the .pkg's identity ("Developer ID Installer: …")
#   MACOS_CERTIFICATE_P12       base64 of a .p12 with both certificates and their private keys
#   MACOS_CERTIFICATE_PASSWORD  the .p12's password
#   APPLE_NOTARY_KEY            an App Store Connect API key (the .p8's text), with
#   APPLE_NOTARY_KEY_ID         its key ID, and
#   APPLE_NOTARY_ISSUER         its issuer ID
set -euo pipefail

keychain="${RUNNER_TEMP:?run in a GitHub Actions job}/codalus-signing.keychain-db"
profile=codalus-notary

# security import, where an item the keychain has already counts as imported: a .p12 exported from Keychain Access
# can hold the Developer ID intermediate too, or one private key twice (two certificates from one request).
import_item() {
  local output
  if output="$(security import "$@" 2>&1)"; then
    echo "$output"
  elif [[ "$output" == *"already exists"* ]]; then
    echo "$output (already imported)"
  else
    echo "$output" >&2
    return 1
  fi
}

case "${1:-}" in
  setup)
    if [ -z "${CODALUS_CODESIGN_IDENTITY:-}" ]; then
      echo "CODALUS_CODESIGN_IDENTITY isn't set: the macOS packages are unsigned."
      exit 0
    fi
    # Half a configuration is a mistake, not a reason to ship a package Gatekeeper refuses.
    # (Strings, not arrays: macOS's bash 3.2 calls an empty array unbound under set -u.)
    missing=""
    for name in CODALUS_INSTALLER_IDENTITY MACOS_CERTIFICATE_P12 MACOS_CERTIFICATE_PASSWORD APPLE_NOTARY_KEY APPLE_NOTARY_KEY_ID APPLE_NOTARY_ISSUER; do
      [ -n "${!name:-}" ] || missing="$missing $name"
    done
    if [ -n "$missing" ]; then
      echo "::error::CODALUS_CODESIGN_IDENTITY is set, but these secrets aren't:$missing (docs/release/releasing.md#macos-signing)"
      exit 1
    fi

    password="$(openssl rand -base64 24)"
    security create-keychain -p "$password" "$keychain"
    security set-keychain-settings -lut 21600 "$keychain" # stays unlocked for the job's 6 hours
    security unlock-keychain -p "$password" "$keychain"
    p12="$RUNNER_TEMP/codalus-signing.p12"
    trap 'rm -f "$p12" "$RUNNER_TEMP/codalus-notary.p8"' EXIT
    printf '%s' "$MACOS_CERTIFICATE_P12" | base64 --decode > "$p12"
    import_item "$p12" -k "$keychain" -P "$MACOS_CERTIFICATE_PASSWORD" -f pkcs12 \
      -T /usr/bin/codesign -T /usr/bin/productsign -T /usr/bin/security
    # The Developer ID intermediates (the current G2 and the previous one): codesign embeds the chain, and a .p12
    # exported from Keychain Access holds only the certificates and their keys.
    for ca in DeveloperIDG2CA DeveloperIDCA; do
      curl -fsSL --retry 3 "https://www.apple.com/certificateauthority/$ca.cer" -o "$RUNNER_TEMP/$ca.cer"
      import_item "$RUNNER_TEMP/$ca.cer" -k "$keychain"
      rm "$RUNNER_TEMP/$ca.cer"
    done
    # Lets codesign and productsign use the keys without a prompt nobody can answer.
    security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$password" "$keychain" > /dev/null
    # First in the search list too, where productsign, notarytool and codesign's chain building look.
    existing=()
    while IFS= read -r line; do
      line="${line#"${line%%[![:space:]]*}"}"
      existing+=("${line//\"/}")
    done < <(security list-keychains -d user)
    security list-keychains -d user -s "$keychain" ${existing[@]+"${existing[@]}"}

    identities="$(security find-identity -v "$keychain")"
    for identity in "$CODALUS_CODESIGN_IDENTITY" "$CODALUS_INSTALLER_IDENTITY"; do
      if ! grep -qF "$identity" <<< "$identities"; then
        echo "::error::The certificate \"$identity\" isn't in MACOS_CERTIFICATE_P12, or it has no private key. The .p12 has:"
        echo "$identities"
        exit 1
      fi
    done

    printf '%s\n' "$APPLE_NOTARY_KEY" > "$RUNNER_TEMP/codalus-notary.p8"
    xcrun notarytool store-credentials "$profile" --key "$RUNNER_TEMP/codalus-notary.p8" --key-id "$APPLE_NOTARY_KEY_ID" \
      --issuer "$APPLE_NOTARY_ISSUER" --keychain "$keychain" # checks the key with Apple before storing it
    {
      echo "CODALUS_KEYCHAIN=$keychain"
      echo "CODALUS_NOTARY_PROFILE=$profile"
    } >> "${GITHUB_ENV:?}"
    echo "Signing with \"$CODALUS_CODESIGN_IDENTITY\" and \"$CODALUS_INSTALLER_IDENTITY\"; notarizing as $profile."
    ;;
  cleanup)
    if [ -f "$keychain" ]; then
      security delete-keychain "$keychain"
    fi
    ;;
  *)
    echo "Usage: eng/macos-signing.sh setup|cleanup" >&2
    exit 2
    ;;
esac
