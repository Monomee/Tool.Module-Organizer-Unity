using System.Collections.Generic;
using NUnit.Framework;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class AsmdefJsonTests
    {
        [Test]
        public void Write_EditorAsmdef()
        {
            var json = AsmdefJson.Write(new AsmdefOp { name = "Game.Combat.Editor", rootNamespace = "Game.Combat.Editor", editorOnly = true, references = new List<string> { "GUID:abc" } });
            StringAssert.Contains("\"includePlatforms\": [\n        \"Editor\"\n    ]", json);
            StringAssert.Contains("\"autoReferenced\": true", json);
            StringAssert.Contains("\"overrideReferences\": false", json);
        }

        [Test]
        public void Write_RuntimeAsmdef_MatchesUnityLayout()
        {
            var json = AsmdefJson.Write(new AsmdefOp { name = "Game.Combat", rootNamespace = "Game.Combat", references = new List<string> { "GUID:a", "GUID:b" } });
            Assert.AreEqual(
                "{\n" +
                "    \"name\": \"Game.Combat\",\n" +
                "    \"rootNamespace\": \"Game.Combat\",\n" +
                "    \"references\": [\n" +
                "        \"GUID:a\",\n" +
                "        \"GUID:b\"\n" +
                "    ],\n" +
                "    \"includePlatforms\": [],\n" +
                "    \"excludePlatforms\": [],\n" +
                "    \"allowUnsafeCode\": false,\n" +
                "    \"overrideReferences\": false,\n" +
                "    \"precompiledReferences\": [],\n" +
                "    \"autoReferenced\": true,\n" +
                "    \"defineConstraints\": [],\n" +
                "    \"versionDefines\": [],\n" +
                "    \"noEngineReferences\": false\n" +
                "}", json);
        }
    }
}
