using System.Linq;
using NUnit.Framework;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class DependencyScannerTests
    {
        static ScanResult cached;
        static ScanResult S => cached ?? (cached = DependencyScanner.Scan());
        static ScriptFile F(string name) => S.FileByPath("Assets/_Fixtures/" + name);

        [Test]
        public void FxA_ReferencesFxB_AndOutside()
        {
            CollectionAssert.IsSupersetOf(F("Scope/FxA.cs").referencedTypes, new[] { "FxB", "FxOut" });
            CollectionAssert.DoesNotContain(F("Scope/FxA.cs").referencedTypes, "FxA");
        }

        [Test]
        public void EnumWithoutMethods_MapsToFile()
        {
            CollectionAssert.Contains(S.types["FxColor"].filePaths, "Assets/_Fixtures/Scope/FxB.cs");
        }

        [Test]
        public void Partial_HasTwoFiles()
        {
            Assert.AreEqual(2, S.types["FxPartial"].filePaths.Count);
        }

        [Test]
        public void EditorKind_OnlyByAssembly()
        {
            Assert.IsTrue(F("Scope/Editor/FxAEditor.cs").IsEditor);
            Assert.IsFalse(F("Scope/FxGizmo.cs").IsEditor);
        }

        [Test]
        public void TypeofInAttribute_IsReferenced_AndKindsAreSet()
        {
            CollectionAssert.Contains(F("Scope/Editor/FxAEditor.cs").referencedTypes, "FxA");
            Assert.AreEqual(AssemblyKind.Runtime, S.types["FxA"].kind);
            Assert.AreEqual(AssemblyKind.Engine, S.types["UnityEngine.MonoBehaviour"].kind);
            Assert.AreEqual(AssemblyKind.AsmDef, S.types["Monome.UtilityPack.ModuleOrganizer.DeclarationFinder"].kind);
            Assert.IsFalse(string.IsNullOrEmpty(S.types["Monome.UtilityPack.ModuleOrganizer.DeclarationFinder"].asmdefGuid));
            Assert.IsFalse(string.IsNullOrEmpty(F("Scope/FxA.cs").guid));
        }

        [Test]
        public void InactiveDeclaration_DoesNotAddFile()
        {
            CollectionAssert.AreEqual(new[] { "Assets/_Fixtures/Scope/FxShared.cs" }, S.types["FxShared"].filePaths);
            CollectionAssert.DoesNotContain(F("Scope/FxShadow.cs").declaredTypes, "FxShared");
        }

        [Test]
        public void FieldOnlyPartialPart_IsMapped()
        {
            CollectionAssert.AreEquivalent(
                new[] { "Assets/_Fixtures/Scope/FxPartialFields.Methods.cs", "Assets/_Fixtures/Scope/FxPartialFields.Fields.cs" },
                S.types["FxPartialFields"].filePaths);
        }

        [Test]
        public void InactiveMethodlessAndPartialDeclarations_AreNotMapped()
        {
            CollectionAssert.AreEqual(new[] { "Assets/_Fixtures/Scope/FxB.cs" }, S.types["FxColor"].filePaths);
            CollectionAssert.AreEquivalent(
                new[] { "Assets/_Fixtures/Scope/FxPartial.Part1.cs", "Assets/_Fixtures/Scope/FxPartial.Part2.cs" },
                S.types["FxPartial"].filePaths);
            CollectionAssert.AreEqual(new[] { "Assets/_Fixtures/Scope/FxInactiveDecls.cs" }, S.types["FxActiveBranch"].filePaths);
        }

        [Test]
        public void LambdaReference_FoldsIntoOuterTypeFile()
        {
            CollectionAssert.Contains(F("Scope/FxA.cs").referencedTypes, "FxLambdaOnly");
        }

        [Test]
        public void SerializableNonUnityObject_Flag()
        {
            Assert.IsTrue(S.types["FxData"].serializableNonUnityObject);
            Assert.IsFalse(S.types["FxA"].serializableNonUnityObject);
            Assert.IsFalse(S.types["FxB"].serializableNonUnityObject);
        }

        [Test]
        public void InlinedConstAndEnumValue_AreReferencedFromSource()
        {
            var refs = F("Scope/FxConstUser.cs").referencedTypes;
            CollectionAssert.Contains(refs, "FxConstSource");
            CollectionAssert.Contains(refs, "FxMode");
            CollectionAssert.DoesNotContain(refs, "FxConstUser");
        }

        [Test]
        public void PseudoAttribute_IsReferenced()
        {
            CollectionAssert.Contains(F("Scope/FxConstUser.cs").referencedTypes, "System.Runtime.CompilerServices.MethodImplAttribute");
            CollectionAssert.Contains(F("Scope/FxData.cs").referencedTypes, "System.SerializableAttribute");
        }

        [Test]
        public void InternalTypeAndMember_AreInternalRefs()
        {
            CollectionAssert.AreEquivalent(new[] { "FxHidden", "FxInternalMember" }, F("Scope/FxConstUser.cs").internalRefs);
            CollectionAssert.IsEmpty(F("Scope/FxA.cs").internalRefs);
        }

        [Test]
        public void TypeInTwoAssetsAssemblies_IsDuplicate_AndAssembliesAreListed()
        {
            CollectionAssert.AreEquivalent(new[] { "Assets/_Fixtures/Twins/A/FxTwin.cs", "Assets/_Fixtures/Twins/B/FxTwin.cs" }, S.duplicateTypes["FxTwin"]);
            Assert.AreEqual(S.types["FxTwin"].assemblyName, "Assembly-CSharp");
            Assert.AreEqual("", S.assemblies["Assembly-CSharp"]);
            Assert.IsFalse(string.IsNullOrEmpty(S.assemblies["Fx.Twins"]));
            Assert.AreEqual("Assembly-CSharp", F("Scope/FxA.cs").assemblyName);
        }

        static string Meta(params (string first, int enabled)[] entries) =>
            "PluginImporter:\n  isExplicitlyReferenced: 0\n  platformData:\n" + string.Concat(entries.Select(e =>
                "  - first:\n      " + e.first + "\n    second:\n      enabled: " + e.enabled + "\n      settings:\n        CPU: AnyCPU\n"));

        [Test]
        public void IsEditorOnlyDll_FromPlatformDataOrEditorFolder()
        {
            var editorOnly = Meta((": Any", 0), ("Editor: Editor", 1), ("Standalone: Win64", 0));
            var anyPlatform = Meta((": Any", 1), ("Editor: Editor", 0));
            var standaloneAndEditor = Meta((": Any", 0), ("Editor: Editor", 1), ("Standalone: Win64", 1));
            Assert.IsTrue(DependencyScanner.IsEditorOnlyDll("Assets/Plugins/Vendor.dll", editorOnly));
            Assert.IsFalse(DependencyScanner.IsEditorOnlyDll("Assets/Plugins/Vendor.dll", anyPlatform));
            Assert.IsFalse(DependencyScanner.IsEditorOnlyDll("Assets/Plugins/Vendor.dll", standaloneAndEditor));
            Assert.IsTrue(DependencyScanner.IsEditorOnlyDll("Assets/Plugins/Editor/Vendor.dll", anyPlatform));
            Assert.IsFalse(DependencyScanner.IsEditorOnlyDll("Assets/Plugins/EditorTools/Vendor.dll", ""));
        }
    }
}
