using System.Collections.Generic;
using System.Linq;
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

        // An Assembly-CSharp(-Editor) file declaring one type; its namespace is that type's.
        ScriptFile Add(string path, string guid, string declares, params string[] refs) => Add(path, guid, false, declares, refs);

        ScriptFile Add(string path, string guid, bool editor, string declares, params string[] refs)
        {
            var f = new ScriptFile { guid = guid, path = path, kind = editor ? AssemblyKind.Editor : AssemblyKind.Runtime, referencedTypes = new HashSet<string>(refs) };
            scan.files[guid] = f;
            f.declaredTypes.Add(declares);
            if (!scan.types.TryGetValue(declares, out var t))
                t = scan.types[declares] = new TypeRecord { fullName = declares, ns = Ns(declares), kind = f.kind, assemblyName = Asm(editor) };
            t.filePaths.Add(path);
            disk.pathByGuid[guid] = path;
            disk.namespaceByGuid[guid] = Ns(declares);
            for (var p = path; p.Contains('/'); p = p.Substring(0, p.LastIndexOf('/'))) disk.existingPaths.Add(p);
            return f;
        }

        string Source(string path)
        {
            var ns = Ns(scan.FileByPath(path).declaredTypes[0]);
            return ns.Length == 0 ? "class X {}" : "namespace " + ns + "\n{\n    class X {}\n}\n";
        }

        Module Assign(string module, params string[] fileGuids)
        {
            var m = cfg.modules.FirstOrDefault(x => x.name == module) ?? cfg.AddModule(module);
            foreach (var g in fileGuids)
                m.scripts.Add(new ScriptEntry { guid = g, originalPath = scan.files[g].path, originalNamespace = disk.namespaceByGuid[g] });
            return m;
        }

        ApplyPlan Plan()
        {
            report = ModuleValidator.Validate(cfg, scan, Source);
            return ApplyPlanner.Build(cfg, scan, report, disk);
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
            .Concat(p.asmdefs.Select(x => (x.delete ? "delete " : "asmdef ") + x.path))
            .Concat(p.edits.Select(x => $"edit {x.guid} ns={x.setNamespace} +{string.Join(",", x.addUsings)} -{string.Join(",", x.removeUsings)} mf={x.movedFrom.Count}"))
            .Concat(p.deleteFoldersIfEmpty.Select(x => "rmdir " + x))
            .Concat(p.removeEntries.Select(x => "drop " + x))
            .Concat(p.clearRetiredAsmdefGuids.Select(x => "unretire " + x))
            .Concat(p.issues.Select(x => x.ToString())));

        // What the executor does on disk, simulated on scan/disk/cfg so the next Plan() sees the result.
        // Asmdefs get their GUID on creation, then every written asmdef is rewritten with references recomputed,
        // so names of not-yet-created asmdefs become GUIDs within the same Apply.
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
            var written = new List<(AsmdefOp op, Module m, string guid)>();
            foreach (var a in p.asmdefs)
            {
                var m = cfg.modules.FirstOrDefault(x => x.name == a.moduleName);
                var guid = m == null ? null : a.editorOnly ? m.editorAsmdefGuid : m.asmdefGuid;
                if (a.delete)
                {
                    var g = disk.pathByGuid.First(kv => kv.Value == a.path).Key;
                    disk.pathByGuid.Remove(g);
                    disk.asmdefTextByGuid.Remove(g);
                    disk.existingPaths.Remove(a.path);
                    if (m != null && a.editorOnly) m.editorAsmdefGuid = null;
                    else if (m != null) m.asmdefGuid = null;
                    continue;
                }
                if (string.IsNullOrEmpty(guid)) guid = (++guids).ToString("x32");
                if (a.editorOnly) m.editorAsmdefGuid = guid;
                else m.asmdefGuid = guid;
                disk.pathByGuid[guid] = a.path;
                disk.existingPaths.Add(a.path);
                written.Add((a, m, guid));
            }
            foreach (var (a, m, guid) in written)
            {
                a.references = ApplyPlanner.ComputeReferences(m, a.editorOnly, report, cfg);
                disk.asmdefTextByGuid[guid] = AsmdefJson.Write(a);
            }

            var rename = new Dictionary<string, string>();
            foreach (var e in p.edits.Where(e => e.setNamespace != null))
            {
                disk.namespaceByGuid[e.guid] = e.setNamespace;
                foreach (var tn in scan.files[e.guid].declaredTypes)
                    rename[tn] = (e.setNamespace.Length == 0 ? "" : e.setNamespace + ".") + Simple(tn);
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
                SetAssembly(f, AssemblyKind.AsmDef, editor ? m.editorAsmdefGuid : m.asmdefGuid, m.namespaceName + (editor ? ".Editor" : ""));
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
            CollectionAssert.AreEqual(new[] { "Game.Core" }, p.asmdefs.Single(x => x.name == "Game.Combat").references); // no GUID yet
            Execute(p);
            var combat = cfg.modules.Single(m => m.name == "Combat");
            StringAssert.Contains("GUID:" + cfg.modules.Single(m => m.name == "Core").asmdefGuid, disk.asmdefTextByGuid[combat.asmdefGuid]);
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
            Assert.AreEqual(S + "Fight/Weapon.cs", p.moves.Single(x => x.guid == "w").to);
            Assert.AreEqual("Game.Fight.Editor", p.edits.Single(x => x.guid == "e").setNamespace);
            CollectionAssert.AreEqual(new[] { "Game.Fight" }, p.edits.Single(x => x.guid == "u").addUsings);
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
            Assert.IsEmpty(cfg.retiredAsmdefGuids);
            AssertReapplyIsEmpty();
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
            CollectionAssert.AreEqual(new[] { "Game.Combat" }, ed.references);
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

            m.rewriteNamespace = false; // assembly still changes
            p = Plan();
            Assert.AreEqual(("Payload", "", "Assembly-CSharp"), p.edits.Single(e => e.guid == "p1").movedFrom.Single());
            Assert.IsNull(p.edits.Single(e => e.guid == "p1").setNamespace);
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
            var r = new ValidationReport();
            r.edges.Add(new ModuleEdge { from = "Combat", to = "Core" });
            r.edges.Add(new ModuleEdge { from = "Combat", to = "UI" });
            r.edges.Add(new ModuleEdge { from = "Combat.Editor", to = "UI.Editor" });
            r.edges.Add(new ModuleEdge { from = "UI", to = "Core" });
            r.asmdefRefs["Combat"] = new HashSet<string> { new string('b', 32), "Lib.Runtime" };
            CollectionAssert.AreEqual(new[] { "GUID:" + new string('a', 32), "GUID:" + new string('b', 32), "Game.UI", "Lib.Runtime", "Some.Dll" },
                ApplyPlanner.ComputeReferences(combat, false, r, cfg));
            CollectionAssert.AreEqual(new[] { "GUID:" + new string('c', 32), "Game.UI.Editor", "Some.Dll" },
                ApplyPlanner.ComputeReferences(combat, true, r, cfg));
        }
    }
}
