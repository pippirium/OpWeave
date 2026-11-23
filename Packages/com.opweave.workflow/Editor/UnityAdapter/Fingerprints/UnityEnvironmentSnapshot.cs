#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// Captures the six SG-11 Unity environment identifiers of SPEC §0.6
    /// (weak-fingerprint material, SPEC §C0.2) as one immutable
    /// <see cref="PlanEnvironment"/> snapshot: <c>unityVersion</c> is the full
    /// <see cref="Application.unityVersion"/>, <c>editorOS</c> is
    /// Windows/macOS/Linux, <c>activeBuildTarget</c> the Unity enum name,
    /// <c>scriptingBackend</c> and <c>apiCompatibilityLevel</c> the active
    /// target's PlayerSettings values, and <c>scriptingDefineSymbols</c> the
    /// active target's effective define list.
    /// </summary>
    /// <remarks>
    /// A required value the Editor cannot answer — an empty version, target
    /// or settings string, an unrecognized OS family, an unnamed build
    /// target, or a PlayerSettings query that throws or answers null — is
    /// reported as a failed <see cref="Result{T}"/> carrying OW-OP-020
    /// diagnostics, never substituted with an empty-string default: an
    /// unobtainable environment identifier rejects the new Plan/Run
    /// (SPEC §0.6 SG-11, §J). Only a successfully queried define-symbol
    /// list may be empty — a project legitimately has no defines — while
    /// a failed or null answer fails closed like every other identifier.
    /// </remarks>
    public static class UnityEnvironmentSnapshot
    {
        /// <summary>
        /// Reads the current Editor's six environment identifiers. The
        /// returned snapshot is immutable; deduplication and Ordinal sorting
        /// of the define symbols are <see cref="PlanEnvironment"/>'s own
        /// contract (SPEC §0.6 SG-11).
        /// </summary>
        public static Result<PlanEnvironment> Capture() =>
            Capture(LiveScriptingDefineSymbols);

        /// <summary>
        /// <see cref="Capture()"/> with the PlayerSettings define-symbol
        /// query injected — the seam the fail-closed tests use to exercise a
        /// query that throws or answers null (SPEC §0.6 SG-11). The query is
        /// asked only when the active build target resolves to a named
        /// target.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="scriptingDefineSymbols"/> is null.</exception>
        public static Result<PlanEnvironment> Capture(
            Func<NamedBuildTarget, string[]?> scriptingDefineSymbols)
        {
            if (scriptingDefineSymbols is null)
            {
                throw new ArgumentNullException(nameof(scriptingDefineSymbols));
            }

            var diagnostics = new List<Diagnostic>();

            var unityVersion = TryGetRef(() => Application.unityVersion);
            if (string.IsNullOrEmpty(unityVersion))
            {
                diagnostics.Add(Unobtainable("unityVersion"));
            }

            var family = TryGetValue(() => SystemInfo.operatingSystemFamily);
            var editorOS = EditorOSName(family);
            if (editorOS is null)
            {
                diagnostics.Add(Unobtainable("editorOS"));
            }

            var activeBuildTarget = TryGetValue(() => EditorUserBuildSettings.activeBuildTarget);
            var activeBuildTargetName = activeBuildTarget.ToString();
            if (string.IsNullOrEmpty(activeBuildTargetName))
            {
                diagnostics.Add(Unobtainable("activeBuildTarget"));
            }

            string? scriptingBackend = null;
            string? apiCompatibilityLevel = null;
            string[]? defineSymbols = null;
            var group = TryGetValue(() => BuildPipeline.GetBuildTargetGroup(activeBuildTarget));
            var named = group == BuildTargetGroup.Unknown
                ? default(NamedBuildTarget)
                : NamedBuildTarget.FromBuildTargetGroup(group);
            if (string.IsNullOrEmpty(named.TargetName))
            {
                // The three target-derived identifiers share one cause: the
                // active target has no named target the PlayerSettings keys
                // can be read from.
                diagnostics.Add(Unobtainable("scriptingBackend"));
                diagnostics.Add(Unobtainable("apiCompatibilityLevel"));
                diagnostics.Add(Unobtainable("scriptingDefineSymbols"));
            }
            else
            {
                scriptingBackend =
                    TryGetRef(() => PlayerSettings.GetScriptingBackend(named).ToString());
                if (string.IsNullOrEmpty(scriptingBackend))
                {
                    diagnostics.Add(Unobtainable("scriptingBackend"));
                }
                apiCompatibilityLevel =
                    TryGetRef(() => PlayerSettings.GetApiCompatibilityLevel(named).ToString());
                if (string.IsNullOrEmpty(apiCompatibilityLevel))
                {
                    diagnostics.Add(Unobtainable("apiCompatibilityLevel"));
                }
                try
                {
                    defineSymbols = scriptingDefineSymbols(named);
                }
                catch (Exception)
                {
                    defineSymbols = null;
                }
                // A failed or null answer is unobtainable — not the empty
                // list (SPEC §0.6 SG-11); a queried empty array succeeds.
                if (defineSymbols is null)
                {
                    diagnostics.Add(Unobtainable("scriptingDefineSymbols"));
                }
            }

            if (diagnostics.Count != 0)
            {
                return Result<PlanEnvironment>.Failure(diagnostics);
            }

            return Result<PlanEnvironment>.Success(new PlanEnvironment(
                unityVersion!,
                editorOS!,
                activeBuildTargetName,
                scriptingBackend!,
                apiCompatibilityLevel!,
                defineSymbols!));
        }

        // SPEC §0.6 SG-11: editorOS is exactly Windows, macOS or Linux; any
        // other family is an unobtainable required value, never a guess.
        private static string? EditorOSName(OperatingSystemFamily family) => family switch
        {
            OperatingSystemFamily.Windows => "Windows",
            OperatingSystemFamily.MacOSX => "macOS",
            OperatingSystemFamily.Linux => "Linux",
            _ => null,
        };

        private static T? TryGetRef<T>(Func<T> query) where T : class
        {
            try
            {
                return query();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // PlayerSettings' out-parameter API adapted to the query delegate.
        private static string[]? LiveScriptingDefineSymbols(NamedBuildTarget target)
        {
            PlayerSettings.GetScriptingDefineSymbols(target, out var defines);
            return defines;
        }

        private static T TryGetValue<T>(Func<T> query) where T : struct
        {
            try
            {
                return query();
            }
            catch (Exception)
            {
                return default;
            }
        }

        private static Diagnostic Unobtainable(string identifier) => ErrorCodes.Create(
            ErrorCodes.OW_OP_020,
            $"A required Unity environment identifier could not be obtained: {identifier}.");
    }
}
