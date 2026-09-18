// Key detection and rebinding logic / 按键检测和重新绑定逻辑
// Handles listening for new key presses during rebinding and converting KeyCodes to display strings / 处理重绑定期间监听新按键，以及将 KeyCode 转换为显示字符串

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;

namespace JipperKeyViewer.KeyViewer
{
    /// <summary>Pre-allocated char buffer for TMP text updates (no per-call ToString allocation) / 预分配的字符缓冲区，用于 TMP 文本更新（无每调用 ToString 分配）</summary>
    internal static class NumBuffer
    {
        private static readonly char[] Buffer = new char[32];

        /// <summary>Write integer into Buffer, return char segment via out params / 将整数写入 Buffer，通过 out 参数返回字符片段</summary>
        public static void Format(int count, bool thousands, out char[] buf, out int offset, out int length)
        {
            int pos = Buffer.Length;
            if (count == 0) { Buffer[--pos] = '0'; buf = Buffer; offset = pos; length = Buffer.Length - pos; return; }
            long val = count;
            if (val < 0) val = -val;
            int seg = 0;
            while (val > 0)
            {
                if (thousands && seg == 3) { Buffer[--pos] = ','; seg = 0; }
                Buffer[--pos] = (char)('0' + val % 10);
                val /= 10;
                seg++;
            }
            if (count < 0) Buffer[--pos] = '-';
            buf = Buffer; offset = pos; length = Buffer.Length - pos;
        }
    }

    /// <summary>
    /// Input processing: key rebinding and display string conversion / 输入处理：按键重绑定和显示字符串转换
    /// </summary>
    public partial class KeyViewer : MonoBehaviour
    {
        /// <summary>
        /// Frame number of the last DrawSettingsWindow pass (IMGUI draws several passes per frame).
        /// Rebind capture only runs while the settings window is being drawn — closing the window
        /// (or hiding the UMM panel) used to leave the armed state alive, silently eating the next
        /// keypress (often the very hotkey that closed the window) as a rebind.
        /// 最近一次 DrawSettingsWindow 的帧号（IMGUI 每帧多个 pass）。改键捕获仅在设置窗口
        /// 正在绘制时进行——旧代码关窗(或隐藏 UMM 面板)后武装态仍存活,下一次按键(往往正是
        /// 关窗用的热键)被静默吞成改绑。
        /// </summary>
        internal int lastSettingsGuiFrame = -1000;

        /// <summary>
        /// Listen for a key press when the user is rebinding a key / 当用户正在重绑定时监听按键按下
        /// Waits for any key down, then assigns it to the SelectedKey / 等待任意键按下，然后分配给 SelectedKey
        /// </summary>
        private void ProcessKeySelection()
        {
            if (SelectedKey == -1 || changeState == 1 || !Application.isFocused) return;
            // Loader-reported visibility is authoritative: a closed window (hotkey toggle, UMM
            // panel hidden) must disarm immediately — the frame heuristic below only catches it
            // a couple of frames later. / loader 上报的可见性是权威判定:窗口关闭(热键切换、UMM
            // 面板隐藏)必须立即解除武装——下方帧号启发式要晚数帧才追上。
            var loader = Loader.Instance;
            if (loader != null && !loader.IsSettingsWindowVisible) { SelectedKey = -1; changeState = 0; return; }
            // Only capture while the settings window is alive (drawn within the last couple of
            // frames) — see lastSettingsGuiFrame above. / 仅当设置窗口存活(最近数帧内绘制过)
            // 才捕获——见上方 lastSettingsGuiFrame 说明。
            if (Time.frameCount - lastSettingsGuiFrame > 2) { SelectedKey = -1; changeState = 0; return; }
            if (!Input.anyKeyDown) return;

            foreach (KeyCode keyCode in AllKeyCodes)
            {
                if (!Input.GetKeyDown(keyCode)) continue;
                // ESC cancels the armed rebind instead of binding Escape into the slot.
                // ESC 取消武装中的改键,而不是把 Escape 绑进槽位。
                if (keyCode == KeyCode.Escape)
                {
                    SelectedKey = -1;
                    changeState = 0;
                    return;
                }
                // Mouse buttons never bind: a click on ANY settings control in the same frame used
                // to be captured as the binding (Update runs before OnGUI, so the click's Mouse0
                // GetKeyDown is already true when the armed Update pass sees it).
                // 鼠标键不参与绑定:旧代码会把同帧点击任意设置控件捕获为绑定(Update 先于
                // OnGUI,点击的 Mouse0 GetKeyDown 在武装态的 Update 里已经为 true)。
                if (keyCode >= KeyCode.Mouse0 && keyCode <= KeyCode.Mouse6) continue;
                // The loader's settings hotkey never binds: the core's Update can run BEFORE the
                // loader consumes the hotkey in its own update, so pressing it to close the window
                // while armed would otherwise bind the hotkey into the slot in that same frame.
                // 加载器的设置热键不参与绑定:核心 Update 可能先于加载器消费热键运行,武装中
                // 按它关窗会在同帧把热键绑进槽位。
                if (loader != null && keyCode == loader.SettingsHotkey) continue;
                SetupKey(keyCode);
                return;
            }
        }

