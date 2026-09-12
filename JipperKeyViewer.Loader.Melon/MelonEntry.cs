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
            _prefs = MelonPreferences.CreateCategory("JipperKeyViewer", "Jipper Key Viewer");
            _hotkeyEntry = _prefs.CreateEntry("Hotkey", "F1",
                "Settings Hotkey", "Key to open/close settings window");

            _handler = new MelonHandler(this);
            Main.Init(_handler);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (!_initialized)
            {
                _initialized = true;
                Main.EnableNow();
            }
        }

        public override void OnUpdate()
        {
            if (_capturingHotkey)
            {
                if (Input.anyKeyDown)
                {
                    KeyCode captured = ReadPressedKey();
                    if (captured != KeyCode.None)
                    {
                        _hotkeyEntry.Value = captured.ToString();
                        MelonPreferences.Save();
                    }
                    _capturingHotkey = false;
                }
                return;
            }

            string keyName = _hotkeyEntry.Value;
            if (string.IsNullOrEmpty(keyName)) return;
            if (Enum.TryParse(keyName, true, out KeyCode key) && Input.GetKeyDown(key))
                ToggleSettings();
        }

        private static KeyCode ReadPressedKey()
        {
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
