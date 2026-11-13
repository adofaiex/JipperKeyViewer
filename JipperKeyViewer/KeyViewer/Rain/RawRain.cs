using UnityEngine;

namespace JipperKeyViewer.KeyViewer.Rain

{
    /// <summary>
    /// Pooled rain-drop state: motion data updated by RainSystem, render data consumed by RainLayer.
    /// No GameObject — the merged layers read this directly every frame.
    /// 对象池雨滴状态：运动数据由 RainSystem 更新，渲染数据由 RainLayer 消费。
    /// 无 GameObject——合并图层每帧直接读取本状态。
    /// </summary>
    public class RawRain
    {
        public float elapsedMs;
        public float startY;
        public byte color;
        public Vector2 FinalSize;
        public Vector2? sizeDelta;
        public Vector2? anchoredPosition;
        public bool removed;
        public bool isGhost;
        public bool growing;

        // --- Render state / 渲染状态 ---
        /// <summary>Layer-space rect of the drop (computed each frame in RainSystem) / 雨滴在图层空间的矩形（RainSystem 每帧计算）</summary>
        public Rect rect;
        /// <summary>Drop color (rain color or ghost color) — the trail gradient's BOTTOM end. /
        /// 雨滴颜色（普通雨色或鬼雨色）——轨迹渐变的底端。</summary>
        public Color mainColor = Color.white;
        /// <summary>Trail gradient's TOP end (alpha carries the base opacity; the fade multiplies
        /// per-end at render time). / 轨迹渐变的顶端（alpha 承载基础不透明度，淡出在渲染时
        /// 逐端相乘）。</summary>
        public Color ColorTop = Color.white;
        /// <summary>Per-node rain start Y offset (px), added to the row's start-Y at render. /
        /// 节点级雨滴起始 Y 偏移（像素），渲染时加在所在排的起始 Y 上。</summary>
        public float StartOffsetY;
        /// <summary>Per-node rain parameter overrides (0 = follow the mapped row). /
        /// 节点级雨滴参数覆盖（0 = 跟随所在排全局参数）。</summary>
        public float NodeWidth;
        public float NodeHeight;
        public float NodeSpeed;
        /// <summary>Custom nodes: absolute distance from the key's BOTTOM edge to the rain start
        /// (= node height + gap, node-top-anchored track model). When set, it REPLACES the row's
        /// start-Y — the 275px container constant is tuned for 50px keys and would spawn the
        /// trail inside taller nodes. / 自定义节点：键底边到雨滴起点的绝对距离（= 节点高 +
        /// 间隙，锚定节点顶边的轨道模型）。设置后替换所在排起始 Y——275px 容器常量是为
        /// 50px 键调校的，较高的节点会让轨迹从按键内部冒出来。</summary>
        public bool HasTrackBase;
        public float TrackBaseY;
        /// <summary>Per-drop X offset override (ghost rain with independent params); when unset
        /// the key's shared rainOffsetX applies. / 逐滴 X 偏移覆盖（独立参数的鬼雨）；未设时
        /// 用按键共享的 rainOffsetX。</summary>
        public bool HasOffsetX;
        public float OffsetXOverride;
        /// <summary>Fade-out alpha (1 = opaque) / 淡出透明度（1 = 不透明）</summary>
        public float alpha = 1f;
        /// <summary>Per-key press scale applied when this rect was computed (scales shadow/outline offsets at emit) / 计算此矩形时的每键按压缩放（发射时缩放阴影/描边偏移）</summary>
        public float scaleF = 1f;
        public bool fadingOut;
        public float fadeTimer;
        // Trail gradient params / 轨迹渐变参数
        public float dNear, dFar, trackHeight, fadePx;
        // Shadow / outline params (per drop, from row settings at creation) / 阴影/描边参数（创建时取自行设置）
        public bool shadowEnabled;
        public Color shadowColor;
        public float shadowOffsetX, shadowOffsetY;
        public bool outlineEnabled;
        public Color outlineColor;
        public float outlineWidth;
        /// <summary>Optional Quartz-style soft glow emitted behind the drop body / 可选的 Quartz 风格雨滴柔光，绘制在本体之下</summary>
        public bool glowEnabled;
        public Color glowColor = Color.white;
        public float glowRange;
        public float glowOpacity = 0.7f;
        /// <summary>Optional per-drop sprite override from a rain image list; null = legacy quad. /
        /// 来自雨滴贴图列表的可选精灵覆盖；null = 旧矩形。</summary>
        public Sprite sprite;
        /// <summary>Per-node sprite transform (sprite drops only), sampled from the node's eased
        /// press/release values at creation. Scale is a MULTIPLIER: 1 = natural size and is the
        /// neutral value the renderer tests against — 0 must never be the default here, or every
        /// sprite drop without a transform (ghost drops, nodes with the feature off) would be
        /// clamped to 5% in the transformed path. /
        /// 逐滴贴图变换（仅贴图雨滴），创建时按节点缓动的按下/松开值取样。缩放是**乘数**：
        /// 1 = 原始尺寸，也是渲染器判定的中性值——默认值绝不能是 0，否则一切没有变换的贴图
        /// 雨滴（鬼雨、未开启该功能的节点）都会在变换路径里被钳到 5%。</summary>
        public float spriteScale = 1f;
        public float spriteOffsetX;
        public float spriteOffsetY;
        public float spriteRotation;
        /// <summary>Optional rounded outline corner radius (0 = square) / 可选圆角描边半径</summary>
        public float outlineCornerRadius;
        /// <summary>0 = all, 1 = vertical, 2 = horizontal / 描边方向</summary>
        public int outlineSides;
        /// <summary>0 = Up (legacy), 1 = Down, 2 = Left, 3 = Right. Rect math stays in
        /// RainSystem; this only records the resolved direction for renderers that need it. /
        /// 0=上（旧行为），1=下，2=左，3=右。矩形数学留在 RainSystem；该字段供需要方向的渲染器读取。</summary>
        public int direction;
        /// <summary>Optional dotted trail pattern / 可选点状轨迹参数</summary>
        public bool dotted;
        public float dotLength;
        public float gapLength;
        /// <summary>Resolved pre-blur level for sprite drops (0 = none, 1/2/3 ≈ radius 2/4/8 px).
        /// Snapshot at creation like the other per-drop params: RainLayer samples the SOURCE
        /// texture by this level, so drops created before the setting changed keep the texture
        /// they were emitted with until they are recycled. / 贴图雨滴解析后的预模糊档位
        /// （0 = 无，1/2/3 约对应半径 2/4/8 像素）。与其它逐滴参数一致，创建时快照：
        /// RainLayer 按此档位采样源贴图，设置变更前创建的雨滴在其生命周期内保留创建时的贴图，
        /// 回收后重新解析。</summary>
        public byte blurLevel;

