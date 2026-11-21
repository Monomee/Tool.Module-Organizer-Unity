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
        const string WeaponSrc = "namespace Game.Combat\n{\n    class Weapon {}\n}\n";
        const string DamageSrc = "namespace Game.Combat.Sub\n{\n    class Damage {}\n}\n";
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
            combat.scripts.Add(new ScriptEntry { guid = "s1", originalPath = "Assets/_Scripts/Weapon.cs", originalNamespace = "Legacy", applied = true });
            disk = new DiskSnapshot();
            Asmdef("a1", F + "/Game.Combat.asmdef", "Game.Combat");
            disk.scriptPathByGuid["s1"] = F + "/Weapon.cs";
            disk.sourceByGuid["s1"] = WeaponSrc; // Capture reads every module-folder script, assigned or not
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
            Assert.That((e.originalPath, e.originalNamespace, e.relativeDir, e.applied), Is.EqualTo((F + "/Sub/Damage.cs", "Old.Stuff", "Sub", true)));
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
            Asmdef("a1", "Assets/_Scripts/Fight/Game.Fight.asmdef", "Game.Fight").rootNamespace = "Other.Root";
            disk.scriptPathByGuid["s1"] = "Assets/_Scripts/Fight/Weapon.cs";
            // Accepted from disk earlier: recorded at its spot in the module folder.
            combat.scripts.Add(new ScriptEntry { guid = "s2", originalPath = F + "/Sub/Damage.cs", originalNamespace = "", applied = true });
            disk.scriptPathByGuid["s2"] = "Assets/_Scripts/Fight/Sub/Damage.cs";
            Assert.That(Diff().Select(i => i.kind),
                Is.EquivalentTo(new[] { DriftKind.AsmdefMovedOrRenamed, DriftKind.ScriptMovedOut, DriftKind.ScriptMovedOut }));

            AcceptAll();
            Assert.That((combat.folder, combat.namespaceName), Is.EqualTo(("Assets/_Scripts/Fight", "Game.Fight")), "namespace from the name, not rootNamespace");
            Assert.That(combat.scripts.Select(e => e.guid), Is.EqualTo(new[] { "s1", "s2" }), "the scripts moved with the asmdef: still in the module");
            Assert.That(combat.scripts[1].originalPath, Is.EqualTo("Assets/_Scripts/Fight/Sub/Damage.cs"));
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
        public void EditorManualReference_GoesToEditorExtraReferences_RuntimeUnchanged()
        {
            combat.editorAsmdefGuid = "e1";
            Asmdef("e1", F + "/Editor/Game.Combat.Editor.asmdef", "Game.Combat.Editor", "Unity.EditorOnly", "GUID:a1");
            var d = Diff();
            Assert.That(d.Select(i => (i.kind, i.guid)), Is.EqualTo(new[] { (DriftKind.ReferencesDiffer, "e1") }));

            ModuleSync.AcceptDisk(cfg, disk, d);
            Assert.That(combat.editorExtraReferences, Is.EqualTo(new[] { "Unity.EditorOnly" }));
            Assert.That(combat.extraReferences, Is.Empty);
            Assert.That(ApplyPlanner.ComputeReferences(combat, false, report, cfg), Is.Empty);
            Assert.That(Diff(), Is.Empty);
        }

        [Test]
        public void BareGuidOnDisk_MatchesComputedGuidReference()
        {
            report.asmdefRefs["Combat"] = new HashSet<string> { Ext };
            Asmdef("a1", F + "/Game.Combat.asmdef", "Game.Combat", Ext);
            Assert.That(Diff(), Is.Empty);
        }

        [Test]
        public void ScriptMovedIntoAnotherModule_AcceptDiskReassigns_WhateverTheModuleOrder()
        {
            var bravo = cfg.AddModule("Bravo");
            cfg.modules.Remove(bravo);
            cfg.modules.Insert(0, bravo);
            bravo.asmdefGuid = "b1";
            Asmdef("b1", bravo.folder + "/Game.Bravo.asmdef", "Game.Bravo");
            disk.scriptPathByGuid["s1"] = bravo.folder + "/Sub/Weapon.cs";
            Assert.That(Diff().Select(i => (i.kind, i.moduleName)),
                Is.EqualTo(new[] { (DriftKind.ScriptAddedInModuleFolder, "Bravo"), (DriftKind.ScriptMovedOut, "Combat") }));

            AcceptAll();
            Assert.That(combat.scripts, Is.Empty);
            var e = bravo.scripts.Single();
            Assert.That((e.guid, e.originalPath, e.originalNamespace, e.relativeDir, e.applied),
                Is.EqualTo(("s1", "Assets/_Scripts/Weapon.cs", "Legacy", "Sub", true)), "same file: history kept, only its place in Bravo is new");
            Assert.That(Diff(), Is.Empty);
        }

        [Test]
        public void AcceptedFileInModuleSubfolder_NextApplyLeavesItAlone()
        {
            AcceptNewDamage();
            var plan = PlanFromDisk();
            Assert.That(plan.moves.Where(mv => mv.guid == "s2"), Is.Empty);
            Assert.That(plan.edits.Where(e => e.guid == "s2" && e.setNamespace != null), Is.Empty);
        }

        [Test]
        public void ModuleFolderRenamedInConfig_AcceptedFileKeepsItsSubfolder()
        {
            AcceptNewDamage();
            combat.folder = "Assets/_Scripts/Fight";
            combat.namespaceName = "Game.Fight";
            var plan = PlanFromDisk();
            Assert.That(plan.moves.Single(mv => mv.guid == "s2").to, Is.EqualTo("Assets/_Scripts/Fight/Sub/Damage.cs"));
            Assert.That(plan.edits.Single(e => e.guid == "s2").setNamespace, Is.EqualTo("Game.Fight.Sub"));
        }

        [Test]
        public void FileRenamedInsideModule_NextApplyKeepsNewName()
        {
            AcceptNewDamage();
            disk.scriptPathByGuid["s2"] = F + "/Sub/Blow.cs";
            disk.scriptPathByGuid["s1"] = F + "/Sword.cs";
            Assert.That(Diff(), Is.Empty);
            Assert.That(PlanFromDisk().moves.Where(mv => mv.guid == "s1" || mv.guid == "s2"), Is.Empty);
        }

        [Test]
        public void MovedAndRenamedIntoAnotherModule_TargetKeepsNewName()
        {
            var bravo = cfg.AddModule("Bravo");
            bravo.asmdefGuid = "b1";
            Asmdef("b1", bravo.folder + "/Game.Bravo.asmdef", "Game.Bravo");
            disk.scriptPathByGuid["s1"] = bravo.folder + "/Sword.cs";
            AcceptAll();
            Assert.That(bravo.scripts.Single().originalPath, Is.EqualTo("Assets/_Scripts/Weapon.cs"), "history kept for restore");
            Assert.That(PlanFromDisk().moves.Where(mv => mv.guid == "s1"), Is.Empty, "stays at Bravo/Sword.cs");
        }

        [Test]
        public void UnassignedRenamedFile_RestoresToOriginalDirectoryUnderCurrentName()
        {
            disk.scriptPathByGuid["s1"] = F + "/Sword.cs";
            var e = combat.scripts.Single();
            combat.scripts.Remove(e);
            cfg.pendingRemovals.Add(e);
            Assert.That(PlanFromDisk().moves.Single(mv => mv.guid == "s1").to, Is.EqualTo("Assets/_Scripts/Sword.cs"));
        }

        void AcceptNewDamage()
        {
            disk.scriptPathByGuid["s2"] = F + "/Sub/Damage.cs";
            disk.sourceByGuid["s2"] = DamageSrc;
            ModuleSync.AcceptDisk(cfg, disk, Diff());
        }

        // Plan for Weapon.cs (s1) and Damage.cs (s2), both compiled in Combat's asmdef at their snapshot paths.
        ApplyPlan PlanFromDisk()
        {
            var scan = new ScanResult();
            var state = new DiskState();
            void File(string guid, string type)
            {
                var path = disk.scriptPathByGuid[guid];
                scan.files[guid] = new ScriptFile { guid = guid, path = path, kind = AssemblyKind.AsmDef, asmdefGuid = "a1", declaredTypes = { type } };
                scan.types[type] = new TypeRecord { fullName = type, ns = type.Substring(0, type.LastIndexOf('.')), kind = AssemblyKind.AsmDef, assemblyName = "Game.Combat", asmdefGuid = "a1", filePaths = { path } };
                state.pathByGuid[guid] = path;
                state.sourceByGuid[guid] = disk.sourceByGuid[guid];
            }
            File("s1", "Game.Combat.Weapon");
            if (disk.scriptPathByGuid.ContainsKey("s2")) File("s2", "Game.Combat.Sub.Damage");
            state.pathByGuid["a1"] = F + "/Game.Combat.asmdef";
            var validation = ModuleValidator.Validate(cfg, scan, p => state.sourceByGuid[scan.FileByPath(p).guid]);
            return ApplyPlanner.Build(cfg, scan, validation, state);
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

        // 0: Added row alone; 1: Added, then MovedOut; 2: MovedOut, then Added. Each row taken with its own AcceptDisk call.
        [Test]
        public void ScriptMovedIntoAnotherModule_RowsTakenOneAtATime_KeepHistory([Values(0, 1, 2)] int order)
        {
            var bravo = cfg.AddModule("Bravo");
            bravo.asmdefGuid = "b1";
            Asmdef("b1", bravo.folder + "/Game.Bravo.asmdef", "Game.Bravo");
            disk.scriptPathByGuid["s1"] = bravo.folder + "/Sub/Weapon.cs";
            var rows = order == 0 ? new[] { DriftKind.ScriptAddedInModuleFolder }
                : order == 1 ? new[] { DriftKind.ScriptAddedInModuleFolder, DriftKind.ScriptMovedOut }
                : new[] { DriftKind.ScriptMovedOut, DriftKind.ScriptAddedInModuleFolder };
            foreach (var k in rows) Assert.That(ModuleSync.AcceptDisk(cfg, disk, Diff().Where(i => i.kind == k).ToList()), Is.Empty);

            Assert.That(combat.scripts, Is.Empty);
            var e = bravo.scripts.Single();
            Assert.That((e.guid, e.originalPath, e.originalNamespace, e.relativeDir, e.applied),
                Is.EqualTo(("s1", "Assets/_Scripts/Weapon.cs", "Legacy", "Sub", true)));
            Assert.That(Diff(), Is.Empty);
        }

        [Test]
        public void AsmdefMovedToTheModulesRootItself_IsAccepted()
        {
            Asmdef("a1", "Assets/_Scripts/Game.Combat.asmdef", "Game.Combat");
            disk.scriptPathByGuid["s1"] = "Assets/_Scripts/Weapon.cs";
            Assert.That(ModuleSync.AcceptDisk(cfg, disk, Diff()), Is.Empty, "the root counts as inside, as for the planner");
            Assert.That(combat.folder, Is.EqualTo("Assets/_Scripts"));
        }

        [Test]
        public void AsmdefMovedOutsideScopeAndModulesRoot_IsRefused_AndStaysDrift()
        {
            Asmdef("a1", "Assets/Elsewhere/Game.Combat.asmdef", "Game.Combat");
            disk.scriptPathByGuid["s1"] = "Assets/Elsewhere/Weapon.cs";
            var refused = ModuleSync.AcceptDisk(cfg, disk, Diff());
            Assert.That(refused, Has.Count.EqualTo(1));
            StringAssert.Contains("Assets/Elsewhere/Game.Combat.asmdef", refused[0]);
            Assert.That(combat.folder, Is.EqualTo(F));
            Assert.That(combat.scripts.Select(x => x.guid), Is.EqualTo(new[] { "s1" }), "its script rows wait with it");
            Assert.That(Diff().Select(i => i.kind), Does.Contain(DriftKind.AsmdefMovedOrRenamed));
        }
    }
}
