// Settings data model and serialization helpers / 设置数据模型和序列化辅助类
// All user-configurable options are stored here and persisted as JSON / 所有用户可配置选项都存储在这里并序列化为 JSON

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using TMPro;
using UnityEngine;

namespace JipperKeyViewer.KeyViewer.Settings
{
    [System.Serializable]
    [JsonObject(MemberSerialization.Fields)]
    public class ProfileData
    {
        public KeyviewerStyle KeyViewerStyle = KeyviewerStyle.Key16;
        public FootKeyviewerStyle FootKeyViewerStyle = FootKeyviewerStyle.Key4;

        public KeyCode[] key8 = {
            KeyCode.Tab, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.E, KeyCode.P, KeyCode.Equals, KeyCode.Backspace, KeyCode.Backslash
        };
        public string[] key8Text = new string[8];
        public KeyCode[] key10 = {
            KeyCode.Tab, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.E, KeyCode.P, KeyCode.Equals, KeyCode.Backspace, KeyCode.Backslash,
            KeyCode.Space, KeyCode.Comma
        };
        public string[] key10Text = new string[10];
        public KeyCode[] key12 = {
            KeyCode.Tab, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.E, KeyCode.P, KeyCode.Equals, KeyCode.Backspace, KeyCode.Backslash,
            KeyCode.Space, KeyCode.C, KeyCode.Comma, KeyCode.Period
        };
        public string[] key12Text = new string[12];
        public KeyCode[] key14 = {
            KeyCode.Tab, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.E, KeyCode.P, KeyCode.Equals, KeyCode.Backspace, KeyCode.Backslash,
            KeyCode.Space, KeyCode.C, KeyCode.Comma, KeyCode.Period, KeyCode.CapsLock, KeyCode.LeftShift
        };
        public string[] key14Text = new string[14];
        public KeyCode[] key16 = {
            KeyCode.Tab, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.E, KeyCode.P, KeyCode.Equals, KeyCode.Backspace, KeyCode.Backslash,
            KeyCode.Space, KeyCode.C, KeyCode.Comma, KeyCode.Period, KeyCode.CapsLock, KeyCode.LeftShift, KeyCode.Return, KeyCode.H
        };
        public string[] key16Text = new string[16];
        public KeyCode[] key20 = {
            KeyCode.Tab, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.E, KeyCode.P, KeyCode.Equals, KeyCode.Backspace, KeyCode.Backslash,
            KeyCode.Space, KeyCode.C, KeyCode.Comma, KeyCode.Period, KeyCode.CapsLock, KeyCode.LeftShift, KeyCode.Return, KeyCode.H,
            KeyCode.LeftControl, KeyCode.D, KeyCode.RightShift, KeyCode.Semicolon
        };
        public string[] key20Text = new string[20];
        public KeyCode[] key24 = {
            KeyCode.Tab, KeyCode.Alpha1, KeyCode.Alpha2, KeyCode.E, KeyCode.P, KeyCode.Equals, KeyCode.Backspace, KeyCode.Backslash,
            KeyCode.Space, KeyCode.C, KeyCode.Comma, KeyCode.Period, KeyCode.CapsLock, KeyCode.LeftShift, KeyCode.Return, KeyCode.H,
            KeyCode.LeftControl, KeyCode.D, KeyCode.RightShift, KeyCode.Q, KeyCode.Z, KeyCode.X, KeyCode.V, KeyCode.B
        };
        public string[] key24Text = new string[24];

        public KeyCode[] key108 = KeyViewer.BuildDefaultKey108();

        public KeyCode[] footkey2 = { KeyCode.F8, KeyCode.F3 };
        public KeyCode[] footkey4 = { KeyCode.F8, KeyCode.F3, KeyCode.F7, KeyCode.F2 };
        public KeyCode[] footkey6 = { KeyCode.F8, KeyCode.F3, KeyCode.F7, KeyCode.F2, KeyCode.F6, KeyCode.F1 };
        public KeyCode[] footkey8 = { KeyCode.F8, KeyCode.F4, KeyCode.F7, KeyCode.F3, KeyCode.F6, KeyCode.F2, KeyCode.F5, KeyCode.F1 };
        public KeyCode[] footkey10 = { KeyCode.F8, KeyCode.F4, KeyCode.F7, KeyCode.F3, KeyCode.F6, KeyCode.F2, KeyCode.F5, KeyCode.F1, KeyCode.F9, KeyCode.F10 };
        public KeyCode[] footkey12 = { KeyCode.F8, KeyCode.F4, KeyCode.F7, KeyCode.F3, KeyCode.F6, KeyCode.F2, KeyCode.F5, KeyCode.F1, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12 };
        public KeyCode[] footkey14 = { KeyCode.F8, KeyCode.F4, KeyCode.F7, KeyCode.F3, KeyCode.F6, KeyCode.F2, KeyCode.F5, KeyCode.F1, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12, KeyCode.F13, KeyCode.F14 };
        public KeyCode[] footkey16 = { KeyCode.F8, KeyCode.F4, KeyCode.F7, KeyCode.F3, KeyCode.F6, KeyCode.F2, KeyCode.F5, KeyCode.F1, KeyCode.F9, KeyCode.F10, KeyCode.F11, KeyCode.F12, KeyCode.F13, KeyCode.F14, KeyCode.F15, KeyCode.F16 };

        public string[] footkey2Text = new string[2];
        public string[] footkey4Text = new string[4];
        public string[] footkey6Text = new string[6];
        public string[] footkey8Text = new string[8];
        public string[] footkey10Text = new string[10];
        public string[] footkey12Text = new string[12];
        public string[] footkey14Text = new string[14];
        public string[] footkey16Text = new string[16];

        public KeyCode[] GhostKey8 = new KeyCode[8];
        public KeyCode[] GhostKey10 = new KeyCode[10];
        public KeyCode[] GhostKey12 = new KeyCode[12];
        public KeyCode[] GhostKey14 = new KeyCode[14];
        public KeyCode[] GhostKey16 = new KeyCode[16];
        public KeyCode[] GhostKey20 = new KeyCode[20];
        public KeyCode[] GhostKey24 = new KeyCode[24];

