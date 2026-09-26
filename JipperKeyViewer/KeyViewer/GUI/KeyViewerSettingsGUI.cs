// Settings GUI: General / Layout / Display tab content / 设置界面:常规 / 布局 / 显示 标签页内容
// Profile, language, count reset, font, folder buttons, custom position, layout, display, full-keyboard KPS/Total layout / 配置、语言、计数重置、字体、文件夹、自定义位置、布局、显示、全键盘 KPS/Total 布局
// Shared small draw helpers (FloatSliderField, foldout buttons) also live here / 通用小绘制工具(FloatSliderField、折叠按钮)也放在此文件

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer : MonoBehaviour
    {
        // ===== Profile UI state / 配置选择器 UI 状态 =====
        bool profileExpanded;
        /// <summary>Id of the currently expanded easing list (null = none). / 当前展开的缓动列表
        /// id（null = 无）。</summary>
        private string easingListExpanded;
        private Vector2 easingScroll;
        bool profileIsRenaming;
        string profileRenameBuffer = "";
        string profileSaveAsBuffer = "";

        /// <summary>
        /// float.TryParse accepts the literals "NaN" / "Infinity" — a NaN would slip past every
        /// &lt;= 0 guard (NaN comparisons are always false) and poison meshes/text with NaN values,
        /// so parsed input must be finite. / float.TryParse 接受 "NaN"/"Infinity" 字面量——NaN 会
        /// 绕过所有 &lt;= 0 守卫（NaN 比较恒 false），把 NaN 值写进 mesh/文本，输入必须为有限值。
        /// </summary>
        private static bool IsFiniteFloat(float v)
            => !float.IsNaN(v) && !float.IsInfinity(v);

        /// <summary>Width options for the float rows. GUILayoutOption is a CLASS, so passing a
        /// constant width allocated one per field per IMGUI event — the rain page alone has 30 of
        /// these, i.e. 30 objects per event, every event, for the whole session. /
        /// 浮点行的宽度选项。GUILayoutOption 是**类**，故传常量宽度会每字段每 IMGUI 事件分配
        /// 一个——仅雨滴页就有 30 处，即每事件 30 个对象，整个会话持续不断。
        /// Instance fields, not static ones: a STATIC initializer runs when the partial class is
        /// first touched, which would drag GUIContent/GUILayoutOption into the type's static
        /// construction and require UnityEngine.IMGUIModule even for callers that never draw a
        /// settings page. / 实例字段而非静态：静态初始化器会在该分部类首次被触碰时运行，从而把
        /// GUIContent/GUILayoutOption 拖进类型的静态构造，让从不绘制设置页的调用方也需要
        /// UnityEngine.IMGUIModule。</summary>
        private readonly GUILayoutOption floatLabelWidth = GUILayout.Width(100f);
        private readonly GUILayoutOption floatSliderWidth = GUILayout.Width(120f);

        /// <summary>Pre-generated control names for the float rows. The name only has to be unique
        /// WITHIN one pass, and the pass draws the same fields in the same order every event — so
        /// the strings can be built once instead of concatenated per field per event. /
        /// 浮点行的预生成控件名。该名字只需在**单次** pass 内唯一，而每个 pass 都以相同顺序绘制
        /// 相同字段——故字符串只需构建一次，而非每字段每事件拼接一次。</summary>
        private static readonly string[] FloatCtrlNames = BuildFloatCtrlNames(256);

        private static string[] BuildFloatCtrlNames(int count)
        {
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = "fsf_" + i;
            return names;
        }

        /// <summary>Label GUIContent cache, keyed by the label string. A dictionary lookup replaces
        /// a `new GUIContent` per field per event. / 按标签字符串缓存的 Label GUIContent。一次
        /// 字典查找取代每字段每事件一个 new GUIContent。</summary>
        private readonly Dictionary<string, GUIContent> floatLabelCache = new Dictionary<string, GUIContent>(StringComparer.Ordinal);

        private GUIContent CachedFloatLabel(string label)
        {
            if (!floatLabelCache.TryGetValue(label, out GUIContent content))
            {
                content = new GUIContent(label);
                floatLabelCache[label] = content;
            }
            return content;
        }

        private float FloatSliderField(GUIContent label, float value, float min, float max, string format = "F2")
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, floatLabelWidth);
            float slid = GUILayout.HorizontalSlider(value, min, max, floatSliderWidth);
            // Buffered text field (TextInputField): typed intermediate states ("", "-", "0.") survive
            // until they parse, and the model only changes when the text actually differs from the
            // model echo. The old version re-fed value.ToString every event, wiping half-typed values
            // (negatives in the -10..10 offset fields were unreachable by typing) and re-clamping a
            // stored below-min value to min on every event while the tab was open.
            // 缓冲文本框（TextInputField）：输入中间态（""、"-"、"0."）保留到可解析为止；仅当文本
            // 与模型回显不同才写回模型。旧版每事件重灌 value.ToString，半输入的值被立刻冲掉
            // （-10..10 偏移字段无法用键盘输入负数），且存储值低于下限时打开标签页就被静默改写。
            //
            // The formatted echo is only rebuilt when the value or the format actually changed —
            // Layout and Repaint carry the same value, and formatting it twice per event per field
            // allocated a string for each. / 格式化回显只在值或格式**真的**变化时重建——Layout 与
            // Repaint 带着同一个值，每事件每字段格式化两次各自都要分配一个字符串。
            // The formatted echo is rebuilt only when THIS field's value or format actually changed,
            // cached per field rather than in one shared slot. See the cache fields' notes: a single
            // slot could never hit on a page drawing dozens of these.
            // 格式化回显只在**本字段**的值或格式真的变化时重建，按字段各自缓存而非共用一个槽。
            // 详见缓存字段处的说明：单槽在一页画几十个这种控件时永远不可能命中。
            int seq = ++sliderFieldSeq;
            string modelText;
            if (seq < FloatModelCacheSize
                && lastFloatModelFormatBySeq[seq] == format
                && floatModelEchoBySeq[seq] != null
                && lastFloatModelValueBySeq[seq] == slid)
            {
                modelText = floatModelEchoBySeq[seq];
            }
            else
            {
                modelText = slid.ToString(format);
                if (seq < FloatModelCacheSize)
                {
                    floatModelEchoBySeq[seq] = modelText;
                    lastFloatModelValueBySeq[seq] = slid;
                    lastFloatModelFormatBySeq[seq] = format;
                }
            }
            string ctrl = seq < FloatCtrlNames.Length ? FloatCtrlNames[seq] : "fsf_" + seq;
            if (slid != value) textInputBuffer.Remove(ctrl); // slider drag refreshes the field / 拖动滑块时刷新文本框
            string text = TextInputField(ctrl, modelText, FloatFieldWidth(modelText.Length));
            if (text != modelText && float.TryParse(text, out float parsed) && IsFiniteFloat(parsed))
            {
                // Fully open input (extending v1.6.2's open ceiling, per user decision): typed values
                // are stored as-is — negatives included, and values beyond either slider end; the
                // slider thumb just pegs. Consumers are degenerate-but-safe with such values (a
                // zero/negative fade duration merely stops the fade; non-positive drop rects are
                // skipped by the renderer).
                // 输入完全不钳制（用户决定，v1.6.2 放开上限的延伸）：键入的值原样存储——包括负数与
                // 超出滑块两端的值，滑块仅顶格。消费端对这类值退化但安全（零/负淡出时长只是不再
                // 淡出；非正的雨滴矩形被渲染器直接跳过）。
                slid = parsed;
            }
            GUILayout.EndHorizontal();
            return slid;
        }

        /// <summary>Per-field model-echo caches, indexed by the field's own pass sequence number.
        ///
        /// This used to be ONE shared slot (a StringBuilder plus a value and a format). With a page
        /// drawing 38 of these — the rain page does — consecutive fields almost always carry
        /// different values and formats, so the "only rebuild when the value or format changed"
        /// check missed on essentially every call and formatted twice per event per field anyway.
        /// The cache existed but could never hit, which is the same "one slot, N consumers" shape
        /// that made groupTotals wrong in the first place.
        ///
        /// Keying by the field's own sequence lets Layout and Repaint of the SAME field hit while
        /// each field keeps its own echo. Sharing an entry between two different fields is still
        /// safe: the echo is a pure function of (value, format) and both are compared, so a
        /// different field with the same value and format gets the same correct string.
        /// Sequence numbers past the table size simply skip the cache, exactly as the control-name
        /// table already does.
        /// 按**每个字段**缓存的模型回显，以该字段本趟的序号为下标。
        ///
        /// 此前是**一份共享槽**（StringBuilder + 值 + 格式）。一页画 38 个这种控件——雨滴页正是
        /// 如此——相邻字段几乎总是带着不同的值与格式，于是「只在值或格式变化时重建」这个检查
        /// **基本每次都不命中**，每事件每字段照样格式化两次。缓存存在却永远打不中，这正是当初让
        /// `groupTotals` 出错的同一个「一个槽位、N 个消费者」形状。
        ///
        /// 改为按字段自身序号索引后，同一字段的 Layout 与 Repaint 能命中，各字段各留一份回显。
        /// 两个不同字段共用同一条缓存**仍然安全**：回显是 (值, 格式) 的纯函数，两者都参与比较，
        /// 故另一个字段若带着相同的值与格式，拿到的就是同一个正确字符串。序号超出表长则直接跳过
        /// 缓存，与控件名表的既有做法一致。
        /// </summary>
        private const int FloatModelCacheSize = 256;
        private readonly float[] lastFloatModelValueBySeq = CreateNaNArray();
        private readonly string[] lastFloatModelFormatBySeq = new string[FloatModelCacheSize];
        private readonly string[] floatModelEchoBySeq = new string[FloatModelCacheSize];
        /// <summary>A slider value can never be NaN, so NaN doubles as "nothing cached yet" — the
        /// same sentinel the single-slot cache used. / 滑块值不可能是 NaN，故 NaN 兼作「尚未缓存」
        /// 标记，与此前单槽缓存用的是同一个哨兵。</summary>
        private static float[] CreateNaNArray()
        {
            float[] s = new float[FloatModelCacheSize];
            for (int i = 0; i < s.Length; i++) s[i] = float.NaN;
            return s;
        }

        private float FloatSliderField(string label, float value, float min, float max, string format = "F2")
            => FloatSliderField(CachedFloatLabel(label), value, min, max, format);

        private static bool DrawFoldoutButton(string label, bool expanded)
        {
            if (GUILayout.Button((expanded ? "◢ " : "▶ ") + label, GUI.skin.label, GUILayout.MinWidth(200)))
                return !expanded;
            return expanded;
        }

        private static int DrawFoldoutButton(string label, int expandedType, int expandValue = 0)
        {
            if (GUILayout.Button((expandedType >= 0 ? "◢ " : "▶ ") + label, GUI.skin.label, GUILayout.MinWidth(200)))
                return expandedType >= 0 ? -1 : expandValue;
            return expandedType;
        }

        private static void DrawFoldoutItemButton(string label, ref int state, int itemIndex)
        {
            if (GUILayout.Button((state == itemIndex ? "◢ " : "▶ ") + label, GUI.skin.label, GUILayout.MinWidth(200)))
                state = state == itemIndex ? -1 : itemIndex;
        }

        private void DrawProfileSection()
        {
            GUILayout.BeginVertical("box");
            DrawProfileFoldout();
            if (profileExpanded)
            {
                DrawProfileList();
                GUILayout.Space(3);
                GUILayout.BeginHorizontal();
                DrawProfileSaveAs();
                DrawProfileRenameButton();
                DrawProfileDeleteButton();
                GUILayout.EndHorizontal();
                GUILayout.Space(4);
                DrawPackageSection();
            }
            GUILayout.EndVertical();
            GUILayout.Space(5);
        }

        private void DrawProfileFoldout()
        {
            string label = I18n.Tr("profile") + ": " + Settings.CurrentProfile;
            if (GUILayout.Button((profileExpanded ? "◢ " : "▶ ") + label, GUILayout.MinWidth(200)))
            {
                profileExpanded = !profileExpanded;
                // Sync with disk once on open instead of on every OnGUI event while expanded — IMGUI
                // fires Layout+Repaint per frame, so the old call was a directory scan several times
                // per frame. The list buttons themselves keep ProfileNames in sync on every action.
                // 展开那一刻与磁盘同步一次，而不是展开期间每个 GUI 事件都同步——IMGUI 每帧触发
                // Layout+Repaint 多次，旧实现等于每帧数次目录扫描。列表按钮的各种操作自身会维护
                // ProfileNames 的同步。
                // 加保护：SyncProfilesWithDisk 内部有 CreateDirectory/GetFiles/SaveCurrentProfile
                // /SaveMetaOnly 且原本全无 try，异常从 GUILayout 回调抛出会让整个设置窗口布局错乱。
                if (profileExpanded) GuardedSave("the profile list", () => SyncProfilesWithDisk());
            }
        }

        private void DrawProfileList()
        {
            if (Settings.ProfileNames == null) return;
            for (int i = 0; i < Settings.ProfileNames.Length; i++)
            {
                string p = Settings.ProfileNames[i];
                bool selected = p == Settings.CurrentProfile;
                if (GUILayout.Button((selected ? "✓ " : "  ") + p, GUILayout.MinWidth(200)))
                {
                    if (!selected) SwitchProfile(p);
                    profileExpanded = false;
                }
            }
        }

        /// <summary>True when the typed text cannot become a profile name at all.
        ///
        /// Both profile-name inputs used to guard on `!string.IsNullOrEmpty(SanitizeFileName(typed))`,
        /// which could never be false: SanitizeFileName substitutes the "Unnamed" placeholder for
        /// exactly those inputs, so the guard was dead in both places. The symptom differed by call
        /// site — Save-As silently produced a profile called "Unnamed" (and every later attempt hit
        /// the duplicate check and returned with no message, so the button looked broken), while
        /// Rename took a REAL profile, renamed it to "Unnamed" and wrote that to disk. The predicate
        /// belongs on the typed text, not on the sanitizer's post-condition, and it now lives in one
        /// place so the two inputs cannot drift apart again.
        ///
        /// 输入完全无法成为配置名时为真（目前即：空白）。
        ///
        /// 两个配置名输入框此前都用 `!string.IsNullOrEmpty(SanitizeFileName(输入))` 守卫，而它**永远**
        /// 为真：SanitizeFileName 恰恰对空白回退成 "Unnamed" 占位，故该守卫在两处都是死代码。
        /// 症状按调用点而不同——另存为会静默产出一个叫 "Unnamed" 的配置（此后每次都被重名检查
        /// 挡下且**毫无提示**，按钮看起来像坏了）；而重命名会把一个**真实**的配置改名成 "Unnamed"
        /// 并落盘。判据应当落在输入文本上，而不是净化器的后置条件上；且现在只有一份实现，
        /// 两个输入框不会再各走各的。
        ///
        /// **刻意不**把「全是文件系统非法字符」算作不可用：Windows 把每个非法字符替换成 '_'，
        /// 故 "?<>|*" 会变成一个全由下划线构成的**合法**文件名，从来都是被接受的（这点未变）。
        /// 我最初把这一档也写进判据，是 Harness 测试当场指出错的——第二段判据同样永远不会命中。
        static bool ProfileNameUnusable(string typed) => string.IsNullOrWhiteSpace(typed);

        private void DrawProfileSaveAs()
        {
            profileSaveAsBuffer = GUILayout.TextField(profileSaveAsBuffer, GUILayout.Width(120));
            if (GUILayout.Button(I18n.Tr("save_as"), GUILayout.MinWidth(60)))
            {
                if (ProfileNameUnusable(profileSaveAsBuffer)) return;
                string name = SanitizeFileName(profileSaveAsBuffer.Trim());
                bool exists = File.Exists(GetProfilePath(name));
                if (Settings.ProfileNames != null)
                    // Case-insensitive: on NTFS "MyProfile"/"myprofile" are the same file / 大小写不敏感:
                    // NTFS 上 "MyProfile"/"myprofile" 是同一个文件
                    foreach (var p in Settings.ProfileNames)
                        if (string.Equals(SanitizeFileName(p), name, System.StringComparison.OrdinalIgnoreCase)) return;
                // Also refuse when the file is already on disk: ProfileNames only syncs when the
                // list is expanded, so a manually copied (or orphaned) Profiles\*.json is invisible
                // here and Save-As would overwrite it with the current profile's settings.
                // 磁盘上已存在时同样拒绝：ProfileNames 只在展开列表时同步，手动拷入（或孤儿）的
                // Profiles\*.json 在此不可见，另存为会直接用当前配置覆盖它。
                if (exists) return;
                // Snapshot before committing, and roll back on failure. Every other lifecycle entry
                // point already does this — DeleteProfile captures previousNames, RenameProfile
                // captures both previousNames and previousCurrent, and both importers capture
                // previousData too. Save-As was the only one that mutated the identity first and had
                // no way back, so with a read-only / full profile folder the session was left
                // editing a profile that had no file, the old profile silently reverted to whatever
                // it last held on disk, and the UI reported success (the buffer cleared and the
                // foldout closed regardless of whether the write happened).
                // 提交前先快照，失败时回滚。生命周期上其它每个入口都已这样做——DeleteProfile 快照
                // previousNames，RenameProfile 快照 previousNames 与 previousCurrent，两个导入器还
                // 快照 previousData。唯独另存为先改身份且无路可退，故目录只读/磁盘满时会话会停在
                // 「正在编辑一个并不存在的文件」的状态，旧配置静默回退到磁盘上最后的内容，而界面
                // 报告成功（缓冲区照清、折叠照关，完全不管写盘是否发生）。
                string[] previousNames = Settings.ProfileNames;
                string previousCurrent = Settings.CurrentProfile;
                var list = new List<string>(Settings.ProfileNames ?? new string[0]) { name };
                Settings.ProfileNames = list.ToArray();
                Settings.CurrentProfile = name;
                // Guarded: both writes below throw on a full disk / read-only profile folder, and
                // an exception escaping this GUILayout callback permanently corrupts the window's
                // layout. GuardedSave also shows the red banner the plain calls never reached.
                // 加保护：下面两次写盘在磁盘满/目录只读时会抛，而从 GUILayout 回调抛出会**永久**
                // 破坏窗口布局；GuardedSave 还会显示此前根本到不了的红色横幅。
                bool wroteNewProfile = false;
                GuardedSave("the new profile",
                    () => { SaveCurrentProfile(); SaveMetaOnly(); wroteNewProfile = true; },
                    () =>
                    {
                        Settings.ProfileNames = previousNames;
                        Settings.CurrentProfile = previousCurrent;
                    });
                // Only report the new profile as created when it actually reached the disk.
                // 只有真的落到磁盘上才把新配置当作已创建。
                if (!wroteNewProfile) return;
                profileSaveAsBuffer = "";
                profileExpanded = false;
            }
        }

        private void DrawProfileRenameButton()
        {
            if (!profileIsRenaming)
            {
                if (GUILayout.Button(I18n.Tr("rename"), GUILayout.MinWidth(60)))
                {
                    profileIsRenaming = true;
                    profileRenameBuffer = Settings.CurrentProfile;
                }
                return;
            }
            profileRenameBuffer = GUILayout.TextField(profileRenameBuffer, GUILayout.Width(100));
            if (GUILayout.Button("✓", GUILayout.Width(24)))
            {
                if (!ProfileNameUnusable(profileRenameBuffer)
                    && SanitizeFileName(profileRenameBuffer.Trim()) != SanitizeFileName(Settings.CurrentProfile))
                {
                    string newName = SanitizeFileName(profileRenameBuffer.Trim());
                    // OrdinalIgnoreCase, matching RenameProfile's own duplicate check. With the
                    // ordinal form here, "Boss" → "boss" passed this test and was then refused
                    // inside RenameProfile, which returns silently — so the foldout closed and the
                    // name was unchanged, making the button look broken. Two spellings of one
                    // question in the same operation.
                    // 用 OrdinalIgnoreCase，与 RenameProfile 自己的重名检查一致。这里用 ordinal 时，
                    // 「Boss」→「boss」能通过本检查、随后却被 RenameProfile 拒绝，而后者是静默
                    // return——于是折叠关闭、名字不变，按钮看起来就是坏的。同一操作里同一问题的两种
                    // 写法。
                    bool dup = Settings.ProfileNames != null
                        && Settings.ProfileNames.Any(p => string.Equals(SanitizeFileName(p), newName, System.StringComparison.OrdinalIgnoreCase));
                    if (!dup)
                        RenameProfile(Settings.CurrentProfile, newName);
                }
                profileIsRenaming = false;
                profileExpanded = false;
            }
            if (GUILayout.Button("✗", GUILayout.Width(24)))
                profileIsRenaming = false;
        }

        // ===== profile packages (.jkv) / 配置包（.jkv） =====
        private bool packageListExpanded;
        private string packageMessage = "";
        private bool dmNoteListExpanded;
        private string dmNoteMessage = "";
        // Directory listings for the two expandable lists. Rebuilt on expand and dropped on
        // collapse/import — a directory scan on every IMGUI event was a steady frame-time cost.
        // 两个可展开列表的目录列表结果：展开时重建，折叠/导入后丢弃——每个 IMGUI 事件都扫目录
        // 会持续消耗帧时间。
        private List<string> packageCache;
        private List<string> dmNoteCache;

        /// <summary>Export / import the current profile as a shareable .jkv archive. / 把当前配置
        /// 导出 / 导入为可分享的 .jkv 归档。</summary>
        private void DrawPackageSection()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label(I18n.Tr("pkg_section"));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(I18n.Tr("pkg_export"), GUILayout.MinWidth(110f)))
            {
                string path = ExportProfilePackage(Settings.CurrentProfile);
                packageMessage = path != null
                    ? string.Format(I18n.Tr("pkg_exported"), Path.GetFileName(path))
                    : I18n.Tr("pkg_err_export");
            }
            if (GUILayout.Button(I18n.Tr("pkg_import"), GUILayout.MinWidth(110f)))
            {
                // Refresh on open, not per event: IMGUI fires Layout+Repaint per frame and this is a
                // directory scan. / 展开时刷新而非每事件刷新：IMGUI 每帧触发 Layout+Repaint 多次，
                // 而这是一次目录扫描。
                packageListExpanded = !packageListExpanded;
                // Guarded like the other SyncProfilesWithDisk call site at :195. The bare call
                // here let a storage exception escape into the GUILayout callback, which leaves
                // Begin/End groups unbalanced — the settings window mislays and stops responding
                // until restart — and never set lastSaveError, so not even the red banner appeared.
                // 磁盘满/只读时抛出的异常会从 GUILayout 回调逃出，留下未闭合的布局组（窗口错乱直到
                // 重启），且从不设置 lastSaveError，连红色横幅都不出现。
                if (packageListExpanded) { packageCache = null; GuardedSave("the profile list", SyncProfilesWithDisk); }
            }
            if (GUILayout.Button(I18n.Tr("fm_open_dir"), GUILayout.MinWidth(90f)))
            {
                try
                {
                    string dir = Path.Combine(Loader.ResolveModPath(), "Packages");
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    System.Diagnostics.Process.Start("explorer.exe", dir);
                }
                catch (Exception e)
                {
                    Loader.Error($"KeyViewer: cannot open packages folder: {e.Message}");
                }
            }
            GUILayout.EndHorizontal();

            if (packageListExpanded)
            {
                // Cached for the lifetime of the expansion. Listing re-stat'ed every file on EVERY
                // IMGUI event (Layout + Repaint + input, ~2-3 per frame) — the sort comparator alone
                // made two GetLastWriteTimeUtc calls per comparison. The list only changes when the
                // user edits the folder or imports, both of which drop the cache.
                // 展开期间缓存：此前每个 IMGUI 事件都重新 stat 每个文件（仅排序比较器就每比较一次
                // 调用两次 GetLastWriteTimeUtc），而列表只会在用户改文件夹或导入时变化——两者都会
                // 清空缓存。
                packageCache ??= ListProfilePackages();
                List<string> packages = packageCache;
                if (packages.Count == 0)
                {
                    GUILayout.Label(I18n.Tr("pkg_none"));
                }
                else
                {
                    foreach (string pkg in packages)
                    {
                        if (!GUILayout.Button(pkg, GUILayout.MinWidth(200f))) continue;
                        if (ImportProfilePackage(pkg, out string msg)) packageMessage = msg;
                        else packageMessage = msg ?? I18n.Tr("pkg_err_failed_generic");
                        packageListExpanded = false;
                        packageCache = null;
                    }
                }
            }

            if (!string.IsNullOrEmpty(packageMessage))
                GUILayout.Label(packageMessage);
            GUILayout.Label(I18n.Tr("pkg_hint"));

            GUILayout.Space(6f);
            GUILayout.Label("<b>" + I18n.Tr("dmnote_section") + "</b>");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(I18n.Tr("dmnote_import"), GUILayout.MinWidth(140f)))
            {
                dmNoteListExpanded = !dmNoteListExpanded;
                dmNoteCache = null; // rescan on every expand / 每次展开都重新扫描
            }
            if (GUILayout.Button(I18n.Tr("dmnote_open_dir"), GUILayout.MinWidth(100f)))
            {
                try
                {
                    Directory.CreateDirectory(DmNotePresetDirectory);
                    System.Diagnostics.Process.Start("explorer.exe", DmNotePresetDirectory);
                }
                catch (Exception e)
                {
                    Loader.Error($"KeyViewer: cannot open DM Note presets folder: {e.Message}");
                }
            }
            GUILayout.EndHorizontal();
            if (dmNoteListExpanded)
            {
                // Cached like the package list: a directory scan plus one FileInfo per file on
                // every IMGUI event. Collapsing (the toggle below) and importing both drop it.
                // 与包列表同样缓存：每个 IMGUI 事件都要扫目录并为每个文件建 FileInfo。
                dmNoteCache ??= ListDmNotePresetFiles();
                List<string> presets = dmNoteCache;
                if (presets.Count == 0)
                {
                    GUILayout.Label(I18n.Tr("dmnote_none"));
                }
                else
                {
                    foreach (string preset in presets)
                    {
                        if (!GUILayout.Button(Path.GetFileName(preset), GUILayout.MinWidth(220f))) continue;
                        if (ImportDmNotePresetFile(preset, out string imported)) dmNoteMessage = imported;
                        else dmNoteMessage = imported ?? I18n.Tr("dmnote_import_failed");
                        dmNoteListExpanded = false;
                        dmNoteCache = null;
                    }
                }
            }
            if (!string.IsNullOrEmpty(dmNoteMessage)) GUILayout.Label(dmNoteMessage);
            GUILayout.Label(I18n.Tr("dmnote_hint"));
            GUILayout.EndVertical();
        }

        private void DrawProfileDeleteButton()
        {
            bool canDelete = Settings.ProfileNames != null && Settings.ProfileNames.Length > 1;
            GUI.enabled = canDelete;
            if (GUILayout.Button(I18n.Tr("delete"), GUILayout.MinWidth(60)))
            {
                DeleteProfile(Settings.CurrentProfile);
                profileExpanded = false;
            }
            GUI.enabled = true;
        }

        /// <summary>Language names are fixed literals, never translated, so a static table is safe
        /// here — nothing IMGUI-typed is involved, which is what makes a static field legal in this
        /// partial class (see the round-45 note about GUIContent/GUILayoutOption in static
        /// initialisers). The per-event `string[3]` literal this replaces was pure garbage.
        /// 语言名是固定字面量、从不翻译，故静态表在此是安全的——其中不涉及任何 IMGUI 类型，
        /// 而这正是该分部类里静态字段合法的前提（见第 45 轮关于静态初始化器里 GUIContent/
        /// GUILayoutOption 的说明）。被替换掉的每事件 `string[3]` 字面量纯属垃圾。
        /// </summary>
        private static readonly string[] LanguageLabels = { "English", "中文", "한국어" };

        /// <summary>Global font-style toggles: display name, TMP mask bit, and an exclusivity
        /// group (0 = independent, 1 = mutually exclusive with Lowercase/Uppercase/SmallCaps,
        /// 2 = with Superscript/Subscript). Fixed tables — hoisted out of the per-event path.
        /// 全局字体样式开关：显示名、TMP 掩码位、互斥组（0=独立，1=与 Lowercase/Uppercase/
        /// SmallCaps 互斥，2=与 Superscript/Subscript 互斥）。固定表——已提出每事件路径。
        /// </summary>
        private static readonly string[] FontStyleNames =
            { "Bold", "Italic", "Underline", "Lowercase", "Uppercase", "SmallCaps", "Strikethrough", "Superscript", "Subscript" };
        private static readonly int[] FontStyleFlagValues = { 1, 2, 4, 8, 16, 32, 64, 128, 256 };
        private static readonly int[] FontStyleGroups = { 0, 0, 0, 1, 1, 1, 0, 2, 2 };

        private void DrawLanguageSection()
        {
            GUILayout.BeginHorizontal();
            string[] langLabels = LanguageLabels;
            // I18n renders unknown language codes as English — mirror that here so the highlighted
            // entry matches what is actually shown. / I18n 将未知语言代码按英文渲染——此处保持一致,
            // 让高亮项与实际显示一致。
            int langIdx = Settings.Language == "zh" ? 1 : Settings.Language == "ko" ? 2 : 0;
            if (GUILayout.Button(I18n.Tr("language") + ": " + langLabels[langIdx]))
            {
                langIdx = (langIdx + 1) % 3;
                Settings.Language = langIdx == 0 ? "en" : langIdx == 1 ? "zh" : "ko";
                I18n.Lang = Settings.Language;
                SaveSettingsFromGui();
            }
            GUILayout.EndHorizontal();
        }

        /// <summary>
        /// Count display options (the enable toggle and reset-count button moved to the header bar) / 计数显示选项(总开关与重置计数按钮已移到顶部常驻栏)
        /// </summary>
        private void DrawCountResetSection()
        {
            GUILayout.BeginHorizontal();
            bool newFormatting = GUILayout.Toggle(Settings.Data.EnableCountFormatting, I18n.Tr("count_formatting"));
            if (newFormatting != Settings.Data.EnableCountFormatting)
            {
                Settings.Data.EnableCountFormatting = newFormatting;
                SaveSettingsFromGui();
                RefreshAllCountDisplay();
            }
            GUILayout.EndHorizontal();
        }

        private void ExecuteCountReset()
        {
            lastTotal = -1;
            Settings.Data.TotalCount = 0;
            for (int i = 0; i < Settings.Data.Count.Length; i++)
                Settings.Data.Count[i] = 0;
            foreach (FmNode node in Settings.Data.CustomNodes)
                if (node != null) node.Count = 0;
            ClearKpsTimers();
            if (Keys != null)
                for (int i = 0; i < Keys.Length; i++)
                    if (Keys[i] != null && Keys[i].value != null) // Unity overload catches destroyed keys / Unity 重载可识别已销毁按键
                        Keys[i].value.text = "0";
            foreach (Key k in IsCustomLayout ? StatKeys(1) : SinglePanel(Kps))
                SetKpsTotalDisplay(k, "KPS", "0");
            foreach (Key k in IsCustomLayout ? StatKeys(2) : SinglePanel(Total))
                SetKpsTotalDisplay(k, "Total", "0");
            SaveSettingsFromGui();
        }

        private static readonly (int flag, string label)[] FontStyleFlagLabels =
        {
            (1, "B"), (2, "I"), (4, "U"), (8, "Lc"),
            (16, "Uc"), (32, "Sc"), (64, "St"), (128, "Sup"), (256, "Sub")
        };

        private string BuildFontStyleSummary()
        {
            int f = Settings.Data.FontStyleFlags;
            if (f == 0) return "Normal";
            // This runs on EVERY IMGUI event, including for the font rows the user has scrolled
            // far past, and the font-style row itself never leaves the screen while the Fonts page
            // is open. A fresh List<string> plus a Join (which allocates the joined string anyway)
            // per event is pure garbage. Reused scratch + a small StringBuilder; the result is
            // short (at most 9 two-to-three character labels) so a bounded StringBuilder never
            // outgrows its first chunk.
            // 本方法在**每个** IMGUI 事件都跑，包括用户已滚得很远、根本看不到的那些字体行；而
            // 字体页开着时字体样式行从不离开屏幕。每个事件新建 List<string> 外加一次 Join（后者
            // 本就要分配拼接结果）纯属垃圾。改用复用暂存 + 一个小的 StringBuilder；结果很短
            // （最多 9 个两三字符的标签），有界 StringBuilder 不会超出它的首个块。
            fontStyleSummary.Length = 0;
            foreach (var (flag, label) in FontStyleFlagLabels)
            {
                if ((f & flag) == 0) continue;
                if (fontStyleSummary.Length > 0) fontStyleSummary.Append(' ');
                fontStyleSummary.Append(label);
            }
            return fontStyleSummary.ToString();
        }

        private readonly System.Text.StringBuilder fontStyleSummary = new System.Text.StringBuilder(48);

        private void DrawFontSection()
        {
            GUILayout.Label(I18n.Tr("font_style") + ":");
            // Same resolution the renderer uses (name first, index as the legacy fallback), so the
            // label here cannot disagree with what is actually drawn. Reading FontIndex directly
            // made the page show one font while the overlay drew another.
            // 用**渲染器同一套**解析（名字优先，索引只作旧配置回退），故这里的标注不可能与实际绘制
            // 的字体不一致。此前直接读 FontIndex，于是设置页显示一个字体、覆盖层画出另一个。
            FontEntry curEntry = GetCurrentFontEntry();
            string curFont = curEntry != null ? curEntry.name : "None";
            if (GUILayout.Button((fontListExpanded ? "◢ " : "▶ ") + curFont, GUILayout.MinWidth(200)))
                fontListExpanded = !fontListExpanded;
            if (fontListExpanded)
            {
                if (fontList.Count > 0)
                {
                    int newIdx = Settings.Data.FontIndex;
                    for (int i = 0; i < fontList.Count; i++)
                    {
                        bool selected = i == Settings.Data.FontIndex;
                        if (GUILayout.Button((selected ? "✓ " : "  ") + fontList[i].name, GUILayout.MinWidth(200)))
                            newIdx = i;
                    }
                    if (newIdx != Settings.Data.FontIndex)
                    {
                        Settings.Data.FontIndex = newIdx;
                        Settings.Data.FontName = fontList[newIdx].name;
                        fontRestored = false;
                        UpdateAllFonts();
                        SaveSettingsFromGui();
                    }
                }
                else
                    GUILayout.Label("▶ " + I18n.Tr("no_fonts_found"), GUILayout.MinWidth(200));

                GUILayout.Space(5);
                GUILayout.BeginVertical("box");
                GUILayout.Label(I18n.Tr("custom_font_tip"));
                GUILayout.Label($"CustomFont : {Path.Combine(Loader.ResolveModPath(), "CustomFont")}");
                GUILayout.EndVertical();
            }

            GUILayout.Space(3);
            string styleSummary = BuildFontStyleSummary();
            fontStyleExpanded = DrawFoldoutButton(I18n.Tr("font_style") + ": " + styleSummary, fontStyleExpanded);
            if (fontStyleExpanded)
            {
                // Three fixed tables, hoisted to statics. Nothing here is i18n or IMGUI-typed, so
                // static is safe in this partial class (see the round-45 note) — and the section
                // stays open while the user fiddles, so this was 3 arrays per IMGUI event.
                // 三张固定表，提为静态。其中既非 i18n 也非 IMGUI 类型，故在该分部类里用静态是
                // 安全的（见第 45 轮说明）——而用户摆弄时该区块一直开着，故此前是每事件 3 个数组。
                int[] styleFlags = FontStyleFlagValues;
                int[] styleGroups = FontStyleGroups;
                bool changed = false;
                for (int i = 0; i < styleFlags.Length; i++)
                {
                    bool active = (Settings.Data.FontStyleFlags & styleFlags[i]) != 0;
                    bool newActive = GUILayout.Toggle(active, FontStyleNames[i]);
                    if (newActive != active)
                    {
                        if (newActive)
                        {
                            if (styleGroups[i] == 1)
                                Settings.Data.FontStyleFlags &= ~(8 | 16 | 32);
                            else if (styleGroups[i] == 2)
                                Settings.Data.FontStyleFlags &= ~(128 | 256);
                        }
                        Settings.Data.FontStyleFlags = newActive ? Settings.Data.FontStyleFlags | styleFlags[i] : Settings.Data.FontStyleFlags & ~styleFlags[i];
                        changed = true;
                    }
                }
                if (changed)
                {
                    UpdateAllFonts();
                    SaveSettingsFromGui();
                }
            }
        }

        private void DrawFolderButtons()
        {
            GUILayout.BeginHorizontal();
            // Both branches are wrapped, matching the structurally identical Packages (:312) and
            // DmNote (:367) folder buttons a few hundred lines down. CreateDirectory throws
            // UnauthorizedAccessException on a read-only mod folder — precisely the situation
            // Loader.ResolveModPath falls back to — and Process.Start throws Win32Exception where
            // explorer.exe cannot be launched; either one escaping a GUILayout callback unbalances
            // the Begin/End groups and disables the whole settings window until restart, with no
            // banner (these paths are not saves, so there is no lastSaveError to show).
            // 两处都加保护，与几百行外结构完全相同的 Packages(:312) 与 DmNote(:367) 按钮一致。
            // 只读 Mod 目录下 CreateDirectory 抛 UnauthorizedAccessException（正是 ResolveModPath
            // 要回退的那种情况），explorer.exe 起不来时 Process.Start 抛 Win32Exception；任一者从
            // GUILayout 回调逃出都会让布局组失衡、整个设置窗口失效直到重启，且没有横幅可显示
            // （这些路径不是保存，不存在 lastSaveError）。
            if (GUILayout.Button(I18n.Tr("open_config_folder"), GUILayout.MinWidth(120)))
            {
                try
                {
                    string dir = Path.GetDirectoryName(ConfigPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                        System.Diagnostics.Process.Start("explorer.exe", dir);
                }
                catch (Exception e) { Loader.Error($"KeyViewer: could not open the config folder: {e.Message}"); }
            }
            if (GUILayout.Button(I18n.Tr("open_font_folder"), GUILayout.MinWidth(120)))
            {
                try
                {
                    string modPath = Loader.ResolveModPath();
                    string customFontDir = Path.Combine(modPath, "CustomFont");
                    if (!Directory.Exists(customFontDir)) Directory.CreateDirectory(customFontDir);
                    System.Diagnostics.Process.Start("explorer.exe", customFontDir);
                }
                catch (Exception e) { Loader.Error($"KeyViewer: could not open the font folder: {e.Message}"); }
            }
            GUILayout.EndHorizontal();

            // DownLocation shifts the fixed layouts' preset rows — custom nodes are absolute
            // screen coordinates, the toggle is meaningless there. / 下移对固定布局的预设排生效
            // ——自定义节点是绝对屏幕坐标，该开关对其无意义。
            if (!IsCustomLayout)
            {
                bool newDownLocation = GUILayout.Toggle(Settings.Data.DownLocation, I18n.Tr("place_below"));
                if (newDownLocation != Settings.Data.DownLocation)
                {
                    Settings.Data.DownLocation = newDownLocation;
                    // ResetKeyViewer recreates foot keys internally — no outer ResetFootKeyViewer.
                    // ResetKeyViewer 内部已重建脚键——无需外层 ResetFootKeyViewer。
                    ResetKeyViewer();
                    SaveSettingsFromGui();
                }
            }
        }

        private void DrawCustomPositionSection()
        {
            // Custom layout: node positions live in the FreeMake editor — the sliders here drive
            // ResetKeyViewerPosition, which deliberately no-ops for custom (dead controls).
            // / 自定义布局：节点位置在 FreeMake 编辑器里调——本区滑块驱动 ResetKeyViewerPosition，
            // 而它对自定义布局刻意空操作（会变成死控件）。
            if (IsCustomLayout)
            {
                GUILayout.Label(I18n.Tr("fm_custom_pos_hint"));
                return;
            }
            CustomPositionExpanded = DrawFoldoutButton(I18n.Tr("custom_pos"), CustomPositionExpanded);
            if (!CustomPositionExpanded) return;

            GUILayout.BeginVertical("box");
            bool newEnabled = GUILayout.Toggle(Settings.Data.CustomPositionEnabled,
                I18n.Tr("custom_pos") + " " + I18n.Tr("enable"));
            if (newEnabled != Settings.Data.CustomPositionEnabled)
            {
                Settings.Data.CustomPositionEnabled = newEnabled;
                SaveSettingsFromGui();
                if (newEnabled)
                {
                    ResetKeyViewerPosition();
                    ResetFootKeyViewerPosition();
                }
                else
                {
                    // ResetKeyViewer recreates foot keys internally — no outer ResetFootKeyViewer.
                    // ResetKeyViewer 内部已重建脚键——无需外层 ResetFootKeyViewer。
                    ResetKeyViewer();
                }
            }

            if (Settings.Data.CustomPositionEnabled)
            {
                if (KeyViewer.IsFullKeyboard)
                {
                    GUILayout.Label(I18n.Tr("main_key_pos") + ":");
                    Vector2 tempMainPos = Settings.Data.MainKeyViewerPosition;
                    bool positionChanged = false;
                    float newMainX = FloatSliderField("X", tempMainPos.x, 0f, 1f);
                    if (newMainX != tempMainPos.x) { tempMainPos.x = newMainX; positionChanged = true; }
                    float newMainY = FloatSliderField("Y", tempMainPos.y, 0f, 1f);
                    if (newMainY != tempMainPos.y) { tempMainPos.y = newMainY; positionChanged = true; }
                    if (positionChanged)
                    {
                        Settings.Data.MainKeyViewerPosition = tempMainPos;
                        ResetKeyViewerPosition();
                        SaveSettingsFromGui();
                    }
                    if (GUILayout.Button(I18n.Tr("reset_pos")))
                    {
                        Settings.Data.MainKeyViewerPosition = new Vector2(0, 1);
                        ResetKeyViewerPosition();
                        SaveSettingsFromGui();
                    }
                }
                else
                {
                    GUILayout.Label(I18n.Tr("main_key_pos") + ":");
                    Vector2 tempMainPos = Settings.Data.MainKeyViewerPosition;
                    Vector2 tempFootPos = Settings.Data.FootKeyViewerPosition;
                    bool positionChanged = false;

                    float newMainX = FloatSliderField("X", tempMainPos.x, 0f, 1f);
                    if (newMainX != tempMainPos.x) { tempMainPos.x = newMainX; positionChanged = true; }
                    float newMainY = FloatSliderField("Y", tempMainPos.y, 0f, 1f);
                    if (newMainY != tempMainPos.y) { tempMainPos.y = newMainY; positionChanged = true; }

                    GUILayout.Label(I18n.Tr("foot_key_pos") + ":");
                    float newFootX = FloatSliderField("X", tempFootPos.x, 0f, 1f);
                    if (newFootX != tempFootPos.x) { tempFootPos.x = newFootX; positionChanged = true; }
                    float newFootY = FloatSliderField("Y", tempFootPos.y, 0f, 1f);
                    if (newFootY != tempFootPos.y) { tempFootPos.y = newFootY; positionChanged = true; }

                    if (positionChanged)
                    {
                        Settings.Data.MainKeyViewerPosition = tempMainPos;
                        Settings.Data.FootKeyViewerPosition = tempFootPos;
                        ResetKeyViewerPosition();
                        ResetFootKeyViewerPosition();
                        SaveSettingsFromGui();
                    }

                    if (GUILayout.Button(I18n.Tr("reset_pos")))
                    {
                        Settings.Data.MainKeyViewerPosition = new Vector2(0, 1);
                        Settings.Data.FootKeyViewerPosition = new Vector2(0.24f, 1f);
                        ResetKeyViewerPosition();
                        ResetFootKeyViewerPosition();
                        SaveSettingsFromGui();
                    }
                }
            }
            GUILayout.EndVertical();
        }

        private void DrawLayoutSection()
        {
            GUILayout.Label(I18n.Tr("key_layout") + ":");
            KeyviewerStyle newStyle = (KeyviewerStyle)GUILayout.SelectionGrid((int)Settings.Data.KeyViewerStyle, KeyLayoutNames, 3);
            if (newStyle != Settings.Data.KeyViewerStyle)
            {
                Settings.Data.KeyViewerStyle = newStyle;
                ChangeKeyViewer();
                SaveSettingsFromGui();
            }

            if (IsCustomLayout)
            {
                if (GUILayout.Button(I18n.Tr("fm_open_editor"), GUILayout.Height(26f)))
                    OpenFreeMakeEditor();
            }

            // Custom layouts keep the width toggle visible — it picks WHICH variant (standard
            // 50px vs mixed-width) the FreeMake PRESET generator builds for 12K/20K styles. /
            // 自定义布局保留宽度开关——它决定 FreeMake 预设生成器为 12K/20K 等样式构建哪套
            // 变体（标准 50px 还是宽窄混排）。
            if (!KeyViewer.IsFullKeyboard)
            {
                // Standard key width toggle: only show for layouts with mixed-width back rows
                // 标准按键宽度开关：仅在有宽窄键混排的布局显示
                bool hasNonStandardWidth = IsCustomLayout || Settings.Data.KeyViewerStyle switch
                {
                    KeyviewerStyle.Key10 or KeyviewerStyle.Key12 or KeyviewerStyle.Key20 => true,
                    _ => false
                };
                if (hasNonStandardWidth)
                {
                    bool newStdWidth = GUILayout.Toggle(Settings.Data.StandardKeyWidth, I18n.Tr("standard_key_width"));
                    if (newStdWidth != Settings.Data.StandardKeyWidth)
                    {
                        Settings.Data.StandardKeyWidth = newStdWidth;
                        ChangeKeyViewer();
                        SaveSettingsFromGui();
                    }
                }

                // FreeMake preset generation uses FootKeyViewerStyle to create foot-key nodes,
                // so the selector must stay reachable in custom layout too. ResetFootKeyViewer
                // is already a no-op for custom layout, so exposing the grid is safe. / FreeMake
                // 预设生成会按 FootKeyViewerStyle 创建脚键节点，因此自定义布局下也必须保留
                // 该选择器；ResetFootKeyViewer 在自定义布局下已是空操作，暴露网格安全。
                GUILayout.Label(I18n.Tr("foot_keys") + ":");
                FootKeyviewerStyle newFootStyle = (FootKeyviewerStyle)GUILayout.SelectionGrid((int)Settings.Data.FootKeyViewerStyle, FootKeyLayoutNames, 5);
                if (newFootStyle != Settings.Data.FootKeyViewerStyle)
                {
                    Settings.Data.FootKeyViewerStyle = newFootStyle;
                    ResetFootKeyViewer();
                    SaveSettingsFromGui();
                }
            }

            float newSettingsSize = FloatSliderField(I18n.Tr("size"), Settings.Data.Size, 0.1f, 2f);
            if (newSettingsSize != Settings.Data.Size)
            {
                Settings.Data.Size = newSettingsSize;
                if (KeyViewerSizeObject != null)
                    KeyViewerSizeObject.transform.localScale = new Vector3(Settings.Data.Size, Settings.Data.Size, 1);
                SaveSettingsFromGui();
            }
        }

        // ===== Per-key text size section / 每键字号 区块 =====
        int perKeyTextSelected = -1;
        /// <summary>Per-slot cached button label for DrawPerKeyTextSizeBtn, plus the cached
        /// GUILayoutOption. Both must be INSTANCE fields: a static GUILayoutOption would drag
        /// UnityEngine.IMGUIModule into this type's static constructor and make every caller need
        /// that assembly (the Harness fails to load the type immediately). / 每槽缓存的按钮标签与
        /// 宽度。两者都必须是**实例**字段：静态 GUILayoutOption 会把 UnityEngine.IMGUIModule 拖进
        /// 本类型的静态构造，使每个调用方都需要该程序集（Harness 会立刻加载失败）。
        /// </summary>
        private readonly PerKeyTextSizeBtnLabel[] perKeyTextSizeBtnLabels = new PerKeyTextSizeBtnLabel[PerKeySlotCount];
        private GUILayoutOption perKeyTextSizeMinWidth;
        bool perKeyTextExpanded = false;

        private void DrawPerKeyTextSizeSection()
        {
            // Per-key text size targets the standard layouts — GetKeyCode/GetBackSequence don't map to
            // the 108-key view, so hide the whole section there. / 每键字号面向标准布局——
            // GetKeyCode/GetBackSequence 与 108 键视图不对应，全键盘下隐藏整个区块。
            if (KeyViewer.IsFullKeyboard) return;
            if (IsCustomLayout) return; // custom nodes carry their own sizes / 自定义节点的字号在编辑器里配
            perKeyTextExpanded = DrawFoldoutButton(I18n.Tr("per_key_text_size"), perKeyTextExpanded);
            if (!perKeyTextExpanded) return;

            GUILayout.BeginVertical("box");
            bool pk = GUILayout.Toggle(Settings.Data.EnablePerKeyTextSize, I18n.Tr("enable"));
            if (pk != Settings.Data.EnablePerKeyTextSize)
            {
                Settings.Data.EnablePerKeyTextSize = pk;
                if (!pk)
                {
                    int n = PerKeySlotCount;
                    if (Settings.Data.PerKeyFontSize != null)
                        for (int i = 0; i < n && i < Settings.Data.PerKeyFontSize.Length; i++)
                            Settings.Data.PerKeyFontSize[i] = 0f;
                }
                UpdateAllFonts();
                SaveSettingsFromGui();
            }

            if (Settings.Data.EnablePerKeyTextSize)
            {
                GUILayout.BeginVertical("box");
                KeyCode[] keyCodes = GetKeyCode();
                KeyCode[] footKeyCodes = GetFootKeyCode();
                // Same guard the per-key COLOUR panel has (KeyViewerColorGUI.cs:330). Row 1 indexed
                // keyCodes[i] unguarded and row 2 indexed keyCodes[backSequence[b]] with only the
                // `b < 8` bound — while the row-3 loop right below DID guard the same thing. Today
                // EnsureSettingsArrays forces both arrays to their exact default lengths, so this is
                // defence in depth rather than a live crash; but an IndexOutOfRangeException inside
                // an IMGUI callback disables the whole settings window until restart, so any future
                // path that swaps Settings.Data without EnsureSettingsArrays would be very
                // expensive. / 与每键**颜色**面板同款守卫。第 1 排无守卫地索引 keyCodes[i]、第 2 排
                // 只判了 `b < 8` 就索引 keyCodes[backSequence[b]]，而紧随其后的第 3 排循环**反倒**
                // 有守卫。今天 EnsureSettingsArrays 会把两个数组强制成精确默认长度，故这是纵深
                // 防御而非现存崩溃；但 IMGUI 回调里的越界会让整个设置窗口失效直到重启。
                if (keyCodes == null || keyCodes.Length < 8)
                {
                    GUILayout.EndVertical();
                    return;
                }

                GUILayout.Label(I18n.Tr("row1_keys") + ":");
                GUILayout.BeginHorizontal();
                for (int i = 0; i < 8; i++) DrawPerKeyTextSizeBtn(i, KeyToString(keyCodes[i]));
                GUILayout.EndHorizontal();

                byte[] backSequence = GetBackSequence();
                if (backSequence != null && backSequence.Length > 0)
                {
                    GUILayout.Label(I18n.Tr("row2_keys") + ":");
                    GUILayout.BeginHorizontal();
                    for (int b = 0; b < backSequence.Length && b < 8; b++)
                        if (backSequence[b] < keyCodes.Length)
                            DrawPerKeyTextSizeBtn(backSequence[b], KeyToString(keyCodes[backSequence[b]]));
                    GUILayout.EndHorizontal();
                }

                if (backSequence != null && backSequence.Length > 8)
                {
                    GUILayout.Label(I18n.Tr("row3_keys") + ":");
                    GUILayout.BeginHorizontal();
                    for (int b = 8; b < backSequence.Length; b++)
                    {
                        // Body, not loop condition — the row-2 loop above does the same. As a
                        // condition this test terminates the loop, dropping every remaining row-3 key.
                        // 放在循环体而非循环条件——上方的第 2 排循环同样如此。作为条件时该检查会终止
                        // 循环，使第 3 排剩余所有按键一起消失。
                        if (backSequence[b] < keyCodes.Length)
                            DrawPerKeyTextSizeBtn(backSequence[b], KeyToString(keyCodes[backSequence[b]]));
                    }
                    GUILayout.EndHorizontal();
                }

                if (footKeyCodes != null && footKeyCodes.Length > 0)
                {
                    GUILayout.Label(I18n.Tr("foot_keys") + ":");
                    int rows = footKeyCodes.Length <= 8 ? 1 : 2;
                    for (int r = 0; r < rows; r++)
                    {
                        GUILayout.BeginHorizontal();
                        int start = r * 8;
                        int end = Mathf.Min(start + 8, footKeyCodes.Length);
                        for (int f = start; f < end; f++)
                            DrawPerKeyTextSizeBtn(FootKeyBase + f, KeyToString(footKeyCodes[f]));
                        GUILayout.EndHorizontal();
                    }
                }

                GUILayout.Space(5);
                GUILayout.BeginHorizontal();
                DrawPerKeyTextSizeBtn(MaxKeySlots, "KPS");
                DrawPerKeyTextSizeBtn(MaxKeySlots + 1, "Total");
                GUILayout.EndHorizontal();

                if (perKeyTextSelected >= 0 && perKeyTextSelected < PerKeySlotCount)
                    DrawPerKeyTextSizeEditor(perKeyTextSelected);

                if (GUILayout.Button(I18n.Tr("per_key_color_reset")))
                {
                    int n = PerKeySlotCount;
                    if (Settings.Data.PerKeyFontSize != null)
                        for (int i = 0; i < n && i < Settings.Data.PerKeyFontSize.Length; i++)
                            Settings.Data.PerKeyFontSize[i] = 0f;
                    UpdateAllFonts();
                    SaveSettingsFromGui();
                }

                GUILayout.EndVertical();
            }

            GUILayout.EndVertical();
        }

        private void DrawPerKeyTextSizeBtn(int idx, string label)
        {
            bool selected = perKeyTextSelected == idx;
            if (perKeyTextSizeMinWidth == null) perKeyTextSizeMinWidth = GUILayout.MinWidth(50);
            // Cached label + cached width, so an open foldout costs zero allocations per IMGUI event.
            // This row is up to 42 calls per event (8 + 8 + 8 + 16 + 2), and the previous inline
            // string concatenation plus GUILayout.MinWidth allocated 2 objects each time — the same
            // defect class already fixed in FloatSliderField and DrawTabBar, missed on the per-key
            // TEXT-SIZE twin of DrawPerKeyColorBtn (which does it right). 缓存标签与宽度，使展开状态
            // 下每个 IMGUI 事件零分配。该排每事件最多调用 42 次（8+8+8+16+2），此前的内联字符串
            // 拼接加 GUILayout.MinWidth 每次分配 2 个对象——与 FloatSliderField/DrawTabBar 已修的
            // 同类缺陷，只是漏在了每键**字号**这一支（颜色那一支本来就是对的）。
            PerKeyTextSizeBtnLabel cached = perKeyTextSizeBtnLabels[idx];
            if (perKeyTextSizeBtnLabels[idx] == null
                || !perKeyTextSizeBtnLabels[idx].Key.Equals(label, StringComparison.Ordinal)
                || perKeyTextSizeBtnLabels[idx].Selected != selected)
            {
                perKeyTextSizeBtnLabels[idx] = new PerKeyTextSizeBtnLabel(
                    (selected ? "[ " : "  ") + label + (selected ? " ]" : ""), label, selected);
                cached = perKeyTextSizeBtnLabels[idx];
            }
            if (GUILayout.Button(cached.Label, perKeyTextSizeMinWidth))
            {
                perKeyTextSelected = perKeyTextSelected == idx ? -1 : idx;
            }
        }

        private sealed class PerKeyTextSizeBtnLabel
        {
            public readonly string Label;
            public readonly string Key;
            public readonly bool Selected;
            public PerKeyTextSizeBtnLabel(string label, string key, bool selected)
            { Label = label; Key = key; Selected = selected; }
        }

        private void DrawPerKeyTextSizeEditor(int s)
        {
            GUILayout.Space(5);
            string label = s == MaxKeySlots ? "KPS" : s == MaxKeySlots + 1 ? "Total" : "Key " + s;
            GUILayout.Label("<b>" + label + " " + I18n.Tr("key_font_size") + "</b>");

            // Font size: 0 = use global, 1-72 = per-key override.
            // The PerKeyFontSize length check is done by the caller, not repeated here: this local
            // used to index it unguarded on a path an IMGUI exception would take the whole window
            // down over. It also built a `sizeLabel` string that nothing ever displayed — a
            // ToString plus a concatenation per event for as long as a key stayed selected.
            // 字号：0 = 跟随全局，1-72 = 每键覆盖。PerKeyFontSize 的长度检查由调用方负责，不在此
            // 重复：此前的本地变量在一条会让整个窗口崩掉的路径上无守卫地索引它。它还拼了一个
            // 从未被显示的 sizeLabel 字符串——选中某个键期间每事件一次 ToString 加拼接。
            // The length check is the caller's job, but the NULL check is not: the reader two lines
            // below the writer was hardened and this one was not, so a null PerKeyFontSize threw
            // an NRE out of a GUILayout callback — which unbalances the Begin/End group stack and
            // disables the ENTIRE settings window until restart, not just this row. Same
            // belt-and-suspenders the surrounding guards establish.
            // 长度检查由调用方负责，但**判空**不是：下方两行的写入点已加守卫而这个读取点没有，
            // 故 PerKeyFontSize 为 null 时会从 GUILayout 回调抛出 NRE——Begin/End 组栈失衡，
            // **整个**设置窗口（而不只是这一行）失效直到重启。与周围守卫同款的谨慎起见。
            float[] perKeyFontSize = Settings.Data.PerKeyFontSize;
            float curSize = perKeyFontSize != null && s < perKeyFontSize.Length ? perKeyFontSize[s] : 0f;
            float newSize = FloatSliderField(label + " " + I18n.Tr("key_font_size"), curSize, 0f, 72f, "F0");
            if (newSize != curSize)
            {
                if (perKeyFontSize != null && s < perKeyFontSize.Length)
                {
                    perKeyFontSize[s] = Mathf.Round(newSize);
                    UpdateAllFonts();
                    SaveSettingsFromGui();
                }
            }
        }

        private void DrawDisplaySection()
        {
            // The main-count / per-key-KPS toggles only apply to the normal (non-full-keyboard) layouts;
            // the full 108-key view shows key labels and has its own KPS/Total controls. Custom nodes
            // carry their own HideCount / PerKeyKps per node. / 主区域计数与每键KPS 开关仅对普通布局
            // 生效；全键盘显示键位字母、并有独立的 KPS/Total 控制。自定义节点逐节点自带
            // HideCount / PerKeyKps。
            if (!KeyViewer.IsFullKeyboard && !IsCustomLayout)
            {
                bool newHideCount = GUILayout.Toggle(Settings.Data.HideMainKeyCount, I18n.Tr("hide_main_count"));
                if (newHideCount != Settings.Data.HideMainKeyCount)
                {
                    Settings.Data.HideMainKeyCount = newHideCount;
                    ResetKeyViewer();
                    SaveSettingsFromGui();
                }

                if (!Settings.Data.HideMainKeyCount)
                {
                    bool newPerKeyKps = GUILayout.Toggle(Settings.Data.EnablePerKeyKps, I18n.Tr("per_key_kps"));
                    if (newPerKeyKps != Settings.Data.EnablePerKeyKps)
                    {
                        Settings.Data.EnablePerKeyKps = newPerKeyKps;
                        RefreshAllCountDisplay();
                        // Clear the change-detection cache: with stale lastPerKeyKps values, a quick
                        // off→on toggle within 1s kept showing the old count (equal kps skipped the
                        // SetText) until the queues drained. / 清变化检测缓存:lastPerKeyKps 残留
                        // 时,1 秒内快速关→开会一直显示旧计数(相等的 kps 跳过 SetText)直到队列排空。
                        if (lastPerKeyKps != null)
                            for (int i = 0; i < lastPerKeyKps.Length; i++)
                                lastPerKeyKps[i] = 0;
                        SaveSettingsFromGui();
                    }
                }
            }

            // Streamer mode (hides KPS/Total) only applies to the normal layouts; the full keyboard has
            // its own dedicated "Show KPS / Total" toggle, so don't show this redundant one there.
            // 主播模式（隐藏 KPS/Total）仅对普通布局生效；全键盘已有专属的「显示 KPS/Total」开关，故不再显示这个重复的。
            if (!KeyViewer.IsFullKeyboard)
            {
                bool newStreamer = GUILayout.Toggle(Settings.Data.StreamerMode, I18n.Tr("streamer_mode"));
                if (newStreamer != Settings.Data.StreamerMode)
                {
                    Settings.Data.StreamerMode = newStreamer;
                    SetStatsVisible(!newStreamer);
                    SaveSettingsFromGui();
                }
            }

            // Hide KPS/Total label (non-slim only): value centered, replaces the top label
            // 隐藏 KPS/Total 标签（仅非 slim）：数值居中，替换顶部标签
            bool hasNonSlimKpsTotal = !KeyViewer.KpsTotalIsSlim();
            if (hasNonSlimKpsTotal)
            {
                bool newHideLabel = GUILayout.Toggle(Settings.Data.HideKpsTotalLabel, I18n.Tr("hide_kps_total_label"));
                if (newHideLabel != Settings.Data.HideKpsTotalLabel)
                {
                    Settings.Data.HideKpsTotalLabel = newHideLabel;
                    ChangeKeyViewer();
                    SaveSettingsFromGui();
                }
            }

            float newFontSize = FloatSliderField(I18n.Tr("key_font_size"), Settings.Data.KeyFontSize, 8f, 72f, "F0");
            if (newFontSize != Settings.Data.KeyFontSize)
            {
                Settings.Data.KeyFontSize = newFontSize;
                UpdateAllFonts();
                SaveSettingsFromGui();
            }

            // Global fixed-layout glow. It is deliberately separate from FreeMake node glow and
            // updates cached Images in place — changing a slider never rebuilds the key mesh.
            // 固定布局全局光效：与 FreeMake 节点光效分开，修改滑块只更新缓存 Image，不重建 Mesh。
            if (!IsCustomLayout)
            {
                GUILayout.Space(5f);
                GUILayout.Label("<b>" + I18n.Tr("fixed_glow") + "</b>");
                bool newFixedGlow = GUILayout.Toggle(Settings.Data.EnableFixedKeyGlow, I18n.Tr("fixed_glow_enable"));
                if (newFixedGlow != Settings.Data.EnableFixedKeyGlow)
                {
                    Settings.Data.EnableFixedKeyGlow = newFixedGlow;
                    ApplyFixedKeyGlows();
                    SaveSettingsFromGui();
                }
                if (Settings.Data.EnableFixedKeyGlow)
                {
                    bool newFollow = GUILayout.Toggle(Settings.Data.FixedKeyGlowFollowBody, I18n.Tr("fixed_glow_follow"));
                    if (newFollow != Settings.Data.FixedKeyGlowFollowBody)
                    {
                        Settings.Data.FixedKeyGlowFollowBody = newFollow;
                        ApplyFixedKeyGlows();
                        SaveSettingsFromGui();
                    }
                    float newGlowSize = FloatSliderField(I18n.Tr("fixed_glow_size"), Settings.Data.FixedKeyGlowSize, 0f, 50f, "F0");
                    if (newGlowSize != Settings.Data.FixedKeyGlowSize)
                    {
                        Settings.Data.FixedKeyGlowSize = newGlowSize;
                        ApplyFixedKeyGlows();
                        SaveSettingsFromGui();
                    }
                    float newGlowOpacity = FloatSliderField(I18n.Tr("fixed_glow_opacity"), Settings.Data.FixedKeyGlowOpacity * 100f, 0f, 100f, "F0");
                    if (newGlowOpacity != Settings.Data.FixedKeyGlowOpacity * 100f)
                    {
                        Settings.Data.FixedKeyGlowOpacity = Mathf.Clamp01(newGlowOpacity / 100f);
                        ApplyFixedKeyGlows();
                        SaveSettingsFromGui();
                    }
                    if (!Settings.Data.FixedKeyGlowFollowBody)
                    {
                        Color newGlowColor = DrawColorPicker(I18n.Tr("fixed_glow_color"), Settings.Data.FixedKeyGlowColor, Color.white);
                        if (newGlowColor != Settings.Data.FixedKeyGlowColor)
                        {
                            Settings.Data.FixedKeyGlowColor = newGlowColor;
                            ApplyFixedKeyGlows();
                            SaveSettingsFromGui();
                        }
                    }

                    bool newPressedOverride = GUILayout.Toggle(Settings.Data.FixedKeyGlowPressedOverride, I18n.Tr("fixed_glow_pressed_override"));
                    if (newPressedOverride != Settings.Data.FixedKeyGlowPressedOverride)
                    {
                        Settings.Data.FixedKeyGlowPressedOverride = newPressedOverride;
                        ApplyFixedKeyGlows();
                        SaveSettingsFromGui();
                    }
                    if (Settings.Data.FixedKeyGlowPressedOverride)
                    {
                        bool newPressedFollow = GUILayout.Toggle(Settings.Data.FixedKeyGlowFollowBodyPressed, I18n.Tr("fixed_glow_pressed_follow"));
                        if (newPressedFollow != Settings.Data.FixedKeyGlowFollowBodyPressed)
                        {
                            Settings.Data.FixedKeyGlowFollowBodyPressed = newPressedFollow;
                            ApplyFixedKeyGlows();
                            SaveSettingsFromGui();
                        }
                        float newPressedSize = FloatSliderField(I18n.Tr("fixed_glow_pressed_size"), Settings.Data.FixedKeyGlowSizePressed, 0f, 50f, "F0");
                        if (newPressedSize != Settings.Data.FixedKeyGlowSizePressed)
                        {
                            Settings.Data.FixedKeyGlowSizePressed = newPressedSize;
                            ApplyFixedKeyGlows();
                            SaveSettingsFromGui();
                        }
                        float newPressedOpacity = FloatSliderField(I18n.Tr("fixed_glow_pressed_opacity"), Settings.Data.FixedKeyGlowOpacityPressed * 100f, 0f, 100f, "F0");
                        if (newPressedOpacity != Settings.Data.FixedKeyGlowOpacityPressed * 100f)
                        {
                            Settings.Data.FixedKeyGlowOpacityPressed = Mathf.Clamp01(newPressedOpacity / 100f);
                            ApplyFixedKeyGlows();
                            SaveSettingsFromGui();
                        }
                        if (!Settings.Data.FixedKeyGlowFollowBodyPressed)
                        {
                            Color newPressedColor = DrawColorPicker(I18n.Tr("fixed_glow_pressed_color"), Settings.Data.FixedKeyGlowColorPressed, Color.white);
                            if (newPressedColor != Settings.Data.FixedKeyGlowColorPressed)
                            {
                                Settings.Data.FixedKeyGlowColorPressed = newPressedColor;
                                ApplyFixedKeyGlows();
                                SaveSettingsFromGui();
                            }
                        }
                    }
                }

                GUILayout.Space(5f);
                GUILayout.Label("<b>" + I18n.Tr("fixed_background_gradient") + "</b>");
                bool newFixedGradient = GUILayout.Toggle(Settings.Data.EnableFixedBackgroundGradient, I18n.Tr("fixed_background_gradient_enable"));
                if (newFixedGradient != Settings.Data.EnableFixedBackgroundGradient)
                {
                    Settings.Data.EnableFixedBackgroundGradient = newFixedGradient;
                    ApplyFixedBackgroundGradients();
                    SaveSettingsFromGui();
                }
                if (Settings.Data.EnableFixedBackgroundGradient)
                {
                    Color top = DrawColorPicker(I18n.Tr("fixed_background_gradient_top"), Settings.Data.FixedBackgroundGradientTop, Color.white);
                    if (top != Settings.Data.FixedBackgroundGradientTop)
                    {
                        Settings.Data.FixedBackgroundGradientTop = top;
                        ApplyFixedBackgroundGradients();
                        SaveSettingsFromGui();
                    }
                    Color bottom = DrawColorPicker(I18n.Tr("fixed_background_gradient_bottom"), Settings.Data.FixedBackgroundGradientBottom, Color.black);
                    if (bottom != Settings.Data.FixedBackgroundGradientBottom)
                    {
                        Settings.Data.FixedBackgroundGradientBottom = bottom;
                        ApplyFixedBackgroundGradients();
                        SaveSettingsFromGui();
                    }
                    bool newGradientPressed = GUILayout.Toggle(Settings.Data.FixedBackgroundGradientPressedOverride, I18n.Tr("fixed_background_gradient_pressed"));
                    if (newGradientPressed != Settings.Data.FixedBackgroundGradientPressedOverride)
                    {
                        Settings.Data.FixedBackgroundGradientPressedOverride = newGradientPressed;
                        ApplyFixedBackgroundGradients();
                        SaveSettingsFromGui();
                    }
                    if (Settings.Data.FixedBackgroundGradientPressedOverride)
                    {
                        Color pressedTop = DrawColorPicker(I18n.Tr("fixed_background_gradient_top_pressed"), Settings.Data.FixedBackgroundGradientTopPressed, Color.white);
                        if (pressedTop != Settings.Data.FixedBackgroundGradientTopPressed)
                        {
                            Settings.Data.FixedBackgroundGradientTopPressed = pressedTop;
                            ApplyFixedBackgroundGradients();
                            SaveSettingsFromGui();
                        }
                        Color pressedBottom = DrawColorPicker(I18n.Tr("fixed_background_gradient_bottom_pressed"), Settings.Data.FixedBackgroundGradientBottomPressed, Color.black);
                        if (pressedBottom != Settings.Data.FixedBackgroundGradientBottomPressed)
                        {
                            Settings.Data.FixedBackgroundGradientBottomPressed = pressedBottom;
                            ApplyFixedBackgroundGradients();
                            SaveSettingsFromGui();
                        }
                    }
                }

                GUILayout.Space(5f);
                GUILayout.Label("<b>" + I18n.Tr("fixed_outline_gradient") + "</b>");
                bool newFixedOutlineGradient = GUILayout.Toggle(Settings.Data.EnableFixedOutlineGradient, I18n.Tr("fixed_outline_gradient_enable"));
                if (newFixedOutlineGradient != Settings.Data.EnableFixedOutlineGradient)
                {
                    Settings.Data.EnableFixedOutlineGradient = newFixedOutlineGradient;
                    ApplyFixedOutlineGradients();
                    SaveSettingsFromGui();
                }
                if (Settings.Data.EnableFixedOutlineGradient)
                {
                    Color top = DrawColorPicker(I18n.Tr("fixed_outline_gradient_top"), Settings.Data.FixedOutlineGradientTop, Color.white);
                    if (top != Settings.Data.FixedOutlineGradientTop)
                    {
                        Settings.Data.FixedOutlineGradientTop = top;
                        ApplyFixedOutlineGradients();
                        SaveSettingsFromGui();
                    }
                    Color bottom = DrawColorPicker(I18n.Tr("fixed_outline_gradient_bottom"), Settings.Data.FixedOutlineGradientBottom, Color.black);
                    if (bottom != Settings.Data.FixedOutlineGradientBottom)
                    {
                        Settings.Data.FixedOutlineGradientBottom = bottom;
                        ApplyFixedOutlineGradients();
                        SaveSettingsFromGui();
                    }
                    bool newOutlinePressed = GUILayout.Toggle(Settings.Data.FixedOutlineGradientPressedOverride, I18n.Tr("fixed_outline_gradient_pressed"));
                    if (newOutlinePressed != Settings.Data.FixedOutlineGradientPressedOverride)
                    {
                        Settings.Data.FixedOutlineGradientPressedOverride = newOutlinePressed;
                        ApplyFixedOutlineGradients();
                        SaveSettingsFromGui();
                    }
                    if (Settings.Data.FixedOutlineGradientPressedOverride)
                    {
                        Color pressedTop = DrawColorPicker(I18n.Tr("fixed_outline_gradient_top_pressed"), Settings.Data.FixedOutlineGradientTopPressed, Color.white);
                        if (pressedTop != Settings.Data.FixedOutlineGradientTopPressed)
                        {
                            Settings.Data.FixedOutlineGradientTopPressed = pressedTop;
                            ApplyFixedOutlineGradients();
                            SaveSettingsFromGui();
                        }
                        Color pressedBottom = DrawColorPicker(I18n.Tr("fixed_outline_gradient_bottom_pressed"), Settings.Data.FixedOutlineGradientBottomPressed, Color.black);
                        if (pressedBottom != Settings.Data.FixedOutlineGradientBottomPressed)
                        {
                            Settings.Data.FixedOutlineGradientBottomPressed = pressedBottom;
                            ApplyFixedOutlineGradients();
                            SaveSettingsFromGui();
                        }
                    }
                }

                GUILayout.Space(5f);
                GUILayout.Label("<b>" + I18n.Tr("fixed_text_gradient") + "</b>");
                bool newTextGradient = GUILayout.Toggle(Settings.Data.EnableKeyTextGradient, I18n.Tr("fixed_text_gradient_enable"));
                if (newTextGradient != Settings.Data.EnableKeyTextGradient)
                {
                    Settings.Data.EnableKeyTextGradient = newTextGradient;
                    TickTextGradients();
                    SaveSettingsFromGui();
                }
                if (Settings.Data.EnableKeyTextGradient)
                {
                    Color left = DrawColorPicker(I18n.Tr("fixed_text_gradient_left"), Settings.Data.KeyTextGradientLeft, Color.white);
                    if (left != Settings.Data.KeyTextGradientLeft)
                    {
                        Settings.Data.KeyTextGradientLeft = left;
                        TickTextGradients();
                        SaveSettingsFromGui();
                    }
                    Color right = DrawColorPicker(I18n.Tr("fixed_text_gradient_right"), Settings.Data.KeyTextGradientRight, Color.white);
                    if (right != Settings.Data.KeyTextGradientRight)
                    {
                        Settings.Data.KeyTextGradientRight = right;
                        TickTextGradients();
                        SaveSettingsFromGui();
                    }
                    bool newPressedTextGradient = GUILayout.Toggle(Settings.Data.KeyTextGradientPressedOverride, I18n.Tr("fixed_text_gradient_pressed"));
                    if (newPressedTextGradient != Settings.Data.KeyTextGradientPressedOverride)
                    {
                        Settings.Data.KeyTextGradientPressedOverride = newPressedTextGradient;
                        TickTextGradients();
                        SaveSettingsFromGui();
                    }
                    if (Settings.Data.KeyTextGradientPressedOverride)
                    {
                        Color pressedLeft = DrawColorPicker(I18n.Tr("fixed_text_gradient_left_pressed"), Settings.Data.KeyTextGradientLeftPressed, Color.white);
                        if (pressedLeft != Settings.Data.KeyTextGradientLeftPressed)
                        {
                            Settings.Data.KeyTextGradientLeftPressed = pressedLeft;
                            TickTextGradients();
                            SaveSettingsFromGui();
                        }
                        Color pressedRight = DrawColorPicker(I18n.Tr("fixed_text_gradient_right_pressed"), Settings.Data.KeyTextGradientRightPressed, Color.white);
                        if (pressedRight != Settings.Data.KeyTextGradientRightPressed)
                        {
                            Settings.Data.KeyTextGradientRightPressed = pressedRight;
                            TickTextGradients();
                            SaveSettingsFromGui();
                        }
                    }
                }
                bool newCountGradient = GUILayout.Toggle(Settings.Data.EnableCountTextGradient, I18n.Tr("fixed_count_text_gradient"));
                if (newCountGradient != Settings.Data.EnableCountTextGradient)
                {
                    Settings.Data.EnableCountTextGradient = newCountGradient;
                    TickTextGradients();
                    SaveSettingsFromGui();
                }
                if (Settings.Data.EnableCountTextGradient)
                {
                    Color countLeft = DrawColorPicker(I18n.Tr("fixed_count_text_gradient_left"), Settings.Data.CountTextGradientLeft, Color.white);
                    if (countLeft != Settings.Data.CountTextGradientLeft)
                    {
                        Settings.Data.CountTextGradientLeft = countLeft;
                        TickTextGradients();
                        SaveSettingsFromGui();
                    }
                    Color countRight = DrawColorPicker(I18n.Tr("fixed_count_text_gradient_right"), Settings.Data.CountTextGradientRight, Color.white);
                    if (countRight != Settings.Data.CountTextGradientRight)
                    {
                        Settings.Data.CountTextGradientRight = countRight;
                        TickTextGradients();
                        SaveSettingsFromGui();
                    }
                    bool newPressedCountGradient = GUILayout.Toggle(Settings.Data.CountTextGradientPressedOverride, I18n.Tr("fixed_count_text_gradient_pressed"));
                    if (newPressedCountGradient != Settings.Data.CountTextGradientPressedOverride)
                    {
                        Settings.Data.CountTextGradientPressedOverride = newPressedCountGradient;
                        TickTextGradients();
                        SaveSettingsFromGui();
                    }
                    if (Settings.Data.CountTextGradientPressedOverride)
                    {
                        Color pressedCountLeft = DrawColorPicker(I18n.Tr("fixed_count_text_gradient_left_pressed"), Settings.Data.CountTextGradientLeftPressed, Color.white);
                        if (pressedCountLeft != Settings.Data.CountTextGradientLeftPressed)
                        {
                            Settings.Data.CountTextGradientLeftPressed = pressedCountLeft;
                            TickTextGradients();
                            SaveSettingsFromGui();
                        }
                        Color pressedCountRight = DrawColorPicker(I18n.Tr("fixed_count_text_gradient_right_pressed"), Settings.Data.CountTextGradientRightPressed, Color.white);
                        if (pressedCountRight != Settings.Data.CountTextGradientRightPressed)
                        {
                            Settings.Data.CountTextGradientRightPressed = pressedCountRight;
                            TickTextGradients();
                            SaveSettingsFromGui();
                        }
                    }
                }
            }

            // Per-key text size / spacing section / 每键字号/字间距 区块
            DrawPerKeyTextSizeSection();

            DrawTextStyleSection();

            // Center KPS / Total text — only for flat (slim) KPS/Total designs (e.g. full keyboard, 8K/14K/16K/24K).
            // Hidden for stacked (non-slim) layouts like 12K/10K/20K standard. / 仅对扁平（slim）KPS/Total 生效（全键盘及 8K/14K/16K/24K 等），堆叠布局（12K/10K/20K 标准）隐藏。
            if (KeyViewer.KpsTotalIsSlim())
            {
                bool newCenterKt = GUILayout.Toggle(Settings.Data.KpsTotalCentered, I18n.Tr("fk_kps_total_centered"));
                if (newCenterKt != Settings.Data.KpsTotalCentered)
                {
                    Settings.Data.KpsTotalCentered = newCenterKt;
                    ChangeKeyViewer();
                    SaveSettingsFromGui();
                }
                // Stack KPS/Total text vertically — only available when centered is enabled
                // KPS/Total 上下堆叠 — 仅在居中开启时可用
                if (Settings.Data.KpsTotalCentered)
                {
                    bool newStacked = GUILayout.Toggle(Settings.Data.KpsTotalStackedWhenCentered, I18n.Tr("kps_total_stacked"));
                    if (newStacked != Settings.Data.KpsTotalStackedWhenCentered)
                    {
                        Settings.Data.KpsTotalStackedWhenCentered = newStacked;
                        ChangeKeyViewer();
                        SaveSettingsFromGui();
                    }
                }
            }

            bool newPressAnim = GUILayout.Toggle(Settings.Data.EnablePressAnimation, I18n.Tr("press_animation"));
            if (newPressAnim != Settings.Data.EnablePressAnimation)
            {
                Settings.Data.EnablePressAnimation = newPressAnim;
                // Turning the animation off mid-press skips the release transition (it's gated on
                // this toggle) — reset scales so no key stays stuck shrunken. / 按住途中关闭动画
                // 会跳过释放过渡（受本开关门控）——重置缩放，避免按键卡在缩小状态。
                if (!newPressAnim) ResetAllPressScales();
                SaveSettingsFromGui();
            }

            if (Settings.Data.EnablePressAnimation)
            {
                float newScale = FloatSliderField(I18n.Tr("press_anim_scale"), Settings.Data.PressAnimationScale, 0.5f, 0.95f);
                if (newScale != Settings.Data.PressAnimationScale)
                {
                    Settings.Data.PressAnimationScale = newScale;
                    SaveSettingsFromGui();
                }

                bool newRainAnim = GUILayout.Toggle(Settings.Data.EnablePressAnimationOnRain, I18n.Tr("press_anim_rain"));
                if (newRainAnim != Settings.Data.EnablePressAnimationOnRain)
                {
                    Settings.Data.EnablePressAnimationOnRain = newRainAnim;
                    SaveSettingsFromGui();
                }

                // Easing + duration: the old build hard-coded a linear 80ms lerp, so these default
                // to exactly that and existing setups look unchanged. / 缓动 + 时长：旧版写死线性
                // 80ms 插值，故默认值即为此，现有配置观感不变。
                DrawEasingSelector("press_anim_easing", Settings.Data.PressAnimationEasing, v =>
                {
                    Settings.Data.PressAnimationEasing = v;
                    SaveSettingsFromGui();
                });
                float newDur = FloatSliderField(I18n.Tr("press_anim_duration"), Settings.Data.PressAnimationDurationMs, 10f, 500f, "F0");
                if (newDur != Settings.Data.PressAnimationDurationMs)
                {
                    Settings.Data.PressAnimationDurationMs = newDur;
                    Settings.Data.PressAnimationDurationMs = Mathf.Clamp(newDur, 10f, 2000f);
                    SaveSettingsFromGui();
                }
            }
        }

        // ===== text outline / shadow (Display tab) / 文字描边/阴影（显示页） =====
        // Key label and press count carry INDEPENDENT outline + shadow pairs: a large label and a
        // small count number rarely want the same treatment. / 按键标签与计数文本各自带独立的描边
        // + 阴影：大号标签与小号计数很少需要同样的处理。
        /// <summary>Named-easing selector with a curve preview. A plain dropdown would make 27
        /// cubic-bezier-free curves indistinguishable by name alone — the preview is what makes the
        /// choice possible. / 带曲线预览的命名缓动选择器。纯下拉框会让 27 条与三次贝塞尔无关的曲线
        /// 仅凭名字无法区分——预览才让选择成为可能。</summary>
        private void DrawEasingSelector(string id, string current, Action<string> apply)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(I18n.Tr("press_anim_easing"), GUILayout.Width(100f));
            // Normalize, NOT KvEasing.Default. The press animation passes the *stored* name to
            // KvEasing.Ease, where an empty string has IndexOf("") == 0 → "linear" → the default
            // branch returns t unchanged. Labelling the button "ease-out-cubic" and drawing that
            // curve next to it therefore advertised an eased animation the runtime never plays:
            // the user sees ease-out-cubic, presses a key, gets a dead-straight one, and re-picking
            // the same curve appears to change nothing. Normalize is what maps "" → "linear" —
            // and it is exactly what the FreeMake editor's twin picker already uses for the same
            // field, so the two pickers now agree. It also canonicalises an unknown-but-non-empty
            // name, which previously was echoed verbatim and left no ✓ on any row.
            // 用 Normalize，而**不是** KvEasing.Default。按压动画把**存储的**名字传给
            // KvEasing.Ease，而空串在那里 IndexOf("") == 0 → "linear" → default 分支原样返回 t。
            // 故把按钮标成「ease-out-cubic」并在旁边画出那条曲线，等于宣传一个运行时根本不播的
            // 缓动：用户看到 ease-out-cubic、按下一个键、得到完全笔直的插值，再重新选一次同一条
            // 曲线看起来毫无变化。Normalize 正是把 "" 映射为 "linear" 的那个——也正是 FreeMake
            // 编辑器里同字段的双胞胎选择器早就在用的，故两者现在一致。它同时把「非空但未知」的
            // 名字规范化，此前那种名字被原样回显、任何一行都没有 ✓。
            string shown = Util.KvEasing.Normalize(current);
            if (GUILayout.Button(shown, GUILayout.Width(140f)))
            {
                easingListExpanded = easingListExpanded == id ? null : id;
                if (easingListExpanded == id) easingScroll = Vector2.zero;
            }
            // Preview of the CURRENT selection sits next to the button, so the chosen curve is always
            // visible without opening the list. / 当前选择的预览就在按钮旁边，无需展开列表即可看到
            // 所选曲线。
            DrawEasingCurve(new Rect(GUILayoutUtility.GetRect(56f, 22f).position, new Vector2(56f, 22f)), shown);
            GUILayout.EndHorizontal();

            if (easingListExpanded != id) return;
            GUILayout.BeginVertical("box");
            // Fixed-height scroll: 27 entries must not stretch the settings window off-screen. /
            // 固定高度滚动区：27 个条目不能把设置窗口撑出屏幕。
            easingScroll = GUILayout.BeginScrollView(easingScroll, GUILayout.Height(180f));
            for (int i = 0; i < Util.KvEasing.Names.Length; i++)
            {
                string name = Util.KvEasing.Names[i];
                bool selected = string.Equals(name, shown, StringComparison.OrdinalIgnoreCase);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button((selected ? "✓ " : "  ") + name, GUILayout.MinWidth(150f)))
                {
                    apply(name);
                    easingListExpanded = null;
                }
                DrawEasingCurve(new Rect(GUILayoutUtility.GetRect(70f, 20f).position, new Vector2(70f, 20f)), name);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        /// <summary>Plot one easing into a rect. Values outside [0,1] (the back variants overshoot)
        /// are accommodated by padding the plot vertically, so an overshoot curve is visible rather
        /// than clipped flat. / 把一条缓动画进矩形。越出 [0,1] 的值（back 系列会过冲）通过在竖直方向
        /// 留边来容纳，使过冲曲线可见而非被削平。</summary>
        private void DrawEasingCurve(Rect r, string name)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            GUIUtils.DrawRect(r, new Color(0.12f, 0.12f, 0.14f, 0.85f));
            const int Steps = 24;
            float minV = 0f, maxV = 1f;
            // Two passes: first find the actual range (only back easings leave [0,1]), then plot. /
            // 两遍：先求实际取值范围（只有 back 系列会越出 [0,1]），再绘制。
            for (int i = 0; i <= Steps; i++)
            {
                float v = Util.KvEasing.Ease(name, i / (float)Steps);
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }
            float span = Mathf.Max(0.0001f, maxV - minV);
            Vector2 prev = Vector2.zero;
            for (int i = 0; i <= Steps; i++)
            {
                float t = i / (float)Steps;
                float v = Util.KvEasing.Ease(name, t);
                Vector2 p = new Vector2(r.x + t * r.width, r.yMax - (v - minV) / span * r.height);
                if (i > 0) DrawEasingSegment(prev, p);
                prev = p;
            }
            DrawRectOutline(r, new Color(1f, 1f, 1f, 0.18f), 1f);
        }

        private void DrawEasingSegment(Vector2 a, Vector2 b)
        {
            // Sample the segment rather than drawing a true line: GUIUtils has only axis-aligned
            // rects, and 3 interpolated dots per step is indistinguishable at this size. /
            // 用取样代替真正的画线：GUIUtils 只有轴对齐矩形，而这个尺寸下每段 3 个插值点已经看不出差别。
            const int Dots = 4;
            for (int i = 0; i <= Dots; i++)
            {
                float t = i / (float)Dots;
                float x = Mathf.Lerp(a.x, b.x, t);
                float y = Mathf.Lerp(a.y, b.y, t);
                GUIUtils.DrawRect(new Rect(x, y, 1.5f, 1.5f), new Color(0.55f, 0.85f, 1f, 1f));
            }
        }

        // Foldout for the text-style block, defaulting COLLAPSED like every other foldout in the
        // settings window (all reset to closed on restart; only the active TAB persists). /
        // 文字样式区的折叠开关，默认收起——与设置窗口所有其它折叠区一致（重启后一律回到
        // 收起；跨会话保存的只有当前标签页）。
        private bool textStyleExpanded = false;

        private void DrawTextStyleSection()
        {
            GUILayout.Space(4f);
            textStyleExpanded = DrawFoldoutButton(I18n.Tr("text_style"), textStyleExpanded);
            if (!textStyleExpanded) return;
            DrawTextStyleBlock("key",
                Settings.Data.EnableKeyTextOutline, Settings.Data.KeyTextOutlineColor, Settings.Data.KeyTextOutlineThickness,
                Settings.Data.EnableKeyTextShadow, Settings.Data.KeyTextShadowColor,
                Settings.Data.KeyTextShadowOffsetX, Settings.Data.KeyTextShadowOffsetY, Settings.Data.KeyTextShadowSoftness,
                (on, col, w) =>
                {
                    Settings.Data.EnableKeyTextOutline = on;
                    Settings.Data.KeyTextOutlineColor = col;
                    Settings.Data.KeyTextOutlineThickness = w;
                },
                (on, col, ox, oy, soft) =>
                {
                    Settings.Data.EnableKeyTextShadow = on;
                    Settings.Data.KeyTextShadowColor = col;
                    Settings.Data.KeyTextShadowOffsetX = ox;
                    Settings.Data.KeyTextShadowOffsetY = oy;
                    Settings.Data.KeyTextShadowSoftness = soft;
                });

            DrawTextStyleBlock("count",
                Settings.Data.EnableCountTextOutline, Settings.Data.CountTextOutlineColor, Settings.Data.CountTextOutlineThickness,
                Settings.Data.EnableCountTextShadow, Settings.Data.CountTextShadowColor,
                Settings.Data.CountTextShadowOffsetX, Settings.Data.CountTextShadowOffsetY, Settings.Data.CountTextShadowSoftness,
                (on, col, w) =>
                {
                    Settings.Data.EnableCountTextOutline = on;
                    Settings.Data.CountTextOutlineColor = col;
                    Settings.Data.CountTextOutlineThickness = w;
                },
                (on, col, ox, oy, soft) =>
                {
                    Settings.Data.EnableCountTextShadow = on;
                    Settings.Data.CountTextShadowColor = col;
                    Settings.Data.CountTextShadowOffsetX = ox;
                    Settings.Data.CountTextShadowOffsetY = oy;
                    Settings.Data.CountTextShadowSoftness = soft;
                });
        }

        private void DrawTextStyleBlock(string id, bool outlineOn, Color outlineColor, float outlineWidth,
            bool shadowOn, Color shadowColor, float offX, float offY, float softness,
            Action<bool, Color, float> applyOutline, Action<bool, Color, float, float, float> applyShadow)
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label(I18n.Tr(id == "key" ? "fm_key_text_style" : "fm_count_text_style"));

            bool newOutline = GUILayout.Toggle(outlineOn, I18n.Tr("fm_text_outline"));
            if (newOutline != outlineOn)
            {
                applyOutline(newOutline, outlineColor, outlineWidth);
                RefreshTextStyles();
            }
            if (newOutline)
            {
                Color newCol = DrawColorPicker(I18n.Tr("fm_text_outline_color"), outlineColor, Color.black);
                if (newCol != outlineColor)
                {
                    applyOutline(newOutline, newCol, outlineWidth);
                    RefreshTextStyles();
                }
                float newW = FloatSliderField(I18n.Tr("fm_text_outline_thickness"), outlineWidth, 0f, 1f);
                if (newW != outlineWidth)
                {
                    applyOutline(newOutline, outlineColor, newW);
                    RefreshTextStyles();
                }
            }

            bool newShadow = GUILayout.Toggle(shadowOn, I18n.Tr("fm_text_shadow"));
            if (newShadow != shadowOn)
            {
                applyShadow(newShadow, shadowColor, offX, offY, softness);
                RefreshTextStyles();
            }
            if (newShadow)
            {
                Color newCol = DrawColorPicker(I18n.Tr("fm_text_shadow_color"), shadowColor, new Color(0f, 0f, 0f, 0.5f));
                if (newCol != shadowColor)
                {
                    applyShadow(newShadow, newCol, offX, offY, softness);
                    RefreshTextStyles();
                }
                float newX = FloatSliderField(I18n.Tr("fm_text_shadow_offset_x"), offX, -20f, 20f, "F0");
                float newY = FloatSliderField(I18n.Tr("fm_text_shadow_offset_y"), offY, -20f, 20f, "F0");
                if (newX != offX || newY != offY)
                {
                    applyShadow(newShadow, shadowColor, newX, newY, softness);
                    RefreshTextStyles();
                }
                float newSoft = FloatSliderField(I18n.Tr("fm_text_shadow_softness"), softness, 0f, 16f, "F0");
                if (newSoft != softness)
                {
                    applyShadow(newShadow, shadowColor, offX, offY, newSoft);
                    RefreshTextStyles();
                }
            }
            GUILayout.EndVertical();
        }

        /// <summary>Push the new global text style onto every live text. A full rebuild would also
        /// do it, but rebuilding on every slider tick during a drag is what the debounced save path
        /// exists to avoid — re-materialing the existing texts is enough, and the overlay only needs
        /// a rebuild when per-node overrides have to re-resolve. / 把新的全局文字样式写入所有现存
        /// 文本。整体重建也能做到，但拖动时每个滑杆 tick 都重建正是去抖保存要避免的——给现存文本
        /// 换材质就够了；只有需要重新解析节点级覆盖时覆盖层才需重建。</summary>
        private void RefreshTextStyles()
        {
            UpdateAllFonts();
            if (IsCustomLayout) RequestEditorRebuild();
            SaveSettingsFromGui();
        }

        // ===== Full-keyboard KPS / Total section foldout state =====
        bool KpsTotalExpanded = false;

        // ======================== Full Keyboard (108K) KPS / Total layout section ========================
        private void DrawFullKeyboardKpsTotalSection()
        {
            KpsTotalExpanded = DrawFoldoutButton(I18n.Tr("fk_kps_total"), KpsTotalExpanded);
            if (!KpsTotalExpanded) return;
            GUILayout.BeginVertical("box");
            // Show / hide toggle (single source of truth for visibility). / 显示开关（可见性的唯一控制入口）。
            bool showKt = GUILayout.Toggle(Settings.Data.FullKeyboardShowKpsTotal, I18n.Tr("fk_show_kps_total"));
            if (showKt != Settings.Data.FullKeyboardShowKpsTotal)
            {
                Settings.Data.FullKeyboardShowKpsTotal = showKt;
                ChangeKeyViewer();
                SaveSettingsFromGui();
            }
            // KPS / Total box size (px) — layout property, kept here not in the color section. / KPS/Total 框尺寸（像素），属布局属性，置于此而非颜色栏目。
            float newKtSize = FloatSliderField(I18n.Tr("fk_kps_total_size"), Settings.Data.FullKeyboardKpsTotalSize, 40f, 400f, "F0");
            if (newKtSize != Settings.Data.FullKeyboardKpsTotalSize)
            {
                Settings.Data.FullKeyboardKpsTotalSize = newKtSize;
                ChangeKeyViewer();
                SaveSettingsFromGui();
            }
            if (Settings.Data.FullKeyboardShowKpsTotal)
            {
                // KPS / Total custom position (normalized 0-1) / KPS/Total 自定义位置（归一化 0-1）
                GUILayout.Space(5);
                GUILayout.Label(I18n.Tr("fk_kps_pos") + ":");
                Vector2 kpsPos = Settings.Data.FullKpsPosition;
                float kpsX = FloatSliderField("X", kpsPos.x, 0f, 1f);
                float kpsY = FloatSliderField("Y", kpsPos.y, 0f, 1f);
                if (kpsX != kpsPos.x || kpsY != kpsPos.y)
                {
                    Settings.Data.FullKpsPosition = new Vector2(kpsX, kpsY);
                    ApplyFullKeyboardKpsTotalPosition();
                    SaveSettingsFromGui();
                }
                GUILayout.Label(I18n.Tr("fk_total_pos") + ":");
                Vector2 totalPos = Settings.Data.FullTotalPosition;
                float totalX = FloatSliderField("X", totalPos.x, 0f, 1f);
                float totalY = FloatSliderField("Y", totalPos.y, 0f, 1f);
                if (totalX != totalPos.x || totalY != totalPos.y)
                {
                    Settings.Data.FullTotalPosition = new Vector2(totalX, totalY);
                    ApplyFullKeyboardKpsTotalPosition();
                    SaveSettingsFromGui();
                }
            }
            GUILayout.EndVertical();
        }
    }
}
