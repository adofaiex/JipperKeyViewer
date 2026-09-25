// Settings GUI: Colors tab content / 设置界面:颜色 标签页内容
// Normal color settings, per-key colors, full-keyboard unified colors, KPS/Total colors, and the shared color picker (RGBA + hex). / 普通配色、每键独立颜色、全键盘统一配色、KPS/Total 配色,以及通用颜色选择器(RGBA + Hex)

using System.Collections.Generic;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer : MonoBehaviour
    {
        /// <summary>Repaint the drops already on screen after a global rain colour change. The rain
        /// system may not exist yet (overlay disabled), and Keys may be mid-rebuild; RefreshDropColors
        /// handles both. / 全局雨色改动后重绘在屏雨滴。雨滴系统可能尚未创建（覆盖层关闭），Keys 也
        /// 可能正在重建；RefreshDropColors 两者都能处理。
        /// </summary>
        private void RefreshRainDropColors()
        {
            var rain = rainSystem;
            if (rain != null) rain.RefreshDropColors(Keys);
        }

        private void DrawColorSettings()
        {
            GUILayout.BeginVertical("box");
            string[] colorNames = {
                I18n.Tr("color_bg"), I18n.Tr("color_bg_clicked"), I18n.Tr("color_outline"), I18n.Tr("color_outline_clicked"),
                I18n.Tr("color_text"), I18n.Tr("color_text_clicked"),
                I18n.Tr("color_rain1"), I18n.Tr("color_rain2"), I18n.Tr("color_rain3"),
                I18n.Tr("ghost_rain_color1"), I18n.Tr("ghost_rain_color2"), I18n.Tr("ghost_rain_color3")
            };
            Color[] defaultColors = {
                Background, BackgroundClicked, Outline, OutlineClicked,
                Text, TextClicked,
                RainColor, RainColor2, RainColor3,
                GhostRainColorDefault, GhostRainColor2Default, GhostRainColor3Default
            };
            for (int i = 0; i < 12; i++)
            {
                if (i >= 6 && i < 9 && !Settings.Data.EnableRainEffect)
                    continue;
                if (i >= 9 && !Settings.Data.EnableGhostRain)
                    continue;
                ColorExpanded[i] = DrawFoldoutButton(colorNames[i], ColorExpanded[i]);
                if (ColorExpanded[i])
                {
                    GUILayout.BeginVertical("box");
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(15);
                    Color currentColor = GetColorByIndex(i);
                    Color newColor = DrawColorPicker(colorNames[i], currentColor, defaultColors[i]);
                    if (newColor != currentColor)
                    {
                        SetColorByIndex(i, newColor);
                        UpdateAllKeyColors();
                        // Indices 6..11 are the rain / ghost-rain colours. A drop's colour is baked
                        // in at creation and UpdateAllKeyColors does not touch the rain system, so
                        // with a tall track and a slow speed the on-screen drops kept the old
                        // colour for seconds — the control looked broken. Repaint them in place.
                        // 下标 6..11 是雨滴/鬼雨颜色。雨滴颜色在创建时烙入，而 UpdateAllKeyColors
                        // 不碰雨滴系统，故高轨道配慢速度时在屏雨滴会保持旧色好几秒——控件看起来像
                        // 坏了。现就地重绘它们。
                        if (i >= 6) RefreshRainDropColors();
                        SaveSettingsFromGui();
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.EndVertical();
                }
            }
            GUILayout.Space(5);
            DrawKpsTotalColors(MaxKeySlots, I18n.Tr("kps_colors"), ref kpsColorType);
            GUILayout.Space(3);
            DrawKpsTotalColors(MaxKeySlots + 1, I18n.Tr("total_colors"), ref totalColorType);
            GUILayout.EndVertical();
        }

        // ===== KPS & Total independent color state / KPS 与 Total 独立配色状态 =====
        int kpsColorType = -1;
        int totalColorType = -1;

        private static Color KpsTotalColor(int pi, int t) => pi == MaxKeySlots
            ? t switch { 0 => Settings.Data.KpsBackground, 1 => Settings.Data.KpsOutline, _ => Settings.Data.KpsText }
            : t switch { 0 => Settings.Data.TotalBackground, 1 => Settings.Data.TotalOutline, _ => Settings.Data.TotalText };

        private static void SetKpsTotalColor(int pi, int t, Color c)
        {
            if (pi == MaxKeySlots)
            {
                if (t == 0) Settings.Data.KpsBackground = c;
                else if (t == 1) Settings.Data.KpsOutline = c;
                else Settings.Data.KpsText = c;
            }
            else
            {
                if (t == 0) Settings.Data.TotalBackground = c;
                else if (t == 1) Settings.Data.TotalOutline = c;
                else Settings.Data.TotalText = c;
            }
        }

        private void DrawKpsTotalColors(int pi, string label, ref int expandedType)
        {
            expandedType = DrawFoldoutButton(label, expandedType);
            if (expandedType < 0) return;

            string[] typeNames = {
                I18n.Tr("color_bg"), I18n.Tr("color_outline"), I18n.Tr("color_text")
            };
            Color[] defaults = { Background, Outline, Text };

            for (int t = 0; t < 3; t++)
            {
                DrawFoldoutItemButton(typeNames[t], ref expandedType, t);
                if (expandedType != t) continue;

                GUILayout.BeginVertical("box");
                GUILayout.BeginHorizontal();
                GUILayout.Space(15);
                Color cur = KpsTotalColor(pi, t);
                Color newColor = DrawColorPicker(typeNames[t], cur, defaults[t]);
                if (newColor != cur)
                {
                    SetKpsTotalColor(pi, t, newColor);
                    UpdateAllKeyColors();
                    SaveSettingsFromGui();
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
            }
        }

        // Per-frame counters naming the text fields of color pickers and slider rows (reset in
        // DrawSettingsWindow) so focus can be tracked per-field via control names on every IMGUI
        // version. / 每帧自增的字段命名计数器(在 DrawSettingsWindow 开头重置),颜色选择器与
        // 滑块行的文本框都用它跨版本稳定跟踪焦点。
        private static int colorPickerFieldSeq;
        private static int sliderFieldSeq;

        // In-progress text for named text fields (color pickers, slider text boxes), keyed by control
        // name. Persists across frames so a focused field isn't reset to the model value while its
        // value is being typed. / 命名文本框(颜色选择器、滑块文本框)的进行中输入(按控件名缓存),
        // 跨帧保留,避免输入过程中被模型值重置。
        //
        // Control names are POSITIONAL ("cpi_N"/"fsf_N" counters), so a buffer entry surviving the
        // disappearance of its control (foldout collapsed, tab switched) would attach to whatever
        // field inherits the sequence number next — showing stale half-typed text and COMMITTING it
        // to the wrong setting on the very first click. The per-pass set below garbage-collects
        // entries whose control wasn't drawn this pass, making stale reuse impossible.
        // 控件名是位置序号("cpi_N"/"fsf_N"计数器),缓冲条目若在控件消失(折叠/切标签)后存活,
        // 会附着到下一个继承该序号的字段上——点击的瞬间回显陈旧半输入文本并提交到错误的设置。
        // 下方的逐 pass 集合会回收"本 pass 未绘制"的条目,杜绝陈旧复用。
        private readonly Dictionary<string, string> textInputBuffer = new Dictionary<string, string>();
        private readonly HashSet<string> textCtrlsDrawnThisPass = new HashSet<string>();
        private readonly List<string> staleTextCtrlScratch = new List<string>();

        /// <summary>Start a GUI pass: reset the drawn-controls set (called from DrawSettingsWindow). / 开始一个 GUI pass:重置本 pass 已绘制控件集合(由 DrawSettingsWindow 调用)。</summary>
        private void BeginTextInputPass()
        {
            textCtrlsDrawnThisPass.Clear();
        }

        /// <summary>
        /// End a GUI pass: drop buffer entries whose control was not drawn this pass (folded away,
        /// tab switched, section hidden) — they must not leak into a different field that reuses
        /// the same positional name later.
        /// 结束一个 GUI pass:丢弃本 pass 未绘制其控件的缓冲条目(被折叠/切标签/隐藏)——
        /// 它们绝不能泄漏进之后复用同一位置序号的另一字段。
        /// </summary>
        private void EndTextInputPass()
        {
            if (textInputBuffer.Count == 0) return;
            // The focused field's entry survives explicitly (its control is normally drawn this
            // pass, but a future early-return could skip the field while focus lingers).
            // 焦点字段条目显式存活(其控件通常本 pass 在绘制,但未来提前 return 可能在焦点
            // 未移走时跳过字段绘制)。
            string focused = GUI.GetNameOfFocusedControl();
            staleTextCtrlScratch.Clear();
            foreach (var key in textInputBuffer.Keys)
            {
                // "fme_" buffers belong to the FreeMake editor window's own pass and are GC'd
                // there — never here. / "fme_" 前缀缓冲归 FreeMake 编辑器自己的 pass 回收，
                // 此处永不触碰。
                if (key.StartsWith("fme_", System.StringComparison.Ordinal)) continue;
                if (textCtrlsDrawnThisPass.Contains(key) || key == focused) continue;
                staleTextCtrlScratch.Add(key);
            }
            for (int i = 0; i < staleTextCtrlScratch.Count; i++)
                textInputBuffer.Remove(staleTextCtrlScratch[i]);
        }

        private string TextInputField(string ctrlName, string modelText, params GUILayoutOption[] options)
        {
            GUI.SetNextControlName(ctrlName);
            textCtrlsDrawnThisPass.Add(ctrlName);
            bool focused = GUI.GetNameOfFocusedControl() == ctrlName;
            string display = focused && textInputBuffer.TryGetValue(ctrlName, out string pending)
                ? pending : modelText;
            string txt = GUILayout.TextField(display, options);
            if (focused) textInputBuffer[ctrlName] = txt;
            else textInputBuffer.Remove(ctrlName);
            return txt;
        }

        /// <summary>Color to #RRGGBB, or #RRGGBBAA when alpha &lt; 255 / 颜色转 #RRGGBB,非不透明时输出 #RRGGBBAA</summary>
        private static string ColorToHex(Color c)
        {
            int r = Mathf.RoundToInt(Mathf.Clamp01(c.r) * 255f);
            int g = Mathf.RoundToInt(Mathf.Clamp01(c.g) * 255f);
            int b = Mathf.RoundToInt(Mathf.Clamp01(c.b) * 255f);
            int a = Mathf.RoundToInt(Mathf.Clamp01(c.a) * 255f);
            return a < 255 ? $"#{r:X2}{g:X2}{b:X2}{a:X2}" : $"#{r:X2}{g:X2}{b:X2}";
        }

        /// <summary>Parse #RRGGBB or #RRGGBBAA; 6-digit input sets alpha to opaque / 解析 #RRGGBB 或 #RRGGBBAA;6 位输入时 alpha 设为不透明</summary>
        private static bool TryParseHex(string input, out Color color)
        {
            color = Color.white;
            string s = string.IsNullOrEmpty(input) ? string.Empty : input.Trim().TrimStart('#');
            if (s.Length != 6 && s.Length != 8) return false;
            if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint v))
                return false;
            if (s.Length == 6)
                color = new Color(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, 1f);
            else
                color = new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
            return true;
        }

        /// <summary>Hex string cached by the exact colour it renders. ColorToHex allocates on every
        /// call, and the picker runs per colour per IMGUI event — with a per-key colour list open
        /// that is dozens of identical strings rebuilt per event forever. / 按其渲染的确切颜色
        /// 缓存的十六进制串。ColorToHex 每次调用都会分配，而取色器每颜色每 IMGUI 事件都跑一次
        /// ——打开每键配色列表时即每事件重复重建几十个完全相同的字符串，永不停歇。</summary>
        private static readonly Dictionary<int, string> hexStringCache = new Dictionary<int, string>(64);

        private static string ColorToHexCached(Color c)
        {
            int key = ((Color32)c).GetHashCode();
            if (hexStringCache.TryGetValue(key, out string cached)) return cached;
            string built = ColorToHex(c);
            // Bounded: distinct colours are few in practice, and an unbounded map keyed by hash
            // could grow without limit on a hand-edited profile. / 有界：实践中不同颜色很少，而按
            // 哈希的无界映射在手改配置下可能无限增长。
            if (hexStringCache.Count > 512) hexStringCache.Clear();
            hexStringCache[key] = built;
            return built;
        }

        /// <summary>Colour picker. `prefix` namespaces the generated control names: the FreeMake
        /// editor and the settings window are separate IMGUI passes that can be open at the SAME
        /// time, and both reset the shared sequence counter — with one shared prefix their Hex/RGB
        /// text buffers collided (typing in one window rewrote the other's field) and the settings
        /// pass's stale-buffer sweep deleted the editor's in-progress entries.
        /// 取色器。`prefix` 用于隔离控件名命名空间：FreeMake 编辑器与设置窗口是两个可同时打开的
        /// IMGUI pass，且共用同一个序号计数器——若前缀相同，两个窗口的 Hex/RGB 输入缓冲会互相
        /// 串写，设置窗口的陈旧缓冲清理还会删掉编辑器正在编辑的条目。</summary>
        private Color DrawColorPicker(string label, Color currentColor, Color defaultColor, string prefix = "cpi_")
        {
            GUILayout.BeginVertical();
            GUILayout.Label(label);

            // Unique control names allocated up-front in draw order so focus tracking stays stable.
            // 先按绘制顺序分配唯一的控件名,保证焦点跟踪一致。
            // The two prefixes are a closed set, so the per-pass sequence can be expanded once into
            // a lookup table instead of concatenating five strings per picker per event. `prefix`
            // selects WHICH table — indexing the table with the bare sequence (the first version
            // of this optimisation) handed the editor "cpi_N" names too, silently undoing the
            // fme_cpi_ namespacing this very method documents.
            // 两个前缀是封闭集合，故每次 pass 的序号可一次性展开成查表，而不必每个取色器每事件
            // 拼接五个字符串。`prefix` 决定用**哪一张**表——最初的优化拿裸序号去索引，于是编辑器
            // 也拿到了 "cpi_N"，静默废掉了本方法注释里写明的那套 fme_cpi_ 隔离。
            string[] names = string.Equals(prefix, EditorColorPickerPrefix, System.StringComparison.Ordinal)
                ? EditorColorPickerNames
                : SettingsColorPickerNames;
            string ctrlR, ctrlG, ctrlB, ctrlA, ctrlHex;
            if (colorPickerFieldSeq >= 0 && colorPickerFieldSeq + 5 <= names.Length)
            {
                int n = colorPickerFieldSeq;
                ctrlR = names[n]; ctrlG = names[n + 1]; ctrlB = names[n + 2];
                ctrlA = names[n + 3]; ctrlHex = names[n + 4];
                colorPickerFieldSeq += 5;
            }
            else
            {
                ctrlR = prefix + (++colorPickerFieldSeq);
                ctrlG = prefix + (++colorPickerFieldSeq);
                ctrlB = prefix + (++colorPickerFieldSeq);
                ctrlA = prefix + (++colorPickerFieldSeq);
                ctrlHex = prefix + (++colorPickerFieldSeq);
            }

            void DrawChannel(string ctrl, string name, ref float channel)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(name + ":", ChannelLabelWidth);
                channel = GUILayout.HorizontalSlider(channel, 0f, 1f, ChannelSliderWidth);
                // Text input keeps Unity's 0-1 scale; use the Hex field below for precise values.
                // 文本框保持 0-1;要精确取色时用下面的 Hex 输入。
                string txt = TextInputField(ctrl, FormatChannel(channel), ChannelEditWidth);
                if (float.TryParse(txt, out float val) && IsFiniteFloat(val))
                    channel = Mathf.Clamp01(val);
                GUILayout.EndHorizontal();
            }

            DrawChannel(ctrlR, "R", ref currentColor.r);
            DrawChannel(ctrlG, "G", ref currentColor.g);
            DrawChannel(ctrlB, "B", ref currentColor.b);
            DrawChannel(ctrlA, "A", ref currentColor.a);

            // Direct #RRGGBB / #RRGGBBAA hex entry. / 直接输入 #RRGGBB 或 #RRGGBBAA
            GUILayout.BeginHorizontal();
            GUILayout.Label("Hex:", ChannelLabelWidth);
            string hex = TextInputField(ctrlHex, ColorToHexCached(currentColor), HexEditWidth);
            if (TryParseHex(hex, out Color parsed))
                currentColor = parsed;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.Tr("preview") + ":", GUILayout.Width(40));
            Rect previewRect = GUILayoutUtility.GetRect(100, 20);
            GUIUtils.DrawRect(previewRect, currentColor);
            GUILayout.EndHorizontal();
            if (GUILayout.Button(I18n.Tr("reset_default")))
            {
                currentColor = defaultColor;
            }
            GUILayout.EndVertical();
            return currentColor;
        }

        private int perKeyColorSelected = -1;
        private int perKeyColorTypeIndex = -1;

        private void DrawPerKeyColorSettings()
        {
            GUILayout.BeginVertical("box");
            KeyCode[] keyCodes = GetKeyCode();
            KeyCode[] footKeyCodes = GetFootKeyCode();
            // Every index below is driven by profile data (key arrays and the per-row back
            // sequence). EnsureSettingsArrays currently guarantees the lengths, but a load path
            // that bypassed it would throw IndexOutOfRange inside OnGUI — which disables the whole
            // settings window, not just this row. Skip what is not there instead.
            // 下面的索引全部来自配置数据（按键数组与每排序列）。EnsureSettingsArrays 目前保证了
            // 长度，但任何绕过它的加载路径都会在 OnGUI 内抛 IndexOutOfRange——整个设置窗口会被
            // 禁用，而不只是这一行。缺失的部分直接跳过。
            if (keyCodes == null || keyCodes.Length < 8)
            {
                GUILayout.EndVertical();
                return;
            }

            GUILayout.Label(I18n.Tr("row1_keys") + ":");
            GUILayout.BeginHorizontal();
            for (int i = 0; i < 8; i++) DrawPerKeyColorBtn(i, KeyToString(keyCodes[i]));
            GUILayout.EndHorizontal();

            byte[] backSequence = GetBackSequence();
            if (backSequence.Length > 0)
            {
                GUILayout.Label(I18n.Tr("row2_keys") + ":");
                GUILayout.BeginHorizontal();
                for (int b = 0; b < backSequence.Length && b < 8; b++)
                    if (backSequence[b] < keyCodes.Length)
                        DrawPerKeyColorBtn(backSequence[b], KeyToString(keyCodes[backSequence[b]]));
                GUILayout.EndHorizontal();
            }

            if (backSequence.Length > 8)
            {
                GUILayout.Label(I18n.Tr("row3_keys") + ":");
                GUILayout.BeginHorizontal();
                for (int b = 8; b < backSequence.Length && backSequence[b] < keyCodes.Length; b++)
                    DrawPerKeyColorBtn(backSequence[b], KeyToString(keyCodes[backSequence[b]]));
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
                        DrawPerKeyColorBtn(FootKeyBase + f, KeyToString(footKeyCodes[f]));
                    GUILayout.EndHorizontal();
                }
            }

            GUILayout.Space(5);
            GUILayout.BeginHorizontal();
            DrawPerKeyColorBtn(MaxKeySlots, "KPS");
            DrawPerKeyColorBtn(MaxKeySlots + 1, "Total");
            GUILayout.EndHorizontal();

            if (perKeyColorSelected >= 0 && perKeyColorSelected < MaxKeySlots + 2)
                DrawPerKeyColorEditor(perKeyColorSelected);

            if (GUILayout.Button(I18n.Tr("per_key_color_reset")))
            { Settings.Data.InitPerKeyColors(); UpdateAllKeyColors(); SaveSettingsFromGui(); }
            if (GUILayout.Button(I18n.Tr("auto_rainbow")))
                AutoAssignRainbowColors();

            GUILayout.EndVertical();
        }

        // Cached button styles — IMGUI redraws every event; the old per-call new GUIStyle(...) in the
        // per-key loop was ~26 allocations per frame. textColor is set per call, before the single
        // Button that consumes the style. / 缓存的按钮样式——IMGUI 每事件重绘;旧实现在每键循环里
        // 逐调用 new GUIStyle(...) 约每帧 26 次分配。textColor 在每次调用时、紧随其后的唯一
        // Button 消费前设置。
        private static GUIStyle perKeyBtnStyle;
        private static GUIStyle redBtnStyle;

        private void DrawPerKeyColorBtn(int idx, string label)
        {
            // PerKeyBackground is populated by the ProfileData constructor, which FromJsonOverwrite
            // can bypass; a null/short array here would throw inside OnGUI and disable the whole
            // settings window. / PerKeyBackground 由 ProfileData 构造函数填充，而 FromJsonOverwrite
            // 可绕过构造函数；数组为 null 或过短会在 OnGUI 内抛异常并禁用整个设置窗口。
            Color c = idx >= 0 && Settings.Data.PerKeyBackground != null && idx < Settings.Data.PerKeyBackground.Length
                ? Settings.Data.PerKeyBackground[idx]
                : Color.white;
            if (perKeyBtnStyle == null) perKeyBtnStyle = new GUIStyle(GUI.skin.button);
            perKeyBtnStyle.normal.textColor = c.grayscale > 0.5f ? Color.black : Color.white;
            if (perKeyColorSelected == idx)
                GUI.backgroundColor = Color.Lerp(c, Color.white, 0.4f);
            else
                GUI.backgroundColor = c;
            bool pressed = GUILayout.Button(label, perKeyBtnStyle);
            GUI.backgroundColor = Color.white;
            if (pressed)
            {
                if (perKeyColorSelected != idx) perKeyColorTypeIndex = -1;
                perKeyColorSelected = perKeyColorSelected == idx ? -1 : idx;
            }
        }

        private static string PerKeyLabel(int s) => s switch
        {
            MaxKeySlots => "KPS",
            MaxKeySlots + 1 => "Total",
            _ => KeyToString(GetKeyCodeForIndex(s))
        };

        // Type sets per slot kind — static: the GUI redraws every event, so per-call new[] was steady
        // garbage. / 槽位类型集合——静态:GUI 每事件重绘,逐调用 new[] 是持续垃圾。
        private static readonly int[] PerKeyMainTypes = { 0, 1, 2, 3, 4, 5, 6, 7 };
        private static readonly int[] PerKeyFootTypes = { 0, 1, 2, 3, 4, 5 };
        private static readonly int[] KpsTotalTypes = { 0, 2, 4 };

        private static int[] PerKeyTypeOrder(int s) => s >= MaxKeySlots
            ? KpsTotalTypes
            : s >= FootKeyBase ? PerKeyFootTypes : PerKeyMainTypes;

        private bool DrawColorFoldout(int t, string name)
        {
            DrawFoldoutItemButton(name, ref perKeyColorTypeIndex, t);
            return perKeyColorTypeIndex == t;
        }

        private void DrawPerKeyColorEditor(int s)
        {
            GUILayout.Space(5);
            GUILayout.Label("Key " + s + " (" + PerKeyLabel(s) + ")");
            // Foot keys skip rain color types (6,7); PerKeyTypeOrder excludes them
            // 脚键不提供雨滴配色(6,7);PerKeyTypeOrder 已排除
            string rainKey = s < 8 ? "color_rain1" : s < 16 ? "color_rain2" : s < FootKeyBase ? "color_rain3" : "";

            // Reused scratch arrays. This method runs for EVERY key on EVERY IMGUI event, and the
            // three literal arrays below were three fresh allocations per key per event — with 24
            // keys and the ≥2 events per frame that is 150+ objects per frame, forever, on the
            // Colors tab. / 复用暂存数组。该方法对**每个**按键在**每个** IMGUI 事件里都跑，而下面
            // 三个数组字面量是每按键每事件三次新分配——24 个按键、每帧 ≥2 个事件即每帧 150+ 个
            // 对象，永不停歇，且就在 Colors 页上。
            PerKeyTypeNames[0] = I18n.Tr("color_bg"); PerKeyTypeNames[1] = I18n.Tr("color_bg_clicked");
            PerKeyTypeNames[2] = I18n.Tr("color_outline"); PerKeyTypeNames[3] = I18n.Tr("color_outline_clicked");
            PerKeyTypeNames[4] = I18n.Tr("color_text"); PerKeyTypeNames[5] = I18n.Tr("color_text_clicked");
            PerKeyTypeNames[6] = I18n.Tr(rainKey);
            PerKeyTypeNames[7] = rainKey.Length > 0 ? "Ghost " + PerKeyTypeNames[6] : PerKeyTypeNames[6];
            PerKeyTypeValues[0] = Settings.Data.PerKeyBackground[s]; PerKeyTypeValues[1] = Settings.Data.PerKeyBackgroundClicked[s];
            PerKeyTypeValues[2] = Settings.Data.PerKeyOutline[s]; PerKeyTypeValues[3] = Settings.Data.PerKeyOutlineClicked[s];
            PerKeyTypeValues[4] = Settings.Data.PerKeyText[s]; PerKeyTypeValues[5] = Settings.Data.PerKeyTextClicked[s];
            PerKeyTypeValues[6] = Settings.Data.PerKeyRainColor[s]; PerKeyTypeValues[7] = Settings.Data.PerKeyGhostRainColor[s];
            PerKeyTypeDefaults[0] = Background; PerKeyTypeDefaults[1] = BackgroundClicked;
            PerKeyTypeDefaults[2] = Outline; PerKeyTypeDefaults[3] = OutlineClicked;
            PerKeyTypeDefaults[4] = Text; PerKeyTypeDefaults[5] = TextClicked;
            PerKeyTypeDefaults[6] = RainColor; PerKeyTypeDefaults[7] = GhostRainColorDefault;
            string[] typeNames = PerKeyTypeNames;
            Color[] values = PerKeyTypeValues;
            Color[] defaults = PerKeyTypeDefaults;

            int[] typeOrder = PerKeyTypeOrder(s);
            for (int ti = 0; ti < typeOrder.Length; ti++)
            {
                int t = typeOrder[ti];
                if (!DrawColorFoldout(t, typeNames[t])) continue;

                GUILayout.BeginHorizontal();
                GUILayout.Space(15);
                GUILayout.BeginVertical("box");
                Color newColor = DrawColorPicker(typeNames[t], values[t], defaults[t]);
                if (newColor != values[t])
                {
                    SetPerKeyColor(s, t, newColor);
                    UpdateAllKeyColors();
                    SaveSettingsFromGui();
                }
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
            }

            if (s < MaxKeySlots && Settings.Data.Count != null && s < Settings.Data.Count.Length)
                DrawPerKeyCountReset(s);
        }

        private static readonly string[] PerKeyTypeNames = new string[8];
        private static readonly Color[] PerKeyTypeValues = new Color[8];
        private static readonly Color[] PerKeyTypeDefaults = new Color[8];

        /// <summary>Per-slot caches for the count row: control name, button label, and the count
        /// that label was built for. Sized for the widest slot count the fixed layouts use. The
        /// width option is an INSTANCE field: a static one would run the partial class's static
        /// constructor on first touch, pulling GUIContent/GUILayoutOption into type load and making
        /// every caller require UnityEngine.IMGUIModule — even ones that never draw a GUI. /
        /// 计数行按槽位的缓存：控件名、按钮文本、以及该文本所对应的计数。尺寸覆盖固定布局用到的
        /// 最大槽位数。宽度选项是**实例**字段：静态的会在该分部类首次被触碰时运行静态构造，
        /// 把 GUIContent/GUILayoutOption 拖进类型加载，使所有调用方都需要
        /// UnityEngine.IMGUIModule——包括从不绘制界面的那些。
        /// </summary>
        private static readonly string[] PerKeyCountCtrlNames = BuildPerKeyCountCtrlNames(MaxKeySlots);
        private static readonly string[] perKeyResetLabelText = new string[MaxKeySlots];
        private static readonly int[] perKeyResetLabelCache = new int[MaxKeySlots];
        private static readonly System.Text.StringBuilder perKeyCtrlBuilder = new System.Text.StringBuilder(24);
        private readonly GUILayoutOption perKeyCountWidth = GUILayout.Width(64f);

        private static string[] BuildPerKeyCountCtrlNames(int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = "perkey_cnt_" + i;
            return names;
        }

        private static string PerKeyCountCtrl(int slot)
        {
            if (slot < 0 || slot >= PerKeyCountCtrlNames.Length)
            {
                perKeyCtrlBuilder.Length = 0;
                perKeyCtrlBuilder.Append("perkey_cnt_").Append(slot);
                return perKeyCtrlBuilder.ToString();
            }
            return PerKeyCountCtrlNames[slot];
        }

        /// <summary>Pre-expanded "<prefix><n>" control names, one table per window. The prefixes
        /// are a closed set ("cpi_" for the settings window, "fme_cpi_" for the FreeMake editor)
        /// and the per-pass sequence counter only ever grows from 0, so every name a pass can
        /// produce is known up front. Falls back to concatenation if a pass ever exceeds the
        /// table. / 每个窗口一张「前缀 + 序号」控件名预展开表。前缀是封闭集合（设置窗口 "cpi_"、
        /// FreeMake 编辑器 "fme_cpi_"），且每次 pass 的序号只会从 0 递增，故某个 pass 可能产生的
        /// 每个名字都是预先已知的。若某个 pass 超出表长则退回拼接。</summary>
        private const int ColorPickerNameCount = 512;
        private const string EditorColorPickerPrefix = "fme_cpi_";
        private static readonly string[] SettingsColorPickerNames = BuildColorPickerNames("cpi_");
        private static readonly string[] EditorColorPickerNames = BuildColorPickerNames(EditorColorPickerPrefix);
        private readonly GUILayoutOption ChannelLabelWidth = GUILayout.Width(20f);
        private readonly GUILayoutOption ChannelSliderWidth = GUILayout.Width(150f);
        private readonly GUILayoutOption ChannelEditWidth = GUILayout.Width(40f);
        private readonly GUILayoutOption HexEditWidth = GUILayout.Width(120f);
        private float lastChannelValue = float.NaN;
        private string lastChannelText;

        private static string[] BuildColorPickerNames(string prefix)
        {
            var names = new string[ColorPickerNameCount];
            for (int i = 0; i < ColorPickerNameCount; i++) names[i] = prefix + i;
            return names;
        }

        /// <summary>Channel echo, rebuilt only when the value actually changes — the Layout and
        /// Repaint of one frame carry the same value, and formatting it for each allocated a string
        /// per channel per event (four per picker per event). / 通道回显，仅在值真的变化时重建
        /// ——同一帧的 Layout 与 Repaint 带着同一个值，各格式化一次即每通道每事件分配一个字符串
        /// （每个取色器每事件四个）。</summary>
        private string FormatChannel(float v)
        {
            if (lastChannelText != null && lastChannelValue == v) return lastChannelText;
            lastChannelValue = v;
            lastChannelText = v.ToString("F2");
            return lastChannelText;
        }

        private static void SetPerKeyColor(int s, int t, Color color)
        {
            switch (t)
            {
                case 0: Settings.Data.PerKeyBackground[s] = color; break;
                case 1: Settings.Data.PerKeyBackgroundClicked[s] = color; break;
                case 2: Settings.Data.PerKeyOutline[s] = color; break;
                case 3: Settings.Data.PerKeyOutlineClicked[s] = color; break;
                case 4: Settings.Data.PerKeyText[s] = color; break;
                case 5: Settings.Data.PerKeyTextClicked[s] = color; break;
                case 6: Settings.Data.PerKeyRainColor[s] = color; break;
                case 7: Settings.Data.PerKeyGhostRainColor[s] = color; break;
            }
        }

        private void DrawPerKeyCountReset(int s)
        {
            GUILayout.Space(5);
            if (redBtnStyle == null)
                redBtnStyle = new GUIStyle(GUI.skin.button) { normal = { textColor = Color.red } };
            GUILayout.BeginHorizontal();
            // Manual count entry — the fixed-layout twin of the FreeMake editor's field: set
            // this key's count to any value, with the global TotalCount following the delta so
            // the Total panel stays truthful. Parses live while typing (same as the editor).
            // 手动输入计数——FreeMake 编辑器同款功能在固定布局这边的镜像：把该键计数设为
            // 任意值，全局 TotalCount 按差额同步。输入即解析（与编辑器一致）。
            // Both ToString calls and the two string concatenations ran for EVERY key on EVERY
            // IMGUI event, including the ones the user never scrolled to. Cache the button label
            // against the count it displays, and reuse one control name per slot.
            // 两次 ToString 与两处字符串拼接此前对**每个**按键在**每个** IMGUI 事件都执行，包括
            // 用户根本没滚到的那些。现把按钮文本按其显示的计数缓存，并为每个槽位复用一个控件名。
            int displayedCount = Settings.Data.Count[s];
            if (perKeyResetLabelCache[s] != displayedCount)
            {
                perKeyResetLabelCache[s] = displayedCount;
                perKeyResetLabelText[s] = I18n.Tr("reset_counts") + " (" + displayedCount.ToString() + ")";
            }
            string typed = TextInputField(PerKeyCountCtrl(s), displayedCount.ToString(), perKeyCountWidth);
            if (int.TryParse((typed ?? "").Replace("—", "").Trim(), out int newCount)
                && newCount >= 0 && newCount != Settings.Data.Count[s])
            {
                Settings.Data.TotalCount += newCount - Settings.Data.Count[s];
                if (Settings.Data.TotalCount < 0) Settings.Data.TotalCount = 0;
                Settings.Data.Count[s] = newCount;
                if (Keys != null && s < Keys.Length && Keys[s]?.value != null)
                    Keys[s].value.text = newCount.ToString();
                SaveSettingsFromGui();
            }
            if (GUILayout.Button(perKeyResetLabelText[s], redBtnStyle))
            {
                // Give this key's presses back to the Total, mirroring the FreeMake reset —
                // previously the per-key reset zeroed the count but left the Total counting
                // them forever. / 把该键的按压还给 Total，与 FreeMake 重置对齐——此前每键
                // 重置只清零计数，Total 却永远算着这些按压。
                Settings.Data.TotalCount -= Settings.Data.Count[s];
                if (Settings.Data.TotalCount < 0) Settings.Data.TotalCount = 0;
                Settings.Data.Count[s] = 0;
                if (keyPressTimes != null && s < keyPressTimes.Length && keyPressTimes[s] != null)
                    keyPressTimes[s].Clear();
                if (lastPerKeyKps != null && s < lastPerKeyKps.Length)
                    lastPerKeyKps[s] = 0;
                if (Keys != null && s < Keys.Length && Keys[s]?.value != null)
                    Keys[s].value.text = "0";
                SaveSettingsFromGui();
            }
            GUILayout.EndHorizontal();
        }

        private static KeyCode GetKeyCodeForIndex(int idx)
        {
            KeyCode[] main = GetKeyCode();
            if (main != null && idx < main.Length) return main[idx];
            KeyCode[] foot = GetFootKeyCode();
            int fi = idx - FootKeyBase;
            if (foot != null && fi >= 0 && fi < foot.Length) return foot[fi];
            return KeyCode.None;
        }

        private void DrawColorSection()
        {
            bool colorsExpanded = DrawFoldoutButton(I18n.Tr("colors"), ColorExpanded != null);
            if (colorsExpanded && ColorExpanded == null) ColorExpanded = new bool[12];
            if (!colorsExpanded) ColorExpanded = null;
            if (ColorExpanded == null) return;

            if (KeyViewer.IsFullKeyboard)
            {
                DrawFullKeyboardColorSection();
                return;
            }
            if (IsCustomLayout)
            {
                // Per-key colors are per node in the editor; global colors still apply to
                // nodes without custom colors. / 每键颜色在编辑器里按节点配置；全局颜色仍
                // 作用于未开自定义配色的节点。
                GUILayout.Label(I18n.Tr("fm_color_hint"));
                DrawColorSettings();
                return;
            }
            bool pk = GUILayout.Toggle(Settings.Data.EnablePerKeyColors, I18n.Tr("per_key_colors"));
            if (pk != Settings.Data.EnablePerKeyColors)
            {
                Settings.Data.EnablePerKeyColors = pk;
                ResetKeyViewer();
                UpdateAllKeyColors();
                SaveSettingsFromGui();
            }
            if (Settings.Data.EnablePerKeyColors)
                DrawPerKeyColorSettings();
            else
                DrawColorSettings();
        }

        // ======================== Full Keyboard (108K) color section ========================
        private void DrawFullKeyboardColorSection()
        {
            GUILayout.BeginVertical("box");

            bool unified = GUILayout.Toggle(Settings.Data.EnableFullKeyboardUnifiedColor, I18n.Tr("fk_unified_color"));
            if (unified != Settings.Data.EnableFullKeyboardUnifiedColor)
            {
                Settings.Data.EnableFullKeyboardUnifiedColor = unified;
                UpdateAllKeyColors();
                SaveSettingsFromGui();
            }

            string[] names = {
                I18n.Tr("color_bg"), I18n.Tr("color_bg_clicked"), I18n.Tr("color_outline"),
                I18n.Tr("color_outline_clicked"), I18n.Tr("color_text"), I18n.Tr("color_text_clicked")
            };
            Color[] defaults = {
                Background, BackgroundClicked, Outline, OutlineClicked, Text, TextClicked
            };
            for (int i = 0; i < 6; i++)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(15);
                Color cur = GetFullKeyboardColor(i);
                Color newColor = DrawColorPicker(names[i], cur, defaults[i]);
                if (newColor != cur)
                {
                    SetFullKeyboardColor(i, newColor);
                    UpdateAllKeyColors();
                    SaveSettingsFromGui();
                }
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(5);
            if (Settings.Data.FullKeyboardShowKpsTotal)
            {
                // KPS/Total colors only apply when unified color is OFF; with unified ON they follow
                // the FullKeyboard color set above, so hide these controls to avoid dead settings.
                // 仅当统一色关闭时 KPS/Total 单独配色才生效;开启时它们跟随上面的 FullKeyboard 配色,故隐藏入口避免无效设置。
                if (!Settings.Data.EnableFullKeyboardUnifiedColor)
                {
                    DrawKpsTotalColors(MaxKeySlots, I18n.Tr("kps_colors"), ref kpsColorType);
                    GUILayout.Space(3);
                    DrawKpsTotalColors(MaxKeySlots + 1, I18n.Tr("total_colors"), ref totalColorType);
                }
            }

            GUILayout.EndVertical();
        }

        private static Color GetFullKeyboardColor(int dim) => dim switch
        {
            0 => Settings.Data.FullKeyboardBackground,
            1 => Settings.Data.FullKeyboardBackgroundClicked,
            2 => Settings.Data.FullKeyboardOutline,
            3 => Settings.Data.FullKeyboardOutlineClicked,
            4 => Settings.Data.FullKeyboardText,
            _ => Settings.Data.FullKeyboardTextClicked
        };

        private static void SetFullKeyboardColor(int dim, Color c)
        {
            switch (dim)
            {
                case 0: Settings.Data.FullKeyboardBackground = c; break;
                case 1: Settings.Data.FullKeyboardBackgroundClicked = c; break;
                case 2: Settings.Data.FullKeyboardOutline = c; break;
                case 3: Settings.Data.FullKeyboardOutlineClicked = c; break;
                case 4: Settings.Data.FullKeyboardText = c; break;
                default: Settings.Data.FullKeyboardTextClicked = c; break;
            }
        }
    }
}