        public int[] Count = new int[KeyViewer.MaxKeySlots];
        public int TotalCount;
        /// <summary>Per-profile schema marker used to make migrations idempotent across a crash.
        /// Older files default to 0; normal saves advance it to the current meta version. /
        /// 单 Profile 架构标记，用于让迁移在崩溃后仍可幂等恢复。旧文件默认为 0，正常保存时
        /// 更新到当前 meta 版本。</summary>
        public int DataVersion;

        // Custom label text for KPS and Total displays / KPS 和 Total 显示的自定义标签文本
        // Default to the standard English labels. The GUI detects "user kept the default"
        // by string comparison with "KPS" / "Total" — no bool flag needed because Unity
        // JsonUtility serializes both null and "" identically, and treating the default
        // value as "use default" works as long as we initialize the field to that value.
        // / 默认填标准英文标签。GUI 通过与 "KPS"/"Total" 字符串对比判断"是否使用默认"——
        //   不要 bool 标志:JsonUtility 对 null 和 "" 序列化结果相同,直接用默认值即可。
        public string KpsLabel = "KPS";
        public string TotalLabel = "Total";

        public bool DownLocation;
        public float Size = 1f;
        public bool Enabled = true;

        public Color Background = KeyViewer.Background;
        public Color BackgroundClicked = KeyViewer.BackgroundClicked;
        public Color Outline = KeyViewer.Outline;
        public Color OutlineClicked = KeyViewer.OutlineClicked;
        public Color Text = KeyViewer.Text;
        public Color TextClicked = KeyViewer.TextClicked;
        public Color RainColor = KeyViewer.RainColor;
        public Color RainColor2 = KeyViewer.RainColor2;
        public Color RainColor3 = KeyViewer.RainColor3;

        public Color KpsBackground = KeyViewer.Background;
        public Color KpsOutline = KeyViewer.Outline;
        public Color KpsText = KeyViewer.Text;

        public Color TotalBackground = KeyViewer.Background;
        public Color TotalOutline = KeyViewer.Outline;
        public Color TotalText = KeyViewer.Text;

        public bool EnableRainEffect = true;
        public bool EnableRainFade = true;
        public bool EnableGhostRain = false;
        public float RainFadeDuration = 0.5f;
        public bool EnableRainGradient = false;
        public float RainFadePx = 40f;
        public bool EnableRainForRow1 = true;
        public bool EnableRainForRow2 = true;
        public bool EnableRainForRow3 = true;
        public float RainSpeedRow1 = 100f;
        public float RainSpeedRow2 = 100f;
        public float RainSpeedRow3 = 100f;
        public float RainHeightRow1 = 275f;
        public float RainHeightRow2 = 275f;
        public float RainHeightRow3 = 275f;
        public float RainWidthRow1 = 50f;
        public float RainWidthRow2 = 40f;
        public float RainWidthRow3 = 30f;
        public float RainStartYRow1 = -223f;
        public float RainStartYRow2 = -169f;
        public float RainStartYRow3 = -115f;
        public float GhostRainStartYRow1 = -223f;
        public float GhostRainStartYRow2 = -169f;
        public float GhostRainStartYRow3 = -115f;
        public float GhostRainSpeedRow1 = 100f;
        public float GhostRainSpeedRow2 = 100f;
        public float GhostRainSpeedRow3 = 100f;
        public float GhostRainHeightRow1 = 275f;
        public float GhostRainHeightRow2 = 275f;
        public float GhostRainHeightRow3 = 275f;
        public float GhostRainWidthRow1 = 50f;
        public float GhostRainWidthRow2 = 40f;
        public float GhostRainWidthRow3 = 30f;

        public Vector2 MainKeyViewerPosition = new Vector2(0, 1);
        public Vector2 FootKeyViewerPosition = new Vector2(0.24f, 1f);
        public bool CustomPositionEnabled = false;

        public int FontIndex = 1;
        public string FontName = "";
        public int FontStyleFlags = 0;

        public bool EnableCountFormatting = false;
        public bool HideMainKeyCount = false;
        public bool EnablePerKeyKps = false;
        public bool StreamerMode = false;
        public bool StandardKeyWidth = false;
        public bool HideKpsTotalLabel = false;

        public float KeyFontSize = 20f;
        public bool EnablePressAnimation = false;
        public float PressAnimationScale = 0.95f;
        public bool EnablePressAnimationOnRain = false;
        // Press-animation easing, by name (see KvEasing). The old build hard-coded a plain linear
        // Lerp, so "linear" is the default and reproduces the previous feel exactly. /
        // 按压动画缓动，按名字取用（见 KvEasing）。旧版写死普通线性 Lerp，故默认 "linear" 精确
        // 复刻此前的观感。
        public string PressAnimationEasing = "linear";
        // Press-animation duration in milliseconds. The old hard-coded 80ms is the default. /
        // 按压动画时长（毫秒）。旧版写死的 80ms 即默认值。
        public float PressAnimationDurationMs = 80f;

        // ===== Text outline / shadow (key label and count text resolved separately) =====
        // Outline uses TMP's SDF outline (OUTLINE_ON + _OutlineWidth/_OutlineColor); shadow uses
        // the underlay pass (UNDERLAY_ON + _UnderlayColor/_UnderlayOffsetX/Y/_UnderlaySoftness).
        // Both live on the font MATERIAL, so a style change builds a NEW cached material instead
        // of mutating the shared font-asset material — mutating that would leak the style into
        // every TMP text in the game.
        // / 文字描边/阴影（按键标签与计数文本分别解析）。描边用 TMP 的 SDF 描边
        //（OUTLINE_ON + _OutlineWidth/_OutlineColor）；阴影用 underlay 通道（UNDERLAY_ON +
        // _UnderlayColor/_UnderlayOffsetX/Y/_UnderlaySoftness）。两者都挂在字体材质上，故样式
        // 变化会新建缓存材质，而不是改写共享的字体资源材质——改写会让样式泄漏到游戏内所有
        // TMP 文本。
        public bool EnableKeyTextOutline = false;
        public Color KeyTextOutlineColor = new Color(0f, 0f, 0f, 1f);
        public float KeyTextOutlineThickness = 0.2f;
        public bool EnableCountTextOutline = false;
        public Color CountTextOutlineColor = new Color(0f, 0f, 0f, 1f);
        public float CountTextOutlineThickness = 0.2f;
        // Shadow defaults reproduce the pre-1.7.2 hard-coded underlay exactly (black at 0.5 alpha,
        // offset 1,-1, no softness), so existing profiles keep the text look they already had.
        // / 阴影默认值精确复刻 1.7.2 之前写死的 underlay（黑 0.5、偏移 1,-1、无柔化），现有配置
        // 的文字观感保持不变。
        public bool EnableKeyTextShadow = true;
        public Color KeyTextShadowColor = new Color(0f, 0f, 0f, 0.5f);
        public float KeyTextShadowOffsetX = 1f;
        public float KeyTextShadowOffsetY = -1f;
        public float KeyTextShadowSoftness = 0f;
        public bool EnableCountTextShadow = true;
        public Color CountTextShadowColor = new Color(0f, 0f, 0f, 0.5f);
        public float CountTextShadowOffsetX = 1f;
        public float CountTextShadowOffsetY = -1f;
        public float CountTextShadowSoftness = 0f;

