using System.Collections.Generic;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Rendering;

namespace JipperKeyViewer.KeyViewer.Rain
{
    /// <summary>
    /// Rain effect state machine. Drops are pooled RawRain records; rendering happens in the merged
    /// RainLayer / GhostRainLayer meshes that read this state every frame — no per-drop GameObjects.
    /// 雨滴效果状态机。雨滴为对象池 RawRain 记录；渲染由每帧读取本状态的合并 RainLayer /
    /// GhostRainLayer mesh 完成——无逐雨滴 GameObject。
    /// </summary>
    public class RainSystem
    {
        private readonly KeyViewerSettings settings;

        /// <summary>Current key array (kept in sync by UpdateEffects) for the render layers / 当前键数组（由 UpdateEffects 保持同步），供渲染层读取</summary>
        internal Key[] Keys;
        /// <summary>Merged rain render layers / 合并雨滴渲染层</summary>
        internal RainLayer Layer;
        internal GhostRainLayer GhostLayer;

        private readonly Stack<RawRain> rawRainPool = new Stack<RawRain>();
        private readonly List<int> rainActiveKeys = new List<int>();
        private readonly HashSet<int> rainActiveSet = new HashSet<int>();

        private const int MAX_RAWRAIN_POOL_SIZE = 60;
        /// <summary>Height of the old per-key rain container; drops measured their top from it / 旧每键雨滴容器的高度；雨滴顶边以此为基准</summary>
        private const float RainContainerHeight = 275f;

        private readonly float[] rowSpeeds = new float[3];
        private readonly float[] rowHeights = new float[3];
        private readonly float[] ghostRowSpeeds = new float[3];
        private readonly float[] ghostRowHeights = new float[3];
        private readonly float[] rowStartYs = new float[3];
        private readonly float[] ghostRowStartYs = new float[3];
        private float cachedRainSpeed1, cachedRainSpeed2, cachedRainSpeed3;
        private float cachedRainHeight1, cachedRainHeight2, cachedRainHeight3;
        private float cachedGhostSpeed1, cachedGhostSpeed2, cachedGhostSpeed3;
        private float cachedGhostHeight1, cachedGhostHeight2, cachedGhostHeight3;
        private float cachedStartY1, cachedStartY2, cachedStartY3;
        private float cachedGhostStartY1, cachedGhostStartY2, cachedGhostStartY3;

        public RainSystem(KeyViewerSettings settings)
        {
            this.settings = settings;
        }

        public void AttachLayers(RainLayer layer, GhostRainLayer ghostLayer)
        {
            Layer = layer;
            GhostLayer = ghostLayer;
            if (layer != null) layer.System = this;
            if (ghostLayer != null) ghostLayer.System = this;
        }

        public void DetachLayers()
        {
            Layer = null;
            GhostLayer = null;
            Keys = null;
        }

        public void UpdateEffects(Key[] keys)
        {
            if (keys == null || keys.Length == 0)
            {
                Keys = keys;
                return;
            }
            Keys = keys;
            if (rainActiveKeys.Count == 0) return;

            SyncCachedSpeeds();
            float dtSec = Time.unscaledDeltaTime;

            for (int i = 0; i < rainActiveKeys.Count; i++)
            {
                int ki = rainActiveKeys[i];
                Key key = keys[ki];
                if (key == null || key.rainList.Count == 0)
                {
                    rainActiveSet.Remove(ki);
                    rainActiveKeys[i] = rainActiveKeys[rainActiveKeys.Count - 1];
                    rainActiveKeys.RemoveAt(rainActiveKeys.Count - 1);
                    i--;
                    continue;
                }

                RectTransform keyRt = (RectTransform)key.transform;
                Vector2 keyPos = keyRt.anchoredPosition;
                // Row for the per-frame speed/height arrays (0/1/2): custom nodes carry their
                // own RainRow — custom slots are assigned in DEPTH order, so deriving from ki
                // handed every 9th+ custom key row-2 params regardless of its setting, while
                // drop CREATION (CreateRainDropForKey) used the correct row → mixed-row drops.
                // Fixed layouts keep the slot thresholds, mirroring CreateRainDropForKey.
                // 每帧速度/高度数组所用的排（0/1/2）：自定义节点自带 RainRow——自定义槽位按
                // 深度序分配，按 ki 推导会让第 9 个及以后的键无论设置如何都吃到第 2 排参数，
                // 而雨滴生成（CreateRainDropForKey）用的是正确的排 → 参数串排。固定布局沿用
                // 槽位阈值，与 CreateRainDropForKey 对齐。
                int row = key.CustomNode != null
                    ? Mathf.Clamp(key.CustomNode.RainRow, 0, 2)
                    : (ki < 8 ? 0 : (ki < 16 ? 1 : 2));
                for (int j = key.rainList.Count - 1; j >= 0; j--)
                    UpdateSingleRainDrop(key.rainList[j], key, ki, keyPos, j, row, dtSec);
            }

            // One mesh rebuild per frame while anything is active / 只要有活跃雨滴，每帧重建一次 mesh
            if (Layer != null) Layer.MarkDirty();
        }

