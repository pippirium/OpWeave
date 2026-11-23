#nullable enable

using System.Runtime.CompilerServices;

// EditMode fixtures drive the service and cursor seams directly (deterministic
// key material, injected Run Store); the public surface stays the SPEC-defined one.
[assembly: InternalsVisibleTo("OpWeave.UnityAdapter.Tests")]
