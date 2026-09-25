// Settings GUI window drawn inside UnityModManager / MelonLoader / 在 UnityModManager / MelonLoader 内绘制的设置 GUI 窗口
// Main window shell: header bar, category tabs, and per-tab dispatch into section drawers / 主窗口外壳:常驻顶部栏、分类标签栏、各标签内容分派
// Section rendering lives in KeyViewerSettingsGUI / BindingGUI / RainGUI / ColorGUI partial files.
// 各区块绘制逻辑位于 KeyViewerSettingsGUI / BindingGUI / RainGUI / ColorGUI 分部文件中。

using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Rain;
using JipperKeyViewer.KeyViewer.Rendering;
using JipperKeyViewer.KeyViewer.Util;
using JipperKeyViewer.KeyViewer.Editor;

namespace JipperKeyViewer.KeyViewer
{
    /// <summary>
    /// Settings window rendered via loader OnGUI / 通过加载器 OnGUI 渲染的设置窗口
    /// Uses IMGUI (GUILayout) for immediate-mode UI / 使用 IMGUI (GUILayout) 即时模式 UI
    /// Scrolling is owned by the loader (UMM panel / Melon window), so no inner scrollview here / 滚动由加载器持有(UMM 面板 / Melon 窗口),这里不包内层滚动
    /// </summary>
    public partial class KeyViewer : MonoBehaviour
    {
        /// <summary>Number of settings tabs / 设置标签页数量</summary>
        const int TabCount = 6;
        /// <summary>Currently selected settings tab index / 当前选中的设置标签索引</summary>
        int settingsGuiTab;

        /// <summary>
        /// Draw the settings window shell: header bar, tab bar, then the active tab's sections / 绘制设置窗口外壳:常驻栏、标签栏,然后分发到当前标签页的区块
        /// </summary>
        public void DrawSettingsWindow()
        {
            // Mark the window alive for the rebind capture gate (ProcessKeySelection).
            // 为改键捕获的存活门控标记窗口存活(见 ProcessKeySelection)。
            lastSettingsGuiFrame = Time.frameCount;
            colorPickerFieldSeq = 0;
            sliderFieldSeq = 0;
            BeginTextInputPass();
            GUILayout.BeginVertical();
            DrawHeaderBar();
            DrawSaveFailureBanner();
            DrawTabBar();
            switch (settingsGuiTab)
            {
                case 1:
                    if (!IsCustomLayout) DrawCustomPositionSection(); // nodes are their own position / 节点即位置
                    DrawLayoutSection();
                    if (KeyViewer.IsFullKeyboard)
                        DrawFullKeyboardKpsTotalSection();
                    break;
                case 2:
                    DrawFontSection();
                    DrawDisplaySection();
                    break;
                case 3:
                    DrawRainSection();
                    break;
                case 4:
                    DrawBindingSection();
                    break;
                case 5:
                    DrawColorSection();
                    break;
                default:
                    DrawProfileSection();
                    DrawLanguageSection();
                    DrawCountResetSection();
                    DrawFolderButtons();
                    Loader.DrawExtraSettingsUI();
                    break;
            }
            GUILayout.EndVertical();
            EndTextInputPass();
        }

        /// <summary>Persistent red banner while the last settings write failed. Without it a full
        /// disk, a read-only profile folder or a file locked by a sync client silently discarded
        /// every change since the last successful save — the GUI looked completely normal. Stays up
        /// until a save succeeds. / 最近一次写盘失败时持续显示红色横幅。没有它，磁盘写满、目录
        /// 只读或被同步软件占用时，自上次成功保存以来的所有改动静默丢失，而界面看起来完全正常。
        /// 直到某次保存成功才消失。</summary>
        private static GUIStyle saveErrorStyle;

        private void DrawSaveFailureBanner()
        {
            string error = LastSaveError;
            if (string.IsNullOrEmpty(error)) return;
            if (saveErrorStyle == null)
                saveErrorStyle = new GUIStyle(GUI.skin.box) { normal = { textColor = new Color(1f, 0.45f, 0.45f) } };
            GUILayout.Label(I18n.Tr("save_failed") + " " + error, saveErrorStyle);
        }

        /// <summary>
        /// Always-visible top bar: master key-display toggle, reset-counts button, current profile / 常驻顶部栏:密钥显示总开关、重置计数按钮、当前配置名
        /// </summary>
        // Cached red-text button style — IMGUI redraws every event, so a per-call new GUIStyle was a
        // per-frame allocation. / 缓存的红字按钮样式——IMGUI 每事件重绘,逐调用 new GUIStyle 是每帧分配。
        private static GUIStyle redButtonStyle;

        private void DrawHeaderBar()
        {
            GUILayout.BeginVertical("box");
            GUILayout.BeginHorizontal();
            bool newEnabled = GUILayout.Toggle(Settings.Data.Enabled,
                (Settings.Data.Enabled ? "✓ " : "✗ ") + I18n.Tr("key_display_on"));
            if (newEnabled != Settings.Data.Enabled)
            {
                Settings.Data.Enabled = newEnabled;
                SaveSettingsFromGui();
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(I18n.Tr("profile") + ": " + Settings.CurrentProfile);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (redButtonStyle == null)
                redButtonStyle = new GUIStyle(GUI.skin.button) { normal = { textColor = Color.red } };
            if (GUILayout.Button(I18n.Tr("reset_counts"), redButtonStyle, GUILayout.MinWidth(120)))
                ExecuteCountReset();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.Space(5);
        }

        /// <summary>
        /// Category tab bar (3-column grid so it wraps cleanly on narrow UMM panels) / 分类标签栏(3 列网格,窄面板下自动换行)
        /// Active tab persists across sessions via Settings.UiTab / 当前标签跨会话记忆(通过 Settings.UiTab)
        /// </summary>
        /// <summary>Stable i18n keys per tab, in tab order / 每个标签页的固定 i18n 键（按标签顺序）
        /// </summary>
        private static readonly string[] TabKeys = { "general", "layout", "display", "rain", "keys", "colors" };

        /// <summary>Translated tab labels, rebuilt only when the UI language changes. The old code
        /// allocated a string[6] plus six "tab_" + key concatenations on EVERY IMGUI event, i.e.
        /// seven objects per event, every event, for the whole session — on the window that is
        /// always open. / 已翻译的标签文本，仅在界面语言变化时重建。旧代码在**每个** IMGUI 事件
        /// 都分配一个 string[6] 加六次「"tab_" + 键」拼接，即每事件 7 个对象，整个会话持续不断
        /// ——而这个窗口一直是开着的。</summary>
        private string[] cachedTabLabels;
        private string cachedTabLang;

        private void DrawTabBar()
        {
            if (cachedTabLabels == null || cachedTabLang != I18n.Lang)
            {
                cachedTabLang = I18n.Lang;
                cachedTabLabels = new string[TabCount];
                for (int i = 0; i < TabCount; i++)
                    cachedTabLabels[i] = I18n.Tr("tab_" + TabKeys[i]);
            }

            int newTab = GUILayout.SelectionGrid(settingsGuiTab, cachedTabLabels, 3);
            if (newTab != settingsGuiTab)
            {
                // Cancel an armed rebind when leaving the Keys tab — the capture gate alone
                // wouldn't stop typing in other tabs' text fields from being eaten as bindings.
                // 离开关键页时取消武装中的改键——仅靠存活门控挡不住在其它标签页文本框打字被吞成绑定。
                SelectedKey = -1;
                changeState = 0;
                settingsGuiTab = newTab;
                Settings.UiTab = newTab;
                SaveMetaOnly();
            }
            GUILayout.Space(5);
        }
    }
}