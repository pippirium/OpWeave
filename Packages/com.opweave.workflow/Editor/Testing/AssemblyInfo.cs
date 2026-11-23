#nullable enable

using System.Runtime.CompilerServices;

// The EditMode contract suites feed the standard §I.3 Operations that read
// the active Run's frozen allowed-paths snapshot through their test-seam
// constructors — registered through HarnessOperationSource.Override, which
// stays internal so the public surface of this assembly keeps to the
// workflow-less contract checks of SPEC §M.6.
[assembly: InternalsVisibleTo("OpWeave.UnityAdapter.Tests")]
