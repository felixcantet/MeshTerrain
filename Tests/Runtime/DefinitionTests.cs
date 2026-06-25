using NUnit.Framework;
using UnityEngine;

namespace Fca.MeshTerrain.Tests
{
    public class DefinitionTests
    {
        [Test]
        public void Definition_HasExpectedDefaults()
        {
            var def = ScriptableObject.CreateInstance<MeshPartitionDefinition>();
            try
            {
                Assert.AreEqual(100f, def.ChannelTexelSize);
                Assert.AreEqual(256f * 256f * 4f, def.MaxSectionComplexity);
                Assert.AreEqual(6400u, def.CellSize);
                Assert.IsTrue(def.Is2D);
                Assert.IsNotNull(def.ChannelNames);
            }
            finally
            {
                Object.DestroyImmediate(def);
            }
        }
    }
}