        private void SyncCachedSpeeds()
        {
            if (SameCached(cachedRainSpeed1, settings.Data.RainSpeedRow1) && SameCached(cachedRainSpeed2, settings.Data.RainSpeedRow2) &&
                SameCached(cachedRainSpeed3, settings.Data.RainSpeedRow3) && SameCached(cachedRainHeight1, settings.Data.RainHeightRow1) &&
                SameCached(cachedRainHeight2, settings.Data.RainHeightRow2) && SameCached(cachedRainHeight3, settings.Data.RainHeightRow3) &&
                SameCached(cachedGhostSpeed1, settings.Data.GhostRainSpeedRow1) && SameCached(cachedGhostSpeed2, settings.Data.GhostRainSpeedRow2) &&
                SameCached(cachedGhostSpeed3, settings.Data.GhostRainSpeedRow3) && SameCached(cachedGhostHeight1, settings.Data.GhostRainHeightRow1) &&
                SameCached(cachedGhostHeight2, settings.Data.GhostRainHeightRow2) && SameCached(cachedGhostHeight3, settings.Data.GhostRainHeightRow3) &&
                SameCached(cachedStartY1, settings.Data.RainStartYRow1) && SameCached(cachedStartY2, settings.Data.RainStartYRow2) &&
                SameCached(cachedStartY3, settings.Data.RainStartYRow3) && SameCached(cachedGhostStartY1, settings.Data.GhostRainStartYRow1) &&
                SameCached(cachedGhostStartY2, settings.Data.GhostRainStartYRow2) && SameCached(cachedGhostStartY3, settings.Data.GhostRainStartYRow3))
                return;
            // Floor speeds and heights: typed values are stored unclamped, and a zero/negative
            // speed never lifts the drop past the track top (y stays <= height forever) — with
            // fade disabled the drop is then NEVER recycled and rainList grows without bound
            // (invisible but a steady per-frame/memory leak). A tiny positive speed keeps the
            // "nearly frozen" look while guaranteeing eventual off-track recycling; heights are
            // floored at 1 so the off-track branch math stays well-defined. The floor is 1px/s
            // (1e-3 px/ms): a zero-speed drop recycles within minutes at typical heights instead
            // of the hours a smaller floor implied.
            // 速度与高度取下限:键入值不钳制存储,零/负速度永远无法把雨滴抬出轨道顶
            //(y 恒 <= height)——若淡出关闭,该雨滴永不回收,rainList 无界增长(不可见但
            // 内存与逐帧遍历成本持续泄漏)。极小正速度保留"近乎冻结"的观感,同时保证最终
            // 落出轨道被回收;高度下限 1 保证离轨分支数学良定义。下限取 1px/s(1e-3 px/ms):
            // 零速雨滴在典型高度下数分钟内回收,而不是更小下限意味着的小时级。
            const float minSpeedFactor = 1e-3f;
            rowSpeeds[0] = Mathf.Max(settings.Data.RainSpeedRow1 / 300f, minSpeedFactor);
            rowSpeeds[1] = Mathf.Max(settings.Data.RainSpeedRow2 / 300f, minSpeedFactor);
            rowSpeeds[2] = Mathf.Max(settings.Data.RainSpeedRow3 / 300f, minSpeedFactor);
            rowHeights[0] = Mathf.Max(settings.Data.RainHeightRow1, 1f);
            rowHeights[1] = Mathf.Max(settings.Data.RainHeightRow2, 1f);
            rowHeights[2] = Mathf.Max(settings.Data.RainHeightRow3, 1f);
            ghostRowSpeeds[0] = Mathf.Max(settings.Data.GhostRainSpeedRow1 / 300f, minSpeedFactor);
            ghostRowSpeeds[1] = Mathf.Max(settings.Data.GhostRainSpeedRow2 / 300f, minSpeedFactor);
            ghostRowSpeeds[2] = Mathf.Max(settings.Data.GhostRainSpeedRow3 / 300f, minSpeedFactor);
            ghostRowHeights[0] = Mathf.Max(settings.Data.GhostRainHeightRow1, 1f);
            ghostRowHeights[1] = Mathf.Max(settings.Data.GhostRainHeightRow2, 1f);
            ghostRowHeights[2] = Mathf.Max(settings.Data.GhostRainHeightRow3, 1f);
            // Start-Y is the only per-row value with NO Mathf.Max floor, so a NaN/Infinity typed (or
            // imported) here survived every comparison and flowed into rawRain.rect — one NaN drop
            // wrote NaN vertices into the SHARED merged mesh, corrupting the whole rain canvas and
            // re-corrupting it every frame. Clamp to a sane band; NaN/Infinity fall back to -223.
            // 起始 Y 是唯一没有 Mathf.Max 下限的按排值：NaN/Inf 能通过所有比较并进入 rawRain.rect，
            // 一滴坏雨滴就会把 NaN 顶点写进共享 mesh，整块雨滴画布每帧都被污染。
            rowStartYs[0] = SanitizeStartY(settings.Data.RainStartYRow1);
            rowStartYs[1] = SanitizeStartY(settings.Data.RainStartYRow2);
            rowStartYs[2] = SanitizeStartY(settings.Data.RainStartYRow3);
            ghostRowStartYs[0] = SanitizeStartY(settings.Data.GhostRainStartYRow1);
            ghostRowStartYs[1] = SanitizeStartY(settings.Data.GhostRainStartYRow2);
            ghostRowStartYs[2] = SanitizeStartY(settings.Data.GhostRainStartYRow3);
            cachedRainSpeed1 = settings.Data.RainSpeedRow1;
            cachedRainSpeed2 = settings.Data.RainSpeedRow2;
            cachedRainSpeed3 = settings.Data.RainSpeedRow3;
            cachedRainHeight1 = settings.Data.RainHeightRow1;
            cachedRainHeight2 = settings.Data.RainHeightRow2;
            cachedRainHeight3 = settings.Data.RainHeightRow3;
            cachedGhostSpeed1 = settings.Data.GhostRainSpeedRow1;
            cachedGhostSpeed2 = settings.Data.GhostRainSpeedRow2;
            cachedGhostSpeed3 = settings.Data.GhostRainSpeedRow3;
            cachedGhostHeight1 = settings.Data.GhostRainHeightRow1;
            cachedGhostHeight2 = settings.Data.GhostRainHeightRow2;
            cachedGhostHeight3 = settings.Data.GhostRainHeightRow3;
            cachedStartY1 = settings.Data.RainStartYRow1;
            cachedStartY2 = settings.Data.RainStartYRow2;
            cachedStartY3 = settings.Data.RainStartYRow3;
            cachedGhostStartY1 = settings.Data.GhostRainStartYRow1;
            cachedGhostStartY2 = settings.Data.GhostRainStartYRow2;
            cachedGhostStartY3 = settings.Data.GhostRainStartYRow3;
        }

