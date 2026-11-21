using System;
using System.Collections.Generic;
using System.Linq;

namespace Monome.UtilityPack.ModuleOrganizer
{
    // Finds, on the draft config and before any file is touched, every problem that would make Apply produce a
    // non-compiling project (spec 5.2-5.4).
    public static class ModuleValidator
    {
        public static bool OwnsAsmdef(ModuleConfig cfg, string asmdefGuid) =>
            !string.IsNullOrEmpty(asmdefGuid) && cfg.modules.Any(m => m.asmdefGuid == asmdefGuid || m.editorAsmdefGuid == asmdefGuid);

        // Assembly-CSharp(-Editor) files in scope, plus files already in a tool-made asmdef. Everything else is read-only.
        public static bool IsAssignable(ScriptFile f, ModuleConfig cfg) =>
            f.kind == AssemblyKind.AsmDef
                ? OwnsAsmdef(cfg, f.asmdefGuid)
                : (f.kind is AssemblyKind.Runtime or AssemblyKind.Editor) && cfg.IsInScope(f.path);

        public static ValidationReport Validate(ModuleConfig cfg, ScanResult scan, Func<string, string> readSource)
        {
            var r = new ValidationReport();
            var files = scan.files.Values.OrderBy(f => f.path, StringComparer.Ordinal).ToList();
            var assigned = new Dictionary<string, (Module module, ScriptEntry entry)>();
            foreach (var m in cfg.modules)
            foreach (var e in m.scripts)
                if (!assigned.ContainsKey(e.guid)) assigned[e.guid] = (m, e);
            var sources = new Dictionary<string, string>();
            var nsInfo = new Dictionary<string, NamespaceInfo>();
            var edges = new Dictionary<(string, string), ModuleEdge>();
            var edgeTypes = new Dictionary<(string, string, string), string>(); // (from, to, path) -> first type used

            // References from editable files (and from mis-assigned read-only ones).
            foreach (var f in files)
            {
                var module = ModuleOf(f);
                var editable = IsAssignable(f, cfg);
                if (module != null && !editable)
                {
                    Add(Severity.Error, IssueCode.OutOfScopeRef, f.path,
                        $"{f.path} is assigned to {module.name} but is read-only (outside scope, firstpass, or in an asmdef the tool did not create).");
                    continue;
                }
                if (!editable)
                {
                    if (f.kind is AssemblyKind.Runtime or AssemblyKind.Editor or AssemblyKind.FirstPass or AssemblyKind.FirstPassEditor)
                        CheckReverseRefs(f);
                    continue;
                }
                if (module != null && module.rewriteNamespace && !Info(f).Rewritable)
                    Add(Severity.Warning, IssueCode.MultiNamespace, f.path,
                        $"{f.path} has several or nested namespaces; its namespace rewrite will be skipped.");

                foreach (var tn in f.referencedTypes.OrderBy(t => t, StringComparer.Ordinal))
                {
                    if (!scan.types.TryGetValue(tn, out var t)) continue;
                    var c = ReferenceClassifier.Classify(f, t, cfg, scan);
                    var tf = t.filePaths.Count > 0 ? scan.FileByPath(t.filePaths[0]) : null;
                    var where = tf != null ? tf.path : t.assemblyName;
                    if (module == null)
                    {
                        if (c == RefClass.OutOfScope)
                            Add(Severity.Warning, IssueCode.OutOfScopeRef, f.path,
                                $"{f.path} uses {tn} from {where}, outside scope; this file cannot be assigned to a module until that is resolved.");
                        continue;
                    }
                    if (tf != null && !f.IsEditor && tf.IsEditor)
                    {
                        Add(Severity.Error, IssueCode.RuntimeToEditor, f.path, $"Runtime file {f.path} uses Editor type {tn} from {tf.path}.");
                        continue;
                    }
                    switch (c)
                    {
                        case RefClass.SameModule:
                        case RefClass.OtherModule:
                            AddEdge(Node(f), Node(tf), f.path, tn);
                            break;
                        case RefClass.UnassignedInScope:
                            Add(Severity.Error, IssueCode.UnassignedInScopeRef, f.path,
                                $"{f.path} ({module.name}) uses {tn} from {where}, which is not assigned to a module; assign it too.");
                            break;
                        case RefClass.OutOfScope:
                            Add(Severity.Error, IssueCode.OutOfScopeRef, f.path,
                                $"{f.path} ({module.name}) uses {tn} from {where}, outside scope; a module asmdef cannot see it.");
                            break;
                        case RefClass.NonAutoDll:
                            Add(Severity.Error, IssueCode.NonAutoDll, f.path,
                                $"{f.path} ({module.name}) uses {tn} from {t.assemblyName}, a precompiled DLL that is not auto-referenced.");
                            break;
                        case RefClass.OtherAsmdef:
                            if (string.IsNullOrEmpty(t.asmdefGuid)) break;
                            var node = Node(f);
                            if (!r.asmdefRefs.TryGetValue(node, out var set)) set = r.asmdefRefs[node] = new HashSet<string>();
                            set.Add(t.asmdefGuid);
                            break;
                    }
                }
            }

            var topLevel = scan.types.Values.Where(t => !t.fullName.Contains('/') && t.filePaths.Count > 0)
                .OrderBy(t => t.fullName, StringComparer.Ordinal).ToList();

            // Partial parts must end up in the same assembly.
            foreach (var t in topLevel.Where(t => t.filePaths.Count > 1))
            {
                var parts = t.filePaths.Select(p => scan.FileByPath(p)).Where(p => p != null).ToList();
                if (parts.Select(p => ModuleOf(p)?.name).Distinct().Count() < 2) continue;
                Add(Severity.Error, IssueCode.PartialSplit, parts[0].path,
                    $"Partial type {t.fullName} is split: " + string.Join(", ", parts.Select(p => $"{p.path} ({ModuleOf(p)?.name ?? "unassigned"})")) + ".");
            }

            // Final namespace of every source-declared type; two different types landing on the same name clash.
            var moved = new Dictionary<string, (string oldNs, string newNs)>();
            var finals = new List<(TypeRecord type, string path, string ns, bool changed)>();
            foreach (var t in topLevel)
            {
                var tf = scan.FileByPath(t.filePaths[0]);
                if (tf == null) continue;
                var changed = Changes(tf, out var oldNs, out var newNs);
                if (changed) moved[t.fullName] = (oldNs, newNs);
                finals.Add((t, tf.path, changed ? newNs : t.ns ?? "", changed));
            }
            foreach (var g in finals.GroupBy(x => (x.ns, SimpleName(x.type.fullName))))
            {
                if (!g.Any(x => x.changed) || g.Select(x => x.type.fullName).Distinct().Count() < 2) continue;
                Add(Severity.Error, IssueCode.NameClash, g.First(x => x.changed).path,
                    $"Name clash in namespace '{g.Key.ns}': " + string.Join(", ", g.Select(x => $"{x.type.fullName} ({x.path})")) + ".");
            }

            // Text-level leftovers Apply will not rewrite.
            var movesAssembly = files.Any(f => ModuleOf(f) != null && (f.kind is AssemblyKind.Runtime or AssemblyKind.Editor));
            foreach (var f in files.Where(f => IsAssignable(f, cfg)))
            {
                var uses = f.referencedTypes.Concat(f.declaredTypes).Select(n => n.Split('/')[0]).Distinct()
                    .Where(moved.ContainsKey).ToList();
                foreach (var g in uses.GroupBy(n => moved[n].oldNs).Where(g => g.Key.Length > 0))
                {
                    var lines = SourceRewriter.FindQualifiedUses(Source(f), g.Key, g.Select(SimpleName).Select(StripArity).Distinct());
                    if (lines.Count > 0)
                        Add(Severity.Warning, IssueCode.QualifiedName, f.path,
                            $"{f.path} line {string.Join(", ", lines)}: fully qualified {g.Key}.* name of a type that moves to {moved[g.First()].newNs}; fix by hand.");
                }
                if (!movesAssembly) continue;
                var aq = SourceRewriter.FindAssemblyQualifiedStrings(Source(f));
                if (aq.Count > 0)
                    Add(Severity.Warning, IssueCode.AssemblyQualifiedString, f.path,
                        $"{f.path} line {string.Join(", ", aq)}: string names Assembly-CSharp; it breaks if that type moves to a module.");
            }

            r.edges = edges.Values.OrderBy(e => e.from, StringComparer.Ordinal).ThenBy(e => e.to, StringComparer.Ordinal).ToList();
            r.cycles = CycleFinder.Find(r.edges.GroupBy(e => e.from).ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(e => e.to))));
            foreach (var cycle in r.cycles)
            {
                var inCycle = r.edges.Where(e => cycle.Contains(e.from) && cycle.Contains(e.to)).ToList();
                Add(Severity.Error, IssueCode.Cycle, inCycle[0].causedBy[0],
                    "Cycle: " + string.Join("; ", inCycle.Select(e =>
                        $"{e.from} → {e.to} (" + string.Join(", ", e.causedBy.Select(p => $"{p} uses {edgeTypes[(e.from, e.to, p)]}")) + ")")));
            }
            return r;

