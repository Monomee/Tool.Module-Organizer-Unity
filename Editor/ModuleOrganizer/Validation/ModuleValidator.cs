using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Monome.UtilityPack.ModuleOrganizer
{
    // Finds, on the draft config and before any file is touched, every problem that would make Apply produce a
    // non-compiling project (spec 5.2-5.4).
    public static class ModuleValidator
    {
        // Asmdefs of current modules, plus those of deleted modules that Apply has yet to remove.
        public static bool OwnsAsmdef(ModuleConfig cfg, string asmdefGuid) =>
            !string.IsNullOrEmpty(asmdefGuid) &&
            (cfg.modules.Any(m => m.asmdefGuid == asmdefGuid || m.editorAsmdefGuid == asmdefGuid) || cfg.retiredAsmdefGuids.Contains(asmdefGuid));

        // Assembly-CSharp(-Editor) files in scope, plus files already in a tool-made asmdef. Everything else is read-only.
        public static bool IsAssignable(ScriptFile f, ModuleConfig cfg) =>
            f.kind == AssemblyKind.AsmDef
                ? OwnsAsmdef(cfg, f.asmdefGuid)
                : (f.kind is AssemblyKind.Runtime or AssemblyKind.Editor) && cfg.IsInScope(f.path);

        class FinalType
        {
            public string fullName, ns, where;
            public bool changed;
        }

        public static ValidationReport Validate(ModuleConfig cfg, ScanResult scan, Func<string, string> readSource)
        {
            var r = new ValidationReport();
            var files = scan.files.Values.OrderBy(f => f.path, StringComparer.Ordinal).ToList();
            var sources = new Dictionary<string, string>();
            var fates = new FileFates(cfg, scan, Source);
            foreach (var (e, first, second) in fates.duplicates)
            {
                var p = scan.files.TryGetValue(e.guid, out var df) ? df.path : e.guid;
                Add(Severity.Error, IssueCode.DuplicateAssignment, p, $"{p} is assigned to both {first.name} and {second.name}.");
            }
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
                    {
                        CheckReverseRefs(f);
                        foreach (var ns in fates.GoneNamespaces().Count == 0 ? new List<string>() : fates.FileUsings(f).Where(fates.GoneNamespaces().Contains))
                            Add(Severity.Error, IssueCode.ReverseRefOutOfScope, f.path,
                                $"{f.path} (outside scope) has 'using {ns};', but Apply leaves no type in {ns}. Add the file's folder to the scope or remove the using.");
                    }
                    continue;
                }
                if (module != null && module.rewriteNamespace && !Info(f).Rewritable)
                    Add(Severity.Warning, IssueCode.MultiNamespace, f.path,
                        $"{f.path} has several or nested namespaces; its namespace rewrite will be skipped.");

                foreach (var tn in f.referencedTypes.OrderBy(t => t, StringComparer.Ordinal))
                {
                    if (!scan.types.TryGetValue(tn, out var t)) continue;
                    var c = ReferenceClassifier.Classify(t, cfg, FileAt, ModuleOfGuid, module);
                    var tf = t.filePaths.Count > 0 ? FileAt(t.filePaths[0]) : null;
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
                                $"{f.path} ({module.name}) uses {tn} from {where}, which is not assigned to a module" +
                                (fates.pending.ContainsKey(tf.guid) ? " (it is being removed from its module)" : "") + "; assign it too.");
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
                            var asmRef = string.IsNullOrEmpty(t.asmdefGuid) ? t.assemblyName : t.asmdefGuid;
                            if (string.IsNullOrEmpty(asmRef)) break;
                            var node = Node(f);
                            if (!r.asmdefRefs.TryGetValue(node, out var set)) set = r.asmdefRefs[node] = new HashSet<string>();
                            set.Add(asmRef);
                            break;
                    }
                }
            }

            var topLevel = scan.types.Values.Where(t => !t.fullName.Contains('/')).OrderBy(t => t.fullName, StringComparer.Ordinal).ToList();

            // Partial parts must end up in the same assembly and the same namespace.
            foreach (var t in topLevel.Where(t => t.filePaths.Count > 1))
            {
                var parts = t.filePaths.Select(FileAt).Where(p => p != null)
                    .Select(p => (file: p, module: ModuleOf(p)?.name ?? "unassigned", ns: FinalNs(p, t))).ToList();
                if (parts.Select(p => (p.module, p.ns)).Distinct().Count() < 2) continue;
                Add(Severity.Error, IssueCode.PartialSplit, parts[0].file.path,
                    $"Partial type {t.fullName} is split: " + string.Join(", ", parts.Select(p => $"{p.file.path} ({p.module}, namespace '{p.ns}')")) + ".");
            }

            // Namespace of every type after Apply; two different types landing on the same name clash.
            var moved = fates.MovedTypes();
            var finals = new Dictionary<string, FinalType>();
            foreach (var t in topLevel)
            {
                var tf = t.filePaths.Count > 0 ? FileAt(t.filePaths[0]) : null;
                var changed = moved.ContainsKey(t.fullName);
                finals[t.fullName] = new FinalType
                {
                    fullName = t.fullName, ns = changed ? moved[t.fullName].newNs : t.ns ?? "", where = tf?.path ?? t.assemblyName, changed = changed,
                };
            }
            var bySimpleName = finals.Values.ToLookup(x => SimpleName(x.fullName));
            foreach (var g in finals.Values.GroupBy(x => (x.ns, SimpleName(x.fullName))))
            {
                if (!g.Any(x => x.changed) || g.Count() < 2) continue;
                Add(Severity.Error, IssueCode.NameClash, g.First(x => x.changed).where,
                    $"Name clash in namespace '{g.Key.ns}': " + string.Join(", ", g.Select(x => $"{x.fullName} ({x.where})")) + ".");
            }

            var movesAssembly = files.Any(f => ModuleOf(f) != null && (f.kind is AssemblyKind.Runtime or AssemblyKind.Editor));
            foreach (var f in files.Where(f => IsAssignable(f, cfg)))
            {
                // A `using` Apply adds must not make a name F already resolves elsewhere ambiguous (CS0104).
                var enclosing = fates.Enclosing(f);
                // Nested refs count as their outer type, even when Cecil registered no record for it (System.Environment/SpecialFolder).
                var refs = f.referencedTypes.Where(scan.types.ContainsKey).Select(Outer).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
                var added = new HashSet<string>(fates.Usings(f).add);
                var unqualified = added.Count == 0 ? null : UnqualifiedNames(Source(f));
                foreach (var rn in added.Count == 0 ? new List<string>() : refs)
                {
                    var rNs = finals.TryGetValue(rn, out var fr) ? fr.ns : NsOf(rn);
                    if (enclosing.Contains(rNs) || !unqualified.Contains(StripArity(SimpleName(rn)))) continue;
                    var other = bySimpleName[SimpleName(rn)].FirstOrDefault(x => x.ns != rNs && added.Contains(x.ns));
                    if (other == null) continue;
                    Add(Severity.Error, IssueCode.NameClash, f.path,
                        $"{f.path} uses {rn}, but Apply adds 'using {other.ns};' which also has {SimpleName(rn)} ({other.fullName}, {other.where}); the name becomes ambiguous.");
                }

                // Text-level leftovers Apply will not rewrite.
                var uses = f.referencedTypes.Concat(f.declaredTypes).Select(Outer).Distinct().Where(moved.ContainsKey).ToList();
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

            ScriptFile FileAt(string path) => fates.FileAt(path);

            Module ModuleOfGuid(string guid) => fates.ModuleOf(guid);

            Module ModuleOf(ScriptFile f) => fates.ModuleOf(f);

            string Node(ScriptFile f) => ModuleOf(f).name + (f.IsEditor ? ".Editor" : "");

            string Source(ScriptFile f)
            {
                if (!sources.TryGetValue(f.path, out var s)) s = sources[f.path] = readSource(f.path) ?? "";
                return s;
            }

            NamespaceInfo Info(ScriptFile f) => fates.Info(f);

            string FinalNs(ScriptFile f, TypeRecord t) => Changes(f, out _, out var n) ? n : t.ns ?? "";

            bool Changes(ScriptFile f, out string oldNs, out string newNs) => fates.Changes(f, out oldNs, out newNs);

            // Apply never edits files outside scope, so they must keep compiling with the moved types' new namespaces.
            void CheckReverseRefs(ScriptFile f)
            {
                foreach (var tn in f.referencedTypes.OrderBy(t => t, StringComparer.Ordinal))
                {
                    if (!scan.types.TryGetValue(tn, out var t)) continue;
                    foreach (var tf in t.filePaths.Select(FileAt))
                    {
                        if (tf == null || !Changes(tf, out var oldNs, out var newNs)) continue;
                        var owner = ModuleOf(tf);
                        Add(Severity.Error, IssueCode.ReverseRefOutOfScope, f.path,
                            $"{f.path} (outside scope) uses {tn} from {tf.path}, whose namespace changes '{oldNs}' → '{newNs}'. Add the file's folder to the scope" +
                            (owner != null ? $" or turn off rewriteNamespace for {owner.name}." : "."));
                        break;
                    }
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

        internal static string Outer(string typeName) => typeName.Split('/')[0];

        static string NsOf(string fullName) => fullName.Contains('.') ? fullName.Substring(0, fullName.LastIndexOf('.')) : "";

        static readonly Regex IdentRx = new Regex(@"(?<![\w@])(?<!(?:\.|::)\s*)@?([\p{L}_]\w*)", RegexOptions.Compiled);
        static readonly Regex AliasRx = new Regex(@"(?<![\w@.])using\s+@?([\p{L}_]\w*)\s*=", RegexOptions.Compiled);

        // Identifiers the code writes unqualified (not right after `.` or `::`), minus using aliases, which bind before
        // imported namespaces. Comments, literals and preprocessor lines are masked; every #if branch is kept.
        static HashSet<string> UnqualifiedNames(string src)
        {
            var mask = DeclarationFinder.Strip(src, null, new bool[src.Length]);
            var aliases = new HashSet<string>(AliasRx.Matches(mask).Cast<Match>().Select(m => m.Groups[1].Value));
            return new HashSet<string>(IdentRx.Matches(mask).Cast<Match>().Select(m => m.Groups[1].Value).Where(n => !aliases.Contains(n)));
        }

        static string SimpleName(string fullName) => fullName.Substring(fullName.LastIndexOf('.') + 1); // keeps `1: Box`1 != Box

        static string StripArity(string name) => name.Split('`')[0];

        // "A.B.C" -> "A.B.C", "A.B", "A"
        internal static IEnumerable<string> Prefixes(string ns)
        {
            for (var n = ns; !string.IsNullOrEmpty(n); n = n.Contains('.') ? n.Substring(0, n.LastIndexOf('.')) : "")
                yield return n;
        }
    }
}
