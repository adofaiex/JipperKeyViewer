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

        private readonly List<FmNode> editorSelection = new List<FmNode>();
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
        private bool fmAxisLocked;
        private bool fmLockToX;
        private float fmDragTotalX;
        private float fmDragTotalY;
        private string fmPendingSnapshot;
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
        private GUIStyle fmHintStyle;
        private readonly Dictionary<string, Texture2D> fmTexCache = new Dictionary<string, Texture2D>();
        private readonly List<string> fmScratchKeys = new List<string>();
        private readonly List<FmNode> fmOrderBuffer = new List<FmNode>();
        private bool fmGroupsExpanded;
        private string fmActiveGroupId = ""; // target group for new stat panels / 新建面板的目标组
        private Rect fmLastCanvasRect;
        private int fmResizeHandle = -1;
        private bool fmResizeMoved;
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

            fmPropsScroll = GUILayout.BeginScrollView(fmPropsScroll, GUILayout.Height(280f));
            DrawEditorProperties();
            GUILayout.EndScrollView();

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
                // Closing the editor flushes any pending debounced changes. /
                // 关闭编辑器时冲刷挂起的去抖变更。
                editorOpen = false;
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
                PushEditorHistory();
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
                EditorMutated();
                return;
            }
            // Flush the CURRENT layout to its file before switching. / 切换前先把当前布局落盘。
            SaveCurrentProfile();
            // Free profile name: "16K-预设", "16K-预设 2", ... / 空闲配置名。
            string baseName = KeyLayoutNames[styleIndex] + "-" + I18n.Tr("fm_presets");
            var existing = new HashSet<string>(Settings.ProfileNames ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            string name = baseName;
            for (int k = 2; existing.Contains(name); k++) name = baseName + " " + k;
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
            editorSelection.Clear();
            // Undo snapshots belong to the previous profile — restoring them here would write the
            // old layout's nodes into the preset profile. / 撤销快照属于原配置——在这里恢复会把
            // 旧布局的节点写进预设配置。
            editorHistory.Clear();
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
            PushEditorHistory();
            Settings.Data.CustomNodes = new List<FmNode>();
            EnsureCustomNodes(); // wipe every group too — no members survive / 连组一并清——无成员存活
            editorSelection.Clear();
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

        private void EditorAddNode(int type)
        {
            string targetGroup = fmActiveGroupId;
            if ((type == 1 || type == 2) && GroupHasStat(targetGroup, type)) return;
            if (type != 3 && KeyLikeCountInGroup(targetGroup) >= CustomKeyNodeCap) return;
            if (type == 3 && Settings.Data.CustomNodes.Count(n => n != null && n.NodeType == 3) >= 8) return;
            PushEditorHistory();
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
            EditorMutated();
        }

        private void EditorDeleteSelection()
        {
            if (editorSelection.Count == 0) return;
            PushEditorHistory();
            for (int i = editorSelection.Count - 1; i >= 0; i--)
                Settings.Data.CustomNodes.Remove(editorSelection[i]);
            editorSelection.Clear();
            // Prune groups that just lost their last member. /
            // 剔除刚刚失去全部成员的组。
            EnsureCustomNodes();
            EditorMutated();
        }

        private void EditorCopySelection()
        {
            editorClipboard.Clear();
            editorPasteSerial = 0;
            foreach (FmNode node in editorSelection)
                if (node != null) editorClipboard.Add(node.Clone());
        }

        private void EditorPaste()
        {
            if (editorClipboard.Count == 0) return;
            PushEditorHistory();
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
            EditorMutated();
        }

        private void EditorSelectAll()
        {
            editorSelection.Clear();
            foreach (FmNode node in Settings.Data.CustomNodes)
                if (node != null) editorSelection.Add(node);
        }

        /// <summary>Align the selection (modes 0-5: left / horizontal-center / right / top /
        /// vertical-center / bottom) against its own bounds. Undoable property change. /
        /// 将选区按自身包围盒对齐（模式 0-5：左 / 水平居中 / 右 / 顶 / 垂直居中 / 底）。
        /// 可撤销的属性变更。</summary>
        private void EditorAlignSelection(int mode)
        {
            List<FmNode> sel = new List<FmNode>();
            foreach (FmNode n in editorSelection) if (n != null) sel.Add(n);
            if (sel.Count < 2) return;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            foreach (FmNode n in sel)
            {
                minX = Math.Min(minX, n.X); maxX = Math.Max(maxX, n.X + n.Width);
                minY = Math.Min(minY, n.Y); maxY = Math.Max(maxY, n.Y + n.Height);
            }
            PushEditorHistory();
            foreach (FmNode n in sel)
            {
                switch (mode)
                {
                    case 0: n.X = minX; break;
                    case 1: n.X = minX + (maxX - minX - n.Width) * 0.5f; break;
                    case 2: n.X = maxX - n.Width; break;
                    case 3: n.Y = minY; break;
                    case 4: n.Y = minY + (maxY - minY - n.Height) * 0.5f; break;
                    case 5: n.Y = maxY - n.Height; break;
                }
            }
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
            PushEditorHistory();
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
            PushEditorHistory();
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
            EditorMutated();
        }

        private void EditorMutated()
        {
            // Structural changes save IMMEDIATELY (they are discrete, low-rate events and the
            // user's layout must survive a crash). / 结构性变更立即落盘（离散低频事件，布局必须
            // 在崩溃后存活）。
            SaveSettings();
            Loader.Log($"KeyViewer: saved {Settings.Data.CustomNodes.Count} custom nodes");
            if (KeyViewerObject != null && IsCustomLayout) ResetKeyViewer();
        }

        private void RequestEditorRebuild()
        {
            if (KeyViewerObject != null && IsCustomLayout) ResetKeyViewer();
        }

        // ======================== history / 撤销 ========================

        private string SnapshotCustomNodes()
        {
            return JsonConvert.SerializeObject(Settings.Data.CustomNodes);
        }
        private void PushEditorHistory()
        {
            try
            {
                editorHistory.Push(SnapshotCustomNodes());
            }
            catch (Exception e)
            {
                Loader.Warning($"KeyViewer: editor snapshot failed: {e.Message}");
            }
        }

        private void EditorUndo()
        {
            string current = SnapshotCustomNodes();
            RestoreEditorSnapshot(editorHistory.Undo(current));
        }

        private void EditorRedo()
        {
            string current = SnapshotCustomNodes();
            RestoreEditorSnapshot(editorHistory.Redo(current));
        }

        private void RestoreEditorSnapshot(string snapshot)
        {
            if (snapshot == null) return;
            try
            {
                Settings.Data.CustomNodes = string.IsNullOrEmpty(snapshot)
                    ? new List<FmNode>()
                    : JsonConvert.DeserializeObject<List<FmNode>>(snapshot) ?? new List<FmNode>();
                EnsureCustomNodes();
                editorSelection.RemoveAll(n => !Settings.Data.CustomNodes.Contains(n));
                EditorMutated();
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: editor snapshot did not parse: {e.Message}");
            }
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
                HandleEditorMinimapDrag(rect, e);
                if (e.type == EventType.MouseDrag || e.type == EventType.MouseUp || e.type == EventType.MouseDown)
                    e.Use();
                return;
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
                        // First real movement: commit the pre-drag snapshot taken at press. /
                        // 首次真实移动：提交按下时预留的拖拽前快照。
                        PushEditorHistorySnapshot(fmPendingSnapshot);
                        fmPendingSnapshot = null;
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
            fmPendingSnapshot = null;
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
            fmAxisLocked = false;
            try
            {
                fmPendingSnapshot = SnapshotCustomNodes();
            }
            catch (Exception ex)
            {
                fmPendingSnapshot = null;
                Loader.Warning($"KeyViewer: editor snapshot failed: {ex.Message}");
            }
        }

        private void EndNodeDrag()
        {
            if (fmDragMoved)
            {
                SaveSettingsFromGui();
                RequestEditorRebuild();
            }
            fmAlignLines.Clear();
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
                    keyShapeLayer.SetRect(key.shapeSlot, node.X, cy - node.Height * 0.5f, node.Width, node.Height);
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
            try
            {
                fmPendingSnapshot = SnapshotCustomNodes();
            }
            catch (Exception ex)
            {
                fmPendingSnapshot = null;
                Loader.Warning($"KeyViewer: editor snapshot failed: {ex.Message}");
            }
        }

        private void UpdateEditorResize(Rect rect, Event e)
        {
            Vector2 canvasPos = EditorCanvasOf(rect, e.mousePosition);
            if (!fmResizeMoved)
            {
                if ((canvasPos - fmPressCanvas).sqrMagnitude < 0.01f) return;
                PushEditorHistorySnapshot(fmPendingSnapshot);
                fmPendingSnapshot = null;
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
        }

        private void EndEditorResize()
        {
            fmResizeOrig.Clear();
            if (fmResizeMoved)
            {
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
                    SaveSettingsFromGui();
                }
                bool vis = GUILayout.Toggle(g.Visible, I18n.Tr("fm_group_show"), GUILayout.MinWidth(48f));
                if (vis != g.Visible)
                {
                    g.Visible = vis;
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
                    PushEditorHistory();
                    foreach (FmNode n in editorSelection) n.GroupId = g.Id;
                    EditorMutated();
                }
                GUI.enabled = true;
                if (GUILayout.Button(new GUIContent("✕", I18n.Tr("fm_gtip_del")), GUILayout.Width(30f)))
                {
                    PushEditorHistory();
                    string deadId = g.Id;
                    groups.RemoveAt(i);
                    foreach (FmNode n in Settings.Data.CustomNodes)
                        if (n != null && n.GroupId == deadId) n.GroupId = "";
                    if (fmActiveGroupId == deadId) fmActiveGroupId = "";
                    EditorMutated();
                    break;
                }
                GUILayout.EndHorizontal();
            }
            if (GUILayout.Button(I18n.Tr("fm_group_add"), GUILayout.MinWidth(140f)))
            {
                PushEditorHistory();
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
                SaveSettingsFromGui();
        }
            }

        private void PushEditorHistorySnapshot(string snapshot)
        {
            if (snapshot == null) return;
            try
            {
                editorHistory.Push(snapshot);
            }
            catch (Exception e)
            {
                Loader.Warning($"KeyViewer: editor snapshot failed: {e.Message}");
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
        private void EditorSnapDrag(float dx, float dy, out float corrX, out float corrY)
        {
            corrX = 0f;
            corrY = 0f;
            float snapLimit = 5f / Mathf.Max(0.05f, fmZoom);
            List<FmNode> refs = new List<FmNode>();
            foreach (FmNode node in Settings.Data.CustomNodes)
                if (node != null && !fmDragStart.ContainsKey(node)) refs.Add(node);

            float bestX = float.MaxValue, bestY = float.MaxValue;
            float atX = 0f, atY = 0f;
            FmNode refXNode = null, refYNode = null;

            foreach (FmNode node in fmDragStart.Keys)
            {
                Vector2 start = fmDragStart[node];
                float w = node.Width, h = node.Height;
                float[] edgesX = { start.x + dx, start.x + dx + w * 0.5f, start.x + dx + w };
                float[] edgesY = { start.y + dy, start.y + dy + h * 0.5f, start.y + dy + h };

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
            foreach (KeyValuePair<FmNode, Vector2> kv in fmDragStart)
            {
                FmNode node = kv.Key;
                float w = node.Width, h = node.Height;
                float x = kv.Value.x + fx, y = kv.Value.y + fy;
                float[] edges = vertical
                    ? new[] { x, x + w * 0.5f, x + w }
                    : new[] { y, y + h * 0.5f, y + h };
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

        private void DrawEditorNode(FmNode node, Rect sr, Rect canvasRect)
        {
            bool selected = editorSelection.Contains(node);
            float dim = node.Hidden ? 0.25f : node.Unselectable && !selected ? 0.45f : 1f;
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
            }
            else
            {
                ProfileData d = Settings.Data;
                Color bg = node.UseCustomColor ? NodeColor(node.Bg, d.Background) : d.Background;
                Color ol = node.UseCustomColor ? NodeColor(node.Outline, d.Outline) : d.Outline;
                GUIUtils.DrawRect(ClipRect(sr, canvasRect), WithAlpha(bg, dim));
                DrawRectOutline(ClipRect(sr, canvasRect), WithAlpha(ol, dim), 1.5f);
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
            if (fmTexCache.TryGetValue(path, out Texture2D cached) && cached != null) return cached;
            Texture2D tex = KvImageLoader.LoadTexture(path);
            fmTexCache[path] = tex;
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
            if (fmResizing && e.type == EventType.MouseDrag)
            {
                editorRect.width = Mathf.Clamp(e.mousePosition.x - editorRect.x + 12f, FmMinWindowWidth,
                    Mathf.Max(FmMinWindowWidth, Screen.width - editorRect.x));
                editorRect.height = Mathf.Clamp(e.mousePosition.y - editorRect.y + 12f, FmMinWindowHeight,
                    Mathf.Max(FmMinWindowHeight, Screen.height - editorRect.y));
                e.Use();
            }
            if (fmResizing && e.type == EventType.MouseUp)
            {
                fmResizing = false;
                e.Use();
            }
        }

        // ---- keyboard shortcuts / 快捷键 ----

        private void HandleEditorShortcuts(Event e)
        {
            bool typing = GUI.GetNameOfFocusedControl()?.StartsWith("fme_", StringComparison.Ordinal) == true;
            if (typing) return;
            if (fmCaptureNode != null) return;
            if (fmCaptureGhostNode != null) return;
            bool ctrl = e.control;
            switch (e.keyCode)
            {
                case KeyCode.Delete:
                case KeyCode.Backspace:
                    EditorDeleteSelection();
                    e.Use();
                    return;
                case KeyCode.Escape:
                    editorSelection.Clear();
                    e.Use();
                    return;
                case KeyCode.Z when ctrl:
                    if (e.shift) EditorRedo();
                    else EditorUndo();
                    e.Use();
                    return;
                case KeyCode.Y when ctrl:
                    EditorRedo();
                    e.Use();
                    return;
                case KeyCode.C when ctrl:
                    EditorCopySelection();
                    e.Use();
                    return;
                case KeyCode.V when ctrl:
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
            try
            {
                editorHistory.PushNudge(SnapshotCustomNodes(), Time.unscaledTime);
            }
            catch (Exception ex)
            {
                Loader.Warning($"KeyViewer: editor snapshot failed: {ex.Message}");
            }
            foreach (FmNode node in editorSelection)
            {
                node.X += dx;
                node.Y += dy;
            }
            e.Use();
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

            DrawEditorFloatField(I18n.Tr("fm_pos_x"), "fme_x_" + first.Id, n => n.X, v =>
            {
                float delta = v - first.X;
                foreach (FmNode n in editorSelection) n.X += delta;
                EditorPropertyChanged();
            }, "fm_help_pos_x");
            DrawEditorFloatField(I18n.Tr("fm_pos_y"), "fme_y_" + first.Id, n => n.Y, v =>
            {
                float delta = v - first.Y;
                foreach (FmNode n in editorSelection) n.Y += delta;
                EditorPropertyChanged();
            }, "fm_help_pos_y");
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
            if (first.NodeType == 0 || (first.NodeType == 3 && !string.IsNullOrWhiteSpace(first.KeyBind)))
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
            DrawEditorToggle(I18n.Tr("fm_unselectable"), first.Unselectable, v => { foreach (FmNode n in editorSelection) n.Unselectable = v; });
            DrawEditorToggle(I18n.Tr("fm_hidden"), first.Hidden, v => { foreach (FmNode n in editorSelection) n.Hidden = v; });
            DrawEditorFontSize(first);
            DrawEditorToggle(I18n.Tr("fm_hide_label"), first.HideLabel, v => { foreach (FmNode n in editorSelection) n.HideLabel = v; });
            DrawEditorToggle(I18n.Tr("fm_hide_count"), first.HideCount, v => { foreach (FmNode n in editorSelection) n.HideCount = v; });

            // Layer group assignment / 图层组指派
            if (!string.IsNullOrEmpty(first.GroupId))
            {
                FmLayerGroup grp = Settings.Data.LayerGroups.FirstOrDefault(g => g != null && g.Id == first.GroupId);
                GUILayout.Label(I18n.Tr("fm_group") + ": " + (grp != null ? grp.Name : first.GroupId));
                if (GUILayout.Button(I18n.Tr("fm_group_ungroup"), GUILayout.MinWidth(140f)))
                {
                    PushEditorHistory();
                    foreach (FmNode n in editorSelection) n.GroupId = "";
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
                // just zero. The global TotalCount follows by each node's exact delta so the
                // Total panels stay truthful; unselected keys are never touched.
                // 手动输入计数：把选中计数节点的 Count 设为任意值，而非只能清零。全局
                // TotalCount 按各节点的增减差额同步，Total 面板保持真实；未选中的按键绝不受影响。
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
                        if (n.CountInTotal) Settings.Data.TotalCount += typedCount - n.Count;
                        n.Count = typedCount;
                    }
                    if (Settings.Data.TotalCount < 0) Settings.Data.TotalCount = 0;
                    RefreshAllCountDisplay();
                    SaveSettingsFromGui();
                }
                GUILayout.EndHorizontal();
                DrawEditorHelpBox("fm_help_count_value");

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(I18n.Tr("fm_reset_count"), GUILayout.MinWidth(140f)))
                {
                    foreach (FmNode n in editorSelection)
                    {
                        if (n == null || (n.NodeType != 0 && !(n.NodeType == 3 && !string.IsNullOrWhiteSpace(n.KeyBind)))) continue;
                        // A CountInTotal key contributed exactly n.Count presses to the global
                        // TotalCount — take only those back, never other keys' share. The clamp
                        // guards profiles where the two counters drifted apart (hand edits).
                        // CountInTotal 的按键恰好向全局 TotalCount 贡献了 n.Count 次——只扣回这部分，
                        // 绝不动其它按键的份额。钳制防两个计数器失配的手改配置。
                        if (n.CountInTotal)
                        {
                            Settings.Data.TotalCount -= n.Count;
                            if (Settings.Data.TotalCount < 0) Settings.Data.TotalCount = 0;
                        }
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
                    RefreshAllCountDisplay();
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
            PushEditorHistory();
            node.NodeType = newType;
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
                fmCaptureNode = capturing ? null : node;
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

        private void DrawEditorToggle(string label, bool value, Action<bool> apply, string helpKey = null)
        {
            GUILayout.BeginHorizontal();
            bool newValue = GUILayout.Toggle(value, label);
            DrawEditorHelpMarker(helpKey);
            GUILayout.EndHorizontal();
            DrawEditorHelpBox(helpKey);
            if (newValue == value) return;
            apply(newValue);
            EditorPropertyChanged();
        }

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
        private void DrawEditorFloatField(string label, string ctrl, Func<FmNode, float> get, Action<float> apply, string helpKey = null)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(96f));
            DrawEditorHelpMarker(helpKey);
            float v0 = get(editorSelection[0]);
            bool mixed = false;
            for (int i = 1; i < editorSelection.Count; i++)
            {
                if (Math.Abs(get(editorSelection[i]) - v0) > 0.001f)
                {
                    mixed = true;
                    break;
                }
            }
            string seed = mixed ? "—" : v0.ToString("0.##");
            string text = TextInputField(ctrl, seed, GUILayout.Width(110f));
            // Strip the mixed marker before parsing: clicking in and typing leaves "—60", which
            // never parsed — the primary multi-select flow (select many, type one value, all
            // apply) was dead. / 解析前剥掉混合标记：点击后直接输入会留下"—60"，此前永不解析
            // ——多选的主流程（选一堆、输一个值、全体生效）等于失效。
            if (float.TryParse(text.Replace("—", "").Trim(), out float parsed) && IsFiniteFloat(parsed) && (mixed || Math.Abs(parsed - v0) > 0.001f))
                apply(parsed);
            GUILayout.EndHorizontal();
            DrawEditorHelpBox(helpKey);
        }

        private void DrawEditorColorField(string label, float[] arr, Color fallback, Action<float[]> apply)
        {
            Color cur = NodeColor(arr, fallback);
            Color next = DrawColorPicker(label, cur, fallback);
            if (next != cur)
            {
                apply(new[] { next.r, next.g, next.b, next.a });
                EditorPropertyChanged();
            }
        }

        private void EditorPropertyChanged()
        {
            // Property edits are undoable too: record the pre-change state once per change burst
            // (the 0.4s nudge window coalesces slider drags). Without this, Ctrl+Z after a color
            // tweak didn't revert it — it undid the last STRUCTURAL op instead, destroying
            // unrelated work. / 属性修改同样可撤销：每个修改突发记录一次变更前状态（0.4 秒
            // 微调窗口合并滑杆拖动）。此前改完颜色按 Ctrl+Z 不会回退——反而会误撤销上一个
            // 结构性操作，破坏无关改动。
            try
            {
                editorHistory.PushNudge(SnapshotCustomNodes(), Time.unscaledTime);
            }
            catch (Exception) { /* snapshot failure must not block the edit / 快照失败不阻塞编辑 */ }
            SaveSettingsFromGui();
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
                    string abs = Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(Loader.ModPath, p));
                    if (!File.Exists(abs)) continue;
                    string dir = Path.Combine(Loader.ModPath, "CustomImages");
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
            // the editor keeps drawing the PRE-import image until restart. /
            // 导入会覆盖 CustomImages\ 下的文件——清掉画布贴图缓存，否则编辑器到重启前
            // 一直画的是导入前的旧图。
            fmTexCache.Clear();
            EditorPropertyChanged();
        }

        private void OpenCustomImagesDir()
        {
            try
            {
                string dir = Path.Combine(Loader.ModPath, "CustomImages");
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
