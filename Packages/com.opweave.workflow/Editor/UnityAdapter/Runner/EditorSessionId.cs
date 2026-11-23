#nullable enable

using System;
using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Editor session identifier of SPEC §F2/§E2 item 3/§F4 item 4: a GUID
    /// persisted in <see cref="SessionState"/>, which lives exactly as long as
    /// the Editor process — surviving every domain reload and reset when the
    /// Editor restarts, so a journal <c>session</c> that differs from the
    /// current value proves an Editor restart (SPEC §F4 item 4).
    /// </summary>
    internal static class EditorSessionId
    {
        /// <summary>The <c>OpWeave.*</c> SessionState key holding the GUID.</summary>
        internal const string SessionStateKey = "OpWeave.EditorSessionId";

        /// <summary>
        /// The current session GUID — read from <see cref="SessionState"/>,
        /// created and stored on the first call of the Editor session.
        /// </summary>
        internal static string GetOrCreate()
        {
            var existing = SessionState.GetString(SessionStateKey, "");
            if (!string.IsNullOrEmpty(existing))
            {
                return existing;
            }
            var created = Guid.NewGuid().ToString("N");
            SessionState.SetString(SessionStateKey, created);
            return created;
        }
    }
}
