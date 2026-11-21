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
                if (op.setNamespace != null || op.addUsings.Count + op.removeUsings.Count + op.movedFrom.Count > 0) plan.edits.Add(op);
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
        // planned for it, else by asmdef name), outside asmdefs, extraReferences, and for Editor its own runtime asmdef.
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
            if (editor) refs.Add(Ref(m, false));
            return refs.Where(r => !string.IsNullOrEmpty(r)).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();

            string Ref(Module t, bool ed)
            {
                var guid = ed ? t.editorAsmdefGuid : t.asmdefGuid;
                if (string.IsNullOrEmpty(guid) && plannedGuidByNode != null) plannedGuidByNode.TryGetValue(Node(t, ed), out guid);
                return !string.IsNullOrEmpty(guid) ? "GUID:" + guid : PathRules.AsmdefName(t, ed);
            }
        }

        static string NormRef(string r) => r != null && GuidRx.IsMatch(r) ? "GUID:" + r : r;

        static string Node(Module m, bool editor) => m.name + (editor ? ".Editor" : "");

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
