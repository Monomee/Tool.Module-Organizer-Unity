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
    }
}
