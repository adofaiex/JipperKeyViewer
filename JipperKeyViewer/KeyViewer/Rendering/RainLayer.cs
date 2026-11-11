// Merged rain rendering / 雨滴合并渲染
// RainLayer draws every normal drop into one mesh, normally using the white texture for gradient
// quads but switching to a drop's cached image when the rain sprite list supplies one. It also
// draws ghost shadow/outline quads; GhostRainLayer draws the ghost sprite bodies, sharing the
// per-key press scales owned by RainLayer. Both read the RawRain state maintained by RainSystem,
// replacing the per-key RainLine canvases and the pooled Rain GameObjects — no per-drop
// RectTransform writes, no SetSiblingIndex, one rebuild per layer per frame while raining.
// RainLayer 把普通雨滴画进一个 Mesh：默认用白色贴图画渐变矩形，但雨滴贴图列表提供贴图时改用
// 该雨滴的缓存图片；同时绘制鬼雨阴影/描边。GhostRainLayer 画鬼雨贴图本体，与 RainLayer 共享
// 每键按压缩放。两者读取 RainSystem 维护的 RawRain 状态，取代每键 RainLine 画布与 Rain 对象池
// ——无逐雨滴 RectTransform 写入、无 SetSiblingIndex，下雨期间每层每帧只重建一次。

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

using JipperKeyViewer.KeyViewer.Rain;

namespace JipperKeyViewer.KeyViewer.Rendering

{
    /// <summary>
    /// Merged rain mesh: normal bodies (solid or sprite) + ghost shadow/outline /
    /// 合并雨滴 Mesh：普通本体（实色或贴图）+ 鬼雨阴影/描边
    /// </summary>
    public class RainLayer : MaskableGraphic
    {
        /// <summary>Rain system providing the drop state / 提供雨滴状态的雨滴系统</summary>
        internal RainSystem System;
        /// <summary>Per-key rain press scale (EnablePressAnimationOnRain), shared with the ghost layer / 每键雨滴按压缩放（雨滴跟随动画），与鬼雨层共享</summary>
        private float[] rainScales;

        private GhostRainLayer ghostLayer;
        /// <summary>The texture bound to the current mesh. Because a mesh can mix the white
        /// quad texture with per-drop image textures, this is assigned between sprite quads and
        /// cleared after each mesh. / 当前 Mesh 绑定的贴图。Mesh 可能混合白色矩形与逐滴贴图，
        /// 因此在精灵四边形前赋值，并在每次 Mesh 结束后清空。</summary>
        private Texture currentSpriteTexture;
        public override Texture mainTexture => currentSpriteTexture != null ? currentSpriteTexture : base.mainTexture;
        public RainLayer()
        {
            raycastTarget = false;
        }

        /// <summary>Reset per-key scales for a new key count (called on rebuilds) / 为新键数重置每键缩放（重建时调用）</summary>
        public void Init(int keyCount)
        {
            rainScales = new float[keyCount];
            for (int i = 0; i < keyCount; i++) rainScales[i] = 1f;
            MarkDirty();
        }

        public void AttachGhostLayer(GhostRainLayer ghost)
        {
            ghostLayer = ghost;
            MarkDirty();
        }

        /// <summary>Texture whose wrapMode this layer changed to Repeat, and its original value —
        /// restoring it on sprite change keeps a shared texture from staying mutated forever. /
        /// 本层把 wrapMode 改成 Repeat 的贴图及其原值——换精灵时还原，避免共用贴图被永久改写。</summary>
        private Texture wrapModeOwner;
        private TextureWrapMode wrapModeOriginal;

        /// <summary>Set the ghost layer's sprite (null = ghost bodies hidden) / 设置鬼雨层贴图（null = 鬼雨本体不显示）</summary>
        public void SetSprite(Sprite sprite)
        {
            // Single-quad UV tiling needs Repeat — only safe to flip on a standalone texture; a
            // packed/atlas sprite keeps its wrap mode and falls back to per-tile quads.
            // 单四边形 UV 平铺需要 Repeat——仅在独立贴图上翻转才安全；打包/图集贴图保持原 wrap，
            // 渲染时回退为逐平铺四边形。
            //
            // The flip mutates a SHARED Texture2D. It used to be one-way, so a texture used
            // elsewhere (a custom image node pointing at the same PNG, say) stayed Repeat for the
            // rest of the session and started bleeding at its edges. Restore the previous value
            // whenever the sprite changes.
            // 该翻转会修改**共享** Texture2D。此前是单向的：同一张 PNG 若还被自定义图片节点使用，
            // 就会一直保持 Repeat 并在边缘出现拉色。现在换精灵时还原上一次的值。
            RestoreWrapMode();
            if (sprite != null && sprite.texture != null && IsStandaloneRect(sprite))
            {
                wrapModeOwner = sprite.texture;
                wrapModeOriginal = sprite.texture.wrapMode;
                sprite.texture.wrapMode = TextureWrapMode.Repeat;
            }
            if (ghostLayer != null) ghostLayer.Sprite = sprite;
            SetVerticesDirty();
            SetMaterialDirty();
            if (ghostLayer != null)
            {
                // A new sprite may carry geometry the old one could not, so force one rebuild
                // rather than letting the MarkDirty latch skip it.
                // 新贴图可能带旧贴图无法产生的几何，故强制一次重建，不让 MarkDirty 闩锁跳过。
                ghostLayer.HadGhostGeometry = true;
                ghostLayer.SetVerticesDirty();
                ghostLayer.SetMaterialDirty();
            }
        }

        private void RestoreWrapMode()
        {
            if (wrapModeOwner == null) return;
            if (wrapModeOwner.wrapMode == TextureWrapMode.Repeat)
                wrapModeOwner.wrapMode = wrapModeOriginal;
            wrapModeOwner = null;
        }

        /// <summary>Whether the sprite's rect covers its whole texture (no atlas packing) / 贴图矩形是否覆盖整张贴图（无图集打包）</summary>
        internal static bool IsStandaloneRect(Sprite sprite)
        {
            if (sprite == null || sprite.texture == null) return false;
            Rect tr = sprite.textureRect;
            Texture tex = sprite.texture;
            return tr.x <= 0.01f && tr.y <= 0.01f && tr.xMax >= tex.width - 0.01f && tr.yMax >= tex.height - 0.01f;
        }

        public void SetKeyScale(int keyIndex, float scale)
        {
            if (rainScales == null || keyIndex < 0 || keyIndex >= rainScales.Length) return;
            if (rainScales[keyIndex] == scale) return;
            rainScales[keyIndex] = scale;
            MarkDirty();
        }

        public float GetKeyScale(int keyIndex)
            => rainScales != null && keyIndex >= 0 && keyIndex < rainScales.Length ? rainScales[keyIndex] : 1f;

        public void MarkDirty()
        {
            SetVerticesDirty();
            // The ghost layer was dirtied on EVERY rain rebuild, for the whole session, even when
            // the document has no ghost binding at all and its mesh is therefore empty both before
            // and after the rebuild. A paired in-game measurement put that at 1.29 ms/frame — the
            // single largest item in the overlay's cost. Rebuilding a mesh that stays empty cannot
            // change a pixel, so skip it; the `HadGhostGeometry` latch keeps the one rebuild that
            // IS needed, the frame the last ghost drop disappears and the mesh must be cleared.
            // 鬼雨层此前在**每次**雨滴重建时都被标脏——整个会话如此——即使文档根本没有鬼雨绑定、
            // 其 mesh 重建前后都是空的。配对实测该项为 1.29ms/帧，是覆盖层成本里最大的一项。
            // 重建一个前后都为空的 mesh 不可能改变任何像素，故跳过；HadGhostGeometry 闩锁保留
            // 那一次真正必需的重建——最后一滴鬼雨消失、必须清空 mesh 的那一帧。
            if (ghostLayer != null)
            {
                bool hasGhost = System != null && System.HasLiveGhostDrops();
                if (hasGhost || ghostLayer.HadGhostGeometry)
                {
                    ghostLayer.HadGhostGeometry = hasGhost;
                    ghostLayer.SetVerticesDirty();
                    ghostLayer.SetMaterialDirty();
                }
            }
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            RainSystem rainSystem = System;
            Key[] keys = rainSystem != null ? rainSystem.Keys : null;
            try
            {
                if (keys == null) return;
                List<int> activeKeys = rainSystem.ActiveKeys;
                // Ghost bodies normally render in GhostRainLayer; if the ghost sprite failed to load
                // (missing bundle asset / PNG), fall back to solid ghost-colored quads here (the old
                // no-sprite path drew opaque white — ghost color is the deliberate improvement).
                // 鬼雨本体通常画在 GhostRainLayer；鬼雨贴图加载失败（bundle 缺资源 / PNG 缺失）时
                // 在此退化为鬼雨色纯色四边形（旧版无贴图路径画不透明白色——用鬼雨色是有意改进）。
                bool ghostFallback = ghostLayer == null || ghostLayer.Sprite == null;
                for (int a = 0; a < activeKeys.Count; a++)
                {
                    int keyIndex = activeKeys[a];
                    if ((uint)keyIndex >= (uint)keys.Length) continue;
                    Key key = keys[keyIndex];
                    if (key == null || key.rainList.Count == 0) continue;
                    // Old per-key stacking: normals below, ghost quads above (SetSiblingIndex reserved
                    // the +1 slot for ghosts) — two passes reproduce that exactly.
                    // 旧版每键内普通雨在下、鬼雨（含阴影/描边）在上（SetSiblingIndex 为鬼雨保留 +1
                    // 槽位）——两趟绘制精确复现。
                    // One pass for bodies, remembering whether this key even has ghost drops: the
                    // second pass only matters for a key that actually owns ghost rain, and a normal-rain
                    // key otherwise paid a second full rainList walk every rebuild.
                    // 一趟画本体并记录该键是否真的有鬼雨：第二趟只对拥有鬼雨的键有意义，普通雨滴的键
                    // 否则每次重建都要再走一遍完整 rainList。
                    bool hasGhost = false;
                    for (int d = 0; d < key.rainList.Count; d++)
                    {
                        RawRain rain = key.rainList[d];
                        if (rain.removed) continue;
                        if (rain.isGhost) { hasGhost = true; continue; }
                        DrawDrop(vh, rain, drawMain: true);
                    }
                    if (!hasGhost) continue;
                    for (int d = 0; d < key.rainList.Count; d++)
                    {
                        RawRain rain = key.rainList[d];
                        if (rain.removed || !rain.isGhost) continue;
                        if (!ghostFallback && !rain.shadowEnabled && !rain.outlineEnabled) continue;
                        DrawDrop(vh, rain, drawMain: ghostFallback);
                    }
                }
            }
            finally
            {
                currentSpriteTexture = null;
            }
        }

