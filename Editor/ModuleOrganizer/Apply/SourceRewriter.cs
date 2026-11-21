using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Monome.UtilityPack.ModuleOrganizer
{
    public class NamespaceInfo
    {
        public List<string> namespaces = new List<string>();
        // Also set for every shape we refuse to rewrite: file-scoped namespace, a namespace header broken by #if,
        // unbalanced braces, or code outside the single namespace block.
        public bool nested;
        public bool IsGlobal => namespaces.Count == 0;
        public bool Rewritable => !nested && namespaces.Count <= 1;
        internal int start, nameStart, nameEnd, open, close = -1; // offsets of the first namespace block
    }

    // Text-level edits of .cs files. All searches run on DeclarationFinder.Strip's mask (comments and literals blanked,
    // same offsets, every #if branch kept) and edits are applied to the original text at the same offsets.
    public static class SourceRewriter
    {
        const string Indent = "    ";
        static readonly Regex NsRx = new Regex(@"(?<![@\w])namespace\s+(?<n>@?\w+(?:\s*\.\s*@?\w+)*)|(?<o>\{)|(?<c>\})", RegexOptions.Compiled);
        static readonly Regex UsingRx = new Regex(@"^(?:using\s+(?!var\b)[^;(]+;\s*)+$", RegexOptions.Compiled);
        static readonly Regex ExternRx = new Regex(@"^extern\s+alias\s+\w+\s*;$", RegexOptions.Compiled);
        static readonly Regex GlobalAttrRx = new Regex(@"^\[\s*(?:assembly|module)\s*:.*\]$", RegexOptions.Compiled);
        static readonly Regex DirectiveRx = new Regex(@"^\s*#\s*(\w*)", RegexOptions.Compiled);
        static readonly Regex ModifiersRx = new Regex(
            @"(?<![\w@])(?:\[[^\[\]]*\]\s*|(?:public|internal|protected|private|sealed|abstract|static|partial|unsafe|new|readonly|ref)\s+)*$",
            RegexOptions.Compiled);
        static readonly Regex AsmQualifiedRx = new Regex(@",\s*Assembly-CSharp", RegexOptions.Compiled);

        enum Kind { Blank, Comment, Using, Extern, Attr, Directive }

        class Src
        {
            public readonly string text, mask, nl;
            public readonly bool[] cov; // char lies inside a comment or literal
            public readonly List<(int start, int end, int next)> lines = new List<(int, int, int)>(); // end excludes \r\n
            // The leading lines before the first code line. depth = #if nesting after the line;
            // clean = the line break is real code, not inside a multi-line comment or verbatim string.
            public readonly List<(Kind kind, string dir, int depth, bool clean)> top = new List<(Kind, string, int, bool)>();

            public Src(string s)
            {
                text = s;
                cov = new bool[s.Length];
                mask = DeclarationFinder.Strip(s, null, cov);
                var n = s.IndexOf('\n');
                nl = n > 0 && s[n - 1] == '\r' ? "\r\n" : "\n";
                for (var i = 0; i < s.Length;)
                {
                    var e = s.IndexOf('\n', i);
                    var next = e < 0 ? s.Length : e + 1;
                    if (e < 0) e = s.Length;
                    else if (e > i && s[e - 1] == '\r') e--;
                    lines.Add((i, e, next));
                    i = next;
                }
                var depth = 0;
                for (var i = 0; i < lines.Count; i++)
                {
                    var m = MaskLine(i).Trim();
                    string dir = null;
                    Kind k;
                    if (m.Length == 0) k = Line(i).Trim().Length == 0 ? Kind.Blank : Kind.Comment;
                    else if (m[0] == '#')
                    {
                        k = Kind.Directive;
                        dir = DirectiveRx.Match(Line(i)).Groups[1].Value;
                        if (dir == "if") depth++;
                        else if (dir == "endif") depth--;
                    }
                    else if (UsingRx.IsMatch(m)) k = Kind.Using;
                    else if (ExternRx.IsMatch(m)) k = Kind.Extern;
                    else if (GlobalAttrRx.IsMatch(m)) k = Kind.Attr;
                    else break;
                    top.Add((k, dir, depth, lines[i].next > lines[i].end && !cov[lines[i].next - 1]));
                }
            }

            public string Line(int i) => text.Substring(lines[i].start, lines[i].end - lines[i].start);
            public string MaskLine(int i) => mask.Substring(lines[i].start, lines[i].end - lines[i].start);
            public bool StartsInSpan(int i) => i > 0 && cov[lines[i - 1].next - 1];
            public int After(int i) => i < 0 ? 0 : lines[i].next; // offset after line i; -1 = file start
            public int TopEnd => top.Count < lines.Count ? lines[top.Count].start : text.Length;
            public bool Boundary(int i) => top[i].depth == 0 && top[i].clean;

            public int LineOf(int offset)
            {
                var i = 0;
                while (i + 1 < lines.Count && lines[i + 1].start <= offset) i++;
                return i;
            }

            // First line after the file header (usings, directives, global attributes and blank lines between them);
            // never inside an open #if or a multi-line comment. Comments directly above code stay with the code.
            public int HeaderEnd()
            {
                var h = 0;
                for (var i = 0; i < top.Count; i++)
                    if (top[i].kind != Kind.Comment && Boundary(i)) h = i + 1;
                return h;
            }
        }

        public static NamespaceInfo Parse(string src) => Parse(new Src(src));

        static NamespaceInfo Parse(Src s)
        {
            var info = new NamespaceInfo();
            var depth = 0;
            foreach (Match m in NsRx.Matches(s.mask))
            {
                if (m.Groups["n"].Success)
                {
                    if (depth > 0) info.nested = true;
                    var name = m.Groups["n"];
                    info.namespaces.Add(Regex.Replace(name.Value, @"[@\s]", ""));
                    var after = m.Index + m.Length;
                    while (after < s.mask.Length && char.IsWhiteSpace(s.mask[after])) after++;
                    if (after >= s.mask.Length || s.mask[after] != '{') info.nested = true;
                    if (info.namespaces.Count == 1)
                    {
                        info.start = m.Index;
                        info.nameStart = name.Index;
                        info.nameEnd = name.Index + name.Length;
                        info.open = after;
                    }
                }
                else if (m.Groups["o"].Success) depth++;
                else if (--depth < 0) info.nested = true;
                else if (depth == 0 && info.namespaces.Count > 0 && info.close < 0 && m.Index > info.open) info.close = m.Index;
            }
            if (depth != 0) info.nested = true;
            if (info.namespaces.Count == 1 && !info.nested &&
                !(OnlyBlank(s.mask, s.TopEnd, info.start) && OnlyBlank(s.mask, info.close + 1, s.mask.Length)))
                info.nested = true;
            return info;
        }

        // Whitespace or (blanked) preprocessor lines only.
        static bool OnlyBlank(string mask, int from, int to)
        {
            for (var i = from; i < to; i++)
                if (!char.IsWhiteSpace(mask[i]) && mask[i] != '#') return false;
            return true;
        }

        public static string SetNamespace(string src, string newNs)
        {
            var s = new Src(src);
            var info = Parse(s);
            if (!info.Rewritable) throw new InvalidOperationException("Namespace layout is too complex to rewrite safely.");
            if (info.IsGlobal) return newNs.Length == 0 ? src : Wrap(s, newNs);
            if (newNs.Length > 0) return src.Substring(0, info.nameStart) + newNs + src.Substring(info.nameEnd);
            return Unwrap(s, info);
        }

        static string Wrap(Src s, string ns)
        {
            var h = s.HeaderEnd();
            var sb = new StringBuilder(s.text, 0, s.After(h - 1), s.text.Length + 64);
            sb.Append("namespace ").Append(ns).Append(s.nl).Append('{').Append(s.nl);
            for (var i = h; i < s.lines.Count; i++)
            {
                if (!s.StartsInSpan(i) && s.Line(i).Trim().Length > 0) sb.Append(Indent);
                sb.Append(s.text, s.lines[i].start, s.lines[i].next - s.lines[i].start);
            }
            var endsNl = s.text.Length == 0 || s.text[s.text.Length - 1] == '\n';
            if (!endsNl) sb.Append(s.nl);
            sb.Append('}');
            if (endsNl) sb.Append(s.nl);
            return sb.ToString();
        }

        static string Unwrap(Src s, NamespaceInfo info)
        {
            var t = s.text;
            int LineStart(int i) => t.LastIndexOf('\n', Math.Max(i - 1, 0)) + 1;
            int NextLine(int i) { var n = t.IndexOf('\n', i); return n < 0 ? t.Length : n + 1; }
            bool Ws(int from, int to) { for (var k = from; k < to; k++) if (!char.IsWhiteSpace(t[k])) return false; return true; }

            // Drop whole lines for the header and closing brace when they stand alone, else just the tokens.
            int a = info.start, b = info.open + 1, c = info.close, d = info.close + 1;
            if (Ws(LineStart(a), a)) a = LineStart(a);
            if (Ws(b, NextLine(b))) b = NextLine(b);
            if (Ws(LineStart(c), c) && Ws(d, NextLine(d))) { c = LineStart(c); d = NextLine(d); }

            var sb = new StringBuilder(t, 0, a, t.Length);
            for (var p = b; p < c;)
            {
                if (t[p - 1] == '\n' && !s.cov[p - 1]) // a real line start: dedent one level where present
                {
                    if (p + Indent.Length <= c && string.CompareOrdinal(t, p, Indent, 0, Indent.Length) == 0) p += Indent.Length;
                    else if (t[p] == '\t') p++;
                }
                var n = p < c ? t.IndexOf('\n', p, c - p) : -1;
                var end = n < 0 ? c : n + 1;
                sb.Append(t, p, end - p);
                p = end;
            }
            return sb.Append(t, d, t.Length - d).ToString();
        }

        public static string AddUsing(string src, string ns)
        {
            var s = new Src(src);
            var rx = UsingOf(ns);
            var anchor = -1;
            var hasUsing = false;
            for (var i = 0; i < s.top.Count; i++)
            {
                var k = s.top[i];
                if (k.kind == Kind.Using)
                {
                    if (rx.IsMatch(s.MaskLine(i))) return src;
                    hasUsing = true;
                }
                if (k.kind == Kind.Using || k.kind == Kind.Extern || k.dir == "define" || k.dir == "undef") anchor = i;
            }
            int at;
            if (anchor < 0) at = s.After(s.HeaderEnd() - 1);
            else
            {
                // After the anchor, but outside any #if it sits in; fall back to the last safe line before it.
                var j = anchor;
                while (j < s.top.Count && !s.Boundary(j)) j++;
                if (j == s.top.Count)
                    for (j = anchor - 1; j >= 0 && !s.Boundary(j); j--) { }
                at = s.After(j);
            }
            var text = "using " + ns + ";" + s.nl;
            if (!hasUsing && !NextLineBlank(src, at)) text += s.nl;
            return src.Insert(at, text);
        }

        static bool NextLineBlank(string src, int at)
        {
            var n = src.IndexOf('\n', at);
            return string.IsNullOrWhiteSpace(src.Substring(at, (n < 0 ? src.Length : n) - at));
        }

        public static string RemoveUsing(string src, string ns)
        {
            var s = new Src(src);
            var rx = UsingOf(ns);
            var sb = new StringBuilder(src);
            for (var i = s.top.Count - 1; i >= 0; i--) // backwards so earlier offsets stay valid
            {
                if (s.top[i].kind != Kind.Using) continue;
                var line = s.MaskLine(i);
                var hits = rx.Matches(line);
                if (hits.Count == 0) continue;
                var l = s.lines[i];
                if (rx.Replace(line, "").Trim().Length == 0 && s.top[i].clean && !s.StartsInSpan(i))
                    sb.Remove(l.start, l.next - l.start);
                else
                    for (var k = hits.Count - 1; k >= 0; k--) sb.Remove(l.start + hits[k].Index, hits[k].Length);
            }
            return sb.ToString();
        }

        // `using Ns;` exactly: not `using static Ns;`, `using A = Ns;` or `using Ns.Sub;`.
        static Regex UsingOf(string ns) => new Regex(@"(?<![\w.@])using\s+" + NsPattern(ns) + @"\s*;");

        static string NsPattern(string ns) => string.Join(@"\s*\.\s*", ns.Split('.').Select(p => "@?" + Regex.Escape(p)));

        // Puts [MovedFrom] directly above the type's attribute lines (or inline before its modifiers when the
        // declaration shares a line with other code). No-op when the type is not found or already has MovedFrom.
        public static string AddMovedFrom(string src, string typeSimpleName, string oldNs, string oldAssembly)
        {
            var s = new Src(src);
            var m = Regex.Match(s.mask, @"(?<![@\w])(?:class|struct|interface|enum)\s+@?" + Regex.Escape(typeSimpleName) + @"(?!\w)");
            if (!m.Success) return src;
            var attr = "[UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"" + oldNs +
                       "\", sourceAssembly: \"" + oldAssembly + "\")]";
            var li = s.LineOf(m.Index);
            var lineStart = s.lines[li].start;
            var mods = ModifiersRx.Match(s.mask.Substring(lineStart, m.Index - lineStart));
            int at;
            string insert;
            if (s.mask.Substring(lineStart, mods.Index).Trim().Length == 0 && !s.StartsInSpan(li))
            {
                var t = li;
                while (t > 0 && !s.StartsInSpan(t - 1) && IsAttributeLine(s.MaskLine(t - 1))) t--;
                at = s.lines[t].start;
                var line = s.Line(t);
                insert = line.Substring(0, line.Length - line.TrimStart(' ', '\t').Length) + attr + s.nl;
            }
            else
            {
                at = lineStart + mods.Index;
                insert = attr + " ";
            }
            if (Regex.IsMatch(s.mask.Substring(at, m.Index - at), @"\bMovedFrom(?:Attribute)?\b")) return src;
            return src.Insert(at, insert);
        }

        static bool IsAttributeLine(string maskLine)
        {
            var t = maskLine.Trim();
            return t.StartsWith("[") && t.EndsWith("]");
        }

        // 1-based lines of `ns.Type` in code (not comments/strings), for the given simple type names.
        public static List<int> FindQualifiedUses(string src, string ns, IEnumerable<string> typeSimpleNames)
        {
            var names = string.Join("|", typeSimpleNames.Select(Regex.Escape));
            if (names.Length == 0) return new List<int>();
            var s = new Src(src);
            var rx = new Regex(@"(?<![\w.@])" + NsPattern(ns) + @"\s*\.\s*@?(?:" + names + @")(?!\w)");
            return LineNumbers(s, rx.Matches(s.mask).Cast<Match>().Select(m => m.Index));
        }

        // 1-based lines whose string literals contain ", Assembly-CSharp" (assembly-qualified type names that break
        // once the type moves to another assembly).
        public static List<int> FindAssemblyQualifiedStrings(string src)
        {
            var s = new Src(src);
            return LineNumbers(s, AsmQualifiedRx.Matches(src).Cast<Match>().Where(m => s.mask[m.Index] == '"').Select(m => m.Index));
        }

        static List<int> LineNumbers(Src s, IEnumerable<int> offsets) => offsets.Select(o => s.LineOf(o) + 1).Distinct().ToList();
    }
}
