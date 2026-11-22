using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class ModuleEditsTests
    {
        ModuleConfig cfg;
        ScanResult scan;
        Dictionary<string, string> sources;
        Module alpha, beta;

        [SetUp]
        public void SetUp()
        {
            cfg = ScriptableObject.CreateInstance<ModuleConfig>();
            cfg.rootNamespace = "Game";
            cfg.scopeFolders = new List<string> { "Assets/_Scripts" };
            alpha = cfg.AddModule("Alpha"); // Assets/_Scripts/Alpha
            beta = cfg.AddModule("Beta");
            scan = new ScanResult();
            sources = new Dictionary<string, string>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(cfg);

        void File(string guid, string path, string src = "class X {}", AssemblyKind kind = AssemblyKind.Runtime)
        {
            scan.files[guid] = new ScriptFile { guid = guid, path = path, kind = kind };
            sources[path] = src;
        }

        int Assign(Module m, params string[] guids) => ModuleEdits.Assign(cfg, scan, guids, m, p => sources.TryGetValue(p, out var s) ? s : null);

        [Test]
        public void Assign_New_RecordsCurrentPathNamespaceAndSubfolder()
        {
            File("g1", "Assets/_Scripts/Weapons/Gun.cs", "namespace Old.Ns\n{\n    class Gun {}\n}\n");
            File("g2", "Assets/_Scripts/Global.cs");
            File("out", "Assets/Other/Out.cs");
            File("fp", "Assets/_Scripts/Plugins/Fp.cs", kind: AssemblyKind.FirstPass);

            Assert.That(Assign(alpha, "g1", "g2", "out", "fp", "missing"), Is.EqualTo(2));
            var e = alpha.scripts.Single(x => x.guid == "g1");
            Assert.That((e.originalPath, e.originalNamespace, e.relativeDir, e.applied),
                Is.EqualTo(("Assets/_Scripts/Weapons/Gun.cs", "Old.Ns", "Weapons", false)));
            Assert.That(alpha.scripts.Single(x => x.guid == "g2").originalNamespace, Is.EqualTo(""));
            Assert.That(alpha.scripts.Select(x => x.guid), Is.EquivalentTo(new[] { "g1", "g2" }));
        }

        [Test]
        public void Assign_IsUndoable()
        {
            File("g1", "Assets/_Scripts/Gun.cs");
            Undo.IncrementCurrentGroup();
            Assign(alpha, "g1");
            Undo.PerformUndo();
            Assert.That(cfg.modules[0].scripts, Is.Empty);
        }

        [Test]
        public void MoveAtoB_KeepsHistory_RecomputesSubfolder()
        {
            File("g1", "Assets/_Scripts/Alpha/Gun.cs"); // applied into Alpha
            alpha.scripts.Add(new ScriptEntry
                { guid = "g1", originalPath = "Assets/_Scripts/Weapons/Gun.cs", originalNamespace = "Old", relativeDir = "Weapons", applied = true });

            Assert.That(Assign(beta, "g1"), Is.EqualTo(1));
            Assert.That(alpha.scripts, Is.Empty);
            var e = beta.scripts.Single();
            Assert.That((e.guid, e.originalPath, e.originalNamespace, e.relativeDir, e.applied),
                Is.EqualTo(("g1", "Assets/_Scripts/Weapons/Gun.cs", "Old", "Weapons", false)));
        }

        [Test]
        public void MoveAtoB_OfAnEntryAcceptedFromDisk_KeepsItsSubfolderInsideTheModule()
        {
            // Accepted from disk: originalPath is inside Alpha's folder; scope-relative it would be Alpha/Sub (doubled).
            File("g1", "Assets/_Scripts/Alpha/Sub/Gun.cs");
            alpha.scripts.Add(new ScriptEntry { guid = "g1", originalPath = "Assets/_Scripts/Alpha/Sub/Gun.cs", relativeDir = "Sub", applied = true });
            Assign(beta, "g1");
            Assert.That(beta.scripts.Single().relativeDir, Is.EqualTo("Sub"));
            Assert.That(PathRules.TargetPath(beta, beta.scripts.Single(), "Assets/_Scripts/Alpha/Sub/Gun.cs", false, cfg.scopeFolders),
                Is.EqualTo("Assets/_Scripts/Beta/Sub/Gun.cs"));

            // Back to a module whose folder holds originalPath: relative to that folder, as for a new entry.
            File("g2", "Assets/_Scripts/Beta/Gun2.cs");
            alpha.scripts.Add(new ScriptEntry { guid = "g2", originalPath = "Assets/_Scripts/Beta/X/Gun2.cs", relativeDir = "Beta/X", applied = true });
            Assign(beta, "g2");
            Assert.That(beta.scripts.Single(x => x.guid == "g2").relativeDir, Is.EqualTo("X"));
        }

        [Test]
        public void Assign_ToSameModule_IsNoOp()
        {
            File("g1", "Assets/_Scripts/Gun.cs");
            Assign(alpha, "g1");
            Assert.That(Assign(alpha, "g1"), Is.EqualTo(0));
            Assert.That(alpha.scripts.Count, Is.EqualTo(1));
        }

        [Test]
        public void Unassign_Applied_GoesToPendingRemovals()
        {
            var e = new ScriptEntry { guid = "g1", originalPath = "Assets/_Scripts/Gun.cs", originalNamespace = "Old", applied = true };
            alpha.scripts.Add(e);
            ModuleEdits.Unassign(cfg, new[] { "g1" });
            Assert.That(alpha.scripts, Is.Empty);
            Assert.That(cfg.pendingRemovals.Single(), Is.SameAs(e));
        }

        [Test]
        public void Unassign_Draft_IsRemoved()
        {
            alpha.scripts.Add(new ScriptEntry { guid = "g1", originalPath = "Assets/_Scripts/Gun.cs" });
            ModuleEdits.Unassign(cfg, new[] { "g1" });
            Assert.That(alpha.scripts, Is.Empty);
            Assert.That(cfg.pendingRemovals, Is.Empty);
        }

        [Test]
        public void Unassign_DraftAwayFromOriginalPlace_GoesToPendingRemovals()
        {
            // Applied in Alpha, moved to Beta in the UI (draft there), then unassigned before Apply: still needs restoring.
            File("g1", "Assets/_Scripts/Alpha/Gun.cs");
            beta.scripts.Add(new ScriptEntry { guid = "g1", originalPath = "Assets/_Scripts/Gun.cs" });
            ModuleEdits.Unassign(cfg, new[] { "g1" }, scan);
            Assert.That(cfg.pendingRemovals.Select(x => x.guid), Is.EqualTo(new[] { "g1" }));
        }

        [Test]
        public void Unassign_DraftWithRewrittenNamespace_InOriginalFolder_GoesToPendingRemovals()
        {
            // Already inside Alpha's folder, so Apply only rewrote its namespace; then moved to Beta and unassigned.
            File("g1", "Assets/_Scripts/Alpha/Gun.cs", "namespace Game.Alpha { class Gun {} }");
            alpha.scripts.Add(new ScriptEntry
                { guid = "g1", originalPath = "Assets/_Scripts/Alpha/Gun.cs", originalNamespace = "Old", relativeDir = "", applied = true });
            Assign(beta, "g1");
            Assert.That(beta.scripts.Single().applied, Is.False);

            ModuleEdits.Unassign(cfg, new[] { "g1" }, scan, p => sources[p]);
            var e = cfg.pendingRemovals.Single();
            Assert.That((e.guid, e.originalPath, e.originalNamespace), Is.EqualTo(("g1", "Assets/_Scripts/Alpha/Gun.cs", "Old")));
        }

        [Test]
        public void Unassign_DraftUnchangedFromOrigin_IsDroppedEvenWithScan()
        {
            File("g1", "Assets/_Scripts/Gun.cs", "namespace Old { class Gun {} }");
            Assign(alpha, "g1");
            ModuleEdits.Unassign(cfg, new[] { "g1" }, scan, p => sources[p]);
            Assert.That(cfg.pendingRemovals, Is.Empty);
        }

        [Test]
        public void DeleteModule_AppliedToPending_DraftsDropped_AsmdefsRetired()
        {
            alpha.asmdefGuid = "a1";
            alpha.editorAsmdefGuid = "";
            alpha.scripts.Add(new ScriptEntry { guid = "g1", originalPath = "Assets/_Scripts/Gun.cs", applied = true });
            alpha.scripts.Add(new ScriptEntry { guid = "g2", originalPath = "Assets/_Scripts/Draft.cs" });

            ModuleEdits.DeleteModule(cfg, alpha);
            Assert.That(cfg.modules.Select(m => m.name), Is.EqualTo(new[] { "Beta" }));
            Assert.That(cfg.pendingRemovals.Select(x => x.guid), Is.EqualTo(new[] { "g1" }));
            Assert.That(cfg.retiredAsmdefGuids, Is.EqualTo(new[] { "a1" }));
        }

        [Test]
        public void Reassign_FromPendingRemovals_ReusesHistory()
        {
            File("g1", "Assets/_Scripts/Alpha/Gun.cs");
            cfg.pendingRemovals.Add(new ScriptEntry
                { guid = "g1", originalPath = "Assets/_Scripts/Weapons/Gun.cs", originalNamespace = "Old", relativeDir = "Weapons", applied = true });

            Assert.That(Assign(beta, "g1"), Is.EqualTo(1));
            Assert.That(cfg.pendingRemovals, Is.Empty);
            var e = beta.scripts.Single();
            Assert.That((e.originalPath, e.originalNamespace, e.relativeDir), Is.EqualTo(("Assets/_Scripts/Weapons/Gun.cs", "Old", "Weapons")));
        }

        [Test]
        public void RenameModule_MovesFolder_RejectsDuplicateOrEmpty()
        {
            Assert.That(ModuleEdits.RenameModule(cfg, alpha, "Combat"), Is.True);
            Assert.That((alpha.name, alpha.folder), Is.EqualTo(("Combat", "Assets/_Scripts/Combat")));
            Assert.That(ModuleEdits.RenameModule(cfg, alpha, "Beta"), Is.False);
            Assert.That(ModuleEdits.RenameModule(cfg, alpha, " "), Is.False);
            Assert.That(alpha.name, Is.EqualTo("Combat"));
        }

        [Test]
        public void AddModule_PicksUniqueName()
        {
            var a = ModuleEdits.AddModule(cfg);
            var b = ModuleEdits.AddModule(cfg);
            Assert.That((a.name, b.name), Is.EqualTo(("NewModule", "NewModule2")));
        }
    }
}
