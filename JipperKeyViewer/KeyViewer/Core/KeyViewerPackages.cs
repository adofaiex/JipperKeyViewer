// Profile packages (.jkv): a shareable archive of one profile plus the images/videos it uses
// / 配置包（.jkv）：单个配置 + 其用到的图片/视频的可分享归档
//
// Why a package at all: a profile JSON is useless on another machine. It references image and video
// files by name, and those live under CustomImages\ — so "here is my layout" meant "here is a JSON,
// plus go find these nine PNGs yourself". A .jkv is a ZIP holding the profile JSON, a small info
// entry, and every asset the profile references, with the node paths already rewritten to plain
// file names so they resolve against the importer's own CustomImages\.
// 为什么要配置包：配置 JSON 换台机器就没用了。它按文件名引用图片/视频，而那些文件在
// CustomImages\ 下——于是「这是我的布局」实际等于「这是一个 JSON，另外九个 PNG 你自己找」。
// .jkv 是一个 ZIP，内含配置 JSON、一小段信息条目、以及配置引用的全部资源，且节点路径已改写为
// 纯文件名，故在导入方的 CustomImages\ 下即可解析。
//
// Resolution: node coordinates live on a 1080-tall reference canvas whose WIDTH is derived from the
// aspect ratio (CanvasWidth = Screen.width * 1080 / Screen.height). A layout authored on an
// ultrawide monitor is therefore stretched when opened on 16:9. The package records the export-time
// canvas width and import rescales X / Width / rain-offset X by the ratio — Y and Height need no
// scaling because the 1080 reference height is fixed by construction.
// 分辨率：节点坐标位于 1080 高的参考画布上，其宽度由宽高比推导（CanvasWidth = Screen.width *
// 1080 / Screen.height）。故在超宽屏上做的布局拿到 16:9 上会被拉伸。包内记录导出时的画布宽度，
// 导入时按比例缩放 X / 宽度 / 雨滴偏移 X——Y 与高度无需缩放，因为 1080 参考高度是构造上固定的。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    /// <summary>Package metadata entry (package.json inside the archive) / 包元数据条目（归档内
    /// 的 package.json）</summary>
    [Serializable]
    public class KvPackageInfo
    {
        public string Name = "";
        public string ModVersion = "";
        /// <summary>Canvas width at export time (see the file header). 0 = unknown/legacy, in which
        /// case the import skips rescaling rather than guessing. / 导出时的画布宽度（见文件头）。
        /// 0 = 未知/旧包，此时导入跳过缩放而非猜测。</summary>
        public float CanvasWidth;
        public int ScreenWidth;
        public int ScreenHeight;
        public string ExportedUtc = "";
    }

    public partial class KeyViewer
    {
        private const string PackageExtension = ".jkv";
        private const string PackageSettingsEntry = "settings.json";
        private const string PackageInfoEntry = "package.json";
        private const string PackageAssetsPrefix = "assets/";
        private const string PackageFontsPrefix = "fonts/";

        /// <summary>Export the named profile (plus its assets) into ModPath\Packages\. / 把指定配置
        /// （连同资源）导出到 ModPath\Packages\。</summary>
        private string ExportProfilePackage(string profileName)
        {
            // Make sure the file on disk matches what is in memory before archiving it — exporting a
            // profile whose last edit is still in the debounced save buffer would ship a stale
            // layout. / 归档前先确保磁盘文件与内存一致——若最后一次编辑还在去抖保存缓冲里，
            // 导出出去的会是过期布局。
            if (string.Equals(profileName, Settings.CurrentProfile, StringComparison.Ordinal))
                SaveCurrentProfile();
            string sourcePath = GetProfilePath(profileName);
            if (!File.Exists(sourcePath)) return null;

            string json = File.ReadAllText(sourcePath);
            ProfileData data;
            try
            {
                data = JsonConvert.DeserializeObject<ProfileData>(json, ProfileData.ProfileSerializer);
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: cannot export profile '{profileName}': {e.Message}");
                return null;
            }
            if (data == null) return null;
            data.SyncArraysFromLists();

            // Rewrite asset references to bare file names and collect the files to bundle. Done on
            // the DESERIALIZED COPY, never on Settings.Data — mutating the live settings would point
            // the running overlay at nothing the moment the user exports. / 把资源引用改写为纯文件名
            // 并收集待打包文件。改动只作用于「反序列化出的副本」，绝不作用于 Settings.Data——改
            // 实时设置会让用户一按导出，正在运行的覆盖层就指向空。
            Dictionary<string, string> assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<FmNode> nodes = data.CustomNodes;
            if (nodes != null)
            {
                for (int i = 0; i < nodes.Count; i++)
                {
                    FmNode n = nodes[i];
                    if (n == null) continue;
                    n.ImagePath = CollectAsset(n.ImagePath, assets);
                    n.ImagePathPressed = CollectAsset(n.ImagePathPressed, assets);
                    n.VideoPath = CollectAsset(n.VideoPath, assets);
                }
            }

            // Bundle the profile's custom font under fonts/: a "Custom: name" selection exists
            // only in the exporter's own CustomFont\, so without the file the recipient silently
            // falls back to another typeface. Game fonts and the bundled CJK/MapleStory fonts
            // exist on every install and are never packaged. / 把配置的自定义字体打包进
            // fonts/：「Custom: 名称」的选择项只存在于导出者自己的 CustomFont\，缺文件时接收方
            // 会静默回退到别的字体。游戏内字体与内置 CJK/MapleStory 字体每个安装都有，绝不打包。
            string fontSelection = data.FontName;
            if (string.IsNullOrWhiteSpace(fontSelection)
                && Settings.Data != null && fontList != null
                && Settings.Data.FontIndex >= 0 && Settings.Data.FontIndex < fontList.Count)
                fontSelection = fontList[Settings.Data.FontIndex].name; // legacy profiles that never stored FontName / 从未写过 FontName 的旧配置
            string fontFile = FindCustomFontFile(fontSelection);

            string packagesDir = PackagesDir;
            Directory.CreateDirectory(packagesDir);
            string safe = SanitizeFileName(profileName);
            string outputPath = Path.Combine(packagesDir, safe + PackageExtension);
            // Overwriting an existing package is the intent (re-export after a tweak), but a
            // half-written archive from a failed export must not survive as a "valid" package, so
            // build into a temp file and move it into place only on success. / 覆盖已有包是预期行为
            //（改完再导出），但失败导出留下的半截归档不能当成「有效包」留存，故先写临时文件，成功
            // 后才移动到目标位置。
            string tempPath = outputPath + ".tmp";
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
                using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    WriteEntry(archive, PackageSettingsEntry, JsonConvert.SerializeObject(data, ProfileData.ProfileSerializer));
                    KvPackageInfo info = new KvPackageInfo
                    {
                        Name = profileName,
                        ModVersion = typeof(KeyViewer).Assembly.GetName().Version?.ToString() ?? "",
                        CanvasWidth = CanvasWidth,
                        ScreenWidth = Screen.width,
                        ScreenHeight = Screen.height,
                        ExportedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    };
                    WriteEntry(archive, PackageInfoEntry, JsonConvert.SerializeObject(info, Formatting.Indented));
                    foreach (KeyValuePair<string, string> pair in assets)
                        WriteFileEntry(archive, PackageAssetsPrefix + pair.Key, pair.Value);
                    if (fontFile != null)
                        WriteFileEntry(archive, PackageFontsPrefix + Path.GetFileName(fontFile), fontFile);
                }
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tempPath, outputPath);
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: package export failed: {e.Message}");
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                return null;
            }
            return outputPath;
        }

        /// <summary>Rewrite one asset reference to its bare file name and register the source file.
        /// Unresolvable paths pass through unchanged (a hand-typed absolute path the user intends to
        /// keep is not silently rewritten to a file name that will not resolve). / 把一处资源引用改写
        /// 为纯文件名并登记源文件。无法解析的路径原样保留（用户手写的绝对路径是有意保留的，不应被
        /// 静默改写成解析不到的文件名）。</summary>
        private static string CollectAsset(string path, Dictionary<string, string> assets)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            string resolved = ResolveCustomImagePath(path);
            if (resolved == null) return path;
            string fileName = Path.GetFileName(resolved);
            if (string.IsNullOrEmpty(fileName)) return path;
            // First writer wins: the same file referenced by several nodes must not be re-registered
            // with a different source path. / 先到者胜：同一文件被多个节点引用时不得用不同源路径
            // 重复登记。
            if (!assets.ContainsKey(fileName)) assets[fileName] = resolved;
            return fileName;
        }

        /// <summary>Resolve a "Custom: name" font selection to its file under CustomFont\ (either
        /// extension tried). Non-custom selections and missing files return null — the caller then
        /// bundles nothing. / 把「Custom: 名称」的字体选择解析为 CustomFont\ 下的文件（尝试两种
        /// 扩展名）。非自定义选择或缺文件返回 null——调用方即不打包字体。</summary>
        private static string FindCustomFontFile(string fontSelection)
        {
            if (string.IsNullOrWhiteSpace(fontSelection)) return null;
            const string customPrefix = "Custom: ";
            if (!fontSelection.StartsWith(customPrefix, StringComparison.Ordinal)) return null;
            string name = fontSelection.Substring(customPrefix.Length).Trim();
            if (string.IsNullOrEmpty(name)) return null;
            try
            {
                string dir = Path.Combine(Loader.ModPath, "CustomFont");
                string ttf = Path.Combine(dir, name + ".ttf");
                if (File.Exists(ttf)) return ttf;
                string otf = Path.Combine(dir, name + ".otf");
                if (File.Exists(otf)) return otf;
            }
            catch (Exception) { /* an unreadable mod path just means no font bundled / 模组路径不可读仅意味着不打包字体 */ }
            return null;
        }

        private static void WriteEntry(ZipArchive archive, string name, string content)
        {
            // Fully qualified: UnityEngine also declares a CompressionLevel enum, so the bare name is
            // ambiguous. / 全限定：UnityEngine 也声明了 CompressionLevel 枚举，裸名有歧义。
            ZipArchiveEntry entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using (Stream s = entry.Open())
            using (StreamWriter w = new StreamWriter(s, new System.Text.UTF8Encoding(false)))
                w.Write(content ?? "");
        }

        private static void WriteFileEntry(ZipArchive archive, string name, string sourcePath)
        {
            if (!File.Exists(sourcePath)) return;
            ZipArchiveEntry entry = archive.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using (Stream target = entry.Open())
            using (FileStream source = File.OpenRead(sourcePath))
                source.CopyTo(target);
        }

        /// <summary>Every package currently in ModPath\Packages\, newest first. / ModPath\Packages\ 下
        /// 现有的全部包，最新在前。</summary>
        private List<string> ListProfilePackages()
        {
            List<string> result = new List<string>();
            try
            {
                if (!Directory.Exists(PackagesDir)) return result;
                foreach (string f in Directory.GetFiles(PackagesDir, "*" + PackageExtension))
                    result.Add(Path.GetFileNameWithoutExtension(f));
                // Newest first: a freshly exported package is what the user almost always wants to
                // import next (round-tripping their own layout). / 最新在前：刚导出的包通常就是用户
                // 接下来要导入的那个（自己来回验证布局）。
                result.Sort((a, b) =>
                    File.GetLastWriteTimeUtc(Path.Combine(PackagesDir, b + PackageExtension))
                        .CompareTo(File.GetLastWriteTimeUtc(Path.Combine(PackagesDir, a + PackageExtension))));
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: cannot list packages: {e.Message}");
            }
            return result;
        }

        /// <summary>Import a package as a NEW profile (never overwriting the current one) and switch
        /// to it. Returns a user-facing message; the profile is only created when the whole archive
        /// validated. / 把包导入为一个「新配置」（绝不覆盖当前配置）并切换过去。返回面向用户的
        /// 消息；仅当整个归档通过校验时才创建配置。</summary>
        private bool ImportProfilePackage(string packageName, out string message)
        {
            message = null;
            string packagePath = Path.Combine(PackagesDir, packageName + PackageExtension);
            if (!File.Exists(packagePath))
            {
                message = I18n.Tr("pkg_err_missing");
                return false;
            }

            ProfileData imported;
            KvPackageInfo info;
            try
            {
                using (FileStream stream = File.OpenRead(packagePath))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    ZipArchiveEntry settingsEntry = archive.GetEntry(PackageSettingsEntry);
                    if (settingsEntry == null)
                    {
                        message = I18n.Tr("pkg_err_format");
                        return false;
                    }
                    string json;
                    using (Stream s = settingsEntry.Open())
                    using (StreamReader r = new StreamReader(s))
                        json = r.ReadToEnd();

                    imported = new ProfileData();
                    JsonConvert.PopulateObject(json, imported, ProfileData.ProfileSerializer);
                    imported.SyncArraysFromLists();

                    // Same sanity gate as LoadProfile: only a null or over-long Count cannot come
                    // from a complete write of any version, so anything else is accepted and
                    // resized. / 与 LoadProfile 同款健全性闸门：只有 null 或超长的 Count 不可能
                    // 出自任何版本的完整写入，其余一律接受并重定长度。
                    if (imported.Count == null || imported.Count.Length > MaxKeySlots)
                    {
                        message = I18n.Tr("pkg_err_format");
                        return false;
                    }
                    if (imported.Count.Length != MaxKeySlots)
                    {
                        int[] c = new int[MaxKeySlots];
                        Array.Copy(imported.Count, c, Math.Min(imported.Count.Length, MaxKeySlots));
                        imported.Count = c;
                    }

                    info = ReadPackageInfo(archive);
                    ExtractPackageAssets(archive);
                    ExtractPackageFonts(archive);
                }
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: package import failed: {e.Message}");
                message = string.Format(I18n.Tr("pkg_err_failed"), e.Message);
                return false;
            }

            AdaptPackageToResolution(imported, info);

            // Unique name: importing the same package twice must produce two profiles, not silently
            // replace the first. / 唯一名：同一个包导入两次必须产生两个配置，而不是静默替换第一个。
            string newName = MakeUniqueProfileName(string.IsNullOrWhiteSpace(info?.Name) ? packageName : info.Name);
            imported.SyncListsToArrays();
            try
            {
                Directory.CreateDirectory(ProfileDir);
                WriteAllTextSafe(GetProfilePath(newName), JsonConvert.SerializeObject(imported, ProfileData.ProfileSerializer));
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: package profile write failed: {e.Message}");
                message = string.Format(I18n.Tr("pkg_err_failed"), e.Message);
                return false;
            }

            SyncProfilesWithDisk();
            SwitchProfile(newName);
            message = string.Format(I18n.Tr("pkg_imported"), newName);
            return true;
        }

        private static KvPackageInfo ReadPackageInfo(ZipArchive archive)
        {
            try
            {
                ZipArchiveEntry entry = archive.GetEntry(PackageInfoEntry);
                if (entry == null) return null;
                using (Stream s = entry.Open())
                using (StreamReader r = new StreamReader(s))
                    return JsonConvert.DeserializeObject<KvPackageInfo>(r.ReadToEnd());
            }
            catch (Exception)
            {
                // A malformed info entry is not a reason to reject a package whose settings parsed —
                // the import just loses resolution adaptation. / 信息条目损坏不应让设置已解析成功的
                // 包被拒绝——导入只是失去分辨率适配。
                return null;
            }
        }

        /// <summary>Extract assets/ entries into CustomImages\. Existing files are NEVER overwritten:
        /// a package must not be able to replace the user's own image with one of the same name, and
        /// an already-present file is by definition the one the imported paths will resolve to. /
        /// 把 assets/ 条目解压到 CustomImages\。绝不覆盖已存在的文件：包不得用同名文件替换用户自己
        /// 的图片，且已存在的文件按定义就是导入后路径将解析到的那个。</summary>
        private static void ExtractPackageAssets(ZipArchive archive)
        {
            string assetsDir = Path.Combine(Loader.ModPath, "CustomImages");
            Directory.CreateDirectory(assetsDir);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string fullName = entry.FullName.Replace('\\', '/');
                if (!fullName.StartsWith(PackageAssetsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = fullName.Substring(PackageAssetsPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(relative)) continue;

                string target = Path.GetFullPath(Path.Combine(assetsDir, relative));
                // Zip-slip guard: a crafted entry name ("../../evil.png") must not escape the assets
                // folder. / 目录穿越防护：构造的条目名（"../../evil.png"）不得逃出资源目录。
                string rootFull = Path.GetFullPath(assetsDir) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    Loader.Warning($"KeyViewer: package asset path rejected: {entry.FullName}");
                    continue;
                }
                if (File.Exists(target)) continue;

                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(target, false);
            }
        }

        /// <summary>Extract fonts/ entries into CustomFont\. Same rules as the asset extraction:
        /// existing files are never overwritten (an already-present same-named font is by
        /// definition the local winner) and the zip-slip guard applies. Fonts are scanned once at
        /// init, so an imported font takes effect after the next game start. / 把 fonts/ 条目解压
        /// 到 CustomFont\。与资源解压同规则：绝不覆盖已存在文件（同名本地字体按定义优先），
        /// 目录穿越防护同样生效。字体仅在启动时扫描一次，导入的字体下次启动游戏后生效。</summary>
        private static void ExtractPackageFonts(ZipArchive archive)
        {
            string fontsDir = Path.Combine(Loader.ModPath, "CustomFont");
            Directory.CreateDirectory(fontsDir);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string fullName = entry.FullName.Replace('\\', '/');
                if (!fullName.StartsWith(PackageFontsPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                string relative = fullName.Substring(PackageFontsPrefix.Length).Replace('/', Path.DirectorySeparatorChar);
                if (string.IsNullOrWhiteSpace(relative)) continue;

                string target = Path.GetFullPath(Path.Combine(fontsDir, relative));
                string rootFull = Path.GetFullPath(fontsDir) + Path.DirectorySeparatorChar;
                if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    Loader.Warning($"KeyViewer: package font path rejected: {entry.FullName}");
                    continue;
                }
                if (File.Exists(target)) continue;

                string dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                entry.ExtractToFile(target, false);
            }
        }

        /// <summary>Rescale the imported layout from the export-time aspect ratio to the current one.
        /// / 把导入的布局从导出时的宽高比缩放到当前宽高比。</summary>
        private void AdaptPackageToResolution(ProfileData data, KvPackageInfo info)
        {
            if (data?.CustomNodes == null || info == null) return;
            if (info.CanvasWidth <= 0f || Mathf.Approximately(info.CanvasWidth, CanvasWidth)) return;
            float scale = CanvasWidth / info.CanvasWidth;
            if (float.IsNaN(scale) || float.IsInfinity(scale) || scale <= 0f) return;
            // Clamp: a corrupt CanvasWidth (say 1e-6) would otherwise fling every node off-canvas,
            // and a layout that far out of range has no correct interpretation to preserve. /
            // 钳制：损坏的 CanvasWidth（比如 1e-6）否则会把所有节点甩出画布，而差距如此之大的布局
            // 本就没有可保留的正确解释。
            scale = Mathf.Clamp(scale, 0.1f, 10f);
            for (int i = 0; i < data.CustomNodes.Count; i++)
            {
                FmNode n = data.CustomNodes[i];
                if (n == null) continue;
                // Only the horizontal axis is aspect-dependent; the reference HEIGHT is a fixed
                // 1080, so Y/Height must stay untouched or every layout would grow vertically. /
                // 只有水平轴与宽高比相关；参考「高度」固定为 1080，故 Y/高度必须保持不动，否则每个
                // 布局都会在竖直方向被放大。
                n.X *= scale;
                n.Width *= scale;
                n.RainOffsetX *= scale;
            }
        }

        /// <summary>A profile name that no existing profile uses / 一个现有配置都未使用的配置名</summary>
        private string MakeUniqueProfileName(string baseName)
        {
            string clean = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? "Package" : baseName.Trim());
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Settings.ProfileNames != null)
                foreach (string p in Settings.ProfileNames)
                    if (!string.IsNullOrWhiteSpace(p)) used.Add(SanitizeFileName(p));
            if (!used.Contains(clean)) return clean;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = clean + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                if (!used.Contains(candidate)) return candidate;
            }
            return clean + " (" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Directory holding exported packages / 导出包所在目录</summary>
        private static string PackagesDir
        {
            get
            {
                if (packagesDir == null)
                {
                    string modPath = Loader.ModPath;
                    packagesDir = Path.Combine(modPath ?? Application.persistentDataPath, "Packages");
                }
                return packagesDir;
            }
        }
        private static string packagesDir;
    }
}