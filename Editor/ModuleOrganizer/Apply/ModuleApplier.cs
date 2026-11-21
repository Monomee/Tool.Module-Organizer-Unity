using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;

namespace Monome.UtilityPack.ModuleOrganizer
{
    // The only code that mutates the project. Everything that can be checked is checked before the first change
    // (pre-flight); a failing move stops the rest, and the next diff-based plan repairs from the real state.
    // GUIDs survive (MoveAsset keeps the .meta), sources keep their BOM and line endings, asset editing is always
    // closed again. Not undoable: git is the undo.
    public static class ModuleApplier
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true); // throws on bytes that are not UTF-8
        static readonly byte[] Bom = { 0xEF, 0xBB, 0xBF };

        public static List<string> Execute(ApplyPlan plan, ModuleConfig cfg, bool refresh = true) => Execute(plan, cfg, out _, refresh);

        // touchedDisk: true once the disk may have changed: a folder created, a move that succeeded (MoveAsset is atomic), or
        // any write/delete attempted (a failed write may have truncated the file). False when it stopped before all of that
        // (plan errors, preflight, a first folder or move that failed).
        public static List<string> Execute(ApplyPlan plan, ModuleConfig cfg, out bool touchedDisk, bool refresh = true)
        {
            touchedDisk = false;
            var errors = plan.issues.Where(i => i.severity == Severity.Error).Select(i => "Plan error: " + i).ToList();
            if (errors.Count > 0) return errors;
            var texts = Preflight(plan, errors);
            if (errors.Count > 0) return errors;

            // Case-only renames first, outside asset editing (the temporary name must be in the database): folders on the
            // way to each target that exist under another case (combat -> Combat), outermost first, then files. A file that
            // only moved with its folder is done; the other moves start inside the renamed folders.
            var moves = plan.moves.Select(m => new MoveOp { guid = m.guid, from = m.from, to = m.to }).ToList();
            foreach (var (from, to) in FolderCaseRenames(moves.Select(m => m.to)))
            {
                var err = CaseRename(from, to);
                if (err != null)
                {
                    errors.Add($"Could not rename folder {from} to {to}: {err}");
                    return errors;
                }
                touchedDisk = true;
                foreach (var m in moves.Where(m => PathRules.IsUnder(m.from, from))) m.from = to + m.from.Substring(from.Length);
            }
            foreach (var m in moves.Where(m => m.from != m.to && string.Equals(m.from, m.to, StringComparison.OrdinalIgnoreCase)))
            {
                var err = CaseRename(m.from, m.to);
                if (err != null)
                {
                    errors.Add($"Could not move {m}: {err}");
                    return errors;
                }
                touchedDisk = true;
                m.from = m.to;
            }

            // Before StartAssetEditing: a folder created inside it is not in the database yet, so MoveAsset into it fails.
            foreach (var f in plan.createFolders)
            {
                if (AssetDatabase.IsValidFolder(f)) continue;
                var cut = f.LastIndexOf('/');
                if (cut >= 0 && !string.IsNullOrEmpty(AssetDatabase.CreateFolder(f.Substring(0, cut), f.Substring(cut + 1))))
                {
                    touchedDisk = true;
                    continue;
                }
                errors.Add($"Could not create folder {f}");
                return errors;
            }

            var done = new List<AsmdefOp>();
            var movesOk = true;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var m in moves.Where(m => m.from != m.to).OrderBy(m => m.to.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
                {
                    var err = AssetDatabase.MoveAsset(m.from, m.to);
                    if (string.IsNullOrEmpty(err))
                    {
                        touchedDisk = true;
                        continue;
                    }
                    errors.Add($"Could not move {m}: {err}");
                    movesOk = false;
                    break;
                }
                if (movesOk)
                {
                    foreach (var op in plan.asmdefs.Where(a => !a.delete))
                        try
                        {
                            // New asmdef: its .meta goes down first, so the planned GUID is the one Unity imports it with.
                            // Writes and deletes count as touching disk once attempted: a failed write may have truncated the file.
                            touchedDisk = true;
                            if (!File.Exists(op.path + ".meta")) File.WriteAllText(op.path + ".meta", Meta(op.guid));
                            File.WriteAllText(op.path, AsmdefJson.Write(op));
                            done.Add(op);
                        }
                        catch (Exception e) { errors.Add($"Could not write {op.path}: {e.Message}"); }

                    foreach (var (path, bytes) in texts)
                        try
                        {
                            touchedDisk = true;
                            File.WriteAllBytes(path, bytes);
                        }
                        catch (Exception e) { errors.Add($"Could not write {path}: {e.Message}"); }

                    foreach (var op in plan.asmdefs.Where(a => a.delete))
                    {
                        touchedDisk = true;
                        if (AssetDatabase.DeleteAsset(op.path)) done.Add(op);
                        else errors.Add($"Could not delete {op.path}");
                    }

                    foreach (var d in plan.deleteFoldersIfEmpty)
                    {
                        if (!Directory.Exists(d) || Directory.EnumerateFileSystemEntries(d).Any()) continue;
                        touchedDisk = true;
                        if (!AssetDatabase.DeleteAsset(d)) errors.Add($"Could not delete empty folder {d}");
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            if (refresh) AssetDatabase.Refresh();
            // Owed until ApplyResultWatcher re-serializes after a good compile, even if this run stopped part way: a later
            // move of the same types would otherwise lose [SerializeReference] data. Outside Undo, like Record.
            if (touchedDisk)
                foreach (var k in plan.reserializeTypeKeys)
                    if (!cfg.owedReserializeKeys.Contains(k)) cfg.owedReserializeKeys.Add(k);
            if (movesOk) Record(plan, cfg, done, refresh, errors);
            EditorUtility.SetDirty(cfg);
            if (EditorUtility.IsPersistent(cfg)) AssetDatabase.SaveAssetIfDirty(cfg);
            return errors;
        }

        // Every check that needs no change: move sources, destinations and their folders, and each edited file's
        // full new text (encoding, namespace layout, MovedFrom targets). Returns (new path, bytes) of changed files.
        static List<(string path, byte[] bytes)> Preflight(ApplyPlan plan, List<string> errors)
        {
            var folders = new HashSet<string>(plan.createFolders);
            foreach (var m in plan.moves)
            {
                var dir = m.to.Contains('/') ? m.to.Substring(0, m.to.LastIndexOf('/')) : "";
                if (!File.Exists(m.from)) errors.Add($"Cannot move {m}: the source is missing");
                else if ((File.Exists(m.to) || Directory.Exists(m.to)) && !string.Equals(m.from, m.to, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"Cannot move {m}: the destination is taken");
                else if (!AssetDatabase.IsValidFolder(dir) && !folders.Contains(dir) && !Directory.Exists(dir)) // a case-only folder rename
                    errors.Add($"Cannot move {m}: folder {dir} does not exist");
            }
            var newPath = plan.moves.GroupBy(m => m.guid).ToDictionary(g => g.Key, g => g.Last().to);
            var texts = new List<(string, byte[])>();
            foreach (var op in plan.edits)
            {
                var path = AssetDatabase.GUIDToAssetPath(op.guid);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    errors.Add($"Cannot edit {op.guid}: file not found");
                    continue;
                }
                try
                {
                    var bytes = Edit(path, op, errors);
                    if (bytes != null) texts.Add((newPath.TryGetValue(op.guid, out var p) ? p : path, bytes));
                }
                catch (Exception e) { errors.Add($"Cannot edit {path}: {e.Message}"); }
            }
            return texts;
        }

        // New bytes of the file (same BOM state), or null when unchanged or on an error.
        static byte[] Edit(string path, TextEditOp op, List<string> errors)
        {
            var n = errors.Count;
            var bytes = File.ReadAllBytes(path);
            var bom = bytes.Length >= 3 && bytes[0] == Bom[0] && bytes[1] == Bom[1] && bytes[2] == Bom[2];
            var skip = bom ? 3 : 0;
            string src;
            // UTF-16/32 BOMs (FF/FE bytes) are invalid UTF-8; BOM-less UTF-16/32 shows up as NULs.
            try { src = Utf8.GetString(bytes, skip, bytes.Length - skip); }
            catch (DecoderFallbackException) { src = null; }
            if (src == null || src.IndexOf('\0') >= 0)
            {
                errors.Add($"Not a UTF-8 text file, cannot edit: {path}");
                return null;
            }
            var text = Rewrite(src, op, path, errors);
            if (errors.Count > n || text == src) return null;
            var body = Utf8.GetBytes(text);
            return bom ? Bom.Concat(body).ToArray() : body;
        }

        // The text edits of op, in the one order everything uses (tests included): stale marked aliases out, namespace,
        // fresh aliases into the new block, usings, MovedFrom.
        public static string Rewrite(string src, TextEditOp op, string path, List<string> errors)
        {
            var text = op.resetAliases ? SourceRewriter.RemoveMarkedAliases(src) : src;
            if (op.setNamespace != null)
            {
                if (SourceRewriter.Parse(text).Rewritable) text = SourceRewriter.SetNamespace(text, op.setNamespace);
                else errors.Add($"Namespace layout too complex to rewrite: {path}");
            }
            if (op.aliases.Count > 0) text = SourceRewriter.AddNamespaceAliases(text, op.aliases);
            foreach (var u in op.removeUsings) text = SourceRewriter.RemoveUsing(text, u);
            foreach (var u in op.addUsings) text = SourceRewriter.AddUsing(text, u, mark: true);
            foreach (var (type, oldNs, oldAsm) in op.movedFrom)
                if (!SourceRewriter.TryAddMovedFrom(text, type, oldNs, oldAsm, out text))
                    errors.Add($"MovedFrom target not found: {type} in {path}");
            return text;
        }

        // Folders on the way to each target that exist on disk under another case (to rename), outermost first.
        static List<(string from, string to)> FolderCaseRenames(IEnumerable<string> targets)
        {
            var list = new List<(string, string)>();
            foreach (var t in targets)
            {
                var parts = PathRules.Norm(t).Split('/');
                for (var i = 1; i < parts.Length - 1; i++)
                {
                    var parent = string.Join("/", parts, 0, i);
                    var actual = Directory.Exists(parent)
                        ? Directory.GetDirectories(parent).Select(Path.GetFileName).FirstOrDefault(n => string.Equals(n, parts[i], StringComparison.OrdinalIgnoreCase))
                        : null;
                    if (actual == null) break; // created by the plan
                    var rename = (parent + "/" + actual, parent + "/" + parts[i]);
                    if (actual != parts[i] && !list.Contains(rename)) list.Add(rename);
                }
            }
            return list;
        }

        // A case-only rename (X -> x) of a file or folder, through a temporary name: MoveAsset refuses a target that differs
        // only in case. Null when it worked; on a failed second step the asset goes back to its old name.
        static string CaseRename(string from, string to)
        {
            var ext = Path.GetExtension(from);
            var tmp = from.Substring(0, from.Length - ext.Length) + "__monome_tmp" + ext;
            var err = AssetDatabase.MoveAsset(from, tmp);
            if (!string.IsNullOrEmpty(err)) return err;
            err = AssetDatabase.MoveAsset(tmp, to);
            if (string.IsNullOrEmpty(err)) return null;
            AssetDatabase.MoveAsset(tmp, from);
            return err;
        }

        // Only GUIDs the project really holds are recorded; the rest only after a fully clean run.
        static void Record(ApplyPlan plan, ModuleConfig cfg, List<AsmdefOp> done, bool refresh, List<string> errors)
        {
            foreach (var op in done)
            {
                if (!op.delete && !Holds(op, refresh))
                {
                    errors.Add($"{op.path} does not carry the planned GUID {op.guid}; not recorded");
                    continue;
                }
                var m = op.moduleName == null ? null : cfg.modules.FirstOrDefault(x => x.name == op.moduleName);
                if (m == null) continue;
                var guid = op.delete ? null : op.guid;
                if (op.editorOnly) m.editorAsmdefGuid = guid;
                else m.asmdefGuid = guid;
            }
            if (errors.Count == 0)
            {
                foreach (var e in cfg.modules.SelectMany(m => m.scripts))
                {
                    var p = AssetDatabase.GUIDToAssetPath(e.guid);
                    if (!string.IsNullOrEmpty(p) && File.Exists(p)) e.applied = true;
                }
                cfg.pendingRemovals.RemoveAll(e => plan.removeEntries.Contains(e.guid));
                cfg.retiredAsmdefGuids.RemoveAll(plan.clearRetiredAsmdefGuids.Contains);
            }
        }

        // After Refresh ask the database; without one (tests) the .meta on disk is the truth.
        static bool Holds(AsmdefOp op, bool refresh) => refresh
            ? AssetDatabase.AssetPathToGUID(op.path) == op.guid
            : File.Exists(op.path + ".meta") && File.ReadAllText(op.path + ".meta").Contains("guid: " + op.guid);

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
