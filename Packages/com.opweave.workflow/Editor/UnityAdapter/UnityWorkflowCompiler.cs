#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;
using OpWeave.Core.Runtime;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Unity-side construction of the production
    /// <see cref="WorkflowCompiler"/> (SPEC §D1): the same snapshot-backed
    /// pipeline the public Core constructor builds, with the D8 Dry Run
    /// environment seams bound to the adapter's real implementations —
    /// each step's <see cref="StepContext"/> built through
    /// <see cref="DryRunStepContextFactory"/> (the
    /// <see cref="UnityStepContext"/> CLR/Ref conversion surface a Run's
    /// Preflight sees), the §D8 item 3 Edit-Mode gate answered by
    /// <see cref="EditorStateProbe"/> and its main-thread gate answered
    /// by <see cref="MainThreadDispatcher"/> (Play Mode or a background
    /// thread → OW-RUN-001, no Preflight runs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The seams cannot be parameters of the public Core constructor
    /// because the context factory's request type is Core-internal: only
    /// an assembly inside the Core internals boundary — the adapter — can
    /// bind them, which is exactly the SPEC §0.6 SG-06 split (Core hosts
    /// no Unity implementation). Production binds them through the
    /// Core-internal <c>DryRunEnvironment</c> the
    /// <see cref="OpWeaveAdapterBootstrap"/> installs once per domain: the
    /// public snapshot-backed <see cref="WorkflowCompiler"/> constructor
    /// picks the registered environment up itself, so a production Dry
    /// Run needs no internal seam. This factory stays internal for the
    /// remaining case that seam cannot serve — a test that must inject a
    /// double for one probe (the §D8 Play Mode / off-thread refusals)
    /// while every other argument forwards unchanged: the D3
    /// static-check context, the D6 type probe, the §D7 planHash
    /// provenance and the D6 policy inputs the public constructor
    /// documents.
    /// </para>
    /// </remarks>
    internal static class UnityWorkflowCompiler
    {
        /// <summary>
        /// Builds the snapshot-backed production compiler with the D8
        /// seams bound. <paramref name="editorState"/> defaults to the
        /// live <see cref="EditorStateProbe"/>; a test injects its double
        /// through the same seam.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="snapshot"/> is null; the remaining required
        /// arguments throw in the Core constructor.
        /// </exception>
        internal static WorkflowCompiler Create(
            RegistrySnapshot snapshot,
            Func<string, IReadOnlyDictionary<string, InputValue>, StaticContext> staticContextFactory,
            IStaticTypeProbe typeProbe,
            IPlanProvenance provenance,
            IEditorState? editorState = null,
            IReadOnlyList<RegistryChange>? changes = null,
            Func<ActorKind, IPermissionPolicy>? policyFor = null,
            IReadOnlyList<string>? allowedPaths = null) =>
            new WorkflowCompiler(
                snapshot ?? throw new ArgumentNullException(nameof(snapshot)),
                staticContextFactory,
                typeProbe,
                provenance,
                new DryRunStepContextFactory(snapshot.Types).Create,
                editorState ?? new EditorStateProbe(),
                static () => MainThreadDispatcher.IsMainThread,
                changes,
                policyFor,
                allowedPaths);
    }
}