        /// <summary>NaN or +/-Infinity? / 是否为 NaN 或 ±无穷？</summary>
        internal static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>Is every component of this drop rect finite? A non-finite component poisons the
        /// WHOLE shared mesh (one drop, every key on screen), and an infinite xMax additionally
        /// hangs the tiling loop, so both layers reject such a drop before emitting vertices.
        /// 该雨滴矩形的每个分量是否都有限？非有限分量会污染**整个**共享 mesh（一滴雨滴、屏幕上
        /// 所有键），而无穷的 xMax 还会让平铺循环卡死，故两层在输出顶点前都拒绝这种雨滴。
        /// </summary>
        internal static bool IsFiniteRect(Rect r)
            => IsFinite(r.xMin) && IsFinite(r.yMin) && IsFinite(r.width) && IsFinite(r.height)
               && IsFinite(r.xMax) && IsFinite(r.yMax);

        /// <summary>Emit one drop's shadow/outline/(main) quads with the trail gradient. The
        /// body carries a bottom→top two-color gradient (bottom-to-top two-color gradient); the
        /// shadow/outline stay single-color. / 输出一滴雨的阴影/描边/(本体)四边形及轨迹渐变。
        /// 本体携带底→顶双色渐变（底→顶双色渐变）；阴影/描边保持单色。</summary>
        private void DrawDrop(VertexHelper vh, RawRain rain, bool drawMain)
        {
            Rect r = rain.rect;
            if (r.width <= 0f || r.height <= 0f) return;
            // NaN fails every `<=` comparison, so a single poisoned rect/scale would slip past the
            // guard above and write NaN vertices into the SHARED mesh — one bad drop would then
            // corrupt the whole rain canvas. Infinity matters just as much and was missed here:
            // the guard tested IsInfinity on scaleF ONLY, so an infinite width produced
            // xMin = -Inf / xMax = +Inf and AddQuad wrote those into the shared mesh.
            // / NaN 能通过所有 `<=` 比较，坏矩形/缩放会把 NaN 顶点写进共享 mesh，一个坏雨滴就会
            // 污染整块雨滴画布。无穷同样致命而此前被漏掉：守卫只对 scaleF 测了 IsInfinity，
            // 故无限宽会产出 xMin=-Inf / xMax=+Inf 并被 AddQuad 写进共享 mesh。
            if (!IsFiniteRect(r) || !IsFinite(rain.scaleF)) return;
            float baseA = rain.isGhost && !drawMain ? rain.alpha : rain.mainColor.a * rain.alpha;
            // Direction-aware trail geometry. The motion model ALWAYS grows the rect along the
            // drop's travel axis, but the gradient params (dNear/dFar/trackHeight/fadePx) and the
            // emitters below were written for a VERTICAL trail. For Left/Right the trail runs along
            // X: the quad's colour ramp must interpolate left→right (not bottom→top), and the
            // top-fade must map onto X. Feeding the vertical values into a horizontal rect is what
            // made Left/Right drops invisible — AlphaAtD collapsed the whole trail to alpha 0. /
            // 方向感知的轨迹几何。运动模型总是沿雨滴的生长方向扩大矩形，但渐变参数
            //（dNear/dFar/trackHeight/fadePx）与下方发射器是按**垂直**轨迹写的。左/右方向的
            // 轨迹沿 X 展开：四边形颜色渐变必须沿左→右插值（而非底→顶），顶部渐隐也必须映射到
            // X 轴。把垂直值喂给水平矩形正是左/右雨滴不可见的原因——AlphaAtD 把整条轨迹塌成了
            // 透明度 0。
            bool horizontal = rain.direction == 2 || rain.direction == 3;
            // Which end of the rect sits on the track base (the node)? Up and Right put the NEAR
            // end at the LOW coordinate (yMin / xMin); Down and Left put the FAR (head) end there.
            // The colour ramp and the top-fade both run near→far, so every emitter below must
            // know the orientation — feeding them "low end == near" unconditionally drew Down and
            // Left with their gradient and fade reversed. /
            // 矩形哪一端落在轨道基座（节点）？上、右把**近**端放在低坐标端（yMin / xMin）；下、左
            // 把**远**端（头部）放在那里。颜色渐变与顶部渐隐都沿近→远展开，故下方每个发射器都必须
            // 知道取向——此前一律按「低端 = 近端」处理，下与左的渐变和渐隐都是反的。
            bool reversed = rain.direction == 1 || rain.direction == 2;
            float h = horizontal ? r.width : r.height;
            float span = rain.dFar - rain.dNear;
            bool simple = rain.fadePx <= 0.5f || rain.trackHeight <= 0.5f || span <= 0.0001f;
            float sf = rain.scaleF;
            if (rain.glowEnabled)
                DrawRainGlow(vh, rain, baseA, simple);

            if (rain.shadowEnabled)
            {
                Color sc = rain.shadowColor;
                sc.a *= baseA;
                if (rain.dotted && rain.dotLength > 0.5f)
                    DrawDottedRainRect(vh, rain, new Rect(r.xMin + rain.shadowOffsetX * sf, r.yMin + rain.shadowOffsetY * sf,
                        r.width, r.height), sc, sc, simple, horizontal, reversed);
                else
                    DrawRainQuad(vh, r.xMin + rain.shadowOffsetX * sf, r.xMax + rain.shadowOffsetX * sf,
                        r.yMin + rain.shadowOffsetY * sf, r.yMax + rain.shadowOffsetY * sf, h, sc, sc,
                        rain.dNear, rain.dFar, rain.trackHeight, rain.fadePx, span, simple, horizontal, reversed);
            }
            if (rain.outlineEnabled)
            {
                Color oc = rain.outlineColor;
                oc.a *= baseA;
                float ow = rain.outlineWidth * sf;
                // A zero outline width with sides==all expands a SOLID quad over the whole drop.
                // For normal rain the body is drawn afterwards and hides it, but GHOST rain is
                // drawn with drawMain:false whenever the ghost sprite is active, so the "outline"
                // became an opaque slab that completely covered the ghost sprite. Only a positive
                // width can produce a border at all. / 宽度为 0 且 sides=all 时会画出覆盖整滴的实心
                // 四边形：普通雨被后画的本体盖住，但鬼雨在 drawMain:false 下会变成一整块实心板把
                // 精灵完全遮住。宽度必须为正才可能形成描边。
                if (ow > 0.01f)
                {
                    if (rain.dotted && rain.dotLength > 0.5f)
                    {
                        // Dotted outline: segment the EXPANDED rect and let the dotted body (drawn
                        // next) cover its middle, which yields a dotted ring. Going through the
                        // rounded/side-specific emitters would need a segment loop per band; the
                        // body covers the interior anyway, so a solid outer ring with the same
                        // dot pattern is visually identical here.
                        // 点状描边：对外扩矩形做同样的切段，随后的点状本体会盖住中段，形成点状描边环。
                        DrawDottedRainRect(vh, rain, new Rect(r.xMin - ow, r.yMin - ow, r.width + ow * 2f, r.height + ow * 2f), oc, oc, simple, horizontal, reversed);
                    }
                    else if (rain.outlineCornerRadius > 0.5f)
                        DrawRoundedRainOutline(vh, r, ow, rain.outlineCornerRadius, rain.outlineSides, oc, rain.dNear, rain.dFar,
                            rain.trackHeight, rain.fadePx, span, simple, horizontal, reversed);
                    else
                        DrawRainOutlineBySide(vh, r, ow, rain.outlineSides, oc, rain.dNear, rain.dFar,
                            rain.trackHeight, rain.fadePx, span, simple, horizontal, reversed);
                }
            }
            if (drawMain)
            {
                if (rain.sprite != null)
                {
                    if (rain.dotted && rain.dotLength > 0.5f)
                        return;
                    currentSpriteTexture = rain.sprite.texture;
                    try
                    {
                        DrawSpriteDrop(vh, rain, r, baseA);
                    }
                    finally
                    {
                        currentSpriteTexture = null;
                    }
                    return;
                }
                Color cb = rain.mainColor; cb.a = baseA;
                Color ct = rain.ColorTop; ct.a = baseA;
                if (rain.dotted && rain.dotLength > 0.5f)
                    DrawDottedRainBody(vh, rain, r, cb, ct, simple, horizontal, reversed);
                else
                    DrawRainQuad(vh, r.xMin, r.xMax, r.yMin, r.yMax, h, cb, ct,
                        rain.dNear, rain.dFar, rain.trackHeight, rain.fadePx, span, simple, horizontal, reversed);
            }
        }

