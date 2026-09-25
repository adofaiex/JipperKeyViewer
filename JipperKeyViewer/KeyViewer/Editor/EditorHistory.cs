// Timeline-style undo for the FreeMake editor. One ordered list of whole-document JSON
// snapshots plus a cursor: editing appends past the cursor (dropping any redone future),
// undo/redo walk the cursor and hand the caller the state to restore. Arrow-key bursts
// coalesce: the first nudge inside a short window records, the rest ride on it.
// / FreeMake 编辑器的时间线式撤销。一个有序的整份文档 JSON 快照列表加游标：编辑在游标后
// 追加（丢弃已重做的未来），撤销/重做移动游标并把要恢复的状态交还给调用方。方向键连发
// 按短窗口合并：窗口内首次微调记录，其余搭车。

using System.Collections.Generic;

namespace JipperKeyViewer.KeyViewer.Editor
{
    internal sealed class EditorHistory
    {
        // How many snapshots the timeline keeps; older ones fall off the front. /
        // 时间线保留的快照数；更早的从头部淘汰。
        private const int TimelineCapacity = 64;
        // …and a byte ceiling, because the count alone does not bound anything real. Every entry
        // is a whole-document JSON snapshot: ~0.5 MB for a 112-node layout, so 64 entries is
        // ~32 MB held for the whole time the editor is open. A small document is nowhere near
        // that, and dropping those entries would only cost the user undo depth for nothing. Trim
        // by whichever limit bites first, and never below the current position.
        // …以及一个字节上限，因为单看条数根本没有约束任何真实成本。每条都是整份文档的 JSON 快照：
        // 112 节点的布局约 0.5 MB，故 64 条 = 编辑器开着期间常驻约 32 MB。小文档远达不到，而淘汰
        // 那些条目只会白白让用户损失撤销深度。取两个上限中先咬紧的那个，且永不越过当前位置。
        private const long MaxTimelineBytes = 16L * 1024L * 1024L;
        // Nudge bursts within this window collapse into one timeline entry. /
        // 该窗口内的微调连发合并为一条时间线记录。
        private const float NudgeMergeWindow = 0.4f;

        // snapshots[0..position] are remembered states, oldest first; `position` is the index
        // the document is currently at (the newest snapshot). Edits truncate everything after
        // `position` before appending, so redo history dies on any new edit — the standard
        // linear-timeline behavior. / snapshots[0..position] 是记住的状态（旧在前）；
        // `position` 是文档当前所在条目（最新快照）。编辑先截断 position 之后的内容再追加，
        // 任何新编辑都会作废重做历史——线性时间线的标准行为。
        private readonly List<string> snapshots = new List<string>();
        private int position = -1;
        private float lastNudgeStamp = float.NegativeInfinity;
        /// <summary>Running total of the bytes held by `snapshots`, so the trim is O(1) instead of
        /// re-summing the whole list on every push. Maintained by Push/ReplaceTop/Clear/Undo/Redo.
        /// `snapshots` 持有字节数的running合计，使裁剪为 O(1) 而不必每次 Push 都重新求和。
        /// 由 Push/ReplaceTop/Clear/Undo/Redo 维护。
        /// </summary>
        private long snapshotBytes;

        internal bool CanUndo => position > 0;
        internal bool CanRedo => position >= 0 && position < snapshots.Count - 1;

        internal void Push(string snapshot)
        {
            if (snapshot == null) return;
            if (position >= 0 && position < snapshots.Count - 1)
                DropRange(position + 1, snapshots.Count - 1 - position);
            snapshots.Add(snapshot);
            snapshotBytes += BytesOf(snapshot);
            position = snapshots.Count - 1;
            TrimFront();
            // Every recorded state closes the nudge burst: a structural edit landing mid-burst
            // must start a fresh entry for the next nudge instead of riding the same window
            // (PushNudge re-stamps after calling in, so its own entry keeps the window open). /
            // 每次记录都闭合微调连发：结构编辑落在连发中途时，下一次微调必须开新条目，而不是
            // 搭同一窗口的车（PushNudge 在调入后重新打戳，故它自己那条仍保持窗口打开）。
            EndNudge();
        }

        private static long BytesOf(string s) => s == null ? 0L : (long)s.Length * 2L;

