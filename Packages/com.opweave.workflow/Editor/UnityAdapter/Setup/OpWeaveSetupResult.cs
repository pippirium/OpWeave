#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The report of one <see cref="OpWeaveProjectSetup"/> run (SPEC §M.3 item 3):
    /// every item of the setup layout, partitioned into created / skipped /
    /// failed. Skipped means "already there" — the run is idempotent, so a
    /// second run reports every item skipped.
    /// </summary>
    public sealed class OpWeaveSetupResult
    {
        private readonly List<OpWeaveSetupItem> _items;
        private readonly List<OpWeaveSetupItem> _created = new List<OpWeaveSetupItem>();
        private readonly List<OpWeaveSetupItem> _skipped = new List<OpWeaveSetupItem>();
        private readonly List<OpWeaveSetupItem> _failed = new List<OpWeaveSetupItem>();

        internal OpWeaveSetupResult(List<OpWeaveSetupItem> items)
        {
            _items = items;
            foreach (var item in items)
            {
                switch (item.State)
                {
                    case OpWeaveSetupItemState.Created:
                        _created.Add(item);
                        break;
                    case OpWeaveSetupItemState.Skipped:
                        _skipped.Add(item);
                        break;
                    default:
                        _failed.Add(item);
                        break;
                }
            }
        }

        /// <summary>Every layout item, in creation order.</summary>
        public IReadOnlyList<OpWeaveSetupItem> Items => _items;

        /// <summary>The items this run created.</summary>
        public IReadOnlyList<OpWeaveSetupItem> Created => _created;

        /// <summary>The items that already existed and were left untouched.</summary>
        public IReadOnlyList<OpWeaveSetupItem> Skipped => _skipped;

        /// <summary>The items that did not exist and could not be created.</summary>
        public IReadOnlyList<OpWeaveSetupItem> Failed => _failed;
    }
}
