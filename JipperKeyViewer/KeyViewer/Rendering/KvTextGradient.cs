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
            // Cached per document. This runs EVERY frame, and with no gradient anywhere — the
            // factory default — it used to walk all CustomNodes just to return false, which is
            // pure O(nodes) cost on the default path (a 2048-node document: 2048 iterations per
            // frame, forever). The answer only changes when the node list does.
            // 按文档缓存。该判断**每帧**运行，而在完全没有渐变时（出厂默认）此前都要遍历全部
            // CustomNodes 只为返回 false——纯粹的 O(节点数) 开销落在默认路径上（2048 节点的文档
            // 即每帧 2048 次迭代，且永不停歇）。该答案只在节点表变化时才可能改变。
            if (customGradientCacheCount == d.CustomNodes.Count)
                return customGradientCacheResult;
            bool found = false;
            for (int i = 0; i < d.CustomNodes.Count; i++)
            {
                FmNode node = d.CustomNodes[i];
                if (node != null && (node.UseTextGradient || node.UseCountTextGradient)) { found = true; break; }
            }
            customGradientCacheCount = d.CustomNodes.Count;
            customGradientCacheResult = found;
            return found;
        }

        /// <summary>Document-length stamp + answer for the per-frame scan above. Cleared on every
        // overlay rebuild so an edit that keeps the node COUNT (toggling a gradient on an existing
        // node) is still picked up. / 上面的逐帧扫描所用的「文档长度戳 + 答案」。每次覆盖层重建都
        // 清空，使**节点数不变**的编辑（在已有节点上切换渐变）仍能被看到。</summary>
        private int customGradientCacheCount = -1;
        private bool customGradientCacheResult;

        /// <summary>Forget the cached scan; call whenever the node list is rebuilt or edited. /
        /// 忘记缓存的扫描结果；节点表重建或被编辑时调用。</summary>
        internal void InvalidateGradientScanCache() => customGradientCacheCount = -1;

        private void TickTextGradients()
        {
            if (Keys == null) return;
            if (!HasTextGradientSettings())
            {
                // No gradient anywhere, but states may still be recorded — the FreeMake editor's
                // in-place refresh path deliberately does NOT rebuild the overlay, so turning the
                // LAST gradient off left every affected label stuck on the white base colour that
                // was forced while it was active. Nothing else writes the solid colour back: the
                // press path only runs on a key edge, so the labels stayed white until the user
                // happened to press something. Restore them here so every path that removes the
                // last gradient (editor toggle, node delete, profile switch) is covered, not just
                // the one that happens to rebuild.
                // 全局已无渐变，但状态可能仍有记录——FreeMake 编辑器的就地刷新路径**刻意**不重建
                // 覆盖层，于是关掉**最后一个**渐变会把受影响的标签永久留在「渐变生效期间强制」的
                // 白色基色上。没有别处会写回实色：按压路径只在按键边沿跑，故标签会一直白到用户
                // 碰巧按了某个键。在此处恢复，使「移除最后一个渐变」的所有路径（编辑器开关、
                // 删除节点、切换配置）都被覆盖，而不只是碰巧会重建的那一条。
                if (textGradientStates.Count == 0) return;
                textGradientStates.Clear();
                for (int i = 0; i < Keys.Length; i++) RestoreSolidTextColor(Keys[i]);
                RestoreSolidTextColor(Kps);
                RestoreSolidTextColor(Total);
                return;
            }
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

        /// <summary>Put a key's label and count back on their solid colours, undoing the white base
        /// that a gradient forces. Shared by the "no gradients left" sweep above and the
        /// per-text restore when a single gradient is switched off.
        /// 把某个按键的标签与计数恢复到实色，抵消渐变强制写入的白色基色。「全局无渐变」的清扫与
        /// 单个渐变关闭时的逐文本恢复共用。
        /// </summary>
        private void RestoreSolidTextColor(Key key)
        {
            if (key == null) return;
            ApplySolidTextColor(key, key.text, false);
            ApplySolidTextColor(key, key.value, true);
        }

        private void ApplySolidTextColor(Key key, TMP_Text text, bool count)
        {
            if (text == null) return;
            Color restored = ResolveSolidTextColor(key, count);
            if (IsCustomLayout && key?.CustomNode != null)
                restored.a *= global::JipperKeyViewer.KeyViewer.KeyViewer.EffectiveTextOpacity(key.CustomNode, count);
            text.color = restored;
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
                if (textGradientStates.Remove(text)) ApplySolidTextColor(key, text, count);
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
            // Force the gradient's white base back BEFORE the cache early-return — otherwise the
            // label keeps rendering the pressed solid colour and the gradient never comes back.
            //
            // That white write is ITSELF the dirty trigger, though, and this is why the cache used
            // to be wrong in the other direction: TMP_Text.color's setter sets m_havePropertiesChanged
            // and calls SetVerticesDirty(), which registers a PreRender rebuild, and
            // GenerateTextMesh() unconditionally repaints every vertex colour from m_fontColor32.
            // So a press scheduled a repaint back to solid, we wrote white, the cache below saw
            // unchanged Text/Left/Right and returned — and PreRender then repainted the whole label
            // white. The gradient was destroyed by the very line meant to protect it, and nothing
            // re-applied it because the label's text never changes again. The same happens when a
            // hidden label is re-activated: SetActive(true) -> OnEnable -> SetAllDirty() registers
            // the rebuild, and the colour is already white so the cache hits and returns.
            //
            // Hence the extra condition: the cache is only trusted while the mesh still actually
            // carries the tint we wrote. TMP repaints meshInfo[].colors32 in place, so reading the
            // first visible character's colour detects the repaint for the cost of one array read.
            // 按压路径会无条件写实色并在帧末重建 mesh；必须在缓存早退之前把渐变基色改回白色，
            // 否则标签会一直渲染按下实色、渐变再也回不来。
            //
            // 但这行白色写入**本身就是**标脏的触发器，这也正是旧缓存错在另一侧的原因：
            // TMP_Text.color 的 setter 置 m_havePropertiesChanged 并调 SetVerticesDirty()，
            // 注册一次 PreRender 重建，而 GenerateTextMesh() 会无条件用 m_fontColor32 重绘每个
            // 顶点色。于是按压安排了一次回刷，按压路径写实色，本行写白色，下面的缓存看到
            // Text/Left/Right 都没变便 return——随后 PreRender 把整条标签重绘成纯白。渐变正是被
            // 这行本该保护它的代码毁掉的，而且因为标签文字再也不变，没有任何东西会重新应用它。
            // 隐藏标签重新激活时同理：SetActive(true) → OnEnable → SetAllDirty() 注册重建，
            // 而颜色已经是白色，缓存命中即 return。
            //
            // 故增加一个条件：只在 mesh 仍**确实**带着我们写入的着色时才信任缓存。TMP 就地重绘
            // meshInfo[].colors32，故读第一个可见字符的颜色即能以一次数组读取检测到重绘。
            bool baseColorChanged = text.color != Color.white;
            if (baseColorChanged) text.color = Color.white;
            if (!baseColorChanged
                && textGradientStates.TryGetValue(text, out TextGradientState state)
                && state.Text == value && state.Left == left && state.Right == right
                && GradientStillApplied(text, left))
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

        /// <summary>Is the tint we last wrote still present in TMP's mesh? A press, a font change,
        /// or re-activating a hidden label all schedule a PreRender rebuild, and
        /// GenerateTextMesh() repaints every vertex colour from the (now white) base colour — so
        /// "the inputs did not change" is NOT evidence that the rendered result did not change.
        /// Read the first visible character's first vertex and compare it with what t=0 should be.
        /// One array read; called only on a cache hit.
        /// 我们上次写入的着色是否还在 TMP 的 mesh 里？按压、换字体、重新激活隐藏标签都会安排一次
        /// PreRender 重建，而 GenerateTextMesh() 会用（此时为白色的）基色重绘每个顶点色——故
        /// 「输入没变」**不能**证明「渲染结果没变」。读第一个可见字符的第一个顶点，与 t=0 应有
        /// 的值比较。一次数组读取；仅在缓存命中时调用。
        /// </summary>
        private static bool GradientStillApplied(TMP_Text text, Color left)
        {
            TMP_TextInfo info = text.textInfo;
            if (info == null || info.characterCount <= 0) return true; // nothing to tint yet
            int charCount = info.characterCount;
            for (int i = 0; i < charCount; i++)
            {
                ref TMP_CharacterInfo character = ref info.characterInfo[i];
                if (!character.isVisible) continue;
                int material = character.materialReferenceIndex;
                if (material < 0 || material >= info.meshInfo.Length) continue;
                Color32[] colors = info.meshInfo[material].colors32;
                int vertex = character.vertexIndex;
                if (colors == null || vertex < 0 || vertex >= colors.Length) continue;
                float t = charCount > 1 ? (float)i / (charCount - 1) : 0f;
                // Color32 has no == operator in this Unity version, so compare components.
                // 该版本 Unity 的 Color32 没有 == 运算符，故逐分量比较。
                Color32 want = Color.Lerp(left, left, t);
                Color32 got = colors[vertex];
                return got.r == want.r && got.g == want.g && got.b == want.b && got.a == want.a;
            }
            return true; // no visible glyph to check
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
                float opacity = global::JipperKeyViewer.KeyViewer.KeyViewer.EffectiveTextOpacity(node, count);
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
                    // The pressed colour must sit INSIDE the UseCustomColor gate, exactly like the idle
                    // one above it and like both count-colour reads in the branch above. It did not:
                    // unticking "custom colours" hides the whole panel but never clears the stored
                    // arrays, so a node still holding a stale TextColorPressed painted its held state
                    // in that stale node colour instead of the global TextClicked — a control taking
                    // effect precisely when the user turned it off. Undo back to a snapshot taken
                    // before the tick reaches the same state.
                    // 按下色必须放在 UseCustomColor 门**内**，与上面的常态色、以及上面那个分支里的两处
                    // 计数色读取完全一致。此前不在：取消勾选「自定义颜色」会隐藏整块面板，却**不会**
                    // 清掉已存的数组，故仍持有旧 TextColorPressed 的节点在按住时会用那个旧节点色而
                    // 非全局 TextClicked 上色——恰好在用户把它关掉的时候生效。撤销回到勾选前的快照
                    // 也会落到同一状态。
                    if (node.UseCustomColor && key.isPressed && node.TextColorPressed != null)
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
            // Every caller is an overlay rebuild / font change, which is exactly when the cached
            // "does this document use text gradients at all" scan must be redone. / 每个调用方都是
            // 覆盖层重建/字体变更，正是必须重算"本文档是否用到文字渐变"缓存的时刻。
            InvalidateGradientScanCache();
        }
    }
}
