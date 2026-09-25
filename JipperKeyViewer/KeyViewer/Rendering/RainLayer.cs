// Merged rain rendering / 雨滴合并渲染
// RainLayer draws every rain drop into one white-texture mesh: normal drop bodies plus the ghost
// drops' shadow/outline quads. GhostRainLayer draws the ghost sprite bodies, sharing the per-key
// press scales owned by RainLayer. Both read the RawRain state maintained by RainSystem, replacing
// the per-key RainLine canvases and the pooled Rain GameObjects — no per-drop RectTransform writes,
// no SetSiblingIndex, one rebuild per layer per frame while raining.
// RainLayer 把所有雨滴画进一个白贴图 mesh：普通雨滴本体 + 鬼雨的阴影/描边四边形。GhostRainLayer
// 画鬼雨贴图本体，与 RainLayer 共享每键按压缩放。两者读取 RainSystem 维护的 RawRain 状态，
// 取代每键 RainLine 画布与 Rain 对象池——无逐雨滴 RectTransform 写入、无 SetSiblingIndex，
// 下雨期间每层每帧只重建一次。

using UnityEngine;
using UnityEngine.UI;

using JipperKeyViewer.KeyViewer.Rain;

namespace JipperKeyViewer.KeyViewer.Rendering

{
    /// <summary>
    /// Solid-color rain quads: normal drop bodies + ghost shadow/outline / 纯色雨滴四边形：普通本体 + 鬼雨阴影/描边
    /// </summary>
    public class RainLayer : MaskableGraphic
    {
        /// <summary>Rain system providing the drop state / 提供雨滴状态的雨滴系统</summary>
        internal RainSystem System;
        /// <summary>Per-key rain press scale (EnablePressAnimationOnRain), shared with the ghost layer / 每键雨滴按压缩放（雨滴跟随动画），与鬼雨层共享</summary>
        private float[] rainScales;

        private GhostRainLayer ghostLayer;

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

        /// <summary>Set the ghost layer's sprite (null = ghost bodies hidden) / 设置鬼雨层贴图（null = 鬼雨本体不显示）</summary>
        public void SetSprite(Sprite sprite)
        {
            // Single-quad UV tiling needs Repeat — only safe to flip on a standalone texture; a
            // packed/atlas sprite keeps its wrap mode and falls back to per-tile quads.
            // 单四边形 UV 平铺需要 Repeat——仅在独立贴图上翻转才安全；打包/图集贴图保持原 wrap，
            // 渲染时回退为逐平铺四边形。
            if (sprite != null && sprite.texture != null && IsStandaloneRect(sprite))
                sprite.texture.wrapMode = TextureWrapMode.Repeat;
            if (ghostLayer != null) ghostLayer.Sprite = sprite;
            SetVerticesDirty();
            SetMaterialDirty();
            if (ghostLayer != null)
            {
                ghostLayer.SetVerticesDirty();
                ghostLayer.SetMaterialDirty();
            }
        }

        /// <summary>Whether the sprite's rect covers its whole texture (no atlas packing) / 贴图矩形是否覆盖整张贴图（无图集打包）</summary>
        internal static bool IsStandaloneRect(Sprite sprite)
        {
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
            if (ghostLayer != null) ghostLayer.SetVerticesDirty();
        }

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            Key[] keys = System != null ? System.Keys : null;
            if (keys == null) return;
            // Ghost bodies normally render in GhostRainLayer; if the ghost sprite failed to load
            // (missing bundle asset / PNG), fall back to solid ghost-colored quads here (the old
            // no-sprite path drew opaque white — ghost color is the deliberate improvement).
            // 鬼雨本体通常画在 GhostRainLayer；鬼雨贴图加载失败（bundle 缺资源 / PNG 缺失）时
            // 在此退化为鬼雨色纯色四边形（旧版无贴图路径画不透明白色——用鬼雨色是有意改进）。
            bool ghostFallback = ghostLayer == null || ghostLayer.Sprite == null;
            for (int i = 0; i < keys.Length; i++)
            {
                Key key = keys[i];
                if (key == null || key.rainList.Count == 0) continue;
                // Old per-key stacking: normals below, ghost quads above (SetSiblingIndex reserved
                // the +1 slot for ghosts) — two passes reproduce that exactly.
                // 旧版每键内普通雨在下、鬼雨（含阴影/描边）在上（SetSiblingIndex 为鬼雨保留 +1
                // 槽位）——两趟绘制精确复现。
                for (int d = 0; d < key.rainList.Count; d++)
                {
                    RawRain rain = key.rainList[d];
                    if (rain.removed || rain.isGhost) continue;
                    DrawDrop(vh, rain, drawMain: true);
                }
                for (int d = 0; d < key.rainList.Count; d++)
                {
                    RawRain rain = key.rainList[d];
                    if (rain.removed || !rain.isGhost) continue;
                    if (!ghostFallback && !rain.shadowEnabled && !rain.outlineEnabled) continue;
                    DrawDrop(vh, rain, drawMain: ghostFallback);
                }
            }
        }

