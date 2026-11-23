#nullable enable

using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §G4 domain-reload counter: SessionState's
    /// <c>OpWeave.ReloadCount</c> is incremented once on every
    /// <c>[InitializeOnLoad]</c> so a wait condition can tell the domains
    /// before and after a reload apart ("リロードの前後を知るため").
    /// </summary>
    /// <remarks>
    /// <para>
    /// SessionState lives exactly as long as the Editor session, so the
    /// count survives the domain reload it measures but resets on a full
    /// Editor restart — matching the §G4 model where a wait resumed from a
    /// persisted token compares its recorded count against the live one.
    /// The increment runs inside the adapter bootstrap's
    /// <c>[InitializeOnLoad]</c> initialization, which SPEC §G1 item 3
    /// skips in asset import worker processes, so the counter tracks the
    /// main Editor domain's reloads alone.
    /// </para>
    /// </remarks>
    internal static class DomainReloadCounter
    {
        /// <summary>The SPEC §G4 SessionState key of the reload counter.</summary>
        internal const string SessionKey = "OpWeave.ReloadCount";

        /// <summary>
        /// The current count — the number of domain loads the Editor
        /// session has run since it started.
        /// </summary>
        internal static int Current => SessionState.GetInt(SessionKey, 0);

        /// <summary>
        /// The [InitializeOnLoad] hook of SPEC §G4: bumps the counter by
        /// one. Called once per domain load by the adapter bootstrap.
        /// </summary>
        internal static void CountDomain() =>
            SessionState.SetInt(SessionKey, Current + 1);
    }
}
