// Shared runtime PNG loader (reflection on ImageConversion.LoadImage): key sprites with a
// fixed 9-slice border, and FreeMake image nodes without one. Allocation-free failure
// paths — every exit either returns a valid sprite/texture or destroys the scratch
// texture before returning null.
// 共享运行时 PNG 加载器（反射调用 ImageConversion.LoadImage）：带固定九宫格边框的按键贴图，
// 以及不带边框的 FreeMake 图片节点。所有失败路径都会先释放占位纹理再返回 null，绝不泄漏。

using System;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace JipperKeyViewer.KeyViewer.Util
{
    internal static class KvImageLoader
    {
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
