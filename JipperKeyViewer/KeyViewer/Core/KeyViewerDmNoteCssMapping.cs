// DmNote CSS theme → FreeMake node mapping / DmNote CSS 主题 → FreeMake 节点映射
//
// WHY JSON-WINS: a DmNote preset fills its own visual fields whenever it has them, and falls back
// to the stylesheet only where it is null. Applying the CSS unconditionally would let a stale
// embedded copy override the JSON the author actually saved last.
//
// 为何「JSON 优先」：DmNote 预设只要自身有视觉字段就用自己的，仅在字段为 null 时才回退到样式表。
// 无条件套用 CSS 会让一份过期的内嵌副本压过作者最后真正保存的 JSON。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;
using JipperKeyViewer.KeyViewer.Settings;

namespace JipperKeyViewer.KeyViewer
{
    internal static class DmNoteCssMapping
    {
        /// <summary>font-weight at or above this is bold in CSS. / 该值及以上视为 CSS 粗体。</summary>
        private const int BoldWeight = 600;

        /// <summary>Apply a parsed theme to one node. `jsonSupplied` decides, per property, whether
        /// the JSON already answered — a null there means the CSS is the fallback source of truth.
        /// 把解析出的主题应用到单个节点。`jsonSupplied` 逐属性说明 JSON 是否已给出答案。
        /// </summary>
        public static void Apply(FmNode node, DmNoteCssTheme theme, string className, bool useCustomCss)
        {
            if (node == null || theme == null || !useCustomCss || !theme.HasAny) return;

            DmNoteCssScope idle = theme.Idle;
            DmNoteCssScope active = theme.Active;
            // A class-scoped block (`.clear`, `.fail`, …) only describes nodes carrying that
            // className, and overrides the shared block for them. / 类作用域块只描述带该 className
            // 的节点，并对它们覆盖共享块。
            if (!string.IsNullOrWhiteSpace(className))
            {
                foreach (string token in className.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (theme.ClassIdle.TryGetValue(token, out DmNoteCssScope ci)) Overlay(idle, ci);
                    if (theme.ClassActive.TryGetValue(token, out DmNoteCssScope ca)) Overlay(active, ca);
                }
            }

            ApplyShape(node, idle, active);
            ApplyText(node, idle, active);
            ApplyCounter(node, theme, className);
        }

        /// <summary>Layer a class scope over a shared one without mutating the shared object —
        /// two nodes can share the same shared scope while getting different class overrides.
        /// 把类作用域叠加到共享作用域之上，且**不修改**共享对象——两个节点可共用同一共享作用域
        /// 却得到不同的类覆盖。
        /// </summary>
        private static void Overlay(DmNoteCssScope shared, DmNoteCssScope over)
        {
            foreach (KeyValuePair<string, string> kv in over.Decls)
            {
                if (!shared.Decls.ContainsKey(kv.Key)) shared.Decls[kv.Key] = kv.Value;
            }
        }

        private static void ApplyShape(FmNode node, DmNoteCssScope idle, DmNoteCssScope active)
        {
            float[] bg = DmNoteCss.ParseColor(idle.Get("--key-bg"));
            float[] bgActive = DmNoteCss.ParseColor(active.Get("--key-bg"));
            if (bg != null || bgActive != null)
            {
                node.UseCustomColor = true;
                if (bg != null) node.Bg = bg;
                if (bgActive != null) node.BgPressed = bgActive;
            }

            float[] border = ParseBorder(idle.Get("--key-border"), out float borderWidth);
            float[] borderActive = ParseBorder(active.Get("--key-border"), out float borderWidthActive);
            float width = borderWidth > 0f ? borderWidth : borderWidthActive;
            if (width > 0f)
            {
                node.BorderThickness = Mathf.Clamp(width, 0f, 20f);
                if (border != null) node.Outline = border;
                if (borderActive != null) node.OutlinePressed = borderActive;
            }

            float radius = DmNoteCss.ParsePx(idle.Get("--key-radius"), float.NaN);
            if (!float.IsNaN(radius) && radius > 0f) node.CornerRadius = Mathf.Clamp(radius, 0f, 100f);

            // box-shadow is the theme's key glow. Only the drop shadow counts: the `inset` layers
            // are inner highlights, which the shared-soft-sprite glow cannot express, and drawing
            // them as an outer glow would visibly halo the key.
            // box-shadow 即主题的按键光效。只有投影算数：`inset` 层是内高光，共享柔光贴图无法表达，
            // 画成外发光会让按键明显起晕。
            if (DmNoteCss.TryParseDropShadow(idle.Get("box-shadow"), out float[] glow, out float blur, out float yOff)
                && glow != null)
            {
                node.UseGlow = true;
                node.GlowFollowBody = false;
                node.GlowColor = glow;
                node.GlowOpacity = Mathf.Clamp01(glow[3]);
                if (!float.IsNaN(blur)) node.GlowSize = Mathf.Clamp(blur, 0f, 200f);
            }
            if (DmNoteCss.TryParseDropShadow(active.Get("box-shadow"), out float[] glowP, out float blurP, out float yP)
                && glowP != null)
            {
                node.GlowPressedOverride = true;
                node.GlowFollowBodyPressed = false;
                node.GlowColorPressed = glowP;
                node.GlowOpacityPressed = Mathf.Clamp01(glowP[3]);
                if (!float.IsNaN(blurP)) node.GlowSizePressed = Mathf.Clamp(blurP, 0f, 200f);
            }

            // `scale(1)` is the CSS identity, not a request to animate: enabling the override would
            // claim a press animation that does nothing. / `scale(1)` 是 CSS 恒等变换而非动画请求：
            // 打开该覆盖等于声称有个什么都不做的按压动画。
            float scale = DmNoteCss.ParseScale(idle.Get("transform"));
            if (!float.IsNaN(scale) && scale > 0.01f && scale < 4f && Math.Abs(scale - 1f) > 0.001f)
            {
                node.UseCustomPressAnim = true;
                node.PressAnimScale = scale;
            }
        }