        /// <summary>Draw an image-list drop. Top fade is applied by vertex alpha exactly like the
        /// quad body; release fade is already folded into baseA. UVs map the full sprite, so a
        /// rounded source image remains rounded. When the node configured a sprite transform
        /// (scale / offset / rotation), the quad is emitted around the trail-rect centre with
        /// that transform applied — the trail segment itself stays where the motion model put
        /// it. / 绘制贴图列表雨滴。顶部渐隐像矩形本体一样通过顶点透明度施加；松开淡出已折入
        /// baseA。UV 映射整张贴图，故源图自带圆角保持不变。节点配置了贴图变换（缩放/偏移/旋转）
        /// 时，四边形绕轨迹矩形中心按该变换输出——轨迹段本身仍停在运动模型放它的位置。</summary>
        private static void DrawSpriteDrop(VertexHelper vh, RawRain rain, Rect r, float alpha)
        {
            bool horizontal = rain.direction == 2 || rain.direction == 3;
            bool reversed = rain.direction == 1 || rain.direction == 2;
            Color near = rain.mainColor;
            Color far = rain.ColorTop;
            bool simple = rain.fadePx <= 0.5f || rain.trackHeight <= 0.5f || (rain.dFar - rain.dNear) <= 0.0001f;
            if (!simple)
            {
                near.a *= AlphaAtD(rain.dNear, rain.trackHeight - rain.fadePx, rain.trackHeight, rain.fadePx);
                far.a *= AlphaAtD(rain.dFar, rain.trackHeight - rain.fadePx, rain.trackHeight, rain.fadePx);
            }
            near.a *= alpha;
            far.a *= alpha;
            near.a = Mathf.Clamp01(near.a);
            far.a = Mathf.Clamp01(far.a);
            // Map the near/far colours onto the quad's edges along the TRAIL axis: bottom/top for
            // a vertical drop, left/right for a horizontal one, and swapped when the rect's low
            // coordinate end is the far end (Down, Left). Applying them to bottom/top
            // unconditionally gave Left/Right a vertical ramp across the trail's thin thickness —
            // i.e. no visible fade at all along the direction the drop travels. /
            // 按**轨迹轴**把近/远两色映射到四边形边缘：垂直雨滴取底/顶，水平雨滴取左/右，且当矩形
            // 低坐标端是远端（下、左）时互换。此前一律按底/顶施加，左/右雨滴的渐变因此落在轨迹那条
            // 很薄的厚度方向上——即沿雨滴行进方向根本看不到渐隐。
            Color cBL, cBR, cTR, cTL;
            if (horizontal)
            {
                Color left = reversed ? far : near;
                Color right = reversed ? near : far;
                cBL = cTL = left;
                cBR = cTR = right;
            }
            else
            {
                Color bottom = reversed ? far : near;
                Color top = reversed ? near : far;
                cBL = cBR = bottom;
                cTR = cTL = top;
            }
            // Transform present when any channel is non-neutral (scale 1 = natural size; the
            // three others default 0). Values were clamped at resolution; the guards here are the
            // last-line mesh protection every other rect consumer applies. /
            // 任一通道非中性即视为有变换（缩放 1 = 原始尺寸；其余三项默认 0）。数值在解析时已
            // 钳制；这里的守卫是其它矩形消费方都有的最后一道 mesh 防线。
            bool hasTransform = rain.spriteScale != 1f || rain.spriteOffsetX != 0f || rain.spriteOffsetY != 0f || rain.spriteRotation != 0f;
            if (!hasTransform || !IsFinite(rain.spriteScale) || !IsFinite(rain.spriteOffsetX)
                || !IsFinite(rain.spriteOffsetY) || !IsFinite(rain.spriteRotation))
            {
                EmitSpriteQuad(vh,
                    new Vector2(r.xMin, r.yMin), new Vector2(r.xMax, r.yMin),
                    new Vector2(r.xMax, r.yMax), new Vector2(r.xMin, r.yMax),
                    cBL, cBR, cTR, cTL);
                return;
            }
            float scale = Mathf.Clamp(rain.spriteScale, 0.05f, 10f);
            float rad = -Mathf.Clamp(rain.spriteRotation, -180f, 180f) * Mathf.Deg2Rad; // screen Y up → negate for CW-positive input / 屏幕 Y 向上 → 取负
            Vector2 centre = new Vector2(r.center.x + rain.spriteOffsetX, r.center.y + rain.spriteOffsetY);
            if (!IsFinite(centre.x) || !IsFinite(centre.y)) return;
            float hw = r.width * 0.5f * scale;
            float hh = r.height * 0.5f * scale;
            if (!IsFinite(hw) || !IsFinite(hh) || hw <= 0f || hh <= 0f) return;
            float cos = Mathf.Cos(rad), sin = Mathf.Sin(rad);
            EmitSpriteQuad(vh,
                new Vector2(centre.x + (-hw) * cos - (-hh) * sin, centre.y + (-hw) * sin + (-hh) * cos), // bottom-left
                new Vector2(centre.x + hw * cos - (-hh) * sin, centre.y + hw * sin + (-hh) * cos),       // bottom-right
                new Vector2(centre.x + hw * cos - hh * sin, centre.y + hw * sin + hh * cos),             // top-right
                new Vector2(centre.x + (-hw) * cos - hh * sin, centre.y + (-hw) * sin + hh * cos),       // top-left
                cBL, cBR, cTR, cTL);
        }

        /// <summary>Emit the sprite quad with PER-CORNER UVs (0,0)-(1,1) and a PER-CORNER colour, so
        /// the trail ramp can follow whichever axis the drop travels along. The shared AddQuad
        /// samples one centre UV — right for solid quads, but a sprite drop drawn through it would
        /// render a single centre texel stretched across the whole rect. /
        /// 以**逐角** UV (0,0)-(1,1) 与**逐角**颜色输出贴图四边形，使轨迹渐变能跟随雨滴行进的轴。
        /// 共用的 AddQuad 采样单一中心 UV——对纯色四边形是对的，但贴图雨滴经它绘制会把**中心一个
        /// texel** 拉满整个矩形。</summary>
        private static void EmitSpriteQuad(VertexHelper vh, Vector2 bl, Vector2 br, Vector2 tr, Vector2 tl,
            Color cbl, Color cbr, Color ctr, Color ctl)
        {
            UIVertex[] verts = RainVertexScratch;
            // uGUI quad order: bottom-left, bottom-right, top-right, top-left. /
            // uGUI 四边形顶点序：左下、右下、右上、左上。
            verts[0].position = bl; verts[0].color = cbl; verts[0].uv0 = new Vector4(0f, 0f, 0f, 0f);
            verts[1].position = br; verts[1].color = cbr; verts[1].uv0 = new Vector4(1f, 0f, 0f, 0f);
            verts[2].position = tr; verts[2].color = ctr; verts[2].uv0 = new Vector4(1f, 1f, 0f, 0f);
            verts[3].position = tl; verts[3].color = ctl; verts[3].uv0 = new Vector4(0f, 1f, 0f, 0f);
            vh.AddUIVertexQuad(verts);
        }

        /// <summary>Shared scratch quad for the sprite paths (no per-drop allocation). /
        /// 贴图路径共用的四顶点暂存（逐滴零分配）。</summary>
        private static readonly UIVertex[] RainVertexScratch = new UIVertex[4];

        /// <summary>Emit one soft-glow quad behind the drop body, using the same padded soft-edge
        /// sprite model as fixed/FreeMake key glow. It stays inside the merged rain mesh, so no
        /// per-drop Image or extra CanvasRenderer is created. / 在雨滴本体下方发出一块柔光四边形，
        /// 沿用固定布局/FreeMake 按键光效的外扩软边模型；仍留在合并雨滴 Mesh 内，不为每滴雨创建
        /// Image 或额外 CanvasRenderer。</summary>
        private void DrawRainGlow(VertexHelper vh, RawRain rain, float baseA, bool simple)
        {
            float range = rain.glowRange;
            float opacity = rain.glowOpacity;
            if (!IsFinite(range) || !IsFinite(opacity)) return;
            range = Mathf.Clamp(range, 0f, 50f);
            opacity = Mathf.Clamp01(opacity);
            if (range <= 0f || opacity <= 0f) return;

            float pad = range * Mathf.Max(rain.scaleF, 0f);
            if (!IsFinite(pad) || pad <= 0f) return;
            Rect glow = new Rect(rain.rect.xMin - pad, rain.rect.yMin - pad,
                rain.rect.width + pad * 2f, rain.rect.height + pad * 2f);
            if (!IsFiniteRect(glow)) return;

            Color color = rain.glowColor;
            if (!IsFinite(color.r) || !IsFinite(color.g) || !IsFinite(color.b) || !IsFinite(color.a))
                color = rain.mainColor;
            color.a *= baseA * opacity;
            color.a = Mathf.Clamp01(color.a);
            if (color.a <= 0f) return;

            float innerRadius = Mathf.Clamp(rain.outlineCornerRadius, 0f,
                Mathf.Min(glow.width, glow.height) * 0.5f);
            bool horizontal = rain.direction == 2 || rain.direction == 3;
            bool reversed = rain.direction == 1 || rain.direction == 2;
            DrawRoundedRainGlowRing(vh, glow, innerRadius, color, rain.dNear, rain.dFar,
                rain.trackHeight, rain.fadePx, rain.dFar - rain.dNear, simple, horizontal, reversed);
        }

