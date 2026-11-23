#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Compiler;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// One unfinished Run's identity plus the Plan whose used definitions
    /// SPEC §D0.3 forbids OpWeave-driven mutations to touch (T-W4.2-06).
    /// The Unity-side guard derives the concrete protected-definition set
    /// through <see cref="RunDefinitionProtection.ForRun"/>.
    /// </summary>
    internal sealed class ProtectedRun
    {
        internal ProtectedRun(string runId, CompiledPlan plan)
        {
            RunId = runId ?? throw new ArgumentNullException(nameof(runId));
            Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        }

        /// <summary>The Run identifier — the safe identity a refusal diagnostic reports.</summary>
        internal string RunId { get; }

        /// <summary>The Run's Plan — the authority on used Operations, Types and Codecs.</summary>
        internal CompiledPlan Plan { get; }
    }

    /// <summary>
    /// The protected-definition set SPEC §D0.3 derives from one unfinished
    /// Run's Plan and the current Registry: the simple name of every
    /// assembly that defines a used Operation, Type or Codec — the whole
    /// DLL is protected, including classes the Plan never uses — and the
    /// UPM package every used Operation ships in (SPEC §D0.3 item 2).
    /// </summary>
    /// <remarks>
    /// <see cref="Uncertain"/> is the SPEC §D0.3 item 3 fail-closed flag: a
    /// used Operation the current Registry no longer resolves, or a Plan
    /// fingerprint member that is not a well-formed array of
    /// <c>{…,"definingAssemblyName":&lt;string&gt;}</c> entries, leaves the
    /// Run's protected set unprovable — every definition-changing mutation
    /// is then undecidable and must answer OW-RUN-013.
    /// </remarks>
    internal sealed class ProtectedRunDefinitions
    {
        internal ProtectedRunDefinitions(
            string runId,
            bool uncertain,
            HashSet<string> assemblyNames,
            HashSet<string> packageIds)
        {
            RunId = runId ?? throw new ArgumentNullException(nameof(runId));
            Uncertain = uncertain;
            AssemblyNames = assemblyNames ?? throw new ArgumentNullException(nameof(assemblyNames));
            PackageIds = packageIds ?? throw new ArgumentNullException(nameof(packageIds));
        }

        /// <summary>The Run these definitions belong to.</summary>
        internal string RunId { get; }

        /// <summary>True when the Plan's protected set cannot be fully established (SPEC §D0.3 item 3).</summary>
        internal bool Uncertain { get; }

        /// <summary>Defining-assembly simple names, Ordinal (used Operations, Types and Codecs).</summary>
        internal HashSet<string> AssemblyNames { get; }

        /// <summary>The UPM package ids used Operations ship in, Ordinal.</summary>
        internal HashSet<string> PackageIds { get; }
    }

    /// <summary>
    /// The SPEC §D0.3 state predicate and per-Run protected-set derivation
    /// (T-W4.2-06): pure Core logic so the dotnet Core tests and the Unity
    /// EditMode tests exercise one identical policy — the Unity-side
    /// guard adds only target classification (paths, assemblies and
    /// packages) on top of the set this derives.
    /// </summary>
    internal static class RunDefinitionProtection
    {
        /// <summary>
        /// SPEC §D0.3 item 1: every Run outside the terminal states is
        /// protected — Queued, AwaitingApproval, Running (a step-level
        /// Waiting keeps its Run Running), Suspended and NeedsReview
        /// alike; NeedsReview stays protected until a human ends it.
        /// </summary>
        internal static bool IsProtectedState(RunState state) =>
            state != RunState.Succeeded &&
            state != RunState.Failed &&
            state != RunState.Cancelled;

        /// <summary>
        /// Derives the Plan's protected-definition set. Used Operations
        /// come from the Plan's steps resolved against the CURRENT
        /// Registry (a used Operation that no longer resolves makes the
        /// set unprovable — <see cref="ProtectedRunDefinitions.Uncertain"/>);
        /// used Types and Codecs come from the persisted
        /// <c>planHashInput</c> fingerprint members, which already carry
        /// each definition's <c>definingAssemblyName</c> (SPEC §D0.1,
        /// §0.6 SG-12).
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal static ProtectedRunDefinitions ForRun(
            ProtectedRun run, RegistrySnapshot registry)
        {
            if (run is null)
            {
                throw new ArgumentNullException(nameof(run));
            }
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }
            var assemblies = new HashSet<string>(StringComparer.Ordinal);
            var packages = new HashSet<string>(StringComparer.Ordinal);
            var uncertain = false;
            var seen = new HashSet<(string Id, int Version)>();
            foreach (var step in run.Plan.Steps)
            {
                if (!seen.Add((step.OperationId, step.Version)))
                {
                    continue;
                }
                var registered = registry.Find(step.OperationId, step.Version);
                if (registered is null)
                {
                    // The used set cannot be established against the
                    // current Registry — fail closed (SPEC §D0.3 item 3).
                    uncertain = true;
                    continue;
                }
                assemblies.Add(registered.AssemblyName);
                if (registered.PackageId is { } packageId)
                {
                    packages.Add(packageId);
                }
            }

            var input = run.Plan.PlanHashInput.ToToken();
            if (input is not JObject planHashInput ||
                !CollectDefiningAssemblies(
                    planHashInput["usedTypeFingerprints"], assemblies) ||
                !CollectDefiningAssemblies(
                    planHashInput["usedCodecFingerprints"], assemblies))
            {
                uncertain = true;
            }

            return new ProtectedRunDefinitions(run.RunId, uncertain, assemblies, packages);
        }

        // Every fingerprint entry carries a required string
        // definingAssemblyName (the shape PlanBuilder writes under
        // §0.6 SG-12): a missing member is a plan with no used
        // definitions of that kind; a malformed one leaves the set
        // unprovable and fails closed.
        private static bool CollectDefiningAssemblies(
            JToken? member, HashSet<string> assemblies)
        {
            if (member is null || member.Type == JTokenType.Null)
            {
                return true;
            }
            if (member is not JArray array)
            {
                return false;
            }
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not JObject entry ||
                    entry["definingAssemblyName"] is not JValue
                        { Type: JTokenType.String } nameToken ||
                    string.IsNullOrEmpty((string?)nameToken.Value))
                {
                    return false;
                }
                assemblies.Add((string)nameToken.Value!);
            }
            return true;
        }
    }
}
