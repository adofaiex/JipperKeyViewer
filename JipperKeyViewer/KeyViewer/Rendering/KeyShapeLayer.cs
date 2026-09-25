// Merged key-box rendering / 按键框合并渲染
// One Graphic per sprite (background / outline) draws every key box into a single mesh, replacing
// the old per-key Background/Outline Image hierarchy. Slot state (rect / colors / press scale /
// visibility) is owned by the background layer and shared by the outline layer, so a color or press
// change rebuilds one small mesh instead of re-batching hundreds of CanvasRenderers.
// 每张贴图一个 Graphic（背景/描边），把所有按键框画进单一 mesh，取代旧的每键 Background/Outline
// Image 层级。槽位状态（矩形/颜色/按压缩放/可见性）由背景层持有并与描边层共享，颜色或按压变化
// 只重建一个小 mesh，不再全画布重批。

using UnityEngine;
using UnityEngine.UI;

namespace JipperKeyViewer.KeyViewer.Rendering
{
    /// <summary>
    /// Draws all key boxes (9-sliced background or outline sprite) into one mesh / 将所有按键框（九宫格背景或描边贴图）绘制进单一 mesh
    /// </summary>
    public class KeyShapeLayer : MaskableGraphic
    {
        // Legacy CreateImage drew at 2x sizeDelta with 0.5 localScale, halving the effective 9-slice
        // border on screen. Reproduce that exactly so visuals are pixel-identical.
        // 旧 CreateImage 以 2 倍 sizeDelta + 0.5 缩放绘制，九宫格边框在屏幕上减半。精确复刻以保证视觉一致。
        private const float BorderScale = 0.5f;

        // Rounded-corner mode is a procedural mesh (the 9-slice sprites cannot round a corner),
        // so its outline ring needs a fallback thickness when the node does not set one. /
        // 圆角模式是程序化 mesh（九宫格贴图无法把直角变圆），因此其描边环在节点未指定厚度
        // 时需要默认厚度。
        private const float DefaultRoundedBorder = 2f;

        // --- Slot state (owner = background layer) / 槽位状态（持有者为背景层） ---
        private Rect[] rects;
        private Color[] bgColors;
        private Color[] outlineColors;
        // Optional vertical fill gradient for the background mesh. It is vertex data, not a
        // texture/Image per key, so rounded and 9-sliced boxes keep their exact geometry.
        // 背景 Mesh 的可选垂直渐变；使用顶点色而非每键贴图/Image，圆角与九宫格几何保持不变。
        private bool[] bgGradientEnabled;
        private Color[] bgGradientTops;
        private Color[] bgGradientBottoms;
        private bool[] outlineGradientEnabled;
        private Color[] outlineGradientTops;
        private Color[] outlineGradientBottoms;
        private float[] scales;
        private bool[] visibles;
        // Per-slot corner radius (0 = square, the legacy 9-slice path) and per-slot border
        // thickness in on-screen px (0 = follow the sprite's own 9-slice border). /
        // 每槽圆角半径（0 = 直角，走原九宫格路径）与每槽边框厚度（屏幕像素，0 = 跟随贴图自带的
        // 九宫格边框）。
        private float[] cornerRadii;
        private float[] borderThicknesses;
        private int count;

        /// <summary>State owner; null on the background layer itself / 状态持有者；背景层自身为 null</summary>
        private KeyShapeLayer owner;
        /// <summary>Outline layer sharing this layer's state (owner side) / 共享本层状态的描边层（持有方）</summary>
        private KeyShapeLayer outlineLayer;
        /// <summary>This layer draws the outline color set / 本层是否绘制描边颜色组</summary>
        private bool isOutline;

        /// <summary>Bumped on every Init so stale press-animation coroutines can detect a rebuild / 每次 Init 递增，供按压动画协程检测重建</summary>
        public int Generation { get; private set; }

        /// <summary>This layer's sprite (null = plain colored quad) / 本层贴图（null = 纯色矩形）</summary>
        public Sprite Sprite { get; set; }

        public override Texture mainTexture => Sprite != null ? Sprite.texture : base.mainTexture;

        public KeyShapeLayer()
        {
            raycastTarget = false;
        }

