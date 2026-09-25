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
            // Same reasoning as the video ledger's ReleaseAll: reset outright rather than trust the
            // per-entry subtraction, so a domain reload that dropped some entries cannot leave the
            // budget permanently narrowed and silently disable images for the rest of the session.
            // 与视频账本的 ReleaseAll 同理：直接归零而非依赖逐条减计数，使域重载丢掉的条目不会让
            // 预算永久变窄、从而在本次会话余下时间里悄悄禁用图片。
            liveCachedImageBytes = 0;
        }

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
            }
            Texture2D loaded = LoadTexture(path);
            if (loaded == null) return null;
            // Read the real decoded dimensions, not the PNG header: the header is what the per-file
            // cap validates, and only the decoded texture's size is what the GPU actually reserves.
            // 读**解码后**的真实尺寸而非 PNG 头部：头部只是单文件上限校验的对象，真正由 GPU 预留的
            // 是解码后贴图的尺寸。
            int texWidth = loaded.width, texHeight = loaded.height;
            long wanted = (long)texWidth * texHeight * 4L;
            if (liveCachedImageBytes + wanted > MaxCachedImageBytes)
            {
                try { UnityEngine.Object.Destroy(loaded); }
                catch (Exception) { /* nothing to do */ }
                Loader.Warning($"KeyViewer: image '{path}' skipped — the custom-image texture budget "
                    + $"({MaxCachedImageBytes / (1024 * 1024)} MB) is already committed");
                return null;
            }
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
        private const int MaxImageDimension = 4096;

        private static bool IsPngHeaderReasonable(byte[] bytes)
        {
            // PNG signature (8) + IHDR chunk (4 len + 4 type + 13 data) / PNG 签名(8) + IHDR 块
            if (bytes.Length < 33) return bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50;
            int width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
            int height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
            return width > 0 && height > 0 && width <= MaxImageDimension && height <= MaxImageDimension;
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
                if (!IsPngHeaderReasonable(bytes))
                {
                    UnityEngine.Object.Destroy(tex);
                    Loader.Error($"KeyViewer: image '{path}' is not a PNG within {MaxImageDimension}x{MaxImageDimension} — not loaded");
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
                    Loader.Error($"KeyViewer: PNG data corrupt, cannot decode '{path}'");
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
                _loadImageRetryAfter = now + _loadImageRetrySeconds;
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
