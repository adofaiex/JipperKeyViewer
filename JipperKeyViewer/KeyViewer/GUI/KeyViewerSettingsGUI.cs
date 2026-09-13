// Settings GUI: General / Layout / Display tab content / 设置界面:常规 / 布局 / 显示 标签页内容
// Profile, language, count reset, font, folder buttons, custom position, layout, display, full-keyboard KPS/Total layout / 配置、语言、计数重置、字体、文件夹、自定义位置、布局、显示、全键盘 KPS/Total 布局
// Shared small draw helpers (FloatSliderField, foldout buttons) also live here / 通用小绘制工具(FloatSliderField、折叠按钮)也放在此文件

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer : MonoBehaviour
    {
        // ===== Profile UI state / 配置选择器 UI 状态 =====
        bool profileExpanded;
        /// <summary>Id of the currently expanded easing list (null = none). / 当前展开的缓动列表
        /// id（null = 无）。</summary>
        private string easingListExpanded;
        private Vector2 easingScroll;
        bool profileIsRenaming;
        string profileRenameBuffer = "";
        string profileSaveAsBuffer = "";

        /// <summary>
        /// float.TryParse accepts the literals "NaN" / "Infinity" — a NaN would slip past every
        /// &lt;= 0 guard (NaN comparisons are always false) and poison meshes/text with NaN values,
        /// so parsed input must be finite. / float.TryParse 接受 "NaN"/"Infinity" 字面量——NaN 会
        /// 绕过所有 &lt;= 0 守卫（NaN 比较恒 false），把 NaN 值写进 mesh/文本，输入必须为有限值。
        /// </summary>
        private static bool IsFiniteFloat(float v)
            => !float.IsNaN(v) && !float.IsInfinity(v);

        private float FloatSliderField(GUIContent label, float value, float min, float max, string format = "F2")
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(100));
            float slid = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(120));
            // Buffered text field (TextInputField): typed intermediate states ("", "-", "0.") survive
            // until they parse, and the model only changes when the text actually differs from the
            // model echo. The old version re-fed value.ToString every event, wiping half-typed values
            // (negatives in the -10..10 offset fields were unreachable by typing) and re-clamping a
            // stored below-min value to min on every event while the tab was open.
            // 缓冲文本框（TextInputField）：输入中间态（""、"-"、"0."）保留到可解析为止；仅当文本
            // 与模型回显不同才写回模型。旧版每事件重灌 value.ToString，半输入的值被立刻冲掉
            // （-10..10 偏移字段无法用键盘输入负数），且存储值低于下限时打开标签页就被静默改写。
            string ctrl = "fsf_" + (++sliderFieldSeq);
            string modelText = slid.ToString(format);
            if (slid != value) textInputBuffer.Remove(ctrl); // slider drag refreshes the field / 拖动滑块时刷新文本框
            string text = TextInputField(ctrl, modelText, FloatFieldWidth(modelText));
            if (text != modelText && float.TryParse(text, out float parsed) && IsFiniteFloat(parsed))
            {
                // Fully open input (extending v1.6.2's open ceiling, per user decision): typed values
                // are stored as-is — negatives included, and values beyond either slider end; the
                // slider thumb just pegs. Consumers are degenerate-but-safe with such values (a
                // zero/negative fade duration merely stops the fade; non-positive drop rects are
                // skipped by the renderer).
                // 输入完全不钳制（用户决定，v1.6.2 放开上限的延伸）：键入的值原样存储——包括负数与
                // 超出滑块两端的值，滑块仅顶格。消费端对这类值退化但安全（零/负淡出时长只是不再
                // 淡出；非正的雨滴矩形被渲染器直接跳过）。
                slid = parsed;
            }
            GUILayout.EndHorizontal();
            return slid;
        }

        private float FloatSliderField(string label, float value, float min, float max, string format = "F2")
            => FloatSliderField(new GUIContent(label), value, min, max, format);

        private static bool DrawFoldoutButton(string label, bool expanded)
        {
            if (GUILayout.Button((expanded ? "◢ " : "▶ ") + label, GUI.skin.label, GUILayout.MinWidth(200)))
                return !expanded;
            return expanded;
        }

        private static int DrawFoldoutButton(string label, int expandedType, int expandValue = 0)
        {
            if (GUILayout.Button((expandedType >= 0 ? "◢ " : "▶ ") + label, GUI.skin.label, GUILayout.MinWidth(200)))
                return expandedType >= 0 ? -1 : expandValue;
            return expandedType;
        }

        private static void DrawFoldoutItemButton(string label, ref int state, int itemIndex)
        {
            if (GUILayout.Button((state == itemIndex ? "◢ " : "▶ ") + label, GUI.skin.label, GUILayout.MinWidth(200)))
                state = state == itemIndex ? -1 : itemIndex;
        }

        private void DrawProfileSection()
        {
            GUILayout.BeginVertical("box");
            DrawProfileFoldout();
            if (profileExpanded)
            {
                DrawProfileList();
                GUILayout.Space(3);
                GUILayout.BeginHorizontal();
                DrawProfileSaveAs();
                DrawProfileRenameButton();
                DrawProfileDeleteButton();
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
                DrawPackageSection();
            }
            GUILayout.EndVertical();
            GUILayout.Space(5);
        }

        private void DrawProfileFoldout()
        {
            string label = I18n.Tr("profile") + ": " + Settings.CurrentProfile;
            if (GUILayout.Button((profileExpanded ? "◢ " : "▶ ") + label, GUILayout.MinWidth(200)))
            {
                profileExpanded = !profileExpanded;
                // Sync with disk once on open instead of on every OnGUI event while expanded — IMGUI
                // fires Layout+Repaint per frame, so the old call was a directory scan several times
                // per frame. The list buttons themselves keep ProfileNames in sync on every action.
                // 展开那一刻与磁盘同步一次，而不是展开期间每个 GUI 事件都同步——IMGUI 每帧触发
                // Layout+Repaint 多次，旧实现等于每帧数次目录扫描。列表按钮的各种操作自身会维护
                // ProfileNames 的同步。
                if (profileExpanded) SyncProfilesWithDisk();
            }
        }

        private void DrawProfileList()
        {
            if (Settings.ProfileNames == null) return;
            for (int i = 0; i < Settings.ProfileNames.Length; i++)
            {
                string p = Settings.ProfileNames[i];
                bool selected = p == Settings.CurrentProfile;
                if (GUILayout.Button((selected ? "✓ " : "  ") + p, GUILayout.MinWidth(200)))
                {
                    if (!selected) SwitchProfile(p);
                    profileExpanded = false;
                }
            }
        }

        private void DrawProfileSaveAs()
        {
            profileSaveAsBuffer = GUILayout.TextField(profileSaveAsBuffer, GUILayout.Width(120));
            if (GUILayout.Button(I18n.Tr("save_as"), GUILayout.MinWidth(60)))
            {
                string name = SanitizeFileName(profileSaveAsBuffer.Trim());
                if (string.IsNullOrEmpty(name)) return;
                if (Settings.ProfileNames != null)
                    // Case-insensitive: on NTFS "MyProfile"/"myprofile" are the same file / 大小写不敏感:
                    // NTFS 上 "MyProfile"/"myprofile" 是同一个文件
                    foreach (var p in Settings.ProfileNames)
                        if (string.Equals(SanitizeFileName(p), name, System.StringComparison.OrdinalIgnoreCase)) return;
                var list = new List<string>(Settings.ProfileNames ?? new string[0]) { name };
                Settings.ProfileNames = list.ToArray();
                Settings.CurrentProfile = name;
                SaveCurrentProfile();
                SaveMetaOnly();
                profileSaveAsBuffer = "";
                profileExpanded = false;
            }
        }

        private void DrawProfileRenameButton()
        {
            if (!profileIsRenaming)
            {
                if (GUILayout.Button(I18n.Tr("rename"), GUILayout.MinWidth(60)))
                {
                    profileIsRenaming = true;
                    profileRenameBuffer = Settings.CurrentProfile;
                }
                return;
            }
            profileRenameBuffer = GUILayout.TextField(profileRenameBuffer, GUILayout.Width(100));
            if (GUILayout.Button("✓", GUILayout.Width(24)))
            {
                string newName = SanitizeFileName(profileRenameBuffer.Trim());
                if (!string.IsNullOrEmpty(newName) && newName != SanitizeFileName(Settings.CurrentProfile))
                {
                    bool dup = Settings.ProfileNames != null
                        && Settings.ProfileNames.Any(p => SanitizeFileName(p) == newName);
                    if (!dup)
                        RenameProfile(Settings.CurrentProfile, newName);
                }
                profileIsRenaming = false;
                profileExpanded = false;
            }
            if (GUILayout.Button("✗", GUILayout.Width(24)))
                profileIsRenaming = false;
        }

        // ===== profile packages (.jkv) / 配置包（.jkv） =====
        private bool packageListExpanded;
        private string packageMessage = "";

        /// <summary>Export / import the current profile as a shareable .jkv archive. / 把当前配置
        /// 导出 / 导入为可分享的 .jkv 归档。</summary>
        private void DrawPackageSection()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label(I18n.Tr("pkg_section"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(I18n.Tr("pkg_export"), GUILayout.MinWidth(110f)))
            {
                string path = ExportProfilePackage(Settings.CurrentProfile);
                packageMessage = path != null
                    ? string.Format(I18n.Tr("pkg_exported"), Path.GetFileName(path))
                    : I18n.Tr("pkg_err_export");
            }
            if (GUILayout.Button(I18n.Tr("pkg_import"), GUILayout.MinWidth(110f)))
            {
                // Refresh on open, not per event: IMGUI fires Layout+Repaint per frame and this is a
                // directory scan. / 展开时刷新而非每事件刷新：IMGUI 每帧触发 Layout+Repaint 多次，
                // 而这是一次目录扫描。
                packageListExpanded = !packageListExpanded;
                if (packageListExpanded) SyncProfilesWithDisk();
            }
            if (GUILayout.Button(I18n.Tr("fm_open_dir"), GUILayout.MinWidth(90f)))
            {
                try
                {
                    string dir = Path.Combine(Loader.ModPath, "Packages");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
                catch (Exception e)
                {
                    Loader.Error($"KeyViewer: cannot open packages folder: {e.Message}");
                }
            }
            GUILayout.EndHorizontal();

            if (packageListExpanded)
            {
                List<string> packages = ListProfilePackages();
                if (packages.Count == 0)
                {
                    GUILayout.Label(I18n.Tr("pkg_none"));
                }
                else
                {
                    foreach (string pkg in packages)
                    {
                        if (!GUILayout.Button(pkg, GUILayout.MinWidth(200f))) continue;
                        if (ImportProfilePackage(pkg, out string msg)) packageMessage = msg;
                        else packageMessage = msg ?? I18n.Tr("pkg_err_failed_generic");
                        packageListExpanded = false;
                    }
                }
            }

            if (!string.IsNullOrEmpty(packageMessage))
                GUILayout.Label(packageMessage);
            GUILayout.Label(I18n.Tr("pkg_hint"));
            GUILayout.EndVertical();
        }

        private void DrawProfileDeleteButton()
        {
            bool canDelete = Settings.ProfileNames != null && Settings.ProfileNames.Length > 1;
            GUI.enabled = canDelete;
            if (GUILayout.Button(I18n.Tr("delete"), GUILayout.MinWidth(60)))
            {
                DeleteProfile(Settings.CurrentProfile);
                profileExpanded = false;
            }
            GUI.enabled = true;
        }

        private void DrawLanguageSection()
        {
            GUILayout.BeginHorizontal();
            string[] langLabels = { "English", "中文", "한국어" };
            // I18n renders unknown language codes as English — mirror that here so the highlighted
            // entry matches what is actually shown. / I18n 将未知语言代码按英文渲染——此处保持一致,
            // 让高亮项与实际显示一致。
            int langIdx = Settings.Language == "zh" ? 1 : Settings.Language == "ko" ? 2 : 0;
            if (GUILayout.Button(I18n.Tr("language") + ": " + langLabels[langIdx]))
            {
                langIdx = (langIdx + 1) % 3;
                Settings.Language = langIdx == 0 ? "en" : langIdx == 1 ? "zh" : "ko";
                I18n.Lang = Settings.Language;
                SaveSettingsFromGui();
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Count display options (the enable toggle and reset-count button moved to the header bar) / 计数显示选项(总开关与重置计数按钮已移到顶部常驻栏)
        /// </summary>
        private void DrawCountResetSection()
        {
            GUILayout.BeginHorizontal();
            bool newFormatting = GUILayout.Toggle(Settings.Data.EnableCountFormatting, I18n.Tr("count_formatting"));
            if (newFormatting != Settings.Data.EnableCountFormatting)
            {
                Settings.Data.EnableCountFormatting = newFormatting;
                SaveSettingsFromGui();
                RefreshAllCountDisplay();
            }
            GUILayout.EndHorizontal();
        }

        private void ExecuteCountReset()
        {
            lastTotal = -1;
            Settings.Data.TotalCount = 0;
            for (int i = 0; i < Settings.Data.Count.Length; i++)
                Settings.Data.Count[i] = 0;
            foreach (FmNode node in Settings.Data.CustomNodes)
                if (node != null) node.Count = 0;
            ClearKpsTimers();
            if (Keys != null)
                for (int i = 0; i < Keys.Length; i++)
                    if (Keys[i] != null && Keys[i].value != null) // Unity overload catches destroyed keys / Unity 重载可识别已销毁按键
                        Keys[i].value.text = "0";
            foreach (Key k in IsCustomLayout ? StatKeys(1) : SinglePanel(Kps))
                SetKpsTotalDisplay(k, "KPS", "0");
            foreach (Key k in IsCustomLayout ? StatKeys(2) : SinglePanel(Total))
                SetKpsTotalDisplay(k, "Total", "0");
            SaveSettingsFromGui();
        }

        private static readonly (int flag, string label)[] FontStyleFlagLabels =
        {
            (1, "B"), (2, "I"), (4, "U"), (8, "Lc"),
            (16, "Uc"), (32, "Sc"), (64, "St"), (128, "Sup"), (256, "Sub")
        };

        private string BuildFontStyleSummary()
        {
            int f = Settings.Data.FontStyleFlags;
            if (f == 0) return "Normal";
            var parts = new List<string>(4);
            foreach (var (flag, label) in FontStyleFlagLabels)
                if ((f & flag) != 0) parts.Add(label);
            return string.Join(" ", parts);
        }

        private void DrawFontSection()
        {
            GUILayout.Label(I18n.Tr("font_style") + ":");
            string curFont = fontList.Count > 0 ? fontList[Mathf.Clamp(Settings.Data.FontIndex, 0, fontList.Count - 1)].name : "None";
            if (GUILayout.Button((fontListExpanded ? "◢ " : "▶ ") + curFont, GUILayout.MinWidth(200)))
                fontListExpanded = !fontListExpanded;
            if (fontListExpanded)
            {
                if (fontList.Count > 0)
                {
                    int newIdx = Settings.Data.FontIndex;
                    for (int i = 0; i < fontList.Count; i++)
                    {
                        bool selected = i == Settings.Data.FontIndex;
                        if (GUILayout.Button((selected ? "✓ " : "  ") + fontList[i].name, GUILayout.MinWidth(200)))
                            newIdx = i;
                    }
                    if (newIdx != Settings.Data.FontIndex)
                    {
                        Settings.Data.FontIndex = newIdx;
                        Settings.Data.FontName = fontList[newIdx].name;
                        fontRestored = false;
                        UpdateAllFonts();
                        SaveSettingsFromGui();
                    }
                }
                else
                    GUILayout.Label("▶ " + I18n.Tr("no_fonts_found"), GUILayout.MinWidth(200));

                GUILayout.Space(5);
                GUILayout.BeginVertical("box");
                GUILayout.Label(I18n.Tr("custom_font_tip"));
                GUILayout.Label($"CustomFont : {Path.Combine(Loader.ModPath, "CustomFont")}");
                GUILayout.EndVertical();
            }

            GUILayout.Space(3);
            string styleSummary = BuildFontStyleSummary();
            fontStyleExpanded = DrawFoldoutButton(I18n.Tr("font_style") + ": " + styleSummary, fontStyleExpanded);
            if (fontStyleExpanded)
            {
                string[] styleNames = { "Bold", "Italic", "Underline", "Lowercase", "Uppercase", "SmallCaps", "Strikethrough", "Superscript", "Subscript" };
                int[] styleFlags = { 1, 2, 4, 8, 16, 32, 64, 128, 256 };
                int[] styleGroups = { 0, 0, 0, 1, 1, 1, 0, 2, 2 };
                bool changed = false;
                for (int i = 0; i < styleFlags.Length; i++)
                {
                    bool active = (Settings.Data.FontStyleFlags & styleFlags[i]) != 0;
                    bool newActive = GUILayout.Toggle(active, styleNames[i]);
                    if (newActive != active)
                    {
                        if (newActive)
                        {
                            if (styleGroups[i] == 1)
                                Settings.Data.FontStyleFlags &= ~(8 | 16 | 32);
                            else if (styleGroups[i] == 2)
                                Settings.Data.FontStyleFlags &= ~(128 | 256);
                        }
                        Settings.Data.FontStyleFlags = newActive ? Settings.Data.FontStyleFlags | styleFlags[i] : Settings.Data.FontStyleFlags & ~styleFlags[i];
                        changed = true;
                    }
                }
                if (changed)
                {
                    UpdateAllFonts();
                    SaveSettingsFromGui();
                }
            }
        }

        private void DrawFolderButtons()
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(I18n.Tr("open_config_folder"), GUILayout.MinWidth(120)))
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            if (GUILayout.Button(I18n.Tr("open_font_folder"), GUILayout.MinWidth(120)))
            {
                string modPath = Loader.ModPath;
                string customFontDir = Path.Combine(modPath, "CustomFont");
                if (!Directory.Exists(customFontDir)) Directory.CreateDirectory(customFontDir);
                System.Diagnostics.Process.Start("explorer.exe", customFontDir);
            }
            GUILayout.EndHorizontal();

            // DownLocation shifts the fixed layouts' preset rows — custom nodes are absolute
            // screen coordinates, the toggle is meaningless there. / 下移对固定布局的预设排生效
            // ——自定义节点是绝对屏幕坐标，该开关对其无意义。
            if (!IsCustomLayout)
            {
                bool newDownLocation = GUILayout.Toggle(Settings.Data.DownLocation, I18n.Tr("place_below"));
                if (newDownLocation != Settings.Data.DownLocation)
                {
                    Settings.Data.DownLocation = newDownLocation;
                    // ResetKeyViewer recreates foot keys internally — no outer ResetFootKeyViewer.
                    // ResetKeyViewer 内部已重建脚键——无需外层 ResetFootKeyViewer。
                    ResetKeyViewer();
                    SaveSettingsFromGui();
                }
            }
        }

        private void DrawCustomPositionSection()
        {
            // Custom layout: node positions live in the FreeMake editor — the sliders here drive
            // ResetKeyViewerPosition, which deliberately no-ops for custom (dead controls).
            // / 自定义布局：节点位置在 FreeMake 编辑器里调——本区滑块驱动 ResetKeyViewerPosition，
            // 而它对自定义布局刻意空操作（会变成死控件）。
            if (IsCustomLayout)
            {
                GUILayout.Label(I18n.Tr("fm_custom_pos_hint"));
                return;
            }
            CustomPositionExpanded = DrawFoldoutButton(I18n.Tr("custom_pos"), CustomPositionExpanded);
            if (!CustomPositionExpanded) return;

            GUILayout.BeginVertical("box");
            bool newEnabled = GUILayout.Toggle(Settings.Data.CustomPositionEnabled,
                I18n.Tr("custom_pos") + " " + I18n.Tr("enable"));
            if (newEnabled != Settings.Data.CustomPositionEnabled)
            {
                Settings.Data.CustomPositionEnabled = newEnabled;
                SaveSettingsFromGui();
                if (newEnabled)
                {
                    ResetKeyViewerPosition();
                    ResetFootKeyViewerPosition();
                }
                else
                {
                    // ResetKeyViewer recreates foot keys internally — no outer ResetFootKeyViewer.
                    // ResetKeyViewer 内部已重建脚键——无需外层 ResetFootKeyViewer。
                    ResetKeyViewer();
                }
            }

            if (Settings.Data.CustomPositionEnabled)
            {
                if (KeyViewer.IsFullKeyboard)
                {
                    GUILayout.Label(I18n.Tr("main_key_pos") + ":");
                    Vector2 tempMainPos = Settings.Data.MainKeyViewerPosition;
                    bool positionChanged = false;
                    float newMainX = FloatSliderField("X", tempMainPos.x, 0f, 1f);
                    if (newMainX != tempMainPos.x) { tempMainPos.x = newMainX; positionChanged = true; }
                    float newMainY = FloatSliderField("Y", tempMainPos.y, 0f, 1f);
                    if (newMainY != tempMainPos.y) { tempMainPos.y = newMainY; positionChanged = true; }
                    if (positionChanged)
                    {
                        Settings.Data.MainKeyViewerPosition = tempMainPos;
                        ResetKeyViewerPosition();
                        SaveSettingsFromGui();
                    }
                    if (GUILayout.Button(I18n.Tr("reset_pos")))
                    {
                        Settings.Data.MainKeyViewerPosition = new Vector2(0, 1);
                        ResetKeyViewerPosition();
                        SaveSettingsFromGui();
                    }
                }
                else
                {
                    GUILayout.Label(I18n.Tr("main_key_pos") + ":");
                    Vector2 tempMainPos = Settings.Data.MainKeyViewerPosition;
                    Vector2 tempFootPos = Settings.Data.FootKeyViewerPosition;
                    bool positionChanged = false;

                    float newMainX = FloatSliderField("X", tempMainPos.x, 0f, 1f);
                    if (newMainX != tempMainPos.x) { tempMainPos.x = newMainX; positionChanged = true; }
                    float newMainY = FloatSliderField("Y", tempMainPos.y, 0f, 1f);
                    if (newMainY != tempMainPos.y) { tempMainPos.y = newMainY; positionChanged = true; }

                    GUILayout.Label(I18n.Tr("foot_key_pos") + ":");
                    float newFootX = FloatSliderField("X", tempFootPos.x, 0f, 1f);
                    if (newFootX != tempFootPos.x) { tempFootPos.x = newFootX; positionChanged = true; }
                    float newFootY = FloatSliderField("Y", tempFootPos.y, 0f, 1f);
                    if (newFootY != tempFootPos.y) { tempFootPos.y = newFootY; positionChanged = true; }

                    if (positionChanged)
                    {
                        Settings.Data.MainKeyViewerPosition = tempMainPos;
                        Settings.Data.FootKeyViewerPosition = tempFootPos;
                        ResetKeyViewerPosition();
                        ResetFootKeyViewerPosition();
                        SaveSettingsFromGui();
                    }

                    if (GUILayout.Button(I18n.Tr("reset_pos")))
                    {
                        Settings.Data.MainKeyViewerPosition = new Vector2(0, 1);
                        Settings.Data.FootKeyViewerPosition = new Vector2(0.24f, 1f);
                        ResetKeyViewerPosition();
                        ResetFootKeyViewerPosition();
                        SaveSettingsFromGui();
                    }
                }
            }
            GUILayout.EndVertical();
        }

        private void DrawLayoutSection()
        {
            GUILayout.Label(I18n.Tr("key_layout") + ":");
            KeyviewerStyle newStyle = (KeyviewerStyle)GUILayout.SelectionGrid((int)Settings.Data.KeyViewerStyle, KeyLayoutNames, 3);
            if (newStyle != Settings.Data.KeyViewerStyle)
            {
                Settings.Data.KeyViewerStyle = newStyle;
                ChangeKeyViewer();
                SaveSettingsFromGui();
            }

            if (IsCustomLayout)
            {
                if (GUILayout.Button(I18n.Tr("fm_open_editor"), GUILayout.Height(26f)))
                    OpenFreeMakeEditor();
            }

            // Custom layouts keep the width toggle visible — it picks WHICH variant (standard
            // 50px vs mixed-width) the FreeMake PRESET generator builds for 12K/20K styles. /
            // 自定义布局保留宽度开关——它决定 FreeMake 预设生成器为 12K/20K 等样式构建哪套
            // 变体（标准 50px 还是宽窄混排）。
            if (!KeyViewer.IsFullKeyboard)
            {
                // Standard key width toggle: only show for layouts with mixed-width back rows
                // 标准按键宽度开关：仅在有宽窄键混排的布局显示
                bool hasNonStandardWidth = IsCustomLayout || Settings.Data.KeyViewerStyle switch
                {
                    KeyviewerStyle.Key10 or KeyviewerStyle.Key12 or KeyviewerStyle.Key20 => true,
                    _ => false
                };
                if (hasNonStandardWidth)
                {
                    bool newStdWidth = GUILayout.Toggle(Settings.Data.StandardKeyWidth, I18n.Tr("standard_key_width"));
                    if (newStdWidth != Settings.Data.StandardKeyWidth)
                    {
                        Settings.Data.StandardKeyWidth = newStdWidth;
                        ChangeKeyViewer();
                        SaveSettingsFromGui();
                    }
                }

                // Custom layouts initialize no foot keys at all (KeyViewerLayout.InitializeMainKeys
                // skips them), so the grid is a dead control there — and leaving it reachable kept
                // the foot-position path alive against the node canvas. / 自定义布局完全不初始化
                // 脚键（见 KeyViewerLayout.InitializeMainKeys），该网格在此是死控件——留着它也让
                // 脚键定位路径继续作用于节点画布。
                if (!IsCustomLayout)
                {
                    GUILayout.Label(I18n.Tr("foot_keys") + ":");
                    FootKeyviewerStyle newFootStyle = (FootKeyviewerStyle)GUILayout.SelectionGrid((int)Settings.Data.FootKeyViewerStyle, FootKeyLayoutNames, 5);
                    if (newFootStyle != Settings.Data.FootKeyViewerStyle)
                    {
                        Settings.Data.FootKeyViewerStyle = newFootStyle;
                        ResetFootKeyViewer();
                        SaveSettingsFromGui();
                    }
                }
            }

            float newSettingsSize = FloatSliderField(I18n.Tr("size"), Settings.Data.Size, 0.1f, 2f);
            if (newSettingsSize != Settings.Data.Size)
            {
                Settings.Data.Size = newSettingsSize;
                if (KeyViewerSizeObject != null)
                    KeyViewerSizeObject.transform.localScale = new Vector3(Settings.Data.Size, Settings.Data.Size, 1);
                SaveSettingsFromGui();
            }
        }

        // ===== Per-key text size section / 每键字号 区块 =====
        int perKeyTextSelected = -1;
        bool perKeyTextExpanded = false;

        private void DrawPerKeyTextSizeSection()
        {
            // Per-key text size targets the standard layouts — GetKeyCode/GetBackSequence don't map to
            // the 108-key view, so hide the whole section there. / 每键字号面向标准布局——
            // GetKeyCode/GetBackSequence 与 108 键视图不对应，全键盘下隐藏整个区块。
            if (KeyViewer.IsFullKeyboard) return;
            if (IsCustomLayout) return; // custom nodes carry their own sizes / 自定义节点的字号在编辑器里配
            perKeyTextExpanded = DrawFoldoutButton(I18n.Tr("per_key_text_size"), perKeyTextExpanded);
            if (!perKeyTextExpanded) return;

            GUILayout.BeginVertical("box");
            bool pk = GUILayout.Toggle(Settings.Data.EnablePerKeyTextSize, I18n.Tr("enable"));
            if (pk != Settings.Data.EnablePerKeyTextSize)
            {
                Settings.Data.EnablePerKeyTextSize = pk;
                if (!pk)
                {
                    int n = KeyViewer.MaxKeySlots + 2;
                    if (Settings.Data.PerKeyFontSize != null)
                        for (int i = 0; i < n && i < Settings.Data.PerKeyFontSize.Length; i++)
                            Settings.Data.PerKeyFontSize[i] = 0f;
                }
                UpdateAllFonts();
                SaveSettingsFromGui();
            }

            if (Settings.Data.EnablePerKeyTextSize)
            {
                GUILayout.BeginVertical("box");
                KeyCode[] keyCodes = GetKeyCode();
                KeyCode[] footKeyCodes = GetFootKeyCode();

                GUILayout.Label(I18n.Tr("row1_keys") + ":");
                GUILayout.BeginHorizontal();
                for (int i = 0; i < 8; i++) DrawPerKeyTextSizeBtn(i, KeyToString(keyCodes[i]));
                GUILayout.EndHorizontal();

                byte[] backSequence = GetBackSequence();
                if (backSequence.Length > 0)
                {
                    GUILayout.Label(I18n.Tr("row2_keys") + ":");
                    GUILayout.BeginHorizontal();
                    for (int b = 0; b < backSequence.Length && b < 8; b++)
                        DrawPerKeyTextSizeBtn(backSequence[b], KeyToString(keyCodes[backSequence[b]]));
                    GUILayout.EndHorizontal();
                }

                if (backSequence.Length > 8)
                {
                    GUILayout.Label(I18n.Tr("row3_keys") + ":");
                    GUILayout.BeginHorizontal();
                    for (int b = 8; b < backSequence.Length && backSequence[b] < keyCodes.Length; b++)
                        DrawPerKeyTextSizeBtn(backSequence[b], KeyToString(keyCodes[backSequence[b]]));
                    GUILayout.EndHorizontal();
                }

                if (footKeyCodes != null && footKeyCodes.Length > 0)
                {
                    GUILayout.Label(I18n.Tr("foot_keys") + ":");
                    int rows = footKeyCodes.Length <= 8 ? 1 : 2;
                    for (int r = 0; r < rows; r++)
                    {
                        GUILayout.BeginHorizontal();
                        int start = r * 8;
                        int end = Mathf.Min(start + 8, footKeyCodes.Length);
                        for (int f = start; f < end; f++)
                            DrawPerKeyTextSizeBtn(FootKeyBase + f, KeyToString(footKeyCodes[f]));
                        GUILayout.EndHorizontal();
                    }
                }

                GUILayout.Space(5);
                GUILayout.BeginHorizontal();
                DrawPerKeyTextSizeBtn(MaxKeySlots, "KPS");
                DrawPerKeyTextSizeBtn(MaxKeySlots + 1, "Total");
                GUILayout.EndHorizontal();

                if (perKeyTextSelected >= 0 && perKeyTextSelected < MaxKeySlots + 2)
                    DrawPerKeyTextSizeEditor(perKeyTextSelected);

                if (GUILayout.Button(I18n.Tr("per_key_color_reset")))
                {
                    int n = MaxKeySlots + 2;
                    if (Settings.Data.PerKeyFontSize != null)
                        for (int i = 0; i < n && i < Settings.Data.PerKeyFontSize.Length; i++)
                            Settings.Data.PerKeyFontSize[i] = 0f;
                    UpdateAllFonts();
                    SaveSettingsFromGui();
                }

                GUILayout.EndVertical();
            }

            GUILayout.EndVertical();
        }

        private void DrawPerKeyTextSizeBtn(int idx, string label)
        {
            bool selected = perKeyTextSelected == idx;
            if (GUILayout.Button((selected ? "[ " : "  ") + label + (selected ? " ]" : ""), GUILayout.MinWidth(50)))
            {
                perKeyTextSelected = perKeyTextSelected == idx ? -1 : idx;
            }
        }

        private void DrawPerKeyTextSizeEditor(int s)
        {
            GUILayout.Space(5);
            string label = s == MaxKeySlots ? "KPS" : s == MaxKeySlots + 1 ? "Total" : "Key " + s;
            GUILayout.Label("<b>" + label + " " + I18n.Tr("key_font_size") + "</b>");

            // Font size: 0 = use global, 1-72 = per-key override
            float curSize = s < Settings.Data.PerKeyFontSize.Length ? Settings.Data.PerKeyFontSize[s] : 0f;
            string sizeLabel = curSize <= 0f ? "(Global: " + Settings.Data.KeyFontSize.ToString("F0") + ")" : curSize.ToString("F0");
            float newSize = FloatSliderField(label + " " + I18n.Tr("key_font_size"), curSize, 0f, 72f, "F0");
            if (newSize != curSize)
            {
                if (Settings.Data.PerKeyFontSize != null && s < Settings.Data.PerKeyFontSize.Length)
                {
                    Settings.Data.PerKeyFontSize[s] = Mathf.Round(newSize);
                    UpdateAllFonts();
                    SaveSettingsFromGui();
                }
            }
        }

        private void DrawDisplaySection()
        {
            // The main-count / per-key-KPS toggles only apply to the normal (non-full-keyboard) layouts;
            // the full 108-key view shows key labels and has its own KPS/Total controls. Custom nodes
            // carry their own HideCount / PerKeyKps per node. / 主区域计数与每键KPS 开关仅对普通布局
            // 生效；全键盘显示键位字母、并有独立的 KPS/Total 控制。自定义节点逐节点自带
            // HideCount / PerKeyKps。
            if (!KeyViewer.IsFullKeyboard && !IsCustomLayout)
            {
                bool newHideCount = GUILayout.Toggle(Settings.Data.HideMainKeyCount, I18n.Tr("hide_main_count"));
                if (newHideCount != Settings.Data.HideMainKeyCount)
                {
                    Settings.Data.HideMainKeyCount = newHideCount;
                    ResetKeyViewer();
                    SaveSettingsFromGui();
                }

                if (!Settings.Data.HideMainKeyCount)
                {
                    bool newPerKeyKps = GUILayout.Toggle(Settings.Data.EnablePerKeyKps, I18n.Tr("per_key_kps"));
                    if (newPerKeyKps != Settings.Data.EnablePerKeyKps)
                    {
                        Settings.Data.EnablePerKeyKps = newPerKeyKps;
                        RefreshAllCountDisplay();
                        // Clear the change-detection cache: with stale lastPerKeyKps values, a quick
                        // off→on toggle within 1s kept showing the old count (equal kps skipped the
                        // SetText) until the queues drained. / 清变化检测缓存:lastPerKeyKps 残留
                        // 时,1 秒内快速关→开会一直显示旧计数(相等的 kps 跳过 SetText)直到队列排空。
                        if (lastPerKeyKps != null)
                            for (int i = 0; i < lastPerKeyKps.Length; i++)
                                lastPerKeyKps[i] = 0;
                        SaveSettingsFromGui();
                    }
                }
            }

            // Streamer mode (hides KPS/Total) only applies to the normal layouts; the full keyboard has
            // its own dedicated "Show KPS / Total" toggle, so don't show this redundant one there.
            // 主播模式（隐藏 KPS/Total）仅对普通布局生效；全键盘已有专属的「显示 KPS/Total」开关，故不再显示这个重复的。
            if (!KeyViewer.IsFullKeyboard)
            {
                bool newStreamer = GUILayout.Toggle(Settings.Data.StreamerMode, I18n.Tr("streamer_mode"));
                if (newStreamer != Settings.Data.StreamerMode)
                {
                    Settings.Data.StreamerMode = newStreamer;
                    SetStatsVisible(!newStreamer);
                    SaveSettingsFromGui();
                }
            }

            // Hide KPS/Total label (non-slim only): value centered, replaces the top label
            // 隐藏 KPS/Total 标签（仅非 slim）：数值居中，替换顶部标签
            bool hasNonSlimKpsTotal = !KeyViewer.KpsTotalIsSlim();
            if (hasNonSlimKpsTotal)
            {
                bool newHideLabel = GUILayout.Toggle(Settings.Data.HideKpsTotalLabel, I18n.Tr("hide_kps_total_label"));
                if (newHideLabel != Settings.Data.HideKpsTotalLabel)
                {
                    Settings.Data.HideKpsTotalLabel = newHideLabel;
                    ChangeKeyViewer();
                    SaveSettingsFromGui();
                }
            }

            float newFontSize = FloatSliderField(I18n.Tr("key_font_size"), Settings.Data.KeyFontSize, 8f, 72f, "F0");
            if (newFontSize != Settings.Data.KeyFontSize)
            {
                Settings.Data.KeyFontSize = newFontSize;
                UpdateAllFonts();
                SaveSettingsFromGui();
            }

            // Per-key text size / spacing section / 每键字号/字间距 区块
            DrawPerKeyTextSizeSection();

            DrawTextStyleSection();

            // Center KPS / Total text — only for flat (slim) KPS/Total designs (e.g. full keyboard, 8K/14K/16K/24K).
            // Hidden for stacked (non-slim) layouts like 12K/10K/20K standard. / 仅对扁平（slim）KPS/Total 生效（全键盘及 8K/14K/16K/24K 等），堆叠布局（12K/10K/20K 标准）隐藏。
            if (KeyViewer.KpsTotalIsSlim())
            {
                bool newCenterKt = GUILayout.Toggle(Settings.Data.KpsTotalCentered, I18n.Tr("fk_kps_total_centered"));
                if (newCenterKt != Settings.Data.KpsTotalCentered)
                {
                    Settings.Data.KpsTotalCentered = newCenterKt;
                    ChangeKeyViewer();
                    SaveSettingsFromGui();
                }
                // Stack KPS/Total text vertically — only available when centered is enabled
                // KPS/Total 上下堆叠 — 仅在居中开启时可用
                if (Settings.Data.KpsTotalCentered)
                {
                    bool newStacked = GUILayout.Toggle(Settings.Data.KpsTotalStackedWhenCentered, I18n.Tr("kps_total_stacked"));
                    if (newStacked != Settings.Data.KpsTotalStackedWhenCentered)
                    {
                        Settings.Data.KpsTotalStackedWhenCentered = newStacked;
                        ChangeKeyViewer();
                        SaveSettingsFromGui();
                    }
                }
            }

            bool newPressAnim = GUILayout.Toggle(Settings.Data.EnablePressAnimation, I18n.Tr("press_animation"));
            if (newPressAnim != Settings.Data.EnablePressAnimation)
            {
                Settings.Data.EnablePressAnimation = newPressAnim;
                // Turning the animation off mid-press skips the release transition (it's gated on
                // this toggle) — reset scales so no key stays stuck shrunken. / 按住途中关闭动画
                // 会跳过释放过渡（受本开关门控）——重置缩放，避免按键卡在缩小状态。
                if (!newPressAnim) ResetAllPressScales();
                SaveSettingsFromGui();
            }

            if (Settings.Data.EnablePressAnimation)
            {
                float newScale = FloatSliderField(I18n.Tr("press_anim_scale"), Settings.Data.PressAnimationScale, 0.5f, 0.95f);
                if (newScale != Settings.Data.PressAnimationScale)
                {
                    Settings.Data.PressAnimationScale = newScale;
                    SaveSettingsFromGui();
                }

                bool newRainAnim = GUILayout.Toggle(Settings.Data.EnablePressAnimationOnRain, I18n.Tr("press_anim_rain"));
                if (newRainAnim != Settings.Data.EnablePressAnimationOnRain)
                {
                    Settings.Data.EnablePressAnimationOnRain = newRainAnim;
                    SaveSettingsFromGui();
                }

                // Easing + duration: the old build hard-coded a linear 80ms lerp, so these default
                // to exactly that and existing setups look unchanged. / 缓动 + 时长：旧版写死线性
                // 80ms 插值，故默认值即为此，现有配置观感不变。
                DrawEasingSelector("press_anim_easing", Settings.Data.PressAnimationEasing, v =>
                {
                    Settings.Data.PressAnimationEasing = v;
                    SaveSettingsFromGui();
                });
                float newDur = FloatSliderField(I18n.Tr("press_anim_duration"), Settings.Data.PressAnimationDurationMs, 10f, 500f, "F0");
                if (newDur != Settings.Data.PressAnimationDurationMs)
                {
                    Settings.Data.PressAnimationDurationMs = newDur;
                    Settings.Data.PressAnimationDurationMs = Mathf.Clamp(newDur, 10f, 2000f);
                    SaveSettingsFromGui();
                }
            }
        }

        // ===== text outline / shadow (Display tab) / 文字描边/阴影（显示页） =====
        // Key label and press count carry INDEPENDENT outline + shadow pairs: a large label and a
        // small count number rarely want the same treatment. / 按键标签与计数文本各自带独立的描边
        // + 阴影：大号标签与小号计数很少需要同样的处理。
        /// <summary>Named-easing selector with a curve preview. A plain dropdown would make 27
        /// cubic-bezier-free curves indistinguishable by name alone — the preview is what makes the
        /// choice possible. / 带曲线预览的命名缓动选择器。纯下拉框会让 27 条与三次贝塞尔无关的曲线
        /// 仅凭名字无法区分——预览才让选择成为可能。</summary>
        private void DrawEasingSelector(string id, string current, Action<string> apply)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.Tr("press_anim_easing"), GUILayout.Width(100f));
            string shown = string.IsNullOrEmpty(current) ? Util.KvEasing.Default : current;
            if (GUILayout.Button(shown, GUILayout.Width(140f)))
            {
                easingListExpanded = easingListExpanded == id ? null : id;
                if (easingListExpanded == id) easingScroll = Vector2.zero;
            }
            // Preview of the CURRENT selection sits next to the button, so the chosen curve is always
            // visible without opening the list. / 当前选择的预览就在按钮旁边，无需展开列表即可看到
            // 所选曲线。
            DrawEasingCurve(new Rect(GUILayoutUtility.GetRect(56f, 22f).position, new Vector2(56f, 22f)), shown);
            GUILayout.EndHorizontal();

            if (easingListExpanded != id) return;
            GUILayout.BeginVertical("box");
            // Fixed-height scroll: 27 entries must not stretch the settings window off-screen. /
            // 固定高度滚动区：27 个条目不能把设置窗口撑出屏幕。
            easingScroll = GUILayout.BeginScrollView(easingScroll, GUILayout.Height(180f));
            for (int i = 0; i < Util.KvEasing.Names.Length; i++)
            {
                string name = Util.KvEasing.Names[i];
                bool selected = string.Equals(name, shown, StringComparison.OrdinalIgnoreCase);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button((selected ? "✓ " : "  ") + name, GUILayout.MinWidth(150f)))
                {
                    apply(name);
                    easingListExpanded = null;
                }
                DrawEasingCurve(new Rect(GUILayoutUtility.GetRect(70f, 20f).position, new Vector2(70f, 20f)), name);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        /// <summary>Plot one easing into a rect. Values outside [0,1] (the back variants overshoot)
        /// are accommodated by padding the plot vertically, so an overshoot curve is visible rather
        /// than clipped flat. / 把一条缓动画进矩形。越出 [0,1] 的值（back 系列会过冲）通过在竖直方向
        /// 留边来容纳，使过冲曲线可见而非被削平。</summary>
        private void DrawEasingCurve(Rect r, string name)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            GUIUtils.DrawRect(r, new Color(0.12f, 0.12f, 0.14f, 0.85f));
            const int Steps = 24;
            float minV = 0f, maxV = 1f;
            // Two passes: first find the actual range (only back easings leave [0,1]), then plot. /
            // 两遍：先求实际取值范围（只有 back 系列会越出 [0,1]），再绘制。
            for (int i = 0; i <= Steps; i++)
            {
                float v = Util.KvEasing.Ease(name, i / (float)Steps);
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            float span = Mathf.Max(0.0001f, maxV - minV);
            Vector2 prev = Vector2.zero;
            for (int i = 0; i <= Steps; i++)
            {
                float t = i / (float)Steps;
                float v = Util.KvEasing.Ease(name, t);
                Vector2 p = new Vector2(r.x + t * r.width, r.yMax - (v - minV) / span * r.height);
                if (i > 0) DrawEasingSegment(prev, p);
                prev = p;
            }
            DrawRectOutline(r, new Color(1f, 1f, 1f, 0.18f), 1f);
        }

        private void DrawEasingSegment(Vector2 a, Vector2 b)
        {
            // Sample the segment rather than drawing a true line: GUIUtils has only axis-aligned
            // rects, and 3 interpolated dots per step is indistinguishable at this size. /
            // 用取样代替真正的画线：GUIUtils 只有轴对齐矩形，而这个尺寸下每段 3 个插值点已经看不出差别。
            const int Dots = 4;
            for (int i = 0; i <= Dots; i++)
            {
                float t = i / (float)Dots;
                float x = Mathf.Lerp(a.x, b.x, t);
                float y = Mathf.Lerp(a.y, b.y, t);
                GUIUtils.DrawRect(new Rect(x, y, 1.5f, 1.5f), new Color(0.55f, 0.85f, 1f, 1f));
            }
        }

        private void DrawTextStyleSection()
        {
            GUILayout.Space(4f);
            GUILayout.Label(I18n.Tr("text_style"));
            DrawTextStyleBlock("key",
                Settings.Data.EnableKeyTextOutline, Settings.Data.KeyTextOutlineColor, Settings.Data.KeyTextOutlineThickness,
                Settings.Data.EnableKeyTextShadow, Settings.Data.KeyTextShadowColor,
                Settings.Data.KeyTextShadowOffsetX, Settings.Data.KeyTextShadowOffsetY, Settings.Data.KeyTextShadowSoftness,
                (on, col, w) =>
                {
                    Settings.Data.EnableKeyTextOutline = on;
                    Settings.Data.KeyTextOutlineColor = col;
                    Settings.Data.KeyTextOutlineThickness = w;
                },
                (on, col, ox, oy, soft) =>
                {
                    Settings.Data.EnableKeyTextShadow = on;
                    Settings.Data.KeyTextShadowColor = col;
                    Settings.Data.KeyTextShadowOffsetX = ox;
                    Settings.Data.KeyTextShadowOffsetY = oy;
                    Settings.Data.KeyTextShadowSoftness = soft;
                });

            DrawTextStyleBlock("count",
                Settings.Data.EnableCountTextOutline, Settings.Data.CountTextOutlineColor, Settings.Data.CountTextOutlineThickness,
                Settings.Data.EnableCountTextShadow, Settings.Data.CountTextShadowColor,
                Settings.Data.CountTextShadowOffsetX, Settings.Data.CountTextShadowOffsetY, Settings.Data.CountTextShadowSoftness,
                (on, col, w) =>
                {
                    Settings.Data.EnableCountTextOutline = on;
                    Settings.Data.CountTextOutlineColor = col;
                    Settings.Data.CountTextOutlineThickness = w;
                },
                (on, col, ox, oy, soft) =>
                {
                    Settings.Data.EnableCountTextShadow = on;
                    Settings.Data.CountTextShadowColor = col;
                    Settings.Data.CountTextShadowOffsetX = ox;
                    Settings.Data.CountTextShadowOffsetY = oy;
                    Settings.Data.CountTextShadowSoftness = soft;
                });
        }

        private void DrawTextStyleBlock(string id, bool outlineOn, Color outlineColor, float outlineWidth,
            bool shadowOn, Color shadowColor, float offX, float offY, float softness,
            Action<bool, Color, float> applyOutline, Action<bool, Color, float, float, float> applyShadow)
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label(I18n.Tr(id == "key" ? "fm_key_text_style" : "fm_count_text_style"));

            bool newOutline = GUILayout.Toggle(outlineOn, I18n.Tr("fm_text_outline"));
            if (newOutline != outlineOn)
            {
                applyOutline(newOutline, outlineColor, outlineWidth);
                RefreshTextStyles();
            }
            if (newOutline)
            {
                Color newCol = DrawColorPicker(I18n.Tr("fm_text_outline_color"), outlineColor, Color.black);
                if (newCol != outlineColor)
                {
                    applyOutline(newOutline, newCol, outlineWidth);
                    RefreshTextStyles();
                }
                float newW = FloatSliderField(I18n.Tr("fm_text_outline_thickness"), outlineWidth, 0f, 1f);
                if (newW != outlineWidth)
                {
                    applyOutline(newOutline, outlineColor, newW);
                    RefreshTextStyles();
                }
            }

            bool newShadow = GUILayout.Toggle(shadowOn, I18n.Tr("fm_text_shadow"));
            if (newShadow != shadowOn)
            {
                applyShadow(newShadow, shadowColor, offX, offY, softness);
                RefreshTextStyles();
            }
            if (newShadow)
            {
                Color newCol = DrawColorPicker(I18n.Tr("fm_text_shadow_color"), shadowColor, new Color(0f, 0f, 0f, 0.5f));
                if (newCol != shadowColor)
                {
                    applyShadow(newShadow, newCol, offX, offY, softness);
                    RefreshTextStyles();
                }
                float newX = FloatSliderField(I18n.Tr("fm_text_shadow_offset_x"), offX, -20f, 20f, "F0");
                float newY = FloatSliderField(I18n.Tr("fm_text_shadow_offset_y"), offY, -20f, 20f, "F0");
                if (newX != offX || newY != offY)
                {
                    applyShadow(newShadow, shadowColor, newX, newY, softness);
                    RefreshTextStyles();
                }
                float newSoft = FloatSliderField(I18n.Tr("fm_text_shadow_softness"), softness, 0f, 16f, "F0");
                if (newSoft != softness)
                {
                    applyShadow(newShadow, shadowColor, offX, offY, newSoft);
                    RefreshTextStyles();
                }
            }
            GUILayout.EndVertical();
        }

        /// <summary>Push the new global text style onto every live text. A full rebuild would also
        /// do it, but rebuilding on every slider tick during a drag is what the debounced save path
        /// exists to avoid — re-materialing the existing texts is enough, and the overlay only needs
        /// a rebuild when per-node overrides have to re-resolve. / 把新的全局文字样式写入所有现存
        /// 文本。整体重建也能做到，但拖动时每个滑杆 tick 都重建正是去抖保存要避免的——给现存文本
        /// 换材质就够了；只有需要重新解析节点级覆盖时覆盖层才需重建。</summary>
        private void RefreshTextStyles()
        {
            UpdateAllFonts();
            if (IsCustomLayout) RequestEditorRebuild();
            SaveSettingsFromGui();
        }

        // ===== Full-keyboard KPS / Total section foldout state =====
        bool KpsTotalExpanded = false;

        // ======================== Full Keyboard (108K) KPS / Total layout section ========================
        private void DrawFullKeyboardKpsTotalSection()
        {
            KpsTotalExpanded = DrawFoldoutButton(I18n.Tr("fk_kps_total"), KpsTotalExpanded);
            if (!KpsTotalExpanded) return;
            GUILayout.BeginVertical("box");
            // Show / hide toggle (single source of truth for visibility). / 显示开关（可见性的唯一控制入口）。
            bool showKt = GUILayout.Toggle(Settings.Data.FullKeyboardShowKpsTotal, I18n.Tr("fk_show_kps_total"));
            if (showKt != Settings.Data.FullKeyboardShowKpsTotal)
            {
                Settings.Data.FullKeyboardShowKpsTotal = showKt;
                ChangeKeyViewer();
                SaveSettingsFromGui();
            }
            // KPS / Total box size (px) — layout property, kept here not in the color section. / KPS/Total 框尺寸（像素），属布局属性，置于此而非颜色栏目。
            float newKtSize = FloatSliderField(I18n.Tr("fk_kps_total_size"), Settings.Data.FullKeyboardKpsTotalSize, 40f, 400f, "F0");
            if (newKtSize != Settings.Data.FullKeyboardKpsTotalSize)
            {
                Settings.Data.FullKeyboardKpsTotalSize = newKtSize;
                ChangeKeyViewer();
                SaveSettingsFromGui();
            }
            if (Settings.Data.FullKeyboardShowKpsTotal)
            {
                // KPS / Total custom position (normalized 0-1) / KPS/Total 自定义位置（归一化 0-1）
                GUILayout.Space(5);
                GUILayout.Label(I18n.Tr("fk_kps_pos") + ":");
                Vector2 kpsPos = Settings.Data.FullKpsPosition;
                float kpsX = FloatSliderField("X", kpsPos.x, 0f, 1f);
                float kpsY = FloatSliderField("Y", kpsPos.y, 0f, 1f);
                if (kpsX != kpsPos.x || kpsY != kpsPos.y)
                {
                    Settings.Data.FullKpsPosition = new Vector2(kpsX, kpsY);
                    ApplyFullKeyboardKpsTotalPosition();
                    SaveSettingsFromGui();
                }
                GUILayout.Label(I18n.Tr("fk_total_pos") + ":");
                Vector2 totalPos = Settings.Data.FullTotalPosition;
                float totalX = FloatSliderField("X", totalPos.x, 0f, 1f);
                float totalY = FloatSliderField("Y", totalPos.y, 0f, 1f);
                if (totalX != totalPos.x || totalY != totalPos.y)
                {
                    Settings.Data.FullTotalPosition = new Vector2(totalX, totalY);
                    ApplyFullKeyboardKpsTotalPosition();
                    SaveSettingsFromGui();
                }
            }
            GUILayout.EndVertical();
        }
    }
}