        private void DropRange(int index, int count)
        {
            for (int i = index; i < index + count && i < snapshots.Count; i++) snapshotBytes -= BytesOf(snapshots[i]);
            if (snapshotBytes < 0) snapshotBytes = 0;
            snapshots.RemoveRange(index, count);
        }

        /// <summary>Drop whole-document snapshots off the front until BOTH limits are satisfied,
        /// never touching the current position (and never emptying the timeline, so the first
        /// structural edit stays undoable — see the baseline seeding in OpenFreeMakeEditor).
        /// 从头部丢弃整份文档快照，直到**两个**上限都满足；绝不越过当前位置，也绝不把时间线清空
        /// （否则第一次结构编辑就不可撤销——见 OpenFreeMakeEditor 里的基线播种）。
        /// </summary>
        private void TrimFront()
        {
            while (snapshots.Count > 1
                   && (snapshots.Count > TimelineCapacity || snapshotBytes > MaxTimelineBytes))
            {
                snapshotBytes -= BytesOf(snapshots[0]);
                snapshots.RemoveAt(0);
                if (position > 0) position--;
            }
            if (snapshotBytes < 0) snapshotBytes = 0;
        }

        /// <summary>Nudge variant: only the first nudge inside the merge window records. /
        /// 微调变体：合并窗口内只有第一次微调记录。</summary>
        internal void PushNudge(string snapshot, float now)
        {
            if (now - lastNudgeStamp <= NudgeMergeWindow) return;
            // Record first, THEN open the window: Push closes any burst (EndNudge), so stamping
            // before the call would be wiped and every nudge would record its own entry. /
            // 先记录再开窗：Push 会闭合连发（EndNudge），若先打戳会被清掉，导致每次微调各记一条。
            Push(snapshot);
            lastNudgeStamp = now;
        }

        /// <summary>Close a nudge burst so the next one starts a fresh entry. /
        /// 结束一次微调连发，下一次连发开新记录。</summary>
        internal void EndNudge() => lastNudgeStamp = float.NegativeInfinity;

        /// <summary>Overwrite the newest entry (at the cursor) with the finished document state.
        /// Drag and resize gestures must record once per gesture, but they can only know that the
        /// entry is final when the gesture ends — and the entry was pushed on the first moved frame.
        /// Without this, any later undo of a different edit returned the node to its mid-drag
        /// geometry. Returns false when there is no entry to replace. / 用完成的文档状态覆盖游标处
        /// （最新）的条目。拖拽与缩放手势每次手势只能记一条，但只有手势结束才知道这条是最终态，
        /// 而条目是在第一个移动帧压入的；不覆盖的话，之后撤销别的编辑会把节点退回拖拽中途的几何。
        /// 没有可替换的条目时返回 false。</summary>
        internal bool ReplaceTop(string snapshot)
        {
            if (snapshot == null || position < 0 || position >= snapshots.Count) return false;
            snapshotBytes -= BytesOf(snapshots[position]);
            snapshots[position] = snapshot;
            snapshotBytes += BytesOf(snapshot);
            return true;
        }

        /// <summary>Step back one entry; `current` becomes the document state at the cursor so a
        /// later redo returns to it. Returns the state to restore, or null at the timeline's
        /// start. / 回退一格；`current` 记为游标处的文档状态供之后重做返回。返回要恢复的
        /// 状态，到达时间线起点时返回 null。</summary>
        internal string Undo(string current)
        {
            if (!CanUndo) return null;
            if (current != null)
            {
                snapshotBytes -= BytesOf(snapshots[position]);
                snapshots[position] = current;
                snapshotBytes += BytesOf(current);
            }
            position--;
            return snapshots[position];
        }

        /// <summary>Step forward one entry; mirrors Undo. / 前进一格，与 Undo 对称。</summary>
        internal string Redo(string current)
        {
            if (!CanRedo) return null;
            if (current != null)
            {
                snapshotBytes -= BytesOf(snapshots[position]);
                snapshots[position] = current;
                snapshotBytes += BytesOf(current);
            }
            position++;
            return snapshots[position];
        }

        internal void Clear()
        {
            snapshots.Clear();
            position = -1;
            snapshotBytes = 0;
            EndNudge();
        }
    }
}
