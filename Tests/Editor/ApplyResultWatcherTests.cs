using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEngine;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class ApplyResultWatcherTests
    {
        const string Yaml =
            "%YAML 1.1\n" +
            "--- !u!114 &1\n" +
            "MonoBehaviour:\n" +
            "  references:\n" +
            "    version: 2\n" +
            "    RefIds:\n" +
            "    - rid: 100\n" +
            "      type: {class: Payload, ns: Game.Old, asm: Assembly-CSharp}\n" +
            "    - rid: 101\n" +
            "      type: {class: Loose, ns: , asm: Assembly-CSharp}\n";

        [Test]
        public void ContainsManagedRefType_MatchesOldLocation()
        {
            Assert.IsTrue(ApplyResultWatcher.ContainsManagedRefType(Yaml, "Game.Old", "Assembly-CSharp", "Payload"));
        }

        [Test]
        public void ContainsManagedRefType_GlobalNamespace()
        {
            Assert.IsTrue(ApplyResultWatcher.ContainsManagedRefType(Yaml, "", "Assembly-CSharp", "Loose"));
            Assert.IsFalse(ApplyResultWatcher.ContainsManagedRefType(Yaml, "", "Assembly-CSharp", "Payload"));
        }

        [Test]
        public void ContainsManagedRefType_DifferentClassSameNamespace()
        {
            Assert.IsFalse(ApplyResultWatcher.ContainsManagedRefType(Yaml, "Game.Old", "Assembly-CSharp", "Pay"));
            Assert.IsFalse(ApplyResultWatcher.ContainsManagedRefType(Yaml, "Game.Old", "Assembly-CSharp", "Other"));
        }

        [Test]
        public void ContainsManagedRefType_DifferentAssembly()
        {
            Assert.IsFalse(ApplyResultWatcher.ContainsManagedRefType(Yaml, "Game.Old", "Game.Combat", "Payload"));
        }

        [Test]
        public void ContainsManagedRefType_ToleratesSpacing()
        {
            Assert.IsTrue(ApplyResultWatcher.ContainsManagedRefType("type: { class:Payload ,ns:  Game.Old,asm: Assembly-CSharp }", "Game.Old", "Assembly-CSharp", "Payload"));
        }

        [Test]
        public void IsYamlHeader_TextVsBinary()
        {
            Assert.IsTrue(ApplyResultWatcher.IsYamlHeader(Encoding.ASCII.GetBytes("%YAML 1.1\n%TAG !u!")));
            Assert.IsTrue(ApplyResultWatcher.IsYamlHeader(Encoding.ASCII.GetBytes("%YAML")));
            Assert.IsFalse(ApplyResultWatcher.IsYamlHeader(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x16, 0x00, 0x00, 0x00 })); // binary asset
            Assert.IsFalse(ApplyResultWatcher.IsYamlHeader(Encoding.ASCII.GetBytes("%YAM")));
            Assert.IsFalse(ApplyResultWatcher.IsYamlHeader(Encoding.ASCII.GetBytes("using UnityEngine;")));
            Assert.IsFalse(ApplyResultWatcher.IsYamlHeader(null));
        }

        [Test]
        public void ParseKeys_SplitsLinesAndFields()
        {
            var keys = ApplyResultWatcher.ParseKeys("Game.Old|Assembly-CSharp|Payload\n|Assembly-CSharp|Outer/Inner\n\nbad");
            Assert.AreEqual(2, keys.Count);
            Assert.AreEqual(("Game.Old", "Assembly-CSharp", "Payload"), keys[0]);
            Assert.AreEqual(("", "Assembly-CSharp", "Outer/Inner"), keys[1]);
            Assert.AreEqual(0, ApplyResultWatcher.ParseKeys("").Count);
            Assert.AreEqual(0, ApplyResultWatcher.ParseKeys(null).Count);
        }

        [Test]
        public void ApplyResult_JsonRoundTrip()
        {
            var r = new ApplyResult
            {
                editorCompileOk = true, playerCompileOk = false, buildTarget = "StandaloneWindows64",
                editorErrors = new List<string> { "e1" }, playerErrors = new List<string> { "p1", "p2" },
                reserialized = new List<string> { "Assets/A.prefab" }, warnings = new List<string> { "w" },
            };
            var back = JsonUtility.FromJson<ApplyResult>(JsonUtility.ToJson(r));
            Assert.IsTrue(back.editorCompileOk);
            Assert.IsFalse(back.playerCompileOk);
            Assert.AreEqual("StandaloneWindows64", back.buildTarget);
            CollectionAssert.AreEqual(r.editorErrors, back.editorErrors);
            CollectionAssert.AreEqual(r.playerErrors, back.playerErrors);
            CollectionAssert.AreEqual(r.reserialized, back.reserialized);
            CollectionAssert.AreEqual(r.warnings, back.warnings);
        }
    }
}