        public Color GhostRainColor = KeyViewer.GhostRainColorDefault;
        public Color GhostRainColor2 = KeyViewer.GhostRainColor2Default;
        public Color GhostRainColor3 = KeyViewer.GhostRainColor3Default;

        public bool EnableRainShadowRow1;
        public bool EnableRainShadowRow2;
        public bool EnableRainShadowRow3;
        public Color RainShadowColorRow1 = KeyViewer.RainShadowColorDefault;
        public Color RainShadowColorRow2 = KeyViewer.RainShadowColorDefault;
        public Color RainShadowColorRow3 = KeyViewer.RainShadowColorDefault;
        public float RainShadowOffsetXRow1 = 3f;
        public float RainShadowOffsetYRow1 = -3f;
        public float RainShadowOffsetXRow2 = 3f;
        public float RainShadowOffsetYRow2 = -3f;
        public float RainShadowOffsetXRow3 = 3f;
        public float RainShadowOffsetYRow3 = -3f;

        public bool EnableRainOutlineRow1;
        public bool EnableRainOutlineRow2;
        public bool EnableRainOutlineRow3;
        public Color RainOutlineColorRow1 = KeyViewer.RainOutlineColorDefault;
        public Color RainOutlineColorRow2 = KeyViewer.RainOutlineColorDefault;
        public Color RainOutlineColorRow3 = KeyViewer.RainOutlineColorDefault;
        public float RainOutlineWidthRow1 = 2f;
        public float RainOutlineWidthRow2 = 2f;
        public float RainOutlineWidthRow3 = 2f;

        public bool EnableGhostRainShadowRow1;
        public bool EnableGhostRainShadowRow2;
        public bool EnableGhostRainShadowRow3;
        public Color GhostRainShadowColorRow1 = KeyViewer.RainShadowColorDefault;
        public Color GhostRainShadowColorRow2 = KeyViewer.RainShadowColorDefault;
        public Color GhostRainShadowColorRow3 = KeyViewer.RainShadowColorDefault;
        public float GhostRainShadowOffsetXRow1 = 3f;
        public float GhostRainShadowOffsetYRow1 = -3f;
        public float GhostRainShadowOffsetXRow2 = 3f;
        public float GhostRainShadowOffsetYRow2 = -3f;
        public float GhostRainShadowOffsetXRow3 = 3f;
        public float GhostRainShadowOffsetYRow3 = -3f;

        public bool EnableGhostRainOutlineRow1;
        public bool EnableGhostRainOutlineRow2;
        public bool EnableGhostRainOutlineRow3;
        public Color GhostRainOutlineColorRow1 = KeyViewer.RainOutlineColorDefault;
        public Color GhostRainOutlineColorRow2 = KeyViewer.RainOutlineColorDefault;
        public Color GhostRainOutlineColorRow3 = KeyViewer.RainOutlineColorDefault;
        public float GhostRainOutlineWidthRow1 = 2f;
        public float GhostRainOutlineWidthRow2 = 2f;
        public float GhostRainOutlineWidthRow3 = 2f;

        public bool EnablePerKeyColors = false;

        // ===== Full 108-key keyboard unified color control / 全键盘统一配色控制 =====
        // Only background / outline / text (+ pressed variants); rain / ghost / KPS colors untouched / 仅背景/描边/文字，雨滴/鬼键/KPS 色不动
        public bool EnableFullKeyboardUnifiedColor = true;
        public Color FullKeyboardBackground = KeyViewer.Background;
        public Color FullKeyboardBackgroundClicked = KeyViewer.BackgroundClicked;
        public Color FullKeyboardOutline = KeyViewer.Outline;
        public Color FullKeyboardOutlineClicked = KeyViewer.OutlineClicked;
        public Color FullKeyboardText = KeyViewer.Text;
        public Color FullKeyboardTextClicked = KeyViewer.TextClicked;
        // Optional KPS / Total boxes in full-keyboard mode / 全键盘模式下可选的 KPS/Total 框
        public bool FullKeyboardShowKpsTotal = false;
        // Custom KPS / Total position (normalized 0-1, applied in full-keyboard mode). Y uses the
        // mod-wide convention (0 = top edge, 1 = bottom edge); the 0.88 default renders near the
        // bottom — the same on-screen spot the historical 0.12 default occupied under the old
        // inverted convention. / KPS/Total 自定义位置（归一化 0-1，仅全键盘生效）。Y 采用全
        // Mod 约定（0=贴顶，1=贴底）；0.88 默认值渲染在近底部——与旧反向约定下 0.12 默认值
        // 的屏幕位置相同。
        public Vector2 FullKpsPosition = new Vector2(0.62f, 0.88f);
        public Vector2 FullTotalPosition = new Vector2(0.71f, 0.88f);
        // KPS / Total box size in full-keyboard mode (width & height in px) / 全键盘下 KPS/Total 框的尺寸（宽高，像素）
        public float FullKeyboardKpsTotalSize = 150f;
        // Center the KPS / Total text+value (stacked, centered) instead of left-label / right-number / 居中显示 KPS/Total（上下堆叠居中）而非左文本右数值（仅 slim 布局生效，含全键盘）
        public bool KpsTotalCentered = false;
        // Stack KPS/Total label and value vertically when centered is enabled / 居中模式下将标签和数值上下堆叠
        public bool KpsTotalStackedWhenCentered = false;
        public Color[] PerKeyBackground;
        public Color[] PerKeyBackgroundClicked;
        public Color[] PerKeyOutline;
        public Color[] PerKeyOutlineClicked;
        public Color[] PerKeyText;
        public Color[] PerKeyTextClicked;
        public Color[] PerKeyRainColor;
        public Color[] PerKeyGhostRainColor;