        private void UpdateSingleRainDrop(RawRain rain, Key key, int keyIndex, Vector2 keyPos, int j, int row, float dtSec)
        {
            if (rain.removed) return;

            // Per-node speeds share the row sliders' user-facing unit — the row pipeline bakes a
            // /300 conversion into rowSpeeds, so NodeSpeed must be scaled the same way here.
            // Consumed raw it was 300× too fast (a seeded row value of 100 snapped the trail to
            // full height instantly — read as "no upward animation"). / 逐节点速度与排滑杆共用
            // 同一用户单位——排管线在 rowSpeeds 里烘焙了 /300 换算，NodeSpeed 此处必须同样
            // 缩放。原样消费会快 300 倍（种子值 100 瞬间拉满轨迹——看起来就是"没有向上动画"）。
            float speed = rain.NodeSpeed > 0f
                ? rain.NodeSpeed / 300f
                : (rain.isGhost ? ghostRowSpeeds[row] : rowSpeeds[row]);
            float height = rain.NodeHeight > 0f ? rain.NodeHeight : (rain.isGhost ? ghostRowHeights[row] : rowHeights[row]);
            float dt = dtSec * 1000f;
            if (!rain.UpdateLocation(rain.growing, speed, height, dt))
            {
                ReturnRawRainAndRemove(rain, key, j);
                return;
            }

            // Rect first, fade last: a drop removed by fade-out must not be written to again
            // (ReturnRawRain can hand the record to a new drop in the same frame).
            // 先算矩形后处理淡出：被淡出移除的雨滴不能再被写入（ReturnRawRain 可能在同帧
            // 就把记录发给了新雨滴）。
            UpdateRectAndTrail(rain, key, keyIndex, keyPos, speed, height);
            UpdateFade(rain, dtSec, key, j);
        }

        private void UpdateFade(RawRain rain, float dtSec, Key key, int j)
        {
            if (!rain.fadingOut) return;
            rain.fadeTimer += dtSec;
            // Zero/negative duration (typed values are unclamped): treat as instant fade — avoids a
            // 0/0 NaN alpha on the first tick. / 零/负时长（键入值不钳制）：按立即淡出处理——
            // 避免首帧 0/0 得到 NaN 透明度。
            // Per-node release-fade duration override (UseCustomRainFade). /
            // 节点级松开淡出时长覆盖（UseCustomRainFade）。
            float fadeDur = key != null && key.CustomNode != null && key.CustomNode.UseCustomRainFade
                ? key.CustomNode.ReleaseFadeDuration
                : settings.Data.RainFadeDuration;
            float t = fadeDur > 0f
                ? Mathf.Clamp01(rain.fadeTimer / fadeDur)
                : 1f;
            rain.alpha = 1f - (t * (2f - t));
            if (t >= 1f)
                ReturnRawRainAndRemove(rain, key, j);
        }

        /// <summary>
        /// Compute the drop's layer-space rect (including the affect-rain press scale) and trail
        /// gradient params. Start-Y and ghost offsets are read live from settings, so the GUI sliders
        /// update existing drops without any container bookkeeping.
        /// 计算雨滴的图层空间矩形（含雨滴跟随按压缩放）与轨迹渐变参数。起始 Y 与鬼雨偏移实时读取
        /// 设置，GUI 滑块直接作用于现有雨滴，无需容器簿记。
        /// </summary>
        /// <summary>The single mapping from a drop's colour byte to its 0-based row. Start-Y,
        /// width and the per-row switches must all agree, and this is the one place that decides
        /// it — a second, independently edited copy is exactly how "row 2 speed with row 1 width"
        /// bugs happen. / 雨滴颜色字节→0 基排号的唯一定义：起始 Y、宽度与各排开关必须一致，
        /// 而独立维护第二份拷贝正是"第 2 排速度配第 1 排宽度"这类 bug 的成因。</summary>
        private static int RowFromRainByte(byte color) => color == 0 ? 0 : color == 3 ? 2 : 1;

