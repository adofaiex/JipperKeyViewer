// Video-backed key / decoration nodes / 视频按键 / 装饰节点
// A video node is an IMAGE node (NodeType 3) that carries a VideoPath: every image-node rule in
// the codebase — binding, counting, press text, opacity, rain, layering — applies unchanged, and
// only the texture SOURCE differs (a VideoPlayer rendering into a RenderTexture instead of a PNG
// loaded by KvImageLoader). That reuse is deliberate: duplicating the image-node branch as a
// separate NodeType would have meant auditing ~30 `NodeType == 3` sites for a fifth type and
// getting one of them wrong.
// 视频节点就是携带 VideoPath 的图片节点（NodeType 3）：代码库中所有图片节点规则——绑定、计
// 数、按压文案、不透明度、雨滴、层级——原样生效，仅贴图来源不同（VideoPlayer 渲染到
// RenderTexture，而非 KvImageLoader 加载 PNG）。这种复用是刻意的：把图片分支复制成独立
// NodeType 意味着要为第五种类型审查约 30 处 `NodeType == 3`，且难免漏改。
//
// Lifetime: entries outlive layout rebuilds. Rebuilding the overlay happens on every settings
// nudge; tearing the VideoPlayer down each time would restart decoding (a visible black flash and
// an audio/video hitch). Instead each build stamps the entries it used, and EndBuild releases only
// the ones no longer referenced — the same generation-stamp scheme CheryTools uses.
// 生命周期：条目跨布局重建存活。每次微调设置都会重建覆盖层；每次都拆掉 VideoPlayer 会让解码
// 重启（可见黑闪与音视频卡顿）。故每次构建为用到的条目打上代次标记，EndBuild 只释放不再被引用
// 的条目——与 CheryTools 同款代次标记方案。

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Video;

namespace JipperKeyViewer.KeyViewer.Rendering
{
    internal static class KvVideoTextureManager
    {
        private sealed class Entry
        {
            public GameObject GameObject;
            public VideoPlayer Player;
            public RenderTexture Texture;
            public string ResolvedPath;
            public bool Loop;
            public int Width;
            public int Height;
            public int LastGeneration;
        }

        private static readonly Dictionary<int, Entry> entries = new Dictionary<int, Entry>();
        private static readonly List<int> staleBuffer = new List<int>();
        private static GameObject root;
        private static int generation;

        // Render textures snap to a 64px grid: nudging a node's size by a few pixels in the editor
        // must not tear down and rebuild the decoder. The video is stretched onto the quad, so a
        // slightly larger texture is invisible. / 渲染纹理按 64px 对齐：编辑器中把节点尺寸挪动几个
        // 像素不应拆掉重建解码器。视频会拉伸到四边形上，纹理略大看不出来。
        private const int SizeBucket = 64;
        private const int MinDimension = 64;
        private const int MaxDimension = 2048;

        private static readonly string[] SupportedExtensions = { ".mp4", ".mov", ".webm", ".avi", ".wmv", ".m4v" };

        /// <summary>Open a build pass. Every GetOrCreate during it is stamped with the new
        /// generation. / 开始一次构建。其间每次 GetOrCreate 都打上新代次标记。</summary>
        public static void BeginBuild() => generation++;

        /// <summary>Close a build pass and destroy every entry this pass did not touch (the node
        /// was deleted, hidden, unbound, or had its path/loop/size changed). / 结束构建并销毁本次
        /// 未触及的条目（节点被删除、隐藏、解绑，或路径/循环/尺寸发生变化）。</summary>
        public static void EndBuild()
        {
            if (entries.Count == 0) return;
            staleBuffer.Clear();
            foreach (KeyValuePair<int, Entry> pair in entries)
            {
                Entry e = pair.Value;
                if (e == null || e.Player == null || e.Texture == null || e.LastGeneration != generation)
                    staleBuffer.Add(pair.Key);
            }
            for (int i = 0; i < staleBuffer.Count; i++)
            {
                if (entries.TryGetValue(staleBuffer[i], out Entry stale))
                {
                    DestroyEntry(stale);
                    entries.Remove(staleBuffer[i]);
                }
            }
            // Ensure every surviving entry is actually playing: the build pass may have just
            // created the player and called Prepare(), but Play() is deferred until the next
            // GetOrCreate. Without this, a freshly-built video node stays black until some later
            // rebuild (e.g. a drag) re-hits GetOrCreate. / 确保所有保留条目都在播放：构建过程
            // 可能刚创建播放器并调用了 Prepare()，但 Play() 被推迟到下次 GetOrCreate。
            // 没有这一步，新建的视频节点会保持黑屏直到后续重建（如拖动）再次命中 GetOrCreate。
            foreach (KeyValuePair<int, Entry> pair in entries)
            {
                if (pair.Value != null && pair.Value.Player != null)
                    EnsurePlaying(pair.Value);
            }
        }

