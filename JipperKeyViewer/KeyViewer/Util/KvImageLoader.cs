// Shared runtime PNG loader (reflection on ImageConversion.LoadImage): key sprites with a
// fixed 9-slice border, and FreeMake image nodes without one. Allocation-free failure
// paths — every exit either returns a valid sprite/texture or destroys the scratch
// texture before returning null.
// 共享运行时 PNG 加载器（反射调用 ImageConversion.LoadImage）：带固定九宫格边框的按键贴图，
// 以及不带边框的 FreeMake 图片节点。所有失败路径都会先释放占位纹理再返回 null，绝不泄漏。

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using JipperKeyViewer.KeyViewer.Settings;
using UnityEngine;

namespace JipperKeyViewer.KeyViewer.Util
{
    internal static class KvImageLoader
    {
        /// <summary>One cached decode of a PNG, plus the file identity it was decoded from so an
        /// edited file is not served stale. / 一次缓存的 PNG 解码结果，外加解码时的文件身份，
        /// 以便文件被改动后不会返回陈旧结果。</summary>
        private sealed class CachedTexture
        {
            public Texture2D Texture;
            public DateTime LastWriteUtc;
            public long Length;
            /// <summary>Decoded dimensions, kept so the VRAM ledger can be returned when this entry
            /// is evicted. The texture object is about to be destroyed, and reading width/height
            /// after a Destroy is exactly the read-after-destroy the resource audit flagged.
            /// / 解码后的尺寸，留着以便逐出此条目时归还显存账本。贴图对象即将被销毁，而 Destroy
            /// 之后读 width/height 正是资源层审计点出的「读已销毁对象」那一类。
            /// </summary>
            public int LastWidth;
            public int LastHeight;
        }

        // Cross-rebuild cache for the FreeMake image-node path. LoadTexture itself stays uncached on
        // purpose: the editor keeps its OWN cache (fmTexCache, destroyed by its own Clear) and
        // LoadSprite bakes a 9-slice that owns a different lifetime, so making the shared entry point
        // own memory would break both. This is a separate, explicitly-owned cache for the one caller
        // that re-decodes the same files dozens of times a second.
        //
        // WHY it exists: ResetKeyViewer — the rebuild a text-style slider drag drives 60-120x/second
        // on a custom layout — destroyed every custom image and then re-read and re-decoded all of
        // them from disk. One 4096x4096 PNG is a 16 MB synchronous read plus 20-60 ms of PNG inflate
        // and 64 MB of VRAM, twice per node (normal + pressed); ten image nodes is 200-600 ms of
        // blocking main-thread work PER REBUILD. A two-second drag issued 120-240 rebuilds.
        // 跨重建缓存，专供 FreeMake 图片节点路径。LoadTexture 本身刻意不加缓存：编辑器有自己的缓存
        // （fmTexCache，由它自己的 Clear 销毁），而 LoadSprite 烘焙的九宫格拥有不同的生命周期，
        // 若让共享入口持有内存会同时破坏两者。这是为**唯一**那个每秒几十次重复解码同一批文件的
        // 调用方准备的、独立且所有权明确的缓存。
        //
        // 存在的原因：ResetKeyViewer（自定义布局上拖一个文字样式滑杆会每秒触发 60-120 次）会先销毁
        // 全部自定义图片、再从磁盘重新读盘并重新解码。一张 4096x4096 PNG = 16 MB 同步读取 + 20-60 毫秒
        // PNG 解压 + 64 MB 显存，每个节点**两次**（常态 + 按下）；十个图片节点即每次重建 200-600 毫秒
        // 的主线程阻塞。两秒的拖拽会发出 120-240 次重建。
        private static readonly Dictionary<string, CachedTexture> textureCache
            = new Dictionary<string, CachedTexture>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<Texture2D> cachedOwnership = new HashSet<Texture2D>();
        /// <summary>Full-rect Sprite wrappers for cached textures. The wrapper is cheap, but
        /// creating one per drop would churn managed/GC state; key it alongside its texture so a
        /// texture refresh invalidates the wrapper too. / 缓存贴图的整图 Sprite 包装。包装本身
        /// 很轻，但每滴创建会搅动托管分配；随贴图一起键控，贴图刷新时包装同步失效。</summary>
        private static readonly Dictionary<string, Sprite> cachedSprites
            = new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Aggregate VRAM ceiling for cached custom-image textures. The per-file caps
        /// (16 MB on disk, 4096x4096) bound any ONE file but say nothing about the total: a
        /// 4096x4096 RGBA32 texture is 67,108,864 bytes = 64 MB of VRAM for a PNG that is only a few
        /// MB on disk, and each key-bound image node can hold two of them (normal + pressed). The
        /// node caps permit far more than that — twenty max-size nodes is 1.28 GB, and the driver
        /// OOMs on a 4 GB card while the mod reports "loaded everything successfully". A shared .jkv
        /// carrying ~30 such PNGs is enough; no user error required. Mirrors the video ledger's
        /// 256 MB. / 缓存自定义图片贴图的显存总上限。单文件上限（磁盘 16MB、4096x4096）只约束
        /// **单个**文件、对总量毫无约束：一张 4096x4096 RGBA32 是 67,108,864 字节 = 64 MB 显存，
        /// 而磁盘上的 PNG 只有几 MB；且每个带键图片节点可持有**两张**（常态 + 按下）。节点上限允许
        /// 的数量远超此——二十个满尺寸节点即 1.28 GB，4GB 显存的卡上驱动会 OOM，而模组报告
        /// 「全部加载成功」。一个携带约 30 张此类 PNG 的分享 .jkv 就足够，无需用户犯错。
        /// 与视频账本的 256 MB 保持一致。</summary>
        private const long MaxCachedImageBytes = 256L * 1024 * 1024;
        private static long liveCachedImageBytes;

        /// <summary>Is this texture owned by the cross-rebuild cache? A caller that is tearing down
        /// per-rebuild state must NOT destroy it — the next rebuild will hand out the same instance.
        /// / 该贴图是否由跨重建缓存持有？拆除逐次构建状态的调用方**不得**销毁它——下一次构建会交出
        /// 同一个实例。</summary>
        internal static bool IsCacheOwned(Texture2D tex)
            => tex != null && cachedOwnership.Contains(tex);

