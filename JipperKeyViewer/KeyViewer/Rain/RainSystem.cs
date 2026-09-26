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
        /// <summary>Does any live ghost drop exist right now? Bounded by the active-key list, so it
        /// is cheap enough to call once per frame. / 当前是否存在存活鬼雨滴。遍历范围限于活跃键
        /// 列表，故每帧调用一次也很便宜。</summary>
        internal bool HasLiveGhostDrops()
        {
            if (Keys == null) return false;
            for (int i = 0; i < rainActiveKeys.Count; i++)
            {
                int ki = rainActiveKeys[i];
                if ((uint)ki >= (uint)Keys.Length) continue;
                Key key = Keys[ki];
                if (key == null) continue;
                for (int d = 0; d < key.rainList.Count; d++)
                {
                    RawRain rain = key.rainList[d];
                    if (!rain.removed && rain.isGhost) return true;
                }
            }
            return false;
        }

        /// <summary>Only keys that currently own one or more live drops. Render layers use this
        /// instead of scanning every layout slot (custom documents may contain many empty slots).
        /// / 当前拥有存活雨滴的键；渲染层用它替代扫描全部布局槽位。</summary>
        internal List<int> ActiveKeys => rainActiveKeys;
        /// <summary>Merged rain render layers / 合并雨滴渲染层</summary>
        internal RainLayer Layer;
        internal GhostRainLayer GhostLayer;

        private readonly Stack<RawRain> rawRainPool = new Stack<RawRain>();
        private readonly List<int> rainActiveKeys = new List<int>();
        private readonly HashSet<int> rainActiveSet = new HashSet<int>();

        private const int MAX_RAWRAIN_POOL_SIZE = 60;
        /// <summary>Ceiling on LIVE drops per key. The pool cap above bounds REUSE, not the number
        /// of drops alive at once, and every live drop is rebuilt into the single shared merged
        /// mesh each frame. Far above any sane trail (a 20 KPS key on the default 275px/100 speed
        /// lives ~16) and far below anything that threatens the frame. / 每键存活雨滴上限。上面的
        /// 池上限管的是**复用**，不是同时存活的数量，而每一滴存活雨滴每帧都要重建进那一个共享
        /// 合并 mesh。该值远高于任何正常轨迹（默认 275px/100 速度下 20 KPS 的按键约 16 滴），
        /// 又远低于任何会威胁帧时间的量。</summary>
        private const int MaxLiveDropsPerKey = 128;
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
                // The index came from a PREVIOUS Keys array: rainActiveKeys is only emptied by
                // ClearActiveDrops, and that is not called from every path that swaps Keys in
                // (a shorter array after a profile/layout switch, or the empty-array early return
                // above, which does not touch the active set at all). Indexing without this guard
                // throws IndexOutOfRangeException in Update, on EVERY frame, which takes down
                // input processing and press counting for the whole overlay until restart.
                // Make the loop self-heal instead: drop the dead index and carry on.
                // 该下标来自**上一个** Keys 数组：rainActiveKeys 只由 ClearActiveDrops 清空，而
                // 并非所有替换 Keys 的路径都调它（切换配置/布局后数组变短，或上面空数组的早退
                // 完全不碰活跃集）。不守卫就索引会在 Update 里抛 IndexOutOfRangeException，且
                // **每帧**都抛——整个覆盖层的输入处理与按键计数随之失效直到重启。
                // 改为自愈：丢掉这个死下标继续。
                Key key = (uint)ki < (uint)keys.Length ? keys[ki] : null;
                if (key == null || key.rainList.Count == 0)
                {
                    rainActiveSet.Remove(ki);
                    // Keep the active-key list sorted by slot index so the merged render layers
                    // preserve the original draw order. The previous swap-remove was cheaper but
                    // made visual stacking depend on press order once rendering used this sparse list.
                    // 保持活跃键索引有序，确保合并渲染层保留原有绘制顺序。此前 swap-remove 虽便宜，
                    // 但渲染改用稀疏列表后会让叠放顺序依赖按压顺序。
                    rainActiveKeys.RemoveAt(i);
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
                // The per-key press scale is constant for every drop of this key, so look it up
                // once per key instead of once per drop per frame. / 每键的按压缩放对该键的所有
                // 雨滴都是常量，按键取一次即可，不必每滴每帧都查。
                float keyScale = Layer != null ? Layer.GetKeyScale(ki) : 1f;
                for (int j = key.rainList.Count - 1; j >= 0; j--)
                    UpdateSingleRainDrop(key.rainList[j], key, ki, keyPos, j, row, keyScale, dtSec);
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
            // Clamp the per-row height to the same band the settings sliders use. A typed (or
            // hand-edited, or .jkv-imported) height of 100000 with a speed of 1 gives a drop a
            // lifetime of RainHeight*300/RainSpeed ≈ 8.3 HOURS, so one key at 10 presses/second
            // accumulates hundreds of thousands of live drops. Every one is written into the ONE
            // shared merged mesh each frame (4 vertices apiece, ×2/×3 with shadow/outline) and the
            // layer is marked dirty every frame — the frame time collapses completely. The pool
            // cap does not help: it bounds reuse, not live count. A 2000px track is still far
            // taller than any screen.
            // 按排高度钳到设置滑杆同一区间。键入（或手改、或 .jkv 导入）的高度 100000 配速度 1，
            // 雨滴寿命为 RainHeight*300/RainSpeed ≈ 8.3 **小时**，于是单键每秒 10 次按压会堆积
            // 数十万滴存活雨滴。它们每一滴都每帧写进**同一个**合并 mesh（每滴 4 顶点，开阴影/描边
            // 再 ×2/×3），且该层每帧都被标脏——帧时间彻底崩塌。池上限无能为力：它管的是复用，
            // 不是存活数。2000px 的轨道仍远高于任何屏幕。
            const float minHeight = 1f;
            const float maxHeight = 2000f;
            // Speed was the LAST per-row value still missing the scrub the heights got right
            // below, and the argument is identical: unclamped text fields, hand-edited JSON and
            // .jkv imports. `Mathf.Max` is `a > b ? a : b`, so Mathf.Max(NaN, x) returns x and NaN
            // happens to be safe — but Mathf.Max(+Inf, x) returns +Inf and that is the bad case:
            //   UpdateLocation: y = elapsedMs * Inf = +Inf
            //   sizeY = FinalSize.y - dropY + height = Inf - Inf + height = NaN
            //   `if (sizeY < 0)` is FALSE for NaN, so the drop never retires and writes NaN
            //   vertices into the shared mesh every frame for the rest of the session. Ghost
            //   drops never fade out, so an immortal ghost drop is guaranteed. A huge but finite
            //   speed is not a crash but still wrong: while growing, FinalSize.y is reassigned to
            //   the current y each frame, so sizeY algebraically collapses to a constant and the
            //   drop can never recycle off the top — the user sees a frozen full-height bar.
            // 上限与下方高度同样的净化此前**唯独**漏了速度，理由完全相同：未钳制的文本框、手改 JSON、
            // .jkv 导入。`Mathf.Max` 是 `a > b ? a : b`，故 Mathf.Max(NaN, x) 返回 x——NaN 恰好安全；
            // 但 Mathf.Max(+Inf, x) 返回 +Inf，这才是坏的情形：
            //   UpdateLocation: y = elapsedMs * Inf = +Inf
            //   sizeY = FinalSize.y - dropY + height = Inf - Inf + height = NaN
            //   `if (sizeY < 0)` 对 NaN 为**假**，故雨滴永不退役，每帧往共享 mesh 写 NaN 顶点直到
            //   会话结束。鬼雨从不淡出，故不死鬼雨是必然的。极大但有限的速度不崩但同样错：生长
            //   期间 FinalSize.y 每帧被重新赋为当前 y，于是 sizeY 代数上塌成一个常量，雨滴永远
            //   无法越过顶端回收——用户看到的是一根卡住的全高条。
            const float minSpeed = 1e-3f;
            const float maxSpeed = 200f;
            rowSpeeds[0] = Mathf.Clamp(SanitizeRowFloat(settings.Data.RainSpeedRow1, 300f) / 300f, minSpeed, maxSpeed);
            rowSpeeds[1] = Mathf.Clamp(SanitizeRowFloat(settings.Data.RainSpeedRow2, 300f) / 300f, minSpeed, maxSpeed);
            rowSpeeds[2] = Mathf.Clamp(SanitizeRowFloat(settings.Data.RainSpeedRow3, 300f) / 300f, minSpeed, maxSpeed);
            rowHeights[0] = Mathf.Clamp(SanitizeRowFloat(settings.Data.RainHeightRow1, 275f), minHeight, maxHeight);
            rowHeights[1] = Mathf.Clamp(SanitizeRowFloat(settings.Data.RainHeightRow2, 275f), minHeight, maxHeight);
            rowHeights[2] = Mathf.Clamp(SanitizeRowFloat(settings.Data.RainHeightRow3, 275f), minHeight, maxHeight);
            ghostRowSpeeds[0] = Mathf.Clamp(SanitizeRowFloat(settings.Data.GhostRainSpeedRow1, 300f) / 300f, minSpeed, maxSpeed);
            ghostRowSpeeds[1] = Mathf.Clamp(SanitizeRowFloat(settings.Data.GhostRainSpeedRow2, 300f) / 300f, minSpeed, maxSpeed);
            ghostRowSpeeds[2] = Mathf.Clamp(SanitizeRowFloat(settings.Data.GhostRainSpeedRow3, 300f) / 300f, minSpeed, maxSpeed);
            ghostRowHeights[0] = Mathf.Clamp(SanitizeRowFloat(settings.Data.GhostRainHeightRow1, 275f), minHeight, maxHeight);
            ghostRowHeights[1] = Mathf.Clamp(SanitizeRowFloat(settings.Data.GhostRainHeightRow2, 275f), minHeight, maxHeight);
            ghostRowHeights[2] = Mathf.Clamp(SanitizeRowFloat(settings.Data.GhostRainHeightRow3, 275f), minHeight, maxHeight);
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

        private void UpdateSingleRainDrop(RawRain rain, Key key, int keyIndex, Vector2 keyPos, int j, int row, float keyScale, float dtSec)
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
            UpdateRectAndTrail(rain, key, keyIndex, keyPos, speed, height, keyScale);
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

        private void UpdateRectAndTrail(RawRain rain, Key key, int keyIndex, Vector2 keyPos, float speed, float height, float keyScale)
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
            // rain.fadePx is the DIVISOR in RainLayer.AlphaAtD — `(trackH - d) / fade` — and a NaN
            // there yields a NaN alpha, i.e. NaN vertex colours in the SHARED merged rain mesh. The
            // other non-finite values are all harmless by luck: 0 makes fadeStartD equal trackH so
            // every d returns 1f, and ±Infinity makes the comparison short-circuit. NaN is the one
            // that slips through both. The per-node twin (TrailFadePx) was already scrubbed by
            // EnsureCustomNodes; only the global read was exposed. The upper bound is deliberately
            // wider than the 1..200 the GUI slider allows, per the round-86 rule that a load-time
            // clamp must never narrow what the user can pick.
            rain.fadePx = rain.isGhost ? 0f
                : key.CustomNode != null && key.CustomNode.UseCustomRainFade
                    ? (key.CustomNode.TrailFadeEnabled ? key.CustomNode.TrailFadePx : 0f)
                    : (settings.Data.EnableRainGradient
                        ? SanitizeRainGeometry(settings.Data.RainFadePx, 40f, 0f, 200f) : 0f);

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
                // Align against the drop's OWN resolved width, not key.rainWidth. rainWidth is
                // written in exactly one place — the FIXED-layout key factory — so every FreeMake
                // key kept the field default 50f forever while the rect used the real width. With
                // Left/Right alignment the drop then hung off the node edge by (50 - w)/2; with a
                // node 200 wide and RainWidth 80 that is 15px outside the box. The row defaults are
                // already 50/40/30, so row 2/3 nodes were off by 5-10px with NO user config at all.
                // Center alignment was accidentally correct (the offsets cancel), which is why
                // nobody noticed.
                // 用雨滴**自身**解析出的宽度对齐，而不是 key.rainWidth。rainWidth 只有固定布局的
                // 按键工厂写过一次，于是每个 FreeMake 按键永远保留字段默认值 50f，而矩形用的是真实
                // 宽度。左/右对齐时雨滴因此按 (50 - w)/2 挂到节点外：节点宽 200、RainWidth 80 时
                // 就有 15px 在框外。按排默认值本就是 50/40/30，故第 2/3 排节点在**完全没配置**时
                // 就已偏 5–10px。居中对齐恰好正确（两个偏移相消），所以一直没人发现。
                if (key.CustomNode.RainAlignment == 0) alignOffset = 0f;
                else if (key.CustomNode.RainAlignment == 1) alignOffset = (key.keySize.x - w) * 0.5f;
                else alignOffset = key.keySize.x - w;
            }
            float cx = keyPos.x + alignOffset + ox + w * 0.5f;
            float topY = keyPos.y - key.keySize.y * 0.5f + baseStart + RainContainerHeight + travel;

            float s = keyScale;
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
            // Clear the bookkeeping FIRST, unconditionally. It used to sit behind the `keys == null`
            // early-out, so a toggle flipped while the overlay was disabled silently left stale
            // indices in rainActiveKeys — and those indices are exactly what UpdateEffects indexes
            // with. Every other early-out in this class (UpdateEffects, UpdateSingleRainDrop,
            // TriggerRainEffect, UpdateFade) still does its bookkeeping.
            // 先**无条件**清活跃集。此前它在 `keys == null` 早退之后，故覆盖层关闭期间切换开关会
            // 把陈旧下标留在 rainActiveKeys 里——而那些下标正是 UpdateEffects 用来索引的。
            // 本类其它早退（UpdateEffects、UpdateSingleRainDrop、TriggerRainEffect、UpdateFade）
            // 都会照常做记账。
            bool hadActiveKeys = rainActiveKeys.Count != 0;
            rainActiveKeys.Clear();
            rainActiveSet.Clear();
            // Disabling rain is checked from Update every frame. Once the first clear has retired all
            // drops, subsequent frames must be a true no-op: walking every key/rainList and dirtying
            // both merged meshes here recreated a full uGUI rebuild storm while rain was disabled.
            // 雨滴关闭状态下 Update 每帧都会检查。第一次清理完成后，后续帧必须是真正的空操作：否则
            // 每帧遍历所有键/雨滴并标脏两层合并 Mesh，会在关闭雨滴时持续触发完整 uGUI 重建。
            if (!hadActiveKeys) return;
            if (keys == null) return;
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

        /// <summary>The BOTTOM colour stop for a key's rain, resolved from the NODE at the point of
        /// use rather than from the build-time key.rainColor cache. That cache is written in only two
        /// places — CreateCustomKey (build) and ApplyCustomAllColors — but the FreeMake editor
        /// refreshes colours IN PLACE instead of rebuilding (the round-36 per-drag-rebuild fix), so
        /// its node rain-bottom picker left the cache stale. Both this class's readers consumed it:
        /// in-flight drops (RefreshDropColors) and drops born after the edit (CreateRainDropForKey).
        /// The visible result was the exact "the colour control does nothing" symptom — the swatch
        /// updated and the value saved, yet not a single drop changed colour until an unrelated
        /// action (dragging the node, toggling another property, a profile switch) happened to force
        /// a full rebuild. Resolving here makes the invariant local instead of dependent on every
        /// live-edit path remembering to refresh a derived cache.
        /// / 某键雨滴的**底**色色标，在**用时**从**节点**解析，而不是取构建期的 key.rainColor 缓存。
        /// 该缓存只有两处会写——CreateCustomKey（构建）与 ApplyCustomAllColors——而 FreeMake 编辑器
        /// 刻意**就地**重绘而非重建（第 36 轮修掉每次拖动都整层重建那条），于是节点的雨滴底色控件
        /// 把缓存留在了旧值上。本类的两个读取点都吃这个缓存：在飞的雨滴（RefreshDropColors）与编辑
        /// 之后新生的雨滴（CreateRainDropForKey）。可见后果正是「颜色控件点了没反应」：色块更新、
        /// 值也存盘了，却没有一滴雨滴变色，直到某个无关动作（拖动节点、切换别的属性、切配置）
        /// 碰巧触发整层重建。在此处解析让该不变量变成局部的，而不再依赖每条就地编辑路径都记得刷新
        /// 一个派生缓存。
        ///
        /// `fallback` is the row's global rain colour, and is returned unchanged when the node opts out
        /// of per-node colour — matching what CreateCustomKey baked in that case.
        /// `fallback` 是该排的全局雨滴色；节点未启用按节点颜色时原样返回，与 CreateCustomKey 在该
        /// 情况下烘焙的值一致。
        /// </summary>
        private static Color ResolveRainMainColor(Key key, Color fallback)
        {
            return key.CustomNode != null && key.CustomNode.UseCustomRainColor
                && key.CustomNode.RainColorBottom != null
                ? KeyViewer.NodeColor(key.CustomNode.RainColorBottom, fallback)
                : fallback;
        }

        /// <summary>The TOP colour stop. Mirrors CreateRainDropForKey's assignment exactly: the
        /// node's own top colour when it has a two-colour body gradient, otherwise the bottom stop
        /// (so a flat drop stays flat). / **顶**色色标。与 CreateRainDropForKey 的赋值完全一致：
        /// 节点有双色本体渐变时用节点顶色，否则用底色（故单色雨滴保持单色）。</summary>
        private static Color ResolveRainTopColor(Key key, Color main)
        {
            return key.CustomNode != null && key.CustomNode.UseCustomRainColor
                && key.CustomNode.RainColorTop != null
                ? KeyViewer.NodeColor(key.CustomNode.RainColorTop, main)
                : main;
        }

        /// <summary>Apply the per-node rain shadow/outline overrides on top of whatever the row
        /// supplied, and report whether any field actually changed (the live-repaint path uses that
        /// to decide whether the shared layer needs redrawing). Returns false for a null key/node so
        /// callers can invoke it unconditionally.
        ///
        /// This block used to live only inside CreateRainDropForKey, which made the two write paths
        /// disagree: RefreshDropColors repainted the drop BODY from the node, but the shadow and
        /// outline kept the values they were born with. A node's rain shadow/outline colour picker
        /// therefore affected only drops created after the edit — one per keypress — so with a tall
        /// track and a slow speed the control looked completely dead. The global Rain tab papers over
        /// this by CLEARING the in-flight drops instead; the editor never did, and clearing would
        /// resurrect the trail-popping that the in-place repaint was written to remove. One helper,
        /// two callers, so they cannot drift apart again.
        /// / 把按节点雨滴阴影/描边覆盖应用到排所提供之上，并报告是否真的有字段变了（就地重绘路径
        /// 据此决定共享层是否需要重绘）。key/node 为 null 时返回 false，故调用方可无条件调用。
        ///
        /// 这段此前只存在于 CreateRainDropForKey 内部，于是两条写入路径彼此不一致：
        /// RefreshDropColors 会按节点重绘雨滴**本体**，而阴影与描边却保留出生时的值。故节点的雨滴
        /// 阴影/描边颜色控件只对**编辑之后**新生的雨滴生效——每次按压一滴——于是高轨道配慢速度时
        /// 该控件看起来完全失效。全局雨滴页是用「**清空**在飞雨滴」绕过这点的，编辑器从未如此，
        /// 而清空会把就地重绘本就要消除的「轨迹反复弹出」问题带回来。一个辅助方法、两个调用点，
        /// 二者从此无法再漂移。
        ///
        /// Known residual, deliberately left: turning an override flag OFF does not restore the
        /// row's colour on drops that were already born under it — that would need the row defaults
        /// re-derived here, and the drops expire on their own within a second or two anyway. Turning
        /// a flag ON, or changing a colour, both take effect immediately.
        /// **已知残留、有意不处理**：把覆盖开关**关掉**不会让已在其下出生的雨滴恢复排色——那需要在此
        /// 重新推导排默认值，而这些雨滴本就会在一两秒内自行过期。打开开关或改颜色则立即生效。
        /// </summary>
        private static bool ApplyNodeRainOverrides(RawRain rawRain, Key key, bool isGhost)
        {
            FmNode cn = key?.CustomNode;
            if (rawRain == null || cn == null) return false;
            bool changed = false;
            if (!isGhost)
            {
                if (cn.UseCustomRainShadow)
                {
                    changed |= rawRain.shadowEnabled != cn.RainShadowEnabled;
                    rawRain.shadowEnabled = cn.RainShadowEnabled;
                    Color sc = KeyViewer.NodeColor(cn.RainShadowColor, rawRain.shadowColor);
                    changed |= rawRain.shadowColor != sc;
                    rawRain.shadowColor = sc;
                    changed |= rawRain.shadowOffsetX != cn.RainShadowOffsetX;
                    rawRain.shadowOffsetX = cn.RainShadowOffsetX;
                    changed |= rawRain.shadowOffsetY != cn.RainShadowOffsetY;
                    rawRain.shadowOffsetY = cn.RainShadowOffsetY;
                }
                if (cn.UseCustomRainOutline)
                {
                    changed |= rawRain.outlineEnabled != cn.RainOutlineEnabled;
                    rawRain.outlineEnabled = cn.RainOutlineEnabled;
                    Color oc = KeyViewer.NodeColor(cn.RainOutlineColor, rawRain.outlineColor);
                    changed |= rawRain.outlineColor != oc;
                    rawRain.outlineColor = oc;
                    float ow = Mathf.Max(0f, cn.RainOutlineWidth);
                    changed |= rawRain.outlineWidth != ow;
                    rawRain.outlineWidth = ow;
                }
            }
            else
            {
                if (cn.UseCustomGhostRainShadow)
                {
                    changed |= rawRain.shadowEnabled != cn.GhostRainShadowEnabled;
                    rawRain.shadowEnabled = cn.GhostRainShadowEnabled;
                    Color sc = KeyViewer.NodeColor(cn.GhostRainShadowColor, rawRain.shadowColor);
                    changed |= rawRain.shadowColor != sc;
                    rawRain.shadowColor = sc;
                    changed |= rawRain.shadowOffsetX != cn.GhostRainShadowOffsetX;
                    rawRain.shadowOffsetX = cn.GhostRainShadowOffsetX;
                    changed |= rawRain.shadowOffsetY != cn.GhostRainShadowOffsetY;
                    rawRain.shadowOffsetY = cn.GhostRainShadowOffsetY;
                }
                if (cn.UseCustomGhostRainOutline)
                {
                    changed |= rawRain.outlineEnabled != cn.GhostRainOutlineEnabled;
                    rawRain.outlineEnabled = cn.GhostRainOutlineEnabled;
                    Color oc = KeyViewer.NodeColor(cn.GhostRainOutlineColor, rawRain.outlineColor);
                    changed |= rawRain.outlineColor != oc;
                    rawRain.outlineColor = oc;
                    float ow = Mathf.Max(0f, cn.GhostRainOutlineWidth);
                    changed |= rawRain.outlineWidth != ow;
                    rawRain.outlineWidth = ow;
                }
            }
            return changed;
        }

        public Color GetRainColor(byte color) => RainColor(color, false);

        public Color GetGhostRainColor(byte color) => RainColor(color, true);

        /// <summary>Repaint every drop that is currently on screen after a rain/ghost-rain colour
        /// change. A drop's colour is baked in at CREATION, so with the default trail (~0.3 s) a
        /// colour edit was invisible almost immediately — but with a tall track and a slow speed a
        /// drop lives for many seconds, and the settings colour page then appeared to do nothing
        /// until every drop happened to expire. The FreeMake editor already worked around this by
        /// clearing the drops; the settings window had no such call at all. Repainting in place
        /// keeps the trail continuous instead of popping it away.
        /// 在雨滴/鬼雨颜色被改动后重绘**当前在屏**的每一滴。雨滴颜色是在**创建**时烙入的，故默认
        /// 轨迹（~0.3 秒）下颜色改动几乎立刻就看不到了——但高轨道配慢速度时一滴能活好几秒，于是
        /// 设置页颜色区看起来**毫无作用**，直到所有雨滴恰好过期。FreeMake 编辑器此前用「清空雨滴」
        /// 绕过了这点，而设置窗口**根本没有**这个调用。现就地重绘，保持轨迹连续而不是把它弹掉。
        /// </summary>
        public void RefreshDropColors(Key[] keys)
        {
            if (keys == null) return;
            bool touched = false;
            for (int i = 0; i < keys.Length; i++)
            {
                Key key = keys[i];
                if (key == null || key.rainList.Count == 0) continue;
                for (int d = 0; d < key.rainList.Count; d++)
                {
                    RawRain rain = key.rainList[d];
                    if (rain == null || rain.removed) continue;
                    // The NULL test was missing here: the bounds check dereferenced
                    // PerKeyGhostRainColor.Length with only `EnablePerKeyColors` and the index in
                    // front of it. This is reached from the Colors page's RefreshRainDropColors,
                    // i.e. straight out of a GUILayout callback, so a profile whose
                    // PerKeyGhostRainColor is null took the whole settings window down until restart
                    // — and lastSaveError is never set on that path, so no banner either.
                    // CreateRainDropForKey got the same guard in an earlier pass; two copies of one
                    // predicate must agree, and the bound alone was never the point.
                    // 此处**缺判空**：边界检查只隔着 EnablePerKeyColors 与下标就去解引用
                    // PerKeyGhostRainColor.Length。该路径由颜色页的 RefreshRainDropColors 抵达——即
                    // 直接出自 GUILayout 回调——故 PerKeyGhostRainColor 为 null 的配置会让整个设置
                    // 窗口直到重启前失效，而该路径从不设置 lastSaveError，连横幅都没有。
                    // CreateRainDropForKey 在早前一轮补了同样的守卫；同一谓词的两份拷贝必须一致，
                    // 而光有边界检查从来就不是重点。
                    Color[] perKeyGhost = settings.Data.PerKeyGhostRainColor;
                    Color resolved = rain.isGhost
                        ? (key.CustomNode != null
                            ? GetGhostRainColor(key.color)
                            : (settings.Data.EnablePerKeyColors
                                && perKeyGhost != null && i >= 0 && i < perKeyGhost.Length
                                ? perKeyGhost[i]
                                : GetGhostRainColor(key.color)))
                        : ResolveRainMainColor(key, key.rainColor);
                    // The top stop is the same value the drop was born with when the node has no
                    // gradient of its own, so repainting never invents a second colour.
                    // 节点自身没有渐变时，顶色就是雨滴出生时那个值，故重绘不会凭空造出第二种颜色。
                    Color top = resolved;
                    // The per-node two-colour body gradient is resolved against the colour the drop
                    // was born with, so a node using it must be repainted from the node's own
                    // gradient rather than from the global colour.
                    //
                    // `main` and `top` are SEPARATE stops: rain.mainColor is the bottom stop and
                    // rain.ColorTop the top one (RainLayer maps cb = mainColor, ct = ColorTop).
                    // This used to reuse one local, so reading the node's top colour OVERWROTE the
                    // bottom before it was written — and every per-node rain gradient collapsed to a
                    // flat top colour the moment any colour was edited. / 每节点双色本体渐变是相对
                    // 雨滴出生时的颜色解析的，故用了它的节点必须从节点自身渐变重绘，而非从全局颜色。
                    //
                    // `main` 与 `top` 是**两个独立**色标：rain.mainColor 是底色、rain.ColorTop 是
                    // 顶色（RainLayer 里 cb = mainColor、ct = ColorTop）。此前复用同一个局部变量，
                    // 于是读节点顶色时**覆盖掉**了底色再写回——任何一次颜色编辑都会让每个用了双色
                    // 渐变的节点塌成一个纯顶色。
                    if (!rain.isGhost && key.CustomNode != null
                        && key.CustomNode.UseCustomRainColor && key.CustomNode.RainColorTop != null)
                        top = KeyViewer.NodeColor(key.CustomNode.RainColorTop, resolved);
                    // The per-node shadow/outline overrides used to be baked at creation only, so this
                    // repaint fixed the drop body while the trail's shadow and outline kept their
                    // birth colours — a node's rain shadow/outline colour picker then affected only
                    // drops created after the edit, one per keypress. Same helper as creation, so the
                    // two write paths can no longer disagree. Clear drops would have worked too and is
                    // what the global Rain tab does, but it resurrects the trail popping this in-place
                    // repaint exists to avoid.
                    // 按节点阴影/描边覆盖此前只在创建时烘焙，故本次重绘只修好了雨滴本体，而轨迹的阴影
                    // 与描边仍保留出生色——节点的雨滴阴影/描边颜色控件于是只对编辑之后新生的雨滴生效，
                    // 每次按压一滴。与创建共用同一辅助方法，两条写入路径从此无法再不一致。改用清空
                    // 雨滴同样有效（全局雨滴页就是这么做的），但会把本次就地重绘本就要避免的「轨迹反复
                    // 弹出」带回来。
                    if (ApplyNodeRainOverrides(rain, key, rain.isGhost)) touched = true;
                    if (rain.mainColor == resolved && rain.ColorTop == top) continue;
                    rain.mainColor = resolved;
                    rain.ColorTop = top;
                    touched = true;
                }
            }
            if (touched && Layer != null) Layer.MarkDirty();
        }

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

        /// <summary>Per-row height/speed values come from unclamped text fields, hand-edited JSON and
        /// .jkv imports, so NaN/Infinity must be rejected BEFORE any comparison — they pass every
        /// one, and Mathf.Clamp hands NaN straight through. These rows were the only per-row
        /// floats still missing this (RainStartY* was fixed earlier).
        /// 按排高度/速度来自不钳制的文本框、手改 JSON 与 .jkv 导入，故 NaN/Inf 必须在任何比较
        /// **之前**拒绝——它们能通过全部比较，而 Mathf.Clamp 会让 NaN 原样穿透。这几行是当时
        /// 仅存仍缺此净化的按排浮点（RainStartY* 此前已修）。
        /// </summary>
        private static float SanitizeRowFloat(float value, float fallback)
            => float.IsNaN(value) || float.IsInfinity(value) ? fallback : value;

        /// <summary>SanitizeRowFloat plus a clamp, for the GLOBAL rain geometry that is written
        /// straight onto a RawRain and then into the SHARED merged rain mesh.
        ///
        /// SanitizeRowFloat was applied to exactly twelve values — the per-row speed/height triples.
        /// Every other global float reaching the renderer was protected only by Mathf.Clamp or
        /// Mathf.Max(0f, …), and BOTH of those pass NaN straight through (NaN fails both comparisons
        /// in each). RainOutlineCornerRadius is the sharpest case: it is the parameter of
        /// DrawRoundedRainOutline, so one non-finite value in a profile writes non-finite vertices
        /// into the shared mesh and corrupts the whole rain canvas — the same failure the per-node
        /// rain-geometry fix in round 47 was about, one scope up.
        ///
        /// Reachability is narrower than it looks: these are slider fields, so nothing in the GUI
        /// produces NaN. It comes from a hand-edited profile or a shared `.jkv`, and Newtonsoft does
        /// parse a bare `NaN` token, so the file really can carry one. Cheap to close, expensive to
        /// diagnose from a screenshot.
        /// / SanitizeRowFloat 加上钳制，用于那些被**直接**写进 RawRain、随后进入**共享**合并雨滴
        /// mesh 的**全局**雨滴几何。
        ///
        /// SanitizeRowFloat 只覆盖了十二个值——按排速度/高度三元组。其余每个到达渲染器的全局浮点
        /// 仅靠 Mathf.Clamp 或 Mathf.Max(0f, …) 保护，而**两者**都让 NaN 原样穿透（NaN 在各自的
        /// 两个比较里都为假）。`RainOutlineCornerRadius` 最尖锐：它正是 `DrawRoundedRainOutline` 的
        /// 参数，故配置里一个非有限值就会把非有限顶点写进共享 mesh、毁掉整块雨滴画布——与第 47 轮
        /// 按节点雨滴几何那条修复是同一个失效，只是范围更大一层。
        ///
        /// 可达性比看上去窄：这些是滑杆字段，GUI 产生不出 NaN。它来自手改配置或他人分享的
        /// `.jkv`，而 Newtonsoft 确实会解析裸写的 `NaN` token，故文件真能带上一个。补上很便宜，
        /// 而从截图里诊断它很贵。
        /// </summary>
        private static float SanitizeRainGeometry(float value, float fallback, float lo, float hi)
            => float.IsNaN(value) || float.IsInfinity(value) ? fallback : Mathf.Clamp(value, lo, hi);

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
                // Length- and null-guard the per-key ghost rain colour like every other per-key
                // reader does. This one had NO validation at all, while its near-twin
                // RefreshDropColors guards the same array — the exact "two copies of one predicate
                // that can drift" shape. A hand-edited or Newtonsoft-populated profile whose
                // PerKeyGhostRainColor is null or short made this throw inside Update, which
                // cancels the rest of that frame's input processing, KPS, rain and gradients on
                // every frame. Fall back to the ghost colour derived from the key's own row byte,
                // which is what the per-key setting is overriding anyway.
                // 与其它每个每键读取点一样，对每键鬼雨颜色做长度与判空守卫。本处此前**完全没有**
                // 任何校验，而它的近孪生 `RefreshDropColors` 却守卫了同一个数组——正是「同一个
                // 谓词的两份拷贝会漂移」那种形状。手改或由 Newtonsoft 填充的配置若让
                // PerKeyGhostRainColor 为 null 或过短，此处会在 Update 内部抛出，而那会取消该帧
                // 剩余的输入处理、KPS、雨滴与渐变，且每帧如此。回落到由该键自身排色字节推导的鬼
                // 雨颜色——那本来就是每键设置要覆盖的东西。
                Color[] perKeyGhost = settings.Data.PerKeyGhostRainColor;
                rawRain.mainColor = key.CustomNode != null
                    ? GetGhostRainColor(key.color)
                    : settings.Data.EnablePerKeyColors
                        ? (perKeyGhost != null && keyIndex >= 0 && keyIndex < perKeyGhost.Length
                            ? perKeyGhost[keyIndex]
                            : GetGhostRainColor(key.color))
                        : GetGhostRainColor(key.color);

                rawRain.shadowEnabled = row == 1 ? settings.Data.EnableGhostRainShadowRow1
                    : row == 2 ? settings.Data.EnableGhostRainShadowRow2
                    : settings.Data.EnableGhostRainShadowRow3;
                rawRain.shadowColor = row == 1 ? settings.Data.GhostRainShadowColorRow1
                    : row == 2 ? settings.Data.GhostRainShadowColorRow2
                    : settings.Data.GhostRainShadowColorRow3;
                rawRain.shadowOffsetX = SanitizeRainGeometry(row == 1 ? settings.Data.GhostRainShadowOffsetXRow1
                    : row == 2 ? settings.Data.GhostRainShadowOffsetXRow2
                    : settings.Data.GhostRainShadowOffsetXRow3, 0f, -50f, 50f);
                rawRain.shadowOffsetY = SanitizeRainGeometry(row == 1 ? settings.Data.GhostRainShadowOffsetYRow1
                    : row == 2 ? settings.Data.GhostRainShadowOffsetYRow2
                    : settings.Data.GhostRainShadowOffsetYRow3, 0f, -50f, 50f);
                rawRain.outlineEnabled = row == 1 ? settings.Data.EnableGhostRainOutlineRow1
                    : row == 2 ? settings.Data.EnableGhostRainOutlineRow2
                    : settings.Data.EnableGhostRainOutlineRow3;
                rawRain.outlineColor = row == 1 ? settings.Data.GhostRainOutlineColorRow1
                    : row == 2 ? settings.Data.GhostRainOutlineColorRow2
                    : settings.Data.GhostRainOutlineColorRow3;
                rawRain.outlineWidth = SanitizeRainGeometry(row == 1 ? settings.Data.GhostRainOutlineWidthRow1
                    : row == 2 ? settings.Data.GhostRainOutlineWidthRow2
                    : settings.Data.GhostRainOutlineWidthRow3, 2f, 0f, 50f);
            }
            else
            {
                rawRain.mainColor = ResolveRainMainColor(key, key.rainColor);

                rawRain.shadowEnabled = row == 1 ? settings.Data.EnableRainShadowRow1
                    : row == 2 ? settings.Data.EnableRainShadowRow2
                    : settings.Data.EnableRainShadowRow3;
                rawRain.shadowColor = row == 1 ? settings.Data.RainShadowColorRow1
                    : row == 2 ? settings.Data.RainShadowColorRow2
                    : settings.Data.RainShadowColorRow3;
                rawRain.shadowOffsetX = SanitizeRainGeometry(row == 1 ? settings.Data.RainShadowOffsetXRow1
                    : row == 2 ? settings.Data.RainShadowOffsetXRow2
                    : settings.Data.RainShadowOffsetXRow3, 0f, -50f, 50f);
                rawRain.shadowOffsetY = SanitizeRainGeometry(row == 1 ? settings.Data.RainShadowOffsetYRow1
                    : row == 2 ? settings.Data.RainShadowOffsetYRow2
                    : settings.Data.RainShadowOffsetYRow3, 0f, -50f, 50f);
                rawRain.outlineEnabled = row == 1 ? settings.Data.EnableRainOutlineRow1
                    : row == 2 ? settings.Data.EnableRainOutlineRow2
                    : settings.Data.EnableRainOutlineRow3;
                rawRain.outlineColor = row == 1 ? settings.Data.RainOutlineColorRow1
                    : row == 2 ? settings.Data.RainOutlineColorRow2
                    : settings.Data.RainOutlineColorRow3;
                rawRain.outlineWidth = SanitizeRainGeometry(row == 1 ? settings.Data.RainOutlineWidthRow1
                    : row == 2 ? settings.Data.RainOutlineWidthRow2
                    : settings.Data.RainOutlineWidthRow3, 2f, 0f, 50f);
            }

            // Per-node shadow/outline overrides — applied AFTER the row assignment so a null node
            // color keeps the row's. Normal rain and ghost rain each have their own override pair
            // (ghost rows stay the ghost fallback). / 节点级阴影/描边覆盖——在排赋值之后应用，
            // 节点颜色为空时保留排颜色。普通雨与鬼雨各有独立的覆盖组（鬼雨排仍是鬼雨的回退）。
            // Shared with RefreshDropColors: these used to exist only here, so editing a node's
            // rain shadow/outline colour repainted the drop BODY (the subagent's finding) but left
            // the shadow and outline on their creation-time values for the rest of their life.
            ApplyNodeRainOverrides(rawRain, key, isGhost);

            rawRain.outlineCornerRadius = settings.Data.EnableRainRoundedOutline
                ? SanitizeRainGeometry(settings.Data.RainOutlineCornerRadius, 0f, 0f, 20f)
                : 0f;
            rawRain.outlineSides = Mathf.Clamp(settings.Data.RainOutlineSides, 0, 2);
            rawRain.dotted = settings.Data.EnableRainDotted;
            rawRain.dotLength = settings.Data.EnableRainDotted
                ? SanitizeRainGeometry(settings.Data.RainDotLength, 12f, 1f, 100f) : 0f;
            rawRain.gapLength = settings.Data.EnableRainDotted
                ? SanitizeRainGeometry(settings.Data.RainGapLength, 8f, 0f, 100f) : 0f;
            if (key.CustomNode != null)
            {
                FmNode node = key.CustomNode;
                if (isGhost)
                {
                    if (node.UseCustomGhostRainCornerRadius)
                        rawRain.outlineCornerRadius = Mathf.Clamp(node.GhostRainCornerRadius, 0f, 20f);
                    if (node.UseCustomGhostRainBorderSides)
                        rawRain.outlineSides = Mathf.Clamp(node.GhostRainBorderSides, 0, 2);
                    if (node.UseCustomGhostRainDotted)
                        ApplyNodeDotted(rawRain, node.GhostRainDotLength, node.GhostRainGapLength);
                }
                else
                {
                    if (node.UseCustomRainCornerRadius)
                        rawRain.outlineCornerRadius = Mathf.Clamp(node.RainCornerRadius, 0f, 20f);
                    if (node.UseCustomRainBorderSides)
                        rawRain.outlineSides = Mathf.Clamp(node.RainBorderSides, 0, 2);
                    if (node.UseCustomRainDotted)
                        ApplyNodeDotted(rawRain, node.RainDotLength, node.RainGapLength);
                }
            }

            // Per-node top color + start-Y offset (two-color gradient / start offset), and the
            // node-top-anchored track model: custom rain starts at the node's TOP edge — the 275px
            // container constant is tuned for 50px keys and would spawn the trail inside taller
            // nodes. / 节点级顶端颜色与起始 Y 偏移（雨滴双色渐变与起始偏移），
            // 以及 锚定节点顶边的轨道模型：自定义雨滴从节点顶边出发——275px 容器常量是为
            // 50px 键调校的，较高的节点会让轨迹从按键内部冒出来。
            rawRain.ColorTop = ResolveRainTopColor(key, rawRain.mainColor);
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
            // The per-row WIDTH was the last unsanitised per-row value. RawRain applies
            // Mathf.Max(NodeWidth, 1f), which floors NaN (returns 1f) but passes +Inf straight
            // through: FinalSize.x = Inf → rect = (cx - Inf*0.5, …, Inf, h), i.e. xMin = -Inf and
            // xMax = +Inf, and AddQuad wrote those into the SHARED rain mesh. The row defaults are
            // already 50/40/30, so 0..2000 is a generous band that no real config can exceed.
            // 按排**宽度**是最后一个未净化的按排值。RawRain 里的 Mathf.Max(NodeWidth, 1f) 会把 NaN
            // 兜到 1f，却让 +Inf 原样通过：FinalSize.x = Inf → rect = (cx - Inf*0.5, …, Inf, h)，
            // 即 xMin = -Inf、xMax = +Inf，而 AddQuad 把它们写进了**共享**雨滴 mesh。
            // 按排默认值本就是 50/40/30，故 0..2000 远宽于任何真实配置。
            rawRain.NodeWidth = Mathf.Clamp(SanitizeRowFloat(rawRain.NodeWidth, 50f), 0f, 2000f);

            rawRain.isGhost = isGhost;
            rawRain.growing = true;

            // Hard ceiling on LIVE drops per key, independent of every setting. The height clamp
            // in SyncCachedSpeeds bounds the lifetime, but a fast key at an extreme speed with a
            // tall track can still stack far more drops than the merged mesh should ever carry —
            // each one is 4 vertices (×2/×3 with shadow/outline) uploaded every frame. Recycling
            // the OLDEST drop keeps the visual steady: the trail is a stream, so dropping its tail
            // is invisible, and the frame time stops depending on how fast the user plays.
            // 每键存活雨滴的硬上限，与所有设置无关。SyncCachedSpeeds 里的高度钳制限制了寿命，
            // 但高速按键配高轨道仍可能堆出远超合并 mesh 应承载的雨滴数——每滴 4 顶点（开阴影/描边
            // 再 ×2/×3）每帧上传。回收**最老**的一滴使观感稳定：轨迹是一条流，砍掉其尾部不可见，
            // 且帧时间不再取决于用户打得有多快。
            if (key.rainList.Count >= MaxLiveDropsPerKey)
            {
                RawRain oldest = key.rainList[0];
                key.rainList.RemoveAt(0);
                if (oldest != null) ReturnRawRain(oldest);
            }
            key.rainList.Add(rawRain);

            if (!rainActiveSet.Contains(keyIndex))
            {
                rainActiveSet.Add(keyIndex);
                int insert = rainActiveKeys.Count;
                while (insert > 0 && rainActiveKeys[insert - 1] > keyIndex) insert--;
                rainActiveKeys.Insert(insert, keyIndex);
            }
        }

        /// <summary>Apply a node's dotted-rain override. A dot length of 0 means the node EXPLICITLY
        /// turns dotted rain off, which is the only way to keep the global toggle on for everything
        /// else while this node stays solid — the override previously always forced dotted=true, so
        /// opting in a node also forced the effect on it. / 应用节点级点状雨滴覆盖。点长为 0 表示
        /// 该节点显式关闭点状雨滴——这是"全局保持开启、唯独这个节点用实心"的唯一途径；此前覆盖
        /// 一律强制 dotted=true，勾选覆盖反而把效果强加给了该节点。</summary>
        private static void ApplyNodeDotted(RawRain rain, float dotLength, float gapLength)
        {
            if (dotLength > 0f)
            {
                rain.dotted = true;
                rain.dotLength = Mathf.Clamp(dotLength, 1f, 100f);
                rain.gapLength = Mathf.Clamp(gapLength, 0f, 100f);
                return;
            }
            rain.dotted = false;
            rain.dotLength = 0f;
            rain.gapLength = 0f;
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
