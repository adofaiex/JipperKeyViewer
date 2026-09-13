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

            int added = 0;
            foreach (var font in allFonts)
            {
                bool exists = false;
                foreach (var e in fontList)
                    if (e.sourceFontName == font.name) { exists = true; break; }
                if (exists) continue;

                var tmpFont = TMP_FontAsset.CreateFontAsset(font);
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
        }

        /// <summary>
        /// Extract bundled defaults, then load sprites from PNG files, fonts from OTF/TTF files, and custom fonts / 释放内嵌默认资源，然后从 PNG 加载精灵、从 OTF/TTF 加载字体以及自定义字体
        /// </summary>
        private bool TryLoadResources()
        {
            if (keyBackgroundSprite != null) return true;

            // Destroy the previous dynamically-created assets before dropping the references —
            // TMP_FontAssets carry atlas textures/materials; without this, every loader-level
            // toggle (UMM off→on) leaked the whole set.
            // 清空前先销毁旧的动态创建资产——TMP_FontAsset 持有图集纹理/材质;否则每次加载器级
            // 开关(UMM 关→开)都会泄漏一整套。
            foreach (var e in fontList)
                if (e.font != null) Destroy(e.font);
            fontList.Clear();
            // The style materials are copies of the just-destroyed font materials — drop them
            // before the next font load builds new ones. / 样式材质是刚被销毁的字体材质的副本——
            // 在下次字体加载构建新材质前先丢弃。
            ReleaseTextStyleMaterials();

            string modPath = Loader.ModPath;
            string assetsDir = Path.Combine(modPath, "assets");

            // Self-install: fresh installs get the embedded defaults on disk; existing files
            // (including user-replaced ones) are never touched. / 自安装:全新安装把内嵌默认
            // 资源落到磁盘;已存在的文件(含用户替换的)绝不动。
            EnsureBundledAssets(assetsDir);

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
                if (font != null)
                {
                    target = TMP_FontAsset.CreateFontAsset(font);
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
                if (font != null)
                {
                    var cjkFont = TMP_FontAsset.CreateFontAsset(font);
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
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: Failed to load CJK font '{fileName}': {e.Message}");
            }
        }

        /// <summary>
        /// Get the currently selected font from the font list / 从字体列表中获取当前选中的字体
        /// </summary>
        private TMP_FontAsset GetCurrentFont()
        {
            return fontList.Count > 0 ? fontList[Mathf.Clamp(Settings.Data.FontIndex, 0, fontList.Count - 1)].font : null;
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
                if (use != null) t.fontMaterial = use;
                t.fontStyle = style;
                t.fontSizeMax = Settings.Data.KeyFontSize;
            }
            bool hasPerKey = Settings.Data.EnablePerKeyTextSize;
            void ApplyPerKeyOverride(TMP_Text t, int pi)
            {
                if (t == null || !hasPerKey || pi < 0 || pi >= Settings.Data.PerKeyFontSize.Length) return;
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
                    // value: reset FIRST, then override — the old order let UpdateText's
                    // unconditional fontSizeMax write clobber the per-key size (the Kps/Total
                    // blocks below already had the correct order).
                    // value:先重置后覆盖——旧顺序会让 UpdateText 的无条件 fontSizeMax 写入
                    // 抹掉每键字号(下方 Kps/Total 段原本顺序就正确)。
                    UpdateText(Keys[i].value, countMat, node, true);
                    ApplyPerKeyOverride(Keys[i].value, pi);
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
                UpdateText(Kps.value, countMat, kpsNode, true);
                ApplyPerKeyOverride(Kps.value, kpsPi);
            }
            if (Total != null)
            {
                Settings.FmNode totalNode = Total.CustomNode;
                UpdateText(Total.text, keyMat, totalNode, false);
                ApplyPerKeyOverride(Total.text, totalPi);
                UpdateText(Total.value, countMat, totalNode, true);
                ApplyPerKeyOverride(Total.value, totalPi);
            }
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
            textStyleMaterials[key] = mat;
            return mat;
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
        }

        static MemberInfo cachedMaterialMember;
        static bool cachedMaterialLogged;

        /// <summary>
        /// Get material from TMP_FontAsset via reflection (handles API differences across Unity/TMP versions) / 通过反射从 TMP_FontAsset 获取材质（处理不同 Unity/TMP 版本的 API 差异）
        /// </summary>
        static Material GetFontMaterial(TMP_FontAsset font)
        {
            if (cachedMaterialMember == null)
            {
                var t = font.GetType();
                const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
                cachedMaterialMember = (MemberInfo)t.GetProperty("material", flags) ?? t.GetField("material", flags);
            }

            Material result = null;
            if (cachedMaterialMember is PropertyInfo pi)
            {
                var val = pi.GetValue(font);
                if (val != null) result = (Material)val;
            }
            else if (cachedMaterialMember is FieldInfo fi)
            {
                var val = fi.GetValue(font);
                if (val != null) result = (Material)val;
            }

            if (!cachedMaterialLogged)
            {
                cachedMaterialLogged = true;
                string foundBy = cachedMaterialMember != null
                    ? $"{cachedMaterialMember.MemberType} \"{cachedMaterialMember.Name}\""
                    : "none";
                Loader.Log($"KeyViewer: Font material resolved via {foundBy}");
            }
            return result;
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
            string modPath = Loader.ModPath;
            string customFontDir = Path.Combine(modPath, "CustomFont");

            if (!Directory.Exists(customFontDir))
            {
                Directory.CreateDirectory(customFontDir);
                Loader.Log($"KeyViewer: Created CustomFont directory at {customFontDir}");
                return;
            }

            string[] ttfFiles = Directory.GetFiles(customFontDir, "*.ttf", SearchOption.TopDirectoryOnly);
            string[] otfFiles = Directory.GetFiles(customFontDir, "*.otf", SearchOption.TopDirectoryOnly);
            string[] fontFiles = new string[ttfFiles.Length + otfFiles.Length];
            Array.Copy(ttfFiles, fontFiles, ttfFiles.Length);
            Array.Copy(otfFiles, 0, fontFiles, ttfFiles.Length, otfFiles.Length);

            if (fontFiles.Length == 0)
            {
                Loader.Log($"KeyViewer: No .ttf/.otf files found in CustomFont directory");
                return;
            }

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

                    Font font = new Font(fontPath);
                    TMP_FontAsset tmpFont = TMP_FontAsset.CreateFontAsset(font);
                    if (tmpFont != null)
                    {
                        fontList.Add(new FontEntry(entryName, tmpFont));
                    }
                    else
                    {
                        Loader.Error($"KeyViewer: Failed to create TMP_FontAsset from '{fontPath}'");
                    }
                }
                catch (Exception e)
                {
                    Loader.Error($"KeyViewer: Failed to load custom font '{fontPath}': {e.Message}");
                }
            }
        }
    }
}
