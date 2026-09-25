// Mod core entry point — loader-agnostic / Mod 核心入口 — 加载器无关
// Called by loader-specific assemblies (UMM, MelonLoader) / 由加载器专属程序集调用
using JipperKeyViewer.KeyViewer;
using UnityEngine;

namespace JipperKeyViewer
{
    /// <summary>
    /// Mod core entry point / Mod 核心入口
    /// Initialises the mod when called by a loader-specific assembly.
    /// 由加载器专属程序集调用以初始化 Mod。
    /// </summary>
    public static class Main
    {
        /// <summary>The persistent GameObject hosting the KeyViewer component / 持有 KeyViewer 组件的持久化 GameObject</summary>
        static GameObject KeyViewerGO;
        /// <summary>Init guard — a second Init call would double-subscribe every loader event. / Init 防护——二次调用会让每个加载器事件被重复订阅。</summary>
        static bool initialized;
        /// <summary>The loader the subscriptions below belong to, plus the delegates themselves.
        /// C# forbids assigning null to an event from outside its declaring type, so a teardown
        /// needs something it can actually -= . Keeping the references is what makes Shutdown
        /// possible at all. / 上述订阅所属的加载器，以及这些委托本身。C# 不允许在声明类型之外给事件
        /// 赋 null，故拆解必须有可 -= 的东西；保留引用正是 Shutdown 能存在的前提。</summary>
        static IModLoader subscribedLoader;
        static System.Action<bool> onToggleHandler;
        static System.Action onGuiHandler;
        static System.Action onSaveGuiHandler;

        /// <summary>
        /// Initialise the mod with the given loader implementation / 使用指定的加载器实现初始化 Mod
        /// Called by loader-specific entry points (UMM, MelonLoader).
        /// 由加载器专属入口（UMM、MelonLoader）调用。
        /// </summary>
        public static void Init(IModLoader loader)
        {
            // Defensive: both shipped loaders call Init exactly once, but a double call would
            // double-subscribe OnToggle/OnGUI/OnSaveGUI (duplicate overlay toggling, double saves).
            // 防御性:两个加载器都只调一次 Init,但重复调用会双重订阅 OnToggle/OnGUI/OnSaveGUI
            //(覆盖层重复开关、双重保存)。
            if (initialized)
            {
                // Refuse LOUDLY rather than silently. The UMM entry wires its handler's events in
                // its CONSTRUCTOR and only then calls Init — so a second loader in the process left
                // every one of those delegates unsubscribed, Loader.Instance still pointing at the
                // previous handler, and the settings panel drawing nothing. UMM still reported the
                // mod as loaded, so the user saw an empty settings window and no error anywhere.
                // 明确报错而非静默。UMM 入口在其**构造函数**里就挂好了 handler 的事件、之后才调
                // Init——故同进程第二个加载器会让这些委托一个都没订阅，Loader.Instance 仍指向旧的
                // handler，设置面板画不出任何内容。UMM 仍会报告 Mod 已加载，于是用户只看到一个空的
                // 设置窗口，日志里什么都没有。
                global::JipperKeyViewer.Loader.Error("JipperKeyViewer: a second loader tried to "
                    + "initialize the mod; ignoring it (the first loader stays in charge). "
                    + "同一进程内第二个加载器试图初始化 Mod，已忽略（仍由第一个加载器负责）。");
                return;
            }

            // Load the embedded TGT compat shim BEFORE anything else: the replay bootstrap
            // scans loaded assemblies during startup, so the "KeyViewer" assembly must be in
            // the AppDomain by then — no separate Mods/KeyViewer folder or mod-list entry.
            // 尽早加载内嵌的 TGT 兼容垫片：回放引导器在启动期间扫描已加载程序集，"KeyViewer"
            // 程序集必须在此之前进入 AppDomain——不再需要独立的 Mods/KeyViewer 文件夹或
            // mod 列表条目。
            KeyViewer.KeySource.EnsureShimLoaded();

            Loader.Instance = loader;

            subscribedLoader = loader;
            onToggleHandler = (enabled) =>
            {
                if (enabled) EnableKeyViewer();
                else DisableKeyViewer();
            };

            // Unity-aware null checks (?. bypasses the destroyed-object check on UnityEngine.Object) /
            // Unity 感知的空检查（?. 会绕过 UnityEngine.Object 的已销毁判断）
            onGuiHandler = () => { var kv = KeyViewer.KeyViewer.instance; if (kv != null) kv.DrawSettingsWindow(); };
            onSaveGuiHandler = () => { var kv = KeyViewer.KeyViewer.instance; if (kv != null) kv.SaveSettings(); };
            loader.OnToggle += onToggleHandler;
            loader.OnGUI += onGuiHandler;
            loader.OnSaveGUI += onSaveGuiHandler;

            // Set LAST. It used to be set first, so a throw anywhere above (a loader whose
            // OnToggle += rejects duplicate handlers, a shim load that fails on a stripped
            // install) left Instance already written but the guard permanently latched: the mod
            // could never initialise again, and without a loader every path resolved against the
            // game install folder. / 最后才置位。此前置位在前，于是上面任何一处抛错都会让
            // Instance 已写入而门永久锁死：Mod 再也无法初始化，且缺少加载器时所有路径都解析到
            // 游戏安装目录。
            initialized = true;
        }

