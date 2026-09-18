**Full Changelog**: https://github.com/2228293026/JipperKeyViewer/compare/1.7.0...1.7.1

# :tada: JipperKeyViewer 1.7.1

## :sparkles: New Features

### FreeMake custom-layout editor
- **Node-based custom layouts**: a new "Custom" layout style plus an in-game editor window — keys, KPS/Total panels and images freely positioned on a real-screen canvas, with snap guides, marquee selection, undo/redo, copy/paste and arrow-nudge (original Unity IMGUI implementation).
- **Built-in presets** (8K/10K/12K/14K/16K/20K/24K/108K) rebuilt as editable nodes, inheriting bindings, per-key counts and custom texts — into a new profile or appended to the current canvas.
- **Layer groups**: named visibility batches with their own KPS/Total panels and per-group node budgets (112 keys + 8 images) — several complete layouts coexist in one profile with independent statistics.
- **Per-node overrides**: every Display/Rain setting has a per-node escape hatch; multi-select edits apply across the selection.
- **Multiple KPS/Total panels**: one pair per group, counting only that group's keys.

### Replay key sync (TogetherBootstrap / TGT replays)
- Replays driven by the TogetherBootstrap bootstrapper bypass every input layer — they feed a specific key-viewer mod ("KeyViewer") through a startup Harmony patch instead. JipperKeyViewer now embeds a tiny compat shim with that exact API surface: during a replay the viewer reproduces the player's **original fingering** live, with counts / KPS / rain behaving exactly like physical presses. No extra mod-list entry; if the standalone KeyViewer mod is installed it takes precedence. Physical keys keep using Unity Input exactly as before.

### Persistence
- **Profiles persist through Newtonsoft** — the runtime JsonUtility silently dropped whole class-array layouts; legacy formats import automatically, and unparseable files are backed up as `*.corrupt` instead of being overwritten.

## :bug: Bug Fixes
- **Six-way parallel code audit, 30 fixes**, highlights:
  - Stale IMGUI text buffers committing half-typed values into the wrong setting (severe).
  - Rebind capture hijacking clicks and hotkeys; ESC now cancels, mouse buttons are skipped.
  - Corrupt profiles are no longer silently overwritten with defaults.
  - Invalid layout enums no longer brick the UI (clamped + fallback).
  - Truncated binding arrays crashed the Keys tab — every array is rebuilt from defaults.
  - Font silently switching after scene loads; font asset leak on every loader toggle.
  - V3→V4 migration wiped part of freshly migrated foot-key data.
  - Rain drops never recycled at speed ≤ 0 (steady memory growth); per-key KPS queues leaked with the feature off.
  - Slider drags wrote to disk ~120×/s — now debounced.
- Full list in [`CHANGELOG.md`](https://github.com/2228293026/JipperKeyViewer/blob/master/CHANGELOG.md) / [`更新日志.md`](https://github.com/2228293026/JipperKeyViewer/blob/master/更新日志.md).

## :rocket: Performance
- Zero-allocation KPS/count display; pre-allocated press queues; debounced GUI saves.

# Supports Alpha latest / 支持Alpha最新 / Alpha 최신 버전 지원
