# Segment Stitch

Jellyfin media segments from [TheIntroDB](https://theintrodb.org), [IntroDB](https://introdb.app) and chapters, merged per segment type.

Each of those sources usually covers an episode completely or not at all, and they cover different episodes. Segment Stitch asks all three and merges per type, so an episode can get its intro from one source and its credits from another.

## Merging

- **Smart** (default): where several sources have a type, the segment runs from the latest start to the earliest end, the time they agree on. A source that overlaps none of the others is left out (a wrong "Intro" chapter, say). When no two sources overlap, the first by priority wins: TheIntroDB by default, since it is asked by TVDB episode id and duration, while IntroDB holds submissions in more than one numbering.
- **Priority**: the first source in the priority order that has the type.

A segment running more than 10 s past the item's runtime is dropped (another release or episode), a smaller overrun is cut to the runtime. IntroDB's post-credits scenes are kept out of the credits.

## Sources

| Source | Asked by | Notes |
| -- | -- | -- |
| TheIntroDB | TVDB id (else TMDB, IMDb), season/episode in the series' display order, TVDB episode id, duration | Series in a TMDB episode-group order are skipped |
| IntroDB | Series or movie IMDb id, season/episode | IMDb numbering only, so skipped for any non-default display order |
| Chapters | The item's named chapters (whole-title match, several languages) | |

## Refresh

An item's segments are fetched again after *Refresh after days* (30, spread by a quarter either way), so corrections in either database arrive. A source with nothing for an item is asked again after 14 days. When a source fails, the stored segments are kept and the item is asked again on the next run.

Items are looked up when added (episodes once their series has a TVDB or TMDB id), after a metadata download and on playback, the rest on the media segment scan. For Gelato's stream rows, Gelato runs the providers before playback starts, so the first play already has its skip buttons.

## Setup

Disable the TheIntroDB, Chapter Segments and Gelato IntroDB segment providers in the library settings, or their segments win over this one.

## Install

Add the repository `https://raw.githubusercontent.com/ojsef39/jellyfin-plugin-segment-stitch/main/manifest.json` under Dashboard, Plugins, Repositories.

## Development

```sh
nix develop
dotnet test SegmentStitch.sln
d-build      # artifacts/plugin: SegmentStitch.dll and meta.json
d-deploy     # build, install on the dev Jellyfin, restart, check the running DLL
d-rollback   # remove the dev builds, a catalog install takes over again
```

`d-deploy` and `d-rollback` act on `SEGSTITCH_KUBE_CONTEXT` (default `admin@talos-dev`) and `SEGSTITCH_KUBE_NAMESPACE` (default `jelly-stack`). A dev build is versioned as the last release plus the commits since it (`v0.2.0` + 3 commits = `0.2.0.3`).

Releases are cut by semantic-release from the Conventional Commits on `main`: it tags, builds the zip with jprm, attaches it to the GitHub release and adds it to `manifest.json`.

Parts are taken from [TheIntroDB's Jellyfin plugin](https://github.com/TheIntroDB/jellyfin-plugin) and [Gelato](https://github.com/lostb1t/Gelato). GPL-3.0.