        /// <summary>
        /// Call this after Init() if the loader doesn't fire OnToggle (e.g. MelonLoader).
        /// Ensures the overlay is created immediately.
        /// 在 Init() 之后调用，用于不会触发 OnToggle 的加载器（如 MelonLoader）。
        /// 确保立即创建覆盖层。
        /// </summary>
        public static void EnableNow()
        {
            EnableKeyViewer();
        }

        internal static void EnableKeyViewer()
        {
            if (KeyViewerGO != null) return;
            KeyViewerGO = new GameObject("JipperKeyViewer");
            GameObject.DontDestroyOnLoad(KeyViewerGO);
            KeyViewerGO.AddComponent<KeyViewer.KeyViewer>();
        }

        internal static void DisableKeyViewer()
        {
            if (KeyViewerGO == null) return;
            GameObject.Destroy(KeyViewerGO);
            KeyViewerGO = null;
        }

        /// <summary>Full teardown, for a loader UNLOAD or reload. DisableKeyViewer alone was not
        /// enough: it is also the mod's own "turn the display off" path, and it leaves the mod's
        /// subscription state intact. MelonLoader unloading a mod does not fire OnToggle(false),
        /// so the overlay GameObject (DontDestroyOnLoad) survived and kept drawing, kept running
        /// its Update and kept counting presses with no UI left to stop it. Reloading a NEWER DLL
        /// then produced a second JipperKeyViewer component alongside the first — two canvases
        /// stacked and two Update loops reading the same physical keys, so every keypress counted
        /// TWICE, while `KeyViewer.instance` pointed at whichever Awake ran last and the older
        /// component kept writing the profile.
        /// 完整拆解，供加载器**卸载或重载**时调用。单靠 DisableKeyViewer 不够：它同时也是 Mod
        /// 自身的「关掉显示」路径，会保留订阅状态。MelonLoader 卸载 Mod 不会触发 OnToggle(false)，
        /// 于是带 DontDestroyOnLoad 的覆盖层 GameObject 存活并继续绘制、继续跑 Update、继续计数，
        /// 而界面上已没有任何东西能关掉它。随后用**新版 DLL** 重载会多出第二个
        /// JipperKeyViewer 组件——两层画布叠加、两条 Update 循环读同一批物理按键，于是每次按压
        /// 计数**翻倍**，而 `KeyViewer.instance` 指向最后 Awake 的那个，旧组件仍在写配置。
        /// </summary>
        public static void Shutdown()
        {
            DisableKeyViewer();
            // Unsubscribe first: the loader may outlive us and fire these during teardown. C#
            // only allows -= from outside the declaring type, so the delegates were kept in
            // fields at Init time — without that, teardown was simply not expressible.
            // 先退订：加载器可能比我们活得久，并在拆解期间触发它们。C# 只允许在声明类型之外用
            // -=，故这些委托在 Init 时被保留在字段里——不这样保存，拆解根本无法表达。
            if (subscribedLoader != null)
            {
                if (onToggleHandler != null) subscribedLoader.OnToggle -= onToggleHandler;
                if (onGuiHandler != null) subscribedLoader.OnGUI -= onGuiHandler;
                if (onSaveGuiHandler != null) subscribedLoader.OnSaveGUI -= onSaveGuiHandler;
            }
            subscribedLoader = null;
            onToggleHandler = null;
            onGuiHandler = null;
            onSaveGuiHandler = null;
            Loader.Instance = null;
            initialized = false;
        }
    }
}
