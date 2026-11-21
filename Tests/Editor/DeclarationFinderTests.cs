using NUnit.Framework;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class DeclarationFinderTests
    {
        [Test]
        public void Find_HandlesNamespacesEnumsAndComments()
        {
            var src = "// class Fake {}\nnamespace A.B {\n public enum Color { R }\n class Gun { class Inner {} }\n}\nstruct Top {}";
            var r = DeclarationFinder.Find(src);
            CollectionAssert.AreEquivalent(new[] { "A.B.Color", "A.B.Gun", "A.B.Gun/Inner", "Top" }, r.Keys);
        }

        [Test]
        public void Find_GenericsDelegatesStringsAndConstraints()
        {
            var src = "namespace N { class Box<T, U> where T : class { string s = \"class Nope {\"; }\n" +
                      "delegate void Handler<T>(T x);\n public delegate System.Collections.Generic.List<int> Make();\n" +
                      "/* struct Hidden {} */ interface IFoo { } }";
            var r = DeclarationFinder.Find(src);
            CollectionAssert.AreEquivalent(new[] { "N.Box`2", "N.Handler`1", "N.Make", "N.IFoo" }, r.Keys);
            Assert.AreEqual("delegate", r["N.Make"]);
            Assert.AreEqual("interface", r["N.IFoo"]);
        }

        [Test]
        public void Find_EscapedIdentifiersAndTupleDelegates()
        {
            var src = "namespace @event . @in { enum Color { Red } class @class { @class other; }\n" +
                      " public delegate (int a, int b) Handler();\n" +
                      " class Q { void M() { Run(delegate (int x) { }); } } }";
            var r = DeclarationFinder.Find(src);
            CollectionAssert.AreEquivalent(new[] { "event.in.Color", "event.in.class", "event.in.Handler", "event.in.Q" }, r.Keys);
        }

        [Test]
        public void Find_NestedTupleAndGenericDelegates()
        {
            var src = "delegate ((int a, int b) pair, int c) Nested();\n" +
                      "delegate System.Collections.Generic.List<(int, (string, int))> H<T>(T x);\n" +
                      "class K { System.Action a = delegate (int x) { }; System.Action b = delegate { }; }";
            CollectionAssert.AreEquivalent(new[] { "Nested", "H`1", "K" }, DeclarationFinder.Find(src).Keys);
        }

        [Test]
        public void Find_PointerReturnDelegates_ButNotFunctionPointers()
        {
            var src = "public unsafe delegate int* Handler();\n" +
                      "unsafe delegate byte** Raw<T>(T x);\n" +
                      "unsafe class F { delegate* unmanaged<int, void> fp; delegate *<int> fq; }";
            CollectionAssert.AreEquivalent(new[] { "Handler", "Raw`1", "F" }, DeclarationFinder.Find(src).Keys);
        }

        [Test]
        public void Find_IfElifElse_FollowsDefines()
        {
            var src = "#if A && !B\nclass X { }\n#elif (C || D) == true\nclass Y { }\n#else\nclass Z { }\n#endif\n";
            CollectionAssert.AreEquivalent(new[] { "X" }, DeclarationFinder.Find(src, new[] { "A" }).Keys);
            CollectionAssert.AreEquivalent(new[] { "Y" }, DeclarationFinder.Find(src, new[] { "A", "B", "D" }).Keys);
            CollectionAssert.AreEquivalent(new[] { "Z" }, DeclarationFinder.Find(src, new[] { "B" }).Keys);
            CollectionAssert.AreEquivalent(new[] { "Z" }, DeclarationFinder.Find(src).Keys);
        }

        [Test]
        public void Find_NestedIf_DefineUndef_AndBracesAcrossBranches()
        {
            var nested = "#if A\n  #if B\nclass AB { }\n  #else\nclass ANotB { }\n  #endif\n#endif\n";
            CollectionAssert.AreEquivalent(new[] { "ANotB" }, DeclarationFinder.Find(nested, new[] { "A" }).Keys);
            CollectionAssert.AreEquivalent(new[] { "AB" }, DeclarationFinder.Find(nested, new[] { "A", "B" }).Keys);
            CollectionAssert.IsEmpty(DeclarationFinder.Find(nested, new[] { "B" }).Keys);

            var defs = "#define D // local\n#define E\n#undef E\n#if D && !E\nclass Def { }\n#endif\n";
            CollectionAssert.AreEquivalent(new[] { "Def" }, DeclarationFinder.Find(defs).Keys);

            var braces = "#if A\nclass P : System.Object {\n#else\nclass P : System.IDisposable {\n#endif\n class Inner { } }\nclass After { }";
            CollectionAssert.AreEquivalent(new[] { "P", "P/Inner", "After" }, DeclarationFinder.Find(braces, new[] { "A" }).Keys);
            CollectionAssert.AreEquivalent(new[] { "P", "P/Inner", "After" }, DeclarationFinder.Find(braces).Keys);
        }

        [Test]
        public void Find_ReportsPartialModifier()
        {
            var r = DeclarationFinder.Find("public sealed partial class P { partial struct S { } } class C { }");
            Assert.AreEqual("partial class", r["P"]);
            Assert.AreEqual("partial struct", r["P/S"]);
            Assert.AreEqual("class", r["C"]);
        }

        [Test]
        public void Find_SourceEndingInStringPrefix_DoesNotThrow()
        {
            CollectionAssert.AreEquivalent(new[] { "A" }, DeclarationFinder.Find("class A { } @$").Keys);
            CollectionAssert.AreEquivalent(new[] { "A" }, DeclarationFinder.Find("class A { } $$").Keys);
        }
    }
}
