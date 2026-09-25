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
            public bool Failed;
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
                if (pair.Value != null && pair.Value.Player != null && !pair.Value.Failed)
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

        public static bool HasFailed(int nodeId)
            => entries.TryGetValue(nodeId, out Entry entry) && entry != null && entry.Failed;

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
                && existing.Player != null && existing.Texture != null && !existing.Failed
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
            GameObject go = null;
            RenderTexture texture = null;
            VideoPlayer player = null;
            try
            {
                EnsureRoot();
                if (root == null) return null;

                go = new GameObject("JipperKV_Video_" + nodeId);
                go.transform.SetParent(root.transform, false);

                texture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    name = "JipperKVVideo_" + nodeId,
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                texture.Create();

                player = go.AddComponent<VideoPlayer>();
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
                // Auto-start via the prepareCompleted callback: Prepare() is async, and calling
                // Play() before it finishes WEDGES the decoder thread — Stop() during teardown
                // then blocks forever and freezes the game on exit. The handler fires on the
                // main thread once the decoder is ready, where Play() is always safe. This also
                // fixes "video stays black until the next rebuild drags it through GetOrCreate".
                // / 通过 prepareCompleted 回调自动起播：Prepare() 是异步的，未完成就调 Play()
                // 会楔死解码线程——拆解时 Stop() 永久阻塞，游戏退出直接卡死。回调在解码器就绪
                // 后于主线程触发，此时 Play() 恒安全。同时也修掉「视频黑屏直到下次重建」。
                player.prepareCompleted += OnVideoPrepared;
                player.errorReceived += OnVideoError;

                Entry created = new Entry
                {
                    GameObject = go,
                    Player = player,
                    Texture = texture,
                    ResolvedPath = resolvedPath,
                    Loop = loop,
                    Width = width,
                    Height = height,
                };
                // Register before Prepare so an immediate decoder error can be associated with
                // this entry; the error callback marks it failed and the runtime swaps to the
                // static image on the next frame. / Prepare 前登记，错误回调即可标记条目失败，
                // 运行时下一帧切换静态图片。
                entries[nodeId] = created;
                player.Prepare();
                return created;
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: video node {nodeId} failed to start: {e.Message}");
                if (player != null)
                {
                    try { player.prepareCompleted -= OnVideoPrepared; player.errorReceived -= OnVideoError; player.targetTexture = null; } catch { }
                }
                if (texture != null)
                {
                    try { texture.Release(); UnityEngine.Object.Destroy(texture); } catch { }
                }
                if (go != null)
                {
                    try { UnityEngine.Object.Destroy(go); } catch { }
                }
                if (entries.TryGetValue(nodeId, out Entry failed) && failed != null && failed.GameObject == go)
                    entries.Remove(nodeId);
                return null;
            }
        }

        /// <summary>Fires on the main thread when a player finishes preparing; starts playback
        /// only then, never on an unprepared player. / 播放器完成 Prepare 后于主线程触发；
        /// 只在就绪后才起播，绝不在未就绪的播放器上调 Play()。</summary>
        private static void OnVideoPrepared(VideoPlayer source)
        {
            try
            {
                if (source == null) return;
                bool registered = false;
                foreach (KeyValuePair<int, Entry> pair in entries)
                {
                    if (pair.Value == null || pair.Value.Player != source) continue;
                    registered = true;
                    if (pair.Value.Failed) return;
                    break;
                }
                if (!registered) return;
                if (!source.isPlaying) source.Play();
            }
            catch (Exception) { /* a player that died mid-prepare is cleaned up by EndBuild / 准备途中死掉的播放器由 EndBuild 清理 */ }
        }

        private static void OnVideoError(VideoPlayer source, string message)
        {
            foreach (KeyValuePair<int, Entry> pair in entries)
            {
                if (pair.Value == null || pair.Value.Player != source) continue;
                pair.Value.Failed = true;
                Loader.Warning($"KeyViewer: video decode failed for node {pair.Key}: {message}");
                break;
            }
        }

        /// <summary>Resume a prepared-but-paused player (reused entries on rebuild). Never calls
        /// Play() while Prepare() is still running — that wedges the decoder thread and later
        /// freezes game exit inside Stop(). Unprepared entries auto-play via OnVideoPrepared
        /// instead. / 恢复已就绪但暂停的播放器（重建时复用的条目）。绝不在 Prepare() 进行中调
        /// Play()——那会楔死解码线程，之后游戏退出时 Stop() 卡死。未就绪的条目改由
        /// OnVideoPrepared 自动起播。</summary>
        private static void EnsurePlaying(Entry e)
        {
            if (e == null || e.Failed || e.Player == null || e.Player.isPlaying) return;
            try
            {
                if (e.Player.isPrepared) e.Player.Play();
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
                    // Detach the auto-play handler BEFORE stopping: a late prepareCompleted on a
                    // half-torn-down player would call Play() on it and wedge the decode thread.
                    // / 先摘掉自动起播回调再停播：半拆解的播放器若迟到触发 prepareCompleted
                    // 会对它调 Play()，把解码线程楔死。
                    e.Player.prepareCompleted -= OnVideoPrepared;
                    e.Player.errorReceived -= OnVideoError;
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