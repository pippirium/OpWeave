#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Editor-session proof that a live prefab-contents editing root is the
    /// object <em>one specific acquisition</em> opened — the missing fact behind
    /// SPEC §E6 item 4's "release the Run's own resource". The journaled
    /// <c>(kind, data)</c> pair names the Prefab, never who opened its contents,
    /// so a release keyed on it alone would unload whatever session of that asset
    /// happened to be open: an unrelated <c>LoadPrefabContents</c> of another
    /// tool, of a later step, or of the user's own code, together with its
    /// unsaved modifications. The acquisition is journaled write-ahead
    /// <em>before</em> the contents load, so an interruption or a failed load
    /// leaves an open acquisition with no root of its own at all, and a release
    /// then has nothing of its own to unload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What one entry says.</b> <c>scope → instance id</c>, where the scope is
    /// the <see cref="OpWeave.Core.Runtime.RunResourceScope"/> text of the
    /// acquisition (its Run id and the step of its own
    /// <c>resource_acquired</c>) and the guid names the asset the root belongs
    /// to. The bind happens right after a successful
    /// <c>LoadPrefabContents</c>, so an acquisition that never got that far has
    /// no entry — which is the honest answer: it never owned a root.
    /// </para>
    /// <para>
    /// <b>Why <see cref="SessionState"/>.</b> The binding must outlive a domain
    /// reload — the whole point of the §G6 pair is that the preview scene and its
    /// unsaved modifications survive one, and a recovery step run after the
    /// reload still has to release the contents of its own acquisition — and it
    /// must NOT outlive an Editor restart, because a restart discards the preview
    /// scene along with every session handle (<c>SessionState</c> is
    /// Editor-session state that is never persisted to disk). An instance id is
    /// stored rather than a reference because a managed reference would not
    /// survive the reload at all (SPEC §C2 item 4 forbids instance state on the
    /// Operation too).
    /// </para>
    /// <para>
    /// <b>Reading an entry is never blind.</b> The id is resolved again and
    /// re-validated as a live, non-stage, preview-scene root of that very asset
    /// through <see cref="PrefabContentsResources.FindEditingRoots"/>; an id that
    /// resolves to nothing, to a destroyed object, or to an object that is no
    /// longer such a root is dropped, and the caller fails safe. No binding
    /// therefore ever authorises unloading an object other than the one the
    /// acquisition opened, and a lost binding costs a reported refusal, never
    /// somebody else's work.
    /// </para>
    /// <para>
    /// <b>The second table: what a release proved.</b> A binding is dropped when
    /// its object was unloaded, so the absent binding of an interrupted release
    /// says two opposite things — the contents were never loaded, or they were
    /// already unloaded and only their <c>resource_released</c> record is still
    /// missing. Nothing in the binding itself tells them apart, and the second
    /// reading is exactly the window the Run-end cleanup leaves between one
    /// physical release and its own record. The release-evidence table therefore
    /// records, per acquisition scope, the object a successful unload removed —
    /// written BEFORE the binding goes, so there is never a moment where the
    /// binding is gone with nothing to say why. An absent evidence entry is the
    /// honest answer to "was this scope's own root unloaded?": never proven, so
    /// the acquisition stays open for review rather than a guessed release.
    /// Both tables are <c>SessionState</c>, so they live and die together — after
    /// an Editor restart neither exists, and no contents root survived it
    /// either, which is the case <see cref="PrefabContentsResources.Release"/>
    /// already closes without unloading anything.
    /// </para>
    /// </remarks>
    internal static class PrefabContentsOwnership
    {
        // One SessionState entry holds the whole table: the bindings are a
        // handful of lines while a Run is open, and one key means no per-scope
        // keys to lose track of. The separators are the ASCII record/unit
        // separators, which cannot occur in a runId, a guid or an integer.
        private const string StoreKey = "opweave.prefab-contents.ownership.v1";

        // The release-evidence table of the same shape and the same lifetime.
        private const string ReleasedStoreKey =
            "opweave.prefab-contents.ownership.released.v1";
        private const char RecordSeparator = '\u001e';
        private const char FieldSeparator = '\u001f';

        /// <summary>
        /// Records that the acquisition of <paramref name="scope"/> opened
        /// <paramref name="root"/>; called once the contents really exist, so
        /// the entry never claims a root the load did not produce. A previous
        /// entry of the same scope is replaced — the same acquisition of the
        /// same Prefab owns at most one live root.
        /// </summary>
        internal static void Bind(string scope, string guid, GameObject root)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(guid) || root is null)
            {
                return;
            }
            var entries = Read(StoreKey);
            entries.RemoveAll(entry => Matches(entry, scope!, guid));
            entries.Add(new Entry(scope!, guid, SessionIdOf(root)));
            Write(StoreKey, entries);
        }

        /// <summary>
        /// The live editing root of <paramref name="assetPath"/> that the
        /// acquisition of <paramref name="scope"/> proved it opened, or
        /// <see langword="null"/> when nothing proves it: no entry (the contents
        /// were never loaded, the load failed, the Editor restarted, the binding
        /// was otherwise lost), an id that no longer resolves, or an id that
        /// resolves to something that is not an editing root of that asset. The
        /// caller must then treat every open session of the asset as somebody
        /// else's — SPEC §E6 item 4 releases only the Run's own resource.
        /// </summary>
        internal static GameObject? FindOwned(
            string scope, string guid, string assetPath)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(guid) ||
                string.IsNullOrEmpty(assetPath))
            {
                return null;
            }
            var entries = Read(StoreKey);
            var index = entries.FindIndex(
                entry => Matches(entry, scope!, guid));
            if (index < 0)
            {
                return null;
            }
            var instanceId = entries[index].InstanceId;
            var owned = SessionIdToObject(instanceId) as GameObject;
            if (owned is not null && IsEditingRootOf(owned!, assetPath!, instanceId))
            {
                return owned;
            }
            // Dead, destroyed or repurposed: the entry proves nothing any more,
            // so it goes — and the caller fails safe instead of unloading a
            // stranger that happens to share the asset path. No release evidence
            // is recorded here: this drop says the object is gone, never that
            // this acquisition unloaded it.
            entries.RemoveAt(index);
            Write(StoreKey, entries);
            return null;
        }

        /// <summary>
        /// The session-scoped identity of <paramref name="obj"/> in this
        /// Editor generation's own idiom (SPEC §T.4 L-04): on 6000.5+ the
        /// raw bits of <see cref="EntityId"/>, below 6000.5 the signed
        /// InstanceID — both carried in a <see cref="long"/> so the record
        /// shape, the comparisons and the SessionState text are the same
        /// on every generation. The InstanceID is kept signed rather than
        /// normalized into <see cref="SessionHandle"/>'s unsigned key
        /// space, so a stored or reported id stays numerically identical
        /// to the one this generation's identity API reports. Whatever a
        /// record stores is only ever compared with, or resolved through,
        /// the same API on the same editor.
        /// </summary>
        internal static long SessionIdOf(UnityEngine.Object obj)
        {
#if UNITY_6000_5_OR_NEWER
            return unchecked((long)EntityId.ToULong(obj.GetEntityId()));
#else
#pragma warning disable CS0618 // InstanceID is the identity below 6000.5; warning-obsolete on 6000.3–6000.4, compile error only on 6000.5+ (L-04).
            return obj.GetInstanceID();
#pragma warning restore CS0618
#endif
        }

        /// <summary>
        /// Resolves a session id recorded by <see cref="SessionIdOf"/> to
        /// the live object through this generation's identity API, or
        /// <see langword="null"/> when no live object carries it — or when
        /// the value cannot be an id of this generation at all (below
        /// 6000.5 an InstanceID is <see cref="int"/>-ranged, so a wider
        /// value is never one).
        /// </summary>
        internal static UnityEngine.Object? SessionIdToObject(long sessionId)
        {
#if UNITY_6000_5_OR_NEWER
            return EditorUtility.EntityIdToObject(
                EntityId.FromULong(unchecked((ulong)sessionId)));
#else
#pragma warning disable CS0618
            return sessionId < int.MinValue || sessionId > int.MaxValue
                ? null
                : EditorUtility.InstanceIDToObject(unchecked((int)sessionId));
#pragma warning restore CS0618
#endif
        }

        /// <summary>
        /// Whether the live editing root of session id
        /// <paramref name="instanceId"/> is a root <em>this Run</em> opened — one
        /// of its acquisitions bound it when its load produced it, whichever of
        /// the Run's steps that acquisition belongs to. This is the question
        /// <c>unity.prefab.save_contents</c> must answer before it writes: the
        /// step that saves is never the step that opened, so the check spans the
        /// Run's scopes, while a session no scope of this Run ever bound belongs
        /// to another owner and may neither be written over the asset nor
        /// unloaded here.
        /// </summary>
        /// <param name="scope">
        /// The acquisition scope of the step asking — its Run and its own step,
        /// in the <see cref="OpWeave.Core.Runtime.RunResourceScope"/> form. Only
        /// its Run half is used.
        /// </param>
        /// <param name="instanceId">
        /// The session id of a root the caller already proved is an editing root
        /// of the asset in question.
        /// </param>
        internal static bool IsBoundInRun(string scope, long instanceId)
        {
            var runPrefix = RunPrefixOf(scope);
            if (runPrefix is null)
            {
                return false;
            }
            var entries = Read(StoreKey);
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].InstanceId == instanceId &&
                    entries[i].Scope!.StartsWith(runPrefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // The "runId:" half of an acquisition scope. A SPEC §0.4 runId carries
        // no ':', so the first separator ends it and every scope of that Run —
        // and only those — starts with its text; a scope with no separator names
        // no Run, so it matches nothing.
        private static string? RunPrefixOf(string scope)
        {
            if (string.IsNullOrEmpty(scope))
            {
                return null;
            }
            var separator = scope!.IndexOf(':');
            return separator < 0
                ? null
                : scope.Substring(0, separator + 1);
        }

        /// <summary>
        /// Drops the binding of one acquisition — called when it released the
        /// root, so a released (or re-acquired) scope never keeps pointing at an
        /// object it no longer holds.
        /// </summary>
        internal static void Unbind(string scope, string guid)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(guid))
            {
                return;
            }
            var entries = Read(StoreKey);
            if (entries.RemoveAll(entry => Matches(entry, scope!, guid)) == 0)
            {
                return;
            }
            Write(StoreKey, entries);
        }

        /// <summary>
        /// Drops every binding that names the object of session id
        /// <paramref name="instanceId"/> — the release of an Operation that
        /// unloaded the object it was handed through its Ref and therefore knows
        /// the object, not the scope that opened it. The id is taken as a number
        /// because an unloaded contents object is destroyed by that very unload
        /// and answers no member afterwards.
        /// <para>
        /// Every binding this removes belonged to an object this very call
        /// released, so each loses it with its evidence recorded first: the
        /// acquisition keeps a durable proof that its own root is gone, which is
        /// what lets the recovery of an interrupted release close that
        /// acquisition's pairing instead of refusing it forever.
        /// </para>
        /// </summary>
        internal static void UnbindObject(long instanceId)
        {
            var entries = Read(StoreKey);
            var removed = entries.FindAll(entry => entry.InstanceId == instanceId);
            if (removed.Count == 0)
            {
                return;
            }
            entries.RemoveAll(entry => entry.InstanceId == instanceId);
            Write(StoreKey, entries);
            for (var i = 0; i < removed.Count; i++)
            {
                Record(
                    ReleasedStoreKey,
                    new Entry(
                        removed[i].Scope, removed[i].Guid, instanceId));
            }
        }

        /// <summary>
        /// Records that the acquisition of <paramref name="scope"/> unloaded the
        /// root <paramref name="instanceId"/> that its own load had bound —
        /// called once the unload really happened and BEFORE
        /// <see cref="Unbind"/>, so the binding is never gone without the proof
        /// of why. One entry per scope and asset: the newest release is what the
        /// scope last proved, and an older object of the same acquisition is
        /// already destroyed by then.
        /// </summary>
        internal static void RecordUnloaded(string scope, string guid, long instanceId)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(guid))
            {
                return;
            }
            Record(ReleasedStoreKey, new Entry(scope!, guid!, instanceId));
        }

        /// <summary>
        /// The session id of the object the acquisition of
        /// <paramref name="scope"/> is <em>proved</em> to have unloaded, or
        /// <see langword="null"/> when nothing proves it — the contents were
        /// never loaded, the load failed, the unload failed, the evidence was
        /// lost, or the Editor restarted (which took the contents with it).
        /// Never a statement about any object other than the one this scope
        /// itself opened.
        /// </summary>
        internal static long? UnloadedOf(string scope, string guid)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(guid))
            {
                return null;
            }
            var entries = Read(ReleasedStoreKey);
            var index = entries.FindIndex(entry => Matches(entry, scope!, guid));
            return index < 0 ? null : new long?(entries[index].InstanceId);
        }

        // The candidate rule is PrefabContentsResources' own enumeration — a
        // preview-scene root of this asset, never a Prefab Mode stage — so the
        // binding can never authorise an object the release would not otherwise
        // have been allowed to consider at all.
        private static bool IsEditingRootOf(
            GameObject candidate, string assetPath, long instanceId)
        {
            var roots = PrefabContentsResources.FindEditingRoots(assetPath);
            for (var i = 0; i < roots.Count; i++)
            {
                if (SessionIdOf(roots[i]) == instanceId)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool Matches(Entry entry, string scope, string guid) =>
            string.Equals(entry.Scope, scope, StringComparison.Ordinal) &&
            string.Equals(entry.Guid, guid, StringComparison.Ordinal);

        // One table read. Both tables share the record shape and the same rule
        // for an unreadable record: it is dropped, never trusted.
        private static List<Entry> Read(string storeKey)
        {
            var entries = new List<Entry>();
            var text = SessionState.GetString(storeKey, string.Empty);
            if (text.Length == 0)
            {
                return entries;
            }
            foreach (var record in text.Split(RecordSeparator))
            {
                if (record.Length == 0)
                {
                    continue;
                }
                var fields = record.Split(FieldSeparator);
                if (fields.Length != 3 ||
                    fields[0].Length == 0 ||
                    fields[1].Length == 0 ||
                    !long.TryParse(
                        fields[2], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var instanceId))
                {
                    continue;
                }
                entries.Add(new Entry(fields[0], fields[1], instanceId));
            }
            return entries;
        }

        private static void Write(string storeKey, List<Entry> entries)
        {
            if (entries.Count == 0)
            {
                SessionState.EraseString(storeKey);
                return;
            }
            var text = new StringBuilder();
            foreach (var entry in entries)
            {
                text.Append(RecordSeparator)
                    .Append(entry.Scope).Append(FieldSeparator)
                    .Append(entry.Guid).Append(FieldSeparator)
                    .Append(entry.InstanceId.ToString(CultureInfo.InvariantCulture));
            }
            SessionState.SetString(storeKey, text.ToString());
        }

        // One table's entry of one scope and asset replaced by
        // <paramref name="entry"/>: the table states what the acquisition last
        // proved, never a history of the objects it held.
        private static void Record(string storeKey, Entry entry)
        {
            var entries = Read(storeKey);
            entries.RemoveAll(existing => Matches(existing, entry.Scope, entry.Guid));
            entries.Add(entry);
            Write(storeKey, entries);
        }

        private sealed class Entry
        {
            internal Entry(string scope, string guid, long instanceId)
            {
                Scope = scope;
                Guid = guid;
                InstanceId = instanceId;
            }

            internal string Scope { get; }
            internal string Guid { get; }
            internal long InstanceId { get; }
        }
    }
}
