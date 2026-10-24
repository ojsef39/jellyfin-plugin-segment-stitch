# Segment Stitch

Jellyfin media segments from [TheIntroDB](https://theintrodb.org), [IntroDB](https://introdb.app) and chapters, merged per segment type.

Each of those sources usually covers an episode completely or not at all, and they cover different episodes. Segment Stitch asks all three and merges per type, so an episode can get its intro from one source and its credits from another.

## Merging

The merge runs for each segment type (intro, recap, credits, preview) on its own, and every source takes part with at most one segment of that type.

| Situation | Result | Decided by |
| -- | -- | -- |
| Only one source has the type | That source's segment, as it is | Nothing to decide |
| Several have it and they overlap | From the latest start to the earliest end: the time they agree on | The overlap. A source that overlaps none of the others is left out, the rest are merged |
| Several have it and no two overlap | One source's segment, whole | The source priority |

**Overlap:** the time all sources agree on.

```
TheIntroDB   |----60........88----|
IntroDB         |--62..............95--|
result          |--62.....88--|
```

**One source off:** a wrong "Intro" chapter is left out, the two databases are merged.

```
Chapters     |15..46|
TheIntroDB                |60........88|
IntroDB                     |62..........90|
result                      |62......88|
```

**No overlap:** "latest start to earliest end" would be 60 to 30, no segment at all, so the first source by priority is taken whole.

```
TheIntroDB   |0....30|                          <- taken (priority)
IntroDB                    |60....90|
result       |0....30|
```

So coverage always counts: every episode only one source has gets that source's times. The priority only decides when sources that both have the type disagree completely, and there are two:

| Items | Default priority | Why |
| -- | -- | -- |
| Local files | TheIntroDB, IntroDB, chapters | TheIntroDB picks the submissions of the file's release by its duration |
| Gelato streams | IntroDB, TheIntroDB, chapters | IntroDB is addressed like Stremio (IMDb id, Cinemeta numbering), so its times are likelier from the debrid releases Gelato streams |

Each disagreement is logged with the sources taken and left out:

```
Sources disagree on the Intro of Pilot (<item id>): took TheIntroDb, left out IntroDb
```

The **Priority** merge mode skips all of this and takes the first source in the priority order that has the type.

Before merging, each segment is checked against the item's runtime:

- An intro or recap must start in the first half, credits or a preview in the second. This drops an opening song filed as credits, and credits "from 0" that would cover the whole episode.
- A segment running more than 10 s past the runtime is dropped (another release or episode), a smaller overrun is cut to the runtime.
- IntroDB's post-credits scenes are kept out of the credits.

## Sources

| Source | Asked by | Notes |
| -- | -- | -- |
| TheIntroDB | TVDB id (else TMDB, IMDb), season/episode in the series' display order, TVDB episode id, duration | The duration picks the matching release's submissions. Series in a TMDB episode-group order are skipped |
| IntroDB | Series or movie IMDb id, season/episode in IMDb's numbering as Cinemeta and Stremio give it | A Gelato episode's Stremio id has these numbers. A local episode is found in [Cinemeta](https://v3-cinemeta.strem.io) by its TVDB id, so any display order works. Otherwise the episode's own numbers are sent, for the default order only. IntroDB takes no duration, so its times may come from another release |
| Chapters | The item's named chapters (whole-title match, several languages) | |

## Refresh

An item's segments are fetched again after *Refresh after days* (30, spread by a quarter either way), so corrections in either database arrive. After an update of the plugin or a change of its merge settings, every item is merged again on its next run. A source with nothing for an item is asked again after 14 days. When a source fails, the stored segments are kept and the item is asked again on the next run.

Local items are looked up when added (episodes once their series has a TVDB or TMDB id), after a metadata download and on playback, the rest on the media segment scan. For Gelato's stream rows, Gelato runs the providers before playback starts, so the first play already has its skip buttons.

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
