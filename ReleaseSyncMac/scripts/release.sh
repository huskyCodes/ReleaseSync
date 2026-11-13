#!/bin/zsh
# Builds ReleaseSync for Mac, signs it with a Developer ID, puts it in a disk image, has Apple notarize it, and staples the ticket.
# Nothing secret is in this file or in the repository: the signing certificate lives in your Keychain, and the notarization login is a
# Keychain profile you create once yourself:
#
#   xcrun notarytool store-credentials "releasesync-notary" --apple-id "you@example.com" --team-id "YOURTEAMID"
#   (it asks for an app-specific password from appleid.apple.com and stores it in your Keychain)
#
# Then:
#   DEVELOPER_ID="Developer ID Application: Your Name (YOURTEAMID)" NOTARY_PROFILE="releasesync-notary" scripts/release.sh
#
# To try the steps without notarizing (for example with an "Apple Development" identity): add SKIP_NOTARIZE=1.
# The result is dist/ReleaseSync-<version>.dmg in this folder (dist/ is not committed).
set -euo pipefail
cd "$(dirname "$0")/.."

: "${DEVELOPER_ID:?Set DEVELOPER_ID to your signing identity, e.g. \"Developer ID Application: Your Name (TEAMID)\"}"
SKIP_NOTARIZE="${SKIP_NOTARIZE:-0}"
[[ "$SKIP_NOTARIZE" == "1" ]] || : "${NOTARY_PROFILE:?Set NOTARY_PROFILE to the notarytool Keychain profile name (see the top of this file)}"

# for a Developer ID the number in brackets is the team; for other identities (a trial run) the team comes from Config/Local.xcconfig
TEAM="${DEVELOPMENT_TEAM:-}"
if [[ -z "$TEAM" && "$DEVELOPER_ID" == "Developer ID Application:"* ]]; then TEAM="$(sed -n 's/.*(\([A-Z0-9]\{10\}\))$/\1/p' <<<"$DEVELOPER_ID")"; fi
BUILD="$PWD/build"
DIST="$PWD/dist"
rm -rf "$BUILD" "$DIST"; mkdir -p "$DIST"

echo "==> Generating the project"
xcodegen generate >/dev/null

echo "==> Building (Release, hardened runtime, no sandbox, no entitlements)"
xcodebuild -project ReleaseSync.xcodeproj -scheme ReleaseSync -configuration Release -destination 'generic/platform=macOS' \
  -derivedDataPath "$BUILD" \
  CODE_SIGN_STYLE=Manual CODE_SIGN_IDENTITY="$DEVELOPER_ID" ${TEAM:+DEVELOPMENT_TEAM="$TEAM"} \
  OTHER_CODE_SIGN_FLAGS="--timestamp" ENABLE_HARDENED_RUNTIME=YES \
  build | grep -E "error:|BUILD (SUCCEEDED|FAILED)" || true
APP="$BUILD/Build/Products/Release/ReleaseSync.app"
[[ -d "$APP" ]] || { echo "The build did not produce an app." >&2; exit 1; }

VERSION="$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' "$APP/Contents/Info.plist")"
echo "==> Checking the signature of ReleaseSync $VERSION"
codesign --verify --deep --strict --verbose=2 "$APP"
codesign -dv --verbose=2 "$APP" 2>&1 | grep -E "Authority=|TeamIdentifier|flags=" || true
ENT="$(codesign -d --entitlements - "$APP" 2>&1 | grep -c 'com.apple' || true)"
[[ "$ENT" == "0" ]] || { echo "The app carries entitlements it should not have." >&2; codesign -d --entitlements - "$APP" >&2; exit 1; }

echo "==> Making the disk image"
STAGE="$BUILD/dmg"; rm -rf "$STAGE"; mkdir -p "$STAGE"
ditto "$APP" "$STAGE/ReleaseSync.app"
ln -s /Applications "$STAGE/Applications"
DMG="$DIST/ReleaseSync-$VERSION.dmg"
hdiutil create -quiet -volname "ReleaseSync $VERSION" -srcfolder "$STAGE" -format UDZO -fs HFS+ "$DMG"
codesign --force --timestamp --sign "$DEVELOPER_ID" "$DMG"

if [[ "$SKIP_NOTARIZE" == "1" ]]; then
  echo "==> Skipped notarization. Made $DMG"
  exit 0
fi

echo "==> Sending it to Apple to be notarized (this takes a few minutes)"
xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
echo "==> Stapling the ticket"
xcrun stapler staple "$DMG"
xcrun stapler validate "$DMG"
echo "==> Checking what Gatekeeper says"
spctl --assess --type open --context context:primary-signature --verbose=4 "$DMG"
shasum -a 256 "$DMG" | tee "$DMG.sha256"
echo "==> Done: $DMG"
