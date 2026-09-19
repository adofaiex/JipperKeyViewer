using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Rendering;

namespace JipperKeyViewer.KeyViewer
{
    /// <summary>
    /// Layout and positioning: creating, initializing, positioning key elements / 布局和定位：创建、初始化、定位按键元素
    /// </summary>
    public partial class KeyViewer : MonoBehaviour
    {
        /// <summary>
        /// Create the canvas overlay and initialize all keys / 创建画布覆盖层并初始化所有按键
        /// Called when the mod is toggled on or when settings require rebuilding / 在 Mod 打开或设置需要重建时调用
        /// </summary>
        private void EnableKeyViewer()
        {
            if (KeyViewerObject != null || !Settings.Data.Enabled) return;
            if (!TryLoadResources())
            {
                Loader.Error("KeyViewer: Cannot load AssetBundle, please check assets/ directory");
                return;
            }
            // Create ScreenSpaceOverlay canvas (independent of game UI) / 创建 ScreenSpaceOverlay 画布（独立于游戏 UI）
            KeyViewerObject = new GameObject("Jipper KeyViewer");
            Canvas = KeyViewerObject.AddComponent<Canvas>();
            Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            Canvas.sortingOrder = 2;
            CanvasScaler scaler = Canvas.gameObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 1f; // Match height so vertical positions are resolution-independent / 匹配高度使垂直位置不受分辨率影响
            // SizeObject applies the Size scale and serves as parent for all keys / SizeObject 应用大小缩放并作为所有按键的父级
            KeyViewerSizeObject = new GameObject("SizeObject");
            RectTransform rectTransform = KeyViewerSizeObject.AddComponent<RectTransform>();
            rectTransform.SetParent(KeyViewerObject.transform);
            rectTransform.localScale = new Vector3(Settings.Data.Size, Settings.Data.Size, 1);
            // Fill full canvas with bottom-left pivot so localScale doesn't shift child positions / 填满画布，左下角轴心，使缩放不改变子元素位置
            rectTransform.anchorMin = Vector2.zero;
            rectTransform.anchorMax = Vector2.one;
            rectTransform.pivot = Vector2.zero;
            rectTransform.offsetMin = rectTransform.offsetMax = Vector2.zero;
            // Merged shape layers: every key box (background + outline) renders into two meshes
            // instead of per-key Image hierarchies. Texts live on their own sub-canvas so text
            // updates never re-batch the shape meshes and vice versa. Key roots are pure data
            // holders (position anchor + rain state) created after all layers.
            // 合并形状层：所有按键框（背景+描边）画进两个 mesh，不再每键一组 Image。
            // 文本放独立子画布，文本更新不会重批形状 mesh，反之亦然。按键根为纯数据载体
            // （位置锚点 + 雨滴状态），在所有图层之后创建。
            keyShapeLayer = CreateFullStretchRect("KeyShapes").gameObject.AddComponent<KeyShapeLayer>();
            keyOutlineLayer = CreateFullStretchRect("KeyOutlines").gameObject.AddComponent<KeyShapeLayer>();
            keyShapeLayer.AttachOutlineLayer(keyOutlineLayer);
            keyShapeLayer.SetSprites(keyBackgroundSprite, keyOutlineSprite);
            GameObject textGo = CreateFullStretchRect("TextLayer").gameObject;
            Canvas textCanvas = textGo.AddComponent<Canvas>();
            textCanvas.additionalShaderChannels = Canvas.additionalShaderChannels; // match TMP input channels of the root canvas / 与根画布的 TMP 通道一致
            textLayer = textGo.transform;
            // Merged rain layers: all drops render into two meshes (solid-color quads / ghost sprite),
            // replacing the per-key RainLine canvases and pooled Rain GameObjects. Created after the
            // text layer so rain keeps rendering above texts.
            // 合并雨滴层：全部雨滴画进两个 mesh（纯色四边形/鬼雨贴图），取代每键 RainLine 画布与
            // Rain 对象池。创建于文本层之后，雨滴保持绘制在文本之上。
            rainLayer = CreateFullStretchRect("RainLayer").gameObject.AddComponent<RainLayer>();
            ghostRainLayer = CreateFullStretchRect("GhostRainLayer").gameObject.AddComponent<GhostRainLayer>();
            rainLayer.AttachGhostLayer(ghostRainLayer);
            rainLayer.SetSprite(ghostRainSprite);
            rainSystem.AttachLayers(rainLayer, ghostRainLayer);
            // Initialize main keys based on selected layout / 根据选中的布局初始化主按键
            Keys = new Key[GetKeyCount()];
            keyShapeLayer.Init(Keys.Length + CustomStatSlotCount()); // + stat panel slots (custom: one per panel / 面板槽位：自定义每面板一个)
            rainLayer.Init(Keys.Length); // per-key rain press scales / 每键雨滴按压缩放
            InitializeMainKeys(GetLayout(Settings.Data.KeyViewerStyle));
            // Initialize foot keys based on selected layout (full keyboard and the FreeMake custom
            // layout have none — a leftover foot style would draw fixed-position foot keys mixed
            // into the node canvas). / 根据选中的布局初始化脚键（全键盘与 FreeMake 自定义布局
            // 无脚键——残留的脚键样式会把固定位置的脚键混进节点画布）。
            if (!IsFullKeyboard && !IsCustomLayout)
            {
                int footSize = FootKeySize(Settings.Data.FootKeyViewerStyle);
                if (footSize > 0) InitializeFootKeyViewer(footSize);
            }
            // Apply streamer mode (hide KPS/Total) — only for normal layouts; the full keyboard has its
            // own dedicated "Show KPS / Total" toggle, so don't let streamer mode fight it.
            // 应用主播模式（隐藏 KPS/Total）——仅普通布局生效；全键盘有专属开关，不与之冲突。
            if (Settings.Data.StreamerMode && !IsFullKeyboard)
            {
                SetStatsVisible(false);
            }
            // Persist the overlay across scene loads / 使覆盖层在场景加载中持久化
            Object.DontDestroyOnLoad(KeyViewerObject);
            PressTimes = new Queue<long>(256);
            keyPressTimes = new Queue<long>[MaxKeySlots];
            for (int i = 0; i < MaxKeySlots; i++)
                keyPressTimes[i] = new Queue<long>(128);
            lastPerKeyKps = new int[MaxKeySlots];
            Stopwatch = System.Diagnostics.Stopwatch.StartNew();
            RefreshAllCountDisplay();
        }

        /// <summary>
        /// Destroy the canvas overlay and clean up all resources / 销毁画布覆盖层并清理所有资源
        /// </summary>
        private void DisableKeyViewer()
        {
            if (KeyViewerObject == null) return;
            // Explicitly clear active rain state before dropping the Keys reference — the active
            // set only stayed valid by the implicit "rain indices < 24 < any array length"
            // invariant; clearing makes correctness independent of it.
            // 显式清空活跃雨滴状态再丢弃 Keys 引用——活跃集此前仅靠"雨滴索引 <24 < 任意
            // 数组长度"的隐式不变量保持正确;清空让正确性不再依赖它。
            rainSystem.ClearActiveDrops(Keys);
            // Same texture-ownership reason as ResetKeyViewer — destroying KeyViewerObject does
            // not free any custom-image texture. / 与 ResetKeyViewer 同理的贴图归属问题——
            // 销毁 KeyViewerObject 不会释放任何自定义图片贴图。
            ReleaseCustomTextures();
            Object.Destroy(KeyViewerObject);
            KeyViewerObject = null;
            KeyViewerSizeObject = null;
            rainSystem.DetachLayers();
            // Video players live on their own DontDestroyOnLoad root, NOT under KeyViewerObject —
            // they must be torn down explicitly or the decoder keeps running with no visible quad.
            // / 视频播放器挂在独立的 DontDestroyOnLoad 根节点上，而非 KeyViewerObject 之下——
            // 必须显式拆解，否则解码器会继续运行而画面上没有四边形。
            Rendering.KvVideoTextureManager.ReleaseAll();
            // Destroy the cached text-style materials / 销毁缓存的文字样式材质
            ReleaseTextStyleMaterials();
            Canvas = null;
            keyShapeLayer = null;
            keyOutlineLayer = null;
            textLayer = null;
            rainLayer = null;
            ghostRainLayer = null;
            Keys = null;
            PressTimes = null;
            keyPressTimes = null;
            lastPerKeyKps = null;
            Stopwatch = null;
        }

        struct ExtraSlot { public int index; public float x, y, w; public int rainRow; public bool slim; }

        struct LayoutDesc { public float frontY; public float bottomY; public ExtraSlot[] extras; }

