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
        HashSet<string> gone;
        // First module listing a GUID wins; later listings are duplicates.
        public readonly Dictionary<string, (Module module, ScriptEntry entry)> assigned = new Dictionary<string, (Module, ScriptEntry)>();
        public readonly List<(ScriptEntry entry, Module first, Module second)> duplicates = new List<(ScriptEntry, Module, Module)>();
        // Pending removals not assigned again: Apply restores them to originalPath / originalNamespace.
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
                return PathRules.TargetPath(a.module, new ScriptEntry { originalPath = Original(f, a.entry) }, f.IsEditor, cfg.scopeFolders);
            return pending.TryGetValue(f.guid, out var p) && !string.IsNullOrEmpty(p.originalPath) ? PathRules.Norm(p.originalPath) : null;
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
                target = PathRules.TargetNamespace(a.module, PathRules.RelativeDir(a.module, Original(f, a.entry), cfg.scopeFolders), f.IsEditor);
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
