# GUI audit — `1.7.2..HEAD`

Scope: `KeyViewerColorGUI.cs`, `KeyViewerSettingsGUI.cs`, `KeyViewerGUI.cs`, `KeyViewerRainGUI.cs`,
`KeyViewerBindingGUI.cs`.

## Per-key text-size guard returns without closing its outer layout group
- file: JipperKeyViewer/KeyViewer/GUI/KeyViewerSettingsGUI.cs
- line: 957, 975, 989-993
- severity: medium
- what: `DrawPerKeyTextSizeSection` opens **two** nested `GUILayout.BeginVertical("box")` groups (the outer at :957, the per-key box at :975). The new `keyCodes == null || keyCodes.Length < 8` guard added in this diff calls `GUILayout.EndVertical()` exactly once and then `return`s, closing only the inner box. The outer box is never closed, so the GUILayout group stack is left one level deep for the rest of `DrawDisplaySection`, everything drawn after `DrawPerKeyTextSizeSection()` (text style block, KPS/Total centring, press animation, easing selector) and the `GUILayout.EndVertical()` in `DrawSettingsWindow`. That is exactly the failure this codebase documents repeatedly as "the whole settings window mislays and stops responding until restart" — the defensive guard is worse than the crash it was written to prevent. The identical guard in the twin `DrawPerKeyColorSettings` (KeyViewerColorGUI.cs:435/445-449) is balanced, so only this copy is wrong.
- path: `DrawSettingsWindow` → `case 2: DrawDisplaySection()` → `DrawPerKeyTextSizeSection()`; with the per-key text-size foldout expanded and `EnablePerKeyTextSize` on, if `GetKeyCode()` returns null or fewer than 8 entries the `if` at :989 fires → one `EndVertical` for two `BeginVertical` → unbalanced IMGUI layout stack from that frame on. `EnsureSettingsArrays` currently repairs `key8..key24` via `EnsureKeyCodeArray`, so this is defence-in-depth that only misfires if a load path ever swaps `Settings.Data` without it — but that is exactly the scenario the guard was added for.
- fix: Close both groups before returning, e.g. replace the guard body with two `GUILayout.EndVertical(); return;`, or (better) hoist the check above the outer `GUILayout.BeginVertical("box")` at :957.

## Per-key "Reset Counts" button renders blank at count 0 and goes stale on language switch
- file: JipperKeyViewer/KeyViewer/GUI/KeyViewerColorGUI.cs
- line: 670-671, 837-854
- severity: medium
- what: The per-key button label is now cached in `perKeyResetLabelText[s]` and only rebuilt when `perKeyResetLabelCache[s] != Settings.Data.Count[s]`. The cache sentinel is `0` (default array value) and so is a fresh `ProfileData.Count`, so on a brand-new profile — and after the user presses the very button that zeroes the count — the label is never built and `GUILayout.Button(perKeyResetLabelText[s], redBtnStyle)` is handed `null`, drawing an **empty** button where `I18n.Tr("reset_counts") + " (0)"` used to be. The same cache is keyed on the count alone, so switching the UI language (`DrawLanguageSection` → `I18n.Lang`) leaves every per-key button showing the caption in the previous language until that slot's count happens to change.
- path: `DrawSettingsWindow` → `case 5: DrawColorSection()` → `DrawPerKeyColorSettings()` → click a slot → `DrawPerKeyColorEditor(s)` → `DrawPerKeyCountReset(s)` (only for `s < MaxKeySlots`, line 651). With `Settings.Data.Count[s] == 0`, `perKeyResetLabelCache[s] == 0` so the `if` at :838 is false and `perKeyResetLabelText[s]` stays `null`; the user sees an unlabelled red button on the Colors tab for every slot that has never been pressed.
- fix: Initialise the sentinel so 0 is never a valid "already cached" key — e.g. fill `perKeyResetLabelCache` with `int.MinValue` in a static constructor (or start `perKeyResetLabelText` at the untranslated string) — and include `I18n.Lang` in the cache comparison, as `ColorNames`/`previewLabel`/`cachedTabLabels` already do in this same file.