        private static void ApplyText(FmNode node, DmNoteCssScope idle, DmNoteCssScope active)
        {
            float[] text = DmNoteCss.ParseColor(idle.Get("--key-text-color"));
            float[] textActive = DmNoteCss.ParseColor(active.Get("--key-text-color"));
            if (text != null || textActive != null)
            {
                if (text != null)
                {
                    node.TextColor = text;
                    // Same guard as the JSON path: a 0 alpha in TextOpacity renders a key with no
                    // label at all. / 与 JSON 路径同一道防线：TextOpacity 为 0 会渲染出没有文字的按键。
                    node.TextOpacity = Mathf.Clamp01(text[3]) <= 0f ? 1f : text[3];
                }
                if (textActive != null) node.TextColorPressed = textActive;
            }

            float size = DmNoteCss.ParsePx(idle.Get("font-size"), float.NaN);
            if (!float.IsNaN(size) && size > 0f) node.FontSize = Mathf.Clamp(size, 1f, 400f);

            int weight = ParseWeight(idle.Get("font-weight"));
            if (weight >= BoldWeight)
            {
                node.UseCustomTextStyle = true;
                node.FontStyleFlags |= 1;   // TMP FontStyle.Bold
            }

            // `color: transparent` in a class block is how the themes hide the label and paint a
            // ::before logo instead. Mapping it to a 0 opacity is the honest translation — the
            // glyph is not renderable here (see the icon note in DmNoteCss).
            string color = idle.Get("color");
            if (color != null)
            {
                float[] c = DmNoteCss.ParseColor(color);
                if (c != null && c[3] <= 0f) node.TextOpacity = 0f;
            }
        }

