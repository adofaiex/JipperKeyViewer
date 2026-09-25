// FreeMake editor — an independent IMGUI window (its own MonoBehaviour OnGUI, a root-level
// GUI.Window that floats above the loader's settings panel). Canvas interactions: draw-order
// hit testing, click-picking that prefers the already-selected node in an overlap stack,
// double-click cycling through stacked hits, incremental drag deltas with a snap correction
// layered on top (the mouse never fights the snap), screen-edge/center snapping, "actually
// aligned" guide lines, and marquee selection with Shift-to-select locked background images.
// Undo is a whole-list JSON snapshot stack (EditorHistory).
// FreeMake 编辑器——独立 IMGUI 弹窗（挂在组件自己的 OnGUI 上，根级 GUI.Window，浮于加载器
// 设置面板之上）。画布交互：绘制序命中测试、重叠栈中优先保持已选中项、双击在堆叠命中间
// 循环拣选、累计增量式拖拽叠加吸附修正（鼠标不会与吸附打架）、屏幕边缘/中心吸附、"实际
// 对齐才显示"的对齐线、框选 + Shift 选锁定背景图。撤销为整表 JSON 快照栈（EditorHistory）。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Rendering;
using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Util;
using JipperKeyViewer.KeyViewer.Editor;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer
    {
        private const int FmWindowId = 7717;
        private const float FmDoubleClickTime = 0.35f;
        private const float FmDoubleClickDist = 8f;
        private const float FmMinWindowWidth = 720f;
        private const float FmMinWindowHeight = 520f;
        private const float FmMinimapWidth = 150f;

        private enum FmGesture { None, Pending, DragNodes, Marquee, Resize }

        private bool editorOpen;
        private Rect editorRect = new Rect(120f, 90f, 1100f, 720f);
        private bool editorNeedsCentre = true;
        private Vector2 fmScroll = Vector2.zero; // pan offset in screen px / 平移偏移（屏幕像素）
        private float fmZoom = 1f;
        private bool fmHasKeyFocus;
        private bool fmResizing;

        /// <summary>Editor selection: ordered list + membership set kept in sync. DrawEditorNode and
        /// DrawEditorMinimap ask "is this node selected?" once per node per frame, and
        /// List.Contains is O(n) — with everything selected on a 112-node document that is ~12k
        /// reference comparisons every frame just to pick highlight colours. The set makes it O(1)
        /// and, because every mutation goes through this type, the two can never disagree. /
        /// 编辑器选区：有序列表 + 同步的成员集合。DrawEditorNode 与 DrawEditorMinimap 每个节点
        /// 每帧都要问一次"是否选中"，而 List.Contains 是 O(n)——112 节点全选时每帧约 1.2 万次
        /// 引用比较，只为挑高亮颜色。集合让其变为 O(1)；所有修改都经由此类型，二者不会失步。</summary>
        private sealed class EditorSelectionList : IReadOnlyList<FmNode>
        {
            private readonly List<FmNode> order = new List<FmNode>();
            private readonly HashSet<FmNode> members = new HashSet<FmNode>();

            public int Count => order.Count;
            public FmNode this[int index] => order[index];

            public bool Contains(FmNode node) => node != null && members.Contains(node);

            public void Add(FmNode node)
            {
                if (node != null && members.Add(node)) order.Add(node);
            }

            public void AddRange(IEnumerable<FmNode> nodes)
            {
                if (nodes == null) return;
                foreach (FmNode n in nodes) Add(n);
            }

            public bool Remove(FmNode node)
            {
                if (node == null || !members.Remove(node)) return false;
                order.Remove(node);
                return true;
            }

            public void Clear()
            {
                order.Clear();
                members.Clear();
            }

            public List<FmNode> ToList() => new List<FmNode>(order);
            public IEnumerator<FmNode> GetEnumerator() => order.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => order.GetEnumerator();
        }

        private readonly EditorSelectionList editorSelection = new EditorSelectionList();
        private readonly List<FmNode> editorClipboard = new List<FmNode>();
        private int editorPasteSerial;
        private readonly EditorHistory editorHistory = new EditorHistory();

        private FmGesture fmGesture;
        private bool fmPointerDown;
        private Vector2 fmPressScreen;
        private Vector2 fmPressCanvas;
        private Vector2 fmMarqueeStart;
        private Vector2 fmMarqueeCur;
        private bool fmDragArmed;
        private bool fmDragMoved;
        private bool fmDragHistoryPushed;
        private bool fmAxisLocked;
        private bool fmLockToX;
        private float fmDragTotalX;
        private float fmDragTotalY;
        private readonly Dictionary<FmNode, Vector2> fmDragStart = new Dictionary<FmNode, Vector2>();
        private readonly List<FmNode> editorSelectionAtPress = new List<FmNode>();
        /// <summary>The ACTIVE node of the selection — the last one the user clicked. The
        /// property panel shows THIS node's values (falling back to the list head when the
        /// selection came from a marquee/select-all, which has no click order). /
        /// 选区的活动节点——用户最后点击的那个。属性面板显示它的值（框选/全选没有点击顺序，
        /// 回落到列表首项）。</summary>
        private FmNode fmActiveNode;
        private readonly List<FmNode> fmHitBuffer = new List<FmNode>();
        private float fmLastClickTime = -10f;
        private Vector2 fmLastClickPos = new Vector2(float.MinValue, float.MinValue);
        private FmNode fmCaptureNode;
        /// <summary>Node whose GHOST key is being captured (separate from the main binding
        /// capture). / 正在捕获鬼键的节点（与主键绑定捕获相互独立）。</summary>
        private FmNode fmCaptureGhostNode;
        private Vector2 fmPropsScroll;
        private GUIStyle fmNodeLabelStyle;
        private GUIStyle fmVideoGlyphStyle;
        /// <summary>Control id of the expanded per-node easing picker (null = none) / 展开的节点级缓动选择器的控件 id（null = 无）</summary>
        private string fmEasingPicker;
        private GUIStyle fmHintStyle;
        private readonly Dictionary<string, Texture2D> fmTexCache = new Dictionary<string, Texture2D>();
        /// <summary>Paths whose load already failed — kept apart from fmTexCache so a null result
        /// does not need to live in the texture map (see EditorNodeTexture). / 加载已失败的路径，
        /// 与 fmTexCache 分开存放，使 null 结果不必混在贴图表里（见 EditorNodeTexture）。</summary>
        private readonly HashSet<string> fmTexFailures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> fmScratchKeys = new List<string>();
        private readonly List<FmNode> fmOrderBuffer = new List<FmNode>();
        private bool fmGroupsExpanded;
        private string fmActiveGroupId = ""; // target group for new stat panels / 新建面板的目标组
        private Rect fmLastCanvasRect;
        private int fmResizeHandle = -1;
        private bool fmResizeMoved;
        private bool fmResizeHistoryPushed;
        private Rect fmResizeBBox;
        private readonly List<KeyValuePair<FmNode, Rect>> fmResizeOrig = new List<KeyValuePair<FmNode, Rect>>();
        private readonly List<float> fmSiblingW = new List<float>();
        private readonly List<float> fmSiblingH = new List<float>();

        private struct FmAlignLine
        {
            public bool Vertical;
            public float Coord, Min, Max;
        }

        private readonly List<FmAlignLine> fmAlignLines = new List<FmAlignLine>();

        /// <summary>Open the editor (called from the Layout tab) / 打开编辑器（布局页按钮调用）</summary>
        internal void OpenFreeMakeEditor()
        {
            editorOpen = true;
            editorNeedsCentre = true;
            // The first edit must be undoable back to the state the editor opened on. Without
            // this seeded baseline the timeline started empty, position stayed at -1 after the
            // first Push and CanUndo was false — the user's first structural edit could never
            // be undone, and the one after it walked straight past it. /
            // 首次编辑必须能撤回到打开编辑器时的状态。没有这条基线时时间线从空开始，首次 Push
            // 后 position 停在 -1、CanUndo 为 false——首个结构编辑永远撤不掉，紧随其后的那次
            // 则直接跨过它。
            SeedEditorHistoryBaseline();
        }

        /// <summary>Root-level window host: a plain MonoBehaviour OnGUI keeps the popup fully
        /// independent of the loader's own window nesting. / 根级窗口宿主：组件自身的 OnGUI 让
        /// 弹窗完全独立于加载器窗口的嵌套。</summary>
        private void OnGUI()
        {
            if (!editorOpen || Settings == null) return;
            Event e = Event.current;
            if (e != null && e.type == EventType.MouseDown)
                fmHasKeyFocus = editorRect.Contains(e.mousePosition);
            editorRect.width = Mathf.Clamp(editorRect.width, FmMinWindowWidth, Mathf.Max(FmMinWindowWidth, Screen.width));
            editorRect.height = Mathf.Clamp(editorRect.height, FmMinWindowHeight, Mathf.Max(FmMinWindowHeight, Screen.height));
            editorRect.x = Mathf.Clamp(editorRect.x, 0f, Mathf.Max(0f, Screen.width - editorRect.width));
            editorRect.y = Mathf.Clamp(editorRect.y, 0f, Mathf.Max(0f, Screen.height - editorRect.height));
            editorRect = GUI.Window(FmWindowId, editorRect, DrawFreeMakeWindow, "FreeMake — " + (Settings.CurrentProfile ?? ""));
        }

        private void DrawFreeMakeWindow(int id)
        {
            Event e = Event.current;
            if (!IsCustomLayout)
            {
                GUILayout.Label(I18n.Tr("fm_not_custom_hint"), GUILayout.MinWidth(320f));
                if (GUILayout.Button(I18n.Tr("fm_switch_custom"), GUILayout.Height(30f)))
                {
                    Settings.Data.KeyViewerStyle = KeyviewerStyle.Custom;
                    ChangeKeyViewer();
                    SaveSettingsFromGui();
                    editorNeedsCentre = true;
                }
                GUI.DragWindow(new Rect(0, 0, 10000f, 24f));
                return;
            }

            DrawEditorToolbar();

            Rect canvasRect = GUILayoutUtility.GetRect(1f, 1f, GUILayout.MinHeight(160f), GUILayout.ExpandHeight(true));
            HandleEditorCanvas(canvasRect, e);

            // Reset the shared colour-picker/slider sequence counters here too. Without it the
            // editor's generated control names (fme_cpi_N) drifted on every pass, so a focused
            // field's identity changed frame to frame and typing was lost.
            // 同样重置共用序号计数器：否则编辑器的生成控件名（fme_cpi_N）每帧都在变，焦点字段
            // 的身份逐帧漂移，输入的字符会丢失。
            colorPickerFieldSeq = 0;
            sliderFieldSeq = 0;
            BeginTextInputPass();
            fmPropsScroll = GUILayout.BeginScrollView(fmPropsScroll, GUILayout.Height(280f));
            DrawEditorProperties();
            GUILayout.EndScrollView();
            EndTextInputPass();

            HandleEditorResize(e);
            GUI.DragWindow(new Rect(0, 0, 10000f, 24f));
            EditorGcBuffers();
        }

        // ======================== toolbar / 工具栏 ========================

        private void DrawEditorToolbar()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(I18n.Tr("fm_add_key"), GUILayout.MinWidth(48f))) EditorAddNode(0);
            if (GUILayout.Button(I18n.Tr("fm_add_kps"), GUILayout.MinWidth(42f))) EditorAddNode(1);
            if (GUILayout.Button(I18n.Tr("fm_add_total"), GUILayout.MinWidth(48f))) EditorAddNode(2);
            if (GUILayout.Button(I18n.Tr("fm_add_image"), GUILayout.MinWidth(48f))) EditorAddNode(3);
            if (GUILayout.Button(I18n.Tr("fm_add_video"), GUILayout.MinWidth(48f))) EditorAddNode(3, focusVideoPath: true);
            GUILayout.Space(6f);
            if (GUILayout.Button(I18n.Tr("fm_copy"), GUILayout.MinWidth(42f))) EditorCopySelection();
            if (GUILayout.Button(I18n.Tr("fm_paste"), GUILayout.MinWidth(42f))) EditorPaste();
            if (GUILayout.Button(I18n.Tr("fm_delete"), GUILayout.MinWidth(42f))) EditorDeleteSelection();
            GUILayout.Space(6f);
            GUI.enabled = editorHistory.CanUndo;
            if (GUILayout.Button(I18n.Tr("fm_undo"), GUILayout.MinWidth(42f))) EditorUndo();
            GUI.enabled = editorHistory.CanRedo;
            if (GUILayout.Button(I18n.Tr("fm_redo"), GUILayout.MinWidth(42f))) EditorRedo();
            GUI.enabled = true;
            GUILayout.Space(6f);
            if (GUILayout.Button(I18n.Tr("fm_select_all"), GUILayout.MinWidth(42f))) EditorSelectAll();
            if (GUILayout.Button(I18n.Tr("fm_clear_sel"), GUILayout.MinWidth(42f))) editorSelection.Clear();
            GUILayout.Space(6f);
            // Built-in layout presets (the fixed layouts' hardcoded arrangements, generated as
            // editable nodes) + canvas wipe. Both push history — Ctrl+Z restores. /
            // 内置布局预设（固定布局的硬编码排布生成为可编辑节点）与清空画布。两者都入撤销栈
            // ——Ctrl+Z 可恢复。
            if (GUILayout.Button(I18n.Tr("fm_presets"), GUILayout.MinWidth(42f))) fmPresetStripOpen = !fmPresetStripOpen;
            if (GUILayout.Button(I18n.Tr("fm_wipe"), GUILayout.MinWidth(42f))) EditorWipeCanvas();
            if (GUILayout.Button(I18n.Tr("fm_arrange"), GUILayout.MinWidth(42f))) fmArrangeStripOpen = !fmArrangeStripOpen;
            GUILayout.FlexibleSpace();
            GUILayout.Label(string.Format(I18n.Tr("fm_status"), Settings.Data.CustomNodes.Count, editorSelection.Count),
                GUILayout.Width(120f));
            if (GUILayout.Button(I18n.Tr("fm_close"), GUILayout.MinWidth(42f)))
            {
                // Closing the editor flushes any pending debounced changes and cancels any
                // in-progress capture/drag so stale node references cannot eat the next keypress.
                // 关闭编辑器时冲刷挂起的去抖变更，并取消进行中的捕获/拖拽，避免旧节点引用吞掉下一次按键。
                editorOpen = false;
                ClearEditorInteractionState();
                SaveSettings();
            }
            GUILayout.EndHorizontal();
            // Preset strip: one click applies a built-in layout as editable nodes. The toggle
            // picks the target — a NEW profile (default, non-destructive) or the CURRENT
            // canvas (undoable replace). / 预设条：一键把内置布局生成为可编辑节点。开关选择
            // 目标——新建配置（默认，非破坏）或当前配置画布（可撤销的替换）。
            if (fmPresetStripOpen)
            {
                GUILayout.BeginHorizontal();
                fmPresetNewProfile = GUILayout.Toggle(fmPresetNewProfile, I18n.Tr("fm_preset_new_profile"), GUILayout.MinWidth(120f));
                string[] names = new string[KeyLayoutNames.Length - 1]; // skip only Custom / 仅跳过「自定义」
                Array.Copy(KeyLayoutNames, names, names.Length);
                int picked = GUILayout.SelectionGrid(-1, names, names.Length, GUILayout.MinHeight(22f));
                GUILayout.EndHorizontal();
                if (picked >= 0)
                {
                    fmPresetStripOpen = false;
                    EditorApplyPreset(picked, fmPresetNewProfile);
                }
            }
            // Arrange strip: align/distribute the selection and stamp array copies. /
            // 排列条：对选区对齐/等距分布，以及阵列复制。
            if (fmArrangeStripOpen)
            {
                GUILayout.BeginHorizontal();
                GUI.enabled = editorSelection.Count >= 2;
                if (GUILayout.Button(I18n.Tr("fm_align_left"), GUILayout.MinWidth(34f))) EditorAlignSelection(0);
                if (GUILayout.Button(I18n.Tr("fm_align_hcenter"), GUILayout.MinWidth(34f))) EditorAlignSelection(1);
                if (GUILayout.Button(I18n.Tr("fm_align_right"), GUILayout.MinWidth(34f))) EditorAlignSelection(2);
                if (GUILayout.Button(I18n.Tr("fm_align_top"), GUILayout.MinWidth(34f))) EditorAlignSelection(3);
                if (GUILayout.Button(I18n.Tr("fm_align_vcenter"), GUILayout.MinWidth(34f))) EditorAlignSelection(4);
                if (GUILayout.Button(I18n.Tr("fm_align_bottom"), GUILayout.MinWidth(34f))) EditorAlignSelection(5);
                GUI.enabled = editorSelection.Count >= 3;
                if (GUILayout.Button(I18n.Tr("fm_dist_h"), GUILayout.MinWidth(34f))) EditorDistributeSelection(true);
                if (GUILayout.Button(I18n.Tr("fm_dist_v"), GUILayout.MinWidth(34f))) EditorDistributeSelection(false);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                GUILayout.Label(I18n.Tr("fm_array_count"), GUILayout.Width(40f));
                fmArrayCountText = TextInputField("fme_arr_n", fmArrayCountText, GUILayout.Width(34f));
                GUILayout.Label(I18n.Tr("fm_array_spacing"), GUILayout.Width(40f));
                fmArraySpacingText = TextInputField("fme_arr_s", fmArraySpacingText, GUILayout.Width(44f));
                int arrayN = 0; float arrayGap = 0f;
                bool arrayOk = int.TryParse(fmArrayCountText.Trim(), out arrayN) && arrayN >= 1 && arrayN <= 200
                    && float.TryParse(fmArraySpacingText.Replace("—", "").Trim(), out arrayGap);
                GUI.enabled = arrayOk && editorSelection.Count > 0;
                if (GUILayout.Button(I18n.Tr("fm_array_h"), GUILayout.MinWidth(64f))) EditorArraySelection(true, arrayN, arrayGap);
                if (GUILayout.Button(I18n.Tr("fm_array_v"), GUILayout.MinWidth(64f))) EditorArraySelection(false, arrayN, arrayGap);
                GUI.enabled = true;
                GUILayout.EndHorizontal();
            }
        }

        private bool fmPresetStripOpen;
        private bool fmPresetNewProfile = true;
        private bool fmArrangeStripOpen;
        private string fmArrayCountText = "4";
        private string fmArraySpacingText = "20";

        /// <summary>Apply a built-in layout preset. `newProfile` (default) lands it in a NEW
        /// profile cloned from the current one — the current layout untouched; otherwise it
        /// REPLACES the current canvas in place (undoable, one Ctrl+Z). /
        /// 应用内置布局预设。`newProfile`（默认）落进克隆自当前配置的新建 Profile——当前布局
        /// 原封不动；否则就地替换当前画布（可撤销，一步 Ctrl+Z 找回）。</summary>
        private void EditorApplyPreset(int styleIndex, bool newProfile = true)
        {
            if (styleIndex < 0 || styleIndex >= 8) return; // 0-7 = 12K/16K/20K/10K/8K/14K/24K/108K
            KeyviewerStyle style = (KeyviewerStyle)styleIndex;
            List<FmNode> nodes = BuildPresetNodes(style, out int nextId);
            if (!newProfile)
            {
                // ADD into the current canvas — existing nodes stay untouched (a replace here
                // wiped the user's layout once). The batch lands in its OWN new layer group FIRST —
                // per-group stat uniqueness then keeps the preset's KPS/Total even when other
                // groups already carry panels. Shift out of overlap when needed. /
                // 追加进当前画布——现有节点原封不动（此前误做成整画布替换）。批次先落入自己的
                // 新建图层组——按组的面板唯一性因此能保住预设的 KPS/Total（即使其它组已有
                // 面板）。与现有节点重叠时整体挪到空白处。
                string presetGroupId = "g" + Settings.Data.LayerGroupNextId++;
                Settings.Data.LayerGroups.Add(new FmLayerGroup
                {
                    Id = presetGroupId,
                    Name = KeyLayoutNames[styleIndex] + "-" + I18n.Tr("fm_presets"),
                    Visible = true,
                });
                foreach (FmNode n in nodes) n.GroupId = presetGroupId;
                List<FmNode> currentNodes = Settings.Data.CustomNodes;
                List<FmNode> add = nodes
                    .Where(n => !(n.NodeType == 1 && GroupHasStat(n.GroupId, 1))
                             && !(n.NodeType == 2 && GroupHasStat(n.GroupId, 2)))
                    .ToList();
                if (add.Count > 0 && currentNodes.Count > 0)
                {
                    float exMinX = currentNodes.Min(n => n.X), exMaxX = currentNodes.Max(n => n.X + n.Width);
                    float exMinY = currentNodes.Min(n => n.Y), exMaxY = currentNodes.Max(n => n.Y + n.Height);
                    float nxMin = add.Min(n => n.X), nxMax = add.Max(n => n.X + n.Width);
                    float nyMin = add.Min(n => n.Y), nyMax = add.Max(n => n.Y + n.Height);
                    bool overlap = nxMin < exMaxX && nxMax > exMinX && nyMin < exMaxY && nyMax > exMinY;
                    if (overlap)
                    {
                        // Prefer the free space to the right; fall below when it would run past
                        // the canvas reference width. / 优先放到现有内容右侧；超出画布参考宽度
                        // 则放到下方。
                        float dx = exMaxX + 20f - nxMin;
                        if (nxMax + dx > 1920f)
                        {
                            float dy = exMaxY + 20f - nyMin;
                            foreach (FmNode n in add) n.Y += dy;
                        }
                        else
                        {
                            foreach (FmNode n in add) n.X += dx;
                        }
                    }
                }
                Settings.Data.CustomNodeNextId = nextId;
                Settings.Data.CustomNodes.AddRange(add);
                EnsureCustomNodes();
                editorSelection.Clear();
                editorSelection.AddRange(add);
                PushEditorHistory(false);
                EditorMutated();
                return;
            }
            // Flush the CURRENT layout to its file before switching. / 切换前先把当前布局落盘。
            SaveCurrentProfile();
            // Free profile name: "16K-预设", "16K-预设 2", ... / 空闲配置名。
            string baseName = KeyLayoutNames[styleIndex] + "-" + I18n.Tr("fm_presets");
            var existing = new HashSet<string>(Settings.ProfileNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            // ALSO check the disk: ProfileNames is only refreshed when the list is expanded, so a
            // file copied into Profiles/ by hand (or any orphan left behind) is not in the set —
            // and the preset would silently overwrite it. .jkv import and Save-As already do this.
            // 同时检查磁盘：ProfileNames 只在展开列表时同步，用户手动拷进 Profiles/ 的文件（或
            // 任何孤儿文件）都不在集合里，预设会直接覆盖它。.jkv 导入与另存为都已这样做。
            string name = baseName;
            for (int k = 2; existing.Contains(name) || File.Exists(GetProfilePath(name)); k++) name = baseName + " " + k;
            // Clone the whole ProfileData so global settings/colors/fonts carry over, then swap in
            // the preset nodes. / 整体克隆 ProfileData 以携带全局设置/配色/字体，再换入预设节点。
            ProfileData pd = JsonConvert.DeserializeObject<ProfileData>(
                JsonConvert.SerializeObject(Settings.Data, ProfileData.ProfileSerializer),
                ProfileData.ProfileSerializer);
            if (pd == null) return;
            // The preset batch lands in its own layer group here too. /
            // 新建配置路径同样让预设批次落入自己的图层组。
            string newGroupId = "g" + pd.LayerGroupNextId++;
            pd.LayerGroups.Add(new FmLayerGroup { Id = newGroupId, Name = KeyLayoutNames[styleIndex] + "-" + I18n.Tr("fm_presets"), Visible = true });
            foreach (FmNode n in nodes) n.GroupId = newGroupId;
            pd.CustomNodes = nodes;
            pd.CustomNodeNextId = nextId;
            Settings.CurrentProfile = name;
            var list = new List<string>(Settings.ProfileNames ?? Array.Empty<string>()) { name };
            Settings.ProfileNames = list.ToArray();
            Settings.Data = pd;
            EnsureCustomNodes();
            // The document identity changed along with the profile. Reuse the same full
            // transient reset as SwitchProfile so stale selections, clipboard entries, capture
            // state, gestures, and ghost-key edges cannot cross into the preset profile.
            // 文档身份已随 Profile 更换；复用与 SwitchProfile 相同的瞬态清理，避免旧选区、
            // 剪贴板、捕获、手势和鬼键边沿状态进入预设 Profile。
            ResetEditorHistoryForProfileSwitch();
            EditorMutated();
        }

        private List<FmNode> BuildPresetNodes(KeyviewerStyle style, out int nextId)
        {
            List<FmNode> nodes = new List<FmNode>();
            LayoutDesc layout = GetLayout(style);
            // Must mirror GetKeyCode's mapping — a missing case (Key16 was) silently fell to the
            // key24 default and preset nodes got the wrong profile's bindings. Texts likewise. /
            // 必须与 GetKeyCode 的映射一致——漏掉某个 case（此前漏了 Key16）会静默落到默认
            // 分支，预设节点就带上了错误的绑定数组。文本同理。
            string[] texts;
            KeyCode[] binds;
            switch (style)
            {
                case KeyviewerStyle.Key8: binds = Settings.Data.key8; texts = Settings.Data.key8Text; break;
                case KeyviewerStyle.Key10: binds = Settings.Data.key10; texts = Settings.Data.key10Text; break;
                case KeyviewerStyle.Key12: binds = Settings.Data.key12; texts = Settings.Data.key12Text; break;
                case KeyviewerStyle.Key14: binds = Settings.Data.key14; texts = Settings.Data.key14Text; break;
                case KeyviewerStyle.Key16: binds = Settings.Data.key16; texts = Settings.Data.key16Text; break;
                case KeyviewerStyle.Key20: binds = Settings.Data.key20; texts = Settings.Data.key20Text; break;
                case KeyviewerStyle.Key24: binds = Settings.Data.key24; texts = Settings.Data.key24Text; break;
                case KeyviewerStyle.Full108: binds = Settings.Data.key108; texts = null; break; // no per-key texts on 108K / 108K 无每键文本
                default: binds = Settings.Data.key16; texts = Settings.Data.key16Text; break;
            }
            int id = Settings.Data.CustomNodeNextId;
            FmNode NewNode(int type, float x, float centerY, float w, float h) => new FmNode
            {
                NodeType = type,
                Id = id++,
                X = x,
                Y = 1080f - centerY - h * 0.5f,
                Width = w,
                Height = h,
            };
            int[] counts = Settings.Data.Count;
            // 108K full keyboard: the shared slot table + the same 56px column-step/6px gap
            // math the fixed layout uses; tall keys (+/Enter) span two rows. The fixed keyboard
            // never rains, so preset nodes start with rain off. / 108K 全键盘：共享槽位表 +
            // 与固定布局相同的 56px 列步进/6px 间隙换算；竖长键（+/回车）跨两行。固定全键盘
            // 从不下雨，预设节点雨滴默认关闭。
            if (style == KeyviewerStyle.Full108)
            {
                const float U = 50f;
                const float colStep = 56f;
                const float gap = 6f;
                const float rightClusterShift = 4f * colStep;
                const float rowStep = 56f;
                foreach (var s in Full108SlotTable())
                {
                    float x = (float)s.x * colStep / U - (s.idx >= 78 ? rightClusterShift : 0f);
                    float w, h, cy;
                    if (s.idx == 95 || s.idx == 104)
                    {
                        w = colStep - gap;
                        h = rowStep + 50f;
                        cy = (s.idx == 95 ? (468f + 412f) : (300f + 356f)) * 0.5f;
                    }
                    else
                    {
                        w = (float)s.w * colStep / U - gap;
                        h = 50f;
                        cy = (float)s.y;
                    }
                    FmNode n = NewNode(0, x, cy, w, h);
                    KeyCode kc = s.idx < binds.Length ? binds[s.idx] : KeyCode.None;
                    if (kc != KeyCode.None) n.KeyBind = kc.ToString();
                    if (s.idx < counts.Length) n.Count = counts[s.idx];
                    nodes.Add(n);
                }
                if (Settings.Data.FullKeyboardShowKpsTotal)
                {
                    float ktW = Settings.Data.FullKeyboardKpsTotalSize;
                    FmNode kps = NewNode(1, Settings.Data.FullKpsPosition.x * CanvasWidth,
                        (1f - Settings.Data.FullKpsPosition.y) * 1080f, ktW, 30f);
                    kps.CustomText = Settings.Data.KpsLabel;
                    nodes.Add(kps);
                    FmNode total = NewNode(2, Settings.Data.FullTotalPosition.x * CanvasWidth,
                        (1f - Settings.Data.FullTotalPosition.y) * 1080f, ktW, 30f);
                    total.CustomText = Settings.Data.TotalLabel;
                    nodes.Add(total);
                }
                nextId = id;
                return nodes;
            }
            // Rain start offsets: reproduce the fixed layout's launch line. The fixed rows tune
            // each row's start-Y so every row's trail head starts at the SAME height; custom
            // nodes always launch from node-top+2, which only matches row 1 by numbers. Seed
            // offY = rowStartY + 273 - height so preset rows 2/3 launch from the same line as
            // the fixed layout does (front row computes to 0 with the stock -223). /
            // 雨滴起始偏移：复刻固定布局的发射线。固定布局各排起始高度经过调校，各排雨滴头从
            // 同一高度起跳；自定义节点恒从节点顶边+2起跳，数值上只与第 1 排吻合。按
            // offY = 排起始Y + 273 - 节点高 播种，让预设的第 2/3 排也跳到同一条线（前排按
            // 出厂 -223 计算恰好为 0）。
            float RowStartY(int rainRow) => rainRow <= 0 ? Settings.Data.RainStartYRow1
                : rainRow == 1 ? Settings.Data.RainStartYRow2 : Settings.Data.RainStartYRow3;
            // Front row: 8 keys at the fixed layout's front-Y. Counts and custom texts carry over
            // from the source profile's per-slot arrays. / 前排：固定布局前排 Y 上的 8 键。
            // 计数与自定义文本按槽位从源配置的数组继承。
            for (int i = 0; i < 8 && i < binds.Length; i++)
            {
                FmNode n = NewNode(0, 54f * i, layout.frontY, 50f, 50f);
                if (binds[i] != KeyCode.None) n.KeyBind = binds[i].ToString();
                if (i < counts.Length) n.Count = counts[i];
                if (texts != null && i < texts.Length) n.CustomText = texts[i] ?? "";
                n.RainEnabled = true;
                n.RainOffsetY = RowStartY(0) + 273f - 50f;
                nodes.Add(n);
            }
            // Back extras + KPS/Total from the same LayoutDesc the fixed layout uses. /
            // 后排扩展与 KPS/Total 来自固定布局用的同一 LayoutDesc。
            if (layout.extras != null)
            {
                foreach (var e in layout.extras)
                {
                    if (e.index == -1 || e.index == -2)
                    {
                        FmNode n = NewNode(e.index == -1 ? 1 : 2, e.x, e.y, e.w, e.slim ? 30f : 50f);
                        n.CustomText = e.index == -1 ? Settings.Data.KpsLabel : Settings.Data.TotalLabel;
                        nodes.Add(n);
                    }
                    else if (e.index >= 0 && e.index < binds.Length)
                    {
                        FmNode n = NewNode(0, e.x, e.y, e.w, 50f);
                        if (binds[e.index] != KeyCode.None) n.KeyBind = binds[e.index].ToString();
                        if (e.index < counts.Length) n.Count = counts[e.index];
                        if (texts != null && e.index < texts.Length) n.CustomText = texts[e.index] ?? "";
                        n.RainEnabled = true;
                        n.RainRow = Mathf.Clamp(e.rainRow, 0, 2);
                        n.RainOffsetY = RowStartY(n.RainRow) + 273f - 50f;
                        nodes.Add(n);
                    }
                }
            }
            // Foot keys — same geometry as the fixed foot layout, when one is set. Counts/texts
            // map to the FootKeyBase-anchored slots. / 脚键——设置了脚键布局时，按固定脚键的
            // 几何生成。计数/文本按 FootKeyBase 起的槽位映射。
            int footSize = FootKeySize(Settings.Data.FootKeyViewerStyle);
            if (footSize > 0)
            {
                KeyCode[] footBinds = GetFootKeyCode();
                string[] footTexts = Settings.Data.FootKeyViewerStyle switch
                {
                    FootKeyviewerStyle.Key2 => Settings.Data.footkey2Text,
                    FootKeyviewerStyle.Key4 => Settings.Data.footkey4Text,
                    FootKeyviewerStyle.Key6 => Settings.Data.footkey6Text,
                    FootKeyviewerStyle.Key8 => Settings.Data.footkey8Text,
                    FootKeyviewerStyle.Key10 => Settings.Data.footkey10Text,
                    FootKeyviewerStyle.Key12 => Settings.Data.footkey12Text,
                    FootKeyviewerStyle.Key14 => Settings.Data.footkey14Text,
                    FootKeyviewerStyle.Key16 => Settings.Data.footkey16Text,
                    _ => null,
                };
                for (int i = 0; i < footSize && i < footBinds.Length; i++)
                {
                    int col = footSize <= 8 || i < 8 ? i : i - 8;
                    int row = footSize <= 8 || i < 8 ? 0 : 1;
                    int baseY = footSize > 8 ? 15 + 34 : 15;
                    int x = 432 + col * 34;
                    if (footSize > 8 && row == 1) x += (8 - (footSize - 8)) * 17;
                    FmNode n = NewNode(0, x, baseY - row * 34, 30f, 30f);
                    if (footBinds[i] != KeyCode.None) n.KeyBind = footBinds[i].ToString();
                    n.HideCount = true; // foot keys hide counts by default (fixed-layout look) / 脚键默认隐藏计数（固定布局同款）
                    int slot = FootKeyBase + i;
                    if (slot < counts.Length) n.Count = counts[slot];
                    if (footTexts != null && i < footTexts.Length) n.CustomText = footTexts[i] ?? "";
                    nodes.Add(n);
                }
            }
            nextId = id;
            return nodes;
        }

        /// <summary>Clear the whole canvas (undoable). / 清空整个画布（可撤销）。</summary>
        private void EditorWipeCanvas()
        {
            if (Settings.Data.CustomNodes.Count == 0) return;
            foreach (FmNode node in Settings.Data.CustomNodes)
            {
                ClearCaptureForNode(node);
            }
            Settings.Data.CustomNodes = new List<FmNode>();
            EnsureCustomNodes(); // wipe every group too — no members survive / 连组一并清——无成员存活
            editorSelection.Clear();
            PushEditorHistory(false);
            EditorMutated();
        }

        /// <summary>Seed a node's per-node rain shadow from its selected rain row's settings. /
        /// 用节点所选雨滴排的设置为其节点级雨滴阴影做种子。</summary>
        private static void SeedRainShadowFromRow(FmNode n)
        {
            ProfileData d = Settings.Data;
            int row = Mathf.Clamp(n.RainRow, 0, 2) + 1; // 1..3
            n.RainShadowEnabled = row == 1 ? d.EnableRainShadowRow1 : row == 2 ? d.EnableRainShadowRow2 : d.EnableRainShadowRow3;
            Color c = row == 1 ? d.RainShadowColorRow1 : row == 2 ? d.RainShadowColorRow2 : d.RainShadowColorRow3;
            n.RainShadowColor = new[] { c.r, c.g, c.b, c.a };
            n.RainShadowOffsetX = row == 1 ? d.RainShadowOffsetXRow1 : row == 2 ? d.RainShadowOffsetXRow2 : d.RainShadowOffsetXRow3;
            n.RainShadowOffsetY = row == 1 ? d.RainShadowOffsetYRow1 : row == 2 ? d.RainShadowOffsetYRow2 : d.RainShadowOffsetYRow3;
        }

        /// <summary>Seed a node's per-node rain outline from its selected rain row's settings. /
        /// 用节点所选雨滴排的设置为其节点级雨滴描边做种子。</summary>
        private static void SeedRainOutlineFromRow(FmNode n)
        {
            ProfileData d = Settings.Data;
            int row = Mathf.Clamp(n.RainRow, 0, 2) + 1; // 1..3
            n.RainOutlineEnabled = row == 1 ? d.EnableRainOutlineRow1 : row == 2 ? d.EnableRainOutlineRow2 : d.EnableRainOutlineRow3;
            Color c = row == 1 ? d.RainOutlineColorRow1 : row == 2 ? d.RainOutlineColorRow2 : d.RainOutlineColorRow3;
            n.RainOutlineColor = new[] { c.r, c.g, c.b, c.a };
            n.RainOutlineWidth = row == 1 ? d.RainOutlineWidthRow1 : row == 2 ? d.RainOutlineWidthRow2 : d.RainOutlineWidthRow3;
        }

        /// <summary>Seed a node's per-node GHOST rain shadow from its selected ghost rain row. /
        /// 用节点所选排的鬼雨阴影设置为其节点级鬼雨阴影做种子。</summary>
        private static void SeedGhostRainShadowFromRow(FmNode n)
        {
            ProfileData d = Settings.Data;
            int row = Mathf.Clamp(n.RainRow, 0, 2) + 1; // 1..3
            n.GhostRainShadowEnabled = row == 1 ? d.EnableGhostRainShadowRow1 : row == 2 ? d.EnableGhostRainShadowRow2 : d.EnableGhostRainShadowRow3;
            Color c = row == 1 ? d.GhostRainShadowColorRow1 : row == 2 ? d.GhostRainShadowColorRow2 : d.GhostRainShadowColorRow3;
            n.GhostRainShadowColor = new[] { c.r, c.g, c.b, c.a };
            n.GhostRainShadowOffsetX = row == 1 ? d.GhostRainShadowOffsetXRow1 : row == 2 ? d.GhostRainShadowOffsetXRow2 : d.GhostRainShadowOffsetXRow3;
            n.GhostRainShadowOffsetY = row == 1 ? d.GhostRainShadowOffsetYRow1 : row == 2 ? d.GhostRainShadowOffsetYRow2 : d.GhostRainShadowOffsetYRow3;
        }

        /// <summary>Seed a node's per-node GHOST rain outline from its selected ghost rain row. /
        /// 用节点所选排的鬼雨描边设置为其节点级鬼雨描边做种子。</summary>
        private static void SeedGhostRainOutlineFromRow(FmNode n)
        {
            ProfileData d = Settings.Data;
            int row = Mathf.Clamp(n.RainRow, 0, 2) + 1; // 1..3
            n.GhostRainOutlineEnabled = row == 1 ? d.EnableGhostRainOutlineRow1 : row == 2 ? d.EnableGhostRainOutlineRow2 : d.EnableGhostRainOutlineRow3;
            Color c = row == 1 ? d.GhostRainOutlineColorRow1 : row == 2 ? d.GhostRainOutlineColorRow2 : d.GhostRainOutlineColorRow3;
            n.GhostRainOutlineColor = new[] { c.r, c.g, c.b, c.a };
            n.GhostRainOutlineWidth = row == 1 ? d.GhostRainOutlineWidthRow1 : row == 2 ? d.GhostRainOutlineWidthRow2 : d.GhostRainOutlineWidthRow3;
        }

        /// <summary>Seed a node's ghost rain shape/offset params from the current effective
        /// values (node normal-rain overrides when set, else the ghost row; offsets from the
        /// node's normal rain offsets). / 用当前生效值为鬼雨形状/偏移参数做种子（节点普通雨覆
        /// 盖优先，否则鬼雨排；偏移取节点普通雨偏移）。</summary>
        private static void SeedGhostRainParams(FmNode n)
        {
            ProfileData d = Settings.Data;
            int row = Mathf.Clamp(n.RainRow, 0, 2) + 1; // 1..3
            n.GhostRainWidth = n.RainWidth > 0f ? n.RainWidth
                : row == 1 ? d.GhostRainWidthRow1 : row == 2 ? d.GhostRainWidthRow2 : d.GhostRainWidthRow3;
            n.GhostRainHeight = n.RainHeight > 0f ? n.RainHeight
                : row == 1 ? d.GhostRainHeightRow1 : row == 2 ? d.GhostRainHeightRow2 : d.GhostRainHeightRow3;
            n.GhostRainSpeed = n.RainSpeed > 0f ? n.RainSpeed
                : row == 1 ? d.GhostRainSpeedRow1 : row == 2 ? d.GhostRainSpeedRow2 : d.GhostRainSpeedRow3;
            n.GhostRainOffsetX = n.RainOffsetX;
            n.GhostRainOffsetY = n.RainOffsetY;
        }

        /// <summary>Seed a node's trail-top fade + release fade from the current global toggles. /
        /// 用当前全局设置为节点的顶部渐隐与松开淡出做种子。</summary>
        private static void SeedRainFadeFromGlobals(FmNode n)
        {
            ProfileData d = Settings.Data;
            n.TrailFadeEnabled = d.EnableRainGradient;
            n.TrailFadePx = d.RainFadePx;
            n.ReleaseFadeEnabled = d.EnableRainFade;
            n.ReleaseFadeDuration = d.RainFadeDuration;
        }

        /// <summary>Return true if the target group already carries a stat node of the given type.
        /// When groupId is null/empty, fall back to the legacy "single KPS / single Total" rule
        /// so ungrouped mode is unchanged. / 若目标 group 已携带同类型面板返 true；groupId 为
        /// 空时退回旧版"全图 1 个 KPS / 1 个 Total"规则。</summary>
        private static bool GroupHasStat(string groupId, int statType)
        {
            if (string.IsNullOrEmpty(groupId))
                return Settings.Data.CustomNodes.Any(n => n != null && n.NodeType == statType);
            return Settings.Data.CustomNodes.Any(n => n != null
                && n.NodeType == statType && n.GroupId == groupId);
        }

        /// <param name="focusVideoPath">After creating an image node, focus the video-path field so
        /// the user can type the file straight away. The toolbar's "video" button used to call this
        /// with the exact same arguments as the "image" button, so it produced an unbound image
        /// node with neither ImagePath nor VideoPath — a grey placeholder that still consumed one
        /// of the group's 8 unbound-image slots. / 创建图片节点后聚焦视频路径输入框。工具栏的
        /// 「视频」按钮此前与「图片」按钮参数完全相同，产出的是既无 ImagePath 也无 VideoPath
        /// 的未绑定节点——一个灰色占位框，还白占该组 8 个未绑定图片名额之一。</param>
        private void EditorAddNode(int type, bool focusVideoPath = false)
        {
            string targetGroup = fmActiveGroupId;
            if ((type == 1 || type == 2) && GroupHasStat(targetGroup, type)) return;
            if (type != 3 && KeyLikeCountInGroup(targetGroup) >= CustomKeyNodeCap) return;
            if (type == 3 && UnboundImageCountInGroup(targetGroup) >= 8) return;
            Vector2 center = EditorViewCenter();
            FmNode node = new FmNode
            {
                NodeType = type,
                Id = Settings.Data.CustomNodeNextId++,
                X = center.x - 30f,
                Y = center.y - 30f,
                Width = 60f,
                Height = 60f,
                Depth = Settings.Data.CustomNodes.Count > 0 ? Settings.Data.CustomNodes.Max(n => n.Depth) + 1 : 0,
            };
            if (type == 1) node.CustomText = Settings.Data.KpsLabel;
            if (type == 2) node.CustomText = Settings.Data.TotalLabel;
            if (!string.IsNullOrEmpty(targetGroup)) node.GroupId = targetGroup;
            Settings.Data.CustomNodes.Add(node);
            editorSelection.Clear();
            editorSelection.Add(node);
            // Keep the new node the ACTIVE one so the panel (which shows the active node) and the
            // focus target are the one just created. / 保持新节点为活动节点，使面板显示与聚焦
            // 目标都是刚创建的这个。
            fmActiveNode = node;
            if (focusVideoPath) fmFocusVideoPathNextPass = true;
            PushEditorHistory(false);
            EditorMutated();
        }

        private void EditorDeleteSelection()
        {
            if (editorSelection.Count == 0) return;
            for (int i = editorSelection.Count - 1; i >= 0; i--)
            {
                FmNode removed = editorSelection[i];
                ClearCaptureForNode(removed);
                Settings.Data.CustomNodes.Remove(removed);
            }
            editorSelection.Clear();
            // Prune groups that just lost their last member. /
            // 剔除刚刚失去全部成员的组。
            EnsureCustomNodes();
            PushEditorHistory(false);
            EditorMutated();
        }

        private void EditorCopySelection()
        {
            editorClipboard.Clear();
            editorPasteSerial = 0;
            foreach (FmNode node in editorSelection)
            {
                if (node == null) continue;
                FmNode copy = node.Clone();
                // The clipboard holds a TEMPLATE, not a record of what the user did. Clone() copies
                // the live press count, so every paste added the SOURCE node's count to the
                // document again — duplicating a session's totals, and a second paste duplicated
                // them once more. A pasted node is a fresh node: its count starts at zero.
                // 剪贴板持有的是**模板**，不是用户做过什么的记录。Clone() 会复制实时按压计数，于是
                // 每次粘贴都把**源**节点的计数再加进文档一遍——把一次游玩的总数翻倍，第二次粘贴
                // 再翻一倍。粘贴出的节点是全新节点：计数从零开始。
                copy.Count = 0;
                editorClipboard.Add(copy);
            }
        }

        private void EditorPaste()
        {
            if (editorClipboard.Count == 0) return;
            editorPasteSerial++;
            float offset = 20f * editorPasteSerial;
            // Paste honors the node caps — the data list used to grow past them and
            // EnsureCustomNodes silently trimmed the excess on the next load. /
            // 粘贴遵守节点上限——此前数据列表可超限，下次加载时被静默裁剪。
            List<FmNode> pasted = new List<FmNode>();
            foreach (FmNode template in editorClipboard)
            {
                if (template == null) continue;
                if (template.NodeType == 1 && GroupHasStat(template.GroupId, 1)) continue;
                if (template.NodeType == 2 && GroupHasStat(template.GroupId, 2)) continue;
                bool keyLike = template.NodeType != 3 || !string.IsNullOrWhiteSpace(template.KeyBind);
                // Per-group budgets, recomputed per template (its group may differ). /
                // 按组预算，逐模板重算（各组不同）。
                int keyRoom = CustomKeyNodeCap - KeyLikeCountInGroup(template.GroupId);
                int imageRoom = 8 - UnboundImageCountInGroup(template.GroupId);
                if (keyLike && keyRoom <= 0) continue;
                if (!keyLike && imageRoom <= 0) continue;
                FmNode copy = template.Clone();
                copy.Id = Settings.Data.CustomNodeNextId++;
                copy.X += offset;
                copy.Y += offset;
                Settings.Data.CustomNodes.Add(copy);
                pasted.Add(copy);
            }
            if (pasted.Count == 0) return;
            editorSelection.Clear();
            editorSelection.AddRange(pasted);
            PushEditorHistory(false);
            EditorMutated();
        }

        private void EditorSelectAll()
        {
            editorSelection.Clear();
            foreach (FmNode node in Settings.Data.CustomNodes)
                if (node != null) editorSelection.Add(node);
        }

        /// <summary>Align the selection (modes 0-5: left / horizontal-center / right / top /
        /// vertical-center / bottom). Edge alignment preserves the existing gaps; center
        /// alignment uses each node's true center so differently-sized nodes are actually
        /// centered on the selection's center line. /
        /// 对齐选区（模式 0-5：左 / 水平居中 / 右 / 顶 / 垂直居中 / 底）。边缘对齐保留
        /// 原有间距；居中对齐按节点真实中心对齐，使不同尺寸的节点也真正位于选区中心线上。
        /// </summary>
        private void EditorAlignSelection(int mode)
        {
            List<FmNode> sel = new List<FmNode>();
            foreach (FmNode n in editorSelection) if (n != null) sel.Add(n);
            if (sel.Count < 2) return;
            if (mode <= 2)
            {
                sel.Sort((a, b) => a.X.CompareTo(b.X));
                float minX = sel[0].X;
                float maxX = float.MinValue;
                foreach (FmNode n in sel) maxX = Math.Max(maxX, n.X + n.Width);
                if (mode == 1)
                {
                    // Center every node on the selection's horizontal midpoint.  The previous
                    // implementation used minX for this branch, making the button a no-op and
                    // failing for nodes with different widths. / 每个节点按选区水平中点居中。
                    // 旧实现此分支误用 minX，按钮实际不生效且无法处理不同宽度。
                    float center = (minX + maxX) * 0.5f;
                    foreach (FmNode n in sel) n.X = center - n.Width * 0.5f;
                }
                else
                {
                    float target = mode == 0 ? minX : maxX - sel[sel.Count - 1].Width;
                    sel[0].X = target;
                    float gap = sel[1].X - (sel[0].X + sel[0].Width);
                    for (int i = 1; i < sel.Count; i++)
                        sel[i].X = sel[i - 1].X + sel[i - 1].Width + gap;
                }
            }
            else
            {
                sel.Sort((a, b) => a.Y.CompareTo(b.Y));
                float minY = sel[0].Y;
                float maxY = float.MinValue;
                foreach (FmNode n in sel) maxY = Math.Max(maxY, n.Y + n.Height);
                if (mode == 4)
                {
                    // Same true-center rule vertically. / 垂直方向使用同样的真实中心规则。
                    float center = (minY + maxY) * 0.5f;
                    foreach (FmNode n in sel) n.Y = center - n.Height * 0.5f;
                }
                else
                {
                    float target = mode == 3 ? minY : maxY - sel[sel.Count - 1].Height;
                    sel[0].Y = target;
                    float gap = sel[1].Y - (sel[0].Y + sel[0].Height);
                    for (int i = 1; i < sel.Count; i++)
                        sel[i].Y = sel[i - 1].Y + sel[i - 1].Height + gap;
                }
            }
            // EditorPropertyChanged records the post-change state, saves, and rebuilds the
            // overlay. Do not push a second snapshot here: doing so creates a no-op undo step.
            // EditorPropertyChanged 已负责后置快照、保存和重建；不要再压第二条快照，否则会多出空撤销。
            EditorPropertyChanged();
        }

        /// <summary>Distribute the selection evenly along one axis: the OUTERMOST nodes stay
        /// put, the ones between spread to equal center spacing. Undoable. /
        /// 沿一根轴等距分布：最外侧两节点不动，中间的按中心等间距铺开。可撤销。</summary>
        private void EditorDistributeSelection(bool horizontal)
        {
            List<FmNode> sel = new List<FmNode>();
            foreach (FmNode n in editorSelection) if (n != null) sel.Add(n);
            if (sel.Count < 3) return;
            if (horizontal)
            {
                sel.Sort((a, b) => a.X.CompareTo(b.X));
                float first = sel[0].X + sel[0].Width * 0.5f;
                float last = sel[sel.Count - 1].X + sel[sel.Count - 1].Width * 0.5f;
                if (Math.Abs(last - first) < 0.001f) return;
                float step = (last - first) / (sel.Count - 1);
                for (int i = 1; i < sel.Count - 1; i++)
                    sel[i].X = first + step * i - sel[i].Width * 0.5f;
            }
            else
            {
                sel.Sort((a, b) => a.Y.CompareTo(b.Y));
                float first = sel[0].Y + sel[0].Height * 0.5f;
                float last = sel[sel.Count - 1].Y + sel[sel.Count - 1].Height * 0.5f;
                if (Math.Abs(last - first) < 0.001f) return;
                float step = (last - first) / (sel.Count - 1);
                for (int i = 1; i < sel.Count - 1; i++)
                    sel[i].Y = first + step * i - sel[i].Height * 0.5f;
            }
            // EditorPropertyChanged already records the post-change state and rebuilds the
            // overlay; a second Push here would add a duplicate/no-op undo entry.
            // EditorPropertyChanged 已负责后置快照和重建；再次 Push 会制造重复/空撤销。
            EditorPropertyChanged();
        }

        /// <summary>Stamp `count` copies of the selection along one axis, each offset by the
        /// selection's bounds size + gap. Honors the same per-group budgets as paste (stat
        /// exclusivity, key-like cap, unbound-image cap) and stops early when they run out.
        /// Structural, undoable, selects originals + copies. / 沿一根轴阵列复制选区 count 份，
        /// 每份偏移选区包围盒尺寸 + 间距。遵守与粘贴相同的按组预算（面板独占、按键上限、
        /// 未绑定图片上限），预算耗尽即停。结构性变更、可撤销、选中原件+副本。</summary>
        private void EditorArraySelection(bool horizontal, int count, float gap)
        {
            if (count < 1 || editorSelection.Count == 0) return;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (FmNode n in editorSelection)
            {
                if (n == null) continue;
                minX = Math.Min(minX, n.X); maxX = Math.Max(maxX, n.X + n.Width);
                minY = Math.Min(minY, n.Y); maxY = Math.Max(maxY, n.Y + n.Height);
            }
            if (minX > maxX) return; // selection was all nulls / 选区全为空
            float dx = horizontal ? (maxX - minX) + gap : 0f;
            float dy = horizontal ? 0f : (maxY - minY) + gap;
            // Snapshot the templates: copies are APPENDED to editorSelection below, and
            // enumerating it directly while adding would throw. / 先快照模板：副本会追加进
            // editorSelection，边枚举边添加会抛异常。
            List<FmNode> templates = new List<FmNode>(editorSelection);
            int stamped = 0;
            for (int i = 1; i <= count; i++)
            {
                bool any = false;
                foreach (FmNode template in templates)
                {
                    if (template == null) continue;
                    if (template.NodeType == 1 && GroupHasStat(template.GroupId, 1)) continue;
                    if (template.NodeType == 2 && GroupHasStat(template.GroupId, 2)) continue;
                    bool keyLike = template.NodeType != 3 || !string.IsNullOrWhiteSpace(template.KeyBind);
                    if (keyLike && KeyLikeCountInGroup(template.GroupId) >= CustomKeyNodeCap) continue;
                    if (!keyLike && UnboundImageCountInGroup(template.GroupId) >= 8) continue;
                    FmNode copy = template.Clone();
                    copy.Id = Settings.Data.CustomNodeNextId++;
                    copy.X += dx * i;
                    copy.Y += dy * i;
                    Settings.Data.CustomNodes.Add(copy);
                    editorSelection.Add(copy);
                    any = true;
                }
                if (!any) break; // group budgets exhausted / 组预算耗尽
                stamped++;
            }
            if (stamped == 0) return;
            PushEditorHistory(false);
            EditorMutated();
        }

        private void EditorMutated()
        {
            // Keep the custom global Total derived from the document after any structural edit.
            // 任何结构编辑后都让 Custom 全局 Total 重新跟随节点文档。
            if (IsCustomLayout) RecalculateCustomTotalCount();
            // Structural changes save IMMEDIATELY (they are discrete, low-rate events and the
            // user's layout must survive a crash). / 结构性变更立即落盘（离散低频事件，布局必须
            // 在崩溃后存活）。
            SaveSettings();
            Loader.Log($"KeyViewer: saved {Settings.Data.CustomNodes.Count} custom nodes");
            if (KeyViewerObject != null && IsCustomLayout) ResetKeyViewer();
        }

        /// <summary>Toggle that only affects EDITOR behaviour (no runtime visual, no geometry, no
        /// node identity). EditorMutated would push history and request a full overlay rebuild —
        /// destroying and recreating every key GameObject — for a flag the running game cannot
        /// even see. / 只影响**编辑器**行为的开关（不改变运行时视觉、几何或节点身份）。
        /// EditorMutated 会压历史并请求整层重建——销毁重建所有按键 GameObject——而运行中的游戏
        /// 根本看不到这个标志。</summary>
        private void EditorOnlyChanged()
        {
            PushEditorHistoryNudge();
            SaveSettings();
        }

        private void RequestEditorRebuild()
        {
            if (KeyViewerObject != null && IsCustomLayout) ResetKeyViewer();
        }

        // ======================== history / 撤销 ========================

        // One timeline entry: the whole editable FreeMake document, not just the node list —
        // layer groups, the id counters and the global TotalCount live outside CustomNodes, and
        // omitting them made group/count edits unrestorable. The field names match the old
        // node-list JSON, so snapshots taken by earlier builds still parse. /
        // 单条时间线条目：整份可编辑 FreeMake 文档，而不仅是节点表——图层组、id 计数器与全局
        // TotalCount 都在 CustomNodes 之外，漏掉它们会让组/计数编辑无法恢复。字段名与旧的
        // 节点表 JSON 一致，早前构建拍的快照仍可解析。
        private sealed class FmDocumentSnapshot
        {
            public List<FmNode> Nodes;
            public List<FmLayerGroup> Groups;
            public int NodeNextId;
            public int GroupNextId;
            public int TotalCount;
            /// <summary>True when this entry deliberately carries NO live counters: the runtime
            /// per-node Count / global TotalCount are stripped on capture and re-applied from the
            /// live document on restore. Without it, undoing any layout or property edit after
            /// playing for a while silently rolled the accumulated keypress counts back — and
            /// EditorMutated saved that zeroed state to disk. Snapshots from earlier builds
            /// deserialize with this false and keep their original behaviour. / 该条目刻意不带
            /// 运行时计数：快照里剥离节点 Count 与全局 TotalCount，恢复时按 id 从实时文档回填。
            /// 否则游玩一段时间后撤销任何布局/属性编辑，都会把已累积的按键计数一起回滚，并被
            /// EditorMutated 落盘。旧快照反序列化为 false，行为不变。</summary>
            public bool PreserveCounts;
        }

        private bool editorBaselineSeeded;

        internal void SeedEditorHistoryBaseline()
        {
            if (editorBaselineSeeded) return;
            // Mark the baseline only after the snapshot call. If serialization fails, a later
            // open/retry can seed it instead of leaving CanUndo permanently disabled.
            // 只有快照成功后才标记基线；序列化失败时后续仍可重试，而不是永久禁用撤销。
            editorBaselineSeeded = PushEditorHistory(false);
        }

        /// <summary>A profile switch swaps the whole document out from under the editor: drop the
        /// old timeline (restoring it would write the previous profile's nodes into the new one)
        /// and re-seed so the first edit in the new profile stays undoable. / 切换配置时整份文档
        /// 被换走：丢弃旧时间线（恢复它会把上一配置的节点写进新配置）并重新播种，使新配置里的
        /// 首次编辑仍可撤销。</summary>
        internal void ResetEditorHistoryForProfileSwitch()
        {
            editorHistory.Clear();
            editorBaselineSeeded = false;
            // The old selection/active-node/group references point into the previous document. /
            // 旧的选中项/活动节点/组引用指向上一份文档。
            editorSelection.Clear();
            editorClipboard.Clear();
            editorPasteSerial = 0;
            fmActiveNode = null;
            fmActiveGroupId = "";
            ClearEditorInteractionState();
            fmEasingPicker = null;
            fmPropsScroll = Vector2.zero;
            fmPresetStripOpen = false;
            fmArrangeStripOpen = false;
            editorNeedsCentre = true;
            // Identity changed, so ghost edge state must not survive into the new document.
            ResetCustomGhostStates();
            SeedEditorHistoryBaseline();
        }

        private void ClearCaptureForNode(FmNode node)
        {
            if (node == null) return;
            if (fmCaptureNode == node) fmCaptureNode = null;
            if (fmCaptureGhostNode == node) fmCaptureGhostNode = null;
        }

        /// <summary>Cancel editor input/gesture state that belongs to the current document.
        /// This is intentionally separate from the undo timeline: closing the window should
        /// abort a capture/drag, while Profile/document replacement additionally clears the
        /// timeline, selection, and clipboard. / 取消属于当前文档的编辑器输入与手势状态。
        /// 关闭窗口只需中止捕获/拖拽；切换配置或替换文档时还会清理时间线、选区和剪贴板。 </summary>
        private void ClearEditorInteractionState()
        {
            // NOT fmHasKeyFocus. That flag is the keyboard-shortcut gate, and it is only restored
            // by a MouseDown inside the canvas. Clearing it here made Ctrl+Z a one-shot: the very
            // first undo (which correctly calls this) armed the shortcut gate off, so every
            // SUBSEQUENT undo/redo, Delete, Escape, copy, paste and arrow nudge silently did
            // nothing until the user clicked the canvas again — and holding Ctrl+Z stepped back
            // exactly one entry. Window focus is not a gesture: the editor window being open is
            // already the precondition, and OnGUI returns early when it is not.
            // 这里**不能**清 fmHasKeyFocus。那是键盘快捷键的门槛，且只有画布内的 MouseDown 会
            // 恢复它。在此清掉会让 Ctrl+Z 变成一次性：第一次撤销（正确地调了本方法）就把门槛
            // 关掉，于是此后**所有**撤销/重做、删除、Esc、复制、粘贴与方向键微调都静默失效，
            // 直到用户再点一次画布——按住 Ctrl+Z 只退一步。窗口焦点不是手势：编辑器窗口已打开
            // 本身已是前提，未打开时 OnGUI 本就早退。
            fmPointerDown = false;
            fmGesture = FmGesture.None;
            fmDragArmed = false;
            fmDragMoved = false;
            fmAxisLocked = false;
            fmLockToX = false;
            fmDragTotalX = 0f;
            fmDragTotalY = 0f;
            fmPressScreen = Vector2.zero;
            fmPressCanvas = Vector2.zero;
            fmMarqueeStart = Vector2.zero;
            fmMarqueeCur = Vector2.zero;
            fmDragStart.Clear();
            editorSelectionAtPress.Clear();
            fmHitBuffer.Clear();
            fmAlignLines.Clear();
            fmResizeHandle = -1;
            fmResizeOrig.Clear();
            fmResizeMoved = false;
            fmResizeHistoryPushed = false;
            // The window-resize flag is a SEPARATE gesture from the canvas resize handle; leaving
            // it set meant a profile switch or undo during a window drag left the next MouseDrag
            // resizing the editor window out of nowhere.
            // 窗口缩放标志与画布缩放手柄是两套独立手势；残留时在窗口拖拽途中切配置或撤销，
            // 下一次 MouseDrag 会莫名去拖动编辑器窗口。
            fmResizing = false;
            fmMinimapDrag = false;
            fmMinimapMoved = false;
            // A pending focus request belongs to the node that was about to be typed into; after a
            // document swap that field no longer exists. / 待处理的聚焦请求属于原本要输入的节点；
            // 文档被替换后该字段已不存在。
            fmFocusVideoPathNextPass = false;
            fmSiblingW.Clear();
            fmSiblingH.Clear();
            fmCaptureNode = null;
            fmCaptureGhostNode = null;
        }

        private string SnapshotEditorDocument(bool preserveCounts = true)
        {
            List<FmNode> nodes = Settings.Data.CustomNodes;
            int total = Settings.Data.TotalCount;
            if (preserveCounts && nodes != null && nodes.Count > 0)
            {
                // Strip the runtime counters from the captured document. Cloning (rather than
                // zeroing the live nodes and restoring them afterwards) keeps a serialization
                // throw from leaving the user's counts at zero.
                var stripped = new List<FmNode>(nodes.Count);
                for (int i = 0; i < nodes.Count; i++)
                {
                    FmNode live = nodes[i];
                    if (live == null) { stripped.Add(null); continue; }
                    FmNode copy = live.Clone();
                    copy.Count = 0;
                    stripped.Add(copy);
                }
                nodes = stripped;
                total = 0;
            }
            return JsonConvert.SerializeObject(new FmDocumentSnapshot
            {
                Nodes = nodes,
                Groups = Settings.Data.LayerGroups,
                NodeNextId = Settings.Data.CustomNodeNextId,
                GroupNextId = Settings.Data.LayerGroupNextId,
                TotalCount = total,
                PreserveCounts = preserveCounts,
            });
        }

        /// <summary>Record the CURRENT document state as one timeline entry. `allowSave` is false
        /// for continuous edits (slider/text bursts) that already save through the debounced GUI
        /// path, true for discrete structural edits. / 把当前文档状态记为一条时间线记录。
        /// 连续编辑（滑杆/文本突发，已走 GUI 去抖保存）传 allowSave=false；离散的结构编辑传
        /// true。</summary>
        private bool PushEditorHistory(bool allowSave = true, bool preserveCounts = true)
        {
            try
            {
                editorHistory.Push(SnapshotEditorDocument(preserveCounts));
            }
            catch (Exception e)
            {
                Loader.Warning($"KeyViewer: editor snapshot failed: {e.Message}");
                return false;
            }
            if (allowSave) SaveSettings();
            return true;
        }

        /// <summary>Nudge variant: coalesces a burst of continuous edits into one entry. /
        /// 微调变体：把一连串连续编辑合并成一条记录。</summary>
        private void PushEditorHistoryNudge()
        {
            try
            {
                editorHistory.PushNudge(SnapshotEditorDocument(), Time.unscaledTime);
            }
            catch (Exception e)
            {
                Loader.Warning($"KeyViewer: editor snapshot failed: {e.Message}");
            }
        }

        private void EditorUndo()
        {
            // Clear here too, not only at the keyboard call site. Undo/redo swap the whole node
            // list for freshly deserialized instances, so a live gesture holding the pre-undo
            // instances in fmDragStart/fmResizeOrig would keep writing coordinates into objects
            // that are no longer in the document — the node appears stuck and the canvas lies.
            // The keyboard path already did this; the TOOLBAR buttons did not, so the same undo
            // behaved differently depending on how it was triggered. Clearing is idempotent, so
            // doing it in both places costs nothing. / 撤销/重做会把整份节点表换成反序列化出的
            // 新实例；仍在进行的手势持有的是撤销前的旧实例，后续每帧都会往已不在文档里的对象写
            // 坐标，表现为节点卡住不动。键盘路径原本已清理，**工具栏按钮没有**，于是同一个撤销
            // 因触发方式不同而行为不同。清理是幂等的，两处都做没有代价。
            ClearEditorInteractionState();
            string current = SnapshotEditorDocument();
            RestoreEditorSnapshot(editorHistory.Undo(current));
        }

        private void EditorRedo()
        {
            ClearEditorInteractionState();
            string current = SnapshotEditorDocument();
            RestoreEditorSnapshot(editorHistory.Redo(current));
        }

        private void RestoreEditorSnapshot(string snapshot)
        {
            if (snapshot == null) return;
            try
            {
                FmDocumentSnapshot doc = string.IsNullOrEmpty(snapshot)
                    ? new FmDocumentSnapshot()
                    : JsonConvert.DeserializeObject<FmDocumentSnapshot>(snapshot);
                // Capture the live counters BEFORE the document is replaced (the restored nodes are
                // fresh instances whose Count defaults to 0).
                var liveCounts = new Dictionary<int, int>();
                List<FmNode> liveNodes = Settings.Data.CustomNodes;
                if (doc != null && doc.PreserveCounts && liveNodes != null)
                    for (int i = 0; i < liveNodes.Count; i++)
                        if (liveNodes[i] != null) liveCounts[liveNodes[i].Id] = liveNodes[i].Count;
                int liveTotal = Settings.Data.TotalCount;

                Settings.Data.CustomNodes = doc?.Nodes ?? new List<FmNode>();
                Settings.Data.LayerGroups = doc?.Groups ?? new List<FmLayerGroup>();
                if (doc != null)
                {
                    if (doc.NodeNextId > 0) Settings.Data.CustomNodeNextId = doc.NodeNextId;
                    if (doc.GroupNextId > 0) Settings.Data.LayerGroupNextId = doc.GroupNextId;
                    if (doc.PreserveCounts)
                    {
                        // Re-apply the live counters onto the restored instances by id. A node that
                        // did not exist before this undo entry simply keeps 0.
                        List<FmNode> restored = Settings.Data.CustomNodes;
                        for (int i = 0; i < restored.Count; i++)
                        {
                            FmNode n = restored[i];
                            if (n != null && liveCounts.TryGetValue(n.Id, out int count)) n.Count = count;
                        }
                        Settings.Data.TotalCount = liveTotal;
                    }
                    else
                    {
                        Settings.Data.TotalCount = doc.TotalCount;
                    }
                }
                EnsureCustomNodes();
                SetEditorSelectionById(doc?.Nodes);
                RefreshAllCountDisplay();
                EditorMutated();
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: editor snapshot did not parse: {e.Message}");
            }
        }

        /// <summary>Re-select the restored nodes by id — the deserialized document holds fresh
        /// FmNode instances, so the pre-undo references would point at nodes no longer in the
        /// list. Unknown/gone ids and dangling group/active-node references drop out. /
        /// 按 id 重新选中恢复后的节点——反序列化出的文档是全新 FmNode 实例，撤销前的引用指向
        /// 已不在表里的节点。找不到的 id 与悬空的组/活动节点引用一并清除。</summary>
        private void SetEditorSelectionById(List<FmNode> nodes)
        {
            var byId = new Dictionary<int, FmNode>();
            if (nodes != null)
                foreach (FmNode n in nodes)
                    if (n != null) byId[n.Id] = n;
            var kept = new List<FmNode>();
            foreach (FmNode n in editorSelection)
                if (n != null && byId.TryGetValue(n.Id, out FmNode same)) kept.Add(same);
            editorSelection.Clear();
            editorSelection.AddRange(kept);
            if (fmActiveNode == null || !editorSelection.Contains(fmActiveNode))
                fmActiveNode = editorSelection.Count > 0 ? editorSelection[0] : null;
            // Undo/redo drops any binding capture — the node it pointed at may be gone. /
            // 撤销/重做清掉绑定捕获——它指向的节点可能已不存在。
            fmCaptureNode = null;
            fmCaptureGhostNode = null;
        }

        // ======================== canvas / 画布 ========================

        private Vector2 EditorOrigin(Rect rect)
        {
            return rect.center + fmScroll;
        }

        private Vector2 EditorCanvasOf(Rect rect, Vector2 screen)
        {
            return (screen - EditorOrigin(rect)) / Mathf.Max(0.05f, fmZoom);
        }

        private Vector2 EditorScreenOf(Rect rect, Vector2 canvasPos)
        {
            return EditorOrigin(rect) + canvasPos * fmZoom;
        }

        private void EditorZoomAt(Rect rect, Vector2 screen, float step)
        {
            float from = fmZoom;
            float to = Mathf.Clamp(from + step, 0.2f, 3f);
            if (Mathf.Approximately(from, to)) return;
            Vector2 anchor = EditorCanvasOf(rect, screen);
            fmZoom = to;
            // Keep the anchor point under the cursor: scroll = screen - origin adjustments. /
            // 让锚点保持在光标下：scroll = screen - origin 调整。
            fmScroll = screen - rect.center - anchor * to;
        }

        private Vector2 EditorViewCenter()
        {
            // View center in canvas coordinates; before the first layout pass fall back to the
            // screen-bounds center. / 画布坐标下的视口中心；首次布局前回退到屏幕范围中心。
            if (fmLastCanvasRect.width > 1f)
                return EditorCanvasOf(fmLastCanvasRect, fmLastCanvasRect.center);
            return new Vector2(CanvasWidth * 0.5f, 540f);
        }

        private void CentreEditorView(Rect rect)
        {
            float zoom = Mathf.Clamp(Mathf.Min(
                rect.width / (CanvasWidth + 80f),
                rect.height / (1080f + 80f)), 0.2f, 1.5f);
            fmZoom = zoom;
            fmScroll = -new Vector2(CanvasWidth * 0.5f, 540f) * zoom;
        }

        private void HandleEditorCanvas(Rect rect, Event e)
        {
            fmLastCanvasRect = rect;
            if (editorNeedsCentre && rect.width > 1f)
            {
                CentreEditorView(rect);
                editorNeedsCentre = false;
            }
            bool hover = rect.Contains(e.mousePosition);
            if (hover && e.type == EventType.ScrollWheel)
            {
                EditorZoomAt(rect, e.mousePosition, -e.delta.y * 0.06f);
                e.Use();
                return;
            }
            if (e.type == EventType.MouseDrag && e.button == 1 && hover)
            {
                // Right-drag pans the canvas / 右键拖动平移画布
                fmScroll += e.delta;
                e.Use();
                return;
            }
            if (e.type == EventType.Repaint)
            {
                DrawEditorCanvas(rect);
                DrawEditorMinimap(rect);
            }
            // Minimap viewport drag runs before canvas gestures and keeps working while the
            // cursor leaves the small box. Use() is only legal on input events — Layout/Repaint
            // must pass through untouched. / 小地图视口拖动先于画布手势处理，光标离开小框后
            // 仍持续生效。Use() 只对输入事件合法——Layout/Repaint 必须原样放行。
            if (fmMinimapDrag)
            {
                // A MouseUp can be lost entirely — Alt-Tab, dragging outside the window, a focus
                // change, a modal popup. Without this fallback the flag stayed true FOREVER and
                // this branch swallowed every subsequent MouseDown/Drag/Up and returned, so
                // selection, dragging, the resize handle, the marquee and all keyboard shortcuts
                // silently died until the editor was reopened. The canvas drag path already had
                // this guard; the minimap did not.
                // MouseUp 可能彻底丢失——Alt-Tab、拖出窗口、焦点变化、弹窗。没有这层兜底，该标志
                // 会**永久**为真，且本分支会吞掉此后每一次 MouseDown/Drag/Up 并返回，于是选中、
                // 拖拽、缩放手柄、框选与全部键盘快捷键静默失效，直到重开编辑器。画布拖拽路径
                // 本就有此守卫，小地图没有。
                if (e.type != EventType.MouseDown && !Input.GetMouseButton(0)) fmMinimapDrag = false;
                if (fmMinimapDrag)
                {
                    HandleEditorMinimapDrag(rect, e);
                    if (e.type == EventType.MouseDrag || e.type == EventType.MouseUp || e.type == EventType.MouseDown)
                        e.Use();
                    return;
                }
            }
            if (HandleEditorMinimapStart(rect, e)) return;
            // Keyboard shortcuts: only while the window has mouse focus (last press inside) and
            // no editor text field is typing — HandleEditorShortcuts itself ignores non-KeyDown
            // events (Layout/Repaint carry KeyCode.None and fall through). /
            // 快捷键仅在窗口持有鼠标焦点（最后一次按下在窗口内）时处理；非 KeyDown 事件
            //（Layout/Repaint 的 KeyCode 为 None）在内部自然落空，不会触发 Use。
            if (fmHasKeyFocus && e.type == EventType.KeyDown)
                HandleEditorShortcuts(e);
            if (e.type == EventType.MouseDown && e.button == 0 && hover)
            {
                fmPointerDown = true;
                fmPressScreen = e.mousePosition;
                fmPressCanvas = EditorCanvasOf(rect, e.mousePosition);
                fmResizeHandle = EditorHandleHit(rect, e.mousePosition);
                if (fmResizeHandle >= 0)
                {
                    fmGesture = FmGesture.Resize;
                    BeginEditorResize();
                    e.Use();
                    return;
                }
                fmGesture = FmGesture.Pending;
                fmHitBuffer.Clear();
                fmHitBuffer.AddRange(HitTestEditorNodes(fmPressCanvas, e.shift));
                bool doubleClick = ConsumeEditorDoubleClick(e.mousePosition);
                FmNode target = PickEditorNode(fmHitBuffer, doubleClick, e.control);
                EditorApplyCanvasSelection(target, doubleClick, e.control);
                fmDragArmed = target != null && !e.control && !doubleClick && editorSelection.Contains(target);
                if (fmDragArmed) BeginNodeDrag();
                e.Use();
            }
            if (fmPointerDown && e.type == EventType.MouseDrag && e.button == 0)
            {
                UpdateEditorGesture(rect, e);
                e.Use();
            }
            if (fmPointerDown && e.type == EventType.MouseUp && e.button == 0)
            {
                EndEditorGesture();
                fmPointerDown = false;
                e.Use();
            }
            if (fmPointerDown && !Input.GetMouseButton(0))
            {
                EndEditorGesture();
                fmPointerDown = false;
            }
        }

        private void UpdateEditorGesture(Rect rect, Event e)
        {
            if (fmGesture == FmGesture.Resize)
            {
                UpdateEditorResize(rect, e);
                return;
            }
            if (fmGesture == FmGesture.Pending)
            {
                if ((e.mousePosition - fmPressScreen).sqrMagnitude < 25f) return;
                if (fmDragArmed) fmGesture = FmGesture.DragNodes;
                else if (fmHitBuffer.Count == 0) { fmGesture = FmGesture.Marquee; fmMarqueeStart = fmPressCanvas; }
                else { fmGesture = FmGesture.None; return; }
            }
            Vector2 canvasPos = EditorCanvasOf(rect, e.mousePosition);
            if (fmGesture == FmGesture.DragNodes)
            {
                fmDragTotalX += e.delta.x / Mathf.Max(0.05f, fmZoom);
                fmDragTotalY += e.delta.y / Mathf.Max(0.05f, fmZoom);
                if (e.delta.x != 0f || e.delta.y != 0f)
                {
                    if (!fmDragMoved)
                    {
                        // First real movement: the move below lands the post-drag geometry, and
                        // this records it as one timeline entry (the pre-drag state is the entry
                        // before it). / 首次真实移动：下方的位移写入拖拽后的几何，这里把它记为
                        // 一条时间线记录（拖拽前状态是它的前一条）。
                        fmDragMoved = true;
                    }
                }
                if (!fmAxisLocked)
                {
                    fmLockToX = Mathf.Abs(e.delta.x) >= Mathf.Abs(e.delta.y);
                    fmAxisLocked = true;
                }
                float dx = fmDragTotalX, dy = fmDragTotalY;
                if (e.shift)
                {
                    if (fmLockToX) dy = 0f;
                    else dx = 0f;
                }
                ApplyEditorDrag(dx, dy, e.alt);
                if (fmDragMoved && !fmDragHistoryPushed)
                {
                    fmDragHistoryPushed = true;
                    PushEditorHistory(false);
                }
            }
            else if (fmGesture == FmGesture.Marquee)
            {
                fmMarqueeCur = canvasPos;
            }
        }

        private void EndEditorGesture()
        {
            if (fmGesture == FmGesture.Resize)
            {
                EndEditorResize();
            }
            else if (fmGesture == FmGesture.DragNodes)
            {
                EndNodeDrag();
            }
            else if (fmGesture == FmGesture.Marquee)
            {
                Rect band = EditorRectFromCorners(fmMarqueeStart, fmMarqueeCur);
                editorSelection.Clear();
                foreach (FmNode node in Settings.Data.CustomNodes)
                {
                    if (node == null) continue;
                    if (node.Unselectable && !Event.current.shift) continue;
                    Rect nodeRect = new Rect(node.X, node.Y, node.Width, node.Height);
                    if (nodeRect.Overlaps(band)) editorSelection.Add(node);
                }
            }
            fmGesture = FmGesture.None;
            fmAlignLines.Clear();
            fmDragStart.Clear();
            fmDragHistoryPushed = false;
            fmResizeHandle = -1;
        }

        private static Rect EditorRectFromCorners(Vector2 a, Vector2 b)
        {
            return new Rect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Abs(b.x - a.x), Mathf.Abs(b.y - a.y));
        }

        private void BeginNodeDrag()
        {
            fmDragStart.Clear();
            foreach (FmNode node in editorSelection)
                if (node != null) fmDragStart[node] = new Vector2(node.X, node.Y);
            fmDragTotalX = 0f;
            fmDragTotalY = 0f;
            fmDragMoved = false;
            fmDragHistoryPushed = false;
            fmAxisLocked = false;
        }

        private void EndNodeDrag()
        {
            if (fmDragMoved)
            {
                // The drag pushed its snapshot on the FIRST moved frame. Undo works by writing
                // "current" into the entry it steps over, so a later undo of some OTHER edit would
                // return to that first-frame geometry and silently yank the node back to where the
                // drag happened to be on frame 1. Replace the top entry with the finished state.
                // 拖拽在第一个移动帧就压入了快照。撤销会把"当前状态"写进它跨过的那一条，因此撤销
                // 之后的其它编辑时，会回到第一帧的几何——节点被悄悄拽回拖拽中途。改为用最终态
                // 就地替换栈顶条目。
                ReplaceLastEditorSnapshot();
                SaveSettingsFromGui();
                RequestEditorRebuild();
            }
            fmAlignLines.Clear();
        }

        /// <summary>Overwrite the newest timeline entry with the current document (used at the end
        /// of drag/resize gestures, whose entry was pushed at the first moved frame). No-op when the
        /// timeline is empty. / 用当前文档覆盖时间线最新一条（拖拽/缩放手势在第一个移动帧就已压入
        /// 该条）。时间线为空时为空操作。</summary>
        private void ReplaceLastEditorSnapshot()
        {
            try
            {
                if (!editorHistory.ReplaceTop(SnapshotEditorDocument())) return;
            }
            catch (Exception e)
            {
                Loader.Warning($"KeyViewer: editor snapshot refresh failed: {e.Message}");
            }
        }

        /// <summary>Live geometry sync during drag/resize gestures: update the EXISTING runtime
        /// keys in place (rect / text wrapper / image) instead of a throttled full rebuild — no
        /// object churn, no rain clearing, every frame. A full rebuild still normalizes
        /// everything at gesture end. / 拖动/缩放手势期间的实时几何同步：就地更新既有运行时
        /// 按键（矩形/文本容器/图片），取代节流整建——无对象抖动、不清雨滴、逐帧生效。手势
        /// 结束时仍做一次完整重建归一。</summary>
        private void ApplyLiveGeometry()
        {
            if (Keys == null || keyShapeLayer == null) return;
            foreach (FmNode node in editorSelection)
            {
                if (node == null || node.RuntimeKey == null) continue;
                Key key = node.RuntimeKey;
                float cy = CustomNodeCenterY(node);
                key.keySize = new Vector2(node.Width, node.Height);
                RectTransform rt = (RectTransform)key.transform;
                rt.anchoredPosition = new Vector2(node.X, cy);
                if (keyShapeLayer != null && key.shapeSlot >= 0)
                {
                    keyShapeLayer.SetRect(key.shapeSlot, node.X, cy - node.Height * 0.5f, node.Width, node.Height);
                    // Shape edits (radius / border) ride the same live path as geometry, so the
                    // panel sliders show their effect without waiting for a full rebuild. /
                    // 形状编辑（圆角/边框）与几何走同一条实时路径，面板滑块无需等整建即可见。
                    if (node.NodeType != 3)
                    {
                        keyShapeLayer.SetCornerRadius(key.shapeSlot, node.CornerRadius);
                        keyShapeLayer.SetBorderThickness(key.shapeSlot, node.BorderThickness);
                    }
                }
                if (key.visuals != null)
                {
                    RectTransform vt = (RectTransform)key.visuals;
                    vt.anchoredPosition = new Vector2(node.X + node.Width * 0.5f, cy);
                    vt.sizeDelta = new Vector2(node.Width, node.Height);
                    // Keep the text width in sync with the box (height follows the layout mode). /
                    // 文本宽度与框同步（高度由布局模式决定）。
                    if (key.text != null)
                        key.text.rectTransform.sizeDelta = new Vector2(node.Width - 4f, key.text.rectTransform.sizeDelta.y);
                    if (key.value != null)
                        key.value.rectTransform.sizeDelta = new Vector2(node.Width - 4f, key.value.rectTransform.sizeDelta.y);
                }
                if (key.CustomImageRect != null)
                {
                    key.CustomImageRect.anchoredPosition = new Vector2(node.X + node.Width * 0.5f, cy);
                    key.CustomImageRect.sizeDelta = new Vector2(node.Width, node.Height);
                }
            }
        }

        // ======================== resize handles / 缩放手柄 ========================
        // Single selection gets 8-way handles; multi selection gets 4 corner handles that
        // scale the whole bounding box from the opposite corner (whole-box
        // resize). / 单选八向手柄；多选四角手柄自对角整体缩放包围盒（整体
        // 缩放语义）。

        private static readonly (int dx, int dy)[] FmHandleDirs8 =
            { (-1, -1), (0, -1), (1, -1), (-1, 0), (1, 0), (-1, 1), (0, 1), (1, 1) };
        private static readonly (int dx, int dy)[] FmHandleDirs4 =
            { (-1, -1), (1, -1), (-1, 1), (1, 1) };

        private (int dx, int dy)[] EditorActiveHandleDirs()
        {
            return editorSelection.Count == 1 ? FmHandleDirs8 : FmHandleDirs4;
        }

        private Rect EditorSelectionBounds()
        {
            Rect bbox = new Rect(float.MaxValue, float.MaxValue, 0f, 0f);
            bool any = false;
            foreach (FmNode node in editorSelection)
            {
                if (node == null) continue;
                Rect r = new Rect(node.X, node.Y, node.Width, node.Height);
                if (!any)
                {
                    bbox = r;
                    any = true;
                    continue;
                }
                bbox = Rect.MinMaxRect(
                    Mathf.Min(bbox.x, r.x), Mathf.Min(bbox.y, r.y),
                    Mathf.Max(bbox.xMax, r.xMax), Mathf.Max(bbox.yMax, r.yMax));
            }
            return any ? bbox : new Rect(0f, 0f, 0f, 0f);
        }

        private static Vector2 FmHandlePoint(Rect r, int dx, int dy)
        {
            return new Vector2(dx < 0 ? r.x : dx > 0 ? r.xMax : r.center.x,
                dy < 0 ? r.y : dy > 0 ? r.yMax : r.center.y);
        }

        private int EditorHandleHit(Rect canvasScreenRect, Vector2 screen)
        {
            if (editorSelection.Count == 0) return -1;
            Rect bbox = EditorSelectionBounds();
            var dirs = EditorActiveHandleDirs();
            for (int i = 0; i < dirs.Length; i++)
            {
                Vector2 sp = EditorScreenOf(canvasScreenRect, FmHandlePoint(bbox, dirs[i].dx, dirs[i].dy));
                if (Mathf.Abs(screen.x - sp.x) <= 7f && Mathf.Abs(screen.y - sp.y) <= 7f) return i;
            }
            return -1;
        }

        private void BeginEditorResize()
        {
            fmResizeOrig.Clear();
            foreach (FmNode node in editorSelection)
                if (node != null)
                    fmResizeOrig.Add(new KeyValuePair<FmNode, Rect>(node, new Rect(node.X, node.Y, node.Width, node.Height)));
            fmResizeBBox = EditorSelectionBounds();
            fmResizeMoved = false;
            fmResizeHistoryPushed = false;
            // Sibling sizes for size-snapping during the resize. /
            // 兄弟节点尺寸表，供缩放时的尺寸吸附。
            fmSiblingW.Clear();
            fmSiblingH.Clear();
            foreach (FmNode node in Settings.Data.CustomNodes)
            {
                if (node == null || editorSelection.Contains(node)) continue;
                fmSiblingW.Add(node.Width);
                fmSiblingH.Add(node.Height);
            }
        }

        private void UpdateEditorResize(Rect rect, Event e)
        {
            Vector2 canvasPos = EditorCanvasOf(rect, e.mousePosition);
            if (!fmResizeMoved)
            {
                if ((canvasPos - fmPressCanvas).sqrMagnitude < 0.01f) return;
                fmResizeMoved = true;
            }
            const float minSize = 10f;
            (int dx, int dy) = EditorActiveHandleDirs()[fmResizeHandle];
            if (editorSelection.Count == 1 && fmResizeOrig.Count > 0)
            {
                FmNode node = fmResizeOrig[0].Key;
                Rect o = fmResizeOrig[0].Value;
                float w = dx == 0 ? o.width : dx > 0 ? o.width + (canvasPos.x - fmPressCanvas.x) : o.width - (canvasPos.x - fmPressCanvas.x);
                float h = dy == 0 ? o.height : dy > 0 ? o.height + (canvasPos.y - fmPressCanvas.y) : o.height - (canvasPos.y - fmPressCanvas.y);
                w = Mathf.Max(minSize, w);
                h = Mathf.Max(minSize, h);
                // Size snapping: grid round + match sibling sizes (±4px); Alt bypasses. /
                // 尺寸吸附：网格取整 + 匹配兄弟节点尺寸（±4px）；Alt 临时关闭。
                if (!e.alt)
                {
                    if (dx != 0) w = EditorSnapSize(w, fmSiblingW);
                    if (dy != 0) h = EditorSnapSize(h, fmSiblingH);
                }
                if (e.shift && o.width > 0f && o.height > 0f)
                {
                    float aspect = o.width / o.height;
                    if (dx != 0 && dy != 0)
                    {
                        if (Mathf.Abs(w - o.width) >= Mathf.Abs(h - o.height)) h = w / aspect;
                        else w = h * aspect;
                    }
                    else if (dx != 0) h = w / aspect;
                    else if (dy != 0) w = h * aspect;
                }
                w = Mathf.Max(minSize, w);
                h = Mathf.Max(minSize, h);
                node.Width = w;
                node.Height = h;
                node.X = dx < 0 ? o.xMax - w : o.x;
                node.Y = dy < 0 ? o.yMax - h : o.y;
                ApplyLiveGeometry();
                PushResizeHistory();
                return;
            }
            // Multi-selection: scale the whole bounding box from the opposite corner, clamped
            // so no node drops below the minimum size. / 多选：自对角整体缩放包围盒，钳制保证
            // 任何节点不低于最小尺寸。
            float sx = 1f, sy = 1f;
            if (dx != 0)
            {
                float nw = dx > 0 ? fmResizeBBox.width + (canvasPos.x - fmPressCanvas.x) : fmResizeBBox.width - (canvasPos.x - fmPressCanvas.x);
                sx = Mathf.Max(0.05f, nw / Mathf.Max(1f, fmResizeBBox.width));
                foreach (var kv in fmResizeOrig)
                    sx = Mathf.Max(sx, minSize / Mathf.Max(1f, kv.Value.width));
            }
            if (dy != 0)
            {
                float nh = dy > 0 ? fmResizeBBox.height + (canvasPos.y - fmPressCanvas.y) : fmResizeBBox.height - (canvasPos.y - fmPressCanvas.y);
                sy = Mathf.Max(0.05f, nh / Mathf.Max(1f, fmResizeBBox.height));
                foreach (var kv in fmResizeOrig)
                    sy = Mathf.Max(sy, minSize / Mathf.Max(1f, kv.Value.height));
            }
            float anchorX = dx < 0 ? fmResizeBBox.xMax : fmResizeBBox.x;
            float anchorY = dy < 0 ? fmResizeBBox.yMax : fmResizeBBox.y;
            foreach (var kv in fmResizeOrig)
            {
                kv.Key.Width = kv.Value.width * sx;
                kv.Key.Height = kv.Value.height * sy;
                kv.Key.X = anchorX + (kv.Value.x - anchorX) * sx;
                kv.Key.Y = anchorY + (kv.Value.y - anchorY) * sy;
            }
            ApplyLiveGeometry();
            PushResizeHistory();
        }

        /// <summary>Record the post-resize geometry once per gesture — the incremental handler
        /// runs every drag frame, so only the first moved frame becomes a timeline entry. /
        /// 每次缩放手势只记录一次拖拽后的几何——增量处理逐帧运行，只有首个真正移动的帧入栈。</summary>
        private void PushResizeHistory()
        {
            if (!fmResizeMoved || fmResizeHistoryPushed) return;
            fmResizeHistoryPushed = true;
            PushEditorHistory(false);
        }

        private void EndEditorResize()
        {
            fmResizeOrig.Clear();
            fmResizeHistoryPushed = false;
            if (fmResizeMoved)
            {
                // Same reason as EndNodeDrag: replace the first-moved-frame entry with the final
                // geometry so a later undo cannot revert the size to mid-gesture. / 与 EndNodeDrag
                // 同理：用最终尺寸就地替换首个移动帧的条目，后续撤销才不会把尺寸退回手势中途。
                ReplaceLastEditorSnapshot();
                SaveSettingsFromGui();
                RequestEditorRebuild();
            }
        }

        /// <summary>Size snapping while resizing: grid rounding (5px) first, then a sibling-size
        /// match within ±4px — resizing a key to another key's exact width takes one gesture. /
        /// 缩放时的尺寸吸附：先按 5px 网格取整，再在 ±4px 内匹配兄弟节点尺寸——把一个键缩放
        /// 到与另一个键完全同宽只需一次手势。</summary>
        private static float EditorSnapSize(float value, List<float> siblingSizes)
        {
            const float grid = 5f;
            const float sizeMatch = 4f;
            float snapped = Mathf.Round(value / grid) * grid;
            if (siblingSizes != null)
            {
                float best = float.MaxValue;
                float at = snapped;
                foreach (float s in siblingSizes)
                {
                    float d = s - value;
                    if (Mathf.Abs(d) < Mathf.Abs(best))
                    {
                        best = d;
                        at = s;
                    }
                }
                if (Mathf.Abs(best) <= sizeMatch) snapped = at;
            }
            return Mathf.Max(10f, snapped);
        }

        private void DrawEditorResizeHandles(Rect rect)
        {
            if (editorSelection.Count == 0 || fmGesture == FmGesture.DragNodes || fmGesture == FmGesture.Marquee) return;
            Rect bbox = EditorSelectionBounds();
            if (bbox.width <= 0f && bbox.height <= 0f) return;
            var dirs = EditorActiveHandleDirs();
            for (int i = 0; i < dirs.Length; i++)
            {
                Vector2 sp = EditorScreenOf(rect, FmHandlePoint(bbox, dirs[i].dx, dirs[i].dy));
                sp.x = Mathf.Clamp(sp.x, rect.x + 4f, rect.xMax - 4f);
                sp.y = Mathf.Clamp(sp.y, rect.y + 4f, rect.yMax - 4f);
                GUIUtils.DrawRect(new Rect(sp.x - 6f, sp.y - 6f, 12f, 12f), Color.white);
                GUIUtils.DrawRect(new Rect(sp.x - 4f, sp.y - 4f, 8f, 8f), new Color(0.25f, 0.55f, 1f, 0.95f));
            }
        }

        // ======================== minimap / 小地图 ========================
        // Small bottom-right corner box. Its position is clamped INSIDE the canvas
        // rect, and the canvas rect is now guaranteed to fit the window (content minimums below
        // the window minimum), so the box can never escape. / 右下角小框。位置钳制在
        // 画布矩形内部，而画布矩形现在保证不超出窗口（内容最小值低于窗口最小值），因此小框
        // 永远不会出界。支持拖动白色视口框平移视图。

        private bool fmMinimapDrag;
        private bool fmMinimapMoved;

        private Rect FmMinimapRect(Rect canvasRect)
        {
            float regionW = CanvasWidth + 200f;
            float regionH = 1080f + 200f;
            float boxW = FmMinimapWidth;
            float boxH = boxW * regionH / regionW;
            float x = Mathf.Clamp(canvasRect.xMax - boxW - 8f, canvasRect.x + 4f, canvasRect.xMax - boxW - 4f);
            float y = Mathf.Clamp(canvasRect.yMax - boxH - 8f, canvasRect.y + 4f, canvasRect.yMax - boxH - 4f);
            return new Rect(x, y, boxW, boxH);
        }

        private bool HandleEditorMinimapStart(Rect canvasRect, Event e)
        {
            Rect box = FmMinimapRect(canvasRect);
            if (e.type != EventType.MouseDown || e.button != 0 || !box.Contains(e.mousePosition)) return false;
            // Only the white viewport rect itself starts a drag; clicks elsewhere in the box are
            // consumed (so canvas gestures don't bleed through) but move nothing. /
            // 只有白色视口框本身可以发起拖动；框内白框外的点击被吞掉（避免误触发画布手势），
            // 但不移动视图。
            e.Use();
            if (!FmMinimapViewportBox(canvasRect, box).Contains(e.mousePosition)) return true;
            fmMinimapDrag = true;
            fmMinimapMoved = false;
            return true;
        }

        /// <summary>The white viewport rectangle mapped into minimap box space. /
        /// 映射到小地图框空间的白色视口矩形。</summary>
        private Rect FmMinimapViewportBox(Rect canvasRect, Rect box)
        {
            float scale = Mathf.Min(box.width / (CanvasWidth + 200f), box.height / (1080f + 200f));
            Vector2 regionCenter = new Vector2(CanvasWidth * 0.5f, 540f);
            Vector2 boxCenter = box.center;
            Vector2 vpMin = EditorCanvasOf(canvasRect, canvasRect.min);
            Vector2 vpMax = EditorCanvasOf(canvasRect, canvasRect.max);
            Vector2 a = boxCenter + (new Vector2(Mathf.Min(vpMin.x, vpMax.x), Mathf.Min(vpMin.y, vpMax.y)) - regionCenter) * scale;
            return new Rect(a.x, a.y, Mathf.Abs(vpMax.x - vpMin.x) * scale, Mathf.Abs(vpMax.y - vpMin.y) * scale);
        }

        private void HandleEditorMinimapDrag(Rect canvasRect, Event e)
        {
            Rect box = FmMinimapRect(canvasRect);
            float scale = Mathf.Min(box.width / (CanvasWidth + 200f), box.height / (1080f + 200f));
            if (e.type == EventType.MouseDrag)
            {
                // Drag the white viewport box: box-space mouse delta converts to canvas-space
                // view-center movement. / 拖动白色视口框：框内鼠标增量换算为画布空间的视口中
                // 心移动。
                Vector2 canvasDelta = new Vector2(e.delta.x, e.delta.y) / Mathf.Max(0.0001f, scale);
                fmScroll -= canvasDelta * fmZoom;
                fmMinimapMoved = true;
            }
            else if (e.type == EventType.MouseUp && e.button == 0)
            {
                if (!fmMinimapMoved)
                {
                    // Plain click centers the view. / 单击居中视图。
                    Vector2 target = (e.mousePosition - box.center) / scale + new Vector2(CanvasWidth * 0.5f, 540f);
                    fmScroll = -target * fmZoom;
                }
                fmMinimapDrag = false;
                fmMinimapMoved = false;
            }
        }

        private void DrawEditorMinimap(Rect canvasRect)
        {
            Rect box = FmMinimapRect(canvasRect);
            float scale = Mathf.Min(box.width / (CanvasWidth + 200f), box.height / (1080f + 200f));
            // Canvas (0,0) maps to the box's top-left area, NOT its center — the mapped region
            // is [−100, CanvasWidth+100] × [−100, 1180] centered on (CanvasWidth/2, 540);
            // forgetting the region-center subtraction shoves everything into the bottom-right
            // quadrant of the box. / 画布 (0,0) 映射到框的左上区域而非中心——映射区域以
            //(CanvasWidth/2, 540) 为中心；漏掉区域中心减法会把所有内容挤进框的右下象限。
            Vector2 regionCenter = new Vector2(CanvasWidth * 0.5f, 540f);
            Vector2 boxCenter = box.center;
            Rect MapRect(Rect r)
            {
                Vector2 a = boxCenter + (new Vector2(r.x, r.y) - regionCenter) * scale;
                return new Rect(a.x, a.y, r.width * scale, r.height * scale);
            }
            DrawRectOutline(MapRect(new Rect(0f, 0f, CanvasWidth, 1080f)), new Color(1f, 0.9f, 0.35f, 0.7f), 1f);
            foreach (FmNode node in EditorDrawOrder())
            {
                if (node.Hidden) continue;
                Color c = editorSelection.Contains(node)
                    ? new Color(1f, 0.82f, 0.15f, 0.95f)
                    : node.NodeType == 3 ? new Color(0.6f, 0.7f, 1f, 0.8f) : new Color(0.75f, 0.75f, 0.78f, 0.8f);
                Rect r = MapRect(new Rect(node.X, node.Y, node.Width, node.Height));
                GUIUtils.DrawRect(new Rect(r.x, r.y, Mathf.Max(1.5f, r.width), Mathf.Max(1.5f, r.height)), c);
            }
            DrawRectOutline(FmMinimapViewportBox(canvasRect, box), new Color(1f, 1f, 1f, 0.85f), 1f);
        }

        // ---- layer groups UI / 图层组 UI ----

        private void DrawEditorGroupManager()
        {
            // Button-style foldout (◢/▶) matching the settings window. /
            // 与设置窗一致的按钮式折叠（◢/▶）。
            fmGroupsExpanded = DrawFoldoutButton(I18n.Tr("fm_groups"), fmGroupsExpanded);
            if (!fmGroupsExpanded) return;
            GUILayout.Label("<i>" + I18n.Tr("fm_groups_hint") + "</i>");
            List<FmLayerGroup> groups = Settings.Data.LayerGroups;
            for (int i = 0; i < groups.Count; i++)
            {
                FmLayerGroup g = groups[i];
                GUILayout.BeginHorizontal();
                string name = TextInputField("fme_g_" + g.Id, g.Name ?? "", GUILayout.MinWidth(90f));
                if (!string.Equals(name, g.Name ?? "", StringComparison.Ordinal))
                {
                    g.Name = name;
                    PushEditorHistoryNudge();
                    SaveSettingsFromGui();
                }
                bool vis = GUILayout.Toggle(g.Visible, I18n.Tr("fm_group_show"), GUILayout.MinWidth(48f));
                if (vis != g.Visible)
                {
                    g.Visible = vis;
                    PushEditorHistory(false);
                    EditorMutated();
                }
                bool isActive = g.Id == fmActiveGroupId;
                string targetTip = isActive ? I18n.Tr("fm_gtip_target_on") : I18n.Tr("fm_gtip_target_off");
                if (GUILayout.Button(new GUIContent(isActive ? "◆" : "◇", targetTip), GUILayout.Width(30f)))
                {
                    fmActiveGroupId = isActive ? "" : g.Id;
                }
                if (GUILayout.Button(new GUIContent("▣", I18n.Tr("fm_gtip_select")), GUILayout.Width(30f)))
                {
                    editorSelection.Clear();
                    foreach (FmNode n in Settings.Data.CustomNodes)
                        if (n != null && n.GroupId == g.Id) editorSelection.Add(n);
                    fmActiveNode = editorSelection.Count > 0 ? editorSelection[0] : null;
                }
                GUI.enabled = editorSelection.Count > 0;
                string assignTip = string.Format(I18n.Tr("fm_gtip_assign"), editorSelection.Count, g.Name);
                if (GUILayout.Button(new GUIContent("＋" + editorSelection.Count, assignTip), GUILayout.Width(52f)))
                {
                    foreach (FmNode n in editorSelection) n.GroupId = g.Id;
                    PushEditorHistory(false);
                    EditorMutated();
                }
                GUI.enabled = true;
                if (GUILayout.Button(new GUIContent("✕", I18n.Tr("fm_gtip_del")), GUILayout.Width(30f)))
                {
                    string deadId = g.Id;
                    groups.RemoveAt(i);
                    foreach (FmNode n in Settings.Data.CustomNodes)
                        if (n != null && n.GroupId == deadId) n.GroupId = "";
                    if (fmActiveGroupId == deadId) fmActiveGroupId = "";
                    PushEditorHistory(false);
                    EditorMutated();
                    break;
                }
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button(I18n.Tr("fm_group_add"), GUILayout.MinWidth(140f)))
            {
                // Id stays monotonic (uniqueness); the NAME reuses the smallest free number —
                // it used to follow the Id counter, so names climbed to "组 18" forever even
                // after deleting old groups. / Id 保持单调（保唯一性）；名称复用最小空闲编号
                // ——此前跟随 Id 计数器，删掉旧组后编号仍一路涨到"组 18"。
                int n = Settings.Data.LayerGroupNextId++;
                string name = I18n.Tr("fm_group") + " " + (groups.Count + 1);
                while (groups.Exists(g => g != null && g.Name == name))
                {
                    int sep = name.LastIndexOf(' ');
                    if (sep < 0) break;
                    if (!int.TryParse(name.Substring(sep + 1), out int num)) break;
                    name = name.Substring(0, sep + 1) + (num + 1);
                }
                groups.Add(new FmLayerGroup { Id = "g" + n, Name = name, Visible = true });
                PushEditorHistory(false);
                SaveSettingsFromGui();
            }
        }

        /// <summary>Incremental drag with a snap correction on top: the accumulated delta is the
        /// raw mouse motion, snapping adds a one-shot correction — the mouse never fights the
        /// snap. / 累计增量式拖拽叠加吸附修正：累计量是原始鼠标运动，吸附加一次性修正——
        /// 鼠标永远不会与吸附打架。</summary>
        private void ApplyEditorDrag(float dx, float dy, bool noSnap)
        {
            if (fmDragStart.Count == 0) return;
            float corrX = 0f, corrY = 0f;
            fmAlignLines.Clear();
            if (!noSnap) EditorSnapDrag(dx, dy, out corrX, out corrY);
            float fx = dx + corrX;
            float fy = dy + corrY;
            foreach (KeyValuePair<FmNode, Vector2> kv in fmDragStart)
            {
                kv.Key.X = kv.Value.x + fx;
                kv.Key.Y = kv.Value.y + fy;
            }
            ApplyLiveGeometry();
        }

        /// <summary>Alignment snap against other nodes' L/C/R + T/C/B and the three screen
        /// lines (left edge / center / right edge of the real screen). Threshold is
        /// screen-constant (5px / zoom). Guide lines are emitted only for edges that are
        /// actually aligned after the correction. / 对其它节点的左/中/右、上/中/下以及真实
        /// 屏幕三条线（左缘/中心/右缘）做对齐吸附。阈值屏幕恒定（5px/zoom）。对齐线只在
        /// 修正后确实对齐的边上输出。</summary>
        /// <summary>Reusable scratch for EditorSnapDrag and EmitAlignLine. Both ran once per dragged
        /// node PER FRAME and allocated a List plus several float[3] each time — with every node
        /// selected on a 112-node document that is hundreds of short-lived arrays per frame.
        /// EditorSnapDrag 与 EmitAlignLine 的复用暂存：二者此前每个被拖节点、每帧都要新建
        /// List 与若干 float[3]——112 节点全选拖动时每帧数百个短命数组。</summary>
        private readonly List<FmNode> fmSnapRefs = new List<FmNode>();
        private readonly float[] fmSnapEdgesX = new float[3];
        private readonly float[] fmSnapEdgesY = new float[3];
        /// <summary>Set by the toolbar's "video" button; consumed on the next Repaint once the
        /// video-path field exists. / 工具栏「视频」按钮置位；下次 Repaint 字段绘出后即消费。</summary>
        private bool fmFocusVideoPathNextPass;

        private void EditorSnapDrag(float dx, float dy, out float corrX, out float corrY)
        {
            corrX = 0f;
            corrY = 0f;
            float snapLimit = 5f / Mathf.Max(0.05f, fmZoom);
            List<FmNode> refs = fmSnapRefs;
            refs.Clear();
            foreach (FmNode node in Settings.Data.CustomNodes)
                if (node != null && !fmDragStart.ContainsKey(node)) refs.Add(node);

            float bestX = float.MaxValue, bestY = float.MaxValue;
            float atX = 0f, atY = 0f;
            FmNode refXNode = null, refYNode = null;

            foreach (FmNode node in fmDragStart.Keys)
            {
                Vector2 start = fmDragStart[node];
                float w = node.Width, h = node.Height;
                float[] edgesX = fmSnapEdgesX;
                float[] edgesY = fmSnapEdgesY;
                edgesX[0] = start.x + dx; edgesX[1] = edgesX[0] + w * 0.5f; edgesX[2] = edgesX[0] + w;
                edgesY[0] = start.y + dy; edgesY[1] = edgesY[0] + h * 0.5f; edgesY[2] = edgesY[0] + h;

                void TryX(float candidate, FmNode owner)
                {
                    foreach (float edge in edgesX)
                    {
                        float diff = candidate - edge;
                        if (Math.Abs(diff) <= snapLimit && Math.Abs(diff) < Math.Abs(bestX))
                        {
                            bestX = diff;
                            atX = candidate;
                            refXNode = owner;
                        }
                    }
                }

                void TryY(float candidate, FmNode owner)
                {
                    foreach (float edge in edgesY)
                    {
                        float diff = candidate - edge;
                        if (Math.Abs(diff) <= snapLimit && Math.Abs(diff) < Math.Abs(bestY))
                        {
                            bestY = diff;
                            atY = candidate;
                            refYNode = owner;
                        }
                    }
                }

                TryX(0f, null);
                TryX(CanvasWidth * 0.5f, null);
                TryX(CanvasWidth, null);
                TryY(0f, null);
                TryY(540f, null);
                TryY(1080f, null);
                foreach (FmNode r in refs)
                {
                    float rw = r.Width, rh = r.Height;
                    TryX(r.X, r);
                    TryX(r.X + rw * 0.5f, r);
                    TryX(r.X + rw, r);
                    TryY(r.Y, r);
                    TryY(r.Y + rh * 0.5f, r);
                    TryY(r.Y + rh, r);
                }
            }

            if (Math.Abs(bestX) <= snapLimit)
            {
                corrX = bestX;
                float fx = dx + corrX, fy = dy;
                EmitAlignLine(true, atX, refXNode, fx, fy);
            }
            if (Math.Abs(bestY) <= snapLimit)
            {
                corrY = bestY;
                float fx = dx, fy = dy + corrY;
                EmitAlignLine(false, atY, refYNode, fx, fy);
            }
        }

        /// <summary>A guide line is drawn only when, after applying the correction, a selected
        /// edge really sits on the reference coordinate; its extent covers both the selected
        /// nodes and the reference node (±10px for screen lines). / 只有应用修正后选中边确实
        /// 落在参考坐标上才画线；线段范围覆盖选中节点与参考节点（屏幕线各延伸 10px）。</summary>
        private void EmitAlignLine(bool vertical, float coord, FmNode refNode, float fx, float fy)
        {
            float min = float.MaxValue, max = float.MinValue;
            bool aligned = false;
            // Reused 3-slot buffer instead of a fresh float[3] per selected node per frame.
            // 复用 3 元素暂存数组，不再每个选中节点每帧新建。
            float[] edges = fmSnapEdgesX;
            foreach (KeyValuePair<FmNode, Vector2> kv in fmDragStart)
            {
                FmNode node = kv.Key;
                float w = node.Width, h = node.Height;
                float x = kv.Value.x + fx, y = kv.Value.y + fy;
                if (vertical) { edges[0] = x; edges[1] = x + w * 0.5f; edges[2] = x + w; }
                else { edges[0] = y; edges[1] = y + h * 0.5f; edges[2] = y + h; }
                foreach (float edge in edges)
                {
                    if (Math.Abs(edge - coord) < 0.01f)
                    {
                        aligned = true;
                        float lo = vertical ? y : x;
                        float hi = vertical ? y + h : x + w;
                        if (lo < min) min = lo;
                        if (hi > max) max = hi;
                    }
                }
            }
            if (!aligned) return;
            if (refNode != null)
            {
                float lo = vertical ? refNode.Y : refNode.X;
                float hi = vertical ? refNode.Y + refNode.Height : refNode.X + refNode.Width;
                if (lo < min) min = lo;
                if (hi > max) max = hi;
            }
            else
            {
                min -= 10f;
                max += 10f;
            }
            fmAlignLines.Add(new FmAlignLine { Vertical = vertical, Coord = coord, Min = min, Max = max });
        }

        // ---- picking / 拣选 ----

        private List<FmNode> EditorDrawOrder()
        {
            fmOrderBuffer.Clear();
            foreach (FmNode node in Settings.Data.CustomNodes)
                if (node != null && node.NodeType == 3) fmOrderBuffer.Add(node);
            foreach (FmNode node in Settings.Data.CustomNodes)
                if (node != null && node.NodeType != 3) fmOrderBuffer.Add(node);
            // Stable sort: images bucket first, then Depth ascending within each bucket. /
            // 稳定排序：图片桶在前，桶内 Depth 升序。
            for (int i = 1; i < fmOrderBuffer.Count; i++)
            {
                FmNode cur = fmOrderBuffer[i];
                int curKey = cur.NodeType == 3 ? 0 : 1;
                int j = i - 1;
                while (j >= 0)
                {
                    FmNode prev = fmOrderBuffer[j];
                    int prevKey = prev.NodeType == 3 ? 0 : 1;
                    if (prevKey < curKey || (prevKey == curKey && prev.Depth <= cur.Depth)) break;
                    fmOrderBuffer[j + 1] = prev;
                    j--;
                }
                fmOrderBuffer[j + 1] = cur;
            }
            return fmOrderBuffer;
        }

        private List<FmNode> HitTestEditorNodes(Vector2 canvasPos, bool includeLocked)
        {
            // Topmost first = reverse draw order. / 最上层优先 = 绘制顺序的逆序。
            List<FmNode> order = EditorDrawOrder();
            List<FmNode> hits = new List<FmNode>();
            for (int i = order.Count - 1; i >= 0; i--)
            {
                FmNode node = order[i];
                if (node.Unselectable && !includeLocked) continue;
                if (canvasPos.x >= node.X && canvasPos.x <= node.X + node.Width
                    && canvasPos.y >= node.Y && canvasPos.y <= node.Y + node.Height)
                    hits.Add(node);
            }
            return hits;
        }

        private FmNode PickEditorNode(List<FmNode> hits, bool doubleClick, bool ctrl)
        {
            if (hits == null || hits.Count == 0) return null;
            if (doubleClick && hits.Count > 1)
            {
                // Cycle through the overlap stack: pick the hit AFTER the currently selected
                // one, wrapping around. / 在重叠栈中循环：选当前选中项之后的那个，环形回绕。
                FmNode sel = hits.FirstOrDefault(h => editorSelection.Contains(h));
                int idx = sel != null ? hits.IndexOf(sel) : -1;
                return hits[(idx + 1 + hits.Count) % hits.Count];
            }
            if (!ctrl)
            {
                FmNode sel = hits.FirstOrDefault(h => editorSelection.Contains(h));
                if (sel != null) return sel;
            }
            return hits[0];
        }

        private void EditorApplyCanvasSelection(FmNode target, bool doubleClick, bool ctrl)
        {
            // Clicking empty canvas deselects (unless Ctrl is held) — the old early-return made
            // it impossible to clear the selection by clicking away. /
            // 点击空白处取消选中（按住 Ctrl 除外）——旧的提前返回导致无法通过点空白取消选中。
            if (target == null)
            {
                if (!ctrl && !doubleClick && editorSelection.Count > 0) editorSelection.Clear();
                fmActiveNode = null;
                return;
            }
            if (doubleClick)
            {
                editorSelection.Clear();
                editorSelection.Add(target);
                fmActiveNode = target;
                return;
            }
            if (ctrl)
            {
                if (!editorSelection.Remove(target))
                {
                    editorSelection.Add(target);
                    fmActiveNode = target; // added → becomes the active node / 新加入 → 成为活动节点
                }
                else if (fmActiveNode == target) fmActiveNode = null; // toggled off / 切换移除
                return;
            }
            if (!editorSelection.Contains(target))
            {
                editorSelection.Clear();
                editorSelection.Add(target);
            }
            fmActiveNode = target;
        }

        private bool ConsumeEditorDoubleClick(Vector2 mousePos)
        {
            float now = Time.unscaledTime;
            Vector2 d = mousePos - fmLastClickPos;
            bool doubleClick = now - fmLastClickTime <= FmDoubleClickTime && d.sqrMagnitude <= FmDoubleClickDist * FmDoubleClickDist;
            fmLastClickTime = now;
            fmLastClickPos = mousePos;
            return doubleClick;
        }

        // ---- canvas rendering / 画布绘制 ----

        private void DrawEditorCanvas(Rect rect)
        {
            GUIUtils.DrawRect(rect, new Color(0.07f, 0.07f, 0.09f, 1f));
            Vector2 origin = EditorOrigin(rect);
            // Grid / 网格
            float step = 50f * fmZoom;
            if (step >= 6f)
            {
                float startX = (origin.x - rect.x) % step;
                if (startX < 0) startX += step;
                for (float x = startX; x < rect.width; x += step)
                    GUIUtils.DrawRect(new Rect(rect.x + x, rect.y, 1f, rect.height), new Color(1f, 1f, 1f, 0.045f));
                float startY = (origin.y - rect.y) % step;
                if (startY < 0) startY += step;
                for (float y = startY; y < rect.height; y += step)
                    GUIUtils.DrawRect(new Rect(rect.x, rect.y + y, rect.width, 1f), new Color(1f, 1f, 1f, 0.045f));
            }
            // Game screen bounds / 游戏屏幕范围
            Rect bounds = new Rect(origin.x, origin.y, CanvasWidth * fmZoom, 1080f * fmZoom);
            Rect clipped = ClipRect(bounds, rect);
            if (clipped.width > 0f && clipped.height > 0f)
            {
                DrawRectOutline(clipped, new Color(1f, 0.9f, 0.35f, 0.75f), 1f);
                if (fmHintStyle == null)
                    fmHintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, normal = { textColor = new Color(1f, 0.9f, 0.35f, 0.85f) } };
                GUI.Label(new Rect(bounds.x + 5f, bounds.y + 3f, 240f, 18f),
                    string.Format(I18n.Tr("fm_screen_bounds"), CanvasWidth.ToString("F0")), fmHintStyle);
            }
            // Nodes / 节点
            foreach (FmNode node in EditorDrawOrder())
            {
                Rect sr = new Rect(origin.x + node.X * fmZoom, origin.y + node.Y * fmZoom,
                    node.Width * fmZoom, node.Height * fmZoom);
                if (!sr.Overlaps(rect)) continue;
                DrawEditorNode(node, sr, rect);
            }
            // Align lines / 对齐线
            foreach (FmAlignLine line in fmAlignLines)
            {
                Color col = new Color(1f, 0.85f, 0.2f, 0.95f);
                if (line.Vertical)
                {
                    float x = origin.x + line.Coord * fmZoom;
                    float y1 = Mathf.Max(rect.y, origin.y + line.Min * fmZoom);
                    float y2 = Mathf.Min(rect.yMax, origin.y + line.Max * fmZoom);
                    if (y2 > y1) GUIUtils.DrawRect(new Rect(x - 0.75f, y1, 1.5f, y2 - y1), col);
                }
                else
                {
                    float y = origin.y + line.Coord * fmZoom;
                    float x1 = Mathf.Max(rect.x, origin.x + line.Min * fmZoom);
                    float x2 = Mathf.Min(rect.xMax, origin.x + line.Max * fmZoom);
                    if (x2 > x1) GUIUtils.DrawRect(new Rect(x1, y - 0.75f, x2 - x1, 1.5f), col);
                }
            }
            // Marquee / 框选
            if (fmGesture == FmGesture.Marquee)
            {
                Rect band = EditorRectFromCorners(fmMarqueeStart, fmMarqueeCur);
                Rect bandScreen = new Rect(origin.x + band.x * fmZoom, origin.y + band.y * fmZoom,
                    band.width * fmZoom, band.height * fmZoom);
                GUIUtils.DrawRect(ClipRect(bandScreen, rect), new Color(1f, 0.92f, 0.4f, 0.12f));
                DrawRectOutline(ClipRect(bandScreen, rect), new Color(1f, 0.92f, 0.4f, 0.9f), 1f);
            }
            // Resize handles / 缩放手柄
            DrawEditorResizeHandles(rect);
        }

        private static Rect ClipRect(Rect r, Rect clip)
        {
            float xMin = Mathf.Max(r.x, clip.x);
            float yMin = Mathf.Max(r.y, clip.y);
            float xMax = Mathf.Min(r.xMax, clip.xMax);
            float yMax = Mathf.Min(r.yMax, clip.yMax);
            if (xMax <= xMin || yMax <= yMin) return new Rect(0, 0, 0, 0);
            return new Rect(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private void DrawRectOutline(Rect r, Color color, float t)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            t = Mathf.Min(t, r.height * 0.5f, r.width * 0.5f);
            GUIUtils.DrawRect(new Rect(r.x, r.y, r.width, t), color);
            GUIUtils.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), color);
            GUIUtils.DrawRect(new Rect(r.x, r.y + t, t, Mathf.Max(0f, r.height - 2f * t)), color);
            GUIUtils.DrawRect(new Rect(r.xMax - t, r.y + t, t, Mathf.Max(0f, r.height - 2f * t)), color);
        }

        // ---- rounded-rect preview / 圆角矩形预览 ----
        // The editor canvas mirrors the runtime box shape, so a radius typed in the panel must be
        // visible on the canvas. IMGUI has no rounded primitive, so the corners are filled with a
        // small per-row span scan (12 rows per corner): cheap, allocation-free, and exact enough
        // at canvas zoom. / 编辑器画布要反映运行时盒子形状，面板里输入的圆角必须能在画布上看到。
        // IMGUI 没有圆角图元，故用逐行跨度扫描填角（每角 12 行）：廉价、零分配，在画布缩放下
        // 足够精确。
        private const int FmCornerRows = 12;

        private static void DrawRoundedRect(Rect r, Color color, float radius)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            float rad = Mathf.Max(0f, Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f));
            if (rad <= 0.5f)
            {
                GUIUtils.DrawRect(r, color);
                return;
            }
            // Middle band (full width) + two side bands minus the corner notches. /
            // 中间整宽带 + 两侧带（扣掉圆角缺口）。
            GUIUtils.DrawRect(new Rect(r.x, r.y + rad, r.width, r.height - 2f * rad), color);
            GUIUtils.DrawRect(new Rect(r.x + rad, r.y, r.width - 2f * rad, rad), color);
            GUIUtils.DrawRect(new Rect(r.x + rad, r.yMax - rad, r.width - 2f * rad, rad), color);
            int rows = FmCornerRows;
            float rowH = rad / rows;
            for (int i = 0; i < rows; i++)
            {
                // Half-width of the circle at this row, measured from the row's outer edge. /
                // 该行处圆的半宽（自该行外缘起算）。
                float dy = rad - (i + 0.5f) * rowH;
                float dx = rad - Mathf.Sqrt(Mathf.Max(0f, rad * rad - dy * dy));
                float w = rad - dx;
                if (w <= 0f) continue;
                float yTop = r.y + i * rowH;
                float yBot = r.yMax - (i + 1) * rowH;
                GUIUtils.DrawRect(new Rect(r.x + dx, yTop, w, rowH), color);
                GUIUtils.DrawRect(new Rect(r.xMax - dx - w, yTop, w, rowH), color);
                GUIUtils.DrawRect(new Rect(r.x + dx, yBot, w, rowH), color);
                GUIUtils.DrawRect(new Rect(r.xMax - dx - w, yBot, w, rowH), color);
            }
        }

        private static void DrawRoundedVerticalGradient(Rect r, Color top, Color bottom, float radius)
        {
            if (r.width <= 0f || r.height <= 0f) return;
            const int steps = 16;
            float rad = Mathf.Max(0f, Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f));
            for (int i = 0; i < steps; i++)
            {
                float y0 = r.yMin + r.height * i / steps;
                float y1 = r.yMin + r.height * (i + 1) / steps;
                float inset = 0f;
                if (rad > 0f)
                {
                    if (y0 < r.yMin + rad)
                    {
                        float dy = rad - (y0 - r.yMin);
                        inset = Mathf.Max(inset, rad - Mathf.Sqrt(Mathf.Max(0f, rad * rad - dy * dy)));
                    }
                    if (y1 > r.yMax - rad)
                    {
                        float dy = rad - (r.yMax - y1);
                        inset = Mathf.Max(inset, rad - Mathf.Sqrt(Mathf.Max(0f, rad * rad - dy * dy)));
                    }
                }
                float t = (i + 0.5f) / steps;
                GUIUtils.DrawRect(new Rect(r.xMin + inset, y0,
                    Mathf.Max(0f, r.width - inset * 2f), y1 - y0), Color.Lerp(bottom, top, t));
            }
        }

        private static void DrawRectOutlineGradient(Rect r, Color top, Color bottom, float width)
        {
            float w = Mathf.Min(width, Mathf.Min(r.width, r.height) * 0.5f);
            if (w <= 0f) return;
            DrawRoundedVerticalGradient(new Rect(r.xMin, r.yMin, r.width, w), top, bottom, 0f);
            DrawRoundedVerticalGradient(new Rect(r.xMin, r.yMax - w, r.width, w), top, bottom, 0f);
            DrawRoundedVerticalGradient(new Rect(r.xMin, r.yMin + w, w, Mathf.Max(0f, r.height - 2f * w)), top, bottom, 0f);
            DrawRoundedVerticalGradient(new Rect(r.xMax - w, r.yMin + w, w, Mathf.Max(0f, r.height - 2f * w)), top, bottom, 0f);
        }

        private void DrawEditorNode(FmNode node, Rect sr, Rect canvasRect)
        {
            bool selected = editorSelection.Contains(node);
            float dim = node.Hidden ? 0.25f : node.Unselectable && !selected ? 0.45f : 1f;
            if (node.UseGlow && node.GlowSize > 0f)
            {
                float size = Mathf.Clamp(node.GlowSize, 0f, 50f) * fmZoom;
                float pad = Mathf.Max(2f, size);
                Color body = node.NodeType == 3
                    ? (node.UseCustomColor ? NodeColor(node.Outline, Settings.Data.Outline) : Settings.Data.Outline)
                    : node.NodeType == 1
                        ? (node.UseCustomColor ? NodeColor(node.Bg, Settings.Data.KpsBackground) : Settings.Data.KpsBackground)
                        : node.NodeType == 2
                            ? (node.UseCustomColor ? NodeColor(node.Bg, Settings.Data.TotalBackground) : Settings.Data.TotalBackground)
                            : node.UseCustomColor ? NodeColor(node.Bg, Settings.Data.Background) : Settings.Data.Background;
                Color glow = node.GlowFollowBody ? body : NodeColor(node.GlowColor, body);
                Color previous = GUI.color;
                GUI.color = new Color(glow.r, glow.g, glow.b, Mathf.Clamp01(node.GlowOpacity) * dim);
                GUI.DrawTexture(new Rect(sr.x - pad, sr.y - pad, sr.width + pad * 2f, sr.height + pad * 2f),
                    GetCustomGlowSprite().texture, ScaleMode.StretchToFill);
                GUI.color = previous;
            }
            if (node.NodeType == 3)
            {
                Texture2D tex = EditorNodeTexture(node);
                if (tex != null)
                {
                    Color prev = GUI.color;
                    GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(node.Opacity) * dim);
                    GUI.DrawTexture(ClipRect(sr, canvasRect), tex, ScaleMode.StretchToFill);
                    GUI.color = prev;
                }
                else
                {
                    GUIUtils.DrawRect(ClipRect(sr, canvasRect), new Color(0.3f, 0.3f, 0.34f, 0.9f * dim));
                }
                // Video nodes cannot be previewed here (the frame only exists in a RenderTexture at
                // runtime), so the canvas marks them with a play glyph and a brighter border — an
                // unmarked video node would be indistinguishable from a broken image node. /
                // 视频节点无法在此预览（画面只在运行时的 RenderTexture 里），故画布用播放符号与更亮
                // 的边框标记它们——不标记的话视频节点和坏掉的图片节点看起来完全一样。
                if (!string.IsNullOrWhiteSpace(node.VideoPath))
                {
                    Rect r = ClipRect(sr, canvasRect);
                    DrawRectOutline(r, WithAlpha(new Color(0.45f, 0.85f, 1f, 1f), dim), 2f);
                    if (fmVideoGlyphStyle == null)
                        fmVideoGlyphStyle = new GUIStyle(GUI.skin.label)
                        {
                            alignment = TextAnchor.MiddleCenter,
                            fontStyle = FontStyle.Bold,
                        };
                    fmVideoGlyphStyle.fontSize = Mathf.Max(9, Mathf.RoundToInt(22f * fmZoom));
                    fmVideoGlyphStyle.normal.textColor = WithAlpha(Color.white, dim);
                    GUI.Label(r, "▶", fmVideoGlyphStyle);
                }
            }
            else
            {
                ProfileData d = Settings.Data;
                Color bg = node.UseCustomColor ? NodeColor(node.Bg, d.Background) : d.Background;
                Color ol = node.UseCustomColor ? NodeColor(node.Outline, d.Outline) : d.Outline;
                Color gradientTop = node.UseBackgroundGradient ? NodeColor(node.BackgroundGradientTop, bg) : bg;
                Color gradientBottom = node.UseBackgroundGradient ? NodeColor(node.BackgroundGradientBottom, bg) : bg;
                Color outlineGradientTop = node.UseOutlineGradient ? NodeColor(node.OutlineGradientTop, ol) : ol;
                Color outlineGradientBottom = node.UseOutlineGradient ? NodeColor(node.OutlineGradientBottom, ol) : ol;
                // Mirror the runtime box shape. Rounded nodes: the outline color fills the
                // rounded rect and the background covers it inset by the border width (the
                // same ring the runtime mesh draws). Square nodes keep the legacy rect +
                // 1.5px marker outline. / 复刻运行时盒子形状。圆角节点：描边色填充圆角矩形，
                // 背景色按边框宽度内缩覆盖（与运行时 mesh 画的是同一个环）。直角节点沿用
                // 原矩形 + 1.5px 标记描边。
                Rect clipped = ClipRect(sr, canvasRect);
                if (node.CornerRadius > 0.5f)
                {
                    float b = node.BorderThickness > 0f ? node.BorderThickness * fmZoom : 1.5f;
                    if (node.UseOutlineGradient)
                        DrawRoundedVerticalGradient(clipped, WithAlpha(outlineGradientTop, dim), WithAlpha(outlineGradientBottom, dim),
                            node.CornerRadius * fmZoom);
                    else
                        DrawRoundedRect(clipped, WithAlpha(ol, dim), node.CornerRadius * fmZoom);
                    Rect inner = new Rect(clipped.x + b, clipped.y + b,
                        Mathf.Max(0f, clipped.width - 2f * b), Mathf.Max(0f, clipped.height - 2f * b));
                    if (node.UseBackgroundGradient)
                        DrawRoundedVerticalGradient(inner, WithAlpha(gradientTop, dim), WithAlpha(gradientBottom, dim),
                            Mathf.Max(0f, node.CornerRadius * fmZoom - b));
                    else
                        DrawRoundedRect(inner, WithAlpha(bg, dim), Mathf.Max(0f, node.CornerRadius * fmZoom - b));
                }
                else
                {
                    // Mirror the runtime square-ring border: custom thickness when set, the
                    // legacy 1.5px marker otherwise. / 复刻运行时直角描边环：有自定义厚度
                    // 用自定义值，否则沿用 1.5px 标记线。
                    float sq = node.BorderThickness > 0f ? node.BorderThickness * fmZoom : 1.5f;
                    if (node.UseBackgroundGradient)
                        DrawRoundedVerticalGradient(clipped, WithAlpha(gradientTop, dim), WithAlpha(gradientBottom, dim), 0f);
                    else
                        GUIUtils.DrawRect(clipped, WithAlpha(bg, dim));
                    if (node.UseOutlineGradient)
                        DrawRectOutlineGradient(clipped, WithAlpha(outlineGradientTop, dim), WithAlpha(outlineGradientBottom, dim), sq);
                    else
                        DrawRectOutline(clipped, WithAlpha(ol, dim), sq);
                }
                string label = node.NodeType == 1
                    ? (string.IsNullOrEmpty(node.CustomText) ? "KPS" : node.CustomText)
                    : node.NodeType == 2
                        ? (string.IsNullOrEmpty(node.CustomText) ? "Total" : node.CustomText)
                        : !string.IsNullOrEmpty(node.CustomText)
                            ? node.CustomText
                            : KeyToString(CustomNodeKeyCode(node));
                if (fmNodeLabelStyle == null)
                    fmNodeLabelStyle = new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        clipping = TextClipping.Overflow,
                        fontStyle = FontStyle.Bold,
                    };
                fmNodeLabelStyle.fontSize = Mathf.Max(7, Mathf.RoundToInt(18f * fmZoom));
                fmNodeLabelStyle.normal.textColor = WithAlpha(d.Text, dim);
                GUI.Label(ClipRect(sr, canvasRect), label, fmNodeLabelStyle);
            }
            if (selected)
            {
                // Multi-select: the ACTIVE node (whose values the property panel shows) gets a
                // distinct cyan, thicker frame; the rest stay yellow. / 多选时活动节点（属性
                // 面板显示其值的那个）用醒目的青色加粗框，其余保持黄色。
                bool active = editorSelection.Count > 1 && fmActiveNode == node;
                DrawRectOutline(new Rect(sr.x - 2f, sr.y - 2f, sr.width + 4f, sr.height + 4f),
                    active ? new Color(0.25f, 0.95f, 1f, 1f) : new Color(1f, 0.82f, 0.15f, 0.95f),
                    active ? 3.5f : 2f);
            }
        }

        /// <summary>Human-readable node name for the panel: key name / custom text / KPS /
        /// Total / image file name — mirrors the canvas label. / 面板用的节点人话名称：键名/
        /// 自定义文本/KPS/Total/图片文件名——与画布标签一致。</summary>
        private static string EditorNodeDisplayName(FmNode node)
        {
            if (node.NodeType == 1) return string.IsNullOrEmpty(node.CustomText) ? "KPS" : node.CustomText;
            if (node.NodeType == 2) return string.IsNullOrEmpty(node.CustomText) ? "Total" : node.CustomText;
            if (node.NodeType == 3)
                return string.IsNullOrWhiteSpace(node.ImagePath) ? I18n.Tr("fm_add_image") : System.IO.Path.GetFileName(node.ImagePath);
            if (!string.IsNullOrEmpty(node.CustomText)) return node.CustomText;
            KeyCode kc = CustomNodeKeyCode(node);
            return kc != KeyCode.None ? KeyToString(kc) : "?";
        }

        private static Color WithAlpha(Color c, float mul)
        {
            c.a *= mul;
            return c;
        }

        private Texture2D EditorNodeTexture(FmNode node)
        {
            string path = ResolveCustomImagePath(node.ImagePath);
            if (path == null) return null;
            // Negative cache, kept OUT of the texture dictionary. Caching a failed load in the same
            // map meant a corrupt or oversized file was re-read from disk and re-logged on EVERY
            // OnGUI repaint (2-3 times a frame, forever).
            // 负缓存与贴图字典分开。此前把加载失败也缓存进同一张表：损坏或超规格的文件会在**每个**
            // OnGUI 重绘（每帧 2-3 次，永远）被重新读盘并重新记一条日志。
            if (fmTexFailures.Contains(path)) return null;
            if (fmTexCache.TryGetValue(path, out Texture2D cached) && cached != null) return cached;
            Texture2D tex = KvImageLoader.LoadTexture(path);
            if (tex == null) fmTexFailures.Add(path);
            else fmTexCache[path] = tex;
            return tex;
        }

        // ---- resize corner / 缩放角 ----

        private void HandleEditorResize(Event e)
        {
            Rect corner = new Rect(editorRect.xMax - 20f, editorRect.yMax - 20f, 20f, 20f);
            if (e.type == EventType.Repaint)
                GUIUtils.DrawRect(new Rect(editorRect.xMax - 12f, editorRect.yMax - 4f, 8f, 2f), new Color(1f, 1f, 1f, 0.35f));
            if (e.type == EventType.MouseDown && e.button == 0 && corner.Contains(e.mousePosition))
            {
                fmResizing = true;
                e.Use();
            }
            // Same lost-MouseUp hazard as the minimap drag: a permanent true here makes EVERY
            // later MouseDrag drag the editor WINDOW to the mouse shape, forever. The release is
            // also restricted to button 0 — an unrelated right/middle release must not end a
            // left-button window drag.
            // 与小地图拖拽同样的丢失 MouseUp 风险：这里永久为真会让此后**每一次** MouseDrag 都
            // 把编辑器窗口拖成鼠标形状，且永不停下。释放事件同时限定为 0 号键——无关的右键/中键
            // 抬起不应结束一次左键窗口拖拽。
            if (fmResizing && e.type != EventType.MouseDown && !Input.GetMouseButton(0)) fmResizing = false;
            if (fmResizing && e.type == EventType.MouseDrag)
            {
                editorRect.width = Mathf.Clamp(e.mousePosition.x - editorRect.x + 12f, FmMinWindowWidth,
                    Mathf.Max(FmMinWindowWidth, Screen.width - editorRect.x));
                editorRect.height = Mathf.Clamp(e.mousePosition.y - editorRect.y + 12f, FmMinWindowHeight,
                    Mathf.Max(FmMinWindowHeight, Screen.height - editorRect.y));
                e.Use();
            }
            if (fmResizing && e.type == EventType.MouseUp && e.button == 0)
            {
                fmResizing = false;
                e.Use();
            }
        }

        // ---- keyboard shortcuts / 快捷键 ----

        // Every text-entry control reachable while the editor is open. Delete/Backspace here
        // DELETES THE SELECTED NODES and persists the change, so a prefix that is missed turns an
        // ordinary text edit into data loss. The colour pickers (cpi_), the settings-style fields
        // (fsf_/kte_/kfte_) and the bind captures all share the same IMGUI focus space.
        // 编辑器打开时可聚焦的所有文本输入控件：此处的 Delete/Backspace 会删除选中节点并立即落盘，
        // 漏掉任何一个前缀都会把普通输入变成数据丢失。取色器（cpi_）与设置页字段共用同一 IMGUI
        // 焦点空间。
        private static readonly string[] EditorTextPrefixes = { "fme_", "cpi_", "fsf_", "kte_", "kfte_", "kv_" };

        private static bool EditorHasFocusedTextField()
        {
            string focused = GUI.GetNameOfFocusedControl();
            if (string.IsNullOrEmpty(focused)) return false;
            for (int i = 0; i < EditorTextPrefixes.Length; i++)
                if (focused.StartsWith(EditorTextPrefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        private void HandleEditorShortcuts(Event e)
        {
            if (EditorHasFocusedTextField()) return;
            if (fmCaptureNode != null) return;
            if (fmCaptureGhostNode != null) return;
            bool ctrl = e.control;
            switch (e.keyCode)
            {
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    // Drop any in-flight gesture FIRST: Delete removes the nodes, and a live
                    // drag/resize would then keep writing coordinates into the removed instances
                    // while the canvas looked frozen.
                    // 先结束进行中的手势：Delete 会移除节点，仍在手势中的拖拽/缩放会继续往已删除
                    // 的实例写坐标，画布看起来像是卡死。
                    ClearEditorInteractionState();
                    EditorDeleteSelection();
                    e.Use();
                    return;
                case KeyCode.Escape:
                    ClearEditorInteractionState();
                    editorSelection.Clear();
                    e.Use();
                    return;
                case KeyCode.Z when ctrl:
                    // Undo/redo swap the whole node list for freshly deserialized instances. A live
                    // gesture still holds the PRE-undo instances in fmDragStart/fmResizeOrig, so
                    // every following frame would write into objects that are no longer in the
                    // document and the node would appear stuck.
                    // 撤销/重做会把整份节点表换成反序列化出的新实例；仍在进行的手势持有的是撤销
                    // 前的旧实例，后续每帧都会往已不在文档里的对象写坐标，表现为节点卡住不动。
                    ClearEditorInteractionState();
                    if (e.shift) EditorRedo();
                    else EditorUndo();
                    e.Use();
                    return;
                case KeyCode.Y when ctrl:
                    ClearEditorInteractionState();
                    EditorRedo();
                    e.Use();
                    return;
                case KeyCode.C when ctrl:
                    EditorCopySelection();
                    e.Use();
                    return;
                case KeyCode.V when ctrl:
                    ClearEditorInteractionState();
                    EditorPaste();
                    e.Use();
                    return;
                case KeyCode.A when ctrl:
                    EditorSelectAll();
                    e.Use();
                    return;
            }
            // Arrow-key nudge (IMGUI repeats KeyDown while held — the OS repeat does the job). /
            // 方向键微调（按住时 IMGUI 重复派发 KeyDown——系统重复即够用）。
            float dx = 0f, dy = 0f;
            switch (e.keyCode)
            {
                case KeyCode.LeftArrow: dx = -1f; break;
                case KeyCode.RightArrow: dx = 1f; break;
                case KeyCode.UpArrow: dy = -1f; break;
                case KeyCode.DownArrow: dy = 1f; break;
                default: return;
            }
            if (editorSelection.Count == 0) return;
            foreach (FmNode node in editorSelection)
            {
                node.X += dx;
                node.Y += dy;
            }
            e.Use();
            PushEditorHistoryNudge();
            SaveSettingsFromGui();
            RequestEditorRebuild();
        }

        // ---- property panel / 属性面板 ----

        private void DrawEditorProperties()
        {
            if (editorSelection.Count == 0)
            {
                GUILayout.Label(I18n.Tr("fm_no_selection_hint"));
                DrawEditorGroupManager();
                return;
            }
            // Panel shows the ACTIVE node's values — the last clicked one; marquee/select-all
            // (no click order) fall back to the list head. / 面板显示活动节点的值——最后点击
            // 的那个；框选/全选（无点击顺序）回落到列表首项。
            if (fmActiveNode == null || !editorSelection.Contains(fmActiveNode)) fmActiveNode = editorSelection[0];
            FmNode first = fmActiveNode;
            bool single = editorSelection.Count == 1;

            GUILayout.Label(string.Format(I18n.Tr("fm_selected_count"), editorSelection.Count));
            if (!single)
                GUILayout.Label("<i>" + string.Format(I18n.Tr("fm_shown_node"), EditorNodeDisplayName(first)) + "</i>");

            if (single)
            {
                DrawEditorNodeTypeCombo(first);
                DrawEditorKeyBindCapture(first);
                if (first.NodeType == 0 || first.NodeType == 3)
                    DrawEditorGhostBindCapture(first);
            }

            // X/Y are relative edits: the typed value is a DELTA from the active node applied to
            // every selected node, so the displayed basis and the delta basis must be the SAME
            // node. They used to differ (field showed selection[0], delta used the active node) —
            // after a Ctrl-click multi-select the panel displayed one node's X while typing
            // applied "v - <other node>.X", shifting the whole selection somewhere unexpected.
            // X/Y 是相对编辑：输入值是与活动节点的差值，施加到每个选中节点，因此显示基准与差值
            // 基准必须是同一个节点。此前两者不同（字段显示 selection[0]，差值用活动节点）——
            // Ctrl 多选后面板显示的是一个节点的 X，输入却按另一个节点的 X 求差，整组会跳到
            // 意料之外的位置。
            DrawEditorFloatField(I18n.Tr("fm_pos_x"), "fme_x_" + first.Id, n => n.X, v =>
            {
                float delta = v - first.X;
                foreach (FmNode n in editorSelection) n.X += delta;
                EditorPropertyChanged();
            }, "fm_help_pos_x", first);
            DrawEditorFloatField(I18n.Tr("fm_pos_y"), "fme_y_" + first.Id, n => n.Y, v =>
            {
                float delta = v - first.Y;
                foreach (FmNode n in editorSelection) n.Y += delta;
                EditorPropertyChanged();
            }, "fm_help_pos_y", first);
            DrawEditorFloatField(I18n.Tr("fm_width"), "fme_w_" + first.Id, n => n.Width, v =>
            {
                foreach (FmNode n in editorSelection) n.Width = Mathf.Max(10f, v);
                EditorPropertyChanged();
            }, "fm_help_width");
            DrawEditorFloatField(I18n.Tr("fm_height"), "fme_h_" + first.Id, n => n.Height, v =>
            {
                foreach (FmNode n in editorSelection) n.Height = Mathf.Max(10f, v);
                EditorPropertyChanged();
            }, "fm_help_height");

            // Box shape: corner radius + border thickness. Radius > 0 switches the slot to the
            // procedural rounded mesh; both values apply to every box-drawing node type (keys,
            // KPS/Total panels) but never to image nodes, which draw no box at all. /
            // 盒子形状：圆角半径 + 边框厚度。半径 > 0 会把槽位切到程序化圆角 mesh；两个值对
            // 所有会画盒子的节点类型生效（按键、KPS/Total 面板），但绝不作用于完全不画盒子的
            // 图片节点。
            if (first.NodeType != 3)
            {
                DrawEditorFloatField(I18n.Tr("fm_corner_radius"), "fme_cr_" + first.Id, n => n.CornerRadius, v =>
                {
                    foreach (FmNode n in editorSelection) n.CornerRadius = Mathf.Max(0f, v);
                    EditorPropertyChanged();
                }, "fm_help_corner_radius");
                DrawEditorFloatField(I18n.Tr("fm_border_thickness"), "fme_bt_" + first.Id, n => n.BorderThickness, v =>
                {
                    foreach (FmNode n in editorSelection) n.BorderThickness = Mathf.Max(0f, v);
                    EditorPropertyChanged();
                }, "fm_help_border_thickness");
            }

            DrawEditorTextStyleSection(first);

            // Per-node count formatting (thousands separator): off = follow the global toggle;
            // opting in seeds the flag from the CURRENT global so enabling never changes the
            // rendered number. Applies to this node's own count, and on KPS/Total nodes to the
            // panel value. / 节点级计数格式（千分位）：关 = 跟随全局开关；开启时从当前全局值
            // 播种，使开启动作本身不改变已显示的数字。作用于本节点计数；KPS/Total 节点上
            // 作用于面板数值。
            DrawEditorToggle(I18n.Tr("fm_count_format_custom"), first.UseCustomCountFormat, v =>
            {
                foreach (FmNode n in editorSelection)
                {
                    n.UseCustomCountFormat = v;
                    if (v) n.CountThousandsSeparator = Settings.Data.EnableCountFormatting;
                }
                EditorPropertyChanged();
            });
            if (first.UseCustomCountFormat)
            {
                DrawEditorToggle(I18n.Tr("count_formatting"), first.CountThousandsSeparator, v =>
                {
                    foreach (FmNode n in editorSelection) n.CountThousandsSeparator = v;
                    EditorPropertyChanged();
                });
            }

            // Press-animation easing, per node / 节点级按压动画缓动
            DrawEditorToggle(I18n.Tr("fm_press_easing_custom"), first.UseCustomPressEasing, v =>
            {
                foreach (FmNode n in editorSelection)
                {
                    n.UseCustomPressEasing = v;
                    if (v)
                    {
                        n.PressAnimEasing = Settings.Data.PressAnimationEasing;
                        n.PressAnimDurationMs = Settings.Data.PressAnimationDurationMs;
                    }
                }
            }, "fm_help_press_easing_custom");
            if (first.UseCustomPressEasing)
            {
                DrawEditorEasingSelector("fme_ease_" + first.Id, first.PressAnimEasing, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressAnimEasing = v;
                });
                DrawEditorFloatField(I18n.Tr("press_anim_duration"), "fme_pad_" + first.Id, n => n.PressAnimDurationMs, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressAnimDurationMs = Mathf.Clamp(v, 10f, 2000f);
                }, "fm_help_press_easing_custom");
            }

            int depth = first.Depth;
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.Tr("fm_depth"), GUILayout.Width(96f));
            DrawEditorHelpMarker("fm_help_depth");
            int newDepth = Mathf.RoundToInt(GUILayout.HorizontalSlider(depth, 0, 60));
            // Mixed depths show "—" (never parses → no accidental mass-apply); a deliberate
            // slider drag or typed number still applies to all. / 深度不一致时显示"—"（永不解析
            // →不会意外群发）；有意拖滑杆或输入数字仍然应用到全部。
            bool depthMixed = false;
            for (int i = 1; i < editorSelection.Count; i++)
                if (editorSelection[i].Depth != first.Depth) { depthMixed = true; break; }
            string depthText = TextInputField("fme_d_" + first.Id, depthMixed ? "—" : newDepth.ToString(), GUILayout.Width(56f));
            // Strip the mixed marker so typing over "—" parses (see DrawEditorFloatField). /
            // 剥掉混合标记，直接在"—"后输入也能解析（见 DrawEditorFloatField）。
            if (int.TryParse(depthText.Replace("—", "").Trim(), out int parsedDepth)) newDepth = Mathf.Clamp(parsedDepth, 0, 60);
            GUILayout.EndHorizontal();
            DrawEditorHelpBox("fm_help_depth");
            // A slider drag is deliberate intent on its own — it must NOT require the text box to
            // parse first (mixed "—" used to block dragging until the dash was deleted). Untouched
            // mixed selections stay safe: the slider sits at the active node's value → equal → no
            // apply. / 拖动滑杆本身就是明确意图——不应要求文本框先可解析（此前混合"—"会把滑杆
            // 堵到先删横线为止）。未触碰的混合选区仍然安全：滑杆停在活动节点的值上 → 相等 →
            // 不应用。
            if (newDepth != first.Depth)
            {
                foreach (FmNode n in editorSelection) n.Depth = newDepth;
                EditorPropertyChanged();
            }

            // Custom/pressed text + count toggles: keys AND bound image keys (an image key
            // renders label/count like a key — its runtime consumes CustomText, PressedText,
            // CountInTotal and PerKeyKps identically). / 自定义/按压文案 + 计数开关：按键与
            // 绑定了按键的图片按键通用（图片按键与按键同样渲染标签/计数——运行时对
            // CustomText、PressedText、CountInTotal、PerKeyKps 的消费完全一致）。
            if (first.NodeType == 1 || first.NodeType == 2)
            {
                // Stat panels have their own runtime label; unlike key nodes they do not use
                // PressedText/CountInTotal/PerKeyKps. / 统计面板拥有独立运行时标签；不同于按键节点，
                // 不显示按压文案、CountInTotal 或 PerKeyKps。
                DrawEditorTextField(I18n.Tr("fm_custom_text"), "fme_ct_" + first.Id, first.CustomText, v =>
                {
                    foreach (FmNode n in editorSelection) n.CustomText = v;
                    EditorPropertyChanged();
                }, "fm_help_custom_text");
            }
            else if (first.NodeType == 0 || (first.NodeType == 3 && !string.IsNullOrWhiteSpace(first.KeyBind)))
            {
                DrawEditorTextField(I18n.Tr("fm_custom_text"), "fme_ct_" + first.Id, first.CustomText, v =>
                {
                    foreach (FmNode n in editorSelection) n.CustomText = v;
                    EditorPropertyChanged();
                }, "fm_help_custom_text");
                DrawEditorTextField(I18n.Tr("fm_pressed_text"), "fme_pt_" + first.Id, first.PressedText, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressedText = v;
                    EditorPropertyChanged();
                }, "fm_help_pressed_text");
                DrawEditorToggle(I18n.Tr("fm_count_in_total"), first.CountInTotal, v => { foreach (FmNode n in editorSelection) n.CountInTotal = v; }, "fm_help_count_in_total");
                DrawEditorToggle(I18n.Tr("fm_per_key_kps"), first.PerKeyKps, v => { foreach (FmNode n in editorSelection) n.PerKeyKps = v; }, "fm_help_per_key_kps");
            }
            if (first.NodeType == 3)
            {
                DrawEditorTextField(I18n.Tr("fm_image_path"), "fme_img_" + first.Id, first.ImagePath, v =>
                {
                    foreach (FmNode n in editorSelection) n.ImagePath = v;
                    EditorPropertyChanged();
                }, "fm_help_image_path");
                DrawEditorTextField(I18n.Tr("fm_pressed_image"), "fme_imgp_" + first.Id, first.ImagePathPressed, v =>
                {
                    foreach (FmNode n in editorSelection) n.ImagePathPressed = v;
                    EditorPropertyChanged();
                }, "fm_help_pressed_image");
                string videoCtrl = "fme_vid_" + first.Id;
                // The toolbar's "video" button asks for focus here so the user can type the file
                // immediately instead of hunting for the field in a long property panel. Only on
                // the Repaint pass after creation, and only while the user is not typing.
                // 工具栏的「视频」按钮会请求在此聚焦，让用户直接输入文件而不必在长属性面板里
                // 找该字段。仅在创建后的下一次 Repaint 生效，且用户未在输入其它字段时。
                if (fmFocusVideoPathNextPass && Event.current.type == EventType.Repaint && !EditorHasFocusedTextField())
                {
                    GUI.FocusControl(videoCtrl);
                    fmFocusVideoPathNextPass = false;
                }
                DrawEditorTextField(I18n.Tr("fm_video_path"), videoCtrl, first.VideoPath, v =>
                {
                    foreach (FmNode n in editorSelection) n.VideoPath = v;
                    EditorPropertyChanged();
                }, "fm_help_video_path");
                if (!string.IsNullOrWhiteSpace(first.VideoPath))
                {
                    DrawEditorToggle(I18n.Tr("fm_video_loop"), first.VideoLoop, v =>
                    {
                        foreach (FmNode n in editorSelection) n.VideoLoop = v;
                        EditorPropertyChanged();
                    }, "fm_help_video_loop");
                    // A video node's press texture still comes from the PNG pair; say so instead of
                    // letting the user assume the video swaps on press. / 视频节点的按压贴图仍来自
                    // PNG 对；明确说明，避免用户误以为按下会切换视频。
                    GUILayout.Label(I18n.Tr("fm_video_press_hint"));
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(I18n.Tr("fm_import"), GUILayout.MinWidth(90f))) EditorImportImages();
                if (GUILayout.Button(I18n.Tr("fm_open_dir"), GUILayout.MinWidth(90f))) OpenCustomImagesDir();
                GUILayout.EndHorizontal();
                DrawEditorFloatField(I18n.Tr("fm_opacity"), "fme_o_" + first.Id, n => n.Opacity, v =>
                {
                    foreach (FmNode n in editorSelection) n.Opacity = Mathf.Clamp01(v);
                    EditorPropertyChanged();
                }, "fm_help_opacity");
                if (!single)
                    GUILayout.Label(I18n.Tr("fm_bind_image_hint"));
            }
            // Stat nodes: centered/stacked toggles sit DIRECTLY on the panel (like the Display
            // tab) — clicking one takes over from the current global state automatically; no
            // master switch to discover first. "Follow global" resets the override. /
            // 面板节点：居中/堆叠开关直接平铺（与显示页同款）——点按即以当前全局状态为基准
            // 自动接管，无需先找总开关。「跟随全局」重置回跟随。
            if (first.NodeType == 1 || first.NodeType == 2)
            {
                // Display values: the override's when active, the global's when following. /
                // 显示值：接管时用节点值，跟随时用全局值。
                bool effCentered = first.UseCustomStatLayout ? first.StatCentered : KpsTotalCenteredApplies();
                bool effStacked = first.UseCustomStatLayout ? first.StatStacked : KpsTotalStackedApplies();
                void EnableOverride()
                {
                    foreach (FmNode n in editorSelection)
                    {
                        if (n.NodeType != 1 && n.NodeType != 2) continue;
                        if (n.UseCustomStatLayout) continue; // already independent / 已独立
                        n.StatCentered = KpsTotalCenteredApplies();
                        n.StatStacked = KpsTotalStackedApplies();
                        n.HideLabel = !KpsTotalIsSlim() && Settings.Data.HideKpsTotalLabel;
                        n.UseCustomStatLayout = true;
                    }
                }
                GUILayout.BeginHorizontal();
                bool newCentered = GUILayout.Toggle(effCentered, I18n.Tr("fk_kps_total_centered"));
                DrawEditorHelpMarker("fm_help_stat_layout");
                GUILayout.EndHorizontal();
                DrawEditorHelpBox("fm_help_stat_layout");
                if (newCentered != effCentered)
                {
                    EnableOverride();
                    foreach (FmNode n in editorSelection)
                        if (n.NodeType == 1 || n.NodeType == 2) n.StatCentered = newCentered;
                    EditorPropertyChanged();
                }
                if (newCentered)
                {
                    bool newStacked = GUILayout.Toggle(effStacked, I18n.Tr("kps_total_stacked"));
                    if (newStacked != effStacked)
                    {
                        EnableOverride();
                        foreach (FmNode n in editorSelection)
                            if (n.NodeType == 1 || n.NodeType == 2) n.StatStacked = newStacked;
                        EditorPropertyChanged();
                    }
                }
                if (first.UseCustomStatLayout)
                {
                    if (GUILayout.Button(I18n.Tr("fm_stat_follow_global"), GUILayout.MinWidth(110f)))
                    {
                        foreach (FmNode n in editorSelection)
                            if (n.NodeType == 1 || n.NodeType == 2) n.UseCustomStatLayout = false;
                        EditorPropertyChanged();
                    }
                }
                else
                {
                    GUILayout.Label("<i>" + I18n.Tr("fm_stat_following_global") + "</i>");
                }
            }
            // Rain: keys and image keys (an image needs a binding to have a trigger source). /
            // 雨滴：按键与图片按键（图片需绑定按键才有触发源）。
            bool rainCapable = first.NodeType == 0 || (first.NodeType == 3 && !string.IsNullOrWhiteSpace(first.KeyBind));
            if (rainCapable)
            {
                GUILayout.Label(I18n.Tr("fm_rain_row"));
                GUILayout.BeginHorizontal();
                string[] rainRowNames = { I18n.Tr("rain_row1"), I18n.Tr("rain_row2"), I18n.Tr("rain_row3") };
                int rainRow = GUILayout.SelectionGrid(Mathf.Clamp(first.RainRow, 0, 2), rainRowNames, 3, GUILayout.Height(20f));
                DrawEditorHelpMarker("fm_help_rain_row");
                GUILayout.EndHorizontal();
                DrawEditorHelpBox("fm_help_rain_row");
                GUILayout.Label(I18n.Tr("fm_rain_alignment"));
                string[] rainAlignments = { I18n.Tr("fm_rain_align_left"), I18n.Tr("fm_rain_align_center"), I18n.Tr("fm_rain_align_right") };
                int rainAlignment = GUILayout.SelectionGrid(Mathf.Clamp(first.RainAlignment, 0, 2), rainAlignments, 3);
                if (rainAlignment != first.RainAlignment)
                {
                    foreach (FmNode n in editorSelection) n.RainAlignment = rainAlignment;
                    EditorPropertyChanged();
                }
                if (rainRow >= 0 && rainRow <= 2 && rainRow != first.RainRow)
                {
                    foreach (FmNode n in editorSelection) n.RainRow = rainRow;
                    EditorPropertyChanged();
                }
                DrawEditorToggle(I18n.Tr("fm_rain"), first.RainEnabled, v => { foreach (FmNode n in editorSelection) n.RainEnabled = v; }, "fm_help_rain_enable");
                DrawEditorFloatField(I18n.Tr("rain_width"), "fme_rw_" + first.Id, n => n.RainWidth, v =>
                {
                    foreach (FmNode n in editorSelection) n.RainWidth = Mathf.Max(0f, v);
                    EditorPropertyChanged();
                }, "fm_help_rain_params");
                DrawEditorFloatField(I18n.Tr("rain_height"), "fme_rh_" + first.Id, n => n.RainHeight, v =>
                {
                    foreach (FmNode n in editorSelection) n.RainHeight = Mathf.Max(0f, v);
                    EditorPropertyChanged();
                }, "fm_help_rain_params");
                DrawEditorFloatField(I18n.Tr("rain_speed"), "fme_rs_" + first.Id, n => n.RainSpeed, v =>
                {
                    foreach (FmNode n in editorSelection) n.RainSpeed = Mathf.Max(0f, v);
                    EditorPropertyChanged();
                }, "fm_help_rain_params");
                DrawEditorToggle(I18n.Tr("fm_use_custom_rain_color"), first.UseCustomRainColor, v =>
                {
                    foreach (FmNode n in editorSelection) n.UseCustomRainColor = v;
                }, "fm_help_rain_color");
                if (first.UseCustomRainColor)
                {
                    // Two-color rain: separate top/bottom ends. /
                    // 雨滴双色：顶/底两端独立取色。
                    Color rowColor = rainSystem.GetRainColor(CustomRainRowByte(first));
                    DrawEditorColorField(I18n.Tr("fm_rain_color_top"), first.RainColorTop, rowColor, arr => { foreach (FmNode n in editorSelection) n.RainColorTop = arr; });
                    DrawEditorColorField(I18n.Tr("fm_rain_color_bottom"), first.RainColorBottom, rowColor, arr => { foreach (FmNode n in editorSelection) n.RainColorBottom = arr; });
                }
                DrawEditorFloatField(I18n.Tr("fm_rain_offset_x"), "fme_rox_" + first.Id, n => n.RainOffsetX, v =>
                {
                    foreach (FmNode n in editorSelection) n.RainOffsetX = Mathf.Clamp(v, -2000f, 2000f);
                    EditorPropertyChanged();
                }, "fm_help_rain_params");
                DrawEditorFloatField(I18n.Tr("fm_rain_offset_y"), "fme_roy_" + first.Id, n => n.RainOffsetY, v =>
                {
                    foreach (FmNode n in editorSelection) n.RainOffsetY = Mathf.Clamp(v, -2000f, 2000f);
                    EditorPropertyChanged();
                }, "fm_help_rain_params");
                // Per-node trail-top fade + release fade (the Rain tab's 顶部渐隐/松开淡出 sunk
                // to the node). / 节点级顶部渐隐与松开淡出（雨线页对应设置下沉到节点）。
                DrawEditorToggle(I18n.Tr("fm_rain_fade_custom"), first.UseCustomRainFade, v =>
                {
                    foreach (FmNode n in editorSelection)
                    {
                        n.UseCustomRainFade = v;
                        if (v) SeedRainFadeFromGlobals(n);
                    }
                }, "fm_help_rain_fade");
                if (first.UseCustomRainFade)
                {
                    DrawEditorToggle(I18n.Tr("rain_gradient"), first.TrailFadeEnabled, v => { foreach (FmNode n in editorSelection) n.TrailFadeEnabled = v; }, "fm_help_rain_fade");
                    DrawEditorFloatField(I18n.Tr("gradient_percent"), "fme_tfp_" + first.Id, n => n.TrailFadePx, v =>
                    {
                        foreach (FmNode n in editorSelection) n.TrailFadePx = Mathf.Clamp(v, 0f, 500f);
                        EditorPropertyChanged();
                    }, "fm_help_rain_fade");
                    DrawEditorToggle(I18n.Tr("rain_fade"), first.ReleaseFadeEnabled, v => { foreach (FmNode n in editorSelection) n.ReleaseFadeEnabled = v; }, "fm_help_rain_fade");
                    DrawEditorFloatField(I18n.Tr("fade_duration"), "fme_rfd_" + first.Id, n => n.ReleaseFadeDuration, v =>
                    {
                        foreach (FmNode n in editorSelection) n.ReleaseFadeDuration = Mathf.Clamp(v, 0f, 5f);
                        EditorPropertyChanged();
                    }, "fm_help_rain_fade");
                }
                // Per-node shadow/outline overrides (the Rain tab's shadow/outline rows sunk to
                // the node; off → follow the selected row). / 节点级阴影/描边覆盖（雨线页的
                // 阴影/描边设置下沉到节点；关闭 → 跟随所选排）。
                DrawEditorToggle(I18n.Tr("fm_rain_shadow_custom"), first.UseCustomRainShadow, v =>
                {
                    foreach (FmNode n in editorSelection)
                    {
                        n.UseCustomRainShadow = v;
                        // Seed from the node's row so enabling takes over the CURRENT look — the
                        // unseeded state mixed node-default enable/offsets with row colors, which
                        // read as "the shadow ignores the per-node setting". / 从节点所在排做种子，
                        // 开启即接管当前外观——未做种子时节点默认开关/偏移与排颜色混搭，看起来
                        // 就是"阴影无视节点设置"。
                        if (v) SeedRainShadowFromRow(n);
                    }
                }, "fm_help_rain_shadow");
                if (first.UseCustomRainShadow)
                {
                    DrawEditorToggle(I18n.Tr("rain_shadow"), first.RainShadowEnabled, v => { foreach (FmNode n in editorSelection) n.RainShadowEnabled = v; }, "fm_help_rain_shadow");
                    DrawEditorColorField(I18n.Tr("rain_shadow_color"), first.RainShadowColor, new Color(0f, 0f, 0f, 0.35f), arr => { foreach (FmNode n in editorSelection) n.RainShadowColor = arr; });
                    DrawEditorFloatField("X", "fme_rshx_" + first.Id, n => n.RainShadowOffsetX, v =>
                    {
                        foreach (FmNode n in editorSelection) n.RainShadowOffsetX = Mathf.Clamp(v, -50f, 50f);
                        EditorPropertyChanged();
                    }, "fm_help_rain_shadow");
                    DrawEditorFloatField("Y", "fme_rshy_" + first.Id, n => n.RainShadowOffsetY, v =>
                    {
                        foreach (FmNode n in editorSelection) n.RainShadowOffsetY = Mathf.Clamp(v, -50f, 50f);
                        EditorPropertyChanged();
                    }, "fm_help_rain_shadow");
                }
                DrawEditorToggle(I18n.Tr("fm_rain_outline_custom"), first.UseCustomRainOutline, v =>
                {
                    foreach (FmNode n in editorSelection)
                    {
                        n.UseCustomRainOutline = v;
                        if (v) SeedRainOutlineFromRow(n);
                    }
                }, "fm_help_rain_outline");
                if (first.UseCustomRainOutline)
                {
                    DrawEditorToggle(I18n.Tr("rain_outline"), first.RainOutlineEnabled, v => { foreach (FmNode n in editorSelection) n.RainOutlineEnabled = v; }, "fm_help_rain_outline");
                    DrawEditorColorField(I18n.Tr("rain_outline_color"), first.RainOutlineColor, new Color(1f, 1f, 1f, 0.5f), arr => { foreach (FmNode n in editorSelection) n.RainOutlineColor = arr; });
                    DrawEditorFloatField(I18n.Tr("rain_outline_width"), "fme_row_" + first.Id, n => n.RainOutlineWidth, v =>
                    {
                        foreach (FmNode n in editorSelection) n.RainOutlineWidth = Mathf.Clamp(v, 0f, 50f);
                        EditorPropertyChanged();
                    }, "fm_help_rain_outline");
                }
                DrawEditorToggle(I18n.Tr("fm_rain_corner_custom"), first.UseCustomRainCornerRadius, v =>
                {
                    foreach (FmNode n in editorSelection)
                    {
                        n.UseCustomRainCornerRadius = v;
                        if (v) n.RainCornerRadius = Settings.Data.RainOutlineCornerRadius;
                    }
                }, "fm_help_rain_corner");
                if (first.UseCustomRainCornerRadius)
                {
                    DrawEditorFloatField(I18n.Tr("fm_rain_corner_radius"), "fme_rcr_" + first.Id, n => n.RainCornerRadius, v =>
                    {
                        foreach (FmNode n in editorSelection) n.RainCornerRadius = Mathf.Clamp(v, 0f, 20f);
                    }, "fm_help_rain_corner");
                }
                DrawEditorToggle(I18n.Tr("fm_rain_border_sides_custom"), first.UseCustomRainBorderSides, v =>
                {
                    foreach (FmNode n in editorSelection) n.UseCustomRainBorderSides = v;
                }, "fm_help_rain_border_sides");
                if (first.UseCustomRainBorderSides)
                {
                    GUILayout.Label(I18n.Tr("rain_outline_sides"));
                    string[] sides = { I18n.Tr("rain_side_all"), I18n.Tr("rain_side_vertical"), I18n.Tr("rain_side_horizontal") };
                    int selectedSides = GUILayout.SelectionGrid(Mathf.Clamp(first.RainBorderSides, 0, 2), sides, 3);
                    if (selectedSides != first.RainBorderSides)
                    {
                        foreach (FmNode n in editorSelection) n.RainBorderSides = selectedSides;
                        EditorPropertyChanged();
                    }
                }
                DrawEditorToggle(I18n.Tr("fm_rain_dotted_custom"), first.UseCustomRainDotted, v =>
                {
                    foreach (FmNode n in editorSelection)
                    {
                        n.UseCustomRainDotted = v;
                        if (v)
                        {
                            n.RainDotLength = Settings.Data.RainDotLength;
                            n.RainGapLength = Settings.Data.RainGapLength;
                        }
                    }
                }, "fm_help_rain_dotted");
                if (first.UseCustomRainDotted)
                {
                    DrawEditorFloatField(I18n.Tr("fm_rain_dot_length"), "fme_rdl_" + first.Id, n => n.RainDotLength, v =>
                    {
                        // 0 = dotted OFF for this node (the only way to keep the global toggle on
                        // everywhere else). / 0 = 该节点关闭点状（全局仍可为其它节点开启）。
                        foreach (FmNode n in editorSelection) n.RainDotLength = Mathf.Clamp(v, 0f, 100f);
                    }, "fm_help_rain_dotted");
                    DrawEditorFloatField(I18n.Tr("fm_rain_gap_length"), "fme_rgl_" + first.Id, n => n.RainGapLength, v =>
                    {
                        foreach (FmNode n in editorSelection) n.RainGapLength = Mathf.Clamp(v, 0f, 100f);
                    }, "fm_help_rain_dotted");
                }
                // Per-node GHOST rain shadow/outline — only meaningful with a ghost key bound. /
                // 节点级鬼雨阴影/描边——仅在绑定了鬼键时有意义。
                if (!string.IsNullOrWhiteSpace(first.GhostKey))
                {
                    DrawEditorToggle(I18n.Tr("fm_ghost_rain_params_custom"), first.UseCustomGhostRainParams, v =>
                    {
                        foreach (FmNode n in editorSelection)
                        {
                            n.UseCustomGhostRainParams = v;
                            if (v) SeedGhostRainParams(n);
                        }
                    }, "fm_help_ghost_rain");
                    if (first.UseCustomGhostRainParams)
                    {
                        DrawEditorFloatField(I18n.Tr("rain_width"), "fme_grw_" + first.Id, n => n.GhostRainWidth, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainWidth = Mathf.Max(0f, v);
                            EditorPropertyChanged();
                        }, "fm_help_ghost_rain");
                        DrawEditorFloatField(I18n.Tr("rain_height"), "fme_grh_" + first.Id, n => n.GhostRainHeight, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainHeight = Mathf.Max(0f, v);
                            EditorPropertyChanged();
                        }, "fm_help_ghost_rain");
                        DrawEditorFloatField(I18n.Tr("rain_speed"), "fme_grs_" + first.Id, n => n.GhostRainSpeed, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainSpeed = Mathf.Max(0f, v);
                            EditorPropertyChanged();
                        }, "fm_help_ghost_rain");
                        DrawEditorFloatField(I18n.Tr("fm_rain_offset_x"), "fme_grox_" + first.Id, n => n.GhostRainOffsetX, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainOffsetX = Mathf.Clamp(v, -2000f, 2000f);
                            EditorPropertyChanged();
                        }, "fm_help_ghost_rain");
                        DrawEditorFloatField(I18n.Tr("fm_rain_offset_y"), "fme_groy_" + first.Id, n => n.GhostRainOffsetY, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainOffsetY = Mathf.Clamp(v, -2000f, 2000f);
                            EditorPropertyChanged();
                        }, "fm_help_ghost_rain");
                    }
                    DrawEditorToggle(I18n.Tr("fm_ghost_rain_shadow_custom"), first.UseCustomGhostRainShadow, v =>
                    {
                        foreach (FmNode n in editorSelection)
                        {
                            n.UseCustomGhostRainShadow = v;
                            if (v) SeedGhostRainShadowFromRow(n);
                        }
                    }, "fm_help_ghost_rain");
                    if (first.UseCustomGhostRainShadow)
                    {
                        DrawEditorToggle(I18n.Tr("rain_shadow"), first.GhostRainShadowEnabled, v => { foreach (FmNode n in editorSelection) n.GhostRainShadowEnabled = v; }, "fm_help_ghost_rain");
                        DrawEditorColorField(I18n.Tr("rain_shadow_color"), first.GhostRainShadowColor, new Color(0f, 0f, 0f, 0.35f), arr => { foreach (FmNode n in editorSelection) n.GhostRainShadowColor = arr; });
                        DrawEditorFloatField("X", "fme_grshx_" + first.Id, n => n.GhostRainShadowOffsetX, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainShadowOffsetX = Mathf.Clamp(v, -50f, 50f);
                            EditorPropertyChanged();
                        }, "fm_help_ghost_rain");
                        DrawEditorFloatField("Y", "fme_grshy_" + first.Id, n => n.GhostRainShadowOffsetY, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainShadowOffsetY = Mathf.Clamp(v, -50f, 50f);
                            EditorPropertyChanged();
                        }, "fm_help_ghost_rain");
                    }
                    DrawEditorToggle(I18n.Tr("fm_ghost_rain_outline_custom"), first.UseCustomGhostRainOutline, v =>
                    {
                        foreach (FmNode n in editorSelection)
                        {
                            n.UseCustomGhostRainOutline = v;
                            if (v) SeedGhostRainOutlineFromRow(n);
                        }
                    }, "fm_help_ghost_rain");
                    if (first.UseCustomGhostRainOutline)
                    {
                        DrawEditorToggle(I18n.Tr("rain_outline"), first.GhostRainOutlineEnabled, v => { foreach (FmNode n in editorSelection) n.GhostRainOutlineEnabled = v; }, "fm_help_ghost_rain");
                        DrawEditorColorField(I18n.Tr("rain_outline_color"), first.GhostRainOutlineColor, new Color(1f, 1f, 1f, 0.5f), arr => { foreach (FmNode n in editorSelection) n.GhostRainOutlineColor = arr; });
                        DrawEditorFloatField(I18n.Tr("rain_outline_width"), "fme_grow_" + first.Id, n => n.GhostRainOutlineWidth, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainOutlineWidth = Mathf.Clamp(v, 0f, 50f);
                            EditorPropertyChanged();
                        }, "fm_help_ghost_rain");
                    }
                    DrawEditorToggle(I18n.Tr("fm_ghost_rain_corner_custom"), first.UseCustomGhostRainCornerRadius, v =>
                    {
                        foreach (FmNode n in editorSelection)
                        {
                            n.UseCustomGhostRainCornerRadius = v;
                            if (v) n.GhostRainCornerRadius = Settings.Data.RainOutlineCornerRadius;
                        }
                    }, "fm_help_ghost_rain_corner");
                    if (first.UseCustomGhostRainCornerRadius)
                    {
                        DrawEditorFloatField(I18n.Tr("fm_ghost_rain_corner_radius"), "fme_gcr_" + first.Id, n => n.GhostRainCornerRadius, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainCornerRadius = Mathf.Clamp(v, 0f, 20f);
                        }, "fm_help_ghost_rain_corner");
                    }
                    DrawEditorToggle(I18n.Tr("fm_ghost_rain_border_sides_custom"), first.UseCustomGhostRainBorderSides, v =>
                    {
                        foreach (FmNode n in editorSelection) n.UseCustomGhostRainBorderSides = v;
                    }, "fm_help_ghost_rain_border_sides");
                    if (first.UseCustomGhostRainBorderSides)
                    {
                        GUILayout.Label(I18n.Tr("rain_outline_sides"));
                        string[] ghostSides = { I18n.Tr("rain_side_all"), I18n.Tr("rain_side_vertical"), I18n.Tr("rain_side_horizontal") };
                        int ghostSelected = GUILayout.SelectionGrid(Mathf.Clamp(first.GhostRainBorderSides, 0, 2), ghostSides, 3);
                        if (ghostSelected != first.GhostRainBorderSides)
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainBorderSides = ghostSelected;
                            EditorPropertyChanged();
                        }
                    }
                    DrawEditorToggle(I18n.Tr("fm_ghost_rain_dotted_custom"), first.UseCustomGhostRainDotted, v =>
                    {
                        foreach (FmNode n in editorSelection)
                        {
                            n.UseCustomGhostRainDotted = v;
                            if (v)
                            {
                                n.GhostRainDotLength = Settings.Data.RainDotLength;
                                n.GhostRainGapLength = Settings.Data.RainGapLength;
                            }
                        }
                    }, "fm_help_ghost_rain_dotted");
                    if (first.UseCustomGhostRainDotted)
                    {
                        DrawEditorFloatField(I18n.Tr("fm_ghost_rain_dot_length"), "fme_gdl_" + first.Id, n => n.GhostRainDotLength, v =>
                        {
                            // 0 = dotted ghost rain OFF for this node. / 0 = 该节点关闭点状鬼雨。
                            foreach (FmNode n in editorSelection) n.GhostRainDotLength = Mathf.Clamp(v, 0f, 100f);
                        }, "fm_help_ghost_rain_dotted");
                        DrawEditorFloatField(I18n.Tr("fm_ghost_rain_gap_length"), "fme_ggl_" + first.Id, n => n.GhostRainGapLength, v =>
                        {
                            foreach (FmNode n in editorSelection) n.GhostRainGapLength = Mathf.Clamp(v, 0f, 100f);
                        }, "fm_help_ghost_rain_dotted");
                    }
                }
                // Per-node press scale (the Display tab's press animation per key). /
                // 节点级按压缩放（显示页的按压缩放，按按键配置）。
                DrawEditorToggle(I18n.Tr("fm_press_anim"), first.PressAnimEnabled, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressAnimEnabled = v;
                }, "fm_help_press_anim");
                if (first.PressAnimEnabled)
                {
                    DrawEditorToggle(I18n.Tr("fm_press_anim_custom"), first.UseCustomPressAnim, v =>
                    {
                        foreach (FmNode n in editorSelection) n.UseCustomPressAnim = v;
                    }, "fm_help_press_anim");
                    if (first.UseCustomPressAnim)
                        DrawEditorFloatField(I18n.Tr("fm_press_anim_scale"), "fme_pas_" + first.Id, n => n.PressAnimScale, v =>
                        {
                            foreach (FmNode n in editorSelection) n.PressAnimScale = Mathf.Clamp(v, 0.3f, 2f);
                            EditorPropertyChanged();
                        }, "fm_help_press_anim");
                }
                // Counter bounce . / 计数器弹跳（计数器弹跳动画）。
                DrawEditorToggle(I18n.Tr("fm_counter_anim"), first.CounterAnimEnabled, v =>
                {
                    foreach (FmNode n in editorSelection) n.CounterAnimEnabled = v;
                }, "fm_help_counter_anim");
                if (first.CounterAnimEnabled)
                {
                    DrawEditorFloatField(I18n.Tr("fm_anim_scale"), "fme_ascale_" + first.Id, n => n.CounterAnimScale, v =>
                    {
                        foreach (FmNode n in editorSelection) n.CounterAnimScale = Mathf.Clamp(v, 1f, 2f);
                        EditorPropertyChanged();
                    }, "fm_help_counter_anim");
                    DrawEditorFloatField(I18n.Tr("fm_anim_duration"), "fme_adur_" + first.Id, n => n.CounterAnimDurationMs, v =>
                    {
                        foreach (FmNode n in editorSelection) n.CounterAnimDurationMs = Mathf.Clamp(v, 100f, 5000f);
                        EditorPropertyChanged();
                    }, "fm_help_counter_anim");
                }
            }
            // Editor-only: the running game has no concept of "this node cannot be selected in the
            // editor", so this must not tear the overlay down and rebuild it. The `after` argument
            // is what makes that stick — the default path would still rebuild.
            // 纯编辑器语义：运行中的游戏没有"此节点在编辑器里不可选中"的概念，不应为此拆掉重建
            // 整层覆盖层。正是 `after` 参数让这一点生效——默认路径仍会重建。
            DrawEditorToggle(I18n.Tr("fm_unselectable"), first.Unselectable, v =>
            {
                foreach (FmNode n in editorSelection) n.Unselectable = v;
            }, null, EditorNothing);
            DrawEditorToggle(I18n.Tr("fm_hidden"), first.Hidden, v => { foreach (FmNode n in editorSelection) n.Hidden = v; });
            DrawEditorFontSize(first);
            DrawEditorToggle(I18n.Tr("fm_hide_label"), first.HideLabel, v => { foreach (FmNode n in editorSelection) n.HideLabel = v; });
            DrawEditorToggle(I18n.Tr("fm_hide_label_while_pressed"), first.HideLabelWhilePressed, v => { foreach (FmNode n in editorSelection) n.HideLabelWhilePressed = v; });
            DrawEditorToggle(I18n.Tr("fm_hide_count"), first.HideCount, v => { foreach (FmNode n in editorSelection) n.HideCount = v; });
            DrawEditorToggle(I18n.Tr("fm_count_show_while_pressed"), first.CountShowWhilePressed, v => { foreach (FmNode n in editorSelection) n.CountShowWhilePressed = v; });
            DrawEditorFloatField(I18n.Tr("fm_count_offset_x"), "fme_cox_" + first.Id, n => n.CountOffsetX, v =>
            {
                foreach (FmNode n in editorSelection) n.CountOffsetX = Mathf.Clamp(v, -200f, 200f);
                EditorPropertyChanged();
            });
            DrawEditorFloatField(I18n.Tr("fm_count_offset_y"), "fme_coy_" + first.Id, n => n.CountOffsetY, v =>
            {
                foreach (FmNode n in editorSelection) n.CountOffsetY = Mathf.Clamp(v, -200f, 200f);
                EditorPropertyChanged();
            });
            DrawEditorFloatField(I18n.Tr("fm_label_offset_x"), "fme_lox_" + first.Id, n => n.LabelOffsetX, v =>
            {
                foreach (FmNode n in editorSelection) n.LabelOffsetX = Mathf.Clamp(v, -200f, 200f);
                EditorPropertyChanged();
            });
            DrawEditorFloatField(I18n.Tr("fm_label_offset_y"), "fme_loy_" + first.Id, n => n.LabelOffsetY, v =>
            {
                foreach (FmNode n in editorSelection) n.LabelOffsetY = Mathf.Clamp(v, -200f, 200f);
                EditorPropertyChanged();
            });
            DrawEditorFloatField(I18n.Tr("fm_label_rotation"), "fme_lrot_" + first.Id, n => n.LabelRotation, v =>
            {
                foreach (FmNode n in editorSelection) n.LabelRotation = Mathf.Clamp(v, -180f, 180f);
                EditorPropertyChanged();
            });
            DrawEditorFloatField(I18n.Tr("fm_count_rotation"), "fme_crot_" + first.Id, n => n.CountRotation, v =>
            {
                foreach (FmNode n in editorSelection) n.CountRotation = Mathf.Clamp(v, -180f, 180f);
                EditorPropertyChanged();
            });
            DrawEditorFloatField(I18n.Tr("fm_label_scale"), "fme_lscale_" + first.Id, n => n.LabelScale, v =>
            {
                foreach (FmNode n in editorSelection) n.LabelScale = Mathf.Clamp(v, 0.5f, 2f);
                EditorPropertyChanged();
            });
            DrawEditorFloatField(I18n.Tr("fm_count_scale"), "fme_cscale_" + first.Id, n => n.CountScale, v =>
            {
                foreach (FmNode n in editorSelection) n.CountScale = Mathf.Clamp(v, 0.5f, 2f);
                EditorPropertyChanged();
            });
            DrawEditorToggle(I18n.Tr("fm_pressed_label_scale_custom"), first.UsePressedLabelScale, v =>
            {
                foreach (FmNode n in editorSelection)
                {
                    n.UsePressedLabelScale = v;
                    if (v) n.PressedLabelScale = n.LabelScale;
                }
                EditorPropertyChanged();
            });
            if (first.UsePressedLabelScale)
                DrawEditorFloatField(I18n.Tr("fm_pressed_label_scale"), "fme_pls_" + first.Id, n => n.PressedLabelScale, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressedLabelScale = Mathf.Clamp(v, 0.5f, 2f);
                    EditorPropertyChanged();
                });
            DrawEditorToggle(I18n.Tr("fm_pressed_count_scale_custom"), first.UsePressedCountScale, v =>
            {
                foreach (FmNode n in editorSelection)
                {
                    n.UsePressedCountScale = v;
                    if (v) n.PressedCountScale = n.CountScale;
                }
                EditorPropertyChanged();
            });
            if (first.UsePressedCountScale)
                DrawEditorFloatField(I18n.Tr("fm_pressed_count_scale"), "fme_pcs_" + first.Id, n => n.PressedCountScale, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressedCountScale = Mathf.Clamp(v, 0.5f, 2f);
                    EditorPropertyChanged();
                });
            DrawEditorToggle(I18n.Tr("fm_pressed_label_rotation_custom"), first.UsePressedLabelRotation, v =>
            {
                foreach (FmNode n in editorSelection)
                {
                    n.UsePressedLabelRotation = v;
                    if (v) n.PressedLabelRotation = n.LabelRotation;
                }
                EditorPropertyChanged();
            });
            if (first.UsePressedLabelRotation)
                DrawEditorFloatField(I18n.Tr("fm_pressed_label_rotation"), "fme_plr_" + first.Id, n => n.PressedLabelRotation, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressedLabelRotation = Mathf.Clamp(v, -180f, 180f);
                    EditorPropertyChanged();
                });
            DrawEditorToggle(I18n.Tr("fm_pressed_count_rotation_custom"), first.UsePressedCountRotation, v =>
            {
                foreach (FmNode n in editorSelection)
                {
                    n.UsePressedCountRotation = v;
                    if (v) n.PressedCountRotation = n.CountRotation;
                }
                EditorPropertyChanged();
            });
            if (first.UsePressedCountRotation)
                DrawEditorFloatField(I18n.Tr("fm_pressed_count_rotation"), "fme_pcr_" + first.Id, n => n.PressedCountRotation, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressedCountRotation = Mathf.Clamp(v, -180f, 180f);
                    EditorPropertyChanged();
                });
            DrawEditorToggle(I18n.Tr("fm_pressed_label_offset_custom"), first.UsePressedLabelOffset, v =>
            {
                foreach (FmNode n in editorSelection) n.UsePressedLabelOffset = v;
                EditorPropertyChanged();
            });
            if (first.UsePressedLabelOffset)
            {
                DrawEditorFloatField(I18n.Tr("fm_pressed_label_offset_x"), "fme_plox_" + first.Id, n => n.PressedLabelOffsetX, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressedLabelOffsetX = Mathf.Clamp(v, -200f, 200f);
                    EditorPropertyChanged();
                });
                DrawEditorFloatField(I18n.Tr("fm_pressed_label_offset_y"), "fme_ploy_" + first.Id, n => n.PressedLabelOffsetY, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressedLabelOffsetY = Mathf.Clamp(v, -200f, 200f);
                    EditorPropertyChanged();
                });
            }
            DrawEditorToggle(I18n.Tr("fm_pressed_count_offset_custom"), first.UsePressedCountOffset, v =>
            {
                foreach (FmNode n in editorSelection) n.UsePressedCountOffset = v;
                EditorPropertyChanged();
            });
            if (first.UsePressedCountOffset)
            {
                DrawEditorFloatField(I18n.Tr("fm_pressed_count_offset_x"), "fme_pcox_" + first.Id, n => n.PressedCountOffsetX, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressedCountOffsetX = Mathf.Clamp(v, -200f, 200f);
                    EditorPropertyChanged();
                });
                DrawEditorFloatField(I18n.Tr("fm_pressed_count_offset_y"), "fme_pcoy_" + first.Id, n => n.PressedCountOffsetY, v =>
                {
                    foreach (FmNode n in editorSelection) n.PressedCountOffsetY = Mathf.Clamp(v, -200f, 200f);
                    EditorPropertyChanged();
                });
            }

            // Layer group assignment / 图层组指派
            if (!string.IsNullOrEmpty(first.GroupId))
            {
                FmLayerGroup grp = Settings.Data.LayerGroups.FirstOrDefault(g => g != null && g.Id == first.GroupId);
                GUILayout.Label(I18n.Tr("fm_group") + ": " + (grp != null ? grp.Name : first.GroupId));
                if (GUILayout.Button(I18n.Tr("fm_group_ungroup"), GUILayout.MinWidth(140f)))
                {
                    foreach (FmNode n in editorSelection) n.GroupId = "";
                    PushEditorHistory(false);
                    EditorMutated();
                }
            }

            // Color overrides work for multi-select too: fields show the first node's current
            // values and every change applies to the whole selection. / 配色覆盖对多选同样生效：
            // 字段显示首个节点的当前值，改动应用到整个选区。
            // Color overrides: box colors apply to key/panel nodes; image nodes get TEXT colors
            // only at runtime, but the editor shows the text pair for them too (mixed selection
            // stays uniform — per-type writers skip inapplicable fields). /
            // 配色覆盖：盒子色作用于按键/面板节点；图片节点运行时只有文本色生效，但编辑器对
            // 图片节点同样显示文本双色（混合选区保持界面一致——写入方按类型跳过不适用字段）。
            {
                bool isKps = first.NodeType == 1;
                bool isTotal = first.NodeType == 2;
                bool isImage = first.NodeType == 3;
                Color fbBg = isKps ? Settings.Data.KpsBackground : isTotal ? Settings.Data.TotalBackground : Settings.Data.Background;
                Color fbOl = isKps ? Settings.Data.KpsOutline : isTotal ? Settings.Data.TotalOutline : Settings.Data.Outline;
                GUILayout.Space(4f);
                DrawEditorToggle(I18n.Tr("fm_custom_colors"), first.UseCustomColor, v =>
                {
                    foreach (FmNode n in editorSelection) n.UseCustomColor = v;
                }, "fm_help_custom_colors");
                if (first.UseCustomColor)
                {
                    if (!isImage)
                    {
                        DrawEditorColorField(I18n.Tr("color_bg"), first.Bg, fbBg, arr => { foreach (FmNode n in editorSelection) if (n.NodeType != 3) n.Bg = arr; });
                        DrawEditorColorField(I18n.Tr("color_bg_clicked"), first.BgPressed, fbBg, arr => { foreach (FmNode n in editorSelection) if (n.NodeType != 3) n.BgPressed = arr; });
                        DrawEditorColorField(I18n.Tr("color_outline"), first.Outline, fbOl, arr => { foreach (FmNode n in editorSelection) if (n.NodeType != 3) n.Outline = arr; });
                        DrawEditorColorField(I18n.Tr("color_outline_clicked"), first.OutlinePressed, fbOl, arr => { foreach (FmNode n in editorSelection) if (n.NodeType != 3) n.OutlinePressed = arr; });
                    }
                    Color fbTxt = isKps ? Settings.Data.KpsText : isTotal ? Settings.Data.TotalText : Settings.Data.Text;
                    Color fbTxtP = isKps || isTotal ? fbTxt : Settings.Data.TextClicked;
                    DrawEditorColorField(I18n.Tr("color_text"), first.TextColor, fbTxt, arr => { foreach (FmNode n in editorSelection) n.TextColor = arr; });
                    DrawEditorColorField(I18n.Tr("color_text_clicked"), first.TextColorPressed, fbTxtP, arr => { foreach (FmNode n in editorSelection) n.TextColorPressed = arr; });
                    DrawEditorToggle(I18n.Tr("fm_count_color_custom"), first.UseCustomCountTextColor, v =>
                    {
                        foreach (FmNode n in editorSelection) n.UseCustomCountTextColor = v;
                        EditorPropertyChanged();
                    }, "fm_help_count_color");
                    if (first.UseCustomCountTextColor)
                    {
                        DrawEditorColorField(I18n.Tr("fm_count_color"), first.CountTextColor, fbTxt, arr => { foreach (FmNode n in editorSelection) n.CountTextColor = arr; });
                        DrawEditorColorField(I18n.Tr("fm_count_color_pressed"), first.CountTextColorPressed, fbTxtP, arr => { foreach (FmNode n in editorSelection) n.CountTextColorPressed = arr; });
                    }
                }
            }

            GUILayout.Space(4f);
            DrawEditorFloatField(I18n.Tr("fm_text_opacity"), "fme_to_" + first.Id, n => n.TextOpacity, v =>
            {
                foreach (FmNode n in editorSelection) n.TextOpacity = Mathf.Clamp01(v);
                EditorTextGradientPropertyChanged();
            });
            DrawEditorFloatField(I18n.Tr("fm_count_text_opacity"), "fme_cto_" + first.Id, n => n.CountTextOpacity, v =>
            {
                foreach (FmNode n in editorSelection) n.CountTextOpacity = Mathf.Clamp01(v);
                EditorTextGradientPropertyChanged();
            });
            Color textFallback = first.NodeType == 1 ? Settings.Data.KpsText
                : first.NodeType == 2 ? Settings.Data.TotalText : Settings.Data.Text;
            DrawEditorToggle(I18n.Tr("fm_text_gradient"), first.UseTextGradient, v =>
            {
                foreach (FmNode n in editorSelection) n.UseTextGradient = v;
                EditorTextGradientPropertyChanged();
            }, "fm_help_text_gradient");
            if (first.UseTextGradient)
            {
                DrawEditorColorField(I18n.Tr("fm_text_gradient_left"), first.TextGradientLeft, textFallback, arr =>
                {
                    foreach (FmNode n in editorSelection) n.TextGradientLeft = arr;
                    EditorTextGradientPropertyChanged();
                });
                DrawEditorColorField(I18n.Tr("fm_text_gradient_right"), first.TextGradientRight, textFallback, arr =>
                {
                    foreach (FmNode n in editorSelection) n.TextGradientRight = arr;
                    EditorTextGradientPropertyChanged();
                });
                DrawEditorToggle(I18n.Tr("fm_text_gradient_pressed"), first.UsePressedTextGradient, v =>
                {
                    foreach (FmNode n in editorSelection) n.UsePressedTextGradient = v;
                    EditorTextGradientPropertyChanged();
                }, "fm_help_text_gradient_pressed");
                if (first.UsePressedTextGradient)
                {
                    Color pressedFallback = first.NodeType == 1 || first.NodeType == 2 ? textFallback : Settings.Data.TextClicked;
                    DrawEditorColorField(I18n.Tr("fm_text_gradient_left_pressed"), first.TextGradientLeftPressed, pressedFallback, arr =>
                    {
                        foreach (FmNode n in editorSelection) n.TextGradientLeftPressed = arr;
                        EditorTextGradientPropertyChanged();
                    });
                    DrawEditorColorField(I18n.Tr("fm_text_gradient_right_pressed"), first.TextGradientRightPressed, pressedFallback, arr =>
                    {
                        foreach (FmNode n in editorSelection) n.TextGradientRightPressed = arr;
                        EditorTextGradientPropertyChanged();
                    });
                }
            }
            DrawEditorToggle(I18n.Tr("fm_count_text_gradient"), first.UseCountTextGradient, v =>
            {
                foreach (FmNode n in editorSelection) n.UseCountTextGradient = v;
                EditorTextGradientPropertyChanged();
            }, "fm_help_text_gradient");
            if (first.UseCountTextGradient)
            {
                DrawEditorColorField(I18n.Tr("fm_count_text_gradient_left"), first.CountTextGradientLeft, textFallback, arr =>
                {
                    foreach (FmNode n in editorSelection) n.CountTextGradientLeft = arr;
                    EditorTextGradientPropertyChanged();
                });
                DrawEditorColorField(I18n.Tr("fm_count_text_gradient_right"), first.CountTextGradientRight, textFallback, arr =>
                {
                    foreach (FmNode n in editorSelection) n.CountTextGradientRight = arr;
                    EditorTextGradientPropertyChanged();
                });
                DrawEditorToggle(I18n.Tr("fm_count_text_gradient_pressed"), first.UsePressedCountTextGradient, v =>
                {
                    foreach (FmNode n in editorSelection) n.UsePressedCountTextGradient = v;
                    EditorTextGradientPropertyChanged();
                }, "fm_help_text_gradient_pressed");
                if (first.UsePressedCountTextGradient)
                {
                    Color pressedFallback = first.NodeType == 1 || first.NodeType == 2 ? textFallback : Settings.Data.TextClicked;
                    DrawEditorColorField(I18n.Tr("fm_count_text_gradient_left_pressed"), first.CountTextGradientLeftPressed, pressedFallback, arr =>
                    {
                        foreach (FmNode n in editorSelection) n.CountTextGradientLeftPressed = arr;
                        EditorTextGradientPropertyChanged();
                    });
                    DrawEditorColorField(I18n.Tr("fm_count_text_gradient_right_pressed"), first.CountTextGradientRightPressed, pressedFallback, arr =>
                    {
                        foreach (FmNode n in editorSelection) n.CountTextGradientRightPressed = arr;
                        EditorTextGradientPropertyChanged();
                    });
                }
            }

            GUILayout.Space(4f);
            DrawEditorToggle(I18n.Tr("fm_custom_glow"), first.UseGlow, v =>
            {
                foreach (FmNode n in editorSelection) n.UseGlow = v;
                EditorGlowPropertyChanged();
            }, "fm_help_custom_glow");
            if (first.UseGlow)
            {
                DrawEditorToggle(I18n.Tr("fm_glow_follow_body"), first.GlowFollowBody, v =>
                {
                    foreach (FmNode n in editorSelection) n.GlowFollowBody = v;
                    EditorGlowPropertyChanged();
                }, "fm_help_glow_follow_body");
                DrawEditorFloatField(I18n.Tr("fm_glow_size"), "fme_gs_" + first.Id, n => n.GlowSize, v =>
                {
                    foreach (FmNode n in editorSelection) n.GlowSize = Mathf.Clamp(v, 0f, 50f);
                    EditorGlowPropertyChanged();
                }, "fm_help_glow_params");
                DrawEditorPercentField(I18n.Tr("fm_glow_opacity"), "fme_go_" + first.Id, n => n.GlowOpacity, v =>
                {
                    foreach (FmNode n in editorSelection) n.GlowOpacity = Mathf.Clamp01(v);
                    EditorGlowPropertyChanged();
                }, "fm_help_glow_params");
                if (!first.GlowFollowBody)
                {
                    Color glowFallback = first.NodeType == 1 ? Settings.Data.KpsBackground
                        : first.NodeType == 2 ? Settings.Data.TotalBackground
                        : first.NodeType == 3 ? Settings.Data.Outline : Settings.Data.Background;
                    DrawEditorColorField(I18n.Tr("fm_glow_color"), first.GlowColor, glowFallback, arr =>
                    {
                        foreach (FmNode n in editorSelection) n.GlowColor = arr;
                        EditorGlowPropertyChanged();
                    });
                }

                DrawEditorToggle(I18n.Tr("fm_glow_pressed_override"), first.GlowPressedOverride, v =>
                {
                    foreach (FmNode n in editorSelection) n.GlowPressedOverride = v;
                    EditorGlowPropertyChanged();
                }, "fm_help_glow_pressed");
                if (first.GlowPressedOverride)
                {
                    DrawEditorToggle(I18n.Tr("fm_glow_pressed_follow_body"), first.GlowFollowBodyPressed, v =>
                    {
                        foreach (FmNode n in editorSelection) n.GlowFollowBodyPressed = v;
                        EditorGlowPropertyChanged();
                    }, "fm_help_glow_pressed_follow");
                    DrawEditorFloatField(I18n.Tr("fm_glow_pressed_size"), "fme_gps_" + first.Id, n => n.GlowSizePressed, v =>
                    {
                        foreach (FmNode n in editorSelection) n.GlowSizePressed = Mathf.Clamp(v, 0f, 50f);
                        EditorGlowPropertyChanged();
                    }, "fm_help_glow_params");
                    DrawEditorPercentField(I18n.Tr("fm_glow_pressed_opacity"), "fme_gpo_" + first.Id, n => n.GlowOpacityPressed, v =>
                    {
                        foreach (FmNode n in editorSelection) n.GlowOpacityPressed = Mathf.Clamp01(v);
                        EditorGlowPropertyChanged();
                    }, "fm_help_glow_params");
                    if (!first.GlowFollowBodyPressed)
                    {
                        Color pressedGlowFallback = first.NodeType == 1 ? Settings.Data.KpsBackground
                            : first.NodeType == 2 ? Settings.Data.TotalBackground
                            : first.NodeType == 3 ? Settings.Data.Outline : Settings.Data.BackgroundClicked;
                        DrawEditorColorField(I18n.Tr("fm_glow_pressed_color"), first.GlowColorPressed, pressedGlowFallback, arr =>
                        {
                            foreach (FmNode n in editorSelection) n.GlowColorPressed = arr;
                            EditorGlowPropertyChanged();
                        });
                    }
                }
            }

            if (first.NodeType != 3)
            {
                GUILayout.Space(4f);
                DrawEditorToggle(I18n.Tr("fm_background_gradient"), first.UseBackgroundGradient, v =>
                {
                    foreach (FmNode n in editorSelection) n.UseBackgroundGradient = v;
                    EditorGradientPropertyChanged();
                }, "fm_help_background_gradient");
                if (first.UseBackgroundGradient)
                {
                    Color normalFallback = first.NodeType == 1 ? Settings.Data.KpsBackground
                        : first.NodeType == 2 ? Settings.Data.TotalBackground : Settings.Data.Background;
                    Color activeFallback = first.NodeType == 1 ? Settings.Data.KpsBackground
                        : first.NodeType == 2 ? Settings.Data.TotalBackground : Settings.Data.BackgroundClicked;
                    DrawEditorColorField(I18n.Tr("fm_background_gradient_top"), first.BackgroundGradientTop, normalFallback, arr =>
                    {
                        foreach (FmNode n in editorSelection) n.BackgroundGradientTop = arr;
                        EditorGradientPropertyChanged();
                    });
                    DrawEditorColorField(I18n.Tr("fm_background_gradient_bottom"), first.BackgroundGradientBottom, normalFallback, arr =>
                    {
                        foreach (FmNode n in editorSelection) n.BackgroundGradientBottom = arr;
                        EditorGradientPropertyChanged();
                    });
                    DrawEditorToggle(I18n.Tr("fm_background_gradient_pressed"), first.UsePressedBackgroundGradient, v =>
                    {
                        foreach (FmNode n in editorSelection) n.UsePressedBackgroundGradient = v;
                        EditorGradientPropertyChanged();
                    }, "fm_help_background_gradient_pressed");
                    if (first.UsePressedBackgroundGradient)
                    {
                        DrawEditorColorField(I18n.Tr("fm_background_gradient_top_pressed"), first.BackgroundGradientTopPressed, activeFallback, arr =>
                        {
                            foreach (FmNode n in editorSelection) n.BackgroundGradientTopPressed = arr;
                            EditorGradientPropertyChanged();
                        });
                        DrawEditorColorField(I18n.Tr("fm_background_gradient_bottom_pressed"), first.BackgroundGradientBottomPressed, activeFallback, arr =>
                        {
                            foreach (FmNode n in editorSelection) n.BackgroundGradientBottomPressed = arr;
                            EditorGradientPropertyChanged();
                        });
                    }
                }

                GUILayout.Space(4f);
                DrawEditorToggle(I18n.Tr("fm_outline_gradient"), first.UseOutlineGradient, v =>
                {
                    foreach (FmNode n in editorSelection) n.UseOutlineGradient = v;
                    EditorGradientPropertyChanged();
                }, "fm_help_outline_gradient");
                if (first.UseOutlineGradient)
                {
                    Color normalOutline = first.NodeType == 1 ? Settings.Data.KpsOutline
                        : first.NodeType == 2 ? Settings.Data.TotalOutline : Settings.Data.Outline;
                    Color activeOutline = first.NodeType == 1 ? Settings.Data.KpsOutline
                        : first.NodeType == 2 ? Settings.Data.TotalOutline : Settings.Data.OutlineClicked;
                    DrawEditorColorField(I18n.Tr("fm_outline_gradient_top"), first.OutlineGradientTop, normalOutline, arr =>
                    {
                        foreach (FmNode n in editorSelection) n.OutlineGradientTop = arr;
                        EditorGradientPropertyChanged();
                    });
                    DrawEditorColorField(I18n.Tr("fm_outline_gradient_bottom"), first.OutlineGradientBottom, normalOutline, arr =>
                    {
                        foreach (FmNode n in editorSelection) n.OutlineGradientBottom = arr;
                        EditorGradientPropertyChanged();
                    });
                    DrawEditorToggle(I18n.Tr("fm_outline_gradient_pressed"), first.UsePressedOutlineGradient, v =>
                    {
                        foreach (FmNode n in editorSelection) n.UsePressedOutlineGradient = v;
                        EditorGradientPropertyChanged();
                    }, "fm_help_outline_gradient_pressed");
                    if (first.UsePressedOutlineGradient)
                    {
                        DrawEditorColorField(I18n.Tr("fm_outline_gradient_top_pressed"), first.OutlineGradientTopPressed, activeOutline, arr =>
                        {
                            foreach (FmNode n in editorSelection) n.OutlineGradientTopPressed = arr;
                            EditorGradientPropertyChanged();
                        });
                        DrawEditorColorField(I18n.Tr("fm_outline_gradient_bottom_pressed"), first.OutlineGradientBottomPressed, activeOutline, arr =>
                        {
                            foreach (FmNode n in editorSelection) n.OutlineGradientBottomPressed = arr;
                            EditorGradientPropertyChanged();
                        });
                    }
                }
            }

            // Count reset zeroes ONLY the selected counting nodes — strictly per key, nothing
            // else. A counted key's presses also live in the global TotalCount accumulator
            // (mirrored by the ungrouped Total panel; group panels sum member Counts), so the
            // reset subtracts exactly THIS key's contribution on the way down — otherwise the
            // Total keeps counting presses the key no longer shows. Total/KPS panels display
            // derived values and own no count, so they are out of scope again.
            // 计数重置只清「选中的」计数节点——严格逐键，不碰任何其它东西。计入按键的按压同时记在
            // 全局 TotalCount 累计器里（未分组 Total 面板镜像它；分组面板汇总成员 Count），故清零时
            // 只扣回该键自己的贡献——否则 Total 会一直算着该键已不显示的按压。Total/KPS 面板显示
            // 派生值、自身不计数，重新移出重置范围。
            bool anyCountable = false;
            foreach (FmNode n in editorSelection)
            {
                if (n != null && (n.NodeType == 0
                    || (n.NodeType == 3 && !string.IsNullOrWhiteSpace(n.KeyBind))))
                {
                    anyCountable = true;
                    break;
                }
            }
            if (anyCountable)
            {
                // Manual count entry: set the selected counting nodes' Count to ANY value, not
                // just zero. Recompute the global Total from the whole document so CountInTotal
                // membership and existing counts cannot drift. / 手动输入计数：把选中计数节点
                // 的 Count 设为任意值，而非只能清零；重算整份文档的全局 Total，避免开关与历史计数漂移。
                int seedCount = 0;
                bool hasSeed = false, countMixed = false;
                foreach (FmNode n in editorSelection)
                {
                    if (n == null || (n.NodeType != 0 && !(n.NodeType == 3 && !string.IsNullOrWhiteSpace(n.KeyBind)))) continue;
                    if (!hasSeed) { seedCount = n.Count; hasSeed = true; }
                    else if (n.Count != seedCount) countMixed = true;
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label(I18n.Tr("fm_count_value"), GUILayout.Width(96f));
                DrawEditorHelpMarker("fm_help_count_value");
                string countText = TextInputField("fme_cnt_" + first.Id,
                    countMixed ? "—" : seedCount.ToString(), GUILayout.Width(110f));
                if (int.TryParse(countText.Replace("—", "").Trim(), out int typedCount) && typedCount >= 0
                    && (countMixed || typedCount != seedCount))
                {
                    foreach (FmNode n in editorSelection)
                    {
                        if (n == null || (n.NodeType != 0 && !(n.NodeType == 3 && !string.IsNullOrWhiteSpace(n.KeyBind)))) continue;
                        n.Count = typedCount;
                    }
                    RecalculateCustomTotalCount();
                    RefreshAllCountDisplay();
                    PushEditorHistoryNudge();
                    SaveSettingsFromGui();
                }
                GUILayout.EndHorizontal();
                DrawEditorHelpBox("fm_help_count_value");

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(I18n.Tr("fm_reset_count"), GUILayout.MinWidth(140f)))
                {
                    // Record the PRE-reset state FIRST, while the counts are still real. EditorHistory
                    // is a post-state timeline, and every ordinary entry is captured with
                    // PreserveCounts=true (counters stripped) precisely so an undo never rolls a
                    // play session's counts back. That is the right default — but it means the state
                    // this button destroys never entered the timeline: with only a post-entry,
                    // undoing lands on the previous (counter-less) entry and re-applies counts from
                    // the LIVE document, which was just zeroed. One Ctrl+Z therefore permanently
                    // wiped the entire count table and persisted the wipe through EditorMutated.
                    // 先记录**重置前**的状态，此时计数还是真实值。EditorHistory 是后置状态时间线，
                    // 而普通条目一律以 PreserveCounts=true 采集（剥离计数），正是为了让撤销永不
                    // 回滚一次游玩的计数——这个默认是对的；但它意味着本按钮销毁的状态从未进过
                    // 时间线：只压后置条目时，撤销落到前一条（无计数）并从**实时文档**回填计数，
                    // 而实时文档刚被清零。于是按一次 Ctrl+Z 就永久抹掉整张计数表，并经
                    // EditorMutated 落盘。
                    PushEditorHistory(false, true);
                    foreach (FmNode n in editorSelection)
                    {
                        if (n == null || (n.NodeType != 0 && !(n.NodeType == 3 && !string.IsNullOrWhiteSpace(n.KeyBind)))) continue;
                        // Recompute after the reset rather than subtracting only the currently
                        // flagged nodes; this also repairs an already-drifted profile.
                        // 重置后从整份文档重算，而不是只扣当前开关为 true 的节点；也能修复既有漂移。
                        n.Count = 0;
                        if (n.RuntimeKey != null)
                        {
                            // The visible per-key KPS comes from KpsLog, not Count — clearing only
                            // Count left the old rate on screen. / 屏上的每键 KPS 来自 KpsLog 而非
                            // Count——只清 Count 会让旧速率留在屏上。
                            n.RuntimeKey.KpsLog.Clear();
                            n.RuntimeKey.LastShownKps = int.MinValue;
                        }
                    }
                    RecalculateCustomTotalCount();
                    RefreshAllCountDisplay();
                    // preserveCounts:false — the counters ARE the thing being edited here, so this
                    // entry must carry the real (zeroed) values instead of re-applying the live
                    // ones on restore. Together with the pre-reset entry pushed above, one undo
                    // brings the counts back and a second undo still walks the ordinary timeline.
                    // preserveCounts:false：计数正是本次编辑的对象，本条必须携带真实的（已清零）值，
                    // 恢复时不能再从实时文档回填。与上面压入的重置前条目配合，一次撤销能把计数
                    // 带回来，第二次撤销仍能正常走普通时间线。
                    PushEditorHistory(false, false);
                    SaveSettingsFromGui();
                }
                DrawEditorHelpMarker("fm_help_reset_count");
                GUILayout.EndHorizontal();
                DrawEditorHelpBox("fm_help_reset_count");
            }

            // Group manager lives at the panel bottom REGARDLESS of selection — it used to show
            // only with an empty selection while 指派 needed a non-empty one, making assign
            // structurally unreachable. / 图层组管理区常驻面板底部，与选中状态无关——此前只在
            // 空选区时显示，而指派又要求非空选区，指派在结构上根本用不了。
            DrawEditorGroupManager();
        }

        private void DrawEditorNodeTypeCombo(FmNode node)
        {
            string[] names = { I18n.Tr("fm_add_key"), I18n.Tr("fm_add_kps"), I18n.Tr("fm_add_total"), I18n.Tr("fm_add_image") };
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.Tr("fm_node_type"), GUILayout.Width(96f));
            DrawEditorHelpMarker("fm_help_node_type");
            int type = node.NodeType;
            int newType = GUILayout.SelectionGrid(type, names, 4, GUILayout.Height(20f));
            GUILayout.EndHorizontal();
            DrawEditorHelpBox("fm_help_node_type");
            if (newType == type || newType < 0 || newType > 3) return;
            if ((newType == 1 && GroupHasStat(node.GroupId, 1))
                || (newType == 2 && GroupHasStat(node.GroupId, 2)))
                return; // one stat panel per group / 每组最多 1 个统计面板
            node.NodeType = newType;
            PushEditorHistory(false);
            EditorMutated();
        }

        /// <summary>Ghost-key capture row: the alternate trigger that drops ghost rain from this
        /// node's column (runtime fully supported it; the editor just never exposed it — the
        /// binding was only settable by hand-editing the profile JSON). / 鬼键捕获行：按它会从
        /// 该节点列掉落鬼雨的备用触发键（运行时早已完整支持，只是编辑器一直没有入口——此前
        /// 只能手改配置 JSON 设置）。</summary>
        private void DrawEditorGhostBindCapture(FmNode node)
        {
            GUILayout.BeginHorizontal();
            string bound = string.IsNullOrWhiteSpace(node.GhostKey) ? "None" : node.GhostKey;
            GUILayout.Label(I18n.Tr("fm_ghost_bind") + ": " + bound, GUILayout.Width(160f));
            bool capturing = fmCaptureGhostNode == node;
            if (GUILayout.Button(capturing ? I18n.Tr("fm_wait_key") : I18n.Tr("fm_bind"), GUILayout.MinWidth(90f)))
                fmCaptureGhostNode = capturing ? null : node;
            if (GUILayout.Button(I18n.Tr("fm_clear"), GUILayout.MinWidth(60f)))
            {
                node.GhostKey = "";
                fmCaptureGhostNode = null;
                EditorPropertyChanged();
            }
            DrawEditorHelpMarker("fm_help_ghost_key");
            GUILayout.EndHorizontal();
            DrawEditorHelpBox("fm_help_ghost_key");
            if (capturing)
            {
                GUILayout.Label(I18n.Tr("fm_press_hint"));
                Event e = Event.current;
                if (e != null && e.type == EventType.KeyDown)
                {
                    if (e.keyCode == KeyCode.Escape)
                    {
                        fmCaptureGhostNode = null;
                        e.Use();
                    }
                    else if (e.keyCode != KeyCode.None
                        && (e.keyCode < KeyCode.Mouse0 || e.keyCode > KeyCode.Mouse6)
                        && e.keyCode != KeyCode.Return)
                    {
                        node.GhostKey = e.keyCode.ToString();
                        fmCaptureGhostNode = null;
                        e.Use();
                        EditorPropertyChanged();
                    }
                }
            }
        }

        private void DrawEditorKeyBindCapture(FmNode node)
        {
            GUILayout.BeginHorizontal();
            string bound = string.IsNullOrEmpty(node.KeyBind) ? "None" : node.KeyBind;
            GUILayout.Label(I18n.Tr("fm_bind") + ": " + bound, GUILayout.Width(160f));
            bool capturing = fmCaptureNode == node;
            if (GUILayout.Button(capturing ? I18n.Tr("fm_wait_key") : I18n.Tr("fm_bind"), GUILayout.MinWidth(90f)))
            {
                // Arming a node capture while the SETTINGS window still has a rebind armed means one
                // physical press is consumed by both: ProcessKeySelection polls in Update and would
                // rebind a fixed-layout slot as well. Disarm the settings side explicitly.
                // 在设置窗口仍处于改键捕获态时又武装节点捕获，同一次物理按键会被两边同时消费：
                // ProcessKeySelection 在 Update 里轮询，会顺带改掉固定布局的槽位绑定。
                SelectedKey = -1;
                changeState = 0;
                fmCaptureNode = capturing ? null : node;
            }
            if (GUILayout.Button(I18n.Tr("fm_clear"), GUILayout.MinWidth(60f)))
            {
                node.KeyBind = "";
                node.CustomText = "";
                fmCaptureNode = null;
                EditorPropertyChanged();
            }
            DrawEditorHelpMarker("fm_help_keybind");
            GUILayout.EndHorizontal();
            DrawEditorHelpBox("fm_help_keybind");
            if (capturing)
            {
                GUILayout.Label(I18n.Tr("fm_press_hint"));
                Event e = Event.current;
                if (e != null && e.type == EventType.KeyDown)
                {
                    if (e.keyCode == KeyCode.Escape)
                    {
                        fmCaptureNode = null;
                        e.Use();
                    }
                    else if (e.keyCode != KeyCode.None
                        && (e.keyCode < KeyCode.Mouse0 || e.keyCode > KeyCode.Mouse6)
                        && e.keyCode != KeyCode.Return)
                    {
                        node.KeyBind = e.keyCode.ToString();
                        node.CustomText = KeyToString(e.keyCode);
                        fmCaptureNode = null;
                        e.Use();
                        EditorPropertyChanged();
                    }
                }
            }
        }

        /// <summary>Node font size (0 = follow the global key font size) / 节点字号（0 = 跟随全局按键字号）</summary>
        private void DrawEditorFontSize(FmNode first)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.Tr("key_font_size"), GUILayout.Width(96f));
            DrawEditorHelpMarker("fm_help_font_size");
            int size = Mathf.RoundToInt(GUILayout.HorizontalSlider(first.FontSize, 0f, 72f));
            // Mixed sizes show "—" (never parses → no accidental mass-apply); deliberate input
            // still applies to all. / 字号不一致时显示"—"（永不解析→不会意外群发）；有意输入
            // 仍然应用到全部。
            bool mixed = false;
            for (int i = 1; i < editorSelection.Count; i++)
                if (!Mathf.Approximately(editorSelection[i].FontSize, first.FontSize)) { mixed = true; break; }
            string text = TextInputField("fme_fs_" + first.Id, mixed ? "—" : size.ToString(), GUILayout.Width(56f));
            if (int.TryParse(text.Replace("—", "").Trim(), out int parsed)) size = Mathf.Clamp(parsed, 0, 72);
            GUILayout.Label(size <= 0 ? I18n.Tr("fm_font_global") : size + "px", GUILayout.Width(48f));
            GUILayout.EndHorizontal();
            DrawEditorHelpBox("fm_help_font_size");
            // Same as depth: a slider drag applies on its own; untouched mixed selections keep the
            // slider at the active node's value → no accidental apply. / 与深度同理：拖滑杆即应
            // 用；未触碰的混合选区滑杆停在活动节点值上——不会误应用。
            if (!Mathf.Approximately(size, first.FontSize))
            {
                foreach (FmNode n in editorSelection) n.FontSize = size;
                EditorPropertyChanged();
            }
            DrawEditorFontStyle(first);
            DrawEditorToggle(I18n.Tr("fm_count_font_style_custom"), first.UseCustomCountFontStyle, v =>
            {
                foreach (FmNode n in editorSelection)
                {
                    n.UseCustomCountFontStyle = v;
                    if (v && n.CountFontStyleFlags == 0) n.CountFontStyleFlags = n.FontStyleFlags;
                }
                EditorPropertyChanged();
            });
            if (first.UseCustomCountFontStyle) DrawEditorCountFontStyle(first);
            DrawEditorFloatField(I18n.Tr("fm_count_font_size"), "fme_cfs_" + first.Id, n => n.CountFontSize, v =>
            {
                foreach (FmNode n in editorSelection) n.CountFontSize = Mathf.Clamp(v, 0f, 72f);
                EditorPropertyChanged();
            });
        }

        private void DrawEditorFontStyle(FmNode first)
        {
            GUILayout.Label(I18n.Tr("fm_font_style"));
            GUILayout.BeginHorizontal();
            // TMP FontStyles values: Bold=1, Italic=2, Underline=4, Strikethrough=64.
            // Keep the numeric masks local to the serialized int field; no per-frame parsing.
            // TMP FontStyles 数值：Bold=1、Italic=2、Underline=4、Strikethrough=64；
            // 序列化 int 只在编辑器属性变更时解析，不做逐帧扫描。
            int flags = first.FontStyleFlags;
            bool mixed = false;
            for (int i = 1; i < editorSelection.Count; i++)
                if (editorSelection[i].FontStyleFlags != flags) { mixed = true; break; }
            if (mixed) GUILayout.Label("—", GUILayout.Width(24f));
            int mask = 0;
            bool bold = (flags & 1) != 0;
            bool italic = (flags & 2) != 0;
            bool underline = (flags & 4) != 0;
            bool strike = (flags & 64) != 0;
            bool newBold = GUILayout.Toggle(bold, I18n.Tr("fm_font_bold"), GUILayout.Width(58f));
            bool newItalic = GUILayout.Toggle(italic, I18n.Tr("fm_font_italic"), GUILayout.Width(58f));
            bool newUnderline = GUILayout.Toggle(underline, I18n.Tr("fm_font_underline"), GUILayout.Width(78f));
            bool newStrike = GUILayout.Toggle(strike, I18n.Tr("fm_font_strikethrough"), GUILayout.Width(82f));
            mask = (newBold ? 1 : 0) | (newItalic ? 2 : 0) | (newUnderline ? 4 : 0) | (newStrike ? 64 : 0);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            bool lower = (flags & 8) != 0;
            bool upper = (flags & 16) != 0;
            bool smallCaps = (flags & 32) != 0;
            bool superscript = (flags & 128) != 0;
            bool subscript = (flags & 256) != 0;
            bool newLower = GUILayout.Toggle(lower, I18n.Tr("fm_font_lowercase"), GUILayout.Width(78f));
            bool newUpper = GUILayout.Toggle(upper, I18n.Tr("fm_font_uppercase"), GUILayout.Width(82f));
            bool newSmallCaps = GUILayout.Toggle(smallCaps, I18n.Tr("fm_font_smallcaps"), GUILayout.Width(78f));
            bool newSuperscript = GUILayout.Toggle(superscript, I18n.Tr("fm_font_superscript"), GUILayout.Width(92f));
            bool newSubscript = GUILayout.Toggle(subscript, I18n.Tr("fm_font_subscript"), GUILayout.Width(88f));
            mask |= (newLower ? 8 : 0) | (newUpper ? 16 : 0) | (newSmallCaps ? 32 : 0)
                | (newSuperscript ? 128 : 0) | (newSubscript ? 256 : 0);
            GUILayout.EndHorizontal();
            if (mask != flags)
            {
                foreach (FmNode n in editorSelection) n.FontStyleFlags = mask;
                EditorPropertyChanged();
            }
        }

        private void DrawEditorCountFontStyle(FmNode first)
        {
            GUILayout.Label(I18n.Tr("fm_count_font_style"));
            GUILayout.BeginHorizontal();
            int flags = first.CountFontStyleFlags;
            bool mixed = false;
            for (int i = 1; i < editorSelection.Count; i++)
                if (editorSelection[i].CountFontStyleFlags != flags) { mixed = true; break; }
            if (mixed) GUILayout.Label("—", GUILayout.Width(24f));
            bool newBold = GUILayout.Toggle((flags & 1) != 0, I18n.Tr("fm_font_bold"), GUILayout.Width(58f));
            bool newItalic = GUILayout.Toggle((flags & 2) != 0, I18n.Tr("fm_font_italic"), GUILayout.Width(58f));
            bool newUnderline = GUILayout.Toggle((flags & 4) != 0, I18n.Tr("fm_font_underline"), GUILayout.Width(78f));
            bool newStrike = GUILayout.Toggle((flags & 64) != 0, I18n.Tr("fm_font_strikethrough"), GUILayout.Width(82f));
            int mask = (newBold ? 1 : 0) | (newItalic ? 2 : 0) | (newUnderline ? 4 : 0) | (newStrike ? 64 : 0);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            bool newLower = GUILayout.Toggle((flags & 8) != 0, I18n.Tr("fm_font_lowercase"), GUILayout.Width(78f));
            bool newUpper = GUILayout.Toggle((flags & 16) != 0, I18n.Tr("fm_font_uppercase"), GUILayout.Width(82f));
            bool newSmallCaps = GUILayout.Toggle((flags & 32) != 0, I18n.Tr("fm_font_smallcaps"), GUILayout.Width(78f));
            bool newSuperscript = GUILayout.Toggle((flags & 128) != 0, I18n.Tr("fm_font_superscript"), GUILayout.Width(92f));
            bool newSubscript = GUILayout.Toggle((flags & 256) != 0, I18n.Tr("fm_font_subscript"), GUILayout.Width(88f));
            mask |= (newLower ? 8 : 0) | (newUpper ? 16 : 0) | (newSmallCaps ? 32 : 0)
                | (newSuperscript ? 128 : 0) | (newSubscript ? 256 : 0);
            GUILayout.EndHorizontal();
            if (mask != flags)
            {
                foreach (FmNode n in editorSelection) n.CountFontStyleFlags = mask;
                EditorPropertyChanged();
            }
        }

        // ---- Inline "?" help markers on editor property rows / 编辑器属性行的行内「?」帮助 ----
        // Click the small self-drawn "?" to toggle a word-wrapped HelpBox under that row.
        // Pure IMGUI — no image assets, identical in both loader variants. The open-state set
        // is keyed by help key, so sibling rows sharing one key (e.g. rain width/height/speed)
        // share one explanation. / 点击行内自绘小「?」，在该行下方展开/收起自动换行的说明框。
        // 纯 IMGUI 自绘，无图片资源，双加载器变体一致。展开状态按帮助键记录——共用同一键的
        // 兄弟行（如雨滴宽/高/速度）共享同一段说明。
        private readonly HashSet<string> fmOpenHelpKeys = new HashSet<string>();
        private static GUIStyle fmHelpButtonStyle;
        private static GUIStyle fmHelpLabelStyle;
        private static GUIStyle fmHelpBoxStyle;

        /// <summary>The "?" toggle itself — call INSIDE the row's horizontal. /
        /// 「?」开关按钮本体——须在该行的 horizontal 内调用。</summary>
        private void DrawEditorHelpMarker(string helpKey)
        {
            if (string.IsNullOrEmpty(helpKey)) return;
            if (fmHelpButtonStyle == null)
            {
                fmHelpButtonStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = 11,
                    alignment = TextAnchor.MiddleCenter,
                    margin = new RectOffset(1, 1, 1, 1),
                    padding = new RectOffset(0, 0, 0, 0)
                };
            }
            if (GUILayout.Button("?", fmHelpButtonStyle, GUILayout.Width(18f), GUILayout.Height(18f)))
            {
                if (!fmOpenHelpKeys.Remove(helpKey)) fmOpenHelpKeys.Add(helpKey);
            }
        }

        /// <summary>The expanded explanation — call AFTER the row's horizontal ends. /
        /// 展开的说明文本——须在该行 horizontal 结束后调用。</summary>
        private void DrawEditorHelpBox(string helpKey)
        {
            if (string.IsNullOrEmpty(helpKey) || !fmOpenHelpKeys.Contains(helpKey)) return;
            if (fmHelpLabelStyle == null)
                fmHelpLabelStyle = new GUIStyle(GUI.skin.label) { wordWrap = true, fontSize = 11 };
            // Derive from GUI.skin.box, NOT the named "HelpBox" — that style exists only in
            // Unity's EDITOR skin; the game's runtime GameSkin doesn't carry it, and the
            // by-name lookup spammed "Unable to find style 'HelpBox'" every IMGUI event.
            // / 从 GUI.skin.box 派生而非按名字取 "HelpBox"——后者只存在于 Unity 编辑器皮肤，
            // 游戏运行时的 GameSkin 没有，按名查找会在每个 IMGUI 事件刷找不到样式的错误。
            if (fmHelpBoxStyle == null)
                fmHelpBoxStyle = new GUIStyle(GUI.skin.box) { padding = new RectOffset(6, 6, 4, 4) };
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(fmHelpBoxStyle);
            GUILayout.Label(I18n.Tr(helpKey), fmHelpLabelStyle);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        /// <summary>Toggle row. `after` decides what follows the apply callback: by default the
        /// generic full-rebuild property change, but callers that already did their own (or that
        /// deliberately do NOT need a rebuild) pass the right one. The old unconditional
        /// EditorPropertyChanged() here silently undid every in-place refresh — EditorOnlyChanged
        /// for the editor-only flag, and the colour/gradient/glow/text-gradient handlers — so
        /// `fm_unselectable` still tore the whole overlay down despite the dedicated no-rebuild
        /// path added for it, and several call sites ran the callback twice (a second full-document
        /// snapshot plus a second teardown per click). / 开关行。`after` 决定 apply 回调之后做什么：
        /// 默认走通用的整层重建属性变更，但已自行处理（或**刻意**不需重建）的调用方要传入对应的那
        /// 个。此处此前无条件调用 EditorPropertyChanged()，悄悄抵消了所有就地刷新路径——
        /// 专为此加的不重建路径被架空，`fm_unselectable` 仍会拆掉整层覆盖层，且若干调用点会
        /// 跑两遍回调（每次点击多一次整档快照与多一次拆解）。
        /// </summary>
        private void DrawEditorToggle(string label, bool value, Action<bool> apply, string helpKey = null, Action after = null)
        {
            GUILayout.BeginHorizontal();
            bool newValue = GUILayout.Toggle(value, label);
            DrawEditorHelpMarker(helpKey);
            GUILayout.EndHorizontal();
            DrawEditorHelpBox(helpKey);
            if (newValue == value) return;
            editorInPlaceRefresh = false;
            apply(newValue);
            if (after != null) after();
            else if (!editorInPlaceRefresh) EditorPropertyChanged();
            editorInPlaceRefresh = false;
        }

        /// <summary>No-op "after" for call sites whose apply callback already did all the work. /
        /// apply 回调已完成全部工作的调用点所用的空「之后」动作。</summary>
        private static void EditorNothing() { }

        private void DrawEditorTextField(string label, string ctrl, string value, Action<string> apply, string helpKey = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(96f));
            DrawEditorHelpMarker(helpKey);
            string text = TextInputField(ctrl, value ?? "", GUILayout.MinWidth(120f));
            GUILayout.EndHorizontal();
            DrawEditorHelpBox(helpKey);
            if (!string.Equals(text, value ?? "", StringComparison.Ordinal)) apply(text ?? "");
        }

        /// <summary>Multi-aware float field. When the selection DISAGREES on the value the field
        /// shows "—" — the active node's value can no longer masquerade as the group's and get
        /// mass-applied by an accidental commit. "—" never parses; typing a number applies it to
        /// the whole selection. Uniform selections behave exactly as before.
        /// / 多选感知浮点字段。选区在该值上不一致时显示"—"——活动节点的值不再冒充组值、也不会
        /// 被一次意外提交群体覆盖；"—"永不解析，输入数字即应用到全部。一致时行为与从前相同。
        /// </summary>
        private void DrawEditorFloatField(string label, string ctrl, Func<FmNode, float> get, Action<float> apply, string helpKey = null, FmNode basis = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(96f));
            DrawEditorHelpMarker(helpKey);
            // The display basis must match whatever the apply callback uses as its reference —
            // see the X/Y fields, which apply a delta from the ACTIVE node. / 显示基准必须与
            // apply 回调所用的参照节点一致（X/Y 字段按活动节点求差）。
            float v0 = get(basis ?? editorSelection[0]);
            bool mixed = false;
            for (int i = 1; i < editorSelection.Count; i++)
            {
                if (Math.Abs(get(editorSelection[i]) - v0) > 0.001f)
                {
                    mixed = true;
                    break;
                }
            }
            // "R" round-trips exactly, so merely DRAWING the field can never differ from the value
            // it shows. The old "0.##" seed rounded: a node dragged to X=100.3333 rendered as
            // "100.33", the parse-back differed by more than the 0.001 threshold, and the field
            // therefore "committed" on EVERY Layout/Repaint — silently truncating the value, and
            // running the apply callback (history push + save + full overlay rebuild) once per
            // such field per repaint, i.e. dozens of full teardowns in a single frame. "R" can
            // also be long, so keep the width and fall back to a shorter form when it is huge.
            // "R" 可精确往返，因此**仅仅绘制**该字段永远不会与它显示的值不同。旧的 "0.##" 会
            // 四舍五入：被拖到 X=100.3333 的节点渲染为 "100.33"，回解后差异超过 0.001 阈值，
            // 于是该字段在**每个** Layout/Repaint 都"提交"一次——静默截断数值，并在每次重绘中
            // 对每个此类字段跑一遍 apply 回调（压历史 + 存盘 + 整层重建），即单帧数十次完整拆解。
            // "R" 可能较长，故限制显示宽度，过长时退回较短的写法。
            string seed = mixed ? "—" : FormatFloatForDisplay(v0);
            string text = TextInputField(ctrl, seed, GUILayout.Width(110f));
            // Strip the mixed marker before parsing: clicking in and typing leaves "—60", which
            // never parsed — the primary multi-select flow (select many, type one value, all
            // apply) was dead. / 解析前剥掉混合标记：点击后直接输入会留下"—60"，此前永不解析
            // ——多选的主流程（选一堆、输一个值、全体生效）等于失效。
            // Guard on a TEXT change, not a numeric difference: the field only means to apply what
            // the user actually committed. Comparing the parsed value against the displayed seed
            // re-triggered the apply whenever rounding made them differ. / 判定改为「文本是否变化」
            // 而非「数值是否不同」：该字段只想施加用户真正提交的内容；拿回解值与显示种子比较会在
            // 四舍五入造成差异时重新触发施加。
            string stripped = text.Replace("—", "").Trim();
            if (stripped != seed && float.TryParse(stripped, out float parsed) && IsFiniteFloat(parsed))
                apply(parsed);
            GUILayout.EndHorizontal();
            DrawEditorHelpBox(helpKey);
        }

        /// <summary>Shortest representation of a float that parses back to the same value, so the
        /// editor never shows a number different from the one it would commit. / 能回解为同一值的
        /// 最短浮点表示，使编辑器显示的数与它会提交的数永不不同。</summary>
        private static string FormatFloatForDisplay(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return "0";
            string s = v.ToString("R");
            // A very small value can round-trip to an absurdly long string; fall back to the
            // 6-decimal form rather than blowing out the 110px field. / 极小值可能往返成长串；退回
            // 6 位小数形式而不是撑爆 110px 的输入框。
            if (s.Length > 14)
            {
                string shorter = v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
                if (float.TryParse(shorter, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float back)
                    && back == v) return shorter;
            }
            return s;
        }

        private void DrawEditorPercentField(string label, string ctrl, Func<FmNode, float> getNormalized,
            Action<float> applyNormalized, string helpKey = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(96f));
            DrawEditorHelpMarker(helpKey);
            float value = getNormalized(editorSelection[0]);
            if (float.IsNaN(value) || float.IsInfinity(value)) value = 0f;
            bool mixed = false;
            for (int i = 1; i < editorSelection.Count; i++)
            {
                float other = getNormalized(editorSelection[i]);
                if (float.IsNaN(other) || float.IsInfinity(other) || Mathf.Abs(other - value) > 0.001f)
                {
                    mixed = true;
                    break;
                }
            }
            string text = TextInputField(ctrl, mixed ? "—" : (value * 100f).ToString("0.##"), GUILayout.Width(110f));
            if (float.TryParse(text.Replace("—", "").Trim(), out float percent) && IsFiniteFloat(percent))
            {
                float normalized = Mathf.Clamp01(percent / 100f);
                if (mixed || Mathf.Abs(normalized - value) > 0.001f) applyNormalized(normalized);
            }
            GUILayout.EndHorizontal();
            DrawEditorHelpBox(helpKey);
        }

        private void DrawEditorColorField(string label, float[] arr, Color fallback, Action<float[]> apply)
        {
            Color cur = NodeColor(arr, fallback);
            // Own control-name namespace: the settings window draws colour pickers too and resets
            // the shared sequence counter, so a shared "cpi_" prefix would have both windows'
            // Hex/RGB fields share one text buffer and one focus identity.
            // 独立的控件名命名空间：设置窗口同样绘制取色器并会重置共用计数器，前缀相同会让两个
            // 窗口的 Hex/RGB 输入框共用同一份缓冲与焦点身份。
            Color next = DrawColorPicker(label, cur, fallback, "fme_cpi_");
            if (next != cur)
            {
                apply(new[] { next.r, next.g, next.b, next.a });
                // Colours never need the overlay rebuilt — see EditorColorPropertyChanged.
                // 颜色永远不需要重建覆盖层——见 EditorColorPropertyChanged。
                EditorColorPropertyChanged();
            }
        }

        /// <summary>Per-node text outline / shadow. Off (the default) follows the global Display-tab
        /// style; turning it on seeds every field from the CURRENT globals first, so the node keeps
        /// the look it had and the user only changes what they came for — the same
        /// seed-then-override flow the rain overrides use. / 节点级文字描边/阴影。关闭（默认）跟随
        /// 显示页的全局样式；开启时先用「当前全局值」填充每个字段，使节点保持原有观感，用户只改
        /// 自己关心的项——与雨滴覆盖同款「先填充再覆盖」流程。</summary>
        private void DrawEditorTextStyleSection(FmNode first)
        {
            DrawEditorToggle(I18n.Tr("fm_text_style_custom"), first.UseCustomTextStyle, v =>
            {
                foreach (FmNode n in editorSelection)
                {
                    n.UseCustomTextStyle = v;
                    if (v) SeedTextStyleFromGlobals(n);
                }
            }, "fm_help_text_style_custom");
            if (!first.UseCustomTextStyle)
            {
                GUILayout.Label("<i>" + I18n.Tr("fm_text_style_following_global") + "</i>");
                return;
            }

            // Label text / 标签文字
            GUILayout.Label(I18n.Tr("fm_key_text_style"));
            DrawEditorToggle(I18n.Tr("fm_text_outline"), first.KeyTextOutlineEnabled, v =>
            {
                foreach (FmNode n in editorSelection) n.KeyTextOutlineEnabled = v;
            }, "fm_help_text_outline");
            if (first.KeyTextOutlineEnabled)
            {
                DrawEditorColorField(I18n.Tr("fm_text_outline_color"), first.KeyTextOutlineColor, Settings.Data.KeyTextOutlineColor,
                    arr => { foreach (FmNode n in editorSelection) n.KeyTextOutlineColor = arr; });
                DrawEditorFloatField(I18n.Tr("fm_text_outline_thickness"), "fme_kot_" + first.Id, n => n.KeyTextOutlineThickness, v =>
                {
                    foreach (FmNode n in editorSelection) n.KeyTextOutlineThickness = Mathf.Clamp(v, 0f, 1f);
                }, "fm_help_text_outline");
            }
            DrawEditorToggle(I18n.Tr("fm_text_shadow"), first.KeyTextShadowEnabled, v =>
            {
                foreach (FmNode n in editorSelection) n.KeyTextShadowEnabled = v;
            }, "fm_help_text_shadow");
            if (first.KeyTextShadowEnabled)
            {
                DrawEditorColorField(I18n.Tr("fm_text_shadow_color"), first.KeyTextShadowColor, Settings.Data.KeyTextShadowColor,
                    arr => { foreach (FmNode n in editorSelection) n.KeyTextShadowColor = arr; });
                DrawEditorFloatField(I18n.Tr("fm_text_shadow_offset_x"), "fme_ksx_" + first.Id, n => n.KeyTextShadowOffsetX, v =>
                {
                    foreach (FmNode n in editorSelection) n.KeyTextShadowOffsetX = Mathf.Clamp(v, -20f, 20f);
                }, "fm_help_text_shadow");
                DrawEditorFloatField(I18n.Tr("fm_text_shadow_offset_y"), "fme_ksy_" + first.Id, n => n.KeyTextShadowOffsetY, v =>
                {
                    foreach (FmNode n in editorSelection) n.KeyTextShadowOffsetY = Mathf.Clamp(v, -20f, 20f);
                }, "fm_help_text_shadow");
                DrawEditorFloatField(I18n.Tr("fm_text_shadow_softness"), "fme_kss_" + first.Id, n => n.KeyTextShadowSoftness, v =>
                {
                    foreach (FmNode n in editorSelection) n.KeyTextShadowSoftness = Mathf.Clamp(v, 0f, 64f);
                }, "fm_help_text_shadow");
            }

            // Count text / 计数文字
            GUILayout.Label(I18n.Tr("fm_count_text_style"));
            DrawEditorToggle(I18n.Tr("fm_text_outline"), first.CountTextOutlineEnabled, v =>
            {
                foreach (FmNode n in editorSelection) n.CountTextOutlineEnabled = v;
            }, "fm_help_text_outline");
            if (first.CountTextOutlineEnabled)
            {
                DrawEditorColorField(I18n.Tr("fm_text_outline_color"), first.CountTextOutlineColor, Settings.Data.CountTextOutlineColor,
                    arr => { foreach (FmNode n in editorSelection) n.CountTextOutlineColor = arr; });
                DrawEditorFloatField(I18n.Tr("fm_text_outline_thickness"), "fme_cot_" + first.Id, n => n.CountTextOutlineThickness, v =>
                {
                    foreach (FmNode n in editorSelection) n.CountTextOutlineThickness = Mathf.Clamp(v, 0f, 1f);
                }, "fm_help_text_outline");
            }
            DrawEditorToggle(I18n.Tr("fm_text_shadow"), first.CountTextShadowEnabled, v =>
            {
                foreach (FmNode n in editorSelection) n.CountTextShadowEnabled = v;
            }, "fm_help_text_shadow");
            if (first.CountTextShadowEnabled)
            {
                DrawEditorColorField(I18n.Tr("fm_text_shadow_color"), first.CountTextShadowColor, Settings.Data.CountTextShadowColor,
                    arr => { foreach (FmNode n in editorSelection) n.CountTextShadowColor = arr; });
                DrawEditorFloatField(I18n.Tr("fm_text_shadow_offset_x"), "fme_csx_" + first.Id, n => n.CountTextShadowOffsetX, v =>
                {
                    foreach (FmNode n in editorSelection) n.CountTextShadowOffsetX = Mathf.Clamp(v, -20f, 20f);
                }, "fm_help_text_shadow");
                DrawEditorFloatField(I18n.Tr("fm_text_shadow_offset_y"), "fme_csy_" + first.Id, n => n.CountTextShadowOffsetY, v =>
                {
                    foreach (FmNode n in editorSelection) n.CountTextShadowOffsetY = Mathf.Clamp(v, -20f, 20f);
                }, "fm_help_text_shadow");
                DrawEditorFloatField(I18n.Tr("fm_text_shadow_softness"), "fme_css_" + first.Id, n => n.CountTextShadowSoftness, v =>
                {
                    foreach (FmNode n in editorSelection) n.CountTextShadowSoftness = Mathf.Clamp(v, 0f, 64f);
                }, "fm_help_text_shadow");
            }

            if (GUILayout.Button(I18n.Tr("fm_text_style_follow_global"), GUILayout.MinWidth(110f)))
            {
                foreach (FmNode n in editorSelection) n.UseCustomTextStyle = false;
                EditorPropertyChanged();
            }
        }

        /// <summary>Named-easing picker for the editor. Unlike the settings window's version this one
        /// lives inside the scrolling property panel, so it renders a flat button grid instead of a
        /// nested scroll view (a scroll view inside a scroll view is unusable with a mouse wheel).
        /// / 编辑器用的命名缓动选择器。与设置窗口的版本不同，它位于可滚动的属性面板内，故渲染为平铺
        /// 按钮网格而非嵌套滚动区（滚动区套滚动区用滚轮根本没法操作）。</summary>
        private void DrawEditorEasingSelector(string ctrl, string current, Action<string> apply)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.Tr("press_anim_easing"), GUILayout.Width(96f));
            string shown = Util.KvEasing.Normalize(current);
            if (GUILayout.Button(shown, GUILayout.Width(140f)))
                fmEasingPicker = fmEasingPicker == ctrl ? null : ctrl;
            GUILayout.EndHorizontal();
            if (fmEasingPicker != ctrl) return;

            // Two per row: a curve preview needs real width to be readable, and 27 entries at two per
            // row is 14 rows — acceptable inside the panel's own scroll view. / 每行两个：曲线预览需要
            // 真实宽度才可读，27 个条目每行两个共 14 行——在面板自身的滚动区内可以接受。
            GUILayout.BeginVertical("box");
            for (int i = 0; i < Util.KvEasing.Names.Length; i += 2)
            {
                GUILayout.BeginHorizontal();
                DrawEditorEasingCell(ctrl, Util.KvEasing.Names[i], shown, apply);
                if (i + 1 < Util.KvEasing.Names.Length)
                    DrawEditorEasingCell(ctrl, Util.KvEasing.Names[i + 1], shown, apply);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
        }

        private void DrawEditorEasingCell(string ctrl, string name, string shown, Action<string> apply)
        {
            bool selected = string.Equals(name, shown, StringComparison.OrdinalIgnoreCase);
            if (GUILayout.Button((selected ? "✓ " : "  ") + name, GUILayout.MinWidth(120f)))
            {
                apply(name);
                fmEasingPicker = null;
                EditorPropertyChanged();
            }
            DrawEasingCurve(new Rect(GUILayoutUtility.GetRect(46f, 18f).position, new Vector2(46f, 18f)), name);
        }

        /// <summary>Seed a node's text-style override from the current global settings — called when
        /// the user first turns the override on, so enabling it never changes the rendered text.
        /// / 用当前全局设置填充节点的文字样式覆盖——用户首次开启覆盖时调用，使开启动作本身绝不改变
        /// 已渲染的文字。</summary>
        private static void SeedTextStyleFromGlobals(FmNode n)
        {
            ProfileData d = Settings.Data;
            n.KeyTextOutlineEnabled = d.EnableKeyTextOutline;
            n.KeyTextOutlineColor = ColorArray(d.KeyTextOutlineColor);
            n.KeyTextOutlineThickness = d.KeyTextOutlineThickness;
            n.KeyTextShadowEnabled = d.EnableKeyTextShadow;
            n.KeyTextShadowColor = ColorArray(d.KeyTextShadowColor);
            n.KeyTextShadowOffsetX = d.KeyTextShadowOffsetX;
            n.KeyTextShadowOffsetY = d.KeyTextShadowOffsetY;
            n.KeyTextShadowSoftness = d.KeyTextShadowSoftness;
            n.CountTextOutlineEnabled = d.EnableCountTextOutline;
            n.CountTextOutlineColor = ColorArray(d.CountTextOutlineColor);
            n.CountTextOutlineThickness = d.CountTextOutlineThickness;
            n.CountTextShadowEnabled = d.EnableCountTextShadow;
            n.CountTextShadowColor = ColorArray(d.CountTextShadowColor);
            n.CountTextShadowOffsetX = d.CountTextShadowOffsetX;
            n.CountTextShadowOffsetY = d.CountTextShadowOffsetY;
            n.CountTextShadowSoftness = d.CountTextShadowSoftness;
        }

        private static float[] ColorArray(Color c) => new[] { c.r, c.g, c.b, c.a };

        /// <summary>Set by every in-place refresh handler below. DrawEditorToggle resets it before
        /// the apply callback and skips the generic full rebuild when a handler already ran — so a
        /// toggle wired to a glow/gradient/colour/text-gradient refresh does NOT additionally tear
        /// down and rebuild the whole overlay, and does not push a second document snapshot.
        /// / 下方每个就地刷新处理器都会置位。DrawEditorToggle 在调用 apply 前清零，并在有处理器
        /// 跑过时跳过通用整层重建——因此接到光效/渐变/颜色/文字渐变刷新的开关**不会**额外拆掉
        /// 重建整层覆盖层，也不会压第二份整档快照。
        /// </summary>
        private bool editorInPlaceRefresh;

        private void EditorGlowPropertyChanged()
        {
            editorInPlaceRefresh = true;
            PushEditorHistoryNudge();
            SaveSettingsFromGui();
            foreach (FmNode node in editorSelection)
            {
                if (node == null) continue;
                ApplyCustomGlow(node, node.RuntimeKey != null && node.RuntimeKey.isPressed);
            }
        }

        private void EditorTextGradientPropertyChanged()
        {
            editorInPlaceRefresh = true;
            PushEditorHistoryNudge();
            SaveSettingsFromGui();
            foreach (FmNode node in editorSelection)
            {
                if (node == null || node.RuntimeKey == null) continue;
                bool pressed = node.RuntimeKey.isPressed;
                if (node.NodeType == 0 || node.NodeType == 3)
                    ApplyCustomKeyColors(node.RuntimeKey, node, pressed);
                else
                    ApplyCustomSpecialColors(node.RuntimeKey, node, pressed);
            }
            TickTextGradients();
        }

        private void EditorGradientPropertyChanged()
        {
            editorInPlaceRefresh = true;
            PushEditorHistoryNudge();
            SaveSettingsFromGui();
            foreach (FmNode node in editorSelection)
            {
                if (node == null) continue;
                bool pressed = node.RuntimeKey != null && node.RuntimeKey.isPressed;
                ApplyCustomBackgroundGradient(node, pressed);
                ApplyCustomOutlineGradient(node, pressed);
            }
        }

        /// <summary>Colour-only property change: refresh every affected visual IN PLACE instead of
        /// rebuilding the whole overlay. Dragging an R/G/B/A slider fires this on every MouseDrag
        /// event (60-120/s), and each one used to run ResetKeyViewer — destroy and recreate every
        /// key GameObject, re-init both shape layers and clear every rain drop. The glow, gradient
        /// and text-gradient fields already had in-place paths; this covers the solid colours too. /
        /// 仅颜色变更：就地刷新受影响的视觉，不再重建整层覆盖层。拖动 R/G/B/A 滑杆时每个
        /// MouseDrag 事件都会触发它（每秒 60-120 次），此前每次都跑一遍 ResetKeyViewer——
        /// 销毁重建所有按键 GameObject、重置两层形状 mesh 并清空全部雨滴。光效、渐变与文字
        /// 渐变字段已有就地路径，此处把实色也纳入。</summary>
        private void EditorColorPropertyChanged()
        {
            editorInPlaceRefresh = true;
            RecalculateCustomTotalCount();
            PushEditorHistoryNudge();
            SaveSettingsFromGui();
            foreach (FmNode node in editorSelection)
            {
                if (node == null) continue;
                bool pressed = node.RuntimeKey != null && node.RuntimeKey.isPressed;
                if (node.RuntimeKey != null)
                {
                    if (node.NodeType == 0 || node.NodeType == 3) ApplyCustomKeyColors(node.RuntimeKey, node, pressed);
                    else ApplyCustomSpecialColors(node.RuntimeKey, node, pressed);
                }
                ApplyCustomGlow(node, pressed);
                ApplyCustomBackgroundGradient(node, pressed);
                ApplyCustomOutlineGradient(node, pressed);
            }
            // Text outline/shadow colours live in the cached font materials (same path the
            // outline-width/softness sliders use). / 文字描边/阴影颜色走缓存字体材质
            // （与描边宽度/柔和度滑杆同一条路径）。
            UpdateAllFonts();
            TickTextGradients();
            // Rain drop colours are baked into each drop when it is created, so in-flight drops keep
            // the old colour. The old rebuild dropped them; clearing just the drops keeps that
            // behaviour at a fraction of the cost (the next press picks up the new colour).
            // 雨滴颜色在创建时烙入，在飞的雨滴仍是旧色。旧的重建路径会清空它们；这里只清雨滴，
            // 成本低得多，且下一次按压即用新颜色。
            rainSystem.ClearActiveDrops(Keys);
        }

        private void EditorPropertyChanged()
        {
            // CountInTotal is a membership switch; reconcile the global accumulator before the
            // post-change snapshot so a toggle cannot leave Total/KPS state inconsistent.
            // CountInTotal 是成员开关；先重算全局累计值，再记录后置快照，避免切换后 Total 状态漂移。
            RecalculateCustomTotalCount();
            // Property edits are undoable too: record the POST-change state once per change burst
            // (the 0.4s nudge window coalesces slider drags), so Ctrl+Z steps back to the value
            // the edit started from. Without this, Ctrl+Z after a color tweak didn't revert it —
            // it undid the last STRUCTURAL op instead, destroying unrelated work. /
            // 属性修改同样可撤销：每个修改突发记录一次变更后状态（0.4 秒微调窗口合并滑杆拖动），
            // Ctrl+Z 因而回退到该次编辑开始前的值。此前改完颜色按 Ctrl+Z 不会回退——反而会误撤销
            // 上一个结构性操作，破坏无关改动。
            PushEditorHistoryNudge();
            SaveSettingsFromGui();
            // Video entries are generation-stamped and GetOrCreate compares path, loop and
            // bucketed size. Do not release every selected video on unrelated property edits:
            // that needlessly restarts the decoder for opacity/position/color changes. Path and
            // loop changes still force replacement inside GetOrCreate. / 视频条目按代次标记管理，
            // GetOrCreate 会比较路径、循环和分桶尺寸；不要因无关属性修改就释放选中视频，否则
            // 透明度/位置/颜色调整都会重启解码器。路径和循环变化仍会由 GetOrCreate 自动替换。
            RequestEditorRebuild();
        }

        private void EditorImportImages()
        {
            foreach (FmNode node in editorSelection)
            {
                if (node == null || node.NodeType != 3) continue;
                string p = node.ImagePath?.Trim();
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    string abs = Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(Loader.ResolveModPath(), p));
                    if (!File.Exists(abs)) continue;
                    string dir = Path.Combine(Loader.ResolveModPath(), "CustomImages");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string dest = Path.Combine(dir, Path.GetFileName(abs));
                    if (!string.Equals(Path.GetFullPath(abs), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                        File.Copy(abs, dest, true);
                    node.ImagePath = Path.GetFileName(abs);
                }
                catch (Exception e)
                {
                    Loader.Error($"KeyViewer: image import failed: {e.Message}");
                }
            }
            // Importing overwrites files under CustomImages\ — drop the canvas texture cache or
            // the editor keeps drawing the PRE-import image until restart. The cached Texture2Ds
            // are ours, so they must be destroyed: Clear() alone orphaned every one of them and
            // leaked the GPU memory, once per import. / 导入会覆盖 CustomImages\ 下的文件——清掉
            // 画布贴图缓存，否则编辑器到重启前一直画的是导入前的旧图。这些 Texture2D 归我们所有，
            // 必须销毁：仅 Clear() 会把每一张变成孤儿并泄漏显存，每次导入一次。
            DestroyEditorTextures();
            EditorPropertyChanged();
        }

        private void DestroyEditorTextures()
        {
            foreach (Texture2D tex in fmTexCache.Values)
                if (tex != null) UnityEngine.Object.Destroy(tex);
            fmTexCache.Clear();
            fmTexFailures.Clear();
        }

        private void OpenCustomImagesDir()
        {
            try
            {
                string dir = Path.Combine(Loader.ResolveModPath(), "CustomImages");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: failed to open folder: {e.Message}");
            }
        }

        /// <summary>Editor-scoped stale-buffer GC: only "fme_" entries belong to this window's
        /// pass; settings-window buffers are managed by EndTextInputPass and are never touched
        /// here (and vice versa). / 编辑器范围的陈旧缓冲回收：只处理 "fme_" 前缀；设置窗口的
        /// 缓冲归 EndTextInputPass 管，二者互不越界。</summary>
        private void EditorGcBuffers()
        {
            if (textInputBuffer.Count == 0) return;
            string focused = GUI.GetNameOfFocusedControl();
            fmScratchKeys.Clear();
            foreach (string key in textInputBuffer.Keys)
            {
                if (!key.StartsWith("fme_", StringComparison.Ordinal)) continue;
                if (textCtrlsDrawnThisPass.Contains(key) || key == focused) continue;
                fmScratchKeys.Add(key);
            }
            for (int i = 0; i < fmScratchKeys.Count; i++)
                textInputBuffer.Remove(fmScratchKeys[i]);
            textCtrlsDrawnThisPass.Clear();
        }
    }
}
