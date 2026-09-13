using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        /// <summary>Whether the current layout has a third row of keys / 当前布局是否有第三排按键</summary>
        internal static bool HasThirdRow => Settings.Data.KeyViewerStyle is KeyviewerStyle.Key20 or KeyviewerStyle.Key24;
        /// <summary>Maximum key slots (keys can be at indices 0..MaxKeySlots-1) / 最大键位槽数</summary>
        internal const int MaxKeySlots = 40;
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
                    string modPath = Loader.ModPath;
                    configPath = Path.Combine(modPath ?? Application.persistentDataPath, "config", "settings.json");
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
                    string modPath = Loader.ModPath;
                    profileDir = Path.Combine(modPath ?? Application.persistentDataPath, "config", "profiles");
                }
                return profileDir;
            }
        }
        static string profileDir;

        /// <summary>Sanitize a profile name for use as a filename / 将配置名称净化用于文件名</summary>
        static string SanitizeFileName(string name)
        {
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
        /// <summary>All non-joystick KeyCodes, cached for input detection / 所有非摇杆按键代码缓存，用于按键检测</summary>
        private static readonly KeyCode[] AllKeyCodes;
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
                CheckResolutionChanged();
                long now = Stopwatch.ElapsedMilliseconds;
                ProcessKeySelection();              // Handle key rebinding input / 处理按键重新绑定输入
                if (IsCustomLayout)
                {
                    // FreeMake nodes: bindings/counters live on the nodes; ghosts included. /
                    // FreeMake 节点：绑定与计数在节点上，鬼键一并处理。
                    ProcessCustomKeysInUpdate(now);
                }
                else
                {
                    ProcessMainAndFootKeysInUpdate(now); // Detect key presses / 检测按键按下
                    ProcessGhostKeysInUpdate();          // Process ghost key inputs / 处理鬼键输入
                    if (Settings.Data.EnableRainEffect) rainSystem.UpdateEffects(Keys); // Update rain drop positions / 更新雨滴位置
                }
                ProcessKpsInUpdate(now);            // Update KPS counter / 更新 KPS 计数器
                ProcessPerKeyKpsInUpdate(now);       // Update per-key KPS / 更新每键 KPS
                if (IsCustomLayout) TickCounterBounces(); // counter bounce animations / 计数器弹跳动画
            }
        }

        // ======================== Config Management / 配置管理 ========================

        private void LoadSettings()
        {
            string directory = Path.GetDirectoryName(ConfigPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

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

        /// <summary>Copy the config meta + current profile to *.corrupt backups before falling back to defaults / 回退默认前把配置元数据与当前 Profile 备份为 *.corrupt</summary>
        private void BackupCorruptConfig()
        {
            try
            {
                if (File.Exists(ConfigPath)) File.Copy(ConfigPath, ConfigPath + ".corrupt", true);
                string cur = Settings?.CurrentProfile;
                if (!string.IsNullOrEmpty(cur))
                {
                    string pp = GetProfilePath(cur);
                    if (File.Exists(pp)) File.Copy(pp, pp + ".corrupt", true);
                }
            }
            catch { /* best-effort backup; the load failure is already reported / 尽力备份；加载失败已另行报告 */ }
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
        }

        private void MigrateV2toV3()
        {
            Loader.Log("Migrating settings v2 → v3: creating Default profile");
            Settings.Version = 3;
            Settings.CurrentProfile = "Default";
            Settings.ProfileNames = new[] { "Default" };
            EnsureSettingsArrays();
            SaveCurrentProfile();
            SaveMetaOnly();
            Loader.Log("Migration v2→v3 complete");
        }

        private void MigrateV3toV4()
        {
            Loader.Log("Migrating settings v3 → v4: FootKeyBase fixed to 24");
            Settings.Version = 4;
            var d = Settings.Data;
            const int oldFootBase = 20;

            if (d.KeyViewerStyle == KeyviewerStyle.Key24)
            {
                // The current profile needs no shift, but the OTHER profile files still do — the
                // early return used to skip MigrateAllProfileFiles entirely, and since the meta
                // Version gate never re-runs the migration, their foot-key counts stayed on the
                // old 20-base slots forever (permanently zeroed foot counters after the switch).
                // 当前配置无需平移,但其余 Profile 文件仍需要——早退曾整体跳过
                // MigrateAllProfileFiles,而 meta Version 门控不会再补跑,它们的脚键计数
                // 永远留在旧的 20 基线槽位(切换后脚键计数永久为零)。
                SaveCurrentProfile();
                MigrateAllProfileFiles();
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
                SaveCurrentProfile();
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

            SaveCurrentProfile();
            MigrateAllProfileFiles();
            SaveMetaOnly();
            Loader.Log("Migration v3→v4 complete");
        }

        private void MigrateV4toV5()
        {
            // No array reshaping: a dedicated key108 array is filled lazily by EnsureSettingsArrays.
            // Existing 8K-24K + foot-key profiles load unchanged.
            Settings.Version = 5;
            EnsureSettingsArrays();
            SaveCurrentProfile();
            SaveMetaOnly();
            Loader.Log("Migration v4→v5 complete");
        }

        /// <summary>Flip a normalized position to the mod-wide Y convention (0=top, 1=bottom). / 将归一化位置翻转为全 Mod 的 Y 约定(0=顶,1=底)。</summary>
        private static Vector2 FlipYConvention(Vector2 v) => new Vector2(v.x, Mathf.Clamp01(1f - v.y));

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
            if (metaVersionOnDisk >= 5 && curProfileHasFullKpsPos)
            {
                Settings.Data.FullKpsPosition = FlipYConvention(Settings.Data.FullKpsPosition);
                Settings.Data.FullTotalPosition = FlipYConvention(Settings.Data.FullTotalPosition);
            }
            SaveCurrentProfile();

            // Batch-flip the other profile files the same way — the meta Version gate never
            // re-runs this migration, so switching to them later must not resurrect old-convention
            // Y values. Each file is content-checked (a v4-form dormant file is skipped).
            // / 同法批量翻转其余 Profile 文件——meta 版本门控不会重跑本迁移,之后切到它们时
            // 不能让旧约定的 Y 值复活。逐文件检查内容(v4 形态的休眠文件跳过)。
            if (metaVersionOnDisk >= 5 && Settings.ProfileNames != null)
            {
                string savedProfile = Settings.CurrentProfile;
                foreach (string name in Settings.ProfileNames)
                {
                    if (name == savedProfile) continue;
                    try
                    {
                        string path = GetProfilePath(name);
                        if (!File.Exists(path)) continue;
                        string raw = File.ReadAllText(path);
                        if (!raw.Contains("FullKpsPosition")) continue; // dormant v4-form file / 休眠的 v4 形态文件
                        var pd = new ProfileData();
                        JsonUtility.FromJsonOverwrite(raw, pd);
                        pd.SyncArraysFromLists();
                        pd.FullKpsPosition = FlipYConvention(pd.FullKpsPosition);
                        pd.FullTotalPosition = FlipYConvention(pd.FullTotalPosition);
                        pd.SyncListsToArrays();
                        WriteAllTextSafe(path, JsonConvert.SerializeObject(pd, Formatting.Indented, ProfileData.ProfileSerializer));
                    }
                    catch (Exception e)
                    {
                        Loader.Warning($"Failed to migrate profile '{name}' to v6: {e.Message}");
                    }
                }
            }
            SaveMetaOnly();
            Loader.Log("Migration v5→v6 complete");
        }

        private void MigrateAllProfileFiles()
        {
            if (Settings.ProfileNames == null) return;
            string savedProfile = Settings.CurrentProfile;
            foreach (string name in Settings.ProfileNames)
            {
                if (name == savedProfile) continue;
                try
                {
                    string path = GetProfilePath(name);
                    if (!File.Exists(path)) continue;
                    string json = File.ReadAllText(path);
                    var pd = new ProfileData();
                    JsonUtility.FromJsonOverwrite(json, pd);
                    if (pd.KeyViewerStyle == KeyviewerStyle.Key24) continue;
                    int fs = pd.FootKeyViewerStyle switch
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
                    if (fs == 0) continue;
                    const int oldBase = 20;
                    // Old builds wrote Count[36]; FromJsonOverwrite restores that shorter array and the
                    // copy below would run past its end (throwing, and the profile would then be
                    // skipped forever because the meta Version already advanced). Resize first, the
                    // same way EnsureSettingsArrays handles the live settings.
                    // 旧版本写入的是 Count[36]；FromJsonOverwrite 会还原成短数组，下面的复制会越界
                    //（抛异常后该 Profile 被永久跳过——meta 的 Version 已经先升上去了）。先按
                    // EnsureSettingsArrays 处理在线设置的同样方式重定长度。
                    if (pd.Count == null || pd.Count.Length != MaxKeySlots)
                    {
                        int[] c = new int[MaxKeySlots];
                        if (pd.Count != null) Array.Copy(pd.Count, c, Math.Min(pd.Count.Length, MaxKeySlots));
                        pd.Count = c;
                    }
                    // 36-era files also carried shorter PerKey color arrays (38 = 36+2): the shifts
                    // below only write inside the old length, so migrating a dormant profile with
                    // footSize 16 silently dropped the tail slots (foot keys 14/15). Resize first —
                    // the tail fills from the profile's own global colors, the same fill
                    // EnsureSettingsArrays applies when a newer build loads a short array.
                    // 36-era 文件的 PerKey 颜色数组同样更短（38 = 36+2）：下方的平移只写入旧长度
                    // 之内，休眠 Profile 带 16K 脚键时会把尾部槽位静默丢掉（脚键 14/15）。先重定
                    // 长度——尾部用该 Profile 自己的全局色填充，与新版加载短数组时
                    // EnsureSettingsArrays 的填充语义一致。
                    pd.PerKeyBackground = EnsureColorArray(pd.PerKeyBackground, MaxKeySlots + 2, pd.Background);
                    pd.PerKeyBackgroundClicked = EnsureColorArray(pd.PerKeyBackgroundClicked, MaxKeySlots + 2, pd.BackgroundClicked);
                    pd.PerKeyOutline = EnsureColorArray(pd.PerKeyOutline, MaxKeySlots + 2, pd.Outline);
                    pd.PerKeyOutlineClicked = EnsureColorArray(pd.PerKeyOutlineClicked, MaxKeySlots + 2, pd.OutlineClicked);
                    pd.PerKeyText = EnsureColorArray(pd.PerKeyText, MaxKeySlots + 2, pd.Text);
                    pd.PerKeyTextClicked = EnsureColorArray(pd.PerKeyTextClicked, MaxKeySlots + 2, pd.TextClicked);
                    pd.PerKeyRainColor = EnsureColorArray(pd.PerKeyRainColor, MaxKeySlots + 2, pd.RainColor);
                    Array.Copy(pd.Count, oldBase, pd.Count, FootKeyBase, fs);
                    // Gap-only clear — the full-range clear overlapped the just-copied entries
                    // when fs > (FootKeyBase - oldBase). Mirror of the live-migration fix above.
                    // 仅清间隙——fs > (FootKeyBase - oldBase) 时全区间清除会重叠刚复制的条目。
                    // 与上方在线迁移的修复互为镜像。
                    Array.Clear(pd.Count, oldBase, Math.Min(fs, FootKeyBase - oldBase));
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
                    Shift(pd.PerKeyBackground, oldBase, FootKeyBase, fs);
                    Shift(pd.PerKeyBackgroundClicked, oldBase, FootKeyBase, fs);
                    Shift(pd.PerKeyOutline, oldBase, FootKeyBase, fs);
                    Shift(pd.PerKeyOutlineClicked, oldBase, FootKeyBase, fs);
                    Shift(pd.PerKeyText, oldBase, FootKeyBase, fs);
                    Shift(pd.PerKeyTextClicked, oldBase, FootKeyBase, fs);
                    Shift(pd.PerKeyRainColor, oldBase, FootKeyBase, fs);
                    pd.SyncListsToArrays();
                    WriteAllTextSafe(path, JsonConvert.SerializeObject(pd, Formatting.Indented, ProfileData.ProfileSerializer));
                }
                catch (Exception e)
                {
                    Loader.Warning($"Failed to migrate profile '{name}': {e.Message}");
                }
            }
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
                Loader.Warning($"Profile '{profileName}' not found, creating new profile");
                Settings.CurrentProfile = profileName;
                if (Settings.ProfileNames == null || Settings.ProfileNames.Length == 0)
                    Settings.ProfileNames = new[] { profileName };
                EnsureSettingsArrays();
                SaveCurrentProfile();
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
            return (KeyCode[])defaults.Clone();
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
            // the binding tab indexes key8[key12 slots] unguarded. A wrong-length array is replaced
            // with the field initializer defaults from a fresh ProfileData.
            // 截断的绑定数组(手改/写坏一半的 Profile)与下方 Count 属同类缺口:FromJsonOverwrite
            // 按 JSON 自带长度还原,而按键页会无守卫地索引 key8[第 12 槽]。长度不对时直接换回
            // 全新 ProfileData 的字段初始化默认值。
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
            int n = MaxKeySlots + 2;
            Settings.Data.PerKeyBackground = EnsureColorArray(Settings.Data.PerKeyBackground, n, Settings.Data.Background);
            Settings.Data.PerKeyBackgroundClicked = EnsureColorArray(Settings.Data.PerKeyBackgroundClicked, n, Settings.Data.BackgroundClicked);
            Settings.Data.PerKeyOutline = EnsureColorArray(Settings.Data.PerKeyOutline, n, Settings.Data.Outline);
            Settings.Data.PerKeyOutlineClicked = EnsureColorArray(Settings.Data.PerKeyOutlineClicked, n, Settings.Data.OutlineClicked);
            Settings.Data.PerKeyText = EnsureColorArray(Settings.Data.PerKeyText, n, Settings.Data.Text);
            Settings.Data.PerKeyTextClicked = EnsureColorArray(Settings.Data.PerKeyTextClicked, n, Settings.Data.TextClicked);
            Settings.Data.PerKeyRainColor = EnsureColorArray(Settings.Data.PerKeyRainColor, n, Settings.Data.RainColor);
            // Same gap class as Count: these two are indexed unguarded by the rain system
            // (PerKeyGhostRainColor) and the per-key font path, and were only sized by the
            // constructor that FromJsonOverwrite bypasses. / 与 Count 同类缺口:雨滴系统未加守卫地
            // 索引 PerKeyGhostRainColor,每键字号路径亦然;此前仅靠被 FromJsonOverwrite 绕过的
            // 构造函数定长。
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
                }
            }
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
            }
            catch (Exception e)
            {
                Loader.Error($"Failed to save settings: {e.Message}");
            }
        }

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
            File.WriteAllText(tmp, contents);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
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
                    try { File.Copy(profilePath, profilePath + ".corrupt", true); } catch { }
                    return false;
                }
                if (pd.Count.Length != MaxKeySlots)
                {
                    int[] c = new int[MaxKeySlots];
                    Array.Copy(pd.Count, c, Math.Min(pd.Count.Length, MaxKeySlots));
                    pd.Count = c;
                }
                Settings.Data = pd;
                // Record field presence ONLY on the fully-successful path — the v5→v6 flip may
                // touch stored values, never rebuild/ctor defaults. / 仅在完全成功路径记录字段
                // 存在性——v5→v6 翻转只可作用于存储值,绝不可作用于重建/构造默认值。
                curProfileHasFullKpsPos = json.Contains("FullKpsPosition");
                return true;
            }
            catch (Exception e)
            {
                Loader.Error($"Failed to load profile '{name}': {e.Message}");
                // Same backup as the settings.json path: without it, the caller's recovery save
                // would overwrite the file and the original content would be gone for good.
                // 与 settings.json 同款备份:否则调用方的恢复性保存会覆盖原文件,内容永久丢失。
                try { File.Copy(profilePath, profilePath + ".corrupt", true); } catch { }
                return false;
            }
        }

        private bool SwitchProfile(string newName)
        {
            if (newName == Settings.CurrentProfile) return true;
            string oldName = Settings.CurrentProfile;
            SaveCurrentProfile();
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
            EnsureSettingsArrays();
            ClearKpsTimers();
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
            // Now delete the file and remove from list / 然后删文件和列表
            try
            {
                string profilePath = GetProfilePath(name);
                if (File.Exists(profilePath))
                    File.Delete(profilePath);
            }
            catch (Exception e)
            {
                Loader.Error($"Failed to delete profile file '{name}': {e.Message}");
            }
            var list = new List<string>(Settings.ProfileNames);
            list.Remove(name);
            Settings.ProfileNames = list.ToArray();
            SaveMetaOnly();
        }

        /// <summary>
        /// Rename a profile / 重命名配置
        /// </summary>
        private void RenameProfile(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(newName)) return;
            newName = SanitizeFileName(newName.Trim());
            if (oldName == newName) return;
            // Case-insensitive duplicate check against OTHER profiles: on NTFS a case variant names
            // the same file, so the File.Delete below would remove that other profile before the
            // move. This profile's own old name is excluded — a case-only rename stays allowed.
            // 对其它 Profile 做大小写不敏感的重名检查：NTFS 上大小写变体指向同一文件，否则下面
            // 的 File.Delete 会在移动前删掉那个 Profile。本配置自身的旧名除外——仅改大小写的
            // 重命名仍然允许。
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
            if (oldPath != newPath)
            {
                try
                {
                    if (File.Exists(oldPath))
                    {
                        // Case-only rename: oldPath and newPath name the SAME file on NTFS — deleting
                        // the target first would delete the source; File.Move alone performs the
                        // case change. / 仅改大小写：两个路径在 NTFS 上是同一个文件——先删目标
                        // 等于删源；仅 File.Move 即可完成大小写重命名。
                        bool sameFile = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
                        if (!sameFile && File.Exists(newPath))
                            File.Delete(newPath);
                        File.Move(oldPath, newPath);
                    }
                }
                catch (Exception e)
                {
                    Loader.Error($"Failed to rename profile file '{oldName}' → '{newName}': {e.Message}");
                }
            }
            var list = new List<string>(Settings.ProfileNames);
            int idx = list.IndexOf(oldName);
            if (idx >= 0) list[idx] = newName;
            else list.Add(newName);
            Settings.ProfileNames = list.ToArray();
            if (Settings.CurrentProfile == oldName)
                Settings.CurrentProfile = newName;
            SaveSettings();
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