        private static LayoutDesc GetLayout(KeyviewerStyle style)
        {
            // Standardized layout: all back-row keys 50px, KPS/Total fill ends
            // 标准模式：后排统一50px宽，KPS/Total填满两端
            if (style == KeyviewerStyle.Full108)
                return default; // full keyboard ignores LayoutDesc (built from key108) / 全键盘不依赖 LayoutDesc（用 key108 构建）
            if (Settings?.Data?.StandardKeyWidth == true)
            {
                switch (style)
                {
                    case KeyviewerStyle.Key10:
                        return new LayoutDesc
                        {
                            frontY = 279, bottomY = 200,
                            extras = new ExtraSlot[]
                            {
                                new() { index = 8, x = 162, y = 225, w = 50, rainRow = 1 },
                                new() { index = 9, x = 216, y = 225, w = 50, rainRow = 1 },
                                new() { index = -1, x = 0, y = 225, w = 158, rainRow = -1 },
                                new() { index = -2, x = 270, y = 225, w = 158, rainRow = -1 },
                            }
                        };
                    case KeyviewerStyle.Key12:
                        return new LayoutDesc
                        {
                            frontY = 279, bottomY = 200,
                            extras = new ExtraSlot[]
                            {
                                new() { index = 9, x = 108, y = 225, w = 50, rainRow = 1 },
                                new() { index = 8, x = 162, y = 225, w = 50, rainRow = 1 },
                                new() { index = 10, x = 216, y = 225, w = 50, rainRow = 1 },
                                new() { index = 11, x = 270, y = 225, w = 50, rainRow = 1 },
                                new() { index = -1, x = 0, y = 225, w = 104, rainRow = -1 },
                                new() { index = -2, x = 324, y = 225, w = 104, rainRow = -1 },
                            }
                        };
                    case KeyviewerStyle.Key20:
                        return new LayoutDesc
                        {
                            frontY = 333, bottomY = 200,
                            extras = new ExtraSlot[]
                            {
                                new() { index = 12, x = 0, y = 279, w = 50, rainRow = 1 },
                                new() { index = 13, x = 54, y = 279, w = 50, rainRow = 1 },
                                new() { index = 9, x = 108, y = 279, w = 50, rainRow = 1 },
                                new() { index = 8, x = 162, y = 279, w = 50, rainRow = 1 },
                                new() { index = 10, x = 216, y = 279, w = 50, rainRow = 1 },
                                new() { index = 11, x = 270, y = 279, w = 50, rainRow = 1 },
                                new() { index = 14, x = 324, y = 279, w = 50, rainRow = 1 },
                                new() { index = 15, x = 378, y = 279, w = 50, rainRow = 1 },
                                new() { index = 17, x = 108, y = 225, w = 50, rainRow = 3 },
                                new() { index = 16, x = 162, y = 225, w = 50, rainRow = 3 },
                                new() { index = 18, x = 216, y = 225, w = 50, rainRow = 3 },
                                new() { index = 19, x = 270, y = 225, w = 50, rainRow = 3 },
                                new() { index = -1, x = 0, y = 225, w = 104, rainRow = -1 },
                                new() { index = -2, x = 324, y = 225, w = 104, rainRow = -1 },
                            }
                        };
                }
            }

            return style switch
            {
                KeyviewerStyle.Key8 => new LayoutDesc
                {
                    frontY = 266, bottomY = 205,
                    extras = new ExtraSlot[]
                    {
                        new() { index = -1, x = 0, y = 220, w = 212, rainRow = -1, slim = true },
                        new() { index = -2, x = 216, y = 220, w = 212, rainRow = -1, slim = true },
                    }
                },
                KeyviewerStyle.Key10 => new LayoutDesc
                {
                    frontY = 279, bottomY = 200,
                    extras = new ExtraSlot[]
                    {
                        new() { index = 8, x = 81, y = 225, w = 129, rainRow = 1 },
                        new() { index = 9, x = 216, y = 225, w = 129, rainRow = 1 },
                        new() { index = -1, x = 0, y = 225, w = 77, rainRow = -1 },
                        new() { index = -2, x = 351, y = 225, w = 77, rainRow = -1 },
                    }
                },
                KeyviewerStyle.Key12 => new LayoutDesc
                {
                    frontY = 279, bottomY = 200,
                    extras = new ExtraSlot[]
                    {
                        new() { index = 8, x = 135, y = 225, w = 77, rainRow = 1 },
                        new() { index = 9, x = 81, y = 225, w = 50, rainRow = 1 },
                        new() { index = 10, x = 216, y = 225, w = 77, rainRow = 1 },
                        new() { index = 11, x = 297, y = 225, w = 50, rainRow = 1 },
                        new() { index = -1, x = 0, y = 225, w = 77, rainRow = -1 },
                        new() { index = -2, x = 351, y = 225, w = 77, rainRow = -1 },
                    }
                },
                KeyviewerStyle.Key14 => new LayoutDesc
                {
                    frontY = 320, bottomY = 205,
                    extras = new ExtraSlot[]
                    {
                        new() { index = 13, x = 54, y = 266, w = 50, rainRow = 1 },
                        new() { index = 9, x = 108, y = 266, w = 50, rainRow = 1 },
                        new() { index = 8, x = 162, y = 266, w = 50, rainRow = 1 },
                        new() { index = 10, x = 216, y = 266, w = 50, rainRow = 1 },
                        new() { index = 11, x = 270, y = 266, w = 50, rainRow = 1 },
                        new() { index = 12, x = 324, y = 266, w = 50, rainRow = 1 },
                        new() { index = -1, x = 0, y = 220, w = 212, rainRow = -1, slim = true },
                        new() { index = -2, x = 216, y = 220, w = 212, rainRow = -1, slim = true },
                    }
                },
                KeyviewerStyle.Key16 => new LayoutDesc
                {
                    frontY = 320, bottomY = 205,
                    extras = new ExtraSlot[]
                    {
                        new() { index = 12, x = 0, y = 266, w = 50, rainRow = 1 },
                        new() { index = 13, x = 54, y = 266, w = 50, rainRow = 1 },
                        new() { index = 9, x = 108, y = 266, w = 50, rainRow = 1 },
                        new() { index = 8, x = 162, y = 266, w = 50, rainRow = 1 },
                        new() { index = 10, x = 216, y = 266, w = 50, rainRow = 1 },
                        new() { index = 11, x = 270, y = 266, w = 50, rainRow = 1 },
                        new() { index = 14, x = 324, y = 266, w = 50, rainRow = 1 },
                        new() { index = 15, x = 378, y = 266, w = 50, rainRow = 1 },
                        new() { index = -1, x = 0, y = 220, w = 212, rainRow = -1, slim = true },
                        new() { index = -2, x = 216, y = 220, w = 212, rainRow = -1, slim = true },
                    }
                },
                KeyviewerStyle.Key20 => new LayoutDesc
                {
                    frontY = 333, bottomY = 200,
                    extras = new ExtraSlot[]
                    {
                        new() { index = 12, x = 0, y = 279, w = 50, rainRow = 1 },
                        new() { index = 13, x = 54, y = 279, w = 50, rainRow = 1 },
                        new() { index = 9, x = 108, y = 279, w = 50, rainRow = 1 },
                        new() { index = 8, x = 162, y = 279, w = 50, rainRow = 1 },
                        new() { index = 10, x = 216, y = 279, w = 50, rainRow = 1 },
                        new() { index = 11, x = 270, y = 279, w = 50, rainRow = 1 },
                        new() { index = 14, x = 324, y = 279, w = 50, rainRow = 1 },
                        new() { index = 15, x = 378, y = 279, w = 50, rainRow = 1 },
                        new() { index = 16, x = 135, y = 225, w = 77, rainRow = 3 },
                        new() { index = 17, x = 81, y = 225, w = 50, rainRow = 3 },
                        new() { index = 18, x = 216, y = 225, w = 77, rainRow = 3 },
                        new() { index = 19, x = 297, y = 225, w = 50, rainRow = 3 },
                        new() { index = -1, x = 0, y = 225, w = 77, rainRow = -1 },
                        new() { index = -2, x = 351, y = 225, w = 77, rainRow = -1 },
                    }
                },
                KeyviewerStyle.Key24 => new LayoutDesc
                {
                    frontY = 375, bottomY = 205,
                    extras = new ExtraSlot[]
                    {
                        new() { index = 12, x = 0, y = 321, w = 50, rainRow = 1 },
                        new() { index = 13, x = 54, y = 321, w = 50, rainRow = 1 },
                        new() { index = 9, x = 108, y = 321, w = 50, rainRow = 1 },
                        new() { index = 8, x = 162, y = 321, w = 50, rainRow = 1 },
                        new() { index = 10, x = 216, y = 321, w = 50, rainRow = 1 },
                        new() { index = 11, x = 270, y = 321, w = 50, rainRow = 1 },
                        new() { index = 14, x = 324, y = 321, w = 50, rainRow = 1 },
                        new() { index = 15, x = 378, y = 321, w = 50, rainRow = 1 },
                        new() { index = 17, x = 0, y = 267, w = 50, rainRow = 3 },
                        new() { index = 16, x = 54, y = 267, w = 50, rainRow = 3 },
                        new() { index = 18, x = 108, y = 267, w = 50, rainRow = 3 },
                        new() { index = 19, x = 162, y = 267, w = 50, rainRow = 3 },
                        new() { index = 21, x = 216, y = 267, w = 50, rainRow = 3 },
                        new() { index = 20, x = 270, y = 267, w = 50, rainRow = 3 },
                        new() { index = 22, x = 324, y = 267, w = 50, rainRow = 3 },
                        new() { index = 23, x = 378, y = 267, w = 50, rainRow = 3 },
                        new() { index = -1, x = 0, y = 221, w = 212, rainRow = -1, slim = true },
                        new() { index = -2, x = 216, y = 221, w = 212, rainRow = -1, slim = true },
                    }
                },
                // Defensive fallback instead of throw: EnsureSettingsArrays clamps deserialized
                // enums, but any future path that slips an unknown value through must not take down
                // EnableKeyViewer (half-initialized overlay + per-frame NRE) AND the settings window
                // (KpsTotalIsSlim → GetLayout) — the user couldn't recover from the GUI.
                // 防御性回落而非 throw:EnsureSettingsArrays 会钳制反序列化枚举,但任何未来路径
                // 若漏进未知值,不能拖垮 EnableKeyViewer(半初始化覆盖层+逐帧 NRE)和设置窗口
                //(KpsTotalIsSlim → GetLayout)——否则用户无法从界面自救。
                _ => GetLayout(KeyviewerStyle.Key16)
            };
        }

        /// <summary>Whether the current layout's KPS / Total boxes use the flat (slim) left-label / right-number design / 当前布局的 KPS/Total 是否采用扁平（slim）左右设计</summary>
        // Cached — this runs on the KPS/Total update path for every keypress, and GetLayout allocates
        // a fresh ExtraSlot[] per call. The result depends only on style + StandardKeyWidth, so a
        // two-bit cache key covers every invalidation (layout switch, toggle, profile switch).
        // 结果缓存——每次按键都会经 KPS/Total 更新路径调用，而 GetLayout 每次新分配 ExtraSlot[]。
        // 结果只取决于 style + StandardKeyWidth，双比特缓存键覆盖全部失效场景（切布局、切开关、切 Profile）。
        private static int _slimCacheKey = int.MinValue;
        private static bool _slimCacheValue;
        private static bool KpsTotalIsSlim()
        {
            if (IsFullKeyboard) return true; // full keyboard KPS/Total are always slim / 全键盘 KPS/Total 始终为 slim
            // Custom stat nodes honor the global hide-label toggle like the fixed non-slim designs
            // (value centered alone); with centered on they stay slim for the centered/stacked
            // modes. Reading the raw KpsTotalCentered toggle (not KpsTotalCenteredApplies) avoids
            // recursion — Applies() itself calls this. / 自定义 KPS/Total 节点像固定布局的非 slim
            // 设计一样响应全局隐藏标签开关（仅数值居中）；居中开启时保持 slim 走居中/堆叠模式。
            // 读原始 KpsTotalCentered 开关（而非 KpsTotalCenteredApplies）以避免递归——后者本身
            // 调用本函数。
            if (IsCustomLayout) return !Settings.Data.HideKpsTotalLabel || Settings.Data.KpsTotalCentered;
            int key = (int)Settings.Data.KeyViewerStyle << 1 | (Settings.Data.StandardKeyWidth ? 1 : 0);
            if (key == _slimCacheKey) return _slimCacheValue;
            _slimCacheKey = key;
            var layout = GetLayout(Settings.Data.KeyViewerStyle);
            _slimCacheValue = false;
            if (layout.extras != null)
            {
                foreach (var e in layout.extras)
                    if (e.index == -1) { _slimCacheValue = e.slim; break; }
            }
            return _slimCacheValue;
        }

        /// <summary>Single source of truth: centered KPS/Total applies only to flat (slim) designs with the toggle on.
        /// 唯一判定：仅扁平（slim）设计且开关开启时，KPS/Total 才居中。显示开关与功能生效共用此判定，保证一致。</summary>
        private static bool KpsTotalCenteredApplies()
            => KpsTotalIsSlim() && Settings.Data.KpsTotalCentered;

        /// <summary>Stacked mode: label and value on separate lines (applies when both centered and stacked are true)
        /// 堆叠模式：标签和数值分两行显示（需同时开启居中和堆叠）</summary>
        private static bool KpsTotalStackedApplies()
            => KpsTotalCenteredApplies() && Settings.Data.KpsTotalStackedWhenCentered;

        /// <summary>Effective stat text mode for a stat key: the FreeMake node's layout override
        /// when enabled, otherwise the global toggles (fixed layouts pass a null node).
        /// hideLabel (value-only) only pairs with slim=false, mirroring the fixed designs.
        /// / 面板按键的生效文本模式：FreeMake 节点开启覆盖时用节点值，否则跟随全局开关
        /// （固定布局传入空节点）。hideLabel（仅数值）与非 slim 配对，与固定设计一致。</summary>
        private void StatTextMode(FmNode node, out bool slim, out bool centered, out bool stacked, out bool hideLabel)
        {
            if (node != null && (node.NodeType == 1 || node.NodeType == 2) && node.UseCustomStatLayout)
            {
                centered = node.StatCentered;
                stacked = centered && node.StatStacked;
                hideLabel = !centered && node.HideLabel;
                slim = !hideLabel;
            }
            else
            {
                slim = KpsTotalIsSlim();
                centered = KpsTotalCenteredApplies();
                stacked = KpsTotalStackedApplies();
                hideLabel = !slim && Settings.Data.HideKpsTotalLabel;
            }
        }

        private void InitializeMainKeys(LayoutDesc layout)
        {
            // Fixed layouts contain no image/video nodes, so any video player left over from a
            // custom layout is now unreferenced — release it here instead of waiting for teardown,
            // or the decoder keeps running with no visible quad after a layout switch. /
            // 固定布局不含图片/视频节点，故自定义布局遗留的视频播放器已无引用——在此释放而不等到
            // 拆解，否则切换布局后解码器会继续运行而画面上没有四边形。
            if (!IsCustomLayout) Rendering.KvVideoTextureManager.ReleaseAll();
            if (IsFullKeyboard) { InitializeFullKeyboard(); return; }
            if (IsCustomLayout) { InitializeCustomLayout(); return; }
            int remove = Settings.Data.DownLocation ? 200 : 0;
            for (int i = 0; i < 8; i++)
                Keys[i] = CreateKey(i, 54 * i, layout.frontY - remove, 50, 0);
            foreach (var e in layout.extras)
            {
                bool hideLabel = (e.index == -1 || e.index == -2) && !e.slim && Settings.Data.HideKpsTotalLabel;
                Key key = CreateKey(e.index, e.x, e.y - remove, e.w, e.rainRow, e.slim, true, 0f, false, hideLabel);
                if (e.index == -1) Kps = key;
                else if (e.index == -2) Total = key;
                else Keys[e.index] = key;
            }
            // Keep each key's own RainLine — adjust X offset and width for front-column alignment
            // 每键独立雨滴容器，不共享，只调 X 偏移和宽度对齐前列
            ApplyRainContainerSharing();
        }