        // ===== Per-key text size / 每键字号 =====
        // PerKeyFontSize: 0 = use global KeyFontSize, >0 = override for this key.
        // / PerKeyFontSize: 0 = 使用全局 KeyFontSize, >0 = 该键独立字号。
        public bool EnablePerKeyTextSize = false;
        public float[] PerKeyFontSize;         // size MaxKeySlots+2, 0 = global default

        // ===== FreeMake custom layout / FreeMake 自定义布局 =====
        // Persisted as REAL nested arrays through Newtonsoft's Fields-mode resolver (the game's
        // JsonUtility drops class arrays/lists — the escaped-string carrier was a workaround for
        // that). The code-facing API is the CustomNodes/LayerGroups list properties; call
        // SyncListsToArrays() before serializing a ProfileData and SyncArraysFromLists() after
        // populating one. / 通过 Newtonsoft 字段模式解析器持久化为真正的嵌套数组（游戏自带的
        // JsonUtility 会丢弃类数组/类列表——转义字符串载体只是权宜）。代码侧 API 是
        // CustomNodes/LayerGroups 列表属性；序列化 ProfileData 前调用 SyncListsToArrays()，
        // 填充后调用 SyncArraysFromLists()。
        [JsonProperty("CustomNodes")] public FmNode[] CustomNodesData = new FmNode[0];
        [JsonProperty("LayerGroups")] public FmLayerGroup[] LayerGroupsData = new FmLayerGroup[0];
        public int CustomNodeNextId = 1;
        public int LayerGroupNextId = 1;

        [System.NonSerialized] private List<FmNode> _customNodes;
        [System.NonSerialized] private List<FmLayerGroup> _layerGroups;

        /// <summary>Typed node list (built from the persisted array on first access). /
        /// 类型化节点列表（首次访问时从持久化数组构建）。</summary>
        [JsonIgnore] public List<FmNode> CustomNodes
        {
            get
            {
                if (_customNodes == null)
                    _customNodes = new List<FmNode>(CustomNodesData ?? new FmNode[0]);
                return _customNodes;
            }
            set
            {
                _customNodes = value ?? new List<FmNode>();
                CustomNodesData = _customNodes.ToArray();
            }
        }

        /// <summary>Typed layer-group list / 类型化图层组列表。</summary>
        [JsonIgnore] public List<FmLayerGroup> LayerGroups
        {
            get
            {
                if (_layerGroups == null)
                    _layerGroups = new List<FmLayerGroup>(LayerGroupsData ?? new FmLayerGroup[0]);
                return _layerGroups;
            }
            set
            {
                _layerGroups = value ?? new List<FmLayerGroup>();
                LayerGroupsData = _layerGroups.ToArray();
            }
        }

        /// <summary>Flush the working lists into the persisted array fields. MUST run before
        /// every profile serialization. / 把工作列表刷入持久化数组字段。每次 Profile 序列化前
        /// 必须调用。</summary>
        public void SyncListsToArrays()
        {
            CustomNodesData = (CustomNodes ?? new List<FmNode>()).ToArray();
            LayerGroupsData = (LayerGroups ?? new List<FmLayerGroup>()).ToArray();
        }

        /// <summary>Rebuild the working lists from the persisted array fields (after load). /
        /// 从持久化数组字段重建工作列表（加载后调用）。</summary>
        public void SyncArraysFromLists()
        {
            ImportLegacyCarriers();
            _customNodes = new List<FmNode>(CustomNodesData ?? new FmNode[0]);
            _layerGroups = new List<FmLayerGroup>(LayerGroupsData ?? new FmLayerGroup[0]);
        }

        // Interim-build string carriers (that build persisted the lists as escaped JSON strings
        // before the array refactor). Newtonsoft-only private fields: read when loading an old
        // profile, imported once into the arrays, then nulled so they are never written back.
        // 过渡构建的字符串载体（该构建在数组重构前把列表存成转义 JSON 字符串）。仅 Newtonsoft
        // 可见的私有字段：加载旧配置时读入，一次性导入数组，随后置空、写盘时省略。
        [JsonProperty("CustomNodesJson", NullValueHandling = NullValueHandling.Ignore)]
        private string LegacyCustomNodesJson;
        [JsonProperty("LayerGroupsJson", NullValueHandling = NullValueHandling.Ignore)]
        private string LegacyLayerGroupsJson;

        /// <summary>One-time import of the interim build's escaped-string carriers when the
        /// array fields are empty; clears the strings so they never serialize again. /
        /// 数组字段为空时一次性导入过渡构建的转义字符串载体；随后清空字符串，绝不再序列化。</summary>
        private void ImportLegacyCarriers()
        {
            bool customImported = true;
            bool groupsImported = true;
            if (!string.IsNullOrEmpty(LegacyCustomNodesJson)
                && (CustomNodesData == null || CustomNodesData.Length == 0))
            {
                try
                {
                    List<FmNode> parsed = JsonConvert.DeserializeObject<List<FmNode>>(
                        LegacyCustomNodesJson, ProfileSerializer);
                    if (parsed == null) throw new JsonSerializationException("legacy CustomNodesJson is null");
                    CustomNodesData = parsed.ToArray();
                }
                catch (Exception e)
                {
                    customImported = false;
                    Loader.Warning($"KeyViewer: legacy CustomNodesJson import failed: {e.Message}");
                }
            }
            if (!string.IsNullOrEmpty(LegacyLayerGroupsJson)
                && (LayerGroupsData == null || LayerGroupsData.Length == 0))
            {
                try
                {
                    List<FmLayerGroup> parsed = JsonConvert.DeserializeObject<List<FmLayerGroup>>(
                        LegacyLayerGroupsJson, ProfileSerializer);
                    if (parsed == null) throw new JsonSerializationException("legacy LayerGroupsJson is null");
                    LayerGroupsData = parsed.ToArray();
                }
                catch (Exception e)
                {
                    groupsImported = false;
                    Loader.Warning($"KeyViewer: legacy LayerGroupsJson import failed: {e.Message}");
                }
            }
            // Keep a malformed carrier in the file so a later load can retry it; clearing it
            // after only a warning would turn a recoverable parse error into silent data loss.
            // 解析失败时保留原载体，后续加载仍可重试；只警告后清空会把可恢复错误变成静默丢失。
            if (customImported) LegacyCustomNodesJson = null;
            if (groupsImported) LegacyLayerGroupsJson = null;
        }