        private void UpdateRectAndTrail(RawRain rain, Key key, int keyIndex, Vector2 keyPos, float speed, float height)
        {
            float trailEdgeDist = rain.elapsedMs * speed;
            float drawH = trailEdgeDist > height
                ? rain.FinalSize.y - trailEdgeDist + height
                : (rain.growing ? trailEdgeDist : rain.FinalSize.y);
            rain.dFar = Mathf.Min(trailEdgeDist, height);
            rain.dNear = rain.dFar - drawH;
            rain.trackHeight = height;
            // Trail-top fade px: per-node override (UseCustomRainFade), ghost stays hard-edged
            // like the global behavior. / 顶部渐隐像素：节点级覆盖（UseCustomRainFade），鬼雨
            // 与全局行为一致保持硬边。
            rain.fadePx = rain.isGhost ? 0f
                : key.CustomNode != null && key.CustomNode.UseCustomRainFade
                    ? (key.CustomNode.TrailFadeEnabled ? key.CustomNode.TrailFadePx : 0f)
                    : (settings.Data.EnableRainGradient ? settings.Data.RainFadePx : 0f);

            float w = rain.sizeDelta?.x ?? rain.FinalSize.x;
            float h = rain.sizeDelta?.y ?? rain.FinalSize.y;
            // Old layout: the 275px container anchored to the key rect's BOTTOM-LEFT corner, and the
            // drop hung on the container's top-center anchor — so X centers on the container column,
            // Y measures from (key bottom + start-Y + container height). Start-Y (normal and ghost)
            // is read live here; the baked startY is subtracted back out.
            // 旧布局：275px 容器锚在按键矩形左下角，雨滴挂在容器顶中锚点上——X 以容器列居中，
            // Y 从（键底边 + 起始 Y + 容器高）起算。起始 Y（普通与鬼雨）在此实时读取，
            // 创建时烙入的 startY 被减回。
            int ri = RowFromRainByte(rain.color);
            float baseStart = rain.HasTrackBase
                ? rain.TrackBaseY + rain.StartOffsetY
                : (rain.isGhost ? ghostRowStartYs[ri] : rowStartYs[ri]) + rain.StartOffsetY;
            float travel = rain.anchoredPosition.Value.y - rain.startY;
            float ox = rain.HasOffsetX ? rain.OffsetXOverride : key.rainOffsetX;
            float alignOffset = 0f;
            if (key.CustomNode != null)
            {
                if (key.CustomNode.RainAlignment == 0) alignOffset = 0f;
                else if (key.CustomNode.RainAlignment == 1) alignOffset = (key.keySize.x - key.rainWidth) * 0.5f;
                else alignOffset = key.keySize.x - key.rainWidth;
            }
            float cx = keyPos.x + alignOffset + ox + key.rainWidth * 0.5f;
            float topY = keyPos.y - key.keySize.y * 0.5f + baseStart + RainContainerHeight + travel;

            float s = Layer != null ? Layer.GetKeyScale(keyIndex) : 1f;
            rain.scaleF = s;
            if (s != 1f)
            {
                // Scale around the key box center — what the old root-scale + compensation achieved /
                // 围绕按键框中心缩放——与旧根缩放 + 位置补偿等效
                float kcx = keyPos.x + key.keySize.x * 0.5f;
                cx = kcx + (cx - kcx) * s;
                w *= s;
                topY = keyPos.y + (topY - keyPos.y) * s;
                h *= s;
            }
            rain.rect = new Rect(cx - w * 0.5f, topY - h, w, h);
        }

        private void ReturnRawRainAndRemove(RawRain rain, Key key, int listIndex)
        {
            rain.removed = true;
            ReturnRawRain(rain);
            key.rainList.RemoveAt(listIndex);
        }

        public void TriggerRainEffect(int keyIndex, Key key)
        {
            if (key == null) return;
            // Custom nodes carry both a per-node gate and the global row gate. / Custom 节点同时
            // 受逐节点开关和全局雨排开关约束。
            if (key.CustomNode != null)
            {
                if (!key.CustomNode.RainEnabled || !IsCustomRainRowEnabled(key.CustomNode, isGhost: false)) return;
                CreateRainDropForKey(keyIndex, key);
                return;
            }
            if (!IsRainEnabledForKey(keyIndex))
                return;
            CreateRainDropForKey(keyIndex, key);
        }

        public void ReleaseRainEffect(int keyIndex, Key key)
        {
            if (key == null || key.rainList.Count == 0) return;
            for (int i = key.rainList.Count - 1; i >= 0; i--)
            {
                if (key.rainList[i].isGhost) continue;
                key.rainList[i].growing = false;
                // Per-node release-fade enable override (UseCustomRainFade). /
                // 节点级松开淡出开关覆盖（UseCustomRainFade）。
                bool fadeOn = key.CustomNode != null && key.CustomNode.UseCustomRainFade
                    ? key.CustomNode.ReleaseFadeEnabled
                    : settings.Data.EnableRainFade;
                if (fadeOn)
                {
                    key.rainList[i].fadingOut = true;
                    key.rainList[i].fadeTimer = 0f;
                }
                break;
            }
        }

        public void TriggerGhostRain(int keyIndex, Key key)
        {
            if (key == null) return;
            if (key.CustomNode != null)
            {
                if (!key.CustomNode.RainEnabled || !IsCustomRainRowEnabled(key.CustomNode, isGhost: true)) return;
                CreateRainDropForKey(keyIndex, key, isGhost: true);
                return;
            }
            if (!IsRainEnabledForKey(keyIndex)) return;
            CreateRainDropForKey(keyIndex, key, isGhost: true);
        }

