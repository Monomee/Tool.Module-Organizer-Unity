using System;
using System.Collections.Generic;
using System.Linq;

namespace Monome.UtilityPack.ModuleOrganizer
{
    // Where each file ends up after Apply (path, namespace, assembly, usings). The one rule the validator checks and
    // the planner executes.
    public class FileFates
    {
        readonly ModuleConfig cfg;
        readonly ScanResult scan;
        readonly Func<ScriptFile, string> source; // null = unknown: treated as not rewritable, no usings
        readonly Dictionary<string, ScriptFile> byPath = new Dictionary<string, ScriptFile>();
        readonly Dictionary<string, NamespaceInfo> infos = new Dictionary<string, NamespaceInfo>();
        readonly Dictionary<string, List<string>> usings = new Dictionary<string, List<string>>();
        Dictionary<string, (string oldNs, string newNs)> moved;
        HashSet<string> gone, nsBefore, nsAfter;
        // First module listing a GUID wins; later listings are duplicates.
        public readonly Dictionary<string, (Module module, ScriptEntry entry)> assigned = new Dictionary<string, (Module, ScriptEntry)>();
        public readonly List<(ScriptEntry entry, Module first, Module second)> duplicates = new List<(ScriptEntry, Module, Module)>();
        // Pending removals not assigned again: Apply restores them to PathRules.RestorePath / originalNamespace.
        public readonly Dictionary<string, ScriptEntry> pending = new Dictionary<string, ScriptEntry>();

        public FileFates(ModuleConfig cfg, ScanResult scan, Func<ScriptFile, string> source)
        {
            this.cfg = cfg;
            this.scan = scan;
            this.source = source;
            foreach (var f in scan.files.Values) byPath[f.path] = f;
            foreach (var m in cfg.modules)
            foreach (var e in m.scripts)
                if (assigned.TryGetValue(e.guid, out var first)) duplicates.Add((e, first.module, m));
                else assigned[e.guid] = (m, e);
            foreach (var e in cfg.pendingRemovals)
                if (!assigned.ContainsKey(e.guid)) pending[e.guid] = e;
        }

        // Current namespace as the rewriter sees it: "" for global, null when the layout is too complex to rewrite.
        public static string CurrentNamespace(NamespaceInfo i) => !i.Rewritable ? null : i.IsGlobal ? "" : i.namespaces[0];

        public NamespaceInfo Info(ScriptFile f)
        {
            if (infos.TryGetValue(f.guid, out var i)) return i;
            var src = source(f);
            return infos[f.guid] = src == null ? new NamespaceInfo { nested = true } : SourceRewriter.Parse(src);
        }

        public List<string> FileUsings(ScriptFile f)
        {
            if (!usings.TryGetValue(f.guid, out var u)) u = usings[f.guid] = SourceRewriter.FindUsings(source(f));
            return u;
        }

        public ScriptFile FileAt(string path) => byPath.TryGetValue(PathRules.Norm(path), out var f) ? f : null;

        public Module ModuleOf(string guid) => assigned.TryGetValue(guid, out var a) ? a.module : null;

        public Module ModuleOf(ScriptFile f) => ModuleOf(f.guid);

        string Original(ScriptFile f, ScriptEntry e) => string.IsNullOrEmpty(e.originalPath) ? f.path : e.originalPath;

        // Path after Apply, or null when Apply does not move the file.
        public string TargetPath(ScriptFile f)
        {
            if (!ModuleValidator.IsAssignable(f, cfg)) return null;
            if (assigned.TryGetValue(f.guid, out var a))
                return PathRules.TargetPath(a.module, new ScriptEntry { originalPath = Original(f, a.entry), relativeDir = a.entry.relativeDir }, f.path, f.IsEditor, cfg.scopeFolders);
            return pending.TryGetValue(f.guid, out var p) && !string.IsNullOrEmpty(p.originalPath) ? PathRules.RestorePath(p, f.path) : null;
        }

        // Assembly after Apply: the module asmdef, Assembly-CSharp(-Editor) for a restored file, else unchanged (null).
        public string TargetAssembly(ScriptFile f)
        {
            if (!ModuleValidator.IsAssignable(f, cfg)) return null;
            if (assigned.TryGetValue(f.guid, out var a)) return PathRules.AsmdefName(a.module, f.IsEditor);
            return pending.ContainsKey(f.guid) ? (f.IsEditor ? "Assembly-CSharp-Editor" : "Assembly-CSharp") : null;
        }

        // Whether Apply will change this file's namespace: to the module's target namespace, or back to
        // originalNamespace for a pending removal. Files it cannot rewrite keep theirs.
        public bool Changes(ScriptFile f, out string oldNs, out string newNs)
        {
            oldNs = newNs = null;
            if (!ModuleValidator.IsAssignable(f, cfg)) return false;
            string target;
            if (assigned.TryGetValue(f.guid, out var a))
            {
                if (!a.module.rewriteNamespace) return false;
                target = PathRules.TargetNamespace(a.module, a.entry.relativeDir ?? PathRules.RelativeDir(a.module, Original(f, a.entry), cfg.scopeFolders), f.IsEditor);
            }
            else if (pending.TryGetValue(f.guid, out var p) && p.originalNamespace != null) target = p.originalNamespace;
            else return false;
            var cur = CurrentNamespace(Info(f));
            if (cur == null) return false;
            oldNs = cur;
            newNs = target;
            return newNs != oldNs;
        }

