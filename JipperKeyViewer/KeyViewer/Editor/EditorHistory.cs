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

        internal bool CanUndo => position > 0;
        internal bool CanRedo => position >= 0 && position < snapshots.Count - 1;

        internal void Push(string snapshot)
        {
            if (snapshot == null) return;
            if (position >= 0 && position < snapshots.Count - 1)
                snapshots.RemoveRange(position + 1, snapshots.Count - 1 - position);
            snapshots.Add(snapshot);
            position = snapshots.Count - 1;
            if (snapshots.Count > TimelineCapacity)
            {
                snapshots.RemoveAt(0);
                position--;
            }
            // Every recorded state closes the nudge burst: a structural edit landing mid-burst
            // must start a fresh entry for the next nudge instead of riding the same window
            // (PushNudge re-stamps after calling in, so its own entry keeps the window open). /
            // 每次记录都闭合微调连发：结构编辑落在连发中途时，下一次微调必须开新条目，而不是
            // 搭同一窗口的车（PushNudge 在调入后重新打戳，故它自己那条仍保持窗口打开）。
            EndNudge();
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
            snapshots[position] = snapshot;
            return true;
        }

        /// <summary>Step back one entry; `current` becomes the document state at the cursor so a
        /// later redo returns to it. Returns the state to restore, or null at the timeline's
        /// start. / 回退一格；`current` 记为游标处的文档状态供之后重做返回。返回要恢复的
        /// 状态，到达时间线起点时返回 null。</summary>
        internal string Undo(string current)
        {
            if (!CanUndo) return null;
            snapshots[position] = current ?? snapshots[position];
            position--;
            return snapshots[position];
        }

        /// <summary>Step forward one entry; mirrors Undo. / 前进一格，与 Undo 对称。</summary>
        internal string Redo(string current)
        {
            if (!CanRedo) return null;
            snapshots[position] = current ?? snapshots[position];
            position++;
            return snapshots[position];
        }

        internal void Clear()
        {
            snapshots.Clear();
            position = -1;
            EndNudge();
        }
    }
}
