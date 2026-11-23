#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;

namespace OpWeave.Operations
{
    /// <summary>
    /// The SPEC §F5 reconciliation evidence of <c>unity.package.remove</c>:
    /// what Preflight persists before <c>StepStarted</c>, and how Reconcile
    /// decides <c>Completed</c> / <c>NotApplied</c> / <c>Unknown</c> against
    /// the persisted snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The §F5 evidence row: Preflight records the package name, the
    /// manifest dependency entry and the resolved source/version — the
    /// full resolved commit SHA for a Git package — of the target. The
    /// snapshot carries every trace the name left in the three channels
    /// (<see cref="PackageInstallView"/>): the manifest dependency keyed
    /// by the name, the lockfile entry keyed by the name and the
    /// registered package list's entries for the name, plus the
    /// per-channel read flags, because a channel the capture could not
    /// read records nothing and proves nothing.
    /// </para>
    /// <para>
    /// <c>Completed</c> requires the package absent from every channel —
    /// no manifest entry, no lockfile entry, no registered package —
    /// <i>and</i> a snapshot that proves the package existed before the
    /// step ran (any recorded trace; SPEC §F5 audit note "実行前に存在した
    /// ことをスナップショットで示し"). A recorded empty pre-state is
    /// proven absence only when every channel was read at capture; on
    /// recorded absence plus current absence the verdict is
    /// <c>NotApplied</c> — the state equals the snapshot and re-running
    /// <c>Client.Remove</c> on an absent package only produces the UPM
    /// error, never a mis-delete. A recorded presence must establish one
    /// complete, internally consistent identity across the three
    /// channels before it proves anything — a partial, ambiguous or
    /// cross-channel-inconsistent record proves nothing. Every other
    /// state is <c>Unknown</c>: a package present that the snapshot did
    /// not record (a possible pre-existing or replaced same-name
    /// artifact — re-executing could delete an artifact this run never
    /// targeted), a package present whose manifest, lockfile or
    /// registered entry differs from the snapshot (modified by another
    /// operation, or partially removed), manifest/registered
    /// disagreement, an unread channel, an inconsistent recorded state
    /// or a malformed snapshot. The verdict never mutates package state
    /// — reconciliation only reads (SPEC §F5).
    /// </para>
    /// <para>
    /// <c>NotApplied</c> requires the name-keyed state to equal the
    /// snapshot exactly — same manifest spec, same lockfile entry, and
    /// registered entries equal in name, version, source, dependency
    /// text, resolved commit and requested revision — with the recorded
    /// state itself a complete, consistent identity: for a registry
    /// entry the manifest spec, the lockfile version and the registered
    /// version agree; for a Git-source entry both sides must carry a
    /// full commit SHA (SPEC §F5 row: "Git なら完全なコミット SHA").
    /// Only then is the removal provably unapplied, and re-execution
    /// can only remove the recorded artifact, never a different one.
    /// </para>
    /// </remarks>
    internal static class PackageRemoveEvidence
    {
        // Fixed English diagnostic fragments for the Unknown verdict —
        // none of them carries the package name or any candidate text
        // (SPEC §G8 item 5: diagnostics never echo).
        internal const string SnapshotMalformed =
            "The persisted reconciliation snapshot is missing or malformed.";
        internal const string EvidenceUnreadable =
            "The manifest, the lockfile or the registered package list " +
            "could not be read, so the removal outcome cannot be proven.";
        internal const string RecordedStateIncomplete =
            "The persisted snapshot was recorded while a package-state " +
            "channel could not be read, so the recorded state cannot be proven.";
        internal const string RecordedStateInconsistent =
            "The persisted snapshot's manifest, lockfile and registered " +
            "entries do not agree on one installed package identity.";
        internal const string Partial =
            "The manifest, the lockfile and the registered package list " +
            "disagree about the package; it may have been partially removed.";
        internal const string Changed =
            "The installed package differs from the persisted snapshot; " +
            "it may have been modified or replaced by another operation.";
        internal const string Appeared =
            "A package is installed that the persisted snapshot recorded " +
            "as absent before the step ran.";

        /// <summary>The recorded pre-execution state of the target,
        /// restricted to the name's own entries in each channel.</summary>
        private sealed class RecordedView
        {
            internal JObject Manifest = new JObject();
            internal JObject Lock = new JObject();
            internal List<ResolvedPackageView> Registered =
                new List<ResolvedPackageView>();

            // True only when the preflight capture could read all three
            // channels — an empty RecordedView without it is an unproven
            // pre-state, never recorded absence (SPEC §F5).
            internal bool AllRead;

            internal bool IsEmpty =>
                Manifest.Count == 0 && Lock.Count == 0 && Registered.Count == 0;
        }

