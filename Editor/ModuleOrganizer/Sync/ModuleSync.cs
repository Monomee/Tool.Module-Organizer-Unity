using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer
{
    public class AsmdefSnapshot
    {
        public string guid;
        public string path;
        public string name;
        public string rootNamespace;
        public List<string> references = new List<string>();
    }

    public class DiskSnapshot
    {
        public Dictionary<string, string> scriptPathByGuid = new Dictionary<string, string>(); // config scripts + .cs under module folders
        public Dictionary<string, AsmdefSnapshot> asmdefsByGuid = new Dictionary<string, AsmdefSnapshot>(); // config asmdefs + asmdefs under module folders
        public Dictionary<string, string> sourceByGuid = new Dictionary<string, string>(); // .cs text of every script under a module folder

        [Serializable]
        class AsmdefDto
        {
            public string name;
            public string rootNamespace;
            public List<string> references = new List<string>();
        }

        public static DiskSnapshot Capture(ModuleConfig cfg)
        {
            var d = new DiskSnapshot();
            var folders = cfg.modules.Select(m => PathRules.Norm(m.folder)).Where(AssetDatabase.IsValidFolder).Distinct().ToArray();
            foreach (var g in cfg.modules.SelectMany(m => m.scripts).Select(e => e.guid)) Script(g, false);
            foreach (var g in cfg.modules.SelectMany(m => new[] { m.asmdefGuid, m.editorAsmdefGuid })) Asmdef(g);
            if (folders.Length > 0)
            {
                foreach (var g in AssetDatabase.FindAssets("t:Script", folders)) Script(g, true);
                foreach (var g in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", folders)) Asmdef(g);
            }
            return d;

            void Script(string g, bool readSource)
            {
                var p = Existing(g, ".cs");
                if (p == null) return;
                d.scriptPathByGuid[g] = p;
                if (readSource) d.sourceByGuid[g] = File.ReadAllText(p);
            }

            void Asmdef(string g)
            {
                var p = Existing(g, ".asmdef");
                if (p == null || d.asmdefsByGuid.ContainsKey(g)) return;
                var dto = JsonUtility.FromJson<AsmdefDto>(File.ReadAllText(p)) ?? new AsmdefDto();
                d.asmdefsByGuid[g] = new AsmdefSnapshot
                    { guid = g, path = p, name = dto.name, rootNamespace = dto.rootNamespace, references = dto.references ?? new List<string>() };
            }
        }

        static string Existing(string guid, string ext)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            var p = AssetDatabase.GUIDToAssetPath(guid);
            return !string.IsNullOrEmpty(p) && p.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && File.Exists(p) ? p : null;
        }
    }

    public enum DriftKind { AsmdefMissing, AsmdefMovedOrRenamed, ScriptDeleted, ScriptAddedInModuleFolder, ScriptMovedOut, ReferencesDiffer }

    public class DriftItem
    {
        public DriftKind kind;
        public string moduleName;
        public string guid; // asmdef GUID for asmdef kinds and ReferencesDiffer, else the script's
        public string detail;
        public List<string> manualReferences = new List<string>(); // ReferencesDiffer: on disk but not computed

        public override string ToString() => $"{kind} {moduleName} {guid}: {detail}";
    }

    // Config vs disk (spec 5.6). Only applied state can drift: modules without asmdefGuid, entries with applied == false
    // and pendingRemovals are drafts. "Re-apply config" is ApplyPlanner.Build + ModuleApplier.Execute.
    public static class ModuleSync
    {
        public static List<DriftItem> Diff(ModuleConfig cfg, DiskSnapshot disk, ValidationReport report)
        {
            var items = new List<DriftItem>();
            var applied = cfg.modules.Where(m => !string.IsNullOrEmpty(m.asmdefGuid)).ToList();
            // A file moved out of its module counts as unassigned, so the module it moved into reports it as added.
            var movedOut = new HashSet<string>(applied.SelectMany(m => m.scripts.Where(e => e.applied &&
                disk.scriptPathByGuid.TryGetValue(e.guid, out var p) && !InModule(m, disk, p)).Select(e => e.guid)));
            var assigned = new HashSet<string>(cfg.modules.SelectMany(m => m.scripts).Concat(cfg.pendingRemovals).Select(e => e.guid)
                .Where(g => !movedOut.Contains(g)));
            foreach (var m in applied)
            {
                Asmdef(m, false, m.asmdefGuid);
                if (!string.IsNullOrEmpty(m.editorAsmdefGuid)) Asmdef(m, true, m.editorAsmdefGuid);
                foreach (var e in m.scripts.Where(e => e.applied))
                {
                    if (!disk.scriptPathByGuid.TryGetValue(e.guid, out var p)) Add(DriftKind.ScriptDeleted, m, e.guid, $"{e.originalPath} no longer exists.");
                    else if (!InModule(m, disk, p)) Add(DriftKind.ScriptMovedOut, m, e.guid, $"{p} is outside {m.folder}.");
                }
                foreach (var kv in disk.scriptPathByGuid.OrderBy(kv => kv.Value, StringComparer.Ordinal))
                    if (!assigned.Contains(kv.Key) && InModule(m, disk, kv.Value))
                        Add(DriftKind.ScriptAddedInModuleFolder, m, kv.Key, $"{kv.Value} is in {m.folder} but not in the module.");
            }
            return items;

            void Asmdef(Module m, bool editor, string guid)
            {
                var name = PathRules.AsmdefName(m, editor);
                if (!disk.asmdefsByGuid.TryGetValue(guid, out var a))
                {
                    Add(DriftKind.AsmdefMissing, m, guid, $"{name}.asmdef no longer exists.");
                    return;
                }
                var path = PathRules.AsmdefPath(m, editor);
                if (PathRules.Norm(a.path) != path || a.name != name)
                    Add(DriftKind.AsmdefMovedOrRenamed, m, guid, $"Expected {name} at {path}, found {a.name} at {a.path}.");
                var expected = ApplyPlanner.ComputeReferences(m, editor, report, cfg);
                var onDisk = a.references.Select(ApplyPlanner.NormRef).ToList();
                if (new HashSet<string>(onDisk).SetEquals(expected)) return;
                var item = Add(DriftKind.ReferencesDiffer, m, guid,
                    $"{a.path} references [{string.Join(", ", onDisk)}], config computes [{string.Join(", ", expected)}].");
                item.manualReferences = onDisk.Except(expected).Distinct().ToList();
            }

            DriftItem Add(DriftKind kind, Module m, string guid, string detail)
            {
                var item = new DriftItem { kind = kind, moduleName = m.name, guid = guid, detail = detail };
                items.Add(item);
                return item;
            }
        }

        public static void AcceptDisk(ModuleConfig cfg, DiskSnapshot disk, IEnumerable<DriftItem> items)
        {
            Undo.RecordObject(cfg, "Sync from disk");
            // Asmdef items first (they can move module.folder, which ScriptMovedOut re-checks), then removals, then
            // additions: a file moved from module A into B's folder must leave A before B can take it.
            var released = new Dictionary<string, ScriptEntry>(); // moved out this pass: history for a module taking them in
            foreach (var it in items.OrderBy(Phase))
            {
                var m = cfg.modules.FirstOrDefault(x => x.name == it.moduleName);
                if (m == null) continue;
                var editor = !string.IsNullOrEmpty(it.guid) && it.guid == m.editorAsmdefGuid;
                switch (it.kind)
                {
                    case DriftKind.AsmdefMissing:
                        // Only the missing one is forgotten; without its runtime asmdef the module's files are drafts again.
                        if (editor) m.editorAsmdefGuid = null;
                        else if (it.guid == m.asmdefGuid)
                        {
                            m.asmdefGuid = null;
                            foreach (var e in m.scripts) e.applied = false;
                        }
                        break;
                    case DriftKind.AsmdefMovedOrRenamed:
                        if (!disk.asmdefsByGuid.TryGetValue(it.guid, out var a)) break;
                        var dir = Dir(PathRules.Norm(a.path));
                        if (editor && dir.EndsWith("/Editor", StringComparison.Ordinal)) dir = Dir(dir);
                        // From the name, not rootNamespace: Diff compares the name to AsmdefName(module).
                        var ns = a.name ?? "";
                        if (editor && ns.EndsWith(".Editor", StringComparison.Ordinal)) ns = ns.Substring(0, ns.Length - ".Editor".Length);
                        // Entries recorded inside the old folder (accepted from disk) follow it: their restore target
                        // moved with the user's rename, and entries without relativeDir derive their subfolder from it.
                        var old = PathRules.Norm(m.folder);
                        foreach (var e in m.scripts.Where(e => PathRules.IsUnder(e.originalPath, old)))
                            e.originalPath = dir + PathRules.Norm(e.originalPath).Substring(old.Length);
                        m.folder = dir;
                        m.namespaceName = ns;
                        break;
                    case DriftKind.ScriptDeleted:
                        m.scripts.RemoveAll(e => e.guid == it.guid);
                        break;
                    case DriftKind.ScriptMovedOut:
                        // Just unassigned: the file stays where the user put it, so no pendingRemovals restore.
                        if (!disk.scriptPathByGuid.TryGetValue(it.guid, out var cur) || InModule(m, disk, cur)) break;
                        foreach (var e in m.scripts.Where(e => e.guid == it.guid)) released[it.guid] = e;
                        m.scripts.RemoveAll(e => e.guid == it.guid);
                        break;
                    case DriftKind.ScriptAddedInModuleFolder:
                        if (!disk.scriptPathByGuid.TryGetValue(it.guid, out var path) || cfg.FindModuleOf(it.guid) != null) break;
                        var fileNs = disk.sourceByGuid.TryGetValue(it.guid, out var s) ? SourceRewriter.Parse(s).namespaces.FirstOrDefault() ?? "" : "";
                        var entry = PathRules.NewEntry(m, it.guid, path, fileNs, cfg.scopeFolders);
                        if (released.TryGetValue(it.guid, out var prev))
                        {
                            // Same file moving A -> B: keep where it came from (restore target), only its place in B is new.
                            entry.originalPath = prev.originalPath;
                            entry.originalNamespace = prev.originalNamespace;
                        }
                        entry.applied = true;
                        m.scripts.Add(entry);
                        break;
                    case DriftKind.ReferencesDiffer:
                        // Hand-added references become (editor)ExtraReferences. References missing on disk stay in the
                        // config: they are computed, and re-applying the config writes them back.
                        var extra = editor ? m.editorExtraReferences : m.extraReferences;
                        foreach (var r in it.manualReferences)
                            if (!extra.Contains(r)) extra.Add(r);
                        break;
                }
            }
            EditorUtility.SetDirty(cfg);
        }

        static int Phase(DriftItem i) => i.kind switch
        {
            DriftKind.AsmdefMissing or DriftKind.AsmdefMovedOrRenamed => 0,
            DriftKind.ScriptDeleted or DriftKind.ScriptMovedOut => 1,
            DriftKind.ScriptAddedInModuleFolder => 2,
            _ => 3,
        };

        // Under the module folder, and not below an asmdef that belongs to another assembly.
        static bool InModule(Module m, DiskSnapshot disk, string path)
        {
            var folder = PathRules.Norm(m.folder);
            if (!PathRules.IsUnder(path, folder)) return false;
            return !disk.asmdefsByGuid.Where(kv => kv.Key != m.asmdefGuid && kv.Key != m.editorAsmdefGuid)
                .Select(kv => Dir(PathRules.Norm(kv.Value.path)))
                .Any(d => (d == folder || PathRules.IsUnder(d, folder)) && PathRules.IsUnder(path, d));
        }

        static string Dir(string path) => path.Contains('/') ? path.Substring(0, path.LastIndexOf('/')) : "";
    }
}