        /// <summary>Allocate slot arrays (owner only); all slots start hidden / 分配槽位数组（仅持有层）；所有槽位初始隐藏</summary>
        public void Init(int slotCount)
        {
            // `count` is published LAST, after every array it describes actually exists. It used to
            // be assigned first, so a throw from any of the twelve allocations below (a negative
            // slotCount is an ArgumentOutOfRangeException, and a huge one is an OOM) left `count`
            // at the NEW value while every array was still the PREVIOUS, shorter one. OnPopulateMesh
            // then iterated src.count over the old arrays and threw IndexOutOfRangeException inside
            // a uGUI mesh rebuild — on the background AND the outline layer (both read
            // `owner ?? this`) — on every subsequent rebuild, because nothing re-runs Init. The
            // whole key-box layer was permanently dead with nothing reaching the player's log.
            // No current caller can trigger it (Init is only ever called with a few thousand slots
            // ≈ 66 KB), but this was the one place in the class that published an invariant before
            // establishing it, and the guard is free.
            // `count` 最后发布，在它所描述的每个数组都真正存在之后。它此前是**最先**赋值，故下方
            // 十二处分配中任何一处抛出（负的 slotCount 是 ArgumentOutOfRangeException，过大则是
            // OOM）都会让 `count` 已是**新**值而所有数组仍是**旧的**、更短的那一批。
            // OnPopulateMesh 随后按 src.count 遍历旧数组，在 uGUI 的 mesh 重建**内部**抛
            // IndexOutOfRangeException——背景层与描边层都中招（两者都读 `owner ?? this`）——
            // 且此后每次重建都如此，因为没有任何东西会重跑 Init。整个按键框层永久死掉，而玩家
            // 的日志里什么都没有。当前的调用方都触发不了（Init 只会被以几千个槽位调用，约
            // 66KB），但这是本类里唯一一处「先发布不变量、后建立它」的地方，而守卫是免费的。
            if (slotCount < 0) slotCount = 0;
            rects = new Rect[slotCount];
            bgColors = new Color[slotCount];
            outlineColors = new Color[slotCount];
            bgGradientEnabled = new bool[slotCount];
            bgGradientTops = new Color[slotCount];
            bgGradientBottoms = new Color[slotCount];
            outlineGradientEnabled = new bool[slotCount];
            outlineGradientTops = new Color[slotCount];
            outlineGradientBottoms = new Color[slotCount];
            scales = new float[slotCount];
            visibles = new bool[slotCount];
            cornerRadii = new float[slotCount];
            borderThicknesses = new float[slotCount];
            for (int i = 0; i < slotCount; i++)
            {
                bgColors[i] = Color.white;
                outlineColors[i] = Color.white;
                scales[i] = 1f;
            }
            count = slotCount;
            Generation++;
            MarkDirty();
        }

        /// <summary>Wire the outline layer to this (owner) layer's state / 将描边层接到本（持有）层的状态上</summary>
        public void AttachOutlineLayer(KeyShapeLayer outline)
        {
            outlineLayer = outline;
            outline.owner = this;
            outline.isOutline = true;
            MarkDirty();
        }

        /// <summary>Set both layers' sprites (call on the owner) / 设置两层的贴图（在持有层上调用）</summary>
        public void SetSprites(Sprite background, Sprite outline)
        {
            Sprite = background;
            if (outlineLayer != null) outlineLayer.Sprite = outline;
            // Texture changed — the material must rebind or the renderer keeps the white default
            // (Image.sprite's setter does SetAllDirty for this reason) / 贴图变了必须重绑材质，
            // 否则渲染器一直用白色默认贴图（Image.sprite 的 setter 调 SetAllDirty 的原因）
            SetVerticesDirty();
            SetMaterialDirty();
            if (outlineLayer != null)
            {
                outlineLayer.SetVerticesDirty();
                outlineLayer.SetMaterialDirty();
            }
        }

        /// <summary>Update a slot's rect without touching its visibility (repositioning must not
        /// reveal streamer-hidden KPS/Total boxes; CreateKey shows new slots explicitly) /
        /// 更新槽位矩形但不改变可见性（重新定位不得重新显示主播模式隐藏的 KPS/Total；新槽位由 CreateKey 显式显示）</summary>
        public void SetRect(int slot, float x, float y, float w, float h)
        {
            if (slot < 0 || slot >= count) return;
            rects[slot] = new Rect(x, y, w, h);
            MarkDirty();
        }

        public void SetColors(int slot, Color background, Color outline)
        {
            if (slot < 0 || slot >= count) return;
            if (bgColors[slot] == background && outlineColors[slot] == outline) return;
            bgColors[slot] = background;
            outlineColors[slot] = outline;
            MarkDirty();
        }

