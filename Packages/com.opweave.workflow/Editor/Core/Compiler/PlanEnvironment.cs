#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The six Unity environment identifiers of SPEC §0.6 SG-11, supplied to
    /// the Build Plan stage by the injected <see cref="IPlanProvenance"/>:
    /// <see cref="UnityVersion"/> is the full Application.unityVersion,
    /// <see cref="EditorOS"/> is Windows/macOS/Linux,
    /// <see cref="ActiveBuildTarget"/> the Unity enum name,
    /// <see cref="ScriptingBackend"/> and
    /// <see cref="ApiCompatibilityLevel"/> the target PlayerSettings values,
    /// and <see cref="ScriptingDefineSymbols"/> the effective define list —
    /// deduplicated and Ordinal-sorted by this record. A value the provider
    /// cannot obtain is never an empty-string default: the provider returns a
    /// failed <see cref="Result{T}"/> and no Plan is created (SPEC §0.6 SG-11).
    /// </summary>
    public sealed record PlanEnvironment(
        string UnityVersion,
        string EditorOS,
        string ActiveBuildTarget,
        string ScriptingBackend,
        string ApiCompatibilityLevel,
        IReadOnlyList<string> ScriptingDefineSymbols)
    {
        /// <summary>The full Unity version string (e.g. <c>"6000.0.85f1"</c>).</summary>
        public string UnityVersion { get; init; } =
            Check(UnityVersion, nameof(UnityVersion));

        /// <summary><c>"Windows"</c>, <c>"macOS"</c> or <c>"Linux"</c>.</summary>
        public string EditorOS { get; init; } =
            Check(EditorOS, nameof(EditorOS));

        /// <summary>The active build target's Unity enum name.</summary>
        public string ActiveBuildTarget { get; init; } =
            Check(ActiveBuildTarget, nameof(ActiveBuildTarget));

        /// <summary>The target PlayerSettings scripting backend value.</summary>
        public string ScriptingBackend { get; init; } =
            Check(ScriptingBackend, nameof(ScriptingBackend));

        /// <summary>The target PlayerSettings API compatibility level.</summary>
        public string ApiCompatibilityLevel { get; init; } =
            Check(ApiCompatibilityLevel, nameof(ApiCompatibilityLevel));

        private readonly IReadOnlyList<string> _defines = CheckDefines(ScriptingDefineSymbols);

        /// <summary>The define symbols, deduplicated and Ordinal-sorted (SPEC §0.6 SG-11).</summary>
        public IReadOnlyList<string> ScriptingDefineSymbols
        {
            get => _defines;
            init => _defines = CheckDefines(value);
        }

        private static string Check(string value, string name) =>
            string.IsNullOrEmpty(value)
                ? throw new ArgumentException(
                    "A required environment identifier must not be null or empty; " +
                    "return a failed Result instead (SPEC §0.6 SG-11).", name)
                : value;

        private static IReadOnlyList<string> CheckDefines(IReadOnlyList<string> defines)
        {
            var deduped = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var define in ReadOnlyCollections.CopyList(defines))
            {
                if (string.IsNullOrEmpty(define))
                {
                    throw new ArgumentException(
                        "A define symbol must not be null or empty.",
                        nameof(ScriptingDefineSymbols));
                }
                deduped.Add(define);
            }
            var list = new List<string>(deduped.Count);
            list.AddRange(deduped);
            return ReadOnlyCollections.CopyList(list);
        }
    }
}
