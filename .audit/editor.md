# Editor audit — bugs introduced between 1.7.2 and HEAD

Files: `JipperKeyViewer/KeyViewer/Editor/KeyViewerEditor.cs`, `JipperKeyViewer/KeyViewer/Editor/EditorHistory.cs`

---

## "Reset counts" pre-state snapshot is captured with the counts stripped, so the reset is not undoable
- file: JipperKeyViewer/KeyViewer/Editor/KeyViewerEditor.cs
- line: 4154 (and 4180)
- severity: high
- what: The "reset count" button pushes a PRE-reset entry with `preserveCounts: true`, which is exactly the flag that makes `SnapshotEditorDocument` zero every `FmNode.Count` and set `TotalCount = 0` in the captured document. The entry that is supposed to hold the real counts therefore holds zeros, and the whole point of pushing it before the reset is defeated. The comment above it ("Record the PRE-reset state FIRST, while the counts are still real … one undo brings the counts back") describes the opposite of what the argument does.
- path: Select a counting node → click "reset count" → line 4154 `PushEditorHistory(false, true)` snapshots the document with `Count` stripped and `PreserveCounts = true` → counts zeroed (4161) → line 4180 `PushEditorHistory(false, false)` snapshots the zeroed state. Now Ctrl+Z: `EditorUndo` (1298) → `EditorHistory.Undo` steps back onto the PRE-reset entry → `RestoreEditorSnapshot` (1322) sees `doc.PreserveCounts == true` at line 1334, so it re-applies counts from `liveCounts`, which was captured (1332-1336) from the just-zeroed LIVE document → every restored node gets `Count = 0` and `Settings.Data.TotalCount = liveTotal` (0). `EditorMutated()` (1392) then `SaveSettings()`s that zeroed table. A second Ctrl+Z lands on the previous ordinary entry, which is also `PreserveCounts = true`, so it behaves identically. The user's whole count table is destroyed and persisted with no way back.
- fix: Change line 4154 to `PushEditorHistory(false, false);` so the pre-reset entry actually carries the live counters; `RestoreEditorSnapshot`'s existing `else { Settings.Data.TotalCount = doc.TotalCount; }` arm (1371) then restores them.

---

## Float field re-applies on every Layout/Repaint when the typed text spells the same value differently
- file: JipperKeyViewer/KeyViewer/Editor/KeyViewerEditor.cs
- line: 4646
- severity: high
- what: `DrawEditorFloatField` switched its commit guard from a numeric comparison to a string comparison, `stripped != seed`. The seed is the "R" round-trip form of the node's value, so any text the user types that parses to the value already stored but is spelled differently never equals the seed and the apply callback runs again on every IMGUI event, forever, while that field keeps focus. Each run goes through `EditorPropertyChanged()` → `RequestEditorRebuild()` → `ResetKeyViewer()`, which destroys and recreates every key GameObject, re-inits both shape layers and clears all rain drops — the exact regression the "R" formatting was introduced to eliminate.
- path: Click into the X field (or Width, RainWidth, …) whose value is `100`, select all, type `1.0` (or `1e3`, `+5`, `2.`). On the next Repaint `TextInputField` returns the focused buffer `"1.0"`, `seed` is `"100"`, `"1.0" != "100"` → `apply(1.0)` runs; the node value becomes 1, so on the FOLLOWING pass `seed` is `"1"`, the buffer is still `"1.0"`, `"1.0" != "1"` → `apply(1.0)` again — and this repeats on every Layout and Repaint indefinitely (≈2-3 full `ResetKeyViewer()` per frame) until the field loses focus. The old guard `Math.Abs(parsed - v0) > 0.001f` returned false in exactly this situation.
- fix: With "R" the seed now round-trips exactly, so the numeric comparison is safe again: replace the condition with `if (float.TryParse(stripped, out float parsed) && IsFiniteFloat(parsed) && (mixed || Math.Abs(parsed - v0) > 0.001f)) apply(parsed);`.

---

## `fm_unselectable` toggle neither pushes history nor saves; `EditorOnlyChanged` is dead code
- file: JipperKeyViewer/KeyViewer/Editor/KeyViewerEditor.cs
- line: 3624-3627 (helper at 1071-1075, dispatch at 4567-4580)
- severity: medium
- what: `DrawEditorToggle`'s new `after` parameter short-circuits the default `EditorPropertyChanged()`, and the `fm_unselectable` call site passes `EditorNothing` — a literal no-op — so that toggle now performs no history push, no save and no rebuild at all. `EditorOnlyChanged()` (push history + save, no rebuild) was added for exactly this case and is referenced nowhere in the codebase, so the intended wiring is provably missing rather than deliberately declined.
- path: Select nodes → click "unselectable" → `DrawEditorToggle` (4567) sees `newValue != value`, resets `editorInPlaceRefresh`, runs `apply` (which only writes `n.Unselectable`), then executes `after()` = `EditorNothing()` and skips the `else if (!editorInPlaceRefresh) EditorPropertyChanged()` branch (4578). Nothing is pushed and nothing is saved. Ctrl+Z then steps back onto whatever unrelated entry was previous, undoing the wrong thing; and if nothing else touches the profile before the process exits, the flag is lost even though the checkbox shows the new state.
- fix: Pass `EditorOnlyChanged` instead of `EditorNothing` at line 3627.

---

## Multi-value "—" indicator skips `editorSelection[0]` when the field's basis is the active node
- file: JipperKeyViewer/KeyViewer/Editor/KeyViewerEditor.cs
- line: 4612-4621
- severity: low
- what: `DrawEditorFloatField` gained a `basis` parameter, and the X/Y rows pass `first` (the active node) as the basis so the display matches the delta reference. But the mixed-value scan still starts at index 1, so when the active node is not `editorSelection[0]`, index 0 is never compared against the basis and the field shows the active node's value as if it were common to the whole selection. Before the change the basis was always `editorSelection[0]`, so index 0 was implicitly covered and the scan was complete.
- path: Ctrl-click a node late in the list so it becomes `fmActiveNode` while `editorSelection[0]` is a different node with a different X, then Ctrl+A (selection rebuilt in document order, `fmActiveNode` untouched) → `v0 = get(first)` (4612) → the loop at 4614 starts at `editorSelection[1]`, so `editorSelection[0]` is never checked → `mixed` stays false and no "—" is shown → typing a value applies `v - first.X` to every selected node with no warning, which is the exact mass-apply the "—" marker exists to prevent.
- fix: When `basis` is supplied, scan from index 0: `int start = (basis != null) ? 0 : 1;` and loop `for (int i = start; i < editorSelection.Count; i++)`.

---

Checked and found clean: `EditorHistory` (byte accounting in `Push`/`ReplaceTop`/`Undo`/`Redo`/`DropRange`/`TrimFront` stays consistent; `PushNudge` records before stamping so `Push`'s `EndNudge` cannot swallow the window), the `EditorSelectionList` list/set pairing, the minimap and window-resize lost-`MouseUp` guards, `EditorGcBuffers` scoping, `EmitAlignLine`/`EditorSnapDrag` shared scratch reuse, the `BuildPresetStripNames` index alignment, and the removal of `KvVideoTextureManager.Release` from `EditorPropertyChanged` (the `GetOrCreate` reuse test covers path/loop/bucketed size, and a now-unplayable path leaves the entry un-stamped so `EndBuild` retires it).
