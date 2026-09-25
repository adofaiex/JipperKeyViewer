// MelonLoader loader entry / MelonLoader 加载器入口
using System;
using System.IO;
using JipperKeyViewer;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(JipperKeyViewer.LoaderMelon.JipperMelonMod), "Jipper Key Viewer", "1.7.2", "HitMargin", null)]

namespace JipperKeyViewer.LoaderMelon
{
    public class JipperMelonMod : MelonMod
    {
        private MelonHandler _handler;
        private bool _initialized;
        private bool _settingsWindowVisible;
        private Rect _settingsRect;
        private Vector2 _settingsScroll;

        private static MelonPreferences_Category _prefs;
        private static MelonPreferences_Entry<string> _hotkeyEntry;
        private bool _capturingHotkey;

        /// <summary>Window visibility for the rebind-capture gate (IModLoader). / 供改键捕获门控查询的窗口可见性(IModLoader)。</summary>
        internal bool IsSettingsWindowVisible => _settingsWindowVisible;

        /// <summary>Currently configured settings hotkey (None when unset/unparseable). / 当前配置的设置热键(未设置/不可解析时为 None)。</summary>
        internal KeyCode CurrentHotkey =>
            Enum.TryParse(_hotkeyEntry?.Value, true, out KeyCode k) ? k : KeyCode.None;

        public override void OnInitializeMelon()
        {
            // Preference creation can throw (a locked/corrupt MelonPreferences.cfg, a read-only
            // UserData folder). Previously the exception aborted OnInitializeMelon, so _handler was
            // never built and Main.Init never ran — yet OnSceneWasInitialized still called
            // Main.EnableNow(), which then operated with Loader.Instance == null. That is exactly
            // the state that used to resolve every path against the GAME INSTALL FOLDER. Fail
            // loudly and stay OFF instead; the user can see why and fix the prefs file.
            // 偏好创建可能抛异常（MelonPreferences.cfg 被锁/损坏、UserData 目录只读）。此前异常会
            // 中断 OnInitializeMelon，_handler 未建、Main.Init 未跑——而 OnSceneWasInitialized
            // 仍会调 Main.EnableNow()，此时 Loader.Instance 为 null，正是过去会把所有路径解析到
            // **游戏安装目录**的状态。改为明确报错并保持关闭，让用户看得见原因并修好偏好文件。
            try
            {
                _prefs = MelonPreferences.CreateCategory("JipperKeyViewer", "Jipper Key Viewer");
                _hotkeyEntry = _prefs.CreateEntry("Hotkey", "F1",
                    "Settings Hotkey", "Key to open/close settings window");
            }
            catch (System.Exception e)
            {
                LoggerInstance.Error($"[JipperKeyViewer] could not read/create MelonPreferences — the mod stays disabled: {e}");
                _hotkeyEntry = null;
                return;
            }

            _handler = new MelonHandler(this);
            Main.Init(_handler);
        }

        public override void OnDeinitializeMelon()
        {
            // MelonLoader does NOT fire OnToggle(false) when a mod is unloaded or reloaded, so
            // without this the DontDestroyOnLoad overlay GameObject survived: it kept drawing, kept
            // running Update, and kept counting presses with no UI left to stop it. Reloading a
            // NEWER DLL then added a SECOND JipperKeyViewer component next to the first — two
            // canvases stacked, two Update loops reading the same physical keys, so every keypress
            // counted twice while the older component kept writing the profile.
            // MelonLoader 卸载或重载 Mod 时**不会**触发 OnToggle(false)，故没有这一句时带
            // DontDestroyOnLoad 的覆盖层会存活：继续绘制、继续跑 Update、继续计数，而界面上已没有
            // 任何东西能关掉它。随后用**新版 DLL** 重载会在旁边多出**第二个** JipperKeyViewer
            // 组件——两层画布叠加、两条 Update 循环读同一批物理按键，于是每次按压计数翻倍，
            // 而旧组件仍在写配置。
            Main.Shutdown();
            _handler = null;
            _initialized = false;
            _hotkeyEntry = null;
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!_initialized)
            {
                _initialized = true;
                // Only enable when Init actually ran. If preference creation failed, EnableNow()
                // would bring the mod up with no loader — the state that used to write the mod's
                // files into the game install folder.
                // 仅在 Init 确实跑过时才启用。偏好创建失败时 EnableNow() 会在没有加载器的情况下
                // 启动 Mod——正是过去会把 Mod 文件写进游戏安装目录的状态。
                if (_handler == null) return;
                Main.EnableNow();
            }
        }