        public void ReleaseGhostRain(int keyIndex, Key key)
        {
            if (key == null || key.rainList.Count == 0) return;
            for (int i = key.rainList.Count - 1; i >= 0; i--)
            {
                if (key.rainList[i].isGhost)
                {
                    key.rainList[i].growing = false;
                    break;
                }
            }
        }

        public void ClearActiveDrops(Key[] keys)
        {
            if (keys == null) return;
            rainActiveKeys.Clear();
            rainActiveSet.Clear();
            foreach (var key in keys)
            {
                if (key == null) continue;
                foreach (var rain in key.rainList)
                    ReturnRawRain(rain);
                key.rainList.Clear();
            }
            if (Layer != null) Layer.MarkDirty();
        }

        public void ClearAll(Key[] keys)
        {
            ClearActiveDrops(keys);
            rawRainPool.Clear();
        }

        public Color GetRainColor(byte color) => RainColor(color, false);

        public Color GetGhostRainColor(byte color) => RainColor(color, true);

        private Color RainColor(byte color, bool ghost)
        {
            return color switch
            {
                0 => ghost ? settings.Data.GhostRainColor : settings.Data.RainColor,
                3 => ghost ? settings.Data.GhostRainColor3 : settings.Data.RainColor3,
                _ => ghost ? settings.Data.GhostRainColor2 : settings.Data.RainColor2
            };
        }

        /// <summary>Rain start-Y must be a finite number inside a sane band: NaN survives every
        /// comparison (including the SyncCachedSpeeds `==` cache, which then never hits) and would
        /// reach the merged mesh as NaN vertices. / 雨滴起始 Y 必须是有限值：NaN 能通过所有比较
        /// （SyncCachedSpeeds 的 `==` 缓存也永不命中），最终会以 NaN 顶点进入合并 mesh。</summary>
        private static float SanitizeStartY(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return -223f;
            return Mathf.Clamp(value, -4000f, 4000f);
        }

        /// <summary>Do the cached settings still match? Compare with "same or both NaN" so a
        /// NaN value cannot make the cache permanently miss and re-run this every frame. / 缓存是否
        /// 仍然有效？按“相同或同为 NaN”比较，避免 NaN 让缓存永久不命中而每帧重算。</summary>
        private static bool SameCached(float a, float b)
            => a == b || (float.IsNaN(a) && float.IsNaN(b));

        private RawRain GetRawRain(byte color)
        {
            RawRain r;
            if (rawRainPool.Count > 0)
            {
                r = rawRainPool.Pop();
                r.color = color;
                r.removed = false;
                r.elapsedMs = 0f;
                r.startY = 0f;
                r.sizeDelta = null;
                r.anchoredPosition = null;
                r.isGhost = false;
                r.growing = false;
                r.FinalSize = default;
                r.rect = default;
                r.mainColor = Color.white;
                r.ColorTop = Color.white;
                r.StartOffsetY = 0f;
                r.HasTrackBase = false;
                r.TrackBaseY = 0f;
                r.HasOffsetX = false;
                r.OffsetXOverride = 0f;
                r.NodeWidth = 0f;
                r.NodeHeight = 0f;
                r.NodeSpeed = 0f;
                r.alpha = 1f;
                r.scaleF = 1f;
                r.fadingOut = false;
                r.fadeTimer = 0f;
                r.dNear = r.dFar = r.trackHeight = r.fadePx = 0f;
                r.shadowEnabled = false;
                r.shadowColor = default;
                r.shadowOffsetX = r.shadowOffsetY = 0f;
                r.outlineEnabled = false;
                r.outlineColor = default;
                r.outlineWidth = 0f;
                r.outlineCornerRadius = 0f;
                r.outlineSides = 0;
                r.dotted = false;
                r.dotLength = 0f;
                r.gapLength = 0f;
            }
            else
            {
                r = new RawRain(color);
            }
            return r;
        }

        public void ReturnRawRain(RawRain r)
        {
            if (rawRainPool.Count >= MAX_RAWRAIN_POOL_SIZE) return;
            // `removed` is deliberately NOT cleared here. It is set by ReturnRawRainAndRemove just
            // before this call, and the renderers skip flagged records; clearing it made the flag
            // dead, so any future caller that recycles WITHOUT removing the record from the key's
            // rainList would hand a recycled drop back to the renderer as a ghost drop. Only
            // GetRawRain — which takes the record OUT of the pool — revives it.
            // 此处刻意不清 `removed`：它由 ReturnRawRainAndRemove 在调用前设置，渲染器会跳过
            // 被标记的记录；清掉它等于让该标志变成死代码，将来任何"只回收、不从 rainList 移除"
            // 的调用方都会把已回收的雨滴当幽灵雨滴交回渲染器。只有把它从池中取出的
            // GetRawRain 才负责复活。
            r.sizeDelta = null;
            r.anchoredPosition = null;
            r.isGhost = false;
            r.growing = false;
            rawRainPool.Push(r);
        }

