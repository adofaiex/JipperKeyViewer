// Static left-to-right glyph gradients for key labels and counts.
// This is a light, non-animated counterpart to Quartz's CSS glyph-gradient path: colors are
// applied only when the text or gradient settings change, never once per character per frame.
// 静态左右字形渐变：Quartz CSS 路径的轻量非动画版本，仅在文字或渐变设置变化时更新，
// 不逐帧逐字符重算。
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using JipperKeyViewer.KeyViewer.Settings;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer
    {
        private sealed class TextGradientState
        {
            public string Text;
            public Color Left;
            public Color Right;
        }

        private readonly Dictionary<TMP_Text, TextGradientState> textGradientStates =
            new Dictionary<TMP_Text, TextGradientState>();

        private bool HasTextGradientSettings()
        {
            if (textGradientStates.Count > 0) return true;
            ProfileData d = Settings.Data;
            if (d.EnableKeyTextGradient || d.EnableCountTextGradient) return true;
            if (!IsCustomLayout || d.CustomNodes == null) return false;
            for (int i = 0; i < d.CustomNodes.Count; i++)
            {
                FmNode node = d.CustomNodes[i];
                if (node != null && (node.UseTextGradient || node.UseCountTextGradient)) return true;
            }
            return false;
        }

        private void TickTextGradients()
        {
            if (Keys == null || !HasTextGradientSettings()) return;
            // Drop entries whose TMP_Text was destroyed (foot-key reset, node deletion). Keeping
            // them both pins a managed reference to a dead component and keeps HasTextGradientSettings
            // true forever, so Tick would walk every key every frame with nothing to draw. / 清理已
            // 销毁文本的缓存项：否则既会一直持有托管引用，又会让 HasTextGradientSettings 永远为真，
            // 导致每帧空转遍历全部按键。
            if (textGradientStates.Count > 0)
            {
                List<TMP_Text> dead = null;
                foreach (KeyValuePair<TMP_Text, TextGradientState> kv in textGradientStates)
                    if (kv.Key == null)
                    {
                        (dead ?? (dead = new List<TMP_Text>())).Add(kv.Key);
                    }
                if (dead != null)
                    foreach (TMP_Text t in dead) textGradientStates.Remove(t);
            }
            for (int i = 0; i < Keys.Length; i++)
                ApplyTextGradientToKey(Keys[i]);
            // KPS/Total are separate roots in fixed layouts and may not be present in Keys.
            ApplyTextGradientToKey(Kps);
            ApplyTextGradientToKey(Total);
        }

        private void ApplyTextGradientToKey(Key key)
        {
            if (key == null) return;
            bool pressed = key.isPressed;
            ResolveTextGradient(key, false, pressed, out bool labelOn, out Color labelLeft, out Color labelRight);
            ResolveTextGradient(key, true, pressed, out bool countOn, out Color countLeft, out Color countRight);
            ApplyTextGradient(key.text, labelOn, labelLeft, labelRight, key, false);
            ApplyTextGradient(key.value, countOn, countLeft, countRight, key, true);
        }

        private void ApplyTextGradient(TMP_Text text, bool enabled, Color left, Color right, Key key, bool count)
        {
            if (text == null) return;
            if (!enabled)
            {
                if (textGradientStates.Remove(text))
                {
                    Color restored = ResolveSolidTextColor(key, count);
                    if (IsCustomLayout && key?.CustomNode != null)
                        restored.a *= count ? key.CustomNode.CountTextOpacity : key.CustomNode.TextOpacity;
                    text.color = restored;
                }
                return;
            }

            string value = text.text ?? string.Empty;
            // Hand-edited / third-party profiles can carry NaN colors; Color32 conversion turns
            // those into 0 (solid black). Fall back to white instead of writing garbage vertices.
            // 手改或第三方配置可能带 NaN 颜色，转成 Color32 会变成纯黑；回退白色而不是写入坏顶点色。
            if (IsInvalidGradientColor(left)) left = Color.white;
            if (IsInvalidGradientColor(right)) right = Color.white;
            // The press path writes the SOLID text color unconditionally (ApplyCustomKeyColors /
            // UpdateKeyColors), and TMP rebuilds the mesh from m_fontColor at the end of the frame.
            // Force the gradient's white base back BEFORE the cache early-return: otherwise the first
            // press silently overwrites every vertex color and the label loses its gradient for the
            // rest of the session (the label text never changes again, so nothing ever re-applies it).
            // 按压路径会无条件写实色并在帧末重建 mesh；必须在缓存早退之前把渐变基色改回白色，
            // 否则第一次按压就会把标签渐变永久抹掉。
            if (text.color != Color.white) text.color = Color.white;
            if (textGradientStates.TryGetValue(text, out TextGradientState state)
                && state.Text == value && state.Left == left && state.Right == right)
                return;

            // Vertex colors are multiplied by TMP_Text.color. Keep the base color white while a
            // gradient is active; when disabled, ResolveSolidTextColor restores the user's color.
            // ignoreActiveState: a hidden label/count (HideLabel / CountShowWhilePressed) would make
            // ForceMeshUpdate skip the rebuild, after which the text would come back plain white.
            // / 必须忽略激活状态：隐藏的文字若跳过重建，重新显示时会是纯白。
            // (This TMP version returns void, so success is inferred from the mesh info below.)
            text.ForceMeshUpdate(true);
            TMP_TextInfo info = text.textInfo;
            if (info == null || info.characterCount <= 0)
            {
                // Nothing to tint (empty or not yet parsed). Remember the state only when the text
                // really is empty; otherwise drop it so a later tick retries the real build.
                if (string.IsNullOrEmpty(value)) textGradientStates[text] =
                    new TextGradientState { Text = value, Left = left, Right = right };
                else textGradientStates.Remove(text);
                return;
            }

            int charCount = info.characterCount;
            for (int i = 0; i < charCount; i++)
            {
                ref TMP_CharacterInfo character = ref info.characterInfo[i];
                if (!character.isVisible) continue;
                int material = character.materialReferenceIndex;
                if (material < 0 || material >= info.meshInfo.Length)
                    continue;
                Color32[] colors = info.meshInfo[material].colors32;
                if (colors == null) continue;
                float t = charCount > 1 ? (float)i / (charCount - 1) : 0f;
                Color32 color = Color.Lerp(left, right, t);
                int vertex = character.vertexIndex;
                if (vertex < 0 || vertex + 3 >= colors.Length) continue;
                colors[vertex] = color;
                colors[vertex + 1] = color;
                colors[vertex + 2] = color;
                colors[vertex + 3] = color;
            }
            text.UpdateVertexData(TMP_VertexDataUpdateFlags.Colors32);
            textGradientStates[text] = new TextGradientState { Text = value, Left = left, Right = right };
        }

        private static bool IsInvalidGradientColor(Color c) =>
            float.IsNaN(c.r) || float.IsNaN(c.g) || float.IsNaN(c.b) || float.IsNaN(c.a)
            || float.IsInfinity(c.r) || float.IsInfinity(c.g) || float.IsInfinity(c.b) || float.IsInfinity(c.a);

        private void ResolveTextGradient(Key key, bool count, bool pressed, out bool enabled, out Color left, out Color right)
        {
            ProfileData d = Settings.Data;
            FmNode node = key?.CustomNode;
            if (IsCustomLayout && node != null)
            {
                bool usePressed = pressed && (count ? node.UsePressedCountTextGradient : node.UsePressedTextGradient);
                enabled = count ? node.UseCountTextGradient : node.UseTextGradient;
                float[] leftArray = count
                    ? (usePressed ? node.CountTextGradientLeftPressed : node.CountTextGradientLeft)
                    : (usePressed ? node.TextGradientLeftPressed : node.TextGradientLeft);
                float[] rightArray = count
                    ? (usePressed ? node.CountTextGradientRightPressed : node.CountTextGradientRight)
                    : (usePressed ? node.TextGradientRightPressed : node.TextGradientRight);
                Color fallback = ResolveSolidTextColor(key, count);
                left = NodeColor(leftArray, fallback);
                right = NodeColor(rightArray, fallback);
                float opacity = count ? node.CountTextOpacity : node.TextOpacity;
                left.a *= opacity;
                right.a *= opacity;
                return;
            }
            bool globalPressed = pressed && (count ? d.CountTextGradientPressedOverride : d.KeyTextGradientPressedOverride);
            enabled = count ? d.EnableCountTextGradient : d.EnableKeyTextGradient;
            left = globalPressed
                ? (count ? d.CountTextGradientLeftPressed : d.KeyTextGradientLeftPressed)
                : (count ? d.CountTextGradientLeft : d.KeyTextGradientLeft);
            right = globalPressed
                ? (count ? d.CountTextGradientRightPressed : d.KeyTextGradientRightPressed)
                : (count ? d.CountTextGradientRight : d.KeyTextGradientRight);
        }

        private Color ResolveSolidTextColor(Key key, bool count)
        {
            if (key == null) return Color.white;
            ProfileData d = Settings.Data;
            FmNode node = key.CustomNode;
            if (IsCustomLayout && node != null)
            {
                Color fallback = node.NodeType == 1
                    ? (count ? d.KpsText : d.KpsText)
                    : node.NodeType == 2
                        ? (count ? d.TotalText : d.TotalText)
                        : (count ? d.Text : d.Text);
                if (count && node.UseCustomCountTextColor)
                {
                    if (node.CountTextColor != null) fallback = NodeColor(node.CountTextColor, fallback);
                    if (key.isPressed && node.CountTextColorPressed != null)
                        fallback = NodeColor(node.CountTextColorPressed, fallback);
                }
                else
                {
                    if (node.UseCustomColor && node.TextColor != null)
                        fallback = NodeColor(node.TextColor, fallback);
                    if (key.isPressed && node.TextColorPressed != null)
                        fallback = NodeColor(node.TextColorPressed, fallback);
                }
                return fallback;
            }

            int index = key.shapeSlot;
            if (d.EnablePerKeyColors && d.PerKeyText != null && d.PerKeyTextClicked != null
                && index >= 0 && index < d.PerKeyText.Length && index < d.PerKeyTextClicked.Length)
                return key.isPressed ? d.PerKeyTextClicked[index] : d.PerKeyText[index];
            if (IsFullKeyboard)
            {
                bool unified = d.EnableFullKeyboardUnifiedColor;
                if (key == Kps) return d.KpsText;
                if (key == Total) return d.TotalText;
                return key.isPressed
                    ? (unified ? d.FullKeyboardTextClicked : d.TextClicked)
                    : (unified ? d.FullKeyboardText : d.Text);
            }
            if (key == Kps) return d.KpsText;
            if (key == Total) return d.TotalText;
            return key.isPressed ? d.TextClicked : d.Text;
        }

        private void ClearTextGradientStates()
        {
            textGradientStates.Clear();
        }
    }
}
