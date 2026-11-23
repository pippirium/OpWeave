#nullable enable

using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// A <c>[OpWeaveTypeProvider]</c> class whose defining assembly could
    /// not be attributed to any installed package — the SPEC §G0 resolution
    /// failed, so no manifest can be located for it. Recorded so the
    /// registration stage can emit its per-candidate diagnostic
    /// (OW-OP-017, T-W2.12-03); the candidate registers nothing here.
    /// </summary>
    public sealed record UnattributedExtensionCandidate(
        string TypeFullName,
        string AssemblyName,
        IReadOnlyList<Diagnostic> Diagnostics);
}
