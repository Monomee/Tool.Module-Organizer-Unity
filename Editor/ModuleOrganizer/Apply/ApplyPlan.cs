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
        public string name;
        public string rootNamespace;
        public bool editorOnly;
        public List<string> references = new List<string>(); // "GUID:..." or the name of an asmdef that has no GUID yet
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

    // Executed in field order: create folders, moves, asmdefs, edits, delete emptied folders, then drop config entries.
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

        public bool IsEmpty => createFolders.Count + moves.Count + asmdefs.Count + edits.Count + deleteFoldersIfEmpty.Count +
                               removeEntries.Count + clearRetiredAsmdefGuids.Count == 0;
    }

    public class DiskState
    {
        public Dictionary<string, string> pathByGuid = new Dictionary<string, string>(); // scripts and asmdefs
        public Dictionary<string, string> namespaceByGuid = new Dictionary<string, string>(); // "" global, null not rewritable
        public HashSet<string> existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); // files and folders
        public Dictionary<string, string> asmdefTextByGuid = new Dictionary<string, string>();

        public static DiskState Capture(ScanResult scan, Func<string, string> readSource)
        {
            var d = new DiskState();
            foreach (var f in scan.files.Values)
            {
                d.pathByGuid[f.guid] = f.path;
                d.namespaceByGuid[f.guid] = FileFates.CurrentNamespace(SourceRewriter.Parse(readSource(f.path) ?? ""));
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

        public static ApplyPlan Build(ModuleConfig cfg, ScanResult scan, ValidationReport report, DiskState disk)
        {
            var plan = new ApplyPlan();
            var fates = new FileFates(cfg, scan, f => disk.namespaceByGuid.TryGetValue(f.guid, out var n) ? n : null);
            var moved = fates.MovedTypes();
            var files = scan.files.Values.Where(f => ModuleValidator.IsAssignable(f, cfg)).OrderBy(f => f.path, StringComparer.Ordinal).ToList();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var cleanup = new HashSet<string>();
            var stops = new HashSet<string>(cfg.scopeFolders.Select(PathRules.Norm).Append(PathRules.Norm(cfg.EffectiveModulesRoot)));

            foreach (var f in files)
            {
                var to = fates.TargetPath(f);
                var from = CurrentPath(f.guid) ?? f.path;
                if (to == null || to == from) continue;
                var restoring = fates.ModuleOf(f) == null;
                if (disk.existingPaths.Contains(to) || !targets.Add(to))
                    Error(restoring ? IssueCode.RestoreConflict : IssueCode.PathConflict, from,
                        $"Cannot move {from} to {to}: that path is already taken.");
                plan.moves.Add(new MoveOp { guid = f.guid, from = from, to = to });
                Need(Dir(to));
                if (f.kind == AssemblyKind.AsmDef) Cleanup(Dir(from)); // leaving a module folder
            }

            // Namespaces left with no type after Apply: their usings must go (CS0246).
            var remaining = new HashSet<string>(scan.types.Values.Select(t =>
                moved.TryGetValue(ModuleValidator.Outer(t.fullName), out var mv) ? mv.newNs : t.ns ?? ""));
            foreach (var f in files)
            {
                var op = new TextEditOp { guid = f.guid };
                string ownNs;
                if (fates.Changes(f, out _, out var newNs)) ownNs = op.setNamespace = newNs;
                else ownNs = CurrentNs(f.guid);
                // C# resolves the file's own and enclosing namespaces without a using.
                var enclosing = new HashSet<string>(ModuleValidator.Prefixes(ownNs ?? "")) { "" };
                var refs = f.referencedTypes.Where(scan.types.ContainsKey).Select(ModuleValidator.Outer).Distinct().Where(moved.ContainsKey).ToList();
                op.addUsings = refs.Select(n => moved[n].newNs).Where(n => !enclosing.Contains(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
                op.removeUsings = refs.Select(n => moved[n].oldNs).Where(n => n.Length > 0 && !remaining.Contains(n)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();

                // [MovedFrom] keeps [SerializeReference] data; only on the type's first file so partials don't repeat it (CS0579).
                var asm = fates.TargetAssembly(f);
                foreach (var t in scan.types.Values.Where(t => t.serializableNonUnityObject && t.filePaths.Count > 0 && fates.FileAt(t.filePaths[0]) == f)
                             .OrderBy(t => t.fullName, StringComparer.Ordinal))
                {
                    var oldNs = t.ns ?? "";
                    var tNs = moved.TryGetValue(ModuleValidator.Outer(t.fullName), out var mv) ? mv.newNs : oldNs;
                    if (tNs != oldNs || (asm != null && asm != t.assemblyName))
                        op.movedFrom.Add((SimpleName(t.fullName), oldNs, t.assemblyName));
                }
                if (op.setNamespace != null || op.addUsings.Count + op.removeUsings.Count + op.movedFrom.Count > 0) plan.edits.Add(op);
            }

            foreach (var m in cfg.modules)
            {
                Asmdef(m, false, m.scripts.Count > 0 || !string.IsNullOrEmpty(m.asmdefGuid));
                Asmdef(m, true, files.Any(f => f.IsEditor && fates.ModuleOf(f) == m));
            }
            foreach (var g in cfg.retiredAsmdefGuids)
            {
                var cur = CurrentPath(g);
                if (cur != null)
                {
                    plan.asmdefs.Add(new AsmdefOp { path = cur, delete = true });
                    Cleanup(Dir(cur));
                }
                plan.clearRetiredAsmdefGuids.Add(g);
            }
            plan.removeEntries = cfg.pendingRemovals.Select(e => e.guid).Distinct().ToList();
            plan.deleteFoldersIfEmpty = cleanup.OrderByDescending(d => d.Count(c => c == '/')).ThenBy(d => d, StringComparer.Ordinal).ToList();
            return plan;

            void Asmdef(Module m, bool editor, bool wanted)
            {
                var guid = editor ? m.editorAsmdefGuid : m.asmdefGuid;
                var cur = string.IsNullOrEmpty(guid) ? null : CurrentPath(guid);
                if (!wanted)
                {
                    if (cur == null) return;
                    plan.asmdefs.Add(new AsmdefOp { path = cur, editorOnly = editor, delete = true, moduleName = m.name });
                    Cleanup(Dir(cur));
                    return;
                }
                var name = m.namespaceName + (editor ? ".Editor" : "");
                var op = new AsmdefOp
                {
                    path = PathRules.Norm(m.folder) + (editor ? "/Editor/" : "/") + name + ".asmdef", name = name, rootNamespace = name,
                    editorOnly = editor, references = ComputeReferences(m, editor, report, cfg), moduleName = m.name,
                };
                if (cur == op.path)
                {
                    if (disk.asmdefTextByGuid.TryGetValue(guid, out var text) && Same(text, AsmdefJson.Write(op))) return;
                }
                else
                {
                    if (disk.existingPaths.Contains(op.path) || !targets.Add(op.path))
                        Error(IssueCode.PathConflict, op.path, $"Cannot write module {m.name}'s asmdef at {op.path}: that path is already taken.");
                    if (cur != null)
                    {
                        plan.moves.Add(new MoveOp { guid = guid, from = cur, to = op.path });
                        Cleanup(Dir(cur));
                    }
                }
                Need(Dir(op.path));
                plan.asmdefs.Add(op);
            }

            string CurrentPath(string guid) => disk.pathByGuid.TryGetValue(guid, out var p) ? p : null;

            string CurrentNs(string guid) => disk.namespaceByGuid.TryGetValue(guid, out var n) ? n : null;

            // Missing folders on the way to dir, parent first.
            void Need(string dir)
            {
                var chain = new List<string>();
                for (var d = dir; d.Contains('/') && !disk.existingPaths.Contains(d); d = Dir(d)) chain.Add(d);
                chain.Reverse();
                foreach (var d in chain)
                    if (!plan.createFolders.Contains(d)) plan.createFolders.Add(d);
            }

            // dir and its parents, up to (not incl.) a scope folder, the modules root or a top-level folder.
            void Cleanup(string dir)
            {
                for (var d = dir; d.Contains('/') && !stops.Contains(d); d = Dir(d)) cleanup.Add(d);
            }

            void Error(IssueCode code, string path, string message) =>
                plan.issues.Add(new Issue { severity = Severity.Error, code = code, path = path, message = message });
        }

        // References of a module's runtime or Editor asmdef: modules its files use (by GUID, or by asmdef name before
        // that asmdef has one), outside asmdefs, extraReferences, and for Editor its own runtime asmdef. Sorted, distinct.
        public static List<string> ComputeReferences(Module m, bool editor, ValidationReport report, ModuleConfig cfg)
        {
            var node = m.name + (editor ? ".Editor" : "");
            var refs = new List<string>();
            foreach (var e in report.edges.Where(e => e.from == node))
            {
                var target = cfg.modules.FirstOrDefault(x => x.name == e.to);
                var toEditor = target == null;
                target ??= cfg.modules.FirstOrDefault(x => x.name + ".Editor" == e.to);
                if (target != null) refs.Add(Ref(target, toEditor));
            }
            if (report.asmdefRefs.TryGetValue(node, out var outside)) refs.AddRange(outside.Select(r => GuidRx.IsMatch(r) ? "GUID:" + r : r));
            refs.AddRange(m.extraReferences);
            if (editor) refs.Add(Ref(m, false));
            return refs.Where(r => !string.IsNullOrEmpty(r)).Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        }

        static string Ref(Module m, bool editor)
        {
            var guid = editor ? m.editorAsmdefGuid : m.asmdefGuid;
            return !string.IsNullOrEmpty(guid) ? "GUID:" + guid : m.namespaceName + (editor ? ".Editor" : "");
        }

        static bool Same(string a, string b) => a.Replace("\r\n", "\n").Trim() == b.Replace("\r\n", "\n").Trim();

        static string Dir(string path) => path.Contains('/') ? path.Substring(0, path.LastIndexOf('/')) : "";

        static string SimpleName(string fullName) => fullName.Substring(Math.Max(fullName.LastIndexOf('.'), fullName.LastIndexOf('/')) + 1);
    }
}
