// DmNote CSS companion parsing / DmNote CSS 伴随文件解析
//
// WHY THIS FILE EXISTS
// A DmNote preset stores almost none of its appearance in the JSON. In the real preset on the
// user's disk every visual field is literally `null`:
//
//     "backgroundColor": null, "activeBackgroundColor": null,
//     "borderColor": null,   "borderWidth": null,
//     "borderRadius": null,   "fontColor": null, "activeFontColor": null,
//     "fontSize": null,       "fontWeight": null,
//     "className": "",        "inactiveImage": "", "activeImage": ""
//
// and the whole theme lives in `customCSS.content` (or a sibling .css file), keyed on
// `[data-state="inactive"]` / `[data-state="active"]`. The importer read the JSON only, so an
// imported layout came out as bare coloured squares: wrong colours, square corners, no glow,
// wrong font — and, worst of all, NO ICONS AT ALL, because the clear/crown/fail/star glyphs
// exist solely as `::before { mask-image: url(data:image/svg+xml;base64,…) }` rules selected by
// the very `className` the JSON leaves empty.
//
// SCOPE — a deliberate subset, not a CSS engine. Supported: rule blocks, comma lists,
// `:where()` / `:is()` expansion, attribute selectors, class selectors, descendant combinators,
// custom properties with `var()` and fallbacks, `!important` (stripped — there is no cascade to
// honour, later rules simply win), @-keyframes/@media/@supports (skipped, reported).
// Explicitly NOT supported, and reported rather than silently dropped: backdrop-filter,
// letter-spacing, transition/animation, and JS plugins.
//
// ICON LIMITATION — the glyphs are SVG. This project loads images through
// `ImageConversion.LoadImage`, which decodes PNG and JPEG only; there is no rasteriser here and
// writing one for a cosmetic icon is not a trade worth making. The parser therefore EXTRACTS the
// SVGs next to the preset and reports that a PNG conversion is required; if a converted PNG of the
// same name already exists it is wired to the node automatically.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace JipperKeyViewer.KeyViewer
{
    /// <summary>One parsed rule, already decomposed into the parts this importer can match.
    /// 单条已解析规则，分解为本导入器可匹配的部分。</summary>
    internal sealed class DmNoteCssRule
    {
        public string State;                 // "inactive" / "active" / null (both) / 状态
        public bool Counter;                 // inside a .counter block / 位于 .counter 块内
        public string Emphasis;              // [data-counter-emphasis="…"] / 计数强调变体
        public bool Before;                  // a ::before pseudo-element / 伪元素
        public readonly List<string> Classes = new List<string>();
        /// <summary>Not readonly: ParseBlock shares one declaration map across every selector the
        /// block expands to, so a rule is filled in after construction. / 非只读：ParseBlock 在同
        /// 一个块展开出的全部选择器间共享一份声明表，故规则在构造后才填入。
        /// </summary>
        public Dictionary<string, string> Decls =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int Order;                    // source order, later wins / 源码顺序，后者胜
    }

    /// <summary>Resolved appearance for one DmNote state, or for its counter.
    /// 解析后的某一状态（或其计数）外观。</summary>
    internal sealed class DmNoteCssScope
    {
        public readonly Dictionary<string, string> Decls =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Has(string name) => Decls.ContainsKey(name);
        public string Get(string name, string fallback = null)
            => Decls.TryGetValue(name, out string v) ? v : fallback;
    }

    /// <summary>Everything the importer needs out of a DmNote stylesheet.
    /// 从 DmNote 样式表中取出的全部内容。</summary>
    internal sealed class DmNoteCssTheme
    {
        public bool HasAny;
        public DmNoteCssScope Idle = new DmNoteCssScope();
        public DmNoteCssScope Active = new DmNoteCssScope();
        public DmNoteCssScope CounterIdle = new DmNoteCssScope();
        public DmNoteCssScope CounterActive = new DmNoteCssScope();
        public DmNoteCssScope CounterEmphasisIdle = new DmNoteCssScope();
        public DmNoteCssScope CounterEmphasisActive = new DmNoteCssScope();
        /// <summary>Per-CLASS scopes. A rule whose selector names a class (`.clear`, `.fail`, …)
        /// describes only nodes carrying that className — in the shipped themes those rules exist
        /// purely to hide the label and paint a logo. Merging them into the global scope would
        /// apply `color: transparent` to EVERY key, which is the opposite of what the theme says.
        /// 按类作用域。带类选择器的规则只描述带该 className 的节点——现成主题里这类规则只用于
        /// 隐藏文字并绘制 logo；并入全局作用域会让 `color: transparent` 作用于**每个**按键，
        /// 与主题意图完全相反。
        /// </summary>
        public readonly Dictionary<string, DmNoteCssScope> ClassIdle =
            new Dictionary<string, DmNoteCssScope>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, DmNoteCssScope> ClassActive =
            new Dictionary<string, DmNoteCssScope>(StringComparer.OrdinalIgnoreCase);
        /// <summary>class name → the base64 SVG data URI from its ::before mask-image.
        /// 类名 → 其 ::before mask-image 中的 base64 SVG data URI。</summary>
        public readonly Dictionary<string, string> IconSvg =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>class name → --logo-color, when the stylesheet overrides it. / 类名 → --logo-color。</summary>
        public readonly Dictionary<string, string> LogoColor =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Warnings = new List<string>();
    }

    internal static class DmNoteCss
    {
        /// <summary>Parse a stylesheet. Never throws — a broken theme must degrade the import,
        /// not fail it. 解析样式表。绝不抛出：坏主题应降级导入而非使其失败。</summary>
        public static DmNoteCssTheme Parse(string css)
        {
            var theme = new DmNoteCssTheme();
            if (string.IsNullOrWhiteSpace(css)) return theme;
            try
            {
                var rules = new List<DmNoteCssRule>();
                foreach (string block in SplitBlocks(StripComments(css), theme)) ParseBlock(block, rules, theme);
                rules.Sort((a, b) => a.Order.CompareTo(b.Order));
                foreach (DmNoteCssRule rule in rules) Apply(rule, theme);
                ResolveAll(theme);
                theme.HasAny = theme.Idle.Decls.Count > 0 || theme.Active.Decls.Count > 0
                    || theme.CounterIdle.Decls.Count > 0 || theme.CounterActive.Decls.Count > 0;
            }
            catch (Exception e)
            {
                theme.Warnings.Add("css parse failed: " + e.Message);
            }
            return theme;
        }

        // ---------- lexing / 词法 ----------

        private static string StripComments(string css)
        {
            var sb = new StringBuilder(css.Length);
            int i = 0;
            while (i < css.Length)
            {
                if (css[i] == '/' && i + 1 < css.Length && css[i + 1] == '*')
                {
                    int end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) break;
                    i = end + 2;
                    continue;
                }
                sb.Append(css[i]);
                i++;
            }
            return sb.ToString();
        }

        /// <summary>Split into `selector { … }` chunks, skipping at-rule preludes but still
        /// yielding their bodies as ordinary chunks — an @media wrapper is a plain scope in every
        /// preset that uses one, and treating its contents as unconditional matches that.
        /// 切成 `选择器 { … }` 块；跳过 at-rule 前导但仍产出其内容——用到 @media 的预设里它就是
        /// 普通作用域，把其内容当作无条件规则处理与实际用法一致。
        /// </summary>
        private static IEnumerable<string> SplitBlocks(string css, DmNoteCssTheme theme)
        {
            var results = new List<string>();
            int i = 0;
            var selector = new StringBuilder();
            while (i < css.Length)
            {
                char c = css[i];
                if (c == '{')
                {
                    int depth = 1;
                    int j = i + 1;
                    while (j < css.Length && depth > 0)
                    {
                        if (css[j] == '{') depth++;
                        else if (css[j] == '}') depth--;
                        if (depth > 0) j++;
                    }
                    if (depth != 0) { theme.Warnings.Add("css: unbalanced braces"); break; }
                    string sel = selector.ToString().Trim();
                    string body = css.Substring(i + 1, j - i - 1);
                    if (sel.StartsWith("@"))
                    {
                        if (sel.StartsWith("@keyframes", StringComparison.OrdinalIgnoreCase))
                            theme.Warnings.Add("css: @keyframes not animated");
                        else if (sel.StartsWith("@media", StringComparison.OrdinalIgnoreCase)
                              || sel.StartsWith("@supports", StringComparison.OrdinalIgnoreCase))
                            theme.Warnings.Add("css: conditional rule applied unconditionally");
                        else theme.Warnings.Add("css: at-rule ignored (" + sel.Split(' ')[0] + ")");
                    }
                    else results.Add(sel + "{" + body + "}");
                    selector.Clear();
                    i = j + 1;
                    continue;
                }
                if (c == '}') { selector.Clear(); i++; continue; }
                selector.Append(c);
                i++;
            }
            return results;
        }

        private static void ParseBlock(string block, List<DmNoteCssRule> rules, DmNoteCssTheme theme)
        {
            int brace = block.IndexOf('{');
            if (brace < 0) return;
            string selectorList = block.Substring(0, brace).Trim();
            string body = block.Substring(brace + 1, block.Length - brace - 2);
            var decls = ParseDeclarations(body);
            if (decls.Count == 0) return;
            foreach (string sel in ExpandSelectorList(selectorList, theme))
            {
                var rule = new DmNoteCssRule { Decls = decls, Order = rules.Count };
                if (!DescribeSelector(sel, rule)) continue;
                rules.Add(rule);
            }
        }

        private static Dictionary<string, string> ParseDeclarations(string body)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string piece in SplitTopLevel(body, ';'))
            {
                int colon = piece.IndexOf(':');
                if (colon <= 0) continue;
                string name = piece.Substring(0, colon).Trim();
                string value = piece.Substring(colon + 1).Trim();
                if (name.Length == 0 || value.Length == 0) continue;
                // `!important` has no meaning here: there is no cascade, later rules already win.
                // `!important` 在此无意义：不存在层叠，后出现的规则本就胜出。
                int bang = value.LastIndexOf("!important", StringComparison.OrdinalIgnoreCase);
                if (bang >= 0) value = value.Substring(0, bang).Trim();
                if (value.Length == 0) continue;
                result[name] = value;
            }
            return result;
        }

        private static IEnumerable<string> SplitTopLevel(string s, char sep)
        {
            int depth = 0;
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                if (c == sep && depth == 0) { yield return sb.ToString(); sb.Clear(); continue; }
                sb.Append(c);
            }
            if (sb.Length > 0) yield return sb.ToString();
        }

        /// <summary>Flatten a selector list, expanding :where()/:is() into their alternatives.
        /// 展开选择器列表，把 :where()/:is() 拆成各自的分支。</summary>
        private static List<string> ExpandSelectorList(string list, DmNoteCssTheme theme)
        {
            var current = new List<string>();
            foreach (string part in SplitTopLevel(list, ',')) current.Add(part.Trim());

            // Expand any functional pseudo-class by breadth-first substitution: one match becomes
            // N matches, each with that pseudo replaced by its arguments (or dropped for :not, which
            // cannot be expressed as a positive match here).
            // 广度优先展开函数式伪类：一个匹配变 N 个，各用其参数替换（或对 :not 直接丢弃，
            // 因为它无法在这里表达为正向匹配）。
            for (int pass = 0; pass < 6 && current.Count > 0; pass++)
            {
                var next = new List<string>();
                bool changed = false;
                foreach (string sel in current)
                {
                    int open = sel.IndexOf(':');
                    while (open >= 0 && open + 1 < sel.Length)
                    {
                        int nameStart = open + 1;
                        int paren = sel.IndexOf('(', nameStart);
                        if (paren < 0) break;
                        string fn = sel.Substring(nameStart, paren - nameStart).Trim().ToLowerInvariant();
                        int close = MatchParen(sel, paren);
                        if (close < 0) break;
                        string args = sel.Substring(paren + 1, close - paren - 1);
                        if (fn == "not") { changed = true; next.Add(sel.Remove(open, close - open + 1)); break; }
                        var alts = new List<string>();
                        foreach (string a in SplitTopLevel(args, ',')) alts.Add(a.Trim());
                        if (alts.Count <= 1) { open = sel.IndexOf(':', close); continue; }
                        changed = true;
                        foreach (string a in alts)
                            next.Add(sel.Substring(0, open) + a + sel.Substring(close + 1));
                        break;
                    }
                    if (!changed) next.Add(sel);
                }
                current = next;
                if (!changed) break;
            }
            return current;
        }

        private static int MatchParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') { depth--; if (depth == 0) return i; }
            }
            return -1;
        }

        /// <summary>Decompose one compound selector. Returns false when it carries nothing this
        /// importer can act on (hover/focus/nth-child, ::after, …). 分解单条复合选择器；不含本导入器
        /// 可响应的部分时返回 false。</summary>
        private static bool DescribeSelector(string sel, DmNoteCssRule rule)
        {
            sel = sel.Trim();
            if (sel.Length == 0) return false;
            if (sel.IndexOf(">", StringComparison.Ordinal) >= 0) return false;

            foreach (string token in sel.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = token;
                if (t.EndsWith("::before", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Before = true;
                    t = t.Substring(0, t.Length - 8);
                }
                else if (t.EndsWith(":before", StringComparison.OrdinalIgnoreCase))
                {
                    rule.Before = true;
                    t = t.Substring(0, t.Length - 7);
                }
                else if (t.IndexOf("::", StringComparison.Ordinal) >= 0
                      || t.IndexOf(":hover", StringComparison.OrdinalIgnoreCase) >= 0
                      || t.IndexOf(":focus", StringComparison.OrdinalIgnoreCase) >= 0
                      || t.IndexOf(":active", StringComparison.OrdinalIgnoreCase) >= 0
                      || t.IndexOf(":nth-", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;   // pseudo-states/animation steps are not importable
                }

                foreach (string attr in ExtractAttributes(t))
                {
                    // The yielded text is the WHOLE attribute (`data-state="inactive"`), not its
                    // name — comparing it to "data-state" never matches, which silently dropped
                    // every [data-state] rule and made idle and active render identically.
                    // 吐出的文本是**整个属性**（data-state="inactive"）而非属性名——拿它与
                    // "data-state" 比较永远不相等，于是每条 [data-state] 规则都被静默丢弃，
                    // 常态与按下被渲染成完全相同的样子。
                    int eq = attr.IndexOf('=');
                    string attrName = eq < 0 ? attr : attr.Substring(0, eq);
                    string attrValue = eq < 0 ? null : Unquote(attr.Substring(eq + 1));
                    if (attrName.Equals("data-state", StringComparison.OrdinalIgnoreCase))
                        rule.State = attrValue;
                    else if (attrName.Equals("data-counter-emphasis", StringComparison.OrdinalIgnoreCase))
                        rule.Emphasis = attrValue;
                }
                foreach (string cls in ExtractClasses(t)) rule.Classes.Add(cls);
            }
            // A rule carrying no class and no state and no counter/emphasis cannot be attributed to
            // any node, and applying it everywhere would colour unrelated layouts.
            // 既无类、无状态、也非 counter/emphasis 的规则无法归属到任何节点；全局套用会给无关
            // 布局上色。
            if (rule.State == null && rule.Classes.Count == 0 && !rule.Counter && rule.Emphasis == null)
                return false;
            foreach (string cls in rule.Classes)
                if (cls.Equals("counter", StringComparison.OrdinalIgnoreCase)) rule.Counter = true;
            return true;
        }

        private static IEnumerable<string> ExtractAttributes(string token)
        {
            int i = 0;
            while ((i = token.IndexOf('[', i)) >= 0)
            {
                int close = token.IndexOf(']', i);
                if (close < 0) yield break;
                yield return token.Substring(i + 1, close - i - 1).Trim();
                i = close + 1;
            }
        }

        private static IEnumerable<string> ExtractClasses(string token)
        {
            int i = 0;
            while ((i = token.IndexOf('.', i)) >= 0)
            {
                int j = i + 1;
                while (j < token.Length && (char.IsLetterOrDigit(token[j]) || token[j] == '-' || token[j] == '_')) j++;
                if (j > i + 1) yield return token.Substring(i + 1, j - i - 1);
                i = j;
            }
        }

        private static string Unquote(string s)
        {
            s = s.Trim();
            if (s.Length >= 2 && ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                return s.Substring(1, s.Length - 2);
            return s;
        }

        // ---------- cascade / 层叠 ----------

        private static void Apply(DmNoteCssRule rule, DmNoteCssTheme theme)
        {
            // ::before rules only ever carry mask-image / logo colour; the glyph itself.
            // ::before 规则只承载 mask-image / logo 颜色，即图形本身。
            if (rule.Before)
            {
                foreach (string cls in rule.Classes)
                {
                    if (rule.Decls.TryGetValue("mask-image", out string uri)) theme.IconSvg[cls] = uri;
                    if (rule.Decls.TryGetValue("--logo-color", out string logo)) theme.LogoColor[cls] = logo;
                }
                return;
            }

            if (rule.Counter)
            {
                bool emphasis = !string.IsNullOrEmpty(rule.Emphasis);
                Merge(emphasis ? theme.CounterEmphasisIdle : theme.CounterIdle, rule, "inactive");
                Merge(emphasis ? theme.CounterEmphasisActive : theme.CounterActive, rule, "active");
                return;
            }
            if (rule.Classes.Count > 0)
            {
                // `[data-state] .clear` carries BOTH a state and a class: it is still class-scoped,
                // and the state still decides which of the two class scopes it lands in.
                // `[data-state] .clear` 同时带状态与类：仍属类作用域，而状态决定进入两个类作用域
                // 中的哪一个。
                foreach (string cls in rule.Classes)
                {
                    if (cls.Equals("counter", StringComparison.OrdinalIgnoreCase)) continue;
                    Merge(ClassScope(theme.ClassIdle, cls), rule, "inactive");
                    Merge(ClassScope(theme.ClassActive, cls), rule, "active");
                }
                return;
            }
            Merge(theme.Idle, rule, "inactive");
            Merge(theme.Active, rule, "active");
        }

        private static DmNoteCssScope ClassScope(Dictionary<string, DmNoteCssScope> map, string cls)
        {
            if (!map.TryGetValue(cls, out DmNoteCssScope scope)) { scope = new DmNoteCssScope(); map[cls] = scope; }
            return scope;
        }

        /// <summary>Apply a rule to one state scope. A rule carrying a state reaches ONLY that
        /// state; a state-less rule (a shared block) reaches both. Later rules win, which is also
        /// what CSS does for equal specificity — so source order alone decides the conflict.
        /// 把规则应用到某个状态作用域。带状态的规则**只**进入该状态，无状态的共享块进入两个；
        /// 后者胜出，这也正是 CSS 同优先级下的行为——故冲突只由源码顺序决定。
        /// </summary>
        private static void Merge(DmNoteCssScope scope, DmNoteCssRule rule, string targetState)
        {
            if (rule.State != null
                && !string.Equals(rule.State, targetState, StringComparison.OrdinalIgnoreCase)) return;
            foreach (KeyValuePair<string, string> kv in rule.Decls) scope.Decls[kv.Key] = kv.Value;
        }

        // ---------- var() resolution / var() 解析 ----------

        private static void ResolveAll(DmNoteCssTheme theme)
        {
            ResolveScope(theme.Idle);
            ResolveScope(theme.Active);
            ResolveScope(theme.CounterIdle);
            ResolveScope(theme.CounterActive);
            ResolveScope(theme.CounterEmphasisIdle);
            ResolveScope(theme.CounterEmphasisActive);
            foreach (KeyValuePair<string, DmNoteCssScope> kv in theme.ClassIdle) ResolveScope(kv.Value);
            foreach (KeyValuePair<string, DmNoteCssScope> kv in theme.ClassActive) ResolveScope(kv.Value);
        }

        private static void ResolveScope(DmNoteCssScope scope)
        {
            // var() is resolved AFTER the merge, because a shared block may define a custom
            // property that a later state block consumes. Depth-capped: a cyclic
            // --a: var(--b); --b: var(--a) would otherwise recurse until the stack dies.
            // var() 在**合并之后**解析，因为共享块可能定义某个自定义属性供后续状态块使用。
            // 深度受限：循环引用 (--a: var(--b); --b: var(--a)) 否则会一直递归到爆栈。
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in scope.Decls)
                resolved[kv.Key] = ResolveVars(kv.Value, scope.Decls, 0);
            scope.Decls.Clear();
            foreach (KeyValuePair<string, string> kv in resolved) scope.Decls[kv.Key] = kv.Value;
        }

        private static string ResolveVars(string value, Dictionary<string, string> vars, int depth)
        {
            if (depth > 8 || value.IndexOf("var(", StringComparison.OrdinalIgnoreCase) < 0) return value;
            var sb = new StringBuilder();
            int i = 0;
            while (i < value.Length)
            {
                int open = value.IndexOf("var(", i, StringComparison.OrdinalIgnoreCase);
                if (open < 0) { sb.Append(value, i, value.Length - i); break; }
                sb.Append(value, i, open - i);
                int close = MatchParen(value, open + 3);
                if (close < 0) { sb.Append(value, open, value.Length - open); break; }
                var parts = new List<string>(SplitTopLevel(value.Substring(open + 4, close - open - 4), ','));
                string name = parts.Count > 0 ? parts[0].Trim() : "";
                string fallback = parts.Count > 1 ? parts[1].Trim() : null;
                if (vars.TryGetValue(name, out string sub))
                    sb.Append(ResolveVars(sub, vars, depth + 1));
                else if (!string.IsNullOrEmpty(fallback))
                    sb.Append(ResolveVars(fallback, vars, depth + 1));
                // An unresolvable var() with no fallback resolves to nothing, which is what CSS
                // says too; the caller then sees an empty value and leaves its default alone.
                // 无后备且无法解析的 var() 解析为空，CSS 亦然；调用方随后看到空值，保持默认不变。
                i = close + 1;
            }
            return sb.ToString();
        }

        // ---------- colour / length helpers / 颜色与长度辅助 ----------

        /// <summary>Parse a CSS colour into the mod's float[4] RGBA. Returns null when the value is
        /// not a colour this importer understands (currentColor, a var() that did not resolve, …).
        /// 解析 CSS 颜色为 float[4] RGBA；无法识别时返回 null。</summary>
        public static float[] ParseColor(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string v = value.Trim().ToLowerInvariant();
            if (v == "transparent") return new float[] { 0f, 0f, 0f, 0f };
            if (v.StartsWith("#", StringComparison.Ordinal))
            {
                string h = v.Substring(1);
                if (h.Length == 3 || h.Length == 4)
                {
                    var c = new float[4];
                    for (int i = 0; i < h.Length; i++)
                    {
                        if (!Hex(h[i], out int nib)) return null;
                        c[i] = nib / 15f;
                    }
                    c[3] = h.Length == 4 ? c[3] : 1f;
                    return c;
                }
                if (h.Length == 6 || h.Length == 8)
                {
                    var c = new float[4];
                    for (int i = 0; i < 4; i++)
                    {
                        if (!Hex(h[i * 2], out int hi) || !Hex(h[i * 2 + 1], out int lo)) return null;
                        c[i] = (hi * 16 + lo) / 255f;
                    }
                    return c;
                }
                return null;
            }
            if (v.StartsWith("rgb", StringComparison.Ordinal) && v.Contains("("))
            {
                int close = v.IndexOf(')');
                if (close < 0) return null;
                var parts = new List<string>(SplitTopLevel(v.Substring(v.IndexOf('(') + 1, close - v.IndexOf('(') - 1), ','));
                if (parts.Count < 3) return null;
                var c = new float[4];
                for (int i = 0; i < 3; i++)
                {
                    string p = parts[i].Trim();
                    bool pct = p.EndsWith("%", StringComparison.Ordinal);
                    if (pct) p = p.Substring(0, p.Length - 1);
                    if (!float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return null;
                    c[i] = pct ? f / 100f : f / 255f;
                }
                c[3] = 1f;
                if (parts.Count >= 4)
                {
                    string a = parts[3].Trim();
                    bool pct = a.EndsWith("%", StringComparison.Ordinal);
                    if (pct) a = a.Substring(0, a.Length - 1);
                    if (!float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out float af)) return null;
                    c[3] = pct ? af / 100f : af;
                }
                return c;
            }
            if (v == "white") return new float[] { 1f, 1f, 1f, 1f };
            if (v == "black") return new float[] { 0f, 0f, 0f, 1f };
            return null;
        }

        private static bool Hex(char c, out int v)
        {
            if (c >= '0' && c <= '9') { v = c - '0'; return true; }
            if (c >= 'a' && c <= 'f') { v = c - 'a' + 10; return true; }
            if (c >= 'A' && c <= 'F') { v = c - 'A' + 10; return true; }
            v = 0; return false;
        }

        /// <summary>Leading length in px. / 取开头的 px 长度。</summary>
        public static float ParsePx(string value, float fallback = float.NaN)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;
            int i = 0;
            while (i < value.Length && (char.IsDigit(value[i]) || value[i] == '.' || value[i] == '-' || value[i] == '+')) i++;
            if (i == 0) return fallback;
            return float.TryParse(value.Substring(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                ? f : fallback;
        }

        /// <summary>Scale factor out of `transform: scale(n)` (uniform only). / 从 transform 取 scale。</summary>
        public static float ParseScale(string transform)
        {
            if (string.IsNullOrWhiteSpace(transform)) return float.NaN;
            int i = transform.IndexOf("scale", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return float.NaN;
            i = transform.IndexOf('(', i);
            if (i < 0) return float.NaN;
            int close = MatchParen(transform, i);
            if (close < 0) return float.NaN;
            var parts = new List<string>(SplitTopLevel(transform.Substring(i + 1, close - i - 1), ','));
            if (parts.Count == 0) return float.NaN;
            return float.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float s)
                ? s : float.NaN;
        }

        /// <summary>First NON-inset shadow as (color, blur, yOffset). box-shadow is
        /// comma-separated, and an `inset` shadow has no drop-shadow meaning here.
        /// 取第一个**非** inset 阴影：(颜色, 模糊, y 偏移)。box-shadow 逗号分隔，而 inset 阴影在此
        /// 没有投影含义。
        /// </summary>
        public static bool TryParseDropShadow(string value, out float[] color, out float blur, out float offsetY)
        {
            color = null; blur = float.NaN; offsetY = float.NaN;
            if (string.IsNullOrWhiteSpace(value)) return false;
            foreach (string layer in SplitTopLevel(value, ','))
            {
                string s = layer.Trim();
                if (s.Length == 0 || s.StartsWith("inset", StringComparison.OrdinalIgnoreCase)) continue;
                // The colour is the last rgb()/hex token; the leading tokens are lengths.
                int paren = s.IndexOf('(');
                int rgbStart = s.LastIndexOf("rgb", StringComparison.OrdinalIgnoreCase);
                if (rgbStart < 0)
                {
                    int hash = s.IndexOf('#');
                    if (hash < 0) continue;
                    int end = hash;
                    while (end < s.Length && s[end] != ' ') end++;
                    color = ParseColor(s.Substring(hash, end - hash));
                    if (color == null) continue;
                }
                else
                {
                    int close = MatchParen(s, rgbStart);
                    if (close < 0) continue;
                    color = ParseColor(s.Substring(rgbStart, close - rgbStart + 1));
                    if (color == null) continue;
                }
                string lengths = s.Substring(0, paren >= 0 && paren < rgbStart ? paren : rgbStart);
                var lens = new List<string>(SplitTopLevel(lengths.Trim(), ' '));
                // box-shadow length order is <offset-x> <offset-y> <blur> <spread> — four slots,
                // not two. Reading lens[0] as y and lens[1] as blur took the 10px y offset of
                // `0 10px 22px` as the blur radius, halving the glow on every imported key.
                // box-shadow 的长度顺序是 <偏移x> <偏移y> <模糊> <扩散>——四档而非两档。把
                // lens[0] 当 y、lens[1] 当 blur，会把 `0 10px 22px` 的 10px 偏移当成模糊半径，
                // 使每个导入按键的光效减半。
                offsetY = lens.Count > 1 ? ParsePx(lens[1]) : float.NaN;
                blur = lens.Count > 2 ? ParsePx(lens[2]) : float.NaN;
                return true;
            }
            return false;
        }

        /// <summary>Write a CSS `url(data:…)` / `url("data:…")` payload to disk.
        /// 把 CSS 的 url(data:…) 载荷写到磁盘。</summary>
        public static string ExtractDataUri(string url, out string extension)
        {
            extension = "svg";
            if (string.IsNullOrWhiteSpace(url)) return null;
            string s = url.Trim();
            int a = s.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
            if (a < 0) return null;
            int open = s.IndexOf('(', a);
            int close = MatchParen(s, open);
            if (close < 0) return null;
            s = Unquote(s.Substring(open + 1, close - open - 1));
            if (!s.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
            int comma = s.IndexOf(',');
            if (comma < 0) return null;
            string meta = s.Substring(5, comma - 5).ToLowerInvariant();
            string payload = s.Substring(comma + 1);
            if (meta.Contains("svg")) extension = "svg";
            else if (meta.Contains("png")) extension = "png";
            else if (meta.Contains("jpeg") || meta.Contains("jpg")) extension = "jpg";
            return meta.IndexOf(";base64", StringComparison.Ordinal) >= 0
                ? payload
                : Uri.UnescapeDataString(payload);
        }

        /// <summary>Read the stylesheet for a preset: the embedded copy wins (it is what the
        /// author actually had loaded), and a sibling .css of the same base name is the fallback.
        /// 读取预设的样式表：内嵌副本优先（那才是作者真正加载的），同名兄弟 .css 为后备。</summary>
        public static string LoadCompanionCss(string presetPath, string embedded)
        {
            if (!string.IsNullOrWhiteSpace(embedded)) return embedded;
            try
            {
                string dir = Path.GetDirectoryName(presetPath);
                if (string.IsNullOrEmpty(dir)) return null;
                string sibling = Path.Combine(dir, Path.GetFileNameWithoutExtension(presetPath) + ".css");
                return File.Exists(sibling) ? File.ReadAllText(sibling) : null;
            }
            catch (Exception) { return null; }
        }
    }
}
