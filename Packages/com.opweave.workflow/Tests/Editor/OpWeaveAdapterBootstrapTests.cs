#nullable enable

// T-W2.1-01 [Edit] tests: local:T-W2.1-01:dispatcher-probe — SPEC §G1 item 3.
// The initialization entry point skips every OpWeave service when the
// worker-process predicate reports an asset import worker process; the
// fixture drives the predicate and observes initialization through the
// injected service delegate.
using NUnit.Framework;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class OpWeaveAdapterBootstrapTests
    {
        [Test]
        public void Initialize_WorkerProcess_SkipsAllOpWeaveServices()
        {
            var initialized = false;
            var ran = OpWeaveAdapterBootstrap.Initialize(
                () => true, () => initialized = true);

            Assert.That(ran, Is.False);
            Assert.That(initialized, Is.False);
        }

        [Test]
        public void Initialize_EditorProcess_InitializesOpWeaveServices()
        {
            var initialized = false;
            var ran = OpWeaveAdapterBootstrap.Initialize(
                () => false, () => initialized = true);

            Assert.That(ran, Is.True);
            Assert.That(initialized, Is.True);
        }
    }
}
