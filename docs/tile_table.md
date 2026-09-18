# Tile Table

Full identity mapping for all 37 renderable tile faces (34 kinds + 3 red fives) in
the Emj addon, derived from `Core/Tile.cs`, `Core/TileHelpers.cs`, and the icon-path
parsing confirmed in `Core/EmjScanner.cs` (see `docs/EMJ_ADDON_REFERENCE.md` for the
full reverse-engineering writeup this table summarizes).

- **Short name** — `Tile.ToString()` output; the compact code used throughout this
  codebase and its logs/tests (`Tile.Parse("7p")`, `TileHelpers.ToIndex`, etc.).
- **Full name** — `TileHelpers.GetDisplayName`, which matches the game's own hover
  tooltip text format exactly (live-confirmed via hundreds of `OnHoverEvent` reads
  this session — e.g. `"Bamboo (9)"`, `"Characters (2)"`, `"East Wind"`).
- **Icon ID** — the FFXIV icon id backing the tile's face texture (`Core/
  TileHelpers.TryTileFromIconId`).
- **Texture path** — `ui/icon/076000/{iconId:D6}_hr1.tex`, confirmed live via
  `EmjScanner.IconIdFromTexPath` parsing (e.g. `"ui/icon/076000/076050_hr1.tex"` →
  `76050` → `1p`). A bare `{iconId:D6}.tex` (no `_hr1` suffix) form also exists for
  the same id — the `_hr1` (high-res) variant is what actually renders in every
  live capture this session, so it's listed as the canonical path.

## Man (Characters) — 萬子

| Short | Full name | Icon ID | Texture path |
|-------|-----------|---------|--------------|
| `1m` | Characters (1) | 76041 | `ui/icon/076000/076041_hr1.tex` |
| `2m` | Characters (2) | 76042 | `ui/icon/076000/076042_hr1.tex` |
| `3m` | Characters (3) | 76043 | `ui/icon/076000/076043_hr1.tex` |
| `4m` | Characters (4) | 76044 | `ui/icon/076000/076044_hr1.tex` |
| `5m` | Characters (5) | 76045 | `ui/icon/076000/076045_hr1.tex` |
| `6m` | Characters (6) | 76046 | `ui/icon/076000/076046_hr1.tex` |
| `7m` | Characters (7) | 76047 | `ui/icon/076000/076047_hr1.tex` |
| `8m` | Characters (8) | 76048 | `ui/icon/076000/076048_hr1.tex` |
| `9m` | Characters (9) | 76049 | `ui/icon/076000/076049_hr1.tex` |
| `0m` (red 5m) | Characters (5), red | 76075 | `ui/icon/076000/076075_hr1.tex` |

## Pin (Dots) — 筒子

| Short | Full name | Icon ID | Texture path |
|-------|-----------|---------|--------------|
| `1p` | Dots (1) | 76050 | `ui/icon/076000/076050_hr1.tex` |
| `2p` | Dots (2) | 76051 | `ui/icon/076000/076051_hr1.tex` |
| `3p` | Dots (3) | 76052 | `ui/icon/076000/076052_hr1.tex` |
| `4p` | Dots (4) | 76053 | `ui/icon/076000/076053_hr1.tex` |
| `5p` | Dots (5) | 76054 | `ui/icon/076000/076054_hr1.tex` |
| `6p` | Dots (6) | 76055 | `ui/icon/076000/076055_hr1.tex` |
| `7p` | Dots (7) | 76056 | `ui/icon/076000/076056_hr1.tex` |
| `8p` | Dots (8) | 76057 | `ui/icon/076000/076057_hr1.tex` |
| `9p` | Dots (9) | 76058 | `ui/icon/076000/076058_hr1.tex` |
| `0p` (red 5p) | Dots (5), red | 76076 | `ui/icon/076000/076076_hr1.tex` |

## Sou (Bamboo) — 索子

| Short | Full name | Icon ID | Texture path |
|-------|-----------|---------|--------------|
| `1s` | Bamboo (1) | 76059 | `ui/icon/076000/076059_hr1.tex` |
| `2s` | Bamboo (2) | 76060 | `ui/icon/076000/076060_hr1.tex` |
| `3s` | Bamboo (3) | 76061 | `ui/icon/076000/076061_hr1.tex` |
| `4s` | Bamboo (4) | 76062 | `ui/icon/076000/076062_hr1.tex` |
| `5s` | Bamboo (5) | 76063 | `ui/icon/076000/076063_hr1.tex` |
| `6s` | Bamboo (6) | 76064 | `ui/icon/076000/076064_hr1.tex` |
| `7s` | Bamboo (7) | 76065 | `ui/icon/076000/076065_hr1.tex` |
| `8s` | Bamboo (8) | 76066 | `ui/icon/076000/076066_hr1.tex` |
| `9s` | Bamboo (9) | 76067 | `ui/icon/076000/076067_hr1.tex` |
| `0s` (red 5s) | Bamboo (5), red | 76077 | `ui/icon/076000/076077_hr1.tex` |

## Winds — 風牌

| Short | Full name | Icon ID | Texture path |
|-------|-----------|---------|--------------|
| `1z` | East Wind | 76068 | `ui/icon/076000/076068_hr1.tex` |
| `2z` | South Wind | 76069 | `ui/icon/076000/076069_hr1.tex` |
| `3z` | West Wind | 76070 | `ui/icon/076000/076070_hr1.tex` |
| `4z` | North Wind | 76071 | `ui/icon/076000/076071_hr1.tex` |

## Dragons — 三元牌

| Short | Full name | Icon ID | Texture path |
|-------|-----------|---------|--------------|
| `5z` | White Dragon (Haku) | 76072 | `ui/icon/076000/076072_hr1.tex` |
| `6z` | Green Dragon (Hatsu) | 76073 | `ui/icon/076000/076073_hr1.tex` |
| `7z` | Red Dragon (Chun) | 76074 | `ui/icon/076000/076074_hr1.tex` |

## Notes

- **Red fives** are the same *kind* as their normal counterpart for every gameplay
  purpose (`TileHelpers.SameKind`/`Normalize` treat `0m`≡`5m`, `0p`≡`5p`, `0s`≡`5s`)
  — they only differ in `Tile.IsRedFive` and their distinct icon/texture. The exact
  raw in-game hover string for a red five hasn't been directly captured this
  session; `Core/GameStateReader.TryParseTileName` accepts both a literal `"(Red)"`
  suffix and a `"(0)"` numeral as equivalent red-five markers, so the full name
  above is a description, not a verbatim-confirmed hover string like the others.
- **Directory bucketing**: the `076000` directory is a fixed bucket for the whole
  76000-76999 icon range (FFXIV icons are bucketed by thousand) — every tile face
  icon in this game falls in that single bucket.
- **`_hr1` suffix**: stands for the high-resolution texture variant. A bare
  `{iconId:D6}.tex` (no suffix) form exists for the same id, but every live capture
  this session rendered through the `_hr1` path — see `EmjScanner.GetIconId` for why
  the path (not the texture resource's `IconId` field) is the trustworthy source of
  a rendered tile's identity: the field can go stale on a pooled/rebound node, the
  path always names what's actually on screen.
