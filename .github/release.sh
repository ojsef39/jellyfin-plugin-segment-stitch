#!/usr/bin/env bash
# Builds the plugin zip for a release and the plugin repository manifest Jellyfin reads, both
# attached to the release: the manifest of the latest release plus this version. Nothing is
# committed. Called by semantic-release with the release's semver (1.2.3 -> 1.2.3.0) and the
# release notes on stdin, which become this version's changelog in the manifest.
set -euo pipefail

release="$1"
version="$release.0"
repo="https://github.com/${GITHUB_REPOSITORY:-ojsef39/jellyfin-plugin-segment-stitch}"

perl -pi -e "s|^version: .*|version: \"$version\"|; s|^changelog: .*|changelog: \"$repo/releases/tag/v$release\"|" build.yaml
mkdir -p artifacts
cat > artifacts/notes.md
jprm --verbosity=info plugin build . --output=artifacts --version="$version" --dotnet-framework=net10.0

curl -fsSL "$repo/releases/latest/download/manifest.json" -o artifacts/manifest.json
zip="$(ls artifacts/*_"$version".zip)"
jprm repo add --plugin-url="$repo/releases/download/v$release/$(basename "$zip")" ./artifacts/manifest.json "$zip"

# Jellyfin shows the version and date itself, so the notes' "# [1.2.3](compare) (date)" line goes.
jq --arg version "$version" --rawfile notes artifacts/notes.md '
    ($notes | sub("^#+ [^\n]*\n+"; "") | sub("\\s+$"; "")) as $changelog
    | .[0].versions |= map(if .version == $version and $changelog != "" then .changelog = $changelog end)
' artifacts/manifest.json > artifacts/manifest.json.tmp
mv artifacts/manifest.json.tmp artifacts/manifest.json
