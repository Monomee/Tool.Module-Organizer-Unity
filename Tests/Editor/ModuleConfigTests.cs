using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class ModuleConfigTests
    {
        [Test]
        public void IsUnder_RespectsFolderBoundary()
        {
            Assert.IsTrue(PathRules.IsUnder("Assets/_Scripts/A.cs", "Assets/_Scripts"));
            Assert.IsTrue(PathRules.IsUnder("Assets/_Scripts/Sub/A.cs", "Assets/_Scripts/"));
            Assert.IsFalse(PathRules.IsUnder("Assets/_Scripts2/A.cs", "Assets/_Scripts"));
        }

        [Test]
        public void AddModule_FillsDefaults()
        {
            var c = ScriptableObject.CreateInstance<ModuleConfig>();
            c.scopeFolders = new List<string> { "Assets/_Scripts" };
            var m = c.AddModule("Combat");
            Assert.AreEqual("Game.Combat", m.namespaceName);
            Assert.AreEqual("Assets/_Scripts/Combat", m.folder);
            Assert.IsTrue(c.IsInScope("Assets/_Scripts/X/A.cs"));
            Assert.IsFalse(c.IsInScope("Assets/Other/A.cs"));
        }

        [Test]
        public void FindModuleOf_ReturnsNullWhenUnassigned()
        {
            var c = ScriptableObject.CreateInstance<ModuleConfig>();
            var m = c.AddModule("A");
            m.scripts.Add(new ScriptEntry { guid = "g1" });
            Assert.AreSame(m, c.FindModuleOf("g1"));
            Assert.IsNull(c.FindModuleOf("g2"));
        }

        [Test]
        public void TargetPathAndNamespace_KeepSubfolders_StripEditor()
        {
            var m = new Module { name = "Combat", namespaceName = "Game.Combat", folder = "Assets/_Scripts/Combat" };
            var scope = new[] { "Assets/_Scripts" };
            var e = new ScriptEntry { originalPath = "Assets/_Scripts/Weapons/Editor/GunEditor.cs" };
            Assert.AreEqual("Weapons", PathRules.ScopeRelativeDir(e.originalPath, scope));
            Assert.AreEqual("Assets/_Scripts/Combat/Editor/Weapons/GunEditor.cs", PathRules.TargetPath(m, e, true, scope));
            Assert.AreEqual("Assets/_Scripts/Combat/Weapons/GunEditor.cs", PathRules.TargetPath(m, e, false, scope));
            Assert.AreEqual("Game.Combat.Editor.Weapons", PathRules.TargetNamespace(m, "Weapons", true));
            Assert.AreEqual("Game.Combat", PathRules.TargetNamespace(m, "", false));
        }

        [Test]
        public void FileAlreadyInModuleFolder_KeepsItsPlace_NoDoubledFolder()
        {
            var m = new Module { name = "Combat", namespaceName = "Game.Combat", folder = "Assets/_Scripts/Combat" };
            var scope = new[] { "Assets/_Scripts" };
            var e = new ScriptEntry { originalPath = "Assets/_Scripts/Combat/Sub/Damage.cs" };
            Assert.AreEqual("Sub", PathRules.RelativeDir(m, e.originalPath, scope));
            Assert.AreEqual("Assets/_Scripts/Combat/Sub/Damage.cs", PathRules.TargetPath(m, e, false, scope));
            Assert.AreEqual("Game.Combat.Sub", PathRules.TargetNamespace(m, PathRules.RelativeDir(m, e.originalPath, scope), false));
            var ed = new ScriptEntry { originalPath = "Assets/_Scripts/Combat/Editor/Sub/DamageEditor.cs" };
            Assert.AreEqual("Assets/_Scripts/Combat/Editor/Sub/DamageEditor.cs", PathRules.TargetPath(m, ed, true, scope));
            Assert.AreEqual("Weapons", PathRules.RelativeDir(m, "Assets/_Scripts/Weapons/Gun.cs", scope), "outside the module: scope-relative");
        }

        [Test]
        public void NewEntry_FixesRelativeDir_SoConfigFolderRenameKeepsIt()
        {
            var m = new Module { name = "Combat", namespaceName = "Game.Combat", folder = "Assets/_Scripts/Combat" };
            var scope = new[] { "Assets/_Scripts" };
            var e = PathRules.NewEntry(m, "g", "Assets/_Scripts/Combat/Sub/X.cs", "Old", scope);
            Assert.AreEqual(("g", "Assets/_Scripts/Combat/Sub/X.cs", "Old", "Sub"), (e.guid, e.originalPath, e.originalNamespace, e.relativeDir));
            Assert.AreEqual("Weapons", PathRules.NewEntry(m, "h", "Assets/_Scripts/Weapons/Gun.cs", "", scope).relativeDir);

            m.folder = "Assets/_Scripts/Fight";
            m.namespaceName = "Game.Fight";
            Assert.AreEqual("Assets/_Scripts/Fight/Sub/X.cs", PathRules.TargetPath(m, e, false, scope));
            Assert.AreEqual("Game.Fight.Sub", PathRules.TargetNamespace(m, PathRules.EntryRelativeDir(m, e, scope), false));
        }
    }
}