        /// <summary>
        /// Assign a key code to the selected slot and update the display / 将按键代码分配给选中的槽位并更新显示
        /// </summary>
        private void SetupKey(KeyCode keyCode)
        {
            if (IsFullKeyboard) { SelectedKey = -1; changeState = 0; return; }
            if (SelectedKey < 0) return; // -1 means no key is being rebound; never index arrays with it / -1 表示没有正在重绑定的键，禁止用作索引
            // Absorb the binding press itself: the key is still physically held, so the polling
            // loop in this same frame would otherwise register a fresh press edge (count+1, KPS,
            // rain, animation) for the key the user just chose. Syncing isPressed to the physical
            // state swallows that edge; the eventual release edge is a harmless no-drop release.
            // 吞掉绑定按键本身:该键物理上仍被按住,否则同帧的轮询会为用户刚选的键注册一次
            // 全新按下边沿(计数+1、KPS、雨滴、动画)。把 isPressed 同步到物理状态即吞掉该边沿;
            // 之后的释放边沿是一次无害的空释放。
            if (changeState == 2)
            {
                KeyCode[] ghostKeyCodes = GetGhostKeyCode();
                if (SelectedKey < ghostKeyCodes.Length)
                {
                    ghostKeyCodes[SelectedKey] = keyCode;
                    if (SelectedKey < ghostKeyStates.Length)
                        // Sync to the physical state (same swallow as main keys below): the bound
                        // key is still held, so the ghost poller must not fire a fresh press edge
                        // (one extra ghost-rain release) for the key the user just chose.
                        // 同步到物理状态(与下方主键同款吞边沿):绑定键仍被按住,鬼键轮询器
                        // 不能为用户刚选的键触发全新按下边沿(多放一次鬼雨)。
                        ghostKeyStates[SelectedKey] = KeySource.GetKey(keyCode);
                }
                SelectedKey = -1;
                changeState = 0;
                SaveSettings();
                return;
            }
            KeyCode[] keyCodes = GetKeyCode();
            KeyCode[] footKeyCodes = GetFootKeyCode();
            string[] keyTexts = GetKeyText();
            if (SelectedKey < FootKeyBase)
            {
                if (SelectedKey < keyCodes.Length)
                    keyCodes[SelectedKey] = keyCode;
            }
            else if (footKeyCodes != null && SelectedKey - FootKeyBase < footKeyCodes.Length)
            {
                footKeyCodes[SelectedKey - FootKeyBase] = keyCode;
            }
            else
            {
                SelectedKey = -1;
                changeState = 0;
                return;
            }
            if (Keys != null && SelectedKey < Keys.Length && Keys[SelectedKey] != null)
            {
                string displayText;
                if (SelectedKey < FootKeyBase && SelectedKey < keyTexts.Length && !string.IsNullOrEmpty(keyTexts[SelectedKey]))
                    displayText = keyTexts[SelectedKey];
                else if (SelectedKey >= FootKeyBase)
                {
                    string[] footTexts = GetFootKeyText();
                    int footIndex = SelectedKey - FootKeyBase;
                    displayText = footTexts != null && footIndex < footTexts.Length && !string.IsNullOrEmpty(footTexts[footIndex])
                        ? footTexts[footIndex] : KeyToString(keyCode);
                }
                else
                    displayText = KeyToString(keyCode);
                Keys[SelectedKey].text.text = displayText;
                // Absorb the binding press itself (see comment at the top of this method).
                // 吞掉绑定按键本身的按下边沿(见方法开头注释)。
                Keys[SelectedKey].isPressed = KeySource.GetKey(keyCode);
            }
            SelectedKey = -1;
            changeState = 0;
            SaveSettings();
        }