        /// <summary>Destroy every cached texture. Call on FULL teardown only (disable / OnDestroy),
        /// never per rebuild. / 销毁全部缓存贴图。仅在**完全**拆解时调用（禁用 / OnDestroy），
        /// 绝不可逐次构建调用。</summary>
        internal static void ReleaseCachedTextures()
        {
            foreach (KeyValuePair<string, CachedTexture> pair in textureCache)
            {
                Texture2D tex = pair.Value?.Texture;
                if (tex == null) continue;
                try { UnityEngine.Object.Destroy(tex); }
                catch (Exception) { /* already destroyed by a domain reload */ }
            }
            textureCache.Clear();
            cachedOwnership.Clear();
            cachedSprites.Clear();
            // Pre-blurred variants are cache-owned textures in their own right; leaving the
            // dictionaries alive would pin destroyed Texture2D references and re-hand out broken
            // sprites on the next enable. / 预模糊变体本身就是缓存持有的贴图；字典若不清空，
            // 会钉住已销毁的 Texture2D 引用，并在下次启用时交出坏精灵。
            for (int level = 1; level < preblurCache.Length; level++)
            {
                Dictionary<string, CachedPreblur> cache = preblurCache[level];
                if (cache == null) continue;
                foreach (KeyValuePair<string, CachedPreblur> pair in cache)
                {
                    Texture2D tex = pair.Value?.Texture;
                    if (tex == null) continue;
                    try { UnityEngine.Object.Destroy(tex); }
                    catch (Exception) { /* already destroyed by a domain reload */ }
                }
                cache.Clear();
            }
            // Budget refusals were judged against the OLD live budget; after a wholesale release
            // everything fits again, so the negative cache must not outlive it.
            // 预算级拒绝是按**旧的**实时预算判的；整体释放后一切都放得下，负缓存不得比它更长寿。
            textureRefusals.Clear();
            // Same reasoning as the video ledger's ReleaseAll: reset outright rather than trust the
            // per-entry subtraction, so a domain reload that dropped some entries cannot leave the
            // budget permanently narrowed and silently disable images for the rest of the session.
            // 与视频账本的 ReleaseAll 同理：直接归零而非依赖逐条减计数，使域重载丢掉的条目不会让
            // 预算永久变窄、从而在本次会话余下时间里悄悄禁用图片。
            liveCachedImageBytes = 0;
        }

        /// <summary>Bounded negative cache for LoadTextureCached. WITHOUT it, every refused or
        /// over-budget image was re-read, re-decoded (up to the per-file 16 MB / 4096² caps) and
        /// re-logged on EVERY rebuild — 60-120×/s while a slider is being dragged. Keyed by
        /// mtime+length, so fixing the file on disk invalidates the entry naturally. Two refusal
        /// kinds: file-level (unreadable/corrupt/too large — stable for a given file) and
        /// budget-level (decode succeeded but did not fit — retried as soon as the committed
        /// budget DROPS below the watermark recorded at refusal time, because other images being
        /// released may free room). Bounded to 256 paths; overflow clears wholesale (self-healing,
        /// worst case one extra re-read per path).
        /// / LoadTextureCached 的有界负缓存。没有它，每个被拒或超预算的图片会在**每次**重建时
        /// 被重读、重解码（直至单文件 16MB/4096² 上限）并重刷日志——拖滑杆时即每秒 60-120 次。
        /// 以 mtime+length 为键，用户在磁盘上修好文件即自然失效。两种拒绝：文件级（不可读/
        /// 损坏/超限——对给定文件是稳定的）与预算级（解码成功但放不下——一旦已提交预算**低于**
        /// 拒绝时记录的水位就重试，因为其它图片释放可能腾出空间）。上限 256 条路径；溢出整体
        /// 清空（自愈，最坏代价是每条路径多读一次）。</summary>
        private sealed class CachedRefusal
        {
            public DateTime LastWriteUtc;
            public long Length;
            public bool BudgetRefused;
            public long BudgetAtRefusal;
        }

        private static readonly Dictionary<string, CachedRefusal> textureRefusals
            = new Dictionary<string, CachedRefusal>(StringComparer.OrdinalIgnoreCase);
        private const int MaxTextureRefusals = 256;

        /// <summary>Load a PNG, reusing the previous decode when the file is unchanged. For callers
        /// that re-request the same paths on every rebuild. / 加载 PNG，文件未变时复用上次的解码结果。
        /// 供那些每次重建都重新请求同一批路径的调用方使用。</summary>
        internal static Texture2D LoadTextureCached(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (textureCache.TryGetValue(path, out CachedTexture hit) && hit?.Texture != null)
            {
                // A stat is microseconds; a re-decode is tens of milliseconds. Comparing mtime+length
                // is what keeps an image the user just replaced from showing the previous artwork.
                // 一次 stat 是微秒级，重新解码是几十毫秒级。比对 mtime+长度正是防止「用户刚换的图仍
                // 显示旧画面」的关键。
                try
                {
                    FileInfo fi = new FileInfo(path);
                    if (fi.Exists && fi.LastWriteTimeUtc == hit.LastWriteUtc && fi.Length == hit.Length)
                        return hit.Texture;
                }
                catch (Exception) { /* fall through to a reload */ }
                textureCache.Remove(path);
                cachedOwnership.Remove(hit.Texture);
                try { UnityEngine.Object.Destroy(hit.Texture); }
                catch (Exception) { /* already gone */ }
                // Returning the evicted texture's bytes is what lets a document that swaps one
                // large image for another keep working instead of slowly starving its own budget.
                // 归还被逐出贴图的字节，一个把大图换成另一张的文档才能继续工作，而不是慢慢饿死
                // 自己的预算。
                liveCachedImageBytes -= (long)hit.LastWidth * hit.LastHeight * 4L;
                if (liveCachedImageBytes < 0) liveCachedImageBytes = 0;
                cachedSprites.Remove(path);
            }
            // Negative-cache gate: a stat is microseconds; re-reading + re-decoding a refused file
            // (and re-logging it) is the exact per-frame cost the rebuild loop cannot afford.
            // 负缓存闸：一次 stat 是微秒级，而重读 + 重解码一个被拒文件（并重刷日志）正是重建
            // 循环负担不起的逐帧成本。
            if (textureRefusals.TryGetValue(path, out CachedRefusal refused))
            {
                bool fileUnchanged = false;
                try
                {
                    FileInfo fi = new FileInfo(path);
                    fileUnchanged = fi.LastWriteTimeUtc == refused.LastWriteUtc && fi.Length == refused.Length;
                }
                catch (Exception) { fileUnchanged = false; }
                bool stillHopeless = !refused.BudgetRefused || liveCachedImageBytes >= refused.BudgetAtRefusal;
                if (fileUnchanged && stillHopeless) return null;
                textureRefusals.Remove(path);
            }
            Texture2D loaded = LoadTexture(path);
            if (loaded == null)
            {
                // File-level refusal (unreadable, corrupt, over the per-file caps, or vanished):
                // stable per mtime+length, so cache it. A vanished file stats as "changed" and is
                // cheaply re-attempted, so no special case is needed.
                // 文件级拒绝（不可读、损坏、超单文件上限或已消失）：按 mtime+length 稳定，故缓存。
                // 已消失的文件 stat 结果是「已变化」且重试很廉价，无需特判。
                RecordTextureRefusal(path, budgetRefused: false, budgetAtRefusal: 0);
                return null;
            }
            // Read the real decoded dimensions, not the PNG header: the header is what the per-file
            // cap validates, and only the decoded texture's size is what the GPU actually reserves.
            // 读**解码后**的真实尺寸而非 PNG 头部：头部只是单文件上限校验的对象，真正由 GPU 预留的
            // 是解码后贴图的尺寸。
            int texWidth = loaded.width, texHeight = loaded.height;
            long wanted = (long)texWidth * texHeight * 4L;
            if (liveCachedImageBytes + wanted > MaxCachedImageBytes)
            {
                // Budget-level refusal: the decode SUCCEEDED, so this is not a file property —
                // cache it with the committed-bytes watermark so other images being released can
                // reopen the door (the next call retries as soon as the live budget drops below
                // the recorded level). / 预算级拒绝：解码**成功**，故这不是文件的属性——连同
                // 「已提交字节」水位一起缓存，其它图片释放即可重新打开大门（下一次调用只要
                // 实时预算低于记录水位就重试）。
                RecordTextureRefusal(path, budgetRefused: true, budgetAtRefusal: liveCachedImageBytes);
                try { UnityEngine.Object.Destroy(loaded); }
                catch (Exception) { /* nothing to do */ }
                Loader.Warning($"KeyViewer: image '{path}' skipped — the custom-image texture budget "
                    + $"({MaxCachedImageBytes / (1024 * 1024)} MB) is already committed");
                return null;
            }
            textureRefusals.Remove(path); // the file now loads fine; any stale refusal must go / 文件已能正常加载；任何陈旧拒绝都必须清除
            try
            {
                FileInfo fi = new FileInfo(path);
                textureCache[path] = new CachedTexture
                {
                    Texture = loaded,
                    LastWriteUtc = fi.LastWriteTimeUtc,
                    Length = fi.Length,
                    LastWidth = texWidth,
                    LastHeight = texHeight,
                };
                cachedOwnership.Add(loaded);
                liveCachedImageBytes += wanted;
            }
            catch (Exception)
            {
                // Uncacheable (path vanished between the two opens): still hand the texture over —
                // it is owned by the caller's usual teardown, which will destroy it as before.
                // 无法记录缓存（两次打开之间路径消失）：仍把贴图交出去——它由调用方原有的拆除路径
                // 持有，会照旧被销毁。
            }
            return loaded;
        }

