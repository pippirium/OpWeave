#nullable enable

using System.Runtime.CompilerServices;

// The EditMode test assemblies drive the dispatcher drain through the internal
// seam directly (invoking EditorApplication.update inside a test re-enters the
// Unity Test Framework's own update callback); the public surface stays the
// SPEC-defined one. The Graph Toolkit tests live in their own 6000.5-gated
// assembly.
[assembly: InternalsVisibleTo("OpWeave.UnityAdapter.Tests")]
[assembly: InternalsVisibleTo("OpWeave.GraphToolkit.Tests")]
// SPEC §G2's public IRefResolver carries only Resolve<T> where T :
// UnityEngine.Object; Scene is a struct, so Operations reach the
// concrete RefResolver.ResolveScene through this internal seam.
[assembly: InternalsVisibleTo("OpWeave.Operations")]
// SPEC §C3 step 8 names the GUI a reader of the atomic publication point:
// OpWeave.GraphToolkit's Descriptor lookup (H6.2 OnDefinePorts) reads the
// published RegistrySnapshot through RegistryPublication.Shared.
[assembly: InternalsVisibleTo("OpWeave.GraphToolkit")]
// SPEC §H5: the Run Viewer's approval panel reaches the runner host's
// OpWeaveApprovalService (the human-only decision surface of T-W3.4-02).
[assembly: InternalsVisibleTo("OpWeave.Viewer")]
// SPEC §H1–§H4: the Agent API's production installer reads the installed
// runner host (OpWeaveRunnerHost.Current/Events/Store/RunStoreRoot) and the
// §F7 journaled-change reader (JournaledChanges) — the same read seams the
// Run Viewer and tests already use, never a second writer.
[assembly: InternalsVisibleTo("OpWeave.AgentApi")]
// SPEC §M.6 item 2: the test-only contract assertions reuse the stage-6
// "Waitable implements Poll" flag (PollImplementation) and the §G2
// hierarchy-path encoding (RefHierarchyPath) instead of duplicating them —
// the same internals the EditMode test assembly already sees.
[assembly: InternalsVisibleTo("OpWeave.Testing")]