        static readonly Dictionary<KeyCode, string> KeyDisplayNames = new Dictionary<KeyCode, string>();

        /// <summary>
        /// Convert a Unity KeyCode to a short display-friendly string / 将 Unity KeyCode 转换为简短友好的显示字符串
        /// Uses a pre-built dictionary to avoid per-call allocations / 使用预建字典避免每次调用产生分配
        /// </summary>
        public static string KeyToString(KeyCode keyCode)
        {
            if (KeyDisplayNames.Count == 0 && AllKeyCodes != null)
                BuildKeyDisplayNames();
            return KeyDisplayNames.TryGetValue(keyCode, out var name) ? name : keyCode.ToString();
        }

        static void BuildKeyDisplayNames()
        {
            foreach (KeyCode k in AllKeyCodes)
            {
                string s = StripKeyCodePrefix(k.ToString());
                s = ReplaceKeyCodeSuffix(s);
                s = MapKeyCodeSymbol(s);
                KeyDisplayNames[k] = s;
            }
        }

        static string StripKeyCodePrefix(string s)
        {
            if (s.StartsWith("Alpha")) return s.Substring(5);
            if (s.StartsWith("Keypad")) return s.Substring(6);
            if (s.StartsWith("Left")) return 'L' + s.Substring(4);
            if (s.StartsWith("Right")) return 'R' + s.Substring(5);
            if (s.StartsWith("Mouse")) return "M" + s.Substring(5);
            return s;
        }

        static string ReplaceKeyCodeSuffix(string s)
        {
            if (s.EndsWith("Shift")) return s.Substring(0, s.Length - 5) + "\u21E7";
            if (s.EndsWith("Control")) return s.Substring(0, s.Length - 7) + "Ctrl";
            return s;
        }

        static string MapKeyCodeSymbol(string s)
        {
            return s switch
            {
                "Plus" => "+",
                "Minus" => "-",
                "Multiply" => "*",
                "Divide" => "/",
                "Enter" => "\u21B5",
                "Equals" => "=",
                "Period" => ".",
                "Return" => "\u21B5",
                "None" => " ",
                "Tab" => "\u21E5",
                "Backslash" => "\\",
                "Backspace" => "Back",
                "Slash" => "/",
                "LBracket" => "[",
                "RBracket" => "]",
                "Semicolon" => ";",
                "Comma" => ",",
                "Quote" => "'",
                "UpArrow" => "\u2191",
                "DownArrow" => "\u2193",
                "LArrow" => "\u2190",
                "RArrow" => "\u2192",
                "Space" => "\u2423",
                "BackQuote" => "`",
                "PageDown" => "Pg\u2193",
                "PageUp" => "Pg\u2191",
                "CapsLock" => "\u21EA",
                "Insert" => "Ins",
                _ => s
            };
        }

        /// <summary>Calculate IMGUI text field width based on content length / 根据内容长度计算 IMGUI 文本框宽度</summary>
        private static GUILayoutOption FloatFieldWidth(string text) => GUILayout.Width(Mathf.Max(30, text.Length * 9));

        // ======================== Input Processing (hot path) / 输入处理（热路径） ========================

