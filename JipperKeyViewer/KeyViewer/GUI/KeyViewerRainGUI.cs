// Settings GUI: Rain tab content / 设置界面:雨线 标签页内容
// The four previous copy-paste row sections (rain shadow/outline, ghost rain shadow/outline) are unified into
// a single row-driven drawer. / 原先复制粘贴的四段行效果(普通雨影/雨描边、鬼键雨影/鬼键描边)统一为一个按行驱动的绘制方法。

using UnityEngine;

using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer : MonoBehaviour
    {
        // ===== Per-row rain shadow/outline foldout state ===== / 各行的雨影/描边折叠状态
        bool[] rainShadowColorExpanded = new bool[3];
        bool[] rainOutlineColorExpanded = new bool[3];
        bool[] ghostShadowColorExpanded = new bool[3];
        bool[] ghostOutlineColorExpanded = new bool[3];

        /// <summary>
        /// Resolve (ghost, outline, row) to the row's enable / color / two floats. / 解析 (鬼键?,描边?,行) 为该行的启用 / 颜色 / 两个数值。
        /// Shadow rows use v1/v2 = offset X / offset Y; outline rows use only v1 = width. / 阴影行 v1/v2 = 偏移 X / 偏移 Y;描边行仅用 v1 = 宽度。
        /// </summary>
        private static void GetRowEffect(bool ghost, bool isOutline, int r,
            out bool enable, out Color color, out float v1, out float v2)
        {
            var d = Settings.Data;
            if (ghost)
            {
                if (isOutline)
                {
                    if (r == 0) { enable = d.EnableGhostRainOutlineRow1; color = d.GhostRainOutlineColorRow1; v1 = d.GhostRainOutlineWidthRow1; v2 = 0f; return; }
                    if (r == 1) { enable = d.EnableGhostRainOutlineRow2; color = d.GhostRainOutlineColorRow2; v1 = d.GhostRainOutlineWidthRow2; v2 = 0f; return; }
                    enable = d.EnableGhostRainOutlineRow3; color = d.GhostRainOutlineColorRow3; v1 = d.GhostRainOutlineWidthRow3; v2 = 0f; return;
                }
                if (r == 0) { enable = d.EnableGhostRainShadowRow1; color = d.GhostRainShadowColorRow1; v1 = d.GhostRainShadowOffsetXRow1; v2 = d.GhostRainShadowOffsetYRow1; return; }
                if (r == 1) { enable = d.EnableGhostRainShadowRow2; color = d.GhostRainShadowColorRow2; v1 = d.GhostRainShadowOffsetXRow2; v2 = d.GhostRainShadowOffsetYRow2; return; }
                enable = d.EnableGhostRainShadowRow3; color = d.GhostRainShadowColorRow3; v1 = d.GhostRainShadowOffsetXRow3; v2 = d.GhostRainShadowOffsetYRow3; return;
            }
            if (isOutline)
            {
                if (r == 0) { enable = d.EnableRainOutlineRow1; color = d.RainOutlineColorRow1; v1 = d.RainOutlineWidthRow1; v2 = 0f; return; }
                if (r == 1) { enable = d.EnableRainOutlineRow2; color = d.RainOutlineColorRow2; v1 = d.RainOutlineWidthRow2; v2 = 0f; return; }
                enable = d.EnableRainOutlineRow3; color = d.RainOutlineColorRow3; v1 = d.RainOutlineWidthRow3; v2 = 0f; return;
            }
            if (r == 0) { enable = d.EnableRainShadowRow1; color = d.RainShadowColorRow1; v1 = d.RainShadowOffsetXRow1; v2 = d.RainShadowOffsetYRow1; return; }
            if (r == 1) { enable = d.EnableRainShadowRow2; color = d.RainShadowColorRow2; v1 = d.RainShadowOffsetXRow2; v2 = d.RainShadowOffsetYRow2; return; }
            enable = d.EnableRainShadowRow3; color = d.RainShadowColorRow3; v1 = d.RainShadowOffsetXRow3; v2 = d.RainShadowOffsetYRow3;
        }

        /// <summary>
        /// Write one changed component of a row back to its fields; null means "not changed". / 将单个变更写回该行字段;null 表示该分量未变更。
        /// </summary>
        private static void SetRowEffect(bool ghost, bool isOutline, int r,
            bool? enable, Color? color, float? v1, float? v2)
        {
            var d = Settings.Data;
            switch (ghost, isOutline, r)
            {
                case (true, true, 0):
                    if (enable.HasValue) d.EnableGhostRainOutlineRow1 = enable.Value;
                    if (color.HasValue) d.GhostRainOutlineColorRow1 = color.Value;
                    if (v1.HasValue) d.GhostRainOutlineWidthRow1 = v1.Value;
                    return;
                case (true, true, 1):
                    if (enable.HasValue) d.EnableGhostRainOutlineRow2 = enable.Value;
                    if (color.HasValue) d.GhostRainOutlineColorRow2 = color.Value;
                    if (v1.HasValue) d.GhostRainOutlineWidthRow2 = v1.Value;
                    return;
                case (true, true, 2):
                    if (enable.HasValue) d.EnableGhostRainOutlineRow3 = enable.Value;
                    if (color.HasValue) d.GhostRainOutlineColorRow3 = color.Value;
                    if (v1.HasValue) d.GhostRainOutlineWidthRow3 = v1.Value;
                    return;
                case (true, false, 0):
                    if (enable.HasValue) d.EnableGhostRainShadowRow1 = enable.Value;
                    if (color.HasValue) d.GhostRainShadowColorRow1 = color.Value;
                    if (v1.HasValue) d.GhostRainShadowOffsetXRow1 = v1.Value;
                    if (v2.HasValue) d.GhostRainShadowOffsetYRow1 = v2.Value;
                    return;
                case (true, false, 1):
                    if (enable.HasValue) d.EnableGhostRainShadowRow2 = enable.Value;
                    if (color.HasValue) d.GhostRainShadowColorRow2 = color.Value;
                    if (v1.HasValue) d.GhostRainShadowOffsetXRow2 = v1.Value;
                    if (v2.HasValue) d.GhostRainShadowOffsetYRow2 = v2.Value;
                    return;
                case (true, false, 2):
                    if (enable.HasValue) d.EnableGhostRainShadowRow3 = enable.Value;
                    if (color.HasValue) d.GhostRainShadowColorRow3 = color.Value;
                    if (v1.HasValue) d.GhostRainShadowOffsetXRow3 = v1.Value;
                    if (v2.HasValue) d.GhostRainShadowOffsetYRow3 = v2.Value;
                    return;
                case (false, true, 0):
                    if (enable.HasValue) d.EnableRainOutlineRow1 = enable.Value;
                    if (color.HasValue) d.RainOutlineColorRow1 = color.Value;
                    if (v1.HasValue) d.RainOutlineWidthRow1 = v1.Value;
                    return;
                case (false, true, 1):
                    if (enable.HasValue) d.EnableRainOutlineRow2 = enable.Value;
                    if (color.HasValue) d.RainOutlineColorRow2 = color.Value;
                    if (v1.HasValue) d.RainOutlineWidthRow2 = v1.Value;
                    return;
                case (false, true, 2):
                    if (enable.HasValue) d.EnableRainOutlineRow3 = enable.Value;
                    if (color.HasValue) d.RainOutlineColorRow3 = color.Value;
                    if (v1.HasValue) d.RainOutlineWidthRow3 = v1.Value;
                    return;
                case (false, false, 0):
                    if (enable.HasValue) d.EnableRainShadowRow1 = enable.Value;
                    if (color.HasValue) d.RainShadowColorRow1 = color.Value;
                    if (v1.HasValue) d.RainShadowOffsetXRow1 = v1.Value;
                    if (v2.HasValue) d.RainShadowOffsetYRow1 = v2.Value;
                    return;
                case (false, false, 1):
                    if (enable.HasValue) d.EnableRainShadowRow2 = enable.Value;
                    if (color.HasValue) d.RainShadowColorRow2 = color.Value;
                    if (v1.HasValue) d.RainShadowOffsetXRow2 = v1.Value;
                    if (v2.HasValue) d.RainShadowOffsetYRow2 = v2.Value;
                    return;
                case (false, false, 2):
                    if (enable.HasValue) d.EnableRainShadowRow3 = enable.Value;
                    if (color.HasValue) d.RainShadowColorRow3 = color.Value;
                    if (v1.HasValue) d.RainShadowOffsetXRow3 = v1.Value;
                    if (v2.HasValue) d.RainShadowOffsetYRow3 = v2.Value;
                    return;
            }
        }

        /// <summary>
        /// Draw one row-based section among rain shadow / rain outline / ghost shadow / ghost outline. / 绘制四类行效果段之一(普通阴影/普通描边/鬼键阴影/鬼键描边)。
        /// Replaces the four former copy-paste drawers; draw order and SaveSettings/ClearActiveDrops timing match the originals. / 替代原四个复制粘贴段;绘制顺序与保存/清雨滴时机与原实现一致。
        /// </summary>
        private void DrawRainRowEffectSection(bool ghost, bool isOutline)
        {
            string title = (ghost ? I18n.Tr("ghost_rain") + " " : "") + I18n.Tr(isOutline ? "rain_outline" : "rain_shadow");
            GUILayout.Label(title);
            for (int r = 0; r < 3; r++)
            {
                if (r == 2 && !HasThirdRow) break;
                string rowLabel = r == 0 ? I18n.Tr("rain_row1") : r == 1 ? I18n.Tr("rain_row2") : I18n.Tr("rain_row3");

                GetRowEffect(ghost, isOutline, r, out bool en, out Color curCol, out float v1, out float v2);

                bool newEn = GUILayout.Toggle(en, rowLabel);
                if (newEn != en)
                {
                    SetRowEffect(ghost, isOutline, r, newEn, null, null, null);
                    if (rainSystem != null && Keys != null) rainSystem.ClearActiveDrops(Keys);
                    SaveSettingsFromGui();
                }
                // Reflect the just-applied toggle immediately (the originals deferred to the next frame by
                // testing the pre-toggle value). / 开关即时生效(原实现检查切换前的值,下一帧才跟随),此处直接采用新值。
                if (!newEn) continue;

                GUILayout.BeginVertical("box");

                bool expanded;
                if (isOutline)
                {
                    expanded = ghost ? ghostOutlineColorExpanded[r] : rainOutlineColorExpanded[r];
                    expanded = DrawFoldoutButton(I18n.Tr("rain_outline_color"), expanded);
                    if (ghost) ghostOutlineColorExpanded[r] = expanded; else rainOutlineColorExpanded[r] = expanded;
                }
                else
                {
                    expanded = ghost ? ghostShadowColorExpanded[r] : rainShadowColorExpanded[r];
                    expanded = DrawFoldoutButton(I18n.Tr("rain_shadow_color"), expanded);
                    if (ghost) ghostShadowColorExpanded[r] = expanded; else rainShadowColorExpanded[r] = expanded;
                }

                if (expanded)
                {
                    Color newCol = DrawColorPicker("", curCol, isOutline ? RainOutlineColorDefault : RainShadowColorDefault);
                    if (newCol != curCol)
                    {
                        SetRowEffect(ghost, isOutline, r, null, newCol, null, null);
                        SaveSettingsFromGui();
                    }
                }

                if (isOutline)
                {
                    float curW = v1;
                    float newW = FloatSliderField(I18n.Tr("rain_outline_width"), curW, 0.5f, 10f, "F1");
                    if (newW != curW)
                    {
                        SetRowEffect(ghost, isOutline, r, null, null, newW, null);
                        SaveSettingsFromGui();
                    }
                }
                else
                {
                    float curX = v1;
                    float newX = FloatSliderField("X " + I18n.Tr("rain_shadow_offset"), curX, -10f, 10f);
                    if (newX != curX)
                    {
                        SetRowEffect(ghost, isOutline, r, null, null, newX, null);
                        SaveSettingsFromGui();
                    }
                    float curY = v2;
                    float newY = FloatSliderField("Y " + I18n.Tr("rain_shadow_offset"), curY, -10f, 10f);
                    if (newY != curY)
                    {
                        SetRowEffect(ghost, isOutline, r, null, null, null, newY);
                        SaveSettingsFromGui();
                    }
                }

                GUILayout.EndVertical();
            }
        }

        private void DrawRainSection()
        {
            RainExpanded = DrawFoldoutButton(I18n.Tr("rain_effect"), RainExpanded);
            if (!RainExpanded) return;

            bool newRainEffect = GUILayout.Toggle(Settings.Data.EnableRainEffect, I18n.Tr("rain_effect"));
            if (newRainEffect != Settings.Data.EnableRainEffect)
            {
                Settings.Data.EnableRainEffect = newRainEffect;
                if (!Settings.Data.EnableRainEffect && rainSystem != null)
                    rainSystem.ClearActiveDrops(Keys);
                SaveSettingsFromGui();
            }

            if (!Settings.Data.EnableRainEffect) return;

            GUILayout.Label(I18n.Tr("rain_rows") + ":");
            GUILayout.BeginHorizontal();
            // Turning a row off also clears that row's in-flight drops, so the toggle takes effect
            // immediately / 关闭某排时同时清掉该排飞行中的雨滴，开关立即生效
            bool newRow1 = GUILayout.Toggle(Settings.Data.EnableRainForRow1, I18n.Tr("rain_row1"));
            if (newRow1 != Settings.Data.EnableRainForRow1)
            {
                Settings.Data.EnableRainForRow1 = newRow1;
                if (!newRow1 && rainSystem != null) rainSystem.ClearRowDrops(Keys, 0);
                SaveSettingsFromGui();
            }
            bool newRow2 = GUILayout.Toggle(Settings.Data.EnableRainForRow2, I18n.Tr("rain_row2"));
            if (newRow2 != Settings.Data.EnableRainForRow2)
            {
                Settings.Data.EnableRainForRow2 = newRow2;
                if (!newRow2 && rainSystem != null) rainSystem.ClearRowDrops(Keys, 1);
                SaveSettingsFromGui();
            }
            if (HasThirdRow)
            {
                bool newRow3 = GUILayout.Toggle(Settings.Data.EnableRainForRow3, I18n.Tr("rain_row3"));
                if (newRow3 != Settings.Data.EnableRainForRow3)
                {
                    Settings.Data.EnableRainForRow3 = newRow3;
                    if (!newRow3 && rainSystem != null) rainSystem.ClearRowDrops(Keys, 2);
                    SaveSettingsFromGui();
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.Label(I18n.Tr("rain_height") + ":");
            // Height/speed/width used to be assigned without saving — the values only persisted
            // when some OTHER change triggered a save, so a game crash right after adjusting them
            // silently reverted all three groups. Now they save through the debounced GUI path
            // like every other slider on this page.
            // 高度/速度/宽度此前直赋值不保存——只有其它变更触发保存时才捎带落盘,游戏刚调完
            // 就崩溃会静默回退这三组值。现在与页面其它滑块一样走去抖保存。
            float newH1 = FloatSliderField(I18n.Tr("rain_row1"), Settings.Data.RainHeightRow1, 1f, 2000f);
            if (!Mathf.Approximately(newH1, Settings.Data.RainHeightRow1)) { Settings.Data.RainHeightRow1 = newH1; SaveSettingsFromGui(); }
            float newH2 = FloatSliderField(I18n.Tr("rain_row2"), Settings.Data.RainHeightRow2, 1f, 2000f);
            if (!Mathf.Approximately(newH2, Settings.Data.RainHeightRow2)) { Settings.Data.RainHeightRow2 = newH2; SaveSettingsFromGui(); }
            if (HasThirdRow)
            {
                float newH3 = FloatSliderField(I18n.Tr("rain_row3"), Settings.Data.RainHeightRow3, 1f, 2000f);
                if (!Mathf.Approximately(newH3, Settings.Data.RainHeightRow3)) { Settings.Data.RainHeightRow3 = newH3; SaveSettingsFromGui(); }
            }

            GUILayout.Label(I18n.Tr("rain_speed") + ":");
            float newS1 = FloatSliderField(I18n.Tr("rain_row1"), Settings.Data.RainSpeedRow1, 50f, 2000f, "F0");
            if (!Mathf.Approximately(newS1, Settings.Data.RainSpeedRow1)) { Settings.Data.RainSpeedRow1 = newS1; SaveSettingsFromGui(); }
            float newS2 = FloatSliderField(I18n.Tr("rain_row2"), Settings.Data.RainSpeedRow2, 50f, 2000f, "F0");
            if (!Mathf.Approximately(newS2, Settings.Data.RainSpeedRow2)) { Settings.Data.RainSpeedRow2 = newS2; SaveSettingsFromGui(); }
            if (HasThirdRow)
            {
                float newS3 = FloatSliderField(I18n.Tr("rain_row3"), Settings.Data.RainSpeedRow3, 50f, 2000f, "F0");
                if (!Mathf.Approximately(newS3, Settings.Data.RainSpeedRow3)) { Settings.Data.RainSpeedRow3 = newS3; SaveSettingsFromGui(); }
            }

            GUILayout.Label(I18n.Tr("rain_width") + ":");
            float newW1 = FloatSliderField(I18n.Tr("rain_width_row1"), Settings.Data.RainWidthRow1, 10f, 200f, "F0");
            if (!Mathf.Approximately(newW1, Settings.Data.RainWidthRow1)) { Settings.Data.RainWidthRow1 = newW1; SaveSettingsFromGui(); }
            float newW2 = FloatSliderField(I18n.Tr("rain_width_row2"), Settings.Data.RainWidthRow2, 10f, 200f, "F0");
            if (!Mathf.Approximately(newW2, Settings.Data.RainWidthRow2)) { Settings.Data.RainWidthRow2 = newW2; SaveSettingsFromGui(); }
            if (HasThirdRow)
            {
                float newW3 = FloatSliderField(I18n.Tr("rain_width_row3"), Settings.Data.RainWidthRow3, 10f, 200f, "F0");
                if (!Mathf.Approximately(newW3, Settings.Data.RainWidthRow3)) { Settings.Data.RainWidthRow3 = newW3; SaveSettingsFromGui(); }
            }

            bool newRoundedOutline = GUILayout.Toggle(Settings.Data.EnableRainRoundedOutline, I18n.Tr("rain_rounded_outline"));
            if (newRoundedOutline != Settings.Data.EnableRainRoundedOutline)
            {
                Settings.Data.EnableRainRoundedOutline = newRoundedOutline;
                if (rainSystem != null && Keys != null) rainSystem.ClearActiveDrops(Keys);
                SaveSettingsFromGui();
            }
            if (Settings.Data.EnableRainRoundedOutline)
            {
                float newCornerRadius = FloatSliderField(I18n.Tr("rain_rounded_outline_radius"), Settings.Data.RainOutlineCornerRadius, 0f, 20f, "F0");
                if (!Mathf.Approximately(newCornerRadius, Settings.Data.RainOutlineCornerRadius))
                {
                    Settings.Data.RainOutlineCornerRadius = newCornerRadius;
                    if (rainSystem != null && Keys != null) rainSystem.ClearActiveDrops(Keys);
                    SaveSettingsFromGui();
                }
            }
            GUILayout.Label(I18n.Tr("rain_outline_sides") + ":");
            string[] outlineSides = { I18n.Tr("rain_side_all"), I18n.Tr("rain_side_vertical"), I18n.Tr("rain_side_horizontal") };
            int newSides = GUILayout.SelectionGrid(Mathf.Clamp(Settings.Data.RainOutlineSides, 0, 2), outlineSides, 3);
            if (newSides != Settings.Data.RainOutlineSides)
            {
                Settings.Data.RainOutlineSides = newSides;
                if (rainSystem != null && Keys != null) rainSystem.ClearActiveDrops(Keys);
                SaveSettingsFromGui();
            }

            bool newDottedRain = GUILayout.Toggle(Settings.Data.EnableRainDotted, I18n.Tr("rain_dotted"));
            if (newDottedRain != Settings.Data.EnableRainDotted)
            {
                Settings.Data.EnableRainDotted = newDottedRain;
                if (rainSystem != null && Keys != null) rainSystem.ClearActiveDrops(Keys);
                SaveSettingsFromGui();
            }
            if (Settings.Data.EnableRainDotted)
            {
                float newDotLength = FloatSliderField(I18n.Tr("rain_dot_length"), Settings.Data.RainDotLength, 1f, 100f, "F0");
                if (!Mathf.Approximately(newDotLength, Settings.Data.RainDotLength))
                {
                    Settings.Data.RainDotLength = newDotLength;
                    if (rainSystem != null && Keys != null) rainSystem.ClearActiveDrops(Keys);
                    SaveSettingsFromGui();
                }
                float newGapLength = FloatSliderField(I18n.Tr("rain_gap_length"), Settings.Data.RainGapLength, 0f, 100f, "F0");
                if (!Mathf.Approximately(newGapLength, Settings.Data.RainGapLength))
                {
                    Settings.Data.RainGapLength = newGapLength;
                    if (rainSystem != null && Keys != null) rainSystem.ClearActiveDrops(Keys);
                    SaveSettingsFromGui();
                }
            }

            GUILayout.Label(I18n.Tr("rain_start_y") + ":");
            // Start-Y is read live at render time — sliders apply to existing drops directly. It
            // still persists through the debounced save like every other slider on this page: the
            // old save-on-next-write behaviour meant a crash right after adjusting reverted the
            // values, and the ghost block below already saved, so the two halves disagreed.
            // 起始 Y 渲染时实时读取——滑块直接作用于现有雨滴。落盘与页面其它滑块一致走去抖保存：
            // 旧的“随下次写入捎带”行为意味着刚调完就崩溃会回退，且下方鬼雨块已经保存，两半行为不一致。
            float newStartY1 = FloatSliderField(I18n.Tr("rain_row1"), Settings.Data.RainStartYRow1, -2000f, 1000f, "F0");
            if (!Mathf.Approximately(newStartY1, Settings.Data.RainStartYRow1)) { Settings.Data.RainStartYRow1 = newStartY1; SaveSettingsFromGui(); }
            float newStartY2 = FloatSliderField(I18n.Tr("rain_row2"), Settings.Data.RainStartYRow2, -2000f, 1000f, "F0");
            if (!Mathf.Approximately(newStartY2, Settings.Data.RainStartYRow2)) { Settings.Data.RainStartYRow2 = newStartY2; SaveSettingsFromGui(); }
            if (HasThirdRow)
            {
                float newStartY3 = FloatSliderField(I18n.Tr("rain_row3"), Settings.Data.RainStartYRow3, -2000f, 1000f, "F0");
                if (!Mathf.Approximately(newStartY3, Settings.Data.RainStartYRow3)) { Settings.Data.RainStartYRow3 = newStartY3; SaveSettingsFromGui(); }
            }

            GUILayout.Space(5);
            bool newRainFade = GUILayout.Toggle(Settings.Data.EnableRainFade, I18n.Tr("rain_fade"));
            if (newRainFade != Settings.Data.EnableRainFade)
            {
                Settings.Data.EnableRainFade = newRainFade;
                if (!newRainFade && rainSystem != null && Keys != null)
                    rainSystem.ClearActiveDrops(Keys);
                SaveSettingsFromGui();
            }
            if (Settings.Data.EnableRainFade)
            {
                float newFadeDur = FloatSliderField(I18n.Tr("fade_duration"), Settings.Data.RainFadeDuration, 0.03f, 5.0f);
                if (newFadeDur != Settings.Data.RainFadeDuration)
                {
                    Settings.Data.RainFadeDuration = newFadeDur;
                    SaveSettingsFromGui();
                }
            }

            GUILayout.Space(5);
            bool newGradient = GUILayout.Toggle(Settings.Data.EnableRainGradient, I18n.Tr("rain_gradient"));
            if (newGradient != Settings.Data.EnableRainGradient)
            {
                Settings.Data.EnableRainGradient = newGradient;
                SaveSettingsFromGui();
            }
            if (Settings.Data.EnableRainGradient)
            {
                float newFadePx = FloatSliderField(I18n.Tr("gradient_percent"), Settings.Data.RainFadePx, 1f, 200f, "F0");
                if (!Mathf.Approximately(newFadePx, Settings.Data.RainFadePx))
                {
                    Settings.Data.RainFadePx = newFadePx;
                    SaveSettingsFromGui();
                }
            }

            GUILayout.Space(5);
            bool newGhostRain = GUILayout.Toggle(Settings.Data.EnableGhostRain, I18n.Tr("ghost_rain"));
            if (newGhostRain != Settings.Data.EnableGhostRain)
            {
                Settings.Data.EnableGhostRain = newGhostRain;
                if (!newGhostRain && rainSystem != null && Keys != null)
                    rainSystem.ClearActiveDrops(Keys);
                SaveSettingsFromGui();
            }
            if (Settings.Data.EnableGhostRain)
            {
                // Ghost row shadow / outline sections / 鬼键行 阴影 / 描边 段
                DrawRainRowEffectSection(true, false);
                DrawRainRowEffectSection(true, true);

                GUILayout.Label(I18n.Tr("ghost_rain") + " " + I18n.Tr("rain_start_y") + ":");
                // Ghost start-Y offsets are read live at render time / 鬼雨起始 Y 渲染时实时读取
                float newGY1 = FloatSliderField(I18n.Tr("rain_row1"), Settings.Data.GhostRainStartYRow1, -2000f, 1000f, "F0");
                if (!Mathf.Approximately(newGY1, Settings.Data.GhostRainStartYRow1)) { Settings.Data.GhostRainStartYRow1 = newGY1; SaveSettingsFromGui(); }
                float newGY2 = FloatSliderField(I18n.Tr("rain_row2"), Settings.Data.GhostRainStartYRow2, -2000f, 1000f, "F0");
                if (!Mathf.Approximately(newGY2, Settings.Data.GhostRainStartYRow2)) { Settings.Data.GhostRainStartYRow2 = newGY2; SaveSettingsFromGui(); }
                if (HasThirdRow)
                {
                    float newGY3 = FloatSliderField(I18n.Tr("rain_row3"), Settings.Data.GhostRainStartYRow3, -2000f, 1000f, "F0");
                    if (!Mathf.Approximately(newGY3, Settings.Data.GhostRainStartYRow3)) { Settings.Data.GhostRainStartYRow3 = newGY3; SaveSettingsFromGui(); }
                }

                GUILayout.Label(I18n.Tr("ghost_rain_height") + ":");
                float newGH1 = FloatSliderField(I18n.Tr("rain_row1"), Settings.Data.GhostRainHeightRow1, 1f, 2000f);
                if (!Mathf.Approximately(newGH1, Settings.Data.GhostRainHeightRow1)) { Settings.Data.GhostRainHeightRow1 = newGH1; SaveSettingsFromGui(); }
                float newGH2 = FloatSliderField(I18n.Tr("rain_row2"), Settings.Data.GhostRainHeightRow2, 1f, 2000f);
                if (!Mathf.Approximately(newGH2, Settings.Data.GhostRainHeightRow2)) { Settings.Data.GhostRainHeightRow2 = newGH2; SaveSettingsFromGui(); }
                if (HasThirdRow)
                {
                    float newGH3 = FloatSliderField(I18n.Tr("rain_row3"), Settings.Data.GhostRainHeightRow3, 1f, 2000f);
                    if (!Mathf.Approximately(newGH3, Settings.Data.GhostRainHeightRow3)) { Settings.Data.GhostRainHeightRow3 = newGH3; SaveSettingsFromGui(); }
                }

                GUILayout.Label(I18n.Tr("ghost_rain_speed") + ":");
                float newGS1 = FloatSliderField(I18n.Tr("rain_row1"), Settings.Data.GhostRainSpeedRow1, 50f, 2000f, "F0");
                if (!Mathf.Approximately(newGS1, Settings.Data.GhostRainSpeedRow1)) { Settings.Data.GhostRainSpeedRow1 = newGS1; SaveSettingsFromGui(); }
                float newGS2 = FloatSliderField(I18n.Tr("rain_row2"), Settings.Data.GhostRainSpeedRow2, 50f, 2000f, "F0");
                if (!Mathf.Approximately(newGS2, Settings.Data.GhostRainSpeedRow2)) { Settings.Data.GhostRainSpeedRow2 = newGS2; SaveSettingsFromGui(); }
                if (HasThirdRow)
                {
                    float newGS3 = FloatSliderField(I18n.Tr("rain_row3"), Settings.Data.GhostRainSpeedRow3, 50f, 2000f, "F0");
                    if (!Mathf.Approximately(newGS3, Settings.Data.GhostRainSpeedRow3)) { Settings.Data.GhostRainSpeedRow3 = newGS3; SaveSettingsFromGui(); }
                }

                GUILayout.Label(I18n.Tr("ghost_rain_width") + ":");
                // Width labels used the row names ("Row 1/2/3") — a copy-paste from the start-Y
                // block; they now use the width labels like the normal-rain block above.
                // 宽度标签此前误用排名("第 N 排")——从起始 Y 区块复制而来;现与上方普通雨
                // 宽度块一致使用宽度文案。
                float newGW1 = FloatSliderField(I18n.Tr("rain_width_row1"), Settings.Data.GhostRainWidthRow1, 10f, 200f, "F0");
                if (!Mathf.Approximately(newGW1, Settings.Data.GhostRainWidthRow1)) { Settings.Data.GhostRainWidthRow1 = newGW1; SaveSettingsFromGui(); }
                float newGW2 = FloatSliderField(I18n.Tr("rain_width_row2"), Settings.Data.GhostRainWidthRow2, 10f, 200f, "F0");
                if (!Mathf.Approximately(newGW2, Settings.Data.GhostRainWidthRow2)) { Settings.Data.GhostRainWidthRow2 = newGW2; SaveSettingsFromGui(); }
                if (HasThirdRow)
                {
                    float newGW3 = FloatSliderField(I18n.Tr("rain_width_row3"), Settings.Data.GhostRainWidthRow3, 10f, 200f, "F0");
                    if (!Mathf.Approximately(newGW3, Settings.Data.GhostRainWidthRow3)) { Settings.Data.GhostRainWidthRow3 = newGW3; SaveSettingsFromGui(); }
                }
            }

            GUILayout.Space(8);
            // Normal rain row shadow / outline sections / 普通雨线行 阴影 / 描边 段
            DrawRainRowEffectSection(false, false);
            DrawRainRowEffectSection(false, true);

            GUILayout.Space(5);
        }
    }
}