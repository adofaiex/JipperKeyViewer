// Settings GUI: Keys tab content / 设置界面:按键 标签页内容
// Key rebinding, ghost-key rebinding, and per-key custom text editing / 按键重绑定、鬼键重绑定、每键自定义文本编辑

using System;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Rain;
using JipperKeyViewer.KeyViewer.Rendering;
using JipperKeyViewer.KeyViewer.Util;
using JipperKeyViewer.KeyViewer.Editor;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer : MonoBehaviour
    {
        /// <summary>
        /// Draw the key rebinding section / 绘制按键重绑定区域
        /// Shows all keys for the current layout as clickable buttons / 将当前布局的所有按键显示为可点击的按钮
        /// </summary>
        private void DrawKeyChangeSection()
        {
            GUILayout.BeginVertical("box");
            KeyCode[] keyCodes = GetKeyCode();
            // Arming here must DISARM the editor's node capture, the mirror of what the editor does
            // when it arms. The editor window and the settings window can both be open (Melon
            // drives two IMGUI passes), and ProcessKeySelection polls Input.GetKeyDown in Update —
            // BEFORE the editor's OnGUI KeyDown handling — so a single physical press was consumed
            // by BOTH: the fixed-layout slot binding changed and so did the FreeMake node's KeyBind.
            // 每边武装时都必须解除另一边的武装，这与编辑器武装时的处理互为镜像。编辑器窗口与设置
            // 窗口可同时打开（Melon 驱动两个 IMGUI pass），而 ProcessKeySelection 在 Update 里轮询
            // Input.GetKeyDown——**早于**编辑器的 OnGUI KeyDown 处理——于是同一次物理按键被两边
            // 同时消费：固定布局槽位绑定与 FreeMake 节点 KeyBind 一起被改。
            CancelEditorNodeCapture();
            DrawMainKeyRows(I18n.Tr("row1_keys"), I18n.Tr("row2_keys"), I18n.Tr("row3_keys"),
                keyCodes, (i, _) => { SelectedKey = i; changeState = 0; });
            DrawFootKeyRows(I18n.Tr("foot_keys_list"), FootKeyBase,
                (i, _) => { SelectedKey = i; changeState = 0; });
            if (SelectedKey != -1 && changeState == 0)
                GUILayout.Label("<b>" + I18n.Tr("press_new_key") + "</b>");
            GUILayout.EndVertical();
        }

        private void DrawMainKeyRows(string row1Label, string row2Label, string row3Label,
            KeyCode[] keyCodes, Action<int, KeyCode> onKeyClick, Func<int, KeyCode, string> labelFunc = null)
        {
            labelFunc ??= (i, kc) => KeyToString(kc);
            GUILayout.Label(row1Label + ":");
            GUILayout.BeginHorizontal();
            // Row-1 loop guards length like the back-row loops below — truncated arrays (hand-edited
            // profiles) used to throw here every GUI event. EnsureSettingsArrays now rebuilds them,
            // the guard is belt-and-suspenders. / 第一排与下方后排循环同样做长度守卫——截断数组
            // (手改 Profile)此前每个 GUI 事件都在此抛异常。EnsureSettingsArrays 现已重建,此守卫
            // 为双保险。
            for (int i = 0; i < 8 && i < keyCodes.Length; i++)
                if (GUILayout.Button(labelFunc(i, keyCodes[i])))
                    onKeyClick(i, keyCodes[i]);
            GUILayout.EndHorizontal();

            byte[] backSequence = GetBackSequence();
            if (backSequence.Length > 0)
            {
                GUILayout.Label(row2Label + ":");
                GUILayout.BeginHorizontal();
                for (int i = 0; i < backSequence.Length && i < 8; i++)
                {
                    if (backSequence[i] >= keyCodes.Length) continue;
                    if (GUILayout.Button(labelFunc(backSequence[i], keyCodes[backSequence[i]])))
                        onKeyClick(backSequence[i], keyCodes[backSequence[i]]);
                }
                GUILayout.EndHorizontal();
            }

            if (backSequence.Length > 8)
            {
                GUILayout.Label(row3Label + ":");
                GUILayout.BeginHorizontal();
                for (int i = 8; i < backSequence.Length; i++)
                {
                    // In the BODY, like the row-2 loop right above. As a for-loop CONDITION this
                    // test does not skip the element — it TERMINATES the loop, so the first
                    // out-of-range backSequence value made every remaining row-3 key vanish from
                    // the panel. In this tab that means the button which opens the key-capture
                    // prompt no longer exists, which for the user is indistinguishable from the
                    // key being ignored.
                    // 放在**循环体内**，与紧邻上方的第 2 排一致。作为 for 的**条件**时该检查不是
                    // 跳过当前元素，而是**终止整个循环**，故第一个越界的 backSequence 值就让第 3 排
                    // 剩余所有按键从面板上消失。在本标签页这意味着「打开改键捕获」的按钮根本不存在，
                    // 对用户而言与「该键被忽略」无法区分。
                    if (backSequence[i] >= keyCodes.Length) continue;
                    if (GUILayout.Button(labelFunc(backSequence[i], keyCodes[backSequence[i]])))
                        onKeyClick(backSequence[i], keyCodes[backSequence[i]]);
                }
                GUILayout.EndHorizontal();
            }
        }

        private void DrawFootKeyRows(string label, int baseIndex, Action<int, KeyCode> onKeyClick,
            Func<int, KeyCode, string> labelFunc = null)
        {
            labelFunc ??= (i, kc) => KeyToString(kc);
            KeyCode[] footKeyCodes = GetFootKeyCode();
            if (footKeyCodes == null || footKeyCodes.Length == 0) return;
            GUILayout.Label(label + ":");
            if (footKeyCodes.Length <= 8)
            {
                GUILayout.BeginHorizontal();
                for (int i = 0; i < footKeyCodes.Length; i++)
                    if (GUILayout.Button(labelFunc(baseIndex + i, footKeyCodes[i])))
                        onKeyClick(baseIndex + i, footKeyCodes[i]);
                GUILayout.EndHorizontal();
            }
            else
            {
                int remaining = footKeyCodes.Length - 8;
                GUILayout.BeginHorizontal();
                for (int i = 0; i < 8; i++)
                    if (GUILayout.Button(labelFunc(baseIndex + i, footKeyCodes[i])))
                        onKeyClick(baseIndex + i, footKeyCodes[i]);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                for (int s = 0; s < 8 - remaining; s++)
                    GUILayout.FlexibleSpace();
                for (int i = 8; i < footKeyCodes.Length; i++)
                    if (GUILayout.Button(labelFunc(baseIndex + i, footKeyCodes[i])))
                        onKeyClick(baseIndex + i, footKeyCodes[i]);
                for (int s = 0; s < 8 - remaining; s++)
                    GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
            }
        }

        /// <summary>
        /// Draw the ghost key rebinding section / 绘制鬼键重绑定区域
        /// Shows ghost key slots — click unbound to bind, click bound to clear / 显示鬼键槽位 — 点击未绑定的进入绑定,点击已绑定的清除
        /// </summary>
        private void DrawGhostKeyChangeSection()
        {
            GUILayout.BeginVertical("box");
            KeyCode[] ghostKeyCodes = GetGhostKeyCode();

            GUILayout.Label(I18n.Tr("row1_keys") + ":");
            GUILayout.BeginHorizontal();
            // Bounds guards mirror DrawMainKeyRows (row2/row3 below): EnsureSettingsArrays
            // rebuilds wrong-length arrays, but the drawing loops stay defensive anyway.
            // 边界守卫与 DrawMainKeyRows 对齐(见下方 row2/row3):EnsureSettingsArrays 会重建
            // 长度不符的数组,但绘制循环仍保持防御。
            for (int i = 0; i < 8 && i < ghostKeyCodes.Length; i++)
                DrawGhostKeyButton(i, ghostKeyCodes);
            GUILayout.EndHorizontal();

            byte[] backSequence = GetBackSequence();
            if (backSequence.Length > 0)
            {
                GUILayout.Label(I18n.Tr("row2_keys") + ":");
                GUILayout.BeginHorizontal();
                for (int i = 0; i < backSequence.Length && i < 8; i++)
                {
                    if (backSequence[i] >= ghostKeyCodes.Length) continue;
                    DrawGhostKeyButton(backSequence[i], ghostKeyCodes);
                }
                GUILayout.EndHorizontal();
            }

            if (backSequence.Length > 8)
            {
                GUILayout.Label(I18n.Tr("row3_keys") + ":");
                GUILayout.BeginHorizontal();
                for (int i = 8; i < backSequence.Length; i++)
                {
                    // Body, not loop condition — see the identical fix in DrawBackRowButtons.
                    // 放在循环体而非循环条件——见 DrawBackRowButtons 中完全相同的修复。
                    if (backSequence[i] >= ghostKeyCodes.Length) continue;
                    DrawGhostKeyButton(backSequence[i], ghostKeyCodes);
                }
                GUILayout.EndHorizontal();
            }

            if (SelectedKey != -1 && changeState == 2)
                GUILayout.Label("<b>" + I18n.Tr("press_new_key") + "</b>");
            GUILayout.EndVertical();
        }

        private void DrawGhostKeyButton(int i, KeyCode[] ghostKeyCodes)
        {
            bool isBound = ghostKeyCodes[i] != KeyCode.None;
            string label = isBound ? KeyToString(ghostKeyCodes[i]) : "-";
            bool selected = i == SelectedKey && changeState == 2;
            if (GUILayout.Button(selected ? "<b>" + label + "</b>" : label))
            {
                if (isBound)
                {
                    ghostKeyCodes[i] = KeyCode.None;
                    SelectedKey = -1;
                    SaveSettingsFromGui();
                }
                else
                {
                    SelectedKey = i;
                    changeState = 2;
                }
            }
        }

        /// <summary>
        /// Draw the custom text editing section / 绘制自定义文本编辑区域
        /// Allows typing custom labels for each key / 允许为每个按键输入自定义标签
        /// </summary>
        private void DrawTextChangeSection()
        {
            GUILayout.BeginVertical("box");
            KeyCode[] keyCodes = GetKeyCode();
            string[] keyTexts = GetKeyText();
            KeyCode[] footKeyCodes = GetFootKeyCode();
            string[] footKeyTexts = GetFootKeyText();

            DrawMainKeyRows(I18n.Tr("row1_text"), I18n.Tr("row2_text"), I18n.Tr("row3_text"),
                keyCodes, (i, _) => { SelectedKey = i; changeState = 1; },
                (i, kc) => GetKeyTextLabel(keyTexts, keyCodes, i));
            DrawFootKeyRows(I18n.Tr("foot_keys_text"), FootKeyBase,
                (i, _) => { SelectedKey = i; changeState = 1; },
                (i, kc) => GetFootKeyTextLabel(footKeyTexts, footKeyCodes, i - FootKeyBase));

            if (SelectedKey != -1 && changeState == 1)
                DrawTextEditArea(keyTexts, keyCodes, footKeyTexts, footKeyCodes);
            GUILayout.EndVertical();
        }

        private static string GetKeyTextLabel(string[] keyTexts, KeyCode[] keyCodes, int i) =>
            keyTexts != null && i < keyTexts.Length && !string.IsNullOrEmpty(keyTexts[i])
                ? keyTexts[i] : KeyToString(i < keyCodes.Length ? keyCodes[i] : KeyCode.None);

        private static string GetFootKeyTextLabel(string[] footKeyTexts, KeyCode[] footKeyCodes, int fi) =>
            footKeyTexts != null && fi < footKeyTexts.Length && !string.IsNullOrEmpty(footKeyTexts[fi])
                ? footKeyTexts[fi] : KeyToString(fi < footKeyCodes.Length ? footKeyCodes[fi] : KeyCode.None);

        private void DrawTextEditArea(string[] keyTexts, KeyCode[] keyCodes, string[] footKeyTexts, KeyCode[] footKeyCodes)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.Tr("input_text") + ":");
            if (SelectedKey < FootKeyBase)
                DrawMainKeyTextField(keyTexts, keyCodes);
            else
                DrawFootKeyTextField(footKeyTexts, footKeyCodes);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(I18n.Tr("reset")))
            {
                if (SelectedKey < FootKeyBase)
                {
                    keyTexts[SelectedKey] = null;
                    if (Keys != null && SelectedKey < Keys.Length && Keys[SelectedKey] != null)
                        Keys[SelectedKey].text.text = KeyToString(keyCodes[SelectedKey]);
                }
                else
                {
                    int footIndex = SelectedKey - FootKeyBase;
                    footKeyTexts[footIndex] = null;
                    if (Keys != null && SelectedKey < Keys.Length && Keys[SelectedKey] != null)
                        Keys[SelectedKey].text.text = KeyToString(footKeyCodes[footIndex]);
                }
                SelectedKey = -1;
                SaveSettingsFromGui();
            }
            if (GUILayout.Button(I18n.Tr("save_btn")))
            {
                SelectedKey = -1;
                SaveSettingsFromGui();
            }
            GUILayout.EndHorizontal();
        }

        private void DrawMainKeyTextField(string[] keyTexts, KeyCode[] keyCodes)
        {
            // Buffered input (TextInputField): clearing the field must not snap it back to the
            // default key label mid-typing (the stored null maps back to the default), and the
            // on-screen key label must not flash empty for one event. / 缓冲输入（TextInputField）：
            // 清空输入框不能中途就跳回默认键名（存储的 null 会映射回默认值），屏幕上的按键文字
            // 也不能闪一帧空白。
            string fallback = KeyToString(keyCodes[SelectedKey]);
            string current = !string.IsNullOrEmpty(keyTexts[SelectedKey]) ? keyTexts[SelectedKey] : fallback;
            string newText = TextInputField("kte_" + SelectedKey, current, GUILayout.Width(150));
            if (newText == current && keyTexts[SelectedKey] == null) return;
            string stored = string.IsNullOrEmpty(newText) || newText == fallback ? null : newText;
            if (keyTexts[SelectedKey] != stored)
            {
                if (Keys != null && SelectedKey < Keys.Length && Keys[SelectedKey] != null)
                    Keys[SelectedKey].text.text = stored ?? fallback;
                keyTexts[SelectedKey] = stored;
            }
        }

        private void DrawFootKeyTextField(string[] footKeyTexts, KeyCode[] footKeyCodes)
        {
            int footIndex = SelectedKey - FootKeyBase;
            // Same buffering rationale as DrawMainKeyTextField. / 与 DrawMainKeyTextField 同样的缓冲理由。
            string fallback = KeyToString(footKeyCodes[footIndex]);
            string current = footKeyTexts != null && !string.IsNullOrEmpty(footKeyTexts[footIndex])
                ? footKeyTexts[footIndex] : fallback;
            string newText = TextInputField("kfte_" + footIndex, current, GUILayout.Width(150));
            if (newText == current && footKeyTexts[footIndex] == null) return;
            string stored = string.IsNullOrEmpty(newText) || newText == fallback ? null : newText;
            if (footKeyTexts[footIndex] != stored)
            {
                if (Keys != null && SelectedKey < Keys.Length && Keys[SelectedKey] != null)
                    Keys[SelectedKey].text.text = stored ?? fallback;
                footKeyTexts[footIndex] = stored;
            }
        }

        private void DrawBindingSection()
        {
            if (KeyViewer.IsFullKeyboard)
            {
                // Key rebinding and per-key text genuinely don't apply here (SetupKey ignores
                // full-keyboard mode) — but the KPS/Total custom labels DO: the full keyboard's own
                // KPS/Total boxes read the same KpsLabel/TotalLabel settings. Since this feature
                // landed (47511c7) the early return hid its editor from 108K users even though the
                // labels themselves render there.
                // 改键与每键文本在全键盘下确实不适用（SetupKey 忽略全键盘模式）——但 KPS/Total
                // 自定义标签生效：全键盘自己的 KPS/Total 框读取同一组 KpsLabel/TotalLabel 设置。
                // 该功能落地（47511c7）起，这个提前 return 就把编辑入口对 108 键用户藏了起来，
                // 而标签本身却在正常渲染。
                kpsTotalTextExpanded = DrawFoldoutButton(I18n.Tr("kps_total_text"), kpsTotalTextExpanded);
                if (kpsTotalTextExpanded)
                    DrawKpsTotalTextSection();
                GUILayout.Space(5);
                GUILayout.Label("<i>" + I18n.Tr("full_kb_keys_na") + "</i>");
                return;
            }
            if (IsCustomLayout)
            {
                // Bindings and texts are per node in the FreeMake editor. / 绑定与文本在
                // FreeMake 编辑器里按节点编辑。
                GUILayout.Label("<i>" + I18n.Tr("fm_binding_hint") + "</i>");
                return;
            }
            KeyChangeExpanded = DrawFoldoutButton(I18n.Tr("key_change"), KeyChangeExpanded);
            if (KeyChangeExpanded)
                DrawKeyChangeSection();

            if (Settings.Data.EnableRainEffect && Settings.Data.EnableGhostRain)
            {
                GhostRainChangeExpanded = DrawFoldoutButton(I18n.Tr("ghost_rain"), GhostRainChangeExpanded);
                if (GhostRainChangeExpanded)
                    DrawGhostKeyChangeSection();
            }

            TextChangeExpanded = DrawFoldoutButton(I18n.Tr("text_change"), TextChangeExpanded);
            if (TextChangeExpanded)
                DrawTextChangeSection();

            // KPS / Total custom text / KPS / Total 自定义文本
            kpsTotalTextExpanded = DrawFoldoutButton(I18n.Tr("kps_total_text"), kpsTotalTextExpanded);
            if (kpsTotalTextExpanded)
                DrawKpsTotalTextSection();
        }

        private bool kpsTotalTextExpanded;

        private void DrawKpsTotalTextSection()
        {
            GUILayout.BeginVertical("box");

            // ---- KPS 标签 ----
            GUILayout.BeginHorizontal();
            GUILayout.Label("KPS " + I18n.Tr("input_text") + ":");
            string newKpsLabel = GUILayout.TextField(Settings.Data.KpsLabel, GUILayout.Width(100));
            if (newKpsLabel != Settings.Data.KpsLabel)
            {
                Settings.Data.KpsLabel = newKpsLabel;
                RefreshKpsTotalLabels();
                SaveSettingsFromGui();
            }
            if (GUILayout.Button(I18n.Tr("reset"), GUILayout.Width(50)))
            {
                Settings.Data.KpsLabel = "KPS";
                RefreshKpsTotalLabels();
                SaveSettingsFromGui();
            }
            GUILayout.EndHorizontal();

            // ---- Total 标签 ----
            GUILayout.BeginHorizontal();
            GUILayout.Label("Total " + I18n.Tr("input_text") + ":");
            string newTotalLabel = GUILayout.TextField(Settings.Data.TotalLabel, GUILayout.Width(100));
            if (newTotalLabel != Settings.Data.TotalLabel)
            {
                Settings.Data.TotalLabel = newTotalLabel;
                RefreshKpsTotalLabels();
                SaveSettingsFromGui();
            }
            if (GUILayout.Button(I18n.Tr("reset"), GUILayout.Width(50)))
            {
                Settings.Data.TotalLabel = "Total";
                RefreshKpsTotalLabels();
                SaveSettingsFromGui();
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }
    }
}