        // Top-level types whose namespace Apply changes (decided by the type's first file), full name -> (old, new).
        public Dictionary<string, (string oldNs, string newNs)> MovedTypes()
        {
            if (moved != null) return moved;
            moved = new Dictionary<string, (string, string)>();
            foreach (var t in scan.types.Values.Where(t => !t.fullName.Contains('/')))
            {
                var tf = t.filePaths.Count > 0 ? FileAt(t.filePaths[0]) : null;
                if (tf != null && Changes(tf, out var oldNs, out var newNs)) moved[t.fullName] = (oldNs, newNs);
            }
            return moved;
        }

        // Namespaces a moved type leaves that hold no type after Apply, directly or in a child namespace: a `using`
        // of them no longer compiles (CS0246).
        public HashSet<string> GoneNamespaces()
        {
            if (gone != null) return gone;
            var m = MovedTypes();
            var remaining = new HashSet<string>(scan.types.Values.SelectMany(t =>
                ModuleValidator.Prefixes(m.TryGetValue(ModuleValidator.Outer(t.fullName), out var mv) ? mv.newNs : t.ns ?? "")));
            return gone = new HashSet<string>(m.Values.Select(v => v.oldNs).Where(n => n.Length > 0 && !remaining.Contains(n)));
        }

        // Namespaces f resolves without a using after Apply: its own (final) namespaces, their parents, and global.
        public HashSet<string> Enclosing(ScriptFile f) =>
            new HashSet<string>((Changes(f, out _, out var n) ? new List<string> { n } : Info(f).namespaces).SelectMany(ModuleValidator.Prefixes)) { "" };

        // C# looks a simple name up in each enclosing namespace's members, child namespaces included, before the file's
        // top-level usings: in Game.Combat.Editor a plain `Editor` is the namespace Game.Combat.Editor, not UnityEditor.Editor
        // (CS0118). For each name f writes unqualified whose type only Apply makes shadowed: (alias, global:: full name),
        // for a `using alias = ...;` inside the namespace block, which binds first. Sorted.
        // ponytail: namespaces are all script-assembly ones (not per-assembly visibility); an extra alias is harmless.
        public List<(string alias, string target)> ShadowAliases(ScriptFile f)
        {
            var result = new List<(string, string)>();
            var cur = CurrentNamespace(Info(f));
            var final = Changes(f, out _, out var n) ? n : cur;
            var src = source(f);
            if (string.IsNullOrEmpty(final) || src == null) return result; // global: nothing encloses it
            var m = MovedTypes();
            nsBefore ??= new HashSet<string>(scan.types.Values.SelectMany(t => ModuleValidator.Prefixes(t.ns ?? "")));
            nsAfter ??= new HashSet<string>(scan.types.Values.SelectMany(t =>
                ModuleValidator.Prefixes(m.TryGetValue(ModuleValidator.Outer(t.fullName), out var mv) ? mv.newNs : t.ns ?? "")));
            var unqualified = ModuleValidator.UnqualifiedNames(src);
            var refs = f.referencedTypes.Select(ModuleValidator.Outer).Distinct().Where(r => !r.Contains('`'))
                .Select(r => (simple: ModuleValidator.SimpleName(r), before: NsOf(r), after: m.TryGetValue(r, out var mv) ? mv.newNs : NsOf(r)))
                .Where(r => unqualified.Contains(r.simple));
            foreach (var g in refs.GroupBy(r => r.simple).Where(g => g.Count() == 1))
            {
                var r = g.Single();
                if (Shadowed(final, r.after, r.simple, nsAfter) && !Shadowed(cur ?? "", r.before, r.simple, nsBefore))
                    result.Add((r.simple, "global::" + (r.after.Length == 0 ? "" : r.after + ".") + r.simple));
            }
            return result.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();

            string NsOf(string fullName) => scan.types.TryGetValue(fullName, out var t) ? t.ns ?? "" : ModuleValidator.NsOf(fullName);
        }

        // Outward from ns: reaching the type's own namespace resolves it; a child namespace with its name shadows it first.
        static bool Shadowed(string ns, string typeNs, string simple, HashSet<string> namespaces)
        {
            foreach (var n in ModuleValidator.Prefixes(ns).Append(""))
            {
                if (n == typeNs) return false;
                if (namespaces.Contains(n.Length == 0 ? simple : n + "." + simple)) return true;
            }
            return false;
        }

        // Usings Apply adds to and removes from f (sorted). Added: new namespaces of moved types f uses, and, when f's own
        // namespace changes, namespaces of unmoved types f used to see through its old enclosing namespaces. Removed:
        // f's `using`s of namespaces that disappear.
        public (List<string> add, List<string> remove) Usings(ScriptFile f)
        {
            var enclosing = Enclosing(f);
            var oldEnclosing = Changes(f, out var oldNs, out _) ? new HashSet<string>(ModuleValidator.Prefixes(oldNs)) : new HashSet<string>();
            var m = MovedTypes();
            var add = new HashSet<string>();
            foreach (var tn in f.referencedTypes)
            {
                if (!scan.types.TryGetValue(tn, out var t)) continue;
                if (m.TryGetValue(ModuleValidator.Outer(tn), out var mv)) add.Add(mv.newNs);
                else if (oldEnclosing.Contains(t.ns ?? "")) add.Add(t.ns);
            }
            var remove = FileUsings(f).Where(GoneNamespaces().Contains).Where(n => !enclosing.Contains(n));
            return (add.Where(n => !enclosing.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList(),
                remove.OrderBy(n => n, StringComparer.Ordinal).ToList());
        }
    }
}