        /// <summary>Destroy every player and render texture. Called from full teardown, never from
        /// a layout rebuild. / 销毁全部播放器与渲染纹理。由完整拆解调用，绝不在布局重建时调用。</summary>
        public static void ReleaseAll()
        {
            foreach (KeyValuePair<int, Entry> pair in entries) DestroyEntry(pair.Value);
            entries.Clear();
            staleBuffer.Clear();
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
            }
        }

        /// <summary>Release one node's video entry immediately. Used when the editor changes a
        /// video node's path/size so the next rebuild gets a fresh player instead of reusing a
        /// stale one that may not have prepared correctly. / 立即释放单个节点的视频条目。
        /// 编辑器修改视频节点的路径/尺寸时使用，使下次重建获得全新播放器，而非复用一个
        /// 可能未正确 prepared 的旧播放器。</summary>
        public static void Release(int nodeId)
        {
            if (entries.TryGetValue(nodeId, out Entry entry))
            {
                DestroyEntry(entry);
                entries.Remove(nodeId);
            }
        }

        /// <summary>True when the path names a file this platform's VideoPlayer can open. Checks
        /// existence too, so callers can use it as the "is this node a video node" test. /
        /// 路径是否指向本平台 VideoPlayer 可打开的文件。同时检查存在性，故调用方可直接用它判断
        ///「本节点是否为视频节点」。</summary>
        public static bool IsPlayableVideo(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) return false;
            bool supported = false;
            for (int i = 0; i < SupportedExtensions.Length; i++)
            {
                if (string.Equals(ext, SupportedExtensions[i], StringComparison.OrdinalIgnoreCase))
                {
                    supported = true;
                    break;
                }
            }
            return supported && File.Exists(path);
        }

        /// <summary>Get (or create) the render texture for one node. Returns null when the file is
        /// missing or unsupported — the caller then falls back to the static-image path, so a typo
        /// in the path degrades to a placeholder instead of an invisible node. / 取得（或创建）
        /// 某节点的渲染纹理。文件缺失或格式不支持时返回 null——调用方随后回落到静态图片路径，
        /// 故路径写错只会退化为占位图，而不是变成看不见的节点。</summary>
        public static RenderTexture GetOrCreate(int nodeId, string path, bool loop, float width, float height)
        {
            string resolved = ResolveVideoPath(path);
            if (!IsPlayableVideo(resolved)) return null;

            int w = BucketSize(width);
            int h = BucketSize(height);

            if (entries.TryGetValue(nodeId, out Entry existing) && existing != null
                && existing.Player != null && existing.Texture != null
                && string.Equals(existing.ResolvedPath, resolved, StringComparison.OrdinalIgnoreCase)
                && existing.Loop == loop && existing.Width == w && existing.Height == h)
            {
                existing.LastGeneration = generation;
                if (existing.Player.isLooping != loop) existing.Player.isLooping = loop;
                EnsurePlaying(existing);
                return existing.Texture;
            }

            if (existing != null)
            {
                DestroyEntry(existing);
                entries.Remove(nodeId);
            }

            Entry created = CreateEntry(nodeId, resolved, loop, w, h);
            if (created == null) return null;
            created.LastGeneration = generation;
            entries[nodeId] = created;
            return created.Texture;
        }

        private static Entry CreateEntry(int nodeId, string resolvedPath, bool loop, int width, int height)
        {
            try
            {
                EnsureRoot();
                if (root == null) return null;

                GameObject go = new GameObject("JipperKV_Video_" + nodeId);
                go.transform.SetParent(root.transform, false);

                RenderTexture texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "JipperKVVideo_" + nodeId,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                texture.Create();

                VideoPlayer player = go.AddComponent<VideoPlayer>();
                player.playOnAwake = false;
                // waitForFirstFrame keeps the RT from showing a stale/black frame on the very first
                // frame; skipOnDrop lets a slow disk drop frames instead of desyncing. /
                // waitForFirstFrame 避免第一帧显示陈旧/黑帧；skipOnDrop 让慢磁盘丢帧而不是失步。
                player.waitForFirstFrame = true;
                player.skipOnDrop = true;
                player.isLooping = loop;
                player.renderMode = VideoRenderMode.RenderTexture;
                // Stretch, not Fit: the node rect IS the sizing control, and letterboxing inside a
                // small key box wastes most of it. / 用 Stretch 而非 Fit：节点矩形本身就是尺寸
                // 控制，小按键框内再加黑边会浪费大部分面积。
                player.aspectRatio = VideoAspectRatio.Stretch;
                player.audioOutputMode = VideoAudioOutputMode.None;
                player.targetTexture = texture;
                player.url = resolvedPath;
                player.Prepare();

                return new Entry
                {
                    GameObject = go,
                    Player = player,
                    Texture = texture,
                    ResolvedPath = resolvedPath,
                    Loop = loop,
                    Width = width,
                    Height = height,
                };
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: video node {nodeId} failed to start: {e.Message}");
                return null;
            }
        }

        /// <summary>Play once prepared. Prepare is asynchronous, so an immediate Play() throws;
        /// this is retried on every rebuild and is a no-op once playing. / 就绪后播放。Prepare 是
        /// 异步的，立刻 Play() 会抛异常；本方法在每次重建时重试，一旦在播就是空操作。</summary>
        private static void EnsurePlaying(Entry e)
        {
            if (e == null || e.Player == null || e.Player.isPlaying) return;
            try
            {
                // Play() is safe to call before Prepare() completes: Unity will auto-prepare
                // and start playback once ready. Without this, a freshly-built entry that
                // hasn't finished Prepare() yet stays silent until the next rebuild. /
                // Play() 在 Prepare() 完成前调用也是安全的：Unity 会自动准备并在就绪后开始
                // 播放。没有这一步，刚构建完、Prepare() 还没完成的条目会一直静默，直到下次重建。
                e.Player.Play();
            }
            catch (Exception ex)
            {
                Loader.Error($"KeyViewer: video node playback failed: {ex.Message}");
            }
        }

        private static void DestroyEntry(Entry e)
        {
            if (e == null) return;
            try
            {
                if (e.Player != null)
                {
                    e.Player.Stop();
                    e.Player.targetTexture = null;
                }
            }
            catch (Exception) { /* stopping an already-dead player is expected / 停止已死的播放器属预期 */ }

            // Release() before Destroy: an un-released RenderTexture keeps its GPU memory until the
            // next GC, and video-sized RTs exhaust VRAM fast. / 先 Release 再 Destroy：未释放的
            // RenderTexture 会把显存留到下次 GC，而视频尺寸的 RT 很快耗尽显存。
            if (e.Texture != null)
            {
                e.Texture.Release();
                UnityEngine.Object.Destroy(e.Texture);
            }
            if (e.GameObject != null) UnityEngine.Object.Destroy(e.GameObject);
        }

        private static void EnsureRoot()
        {
            if (root != null) return;
            root = new GameObject("JipperKV_Video_Root");
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.hideFlags = HideFlags.HideAndDontSave;
        }

        private static int BucketSize(float size)
        {
            int s = Mathf.CeilToInt(Mathf.Max(1f, size) / SizeBucket) * SizeBucket;
            return Mathf.Clamp(s, MinDimension, MaxDimension);
        }

        /// <summary>Resolve a video reference the same way images resolve: absolute as-is,
        /// otherwise relative to CustomImages\ (users drop .mp4 files next to their .png files). /
        /// 视频引用与图片同样解析：绝对路径原样，否则相对 CustomImages\（用户把 .mp4 和 .png 放在
        /// 一起）。</summary>
        internal static string ResolveVideoPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try
            {
                if (Path.IsPathRooted(path)) return path;
                string rel = Path.Combine(Loader.ModPath, "CustomImages", path);
                if (File.Exists(rel)) return rel;
                return path;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}