        /// <summary>Get or create a full-rect sprite for a cached rain texture. Use this only on
        /// paths whose lifetime is owned by the cross-rebuild cache. / 获取或创建缓存雨滴贴图的
        /// 整图 Sprite。仅用于由跨重建缓存持有生命周期的路径。</summary>
        internal static Sprite GetOrCreateRainSprite(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Texture2D texture = LoadTextureCached(path);
            if (texture == null) return null;
            if (cachedSprites.TryGetValue(path, out Sprite sprite) && sprite != null) return sprite;
            try
            {
                sprite = Sprite.Create(texture, new Rect(0f, 0f, texture.width, texture.height),
                    new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, Vector4.zero);
                cachedSprites[path] = sprite;
                return sprite;
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: failed to create rain sprite from '{path}': {e.Message}");
                return null;
            }
        }

        // ---- Rain pre-blur (level 1/2/3 ≈ radius 2/4/8 px, baked on the CPU) ----
        // ---- 雨滴预模糊（档位 1/2/3 约对应半径 2/4/8 像素，CPU 侧烘焙）----

        /// <summary>WORKING resolution of the pre-blur, not a refusal threshold. A source larger than
        /// this is DOWNSCALED to it (area average) and then blurred, because real rain art is often a
        /// photo (1280x1871) and refusing it meant the blur slider silently did nothing at all. The
        /// drop is drawn scaled anyway, and a blurred variant is soft by definition. 1024 keeps the
        /// bake cheap (~1 M pixels, one cached texture per image and level).
        /// / 预模糊的**工作分辨率**，不是拒绝阈值。比它大的源图会被**降采样**（面积平均）后再模糊——
        /// 真实雨滴素材常常是一张照片（1280x1871），而拒绝它意味着模糊滑条静默地什么也没做。雨滴
        /// 本就是缩放绘制，且模糊变体按定义就是柔和的。1024 让烘焙保持廉价（~100 万像素，每图每档
        /// 一份缓存纹理）。</summary>
        internal const int MaxPreblurDimension = 1024;
        /// <summary>Box radius baked per level, index = level. 3 box passes approximate a gaussian;
        /// the level semantics are "visibly softer", not an exact pixel radius.
        /// / 每档位烘焙的盒式半径，下标即档位。3 次盒式模糊近似高斯；档位语义是「明显更柔和」，
        /// 不承诺精确像素半径。</summary>
        internal static readonly int[] PreblurRadiusByLevel = { 0, 2, 4, 8 };

        /// <summary>One cached pre-blurred rain sprite plus the source texture instance it was
        /// derived from. The Source reference is the staleness test: when the underlying file is
        /// edited, LoadTextureCached re-decodes into a NEW texture instance and this entry no
        /// longer matches — the next request rebuilds from the fresh decode.
        /// / 一条缓存的预模糊雨滴精灵，及其来源贴图实例。Source 引用即陈旧判据：底层文件被改动后
        /// LoadTextureCached 会解码出**新的**贴图实例，本条目随之失配——下一次请求即用新解码重建。</summary>
        private sealed class CachedPreblur
        {
            public Sprite Sprite;
            public Texture2D Texture;
            public Texture2D Source;
            public long Bytes;
        }

        // One dictionary PER LEVEL, never a composite key on the base dict: levels must not evict
        // each other, and ReleaseCachedTextures destroys every level in one sweep.
        // 每个档位一个独立字典，绝不在基础字典上做复合键：档位之间不得互相覆盖，
        // ReleaseCachedTextures 一次遍历即可销毁全部档位。
        private static readonly Dictionary<string, CachedPreblur>[] preblurCache =
            new Dictionary<string, CachedPreblur>[ProfileData.MaxRainBlurLevel + 1];

