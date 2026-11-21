using System;
using NUnit.Framework;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class SourceRewriterTests
    {
        [Test] public void SetNamespace_ReplacesBlock() =>
          StringAssert.Contains("namespace Game.Combat\n{", SourceRewriter.SetNamespace("namespace Old\n{\n    class A {}\n}\n", "Game.Combat"));
        [Test] public void SetNamespace_WrapsGlobal_KeepsUsingsOutside() {
          var r = SourceRewriter.SetNamespace("using UnityEngine;\n\npublic class A {}\n", "Game.Combat");
          Assert.AreEqual("using UnityEngine;\n\nnamespace Game.Combat\n{\n    public class A {}\n}\n", r);
        }
        [Test] public void SetNamespace_Empty_Unwraps() =>
          Assert.AreEqual("using X;\n\npublic class A {}\n", SourceRewriter.SetNamespace("using X;\n\nnamespace N\n{\n    public class A {}\n}\n", ""));
        [Test] public void Parse_IgnoresCommentsAndStrings() {
          var i = SourceRewriter.Parse("// namespace Fake\nclass A { string s = \"namespace X\"; }");
          Assert.IsTrue(i.IsGlobal);
        }
        [Test] public void Parse_DetectsNestedAndMultiple() {
          Assert.IsFalse(SourceRewriter.Parse("namespace A { namespace B { } }").Rewritable);
          Assert.IsFalse(SourceRewriter.Parse("namespace A { } namespace B { }").Rewritable);
        }
        [Test] public void AddUsing_AfterLastTopUsing_Idempotent_IgnoresIfBlocks() {
          var src = "using A;\n#if UNITY_EDITOR\nusing UnityEditor;\n#endif\nclass X {}\n";
          var r = SourceRewriter.AddUsing(SourceRewriter.AddUsing(src, "Game.Combat"), "Game.Combat");
          Assert.AreEqual("using A;\n#if UNITY_EDITOR\nusing UnityEditor;\n#endif\nusing Game.Combat;\nclass X {}\n", r);
        }
        [Test] public void RemoveUsing_RemovesOnlyExact() =>
          Assert.AreEqual("using Game.CombatX;\nclass X {}\n", SourceRewriter.RemoveUsing("using Game.Combat;\nusing Game.CombatX;\nclass X {}\n", "Game.Combat"));
        [Test] public void PreservesCrlf() =>
          StringAssert.Contains("\r\n    class A", SourceRewriter.SetNamespace("class A {}\r\n", "N"));
        [Test] public void AddMovedFrom_OnTypeDeclaration() =>
          StringAssert.Contains("[UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"\", sourceAssembly: \"Assembly-CSharp\")]\n    [System.Serializable] public class P",
            SourceRewriter.AddMovedFrom("namespace N\n{\n    [System.Serializable] public class P {}\n}\n", "P", "", "Assembly-CSharp"));
        [Test] public void FindQualifiedUses_ReportsLine() =>
          CollectionAssert.AreEqual(new[]{2}, SourceRewriter.FindQualifiedUses("class X {\n  Old.Gun g;\n}", "Old", new[]{"Gun"}));

        // --- risky cases beyond the brief ---

        [Test]
        public void SetNamespace_RenameIgnoresNamespaceTextInCommentsAndStrings()
        {
            var src = "// namespace Old\nnamespace Old\n{\n    class A { string s = \"namespace Old\"; }\n}\n";
            Assert.AreEqual("// namespace Old\nnamespace New\n{\n    class A { string s = \"namespace Old\"; }\n}\n",
                SourceRewriter.SetNamespace(src, "New"));
        }

        [Test]
        public void SetNamespace_Wrap_KeepsHeaderCommentAndCrlfAndNoTrailingNewline()
        {
            Assert.AreEqual("// Copyright\r\n\r\nnamespace N\r\n{\r\n    class A\r\n    {\r\n\r\n    }\r\n}\r\n",
                SourceRewriter.SetNamespace("// Copyright\r\n\r\nclass A\r\n{\r\n\r\n}\r\n", "N"));
            Assert.AreEqual("namespace N\n{\n    class A {}\n}", SourceRewriter.SetNamespace("class A {}", "N"));
        }

        [Test]
        public void SetNamespace_Wrap_LeavesBlockCommentHeaderAndVerbatimStringsAlone()
        {
            var src = "/*\n\n * License\n */\nusing A;\nclass B\n{\n    string s = @\"line1\nline2\";\n}\n";
            Assert.AreEqual("/*\n\n * License\n */\nusing A;\nnamespace N\n{\n    class B\n    {\n        string s = @\"line1\nline2\";\n    }\n}\n",
                SourceRewriter.SetNamespace(src, "N"));
        }

        [Test]
        public void SetNamespace_Wrap_DoesNotOpenInsideIfBlock()
        {
            var r = SourceRewriter.SetNamespace("using A;\n#if UNITY_EDITOR\nclass B {}\n#endif\n", "N");
            Assert.AreEqual("using A;\nnamespace N\n{\n    #if UNITY_EDITOR\n    class B {}\n    #endif\n}\n", r);
        }

        [Test]
        public void SetNamespace_Unwrap_DedentsOnlyWherePresent_AndKeepsVerbatimStrings()
        {
            var src = "namespace N\n{\n\tclass A\n\t{\n  int x;\n        string s = @\"\n    keep\";\n\t}\n}\n";
            Assert.AreEqual("class A\n{\n  int x;\n    string s = @\"\n    keep\";\n}\n", SourceRewriter.SetNamespace(src, ""));
        }

        [Test]
        public void SetNamespace_NotRewritable_Throws()
        {
            Assert.Throws<InvalidOperationException>(() => SourceRewriter.SetNamespace("namespace A { } namespace B { }", "C"));
        }

        [Test]
        public void Parse_UnsafeShapesAreNotRewritable()
        {
            Assert.IsFalse(SourceRewriter.Parse("namespace A;\nclass B {}").Rewritable, "file-scoped");
            Assert.IsFalse(SourceRewriter.Parse("class G {}\nnamespace A { class B {} }").Rewritable, "code outside block");
            Assert.IsFalse(SourceRewriter.Parse("#if X\nnamespace A\n#else\nnamespace B\n#endif\n{ }").Rewritable, "#if branches");
            Assert.IsFalse(SourceRewriter.Parse("namespace A { class B {").Rewritable, "unbalanced");
            var ok = SourceRewriter.Parse("using X;\n[assembly: Foo]\nnamespace A.B { class C { void M() { } } }\n#if Y\n#endif\n");
            Assert.IsTrue(ok.Rewritable);
            CollectionAssert.AreEqual(new[] { "A.B" }, ok.namespaces);
        }

        [Test]
        public void AddUsing_LeavesUsingsInsideNamespaceAlone_AndHandlesNoTopUsings()
        {
            var src = "namespace N\n{\n    using Game.Combat;\n    class X {}\n}\n";
            Assert.AreEqual("using Game.Combat;\n\n" + src, SourceRewriter.AddUsing(src, "Game.Combat"));
            Assert.AreEqual("// Header\n\nusing G;\n\nclass X {}\n", SourceRewriter.AddUsing("// Header\n\nclass X {}\n", "G"));
            Assert.AreEqual("#define D\nusing G;\n\nclass X {}\n", SourceRewriter.AddUsing("#define D\nclass X {}\n", "G"));
        }

        [Test]
        public void RemoveUsing_KeepsStaticAliasAndNested()
        {
            var src = "using static Game.Combat;\nusing C = Game.Combat;\nusing Game.Combat; // gone\nnamespace N\n{\n    using Game.Combat;\n}\n";
            Assert.AreEqual("using static Game.Combat;\nusing C = Game.Combat;\nnamespace N\n{\n    using Game.Combat;\n}\n",
                SourceRewriter.RemoveUsing(src, "Game.Combat"));
            Assert.AreEqual("using A; \nclass X {}\n", SourceRewriter.RemoveUsing("using A; using G;\nclass X {}\n", "G"));
        }

        [Test]
        public void AddMovedFrom_Idempotent_AndAboveAttributeLines()
        {
            var src = "namespace N\n{\n    /// doc\n    [Serializable]\n    [Obsolete]\n    public class P {}\n    class PX {}\n}\n";
            var once = SourceRewriter.AddMovedFrom(src, "P", "Old", "Game");
            Assert.AreEqual("namespace N\n{\n    /// doc\n    [UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"Old\", sourceAssembly: \"Game\")]\n" +
                            "    [Serializable]\n    [Obsolete]\n    public class P {}\n    class PX {}\n}\n", once);
            Assert.AreEqual(once, SourceRewriter.AddMovedFrom(once, "P", "Old", "Game"));
            var inline = SourceRewriter.AddMovedFrom("class Outer { class P {} }", "P", "", "A");
            Assert.AreEqual("class Outer { [UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"\", sourceAssembly: \"A\")] class P {} }", inline);
        }

        [Test]
        public void FindQualifiedUses_IgnoresCommentsStringsAndNearMisses()
        {
            var src = "// Old.Gun\nclass X {\n  string s = \"Old.Gun\";\n  OldX.Gun a; Old.GunX b; Game.Old.Gun c;\n  global::Old.Gun d;\n  Old . Gun e;\n}";
            CollectionAssert.AreEqual(new[] { 5, 6 }, SourceRewriter.FindQualifiedUses(src, "Old", new[] { "Gun" }));
        }

        [Test]
        public void FindAssemblyQualifiedStrings_OnlyInStringLiterals()
        {
            var src = "// \"X, Assembly-CSharp\"\nclass A {\n  string t = \"Game.Gun, Assembly-CSharp\";\n  string u = @\"x\n  Y, Assembly-CSharp-firstpass\";\n}";
            CollectionAssert.AreEqual(new[] { 3, 5 }, SourceRewriter.FindAssemblyQualifiedStrings(src));
        }
    }
}