        /// <summary>
        /// Check each main key and foot key for state changes (press/release) every frame / 每帧检查每个主键和脚键的状态变化（按下/释放）
        /// </summary>
        private void ProcessMainAndFootKeysInUpdate(long elapsedMilliseconds)
        {
            ProfileData d = Settings.Data;
            if (cachedKeyStyle != d.KeyViewerStyle)
            {
                cachedMainKeys = GetKeyCode();
                cachedGhostKeys = GetGhostKeyCode();
                cachedKeyStyle = d.KeyViewerStyle;
                ghostKeyStates = new bool[cachedGhostKeys.Length];
            }
            else if (cachedGhostKeys == null)
            {
                cachedGhostKeys = GetGhostKeyCode();
                ghostKeyStates = new bool[cachedGhostKeys.Length];
            }
            if (cachedFootStyle != d.FootKeyViewerStyle)
            {
                cachedFootKeys = GetFootKeyCode();
                cachedFootStyle = d.FootKeyViewerStyle;
            }
            ProcessKeyGroup(cachedMainKeys, 0, elapsedMilliseconds);
            // Full keyboard uses indices 0-104 for main keys; foot-key indices (24+) overlap real keys,
            // so never process the foot group here or it corrupts main-key press states.
            // 全键盘主键占用 0-104，脚键索引(24+)与真实键重叠，绝不能再处理脚键组，否则污染主键状态。
            if (!IsFullKeyboard && cachedFootKeys != null)
                ProcessKeyGroup(cachedFootKeys, FootKeyBase, elapsedMilliseconds);
        }

        /// <summary>
        /// Process a group of keys for input state changes / 处理一组按键的输入状态变化
        /// Local-caches Settings references for hot-path performance / 局部缓存 Settings 引用以优化热路径性能
        /// </summary>
        private void ProcessKeyGroup(KeyCode[] keyCodes, int baseIndex, long elapsedMs)
        {
            ProfileData d = Settings.Data;
            int[] countArr = d.Count;
            bool rainEnabled = d.EnableRainEffect;
            bool enablePressAnim = d.EnablePressAnimation;
            float pressAnimScale = d.PressAnimationScale;
            bool enablePerKeyKps = d.EnablePerKeyKps;
            bool enableCountFmt = d.EnableCountFormatting;
            for (int i = 0; i < keyCodes.Length; i++)
            {
                int idx = baseIndex + i;
                if (idx >= Keys.Length) continue;
                Key key = Keys[idx];
                if (key == null) continue;
                bool current = KeySource.GetKey(keyCodes[i]);
                if (current != key.isPressed)
                {
                    UpdateKeyColors(idx, current, d);
                    key.isPressed = current;
                    if (enablePressAnim)
                    {
                        float target = current ? pressAnimScale : 1f;
                        if (key.currentAnim != null)
                            StopCoroutine(key.currentAnim);
                        key.currentAnim = StartCoroutine(AnimateKeyScale(key, target, PressAnimDurationFor(key)));
                    }
                    if (current)
                    {
                        // d.Count and keyPressTimes are sized for MaxKeySlots (40). Full-keyboard keys (idx 0-104)
                        // have no per-key count text, so only the shared TotalCount accumulates for them. / 全键盘主键无每键计数，仅累加 TotalCount
                        if (idx < countArr.Length)
                        {
                            countArr[idx]++;
                            if (key.value != null && !enablePerKeyKps)
                            {
                                NumBuffer.Format(countArr[idx], enableCountFmt, out var buf, out int off, out int len);
                                key.value.SetText(buf, off, len);
                            }
                        }
                        d.TotalCount++;
                        PressTimes.Enqueue(elapsedMs);
                        // Gate per-key queues on the display toggle: the cleanup loop below is
                        // also gated, so with the feature off the queues used to grow forever
                        // (8 bytes/press/key leaked for the whole session — the default config).
                        // 每键队列受显示开关门控:下方清理循环同样被门控,功能关闭时队列
                        // 此前只进不出(每键每按压泄漏 8 字节,整场累积——默认配置即中招)。
                        if (enablePerKeyKps && keyPressTimes != null && idx < keyPressTimes.Length)
                        {
                            keyPressTimes[idx].Enqueue(elapsedMs);
                            _hasKeyPressActivity = true;
                        }
                        if (rainEnabled) rainSystem.TriggerRainEffect(idx, key);
                    }
                    else
                    {
                        if (rainEnabled) rainSystem.ReleaseRainEffect(idx, key);
                    }
                }
            }
        }