        static KvImageLoader()
        {
            for (int level = 1; level < preblurCache.Length; level++)
                preblurCache[level] = new Dictionary<string, CachedPreblur>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Get the rain sprite for a path at a pre-blur level (0 = unblurred original).
        /// Never throws, never returns a broken sprite: on ANY failure (unreadable source, refused
        /// blur, budget full) it degrades to the ORIGINAL sprite, because a crisp raindrop always
        /// beats a missing one. / 按预模糊档位获取雨滴精灵（0 = 原图）。绝不抛出、绝不返回坏精灵：
        /// 任何失败（源不可读、模糊被拒、预算已满）都退回**原图**——清晰的雨滴总比没有雨滴好。</summary>
        internal static Sprite GetOrCreateRainSprite(string path, byte blurLevel)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (blurLevel <= 0) return GetOrCreateRainSprite(path);
            if (blurLevel > ProfileData.MaxRainBlurLevel) blurLevel = ProfileData.MaxRainBlurLevel;
            Sprite baseSprite = GetOrCreateRainSprite(path);
            if (baseSprite == null) return null;
            Texture2D srcTex = baseSprite.texture;
            if (srcTex == null) return baseSprite;
            Dictionary<string, CachedPreblur> cache = preblurCache[blurLevel];
            if (cache.TryGetValue(path, out CachedPreblur entry))
            {
                if (entry?.Sprite != null && entry.Source == srcTex) return entry.Sprite;
                // Stale: the source was re-decoded (file edited) or the entry is broken. Destroy the
                // old variant so its VRAM returns to the ledger before the rebuild attempt.
                // 陈旧：源被重新解码（文件被改动）或条目损坏。先销毁旧变体，把显存还给账本再尝试重建。
                DestroyPreblurEntry(cache, path);
            }
            Texture2D blurred = PreblurRainTexture(srcTex, blurLevel);
            if (blurred == null) return baseSprite; // refused / failed → original / 被拒或失败 → 原图
            long wanted = (long)blurred.width * blurred.height * 4L;
            if (liveCachedImageBytes + wanted > MaxCachedImageBytes)
            {
                // Budget red line: degrade to the original rather than OOM. No negative-cache entry
                // here — the next request retries cheaply once other entries free room.
                // 预算红线：宁可退化回原图也不 OOM。这里不写负缓存——其它条目腾出空间后下一次
                // 请求即可廉价重试。
                try { UnityEngine.Object.Destroy(blurred); }
                catch (Exception) { /* nothing to release */ }
                Loader.Warning($"KeyViewer: rain pre-blur '{path}' skipped — the custom-image texture "
                    + $"budget ({MaxCachedImageBytes / (1024 * 1024)} MB) is already committed");
                return baseSprite;
            }
            Sprite sprite;
            try
            {
                sprite = Sprite.Create(blurred,
                    new Rect(0f, 0f, blurred.width, blurred.height), new Vector2(0.5f, 0.5f),
                    100f, 0, SpriteMeshType.FullRect, Vector4.zero);
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: failed to create pre-blurred rain sprite from '{path}': {e.Message}");
                try { UnityEngine.Object.Destroy(blurred); }
                catch (Exception) { /* nothing to release */ }
                return baseSprite;
            }
            cachedOwnership.Add(blurred);
            liveCachedImageBytes += wanted;
            cache[path] = new CachedPreblur
            {
                Sprite = sprite,
                Texture = blurred,
                Source = srcTex,
                Bytes = wanted,
            };
            return sprite;
        }

        private static void DestroyPreblurEntry(Dictionary<string, CachedPreblur> cache, string path)
        {
            if (!cache.TryGetValue(path, out CachedPreblur entry) || entry == null) return;
            cache.Remove(path);
            Texture2D tex = entry.Texture;
            if (tex != null)
            {
                cachedOwnership.Remove(tex);
                try { UnityEngine.Object.Destroy(tex); }
                catch (Exception) { /* already destroyed by a domain reload */ }
            }
            liveCachedImageBytes -= entry.Bytes;
            if (liveCachedImageBytes < 0) liveCachedImageBytes = 0;
        }

