// Mod loader abstraction / Mod 加载器抽象层
// Decouples mod core from UnityModManager / MelonLoader / etc.
// 将 Mod 核心与 UnityModManager / MelonLoader 等解耦

using System;
using System.IO;
using UnityEngine;

namespace JipperKeyViewer
{
    /// <summary>
    /// Abstract mod loader interface / 抽象 Mod 加载器接口
    /// Each supported mod loader (UMM, MelonLoader) implements this.
    /// 每个受支持的 Mod 加载器（UMM、MelonLoader）实现此接口。
    /// </summary>
    public interface IModLoader
    {
        /// <summary>Mod installation directory path / Mod 安装目录路径</summary>
        string ModPath { get; }

        /// <summary>
        /// Whether the settings window is currently shown. Used to gate the rebind capture:
        /// an armed capture must not survive a closed window (the closing hotkey would
        /// otherwise bind itself into the slot).
        /// 设置窗口当前是否显示。用于门控改键捕获:武装中的捕获不得在窗口关闭后存活
        /// (否则关窗热键会把自己绑进槽位)。
        /// </summary>
        bool IsSettingsWindowVisible { get; }

        /// <summary>
        /// The loader's settings hotkey (KeyCode.None when unknown). Same-frame protection:
        /// the core's Update may run before the loader consumes the hotkey, so capture must
        /// skip this key explicitly.
        /// 加载器的设置热键(未知时为 KeyCode.None)。同帧保护:核心 Update 可能先于加载器
        /// 消费热键运行,捕获必须显式跳过该键。
        /// </summary>
        KeyCode SettingsHotkey { get; }

        /// <summary>Log an informational message / 记录信息日志</summary>
        void Log(string message);
        /// <summary>Log a warning / 记录警告</summary>
        void Warning(string message);
        /// <summary>Log an error / 记录错误</summary>
        void Error(string message);

        /// <summary>Called every frame / 每帧调用</summary>
        event Action<float> OnUpdate;
        /// <summary>Called when the mod is toggled on/off / 开关 Mod 时调用</summary>
        event Action<bool> OnToggle;
        /// <summary>Called to draw the settings GUI / 绘制设置 GUI 时调用</summary>
        event Action OnGUI;
        /// <summary>Called when settings should be saved / 需要保存设置时调用</summary>
        event Action OnSaveGUI;

        /// <summary>
        /// Optional hook for the loader to draw extra settings rows inside the shared
        /// settings window (e.g. MelonLoader hotkey binding). UMM leaves this empty.
        /// 加载器在共享设置窗口内绘制额外设置行的可选钩子（如 MelonLoader 热键绑定）。
        /// UMM 留空。
        /// </summary>
        void DrawExtraSettings();
    }

    /// <summary>
    /// Static accessor for the active mod loader / 当前活跃 Mod 加载器的静态访问器
    /// All mod code references Loader.Instance instead of Main.Mod directly.
    /// 所有 Mod 代码通过 Loader.Instance 引用，而非直接引用 Main.Mod。
    /// </summary>
    public static class Loader
    {
        private static IModLoader instance;
        private static string warnedMissingPath;
        private static string resolvedPath;

        public static IModLoader Instance
        {
            get => instance;
            internal set
            {
                instance = value;
                // configPath / profileDir / packagesDir / resolvedPath are lazily-cached strings.
                // If the loader is ever swapped (a reload, a second loader in the same process)
                // they would keep pointing at the OLD mod directory, splitting reads and writes
                // across two folders. Clearing them here makes every cache follow the active
                // loader. Clearing on the null case too: Main.Shutdown() now assigns null, and a
                // teardown that left the old paths cached would let a subsequent instance read and
                // write a different folder than the one it reports.
                // 这些都是惰性缓存的字符串。若加载器被换掉（重载、同进程内第二个加载器），它们仍会
                // 指向**旧**模组目录，读写分裂到两个文件夹。在此清空让所有缓存跟随当前加载器。
                // null 分支同样清空：Main.Shutdown() 现在会赋 null，而若拆解时留下旧路径缓存，
                // 后续实例读写的文件夹就会与它报告的那个不是同一个。
                if (value != null) resolvedPath = null;
                warnedMissingPath = null;
                global::JipperKeyViewer.KeyViewer.KeyViewer.ResetCachedPaths();
            }
        }

        /// <summary>Mod installation path (shorthand) / Mod 安装路径（简写）</summary>
        public static string ModPath => Instance?.ModPath;

