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
    }
}
