using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class ApplyPlannerTests
    {
        const string S = "Assets/_Scripts/";
        ModuleConfig cfg;
        ScanResult scan;
        DiskState disk;
        ValidationReport report;
        int guids;

        [SetUp]
        public void SetUp()
        {
            cfg = ScriptableObject.CreateInstance<ModuleConfig>();
            cfg.scopeFolders = new List<string> { "Assets/_Scripts" };
            scan = new ScanResult();
            disk = new DiskState();
            guids = 0;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(cfg);

        static string Ns(string fullName) => fullName.Contains('.') ? fullName.Substring(0, fullName.LastIndexOf('.')) : "";

        static string Simple(string fullName) => fullName.Substring(fullName.LastIndexOf('.') + 1);

        static string Asm(bool editor) => editor ? "Assembly-CSharp-Editor" : "Assembly-CSharp";

        // An Assembly-CSharp(-Editor) file declaring one type, with a real source in that type's namespace.
        ScriptFile Add(string path, string guid, string declares, params string[] refs) => Add(path, guid, false, declares, refs);

        ScriptFile Add(string path, string guid, bool editor, string declares, params string[] refs)
        {
            var f = new ScriptFile { guid = guid, path = path, kind = editor ? AssemblyKind.Editor : AssemblyKind.Runtime, referencedTypes = new HashSet<string>(refs) };
            scan.files[guid] = f;
            f.declaredTypes.Add(declares);
            var partial = scan.types.TryGetValue(declares, out var t);
            if (!partial) t = scan.types[declares] = new TypeRecord { fullName = declares, ns = Ns(declares), kind = f.kind, assemblyName = Asm(editor) };
            t.filePaths.Add(path);
            var decl = (partial || scan.types[declares].filePaths.Count > 1 ? "partial class " : "class ") + Simple(declares) + " {}";
            disk.sourceByGuid[guid] = Ns(declares).Length == 0 ? decl + "\n" : "namespace " + Ns(declares) + "\n{\n    " + decl + "\n}\n";
            disk.pathByGuid[guid] = path;
            for (var p = path; p.Contains('/'); p = p.Substring(0, p.LastIndexOf('/'))) disk.existingPaths.Add(p);
            return f;
        }

        string Source(string path) => disk.sourceByGuid[scan.FileByPath(path).guid];

        Module Assign(string module, params string[] fileGuids)
        {
            var m = cfg.modules.FirstOrDefault(x => x.name == module) ?? cfg.AddModule(module);
            foreach (var g in fileGuids)
                m.scripts.Add(new ScriptEntry
                {
                    guid = g, originalPath = scan.files[g].path,
                    originalNamespace = FileFates.CurrentNamespace(SourceRewriter.Parse(disk.sourceByGuid[g])),
                });
            return m;
        }

        ApplyPlan Plan()
        {
            report = ModuleValidator.Validate(cfg, scan, Source);
            return ApplyPlanner.Build(cfg, scan, report, disk, () => (++guids).ToString("x32"));
        }

        void Unassign(string guid)
        {
            var m = cfg.FindModuleOf(guid);
            var e = m.scripts.Single(x => x.guid == guid);
            m.scripts.Remove(e);
            cfg.pendingRemovals.Add(e);
        }

        static string Describe(ApplyPlan p) => string.Join("\n", p.createFolders.Select(x => "mkdir " + x)
            .Concat(p.moves.Select(x => "move " + x))
            .Concat(p.asmdefs.Select(x => (x.delete ? "delete " : "asmdef ") + x.path + " " + string.Join(",", x.references)))
            .Concat(p.edits.Select(x => $"edit {x.guid} ns={x.setNamespace} +{string.Join(",", x.addUsings)} -{string.Join(",", x.removeUsings)} mf={x.movedFrom.Count}"))
            .Concat(p.deleteFoldersIfEmpty.Select(x => "rmdir " + x))
            .Concat(p.removeEntries.Select(x => "drop " + x))
            .Concat(p.clearRetiredAsmdefGuids.Select(x => "unretire " + x))
            .Concat(p.issues.Select(x => x.ToString())));

        // What the executor does, simulated on scan/disk/cfg (sources edited with SourceRewriter) so the next Plan() sees it.
        void Execute(ApplyPlan p)
        {
            Assert.IsTrue(report.CanApply, string.Join("\n", report.issues));
            Assert.IsEmpty(p.issues, Describe(p));
            foreach (var d in p.createFolders) disk.existingPaths.Add(d);
            foreach (var mv in p.moves)
            {
                disk.existingPaths.Remove(mv.from);
                disk.existingPaths.Add(mv.to);
                disk.pathByGuid[mv.guid] = mv.to;
                if (!scan.files.TryGetValue(mv.guid, out var f)) continue;
                foreach (var t in scan.types.Values)
                    for (var i = 0; i < t.filePaths.Count; i++)
                        if (t.filePaths[i] == f.path) t.filePaths[i] = mv.to;
                f.path = mv.to;
            }
            foreach (var a in p.asmdefs)
            {
                var m = cfg.modules.FirstOrDefault(x => x.name == a.moduleName);
                if (a.delete)
                {
                    disk.pathByGuid.Remove(a.guid);
                    disk.asmdefTextByGuid.Remove(a.guid);
                    disk.existingPaths.Remove(a.path);
                    if (m != null && a.editorOnly) m.editorAsmdefGuid = null;
                    else if (m != null) m.asmdefGuid = null;
                    continue;
                }
                Assert.IsNotEmpty(a.guid);
                if (a.editorOnly) m.editorAsmdefGuid = a.guid;
                else m.asmdefGuid = a.guid;
                disk.pathByGuid[a.guid] = a.path;
                disk.existingPaths.Add(a.path);
                disk.asmdefTextByGuid[a.guid] = AsmdefJson.Write(a);
            }

            var rename = new Dictionary<string, string>();
            foreach (var e in p.edits)
            {
                var src = disk.sourceByGuid[e.guid];
                if (e.setNamespace != null)
                {
                    src = SourceRewriter.SetNamespace(src, e.setNamespace);
                    foreach (var tn in scan.files[e.guid].declaredTypes)
                        rename[tn] = (e.setNamespace.Length == 0 ? "" : e.setNamespace + ".") + Simple(tn);
                }
                foreach (var u in e.addUsings) src = SourceRewriter.AddUsing(src, u);
                foreach (var u in e.removeUsings) src = SourceRewriter.RemoveUsing(src, u);
                foreach (var (type, oldNs, oldAsm) in e.movedFrom) src = SourceRewriter.AddMovedFrom(src, type, oldNs, oldAsm);
                disk.sourceByGuid[e.guid] = src;
            }
            string R(string n) => rename.TryGetValue(n, out var r) ? r : n;
            foreach (var t in scan.types.Values.ToList())
            {
                if (!rename.ContainsKey(t.fullName)) continue;
                scan.types.Remove(t.fullName);
                t.fullName = R(t.fullName);
                t.ns = Ns(t.fullName);
                scan.types[t.fullName] = t;
            }
            foreach (var f in scan.files.Values)
            {
                f.declaredTypes = f.declaredTypes.Select(R).ToList();
                f.referencedTypes = new HashSet<string>(f.referencedTypes.Select(R));
            }

            void SetAssembly(ScriptFile f, AssemblyKind kind, string asmdefGuid, string asmName)
            {
                var editor = f.IsEditor;
                f.kind = kind;
                f.asmdefGuid = asmdefGuid;
                f.editorOnlyAssembly = kind == AssemblyKind.AsmDef && editor;
                foreach (var tn in f.declaredTypes)
                {
                    scan.types[tn].kind = kind;
                    scan.types[tn].asmdefGuid = asmdefGuid;
                    scan.types[tn].assemblyName = asmName;
                }
            }
            foreach (var m in cfg.modules)
            foreach (var e in m.scripts)
            {
                var f = scan.files[e.guid];
                var editor = f.IsEditor;
                SetAssembly(f, AssemblyKind.AsmDef, editor ? m.editorAsmdefGuid : m.asmdefGuid, PathRules.AsmdefName(m, editor));
                e.applied = true;
            }
            foreach (var g in p.removeEntries)
            {
                if (!scan.files.TryGetValue(g, out var f)) continue;
                var editor = f.IsEditor;
                SetAssembly(f, editor ? AssemblyKind.Editor : AssemblyKind.Runtime, null, Asm(editor));
            }
            cfg.pendingRemovals.RemoveAll(e => p.removeEntries.Contains(e.guid));
            cfg.retiredAsmdefGuids.RemoveAll(p.clearRetiredAsmdefGuids.Contains);
            foreach (var d in p.deleteFoldersIfEmpty)
                if (!disk.existingPaths.Any(x => x.StartsWith(d + "/")))
                    disk.existingPaths.Remove(d);
        }

        void AssertReapplyIsEmpty()
        {
            var again = Plan();
            Assert.IsTrue(again.IsEmpty, Describe(again));
        }

        [Test]
        public void FirstApply_MovesWrapsAndCreatesAsmdef()
        {
            Add(S + "Weapon.cs", "w", "Weapon");
            Assign("Combat", "w");
            var p = Plan();
            Assert.AreEqual("Assets/_Scripts/Combat/Weapon.cs", p.moves.Single().to);
            Assert.AreEqual(S + "Weapon.cs", p.moves.Single().from);
            Assert.AreEqual("Game.Combat", p.edits.Single(e => e.guid == "w").setNamespace);
            var a = p.asmdefs.Single();
            Assert.AreEqual("Assets/_Scripts/Combat/Game.Combat.asmdef", a.path);
            Assert.AreEqual("Game.Combat", a.name);
            Assert.AreEqual("Game.Combat", a.rootNamespace);
            Assert.AreEqual(1.ToString("x32"), a.guid); // pre-allocated
            Assert.IsFalse(a.editorOnly);
            Assert.AreEqual("Combat", a.moduleName);
            CollectionAssert.AreEqual(new[] { S + "Combat" }, p.createFolders);
            Assert.IsEmpty(p.deleteFoldersIfEmpty);
            Assert.IsEmpty(p.issues);
        }

        [Test]
        public void Dependent_GetsUsing()
        {
            Add(S + "Weapon.cs", "w", "Game.Weapon");
            Add(S + "Sub/Helper.cs", "h", "Game.Helper", "Game.Weapon");
            Add(S + "UI/User.cs", "u", "Game.UI.User", "Game.Weapon");
            Assign("Combat", "w", "h");
            var p = Plan();
            CollectionAssert.AreEqual(new[] { "Game.Combat" }, p.edits.Single(e => e.guid == "u").addUsings);
            Assert.IsNull(p.edits.Single(e => e.guid == "u").setNamespace);
            var helper = p.edits.Single(e => e.guid == "h");
            Assert.AreEqual("Game.Combat.Sub", helper.setNamespace);
            Assert.IsEmpty(helper.addUsings); // Game.Combat encloses Game.Combat.Sub
            Assert.AreEqual(S + "Combat/Sub/Helper.cs", p.moves.Single(m => m.guid == "h").to);
        }

        [Test]
        public void NamespaceChange_KeepsTypesOfOldEnclosingNamespace()
        {
            // Weapon used Helper unqualified through its own namespace Shared; Helper stays in Shared.
            Add(S + "Core/Helper.cs", "h", "Shared.Helper");
            Add(S + "Weapon.cs", "w", "Shared.Weapon", "Shared.Helper");
            Assign("Core", "h").rewriteNamespace = false;
            Assign("Combat", "w");
            var p = Plan();
            Assert.IsTrue(report.CanApply, string.Join("\n", report.issues));
            CollectionAssert.AreEqual(new[] { "Shared" }, p.edits.Single(e => e.guid == "w").addUsings);
            Execute(p);
            AssertReapplyIsEmpty();
        }

        [Test]
        public void ReapplyWithNoChanges_IsEmpty()
        {
            Add(S + "Util.cs", "c", "Game.Util");
            Add(S + "Weapon.cs", "w", "Game.Weapon", "Game.Util");
            Add(S + "Editor/WeaponEditor.cs", "e", true, "Game.WeaponEditor", "Game.Weapon");
            Add(S + "User.cs", "u", "Game.User", "Game.Weapon");
            Assign("Core", "c");
            Assign("Combat", "w", "e");
            var p = Plan();
            Assert.IsEmpty(p.issues, Describe(p));
            CollectionAssert.AreEqual(new[] { "Game.Core" }, p.edits.Single(x => x.guid == "w").addUsings);
            // GUIDs are planned, so asmdefs reference each other by GUID on first Apply already.
            var core = p.asmdefs.Single(x => x.name == "Game.Core");
            var combat = p.asmdefs.Single(x => x.name == "Game.Combat");
            CollectionAssert.AreEqual(new[] { "GUID:" + core.guid }, combat.references);
            CollectionAssert.AreEqual(new[] { "GUID:" + combat.guid }, p.asmdefs.Single(x => x.editorOnly).references);
            CollectionAssert.AllItemsAreUnique(p.asmdefs.Select(x => x.guid));
            Execute(p);
            AssertReapplyIsEmpty();
        }

        [Test]
        public void MoveBetweenModules_MovesAndRenamespaces()
        {
            Add(S + "Weapon.cs", "w", "Game.Weapon");
            Add(S + "User.cs", "u", "Game.User", "Game.Weapon");
            Assign("Combat", "w");
            Execute(Plan());

            var combat = cfg.modules.Single(m => m.name == "Combat");
            var entry = combat.scripts.Single();
            combat.scripts.Clear();
            cfg.AddModule("Gear").scripts.Add(entry);
            var p = Plan();
            var mv = p.moves.Single();
            Assert.AreEqual(S + "Combat/Weapon.cs", mv.from);
            Assert.AreEqual(S + "Gear/Weapon.cs", mv.to);
            Assert.AreEqual("Game.Gear", p.edits.Single(e => e.guid == "w").setNamespace);
            var user = p.edits.Single(e => e.guid == "u");
            CollectionAssert.AreEqual(new[] { "Game.Gear" }, user.addUsings);
            CollectionAssert.AreEqual(new[] { "Game.Combat" }, user.removeUsings);
            Assert.AreEqual(S + "Gear/Game.Gear.asmdef", p.asmdefs.Single().path);
            CollectionAssert.Contains(p.deleteFoldersIfEmpty, S + "Combat");
            Execute(p);
            AssertReapplyIsEmpty();
        }

        [Test]
        public void RenameModule_MovesAsmdefKeepsGuid()
        {
            Add(S + "Weapon.cs", "w", "Game.Weapon");
            Add(S + "Editor/WeaponEditor.cs", "e", true, "Game.WeaponEditor", "Game.Weapon");
            Add(S + "User.cs", "u", "Game.User", "Game.Weapon");
            var m = Assign("Combat", "w", "e");
            Execute(Plan());
            var guid = m.asmdefGuid;

            m.name = "Fight";
            m.namespaceName = "Game.Fight";
            m.folder = S + "Fight";
            var p = Plan();
            var asmMove = p.moves.Single(x => x.guid == guid);
            Assert.AreEqual(S + "Combat/Game.Combat.asmdef", asmMove.from);
            Assert.AreEqual(S + "Fight/Game.Fight.asmdef", asmMove.to);
            Assert.AreEqual(S + "Combat/Editor/Game.Combat.Editor.asmdef", p.moves.Single(x => x.guid == m.editorAsmdefGuid).from);
            var a = p.asmdefs.Single(x => !x.editorOnly);
            Assert.AreEqual(S + "Fight/Game.Fight.asmdef", a.path);
            Assert.AreEqual("Game.Fight", a.name);
            Assert.AreEqual(guid, a.guid);
            Assert.AreEqual(S + "Fight/Weapon.cs", p.moves.Single(x => x.guid == "w").to);
            Assert.AreEqual("Game.Fight.Editor", p.edits.Single(x => x.guid == "e").setNamespace);
            var user = p.edits.Single(x => x.guid == "u");
            CollectionAssert.AreEqual(new[] { "Game.Fight" }, user.addUsings);
            CollectionAssert.AreEqual(new[] { "Game.Combat" }, user.removeUsings);
            CollectionAssert.AreEqual(new[] { S + "Combat/Editor", S + "Combat" }, p.deleteFoldersIfEmpty);
            Execute(p);
            Assert.AreEqual(guid, m.asmdefGuid);
            Assert.IsFalse(disk.existingPaths.Contains(S + "Combat"));
            AssertReapplyIsEmpty();
        }

        [Test]
        public void Unassign_RestoresOriginalPathAndNamespace_RemovesStaleUsing()
        {
            Add(S + "Weapon.cs", "w", "Weapon");
            Add(S + "User.cs", "u", "User", "Weapon");
            Assign("Combat", "w");
            var first = Plan();
            CollectionAssert.AreEqual(new[] { "Game.Combat" }, first.edits.Single(e => e.guid == "u").addUsings);
            Execute(first);

            Unassign("w");
            var p = Plan();
            var mv = p.moves.Single();
            Assert.AreEqual(S + "Combat/Weapon.cs", mv.from);
            Assert.AreEqual(S + "Weapon.cs", mv.to);
            Assert.AreEqual("", p.edits.Single(e => e.guid == "w").setNamespace);
            var user = p.edits.Single(e => e.guid == "u");
            Assert.IsEmpty(user.addUsings); // back to global: nothing to import
            CollectionAssert.AreEqual(new[] { "Game.Combat" }, user.removeUsings);
            CollectionAssert.AreEqual(new[] { "w" }, p.removeEntries);
            Assert.IsEmpty(p.issues);
            Execute(p);
            Assert.IsEmpty(cfg.pendingRemovals);
            AssertReapplyIsEmpty();
        }

        [Test]
        public void NamespaceEmptied_StaleUsingRemoved_InScope_ErrorOutside()
        {
            Add(S + "Weapon.cs", "w", "Game.Combat.Weapon");
            Add(S + "Other.cs", "o", "Game.Other");
            disk.sourceByGuid["o"] = "using Game.Combat;\nnamespace Game\n{\n    class Other {}\n}\n"; // unused using
            Assign("Combat", "w");
            Assign("Gear");
            var p = Plan(); // Weapon already in Game.Combat: nothing gone yet
            Assert.IsFalse(p.edits.Any(e => e.guid == "o"), Describe(p));

            var entry = cfg.modules.Single(m => m.name == "Combat").scripts.Single();
            cfg.modules.Single(m => m.name == "Combat").scripts.Clear();
            cfg.modules.Single(m => m.name == "Gear").scripts.Add(entry);
            p = Plan();
            CollectionAssert.AreEqual(new[] { "Game.Combat" }, p.edits.Single(e => e.guid == "o").removeUsings);

            Add("Assets/Old/Legacy.cs", "l", "Legacy");
            disk.sourceByGuid["l"] = "using Game.Combat;\nclass Legacy {}\n";
            Plan();
            var i = report.issues.Single(x => x.code == IssueCode.ReverseRefOutOfScope);
            Assert.AreEqual(Severity.Error, i.severity);
            Assert.AreEqual("Assets/Old/Legacy.cs", i.path);
            StringAssert.Contains("Game.Combat", i.message);
        }

        [Test]
        public void Unassign_OriginalPathTaken_IsRestoreConflict()
        {
            Add(S + "Weapon.cs", "w", "Weapon");
            Assign("Combat", "w");
            Execute(Plan());
            Add(S + "Weapon.cs", "w2", "Other");
            Unassign("w");
            var i = Plan().issues.Single();
            Assert.AreEqual(IssueCode.RestoreConflict, i.code);
            Assert.AreEqual(Severity.Error, i.severity);
            StringAssert.Contains(S + "Weapon.cs", i.message);
        }

        [Test]
        public void Restore_RecreatesDeletedFolder()
        {
            Add(S + "Old/Deep/Weapon.cs", "w", "Weapon");
            Assign("Combat", "w");
            Execute(Plan());
            disk.existingPaths.Remove(S + "Old/Deep");
            disk.existingPaths.Remove(S + "Old");
            Unassign("w");
            var p = Plan();
            CollectionAssert.AreEqual(new[] { S + "Old", S + "Old/Deep" }, p.createFolders);
            Assert.AreEqual(S + "Old/Deep/Weapon.cs", p.moves.Single().to);
            Assert.IsEmpty(p.issues);
        }

        [Test]
        public void TwoFilesToSameTarget_IsPathConflict_CaseOnlyRenameIsNot()
        {
            Add(S + "Editor/A/X.cs", "x1", true, "Game.X1");
            Add(S + "A/Editor/X.cs", "x2", true, "Game.X2");
            Assign("Combat", "x1", "x2");
            var i = Plan().issues.Single();
            Assert.AreEqual(IssueCode.PathConflict, i.code);
            StringAssert.Contains(S + "Combat/Editor/A/X.cs", i.message);

            cfg.modules.Clear();
            Add(S + "Combat/W.cs", "w", "Game.Combat.W"); // already where a "combat" module would put it, but in other case
            cfg.AddModule("combat").scripts.Add(new ScriptEntry { guid = "w", originalPath = S + "W.cs", originalNamespace = "Game.Combat" });
            cfg.modules[0].namespaceName = "Game.Combat";
            var p = Plan();
            Assert.AreEqual(S + "combat/W.cs", p.moves.Single(x => x.guid == "w").to);
            Assert.IsEmpty(p.issues, Describe(p));
        }

        [Test]
        public void DeleteModule_DeletesAsmdefAndRestoresAll()
        {
            Add(S + "Weapon.cs", "w", "Game.Weapon");
            Add(S + "Editor/WeaponEditor.cs", "e", true, "Game.WeaponEditor", "Game.Weapon");
            var m = Assign("Combat", "w", "e");
            Execute(Plan());
            var asmGuids = new[] { m.asmdefGuid, m.editorAsmdefGuid };

            cfg.modules.Remove(m);
            cfg.pendingRemovals.AddRange(m.scripts);
            cfg.retiredAsmdefGuids.AddRange(asmGuids);
            var p = Plan();
            CollectionAssert.AreEquivalent(new[] { S + "Weapon.cs", S + "Editor/WeaponEditor.cs" }, p.moves.Select(x => x.to));
            Assert.IsTrue(p.asmdefs.All(a => a.delete));
            CollectionAssert.AreEquivalent(new[] { S + "Combat/Game.Combat.asmdef", S + "Combat/Editor/Game.Combat.Editor.asmdef" }, p.asmdefs.Select(a => a.path));
            Assert.AreEqual("Game", p.edits.Single(x => x.guid == "w").setNamespace);
            Assert.AreEqual("Game", p.edits.Single(x => x.guid == "e").setNamespace);
            CollectionAssert.AreEqual(new[] { S + "Combat/Editor", S + "Combat" }, p.deleteFoldersIfEmpty);
            CollectionAssert.AreEquivalent(asmGuids, p.clearRetiredAsmdefGuids);
            CollectionAssert.AreEquivalent(new[] { "w", "e" }, p.removeEntries);
            Execute(p);
            Assert.IsFalse(disk.existingPaths.Contains(S + "Combat"));
            Assert.IsTrue(disk.existingPaths.Contains(S + "Editor"));
            Assert.IsEmpty(cfg.retiredAsmdefGuids);
            AssertReapplyIsEmpty();
        }

        [Test]
        public void Cleanup_StaysInsideModuleFolder()
        {
            cfg.modulesRoot = S + "Modules";
            Add(S + "Weapon.cs", "w", "Game.Weapon");
            var m = Assign("Combat", "w");
            m.folder = S + "Modules/Deep/Combat";
            Execute(Plan());
            Unassign("w");
            var p = Plan();
            CollectionAssert.AreEqual(new[] { S + "Modules/Deep/Combat" }, p.deleteFoldersIfEmpty); // never Deep or Modules
        }

        [Test]
        public void EditorFiles_GetEditorAsmdefReferencingRuntime()
        {
            Add(S + "Weapon.cs", "w", "Weapon");
            Add(S + "Editor/WeaponEditor.cs", "e", true, "WeaponEditor", "Weapon");
            var m = Assign("Combat", "w", "e");
            var p = Plan();
            Assert.AreEqual(S + "Combat/Editor/WeaponEditor.cs", p.moves.Single(x => x.guid == "e").to);
            Assert.AreEqual("Game.Combat.Editor", p.edits.Single(x => x.guid == "e").setNamespace);
            var ed = p.asmdefs.Single(a => a.editorOnly);
            Assert.AreEqual(S + "Combat/Editor/Game.Combat.Editor.asmdef", ed.path);
            Assert.AreEqual("Game.Combat.Editor", ed.name);
            CollectionAssert.AreEqual(new[] { "GUID:" + p.asmdefs.Single(a => !a.editorOnly).guid }, ed.references);
            CollectionAssert.AreEqual(new[] { S + "Combat", S + "Combat/Editor" }, p.createFolders);
            Execute(p);
            CollectionAssert.AreEqual(new[] { "GUID:" + m.asmdefGuid }, ApplyPlanner.ComputeReferences(m, true, report, cfg));

            // Last Editor script leaves: the Editor asmdef goes.
            Unassign("e");
            p = Plan();
            var del = p.asmdefs.Single();
            Assert.IsTrue(del.delete);
            Assert.AreEqual(S + "Combat/Editor/Game.Combat.Editor.asmdef", del.path);
        }

        [Test]
        public void SerializableMoved_GetsMovedFrom()
        {
            Add(S + "Payload.A.cs", "p1", "Payload");
            Add(S + "Payload.B.cs", "p2", "Payload");
            scan.types["Payload"].serializableNonUnityObject = true;
            var m = Assign("Combat", "p1", "p2");
            var p = Plan();
            Assert.AreEqual(("Payload", "", "Assembly-CSharp"), p.edits.Single(e => e.guid == "p1").movedFrom.Single());
            Assert.IsEmpty(p.edits.Single(e => e.guid == "p2").movedFrom); // one MovedFrom per type (CS0579)
            CollectionAssert.AreEqual(new[] { "|Assembly-CSharp|Payload" }, p.reserializeTypeKeys);

            m.rewriteNamespace = false; // assembly still changes
            p = Plan();
            Assert.AreEqual(("Payload", "", "Assembly-CSharp"), p.edits.Single(e => e.guid == "p1").movedFrom.Single());
            Assert.IsNull(p.edits.Single(e => e.guid == "p1").setNamespace);
        }

        [Test]
        public void SerializableMovedTwice_MovedFromPointsAtLatestSource()
        {
            Add(S + "Payload.cs", "p", "Payload");
            scan.types["Payload"].serializableNonUnityObject = true;
            Assign("Combat", "p");
            Execute(Plan());
            StringAssert.Contains("sourceNamespace: \"\", sourceAssembly: \"Assembly-CSharp\"", disk.sourceByGuid["p"]);

            var entry = cfg.modules[0].scripts.Single();
            cfg.modules[0].scripts.Clear();
            cfg.AddModule("Gear").scripts.Add(entry);
            var p = Plan();
            Assert.AreEqual(("Payload", "Game.Combat", "Game.Combat"), p.edits.Single(e => e.guid == "p").movedFrom.Single());
            CollectionAssert.AreEqual(new[] { "Game.Combat|Game.Combat|Payload" }, p.reserializeTypeKeys);
            Execute(p);
            var src = disk.sourceByGuid["p"];
            Assert.AreEqual(1, Regex.Matches(src, "MovedFrom").Count, src);
            StringAssert.Contains("sourceNamespace: \"Game.Combat\", sourceAssembly: \"Game.Combat\"", src);
            AssertReapplyIsEmpty();
        }

        [Test]
        public void ReadOnlyFiles_AreNeverTouched()
        {
            Add(S + "Weapon.cs", "w", "Game.Weapon");
            Add("Assets/Old/Legacy.cs", "l", "Legacy", "Game.Weapon");
            var third = Add(S + "Third/T.cs", "t", "Third.T", "Game.Weapon");
            third.kind = AssemblyKind.AsmDef;
            third.asmdefGuid = "third";
            Assign("Combat", "w", "l", "t");
            var p = Plan();
            Assert.IsFalse(report.CanApply);
            var touched = p.moves.Select(x => x.guid).Concat(p.edits.Select(x => x.guid)).Where(scan.files.ContainsKey).ToList();
            CollectionAssert.IsNotEmpty(touched);
            Assert.IsTrue(touched.All(g => ModuleValidator.IsAssignable(scan.files[g], cfg) && cfg.IsInScope(scan.files[g].path)), Describe(p));
            CollectionAssert.DoesNotContain(touched, "l");
            CollectionAssert.DoesNotContain(touched, "t");
        }

        [Test]
        public void ComputeReferences_EdgesOutsideAsmdefsExtras()
        {
            var core = cfg.AddModule("Core");
            core.asmdefGuid = new string('a', 32);
            cfg.AddModule("UI");
            var combat = cfg.AddModule("Combat");
            combat.asmdefGuid = new string('c', 32);
            combat.extraReferences.Add("Some.Dll");
            combat.extraReferences.Add(new string('d', 32));
            var r = new ValidationReport();
            r.edges.Add(new ModuleEdge { from = "Combat", to = "Core" });
            r.edges.Add(new ModuleEdge { from = "Combat", to = "UI" });
            r.edges.Add(new ModuleEdge { from = "Combat.Editor", to = "UI.Editor" });
            r.edges.Add(new ModuleEdge { from = "UI", to = "Core" });
            r.asmdefRefs["Combat"] = new HashSet<string> { new string('b', 32), "Lib.Runtime" };
            var a = "GUID:" + new string('a', 32);
            var b = "GUID:" + new string('b', 32);
            var c = "GUID:" + new string('c', 32);
            var d = "GUID:" + new string('d', 32);
            CollectionAssert.AreEqual(new[] { a, b, d, "Game.UI", "Lib.Runtime", "Some.Dll" }, ApplyPlanner.ComputeReferences(combat, false, r, cfg));
            CollectionAssert.AreEqual(new[] { c, d, "Game.UI.Editor", "Some.Dll" }, ApplyPlanner.ComputeReferences(combat, true, r, cfg));
            var planned = new Dictionary<string, string> { ["UI"] = new string('e', 32) };
            CollectionAssert.Contains(ApplyPlanner.ComputeReferences(combat, false, r, cfg, planned), "GUID:" + new string('e', 32));
        }
    }
}
