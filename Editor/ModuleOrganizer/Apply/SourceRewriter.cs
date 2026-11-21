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
    // A leading BOM is set aside and put back at position 0.
    public static class SourceRewriter
    {
        const string Indent = "    ";
        const string Bom = "﻿";
        static readonly Regex NsRx = new Regex(@"(?<![@\w])namespace\s+(?<n>@?\w+(?:\s*\.\s*@?\w+)*)|(?<o>\{)|(?<c>\})", RegexOptions.Compiled);
        static readonly Regex UsingRx = new Regex(@"^(?:using\s+(?!var\b)[^;(]+;\s*)+$", RegexOptions.Compiled);
        static readonly Regex ExternRx = new Regex(@"^extern\s+alias\s+\w+\s*;$", RegexOptions.Compiled);
        static readonly Regex GlobalAttrRx = new Regex(@"^\[\s*(?:assembly|module)\s*:", RegexOptions.Compiled);
        static readonly Regex GlobalAttrAnywhereRx = new Regex(@"\[\s*(?:assembly|module)\s*:", RegexOptions.Compiled);
        static readonly Regex PartialRx = new Regex(@"(?<![\w@])partial(?!\w)", RegexOptions.Compiled);
        static readonly Regex DirectiveRx = new Regex(@"^\s*#\s*(\w*)", RegexOptions.Compiled);
        static readonly Regex MovedFromRx = new Regex(@"\bMovedFrom(?:Attribute)?\b", RegexOptions.Compiled);
        static readonly Regex AsmQualifiedRx = new Regex(@",\s*Assembly-CSharp", RegexOptions.Compiled);
        static readonly HashSet<string> Modifiers = new HashSet<string>
        {
            "public", "internal", "protected", "private", "sealed", "abstract", "static", "partial", "unsafe", "new", "readonly", "ref"
        };

        enum Kind { Blank, Comment, Using, Extern, Attr, Directive }

        class Src
        {
            public readonly string text, mask, nl;
            public readonly bool[] cov; // char lies inside a comment or literal
            public readonly List<(int start, int end, int next)> lines = new List<(int, int, int)>(); // end excludes \r\n
            // The leading lines before the first code line. depth = #if + #region nesting after the line, ifDepth = #if only;
            // clean = the line break is a real one: not inside a multi-line comment, verbatim string or global attribute.
            public readonly List<(Kind kind, string dir, int depth, int ifDepth, bool clean)> top =
                new List<(Kind, string, int, int, bool)>();

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
                int ifDepth = 0, regionDepth = 0, attrOpen = 0;
                for (var i = 0; i < lines.Count; i++)
                {
                    var m = MaskLine(i).Trim();
                    string dir = null;
                    Kind k;
                    if (attrOpen > 0 || GlobalAttrRx.IsMatch(m)) // [assembly: ...], possibly over several lines
                    {
                        attrOpen += m.Count(ch => ch == '[') - m.Count(ch => ch == ']');
                        if (attrOpen <= 0 && !m.EndsWith("]")) break; // code after the attribute on the same line
                        attrOpen = Math.Max(attrOpen, 0);
                        k = Kind.Attr;
                    }
                    else if (m.Length == 0) k = Line(i).Trim().Length == 0 ? Kind.Blank : Kind.Comment;
                    else if (m[0] == '#')
                    {
                        k = Kind.Directive;
                        dir = DirectiveRx.Match(Line(i)).Groups[1].Value;
                        if (dir == "if") ifDepth++;
                        else if (dir == "endif") ifDepth--;
                        else if (dir == "region") regionDepth++;
                        else if (dir == "endregion") regionDepth--;
                    }
                    else if (UsingRx.IsMatch(m)) k = Kind.Using;
                    else if (ExternRx.IsMatch(m)) k = Kind.Extern;
                    else break;
                    var clean = lines[i].next > lines[i].end && !cov[lines[i].next - 1] && attrOpen == 0;
                    top.Add((k, dir, ifDepth + regionDepth, ifDepth, clean));
                }
            }

            public string Line(int i) => text.Substring(lines[i].start, lines[i].end - lines[i].start);
            public string MaskLine(int i) => mask.Substring(lines[i].start, lines[i].end - lines[i].start);
            public bool StartsInSpan(int i) => i > 0 && cov[lines[i - 1].next - 1];
            public int After(int i) => i < 0 ? 0 : lines[i].next; // offset after line i; -1 = file start
            public int TopEnd => top.Count < lines.Count ? lines[top.Count].start : text.Length;
            public bool Boundary(int i) => top[i].depth == 0 && top[i].clean;

            // Per line: the enclosing #if groups as (group id, branch index: 0 = #if, then each #elif/#else).
            public List<(int id, int branch)>[] BranchPaths()
            {
                var paths = new List<(int id, int branch)>[lines.Count];
                var stack = new List<(int id, int branch)>();
                var nextId = 0;
                for (var i = 0; i < lines.Count; i++)
                {
                    var m = MaskLine(i).TrimStart();
                    if (m.Length > 0 && m[0] == '#')
                    {
                        var dir = DirectiveRx.Match(Line(i)).Groups[1].Value;
                        var last = stack.Count - 1;
                        if (dir == "if") stack.Add((nextId++, 0));
                        else if ((dir == "elif" || dir == "else") && last >= 0) stack[last] = (stack[last].id, stack[last].branch + 1);
                        else if (dir == "endif" && last >= 0) stack.RemoveAt(last);
                    }
                    paths[i] = new List<(int id, int branch)>(stack);
                }
                return paths;
            }

            public int LineOf(int offset)
            {
                var i = 0;
                while (i + 1 < lines.Count && lines[i + 1].start <= offset) i++;
                return i;
            }

            // First line after the file header (usings, directives, global attributes and blank lines between them),
            // looking only at the first `limit` top lines; never inside an open #if/#region or a multi-line comment.
            // Comments directly above code stay with the code.
            public int HeaderEnd(int limit)
            {
                var h = 0;
                for (var i = 0; i < Math.Min(limit, top.Count); i++)
                    if (top[i].kind != Kind.Comment && Boundary(i)) h = i + 1;
                return h;
            }
        }

        static string KeepBom(string src, Func<string, string> edit) =>
            src.StartsWith(Bom, StringComparison.Ordinal) ? Bom + edit(src.Substring(1)) : edit(src);

        static string NoBom(string src) => src.StartsWith(Bom, StringComparison.Ordinal) ? src.Substring(1) : src;

        public static NamespaceInfo Parse(string src) => Parse(new Src(NoBom(src)));

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
            // Global file: wrapping must not pull an [assembly:]/[module:] attribute into the namespace (CS1730).
            if (info.IsGlobal && GlobalAttrAnywhereRx.IsMatch(s.mask, s.After(s.HeaderEnd(s.top.Count) - 1))) info.nested = true;
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

        public static string SetNamespace(string src, string newNs) => KeepBom(src, body =>
        {
            var s = new Src(body);
            var info = Parse(s);
            if (!info.Rewritable) throw new InvalidOperationException("Namespace layout is too complex to rewrite safely.");
            if (info.IsGlobal) return newNs.Length == 0 ? body : Wrap(s, newNs);
            if (newNs.Length > 0) return body.Substring(0, info.nameStart) + newNs + body.Substring(info.nameEnd);
            return Unwrap(s, info);
        });

        static bool EndsWithNewline(string t) => t.Length == 0 || t[t.Length - 1] == '\n';

        static string Wrap(Src s, string ns)
        {
            var h = s.HeaderEnd(s.top.Count);
            var sb = new StringBuilder(s.text, 0, s.After(h - 1), s.text.Length + 64);
            sb.Append("namespace ").Append(ns).Append(s.nl).Append('{').Append(s.nl);
            for (var i = h; i < s.lines.Count; i++)
            {
                if (!s.StartsInSpan(i) && s.Line(i).Trim().Length > 0) sb.Append(Indent);
                sb.Append(s.text, s.lines[i].start, s.lines[i].next - s.lines[i].start);
            }
            var endsNl = EndsWithNewline(s.text);
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
            var r = sb.Append(t, d, t.Length - d).ToString();
            if (!EndsWithNewline(t) && r.EndsWith("\n")) r = r.Substring(0, r.Length - (r.EndsWith("\r\n") ? 2 : 1));
            return r;
        }

        public static string AddUsing(string src, string ns) => KeepBom(src, body =>
        {
            var s = new Src(body);
            var rx = UsingOf(ns);
            var limit = s.top.FindIndex(t => t.kind == Kind.Attr); // usings must precede global attributes
            if (limit < 0) limit = s.top.Count;
            var anchor = -1;
            var hasUsing = false;
            for (var i = 0; i < limit; i++)
            {
                var k = s.top[i];
                if (k.kind == Kind.Using)
                {
                    // A using under #if does not count: the type would be missing in the other configuration.
                    if (k.ifDepth == 0 && rx.IsMatch(s.MaskLine(i))) return body;
                    hasUsing = true;
                }
                if (k.kind == Kind.Using || k.kind == Kind.Extern || k.dir == "define" || k.dir == "undef") anchor = i;
            }
            int at;
            if (anchor < 0) at = s.After(s.HeaderEnd(limit) - 1);
            else
            {
                // After the anchor, but outside any #if/#region it sits in; fall back to the last safe line before it.
                var j = anchor;
                while (j < limit && !s.Boundary(j)) j++;
                if (j == limit)
                    for (j = anchor - 1; j >= 0 && !s.Boundary(j); j--) { }
                at = s.After(j);
            }
            var text = "using " + ns + ";" + s.nl;
            if (!hasUsing && !NextLineBlank(body, at)) text += s.nl;
            return body.Insert(at, text);
        });

        static bool NextLineBlank(string src, int at)
        {
            var n = src.IndexOf('\n', at);
            return string.IsNullOrWhiteSpace(src.Substring(at, (n < 0 ? src.Length : n) - at));
        }

        public static string RemoveUsing(string src, string ns) => KeepBom(src, body =>
        {
            var s = new Src(body);
            var rx = UsingOf(ns);
            var sb = new StringBuilder(body);
            // File header and the heads of namespace blocks: a plain `using Ns;` is a directive wherever it appears.
            for (var i = s.lines.Count - 1; i >= 0; i--) // backwards so earlier offsets stay valid
            {
                var top = i < s.top.Count;
                if (top && s.top[i].kind != Kind.Using) continue;
                var line = s.MaskLine(i);
                var hits = rx.Matches(line);
                if (hits.Count == 0) continue;
                var l = s.lines[i];
                var clean = top ? s.top[i].clean : l.next > l.end && !s.cov[l.next - 1];
                if (rx.Replace(line, "").Trim().Length == 0 && clean && !s.StartsInSpan(i))
                    sb.Remove(l.start, l.next - l.start);
                else
                    for (var k = hits.Count - 1; k >= 0; k--) sb.Remove(l.start + hits[k].Index, hits[k].Length);
            }
            return sb.ToString();
        });

        // `using Ns;` exactly: not `using static Ns;`, `using A = Ns;` or `using Ns.Sub;`.
        static Regex UsingOf(string ns) => new Regex(@"(?<![\w.@])using\s+" + NsPattern(ns) + @"\s*;");

        static string NsPattern(string ns) => string.Join(@"\s*\.\s*", ns.Split('.').Select(p => "@?" + Regex.Escape(p)));

        public static string AddMovedFrom(string src, string typeSimpleName, string oldNs, string oldAssembly)
        {
            TryAddMovedFrom(src, typeSimpleName, oldNs, oldAssembly, out var result);
            return result;
        }

        // Puts [MovedFrom] above every declaration of the type (each #if/#else variant), on its own line above the
        // attributes and modifiers, or inline when the declaration shares a line with other code. A MovedFrom already
        // on a declaration (from an earlier Apply) is repointed at the given source instead: one per type. typeSimpleName may carry a Cecil arity ("Box`2").
        // Returns false (result = src) when the file declares no such type.
        // ponytail: two nested types with the same simple name in one file both get annotated.
        public static bool TryAddMovedFrom(string src, string typeSimpleName, string oldNs, string oldAssembly, out string result)
        {
            var body = NoBom(src);
            var tick = typeSimpleName.IndexOf('`');
            var name = tick < 0 ? typeSimpleName : typeSimpleName.Substring(0, tick);
            var arity = tick < 0 ? 0 : int.Parse(typeSimpleName.Substring(tick + 1));
            var s = new Src(body);
            var attr = "[UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"" + oldNs +
                       "\", sourceAssembly: \"" + oldAssembly + "\")]";
            var decls = Regex.Matches(s.mask, @"(?<![@\w])(?:class|struct|interface|enum)\s+@?" + Regex.Escape(name) + @"(?!\w)")
                .Cast<Match>().Where(m => Arity(s.mask, m.Index + m.Length) == arity).ToList();
            bool Has(int kw) { var st = DeclStart(s.mask, kw, true); return MovedFromRx.IsMatch(s.mask.Substring(st, kw - st)); }
            var repointed = Repoint(s, decls, attr.Substring(1, attr.Length - 2));
            if (repointed != body) return TryAddMovedFrom((body.Length < src.Length ? Bom : "") + repointed, typeSimpleName, oldNs, oldAssembly, out result);
            bool IsPartial(int kw) { var st = DeclStart(s.mask, kw, false); return PartialRx.IsMatch(s.mask.Substring(st, kw - st)); }
            // Non-partial declarations are #if alternatives: each gets one. Partial parts are one type, so exactly one
            // part per preprocessor configuration may carry it (else CS0579): a part is annotated only when it is
            // mutually exclusive (same #if group, different branch) with every part already carrying it. Parts are visited
            // least-conditional first (fewest enclosing #if groups, then source order) so an unconditional part wins.
            // ponytail: parts only in independent groups (#if X ... #endif #if Y ... #endif, no unconditional part) get
            // one annotation, so configurations with !X lack MovedFrom; covering them needs a part per configuration.
            var paths = s.BranchPaths();
            bool Exclusive(Match a, Match b) => paths[s.LineOf(a.Index)]
                .Any(x => paths[s.LineOf(b.Index)].Any(y => y.id == x.id && y.branch != x.branch));
            var partials = decls.Where(m => IsPartial(m.Index)).ToList();
            var targets = decls.Where(m => !partials.Contains(m) && !Has(m.Index)).ToList();
            var annotated = partials.Where(m => Has(m.Index)).ToList();
            foreach (var m in partials.OrderBy(m => paths[s.LineOf(m.Index)].Count)) // stable: ties keep source order
                if (!Has(m.Index) && annotated.All(a => Exclusive(a, m)))
                {
                    annotated.Add(m);
                    targets.Add(m);
                }
            targets.Sort((x, y) => x.Index.CompareTo(y.Index));
            var sb = new StringBuilder(body);
            for (var d = targets.Count - 1; d >= 0; d--) // backwards so earlier offsets stay valid
            {
                var kw = targets[d].Index;
                var p = DeclStart(s.mask, kw, false);
                var li = s.LineOf(p);
                var ls = s.lines[li].start;
                if (s.mask.Substring(ls, p - ls).Trim().Length == 0 && !s.StartsInSpan(li))
                {
                    var line = s.Line(li);
                    sb.Insert(ls, line.Substring(0, line.Length - line.TrimStart(' ', '\t').Length) + attr + s.nl);
                }
                else sb.Insert(p, attr + " ");
            }
            result = decls.Count == 0 ? src : (body.Length < src.Length ? Bom : "") + sb;
            return decls.Count > 0;
        }

        // Replaces each existing `[Ns.]MovedFrom[Attribute](...)` on the declarations with `call`.
        static string Repoint(Src s, List<Match> decls, string call)
        {
            var spans = new SortedSet<(int start, int end)>();
            foreach (var d in decls)
            {
                var st = DeclStart(s.mask, d.Index, true);
                foreach (Match a in MovedFromRx.Matches(s.mask.Substring(st, d.Index - st)))
                {
                    var b = QualifierStart(s.mask, st + a.Index);
                    var e = st + a.Index + a.Length;
                    var q = e;
                    while (q < s.mask.Length && char.IsWhiteSpace(s.mask[q])) q++;
                    if (q < s.mask.Length && s.mask[q] == '(')
                        for (var depth = 0; q < s.mask.Length; q++)
                        {
                            if (s.mask[q] == '(') depth++;
                            else if (s.mask[q] == ')' && --depth == 0) { e = q + 1; break; }
                        }
                    spans.Add((b, e));
                }
            }
            var sb = new StringBuilder(s.text);
            foreach (var (b, e) in spans.Reverse()) sb.Remove(b, e - b).Insert(b, call);
            return sb.ToString();
        }

        // Start of the qualified name ending at p (`A . B.`, `global::`), whitespace and blanked comments allowed around dots.
        static int QualifierStart(string mask, int p)
        {
            while (true)
            {
                var q = p;
                while (q > 0 && char.IsWhiteSpace(mask[q - 1])) q--;
                if (q > 0 && mask[q - 1] == '.') q--;
                else if (q > 1 && mask[q - 1] == ':' && mask[q - 2] == ':') q -= 2;
                else return p;
                while (q > 0 && char.IsWhiteSpace(mask[q - 1])) q--;
                var w = q;
                while (w > 0 && (char.IsLetterOrDigit(mask[w - 1]) || mask[w - 1] == '_' || mask[w - 1] == '@')) w--;
                if (w == q) return p;
                p = w;
            }
        }

        static readonly Regex PlainUsingRx = new Regex(@"(?<![\w.@])using\s+(?!static\b)(@?\w+(?:\s*\.\s*@?\w+)*)\s*;", RegexOptions.Compiled);

        // Namespaces of the file's `using Ns;` directives, in the header or at the head of a namespace block (not
        // `using static`, not aliases), #if branches included. A plain `using A.B;` can only be a directive.
        public static List<string> FindUsings(string src)
        {
            var mask = new Src(NoBom(src ?? "")).mask;
            return PlainUsingRx.Matches(mask).Cast<Match>().Select(m => Regex.Replace(m.Groups[1].Value, @"[@\s]", "")).Distinct().ToList();
        }

        // Number of type parameters in a `<...>` right after offset i (0 when there is none). Only top-level commas
        // count: nested <...> and [attribute(...)] sections are skipped.
        static int Arity(string mask, int i)
        {
            while (i < mask.Length && char.IsWhiteSpace(mask[i])) i++;
            if (i >= mask.Length || mask[i] != '<') return 0;
            int depth = 0, inner = 0, commas = 0;
            for (; i < mask.Length; i++)
            {
                var c = mask[i];
                if (c == '[' || c == '(') inner++;
                else if (c == ']' || c == ')') inner--;
                else if (inner > 0) continue;
                else if (c == '<') depth++;
                else if (c == '>' && --depth == 0) break;
                else if (c == ',' && depth == 1) commas++;
            }
            return commas + 1;
        }

        // Start of the declaration whose keyword is at p: walks back over whitespace, modifiers and [attributes]
        // (and over preprocessor lines when crossDirectives, used to spot an existing MovedFrom).
        static int DeclStart(string mask, int p, bool crossDirectives)
        {
            bool Ident(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
            while (true)
            {
                var q = p;
                while (q > 0 && (char.IsWhiteSpace(mask[q - 1]) || (crossDirectives && mask[q - 1] == '#'))) q--;
                if (q > 0 && mask[q - 1] == ']')
                {
                    int k = q - 1, depth = 0;
                    for (; k >= 0; k--)
                    {
                        if (mask[k] == ']') depth++;
                        else if (mask[k] == '[' && --depth == 0) break;
                    }
                    if (k < 0 || (k > 0 && Ident(mask[k - 1]))) return p; // unbalanced, or an indexer / array type
                    p = k;
                    continue;
                }
                var w = q;
                while (w > 0 && Ident(mask[w - 1])) w--;
                if (w < q && Modifiers.Contains(mask.Substring(w, q - w)) && (w == 0 || (mask[w - 1] != '@' && mask[w - 1] != '.')))
                {
                    p = w;
                    continue;
                }
                return p;
            }
        }

        // 1-based lines of `ns.Type` in code (not comments/strings), for the given simple type names.
        public static List<int> FindQualifiedUses(string src, string ns, IEnumerable<string> typeSimpleNames)
        {
            var names = string.Join("|", typeSimpleNames.Select(Regex.Escape));
            if (names.Length == 0 || ns.Length == 0) return new List<int>();
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
