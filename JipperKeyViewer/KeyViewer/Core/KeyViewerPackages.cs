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

        // Import limits are deliberately checked before extraction. A .jkv is user-supplied ZIP
        // data, so an unbounded entry count/expanded size is both a disk-exhaustion and zip-bomb
        // risk. The limits are generous for image/video layouts but finite. / 导入前先检查大小：
        // .jkv 是用户提供的 ZIP，无限制的条目数/展开体积会造成磁盘耗尽和 zip bomb。限制对图片/
        // 视频布局较宽松，但不是无限。
        private const long MaxPackageFileBytes = 512L * 1024L * 1024L;
        private const long MaxPackageExpandedBytes = 2L * 1024L * 1024L * 1024L;
        private const long MaxPackageEntryBytes = 512L * 1024L * 1024L;
        private const long MaxPackageSettingsBytes = 32L * 1024L * 1024L;
        private const int MaxPackageEntries = 4096;

        private sealed class PackageImportTransaction
        {
            private sealed class StagedFile
            {
                public string StagedPath;
                public string FinalPath;
                public string FinalDirectory;
            }

            private readonly string stagingRoot;
            private readonly List<StagedFile> stagedFiles = new List<StagedFile>();
            private readonly HashSet<string> stagedTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> committedFiles = new List<string>();
            private readonly List<string> createdDirectories = new List<string>();
            private string profilePath;

            public PackageImportTransaction()
            {
                string modPath = Loader.ModPath;
                if (string.IsNullOrEmpty(modPath)) throw new InvalidOperationException("Mod path is unavailable");
                stagingRoot = Path.Combine(modPath, ".jkv-staging", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(stagingRoot);
            }

            public void Stage(ZipArchiveEntry entry, string category, string finalRoot, string prefix)
            {
                string relative = GetSafePackageRelativePath(entry.FullName, prefix);
                string finalPath = GetSafePackageTargetPath(finalRoot, relative);
                string targetKey = finalPath;
                if (!stagedTargets.Add(targetKey)) throw new InvalidDataException($"Duplicate package entry target: {entry.FullName}");

                string stagedPath = Path.Combine(stagingRoot, category, relative);
                string stagedDirectory = Path.GetDirectoryName(stagedPath);
                if (!string.IsNullOrEmpty(stagedDirectory)) Directory.CreateDirectory(stagedDirectory);
                using (Stream source = entry.Open())
                using (FileStream target = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(target);

                stagedFiles.Add(new StagedFile
                {
                    StagedPath = stagedPath,
                    FinalPath = finalPath,
                    FinalDirectory = Path.GetDirectoryName(finalPath)
                });
            }

            public void TrackProfile(string path)
            {
                if (File.Exists(path)) throw new IOException("Profile target already exists");
                profilePath = Path.GetFullPath(path);
            }

            public void CommitFiles()
            {
                for (int i = 0; i < stagedFiles.Count; i++)
                {
                    StagedFile file = stagedFiles[i];
                    // Local files win. Stage every entry anyway, so a local file that disappears
                    // before commit still has a valid staged fallback. / 本地同名文件优先；仍然先
                    // 暂存每个条目，若本地文件在提交前消失仍有有效备份。
                    if (File.Exists(file.FinalPath)) continue;
                    EnsureDirectory(file.FinalDirectory);
                    File.Move(file.StagedPath, file.FinalPath);
                    committedFiles.Add(file.FinalPath);
                }
            }

            public void Rollback()
            {
                for (int i = committedFiles.Count - 1; i >= 0; i--)
                {
                    try { if (File.Exists(committedFiles[i])) File.Delete(committedFiles[i]); } catch { }
                }
                for (int i = createdDirectories.Count - 1; i >= 0; i--)
                {
                    try
                    {
                        if (Directory.Exists(createdDirectories[i]) && Directory.GetFileSystemEntries(createdDirectories[i]).Length == 0)
                            Directory.Delete(createdDirectories[i]);
                    }
                    catch { }
                }
                try { if (!string.IsNullOrEmpty(profilePath) && File.Exists(profilePath)) File.Delete(profilePath); } catch { }
                // A failed SwitchProfile made LoadProfile back the target up as <name>.json.corrupt
                // before returning false. Rolling back only the .json left that orphan behind, so
                // every failed import littered the profile folder with a bogus backup.
                // 切换失败时 LoadProfile 会先把目标备份成 <name>.json.corrupt 再返回 false；
                // 回滚只删 .json 就会留下孤儿文件，每次失败导入都会在配置目录留下垃圾备份。
                try
                {
                    if (!string.IsNullOrEmpty(profilePath))
                    {
                        string corrupt = profilePath + ".corrupt";
                        if (File.Exists(corrupt)) File.Delete(corrupt);
                    }
                }
                catch { }
                TryDeleteDirectory(stagingRoot);
            }

            public void Dispose()
            {
                TryDeleteDirectory(stagingRoot);
            }

            private void EnsureDirectory(string directory)
            {
                if (string.IsNullOrEmpty(directory) || Directory.Exists(directory)) return;
                List<string> missing = new List<string>();
                string current = Path.GetFullPath(directory);
                while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
                {
                    missing.Add(current);
                    string parent = Path.GetDirectoryName(current);
                    if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                    current = parent;
                }
                Directory.CreateDirectory(directory);
                for (int i = missing.Count - 1; i >= 0; i--) createdDirectories.Add(missing[i]);
            }

            private static void TryDeleteDirectory(string path)
            {
                if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
                try { Directory.Delete(path, true); } catch { }
            }
        }

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
            try
            {
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
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: cannot collect package assets: {e.Message}");
                return null;
            }

            // Bundle the profile's custom font under fonts/: a "Custom: name" selection exists
            // only in the exporter's own CustomFont\, so without the file the recipient silently
            // falls back to another typeface. Game fonts and the bundled CJK/MapleStory fonts
            // exist on every install and are never packaged. / 把配置的自定义字体打包进
            // fonts/：「Custom: 名称」的选择项只存在于导出者自己的 CustomFont\，缺文件时接收方
            // 会静默回退到别的字体。游戏内字体与内置 CJK/MapleStory 字体每个安装都有，绝不打包。
            string fontSelection = data.FontName;
            if (string.IsNullOrWhiteSpace(fontSelection)
                && fontList != null
                && data.FontIndex >= 0 && data.FontIndex < fontList.Count)
                fontSelection = fontList[data.FontIndex].name; // legacy profiles that never stored FontName / 从未写过 FontName 的旧配置
            string fontFile = FindCustomFontFile(fontSelection);
            string settingsJson;
            string infoJson;
            try
            {
                settingsJson = JsonConvert.SerializeObject(data, ProfileData.ProfileSerializer);
                if (settingsJson.Length > MaxPackageSettingsBytes)
                    throw new InvalidDataException("Profile settings exceed the package size limit");
                KvPackageInfo info = new KvPackageInfo
                {
                    Name = profileName,
                    ModVersion = typeof(KeyViewer).Assembly.GetName().Version?.ToString() ?? "",
                    CanvasWidth = CanvasWidth,
                    ScreenWidth = Screen.width,
                    ScreenHeight = Screen.height,
                    ExportedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                };
                infoJson = JsonConvert.SerializeObject(info, Formatting.Indented);
                long expandedBytes = settingsJson.Length + infoJson.Length;
                int entryCount = 2 + assets.Count + (fontFile == null ? 0 : 1);
                if (entryCount > MaxPackageEntries) throw new InvalidDataException("Profile package has too many entries");
                foreach (string source in assets.Values)
                {
                    long length = new FileInfo(source).Length;
                    if (length > MaxPackageEntryBytes) throw new InvalidDataException($"Asset is too large to package: {source}");
                    if (length > MaxPackageExpandedBytes - expandedBytes) throw new InvalidDataException("Package expanded size exceeds the safety limit");
                    expandedBytes += length;
                }
                if (fontFile != null)
                {
                    long length = new FileInfo(fontFile).Length;
                    if (length > MaxPackageEntryBytes) throw new InvalidDataException($"Font is too large to package: {fontFile}");
                    if (length > MaxPackageExpandedBytes - expandedBytes) throw new InvalidDataException("Package expanded size exceeds the safety limit");
                }
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: package size validation failed: {e.Message}");
                return null;
            }

            string packagesDir = PackagesDir;
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
                Directory.CreateDirectory(packagesDir);
                if (File.Exists(tempPath)) File.Delete(tempPath);
                using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    WriteEntry(archive, PackageSettingsEntry, settingsJson);
                    WriteEntry(archive, PackageInfoEntry, infoJson);
                    foreach (KeyValuePair<string, string> pair in assets)
                        WriteFileEntry(archive, PackageAssetsPrefix + pair.Key, pair.Value);
                    if (fontFile != null)
                        WriteFileEntry(archive, PackageFontsPrefix + Path.GetFileName(fontFile), fontFile);
                }
                if (new FileInfo(tempPath).Length > MaxPackageFileBytes)
                    throw new InvalidDataException($"Package file exceeds {MaxPackageFileBytes} bytes");
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
            // The same file may be referenced through different relative paths; that is safe to
            // de-duplicate. Two DIFFERENT files with the same basename are not: exporting both
            // under one entry would make the imported profile point at the wrong resource.
            // 同一文件经不同相对路径引用可去重；不同文件同名则不能静默合并，否则接收方会引用错资源。
            if (assets.TryGetValue(fileName, out string existing))
            {
                string a = Path.GetFullPath(existing);
                string b = Path.GetFullPath(resolved);
                if (!string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Asset basename collision: {existing} and {resolved}");
                return fileName;
            }
            assets[fileName] = resolved;
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
        /// to it. Assets are staged and committed only after the archive/profile validates; a failed
        /// activation rolls back the new files and metadata. / 把包导入为新配置并切换。资源先暂存，
        /// 只有归档和 Profile 验证通过后才提交；激活失败会回滚新文件和元数据。</summary>
        private bool ImportProfilePackage(string packageName, out string message)
        {
            message = null;
            string packagePath = Path.Combine(PackagesDir, packageName + PackageExtension);
            if (!File.Exists(packagePath))
            {
                message = I18n.Tr("pkg_err_missing");
                return false;
            }

            string[] previousNames = Settings.ProfileNames == null
                ? Array.Empty<string>() : (string[])Settings.ProfileNames.Clone();
            string previousCurrent = Settings.CurrentProfile;
            bool metadataChanged = false;
            bool success = false;
            PackageImportTransaction transaction = null;
            try
            {
                FileInfo packageInfo = new FileInfo(packagePath);
                if (packageInfo.Length > MaxPackageFileBytes)
                    throw new InvalidDataException($"Package file exceeds {MaxPackageFileBytes} bytes");

                ProfileData imported;
                KvPackageInfo info;
                using (FileStream stream = File.OpenRead(packagePath))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    ValidatePackageArchive(archive);
                    ZipArchiveEntry settingsEntry = archive.Entries.FirstOrDefault(e =>
                        string.Equals((e.FullName ?? "").Replace('\\', '/'), PackageSettingsEntry, StringComparison.OrdinalIgnoreCase));
                    if (settingsEntry == null) throw new InvalidDataException("Package settings.json is missing");
                    string json = ReadPackageEntryText(settingsEntry, MaxPackageSettingsBytes);
                    if (json.IndexOf("\"Count\"", StringComparison.OrdinalIgnoreCase) < 0)
                        throw new InvalidDataException("Package settings has no Count field");

                    imported = new ProfileData();
                    JsonConvert.PopulateObject(json, imported, ProfileData.ProfileSerializer);
                    imported.SyncArraysFromLists();
                    if (imported.Count == null || imported.Count.Length > MaxKeySlots)
                        throw new InvalidDataException("Package Count array is invalid");
                    if (imported.Count.Length != MaxKeySlots)
                    {
                        int[] c = new int[MaxKeySlots];
                        Array.Copy(imported.Count, c, Math.Min(imported.Count.Length, MaxKeySlots));
                        imported.Count = c;
                    }

                    info = ReadPackageInfo(archive);
                    transaction = new PackageImportTransaction();
                    StagePackageAssets(archive, transaction);
                    StagePackageFonts(archive, transaction);
                }

                AdaptPackageToResolution(imported, info);
                string baseName = string.IsNullOrWhiteSpace(info?.Name) ? packageName : info.Name;
                string newName = null;
                for (int attempt = 0; attempt < 1000 && newName == null; attempt++)
                {
                    string candidate = MakeUniqueProfileName(attempt == 0 ? baseName
                        : baseName + " (" + (attempt + 1).ToString(CultureInfo.InvariantCulture) + ")");
                    if (!File.Exists(GetProfilePath(candidate))) newName = candidate;
                }
                if (newName == null) throw new IOException("Could not allocate a unique profile name");

                imported.SyncListsToArrays();
                if (imported.DataVersion < Settings.Version) imported.DataVersion = Settings.Version;
                Directory.CreateDirectory(ProfileDir);
                string profilePath = GetProfilePath(newName);
                transaction.TrackProfile(profilePath);
                WriteAllTextSafe(profilePath, JsonConvert.SerializeObject(imported, ProfileData.ProfileSerializer));

                // The profile can only resolve assets after they are in their final directories.
                transaction.CommitFiles();
                metadataChanged = true;
                SyncProfilesWithDisk();
                if (!SwitchProfile(newName)) throw new IOException($"Imported profile '{newName}' could not be activated");

                success = true;
                message = string.Format(I18n.Tr("pkg_imported"), newName);
                return true;
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: package import failed: {e.Message}");
                message = string.Format(I18n.Tr("pkg_err_failed"), e.Message);
                return false;
            }
            finally
            {
                if (!success)
                {
                    if (metadataChanged)
                    {
                        Settings.ProfileNames = previousNames;
                        Settings.CurrentProfile = previousCurrent;
                        try { SaveMetaOnly(); } catch { }
                    }
                    transaction?.Rollback();
                }
                transaction?.Dispose();
            }
        }

        private static KvPackageInfo ReadPackageInfo(ZipArchive archive)
        {
            try
            {
                ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals((e.FullName ?? "").Replace('\\', '/'), PackageInfoEntry, StringComparison.OrdinalIgnoreCase));
                if (entry == null) return null;
                string json = ReadPackageEntryText(entry, 256L * 1024L);
                return JsonConvert.DeserializeObject<KvPackageInfo>(json);
            }
            catch (Exception)
            {
                // A malformed info entry is not a reason to reject a package whose settings parsed —
                // the import just loses resolution adaptation. / 信息条目损坏不应让设置已解析成功的
                // 包被拒绝——导入只是失去分辨率适配。
                return null;
            }
        }

        private static void ValidatePackageArchive(ZipArchive archive)
        {
            if (archive == null) throw new InvalidDataException("Package archive is null");
            if (archive.Entries.Count > MaxPackageEntries)
                throw new InvalidDataException($"Package contains too many entries ({archive.Entries.Count})");

            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            bool foundSettings = false;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = (entry.FullName ?? "").Replace('\\', '/');
                if (string.IsNullOrEmpty(name) || !names.Add(name))
                    throw new InvalidDataException($"Package contains an empty or duplicate entry name: {name}");

                long length;
                try { length = entry.Length; }
                catch (Exception e) { throw new InvalidDataException($"Cannot inspect package entry '{name}': {e.Message}", e); }
                if (length < 0 || length > MaxPackageEntryBytes)
                    throw new InvalidDataException($"Package entry is too large: {name}");
                if (length > MaxPackageExpandedBytes - expanded)
                    throw new InvalidDataException("Package expanded size exceeds the safety limit");
                expanded += length;

                if (string.Equals(name, PackageSettingsEntry, StringComparison.OrdinalIgnoreCase))
                {
                    if (length > MaxPackageSettingsBytes) throw new InvalidDataException("Package settings entry is too large");
                    foundSettings = true;
                }
                if (name.StartsWith(PackageAssetsPrefix, StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith(PackageFontsPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrEmpty(entry.Name))
                        GetSafePackageRelativePath(name, name.StartsWith(PackageAssetsPrefix, StringComparison.OrdinalIgnoreCase)
                            ? PackageAssetsPrefix : PackageFontsPrefix);
                }
            }
            if (!foundSettings) throw new InvalidDataException("Package settings.json is missing");
        }

        private static string GetSafePackageRelativePath(string fullName, string prefix)
        {
            string normalized = (fullName ?? "").Replace('\\', '/');
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package entry is outside its resource prefix: {fullName}");
            string relative = normalized.Substring(prefix.Length);
            if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException($"Invalid package entry path: {fullName}");

            string[] parts = relative.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part) || part == "." || part == ".." || part.IndexOf(':') >= 0 || part.IndexOf('\0') >= 0)
                    throw new InvalidDataException($"Invalid package entry path: {fullName}");
            }
            return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
        }

        private static string GetSafePackageTargetPath(string root, string relative)
        {
            string rootFull = Path.GetFullPath(root);
            string target = Path.GetFullPath(Path.Combine(rootFull, relative));
            string rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package entry escapes its target directory: {relative}");
            return target;
        }

        private static string ReadPackageEntryText(ZipArchiveEntry entry, long maxBytes)
        {
            if (entry == null) return null;
            if (entry.Length < 0 || entry.Length > maxBytes)
                throw new InvalidDataException("Package text entry exceeds the safety limit");
            using (Stream s = entry.Open())
            using (StreamReader r = new StreamReader(s))
            {
                string text = r.ReadToEnd();
                if (text.Length > maxBytes) throw new InvalidDataException("Package text entry exceeds the safety limit");
                return text;
            }
        }


        private static void StagePackageAssets(ZipArchive archive, PackageImportTransaction transaction)
        {
            string assetsDir = Path.Combine(Loader.ModPath, "CustomImages");
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith(PackageAssetsPrefix, StringComparison.OrdinalIgnoreCase))
                    transaction.Stage(entry, "assets", assetsDir, PackageAssetsPrefix);
            }
        }

        private static void StagePackageFonts(ZipArchive archive, PackageImportTransaction transaction)
        {
            string fontsDir = Path.Combine(Loader.ModPath, "CustomFont");
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string name = entry.FullName.Replace('\\', '/');
                if (name.StartsWith(PackageFontsPrefix, StringComparison.OrdinalIgnoreCase))
                    transaction.Stage(entry, "fonts", fontsDir, PackageFontsPrefix);
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