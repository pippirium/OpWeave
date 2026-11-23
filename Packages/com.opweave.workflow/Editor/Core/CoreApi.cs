#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// The Core public API version external extensions declare compatibility
    /// against (SPEC §B0.2). Managed independently of the
    /// <c>com.opweave.workflow</c> package SemVer: breaking public-contract
    /// changes bump MAJOR, backward-compatible additions MINOR, contract-neutral
    /// fixes PATCH.
    /// </summary>
    public static class CoreApi
    {
        /// <summary>The current Core public API version as MAJOR.MINOR.PATCH (SPEC §B0.2).</summary>
        public const string Version = "1.0.0";

        /// <summary><see cref="Version"/> parsed into a <see cref="CoreApiVersion"/> (SPEC §B0.2).</summary>
        public static CoreApiVersion Current { get; } =
            CoreApiVersion.Parse(Version).Value;
    }
}
