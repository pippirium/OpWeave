#nullable enable

using System;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The stale-Registry refusal of SPEC §C3 rule 9 (the SG-18 ordering's
    /// second acceptance gate): while the published Registry is stale — a
    /// failed script compile kept the old assemblies, or no build has
    /// completed yet — a new Run is refused with OW-RUN-009 before any side
    /// effect. The gate reports the refusal's own diagnostic; SG-18 keeps it
    /// behind the settings-error gate and ahead of Plan-permission checks.
    /// </summary>
    internal sealed class StaleRegistryGate : IRunAcceptanceGate
    {
        private readonly Func<bool> _registryStale;

        /// <summary>
        /// <paramref name="registryStale"/> is re-read per accept so a
        /// compile error that appears after publication refuses Runs
        /// immediately — in production it reads
        /// <see cref="RegistryPublication.Stale"/>, which already reflects
        /// the live <see cref="UnityEditor.EditorUtility.scriptCompilationFailed"/>
        /// signal a failed compile raises without a domain reload (SPEC
        /// §C3 rule 9: "scriptCompilationFailed が真の間").
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="registryStale"/> is null.</exception>
        internal StaleRegistryGate(Func<bool> registryStale) =>
            _registryStale = registryStale ?? throw new ArgumentNullException(nameof(registryStale));

        /// <inheritdoc cref="IRunAcceptanceGate.Admit"/>
        public Result<Unit> Admit(RunAcceptanceRequest request) =>
            _registryStale()
                ? Result<Unit>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_RUN_009,
                    "The Operation Registry is stale because script compilation failed; new Runs are not accepted."))
                : Result<Unit>.Success(Unit.Value);
    }
}