            Module ModuleOf(ScriptFile f) => assigned.TryGetValue(f.guid, out var a) ? a.module : null;

            string Node(ScriptFile f) => ModuleOf(f).name + (f.IsEditor ? ".Editor" : "");

            string Source(ScriptFile f)
            {
                if (!sources.TryGetValue(f.path, out var s)) s = sources[f.path] = readSource(f.path) ?? "";
                return s;
            }

            NamespaceInfo Info(ScriptFile f)
            {
                if (!nsInfo.TryGetValue(f.path, out var i)) i = nsInfo[f.path] = SourceRewriter.Parse(Source(f));
                return i;
            }

            // Whether Apply will change this file's namespace. Files it cannot rewrite keep theirs.
            bool Changes(ScriptFile f, out string oldNs, out string newNs)
            {
                oldNs = newNs = null;
                if (!assigned.TryGetValue(f.guid, out var a) || !a.module.rewriteNamespace || !IsAssignable(f, cfg) || !Info(f).Rewritable)
                    return false;
                oldNs = Info(f).IsGlobal ? "" : Info(f).namespaces[0];
                var original = string.IsNullOrEmpty(a.entry.originalPath) ? f.path : a.entry.originalPath;
                newNs = PathRules.TargetNamespace(a.module, PathRules.ScopeRelativeDir(original, cfg.scopeFolders), f.IsEditor);
                return newNs != oldNs;
            }