        /// <summary>Area-average downscale of a straight-alpha RGBA buffer. Pure, so the Harness can
        /// verify it: a uniform 2x2 block collapses to one pixel of the same colour, four different
        /// pixels average to their mean, and the alpha channel is averaged with the colour (never
        /// premultiplied, so a transparent-but-coloured pixel does not drag its hue into the result).
        /// Never upscales. / 直通 alpha RGBA 缓冲的面积平均降采样。纯函数，Harness 可验证：同色 2x2
        /// 块塌成同色单像素，四个不同像素取均值；alpha 与颜色一并平均（**不做**预乘，故带颜色但透明
        /// 的像素不会把色相拖进结果）。绝不放大。</summary>
        internal static Color32[] DownscaleBox(Color32[] src, int w, int h, int dw, int dh)
        {
            if (src == null || w <= 0 || h <= 0 || dw <= 0 || dh <= 0) return null;
            if (src.Length != w * h) return null;
            if (dw == w && dh == h) return (Color32[])src.Clone();
            if (dw > w || dh > h) return null; // no upscaling / 不放大
            Color32[] dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int y0 = y * h / dh;
                int y1 = ((y + 1) * h + dh - 1) / dh;
                if (y1 <= y0) y1 = y0 + 1;
                if (y1 > h) y1 = h;
                for (int x = 0; x < dw; x++)
                {
                    int x0 = x * w / dw;
                    int x1 = ((x + 1) * w + dw - 1) / dw;
                    if (x1 <= x0) x1 = x0 + 1;
                    if (x1 > w) x1 = w;
                    long r = 0, g = 0, b = 0, a = 0;
                    int n = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        for (int xx = x0; xx < x1; xx++)
                        {
                            Color32 c = src[yy * w + xx];
                            r += c.r; g += c.g; b += c.b; a += c.a;
                            n++;
                        }
                    }
                    if (n == 0) n = 1;
                    dst[y * dw + x] = new Color32(
                        (byte)(r / n), (byte)(g / n), (byte)(b / n), (byte)(a / n));
                }
            }
            return dst;
        }

        /// <summary>CPU pre-blur of a texture: premultiplied-alpha, three separable box passes with a
        /// running-sum window (edge-clamped, so the kernel stays normalized at borders — an
        /// all-opaque texture keeps alpha 255 exactly, with zero attenuation). The source texture is
        /// never mutated. Returns null on any refusal: over-size, unreadable (non-readable textures
        /// throw on GetPixels32), or internal failure — callers fall back to the original.
        /// / 贴图的 CPU 预模糊：预乘 alpha，三趟可分离盒式模糊（滑动窗口、边缘钳制，故核在边界处
        /// 依旧归一化——全不透明贴图的 alpha 精确保持 255，零衰减）。源贴图绝不被修改。任何拒绝
        /// （超尺寸、不可读（不可读贴图调 GetPixels32 会抛出）或内部失败）都返回 null，调用方退回原图。</summary>
        internal static Texture2D PreblurRainTexture(Texture2D src, byte level)
        {
            if (src == null || level <= 0 || level > ProfileData.MaxRainBlurLevel) return null;
            int radius = PreblurRadiusByLevel[level];
            int w = src.width, h = src.height;
            if (w <= 0 || h <= 0) return null;
            Color32[] pixels;
            try { pixels = src.GetPixels32(); }
            catch (Exception) { return null; } // non-readable source / 源不可读
            if (pixels == null || pixels.Length != w * h) return null;
            // Real rain art is usually far larger than a rain sprite (a 1280x1871 photo), and
            // REFUSING it meant the blur slider silently did nothing. Downscale to the working
            // resolution instead: the drop is drawn scaled anyway, and a blurred variant is soft
            // by definition. The blur radius then lives in the downscaled space, which is what the
            // user sees.
            // 真实雨滴素材通常远大于雨滴精灵（一张 1280x1871 的照片），而**拒绝**它意味着模糊滑条
            // 静默地什么也没做。改为先降到工作分辨率：雨滴本就是缩放绘制，且模糊变体按定义就是柔和
            // 的。模糊半径此后存在于降采样空间里——那正是用户看到的东西。
            if (w > MaxPreblurDimension || h > MaxPreblurDimension)
            {
                float k = (float)MaxPreblurDimension / Mathf.Max(w, h);
                int dw = Mathf.Max(1, Mathf.RoundToInt(w * k));
                int dh = Mathf.Max(1, Mathf.RoundToInt(h * k));
                Color32[] scaled = DownscaleBox(pixels, w, h, dw, dh);
                if (scaled == null) return null;
                pixels = scaled;
                w = dw;
                h = dh;
            }
            Color32[] blurred;
            try { blurred = PreblurPixels(pixels, w, h, radius); }
            catch (Exception) { return null; }
            Texture2D outTex = null;
            try
            {
                outTex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                outTex.SetPixels32(blurred);
                // Sampler settings BEFORE Apply: they are uploaded with the pixel data, so a
                // non-readable texture never needs a second upload for them. Copied from the source
                // so the blurred variant samples exactly like the original did.
                // 采样设置在 Apply **之前**：它们随像素数据一起上传，不可读贴图无需为此二次上传。
                // 从源贴图复制，保证模糊变体与原图的采样行为完全一致。
                outTex.wrapMode = src.wrapMode;
                outTex.filterMode = src.filterMode;
                outTex.anisoLevel = src.anisoLevel;
                outTex.Apply(false, true); // upload once, drop the CPU copy / 上传一次，丢弃 CPU 副本
                return outTex;
            }
            catch (Exception)
            {
                if (outTex != null)
                {
                    try { UnityEngine.Object.Destroy(outTex); }
                    catch (Exception) { /* nothing to release */ }
                }
                return null;
            }
        }

        /// <summary>The pure, Unity-free core of the rain pre-blur — what the Harness tests. Input is
        /// a straight-alpha RGBA buffer of w*h pixels; output is the blurred equivalent. Premultiply →
        /// three edge-clamped separable box passes (radius r each; the window size 2r+1 is the
        /// constant divisor, which IS the kernel normalization) → unpremultiply. Premultiplication is
        /// what keeps transparent pixels from bleeding their (often garbage) RGB into neighbours.
        /// / 雨滴预模糊的纯函数核心——Harness 测的就是它。输入为 w*h 个直通 alpha 的 RGBA 像素，
        /// 输出为模糊后的等价结果。预乘 → 三趟边缘钳制的可分离盒式模糊（半径均为 r；窗口大小
        /// 2r+1 即除数，这正是核归一化）→ 逆预乘。预乘正是防止透明像素把（常为垃圾值的）RGB
        /// 渗进邻居的手段。</summary>
        internal static Color32[] PreblurPixels(Color32[] src, int w, int h, int radius)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (w <= 0 || h <= 0) throw new ArgumentOutOfRangeException(nameof(w), "empty blur domain");
            if (src.Length != w * h) throw new ArgumentException("src length != w*h", nameof(src));
            if (radius <= 0) return (Color32[])src.Clone();
            int n = w * h;
            // Premultiply into float channels. Sums stay below 2^24 (65025 * 17 max), so every
            // accumulate/subtract below is EXACT in float32 — the opaque-alpha invariant then holds
            // without rounding drift. / 预乘到 float 通道。和值不超过 2^24（最大 65025*17），
            // 下方所有累加/消减在 float32 下**精确**——不透明 alpha 不变量因此不受舍入漂移影响。
            float[] r = new float[n];
            float[] g = new float[n];
            float[] b = new float[n];
            float[] a = new float[n];
            for (int i = 0; i < n; i++)
            {
                float af = src[i].a;
                r[i] = src[i].r * af;
                g[i] = src[i].g * af;
                b[i] = src[i].b * af;
                a[i] = af;
            }
            int window = radius * 2 + 1;
            float invWindow = 1f / window;
            // Three box passes, each separable horizontal+vertical with a sliding window and
            // edge-clamped sampling (out-of-range reads duplicate the border pixel, so the divisor
            // stays 2r+1 and the kernel remains normalized at the borders).
            // 三趟盒式模糊，每趟水平+垂直可分离，滑动窗口 + 边缘钳制采样（越界读取复制边界像素，
            // 除数恒为 2r+1，核在边界处依旧归一化）。
            float[] tmp = new float[n];
            for (int pass = 0; pass < 3; pass++)
            {
                BoxPass(r, tmp, w, h, radius, invWindow, true); BoxPass(r, tmp, w, h, radius, invWindow, false);
                BoxPass(g, tmp, w, h, radius, invWindow, true); BoxPass(g, tmp, w, h, radius, invWindow, false);
                BoxPass(b, tmp, w, h, radius, invWindow, true); BoxPass(b, tmp, w, h, radius, invWindow, false);
                BoxPass(a, tmp, w, h, radius, invWindow, true); BoxPass(a, tmp, w, h, radius, invWindow, false);
            }
            // Unpremultiply. Each BoxPass already wrote normalized (window-averaged) values, so no
            // further divisor here: straight RGB = premulRGB / alpha on the 0..255 scale. Where
            // alpha collapses to ~0 the RGB is undefined — write black with the blurred alpha
            // (which is ~0 anyway), never divide by zero.
            // 逆预乘。每趟 BoxPass 写出的已是归一化（窗口均值）值，此处不再有除数：直通
            // RGB = 预乘RGB / alpha（均为 0..255 标度）。alpha 收敛到 ~0 处 RGB 无定义——写入
            // 模糊后的 alpha（其本身 ~0）配黑色，绝不除零。
            Color32[] dst = new Color32[n];
            for (int i = 0; i < n; i++)
            {
                float af = a[i];
                if (af > 0.5f)
                {
                    float ia = 1f / af;
                    dst[i] = new Color32(
                        (byte)Clamp255(r[i] * ia + 0.5f),
                        (byte)Clamp255(g[i] * ia + 0.5f),
                        (byte)Clamp255(b[i] * ia + 0.5f),
                        (byte)Clamp255(af + 0.5f));
                }
                else
                {
                    dst[i] = new Color32(0, 0, 0, (byte)Clamp255(af + 0.5f));
                }
            }
            return dst;
        }

        private static void BoxPass(float[] channel, float[] tmp, int w, int h, int radius,
            float invWindow, bool horizontal)
        {
            if (horizontal)
            {
                for (int y = 0; y < h; y++)
                {
                    int row = y * w;
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int x = k < 0 ? 0 : (k >= w ? w - 1 : k);
                        sum += channel[row + x];
                    }
                    for (int x = 0; x < w; x++)
                    {
                        tmp[row + x] = sum * invWindow;
                        int addX = x + radius + 1 >= w ? w - 1 : x + radius + 1;
                        int subX = x - radius < 0 ? 0 : x - radius;
                        sum += channel[row + addX] - channel[row + subX];
                    }
                }
                CopyInto(channel, tmp);
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int y = k < 0 ? 0 : (k >= h ? h - 1 : k);
                        sum += channel[y * w + x];
                    }
                    for (int y = 0; y < h; y++)
                    {
                        tmp[y * w + x] = sum * invWindow;
                        int addY = y + radius + 1 >= h ? h - 1 : y + radius + 1;
                        int subY = y - radius < 0 ? 0 : y - radius;
                        sum += channel[addY * w + x] - channel[subY * w + x];
                    }
                }
                CopyInto(channel, tmp);
            }
        }

        private static void CopyInto(float[] dst, float[] src)
        {
            Array.Copy(src, dst, dst.Length);
        }

        private static float Clamp255(float v)
        {
            return v < 0f ? 0f : (v > 255f ? 255f : v);
        }

        /// <summary>Record a refusal for the negative cache (bounded; overflow clears wholesale).
        /// / 为负缓存记录一次拒绝（有界；溢出整体清空）。</summary>
        private static void RecordTextureRefusal(string path, bool budgetRefused, long budgetAtRefusal)
        {
            try
            {
                if (textureRefusals.Count >= MaxTextureRefusals) textureRefusals.Clear();
                FileInfo fi = new FileInfo(path);
                textureRefusals[path] = new CachedRefusal
                {
                    LastWriteUtc = fi.LastWriteTimeUtc,
                    Length = fi.Length,
                    BudgetRefused = budgetRefused,
                    BudgetAtRefusal = budgetAtRefusal,
                };
            }
            catch (Exception)
            {
                // Unstatable path: record it with sentinels so the next call's stat compare fails
                // and retries — a missing file is cheap to re-attempt.
                // 无法 stat 的路径：以哨兵值记录，下一次调用的 stat 比对必然失败并重试——重试一个
                // 不存在的文件本就很廉价。
                textureRefusals[path] = new CachedRefusal
                {
                    LastWriteUtc = DateTime.MinValue,
                    Length = -1L,
                    BudgetRefused = budgetRefused,
                    BudgetAtRefusal = budgetAtRefusal,
                };
            }
        }

        private static bool _loadImageCached;
        private static MethodInfo _cachedLoadImage;
        private static int _loadImageParamCount;
        /// <summary>Rate limit for retrying a FAILED reflection lookup, in seconds. A success is
        /// cached forever; an absence is retried this often so a late-loading module recovers,
        /// without turning a genuinely missing one into a per-image log spam. / 反射查找**失败**后
        /// 的重试间隔（秒）。成功永久缓存；「缺席」则按此频率重试，使较晚加载的模块能恢复，
        /// 同时不让真的缺失变成每张图刷一次日志。
        /// </summary>
        private const float _loadImageRetrySeconds = 10f;
        private static float _loadImageRetryAfter = float.NegativeInfinity;
        private static float _loadImageRetryLoggedAfter = float.NegativeInfinity;

        /// <summary>
        /// Load a PNG as a Sprite. Pass a border to get a 9-slice sprite (bordered images smaller
        /// than twice the border are rejected as corrupt); pass null for a plain full-rect sprite.
        /// / 加载 PNG 为 Sprite。传 border 得到九宫格贴图（带边框时小于两倍边框的图按损坏拒绝）；
        ///   传 null 得到普通整图贴图。
        /// </summary>
        internal static Sprite LoadSprite(string path, Vector4? border)
        {
            Texture2D tex = LoadTexture(path);
            if (tex == null) return null;
            Vector4 b = border ?? Vector4.zero;
            if (border.HasValue)
            {
                // Derive the requirement from the border itself: the hardcoded 22 came from an 11px
                // border, while KeyViewerResources passes Vector4(11,11,11,11) — a magic number that
                // silently stops matching if any caller ever uses a different border.
                // 需求从 border 本身推导：硬编码的 22 来自 11px 边框，而 KeyViewerResources 传的是
                // Vector4(11,11,11,11)——一旦有调用方换用别的边框，这个魔数就会静默失准。
                float need = b.x + b.z;
                if (tex.width < need || tex.height < need)
                {
                    int w = tex.width, h = tex.height;
                    UnityEngine.Object.Destroy(tex);
                    Loader.Error($"KeyViewer: sprite '{path}' too small ({w}x{h}) for its {need}px 9-slice border");
                    return null;
                }
            }
            try
            {
                // Read the dimensions BEFORE any Destroy: reading tex.width after Destroy relies on
                // the deferred-destroy implementation detail and throws under DestroyImmediate or a
                // domain reload. / 先取值再销毁：Destroy 之后读 tex.width 依赖延迟销毁的实现细节，
                // 在 DestroyImmediate 或域重载下会抛异常。
                float pw = tex.width, ph = tex.height;
                Sprite sprite = Sprite.Create(tex, new Rect(0, 0, pw, ph),
                    new Vector2(0.5f, 0.5f), 100f, 0,
                    border.HasValue ? SpriteMeshType.Tight : SpriteMeshType.FullRect, b);
                return sprite;
            }
            catch (Exception e)
            {
                UnityEngine.Object.Destroy(tex);
                Loader.Error($"KeyViewer: failed to create sprite from '{path}': {DescribeException(e)}");
                return null;
            }
        }

        /// <summary>Reflection wraps whatever the callee threw in a TargetInvocationException whose
        /// own Message is the useless "Exception has been thrown by the target of an invocation." —
        /// every real cause (corrupt PNG, unsupported format) was therefore unreportable. Report
        /// the INNER exception instead. / 反射会把被调用方的异常包进 TargetInvocationException，
        /// 其自身的 Message 是无用的"Exception has been thrown by the target of an invocation."——
        /// 真实原因（PNG 损坏、格式不支持）此前永远无法上报。改为上报内层异常。</summary>
        private static string DescribeException(Exception e)
        {
            Exception inner = e is TargetInvocationException tie ? tie.InnerException : null;
            return inner != null ? $"{inner.GetType().Name}: {inner.Message}" : $"{e.GetType().Name}: {e.Message}";
        }

        /// <summary>Refuse absurd image files before handing bytes to the decoder. The path comes
        /// from user-editable profile data (any absolute path is accepted), and LoadImage allocates
        /// by the dimensions declared in the PNG header — a 65535x65535 file asks for ~17 GB of
        /// VRAM and an OutOfMemoryException would be caught by the generic handler below and the
        /// load "succeeded" with a broken texture. A .jkv from someone else can carry such a file. /
        /// 在把字节交给解码器前拒绝离谱的图片。路径来自用户可编辑的配置（接受任意绝对路径），
        /// 而 LoadImage 按 PNG 头部声明的尺寸分配——65535x65535 需要约 17 GB 显存，其
        /// OutOfMemoryException 会被下方的通用 catch 吞掉、加载"成功"却得到损坏贴图。他人分享的
        /// .jkv 就能携带这种文件。</summary>
        private const long MaxImageBytes = 16L * 1024 * 1024;

        /// <summary>Ceiling on ONE axis. 4096 was not chosen by a pixel budget but is exactly one:
        /// 4096x4096 RGBA32 is 67,108,864 bytes = 64 MB, the largest single texture this loader has
        /// always claimed to allow. Both edges being capped at 4096 also means no accepted image can
        /// exceed 4096x4096 in area, so the cap IS the pixel budget — a second, wider area rule
        /// would be unreachable code. / 单边上限。4096 不是按像素预算挑的，而是恰好等于一个：
        /// 4096x4096 RGBA32 是 67,108,864 字节 = 64 MB，是本加载器一直声称允许的最大单张贴图。
        /// 且两条边都被 4096 挡住时，任何被接受的图片面积都不可能超过 4096x4096——所以单边
        /// 上限**就是**像素预算，再加一条更宽松的面积规则只会是走不到的死代码。</summary>
        private const int MaxImageEdge = 4096;

        /// <summary>Is this decoded (or PNG-header-declared) size one the loader accepts? Shared by
        /// the pre-decode PNG header check and the post-decode check so the two can never disagree
        /// about what "reasonable" means — they did once, when only the PNG path had a per-axis
        /// rule. / 解码后（或 PNG 头部声明）的尺寸是否是加载器接受的？供解码前的 PNG 头部检查
        /// 与解码后的检查共用，使两者永远不会对「合理」的定义产生分歧——它们曾分歧过，那时只有
        /// PNG 那条路径带着单边规则。</summary>
        internal static bool SizeWithinLimits(int width, int height)
        {
            if (width <= 0 || height <= 0) return false;
            return width <= MaxImageEdge && height <= MaxImageEdge;
        }

        /// <summary>Which limit a refused size broke, with the numbers — "unusable size" sent every
        /// report of this back to the decoder instead of to the guard. / 被拒的尺寸破了哪一条上限，
        /// 带上数字——「尺寸不可用」把每一条此类报告都指回解码器而非这道防线。</summary>
        private static string DescribeSizeRejection(int width, int height)
        {
            if (width <= 0 || height <= 0) return "an unusable size";
            return $"an edge over the {MaxImageEdge}px limit";
        }

        private static bool IsPngSignature(byte[] bytes)
            => bytes != null && bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50
               && bytes[2] == 0x4E && bytes[3] == 0x47;

        /// <summary>Read width/height out of a PNG IHDR. False when the file is too short to hold
        /// one — the caller then lets the decoder speak, because a truncated file's real failure is
        /// corruption, not size. / 从 PNG 的 IHDR 读出宽高。文件短到装不下时返回 false——此时交给
        /// 解码器发言，因为被截断的文件真正的故障是损坏而非尺寸。</summary>
        private static bool TryReadPngHeaderSize(byte[] bytes, out int width, out int height)
        {
            width = 0; height = 0;
            if (bytes == null || bytes.Length < 33) return false;
            width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return true;
        }

        /// <summary>Load a PNG as a Texture2D (FreeMake image nodes draw the texture directly). /
        /// 加载 PNG 为 Texture2D（FreeMake 图片节点直接绘制纹理）。</summary>
        internal static Texture2D LoadTexture(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            Texture2D tex = null;
            try
            {
                // Read through a single FileStream and enforce the cap on the bytes actually read.
                // The previous shape — `new FileInfo(path).Length` to decide, then
                // File.ReadAllBytes(path) to load — is a TOCTOU window: the stat and the read are
                // two separate opens, so a file that grows in between (or a path that resolves
                // differently on the second open) bypasses the documented 16 MB limit and lands the
                // whole thing in a managed byte[]. This 16 MB cap is the only thing between a
                // user-supplied / .jkv-carried path and an unbounded allocation, so make it exact
                // rather than advisory. It also avoids allocating a FileInfo we never dispose.
                // 经单个 FileStream 读取，并按**实际读到的**字节强制上限。旧写法是
                // `new FileInfo(path).Length` 判定、再 `File.ReadAllBytes(path)` 载入——这是一个
                // TOCTOU 窗口：stat 与读取是两次独立的打开，故期间增长的文件（或第二次打开时
                // 解析到别处的路径）能绕过文档承诺的 16MB 上限，把整份内容塞进托管 byte[]。
                // 这道 16MB 上限是「用户可填 / .jkv 可携带的路径」与无界分配之间**唯一**的屏障，
                // 故它必须是精确的而非参考性的；顺带省掉一个从不释放的 FileInfo。
                byte[] bytes;
                using (FileStream src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (src.Length > MaxImageBytes)
                    {
                        Loader.Error($"KeyViewer: image '{path}' is {src.Length / (1024 * 1024)} MB, over the {MaxImageBytes / (1024 * 1024)} MB limit — not loaded");
                        return null;
                    }
                    bytes = new byte[src.Length];
                    int filled = 0;
                    while (filled < bytes.Length)
                    {
                        int read = src.Read(bytes, filled, bytes.Length - filled);
                        if (read <= 0) break;
                        filled += read;
                    }
                    if (filled > MaxImageBytes || filled != bytes.Length)
                    {
                        Loader.Error($"KeyViewer: image '{path}' changed size while being read — not loaded");
                        return null;
                    }
                }
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                // The header guard applies to PNG ONLY. It parses the IHDR to reject a decompression
                // bomb before allocating — but a JPEG has no IHDR, and running that parse over one
                // reads width/height out of unrelated JPEG bytes, which comes out garbage, fails the
                // range test, and rejects the file with "is not a PNG". Every JPEG then degraded to
                // the grey placeholder at the call site, while a real oversized PNG was the only
                // thing the check was ever written for. The decoded-size check below now covers
                // every format, which is where the real allocation risk actually is.
                // 头部校验**只对 PNG 生效**。它解析 IHDR 以在分配前拒绝解压炸弹——但 JPEG 没有
                // IHDR，把这段解析跑在 JPEG 上会从未无关的 JPEG 字节里读出宽高，得到垃圾值、
                // 范围检查不通过，于是以「不是 PNG」拒掉文件。此后**每一张** JPEG 都在调用处退化成
                // 灰色占位框，而该检查当初只为「真实超大的 PNG」而写。下面按**解码后尺寸**的检查
                // 覆盖所有格式——那才是真正会分配显存的地方。
                if (IsPngSignature(bytes) && TryReadPngHeaderSize(bytes, out int pngW, out int pngH)
                    && !SizeWithinLimits(pngW, pngH))
                {
                    UnityEngine.Object.Destroy(tex);
                    Loader.Error($"KeyViewer: image '{path}' is a PNG declaring {DescribeSizeRejection(pngW, pngH)} — not loaded");
                    return null;
                }
                if (!EnsureLoadImageMethod())
                {
                    UnityEngine.Object.Destroy(tex);
                    return null;
                }
                object result = _loadImageParamCount == 2
                    ? _cachedLoadImage.Invoke(null, new object[] { tex, bytes })
                    : _cachedLoadImage.Invoke(null, new object[] { tex, bytes, false });
                if (result is bool ok && !ok)
                {
                    UnityEngine.Object.Destroy(tex);
                    Loader.Error($"KeyViewer: image data corrupt, cannot decode '{path}'");
                    return null;
                }
                // The real allocation guard, and the only one that covers every format. The PNG
                // header check above can see the dimensions of a PNG before decoding; for anything
                // else the dimensions are only knowable here, and by then Texture2D has already
                // reserved width*height*4 bytes. A hand-edited or .jkv-carried image that claims to
                // be huge must still be refused, exactly as before — just without refusing every
                // JPEG on the way.
                // 真正的分配防护，且是唯一覆盖所有格式的那一个。上面的 PNG 头部检查能在解码前看到
                // PNG 的尺寸；其它格式的尺寸只能在这里知道，而此时 Texture2D 已经预留了
                // width*height*4 字节。手改或 .jkv 携带的、声称自己极大的图片仍必须被拒绝——与
                // 之前完全一样，只是不再顺带拒绝每一张 JPEG。
                int decodedWidth = tex != null ? tex.width : 0;
                int decodedHeight = tex != null ? tex.height : 0;
                if (!SizeWithinLimits(decodedWidth, decodedHeight))
                {
                    if (tex != null) UnityEngine.Object.Destroy(tex);
                    Loader.Error($"KeyViewer: image '{path}' decoded to {DescribeSizeRejection(decodedWidth, decodedHeight)} — not loaded");
                    return null;
                }
                tex.filterMode = FilterMode.Bilinear;
                tex.wrapMode = TextureWrapMode.Clamp;
                return tex;
            }
            catch (Exception e)
            {
                if (tex != null) UnityEngine.Object.Destroy(tex);
                Loader.Error($"KeyViewer: failed to load image from '{path}': {DescribeException(e)}");
                return null;
            }
        }

        private static bool EnsureLoadImageMethod()
        {
            // A successful lookup is cached forever. A FAILED one is not: the latch is permanent,
            // so a single early failure (the module not yet resolvable during a fast OnEnable, a
            // host that loads modules later, a domain-reload ordering quirk) left _cachedLoadImage
            // null for the rest of the process and EVERY image failed with one log line. Retry
            // occasionally instead of caching the absence forever, and rate-limit the complaint so
            // a genuinely missing module does not spam the log once per image.
            // 查找成功则永久缓存。**失败**不缓存：该闩锁是永久的，故一次早期失败（快速 OnEnable
            // 时模块尚不可解析、宿主较晚加载模块、域重载顺序问题）会让 _cachedLoadImage 在整个
            // 进程内为 null，此后**每一张**图片都失败且只有一行日志。改为偶尔重试而不是把「缺席」
            // 永久缓存，并对抱怨做限流，使模块真的缺失时不会每张图刷一次日志。
            if (_cachedLoadImage != null) return true;
            float now = Time.realtimeSinceStartup;
            if (_loadImageCached && now - _loadImageRetryAfter < _loadImageRetrySeconds) return false;

            Type type = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule");
            if (type == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    type = asm.GetType("UnityEngine.ImageConversion");
                    if (type != null) break;
                }
            }
            if (type != null)
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name != "LoadImage") continue;
                    var parms = m.GetParameters();
                    // Accept ONLY the two call shapes actually used below. GetMethods has no
                    // defined order, so a future 4-argument overload sorting first would be picked
                    // and then neither invocation branch would match its parameter count — every
                    // image would fail. / 只接受下方实际调用的两种形态。GetMethods 没有定义顺序，
                    // 将来若 4 参重载排在前面就会被选中，而下方两个调用分支都不匹配其参数个数
                    // ——所有图片都会失败。
                    if (parms.Length != 2 && parms.Length != 3) continue;
                    if (parms[0].ParameterType != typeof(Texture2D) || parms[1].ParameterType != typeof(byte[])) continue;
                    _cachedLoadImage = m;
                    _loadImageParamCount = parms.Length;
                    break;
                }
            }
            _loadImageCached = true;
            if (_cachedLoadImage == null)
            {
                // The gate BELOW adds the interval (`now - stamp < interval`), so stamping
                // `now + interval` made the real wait 2× _loadImageRetrySeconds (retry allowed at
                // now ≥ T + 20 for a documented 10 s). Stamp the failure time; the gate owns the
                // interval. / 下面的闸**自己会加**间隔（`now - 戳 < 间隔`），因此存 `now + 间隔`
                // 让真实等待变成 2 倍（文档写 10s，实际 now ≥ T + 20 才重试）。只记失败时刻，
                // 间隔归闸管。
                _loadImageRetryAfter = now;
                if (now - _loadImageRetryLoggedAfter >= _loadImageRetrySeconds)
                {
                    _loadImageRetryLoggedAfter = now;
                    Loader.Error("KeyViewer: ImageConversion.LoadImage not found via reflection, images will be missing (will retry)");
                }
                return false;
            }
            return true;
        }
    }
}
