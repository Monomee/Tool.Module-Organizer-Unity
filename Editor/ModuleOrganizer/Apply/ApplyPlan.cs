using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;

namespace Monome.UtilityPack.ModuleOrganizer
{
    public class MoveOp
    {
        public string guid;
        public string from;
        public string to;

        public override string ToString() => $"{from} -> {to}";
    }

    public class AsmdefOp
    {
        public string path;
        public string guid; // existing GUID, or one allocated by the planner: the executor writes it into the new .meta
        public string name;
        public string rootNamespace;
        public bool editorOnly;
        public List<string> references = new List<string>(); // "GUID:..." (planned GUIDs included) or an assembly name
        public bool delete;
        public string moduleName; // null for a retired asmdef
    }

    public class TextEditOp
    {
        public string guid;
        public string setNamespace; // null = unchanged, "" = back to global
        public List<string> addUsings = new List<string>();
        public List<string> removeUsings = new List<string>();
        public bool resetAliases; // drop the file's marked (`// monome:alias`) aliases first, then add `aliases`
        public List<(string alias, string target)> aliases = new List<(string, string)>(); // marked, inside the namespace block (ApplyPlanner ShadowAliases)
        public List<(string type, string oldNs, string oldAsm)> movedFrom = new List<(string, string, string)>(); // type = simple name, Cecil arity kept
    }

    // Executed in field order: create folders; moves; asmdefs (write the .asmdef, plus a .meta carrying op.guid when the
    // file is new; delete ops remove the file); edits; delete emptied folders; drop config entries.
    // GUIDs are known up front, so asmdefs reference each other by GUID even on first Apply.
    public class ApplyPlan
    {
        public List<string> createFolders = new List<string>(); // parent first
        public List<MoveOp> moves = new List<MoveOp>(); // scripts and tool asmdefs (GUID kept)
        public List<AsmdefOp> asmdefs = new List<AsmdefOp>();
        public List<TextEditOp> edits = new List<TextEditOp>();
        public List<string> deleteFoldersIfEmpty = new List<string>(); // deepest first
        public List<Issue> issues = new List<Issue>();
        public List<string> removeEntries = new List<string>(); // pendingRemovals GUIDs to drop after Apply
        public List<string> clearRetiredAsmdefGuids = new List<string>();
        // "oldNs|oldAsm|className" of every MovedFrom emitted: assets holding [SerializeReference] data under these
        // names must be re-serialized after compile (Task 9).
        public List<string> reserializeTypeKeys = new List<string>();

        public bool IsEmpty => createFolders.Count + moves.Count + asmdefs.Count + edits.Count + deleteFoldersIfEmpty.Count +
                               removeEntries.Count + clearRetiredAsmdefGuids.Count == 0;
    }

    public class DiskState
    {
        public Dictionary<string, string> pathByGuid = new Dictionary<string, string>(); // scripts and asmdefs
        public Dictionary<string, string> sourceByGuid = new Dictionary<string, string>(); // .cs text; missing = not rewritable
        public HashSet<string> existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // files and folders
        public Dictionary<string, string> asmdefTextByGuid = new Dictionary<string, string>();