        private void CreateRainDropForKey(int keyIndex, Key key, bool isGhost = false)
        {
            if (KeyViewer.IsFullKeyboard) return;

            // Custom nodes carry their own rain row; fixed layouts derive it from the slot
            // index. / 自定义节点自带雨滴排；固定布局按槽位序号推导。
            int row = key.CustomNode != null
                ? Mathf.Clamp(key.CustomNode.RainRow, 0, 2) + 1
                : (keyIndex < 8 ? 1 : (keyIndex < 16 ? 2 : 3));

            RawRain rawRain = GetRawRain(key.color);
            float baseY = row == 1 ? settings.Data.RainStartYRow1 : row == 2 ? settings.Data.RainStartYRow2 : settings.Data.RainStartYRow3;
            rawRain.startY = isGhost
                ? (row == 1 ? settings.Data.GhostRainStartYRow1 : row == 2 ? settings.Data.GhostRainStartYRow2 : settings.Data.GhostRainStartYRow3) - baseY
                : 0f;

            if (isGhost)
            {
                rawRain.mainColor = key.CustomNode != null
                    ? GetGhostRainColor(key.color)
                    : settings.Data.EnablePerKeyColors
                        ? settings.Data.PerKeyGhostRainColor[keyIndex]
                        : GetGhostRainColor(key.color);

                rawRain.shadowEnabled = row == 1 ? settings.Data.EnableGhostRainShadowRow1
                    : row == 2 ? settings.Data.EnableGhostRainShadowRow2
                    : settings.Data.EnableGhostRainShadowRow3;
                rawRain.shadowColor = row == 1 ? settings.Data.GhostRainShadowColorRow1
                    : row == 2 ? settings.Data.GhostRainShadowColorRow2
                    : settings.Data.GhostRainShadowColorRow3;
                rawRain.shadowOffsetX = row == 1 ? settings.Data.GhostRainShadowOffsetXRow1
                    : row == 2 ? settings.Data.GhostRainShadowOffsetXRow2
                    : settings.Data.GhostRainShadowOffsetXRow3;
                rawRain.shadowOffsetY = row == 1 ? settings.Data.GhostRainShadowOffsetYRow1
                    : row == 2 ? settings.Data.GhostRainShadowOffsetYRow2
                    : settings.Data.GhostRainShadowOffsetYRow3;
                rawRain.outlineEnabled = row == 1 ? settings.Data.EnableGhostRainOutlineRow1
                    : row == 2 ? settings.Data.EnableGhostRainOutlineRow2
                    : settings.Data.EnableGhostRainOutlineRow3;
                rawRain.outlineColor = row == 1 ? settings.Data.GhostRainOutlineColorRow1
                    : row == 2 ? settings.Data.GhostRainOutlineColorRow2
                    : settings.Data.GhostRainOutlineColorRow3;
                rawRain.outlineWidth = Mathf.Max(0f, row == 1 ? settings.Data.GhostRainOutlineWidthRow1
                    : row == 2 ? settings.Data.GhostRainOutlineWidthRow2
                    : settings.Data.GhostRainOutlineWidthRow3);
            }
            else
            {
                rawRain.mainColor = key.rainColor;

                rawRain.shadowEnabled = row == 1 ? settings.Data.EnableRainShadowRow1
                    : row == 2 ? settings.Data.EnableRainShadowRow2
                    : settings.Data.EnableRainShadowRow3;
                rawRain.shadowColor = row == 1 ? settings.Data.RainShadowColorRow1
                    : row == 2 ? settings.Data.RainShadowColorRow2
                    : settings.Data.RainShadowColorRow3;
                rawRain.shadowOffsetX = row == 1 ? settings.Data.RainShadowOffsetXRow1
                    : row == 2 ? settings.Data.RainShadowOffsetXRow2
                    : settings.Data.RainShadowOffsetXRow3;
                rawRain.shadowOffsetY = row == 1 ? settings.Data.RainShadowOffsetYRow1
                    : row == 2 ? settings.Data.RainShadowOffsetYRow2
                    : settings.Data.RainShadowOffsetYRow3;
                rawRain.outlineEnabled = row == 1 ? settings.Data.EnableRainOutlineRow1
                    : row == 2 ? settings.Data.EnableRainOutlineRow2
                    : settings.Data.EnableRainOutlineRow3;
                rawRain.outlineColor = row == 1 ? settings.Data.RainOutlineColorRow1
                    : row == 2 ? settings.Data.RainOutlineColorRow2
                    : settings.Data.RainOutlineColorRow3;
                rawRain.outlineWidth = Mathf.Max(0f, row == 1 ? settings.Data.RainOutlineWidthRow1
                    : row == 2 ? settings.Data.RainOutlineWidthRow2
                    : settings.Data.RainOutlineWidthRow3);
            }

            // Per-node shadow/outline overrides — applied AFTER the row assignment so a null node
            // color keeps the row's. Normal rain and ghost rain each have their own override pair
            // (ghost rows stay the ghost fallback). / 节点级阴影/描边覆盖——在排赋值之后应用，
            // 节点颜色为空时保留排颜色。普通雨与鬼雨各有独立的覆盖组（鬼雨排仍是鬼雨的回退）。
            if (key.CustomNode != null)
            {
                FmNode cn = key.CustomNode;
                if (!isGhost)
                {
                    if (cn.UseCustomRainShadow)
                    {
                        rawRain.shadowEnabled = cn.RainShadowEnabled;
                        rawRain.shadowColor = KeyViewer.NodeColor(cn.RainShadowColor, rawRain.shadowColor);
                        rawRain.shadowOffsetX = cn.RainShadowOffsetX;
                        rawRain.shadowOffsetY = cn.RainShadowOffsetY;
                    }
                    if (cn.UseCustomRainOutline)
                    {
                        rawRain.outlineEnabled = cn.RainOutlineEnabled;
                        rawRain.outlineColor = KeyViewer.NodeColor(cn.RainOutlineColor, rawRain.outlineColor);
                        rawRain.outlineWidth = Mathf.Max(0f, cn.RainOutlineWidth);
                    }
                }
                else
                {
                    if (cn.UseCustomGhostRainShadow)
                    {
                        rawRain.shadowEnabled = cn.GhostRainShadowEnabled;
                        rawRain.shadowColor = KeyViewer.NodeColor(cn.GhostRainShadowColor, rawRain.shadowColor);
                        rawRain.shadowOffsetX = cn.GhostRainShadowOffsetX;
                        rawRain.shadowOffsetY = cn.GhostRainShadowOffsetY;
                    }
                    if (cn.UseCustomGhostRainOutline)
                    {
                        rawRain.outlineEnabled = cn.GhostRainOutlineEnabled;
                        rawRain.outlineColor = KeyViewer.NodeColor(cn.GhostRainOutlineColor, rawRain.outlineColor);
                        rawRain.outlineWidth = Mathf.Max(0f, cn.GhostRainOutlineWidth);
                    }
                }
            }

            rawRain.outlineCornerRadius = settings.Data.EnableRainRoundedOutline
                ? Mathf.Clamp(settings.Data.RainOutlineCornerRadius, 0f, 20f)
                : 0f;
            rawRain.outlineSides = Mathf.Clamp(settings.Data.RainOutlineSides, 0, 2);
            rawRain.dotted = settings.Data.EnableRainDotted;
            rawRain.dotLength = settings.Data.EnableRainDotted ? Mathf.Clamp(settings.Data.RainDotLength, 1f, 100f) : 0f;
            rawRain.gapLength = settings.Data.EnableRainDotted ? Mathf.Clamp(settings.Data.RainGapLength, 0f, 100f) : 0f;
            if (key.CustomNode != null)
            {
                FmNode node = key.CustomNode;
                if (isGhost)
                {
                    if (node.UseCustomGhostRainCornerRadius)
                        rawRain.outlineCornerRadius = Mathf.Clamp(node.GhostRainCornerRadius, 0f, 20f);
                    if (node.UseCustomGhostRainDotted)
                    {
                        rawRain.dotted = true;
                        rawRain.dotLength = Mathf.Clamp(node.GhostRainDotLength, 1f, 100f);
                        rawRain.gapLength = Mathf.Clamp(node.GhostRainGapLength, 0f, 100f);
                    }
                }
                else
                {
                    if (node.UseCustomRainCornerRadius)
                        rawRain.outlineCornerRadius = Mathf.Clamp(node.RainCornerRadius, 0f, 20f);
                    if (node.UseCustomRainBorderSides)
                        rawRain.outlineSides = Mathf.Clamp(node.RainBorderSides, 0, 2);
                    if (node.UseCustomRainDotted)
                    {
                        rawRain.dotted = true;
                        rawRain.dotLength = Mathf.Clamp(node.RainDotLength, 1f, 100f);
                        rawRain.gapLength = Mathf.Clamp(node.RainGapLength, 0f, 100f);
                    }
                }
            }

            // Per-node top color + start-Y offset (two-color gradient / start offset), and the
            // node-top-anchored track model: custom rain starts at the node's TOP edge — the 275px
            // container constant is tuned for 50px keys and would spawn the trail inside taller
            // nodes. / 节点级顶端颜色与起始 Y 偏移（雨滴双色渐变与起始偏移），
            // 以及 锚定节点顶边的轨道模型：自定义雨滴从节点顶边出发——275px 容器常量是为
            // 50px 键调校的，较高的节点会让轨迹从按键内部冒出来。
            rawRain.ColorTop = key.CustomNode != null && key.CustomNode.UseCustomRainColor && key.CustomNode.RainColorTop != null
                ? KeyViewer.NodeColor(key.CustomNode.RainColorTop, rawRain.mainColor)
                : rawRain.mainColor;
            if (key.CustomNode != null)
            {
                rawRain.HasTrackBase = true;
                rawRain.TrackBaseY = key.keySize.y - RainContainerHeight + 2f;
                rawRain.StartOffsetY = key.CustomNode.RainOffsetY;
            }
            else
            {
                rawRain.StartOffsetY = 0f;
            }

            // Per-node rain parameter overrides (per-node rain width/height/speed). /
            // 节点级雨滴参数覆盖（逐键雨滴宽度/高度/速度）。
            if (key.CustomNode != null)
            {
                if (key.CustomNode.RainWidth > 0f) rawRain.NodeWidth = key.CustomNode.RainWidth;
                if (key.CustomNode.RainHeight > 0f) rawRain.NodeHeight = key.CustomNode.RainHeight;
                if (key.CustomNode.RainSpeed > 0f) rawRain.NodeSpeed = key.CustomNode.RainSpeed;
            }

            // Ghost rain with independent params: shape/offset overrides of its own, layered on
            // top of the shared ones. / 独立参数的鬼雨：自有的形状/偏移覆盖，叠加在共享覆盖之上。
            if (isGhost && key.CustomNode != null && key.CustomNode.UseCustomGhostRainParams)
            {
                FmNode g = key.CustomNode;
                if (g.GhostRainWidth > 0f) rawRain.NodeWidth = g.GhostRainWidth;
                if (g.GhostRainHeight > 0f) rawRain.NodeHeight = g.GhostRainHeight;
                if (g.GhostRainSpeed > 0f) rawRain.NodeSpeed = g.GhostRainSpeed;
                rawRain.StartOffsetY = g.GhostRainOffsetY;
                rawRain.HasOffsetX = true;
                rawRain.OffsetXOverride = g.GhostRainOffsetX;
            }

            // Resolve the row's width HERE, at creation, instead of letting RawRain reach back into
            // the KeyViewer.Settings component on the per-frame hot path (a static back-reference
            // that also NREs whenever Settings is null). RowFromRainByte is the single mapping from
            // the drop's colour byte to its row, shared with the start-Y lookup in
            // UpdateRectAndTrail so the two can never drift apart.
            // 在创建时就解析好该排的宽度，而不是让 RawRain 在逐帧热路径上回访
            // KeyViewer.Settings 组件（静态反向引用，Settings 为 null 时还会 NRE）。
            // RowFromRainByte 是"颜色字节→排"的唯一定义，与 UpdateRectAndTrail 里的起始 Y
            // 查询共用，二者不会各自漂移。
            if (rawRain.NodeWidth <= 0f)
            {
                int widthRow = RowFromRainByte(key.color);
                rawRain.NodeWidth = isGhost
                    ? (widthRow == 0 ? settings.Data.GhostRainWidthRow1 : widthRow == 1 ? settings.Data.GhostRainWidthRow2 : settings.Data.GhostRainWidthRow3)
                    : (widthRow == 0 ? settings.Data.RainWidthRow1 : widthRow == 1 ? settings.Data.RainWidthRow2 : settings.Data.RainWidthRow3);
            }

            rawRain.isGhost = isGhost;
            rawRain.growing = true;

            key.rainList.Add(rawRain);

            if (!rainActiveSet.Contains(keyIndex))
            {
                rainActiveSet.Add(keyIndex);
                rainActiveKeys.Add(keyIndex);
            }
        }

