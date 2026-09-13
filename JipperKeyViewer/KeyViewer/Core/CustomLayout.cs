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
        /// <summary>Shape-slot cursor for stat panels during a custom build — each panel gets
        /// its OWN slot (KeyIndex(-1/-2) collapsed them all onto one). /
        /// 自定义构建期间面板形状槽位游标——每块面板独占一槽（KeyIndex(-1/-2) 曾全部压成一槽）。</summary>
        private int customStatSlotCursor;

        // Per-GROUP press timestamps for grouped stat panels: a panel in group G shows the KPS
        // of G's keys only. Ungrouped panels keep the global PressTimes semantics. /
        // 按组的按压时间戳：G 组的面板只显示 G 组按键的 KPS。未分组面板保持全局语义。
        private readonly Dictionary<string, Queue<long>> customGroupPresses = new Dictionary<string, Queue<long>>();
        private readonly Dictionary<string, int> customShownKps = new Dictionary<string, int>();
        private readonly Dictionary<string, long> customShownTotal = new Dictionary<string, long>();

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
            long sum = 0;
            foreach (FmNode n in Settings.Data.CustomNodes)
                if (n != null && n.GroupId == g && n.CountInTotal && (n.NodeType == 0 || n.NodeType == 3))
                    sum += n.Count;
            return sum;
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
                float eased = CubicBezierEase(node.CounterAnimBezier, t);                float scale = 1f + (node.CounterAnimScale - 1f) * (1f - eased);
                RectTransform rt = target.rectTransform;
                rt.localScale = new Vector3(scale, scale, 1f);
                Vector2 centerOffset = (new Vector2(0.5f, 0.5f) - rt.pivot) * rt.rect.size * (scale - 1f);
                rt.anchoredPosition = key.BounceBasePos - centerOffset;
                if (t >= 1f)
                {
                    rt.localScale = Vector3.one;
                    rt.anchoredPosition = key.BounceBasePos;
                    key.Bouncing = false;
                    if (i < counterBounces.Count) counterBounces.RemoveAt(i);
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

        /// <summary>Validate/clamp the custom node list: drop nulls and hidden inconsistencies,
        /// assign missing ids, enforce caps and sane bounds. / 校验并钳制节点列表：去空、补 id、
        /// 强制上限与合理边界。</summary>
        private static void EnsureCustomNodes()
        {
            List<FmNode> nodes = Settings.Data.CustomNodes;
            if (nodes == null)
            {
                Settings.Data.CustomNodes = nodes = new List<FmNode>();
            }
            for (int i = nodes.Count - 1; i >= 0; i--)
            {
                FmNode node = nodes[i];
                if (node == null)
                {
                    nodes.RemoveAt(i);
                    continue;
                }
                if (node.Id <= 0) node.Id = Settings.Data.CustomNodeNextId++;
                if (node.Id >= Settings.Data.CustomNodeNextId) Settings.Data.CustomNodeNextId = node.Id + 1;
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
                node.RainRow = Mathf.Clamp(node.RainRow, 0, 2);
                node.FontSize = float.IsNaN(node.FontSize) || node.FontSize < 0f ? 0f : Mathf.Min(node.FontSize, 72f);
                node.RainOffsetX = float.IsNaN(node.RainOffsetX) ? 0f : Mathf.Clamp(node.RainOffsetX, -2000f, 2000f);
                node.RainOffsetY = float.IsNaN(node.RainOffsetY) ? 0f : Mathf.Clamp(node.RainOffsetY, -2000f, 2000f);
                node.CounterAnimScale = float.IsNaN(node.CounterAnimScale) ? 1.1f : Mathf.Clamp(node.CounterAnimScale, 1f, 2f);
                node.CounterAnimDurationMs = node.CounterAnimDurationMs <= 0f || float.IsNaN(node.CounterAnimDurationMs)
                    ? 300f
                    : Mathf.Min(node.CounterAnimDurationMs, 5000f);
                // Hand-edited profiles could carry a null/short bezier — CubicBezierEase indexes
                // [0..3] raw. / 手改配置可能带空/短贝塞尔——CubicBezierEase 直接索引 [0..3]。
                if (node.CounterAnimBezier == null || node.CounterAnimBezier.Length != 4)
                    node.CounterAnimBezier = new float[] { 0.25f, 0.46f, 0.45f, 0.94f };
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
            // Stat panels draw after the key slots, one shape slot each. /
            // 面板排在按键槽位之后绘制，各占一个形状槽。
            customStatSlotCursor = Keys.Length;
            customGroupPresses.Clear();
            customShownKps.Clear();
            customShownTotal.Clear();
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
            foreach (FmNode node in nodes)
                if (node != null && node.NodeType == 3 && !CustomNodeHasKey(node) && CustomNodeVisible(node))
                    CreateCustomImageObject(node);
            // Release every video player this pass did not touch. / 释放本次构建未触及的所有视频播放器。
            KvVideoTextureManager.EndBuild();
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
            {
                key.text.fontSizeMax = node.FontSize;
                if (key.value != null) key.value.fontSizeMax = node.FontSize;
            }
            if (!isStat)
                UpdateCustomKeyText(key, node); // stat labels are owned by SetKpsTotalDisplay / stat 标签由 SetKpsTotalDisplay 接管
            ApplyCustomShapeStyle(key, node);
            ApplyCustomTextStyles(key, node);
            LayoutCustomTexts(key, node);
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
            // Below the shape layers: children render in order, so slot 0 draws first. /
            // 置于形状层之下：子物体按顺序渲染，槽位 0 最先画。
            rt.SetSiblingIndex(0);
            RawImage raw = go.AddComponent<RawImage>();
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
        private void ReleaseCustomTextures()
        {
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
                }
            }
            for (int i = 0; i < customDecorationTextures.Count; i++)
                if (customDecorationTextures[i] != null) Destroy(customDecorationTextures[i]);
            customDecorationTextures.Clear();
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
                string rel = Path.Combine(Loader.ModPath, "CustomImages", path);
                if (File.Exists(rel)) return rel;
                return File.Exists(path) ? path : null;
            }
            catch (Exception)
            {
                return null;
            }
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
                if (m != null) key.text.fontMaterial = m;
            }
            if (key.value != null)
            {
                Material m = GetTextStyleMaterial(font, KvTextStyle.Resolve(Settings.Data, node, KvTextKind.Count));
                if (m != null) key.value.fontMaterial = m;
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
            key.text.color = pressed ? txtPressed : txt;
            if (key.value != null) key.value.color = key.text.color;
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
            key.text.color = pressed ? statTxtP : statTxt;
            if (key.value != null) key.value.color = key.text.color;
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
                key.value.text = FormatCount(node.Count);
        }

        internal static KeyCode CustomNodeKeyCode(FmNode node)
        {
            if (string.IsNullOrWhiteSpace(node.KeyBind)) return KeyCode.None;
            return Enum.TryParse(node.KeyBind, true, out KeyCode parsed) ? parsed : KeyCode.None;
        }

        // ======================== per-frame input / 逐帧输入 ========================

        private void ProcessCustomKeysInUpdate(long nowMs)
        {
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

                    // Ghost binding: same edge semantics as the fixed layouts' ghost keys. /
                    // 鬼键：与固定布局鬼键相同的边沿语义。
                    if (!string.IsNullOrWhiteSpace(node.GhostKey)
                        && Enum.TryParse(node.GhostKey, true, out KeyCode ghostCode)
                        && ghostCode != KeyCode.None)
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
                            NumBuffer.Format(kps, d.EnableCountFormatting, out var buf, out int off, out int len);
                            key.value.SetText(buf, off, len);
                        }
                    }
                }
                else
                {
                    // KPS/Total nodes only track press visuals (no counting of their own). /
                    // KPS/Total 节点只跟踪按压视觉（自身不计数）。
                    bool statPressed = !string.IsNullOrWhiteSpace(node.KeyBind)
                        && Enum.TryParse(node.KeyBind, true, out KeyCode statCode)
                        && statCode != KeyCode.None
                        && KeySource.GetKey(statCode);
                    if (statPressed != key.isPressed)
                    {
                        key.isPressed = statPressed;
                        ApplyCustomSpecialColors(key, node, statPressed);
                    }
                }
            }
            if (rainEnabled) rainSystem.UpdateEffects(Keys);
        }

        private void ApplyCustomKeyEdge(Key key, FmNode node, bool down, long timeMs, ProfileData d)
        {
            key.isPressed = down;
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
            // Image keys swap to the pressed texture first. /
            // 图片按键先切换按压贴图（按压语义）。
            if (key.CustomImage != null && key.CustomTexPressed != null)
            {
                key.CustomImage.texture = down ? key.CustomTexPressed : key.CustomTexNormal;
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
                PressTimes.Enqueue(timeMs);
            }
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
                    key.BounceBasePos = target.rectTransform.anchoredPosition;
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
                NumBuffer.Format(node.Count, d.EnableCountFormatting, out var buf, out int off, out int len);
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
                        key.value.text = FormatCount(node.Count);
                    }
                }
            }
        }
    }
}