        private static void ApplyCounter(FmNode node, DmNoteCssTheme theme, string className)
        {
            DmNoteCssScope idle = theme.CounterIdle;
            DmNoteCssScope active = theme.CounterActive;
            if (idle.Decls.Count == 0 && active.Decls.Count == 0) return;

            // The emphasis variant is an author override for a specific counter state, so it wins.
            // 强调变体是作者针对特定计状态的覆盖，故优先。
            bool emphasised = !string.IsNullOrWhiteSpace(className)
                && (className.IndexOf("highlight", StringComparison.OrdinalIgnoreCase) >= 0
                 || className.IndexOf("emphasis", StringComparison.OrdinalIgnoreCase) >= 0);
            if (emphasised)
            {
                if (theme.CounterEmphasisIdle.Decls.Count > 0) Overlay(idle, theme.CounterEmphasisIdle);
                if (theme.CounterEmphasisActive.Decls.Count > 0) Overlay(active, theme.CounterEmphasisActive);
            }

            float[] fill = DmNoteCss.ParseColor(idle.Get("color") ?? idle.Get("--counter-color"));
            float[] fillActive = DmNoteCss.ParseColor(active.Get("color") ?? active.Get("--counter-color"));
            if (fill != null || fillActive != null)
            {
                node.UseCustomCountTextColor = true;
                if (fill != null)
                {
                    node.CountTextColor = fill;
                    node.CountTextOpacity = Mathf.Clamp01(fill[3]) <= 0f ? 1f : fill[3];
                }
                if (fillActive != null) node.CountTextColorPressed = fillActive;
            }

            float size = DmNoteCss.ParsePx(idle.Get("font-size"), float.NaN);
            if (!float.IsNaN(size) && size > 0f) node.CountFontSize = Mathf.Clamp(size, 1f, 400f);

            int weight = ParseWeight(idle.Get("font-weight"));
            if (weight >= BoldWeight)
            {
                node.UseCustomCountFontStyle = true;
                node.CountFontStyleFlags |= 1;
            }

            // -webkit-text-stroke: <width> <colour> is the themes' counter outline. / 计数描边。
            float[] stroke = ParseTextStroke(idle.Get("-webkit-text-stroke"), out float strokeWidth);
            float[] strokeActive = ParseTextStroke(active.Get("-webkit-text-stroke"), out float strokeWidthActive);
            float w = strokeWidth > 0f ? strokeWidth : strokeWidthActive;
            if (w > 0f && (stroke != null || strokeActive != null))
            {
                node.CountTextOutlineEnabled = true;
                node.CountTextOutlineThickness = Mathf.Clamp01(w / 8f);   // mod unit is 0..1
                if (stroke != null) node.CountTextOutlineColor = stroke;
                if (strokeActive != null) node.CountTextOutlineColor = strokeActive;
            }
        }

        /// <summary>`none`, or `&lt;width&gt; [style] &lt;color&gt;`. / `none` 或「宽度 [样式] 颜色」。</summary>
        private static float[] ParseBorder(string value, out float width)
        {
            width = 0f;
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim().ToLowerInvariant();
            if (v == "none" || v == "0" || v == "0px") return null;
            foreach (string token in v.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.EndsWith("px", StringComparison.Ordinal)
                    && float.TryParse(token.Substring(0, token.Length - 2), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float w))
                {
                    width = w;
                    break;
                }
            }
            // The colour is whatever token is not a width and not a style keyword.
            // 颜色即非宽度、非样式关键字的那个 token。
            foreach (string token in v.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token == "solid" || token == "dashed" || token == "dotted" || token == "none") continue;
                if (token.EndsWith("px", StringComparison.Ordinal)
                    || token == "thin" || token == "medium" || token == "thick") continue;
                float[] c = DmNoteCss.ParseColor(token);
                if (c != null) return c;
            }
            return null;
        }

        private static float[] ParseTextStroke(string value, out float width)
        {
            width = 0f;
            if (string.IsNullOrWhiteSpace(value)) return null;
            foreach (string token in value.Trim().Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.EndsWith("px", StringComparison.Ordinal)
                    && float.TryParse(token.Substring(0, token.Length - 2), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out float w))
                {
                    if (width <= 0f) width = w;
                    continue;
                }
                float[] c = DmNoteCss.ParseColor(token);
                if (c != null && width > 0f) return c;
            }
            return null;
        }

        private static int ParseWeight(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return -1;
            if (value.Trim().Equals("bold", StringComparison.OrdinalIgnoreCase)) return BoldWeight;
            if (value.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase)) return 400;
            if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int w)) return w;
            return -1;
        }

        /// <summary>Write the stylesheet's ::before glyphs next to the preset and report that a PNG
        /// conversion is needed, because `ImageConversion.LoadImage` decodes PNG and JPEG only —
        /// an SVG data URI cannot be handed to it. SVG 已提取到预设旁边，并提示需要转成 PNG。
        /// </summary>
        public static List<string> ExtractIcons(DmNoteCssTheme theme, string presetPath, string imagesDirectory)
        {
            var notes = new List<string>();
            if (theme == null || theme.IconSvg.Count == 0) return notes;
            if (string.IsNullOrEmpty(presetPath) || string.IsNullOrEmpty(imagesDirectory)) return notes;
            string dir = Path.Combine(imagesDirectory, "dmnote-icons");
            int written = 0;
            foreach (KeyValuePair<string, string> kv in theme.IconSvg)
            {
                try
                {
                    string payload = DmNoteCss.ExtractDataUri(kv.Value, out string ext);
                    if (string.IsNullOrEmpty(payload)) continue;
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, kv.Key + "." + ext), payload);
                    written++;
                }
                catch (Exception) { /* one bad glyph must not fail the import / 单个图标失败不影响导入 */ }
            }
            if (written > 0)
                notes.Add("dmnote_css_icons_svg:" + written);
            return notes;
        }
    }
}
