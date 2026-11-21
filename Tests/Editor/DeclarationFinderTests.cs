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
    }
}