        public void SetBackgroundGradient(int slot, bool enabled, Color top, Color bottom)
        {
            if (slot < 0 || slot >= count) return;
            enabled &= IsUsableGradientColor(top) && IsUsableGradientColor(bottom);
            if (bgGradientEnabled[slot] == enabled
                && (!enabled || (bgGradientTops[slot] == top && bgGradientBottoms[slot] == bottom)))
                return;
            bgGradientEnabled[slot] = enabled;
            bgGradientTops[slot] = enabled ? top : Color.white;
            bgGradientBottoms[slot] = enabled ? bottom : Color.white;
            MarkDirty();
        }

        public void SetOutlineGradient(int slot, bool enabled, Color top, Color bottom)
        {
            if (slot < 0 || slot >= count) return;
            enabled &= IsUsableGradientColor(top) && IsUsableGradientColor(bottom);
            if (outlineGradientEnabled[slot] == enabled
                && (!enabled || (outlineGradientTops[slot] == top && outlineGradientBottoms[slot] == bottom)))
                return;
            outlineGradientEnabled[slot] = enabled;
            outlineGradientTops[slot] = enabled ? top : Color.white;
            outlineGradientBottoms[slot] = enabled ? bottom : Color.white;
            MarkDirty();
        }

        private static bool IsInvalidColor(Color color) =>
            float.IsNaN(color.r) || float.IsNaN(color.g) || float.IsNaN(color.b) || float.IsNaN(color.a)
            || float.IsInfinity(color.r) || float.IsInfinity(color.g) || float.IsInfinity(color.b) || float.IsInfinity(color.a);

        /// <summary>A gradient whose stops are NaN/Inf OR fully transparent paints the whole key
        /// (or its border) invisible with no log at all. A hand-picked alpha-0 stop, an imported
        /// third-party .jkv or a corrupted color entry all hit this. Reject such a gradient and fall
        /// back to the plain solid color instead of silently deleting the key. / 渐变端点为 NaN/Inf
        /// 或完全透明时，整块按键（或整圈描边）会无日志地消失；此时拒绝该渐变并回退实色。</summary>
        private static bool IsUsableGradientColor(Color color)
            => !IsInvalidColor(color) && color.a > 0.001f;

        public void SetScale(int slot, float scale)
        {
            if (slot < 0 || slot >= count) return;
            if (scales[slot] == scale) return;
            scales[slot] = scale;
            MarkDirty();
        }

        public void SetVisible(int slot, bool visible)
        {
            if (slot < 0 || slot >= count) return;
            visibles[slot] = visible;
            MarkDirty();
        }

        /// <summary>Per-slot corner radius in px (0 = the square 9-slice path) /
        /// 每槽圆角半径（像素；0 = 直角九宫格路径）</summary>
        public void SetCornerRadius(int slot, float radius)
        {
            if (slot < 0 || slot >= count) return;
            radius = radius < 0f ? 0f : radius;
            if (cornerRadii[slot] == radius) return;
            cornerRadii[slot] = radius;
            MarkDirty();
        }

        /// <summary>Per-slot border thickness in px (0 = follow the sprite's own 9-slice border) /
        /// 每槽边框厚度（像素；0 = 跟随贴图自带的九宫格边框）</summary>
        public void SetBorderThickness(int slot, float thickness)
        {
            if (slot < 0 || slot >= count) return;
            thickness = thickness < 0f ? 0f : thickness;
            if (borderThicknesses[slot] == thickness) return;
            borderThicknesses[slot] = thickness;
            MarkDirty();
        }

