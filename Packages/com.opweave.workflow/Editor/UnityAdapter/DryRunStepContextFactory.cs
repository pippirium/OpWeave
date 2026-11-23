#nullable enable

using System;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Unity-side <see cref="StepContext"/> factory the Core compiler's
    /// D8 Dry Run phase drives (SPEC §D8, §0.6 SG-06): each step gets a real
    /// <see cref="UnityStepContext"/> — the same CLR/Ref conversion surface a
    /// Run's Preflight sees — with the node's resolved input values, the
    /// SG-06 static port types and the pending-value map carrying every
    /// data-edge-fed port's producing node id (SPEC §D8 item 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Dry Run owns no Run and writes no journal: the context's §F7
    /// recorder is the in-memory per-step recorder and its §F6 sink appends
    /// nowhere — Preflight is contractually side-effect-free (SPEC §C2
    /// item 7), so anything it records is diagnosis, never a change. The
    /// §E6 item 4 Run-resource registry is absent like a context built
    /// outside the run loop.
    /// </para>
    /// <para>
    /// The §D8 item 3 Edit-Mode gate lives with the pipeline's editor-state
    /// seam, not here — this type only builds contexts.
    /// </para>
    /// </remarks>
    internal sealed class DryRunStepContextFactory
    {
        private readonly ITypeRegistry _types;
        private readonly RefResolver _refs;

        /// <summary>
        /// Binds the factory to the snapshot's type registry (the port
        /// Codec/Ref conversions resolve against it, matching the run loop's
        /// contexts) and a Ref resolver; a null resolver selects the default
        /// one.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="types"/> is null.</exception>
        internal DryRunStepContextFactory(ITypeRegistry types, RefResolver? refs = null)
        {
            _types = types ?? throw new ArgumentNullException(nameof(types));
            _refs = refs ?? new RefResolver();
        }

        /// <summary>
        /// Builds the <see cref="UnityStepContext"/> for one Dry Run step
        /// request — the pipeline supplies everything the SG-06 surface
        /// needs; the inert per-step recorder and sink keep the call's §F7/§F6
        /// surface intact.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        internal StepContext Create(DryRunStepRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            return new UnityStepContext(
                request.Inputs,
                request.InputTypes,
                request.OutputTypes,
                _types,
                _refs,
                new StepChangeRecorder(request.StepIndex, request.NodeId),
                new StepLogSink(
                    "dryrun",
                    request.StepIndex,
                    (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                pendingProducers: request.PendingProducers,
                operationId: request.OperationId,
                operationOrigin: request.Origin);
        }
    }
}
