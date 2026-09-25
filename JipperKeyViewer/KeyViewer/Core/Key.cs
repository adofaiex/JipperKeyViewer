// Key MonoBehaviour: logical key / 按键 MonoBehaviour：逻辑按键
// Box geometry lives in the merged KeyShapeLayer; text lives under a per-key wrapper in the text
// canvas; rain drops live in the merged RainLayer reading key.rainList. This component stays on
// the key root, marking the key's position and holding state.
// 按键框几何在合并的 KeyShapeLayer 中；文本在文本画布的每键包裹层下；雨滴在合并 RainLayer 中读取
// key.rainList。本组件保留在按键根节点上，标记按键位置并持有状态。

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Rain;

namespace JipperKeyViewer.KeyViewer
{
    /// <summary>
    /// Represents a single on-screen key / 表示一个屏幕上的按键
    /// Composed of a text label, count text, a shape slot in the merged layer, and an optional rain container / 由文本标签、计数文本、合并图层中的形状槽位和可选的雨滴容器组成
    /// </summary>
    public class Key : MonoBehaviour
    {
        /// <summary>Key label text (e.g. "Tab", "A") / 按键标签文本（如 "Tab"、"A"）</summary>
        public TextMeshProUGUI text;
        /// <summary>Press count text / 按键计数文本</summary>
        public TextMeshProUGUI value;
        /// <summary>Slot index in the merged KeyShapeLayer (-1 = none) / 合并 KeyShapeLayer 中的槽位索引（-1 = 无）</summary>
        public int shapeSlot = -1;
        /// <summary>Key box size (width, height) for layer/text positioning / 按键框尺寸（宽，高），用于图层与文本定位</summary>
        public Vector2 keySize;
        /// <summary>Rain color index (0=row1, 1=row2, 3=row3) / 雨滴颜色索引（0=第1排，1=第2排，3=第3排）</summary>
        public byte color;
        /// <summary>Pre-computed rain color for this key / 预先计算的该键雨滴颜色</summary>
        public Color rainColor = Color.white;
        /// <summary>Top end of the custom rain gradient (Custom nodes) / 自定义雨滴渐变的顶端颜色</summary>
        public Color rainColorTop = Color.white;
        /// <summary>Counter bounce state  / 计数器弹跳状态（计数器弹跳动画）</summary>
        public bool Bouncing;
        public float BounceStart;
        public Vector2 BounceBasePos;
        /// <summary>Active rain drops list / 活跃中的雨滴列表</summary>
        public List<RawRain> rainList = new List<RawRain>();
        /// <summary>Whether this key is currently pressed / 当前是否被按下</summary>
        public bool isPressed;
        /// <summary>Running press-animation coroutine (null if none) / 运行中的按键动画协程（无则为 null）</summary>
        public Coroutine currentAnim;
        /// <summary>Text wrapper in the text canvas — center pivot over the key box, scaled on press / 文本画布中的文本包裹层，轴心在按键框中心，按压时缩放</summary>
        public Transform visuals;
        /// <summary>X offset for rain container alignment (0 for standard keys) / 雨滴容器的 X 偏移（标准按键为 0）</summary>
        public float rainOffsetX;
        /// <summary>Rain column width (key width; 50 when redirected to a front column) / 雨滴列宽（按键宽度；重指向前列时为 50）</summary>
        public float rainWidth = 50f;
        /// <summary>Backing node when this key belongs to a Custom layout / 自定义布局时对应的节点</summary>
        public FmNode CustomNode;
        /// <summary>Parsed KeyBind of CustomNode, cached with the raw string it was parsed from / CustomNode 绑定键的解析缓存（附解析时的原始字符串）</summary>
        public KeyCode CustomKeyCode;
        public string CustomKeyBindCached;
        /// <summary>Per-key KPS press log for Custom nodes (ephemeral, rebuilt with the overlay) / 自定义节点的每键 KPS 队列（临时态，随覆盖层重建）</summary>
        public readonly Queue<long> KpsLog = new Queue<long>(32);
        /// <summary>Last per-key KPS value written to the counter text / 上一次写入计数文本的每键 KPS 值</summary>
        public int LastShownKps = int.MinValue;
        /// <summary>Last group Total written to a custom Total panel. Per KEY, not per group:
        /// several panels can share one group, and a group-keyed cache let the first panel
        /// suppress every sibling's refresh. / 上一次写入自定义 Total 面板的组总数。按「按键」
        /// 而非按「组」缓存：一个组可以有多个面板，按组缓存会让第一个面板把同组的其它面板
        /// 永远压掉不刷新。</summary>
        public long LastShownTotal = long.MinValue;
        /// <summary>Last group KPS written to a custom KPS panel — same per-key reasoning. /
        /// 上一次写入自定义 KPS 面板的组 KPS——同样按按键缓存。</summary>
        public int LastShownStatKps = int.MinValue;
        /// <summary>Image-key visuals: the RawImage replaces the shape-layer box, and the two
        /// textures swap on press / 图片按键视觉：RawImage 取代形状层盒子，两张贴图按压时切换</summary>
        public RectTransform CustomImageRect;
        public RawImage CustomImage;
        public Texture2D CustomTexNormal;
        public Texture2D CustomTexPressed;
        /// <summary>Active video texture for this image key, owned by KvVideoTextureManager.
        /// Static normal/pressed textures remain available for the press overlay and fallback. /
        /// 图片键当前视频纹理由 KvVideoTextureManager 管理；静态常态/按压纹理仍保留用于按压覆盖和回退。</summary>
        public RenderTexture CustomVideoTexture;
    }
}