        /// <summary>
        /// Build the 108-key physical keyboard layout with realistic QWERTY stagger / 构建 108 键物理键盘，真实 QWERTY 错位排布
        /// Keys are created from Settings.Data.key108 (index-aligned with BuildDefaultKey108). / 按键来自 key108 数组（下标与 BuildDefaultKey108 对齐）
        /// No foot keys, no per-key colors, no ghost keys. / 无脚键、无每键配色、无鬼键。
        /// </summary>
        /// <summary>Shared 108-key slot table (index, x, width-units) used by both the fixed layout
        /// builder and the FreeMake preset generator. / 108 键槽位表（索引, x, y, 宽度单位），
        /// 固定布局构建与 FreeMake 预设生成共用。</summary>
        private static System.Collections.Generic.List<(int idx, double x, double y, double w)> Full108SlotTable()
        {
            const float U = 50f; // base key unit / 基准键宽
            return new System.Collections.Generic.List<(int idx, double x, double y, double w)>
            {
                (0, 0*U, 580, 1*U),
                (1, 2*U, 580, 1*U),
                (2, 3*U, 580, 1*U),
                (3, 4*U, 580, 1*U),
                (4, 5*U, 580, 1*U),
                (5, 6.5*U, 580, 1*U),
                (6, 7.5*U, 580, 1*U),
                (7, 8.5*U, 580, 1*U),
                (8, 9.5*U, 580, 1*U),
                (9, 11*U, 580, 1*U),
                (10, 12*U, 580, 1*U),
                (11, 13*U, 580, 1*U),
                (12, 14*U, 580, 1*U),
                (13, 15*U, 580, 1*U),
                (14, 16*U, 580, 1*U),
                (15, 17*U, 580, 1*U),
                (16, 18*U, 580, 1*U),
                (17, 0*U, 524, 1*U),
                (18, 1*U, 524, 1*U),
                (19, 2*U, 524, 1*U),
                (20, 3*U, 524, 1*U),
                (21, 4*U, 524, 1*U),
                (22, 5*U, 524, 1*U),
                (23, 6*U, 524, 1*U),
                (24, 7*U, 524, 1*U),
                (25, 8*U, 524, 1*U),
                (26, 9*U, 524, 1*U),
                (27, 10*U, 524, 1*U),
                (28, 11*U, 524, 1*U),
                (29, 12*U, 524, 1*U),
                (30, 13*U, 524, 2*U),
                (31, 0*U, 468, 1.5*U),
                (32, 1.5*U, 468, 1*U),
                (33, 2.5*U, 468, 1*U),
                (34, 3.5*U, 468, 1*U),
                (35, 4.5*U, 468, 1*U),
                (36, 5.5*U, 468, 1*U),
                (37, 6.5*U, 468, 1*U),
                (38, 7.5*U, 468, 1*U),
                (39, 8.5*U, 468, 1*U),
                (40, 9.5*U, 468, 1*U),
                (41, 10.5*U, 468, 1*U),
                (42, 11.5*U, 468, 1*U),
                (43, 12.5*U, 468, 1*U),
                (44, 13.5*U, 468, 1.5*U),
                (45, 0*U, 412, 1.75*U),
                (46, 1.75*U, 412, 1*U),
                (47, 2.75*U, 412, 1*U),
                (48, 3.75*U, 412, 1*U),
                (49, 4.75*U, 412, 1*U),
                (50, 5.75*U, 412, 1*U),
                (51, 6.75*U, 412, 1*U),
                (52, 7.75*U, 412, 1*U),
                (53, 8.75*U, 412, 1*U),
                (54, 9.75*U, 412, 1*U),
                (55, 10.75*U, 412, 1*U),
                (56, 11.75*U, 412, 1*U),
                (57, 12.75*U, 412, 2.25*U),
                (58, 0*U, 356, 2.25*U),
                (59, 2.25*U, 356, 1*U),
                (60, 3.25*U, 356, 1*U),
                (61, 4.25*U, 356, 1*U),
                (62, 5.25*U, 356, 1*U),
                (63, 6.25*U, 356, 1*U),
                (64, 7.25*U, 356, 1*U),
                (65, 8.25*U, 356, 1*U),
                (66, 9.25*U, 356, 1*U),
                (67, 10.25*U, 356, 1*U),
                (68, 11.25*U, 356, 1*U),
                (69, 12.25*U, 356, 2.75*U),
                (70, 0*U, 300, 1.25*U),
                (71, 1.25*U, 300, 1.25*U),
                (72, 2.5*U, 300, 1.25*U),
                (73, 3.75*U, 300, 6.25*U),
                (74, 10*U, 300, 1.25*U),
                (75, 11.25*U, 300, 1.25*U),
                (76, 12.5*U, 300, 1.25*U),
                (77, 13.75*U, 300, 1.25*U),
                (78, 19*U, 524, 1*U),
                (79, 19*U, 468, 1*U),
                (80, 20*U, 524, 1*U),
                (81, 20*U, 468, 1*U),
                (82, 21*U, 524, 1*U),
                (83, 21*U, 468, 1*U),
                (84, 20*U, 356, 1*U),
                (85, 19*U, 300, 1*U),
                (86, 20*U, 300, 1*U),
                (87, 21*U, 300, 1*U),
                (88, 22*U, 524, 1*U),
                (89, 23*U, 524, 1*U),
                (90, 24*U, 524, 1*U),
                (91, 25*U, 524, 1*U),
                (92, 22*U, 468, 1*U),
                (93, 23*U, 468, 1*U),
                (94, 24*U, 468, 1*U),
                (95, 25*U, 468, 1*U),
                (96, 22*U, 412, 1*U),
                (97, 23*U, 412, 1*U),
                (98, 24*U, 412, 1*U),
                (99, 22*U, 356, 1*U),
                (100, 23*U, 356, 1*U),
                (101, 24*U, 356, 1*U),
                (102, 22*U, 300, 2*U),
                (103, 24*U, 300, 1*U),
                (104, 25*U, 300, 1*U)
            };
        }

        private void InitializeFullKeyboard()
        {
            const float U = 50f; // base key unit / 基准键宽
            // y values are absolute px in the standard-layout visual band (top row ~350, bottom ~42).
            // DownLocation shifts the whole block down. / y 为标准布局可见视觉带内的绝对像素，DownLocation 整体下移。
            float yShift = Settings.Data.DownLocation ? 200f : 0f;
            var slots = Full108SlotTable();

            // Uniform 6px horizontal gap (matching the 6px vertical row gap): scale every key's x and
            // width from the 50px unit to a 56px column step, then trim 6px off each width. Because the
            // original layout had 0px gaps between flush keys, this yields exactly 6px gaps everywhere
            // while preserving the exact relative positions (no rearrangement, no overlap).
            // 统一 6px 横向间隙（与纵向 6px 行隙一致）：把每个键的 x 与宽从 50px 基准缩放到 56px 列步进，再各减 6px。
            // 原布局紧贴键之间为 0 间隙，故缩放后处处得到恰好 6px 间隙，且相对位置完全不变（不重排、不重叠）。
            const float colStep = 56f;   // 50px key + 6px gap / 键宽50 + 间隙6
            const float gap = 6f;
            // Pull the right cluster left so its left edge hugs the main block's right edge (same role
            // as the old 4U shift, now expressed in the 56px column step). / 把右簇左拉，使其左缘贴住主键区右缘（等价于原 4U 左移，现按 56px 列步进换算）。
            const float rightClusterShift = 4f * colStep;
            const float rowStep = 56f; // vertical distance between key rows / 按键行间距
            foreach (var s in slots)
            {
                float x = (float)s.x * colStep / U - (s.idx >= 78 ? rightClusterShift : 0f);
                if (s.idx == 95 || s.idx == 104)
                {
                    // Numpad "+" (95) and Enter (104) are tall vertical keys, each spanning two rows so the
                    // right column is fully filled (no gaps). / 小键盘「+」(95)与「回车」(104)为竖排高键，各跨两行，右列恰好占满无空隙。
                    float w = colStep - gap; // matches a normal key's 50px width / 与普通键 50px 同宽
                    // height = two key rows (rowStep=56) so the tall key's top/bottom exactly meet the
                    // neighbouring keys' edges (no stray 3px gap, looks the same height as two stacked
                    // normal keys). / 高键取两行距 56px，使其顶/底正好贴合相邻两键边缘，视觉高度与两枚普通键叠放一致。
                    float h = rowStep + 50f;
                    // "+" (95) sits on the 9-key (y=468) and 6-key (y=412) rows; Enter (104) sits on the
                    // 0-key (y=300) and 3-key (y=356) rows. Center each at the midpoint of its two rows. /
                    // 「+」以 9键/6键 中点(440) 为中心；回车以 0键/3键 中点(328) 为中心。
                    float y = (s.idx == 95 ? (468f + 412f) : (300f + 356f)) * 0.5f;
                    Keys[s.idx] = CreateKey(s.idx, x, y - yShift, w, -1, false, false, h);
                }
                else
                {
                    float w = (float)s.w * colStep / U - gap;
                    Keys[s.idx] = CreateKey(s.idx, x, (float)s.y - yShift, w, -1, false, false);
                }
            }
            // Optional KPS / Total boxes, placed at user-set normalized positions / 可选 KPS/Total，位置由用户归一化坐标决定
            if (Settings.Data.FullKeyboardShowKpsTotal)
            {
                float ktSize = Settings.Data.FullKeyboardKpsTotalSize;
                // Y follows the mod-wide convention (0 = top edge, 1 = bottom edge) — these boxes
                // used to be the only place where 1 meant top, contradicting the main/foot
                // position sliders shown in the same panel.
                // Y 遵循全 Mod 约定(0=贴顶,1=贴底)——这两个框曾是唯一"1=贴顶"的地方,与同一
                // 面板中主键盘/脚键位置滑块的语义相反。
                Kps = CreateKey(-1, Settings.Data.FullKpsPosition.x * CanvasWidth, (1f - Settings.Data.FullKpsPosition.y) * 1080f, ktSize, -1, true, true);
                Total = CreateKey(-2, Settings.Data.FullTotalPosition.x * CanvasWidth, (1f - Settings.Data.FullTotalPosition.y) * 1080f, ktSize, -1, true, true);
            }
            ApplyFullKeyboardKpsTotalPosition();
            ApplyFullKeyboardColors();
            CaptureFullKeyboardHome();
        }

        /// <summary>Snapshot the natural anchored positions of every full-keyboard key + KPS/Total / 记录全键盘各键及 KPS/Total 的自然锚点位置</summary>
        private void CaptureFullKeyboardHome()
        {
            if (Keys == null) { _fkHomeValid = false; return; }
            _fkHome = new Vector2[Keys.Length];
            for (int i = 0; i < Keys.Length; i++)
                _fkHome[i] = Keys[i] != null ? ((RectTransform)Keys[i].transform).anchoredPosition : Vector2.zero;
            _fkHomeValid = true;
        }

        /// <summary>Apply unified colors to all 108-key layout keys / 将统一配色应用到全部 108 键</summary>
        private void ApplyFullKeyboardColors()
        {
            if (Keys == null) return;
            var d = Settings.Data;
            bool unified = d.EnableFullKeyboardUnifiedColor;
            Color bg = unified ? d.FullKeyboardBackground : d.Background;
            Color bgC = unified ? d.FullKeyboardBackgroundClicked : d.BackgroundClicked;
            Color ol = unified ? d.FullKeyboardOutline : d.Outline;
            Color olC = unified ? d.FullKeyboardOutlineClicked : d.OutlineClicked;
            Color tx = unified ? d.FullKeyboardText : d.Text;
            Color txC = unified ? d.FullKeyboardTextClicked : d.TextClicked;
            for (int i = 0; i < Keys.Length; i++)
            {
                if (Keys[i] == null) continue;
                bool pressed = Keys[i].isPressed;
                SetShapeColors(Keys[i], pressed ? bgC : bg, pressed ? olC : ol);
                Keys[i].text.color = pressed ? txC : tx;
                if (Keys[i].value != null) Keys[i].value.color = pressed ? txC : tx;
            }
            ApplyKpsTotalColors();
        }

