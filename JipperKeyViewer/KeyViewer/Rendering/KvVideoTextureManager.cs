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
            /// <summary>Has this entry's budget slice already been handed back by OnVideoError?
            /// Without it, destroying the tombstone later (a path change, node deletion, teardown)
            /// would decrement `liveTextureBytes` a second time, and the floor-at-0 clamp would then
            /// silently widen the budget guard. / 该条目的预算份额是否已由 OnVideoError 归还？
            /// 没有它，之后销毁这枚墓碑时（路径改变、节点删除、拆解）会**第二次**减计数，而
            /// 下限 0 的钳制会随即悄悄放宽预算守卫。
            /// </summary>
            public bool BudgetReturned;
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

        /// <summary>Total render-texture budget across all live video nodes. Generous for any real
        /// layout (a handful of 1080p clips) and small enough that a pathological document cannot
        /// ask the GPU for tens of gigabytes. / 所有存活视频节点渲染纹理的总预算。对任何真实布局
        /// 都够用（几个 1080p 片段），又足够小，使病态文档无法向显卡索要数十 GB。</summary>
        private const long MaxTotalTextureBytes = 256L * 1024 * 1024;
        private static long liveTextureBytes;

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
                // A null entry is dead weight with nothing to release — reclaim it, as before.
                if (e == null) { staleBuffer.Add(pair.Key); continue; }
                // A Failed entry is a tombstone, not a live node: OnVideoError already released its
                // RenderTexture and returned its budget share, and GetOrCreate's never-retry
                // short-circuit re-stamps LastGeneration so it is never retried. Reclaiming one
                // that is still wanted would undo both things the tombstone is there to prevent —
                // the next build would go straight back into the allocate -> fail -> release ->
                // reallocate loop.
                //
                // "Still wanted" is decided by LastGeneration, which is already tracked and is
                // re-stamped on BOTH GetOrCreate paths (reuse and the never-retry short-circuit).
                // So a tombstone that went untouched through this build pass is one whose node no
                // longer exists. This used to be a bare `continue`, on the stated grounds that
                // "node deleted" is one of the release paths — it is not, and never was: a deleted
                // node is never passed to GetOrCreate, so the path-change branch cannot fire
                // either, and this line ran BEFORE the staleness test below, so the tombstone was
                // never reclaimed. Deleting a video node after it had failed therefore stranded a
                // GameObject, a VideoPlayer and a dictionary entry for the rest of the session. It
                // was worse than a leak, because NextId restarts per profile and a profile switch
                // does not call ReleaseAll: a later profile's node with the same id inherits the
                // tombstone, and the never-retry short-circuit then decides its fate by comparing
                // path strings.
                // Failed 条目是墓碑而非活节点：OnVideoError 已释放其 RenderTexture 并归还预算份额，
                // 而 GetOrCreate 的「不再重试」短路会重盖 LastGeneration，故永不重试。回收一个
                // **仍被需要**的墓碑会抵消墓碑存在的两个目的——下次构建会直接回到
                // 分配→失败→释放→再分配的循环。
                //
                // 「是否仍被需要」由 LastGeneration 决定——该字段本就在跟踪，且在 GetOrCreate 的
                // **两条**路径（复用与不再重试短路）上都会重盖。故本轮构建中完全没被碰过的墓碑，
                // 其节点已不存在。此处此前是一句裸 `continue`，理由写的是「节点删除也是释放路径之一」
                // ——它**不是**，从来不是：被删除的节点不会传给 GetOrCreate，故路径变更分支同样不会
                // 触发；而这一行又跑在下面的陈旧判定**之前**，故墓碑永不被回收。于是「一个视频节点先
                // 失败、再被删除」会让 GameObject、VideoPlayer 与字典条目在本次会话余下时间里滞留。
                // 这比单纯泄漏更糟：NextId 每配置从头计，而切换配置并不调 ReleaseAll——**后续配置**里
                // 同 id 的节点会继承这个墓碑，「不再重试」短路随后按路径字符串决定它的命运。
                if (e.Failed && e.LastGeneration == generation) continue;
                if (e.Player == null || e.Texture == null || e.LastGeneration != generation)
                    staleBuffer.Add(pair.Key);
            }
            for (int i = 0; i < staleBuffer.Count; i++)
            {
                if (entries.TryGetValue(staleBuffer[i], out Entry stale))
                {
                    // Remove in a finally, not after the call. DestroyEntry is defensive internally
                    // now, but "the callee is careful" is not a property this loop should depend on:
                    // if it ever throws, an entry left in `entries` is re-examined on every later
                    // build, keeps re-stamping itself as current-or-stale, and the remaining
                    // entries in staleBuffer never get swept at all. The removal is the one step
                    // that must survive the callee, so it owns the finally.
                    // 移除放在 finally 里，而不是调用之后。DestroyEntry 现在内部已有防护，但
                    // 「被调方足够小心」不该是本循环依赖的性质：它一旦抛出，留在 `entries` 里的
                    // 条目会在此后每次构建被重新检查、反复把自己盖成「当前/陈旧」，而 staleBuffer
                    // 里其余条目**完全得不到清扫**。移除是必须活过被调方的那一步，故由它负责 finally。
                    try { DestroyEntry(stale); }
                    catch (Exception) { /* teardown is best-effort; the removal below is not / 拆解尽力而为，但下面的移除不是 */ }
                    finally { entries.Remove(staleBuffer[i]); }
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
            // DestroyEntry already returns each entry's budget, but reset outright: entries that
            // were destroyed by something other than this manager (a domain reload leaving a stale
            // counter) must not permanently shrink the budget.
            // DestroyEntry 已归还每个条目的预算，但仍直接归零：被本管理器之外的东西销毁的条目
            // （域重载留下陈旧计数）不得永久缩小预算。
            liveTextureBytes = 0;
            if (root != null)
            {
                UnityEngine.Object.Destroy(root);
                root = null;
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

            // A file that already failed to decode is not retried. The reuse test above excludes
            // FAILED entries, so before this fix EVERY rebuild (dragging a node, nudging a colour
            // slider) destroyed the dead player + RT, re-created them, re-ran the decoder, failed
            // again and logged again — a permanent allocate→fail→free→allocate cycle plus a
            // repeating warning. Return null so the caller immediately takes the static-image path,
            // and keep the dead entry until EndBuild retires it.
            // 已确认解码失败的文件不再重试。复用判定排除 FAILED 条目，此前每次重建（拖节点、调色
            // 滑杆）都会销毁死的播放器与 RT、重新创建、重新解码、再次失败、再次刷日志——形成
            // 永久的"分配→失败→释放→再分配"循环与重复告警。返回 null 让调用方立即走静态图
            // 路径，死条目留到 EndBuild 统一回收。
            if (existing != null && existing.Failed
                && string.Equals(existing.ResolvedPath, resolved, StringComparison.OrdinalIgnoreCase))
            {
                existing.LastGeneration = generation;
                return null;
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
            // Did this attempt take a slice of the global render-texture budget? The budget is
            // committed at line ~295 and only returned by DestroyEntry — but the catch below
            // removes the entry from `entries` itself, so DestroyEntry can never run for it.
            // 这次尝试是否占用了全局渲染纹理预算？预算在 ~295 行提交、只由 DestroyEntry 归还——
            // 而下面的 catch 自己把条目从 `entries` 里移除，故 DestroyEntry 永远等不到它。
            bool budgetCommitted = false;
            try
            {
                EnsureRoot();
                if (root == null) return null;

                // Budget check FIRST, before the GameObject, the RenderTexture and its Create()
                // exist at all. This used to sit ~38 lines further down, past the allocation it
                // claims to prevent, so an over-budget node paid the full cost anyway: a
                // GameObject, a VideoPlayer, and a real 2048x2048 ARGB32 GPU allocation (16 MB)
                // created and then thrown away. Object.Destroy is deferred to the end of the
                // frame, and a dragged settings slider drives on the order of 100 rebuilds per
                // second, so several such RTs can be live at once — the exact "exhausts VRAM fast"
                // case the 256 MB budget exists to prevent, reached by the refusal path itself.
                // 预算检查放在**最前面**，在 GameObject、RenderTexture 及其 Create() 存在之前。
                // 此前它在约 38 行之后、也就是它声称要阻止的那次分配**之后**，故超预算的节点照样付了
                // 全额代价：建好 GameObject、VideoPlayer 与一张真实的 2048x2048 ARGB32 显存分配
                // （16 MB）再扔掉。而 `Object.Destroy` 延迟到帧末，拖动设置滑杆每秒可触发约 100 次
                // 重建，于是同时会有好几张这样的 RT 存活——正是 256 MB 预算要防的「很快耗尽显存」，
                // 且是被**拒绝路径自己**走到的。
                long wanted = (long)width * height * 4L;
                if (liveTextureBytes + wanted > MaxTotalTextureBytes)
                {
                    Loader.Warning($"KeyViewer: video node {nodeId} skipped — the video render-texture budget ({MaxTotalTextureBytes / (1024 * 1024)} MB) is already committed");
                    return null;
                }

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
                // The budget check now runs at the very top of this method, before the GameObject
                // and the RenderTexture exist — see the note there. Committing the slice here is
                // the only place it is taken. MaxDimension is 2048 and the format is ARGB32, so ONE
                // node at full size is already 16 MB; a FreeMake document can hold 2048 video
                // nodes, which would be 32 GB of VRAM, so the guard refuses the extra ones and
                // the caller falls them back to the static image (it already handles a null
                // texture).
                // 预算检查现已移到本方法最前面、在 GameObject 与 RenderTexture 存在之前——见那里的
                // 说明。这里是**唯一**取走预算份额的地方。MaxDimension 为 2048 且格式为 ARGB32，
                // 单个满尺寸节点已是 16 MB；一份 FreeMake 文档最多可含 2048 个视频节点，即 32 GB
                // 显存，故闸门拒绝多余的那些，由调用方回退到静态图片（它已处理 null 纹理）。
                liveTextureBytes += wanted;
                budgetCommitted = true;
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
                // Hand the budget slice back. This is the only place that can: the entry is gone
                // from `entries`, so DestroyEntry — the sole other path that decrements — will
                // never see it. Leaking here is not a small drift: `wanted` is up to 16 MB for one
                // 2048×2048 node against a 256 MB GLOBAL budget, and a failed start is
                // re-attempted on every node edit, profile switch and overlay rebuild. A user
                // pointing a node at a broken video file would burn 16 MB per attempt and, after
                // 16 of them, every video node in every profile would silently fall back to its
                // static image — with nothing on screen to explain it, only the one log line
                // emitted the first time.
                // 归还这一份预算。这是**唯一**能归还的地方：条目已从 `entries` 移除，故唯一另一条
                // 减计数路径 DestroyEntry 永远看不到它。这里的泄漏不是小偏移：`wanted` 对一个
                // 2048×2048 节点最大 16 MB，而预算是 **256 MB 全局**的；且失败的启动会在每次节点
                // 编辑、配置切换与覆盖层重建时**重试**。把节点指向损坏视频文件的用户，每次尝试
                // 烧掉 16 MB——16 次之后，**所有配置里**的视频节点都会静默回退到静态图，屏幕上
                // 没有任何提示，只有第一次那条日志。
                if (budgetCommitted)
                {
                    liveTextureBytes -= (long)width * height * 4L;
                    if (liveTextureBytes < 0) liveTextureBytes = 0;
                }
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
                // Idempotence guard. A VideoPlayer may raise errorReceived more than once for one
                // source, and a second pass used to re-run this whole handler: the budget return was
                // correctly skipped (OnVideoError nulls the Texture, and that null is the guard), but
                // it logged a duplicate warning AND re-added the id to the layout's pending-fallback
                // set. The scan skips an id whose fallback is already applied, so that re-added id
                // could never be consumed, and the set's prune deliberately KEEPS ids that are
                // already applied — the one state in which they are permanently inert. The set
                // therefore never emptied, its `Count == 0` fast path never fired again, and
                // UpdateCustomVideoFallbacks went back to walking every node every frame — the exact
                // per-frame scan the pending set was introduced to remove.
                // 幂等保护。VideoPlayer 对同一个源可能多次触发 errorReceived；第二次会重跑整个处理：
                // 预算归还被正确跳过（OnVideoError 会把 Texture 置空，而那个 null 就是判据），但它会
                // 多打一条重复警告，**并且**把 id 重新加进布局的待回退集合。扫描会跳过「回退已施加」的
                // id，故被重新加回的 id 永远无法被消费；而剪枝偏偏**保留**「已施加」的 id——那正是它们
                // 永久失效的状态。于是集合永不为空，其 `Count == 0` 快路径再也不触发，
                // UpdateCustomVideoFallbacks 退回逐帧遍历每个节点——正是引入待回退集合要消除的那种扫描。
                if (pair.Value.Failed) return;
                pair.Value.Failed = true;
                // Stop the decoder and drop the render texture immediately instead of leaving a
                // dead player decoding into a RT nobody will ever draw. The objects themselves are
                // still destroyed by the manager (main thread) — only the work stops now.
                // 立刻停掉解码器并断开渲染贴图，而不是让一个死播放器继续往无人绘制的 RT 里解码。
                // 对象本身仍由管理器（主线程）销毁——这里只是立刻停止工作。
                try
                {
                    source.Pause();
                    source.targetTexture = null;
                }
                catch (Exception) { /* a player destroyed mid-callback needs no cleanup */ }
                // ...and give the GPU memory and its budget share back NOW. A failed entry stays in
                // `entries` as a never-retry tombstone (see the reuse short-circuit in GetOrCreate,
                // which re-stamps LastGeneration and therefore makes EndBuild's staleness test
                // false for it) — so DestroyEntry, the only other place that returns the budget,
                // will never run for it. Without this the entry pinned a 2048x2048 ARGB32 RT
                // (16 MB of VRAM, rendering nothing) AND its share of the 256 MB global budget for
                // the rest of the session. Sixteen unplayable files — an .avi or .wmv the platform
                // decoder cannot open, a truncated download, a shared .jkv pointing at paths that
                // exist but do not decode — and then every subsequent video node in every profile
                // is refused at the budget check with a bare Loader.Warning. The entry must stay
                // (that is what stops the retry loop) but it must not COST anything.
                // 立刻把显存与预算份额还回去。失败条目会以「不再重试的墓碑」形式留在 `entries` 里
                // （见 GetOrCreate 的复用短路，它会重新盖上 LastGeneration，于是对 EndBuild 的
                // 陈旧判定而言该条目**永远不算陈旧**）——故唯一另一条归还预算的路径 DestroyEntry
                // 永远等不到它。不归还的话，该条目会在本次会话余下时间里**同时**钉住一块
                // 2048x2048 ARGB32 RT（16 MB 显存，且什么都不渲染）与 256 MB 全局预算中的一份。
                // 十六个放不了的文件——平台解码器打不开的 .avi/.wmv、下载截断的文件、指向「存在
                // 但解不出」的路径的分享 .jkv——之后**所有配置**里的每个新视频节点都会在预算检查
                // 处被拒绝，只留一行 Loader.Warning。条目必须留下（那正是阻止重试循环的东西），
                // 但它必须**不花任何代价**。
                if (pair.Value.Texture != null)
                {
                    RenderTexture deadTexture = pair.Value.Texture;
                    pair.Value.Texture = null;
                    try { deadTexture.Release(); UnityEngine.Object.Destroy(deadTexture); }
                    catch (Exception) { /* already released by a concurrent teardown */ }
                    liveTextureBytes -= (long)pair.Value.Width * pair.Value.Height * 4L;
                    if (liveTextureBytes < 0) liveTextureBytes = 0;
                    pair.Value.BudgetReturned = true;
                }
                Loader.Warning($"KeyViewer: video decode failed for node {pair.Key}: {message}");
                // Tell the custom layout that THIS node still owes a static fallback. The scan
                // that applies it is per-frame, so without this signal it would have to walk every
                // node every frame just to discover the failure again.
                // 通知自定义布局：该节点仍欠一次静态回退。施加回退的扫描是逐帧的，没有这个信号
                // 就得每帧遍历每个节点，只为重新发现这次失败。
                try { global::JipperKeyViewer.KeyViewer.KeyViewer.NoteVideoDecodeFailure(pair.Key); }
                catch (Exception) { /* the layout may be torn down; the placeholder is cosmetic */ }
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
            //
            // GUARDED, like the two other copies of this block (the catch in GetOrCreate and
            // OnVideoError). This copy had lost its guard, and because it sits *before* the
            // accounting, a throw here did not merely skip a cleanup call: it skipped the budget
            // return AND the GameObject destroy AND — at the EndBuild call site — the
            // `entries.Remove`, because that removal is the statement right after this call and
            // an exception unwinds past it. One throw therefore leaked the GameObject, left the
            // entry in the dictionary pointing at a dead texture with BudgetReturned still false,
            // and aborted the whole stale sweep, so every remaining stale entry kept its budget
            // share too. `liveTextureBytes` then over-counts for the rest of the session, the
            // 256 MB guard refuses new video nodes one by one, and the only symptom is a bare
            // Loader.Warning per node — no way for the user to tell it apart from a decode failure.
            // 加上保护，与另两处同一段的副本一致（GetOrCreate 的 catch 与 OnVideoError）。
            // 这一份此前**丢掉了**保护，而它恰好位于记账**之前**：此处一抛，不只是少一次清理调用——
            // 预算归还、GameObject 销毁、以及调用点处紧跟其后的 `entries.Remove` 全部被跳过
            // （异常直接展开过去）。故一次抛出即泄漏 GameObject、让条目带着死贴图与
            // BudgetReturned=false 留在字典里，并中断整轮陈旧清扫，其余每个陈旧条目也一起保住预算。
            // `liveTextureBytes` 随后在本次会话余下时间里**多计**，256 MB 闸门逐个拒绝新视频节点，
            // 而唯一的症状是每个节点一行 Loader.Warning——用户无法把它与解码失败区分开。
            try
            {
                if (e.Texture != null)
                {
                    RenderTexture rt = e.Texture;
                    e.Texture = null;
                    rt.Release();
                    UnityEngine.Object.Destroy(rt);
                }
            }
            catch (Exception) { /* already released by a concurrent teardown / 已被并发拆解释放 */ }

            // Give the budget back, or a document that once had many video nodes could never
            // create another one for the rest of the session. Floor at zero so a double-destroy
            // (destroyed-object null checks can race a deferred Destroy) cannot make it negative.
            // A Failed tombstone already returned its share when OnVideoError fired; decrementing
            // again here would be a double-release that the floor would convert into a silently
            // over-wide budget.
            // 把预算还回去，否则曾经有很多视频节点的文档此后永远创建不出新的。把下限设为 0，
            // 避免（延迟 Destroy 与已销毁判定竞态导致的）二次销毁把它变成负数。
            // Failed 墓碑的份额在 OnVideoError 触发时**已经**归还；此处再减一次即是重复归还，
            // 会被下限转成悄悄放宽的预算。
            if (!e.BudgetReturned)
            {
                liveTextureBytes -= (long)e.Width * e.Height * 4L;
                if (liveTextureBytes < 0) liveTextureBytes = 0;
                e.BudgetReturned = true;
            }
            // The GameObject is last, and it is the one cleanup that is purely a Unity call on
            // this entry's own holder — so it gets its own guard rather than relying on the one
            // above, which is scoped to the texture.
            // GameObject 放最后，且它是纯粹针对本条目持有者的 Unity 调用，故自带保护，
            // 而不依赖上面那个仅限贴图的作用域。
            try
            {
                if (e.GameObject != null) UnityEngine.Object.Destroy(e.GameObject);
            }
            catch (Exception) { /* a holder destroyed by a concurrent teardown / 持有者已被并发拆解销毁 */ }
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
            // NaN/Infinity survive every comparison, and CeilToInt(NaN) is 0 while
            // CeilToInt(Infinity) is undefined — the rest of this codebase sanitizes explicitly
            // (EnsureCustomNodes, the rain start-Y), so do the same here rather than letting a
            // hand-edited profile pick the render-texture size.
            // NaN/Inf 能通过所有比较：CeilToInt(NaN) 为 0，而 CeilToInt(Infinity) 未定义——本
            // 代码库其它地方都显式净化（EnsureCustomNodes、雨滴起始 Y），此处同样处理，不让手改
            // 的配置决定渲染纹理尺寸。
            if (float.IsNaN(size) || float.IsInfinity(size)) size = MinDimension;
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
                string rel = Path.Combine(Loader.ResolveModPath(), "CustomImages", path);
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