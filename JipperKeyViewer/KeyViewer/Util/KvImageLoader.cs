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
                var info = new FileInfo(path);
                if (info.Length > MaxImageBytes)
                {
                    Loader.Error($"KeyViewer: image '{path}' is {info.Length / (1024 * 1024)} MB, over the {MaxImageBytes / (1024 * 1024)} MB limit — not loaded");
                    return null;
                }
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                byte[] bytes = File.ReadAllBytes(path);
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
            if (_loadImageCached) return _cachedLoadImage != null;
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
            // Only latch the negative result once the search actually ran. Setting the flag BEFORE
            // the lookup meant a failed search was cached forever, so images silently disappeared
            // for the rest of the session with no way to recover.
            // 仅在查找真正执行后才锁定结果。此前在查找前置位，查找失败会被永久缓存，此后整个
            // 会话图片静默消失且无法恢复。
            _loadImageCached = true;
            if (_cachedLoadImage == null)
                Loader.Error("KeyViewer: ImageConversion.LoadImage not found via reflection, images will be missing");
            return _cachedLoadImage != null;
        }
    }
}
