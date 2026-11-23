#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// The frozen SPEC §F2 record-type table: wire name, <c>required</c> flag
    /// and durability class for every <see cref="JournalRecordType"/>. Wire
    /// names are literal strings — not a JsonNaming derivation — so the
    /// persisted contract cannot shift under a naming-rule change; golden lines
    /// freeze them byte for byte.
    /// </summary>
    public static class JournalRecordTypes
    {
        private static readonly JournalRecordTypeInfo[] s_all =
        {
            Info(JournalRecordType.RunAccepted, "run_accepted", true, Durability.Flushed),
            Info(JournalRecordType.ApprovalGranted, "approval_granted", true, Durability.Flushed),
            Info(JournalRecordType.ApprovalRejected, "approval_rejected", true, Durability.Flushed),
            Info(JournalRecordType.RunStarted, "run_started", true, Durability.Flushed),
            Info(JournalRecordType.RunSuspended, "run_suspended", true, Durability.Flushed),
            Info(JournalRecordType.RunResumed, "run_resumed", true, Durability.Flushed),
            Info(JournalRecordType.StepStarted, "step_started", true, Durability.Flushed),
            Info(JournalRecordType.StepWaiting, "step_waiting", true, Durability.Flushed),
            Info(JournalRecordType.StepProgress, "step_progress", false, Durability.Buffered),
            Info(JournalRecordType.StepSucceeded, "step_succeeded", true, Durability.Flushed),
            Info(JournalRecordType.StepFailed, "step_failed", true, Durability.Flushed),
            Info(JournalRecordType.StepCancelled, "step_cancelled", true, Durability.Flushed),
            Info(JournalRecordType.StepSkipped, "step_skipped", true, Durability.Flushed),
            Info(JournalRecordType.StepNeedsReview, "step_needs_review", true, Durability.Flushed),
            Info(JournalRecordType.CancelRequested, "cancel_requested", true, Durability.Flushed),
            Info(JournalRecordType.ReviewResolved, "review_resolved", true, Durability.Flushed),
            Info(JournalRecordType.ResourceAcquired, "resource_acquired", true, Durability.Flushed),
            Info(JournalRecordType.ResourceReleased, "resource_released", true, Durability.Flushed),
            Info(JournalRecordType.Log, "log", false, Durability.Buffered),
            Info(JournalRecordType.RunFinished, "run_finished", true, Durability.Flushed),
        };

        private static readonly IReadOnlyDictionary<JournalRecordType, JournalRecordTypeInfo> s_byType =
            BuildTypeIndex();

        private static readonly IReadOnlyDictionary<string, JournalRecordTypeInfo> s_byWireName =
            BuildWireNameIndex();

        private static readonly IReadOnlyList<JournalRecordTypeInfo> s_allReadOnly =
            Array.AsReadOnly(s_all);

        /// <summary>Every row of the SPEC §F2 record-type table, in table order.</summary>
        public static IReadOnlyList<JournalRecordTypeInfo> All => s_allReadOnly;

        /// <summary>The SPEC §F2 table row for <paramref name="type"/>.</summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="type"/> is outside the declared
        /// <see cref="JournalRecordType"/> members.
        /// </exception>
        public static JournalRecordTypeInfo Of(JournalRecordType type)
        {
            if (s_byType.TryGetValue(type, out var info))
            {
                return info;
            }
            throw new ArgumentException(
                "JournalRecordType is outside the SPEC F2 table.", nameof(type));
        }

        /// <summary>
        /// Resolves a wire <c>"type"</c> name to its <see cref="JournalRecordType"/>.
        /// Returns false for any name outside the SPEC §F2 table — the caller applies
        /// the unknown-record rules of SPEC §F2 item 2 to the line's
        /// <c>required</c> flag.
        /// </summary>
        public static bool TryParse(string wireName, out JournalRecordType type)
        {
            if (wireName is not null &&
                s_byWireName.TryGetValue(wireName, out var info))
            {
                type = info.Type;
                return true;
            }
            type = default;
            return false;
        }

        private static JournalRecordTypeInfo Info(
            JournalRecordType type, string wireName, bool required, Durability durability) =>
            new JournalRecordTypeInfo(type, wireName, required, durability);

        private static IReadOnlyDictionary<JournalRecordType, JournalRecordTypeInfo> BuildTypeIndex()
        {
            var index = new Dictionary<JournalRecordType, JournalRecordTypeInfo>();
            foreach (var info in s_all)
            {
                index.Add(info.Type, info);
            }
            return new ReadOnlyDictionary<JournalRecordType, JournalRecordTypeInfo>(index);
        }

        private static IReadOnlyDictionary<string, JournalRecordTypeInfo> BuildWireNameIndex()
        {
            var index = new Dictionary<string, JournalRecordTypeInfo>(StringComparer.Ordinal);
            foreach (var info in s_all)
            {
                index.Add(info.WireName, info);
            }
            return new ReadOnlyDictionary<string, JournalRecordTypeInfo>(index);
        }
    }
}