        private void MarkDirty()
        {
            SetVerticesDirty();
            if (outlineLayer != null) outlineLayer.SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            KeyShapeLayer src = owner ?? this;
            if (src.rects == null) return;
            Color[] colors = isOutline ? src.outlineColors : src.bgColors;
            for (int i = 0; i < src.count; i++)
            {
                if (!src.visibles[i]) continue;
                Rect r = src.rects[i];
                if (r.width <= 0f || r.height <= 0f) continue;
                float s = src.scales[i];
                if (s != 1f)
                {
                    float cx = r.center.x, cy = r.center.y;
                    r = new Rect(cx - r.width * s * 0.5f, cy - r.height * s * 0.5f, r.width * s, r.height * s);
                    // Re-check AFTER scaling: the visible-rect guard above runs on the unscaled
                    // rect, and a negative press-animation scale (typed values are unclamped)
                    // would produce negative width/height — mirrored overlapping garbage slices.
                    // Also rejects NaN produced by any future bad scale math.
                    // 缩放后复查:上方可见矩形守卫跑在未缩放矩形上,负的按压缩放(键入值不
                    // 钳制)会产生负宽高——镜像交叠的垃圾切片。同时拦截未来坏缩放数学产生的 NaN。
                    if (float.IsNaN(r.width) || float.IsNaN(r.height) || r.width <= 0f || r.height <= 0f) continue;
                }
                bool gradient = isOutline
                    ? src.outlineGradientEnabled[i]
                    : src.bgGradientEnabled[i];
                Color bottom = gradient
                    ? (isOutline ? src.outlineGradientBottoms[i] : src.bgGradientBottoms[i])
                    : colors[i];
                Color top = gradient
                    ? (isOutline ? src.outlineGradientTops[i] : src.bgGradientTops[i])
                    : colors[i];
                float radius = src.cornerRadii[i];
                if (radius > 0f)
                {
                    // Outline ring drawn separately (slightly smaller quad below the filled one)
                    // so both layers keep the same two-mesh structure as the 9-slice path.
                    // 描边环单独绘制（填充块下方略小的块），使两层与九宫格路径保持同样的双
                    // mesh 结构。
                    float border = src.borderThicknesses[i];
                    DrawRounded(vh, r, top, bottom, gradient, radius, border);
                }
                else if (src.borderThicknesses[i] > 0f && isOutline)
                {
                    // Custom border thickness on a SQUARE key: an explicit ring replaces the
                    // sprite's baked 11px 9-slice border on the outline layer. Without this
                    // branch the BorderThickness setting changed nothing at all for square
                    // keys — only the rounded path ever read it. The background layer keeps
                    // its full 9-slice fill; the ring draws over its outer edge. /
                    // 直角键的自定义边框厚度：描边层改画指定厚度的直角环，取代贴图自带的
                    // 11px 九宫格边框。没有这个分支时 BorderThickness 对直角键完全无效——
                    // 只有圆角路径读它。背景层保持整块九宫格填充；环画在其外缘之上。
                    DrawSquareRing(vh, r, top, bottom, gradient, src.borderThicknesses[i]);
                }
                else
                {
                    DrawSliced(vh, r, top, bottom, gradient, Sprite);
                }
            }
        }

        /// <summary>
        /// Draw one 9-sliced quad, matching uGUI Image.Type.Sliced geometry: border in sprite pixels,
        /// squeezed proportionally when the rect is smaller than the combined borders; UV splits use
        /// the sprite's inner UV (border fraction of the sprite, independent of on-screen size).
        /// 绘制单个九宫格，几何与 uGUI Sliced 一致：边框按贴图像素，矩形过小时等比收缩；
        /// UV 切分用贴图 innerUV（按贴图边框比例，与屏幕尺寸无关）。
        /// </summary>
        // Scratch arrays reused across DrawSliced calls: meshes rebuild on every press/release, so
        // per-call float[4] allocations would be steady GC garbage on the input hot path.
        // DrawSliced 调用间复用的暂存数组：每次按下/松开都会重建 mesh，逐调用分配 float[4] 会在输入热路径持续产生 GC 垃圾。
        private static readonly float[] scratchX = new float[4];
        private static readonly float[] scratchY = new float[4];
        private static readonly float[] scratchU = new float[4];
        private static readonly float[] scratchV = new float[4];

        // Rounded-corner geometry scratch: one arc segment per ~7.5 degrees of a 90-degree corner
        // (12 segments), so 4 corners produce 52 points — smooth enough at any key size while
        // keeping the fan/ring vertex count bounded. Same no-per-call-allocation rule as above. /
        // 圆角几何暂存：每 90 度角按约 7.5 度一段（12 段），4 个角共 52 点——任意键尺寸下都足够
        // 平滑，同时把扇形/环的顶点数控制住。与上方同样的「逐调用不分配」原则。
        private const int RoundedSegments = 12;
        private const int MaxRoundedPoints = (RoundedSegments + 1) * 4;
        private static readonly float[] scratchRoundX = new float[MaxRoundedPoints];
        private static readonly float[] scratchRoundY = new float[MaxRoundedPoints];
        private static readonly float[] scratchInnerX = new float[MaxRoundedPoints];
        private static readonly float[] scratchInnerY = new float[MaxRoundedPoints];

