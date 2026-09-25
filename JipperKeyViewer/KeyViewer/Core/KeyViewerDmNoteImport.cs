// DM Note JSON preset import / DM Note JSON 预设导入
// The importer is intentionally independent from Quartz's GPL implementation: it reads the
// documented DmNote field vocabulary, maps it onto FreeMake FmNode data, and creates a NEW
// profile. Graph/knob/embedded-asset payloads are reported and skipped in this first phase.
// 导入器独立实现：只读取 DmNote 的字段词汇，映射到 FreeMake 的 FmNode，并始终创建新 Profile。
// 第一阶段会报告并跳过 graph/knob/嵌入资源，不复制 Quartz 的实现。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

using JipperKeyViewer.KeyViewer.Settings;
using JipperKeyViewer.KeyViewer.Util;

namespace JipperKeyViewer.KeyViewer
{
    public partial class KeyViewer
    {
        private const long MaxDmNotePresetBytes = 64L * 1024L * 1024L;
        private const string DmNotePresetDirectoryName = "DmNotePresets";
        /// <summary>Hard cap on imported elements. A 64 MB JSON can describe hundreds of thousands
        /// of nodes; each FmNode carries several float[4] arrays, so the parse would spike the Mono
        /// heap long before EnsureCustomNodes trims the list back to 112. / 导入元素数硬上限：64MB
        /// JSON 可能描述数十万个节点，每个节点还带多个颜色数组，会在裁剪前就把 Mono 堆打爆。</summary>
        private const int MaxDmNoteElements = 4096;

        /// <summary>Deduplicated warning collector: a preset with 10 000 unsupported panels used to
        /// append 10 000 identical strings, all of which were then joined into the UI message.
        /// 警告去重收集器：曾出现上万个相同警告字符串全部拼进提示文本。</summary>
        internal sealed class DmNoteWarnings
        {
            private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<string> ordered = new List<string>();
            public int Count => ordered.Count;
            public IEnumerable<string> Items => ordered;
            public void Add(string message)
            {
                if (string.IsNullOrEmpty(message) || !seen.Add(message)) return;
                ordered.Add(message);
            }
        }

        /// <summary>Parsed DmNote data before it is turned into a new profile. / 转为新 Profile 前的解析结果。</summary>
        internal sealed class DmNoteImportDocument
        {
            public readonly List<FmNode> Nodes = new List<FmNode>();
            public readonly DmNoteWarnings Warnings = new DmNoteWarnings();
            public int SeenElements;
            public string Tab = "default";
            public bool NoteEnabled = true;
            public float NoteSpeed;
        }

        /// <summary>Profile name free in BOTH the profile list and on disk. MakeUniqueProfileName
        /// only knows the in-memory list, so an orphan file left by an earlier failed import could
        /// still collide and abort the import with "profile target already exists".
        /// Profile 名需同时避开内存列表与磁盘文件：仅查列表时，早前失败导入留下的孤儿文件会撞名。
        /// </summary>
        private string MakeUniqueDmNoteProfileName(string baseName)
        {
            string clean = SanitizeFileName(string.IsNullOrWhiteSpace(baseName) ? "DmNote" : baseName.Trim());
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Settings.ProfileNames != null)
                foreach (string p in Settings.ProfileNames)
                    if (!string.IsNullOrWhiteSpace(p)) used.Add(SanitizeFileName(p));
            if (!used.Contains(clean) && !File.Exists(GetProfilePath(clean))) return clean;
            for (int i = 2; i < 1000; i++)
            {
                string candidate = clean + " (" + i.ToString(CultureInfo.InvariantCulture) + ")";
                if (!used.Contains(candidate) && !File.Exists(GetProfilePath(candidate))) return candidate;
            }
            return clean + " (" + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + ")";
        }

