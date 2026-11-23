#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Persistence;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The production release path of SPEC §E6 item 4: dispatches a still-open
    /// Run resource to the release handler registered for its <c>kind</c> —
    /// the Unity-side counterpart of the acquisition an Operation journaled
    /// through the step's resource registry (for example the
    /// <c>UnloadPrefabContents</c> counterpart of a Prefab contents
    /// acquisition).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table is internal plumbing the adapter wiring populates: the
    /// production host registers the one resource kind a standard Operation
    /// acquires in v1, SPEC §G6's prefab-contents session
    /// (<see cref="PrefabContentsResources.Release"/>).
    /// Dispatch is fail-safe, never a silent skip: an unknown kind — or a
    /// handler that throws — comes back as a failure diagnostic, so the run
    /// loop leaves the acquisition open in the journal for the §F4 recovery
    /// engine and reports the gap instead of forgetting the resource.
    /// </para>
    /// <para>
    /// <b>Every handler is told which acquisition it is releasing.</b> The Run
    /// id travels with the resource, whose <c>AcquiringStep</c> is the step of
    /// its own <c>resource_acquired</c> record; together they are the
    /// acquisition scope the handler's session-side ownership binding is keyed
    /// by. SPEC §E6 item 4 releases only the Run's own resource, and (kind,
    /// data) names the object without naming who opened it, so a handler that
    /// cannot prove the object is its own must fail and leave the acquisition
    /// open rather than discard somebody else's unsaved work.
    /// </para>
    /// </remarks>
    internal sealed class RunResourceReleaseDispatcher
    {
        private readonly Dictionary<string, Func<string, RunResource, Result<Unit>>> _handlers =
            new Dictionary<string, Func<string, RunResource, Result<Unit>>>(StringComparer.Ordinal);

        /// <summary>
        /// Registers the release handler for one resource <paramref name="kind"/>;
        /// registering the same kind twice replaces the earlier handler.
        /// </summary>
        /// <exception cref="ArgumentException"><paramref name="kind"/> is null or empty.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="release"/> is null.</exception>
        internal void Register(
            string kind, Func<string, RunResource, Result<Unit>> release)
        {
            if (string.IsNullOrEmpty(kind))
            {
                throw new ArgumentException(
                    "A Run resource kind must not be null or empty.", nameof(kind));
            }
            _handlers[kind] = release ?? throw new ArgumentNullException(nameof(release));
        }

        /// <summary>
        /// Releases <paramref name="resource"/>, one still-open acquisition of
        /// Run <paramref name="runId"/>, through the handler registered for its
        /// kind; an unregistered kind or a throwing handler returns a failure
        /// diagnostic — the acquisition stays open in the journal
        /// (SPEC §E6 item 4, §F2).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="resource"/> is null.</exception>
        internal Result<Unit> Release(string runId, RunResource resource)
        {
            if (resource is null)
            {
                throw new ArgumentNullException(nameof(resource));
            }
            if (!_handlers.TryGetValue(resource.Kind, out var release))
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_API_004,
                    "No release handler is registered for the Run resource kind.",
                    hint: resource.Kind));
            }
            try
            {
                return release(runId, resource);
            }
            catch (Exception e)
            {
                return Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_API_004,
                    "The Run resource release handler threw an exception.",
                    hint: e.GetType().FullName));
            }
        }
    }
}