        public static DiskState Capture(ScanResult scan, Func<string, string> readSource)
        {
            var d = new DiskState();
            foreach (var f in scan.files.Values)
            {
                d.pathByGuid[f.guid] = f.path;
                d.sourceByGuid[f.guid] = readSource(f.path);
            }
            foreach (var p in AssetDatabase.GetAllAssetPaths())
            {
                d.existingPaths.Add(p);
                // ponytail: reads every asmdef under Assets/; pass the config in if that gets slow.
                if (!p.StartsWith("Assets/", StringComparison.Ordinal) || !p.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase)) continue;
                var g = AssetDatabase.AssetPathToGUID(p);
                d.pathByGuid[g] = p;
                d.asmdefTextByGuid[g] = File.ReadAllText(p);
            }
            return d;
        }
    }

    // Diff of config vs disk: first Apply and re-Apply share this path; nothing changed = empty plan (spec 5.5).
    // Only files ModuleValidator.IsAssignable accepts are ever moved or edited.
    public static class ApplyPlanner
    {
        static readonly Regex GuidRx = new Regex("^[0-9a-fA-F]{32}$");

        public static ApplyPlan Build(ModuleConfig cfg, ScanResult scan, ValidationReport report, DiskState disk, Func<string> newGuid = null)
        {
            newGuid ??= () => Guid.NewGuid().ToString("N");
            var plan = new ApplyPlan();
            var fates = new FileFates(cfg, scan, f => disk.sourceByGuid.TryGetValue(f.guid, out var src) ? src : null);
            var moved = fates.MovedTypes();
            var files = scan.files.Values.Where(f => ModuleValidator.IsAssignable(f, cfg)).OrderBy(f => f.path, StringComparer.Ordinal).ToList();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cleanup = new HashSet<string>();
            var roots = new HashSet<string>(cfg.scopeFolders.Select(PathRules.Norm).Append(PathRules.Norm(cfg.EffectiveModulesRoot)));
            List<FinalType> finalTypes = null;

            foreach (var f in files)
            {
                var to = fates.TargetPath(f);
                var from = CurrentPath(f.guid) ?? f.path;
                if (to == null || string.Equals(to, from, StringComparison.Ordinal)) continue;
                if (Taken(to, from))
                    Error(fates.ModuleOf(f) == null ? IssueCode.RestoreConflict : IssueCode.PathConflict, from,
                        $"Cannot move {from} to {to}: that path is already taken.");
                plan.moves.Add(new MoveOp { guid = f.guid, from = from, to = to });
                Need(Dir(to));
                if (f.kind == AssemblyKind.AsmDef) Cleanup(Dir(from), ModuleFolderOf(CurrentPath(f.asmdefGuid ?? ""))); // leaving a module folder
            }

            // [MovedFrom] keeps [SerializeReference] data; only on the type's first file so partials don't repeat it (CS0579).
            var serializableByFirstFile = scan.types.Values.Where(t => t.serializableNonUnityObject && t.filePaths.Count > 0)
                .ToLookup(t => PathRules.Norm(t.filePaths[0]));
            foreach (var f in files)
            {
                var op = new TextEditOp { guid = f.guid };
                if (fates.Changes(f, out _, out var newNs)) op.setNamespace = newNs;
                (op.addUsings, op.removeUsings) = fates.Usings(f);
                op.removeUsings = op.removeUsings.Union(UnreachableUsings(f)).OrderBy(n => n, StringComparer.Ordinal).ToList();
                // Marked aliases are recomputed from scratch; the file is edited only when the set changes.
                var src = disk.sourceByGuid.TryGetValue(f.guid, out var text) ? text : null;
                var needed = src == null ? new List<(string, string)>() : ShadowAliases(f, SourceRewriter.RemoveMarkedAliases(src));
                if (src != null && !SourceRewriter.MarkedAliases(src).SequenceEqual(needed))
                {
                    op.resetAliases = true;
                    op.aliases = needed;
                }
                var asm = fates.TargetAssembly(f);
                foreach (var t in serializableByFirstFile[f.path].OrderBy(t => t.fullName, StringComparer.Ordinal))
                {
                    var oldNs = t.ns ?? "";
                    var tNs = moved.TryGetValue(ModuleValidator.Outer(t.fullName), out var mv) ? mv.newNs : oldNs;
                    if (tNs == oldNs && (asm == null || asm == t.assemblyName)) continue;
                    op.movedFrom.Add((SimpleName(t.fullName), oldNs, t.assemblyName));
                    var className = oldNs.Length == 0 ? t.fullName : t.fullName.Substring(oldNs.Length + 1);
                    plan.reserializeTypeKeys.Add(oldNs + "|" + t.assemblyName + "|" + className);
                }
                if (op.setNamespace != null || op.resetAliases || op.addUsings.Count + op.removeUsings.Count + op.aliases.Count + op.movedFrom.Count > 0) plan.edits.Add(op);
            }

            // GUIDs first, so every asmdef can reference every other by GUID.
            var wanted = new List<(Module m, bool editor, bool want)>();
            foreach (var m in cfg.modules)
            {
                wanted.Add((m, false, m.scripts.Count > 0 || !string.IsNullOrEmpty(m.asmdefGuid)));
                wanted.Add((m, true, files.Any(f => f.IsEditor && fates.ModuleOf(f) == m)));
            }
            var planned = new Dictionary<string, string>();
            foreach (var (m, editor, _) in wanted.Where(w => w.want))
            {
                var g = editor ? m.editorAsmdefGuid : m.asmdefGuid;
                planned[Node(m, editor)] = string.IsNullOrEmpty(g) ? newGuid() : g;
            }
            foreach (var (m, editor, want) in wanted) Asmdef(m, editor, want);
            foreach (var g in cfg.retiredAsmdefGuids)
            {
                var cur = CurrentPath(g);
                if (cur != null)
                {
                    plan.asmdefs.Add(new AsmdefOp { path = cur, guid = g, delete = true });
                    Cleanup(Dir(cur), ModuleFolderOf(cur));
                }
                plan.clearRetiredAsmdefGuids.Add(g);
            }
            plan.removeEntries = cfg.pendingRemovals.Select(e => e.guid).Distinct().ToList();
            plan.reserializeTypeKeys = plan.reserializeTypeKeys.Distinct().ToList();
            plan.deleteFoldersIfEmpty = cleanup.OrderByDescending(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal).ToList();
            return plan;

            void Asmdef(Module m, bool editor, bool want)
            {
                if (!want)
                {
                    var old = editor ? m.editorAsmdefGuid : m.asmdefGuid;
                    var oldPath = string.IsNullOrEmpty(old) ? null : CurrentPath(old);
                    if (oldPath == null) return;
                    plan.asmdefs.Add(new AsmdefOp { path = oldPath, guid = old, editorOnly = editor, delete = true, moduleName = m.name });
                    Cleanup(Dir(oldPath), ModuleFolderOf(oldPath));
                    return;
                }
                var guid = planned[Node(m, editor)];
                var cur = CurrentPath(guid);
                var name = PathRules.AsmdefName(m, editor);
                var op = new AsmdefOp
                {
                    path = PathRules.AsmdefPath(m, editor), guid = guid, name = name, rootNamespace = name,
                    editorOnly = editor, references = ComputeReferences(m, editor, report, cfg, planned), moduleName = m.name,
                };
                if (string.Equals(cur, op.path, StringComparison.Ordinal))
                {
                    if (disk.asmdefTextByGuid.TryGetValue(guid, out var text) && Same(text, AsmdefJson.Write(op))) return;
                }
                else
                {
                    if (Taken(op.path, cur))
                        Error(IssueCode.PathConflict, op.path, $"Cannot write module {m.name}'s asmdef at {op.path}: that path is already taken.");
                    if (cur != null)
                    {
                        plan.moves.Add(new MoveOp { guid = guid, from = cur, to = op.path });
                        Cleanup(Dir(cur), ModuleFolderOf(cur));
                    }
                }
                Need(Dir(op.path));
                plan.asmdefs.Add(op);
            }

            // `using N;` in a module file whose namespace, after Apply, only lives in modules its asmdef will not reference
            // (the type it used from there moved away): CS0234. Namespaces with any type outside modules are kept.
            List<string> UnreachableUsings(ScriptFile f)
            {
                if (fates.ModuleOf(f) == null) return new List<string>(); // Assembly-CSharp(-Editor) sees every auto-referenced asmdef
                var sees = Sees(f);
                var enclosing = fates.Enclosing(f);
                return fates.FileUsings(f).Where(n => !enclosing.Contains(n)).Where(n =>
                {
                    var holders = FinalTypes().Where(t => t.ns == n || t.ns.StartsWith(n + ".", StringComparison.Ordinal)).ToList();
                    return holders.Count > 0 && holders.All(t => t.node != null && !sees(t));
                }).ToList();
            }

            // C# looks a simple name up in each enclosing namespace's members, child namespaces included, before the file's
            // top-level usings and aliases: in Game.Combat.Editor a plain `Editor` is the namespace Game.Combat.Editor, not
            // UnityEditor.Editor (CS0118). For each name f writes unqualified whose referenced type a namespace visible to f's
            // assembly shadows after Apply: (alias, global:: full name), for a marked alias inside the namespace block.
            // src is f's source without its marked aliases; its own aliases inside the block protect their names. Sorted.
            // ponytail: an unqualified name used as a namespace qualifier (`Editor.Util` for a namespace) next to a reference
            // to a type of that name gets an alias it does not want; a shadowed name used both ways cannot be fixed by text.
            List<(string, string)> ShadowAliases(ScriptFile f, string src)
            {
                var result = new List<(string, string)>();
                var final = fates.Changes(f, out _, out var n) ? n : FileFates.CurrentNamespace(fates.Info(f));
                if (string.IsNullOrEmpty(final)) return result; // global (nothing encloses it) or not rewritable
                var sees = Sees(f);
                var namespaces = new HashSet<string>(FinalTypes().Where(sees).SelectMany(t => ModuleValidator.Prefixes(t.ns)));
                namespaces.UnionWith(ModuleValidator.Prefixes(final));
                var names = ModuleValidator.UnqualifiedNames(src, false);
                names.ExceptWith(SourceRewriter.BlockAliasNames(src));
                var refs = f.referencedTypes.Select(ModuleValidator.Outer).Distinct().Where(r => !r.Contains('`'))
                    .Select(r => (simple: ModuleValidator.SimpleName(r),
                        ns: moved.TryGetValue(r, out var mv) ? mv.newNs : scan.types.TryGetValue(r, out var t) ? t.ns ?? "" : ModuleValidator.NsOf(r)))
                    .Where(r => names.Contains(r.simple));
                foreach (var g in refs.GroupBy(r => r.simple).Where(g => g.Count() == 1))
                {
                    var r = g.Single();
                    if (Shadowed(final, r.ns, r.simple, namespaces))
                        result.Add((r.simple, "global::" + (r.ns.Length == 0 ? "" : r.ns + ".") + r.simple));
                }
                return result.OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();
            }

            // Which top-level types (after Apply) f's assembly can see. A module asmdef: its own node, the modules and outside
            // asmdefs it references (report edges/asmdefRefs, extra references; an Editor asmdef also its runtime one).
            // Assembly-CSharp: everything that is not Editor-only. Assembly-CSharp-Editor: everything.
            Func<FinalType, bool> Sees(ScriptFile f)
            {
                var m = fates.ModuleOf(f);
                if (m == null) return f.IsEditor ? (Func<FinalType, bool>)(_ => true) : t => !t.editor;
                var node = Node(m, f.IsEditor);
                var nodes = new HashSet<string>(report.edges.Where(e => e.from == node).Select(e => e.to)) { node };
                if (f.IsEditor) nodes.Add(m.name);
                var refs = new HashSet<string>(m.extraReferences.Concat(f.IsEditor ? m.editorExtraReferences : new List<string>())
                    .Concat(report.asmdefRefs.TryGetValue(node, out var outside) ? outside : new HashSet<string>()).Select(NormRef));
                foreach (var x in cfg.modules)
                foreach (var ed in new[] { false, true })
                {
                    var g = ed ? x.editorAsmdefGuid : x.asmdefGuid;
                    if (refs.Contains(PathRules.AsmdefName(x, ed)) || (!string.IsNullOrEmpty(g) && refs.Contains("GUID:" + g))) nodes.Add(Node(x, ed));
                }
                return t => t.node != null
                    ? nodes.Contains(t.node)
                    : t.asmdefGuid != null && (refs.Contains("GUID:" + t.asmdefGuid) || refs.Contains(t.asm));
            }

            List<FinalType> FinalTypes() => finalTypes ??= scan.types.Values.Where(t => !t.fullName.Contains('/')).Select(t =>
            {
                var tf = t.filePaths.Count > 0 ? fates.FileAt(t.filePaths[0]) : null;
                var tm = tf == null ? null : fates.ModuleOf(tf);
                return new FinalType
                {
                    ns = moved.TryGetValue(t.fullName, out var mv) ? mv.newNs : t.ns ?? "",
                    node = tm == null ? null : Node(tm, tf.IsEditor),
                    asm = tf == null ? t.assemblyName : fates.TargetAssembly(tf) ?? t.assemblyName,
                    // ponytail: types of a file-less asmdef (package) count as runtime; their editor-ness is not scanned.
                    editor = tf?.IsEditor ?? (t.kind is AssemblyKind.Editor or AssemblyKind.FirstPassEditor),
                    asmdefGuid = tf == null || tm == null && fates.TargetAssembly(tf) == null ? t.asmdefGuid : null,
                };
            }).ToList();

            string CurrentPath(string guid) => disk.pathByGuid.TryGetValue(guid, out var p) ? p : null;

            // Taken by another file (a case-only rename of the file itself is not a conflict), or already planned.
            bool Taken(string to, string from) =>
                (disk.existingPaths.Contains(to) && !string.Equals(to, from, StringComparison.OrdinalIgnoreCase)) || !targets.Add(to);

            // Missing folders on the way to dir, parent first.
            void Need(string dir)
            {
                var chain = new List<string>();
                for (var d = dir; d.Contains('/') && !disk.existingPaths.Contains(d); d = Dir(d)) chain.Add(d);
                chain.Reverse();
                foreach (var d in chain)
                    if (!plan.createFolders.Contains(d)) plan.createFolders.Add(d);
            }

            // dir and its parents up to the module folder, only strictly inside a scope folder or the modules root.
            void Cleanup(string dir, string moduleFolder)
            {
                if (moduleFolder == null) return;
                for (var d = dir; d == moduleFolder || PathRules.IsUnder(d, moduleFolder); d = Dir(d))
                    if (!roots.Contains(d) && roots.Any(r => PathRules.IsUnder(d, r))) cleanup.Add(d);
            }

            void Error(IssueCode code, string path, string message) =>
                plan.issues.Add(new Issue { severity = Severity.Error, code = code, path = path, message = message });
        }

        // References of a module's runtime or Editor asmdef: modules its files use (by GUID: the module's, else the one
        // planned for it, else by asmdef name), outside asmdefs, extraReferences, and for Editor editorExtraReferences and
        // its own runtime asmdef.
        // Raw 32-hex GUIDs become "GUID:...". Sorted, distinct.
        public static List<string> ComputeReferences(Module m, bool editor, ValidationReport report, ModuleConfig cfg,
            IReadOnlyDictionary<string, string> plannedGuidByNode = null)
        {
            var node = Node(m, editor);
            var refs = new List<string>();
            foreach (var e in report.edges.Where(e => e.from == node))
            {
                var target = cfg.modules.FirstOrDefault(x => x.name == e.to);
                var toEditor = target == null;
                target ??= cfg.modules.FirstOrDefault(x => Node(x, true) == e.to);
                if (target != null) refs.Add(Ref(target, toEditor));
            }
            if (report.asmdefRefs.TryGetValue(node, out var outside)) refs.AddRange(outside.Select(NormRef));
            refs.AddRange(m.extraReferences.Select(NormRef));
            if (editor) refs.AddRange(m.editorExtraReferences.Select(NormRef));
            if (editor) refs.Add(Ref(m, false));
            return refs.Where(r => !string.IsNullOrEmpty(r)).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();

            string Ref(Module t, bool ed)
            {
                var guid = ed ? t.editorAsmdefGuid : t.asmdefGuid;
                if (string.IsNullOrEmpty(guid) && plannedGuidByNode != null) plannedGuidByNode.TryGetValue(Node(t, ed), out guid);
                return !string.IsNullOrEmpty(guid) ? "GUID:" + guid : PathRules.AsmdefName(t, ed);
            }
        }

        internal static string NormRef(string r) => r != null && GuidRx.IsMatch(r) ? "GUID:" + r : r;

        static string Node(Module m, bool editor) => m.name + (editor ? ".Editor" : "");

        // A top-level type after Apply: namespace and where it lives (module node, else assembly; asmdefGuid for an outside asmdef).
        class FinalType
        {
            public string ns, node, asm, asmdefGuid;
            public bool editor;
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

        // Tool layout: {folder}/X.asmdef or {folder}/Editor/X.Editor.asmdef.
        static string ModuleFolderOf(string asmdefPath)
        {
            if (asmdefPath == null) return null;
            var d = Dir(asmdefPath);
            return d.EndsWith("/Editor", StringComparison.Ordinal) ? Dir(d) : d;
        }

        static bool Same(string a, string b) => a.Replace("\r\n", "\n").Trim() == b.Replace("\r\n", "\n").Trim();

        static string Dir(string path) => path.Contains('/') ? path.Substring(0, path.LastIndexOf('/')) : "";

        static string SimpleName(string fullName) => fullName.Substring(Math.Max(fullName.LastIndexOf('.'), fullName.LastIndexOf('/')) + 1);
    }
}
