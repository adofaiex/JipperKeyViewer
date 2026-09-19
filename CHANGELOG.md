## 1.7.2

### 🔀 Two variants merged into a single JipperKeyViewer

- **The AB-bundle and FileBased variants are now one mod**: this mod previously shipped as two variants — `JipperKeyViewer` (AssetBundle-packed resources) and `JipperKeyViewer-FileBased` (resources loaded from files on disk). As of this version they merge into a **single JipperKeyViewer**: one package, one codebase.
- **Resource pipeline unified to runtime construction** (the former FileBased mechanism): sprites load from PNG and fonts from OTF/TTF at runtime, fully **decoupled from the game's Unity version** — a game engine upgrade no longer requires repacking resources or re-shipping the mod. The default resources (3 PNGs + 2 fonts) are DEFLATE-embedded inside the main DLL and self-extract to `assets\` on first launch: only missing files are written (**user-replaced files are never overwritten**), via a temp-file + atomic-rename write with crash-leftover self-healing.
- **Upgrading / migration**:
  - **Coming from the AB-bundle variant**: extract the new package **over your existing folder in place** — that's the whole upgrade.
  - **Coming from the FileBased variant**: move `config\`, `CustomFont\`, `CustomImages\` (plus any `assets\` files you replaced yourself) from the old folder (`JipperKeyViewer-FileBased`) **into the new `JipperKeyViewer` folder**, then **delete the old variant's folder**. ⚠️ **Having both installed at once double-loads the mod and stacks two overlays** — remove the old one.
- Project-side changes: loader entries renamed `JipperKeyViewer.Loader.UMM / .Loader.Melon`; all AB-variant artifacts (incl. the 23.5 MB bundle) and both Unity packing projects removed; CI now produces a single-variant single zip (`JipperKeyViewer.zip`).

### 🚀 New Features

#### FreeMake custom-layout editor
- **Rounded key boxes + per-node border thickness**: a node whose Corner Radius is above 0 draws a procedurally generated rounded rect instead of the square 9-sliced sprite (a 9-slice cannot round a corner), with an optional independent outline width. Radius is clamped to half the shorter side; Border Thickness 0 keeps the sprite's own 9-slice border. The editor mirrors the shape on the canvas.
- **Text outline & shadow**: TMP's SDF outline and underlay shadow are now configurable, separately for key labels and press counts (a large label and a small count rarely want the same treatment), and per node via a "Custom Text Style" override that seeds itself from the current globals when switched on — so enabling it never changes the rendered text. Styles are cached per resolved value, and the font asset's own material is never mutated (doing so would leak the style into every TMP text in the game). Existing profiles keep the previous look: shadow defaults reproduce the old hard-coded black/0.5/offset 1,-1 underlay exactly.
- **Video key nodes**: an image node given a video path plays it (mp4/mov/webm/avi/wmv/m4v) through a VideoPlayer rendering into a RenderTexture, replacing the still image while keeping binding, counting, press images, opacity, rain and layering. Players survive layout rebuilds (a rebuild per settings nudge would otherwise restart the decoder — a visible black flash) and are released only when their node disappears or the mod tears down. Relative paths resolve under `CustomImages\`, and a missing or unsupported file degrades to the still image instead of an invisible node.
- **Share packages (.jkv)**: export the current profile as a ZIP holding the profile JSON, package info and every referenced image/video, with node paths rewritten to bare file names. A profile using a **custom font** gets the font file bundled too (a `fonts\` entry), extracted into `CustomFont\` on import (same no-overwrite rule; takes effect after the next game start) — game fonts and the bundled CJK/MapleStory fonts exist on every install and are never packaged. Import extracts the assets into `CustomImages\` (never overwriting existing files, with a zip-slip guard) and always creates a NEW profile — importing twice yields two profiles rather than silently replacing one. The package records the export-time canvas width and import rescales node X/width/rain-offset-X to the local aspect ratio; the fixed 1080 reference height means Y and height are never touched.
- **Named press-animation easing**: the press animation gained a 27-entry easing selector (linear, smoothstep/smootherstep, the Penner sine/quad/cubic/quart/quint/expo/circ/back families) with a curve preview next to both the global Display-tab control and the per-node override, plus an adjustable duration. Defaults are `linear` at 80ms — byte-for-byte the previous hard-coded behaviour, so existing setups look unchanged. Easing is applied to the normalized progress before interpolation, so an ease-out curve still lands exactly on the target scale.
- **Arrange toolbar (toolbar "Arrange")**: left / horizontal-center / right, top / vertical-center / bottom alignment (2+ selected) and horizontal / vertical even distribution (3+ selected, outermost keys pinned, the rest spread at equal center spacing). **Alignment is gap-preserving**: keys are sorted by position, the outermost one is pinned to the target line and the rest keep their original relative spacing — aligning a row of keys never collapses them onto one point. Also **array stamping** (N copies × spacing along an axis, respecting the same per-group budgets as paste). All undoable.
- **Toolbar "Video" button**: creates a video node in one click (still an image node underneath — set the path and loop toggle in the property panel afterwards), instead of first adding an image node and hunting for the path field. Tri-lingual labels included.
- **Per-node count format (thousands separator)**: every node can independently toggle the "1,234" grouping — off follows the global toggle; opting in seeds the flag from the CURRENT global so enabling never changes the rendered number. Applies to the node's own press count, and on KPS/Total panel nodes to the panel value. Keys, image/video nodes and stat panels all qualify.

### 🐞 Bug Fixes
- **Editing a group's count no longer overwrites every other group's Total**: the forced-refresh paths (`RefreshAllCountDisplay`, `RefreshKpsTotalLabels`) wrote the *global* `TotalCount` into every Total panel. Since the per-frame loop skips a group whose value it believes is already displayed, the wrong number stuck permanently rather than self-correcting. Both paths now refresh each custom stat panel from its own group — the per-frame path was already group-aware, so group panels were only ever corrupted by these two forced refreshes.
- **Video nodes now play automatically after creation/loading**: the VideoPlayer was only ever Prepared, never Played — the node stayed black until some later rebuild (e.g. dragging it in the editor) happened to route it back through the texture manager. Playback now starts from the `prepareCompleted` callback once the decoder is ready.
- **Fixed: the game froze on exit while a video was playing**: an intermediate fix issued `Play()` unconditionally; calling it before `Prepare()` finishes wedges the decoder thread, and teardown's `Stop()` then blocked on it forever, hanging the whole game on quit. `Play()` is never issued on an unprepared player anymore, and the `prepareCompleted` handler is detached before stopping.
- **Border Thickness finally works on square keys**: the value was only read by the rounded path — square keys went through the 9-sliced sprite path where the baked 11px sprite border made the setting a no-op no matter the value. A square key with thickness > 0 now draws an explicit square ring of that width on the outline layer (thickness 0 keeps the sprite's own border, so existing layouts are unchanged). The editor canvas preview follows the same rule.
- **Procedural outline rings are no longer invisible (the real cause of "the border vanishes when corner radius is on")**: `KeyOutline.png` is a thin frame texture — only its outer ~4px are opaque, the centre texel is alpha-0 — yet the rounded and square rings sampled the sprite's CENTRE, multiplying the outline colour by a transparent texel and rendering the whole ring invisible. Rings now sample inside the opaque frame band; the fill path is unaffected (KeyBackground.png's centre is opaque).
- **A too-thick rounded border no longer erases the whole ring**: when the inset rect collapsed (thickness ≥ half the box), the old code skipped the entire outline. It now degrades to drawing the outer ring instead of vanishing.
- **Image texture leak**: decoration-node textures had no ownership list — destroying a RawImage does not destroy its Texture2D, so every layout rebuild leaked one GPU texture per image node. Decoration textures are now registered and released idempotently alongside the keyed textures (`Key.CustomTex*`) at both teardown points.
- **Rain row mismatch**: rain row assignment was derived from slot order, but custom slots are assigned in Depth order — the 9th and later keys always took row 2's per-frame speed/height regardless of their settings. Assignment now follows each node's own `RainRow`.
- **Fixed-layout per-key counts (Colors tab)**: any value can be typed directly, with Total synced by the difference; the reset button now also subtracts `TotalCount`, fixing the "Total forever over-counted after a reset" inconsistency and matching FreeMake's reset semantics.

## 1.7.1
### 🚀 New Features


#### FreeMake custom-layout editor
- **Node-based custom layout**: a new "自定义 (Custom)" layout style plus an independent in-game editor window (Layout tab). Keys, one-or-more KPS/Total panels, and up to 8 images per group are freely positioned on a canvas that draws the real screen bounds. Editing follows the FreeMake-style paradigm popularized by community key-viewer editors: draw-order hit testing, click-picking that prefers the already-selected node in an overlap stack, double-click cycling through stacked hits, drag with alignment snapping to other nodes and screen edges/centers (screen-constant threshold; Alt bypasses, Shift locks the axis), "actually aligned" guide lines, marquee selection, and Shift+click for locked background images. All code is an original Unity IMGUI implementation. Arrow keys nudge, Del deletes, Ctrl+C/V duplicate with a growing offset, Ctrl+Z/Y undo/redo over a snapshot timeline with a coalescing window.
- **Built-in layout presets**: the Preset button offers 12K/16K/20K/10K/8K/14K/24K/108K — the fixed layouts' arrangements rebuilt as editable nodes (launch-line rain offsets included), inheriting the profile's bindings, per-key counts and custom texts. Two destinations: a NEW profile cloned from the current one (default, non-destructive), or added into the current canvas with overlap avoidance. Each application creates its own layer group. 12K/20K variants follow the Standard Key Width toggle.
- **Layer groups**: named visibility batches — one toggle shows/hides a whole batch (hidden nodes don't render, respond, or count). Groups carry their own KPS/Total panels, so several complete layouts live in one profile with independent statistics. Per-group node budgets: 112 key-like nodes + 8 decorative images each. Empty groups are pruned automatically; group names reuse the smallest free number; icon buttons with hover tooltips.
- **Per-node overrides**: every Display- and Rain-tab setting has a per-node escape hatch (off = follow the global/row setting) — background/outline/text colors with pressed variants, custom & pressed texts, font size, hide label/count, rain width/height/speed, X/Y offsets, per-row mapping, two-color gradient, shadow, outline, trail-top fade, release fade, press scale (enable + value), counter bounce (bezier/scale/duration), KPS/Total text layout (centered/stacked/value-only), depth, per-key KPS, ghost-key capture. Multi-select edits show the active node's values (cyan canvas frame), apply to the whole selection, and display `—` when values are mixed.
- **Multiple KPS/Total panels**: one pair per layer group (gated by group visibility); ungrouped panels keep the global single-pair semantics. Panels in a group count only that group's keys — per-group press queues for KPS, per-group sums for Total.

#### Replay key sync (TogetherBootstrap-style replays)
- Replays driven by the TogetherBootstrap bootstrapper (TGT) move the game's judgment directly — replayed presses never pass through ANY input layer (probed live: the OS async key table, Unity Input, the game's async-input masks and its key event queue all stay silent during replay), so no key viewer that polls input can see them. TogetherBootstrap instead detects a specific key-viewer mod named "KeyViewer" at startup and Harmony-patches its input funnel to feed the replay's recorded key state. This mod ships a compatibility shim for that exact integration: a tiny embedded assembly whose name, mod entry and public input API mirror that KeyViewer surface. During a replay, the replay system's own patch routes the player's original technique through the shim, and the centralized key source (`KeySource`) picks it up by reflection — the viewer reproduces the recorded fingering live, with counts/KPS/rain behaving exactly like physical presses.
- No extra mod-list entry: the shim is embedded as a resource inside JipperKeyViewer.dll and loaded at init; if the standalone KeyViewer mod is installed, it takes precedence and the embedded shim stays unloaded. Physical keys keep flowing through plain Unity Input exactly as before.

#### Persistence & stability
- **Profiles persist through Newtonsoft** (Fields contract + a Unity-struct converter): the runtime JsonUtility drops class-array fields, which silently lost entire layouts; legacy interim formats (embedded JSON strings) import automatically, and unparseable files are backed up as `*.corrupt` before any fallback.
- **Known behavior**: an old build reading a Custom profile falls back to Key16 (enum clamping).

#### Other
- **Standard Key Width** toggle normalizes mixed-width back rows to 50px; the 108-key full keyboard gained whole-block move sliders and unified color; KPS/Total text can be centered or stacked on flat boxes; per-key font sizes; count formatting; streamer mode.

### 🔍 Code Audit (30 fixes from a six-way parallel review)
- **Stale text-input buffers committed to the wrong setting (severe)**: color-picker/slider text fields are named by positional counters (`cpi_N`/`fsf_N`), and a buffer entry survived its control's disappearance (foldout collapsed, tab switched) — the next field inheriting the number displayed the stale half-typed text on the very first click and parsed/committed it into a DIFFERENT setting (e.g. a half-typed `#ff` silently recoloring Outline). A per-pass drawn-controls set now garbage-collects entries whose control wasn't drawn that pass.
- **Rebind capture hijacked clicks/hotkeys**: the armed "press a key" state had no cancel path, didn't filter mouse buttons, and survived tab switches and window close — clicking any settings control bound Mouse0 into the slot, ESC bound Escape, and closing the window with the settings hotkey bound the hotkey itself. Mouse buttons are now skipped, ESC cancels, the loader reports window visibility (checked before capture), and the loader's settings hotkey is never captured (same-frame protection — fully effective under MelonLoader; UMM's own show/hide hotkey isn't exposed to mods, leaving a small same-frame residual there). Leaving the Keys tab disarms, and every SetupKey exit resets the state machine.
- **Corrupt profiles silently overwritten with defaults**: `LoadProfileFromMeta` ignored `LoadProfile`'s return value and a parseable-but-truncated JSON left a half-default Data that the next high-frequency save wrote over the user's file. Loads now validate and back up unparseable files as `*.corrupt`; a short-but-present `Count` (the legal `Count[36]` of older builds) is resized in place instead of rejected, preserving the V3→V4 migration path.
- **Invalid layout enums bricked the UI**: a hand-edited `"KeyViewerStyle": 42` reached `GetLayout`'s throw after the overlay was half-built (per-frame NRE), and the settings window itself crashed through `KpsTotalIsSlim` — unrecoverable from the GUI. Enums are clamped to legal ranges at load; `GetLayout` falls back to Key16 instead of throwing.
- **Truncated binding arrays crashed the Keys tab every GUI event**: `key8`…`GhostKey24` only had null-checks, no length validation (the exact gap class previously fixed twice for `Count`/`key108`). `EnsureSettingsArrays` now rebuilds every wrong-length KeyCode/text array from defaults; the binding rows gained matching bounds guards.
- **Font silently switched after scene loads**: `OnSceneLoaded` prunes dead fonts but never rebuilt the name→index map built once in `TryLoadResources`; `RestoreFontOnce` resolved the stored font name against stale indices, switched to the wrong font, and persisted it. The map is rebuilt after each prune.
- **Font asset leak**: `fontList.Clear()` dropped old `TMP_FontAsset`s (atlas textures/materials) without destroying them — every loader-level toggle (UMM off→on) or failed-bundle retry leaked a full set. Cleared assets are now destroyed; `CreateFontAsset` null results no longer enter the list.
- **FileBased: corrupt PNGs rendered as noise**: `ImageConversion.LoadImage`'s false return was ignored (a 2×2 garbage texture wearing an 11px 9-slice border), and exception paths leaked one texture per attempt. The return value is checked, textures are released on every failure path, and images smaller than the borders are rejected.
- **V3→V4 migration skipped other profiles on Key24**: the early return for a Key24 current profile skipped `MigrateAllProfileFiles` entirely, and the meta version gate never re-ran it — foot-key counts in other profiles stayed on the old 20-base slots forever.
- **V3→V4 migration wiped part of the just-migrated foot-key data** (pre-existing): the old foot-key range was cleared over its FULL length after the copy, but the destination range [24, 24+fs) overlaps it — footSize 8 zeroed 4 freshly migrated counts/colors, footSize 16 zeroed 12. The clear now covers only the gap between the old and new base [20, 24). Fixed in both the live migration and the batch profile loop.
- **KPS display stuck at 0 while editing its label**: `RefreshKpsTotalLabels` hardcoded "0" and the change-detection cache (`lastKps`) wasn't invalidated, so at a steady key rate the box showed the wrong 0 indefinitely. It now writes the live value and forces a rewrite.
- **Full-keyboard KPS/Total Y slider was inverted**: these two boxes were the only place where Y=1 meant "top"; they now follow the mod-wide convention (0=top, 1=bottom) like the main/foot position sliders. **v6 settings migration** flips stored values once (all profiles, batch) so existing placements keep their on-screen position.
- **Per-key font size on count texts never applied**: in `UpdateAllFonts`, the value texts got their per-key override BEFORE the global reset wiped it (order was inverted vs. the KPS/Total blocks). Order fixed in both variants.
- **Rain drops never recycled at speed ≤ 0**: typed values are stored unclamped, and a zero/negative speed never lifted drops past the track top — with fade off, every press leaked an invisible drop forever (steady memory/frame-cost growth). Speeds are floored (near-frozen but recyclable), heights/widths floored at 1.
- **Per-key KPS queues grew forever with the feature off**: enqueue was not gated on the display toggle while cleanup was — 8 bytes/press/key leaked for the whole session in the default configuration.
- **Slider drags wrote to disk ~120×/s**: every IMGUI change event called the full two-file save (profile JSON + meta). GUI handlers now use a debounced save (first change after a quiet spell saves at once; rapid changes coalesce and flush on mouse-up or after 0.5s). Critical paths (window close, disable, scene load, profile ops) still save immediately.
- **Rain height/speed/width sliders never saved**: the 18 direct-assignment sliders (normal + ghost) persisted only when some other change triggered a save — a crash right after adjusting them reverted all three groups. They now save through the debounced path.
- **Ghost rain width sliders showed row labels**: copy-paste from the start-Y block; now uses the width labels like the normal-rain block.
- **Per-key KPS toggle showed stale counts ≤1s**: the change-detection cache wasn't cleared on toggle, so a quick off→on displayed the old count until the queues drained.
- **Negative press-animation scale rendered mirrored garbage**: the visible-rect guard ran before scaling, so a typed negative scale produced negative-width rects. The mesh builder re-checks after scaling (also rejects NaN).
- **ppu scale direction was inverted** (`×ppu/100` instead of `×100/ppu`) in both shape and rain renderers, and the 9-slice inner UV used the scaled border instead of the raw one — inert with the shipped 100-ppu sprites, now correct for non-default imports.
- **Rain pool out-reset missed 5 render fields** (shadow/outline colors + offsets) — currently harmless because every creation path writes them all, now reset symmetrically anyway.
- **DisableKeyViewer didn't clear active rain state** — correctness relied on the implicit "rain indices < 24 < any array length" invariant; clearing is now explicit.
- **Misc hardening**: `MigrateV1toV2` is idempotent (already-normalized positions are no longer re-divided if the version field was lost); `DeleteProfile` aborts when the pre-delete profile switch fails; the rebind's own keypress is no longer counted for main AND ghost keys (edge absorbed); `Main.Init` guards against double subscription; three leftover double foot-key rebuilds removed; foot keys in `DrawMainKeyRows`/ghost section gained the missing bounds guards.
- **Known limitation (documented)**: key detection polls `Input.GetKey` once per frame, so a full press+release inside one frame (very low FPS) can be missed. Documented in the README; fixing it needs an event-based input source and is out of scope for this pass.
- **Game crashed natively (0xc0000005) on every launch with any profile (severe, root-caused offline)**: saving profiles through Newtonsoft walked the `Color`/`Vector2` contracts and read Unity's computed properties (`linear`, `grayscale`, `gamma`, `maxColorComponent`, …) — ECall icalls invoked from Newtonsoft's compiled expression value providers, which kills Mono silently (no exception, no log; the game just vanishes right after `[Manager] Spawning`, at the first scene-load save). The earlier Fields-mode contract resolver did NOT prevent this — forcing `MemberSerialization.Fields` inside `CreateProperties` still produced property-bearing contracts for Unity structs (verified by an offline harness that reproduces the exact load→save path with the game's own Newtonsoft 13.0.2 and the built mod DLL; the harness caught `Error getting value from 'linear' on 'UnityEngine.Color'`, the in-game twin of the native crash). Fix: Unity struct types now serialize through an explicit `UnityStructConverter` (writes/reads only r,g,b,a / x,y / x,y,z,w — no member walking at all), and the mod's own data classes carry `[JsonObject(MemberSerialization.Fields)]`. The resolver is gone. The escaped-string carriers of the interim build (`CustomNodesJson`/`LayerGroupsJson`) are still imported once when the array fields are empty (the user's 14-node layout survived this way), the list properties stay `[JsonIgnore]`d, and the startup native-crash breadcrumbs used to bisect this were removed.
- **The same crash's collateral, cleaned up**: the member-name collision that previously quarantined profiles as `*.corrupt` ("A member with the name 'CustomNodes' already exists") came from renaming the persisted array field to `"CustomNodes"` while a same-named list property existed; the attributes above resolve it at the source.
- **Per-node press scale in the FreeMake editor**: the Display tab's press animation is now configurable per key — `PressAnimEnabled` opts a node out (the global toggle stays the master gate) and `UseCustomPressAnim` + `PressAnimScale` override the global scale value for that node alone (clamped 0.3–2.0, so keys can also grow on press). With the Display-tab features already node-level — hide count, hide label, per-key KPS, font size, opacity, colors, hidden — every per-key display control now lives on the node.
- **Built-in layout presets in the FreeMake editor (non-destructive)**: a "Preset" button in the toolbar opens a strip with 12K/16K/20K/10K/8K/14K/24K — applying one creates a NEW profile (named e.g. "16K-Preset", global settings cloned from the current profile) with the fixed layout's hardcoded arrangement as editable nodes (front row, back extras, KPS/Total, plus the current foot layout's keys when set; bindings from the profile's arrays, rain rows matching the fixed layout) and switches to it. Preset nodes also seed their per-key counts and custom texts from the source profile's slot arrays (front/back by key index, foot keys at the FootKeyBase slots) — converting a fixed layout to preset nodes no longer wipes the key statistics or labels; a missing switch case (Key16) used to feed nodes the key24 bindings instead. The current layout is untouched; the editor's undo history is cleared on the switch so a Ctrl+Z can't write the old profile's nodes into the new one. The "Clear Canvas" button stays destructive-but-undoable. 108K included: all 105 keys from the shared slot table (same 56px column-step math, tall keys spanning two rows, KPS/Total panels when the full-keyboard toggle is on); the node cap rose 40→112 and per-key color arrays are no longer slot-indexed for custom layouts (bounds guard).
- **Per-node rain shadow & outline**: the Rain tab's shadow/outline settings (enable, color, offsets / enable, color, width) are now overridable per node in the FreeMake editor — off by default, the node follows its selected rain row's settings; ghost rain deliberately keeps the ghost-row settings (no per-node ghost override). The node colors fall back to the row's when unset. Together with the existing per-node rain width/height/speed, two-color gradient and offsets, every rain parameter the fixed layouts expose per row now has a per-node escape hatch.
- **Per-node KPS/Total text layout**: stat panels gained a "Custom Text Layout" override in the editor (centered / stacked / value-only via the node's Hide Label) that takes precedence over the global 居中/堆叠/隐藏标签 toggles — off by default, so existing panels keep following the Display tab. The mode resolver (`StatTextMode`) is now shared by the build path and `SetKpsTotalDisplay`, so the runtime text always matches the built text objects. Enabling the override seeds the node fields from the current effective global mode — the first version snapped to the field defaults (flat), which read as centered/stacked "disappearing".
- **Custom-layout conflicts in the Display tab (from in-game testing)**: press scale never animated custom keys (the animation only started from the fixed-layout input path — `ApplyCustomKeyEdge` now starts the same coroutine on every edge, image keys included); the counter bounce animated the count text only, which `HideCount` hides — it now animates whichever text is visible (label when the count is hidden); the global hide-main-count toggle stripped every custom key's value text regardless of node settings (the gate is fixed-layout-only now — `node.HideCount` is authoritative); 「隐藏 KPS/Total 标签」now reaches custom stat panels (they follow `KpsTotalIsSlim`, i.e. value-only mode when the label is hidden and centered is off); the custom-position sliders, DownLocation and hide-main-count toggles are hidden for the Custom layout with a hint pointing at the FreeMake editor (the main-key sliders drove `ResetKeyViewerPosition`, which no-ops there); foot keys are no longer initialized for custom layouts (a leftover foot style drew fixed-position keys into the node canvas — and `ResetFootKeyViewer`, the second path `ResetKeyViewer` takes, still wrote the FootKeyBase(24)-anchored slots into the node-count-sized Keys array, an OnGUI `IndexOutOfRangeException` when switching fixed → Custom on a profile with no nodes); a null/short hand-edited counter bezier is reset to the default curve instead of throwing per-frame.

### 🐛 Bug Fixes
- **Slider text fields couldn't type intermediate values**: The old helper re-fed `value.ToString` every IMGUI event, wiping half-typed input ("", "-", "0.") — negatives in the rain shadow X/Y fields (-10..10) were unreachable by typing — and silently re-clamped stored below-min values to the minimum just by opening the tab. Text fields now use the color picker's input-buffer pattern and only commit when the text actually changed; typed values are stored fully unclamped (negatives and out-of-range values included — the slider is just a range indicator), except that "NaN"/"Infinity" literals are rejected (NaN slips past every `<=0` guard and poisons mesh vertices).
- **Old-profile migration crashed and wiped user settings**: Builds between the profile refactor and MaxKeySlots=40 wrote `Count[36]`; the V3→V4 foot-base migration's `Array.Copy` ran past its end, the exception reset everything to defaults, and the next save overwrote the profile files — permanent settings loss. `EnsureSettingsArrays` now resizes `Count`, `PerKeyGhostRainColor`, `PerKeyFontSize`, and `key108` to their target lengths (it used to null-check only), and the batch migration of other profile files resizes first too.
- **Load failures swallowed user config**: On a parse failure or migration exception, settings fall back to defaults — but now the offending settings.json and current profile are first backed up as `*.corrupt`. All config writes are atomic (temp file + replace), so a crash or power loss mid-write can no longer truncate the file.
- **Press animation stuck shrunken when toggled off mid-press**: The release transition is gated on the toggle; turning it off now resets every key's scale and stops the running animation coroutines.
- **Ghost rain desynced when re-enabled mid-hold**: Ghost key states weren't tracked while the rain toggles were off, so a held key needed an extra release/press cycle before triggering again. Tracking no longer depends on the gates.
- **Profile foldout scanned the disk every GUI event** while expanded (several times per frame); it now syncs once on open.
- **16K drew an empty "row 3" label** in the per-key color / text-size sections (`>= 8` vs the binding tab's `> 8`; 16K has exactly 8 back keys). Unified to `> 8`.
- **Per-key text-size section shown on the 108-key layout** with mislabeled rows — it targets the standard layouts and is now hidden there.
- **24K key lists ordered 23 before 22**, opposite the on-screen layout (…22, 23 left-to-right). Sequence corrected.
- **Unknown language highlighted Korean** while I18n rendered English; unknown codes now map to English everywhere.
- **Profile duplicate checks were case-sensitive**: On NTFS "MyProfile"/"myprofile" are the same file, and renaming could delete the case variant first. The check is now case-insensitive against OTHER profiles; case-only renames stay allowed (the old code path deleted the source file before the move — now a single File.Move performs the case change).
- **KPS/Total custom text couldn't be edited on the 108-key layout**: the editor has lived at the bottom of the Keys tab since the feature shipped (v1.7.0), and the tab's full-keyboard early return hid it — even though the labels themselves render in the full keyboard's KPS/Total boxes. The Keys tab now keeps the KPS/Total text editor reachable in 108K.
- **Custom key text couldn't be cleared with backspace**: the moment the last character was deleted, the stored empty value mapped back to the default key label and the field snapped to "A" etc. — only select-all-retype or the reset button worked; the on-screen label also flashed empty for one frame. Both text fields use the input-buffer pattern (same as the sliders / color picker).
- **Keys tab was fully blank on the 108-key layout**: it now shows an explanatory note (localized in all three languages).
- **NaN alpha on the first frame when fade duration is 0**: the 0/0 NaN slips past Clamp01; zero/negative durations now behave as an instant fade.
- **Per-scene font restore was dead logic**: `OnSceneLoaded` reset the flag but `RestoreFontOnce` only ever ran from `Start`. It now runs at the end of each scene load.
- **Unity fake-null checks**: `UpdateAllFonts` / `ExecuteCountReset` used `?.` on MonoBehaviours (bypasses the destroyed-check) — replaced with explicit nulls; one `ClearActiveDrops` call gained the null guard its siblings have.
- **FileBased variant failed silently on missing assets**: font files (MapleStory/CJK) and GhostRain.png missing produced no log — a missing CJK font also broke the fallback chain and rendered CJK labels as boxes. Both now log like the bundle variant.
- **Foot keys rebuilt twice per layout/profile switch**: `ResetKeyViewer` already recreates them internally; the outer duplicate calls were removed.
- **Normal rain start-Y sliders never saved**: the three "Start Height" sliders assigned their value without the debounced save every other rain slider uses (including the ghost start-Y block directly below) — a crash right after adjusting reverted them. They now save through the same debounced path.
- **Batch V3→V4 migration dropped per-key colors for foot keys 14/15**: profiles written by 36-era builds also carry 38-length `PerKey*` color arrays, and the batch shift only writes inside the old length — migrating a dormant 16K-foot profile lost the last two slots (`Count` was resized there, the color arrays were not). The batch migration now resizes all seven color arrays first, filling the tail from the profile's own global colors.
- **Failed profile switch could resurrect legacy-length arrays**: the fallback re-load of the old profile skipped `EnsureSettingsArrays` (the success path runs it). Only reachable if the just-rewritten file couldn't be read back (failed write / external change), but that left a `Count[36]` `Data` that `RefreshAllCountDisplay` and the per-key color editors index out of range. The fallback now runs the same resize/clamp.

### 🔧 Performance
- **Zero-allocation KPS/Total update path**: `KpsTotalIsSlim` allocated a fresh LayoutDesc + ExtraSlot[] per keypress via `GetLayout` (twice per press in hide-label mode). Its result depends only on layout + standard-width, so it's cached now.
- **Settings GUI GUIStyle caching**: the per-key color button loop allocated a GUIStyle per button (~26 per frame) plus red-text styles every event — all cached; `PerKeyTypeOrder` arrays made static.
- **Bundle rebuild can't lose sprite borders**: `.meta` files aren't version-controlled, so a fresh clone re-imported the PNGs with border 0, silently collapsing 9-slicing and ghost-rain tiling in both variants. The build script now enforces the 11px spriteBorder + 100 ppu on the three sprites.

### 🧰 Build & Release
- **SDK-style csproj migration**: All six projects converted from classic (non-SDK) to `Microsoft.NET.Sdk` format. The hand-written `Properties\AssemblyInfo.cs` files stay the single source of assembly attributes and versions (`GenerateAssemblyInfo=false` — `/p:Version` remains a no-op and SetVersion.ps1 keeps working); output stays at `bin\Release\` with no TFM sub-folder (`AppendTargetFrameworkToOutputPath=false`), so the CI copy steps are unchanged; `LangVersion latest` kept (SDK defaults net481 to C# 7.3); the FileBased variant pulls shared sources via a wildcard with an explicit `KeyViewerResources.cs` exclude instead of a 19-entry link list. A `Microsoft.NETFramework.ReferenceAssemblies` package reference (PrivateAssets) means builds no longer need the .NET Framework 4.8.1 Developer Pack installed. Verified via full rebuilds with both `dotnet build` and CI-style MSBuild: zero errors/warnings, all six DLLs at 1.7.0.0, clean output dirs.
- **One-command version bump**: new `tools/SetVersion.ps1` updates all six AssemblyInfo files, the `[MelonInfo]` version string, both Info.json files, and Repository.json (versions + download URLs) in one idempotent pass. The release workflow runs the same script before msbuild — previously `/p:Version` had no effect on the classic (non-SDK) csproj files, so shipped DLL versions never changed; the dead "patch Info.json after zipping" step was also removed.
- **Resolution changes reposition immediately**: full-keyboard KPS/Total, the 108-key block offset, and custom-position bases were baked at build/slider time and stayed misplaced after a mid-session resolution change until the next rebuild; the change is now detected and positions re-applied. The 108K block's natural slot positions are resolution-independent — only the KPS/Total normalized positions and the (toggle-gated) custom offset are re-applied.
- **Untracked the Unity-generated `Assembly-CSharp-Editor.csproj`** (regenerated per editor version) and added it to .gitignore.
- **Per-row rain toggles did nothing individually**: The released version wired rows 1 and 2 both to the Row-2 toggle, leaving the Row-1 toggle dead. All three rows now toggle independently (keys 0-7 = row 1, 8-15 = row 2, 16+ = row 3), and turning a row off immediately clears that row's in-flight drops.
- **Ghost rain sprite broke on long holds**: The GhostRain sprite carries an 11px 9-slice border, and the old `Image.Type.Tiled` only tiles the inner region for bordered sprites, repeating the border strips along their own direction. The merged renderer mistakenly tiled the whole texture, so ghost columns past 100px re-drew a border every 100px. Now ported from uGUI's `GenerateTiledSpriteToVertexBuffer` verbatim: center tiles whole, side border columns tile vertically, top/bottom rows tile horizontally, corners once, borders squeeze when the rect is too small.
- **FileBased: font-size slider didn't update existing texts**: The file-based variant's `UpdateAllFonts` was missing the `fontSizeMax` write the bundle variant has.
- **Changing settings with the display off threw NRE**: Layout/width/foot-key/centering/color handlers kept running with no overlay and crashed on null. They now safely skip while the display is off; the next enable builds from the updated settings. Pre-existing.
- **MelonLoader didn't save on window close**: Melon's save events are no-ops, so settings that rely on close-time saving (rain height/speed/width, start-Y, KPS/Total labels) were only written on game quit and lost on a crash. The settings window now saves when hidden; UMM's window also stops running on a destroyed component after the mod is toggled off.

#### Rendering performance rework shipped during 1.7.1 development (originally part of v1.7.0 code, documented here for completeness)
- **Merged key-box rendering**: All key backgrounds/outlines draw into two self-drawn meshes (background layer + outline layer sharing slot state, 9-sliced) instead of two Image GameObjects per key. The main canvas drops from 500+ Graphics to 2; press color/scale changes rebuild one small mesh instead of re-batching the whole canvas.
- **Dedicated text sub-canvas**: All key texts moved onto a TextLayer sub-canvas; KPS/count text updates no longer re-batch the shape meshes and vice versa. Press-scale animation drives the text wrapper and shape mesh together (including "rain follows scale" mode).
- **Merged rain rendering**: The per-key RainLine canvases (~25 nested Canvases) and the whole Rain GameObject pool are gone — every drop draws into two self-drawn meshes (normal / ghost layers). Drops are pooled plain-data records (RawRain); each frame only updates rects and fade params and rewrites vertices: no per-drop RectTransform writes, no SetSiblingIndex. Start-Y and ghost offsets are read live at render time, so sliders act on in-flight drops directly.
- **Behavior kept**: 9-slice border math (legacy 2x size + 0.5 scale), trail gradient/shadow/outline math, ghost bordered tiling (see fix above), and ghost-above-normal stacking per key (bodies and shadow/outline). Known differences: (1) when columns from different rows overlap (wide rain / shared columns), the old build interleaved by row while now all ghost rain draws on top; (2) with a translucent rain color plus fade enabled, the old build snapped to full opacity on release before fading — the new one fades smoothly from the color's own alpha; (3) if the ghost sprite fails to load, ghost rain degrades to solid ghost-colored columns (the old fallback was opaque white).

---

## v1.7.0

### 🚀 Features
- **Hide KPS/Total Label**: New toggle that hides the "KPS"/"Total" label text and centers the value in the middle of the box, updating every frame. Only shown for non-flat layouts (10K/12K/20K standard mode).
- **Stacked KPS/Total display**: New "Stacked" toggle, only available when Centered is enabled. Label on top (compact font), value on bottom, both stacked inside the box.
- **Settings UI multi-tab refactor**: Settings window split into 6 tabs — General, Layout, Display, Rain, Keys, Colors — with a permanent top bar for master toggles. Each tab's content lives in its own partial class file for cleaner organization and easier maintenance. Current tab index persisted to settings for cross-session recall.
- **Hex color input**: Color picker fields now accept `#RRGGBB` / `#RRGGBBAA` hex paste/typing, auto-parsed to Unity `Color`. Input focus no longer resets on redraw; each channel input has a unique control name.
- **Foot key text centered**: Foot key text is now centered in the key box instead of pinned to the top.

### 🐛 Bug Fixes
- **Profile data leak on switch**: `LoadProfile` now creates `new ProfileData()` before `FromJsonOverwrite` — prevents fields from the previously loaded profile leaking into the newly loaded one.
- **Profile size not applied after switch**: `ResetKeyViewer()` now re-applies `KeyViewerSizeObject` localScale at the end, so a profile's Size setting isn't overridden by the previous profile's slider value.
- **Centered text stuck after switching layouts**: Turning centering on for one layout and switching to another now correctly restores the normal label/number layout.
- **Skip count update when main key count is hidden**: `HideMainKeyCount=true` no longer creates a `value` text object or tries to update it.
- **Full keyboard KPS/Total toggle survives layout switch**: Switching away from and back to the full keyboard no longer loses the KPS/Total show/hide state.

### 🔧 Performance
- Smoother settings access and reworked rain-drop object pool (larger, no leftover drops).

---

## v1.6.5

### 🚀 Features
- **108-key full keyboard layout**: New layout option that shows a complete physical QWERTY keyboard (with numpad). Keys are evenly spaced with a consistent 6px gap, the number pad hugs the main block, and the tall Numpad `+` / `Enter` keys span two rows. No foot keys, per-key colors, or ghost keys in this mode.
- **Dedicated KPS / Total for the full keyboard**: The full keyboard has its own "Show KPS / Total" toggle, plus separate position controls and a size slider (40–400px). A "Full Keyboard Unified Color" option lets KPS/Total share the keyboard's background, outline, and text colors.
- **Move the whole keyboard freely**: The custom-position sliders now move the entire 108-key block. Drag the X/Y sliders to pin it to any screen edge (left/right/top/bottom); KPS/Total keep their own independent position and are not dragged along.
- **Center the KPS / Total text**: New "Center KPS / Total Text" option. On flat (side-by-side) KPS/Total boxes it merges the label and number into one centered line that re-centers as the number grows (e.g. `KPS 123`). Stacked (top/bottom) layouts keep the label above the number and hide this toggle.
- **Cleaner full-keyboard settings**: Unrelated toggles (hide main count, per-key KPS, streamer mode) are hidden in full-keyboard mode, and the KPS/Total controls get their own collapsible section above the color settings.
- **Rebind the settings hotkey in-game (MelonLoader)**: A new "Settings Hotkey" row in the settings window lets you click the button and press any key to rebind the hotkey that opens/closes the UI. No more editing `MelonPreferences.cfg` by hand.

### 🐛 Bug Fixes
- **Crash when enabling custom position on the full keyboard**: Enabling custom position no longer throws an error on the 108-key layout.
- **Stray foot keys on the full keyboard**: Toggling custom position no longer makes foot-key controls or phantom keys appear in full-keyboard mode.
- **Centered text stuck after switching layouts**: Turning centering on for one layout and switching to another now correctly restores the normal label/number layout.

### 🔧 Performance
- Smoother settings access and a reworked rain-drop object pool (larger, no leftover drops); slightly larger internal queues for key-press timing.

## v1.6.4.1

### 🚀 Features
- **Dual loader support**: Works with both UnityModManager and MelonLoader. Core DLL is loader-agnostic, bridged via the `IModLoader` interface. Four thin loader projects added.
- **MelonLoader settings UI**: Press F1 (default, rebindable) to open the settings window; window scales with resolution.
- **MelonLoader hotkey configurable**: Edit the `Hotkey` field under `[JipperKeyViewer]` in `UserData/MelonPreferences.cfg`.
- **`[MelonGame]` removed**: Loads under any MelonLoader game (TMP support required).
- **Key Font Size**: New `KeyFontSize` slider (8–72) replaces hardcoded `fontSizeMax=20`.

### 🧹 Refactor
- **Main.cs** loader-agnostic: `Init(IModLoader)` + `EnableNow()` replace the UMM binding.
- **ModLoader.cs**: New `IModLoader` interface + static `Loader` accessor.
- **KeyViewerResources.cs / I18n.cs** etc.: All `Main.Mod.*` references replaced with `Loader.*`.
- **Info.json**: Entry points to `JipperKeyViewer.Loader.UMM.dll`.
- **Build**: 6-project build packaging both loaders and both variants.

## v1.6.4

### 🚀 Features
- **Key Font Size**: New `KeyFontSize` slider (8–72) replaces hardcoded `fontSizeMax=20`. Adjust key label text size live — fixes Tab (⇥) and Space (␣) rendering too small without `<size>` tag hacks.
- **Key Press Animation**: Keys shrink smoothly on press, return on release (80ms lerp coroutine). Toggle `EnablePressAnimation`, adjust `PressAnimationScale` (0.5–0.95), and optionally `EnablePressAnimationOnRain` so rain drops animate together. Visuals wrapper isolates scaling from rain containers.
- **Per-Key Ghost Rain Color**: New `PerKeyGhostRainColor` array with dedicated color picker in per-key color editor — set each key's ghost rain color independently from normal rain.

### 🐛 Bug Fixes
- **14K back row alignment**: Corrected to match "16K cut off outermost columns" — `BackSequence14={13,9,8,10,11,12}` with keys centered at columns 1–6 (x=54–324). Per-key counts stay at consistent screen positions when switching between layouts.
- **Rain container no-sharing**: Each key keeps its own RainLine; `ShareRainContainer` now only adjusts X offset and width for front-column alignment instead of destroying/redirecting. Eliminates Z-order issues between rows without Canvas sorting hacks.
- **Canvas.sortingOrder removed**: `FixRainContainerSortOrder()` removed — caused rain containers to override parent-key layering. GraphicRaycaster also removed from RainLine (unnecessary).
- **RainGraphic shadow gradient**: Shadow quad changed from solid-color `AddQuad` to `DrawRainQuad` — follows the top-edge gradient fade along with main rain and outline.
- **24K back sequence**: Corrected `BackSequence24` to match row 2/row 3 layout order.
- **AutoAssignRainbowColors**: Refactored to assign colors per-layout key count (+ foot keys + KPS/Total) instead of sequential index.
- **Animation null-guard**: Added `if (animTarget == null) yield break;` in coroutine to prevent NRE on destroyed keys.
- **NRE on load**: Color array initialization uses `SafeEnsure` helper; `InitPerKeyColors` rain color loop uses `footBase`.

### 🧹 Refactor
- **FootKeyBase fixed to 24**: Removed conditional (was `Key24 ? 24 : 20`). V3→V4 migration shifts foot key data from indices 20 → 24 for all profiles.
- **Image-based rain shadow/outline**: Added `shadowImage`/`outlineImage` child objects on Rain with `SetupShadow`/`SetupOutline` methods. Gradient texture synced to all three images.
- **EnsureColorArray/SafeEnsure**: Unified helper replaces per-field null checks for color arrays.
- **PerKeyTypeOrder extended**: Includes type 7 (ghost rain color) for main keys.
- **Settings.Version bumped**: 3 → 4 for foot base migration.

## v1.6.3

### 🚀 Features
- **24K layout**: New 24-key layout (8+8+8) — three full rows of 8 keys each at 50px, all 54px apart. Foot keys use indices 24-39, allowing up to 16K foot key selection. Ghost key bindings and third-row rain settings fully supported. Layout tuned with frontY=375, KPS/Total at y=221, 6px gap between rows and KPS (matching 16K standard).
- **Standard Key Width mode**: Toggle `StandardKeyWidth` that converts mixed-width back rows (12K key 8/10 from 77→50px, 10K 129→50px, 20K third row 77→50px) with widened KPS/Total filling the ends  — uniform back-row look.
- **Ghost rain independent height/speed/width**: New `GhostRainHeightRow1/2/3`, `GhostRainSpeedRow1/2/3`, `GhostRainWidthRow1/2/3` sliders under the ghost rain section.

### 🐛 Bug Fixes
- **12K/10K grid rain position**: Back row keys with non-standard widths (12K key 8/10=77px, 10K key 8/9=129px) now share the front row's RainLine (key 9→2, 8→3, 10→4, 11→5 for 12K; 8→3, 9→4 for 10K). Rain drops align with the front column instead of centering within wide keys.
- **Rain render order with shared containers**: Back row drops hidden behind front row drops in shared containers. Fixed by `SetSiblingIndex((row-1)*2 + isGhost)` — front=0, middle=2, third=4, ghost=base+1.
- **14K layout jump**: Switching from 16K to 14K dropped the overlay by 21px. All 14K Y values normalized to 16K (frontY 299→320, backY 245→266, KPS 199→220).
- **8K layout too high with DownLocation**: 8K repositioned as "16K minus second row" — frontY=266 (16K's back row position), KPS/Total y=220 (16K KPS position). DownLocation now aligns KPS at y=20 matching 16K.
- **20K third row ghost key UI reversed**: Ghost key buttons showed 16,17,18,19 but visual layout is 17,16,18,19. Now uses `BackSequence20[8..12]`.
- **Custom position foldout conflated with toggle**: Foldout was acting as on/off switch. Now separate expand state `CustomPositionExpanded` + internal enable toggle.
- **DrawMainKeyRows third row loop guard**: `i < keyCodes.Length` was checking loop index instead of `backSequence[i]`. Corrected guard.
- **24K third row rain not showing**: `IsRainEnabledForKey` capped at index 20, blocking indices 20-23. Changed to `keyIndex < FootKeyBase`.
- **24K KPS/Total overlapping third row**: KPS/Total at y=165 overlapped with third row (y=232). Raised to y=221 with 6px gap.
- **24K layout switching stale keys**: Switching out of 24K left Keys[20-23] with stale 24K main key objects. Fixed `ChangeKeyViewer()` to also call `ResetFootKeyViewer()`.
- **24K foot key limit removed**: MaxKeySlots=40 allows 14K/16K foot keys in 24K mode (indices 24-39).
- **Crash on settings load**: `InitPerKeyColors` called `FootKeyBase` before `Settings` was assigned, causing NullReferenceException. Replaced with `MaxKeySlots` constant.

### 🧹 Refactor
- **Rain container sharing**: `ApplyRainContainerSharing()` / `ShareRainContainer()` redirects back-row `key.rain` to front row's RainLine. `UpdateRainContainerPositions` uses `HashSet<RectTransform>` dedup.
- **Dynamic foot key base**: `FootKeyBase` property replaces hardcoded 20 throughout the codebase (24 for 24K, 20 otherwise).
- **MaxKeySlots=40**: All key arrays (`Keys`, `Count`, `keyPressTimes`, `lastPerKeyKps`) use `MaxKeySlots`. Per-key color arrays use `MaxKeySlots + 2`. `KeyIndex` returns `Keys.Length`/`+1` for KPS/Total. All hardcoded 36/37/38/20 boundaries removed.
- **HasThirdRow property**: Replaces `style == Key20` checks in 18 locations across rain settings, shadow/outline, and ghost rain sections. Covers both 20K and 24K.

## v1.6.2

### 🚀 Features
- **Per-row rain start height**: `RainStartYRow1/2/3` sliders replace hardcoded container Y (−223/−169/−115), live preview on slider drag via `UpdateRainContainerPositions()`
- **Per-row ghost rain start height**: `GhostRainStartYRow1/2/3` absolute position sliders, independent from normal rain.
- **Per-row rain shadow**: Independent shadow toggle, color, X/Y offset for each row (Row1/2/3), separate settings for ghost rain
- **Per-row rain outline**: Independent outline toggle, color, width for each row (Row1/2/3), separate settings for ghost rain
- **Per-row rain width**: New `RainWidthRow1/2/3` sliders (10–200px), each row's rain width adjustable independently
- **Rain speed/height range extended**: Slider ceiling raised from 1000→2000, text field upper bound fully removed
- **Korean (ko) language added**

### 🐛 Bug Fixes
- **Profile load field leak**: `LoadProfile` used `FromJsonOverwrite` on existing `Settings.Data` — new fields missing from old profile JSONs kept their value from the previous profile, behaving as global. Fixed by resetting `Settings.Data = new ProfileData()` before overwrite.
- **Ghost rain invisible**: `RainGraphic` always enabled for ghost rain, empty mesh (0 verts) with both shadow/outline off interfered with child `ghostImage`. Fixed: only enable `graphic` when shadow or outline active.
- **Ghost rain start Y trail size**: `startY` was mixed into trail length calculation (`FinalSize`), making ghost drops appear with an initial trail. Separated `y` (elapsed → size) from `dropY = startY + y` (→ position).
- **Rain invisible on first press**: `RainSystem.PreWarmPool` created objects without a valid Canvas. Removed pre-warming entirely.
- **Color foldout resetting per-item state every frame**: Foldout expanded state was overwritten each frame.
- **KPS/Total sub-foldout comparison**: Used `>=0` instead of `==t`.

### 🔧 Performance
- **Zero-allocation KPS/Count display**: `NumBuffer.Format` with pre-allocated `char[32]` + `TMP_Text.SetText(buf, offset, length)` eliminates per-frame `ToString` allocations
- **Queue pre-allocation**: `PressTimes` (256) and per-key KPS queues (32) pre-allocated
- **Idle skip**: `_hasKeyPressActivity` flag skips `ProcessPerKeyKpsInUpdate` loop when no keys pressed

### 🧹 Refactor
- **Profile system**: `KeyViewerSettings` split into `ProfileData` (all config) + meta wrapper. Multi-profile create/switch/rename/delete
- **GUI refactor**: Extracted `FloatSliderField` / `DrawFoldoutButton`; split `DrawSettingsWindow` into 10+ standalone section methods
- **RainSystem refactor**: `UpdateEffects` split into `SyncCachedSpeeds` / `UpdateSingleRainDrop` / `ApplyRainTransforms` / `UpdateFadeOut` / `UpdateTrailEdge`
- **AddQuad signature simplified**: 7 params → 4 params `(VertexHelper, Rect, Color, Color)`
- **I18n rewrite**: Custom JSON parser replaced with `JsonUtility.FromJson<LangFile>`
- **FileBased font style parity**: Added `FontStyleFlags` support (Bold/Italic/etc.)

## v1.6.1

### 🚀 Features
- Custom `RainGraphic` replaces `Image` for normal rain — lighter rendering (solid/gradient quad, 4 verts, no texture sampling)
- Ghost rain now correctly renders the `GhostRain.png` sprite (child `Image` + Tiled mode)
- Per-row ghost rain color customization (`GhostRainColor` / `GhostRainColor2` / `GhostRainColor3`)
- Edge fade slider changed from percentage to pixels (1–200px)

### 🐛 Bug Fixes
- Ghost rain sprite was loaded but never used for rendering — now displays correctly
- Release fade and edge fade now work together without conflict

### 🔧 Performance
- Normal rain: 4 verts / 2 tris, no texture sampling — lighter than old `Image`
- Ghost rain: child `Image` tiling — negligible overhead (low frequency)

### 🧹 Misc
- Removed dead code (unused `Init` overloads, pool methods, stale `ghostSprite`)
- Bumped version to 1.6.1
