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
            @"|(?<![@\w])(?<tk>class|struct|interface|enum)\s+@?(?<tn>\w+)\s*(?<ta><[^>]*>)?" +
            // The return type may be a tuple: "delegate (int a, int b) Handler();"
            @"|(?<![@\w])delegate\s+(?:[^;{}()=]|\([^;{}()=]*\))*?(?<dn>\w+)\s*(?<da><[^>]*>)?\s*\(" +
            @"|(?<open>\{)|(?<close>\})",
            RegexOptions.Compiled);

        // Returns fullName -> kind, using Cecil naming: "Ns.Outer`1/Inner".
        public static Dictionary<string, string> Find(string source)
        {
            var result = new Dictionary<string, string>();
            var frames = new List<(char kind, string name)>(); // 'n' namespace, 't' type, 'b' block
            (char kind, string name)? pending = null;
            foreach (Match m in Token.Matches(Strip(source)))
            {
                if (m.Groups["ns"].Success) pending = ('n', Regex.Replace(m.Groups["ns"].Value, @"[@\s]", ""));
                else if (m.Groups["tn"].Success)
                {
                    if (m.Groups["tn"].Value == "where") continue; // "where T : class where U ..."
                    var name = Arity(m.Groups["tn"].Value, m.Groups["ta"]);
                    result[FullName(frames, name)] = m.Groups["tk"].Value;
                    pending = ('t', name);
                }
                else if (m.Groups["dn"].Success)
                    result[FullName(frames, Arity(m.Groups["dn"].Value, m.Groups["da"]))] = "delegate";
                else if (m.Groups["open"].Success)
                {
                    frames.Add(pending ?? ('b', null));
                    pending = null;
                }
                else if (frames.Count > 0) frames.RemoveAt(frames.Count - 1);
            }
            return result;
        }

        static string Arity(string name, Group args) =>
            args.Success ? name + "`" + (args.Value.Count(c => c == ',') + 1) : name;

        static string FullName(List<(char kind, string name)> frames, string name)
        {
            var ns = string.Join(".", frames.Where(f => f.kind == 'n').Select(f => f.name));
            var path = string.Join("/", frames.Where(f => f.kind == 't').Select(f => f.name).Append(name));
            return ns.Length > 0 ? ns + "." + path : path;
        }

        // Blanks comments, string/char literals and preprocessor lines.
        // ponytail: interpolated strings with nested quotes in holes and unbalanced braces across #if branches can confuse it.
        static string Strip(string s)
        {
            var sb = new StringBuilder(s.Length);
            var lineStart = true;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                var next = i + 1 < s.Length ? s[i + 1] : '\0';
                if (lineStart && c == '#') { i = SkipTo(s, i, "\n") - 1; sb.Append(' '); continue; }
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
                        if (!verbatim && s[i] == '\n') break;
                    }
                    sb.Append(' ');
                    continue;
                }
                if (c == '\n') lineStart = true;
                else if (!char.IsWhiteSpace(c)) lineStart = false;
                sb.Append(c);
            }
            return sb.ToString();
        }

        // Index of the end of `end` searching from `from` (or s.Length when absent); for "\n" returns the newline index.
        static int SkipTo(string s, int from, string end)
        {
            var i = s.IndexOf(end, from, System.StringComparison.Ordinal);
            if (i < 0) return s.Length;
            return end == "\n" ? i : i + end.Length - 1;
        }
    }
}