        private static void DrawSliced(VertexHelper vh, Rect r, Color top, Color bottom, bool gradient, Sprite sprite)
        {
            if (sprite == null)
            {
                AddQuad(vh, r.xMin, r.xMax, r.yMin, r.yMax, 0f, 1f, 0f, 1f, top, bottom, gradient, r.yMin, r.yMax);
                return;
            }
            // UV rects computed from textureRect (outer) and border (inner) — this Unity version has
            // no Sprite.outerUV/innerUV properties / UV 矩形由 textureRect（外）与 border（内）换算，
            // 该 Unity 版本无 Sprite.outerUV/innerUV 属性
            Rect tr = sprite.textureRect;
            Texture tex = sprite.texture;
            float tw = tex.width, th = tex.height;
            Rect o = new Rect(tr.x / tw, tr.y / th, tr.width / tw, tr.height / th);
            // uGUI scales the ON-SCREEN border by 100/ppu (border * referencePixelsPerUnit /
            // spritePixelsPerUnit): a 200-ppu sprite renders at half reference size, so its 11px
            // border occupies 5.5px on screen. The UV splits stay RAW texture fractions (uGUI's
            // inner UV comes from the unmodified texel border). Inert at ppu 100 — guards
            // non-default imports. (The factor used to be inverted; it never mattered because
            // every shipped sprite is 100 ppu.)
            // uGUI 按 100/ppu 缩放"屏幕上"的边框(border * referencePixelsPerUnit /
            // spritePixelsPerUnit):200 ppu 的精灵按参考尺寸的一半渲染,11px 边框在屏幕上占
            // 5.5px。UV 切分保持原始纹理比例(uGUI 的内圈 UV 来自未缩放的 texel 边框)。
            // ppu=100 时无变化——防御非默认导入。(此前的系数方向写反;因所有随包贴图均为
            // 100 ppu 而从未暴露。)
            float ppuScale = sprite.pixelsPerUnit > 0f ? 100f / sprite.pixelsPerUnit : 1f;
            Vector4 spriteBorder = sprite.border * ppuScale;
            if (sprite.border == Vector4.zero)
            {
                AddQuad(vh, r.xMin, r.xMax, r.yMin, r.yMax, o.xMin, o.xMax, o.yMin, o.yMax, top, bottom, gradient, r.yMin, r.yMax);
                return;
            }
            // Inner UV from the RAW texel border — not the ppu-scaled one. / 内圈 UV 用原始 texel 边框计算,而非 ppu 缩放后的。
            Rect n = new Rect((tr.x + sprite.border.x) / tw, (tr.y + sprite.border.y) / th,
                (tr.width - sprite.border.x - sprite.border.z) / tw, (tr.height - sprite.border.y - sprite.border.w) / th);
            Vector4 border = spriteBorder * BorderScale;
            if (r.width < border.x + border.z)
            {
                float k = r.width / (border.x + border.z);
                border.x *= k;
                border.z *= k;
            }
            if (r.height < border.y + border.w)
            {
                float k = r.height / (border.y + border.w);
                border.y *= k;
                border.w *= k;
            }
            float[] xs = scratchX, ys = scratchY, us = scratchU, vs = scratchV;
            xs[0] = r.xMin; xs[1] = r.xMin + border.x; xs[2] = r.xMax - border.z; xs[3] = r.xMax;
            ys[0] = r.yMin; ys[1] = r.yMin + border.y; ys[2] = r.yMax - border.w; ys[3] = r.yMax;
            us[0] = o.xMin; us[1] = n.xMin; us[2] = n.xMax; us[3] = o.xMax;
            vs[0] = o.yMin; vs[1] = n.yMin; vs[2] = n.yMax; vs[3] = o.yMax;
            for (int yi = 0; yi < 3; yi++)
            {
                for (int xi = 0; xi < 3; xi++)
                {
                    if (xs[xi + 1] - xs[xi] <= 0f || ys[yi + 1] - ys[yi] <= 0f) continue;
                    AddQuad(vh, xs[xi], xs[xi + 1], ys[yi], ys[yi + 1], us[xi], us[xi + 1], vs[yi], vs[yi + 1], top, bottom, gradient, r.yMin, r.yMax);
                }
            }
        }

        /// <summary>Fill xs/ys with the counter-clockwise outline of a rounded rect (4 arcs,
        /// bottom-left corner first) and return the point count. / 用圆角矩形的逆时针轮廓
        ///（4 段圆弧，从左下角开始）填充 xs/ys 并返回点数。</summary>
        // Corner scratch for FillRoundedPoints. The mesh is rebuilt on every press/release, so the
        // three float[4] literals this method used to allocate became steady GC garbage on the input
        // hot path (105 rounded keys = 315 short-lived arrays per rebuild).
        // 圆角角点暂存数组：mesh 每次按压/松开都会重建，原先的三个 float[4] 字面量是输入热路径上
        // 持续产生的 GC 垃圾（105 个圆角键 = 每次重建 315 个短命数组）。
        private static readonly float[] scratchCornerX = new float[4];
        private static readonly float[] scratchCornerY = new float[4];
        private static readonly float[] scratchCornerAngle = new float[] { 180f, 270f, 0f, 90f };