        /// <summary>
        /// Calculate KPS by removing presses older than 1 second / 通过移除超过 1 秒的按下记录计算 KPS
        /// </summary>
        private void ProcessKpsInUpdate(long elapsedMilliseconds)
        {
            if (PressTimes == null) return;
            while (PressTimes.Count > 0 && elapsedMilliseconds - PressTimes.Peek() > 1000)
                PressTimes.Dequeue();
            int currentKps = PressTimes.Count;
            if (IsCustomLayout)
            {
                // Per-GROUP values: a panel in group G shows the KPS/Total of G's keys only;
                // ungrouped panels keep the global semantics. The "already shown" cache is per
                // PANEL (per Key), not per group: one group may own several panels, and a
                // group-keyed cache let the first panel suppress every sibling's refresh — a
                // sibling kept whatever UpdateKeyText stamped on it, which is the GLOBAL total.
                // 按组取值：G 组的面板只显示 G 组按键的 KPS/Total；未分组面板保持全局语义。
                // 「已显示」缓存按「面板」（按键）而非按「组」：一个组可以有多个面板，按组缓存
                // 会让第一个面板压掉同组其它面板的刷新——后者会一直留着 UpdateKeyText 盖上的
                // 全局总数。
                foreach (Key k in StatKeys(1))
                {
                    int kps = CustomGroupKps(k.CustomNode.GroupId ?? "", elapsedMilliseconds);
                    if (k.LastShownStatKps == kps) continue;
                    k.LastShownStatKps = kps;
                    SetKpsTotalDisplay(k, "KPS", FormatStatNumber(kps));
                }
                foreach (Key k in StatKeys(2))
                {
                    long total = CustomGroupTotal(k.CustomNode.GroupId ?? "");
                    if (k.LastShownTotal == total) continue;
                    k.LastShownTotal = total;
                    SetKpsTotalDisplay(k, "Total", FormatStatNumber(total));
                }
                lastKps = currentKps;   // keep the global cache in step for the forced refresh /
                                        // 同步全局缓存，供强制刷新路径使用
                lastTotal = Settings.Data.TotalCount;
                return;
            }
            if (lastKps != currentKps)
            {
                lastKps = currentKps;
                NumBuffer.Format(currentKps, Settings.Data.EnableCountFormatting, out var buf, out int off, out int len);
                foreach (Key k in SinglePanel(Kps))
                {
                    if (KpsTotalCenteredApplies())
                        SetKpsTotalDisplay(k, "KPS", new string(buf, off, len));
                    else if (!KpsTotalIsSlim() && Settings.Data.HideKpsTotalLabel)
                        k.text.SetText(buf, off, len);
                    else if (k.value != null)
                        k.value.SetText(buf, off, len);
                }
            }
            // The Total display update lives HERE (not in ProcessMainAndFootKeys) so both input
            // paths — fixed layouts AND the FreeMake custom path, which never runs the former —
            // refresh it every frame. / Total 计数显示更新放在此处（而非 ProcessMainAndFootKeys），
            // 使固定布局与 FreeMake 自定义两条输入路径（后者不经过前者）都会逐帧刷新。
            if (lastTotal != Settings.Data.TotalCount)
            {
                lastTotal = Settings.Data.TotalCount;
                NumBuffer.Format(lastTotal, Settings.Data.EnableCountFormatting, out var buf2, out int off2, out int len2);
                foreach (Key k in SinglePanel(Total))
                {
                    if (KpsTotalCenteredApplies())
                        SetKpsTotalDisplay(k, "Total", new string(buf2, off2, len2));
                    else if (!KpsTotalIsSlim() && Settings.Data.HideKpsTotalLabel)
                        k.text.SetText(buf2, off2, len2);
                    else if (k.value != null)
                        k.value.SetText(buf2, off2, len2);
                }
            }
        }

