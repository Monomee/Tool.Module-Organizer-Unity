using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;

namespace Monome.UtilityPack.ModuleOrganizer
{
    // The only code that mutates the project. GUIDs survive (MoveAsset keeps the .meta), sources keep their BOM and
    // line endings, and asset editing is always closed again. Not undoable: git is the undo.
    public static class ModuleApplier
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true); // throws on bytes that are not UTF-8

        public static List<string> Execute(ApplyPlan plan, ModuleConfig cfg, bool refresh = true)
        {
            var errors = plan.issues.Where(i => i.severity == Severity.Error).Select(i => "Plan error: " + i).ToList();
            if (errors.Count > 0) return errors;

            var failed = new HashSet<string>(); // GUIDs whose move failed: no edits on them
            var written = new List<AsmdefOp>();
            // Before StartAssetEditing: a folder created inside it is not in the database yet, so MoveAsset into it fails.
            foreach (var f in plan.createFolders)
            {
                if (AssetDatabase.IsValidFolder(f)) continue;
                var cut = f.LastIndexOf('/');
                if (cut < 0 || string.IsNullOrEmpty(AssetDatabase.CreateFolder(f.Substring(0, cut), f.Substring(cut + 1))))
                    errors.Add($"Could not create folder {f}");
            }

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var m in plan.moves.OrderBy(m => m.to.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    var err = AssetDatabase.MoveAsset(m.from, m.to);
                    if (string.IsNullOrEmpty(err)) continue;
                    failed.Add(m.guid);
                    errors.Add($"Could not move {m}: {err}");
                }

                foreach (var op in plan.asmdefs.Where(a => !a.delete))
                {
                    try
                    {
                        File.WriteAllText(op.path, AsmdefJson.Write(op));
                        // New asmdef: its .meta goes down before the import so the planned GUID is the one Unity uses.
                        if (!File.Exists(op.path + ".meta")) File.WriteAllText(op.path + ".meta", Meta(op.guid));
                        written.Add(op);
                    }
                    catch (Exception e) { errors.Add($"Could not write {op.path}: {e.Message}"); }
                }

                var newPath = plan.moves.GroupBy(m => m.guid).ToDictionary(g => g.Key, g => g.Last().to);
                foreach (var op in plan.edits.Where(e => !failed.Contains(e.guid)))
                {
                    var path = newPath.TryGetValue(op.guid, out var p) ? p : AssetDatabase.GUIDToAssetPath(op.guid);
                    try { Edit(path, op, errors); }
                    catch (Exception e) { errors.Add($"Could not edit {path}: {e.Message}"); }
                }

                foreach (var op in plan.asmdefs.Where(a => a.delete))
                {
                    if (AssetDatabase.DeleteAsset(op.path)) written.Add(op);
                    else errors.Add($"Could not delete {op.path}");
                }

                foreach (var d in plan.deleteFoldersIfEmpty)
                    if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any() && !AssetDatabase.DeleteAsset(d))
                        errors.Add($"Could not delete empty folder {d}");
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (refresh)
            {
                AssetDatabase.Refresh();
                foreach (var op in written.Where(a => !a.delete && AssetDatabase.AssetPathToGUID(a.path) != a.guid))
                    errors.Add($"{op.path} was imported with GUID {AssetDatabase.AssetPathToGUID(op.path)}, expected {op.guid}");
            }

            Record(plan, cfg, written, errors.Count == 0);
            return errors;
        }

        // Asmdef GUIDs are recorded even after a partial failure (the files exist now); the rest only on success,
        // so a failed Apply is simply planned again.
        static void Record(ApplyPlan plan, ModuleConfig cfg, List<AsmdefOp> written, bool ok)
        {
            foreach (var op in written.Where(a => a.moduleName != null))
            {
                var m = cfg.modules.FirstOrDefault(x => x.name == op.moduleName);
                if (m == null) continue;
                var guid = op.delete ? null : op.guid;
                if (op.editorOnly) m.editorAsmdefGuid = guid;
                else m.asmdefGuid = guid;
            }
            if (ok)
            {
                foreach (var e in cfg.modules.SelectMany(m => m.scripts)) e.applied = true;
                cfg.pendingRemovals.RemoveAll(e => plan.removeEntries.Contains(e.guid));
                cfg.retiredAsmdefGuids.RemoveAll(plan.clearRetiredAsmdefGuids.Contains);
            }
            EditorUtility.SetDirty(cfg);
            if (EditorUtility.IsPersistent(cfg)) AssetDatabase.SaveAssetIfDirty(cfg);
        }

        static void Edit(string path, TextEditOp op, List<string> errors)
        {
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var skip = bom ? 3 : 0;
            var original = Utf8.GetString(bytes, skip, bytes.Length - skip); // non-UTF-8 file: throws, left untouched
            var src = original;
            if (op.setNamespace != null)
            {
                if (SourceRewriter.Parse(src).Rewritable) src = SourceRewriter.SetNamespace(src, op.setNamespace);
                else errors.Add($"Namespace layout too complex to rewrite, namespace left as is: {path}");
            }
            foreach (var u in op.removeUsings) src = SourceRewriter.RemoveUsing(src, u);
            foreach (var u in op.addUsings) src = SourceRewriter.AddUsing(src, u);
            foreach (var (type, oldNs, oldAsm) in op.movedFrom)
                if (!SourceRewriter.TryAddMovedFrom(src, type, oldNs, oldAsm, out src))
                    errors.Add($"MovedFrom target not found: {type} in {path}");
            if (src == original) return;
            var body = Utf8.GetBytes(src);
            File.WriteAllBytes(path, bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(body).ToArray() : body);
        }

        static string Meta(string guid) =>
            "fileFormatVersion: 2\nguid: " + guid + "\nAssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n" +
            "  assetBundleName: \n  assetBundleVariant: \n";

        public static bool HasUncommittedGitChanges(string projectRoot)
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("git", "status --porcelain")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    WorkingDirectory = projectRoot,
                });
                if (p == null) return false;
                var output = p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(10000))
                {
                    try { p.Kill(); } catch { }
                    return false;
                }
                return p.ExitCode == 0 && output.Result.Trim().Length > 0;
            }
            catch (Exception) { return false; } // no git, or a bad folder
        }
    }
}
