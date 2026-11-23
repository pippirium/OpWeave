#nullable enable

// T-W5.2-01 [Edit] exclusion check (SPEC §H6 heading, §0.5): the
// OpWeave.GraphToolkit assembly compiles only on Unity 6000.5 and later —
// on 6000.0 the GUI is unavailable while Core and the other assemblies keep
// working. The check needs no Graph Toolkit type, so it runs ungated on
// every Editor version and fails if the constraint ever drifts.
using System;
using System.Linq;
using NUnit.Framework;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class GraphToolkitExclusionTests
    {
        [Test]
        public void GraphToolkitAssembly_LoadedOnlyOnUnity6000_5OrNewer()
        {
            var loaded = AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => assembly.GetName().Name == "OpWeave.GraphToolkit");
#if UNITY_6000_5_OR_NEWER
            Assert.IsTrue(loaded,
                "OpWeave.GraphToolkit must be compiled on Unity 6000.5 or later.");
#else
            Assert.IsFalse(loaded,
                "OpWeave.GraphToolkit must not be compiled below Unity 6000.5 " +
                "(defineConstraints: UNITY_6000_5_OR_NEWER).");
#endif
        }
    }
}