        public override void OnUpdate()
        {
            // Init nulls this when preference creation fails (the mod stays disabled). MelonLoader
            // still calls OnUpdate every frame regardless, so without this guard the disabled state
            // produced a NullReferenceException 60+ times a second — burying the one real error
            // line under thousands of stack traces, and making "stays disabled" look like "spams
            // errors". / Init 在偏好创建失败时把它置 null（Mod 保持关闭）。MelonLoader 无论如何都
            // 每帧调 OnUpdate，故无此守卫时"保持关闭"的状态每秒产生 60+ 次 NullReferenceException
            // ——真正的那行错误会被上千条堆栈淹没，让"保持关闭"看起来像"疯狂报错"。
            if (_hotkeyEntry == null) return;

            if (_capturingHotkey)
            {
                if (Input.anyKeyDown)
                {
                    // Clear the capture BEFORE doing anything that can throw. MelonPreferences
                    // .Save() is exactly what throws when the cfg file is locked or corrupt — and
                    // because `_capturingHotkey = false` sat AFTER it, the capture latched forever:
                    // OnUpdate returned early every frame, so the settings hotkey stopped responding
                    // entirely and the user could no longer close the window by keyboard.
                    // 在做任何可能抛异常的事情**之前**先清捕获态。MelonPreferences.Save() 恰是
                    // cfg 被锁/损坏时会抛的那一个——而 `_capturingHotkey = false` 原本在它**之后**，
                    // 于是捕获态永久卡住：OnUpdate 每帧提前返回，设置热键彻底失灵，用户再也无法用
                    // 键盘关窗。
                    _capturingHotkey = false;
                    KeyCode captured = ReadPressedKey();
                    if (captured != KeyCode.None)
                    {
                        try
                        {
                            _hotkeyEntry.Value = captured.ToString();
                            MelonPreferences.Save();
                        }
                        catch (System.Exception e)
                        {
                            LoggerInstance.Error($"[JipperKeyViewer] could not save the settings hotkey: {e.Message}");
                        }
                    }
                }
                return;
            }

            // Cache the parsed hotkey and only re-parse when the stored STRING actually changed.
            // This runs every frame for every MelonLoader user, and Enum.TryParse scans ~500
            // KeyCode names with OrdinalIgnoreCase comparisons — pure waste to rediscover an
            // unchanged value 60 times a second. / 缓存解析后的热键，仅在存储**字符串**真的变化时
            // 才重新解析。该方法对每个 MelonLoader 用户每帧都跑，而 Enum.TryParse 会对约 500 个
            // KeyCode 名字做 OrdinalIgnoreCase 比较——每秒 60 次重新发现一个没变的值，纯属浪费。
            string keyName = _hotkeyEntry.Value;
            if (string.IsNullOrEmpty(keyName)) return;
            if (!string.Equals(keyName, _hotkeyParsedFrom, System.StringComparison.Ordinal))
            {
                _hotkeyParsedFrom = keyName;
                _hotkeyKey = Enum.TryParse(keyName, true, out KeyCode parsed) ? parsed : KeyCode.None;
            }
            if (_hotkeyKey != KeyCode.None && Input.GetKeyDown(_hotkeyKey))
                ToggleSettings();
        }

        private string _hotkeyParsedFrom;
        private KeyCode _hotkeyKey = KeyCode.None;

