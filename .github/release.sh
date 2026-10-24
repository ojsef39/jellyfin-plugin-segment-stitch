#!/usr/bin/env bash
# Builds the plugin zip for a release and the plugin repository manifest Jellyfin reads, both
# attached to the release: the manifest of the latest release plus this version. Nothing is
# committed. Called by semantic-release with the release's semver (1.2.3 -> 1.2.3.0).
set -euo pipefail

release="$1"
version="$release.0"
repo="https://github.com/${GITHUB_REPOSITORY:-ojsef39/jellyfin-plugin-segment-stitch}"

perl -pi -e "s|^version: .*|version: \"$version\"|; s|^changelog: .*|changelog: \"$repo/releases/tag/v$release\"|" build.yaml
mkdir -p artifacts
jprm --verbosity=info plugin build . --output=artifacts --version="$version" --dotnet-framework=net10.0

# The repository's manifest.json holds the versions released before manifests were assets.
if ! curl -fsSL "$repo/releases/latest/download/manifest.json" -o artifacts/manifest.json; then
    cp manifest.json artifacts/manifest.json
fi

zip="$(ls artifacts/*_"$version".zip)"
jprm repo add --plugin-url="$repo/releases/download/v$release/$(basename "$zip")" ./artifacts/manifest.json "$zip"