        private bool IsCustomRainRowEnabled(FmNode node, bool isGhost)
        {
            if (node == null) return false;
            int row = Mathf.Clamp(node.RainRow, 0, 2);
            bool rowEnabled = row == 0 ? settings.Data.EnableRainForRow1
                : row == 1 ? settings.Data.EnableRainForRow2
                : settings.Data.EnableRainForRow3;
            // Ghost rain has one global enable plus the same per-row normal-rain gates; it does
            // not define separate ghost-row enable switches in ProfileData. / 鬼雨只有一个全局
            // 开关，并沿用普通雨排开关；ProfileData 没有独立的鬼雨排开关。
            return rowEnabled && (!isGhost || settings.Data.EnableGhostRain);
        }

        private bool IsRainEnabledForKey(int keyIndex)
        {
            if (KeyViewer.IsFullKeyboard) return false;
            // Row mapping must match the speed/height/start-Y rows: 0-7 = row 1, 8-15 = row 2,
            // 16+ = row 3. (The released version gated rows 1+2 together on Row2, leaving the
            // row-1 toggle dead.) / 排映射须与速度/高度/起始 Y 一致：0-7 第1排，8-15 第2排，16+ 第3排。
            // （旧版本把第 1、2 排一起挂在 Row2 开关上，第 1 排开关无效。）
            if (keyIndex < 8) return settings.Data.EnableRainForRow1;
            if (keyIndex < 16) return settings.Data.EnableRainForRow2;
            if (keyIndex < KeyViewer.FootKeyBase) return settings.Data.EnableRainForRow3;
            return false;
        }

