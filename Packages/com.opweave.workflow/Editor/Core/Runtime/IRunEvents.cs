#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Runtime
{
    /// <summary>
    /// The SPEC §E8 event surface: state changes are delivered to GUI, log
    /// and API consumers synchronously on the main thread after the journal
    /// write that records them. A subscriber's exception is logged and
    /// ignored — it never stops a Run.
    /// </summary>
    /// <remarks>
    /// Subscriptions do not survive a domain reload; subscribers
    /// re-register through <c>[InitializeOnLoad]</c> (SPEC §E8). Registry
    /// diffs (SPEC §C3 rule 7) travel through the same mechanism as
    /// <see cref="RegistryChanged"/>.
    /// </remarks>
    public interface IRunEvents
    {
        /// <summary>The journaled Run-state change (SPEC §E1).</summary>
        event Action<RunStateChanged> RunStateChanged;

        /// <summary>The journaled Step-state change (SPEC §E0).</summary>
        event Action<StepStateChanged> StepStateChanged;

        /// <summary>The journaled <c>log</c> record as a <see cref="RunLogEntry"/> (SPEC §F6).</summary>
        event Action<RunLogEntry> LogAppended;

        /// <summary>The SPEC §C3 rule 7 Registry diff of a new published snapshot.</summary>
        event Action<IReadOnlyList<RegistryChange>> RegistryChanged;
    }
}
