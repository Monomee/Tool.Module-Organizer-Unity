using System.Collections.Generic;
using System.IO;
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
        public void Cancel_ClearsPending()
        {
            try
            {
                ApplyResultWatcher.BeginPending(new[] { "Game.Old|Assembly-CSharp|Payload" });
                Assert.IsTrue(ApplyResultWatcher.Pending);
            }
            finally
            {
                ApplyResultWatcher.Cancel();
            }
            Assert.IsFalse(ApplyResultWatcher.Pending);
        }

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
        public void StillOwed_KeepsKeysOfUnreadFiles_AndOfFilesThatFailedToReserialize()
        {
            var keys = new[] { "N|A|Payload", "N|A|Other", "N|A|Third" };
            var settled = new (bool, bool, ICollection<string>)[] { (true, false, new[] { "N|A|Payload" }), (true, false, new string[0]) };
            CollectionAssert.IsEmpty(ApplyResultWatcher.StillOwed(keys, settled), "read, and re-serialized or holding nothing");
            var failed = new (bool, bool, ICollection<string>)[] { (true, true, new[] { "N|A|Other" }) };
            CollectionAssert.AreEqual(new[] { "N|A|Other" }, ApplyResultWatcher.StillOwed(keys, failed), "only what the failed file might hold");
            var unread = new (bool, bool, ICollection<string>)[] { (true, false, new string[0]), (false, false, new string[0]) };
            CollectionAssert.AreEqual(keys, ApplyResultWatcher.StillOwed(keys, unread), "an unread file might hold any key");
        }

        [Test]
        public void ScanYaml_PrefiltersPerLine_MatchesWrappedBlock_StopsOnceAllMatched()
        {
            var keys = new List<(string, string, string)> { ("Game.Old", "Assembly-CSharp", "Payload"), ("", "Assembly-CSharp", "Loose"), ("X", "Y", "Absent") };
            var (candidates, matched) = ApplyResultWatcher.ScanYaml(new StringReader(Yaml), keys);
            Assert.IsTrue(matched);
            CollectionAssert.AreEqual(new[] { 0, 1 }, candidates);

            const string wrapped = "%YAML 1.1\n      type: {class: Payload,\n        ns: Game.Old,\n        asm: Assembly-CSharp}\n";
            Assert.IsTrue(ApplyResultWatcher.ScanYaml(new StringReader(wrapped), keys.GetRange(0, 1)).matched);
            var named = ApplyResultWatcher.ScanYaml(new StringReader("m_Name: Payload\n  type: {class: Payload, ns: Game.New, asm: Game.Combat}\n"), keys);
            Assert.IsFalse(named.matched, "the class name alone is not the old location");
            CollectionAssert.AreEqual(new[] { 0 }, named.candidates);

            // Stops reading once every key matched: the reader is left unfinished.
            var reader = new StringReader("type: {class: Payload, ns: Game.Old, asm: Assembly-CSharp}\nA\nB\nC\nD\n");
            Assert.IsTrue(ApplyResultWatcher.ScanYaml(reader, keys.GetRange(0, 1)).matched);
            Assert.AreEqual("C", reader.ReadLine());
        }

        [Test]
        public void OnlyMovesAsmdefsAndEdits_TriggerACompile()
        {
            Assert.IsFalse(new ApplyPlan { deleteFoldersIfEmpty = { "Assets/X" }, removeEntries = { "g" }, clearRetiredAsmdefGuids = { "a" } }.TriggersCompile);
            Assert.IsTrue(new ApplyPlan { edits = { new TextEditOp { guid = "g" } } }.TriggersCompile);
            Assert.IsTrue(new ApplyPlan { asmdefs = { new AsmdefOp { delete = true } } }.TriggersCompile);
            Assert.IsTrue(new ApplyPlan { moves = { new MoveOp() } }.TriggersCompile);
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
