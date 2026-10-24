#!/usr/bin/env bash
# Builds the plugin zip for a release and adds it to manifest.json, the plugin repository
# Jellyfin reads. Called by semantic-release with the release's semver (1.2.3 -> 1.2.3.0).
set -euo pipefail

release="$1"
version="$release.0"
repo="https://github.com/${GITHUB_REPOSITORY:-ojsef39/jellyfin-plugin-segment-stitch}"

perl -pi -e "s|^version: .*|version: \"$version\"|; s|^changelog: .*|changelog: \"$repo/releases/tag/v$release\"|" build.yaml
mkdir -p artifacts
jprm --verbosity=info plugin build . --output=artifacts --version="$version" --dotnet-framework=net10.0

zip="$(ls artifacts/*_"$version".zip)"
jprm repo add --plugin-url="$repo/releases/download/v$release/$(basename "$zip")" ./manifest.json "$zip"