        /// <summary>Shared Newtonsoft settings for ProfileData. Unity struct types (Color /
        /// Vector2/3/4) MUST go through <see cref="UnityStructConverter"/>: walking their
        /// contracts reads computed properties like Color.linear / Vector2.normalized, which are
        /// ECall icalls — calling them from Newtonsoft's compiled expression value providers
        /// crashed Mono natively (silent 0xc0000005 on every save, killing the game at the
        /// first scene-load save). The converter writes/reads plain fields only. Our own data
        /// classes carry [JsonObject(MemberSerialization.Fields)] so no member-mode plumbing is
        /// needed here. / ProfileData 专用 Newtonsoft 设置。Unity 结构体（Color/Vector2/3/4）
        /// 必须走 <see cref="UnityStructConverter"/>：遍历其契约会读取 Color.linear /
        /// Vector2.normalized 这类计算属性——它们是 ECall icall，从 Newtonsoft 编译的表达式
        /// 取值器里调用会让 Mono 原生崩溃（每次保存静默 0xc0000005，游戏死在首次场景加载
        /// 保存上）。转换器只读写纯字段。我们自己的数据类带
        /// [JsonObject(MemberSerialization.Fields)]，此处无需任何成员模式机关。</summary>
        internal static readonly JsonSerializerSettings ProfileSerializer = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Converters = { new UnityStructConverter() },
        };