        /// <summary>Format a stat number with the count-formatting setting (KPS is int-safe). /
        /// 按计数格式化设置格式化面板数值（KPS 用 int 安全路径）。</summary>
        private string FormatStatNumber(long value)
        {
            NumBuffer.Format((int)System.Math.Min(value, int.MaxValue), Settings.Data.EnableCountFormatting,
                out var buf, out int off, out int len);
            return new string(buf, off, len);
        }

        /// <summary>Wrap a single (possibly null) panel ref as a list — keeps the fixed-layout
        /// path uniform with the multi-panel iteration. / 把单个（可能为空的）面板引用包装成
        /// 列表——让固定布局路径与多面板遍历统一。</summary>
        private static List<Key> SinglePanel(Key k)
        {
            var list = new List<Key>(1);
            if (k != null) list.Add(k);
            return list;
        }

        /// <summary>
        /// Per-key KPS: clean timestamps older than 1s and update display / 每键 KPS：清理超过 1 秒的时间戳并更新显示
        /// </summary>
        private void ProcessPerKeyKpsInUpdate(long elapsedMilliseconds)
        {
            if (!_hasKeyPressActivity) return;
            if (IsCustomLayout) return; // custom nodes drain their own KPS logs / 自定义节点消费自己的 KPS 队列
            if (!Settings.Data.EnablePerKeyKps || keyPressTimes == null || Keys == null) return;
            for (int i = 0; i < Keys.Length && i < keyPressTimes.Length; i++)
            {
                var q = keyPressTimes[i];
                while (q.Count > 0 && elapsedMilliseconds - q.Peek() > 1000)
                    q.Dequeue();
                int kps = q.Count;
                if (lastPerKeyKps != null && i < lastPerKeyKps.Length && lastPerKeyKps[i] != kps)
                {
                    lastPerKeyKps[i] = kps;
                    if (Keys[i] != null && Keys[i].value != null)
                    {
                        NumBuffer.Format(kps, Settings.Data.EnableCountFormatting, out var buf, out int off, out int len);
                        Keys[i].value.SetText(buf, off, len);
                    }
                }
            }
            bool anyActive = false;
            foreach (var q in keyPressTimes)
                if (q.Count > 0) { anyActive = true; break; }
            _hasKeyPressActivity = anyActive;
        }

        /// <summary>
        /// Process ghost key inputs — secondary keys that only trigger rain, no display/count / 处理鬼键输入 — 仅触发雨滴的副按键，无显示/计数
        /// ghostKeyStates is guaranteed non-null and same length as cachedGhostKeys (initialized in ProcessMainAndFootKeysInUpdate before this runs) / ghostKeyStates 保证非空且长度与 cachedGhostKeys 相同（在此方法之前由 ProcessMainAndFootKeysInUpdate 初始化）
        /// </summary>
        private void ProcessGhostKeysInUpdate()
        {
            if (cachedGhostKeys == null) return;
            ProfileData d = Settings.Data;
            bool rainEnabled = d.EnableRainEffect;
            bool ghostRainEnabled = d.EnableGhostRain;

            KeyCode[] ghosts = cachedGhostKeys;
            for (int i = 0; i < ghosts.Length; i++)
            {
                if (ghosts[i] == KeyCode.None) continue;

                bool current = KeySource.GetKey(ghosts[i]);
                if (current == ghostKeyStates[i]) continue;
                ghostKeyStates[i] = current;
                // Keep tracking state even while the rain gates are off, so re-enabling them mid-hold
                // doesn't desync — the old early-return left a held ghost key "pressed" and its next
                // real press produced no rain until an extra release/press cycle.
                // 即便雨滴开关关闭也继续跟踪状态，避免按住途中重新开启后失步——旧的提前返回会让
                // 按住的鬼键停留在“已按下”，下一次真实按键不触发雨滴，直到多松/按一次才恢复。
                if (!rainEnabled || !ghostRainEnabled) continue;
                if (current)
                    rainSystem.TriggerGhostRain(i, Keys[i]);
                else
                    rainSystem.ReleaseGhostRain(i, Keys[i]);
            }
        }

