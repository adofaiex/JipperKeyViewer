// DM Note JSON preset import / DM Note JSON 预设导入
// The importer is intentionally independent from Quartz's GPL implementation: it reads the
// documented DmNote field vocabulary, maps it onto FreeMake FmNode data, and creates a NEW
// profile. Graph/knob/embedded-asset payloads are reported and skipped in this first phase.
// 导入器独立实现：只读取 DmNote 的字段词汇，映射到 FreeMake 的 FmNode，并始终创建新 Profile。
// 第一阶段会报告并跳过 graph/knob/嵌入资源，不复制 Quartz 的实现。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer
    {
        private const long MaxDmNotePresetBytes = 64L * 1024L * 1024L;
        private const string DmNotePresetDirectoryName = "DmNotePresets";
        /// <summary>Hard cap on imported elements. A 64 MB JSON can describe hundreds of thousands
        /// of nodes; each FmNode carries several float[4] arrays, so the parse would spike the Mono
        /// heap long before EnsureCustomNodes trims the list back to 112. / 导入元素数硬上限：64MB
        /// JSON 可能描述数十万个节点，每个节点还带多个颜色数组，会在裁剪前就把 Mono 堆打爆。</summary>
        private const int MaxDmNoteElements = 4096;

        /// <summary>Deduplicated warning collector: a preset with 10 000 unsupported panels used to
        /// append 10 000 identical strings, all of which were then joined into the UI message.
        /// 警告去重收集器：曾出现上万个相同警告字符串全部拼进提示文本。</summary>
        internal sealed class DmNoteWarnings
        {
            private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<string> ordered = new List<string>();
            public int Count => ordered.Count;
            public IEnumerable<string> Items => ordered;
            public void Add(string message)
            {
                if (string.IsNullOrEmpty(message) || !seen.Add(message)) return;
                ordered.Add(message);
            }
        }

        /// <summary>Parsed DmNote data before it is turned into a new profile. / 转为新 Profile 前的解析结果。</summary>
        internal sealed class DmNoteImportDocument
        {
            public readonly List<FmNode> Nodes = new List<FmNode>();
            public readonly DmNoteWarnings Warnings = new DmNoteWarnings();
            public int SeenElements;
            public string Tab = "default";
            public bool NoteEnabled = true;
            public float NoteSpeed;
            /// <summary>noteSettings.trackHeight — how far a note travels, in the preset's own
            /// units. It is the rain HEIGHT: every per-node noteHeight in the real preset is null,
            /// so nothing carried it and the imported layout silently used the global default
            /// instead. The preset's geometry (60px keys, dx/dy on the same grid) is in the same
            /// unit, so the mapping is 1:1. / noteSettings.trackHeight——音符下落距离，单位与预设
            /// 自身一致。真实预设里每个节点的 noteHeight 都是 null，没有任何东西携带它，于是导入后
            /// 静默改用全局默认值。预设几何（60px 键、同网格的 dx/dy）同单位，故按 1:1 映射。
            /// </summary>
            public float NoteTrackHeight;
            /// <summary>Height of the first key element, needed to place the track origin. /
            /// 首个按键元素的高度，用于定位轨道原点。</summary>
            public float FirstKeyHeight;
            /// <summary>Y of the topmost visible key — Quartz's topMostY, the track origin when a
            /// node sets noteAutoYCorrection. / 最上方可见按键的 Y，即 Quartz 的 topMostY。</summary>
            public float TopMostKeyY = float.MaxValue;
            public float FirstKeyY;



            /// <summary>Companion stylesheet, already parsed. Null when the preset has none and the
            /// loader found no sibling .css. / 伴随样式表（已解析）；预设没有且找不到同名 .css 时为 null。</summary>
            public DmNoteCssTheme CssTheme = new DmNoteCssTheme();
            public bool UseCustomCss = true;
            /// <summary>Which stylesheet answered: sibling / folder / embedded / ambiguous.
            /// 样式表来源：同名兄弟 / 目录唯一 / 内嵌 / 有歧义。</summary>
            public string CssSource = "embedded";
            /// <summary>The preset ships DM Note JS plugins. They cannot run here, but the user
            /// should be told rather than discovering a missing graph panel. / 预设带 JS 插件；无法
            /// 运行，但应告知用户，而不是让图像面板凭空消失。</summary>
            public bool HasJsPlugins;
        }

        /// <summary>Profile name free in BOTH the profile list and on disk. MakeUniqueProfileName
        /// only knows the in-memory list, so an orphan file left by an earlier failed import could
        /// still collide and abort the import with "profile target already exists".
        /// Profile 名需同时避开内存列表与磁盘文件：仅查列表时，早前失败导入留下的孤儿文件会撞名。
        /// </summary>
        private string MakeUniqueDmNoteProfileName(string baseName)
        {
            string clean = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? "DmNote" : baseName.Trim());
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Settings.ProfileNames != null)
                foreach (string p in Settings.ProfileNames)
                    if (!string.IsNullOrWhiteSpace(p)) used.Add(SanitizeFileName(p));
            if (!used.Contains(clean) && !File.Exists(GetProfilePath(clean))) return clean;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = clean + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                if (!used.Contains(candidate) && !File.Exists(GetProfilePath(candidate))) return candidate;
            }
            return clean + " (" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Files offered by the DM Note import list. / DM Note 导入列表中的文件。</summary>
        internal List<string> ListDmNotePresetFiles()
        {
            try
            {
                string dir = DmNotePresetDirectory;
                if (!Directory.Exists(dir)) return new List<string>();
                return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(f => new FileInfo(f).Length <= MaxDmNotePresetBytes)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception e)
            {
                Loader.Warning($"KeyViewer: cannot list DM Note presets: {e.Message}");
                return new List<string>();
            }
        }

        internal string DmNotePresetDirectory => Path.Combine(Loader.ResolveModPath(), DmNotePresetDirectoryName);

        /// <summary>Import one DmNote JSON file as a new FreeMake profile. The current profile is
        /// saved first and is never overwritten. / 将一个 DmNote JSON 导入为新的 FreeMake Profile，
        /// 当前 Profile 会先保存且绝不被覆盖。</summary>
        internal bool ImportDmNotePresetFile(string filePath, out string message)
        {
            message = "";
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    message = I18n.Tr("dmnote_import_missing");
                    return false;
                }
                long length = new FileInfo(filePath).Length;
                if (length <= 0 || length > MaxDmNotePresetBytes)
                {
                    message = I18n.Tr("dmnote_import_too_large");
                    return false;
                }

                string json = File.ReadAllText(filePath);
                if (!TryParseDmNoteJson(json, out DmNoteImportDocument document, out string parseError, filePath))
                {
                    message = I18n.Tr("dmnote_import_invalid") + " " + parseError;
                    return false;
                }
                if (document.Nodes.Count == 0)
                {
                    message = I18n.Tr("dmnote_import_empty");
                    return false;
                }

                // Report what the stylesheet did and did not carry, BEFORE the profile is written:
                // a theme that looks wrong after the fact is otherwise indistinguishable from the
                // import having failed. The glyphs are extracted here so the user has something to
                // convert even if they later decide to delete the profile.
                // 在写 Profile **之前**说明样式表带了什么、没带什么：事后主题看起来不对时，
                // 无法与「导入失败」区分。图标在此提取，即使用户随后删掉 Profile 也还有东西可转。
                if (document.UseCustomCss && document.CssTheme.HasAny)
                {
                    document.Warnings.Add(I18n.Tr("dmnote_css_applied") + " (" + document.CssSource + ")");
                    document.Warnings.Add(I18n.Tr("dmnote_css_unsupported"));
                    List<string> iconNotes = DmNoteCssMapping.ExtractIcons(document.CssTheme, filePath,
                        Path.Combine(Loader.ResolveModPath(), "CustomImages"));
                    foreach (string note in iconNotes) document.Warnings.Add(I18n.Tr("dmnote_css_icons"));
                    // The embedded kps.js plugin builds a graph panel DmNote renders and this mod
                    // cannot; say so rather than letting the panel simply be missing.
                    // 内嵌的 kps.js 插件会构建 DmNote 渲染、本 Mod 无法渲染的图像面板；
                    // 明确说明，而不是让面板凭空消失。
                    if (document.HasJsPlugins) document.Warnings.Add(I18n.Tr("dmnote_js_plugins"));
                }

                SaveCurrentProfile();
                ProfileData imported = JsonConvert.DeserializeObject<ProfileData>(
                    JsonConvert.SerializeObject(Settings.Data, ProfileData.ProfileSerializer),
                    ProfileData.ProfileSerializer);
                if (imported == null) throw new InvalidDataException("profile clone failed");

                string baseName = Path.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "DmNote";
                string profileName = MakeUniqueDmNoteProfileName(baseName);
                string groupId = "g1";
                imported.KeyViewerStyle = KeyviewerStyle.Custom;
                imported.EnableRainEffect = document.NoteEnabled;
                // Only trackHeight is mapped, and only because it is a LENGTH in the preset's own
                // unit (the same grid as the 60px keys and their dx/dy).
                // `noteSettings.speed` is deliberately NOT mapped. It is DM Note's own speed unit
                // and has no relation to RainSpeed, whose fall time is RainHeight * 300 / RainSpeed
                // in this mod — feeding 500 straight in made the imported rain several times faster
                // than the layout it was imported into. It was also written to ALL THREE rows at
                // once, which silently destroyed the per-row speed spread the profile was using.
                // A units-correct conversion needs DM Note's speed scale, which is not derivable
                // from the preset; the profile's own per-row speeds are left intact until it is.
                // 只映射 trackHeight，且仅因为它是预设自身单位下的**长度**（与 60px 键及其 dx/dy 同一
                // 网格）。`noteSettings.speed` **刻意不映射**：它是 DM Note 自己的速度单位，与
                // RainSpeed 毫无关系——后者在本 Mod 里的下落时间是 RainHeight * 300 / RainSpeed，
                // 把 500 直接灌进去会让导入后的雨滴比它被导入的那个布局快好几倍。而且它是**同时写入
                // 三排**的，会静默抹掉配置原本逐排不同的速度差。单位换算需要 DM Note 的速度标度，
                // 那无法从预设本身推出；在拿到之前保留配置自己的逐排速度。
                if (document.NoteTrackHeight > 0f)
                {
                    float h = Mathf.Clamp(document.NoteTrackHeight, 1f, 2000f);
                    imported.RainHeightRow1 = imported.RainHeightRow2 = imported.RainHeightRow3 = h;
                    imported.GhostRainHeightRow1 = imported.GhostRainHeightRow2 = imported.GhostRainHeightRow3 = h;
                    // A DM Note tab has ONE note track: one origin, one length. Jipper's three rain
                    // rows are a stagger *within* that track, so their start Y has to be derived
                    // from the imported length — inheriting the cloned profile's offsets imported
                    // the previous layout's row geometry wholesale. The clone came from a layout
                    // whose rows started -223/-169/-115, which are offsets tuned for a 200px track;
                    // replayed against a 150px one they stagger the rain outside the note's own
                    // travel, and the middle row is the most visible of the three.
                    // So spread the three rows evenly along the imported track: row 1 at the top,
                    // row 3 at the bottom, and the per-row RainOffsetY still applies on top.
                    // 故把三排沿导入轨道均匀分布：第 1 排在顶、第 3 排在底，逐排 RainOffsetY 仍叠加
                    // 在其上。
                    // Quartz resolves the track origin the same way this mod has to
                    // (modules/KeyViewer/KeyViewerOverlay.DmNoteParsing.cs):
                    //     spec.TrackBottomY = (spec.NoteAutoYCorrection ? topMostY : spec.Y) + spec.NoteOffsetY
                    // Every node in a real preset carries noteAutoYCorrection: true, so the origin
                    // is the TOPMOST key in the layout rather than each node's own Y — that is what
                    // "all the drops are produced above the first row" means. This mod places the
                    // drop's top at
                    //     keyCentre - keyH/2 + RainStartY + RainContainerHeight(275) + travel
                    // so for travel=0 to land on that line:
                    //     RainStartY = topMostY - keyY + keyH/2 - 275
                    // All three rows share it because there is one origin. A previous revision used
                    // a flat -275, exactly half a key height too low, which is the vertical offset
                    // the import kept being blamed for. 真实预设每个节点都带 noteAutoYCorrection:
                    // true，故原点是布局中最上方的按键——这正是「雨滴统一在第一排按键上方生成」，
                    // 需 RainStartY = topMostY - keyY + 键高/2 - 275，三排共用。
                    if (document.FirstKeyHeight > 0f)
                    {
                        float startY = document.TopMostKeyY - document.FirstKeyY
                            + document.FirstKeyHeight * 0.5f - 275f;
                        imported.RainStartYRow1 = imported.RainStartYRow2 = imported.RainStartYRow3 = startY;
                        imported.GhostRainStartYRow1 = startY;
                        imported.GhostRainStartYRow2 = startY;
                        imported.GhostRainStartYRow3 = startY;
                    }
                }
                // Fall TIME is what has to match, not either speed on its own. DM Note's own
                // implementation (src/hooks/overlay/useNoteSystem.ts) expires a note after
                //     (trackHeight * 1000) / speed        milliseconds
                // and this mod's rain travels RainHeight * 300 / RainSpeed. Writing DM Note's
                // `speed` straight into RainSpeed made the imported rain several times faster than
                // the layout it landed in — 500 against a fall time the preset spent 300ms on.
                // Equating the two with RainHeight == trackHeight gives RainSpeed = speed * 0.3.
                // 下落**时长**要一致，而不是任一边的速度直接抄。DM Note 自身实现
                // (useNoteSystem.ts) 在 (trackHeight * 1000) / speed 毫秒后回收音符，而本 Mod 的
                // 雨滴走 RainHeight * 300 / RainSpeed。此前把 DM Note 的 `speed` 直接写进 RainSpeed，
                // 使导入后的雨滴比落点布局快好几倍——500 对上预设原本 300ms 的下落。令
                // RainHeight == trackHeight 解得 RainSpeed = speed * 0.3。
                if (document.NoteSpeed > 0f)
                {
                    float s = Mathf.Clamp(document.NoteSpeed * 0.3f, 1f, 2000f);
                    imported.RainSpeedRow1 = imported.RainSpeedRow2 = imported.RainSpeedRow3 = s;
                    imported.GhostRainSpeedRow1 = imported.GhostRainSpeedRow2 = imported.GhostRainSpeedRow3 = s;
                }
                // A DmNote preset carries its own per-node counts, but the fixed-layout counter
                // arrays belong to the profile we cloned from. Carrying them over would import an
                // unrelated profile's Total/KPS history into the new layout. / 预设自带每节点计数，
                // 但固定布局的计数数组属于被克隆的配置——继承过来会把无关配置的统计带进新布局。
                imported.Count = new int[MaxKeySlots];
                imported.TotalCount = 0;
                imported.CustomNodes = document.Nodes;
                imported.CustomNodeNextId = document.Nodes.Max(n => n.Id) + 1;
                imported.LayerGroups = new List<FmLayerGroup>
                {
                    new FmLayerGroup { Id = groupId, Name = document.Tab, Visible = true }
                };
                foreach (FmNode node in imported.CustomNodes) node.GroupId = groupId;
                imported.LayerGroupNextId = 2;
                imported.DataVersion = Settings.Version;
                // These nodes are produced by `new FmNode { … }`, so the field initializers ran and
                // every defaulted field already holds its intended value — there is nothing for the
                // legacy FmNode-defaults repair to fix. Stamp it so the repair does not re-derive
                // them on every load.
                //
                // Today it is harmless either way, because the repair early-outs on
                // `LabelScale > 0 && CountScale > 0` and the object initializer never assigns those
                // two. That is a subtle coincidence: a future DmNote field mapping that set
                // LabelScale to 0 (say, from a `noteScale` alias) would make the repair fire and
                // silently reset ~25 fields this importer deliberately set — including the
                // TextOpacity it maps from `noteOpacity` and the CountInTotal it reads at :369.
                // Stating the intent beats relying on that.
                // 这些节点由 `new FmNode { … }` 产生，字段初始化器已跑过，每个带默认值的字段都已持有
                // 其应有值——旧 FmNode 默认值修复无事可修。盖上戳，使它不会在每次加载时重算一遍。
                //
                // 今天两种做法都无害，因为修复会在 `LabelScale > 0 && CountScale > 0` 处早退，而对象
                // 初始化器从不赋这两个值。但那是个微妙的巧合：将来某个 DmNote 字段映射若把
                // LabelScale 设成 0（例如来自 `noteScale` 别名），修复就会触发并静默重置本导入器
                // 刻意设置的约 25 个字段——包括从 `noteOpacity` 映射来的 TextOpacity 与 :369 读到的
                // CountInTotal。把意图写出来，胜过依赖那个巧合。
                imported.NodeDefaultsVersion = ProfileData.NodeTextDefaultsVersion;
                imported.SyncListsToArrays();

                string profilePath = GetProfilePath(profileName);
                string[] previousNames = Settings.ProfileNames == null
                    ? Array.Empty<string>() : (string[])Settings.ProfileNames.Clone();
                string previousCurrent = Settings.CurrentProfile;
                ProfileData previousData = Settings.Data;
                try
                {
                    if (File.Exists(profilePath)) throw new IOException("profile target already exists");
                    Directory.CreateDirectory(ProfileDir);
                    WriteAllTextSafe(profilePath, JsonConvert.SerializeObject(
                        imported, ProfileData.ProfileSerializer));
                    var names = new List<string>(Settings.ProfileNames ?? Array.Empty<string>()) { profileName };
                    Settings.ProfileNames = names.ToArray();
                    // SwitchProfile must still see the OLD current name: it saves the old profile
                    // before loading the new one. Setting CurrentProfile here would make it a no-op
                    // and leave the imported file unactivated.
                    if (!SwitchProfile(profileName)) throw new IOException("profile activation failed");
                }
                catch
                {
                    Settings.ProfileNames = previousNames;
                    Settings.CurrentProfile = previousCurrent;
                    Settings.Data = previousData;
                    try { if (File.Exists(profilePath)) File.Delete(profilePath); } catch { }
                    // SwitchProfile already persisted the NEW meta (CurrentProfile/ProfileNames)
                    // through SaveSettings. Without rewriting it here, settings.json would point at
                    // the profile file we just deleted and the next launch would silently fall back
                    // to a fresh empty profile. / SwitchProfile 已把新 Profile 名写进 meta；不回写
                    // 就会让 settings.json 指向刚删除的文件，下次启动静默回到空配置。
                    //
                    // Report it when it fails rather than swallowing: a failed meta rewrite here
                    // leaves the disk pointing at a DELETED file, so the next launch takes the
                    // "profile not found" path and the user's own profile is not loaded — with no
                    // log line and no banner. The .jkv importer reports the identical operation.
                    // 失败时上报而非静默吞掉：此处 meta 回写失败会让磁盘指向已**删除**的文件，下次
                    // 启动走「Profile not found」而用户原本在用的配置不会被加载——日志无、界面无
                    // 横幅。`.jkv` 导入器对完全相同的操作是会上报的。
                    try { SaveMetaOnly(); }
                    catch (Exception metaError)
                    {
                        lastSaveError = metaError.Message;
                        Loader.Error("KeyViewer: DM Note import rollback could not rewrite the "
                            + "profile metadata; the next launch may not find the active profile: "
                            + metaError);
                    }
                    throw;
                }

                string warningText = document.Warnings.Count == 0
                    ? "" : " " + string.Join("; ", document.Warnings.Items.Take(3));
                message = string.Format(I18n.Tr("dmnote_import_success"), document.Nodes.Count, profileName)
                    + warningText;
                return true;
            }
            catch (Exception e)
            {
                message = I18n.Tr("dmnote_import_failed") + " " + e.Message;
                Loader.Error("KeyViewer: DM Note import failed: " + e);
                return false;
            }
        }

        /// <summary>Parse the documented DmNote layout shape. Pure and side-effect free so the
        /// Harness can exercise it without Unity scene state. / 解析 DmNote 布局格式；纯函数便于测试。</summary>
        internal static bool TryParseDmNoteJson(string json, out DmNoteImportDocument document, out string error,
            string presetPath = null)
        {
            document = null;
            error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(json)) throw new FormatException("empty file");
                JObject root = JObject.Parse(json);
                JToken keyTable = root["keyPositions"] ?? root["positions"];
                JToken statTable = root["statPositions"];
                if (keyTable == null && statTable == null)
                    throw new FormatException("no keyPositions/statPositions object");

                DmNoteImportDocument result = new DmNoteImportDocument
                {
                    Tab = SelectDmNoteTab(root, keyTable, statTable),
                    NoteEnabled = ReadBool(root, "noteEffect", true),
                    NoteSpeed = root["noteSettings"] is JObject noteSettings
                        ? ReadNumber(noteSettings, noteSettings, "speed", 0f) : 0f,
                    NoteTrackHeight = root["noteSettings"] is JObject noteSettings2
                        ? ReadNumber(noteSettings2, noteSettings2, "trackHeight", 0f) : 0f
                };
                // The companion stylesheet. A shipped preset leaves every visual field null and
                // carries the whole theme here — including, for the shipped themes, the ONLY copy
                // of the clear/crown/fail/star glyphs, which live in ::before mask-image rules
                // selected by the very `className` the JSON leaves empty. Read it before the
                // elements so node construction can consult it.
                // 伴随样式表。随包预设把所有视觉字段留空、整个主题都在这里——对现成主题而言，
                // clear/crown/fail/star 图标的**唯一副本**也在此处，藏在由 JSON 留空的 className
                // 选中的 ::before mask-image 规则里。必须先于元素读取，供节点构造查阅。
                result.UseCustomCss = ReadBool(root, "useCustomCSS", true);
                result.HasJsPlugins = ReadBool(root, "useCustomJS", false);
                string css = DmNoteCss.LoadCompanionCss(presetPath,
                    root["customCSS"] is JObject customCss ? (string)customCss["content"] : null,
                    out string cssSource);
                result.CssSource = cssSource;
                result.CssTheme = DmNoteCss.Parse(css);
                foreach (string w in result.CssTheme.Warnings) result.Warnings.Add(w);
                JArray keyElements = SelectDmNoteTabArray(keyTable, result.Tab);
                JArray statElements = SelectDmNoteTabArray(statTable, result.Tab);
                if ((keyElements == null || keyElements.Count == 0)
                    && (statElements == null || statElements.Count == 0))
                    throw new FormatException("selected tab has no key or stat elements");
                // The tab is selected if EITHER table has it, but each table is then read strictly.
                // A preset whose two tables disagree on tab naming (a very plausible hand-edit, and
                // also what a partial export produces) therefore imports one table and silently
                // drops the other — the message then says "imported 2 nodes" while 7 key nodes are
                // simply gone. Every other unsupported feature in this importer emits a deduped
                // warning; this one reported success. Say so instead.
                // 标签只要**任一**表里有就被选中，但随后每张表都严格读取。故两张表对标签命名不一致
                // 的预设（手改很常见，部分导出也会这样）只会导入其中一张、静默丢掉另一张——提示却说
                // 「已导入 2 个节点」而 7 个按键节点整个消失。本导入器对其它一切不支持项都发去重
                // 警告，唯独这条报告成功。改为明确提示。
                if ((keyElements == null || keyElements.Count == 0) != (statElements == null || statElements.Count == 0))
                    result.Warnings.Add(I18n.Tr("dmnote_partial_tab"));

                JArray names = root["keys"] is JObject keyNames
                    ? keyNames[result.Tab] as JArray : null;
                int nextId = 1;
                AppendDmNoteElements(result, keyElements, names, false, ref nextId);
                // Stat panels normally carry `statType`. The parallel-array fallback is only
                // meaningful when the key-name array and the stat-element array are the SAME
                // length — that is what makes index i of one correspond to index i of the other.
                // The test used to be `names.Count >= statElements.Count`, which the ordinary
                // 7-keys-plus-2-panels preset satisfies, so the fallback fired routinely and:
                //   (a) a panel with no displayText displayed a KEY's name as its label;
                //   (b) a panel with no statType fell back to that key name in
                //       ResolveDmNoteStatType, matched neither "total" nor "kps", and was DROPPED
                //       with only a generic "unsupported statistic panel" warning.
                // Require exact equality; otherwise pass null and let the panels fall back to the
                // global KPS/Total labels, which is what a nameless panel should show.
                JArray statNames = names != null && statElements != null && names.Count == statElements.Count
                    ? names : null;
                if (names != null && statElements != null && statNames == null)
                    result.Warnings.Add(I18n.Tr("dmnote_stat_names_skipped"));
                AppendDmNoteElements(result, statElements, statNames, true, ref nextId);
                if (root["graphPositions"] != null)
                    result.Warnings.Add(I18n.Tr("dmnote_skip_graph"));
                if (root["knobPositions"] != null)
                    result.Warnings.Add(I18n.Tr("dmnote_skip_knob"));
                if (keyTable == root["positions"] && root["keyPositions"] == null)
                    result.Warnings.Add(I18n.Tr("dmnote_legacy_positions"));
                document = result;
                return true;
            }
            catch (Exception e) when (e is FormatException || e is JsonException || e is InvalidOperationException)
            {
                error = e.Message;
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static void AppendDmNoteElements(DmNoteImportDocument result, JArray elements, JArray names,
            bool stat, ref int nextId)
        {
            if (elements == null) return;
            for (int i = 0; i < elements.Count; i++)
            {
                // Refuse oversized documents DURING the walk: building hundreds of thousands of
                // FmNodes first and trimming afterwards is what blows the Mono heap. / 遍历中即拒绝
                // 超量文档：先构造再裁剪正是把托管堆打爆的原因。
                if (result.SeenElements >= MaxDmNoteElements)
                    throw new FormatException("too many elements (max " + MaxDmNoteElements + ")");
                result.SeenElements++;
                if (!(elements[i] is JObject raw)) continue;
                if (result.FirstKeyHeight <= 0f)
                {
                    JObject pos0 = raw["position"] as JObject ?? raw;
                    float h0 = ReadNumber(raw, pos0, "height", "h", "size", 0f);
                    if (h0 > 0f)
                    {
                        result.FirstKeyHeight = h0;
                        result.FirstKeyY = ReadNumber(raw, pos0, "dy", "y", "top", 0f);
                    }
                }
                if (!stat)
                {
                    JObject posY = raw["position"] as JObject ?? raw;
                    float y = ReadNumber(raw, posY, "dy", "y", "top", 0f);
                    if (y < result.TopMostKeyY) result.TopMostKeyY = y;
                }
                string name = names != null && i < names.Count ? names[i]?.ToString() : "";
                int nodeType = stat ? ResolveDmNoteStatType(raw, name, result.Warnings) : 0;
                if (stat && nodeType == 0) continue; // unsupported KPS avg/max panels
                FmNode node = BuildDmNoteNode(raw, name, nodeType, nextId++, result.Warnings);
                if (node != null)
                {
                    // CSS is the FALLBACK source: the JSON wins wherever it actually supplied a
                    // value, and the stylesheet only fills the nulls. That is why this runs after
                    // the JSON mapping rather than instead of it. / CSS 是**回退**来源：JSON 真正给出
                    // 值的地方以 JSON 为准，样式表只填补 null。故此处在 JSON 映射**之后**运行。
                    DmNoteCssMapping.Apply(node, result.CssTheme, ReadString(raw, raw, "className", ""),
                        result.UseCustomCss);
                    result.Nodes.Add(node);
                }
            }
        }

        private static FmNode BuildDmNoteNode(JObject raw, string keyName, int nodeType, int id,
            DmNoteWarnings warnings)
        {
            JObject position = raw["position"] as JObject ?? raw;
            // A present-but-unparseable geometry field ("width":"abc") used to fall back to the
            // default 60x60, producing a plausible-looking but WRONG node. Treat it like missing
            // geometry and skip the element instead. / 几何字段存在但无法解析（如 "width":"abc"）
            // 时曾回退成默认 60x60，生成看似正常但完全错位的节点；现在按无效几何跳过该元素。
            if (HasUnreadableNumber(raw, position, "dx", "x", "left", "dy", "y", "top",
                    "width", "w", "size", "height", "h"))
            {
                warnings.Add(I18n.Tr("dmnote_skip_geometry"));
                return null;
            }
            float x = ReadNumber(raw, position, "dx", "x", "left", float.NaN);
            float y = ReadNumber(raw, position, "dy", "y", "top", float.NaN);
            float w = ReadNumber(raw, position, "width", "w", "size", nodeType == 0 ? 60f : 100f);
            float h = ReadNumber(raw, position, "height", "h", "size", nodeType == 0 ? 60f : 30f);
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(w) || !IsFinite(h) || w <= 0f || h <= 0f)
            {
                warnings.Add(I18n.Tr("dmnote_skip_geometry"));
                return null;
            }

            FmNode node = new FmNode
            {
                Id = id,
                NodeType = nodeType,
                X = x,
                Y = y,
                Width = Mathf.Clamp(w, 1f, 2000f),
                Height = Mathf.Clamp(h, 1f, 2000f),
                Depth = Mathf.Max(0, Mathf.RoundToInt(ReadNumber(raw, position, "z", "zIndex", "layer", id))),
                Count = Mathf.Max(0, Mathf.RoundToInt(ReadNumber(raw, position, "count", 0f))),
                KeyBind = nodeType == 0 ? ResolveDmNoteKeyName(keyName) : "",
                GhostKey = ResolveDmNoteKeyName(ReadString(raw, position, "ghostKey", "")),
                CustomText = ReadString(raw, position, "displayText", keyName ?? ""),
                PressedText = nodeType == 0 ? ReadString(raw, position, "quartzPressedText", "") : "",
                CountInTotal = ReadBool(raw, position, "countInTotal", true),
                PerKeyKps = ReadBool(raw, position, "perKeyKps", false),
                Hidden = ReadBool(raw, position, "hidden", false),
                RainEnabled = ReadBool(raw, position, "noteEffectEnabled", true),
                RainRow = Mathf.Clamp((int)ReadNumber(raw, position, "rainRow", "row", 0f), 0, 2),
                RainAlignment = ResolveDmNoteAlign(ReadString(raw, position, "noteAlignment", "center")),
                RainOffsetX = Mathf.Clamp(ReadNumber(raw, position, "noteOffsetX", 0f), -500f, 500f),
                RainOffsetY = Mathf.Clamp(ReadNumber(raw, position, "noteOffsetY", 0f), -500f, 500f),
                CornerRadius = Mathf.Clamp(ReadNumber(raw, position, "borderRadius", 0f), 0f, 100f),
                BorderThickness = Mathf.Clamp(ReadNumber(raw, position, "borderWidth", 0f), 0f, 20f),
                FontSize = Mathf.Max(0f, ReadNumber(raw, position, "fontSize", nodeType == 0 ? 18f : 16f)),
                CountFontSize = Mathf.Max(0f, ReadNumber(raw, position, "counterFontSize", 16f)),
                CountShowWhilePressed = ReadBool(raw, "quartzCounterShowWhilePressed", true),
                // UseCustomCountFontStyle is set by ApplyDmNoteFontStyles below, which decides it
                // from whether the counter object actually carries style keys. Setting it true here
                // was a dead assignment overwritten four lines later — and had it survived, every
                // imported node would have claimed a count-font override it did not have.
                // 该标志由下方 ApplyDmNoteFontStyles 依据 counter 对象是否真的带样式键来决定。此处
                // 设 true 是死赋值，四行后即被覆盖——若真活下来，每个导入节点都会声称自己有一个
                // 并不存在的计数字体覆盖。
            };

            ApplyDmNoteColors(node, raw, position);
            ApplyDmNoteFontStyles(node, raw, position);
            ApplyDmNoteRain(node, raw, position, warnings);
            JObject counter = (raw["counter"] ?? position["counter"]) as JObject;
            if (counter?["fontSize"] != null)
                node.CountFontSize = Mathf.Clamp(ReadNumber(counter, counter, "fontSize", 16f), 0f, 200f);
            if (counter?["animation"] is JObject animation)
            {
                node.CounterAnimEnabled = ReadBool(animation, "enabled", true);
                node.CounterAnimScale = Mathf.Clamp(ReadNumber(animation, animation, "scale", 1.1f), 0.25f, 4f);
                node.CounterAnimDurationMs = Mathf.Clamp(ReadNumber(animation, animation, "durationMs", 300f), 1f, 5000f);
                if (animation["bezier"] is JArray bezier && bezier.Count == 4)
                {
                    float[] curve = new float[4];
                    for (int i = 0; i < 4; i++)
                    {
                        try { curve[i] = Mathf.Clamp01(bezier[i].Value<float>()); }
                        catch { curve[i] = i == 0 ? 0.25f : i == 1 ? 0.46f : i == 2 ? 0.45f : 0.94f; }
                    }
                    node.CounterAnimBezier = curve;
                }
            }
            float pressScale = ReadNumber(raw, position, "quartzPressScale", 1f);
            if (Math.Abs(pressScale - 1f) > 0.001f)
            {
                node.UseCustomPressAnim = true;
                node.PressAnimScale = Mathf.Clamp(pressScale, 0.25f, 2f);
            }
            if (ReadBool(raw, position, "quartzLabelEnabled", true) == false)
            {
                node.CustomText = "";
                node.PressedText = "";
            }
            if (ReadBool(raw, position, "noteGlowEnabled", false))
                warnings.Add(I18n.Tr("dmnote_skip_glow"));
            if (raw["inactiveImage"] != null || raw["activeImage"] != null
                || position["inactiveImage"] != null || position["activeImage"] != null)
                warnings.Add(I18n.Tr("dmnote_skip_images"));
            return node;
        }

        private static void ApplyDmNoteColors(FmNode node, JObject raw, JObject position)
        {
            bool hasBg = HasAnyToken(raw, position, "backgroundColor", "activeBackgroundColor");
            bool hasOutline = HasAnyToken(raw, position, "borderColor", "activeBorderColor");
            bool hasText = HasAnyToken(raw, position, "fontColor", "activeFontColor");
            Color bg = ReadColor(raw, position, "backgroundColor", Color.white);
            Color activeBg = ReadColor(raw, position, "activeBackgroundColor", bg);
            Color outline = ReadColor(raw, position, "borderColor", Color.white);
            Color activeOutline = ReadColor(raw, position, "activeBorderColor", outline);
            Color text = ReadColor(raw, position, "fontColor", Color.white);
            Color activeText = ReadColor(raw, position, "activeFontColor", text);
            if (ReadBool(raw, position, "idleTransparent", false)) bg.a = 0f;
            if (ReadBool(raw, position, "activeTransparent", false)) activeBg.a = 0f;
            node.UseCustomColor = hasBg || hasOutline || hasText;
            if (hasBg)
            {
                node.Bg = DmColorArray(bg);
                node.BgPressed = DmColorArray(activeBg);
            }
            if (hasOutline)
            {
                node.Outline = DmColorArray(outline);
                node.OutlinePressed = DmColorArray(activeOutline);
            }
            if (hasText)
            {
                node.TextColor = DmColorArray(text);
                node.TextColorPressed = DmColorArray(activeText);
                // Never write a 0 here. The DmNote text colour is frequently absent (`fontColor:
                // null` in most presets), and a colour object like {"type":"gradient", …} that
                // ReadColor cannot reduce falls back too — and a 0 alpha landing in TextOpacity
                // renders as a key with no label at all. See KeyViewer.EffectiveTextOpacity.
                // 这里**绝不**写入 0。DmNote 的文字颜色经常是缺的（多数预设里就是 `fontColor: null`），
                // 而 `{"type":"gradient", …}` 这类 ReadColor 无法归约的颜色对象同样会落到回退值；
                // alpha 0 一旦写进 TextOpacity，渲染出来就是一个完全没有标签的按键。
                // 见 KeyViewer.EffectiveTextOpacity。
                node.TextOpacity = Mathf.Clamp01(text.a) <= 0f ? 1f : text.a;
            }
            if (ReadBool(raw, "noteEffectEnabled", true)) node.RainEnabled = true;

            if (TryReadGradient(raw, position, "backgroundGradient", out Color top, out Color bottom))
            {
                node.UseBackgroundGradient = true;
                node.BackgroundGradientTop = DmColorArray(top);
                node.BackgroundGradientBottom = DmColorArray(bottom);
            }
            if (TryReadGradient(raw, position, "borderGradient", out top, out bottom))
            {
                node.UseOutlineGradient = true;
                node.OutlineGradientTop = DmColorArray(top);
                node.OutlineGradientBottom = DmColorArray(bottom);
            }
            if (TryReadGradient(raw, position, "fontGradient", out top, out bottom))
            {
                node.UseTextGradient = true;
                node.TextGradientLeft = DmColorArray(top);
                node.TextGradientRight = DmColorArray(bottom);
            }
            JObject counter = (raw["counter"] ?? position["counter"]) as JObject;
            JObject fill = counter?["fill"] as JObject;
            if (fill != null)
            {
                node.UseCustomCountTextColor = true;
                node.CountTextColor = DmColorArray(ReadColor(fill, fill, "idle", text));
                node.CountTextColorPressed = DmColorArray(ReadColor(fill, fill, "active", activeText));
            }
        }

        private static void ApplyDmNoteFontStyles(FmNode node, JObject raw, JObject position)
        {
            node.FontStyleFlags = ResolveDmNoteFontFlags(raw, position);
            JObject counter = (raw["counter"] ?? position["counter"]) as JObject;
            bool hasCounterStyle = counter != null && HasAnyToken(counter, counter,
                "fontWeight", "fontItalic", "fontUnderline", "fontStrikethrough", "fontLowercase",
                "fontUppercase", "fontSmallCaps");
            node.UseCustomCountFontStyle = hasCounterStyle;
            node.CountFontStyleFlags = hasCounterStyle ? ResolveDmNoteFontFlags(counter, null) : node.FontStyleFlags;
        }

        private static int ResolveDmNoteFontFlags(JObject p, JObject inner)
        {
            int flags = 0;
            float weight = ReadNumber(p, inner, "fontWeight", 400f);
            if (weight >= 600f) flags |= 1;
            if (ReadBool(p, inner, "fontItalic", false)) flags |= 2;
            if (ReadBool(p, inner, "fontUnderline", false)) flags |= 4;
            if (ReadBool(p, inner, "fontStrikethrough", false)) flags |= 64;
            if (ReadBool(p, inner, "fontLowercase", false)) flags |= 8;
            if (ReadBool(p, inner, "fontUppercase", false)) flags |= 16;
            if (ReadBool(p, inner, "fontSmallCaps", false)) flags |= 32;
            return flags;
        }

        private static void ApplyDmNoteRain(FmNode node, JObject raw, JObject position, DmNoteWarnings warnings)
        {
            float width = ReadNumber(raw, position, "noteWidth", "rainWidth", 0f);
            if (width > 0f) node.RainWidth = width;
            // "noteHeight"/"noteSpeed" are tried FIRST because every other note-scoped field in
            // this file uses the note* prefix (noteColor/noteOpacity/noteOffsetX,Y/noteBorder*/
            // noteAlignment/noteEnabled/noteGradient/noteRadius), and only these two had a bare
            // "rain" prefix. Read both spellings in note-first order: whichever DmNote actually
            // writes wins, and a preset using the other one still imports instead of silently
            // falling back to the global per-row value.
            // 先试 "noteHeight"/"noteSpeed"，因为本文件里其它所有 note 作用域字段都用 note* 前缀
            // （noteColor/noteOpacity/noteOffsetX,Y/noteBorder*/noteAlignment/noteEnabled/
            // noteGradient/noteRadius），只有这两个用了裸 "rain" 前缀。按 note 优先的顺序两种
            // 拼写都读：DmNote 实际写的那个胜出，用另一个的预设也能导入，而不是静默退回按排全局值。
            float height = ReadNumber(raw, position, "noteHeight", "rainHeight", 0f);
            if (height > 0f) node.RainHeight = height;
            float speed = ReadNumber(raw, position, "noteSpeed", "rainSpeed", 0f);
            if (speed > 0f) node.RainSpeed = speed;
            Color top = Color.white;
            Color bottom = Color.white;
            if (TryReadColor(raw, position, "noteColor", out Color rain))
            {
                top = rain;
                bottom = rain;
                node.UseCustomRainColor = true;
            }
            if (TryReadGradient(raw, position, "noteGradient", out Color gradientTop, out Color gradientBottom))
            {
                top = gradientTop;
                bottom = gradientBottom;
                node.UseCustomRainColor = true;
            }
            if (node.UseCustomRainColor)
            {
                float opacity = Mathf.Clamp01(ReadNumber(raw, position, "noteOpacity", 100f) / 100f);
                if (float.IsNaN(opacity)) opacity = 1f;
                top.a *= opacity;
                bottom.a *= opacity;
                node.RainColorTop = DmColorArray(top);
                node.RainColorBottom = DmColorArray(bottom);
            }
            if (raw["noteEnabled"] != null || position["noteEnabled"] != null)
                node.RainEnabled = ReadBool(raw, position, "noteEnabled", true);
            if (raw["quartzNoteShadow"] != null || position["quartzNoteShadow"] != null)
            {
                node.UseCustomRainShadow = true;
                node.RainShadowEnabled = ReadBool(raw, position, "quartzNoteShadow", false);
                node.RainShadowColor = DmColorArray(ReadColor(raw, position, "quartzNoteShadowColor", new Color(0f, 0f, 0f, 0.5f)));
                node.RainShadowOffsetX = Mathf.Clamp(ReadNumber(raw, position, "quartzNoteShadowX", 3f), -64f, 64f);
                node.RainShadowOffsetY = Mathf.Clamp(ReadNumber(raw, position, "quartzNoteShadowY", -3f), -64f, 64f);
            }
            // The border width lives under BOTH spellings. `borderWidth` is key-scoped and is what
            // the real preset uses ("borderWidth": null next to a populated borderColor), while
            // `noteBorderWidth` is the note-scoped variant. Only the note-scoped one was read, so
            // every imported key resolved to a 0px border and the imported colours were never
            // drawn — the key looked borderless even though borderColor and activeBorderColor were
            // both present.
            // 边框宽度在两种拼写下都存在：`borderWidth` 是按键作用域、也是真实预设使用的那个
            // （与有值的 borderColor 并列出现 "borderWidth": null），`noteBorderWidth` 才是雨滴
            // 作用域的变体。此前只读雨滴作用域那个，于是每个导入的按键边框宽度都是 0，那两个颜色
            // 永远画不出来——明明 borderColor 与 activeBorderColor 都有值。
            float borderWidth = Mathf.Clamp(
                ReadNumber(raw, position, "borderWidth", "noteBorderWidth", 0f), 0f, 20f);

            if (borderWidth > 0f || raw["noteBorderColor"] != null || position["noteBorderColor"] != null)
            {
                node.UseCustomRainOutline = true;
                node.RainOutlineEnabled = borderWidth > 0f;
                node.RainOutlineWidth = borderWidth;
                Color border = ReadColor(raw, position, "noteBorderColor", Color.white);
                float borderOpacity = Mathf.Clamp01(ReadNumber(raw, position, "noteBorderOpacity", 100f) / 100f);
                if (float.IsNaN(borderOpacity)) borderOpacity = 1f;
                border.a *= borderOpacity;
                node.RainOutlineColor = DmColorArray(border);
            }
            if (raw["noteBorderSide"] != null || position["noteBorderSide"] != null)
            {
                node.UseCustomRainBorderSides = true;
                node.RainBorderSides = ResolveDmNoteSide(ReadString(raw, position, "noteBorderSide", "all"));
            }
            float radius = ReadNumber(raw, position, "noteBorderRadius", 0f);
            if (radius <= 0f) radius = ReadNumber(raw, position, "noteRadius", 0f);
            if (raw["noteBorderRadius"] != null || raw["noteRadius"] != null
                || position["noteBorderRadius"] != null || position["noteRadius"] != null)
            {
                node.UseCustomRainCornerRadius = true;
                node.RainCornerRadius = Mathf.Clamp(radius, 0f, 20f);
            }
            if (raw["ghostNoteColor"] != null || position["ghostNoteColor"] != null)
                warnings.Add(I18n.Tr("dmnote_skip_ghost"));
        }

        /// <summary>Pick the tab to import. The returned name is ALWAYS a tab that really exists in
        /// the position tables (or "default" for a table-less document), so the element lookup and
        /// the `keys[tab]` name array can never drift onto different tabs. The earlier version fell
        /// back to the first array inside SelectDmNoteTabArray while still reading names for the
        /// requested tab, which silently bound every key to the wrong name. Also: `||` binds looser
        /// than `&&`, so a null selectedKeyType used to reach TabExists(stats, null) and throw
        /// ArgumentNullException out of the JObject indexer. / 选中的 tab 名必定真实存在，避免元素与
        /// 键名数组取自不同 tab；并修正 selectedKeyType 缺失时 JObject[null] 抛异常的优先级 bug。</summary>
        private static string SelectDmNoteTab(JObject root, JToken keys, JToken stats)
        {
            string selected = root["selectedKeyType"]?.ToString();
            if (!string.IsNullOrWhiteSpace(selected)
                && (TabExists(keys, selected) || TabExists(stats, selected)))
                return selected;
            string first = FirstTab(keys) ?? FirstTab(stats);
            return string.IsNullOrEmpty(first) ? "default" : first;
        }

        private static string FirstTab(JToken table)
            => table is JObject obj ? obj.Properties().Select(p => p.Name).FirstOrDefault() : null;

        private static bool TabExists(JToken table, string tab)
        {
            if (string.IsNullOrEmpty(tab)) return false;
            return table is JObject obj && obj[tab] is JArray;
        }

        /// <summary>Strict lookup: a missing tab yields null instead of silently borrowing another
        /// tab's elements (the caller treats null as "this table has nothing for the chosen tab").
        /// 严格按 tab 取数组；缺失时返回 null，绝不借用别的 tab 造成元素与键名错位。</summary>
        private static JArray SelectDmNoteTabArray(JToken table, string tab)
        {
            if (table is JArray direct) return direct;
            if (table is JObject obj && !string.IsNullOrEmpty(tab))
                return obj[tab] as JArray;
            return null;
        }

        /// <summary>Classify a stat panel. A panel with no statType and no type is a nameless panel,
        /// NOT a key name — the caller only passes a name here when the key-name array is a true
        /// parallel array of the stat elements, so a non-empty `name` really is a panel label and
        /// there is no way to tell KPS from Total by it. Guessing "key" and dropping the panel is the
        /// safe half; what matters is that the reason reaches the user instead of a generic
        /// "unsupported panel" line that points at the wrong cause.
        /// 判定统计面板的类型。没有 statType 也没有 type 的面板是**无名义面板**，不是键名——调用方
        /// 只在键名数组确实是统计元素的等长平行数组时才会传名字，故非空 `name` 确实是面板标签，
        /// 无法由它区分 KPS 与 Total。猜成「按键」并丢弃是安全的一半；关键是要让真实原因到达
        /// 用户，而不是一条指向错误原因的泛化「不支持的面板」提示。
        /// </summary>
        private static int ResolveDmNoteStatType(JObject raw, string name, DmNoteWarnings warnings)
        {
            JObject position = raw["position"] as JObject ?? raw;
            string type = ReadString(raw, position, "statType", ReadString(raw, position, "type", name)).ToLowerInvariant();
            if (type.Contains("total")) return 2;
            if (type.Contains("kps") && !type.Contains("avg") && !type.Contains("max")) return 1;
            warnings.Add(string.IsNullOrEmpty(type)
                ? I18n.Tr("dmnote_skip_stat_untyped")
                : I18n.Tr("dmnote_skip_stat"));
            return 0;
        }

        private static int ResolveDmNoteAlign(string value)
        {
            if (string.Equals(value, "left", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(value, "right", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;
        }

        private static string ResolveDmNoteKeyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            {
                KeyCode numericKey = ResolveDmNumericKey(numeric);
                if (numericKey != KeyCode.None) return numericKey.ToString();
                // A single digit is not a virtual-key code — it is DM Note's label for a MAIN row
                // digit, produced by keyboard_key_to_global(Digit1) and friends. It must be tried
                // BEFORE the virtual-key table, because "1" is 0x01, which matches no key there and
                // resolved to nothing: every main-row number key in an imported preset came out
                // unbound.  单个数字**不是**虚拟键码，而是 keyboard_key_to_global(Digit1) 等给出的
                // **主**键盘数字标签。它必须在虚拟键码表**之前**处理——因为 "1" 是 0x01，在那张表里
                // 不匹配任何键，于是解析为空：导入后所有主键盘数字键都没有绑定。
                if (numeric >= 0 && numeric <= 9)
                    return ((KeyCode)((int)KeyCode.Alpha0 + numeric)).ToString();
                return "";
            }
            string normalized = new string(value.Trim().Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (normalized.StartsWith("KEY", StringComparison.Ordinal) && normalized.Length > 3)
                normalized = normalized.Substring(3);
            if (normalized.StartsWith("DIGIT", StringComparison.Ordinal) && normalized.Length > 5)
                normalized = normalized.Substring(5);
            if (normalized.StartsWith("NUMPAD", StringComparison.Ordinal) && normalized.Length > 6)
            {
                string pad = normalized.Substring(6);
                if (pad.Length == 1 && pad[0] >= '0' && pad[0] <= '9')
                    return ((KeyCode)((int)KeyCode.Keypad0 + (pad[0] - '0'))).ToString();
                switch (pad)
                {
                    // Unity has a distinct KeypadEnter and KeypadDelete. DM Note's own labels are
                    // "NUMPAD RETURN" and "NUMPAD DELETE" (numpad_override_label, by scan code), so
                    // both previously fell into the next-best branch: the numpad ENTER triggered the
                    // MAIN Return, and NUMPAD DELETE was resolved to KeypadPeriod. On a layout that
                    // uses the numeric keypad for a whole piano column, both are unplayable keys —
                    // and unlike a plain digit there is no main-row equivalent the player would
                    // stumble onto by accident.
                    // Unity 有独立的 KeypadEnter 与 KeypadDelete。DM Note 自身的标签是
                    // 「NUMPAD RETURN」与「NUMPAD DELETE」（numpad_override_label 按扫描码给出），
                    // 此前两者都被降级到次优分支：小键盘回车触发**主**回车，NUMPAD DELETE 被解析成
                    // KeypadPeriod。在用整个小键盘排做一列琴键的布局上，这两个键完全没法按——而且与
                    // 普通数字不同，玩家不会「误触」到主键行的对应键而察觉。
                    case "ENTER": return KeyCode.KeypadEnter.ToString();
                    case "RETURN": return KeyCode.KeypadEnter.ToString();
                    // Unity's KeyCode has NO keypad delete: the numeric block's non-Enter key is
                    // KeypadPeriod, so that is the closest faithful target. What the previous
                    // revision got wrong here was NUMPAD ENTER, which triggered the MAIN Return.
                    // Unity 的 KeyCode **没有**小键盘 Delete：数字区除回车外的那个键是 KeypadPeriod，
                    // 故取它作为最贴近的映射。此前真正出错的是 NUMPAD ENTER——它触发的是**主**回车。
                    case "DELETE": case "DEL": return KeyCode.KeypadPeriod.ToString();
                    case "DECIMAL": case "PERIOD": case "DOT": return KeyCode.KeypadPeriod.ToString();
                    case "PLUS": case "ADD": return KeyCode.KeypadPlus.ToString();
                    case "MINUS": case "SUBTRACT": return KeyCode.KeypadMinus.ToString();
                    case "MULTIPLY": case "STAR": case "ASTERISK": return KeyCode.KeypadMultiply.ToString();
                    case "DIVIDE": case "SLASH": return KeyCode.KeypadDivide.ToString();
                    case "EQUALS": case "EQUAL": return KeyCode.KeypadEquals.ToString();
                }
            }
            if (Enum.TryParse(normalized, true, out KeyCode direct) && direct != KeyCode.None)
                return direct.ToString();
            switch (normalized)
            {
                case "ESC": return KeyCode.Escape.ToString();
                case "RETURN": case "ENTER": return KeyCode.Return.ToString();
                case "SPACE": return KeyCode.Space.ToString();
                case "BACK": return KeyCode.Backspace.ToString();
                case "DEL": return KeyCode.Delete.ToString();
                case "INS": return KeyCode.Insert.ToString();
                case "PGUP": return KeyCode.PageUp.ToString();
                case "PGDN": return KeyCode.PageDown.ToString();
                case "LEFT": return KeyCode.LeftArrow.ToString();
                case "RIGHT": return KeyCode.RightArrow.ToString();
                case "UP": return KeyCode.UpArrow.ToString();
                case "DOWN": return KeyCode.DownArrow.ToString();
                case "DOT": case "PERIOD": return KeyCode.Period.ToString();
                case "FORWARDSLASH": case "SLASH": return KeyCode.Slash.ToString();
                case "LCONTROL": case "LEFTCONTROL": case "LEFTCTRL": case "CTRL": case "CONTROL": case "LCTRL":
                    return KeyCode.LeftControl.ToString();
                case "RCONTROL": case "RIGHTCONTROL": case "RIGHTCTRL": case "RCTRL": case "HANJA":
                    return KeyCode.RightControl.ToString();
                case "LALT": case "LEFTALT": return KeyCode.LeftAlt.ToString();
                case "RALT": case "RIGHTALT": case "ALTGR": case "HANGUL": return KeyCode.RightAlt.ToString();
                case "PRINTSCREEN": case "PRTSC": case "PRTSCR": case "SYSREQ": return KeyCode.Print.ToString();
                case "CONTEXTMENU": return KeyCode.Menu.ToString();
                case "CAPSLOCK": return KeyCode.CapsLock.ToString();
                case "COMMA": return KeyCode.Comma.ToString();
                case "PLUS": return KeyCode.Plus.ToString();
                case "MINUS": return KeyCode.Minus.ToString();
                case "EQUAL": case "EQUALS": return KeyCode.Equals.ToString();
                case "SEMICOLON": return KeyCode.Semicolon.ToString();
                case "QUOTE": return KeyCode.Quote.ToString();
                case "BACKQUOTE": case "SECTION": return KeyCode.BackQuote.ToString();
                case "SQUAREBRACKETOPEN": case "OPENBRACKET": case "LBRACKET": return KeyCode.LeftBracket.ToString();
                case "SQUAREBRACKETCLOSE": case "CLOSEBRACKET": case "RBRACKET": return KeyCode.RightBracket.ToString();
                case "BACKSLASH": return KeyCode.Backslash.ToString();
                default: return "";
            }
        }

        /// <summary>Common Windows virtual-key numbers, used for DmNote's numeric labels. /
        /// 常见 Windows 虚拟键码，用于 DmNote 的数字标签。
        ///
        /// DmNote's own source decides what these mean (src-tauri/src/keyboard/labels.rs): "21" is
        /// Right Alt / the Hangul 한영 key (VK 0xA5 or 0x15), and a numeric label that matches no
        /// named key falls back to the raw vk_code in decimal. A previous revision of this file
        /// "corrected" those ids into keypad row/column guesses; the source shows that was wrong and
        /// the virtual-key reading is the right one. Bare "1"/"2" are the MAIN row digits — the
        /// numeric keypad labels are "NUMPAD 1" / "NUMPAD 2" and take the branch above.
        /// DmNote 源码 (labels.rs) 决定了这些数字的含义："21" 是右 Alt／韩文한영键（VK 0xA5 或
        /// 0x15）；无对应具名键的数字标签回退成十进制 vk_code。本文件此前某个版本把这些 id「修正」
        /// 成小键盘排/列的猜测——源码证明那是错的，虚拟键读法才是对的。裸的 "1"/"2" 是**主**键盘
        /// 数字行；小键盘的标签是「NUMPAD 1」/「NUMPAD 2」，走上面那个分支。
        /// </summary>
        private static KeyCode ResolveDmNumericKey(int value)
        {
            if (value >= 0x30 && value <= 0x39) return (KeyCode)((int)KeyCode.Alpha0 + value - 0x30);
            if (value >= 0x41 && value <= 0x5A) return (KeyCode)((int)KeyCode.A + value - 0x41);
            if (value >= 0x60 && value <= 0x69) return (KeyCode)((int)KeyCode.Keypad0 + value - 0x60);
            if (value >= 0x70 && value <= 0x7E) return (KeyCode)((int)KeyCode.F1 + value - 0x70);
            switch (value)
            {
                case 0x08: return KeyCode.Backspace;
                case 0x09: return KeyCode.Tab;
                case 0x0D: return KeyCode.Return;
                case 0x10: return KeyCode.LeftShift;
                case 0x11: return KeyCode.LeftControl;
                case 0x12: return KeyCode.LeftAlt;
                case 0x13: return KeyCode.Pause;
                case 0x15: case 0xA5: return KeyCode.RightAlt;
                case 0x19: case 0xA3: return KeyCode.RightControl;
                case 0x14: return KeyCode.CapsLock;
                case 0x1B: return KeyCode.Escape;
                case 0x20: return KeyCode.Space;
                case 0x21: return KeyCode.PageUp;
                case 0x22: return KeyCode.PageDown;
                case 0x23: return KeyCode.End;
                case 0x24: return KeyCode.Home;
                case 0x25: return KeyCode.LeftArrow;
                case 0x26: return KeyCode.UpArrow;
                case 0x27: return KeyCode.RightArrow;
                case 0x28: return KeyCode.DownArrow;
                case 0x2C: return KeyCode.Print;
                case 0x2D: return KeyCode.Insert;
                case 0x2E: return KeyCode.Delete;
                case 0x5B: return KeyCode.LeftWindows;
                case 0x5C: return KeyCode.RightWindows;
                case 0x5D: return KeyCode.Menu;
                case 0x6A: return KeyCode.KeypadMultiply;
                case 0x6B: return KeyCode.KeypadPlus;
                case 0x6D: return KeyCode.KeypadMinus;
                case 0x6E: return KeyCode.KeypadPeriod;
                case 0x6F: return KeyCode.KeypadDivide;
                case 0x90: return KeyCode.Numlock;
                case 0x91: return KeyCode.ScrollLock;
                case 0xA0: return KeyCode.LeftShift;
                case 0xA1: return KeyCode.RightShift;
                case 0xA2: return KeyCode.LeftControl;
                case 0xA4: return KeyCode.LeftAlt;
                case 0xBA: return KeyCode.Semicolon;
                case 0xBB: return KeyCode.Equals;
                case 0xBC: return KeyCode.Comma;
                case 0xBD: return KeyCode.Minus;
                case 0xBE: return KeyCode.Period;
                case 0xBF: return KeyCode.Slash;
                case 0xC0: return KeyCode.BackQuote;
                case 0xDB: return KeyCode.LeftBracket;
                case 0xDC: return KeyCode.Backslash;
                case 0xDD: return KeyCode.RightBracket;
                case 0xDE: return KeyCode.Quote;
                default: return KeyCode.None;
            }
        }

        private static float[] DmColorArray(Color c) => new[] { c.r, c.g, c.b, c.a };

        private static bool TryReadGradient(JObject p, string name, out Color top, out Color bottom)
            => TryReadGradient(p, null, name, out top, out bottom);

        private static bool TryReadGradient(JObject outer, JObject inner, string name, out Color top, out Color bottom)
        {
            top = bottom = Color.white;
            if (!((outer?[name] ?? inner?[name]) is JObject gradient)) return false;
            top = ReadColor(gradient, gradient, "top", Color.white);
            bottom = ReadColor(gradient, gradient, "bottom", top);
            return true;
        }

        private static Color ReadColor(JObject outer, JObject inner, string name, Color fallback)
        {
            JToken token = outer?[name] ?? inner?[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token is JObject obj)
            {
                if (obj["color"] != null) return ParseColorToken(obj["color"], fallback);
                if (obj["value"] != null) return ParseColorToken(obj["value"], fallback);
                return ReadColor(obj, obj, "fill", fallback);
            }
            return ParseColorToken(token, fallback);
        }

        private static bool TryReadColor(JObject p, string name, out Color color)
            => TryReadColor(p, null, name, out color);

        private static bool TryReadColor(JObject outer, JObject inner, string name, out Color color)
        {
            color = Color.white;
            JToken token = outer?[name] ?? inner?[name];
            if (token == null) return false;
            color = ParseColorToken(token, Color.white);
            return true;
        }

        private static Color ParseColorToken(JToken token, Color fallback)
        {
            if (token is JObject obj)
            {
                if (obj["color"] != null) return ParseColorToken(obj["color"], fallback);
                if (obj["value"] != null) return ParseColorToken(obj["value"], fallback);
                return fallback;
            }
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                float v = token.Value<float>();
                if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
                return new Color(Mathf.Clamp01(v), Mathf.Clamp01(v), Mathf.Clamp01(v), 1f);
            }
            string text = token.ToString().Trim();
            if (text.StartsWith("#", StringComparison.Ordinal)
                && TryParseHexColor(text, out Color html)) return html;
            if (text.StartsWith("rgba", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                string inner = text.Substring(text.IndexOf('(') + 1).TrimEnd(')');
                string[] parts = inner.Split(',');
                if (parts.Length >= 3
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                {
                    float a = parts.Length > 3 && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 1f;
                    // rgb()/rgba() components are 0-255 (or percentages); without clamping,
                    // rgba(300,0,0,2) produced an out-of-gamut colour that reached the mesh vertices.
                    // rgb()/rgba() 分量为 0-255，不钳制会让 rgba(300,0,0,2) 产生超范围颜色写进顶点色。
                    return new Color(Mathf.Clamp01(r / 255f), Mathf.Clamp01(g / 255f), Mathf.Clamp01(b / 255f),
                        Mathf.Clamp01(a));
                }
            }
            return fallback;
        }

        private static bool TryParseHexColor(string text, out Color color)
        {
            color = Color.white;
            string hex = text.TrimStart('#');
            if (hex.Length == 3 || hex.Length == 4)
            {
                string expanded = "";
                for (int i = 0; i < hex.Length; i++) expanded += hex[i].ToString() + hex[i];
                hex = expanded;
            }
            if (hex.Length != 6 && hex.Length != 8) return false;
            if (!int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r)) return false;
            if (!int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g)) return false;
            if (!int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b)) return false;
            int a = 255;
            if (hex.Length == 8
                && !int.TryParse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a)) return false;
            color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            return true;
        }

        /// <summary>Read the first present alias from outer/inner, else the last argument (default).
        /// The args are "alias, alias, ..., default" — EVERY alias must be probed, not just the even
        /// indexes: a step-of-two walk silently dropped `x`, `w`, `row` and `zIndex`, which are real
        /// DmNote field names. / 依次探测所有别名，最后一个参数才是默认值；早期实现按步长 2 遍历，
        /// 会静默丢掉 x / w / row / zIndex 这些 DmNote 原生字段名。</summary>
        private static float ReadNumber(JObject outer, JObject inner, params object[] namesAndDefault)
        {
            int last = namesAndDefault.Length - 1;
            for (int i = 0; i < last; i++)
            {
                string name = namesAndDefault[i] as string;
                if (string.IsNullOrEmpty(name)) continue;
                JToken token = outer?[name] ?? inner?[name];
                if (token == null || token.Type == JTokenType.Null) continue;
                try
                {
                    float value = token.Value<float>();
                    if (!float.IsNaN(value) && !float.IsInfinity(value)) return value;
                }
                catch { }
            }
            return last < 0 ? 0f : Convert.ToSingle(namesAndDefault[last], CultureInfo.InvariantCulture);
        }

        /// <summary>True when one of the named tokens EXISTS but cannot be read as a finite float.
        /// A malformed value must not silently fall back to a default. / 命名字段存在但无法读成有限
        /// 浮点数时返回 true：坏值不能悄悄回退成默认值。</summary>
        private static bool HasUnreadableNumber(JObject outer, JObject inner, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                JToken token = outer?[names[i]] ?? inner?[names[i]];
                if (token == null || token.Type == JTokenType.Null) continue;
                float value;
                try { value = token.Value<float>(); }
                catch { return true; }
                if (float.IsNaN(value) || float.IsInfinity(value)) return true;
            }
            return false;
        }

        private static bool ReadBool(JObject p, string name, bool fallback)
            => ReadBool(p, null, name, fallback);

        private static bool ReadBool(JObject outer, JObject inner, string name, bool fallback)
        {
            JToken token = outer?[name] ?? inner?[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<float>() != 0f;
            return bool.TryParse(token.ToString(), out bool parsed) ? parsed : fallback;
        }

        private static bool HasAnyToken(JObject outer, JObject inner, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
                if (outer?[names[i]] != null || inner?[names[i]] != null) return true;
            return false;
        }

        private static int ResolveDmNoteSide(string value)
        {
            if (string.Equals(value, "vertical", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(value, "horizontal", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }

        private static string ReadString(JObject p, string name, string fallback)
            => ReadString(p, null, name, fallback);

        private static string ReadString(JObject outer, JObject inner, string name, string fallback)
        {
            JToken token = outer?[name] ?? inner?[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToString();
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
