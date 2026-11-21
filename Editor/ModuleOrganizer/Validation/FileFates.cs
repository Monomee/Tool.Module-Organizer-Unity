using System;
using System.Collections.Generic;
using System.Linq;

namespace Monome.UtilityPack.ModuleOrganizer
{
    // Where each file ends up after Apply (path, namespace, owner). The one rule the validator checks and the planner executes.
    public class FileFates
    {
        readonly ModuleConfig cfg;
        readonly ScanResult scan;
        readonly Func<ScriptFile, string> currentNs; // null = file not rewritable
        readonly Dictionary<string, ScriptFile> byPath = new Dictionary<string, ScriptFile>();
        // First module listing a GUID wins; later listings are duplicates.
        public readonly Dictionary<string, (Module module, ScriptEntry entry)> assigned = new Dictionary<string, (Module, ScriptEntry)>();
        public readonly List<(ScriptEntry entry, Module first, Module second)> duplicates = new List<(ScriptEntry, Module, Module)>();
        // Pending removals not assigned again: Apply restores them to originalPath / originalNamespace.
        public readonly Dictionary<string, ScriptEntry> pending = new Dictionary<string, ScriptEntry>();

        public FileFates(ModuleConfig cfg, ScanResult scan, Func<ScriptFile, string> currentNs)
        {
            this.cfg = cfg;
            this.scan = scan;
            this.currentNs = currentNs;
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
            if (assigned.TryGetValue(f.guid, out var a)) return a.module.namespaceName + (f.IsEditor ? ".Editor" : "");
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
                target = PathRules.TargetNamespace(a.module, PathRules.ScopeRelativeDir(Original(f, a.entry), cfg.scopeFolders), f.IsEditor);
            }
            else if (pending.TryGetValue(f.guid, out var p) && p.originalNamespace != null) target = p.originalNamespace;
            else return false;
            var cur = currentNs(f);
            if (cur == null) return false;
            oldNs = cur;
            newNs = target;
            return newNs != oldNs;
        }

        // Top-level types whose namespace Apply changes (decided by the type's first file), full name -> (old, new).
        public Dictionary<string, (string oldNs, string newNs)> MovedTypes()
        {
            var moved = new Dictionary<string, (string, string)>();
            foreach (var t in scan.types.Values.Where(t => !t.fullName.Contains('/')))
            {
                var tf = t.filePaths.Count > 0 ? FileAt(t.filePaths[0]) : null;
                if (tf != null && Changes(tf, out var oldNs, out var newNs)) moved[t.fullName] = (oldNs, newNs);
            }
            return moved;
        }
    }
}
