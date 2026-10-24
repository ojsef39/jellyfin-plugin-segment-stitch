{
  description = "Segment Stitch Jellyfin plugin";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";

  outputs = {nixpkgs, ...}: let
    systems = ["aarch64-darwin" "aarch64-linux" "x86_64-linux"];
    forAllSystems = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
  in {
    devShells = forAllSystems (pkgs: let
      # Where d-deploy and d-rollback act; override with the environment variables.
      kube = ''
        ctx="''${SEGSTITCH_KUBE_CONTEXT:-admin@talos-dev}"
        ns="''${SEGSTITCH_KUBE_NAMESPACE:-jelly-stack}"
        k() { kubectl --context "$ctx" -n "$ns" "$@"; }
        kx() { k exec deploy/jellyfin -c jellyfin -- "$@"; }
        kxi() { k exec -i deploy/jellyfin -c jellyfin -- "$@"; }
        restart() {
          k rollout restart deploy/jellyfin
          k rollout status deploy/jellyfin --timeout=5m
        }
      '';

      # Builds artifacts/plugin (SegmentStitch.dll and meta.json). The version is the last
      # release tag plus the commits since it as fourth part (v0.2.0 + 3 = 0.2.0.3), so a dev
      # build sorts above its release and below the next one.
      d-build = pkgs.writeShellApplication {
        name = "d-build";
        runtimeInputs = [pkgs.dotnet-sdk_10 pkgs.git pkgs.yq-go pkgs.jq pkgs.coreutils];
        text = ''
          cd "$(git rev-parse --show-toplevel)"
          if tag="$(git describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null)"; then
            version="''${tag#v}.$(git rev-list --count "$tag"..HEAD)"
          else
            version="0.0.0.$(git rev-list --count HEAD)"
          fi

          rm -rf artifacts/publish artifacts/plugin
          dotnet publish SegmentStitch/SegmentStitch.csproj -c Release -o artifacts/publish --nologo -v quiet \
            -p:Version="$version" -p:AssemblyVersion="$version" -p:FileVersion="$version"
          mkdir -p artifacts/plugin
          cp artifacts/publish/SegmentStitch.dll artifacts/plugin/
          yq -o json '{"guid": .guid, "name": .name, "description": .description, "overview": .overview,
              "owner": .owner, "category": .category, "changelog": .changelog, "targetAbi": .targetAbi}' build.yaml \
            | jq --arg v "$version" --arg ts "$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
              '. + {version: $v, timestamp: $ts, status: "Active", autoUpdate: false}' \
            > artifacts/plugin/meta.json
          echo "$version" > artifacts/plugin/VERSION
          echo "=> Built $version"
          sha256sum artifacts/plugin/SegmentStitch.dll
        '';
      };

      # Builds and installs the plugin on the dev Jellyfin as /config/plugins/SegmentStitch-dev_<version>,
      # replacing earlier dev builds, restarts it and checks the running pod has the same DLL.
      d-deploy = pkgs.writeShellApplication {
        name = "d-deploy";
        runtimeInputs = [d-build pkgs.kubectl pkgs.coreutils pkgs.gnugrep];
        excludeShellChecks = ["SC2016"];
        text = ''
          ${kube}
          d-build
          cd "$(git rev-parse --show-toplevel)/artifacts/plugin"
          version="$(cat VERSION)"
          dir="/config/plugins/SegmentStitch-dev_$version"
          want="$(sha256sum SegmentStitch.dll | cut -d' ' -f1)"

          echo "=> Installing $version on $ctx"
          kx sh -c 'rm -rf /config/plugins/SegmentStitch-dev_* && mkdir -p "$1"' sh "$dir"
          for f in SegmentStitch.dll meta.json; do
            kxi sh -c 'cat > "$1"' sh "$dir/$f" < "$f"
          done

          restart
          have="$(kx sha256sum "$dir/SegmentStitch.dll" | cut -d' ' -f1)"
          if [ "$have" != "$want" ]; then
            echo "!! The pod has $have, expected $want" >&2
            exit 1
          fi

          echo "=> Waiting for Jellyfin to load it"
          for _ in $(seq 60); do
            if k logs deploy/jellyfin -c jellyfin 2>/dev/null | grep -q "Loaded plugin: Segment Stitch $version"; then
              echo "=> Segment Stitch $version is running ($want)"
              exit 0
            fi
            sleep 2
          done
          echo "!! No 'Loaded plugin: Segment Stitch $version' in the log after 2 minutes" >&2
          exit 1
        '';
      };

      # Removes the dev builds; a Segment Stitch installed from the catalog takes over again.
      d-rollback = pkgs.writeShellApplication {
        name = "d-rollback";
        runtimeInputs = [pkgs.kubectl pkgs.coreutils];
        text = ''
          ${kube}
          echo "=> Removing dev builds on $ctx"
          kx sh -c 'ls -d /config/plugins/SegmentStitch-dev_* 2>/dev/null; rm -rf /config/plugins/SegmentStitch-dev_*'
          restart
          echo "=> Installed Segment Stitch versions left:"
          kx sh -c 'grep -l "\"name\": *\"Segment Stitch\"" /config/plugins/*/meta.json 2>/dev/null || echo "(none)"'
        '';
      };
    in {
      default = pkgs.mkShell {
        packages = [
          pkgs.dotnet-sdk_10
          pkgs.roslyn-ls
          d-build
          d-deploy
          d-rollback
        ];
        DOTNET_ROOT = "${pkgs.dotnet-sdk_10}/share/dotnet";
        DOTNET_CLI_TELEMETRY_OPTOUT = "1";
      };
    });

    formatter = forAllSystems (pkgs: pkgs.alejandra);
  };
}
