#nullable enable

using System;
using System.Threading;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The Core-side registration point for the D8 Dry Run environment of
    /// SPEC §D8 item 3 (SPEC §0.6 SG-06): the snapshot-backed public
    /// <see cref="WorkflowCompiler"/> constructor binds its D8 phase to the
    /// environment installed here — the per-snapshot <see cref="StepContext"/>
    /// factory the phase builds step contexts through, and the Edit-Mode and
    /// main-thread probes its §D8 item 3 gate requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Core hosts no Unity implementation, so it cannot produce these
    /// itself: the UnityAdapter installs them once per domain from its
    /// <c>[InitializeOnLoad]</c> bootstrap through
    /// <c>InternalsVisibleTo</c> — the <c>DryRunStepContextFactory</c> /
    /// <c>UnityStepContext</c> surface a Run's Preflight sees, the live
    /// <c>EditorStateProbe</c> and <c>MainThreadDispatcher.IsMainThread</c>.
    /// A host that installs nothing — pure .NET, or an Editor domain whose
    /// bootstrap was skipped (the SPEC §G1 item 3 asset import worker) —
    /// leaves the phase without probes and the gate keeps failing closed
    /// with OW-RUN-001.
    /// </para>
    /// <para>
    /// Installation is first-write-wins for the domain's lifetime and is
    /// published atomically: the gate may be evaluated on a background
    /// thread while the bootstrap installs on the main thread, and a
    /// half-visible environment must never pass the boundary.
    /// </para>
    /// </remarks>
    internal static class DryRunEnvironment
    {
        // The three seams travel as one immutable holder so publication is
        // atomic: a racing gate read sees the whole environment or none.
        internal sealed class Binding
        {
            internal Binding(
                Func<ITypeRegistry, Func<DryRunStepRequest, StepContext>> contextFactoryFor,
                IEditorState editorState,
                Func<bool> isMainThread)
            {
                ContextFactoryFor = contextFactoryFor;
                EditorState = editorState;
                IsMainThread = isMainThread;
            }

            internal Func<ITypeRegistry, Func<DryRunStepRequest, StepContext>> ContextFactoryFor { get; }
            internal IEditorState EditorState { get; }
            internal Func<bool> IsMainThread { get; }
        }

        private static Binding? _binding;

        /// <summary>
        /// Installs the environment once per domain. The first call wins; a
        /// later call in the same domain answers <c>false</c> and changes
        /// nothing — the bootstrap is the domain's single Dry Run
        /// environment authority.
        /// </summary>
        /// <param name="contextFactoryFor">
        /// Binds the per-step <see cref="StepContext"/> factory to a
        /// compiler snapshot's type registry: the port Codec/Ref
        /// conversions resolve against it, matching the run loop's
        /// contexts.
        /// </param>
        /// <param name="editorState">The §D8 item 3 Edit-Mode probe.</param>
        /// <param name="isMainThread">The §D8 item 3 main-thread probe.</param>
        /// <returns><c>true</c> when this call performed the installation.</returns>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal static bool Install(
            Func<ITypeRegistry, Func<DryRunStepRequest, StepContext>> contextFactoryFor,
            IEditorState editorState,
            Func<bool> isMainThread)
        {
            var binding = new Binding(
                contextFactoryFor ?? throw new ArgumentNullException(nameof(contextFactoryFor)),
                editorState ?? throw new ArgumentNullException(nameof(editorState)),
                isMainThread ?? throw new ArgumentNullException(nameof(isMainThread)));
            return Interlocked.CompareExchange(ref _binding, binding, null) is null;
        }

        /// <summary>
        /// The test-only scoped override: swaps the installed binding for
        /// <paramref name="binding"/> — null is the uninstalled reading —
        /// until the returned scope is disposed, which atomically restores
        /// the previously installed one. A test reaches it through
        /// <c>InternalsVisibleTo</c> to observe the fail-closed gate inside
        /// a domain whose bootstrap already installed the real environment;
        /// it does not take part in the first-write-wins contract.
        /// </summary>
        internal static IDisposable OverrideForTest(Binding? binding) =>
            new RestoreScope(Interlocked.Exchange(ref _binding, binding));

        /// <summary>
        /// The installed per-step <see cref="StepContext"/> factory bound to
        /// <paramref name="types"/> — the compiler snapshot's type registry —
        /// or null when no environment is installed, the fail-closed reading.
        /// </summary>
        internal static Func<DryRunStepRequest, StepContext>? ContextFactoryFor(
            ITypeRegistry? types) =>
            types is null ? null : _binding?.ContextFactoryFor(types);

        /// <summary>The installed Edit-Mode probe; null until installed.</summary>
        internal static IEditorState? EditorState => _binding?.EditorState;

        /// <summary>The installed main-thread probe; null until installed.</summary>
        internal static Func<bool>? IsMainThread => _binding?.IsMainThread;

        // The OverrideForTest scope: disposing publishes the captured
        // previous binding back, atomically like an installation.
        private sealed class RestoreScope : IDisposable
        {
            private readonly Binding? _previous;

            internal RestoreScope(Binding? previous) => _previous = previous;

            public void Dispose() => Interlocked.Exchange(ref _binding, _previous);
        }
    }
}
