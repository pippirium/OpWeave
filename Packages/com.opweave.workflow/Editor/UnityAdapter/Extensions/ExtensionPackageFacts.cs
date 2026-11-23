#nullable enable

using System.Collections.Generic;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The on-disk facts the SPEC §C3 stages 2–3 gather about one installed
    /// UPM package that contributes extension candidates: the raw
    /// <c>opweave.extension.json</c> text when present and readable, the
    /// package's resolved root path and the names of the Editor assemblies
    /// rooted inside it.
    /// </summary>
    /// <remarks>
    /// <see cref="ManifestText"/> is null when the package or its manifest
    /// file is absent or unreadable — the SPEC §X.3.2 / §B0.2 metadata gate
    /// treats that like a missing manifest and rejects the package's
    /// candidates (SPEC §X.2). <see cref="EditorAssemblies"/> feeds the
    /// declared-assemblies check of the observed-fact validation.
    /// </remarks>
    internal sealed record ExtensionPackageFacts(
        string? ManifestText,
        string? RootPath,
        IReadOnlyList<string> EditorAssemblies);
}
