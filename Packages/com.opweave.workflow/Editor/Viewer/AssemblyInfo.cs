#nullable enable

using System.Runtime.CompilerServices;

// The EditMode test assembly binds a fixture runner host into the window
// through the internal source seam; the public surface stays the SPEC §H5
// one.
[assembly: InternalsVisibleTo("OpWeave.UnityAdapter.Tests")]