        /// <summary>Apply user-set normalized positions to the KPS / Total boxes (full keyboard only) / 将用户设置的归一化位置套用到 KPS/Total 框（仅全键盘）</summary>
        private void ApplyFullKeyboardKpsTotalPosition()
        {
            // Same mod-wide Y convention as creation above (0 = top, 1 = bottom). / 与上方创建时相同的全 Mod Y 约定(0=顶,1=底)。
            if (Kps != null)
                SetKeyPosition(-1, Settings.Data.FullKpsPosition.x * CanvasWidth, (1f - Settings.Data.FullKpsPosition.y) * 1080f);
            if (Total != null)
                SetKeyPosition(-2, Settings.Data.FullTotalPosition.x * CanvasWidth, (1f - Settings.Data.FullTotalPosition.y) * 1080f);
        }

        /// <summary>Redirect back-row rain to front-row columns for layouts with non-standard key widths:
        /// rain drops inherit the front row's X position, while per-row settings (color, speed,
        /// height) remain from the pressed key's row.
        /// 将非标准宽度布局的后排雨滴重指向前排列，雨滴位置与前列对齐
        /// </summary>
        private void ApplyRainContainerSharing()
        {
            switch (Settings.Data.KeyViewerStyle)
            {
                case KeyviewerStyle.Key10:
                    // Back row [8,9] → front [3,4]
                    ShareRainContainer(8, 3);
                    ShareRainContainer(9, 4);
                    break;
                case KeyviewerStyle.Key12:
                    // Back row [9,8,10,11] → front [2,3,4,5]
                    ShareRainContainer(9, 2);
                    ShareRainContainer(8, 3);
                    ShareRainContainer(10, 4);
                    ShareRainContainer(11, 5);
                    break;
                case KeyviewerStyle.Key20:
                    // Third row [17,16,18,19] → front [2,3,4,5] (same positions as 12K back)
                    ShareRainContainer(17, 2);
                    ShareRainContainer(16, 3);
                    ShareRainContainer(18, 4);
                    ShareRainContainer(19, 5);
                    break;
                case KeyviewerStyle.Key24:
                    // Third row [16-23] all 50px aligned, no sharing needed; included for consistency
                    break;
            }
        }

        private void ShareRainContainer(int backIndex, int frontIndex)
        {
            if (backIndex < 0 || backIndex >= Keys.Length || frontIndex < 0 || frontIndex >= Keys.Length) return;
            var backKey = Keys[backIndex];
            var frontKey = Keys[frontIndex];
            if (backKey == null || frontKey == null) return;

            // Keep the back key's own rain column — just align its center to the front column
            // (the old code repointed the container and reset its width to 50).
            // 保留后排按键自己的雨滴列，只把列中心对齐到前列（旧代码重指容器并把宽度重置为 50）。
            backKey.rainOffsetX = ((RectTransform)frontKey.transform).anchoredPosition.x
                - ((RectTransform)backKey.transform).anchoredPosition.x;
            backKey.rainWidth = 50f;
        }

        private void RepositionMainKeys(LayoutDesc layout, float baseX, float baseY)
        {
            int remove = Settings.Data.DownLocation ? 200 : 0;
            for (int i = 0; i < 8; i++)
                SetKeyPosition(i, baseX + 54 * i, baseY + layout.frontY - remove);
            foreach (var e in layout.extras)
                SetKeyPosition(e.index, baseX + e.x, baseY + e.y - remove);
        }

