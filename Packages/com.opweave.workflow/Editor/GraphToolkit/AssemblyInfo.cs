#nullable enable

using System.Runtime.CompilerServices;

// The EditMode test assembly drives the Descriptor lookup through the
// internal snapshot seam (GraphToolkitRegistry.SnapshotSource); the public
// surface stays the SPEC-defined one.
[assembly: InternalsVisibleTo("OpWeave.UnityAdapter.Tests")]
