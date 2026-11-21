using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Monome.UtilityPack.ModuleOrganizer
{
    public static class DeclarationFinder
    {
        static readonly Regex Token = new Regex(
            // (?<![@\w]) instead of \b so "@class" used as an identifier is not read as a keyword.
            @"(?<![@\w])namespace\s+(?<ns>@?\w+(?:\s*\.\s*@?\w+)*)" +
            @"|(?<![@\w])(?:(?<pt>partial)\s+)?(?<tk>class|struct|interface|enum)\s+@?(?<tn>\w+)\s*(?<ta><[^>]*>)?" +
            @"|(?<![@\w])(?<dg>delegate)(?!\w)" +
            @"|(?<open>\{)|(?<close>\})",
            RegexOptions.Compiled);

        static readonly Regex DirectiveRx = new Regex(@"^\s*(\w+)\s*(.*?)\s*$", RegexOptions.Compiled);
        static readonly Regex ExprToken = new Regex(@"\w+|&&|\|\||==|!=|[!()]", RegexOptions.Compiled);

        public static Dictionary<string, string> Find(string source) => Find(source, null);

        // Returns fullName -> kind, using Cecil naming: "Ns.Outer`1/Inner". Kind is prefixed "partial " for partial declarations.
        // Code in inactive #if/#elif/#else branches (given `defines` plus the file's own #define/#undef) is ignored.
        public static Dictionary<string, string> Find(string source, ICollection<string> defines)
        {
            var result = new Dictionary<string, string>();
            var frames = new List<(char kind, string name)>(); // 'n' namespace, 't' type, 'b' block
            (char kind, string name)? pending = null;
            var text = Strip(source, defines);
            foreach (Match m in Token.Matches(text))
            {
                if (m.Groups["ns"].Success) pending = ('n', Regex.Replace(m.Groups["ns"].Value, @"[@\s]", ""));
                else if (m.Groups["tn"].Success)
                {
                    if (m.Groups["tn"].Value == "where") continue; // "where T : class where U ..."
                    var name = m.Groups["tn"].Value;
                    if (m.Groups["ta"].Success) name += "`" + (m.Groups["ta"].Value.Count(c => c == ',') + 1);
                    result[FullName(frames, name)] = (m.Groups["pt"].Success ? "partial " : "") + m.Groups["tk"].Value;
                    pending = ('t', name);
                }
                else if (m.Groups["dg"].Success)
                {
                    var name = DelegateName(text, m.Index + m.Length);
                    if (name != null) result[FullName(frames, name)] = "delegate";
                }
                else if (m.Groups["open"].Success)
                {
                    frames.Add(pending ?? ('b', null));
                    pending = null;
                }
                else if (frames.Count > 0) frames.RemoveAt(frames.Count - 1);
            }
            return result;
        }

        static string FullName(List<(char kind, string name)> frames, string name)
        {
            var ns = string.Join(".", frames.Where(f => f.kind == 'n').Select(f => f.name));
            var path = string.Join("/", frames.Where(f => f.kind == 't').Select(f => f.name).Append(name));
            return ns.Length > 0 ? ns + "." + path : path;
        }

        // The identifier right before the top-level parameter list, with its generic arity; null for anonymous delegates
        // ("delegate (int x) { }", "delegate { }") and function pointers ("delegate*<...>").
        static string DelegateName(string s, int i)
        {
            string last = null;
            int arity = 0, paren = 0, angle = 0;
            for (; i < s.Length; i++)
            {
                var c = s[i];
                if (char.IsLetter(c) || c == '_' || c == '@')
                {
                    var start = i;
                    while (i + 1 < s.Length && (char.IsLetterOrDigit(s[i + 1]) || s[i + 1] == '_')) i++;
                    if (paren == 0 && angle == 0)
                    {
                        last = s.Substring(start, i - start + 1).TrimStart('@');
                        arity = 0;
                    }
                }
                else if (c == '<') { if (angle++ == 0 && paren == 0) arity = 1; }
                else if (c == '>') angle--;
                else if (c == ',' && angle == 1 && paren == 0) arity++;
                else if (c == '(')
                {
                    if (paren == 0 && angle == 0 && last != null) return arity > 0 ? last + "`" + arity : last;
                    paren++;
                }
                else if (c == ')') paren--;
                else if (paren == 0 && angle == 0 && (c == '{' || c == '}' || c == ';' || c == '=' || c == '*')) return null;
            }
            return null;
        }

        // Blanks comments, string/char literals, preprocessor lines and inactive #if branches.
        // ponytail: interpolated strings with nested quotes inside holes can confuse it.
        static string Strip(string s, ICollection<string> defines)
        {
            var defs = new HashSet<string>(defines ?? Array.Empty<string>());
            var conds = new List<(bool parent, bool taken)>();
            var active = true;
            var sb = new StringBuilder(s.Length);
            var lineStart = true;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                var next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (lineStart && c == '#')
                {
                    var end = SkipTo(s, i, "\n");
                    active = Directive(s.Substring(i + 1, end - i - 1), defs, conds, active);
                    i = end - 1;
                    sb.Append(' ');
                    continue;
                }
                if (c == '\n') { lineStart = true; sb.Append(c); continue; }
                if (!char.IsWhiteSpace(c)) lineStart = false;
                if (!active) continue; // inactive branch: only directive lines matter
                if (c == '/' && next == '/') { i = SkipTo(s, i, "\n") - 1; sb.Append(' '); continue; }
                if (c == '/' && next == '*') { i = SkipTo(s, i + 2, "*/"); sb.Append(' '); continue; }
                if (c == '"' || c == '\'' || ((c == '@' || c == '$') && (next == '"' || next == '@' || next == '$')))
                {
                    var verbatim = false;
                    while (i < s.Length && s[i] != '"' && s[i] != '\'') { verbatim |= s[i] == '@'; i++; }
                    if (i >= s.Length) break;
                    var q = s[i];
                    for (i++; i < s.Length; i++)
                    {
                        if (!verbatim && s[i] == '\\') { i++; continue; }
                        if (s[i] == q)
                        {
                            if (verbatim && i + 1 < s.Length && s[i + 1] == q) { i++; continue; }
                            break;
                        }
                        if (!verbatim && s[i] == '\n') { i--; break; } // unterminated: keep the newline
                    }
                    sb.Append(' ');
                    continue;
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Applies one directive line (text after '#'); returns whether the following code is active.
        static bool Directive(string line, HashSet<string> defs, List<(bool parent, bool taken)> conds, bool active)
        {
            var cut = line.IndexOf("//", StringComparison.Ordinal);
            if (cut >= 0) line = line.Substring(0, cut);
            var m = DirectiveRx.Match(line);
            var arg = m.Groups[2].Value;
            var top = conds.Count - 1;
            switch (m.Groups[1].Value)
            {
                case "if":
                    var taken = active && Eval(arg, defs);
                    conds.Add((active, taken));
                    return taken;
                case "elif":
                    if (top < 0) return active;
                    if (conds[top].taken) return false;
                    var now = conds[top].parent && Eval(arg, defs);
                    conds[top] = (conds[top].parent, now);
                    return now;
                case "else":
                    if (top < 0) return active;
                    var elseActive = conds[top].parent && !conds[top].taken;
                    conds[top] = (conds[top].parent, true);
                    return elseActive;
                case "endif":
                    if (top < 0) return active;
                    var parent = conds[top].parent;
                    conds.RemoveAt(top);
                    return parent;
                case "define":
                    if (active) defs.Add(arg);
                    return active;
                case "undef":
                    if (active) defs.Remove(arg);
                    return active;
                default:
                    return active;
            }
        }

        // #if expression: identifiers, true/false, !, &&, ||, ==, !=, parentheses.
        static bool Eval(string expr, HashSet<string> defs)
        {
            var toks = ExprToken.Matches(expr).Cast<Match>().Select(t => t.Value).ToList();
            var i = 0;
            return Or();

            bool Or()
            {
                var v = And();
                while (Peek("||")) { i++; var r = And(); v = v || r; }
                return v;
            }

            bool And()
            {
                var v = Eq();
                while (Peek("&&")) { i++; var r = Eq(); v = v && r; }
                return v;
            }

            bool Eq()
            {
                var v = Unary();
                while (Peek("==") || Peek("!="))
                {
                    var eq = toks[i++] == "==";
                    var r = Unary();
                    v = eq ? v == r : v != r;
                }
                return v;
            }

            bool Unary()
            {
                if (Peek("!")) { i++; return !Unary(); }
                if (Peek("("))
                {
                    i++;
                    var v = Or();
                    if (Peek(")")) i++;
                    return v;
                }
                var t = i < toks.Count ? toks[i++] : "false";
                return t == "true" || (t != "false" && defs.Contains(t));
            }

            bool Peek(string t) => i < toks.Count && toks[i] == t;
        }

        // Index of the end of `end` searching from `from` (or s.Length when absent); for "\n" returns the newline index.
        static int SkipTo(string s, int from, string end)
        {
            var i = s.IndexOf(end, from, StringComparison.Ordinal);
            if (i < 0) return s.Length;
            return end == "\n" ? i : i + end.Length - 1;
        }
    }
}
