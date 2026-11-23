#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>What one <see cref="OpWeaveSetupItem"/> of a SPEC §M.3 setup run
    /// reported.</summary>
    public enum OpWeaveSetupItemState
    {
        /// <summary>The item did not exist and was created by this run.</summary>
        Created,

        /// <summary>The item already existed; the run left it untouched
        /// (SPEC §M.3 item 3: existing files are never overwritten).</summary>
        Skipped,

        /// <summary>The item did not exist and could not be created; the run
        /// kept going and reports the error on the item.</summary>
        Failed,
    }
}