        /// <summary>
        /// Reset every key's press-animation scale to 1 and stop running animations. Used when the
        /// press animation is turned off mid-press: the release transition is gated on the toggle,
        /// so without this the key would stay stuck at PressAnimationScale (visuals wrapper, shape
        /// slot, rain scale) until the next rebuild or a press with the toggle re-enabled.
        /// 将所有按键的按压缩放重置为 1 并停止进行中的动画。按压动画在按住途中被关闭时使用：
        /// 释放过渡受开关门控，不重置的话按键会卡在缩小状态（文本包裹层、形状槽位、雨滴缩放），
        /// 直到下次重建或重新开启动画后再按一次。
        /// </summary>
        private void ResetAllPressScales()
        {
            if (Keys == null) return;
            for (int i = 0; i < Keys.Length; i++)
            {
                Key key = Keys[i];
                if (key == null) continue;
                if (key.currentAnim != null)
                {
                    StopCoroutine(key.currentAnim);
                    key.currentAnim = null;
                }
                if (key.visuals != null)
                    key.visuals.localScale = Vector3.one;
                if (key.CustomImageRect != null)
                    key.CustomImageRect.localScale = Vector3.one;
                if (key.shapeSlot >= 0)
                {
                    if (keyShapeLayer != null) keyShapeLayer.SetScale(key.shapeSlot, 1f);
                    if (rainLayer != null) rainLayer.SetKeyScale(key.shapeSlot, 1f);
                }
            }
        }

        /// <summary>
        /// Update key visual colors based on press state / 根据按下状态更新按键视觉颜色
        /// </summary>
        private void UpdateKeyColors(int i, bool pressed, ProfileData d = null)
        {
            if (IsFullKeyboard) {
                var d2 = Settings.Data;
                bool u = d2.EnableFullKeyboardUnifiedColor;
                Key k = Keys[i];
                SetShapeColors(k,
                    pressed ? (u ? d2.FullKeyboardBackgroundClicked : d2.BackgroundClicked) : (u ? d2.FullKeyboardBackground : d2.Background),
                    pressed ? (u ? d2.FullKeyboardOutlineClicked : d2.OutlineClicked) : (u ? d2.FullKeyboardOutline : d2.Outline));
                k.text.color = pressed ? (u ? d2.FullKeyboardTextClicked : d2.TextClicked) : (u ? d2.FullKeyboardText : d2.Text);
                if (k.value != null) k.value.color = k.text.color;
                return;
            }
            if (Keys == null || i >= Keys.Length) return;
            Key key = Keys[i];
            if (key == null) return;
            if (d == null) d = Settings.Data;
            if (d.EnablePerKeyColors && i < MaxKeySlots)
            {
                SetShapeColors(key,
                    pressed ? d.PerKeyBackgroundClicked[i] : d.PerKeyBackground[i],
                    pressed ? d.PerKeyOutlineClicked[i] : d.PerKeyOutline[i]);
                key.text.color = pressed ? d.PerKeyTextClicked[i] : d.PerKeyText[i];
            }
            else
            {
                SetShapeColors(key,
                    pressed ? d.BackgroundClicked : d.Background,
                    pressed ? d.OutlineClicked : d.Outline);
                key.text.color = pressed ? d.TextClicked : d.Text;
            }
            if (key.value != null) key.value.color = key.text.color;
        }

        /// <summary>Press-animation duration for a key, in seconds. Custom-layout nodes may carry
        /// their own easing/duration; every other key follows the global Display-tab values. /
        /// 某按键的按压动画时长（秒）。自定义布局节点可携带自己的缓动/时长；其余按键跟随显示页的
        /// 全局值。</summary>
        private static float PressAnimDurationFor(Key key)
        {
            FmNode node = key != null ? key.CustomNode : null;
            float ms = node != null && node.UseCustomPressEasing ? node.PressAnimDurationMs : Settings.Data.PressAnimationDurationMs;
            // Clamp: a 0 or negative duration would make the while-loop body never run and the key
            // would be left at the previous scale forever. / 钳制：0 或负时长会让 while 循环体一次
            // 都不执行，按键将永远停在上一档缩放。
            return Mathf.Clamp(ms, 10f, 2000f) / 1000f;
        }

