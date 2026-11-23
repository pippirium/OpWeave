#nullable enable

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One entry of an <see cref="OpWeaveSetupResult"/>: a folder or file of the
    /// SPEC §M.3 setup layout and what the run did with it.
    /// </summary>
    public sealed class OpWeaveSetupItem
    {
        internal OpWeaveSetupItem(
            OpWeaveSetupItemKind kind,
            string path,
            OpWeaveSetupItemState state,
            string? error)
        {
            Kind = kind;
            Path = path;
            State = state;
            Error = error;
        }

        /// <summary>Whether the item is a folder or a file.</summary>
        public OpWeaveSetupItemKind Kind { get; }

        /// <summary>
        /// The project-relative path with <c>/</c> separators — the same text the
        /// Console listing prints.
        /// </summary>
        public string Path { get; }

        /// <summary>What the run did with this item.</summary>
        public OpWeaveSetupItemState State { get; }

        /// <summary>
        /// The fixed English failure text when <see cref="State"/> is
        /// <see cref="OpWeaveSetupItemState.Failed"/>; otherwise <c>null</c>.
        /// </summary>
        public string? Error { get; }
    }
}