        /// <summary>Mod path that is ALWAYS usable, for anything that writes to disk. A null
        /// Instance is reachable in practice: MelonLoader's preferences creation can throw before
        /// Main.Init runs, and any third-party loader that forgets Init does the same. The old
        /// `?? "."` fallback made ModPath non-null ALWAYS, so every writer — config/, assets/,
        /// CustomFont/, CustomImages/, Packages/, .jkv-staging/ — resolved against the process
        /// working directory, i.e. the GAME INSTALL FOLDER. On a read-only install that is a wall
        /// of UnauthorizedAccessException; on a writable one it litters the game directory and the
        // settings vanish with the next game update.
        /// 恒可用的 Mod 路径，供一切需要写盘的地方使用。Instance 为 null 在实践中可达：
        /// MelonLoader 的偏好创建可能在 Main.Init 之前抛异常，任何忘记 Init 的第三方加载器同样。
        /// 旧的 `?? "."` 兜底让 ModPath **永远非 null**，于是所有写入方（config/、assets/、
        /// CustomFont/、CustomImages/、Packages/、.jkv-staging/）都相对进程工作目录解析，也就是
        /// **游戏安装目录**。只读安装下是一连串 UnauthorizedAccessException；可写时则把配置散落
        /// 在游戏目录里，并随游戏更新一起消失。
        public static string ResolveModPath()
        {
            // Cached: this runs per node while a custom layout resolves its image paths, and the
            // Directory.Exists probe is a filesystem hit. / 缓存：解析自定义布局的图片路径时按节点
            // 调用，而 Directory.Exists 是一次文件系统访问。
            if (resolvedPath != null) return resolvedPath;
            string path = ModPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    if (Directory.Exists(path)) { resolvedPath = path; return resolvedPath; }
                }
                catch (Exception) { /* fall through to the fallback / 落到兜底 */ }
            }
            if (warnedMissingPath == null)
            {
                warnedMissingPath = path ?? "(null)";
                // Resolve ONCE and use that exact value. This used to compute `fallback` inline for
                // the log line and then assign `resolvedPath = CurrentFallback()` — a SECOND,
                // independent probe. On a host where Application.persistentDataPath is flaky (the
                // very case the comments here name: offline test runners, some Wine/Proton setups)
                // the two could disagree, and the one diagnostic this whole chain exists to emit
                // would name the wrong directory — which is exactly where the user goes looking
                // for the settings that "disappeared". The fallback POLICY also lived in two
                // places (this block and CurrentFallback), so the round-40 argument against ever
                // accepting "." would have had to be made twice, and could have been made once.
                //
                // 只解析一次并使用那个确切的值。此前先就地算出 `fallback` 供日志、再另行
                // `resolvedPath = CurrentFallback()` 做**第二次**独立探测。在
                // Application.persistentDataPath 不稳的宿主上（正是这里注释点名的场景：离线测试
                // 运行器、部分 Wine/Proton），两者可能不一致，而这条链唯一发出的诊断就会指向
                // **错误**的目录——而那恰恰是用户去找「消失的配置」时会看的地方。兜底**策略**同样
                // 散落两处，于是第 40 轮那套「绝不能接受 `.`」的论证得讲两遍，且可能只改一遍。
                string fallback = CurrentFallback();
                Error($"KeyViewer: no usable mod folder ('{warnedMissingPath}'); falling back to {fallback}. The mod's settings, images, videos and packages will live there instead of next to the game.");
                warnedMissingPath = warnedMissingPath + "->" + fallback;
                resolvedPath = fallback;
                return resolvedPath;
            }
            resolvedPath = CurrentFallback();
            return resolvedPath;
        }

        /// <summary>The single copy of the fallback policy: persistent data path, then the temp
        /// directory, then the process directory. Every branch is a last resort and all of them are
        /// reported — this is the only place the user learns where their settings went.
        /// / 兜底策略的**唯一**一份实现：持久化路径，其次临时目录，最后进程目录。每个分支都是最后
        /// 手段，且都会被上报——用户得知配置去了哪里的唯一途径就是这里。</summary>
        private static string CurrentFallback()
        {
            try { string p = Application.persistentDataPath; if (!string.IsNullOrWhiteSpace(p)) return p; }
            catch (Exception e) { Error($"KeyViewer: could not resolve the persistent data path: {e.Message}"); }
            try { return Path.GetTempPath(); } catch (Exception) { return "."; }
        }

        public static void Log(string msg)   { Instance?.Log(msg); }
        public static void Warning(string msg) { Instance?.Warning(msg); }
        public static void Error(string msg) { Instance?.Error(msg); }

        /// <summary>
        /// Invoke the loader's optional extra settings drawing hook, if any.
        /// 调用加载器可选的额外设置绘制钩子（若有）。
        /// </summary>
        public static void DrawExtraSettingsUI() => Instance?.DrawExtraSettings();
    }
}