        /// <summary>Emit one drop's shadow/outline/(main) quads with the trail gradient. The
        /// body carries a bottom→top two-color gradient (bottom-to-top two-color gradient); the
        /// shadow/outline stay single-color. / 输出一滴雨的阴影/描边/(本体)四边形及轨迹渐变。
        /// 本体携带底→顶双色渐变（底→顶双色渐变）；阴影/描边保持单色。</summary>
        private static void DrawDrop(VertexHelper vh, RawRain rain, bool drawMain)
        {
            Rect r = rain.rect;
            if (r.width <= 0f || r.height <= 0f) return;
            // NaN fails every `<=` comparison, so a single poisoned rect/scale would slip past the
            // guard above and write NaN vertices into the SHARED mesh — one bad drop would then
            // corrupt the whole rain canvas. / NaN 能通过所有 `<=` 比较，坏矩形/缩放会把 NaN 顶点
            // 写进共享 mesh，一个坏雨滴就会污染整块雨滴画布。
            if (float.IsNaN(r.xMin) || float.IsNaN(r.yMin) || float.IsNaN(r.width) || float.IsNaN(r.height)
                || float.IsNaN(rain.scaleF) || float.IsInfinity(rain.scaleF)) return;
            float baseA = rain.isGhost && !drawMain ? rain.alpha : rain.mainColor.a * rain.alpha;
            float h = r.height;
            float span = rain.dFar - rain.dNear;
            bool simple = rain.fadePx <= 0.5f || rain.trackHeight <= 0.5f || span <= 0.0001f;
            float sf = rain.scaleF;

            if (rain.shadowEnabled)
            {
                Color sc = rain.shadowColor;
                sc.a *= baseA;
                if (rain.dotted && rain.dotLength > 0.5f)
                    DrawDottedRainRect(vh, rain, new Rect(r.xMin + rain.shadowOffsetX * sf, r.yMin + rain.shadowOffsetY * sf,
                        r.width, r.height), sc, sc, simple);
                else
                    DrawRainQuad(vh, r.xMin + rain.shadowOffsetX * sf, r.xMax + rain.shadowOffsetX * sf,
                        r.yMin + rain.shadowOffsetY * sf, r.yMax + rain.shadowOffsetY * sf, h, sc, sc,
                        rain.dNear, rain.dFar, rain.trackHeight, rain.fadePx, span, simple);
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
                        DrawDottedRainRect(vh, rain, new Rect(r.xMin - ow, r.yMin - ow, r.width + ow * 2f, r.height + ow * 2f), oc, oc, simple);
                    }
                    else if (rain.outlineCornerRadius > 0.5f)
                        DrawRoundedRainOutline(vh, r, ow, rain.outlineCornerRadius, rain.outlineSides, oc, rain.dNear, rain.dFar,
                            rain.trackHeight, rain.fadePx, span, simple);
                    else
                        DrawRainOutlineBySide(vh, r, ow, rain.outlineSides, oc, rain.dNear, rain.dFar,
                            rain.trackHeight, rain.fadePx, span, simple);
                }
            }
            if (drawMain)
            {
                Color cb = rain.mainColor; cb.a = baseA;
                Color ct = rain.ColorTop; ct.a = baseA;
                if (rain.dotted && rain.dotLength > 0.5f)
                    DrawDottedRainBody(vh, rain, r, cb, ct, simple);
                else
                    DrawRainQuad(vh, r.xMin, r.xMax, r.yMin, r.yMax, h, cb, ct,
                        rain.dNear, rain.dFar, rain.trackHeight, rain.fadePx, span, simple);
            }
        }

        /// <summary>Trail-gradient quad, ported verbatim from the old RainGraphic and extended to
        /// a bottom→top two-color body (single-color callers pass the same color twice). /
        /// 轨迹渐变四边形，自旧 RainGraphic 原样移植并扩展为底→顶双色（单色调用方传同一颜色
        /// 两次）。</summary>
        private static void DrawRainOutlineBySide(VertexHelper vh, Rect body, float width, int sides, Color color,
            float dNear, float dFar, float trackH, float fade, float span, bool simple)
        {
            bool vertical = sides != 2;
            bool horizontal = sides != 1;
            if (vertical && horizontal)
            {
                DrawRainQuad(vh, body.xMin - width, body.xMax + width, body.yMin - width, body.yMax + width,
                    body.height + width * 2f, color, color, dNear, dFar, trackH, fade, span, simple);
                return;
            }
            Rect outer = new Rect(body.xMin - width, body.yMin - width,
                body.width + width * 2f, body.height + width * 2f);
            if (horizontal)
            {
                DrawRainSolidRect(vh, outer.xMin, outer.xMax, outer.yMin, outer.yMin + width, color, outer,
                    dNear, dFar, trackH, fade, span, simple);
                DrawRainSolidRect(vh, outer.xMin, outer.xMax, outer.yMax - width, outer.yMax, color, outer,
                    dNear, dFar, trackH, fade, span, simple);
            }
            if (vertical)
            {
                DrawRainSolidRect(vh, outer.xMin, outer.xMin + width, outer.yMin, outer.yMax, color, outer,
                    dNear, dFar, trackH, fade, span, simple);
                DrawRainSolidRect(vh, outer.xMax - width, outer.xMax, outer.yMin, outer.yMax, color, outer,
                    dNear, dFar, trackH, fade, span, simple);
            }
        }

        private static void DrawRoundedRainOutline(VertexHelper vh, Rect body, float width, float requestedRadius, int sides,
            Color color, float dNear, float dFar, float trackH, float fade, float span, bool simple)
        {
            float halfW = Mathf.Min(width, Mathf.Min(body.width, body.height) * 0.5f);
            if (halfW <= 0.01f)
            {
                DrawRainOutlineBySide(vh, body, width, sides, color, dNear, dFar, trackH, fade, span, simple);
                return;
            }
            Rect outer = new Rect(body.xMin - halfW, body.yMin - halfW,
                body.width + halfW * 2f, body.height + halfW * 2f);
            float radius = Mathf.Clamp(requestedRadius, 0f, Mathf.Min(outer.width, outer.height) * 0.5f);
            if (radius <= 0.01f)
            {
                DrawRainOutlineBySide(vh, new Rect(outer.xMin + halfW, outer.yMin + halfW,
                    outer.width - halfW * 2f, outer.height - halfW * 2f), halfW, sides, color,
                    dNear, dFar, trackH, fade, span, simple);
                return;
            }
            DrawRoundedRainOutlineWithRadius(vh, outer, radius, halfW, sides, color, dNear, dFar, trackH, fade, span, simple);
        }

        private static void DrawRoundedRainOutlineWithRadius(VertexHelper vh, Rect outer, float radius, float width,
            int sides, Color color, float dNear, float dFar, float trackH, float fade, float span, bool simple)
        {
            float inner = Mathf.Max(0f, radius - width);
            bool vertical = sides != 2;
            bool horizontal = sides != 1;
            if (horizontal)
            {
                DrawRainSolidRect(vh, outer.xMin + radius, outer.xMax - radius, outer.yMin, outer.yMin + width,
                    color, outer, dNear, dFar, trackH, fade, span, simple);
                DrawRainSolidRect(vh, outer.xMin + radius, outer.xMax - radius, outer.yMax - width, outer.yMax,
                    color, outer, dNear, dFar, trackH, fade, span, simple);
            }
            if (vertical)
            {
                DrawRainSolidRect(vh, outer.xMin, outer.xMin + width, outer.yMin + radius, outer.yMax - radius,
                    color, outer, dNear, dFar, trackH, fade, span, simple);
                DrawRainSolidRect(vh, outer.xMax - width, outer.xMax, outer.yMin + radius, outer.yMax - radius,
                    color, outer, dNear, dFar, trackH, fade, span, simple);
            }
            if (horizontal)
            {
                AddRainArc(vh, outer.xMax - radius, outer.yMax - radius, radius, inner, 0f, Mathf.PI * 0.5f,
                    color, outer, dNear, dFar, trackH, fade, span, simple);
                AddRainArc(vh, outer.xMin + radius, outer.yMax - radius, radius, inner, Mathf.PI * 0.5f, Mathf.PI,
                    color, outer, dNear, dFar, trackH, fade, span, simple);
            }
            if (vertical)
            {
                AddRainArc(vh, outer.xMin + radius, outer.yMin + radius, radius, inner, Mathf.PI, Mathf.PI * 1.5f,
                    color, outer, dNear, dFar, trackH, fade, span, simple);
                AddRainArc(vh, outer.xMax - radius, outer.yMin + radius, radius, inner, Mathf.PI * 1.5f, Mathf.PI * 2f,
                    color, outer, dNear, dFar, trackH, fade, span, simple);
            }
        }

        private static void DrawRainSolidRect(VertexHelper vh, float xL, float xR, float yB, float yT, Color color,
            Rect outer, float dNear, float dFar, float trackH, float fade, float span, bool simple)
        {
            if (xR <= xL || yT <= yB) return;
            Color bottom = RainColorAtY(color, yB, outer, dNear, dFar, trackH, fade, simple);
            Color top = RainColorAtY(color, yT, outer, dNear, dFar, trackH, fade, simple);
            AddQuad(vh, xL, xR, yB, yT, 0f, 1f, 0f, 1f, bottom, top);
        }

        private static Color RainColorAtY(Color color, float y, Rect outer, float dNear, float dFar,
            float trackH, float fade, bool simple)
        {
            if (simple || outer.height <= 0.0001f) return color;
            float t = Mathf.Clamp01((y - outer.yMin) / outer.height);
            float distance = Mathf.Lerp(dNear, dFar, t);
            color.a *= AlphaAtD(distance, trackH - fade, trackH, fade);
            return color;
        }

        private static void AddRainArc(VertexHelper vh, float cx, float cy, float outerRadius, float innerRadius,
            float a0, float a1, Color color, Rect outer, float dNear, float dFar, float trackH, float fade,
            float span, bool simple)
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(outerRadius * 0.5f), 3, 12);
            float previousCos = Mathf.Cos(a0);
            float previousSin = Mathf.Sin(a0);
            for (int i = 1; i <= steps; i++)
            {
                float angle = Mathf.Lerp(a0, a1, i / (float)steps);
                float c = Mathf.Cos(angle);
                float s = Mathf.Sin(angle);
                Color c0 = RainColorAtY(color, cy + previousSin * outerRadius, outer, dNear, dFar, trackH, fade, simple);
                Color c1 = RainColorAtY(color, cy + s * outerRadius, outer, dNear, dFar, trackH, fade, simple);
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

        private static void DrawDottedRainBody(VertexHelper vh, RawRain rain, Rect r, Color bottom, Color top, bool simple)
            => DrawDottedRainRect(vh, rain, r, bottom, top, simple);

        /// <summary>Segment a rect along Y into dotLength dots separated by gapLength. The shadow
        /// and outline use this too when the drop is dotted: drawing a SOLID shadow/outline under a
        /// segmented body let the drop's body colour show through every gap, which made the dotted
        /// trail look like one solid bar (the feature silently did nothing whenever a shadow or
        /// outline was on — and the two toggles sit next to each other in the GUI). /
        /// 按 Dot/Gap 沿 Y 切段。阴影与描边在点状时也走这里：实心阴影/描边画在分段本体之下时，
        /// 每个间隙都会漏出本体颜色，点状轨迹看起来仍是一整条实心条——也就是说，只要同时开着
        /// 阴影或描边（两个开关在 GUI 里相邻），点状功能就完全失效。</summary>
        private static void DrawDottedRainRect(VertexHelper vh, RawRain rain, Rect r, Color bottom, Color top, bool simple)
        {
            float dot = rain.dotLength;
            float pattern = Mathf.Max(0.5f, dot + rain.gapLength);
            float step = pattern;
            int maxSegments = Mathf.CeilToInt(r.height / step);
            if (maxSegments > 64)
            {
                // Segment cap: scale the DOT too, not just the spacing — keeping the original
                // dotLength while stretching the step would blow the gaps far past gapLength and
                // silently produce a much sparser trail than the user configured.
                // 段数上限：点长也要同比缩放——只放大步长而保留原点长会把间距拉得远超 gapLength，
                // 实际轨迹比用户设置的稀疏得多且无任何提示。
                float shrink = (r.height / 64f) / pattern;
                step = r.height / 64f;
                dot = Mathf.Max(0.5f, dot * shrink);
            }
            for (float y0 = r.yMin; y0 < r.yMax; y0 += step)
            {
                float y1 = Mathf.Min(r.yMax, y0 + dot);
                if (y1 <= y0) continue;
                Color c0 = RainBodyColorAtY(bottom, top, y0, r, rain, simple);
                Color c1 = RainBodyColorAtY(bottom, top, y1, r, rain, simple);
                AddQuad(vh, r.xMin, r.xMax, y0, y1, 0f, 1f, 0f, 1f, c0, c1);
            }
        }

        private static Color RainBodyColorAtY(Color bottom, Color top, float y, Rect body, RawRain rain, bool simple)
        {
            float t = body.height <= 0.0001f ? 0f : Mathf.Clamp01((y - body.yMin) / body.height);
            Color color = Color.Lerp(bottom, top, t);
            if (!simple && rain.trackHeight > 0.5f && rain.fadePx > 0.5f)
            {
                float distance = Mathf.Lerp(rain.dNear, rain.dFar, t);
                color.a *= AlphaAtD(distance, rain.trackHeight - rain.fadePx, rain.trackHeight, rain.fadePx);
            }
            return color;
        }

        private static void DrawRainQuad(VertexHelper vh, float xL, float xR, float yB, float yT, float h, Color colBot, Color colTop,
            float dNear, float dFar, float trackH, float fade, float span, bool simple)
        {
            if (simple)
            {
                AddQuad(vh, xL, xR, yB, yT, 0f, 1f, 0f, 1f, colBot, colTop);
                return;
            }

            float fadeStartD = trackH - fade;
            float aNear = AlphaAtD(dNear, fadeStartD, trackH, fade);
            float aFar = AlphaAtD(dFar, fadeStartD, trackH, fade);
            bool crosses = dNear < fadeStartD && dFar > fadeStartD;
            if (!crosses)
            {
                // reverseFade was always false in the old renderer / 旧渲染器 reverseFade 恒为 false
                AddQuad(vh, xL, xR, yB, yT, 0f, 1f, 0f, 1f, WithA(colBot, aNear), WithA(colTop, aFar));
                return;
            }
            float t = (fadeStartD - dNear) / span;
            float yMid = yB + t * h;
            Color cMid = Color.Lerp(colBot, colTop, t);
            AddQuad(vh, xL, xR, yB, yMid, 0f, 1f, 0f, 0.5f, WithA(colBot, aNear), cMid);
            AddQuad(vh, xL, xR, yMid, yT, 0f, 1f, 0.5f, 1f, cMid, WithA(colTop, aFar));
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
            vert.position = new Vector3(x0, y0, 0f);
            vert.uv0 = new Vector4(u0, v0, 0f, 0f);
            vert.color = bot;
            vh.AddVert(vert);
            vert.position = new Vector3(x1, y0, 0f);
            vert.uv0 = new Vector4(u1, v0, 0f, 0f);
            vh.AddVert(vert);
            vert.position = new Vector3(x1, y1, 0f);
            vert.uv0 = new Vector4(u1, v1, 0f, 0f);
            vert.color = top;
            vh.AddVert(vert);
            vert.position = new Vector3(x0, y1, 0f);
            vert.uv0 = new Vector4(u0, v1, 0f, 0f);
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
            bool standalone = !hasBorder && RainLayer.IsStandaloneRect(Sprite);
            for (int i = 0; i < keys.Length; i++)
            {
                Key key = keys[i];
                if (key == null || key.rainList.Count == 0) continue;
                for (int d = 0; d < key.rainList.Count; d++)
                {
                    RawRain rain = key.rainList[d];
                    if (rain.removed || !rain.isGhost) continue;
                    Rect r = rain.rect;
                    if (r.width <= 0f || r.height <= 0f) continue;
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
            float px = r.xMin, py = r.yMin;

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
            float x = r.xMin;
            while (x < r.xMax - 0.01f)
            {
                float wTile = Mathf.Min(tileW, r.xMax - x);
                float uu1 = u0 + (u1 - u0) * (wTile / tileW);
                float y = r.yMin;
                while (y < r.yMax - 0.01f)
                {
                    float hTile = Mathf.Min(tileH, r.yMax - y);
                    float vv1 = v0 + (v1 - v0) * (hTile / tileH);
                    RainLayer.AddQuad(vh, x, x + wTile, y, y + hTile, u0, uu1, v0, vv1, c, c);
                    y += hTile;
                }
                x += wTile;
            }
        }
    }
}