        /// <summary>Translate the whole 108-key block by the normalized custom-position offset / 按归一化自定义位置整体平移 108 键</summary>
        private void RepositionFullKeyboard()
        {
            if (!_fkHomeValid || _fkHome == null || Keys == null) return;
            Vector2 norm = Settings.Data.MainKeyViewerPosition;
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < _fkHome.Length; i++)
            {
                Vector2 p = _fkHome[i];
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.y < minY) minY = p.y;
                if (p.y > maxY) maxY = p.y;
            }
            // Edge-pinned normalized placement (Y grows upward; 1080 = top, 0 = bottom).
            // norm.x=0 -> left edge at screen left;  norm.x=1 -> right edge at screen right.
            // norm.y=0 -> top edge at screen top;    norm.y=1 -> bottom edge at screen bottom
            // (matches the mod-wide custom-position convention; see the Lerp below).
            // DownLocation is already baked into the captured home positions, so no extra shift here.
            // 归一化贴边定位（Y 向上，1080=顶，0=底）。norm.x=0 左缘贴左，=1 右缘贴右；
            // norm.y=0 顶缘贴顶，=1 底缘贴底（与全 Mod 自定义位置约定一致，见下方 Lerp）。
            // DownLocation 已含在 home 中，不再额外偏移。
            float dxLeft = -minX;
            float dxRight = CanvasWidth - maxX;
            float dx = Mathf.Lerp(dxLeft, dxRight, norm.x);
            float dyBottom = 25f - minY;        // bottom edge (minY - 25) -> y = 0
            float dyTop = (1080f - 25f) - maxY; // top edge (maxY + 25) -> y = 1080
            float dy = Mathf.Lerp(dyTop, dyBottom, norm.y); // Y inverted to match the standard layout (norm.y=1 -> bottom) / Y 反向，与标准布局一致（norm.y=1 贴底）
            for (int i = 0; i < Keys.Length; i++)
            {
                if (Keys[i] == null) continue;
                SetKeyPosition(i, _fkHome[i].x + dx, _fkHome[i].y + dy);
            }
            // KPS / Total keep their own independent normalized positions (not shifted by the main block).
            // KPS/Total 保持各自独立的归一化位置，不随主键盘自定义位置移动。
        }

        /// <summary>
        /// Initialize foot keys starting at Keys[20] / 初始化从 Keys[20] 开始的脚键
        /// Supports 2-16 keys, automatically arranging in 1 or 2 rows / 支持 2-16 个键，自动排列为 1 或 2 排
        /// </summary>
        private void InitializeFootKeyViewer(int size)
        {
            for (int i = FootKeyBase; i < FootKeyBase + size; i++)
            {
                int col;
                int row;
                int footOfs = i - FootKeyBase;
                if (size <= 8)
                {
                    col = footOfs;
                    row = 0;
                }
                else
                {
                    if (footOfs < 8)
                    {
                        col = footOfs;
                        row = 0;
                    }
                    else
                    {
                        col = footOfs - 8;
                        row = 1;
                    }
                }
                int baseY = size > 8 ? 15 + 34 : 15;
                int x = 432 + col * 34;
                // Center the second row under the first when not full / 第二排不满时居中于第一排下方
                if (size > 8 && row == 1)
                    x += (8 - (size - 8)) * 17;
                int y = baseY - row * 34;
                Keys[i] = CreateKey(i, x, y, 30, -1, true, false, 0f, true);
            }
        }

        /// <summary>
        /// Create a single key GameObject with background, outline, text, count, and optional rain container / 创建单个按键 GameObject，包含背景、轮廓、文本、计数和可选的雨滴容器
        /// </summary>
        /// <param name="i">Key index (-1=KPS, -2=Total, 0-35=keys) / 按键索引（-1=KPS，-2=Total，0-35=按键）</param>
        /// <param name="x">X position in canvas reference coordinates / 画布参考坐标系中的 X 位置</param>
        /// <param name="y">Y position in canvas reference coordinates / 画布参考坐标系中的 Y 位置</param>
        /// <param name="sizeX">Key width / 按键宽度</param>
        /// <param name="raining">Rain row index (-1=no rain, 0=row1, 1=row2, 3=row3) / 雨滴行索引（-1=无雨滴，0=第1排，1=第2排，3=第3排）</param>
        /// <param name="slim">Use slim style (for KPS/Total display) / 使用窄样式（用于 KPS/Total 显示）</param>
        /// <param name="count">Show press count text / 显示按下计数文本</param>
        /// <param name="hideLabel">Hide label text, center value (non-slim KPS/Total only) / 隐藏标签文字，数值居中（仅非 slim 的 KPS/Total）</param>
        private Key CreateKey(int i, float x, float y, float sizeX, int raining, bool slim = false, bool count = true, float sizeY = 0f, bool isFootKey = false, bool hideLabel = false, bool? forceCentered = null, bool? forceStacked = null, int? explicitShapeSlot = null)
        {
            // Custom nodes own their count visibility (node.HideCount) — the global hide-main-count
            // toggle is a fixed-layout control and used to strip the value texts of every custom
            // key regardless of node settings. / 自定义节点的计数可见性由节点自身（HideCount）决定
            // ——全局「隐藏主键计数」是固定布局的开关，此前会无视节点设置把所有自定义键的数值
            // 文本整个去掉。
            if (!IsCustomLayout && i >= 0 && i < FootKeyBase && Settings.Data.HideMainKeyCount)
                count = false;
            // KPS / Total boxes: four modes
            // 1. Default slim: left label / right number
            // 2. Centered single-line: "KPS 123" in one centered box
            // 3. Centered stacked: label on top, number below (smaller font to fit)
            // 4. Hide label: value centered, replacing the top label (non-slim only)
            // KPS/Total 框四种模式：
            // 1. 默认 slim：左标签右数值
            // 2. 居中单行："KPS 123" 合并为一个居中框
            // 3. 居中堆叠：标签在上方，数值在下方（字号缩小以适应）
            // 4. 隐藏标签：数值居中，替换顶部标签（仅非 slim）
            // forceCentered/forceStacked: per-node overrides from the FreeMake stat nodes; null =
            // derive from the global toggles (fixed layouts). / forceCentered/forceStacked：
            // FreeMake 面板节点的节点级覆盖；null = 跟随全局开关（固定布局）。
            bool centeredText = (i == -1 || i == -2) && (forceCentered ?? KpsTotalCenteredApplies());
            bool stackedText = centeredText && (forceStacked ?? KpsTotalStackedApplies());
            GameObject obj = new("Key " + i);
            KeyViewerSettings settings = Settings;
            // For stacked mode, use taller container to fit two rows of text without overlap
            // 堆叠模式使用更高的容器，让两行文字不重叠
            float h = sizeY > 0f ? sizeY : (stackedText ? 30f : (slim ? 30f : 50f));
            // Key root: anchors the rain container and marks the key box position (pivot left-center)
            // 按键根：锚定雨滴容器，标记按键框位置（轴心左中）
            RectTransform transform = obj.AddComponent<RectTransform>();
            transform.SetParent(KeyViewerSizeObject.transform);
            transform.sizeDelta = new Vector2(sizeX, h);
            transform.anchorMin = transform.anchorMax = Vector2.zero;
            transform.pivot = new Vector2(0, 0.5f);
            transform.anchoredPosition = new Vector2(x, y);
            transform.localScale = Vector3.one;
            Key key = obj.AddComponent<Key>();
            key.isPressed = false;
            key.keySize = new Vector2(sizeX, h);
            // Box goes into the merged shape layer (bottom-left rect coordinates) / 按键框进合并形状层（左下角矩形坐标）
            // Box goes into the merged shape layer (bottom-left rect coordinates). Custom stat
            // panels pass an EXPLICIT slot — KeyIndex(-1/-2) collapses every KPS onto one slot,
            // which stacked multiple panels onto the last one's box. /
            // 按键框进合并形状层（左下角矩形坐标）。自定义面板传入显式槽位——KeyIndex(-1/-2)
            // 会把所有 KPS 压到同一个槽，多面板时全部叠到最后一个的框上。
            key.shapeSlot = explicitShapeSlot ?? KeyIndex(i);
            if (keyShapeLayer != null && key.shapeSlot >= 0)
            {
                keyShapeLayer.SetRect(key.shapeSlot, x, y - h * 0.5f, sizeX, h);
                keyShapeLayer.SetVisible(key.shapeSlot, true);
            }
            // Text wrapper in the text canvas: same rect the old per-key Visuals had (center pivot
            // over the box), so press scaling stays centered and the text layout math is unchanged.
            // 文本画布中的文本包裹层：与旧每键 Visuals 同矩形（轴心在框中心），按压缩放保持居中，文本布局算法不变。
            GameObject visuals = new("TextRoot");
            RectTransform vrt = visuals.AddComponent<RectTransform>();
            vrt.SetParent(textLayer);
            vrt.sizeDelta = new Vector2(sizeX, h);
            vrt.anchorMin = vrt.anchorMax = Vector2.zero;
            vrt.pivot = new Vector2(0.5f, 0.5f);
            vrt.anchoredPosition = new Vector2(x + sizeX * 0.5f, y);
            vrt.localScale = Vector3.one;
            key.visuals = visuals.transform;
            // Per-key slot index for text size/spacing overrides: KPS → MaxKeySlots, Total → MaxKeySlots+1 / 每键槽位索引
            int pki = i == -1 ? MaxKeySlots : i == -2 ? MaxKeySlots + 1 : i;
            // For stacked mode, create separate text/value objects; otherwise use single centered text
            // 堆叠模式创建分离的标签/数值对象，否则使用单个居中文本
            if (stackedText)
            {
                key.text = CreateKeyText(visuals, sizeX, slim, false, settings, false, true, pki, isFootKey); // label top
                key.value = CreateCountText(visuals, sizeX, slim, settings, true, pki); // value bottom
            }
            else if (centeredText)
            {
                key.text = CreateKeyText(visuals, sizeX, slim, false, settings, true, false, pki); // single merged
            }
            else if (hideLabel)
            {
                // Hide label: single centered text, value only / 隐藏标签：单个居中文本，仅数值
                key.text = CreateKeyText(visuals, sizeX, slim, false, settings, true, false, pki);
            }
            else if (count)
            {
                key.text = CreateKeyText(visuals, sizeX, slim, true, settings, false, false, pki);
                key.value = CreateCountText(visuals, sizeX, slim, settings, false, pki);
            }
            else
            {
                key.text = CreateKeyText(visuals, sizeX, slim, false, settings, false, false, pki, isFootKey);
            }
            UpdateKeyText(key, i);
            SetupRainContainer(key, raining, sizeX);
            ApplyKeyColors(key, i, raining);
            return key;
        }

        /// <summary>Child GameObject filling the SizeObject rect (bottom-left origin) / 填满 SizeObject 矩形的子物体（左下原点）</summary>
        private RectTransform CreateFullStretchRect(string name)
        {
            GameObject go = new GameObject(name);
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.SetParent(KeyViewerSizeObject.transform);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = Vector2.zero;
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            rt.localScale = Vector3.one;
            return rt;
        }

        /// <summary>Write a key's box colors into the merged shape layer / 将按键框颜色写入合并形状层</summary>
        private void SetShapeColors(Key key, Color background, Color outline)
        {
            if (key == null || keyShapeLayer == null || key.shapeSlot < 0) return;
            keyShapeLayer.SetColors(key.shapeSlot, background, outline);
        }

        /// <summary>Toggle a KPS/Total box (shape + texts) without touching the key root / 切换 KPS/Total 框（形状+文本）的显示，不动按键根</summary>
        private void SetKeyObjectActive(Key key, bool active)
        {
            if (key == null) return;
            if (key.visuals != null) key.visuals.gameObject.SetActive(active);
            if (keyShapeLayer != null && key.shapeSlot >= 0) keyShapeLayer.SetVisible(key.shapeSlot, active);
        }

        private TextMeshProUGUI CreateKeyText(GameObject parent, float sizeX, bool slim, bool count, KeyViewerSettings settings, bool centered = false, bool stackedLabel = false, int perKeyIndex = -1, bool isFootKey = false)
        {
            GameObject go = new("KeyText");
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent.transform);
            if (centered && !stackedLabel)
            {
                // Single merged, full-width, centered box for the combined "label value" string.
                // 合并用的整宽居中框，承载 "标签 数值" 整体。
                rt.sizeDelta = new Vector2(sizeX - 4, 32);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
            }
            else if (stackedLabel)
            {
                // Label at top for stacked mode (compact font, inset to stay inside the key box) / 堆叠模式标签在上方（紧凑字体，内嵌在按键框内）
                rt.sizeDelta = new Vector2(sizeX - 4, 14);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1);
                rt.anchoredPosition = new Vector2(0, -1);
            }
            else if (slim && !isFootKey)
            {
                rt.sizeDelta = new Vector2(sizeX / 2, 30);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0, 0.5f);
                rt.anchoredPosition = new Vector2(count ? 10 : 7.5f, 0);
            }
            else
            {
                rt.sizeDelta = new Vector2(sizeX - 4, 32);
                if (!count)
                {
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
                    rt.anchoredPosition = Vector2.zero;
                }
                else
                {
                    rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 1);
                    rt.anchoredPosition = new Vector2(0, 2);
                }
            }
            rt.localScale = Vector3.one;
            TextAlignmentOptions align = stackedLabel ? TextAlignmentOptions.Center : (centered ? TextAlignmentOptions.Center : ((slim && !isFootKey) ? TextAlignmentOptions.Left : TextAlignmentOptions.Center));
            return ConfigureText(go, settings, align, perKeyIndex, Rendering.KvTextKind.KeyLabel);
        }

        private TextMeshProUGUI CreateCountText(GameObject parent, float sizeX, bool slim, KeyViewerSettings settings, bool stackedValue = false, int perKeyIndex = -1)
        {
            GameObject go = new("CountText");
            RectTransform rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent.transform);
            if (stackedValue)
            {
                // Value at bottom for stacked mode (inset to stay inside the key box) / 堆叠模式数值在下方（内嵌在按键框内）
                rt.sizeDelta = new Vector2(sizeX - 4, 14);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0);
                rt.anchoredPosition = new Vector2(0, 1);
            }
            else if (slim)
            {
                rt.sizeDelta = new Vector2(sizeX / 2, 30);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1, 0.5f);
                rt.anchoredPosition = new Vector2(-10, 0);
            }
            else
            {
                rt.sizeDelta = new Vector2(sizeX - 4, 16);
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0);
                rt.anchoredPosition = new Vector2(0, 2);
            }
            rt.localScale = Vector3.one;
            TextAlignmentOptions align = stackedValue ? TextAlignmentOptions.Center : (slim ? TextAlignmentOptions.Right : TextAlignmentOptions.Top);
            return ConfigureText(go, settings, align, perKeyIndex, Rendering.KvTextKind.Count);
        }

        private TextMeshProUGUI ConfigureText(GameObject go, KeyViewerSettings settings, TextAlignmentOptions alignment, int perKeyIndex = -1, Rendering.KvTextKind kind = Rendering.KvTextKind.KeyLabel)
        {
            var text = go.AddComponent<TextMeshProUGUI>();
            var keyFont = GetCurrentFont();
            if (keyFont != null)
            {
                text.font = keyFont;
                // Global outline/shadow (custom nodes may override this right after CreateKey
                // returns, via ApplyCustomTextStyles). / 全局描边/阴影（自定义节点可能在 CreateKey
                // 返回后由 ApplyCustomTextStyles 覆盖）。
                var mat = GetTextStyleMaterial(keyFont, Rendering.KvTextStyle.Resolve(settings.Data, null, kind));
                if (mat != null) text.fontMaterial = mat;
            }
            text.fontStyle = (FontStyles)settings.Data.FontStyleFlags;
            text.enableAutoSizing = true;
            text.fontSizeMin = 0;

            // Per-key font size and vertical offset (via RectTransform Y) / 每键字号和垂直偏移（通过 RectTransform Y）
            if (settings.Data.EnablePerKeyTextSize && perKeyIndex >= 0)
            {
                int pi = perKeyIndex;
                if (pi >= 0 && pi < settings.Data.PerKeyFontSize.Length && settings.Data.PerKeyFontSize[pi] > 0)
                {
                    text.fontSizeMax = settings.Data.PerKeyFontSize[pi];
                }
                else
                {
                    text.fontSizeMax = settings.Data.KeyFontSize;
                }
            }
            else
            {
                text.fontSizeMax = settings.Data.KeyFontSize;
            }

            text.alignment = alignment;
            text.color = settings.Data.Text;
            text.raycastTarget = false;
            return text;
        }

        /// <summary>Assign the key's rain row (-1 = no rain); drops render in the merged RainLayer /
        /// 设置按键的雨滴行（-1 = 无雨滴）；雨滴绘制在合并 RainLayer 中</summary>
        private static void SetupRainContainer(Key key, int raining, float sizeX)
        {
            key.color = raining >= 0 ? (byte)raining : (byte)1;
            key.rainWidth = sizeX;
        }

        private int KeyIndex(int i) => i >= 0 && i < Keys.Length ? i : i == -1 ? Keys.Length : i == -2 ? Keys.Length + 1 : -1;

        private void ApplyKeyColors(Key key, int i, int raining)
        {
            int pi = KeyIndex(i);
            if (IsFullKeyboard) return; // colors set by ApplyFullKeyboardColors after creation
            // Custom layouts: colors are node-owned (ApplyCustomKeyColors overwrites right after
            // CreateKey returns) and the per-key arrays are sized MaxKeySlots+2 — custom slots can
            // exceed that (the 108K preset fills 105 slots), so indexing here would go out of
            // bounds. / 自定义布局：颜色归节点所有（CreateKey 返回后立即被
            // ApplyCustomKeyColors 覆盖），且每键数组按 MaxKeySlots+2 定长——自定义槽位可超出
            // （108K 预设占满 105 槽），此处索引会越界。
            if (IsCustomLayout) return;
            if (Settings.Data.EnablePerKeyColors)
            {
                if (pi < 0) return;
                SetShapeColors(key, Settings.Data.PerKeyBackground[pi], Settings.Data.PerKeyOutline[pi]);
                key.text.color = Settings.Data.PerKeyText[pi];
                if (key.value != null) key.value.color = Settings.Data.PerKeyText[pi];
                key.rainColor = Settings.Data.PerKeyRainColor[pi];
                return;
            }
            if (pi >= Keys.Length)
            {
                bool isKps = pi == Keys.Length;
                SetShapeColors(key,
                    isKps ? Settings.Data.KpsBackground : Settings.Data.TotalBackground,
                    isKps ? Settings.Data.KpsOutline : Settings.Data.TotalOutline);
                key.text.color = isKps ? Settings.Data.KpsText : Settings.Data.TotalText;
                if (key.value != null) key.value.color = key.text.color;
            }
            else
            {
                SetShapeColors(key, Settings.Data.Background, Settings.Data.Outline);
            }
            if (raining >= 0)
                key.rainColor = rainSystem.GetRainColor((byte)raining);
        }

        /// <summary>
        /// Set the KPS / Total display. Modes:
        /// 1. Default slim: separate label (left) and value (right)
        /// 2. Centered single-line: merged "KPS 123" in one centered box
        /// 3. Centered stacked: label (top) and value (bottom) with smaller fonts
        /// 4. Hide label (non-slim only): value centered, replacing the top label
        /// 设置 KPS/Total 显示。模式：
        /// 1. 默认 slim：标签（左）和数值（右）分开
        /// 2. 居中单行："KPS 123" 合并为一个居中框
        /// 3. 居中堆叠：标签（上）和数值（下）用较小字号
        /// 4. 隐藏标签（仅非 slim）：数值居中显示，替换顶部标签
        /// </summary>
        private void SetKpsTotalDisplay(Key key, string defaultLabel, string valueStr)
        {
            if (key == null) return;
            bool isKps = defaultLabel == "KPS";
            string customLabel = isKps ? Settings.Data.KpsLabel : Settings.Data.TotalLabel;
            string label = customLabel;

            // One mode resolver for fixed layouts AND custom stat nodes — the node's layout
            // override (UseCustomStatLayout) wins over the global toggles.
            // 固定布局与自定义面板节点共用同一模式解析——节点布局覆盖（UseCustomStatLayout）
            // 优先于全局开关。
            StatTextMode(key.CustomNode, out bool slim, out bool centered, out bool stacked, out bool hideLabelMode);

            // Hide label mode: value hidden, text already centered from CreateKey
            // 隐藏标签模式：隐藏数值，文字已在 CreateKey 中居中
            if (hideLabelMode)
            {
                if (key.value != null)
                    key.value.gameObject.SetActive(false);
                key.text.text = valueStr;
            }
            else
            {
                if (stacked)
                {
                    if (key.value != null)
                    {
                        key.value.gameObject.SetActive(true);
                        key.value.text = valueStr;
                    }
                    key.text.text = label;
                }
                else if (centered)
                {
                    if (key.value != null) key.value.gameObject.SetActive(false);
                    key.text.text = label + " " + valueStr;
                }
                else
                {
                    if (key.value != null)
                    {
                        key.value.gameObject.SetActive(true);
                        key.value.text = valueStr;
                    }
                    key.text.text = label;
                }
            }
        }

        /// <summary>Refresh only the label text (called after user changes KPS/Total custom label) / 仅刷新标签文本（用户修改 KPS/Total 自定义标签后调用）</summary>
        private void RefreshKpsTotalLabels()
        {
            // Use the live KPS value, not a hardcoded "0": ProcessKpsInUpdate only rewrites the
            // text when the count CHANGES, so while the user holds a steady rate the box would
            // otherwise show a wrong 0 indefinitely. lastKps = -1 also forces a rewrite next frame.
            // 用实时 KPS 值而非硬编码 "0":ProcessKpsInUpdate 只在计数变化时重写文本,匀速游玩时
            // 数值框会无限期显示错误的 0。lastKps = -1 同时强制下帧重写。
            if (IsCustomLayout)
            {
                // Each stat panel takes its own group's value — a global write would stamp the
                // same number onto every group's panel. / 每个统计面板取自己所属组的值——写全局值
                // 会把同一个数字盖到所有组的面板上。
                RefreshCustomStatDisplays();
            }
            else
            {
                foreach (Key k in SinglePanel(Kps))
                    SetKpsTotalDisplay(k, "KPS", (PressTimes?.Count ?? 0).ToString());
                foreach (Key k in SinglePanel(Total))
                    SetKpsTotalDisplay(k, "Total", FormatCount(Settings.Data.TotalCount));
            }
            lastKps = -1;
        }

        /// <summary>
        /// Set the display text for a key based on its index and current bindings / 根据按键索引和当前绑定设置显示文本
        /// Special indices: -1=KPS, -2=Total / 特殊索引：-1=KPS，-2=Total
        /// </summary>
        private void UpdateKeyText(Key key, int i)
        {
            if (key == null) return;
            if (i == -1)
            {
                SetKpsTotalDisplay(key, "KPS", "0");
                return;
            }
            if (i == -2)
            {
                SetKpsTotalDisplay(key, "Total", FormatCount(Settings.Data.TotalCount));
                return;
            }
            if (IsFullKeyboard)
            {
                // Full keyboard: text comes straight from the key code (no per-key custom text).
                // 全键盘：文本直接来自按键代码（无每键自定义文本）。
                KeyCode[] keyCodes = GetKeyCode();
                if (keyCodes != null && i >= 0 && i < keyCodes.Length)
                {
                    key.text.text = KeyToString(keyCodes[i]);
                }
            }
            else if (i < FootKeyBase)
            {
                KeyCode[] keyCodes = GetKeyCode();
                string[] keyTexts = GetKeyText();
                if (keyCodes != null && keyTexts != null && i < keyCodes.Length && i < keyTexts.Length)
                {
                    string displayText = !string.IsNullOrEmpty(keyTexts[i]) ? keyTexts[i] : KeyToString(keyCodes[i]);
                    key.text.text = displayText;
                    if (key.value != null)
                        key.value.text = FormatCount(Settings.Data.Count[i]);
                }
            }
            else
            {
                KeyCode[] footKeyCodes = GetFootKeyCode();
                string[] footTexts = GetFootKeyText();
                int footIndex = i - FootKeyBase;
                if (footKeyCodes != null && footIndex >= 0 && footIndex < footKeyCodes.Length)
                {
                    string displayText = footTexts != null && footIndex < footTexts.Length && !string.IsNullOrEmpty(footTexts[footIndex])
                        ? footTexts[footIndex] : KeyToString(footKeyCodes[footIndex]);
                    key.text.text = displayText;
                }
            }
        }

        /// <summary>
        /// Lowest key bottom edge Y for normalized positioning / 归一化定位中最低按键底边的 Y 值
        /// </summary>
        private float GetMinMainKeyOffset()
        {
            float bottomY = GetLayout(Settings.Data.KeyViewerStyle).bottomY;
            return Settings.Data.DownLocation ? bottomY - 200 : bottomY;
        }

        /// <summary>Total width of the main key layout in reference pixels / 主按键布局的总宽度（参考像素）</summary>
        private float GetMainLayoutRightmostOffset() => 428f;

        /// <summary>Topmost key top edge Y for normalized positioning / 归一化定位中最顶部按键顶边的 Y 值</summary>
        private float GetMaxMainKeyOffset()
        {
            return GetLayout(Settings.Data.KeyViewerStyle).frontY + 25;
        }

        /// <summary>Width of the foot key section in reference pixels / 脚键区域的宽度（参考像素）</summary>
        private float GetFootLayoutRightmostOffset(int size)
        {
            int row0Cols = Mathf.Min(size, 8);
            return (row0Cols - 1) * 34 + 30;
        }

        /// <summary>
        /// Reposition main keys based on normalized (0-1) custom position / 基于归一化（0-1）自定义位置重新定位主按键
        /// Maps (0,0) to screen top-left and (1,1) to screen bottom-right / (0,0) 映射到屏幕左上角，(1,1) 映射到屏幕右下角
        /// </summary>
        private void ResetKeyViewerPosition()
        {
            if (Keys == null || !Settings.Data.CustomPositionEnabled) return;
            if (IsFullKeyboard) { RepositionFullKeyboard(); return; }
            if (IsCustomLayout) return; // nodes are their own position / 自定义节点即位置
            Vector2 norm = Settings.Data.MainKeyViewerPosition;
            // Convert normalized (X: 0=left 1=right, Y: 0=top 1=bottom) to reference pixel offsets from bottom-left.
            // X: interpolate so X=0 = left edge at screen left, X=1 = right edge at screen right.
            // Y: subtract min layout offset so Y=1 puts the lowest key's bottom edge at screen bottom.
            float r = GetMainLayoutRightmostOffset();
            float baseX = norm.x * (CanvasWidth - r);
            int remove = Settings.Data.DownLocation ? 200 : 0;
            // Y: lerp so Y=0 = top edge at screen top, Y=1 = bottom edge at screen bottom
            float topBaseY = 1080f - GetMaxMainKeyOffset() + remove;
            float bottomBaseY = -GetMinMainKeyOffset();
            float baseY = Mathf.Lerp(bottomBaseY, topBaseY, 1f - norm.y);
            RepositionMainKeys(GetLayout(Settings.Data.KeyViewerStyle), baseX, baseY);
        }

        private static int FootKeySize(FootKeyviewerStyle style) => style switch
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

        private void ResetFootKeyViewerPosition()
        {
            if (Keys == null || !Settings.Data.CustomPositionEnabled) return;
            if (IsFullKeyboard) return; // full keyboard has no foot keys / 全键盘无脚键
            // Custom layouts have no foot keys EITHER, and their Keys array is only as long as the
            // visible node count — the FootKeyBase(24)-anchored writes below would land on REAL
            // nodes (a 25+ node canvas, e.g. a 108K preset) and teleport them to the fixed foot
            // rows. Reachable with the flag persisted from a fixed layout plus a non-Off foot style,
            // via OnEnable / the master toggle / CheckResolutionChanged. / 自定义布局同样没有脚键，
            // 且其 Keys 数组只有可见节点数那么长——下方按 FootKeyBase(24) 起的写入会落在**真实
            // 节点**上（25 个节点以上的画布，如 108K 预设），把它们瞬移到固定脚键排布。
            // 该标志从固定布局持久化 + 脚键布局非 Off 时即可触发，路径有 OnEnable / 显示总开关 /
            // CheckResolutionChanged。
            if (IsCustomLayout) return; // nodes are their own position / 节点即位置
            Vector2 norm = Settings.Data.FootKeyViewerPosition;
            int size = FootKeySize(Settings.Data.FootKeyViewerStyle);
            if (size == 0) return;
            float r = GetFootLayoutRightmostOffset(size);
            float baseX = norm.x * (CanvasWidth - r);
            // Y: lerp so Y=0 = top edge at screen top, Y=1 = bottom edge at screen bottom
            // Single row (size≤8): top edge at baseY+15. Two rows: top+34, top edge at baseY+49.
            float footTopOffset = size <= 8 ? 15f : 49f;
            float topBaseY = 1080f - footTopOffset;
            float bottomBaseY = 15f; // lowest key center at baseY, bottom edge = baseY-15 → baseY=15
            float baseY = Mathf.Lerp(bottomBaseY, topBaseY, 1f - norm.y);
            int firstRowCount = size <= 8 ? size : 8;
            float yBase = size > 8 ? baseY + 34 : baseY;
            for (int i = FootKeyBase; i < FootKeyBase + size; i++)
            {
                int offset = i - FootKeyBase;
                if (offset < firstRowCount)
                {
                    SetKeyPosition(i, baseX + offset * 34, yBase);
                }
                else
                {
                    int col = offset - firstRowCount;
                    float x = baseX + col * 34 + (firstRowCount - (size - firstRowCount)) * 17;
                    SetKeyPosition(i, x, yBase - 34);
                }
            }
        }

        private int lastScreenWidth, lastScreenHeight;
        private float canvasWidth;

        // Full-keyboard custom-position support: captured home (natural) anchored positions so the
        // whole 108K block can be translated by the normalized offset. / 全键盘自定义位置：记录每个键的自然位置，整体平移。
        private Vector2[] _fkHome;
        private bool _fkHomeValid;

        private float CanvasWidth
        {
            get
            {
                if (lastScreenWidth != Screen.width || lastScreenHeight != Screen.height)
                {
                    canvasWidth = Screen.width * 1080f / Screen.height;
                    lastScreenWidth = Screen.width;
                    lastScreenHeight = Screen.height;
                }
                return canvasWidth;
            }
        }

        /// <summary>
        /// Re-apply CanvasWidth-derived positions when the resolution changes mid-session. Full-KB
        /// KPS/Total, the 108-key block offset, and the custom-position bases are computed only at
        /// build / slider time and would otherwise stay misplaced until the next rebuild.
        /// 会话中途分辨率变化时重新应用由 CanvasWidth 推导的位置。全键盘 KPS/Total、108 键整体
        /// 偏移与自定义位置基点只在构建/滑块操作时计算，不重排会一直错位到下次重建。
        /// </summary>
        private void CheckResolutionChanged()
        {
            if (lastScreenWidth == Screen.width && lastScreenHeight == Screen.height) return;
            canvasWidth = Screen.width * 1080f / Screen.height; // keep CanvasWidth's cache in sync / 同步 CanvasWidth 的缓存
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;
            if (KeyViewerObject == null) return;
            if (IsFullKeyboard)
            {
                // The 108K block's natural slot positions don't depend on the resolution — only the
                // custom-position offset (and the KPS/Total normalized positions) derive from
                // CanvasWidth. Calling RepositionFullKeyboard with the toggle off would drag the
                // whole block to the stored-but-inactive custom offset (e.g. on a startup
                // windowed→fullscreen resolution change).
                // 108 键整块的自然槽位与分辨率无关——只有自定义位置偏移（和 KPS/Total 归一化位置）
                // 依赖 CanvasWidth。开关关闭时调用 RepositionFullKeyboard 会把整块拖到存储的
                // （未启用的）自定义偏移上（例如启动时窗口→全屏的分辨率变化）。
                if (Settings.Data.CustomPositionEnabled)
                    RepositionFullKeyboard();
                ApplyFullKeyboardKpsTotalPosition();
            }
            else if (Settings.Data.CustomPositionEnabled)
            {
                ResetKeyViewerPosition();
                ResetFootKeyViewerPosition();
            }
        }

        private void SetKeyPosition(int keyIndex, float x, float y)
        {
            Key key = keyIndex == -1 ? Kps
                : keyIndex == -2 ? Total
                : (keyIndex >= 0 && keyIndex < Keys.Length ? Keys[keyIndex] : null);
            if (key == null) return;
            ((RectTransform)key.transform).anchoredPosition = new Vector2(x, y);
            // Keep the text wrapper and merged shape rect in sync with the key root / 文本包裹层与合并形状矩形随按键根同步
            if (key.visuals != null)
                ((RectTransform)key.visuals).anchoredPosition = new Vector2(x + key.keySize.x * 0.5f, y);
            if (keyShapeLayer != null && key.shapeSlot >= 0)
                keyShapeLayer.SetRect(key.shapeSlot, x, y - key.keySize.y * 0.5f, key.keySize.x, key.keySize.y);
        }

        /// <summary>Get color setting by numeric index (0-8) for the color picker / 通过数字索引（0-8）获取颜色设置，用于颜色选择器</summary>
        private Color GetColorByIndex(int index)
        {
            return index switch
            {
                0 => Settings.Data.Background,
                1 => Settings.Data.BackgroundClicked,
                2 => Settings.Data.Outline,
                3 => Settings.Data.OutlineClicked,
                4 => Settings.Data.Text,
                5 => Settings.Data.TextClicked,
                6 => Settings.Data.RainColor,
                7 => Settings.Data.RainColor2,
                8 => Settings.Data.RainColor3,
                9 => Settings.Data.GhostRainColor,
                10 => Settings.Data.GhostRainColor2,
                11 => Settings.Data.GhostRainColor3,
                _ => Color.white
            };
        }

        /// <summary>Set color setting by numeric index (0-8) from the color picker / 通过数字索引（0-8）从颜色选择器设置颜色</summary>
        private void SetColorByIndex(int index, Color color)
        {
            switch (index)
            {
                case 0: Settings.Data.Background = color; break;
                case 1: Settings.Data.BackgroundClicked = color; break;
                case 2: Settings.Data.Outline = color; break;
                case 3: Settings.Data.OutlineClicked = color; break;
                case 4: Settings.Data.Text = color; break;
                case 5: Settings.Data.TextClicked = color; break;
                case 6: Settings.Data.RainColor = color; break;
                case 7: Settings.Data.RainColor2 = color; break;
                case 8: Settings.Data.RainColor3 = color; break;
                case 9: Settings.Data.GhostRainColor = color; break;
                case 10: Settings.Data.GhostRainColor2 = color; break;
                case 11: Settings.Data.GhostRainColor3 = color; break;
            }
        }

        private void UpdateAllKeyColors()
        {
            if (Keys == null) return; // overlay disabled — nothing to recolor / 覆盖层未启用，无需重着色
            if (IsFullKeyboard) { ApplyFullKeyboardColors(); return; }
            if (IsCustomLayout) { ApplyCustomAllColors(); return; }
            if (Settings.Data.EnablePerKeyColors)
                ApplyPerKeyColorsToAll();
            else
                ApplyGlobalColorsToAll();
            ApplyKpsTotalColors();
        }

        private void ApplyKpsTotalColors()
        {
            ApplyColorToKey(Kps, Keys.Length);
            ApplyColorToKey(Total, Keys.Length + 1);
        }

        private void ApplyColorToKey(Key k, int pi)
        {
            if (k == null) return;
            // Per-key arrays are sized MaxKeySlots+2; on the full keyboard the KPS/Total slots (105/106)
            // are out of range, so fall through to the dedicated KPS/Total colors instead of crashing.
            // PerKey 数组长度为 MaxKeySlots+2；全键盘的 KPS/Total 槽位（105/106）越界，
            // 应落入下方 KPS/Total 专属颜色分支而不是崩溃。
            if (Settings.Data.EnablePerKeyColors && pi >= 0 && pi < Settings.Data.PerKeyBackground.Length)
            {
                SetShapeColors(k, Settings.Data.PerKeyBackground[pi], Settings.Data.PerKeyOutline[pi]);
                k.text.color = Settings.Data.PerKeyText[pi];
                if (k.value != null) k.value.color = Settings.Data.PerKeyText[pi];
            }
            else if (pi == Keys.Length)
            {
                if (IsFullKeyboard && Settings.Data.EnableFullKeyboardUnifiedColor)
                {
                    SetShapeColors(k, Settings.Data.FullKeyboardBackground, Settings.Data.FullKeyboardOutline);
                    k.text.color = Settings.Data.FullKeyboardText;
                    if (k.value != null) k.value.color = Settings.Data.FullKeyboardText;
                }
                else
                {
                    SetShapeColors(k, Settings.Data.KpsBackground, Settings.Data.KpsOutline);
                    k.text.color = Settings.Data.KpsText;
                    if (k.value != null) k.value.color = Settings.Data.KpsText;
                }
            }
            else if (pi == Keys.Length + 1)
            {
                if (IsFullKeyboard && Settings.Data.EnableFullKeyboardUnifiedColor)
                {
                    SetShapeColors(k, Settings.Data.FullKeyboardBackground, Settings.Data.FullKeyboardOutline);
                    k.text.color = Settings.Data.FullKeyboardText;
                    if (k.value != null) k.value.color = Settings.Data.FullKeyboardText;
                }
                else
                {
                    SetShapeColors(k, Settings.Data.TotalBackground, Settings.Data.TotalOutline);
                    k.text.color = Settings.Data.TotalText;
                    if (k.value != null) k.value.color = Settings.Data.TotalText;
                }
            }
        }

        private void ApplyPerKeyColorsToAll()
        {
            for (int i = 0; i < Keys.Length; i++)
            {
                if (Keys[i] == null) continue;
                SetShapeColors(Keys[i], Settings.Data.PerKeyBackground[i], Settings.Data.PerKeyOutline[i]);
                Keys[i].text.color = Settings.Data.PerKeyText[i];
                if (Keys[i].value != null) Keys[i].value.color = Settings.Data.PerKeyText[i];
                Keys[i].rainColor = Settings.Data.PerKeyRainColor[i];
            }
        }

        private void ApplyGlobalColorsToAll()
        {
            KeyCode[] keyCodes = GetKeyCode();
            for (int i = 0; i < keyCodes.Length && i < Keys.Length; i++)
            {
                if (Keys[i] == null) continue;
                SetShapeColors(Keys[i], Settings.Data.Background, Settings.Data.Outline);
                Keys[i].text.color = Settings.Data.Text;
                if (Keys[i].value != null) Keys[i].value.color = Settings.Data.Text;
                Keys[i].rainColor = rainSystem?.GetRainColor(Keys[i].color) ?? Settings.Data.RainColor;
            }
            KeyCode[] footKeyCodes = GetFootKeyCode();
            if (footKeyCodes == null) return;
            for (int i = 0; i < footKeyCodes.Length; i++)
            {
                int index = i + FootKeyBase;
                if (index >= Keys.Length || Keys[index] == null) continue;
                SetShapeColors(Keys[index], Settings.Data.Background, Settings.Data.Outline);
                Keys[index].text.color = Settings.Data.Text;
                if (Keys[index].value != null) Keys[index].value.color = Settings.Data.Text;
            }
        }

        /// <summary>
        /// Handle main key layout change / 处理主按键布局变化
        /// </summary>
        private void ChangeKeyViewer()
        {
            // ResetKeyViewer destroys every child (foot keys included) and recreates them internally —
            // a second ResetFootKeyViewer here would only destroy and recreate them again.
            // ResetKeyViewer 销毁全部子物体（含脚键）并在内部重建——此处再调一次
            // ResetFootKeyViewer 只会把脚键销毁重建第二遍。
            ResetKeyViewer();
        }

        /// <summary>
        /// Destroy and recreate main keys (for layout/style changes) / 销毁并重建主按键（用于布局/样式变化）
        /// </summary>
        private void ResetKeyViewer()
        {
            SelectedKey = -1;
            // Per-key editor selections point at slots of the old layout — drop them so the Colors /
            // Display tabs don't keep editing a slot the new layout never draws.
            // 每键编辑器的选中项指向旧布局的槽位——清掉，避免颜色/显示标签页继续编辑新布局
            // 根本不绘制的槽位。
            perKeyColorSelected = -1;
            perKeyTextSelected = -1;
            // Rebuilding needs the overlay; with the display off just keep the changed settings —
            // the next EnableKeyViewer builds from them. GUI handlers can fire while disabled.
            // 重建需要覆盖层存在；显示关闭时仅保留已改的设置——下次启用时按新设置构建
            //（GUI 处理器可能在显示关闭时触发）。
            if (KeyViewerObject == null) return;
            if (Keys != null)
            {
                // Custom-image textures are NOT owned by the GameObjects being destroyed below —
                // release them first or every rebuild leaks one GPU texture per image node.
                // / 自定义图片贴图不属于下方将被销毁的 GameObject——先释放，否则每次重建
                // 每个图片节点泄漏一张 GPU 贴图。
                ReleaseCustomTextures();
                // Destroy EVERY key child under the size object (main keys, foot keys, KPS/Total boxes,
                // any leaks) so no stale key survives a layout switch — but keep the shape/text layer
                // objects; their slots are reset via Init and their text roots destroyed below.
                // Relying on the Keys array length alone misses foot keys when switching to the full
                // keyboard (different array size).
                // 销毁 SizeObject 下全部按键子物体（主键/脚键/KPS/Total/任何残留），不依赖数组长度——
                // 切到全键盘时数组长度变化会漏掉脚键。但保留形状/文本层物体：槽位由 Init 重置，文本包裹层在下方销毁。
                rainSystem.ClearActiveDrops(Keys);
                Transform shapeT = keyShapeLayer != null ? keyShapeLayer.transform : null;
                Transform outlineT = keyOutlineLayer != null ? keyOutlineLayer.transform : null;
                Transform rainT = rainLayer != null ? rainLayer.transform : null;
                Transform ghostT = ghostRainLayer != null ? ghostRainLayer.transform : null;
                if (KeyViewerSizeObject != null)
                {
                    var children = KeyViewerSizeObject.transform;
                    for (int c = children.childCount - 1; c >= 0; c--)
                    {
                        Transform child = children.GetChild(c);
                        if (child == shapeT || child == outlineT || child == textLayer || child == rainT || child == ghostT) continue;
                        Object.Destroy(child.gameObject);
                    }
                }
                if (textLayer != null)
                {
                    for (int c = textLayer.childCount - 1; c >= 0; c--)
                        Object.Destroy(textLayer.GetChild(c).gameObject);
                }
                Total = null;
                Kps = null;
            }
            // Array length must match the target layout (40 standard / 105 full keyboard).
            // 数组长度必须匹配目标布局（标准40/全键盘105）。
            Keys = new Key[GetKeyCount()];
            if (keyShapeLayer != null) keyShapeLayer.Init(Keys.Length + CustomStatSlotCount()); // resets slots, bumps Generation / 重置槽位并递增 Generation
            if (rainLayer != null) rainLayer.Init(Keys.Length); // resets per-key rain press scales / 重置每键雨滴按压缩放
            Total = null;
            Kps = null;
            InitializeMainKeys(GetLayout(Settings.Data.KeyViewerStyle));
            // Rebuild foot keys too: ResetKeyViewer now destroys every child (including foot keys)
            // to avoid leaking them when switching to the full keyboard, so they must be recreated here.
            // 同时重建脚键：ResetKeyViewer 现在销毁全部子物体（含脚键）以避免切到全键盘时残留，故需在此重建。
            ResetFootKeyViewer();
            if (Settings.Data.StreamerMode && !IsFullKeyboard)
            {
                SetStatsVisible(false);
            }
            if (Settings.Data.CustomPositionEnabled)
                ResetKeyViewerPosition();
            // Re-apply the per-profile size scale: a profile switch loads a new Settings.Data.Size
            // but KeyViewerSizeObject's localScale is only set at init / from the GUI slider, so it
            // would otherwise keep the previous profile's size.
            // 重新应用每套配置的尺寸缩放：切换配置会载入新的 Settings.Data.Size，
            // 但 SizeObject 的缩放只在初始化/GUI 滑块处赋值，否则会沿用上一套配置的尺寸。
            if (KeyViewerSizeObject != null)
                KeyViewerSizeObject.transform.localScale = new Vector3(Settings.Data.Size, Settings.Data.Size, 1);
            RefreshAllCountDisplay();
        }

        /// <summary>
        /// Destroy and recreate foot keys (for layout/style changes) / 销毁并重建脚键（用于布局/样式变化）
        /// </summary>
        private void ResetFootKeyViewer()
        {
            if (IsFullKeyboard) return; // full keyboard has no foot keys / 全键盘无脚键
            // Custom layouts have no foot keys EITHER — and their Keys array is only as long as
            // the node count, so writing the FootKeyBase(24)-anchored foot slots would index out
            // of bounds (crashed OnGUI when switching a fixed layout → Custom with 0 nodes).
            // / 自定义布局同样没有脚键——而且它的 Keys 数组只有节点数那么长，往
            // FootKeyBase(24) 起的脚键槽位写会越界（从固定布局切到无节点的自定义时
            // OnGUI 崩溃即此）。
            if (IsCustomLayout) return;
            SelectedKey = -1;
            perKeyColorSelected = -1;
            perKeyTextSelected = -1;
            // Same overlay guard as ResetKeyViewer — GUI can fire while the display is off /
            // 与 ResetKeyViewer 相同的覆盖层守卫——GUI 可能在显示关闭时触发
            if (KeyViewerObject == null) return;
            if (Keys != null)
            {
                for (int i = FootKeyBase; i < Keys.Length; i++)
                {
                    var key = Keys[i];
                    if (key == null) continue;
                    foreach (var rain in key.rainList)
                        rainSystem.ReturnRawRain(rain);
                    key.rainList.Clear();
                    // Texts live in the text canvas; hide + reset the slot (a leftover press-animation
                    // scale would render the recreated key's box permanently shrunken) /
                    // 文本在文本画布中；隐藏并重置槽位（残留的按压缩放会让重建后的按键框永久缩小）
                    if (key.visuals != null)
                        Object.Destroy(key.visuals.gameObject);
                    if (keyShapeLayer != null && key.shapeSlot >= 0)
                    {
                        keyShapeLayer.SetVisible(key.shapeSlot, false);
                        keyShapeLayer.SetScale(key.shapeSlot, 1f);
                    }
                    if (key.gameObject != null)
                        Object.Destroy(key.gameObject);
                }
            }
            int footSize = FootKeySize(Settings.Data.FootKeyViewerStyle);
            if (footSize > 0) InitializeFootKeyViewer(footSize);
            if (Settings.Data.CustomPositionEnabled)
                ResetFootKeyViewerPosition();
            RefreshAllCountDisplay();
        }

        /// <summary>
        /// Get the key code array for the current main layout / 获取当前主布局的按键代码数组
        /// </summary>
        private static KeyCode[] GetKeyCode()
        {
            return Settings.Data.KeyViewerStyle switch
            {
                KeyviewerStyle.Key8 => Settings.Data.key8,
                KeyviewerStyle.Key12 => Settings.Data.key12,
                KeyviewerStyle.Key14 => Settings.Data.key14,
                KeyviewerStyle.Key16 => Settings.Data.key16,
                KeyviewerStyle.Key20 => Settings.Data.key20,
                KeyviewerStyle.Key24 => Settings.Data.key24,
                KeyviewerStyle.Key10 => Settings.Data.key10,
                KeyviewerStyle.Full108 => Settings.Data.key108,
                _ => Settings.Data.key16
            };
        }

        /// <summary>
        /// Get the foot key code array for the current foot layout / 获取当前脚键布局的按键代码数组
        /// </summary>
        private static KeyCode[] GetFootKeyCode()
        {
            return Settings.Data.FootKeyViewerStyle switch
            {
                FootKeyviewerStyle.Key2 => Settings.Data.footkey2,
                FootKeyviewerStyle.Key4 => Settings.Data.footkey4,
                FootKeyviewerStyle.Key6 => Settings.Data.footkey6,
                FootKeyviewerStyle.Key8 => Settings.Data.footkey8,
                FootKeyviewerStyle.Key10 => Settings.Data.footkey10,
                FootKeyviewerStyle.Key12 => Settings.Data.footkey12,
                FootKeyviewerStyle.Key14 => Settings.Data.footkey14,
                FootKeyviewerStyle.Key16 => Settings.Data.footkey16,
                _ => new KeyCode[0]
            };
        }

        /// <summary>
        /// Get the ghost key code array for the current main layout / 获取当前主布局的鬼键代码数组
        /// </summary>
        private static KeyCode[] GetGhostKeyCode()
        {
            return Settings.Data.KeyViewerStyle switch
            {
                KeyviewerStyle.Key8 => Settings.Data.GhostKey8,
                KeyviewerStyle.Key10 => Settings.Data.GhostKey10,
                KeyviewerStyle.Key12 => Settings.Data.GhostKey12,
                KeyviewerStyle.Key14 => Settings.Data.GhostKey14,
                KeyviewerStyle.Key16 => Settings.Data.GhostKey16,
                KeyviewerStyle.Key20 => Settings.Data.GhostKey20,
                KeyviewerStyle.Key24 => Settings.Data.GhostKey24,
                KeyviewerStyle.Full108 => new KeyCode[0],
                _ => Settings.Data.GhostKey16
            };
        }

        /// <summary>
        /// Get the custom text labels for the current main layout / 获取当前主布局的自定义文本标签
        /// </summary>
        private static string[] GetKeyText()
        {
            return Settings.Data.KeyViewerStyle switch
            {
                KeyviewerStyle.Key8 => Settings.Data.key8Text,
                KeyviewerStyle.Key12 => Settings.Data.key12Text,
                KeyviewerStyle.Key14 => Settings.Data.key14Text,
                KeyviewerStyle.Key16 => Settings.Data.key16Text,
                KeyviewerStyle.Key20 => Settings.Data.key20Text,
                KeyviewerStyle.Key24 => Settings.Data.key24Text,
                KeyviewerStyle.Key10 => Settings.Data.key10Text,
                _ => Settings.Data.key16Text
            };
        }

        /// <summary>
        /// Get the custom text labels for the current foot key layout / 获取当前脚键布局的自定义文本标签
        /// </summary>
        private static string[] GetFootKeyText()
        {
            return Settings.Data.FootKeyViewerStyle switch
            {
                FootKeyviewerStyle.Key2 => Settings.Data.footkey2Text,
                FootKeyviewerStyle.Key4 => Settings.Data.footkey4Text,
                FootKeyviewerStyle.Key6 => Settings.Data.footkey6Text,
                FootKeyviewerStyle.Key8 => Settings.Data.footkey8Text,
                FootKeyviewerStyle.Key10 => Settings.Data.footkey10Text,
                FootKeyviewerStyle.Key12 => Settings.Data.footkey12Text,
                FootKeyviewerStyle.Key14 => Settings.Data.footkey14Text,
                FootKeyviewerStyle.Key16 => Settings.Data.footkey16Text,
                _ => new string[0]
            };
        }

        /// <summary>
        /// Get the back-row index mapping for the current main layout / 获取当前主布局的后排索引映射
        /// </summary>
        private static byte[] GetBackSequence()
        {
            return Settings.Data.KeyViewerStyle switch
            {
                KeyviewerStyle.Key8 => BackSequence8,
                KeyviewerStyle.Key12 => BackSequence12,
                KeyviewerStyle.Key14 => BackSequence14,
                KeyviewerStyle.Key16 => BackSequence16,
                KeyviewerStyle.Key20 => BackSequence20,
                KeyviewerStyle.Key24 => BackSequence24,
                KeyviewerStyle.Key10 => BackSequence10,
                _ => BackSequence16
            };
        }

        /// <summary>Format count with thousands separator if enabled / 千分位格式化数字</summary>
        private static string FormatCount(int count)
        {
            return Settings.Data.EnableCountFormatting ? count.ToString("N0") : count.ToString();
        }

        /// <summary>Node-aware variant: a custom node with UseCustomCountFormat opts out of the
        /// global setting. A null node (fixed layouts) resolves to the global. / 节点感知变体：
        /// 开启 UseCustomCountFormat 的自定义节点退出全局设置；null 节点（固定布局）取全局。</summary>
        private static string FormatCount(int count, Settings.FmNode node)
        {
            return NodeThousands(node) ? count.ToString("N0") : count.ToString();
        }

        /// <summary>Refresh all key value displays (count or per-key KPS) / 刷新所有按键数值显示（计数或每键 KPS）</summary>
        public void RefreshAllCountDisplay()
        {
            if (Keys == null) return;
            if (IsCustomLayout)
            {
                // Counts live on the nodes; per-key KPS drains each node's own log. /
                // 计数内聚在节点上；每键 KPS 由节点自己的队列驱动。
                RefreshCustomCountDisplays();
                // Stat panels are refreshed from THEIR OWN group, never from the global
                // accumulator: writing TotalCount into every Total panel made editing one
                // group's count overwrite every other group's total. / 统计面板按各自所属组
                // 刷新，绝不使用全局累计值：把 TotalCount 写进每个 Total 面板会让「改一个
                // 组的计数」覆盖其它所有组的总数。
                RefreshCustomStatDisplays();
                return;
            }
            for (int i = 0; i < Keys.Length; i++)
            {
                if (Keys[i] != null && Keys[i].value != null)
                {
                    if (Settings.Data.EnablePerKeyKps)
                        Keys[i].value.text = (keyPressTimes != null && i < keyPressTimes.Length && keyPressTimes[i] != null) ? keyPressTimes[i].Count.ToString() : "0";
                    else
                        Keys[i].value.text = FormatCount(Settings.Data.Count[i]);
                }
            }
            foreach (Key k in SinglePanel(Total))
                SetKpsTotalDisplay(k, "Total", FormatCount(Settings.Data.TotalCount));
        }

        public void AutoAssignRainbowColors()
        {
            int mainCount = Settings.Data.KeyViewerStyle switch
            {
                KeyviewerStyle.Key8 => 8,
                KeyviewerStyle.Key10 => 10,
                KeyviewerStyle.Key12 => 12,
                KeyviewerStyle.Key14 => 14,
                KeyviewerStyle.Key16 => 16,
                KeyviewerStyle.Key20 => 20,
                KeyviewerStyle.Key24 => 24,
                _ => 16
            };
            int footCount = FootKeySize(Settings.Data.FootKeyViewerStyle);
            Settings.Data.EnablePerKeyColors = true;
            int slot = 0;
            void AssignSlot(int i)
            {
                float hue = slot * 0.618033988f;
                hue -= Mathf.Floor(hue);
                float h = hue * 6f;
                int sector = (int)h;
                float f = h - sector;
                float p = 0.9f * (1f - 0.85f);
                float q = 0.9f * (1f - 0.85f * f);
                float t = 0.9f * (1f - 0.85f * (1f - f));
                float r, g, b;
                switch (sector % 6)
                {
                    case 0: r = 0.9f; g = t; b = p; break;
                    case 1: r = q; g = 0.9f; b = p; break;
                    case 2: r = p; g = 0.9f; b = t; break;
                    case 3: r = p; g = q; b = 0.9f; break;
                    case 4: r = t; g = p; b = 0.9f; break;
                    default: r = 0.9f; g = p; b = q; break;
                }
                Color baseColor = new Color(r, g, b);
                float bright = baseColor.grayscale > 0.5f ? 0f : 1f;
                Settings.Data.PerKeyBackground[i] = baseColor;
                Settings.Data.PerKeyBackgroundClicked[i] = Color.Lerp(baseColor, Color.white, 0.5f);
                Settings.Data.PerKeyOutline[i] = baseColor;
                Settings.Data.PerKeyOutlineClicked[i] = Color.Lerp(baseColor, Color.white, 0.7f);
                Settings.Data.PerKeyText[i] = new Color(bright, bright, bright);
                Settings.Data.PerKeyTextClicked[i] = new Color(1f - bright, 1f - bright, 1f - bright);
                Settings.Data.PerKeyRainColor[i] = baseColor;
                slot++;
            }
            for (int i = 0; i < mainCount; i++) AssignSlot(i);
            for (int i = 0; i < footCount; i++) AssignSlot(FootKeyBase + i);
            AssignSlot(MaxKeySlots);
            AssignSlot(MaxKeySlots + 1);
            // ResetKeyViewer recreates foot keys internally — no outer ResetFootKeyViewer
            // (it would only destroy and recreate them a second time).
            // ResetKeyViewer 内部已重建脚键——无需外层 ResetFootKeyViewer(只会二次销毁重建)。
            ResetKeyViewer();
            SaveSettings();
        }
    }
}





