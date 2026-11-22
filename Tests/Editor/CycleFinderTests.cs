using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Monome.UtilityPack.ModuleOrganizer.Tests
{
    public class CycleFinderTests
    {
        [Test]
        public void Finds_TwoAndThreeCycles_IgnoresDag()
        {
            var e = new Dictionary<string, HashSet<string>>
            {
                ["A"] = new() { "B" }, ["B"] = new() { "A" }, ["C"] = new() { "D" }, ["D"] = new() { "E" }, ["E"] = new() { "C" }, ["F"] = new() { "A" },
            };
            var r = CycleFinder.Find(e);
            Assert.AreEqual(2, r.Count);
            CollectionAssert.AreEquivalent(new[] { "A", "B" }, r.Single(c => c.Contains("A")));
            CollectionAssert.AreEqual(new[] { "C", "D", "E" }, r.Single(c => c.Contains("C")));
        }
    }
}