        private static int FillRoundedPoints(Rect r, float radius, float[] xs, float[] ys)
        {
            if (r.width <= 0f || r.height <= 0f) return 0;
            float rad = Mathf.Max(0f, Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f));
            int k = 0;
            // Corner centers in CCW order with each arc's start angle (degrees). / 逆时针顺序的
            // 角心及每段圆弧的起始角度（度）。
            float[] cx = scratchCornerX, cy = scratchCornerY, a0 = scratchCornerAngle;
            cx[0] = r.xMin + rad; cx[1] = r.xMax - rad; cx[2] = r.xMax - rad; cx[3] = r.xMin + rad;
            cy[0] = r.yMin + rad; cy[1] = r.yMin + rad; cy[2] = r.yMax - rad; cy[3] = r.yMax - rad;
            for (int c = 0; c < 4; c++)
            {
                for (int i = 0; i <= RoundedSegments; i++)
                {
                    float a = (a0[c] + 90f * i / RoundedSegments) * Mathf.Deg2Rad;
                    xs[k] = cx[c] + rad * Mathf.Cos(a);
                    ys[k] = cy[c] + rad * Mathf.Sin(a);
                    k++;
                }
            }
            return k;
        }

        /// <summary>Sample point for the procedural path: the middle texel of this layer's sprite,
        /// so a flat fill picks up the same texture tint the 9-slice body would. / 程序化路径的
        /// 采样点：本层贴图的中央 texel，使纯色填充取到与九宫格主体一致的贴图色。</summary>
        private Vector2 RoundedUV()
        {
            if (Sprite == null) return new Vector2(0.5f, 0.5f);
            Rect tr = Sprite.textureRect;
            Texture tex = Sprite.texture;
            if (tex == null) return new Vector2(0.5f, 0.5f);
            return new Vector2((tr.x + tr.width * 0.5f) / tex.width, (tr.y + tr.height * 0.5f) / tex.height);
        }

        /// <summary>Sample point for the procedural OUTLINE ring. The outline sprite
        /// (KeyOutline.png) is a thin FRAME: only its outer ~4px band is opaque, the centre
        /// texel is alpha-0 — a centre-sampled ring multiplies the vertex colour by transparent
        /// and renders invisible (the "border vanishes" bug). Sample 2 texels inside the LEFT
        /// edge instead: inside the opaque frame band, vertically centred. /
        /// 程序化描边环的采样点。描边贴图（KeyOutline.png）是一个细框：只有最外约 4px 带
        /// 不透明，中心 texel 为 alpha-0——按中心采样的环会被乘以透明而渲染成隐形（即
        /// 「边框消失」bug）。改为采样左缘内侧 2 texel：落在不透明框带内、垂直居中。</summary>
        private Vector2 RingUV()
        {
            if (Sprite == null) return new Vector2(0.5f, 0.5f);
            Rect tr = Sprite.textureRect;
            Texture tex = Sprite.texture;
            if (tex == null || tex.width <= 0 || tex.height <= 0) return new Vector2(0.5f, 0.5f);
            float x = Mathf.Clamp(tr.x + 2f, tr.xMin, tr.xMax - 0.5f);
            return new Vector2(x / tex.width, (tr.y + tr.height * 0.5f) / tex.height);
        }

