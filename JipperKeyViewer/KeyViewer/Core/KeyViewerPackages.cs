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
using System.Text;
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
        // Import and export both run SYNCHRONOUSLY inside one IMGUI callback on the main thread:
        // read zip, decompress, write to disk, then rebuild the whole overlay. A 2 GB expanded
        // package is therefore minutes of hard freeze — the game shows "not responding", the
        // watchdog may kill it, and nothing can be cancelled or saved meanwhile. 512 MB is still
        // ~10x a realistic image/video layout and bounds the freeze to a few seconds. Export keeps
        // the same bound so a package can always be re-imported.
        // 导入与导出都在**一个** IMGUI 回调里于主线程同步执行：读 zip、解压、落盘、再重建整层
        // 覆盖层。因此 2 GB 展开体积意味着数分钟的硬冻结——游戏显示"未响应"，看门狗可能杀掉进程，
        // 期间无法取消也无法保存。512 MB 仍是一个真实图片/视频布局的约 10 倍，把冻结限制在数秒。
        // 导出用同一上限，保证导出的包总能被重新导入。
        private const long MaxPackageExpandedBytes = 512L * 1024L * 1024L;
        private const long MaxPackageEntryBytes = 512L * 1024L * 1024L;
        private const long MaxPackageSettingsBytes = 32L * 1024L * 1024L;
        private const int MaxPackageEntries = 4096;
        /// <summary>Node / layer-group caps for an imported document. Aligned with the DmNote
        /// importer's 4096. / 导入文档的节点/图层组上限，与 DmNote 导入器的 4096 对齐。</summary>
        private const int MaxPackageNodes = 4096;
        private const int MaxPackageGroups = 64;

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
                string modPath = Loader.ResolveModPath();
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
                // Count the bytes WE actually write, not the length the archive declares. The
                // expanded-size budget above is built from ZipArchiveEntry.Length, which comes from
                // the central directory — metadata the file itself supplies. An entry that declares
                // 1 byte and then inflates to hundreds of MB passes every check in ValidatePackage
                // and still gets written to disk, synchronously, on the main thread (a hard game
                // freeze), before moving into CustomImages\. KvImageLoader's 16 MB / 4096x4096
                // checks only apply at LOAD time and cannot save the write.
                // 统计**我们自己**写出的字节，而不是归档声明的长度。上面的展开体积预算由
                // ZipArchiveEntry.Length 累加而来，那来自中央目录——由文件本身提供的元数据。一个
                // 声明 1 字节、随后膨胀到数百 MB 的条目能通过 ValidatePackage 的每一项检查，
                // 仍会被同步写入磁盘（主线程硬冻结），再 move 进 CustomImages\。
                // KvImageLoader 的 16 MB / 4096x4096 只在**加载**时生效，救不了这次落盘。
                long written = 0;
                byte[] buffer = new byte[64 * 1024];
                using (Stream source = entry.Open())
                using (FileStream target = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    int read;
                    while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += read;
                        if (written > MaxPackageEntryBytes)
                            throw new InvalidDataException(
                                $"Package entry expands beyond the safety limit: {entry.FullName}");
                        target.Write(buffer, 0, read);
                    }
                }

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
                // Snapshot whether a .corrupt backup for this name already exists. A failed import
                // must not delete the user's own safety copy.
                // 记录该名字下是否已存在 .corrupt 备份。失败的导入绝不能删掉用户自己的安全副本。
                try { corruptBackupIsOurs = !File.Exists(profilePath + ".corrupt"); }
                catch (Exception) { corruptBackupIsOurs = true; }
            }

            /// <summary>Targets that already existed locally and were therefore NOT overwritten.
            /// "Local file wins" is the right call, but it used to be completely silent: importing a
            /// shared layout whose key.png differs from the receiver's produced a profile pointing at
            /// the OLD image, with a clean log and a clean message — the single most common real
            /// .jkv failure. / 本地已存在、因而**未被覆盖**的目标。"本地优先"是对的，但此前完全
            /// 静默：导入的共享布局与接收方同名图片内容不同时，新配置会指向**旧图**，日志与提示都
            /// 干净——这是 .jkv 最常见的实际故障。</summary>
            public readonly List<string> SkippedLocalFiles = new List<string>();

            public void CommitFiles()
            {
                for (int i = 0; i < stagedFiles.Count; i++)
                {
                    StagedFile file = stagedFiles[i];
                    // Local files win. Stage every entry anyway, so a local file that disappears
                    // before commit still has a valid staged fallback. / 本地同名文件优先；仍然先
                    // 暂存每个条目，若本地文件在提交前消失仍有有效备份。
                    if (File.Exists(file.FinalPath))
                    {
                        SkippedLocalFiles.Add(file.FinalPath);
                        continue;
                    }
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
                //
                // BUT only when THIS import created that backup. The unique-name allocator checks
                // File.Exists(<name>.json) alone, so an import can land on a name whose .corrupt is
                // the user's own earlier safety copy — deleting it unconditionally threw away the
                // only remaining copy of a file that was already in trouble.
                // 但前提是**本次导入**创建了该备份。唯一名分配只检查 File.Exists(<name>.json)，
                // 因此导入可能落在一个其 .corrupt 正是用户自己早先安全副本的名字上——无条件删除
                // 会毁掉那个已经处于麻烦中的文件的仅存副本。
                if (!string.IsNullOrEmpty(profilePath) && corruptBackupIsOurs)
                {
                    try
                    {
                        string corrupt = profilePath + ".corrupt";
                        if (File.Exists(corrupt)) File.Delete(corrupt);
                    }
                    catch { }
                }
                TryDeleteDirectory(stagingRoot);
            }

            /// <summary>True when no &lt;name&gt;.json.corrupt existed before this import used the
            /// name, so any such file afterwards must have been created by our own LoadProfile.
            /// / 本次导入占用该名字之前不存在 &lt;name&gt;.json.corrupt，因此之后出现的任何此类文件
            /// 都必然是我们自己的 LoadProfile 创建的。</summary>
            private bool corruptBackupIsOurs;

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

        /// <summary>Delete the shared &lt;mod&gt;\.jkv-staging\ parent once it is empty, and sweep
        /// GUID sub-folders left behind by a hard kill. Each failed import used to leave its GUID
        /// directory (up to the 2 GB expanded limit) on disk, and nothing ever collected it —
        /// the staging parent itself was never removed either, so an interrupted import leaked
        /// space permanently. / 在共享的 &lt;mod&gt;\.jkv-staging\ 变空时删除它，并清扫被强杀留下的
        /// GUID 子目录。每次失败导入都把自己的 GUID 目录（最多 2 GB 展开上限）留在磁盘上且无人
        /// 回收——staging 父目录本身也从不删除，因此一次中断的导入会永久泄漏空间。
        internal static void SweepStaleStaging()
        {
            try
            {
                string root = Path.Combine(Loader.ResolveModPath(), ".jkv-staging");
                if (!Directory.Exists(root)) return;
                foreach (string dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        TimeSpan age = DateTime.UtcNow - new DirectoryInfo(dir).LastWriteTimeUtc;
                        // 24h: a live import never lasts that long, and an interrupted one is
                        // worthless to the user either way. / 24 小时：一次活的导入不会持续那么久，
                        // 而中断的导入对用户本就毫无价值。
                        if (age > TimeSpan.FromHours(24)) TryDeleteStagingDir(dir);
                    }
                    catch (Exception e) { Loader.Warning($"KeyViewer: could not sweep the staging folder '{dir}': {e.Message}"); }
                }
                if (Directory.GetFileSystemEntries(root).Length == 0)
                {
                    try { Directory.Delete(root); }
                    catch (Exception e) { Loader.Warning($"KeyViewer: could not remove the staging folder: {e.Message}"); }
                }
            }
            catch (Exception e)
            {
                Loader.Warning($"KeyViewer: staging sweep failed: {e.Message}");
            }
        }

        private static void TryDeleteStagingDir(string path)
        {
            try { Directory.Delete(path, true); } catch { }
        }

        /// <summary>Export the named profile (plus its assets) into ModPath\Packages\. / 把指定配置
        /// （连同资源）导出到 ModPath\Packages\。</summary>
        private string ExportProfilePackage(string profileName)
        {
            // Make sure the file on disk matches what is in memory before archiving it — exporting a
            // profile whose last edit is still in the debounced save buffer would ship a stale
            // layout. / 归档前先确保磁盘文件与内存一致——若最后一次编辑还在去抖保存缓冲里，
            // 导出出去的会是过期布局。
            // OrdinalIgnoreCase: every other profile comparison is case-insensitive, and with a
            // plain Ordinal a differently-cased current profile skipped the flush and exported a
            // stale layout.
            // OrdinalIgnoreCase：其它所有配置名比较都不区分大小写，而用 Ordinal 时大小写不同的
            // 当前配置会跳过这次落盘、导出过期布局。
            try
            {
                if (string.Equals(profileName, Settings.CurrentProfile, StringComparison.OrdinalIgnoreCase))
                    SaveCurrentProfile();
            }
            catch (Exception e)
            {
                // A full disk / read-only folder here means the archive would ship a STALE layout —
                // worse than refusing. Previously the exception escaped into the IMGUI caller and
                // broke that frame's GUILayout stack with nothing visible on screen.
                // 此处磁盘满/只读意味着归档会导出**过期**布局——比拒绝更糟。此前异常逃逸进
                // IMGUI 调用方，打断该帧 GUILayout 栈且界面上毫无提示。
                Loader.Error($"KeyViewer: cannot save profile '{profileName}' before export: {e.Message}");
                lastSaveError = e.Message;
                return null;
            }
            string sourcePath = GetProfilePath(profileName);
            if (!File.Exists(sourcePath)) return null;

            string json;
            try
            {
                json = File.ReadAllText(sourcePath);
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: cannot read profile '{profileName}' for export: {e.Message}");
                return null;
            }
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
        /// Anything that cannot be bundled is BLANKED: a shareable .jkv must not carry a reference
        /// that leaks the exporter's local directory layout, nor silently point at a resource the
        /// recipient cannot possibly have. / 把一处资源引用改写为纯文件名并登记源文件。任何无法
        /// 打包的内容一律**置空**：可分享的 .jkv 既不该携带泄露导出方本机目录结构的引用，也不该
        /// 悄悄指向接收方根本不可能拥有的资源。</summary>
        private static string CollectAsset(string path, Dictionary<string, string> assets)
        {
            if (string.IsNullOrWhiteSpace(path)) return path ?? "";
            // An ABSOLUTE path means the user pointed the node at a file somewhere on their
            // machine. Keeping the path string was intentional (so the export still round-trips),
            // but two things are not: bundling the file's CONTENT would copy e.g.
            // C:\Users\<user>\Desktop\private.png into a shareable .jkv, and keeping the string
            // leaks the exporter's username and directory layout to every recipient. Blank it.
            // 绝对路径意味着用户把节点指向了机器上某个文件。保留路径字符串是有意的（导出仍可往返），
            // 但有两件事不是：打包文件**内容**会把 C:\Users\<用户>\Desktop\private.png 复制进可
            // 分享的 .jkv；保留字符串则把导出方的用户名与目录结构泄露给每个接收方。置空。
            if (Path.IsPathRooted(path))
            {
                Loader.Warning($"KeyViewer: export dropped the reference to '{path}' — absolute paths are neither bundled nor shared");
                return "";
            }
            string resolved = ResolveCustomImagePath(path);
            // An unresolvable relative path is written back VERBATIM into the package's
            // settings.json, leaking the local directory layout. Blank it instead; the receiving
            // side then shows the documented "image not found" placeholder.
            // 解析不到的相对路径会被**原样**写进包内 settings.json，泄露本机目录结构。改为置空，
            // 接收方会显示有文档说明的"图片未找到"占位。
            if (resolved == null)
            {
                Loader.Warning($"KeyViewer: export could not resolve '{path}' — the reference is dropped and no file is bundled");
                return "";
            }
            string fileName = Path.GetFileName(resolved);
            if (string.IsNullOrEmpty(fileName)) return "";
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
            // FontName is a plain JSON string inside the profile, and a profile can arrive from a
            // .jkv — so it is attacker-controlled. It is concatenated straight into a path here, so
            // "Custom: ..\..\..\Users\<user>\Documents\secret" would have made the EXPORT read that
            // file and copy it into the package. Keep the name to a bare file name.
            // FontName 是配置里的普通 JSON 字符串，而配置可以来自 .jkv——即攻击者可控。此处直接
            // 拼进路径，"Custom: ..\..\..\Users\<用户>\Documents\secret" 会让**导出**读出该文件
            // 并打进包里。限定为纯文件名。
            if (name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' }) >= 0
                || name.Contains(".."))
            {
                Loader.Warning($"KeyViewer: export skipped the font '{fontSelection}' — the name is not a plain file name");
                return null;
            }
            try
            {
                string dir = Path.Combine(Loader.ResolveModPath(), "CustomFont");
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
            // Previously a silent `return` when the file was gone. The export then SUCCEEDED with a
            // profile whose asset reference had already been rewritten to the bare file name — the
            // recipient gets a layout pointing at a resource that does not exist, and the exporter
            // is never told. It also made the entry/size limits disagree with what was written.
            // 此前文件消失时静默 `return`：导出照样**成功**，而资源引用已被改写成裸文件名——接收方
            // 拿到一份指向不存在资源的布局，导出方却毫不知情。还会让条目数/体积闸门与实际写入不一致。
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException($"Cannot bundle '{name}': '{sourcePath}' no longer exists", sourcePath);
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
                    // Root-object property, not a substring: a truncated settings.json that merely
                    // contains the literal "Count" somewhere (a node label, a path) passed this gate,
                    // and PopulateObject then left the CONSTRUCTOR defaults in place — a silently
                    // empty profile reported as a successful import. KeyViewer already has the
                    // correct helper, written for exactly this class of bug.
                    // 必须按根对象属性判定而非子串：被截断的 settings.json 只要某处（节点文字、
                    // 路径）含字面量 "Count" 就能通过该闸门，而 PopulateObject 随后留下**构造默认值**
                    // ——静默产出一份空白配置并报告"导入成功"。KeyViewer 已有为这类问题写的
                    // 正确实现。
                    if (!HasRootProperty(json, "Count"))
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
                    // Element caps, matching the DmNote importer. EnsureCustomNodes' limits are
                    // PER GROUP, so a package that simply declares hundreds of distinct GroupIds
                    // walked straight past them: the group lookup is a full list scan, giving
                    // O(nodes × groups) — billions of comparisons that hang the process — and the
                    // unbound-decoration loop has no cap at all, so it could create tens of
                    // thousands of GameObjects and textures.
                    // 元素数量上限，与 DmNote 导入器一致。EnsureCustomNodes 的限制是**按组**计数，
                    // 声明数百个不同 GroupId 的包可以完全绕过：组查找是全表扫描，构成
                    // O(节点 × 组)——十亿级比较足以卡死进程；而未绑定装饰节点循环根本没有上限，
                    // 可以创建数万个 GameObject 与贴图。
                    if (imported.CustomNodes != null && imported.CustomNodes.Count > MaxPackageNodes)
                        throw new InvalidDataException($"Package has {imported.CustomNodes.Count} nodes (limit {MaxPackageNodes})");
                    if (imported.LayerGroups != null && imported.LayerGroups.Count > MaxPackageGroups)
                        throw new InvalidDataException($"Package has {imported.LayerGroups.Count} layer groups (limit {MaxPackageGroups})");

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
                // Do NOT pre-stamp DataVersion. A package exported from a DORMANT v3 profile still
                // carries DataVersion=3 (ExportProfilePackage copies the file verbatim, unlike
                // SaveCurrentProfile which bumps it), and stamping it here made LoadProfile's
                // MigrateFootSlots / v5→v6 flip skip it — reintroducing the very bugs those
                // load-time repairs exist for: foot-key counters stuck on the old 20-base slots
                // (reading as zero) and a v5 profile's KPS/Total Y convention locked in wrong. The
                // bump belongs to SaveCurrentProfile, after the lazy repairs have run.
                // **不要**预先盖 DataVersion。从**休眠** v3 配置导出的包里仍是 DataVersion=3
                // （ExportProfilePackage 原样复制文件，不像 SaveCurrentProfile 会提升），
                // 在这里盖戳会让 LoadProfile 的 MigrateFootSlots / v5→v6 翻转跳过它——正好
                // 重新引入这些加载时修复要解决的 bug：脚键计数卡在旧的 20 基线槽位（读出来是 0）、
                // v5 配置的 KPS/Total Y 约定被锁死为错误值。提升属于 SaveCurrentProfile，
                // 且必须在惰性修复之后。
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
                if (transaction != null && transaction.SkippedLocalFiles.Count > 0)
                {
                    // Surface what "local file wins" cost the user, in the dialog itself.
                    foreach (string skipped in transaction.SkippedLocalFiles)
                        Loader.Warning($"KeyViewer: import kept the local '{skipped}' instead of the packaged one");
                    message += " " + string.Format(I18n.Tr("pkg_imported_skipped_local"),
                        transaction.SkippedLocalFiles.Count);
                }
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
                        // Swallowing this hid a real broken state: the rollback deletes the imported
                        // profile, so a failed meta write leaves settings.json naming a file that no
                        // longer exists — and the next launch takes the "Profile not found" path.
                        // Report it and raise the banner rather than leaving the chain silent.
                        // 吞掉它会掩盖一种真实的损坏状态：回滚删除了导入的配置，而 meta 写失败会让
                        // settings.json 指着一个已不存在的文件——下次启动就走「Profile not found」
                        // 分支。现上报并显示横幅，不再让这条链静默。
                        try { SaveMetaOnly(); }
                        catch (Exception metaError)
                        {
                            Loader.Error($"KeyViewer: could not restore the profile list after a failed import: {metaError.Message}");
                            lastSaveError = metaError.Message;
                        }
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
            // The exporter only ever produces FLAT file names. A nested path multiplies the
            // landing surface for no benefit and widens the reparse-point question below, so
            // refuse anything with a separator.
            // 导出器只会产生**扁平**文件名。嵌套路径不带来任何好处，却成倍放大落点面与下面的
            // 重解析点问题，因此拒绝任何含分隔符的路径。
            if (parts.Length != 1)
                throw new InvalidDataException($"Package entry must be a plain file name: {fullName}");
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part) || part == "." || part == ".." || part.IndexOf(':') >= 0 || part.IndexOf('\0') >= 0)
                    throw new InvalidDataException($"Invalid package entry path: {fullName}");
                if (IsWindowsReservedName(part))
                    // "NUL.txt" is not a file: File.Exists says TRUE for it on Windows, so the
                    // duplicate check passes and FileMode.CreateNew then "succeeds" against the
                    // null device — the entry silently vanishes from the import.
                    // 「NUL.txt」不是文件：Windows 上 File.Exists 对它返回 TRUE，于是重名检查通过，
                    // 而 FileMode.CreateNew 又对空设备「成功」——该条目从导入中静默消失。
                    throw new InvalidDataException($"Package entry uses a reserved device name: {part}");
                if (part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal))
                    // Windows silently strips trailing dots/spaces, so "key.png " and "key.png"
                    // collide on disk while the duplicate check sees them as distinct.
                    // Windows 会静默去掉结尾的点/空格，于是「key.png 」与「key.png」在磁盘上冲突，
                    // 而重名检查却认为它们不同。
                    throw new InvalidDataException($"Package entry has a trailing dot or space: {part}");
            }
            return parts[0];
        }

        /// <summary>Windows reserved device names, with or without an extension and ignoring case.
        /// / Windows 保留设备名，带或不带扩展名、不区分大小写。</summary>
        private static bool IsWindowsReservedName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            int dot = fileName.IndexOf('.');
            string stem = dot < 0 ? fileName : fileName.Substring(0, dot);
            switch (stem.ToUpperInvariant())
            {
                case "CON": case "PRN": case "AUX": case "NUL":
                case "COM1": case "COM2": case "COM3": case "COM4": case "COM5":
                case "COM6": case "COM7": case "COM8": case "COM9":
                case "LPT1": case "LPT2": case "LPT3": case "LPT4": case "LPT5":
                case "LPT6": case "LPT7": case "LPT8": case "LPT9":
                    return true;
                default:
                    return false;
            }
        }

        private static string GetSafePackageTargetPath(string root, string relative)
        {
            string rootFull = Path.GetFullPath(root);
            string target = Path.GetFullPath(Path.Combine(rootFull, relative));
            string rootPrefix = rootFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!target.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Package entry escapes its target directory: {relative}");

            // The prefix check above is LEXICAL — it cannot see that a folder already on disk is a
            // junction/symlink pointing somewhere else, so a pre-planted link under CustomImages\
            // would make every write land outside the mod folder. ZipArchive cannot create such a
            // link itself, but one that is already there defeats the check. / 上述前缀检查只是
            // **词法**的：它看不见磁盘上已存在的目录其实是 junction/符号链接，于是预先放置在
            // CustomImages\ 下的链接会让每次写入都落到 Mod 目录之外。ZipArchive 自身无法创建
            // 这种链接，但已经存在的就足以让检查失效。
            try
            {
                if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"Package target is a link: {relative}");
            }
            catch (IOException)
            {
                // Does not exist — nothing to check. / 不存在——无需检查。
            }
            catch (UnauthorizedAccessException) { /* unreadable: let the real write report it / 不可读：交给真正的写入去报错 */ }
            return target;
        }

        /// <summary>Read a text entry with a HARD byte ceiling that is enforced *while* reading.
        ///
        /// The previous version checked `entry.Length` (the central-directory value the package
        /// itself declares) and then called ReadToEnd(), only testing `text.Length` afterwards — too
        /// late, because the whole thing is already materialised. An entry declaring 1 byte and
        /// inflating to 2 GB therefore passed the check and took ~4 GB of managed heap (UTF-16)
        /// before anything noticed. The settings entry is read FIRST, before any asset is staged,
        /// so this was the cheapest way to kill the process.
        ///
        /// Note the old post-hoc comparison was also off by up to 2x: it compared a CHARACTER
        /// count against a BYTE limit.
        /// 读取文本条目，且在**读取过程中**强制字节上限。
        ///
        /// 旧版先检查 `entry.Length`（由包自身声明的中央目录值），再 `ReadToEnd()`，事后才测
        /// `text.Length`——为时已晚，因为整份内容此时已物化。一个声明 1 字节、膨胀出 2GB 的条目
        /// 因此能通过检查，并在任何人察觉之前占用约 4GB 托管堆（UTF-16）。settings 条目是**最先**
        /// 读取的（早于任何资源落盘），故这是最廉价的进程击杀路径。
        ///
        /// 注：旧的事后比较还最多差 2 倍——拿**字符**数去比**字节**上限。
        /// </summary>
        private static string ReadPackageEntryText(ZipArchiveEntry entry, long maxBytes)
        {
            if (entry == null) return null;
            if (entry.Length < 0 || entry.Length > maxBytes)
                throw new InvalidDataException("Package text entry exceeds the safety limit");
            byte[] buffer = new byte[64 * 1024];
            using (MemoryStream sink = new MemoryStream())
            using (Stream s = entry.Open())
            {
                long total = 0;
                int read;
                while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                        throw new InvalidDataException("Package text entry exceeds the safety limit");
                    sink.Write(buffer, 0, read);
                }
                return DecodePackageText(sink.ToArray());
            }
        }

        private static string DecodePackageText(byte[] bytes)
        {
            if (bytes.Length == 0) return string.Empty;
            // Match StreamReader's default: detect and strip a UTF-8/UTF-16 BOM, otherwise UTF-8.
            // 与 StreamReader 默认一致：识别并去掉 UTF-8/UTF-16 BOM，否则按 UTF-8 解码。
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            return Encoding.UTF8.GetString(bytes);
        }


        private static void StagePackageAssets(ZipArchive archive, PackageImportTransaction transaction)
        {
            string assetsDir = Path.Combine(Loader.ResolveModPath(), "CustomImages");
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
            string fontsDir = Path.Combine(Loader.ResolveModPath(), "CustomFont");
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
                    packagesDir = Path.Combine(Loader.ResolveModPath(), "Packages");
                }
                return packagesDir;
            }
        }
        private static string packagesDir;
    }
}