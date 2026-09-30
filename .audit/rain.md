# Rain audit — 1.7.2..HEAD

Area: `JipperKeyViewer/KeyViewer/Rain/RainSystem.cs`, `JipperKeyViewer/KeyViewer/Rain/RawRain.cs`,
`JipperKeyViewer/KeyViewer/Rendering/RainLayer.cs`.

## Rounded outline with a single-axis side setting leaves the opposite corners empty
- file: JipperKeyViewer/KeyViewer/Rendering/RainLayer.cs
- line: 353-386 (`DrawRoundedRainOutlineWithRadius`, arc blocks at 373-386)
- severity: medium
- what: The horizontal strip pair is inset by `radius` at both ends but only the **top** two corner
  arcs are emitted, and the vertical strip pair is inset by `radius` at both ends but only the
  **bottom** two corner arcs are emitted. So with `outlineSides` = 1 (vertical) the left/right border
  ends `radius` px short of the top corners, and with `outlineSides` = 2 (horizontal) the top/bottom
  border ends `radius` px short of the bottom corners — transparent `radius x radius` notches in the
  ring. `sides == 0` is fine (all four arcs fire); the two single-axis modes are the broken ones.
- path: Settings -> `EnableRainRoundedOutline` = on, `RainOutlineSides` = 1 or 2 (the Rain tab's
  3-way `SelectionGrid`, KeyViewerRainGUI.cs:330; the per-node `RainBorderSides` editor toggle
  produces the same value) -> `CreateRainDropForKey` (RainSystem.cs:1027) stores
  `rawRain.outlineSides` and `outlineCornerRadius` -> `DrawDrop` takes the
  `rain.outlineCornerRadius > 0.5f` branch (RainLayer.cs:264) -> `DrawRoundedRainOutline` ->
  `DrawRoundedRainOutlineWithRadius` -> the two unmatched corner quadrants are never emitted, so the
  border ring is visibly broken on two of its four corners for every in-flight drop.
- fix: Emit the arcs that terminate each strip: for `horizontal` draw the arcs at BOTH the top pair
  and the bottom pair (or simply always draw all four corner arcs — the ones belonging to the
  disabled axis overlap strips that are already emitted and are visually identical), and likewise for
  `vertical` at both the left and right pairs.

## Ghost drop narrower/shorter than the 9-slice border renders nothing at all
- file: JipperKeyViewer/KeyViewer/Rendering/RainLayer.cs
- line: 775 (`if (nTilesW <= 0 || nTilesH <= 0) return;` in `DrawTiledWithBorder`)
- severity: medium
- what: The new guard returns from the whole method when either tile count collapses to 0, which
  skips the four 9-slice corner `AddQuad` calls that used to run unconditionally after the loops. When
  the drop rect is fully consumed by the sprite's borders (`GetAdjustedBorders` squeezes them so
  `xMax == xMin` / `yMax == yMin`) the corner quads were exactly what drew the remaining sliver, so the
  ghost body now disappears completely instead of being drawn squeezed. A growing drop is shorter than
  the combined 11px borders for its first frames, so every ghost drop loses its first frames, and a
  drop configured narrower/shorter than 22px never draws at all.
- path: Settings -> `EnableGhostRain` with the default 11px 9-slice GhostRain border (22px combined)
  + a ghost drop whose rect is under 22px on either axis (per-node `GhostRainWidth`/`GhostRainHeight`
  below 22, a row width of 0 floored to 1px by `RawRain.UpdateLocation`, or simply a young growing
  drop: `h = elapsedMs * speed`, e.g. speed 50 gives h = 8px on frame 1) -> `GhostRainLayer.OnPopulateMesh`
  -> `DrawTiledWithBorder` (RainLayer.cs:720) -> `bx = r.width/2, bz = r.width/2` so `xMax == xMin`,
  `nTilesW == 0` -> line 775 returns -> no vertices at all for that drop.
- fix: Don't return; only skip the tiling loops. Emit the four corner quads unconditionally (they are
  the correct squeezed output when there is no interior to tile), e.g. change the guard to
  `if (nTilesW > 0 && nTilesH > 0) { ...loops... }` and let the corner `AddQuad` block run after it.

## Per-row rain width is frozen into the drop at creation, so the width sliders no longer act on in-flight drops
- file: JipperKeyViewer/KeyViewer/Rain/RawRain.cs
- line: 102 (with RainSystem.cs:1105-1133)
- severity: low
- what: The row width used to be re-read from `KeyViewer.Settings.Data.RainWidthRowN` on every
  `UpdateLocation` call, so a growing drop tracked the slider live. It is now resolved once in
  `CreateRainDropForKey` into `RawRain.NodeWidth` and `UpdateLocation` only does
  `Mathf.Max(NodeWidth, 1f)`, so a drop keeps the width it was born with until it expires. The Rain
  tab's three normal-width sliders and three ghost-width sliders neither call `ClearActiveDrops` nor
  go through any live path, so dragging them only affects drops created after the drag.
- path: Settings window -> Rain page -> drag `rain_width` / ghost `rain_width` sliders
  (KeyViewerRainGUI.cs:293-301 and 470-477: the handlers only write
  `Settings.Data.RainWidthRowN` and `SaveSettingsFromGui()`; unlike the rounded-outline / dotted /
  border-sides controls they do NOT call `rainSystem.ClearActiveDrops`) -> existing drops keep the
  `NodeWidth` baked at RainSystem.cs:1119-1122 -> `RawRain.UpdateLocation` (line 102) reuses it every
  frame -> the trail keeps its old width for the remainder of each drop's life (with the default
  275px/300 speed that is ~0.3 s, but with a slow speed or a tall custom track the old width stays on
  screen for seconds and the slider reads as dead).
- fix: Either call `rainSystem.ClearActiveDrops(Keys)` from the six width-slider handlers (matching
  the rounded-outline / dotted / sides handlers right below them), or keep a per-row width cache in
  `RainSystem` (like `SyncCachedSpeeds` already does for speed/height/start-Y) and re-resolve
  `NodeWidth` for growing drops in `UpdateRectAndTrail`.