        /// <summary>Explicit field-only JSON for Unity struct types. /
        /// Unity 结构体类型的显式纯字段 JSON。</summary>
        internal sealed class UnityStructConverter : JsonConverter
        {
            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Color) || objectType == typeof(Vector2)
                    || objectType == typeof(Vector3) || objectType == typeof(Vector4);
            }

            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                writer.WriteStartObject();
                switch (value)
                {
                    case Color c:
                        writer.WritePropertyName("r"); writer.WriteValue(c.r);
                        writer.WritePropertyName("g"); writer.WriteValue(c.g);
                        writer.WritePropertyName("b"); writer.WriteValue(c.b);
                        writer.WritePropertyName("a"); writer.WriteValue(c.a);
                        break;
                    case Vector2 v2:
                        writer.WritePropertyName("x"); writer.WriteValue(v2.x);
                        writer.WritePropertyName("y"); writer.WriteValue(v2.y);
                        break;
                    case Vector3 v3:
                        writer.WritePropertyName("x"); writer.WriteValue(v3.x);
                        writer.WritePropertyName("y"); writer.WriteValue(v3.y);
                        writer.WritePropertyName("z"); writer.WriteValue(v3.z);
                        break;
                    case Vector4 v4:
                        writer.WritePropertyName("x"); writer.WriteValue(v4.x);
                        writer.WritePropertyName("y"); writer.WriteValue(v4.y);
                        writer.WritePropertyName("z"); writer.WriteValue(v4.z);
                        writer.WritePropertyName("w"); writer.WriteValue(v4.w);
                        break;
                }
                writer.WriteEndObject();
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.Null) return existingValue;
                float r = 0f, g = 0f, b = 0f, a = 0f, x = 0f, y = 0f, z = 0f, w = 0f;
                if (reader.TokenType == JsonToken.StartObject)
                {
                    reader.Read();
                    while (reader.TokenType == JsonToken.PropertyName)
                    {
                        string name = (string)reader.Value;
                        reader.Read();
                        float f = Convert.ToSingle(reader.Value, System.Globalization.CultureInfo.InvariantCulture);
                        switch (name)
                        {
                            case "r": r = f; break;
                            case "g": g = f; break;
                            case "b": b = f; break;
                            case "a": a = f; break;
                            case "x": x = f; break;
                            case "y": y = f; break;
                            case "z": z = f; break;
                            case "w": w = f; break;
                        }
                        reader.Read();
                    }
                }
                if (objectType == typeof(Color)) return new Color(r, g, b, a);
                if (objectType == typeof(Vector2)) return new Vector2(x, y);
                if (objectType == typeof(Vector3)) return new Vector3(x, y, z);
                return new Vector4(x, y, z, w);
            }
        }

        public ProfileData()
        {
            key8Text = key8Text ?? new string[8];
            key10Text = key10Text ?? new string[10];
            key12Text = key12Text ?? new string[12];
            key14Text = key14Text ?? new string[14];
            key16Text = key16Text ?? new string[16];
            key20Text = key20Text ?? new string[20];
            key24Text = key24Text ?? new string[24];
            key108 = key108 ?? KeyViewer.BuildDefaultKey108();
            footkey2Text = footkey2Text ?? new string[2];
            footkey4Text = footkey4Text ?? new string[4];
            footkey6Text = footkey6Text ?? new string[6];
            footkey8Text = footkey8Text ?? new string[8];
            footkey10Text = footkey10Text ?? new string[10];
            footkey12Text = footkey12Text ?? new string[12];
            footkey14Text = footkey14Text ?? new string[14];
            footkey16Text = footkey16Text ?? new string[16];
            GhostKey8 = GhostKey8 ?? new KeyCode[8];
            GhostKey10 = GhostKey10 ?? new KeyCode[10];
            GhostKey12 = GhostKey12 ?? new KeyCode[12];
            GhostKey14 = GhostKey14 ?? new KeyCode[14];
            GhostKey16 = GhostKey16 ?? new KeyCode[16];
            GhostKey20 = GhostKey20 ?? new KeyCode[20];
            GhostKey24 = GhostKey24 ?? new KeyCode[24];
            Count = Count ?? new int[KeyViewer.MaxKeySlots];
            int n = KeyViewer.MaxKeySlots + 2;
            PerKeyBackground = SafeEnsure(PerKeyBackground, n, KeyViewer.Background);
            PerKeyBackgroundClicked = SafeEnsure(PerKeyBackgroundClicked, n, KeyViewer.BackgroundClicked);
            PerKeyOutline = SafeEnsure(PerKeyOutline, n, KeyViewer.Outline);
            PerKeyOutlineClicked = SafeEnsure(PerKeyOutlineClicked, n, KeyViewer.OutlineClicked);
            PerKeyText = SafeEnsure(PerKeyText, n, KeyViewer.Text);
            PerKeyTextClicked = SafeEnsure(PerKeyTextClicked, n, KeyViewer.TextClicked);
            PerKeyRainColor = SafeEnsure(PerKeyRainColor, n, KeyViewer.RainColor);
            PerKeyGhostRainColor = SafeEnsure(PerKeyGhostRainColor, n, KeyViewer.GhostRainColorDefault);
            PerKeyFontSize = SafeEnsureFloat(PerKeyFontSize, n, 0f);
        }

        private static Color[] SafeEnsure(Color[] arr, int len, Color fill)
        {
            if (arr != null && arr.Length == len) return arr;
            Color[] r = new Color[len];
            for (int i = 0; i < len; i++)
                r[i] = (arr != null && i < arr.Length) ? arr[i] : fill;
            return r;
        }

        private static float[] SafeEnsureFloat(float[] arr, int len, float fill)
        {
            if (arr != null && arr.Length == len) return arr;
            float[] r = new float[len];
            for (int i = 0; i < len; i++)
                r[i] = (arr != null && i < arr.Length) ? arr[i] : fill;
            return r;
        }

        public void InitPerKeyColors()
        {
            int n = KeyViewer.MaxKeySlots + 2;
            int footBase = KeyViewer.FootKeyBase;

            var oldBg = PerKeyBackground;
            var oldBgClicked = PerKeyBackgroundClicked;
            var oldOutline = PerKeyOutline;
            var oldOutlineClicked = PerKeyOutlineClicked;
            var oldText = PerKeyText;
            var oldTextClicked = PerKeyTextClicked;
            var oldRain = PerKeyRainColor;
            var oldGhostRain = PerKeyGhostRainColor;

            PerKeyBackground = new Color[n];
            PerKeyBackgroundClicked = new Color[n];
            PerKeyOutline = new Color[n];
            PerKeyOutlineClicked = new Color[n];
            PerKeyText = new Color[n];
            PerKeyTextClicked = new Color[n];
            PerKeyRainColor = new Color[n];
            PerKeyGhostRainColor = new Color[n];

            for (int i = 0; i < n; i++)
            {
                PerKeyBackground[i] = oldBg != null && i < oldBg.Length ? oldBg[i] : KeyViewer.Background;
                PerKeyBackgroundClicked[i] = oldBgClicked != null && i < oldBgClicked.Length
                    ? oldBgClicked[i] : KeyViewer.BackgroundClicked;
                PerKeyOutline[i] = oldOutline != null && i < oldOutline.Length ? oldOutline[i] : KeyViewer.Outline;
                PerKeyOutlineClicked[i] = oldOutlineClicked != null && i < oldOutlineClicked.Length
                    ? oldOutlineClicked[i] : KeyViewer.OutlineClicked;
                PerKeyText[i] = oldText != null && i < oldText.Length ? oldText[i] : KeyViewer.Text;
                PerKeyTextClicked[i] = oldTextClicked != null && i < oldTextClicked.Length
                    ? oldTextClicked[i] : KeyViewer.TextClicked;
            }

            for (int i = 0; i < n; i++)
            {
                if (oldRain != null && i < oldRain.Length)
                {
                    PerKeyRainColor[i] = oldRain[i];
                }
                else
                {
                    if (i < 8) PerKeyRainColor[i] = KeyViewer.RainColor;
                    else if (i < 16) PerKeyRainColor[i] = KeyViewer.RainColor2;
                    else if (i < footBase) PerKeyRainColor[i] = KeyViewer.RainColor3;
                    else if (i < KeyViewer.MaxKeySlots) PerKeyRainColor[i] = KeyViewer.RainColor;
                    else PerKeyRainColor[i] = KeyViewer.RainColor;
                }
                PerKeyGhostRainColor[i] = oldGhostRain != null && i < oldGhostRain.Length
                    ? oldGhostRain[i] : KeyViewer.GhostRainColorDefault;
            }
        }
    }

    /// <summary>
    /// One node of the FreeMake custom layout / FreeMake 自定义布局的单个节点
    /// NodeType: 0 = key, 1 = KPS panel, 2 = Total panel, 3 = image.
    /// Coordinates use the reference canvas (top-left origin, Y grows downward); the runtime
    /// converts to the overlay's bottom-left space at build time.
    /// / 坐标采用参考画布（左上原点，Y 向下），运行时在构建时换算为覆盖层的左下坐标系。
    /// </summary>
    [System.Serializable]
    [JsonObject(MemberSerialization.Fields)]
    public class FmNode
    {
        public int NodeType;
        public int Id;
        public string KeyBind = "";
        public string GhostKey = "";
        public string CustomText = "";
        public string PressedText = "";
        public float X;
        public float Y;
        public float Width = 60f;
        public float Height = 60f;
        public int Depth;
        public int Count;
        public bool CountInTotal = true;
        public bool PerKeyKps;
        public bool RainEnabled;
        public bool Unselectable;
        public float Opacity = 1f;
        public string ImagePath = "";
        // Video source for an image node. A non-empty VideoPath that points at a playable file makes
        // the node render a looping VideoPlayer into a RenderTexture INSTEAD of the PNG — the node
        // stays NodeType 3, so binding/counting/press/opacity/rain/layering all keep working. A
        // missing or unsupported file silently falls back to ImagePath. /
        // 图片节点的视频来源。VideoPath 非空且指向可播放文件时，节点改为把 VideoPlayer 循环渲染
        // 到 RenderTexture，而非使用 PNG——节点仍为 NodeType 3，故绑定/计数/按压/不透明度/雨滴/
        // 层级全部照常工作。文件缺失或格式不支持时静默回落到 ImagePath。
        public string VideoPath = "";
        public bool VideoLoop = true;
        public bool UseCustomColor;
        public float[] Bg;
        public float[] BgPressed;
        public float[] Outline;
        public float[] OutlinePressed;
        // Per-node text colors (null = follow the global Text/TextClicked). /
        // 节点级文本颜色（null = 跟随全局 Text/TextClicked）。
        public float[] TextColor;
        public float[] TextColorPressed;

        // ===== rain / 雨滴 =====
        // RainRow maps the node onto the three global rain parameter rows (speed / height /
        // width / start-Y / shadow / outline), so every existing rain slider applies. /
        // RainRow 把节点映射到全局三排雨滴参数（速度/高度/宽度/起始Y/阴影/描边），全部现有
        // 雨滴滑块因此对自定义节点生效。
        public int RainRow;
        // Per-node rain parameter overrides (0 = follow the mapped row's global settings). /
        // 节点级雨滴参数覆盖（0 = 跟随所在排的全局设置）。
        public float RainWidth;
        public float RainHeight;
        public float RainSpeed;
        // Per-node rain gradient (top/bottom colors). UseCustomRainColor
        // switches both; when off the node follows its rain row's global color. /
        // 节点级雨滴渐变（顶/底双色渐变）。UseCustomRainColor 同时切换两色；关闭时
        // 跟随所在雨滴排的全局颜色。
        public bool UseCustomRainColor;
        public float[] RainColorTop;
        public float[] RainColorBottom;
        // Per-node rain offsets (px): X shifts the rain column, Y shifts the track start. /
        // 节点级雨滴偏移（像素）：X 平移雨滴列，Y 平移轨道起点。
        public float RainOffsetX;
        public float RainOffsetY;
        // ===== counter bounce / 计数器弹跳 =====
        // counter bounce animation: bezier ease, scale peak, duration. The bezier is a
        // float[4] (NOT Vector4 — Vector4's computed 'normalized' property sends Newtonsoft
        // into a self-referencing loop that threw on EVERY save). /
        // 计数器弹跳动画：贝塞尔缓动、峰值缩放、时长。贝塞尔用 float[4]（不用
        // Vector4——其计算属性 normalized 会让 Newtonsoft 陷入自引用循环，导致每次保存都抛异常）。
        public bool CounterAnimEnabled = true;
        public float CounterAnimScale = 1.1f;
        // Per-node press scale (the Display tab's press animation, sunk to the node). Master gate
        // stays global (EnablePressAnimation); PressAnimEnabled opts the node out, and
        // UseCustomPressAnim+PressAnimScale override the global scale value when set.
        // / 节点级按压缩放（显示页的按压缩放下沉到节点）。总开关仍是全局 EnablePressAnimation；
        // PressAnimEnabled 可按节点退出，UseCustomPressAnim+PressAnimScale 覆盖全局缩放值。
        public bool PressAnimEnabled = true;
        public bool UseCustomPressAnim;
        public float PressAnimScale = 0.9f;
        // Per-node press easing + duration. Off (the default) follows the global Display-tab
        // values; on, this node keeps its own curve and timing. / 节点级按压缓动与时长。关闭
        //（默认）跟随显示页的全局值；开启后该节点使用自己的曲线与时长。
        public bool UseCustomPressEasing;
        public string PressAnimEasing = "linear";
        public float PressAnimDurationMs = 80f;
        // Per-node KPS/Total text layout: follows the global centered/stacked/hide-label toggles
        // unless UseCustomStatLayout. node.HideLabel doubles as the value-only switch.
        // / 节点级 KPS/Total 文本布局：未开启 UseCustomStatLayout 时跟随全局的居中/堆叠/
        // 隐藏标签开关。node.HideLabel 兼作「仅数值」开关。
        public bool UseCustomStatLayout;
        public bool StatCentered;
        public bool StatStacked;
        // Per-node rain shadow/outline overrides: off → the selected row's settings apply (ghost
        // rain keeps following the ghost row settings). / 节点级雨滴阴影/描边覆盖：关闭时跟随
        // 所选排的设置（鬼雨始终跟随鬼雨排设置）。
        public bool UseCustomRainShadow;
        public bool RainShadowEnabled = true;
        public float[] RainShadowColor;
        public float RainShadowOffsetX = 3f;
        public float RainShadowOffsetY = -3f;
        public bool UseCustomRainOutline;
        public bool RainOutlineEnabled;
        public float[] RainOutlineColor;
        public float RainOutlineWidth = 2f;
        // Per-node GHOST rain shadow/outline overrides (same model as the normal-rain pair above;
        // off → the ghost-row settings apply). / 节点级鬼雨阴影/描边覆盖（与上方普通雨同模型；
        // 关闭 → 跟随鬼雨排设置）。
        public bool UseCustomGhostRainShadow;
        public bool GhostRainShadowEnabled = true;
        public float[] GhostRainShadowColor;
        public float GhostRainShadowOffsetX = 3f;
        public float GhostRainShadowOffsetY = -3f;
        public bool UseCustomGhostRainOutline;
        public bool GhostRainOutlineEnabled;
        public float[] GhostRainOutlineColor;
        public float GhostRainOutlineWidth = 2f;
        // Per-node GHOST rain shape/offset params: off → ghost shares the node's normal rain
        // overrides (and the ghost row fallbacks); on → fully independent. /
        // 节点级鬼雨形状/偏移参数：关闭 → 鬼雨共用节点的普通雨覆盖（及鬼雨排回退）；开启 → 完全独立。
        public bool UseCustomGhostRainParams;
        public float GhostRainWidth;
        public float GhostRainHeight;
        public float GhostRainSpeed;
        public float GhostRainOffsetX;
        public float GhostRainOffsetY;
        // Per-node trail-top fade + release fade (the Rain tab's 顶部渐隐/松开淡出 sunk to the
        // node; off → the global toggles). / 节点级顶部渐隐与松开淡出（雨线页对应设置下沉到
        // 节点；关闭 → 跟随全局开关）。
        public bool UseCustomRainFade;
        public bool TrailFadeEnabled = true;
        public float TrailFadePx = 50f;
        public bool ReleaseFadeEnabled;
        public float ReleaseFadeDuration = 0.5f;
        public float CounterAnimDurationMs = 300f;
        public float[] CounterAnimBezier = new float[] { 0.25f, 0.46f, 0.45f, 0.94f };
        // Layer group id ("" = ungrouped); groups carry a name and a visibility toggle. /
        // 图层组 id（"" = 未分组）；组带名称与可见性开关。
        public string GroupId = "";
        // Image keys: swap to this image while pressed. /
        // 图片按键：按下时切换到该图片（按压语义）。
        public string ImagePathPressed = "";
        public bool HideLabel;
        // Per-node count hiding (independent of the global HideMainKeyCount). /
        // 逐节点隐藏计数（独立于全局「隐藏主按键计数」）。
        public bool HideCount;
        // Per-node count formatting: off = follow the global EnableCountFormatting; opting in
        // seeds the flag from the CURRENT global so the enabling action itself never changes
        // the rendered number. Applies to the node's own count AND, on stat nodes, the panel's
        // KPS/Total value. / 节点级计数格式：关 = 跟随全局 EnableCountFormatting；开启时从
        // 当前全局值播种，使开启动作本身不改变已显示的数字。作用于节点自身的计数，且在统计
        // 节点上作用于面板的 KPS/Total 数值。
        public bool UseCustomCountFormat;
        public bool CountThousandsSeparator;
        // Per-node box shape: corner radius in px (0 = the square 9-slice box) and border
        // thickness in px (0 = follow the sprite's own 9-slice border). A radius > 0 switches the
        // slot to the procedural rounded mesh, which is the only way to round a 9-sliced corner.
        // / 节点级盒子形状：圆角半径（像素，0 = 直角九宫格盒子）与边框厚度（像素，0 = 跟随
        // 贴图自带的九宫格边框）。半径 > 0 会把该槽位切到程序化圆角 mesh——这是让九宫格圆角
        // 唯一的办法。
        public float CornerRadius;
        public float BorderThickness;
        // 0 = use the global key font size / 0 = 使用全局按键字号
        public float FontSize;
        // Temporarily excluded from the runtime build; the editor dims it. /
        // 暂时从运行时构建中排除；编辑器里以低透明度显示。
        public bool Hidden;

        // ===== per-node text outline / shadow override / 节点级文字描边/阴影覆盖 =====
        // UseCustomTextStyle takes the node out of the global text-style settings for BOTH its
        // label and its count; the label and count each carry an independent outline + shadow
        // (the same split the Display tab exposes globally). / UseCustomTextStyle 让该节点的标签与
        // 计数文本同时脱离全局文字样式；标签与计数各自带独立的描边 + 阴影（与显示页的全局划分
        // 一致）。
        public bool UseCustomTextStyle;
        public bool KeyTextOutlineEnabled;
        public float[] KeyTextOutlineColor;
        public float KeyTextOutlineThickness = 0.2f;
        public bool KeyTextShadowEnabled = true;
        public float[] KeyTextShadowColor;
        public float KeyTextShadowOffsetX = 1f;
        public float KeyTextShadowOffsetY = -1f;
        public float KeyTextShadowSoftness;
        public bool CountTextOutlineEnabled;
        public float[] CountTextOutlineColor;
        public float CountTextOutlineThickness = 0.2f;
        public bool CountTextShadowEnabled = true;
        public float[] CountTextShadowColor;
        public float CountTextShadowOffsetX = 1f;
        public float CountTextShadowOffsetY = -1f;
        public float CountTextShadowSoftness;

        /// <summary>Runtime-only back-reference to the built Key (never serialized — JsonUtility
        /// would write UnityEngine.Object references as instance IDs). / 仅运行时的 Key 反向引用
        /// （绝不序列化——JsonUtility 会把 UnityEngine.Object 引用写成实例 ID）。</summary>
        [System.NonSerialized] internal Key RuntimeKey;

        public FmNode() { }

        /// <summary>Deep copy via a JSON round-trip (FmNode contains only primitives, strings
        /// and float arrays — no Unity computed-property traps). / 经 JSON 往返的深拷贝（FmNode
        /// 仅含基础类型/字符串/浮点数组，无 Unity 计算属性陷阱）。</summary>
        public FmNode Clone()
        {
            return JsonConvert.DeserializeObject<FmNode>(JsonConvert.SerializeObject(this));
        }
    }

    /// <summary>A named, visibility-toggled group of custom nodes (named visibility groups). /
    /// 自定义节点的命名可见性分组（命名可见性分组）。</summary>
    [System.Serializable]
    [JsonObject(MemberSerialization.Fields)]
    public class FmLayerGroup
    {
        public string Id = "";
        public string Name = "";
        public bool Visible = true;
    }

    /// <summary>
    /// Serializable settings data model for the mod / Mod 的可序列化设置数据模型
    /// Includes key bindings, layout configuration, colors, rain effect parameters, and positioning / 包含按键绑定、布局配置、颜色、雨滴效果参数和位置
    /// </summary>
    [System.Serializable]
    public class KeyViewerSettings
    {        // v6: full-keyboard KPS/Total position Y flipped to the mod-wide convention (0=top, 1=bottom).
        // / v6:全键盘 KPS/Total 位置 Y 翻转为全 Mod 约定(0=顶,1=底)。
        public int Version = 6;
        public string CurrentProfile = "Default";
        public string[] ProfileNames = new[] { "Default" };
        public string Language = "en";
        /// <summary>Last active settings GUI tab (persisted in meta JSON) / 上次停留的设置界面标签页(存入 meta JSON)</summary>
        public int UiTab;
        public ProfileData Data = new ProfileData();

        public KeyViewerSettings()
        {
            CurrentProfile = CurrentProfile ?? "Default";
            ProfileNames = ProfileNames ?? new[] { "Default" };
        }
    }

    /// <summary>
    /// Font entry associating a display name with a TMP_FontAsset / 字体条目，将显示名称关联到 TMP_FontAsset
    /// </summary>
    public class FontEntry
    {
        public string name;
        public TMP_FontAsset font;
        public string sourceFontName;
        public FontEntry(string name, TMP_FontAsset font) { this.name = name; this.font = font; sourceFontName = name; }
    }

    /// <summary>
    /// Utility helpers for IMGUI drawing / IMGUI 绘制工具方法
    /// </summary>
    public static class GUIUtils
    {
        /// <summary>
        /// Draw a solid-color rectangle using GUI.DrawTexture / 使用 GUI.DrawTexture 绘制纯色矩形
        /// </summary>
        public static void DrawRect(Rect position, Color color)
        {
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(position, Texture2D.whiteTexture);
            GUI.color = prev;
        }
    }
}