        /// <summary>Rounded-rect mesh: the background layer fills the shape, the outline layer
        /// draws a border-thick ring just inside the edge. A 9-slice sprite cannot round a corner,
        /// so radius &gt; 0 slots bypass DrawSliced entirely. / 圆角矩形 mesh：背景层填充形状，
        /// 描边层沿边缘内侧画 border 像素的环。九宫格贴图无法圆角，故 radius &gt; 0 的槽位完全
        /// 绕开 DrawSliced。</summary>
        private void DrawRounded(VertexHelper vh, Rect r, Color top, Color bottom, bool gradient, float radius, float border)
        {
            float rad = Mathf.Max(0f, Mathf.Min(radius, Mathf.Min(r.width, r.height) * 0.5f));
            Vector2 uv = RoundedUV();
            // The ring MUST sample the opaque frame band, not the centre — see RingUV. /
            // 描边环必须采不透明框带而非中心——见 RingUV。
            Vector2 ringUv = isOutline ? RingUV() : uv;
            if (rad <= 0f)
            {
                // A radius that collapsed (sub-pixel box) still renders as a plain quad rather
                // than vanishing. / 半径被压缩到 0（亚像素盒子）时仍画成普通矩形而非消失。
                AddQuad(vh, r.xMin, r.xMax, r.yMin, r.yMax, ringUv.x, ringUv.x, ringUv.y, ringUv.y, top, bottom, gradient, r.yMin, r.yMax);
                return;
            }
            int n = FillRoundedPoints(r, rad, scratchRoundX, scratchRoundY);
            if (n == 0) return;
            if (!isOutline)
            {
                // Filled shape: triangle fan around the rect centre. / 填充形状：绕矩形中心的三角扇。
                int c = vh.currentVertCount;
                UIVertex v = UIVertex.simpleVert;
                v.color = VerticalGradientColor(top, bottom, gradient, r.center.y, r.yMin, r.yMax);
                v.uv0 = new Vector4(uv.x, uv.y, 0f, 0f);
                v.position = new Vector3(r.center.x, r.center.y, 0f);
                vh.AddVert(v);
                for (int i = 0; i < n; i++)
                {
                    v.color = VerticalGradientColor(top, bottom, gradient, scratchRoundY[i], r.yMin, r.yMax);
                    v.position = new Vector3(scratchRoundX[i], scratchRoundY[i], 0f);
                    vh.AddVert(v);
                }
                for (int i = 0; i < n; i++)
                    vh.AddTriangle(c, c + 1 + i, c + 1 + (i + 1) % n);
                return;
            }
            // Outline ring between the outer rounded rect and an inset rounded rect. / 描边环：
            // 外圈圆角矩形与内缩圆角矩形之间。
            float b = border > 0f ? border : DefaultRoundedBorder;
            b = Mathf.Min(b, Mathf.Min(r.width, r.height) * 0.5f);
            if (b <= 0f) return;
            Rect inner = new Rect(r.x + b, r.y + b, r.width - 2f * b, r.height - 2f * b);
            int m = FillRoundedPoints(inner, Mathf.Max(0f, rad - b), scratchInnerX, scratchInnerY);
            if (m != n)
            {
                // The inset rect collapsed (border too thick for the box) or its radius
                // shrank to 0 while the outer is still rounded. Fall back to drawing the
                // outer ring only — still better than vanishing entirely. /
                // 内缩矩形崩溃（边框过粗）或其半径缩到 0 而外圈仍圆角。降级为只画外圈环——
                // 仍比完全消失要好。
                m = n;
                for (int i = 0; i < n; i++)
                {
                    float angle = (float)i / n * Mathf.PI * 2f;
                    float cos = Mathf.Cos(angle);
                    float sin = Mathf.Sin(angle);
                    scratchInnerX[i] = r.x + b + cos * Mathf.Max(0f, rad - b);
                    scratchInnerY[i] = r.y + b + sin * Mathf.Max(0f, rad - b);
                }
            }
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                AddQuadVerts(vh,
                    new Vector2(scratchRoundX[i], scratchRoundY[i]),
                    new Vector2(scratchRoundX[j], scratchRoundY[j]),
                    new Vector2(scratchInnerX[j], scratchInnerY[j]),
                    new Vector2(scratchInnerX[i], scratchInnerY[i]),
                    ringUv, top, bottom, gradient, r.yMin, r.yMax);
            }
        }

        /// <summary>Square ring of an explicit thickness — the square-key counterpart of the
        /// rounded outline ring. Four axis-aligned strips (horizontal ones span the full width,
        /// vertical ones only the middle) so corners never double-draw. Thickness 0 never
        /// reaches here — the legacy 9-slice path keeps the sprite's baked border. /
        /// 指定厚度的直角描边环——圆角描边环在直角键上的对应物。四条轴向边（横条占满全宽、
        /// 竖条只占中段），角上不会重复绘制。厚度 0 不会走到这里——旧九宫格路径保留贴图
        /// 自带边框。</summary>
        private void DrawSquareRing(VertexHelper vh, Rect r, Color top, Color bottom, bool gradient, float border)
        {
            float b = Mathf.Min(border, Mathf.Min(r.width, r.height) * 0.5f);
            if (b <= 0f) return;
            Vector2 uv = RingUV();
            AddQuad(vh, r.xMin, r.xMax, r.yMax - b, r.yMax, uv.x, uv.x, uv.y, uv.y, top, bottom, gradient, r.yMin, r.yMax); // top
            AddQuad(vh, r.xMin, r.xMax, r.yMin, r.yMin + b, uv.x, uv.x, uv.y, uv.y, top, bottom, gradient, r.yMin, r.yMax); // bottom
            AddQuad(vh, r.xMin, r.xMin + b, r.yMin + b, r.yMax - b, uv.x, uv.x, uv.y, uv.y, top, bottom, gradient, r.yMin, r.yMax); // left
            AddQuad(vh, r.xMax - b, r.xMax, r.yMin + b, r.yMax - b, uv.x, uv.x, uv.y, uv.y, top, bottom, gradient, r.yMin, r.yMax); // right
        }

        /// <summary>Arbitrary quad — the ring segments are not axis-aligned. / 任意四边形——环段
        /// 不与坐标轴对齐。</summary>
        private static void AddQuadVerts(VertexHelper vh, Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 uv,
            Color top, Color bottom, bool gradient, float gradientYMin, float gradientYMax)
        {
            int i = vh.currentVertCount;
            UIVertex vert = UIVertex.simpleVert;
            vert.uv0 = new Vector4(uv.x, uv.y, 0f, 0f);
            vert.color = VerticalGradientColor(top, bottom, gradient, a.y, gradientYMin, gradientYMax);
            vert.position = new Vector3(a.x, a.y, 0f);
            vh.AddVert(vert);
            vert.color = VerticalGradientColor(top, bottom, gradient, b.y, gradientYMin, gradientYMax);
            vert.position = new Vector3(b.x, b.y, 0f);
            vh.AddVert(vert);
            vert.color = VerticalGradientColor(top, bottom, gradient, c.y, gradientYMin, gradientYMax);
            vert.position = new Vector3(c.x, c.y, 0f);
            vh.AddVert(vert);
            vert.color = VerticalGradientColor(top, bottom, gradient, d.y, gradientYMin, gradientYMax);
            vert.position = new Vector3(d.x, d.y, 0f);
            vh.AddVert(vert);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }

        private static Color VerticalGradientColor(Color top, Color bottom, bool gradient, float y, float yMin, float yMax)
        {
            if (!gradient || yMax <= yMin) return top;
            return Color.Lerp(bottom, top, Mathf.Clamp01((y - yMin) / (yMax - yMin)));
        }

        private static void AddQuad(VertexHelper vh, float x0, float x1, float y0, float y1, float u0, float u1, float v0, float v1,
            Color top, Color bottom, bool gradient, float gradientYMin, float gradientYMax)
        {
            int i = vh.currentVertCount;
            UIVertex vert = UIVertex.simpleVert;
            vert.color = VerticalGradientColor(top, bottom, gradient, y0, gradientYMin, gradientYMax);
            vert.position = new Vector3(x0, y0, 0f);
            vert.uv0 = new Vector4(u0, v0, 0f, 0f);
            vh.AddVert(vert);
            vert.color = VerticalGradientColor(top, bottom, gradient, y0, gradientYMin, gradientYMax);
            vert.position = new Vector3(x1, y0, 0f);
            vert.uv0 = new Vector4(u1, v0, 0f, 0f);
            vh.AddVert(vert);
            vert.color = VerticalGradientColor(top, bottom, gradient, y1, gradientYMin, gradientYMax);
            vert.position = new Vector3(x1, y1, 0f);
            vert.uv0 = new Vector4(u1, v1, 0f, 0f);
            vh.AddVert(vert);
            vert.color = VerticalGradientColor(top, bottom, gradient, y1, gradientYMin, gradientYMax);
            vert.position = new Vector3(x0, y1, 0f);
            vert.uv0 = new Vector4(u0, v1, 0f, 0f);
            vh.AddVert(vert);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }

        private static void AddQuad(VertexHelper vh, float x0, float x1, float y0, float y1, float u0, float u1, float v0, float v1, Color color)
        {
            int i = vh.currentVertCount;
            UIVertex vert = UIVertex.simpleVert;
            vert.color = color;
            vert.position = new Vector3(x0, y0, 0f);
            vert.uv0 = new Vector4(u0, v0, 0f, 0f);
            vh.AddVert(vert);
            vert.position = new Vector3(x1, y0, 0f);
            vert.uv0 = new Vector4(u1, v0, 0f, 0f);
            vh.AddVert(vert);
            vert.position = new Vector3(x1, y1, 0f);
            vert.uv0 = new Vector4(u1, v1, 0f, 0f);
            vh.AddVert(vert);
            vert.position = new Vector3(x0, y1, 0f);
            vert.uv0 = new Vector4(u0, v1, 0f, 0f);
            vh.AddVert(vert);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }
    }
}
