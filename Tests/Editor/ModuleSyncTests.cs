using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class ModuleSyncTests
    {
        const string F = "Assets/_Scripts/Combat";
        const string Ext = "0123456789abcdef0123456789abcdef";
        ModuleConfig cfg;
        DiskSnapshot disk;
        ValidationReport report;
        Module combat;

        // Combat applied: asmdef a1 at its folder, Weapon.cs (s1) inside it.
        [SetUp]
        public void SetUp()
        {
            cfg = ScriptableObject.CreateInstance<ModuleConfig>();
            cfg.rootNamespace = "Game";
            cfg.scopeFolders = new List<string> { "Assets/_Scripts" };
            combat = cfg.AddModule("Combat");
            combat.asmdefGuid = "a1";
            combat.scripts.Add(new ScriptEntry { guid = "s1", originalPath = "Assets/_Scripts/Weapon.cs", originalNamespace = "", applied = true });
            disk = new DiskSnapshot();
            Asmdef("a1", F + "/Game.Combat.asmdef", "Game.Combat");
            disk.scriptPathByGuid["s1"] = F + "/Weapon.cs";
            report = new ValidationReport();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(cfg);

        AsmdefSnapshot Asmdef(string guid, string path, string name, params string[] refs) =>
            disk.asmdefsByGuid[guid] = new AsmdefSnapshot { guid = guid, path = path, name = name, rootNamespace = name, references = refs.ToList() };

        List<DriftItem> Diff() => ModuleSync.Diff(cfg, disk, report);

        void AcceptAll() => ModuleSync.AcceptDisk(cfg, disk, Diff());

        [Test]
        public void NoDrift_WhenDiskMatches() => Assert.That(Diff(), Is.Empty);

        [Test]
        public void NewFileInModuleFolder_IsAdded_AndAcceptDiskAddsEntry()
        {
            disk.scriptPathByGuid["s2"] = F + "/Sub/Damage.cs";
            disk.sourceByGuid["s2"] = "namespace Old.Stuff\n{\n    class Damage {}\n}\n";
            // A subfolder with someone else's asmdef is not the module's.
            Asmdef("x1", F + "/Plugin/Plugin.asmdef", "Plugin");
            disk.scriptPathByGuid["s3"] = F + "/Plugin/Foreign.cs";

            var d = Diff();
            Assert.That(d.Select(i => (i.kind, i.moduleName, i.guid)),
                Is.EqualTo(new[] { (DriftKind.ScriptAddedInModuleFolder, "Combat", "s2") }));

            ModuleSync.AcceptDisk(cfg, disk, d);
            var e = combat.scripts.Single(x => x.guid == "s2");
            Assert.That((e.originalPath, e.originalNamespace, e.applied), Is.EqualTo((F + "/Sub/Damage.cs", "Old.Stuff", true)));
            Assert.That(Diff(), Is.Empty);
        }

        [Test]
        public void DeletedScript_IsReported_AndAcceptDiskRemovesEntry()
        {
            disk.scriptPathByGuid.Remove("s1");
            Assert.That(Diff().Select(i => (i.kind, i.guid)), Is.EqualTo(new[] { (DriftKind.ScriptDeleted, "s1") }));
            AcceptAll();
            Assert.That(combat.scripts, Is.Empty);
            Assert.That(Diff(), Is.Empty);
        }

        [Test]
        public void AsmdefRenamedOnDisk_AcceptDiskUpdatesModule()
        {
            Asmdef("a1", "Assets/_Scripts/Fight/Game.Fight.asmdef", "Game.Fight");
            disk.scriptPathByGuid["s1"] = "Assets/_Scripts/Fight/Weapon.cs";
            Assert.That(Diff().Select(i => i.kind), Is.EquivalentTo(new[] { DriftKind.AsmdefMovedOrRenamed, DriftKind.ScriptMovedOut }));

            AcceptAll();
            Assert.That((combat.folder, combat.namespaceName), Is.EqualTo(("Assets/_Scripts/Fight", "Game.Fight")));
            Assert.That(combat.scripts.Select(e => e.guid), Is.EqualTo(new[] { "s1" }), "the script moved with the asmdef: still in the module");
            Assert.That(Diff(), Is.Empty);
        }

        [Test]
        public void EditorAsmdefMoved_FolderDropsEditor_NamespaceDropsSuffix()
        {
            combat.editorAsmdefGuid = "e1";
            Asmdef("a1", "Assets/_Scripts/Fight/Game.Fight.asmdef", "Game.Fight");
            Asmdef("e1", "Assets/_Scripts/Fight/Editor/Game.Fight.Editor.asmdef", "Game.Fight.Editor", "GUID:a1");
            disk.scriptPathByGuid["s1"] = "Assets/_Scripts/Fight/Weapon.cs";
            var editorItem = Diff().Where(i => i.kind == DriftKind.AsmdefMovedOrRenamed && i.guid == "e1").ToList();
            Assert.That(editorItem, Has.Count.EqualTo(1));

            ModuleSync.AcceptDisk(cfg, disk, editorItem);
            Assert.That((combat.folder, combat.namespaceName), Is.EqualTo(("Assets/_Scripts/Fight", "Game.Fight")));
            Assert.That(Diff(), Is.Empty);
        }

        [Test]
        public void ManualReference_AcceptDiskAddsExtraReference()
        {
            report.asmdefRefs["Combat"] = new HashSet<string> { Ext };
            Asmdef("a1", F + "/Game.Combat.asmdef", "Game.Combat", "Unity.Manual", "GUID:" + Ext);
            var d = Diff();
            Assert.That(d.Select(i => (i.kind, i.guid)), Is.EqualTo(new[] { (DriftKind.ReferencesDiffer, "a1") }));

            ModuleSync.AcceptDisk(cfg, disk, d);
            Assert.That(combat.extraReferences, Is.EqualTo(new[] { "Unity.Manual" }));
            Assert.That(Diff(), Is.Empty, "compared as sets: disk order differs from the sorted computed list");
        }

        [Test]
        public void ReferenceMissingOnDisk_IsDrift_AcceptDiskKeepsConfig()
        {
            report.asmdefRefs["Combat"] = new HashSet<string> { Ext };
            var d = Diff();
            Assert.That(d.Select(i => i.kind), Is.EqualTo(new[] { DriftKind.ReferencesDiffer }));
            ModuleSync.AcceptDisk(cfg, disk, d);
            Assert.That(combat.extraReferences, Is.Empty);
        }

        [Test]
        public void MissingAsmdef_AcceptDiskTurnsModuleBackIntoDraft()
        {
            disk.asmdefsByGuid.Remove("a1");
            Assert.That(Diff().Select(i => (i.kind, i.guid)), Is.EqualTo(new[] { (DriftKind.AsmdefMissing, "a1") }));
            AcceptAll();
            Assert.That(combat.asmdefGuid, Is.Null.Or.Empty);
            Assert.That(combat.scripts.Single().applied, Is.False);
            Assert.That(Diff(), Is.Empty);
        }

        [Test]
        public void ScriptMovedOut_AcceptDiskUnassignsWithoutRestore()
        {
            disk.scriptPathByGuid["s1"] = "Assets/Other/Weapon.cs";
            Assert.That(Diff().Select(i => (i.kind, i.guid)), Is.EqualTo(new[] { (DriftKind.ScriptMovedOut, "s1") }));
            AcceptAll();
            Assert.That(combat.scripts, Is.Empty);
            Assert.That(cfg.pendingRemovals, Is.Empty);
        }

        [Test]
        public void DraftEntries_AreNotDrift()
        {
            // Unapplied entry of an applied module: not on disk, or elsewhere.
            combat.scripts.Add(new ScriptEntry { guid = "d1", originalPath = "Assets/_Scripts/Draft.cs" });
            disk.scriptPathByGuid["d1"] = "Assets/_Scripts/Draft.cs";
            combat.scripts.Add(new ScriptEntry { guid = "d2", originalPath = "Assets/_Scripts/Gone.cs" });
            // Pending removal whose file is gone, or still in the module folder.
            cfg.pendingRemovals.Add(new ScriptEntry { guid = "p1", originalPath = "Assets/_Scripts/P1.cs", applied = true });
            cfg.pendingRemovals.Add(new ScriptEntry { guid = "p2", originalPath = "Assets/_Scripts/P2.cs", applied = true });
            disk.scriptPathByGuid["p2"] = F + "/P2.cs";
            // Never-applied module: no asmdef, applied-looking entry missing, new file in its folder.
            var ui = cfg.AddModule("UI");
            ui.scripts.Add(new ScriptEntry { guid = "u1", originalPath = "Assets/_Scripts/HUD.cs", applied = true });
            disk.scriptPathByGuid["u2"] = ui.folder + "/Menu.cs";

            Assert.That(Diff(), Is.Empty);
        }
    }
}
