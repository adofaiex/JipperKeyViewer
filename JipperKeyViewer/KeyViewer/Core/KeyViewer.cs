using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Rain;
using JipperKeyViewer.KeyViewer.Rendering;
using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    /// <summary>
    /// Core mod controller (partial class, split across multiple files) / Mod 核心控制器（分部类，分散在多个文件中）
    /// Manages lifecycle, settings, key overlay, rain effect, and input / 管理生命周期、设置、按键覆盖层、雨滴效果和输入
    /// </summary>
    public partial class KeyViewer : MonoBehaviour
    {
        /// <summary>Global settings instance / 全局设置实例</summary>
        public static KeyViewerSettings Settings;

        // Default color values used as initial settings and reset targets / 默认颜色值，用于初始设置和重置目标
        public static readonly Color Background = new(0.5607843f, 0.2352941f, 1, 0.1960784f);
        public static readonly Color BackgroundClicked = Color.white;
        public static readonly Color Outline = new(0.5529412f, 0.2431373f, 1);
        public static readonly Color OutlineClicked = Color.white;
        public static readonly Color Text = Color.white;
        public static readonly Color TextClicked = Color.black;
        public static readonly Color RainColor = new(0.5137255f, 0.1254902f, 0.858823538f);
        public static readonly Color RainColor2 = Color.white;
        public static readonly Color RainColor3 = Color.magenta;
        public static readonly Color GhostRainColorDefault = new(1, 1, 1, 0.6f);
        public static readonly Color GhostRainColor2Default = new(1, 1, 1, 0.6f);
        public static readonly Color GhostRainColor3Default = new(1, 1, 1, 0.6f);

        public static readonly Color RainShadowColorDefault = new(0, 0, 0, 0.35f);
        public static readonly Color RainOutlineColorDefault = new(1, 1, 1, 0.5f);

        // Back-row key index mapping for each layout style / 每种布局样式的后排按键索引映射
        // Each byte array defines which keys go in the second row, in display order / 每个字节数组定义了第二排有哪些按键及其显示顺序
        public static readonly byte[] BackSequence8 = Array.Empty<byte>();
        public static readonly byte[] BackSequence10 = new byte[] { 8, 9 };
        public static readonly byte[] BackSequence12 = new byte[] { 9, 8, 10, 11 };
        public static readonly byte[] BackSequence14 = new byte[] { 13, 9, 8, 10, 11, 12 };
        public static readonly byte[] BackSequence16 = new byte[] { 12, 13, 9, 8, 10, 11, 14, 15 };
        public static readonly byte[] BackSequence20 = new byte[] { 12, 13, 9, 8, 10, 11, 14, 15, 17, 16, 18, 19 };
        public static readonly byte[] BackSequence24 = new byte[] { 12, 13, 9, 8, 10, 11, 14, 15, 17, 16, 18, 19, 21, 20, 22, 23 };

        /// <summary>Display names for main key layout selection grid / 主按键布局选择网格的显示名称</summary>
        static readonly string[] KeyLayoutNames = { "12K", "16K", "20K", "10K", "8K", "14K", "24K", "108K", "自定义/Custom" };
        /// <summary>Display names for foot key layout selection grid / 脚键布局选择网格的显示名称</summary>
        static readonly string[] FootKeyLayoutNames = { "Off", "2K", "4K", "6K", "8K", "10K", "12K", "14K", "16K" };

        /// <summary>Foot key starting index (20 for normal layouts, 24 for 24K) / 脚键起始索引</summary>
        internal static int FootKeyBase => 24;
        /// <summary>Whether the current layout exposes the third rain-parameter row / 当前布局是否显示第三排雨滴参数。
        /// Custom nodes may explicitly select RainRow=2, so their global row-3 controls must be visible;
        /// Full108 remains excluded by its intentional no-rain behavior. / Custom 节点可选择第三排，
        /// 因此显示第三排全局控件；Full108 按既定无雨滴行为排除。 </summary>
        internal static bool HasThirdRow => Settings.Data.KeyViewerStyle is KeyviewerStyle.Key20 or KeyviewerStyle.Key24 or KeyviewerStyle.Custom;
        /// <summary>Maximum key slots (keys can be at indices 0..MaxKeySlots-1) / 最大键位槽数</summary>
        internal const int MaxKeySlots = 40;
        /// <summary>Length of every per-key settings array: the 40 key slots plus the two stat
        /// panel slots (KPS at MaxKeySlots, Total at MaxKeySlots+1).
        ///
        /// This was the literal `MaxKeySlots + 2`, which was written out at every place that sizes
        /// or clamps a per-key array: the migration block, EnsureSettingsArrays, the ProfileData
        /// ctor and InitPerKeyColors, and the per-key font-size / colour panels in
        /// KeyViewerSettingsGUI.cs and KeyViewerColorGUI.cs. The ProfileData-side copies now share
        /// this constant; the GUI-side ones still spell out the expression against
        /// `KeyViewer.MaxKeySlots + 2` because those live in other partial-class files and use the
        /// bare constant. They all agree today, but a layout that grew a third stat panel, or a
        /// change to MaxKeySlots applied to some copies and not others, would silently size the
        /// per-key arrays differently from each other.
        ///
        /// The indices are ALSO produced two ways: `KeyIndex(-1)/(-2)` (Keys.Length-relative, in
        /// KeyViewerLayout.cs) and a hard-coded MaxKeySlots/MaxKeySlots+1 in CreateKeyText. They
        /// agree for every normal layout, but on Full108 they differ (KeyIndex(-1) == 105 while the
        /// other is 40) — harmless today only because nothing on that path is user-editable. The
        /// constant, both index derivations, and the seven guard predicates that answer "may I read
        /// per-key array i?" all have to move together.
        /// 每个每键设置数组的长度：40 个按键槽位加两个统计面板槽位（KPS 在 MaxKeySlots，Total 在
        /// MaxKeySlots+1）。
        ///
        /// 此前是字面量 `MaxKeySlots + 2`，写在**所有**给每键数组定长或钳制的地方：迁移块、
        /// EnsureSettingsArrays、ProfileData 构造函数与 InitPerKeyColors，以及
        /// KeyViewerSettingsGUI.cs / KeyViewerColorGUI.cs 里的每键字号与颜色面板。ProfileData 一侧
        /// 现已共用此常量；GUI 一侧仍写成 `KeyViewer.MaxKeySlots + 2` 的表达式，因为那些代码位于
        /// 其它分部类文件并使用裸常量。今天全部一致，但只要将来多出第三个统计面板、或对
        /// MaxKeySlots 的改动只落到部分副本，每键数组之间就会**静默地**长度不一。
        ///
        /// 相关下标也由**两种**方式产生：`KeyIndex(-1)/(-2)`（相对 Keys.Length，在
        /// KeyViewerLayout.cs）与 CreateKeyText 里写死的 MaxKeySlots/MaxKeySlots+1。它们对每个常规
        /// 布局都一致，但在 Full108 上不同（KeyIndex(-1) == 105 而另一个是 40）——今天无害，仅因为
        /// 那条路径上没有用户可编辑的项。常量、两种下标推导、以及回答「我可以读每键数组 i 吗？」
        /// 的七个守卫谓词，必须同步演进。
        /// </summary>
        internal const int PerKeySlotCount = MaxKeySlots + 2;
        /// <summary>Whether the current layout is the full 108-key keyboard / 当前布局是否为全键盘</summary>
        internal static bool IsFullKeyboard => Settings.Data.KeyViewerStyle == KeyviewerStyle.Full108;
        /// <summary>Whether the current layout is the FreeMake custom node layout / 当前布局是否为 FreeMake 自定义节点布局</summary>
        internal static bool IsCustomLayout => Settings.Data.KeyViewerStyle == KeyviewerStyle.Custom;
        /// <summary>Number of main keys for the current layout (40 normal, 108 full) / 当前布局的主键数</summary>
        private static int GetKeyCount()
        {
            if (IsFullKeyboard) return Settings.Data.key108.Length;
            if (IsCustomLayout) return CustomKeyNodeCount();
            return MaxKeySlots;
        }

        /// <summary>Default 108-key physical keyboard bindings, indexed by Full108 array slot / 全键盘默认键位绑定，下标对应 Full108 数组槽位</summary>
        internal static KeyCode[] BuildDefaultKey108()
        {
            // 105 keys, index-aligned with the slot list in InitializeFullKeyboard (function / number / QWERTY / ASDF / ZXCV / bottom / edit / arrows / numpad).
            return new KeyCode[]
            {
                KeyCode.Escape,
                KeyCode.F1,
                KeyCode.F2,
                KeyCode.F3,
                KeyCode.F4,
                KeyCode.F5,
                KeyCode.F6,
                KeyCode.F7,
                KeyCode.F8,
                KeyCode.F9,
                KeyCode.F10,
                KeyCode.F11,
                KeyCode.F12,
                KeyCode.Print,
                KeyCode.ScrollLock,
                KeyCode.Pause,
                KeyCode.SysReq,
                KeyCode.BackQuote,
                KeyCode.Alpha1,
                KeyCode.Alpha2,
                KeyCode.Alpha3,
                KeyCode.Alpha4,
                KeyCode.Alpha5,
                KeyCode.Alpha6,
                KeyCode.Alpha7,
                KeyCode.Alpha8,
                KeyCode.Alpha9,
                KeyCode.Alpha0,
                KeyCode.Minus,
                KeyCode.Equals,
                KeyCode.Backspace,
                KeyCode.Tab,
                KeyCode.Q,
                KeyCode.W,
                KeyCode.E,
                KeyCode.R,
                KeyCode.T,
                KeyCode.Y,
                KeyCode.U,
                KeyCode.I,
                KeyCode.O,
                KeyCode.P,
                KeyCode.LeftBracket,
                KeyCode.RightBracket,
                KeyCode.Backslash,
                KeyCode.CapsLock,
                KeyCode.A,
                KeyCode.S,
                KeyCode.D,
                KeyCode.F,
                KeyCode.G,
                KeyCode.H,
                KeyCode.J,
                KeyCode.K,
                KeyCode.L,
                KeyCode.Semicolon,
                KeyCode.Quote,
                KeyCode.Return,
                KeyCode.LeftShift,
                KeyCode.Z,
                KeyCode.X,
                KeyCode.C,
                KeyCode.V,
                KeyCode.B,
                KeyCode.N,
                KeyCode.M,
                KeyCode.Comma,
                KeyCode.Period,
                KeyCode.Slash,
                KeyCode.RightShift,
                KeyCode.LeftControl,
                KeyCode.LeftWindows,
                KeyCode.LeftAlt,
                KeyCode.Space,
                KeyCode.RightAlt,
                KeyCode.RightWindows,
                KeyCode.Menu,
                KeyCode.RightControl,
                KeyCode.Insert,
                KeyCode.Delete,
                KeyCode.Home,
                KeyCode.End,
                KeyCode.PageUp,
                KeyCode.PageDown,
                KeyCode.UpArrow,
                KeyCode.LeftArrow,
                KeyCode.DownArrow,
                KeyCode.RightArrow,
                KeyCode.Numlock,
                KeyCode.KeypadDivide,
                KeyCode.KeypadMultiply,
                KeyCode.KeypadMinus,
                KeyCode.Keypad7,
                KeyCode.Keypad8,
                KeyCode.Keypad9,
                KeyCode.KeypadPlus,
                KeyCode.Keypad4,
                KeyCode.Keypad5,
                KeyCode.Keypad6,
                KeyCode.Keypad1,
                KeyCode.Keypad2,
                KeyCode.Keypad3,
                KeyCode.Keypad0,
                KeyCode.KeypadPeriod,
                KeyCode.KeypadEnter
            };
        }

        /// <summary>
        /// Static constructor: pre-compute AllKeyCodes (all non-Joystick keys) for input detection / 静态构造函数：预计算 AllKeyCodes（所有非摇杆按键），用于按键检测
        /// </summary>
        static KeyViewer()
        {
            var all = (KeyCode[])Enum.GetValues(typeof(KeyCode));
            AllKeyCodes = Array.FindAll(all, k => !k.ToString().StartsWith("Joystick"));
        }

        // --- Instance fields ---

        /// <summary>Root canvas GameObject for the key overlay / 按键覆盖层的根画布 GameObject</summary>
        GameObject KeyViewerObject;
        /// <summary>Child GameObject that applies the Size scale transform / 应用大小缩放的子 GameObject</summary>
        GameObject KeyViewerSizeObject;
        /// <summary>Merged background-shape layer (owns slot state) / 合并背景形状层（持有槽位状态）</summary>
        KeyShapeLayer keyShapeLayer;
        /// <summary>Merged outline-shape layer (shares state with keyShapeLayer) / 合并描边形状层（与背景层共享状态）</summary>
        KeyShapeLayer keyOutlineLayer;
        /// <summary>Quartz-style cached soft-glow layer for FreeMake nodes / FreeMake 节点缓存柔光层</summary>
        Transform keyGlowLayer;
        /// <summary>Cached glow Images for fixed-layout keys / 固定布局按键的缓存光效 Image</summary>
        readonly Dictionary<Key, Image> fixedGlowImages = new Dictionary<Key, Image>();
        /// <summary>Sub-canvas holding all key texts (isolates text rebatching from shape layer) / 持有全部按键文本的子画布（文本重批与形状层隔离）</summary>
        Transform textLayer;
        /// <summary>Merged rain layer (solid quads: normal bodies + ghost shadow/outline) / 合并雨滴层（纯色四边形：普通本体 + 鬼雨阴影/描边）</summary>
        RainLayer rainLayer;
        /// <summary>Merged ghost rain layer (ghost sprite bodies) / 合并鬼雨层（鬼雨贴图本体）</summary>
        GhostRainLayer ghostRainLayer;
        /// <summary>The overlay canvas (ScreenSpaceOverlay) / 覆盖层画布</summary>
        Canvas Canvas;
        /// <summary>All key instances (index 0-19 main, 20-35 foot) / 所有按键实例（0-19 主键，20-35 脚键）</summary>
        Key[] Keys;
        /// <summary>KPS display key / KPS 显示按键</summary>
        Key Kps;
        /// <summary>Last frame's KPS value for change detection / 上一帧的 KPS 值，用于变化检测</summary>
        int lastKps;
        /// <summary>Last frame's total count for change detection / 上一帧的总计数，用于变化检测</summary>
        int lastTotal;
        /// <summary>Total count display key / 总计数显示按键</summary>
        Key Total;
        /// <summary>Queue of press timestamps for KPS calculation / 按下时间戳队列，用于 KPS 计算</summary>
        Queue<long> PressTimes;
        /// <summary>Per-key press timestamp queues for per-key KPS / 每键按下时间戳队列，用于每键 KPS</summary>
        Queue<long>[] keyPressTimes;
        /// <summary>Last frame per-key KPS values for change detection / 上一帧每键 KPS 值，用于变化检测</summary>
        int[] lastPerKeyKps;
        /// <summary>High-resolution stopwatch for timing / 用于计时的高精度秒表</summary>
        Stopwatch Stopwatch;
        /// <summary>Timestamp of last frame for delta calculation / 上一帧的时间戳，用于增量计算</summary>
        /// <summary>Whether the key change section in settings is expanded / 设置中按键更改区域是否展开</summary>
        bool KeyChangeExpanded;
        /// <summary>Whether the ghost rain key section in settings is expanded / 设置中鬼键区域是否展开</summary>
        bool GhostRainChangeExpanded;
        /// <summary>Whether the text change section in settings is expanded / 设置中文本更改区域是否展开</summary>
        bool TextChangeExpanded;
        /// <summary>Whether the rain effect section in settings is expanded / 设置中雨线效果区域是否展开</summary>
        bool RainExpanded;
        /// <summary>Per-color-section expanded state in settings / 设置中每个颜色区域的展开状态</summary>
        bool[] ColorExpanded;
        /// <summary>Whether the custom position section in settings is expanded / 设置中自定义位置区域是否展开</summary>
        bool CustomPositionExpanded;
        /// <summary>Currently selected key index for rebinding (-1 = none) / 当前为重新绑定选中的按键索引（-1 = 无）</summary>
        int SelectedKey = -1;
        /// <summary>Current rebind mode: 0=key, 1=text, 2=ghost key / 当前重绑定模式：0=按键，1=文本，2=鬼键</summary>
        int changeState;

        /// <summary>Path to the settings JSON file / 设置 JSON 文件路径</summary>
        static string ConfigPath
        {
            get
            {
                if (configPath == null)
                {
                    // ResolveModPath, not ModPath: the old `?? "."` made ModPath non-null always, so
                    // the persistentDataPath fallback below was unreachable dead code and a missing
                    // loader wrote config into the GAME INSTALL FOLDER.
                    // 用 ResolveModPath 而非 ModPath：旧的 `?? "."` 让 ModPath 永远非 null，下面的
                    // persistentDataPath 兜底是不可达的死代码，缺加载器时会把配置写进**游戏安装
                    // 目录**。
                    configPath = Path.Combine(Loader.ResolveModPath(), "config", "settings.json");
                }
                return configPath;
            }
        }
        static string configPath;

        /// <summary>Path to the profiles directory / 配置目录路径</summary>
        static string ProfileDir
        {
            get
            {
                if (profileDir == null)
                {
                    profileDir = Path.Combine(Loader.ResolveModPath(), "config", "profiles");
                }
                return profileDir;
            }
        }
        static string profileDir;

        /// <summary>Drop the cached mod-folder-derived paths so they follow the active loader /
        /// 丢弃由 Mod 目录派生出的缓存路径，使其跟随当前加载器</summary>
        internal static void ResetCachedPaths()
        {
            configPath = null;
            profileDir = null;
            // packagesDir lives in the KeyViewerPackages part of this same partial class.
            // packagesDir 定义在本 partial 类的 KeyViewerPackages 部分。
            packagesDir = null;
        }

        /// <summary>Sanitize a profile name for use as a filename / 将配置名称净化用于文件名</summary>
        static string SanitizeFileName(string name)
        {
            // A null name used to NRE inside Replace(); callers include GetProfilePath, which can be
            // reached with a null profile name from a corrupt meta. Treat it as "no name".
            // null 名称曾在 Replace() 内抛 NRE；GetProfilePath 等调用方可能从损坏的 meta 传入 null。
            if (string.IsNullOrWhiteSpace(name)) return "Unnamed";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "Unnamed" : name;
        }

        /// <summary>Get the full path to a profile JSON file / 获取配置 JSON 文件的完整路径</summary>
        static string GetProfilePath(string name) => Path.Combine(ProfileDir, SanitizeFileName(name) + ".json");

        /// <summary>Cached background sprite from AssetBundle / 从 AssetBundle 缓存的背景精灵</summary>
        Sprite keyBackgroundSprite;
        /// <summary>Cached outline sprite from AssetBundle / 从 AssetBundle 缓存的轮廓精灵</summary>
        Sprite keyOutlineSprite;
        /// <summary>Cached ghost rain sprite (loaded from PNG file) / 从 PNG 文件缓存的鬼雨精灵</summary>
        Sprite ghostRainSprite;
        /// <summary>Singleton instance reference / 单例实例引用</summary>
        public static KeyViewer instance;
        /// <summary>Rain effect system (object-pooled, zero-GC on hot path) / 雨滴效果系统</summary>
        private RainSystem rainSystem;
        /// <summary>Font name → index lookup dictionary / 字体名称到索引的查找字典</summary>
        static Dictionary<string, int> fontNameIndex;
        /// <summary>All non-joystick KeyCodes, cached for input detection / 所有非摇杆按键代码缓存，用于按键检测。
        /// Public because the loader entry points need the SAME list: MelonLoader's hotkey capture
        /// called Enum.GetValues, which allocates a fresh ~500-element array on every capture.
        /// 公开是因为各加载器入口需要**同一份**列表：MelonLoader 的热键捕获此前调
        /// Enum.GetValues，每次捕获都要分配一个约 500 元素的数组。</summary>
        public static readonly KeyCode[] AllKeyCodes;
        /// <summary>Cached current style to avoid redundant GetKeyCode calls / 缓存当前样式，避免重复调用 GetKeyCode</summary>
        private KeyviewerStyle cachedKeyStyle = (KeyviewerStyle)(-1);
        /// <summary>Cached main key array / 缓存的主按键数组</summary>
        private KeyCode[] cachedMainKeys;
        /// <summary>Cached current foot style / 缓存当前的脚键样式</summary>
        private FootKeyviewerStyle cachedFootStyle = (FootKeyviewerStyle)(-1);
        /// <summary>Cached foot key array / 缓存的脚键数组</summary>
        private KeyCode[] cachedFootKeys;
        /// <summary>Cached ghost key array / 缓存的鬼键数组</summary>
        private KeyCode[] cachedGhostKeys;
        /// <summary>Ghost key press state tracking / 鬼键按下状态跟踪</summary>
        private bool[] ghostKeyStates;
        /// <summary>MapleStory font loaded from AssetBundle / 从 AssetBundle 加载的 MapleStory 字体</summary>
        private TMP_FontAsset mapleFont;
        /// <summary>Cache of text-style materials keyed by the RESOLVED outline/shadow style, so
        /// every combination (on/off, colors, widths, offsets) gets its own material instead of
        /// one hard-coded underlay. / 以「解析后的描边/阴影样式」为键的文字样式材质缓存，使每种
        /// 组合（开关、颜色、粗细、偏移）各有材质，取代单一写死的 underlay。</summary>
        private Dictionary<long, Material> textStyleMaterials = new Dictionary<long, Material>();
        /// <summary>Per-font material for the neutral style (no outline, no shadow) — the font's
        /// own material. / 中性样式（无描边无阴影）的每字体材质——即字体自带材质。</summary>
        private Dictionary<TMP_FontAsset, Material> neutralFontMaterials = new Dictionary<TMP_FontAsset, Material>();
        /// <summary>How many live TMP_Text objects currently render with each cached text-style
        /// material (keyed by Material.GetInstanceID). Only materials nobody is using may be
        /// destroyed — evicting one that a text still renders with makes that text go blank. /
        /// 每个缓存文字样式材质当前被多少个存活 TMP_Text 使用（以 Material.GetInstanceID 为键）。
        /// 只有无人使用的材质才可销毁——回收仍在使用的材质会让对应文本变成空白。</summary>
        private readonly Dictionary<int, int> textStyleMaterialRefs = new Dictionary<int, int>();
        /// <summary>Which cached material each TMP_Text is currently pointed at, so switching a text
        /// to a new style releases the old one. / 每个 TMP_Text 当前指向哪个缓存材质，便于切换样式
        /// 时释放旧引用。</summary>
        private readonly Dictionary<TMP_Text, int> textStyleMaterialUse = new Dictionary<TMP_Text, int>();
        private readonly List<TMP_Text> textStyleEvictScratch = new List<TMP_Text>();
        /// <summary>Separate scratch for evicted cache KEYS. The text scratch above is already in
        /// use in the same method, and reusing one list for two element types would corrupt the
        /// pending removals. / 淘汰缓存**键**的独立暂存。上面的文本暂存在同一方法里已被占用，
        /// 一个列表存两种元素类型会破坏待删除项。</summary>
        private readonly List<long> textStyleEvictKeyScratch = new List<long>();
        /// <summary>List of all available fonts (built-in + custom) / 所有可用字体列表（内置 + 自定义）</summary>
        static readonly List<FontEntry> fontList = new List<FontEntry>();
        /// <summary>Whether the font selection list is expanded in settings / 设置中字体选择列表是否展开</summary>
        bool fontListExpanded;
        bool fontStyleExpanded;
        /// <summary>Whether the overlay was enabled last frame (for toggle detection) / 上一帧覆盖层是否启用（用于开关检测）</summary>
        private bool wasEnabled;
        /// <summary>Whether the font has been restored after scene load / 场景加载后字体是否已恢复</summary>
        private bool fontRestored;
        /// <summary>Whether any key press occurred recently (skip idle per-key KPS loop) / 最近是否有按键（跳过空闲的每键 KPS 循环）</summary>
        private bool _hasKeyPressActivity;

        // ======================== Unity Lifecycle / Unity 生命周期 ========================

        /// <summary>
        /// Initialize the mod: load settings, i18n, resources / 初始化 Mod：加载设置、国际化、资源
        /// </summary>
        void Awake()
        {
            instance = this;
            // Install the bridge KvTextStyle.Apply routes through, so a font-material assignment
            // from there keeps the material cache's reference count just like the direct
            // ApplyFontMaterial call sites do. / 安装 KvTextStyle.Apply 转发所用的桥接，使那里的
            // 字体材质赋值与直接调用 ApplyFontMaterial 一样维护材质缓存的引用计数。
            Rendering.KvTextStyle.KeyViewerApplier.Apply = (t, m) => ApplyFontMaterial(t, m);
            LoadSettings();
            I18n.Lang = Settings.Language;
            rainSystem = new RainSystem(Settings);
            TryLoadResources();
            wasEnabled = Settings.Data.Enabled;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        /// <summary>
        /// Restore the user's font selection after scene load (once per scene) / 场景加载后恢复用户字体选择（每场景一次）
        /// </summary>
        void RestoreFontOnce()
        {
            if (fontNameIndex == null || fontRestored || string.IsNullOrEmpty(Settings.Data.FontName)) return;
            if (fontNameIndex.TryGetValue(Settings.Data.FontName, out int idx))
            {
                Settings.Data.FontIndex = idx;
                UpdateAllFonts();
                SaveSettings();
            }
            fontRestored = true;
        }

        /// <summary>
        /// Called when the GameObject becomes active / GameObject 变为活跃时调用
        /// </summary>
        void OnEnable()
        {
            if (Settings.Data.Enabled) EnableKeyViewer();
            else DisableKeyViewer();
            if (Settings.Data.CustomPositionEnabled)
            {
                ResetKeyViewerPosition();
                ResetFootKeyViewerPosition();
            }
        }

        /// <summary>
        /// Called when the GameObject becomes inactive / GameObject 变为不活跃时调用
        /// </summary>
        void OnDisable()
        {
            SaveSettings();
            DisableKeyViewer();
        }

        /// <summary>
        /// Flush any pending debounced save before shutdown (UMM has no quit hook; Melon's
        /// OnApplicationQuit also calls SaveSettings — the double save is harmless). /
        /// 关闭前冲刷挂起的去抖保存（UMM 没有退出钩子；Melon 的 OnApplicationQuit 也会调用
        /// SaveSettings——重复保存无害）。
        /// </summary>
        void OnApplicationQuit()
        {
            if (Settings != null) SaveSettings();
        }

        /// <summary>
        /// Called when the GameObject is destroyed / GameObject 被销毁时调用
        /// </summary>
        void OnDestroy()        {
            SaveSettings();
            instance = null; // stop loader GUI callbacks from running on the destroyed component / 阻止加载器 GUI 回调继续在已销毁组件上运行
            SceneManager.sceneLoaded -= OnSceneLoaded;
            rainSystem?.ClearAll(Keys);
            ReleaseTextStyleMaterials();
            // Unhook the static bridge KvTextStyle.Apply routes through. It is an ASSIGNMENT, so it
            // never double-registered — but it left a static field holding this (now destroyed)
            // component for the rest of the process, and any Apply call after teardown would write
            // through it. / 摘掉 KvTextStyle.Apply 转发所用的静态桥接。它是**赋值**故从不重复注册，
            // 但会让静态字段在本组件销毁后仍持有它直到进程结束，此后任何 Apply 调用都会写向死组件。
            if (Rendering.KvTextStyle.KeyViewerApplier.Apply != null)
                Rendering.KvTextStyle.KeyViewerApplier.Apply = null;
        }

        /// <summary>
        /// Called when a new scene is loaded: save counts, clean up rain, re-link fallback fonts / 新场景加载时调用：保存计数、清理雨滴、重新链接后备字体
        /// </summary>
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            SaveSettings();
            for (int i = fontList.Count - 1; i >= 0; i--)
                if (fontList[i].font == null) fontList.RemoveAt(i);
            // Pruning shifts every later entry — rebuild the name→index map, or RestoreFontOnce
            // would resolve the stored FontName against stale indices and silently switch (and
            // persist) the wrong font. / 剔除会让后续条目全部前移——重建名称→索引映射,否则
            // RestoreFontOnce 会拿过期索引解析存储的 FontName,静默切到(并持久化)错误字体。
            if (fontNameIndex != null)
            {
                fontNameIndex.Clear();
                for (int i = 0; i < fontList.Count; i++)
                    fontNameIndex[fontList[i].name] = i;
            }
            if (fontList.Count == 0 || Settings.Data.FontIndex >= fontList.Count)
                Settings.Data.FontIndex = 0;
            fontRestored = false;
            LinkFallbackFonts();
            rainSystem.ClearActiveDrops(Keys);
            // RestoreFontOnce re-maps FontName to a valid index — the pruning above may have shifted
            // the list. It used to run only from Start, so the per-scene reset above was dead logic.
            // RestoreFontOnce 把 FontName 重新映射为有效索引——上面的清理可能使列表移位。
            // 此前它只从 Start 调用，上面的每场景重置是死逻辑。
            RestoreFontOnce();
        }

        /// <summary>
        /// Start is called after OnEnable; restore font selection / Start 在 OnEnable 之后调用；恢复字体选择
        /// </summary>
        void Start()
        {
            RestoreFontOnce();
        }

        /// <summary>
        /// Main update loop: input detection, KPS calculation, rain effect update / 主更新循环：按键检测、KPS 计算、雨滴效果更新
        /// </summary>
        void Update()
        {
            // Flush debounced GUI saves regardless of the focus/enable gates below — a pending
            // save must not be lost just because the window lost focus or the overlay is off.
            // 无条件落盘挂起的 GUI 去抖保存——不能因失焦或覆盖层关闭而丢失待写变更。
            FlushGuiSaveIfNeeded();

            // Skip all processing when game window is not focused / 窗口未激活时跳过所有处理
            if (!Application.isFocused) return;

            bool enabled = Settings.Data.Enabled;
            // Detect toggle change for enabled/disabled / 检测启用/禁用状态切换
            if (wasEnabled != enabled)
            {
                if (enabled)
                {
                    EnableKeyViewer();
                    if (Settings.Data.CustomPositionEnabled)
                    {
                        ResetKeyViewerPosition();
                        ResetFootKeyViewerPosition();
                    }
                }
                else DisableKeyViewer();
                wasEnabled = enabled;
            }
            if (KeyViewerObject != null && enabled)
            {
                RunStage("resolution", CheckResolutionChanged);
                long now = Stopwatch.ElapsedMilliseconds;
                RunStage("key selection", ProcessKeySelection);   // Handle key rebinding input / 处理按键重新绑定输入
                if (IsCustomLayout)
                {
                    // FreeMake nodes: bindings/counters live on the nodes; ghosts included. /
                    // FreeMake 节点：绑定与计数在节点上，鬼键一并处理。
                    RunStage("custom keys", () => ProcessCustomKeysInUpdate(now));
                }
                else
                {
                    RunStage("main/foot keys", () => ProcessMainAndFootKeysInUpdate(now)); // Detect key presses / 检测按键按下
                    RunStage("ghost keys", ProcessGhostKeysInUpdate);          // Process ghost key inputs / 处理鬼键输入
                    if (Settings.Data.EnableRainEffect) RunStage("rain", () => rainSystem.UpdateEffects(Keys)); // Update rain drop positions / 更新雨滴位置
                    else rainSystem.ClearActiveDrops(Keys); // 清掉在途雨滴
                }
                RunStage("kps", () => ProcessKpsInUpdate(now));            // Update KPS counter / 更新 KPS 计数器
                RunStage("per-key kps", () => ProcessPerKeyKpsInUpdate(now));       // Update per-key KPS / 更新每键 KPS
                if (IsCustomLayout) RunStage("counter bounce", TickCounterBounces); // counter bounce animations / 计数器弹跳动画
                RunStage("text gradients", TickTextGradients); // static glyph gradients / 静态字形渐变
            }
        }

        /// <summary>Run one per-frame subsystem so a throw in it cannot take the others down with
        /// it, and so it is reported once rather than every frame. Unity catches an exception out
        /// of Update and carries on next frame, so a persistently faulting stage is a per-frame
        /// log flood AND it silently cancels the rest of that frame: press counting, KPS, rain and
        /// the glyph gradients all stop for as long as it lasts, with no user-visible signal beyond
        /// the log. Segmented guards keep an unrelated subsystem alive and turn the failure into a
        /// single reported line that re-arms if the message changes (so a genuinely new fault is
        /// still surfaced).
        /// 运行单个逐帧子系统，使其抛出时不拖垮其余部分，且只**报告一次**而非每帧。Unity 会捕获
        /// Update 中逃出的异常并在下一帧继续，故一个持续故障的阶段既是逐帧日志洪水，也会静默
        /// 取消该帧的其余工作：按键计数、KPS、雨滴与字形渐变在它持续期间全部停止，用户侧除了
        /// 日志没有任何信号。分段守卫让无关子系统继续存活，并把故障变成一行报告；消息变化时
        /// 重新武装，使真正的新故障仍会被暴露。
        /// </summary>
        private void RunStage(string stage, Action work)
        {
            if (work == null) return;
            try
            {
                work();
                if (stageFailures.Remove(stage))
                    Loader.Warning($"KeyViewer: '{stage}' recovered after an earlier failure");
            }
            catch (Exception e)
            {
                // Report the FIRST failure of a given message, then stay quiet while it repeats.
                // A stage that fails every frame would otherwise bury every other log line — the
                // same trap the MelonLoader OnUpdate NullReferenceException used to be.
                // 同一消息的首次失败才报告，随后重复时保持安静。一个每帧都失败的阶段否则会淹没
                // 所有其它日志行——正是 MelonLoader OnUpdate 那个 NullReferenceException 曾经的陷阱。
                string message = e.GetType().Name + ": " + e.Message;
                if (!stageFailures.TryGetValue(stage, out string last) || last != message)
                {
                    stageFailures[stage] = message;
                    Loader.Error($"KeyViewer: per-frame stage '{stage}' failed — {message}");
                }
            }
        }

        private readonly Dictionary<string, string> stageFailures = new Dictionary<string, string>();

        // ======================== Config Management / 配置管理 ========================

        private void LoadSettings()
        {
            string directory = Path.GetDirectoryName(ConfigPath);
            // Outside the try below: if the config directory cannot be created the exception escaped
            // Awake, leaving Settings null — and every later Settings.Data access then NREd, so the
            // mod failed to initialize with no useful message. Keep the mod alive on defaults and
            // show the failure instead. / 位于下方 try 之外：配置目录无法创建时异常会逃出 Awake，
            // Settings 保持 null——此后每次 Settings.Data 访问都 NRE，Mod 带着无意义的信息初始化
            // 失败。改为用默认值继续并显示失败。
            try
            {
                if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            }
            catch (Exception e)
            {
                Loader.Error($"KeyViewer: cannot create the config directory '{directory}': {e.Message}");
                lastSaveError = e.Message;
                Settings = new KeyViewerSettings();
                return;
            }

            if (!File.Exists(ConfigPath))
            {
                Settings = new KeyViewerSettings();
                SaveSettings();
                return;
            }

            try
            {
                string json = File.ReadAllText(ConfigPath);
                Settings = JsonUtility.FromJson<KeyViewerSettings>(json);
                if (Settings == null)
                {
                    Loader.Error("Failed to parse settings file (empty or corrupt), creating new settings");
                    BackupCorruptConfig();
                    Settings = new KeyViewerSettings();
                    return;
                }
                // JsonUtility.FromJson does NOT run field initializers — the meta JSON never
                // carries "Data", so Data would be null here and the legacy overwrite below
                // would NRE. / JsonUtility.FromJson 不运行字段初始化器——meta JSON 永远没有
                // "Data"，此处 Data 会是 null，随后的旧版覆盖会 NRE。
                if (Settings.Data == null) Settings.Data = new ProfileData();
                if (Settings.CurrentProfile == null) Settings.CurrentProfile = "Default";

                // Backward compat: old flat JSON had profile fields directly on KeyViewerSettings,
                // now they live in ProfileData. Overwrite Data from the flat JSON to preserve them.
                JsonUtility.FromJsonOverwrite(json, Settings.Data);

                // Meta version as stored on disk, before any migration bumps it. v5→v6 uses it to
                // decide whether profile files predate the full-keyboard KPS/Total feature (fields
                // absent → ctor defaults, must not be flipped).
                // 磁盘上的原始 meta 版本号,先于任何迁移提升。v5→v6 据此判断 Profile 文件是否
                // 早于全键盘 KPS/Total 功能(字段缺失 → 构造默认值,不可翻转)。
                int metaVersionOnDisk = Settings.Version;
                // JsonUtility does not run field initializers, so a settings.json that is missing
                // "Version" (hand-edited, truncated, renamed field) deserializes it as 0 — which
                // would re-run the ENTIRE migration chain from v1, and MigrateV2toV3 resets the
                // profile list. A missing version means "unknown", not "v1": treat it as current
                // and let the per-profile DataVersion guards handle whatever actually needs work.
                // JsonUtility 不运行字段初始化器，因此缺 "Version" 的 settings.json（手改、截断、
                // 字段改名）会反序列化为 0——那会让整条迁移链从 v1 重跑，而 MigrateV2toV3 会重置
                // 配置列表。缺版本号意味着"未知"而非"v1"：按当前版本处理，真正的补齐交给逐
                // Profile 的 DataVersion 守卫。
                if (metaVersionOnDisk <= 0) metaVersionOnDisk = Settings.Version;
                if (Settings.Version <= 0) Settings.Version = KeyViewerSettings.CurrentVersion;

                if (Settings.Version < 2) MigrateV1toV2();
                if (Settings.Version < 3) MigrateV2toV3();
                LoadProfileFromMeta();
                if (Settings.Version < 4) MigrateV3toV4();
                if (Settings.Version < 5) MigrateV4toV5();
                if (Settings.Version < 6) MigrateV5toV6(metaVersionOnDisk);

                EnsureSettingsArrays();
                // Startup diagnostic: makes stale-DLL / lost-config situations immediately
                // visible in the log. / 启动诊断：旧 DLL 或配置丢失在日志里立即可见。
                Loader.Log($"KeyViewer: profile '{Settings.CurrentProfile}' loaded ({Settings.Data.CustomNodes.Count} custom nodes, {Settings.Data.LayerGroups.Count} layer groups)");
                SyncProfilesWithDisk();
                settingsGuiTab = Mathf.Clamp(Settings.UiTab, 0, TabCount - 1);
            }
            catch (Exception e) when (IsStorageFailure(e))
            {
                // A full disk, a read-only profile folder or a file locked by a sync client throws
                // here just as readily as a corrupt JSON does — the migration chain calls
                // SaveCurrentProfile/SaveMetaOnly, and those throw IOException. The catch-all used
                // to treat that as "config is corrupt": it reset Settings to defaults, so the very
                // next SaveSettings (on every scene load) overwrote the user's real profile with
                // default values, and it discarded the "roll the meta version back so we retry"
                // bookkeeping the migrations rely on. Keep whatever loaded successfully, surface the
                // failure, and retry the save later.
                // 磁盘满、Profile 目录只读或文件被同步软件占用时，迁移链里的
                // SaveCurrentProfile/SaveMetaOnly 抛出的 IOException 会和配置损坏一样到达这里。
                // 此前 catch-all 一律当成"配置损坏"：把 Settings 重置为默认值，于是下一次
                // SaveSettings（每次场景加载都跑）就用默认值覆盖用户的真实配置，并且废掉了迁移
                // 依赖的"回滚 meta 版本号以便重试"记账。现保留已加载的状态、显示失败并稍后重试。
                Loader.Error($"Settings load hit a storage error (kept the loaded state): {e.Message}");
                lastSaveError = e.Message;
            }
            catch (Exception e)
            {
                Loader.Error($"Failed to load settings: {e.Message}");
                // Back up the offending files before falling back to defaults — the next SaveSettings
                // would otherwise overwrite them and permanently lose the user's config (an old
                // mid-migration crash used to wipe profiles this way).
                // 回退默认前先备份出问题的文件——否则下一次 SaveSettings 会直接覆盖，用户的配置就
                // 永久丢了（旧版本迁移中途崩溃曾以此方式清空配置）。
                BackupCorruptConfig();
                Settings = new KeyViewerSettings();
            }
        }

        /// <summary>Storage-layer failure (disk full, permissions, sharing violation, a sync client
        /// holding the file) rather than a malformed document. These must never be treated as
        /// "the user's config is corrupt" — the files are fine, the machine just could not read or
        /// write them right now. / 存储层故障（磁盘满、权限、共享冲突、同步软件占用），而不是文档
        /// 格式损坏。绝不能当成"用户配置损坏"——文件本身没问题，只是这台机器此刻读写不了。</summary>
        private static bool IsStorageFailure(Exception e)
        {
            for (Exception cur = e; cur != null; cur = cur.InnerException)
            {
                if (cur is IOException || cur is UnauthorizedAccessException
                    || cur is System.Security.SecurityException) return true;
            }
            return false;
        }

        /// <summary>Copy the config meta + current profile to *.corrupt backups before falling back to defaults / 回退默认前把配置元数据与当前 Profile 备份为 *.corrupt</summary>
        private void BackupCorruptConfig()
        {
            try
            {
                RotateCorruptBackup(ConfigPath);
                string cur = Settings?.CurrentProfile;
                if (!string.IsNullOrEmpty(cur))
                {
                    RotateCorruptBackup(GetProfilePath(cur));
                }
            }
            catch { /* best-effort backup; the load failure is already reported / 尽力备份；加载失败已另行报告 */ }
        }

        /// <summary>Snapshot a damaged config file as &lt;path&gt;.corrupt, keeping the PREVIOUS
        /// backup as &lt;path&gt;.corrupt.1. Every corrupt-recovery site used File.Copy(..., true),
        /// which overwrote the last known-good copy — so a single transient failure (a half-written
        /// file, a sync client mid-write) destroyed the one backup that was still good. / 把损坏的
        /// 配置文件快照为 &lt;path&gt;.corrupt，同时保留**上一份**备份为 &lt;path&gt;.corrupt.1。
        /// 此前所有损坏恢复点都用 File.Copy(..., true) 覆盖上一份——一次瞬时故障（半截文件、同步
        /// 软件写到一半）就会毁掉唯一仍然完好的那份备份。</summary>
        private static void RotateCorruptBackup(string path)
        {
            if (!File.Exists(path)) return;
            string backup = path + ".corrupt";
            string previous = path + ".corrupt.1";
            if (File.Exists(backup))
            {
                try { File.Delete(previous); } catch { /* best effort / 尽力而为 */ }
                try { File.Move(backup, previous); } catch { /* fall through to a plain copy / 退回普通复制 */ }
            }
            File.Copy(path, backup, true);
        }

        private void MigrateV1toV2()
        {
            const float refW = 1920f, refH = 1080f;
            float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);
            // Idempotence guard: if the meta's Version field was ever lost/reset while the stored
            // positions were already normalized, dividing again would collapse everything to the
            // top-left corner. v1 positions were raw pixels (0..1920/0..1080) — values already
            // inside [0,1] on both axes are normalized v2 data, skip the rescale.
            // 幂等守卫:若 meta 的 Version 曾丢失/重置而存量坐标已是归一化值,再除一次会把
            // 所有位置压到左上角。v1 坐标是原始像素(0..1920/0..1080)——两轴都落在 [0,1] 内
            // 即为已归一化的 v2 数据,跳过缩放。
            var p1 = Settings.Data.MainKeyViewerPosition;
            var p2 = Settings.Data.FootKeyViewerPosition;
            bool alreadyNormalized = p1.x >= 0f && p1.x <= 1f && p1.y >= 0f && p1.y <= 1f
                && p2.x >= 0f && p2.x <= 1f && p2.y >= 0f && p2.y <= 1f;
            if (!alreadyNormalized)
            {
                Settings.Data.MainKeyViewerPosition = new Vector2(
                    Clamp01(p1.x / refW),
                    1f - Clamp01(p1.y / refH));
                Settings.Data.FootKeyViewerPosition = new Vector2(
                    Clamp01(p2.x / refW),
                    1f - Clamp01(p2.y / refH));
            }
            Settings.Version = 2;
            if (Settings.Data.DataVersion < 2) Settings.Data.DataVersion = 2;
        }

        private void MigrateV2toV3()
        {
            Loader.Log("Migrating settings v2 → v3: creating Default profile");
            Settings.Version = 3;
            // Only synthesize the profile list when there is none. Unconditionally overwriting it
            // meant that any later failure (a full disk makes SaveMetaOnly throw) made the next
            // launch re-run this migration and wipe the user's profile list — the exact damage a
            // retry is supposed to avoid.
            // 仅在列表为空时补建。此前无条件覆盖：一旦后续失败（磁盘满时 SaveMetaOnly 抛异常），
            // 下次启动重跑本迁移就会清空用户的配置列表——而重试本该避免这种破坏。
            if (Settings.ProfileNames == null || Settings.ProfileNames.Length == 0)
            {
                Settings.CurrentProfile = "Default";
                Settings.ProfileNames = new[] { "Default" };
            }
            EnsureSettingsArrays();
            if (Settings.Data.DataVersion < 3) Settings.Data.DataVersion = 3;
            try
            {
                SaveCurrentProfile();
                SaveMetaOnly();
            }
            catch (Exception e)
            {
                // Roll the meta gate back so the whole migration retries next launch, exactly like
                // MigrateV3toV4/MigrateV5toV6 do. Version was already bumped in memory above.
                // 回滚 meta 版本门使整个迁移下次启动重跑，与 MigrateV3toV4/MigrateV5toV6 一致。
                // 上方已在内存中提升了 Version。
                Settings.Version = 2;
                Loader.Error($"Migration v2→v3 failed, will retry next launch: {e.Message}");
                return;
            }
            Loader.Log("Migration v2→v3 complete");
        }

        private void MigrateV3toV4()
        {
            Loader.Log("Migrating settings v3 → v4: FootKeyBase fixed to 24");
            Settings.Version = 4;
            var d = Settings.Data;
            bool needsFootShift = d.DataVersion < 4;
            const int oldFootBase = 20;

            if (d.KeyViewerStyle == KeyviewerStyle.Key24 || !needsFootShift)
            {
                // The current profile needs no shift, but the OTHER profile files still do — the
                // early return used to skip MigrateAllProfileFiles entirely, and since the meta
                // Version gate never re-runs the migration, their foot-key counts stayed on the
                // old 20-base slots forever (permanently zeroed foot counters after the switch).
                // 当前配置无需平移,但其余 Profile 文件仍需要——早退曾整体跳过
                // MigrateAllProfileFiles,而 meta Version 门控不会再补跑,它们的脚键计数
                // 永远留在旧的 20 基线槽位(切换后脚键计数永久为零)。
                if (d.DataVersion < 4) d.DataVersion = 4;
                SaveCurrentProfile();
                if (!MigrateAllProfileFiles())
                {
                    Settings.Version = 3; // keep the old meta gate so failed profiles retry next launch
                    return;
                }
                SaveMetaOnly();
                return;
            }

            int footSize = d.FootKeyViewerStyle switch
            {
                FootKeyviewerStyle.Key2 => 2,
                FootKeyviewerStyle.Key4 => 4,
                FootKeyviewerStyle.Key6 => 6,
                FootKeyviewerStyle.Key8 => 8,
                FootKeyviewerStyle.Key10 => 10,
                FootKeyviewerStyle.Key12 => 12,
                FootKeyviewerStyle.Key14 => 14,
                FootKeyviewerStyle.Key16 => 16,
                _ => 0
            };
            if (footSize == 0)
            {
                if (d.DataVersion < 4) d.DataVersion = 4;
                SaveCurrentProfile();
                if (!MigrateAllProfileFiles())
                {
                    Settings.Version = 3; // keep the old meta gate so failed profiles retry next launch
                    return;
                }
                SaveMetaOnly();
                return;
            }

            static void ShiftColorArray(Color[] arr, int from, int to, int count)
            {
                if (arr == null) return;
                for (int i = count - 1; i >= 0; i--)
                {
                    if (to + i < arr.Length)
                        arr[to + i] = from + i < arr.Length ? arr[from + i] : default;
                }
                // Clear only the GAP between the old and new base — clearing the full old range
                // would overlap the just-written destination when count > (to - from) and wipe
                // freshly migrated entries (footSize 8 used to zero 4 of them).
                // 只清除新旧基线之间的间隙——清除整个旧区间会在 count > (to - from) 时与刚
                // 写入的目标区间重叠,抹掉刚迁入的条目(footSize 为 8 时会清掉其中 4 个)。
                int clearLen = Math.Min(count, to - from);
                for (int i = 0; i < clearLen && from + i < arr.Length; i++)
                    arr[from + i] = default;
            }

            if (needsFootShift)
            {
            Array.Copy(d.Count, oldFootBase, d.Count, FootKeyBase, footSize);
            // Same gap-only clear as ShiftColorArray (full-range clear overlapped the copy).
            // 与 ShiftColorArray 同款"仅清间隙"(全区间清除会与复制重叠)。
            Array.Clear(d.Count, oldFootBase, Math.Min(footSize, FootKeyBase - oldFootBase));
            ShiftColorArray(d.PerKeyBackground, oldFootBase, FootKeyBase, footSize);
            ShiftColorArray(d.PerKeyBackgroundClicked, oldFootBase, FootKeyBase, footSize);
            ShiftColorArray(d.PerKeyOutline, oldFootBase, FootKeyBase, footSize);
            ShiftColorArray(d.PerKeyOutlineClicked, oldFootBase, FootKeyBase, footSize);
            ShiftColorArray(d.PerKeyText, oldFootBase, FootKeyBase, footSize);
            ShiftColorArray(d.PerKeyTextClicked, oldFootBase, FootKeyBase, footSize);
            ShiftColorArray(d.PerKeyRainColor, oldFootBase, FootKeyBase, footSize);
            }
            if (d.DataVersion < 4) d.DataVersion = 4;

            SaveCurrentProfile();
            if (!MigrateAllProfileFiles())
            {
                Settings.Version = 3; // keep the old meta gate so failed profiles retry next launch
                return;
            }
            SaveMetaOnly();
            Loader.Log("Migration v3→v4 complete");
        }

        private void MigrateV4toV5()
        {
            // No array reshaping: a dedicated key108 array is filled lazily by EnsureSettingsArrays.
            // Existing 8K-24K + foot-key profiles load unchanged.
            Settings.Version = 5;
            EnsureSettingsArrays();
            if (Settings.Data.DataVersion < 5) Settings.Data.DataVersion = 5;
            try
            {
                SaveCurrentProfile();
                SaveMetaOnly();
            }
            catch (Exception e)
            {
                // Same rollback as the other migrations: a failed write must not leave the meta gate
                // at 5, or the profile would never be re-stamped.
                // 与其它迁移同样的回滚：写盘失败不能把 meta 门留在 5，否则该 Profile 永远不会被
                // 重新打戳。
                Settings.Version = 4;
                Loader.Error($"Migration v4→v5 failed, will retry next launch: {e.Message}");
                return;
            }
            Loader.Log("Migration v4→v5 complete");
        }

        /// <summary>Flip a normalized position to the mod-wide Y convention (0=top, 1=bottom). / 将归一化位置翻转为全 Mod 的 Y 约定(0=顶,1=底)。</summary>
        private static Vector2 FlipYConvention(Vector2 v) => new Vector2(v.x, Mathf.Clamp01(1f - v.y));

        /// <summary>Does the profile JSON carry this field as a property of the ROOT object? A
        /// substring search over the whole file also matches node text, image paths and key binds,
        /// which can make a pre-field profile look like a post-field one and get its constructor
        /// defaults flipped (irreversibly). Falls back to a substring check only when the document
        /// will not parse, so a merely malformed file still gets the old lenient treatment. /
        /// Profile JSON 的**根对象**是否带有该字段？对整份文件做子串搜索还会命中节点文字、图片
        /// 路径与按键名，可能让字段出现前的配置看起来像出现后的，进而把构造默认值翻转（不可逆）。
        /// 仅在文档无法解析时回退到子串判断，使"仅仅是格式损坏"的文件仍走原有的宽松处理。</summary>
        private static bool HasRootProperty(string json, string propertyName)
        {
            if (string.IsNullOrEmpty(json)) return false;
            try
            {
                var root = Newtonsoft.Json.Linq.JObject.Parse(json);
                return root[propertyName] != null;
            }
            catch (Exception)
            {
                return json.IndexOf(propertyName, StringComparison.Ordinal) >= 0;
            }
        }

        private void MigrateV5toV6(int metaVersionOnDisk)
        {
            // The full-keyboard KPS/Total boxes were the ONLY place using Y=1=top; they now follow
            // the mod-wide convention (0=top, 1=bottom) like the main/foot position sliders. Stored
            // values are flipped once so existing placements keep their on-screen position.
            //
            // Which profiles carry the field is decided by the ON-DISK meta version COMBINED with
            // per-file content: FullKpsPosition shipped with the v5-era full-keyboard feature, so
            // profiles written by v5 binaries always contain it (JsonUtility serializes every
            // field), while v4-and-older profiles never do. Two traps a version-only gate misses:
            // (1) a DORMANT secondary profile that a v4 user never re-saved after upgrading to a
            //     v5 binary is still v4-form on disk (field absent → ctor default would be flipped);
            // (2) earlier migrations in this same load (V3→V4/V4→V5) rewrite files with the field
            //     present at its ctor default — which is exactly why the content check alone is
            //     not enough either and the version gate must stay.
            // The current profile additionally requires a fully successful disk load: a meta=5
            // user whose file was just REBUILT with defaults (LoadProfileFromMeta's recovery
            // branch) must not have those fresh defaults flipped.
            //
            // 哪些 Profile 带有该字段由"磁盘上的 meta 版本 + 逐文件内容"联合决定:
            // FullKpsPosition 随 v5 时代的全键盘功能发布,v5 二进制写入的 Profile 必然含它
            //(JsonUtility 序列化所有字段),v4 及更早必然不含。仅按版本判断会漏两个坑:
            //(1)休眠的次要 Profile——v4 用户升级到 v5 二进制后从未保存过的那个文件在磁盘上
            //    仍是 v4 形态(字段缺失 → 构造默认值会被错误翻转);
            //(2)本次加载中更早的迁移(V3→V4/V4→V5)会以"字段存在但为构造默认值"重写文件——
            //    这正是仅按内容判断也不够、版本门必须保留的原因。
            // 当前 Profile 额外要求"完全成功地从磁盘加载":meta=5 但文件刚被恢复分支用默认值
            // 重建(LoadProfileFromMeta)时,不能翻转这些新鲜默认值。
            Loader.Log("Migrating settings v5 → v6: full-keyboard KPS/Total Y convention flip");
            Settings.Version = 6;
            if (Settings.Data.DataVersion < 6 && metaVersionOnDisk >= 5 && curProfileHasFullKpsPos)
            {
                Settings.Data.FullKpsPosition = FlipYConvention(Settings.Data.FullKpsPosition);
                Settings.Data.FullTotalPosition = FlipYConvention(Settings.Data.FullTotalPosition);
            }
            if (Settings.Data.DataVersion < 6) Settings.Data.DataVersion = 6;
            SaveCurrentProfile();

            // Batch-flip the other profile files the same way — the meta Version gate never
            // re-runs this migration, so switching to them later must not resurrect old-convention
            // Y values. Each file is content-checked (a v4-form dormant file is skipped).
            // / 同法批量翻转其余 Profile 文件——meta 版本门控不会重跑本迁移,之后切到它们时
            // 不能让旧约定的 Y 值复活。逐文件检查内容(v4 形态的休眠文件跳过)。
            bool allProfilesSucceeded = true;
            if (metaVersionOnDisk >= 5 && Settings.ProfileNames != null)
            {
                string savedProfile = Settings.CurrentProfile;
                foreach (string name in Settings.ProfileNames)
                {
                    if (name == savedProfile) continue;
                    try
                    {
                        string path = GetProfilePath(name);
                        // A file that is merely unreadable RIGHT NOW (cloud placeholder, unplugged
                        // drive, permissions) must count as a failure: advancing the meta gate with
                        // it still un-migrated means it is never revisited — its foot counters stay
                        // on the pre-v4 slots and its KPS panel keeps the old Y convention forever,
                        // with no compensating path.
                        // 此刻"读不到"的文件（云盘占位、移动硬盘未挂载、权限）必须计为失败：若带着
                        // 未迁移的文件推进 meta 门，它就再也不会被回访——脚键计数永远留在 v4 之前
                        // 的槽位上、KPS 面板永远是旧 Y 约定，且没有任何补偿路径。
                        if (!File.Exists(path))
                        {
                            allProfilesSucceeded = false;
                            Loader.Warning($"Profile '{name}' is missing or inaccessible; v6 migration will retry next launch");
                            continue;
                        }
                        string raw = File.ReadAllText(path);
                        // Root-object property check, not a substring search. A FreeMake node's
                        // custom label, image path or key bind containing the literal text
                        // "FullKpsPosition" used to make a genuinely v4-form file look like v5, and
                        // its CONSTRUCTOR DEFAULTS were then flipped and stamped DataVersion=6 —
                        // an irreversible, silent misplacement.
                        // 必须判断根对象属性而非子串。FreeMake 节点的自定义文字、图片路径或按键名
                        // 中出现字面量 "FullKpsPosition" 时，真正 v4 形态的文件会被误判为 v5，
                        // 其**构造默认值**被翻转并盖上 DataVersion=6——不可逆且静默的错位。
                        if (!HasRootProperty(raw, "FullKpsPosition")) continue; // dormant v4-form file / 休眠的 v4 形态文件
                        var pd = new ProfileData();
                        JsonConvert.PopulateObject(raw, pd, ProfileData.ProfileSerializer);
                        pd.SyncArraysFromLists();
                        if (pd.DataVersion >= 6) continue;
                        pd.FullKpsPosition = FlipYConvention(pd.FullKpsPosition);
                        pd.FullTotalPosition = FlipYConvention(pd.FullTotalPosition);
                        pd.DataVersion = 6;
                        pd.SyncListsToArrays();
                        WriteAllTextSafe(path, JsonConvert.SerializeObject(pd, Formatting.Indented, ProfileData.ProfileSerializer));
                    }
                    catch (Exception e)
                    {
                        allProfilesSucceeded = false;
                        Loader.Warning($"Failed to migrate profile '{name}' to v6: {e.Message}");
                    }
                }
            }
            if (!allProfilesSucceeded)
            {
                // Keep the on-disk meta at v5 so failed profiles are retried next launch. The
                // current profile already has DataVersion=6, so its completed flip is not repeated.
                // 保持磁盘 meta 为 v5，让失败 Profile 下次启动重试；当前 Profile 已标记 6，不会重复翻转。
                Settings.Version = 5;
                return;
            }
            SaveMetaOnly();
            Loader.Log("Migration v5→v6 complete");
        }

        /// <summary>Apply the v3→v4 foot-slot shift to one ProfileData in place. Foot keys used to
        /// start at slot 20 and moved to FootKeyBase(24); a profile written before that keeps its
        /// foot counters and per-key colors on the OLD slots, so after loading they read as zero /
        /// wrong. The transformation is idempotent (guarded by DataVersion) and is shared by the
        /// one-time version bump and by LoadProfile — a profile file that arrives AFTER the meta was
        /// already upgraded (copied in by hand, or imported from an older .jkv) is never seen by the
        /// version-bump pass, so without this it loaded with permanently zeroed foot counters. /
        /// 就地把 v3→v4 的脚键槽位平移应用到一份 ProfileData。脚键过去从槽位 20 起，后移至
        /// FootKeyBase(24)；此前写出的配置把脚键计数与每键颜色留在旧槽位上，加载后读出来就是 0/
        /// 错值。该变换幂等（由 DataVersion 守卫），并由一次性版本升级与 LoadProfile 共用——meta
        /// 升级之后才出现的配置文件（手动拷入，或从旧版 .jkv 导入）不会被升级流程看到，没有这里
        /// 就会带着永久为零的脚键计数加载。</summary>
        private static void MigrateFootSlots(ProfileData pd)
        {
            if (pd == null || pd.DataVersion >= 4) return;
            int footSize = pd.FootKeyViewerStyle switch
            {
                FootKeyviewerStyle.Key2 => 2,
                FootKeyviewerStyle.Key4 => 4,
                FootKeyviewerStyle.Key6 => 6,
                FootKeyviewerStyle.Key8 => 8,
                FootKeyviewerStyle.Key10 => 10,
                FootKeyviewerStyle.Key12 => 12,
                FootKeyviewerStyle.Key14 => 14,
                FootKeyviewerStyle.Key16 => 16,
                _ => 0
            };
            if (pd.KeyViewerStyle == KeyviewerStyle.Key24 || footSize == 0)
            {
                pd.DataVersion = 4;
                return;
            }
            const int oldBase = 20;
            // Old builds wrote Count[36]; the copy below would run past its end and throw. Resize
            // first, the same way EnsureSettingsArrays handles the live settings.
            // 旧版本写入的是 Count[36]；下面的复制会越界并抛异常。先按 EnsureSettingsArrays
            // 处理在线设置的同样方式重定长度。
            if (pd.Count == null || pd.Count.Length != MaxKeySlots)
            {
                int[] c = new int[MaxKeySlots];
                if (pd.Count != null) Array.Copy(pd.Count, c, Math.Min(pd.Count.Length, MaxKeySlots));
                pd.Count = c;
            }
            // 36-era files also carried shorter PerKey color arrays (38 = 36+2): the shifts below
            // only write inside the old length, so a dormant profile with footSize 16 silently
            // dropped the tail slots. Resize first — the tail fills from the profile's own global
            // colors, using the SAME per-row rule EnsureSettingsArrays applies for a newer build
            // (the rain array resolves per rain row, not flat row-1; see
            // ProfileData.DefaultPerKeyRainColor).
            // 36-era 文件的 PerKey 颜色数组同样更短（38 = 36+2）：平移只写入旧长度之内，休眠
            // Profile 带 16 脚键时会把尾部槽位静默丢掉。先重定长度，尾部用该 Profile 自己的
            // 全局色填充，并采用与新版 EnsureSettingsArrays **相同**的按排规则（雨色数组按雨排
            // 解析，而非一律第 1 排——见 ProfileData.DefaultPerKeyRainColor）。
            pd.PerKeyBackground = EnsureColorArray(pd.PerKeyBackground, PerKeySlotCount, pd.Background);
            pd.PerKeyBackgroundClicked = EnsureColorArray(pd.PerKeyBackgroundClicked, PerKeySlotCount, pd.BackgroundClicked);
            pd.PerKeyOutline = EnsureColorArray(pd.PerKeyOutline, PerKeySlotCount, pd.Outline);
            pd.PerKeyOutlineClicked = EnsureColorArray(pd.PerKeyOutlineClicked, PerKeySlotCount, pd.OutlineClicked);
            pd.PerKeyText = EnsureColorArray(pd.PerKeyText, PerKeySlotCount, pd.Text);
            pd.PerKeyTextClicked = EnsureColorArray(pd.PerKeyTextClicked, PerKeySlotCount, pd.TextClicked);
            pd.PerKeyRainColor = ProfileData.EnsureRainColorArray(
                pd.PerKeyRainColor, PerKeySlotCount, pd.RainColor, pd.RainColor2, pd.RainColor3);
            Array.Copy(pd.Count, oldBase, pd.Count, FootKeyBase, footSize);
            // Gap-only clear — the full-range clear overlapped the just-copied entries
            // when footSize > (FootKeyBase - oldBase).
            // 仅清间隙——footSize > (FootKeyBase - oldBase) 时全区间清除会重叠刚复制的条目。
            Array.Clear(pd.Count, oldBase, Math.Min(footSize, FootKeyBase - oldBase));
            static void Shift(Color[] a, int from, int to, int n)
            {
                if (a == null) return;
                for (int i = n - 1; i >= 0; i--)
                {
                    if (to + i < a.Length)
                        a[to + i] = from + i < a.Length ? a[from + i] : default;
                }
                int clearLen = Math.Min(n, to - from);
                for (int i = 0; i < clearLen && from + i < a.Length; i++)
                    a[from + i] = default;
            }
            Shift(pd.PerKeyBackground, oldBase, FootKeyBase, footSize);
            Shift(pd.PerKeyBackgroundClicked, oldBase, FootKeyBase, footSize);
            Shift(pd.PerKeyOutline, oldBase, FootKeyBase, footSize);
            Shift(pd.PerKeyOutlineClicked, oldBase, FootKeyBase, footSize);
            Shift(pd.PerKeyText, oldBase, FootKeyBase, footSize);
            Shift(pd.PerKeyTextClicked, oldBase, FootKeyBase, footSize);
            Shift(pd.PerKeyRainColor, oldBase, FootKeyBase, footSize);
            pd.DataVersion = 4;
        }

        private bool MigrateAllProfileFiles()
        {
            if (Settings.ProfileNames == null) return true;
            bool allSucceeded = true;
            string savedProfile = Settings.CurrentProfile;
            foreach (string name in Settings.ProfileNames)
            {
                if (name == savedProfile) continue;
                try
                {
                    string path = GetProfilePath(name);
                    // Unreadable-right-now is a FAILURE, not a skip: advancing the meta gate with a
                    // still-unmigrated profile means it is never revisited and its foot-key counts
                    // stay on the pre-v4 slots (reading as zero) for good.
                    // 此刻"读不到"必须计为失败而非跳过：带着未迁移的配置推进 meta 门意味着它再也
                    // 不会被回访，脚键计数会永远留在 v4 之前的槽位上（读出来是 0）。
                    if (!File.Exists(path))
                    {
                        allSucceeded = false;
                        Loader.Warning($"Profile '{name}' is missing or inaccessible; v4 migration will retry next launch");
                        continue;
                    }
                    string json = File.ReadAllText(path);
                    var pd = new ProfileData();
                    JsonConvert.PopulateObject(json, pd, ProfileData.ProfileSerializer);
                    pd.SyncArraysFromLists();
                    if (pd.DataVersion >= 4) continue;
                    // Shared with LoadProfile so there is exactly one implementation of the foot-slot
                    // shift. / 与 LoadProfile 共用，确保脚键槽位平移只有一份实现。
                    MigrateFootSlots(pd);
                    pd.SyncListsToArrays();
                    WriteAllTextSafe(path, JsonConvert.SerializeObject(pd, Formatting.Indented, ProfileData.ProfileSerializer));
                }
                catch (Exception e)
                {
                    allSucceeded = false;
                    Loader.Warning($"Failed to migrate profile '{name}': {e.Message}");
                }
            }
            return allSucceeded;
        }

        private void LoadProfileFromMeta()
        {
            string profileName = !string.IsNullOrEmpty(Settings.CurrentProfile)
                ? Settings.CurrentProfile : "Default";
            if (File.Exists(GetProfilePath(profileName)))
            {
                // LoadProfile reports IO/validation failures (it backs the file up as *.corrupt);
                // ignoring that return value used to let constructor defaults masquerade as the
                // profile and get saved over it on the next high-frequency SaveSettings.
                // LoadProfile 会报告 IO/校验失败(并备份为 *.corrupt);旧代码无视返回值,构造
                // 函数默认值会冒充该配置内容,并在下一次高频 SaveSettings 时覆盖写回文件。
                if (LoadProfile(profileName))
                {
                    Settings.CurrentProfile = profileName;
                    EnsureSettingsArrays();
                }
                else
                {
                    Loader.Warning($"Profile '{profileName}' unreadable, recreating with defaults (original saved as .corrupt)");
                    Settings.CurrentProfile = profileName;
                    EnsureSettingsArrays();
                    SaveCurrentProfile();
                }
            }
            else
            {
                // File.Exists returns FALSE for a path that exists but is temporarily inaccessible
                // (locked by a sync client, a permissions change, an OneDrive placeholder that has
                // not materialised). This branch used to write a default profile straight back to
                // that very path — and since SaveSettings runs on every scene load, the first
                // successful write after the lock cleared overwrote the user's real config with
                // defaults, with nothing but a log line to show for it. Bring the session up with
                // defaults in memory only and let a later save decide.
                // File.Exists 对"存在但暂时不可访问"的路径同样返回 false（被同步软件锁定、权限
                // 变化、OneDrive 占位文件未落地）。此前该分支会把一份默认 Profile 直接写回同一
                // 路径——而 SaveSettings 每次场景加载都跑，锁一解除的首次成功写入就会用默认值
                // 覆盖用户真实配置，全程只有一行日志。现只在内存中启用默认值，交由后续保存决定。
                Loader.Warning($"Profile '{profileName}' not found (or not accessible); running with defaults in memory until it can be written");
                lastSaveError = $"Profile file for '{profileName}' is not accessible";
                Settings.CurrentProfile = profileName;
                if (Settings.ProfileNames == null || Settings.ProfileNames.Length == 0)
                    Settings.ProfileNames = new[] { profileName };
                EnsureSettingsArrays();
            }
        }

        private static Color[] EnsureColorArray(Color[] arr, int n, Color fill)
        {
            if (arr != null && arr.Length == n) return arr;
            Color[] result = new Color[n];
            for (int i = 0; i < n; i++)
                result[i] = arr != null && i < arr.Length ? arr[i] : fill;
            return result;
        }

        /// <summary>Null- AND length-checked KeyCode array; wrong length falls back to defaults / 空值与长度双检的 KeyCode 数组，长度不符回退默认</summary>
        private static KeyCode[] EnsureKeyCodeArray(KeyCode[] arr, KeyCode[] defaults)
        {
            if (arr != null && arr.Length == defaults.Length) return arr;
            // Preserve a hand-written/partial binding prefix and fill only the missing tail;
            // replacing the whole array with defaults silently erased user bindings.
            // 保留手写/截断绑定的已有前缀，只补缺失尾部；整数组回退默认会静默抹掉用户绑定。
            KeyCode[] result = (KeyCode[])defaults.Clone();
            if (arr != null)
                for (int i = 0; i < result.Length && i < arr.Length; i++) result[i] = arr[i];
            return result;
        }

        /// <summary>Null- AND length-checked string array (keeps existing entries on resize) / 空值与长度双检的字符串数组（重定长度时保留已有条目）</summary>
        private static string[] EnsureStringArray(string[] arr, int n)
        {
            if (arr != null && arr.Length == n) return arr;
            string[] result = new string[n];
            if (arr != null)
                for (int i = 0; i < n && i < arr.Length; i++)
                    result[i] = arr[i];
            return result;
        }

        /// <summary>Ensure all settings arrays are initialized / 确保所有设置数组已初始化</summary>
        private static void EnsureSettingsArrays()
        {
            // Clamp deserialized enums to their legal ranges: JsonUtility accepts any integer for an
            // enum field, and an out-of-range style reaches GetLayout's throw → EnableKeyViewer dies
            // half-initialized AND the settings window (KpsTotalIsSlim → GetLayout) can no longer
            // render, leaving the user unable to switch back to a valid layout from the GUI.
            // 将反序列化的枚举钳制到合法范围：JsonUtility 接受任意整数,越界样式会走到 GetLayout
            // 的 throw——EnableKeyViewer 半初始化死亡,设置窗口(KpsTotalIsSlim → GetLayout)也画
            // 不出来,用户无法从界面切回合法布局。
            if (!System.Enum.IsDefined(typeof(KeyviewerStyle), Settings.Data.KeyViewerStyle))
            {
                Loader.Warning($"KeyViewer: invalid KeyViewerStyle {(int)Settings.Data.KeyViewerStyle}, falling back to Key16");
                Settings.Data.KeyViewerStyle = KeyviewerStyle.Key16;
            }
            if (!System.Enum.IsDefined(typeof(FootKeyviewerStyle), Settings.Data.FootKeyViewerStyle))
            {
                Loader.Warning($"KeyViewer: invalid FootKeyViewerStyle {(int)Settings.Data.FootKeyViewerStyle}, falling back to None");
                Settings.Data.FootKeyViewerStyle = FootKeyviewerStyle.None;
            }

            // Truncated binding arrays (hand-edited / partially written profiles) are the same gap
            // class as Count below: FromJsonOverwrite restores whatever length the JSON carries and
            // the binding tab indexes key8[key12 slots] unguarded. Preserve the existing prefix and
            // fill only the missing tail. / 截断的绑定数组与下方 Count 属同类缺口；保留已有前缀，
            // 只补缺失尾部，避免整组用户绑定被默认数组覆盖。
            ProfileData defaults = new ProfileData();
            Settings.Data.key8 = EnsureKeyCodeArray(Settings.Data.key8, defaults.key8);
            Settings.Data.key10 = EnsureKeyCodeArray(Settings.Data.key10, defaults.key10);
            Settings.Data.key12 = EnsureKeyCodeArray(Settings.Data.key12, defaults.key12);
            Settings.Data.key14 = EnsureKeyCodeArray(Settings.Data.key14, defaults.key14);
            Settings.Data.key16 = EnsureKeyCodeArray(Settings.Data.key16, defaults.key16);
            Settings.Data.key20 = EnsureKeyCodeArray(Settings.Data.key20, defaults.key20);
            Settings.Data.key24 = EnsureKeyCodeArray(Settings.Data.key24, defaults.key24);
            Settings.Data.footkey2 = EnsureKeyCodeArray(Settings.Data.footkey2, defaults.footkey2);
            Settings.Data.footkey4 = EnsureKeyCodeArray(Settings.Data.footkey4, defaults.footkey4);
            Settings.Data.footkey6 = EnsureKeyCodeArray(Settings.Data.footkey6, defaults.footkey6);
            Settings.Data.footkey8 = EnsureKeyCodeArray(Settings.Data.footkey8, defaults.footkey8);
            Settings.Data.footkey10 = EnsureKeyCodeArray(Settings.Data.footkey10, defaults.footkey10);
            Settings.Data.footkey12 = EnsureKeyCodeArray(Settings.Data.footkey12, defaults.footkey12);
            Settings.Data.footkey14 = EnsureKeyCodeArray(Settings.Data.footkey14, defaults.footkey14);
            Settings.Data.footkey16 = EnsureKeyCodeArray(Settings.Data.footkey16, defaults.footkey16);
            Settings.Data.GhostKey8 = EnsureKeyCodeArray(Settings.Data.GhostKey8, defaults.GhostKey8);
            Settings.Data.GhostKey10 = EnsureKeyCodeArray(Settings.Data.GhostKey10, defaults.GhostKey10);
            Settings.Data.GhostKey12 = EnsureKeyCodeArray(Settings.Data.GhostKey12, defaults.GhostKey12);
            Settings.Data.GhostKey14 = EnsureKeyCodeArray(Settings.Data.GhostKey14, defaults.GhostKey14);
            Settings.Data.GhostKey16 = EnsureKeyCodeArray(Settings.Data.GhostKey16, defaults.GhostKey16);
            Settings.Data.GhostKey20 = EnsureKeyCodeArray(Settings.Data.GhostKey20, defaults.GhostKey20);
            Settings.Data.GhostKey24 = EnsureKeyCodeArray(Settings.Data.GhostKey24, defaults.GhostKey24);
            Settings.Data.key8Text = EnsureStringArray(Settings.Data.key8Text, 8);
            Settings.Data.key10Text = EnsureStringArray(Settings.Data.key10Text, 10);
            Settings.Data.key12Text = EnsureStringArray(Settings.Data.key12Text, 12);
            Settings.Data.key14Text = EnsureStringArray(Settings.Data.key14Text, 14);
            Settings.Data.key16Text = EnsureStringArray(Settings.Data.key16Text, 16);
            Settings.Data.key20Text = EnsureStringArray(Settings.Data.key20Text, 20);
            Settings.Data.key24Text = EnsureStringArray(Settings.Data.key24Text, 24);
            // A truncated key108 (hand-edited profile) would crash InitializeFullKeyboard's fixed
            // 105-slot table; the array isn't user-rebindable (SetupKey ignores full-keyboard mode),
            // so a wrong-length array is simply replaced with the default.
            // 截断的 key108（手工编辑的 Profile）会让 InitializeFullKeyboard 的固定 105 槽位表越界；
            // 该数组不支持用户重绑（SetupKey 在全键盘模式下直接返回），长度不对时直接换回默认值。
            if (Settings.Data.key108 == null || Settings.Data.key108.Length != 105)
                Settings.Data.key108 = BuildDefaultKey108();
            Settings.Data.footkey2Text = EnsureStringArray(Settings.Data.footkey2Text, 2);
            Settings.Data.footkey4Text = EnsureStringArray(Settings.Data.footkey4Text, 4);
            Settings.Data.footkey6Text = EnsureStringArray(Settings.Data.footkey6Text, 6);
            Settings.Data.footkey8Text = EnsureStringArray(Settings.Data.footkey8Text, 8);
            Settings.Data.footkey10Text = EnsureStringArray(Settings.Data.footkey10Text, 10);
            Settings.Data.footkey12Text = EnsureStringArray(Settings.Data.footkey12Text, 12);
            Settings.Data.footkey14Text = EnsureStringArray(Settings.Data.footkey14Text, 14);
            Settings.Data.footkey16Text = EnsureStringArray(Settings.Data.footkey16Text, 16);
            Settings.Data.Count = Settings.Data.Count ?? new int[MaxKeySlots];
            // FromJsonOverwrite restores whatever array length the profile JSON carries — builds
            // between the profile refactor and MaxKeySlots=40 wrote Count[36]. A short array made
            // the V3→V4 foot-base migration's Array.Copy throw (which reset ALL settings to
            // defaults and saved them over the profile files). Resize like the color arrays.
            // FromJsonOverwrite 会按 Profile JSON 里的数组长度原样还原——profile 重构到
            // MaxKeySlots=40 之间的版本写入的是 Count[36]。短数组会让 V3→V4 脚键基线迁移的
            // Array.Copy 抛异常（进而把全部设置重置为默认值并覆盖 Profile 文件）。像颜色数组
            // 一样重定长度。
            if (Settings.Data.Count.Length != MaxKeySlots)
            {
                int[] c = new int[MaxKeySlots];
                Array.Copy(Settings.Data.Count, c, Math.Min(Settings.Data.Count.Length, MaxKeySlots));
                Settings.Data.Count = c;
            }
            int n = PerKeySlotCount;
            Settings.Data.PerKeyBackground = EnsureColorArray(Settings.Data.PerKeyBackground, n, Settings.Data.Background);
            Settings.Data.PerKeyBackgroundClicked = EnsureColorArray(Settings.Data.PerKeyBackgroundClicked, n, Settings.Data.BackgroundClicked);
            Settings.Data.PerKeyOutline = EnsureColorArray(Settings.Data.PerKeyOutline, n, Settings.Data.Outline);
            Settings.Data.PerKeyOutlineClicked = EnsureColorArray(Settings.Data.PerKeyOutlineClicked, n, Settings.Data.OutlineClicked);
            Settings.Data.PerKeyText = EnsureColorArray(Settings.Data.PerKeyText, n, Settings.Data.Text);
            Settings.Data.PerKeyTextClicked = EnsureColorArray(Settings.Data.PerKeyTextClicked, n, Settings.Data.TextClicked);
            // Same gap class as Count: these two are indexed unguarded by the rain system
            // (PerKeyGhostRainColor) and the per-key font path, and were only sized by the
            // constructor that FromJsonOverwrite bypasses. / 与 Count 同类缺口:雨滴系统未加守卫地
            // 索引 PerKeyGhostRainColor,每键字号路径亦然;此前仅靠被 FromJsonOverwrite 绕过的
            // 构造函数定长。
            // PerKeyRainColor resolves its fill per rain ROW (shared with the ctor and the
            // "reset per-key colours" button) rather than flat row-1, so the three agree.
            // PerKeyRainColor 的填充按**雨排**解析（与构造函数及「重置每键颜色」共用），而不是
            // 一律第 1 排，故三者一致。
            Settings.Data.PerKeyRainColor = ProfileData.EnsureRainColorArray(Settings.Data.PerKeyRainColor, n);
            Settings.Data.PerKeyGhostRainColor = EnsureColorArray(Settings.Data.PerKeyGhostRainColor, n, GhostRainColorDefault);
            if (Settings.Data.PerKeyFontSize == null || Settings.Data.PerKeyFontSize.Length != n)
            {
                float[] f = new float[n]; // 0 = use the global font size / 0 = 使用全局字号
                if (Settings.Data.PerKeyFontSize != null)
                    Array.Copy(Settings.Data.PerKeyFontSize, f, Math.Min(Settings.Data.PerKeyFontSize.Length, n));
                Settings.Data.PerKeyFontSize = f;
            }
            EnsureCustomNodes();
        }

        private void ClearKpsTimers()
        {
            PressTimes?.Clear();
            if (keyPressTimes != null)
                for (int i = 0; i < keyPressTimes.Length; i++)
                    keyPressTimes[i]?.Clear();
            if (lastPerKeyKps != null)
                for (int i = 0; i < lastPerKeyKps.Length; i++)
                    lastPerKeyKps[i] = 0;
            // Custom nodes keep their own per-key KPS log on the Key, and nothing else ever
            // drained it on reset — the fixed layouts' keyPressTimes above are a different
            // container, so custom Per-Key KPS counters kept showing the old rate for up to a
            // second after "Reset Counts". Clear them here too and force a rewrite.
            // 自定义节点把每键 KPS 队列挂在 Key 上，重置时此前无人清理——上面的 keyPressTimes
            // 是另一套容器，故自定义 Per-Key KPS 计数在点「重置计数」后仍会显示旧速率最多一秒。
            // 在此一并清空并强制重写。
            if (Keys != null)
            {
                for (int i = 0; i < Keys.Length; i++)
                {
                    Key k = Keys[i];
                    if (k == null || k.CustomNode == null) continue;
                    k.KpsLog.Clear();
                    k.LastShownKps = int.MinValue;
                    // Stat-panel compare-skip caches must be invalidated too, or the per-frame
                    // writer keeps skipping while the live rate equals the pre-reset cached
                    // value — a steady-rate player's KPS panel stuck on "0" after reset.
                    // 统计面板的比较跳过缓存也须失效，否则实时速率等于重置前缓存值时
                    // 逐帧写入方会一直跳过刷新——匀速游玩者的 KPS 面板会卡在"0"上。
                    k.LastShownStatKps = int.MinValue;
                    k.LastShownTotal = long.MinValue;
                }
            }
            // The per-GROUP queues feed the custom KPS panels — uncleared, CustomGroupKps
            // keeps counting pre-reset presses for up to 1s after "Reset Counts". /
            // 按组队列喂着自定义 KPS 面板——不清空时，点「重置计数」后最多 1 秒内
            // CustomGroupKps 仍在统计重置前的按压。
            customGroupPresses.Clear();
            lastKps = -1;
            _hasKeyPressActivity = false;
        }

        /// <summary>
        /// Save current settings: meta + current profile / 保存当前设置：元数据 + 当前配置
        /// </summary>
        public void SaveSettings()
        {
            try
            {
                Settings.UiTab = settingsGuiTab;
                SaveCurrentProfile();
                SaveMetaOnly();
                lastSaveError = null;
            }
            catch (Exception e)
            {
                // A failed write used to be a single log line: the user kept editing, the GUI showed
                // no sign anything was wrong, and every change since the last successful save was
                // silently lost on the next launch (read-only profile dir, full disk, file locked by
                // a sync client). Keep the message and surface it in the settings window until a
                // save succeeds.
                // 写盘失败此前只是一行日志：用户继续编辑、界面毫无提示，下次启动时自上次成功保存
                // 以来的所有改动静默丢失（目录只读、磁盘满、被同步软件占用）。现保留消息并在
                // 设置窗口持续提示，直到某次保存成功。
                lastSaveError = e.Message;
                Loader.Error($"Failed to save settings: {e.Message}");
            }
        }

        /// <summary>Run a storage write that does NOT warrant a full SaveSettings (a meta-only
        /// write, a profile save, a directory resync) with the same try/catch and the same
        /// user-visible failure banner. IMGUI call sites MUST use this: an exception thrown out of a
        /// GUILayout callback leaves the Begin/End group stack unbalanced, which corrupts the layout
        /// of the whole window from that frame on — the controls misplace and stop responding until
        /// the game restarts — while the red banner never appears, because lastSaveError was never
        /// set. The tab bar is the widest trigger: a full disk makes every single tab click one.
        /// 执行不需要完整 SaveSettings 的写盘操作（仅 meta、仅配置、目录重扫）时，用同一套
        /// try/catch 与同一个用户可见的失败横幅。IMGUI 调用点**必须**走这里：从 GUILayout 回调
        /// 抛出的异常会让 Begin/End 组栈失衡，自该帧起破坏**整个窗口**的布局——控件错位且不再
        /// 响应，直到重启游戏——而红色横幅永远不会出现，因为 lastSaveError 从未被设置。标签栏
        /// 触发面最广：磁盘一满，点任意一次标签页就触发一次。
        /// </summary>
        internal void GuardedSave(string what, Action write)
        {
            if (write == null) return;
            try
            {
                write();
                lastSaveError = null;
            }
            catch (Exception e)
            {
                lastSaveError = e.Message;
                Loader.Error($"Failed to save {what}: {e.Message}");
            }
        }

        /// <summary>Message from the most recent failed save, or null once a save has succeeded.
        /// Rendered as a persistent warning in the settings window. / 最近一次保存失败的消息；
        /// 保存成功后清空。在设置窗口作为持续警告显示。</summary>
        internal string LastSaveError => lastSaveError;
        private string lastSaveError;

        // ---- Debounced saving for high-frequency GUI changes / 高频 GUI 变更的去抖保存 ----
        // Dragging a slider or a color channel fired SaveSettings on every IMGUI change event
        // (~120 full-profile JSON + meta writes per second while dragging). GUI handlers now call
        // SaveSettingsFromGui() instead: the first change after a quiet spell still saves at once,
        // rapid successive changes coalesce and flush from Update on mouse-up or after 0.5s.
        // Critical paths (window close, disable, scene load, profile ops) keep calling SaveSettings
        // directly and always write immediately.
        // 拖动滑块/颜色通道时每次 IMGUI 变更事件都会触发 SaveSettings(拖动期间每秒约 120 次
        // 全量 profile JSON + meta 写盘)。GUI 处理器改调 SaveSettingsFromGui():静默期后的首次
        // 变更仍然立即保存,快速连续变更合并,由 Update 在松开鼠标或 0.5 秒后统一落盘。
        // 关键路径(关窗/禁用/场景加载/Profile 操作)仍直接调 SaveSettings,恒为立即写。
        private bool guiSaveDirty;
        private float lastGuiSaveTime = -999f;

        /// <summary>Debounced save for GUI change handlers / GUI 变更处理器用的去抖保存</summary>
        private void SaveSettingsFromGui()
        {
            float now = Time.unscaledTime;
            if (now - lastGuiSaveTime >= 0.5f)
            {
                lastGuiSaveTime = now;
                guiSaveDirty = false;
                SaveSettings();
            }
            else
            {
                guiSaveDirty = true;
            }
        }

        /// <summary>Flush a pending debounced save (mouse-up or 0.5s timeout). Called from Update. / 落盘挂起的去抖保存(松开鼠标或 0.5 秒超时),由 Update 调用。</summary>
        private void FlushGuiSaveIfNeeded()
        {
            if (!guiSaveDirty) return;
            float now = Time.unscaledTime;
            if (Input.GetMouseButtonUp(0) || now - lastGuiSaveTime >= 0.5f)
            {
                lastGuiSaveTime = now;
                guiSaveDirty = false;
                SaveSettings();
            }
        }

        /// <summary>
        /// Save only the meta file (settings.json) — Version, CurrentProfile, ProfileNames, Language / 仅保存元数据文件（settings.json）
        /// </summary>
        private void SaveMetaOnly()
        {
            string directory = Path.GetDirectoryName(ConfigPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            string metaJson = JsonUtility.ToJson(new SettingsMeta
            {
                Version = Settings.Version,
                CurrentProfile = Settings.CurrentProfile,
                ProfileNames = Settings.ProfileNames,
                Language = Settings.Language,
                UiTab = Settings.UiTab
            }, true);
            WriteAllTextSafe(ConfigPath, metaJson);
        }

        /// <summary>
        /// Atomic file write: temp file + replace, so a crash or power loss mid-write can't truncate
        /// the live config (SaveSettings runs on every scene load, so the write window is exercised
        /// constantly). / 原子写文件：先写临时文件再替换，崩溃或断电在写入中途也不会截断现有配置
        ///（SaveSettings 每次场景加载都会执行，写入窗口一直在被反复触发）。
        /// </summary>
        private static void WriteAllTextSafe(string path, string contents)
        {
            string tmp = path + ".tmp";
            try
            {
                // Write through a FileStream and flush to the DEVICE, not just the OS cache.
                // File.WriteAllText stops at the cache, so a power loss right after this returned
                // could still leave a truncated file — the "atomic" claim only ever covered the
                // rename, not the content.
                // 通过 FileStream 写入并 flush 到**设备**而非仅 OS 缓存。File.WriteAllText 只写到
                // 缓存，返回后立刻断电仍可能留下截断文件——此前的"原子"只覆盖了 rename，不覆盖内容。
                using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.Write(contents);
                    writer.Flush();
                    stream.Flush(true);
                }
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        // File.Replace is not implemented on some Mono/Wine/Proton and network/exFAT
                        // setups, and it fails there EVERY time — a permanent save failure that
                        // left the user staring at the error banner forever. Fall back to
                        // delete+move, which is still far better than never saving.
                        // File.Replace 在部分 Mono/Wine/Proton 与网络盘/exFAT 上未实现，且每次都
                        // 失败——那是让用户永远看着错误横幅的**永久性**保存失败。降级为
                        // delete+move，仍远好过永远存不下去。
                        File.Delete(path);
                        File.Move(tmp, path);
                    }
                }
                else File.Move(tmp, path);
            }
            catch
            {
                // A failed write used to leave <path>.tmp next to the live config forever, and the
                // next boot would then trip over the stale temp file. / 写失败会在配置旁留下永久的
                // .tmp 残留，下次启动会被这个陈旧临时文件绊住。
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                throw;
            }
        }

        /// <summary>
        /// Save the current profile to its file / 将当前配置保存到文件
        /// </summary>
        private void SaveCurrentProfile()
        {
            if (!Directory.Exists(ProfileDir)) Directory.CreateDirectory(ProfileDir);
            // Flush the working lists into the persisted array fields, then serialize with
            // Newtonsoft (Fields mode) — real nested arrays, no escaped embedded strings. /
            // 先把工作列表刷入持久化数组字段，再用 Newtonsoft（字段模式）序列化——真正的嵌套
            // 数组，无转义内嵌字符串。
            Settings.Data.SyncListsToArrays();
            if (Settings.Data.DataVersion < Settings.Version)
                Settings.Data.DataVersion = Settings.Version;
            // The legacy FmNode-defaults repair rides its OWN stamp, deliberately separate from
            // DataVersion. DataVersion is stamped from the META schema version (2..6) and 1 is never
            // written by any path, so gating the repair on `DataVersion < NodeTextDefaultsVersion`
            // meant the documented "bump the constant when you add a defaulted field" escalation
            // would have permanently disabled the repair for every profile the moment anyone
            // followed it — the two fields live on incompatible version axes. NodeDefaultsVersion
            // is 0 in every profile written before this field existed and is stamped forward here,
            // which is exactly the question the gate needs to answer: "was this file written
            // before the repair list was last extended?"
            // 旧 FmNode 默认值修复走**自己**的版本戳，刻意与 DataVersion 分开。DataVersion 由
            // **meta** 架构版本（2..6）盖章，而 1 从未被任何路径写过，故用
            // `DataVersion < NodeTextDefaultsVersion` 当闸门意味着：任何人一旦照注释递增那个
            // 常量，修复就会对**所有** Profile 永久失效——两者根本不在同一条版本轴上。
            // NodeDefaultsVersion 在该字段出现之前写出的每个 Profile 里都是 0，并在此向前盖章，
            // 这正是闸门需要回答的问题：「这份文件是否写在修复清单上次扩充之前？」
            if (Settings.Data.NodeDefaultsVersion < ProfileData.NodeTextDefaultsVersion)
                Settings.Data.NodeDefaultsVersion = ProfileData.NodeTextDefaultsVersion;
            string profilePath = GetProfilePath(Settings.CurrentProfile);
            string json = JsonConvert.SerializeObject(Settings.Data, ProfileData.ProfileSerializer);
            WriteAllTextSafe(profilePath, json);
        }

        /// <summary>
        /// Load a named profile into Settings.Data / 加载指定配置到 Settings.Data
        /// </summary>
        /// <remarks>On a fully successful load, records whether the disk JSON carried the
        /// full-keyboard position fields (v5→v6 needs this: only genuinely stored values may be
        /// flipped; ctor defaults must stay untouched).</remarks>
        /// <remarks>完全成功加载时记录磁盘 JSON 是否带全键盘位置字段(v5→v6 需要:只有真实
        /// 存储的值才可翻转;构造默认值必须保持不动)。</remarks>
        private bool curProfileHasFullKpsPos;

        private bool LoadProfile(string name)
        {
            string profilePath = GetProfilePath(name);
            if (!File.Exists(profilePath)) { curProfileHasFullKpsPos = false; return false; }
            curProfileHasFullKpsPos = false;
            try
            {
                string json = File.ReadAllText(profilePath);
                // A syntactically valid but truncated object can leave constructor defaults in
                // place. Count has existed in every supported profile format, so its complete
                // absence is a reliable structural-failure signal. / 可解析但被截断的对象可能留下
                // 构造默认值；Count 存在于所有支持版本，完全缺失可作为结构损坏信号。
                if (json.IndexOf("\"Count\"", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    Loader.Error($"Profile '{name}' failed validation (Count field missing)");
                    try { RotateCorruptBackup(profilePath); } catch { }
                    return false;
                }
                // Replace the instance first: FromJsonOverwrite only writes fields present in the JSON
                // and leaves any other field/array entry from the previously loaded profile intact,
                // which would then leak into (and be saved over) the new profile. A fresh default
                // instance guarantees no stale data survives a profile switch.
                // 先替换实例：FromJsonOverwrite 只写入 JSON 中存在的字段，会保留上一套配置残留的
                // 字段/数组项，这些残留随后会被保存并覆盖新配置。用全新默认实例可杜绝跨配置污染。
                // Sanity gate: a truncated/corrupt-but-parseable JSON makes FromJsonOverwrite silently
                // stop mid-way, returning true with a half-default Data that the next SaveSettings
                // would write over the user's file. IMPORTANT: builds between the profile refactor
                // and MaxKeySlots=40 legally wrote Count[36] — a short-but-present Count is resized
                // in place (same as EnsureSettingsArrays / MigrateAllProfileFiles), NOT rejected:
                // rejecting it made LoadProfileFromMeta fall back to defaults and overwrite the
                // original file, defeating the V3→V4 migration that runs later in LoadSettings.
                // Only a null or over-long Count can't come from a complete write of any version.
                // 健全性闸门：截断/损坏但可解析的 JSON 会让 FromJsonOverwrite 中途静默停止,返回
                // true 的同时留下半默认的 Data,下一次 SaveSettings 就会把它覆盖写回用户文件。
                // 重要:profile 重构到 MaxKeySlots=40 之间的版本合法写入过 Count[36]——"短但
                // 存在"的 Count 就地重定长度(与 EnsureSettingsArrays/MigrateAllProfileFiles 同
                // 法),而不是拒绝:拒绝会让 LoadProfileFromMeta 回退默认数据并覆盖原文件,
                // 摧毁稍后在 LoadSettings 运行的 V3→V4 迁移。只有 null 或超长的 Count 才不可
                // 能出自任何版本的完整写入。
                ProfileData pd = new ProfileData();
                JsonConvert.PopulateObject(json, pd, ProfileData.ProfileSerializer);
                pd.SyncArraysFromLists();
                if (pd.Count == null || pd.Count.Length > MaxKeySlots)
                {
                    Loader.Error($"Profile '{name}' failed validation (Count length {(pd.Count?.Length.ToString() ?? "null")}), backing up and falling back to defaults");
                    try { RotateCorruptBackup(profilePath); } catch { }
                    return false;
                }
                if (pd.Count.Length != MaxKeySlots)
                {
                    int[] c = new int[MaxKeySlots];
                    Array.Copy(pd.Count, c, Math.Min(pd.Count.Length, MaxKeySlots));
                    pd.Count = c;
                }
                Settings.Data = pd;
                // A profile file that predates this build's schema can still arrive here even though
                // the META was long ago upgraded: copied in by hand, restored from a backup, or
                // unpacked from a .jkv produced on an older version. The version-bump pass only runs
                // once per meta version, so such a profile would otherwise load with its foot-key
                // counts and per-key colors still on the pre-v4 slots (counters read as zero).
                // 即使 meta 早已升级，仍可能有旧版 schema 的 Profile 到达这里：手动拷入、从备份
                // 恢复，或由旧版本打出的 .jkv 解包而来。版本升级流程每个 meta 版本只跑一次，
                // 否则这类配置会带着 v4 之前的脚键计数与每键颜色加载（计数读出来是 0）。
                MigrateFootSlots(pd);
                // Record field presence ONLY on the fully-successful path — the v5→v6 flip may
                // touch stored values, never rebuild/ctor defaults. / 仅在完全成功路径记录字段
                // 存在性——v5→v6 翻转只可作用于存储值,绝不可作用于重建/构造默认值。
                curProfileHasFullKpsPos = HasRootProperty(json, "FullKpsPosition");
                // Same story for the v5→v6 Y-convention flip, and symmetric with MigrateFootSlots
                // above: a v5-form profile arriving after the meta was upgraded would never see the
                // one-shot version pass, and SaveCurrentProfile would then stamp DataVersion=6 onto
                // it — permanently locking in the OLD KPS/Total Y convention. Only flip when the
                // field is genuinely present, so constructor defaults are never touched.
                // v5→v6 的 Y 约定翻转同理，且与上面的 MigrateFootSlots 完全对称：meta 升级后才
                // 到达的 v5 形态配置永远看不到那次一次性升级，而 SaveCurrentProfile 随后会给它盖上
                // DataVersion=6——把旧的 KPS/Total Y 约定永久锁死。仅在字段确实存在时翻转，
                // 绝不触碰构造默认值。
                if (pd.DataVersion < 6 && curProfileHasFullKpsPos)
                {
                    pd.FullKpsPosition = FlipYConvention(pd.FullKpsPosition);
                    pd.FullTotalPosition = FlipYConvention(pd.FullTotalPosition);
                    pd.DataVersion = 6;
                }
                return true;
            }
            catch (Exception e)
            {
                Loader.Error($"Failed to load profile '{name}': {e.Message}");
                // Same backup as the settings.json path: without it, the caller's recovery save
                // would overwrite the file and the original content would be gone for good.
                // 与 settings.json 同款备份:否则调用方的恢复性保存会覆盖原文件,内容永久丢失。
                try { RotateCorruptBackup(profilePath); } catch { }
                return false;
            }
        }

        private bool SwitchProfile(string newName)
        {
            if (newName == Settings.CurrentProfile) return true;
            string oldName = Settings.CurrentProfile;
            try
            {
                SaveCurrentProfile();
            }
            catch (Exception e)
            {
                // The outgoing profile's pending changes could not be written. Previously the
                // exception escaped into the IMGUI caller (breaking that frame with no message) and,
                // because it bypassed SaveSettings, never reached the error banner — the user only
                // ever saw a Unity log line. Switch anyway (they asked to) but SAY so: those changes
                // are gone unless a later save succeeds.
                // 旧配置的待写改动没能落盘。此前异常会逃逸进 IMGUI 调用方（打断该帧且无任何提示），
                // 又因为绕过了 SaveSettings 而到不了错误横幅——用户只能在 Unity 日志里看到。
                // 仍然切换（是用户主动点的），但要说清楚：除非后续某次保存成功，这些改动已丢失。
                lastSaveError = e.Message;
                Loader.Error($"KeyViewer: could not save profile '{oldName}' before switching: {e.Message}");
            }
            if (!LoadProfile(newName))
            {
                Loader.Warning($"Failed to switch to profile '{newName}', staying on '{oldName}'");
                // The fallback re-load bypasses the success path below, which is what normally runs
                // EnsureSettingsArrays. In practice the old file was just rewritten by
                // SaveCurrentProfile above, but a failed write or external change could hand back a
                // legacy-length Data (Count[36]) that RefreshAllCountDisplay / the per-key color
                // editors index out of range, or unclamped enums that throw in GetLayout.
                // 回退重载绕过了下方成功路径的 EnsureSettingsArrays。实际旧文件刚被上方
                // SaveCurrentProfile 重写过，但写盘失败或外部改动可能递回旧长度的数据
                //（Count[36]），让 RefreshAllCountDisplay / 每键颜色编辑器越界索引，或未钳制的
                // 枚举在 GetLayout 抛异常——此处对称补齐。
                if (LoadProfile(oldName))
                    EnsureSettingsArrays();
                return false;
            }
            Settings.CurrentProfile = newName;
            try
            {
                EnsureSettingsArrays();
                ClearKpsTimers();
                // The editor's undo timeline describes the profile we just left — keeping it would
                // let Ctrl+Z write that layout's nodes into this one, and would leave the editor's
                // selection pointing at nodes that are gone. / 编辑器的撤销时间线描述的是刚离开的
                // 配置——留着它会让 Ctrl+Z 把那份布局的节点写进当前配置，且编辑器选中项会指向已不存在的节点。
                ResetEditorHistoryForProfileSwitch();
                cachedKeyStyle = (KeyviewerStyle)(-1);
                cachedFootStyle = (FootKeyviewerStyle)(-1);
                cachedMainKeys = null;
                cachedFootKeys = null;
                cachedGhostKeys = null;
                // Rebuild overlay for new settings. ResetKeyViewer recreates foot keys internally (it
                // destroys every child including them), so the outer ResetFootKeyViewer here would only
                // destroy and recreate them a second time.
                // 为新设置重建覆盖层。ResetKeyViewer 内部已重建脚键（它销毁含脚键在内的全部子物体），
                // 此处再调 ResetFootKeyViewer 只会把脚键销毁重建第二遍。
                ResetKeyViewer();
                UpdateAllFonts();
                UpdateAllKeyColors();
                if (Settings.Data.StreamerMode && !IsFullKeyboard)
                {
                    SetStatsVisible(false);
                }
            }
            catch (Exception e)
            {
                // The runtime rebuild threw AFTER CurrentProfile had already been switched. Left
                // alone, the in-memory name pointed at the new profile while the overlay was only
                // half rebuilt, and the next save would persist that half-state. Reload the profile
                // we came from and put the overlay back the way it was. / 重建抛异常时 CurrentProfile
                // 已经切到新配置：内存指向新配置而覆盖层只重建了一半，下次保存会把这个半成品落盘。
                // 重新加载原配置并恢复覆盖层。
                Loader.Error($"KeyViewer: switching to profile '{newName}' failed ({e.Message}); rolling back to '{oldName}'");
                try
                {
                    Settings.CurrentProfile = oldName;
                    if (LoadProfile(oldName))
                    {
                        EnsureSettingsArrays();
                        cachedKeyStyle = (KeyviewerStyle)(-1);
                        cachedFootStyle = (FootKeyviewerStyle)(-1);
                        cachedMainKeys = null;
                        cachedFootKeys = null;
                        cachedGhostKeys = null;
                        ResetEditorHistoryForProfileSwitch();
                        ResetKeyViewer();
                        UpdateAllFonts();
                        UpdateAllKeyColors();
                    }
                }
                catch (Exception rollbackError)
                {
                    Loader.Error($"KeyViewer: profile rollback to '{oldName}' also failed: {rollbackError.Message}");
                }
                return false;
            }
            SaveSettings();
            return true;
        }

        /// <summary>
        /// Delete a profile file (cannot delete the last one) / 删除配置文件（不能删除最后一个）
        /// </summary>
        private void DeleteProfile(string name)
        {
            if (Settings.ProfileNames == null || Settings.ProfileNames.Length <= 1) return;
            // If deleting the current profile, switch to first available first. Abort when the
            // switch fails (target file missing/corrupt): the old code deleted the in-use profile
            // anyway and left meta pointing at a deleted name until memory state re-created it.
            // 删除当前配置时先切到第一个可用项。切换失败则中止：旧代码照删正在使用的配置,
            // meta 会指向已删除的名字,直到内存状态把它重建出来为止。
            bool wasCurrent = Settings.CurrentProfile == name;
            if (wasCurrent)
            {
                var others = new List<string>(Settings.ProfileNames);
                others.Remove(name);
                if (!SwitchProfile(others[0])) return;
            }
            var list = new List<string>(Settings.ProfileNames);
            list.Remove(name);
            string[] previousNames = Settings.ProfileNames;
            Settings.ProfileNames = list.ToArray();
            // Update the META FIRST, then unlink. The old order deleted the file and only then
            // wrote the list, so a failed SaveMetaOnly left settings.json naming a file that no
            // longer existed — and a crash in the same window did the same. With the meta written
            // first, a failed unlink just leaves an orphan file that the next SyncProfilesWithDisk
            // adds back to the list, which is the recoverable direction.
            // 先写 meta 再删文件。旧顺序是先删文件再写列表：SaveMetaOnly 失败会让 settings.json
            // 指着一个已不存在的文件——同一窗口内崩溃也是同样结果。先写 meta 时，删除失败只留下
            // 一个孤儿文件，下次 SyncProfilesWithDisk 会把它加回列表——这才是可恢复的方向。
            try
            {
                SaveMetaOnly();
            }
            catch (Exception e)
            {
                Settings.ProfileNames = previousNames;
                Loader.Error($"Failed to update the profile list after deleting '{name}': {e.Message}");
                return;
            }
            try
            {
                string profilePath = GetProfilePath(name);
                if (File.Exists(profilePath))
                    File.Delete(profilePath);
            }
            catch (Exception e)
            {
                // Keep the name in ProfileNames when the file could not be removed; otherwise
                // meta and disk diverge until the next directory scan. / 文件删除失败时保留
                // ProfileNames，避免元数据与磁盘在下一次扫描前不一致。
                Loader.Error($"Failed to delete profile file '{name}': {e.Message}");
                var restored = new List<string>(Settings.ProfileNames);
                restored.Add(name);
                Settings.ProfileNames = restored.ToArray();
                try { SaveMetaOnly(); } catch { }
                return;
            }
        }

        /// <summary>
        /// Rename a profile / 重命名配置
        /// </summary>
        private void RenameProfile(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = SanitizeFileName(newName.Trim());
            if (oldName == newName) return;

            // Check both the in-memory list and the actual target path. A stale/orphan profile
            // file must never be deleted merely because its name is absent from ProfileNames.
            // 同时检查内存列表与真实磁盘路径：内存列表漏掉的孤儿 Profile 不能因此被删除。
            if (Settings.ProfileNames != null)
            {
                string oldSan = SanitizeFileName(oldName);
                foreach (string p in Settings.ProfileNames)
                {
                    string ps = SanitizeFileName(p);
                    if (string.Equals(ps, oldSan, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(ps, newName, StringComparison.OrdinalIgnoreCase)) return;
                }
            }

            string oldPath = GetProfilePath(oldName);
            string newPath = GetProfilePath(newName);
            bool sameFile = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
            if (!sameFile)
            {
                if (!File.Exists(oldPath))
                {
                    Loader.Error($"Cannot rename missing profile file '{oldName}'");
                    return;
                }
                if (File.Exists(newPath))
                {
                    Loader.Error($"Cannot rename profile: target file '{newName}' already exists");
                    return;
                }
                try
                {
                    File.Move(oldPath, newPath);
                }
                catch (Exception e)
                {
                    // Do not update ProfileNames/CurrentProfile after a failed move. The old
                    // profile and the meta file must remain a consistent pair.
                    // 移动失败时绝不更新 ProfileNames/CurrentProfile，保持旧 Profile 与元数据一致。
                    Loader.Error($"Failed to rename profile file '{oldName}' → '{newName}': {e.Message}");
                    return;
                }
            }

            string[] previousNames = Settings.ProfileNames == null
                ? Array.Empty<string>() : (string[])Settings.ProfileNames.Clone();
            string previousCurrent = Settings.CurrentProfile;
            var list = new List<string>(previousNames);
            int idx = list.IndexOf(oldName);
            if (idx >= 0) list[idx] = newName;
            else list.Add(newName);
            Settings.ProfileNames = list.ToArray();
            if (string.Equals(Settings.CurrentProfile, oldName, StringComparison.OrdinalIgnoreCase))
                Settings.CurrentProfile = newName;
            try
            {
                // Finish the profile write and metadata write directly so either failure can be
                // rolled back instead of being swallowed by SaveSettings' broad catch. / 直接完成
                // Profile 与元数据写入，任一步失败都可回滚，而不是被 SaveSettings 的大范围捕获吞掉。
                Settings.UiTab = settingsGuiTab;
                SaveCurrentProfile();
                SaveMetaOnly();
            }
            catch (Exception e)
            {
                Settings.ProfileNames = previousNames;
                Settings.CurrentProfile = previousCurrent;
                if (!sameFile)
                {
                    try
                    {
                        if (File.Exists(newPath) && !File.Exists(oldPath)) File.Move(newPath, oldPath);
                    }
                    catch (Exception rollbackError)
                    {
                        // The file now only exists under the NEW name, but the meta we are about to
                        // write still says oldName. That is the worst possible state: the next boot
                        // finds no file for the current profile and (before the earlier fix) would
                        // happily write a fresh default there — the user's data would appear to have
                        // vanished. Keep the new name instead: the data is reachable and
                        // SyncProfilesWithDisk will list it.
                        // 文件此时只存在于**新**名下，而即将写入的 meta 仍写着 oldName——这是最坏的
                        // 状态：下次启动找不到当前配置的文件，（在更早的修复之前）会高高兴兴地在那里
                        // 写一份全新默认值，用户的数据看起来就"消失"了。改为保留新名字：数据
                        // 仍在，且 SyncProfilesWithDisk 会把它列出来。
                        Loader.Error($"Failed to roll back profile rename: {rollbackError.Message} — keeping the new name '{newName}'");
                        Settings.ProfileNames = list.ToArray();
                        if (string.Equals(previousCurrent, oldName, StringComparison.OrdinalIgnoreCase))
                            Settings.CurrentProfile = newName;
                    }
                }
                try { SaveMetaOnly(); } catch (Exception metaError) { lastSaveError = metaError.Message; }
                Loader.Error($"Failed to save renamed profile metadata: {e.Message}");
            }
        }

        /// <summary>
        /// Sync ProfileNames with actual files on disk — remove entries with no file, recreate Default if empty / 同步配置列表与磁盘文件 — 移除无对应文件的条目，空列表时重建 Default
        /// </summary>
        private void SyncProfilesWithDisk()
        {
            if (!Directory.Exists(ProfileDir))
            {
                Directory.CreateDirectory(ProfileDir);
                Settings.ProfileNames = new[] { "Default" };
                Settings.CurrentProfile = "Default";
                SaveCurrentProfile();
                SaveMetaOnly();
                return;
            }
            var valid = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Settings.ProfileNames ?? Array.Empty<string>())
            {
                // A damaged meta can carry null/blank entries; they were added to the list verbatim
                // and then written back out, so the corruption survived every sync.
                // 损坏的 meta 可能带 null/空白条目；此前它们被原样加入列表并写回，损坏因此在每次
                // 同步后依然存在。
                if (string.IsNullOrWhiteSpace(p)) continue;
                string sp = SanitizeFileName(p);
                if (File.Exists(GetProfilePath(p)) && seen.Add(sp))
                    valid.Add(p);
            }
            var nameSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            nameSeen.UnionWith(valid.Select(v => SanitizeFileName(v)));
            foreach (string filePath in Directory.GetFiles(ProfileDir, "*.json"))
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                if (nameSeen.Add(SanitizeFileName(name)))
                    valid.Add(name);
            }
            bool changed = valid.Count != (Settings.ProfileNames?.Length ?? 0)
                || !valid.SequenceEqual(Settings.ProfileNames ?? Array.Empty<string>());
            if (valid.Count == 0)
            {
                valid.Add("Default");
                Settings.CurrentProfile = "Default";
                SaveCurrentProfile();
                changed = true;
            }
            Settings.ProfileNames = valid.ToArray();
            if (!valid.Contains(Settings.CurrentProfile))
            {
                SwitchProfile(valid[0]);
                return;
            }
            if (changed)
                SaveMetaOnly();
        }

        [System.Serializable]
        private class SettingsMeta
        {
            public int Version = 6;
            public string CurrentProfile = "Default";
            public string[] ProfileNames = new[] { "Default" };
            public string Language = "en";
            public int UiTab;
        }

    }
}