        /// <summary>Soft rounded halo: a ring whose alpha runs from full at the drop edge to zero
        /// at the outer edge. It applies the SAME top fade as the body, but from the halo's own
        /// padded track segment — scaling the body's endpoint alphas instead made the halo fade
        /// twice inside the body's top band and pop harder than the drop. / 柔和圆角光环：与本体
        /// 使用同一顶部渐隐，但按光晕自己的外扩轨迹段计算；若直接缩放本体端点透明度，光晕会在
        /// 本体顶部渐隐带内衰减两次，比雨滴本体更突兀。使用与雨滴一致的圆角模型，避免圆角雨滴
        /// 被矩形光晕破坏。</summary>
        private static void DrawRoundedRainGlowRing(VertexHelper vh, Rect inner, float innerRadius, Color color,
            float dNear, float dFar, float trackH, float fade, float span, bool simple,
            bool horizontal = false, bool reversed = false)
        {
            if (inner.width <= 0f || inner.height <= 0f) return;
            Rect outer = new Rect(inner.xMin, inner.yMin, inner.width, inner.height);
            float innerR = Mathf.Clamp(innerRadius, 0f, Mathf.Min(inner.width, inner.height) * 0.5f);
            float outerR = innerR;
            if (IsFiniteRect(outer))
            {
                outer.xMin -= outerR;
                outer.yMin -= outerR;
                outer.xMax += outerR;
                outer.yMax += outerR;
            }
            if (!IsFiniteRect(outer) || outer.width <= 0f || outer.height <= 0f) return;

            Color transparent = color;
            transparent.a = 0f;
            if (horizontal)
            {
                // The halo's fade runs along the trail's X, like the body's. Sampling it along Y
                // (the thin thickness) made the halo two-tone across its width and perfectly flat
                // along the direction the drop travels. For the side strips the X span is the halo
                // radius, so each is near-uniform — read it at its own X. /
                // 光晕的渐隐与本体一样沿轨迹 X 展开。按 Y（那条很薄的厚度）采样会让光晕在宽度方向上
                // 出现两段色、而沿雨滴行进方向完全平坦。侧条的 X 跨度只是光晕半径，故各自近乎均匀
                //——按自己的 X 取值即可。
                Color left = GlowColorAt(color, inner.xMin, inner.yMin, inner, dNear, dFar, trackH, fade, simple, true, reversed);
                Color right = GlowColorAt(color, inner.xMax, inner.yMin, inner, dNear, dFar, trackH, fade, simple, true, reversed);
                AddRainPoly4(vh, inner.xMin + innerR, inner.yMax, inner.xMax - innerR, inner.yMax,
                    inner.xMax - innerR, outer.yMax, inner.xMin + innerR, outer.yMax,
                    left, right, transparent, transparent);
                AddRainPoly4(vh, inner.xMin + innerR, inner.yMin, inner.xMax - innerR, inner.yMin,
                    inner.xMax - innerR, outer.yMin, inner.xMin + innerR, outer.yMin,
                    left, right, transparent, transparent);
                // The caps are the ring's LEFT/RIGHT ends: their vertical span is the rect's Y
                // inset by the corner radius, and each keeps the colour of its own end. Taking the
                // X inset here (the first cut did) sized the caps from the trail's position in
                // layer space — a node at x≈200 put them ~200px off the drop, leaving a gap at
                // both ends and a stray bar elsewhere. /
                // 端盖是光环的**左/右**两端：纵向跨度是矩形 Y 内缩一个圆角半径，各自沿用本端的
                // 颜色。第一版误用 X 内缩——端盖尺寸取自轨迹在图层空间的 x（节点 x≈200 时端盖
                // 会跑到雨滴外约 200px），两端各留缺口、远处还多出一条杂条。
                float capY0 = inner.yMin + innerR;
                float capY1 = inner.yMax - innerR;
                AddRainPoly4(vh, inner.xMin, capY0, outer.xMin, capY0,
                    outer.xMin, capY1, inner.xMin, capY1,
                    left, transparent, transparent, left);
                AddRainPoly4(vh, inner.xMax, capY0, outer.xMax, capY0,
                    outer.xMax, capY1, inner.xMax, capY1,
                    right, transparent, transparent, right);
                if (innerR <= 0.01f) return;
                AddGlowArc(vh, inner.xMax - innerR, inner.yMax - innerR, innerR, outerR, 0f, Mathf.PI * 0.5f,
                    color, inner, dNear, dFar, trackH, fade, simple, true, reversed);
                AddGlowArc(vh, inner.xMin + innerR, inner.yMax - innerR, innerR, outerR, Mathf.PI * 0.5f, Mathf.PI,
                    color, inner, dNear, dFar, trackH, fade, simple, true, reversed);
                AddGlowArc(vh, inner.xMin + innerR, inner.yMin + innerR, innerR, outerR, Mathf.PI, Mathf.PI * 1.5f,
                    color, inner, dNear, dFar, trackH, fade, simple, true, reversed);
                AddGlowArc(vh, inner.xMax - innerR, inner.yMin + innerR, innerR, outerR, Mathf.PI * 1.5f, Mathf.PI * 2f,
                    color, inner, dNear, dFar, trackH, fade, simple, true, reversed);
                return;
            }

            Color innerBottom = GlowColorAt(color, inner.xMin, inner.yMin, inner, dNear, dFar, trackH, fade, simple, false, reversed);
            Color innerTop = GlowColorAt(color, inner.xMin, inner.yMax, inner, dNear, dFar, trackH, fade, simple, false, reversed);

            AddRainPoly4(vh, inner.xMin + innerR, inner.yMax, inner.xMax - innerR, inner.yMax,
                inner.xMax - innerR, outer.yMax, inner.xMin + innerR, outer.yMax,
                innerTop, innerTop, transparent, transparent);
            AddRainPoly4(vh, inner.xMin + innerR, inner.yMin, inner.xMax - innerR, inner.yMin,
                inner.xMax - innerR, outer.yMin, inner.xMin + innerR, outer.yMin,
                innerBottom, innerBottom, transparent, transparent);

            float sideY0 = inner.yMin + innerR;
            float sideY1 = inner.yMax - innerR;
            Color sideBottom = GlowColorAt(color, inner.xMin, sideY0, inner, dNear, dFar, trackH, fade, simple, false, reversed);
            Color sideTop = GlowColorAt(color, inner.xMin, sideY1, inner, dNear, dFar, trackH, fade, simple, false, reversed);
            AddRainPoly4(vh, inner.xMin, sideY0, outer.xMin, sideY0,
                outer.xMin, sideY1, inner.xMin, sideY1,
                sideBottom, transparent, transparent, sideTop);
            AddRainPoly4(vh, inner.xMax, sideY0, outer.xMax, sideY0,
                outer.xMax, sideY1, inner.xMax, sideY1,
                sideBottom, transparent, transparent, sideTop);

            if (innerR <= 0.01f) return;
            AddGlowArc(vh, inner.xMax - innerR, inner.yMax - innerR, innerR, outerR, 0f, Mathf.PI * 0.5f,
                color, inner, dNear, dFar, trackH, fade, simple, false, reversed);
            AddGlowArc(vh, inner.xMin + innerR, inner.yMax - innerR, innerR, outerR, Mathf.PI * 0.5f, Mathf.PI,
                color, inner, dNear, dFar, trackH, fade, simple, false, reversed);
            AddGlowArc(vh, inner.xMin + innerR, inner.yMin + innerR, innerR, outerR, Mathf.PI, Mathf.PI * 1.5f,
                color, inner, dNear, dFar, trackH, fade, simple, false, reversed);
            AddGlowArc(vh, inner.xMax - innerR, inner.yMin + innerR, innerR, outerR, Mathf.PI * 1.5f, Mathf.PI * 2f,
                color, inner, dNear, dFar, trackH, fade, simple, false, reversed);
        }

        private static Color GlowColorAt(Color color, float x, float y, Rect glowInner, float dNear, float dFar,
            float trackH, float fade, bool simple, bool horizontal, bool reversed)
        {
            if (simple) return color;
            float t = horizontal
                ? (glowInner.width <= 0.0001f ? 0f : Mathf.Clamp01((x - glowInner.xMin) / glowInner.width))
                : (glowInner.height <= 0.0001f ? 0f : Mathf.Clamp01((y - glowInner.yMin) / glowInner.height));
            if (reversed) t = 1f - t;
            float distance = Mathf.Lerp(dNear, dFar, t);
            color.a *= AlphaAtD(distance, trackH - fade, trackH, fade);
            return color;
        }

