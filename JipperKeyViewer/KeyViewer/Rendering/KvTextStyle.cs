// Resolved text outline/shadow style for one TMP text / 单个 TMP 文本解析后的描边/阴影样式
// The global settings and a node's per-node overrides collapse into one immutable value here, so
// the material cache key and the applier both work from a single source of truth — no chance of
// the cache and the applied material disagreeing about which flags were on.
// 全局设置与节点级覆盖在此合并为一个不可变值，使材质缓存键与写入方共用同一份事实来源——不会
// 出现缓存与实际材质对开关判断不一致的情况。

using TMPro;
using UnityEngine;

namespace JipperKeyViewer.KeyViewer.Rendering
{
    /// <summary>Which TMP text a style applies to (label vs. press count) / 样式作用于哪个 TMP
    /// 文本（标签或计数）</summary>
    public enum KvTextKind
    {
        KeyLabel,
        Count,
    }

    /// <summary>Immutable resolved outline/shadow style / 不可变的描边/阴影解析结果</summary>
    public struct KvTextStyle
    {
        public bool Outline;
        public Color OutlineColor;
        public float OutlineWidth;
        public bool Shadow;
        public Color ShadowColor;
        public float ShadowOffsetX;
        public float ShadowOffsetY;
        public float ShadowSoftness;

        /// <summary>True when the style needs a dedicated material at all. A fully-off style
        /// resolves to the font's own material, which is what every TMP text used before this
        /// feature existed. / 该样式是否需要专属材质。全关的样式回落到字体自带材质，即本功能
        /// 出现前所有 TMP 文本使用的那个。</summary>
        public bool NeedsMaterial => Outline || Shadow;

        /// <summary>Stable cache key: identical styles share one material, and the key includes
        /// the font so a font switch never reuses another font's material. Colors are quantized
        /// to 8-bit channels (a color picked in the GUI is 8-bit anyway) to keep the key an int
        /// pair instead of a float-hash. / 稳定的缓存键：相同样式共用一个材质，键内含字体，故切换
        /// 字体不会复用别的字体材质。颜色量化为 8 位通道（GUI 取色本就是 8 位），使键保持整数
        /// 而非浮点哈希。</summary>
        public long CacheKey(int fontId)
        {
            long h = 1469598103934665603L;
            void Mix(long v)
            {
                h ^= v;
                h *= 1099511628211L;
            }
            Mix(fontId);
            Mix(Outline ? 1 : 0);
            Mix(Color32(OutlineColor));
            Mix(Bits(OutlineWidth));
            Mix(Shadow ? 1 : 0);
            Mix(Color32(ShadowColor));
            Mix(Bits(ShadowOffsetX));
            Mix(Bits(ShadowOffsetY));
            Mix(Bits(ShadowSoftness));
            return h;
        }

        private static long Bits(float f)
        {
            // NaN would make the key collide with every other NaN style; normalize it to zero —
            // a NaN thickness is degenerate anyway and TMP would render nothing.
            if (float.IsNaN(f)) f = 0f;
            return (long)Mathf.RoundToInt(f * 1000f);
        }

        /// <summary>Node color array → Color with a global fallback (a null or malformed array
        /// means "follow the global"), matching how every other per-node color override reads its
        /// data. / 节点颜色数组 → Color，带全局回退（数组为空或长度不对即「跟随全局」），与其它
        /// 所有节点级配色覆盖的读取方式一致。</summary>
        private static Color ColorOf(float[] arr, Color fallback)
        {
            return arr != null && arr.Length == 4
                ? new Color(arr[0], arr[1], arr[2], arr[3])
                : fallback;
        }

        private static long Color32(Color c)
        {
            Color32 q = c;
            return (q.r << 24) | (q.g << 16) | (q.b << 8) | q.a;
        }

        /// <summary>Resolve the style for one text kind: the node override when the node opted in,
        /// otherwise the global settings. / 解析某一类文本的样式：节点已接管时用节点覆盖，否则用
        /// 全局设置。</summary>
        public static KvTextStyle Resolve(Settings.ProfileData d, Settings.FmNode node, KvTextKind kind)
        {
            KvTextStyle s = new KvTextStyle();
            bool key = kind == KvTextKind.KeyLabel;
            if (d != null && node != null && node.UseCustomTextStyle)
            {
                if (key)
                {
                    s.Outline = node.KeyTextOutlineEnabled;
                    s.OutlineColor = ColorOf(node.KeyTextOutlineColor, d.KeyTextOutlineColor);
                    s.OutlineWidth = node.KeyTextOutlineThickness;
                    s.Shadow = node.KeyTextShadowEnabled;
                    s.ShadowColor = ColorOf(node.KeyTextShadowColor, d.KeyTextShadowColor);
                    s.ShadowOffsetX = node.KeyTextShadowOffsetX;
                    s.ShadowOffsetY = node.KeyTextShadowOffsetY;
                    s.ShadowSoftness = node.KeyTextShadowSoftness;
                }
                else
                {
                    s.Outline = node.CountTextOutlineEnabled;
                    s.OutlineColor = ColorOf(node.CountTextOutlineColor, d.CountTextOutlineColor);
                    s.OutlineWidth = node.CountTextOutlineThickness;
                    s.Shadow = node.CountTextShadowEnabled;
                    s.ShadowColor = ColorOf(node.CountTextShadowColor, d.CountTextShadowColor);
                    s.ShadowOffsetX = node.CountTextShadowOffsetX;
                    s.ShadowOffsetY = node.CountTextShadowOffsetY;
                    s.ShadowSoftness = node.CountTextShadowSoftness;
                }
            }
            else if (d != null)
            {
                if (key)
                {
                    s.Outline = d.EnableKeyTextOutline;
                    s.OutlineColor = d.KeyTextOutlineColor;
                    s.OutlineWidth = d.KeyTextOutlineThickness;
                    s.Shadow = d.EnableKeyTextShadow;
                    s.ShadowColor = d.KeyTextShadowColor;
                    s.ShadowOffsetX = d.KeyTextShadowOffsetX;
                    s.ShadowOffsetY = d.KeyTextShadowOffsetY;
                    s.ShadowSoftness = d.KeyTextShadowSoftness;
                }
                else
                {
                    s.Outline = d.EnableCountTextOutline;
                    s.OutlineColor = d.CountTextOutlineColor;
                    s.OutlineWidth = d.CountTextOutlineThickness;
                    s.Shadow = d.EnableCountTextShadow;
                    s.ShadowColor = d.CountTextShadowColor;
                    s.ShadowOffsetX = d.CountTextShadowOffsetX;
                    s.ShadowOffsetY = d.CountTextShadowOffsetY;
                    s.ShadowSoftness = d.CountTextShadowSoftness;
                }
            }
            // A zero/negative outline width renders nothing while still costing a material, so a
            // width that small collapses the feature instead of drawing an invisible pass. /
            // 零/负描边宽度什么也不画却仍占一个材质，故这么小的宽度直接关掉该功能，而非画一层
            // 看不见的东西。
            if (s.OutlineWidth <= 0f) s.Outline = false;
            return s;
        }

        /// <summary>Write the style onto a TMP text's font material. The caller supplies the
        /// material (see KeyViewer.GetTextStyleMaterial) so identical styles share one instance.
        /// / 把样式写入 TMP 文本的字体材质。材质由调用方提供（见
        /// KeyViewer.GetTextStyleMaterial），相同样式共用一个实例。</summary>
        public void Apply(TMP_Text text, Material material)
        {
            if (text == null) return;
            text.fontMaterial = material;
        }
    }
}