            // Apply never edits files outside scope, so they must keep compiling with the moved types' new namespaces.
            void CheckReverseRefs(ScriptFile f)
            {
                foreach (var tn in f.referencedTypes.OrderBy(t => t, StringComparer.Ordinal))
                {
                    if (!scan.types.TryGetValue(tn, out var t)) continue;
                    var tf = t.filePaths.Select(p => scan.FileByPath(p)).FirstOrDefault(p => p != null && ModuleOf(p) != null);
                    if (tf == null || !Changes(tf, out var oldNs, out var newNs)) continue;
                    Add(Severity.Error, IssueCode.ReverseRefOutOfScope, f.path,
                        $"{f.path} (outside scope) uses {tn} from {tf.path}, whose namespace changes '{oldNs}' → '{newNs}'. " +
                        $"Add the file's folder to the scope or turn off rewriteNamespace for {ModuleOf(tf).name}.");
                }
            }

            void AddEdge(string from, string to, string path, string type)
            {
                if (from == to) return;
                if (!edges.TryGetValue((from, to), out var e)) e = edges[(from, to)] = new ModuleEdge { from = from, to = to };
                if (e.causedBy.Contains(path)) return;
                e.causedBy.Add(path);
                edgeTypes[(from, to, path)] = type;
            }

            void Add(Severity severity, IssueCode code, string path, string message) =>
                r.issues.Add(new Issue { severity = severity, code = code, path = path, message = message });
        }

        static string SimpleName(string fullName) => fullName.Substring(fullName.LastIndexOf('.') + 1); // keeps `1: Box`1 != Box

        static string StripArity(string name) => name.Split('`')[0];
    }
}
