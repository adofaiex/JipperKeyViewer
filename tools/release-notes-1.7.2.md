**Full Changelog**: https://github.com/adofaiex/JipperKeyViewer/compare/1.7.1...1.7.2

# :tada: JipperKeyViewer 1.7.2

## :warning: READ THIS FIRST — the two variants are now ONE mod

This release **merges the AB-bundle and FileBased variants into a single JipperKeyViewer**: one package, one codebase. Resources are built at runtime from PNG/OTF files — fully decoupled from the game's Unity version, so a game engine upgrade no longer requires repacking resources. Default assets are DEFLATE-embedded inside the DLL and self-extract to `assets\` on first launch; **user-replaced files are never overwritten**.

**How to upgrade:**
- **AB-bundle variant users** — extract the new package **over your existing folder in place**. That's the whole upgrade.
- **FileBased variant users** — move `config\`, `CustomFont\`, `CustomImages\` (plus any `assets\` files you replaced yourself) from the old `JipperKeyViewer-FileBased` folder **into the new `JipperKeyViewer` folder**, then **delete the old variant's folder**. :warning: **Keeping both installed double-loads the mod and stacks two overlays — remove the old one.**

## :sparkles: New Features

### FreeMake custom-layout editor
- **Rounded key boxes + per-node border thickness**: a corner radius above 0 switches the slot to a procedural rounded mesh (a 9-slice cannot round a corner) with an independent outline width; square keys honor a custom border thickness too (thickness 0 keeps the sprite's own border).
- **Text outline & shadow**: TMP's SDF outline and underlay shadow, configured as independent pairs for key labels and press counts, plus per-node overrides that seed themselves from the current globals — enabling an override never changes the rendered text.
- **Video key nodes**: give an image node a video path (mp4/mov/webm/avi/wmv/m4v) and it plays through a VideoPlayer → RenderTexture, keeping binding, counting, press images, opacity, rain and layering. Players survive layout rebuilds and auto-start once prepared; a dedicated toolbar button creates video nodes in one click.
- **Share packages (.jkv)**: export the current profile plus every referenced image/video **and the custom font** as one ZIP; importing always creates a NEW profile, never overwrites existing assets, and rescales node X/width to the importer's aspect ratio.
- **Named press-animation easing**: 27-entry easing selector (linear, smoothstep, the Penner families) with a curve preview next to both the global control and per-node overrides, plus adjustable duration. Defaults reproduce the previous hard-coded behaviour exactly.
- **Arrange toolbar**: left/center/right + top/center/bottom alignment (2+ selected, **gap-preserving** — aligning a row never collapses it onto one point), horizontal/vertical even distribution (3+ selected), and array stamping (N copies × spacing, respecting per-group budgets). All undoable.
- **Per-node count format**: the thousands-separator toggle ("1,234") can now be set per node — for the node's own count and, on stat nodes, the KPS/Total panel value — independently of the global setting.

## :bug: Bug Fixes
- Editing one group's count no longer overwrites every other group's Total panel.
- Video nodes now play automatically after creation/loading; **fixed the game freezing on exit while a video was playing**.
- **Border Thickness finally works on square keys** (previously only the rounded path ever read it); procedural outline rings are no longer invisible (they sampled the outline sprite's transparent centre texel); an over-thick rounded border no longer erases the whole ring.
- Fixed an image-texture GPU leak on every layout rebuild; rain rows no longer mismatch on Depth-sorted custom slots; **Reset Counts** now also clears per-group KPS queues and panel caches (a KPS panel could stick at "0" at a steady press rate).
- Fixed-layout per-key counts editable directly (Colors tab) with Total synced by the difference; the reset button no longer leaves Total over-counted.
- Full list in [`CHANGELOG.md`](https://github.com/adofaiex/JipperKeyViewer/blob/master/CHANGELOG.md) / [`更新日志.md`](https://github.com/adofaiex/JipperKeyViewer/blob/master/更新日志.md).

# Supports Alpha latest / 支持Alpha最新 / Alpha 최신 버전 지원
