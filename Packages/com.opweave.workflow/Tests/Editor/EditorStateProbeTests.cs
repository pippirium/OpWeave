#nullable enable

// T-W2.1-01 [Edit] tests: local:T-W2.1-01:dispatcher-probe — SPEC §G1 item 2.
// The probe reports every flag the Runner and Requirements consume; test
// doubles toggle each input, and the live probe is checked against the real
// EditorApplication/BuildPipeline/Application flags.
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class EditorStateProbeTests
    {
        [Test]
        public void LiveProbe_MatchesTheRealEditorFlags()
        {
            var probe = new EditorStateProbe();

            Assert.That(probe.IsPlayingOrWillChangePlaymode,
                Is.EqualTo(EditorApplication.isPlayingOrWillChangePlaymode));
            Assert.That(probe.IsEditMode,
                Is.EqualTo(!EditorApplication.isPlayingOrWillChangePlaymode));
            Assert.That(probe.IsCompiling, Is.EqualTo(EditorApplication.isCompiling));
            Assert.That(probe.IsUpdating, Is.EqualTo(EditorApplication.isUpdating));
            Assert.That(probe.IsBuildingPlayer, Is.EqualTo(BuildPipeline.isBuildingPlayer));
            Assert.That(probe.IsBatchMode, Is.EqualTo(Application.isBatchMode));
        }

        [Test]
        public void Probe_TestDoublesReportEachState()
        {
            var playing = false;
            var compiling = false;
            var updating = false;
            var building = false;
            var batch = false;
            var probe = new EditorStateProbe(
                () => playing, () => compiling, () => updating, () => building, () => batch);

            Assert.That(probe.IsPlayingOrWillChangePlaymode, Is.False);
            Assert.That(probe.IsEditMode, Is.True);
            Assert.That(probe.IsCompiling, Is.False);
            Assert.That(probe.IsUpdating, Is.False);
            Assert.That(probe.IsBuildingPlayer, Is.False);
            Assert.That(probe.IsBatchMode, Is.False);

            playing = true;
            Assert.That(probe.IsPlayingOrWillChangePlaymode, Is.True);
            Assert.That(probe.IsEditMode, Is.False);

            compiling = true;
            Assert.That(probe.IsCompiling, Is.True);

            updating = true;
            Assert.That(probe.IsUpdating, Is.True);

            building = true;
            Assert.That(probe.IsBuildingPlayer, Is.True);

            batch = true;
            Assert.That(probe.IsBatchMode, Is.True);
        }
    }
}
