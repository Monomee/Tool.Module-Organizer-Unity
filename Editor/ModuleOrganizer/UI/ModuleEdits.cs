using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Monome.UtilityPack.ModuleOrganizer
{
    // Every config change the window makes. Each one is a single Undo step on the config and marks it dirty.
    public static class ModuleEdits
    {
        // Assigns the assignable, in-scope files to m. A file taken from another module or from pendingRemovals keeps
        // its history (where it was before the first Apply); its subfolder is recomputed for m as NewEntry would.
        // Returns how many entries changed.
        public static int Assign(ModuleConfig cfg, ScanResult scan, IEnumerable<string> guids, Module m, Func<string, string> readSource)
        {
            Undo.RecordObject(cfg, "Assign scripts");
            var n = 0;
            foreach (var g in guids.Distinct())
            {
                if (!scan.files.TryGetValue(g, out var f) || !ModuleValidator.IsAssignable(f, cfg) || !cfg.IsInScope(f.path)) continue;
                var from = cfg.FindModuleOf(g);
                if (from == m) continue;
                var prev = from?.scripts.First(e => e.guid == g) ?? cfg.pendingRemovals.FirstOrDefault(e => e.guid == g);
                from?.scripts.RemoveAll(e => e.guid == g);
                cfg.pendingRemovals.RemoveAll(e => e.guid == g);
                // ponytail: an entry accepted from disk has originalPath inside its old module folder, so its subfolder
                // here is scope-relative (may repeat the old module's name); carry the old relativeDir if that bites.
                m.scripts.Add(prev != null
                    ? PathRules.NewEntry(m, g, prev.originalPath, prev.originalNamespace, cfg.scopeFolders)
                    : PathRules.NewEntry(m, g, f.path, Namespace(readSource(f.path)), cfg.scopeFolders));
                n++;
            }
            EditorUtility.SetDirty(cfg);
            return n;
        }

        // Entries Apply must move back (applied, or the scan shows the file away from its original folder) go to
        // pendingRemovals; drafts are just dropped.
        public static void Unassign(ModuleConfig cfg, IEnumerable<string> guids, ScanResult scan = null)
        {
            Undo.RecordObject(cfg, "Unassign scripts");
            var set = new HashSet<string>(guids);
            foreach (var m in cfg.modules) Release(cfg, m, set, scan);
            EditorUtility.SetDirty(cfg);
        }

        public static void DeleteModule(ModuleConfig cfg, Module m, ScanResult scan = null)
        {
            Undo.RecordObject(cfg, "Delete module");
            Release(cfg, m, new HashSet<string>(m.scripts.Select(e => e.guid)), scan);
            foreach (var g in new[] { m.asmdefGuid, m.editorAsmdefGuid })
                if (!string.IsNullOrEmpty(g) && !cfg.retiredAsmdefGuids.Contains(g)) cfg.retiredAsmdefGuids.Add(g);
            cfg.modules.Remove(m);
            EditorUtility.SetDirty(cfg);
        }

        public static Module AddModule(ModuleConfig cfg)
        {
            Undo.RecordObject(cfg, "Add module");
            var name = "NewModule";
            for (var i = 2; cfg.modules.Any(x => x.name == name); i++) name = "NewModule" + i;
            var m = cfg.AddModule(name);
            EditorUtility.SetDirty(cfg);
            return m;
        }

        // Modules are looked up by name (applier, sync), so names stay unique. The folder's last segment follows the name;
        // Apply moves the folder.
        public static bool RenameModule(ModuleConfig cfg, Module m, string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.IndexOfAny(new[] { '/', '\\' }) >= 0 || cfg.modules.Any(x => x != m && x.name == name)) return false;
            Undo.RecordObject(cfg, "Rename module");
            var folder = PathRules.Norm(m.folder);
            m.folder = (folder.Contains('/') ? folder.Substring(0, folder.LastIndexOf('/') + 1) : "") + name;
            m.name = name;
            EditorUtility.SetDirty(cfg);
            return true;
        }

        // Any other field (namespace, color, description, references, scope folders...).
        public static void Change(ModuleConfig cfg, string label, Action change)
        {
            Undo.RecordObject(cfg, label);
            change();
            EditorUtility.SetDirty(cfg);
        }

        static void Release(ModuleConfig cfg, Module m, HashSet<string> guids, ScanResult scan)
        {
            foreach (var e in m.scripts.Where(e => guids.Contains(e.guid)))
                if (e.applied || AwayFromOrigin(e, scan))
                {
                    cfg.pendingRemovals.RemoveAll(p => p.guid == e.guid);
                    cfg.pendingRemovals.Add(e);
                }
            m.scripts.RemoveAll(e => guids.Contains(e.guid));
        }

        static bool AwayFromOrigin(ScriptEntry e, ScanResult scan) =>
            scan != null && scan.files.TryGetValue(e.guid, out var f) && PathRules.RestorePath(e, f.path) != f.path;

        static string Namespace(string src) => src == null ? "" : SourceRewriter.Parse(src).namespaces.FirstOrDefault() ?? "";
    }
}