        /// <summary>The §F5 preflight snapshot: the target name plus the
        /// recorded pre-execution state of every channel, restricted to
        /// the name's entries.</summary>
        internal static JsonValue BuildSnapshot(string name, PackageInstallView view)
        {
            if (name is null)
            {
                throw new ArgumentNullException(nameof(name));
            }
            if (view is null)
            {
                throw new ArgumentNullException(nameof(view));
            }
            var obj = new JObject
            {
                ["v"] = 1,
                ["name"] = name,
                ["manifest"] = NameEntry(view.ManifestDependencies, name),
                ["lock"] = NameEntry(view.LockDependencies, name),
                ["registered"] =
                    ToJArray(MatchingRegistered(name, view.Registered)),
                ["read"] = new JObject
                {
                    ["manifest"] = view.ManifestRead,
                    ["lock"] = view.LockRead,
                    ["registered"] = view.RegisteredRead,
                },
            };
            return JsonValue.From(obj);
        }

        /// <summary>
        /// The §F5 verdict: <paramref name="snapshot"/> is the persisted
        /// preflight evidence, <paramref name="view"/> the current
        /// install state.
        /// </summary>
        // The §F5 removal row as one explicit fail-closed decision order
        // — never a Completed or NotApplied by default or by
        // fall-through (SPEC §F5, §G8.1):
        //
        //   1. Evidence completeness: a channel that could not be read
        //      — at preflight capture or at the current observation —
        //      holds no evidence at all, so neither presence nor
        //      absence can be proven from it → Unknown.
        //   2. Preflight identity: the recorded state must be either a
        //      proven-empty pre-state or exactly one complete,
        //      internally consistent installed identity — one manifest
        //      entry, one lockfile entry and one registered package
        //      agreeing with each other (for Git the lock hash and the
        //      registered commit are the persisted full commit SHA).
        //      A partial, ambiguous or cross-channel-inconsistent
        //      record proves nothing → Unknown.
        //   3. Current state, across all channels AND against the
        //      recorded identity: absent in every channel is Completed
        //      when the snapshot proved the package existed (the
        //      removal's effect) and NotApplied when absence was proven
        //      at capture too — both are states re-execution cannot
        //      worsen; present and exactly equal to the recorded
        //      identity is NotApplied — the removal provably did not
        //      apply and re-execution removes the recorded artifact
        //      alone; everything else — presence after recorded
        //      absence, a partial state, a changed or replaced artifact
        //      — is Unknown, never an automatic re-remove.
        internal static ReconcileResult Judge(JsonValue snapshot, PackageInstallView view)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (view is null)
            {
                throw new ArgumentNullException(nameof(view));
            }
            if (!TryReadSnapshot(snapshot, out var name, out var recorded) ||
                name is null ||
                recorded is null)
            {
                return Unknown(SnapshotMalformed);
            }

            // 1. Evidence completeness: a channel that could not be
            //    read — at preflight capture or now — holds no
            //    evidence at all (SPEC §F5).
            if (!recorded.AllRead)
            {
                return Unknown(RecordedStateIncomplete);
            }
            if (!view.AllChannelsRead)
            {
                return Unknown(EvidenceUnreadable);
            }

            // 2. Preflight identity: the recorded state must be a
            //    proven-empty pre-state or establish one complete,
            //    internally consistent installed identity — a partial,
            //    ambiguous or cross-channel-inconsistent record proves
            //    nothing.
            if (!RecordedIdentityConsistent(name, recorded))
            {
                return Unknown(RecordedStateInconsistent);
            }

            // Every channel was read at both captures, so the
            // name-keyed current state is complete evidence.
            var mNow = NameEntry(view.ManifestDependencies, name);
            var lNow = NameEntry(view.LockDependencies, name);
            var rNow = MatchingRegistered(name, view.Registered);

            // 3. Current state. Absent in every channel: Completed when
            //    the recorded identity proves the package existed
            //    before the step ran; NotApplied when the recorded
            //    state proved it absent — the snapshot and the current
            //    state agree, and re-execution fails at the UPM
            //    boundary while deleting nothing.
            if (mNow.Count == 0 && lNow.Count == 0 && rNow.Count == 0)
            {
                if (recorded.IsEmpty)
                {
                    return new ReconcileResult.NotApplied();
                }
                return new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(0));
            }

