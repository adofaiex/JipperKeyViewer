# Render-area audit — bugs introduced between 1.7.2 and HEAD

Scope: `JipperKeyViewer/KeyViewer/Rendering/KeyShapeLayer.cs`, `KvTextGradient.cs`,
`KvTextStyle.cs`, `KvVideoTextureManager.cs` (+ their call sites).

---

## New `SafeColor` sanitization is bypassed by the node-override branch
- file: JipperKeyViewer/KeyViewer/Rendering/KvTextStyle.cs
- line: 140, 143, 151, 154 (contrast 165-182)
- severity: medium
- what: The diff wrapped the four *global* outline/shadow `Color` fields in `SafeColor(...)`, but the
  node-override branch above still passes the **raw** globals as `ColorOf`'s fallback:
  `ColorOf(node.KeyTextOutlineColor, d.KeyTextOutlineColor)`. A node that opts into
  `UseCustomTextStyle` with a null/malformed colour array therefore resolves to the un-sanitised
  global colour, and NaN/Inf still reaches `mat.SetColor("_OutlineColor")` plus the `Color32` cache
  key — exactly the two outcomes the new function was added to prevent.
- path: settings.json / a shared `.jkv` carries `KeyTextOutlineColor = NaN` → user ticks a node's
  "custom text style" but never sets its outline colour (array is null) → `KvTextStyle.Resolve` takes
  the `node != null` branch (line 137) → `ColorOf(null, d.KeyTextOutlineColor)` returns the raw NaN →
  `KeyViewerResources.GetTextStyleMaterial` (line 619) `mat.SetColor("_OutlineColor", NaN)` → NaN in
  the SDF fragment output, and `Color32(NaN)` quantises so two different broken styles share one
  material.
- fix: Pass the sanitised value as the fallback in all four node-branch reads, e.g.
  `ColorOf(node.KeyTextOutlineColor, SafeColor(d.KeyTextOutlineColor, Color.black))` (same for
  `KeyTextShadowColor`, `CountTextOutlineColor`, `CountTextShadowColor`).

## Global text-style floats are not sanitised — NaN reaches `SetFloat` on the outline material
- file: JipperKeyViewer/KeyViewer/Rendering/KvTextStyle.cs
- line: 166-182 (`Resolve`, the `else if (d != null)` global branch) and 189
- severity: medium
- what: The same non-finite-value hazard the diff addressed for colours is left open for the widths
  and offsets. The *node* copies of these floats are clamped upstream by
  `CustomLayout.SanitizeTextStyle` (CustomLayout.cs:494-501), but the four *global* `ProfileData`
  fields are plain floats that nothing scrubs — `SafeColor`'s own new doc comment states this about
  the neighbouring colour fields. `s.OutlineWidth <= 0f` is false for NaN, so the feature is not
  collapsed and `mat.SetFloat("_OutlineWidth", NaN)` executes.
- path: a hand-edited `settings.json` or a shared `.jkv` with
  `"KeyTextOutlineThickness": NaN` (or `KeyTextShadowOffsetX: NaN`) → `Resolve` global branch
  (line 166/169-171) copies it verbatim → `GetTextStyleMaterial` line 620
  `mat.SetFloat("_OutlineWidth", NaN)` / line 626 `SetFloat("_UnderlayOffsetX", NaN)` → NaN in the
  TMP outline/underlay fragment math, so every label drawn with that material renders black or
  vanishes, with no log. `Bits()` clamping the *cache key* does not help — it only stops the int
  conversion from overflowing.
- fix: Sanitise the same way `SafeColor` does, e.g. add
  `private static float SafeFloat(float f, float fallback) => float.IsNaN(f) || float.IsInfinity(f) ? fallback : f;`
  and wrap the eight global width/offset reads at lines 166-182 with it (0.2f / 0f / 1f / 20f
  defaults respectively).

## Budget-refusal warning repeats on every rebuild once the 256 MB cap is hit
- file: JipperKeyViewer/KeyViewer/Rendering/KvVideoTextureManager.cs
- line: 412-417
- severity: low
- what: `CreateEntry` logs a `Loader.Warning` every time it refuses a node for the render-texture
  budget, and a refused node never gets an entry — so nothing memoises the refusal the way the
  decode-failure tombstone does. The budget is global and permanent, so once it is exhausted every
  subsequent rebuild re-logs for every over-budget video node.
- path: 17 video nodes sized 2048×2048 (16 MB each) fill the 256 MB cap → the 17th is refused at
  line 413 and returns null → the user drags any colour slider, and every `ResetKeyViewer` (the
  editor does ~100/s while dragging) re-runs `InitializeCustomLayout` → `GetOrCreate` →
  `CreateEntry` → the same `Loader.Warning` for the same node, ~100 lines/second into the game log
  for as long as the slider is held. Note the decode-failure path deliberately avoids exactly this
  (tombstone + never-retry), so the two refusals behave inconsistently.
- fix: Record the refusal on the node id (e.g. a `HashSet<int> budgetRefused`) and only warn the
  first time for a given id, or downgrade to a single `Loader.Info` emitted from `ReleaseAll` /
  `EndBuild` summarising how many nodes fell back to their static image.