        public RawRain(byte color)
        {
            this.color = color;
            elapsedMs = 0f;
        }

        public bool UpdateLocation(bool updateSize, float speedFactor, float height, float deltaMs)
        {
            elapsedMs += deltaMs;
            float y = elapsedMs * speedFactor;
            float dropY = startY + y;
            if (updateSize || FinalSize == default)
            {
                // Width floor mirrors the speed/height floors in RainSystem: typed widths are
                // stored unclamped, and a zero/negative width drop is skipped by the renderers
                // but would otherwise never be recycled — floor keeps it recyclable and sane.
                // RainSystem resolves the row's global width into NodeWidth at CREATION time; this
                // hot path therefore never reads KeyViewer.Settings (a static back-reference that
                // also NREs when Settings is null).
                // 宽度下限与 RainSystem 的速度/高度下限同理:键入宽度不钳制,零/负宽度雨滴
                // 被渲染器跳过但若不设下限将永不回收——下限保证可回收且尺寸正常。
                // 该排的全局宽度在 RainSystem 创建雨滴时就解析进 NodeWidth，因此本热路径不再
                // 访问 KeyViewer.Settings。
                FinalSize = new Vector2(Mathf.Max(NodeWidth, 1f), y);
            }
            if (dropY > height)
            {
                float sizeY = FinalSize.y - dropY + height;
                if (sizeY < 0) return false;
                sizeDelta = new Vector2(FinalSize.x, sizeY);
                anchoredPosition = new Vector2(0, height);
            }
            else
            {
                if (updateSize) sizeDelta = FinalSize;
                anchoredPosition = new Vector2(0, dropY);
            }
            return true;
        }
    }
}
