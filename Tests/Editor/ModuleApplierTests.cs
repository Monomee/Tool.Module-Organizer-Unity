using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    // .txt files only: nothing here may trigger a compile.
    public class ModuleApplierTests
    {
        const string T = "Assets/_ApplierTmp";
        ModuleConfig cfg;

        [SetUp]
        public void SetUp()
        {
            cfg = ScriptableObject.CreateInstance<ModuleConfig>();
            AssetDatabase.CreateFolder("Assets", "_ApplierTmp");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(cfg);
            AssetDatabase.DeleteAsset(T);
            if (Directory.Exists(T)) Directory.Delete(T, true);
            File.Delete(T + ".meta");
        }

        static string Add(string path, byte[] bytes)
        {
            File.WriteAllBytes(path, bytes);
            AssetDatabase.ImportAsset(path);
            return AssetDatabase.AssetPathToGUID(path);
        }

        static string Add(string path, string text = "x") => Add(path, Encoding.UTF8.GetBytes(text));

        [Test]
        public void Move_PreservesGuid()
        {
            var guid = Add(T + "/a.txt");
            var errors = ModuleApplier.Execute(new ApplyPlan
            {
                createFolders = { T + "/M", T + "/M/Sub" },
                moves = { new MoveOp { guid = guid, from = T + "/a.txt", to = T + "/M/Sub/a.txt" } },
            }, cfg, refresh: false);
            CollectionAssert.IsEmpty(errors);
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(T + "/M/Sub/a.txt"));
            Assert.IsFalse(File.Exists(T + "/a.txt"));
        }

        [Test]
        public void TextEdit_PreservesBomAndCrlf()
        {
            var src = "namespace Old\r\n{\r\n    public class A {}\r\n}\r\n";
            var guid = Add(T + "/a.txt", new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(src)).ToArray());
            var edit = new TextEditOp { guid = guid, setNamespace = "New", addUsings = { "System" } };
            edit.movedFrom.Add(("A", "Old", "Assembly-CSharp"));
            var errors = ModuleApplier.Execute(new ApplyPlan
            {
                createFolders = { T + "/M" },
                moves = { new MoveOp { guid = guid, from = T + "/a.txt", to = T + "/M/a.txt" } },
                edits = { edit },
            }, cfg, refresh: false);
            CollectionAssert.IsEmpty(errors);
            var bytes = File.ReadAllBytes(T + "/M/a.txt");
            CollectionAssert.AreEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3));
            var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.AreEqual(
                "using System;\r\n\r\nnamespace New\r\n{\r\n" +
                "    [UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"Old\", sourceAssembly: \"Assembly-CSharp\")]\r\n" +
                "    public class A {}\r\n}\r\n", text);
        }

        [Test]
        public void TextEdit_NoBom_StaysWithoutBom()
        {
            var guid = Add(T + "/a.txt", "namespace Old\n{\n}\n");
            ModuleApplier.Execute(new ApplyPlan { edits = { new TextEditOp { guid = guid, setNamespace = "New" } } }, cfg, refresh: false);
            Assert.AreEqual("namespace New\n{\n}\n", File.ReadAllText(T + "/a.txt"));
            Assert.AreNotEqual(0xEF, File.ReadAllBytes(T + "/a.txt")[0]);
        }

        [Test]
        public void TextEdit_ReportsUnrewritableNamespaceAndMissingMovedFromTarget()
        {
            var guid = Add(T + "/a.txt", "namespace A { }\nnamespace B { }\n");
            var edit = new TextEditOp { guid = guid, setNamespace = "New", addUsings = { "System" } };
            edit.movedFrom.Add(("Missing", "A", "Assembly-CSharp"));
            var errors = ModuleApplier.Execute(new ApplyPlan { edits = { edit } }, cfg, refresh: false);
            Assert.AreEqual(2, errors.Count, string.Join("\n", errors));
            StringAssert.Contains(T + "/a.txt", errors[0]);
            Assert.AreEqual("MovedFrom target not found: Missing in " + T + "/a.txt", errors[1]);
            Assert.AreEqual("using System;\n\nnamespace A { }\nnamespace B { }\n", File.ReadAllText(T + "/a.txt")); // other edits still applied
        }

        [Test]
        public void FailedMove_SkipsItsEdits_AndReleasesAssetEditing()
        {
            var edit = new TextEditOp { guid = "0123456789abcdef0123456789abcdef", setNamespace = "New" };
            var errors = ModuleApplier.Execute(new ApplyPlan
            {
                moves = { new MoveOp { guid = edit.guid, from = T + "/missing.txt", to = T + "/b.txt" } },
                edits = { edit },
            }, cfg, refresh: false);
            Assert.AreEqual(1, errors.Count, string.Join("\n", errors));
            StringAssert.Contains("missing.txt", errors[0]);
            // Asset editing was stopped: an import is visible right away.
            var guid = Add(T + "/c.txt");
            Assert.AreEqual(T + "/c.txt", AssetDatabase.GUIDToAssetPath(guid));
        }

        [Test]
        public void PlanWithErrors_DoesNothing()
        {
            var plan = new ApplyPlan { createFolders = { T + "/M" } };
            plan.issues.Add(new Issue { severity = Severity.Error, code = IssueCode.PathConflict, path = "p", message = "taken" });
            var errors = ModuleApplier.Execute(plan, cfg, refresh: false);
            Assert.AreEqual(1, errors.Count);
            Assert.IsFalse(AssetDatabase.IsValidFolder(T + "/M"));
        }

        [Test]
        public void DeleteFoldersIfEmpty_KeepsNonEmpty()
        {
            AssetDatabase.CreateFolder(T, "E");
            AssetDatabase.CreateFolder(T, "K");
            AssetDatabase.CreateFolder(T, "D");
            AssetDatabase.CreateFolder(T + "/D", "Sub");
            Add(T + "/K/k.txt");
            var guid = Add(T + "/D/Sub/d.txt");
            var errors = ModuleApplier.Execute(new ApplyPlan
            {
                moves = { new MoveOp { guid = guid, from = T + "/D/Sub/d.txt", to = T + "/d.txt" } },
                deleteFoldersIfEmpty = { T + "/D/Sub", T + "/D", T + "/E", T + "/K" },
            }, cfg, refresh: false);
            CollectionAssert.IsEmpty(errors);
            Assert.IsFalse(AssetDatabase.IsValidFolder(T + "/E"));
            Assert.IsFalse(AssetDatabase.IsValidFolder(T + "/D"), "emptied by its child's deletion");
            Assert.IsFalse(Directory.Exists(T + "/D"));
            Assert.IsTrue(AssetDatabase.IsValidFolder(T + "/K"));
            Assert.AreEqual(guid, AssetDatabase.AssetPathToGUID(T + "/d.txt"));
        }

        [Test]
        public void NewAsmdef_WritesMetaWithPlannedGuid_AndRecordsConfig()
        {
            const string guid = "0123456789abcdef0123456789abcdef";
            var m = cfg.AddModule("Mod");
            m.scripts.Add(new ScriptEntry { guid = "s1" });
            cfg.pendingRemovals.Add(new ScriptEntry { guid = "r1" });
            cfg.pendingRemovals.Add(new ScriptEntry { guid = "r2" });
            cfg.retiredAsmdefGuids.Add("old");
            var op = new AsmdefOp
            {
                path = T + "/Mod/Editor/Game.Mod.Editor.asmdef", guid = guid, name = "Game.Mod.Editor", rootNamespace = "Game.Mod.Editor",
                editorOnly = true, moduleName = "Mod",
            };
            var existing = new AsmdefOp { path = T + "/Mod/Game.Mod.asmdef", guid = "fedcba9876543210fedcba9876543210", name = "Game.Mod", moduleName = "Mod" };
            try
            {
                AssetDatabase.CreateFolder(T, "Mod");
                File.WriteAllText(existing.path + ".meta", "keep"); // stands in for an already-imported asmdef
                var errors = ModuleApplier.Execute(new ApplyPlan
                {
                    createFolders = { T + "/Mod/Editor" }, asmdefs = { existing, op }, removeEntries = { "r1" }, clearRetiredAsmdefGuids = { "old" },
                }, cfg, refresh: false);
                CollectionAssert.IsEmpty(errors);
                Assert.AreEqual(AsmdefJson.Write(op), File.ReadAllText(op.path));
                Assert.AreEqual("fileFormatVersion: 2\nguid: " + guid + "\nAssemblyDefinitionImporter:\n  externalObjects: {}\n  userData: \n" +
                                "  assetBundleName: \n  assetBundleVariant: \n", File.ReadAllText(op.path + ".meta"));
                Assert.AreEqual(AsmdefJson.Write(existing), File.ReadAllText(existing.path));
                Assert.AreEqual("keep", File.ReadAllText(existing.path + ".meta"));
                Assert.AreEqual(guid, m.editorAsmdefGuid);
                Assert.AreEqual(existing.guid, m.asmdefGuid);
                Assert.IsTrue(m.scripts[0].applied);
                CollectionAssert.AreEqual(new[] { "r2" }, cfg.pendingRemovals.Select(e => e.guid));
                CollectionAssert.IsEmpty(cfg.retiredAsmdefGuids);
            }
            finally
            {
                // Never let an asmdef get imported (that would compile): remove it before any refresh.
                foreach (var p in new[] { op.path, existing.path })
                {
                    File.Delete(p);
                    File.Delete(p + ".meta");
                }
            }
        }

        [Test]
        public void HasUncommittedGitChanges_FalseOutsideARepo()
        {
            var dir = Path.Combine(Path.GetTempPath(), "applier-nogit-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                Assert.IsFalse(ModuleApplier.HasUncommittedGitChanges(dir));
                Assert.IsFalse(ModuleApplier.HasUncommittedGitChanges(dir + "/does-not-exist"));
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
