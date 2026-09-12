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
            if (border.HasValue && (tex.width < 22 || tex.height < 22))
            {
                UnityEngine.Object.Destroy(tex);
                Loader.Error($"KeyViewer: sprite '{path}' too small ({tex.width}x{tex.height}) for the 11px 9-slice border");
                return null;
            }
            try
            {
                return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height),
                    new Vector2(0.5f, 0.5f), 100f, 0,
                    border.HasValue ? SpriteMeshType.Tight : SpriteMeshType.FullRect, b);
            }
            catch (Exception e)
            {
                UnityEngine.Object.Destroy(tex);
                Loader.Error($"KeyViewer: failed to create sprite from '{path}': {e.Message}");
                return null;
            }
        }

        /// <summary>Load a PNG as a Texture2D (FreeMake image nodes draw the texture directly). /
        /// 加载 PNG 为 Texture2D（FreeMake 图片节点直接绘制纹理）。</summary>
        internal static Texture2D LoadTexture(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            Texture2D tex = null;
            try
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                byte[] bytes = File.ReadAllBytes(path);
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
                Loader.Error($"KeyViewer: failed to load image from '{path}': {e.Message}");
                return null;
            }
        }

        private static bool EnsureLoadImageMethod()
        {
            if (_loadImageCached) return _cachedLoadImage != null;
            _loadImageCached = true;
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
                    if (parms.Length >= 2 && parms[0].ParameterType == typeof(Texture2D) && parms[1].ParameterType == typeof(byte[]))
                    {
                        _cachedLoadImage = m;
                        _loadImageParamCount = parms.Length;
                        break;
                    }
                }
            }
            if (_cachedLoadImage == null)
                Loader.Error("KeyViewer: ImageConversion.LoadImage not found via reflection, images will be missing");
            return _cachedLoadImage != null;
        }
    }
}
