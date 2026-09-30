# util — audit of `JipperKeyViewer/KeyViewer/Util/{I18n,KvEasing,KvImageLoader}.cs` + `Settings/KeyViewerSettings.cs`, diff `1.7.2..HEAD`

Reviewed: the full `git diff 1.7.2..HEAD` for the four assigned files plus their call sites
(`CustomLayout.cs`, `KeyViewerLayout.cs`, `KeyViewer.cs`, `KeyViewerPackages.cs`,
`KeyViewerDmNoteImport.cs`, `KeyViewerDmNoteCssMapping.cs`, `Rendering/KvTextGradient.cs`).

Cleared (checked, no defect found): `I18n.cs` (all three dictionaries carry the identical 466-key
set — no missing, extra, duplicate, or placeholder-mismatched entry; the only unmatched `Tr()` literal
is the `"tab_" + TabKeys[i]` prefix, which is a concatenation, not a key);
`KvEasing.cs` (the diff adds only `if (float.IsNaN(t)) t = 0f;`; `Mathf.Clamp01` already scrubs ±Inf);
`DefaultPerKeyRainColor` / `SafeEnsureRain` / `EnsureRainColorArray` (reproduce the 1.7.2 per-row
rule exactly, and `PerKeySlotCount == MaxKeySlots + 2`); `ImportLegacyCarriers`'s keep-on-failure
change; the `MaterializeNodes`/`MaterializeGroups` null filters; the `NodeDefaultsVersion` one-shot
gate for `ApplyLegacyFmNodeDefaults`; `EnsureLoadImageMethod`'s retry latch (the `now` captured before
the search is only used for the rate-limit bookkeeping, and `_loadImageCached` is only ever set on a
path that also sets `_loadImageRetryAfter`).

---

## Cross-rebuild image cache never evicts, so its VRAM budget is never given back
- file: JipperKeyViewer/KeyViewer/Util/KvImageLoader.cs
- line: 108–173 (`LoadTextureCached`), gate at 143–150
- severity: high
- what: `textureCache` / `liveCachedImageBytes` only ever shrink in two places: the same-path
  file-changed eviction (124–133) and `ReleaseCachedTextures` (86–103), which runs **only** in
  `DisableKeyViewer` (KeyViewerLayout.cs:206) / `OnDestroy` (KeyViewer.cs:578). A per-rebuild
  `ResetKeyViewer` — which is what a profile switch, a node delete, an image-path edit and every
  in-place colour/style refresh go through — calls `ReleaseCustomTextures()`
  (KeyViewerLayout.cs:1745), which deliberately skips cache-owned textures. There is no LRU, no
  reference count and no size-ordered eviction, so an entry nothing points at any more keeps both
  its Texture2D and its share of the 256 MB ledger alive for the rest of the session. Once
  `liveCachedImageBytes + wanted > MaxCachedImageBytes`, the new image is destroyed and `null` is
  returned, and the caller draws the grey placeholder box.
- path: FreeMake layout with image nodes → user switches profile via the settings window →
  `KeyViewer.SwitchProfile` (KeyViewer.cs:2089) → `ResetKeyViewer()` → `ReleaseCustomTextures()`
  (skips cache-owned) → the previous profile's textures stay in `textureCache` and in
  `liveCachedImageBytes` → the new profile's `SetupCustomImageNode`
  (CustomLayout.cs:1395/1401/1417) calls `LoadTextureCached`, hits `liveCachedImageBytes + wanted >
  MaxCachedImageBytes` at KvImageLoader.cs:143, logs one `Loader.Warning` and returns null →
  `raw.color = new Color(0.25f, 0.25f, 0.28f, 0.85f)` placeholder. The 256 MB stays committed, so
  every subsequent profile shows placeholders too, even though actual live usage is a fraction of
  the budget. The only recovery is toggling the display off and on again (full teardown).
  Same for simply deleting image nodes: the freed VRAM is never returned.
- fix: Give the cache a real reclaim path. Either (a) call `KvImageLoader.ReleaseCachedTextures()`
  from `ResetKeyViewer` when the *set of requested paths* changes (i.e. not on the in-place
  refreshes that motivated the cache), or (b) track a per-build "last requested" set and evict
  entries absent from it, decrementing `liveCachedImageBytes` by `LastWidth * LastHeight * 4`
  exactly as the changed-file path does at 132–133.

## RepairLegacyScaledTextPoison clobbers a legitimate CountTextOpacity whenever the label opacity is 0
- file: JipperKeyViewer/KeyViewer/Settings/KeyViewerSettings.cs
- line: 601–606
- severity: medium
- what: The trigger is `TextOpacity <= 0 || CountTextOpacity <= 0`, but the body writes **both**
  fields to `1f`. A node whose label opacity is legitimately 0 therefore has its
  `CountTextOpacity` — a value the runtime really does honour (`KeyViewer.EffectiveTextOpacity`,
  CustomLayout.cs:1692) — silently reset to fully opaque. Unlike `ApplyLegacyFmNodeDefaults`, this
  repair is deliberately called with **no version gate** (CustomLayout.cs:437–438 gates, 450 does
  not), so it runs on every overlay rebuild, and the rewritten value is then written to disk by the
  next `SaveCurrentProfile`.
- path: Import a DmNote preset whose companion CSS hides a label — `KeyViewerDmNoteCssMapping.ApplyText`
  (KeyViewerDmNoteCssMapping.cs:156–161) maps CSS `color: transparent` to `node.TextOpacity = 0f`
  ("how the themes hide the label and paint a ::before logo instead") — while the counter colour is
  translucent, e.g. `--counter-color: rgba(255,255,255,0.4)`, which `ApplyCounter` stores as
  `CountTextOpacity = 0.4f` (line 189). The import then does
  `imported.NodeDefaultsVersion = ProfileData.NodeTextDefaultsVersion` (KeyViewerDmNoteImport.cs:304)
  and `SwitchProfile` (line 323) → `ResetKeyViewer` → `EnsureCustomNodes` → CustomLayout.cs:450
  `RepairLegacyScaledTextPoison` sees `TextOpacity (0) <= 0f` and sets **both** opacities to 1. The
  counter renders at 100 % instead of 40 %, and the next profile save persists it. The same fires
  for any hand-edited profile or third-party `.jkv` that carries a 0 label opacity.
- fix: Only reset the field that is actually poisoned, and don't touch a sibling the trigger didn't
  implicate — e.g. `if (node.TextOpacity <= 0f) node.TextOpacity = 1f; if (node.CountTextOpacity
  <= 0f) node.CountTextOpacity = 1f;`. Better still, gate the whole call on the same one-shot
  `NodeDefaultsVersion` marker used by `ApplyLegacyFmNodeDefaults` (or record a per-node "repaired"
  bit) so it can never re-fire on a value the importer or a hand edit deliberately produced.