        /// <summary>
        /// Smoothly animate key visuals scale (text wrapper + merged shape mesh).
        /// When EnablePressAnimationOnRain is on, the merged rain layer scales that key's drops too;
        /// the key root carries no visuals, so nothing else needs scaling.
        /// 平滑缩放按键：文本包裹层 + 合并形状 mesh。开启「雨滴跟随缩放」时合并雨滴层同步缩放该键
        /// 雨滴；按键根不承载可见物体，无需再缩放其它内容。
        /// </summary>
        private IEnumerator AnimateKeyScale(Key key, float target, float duration)
        {
            bool affectRain = Settings.Data.EnablePressAnimationOnRain;
            Transform animTarget = key.visuals;
            float startS = animTarget.localScale.x;
            int generation = keyShapeLayer != null ? keyShapeLayer.Generation : -1;
            // Read the easing ONCE: re-reading it per frame would make a mid-animation settings edit
            // (or a profile switch) bend the curve halfway through, and the curve's own start point
            // would then no longer match startS. / 只读取一次缓动：逐帧重读会让动画途中的设置修改
            //（或切换配置）把曲线在中途掰弯，且曲线的起点将不再对应 startS。
            string easing = key.CustomNode != null && key.CustomNode.UseCustomPressEasing
                ? key.CustomNode.PressAnimEasing
                : Settings.Data.PressAnimationEasing;
            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (key == null || animTarget == null) yield break;
                // Overlay was rebuilt mid-animation — slots were re-indexed, stop writing to them
                // 动画中途覆盖层被重建——槽位已重新编号，停止写入
                if (keyShapeLayer == null || keyShapeLayer.Generation != generation) yield break;
                elapsed += Time.unscaledDeltaTime;
                float p = Mathf.Min(1f, elapsed / duration);
                // Ease the NORMALIZED progress, then lerp: easing the lerp result instead would move
                // the endpoint (an ease-out would land short of `target`). / 对「归一化进度」做缓动
                // 再插值：若对插值结果做缓动，终点会偏移（ease-out 会停在不到 target 的位置）。
                float s = Mathf.Lerp(startS, target, Util.KvEasing.Ease(easing, p));
                animTarget.localScale = new Vector3(s, s, 1);
                // Image keys scale their RawImage visual alongside the text wrapper. /
                // 图片按键的 RawImage 视觉与文本包裹层同步缩放。
                if (key.CustomImageRect != null)
                    key.CustomImageRect.localScale = new Vector3(s, s, 1f);
                // shapeSlot equals the key index for all real keys; when rain-follow is off, drive
                // the rain scale back to 1 so a mid-hold toggle never leaves it stuck scaled /
                // 真实按键的 shapeSlot 即键索引；雨滴跟随关闭时把雨滴缩放拉回 1，
                // 避免按住途中切换开关导致缩放卡住
                if (key.shapeSlot >= 0) keyShapeLayer.SetScale(key.shapeSlot, s);
                if (rainLayer != null && key.shapeSlot >= 0) rainLayer.SetKeyScale(key.shapeSlot, affectRain ? s : 1f);
                yield return null;
            }
            if (key == null || animTarget == null) yield break;
            if (keyShapeLayer == null || keyShapeLayer.Generation != generation) yield break;
            animTarget.localScale = new Vector3(target, target, 1);
            if (key.CustomImageRect != null)
                key.CustomImageRect.localScale = new Vector3(target, target, 1f);
            if (key.shapeSlot >= 0) keyShapeLayer.SetScale(key.shapeSlot, target);
            if (rainLayer != null && key.shapeSlot >= 0) rainLayer.SetKeyScale(key.shapeSlot, affectRain ? target : 1f);
        }
    }
}
