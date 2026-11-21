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
        public void RemoveUsing_KeepsStaticAlias_RemovesNamespaceLevel()
        {
            var src = "using static Game.Combat;\nusing C = Game.Combat;\nusing Game.Combat; // gone\nnamespace N\n{\n    using Game.Combat;\n    using Game . Combat ; using X;\n    class Y {}\n}\n";
            Assert.AreEqual("using static Game.Combat;\nusing C = Game.Combat;\nnamespace N\n{\n    using X;\n    class Y {}\n}\n",
                SourceRewriter.RemoveUsing(src, "Game.Combat"));
            Assert.AreEqual("using A;\nclass X {}\n", SourceRewriter.RemoveUsing("using A; using G;\nclass X {}\n", "G"));
        }

        [Test]
        public void RemoveUsing_SharingALineWithCode_LeavesExactlyOneSeparator()
        {
            Assert.AreEqual("namespace N\n{\n    class Y {}\n}\n", SourceRewriter.RemoveUsing("namespace N\n{\n    using G;   class Y {}\n}\n", "G"));
            Assert.AreEqual("namespace N { class Y {} }\n", SourceRewriter.RemoveUsing("namespace N {using G;  class Y {} }\n", "G"));
            Assert.AreEqual("namespace N { class Y {} }\n", SourceRewriter.RemoveUsing("namespace N { using G; class Y {} }\n", "G"));
            Assert.AreEqual("using A;\r\nclass X {}\r\n", SourceRewriter.RemoveUsing("using A;\tusing G; \r\nclass X {}\r\n", "G"));
        }

        [Test]
        public void AddUsing_Marked_IsFoundByMarkedUsings_AndRemovedWithItsMarker()
        {
            var src = SourceRewriter.AddUsing("using A;\nclass X {}\n", "Game.Combat", mark: true);
            Assert.AreEqual("using A;\nusing Game.Combat; // monome:using\nclass X {}\n", src);
            CollectionAssert.AreEqual(new[] { "Game.Combat" }, SourceRewriter.MarkedUsings(src + "// using B; // monome:using\n"));
            Assert.AreEqual("using A;\nclass X {}\n", SourceRewriter.RemoveUsing(src, "Game.Combat"));
            CollectionAssert.IsEmpty(SourceRewriter.MarkedUsings("using A;\nclass X {}\n"));
        }

        [Test]
        public void Unwrap_TwoSpaceIndent_DedentsOneLevelEverywhere()
        {
            Assert.AreEqual("class A\n{\n  int x;\n  void F()\n  {\n  }\n}\n",
                SourceRewriter.SetNamespace("namespace N\n{\n  class A\n  {\n    int x;\n    void F()\n    {\n    }\n  }\n}\n", ""));
            Assert.AreEqual("class A {}\n  // not indented content stays as it is\n",
                SourceRewriter.SetNamespace("namespace N\n{\nclass A {}\n  // not indented content stays as it is\n}\n", ""));
        }

        [Test]
        public void Unwrap_IndentUnit_ComesFromTheFirstCodeLine_NotADirectiveOrComment()
        {
            Assert.AreEqual("#if A\n// note\nclass A\n{\n  int x;\n}\n#endif\n",
                SourceRewriter.SetNamespace("namespace N\n{\n#if A\n// note\n  class A\n  {\n    int x;\n  }\n#endif\n}\n", ""));
        }

        [Test]
        public void Unwrap_ConditionalUsingGroupBelowAnAssemblyAttribute_MovesWhole_GroupWithCodeStaysAndIsReported()
        {
            var r = SourceRewriter.SetNamespace("[assembly: Foo]\nnamespace N\n{\n#if UNITY_EDITOR\n    using UnityEditor;\n#endif\n    class C {}\n}\n", "");
            Assert.AreEqual("#if UNITY_EDITOR\nusing UnityEditor;\n#endif\n[assembly: Foo]\nclass C {}\n", r);
            Assert.IsFalse(SourceRewriter.UsingBelowGlobalAttribute(r));

            r = SourceRewriter.SetNamespace("[assembly: Foo]\nnamespace N\n{\n#if UNITY_EDITOR\n    using UnityEditor;\n    class E {}\n#endif\n}\n", "");
            StringAssert.StartsWith("[assembly: Foo]\n#if UNITY_EDITOR\nusing UnityEditor;\n", r);
            Assert.IsTrue(SourceRewriter.UsingBelowGlobalAttribute(r));
        }

        [Test]
        public void Unwrap_BlockUsingBelowAnAssemblyAttribute_MovesAboveIt()
        {
            Assert.AreEqual("using X;\nusing Y;\n[assembly: Foo]\n\nclass C {}\n",
                SourceRewriter.SetNamespace("using X;\n[assembly: Foo]\n\nnamespace N\n{\n    using Y;\n\n    class C {}\n}\n", ""));
            Assert.AreEqual("using Y;\n#if A\n[assembly: Foo]\n#endif\nclass C {}\n",
                SourceRewriter.SetNamespace("#if A\n[assembly: Foo]\n#endif\nnamespace N\n{\n    using Y;\n    class C {}\n}\n", ""));
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

        // --- fix round 1 regressions ---

        const string MF = "[UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"\", sourceAssembly: \"A\")]";

        [Test]
        public void AddMovedFrom_AnnotatesEveryConditionalDeclaration()
        {
            var src = "#if UNITY_EDITOR\npublic class P { int a; }\n#else\npublic class P { }\n#endif\n";
            var r = SourceRewriter.AddMovedFrom(src, "P", "", "A");
            Assert.AreEqual("#if UNITY_EDITOR\n" + MF + "\npublic class P { int a; }\n#else\n" + MF + "\npublic class P { }\n#endif\n", r);
            Assert.AreEqual(r, SourceRewriter.AddMovedFrom(r, "P", "", "A"));
        }

        [Test]
        public void AddMovedFrom_MultilineModifiers_AndDirectiveBetweenExisting()
        {
            Assert.AreEqual("namespace N\n{\n    " + MF + "\n    public\n    sealed class P {}\n}\n",
                SourceRewriter.AddMovedFrom("namespace N\n{\n    public\n    sealed class P {}\n}\n", "P", "", "A"));
            var has = MF + "\n#if X\n[Foo]\n#endif\nclass P {}\n";
            Assert.AreEqual(has, SourceRewriter.AddMovedFrom(has, "P", "", "A"));
        }

        [Test]
        public void TryAddMovedFrom_ReportsMissingType_AndMatchesCecilArity()
        {
            Assert.IsFalse(SourceRewriter.TryAddMovedFrom("class Q {}", "P", "", "A", out var none));
            Assert.AreEqual("class Q {}", none);
            Assert.IsTrue(SourceRewriter.TryAddMovedFrom("class Box {}\nclass Box<T, U> {}\n", "Box`2", "", "A", out var r));
            Assert.AreEqual("class Box {}\n" + MF + "\nclass Box<T, U> {}\n", r);
            Assert.IsTrue(SourceRewriter.TryAddMovedFrom(r, "Box`2", "", "A", out var again), "found but already present");
            Assert.AreEqual(r, again);
        }

        [Test]
        public void AddUsing_ConditionalUsingDoesNotSatisfy_RegionUsingDoes()
        {
            Assert.AreEqual("#if UNITY_EDITOR\nusing Game.Combat;\n#endif\nusing Game.Combat;\nclass X { Gun g; }\n",
                SourceRewriter.AddUsing("#if UNITY_EDITOR\nusing Game.Combat;\n#endif\nclass X { Gun g; }\n", "Game.Combat"));
            var region = "#region U\nusing G;\n#endregion\nclass X {}\n";
            Assert.AreEqual(region, SourceRewriter.AddUsing(region, "G"));
        }

        [Test]
        public void GlobalAttributes_UsingGoesBefore_WrapKeepsMultilineOutside()
        {
            Assert.AreEqual("using G;\n\n[assembly: Foo]\nclass X {}\n", SourceRewriter.AddUsing("[assembly: Foo]\nclass X {}\n", "G"));
            Assert.AreEqual("using A;\nusing G;\n[assembly: Foo]\nclass X {}\n", SourceRewriter.AddUsing("using A;\n[assembly: Foo]\nclass X {}\n", "G"));
            Assert.AreEqual("using A;\n[assembly: Foo(\n    \"x\")]\nnamespace N\n{\n    class B {}\n}\n",
                SourceRewriter.SetNamespace("using A;\n[assembly: Foo(\n    \"x\")]\nclass B {}\n", "N"));
        }

        [Test]
        public void Unwrap_KeepsMissingTrailingNewline() =>
            Assert.AreEqual("class A {}", SourceRewriter.SetNamespace("namespace N\n{\n    class A {}\n}", ""));

        [Test]
        public void Bom_IsKeptAtStart()
        {
            const string bom = "﻿";
            Assert.AreEqual(bom + "using A;\nusing G;\nclass X {}\n", SourceRewriter.AddUsing(bom + "using A;\nclass X {}\n", "G"));
            Assert.AreEqual(bom + "class X {}\n", SourceRewriter.RemoveUsing(bom + "using G;\nclass X {}\n", "G"));
            Assert.AreEqual(bom + "namespace N\n{\n    class A {}\n}\n", SourceRewriter.SetNamespace(bom + "class A {}\n", "N"));
            Assert.AreEqual(bom + MF + "\nclass P {}", SourceRewriter.AddMovedFrom(bom + "class P {}", "P", "", "A"));
        }

        [Test]
        public void Wrap_DoesNotSplitRegion() =>
            Assert.AreEqual("using A;\nnamespace N\n{\n    #region Types\n    class B {}\n    #endregion\n}\n",
                SourceRewriter.SetNamespace("using A;\n#region Types\nclass B {}\n#endregion\n", "N"));

        [Test]
        public void FindQualifiedUses_EmptyNamespace_ReturnsNothing() =>
            CollectionAssert.IsEmpty(SourceRewriter.FindQualifiedUses("class X { Gun g; }", "", new[] { "Gun" }));

        // --- fix round 2 regressions ---

        [Test]
        public void AddMovedFrom_PartialParts_OnlyFirstGetsIt()
        {
            var src = "partial class P {}\npartial class P {}\n";
            var r = SourceRewriter.AddMovedFrom(src, "P", "", "A");
            Assert.AreEqual(MF + "\npartial class P {}\npartial class P {}\n", r);
            Assert.AreEqual(r, SourceRewriter.AddMovedFrom(r, "P", "", "A"));
            var second = "partial class P {}\n" + MF + "\npublic partial class P {}\n";
            Assert.AreEqual(second, SourceRewriter.AddMovedFrom(second, "P", "", "A"));
        }

        [Test]
        public void Wrap_WouldSwallowAssemblyAttribute_NotRewritable()
        {
            var src = "#region All\n[assembly: System.CLSCompliant(true)]\nclass P {}\n#endregion\n";
            Assert.IsFalse(SourceRewriter.Parse(src).Rewritable);
            Assert.Throws<InvalidOperationException>(() => SourceRewriter.SetNamespace(src, "N"));
        }

        [Test]
        public void TryAddMovedFrom_ArityCountsTopLevelParametersOnly()
        {
            Assert.IsTrue(SourceRewriter.TryAddMovedFrom("class Box<[A(1, 2)] T> {}", "Box`1", "", "A", out _));
            Assert.IsTrue(SourceRewriter.TryAddMovedFrom("class Map<TKey, List<TValue>> {}", "Map`2", "", "A", out _));
            Assert.IsFalse(SourceRewriter.TryAddMovedFrom("class Map<TKey, List<TValue>> {}", "Map`3", "", "A", out _));
        }

        // --- fix round 3 regressions ---

        [Test]
        public void AddMovedFrom_PartialPerConfiguration()
        {
            const string P = "partial class P {}\n";
            var alt = "#if X\n" + P + "#else\n" + P + "#endif\n";
            var r = SourceRewriter.AddMovedFrom(alt, "P", "", "A");
            Assert.AreEqual("#if X\n" + MF + "\n" + P + "#else\n" + MF + "\n" + P + "#endif\n", r);
            Assert.AreEqual(r, SourceRewriter.AddMovedFrom(r, "P", "", "A"));

            Assert.AreEqual(MF + "\n" + P + "#if X\n" + P + "#endif\n",
                SourceRewriter.AddMovedFrom(P + "#if X\n" + P + "#endif\n", "P", "", "A"));

            var nested = "#if A\n" + P + "#if B\n" + P + "#endif\n#else\n" + P + "#endif\n";
            Assert.AreEqual("#if A\n" + MF + "\n" + P + "#if B\n" + P + "#endif\n#else\n" + MF + "\n" + P + "#endif\n",
                SourceRewriter.AddMovedFrom(nested, "P", "", "A"));
        }

        [Test]
        public void AddMovedFrom_UnconditionalPartialWinsOverConditional()
        {
            const string P = "partial class P {}\n";
            Assert.AreEqual("#if X\n" + P + "#endif\n" + MF + "\n" + P,
                SourceRewriter.AddMovedFrom("#if X\n" + P + "#endif\n" + P, "P", "", "A"));
            Assert.AreEqual(MF + "\n" + P + "#if X\n" + P + "#endif\n",
                SourceRewriter.AddMovedFrom(P + "#if X\n" + P + "#endif\n", "P", "", "A"));
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

        // --- task 7 fix round 1 ---

        [Test]
        public void AddMovedFrom_ExistingMovedFrom_IsRepointed()
        {
            const string old = "[UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"\", sourceAssembly: \"Assembly-CSharp\")]";
            const string now = "[UnityEngine.Scripting.APIUpdating.MovedFrom(false, sourceNamespace: \"Game.Combat\", sourceAssembly: \"Game.Combat\")]";
            var src = "namespace Game.Gear\n{\n    " + old + "\n    [System.Serializable] public class P {}\n}\n";
            var r = SourceRewriter.AddMovedFrom(src, "P", "Game.Combat", "Game.Combat");
            Assert.AreEqual("namespace Game.Gear\n{\n    " + now + "\n    [System.Serializable] public class P {}\n}\n", r);
            Assert.AreEqual(r, SourceRewriter.AddMovedFrom(r, "P", "Game.Combat", "Game.Combat"));
            // Short form, on the partial part that carries it: still one per type.
            var partial = "using UnityEngine.Scripting.APIUpdating;\n[MovedFrom(true, \"Old\")] partial class P {}\npartial class P {}\n";
            Assert.AreEqual("using UnityEngine.Scripting.APIUpdating;\n" + MF + " partial class P {}\npartial class P {}\n",
                SourceRewriter.AddMovedFrom(partial, "P", "", "A"));
            // A qualified name spread over whitespace and comments is replaced whole.
            var spaced = "[UnityEngine . /* x */ Scripting.APIUpdating\n    .MovedFrom(false, \"Old\")] class P {}\n";
            Assert.AreEqual(MF + " class P {}\n", SourceRewriter.AddMovedFrom(spaced, "P", "", "A"));
            Assert.AreEqual(MF + " class P {}\n", SourceRewriter.AddMovedFrom("[global::UnityEngine.Scripting.APIUpdating.MovedFrom(true)] class P {}\n", "P", "", "A"));
        }

        [Test]
        public void FindUsings_PlainDirectives_HeaderAndNamespaceLevel() =>
            CollectionAssert.AreEquivalent(new[] { "Game.Combat", "System", "Inner" },
                SourceRewriter.FindUsings("using Game . Combat;\nusing static Foo.Bar;\nusing A = B.C;\n#if X\nusing System;\n#endif\nnamespace N\n{\n    using Inner;\n}\n"));

        [Test]
        public void AddNamespaceAliases_FirstInTheBlock_MarkedIdempotentRemovable()
        {
            const string a = "using Editor = global::UnityEditor.Editor; // monome:alias";
            var al = new[] { ("Editor", "global::UnityEditor.Editor") };
            var src = "using UnityEditor;\r\n\r\nnamespace G.Editor\r\n{ // x\r\n    class E : Editor {}\r\n}\r\n";
            var r = SourceRewriter.AddNamespaceAliases(src, al);
            Assert.AreEqual("using UnityEditor;\r\n\r\nnamespace G.Editor\r\n{ // x\r\n    " + a + "\r\n\r\n    class E : Editor {}\r\n}\r\n", r);
            Assert.AreEqual(r, SourceRewriter.AddNamespaceAliases(r, al));
            CollectionAssert.AreEqual(al, SourceRewriter.MarkedAliases(r));
            Assert.AreEqual(src, SourceRewriter.RemoveMarkedAliases(r));
            Assert.AreEqual("namespace N {\n    " + a + "\n class E {} }", SourceRewriter.AddNamespaceAliases("namespace N { class E {} }", al));
            Assert.Throws<System.InvalidOperationException>(() => SourceRewriter.AddNamespaceAliases("class E {}\n", al));
        }

        [Test]
        public void AddNamespaceAliases_NeverInsideAComment_MarkerInCommentIgnored()
        {
            var al = new[] { ("Editor", "global::UnityEditor.Editor") };
            var src = "namespace N { /* comment\ncontinued */\n    class E : Editor {}\n}\n";
            Assert.AreEqual("namespace N {\n    using Editor = global::UnityEditor.Editor; // monome:alias\n /* comment\ncontinued */\n    class E : Editor {}\n}\n",
                SourceRewriter.AddNamespaceAliases(src, al));
            var fake = "namespace N\n{\n/*\n    using Editor = X; // monome:alias\n*/\n}\n";
            Assert.IsEmpty(SourceRewriter.MarkedAliases(fake));
            Assert.AreEqual(fake, SourceRewriter.RemoveMarkedAliases(fake));
            // A user's own (unmarked) alias is neither reported nor removed, and blocks a second one of that name.
            var user = "namespace N\n{\n    using Editor = UnityEditor.Editor;\n}\n";
            Assert.IsEmpty(SourceRewriter.MarkedAliases(user));
            Assert.AreEqual(user, SourceRewriter.RemoveMarkedAliases(user));
            Assert.AreEqual(user, SourceRewriter.AddNamespaceAliases(user, al));
        }
    }
}
