# Segment Stitch

Jellyfin media segments from [TheIntroDB](https://theintrodb.org), [IntroDB](https://introdb.app) and chapters, merged per segment type.

Each of those sources usually covers an episode completely or not at all, and they cover different episodes. Segment Stitch asks all three and merges per type, so an episode can get its intro from one source and its credits from another.

## Merging

- **Smart** (default): where several sources have a type, the segment runs from the latest start to the earliest end, the time they agree on. A source that overlaps none of the others is left out (a wrong "Intro" chapter, say). Two sources that do not overlap at all give no segment. It errs on skipping too little.
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

New items are looked up right away (episodes once their series has a TVDB or TMDB id), the rest on the media segment scan.

## Setup

Disable the TheIntroDB, Chapter Segments and Gelato IntroDB segment providers in the library settings, or their segments win over this one.

## Development

```sh
nix develop
dotnet test SegmentStitch.sln
```

Parts are taken from [TheIntroDB's Jellyfin plugin](https://github.com/TheIntroDB/jellyfin-plugin) and [Gelato](https://github.com/lostb1t/Gelato). GPL-3.0.
