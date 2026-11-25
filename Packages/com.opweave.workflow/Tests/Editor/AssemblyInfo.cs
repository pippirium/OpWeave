#nullable enable

using System.Runtime.CompilerServices;

// The Graph Toolkit EditMode suite lives in its own assembly so that the
// OpWeave.UnityAdapter.Tests asmdef carries no reference to
// OpWeave.GraphToolkit, which compiles only on Unity 6000.5+; the Graph
// Toolkit tests still reuse the shared internal fixtures
// (RunnerHostFixture, EditorProcessHarness).
[assembly: InternalsVisibleTo("OpWeave.GraphToolkit.Tests")]