        private static KeyCode ReadPressedKey()
        {
            // Enum.GetValues allocates a fresh ~500-element array on every capture; KeyViewer's
            // static AllKeyCodes is the same set, already built once, without the joystick axes.
            // Enum.GetValues 在每次捕获时都会分配一个约 500 元素的数组；KeyViewer 的静态
            // AllKeyCodes 是同一集合，只构建一次，且不含摇杆轴。
            KeyCode[] all = global::JipperKeyViewer.KeyViewer.KeyViewer.AllKeyCodes;
            if (all != null)
            {
                foreach (KeyCode k in all)
                {
                    if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6) continue;
                    if (Input.GetKeyDown(k)) return k;
                }
                return KeyCode.None;
            }
            foreach (KeyCode k in System.Enum.GetValues(typeof(KeyCode)))
            {
                if (k >= KeyCode.Mouse0 && k <= KeyCode.Mouse6) continue;
                if (Input.GetKeyDown(k)) return k;
            }
            return KeyCode.None;
        }

        internal void DrawHotkeySettings()
        {
            GUILayout.BeginVertical("box");
            GUILayout.BeginHorizontal();
            GUILayout.Label("Settings Hotkey / 设置界面热键", GUILayout.MinWidth(200));
            if (_capturingHotkey)
            {
                if (GUILayout.Button("Press any key... / 按任意键...", GUILayout.MinWidth(160)))
                    _capturingHotkey = false;
            }
            else
            {
                string cur = string.IsNullOrEmpty(_hotkeyEntry.Value) ? "None" : _hotkeyEntry.Value;
                if (GUILayout.Button(cur, GUILayout.MinWidth(160)))
                    _capturingHotkey = true;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        public override void OnGUI()
        {
            if (!_settingsWindowVisible) return;

            // Clamp window to current screen resolution / 根据当前分辨率更新窗口
            _settingsRect.width = Mathf.Min(_settingsRect.width, Screen.width);
            _settingsRect.height = Mathf.Min(_settingsRect.height, Screen.height);
            _settingsRect.x = Mathf.Clamp(_settingsRect.x, 0, Screen.width - _settingsRect.width);
            _settingsRect.y = Mathf.Clamp(_settingsRect.y, 0, Screen.height - _settingsRect.height);

            _settingsRect = GUILayout.Window(999, _settingsRect, DrawSettingsWindow,
                "Jipper Key Viewer - Settings");
        }

        private void DrawSettingsWindow(int id)
        {
            _settingsScroll = GUILayout.BeginScrollView(_settingsScroll);
            var kv = KeyViewer.KeyViewer.instance;
            if (kv != null) kv.DrawSettingsWindow();
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0, 0, 10000, 20));
        }

        public override void OnApplicationQuit()
        {
            var kv = KeyViewer.KeyViewer.instance;
            if (kv != null) kv.SaveSettings();
        }

        private void ToggleSettings()
        {
            _settingsWindowVisible = !_settingsWindowVisible;
            if (_settingsWindowVisible)
                _settingsRect = new Rect(
                    Screen.width * 0.05f, Screen.height * 0.05f,
                    Screen.width * 0.9f,  Screen.height * 0.85f);
            else
            {
                // UMM saves on window close via OnSaveGUI/OnHideGUI; Melon's equivalents are no-op
                // events, so persist directly on hide — several sliders rely on close-time saving.
                // UMM 关窗经 OnSaveGUI/OnHideGUI 保存；Melon 对应事件为空实现，隐藏窗口时直接
                // 落盘——多处滑块依赖关窗时保存。
                var kv = KeyViewer.KeyViewer.instance;
                if (kv != null) kv.SaveSettings();
            }
        }
    }

    class MelonHandler : IModLoader
    {
        readonly string _modPath;
        readonly JipperMelonMod _mod;

        public string ModPath => _modPath;

        public bool IsSettingsWindowVisible => _mod.IsSettingsWindowVisible;

        public KeyCode SettingsHotkey => _mod.CurrentHotkey;

        public event Action<float> OnUpdate { add { } remove { } }
        public event Action<bool> OnToggle { add { } remove { } }
        public event Action OnGUI { add { } remove { } }
        public event Action OnSaveGUI { add { } remove { } }

        public void DrawExtraSettings() => _mod.DrawHotkeySettings();

        public MelonHandler(JipperMelonMod mod)
        {
            _mod = mod;
            string loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
            _modPath = Path.GetDirectoryName(loc) ?? ".";
        }

        public void Log(string msg)     => MelonLogger.Msg(msg);
        public void Warning(string msg) => MelonLogger.Warning(msg);
        public void Error(string msg)   => MelonLogger.Error(msg);
    }
}
