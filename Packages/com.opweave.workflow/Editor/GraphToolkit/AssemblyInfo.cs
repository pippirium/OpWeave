#nullable enable

using System.Runtime.CompilerServices;

// The EditMode test assemblies drive the Descriptor lookup through the
// internal snapshot seam (GraphToolkitRegistry.SnapshotSource); the public
// surface stays the SPEC-defined one. Graph Toolkit tests live in their
// own 6000.5-gated assembly since the host Tests asmdef cannot reference
// this assembly below Unity 6000.5.
[assembly: InternalsVisibleTo("OpWeave.UnityAdapter.Tests")]
[assembly: InternalsVisibleTo("OpWeave.GraphToolkit.Tests")]