        /// <summary>Return the active drops of one rain row (0/1/2) to the pool / 将某一雨滴排（0/1/2）的活跃雨滴回收到池</summary>
        public void ClearRowDrops(Key[] keys, int row)
        {
            if (keys == null) return;
            if (KeyViewer.IsCustomLayout)
            {
                // Custom slots are depth-ordered, NOT row-packed (row*8..+8 ranges select
                // unrelated keys) — select by each node's own RainRow instead.
                // / 自定义槽位按深度排序而非按排打包（row*8..+8 选中的是无关按键）——
                // 改按节点自身的 RainRow 选取。
                for (int i = 0; i < keys.Length; i++)
                {
                    Key key = keys[i];
                    if (key == null || key.CustomNode == null) continue;
                    if (Mathf.Clamp(key.CustomNode.RainRow, 0, 2) != row) continue;
                    foreach (var rain in key.rainList)
                        ReturnRawRain(rain);
                    key.rainList.Clear();
                }
                for (int i = rainActiveKeys.Count - 1; i >= 0; i--)
                {
                    int ki = rainActiveKeys[i];
                    Key k = ki >= 0 && ki < keys.Length ? keys[ki] : null;
                    if (k != null && k.CustomNode != null && Mathf.Clamp(k.CustomNode.RainRow, 0, 2) == row)
                    {
                        rainActiveSet.Remove(ki);
                        rainActiveKeys.RemoveAt(i);
                    }
                }
            }
            else
            {
                int start = row * 8;
                int end = start + 8;
                for (int i = start; i < end && i < keys.Length; i++)
                {
                    Key key = keys[i];
                    if (key == null) continue;
                    foreach (var rain in key.rainList)
                        ReturnRawRain(rain);
                    key.rainList.Clear();
                }
                for (int i = rainActiveKeys.Count - 1; i >= 0; i--)
                {
                    if (rainActiveKeys[i] >= start && rainActiveKeys[i] < end)
                    {
                        rainActiveSet.Remove(rainActiveKeys[i]);
                        rainActiveKeys.RemoveAt(i);
                    }
                }
            }
            if (Layer != null) Layer.MarkDirty();
        }
    }
}
