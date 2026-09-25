// FreeMake custom layout runtime / FreeMake 自定义布局运行时
// Builds the overlay from ProfileData.CustomNodes instead of a fixed LayoutDesc. Every node
// carrying a binding (key nodes, KPS/Total panels, AND image nodes with a KeyBind) gets a
// runtime Key slot and participates in input / counting / rain; image nodes without a binding
// are pure decoration. Per-node state (count / colors / bindings / rain row) lives ON the
// FmNode, so list reordering from deletes can never scramble counts or colors across nodes.
// Rain: each node maps onto one of the three global parameter rows (speed / height / width /
// start-Y / shadow / outline) via RainRow, so every existing rain slider applies to custom
// nodes; the rain column anchors to the node's own rect.
// 以 ProfileData.CustomNodes 构建覆盖层而非固定 LayoutDesc。所有携带绑定的节点（按键、
// KPS/Total 面板、以及带 KeyBind 的图片节点）都获得运行时 Key 槽位并参与输入/计数/雨滴；
// 未绑定按键的图片节点为纯装饰。节点状态（计数/配色/绑定/雨滴排）内聚在 FmNode 上，删除
// 导致的列表序号位移绝不会让计数或配色串位。雨滴：每个节点经 RainRow 映射到全局三排参数
//（速度/高度/宽度/起始Y/阴影/描边），全部现有雨滴滑块对自定义节点生效；雨滴列锚定在节点
// 自身矩形上。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using JipperKeyViewer.KeyViewer.Rendering;
using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer
    {
        /// <summary>Ghost-key press states for Custom nodes, keyed by node Id / 自定义节点鬼键按下态（按节点 Id）</summary>
        private readonly Dictionary<int, bool> customGhostStates = new Dictionary<int, bool>();
        private readonly Dictionary<FmNode, RectTransform> customImageRects = new Dictionary<FmNode, RectTransform>();
        private readonly Dictionary<FmNode, RawImage> customImageRaws = new Dictionary<FmNode, RawImage>();
        private readonly Dictionary<FmNode, Image> customGlowImages = new Dictionary<FmNode, Image>();
        private Sprite customGlowSprite;
        private readonly HashSet<int> customVideoFallbackApplied = new HashSet<int>();

        /// <summary>Clear ghost-key edge state when the active document identity changes.
        /// Do not clear this on every overlay rebuild: holding a ghost key across a normal
        /// rebuild must not create a second synthetic press. / 文档身份变化时清理鬼键边沿状态。
        /// 普通覆盖层重建不能清理，否则按住鬼键跨重建会重复触发一次。 </summary>
        private void ResetCustomGhostStates() => customGhostStates.Clear();

        /// <summary>Shape-slot cursor for stat panels during a custom build — each panel gets
        /// its OWN slot (KeyIndex(-1/-2) collapsed them all onto one). /
        /// 自定义构建期间面板形状槽位游标——每块面板独占一槽（KeyIndex(-1/-2) 曾全部压成一槽）。</summary>
        private int customStatSlotCursor;

        // Per-GROUP press timestamps for grouped stat panels: a panel in group G shows the KPS
        // of G's keys only. Ungrouped panels keep the global PressTimes semantics. /
        // 按组的按压时间戳：G 组的面板只显示 G 组按键的 KPS。未分组面板保持全局语义。
        private readonly Dictionary<string, Queue<long>> customGroupPresses = new Dictionary<string, Queue<long>>();

        /// <summary>Append a press stamp to a group's queue. Ungrouped ("") is a no-op: the
        /// ungrouped panels read the already-drained GLOBAL PressTimes, so the "" bucket was
        /// enqueued into and never read — pure dead weight. /
        /// 向组队列追加按压时间戳。未分组（""）为空操作：未分组面板读取的是已排过的全局
        /// PressTimes，""桶入队后永不被读——纯死重。</summary>
        internal void EnqueueCustomGroupPress(string groupId, long timeMs)
        {
            string g = groupId ?? "";
            if (g.Length == 0) return;
            if (!customGroupPresses.TryGetValue(g, out Queue<long> q))
                customGroupPresses[g] = q = new Queue<long>(64);
            q.Enqueue(timeMs);
        }

        /// <summary>Group KPS right now: drains stale stamps, then counts. Ungrouped ("")
        /// mirrors the already-drained global PressTimes. / 组的当前 KPS：排掉过期戳后计数。
        /// 未分组（""）沿用调用方已排过的全局 PressTimes。</summary>
        private int CustomGroupKps(string groupId, long nowMs)
        {
            string g = groupId ?? "";
            if (g.Length == 0) return PressTimes != null ? PressTimes.Count : 0;
            if (!customGroupPresses.TryGetValue(g, out Queue<long> q)) return 0;
            while (q.Count > 0 && nowMs - q.Peek() > 1000) q.Dequeue();
            return q.Count;
        }

        /// <summary>Group total: sum of its counting keys' node counts. Ungrouped ("") is the
        /// global accumulated TotalCount. / 组总数：该组计入按键的节点计数之和。未分组（""）
        /// 为全局累计 TotalCount。</summary>
        private long CustomGroupTotal(string groupId)
        {
            string g = groupId ?? "";
            if (g.Length == 0) return Settings.Data.TotalCount;
            // Incremental, not a scan. This is called once per Total panel PER FRAME, and the scan
            // it replaced was a full CustomNodes walk with a string comparison per node — so P
            // panels cost O(P × N) node visits every frame. The two places a count changes are
            // ApplyCustomKeyEdge (one press, incremental below) and RecalculateCustomTotalCount
            // (a structural/membership change, which invalidates and rebuilds). First use after a
            // rebuild builds lazily, so the table is always derived from the live document.
            // 增量维护而非扫描。该方法每帧被每块 Total 面板各调一次，而它取代的扫描是带每节点一次
            // 字符串比较的完整 CustomNodes 遍历——P 块面板即每帧 O(P × N) 次节点访问。计数变化的
            // 只有两处：ApplyCustomKeyEdge（一次按压，下方增量）与 RecalculateCustomTotalCount
            // （结构/成员变化，令缓存失效并重建）。重建后的首次使用走惰性构建，故该表始终由
            // 实时文档推导而来。
            if (!groupTotalsValid) RebuildGroupTotals();
            groupTotals.TryGetValue(g, out long cached);
            return cached;
        }

        private readonly Dictionary<string, long> groupTotals = new Dictionary<string, long>(StringComparer.Ordinal);
        private bool groupTotalsValid;

        private void BumpGroupTotal(string groupId, long delta)
        {
            if (!groupTotalsValid) return; // a rebuild is pending; it recomputes from the document
            string g = groupId ?? "";
            long current = groupTotals.TryGetValue(g, out long v) ? v : 0L;
            current += delta;
            if (current > int.MaxValue) current = int.MaxValue;
            groupTotals[g] = current;
        }

        /// <summary>Rebuild the incremental per-group totals from the node document. /
        /// 从节点文档重建增量式每组总数。</summary>
        private void RebuildGroupTotals()
        {
            groupTotals.Clear();
            List<FmNode> nodes = Settings.Data.CustomNodes;
            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    FmNode n = nodes[i];
                    if (n == null || !n.CountInTotal || (n.NodeType != 0 && n.NodeType != 3)) continue;
                    string g = n.GroupId ?? "";
                    long current = groupTotals.TryGetValue(g, out long v) ? v : 0L;
                    current += Math.Max(0, n.Count);
                    if (current > int.MaxValue) current = int.MaxValue;
                    groupTotals[g] = current;
                }
            }
            groupTotalsValid = true;
        }

        /// <summary>Recompute the global Custom Total from the node document. CountInTotal is a
        /// membership switch, so toggling it must reconcile existing node counts rather than
        /// leaving the old accumulator value behind. / 从节点文档重算 Custom 全局 Total。
        /// CountInTotal 是成员开关，切换时必须重算已有节点计数，不能留下旧累计值。 </summary>
        private void RecalculateCustomTotalCount()
        {
            long total = 0;
            foreach (FmNode n in Settings.Data.CustomNodes)
            {
                if (n == null || !n.CountInTotal || (n.NodeType != 0 && n.NodeType != 3)) continue;
                total += Math.Max(0, n.Count);
                if (total >= int.MaxValue) { total = int.MaxValue; break; }
            }
            Settings.Data.TotalCount = (int)total;
            // The per-group incremental totals derive from the same document walk, so rebuild them
            // here rather than letting them drift. Marking the cache invalid FIRST means a press
            // racing this rebuild takes the lazy path instead of adding to a stale table. /
            // 每组增量总数来自同一次文档遍历，故在此一并重建，避免各自漂移。先把缓存标记为无效，
            // 使与本次重建竞态的按压走惰性路径，而不是往陈旧表里累加。
            groupTotalsValid = false;
            RebuildGroupTotals();
        }

        /// <summary>Write one custom stat panel with ITS OWN group's value. A panel in group G
        /// shows G's keys only; ungrouped panels mirror the global accumulator. / 把单个自定义
        /// 统计面板刷新为自己所属组的值。G 组的面板只显示 G 组按键，未分组面板镜像全局累计值。</summary>
        private void RefreshCustomStatKey(Key key)
        {
            if (key == null || key.CustomNode == null) return;
            string g = key.CustomNode.GroupId ?? "";
            if (key.CustomNode.NodeType == 1)
            {
                long now = Stopwatch != null ? Stopwatch.ElapsedMilliseconds : 0L;
                int kps = CustomGroupKps(g, now);
                key.LastShownStatKps = kps;
                SetKpsTotalDisplay(key, "KPS", FormatStatNumber(kps, key.CustomNode));
            }
            else if (key.CustomNode.NodeType == 2)
            {
                long total = CustomGroupTotal(g);
                key.LastShownTotal = total;
                SetKpsTotalDisplay(key, "Total", FormatStatNumber(total, key.CustomNode));
            }
        }

        /// <summary>Resolve the thousands-separator flag for one node: the node's override when
        /// it opted in, otherwise the global setting. Fixed-layout callers never come through
        /// here — they pass a null node and take the global. / 解析某节点的千分位开关：节点
        /// 已接管时用节点值，否则用全局设置。固定布局调用方不经过此处——传 null 节点即取全局。</summary>
        internal static bool NodeThousands(FmNode node)
        {
            return node != null && node.UseCustomCountFormat
                ? node.CountThousandsSeparator
                : Settings.Data.EnableCountFormatting;
        }

        /// <summary>Refresh every custom KPS/Total panel from its own group. / 按各自所属组
        /// 刷新全部自定义 KPS/Total 面板。</summary>
        private void RefreshCustomStatDisplays()
        {
            foreach (Key k in StatKeys(1)) RefreshCustomStatKey(k);
            foreach (Key k in StatKeys(2)) RefreshCustomStatKey(k);
            lastKps = PressTimes != null ? PressTimes.Count : 0;
            lastTotal = Settings.Data.TotalCount;
        }

        /// <summary>Keys with a live counter bounce animation / 正在进行计数器弹跳动画的按键</summary>
        private readonly List<Key> counterBounces = new List<Key>();

        /// <summary>Tick counter bounce animations: the value text scales up around its center
        /// with a cubic-bezier ease. / 计数器弹跳动画推进：数值文本绕中心以三次
        /// 贝塞尔缓动放大。</summary>
        private void TickCounterBounces()
        {
            for (int i = counterBounces.Count - 1; i >= 0; i--)
            {
                Key key = counterBounces[i];
                FmNode node = key != null ? key.CustomNode : null;
                if (key == null || node == null)
                {
                    if (i < counterBounces.Count) counterBounces.RemoveAt(i);
                    continue;
                }
                // Animate whichever text is VISIBLE: with HideCount the counter is the label slot.
                // / 动画当前可见的那个文本：HideCount 时计数位就是标签。
                TextMeshProUGUI target = node.HideCount || key.value == null ? key.text : key.value;
                if (target == null)
                {
                    if (i < counterBounces.Count) counterBounces.RemoveAt(i);
                    continue;
                }
                float t = Mathf.Clamp01((Time.unscaledTime - key.BounceStart) * 1000f / Mathf.Max(1f, node.CounterAnimDurationMs));
                float eased = CubicBezierEase(node.CounterAnimBezier, t);
                float scale = 1f + (node.CounterAnimScale - 1f) * (1f - eased);
                // The bounce must ride ON TOP of the node's own text transform. Writing the captured
                // press-time position back at the end (the old code) left the label permanently at
                // the pressed offset whenever the key was released mid-bounce, and scaling to 1.0
                // threw away LabelScale/CountScale. / 弹跳必须叠加在节点自身文字变换之上：旧实现在
                // 收尾时写回按下时的位置，按住中途松开会让标签永久停在按下偏移；缩放归 1 也会丢掉
                // LabelScale/CountScale。
                bool isCount = key.value != null && target == key.value;
                bool pressed = key.isPressed;
                float baseScale = isCount
                    ? (pressed && node.UsePressedCountScale ? node.PressedCountScale : node.CountScale)
                    : (pressed && node.UsePressedLabelScale ? node.PressedLabelScale : node.LabelScale);
                Vector2 pressedOffset = Vector2.zero;
                if (pressed)
                {
                    if (isCount && node.UsePressedCountOffset)
                        pressedOffset = new Vector2(node.PressedCountOffsetX, node.PressedCountOffsetY);
                    else if (!isCount && node.UsePressedLabelOffset)
                        pressedOffset = new Vector2(node.PressedLabelOffsetX, node.PressedLabelOffsetY);
                }
                Vector2 restOffset = isCount
                    ? new Vector2(node.CountOffsetX, node.CountOffsetY)
                    : new Vector2(node.LabelOffsetX, node.LabelOffsetY);
                Vector2 basePos = (isCount ? key.customValueBasePos : key.customTextBasePos) + restOffset + pressedOffset;
                RectTransform rt = target.rectTransform;
                float finalScale = baseScale * scale;
                rt.localScale = new Vector3(finalScale, finalScale, 1f);
                Vector2 centerOffset = (new Vector2(0.5f, 0.5f) - rt.pivot) * rt.rect.size * (scale - 1f);
                rt.anchoredPosition = basePos + centerOffset;
                if (t >= 1f)
                {
                    key.Bouncing = false;
                    if (i < counterBounces.Count) counterBounces.RemoveAt(i);
                    // Hand the rect back to the single owner of the press transform so the resting
                    // scale/rotation/offset can never drift out of sync with the node settings.
                    // 把 rect 交还给按下变换的唯一管理者，避免静息缩放/旋转/偏移与节点设置漂移。
                    ApplyCustomPressedTextTransform(key, node, key.isPressed);
                }
            }
        }

        private static float CubicBezierEase(float[] b, float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float s = t;
            for (int i = 0; i < 6; i++)
            {
                float x = Bez(b[0], b[2], s) - t;
                if (Mathf.Abs(x) < 0.0005f) break;
                float dx = BezDeriv(b[0], b[2], s);
                if (Mathf.Abs(dx) < 0.0001f) break;
                s = Mathf.Clamp01(s - x / dx);
            }
            return Bez(b[1], b[3], s);
        }

        private static float Bez(float p1, float p2, float s)
        {
            float inv = 1f - s;
            return 3f * inv * inv * s * p1 + 3f * inv * s * s * p2 + s * s * s;
        }

        private static float BezDeriv(float p1, float p2, float s)
        {
            float inv = 1f - s;
            return 3f * inv * inv * p1 + 6f * inv * s * (p2 - p1) + 3f * s * s * (1f - p2);
        }

        // 108 keys (the full-keyboard preset) + KPS/Total panels + a little headroom. The old
        // MaxKeySlots(40) tie couldn't hold a 108K preset. / 108 键（全键盘预设）+ KPS/Total
        // 面板 + 少量余量。此前与 MaxKeySlots(40) 绑定的上限装不下 108K 预设。
        internal static int CustomKeyNodeCap => 112;

        /// <summary>Shape-layer slot budget for stat panels: custom layouts carry one slot PER
        /// VISIBLE panel (several may exist — one per layer group); fixed layouts have exactly
        /// the classic two. / 面板占用的形状层槽位数：自定义布局每个可见面板一槽（可存在
        /// 多块——每个图层组各一）；固定布局恒为经典的两块。</summary>
        private static int CustomStatSlotCount()
        {
            if (!IsCustomLayout) return 2;
            int count = 0;
            foreach (FmNode n in Settings.Data.CustomNodes)
                if (n != null && (n.NodeType == 1 || n.NodeType == 2) && CustomNodeVisible(n)) count++;
            return count;
        }

        /// <summary>Whether a node takes a runtime Key slot: everything except an unbound image. /
        /// 节点是否占用运行时 Key 槽位：除未绑定按键的图片节点外全部占用。</summary>
        private static bool CustomNodeHasKey(FmNode node)
        {
            return node != null && (node.NodeType != 3 || !string.IsNullOrWhiteSpace(node.KeyBind));
        }

        /// <summary>Rain row (0/1/2) → the RawRain color byte (0 = row 1, 1 = row 2, 3 = row 3). /
        /// 雨滴排（0/1/2）→ RawRain 颜色字节（0=第1排，1=第2排，3=第3排）。</summary>
        private static byte CustomRainRowByte(FmNode node)
        {
            int row = Mathf.Clamp(node.RainRow, 0, 2);
            return row == 0 ? (byte)0 : row == 2 ? (byte)3 : (byte)1;
        }

        /// <summary>Drop a malformed node colour array (wrong length or NaN/Inf component) so the
        /// callers fall back to their global default instead of writing bad vertex colours.
        /// 丢弃格式错误的节点颜色数组（长度不对或含 NaN/Inf），让调用方回退全局默认色。</summary>
        private static float[] SanitizeNodeColor(float[] color)
        {
            if (color == null) return null;
            if (color.Length != 4) return null;
            for (int i = 0; i < 4; i++)
                if (float.IsNaN(color[i]) || float.IsInfinity(color[i])) return null;
            return color;
        }

        /// <summary>Ensure the custom node list: drop nulls and hidden inconsistencies,
        /// assign missing ids, enforce caps and sane bounds. / 校验并钳制节点列表：去空、补 id、
        /// 强制上限与合理边界。</summary>
        /// <summary>Reused across EnsureCustomNodes calls so the id-uniqueness pass allocates
        /// nothing. / 跨 EnsureCustomNodes 调用复用，使 id 唯一性检查零分配。</summary>
        private static readonly HashSet<int> seenNodeIds = new HashSet<int>();

        private static void EnsureCustomNodes()
        {
            List<FmNode> nodes = Settings.Data.CustomNodes;
            if (nodes == null)
            {
                Settings.Data.CustomNodes = nodes = new List<FmNode>();
            }
            seenNodeIds.Clear();
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                FmNode node = nodes[i];
                if (node == null)
                {
                    nodes.RemoveAt(i);
                    continue;
                }
                if (node.Id <= 0) node.Id = Settings.Data.CustomNodeNextId++;
                // A hand-edited profile — or a merge of two documents — can carry the same Id twice.
                // Ids are the identity used by the editor's selection remapping, by undo's live-count
                // re-application, and by the capture state, so a duplicate silently made one of the
                // two nodes unselectable-by-id and credited the other's press count to it. The
                // original stayed authoritative; the later one is re-stamped.
                // 手改的配置——或两份文档合并——可能带重复 Id。而 Id 是编辑器选区重映射、撤销时
                // 实时计数回填与捕获状态共同使用的身份键，重复会让其中一个无法按 id 选中、并拿到
                // 另一个的按压计数。保留首个，后到的重新打戳。
                if (!seenNodeIds.Add(node.Id)) node.Id = Settings.Data.CustomNodeNextId++;
                if (node.Id >= Settings.Data.CustomNodeNextId) Settings.Data.CustomNodeNextId = node.Id + 1;
                // A legacy profile can reach here without SyncArraysFromLists (e.g. a package
                // export/import path); restore non-zero field defaults before clamping would
                // otherwise turn LabelScale=0 into 0.5 and leave TextOpacity=0 invisible.
                ProfileData.ApplyLegacyFmNodeDefaults(node);
                // Hand-edited profiles may carry an unknown node type — treat as a key node. /
                // 手改配置可能带未知节点类型——按按键节点处理。
                if (node.NodeType is not (0 or 1 or 2 or 3)) node.NodeType = 0;
                // Mathf.Clamp passes NaN through (both comparisons are false) — sanitize first or
                // a hand-edited NaN reaches the geometry. / Mathf.Clamp 对 NaN 原样穿透（两个
                // 比较都为假）——先净化，否则手改的 NaN 会进入几何渲染。
                node.X = float.IsNaN(node.X) || float.IsInfinity(node.X) ? 0f : Mathf.Clamp(node.X, -8000f, 8000f);
                node.Y = float.IsNaN(node.Y) || float.IsInfinity(node.Y) ? 0f : Mathf.Clamp(node.Y, -8000f, 8000f);
                node.Width = float.IsNaN(node.Width) || float.IsInfinity(node.Width) ? 60f : Mathf.Clamp(node.Width, 10f, 2000f);
                node.Height = float.IsNaN(node.Height) || float.IsInfinity(node.Height) ? 60f : Mathf.Clamp(node.Height, 10f, 2000f);
                node.Opacity = float.IsNaN(node.Opacity) ? 1f : Mathf.Clamp01(node.Opacity);
                node.GlowSize = float.IsNaN(node.GlowSize) || float.IsInfinity(node.GlowSize) ? 20f : Mathf.Clamp(node.GlowSize, 0f, 50f);
                node.GlowOpacity = float.IsNaN(node.GlowOpacity) || float.IsInfinity(node.GlowOpacity) ? 0.7f : Mathf.Clamp01(node.GlowOpacity);
                node.GlowSizePressed = float.IsNaN(node.GlowSizePressed) || float.IsInfinity(node.GlowSizePressed) ? 20f : Mathf.Clamp(node.GlowSizePressed, 0f, 50f);
                node.GlowOpacityPressed = float.IsNaN(node.GlowOpacityPressed) || float.IsInfinity(node.GlowOpacityPressed) ? 0.7f : Mathf.Clamp01(node.GlowOpacityPressed);
                node.RainRow = Mathf.Clamp(node.RainRow, 0, 2);
                node.FontSize = float.IsNaN(node.FontSize) || node.FontSize < 0f ? 0f : Mathf.Min(node.FontSize, 72f);
                node.CountFontSize = float.IsNaN(node.CountFontSize) || node.CountFontSize < 0f ? 0f : Mathf.Min(node.CountFontSize, 72f);
                node.CountOffsetX = float.IsNaN(node.CountOffsetX) || float.IsInfinity(node.CountOffsetX) ? 0f : Mathf.Clamp(node.CountOffsetX, -200f, 200f);
                node.CountOffsetY = float.IsNaN(node.CountOffsetY) || float.IsInfinity(node.CountOffsetY) ? 0f : Mathf.Clamp(node.CountOffsetY, -200f, 200f);
                node.TextOpacity = float.IsNaN(node.TextOpacity) || float.IsInfinity(node.TextOpacity) ? 1f : Mathf.Clamp01(node.TextOpacity);
                node.CountTextOpacity = float.IsNaN(node.CountTextOpacity) || float.IsInfinity(node.CountTextOpacity) ? 1f : Mathf.Clamp01(node.CountTextOpacity);
                node.LabelOffsetX = float.IsNaN(node.LabelOffsetX) || float.IsInfinity(node.LabelOffsetX) ? 0f : Mathf.Clamp(node.LabelOffsetX, -200f, 200f);
                node.LabelOffsetY = float.IsNaN(node.LabelOffsetY) || float.IsInfinity(node.LabelOffsetY) ? 0f : Mathf.Clamp(node.LabelOffsetY, -200f, 200f);
                node.LabelRotation = float.IsNaN(node.LabelRotation) || float.IsInfinity(node.LabelRotation) ? 0f : Mathf.Clamp(node.LabelRotation, -180f, 180f);
                node.CountRotation = float.IsNaN(node.CountRotation) || float.IsInfinity(node.CountRotation) ? 0f : Mathf.Clamp(node.CountRotation, -180f, 180f);
                node.LabelScale = float.IsNaN(node.LabelScale) || float.IsInfinity(node.LabelScale) ? 1f : Mathf.Clamp(node.LabelScale, 0.5f, 2f);
                node.CountScale = float.IsNaN(node.CountScale) || float.IsInfinity(node.CountScale) ? 1f : Mathf.Clamp(node.CountScale, 0.5f, 2f);
                node.PressedLabelScale = float.IsNaN(node.PressedLabelScale) || float.IsInfinity(node.PressedLabelScale) ? 1f : Mathf.Clamp(node.PressedLabelScale, 0.5f, 2f);
                node.PressedCountScale = float.IsNaN(node.PressedCountScale) || float.IsInfinity(node.PressedCountScale) ? 1f : Mathf.Clamp(node.PressedCountScale, 0.5f, 2f);
                node.PressedLabelOffsetX = float.IsNaN(node.PressedLabelOffsetX) || float.IsInfinity(node.PressedLabelOffsetX) ? 0f : Mathf.Clamp(node.PressedLabelOffsetX, -200f, 200f);
                node.PressedLabelOffsetY = float.IsNaN(node.PressedLabelOffsetY) || float.IsInfinity(node.PressedLabelOffsetY) ? 0f : Mathf.Clamp(node.PressedLabelOffsetY, -200f, 200f);
                node.PressedCountOffsetX = float.IsNaN(node.PressedCountOffsetX) || float.IsInfinity(node.PressedCountOffsetX) ? 0f : Mathf.Clamp(node.PressedCountOffsetX, -200f, 200f);
                node.PressedCountOffsetY = float.IsNaN(node.PressedCountOffsetY) || float.IsInfinity(node.PressedCountOffsetY) ? 0f : Mathf.Clamp(node.PressedCountOffsetY, -200f, 200f);
                node.PressedLabelRotation = float.IsNaN(node.PressedLabelRotation) || float.IsInfinity(node.PressedLabelRotation) ? 0f : Mathf.Clamp(node.PressedLabelRotation, -180f, 180f);
                node.PressedCountRotation = float.IsNaN(node.PressedCountRotation) || float.IsInfinity(node.PressedCountRotation) ? 0f : Mathf.Clamp(node.PressedCountRotation, -180f, 180f);
                node.RainOffsetX = float.IsNaN(node.RainOffsetX) ? 0f : Mathf.Clamp(node.RainOffsetX, -2000f, 2000f);
                node.RainOffsetY = float.IsNaN(node.RainOffsetY) ? 0f : Mathf.Clamp(node.RainOffsetY, -2000f, 2000f);
                node.RainAlignment = Mathf.Clamp(node.RainAlignment, 0, 2);
                node.RainCornerRadius = float.IsNaN(node.RainCornerRadius) || float.IsInfinity(node.RainCornerRadius) ? 0f : Mathf.Clamp(node.RainCornerRadius, 0f, 20f);
                node.RainBorderSides = Mathf.Clamp(node.RainBorderSides, 0, 2);
                node.GhostRainBorderSides = Mathf.Clamp(node.GhostRainBorderSides, 0, 2);
                // Dot length keeps 0: it is the node's explicit "dotted off" state, and clamping it
                // up to 1 turned every such node into 1px dots that the user could then not undo.
                // 点长保留 0：它是节点"显式关闭点状"的取值，钳到 1 会把这类节点变成 1 像素点、
                // 且用户无法再关掉。
                node.RainDotLength = float.IsNaN(node.RainDotLength) || float.IsInfinity(node.RainDotLength) ? 12f : Mathf.Clamp(node.RainDotLength, 0f, 100f);
                node.RainGapLength = float.IsNaN(node.RainGapLength) || float.IsInfinity(node.RainGapLength) ? 8f : Mathf.Clamp(node.RainGapLength, 0f, 100f);
                node.GhostRainCornerRadius = float.IsNaN(node.GhostRainCornerRadius) || float.IsInfinity(node.GhostRainCornerRadius) ? 0f : Mathf.Clamp(node.GhostRainCornerRadius, 0f, 20f);
                node.GhostRainDotLength = float.IsNaN(node.GhostRainDotLength) || float.IsInfinity(node.GhostRainDotLength) ? 12f : Mathf.Clamp(node.GhostRainDotLength, 0f, 100f);
                node.GhostRainGapLength = float.IsNaN(node.GhostRainGapLength) || float.IsInfinity(node.GhostRainGapLength) ? 8f : Mathf.Clamp(node.GhostRainGapLength, 0f, 100f);
                node.CounterAnimScale = float.IsNaN(node.CounterAnimScale) ? 1.1f : Mathf.Clamp(node.CounterAnimScale, 1f, 2f);
                node.CounterAnimDurationMs = node.CounterAnimDurationMs <= 0f || float.IsNaN(node.CounterAnimDurationMs)
                    ? 300f
                    : Mathf.Min(node.CounterAnimDurationMs, 5000f);
                // Hand-edited profiles could carry a null/short bezier — CubicBezierEase indexes
                // [0..3] raw. / 手改配置可能带空/短贝塞尔——CubicBezierEase 直接索引 [0..3]。
                if (node.CounterAnimBezier == null || node.CounterAnimBezier.Length != 4)
                    node.CounterAnimBezier = new float[] { 0.25f, 0.46f, 0.45f, 0.94f };
                else
                    for (int b = 0; b < 4; b++)
                        if (float.IsNaN(node.CounterAnimBezier[b]) || float.IsInfinity(node.CounterAnimBezier[b]))
                            node.CounterAnimBezier[b] = b == 0 ? 0.25f : b == 1 ? 0.46f : b == 2 ? 0.45f : 0.94f;
                // The colour arrays added with the glow/gradient features were the only FmNode
                // fields WITHOUT sanitization: NodeColor accepts any 4-length array, so a NaN or a
                // hand-edited out-of-range component reached the mesh vertex colours and made the key
                // box (or the glyphs) render black/garbage. Normalize them exactly like the scalars.
                // 光效/渐变新增的颜色数组是唯一未被净化的 FmNode 字段：NodeColor 只看长度 4，
                // NaN 或超范围分量会直接写进 mesh 顶点色，使按键框/文字渲染成黑色或乱色。
                node.Bg = SanitizeNodeColor(node.Bg);
                node.BgPressed = SanitizeNodeColor(node.BgPressed);
                node.Outline = SanitizeNodeColor(node.Outline);
                node.OutlinePressed = SanitizeNodeColor(node.OutlinePressed);
                node.TextColor = SanitizeNodeColor(node.TextColor);
                node.TextColorPressed = SanitizeNodeColor(node.TextColorPressed);
                node.CountTextColor = SanitizeNodeColor(node.CountTextColor);
                node.CountTextColorPressed = SanitizeNodeColor(node.CountTextColorPressed);
                node.GlowColor = SanitizeNodeColor(node.GlowColor);
                node.GlowColorPressed = SanitizeNodeColor(node.GlowColorPressed);
                node.BackgroundGradientTop = SanitizeNodeColor(node.BackgroundGradientTop);
                node.BackgroundGradientBottom = SanitizeNodeColor(node.BackgroundGradientBottom);
                node.BackgroundGradientTopPressed = SanitizeNodeColor(node.BackgroundGradientTopPressed);
                node.BackgroundGradientBottomPressed = SanitizeNodeColor(node.BackgroundGradientBottomPressed);
                node.OutlineGradientTop = SanitizeNodeColor(node.OutlineGradientTop);
                node.OutlineGradientBottom = SanitizeNodeColor(node.OutlineGradientBottom);
                node.OutlineGradientTopPressed = SanitizeNodeColor(node.OutlineGradientTopPressed);
                node.OutlineGradientBottomPressed = SanitizeNodeColor(node.OutlineGradientBottomPressed);
                node.TextGradientLeft = SanitizeNodeColor(node.TextGradientLeft);
                node.TextGradientRight = SanitizeNodeColor(node.TextGradientRight);
                node.TextGradientLeftPressed = SanitizeNodeColor(node.TextGradientLeftPressed);
                node.TextGradientRightPressed = SanitizeNodeColor(node.TextGradientRightPressed);
                node.CountTextGradientLeft = SanitizeNodeColor(node.CountTextGradientLeft);
                node.CountTextGradientRight = SanitizeNodeColor(node.CountTextGradientRight);
                node.CountTextGradientLeftPressed = SanitizeNodeColor(node.CountTextGradientLeftPressed);
                node.CountTextGradientRightPressed = SanitizeNodeColor(node.CountTextGradientRightPressed);
                node.RainColorTop = SanitizeNodeColor(node.RainColorTop);
                node.RainColorBottom = SanitizeNodeColor(node.RainColorBottom);
                node.RainShadowColor = SanitizeNodeColor(node.RainShadowColor);
                node.RainOutlineColor = SanitizeNodeColor(node.RainOutlineColor);
                node.GhostRainShadowColor = SanitizeNodeColor(node.GhostRainShadowColor);
                node.GhostRainOutlineColor = SanitizeNodeColor(node.GhostRainOutlineColor);
                node.PressAnimScale = float.IsNaN(node.PressAnimScale) ? 0.9f : Mathf.Clamp(node.PressAnimScale, 0.3f, 2f);
                node.RainShadowOffsetX = float.IsNaN(node.RainShadowOffsetX) ? 3f : Mathf.Clamp(node.RainShadowOffsetX, -50f, 50f);
                node.RainShadowOffsetY = float.IsNaN(node.RainShadowOffsetY) ? -3f : Mathf.Clamp(node.RainShadowOffsetY, -50f, 50f);
                node.RainOutlineWidth = float.IsNaN(node.RainOutlineWidth) ? 2f : Mathf.Clamp(node.RainOutlineWidth, 0f, 50f);
                node.GhostRainShadowOffsetX = float.IsNaN(node.GhostRainShadowOffsetX) ? 3f : Mathf.Clamp(node.GhostRainShadowOffsetX, -50f, 50f);
                node.GhostRainShadowOffsetY = float.IsNaN(node.GhostRainShadowOffsetY) ? -3f : Mathf.Clamp(node.GhostRainShadowOffsetY, -50f, 50f);
                node.GhostRainOutlineWidth = float.IsNaN(node.GhostRainOutlineWidth) ? 2f : Mathf.Clamp(node.GhostRainOutlineWidth, 0f, 50f);
                node.GhostRainWidth = float.IsNaN(node.GhostRainWidth) ? 0f : Mathf.Clamp(node.GhostRainWidth, 0f, 2000f);
                node.GhostRainHeight = float.IsNaN(node.GhostRainHeight) ? 0f : Mathf.Clamp(node.GhostRainHeight, 0f, 2000f);
                node.GhostRainSpeed = float.IsNaN(node.GhostRainSpeed) ? 0f : Mathf.Clamp(node.GhostRainSpeed, 0f, 5000f);
                node.GhostRainOffsetX = float.IsNaN(node.GhostRainOffsetX) ? 0f : Mathf.Clamp(node.GhostRainOffsetX, -2000f, 2000f);
                node.GhostRainOffsetY = float.IsNaN(node.GhostRainOffsetY) ? 0f : Mathf.Clamp(node.GhostRainOffsetY, -2000f, 2000f);
                node.TrailFadePx = float.IsNaN(node.TrailFadePx) ? 50f : Mathf.Clamp(node.TrailFadePx, 0f, 500f);
                node.ReleaseFadeDuration = float.IsNaN(node.ReleaseFadeDuration) ? 0.5f : Mathf.Clamp(node.ReleaseFadeDuration, 0f, 5f);
            }
            // Per-GROUP caps: each layer group (and the ungrouped bucket) gets its own key-like
            // and unbound-image budgets — a 108K preset group no longer eats every other group's
            // allowance. Stat panels count toward their group's key-like pool. /
            // 按组上限：每个图层组（与未分组桶）各有一份按键类/未绑定图片预算——108K 预设组
            // 不再吃光其它组的额度。面板计入所在组的按键类池。
            var keyLikeByGroup = new Dictionary<string, int>();
            var imagesByGroup = new Dictionary<string, int>();
            int GroupCount(Dictionary<string, int> map, string g)
            {
                return map.TryGetValue(g, out int v) ? v : 0;
            }
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                FmNode node = nodes[i];
                string g = node.GroupId ?? "";
                if (node.NodeType == 3 && !CustomNodeHasKey(node))
                {
                    int im = GroupCount(imagesByGroup, g) + 1;
                    imagesByGroup[g] = im;
                    if (im > 8) nodes.RemoveAt(i);
                }
                else
                {
                    int k = GroupCount(keyLikeByGroup, g) + 1;
                    keyLikeByGroup[g] = k;
                    if (k > CustomKeyNodeCap) nodes.RemoveAt(i);
                }
            }
            // Layer groups: drop null/degenerate entries and ungroup dangling references. /
            // 图层组：剔除空/退化条目，悬空引用取消分组。
            List<FmLayerGroup> groups = Settings.Data.LayerGroups;
            if (groups == null) Settings.Data.LayerGroups = groups = new List<FmLayerGroup>();
            for (int i = groups.Count - 1; i >= 0; i--)
                if (groups[i] == null || string.IsNullOrEmpty(groups[i].Id)) groups.RemoveAt(i);
            foreach (FmNode node in nodes)
                if (!string.IsNullOrEmpty(node.GroupId) && !groups.Exists(g => g.Id == node.GroupId))
                    node.GroupId = "";

            // Groups with no surviving members serve no purpose — drop them, or empty groups
            // linger after deleting a whole batch. / 无存活成员的组没有意义——剔除，否则整批
            // 删除后空组残留。
            for (int i = groups.Count - 1; i >= 0; i--)
            {
                string gid = groups[i].Id;
                bool any = false;
                foreach (FmNode node in nodes)
                    if (node.GroupId == gid) { any = true; break; }
                if (!any) groups.RemoveAt(i);
            }
        }

        /// <summary>Key-like node count within ONE group ("" = ungrouped bucket). /
        /// 单个组内的按键类节点数（"" = 未分组桶）。</summary>
        private static int KeyLikeCountInGroup(string groupId)
        {
            string g = groupId ?? "";
            int c = 0;
            foreach (FmNode n in Settings.Data.CustomNodes)
            {
                if (n == null || (n.GroupId ?? "") != g) continue;
                if (n.NodeType != 3 || CustomNodeHasKey(n)) c++;
            }
            return c;
        }

        /// <summary>Unbound decorative-image count within ONE group. /
        /// 单个组内的未绑定装饰图片数。</summary>
        private static int UnboundImageCountInGroup(string groupId)
        {
            string g = groupId ?? "";
            int c = 0;
            foreach (FmNode n in Settings.Data.CustomNodes)
            {
                if (n == null || (n.GroupId ?? "") != g || n.NodeType != 3) continue;
                if (!CustomNodeHasKey(n)) c++;
            }
            return c;
        }

        private static int CustomKeyNodeCount()
        {
            int count = 0;
            foreach (FmNode node in Settings.Data.CustomNodes)
                if (CustomNodeHasKey(node) && CustomNodeVisible(node)) count++;
            return count; // per-group caps enforced in EnsureCustomNodes / 按组上限由 EnsureCustomNodes 执行
        }

        /// <summary>Runtime center Y of a node: stored top-left origin converted to the overlay's
        /// bottom-left pivot. / 节点运行时中心 Y：存储的左上原点换算为覆盖层左下轴心。</summary>
        private static float CustomNodeCenterY(FmNode node)
        {
            return 1080f - node.Y - node.Height * 0.5f;
        }

        /// <summary>All CREATED runtime keys of a stat type (custom layouts may carry several
        /// panels — one per visible layer group; hidden groups' nodes are never created). Fixed
        /// layouts return an empty list — they use the single Kps/Total refs. /
        /// 某面板类型的全部已创建运行时按键（自定义布局可携带多块——每个可见图层组各一；
        /// 隐藏组的节点不会创建）。固定布局返回空列表——它们用单一 Kps/Total 引用。</summary>
        // Shared scratch list for StatKeys: every one of its call sites foreach-es the result
        // to completion before the next call (verified: sequential, never nested), so a reused
        // buffer is safe and the per-frame "new List" allocations stop. Callers must not hold
        // the returned reference. / StatKeys 的共享暂存列表：全部调用点都在下一次调用前
        // foreach 完毕（已核实：顺序、无嵌套），复用缓冲安全，同时消灭每帧 new List 分配。
        // 调用方不得持有返回引用。
        private readonly List<Key> statKeyBuffer = new List<Key>(8);

        private List<Key> StatKeys(int type)
        {
            statKeyBuffer.Clear();
            if (Keys == null) return statKeyBuffer;
            for (int i = 0; i < Keys.Length; i++)
            {
                Key k = Keys[i];
                if (k != null && k.CustomNode != null && k.CustomNode.NodeType == type) statKeyBuffer.Add(k);
            }
            return statKeyBuffer;
        }

        /// <summary>Show/hide every stat panel (streamer mode). Custom layouts iterate all
        /// created panels; fixed layouts toggle the single refs. / 显隐全部面板（主播模式）。
        /// 自定义布局遍历全部已创建面板；固定布局切换单一引用。</summary>
        private void SetStatsVisible(bool active)
        {
            if (IsCustomLayout)
            {
                foreach (Key k in StatKeys(1)) SetKeyObjectActive(k, active);
                foreach (Key k in StatKeys(2)) SetKeyObjectActive(k, active);
            }
            else
            {
                SetKeyObjectActive(Kps, active);
                SetKeyObjectActive(Total, active);
            }
        }

        private void InitializeCustomLayout()
        {
            List<FmNode> nodes = Settings.Data.CustomNodes;
            // One video build pass: every player this pass uses is stamped, and players left over
            // from the previous build (deleted / hidden / re-pathed nodes) are released at the end.
            // / 一次视频构建：本次用到的播放器全部打标，上一次构建遗留的（节点被删/隐藏/改路径）
            // 在末尾释放。
            KvVideoTextureManager.BeginBuild();
            try
            {
            // Stat panels draw after the key slots, one shape slot each. /
            // 面板排在按键槽位之后绘制，各占一个形状槽。
            customStatSlotCursor = Keys.Length;
            customGroupPresses.Clear();
            customImageRects.Clear();
            customImageRaws.Clear();
            customGlowImages.Clear();
            customVideoFallbackApplied.Clear();
            // Normalize a legacy/drifted global Total against the node document before the first
            // panel refresh. / 首次刷新面板前，按节点文档归一化旧版或漂移的全局 Total。
            RecalculateCustomTotalCount();
            // The "already shown" caches live on Key (LastShownStatKps/LastShownTotal) and are
            // rebuilt with the overlay — keys are recreated on every rebuild, so nothing to clear.
            // 「已显示」缓存挂在 Key 上（LastShownStatKps/LastShownTotal），随覆盖层重建——
            // 每次重建按键都是新建的，无需清理。
            // Slot nodes in Depth order so the merged mesh draws lowest Depth first. /
            // 按 Depth 排序分配槽位，使合并 mesh 从低 Depth 先画。
            List<FmNode> slotNodes = nodes
                .Where(CustomNodeVisible)
                .Where(CustomNodeHasKey)
                .OrderBy(n => n.Depth)
                .Take(2048) // absolute guard vs pathological files only — per-group caps live in EnsureCustomNodes / 仅防病态文件的绝对上限——按组上限在 EnsureCustomNodes
                .ToList();
            int slot = 0;
            foreach (FmNode node in slotNodes)
            {
                Keys[slot] = CreateCustomKey(node, slot);
                slot++;
            }
            // Dedicated Kps/Total references point at their nodes' keys (created above). /
            // 专用 Kps/Total 引用指向上面创建的对应节点按键。
            Kps = slotNodes.FirstOrDefault(n => n.NodeType == 1)?.RuntimeKey;
            Total = slotNodes.FirstOrDefault(n => n.NodeType == 2)?.RuntimeKey;
            // Unbound image nodes are pure decoration. / 未绑定按键的图片节点为纯装饰。
            // The Take(2048) matches the key-slot loop above and is NOT redundant with it: a node is
            // EITHER a key slot OR a decoration, so the cap there never bounded this loop. Without
            // one, a document full of unbound image nodes created one GameObject + one texture each.
            // 此处的 Take(2048) 与上方按键槽位循环一致，且**不**冗余：一个节点要么是按键槽位、
            // 要么是装饰，上限管不到这个循环。没有它，满是未绑定图片节点的文档会为每个节点创建
            // 一个 GameObject + 一张贴图。
            int decorationCount = 0;
            foreach (FmNode node in nodes)
            {
                if (node == null || node.NodeType != 3 || CustomNodeHasKey(node) || !CustomNodeVisible(node)) continue;
                if (decorationCount++ >= 2048) break;
                CreateCustomImageObject(node);
                ApplyCustomGlow(node, false);
            }
            OrderCustomImageRects();
            }
            finally
            {
                // EndBuild retires the players this pass did not stamp. Without the finally, any
                // throw between BeginBuild and here (a malformed node, a failed texture load) left
                // the generation bumped but the sweep un-run: every player created in this pass and
                // every player from the previous one stayed alive decoding with nothing on screen.
                // EndBuild 回收本次未打标的播放器。没有 finally 时，BeginBuild 与此处之间的任何
                // 异常（节点数据异常、贴图加载失败）都会让代次已自增而回收未执行：本轮创建的与
                // 上一轮遗留的播放器全部继续解码，屏幕上却什么都没有。
                KvVideoTextureManager.EndBuild();
            }
        }

        private void ApplyCustomGlow(FmNode node, bool pressed)
        {
            if (node == null || keyGlowLayer == null) return;
            Image image;
            bool usePressedGlow = pressed && node.GlowPressedOverride;
            float rawSize = usePressedGlow ? node.GlowSizePressed : node.GlowSize;
            float rawOpacity = usePressedGlow ? node.GlowOpacityPressed : node.GlowOpacity;
            float size = float.IsNaN(rawSize) || float.IsInfinity(rawSize)
                ? 20f : Mathf.Clamp(rawSize, 0f, 50f);
            float opacity = float.IsNaN(rawOpacity) || float.IsInfinity(rawOpacity)
                ? 0.7f : Mathf.Clamp01(rawOpacity);
            bool visible = CustomNodeVisible(node);
            if (!visible || !node.UseGlow || size <= 0f)
            {
                if (customGlowImages.TryGetValue(node, out image) && image != null)
                    image.enabled = false;
                return;
            }

            if (!customGlowImages.TryGetValue(node, out image) || image == null)
            {
                GameObject glowObject = new GameObject("Glow_" + node.Id);
                glowObject.transform.SetParent(keyGlowLayer, false);
                RectTransform rect = glowObject.AddComponent<RectTransform>();
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0f, 1f);
                image = glowObject.AddComponent<Image>();
                image.sprite = GetCustomGlowSprite();
                image.type = Image.Type.Sliced;
                image.raycastTarget = false;
                customGlowImages[node] = image;
            }

            float pad = Mathf.Max(2f, size);
            RectTransform glowRect = (RectTransform)image.transform;
            Vector2 wantedPos = new Vector2(node.X - pad, -(node.Y - pad));
            Vector2 wantedSize = new Vector2(node.Width + pad * 2f, node.Height + pad * 2f);
            // This runs on EVERY press/release. Writing anchoredPosition/sizeDelta/sibling index
            // unconditionally dirties the canvas batch for a rect that almost never moved; only the
            // actual press-dependent values (colour) are expected to change.
            // 该方法每次按压/松开都会调用：无条件写位置/尺寸/兄弟序号会白白弄脏 canvas 批次，
            // 而这些值几乎不变；真正随按压变化的只有颜色。
            if (glowRect.anchoredPosition != wantedPos) glowRect.anchoredPosition = wantedPos;
            if (glowRect.sizeDelta != wantedSize) glowRect.sizeDelta = wantedSize;
            int wantedSibling = Mathf.Clamp(node.Depth, 0, 60);
            if (glowRect.GetSiblingIndex() != wantedSibling) glowRect.SetSiblingIndex(wantedSibling);

            Color body = node.NodeType == 3
                ? (node.UseCustomColor ? NodeColor(node.Outline, Settings.Data.Outline) : Settings.Data.Outline)
                : node.NodeType == 1
                    ? (node.UseCustomColor ? NodeColor(node.Bg, Settings.Data.KpsBackground) : Settings.Data.KpsBackground)
                    : node.NodeType == 2
                        ? (node.UseCustomColor ? NodeColor(node.Bg, Settings.Data.TotalBackground) : Settings.Data.TotalBackground)
                        : node.UseCustomColor
                            ? NodeColor(pressed ? node.BgPressed : node.Bg,
                                pressed ? Settings.Data.BackgroundClicked : Settings.Data.Background)
                            : (pressed ? Settings.Data.BackgroundClicked : Settings.Data.Background);
            bool followBody = usePressedGlow ? node.GlowFollowBodyPressed : node.GlowFollowBody;
            float[] glowColor = usePressedGlow ? node.GlowColorPressed : node.GlowColor;
            Color glow = followBody ? body : NodeColor(glowColor, body);
            glow.a *= opacity;
            image.enabled = true;
            image.color = glow;
        }

        private void ApplyCustomBackgroundGradient(FmNode node, bool pressed)
        {
            if (node == null || keyShapeLayer == null || node.RuntimeKey == null) return;
            int slot = node.RuntimeKey.shapeSlot;
            // slot < 0 means the node has no shape slot at all (unbound image / stat panel), so there
            // is nothing to clear — calling the setter with a negative index was a dead call.
            // slot<0 表示节点没有形状槽位（未绑定图片/面板），无需清理；此前是无效调用。
            if (slot < 0) return;
            if (!node.UseBackgroundGradient)
            {
                keyShapeLayer.SetBackgroundGradient(slot, false, Color.white, Color.white);
                return;
            }

            Color normal = node.NodeType == 1
                ? node.UseCustomColor ? NodeColor(node.Bg, Settings.Data.KpsBackground) : Settings.Data.KpsBackground
                : node.NodeType == 2
                    ? node.UseCustomColor ? NodeColor(node.Bg, Settings.Data.TotalBackground) : Settings.Data.TotalBackground
                    : node.UseCustomColor ? NodeColor(node.Bg, Settings.Data.Background) : Settings.Data.Background;
            Color active = node.NodeType == 1 || node.NodeType == 2
                ? normal
                : node.UseCustomColor
                    ? NodeColor(node.BgPressed, Settings.Data.BackgroundClicked)
                    : Settings.Data.BackgroundClicked;
            bool usePressed = pressed && node.UsePressedBackgroundGradient;
            Color top = NodeColor(usePressed ? node.BackgroundGradientTopPressed : node.BackgroundGradientTop,
                usePressed ? active : normal);
            Color bottom = NodeColor(usePressed ? node.BackgroundGradientBottomPressed : node.BackgroundGradientBottom,
                usePressed ? active : normal);
            keyShapeLayer.SetBackgroundGradient(slot, true, top, bottom);
        }

        private void ApplyCustomOutlineGradient(FmNode node, bool pressed)
        {
            if (node == null || keyShapeLayer == null || node.RuntimeKey == null) return;
            int slot = node.RuntimeKey.shapeSlot;
            if (slot < 0 || !node.UseOutlineGradient)
            {
                if (slot >= 0) keyShapeLayer.SetOutlineGradient(slot, false, Color.white, Color.white);
                return;
            }

            Color normal = node.NodeType == 1
                ? node.UseCustomColor ? NodeColor(node.Outline, Settings.Data.KpsOutline) : Settings.Data.KpsOutline
                : node.NodeType == 2
                    ? node.UseCustomColor ? NodeColor(node.Outline, Settings.Data.TotalOutline) : Settings.Data.TotalOutline
                    : node.UseCustomColor ? NodeColor(node.Outline, Settings.Data.Outline) : Settings.Data.Outline;
            Color active = node.NodeType == 1
                ? node.UseCustomColor ? NodeColor(node.OutlinePressed, normal) : normal
                : node.NodeType == 2
                    ? node.UseCustomColor ? NodeColor(node.OutlinePressed, normal) : normal
                    : node.UseCustomColor
                        ? NodeColor(node.OutlinePressed, Settings.Data.OutlineClicked)
                        : Settings.Data.OutlineClicked;
            bool usePressed = pressed && node.UsePressedOutlineGradient;
            keyShapeLayer.SetOutlineGradient(slot, true,
                NodeColor(usePressed ? node.OutlineGradientTopPressed : node.OutlineGradientTop, usePressed ? active : normal),
                NodeColor(usePressed ? node.OutlineGradientBottomPressed : node.OutlineGradientBottom, usePressed ? active : normal));
        }

        private void ApplyFixedBackgroundGradients()
        {
            if (IsCustomLayout || keyShapeLayer == null || Keys == null) return;
            for (int i = 0; i < Keys.Length; i++)
                ApplyFixedBackgroundGradient(Keys[i], Keys[i] != null && Keys[i].isPressed);
            ApplyFixedBackgroundGradient(Kps, Kps != null && Kps.isPressed);
            ApplyFixedBackgroundGradient(Total, Total != null && Total.isPressed);
        }

        private void ApplyFixedBackgroundGradient(Key key, bool pressed)
        {
            if (IsCustomLayout || key == null || keyShapeLayer == null) return;
            ProfileData d = Settings.Data;
            bool usePressed = pressed && d.FixedBackgroundGradientPressedOverride;
            keyShapeLayer.SetBackgroundGradient(key.shapeSlot, d.EnableFixedBackgroundGradient,
                usePressed ? d.FixedBackgroundGradientTopPressed : d.FixedBackgroundGradientTop,
                usePressed ? d.FixedBackgroundGradientBottomPressed : d.FixedBackgroundGradientBottom);
        }

        private void ApplyFixedOutlineGradients()
        {
            if (IsCustomLayout || keyShapeLayer == null || Keys == null) return;
            for (int i = 0; i < Keys.Length; i++)
                ApplyFixedOutlineGradient(Keys[i], Keys[i] != null && Keys[i].isPressed);
            ApplyFixedOutlineGradient(Kps, Kps != null && Kps.isPressed);
            ApplyFixedOutlineGradient(Total, Total != null && Total.isPressed);
        }

        private void ApplyFixedOutlineGradient(Key key, bool pressed)
        {
            if (IsCustomLayout || key == null || keyShapeLayer == null) return;
            ProfileData d = Settings.Data;
            bool usePressed = pressed && d.FixedOutlineGradientPressedOverride;
            keyShapeLayer.SetOutlineGradient(key.shapeSlot, d.EnableFixedOutlineGradient,
                usePressed ? d.FixedOutlineGradientTopPressed : d.FixedOutlineGradientTop,
                usePressed ? d.FixedOutlineGradientBottomPressed : d.FixedOutlineGradientBottom);
        }

        private void ApplyFixedKeyGlows()
        {
            if (IsCustomLayout)
            {
                ClearFixedGlowImages();
                return;
            }
            if (keyGlowLayer == null || Keys == null) return;
            keyGlowLayer.SetSiblingIndex(0);
            for (int i = 0; i < Keys.Length; i++)
                ApplyFixedGlow(Keys[i], i, Keys[i] != null && Keys[i].isPressed);
            ApplyFixedGlow(Kps, -1, Kps != null && Kps.isPressed);
            ApplyFixedGlow(Total, -2, Total != null && Total.isPressed);
        }

        private void ApplyFixedGlow(Key key, int index, bool pressed)
        {
            if (key == null) return;
            ProfileData d = Settings.Data;
            bool usePressedGlow = pressed && d.FixedKeyGlowPressedOverride;
            float rawSize = usePressedGlow ? d.FixedKeyGlowSizePressed : d.FixedKeyGlowSize;
            float rawOpacity = usePressedGlow ? d.FixedKeyGlowOpacityPressed : d.FixedKeyGlowOpacity;
            float size = float.IsNaN(rawSize) || float.IsInfinity(rawSize)
                ? 20f : Mathf.Clamp(rawSize, 0f, 50f);
            float opacity = float.IsNaN(rawOpacity) || float.IsInfinity(rawOpacity)
                ? 0.7f : Mathf.Clamp01(rawOpacity);
            bool visible = key.visuals == null || key.visuals.gameObject.activeSelf;
            Image image;
            if (!visible || !d.EnableFixedKeyGlow || size <= 0f || keyGlowLayer == null)
            {
                if (fixedGlowImages.TryGetValue(key, out image) && image != null)
                    image.enabled = false;
                return;
            }

            RectTransform source = key.transform as RectTransform;
            if (source == null) return;
            if (!fixedGlowImages.TryGetValue(key, out image) || image == null)
            {
                GameObject glowObject = new GameObject("GlowFixed_" + index);
                glowObject.transform.SetParent(keyGlowLayer, false);
                RectTransform rect = glowObject.AddComponent<RectTransform>();
                // Anchors/pivot mirror the key root and never change for the life of the key, so
                // they are copied once here instead of on every press.
                // 锚点/轴心与按键根一致且终生不变，只在建时复制一次，不再每次按压都写。
                rect.anchorMin = source.anchorMin;
                rect.anchorMax = source.anchorMax;
                rect.pivot = source.pivot;
                image = glowObject.AddComponent<Image>();
                image.sprite = GetCustomGlowSprite();
                image.type = Image.Type.Sliced;
                image.raycastTarget = false;
                fixedGlowImages[key] = image;
            }

            float pad = Mathf.Max(2f, size);
            RectTransform glowRect = (RectTransform)image.transform;
            Vector2 wantedPos = new Vector2(source.anchoredPosition.x - pad, source.anchoredPosition.y);
            Vector2 wantedSize = new Vector2(source.sizeDelta.x + pad * 2f, source.sizeDelta.y + pad * 2f);
            // Called on every press/release like the FreeMake path: skip unchanged rect writes so an
            // unchanged key does not dirty the canvas batch for nothing.
            // 同样每次按压都会调用：未变化的矩形不重写，避免无谓地弄脏 canvas 批次。
            if (glowRect.anchoredPosition != wantedPos) glowRect.anchoredPosition = wantedPos;
            if (glowRect.sizeDelta != wantedSize) glowRect.sizeDelta = wantedSize;
            int wantedSibling = Mathf.Clamp(index >= 0 ? index : index == -1 ? 62 : 63, 0, 63);
            if (glowRect.GetSiblingIndex() != wantedSibling) glowRect.SetSiblingIndex(wantedSibling);

            Color body = FixedGlowBodyColor(index, pressed, d);
            bool followBody = usePressedGlow ? d.FixedKeyGlowFollowBodyPressed : d.FixedKeyGlowFollowBody;
            Color glow = followBody ? body : (usePressedGlow ? d.FixedKeyGlowColorPressed : d.FixedKeyGlowColor);
            glow.a *= opacity;
            image.color = glow;
            image.enabled = true;
        }

        private static Color FixedGlowBodyColor(int index, bool pressed, ProfileData d)
        {
            if (index == -1) return d.KpsBackground;
            if (index == -2) return d.TotalBackground;
            if (KeyViewer.IsFullKeyboard)
            {
                bool unified = d.EnableFullKeyboardUnifiedColor;
                return pressed
                    ? (unified ? d.FullKeyboardBackgroundClicked : d.BackgroundClicked)
                    : (unified ? d.FullKeyboardBackground : d.Background);
            }
            if (d.EnablePerKeyColors && d.PerKeyBackground != null && d.PerKeyBackgroundClicked != null
                && index >= 0 && index < d.PerKeyBackground.Length && index < d.PerKeyBackgroundClicked.Length)
                return pressed ? d.PerKeyBackgroundClicked[index] : d.PerKeyBackground[index];
            return pressed ? d.BackgroundClicked : d.Background;
        }

        private void SetFixedGlowVisible(Key key, bool visible)
        {
            if (key == null) return;
            if (fixedGlowImages.TryGetValue(key, out Image image) && image != null)
                image.enabled = visible && Settings.Data.EnableFixedKeyGlow;
        }

        private void ClearFixedGlowImages()
        {
            foreach (Image image in fixedGlowImages.Values)
                if (image != null) Destroy(image.gameObject);
            fixedGlowImages.Clear();
        }

        private Key CreateCustomKey(FmNode node, int slot)
        {
            float cy = CustomNodeCenterY(node);
            bool isStat = node.NodeType == 1 || node.NodeType == 2;
            bool isImage = node.NodeType == 3;
            byte rainByte = CustomRainRowByte(node);
            Key key;
            if (isStat)
            {
                // KPS/Total panels use the dedicated -1/-2 indices so the full KPS/Total
                // machinery applies (SetKpsTotalDisplay etc.). The text mode comes from
                // StatTextMode — the node's layout override (UseCustomStatLayout) or the global
                // toggles. / KPS/Total 面板使用专属 -1/-2 索引，使完整 KPS/Total 机制生效
                //（SetKpsTotalDisplay 等）。文本模式来自 StatTextMode——节点的布局覆盖
                //（UseCustomStatLayout）或全局开关。
                StatTextMode(node, out bool statSlim, out bool statCentered, out bool statStacked, out bool statHideLabel);
                key = CreateKey(node.NodeType == 1 ? -1 : -2, node.X, cy, node.Width, -1, statSlim, true, node.Height, false, statHideLabel, statCentered, statStacked, customStatSlotCursor++);
            }
            else
            {
                key = CreateKey(slot, node.X, cy, node.Width,
                    node.RainEnabled ? rainByte : -1, false, true, node.Height);
            }
            key.CustomNode = node;
            node.RuntimeKey = key;

            if (isImage)
            {
                // Image keys draw no box — the RawImage replaces the shape-layer slot (kept
                // assigned but invisible so rain-follow press scaling still has an index). /
                // 图片按键不画盒子——RawImage 取代形状层槽位（槽位保留但不可见，使雨滴跟随
                // 的按压缩放仍有索引可用）。
                if (keyShapeLayer != null && key.shapeSlot >= 0)
                    keyShapeLayer.SetVisible(key.shapeSlot, false);
                CreateCustomImageObject(node, key);
                // Still apply the text/glow side of the color pass: image nodes were previously
                // skipped entirely, so their node glow and text colors only appeared after the
                // FIRST key press (unbound decorations applied them right away, which made the two
                // FreeMake paths inconsistent). ApplyCustomKeyColors already skips the box colors
                // for NodeType 3. / 图片节点仍要走文字与光效部分：此前整条分支被跳过，节点光效
                // 与文字色要等第一次按压才生效（未绑定装饰却立即生效，两条路径不一致）。
                ApplyCustomKeyColors(key, node, false);
            }
            else if (!isStat)
            {
                ApplyCustomKeyColors(key, node, false);
            }
            else
            {
                ApplyCustomSpecialColors(key, node, false);
            }

            key.rainColor = node.UseCustomRainColor
                ? NodeColor(node.RainColorBottom, rainSystem.GetRainColor(rainByte))
                : rainSystem.GetRainColor(rainByte);
            key.rainColorTop = node.UseCustomRainColor
                ? NodeColor(node.RainColorTop, key.rainColor)
                : key.rainColor;
            key.rainOffsetX = Mathf.Clamp(node.RainOffsetX, -2000f, 2000f);

            if (node.FontSize > 0f)
                key.text.fontSizeMax = node.FontSize;
            if (key.value != null)
                key.value.fontSizeMax = node.CountFontSize > 0f
                    ? node.CountFontSize
                    : (node.FontSize > 0f ? node.FontSize : key.text.fontSizeMax);
            FontStyles nodeFontStyle = (FontStyles)node.FontStyleFlags;
            key.text.fontStyle = nodeFontStyle;
            if (key.value != null)
                key.value.fontStyle = node.UseCustomCountFontStyle
                    ? (FontStyles)node.CountFontStyleFlags
                    : nodeFontStyle;
            if (!isStat)
                UpdateCustomKeyText(key, node); // stat labels are owned by SetKpsTotalDisplay / stat 标签由 SetKpsTotalDisplay 接管
            ApplyCustomShapeStyle(key, node);
            ApplyCustomTextStyles(key, node);
            LayoutCustomTexts(key, node);
            ApplyCustomTextOffsets(key, node);
            ApplyCustomGlow(node, false);
            return key;
        }

        /// <summary>Re-layout label/count texts after HideLabel/HideCount: hiding the count
        /// centers the label across the whole box (the same look as the fixed layout's
        /// hide-main-count), and hiding the label centers the count. / HideLabel/HideCount
        /// 切换后重排标签/计数文本：隐藏计数后标签居中占满整框（与固定布局的隐藏主键计数
        /// 同款观感），隐藏文字后计数居中。</summary>
        private static void LayoutCustomTexts(Key key, FmNode node)
        {
            if (key.text == null) return;
            bool hideLabel = node.HideLabel;
            bool hideCount = node.HideCount;
            if (key.value != null) key.value.gameObject.SetActive(!hideCount);
            key.text.gameObject.SetActive(!hideLabel);
            if (hideCount && !hideLabel)
            {
                RectTransform lt = key.text.rectTransform;
                lt.anchorMin = lt.anchorMax = lt.pivot = new Vector2(0.5f, 0.5f);
                lt.anchoredPosition = Vector2.zero;
                lt.sizeDelta = new Vector2(key.keySize.x - 4f, key.keySize.y - 4f);
                key.text.alignment = TextAlignmentOptions.Center;
            }
            else if (hideLabel && !hideCount && key.value != null)
            {
                RectTransform vt = key.value.rectTransform;
                vt.anchorMin = vt.anchorMax = vt.pivot = new Vector2(0.5f, 0.5f);
                vt.anchoredPosition = Vector2.zero;
                vt.sizeDelta = new Vector2(key.keySize.x - 4f, key.keySize.y - 4f);
                key.value.alignment = TextAlignmentOptions.Center;
            }
        }

        private static void ApplyCustomTextOffsets(Key key, FmNode node)
        {
            if (key == null || node == null) return;
            // Base-and-add, not "+=" on the live rect: LayoutCustomTexts has just authored the
            // neutral position, so capture it once and add the node offset on top. Accumulating on
            // the live value would drift by the offset again on any re-entrant call.
            // 先取中性基准再加节点偏移，而不是在当前 rect 上累加：任何再次进入的路径都会重复叠加。
            if (key.text != null)
            {
                RectTransform rt = key.text.rectTransform;
                Vector2 basePos = rt.anchoredPosition;
                rt.anchoredPosition = basePos + new Vector2(node.LabelOffsetX, node.LabelOffsetY);
                rt.localRotation = Quaternion.Euler(0f, 0f, node.LabelRotation);
                rt.localScale = Vector3.one * node.LabelScale;
                key.customTextBasePos = basePos;
            }
            if (key.value != null)
            {
                RectTransform rt = key.value.rectTransform;
                Vector2 basePos = rt.anchoredPosition;
                rt.anchoredPosition = basePos + new Vector2(node.CountOffsetX, node.CountOffsetY);
                rt.localRotation = Quaternion.Euler(0f, 0f, node.CountRotation);
                rt.localScale = Vector3.one * node.CountScale;
                key.customValueBasePos = basePos;
            }
        }

        private static void ApplyCustomPressedTextTransform(Key key, FmNode node, bool pressed)
        {
            if (key == null || node == null) return;
            // customTextBasePos / customValueBasePos hold the NEUTRAL layout position; the node's own
            // resting offset is added on top here so both the resting and pressed states stay derived
            // from one base (and the pressed offset never accumulates).
            // 基准位置是中性布局值：节点自身偏移在这里叠加，按下偏移只做加法，不会累积。
            if (key.text != null)
            {
                float scale = pressed && node.UsePressedLabelScale ? node.PressedLabelScale : node.LabelScale;
                float rotation = pressed && node.UsePressedLabelRotation ? node.PressedLabelRotation : node.LabelRotation;
                Vector2 rest = key.customTextBasePos + new Vector2(node.LabelOffsetX, node.LabelOffsetY);
                Vector2 offset = pressed && node.UsePressedLabelOffset
                    ? new Vector2(node.PressedLabelOffsetX, node.PressedLabelOffsetY) : Vector2.zero;
                key.text.rectTransform.anchoredPosition = rest + offset;
                key.text.rectTransform.localScale = Vector3.one * scale;
                key.text.rectTransform.localRotation = Quaternion.Euler(0f, 0f, rotation);
            }
            if (key.value != null)
            {
                float scale = pressed && node.UsePressedCountScale ? node.PressedCountScale : node.CountScale;
                float rotation = pressed && node.UsePressedCountRotation ? node.PressedCountRotation : node.CountRotation;
                Vector2 rest = key.customValueBasePos + new Vector2(node.CountOffsetX, node.CountOffsetY);
                Vector2 offset = pressed && node.UsePressedCountOffset
                    ? new Vector2(node.PressedCountOffsetX, node.PressedCountOffsetY) : Vector2.zero;
                key.value.rectTransform.anchoredPosition = rest + offset;
                key.value.rectTransform.localScale = Vector3.one * scale;
                key.value.rectTransform.localRotation = Quaternion.Euler(0f, 0f, rotation);
            }
        }

        /// <summary>Create the RawImage for an image node. With a Key it becomes the key's
        /// visual (textures swap on press); without one it is pure decoration. / 为图片节点创建
        /// RawImage。携带按键时它就是按键的视觉本体（按压时切换贴图）；否则为纯装饰。</summary>
        private void CreateCustomImageObject(FmNode node, Key key = null)
        {
            GameObject go = new GameObject("CustomImage_" + node.Id);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.SetParent(KeyViewerSizeObject.transform, false);
            rt.anchorMin = rt.anchorMax = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(node.X + node.Width * 0.5f, CustomNodeCenterY(node));
            rt.sizeDelta = new Vector2(node.Width, node.Height);
            // Sibling order is normalized after all image nodes are created; setting every image
            // to slot 0 here reverses their actual draw order. / 图片全部创建后统一规范 sibling
            // 顺序；这里每次都设为 0 会反转实际绘制顺序。
            RawImage raw = go.AddComponent<RawImage>();
            customImageRaws[node] = raw;
            raw.raycastTarget = false;
            // A video node renders the VideoPlayer's RenderTexture instead of a PNG. The node stays
            // NodeType 3, so everything else about it (binding, counting, press, rain, layering) is
            // untouched. When the file is missing/unsupported GetOrCreate returns null and this
            // falls through to the static-image path — a typo degrades to a placeholder, not to an
            // invisible node. / 视频节点渲染 VideoPlayer 的 RenderTexture 而非 PNG。节点仍为
            // NodeType 3，故其它一切（绑定、计数、按压、雨滴、层级）不受影响。文件缺失/不支持时
            // GetOrCreate 返回 null，本方法继续走静态图片路径——路径写错只退化为占位图，而非
            // 变成看不见的节点。
            RenderTexture video = string.IsNullOrWhiteSpace(node.VideoPath)
                ? null
                : KvVideoTextureManager.GetOrCreate(node.Id, node.VideoPath, node.VideoLoop, node.Width, node.Height);
            if (video != null)
            {
                raw.texture = video;
                raw.color = new Color(1f, 1f, 1f, Mathf.Clamp01(node.Opacity));
                if (key != null)
                {
                    // Keep static textures alongside the video so a configured pressed image can
                    // overlay the video and the normal image remains available as a fallback.
                    // 有效视频仍保留静态常态/按压纹理，使按压图片能覆盖视频，正常图片也可回退。
                    key.CustomVideoTexture = video;
                    key.CustomTexNormal = KvImageLoader.LoadTexture(ResolveCustomImagePath(node.ImagePath));
                    key.CustomTexPressed = KvImageLoader.LoadTexture(ResolveCustomImagePath(node.ImagePathPressed));
                }
            }
            else
            {
                Texture2D normal = KvImageLoader.LoadTexture(ResolveCustomImagePath(node.ImagePath));
                if (normal == null)
                {
                    raw.color = new Color(0.25f, 0.25f, 0.28f, 0.85f);
                    if (!string.IsNullOrWhiteSpace(node.ImagePath) || !string.IsNullOrWhiteSpace(node.VideoPath))
                        Loader.Warning($"KeyViewer: custom image/video '{node.ImagePath}{node.VideoPath}' not found, drew a placeholder");
                }
                else
                {
                    raw.texture = normal;
                    raw.color = new Color(1f, 1f, 1f, Mathf.Clamp01(node.Opacity));
                }
                if (key != null)
                {
                    key.CustomVideoTexture = null;
                    key.CustomTexNormal = normal;
                    key.CustomTexPressed = KvImageLoader.LoadTexture(ResolveCustomImagePath(node.ImagePathPressed));
                }
                else if (normal != null)
                {
                    // Decoration images own their texture exclusively (nothing else references it),
                    // so track it here — ReleaseCustomTextures destroys it on teardown. /
                    // 装饰图片独占自己的贴图（无其它引用），在此登记——ReleaseCustomTextures
                    // 在拆解时销毁它。
                    customDecorationTextures.Add(normal);
                }
            }
            if (key != null)
            {
                key.CustomImageRect = rt;
                key.CustomImage = raw;
            }
            customImageRects[node] = rt;
        }

        /// <summary>Place all image RawImages below the shape layers in stable Depth order.
        /// The editor uses the same image-first, Depth-ascending bucket, so hit selection and
        /// runtime overlap agree. / 将所有图片 RawImage 放在形状层之前，并按 Depth 稳定排序；
        /// 与编辑器使用相同的图片优先、Depth 升序规则保持一致。 </summary>
        private void OrderCustomImageRects()
        {
            int sibling = 0;
            // Keep the shared glow layer behind image bodies, then order images by Depth. /
            // 先把共享光效层固定在图片本体之后（下方），再按 Depth 排列图片。
            if (keyGlowLayer != null) keyGlowLayer.SetSiblingIndex(sibling++);
            foreach (FmNode node in Settings.Data.CustomNodes
                .Where(n => n != null && customImageRects.ContainsKey(n))
                .OrderBy(n => n.Depth))
            {
                RectTransform rect = customImageRects[node];
                if (rect == null) continue;
                rect.SetSiblingIndex(sibling++);
            }
        }

        // Textures loaded for DECORATION image nodes (keyed ones live on Key.CustomTexNormal/
        // Pressed). Destroying the RawImage or its GameObject does NOT destroy the Texture2D —
        // without this list every layout rebuild leaked one GPU texture per decoration node.
        // / 装饰图片节点加载的贴图（带键的存于 Key.CustomTexNormal/Pressed）。销毁 RawImage
        // 或其 GameObject 并不会销毁 Texture2D——没有这份清单，每次布局重建每个装饰节点
        // 都泄漏一张 GPU 贴图。
        private readonly List<Texture2D> customDecorationTextures = new List<Texture2D>();

        /// <summary>Destroy every custom-image texture (keyed + decoration) and drop the
        /// references. Unity's overloaded null makes this idempotent — a second call is a no-op.
        /// Called from ResetKeyViewer/DisableKeyViewer BEFORE the GameObjects go away.
        /// / 销毁全部自定义图片贴图（带键+装饰）并清引用。Unity 重载的判空使其幂等——
        /// 二次调用为空操作。由 ResetKeyViewer/DisableKeyViewer 在 GameObject 销毁前调用。
        /// </summary>
        private void ReleaseCustomGlowSprite()
        {
            if (customGlowSprite == null) return;
            if (customGlowSprite.texture != null) Destroy(customGlowSprite.texture);
            Destroy(customGlowSprite);
            customGlowSprite = null;
        }

        private void ClearCustomGlowImages()
        {
            foreach (Image image in customGlowImages.Values)
                if (image != null) Destroy(image.gameObject);
            customGlowImages.Clear();
        }

        private void ReleaseCustomTextures()
        {
            ClearTextGradientStates();
            ClearFixedGlowImages();
            ClearCustomGlowImages();
            if (Keys != null)
            {
                for (int i = 0; i < Keys.Length; i++)
                {
                    Key k = Keys[i];
                    if (k == null) continue;
                    if (k.CustomTexNormal != null) Destroy(k.CustomTexNormal);
                    if (k.CustomTexPressed != null) Destroy(k.CustomTexPressed);
                    k.CustomTexNormal = null;
                    k.CustomTexPressed = null;
                    k.CustomImage = null;
                    k.CustomImageRect = null;
                    k.CustomVideoTexture = null;
                }
            }
            for (int i = 0; i < customDecorationTextures.Count; i++)
                if (customDecorationTextures[i] != null) Destroy(customDecorationTextures[i]);
            customDecorationTextures.Clear();
            customImageRects.Clear();
            customImageRaws.Clear();
            customGlowImages.Clear();
            customVideoFallbackApplied.Clear();
        }

        /// <summary>Resolve an image reference: absolute path as-is, otherwise relative to
        /// CustomImages/ under the mod directory. / 解析图片引用：绝对路径原样，否则相对 Mod
        /// 目录下 CustomImages/。</summary>
        internal static string ResolveCustomImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (Path.IsPathRooted(path)) return File.Exists(path) ? path : null;
                string rel = Path.Combine(Loader.ResolveModPath(), "CustomImages", path);
                if (File.Exists(rel)) return rel;
                return File.Exists(path) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private Sprite GetCustomGlowSprite()
        {
            if (customGlowSprite != null) return customGlowSprite;
            const int size = 64;
            const int margin = 22;
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                float ty = Mathf.Clamp01(Mathf.Min(y, size - 1 - y) / (float)margin);
                float ay = ty * ty * (3f - 2f * ty);
                for (int x = 0; x < size; x++)
                {
                    float tx = Mathf.Clamp01(Mathf.Min(x, size - 1 - x) / (float)margin);
                    float ax = tx * tx * (3f - 2f * tx);
                    pixels[y * size + x] = new Color(1f, 1f, 1f, ax * ay);
                }
            }
            texture.SetPixels(pixels);
            texture.Apply(false, false);
            customGlowSprite = Sprite.Create(texture, new Rect(0f, 0f, size, size),
                new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect,
                new Vector4(margin, margin, margin, margin));
            return customGlowSprite;
        }

        internal static Color NodeColor(float[] arr, Color fallback)
        {
            return arr != null && arr.Length == 4
                ? new Color(arr[0], arr[1], arr[2], arr[3])
                : fallback;
        }

        /// <summary>Runtime visibility of a node: the Hidden flag AND its layer group's toggle. /
        /// 节点运行时可见性：Hidden 标志与其图层组开关的组合。</summary>
        private static bool CustomNodeVisible(FmNode node)
        {
            if (node.Hidden) return false;
            if (string.IsNullOrEmpty(node.GroupId)) return true;
            foreach (FmLayerGroup group in Settings.Data.LayerGroups)
                if (group != null && group.Id == node.GroupId) return group.Visible;
            return true;
        }

        /// <summary>Apply the node's text outline/shadow override to its label and count texts.
        /// Only nodes with UseCustomTextStyle do anything here — everything else already got the
        /// global style in ConfigureText. / 把节点的文字描边/阴影覆盖应用到其标签与计数文本。只有
        /// 开启了 UseCustomTextStyle 的节点在此有动作——其余节点已在 ConfigureText 中拿到全局
        /// 样式。</summary>
        private void ApplyCustomTextStyles(Key key, FmNode node)
        {
            if (key == null || node == null || !node.UseCustomTextStyle) return;
            TMP_FontAsset font = GetCurrentFont();
            if (font == null) return;
            if (key.text != null)
            {
                Material m = GetTextStyleMaterial(font, KvTextStyle.Resolve(Settings.Data, node, KvTextKind.KeyLabel));
                // Through the reference-counted setter: a direct assignment would leave the cached
                // material's use count stale, so it could either never be evicted or be evicted
                // while this text still renders with it (blank text). / 走引用计数 setter：直接赋值
                // 会让缓存材质的使用计数失真，材质要么永远无法回收，要么在该文本仍在使用时被回收
                // （文字变空白）。
                if (m != null) ApplyFontMaterial(key.text, m);
            }
            if (key.value != null)
            {
                Material m = GetTextStyleMaterial(font, KvTextStyle.Resolve(Settings.Data, node, KvTextKind.Count));
                if (m != null) ApplyFontMaterial(key.value, m);
            }
        }

        /// <summary>Push the node's box shape (corner radius / border thickness) into the merged
        /// shape layer. Image nodes draw no box, so their slot keeps the defaults. / 把节点的盒子
        /// 形状（圆角半径/边框厚度）写入合并形状层。图片节点不画盒子，其槽位保持默认值。</summary>
        private void ApplyCustomShapeStyle(Key key, FmNode node)
        {
            if (key == null || keyShapeLayer == null || key.shapeSlot < 0) return;
            if (node.NodeType == 3) return;
            keyShapeLayer.SetCornerRadius(key.shapeSlot, node.CornerRadius);
            keyShapeLayer.SetBorderThickness(key.shapeSlot, node.BorderThickness);
        }

        private void ApplyCustomKeyColors(Key key, FmNode node, bool pressed)
        {
            ProfileData d = Settings.Data;
            // Image keys draw no box — only their texts follow the press colors. /
            // 图片按键无盒子——只有文本跟随按压配色。
            if (node.NodeType != 3)
            {
                Color bg = node.UseCustomColor ? NodeColor(node.Bg, d.Background) : d.Background;
                Color bgPressed = node.UseCustomColor ? NodeColor(node.BgPressed, d.BackgroundClicked) : d.BackgroundClicked;
                Color ol = node.UseCustomColor ? NodeColor(node.Outline, d.Outline) : d.Outline;
                Color olPressed = node.UseCustomColor ? NodeColor(node.OutlinePressed, d.OutlineClicked) : d.OutlineClicked;
                SetShapeColors(key, pressed ? bgPressed : bg, pressed ? olPressed : ol);
            }
            // Per-node text colors (null arrays fall back to the globals). /
            // 节点级文本颜色（数组为空回落全局）。
            Color txt = node.UseCustomColor && node.TextColor != null ? NodeColor(node.TextColor, d.Text) : d.Text;
            Color txtPressed = node.UseCustomColor && node.TextColorPressed != null ? NodeColor(node.TextColorPressed, d.TextClicked) : d.TextClicked;
            Color labelColor = pressed ? txtPressed : txt;
            labelColor.a *= node.TextOpacity;
            key.text.color = labelColor;
            if (key.value != null)
            {
                Color countTxt = node.UseCustomCountTextColor && node.CountTextColor != null
                    ? NodeColor(node.CountTextColor, txt) : txt;
                Color countTxtP = node.UseCustomCountTextColor && node.CountTextColorPressed != null
                    ? NodeColor(node.CountTextColorPressed, txtPressed) : txtPressed;
                countTxt.a *= node.CountTextOpacity;
                countTxtP.a *= node.CountTextOpacity;
                key.value.color = pressed ? countTxtP : countTxt;
            }
            ApplyCustomGlow(node, pressed);
            ApplyCustomBackgroundGradient(node, pressed);
            ApplyCustomOutlineGradient(node, pressed);
        }

        /// <summary>KPS/Total nodes keep the dedicated Kps*/Total* colors (no per-node override in v1). /
        /// KPS/Total 节点沿用专属 Kps*/Total* 配色（v1 不做节点级覆盖）。</summary>
        /// <summary>KPS/Total panels follow the dedicated Kps*/Total* colors, with the node
        /// color override (UseCustomColor) taking precedence — all node types share the same
        /// override fields. / KPS/Total 面板跟随专属 Kps*/Total* 颜色，
        /// 节点配色覆盖（UseCustomColor）优先——所有节点类型共用同一组覆盖字段。
        /// KPS/Total 专属色没有按压变体，节点覆盖色有。</summary>
        private void ApplyCustomSpecialColors(Key key, FmNode node, bool pressed)
        {
            bool isKps = node.NodeType == 1;
            Color bg, bgP, ol, olP;
            if (node.UseCustomColor)
            {
                bg = NodeColor(node.Bg, isKps ? Settings.Data.KpsBackground : Settings.Data.TotalBackground);
                // The dedicated KPS/Total sets have no pressed variants — node pressed colors
                // fall back to the node's own idle color. / 专属 KPS/Total 色没有按压变体——
                // 节点按压色回退到节点自身的常态色。
                bgP = NodeColor(node.BgPressed, bg);
                ol = NodeColor(node.Outline, isKps ? Settings.Data.KpsOutline : Settings.Data.TotalOutline);
                olP = NodeColor(node.OutlinePressed, ol);
            }
            else
            {
                bg = isKps ? Settings.Data.KpsBackground : Settings.Data.TotalBackground;
                bgP = bg;
                ol = isKps ? Settings.Data.KpsOutline : Settings.Data.TotalOutline;
                olP = ol;
            }
            SetShapeColors(key, pressed ? bgP : bg, pressed ? olP : ol);
            // Per-node text colors on stat panels: base falls back to the dedicated Kps/Total
            // text color, the pressed variant to the node's own base (dedicated sets have no
            // pressed variant). / 面板节点的节点级文本颜色：常态回落专属 Kps/Total 文本色，
            // 按压变体回落节点自身常态色（专属色无按压变体）。
            Color statBase = isKps ? Settings.Data.KpsText : Settings.Data.TotalText;
            Color statTxt = node.UseCustomColor && node.TextColor != null ? NodeColor(node.TextColor, statBase) : statBase;
            Color statTxtP = node.UseCustomColor && node.TextColorPressed != null ? NodeColor(node.TextColorPressed, statTxt) : statTxt;
            Color labelColor = pressed ? statTxtP : statTxt;
            labelColor.a *= node.TextOpacity;
            key.text.color = labelColor;
            if (key.value != null)
            {
                Color countTxt = node.UseCustomCountTextColor && node.CountTextColor != null
                    ? NodeColor(node.CountTextColor, statTxt) : statTxt;
                Color countTxtP = node.UseCustomCountTextColor && node.CountTextColorPressed != null
                    ? NodeColor(node.CountTextColorPressed, statTxtP) : statTxtP;
                countTxt.a *= node.CountTextOpacity;
                countTxtP.a *= node.CountTextOpacity;
                key.value.color = pressed ? countTxtP : countTxt;
            }
            ApplyCustomGlow(node, pressed);
            ApplyCustomBackgroundGradient(node, pressed);
            ApplyCustomOutlineGradient(node, pressed);
        }

        private void UpdateCustomKeyText(Key key, FmNode node)
        {
            UpdateCustomKeyText(key, node, false);
        }

        private void UpdateCustomKeyText(Key key, FmNode node, bool pressed)
        {
            string label = pressed && !string.IsNullOrEmpty(node.PressedText) ? node.PressedText
                : !string.IsNullOrEmpty(node.CustomText)
                    ? node.CustomText
                    : KeyToString(CustomNodeKeyCode(node));
            key.text.text = label;
            if (key.value != null)
                key.value.text = FormatCount(node.Count, node);
        }

        internal static KeyCode CustomNodeKeyCode(FmNode node)
        {
            if (string.IsNullOrWhiteSpace(node.KeyBind)) return KeyCode.None;
            return Enum.TryParse(node.KeyBind, true, out KeyCode parsed) ? parsed : KeyCode.None;
        }

        /// <summary>Ids whose video decoder has failed and whose node therefore still needs the
        /// static fallback. A fallback is a ONE-TIME reaction to an error callback, yet the scan
        /// that applied it walked every CustomNode on every single frame — a per-frame
        /// IsNullOrWhiteSpace + two hash probes per node, on a document that may hold 2048 of
        /// them. Registered from OnVideoError so an ordinary frame does no scanning at all. /
        /// 解码失败、因而仍需套用静态回退图的节点 id。回退是对一次错误回调的**一次性**反应，
        /// 而施加它的扫描此前每帧遍历每个 CustomNode——每帧每节点一次 IsNullOrWhiteSpace 加两次
        /// 哈希探针，而文档最多可有 2048 个节点。改由 OnVideoError 登记，使普通帧完全不扫描。</summary>
        private static readonly HashSet<int> pendingVideoFallbacks = new HashSet<int>();

        /// <summary>Called by the video manager when a node's decoder errors. / 视频管理器在某个
        /// 节点解码出错时调用。</summary>
        internal static void NoteVideoDecodeFailure(int nodeId) => pendingVideoFallbacks.Add(nodeId);

        private void UpdateCustomVideoFallbacks()
        {
            // Nothing failed → nothing to do. This is the overwhelmingly common case. /
            // 没有失败 → 无事可做。这占绝大多数情况。
            if (pendingVideoFallbacks.Count == 0) return;
            if (Settings.Data?.CustomNodes == null) return;
            foreach (FmNode node in Settings.Data.CustomNodes)
            {
                if (node == null || node.NodeType != 3 || string.IsNullOrWhiteSpace(node.VideoPath)
                    || customVideoFallbackApplied.Contains(node.Id)
                    || !pendingVideoFallbacks.Contains(node.Id)
                    || !KvVideoTextureManager.HasFailed(node.Id)) continue;
                RawImage raw;
                if (!customImageRaws.TryGetValue(node, out raw) || raw == null) continue;

                // Key-bound images already retained their static normal texture during creation;
                // unbound decoration images load theirs only after a decoder failure.
                Key key = node.RuntimeKey;
                if (key != null) key.CustomVideoTexture = null;
                Texture normal = key != null ? key.CustomTexNormal : null;
                if (normal == null)
                {
                    Texture2D loaded = KvImageLoader.LoadTexture(ResolveCustomImagePath(node.ImagePath));
                    normal = loaded;
                    // A DECORATION video node (no key) owns whatever it loads. Without registering
                    // it, ReleaseCustomTextures — which only walks the Keys array and this list —
                    // never destroyed it, leaking one GPU Texture2D per rebuild for the rest of
                    // the session. The key-bound branch stores its textures on the Key instead.
                    // 无键的**装饰**视频节点独占它加载的贴图。未登记时，ReleaseCustomTextures
                    // （只遍历 Keys 数组与该列表）永远不会销毁它——此后每次布局重建都泄漏一张
                    // GPU 贴图。带键分支则把贴图存在 Key 上。
                    if (key == null && loaded != null) customDecorationTextures.Add(loaded);
                }
                Texture fallback = key != null && key.isPressed && key.CustomTexPressed != null
                    ? key.CustomTexPressed : normal;
                if (fallback != null)
                {
                    raw.texture = fallback;
                    raw.color = new Color(1f, 1f, 1f, Mathf.Clamp01(node.Opacity));
                }
                else
                {
                    raw.texture = null;
                    raw.color = new Color(0.25f, 0.25f, 0.28f, 0.85f * Mathf.Clamp01(node.Opacity));
                }
                customVideoFallbackApplied.Add(node.Id);
                pendingVideoFallbacks.Remove(node.Id);
            }
            // Anything still pending belongs to a node that no longer exists (deleted, or rebuilt
            // away) — keeping it would make this scan run forever on a document that has no video
            // failures at all. / 仍待处理的项属于已不存在的节点（被删除或重建掉）——留着会让这次
            // 扫描在一个根本没有视频失败的文档上永远运行。
            if (pendingVideoFallbacks.Count > 0 && customVideoFallbackApplied.Count > 0)
            {
                customFallbackPruneBuffer.Clear();
                foreach (int id in pendingVideoFallbacks)
                    if (!customVideoFallbackApplied.Contains(id)) customFallbackPruneBuffer.Add(id);
                for (int i = 0; i < customFallbackPruneBuffer.Count; i++)
                    pendingVideoFallbacks.Remove(customFallbackPruneBuffer[i]);
            }
        }

        private static readonly List<int> customFallbackPruneBuffer = new List<int>();

        // ======================== per-frame input / 逐帧输入 ========================

        private void ProcessCustomKeysInUpdate(long nowMs)
        {
            UpdateCustomVideoFallbacks();
            ProfileData d = Settings.Data;
            bool rainEnabled = d.EnableRainEffect;
            for (int i = 0; i < Keys.Length; i++)
            {
                Key key = Keys[i];
                if (key == null || key.CustomNode == null) continue;
                FmNode node = key.CustomNode;

                if (node.NodeType == 0 || node.NodeType == 3)
                {
                    // Cache the parsed binding; reparse only when the raw string changes. /
                    // 缓存解析结果，仅当原始字符串变化时重解析。
                    if (!string.Equals(key.CustomKeyBindCached, node.KeyBind, StringComparison.Ordinal))
                    {
                        key.CustomKeyBindCached = node.KeyBind;
                        key.CustomKeyCode = CustomNodeKeyCode(node);
                        // Bindings changed → the on-screen label follows the new key. /
                        // 绑定变更 → 屏幕文本跟随新按键。
                        UpdateCustomKeyText(key, node);
                    }
                    bool current = key.CustomKeyCode != KeyCode.None && KeySource.GetKey(key.CustomKeyCode);
                    if (current != key.isPressed)
                        ApplyCustomKeyEdge(key, node, current, nowMs, d);

                    // Ghost binding: same edge semantics as the fixed layouts' ghost keys, and the
                    // same parse caching as the main binding above. / 鬼键：与固定布局鬼键相同的
                    // 边沿语义，并复用上方主绑定那套解析缓存。
                    if (!string.Equals(key.CustomGhostBindCached, node.GhostKey, StringComparison.Ordinal))
                    {
                        key.CustomGhostBindCached = node.GhostKey;
                        key.CustomGhostCode = string.IsNullOrWhiteSpace(node.GhostKey)
                            || !Enum.TryParse(node.GhostKey, true, out KeyCode parsedGhost)
                            ? KeyCode.None : parsedGhost;
                    }
                    KeyCode ghostCode = key.CustomGhostCode;
                    if (ghostCode != KeyCode.None)
                    {
                        bool ghostNow = KeySource.GetKey(ghostCode);
                        // First-sight default must be FALSE (key up), not the current reading —
                        // defaulting to `ghostNow` swallowed the press edge every time and ghost
                        // rain NEVER fired on custom layouts. / 首见默认必须是"未按下"而非当前
                        // 读数——默认成 ghostNow 会每次吞掉按下边沿，自定义布局的鬼雨从未触发过。
                        if (!customGhostStates.TryGetValue(node.Id, out bool ghostPrev)) ghostPrev = false;
                        if (ghostNow != ghostPrev)
                        {
                            customGhostStates[node.Id] = ghostNow;
                            if (rainEnabled && d.EnableGhostRain && node.RainEnabled)
                            {
                                if (ghostNow) rainSystem.TriggerGhostRain(i, key);
                                else rainSystem.ReleaseGhostRain(i, key);
                            }
                        }
                    }
                    else
                    {
                        customGhostStates.Remove(node.Id);
                    }

                    // Per-key KPS display drains its own log. / 每键 KPS 显示消费自己的队列。
                    if (node.PerKeyKps && key.value != null)
                    {
                        while (key.KpsLog.Count > 0 && nowMs - key.KpsLog.Peek() > 1000)
                            key.KpsLog.Dequeue();
                        int kps = key.KpsLog.Count;
                        if (key.LastShownKps != kps)
                        {
                            key.LastShownKps = kps;
                            NumBuffer.Format(kps, NodeThousands(node), out var buf, out int off, out int len);
                            key.value.SetText(buf, off, len);
                        }
                    }
                }
                else
                {
                    // KPS/Total nodes only track press visuals (no counting of their own). The
                    // binding is cached exactly like the main and ghost ones — parsing it every
                    // frame was O(nodes × 500) OrdinalIgnoreCase comparisons.
                    // KPS/Total 节点只跟踪按压视觉（自身不计数）。绑定与主键、鬼键一样缓存——
                    // 此前每帧解析即 O(节点数 × 500) 次 OrdinalIgnoreCase 比较。
                    if (!string.Equals(key.CustomPanelBindCached, node.KeyBind, StringComparison.Ordinal))
                    {
                        key.CustomPanelBindCached = node.KeyBind;
                        key.CustomPanelCode = string.IsNullOrWhiteSpace(node.KeyBind)
                            || !Enum.TryParse(node.KeyBind, true, out KeyCode parsedPanel)
                            ? KeyCode.None : parsedPanel;
                    }
                    bool statPressed = key.CustomPanelCode != KeyCode.None
                        && KeySource.GetKey(key.CustomPanelCode);
                    if (statPressed != key.isPressed)
                    {
                        key.isPressed = statPressed;
                        if (key.text != null)
                            key.text.gameObject.SetActive(!node.HideLabel && (!statPressed || !node.HideLabelWhilePressed));
                        if (key.value != null)
                            key.value.gameObject.SetActive(!node.HideCount && (!statPressed || node.CountShowWhilePressed));
                        ApplyCustomPressedTextTransform(key, node, statPressed);
                        ApplyCustomSpecialColors(key, node, statPressed);
                    }
                }
            }
            if (rainEnabled) rainSystem.UpdateEffects(Keys);
        }

        private void ApplyCustomKeyEdge(Key key, FmNode node, bool down, long timeMs, ProfileData d)
        {
            key.isPressed = down;
            if (key.text != null)
                key.text.gameObject.SetActive(!node.HideLabel && (!down || !node.HideLabelWhilePressed));
            if (key.value != null)
                key.value.gameObject.SetActive(!node.HideCount && (!down || node.CountShowWhilePressed));
            ApplyCustomPressedTextTransform(key, node, down);
            // Press scale — the same animation the fixed layouts run from ProcessKeyGroup; the
            // custom input path used to skip it entirely, so 按压缩放 did nothing here. Per-node:
            // PressAnimEnabled opts out, UseCustomPressAnim overrides the global scale value.
            // / 按压缩放——与固定布局在 ProcessKeyGroup 里启动的同一动画；自定义输入路径此前
            // 完全没启动它，导致「按压缩放」在这里不生效。节点级：PressAnimEnabled 可退出，
            // UseCustomPressAnim 覆盖全局缩放值。
            if (d.EnablePressAnimation && node.PressAnimEnabled)
            {
                float pressScale = node.UseCustomPressAnim ? node.PressAnimScale : d.PressAnimationScale;
                float scaleTarget = down ? pressScale : 1f;
                if (key.currentAnim != null)
                    StopCoroutine(key.currentAnim);
                key.currentAnim = StartCoroutine(AnimateKeyScale(key, scaleTarget, PressAnimDurationFor(key)));
            }
            // Image keys swap to the pressed texture first, while video keys restore their
            // RenderTexture on release. / 图片按键先切换按压贴图；视频按键松开时恢复视频纹理。
            if (key.CustomImage != null)
            {
                Texture target = down
                    ? (Texture)(key.CustomTexPressed != null ? key.CustomTexPressed : key.CustomVideoTexture)
                    : (Texture)(key.CustomVideoTexture != null ? key.CustomVideoTexture : key.CustomTexNormal);
                key.CustomImage.texture = target;
                // Re-apply the node opacity on the swapped texture — the texture swap above
                // resets nothing, but the RawImage color was set only at creation; keep it
                // authoritative here so an opacity edit mid-session holds. / 换贴图后重施节点
                // 不透明度——RawImage 的 color 只在创建时设置过，此处保持其为权威值。
                key.CustomImage.color = new Color(1f, 1f, 1f, Mathf.Clamp01(node.Opacity));
            }
            ApplyCustomKeyColors(key, node, down);
            // Pressed-text swap: the label swaps while held, restores on
            // release. / 按压文案切换（按压语义）：按住时替换标签，松开恢复。
            if (!string.IsNullOrEmpty(node.PressedText))
                UpdateCustomKeyText(key, node, down);
            if (!down)
            {
                if (d.EnableRainEffect && node.RainEnabled)
                    rainSystem.ReleaseRainEffect(key.shapeSlot, key);
                return;
            }
            node.Count++;
            if (node.CountInTotal)
            {
                d.TotalCount++;
                BumpGroupTotal(node.GroupId, 1);
            }
            // CountInTotal controls Total membership only. KPS must continue to see every
            // physical/replay press, including nodes excluded from Total. / CountInTotal 只控制
            // Total 成员资格；KPS 仍必须看到所有物理/回放按压，包括被排除在 Total 之外的节点。
            PressTimes.Enqueue(timeMs);
            // KPS group queues must record EVERY press in the group, not just those whose
            // nodes opt into CountInTotal — otherwise a group with CountInTotal=false on all
            // its keys would always show KPS 0. / KPS 组队列必须记录该组的每次按压，而非仅
            // 记录选择 CountInTotal 的节点——否则全组 CountInTotal=false 时 KPS 恒为 0。
            EnqueueCustomGroupPress(node.GroupId, timeMs);
            // Counter bounce : kick on press — on whichever text is
            // visible (label when the count is hidden). / 计数器弹跳
            // （计数器弹跳动画）：按下时启动——作用在当前可见的文本上（计数隐藏时为标签）。
            if (node.CounterAnimEnabled && node.CounterAnimScale > 1.001f
                && (key.value != null || key.text != null))
            {
                if (!key.Bouncing)
                {
                    key.Bouncing = true;
                    TextMeshProUGUI target = node.HideCount || key.value == null ? key.text : key.value;
                    // Store the NEUTRAL base, not the live rect: the press transform has already
                    // been applied at this point, so capturing the rect would bake the pressed
                    // offset into the bounce's resting position. / 存中性基准而非当前 rect：此刻
                    // 按下变换已生效，捕获 rect 会把按下偏移固化进弹跳的静止位置。
                    key.BounceBasePos = target == key.value && key.value != null
                        ? key.customValueBasePos : key.customTextBasePos;
                    counterBounces.Add(key);
                }
                key.BounceStart = Time.unscaledTime;
            }
            if (node.PerKeyKps)
            {
                key.KpsLog.Enqueue(timeMs);
                _hasKeyPressActivity = true;
            }
            if (key.value != null && !node.PerKeyKps)
            {
                NumBuffer.Format(node.Count, NodeThousands(node), out var buf, out int off, out int len);
                key.value.SetText(buf, off, len);
            }
            if (d.EnableRainEffect && node.RainEnabled)
                rainSystem.TriggerRainEffect(key.shapeSlot, key);
        }

        private void ApplyCustomAllColors()
        {
            for (int i = 0; i < Keys.Length; i++)
            {
                Key key = Keys[i];
                if (key == null || key.CustomNode == null) continue;
                FmNode node = key.CustomNode;
                if (node.NodeType == 0 || node.NodeType == 3) ApplyCustomKeyColors(key, node, key.isPressed);
                else ApplyCustomSpecialColors(key, node, key.isPressed);

                // Global rain color changes must update the cached colors used by the next drop.
                // Previously only shape/text colors were refreshed, so non-overridden Custom nodes
                // kept the old color until a full rebuild. / 全局雨色变化要同步下一滴使用的缓存色；
                // 旧实现只刷新盒子/文字颜色，未覆盖的 Custom 节点要等完整重建才换色。
                byte rainByte = CustomRainRowByte(node);
                key.rainColor = node.UseCustomRainColor
                    ? NodeColor(node.RainColorBottom, rainSystem.GetRainColor(rainByte))
                    : rainSystem.GetRainColor(rainByte);
                key.rainColorTop = node.UseCustomRainColor
                    ? NodeColor(node.RainColorTop, key.rainColor)
                    : key.rainColor;
            }
        }

        private void RefreshCustomCountDisplays()
        {
            for (int i = 0; i < Keys.Length; i++)
            {
                Key key = Keys[i];
                if (key == null || key.CustomNode == null || key.value == null) continue;
                FmNode node = key.CustomNode;
                if (node.NodeType == 0 || node.NodeType == 3)
                {
                    if (node.PerKeyKps)
                    {
                        key.LastShownKps = int.MinValue; // force rewrite / 强制下帧重写
                    }
                    else
                    {
                        key.value.text = FormatCount(node.Count, node);
                    }
                }
            }
        }
    }
}
