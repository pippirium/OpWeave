#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The one place the SPEC §F7 change element is written and read:
    /// <c>{step, attempt, nodeId, action, target: {kind, path, guid?, globalId?}, undoable}</c>
    /// with the action in §0.4 snake_case and <c>guid</c>/<c>globalId</c> omitted
    /// rather than written null (§A0.1 keeps an absent identifier distinct from a
    /// null one). Every writer of the element uses it — the run loop's §F2
    /// <c>changes</c> members, the harness's journal of one Operation, and the
    /// <c>changes.json</c> document — and every reader of one element reads it
    /// field by field, so a mistyped member is a readable failure instead of a
    /// conversion exception on the step path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the element carries its attempt.</b> A §F2 record's envelope already
    /// names the attempt, but the <c>changes.json</c> element has no envelope, and
    /// §F7 keeps two attempts' identical changes as two elements. One shape for
    /// both therefore writes <c>attempt</c> inside the element; a reader of a
    /// journal element written before that member existed (§F2's optional-member
    /// rule) falls back to the envelope's attempt — see <see cref="TryReadElement"/>.
    /// </para>
    /// </remarks>
    internal static class ChangeRecordJson
    {
        private const string StepMember = "step";
        private const string AttemptMember = "attempt";
        private const string NodeIdMember = "nodeId";
        private const string ActionMember = "action";
        private const string TargetMember = "target";
        private const string KindMember = "kind";
        private const string PathMember = "path";
        private const string GuidMember = "guid";
        private const string GlobalIdMember = "globalId";
        private const string UndoableMember = "undoable";

        // The §0.4 snake_case action members of SPEC §F7, in ChangeAction order.
        private const string CreatedAction = "created";
        private const string ModifiedAction = "modified";
        private const string DeletedAction = "deleted";
        private const string MovedAction = "moved";
        private const string SavedAction = "saved";

        /// <summary>
        /// The §F7 array of <paramref name="records"/> in call order — the shape
        /// a §F2 <c>changes</c> member carries.
        /// </summary>
        /// <exception cref="System.ArgumentNullException">
        /// <paramref name="records"/> is null.
        /// </exception>
        internal static JArray Write(IReadOnlyList<ChangeRecord> records)
        {
            if (records is null)
            {
                throw new System.ArgumentNullException(nameof(records));
            }
            var changes = new JArray();
            for (var i = 0; i < records.Count; i++)
            {
                changes.Add(Element(records[i]));
            }
            return changes;
        }

        /// <summary>One §F7 element as its JSON object.</summary>
        /// <exception cref="System.ArgumentNullException">
        /// <paramref name="record"/> is null.
        /// </exception>
        internal static JObject Element(ChangeRecord record)
        {
            if (record is null)
            {
                throw new System.ArgumentNullException(nameof(record));
            }
            var target = new JObject
            {
                ["kind"] = record.Target.Kind,
                ["path"] = record.Target.Path,
            };
            if (record.Target.Guid is { } guid)
            {
                target["guid"] = guid;
            }
            if (record.Target.GlobalId is { } globalId)
            {
                target["globalId"] = globalId;
            }
            return new JObject
            {
                ["step"] = record.Step,
                ["attempt"] = record.Attempt,
                ["nodeId"] = record.NodeId,
                ["action"] = JsonNaming.ToSnakeCase(record.Action.ToString()),
                ["target"] = target,
                ["undoable"] = record.Undoable,
            };
        }

        /// <summary>
        /// Reads one §F7 element.
        /// </summary>
        /// <param name="token">The element token.</param>
        /// <param name="fallbackAttempt">
        /// The attempt to read for an element written without its own
        /// <c>attempt</c> member — the journal envelope's attempt, which is the
        /// attempt the record belongs to (§F2: a missing member is the pre-decision
        /// form, never a damaged one).
        /// </param>
        /// <param name="source">
        /// What the element came from, in the reader's own words — it goes into the
        /// failure text so the report names the record whose element was unreadable.
        /// </param>
        /// <param name="record">The element, when it read cleanly.</param>
        /// <param name="failure">
        /// Why the element could not be read; null on success. A caller never skips
        /// a failure silently — a partial change list must be reported, not written.
        /// </param>
        /// <returns>True when <paramref name="record"/> was read.</returns>
        internal static bool TryReadElement(
            JToken token, int fallbackAttempt, string source,
            out ChangeRecord? record, out string? failure)
        {
            record = null;
            failure = null;
            if (token is not JObject element ||
                element[StepMember] is not JValue { Value: long step } ||
                element[NodeIdMember] is not JValue { Value: string nodeId } ||
                element[ActionMember] is not JValue { Value: string action } ||
                element[TargetMember] is not JObject target ||
                target[KindMember] is not JValue { Value: string kind } ||
                target[PathMember] is not JValue { Value: string path } ||
                element[UndoableMember] is not JValue { Value: bool undoable } ||
                !TryParseAction(action, out var changeAction))
            {
                failure = $"A change element of {source} is outside the SPEC §F7 shape.";
                return false;
            }
            if (string.IsNullOrEmpty(nodeId) || string.IsNullOrEmpty(kind))
            {
                failure = $"A change element of {source} has an " +
                    "empty nodeId or target kind.";
                return false;
            }
            // An optional identifier: absent or null stays absent (§A0.1 keeps the
            // two distinct), and a member of the wrong type is a malformed element.
            if (!TryOptionalIdentifier(target[GuidMember], out var guid) ||
                !TryOptionalIdentifier(target[GlobalIdMember], out var globalId))
            {
                failure = $"A change element of {source} has a " +
                    "target identifier that is not a string.";
                return false;
            }
            if (step < int.MinValue || step > int.MaxValue)
            {
                failure = $"A change element of {source} has a " +
                    "step index outside the Plan step range.";
                return false;
            }
            // The element's own attempt when it carries one (every element written
            // since the 2026-11-08 decision); an older element reads as the attempt
            // of the record that carried it.
            var attempt = fallbackAttempt;
            if (element[AttemptMember] is { } attemptToken &&
                attemptToken.Type != JTokenType.Null)
            {
                if (attemptToken is not JValue { Type: JTokenType.Integer, Value: long written } ||
                    written < 1 || written > int.MaxValue)
                {
                    failure = $"A change element of {source} has an " +
                        "attempt that is not a positive record attempt number.";
                    return false;
                }
                attempt = (int)written;
            }
            record = new ChangeRecord(
                (int)step,
                attempt,
                nodeId,
                changeAction,
                new ChangeTarget(kind, path, guid, globalId),
                undoable);
            return true;
        }

        private static bool TryOptionalIdentifier(JToken? token, out string? value)
        {
            value = null;
            if (token is null || token.Type == JTokenType.Null)
            {
                return true;
            }
            if (token is JValue { Type: JTokenType.String, Value: string text })
            {
                value = text.Length == 0 ? null : text;
                return true;
            }
            return false;
        }

        private static bool TryParseAction(string action, out ChangeAction changeAction)
        {
            switch (action)
            {
                case CreatedAction:
                    changeAction = ChangeAction.Created;
                    return true;
                case ModifiedAction:
                    changeAction = ChangeAction.Modified;
                    return true;
                case DeletedAction:
                    changeAction = ChangeAction.Deleted;
                    return true;
                case MovedAction:
                    changeAction = ChangeAction.Moved;
                    return true;
                case SavedAction:
                    changeAction = ChangeAction.Saved;
                    return true;
                default:
                    changeAction = default;
                    return false;
            }
        }
    }
}
