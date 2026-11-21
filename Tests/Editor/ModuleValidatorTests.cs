using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class ModuleValidatorTests
    {
        const string S = "Assets/_Scripts/";
        ModuleConfig cfg;
        Dictionary<string, string> sources;

        [SetUp]
        public void SetUp()
        {
            cfg = ScriptableObject.CreateInstance<ModuleConfig>();
            cfg.scopeFolders = new List<string> { "Assets/_Scripts" };
            sources = new Dictionary<string, string>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(cfg);

        static ScanResult Scan(params (string path, string guid, AssemblyKind kind, string[] declares, string[] refs)[] files)
        {
            var s = new ScanResult();
            foreach (var (path, guid, kind, declares, refs) in files)
            {
                s.files[guid] = new ScriptFile { guid = guid, path = path, kind = kind, declaredTypes = declares.ToList(), referencedTypes = new HashSet<string>(refs) };
                foreach (var t in declares)
                {
                    if (!s.types.TryGetValue(t, out var rec))
                        rec = s.types[t] = new TypeRecord { fullName = t, ns = Ns(t), kind = kind, assemblyName = kind.ToString() };
                    rec.filePaths.Add(path);
                }
            }
            return s;
        }

        static string Ns(string fullName) => fullName.Contains('.') ? fullName.Substring(0, fullName.LastIndexOf('.')) : "";

        static (string, string, AssemblyKind, string[], string[]) F(string path, string guid, AssemblyKind kind, string declares, params string[] refs) =>
            (path, guid, kind, declares == null ? new string[0] : new[] { declares }, refs);

        ValidationReport Validate(ScanResult scan) => ModuleValidator.Validate(cfg, scan, p =>
        {
            if (sources.TryGetValue(p, out var src)) return src;
            var ns = scan.FileByPath(p).declaredTypes.Select(Ns).FirstOrDefault() ?? "";
            return ns.Length == 0 ? "class X {}" : "namespace " + ns + "\n{\n    class X {}\n}\n";
        });

        Module Assign(string module, ScanResult scan, params string[] guids)
        {
            var m = cfg.modules.FirstOrDefault(x => x.name == module) ?? cfg.AddModule(module);
            foreach (var g in guids) m.scripts.Add(new ScriptEntry { guid = g, originalPath = scan.files[g].path });
            return m;
        }

        static Issue Single(ValidationReport r, IssueCode code) => r.issues.Single(i => i.code == code);

        [Test]
        public void CrossModuleCycle_BlocksApply()
        {
            var scan = Scan(F(S + "Weapon.cs", "w", AssemblyKind.Runtime, "Game.Weapon", "Game.HUD"),
                F(S + "HUD.cs", "h", AssemblyKind.Runtime, "Game.HUD", "Game.Weapon"));
            Assign("Combat", scan, "w");
            Assign("UI", scan, "h");
            var r = Validate(scan);
            Assert.IsFalse(r.CanApply);
            Assert.AreEqual(1, r.cycles.Count);
            CollectionAssert.AreEqual(new[] { "Combat", "UI" }, r.cycles[0]);
            Assert.AreEqual(2, r.edges.Count);
            var cycle = Single(r, IssueCode.Cycle);
            Assert.AreEqual(Severity.Error, cycle.severity);
            StringAssert.Contains("HUD.cs", cycle.message);
            StringAssert.Contains("Weapon.cs", cycle.message);
        }

        [Test]
        public void OutOfScopeRef_ErrorWhenAssigned_WarningWhenNot()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A", "Legacy"),
                F("Assets/Old/Legacy.cs", "l", AssemblyKind.Runtime, "Legacy"));
            Assert.AreEqual(RefClass.OutOfScope, ReferenceClassifier.Classify(scan.files["a"], scan.types["Legacy"], cfg, scan));
            var r = Validate(scan);
            Assert.AreEqual(Severity.Warning, Single(r, IssueCode.OutOfScopeRef).severity);
            StringAssert.Contains("Assets/Old/Legacy.cs", Single(r, IssueCode.OutOfScopeRef).message);
            Assert.IsTrue(r.CanApply);

            Assign("Combat", scan, "a");
            r = Validate(scan);
            var i = Single(r, IssueCode.OutOfScopeRef);
            Assert.AreEqual(Severity.Error, i.severity);
            Assert.AreEqual(S + "A.cs", i.path);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void FirstPassRef_IsOutOfScope()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A", "Lib"),
                F("Assets/Plugins/Lib.cs", "l", AssemblyKind.FirstPass, "Lib"));
            Assert.AreEqual(RefClass.OutOfScope, ReferenceClassifier.Classify(scan.files["a"], scan.types["Lib"], cfg, scan));
            Assign("Combat", scan, "a");
            var r = Validate(scan);
            Assert.AreEqual(Severity.Error, Single(r, IssueCode.OutOfScopeRef).severity);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void UnassignedInScopeRef_ErrorOnlyWhenAssigned()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A", "Game.B"),
                F(S + "B.cs", "b", AssemblyKind.Runtime, "Game.B"));
            Assert.AreEqual(RefClass.UnassignedInScope, ReferenceClassifier.Classify(scan.files["a"], scan.types["Game.B"], cfg, scan));
            Assert.IsEmpty(Validate(scan).issues);
            Assign("Combat", scan, "a");
            var r = Validate(scan);
            Assert.AreEqual(Severity.Error, Single(r, IssueCode.UnassignedInScopeRef).severity);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void ReverseRefFromOutside_WithNamespaceChange_IsError_WithoutChange_IsFine()
        {
            var scan = Scan(F(S + "Weapon.cs", "w", AssemblyKind.Runtime, "Game.Weapon"),
                F("Assets/Old/Legacy.cs", "l", AssemblyKind.Runtime, "Legacy", "Game.Weapon"));
            var m = Assign("Combat", scan, "w");
            var r = Validate(scan);
            var i = Single(r, IssueCode.ReverseRefOutOfScope);
            Assert.AreEqual(Severity.Error, i.severity);
            Assert.AreEqual("Assets/Old/Legacy.cs", i.path);
            Assert.IsFalse(r.CanApply);

            m.rewriteNamespace = false;
            r = Validate(scan);
            Assert.IsFalse(r.issues.Any(x => x.code == IssueCode.ReverseRefOutOfScope));
            Assert.IsTrue(r.CanApply);
        }

        [Test]
        public void PartialSplitAcrossModules_IsError()
        {
            var scan = Scan(F(S + "P.Part1.cs", "p1", AssemblyKind.Runtime, "Game.P"),
                F(S + "P.Part2.cs", "p2", AssemblyKind.Runtime, "Game.P"));
            Assign("Combat", scan, "p1");
            var r = Validate(scan);
            Assert.AreEqual(Severity.Error, Single(r, IssueCode.PartialSplit).severity);
            Assert.IsFalse(r.CanApply);

            Assign("Combat", scan, "p2");
            Assert.IsTrue(Validate(scan).CanApply);
        }

        [Test]
        public void RuntimeReferencingEditor_IsError_EditorReferencingRuntime_IsFine()
        {
            var scan = Scan(F(S + "R.cs", "r", AssemblyKind.Runtime, "Game.R"),
                F(S + "Editor/Ed.cs", "e", AssemblyKind.Editor, "Game.Ed", "Game.R"));
            Assign("Combat", scan, "r", "e");
            var r = Validate(scan);
            Assert.IsTrue(r.CanApply, string.Join("\n", r.issues.Select(i => i.message)));
            Assert.IsTrue(r.edges.Any(x => x.from == "Combat.Editor" && x.to == "Combat"));

            scan.files["r"].referencedTypes.Add("Game.Ed");
            r = Validate(scan);
            var i = Single(r, IssueCode.RuntimeToEditor);
            Assert.AreEqual(Severity.Error, i.severity);
            Assert.AreEqual(S + "R.cs", i.path);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void EditorOnlyAsmdefFile_IsEditor()
        {
            var scan = Scan(F(S + "R.cs", "r", AssemblyKind.Runtime, "Game.R", "Game.Combat.Editor.Ed"),
                F("Assets/Modules/Combat/Editor/Ed.cs", "e", AssemblyKind.AsmDef, "Game.Combat.Editor.Ed"));
            scan.files["e"].asmdefGuid = scan.types["Game.Combat.Editor.Ed"].asmdefGuid = "edasm";
            scan.files["e"].editorOnlyAssembly = true;
            Assert.IsTrue(scan.files["e"].IsEditor);
            var m = Assign("Combat", scan, "r", "e");
            m.editorAsmdefGuid = "edasm";
            Assert.IsTrue(ModuleValidator.IsAssignable(scan.files["e"], cfg));
            var r = Validate(scan);
            Assert.AreEqual(Severity.Error, Single(r, IssueCode.RuntimeToEditor).severity);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void SameSimpleNameSameTargetNamespace_IsNameClash()
        {
            var scan = Scan(F(S + "Foo.cs", "f1", AssemblyKind.Runtime, "Game.A.Foo"),
                F(S + "Foo2.cs", "f2", AssemblyKind.Runtime, "Game.B.Foo"),
                F(S + "Box.cs", "b1", AssemblyKind.Runtime, "Game.A.Box`1"),
                F(S + "Box2.cs", "b2", AssemblyKind.Runtime, "Game.B.Box"));
            Assign("Combat", scan, "f1", "f2", "b1", "b2");
            var r = Validate(scan);
            var i = Single(r, IssueCode.NameClash);
            Assert.AreEqual(Severity.Error, i.severity);
            StringAssert.Contains("Foo2.cs", i.message);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void OtherAsmdefRef_GoesToAsmdefRefs()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A", "Lib.Thing"));
            scan.types["Lib.Thing"] = new TypeRecord { fullName = "Lib.Thing", ns = "Lib", kind = AssemblyKind.AsmDef, asmdefGuid = "libguid" };
            Assert.AreEqual(RefClass.OtherAsmdef, ReferenceClassifier.Classify(scan.files["a"], scan.types["Lib.Thing"], cfg, scan));
            Assign("Combat", scan, "a");
            var r = Validate(scan);
            Assert.IsEmpty(r.issues);
            Assert.IsTrue(r.CanApply);
            CollectionAssert.AreEquivalent(new[] { "libguid" }, r.asmdefRefs["Combat"]);

            scan.types["Lib.NoGuid"] = new TypeRecord { fullName = "Lib.NoGuid", ns = "Lib", kind = AssemblyKind.AsmDef, assemblyName = "Lib.Runtime" };
            scan.files["a"].referencedTypes.Add("Lib.NoGuid");
            CollectionAssert.AreEquivalent(new[] { "libguid", "Lib.Runtime" }, Validate(scan).asmdefRefs["Combat"]);
        }

        [Test]
        public void NonAutoDll_ErrorWhenAssigned_AutoDllIgnored()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A", "Dll.Manual", "Dll.Auto", "UnityEngine.Object"));
            scan.types["Dll.Manual"] = new TypeRecord { fullName = "Dll.Manual", kind = AssemblyKind.Precompiled };
            scan.types["Dll.Auto"] = new TypeRecord { fullName = "Dll.Auto", kind = AssemblyKind.Precompiled, autoReferencedDll = true };
            scan.types["UnityEngine.Object"] = new TypeRecord { fullName = "UnityEngine.Object", kind = AssemblyKind.Engine };
            Assert.IsEmpty(Validate(scan).issues);
            Assign("Combat", scan, "a");
            var r = Validate(scan);
            Assert.AreEqual(Severity.Error, Single(r, IssueCode.NonAutoDll).severity);
            Assert.AreEqual(1, r.issues.Count);
        }

        [Test]
        public void QualifiedOldName_IsWarning_NotBlocking()
        {
            var scan = Scan(F(S + "Weapon.cs", "w", AssemblyKind.Runtime, "Game.Weapon"),
                F(S + "User.cs", "u", AssemblyKind.Runtime, "Game.User", "Game.Weapon"));
            sources[S + "User.cs"] = "namespace Game\n{\n    class User\n    {\n        Game.Weapon w;\n    }\n}\n";
            Assign("Combat", scan, "w");
            var r = Validate(scan);
            var i = Single(r, IssueCode.QualifiedName);
            Assert.AreEqual(Severity.Warning, i.severity);
            Assert.AreEqual(S + "User.cs", i.path);
            Assert.IsTrue(r.CanApply);
        }

        [Test]
        public void MultiNamespaceFile_IsWarning_AndKeepsItsNamespace()
        {
            var scan = Scan(F(S + "Multi.cs", "m", AssemblyKind.Runtime, "Game.Multi"),
                F("Assets/Old/Legacy.cs", "l", AssemblyKind.Runtime, "Legacy", "Game.Multi"));
            sources[S + "Multi.cs"] = "namespace Game { class Multi {} }\nnamespace Other { class Y {} }\n";
            Assign("Combat", scan, "m");
            var r = Validate(scan);
            Assert.AreEqual(Severity.Warning, Single(r, IssueCode.MultiNamespace).severity);
            Assert.IsTrue(r.CanApply);
        }

        [Test]
        public void ThirdPartyAsmdefFileInScope_IsNotAssignable()
        {
            var scan = Scan(F(S + "Third/T.cs", "t", AssemblyKind.AsmDef, "Third.T"),
                F(S + "Mine/M.cs", "m", AssemblyKind.AsmDef, "Game.Combat.M"));
            scan.files["t"].asmdefGuid = "third";
            scan.files["m"].asmdefGuid = "mine";
            Assert.IsFalse(ModuleValidator.IsAssignable(scan.files["t"], cfg));
            var mod = Assign("Combat", scan, "m");
            mod.asmdefGuid = "mine";
            Assert.IsTrue(ModuleValidator.IsAssignable(scan.files["m"], cfg));
            Assert.IsTrue(Validate(scan).CanApply);

            Assign("Combat", scan, "t");
            var r = Validate(scan);
            var i = Single(r, IssueCode.OutOfScopeRef);
            Assert.AreEqual(Severity.Error, i.severity);
            Assert.AreEqual(S + "Third/T.cs", i.path);
        }

        [Test]
        public void PartialSplitAcrossTwoModules_IsError()
        {
            var scan = Scan(F(S + "P.Part1.cs", "p1", AssemblyKind.Runtime, "Game.P"),
                F(S + "P.Part2.cs", "p2", AssemblyKind.Runtime, "Game.P"));
            Assign("Combat", scan, "p1");
            Assign("UI", scan, "p2");
            var r = Validate(scan);
            Assert.AreEqual(Severity.Error, Single(r, IssueCode.PartialSplit).severity);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void PartialPartsInDifferentTargetNamespaces_IsError()
        {
            var scan = Scan(F(S + "A/P1.cs", "p1", AssemblyKind.Runtime, "Game.P"),
                F(S + "B/P2.cs", "p2", AssemblyKind.Runtime, "Game.P"));
            Assign("Combat", scan, "p1", "p2");
            var r = Validate(scan);
            StringAssert.Contains("Game.Combat.B", Single(r, IssueCode.PartialSplit).message);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void PartialPartNotRewritable_SplitsNamespace_IsError()
        {
            var scan = Scan(F(S + "P1.cs", "p1", AssemblyKind.Runtime, "Game.P"),
                F(S + "P2.cs", "p2", AssemblyKind.Runtime, "Game.P"));
            sources[S + "P2.cs"] = "namespace Game { partial class P {} }\nnamespace Other { class Y {} }\n";
            Assign("Combat", scan, "p1", "p2");
            var r = Validate(scan);
            Assert.AreEqual(Severity.Error, Single(r, IssueCode.PartialSplit).severity);
            Assert.AreEqual(Severity.Warning, Single(r, IssueCode.MultiNamespace).severity);
        }

        [Test]
        public void MovingIntoNamespaceWithExistingSameName_IsNameClash()
        {
            var scan = Scan(F(S + "Existing/Foo.cs", "e", AssemblyKind.Runtime, "Game.Combat.Foo"),
                F(S + "Foo.cs", "f", AssemblyKind.Runtime, "Game.Old.Foo"));
            Assign("Combat", scan, "f");
            var r = Validate(scan);
            var i = Single(r, IssueCode.NameClash);
            Assert.AreEqual(S + "Foo.cs", i.path);
            StringAssert.Contains(S + "Existing/Foo.cs", i.message);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void AddedUsing_MakingNameAmbiguous_IsNameClash()
        {
            var scan = Scan(F(S + "Weapon.cs", "w", AssemblyKind.Runtime, "Game.Weapon"),
                F(S + "Foo.cs", "f", AssemblyKind.Runtime, "Game.Stuff.Foo"),
                F(S + "UI/User.cs", "u", AssemblyKind.Runtime, "Game.UI.User", "Game.Weapon", "Lib.Foo"));
            scan.types["Lib.Foo"] = new TypeRecord { fullName = "Lib.Foo", ns = "Lib", kind = AssemblyKind.AsmDef, asmdefGuid = "lib" };
            sources[S + "UI/User.cs"] = "using Lib;\nnamespace Game.UI\n{\n    class User\n    {\n        Foo f;\n        Weapon w;\n    }\n}\n";
            Assign("Combat", scan, "w");
            Assert.IsTrue(Validate(scan).CanApply); // Foo stays in Game.Stuff: `using Game.Combat;` adds no second Foo

            Assign("Combat", scan, "f");
            var r = Validate(scan);
            var i = Single(r, IssueCode.NameClash);
            Assert.AreEqual(S + "UI/User.cs", i.path);
            StringAssert.Contains("Game.Combat", i.message);
            Assert.IsFalse(r.CanApply);
        }

        [TestCase("namespace Game.UI { class User { Weapon w; } }")] // System.Object only implicit (base type)
        [TestCase("namespace Game.UI { class User { System.Object o; Weapon w; } }")]
        [TestCase("namespace Game.UI { class User { global::System.Object o; Weapon w; } }")]
        [TestCase("using Object = UnityEngine.Object;\nnamespace Game.UI { class User { Object o; Weapon w; } }")]
        [TestCase("namespace Game.UI { class User { Weapon w; /* Object */ string s = \"Object\"; } }")]
        public void AddedUsing_NameNotWrittenUnqualified_IsNotNameClash(string source)
        {
            var scan = Scan(F(S + "Weapon.cs", "w", AssemblyKind.Runtime, "Game.Weapon"),
                F(S + "Object.cs", "o", AssemblyKind.Runtime, "Game.Stuff.Object"),
                F(S + "UI/User.cs", "u", AssemblyKind.Runtime, "Game.UI.User", "Game.Weapon", "System.Object", "UnityEngine.Object"));
            scan.types["System.Object"] = new TypeRecord { fullName = "System.Object", ns = "System", kind = AssemblyKind.Engine };
            scan.types["UnityEngine.Object"] = new TypeRecord { fullName = "UnityEngine.Object", ns = "UnityEngine", kind = AssemblyKind.Engine };
            sources[S + "UI/User.cs"] = source;
            Assign("Combat", scan, "w", "o");
            var r = Validate(scan);
            Assert.IsTrue(r.CanApply, string.Join("\n", r.issues));
        }

        [Test]
        public void AddedUsing_AmbiguousOuterOfNestedExternalType_IsNameClash()
        {
            var scan = Scan(F(S + "Weapon.cs", "w", AssemblyKind.Runtime, "Game.Weapon"),
                F(S + "Environment.cs", "e", AssemblyKind.Runtime, "Game.Stuff.Environment"),
                F(S + "UI/User.cs", "u", AssemblyKind.Runtime, "Game.UI.User", "Game.Weapon", "System.Environment/SpecialFolder"));
            scan.types["System.Environment/SpecialFolder"] = new TypeRecord { fullName = "System.Environment/SpecialFolder", ns = "System", kind = AssemblyKind.Engine };
            sources[S + "UI/User.cs"] = "using System;\nnamespace Game.UI { class User { Environment.SpecialFolder f; Weapon w; } }";
            Assign("Combat", scan, "w", "e");
            var r = Validate(scan);
            var i = Single(r, IssueCode.NameClash);
            Assert.AreEqual(S + "UI/User.cs", i.path);
            StringAssert.Contains("System.Environment", i.message);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void PendingRemoval_OfRetiredModule_IsUnassigned_AndRestoresNamespace()
        {
            var scan = Scan(F("Assets/Modules/Old/X.cs", "x", AssemblyKind.AsmDef, "Game.Old.X"),
                F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A", "Game.Old.X"),
                F("Assets/Legacy/L.cs", "l", AssemblyKind.Runtime, "L", "Game.Old.X"));
            scan.files["x"].asmdefGuid = scan.types["Game.Old.X"].asmdefGuid = "oldguid";
            cfg.retiredAsmdefGuids.Add("oldguid");
            var entry = new ScriptEntry { guid = "x", originalPath = S + "X.cs", originalNamespace = "Game", applied = true };
            cfg.pendingRemovals.Add(entry);
            Assert.IsTrue(ModuleValidator.IsAssignable(scan.files["x"], cfg));
            Assign("Combat", scan, "a");
            Assert.AreEqual(RefClass.UnassignedInScope, ReferenceClassifier.Classify(scan.files["a"], scan.types["Game.Old.X"], cfg, scan));

            var r = Validate(scan);
            Assert.AreEqual(Severity.Error, Single(r, IssueCode.UnassignedInScopeRef).severity);
            Assert.AreEqual("Assets/Legacy/L.cs", Single(r, IssueCode.ReverseRefOutOfScope).path);
            Assert.IsFalse(r.asmdefRefs.ContainsKey("Combat"));

            entry.originalNamespace = "Game.Old";
            Assert.IsFalse(Validate(scan).issues.Any(i => i.code == IssueCode.ReverseRefOutOfScope));
        }

        [Test]
        public void GuidInTwoModules_IsDuplicateAssignment()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A"));
            Assign("Combat", scan, "a");
            Assign("UI", scan, "a");
            var r = Validate(scan);
            var i = Single(r, IssueCode.DuplicateAssignment);
            Assert.AreEqual(Severity.Error, i.severity);
            Assert.AreEqual(S + "A.cs", i.path);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void UnassignedFileInsideModuleFolder_IsCapturedByModule()
        {
            var scan = Scan(F(S + "Weapon.cs", "w", AssemblyKind.Runtime, "Game.Weapon"),
                F(S + "Combat/Stray.cs", "s", AssemblyKind.Runtime, "Game.Stray"));
            Assign("Combat", scan, "w");
            var r = Validate(scan);
            var i = Single(r, IssueCode.CapturedByModule);
            Assert.AreEqual(Severity.Error, i.severity);
            Assert.AreEqual(S + "Combat/Stray.cs", i.path);
            StringAssert.Contains("assign it to Combat or move it out of " + S + "Combat", i.message);
            Assert.IsFalse(r.CanApply);

            Assign("Combat", scan, "s");
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.CapturedByModule));
        }

        [Test]
        public void ToolAsmdefFile_NeitherAssignedNorPending_IsCaptured()
        {
            var scan = Scan(F(S + "Combat/Stray.cs", "s", AssemblyKind.AsmDef, "Game.Combat.Stray"));
            scan.files["s"].asmdefGuid = "a1";
            cfg.AddModule("Combat").asmdefGuid = "a1";
            var i = Single(Validate(scan), IssueCode.CapturedByModule);
            Assert.AreEqual(Severity.Error, i.severity);
            StringAssert.Contains("assign it or Sync first", i.message);
        }

        [Test]
        public void PendingRemoval_RestoringIntoModuleFolder_IsRestoreConflict()
        {
            var scan = Scan(F(S + "Combat/Weapon.cs", "w", AssemblyKind.AsmDef, "Game.Combat.Weapon"),
                F(S + "Combat/Armor.cs", "a", AssemblyKind.AsmDef, "Game.Combat.Armor"));
            scan.files["w"].asmdefGuid = scan.files["a"].asmdefGuid = "a1";
            var m = Assign("Combat", scan, "a");
            m.asmdefGuid = "a1";
            // It lived in the folder named like the module before the first Apply; unassigned, it goes back there.
            cfg.pendingRemovals.Add(new ScriptEntry { guid = "w", originalPath = S + "Combat/Weapon.cs", originalNamespace = "Game", applied = true });
            var i = Single(Validate(scan), IssueCode.RestoreConflict);
            Assert.AreEqual(Severity.Error, i.severity);
            StringAssert.Contains("move it out of " + S + "Combat", i.message);
        }

        [Test]
        public void InvalidModuleNamespace_IsError_AndFolderSegmentsAreSanitized()
        {
            Assert.AreEqual("_1st", PathRules.Identifier("1st"));
            Assert.AreEqual("My_Stuff", PathRules.Identifier("My Stuff"));
            Assert.AreEqual("a_b", PathRules.Identifier("a-b"));
            Assert.AreEqual("_class", PathRules.Identifier("class"));
            Assert.AreEqual("Ok", PathRules.Identifier("Ok"));
            Assert.AreEqual("Game.Combat._3D.My_Stuff", PathRules.TargetNamespace(new Module { namespaceName = "Game.Combat" }, "3D/My Stuff", false));
            Assert.IsTrue(PathRules.IsValidNamespace("Game.Combat_2"));
            foreach (var bad in new[] { "Game.1x", "Game..A", "Game.class", "Game Combat", "", "Game.A-B" })
                Assert.IsFalse(PathRules.IsValidNamespace(bad), bad);

            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A"));
            Assign("Combat", scan, "a").namespaceName = "Game.class";
            var r = Validate(scan);
            Assert.AreEqual(Severity.Error, Single(r, IssueCode.InvalidNamespace).severity);
            Assert.IsFalse(r.CanApply);
        }

        [Test]
        public void UserAsmdefFile_UsingATypeWhoseNamespaceChanges_IsReverseRefError()
        {
            var scan = Scan(F(S + "Combat/Weapon.cs", "w", AssemblyKind.AsmDef, "Game.Combat.Weapon"),
                F("Assets/Lib/Tool.cs", "t", AssemblyKind.AsmDef, "Lib.Tool", "Game.Combat.Weapon"));
            scan.files["w"].asmdefGuid = "a1";
            scan.files["t"].asmdefGuid = "u1";
            var m = Assign("Combat", scan, "w");
            m.asmdefGuid = "a1";
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.ReverseRefOutOfScope));
            m.namespaceName = "Game.Fight";
            var i = Single(Validate(scan), IssueCode.ReverseRefOutOfScope);
            Assert.AreEqual("Assets/Lib/Tool.cs", i.path);
        }

        [Test]
        public void InternalUseAcrossTheNewBoundary_IsError()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A"),
                F(S + "B.cs", "b", AssemblyKind.Runtime, "Game.B", "Game.A"));
            scan.files["b"].internalRefs.Add("Game.A");
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.InternalAccess), "same assembly today, nothing moves");
            Assign("Combat", scan, "a");
            var i = Single(Validate(scan), IssueCode.InternalAccess);
            Assert.AreEqual(Severity.Error, i.severity);
            Assert.AreEqual(S + "B.cs", i.path);
            StringAssert.Contains("Game.Combat grants no InternalsVisibleTo(\"Assembly-CSharp\")", i.message);
            Assign("Combat", scan, "b");
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.InternalAccess));
        }

        [Test]
        public void InternalUse_OfAPackageType_NeedsInternalsVisibleToTheFinalAssembly()
        {
            var scan = Scan(F(S + "B.cs", "b", AssemblyKind.Runtime, "Game.B", "Pkg.Secret"));
            scan.types["Pkg.Secret"] = new TypeRecord { fullName = "Pkg.Secret", ns = "Pkg", assemblyName = "Pkg.Core", kind = AssemblyKind.AsmDef };
            scan.files["b"].internalRefs.Add("Pkg.Secret");
            scan.internalsVisibleTo["Pkg.Core"] = new HashSet<string> { "Assembly-CSharp" };
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.InternalAccess), "compiles today through IVT");
            Assign("Combat", scan, "b");
            var i = Single(Validate(scan), IssueCode.InternalAccess);
            StringAssert.Contains("Pkg.Core grants no InternalsVisibleTo(\"Game.Combat\")", i.message);
            scan.internalsVisibleTo["Pkg.Core"].Add("Game.Combat");
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.InternalAccess), "granted to the final assembly too");
        }

        [Test]
        public void InternalUse_GrantedToTheFinalAssembly_IsFine_ButNotWhenTheDeclarerMovesToo()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A"),
                F(S + "B.cs", "b", AssemblyKind.Runtime, "Game.B", "Game.A"));
            scan.files["b"].internalRefs.Add("Game.A");
            scan.internalsVisibleTo["Assembly-CSharp"] = new HashSet<string> { "Game.UI" };
            Assign("UI", scan, "b");
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.InternalAccess), "Assembly-CSharp grants Game.UI");
            Assign("Combat", scan, "a");
            Assert.AreEqual(Severity.Error, Single(Validate(scan), IssueCode.InternalAccess).severity, "the grant stays behind in Assembly-CSharp");
        }

        [Test]
        public void InternalUse_OfACompilerEmittedTypeOfItsOwnAssembly_IsIgnored()
        {
            var scan = Scan(F(S + "B.cs", "b", AssemblyKind.Runtime, "Game.B", "System.Runtime.CompilerServices.IsReadOnlyAttribute"));
            scan.files["b"].assemblyName = "Assembly-CSharp";
            scan.types["System.Runtime.CompilerServices.IsReadOnlyAttribute"] = new TypeRecord
                { fullName = "System.Runtime.CompilerServices.IsReadOnlyAttribute", ns = "System.Runtime.CompilerServices", assemblyName = "Assembly-CSharp", kind = AssemblyKind.Runtime };
            scan.files["b"].internalRefs.Add("System.Runtime.CompilerServices.IsReadOnlyAttribute");
            Assign("Combat", scan, "b");
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.InternalAccess), "the compiler emits it into the new assembly too");
        }

        [Test]
        public void PublicTypeDeclaredByTwoAssembliesTheFinalAssemblySees_IsDuplicateTypeName()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A", "Json.Node"));
            var pkg = new TypeRecord { fullName = "Json.Node", ns = "Json", assemblyName = "Pkg.Json", kind = AssemblyKind.AsmDef, asmdefGuid = "0123456789abcdef0123456789abcdef" };
            var dll = new TypeRecord { fullName = "Json.Node", ns = "Json", assemblyName = "Vendor.Json", kind = AssemblyKind.Precompiled, autoReferencedDll = true };
            scan.types["Json.Node"] = pkg;
            scan.publicDeclarers["Json.Node"] = new List<TypeRecord> { pkg, dll };
            dll.autoReferencedDll = false;
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.DuplicateTypeName), "Assembly-CSharp sees only Pkg.Json");
            dll.autoReferencedDll = true;
            Assign("Combat", scan, "a"); // references Pkg.Json by GUID and sees every auto-referenced DLL
            var i = Single(Validate(scan), IssueCode.DuplicateTypeName);
            Assert.AreEqual(S + "A.cs", i.path);
            StringAssert.Contains("Pkg.Json and Vendor.Json", i.message);
            dll.autoReferencedDll = false;
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.DuplicateTypeName), "the DLL is not seen");
        }

        [Test]
        public void StayPutFile_SeesBothCopiesOnceItsOwnMovesOut_ButNotWhileItsAssemblyDeclaresTheName()
        {
            var scan = Scan(F(S + "Node.cs", "n", AssemblyKind.Runtime, "Json.Node"), F(S + "User.cs", "u", AssemblyKind.Runtime, "Game.User", "Json.Node"));
            var own = scan.types["Json.Node"];
            own.assemblyName = "Assembly-CSharp";
            var pkg = new TypeRecord { fullName = "Json.Node", ns = "Json", assemblyName = "Pkg.Json", kind = AssemblyKind.AsmDef, asmdefGuid = "0123456789abcdef0123456789abcdef" };
            scan.publicDeclarers["Json.Node"] = new List<TypeRecord> { own, pkg };
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.DuplicateTypeName), "Assembly-CSharp's own Json.Node wins (CS0436)");
            Assign("Combat", scan, "n"); // Node leaves Assembly-CSharp for the auto-referenced Game.Combat
            var i = Single(Validate(scan), IssueCode.DuplicateTypeName);
            Assert.AreEqual(S + "User.cs", i.path);
            StringAssert.Contains("Game.Combat and Pkg.Json", i.message);
        }

        [Test]
        public void DuplicateFullTypeName_IsErrorOnInScopeDeclarers()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A"));
            scan.duplicateTypes["Game.A"] = new List<string> { S + "A.cs", "Assets/Vendor/A.cs" };
            var i = Single(Validate(scan), IssueCode.DuplicateTypeName);
            Assert.AreEqual(Severity.Error, i.severity);
            Assert.AreEqual(S + "A.cs", i.path);
        }

        [Test]
        public void ModuleAsmdefName_TakenByAnotherAssemblyOrModule_IsError()
        {
            var scan = Scan(F(S + "A.cs", "a", AssemblyKind.Runtime, "Game.A"), F(S + "B.cs", "b", AssemblyKind.Runtime, "Game.B"));
            var combat = Assign("Combat", scan, "a");
            combat.asmdefGuid = "own";
            scan.assemblies["Game.Combat"] = "own"; // its own asmdef: fine
            scan.assemblies["Assembly-CSharp"] = "";
            Assert.IsFalse(Validate(scan).issues.Any(x => x.code == IssueCode.AssemblyNameClash));

            scan.assemblies["Game.UI"] = "userguid";
            Assign("UI", scan, "b");
            var i = Single(Validate(scan), IssueCode.AssemblyNameClash);
            StringAssert.Contains("Game.UI", i.message);

            scan.assemblies.Remove("Game.UI");
            cfg.modules[1].namespaceName = "Game.Combat";
            StringAssert.Contains("module Combat", Single(Validate(scan), IssueCode.AssemblyNameClash).message);
        }
    }
}