        private static void AddGlowArc(VertexHelper vh, float cx, float cy, float innerRadius, float outerRadius,
            float a0, float a1, Color color, Rect outer, float dNear, float dFar, float trackH, float fade,
            bool simple, bool horizontal = false, bool reversed = false)
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(outerRadius * 0.35f), 3, 8);
            float previousCos = Mathf.Cos(a0);
            float previousSin = Mathf.Sin(a0);
            for (int i = 1; i <= steps; i++)
            {
                float angle = Mathf.Lerp(a0, a1, i / (float)steps);
                float c = Mathf.Cos(angle);
                float s = Mathf.Sin(angle);
                Color inner0 = GlowColorAt(color, cx + previousCos * innerRadius, cy + previousSin * innerRadius,
                    outer, dNear, dFar, trackH, fade, simple, horizontal, reversed);
                Color inner1 = GlowColorAt(color, cx + c * innerRadius, cy + s * innerRadius,
                    outer, dNear, dFar, trackH, fade, simple, horizontal, reversed);
                Color outer0 = transparentGlow;
                Color outer1 = transparentGlow;
                AddRainPoly4(vh,
                    cx + previousCos * innerRadius, cy + previousSin * innerRadius,
                    cx + previousCos * outerRadius, cy + previousSin * outerRadius,
                    cx + c * outerRadius, cy + s * outerRadius,
                    cx + c * innerRadius, cy + s * innerRadius,
                    inner0, outer0, outer1, inner1);
                previousCos = c;
                previousSin = s;
            }
        }

        private static readonly Color transparentGlow = new Color(0f, 0f, 0f, 0f);

        /// <summary>Trail-gradient quad, ported verbatim from the old RainGraphic and extended to
        /// a bottom→top two-color body (single-color callers pass the same color twice). /
        /// 轨迹渐变四边形，自旧 RainGraphic 原样移植并扩展为底→顶双色（单色调用方传同一颜色
        /// 两次）。</summary>
        private static void DrawRainOutlineBySide(VertexHelper vh, Rect body, float width, int sides, Color color,
            float dNear, float dFar, float trackH, float fade, float span, bool simple,
            bool horizontal = false, bool reversed = false)
        {
            // Renamed from vertical/horizontal: `sides != 2` gates the TOP/BOTTOM bars (which are
            // horizontal strips) and `sides != 1` the LEFT/RIGHT bars — the old names described the
            // strip orientation, the opposite of the axis they gate, and now also collide with the
            // trail-axis parameter above. / 原名为 vertical/horizontal：`sides != 2` 门控**上下**条
            //（水平条）、`sides != 1` 门控**左右**条——旧名描述的是条带朝向，与它们门控的轴相反，
            // 且现在会与上方的轨迹轴参数撞名。
            bool drawTopBottom = sides != 2;
            bool drawLeftRight = sides != 1;
            // NO full-quad fast path for the "all sides" case. It used to emit ONE solid quad over
            // the whole expanded drop and let the body paint on top of it — visually a border,
            // but every pixel of the interior was blended and then immediately covered. The
            // measured cost was 1.75 ms/frame of rain, of which only 0.43 ms appeared inside the
            // canvas pass; the remaining ~1.3 ms is fill/overdraw that lands after it, on the
            // GPU, and it scales with drop area × drop count. The drop body is a large
            // alpha-blended quad (50 px wide by up to `RainHeight` tall), so a profile with both
            // shadow and outline enabled paid roughly four full-area blended quads per drop where
            // three were needed.
            // The strip emitters below already produce exactly the same visible border: with both
            // axes set they draw the top/bottom strips at full width and the left/right strips at
            // full height, whose union IS the complete ring. The interior is simply never emitted.
            // 描边「全部」模式不再走整块四边形快路径。旧实现对整滴外扩矩形画一块实心四边形、再由
            // 本体覆盖——视觉上是边框，但中间每一个像素都被混合了一次随即被覆盖。实测雨滴每帧
            // 1.75ms 中只有 0.43ms 落在画布 pass 内，其余约 1.3ms 是其后的填充/overdraw、开销
            // 随雨滴面积与数量增长。雨滴本体是又宽又高的半透明四边形，故同时开阴影与描边的配置
            // 每滴要付约四块全面积混合，而只需要三块。
            //
            // 下方条带发射器给出的可见边框完全相同：两轴都开时先画满宽的上下条、再画满高的左右条，
            // 其并集正是完整边框环，内部区域根本不会被发出。
            Rect outer = new Rect(body.xMin - width, body.yMin - width,
                body.width + width * 2f, body.height + width * 2f);
            if (drawTopBottom)
            {
                DrawRainSolidRect(vh, outer.xMin, outer.xMax, outer.yMin, outer.yMin + width, color, outer,
                    dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
                DrawRainSolidRect(vh, outer.xMin, outer.xMax, outer.yMax - width, outer.yMax, color, outer,
                    dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
            }
            if (drawLeftRight)
            {
                DrawRainSolidRect(vh, outer.xMin, outer.xMin + width, outer.yMin, outer.yMax, color, outer,
                    dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
                DrawRainSolidRect(vh, outer.xMax - width, outer.xMax, outer.yMin, outer.yMax, color, outer,
                    dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
            }
        }

        private static void DrawRoundedRainOutline(VertexHelper vh, Rect body, float width, float requestedRadius, int sides,
            Color color, float dNear, float dFar, float trackH, float fade, float span, bool simple,
            bool horizontal = false, bool reversed = false)
        {
            float halfW = Mathf.Min(width, Mathf.Min(body.width, body.height) * 0.5f);
            if (halfW <= 0.01f)
            {
                DrawRainOutlineBySide(vh, body, width, sides, color, dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
                return;
            }
            Rect outer = new Rect(body.xMin - halfW, body.yMin - halfW,
                body.width + halfW * 2f, body.height + halfW * 2f);
            float radius = Mathf.Clamp(requestedRadius, 0f, Mathf.Min(outer.width, outer.height) * 0.5f);
            if (radius <= 0.01f)
            {
                DrawRainOutlineBySide(vh, new Rect(outer.xMin + halfW, outer.yMin + halfW,
                    outer.width - halfW * 2f, outer.height - halfW * 2f), halfW, sides, color,
                    dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
                return;
            }
            DrawRoundedRainOutlineWithRadius(vh, outer, radius, halfW, sides, color, dNear, dFar, trackH, fade, span, simple,
                horizontal, reversed);
        }

        private static void DrawRoundedRainOutlineWithRadius(VertexHelper vh, Rect outer, float radius, float width,
            int sides, Color color, float dNear, float dFar, float trackH, float fade, float span, bool simple,
            bool horizontal = false, bool reversed = false)
        {
            float inner = Mathf.Max(0f, radius - width);
            bool drawTopBottom = sides != 2;
            bool drawLeftRight = sides != 1;
            if (drawTopBottom)
            {
                DrawRainSolidRect(vh, outer.xMin + radius, outer.xMax - radius, outer.yMin, outer.yMin + width,
                    color, outer, dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
                DrawRainSolidRect(vh, outer.xMin + radius, outer.xMax - radius, outer.yMax - width, outer.yMax,
                    color, outer, dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
            }
            if (drawLeftRight)
            {
                DrawRainSolidRect(vh, outer.xMin, outer.xMin + width, outer.yMin + radius, outer.yMax - radius,
                    color, outer, dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
                DrawRainSolidRect(vh, outer.xMax - width, outer.xMax, outer.yMin + radius, outer.yMax - radius,
                    color, outer, dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
            }
            // ALL FOUR corner arcs, in every side mode. The two axis flags gate only the straight
            // strips, and both single-axis modes keep bars whose ends are the top AND the bottom
            // corners -- so gating the arcs on the same flags left the opposite pair undrawn and
            // punched two radius x radius transparent notches out of the outline ring (sides == 2
            // lost the top pair, sides == 1 the bottom pair). / 四角弧在**任何** sides 模式下都
            // 全部发出。两个轴开关只门控直条，而两种单轴模式保留下来的直条其两端都是**上下**
            // 两组角，于是沿用同一组开关门控弧会让对侧那对弧不发，在描边环上挖出两个
            // radius x radius 的透明缺口（sides == 2 缺顶部、sides == 1 缺底部）。
            AddRainArc(vh, outer.xMax - radius, outer.yMax - radius, radius, inner, 0f, Mathf.PI * 0.5f,
                color, outer, dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
            AddRainArc(vh, outer.xMin + radius, outer.yMax - radius, radius, inner, Mathf.PI * 0.5f, Mathf.PI,
                color, outer, dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
            AddRainArc(vh, outer.xMin + radius, outer.yMin + radius, radius, inner, Mathf.PI, Mathf.PI * 1.5f,
                color, outer, dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
            AddRainArc(vh, outer.xMax - radius, outer.yMin + radius, radius, inner, Mathf.PI * 1.5f, Mathf.PI * 2f,
                color, outer, dNear, dFar, trackH, fade, span, simple, horizontal, reversed);
        }

        private static void DrawRainSolidRect(VertexHelper vh, float xL, float xR, float yB, float yT, Color color,
            Rect outer, float dNear, float dFar, float trackH, float fade, float span, bool simple,
            bool horizontal = false, bool reversed = false)
        {
            if (xR <= xL || yT <= yB) return;
            if (horizontal)
            {
                // The fade runs along X, so the top/bottom bars get a left→right ramp and the
                // vertical side bars (whose X span is just the outline width) read as near-uniform
                // — which is exactly what they should be. /
                // 渐隐沿 X 展开：上下条得到左→右渐变，竖直侧条（X 跨度只是描边宽度）自然近乎均匀
                //——本来就是如此。
                Color left = RainColorAt(color, xL, yB, outer, dNear, dFar, trackH, fade, simple, true, reversed);
                Color right = RainColorAt(color, xR, yB, outer, dNear, dFar, trackH, fade, simple, true, reversed);
                AddQuadH(vh, xL, xR, yB, yT, left, right);
                return;
            }
            Color bottom = RainColorAt(color, xL, yB, outer, dNear, dFar, trackH, fade, simple, false, reversed);
            Color top = RainColorAt(color, xL, yT, outer, dNear, dFar, trackH, fade, simple, false, reversed);
            AddQuad(vh, xL, xR, yB, yT, 0f, 1f, 0f, 1f, bottom, top);
        }

        private static Color RainColorAt(Color color, float x, float y, Rect outer, float dNear, float dFar,
            float trackH, float fade, bool simple, bool horizontal, bool reversed)
        {
            if (simple) return color;
            float t = horizontal
                ? (outer.width <= 0.0001f ? 0f : Mathf.Clamp01((x - outer.xMin) / outer.width))
                : (outer.height <= 0.0001f ? 0f : Mathf.Clamp01((y - outer.yMin) / outer.height));
            if (reversed) t = 1f - t;
            float distance = Mathf.Lerp(dNear, dFar, t);
            color.a *= AlphaAtD(distance, trackH - fade, trackH, fade);
            return color;
        }

        private static void AddRainArc(VertexHelper vh, float cx, float cy, float outerRadius, float innerRadius,
            float a0, float a1, Color color, Rect outer, float dNear, float dFar, float trackH, float fade,
            float span, bool simple, bool horizontal = false, bool reversed = false)
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(outerRadius * 0.5f), 3, 12);
            float previousCos = Mathf.Cos(a0);
            float previousSin = Mathf.Sin(a0);
            for (int i = 1; i <= steps; i++)
            {
                float angle = Mathf.Lerp(a0, a1, i / (float)steps);
                float c = Mathf.Cos(angle);
                float s = Mathf.Sin(angle);
                Color c0 = RainColorAt(color, cx + previousCos * outerRadius, cy + previousSin * outerRadius,
                    outer, dNear, dFar, trackH, fade, simple, horizontal, reversed);
                Color c1 = RainColorAt(color, cx + c * outerRadius, cy + s * outerRadius,
                    outer, dNear, dFar, trackH, fade, simple, horizontal, reversed);
                AddRainPoly4(vh,
                    cx + previousCos * innerRadius, cy + previousSin * innerRadius,
                    cx + previousCos * outerRadius, cy + previousSin * outerRadius,
                    cx + c * outerRadius, cy + s * outerRadius,
                    cx + c * innerRadius, cy + s * innerRadius,
                    c0, c0, c1, c1);
                previousCos = c;
                previousSin = s;
            }
        }

        private static void AddRainPoly4(VertexHelper vh, float x0, float y0, float x1, float y1,
            float x2, float y2, float x3, float y3, Color c0, Color c1, Color c2, Color c3)
        {
            int i = vh.currentVertCount;
            UIVertex v = UIVertex.simpleVert;
            v.uv0 = new Vector4(0.5f, 0.5f, 0f, 0f);
            v.position = new Vector3(x0, y0, 0f); v.color = c0; vh.AddVert(v);
            v.position = new Vector3(x1, y1, 0f); v.color = c1; vh.AddVert(v);
            v.position = new Vector3(x2, y2, 0f); v.color = c2; vh.AddVert(v);
            v.position = new Vector3(x3, y3, 0f); v.color = c3; vh.AddVert(v);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }

        private static void DrawDottedRainBody(VertexHelper vh, RawRain rain, Rect r, Color bottom, Color top, bool simple,
            bool horizontal = false, bool reversed = false)
            => DrawDottedRainRect(vh, rain, r, bottom, top, simple, horizontal, reversed);

        /// <summary>Segment a rect along Y into dotLength dots separated by gapLength. The shadow
        /// and outline use this too when the drop is dotted: drawing a SOLID shadow/outline under a
        /// segmented body let the drop's body colour show through every gap, which made the dotted
        /// trail look like one solid bar (the feature silently did nothing whenever a shadow or
        /// outline was on — and the two toggles sit next to each other in the GUI). /
        /// 按 Dot/Gap 沿 Y 切段。阴影与描边在点状时也走这里：实心阴影/描边画在分段本体之下时，
        /// 每个间隙都会漏出本体颜色，点状轨迹看起来仍是一整条实心条——也就是说，只要同时开着
        /// 阴影或描边（两个开关在 GUI 里相邻），点状功能就完全失效。</summary>
        private static void DrawDottedRainRect(VertexHelper vh, RawRain rain, Rect r, Color near, Color far, bool simple,
            bool horizontal = false, bool reversed = false)
        {
            // A dotted body/outline can multiply one logical drop into dozens of quads. Keep a
            // per-drop segment cap independent of the live-drop cap; this bounds malformed or very
            // tall custom tracks without changing the normal short-trail appearance.
            const int maxSegmentsPerDrop = 24;
            float dot = rain.dotLength;
            // The per-node dot/gap values are already NaN-sanitized in EnsureCustomNodes, but this
            // is the point that actually writes the SHARED merged rain mesh, and the obvious guard
            // here does not work: Mathf.Max returns NaN for NaN (0.5 > NaN is false, so it picks the
            // NaN), and neither does Mathf.Clamp. A NaN gap therefore survives the floor, makes
            // `step` NaN, runs the segment loop exactly ONCE (y0 += NaN ends it), and — because
            // `if (y1 <= y0) continue` is FALSE for NaN — reaches AddQuad with NaN y-coordinates,
            // corrupting the entire rain canvas rather than just this drop. Same shared-mesh
            // failure mode as the per-node rain-geometry fix; defended here one level down, at the
            // only place that can poison every other drop's geometry.
            //
            // Note WHICH of the two actually needs it: the call sites gate on
            // `rain.dotted && rain.dotLength > 0.5f`, and `NaN > 0.5f` is false, so a NaN dot is
            // already excluded before reaching this method — gapLength is NOT part of that gate and
            // is the live hole. The dot check is kept only so both operands of the pattern sum are
            // known finite at the one place that writes shared geometry.
            //
            // 每节点点长/间距在 EnsureCustomNodes 里已做 NaN 净化，但这里才是真正写入**共享**雨滴
            // mesh 的地方，而此处最顺手的守卫并不管用：Mathf.Max 对 NaN 返回 NaN（0.5 > NaN 为假，
            // 于是它选中 NaN），Mathf.Clamp 同样如此。NaN 间距因此能穿过这个下限、令 `step` 变
            // NaN、让分段循环**只跑一次**（y0 += NaN 即终止），而 `if (y1 <= y0) continue` 对 NaN
            // 为**假**，于是带着 NaN 坐标抵达 AddQuad，毁掉的是**整块**雨滴画布而不只是这一滴。与按
            // 节点雨滴几何那条修复是同一个共享 mesh 失效形态；此处再守一层，因为只有这里能污染
            // 所有雨滴。
            //
            // 两者中**究竟哪个**真的需要守：三个调用点的门是 `rain.dotted && rain.dotLength >
            // 0.5f`，而 `NaN > 0.5f` 为假，故 NaN 点长在抵达本方法**之前**就已被排除——间距不在
            // 那个门里，才是真正的漏洞。点长那次保留，只是为了在唯一写入共享几何的地方确保求和的
            // 两个操作数都已知有限。
            if (float.IsNaN(dot) || float.IsInfinity(dot)) dot = 12f;
            float gap = rain.gapLength;
            if (float.IsNaN(gap) || float.IsInfinity(gap)) gap = 8f;
            float pattern = Mathf.Max(0.5f, dot + gap);
            float step = pattern;
            // Segment along the TRAIL axis: Y for Up/Down, X for Left/Right. Segmenting a
            // horizontal trail along Y would cut its (thin) thickness into stripes running
            // parallel to the trail — dotted rain would silently look wrong only for Left/Right. /
            // 沿**轨迹轴**切段：上/下用 Y，左/右用 X。把水平轨迹按 Y 切会把它的（很薄的）厚度切成一
            // 条条平行于轨迹的条纹——点状雨滴只在左/右方向下静默出错。
            float axisLen = horizontal ? r.width : r.height;
            float axisMin = horizontal ? r.xMin : r.yMin;
            int maxSegments = Mathf.CeilToInt(axisLen / step);
            if (maxSegments > maxSegmentsPerDrop)
            {
                // Segment cap: scale the DOT too, not just the spacing — keeping the original
                // dotLength while stretching the step would blow the gaps far past gapLength and
                // silently produce a much sparser trail than the user configured.
                // 段数上限：点长也要同比缩放——只放大步长而保留原点长会把间距拉得远超 gapLength，
                // 实际轨迹比用户设置的稀疏得多且无任何提示。
                float shrink = (axisLen / maxSegmentsPerDrop) / pattern;
                step = axisLen / maxSegmentsPerDrop;
                dot = Mathf.Max(0.5f, dot * shrink);
            }
            for (float d0 = axisMin; d0 < axisMin + axisLen; d0 += step)
            {
                float d1 = Mathf.Min(axisMin + axisLen, d0 + dot);
                if (d1 <= d0) continue;
                Color c0 = TrailColorAt(r, d0, d0, near, far, rain, simple, horizontal, reversed);
                Color c1 = TrailColorAt(r, d1, d1, near, far, rain, simple, horizontal, reversed);
                if (horizontal) AddQuadH(vh, d0, d1, r.yMin, r.yMax, c0, c1);
                else AddQuad(vh, r.xMin, r.xMax, d0, d1, 0f, 1f, 0f, 1f, c0, c1);
            }
        }

        /// <summary>Colour at a position along the trail axis, including the two-colour ramp and the
        /// top-fade alpha. `x` is meaningful for horizontal trails, `y` for vertical ones; the
        /// unused coordinate is ignored. `reversed` measures the ramp from the HIGH end because
        /// there the rect's low coordinate is the far (head) end. /
        /// 轨迹轴上的某点颜色，含双色渐变与顶部渐隐。水平轨迹用 `x`、垂直用 `y`（另一坐标忽略）。
        /// `reversed` 时改从**高端**量起：此时矩形低坐标端才是远端（头部）。</summary>
        private static Color TrailColorAt(Rect body, float x, float y, Color near, Color far, RawRain rain, bool simple,
            bool horizontal, bool reversed)
        {
            float t = horizontal
                ? (body.width <= 0.0001f ? 0f : Mathf.Clamp01((x - body.xMin) / body.width))
                : (body.height <= 0.0001f ? 0f : Mathf.Clamp01((y - body.yMin) / body.height));
            if (reversed) t = 1f - t;
            Color color = Color.Lerp(near, far, t);
            if (!simple && rain.trackHeight > 0.5f && rain.fadePx > 0.5f)
            {
                float distance = Mathf.Lerp(rain.dNear, rain.dFar, t);
                color.a *= AlphaAtD(distance, rain.trackHeight - rain.fadePx, rain.trackHeight, rain.fadePx);
            }
            return color;
        }

        private static void DrawRainQuad(VertexHelper vh, float xL, float xR, float yB, float yT, float h, Color colNear, Color colFar,
            float dNear, float dFar, float trackH, float fade, float span, bool simple,
            bool horizontal = false, bool reversed = false)
        {
            // The ramp runs NEAR → FAR. `reversed` means the rect's LOW coordinate end is the FAR
            // end (Down, Left), so the two colours swap and the fade boundary is measured from the
            // high end instead. Flipping only the colours would still leave the fade split at the
            // wrong end; flipping only the split would paint the wrong side. Both flip together. /
            // 渐变沿**近→远**展开。`reversed` 表示矩形低坐标端是**远**端（下、左），故两色互换、
            // 渐隐边界改从高端量起。只翻颜色会让切分留在错端，只翻切分会涂错一侧——两者必须一起翻。
            Color cLow = reversed ? colFar : colNear;
            Color cHigh = reversed ? colNear : colFar;
            if (simple)
            {
                if (horizontal) AddQuadH(vh, xL, xR, yB, yT, cLow, cHigh);
                else AddQuad(vh, xL, xR, yB, yT, 0f, 1f, 0f, 1f, cLow, cHigh);
                return;
            }

            float fadeStartD = trackH - fade;
            float aNear = AlphaAtD(dNear, fadeStartD, trackH, fade);
            float aFar = AlphaAtD(dFar, fadeStartD, trackH, fade);
            Color cLowA = WithA(cLow, reversed ? aFar : aNear);
            Color cHighA = WithA(cHigh, reversed ? aNear : aFar);
            bool crosses = dNear < fadeStartD && dFar > fadeStartD;
            if (!crosses)
            {
                // reverseFade was always false in the old renderer / 旧渲染器 reverseFade 恒为 false
                if (horizontal) AddQuadH(vh, xL, xR, yB, yT, cLowA, cHighA);
                else AddQuad(vh, xL, xR, yB, yT, 0f, 1f, 0f, 1f, cLowA, cHighA);
                return;
            }
            float t = (fadeStartD - dNear) / span;
            Color cMid = Color.Lerp(colNear, colFar, t);
            // Fraction of the rect measured from its LOW end to the fade boundary: the boundary is
            // `t` away from the near end, which IS the high end when reversed. /
            // 从矩形低端量到渐隐边界的比例：边界距近端 `t`，而 reversed 时近端正是高端。
            float tLow = reversed ? 1f - t : t;
            if (horizontal)
            {
                // Split the quad along X at the fade boundary: the colour ramp runs left→right. /
                // 在渐隐边界处沿 X 切分四边形：颜色渐变沿左→右展开。
                float xMid = xL + tLow * h;
                AddQuadH(vh, xL, xMid, yB, yT, cLowA, cMid);
                AddQuadH(vh, xMid, xR, yB, yT, cMid, cHighA);
            }
            else
            {
                float yMid = yB + tLow * h;
                AddQuad(vh, xL, xR, yB, yMid, 0f, 1f, 0f, 0.5f, cLowA, cMid);
                AddQuad(vh, xL, xR, yMid, yT, 0f, 1f, 0.5f, 1f, cMid, cHighA);
            }
        }

        /// <summary>Axis-swapped quad for horizontal (Left/Right) trails: same four corners as
        /// AddQuad but the colour ramp runs left→right instead of bottom→top, so the trail's
        /// gradient and top-fade follow the X axis the drop actually travels along. /
        /// 水平（左/右）轨迹的轴交换四边形：四角与 AddQuad 相同，但颜色渐变沿左→右而非底→顶，
        /// 使轨迹渐变与顶部渐隐跟随雨滴真实行进的 X 轴。</summary>
        private static void AddQuadH(VertexHelper vh, float x0, float x1, float y0, float y1, Color left, Color right)
        {
            int i = vh.currentVertCount;
            UIVertex vert = UIVertex.simpleVert;
            Vector4 centreUv = new Vector4(0.5f, 0.5f, 0f, 0f);
            vert.position = new Vector3(x0, y0, 0f); vert.uv0 = centreUv; vert.color = left; vh.AddVert(vert);
            vert.position = new Vector3(x1, y0, 0f); vert.uv0 = centreUv; vert.color = right; vh.AddVert(vert);
            vert.position = new Vector3(x1, y1, 0f); vert.uv0 = centreUv; vert.color = right; vh.AddVert(vert);
            vert.position = new Vector3(x0, y1, 0f); vert.uv0 = centreUv; vert.color = left; vh.AddVert(vert);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }

        private static Color WithA(Color c, float mul)
        {
            c.a *= mul;
            return c;
        }

        private static float AlphaAtD(float d, float fadeStartD, float trackH, float fade)
        {
            if (d <= fadeStartD) return 1f;
            if (d >= trackH) return 0f;
            return (trackH - d) / fade;
        }

        internal static void AddQuad(VertexHelper vh, float x0, float x1, float y0, float y1,
            float u0, float u1, float v0, float v1, Color bot, Color top)
        {
            int i = vh.currentVertCount;
            UIVertex vert = UIVertex.simpleVert;
            Vector4 centreUv = new Vector4((u0 + u1) * 0.5f, (v0 + v1) * 0.5f, 0f, 0f);
            vert.position = new Vector3(x0, y0, 0f);
            vert.uv0 = centreUv;
            vert.color = bot;
            vh.AddVert(vert);
            vert.position = new Vector3(x1, y0, 0f);
            vert.uv0 = centreUv;
            vh.AddVert(vert);
            vert.position = new Vector3(x1, y1, 0f);
            vert.uv0 = centreUv;
            vert.color = top;
            vh.AddVert(vert);
            vert.position = new Vector3(x0, y1, 0f);
            vert.uv0 = centreUv;
            vh.AddVert(vert);
            vh.AddTriangle(i, i + 1, i + 2);
            vh.AddTriangle(i, i + 2, i + 3);
        }
    }

    /// <summary>
    /// Ghost rain sprite bodies — reproduced as uGUI Image.Type.Tiled did: with a bordered sprite
    /// (the 11px 9-slice GhostRain border) the inner region and border strips tile separately;
    /// borderless standalone textures use a single Repeat-wrapped UV quad / 鬼雨贴图本体——按
    /// uGUI Image.Type.Tiled 的行为复刻：带边框贴图（GhostRain 的 11px 九宫格）内区与边框条
    /// 分别平铺；无边框独立贴图用 Repeat wrap 的单四边形 UV 平铺
    /// </summary>
    public class GhostRainLayer : MaskableGraphic
    {
        internal RainSystem System;

        /// <summary>True when the last rebuild actually emitted ghost geometry. Latched so the
        /// frame that removes the FINAL ghost drop still dirties this layer, otherwise its mesh
        /// would keep the last ghost drawn forever.
        /// 上一次重建是否真的发出了鬼雨几何。作为闩锁，使移除**最后一滴**鬼雨的那一帧仍会标脏
        /// 本层，否则其 mesh 会把最后一滴鬼雨永久留在画面上。</summary>
        internal bool HadGhostGeometry;

        public Sprite Sprite { get; set; }

        public override Texture mainTexture => Sprite != null ? Sprite.texture : base.mainTexture;

        public GhostRainLayer()
        {
            raycastTarget = false;
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (Sprite == null) return;
            // The latch must reflect whether THIS rebuild really emitted geometry. Setting it
            // unconditionally would re-arm on every rebuild and the MarkDirty guard above would
            // never stop skipping again — which is the exact waste this latch exists to remove.
            // Start pessimistic and raise it only when a quad is actually produced.
            // 闩锁必须反映**本次重建**是否真的发出了几何。无条件置位会在每次重建时重新武装，
            // 使上方的 MarkDirty 守卫再也停不下来——恰是它要消除的那种浪费。先按悲观置位，
            // 只有真正产出四边形时才抬起。
            HadGhostGeometry = false;
            Key[] keys = System != null ? System.Keys : null;
            if (keys == null) return;
            Rect tr = Sprite.textureRect;
            Texture tex = Sprite.texture;
            float tw = tex.width;
            float th = tex.height;
            Vector4 border = Sprite.border;
            // The GhostRain sprite carries an 11px 9-slice border (matching the Unity import
            // settings), and the old ghost Image used Image.Type.Tiled — which for a bordered
            // sprite tiles the INNER region and the border strips separately (per-tile quads),
            // not the whole texture. Port that layout here; whole-texture tiling (single Repeat
            // quad or per-tile quads) only applies to borderless sprites.
            // GhostRain 贴图带 11px 九宫格边框（与 Unity 导入设置一致），旧版鬼雨 Image 是
            // Image.Type.Tiled——带边框的 sprite 只平铺内侧区域，边框条单独按方向平铺
            //（逐四边形），而不是整张贴图重复。此处移植该布局；整图平铺（Repeat 单四边形
            // 或逐块四边形）仅适用于无边框贴图。
            bool hasBorder = border.x > 0f || border.y > 0f || border.z > 0f || border.w > 0f;
            // Outer = textureRect, inner = inset by border (uGUI DataUtility outer/inner UV) /
            // 外圈 = textureRect，内圈 = 内缩边框（uGUI DataUtility 的 outer/inner UV）
            float oL = tr.x / tw, oR = tr.xMax / tw, oB = tr.y / th, oT = tr.yMax / th;
            float iL = (tr.x + border.x) / tw, iR = (tr.xMax - border.z) / tw;
            float iB = (tr.y + border.y) / th, iT = (tr.yMax - border.w) / th;
            // uGUI scales tile size and border by 100/ppu (tile = innerRect * referencePixelsPerUnit
            // / spritePixelsPerUnit): a 200-ppu sprite tiles at half reference size. Inert at ppu
            // 100 — the factor direction was previously inverted but never mattered because every
            // shipped sprite is 100 ppu. / uGUI 按 100/ppu 缩放平铺尺寸与边框(tile = 内区 * 参
            // 考像素单位 / 精灵像素单位):200 ppu 的精灵按参考尺寸的一半平铺。ppu=100 时无变化
            // ——此前系数方向写反,因随包贴图均为 100 ppu 而未暴露。
            float ppuScale = Sprite.pixelsPerUnit > 0f ? 100f / Sprite.pixelsPerUnit : 1f;
            float tileW = (tr.width - border.x - border.z) * ppuScale;
            float tileH = (tr.height - border.y - border.w) * ppuScale;
            // Borders swallowing the whole sprite: uGUI stretches a single tile instead of tiling /
            // 边框吞掉整张贴图时，uGUI 用单块拉伸而非平铺
            bool degenerate = hasBorder && (tileW <= 0f || tileH <= 0f);
            // The STANDALONE (borderless) path computes its UVs inline as r.width / tileW, and
            // AddTiled divides by tileW too. A non-positive or non-finite tile size therefore
            // yields Inf/NaN UVs even though the drop's own rect is perfectly finite — the finite
            // guard above cannot catch it, because the poison is introduced here, not in the rect.
            // A sprite small enough (or ppu-large enough) to hit this is unusual, but the ghost
            // sprite is a user-replaceable file, so it is reachable. Floor it once, here, so every
            // consumer of tileW/tileH below is safe.
            // **独立**（无边框）路径的内联 UV 按 r.width / tileW 计算，AddTiled 也除以 tileW。
            // 故非正或非有限的平铺尺寸会产出 Inf/NaN UV，**尽管**雨滴矩形本身完全有限——上方的
            // 有限守卫抓不到它，因为毒是在这里产生的、不在矩形里。够小（或 ppu 够大）的精灵才会
            // 触发，但这不寻常；鬼雨精灵是**用户可替换**的文件，故确实可达。在此一次性兜住，
            // 使下方每个 tileW/tileH 的使用者都安全。
            if (!RainLayer.IsFinite(tileW) || tileW <= 0f) tileW = tr.width > 0f ? tr.width : 1f;
            if (!RainLayer.IsFinite(tileH) || tileH <= 0f) tileH = tr.height > 0f ? tr.height : 1f;
            bool standalone = !hasBorder && RainLayer.IsStandaloneRect(Sprite);
            RainSystem rainSystem = System;
            List<int> activeKeys = rainSystem != null ? rainSystem.ActiveKeys : null;
            if (activeKeys == null) return;
            for (int a = 0; a < activeKeys.Count; a++)
            {
                int keyIndex = activeKeys[a];
                if ((uint)keyIndex >= (uint)keys.Length) continue;
                Key key = keys[keyIndex];
                if (key == null || key.rainList.Count == 0) continue;
                for (int d = 0; d < key.rainList.Count; d++)
                {
                    RawRain rain = key.rainList[d];
                    if (rain.removed || !rain.isGhost) continue;
                    Rect r = rain.rect;
                    // Same hazard DrawDrop guards against, and it was missing HERE: this layer
                    // reads the SAME rain.rect from the SAME shared rainList. The old test
                    // (`r.width <= 0f`) is false for NaN AND for +/-Inf, so a single poisoned
                    // drop wrote 8 NaN vertices per quad into the shared ghost mesh and corrupted
                    // the ENTIRE ghost canvas (every key, every drop) on every frame for the rest
                    // of the session. AddTiled is worse: its `while (x < r.xMax - 0.01f)` loop has
                    // no iteration bound, so an infinite xMax never terminates and grows the
                    // VertexHelper until the process hangs.
                    // 与 DrawDrop 相同、而此处**缺失**的防护：这一层读的是**同一个**共享 rainList
                    // 里的**同一个** rain.rect。旧判据（`r.width <= 0f`）对 NaN 与 ±Inf 都为假，
                    // 故一滴坏雨滴就会把每帧 8 个 NaN 顶点写进共享鬼雨 mesh，把**整块**鬼雨画布
                    // （所有键、所有雨滴）在本次会话余下时间里全部搞坏。AddTiled 更糟：它的
                    // `while (x < r.xMax - 0.01f)` 循环**没有迭代上限**，xMax 为无穷时永不退出，
                    // 不断撑大 VertexHelper 直到进程卡死。
                    if (!RainLayer.IsFiniteRect(r) || !RainLayer.IsFinite(rain.scaleF)) continue;
                    // A real drop is about to be emitted — raise the latch so the NEXT MarkDirty
                    // knows this layer still has content worth clearing.
                    // 即将发出真实雨滴——抬起闩锁，使下一次 MarkDirty 知道本层仍有需清空的内容。
                    HadGhostGeometry = true;
                    Color c = rain.mainColor;
                    c.a *= rain.alpha;
                    if (hasBorder && !degenerate)
                    {
                        DrawTiledWithBorder(vh, r, c, border.x * ppuScale, border.z * ppuScale, border.y * ppuScale, border.w * ppuScale,
                            tileW, tileH, oL, iL, iR, oR, oB, iB, iT, oT);
                    }
                    else if (hasBorder)
                    {
                        RainLayer.AddQuad(vh, r.xMin, r.xMax, r.yMin, r.yMax, oL, oR, oB, oT, c, c);
                    }
                    else if (standalone)
                    {
                        // UV anchored bottom-left, tiling per tile size — matches the old tiled Image /
                        // UV 锚定左下，按贴图尺寸平铺——与旧平铺 Image 一致
                        RainLayer.AddQuad(vh, r.xMin, r.xMax, r.yMin, r.yMax,
                            0f, r.width / tileW, 0f, r.height / tileH, c, c);
                    }
                    else
                    {
                        // Packed borderless sprite: per-tile quads keep UVs inside the sprite rect
                        // (no atlas bleed, no Repeat needed) / 打包无边框贴图：逐平铺四边形把
                        // UV 限制在贴图矩形内（不越界采样图集，也无需 Repeat）
                        AddTiled(vh, r, tr, tw, th, tileW, tileH, c);
                    }
                }
            }
        }

        /// <summary>
        /// Port of uGUI Image.Type.Tiled for a bordered sprite (GenerateTiledSpriteToVertexBuffer):
        /// the border strips tile only along their own direction — sampled from the inner band —
        /// and the center region tiles as whole quads clipped at the edge. Borders squeeze
        /// proportionally when the rect is smaller than the combined borders (GetAdjustedBorders).
        /// uGUI Image.Type.Tiled 带边框 sprite 的移植（GenerateTiledSpriteToVertexBuffer）：
        /// 边框条只沿自身方向平铺——取样自内侧带——中心区域整块平铺并在边缘裁剪。
        /// 矩形容不下两侧边框时按比例压缩（GetAdjustedBorders）。
        /// </summary>
        private static void DrawTiledWithBorder(VertexHelper vh, Rect r, Color c,
            float bx, float bz, float by, float bw, float tileW, float tileH,
            float oL, float iL, float iR, float oR, float oB, float iB, float iT, float oT)
        {
            float cbx = bx + bz;
            if (r.width < cbx && cbx > 0f) { float k = r.width / cbx; bx *= k; bz *= k; }
            float cby = by + bw;
            if (r.height < cby && cby > 0f) { float k = r.height / cby; by *= k; bw *= k; }
            float xMin = bx, xMax = r.width - bz, yMin = by, yMax = r.height - bw;
            if (xMax < xMin) xMax = xMin;
            if (yMax < yMin) yMax = yMin;
            long nTilesW = (long)Mathf.Ceil((xMax - xMin) / tileW);
            long nTilesH = (long)Mathf.Ceil((yMax - yMin) / tileH);
            // A user-replaceable ghost sprite and a large custom node can otherwise turn one drop
            // into an unbounded nested tile loop. The border path historically had no cap at all;
            // fail soft to one stretched quad when the requested tile count is pathological. This
            // preserves a visible ghost and, more importantly, makes one malformed asset unable to
            // freeze the main thread or allocate millions of vertices.
            // 可替换鬼雨贴图配合大自定义节点会把一滴雨变成无界嵌套平铺循环。带边框路径此前完全没
            // 上限；遇到病态平铺数量时退化为一个拉伸四边形，保留可见鬼雨并防止主线程卡死/顶点爆炸。
            const long maxGhostTilesPerAxis = 32;
            // A zero/negative tile count means the borders swallowed the whole rect (the default
            // GhostRain border is 11 px on every side, 22 px total), which happens routinely while
            // a drop is still growing. That is NOT a reason to skip the method: the four corner
            // quads below are the ONLY way this drop gets drawn in that state, and returning early
            // made the ghost vanish outright — dropping its first frames on every spawn. The
            // center tiling and the border strips need positive counts; the corners never do.
            // 平铺数为零/负说明边框吃掉了整个矩形（默认鬼雨四边各 11px、合计 22px），而这在雨滴
            // 生长过程中是常事。这**不是**跳过整个方法的理由：下面的四角四边形在该状态下是这滴
            // 雨唯一的画法，提前返回会让鬼雨彻底消失——每次生成都丢掉最初几帧。中心平铺与边框
            // 条带需要正的平铺数，四角不需要。
            bool hasTiles = nTilesW > 0 && nTilesH > 0;
            if (hasTiles && (nTilesW > maxGhostTilesPerAxis || nTilesH > maxGhostTilesPerAxis))
            {
                RainLayer.AddQuad(vh, r.xMin, r.xMax, r.yMin, r.yMax, oL, oR, oB, oT, c, c);
                return;
            }
            float px = r.xMin, py = r.yMin;

            if (hasTiles)
            {
            // Center tiles / 中心平铺
            for (long j = 0; j < nTilesH; j++)
            {
                float y1 = yMin + j * tileH, y2 = y1 + tileH;
                float vClip = iT;
                if (y2 > yMax) { vClip = iB + (iT - iB) * (yMax - y1) / (y2 - y1); y2 = yMax; }
                for (long i = 0; i < nTilesW; i++)
                {
                    float x1 = xMin + i * tileW, x2 = x1 + tileW;
                    float uClip = iR;
                    if (x2 > xMax) { uClip = iL + (iR - iL) * (xMax - x1) / (x2 - x1); x2 = xMax; }
                    RainLayer.AddQuad(vh, px + x1, px + x2, py + y1, py + y2, iL, uClip, iB, vClip, c, c);
                }
            }
            // Left and right border columns, tiled vertically from the inner band / 左右边框列，
            // 取样内侧带纵向平铺
            for (long j = 0; j < nTilesH; j++)
            {
                float y1 = yMin + j * tileH, y2 = y1 + tileH;
                float vClip = iT;
                if (y2 > yMax) { vClip = iB + (iT - iB) * (yMax - y1) / (y2 - y1); y2 = yMax; }
                RainLayer.AddQuad(vh, px, px + xMin, py + y1, py + y2, oL, iL, iB, vClip, c, c);
                RainLayer.AddQuad(vh, px + xMax, px + r.width, py + y1, py + y2, iR, oR, iB, vClip, c, c);
            }
            // Bottom and top border rows, tiled horizontally from the inner band / 上下边框行，
            // 取样内侧带横向平铺
            for (long i = 0; i < nTilesW; i++)
            {
                float x1 = xMin + i * tileW, x2 = x1 + tileW;
                float uClip = iR;
                if (x2 > xMax) { uClip = iL + (iR - iL) * (xMax - x1) / (x2 - x1); x2 = xMax; }
                RainLayer.AddQuad(vh, px + x1, px + x2, py, py + yMin, iL, uClip, oB, iB, c, c);
                RainLayer.AddQuad(vh, px + x1, px + x2, py + yMax, py + r.height, iL, uClip, iT, oT, c, c);
            }
            }
            // Corners / 四角
            RainLayer.AddQuad(vh, px, px + xMin, py, py + yMin, oL, iL, oB, iB, c, c);
            RainLayer.AddQuad(vh, px + xMax, px + r.width, py, py + yMin, iR, oR, oB, iB, c, c);
            RainLayer.AddQuad(vh, px, px + xMin, py + yMax, py + r.height, oL, iL, iT, oT, c, c);
            RainLayer.AddQuad(vh, px + xMax, px + r.width, py + yMax, py + r.height, iR, oR, iT, oT, c, c);
        }

        private static void AddTiled(VertexHelper vh, Rect r, Rect tr, float texW, float texH, float tileW, float tileH, Color c)
        {
            float u0 = tr.x / texW, u1 = tr.xMax / texW;
            float v0 = tr.y / texH, v1 = tr.yMax / texH;
            // These loops advance by tileW/tileH, so a non-positive or non-finite step would spin
            // forever (adding 4 vertices per iteration until the process dies). Callers reject
            // non-finite rects, but a zero/sprite-scale-degenerate tile size is reachable on its
            // own, so bound the iteration count explicitly rather than trusting the geometry.
            // 这些循环按 tileW/tileH 前进，故步长非正或非有限就会**永远**自旋（每次迭代加 4 个
            // 顶点直到进程死）。调用方已拒绝非有限矩形，但步长为零/因精灵缩放退化是可以单独发生
            // 的，故显式限制迭代次数而不是信任几何。
            if (!RainLayer.IsFinite(tileW) || tileW <= 0f) tileW = r.width > 0f ? r.width : 1f;
            if (!RainLayer.IsFinite(tileH) || tileH <= 0f) tileH = r.height > 0f ? r.height : 1f;
            const int maxTilesPerAxis = 256;
            float x = r.xMin;
            for (int tx = 0; x < r.xMax - 0.01f && tx < maxTilesPerAxis; tx++)
            {
                float wTile = Mathf.Min(tileW, r.xMax - x);
                float uu1 = u0 + (u1 - u0) * (wTile / tileW);
                float y = r.yMin;
                for (int ty = 0; y < r.yMax - 0.01f && ty < maxTilesPerAxis; ty++)
                {
                    float hTile = Mathf.Min(tileH, r.yMax - y);
                    float vv1 = v0 + (v1 - v0) * (hTile / tileH);
                    RainLayer.AddQuad(vh, x, x + wTile, y, y + hTile, u0, uu1, v0, vv1, c, c);
                    // Advance by the (possibly clipped) tile size, exactly as before — using the
                    // full tile size here would skip pixels on the clipped edge tile.
                    // 按（可能被裁剪的）平铺尺寸前进，与此前完全一致——用完整尺寸会在被裁剪的
                    // 边缘块上跳过像素。
                    y += hTile;
                }
                x += wTile;
            }
        }
    }
}
