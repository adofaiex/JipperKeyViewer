// Resource and font management (unified single-variant build) / 统一单变体构建的资源与字体管理
// Default sprites/fonts ship DEFLATED inside the DLL and are extracted to ModPath\assets\ on
// first run — only where the file is MISSING, so user-replaced assets always win. Sprites load
// from PNG, fonts from OTF/TTF at runtime (no AssetBundle → immune to game Unity-version bumps),
// plus game-font scanning, custom fonts, shadow materials and fallback chains.
// 默认贴图/字体以 DEFLATE 压缩内嵌于 DLL，首次运行释放到 ModPath\assets\——仅在该文件缺失时
// 写入，用户替换过的资源永远优先。贴图从 PNG 运行时加载、字体从 OTF/TTF 运行时构建（不再依赖
// AssetBundle，对游戏 Unity 版本升级免疫），另含游戏字体扫描、自定义字体、阴影材质与后备链。

using System;
using JipperKeyViewer.KeyViewer.Settings;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using TMPro;
using UnityEngine;
using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    /// <summary>
    /// Resource loading: file-based sprites, font scanning, shadow material creation / 资源加载：基于文件的精灵、字体扫描、阴影材质创建
    /// </summary>
    public partial class KeyViewer : MonoBehaviour
    {
        /// <summary>
        /// Default assets embedded in the DLL (deflated): resource name → on-disk file name.
        /// / 内嵌于 DLL 的默认资源（deflate 压缩）：资源名 → 落盘文件名。
        /// </summary>
        private static readonly (string resource, string file)[] BundledAssets =
        {
            ("JipperKeyViewer.Assets.KeyBackground.png", "KeyBackground.png"),
            ("JipperKeyViewer.Assets.KeyOutline.png", "KeyOutline.png"),
            ("JipperKeyViewer.Assets.GhostRain.png", "GhostRain.png"),
            ("JipperKeyViewer.Assets.MAPLESTORY_OTF_BOLD.OTF", "MAPLESTORY_OTF_BOLD.OTF"),
            ("JipperKeyViewer.Assets.cjkFonts-regular-normalized.otf", "cjkFonts-regular-normalized.otf"),
        };

        /// <summary>
        /// Extract embedded default assets into assetsDir — ONLY files that don't exist yet.
        /// Written via a .tmp + move so a crash mid-write never leaves a half font on disk
        /// (a truncated OTF would poison every later load). / 把内嵌默认资源释放到 assetsDir
        /// ——只写尚不存在的文件。经 .tmp + 移动落盘，中途崩溃不会留下残缺字体（截断的 OTF
        /// 会毒化之后每次加载）。
        /// </summary>
        private static void EnsureBundledAssets(string assetsDir)
        {
            try
            {
                Directory.CreateDirectory(assetsDir);
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: cannot create assets directory {assetsDir}: {e.Message}");
                return;
            }
            Assembly asm = typeof(KeyViewer).Assembly;
            int extracted = 0;
            foreach ((string resource, string file) in BundledAssets)
            {
                string path = Path.Combine(assetsDir, file);
                if (File.Exists(path)) continue; // user's own file wins / 用户文件优先
                try
                {
                    using (Stream rs = asm.GetManifestResourceStream(resource))
                    {
                        if (rs == null)
                        {
                            // A missing embedded resource is a BUILD problem (Pack-EmbeddedAssets
                            // not run), not a user problem — name it outright.
                            // 内嵌资源缺失是构建问题（没跑 Pack-EmbeddedAssets），不是用户问题——直接点名。
                            Loader.Error($"KeyViewer: embedded resource missing: {resource}");
                            continue;
                        }
                        string tmp = path + ".tmp";
                        using (Stream ds = new DeflateStream(rs, CompressionMode.Decompress))
                        using (Stream fs = File.Create(tmp))
                            ds.CopyTo(fs);
                        if (File.Exists(path)) File.Delete(tmp); // raced another extract / 并发释放已写好
                        else File.Move(tmp, path);
                        extracted++;
                    }
                }
                catch (Exception e)
                {
                    Loader.Error($"KeyViewer: failed to extract bundled asset '{file}': {e.Message}");
                }
            }
            if (extracted > 0)
                Loader.Log($"KeyViewer: extracted {extracted} bundled default asset(s) to {assetsDir}");
        }

        /// <summary>
        /// Scan for traditional Unity Font objects in the scene and convert them to TMP_FontAsset / 扫描场景中的传统 Unity Font 对象并转换为 TMP_FontAsset
        /// This allows the mod to use any font the game itself uses / 这使 Mod 可以使用游戏本身使用的任何字体
        /// </summary>
        void ScanGameFonts()
        {
            var allFonts = Resources.FindObjectsOfTypeAll<Font>();
            if (allFonts == null || allFonts.Length == 0)
                return;

            // FindObjectsOfTypeAll returns EVERY font loaded anywhere in the game. Each conversion
            // bakes a 1024x1024 (sometimes larger) glyph atlas plus its material, so a title with a
            // few hundred fonts would silently cost hundreds of MB of VRAM the user never asked
            // for and cannot turn off. Cap the list and say so — the bundled fonts and the user's
            // own CustomFont files are unaffected.
            // FindObjectsOfTypeAll 返回游戏中任何位置加载的**所有**字体。每次转换都会烘焙一张
            // 1024×1024（有时更大）的字形图集加材质——标题画面有几百个字体时会静默吃掉用户从未
            // 申请、也无法关闭的数百 MB 显存。现加上限并明确提示；内置字体与用户自己的
            // CustomFont 文件不受影响。
            const int MaxGameFonts = 32;
            int added = 0;
            int skipped = 0;
            foreach (var font in allFonts)
            {
                if (font == null) continue;
                bool exists = false;
                foreach (var e in fontList)
                    if (e.sourceFontName == font.name) { exists = true; break; }
                if (exists) continue;

                if (fontList.Count >= MaxGameFonts)
                {
                    skipped++;
                    continue;
                }

                // Guarded exactly like the three sibling CreateFontAsset sites (the OTF/TTF loader,
                // the CJK loader, and ScanCustomFonts). A game font the baker cannot rasterise — a
                // bitmap or legacy CJK font, one with no rasterisable glyphs — throws here, and this
                // call sits OUTSIDE the try barrier that wraps BuildOverlay, so the exception
                // escapes TryLoadResources -> EnableKeyViewer and the whole overlay never appears.
                // The user sees no key display at all, and the only trace is a Unity log line. One
                // bad optional font must not take the keys down — that is the reason the sibling
                // sites are guarded, and this one was simply missed.
                // 与另外三处 CreateFontAsset（OTF/TTF 加载器、CJK 加载器、ScanCustomFonts）一样加
                // 保护。无法烘焙的游戏字体——位图/旧式 CJK 字体、没有任何可栅格化字形的字体——会在
                // 此抛异常，而本次调用位于包住 BuildOverlay 的 try 屏障**之外**，故异常会逃出
                // TryLoadResources → EnableKeyViewer，整个覆盖层**从不出现**：用户看不到任何按键
                // 显示，唯一痕迹是一行 Unity 日志。一个坏的可选字体不该拖垮按键——这正是那些
                // 孪生调用点被保护的原因，只是这一处被漏掉了。
                TMP_FontAsset tmpFont = null;
                try
                {
                    tmpFont = TMP_FontAsset.CreateFontAsset(font);
                }
                catch (Exception e)
                {
                    Loader.Error($"KeyViewer: could not bake the game font '{font.name}' — skipping it ({e.Message})");
                    continue;
                }
                if (tmpFont != null)
                {
                    var entry = new FontEntry(font.name, tmpFont);
                    entry.sourceFontName = font.name;
                    fontList.Add(entry);
                    added++;
                }
            }

            if (added > 0)
                Loader.Log($"KeyViewer: Converted {added} traditional font(s) to TMP_FontAsset");
            if (skipped > 0)
                Loader.Warning($"KeyViewer: {skipped} game font(s) were not added — the list is capped at {MaxGameFonts} entries to bound the glyph atlas VRAM");
        }

        /// <summary>Destroy a sprite loaded by LoadSpriteFromFile together with the Texture2D behind
        /// it, then clear the field. The sprite owns nothing: its texture is a plain asset, so
        /// destroying the sprite alone would leave the texture resident. Null and already-destroyed
        /// inputs are no-ops. / 销毁 LoadSpriteFromFile 加载的精灵**及其背后的** Texture2D，然后清空
        /// 字段。精灵本身不持有纹理——纹理是独立的普通资源，故只销毁精灵会把纹理留在显存里。
        /// null 与已销毁的输入均为空操作。
        /// </summary>
        private static void DestroyReloadedSprite(ref Sprite sprite)
        {
            if (sprite == null) return;
            Texture2D texture = sprite.texture;
            Destroy(sprite);
            if (texture != null) Destroy(texture);
            sprite = null;
        }

        /// <summary>
        /// Extract bundled defaults, then load sprites from PNG files, fonts from OTF/TTF files, and custom fonts / 释放内嵌默认资源，然后从 PNG 加载精灵、从 OTF/TTF 加载字体以及自定义字体
        /// </summary>
        private bool TryLoadResources()
        {
            if (keyBackgroundSprite != null) return true;

            // Destroy the previous dynamically-created assets before dropping the references —
            // TMP_FontAssets carry atlas textures/materials; without this, every loader-level
            // toggle (UMM off→on) leaked the whole set. The three sprites below are exactly such
            // assets and were simply omitted from this list, so each toggle ALSO leaked three
            // sprites and their backing textures: LoadSpriteFromFile allocates a Texture2D plus a
            // Sprite, and neither is parented to a GameObject, so Unity never reclaims them when
            // the component's object is destroyed. The three lines below were re-assigned
            // unconditionally a few lines further down, quietly orphaning the previous set.
            // 清空前先销毁旧的动态创建资产——TMP_FontAsset 持有图集纹理/材质；否则每次加载器级
            // 开关(UMM 关→开)都会泄漏一整套。下面三个精灵**正是**这类资产，却只是被漏掉了：
            // LoadSpriteFromFile 会分配一个 Texture2D 加一个 Sprite，二者都**不**挂在任何
            // GameObject 下，故组件对象被销毁时 Unity 绝不会回收它们。这三行在几行之后被无条件
            // 重新赋值，于是上一套被静默孤立成孤儿。
            foreach (var e in fontList)
                if (e.font != null) Destroy(e.font);
            fontList.Clear();
            // The source Fonts the dynamic atlases rasterise through, freed with the assets above.
            // Releasing them earlier is what made every non-CJK font render as the CJK face.
            // 动态图集赖以光栅化的源 Font，与上面的资源一同释放。提前释放正是让每一个非 CJK 字体
            // 都显示成 CJK 那张脸的原因。
            ReleaseSourceFonts();
            DestroyReloadedSprite(ref keyBackgroundSprite);
            DestroyReloadedSprite(ref keyOutlineSprite);
            DestroyReloadedSprite(ref ghostRainSprite);
            // The style materials are copies of the just-destroyed font materials — drop them
            // before the next font load builds new ones. / 样式材质是刚被销毁的字体材质的副本——
            // 在下次字体加载构建新材质前先丢弃。
            ReleaseTextStyleMaterials();

            string modPath = Loader.ResolveModPath();
            string assetsDir = Path.Combine(modPath, "assets");

            // Self-install: fresh installs get the embedded defaults on disk; existing files
            // (including user-replaced ones) are never touched. / 自安装:全新安装把内嵌默认
            // 资源落到磁盘;已存在的文件(含用户替换的)绝不动。
            EnsureBundledAssets(assetsDir);

            // Reclaim whatever an interrupted package import left behind: a hard-killed import
            // keeps its GUID staging folder (and the up-to-2 GB inside it) forever, because the
            // cleanup only runs on the normal Dispose/Rollback path.
            // 回收被中断的包导入留下的东西：被强杀的导入会永久保留它的 GUID 暂存目录（以及里面
            // 最多 2 GB 的内容），因为清理只在正常的 Dispose/Rollback 路径上跑。
            try { global::JipperKeyViewer.KeyViewer.KeyViewer.SweepStaleStaging(); } catch (Exception) { /* best effort / 尽力而为 */ }

            ScanGameFonts();

            keyBackgroundSprite = LoadSpriteFromFile(Path.Combine(assetsDir, "KeyBackground.png"));
            keyOutlineSprite = LoadSpriteFromFile(Path.Combine(assetsDir, "KeyOutline.png"));
            ghostRainSprite = LoadSpriteFromFile(Path.Combine(assetsDir, "GhostRain.png"));

            LoadFontFromFile(assetsDir, "MAPLESTORY_OTF_BOLD.OTF", "MapleStory", ref mapleFont, fontList);
            LoadCJKFontFromFile(assetsDir, "cjkFonts-regular-normalized.otf", "CJK (Default)", fontList);

            if (keyBackgroundSprite == null)
                Loader.Warning("KeyViewer: KeyBackground.png not found in assets/");
            if (keyOutlineSprite == null)
                Loader.Warning("KeyViewer: KeyOutline.png not found in assets/");
            // Without the sprite, ghost rain silently degrades to ghost-color solid columns —
            // log it so the change isn't mysterious. / 缺贴图时鬼雨静默退化为鬼雨色纯色柱——
            // 记日志避免莫名其妙。
            if (ghostRainSprite == null)
                Loader.Warning("KeyViewer: GhostRain.png not found in assets/ (ghost rain falls back to solid columns)");

            ScanCustomFonts();
            LinkFallbackFonts();

            if (Settings.Data.FontIndex >= fontList.Count)
                Settings.Data.FontIndex = 0;

            fontNameIndex = new Dictionary<string, int>(fontList.Count);
            for (int i = 0; i < fontList.Count; i++)
                fontNameIndex[fontList[i].name] = i;

            return true;
        }

        /// <summary>
        /// Load a PNG file as a Sprite with 9-slice border / 加载 PNG 文件为带九宫格边框的 Sprite
        /// Border values (11px) match the original Unity import settings / 边框值（11px）与原始 Unity 导入设置一致
        /// Uses ImageConversion.LoadImage via reflection since the module isn't referenced at compile time / 通过反射调用 ImageConversion.LoadImage
        /// </summary>
        private static Sprite LoadSpriteFromFile(string path)
        {
            // Delegates to the shared loader (FreeMake image nodes use it too, without the
            // 9-slice border). / 委托给共享加载器（FreeMake 图片节点同样使用它，无九宫格边框）。
            return KvImageLoader.LoadSprite(path, new Vector4(11, 11, 11, 11));
        }

        /// <summary>
        /// Load an OTF/TTF font file and add it to the font list / 加载 OTF/TTF 字体文件并添加到字体列表
        /// </summary>
        private static void LoadFontFromFile(string assetsDir, string fileName, string entryName, ref TMP_FontAsset target, List<FontEntry> fontList)
        {
            string path = Path.Combine(assetsDir, fileName);
            // Without logging here the entry simply never appears in the font list with no hint
            // why. / 不打日志则字体列表里永远不出现该条目且无任何线索。
            if (!File.Exists(path)) { Loader.Error($"KeyViewer: font file not found: {path}"); return; }
            try
            {
                Font font = new Font(path);
                if (font == null) { Loader.Error($"KeyViewer: failed to create font from '{fileName}'"); return; }
                // CreateFontAsset bakes the glyph atlas immediately and does not keep a reference to
                // the source Font, so the legacy Font object can be released right away. It was
                // never destroyed, which leaked the native font face plus a file copy on every
                // font-set reload. / CreateFontAsset 会立即烘焙字形图集且不持有源 Font 引用，
                // 因此 legacy Font 对象可立即释放。此前从不销毁，每次字体集重载都泄漏一份原生
                // 字体面与文件副本。
                try
                {
                    target = TMP_FontAsset.CreateFontAsset(font);
                }
                finally
                {
                    UnityEngine.Object.Destroy(font);
                }
                // CreateFontAsset can return null (unreadable font) — a null entry would render
                // as an empty row in the font list; skip it like ScanCustomFonts does.
                // CreateFontAsset 可能返回 null(不可读字体)——null 条目会在字体列表中渲染成
                // 空行;与 ScanCustomFonts 一致地跳过。
                if (target != null)
                {
                    var entry = new FontEntry(entryName, target);
                    entry.sourceFontName = Path.GetFileNameWithoutExtension(fileName);
                    fontList.Add(entry);
                }
                else
                {
                    Loader.Error($"KeyViewer: TMP_FontAsset.CreateFontAsset failed for '{fileName}'");
                }
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: Failed to load font '{fileName}': {e.Message}");
            }
        }

        /// <summary>
        /// Load CJK font and insert it at the front of the font list / 加载 CJK 字体并插入到字体列表最前面
        /// </summary>
        private static void LoadCJKFontFromFile(string assetsDir, string fileName, string entryName, List<FontEntry> fontList)
        {
            string path = Path.Combine(assetsDir, fileName);
            // Losing the CJK font also breaks the fallback chain LinkFallbackFonts wires into every
            // other font — CJK key labels would render as boxes with zero log hints. / CJK 字体缺失
            // 还会破坏 LinkFallbackFonts 接到其他所有字体上的后备链——中文键位会渲染成方块且无任何日志线索。
            if (!File.Exists(path)) { Loader.Error($"KeyViewer: CJK font file not found: {path} (CJK labels render as boxes)"); return; }
            try
            {
                Font font = new Font(path);
                if (font == null) { Loader.Error($"KeyViewer: failed to create CJK font from '{fileName}'"); return; }
                TMP_FontAsset cjkFont;
                // Same as LoadFontFromFile: the source Font is released right after the atlas is
                // baked, so a font-set reload no longer leaks a native face per attempt.
                // 与 LoadFontFromFile 相同：图集烘焙后立即释放源 Font，字体集重载不再每次泄漏。
                try
                {
                    cjkFont = TMP_FontAsset.CreateFontAsset(font);
                }
                finally
                {
                    UnityEngine.Object.Destroy(font);
                }
                // Null CJK font breaks the whole fallback chain; don't insert the entry when
                // creation failed — Insert(0) would occupy the default slot with a dead font.
                // CJK 字体为 null 会破坏整条后备链;创建失败时不要插入条目——Insert(0) 会把
                // 默认槽位让给死字体。
                if (cjkFont != null)
                {
                    var entry = new FontEntry(entryName, cjkFont);
                    entry.sourceFontName = Path.GetFileNameWithoutExtension(fileName);
                    fontList.Insert(0, entry);
                }
                else
                {
                    Loader.Error($"KeyViewer: TMP_FontAsset.CreateFontAsset failed for CJK font '{fileName}' (CJK labels render as boxes)");
                }
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: Failed to load CJK font '{fileName}': {e.Message}");
            }
        }

        /// <summary>
        /// Get the currently selected font from the font list / 从字体列表中获取当前选中的字体
        /// </summary>
        /// <remarks>
        /// Resolve by NAME, and only fall back to the index for a profile that never stored one.
        ///
        /// FontIndex is a POSITION, and positions in this list are not identities: entries with a
        /// null asset are pruned with RemoveAt (shifting everything after them down), and each CJK
        /// font is Insert(0) at the FRONT, so every one of them reverses the custom fonts ahead of
        /// it. FontName, by contrast, is written on every font change and is the only stable handle
        /// the profile has. RestoreFontOnce re-maps the name to an index exactly once, behind
        /// `fontRestored`; after that, any list change left FontIndex pointing at whatever font
        /// happened to slide into that slot — the profile still said "LexendDeca" while the overlay
        /// drew a different face, and switching fonts appeared to do nothing.
        ///
        /// The index is still honoured when FontName is empty (a legacy profile that never stored
        /// one) or when the name is genuinely gone from the list, so nothing that used to work stops
        /// working — the name simply wins whenever both are present and consistent.
        ///
        /// 按**名字**解析，只在从未存过名字的配置上才回退到索引。
        ///
        /// FontIndex 是**位置**，而这个列表里的位置不是身份：资产为 null 的条目会被 `RemoveAt`
        /// 剪掉（其后所有条目整体前移），而每个 CJK 字体都被 `Insert(0)` 插在**最前面**，等于把它
        /// 前面所有自定义字体都反转一次。相比之下 FontName 每次换字体都会写入，是配置唯一稳定的
        /// 句柄。RestoreFontOnce 只在 `fontRestored` 之后第一次做名字→索引的重映射；此后列表一有
        /// 变化，FontIndex 就指向了恰好滑进那个槽位的别的字体——配置里仍写着「LexendDeca」，画面上
        /// 却是另一张脸，而切换字体看上去毫无作用。
        ///
        /// FontName 为空（从未存过名字的旧配置）或名字确实已不在列表中时，仍然沿用索引，故原本可用
        /// 的行为不会失效——两者都存在且一致时，名字优先。
        /// </remarks>
        private TMP_FontAsset GetCurrentFont()
            => GetCurrentFontEntry()?.font;

        /// <summary>The entry the profile actually points at, resolved by name first. Exposed so the
        /// settings page can LABEL the current font from the same source the renderer uses — it used
        /// to read FontIndex itself, so after a list change the page highlighted one font while the
        /// overlay drew another, and the two disagreed about what "current" even meant. / 配置实际
        /// 指向的条目，按名字优先解析。暴露出来是为了让设置页**用渲染器同一来源**标注当前字体——
        /// 它此前自己读 FontIndex，故列表一变化，设置页高亮的字体与画面上画出的字体就不同，
        /// 两者对「当前」的定义都不一致了。
        /// </summary>
        internal FontEntry GetCurrentFontEntry()
        {
            if (fontList.Count == 0) return null;
            if (!string.IsNullOrEmpty(Settings.Data.FontName))
            {
                for (int i = 0; i < fontList.Count; i++)
                {
                    FontEntry entry = fontList[i];
                    if (entry != null && entry.font != null
                        && string.Equals(entry.name, Settings.Data.FontName, StringComparison.OrdinalIgnoreCase))
                        return entry;
                }
            }
            int idx = Mathf.Clamp(Settings.Data.FontIndex, 0, fontList.Count - 1);
            return fontList[idx];
        }

        /// <summary>
        /// Update the font on all key text elements / 更新所有按键文本元素的字体
        /// Called when the user changes font selection / 用户更改字体时调用
        /// </summary>
        private void UpdateAllFonts()
        {
            TMP_FontAsset currentFont = GetCurrentFont();
            if (currentFont == null) return;
            // Resolve per text KIND (label vs. count) so the Display tab's separate outline/shadow
            // pairs actually apply; a node's own override is applied afterwards by
            // ApplyCustomTextStyles when the overlay rebuilds. / 按文本类别（标签/计数）分别解析，
            // 使显示页的两套描边/阴影真正生效；节点自身的覆盖在覆盖层重建时由
            // ApplyCustomTextStyles 追加应用。
            Material keyMat = GetTextStyleMaterial(currentFont, Rendering.KvTextStyle.Resolve(Settings.Data, null, Rendering.KvTextKind.KeyLabel));
            Material countMat = GetTextStyleMaterial(currentFont, Rendering.KvTextStyle.Resolve(Settings.Data, null, Rendering.KvTextKind.Count));
            FontStyles style = (FontStyles)Settings.Data.FontStyleFlags;
            // node is non-null only for custom-layout keys: their own text style then wins over
            // the global one, which is the ONLY way a font switch can preserve a per-node override
            // (this method rewrites fontMaterial on every text). / node 仅对自定义布局按键非空：
            // 此时节点自身的文字样式优先于全局——这是字体切换能保住节点级覆盖的唯一办法（本方
            // 法会重写每个文本的 fontMaterial）。
            void UpdateText(TMP_Text t, Material mat, Settings.FmNode node = null, bool isCount = false)
            {
                if (t == null) return;
                t.font = currentFont;
                Material use = mat;
                if (node != null && node.UseCustomTextStyle)
                {
                    use = GetTextStyleMaterial(currentFont, Rendering.KvTextStyle.Resolve(Settings.Data, node,
                        isCount ? Rendering.KvTextKind.Count : Rendering.KvTextKind.KeyLabel));
                }
                if (use != null) ApplyFontMaterial(t, use);
                t.fontStyle = node != null
                    ? (isCount && node.UseCustomCountFontStyle ? (FontStyles)node.CountFontStyleFlags : (FontStyles)node.FontStyleFlags)
                    : style;
                // Key labels are sized purely by TMP auto-sizing (enableAutoSizing is on for every
                // key text and nothing sets `fontSize`), so fontSizeMax is the CEILING the fitter
                // works under, measured against the RectTransform — which the node's LabelScale
                // then shrinks via localScale. Once the fitting size falls below that ceiling,
                // raising it does nothing and the label is stuck, which is why the size control
                // looked completely inert. Divide the ceiling by the node's scale so the number in
                // the field is the number on screen. Fixed-layout keys have no such scale, and a
                // scale of 1 is a no-op, so neither is affected.
                // 按键标签纯粹由 TMP 自动缩放决定（每个按键文字都开着 enableAutoSizing，且没有任何地方
                // 设置 `fontSize`），故 fontSizeMax 是适配器工作的**上限**，且是相对 RectTransform
                // 量出来的——而节点的 LabelScale 又通过 localScale 把那个 transform 缩小。一旦适配
                // 尺寸低于该上限，调高它就不起作用、标签被卡死，这正是字号控件看起来完全失灵的原因。
                // 故把上限除以节点缩放，使字段里的数字就是屏幕上的数字。固定布局按键没有这个缩放，
                // 缩放为 1 时也是空操作，故两者都不受影响。
                float sizeScale = node != null
                    ? (isCount ? (node.CountScale > 0f ? node.CountScale : 1f) : (node.LabelScale > 0f ? node.LabelScale : 1f))
                    : 1f;
                t.fontSizeMax = Settings.Data.KeyFontSize / sizeScale;
            }
            // Per-key text size only exists for the FIXED layouts. In FreeMake the loop index is a
            // custom node slot, so a leftover PerKeyFontSize entry (e.g. slot 0 = 24) would silently
            // resize an unrelated node whenever that node has no explicit FontSize. / 每键字号只属于
            // 固定布局；FreeMake 的下标是节点槽位，残留的每键字号会误用到无自定义字号的节点上。
            bool hasPerKey = Settings.Data.EnablePerKeyTextSize && !IsCustomLayout;
            void ApplyNodeFontSize(TMP_Text t, Settings.FmNode node, bool isCount)
            {
                if (t == null || node == null) return;
                float size = isCount
                    ? (node.CountFontSize > 0f ? node.CountFontSize : node.FontSize)
                    : node.FontSize;
                // Same ceiling-vs-scale division as the global write above: a node's own size has to
                // be divided by the scale its RectTransform will be shrunk by, or the control is
                // inert for exactly the nodes that use it.
                // 与上面全局那次写入同样的「上限 ÷ 缩放」：节点自己的字号必须除以它那个将被缩小的
                // RectTransform 的缩放，否则**恰恰是**使用该控件的那些节点上它完全失灵。
                float nodeScale = isCount
                    ? (node.CountScale > 0f ? node.CountScale : 1f)
                    : (node.LabelScale > 0f ? node.LabelScale : 1f);
                if (size > 0f) t.fontSizeMax = size / nodeScale;
            }
            void ApplyPerKeyOverride(TMP_Text t, int pi)
            {
                if (t == null || !hasPerKey || pi < 0 || Settings.Data.PerKeyFontSize == null
                    || pi >= Settings.Data.PerKeyFontSize.Length) return;
                float fs = Settings.Data.PerKeyFontSize[pi];
                if (fs > 0f) t.fontSizeMax = fs;
            }
            if (Keys != null)
            {
                for (int i = 0; i < Keys.Length; i++)
                {
                    if (Keys[i] == null) continue;
                    int pi = i;
                    Settings.FmNode node = Keys[i].CustomNode;
                    UpdateText(Keys[i].text, keyMat, node, false);
                    ApplyPerKeyOverride(Keys[i].text, pi);
                    ApplyNodeFontSize(Keys[i].text, node, false);
                    // value: reset FIRST, then override — the old order let UpdateText's
                    // unconditional fontSizeMax write clobber the per-key size (the Kps/Total
                    // blocks below already had the correct order).
                    // value:先重置后覆盖——旧顺序会让 UpdateText 的无条件 fontSizeMax 写入
                    // 抹掉每键字号(下方 Kps/Total 段原本顺序就正确)。
                    UpdateText(Keys[i].value, countMat, node, true);
                    ApplyPerKeyOverride(Keys[i].value, pi);
                    ApplyNodeFontSize(Keys[i].value, node, true);
                }
            }
            int kpsPi = MaxKeySlots;
            int totalPi = MaxKeySlots + 1;
            // Explicit null checks, not `?.` — Key is a MonoBehaviour, and the null-conditional
            // bypasses Unity's destroyed-check (a destroyed component would slip through and only
            // survive via the later Unity-overload checks). / 显式判空而非 `?.`——Key 是
            // MonoBehaviour，空条件运算符绕过 Unity 的销毁检查（已销毁组件会漏进来，仅靠后续
            // Unity 重载检查兜底）。
            if (Kps != null)
            {
                Settings.FmNode kpsNode = Kps.CustomNode;
                UpdateText(Kps.text, keyMat, kpsNode, false);
                ApplyPerKeyOverride(Kps.text, kpsPi);
                ApplyNodeFontSize(Kps.text, kpsNode, false);
                UpdateText(Kps.value, countMat, kpsNode, true);
                ApplyPerKeyOverride(Kps.value, kpsPi);
                ApplyNodeFontSize(Kps.value, kpsNode, true);
            }
            if (Total != null)
            {
                Settings.FmNode totalNode = Total.CustomNode;
                UpdateText(Total.text, keyMat, totalNode, false);
                ApplyPerKeyOverride(Total.text, totalPi);
                ApplyNodeFontSize(Total.text, totalNode, false);
                UpdateText(Total.value, countMat, totalNode, true);
                ApplyPerKeyOverride(Total.value, totalPi);
                ApplyNodeFontSize(Total.value, totalNode, true);
            }
            ClearTextGradientStates();
            TickTextGradients();
        }

        /// <summary>
        /// Get or create a shadow material for the given font / 获取或为指定字体创建阴影材质
        /// Uses the "UNDERLAY_ON" shader keyword for TMP drop shadow / 使用 TMP 的 "UNDERLAY_ON" 着色器关键字实现投影
        /// Materials are cached and reused / 材质会被缓存和复用
        /// </summary>
        Material GetTextStyleMaterial(TMP_FontAsset font, in Rendering.KvTextStyle style)
        {
            if (font == null) return null;
            if (!style.NeedsMaterial)
            {
                // Neutral style: the font's own material, exactly what every TMP text used before
                // outline/shadow became configurable.
                // 中性样式：字体自带材质，即描边/阴影可配置之前所有 TMP 文本用的那个。
                if (!neutralFontMaterials.TryGetValue(font, out Material neutral) || neutral == null)
                {
                    neutral = GetFontMaterial(font);
                    neutralFontMaterials[font] = neutral;
                }
                return neutral;
            }
            long key = style.CacheKey(font.GetInstanceID());
            if (textStyleMaterials.TryGetValue(key, out Material cached) && cached != null) return cached;
            Material fontMat = GetFontMaterial(font);
            if (fontMat == null)
            {
                Loader.Error("KeyViewer: Cannot get material from font asset, skipping text outline/shadow");
                return null;
            }
            Material mat = new Material(fontMat);
            // TMP's SDF outline and underlay are shader keywords + floats on the font material; the
            // keyword must be enabled or the shader skips the pass entirely.
            // TMP 的 SDF 描边与 underlay 是字体材质上的着色器关键字 + 浮点值；必须启用关键字，
            // 否则着色器整段跳过。
            if (style.Outline)
            {
                mat.EnableKeyword("OUTLINE_ON");
                mat.SetColor("_OutlineColor", style.OutlineColor);
                mat.SetFloat("_OutlineWidth", style.OutlineWidth);
            }
            if (style.Shadow)
            {
                mat.EnableKeyword("UNDERLAY_ON");
                mat.SetColor("_UnderlayColor", style.ShadowColor);
                mat.SetFloat("_UnderlayOffsetX", style.ShadowOffsetX);
                mat.SetFloat("_UnderlayOffsetY", style.ShadowOffsetY);
                mat.SetFloat("_UnderlaySoftness", style.ShadowSoftness);
            }
            // The cache key includes colour/offset floats, so every distinct style mints a new
            // material. The cache key quantizes to 1/1000 while the GUI sliders are continuous and
            // UpdateAllFonts runs on EVERY slider tick — one drag of the shadow offset from -20 to
            // +20 could mint tens of thousands of materials, none of them freed until teardown
            // (hundreds of MB plus the native-object churn).
            //
            // Evicting naively would destroy a material some live text still renders with, turning
            // that text BLANK — far worse than the growth. So materials are reference counted by
            // ApplyFontMaterial, and only entries nobody is using are ever destroyed.
            // 缓存键含颜色/偏移，每个不同样式都会新建材质。键按 1/1000 量化，而 GUI 滑杆是连续的、
            // UpdateAllFonts 在**每个**滑杆 tick 都跑——把阴影偏移从 -20 拖到 +20 一次就能铸出
            // 数万个材质，在拆解前一个都不会释放（数百 MB 外加原生对象抖动）。
            //
            // 粗暴淘汰会销毁某个存活文本仍在渲染的材质，让那个文本变成**空白**——比增长严重得多。
            // 因此材质由 ApplyFontMaterial 引用计数，只有无人使用的条目才会被销毁。
            textStyleMaterials[key] = mat;
            // Exclude the entry we JUST minted. Its refcount is 0 by construction — it is the one
            // entry guaranteed to match the eviction predicate — so without this the "which entry
            // dies" decision can land on the material about to be returned, handing a DESTROYED
            // Material to the caller. Object.Destroy is deferred, so the text renders once and is
            // blank from the next frame on: exactly the failure the ref counting exists to prevent.
            // 排除**刚**铸出的条目。它的引用计数按构造就是 0——是唯一必定匹配淘汰判据的条目——
            // 故不加此排除时「哪条会死」的判定可能落在正要返回的材质上，把一个**已销毁**的
            // 材质交给调用方。Object.Destroy 是延迟的，故文本会渲染一帧然后变空白——正是引用
            // 计数要防止的那种故障。
            EvictUnusedTextStyleMaterials(key);
            return mat;
        }

        /// <summary>How many distinct text-style materials may be cached. Far above what any real
        /// configuration needs (a handful of styles at a time), low enough to bound the worst case
        /// when a slider is dragged. / 文字样式材质缓存上限。远高于任何真实配置所需（同时只有少数
        /// 几种样式），又低到足以约束拖动滑杆时的最坏情况。</summary>
        private const int MaxTextStyleMaterials = 48;

        /// <summary>Destroy cached materials that no live TMP_Text is using, until the cache is
        /// back under the cap. Uses a scratch list so the eviction scan never allocates.
        /// `keepKey` is the entry the caller is about to return — it must never be the one evicted.
        /// 销毁没有任何存活 TMP_Text 正在使用的缓存材质，直到缓存回到上限以下。用暂存列表
        /// 避免淘汰扫描产生分配。`keepKey` 是调用方即将返回的条目——绝不能被淘汰掉的正是它。
        /// </summary>
        private void EvictUnusedTextStyleMaterials(long keepKey = -1)
        {
            if (textStyleMaterials.Count <= MaxTextStyleMaterials) return;
            // Drop entries whose text was destroyed: Object.Destroy is deferred, so a destroyed
            // component still compares non-null for the rest of the frame and would keep its
            // material pinned as "in use" until the next sweep.
            // 丢弃文本已被销毁的条目：Object.Destroy 是延迟的，已销毁组件在帧内比较仍为非 null，
            // 会把其材质一直钉成"使用中"直到下一次清扫。
            if (textStyleMaterialUse.Count > 0)
            {
                textStyleEvictScratch.Clear();
                foreach (KeyValuePair<TMP_Text, int> pair in textStyleMaterialUse)
                    if (pair.Key == null) textStyleEvictScratch.Add(pair.Key);
                for (int i = 0; i < textStyleEvictScratch.Count; i++)
                    ReleaseTextMaterialUse(textStyleEvictScratch[i]);
            }
            // Collect, THEN remove. The previous body called textStyleMaterials.Remove() from
            // inside the foreach over that same dictionary: .NET invalidates the enumerator, so
            // the very next MoveNext() threw InvalidOperationException. It only escaped notice
            // because the `break` usually fired first — but that needs the count to reach the cap
            // on that same iteration, which is not guaranteed, and the exception would surface
            // in an IMGUI callback and disable the whole settings window.
            // 先收集**再**删除。旧代码在遍历该字典的 foreach 内部调 textStyleMaterials.Remove()：
            // .NET 会使枚举器失效，故下一次 MoveNext() 抛 InvalidOperationException。它没被发现
            // 只是因为通常会先命中 break——但那要求恰好在同一次迭代把计数降到上限，而并无保证；
            // 异常会浮到 IMGUI 回调里并禁用整个设置窗口。
            textStyleEvictKeyScratch.Clear();
            foreach (KeyValuePair<long, Material> pair in textStyleMaterials)
            {
                if (pair.Value == null) { textStyleEvictKeyScratch.Add(pair.Key); continue; }
                if (pair.Key == keepKey) continue;
                if (textStyleMaterialRefs.ContainsKey(pair.Value.GetInstanceID())) continue;
                textStyleEvictKeyScratch.Add(pair.Key);
            }
            for (int i = 0; i < textStyleEvictKeyScratch.Count; i++)
            {
                long victim = textStyleEvictKeyScratch[i];
                Material material;
                if (textStyleMaterials.TryGetValue(victim, out material))
                {
                    textStyleMaterials.Remove(victim);
                    if (material != null) UnityEngine.Object.Destroy(material);
                }
                if (textStyleMaterials.Count <= MaxTextStyleMaterials) break;
            }
        }

        /// <summary>How many texts may be stamped onto a destroyed component before the dead-key
        /// sweep runs. Bounds both the leak and the sweep cost: the sweep walks the whole
        /// dictionary, so running it on every stamp would be O(n²) per overlay build
        /// (≈215 texts → ≈46k comparisons). / 允许被「钉」在已销毁组件上的文本数量，超过即触发
        /// 死键清扫。同时约束泄漏与清扫开销：清扫要遍历整个字典，每次盖章都跑会让每次覆盖层构建
        /// 变成 O(n²)（约 215 个文本 → 约 4.6 万次比较）。</summary>
        private const int MaxDeadTextMaterials = 256;

        private int textStampsSinceDeadSweep;

        /// <summary>Point a TMP text at a text-style material, keeping the reference count in step.
        /// Every `text.fontMaterial = ...` for a CACHED material must go through here — a direct
        /// assignment would leave the count stale and the material un-evictable (or, worse, evictable
        /// while still in use). / 把 TMP 文本指向某个文字样式材质，并同步维护引用计数。所有对
        /// **缓存材质**的 `text.fontMaterial = ...` 都必须经由此处——直接赋值会让计数失真，
        /// 材质要么无法被回收，要么在仍被使用时被回收。</summary>
        internal void ApplyFontMaterial(TMP_Text text, Material material)
        {
            if (text == null || material == null) return;
            int newId = material.GetInstanceID();
            if (textStyleMaterialUse.TryGetValue(text, out int oldId))
            {
                if (oldId == newId) return;
                ReleaseTextMaterialUse(text);
            }
            // This dictionary is keyed by the COMPONENT and holds a strong reference, so an entry
            // pins a destroyed TMP_Text (and its glyph/characterInfo arrays) until it is removed.
            // ResetKeyViewer — the per-edit rebuild that a slider drag drives 60-120×/s — destroys
            // every text but does NOT call ReleaseTextStyleMaterials (that runs on DisableKeyViewer
            // and OnDestroy only), so each rebuild used to leave ~215 pinned dead components behind.
            // The only pruning code used to sit behind `textStyleMaterials.Count > 48` inside
            // EvictUnusedTextStyleMaterials, which itself only runs when a NEW material is minted —
            // and the stock configuration (shadow on, outline off) mints exactly ONE material, so
            // 1 <= 48 and the sweep never ran, ever. A normal FreeMake tuning session accumulated
            // hundreds of thousands of pinned components. Sweep on the dictionary's own size instead.
            // 该字典以**组件**为键并持有强引用，故一个条目会把已销毁的 TMP_Text（及其字形/
            // characterInfo 数组）一直钉住。ResetKeyViewer（滑杆拖动每秒触发 60-120 次的逐次编辑
            // 重建）销毁所有文本却**不**调 ReleaseTextStyleMaterials（那只在 DisableKeyViewer 与
            // OnDestroy 里跑），故每次重建都会留下约 215 个被钉住的死组件。唯一的清理代码此前位于
            // EvictUnusedTextStyleMaterials 内的 `textStyleMaterials.Count > 48` 之后，而该方法本身
            // 只在**铸出**新材质时运行——而出厂配置（开阴影、关描边）只铸**一个**材质，故
            // 1 <= 48，清扫**从未**运行。一次正常的 FreeMake 调参会累积数十万被钉住的组件。
            // 改为按该字典自身的大小清扫。
            if (++textStampsSinceDeadSweep >= MaxDeadTextMaterials
                && textStyleMaterialUse.Count > MaxDeadTextMaterials)
            {
                textStampsSinceDeadSweep = 0;
                textStyleEvictScratch.Clear();
                foreach (KeyValuePair<TMP_Text, int> pair in textStyleMaterialUse)
                    if (pair.Key == null) textStyleEvictScratch.Add(pair.Key);
                for (int i = 0; i < textStyleEvictScratch.Count; i++)
                    ReleaseTextMaterialUse(textStyleEvictScratch[i]);
            }
            textStyleMaterialUse[text] = newId;
            textStyleMaterialRefs[newId] = GetRefCount(newId) + 1;
            text.fontMaterial = material;
        }

        private int GetRefCount(int instanceId)
            => textStyleMaterialRefs.TryGetValue(instanceId, out int c) ? c : 0;

        private void ReleaseTextMaterialUse(TMP_Text text)
        {
            if (!textStyleMaterialUse.TryGetValue(text, out int id)) return;
            textStyleMaterialUse.Remove(text);
            int c = GetRefCount(id) - 1;
            if (c > 0) textStyleMaterialRefs[id] = c;
            else textStyleMaterialRefs.Remove(id);
        }

        /// <summary>Destroy every cached text-style material (teardown), so a long session of style
        /// tweaks cannot leak materials. The font's own materials are never touched. / 销毁全部缓存
        /// 文字样式材质（拆解时），使反复调样式的长会话不会泄漏材质。字体自带材质绝不触碰。</summary>
        private void ReleaseTextStyleMaterials()
        {
            foreach (Material m in textStyleMaterials.Values)
                if (m != null) Destroy(m);
            textStyleMaterials.Clear();
            neutralFontMaterials.Clear();
            // The reference tracking indexes destroyed materials, so it must go with them —
            // otherwise a later GetInstanceID could collide with a recycled id and a fresh
            // material would look permanently "in use" (and never be evicted).
            // 引用跟踪索引的是已销毁材质，必须一并清除——否则之后的 GetInstanceID 可能与回收复用
            // 的 id 冲突，新材质会被永久判为"使用中"而永不回收。
            textStyleMaterialUse.Clear();
            textStyleMaterialRefs.Clear();
            textStyleEvictScratch.Clear();
        }

        static MemberInfo cachedMaterialMember;
        /// <summary>Type the cached member was resolved from — a MemberInfo is only valid for the
        /// type it came from, so a different font type forces a re-resolve. / 缓存成员所属的类型
        /// ——MemberInfo 只对其来源类型有效，字体类型不同时必须重新解析。</summary>
        static Type cachedMaterialType;
        static bool cachedMaterialLogged;

        /// <summary>
        /// Get material from TMP_FontAsset via reflection (handles API differences across Unity/TMP versions) / 通过反射从 TMP_FontAsset 获取材质（处理不同 Unity/TMP 版本的 API 差异）
        /// </summary>
        static Material GetFontMaterial(TMP_FontAsset font)
        {
            if (font == null) return null;
            try
            {
                // The cached MemberInfo comes from the FIRST font's runtime type and is then reused
                // unconditionally. TMP_FontAsset is not sealed, so a subclassed asset would make
                // GetValue throw TargetException — and neither this method nor its callers guard
                // that, so the exception escaped into the overlay build. Re-resolve on a type
                // mismatch and never let the lookup break the caller.
                // 缓存的 MemberInfo 来自**首个**字体的运行时类型，之后被无条件复用。TMP_FontAsset
                // 并非 sealed，子类资产会让 GetValue 抛 TargetException——而本方法与调用方都没有
                // 防护，异常会逃逸进覆盖层构建。类型不匹配时重新解析，且绝不让查找失败影响调用方。
                Type fontType = font.GetType();
                if (cachedMaterialMember == null || cachedMaterialType != fontType)
                {
                    const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                    cachedMaterialMember = (MemberInfo)fontType.GetProperty("material", flags) ?? fontType.GetField("material", flags);
                    cachedMaterialType = fontType;
                }

                Material result = null;
                if (cachedMaterialMember is PropertyInfo pi)
                {
                    var val = pi.GetValue(font);
                    if (val is Material mat) result = mat;
                }
                else if (cachedMaterialMember is FieldInfo fi)
                {
                    var val = fi.GetValue(font);
                    if (val is Material mat) result = mat;
                }

                if (!cachedMaterialLogged)
                {
                    cachedMaterialLogged = true;
                    string foundBy = cachedMaterialMember != null
                        ? $"{cachedMaterialMember.MemberType} \"{cachedMaterialMember.Name}\""
                        : "none";
                    if (cachedMaterialMember == null)
                        // This used to be Loader.Log, so a font asset whose material member cannot
                        // be resolved showed up as one Info line — the visible symptom is outlines
                        // and shadows silently doing nothing. It is a build problem, so say so.
                        // 此前是 Loader.Log：解析不到材质成员时只输出一行 Info——可见症状是描边
                        // 与阴影静默失效。这是构建问题，应当报错。
                        Loader.Error("KeyViewer: font material member not found — text outline/shadow will not render");
                    else
                        Loader.Log($"KeyViewer: Font material resolved via {foundBy}");
                }
                return result;
            }
            catch (Exception e)
            {
                if (!cachedMaterialLogged)
                {
                    cachedMaterialLogged = true;
                    Loader.Error($"KeyViewer: reading the font material failed: {e.GetType().Name}: {e.Message}");
                }
                return null;
            }
        }

        /// <summary>Hold a source Font alive for as long as the font asset that rasterises through
        /// it. A dynamic TMP atlas cannot produce a glyph without it, so freeing one at creation time
        /// silently kills every character that font would ever draw. Freed once, on teardown, by
        /// ReleaseSourceFonts. / 让源 Font 活得和「靠它光栅化」的字体资源一样久。动态 TMP 图集没有它
        /// 就产不出任何一个字形，故在创建时就释放它，等于悄悄废掉该字体要画的每一个字符。仅在拆解
        /// 时由 ReleaseSourceFonts 释放一次。
        ///
        /// 刻意是**实例**字段而非 static：该分部类的静态初始化器一旦需要 `Font`，就等于要求
        /// **任何**触碰 KeyViewer 的调用方都带上 UnityEngine.TextRenderingModule——Harness 只引用
        /// CoreModule，会立刻以程序集加载失败暴露这一点。
        /// </summary>
        readonly List<Font> sourceFonts = new List<Font>();

        void RetainSourceFont(Font font)
        {
            if (font != null) sourceFonts.Add(font);
        }

        void ReleaseSourceFonts()
        {
            for (int i = 0; i < sourceFonts.Count; i++)
                if (sourceFonts[i] != null) UnityEngine.Object.Destroy(sourceFonts[i]);
            sourceFonts.Clear();
        }

        /// <summary>
        /// Link CJK font as fallback to all other fonts so Chinese characters display correctly / 将 CJK 字体链接为所有其他字体的后备字体，使中文字符正确显示
        /// </summary>
        static void LinkFallbackFonts()
        {
            FontEntry cjkEntry = null;
            foreach (var e in fontList)
                if (e.name == "CJK (Default)") { cjkEntry = e; break; }
            if (cjkEntry?.font == null) return;

            foreach (var entry in fontList)
            {
                if (entry.font == null || entry == cjkEntry) continue;
                if (entry.font.fallbackFontAssetTable == null)
                    entry.font.fallbackFontAssetTable = new List<TMP_FontAsset>();
                if (!entry.font.fallbackFontAssetTable.Contains(cjkEntry.font))
                    entry.font.fallbackFontAssetTable.Add(cjkEntry.font);
            }
        }

        /// <summary>
        /// Scan the CustomFont directory for .ttf and .otf files and load them as TMP_FontAsset / 扫描 CustomFont 目录中的 .ttf 和 .otf 文件并将其作为 TMP_FontAsset 加载
        /// </summary>
        void ScanCustomFonts()
        {
            string modPath = Loader.ResolveModPath();
            string customFontDir = Path.Combine(modPath, "CustomFont");
            int customFontCount = 0;
            string[] fontFiles;
            // Every directory operation is guarded: this method runs inside the overlay build
            // (TryLoadResources → EnableKeyViewer → OnEnable), so an UnauthorizedAccessException or
            // a directory that is actually a file used to escape and abort the WHOLE overlay — the
            // key display simply never appeared, with the exception only in the Unity log. Custom
            // fonts are an optional extra; failing to find them must not take the keys down.
            // 目录操作全部加保护：本方法运行在覆盖层构建流程中（TryLoadResources →
            // EnableKeyViewer → OnEnable），权限异常或该路径其实是文件时异常会逃逸并中断**整个**
            // 覆盖层——按键显示根本不出现，异常只留在 Unity 日志里。自定义字体是可选附加项，
            // 找不到它绝不能把按键一起带倒。
            try
            {
                if (!Directory.Exists(customFontDir))
                {
                    Directory.CreateDirectory(customFontDir);
                    Loader.Log($"KeyViewer: Created CustomFont directory at {customFontDir}");
                    return;
                }

                string[] ttfFiles = Directory.GetFiles(customFontDir, "*.ttf", SearchOption.TopDirectoryOnly);
                string[] otfFiles = Directory.GetFiles(customFontDir, "*.otf", SearchOption.TopDirectoryOnly);
                fontFiles = new string[ttfFiles.Length + otfFiles.Length];
                Array.Copy(ttfFiles, fontFiles, ttfFiles.Length);
                Array.Copy(otfFiles, 0, fontFiles, ttfFiles.Length, otfFiles.Length);
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: could not read the CustomFont directory '{customFontDir}': {e.Message}");
                return;
            }

            if (fontFiles.Length == 0)
            {
                Loader.Log($"KeyViewer: No .ttf/.otf files found in CustomFont directory");
                return;
            }

            // Same reasoning as ScanGameFonts: every custom font bakes a full glyph atlas, and the
            // directory is user-controlled with no upper bound. Dropping a custom font folder with
            // hundreds of files in it used to bake hundreds of atlases at startup.
            // 与 ScanGameFonts 同理：每个自定义字体都会烘焙完整字形图集，而该目录由用户控制、
            // 没有上限。往里丢一个上百文件的目录，过去会在启动时烘焙上百张图集。
            const int MaxCustomFonts = 24;
            int skipped = 0;
            foreach (string fontPath in fontFiles)
            {
                try
                {
                    string fileName = Path.GetFileNameWithoutExtension(fontPath);
                    string entryName = $"Custom: {fileName}";

                    // Avoid duplicates by checking existing entries / 检查已有条目以避免重复
                    bool exists = false;
                    foreach (var e in fontList)
                    {
                        if (e.name.Equals(entryName, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }
                    }
                    if (exists)
                    {
                        Loader.Log($"KeyViewer: Custom font '{fileName}' already loaded, skipping");
                        continue;
                    }
                    if (customFontCount >= MaxCustomFonts)
                    {
                        skipped++;
                        continue;
                    }

                    Font font = new Font(fontPath);
                    if (font == null) { Loader.Error($"KeyViewer: could not create font from '{fontPath}'"); continue; }
                    TMP_FontAsset tmpFont;
                    try
                    {
                        tmpFont = TMP_FontAsset.CreateFontAsset(font);
                    }
                    catch (Exception e)
                    {
                        UnityEngine.Object.Destroy(font);
                        Loader.Error($"KeyViewer: Failed to create TMP_FontAsset from '{fontPath}': {e.Message}");
                        continue;
                    }
                    if (tmpFont != null)
                    {
                        fontList.Add(new FontEntry(entryName, tmpFont));
                        customFontCount++;
                        // DO NOT destroy the source Font here. TMP_FontAsset.CreateFontAsset builds a
                        // DYNAMIC atlas: it rasterises glyphs on demand, at runtime, through this very
                        // Font. Destroying it right after creation leaves a font asset that can never
                        // produce a single glyph, so every lookup misses and TMP walks
                        // fallbackFontAssetTable — which LinkFallbackFonts points at the CJK font. The
                        // symptom is that EVERY font you pick renders as the same CJK face, which is
                        // exactly what was reported. The sibling game-font path never destroyed it,
                        // which is why some fonts appeared to work and custom ones never did.
                        // Object.Destroy is deferred to end of frame, so the destruction did not even
                        // "work" immediately — it removed the source font on the first frame and
                        // every glyph after that resolved through the fallback.
                        // 这里**不要**销毁源 Font。TMP_FontAsset.CreateFontAsset 生成的是**动态**图集：
                        // 它在运行时通过这个 Font **按需**光栅化字形。创建后立刻销毁它，留下的就是一个
                        // 一个字形都产不出的字体资源，于是每次查找都落空，TMP 便顺着
                        // fallbackFontAssetTable 退到 LinkFallbackFonts 指向的 CJK 字体。症状正是
                        // **无论选哪个字体都显示成同一张 CJK 脸**——而这正是被报告的现象。孪生的游戏字体
                        // 路径从不销毁它，故部分字体看似可用、自定义字体则从不可用。
                        // Object.Destroy 是帧末的延迟销毁，所以它连「立刻生效」都做不到：它在第一帧
                        // 移除了源字体，此后每一个字形都走回退链。
                        RetainSourceFont(font);
                    }
                    else
                    {
                        UnityEngine.Object.Destroy(font);
                        Loader.Error($"KeyViewer: Failed to create TMP_FontAsset from '{fontPath}'");
                    }
                }
                catch (Exception e)
                {
                    Loader.Error($"KeyViewer: Failed to load custom font '{fontPath}': {e.Message}");
                }
            }
            if (skipped > 0)
                Loader.Warning($"KeyViewer: {skipped} custom font file(s) were not loaded — the list is capped at {MaxCustomFonts} to bound the glyph atlas VRAM");
        }
    }
}
