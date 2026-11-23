#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The startup sequencing contract of SPEC §1.4 / §F4 item 1: after
    /// <c>[InitializeOnLoad]</c>, the first <c>EditorApplication.delayCall</c>
    /// runs this ordered hook list. Hooks are registered with an order key —
    /// <see cref="RunnerStartupOrder"/> — so the Registry rebuild (T-W2.2-07)
    /// always runs before Run recovery (T-W4.3-01) regardless of
    /// registration order, exactly as §F4 item 1 requires ("C3 の Registry
    /// 再構築が終わってから" the unfinished Runs are reconciled).
    /// </summary>
    /// <remarks>
    /// <see cref="RunAll"/> executes hooks in ascending order — stable for
    /// equal keys — and stops at the first throwing hook: a failed Registry
    /// rebuild must not let recovery proceed on an unverified snapshot.
    /// Hooks registered after the delayCall has run never execute; the list
    /// is rebuilt per domain (the managed state dies with the reload, like
    /// the statics that re-register it).
    /// </remarks>
    internal sealed class StartupHookList
    {
        private readonly List<Entry> _entries = new List<Entry>();

        /// <summary>
        /// Adds <paramref name="hook"/> under <paramref name="order"/>; hooks
        /// with the same order keep registration order. <paramref name="name"/>
        /// labels the hook for failure reporting.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="name"/> or <paramref name="hook"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
        internal void Register(int order, string name, Action hook)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException(
                    "The startup hook name must not be null or empty.", nameof(name));
            }
            if (hook is null)
            {
                throw new ArgumentNullException(nameof(hook));
            }
            var entry = new Entry(order, name, hook);
            var index = _entries.Count;
            while (index > 0 && _entries[index - 1].Order > order)
            {
                index--;
            }
            _entries.Insert(index, entry);
        }

        /// <summary>
        /// Runs every hook in ascending order; a throwing hook is logged and
        /// stops the sequence (a failed rebuild must not let later hooks run
        /// on unverified state). Returns the names of the hooks that ran, in
        /// execution order.
        /// </summary>
        internal IReadOnlyList<string> RunAll()
        {
            var ran = new List<string>(_entries.Count);
            foreach (var entry in _entries)
            {
                ran.Add(entry.Name);
                try
                {
                    entry.Hook();
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    break;
                }
            }
            return ran;
        }

        /// <summary>
        /// The registered hook names in the order <see cref="RunAll"/> would
        /// run them, without running anything — the seam that asserts the
        /// SPEC §F4 item 1 sequence (the Registry rebuild before Run recovery)
        /// without forcing a real rebuild.
        /// </summary>
        internal IReadOnlyList<string> RegisteredNames()
        {
            var names = new List<string>(_entries.Count);
            foreach (var entry in _entries)
            {
                names.Add(entry.Name);
            }
            return names;
        }

        private sealed class Entry
        {
            internal Entry(int order, string name, Action hook)
            {
                Order = order;
                Name = name;
                Hook = hook;
            }

            internal int Order { get; }
            internal string Name { get; }
            internal Action Hook { get; }
        }
    }

    /// <summary>
    /// The order keys of SPEC §1.4's startup hook list: Registry rebuild
    /// registers ahead of Run recovery so §F4 item 1's "Registry 再構築が
    /// 終わってから" holds for any registration order.
    /// </summary>
    internal static class RunnerStartupOrder
    {
        /// <summary>The C3 Registry rebuild slot (T-W2.2-07).</summary>
        internal const int RegistryRebuild = 0;

        /// <summary>The §F4 recovery slot (T-W4.3-01) — always after the Registry.</summary>
        internal const int RunRecovery = 100;

        /// <summary>
        /// The SPEC §F8 retention slot (T-W4.4-01) — after recovery, so an
        /// unfinished Run is re-attached before finished-Run candidates are
        /// selected (a still-unfinished Run is never a candidate).
        /// </summary>
        internal const int RunRetention = 200;
    }
}