        /// <summary>Files offered by the DM Note import list. / DM Note 导入列表中的文件。</summary>
        internal List<string> ListDmNotePresetFiles()
        {
            try
            {
                string dir = DmNotePresetDirectory;
                if (!Directory.Exists(dir)) return new List<string>();
                return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly)
                    .Where(f => new FileInfo(f).Length <= MaxDmNotePresetBytes)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception e)
            {
                Loader.Warning($"KeyViewer: cannot list DM Note presets: {e.Message}");
                return new List<string>();
            }
        }

        internal string DmNotePresetDirectory => Path.Combine(Loader.ResolveModPath(), DmNotePresetDirectoryName);

        /// <summary>Import one DmNote JSON file as a new FreeMake profile. The current profile is
        /// saved first and is never overwritten. / 将一个 DmNote JSON 导入为新的 FreeMake Profile，
        /// 当前 Profile 会先保存且绝不被覆盖。</summary>
        internal bool ImportDmNotePresetFile(string filePath, out string message)
        {
            message = "";
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                {
                    message = I18n.Tr("dmnote_import_missing");
                    return false;
                }
                long length = new FileInfo(filePath).Length;
                if (length <= 0 || length > MaxDmNotePresetBytes)
                {
                    message = I18n.Tr("dmnote_import_too_large");
                    return false;
                }

                string json = File.ReadAllText(filePath);
                if (!TryParseDmNoteJson(json, out DmNoteImportDocument document, out string parseError))
                {
                    message = I18n.Tr("dmnote_import_invalid") + " " + parseError;
                    return false;
                }
                if (document.Nodes.Count == 0)
                {
                    message = I18n.Tr("dmnote_import_empty");
                    return false;
                }

                SaveCurrentProfile();
                ProfileData imported = JsonConvert.DeserializeObject<ProfileData>(
                    JsonConvert.SerializeObject(Settings.Data, ProfileData.ProfileSerializer),
                    ProfileData.ProfileSerializer);
                if (imported == null) throw new InvalidDataException("profile clone failed");

                string baseName = Path.GetFileNameWithoutExtension(filePath);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "DmNote";
                string profileName = MakeUniqueDmNoteProfileName(baseName);
                string groupId = "g1";
                imported.KeyViewerStyle = KeyviewerStyle.Custom;
                imported.EnableRainEffect = document.NoteEnabled;
                if (document.NoteSpeed > 0f)
                {
                    imported.RainSpeedRow1 = imported.RainSpeedRow2 = imported.RainSpeedRow3 = document.NoteSpeed;
                    imported.GhostRainSpeedRow1 = imported.GhostRainSpeedRow2 = imported.GhostRainSpeedRow3 = document.NoteSpeed;
                }
                // A DmNote preset carries its own per-node counts, but the fixed-layout counter
                // arrays belong to the profile we cloned from. Carrying them over would import an
                // unrelated profile's Total/KPS history into the new layout. / 预设自带每节点计数，
                // 但固定布局的计数数组属于被克隆的配置——继承过来会把无关配置的统计带进新布局。
                imported.Count = new int[MaxKeySlots];
                imported.TotalCount = 0;
                imported.CustomNodes = document.Nodes;
                imported.CustomNodeNextId = document.Nodes.Max(n => n.Id) + 1;
                imported.LayerGroups = new List<FmLayerGroup>
                {
                    new FmLayerGroup { Id = groupId, Name = document.Tab, Visible = true }
                };
                foreach (FmNode node in imported.CustomNodes) node.GroupId = groupId;
                imported.LayerGroupNextId = 2;
                imported.DataVersion = Settings.Version;
                imported.SyncListsToArrays();

                string profilePath = GetProfilePath(profileName);
                string[] previousNames = Settings.ProfileNames == null
                    ? Array.Empty<string>() : (string[])Settings.ProfileNames.Clone();
                string previousCurrent = Settings.CurrentProfile;
                ProfileData previousData = Settings.Data;
                try
                {
                    if (File.Exists(profilePath)) throw new IOException("profile target already exists");
                    Directory.CreateDirectory(ProfileDir);
                    WriteAllTextSafe(profilePath, JsonConvert.SerializeObject(
                        imported, ProfileData.ProfileSerializer));
                    var names = new List<string>(Settings.ProfileNames ?? Array.Empty<string>()) { profileName };
                    Settings.ProfileNames = names.ToArray();
                    // SwitchProfile must still see the OLD current name: it saves the old profile
                    // before loading the new one. Setting CurrentProfile here would make it a no-op
                    // and leave the imported file unactivated.
                    if (!SwitchProfile(profileName)) throw new IOException("profile activation failed");
                }
                catch
                {
                    Settings.ProfileNames = previousNames;
                    Settings.CurrentProfile = previousCurrent;
                    Settings.Data = previousData;
                    try { if (File.Exists(profilePath)) File.Delete(profilePath); } catch { }
                    // SwitchProfile already persisted the NEW meta (CurrentProfile/ProfileNames)
                    // through SaveSettings. Without rewriting it here, settings.json would point at
                    // the profile file we just deleted and the next launch would silently fall back
                    // to a fresh empty profile. / SwitchProfile 已把新 Profile 名写进 meta；不回写
                    // 就会让 settings.json 指向刚删除的文件，下次启动静默回到空配置。
                    try { SaveMetaOnly(); } catch { }
                    throw;
                }

                string warningText = document.Warnings.Count == 0
                    ? "" : " " + string.Join("; ", document.Warnings.Items.Take(3));
                message = string.Format(I18n.Tr("dmnote_import_success"), document.Nodes.Count, profileName)
                    + warningText;
                return true;
            }
            catch (Exception e)
            {
                message = I18n.Tr("dmnote_import_failed") + " " + e.Message;
                Loader.Error("KeyViewer: DM Note import failed: " + e);
                return false;
            }
        }

        /// <summary>Parse the documented DmNote layout shape. Pure and side-effect free so the
        /// Harness can exercise it without Unity scene state. / 解析 DmNote 布局格式；纯函数便于测试。</summary>
        internal static bool TryParseDmNoteJson(string json, out DmNoteImportDocument document, out string error)
        {
            document = null;
            error = "";
            try
            {
                if (string.IsNullOrWhiteSpace(json)) throw new FormatException("empty file");
                JObject root = JObject.Parse(json);
                JToken keyTable = root["keyPositions"] ?? root["positions"];
                JToken statTable = root["statPositions"];
                if (keyTable == null && statTable == null)
                    throw new FormatException("no keyPositions/statPositions object");

                DmNoteImportDocument result = new DmNoteImportDocument
                {
                    Tab = SelectDmNoteTab(root, keyTable, statTable),
                    NoteEnabled = ReadBool(root, "noteEffect", true),
                    NoteSpeed = root["noteSettings"] is JObject noteSettings
                        ? ReadNumber(noteSettings, noteSettings, "speed", 0f) : 0f
                };
                JArray keyElements = SelectDmNoteTabArray(keyTable, result.Tab);
                JArray statElements = SelectDmNoteTabArray(statTable, result.Tab);
                if ((keyElements == null || keyElements.Count == 0)
                    && (statElements == null || statElements.Count == 0))
                    throw new FormatException("selected tab has no key or stat elements");

                JArray names = root["keys"] is JObject keyNames
                    ? keyNames[result.Tab] as JArray : null;
                int nextId = 1;
                AppendDmNoteElements(result, keyElements, names, false, ref nextId);
                // Stat panels normally carry `statType`; only fall back to the parallel names array
                // when it actually covers this tab's stat elements (otherwise the index would bind
                // a panel to an unrelated key name). / 统计面板通常带 statType；仅当 names 数组确实
                // 覆盖本 tab 的统计元素时才作为回退，避免按下标绑到无关键名。
                JArray statNames = names != null && statElements != null && names.Count >= statElements.Count
                    ? names : null;
                AppendDmNoteElements(result, statElements, statNames, true, ref nextId);
                if (root["graphPositions"] != null)
                    result.Warnings.Add(I18n.Tr("dmnote_skip_graph"));
                if (root["knobPositions"] != null)
                    result.Warnings.Add(I18n.Tr("dmnote_skip_knob"));
                if (keyTable == root["positions"] && root["keyPositions"] == null)
                    result.Warnings.Add(I18n.Tr("dmnote_legacy_positions"));
                document = result;
                return true;
            }
            catch (Exception e) when (e is FormatException || e is JsonException || e is InvalidOperationException)
            {
                error = e.Message;
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        private static void AppendDmNoteElements(DmNoteImportDocument result, JArray elements, JArray names,
            bool stat, ref int nextId)
        {
            if (elements == null) return;
            for (int i = 0; i < elements.Count; i++)
            {
                // Refuse oversized documents DURING the walk: building hundreds of thousands of
                // FmNodes first and trimming afterwards is what blows the Mono heap. / 遍历中即拒绝
                // 超量文档：先构造再裁剪正是把托管堆打爆的原因。
                if (result.SeenElements >= MaxDmNoteElements)
                    throw new FormatException("too many elements (max " + MaxDmNoteElements + ")");
                result.SeenElements++;
                if (!(elements[i] is JObject raw)) continue;
                string name = names != null && i < names.Count ? names[i]?.ToString() : "";
                int nodeType = stat ? ResolveDmNoteStatType(raw, name, result.Warnings) : 0;
                if (stat && nodeType == 0) continue; // unsupported KPS avg/max panels
                FmNode node = BuildDmNoteNode(raw, name, nodeType, nextId++, result.Warnings);
                if (node != null) result.Nodes.Add(node);
            }
        }

        private static FmNode BuildDmNoteNode(JObject raw, string keyName, int nodeType, int id,
            DmNoteWarnings warnings)
        {
            JObject position = raw["position"] as JObject ?? raw;
            // A present-but-unparseable geometry field ("width":"abc") used to fall back to the
            // default 60x60, producing a plausible-looking but WRONG node. Treat it like missing
            // geometry and skip the element instead. / 几何字段存在但无法解析（如 "width":"abc"）
            // 时曾回退成默认 60x60，生成看似正常但完全错位的节点；现在按无效几何跳过该元素。
            if (HasUnreadableNumber(raw, position, "dx", "x", "left", "dy", "y", "top",
                    "width", "w", "size", "height", "h"))
            {
                warnings.Add(I18n.Tr("dmnote_skip_geometry"));
                return null;
            }
            float x = ReadNumber(raw, position, "dx", "x", "left", float.NaN);
            float y = ReadNumber(raw, position, "dy", "y", "top", float.NaN);
            float w = ReadNumber(raw, position, "width", "w", "size", nodeType == 0 ? 60f : 100f);
            float h = ReadNumber(raw, position, "height", "h", "size", nodeType == 0 ? 60f : 30f);
            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(w) || !IsFinite(h) || w <= 0f || h <= 0f)
            {
                warnings.Add(I18n.Tr("dmnote_skip_geometry"));
                return null;
            }

            FmNode node = new FmNode
            {
                Id = id,
                NodeType = nodeType,
                X = x,
                Y = y,
                Width = Mathf.Clamp(w, 1f, 2000f),
                Height = Mathf.Clamp(h, 1f, 2000f),
                Depth = Mathf.Max(0, Mathf.RoundToInt(ReadNumber(raw, position, "z", "zIndex", "layer", id))),
                Count = Mathf.Max(0, Mathf.RoundToInt(ReadNumber(raw, position, "count", 0f))),
                KeyBind = nodeType == 0 ? ResolveDmNoteKeyName(keyName) : "",
                GhostKey = ResolveDmNoteKeyName(ReadString(raw, position, "ghostKey", "")),
                CustomText = ReadString(raw, position, "displayText", keyName ?? ""),
                PressedText = nodeType == 0 ? ReadString(raw, position, "quartzPressedText", "") : "",
                CountInTotal = ReadBool(raw, position, "countInTotal", true),
                PerKeyKps = ReadBool(raw, position, "perKeyKps", false),
                Hidden = ReadBool(raw, position, "hidden", false),
                RainEnabled = ReadBool(raw, position, "noteEffectEnabled", true),
                RainRow = Mathf.Clamp((int)ReadNumber(raw, position, "rainRow", "row", 0f), 0, 2),
                RainAlignment = ResolveDmNoteAlign(ReadString(raw, position, "noteAlignment", "center")),
                RainOffsetX = Mathf.Clamp(ReadNumber(raw, position, "noteOffsetX", 0f), -500f, 500f),
                RainOffsetY = Mathf.Clamp(ReadNumber(raw, position, "noteOffsetY", 0f), -500f, 500f),
                CornerRadius = Mathf.Clamp(ReadNumber(raw, position, "borderRadius", 0f), 0f, 100f),
                BorderThickness = Mathf.Clamp(ReadNumber(raw, position, "borderWidth", 0f), 0f, 20f),
                FontSize = Mathf.Max(0f, ReadNumber(raw, position, "fontSize", nodeType == 0 ? 18f : 16f)),
                CountFontSize = Mathf.Max(0f, ReadNumber(raw, position, "counterFontSize", 16f)),
                CountShowWhilePressed = ReadBool(raw, "quartzCounterShowWhilePressed", true),
                UseCustomCountFontStyle = true,
            };

            ApplyDmNoteColors(node, raw, position);
            ApplyDmNoteFontStyles(node, raw, position);
            ApplyDmNoteRain(node, raw, position, warnings);
            JObject counter = (raw["counter"] ?? position["counter"]) as JObject;
            if (counter?["fontSize"] != null)
                node.CountFontSize = Mathf.Clamp(ReadNumber(counter, counter, "fontSize", 16f), 0f, 200f);
            if (counter?["animation"] is JObject animation)
            {
                node.CounterAnimEnabled = ReadBool(animation, "enabled", true);
                node.CounterAnimScale = Mathf.Clamp(ReadNumber(animation, animation, "scale", 1.1f), 0.25f, 4f);
                node.CounterAnimDurationMs = Mathf.Clamp(ReadNumber(animation, animation, "durationMs", 300f), 1f, 5000f);
                if (animation["bezier"] is JArray bezier && bezier.Count == 4)
                {
                    float[] curve = new float[4];
                    for (int i = 0; i < 4; i++)
                    {
                        try { curve[i] = Mathf.Clamp01(bezier[i].Value<float>()); }
                        catch { curve[i] = i == 0 ? 0.25f : i == 1 ? 0.46f : i == 2 ? 0.45f : 0.94f; }
                    }
                    node.CounterAnimBezier = curve;
                }
            }
            float pressScale = ReadNumber(raw, position, "quartzPressScale", 1f);
            if (Math.Abs(pressScale - 1f) > 0.001f)
            {
                node.UseCustomPressAnim = true;
                node.PressAnimScale = Mathf.Clamp(pressScale, 0.25f, 2f);
            }
            if (ReadBool(raw, position, "quartzLabelEnabled", true) == false)
            {
                node.CustomText = "";
                node.PressedText = "";
            }
            if (ReadBool(raw, position, "noteGlowEnabled", false))
                warnings.Add(I18n.Tr("dmnote_skip_glow"));
            if (raw["inactiveImage"] != null || raw["activeImage"] != null
                || position["inactiveImage"] != null || position["activeImage"] != null)
                warnings.Add(I18n.Tr("dmnote_skip_images"));
            return node;
        }

        private static void ApplyDmNoteColors(FmNode node, JObject raw, JObject position)
        {
            bool hasBg = HasAnyToken(raw, position, "backgroundColor", "activeBackgroundColor");
            bool hasOutline = HasAnyToken(raw, position, "borderColor", "activeBorderColor");
            bool hasText = HasAnyToken(raw, position, "fontColor", "activeFontColor");
            Color bg = ReadColor(raw, position, "backgroundColor", Color.white);
            Color activeBg = ReadColor(raw, position, "activeBackgroundColor", bg);
            Color outline = ReadColor(raw, position, "borderColor", Color.white);
            Color activeOutline = ReadColor(raw, position, "activeBorderColor", outline);
            Color text = ReadColor(raw, position, "fontColor", Color.white);
            Color activeText = ReadColor(raw, position, "activeFontColor", text);
            if (ReadBool(raw, position, "idleTransparent", false)) bg.a = 0f;
            if (ReadBool(raw, position, "activeTransparent", false)) activeBg.a = 0f;
            node.UseCustomColor = hasBg || hasOutline || hasText;
            if (hasBg)
            {
                node.Bg = DmColorArray(bg);
                node.BgPressed = DmColorArray(activeBg);
            }
            if (hasOutline)
            {
                node.Outline = DmColorArray(outline);
                node.OutlinePressed = DmColorArray(activeOutline);
            }
            if (hasText)
            {
                node.TextColor = DmColorArray(text);
                node.TextColorPressed = DmColorArray(activeText);
                node.TextOpacity = text.a;
            }
            if (ReadBool(raw, "noteEffectEnabled", true)) node.RainEnabled = true;

            if (TryReadGradient(raw, position, "backgroundGradient", out Color top, out Color bottom))
            {
                node.UseBackgroundGradient = true;
                node.BackgroundGradientTop = DmColorArray(top);
                node.BackgroundGradientBottom = DmColorArray(bottom);
            }
            if (TryReadGradient(raw, position, "borderGradient", out top, out bottom))
            {
                node.UseOutlineGradient = true;
                node.OutlineGradientTop = DmColorArray(top);
                node.OutlineGradientBottom = DmColorArray(bottom);
            }
            if (TryReadGradient(raw, position, "fontGradient", out top, out bottom))
            {
                node.UseTextGradient = true;
                node.TextGradientLeft = DmColorArray(top);
                node.TextGradientRight = DmColorArray(bottom);
            }
            JObject counter = (raw["counter"] ?? position["counter"]) as JObject;
            JObject fill = counter?["fill"] as JObject;
            if (fill != null)
            {
                node.UseCustomCountTextColor = true;
                node.CountTextColor = DmColorArray(ReadColor(fill, fill, "idle", text));
                node.CountTextColorPressed = DmColorArray(ReadColor(fill, fill, "active", activeText));
            }
        }

        private static void ApplyDmNoteFontStyles(FmNode node, JObject raw, JObject position)
        {
            node.FontStyleFlags = ResolveDmNoteFontFlags(raw, position);
            JObject counter = (raw["counter"] ?? position["counter"]) as JObject;
            bool hasCounterStyle = counter != null && HasAnyToken(counter, counter,
                "fontWeight", "fontItalic", "fontUnderline", "fontStrikethrough", "fontLowercase",
                "fontUppercase", "fontSmallCaps");
            node.UseCustomCountFontStyle = hasCounterStyle;
            node.CountFontStyleFlags = hasCounterStyle ? ResolveDmNoteFontFlags(counter, null) : node.FontStyleFlags;
        }

        private static int ResolveDmNoteFontFlags(JObject p, JObject inner)
        {
            int flags = 0;
            float weight = ReadNumber(p, inner, "fontWeight", 400f);
            if (weight >= 600f) flags |= 1;
            if (ReadBool(p, inner, "fontItalic", false)) flags |= 2;
            if (ReadBool(p, inner, "fontUnderline", false)) flags |= 4;
            if (ReadBool(p, inner, "fontStrikethrough", false)) flags |= 64;
            if (ReadBool(p, inner, "fontLowercase", false)) flags |= 8;
            if (ReadBool(p, inner, "fontUppercase", false)) flags |= 16;
            if (ReadBool(p, inner, "fontSmallCaps", false)) flags |= 32;
            return flags;
        }

        private static void ApplyDmNoteRain(FmNode node, JObject raw, JObject position, DmNoteWarnings warnings)
        {
            float width = ReadNumber(raw, position, "noteWidth", 0f);
            if (width > 0f) node.RainWidth = width;
            float height = ReadNumber(raw, position, "rainHeight", 0f);
            if (height > 0f) node.RainHeight = height;
            float speed = ReadNumber(raw, position, "rainSpeed", 0f);
            if (speed > 0f) node.RainSpeed = speed;
            Color top = Color.white;
            Color bottom = Color.white;
            if (TryReadColor(raw, position, "noteColor", out Color rain))
            {
                top = rain;
                bottom = rain;
                node.UseCustomRainColor = true;
            }
            if (TryReadGradient(raw, position, "noteGradient", out Color gradientTop, out Color gradientBottom))
            {
                top = gradientTop;
                bottom = gradientBottom;
                node.UseCustomRainColor = true;
            }
            if (node.UseCustomRainColor)
            {
                float opacity = Mathf.Clamp01(ReadNumber(raw, position, "noteOpacity", 100f) / 100f);
                if (float.IsNaN(opacity)) opacity = 1f;
                top.a *= opacity;
                bottom.a *= opacity;
                node.RainColorTop = DmColorArray(top);
                node.RainColorBottom = DmColorArray(bottom);
            }
            if (raw["noteEnabled"] != null || position["noteEnabled"] != null)
                node.RainEnabled = ReadBool(raw, position, "noteEnabled", true);
            if (raw["quartzNoteShadow"] != null || position["quartzNoteShadow"] != null)
            {
                node.UseCustomRainShadow = true;
                node.RainShadowEnabled = ReadBool(raw, position, "quartzNoteShadow", false);
                node.RainShadowColor = DmColorArray(ReadColor(raw, position, "quartzNoteShadowColor", new Color(0f, 0f, 0f, 0.5f)));
                node.RainShadowOffsetX = Mathf.Clamp(ReadNumber(raw, position, "quartzNoteShadowX", 3f), -64f, 64f);
                node.RainShadowOffsetY = Mathf.Clamp(ReadNumber(raw, position, "quartzNoteShadowY", -3f), -64f, 64f);
            }
            float borderWidth = Mathf.Clamp(ReadNumber(raw, position, "noteBorderWidth", 0f), 0f, 20f);
            if (borderWidth > 0f || raw["noteBorderColor"] != null || position["noteBorderColor"] != null)
            {
                node.UseCustomRainOutline = true;
                node.RainOutlineEnabled = borderWidth > 0f;
                node.RainOutlineWidth = borderWidth;
                Color border = ReadColor(raw, position, "noteBorderColor", Color.white);
                float borderOpacity = Mathf.Clamp01(ReadNumber(raw, position, "noteBorderOpacity", 100f) / 100f);
                if (float.IsNaN(borderOpacity)) borderOpacity = 1f;
                border.a *= borderOpacity;
                node.RainOutlineColor = DmColorArray(border);
            }
            if (raw["noteBorderSide"] != null || position["noteBorderSide"] != null)
            {
                node.UseCustomRainBorderSides = true;
                node.RainBorderSides = ResolveDmNoteSide(ReadString(raw, position, "noteBorderSide", "all"));
            }
            float radius = ReadNumber(raw, position, "noteBorderRadius", 0f);
            if (radius <= 0f) radius = ReadNumber(raw, position, "noteRadius", 0f);
            if (raw["noteBorderRadius"] != null || raw["noteRadius"] != null
                || position["noteBorderRadius"] != null || position["noteRadius"] != null)
            {
                node.UseCustomRainCornerRadius = true;
                node.RainCornerRadius = Mathf.Clamp(radius, 0f, 20f);
            }
            if (raw["ghostNoteColor"] != null || position["ghostNoteColor"] != null)
                warnings.Add(I18n.Tr("dmnote_skip_ghost"));
        }

        /// <summary>Pick the tab to import. The returned name is ALWAYS a tab that really exists in
        /// the position tables (or "default" for a table-less document), so the element lookup and
        /// the `keys[tab]` name array can never drift onto different tabs. The earlier version fell
        /// back to the first array inside SelectDmNoteTabArray while still reading names for the
        /// requested tab, which silently bound every key to the wrong name. Also: `||` binds looser
        /// than `&&`, so a null selectedKeyType used to reach TabExists(stats, null) and throw
        /// ArgumentNullException out of the JObject indexer. / 选中的 tab 名必定真实存在，避免元素与
        /// 键名数组取自不同 tab；并修正 selectedKeyType 缺失时 JObject[null] 抛异常的优先级 bug。</summary>
        private static string SelectDmNoteTab(JObject root, JToken keys, JToken stats)
        {
            string selected = root["selectedKeyType"]?.ToString();
            if (!string.IsNullOrWhiteSpace(selected)
                && (TabExists(keys, selected) || TabExists(stats, selected)))
                return selected;
            string first = FirstTab(keys) ?? FirstTab(stats);
            return string.IsNullOrEmpty(first) ? "default" : first;
        }

        private static string FirstTab(JToken table)
            => table is JObject obj ? obj.Properties().Select(p => p.Name).FirstOrDefault() : null;

        private static bool TabExists(JToken table, string tab)
        {
            if (string.IsNullOrEmpty(tab)) return false;
            return table is JObject obj && obj[tab] is JArray;
        }

        /// <summary>Strict lookup: a missing tab yields null instead of silently borrowing another
        /// tab's elements (the caller treats null as "this table has nothing for the chosen tab").
        /// 严格按 tab 取数组；缺失时返回 null，绝不借用别的 tab 造成元素与键名错位。</summary>
        private static JArray SelectDmNoteTabArray(JToken table, string tab)
        {
            if (table is JArray direct) return direct;
            if (table is JObject obj && !string.IsNullOrEmpty(tab))
                return obj[tab] as JArray;
            return null;
        }

        private static int ResolveDmNoteStatType(JObject raw, string name, DmNoteWarnings warnings)
        {
            JObject position = raw["position"] as JObject ?? raw;
            string type = ReadString(raw, position, "statType", ReadString(raw, position, "type", name)).ToLowerInvariant();
            if (type.Contains("total")) return 2;
            if (type.Contains("kps") && !type.Contains("avg") && !type.Contains("max")) return 1;
            warnings.Add(I18n.Tr("dmnote_skip_stat"));
            return 0;
        }

        private static int ResolveDmNoteAlign(string value)
        {
            if (string.Equals(value, "left", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(value, "right", StringComparison.OrdinalIgnoreCase)) return 2;
            return 1;
        }

        private static string ResolveDmNoteKeyName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int numeric))
            {
                KeyCode numericKey = ResolveDmNumericKey(numeric);
                return numericKey == KeyCode.None ? "" : numericKey.ToString();
            }
            string normalized = new string(value.Trim().Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (normalized.StartsWith("KEY", StringComparison.Ordinal) && normalized.Length > 3)
                normalized = normalized.Substring(3);
            if (normalized.StartsWith("DIGIT", StringComparison.Ordinal) && normalized.Length > 5)
                normalized = normalized.Substring(5);
            if (normalized.StartsWith("NUMPAD", StringComparison.Ordinal) && normalized.Length > 6)
            {
                string pad = normalized.Substring(6);
                if (pad.Length == 1 && pad[0] >= '0' && pad[0] <= '9')
                    return ((KeyCode)((int)KeyCode.Keypad0 + (pad[0] - '0'))).ToString();
                switch (pad)
                {
                    case "ENTER": case "RETURN": return KeyCode.Return.ToString();
                    case "PLUS": case "ADD": return KeyCode.KeypadPlus.ToString();
                    case "MINUS": case "SUBTRACT": return KeyCode.KeypadMinus.ToString();
                    case "MULTIPLY": case "STAR": case "ASTERISK": return KeyCode.KeypadMultiply.ToString();
                    case "DIVIDE": case "SLASH": return KeyCode.KeypadDivide.ToString();
                    case "DELETE": case "DECIMAL": case "PERIOD": case "DOT": case "DEL": return KeyCode.KeypadPeriod.ToString();
                    case "EQUALS": case "EQUAL": return KeyCode.KeypadEquals.ToString();
                }
            }
            if (Enum.TryParse(normalized, true, out KeyCode direct) && direct != KeyCode.None)
                return direct.ToString();
            switch (normalized)
            {
                case "ESC": return KeyCode.Escape.ToString();
                case "RETURN": case "ENTER": return KeyCode.Return.ToString();
                case "SPACE": return KeyCode.Space.ToString();
                case "BACK": return KeyCode.Backspace.ToString();
                case "DEL": return KeyCode.Delete.ToString();
                case "INS": return KeyCode.Insert.ToString();
                case "PGUP": return KeyCode.PageUp.ToString();
                case "PGDN": return KeyCode.PageDown.ToString();
                case "LEFT": return KeyCode.LeftArrow.ToString();
                case "RIGHT": return KeyCode.RightArrow.ToString();
                case "UP": return KeyCode.UpArrow.ToString();
                case "DOWN": return KeyCode.DownArrow.ToString();
                case "DOT": case "PERIOD": return KeyCode.Period.ToString();
                case "FORWARDSLASH": case "SLASH": return KeyCode.Slash.ToString();
                case "LCONTROL": case "LEFTCONTROL": case "LEFTCTRL": case "CTRL": case "CONTROL": case "LCTRL":
                    return KeyCode.LeftControl.ToString();
                case "RCONTROL": case "RIGHTCONTROL": case "RIGHTCTRL": case "RCTRL": case "HANJA":
                    return KeyCode.RightControl.ToString();
                case "LALT": case "LEFTALT": return KeyCode.LeftAlt.ToString();
                case "RALT": case "RIGHTALT": case "ALTGR": case "HANGUL": return KeyCode.RightAlt.ToString();
                case "PRINTSCREEN": case "PRTSC": case "PRTSCR": case "SYSREQ": return KeyCode.Print.ToString();
                case "CONTEXTMENU": return KeyCode.Menu.ToString();
                case "CAPSLOCK": return KeyCode.CapsLock.ToString();
                case "COMMA": return KeyCode.Comma.ToString();
                case "PLUS": return KeyCode.Plus.ToString();
                case "MINUS": return KeyCode.Minus.ToString();
                case "EQUAL": case "EQUALS": return KeyCode.Equals.ToString();
                case "SEMICOLON": return KeyCode.Semicolon.ToString();
                case "QUOTE": return KeyCode.Quote.ToString();
                case "BACKQUOTE": case "SECTION": return KeyCode.BackQuote.ToString();
                case "SQUAREBRACKETOPEN": case "OPENBRACKET": case "LBRACKET": return KeyCode.LeftBracket.ToString();
                case "SQUAREBRACKETCLOSE": case "CLOSEBRACKET": case "RBRACKET": return KeyCode.RightBracket.ToString();
                case "BACKSLASH": return KeyCode.Backslash.ToString();
                default: return "";
            }
        }

        /// <summary>Common Windows virtual-key numbers used by DmNote's numeric key ids. / DmNote
        /// 数字键名使用的常见 Windows 虚拟键码映射，独立于 Quartz 的实现。</summary>
        private static KeyCode ResolveDmNumericKey(int value)
        {
            if (value >= 0x30 && value <= 0x39) return (KeyCode)((int)KeyCode.Alpha0 + value - 0x30);
            if (value >= 0x41 && value <= 0x5A) return (KeyCode)((int)KeyCode.A + value - 0x41);
            if (value >= 0x60 && value <= 0x69) return (KeyCode)((int)KeyCode.Keypad0 + value - 0x60);
            if (value >= 0x70 && value <= 0x7E) return (KeyCode)((int)KeyCode.F1 + value - 0x70);
            switch (value)
            {
                case 0x08: return KeyCode.Backspace;
                case 0x09: return KeyCode.Tab;
                case 0x0D: return KeyCode.Return;
                case 0x10: return KeyCode.LeftShift;
                case 0x11: return KeyCode.LeftControl;
                case 0x12: return KeyCode.LeftAlt;
                case 0x13: return KeyCode.Pause;
                case 0x15: case 0xA5: return KeyCode.RightAlt;
                case 0x19: case 0xA3: return KeyCode.RightControl;
                case 0x14: return KeyCode.CapsLock;
                case 0x1B: return KeyCode.Escape;
                case 0x20: return KeyCode.Space;
                case 0x21: return KeyCode.PageUp;
                case 0x22: return KeyCode.PageDown;
                case 0x23: return KeyCode.End;
                case 0x24: return KeyCode.Home;
                case 0x25: return KeyCode.LeftArrow;
                case 0x26: return KeyCode.UpArrow;
                case 0x27: return KeyCode.RightArrow;
                case 0x28: return KeyCode.DownArrow;
                case 0x2C: return KeyCode.Print;
                case 0x2D: return KeyCode.Insert;
                case 0x2E: return KeyCode.Delete;
                case 0x5B: return KeyCode.LeftWindows;
                case 0x5C: return KeyCode.RightWindows;
                case 0x5D: return KeyCode.Menu;
                case 0x6A: return KeyCode.KeypadMultiply;
                case 0x6B: return KeyCode.KeypadPlus;
                case 0x6D: return KeyCode.KeypadMinus;
                case 0x6E: return KeyCode.KeypadPeriod;
                case 0x6F: return KeyCode.KeypadDivide;
                case 0x90: return KeyCode.Numlock;
                case 0x91: return KeyCode.ScrollLock;
                case 0xA0: return KeyCode.LeftShift;
                case 0xA1: return KeyCode.RightShift;
                case 0xA2: return KeyCode.LeftControl;
                case 0xA4: return KeyCode.LeftAlt;
                case 0xBA: return KeyCode.Semicolon;
                case 0xBB: return KeyCode.Equals;
                case 0xBC: return KeyCode.Comma;
                case 0xBD: return KeyCode.Minus;
                case 0xBE: return KeyCode.Period;
                case 0xBF: return KeyCode.Slash;
                case 0xC0: return KeyCode.BackQuote;
                case 0xDB: return KeyCode.LeftBracket;
                case 0xDC: return KeyCode.Backslash;
                case 0xDD: return KeyCode.RightBracket;
                case 0xDE: return KeyCode.Quote;
                default: return KeyCode.None;
            }
        }

        private static float[] DmColorArray(Color c) => new[] { c.r, c.g, c.b, c.a };

        private static bool TryReadGradient(JObject p, string name, out Color top, out Color bottom)
            => TryReadGradient(p, null, name, out top, out bottom);

        private static bool TryReadGradient(JObject outer, JObject inner, string name, out Color top, out Color bottom)
        {
            top = bottom = Color.white;
            if (!((outer?[name] ?? inner?[name]) is JObject gradient)) return false;
            top = ReadColor(gradient, gradient, "top", Color.white);
            bottom = ReadColor(gradient, gradient, "bottom", top);
            return true;
        }

        private static Color ReadColor(JObject outer, JObject inner, string name, Color fallback)
        {
            JToken token = outer?[name] ?? inner?[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token is JObject obj)
            {
                if (obj["color"] != null) return ParseColorToken(obj["color"], fallback);
                if (obj["value"] != null) return ParseColorToken(obj["value"], fallback);
                return ReadColor(obj, obj, "fill", fallback);
            }
            return ParseColorToken(token, fallback);
        }

        private static bool TryReadColor(JObject p, string name, out Color color)
            => TryReadColor(p, null, name, out color);

        private static bool TryReadColor(JObject outer, JObject inner, string name, out Color color)
        {
            color = Color.white;
            JToken token = outer?[name] ?? inner?[name];
            if (token == null) return false;
            color = ParseColorToken(token, Color.white);
            return true;
        }

        private static Color ParseColorToken(JToken token, Color fallback)
        {
            if (token is JObject obj)
            {
                if (obj["color"] != null) return ParseColorToken(obj["color"], fallback);
                if (obj["value"] != null) return ParseColorToken(obj["value"], fallback);
                return fallback;
            }
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                float v = token.Value<float>();
                if (float.IsNaN(v) || float.IsInfinity(v)) return fallback;
                return new Color(Mathf.Clamp01(v), Mathf.Clamp01(v), Mathf.Clamp01(v), 1f);
            }
            string text = token.ToString().Trim();
            if (text.StartsWith("#", StringComparison.Ordinal)
                && TryParseHexColor(text, out Color html)) return html;
            if (text.StartsWith("rgba", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                string inner = text.Substring(text.IndexOf('(') + 1).TrimEnd(')');
                string[] parts = inner.Split(',');
                if (parts.Length >= 3
                    && float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float r)
                    && float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float g)
                    && float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float b))
                {
                    float a = parts.Length > 3 && float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed) ? parsed : 1f;
                    // rgb()/rgba() components are 0-255 (or percentages); without clamping,
                    // rgba(300,0,0,2) produced an out-of-gamut colour that reached the mesh vertices.
                    // rgb()/rgba() 分量为 0-255，不钳制会让 rgba(300,0,0,2) 产生超范围颜色写进顶点色。
                    return new Color(Mathf.Clamp01(r / 255f), Mathf.Clamp01(g / 255f), Mathf.Clamp01(b / 255f),
                        Mathf.Clamp01(a));
                }
            }
            return fallback;
        }

        private static bool TryParseHexColor(string text, out Color color)
        {
            color = Color.white;
            string hex = text.TrimStart('#');
            if (hex.Length == 3 || hex.Length == 4)
            {
                string expanded = "";
                for (int i = 0; i < hex.Length; i++) expanded += hex[i].ToString() + hex[i];
                hex = expanded;
            }
            if (hex.Length != 6 && hex.Length != 8) return false;
            if (!int.TryParse(hex.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r)) return false;
            if (!int.TryParse(hex.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g)) return false;
            if (!int.TryParse(hex.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b)) return false;
            int a = 255;
            if (hex.Length == 8
                && !int.TryParse(hex.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out a)) return false;
            color = new Color(r / 255f, g / 255f, b / 255f, a / 255f);
            return true;
        }

        /// <summary>Read the first present alias from outer/inner, else the last argument (default).
        /// The args are "alias, alias, ..., default" — EVERY alias must be probed, not just the even
        /// indexes: a step-of-two walk silently dropped `x`, `w`, `row` and `zIndex`, which are real
        /// DmNote field names. / 依次探测所有别名，最后一个参数才是默认值；早期实现按步长 2 遍历，
        /// 会静默丢掉 x / w / row / zIndex 这些 DmNote 原生字段名。</summary>
        private static float ReadNumber(JObject outer, JObject inner, params object[] namesAndDefault)
        {
            int last = namesAndDefault.Length - 1;
            for (int i = 0; i < last; i++)
            {
                string name = namesAndDefault[i] as string;
                if (string.IsNullOrEmpty(name)) continue;
                JToken token = outer?[name] ?? inner?[name];
                if (token == null || token.Type == JTokenType.Null) continue;
                try
                {
                    float value = token.Value<float>();
                    if (!float.IsNaN(value) && !float.IsInfinity(value)) return value;
                }
                catch { }
            }
            return last < 0 ? 0f : Convert.ToSingle(namesAndDefault[last], CultureInfo.InvariantCulture);
        }

        /// <summary>True when one of the named tokens EXISTS but cannot be read as a finite float.
        /// A malformed value must not silently fall back to a default. / 命名字段存在但无法读成有限
        /// 浮点数时返回 true：坏值不能悄悄回退成默认值。</summary>
        private static bool HasUnreadableNumber(JObject outer, JObject inner, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                JToken token = outer?[names[i]] ?? inner?[names[i]];
                if (token == null || token.Type == JTokenType.Null) continue;
                float value;
                try { value = token.Value<float>(); }
                catch { return true; }
                if (float.IsNaN(value) || float.IsInfinity(value)) return true;
            }
            return false;
        }

        private static bool ReadBool(JObject p, string name, bool fallback)
            => ReadBool(p, null, name, fallback);

        private static bool ReadBool(JObject outer, JObject inner, string name, bool fallback)
        {
            JToken token = outer?[name] ?? inner?[name];
            if (token == null || token.Type == JTokenType.Null) return fallback;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) return token.Value<float>() != 0f;
            return bool.TryParse(token.ToString(), out bool parsed) ? parsed : fallback;
        }

        private static bool HasAnyToken(JObject outer, JObject inner, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
                if (outer?[names[i]] != null || inner?[names[i]] != null) return true;
            return false;
        }

        private static int ResolveDmNoteSide(string value)
        {
            if (string.Equals(value, "vertical", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(value, "horizontal", StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }

        private static string ReadString(JObject p, string name, string fallback)
            => ReadString(p, null, name, fallback);

        private static string ReadString(JObject outer, JObject inner, string name, string fallback)
        {
            JToken token = outer?[name] ?? inner?[name];
            return token == null || token.Type == JTokenType.Null ? fallback : token.ToString();
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
