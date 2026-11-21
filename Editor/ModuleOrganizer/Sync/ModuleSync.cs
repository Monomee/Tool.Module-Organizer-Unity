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
        public Dictionary<string, string> sourceByGuid = new Dictionary<string, string>(); // .cs text of unassigned scripts under module folders

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
            var known = new HashSet<string>(cfg.modules.SelectMany(m => m.scripts).Concat(cfg.pendingRemovals).Select(e => e.guid));
            foreach (var g in cfg.modules.SelectMany(m => m.scripts).Select(e => e.guid)) Script(g);
            foreach (var g in cfg.modules.SelectMany(m => new[] { m.asmdefGuid, m.editorAsmdefGuid })) Asmdef(g);
            if (folders.Length > 0)
            {
                foreach (var g in AssetDatabase.FindAssets("t:Script", folders)) Script(g);
                foreach (var g in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset", folders)) Asmdef(g);
            }
            return d;

            void Script(string g)
            {
                var p = Existing(g, ".cs");
                if (p == null) return;
                d.scriptPathByGuid[g] = p;
                if (!known.Contains(g)) d.sourceByGuid[g] = File.ReadAllText(p);
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
            var assigned = new HashSet<string>(cfg.modules.SelectMany(m => m.scripts).Concat(cfg.pendingRemovals).Select(e => e.guid));
            foreach (var m in cfg.modules.Where(m => !string.IsNullOrEmpty(m.asmdefGuid)))
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
                if (new HashSet<string>(a.references).SetEquals(expected)) return;
                var item = Add(DriftKind.ReferencesDiffer, m, guid,
                    $"{a.path} references [{string.Join(", ", a.references)}], config computes [{string.Join(", ", expected)}].");
                item.manualReferences = a.references.Except(expected).Distinct().ToList();
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
            // Asmdef items first: they can move module.folder, which ScriptMovedOut re-checks.
            foreach (var it in items.OrderBy(i => i.kind == DriftKind.AsmdefMissing || i.kind == DriftKind.AsmdefMovedOrRenamed ? 0 : 1))
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
                        var ns = !string.IsNullOrEmpty(a.rootNamespace) ? a.rootNamespace : a.name;
                        if (editor && ns.EndsWith(".Editor", StringComparison.Ordinal)) ns = ns.Substring(0, ns.Length - ".Editor".Length);
                        m.folder = dir;
                        m.namespaceName = ns;
                        break;
                    case DriftKind.ScriptDeleted:
                        m.scripts.RemoveAll(e => e.guid == it.guid);
                        break;
                    case DriftKind.ScriptMovedOut:
                        // Just unassigned: the file stays where the user put it, so no pendingRemovals restore.
                        if (disk.scriptPathByGuid.TryGetValue(it.guid, out var cur) && !InModule(m, disk, cur)) m.scripts.RemoveAll(e => e.guid == it.guid);
                        break;
                    case DriftKind.ScriptAddedInModuleFolder:
                        if (!disk.scriptPathByGuid.TryGetValue(it.guid, out var path) || cfg.FindModuleOf(it.guid) != null) break;
                        var src = disk.sourceByGuid.TryGetValue(it.guid, out var s) ? s : "";
                        m.scripts.Add(new ScriptEntry
                        {
                            guid = it.guid, originalPath = path, originalNamespace = SourceRewriter.Parse(src).namespaces.FirstOrDefault() ?? "", applied = true,
                        });
                        break;
                    case DriftKind.ReferencesDiffer:
                        // Hand-added references become extraReferences. References missing on disk stay in the config:
                        // they are computed, and re-applying the config writes them back.
                        foreach (var r in it.manualReferences)
                            if (!m.extraReferences.Contains(r)) m.extraReferences.Add(r);
                        break;
                }
            }
            EditorUtility.SetDirty(cfg);
        }

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