            // The snapshot recorded proven absence: the package was not
            // there when the step ran, so its presence now cannot be
            // attributed to the removal — a possible pre-existing
            // artifact or a same-name replacement an automatic re-run
            // must never delete (SPEC §F5 same-name rule).
            if (recorded.IsEmpty)
            {
                return Unknown(Appeared);
            }
            // Present, and every channel still shows exactly the
            // recorded identity — manifest spec, lockfile entry and
            // registered package all equal the snapshot: the removal
            // provably did not apply, and re-execution removes the
            // recorded artifact alone (safe to retry).
            if (SameRecordedState(recorded, mNow, lNow, rNow))
            {
                return new ReconcileResult.NotApplied();
            }
            // Present in only some channels: manifest, lockfile and
            // registered list disagree — a possibly partial removal
            // (SPEC §F5 row's own Unknown example).
            if (mNow.Count == 0 || lNow.Count == 0 || rNow.Count == 0)
            {
                return Unknown(Partial);
            }
            // Present in every channel but not the recorded artifact:
            // the manifest spec, the lockfile entry or the registered
            // identity changed since the snapshot — another operation
            // touched it, or a different same-name package took its
            // place; the removal's effect cannot be proven either way.
            return Unknown(Changed);
        }

        // Step 2's recorded-identity test. A proven-empty pre-state is
        // complete evidence of its own — every channel was read and
        // none held the name. A recorded presence must be exactly one
        // internally consistent installed identity: one manifest entry,
        // one lockfile entry and one registered package, all for the
        // name and agreeing with each other — for registry the lock and
        // registered sources are "registry" and the manifest spec, the
        // lockfile version and the registered version are the same; for
        // Git the lock and registered sources are "git", the manifest
        // spec, the lockfile version text and the registered dependency
        // text are the same request, and the lock hash and the
        // registered resolved commit are one full commit SHA (SPEC §F5
        // row: "Git なら完全なコミット SHA"). A snapshot recorded while
        // the channels disagreed — or a partial one — never established
        // the artifact's identity, so nothing downstream can attribute
        // state to it.
        private static bool RecordedIdentityConsistent(
            string name, RecordedView recorded)
        {
            if (recorded.IsEmpty)
            {
                return true;
            }
            if (recorded.Manifest.Count != 1 ||
                recorded.Lock.Count != 1 ||
                recorded.Registered.Count != 1)
            {
                return false;
            }
            if (recorded.Manifest[name] is not JValue
                    { Type: JTokenType.String } manifestSpec ||
                recorded.Lock[name] is not JObject lockEntry ||
                lockEntry["version"] is not JValue
                    { Type: JTokenType.String } lockVersion ||
                lockEntry["source"] is not JValue
                    { Type: JTokenType.String } lockSource)
            {
                return false;
            }
            var registered = recorded.Registered[0];
            if (!string.Equals(
                    registered.Name, name, StringComparison.Ordinal))
            {
                return false;
            }
            var spec = (string)manifestSpec.Value!;
            var version = (string)lockVersion.Value!;
            var source = (string)lockSource.Value!;
            if (string.Equals(
                    source, "registry", StringComparison.Ordinal) &&
                string.Equals(
                    registered.Source, "registry", StringComparison.Ordinal))
            {
                return
                    string.Equals(version, spec, StringComparison.Ordinal) &&
                    string.Equals(
                        registered.Version, spec, StringComparison.Ordinal);
            }
            if (string.Equals(
                    source, "git", StringComparison.Ordinal) &&
                string.Equals(
                    registered.Source, "git", StringComparison.Ordinal))
            {
                var hash = lockEntry["hash"] is JValue
                    { Type: JTokenType.String } lockHash
                    ? (string)lockHash.Value!
                    : null;
                return
                    string.Equals(version, spec, StringComparison.Ordinal) &&
                    string.Equals(
                        registered.Dependency, spec,
                        StringComparison.Ordinal) &&
                    PackageAddSource.IsFullCommitSha(hash) &&
                    string.Equals(
                        registered.ResolvedCommit, hash,
                        StringComparison.Ordinal);
            }
            return false;
        }

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        // ---------- snapshot read-back ----------

        private static bool TryReadSnapshot(
            JsonValue snapshot,
            out string? name,
            out RecordedView? recorded)
        {
            name = null;
            recorded = null;
            if (snapshot.ToToken() is not JObject obj ||
                obj["name"] is not JValue { Type: JTokenType.String } text ||
                ((string)text.Value!).Length == 0)
            {
                return false;
            }
            name = (string)text.Value!;
            recorded = new RecordedView();
            if (obj["manifest"] is JObject manifest)
            {
                recorded.Manifest = manifest;
            }
            if (obj["lock"] is JObject lockDeps)
            {
                recorded.Lock = lockDeps;
            }
            if (obj["registered"] is JArray registered)
            {
                foreach (var entry in registered)
                {
                    if (entry is JObject item &&
                        item["name"] is JValue { Type: JTokenType.String } entryName)
                    {
                        recorded.Registered.Add(new ResolvedPackageView(
                            (string)entryName.Value!,
                            Text(item["version"]),
                            Text(item["source"]) ?? string.Empty,
                            Text(item["dependency"]),
                            Text(item["resolvedCommit"]),
                            Text(item["requestedRevision"])));
                    }
                }
            }
            // A snapshot without readable-channel flags cannot prove its
            // recorded state — missing or malformed flags fail closed.
            recorded.AllRead =
                obj["read"] is JObject read &&
                Flag(read["manifest"]) &&
                Flag(read["lock"]) &&
                Flag(read["registered"]);
            return true;

            static bool Flag(JToken? token) =>
                token is JValue { Type: JTokenType.Boolean } value &&
                (bool)value.Value!;

            static string? Text(JToken? token) =>
                token is JValue { Type: JTokenType.String } value
                    ? (string)value.Value!
                    : null;
        }

        // ---------- name-keyed matching over the three channels ----------

        // The target is a bare package name, so every channel matches by
        // the name key alone — the manifest dependency, the lockfile
        // entry and each registered package named exactly this.
        private static JObject NameEntry(JObject? dependencies, string name)
        {
            var obj = new JObject();
            if (dependencies is not null &&
                dependencies[name] is { } value)
            {
                obj[name] = value;
            }
            return obj;
        }

        private static List<ResolvedPackageView> MatchingRegistered(
            string name, IReadOnlyList<ResolvedPackageView> registered)
        {
            var matches = new List<ResolvedPackageView>();
            foreach (var entry in registered)
            {
                if (string.Equals(entry.Name, name, StringComparison.Ordinal))
                {
                    matches.Add(entry);
                }
            }
            return matches;
        }

        // The "same as snapshot" half of the §F5 NotApplied row: every
        // channel's recorded entry for the name must equal the current
        // one — the manifest spec verbatim, the lockfile entry verbatim
        // (version, resolved hash, source and every other field), and
        // each registered package equal in name, version, source,
        // dependency text, resolved commit and requested revision. For a
        // Git-source entry both sides must carry a full commit SHA:
        // without it the artifact identity is unprovable and "same" is
        // not proven (SPEC §F5 row).
        private static bool SameRecordedState(
            RecordedView recorded,
            JObject manifestNow,
            JObject lockNow,
            IReadOnlyList<ResolvedPackageView> registeredNow)
        {
            if (!JToken.DeepEquals(recorded.Manifest, manifestNow) ||
                !JToken.DeepEquals(recorded.Lock, lockNow))
            {
                return false;
            }
            if (recorded.Registered.Count != registeredNow.Count)
            {
                return false;
            }
            for (var i = 0; i < recorded.Registered.Count; i++)
            {
                var a = recorded.Registered[i];
                var b = registeredNow[i];
                if (!SameRegisteredEntry(a, b))
                {
                    return false;
                }
                if ((string.Equals(a.Source, "git", StringComparison.Ordinal) ||
                     string.Equals(b.Source, "git", StringComparison.Ordinal)) &&
                    (!PackageAddSource.IsFullCommitSha(a.ResolvedCommit) ||
                     !PackageAddSource.IsFullCommitSha(b.ResolvedCommit)))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool SameRegisteredEntry(
            ResolvedPackageView a, ResolvedPackageView b) =>
            string.Equals(a.Name, b.Name, StringComparison.Ordinal) &&
            string.Equals(a.Version, b.Version, StringComparison.Ordinal) &&
            string.Equals(a.Source, b.Source, StringComparison.Ordinal) &&
            string.Equals(a.Dependency, b.Dependency, StringComparison.Ordinal) &&
            string.Equals(a.ResolvedCommit, b.ResolvedCommit, StringComparison.Ordinal) &&
            string.Equals(a.RequestedRevision, b.RequestedRevision, StringComparison.Ordinal);

        // ---------- snapshot writers ----------

        private static JArray ToJArray(IReadOnlyList<ResolvedPackageView> entries)
        {
            var array = new JArray();
            foreach (var entry in entries)
            {
                var item = new JObject
                {
                    ["name"] = entry.Name,
                    ["source"] = entry.Source,
                };
                if (entry.Version is not null)
                {
                    item["version"] = entry.Version;
                }
                if (entry.Dependency is not null)
                {
                    item["dependency"] = entry.Dependency;
                }
                if (entry.ResolvedCommit is not null)
                {
                    item["resolvedCommit"] = entry.ResolvedCommit;
                }
                if (entry.RequestedRevision is not null)
                {
                    item["requestedRevision"] = entry.RequestedRevision;
                }
                array.Add(item);
            }
            return array;
        }
    }
}
