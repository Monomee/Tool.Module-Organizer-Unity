using NUnit.Framework;

namespace Monome.UtilityPack.Editor.Tests
{
    public class SmokeTests
    {
        [Test]
        public void CecilIsAvailable() => Assert.IsNotNull(typeof(Mono.Cecil.AssemblyDefinition));
    }
}
