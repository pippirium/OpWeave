#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpWeave.Core
{
    /// <summary>
    /// Central catalog of every published <c>OW-&lt;AREA&gt;-&lt;NNN&gt;</c> diagnostic code of
    /// SPEC §J and §J.1 (code, default severity, default retryability), so emitters never
    /// hand-type code strings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The retired numbers OW-OP-009, OW-PERM-005 and OW-VAL-009 stay reserved and the
    /// documentation-only aliases OW-OP-BLUEPRINT-UNSUPPORTED / OW-INTEGRITY-UNSUPPORTED are
    /// never emitted (SPEC §J): <see cref="Create"/>, <see cref="Get"/> and
    /// <see cref="Find"/> accept only codes listed in <see cref="All"/>.
    /// </para>
    /// <para>
    /// Defaults follow the SPEC table's main cell; emitters override for the conditional
    /// cases: OW-WF-006 is emitted with <see cref="Severity.Error"/> for strict mode and
    /// reserved "$" keys, OW-STORE-002 with <see cref="Severity.Warning"/> when corruption is
    /// limited to the last journal line, and OW-RUN-004 with <c>retryable: true</c> only when
    /// the §F5 retry conditions hold.
    /// </para>
    /// </remarks>
    public static class ErrorCodes
    {
        // ── SPEC §J — WF document (A2–A7, D2) ──────────────────────────────────
        public const string OW_WF_001 = "OW-WF-001"; // JSON parse failure (syntax, duplicate keys, depth)
        public const string OW_WF_002 = "OW-WF-002"; // unsupported schemaVersion
        public const string OW_WF_003 = "OW-WF-003"; // required property missing
        public const string OW_WF_004 = "OW-WF-004"; // duplicate ID or edge
        public const string OW_WF_005 = "OW-WF-005"; // malformed ID
        public const string OW_WF_006 = "OW-WF-006"; // unknown property / reserved "$" key (Error when strict or "$")
        public const string OW_WF_007 = "OW-WF-007"; // migration failed
        public const string OW_WF_008 = "OW-WF-008"; // size limit exceeded

        // ── SPEC §J — type system (A5, B1–B7, D4) ──────────────────────────────
        public const string OW_TYPE_001 = "OW-TYPE-001"; // unknown type / malformed Type ID
        public const string OW_TYPE_002 = "OW-TYPE-002"; // output type not assignable to input type
        public const string OW_TYPE_003 = "OW-TYPE-003"; // value does not match type (incl. explicit null on Nullable=false)
        public const string OW_TYPE_005 = "OW-TYPE-005"; // wrong number of generic arguments

        // ── SPEC §J — Operations (C2–C8, D3–D4, M.4) ───────────────────────────
        public const string OW_OP_001 = "OW-OP-001"; // unknown Operation
        public const string OW_OP_002 = "OW-OP-002"; // unsupported Operation version
        public const string OW_OP_003 = "OW-OP-003"; // invalid Descriptor at registration
        public const string OW_OP_004 = "OW-OP-004"; // duplicate (id, version) registration
        public const string OW_OP_005 = "OW-OP-005"; // unknown port
        public const string OW_OP_006 = "OW-OP-006"; // parameter violates constraints (enum, range, ConstantOnly)
        public const string OW_OP_007 = "OW-OP-007"; // custom Operation in reserved namespace (core./unity./opweave.)
        public const string OW_OP_008 = "OW-OP-008"; // Operation class cannot be instantiated

        // ── SPEC §J — validation (A1, A6, D2–D6, G6, G8) ───────────────────────
        public const string OW_VAL_001 = "OW-VAL-001"; // core.start missing or multiple
        public const string OW_VAL_002 = "OW-VAL-002"; // unreachable node
        public const string OW_VAL_003 = "OW-VAL-003"; // control-flow cycle
        public const string OW_VAL_004 = "OW-VAL-004"; // branch/join or unsupported control port (v1: "next" only)
        public const string OW_VAL_005 = "OW-VAL-005"; // required input not satisfied
        public const string OW_VAL_006 = "OW-VAL-006"; // multiple explicit sources for one input
        public const string OW_VAL_007 = "OW-VAL-007"; // data source not guaranteed to execute before consumer
        public const string OW_VAL_008 = "OW-VAL-008"; // required WF input missing
        public const string OW_VAL_010 = "OW-VAL-010"; // node count limit exceeded
        public const string OW_VAL_011 = "OW-VAL-011"; // reference to a nonexistent node/input
        public const string OW_VAL_012 = "OW-VAL-012"; // compile node inside Prefab editing session
        public const string OW_VAL_013 = "OW-VAL-013"; // no compile wait between codegen and type use
        public const string OW_VAL_014 = "OW-VAL-014"; // execution input not declared by the workflow (ignored)
        public const string OW_VAL_015 = "OW-VAL-015"; // type_exists type not found but earlier codegen may define it (rechecked at run time)

        // ── SPEC §J — permissions (D6, G1, G7, G8, H4) ─────────────────────────
        public const string OW_PERM_001 = "OW-PERM-001"; // denied effect or unapproved source
        public const string OW_PERM_002 = "OW-PERM-002"; // outside allowed paths
        public const string OW_PERM_003 = "OW-PERM-003"; // approval not granted (rejected/expired)
        public const string OW_PERM_004 = "OW-PERM-004"; // approval impossible in non-interactive environment

        // ── SPEC §J — run (D7, E2–E7, F4, G1, G4, G8, H1) ──────────────────────
        public const string OW_RUN_001 = "OW-RUN-001"; // not in Edit Mode
        public const string OW_RUN_002 = "OW-RUN-002"; // another Run is active or the queue is full
        public const string OW_RUN_003 = "OW-RUN-003"; // planHash mismatch
        public const string OW_RUN_004 = "OW-RUN-004"; // step timeout (retryable only when §F5 conditions hold)
        public const string OW_RUN_005 = "OW-RUN-005"; // cancelled (reason for Cancelled)
        public const string OW_RUN_006 = "OW-RUN-006"; // Operation threw an exception
        public const string OW_RUN_007 = "OW-RUN-007"; // missing output, type mismatch or output limit exceeded
        public const string OW_RUN_008 = "OW-RUN-008"; // retry limit reached
        public const string OW_RUN_009 = "OW-RUN-009"; // compile error
        public const string OW_RUN_010 = "OW-RUN-010"; // unknown runId
        public const string OW_RUN_011 = "OW-RUN-011"; // Operation used by Plan deleted/changed; cannot start/resume safely
        public const string OW_RUN_012 = "OW-RUN-012"; // unsaved changes may have been lost on Editor restart

        // ── SPEC §J — persistence (D7, F1–F8) ──────────────────────────────────
        public const string OW_STORE_001 = "OW-STORE-001"; // Run Store write failed
        public const string OW_STORE_002 = "OW-STORE-002"; // journal corruption / planFileDigest or Plan inconsistency (Warning for last line)
        public const string OW_STORE_003 = "OW-STORE-003"; // already deleted by retention

        // ── SPEC §J — Unity adapter (D6, G1–G8, M.4) ───────────────────────────
        public const string OW_UNITY_001 = "OW-UNITY-001"; // referenced object not found
        public const string OW_UNITY_002 = "OW-UNITY-002"; // ambiguous reference / conflicting keys
        public const string OW_UNITY_003 = "OW-UNITY-003"; // scene not loaded
        public const string OW_UNITY_004 = "OW-UNITY-004"; // Prefab asset vs instance mix-up
        public const string OW_UNITY_005 = "OW-UNITY-005"; // invalid path or conflicting existing asset
        public const string OW_UNITY_006 = "OW-UNITY-006"; // type/shader not found or cannot be added
        public const string OW_UNITY_007 = "OW-UNITY-007"; // property/hierarchy/Component operation failed
        public const string OW_UNITY_008 = "OW-UNITY-008"; // import failed
        public const string OW_UNITY_009 = "OW-UNITY-009"; // package operation failed
        public const string OW_UNITY_010 = "OW-UNITY-010"; // read-only target (immutable package etc.)
        public const string OW_UNITY_011 = "OW-UNITY-011"; // unsaved scene prevents the operation
        public const string OW_UNITY_012 = "OW-UNITY-012"; // Operation created outside an Editor-only folder

        // ── SPEC §J — Agent API (H1–H3) ─────────────────────────────────────────
        public const string OW_API_001 = "OW-API-001"; // malformed request
        public const string OW_API_002 = "OW-API-002"; // invalid cursor
        public const string OW_API_003 = "OW-API-003"; // Editor busy, cannot accept
        public const string OW_API_004 = "OW-API-004"; // internal error

        // ── SPEC §J.1 — codes added after the v1 integration ───────────────────
        public const string OW_OP_010 = "OW-OP-010"; // Blueprint Operation registration/execution unsupported in v1
        public const string OW_OP_011 = "OW-OP-011"; // IntegrityLevel=full execution unsupported in v1
        public const string OW_OP_012 = "OW-OP-012"; // invalid external CoreApi range / current Core out of range
        public const string OW_OP_013 = "OW-OP-013"; // duplicate Type ID or Provider ID (both candidates invalidated)
        public const string OW_OP_014 = "OW-OP-014"; // reference to unregistered input/output/base/dependency type
        public const string OW_OP_015 = "OW-OP-015"; // cycle in type base/dependency relations
        public const string OW_OP_016 = "OW-OP-016"; // missing Codec or normalization-contract violation detectable at registration
        public const string OW_OP_017 = "OW-OP-017"; // defining assembly unresolvable / not provably Editor-only
        public const string OW_OP_018 = "OW-OP-018"; // external extension metadata contradicts the actual artifact
        public const string OW_OP_019 = "OW-OP-019"; // TypeProvider class cannot be instantiated
        public const string OW_OP_020 = "OW-OP-020"; // SG-11 Plan environment identifier unobtainable (rejects new Plan/Run)
        public const string OW_RUN_013 = "OW-RUN-013"; // change to a definition used by an unfinished Run rejected
        public const string OW_RUN_014 = "OW-RUN-014"; // Agent cancel_workflow on a Run state that cannot be cancelled
        public const string OW_CFG_001 = "OW-CFG-001"; // settings JSON parse failure / unknown version
        public const string OW_CFG_002 = "OW-CFG-002"; // invalid value for a known settings key
        public const string OW_CFG_003 = "OW-CFG-003"; // unknown settings key ignored (preserved on human save)
        public const string OW_CFG_004 = "OW-CFG-004"; // settings v1 migrated to v2 in memory
        public const string OW_CFG_005 = "OW-CFG-005"; // code.write=allow corrected to ask (warning, once)

        // ── Reserved numbers and documentation-only aliases — never emitted ────
        // Kept as constants only so callers/tests refer to them without hand-typed strings;
        // they are absent from the published catalog below and rejected by Create/Get/Find.
        public const string OW_OP_009 = "OW-OP-009";   // retired source/MVID fallback warning
        public const string OW_PERM_005 = "OW-PERM-005"; // retired review mode
        public const string OW_VAL_009 = "OW-VAL-009";  // unassigned, stays reserved
        public const string OW_OP_BLUEPRINT_UNSUPPORTED = "OW-OP-BLUEPRINT-UNSUPPORTED"; // emit OW_OP_010
        public const string OW_INTEGRITY_UNSUPPORTED = "OW-INTEGRITY-UNSUPPORTED";        // emit OW_OP_011

        private static ErrorCodeInfo E(string code, Severity severity, bool retryable) =>
            new ErrorCodeInfo(code, severity, retryable);

        // Mirrors the SPEC §J table (in table order) followed by the SPEC §J.1 table.
        private static readonly ErrorCodeInfo[] s_all =
        {
            E(OW_WF_001, Severity.Error, false),
            E(OW_WF_002, Severity.Error, false),
            E(OW_WF_003, Severity.Error, false),
            E(OW_WF_004, Severity.Error, false),
            E(OW_WF_005, Severity.Error, false),
            E(OW_WF_006, Severity.Warning, false),
            E(OW_WF_007, Severity.Error, false),
            E(OW_WF_008, Severity.Error, false),

            E(OW_TYPE_001, Severity.Error, false),
            E(OW_TYPE_002, Severity.Error, false),
            E(OW_TYPE_003, Severity.Error, false),
            E(OW_TYPE_005, Severity.Error, false),

            E(OW_OP_001, Severity.Error, false),
            E(OW_OP_002, Severity.Error, false),
            E(OW_OP_003, Severity.Error, false),
            E(OW_OP_004, Severity.Error, false),
            E(OW_OP_005, Severity.Error, false),
            E(OW_OP_006, Severity.Error, false),
            E(OW_OP_007, Severity.Error, false),
            E(OW_OP_008, Severity.Error, false),

            E(OW_VAL_001, Severity.Error, false),
            E(OW_VAL_002, Severity.Error, false),
            E(OW_VAL_003, Severity.Error, false),
            E(OW_VAL_004, Severity.Error, false),
            E(OW_VAL_005, Severity.Error, false),
            E(OW_VAL_006, Severity.Error, false),
            E(OW_VAL_007, Severity.Error, false),
            E(OW_VAL_008, Severity.Error, false),
            E(OW_VAL_010, Severity.Error, false),
            E(OW_VAL_011, Severity.Error, false),
            E(OW_VAL_012, Severity.Error, false),
            E(OW_VAL_013, Severity.Warning, false),
            E(OW_VAL_014, Severity.Warning, false),
            E(OW_VAL_015, Severity.Warning, false),

            E(OW_PERM_001, Severity.Error, false),
            E(OW_PERM_002, Severity.Error, false),
            E(OW_PERM_003, Severity.Error, false),
            E(OW_PERM_004, Severity.Error, false),

            E(OW_RUN_001, Severity.Error, true),
            E(OW_RUN_002, Severity.Error, true),
            E(OW_RUN_003, Severity.Error, false),
            E(OW_RUN_004, Severity.Error, false),
            E(OW_RUN_005, Severity.Info, false),
            E(OW_RUN_006, Severity.Error, false),
            E(OW_RUN_007, Severity.Error, false),
            E(OW_RUN_008, Severity.Error, false),
            E(OW_RUN_009, Severity.Error, false),
            E(OW_RUN_010, Severity.Error, false),
            E(OW_RUN_011, Severity.Error, false),
            E(OW_RUN_012, Severity.Error, false),

            E(OW_STORE_001, Severity.Error, false),
            E(OW_STORE_002, Severity.Error, false),
            E(OW_STORE_003, Severity.Error, false),

            E(OW_UNITY_001, Severity.Error, false),
            E(OW_UNITY_002, Severity.Error, false),
            E(OW_UNITY_003, Severity.Error, false),
            E(OW_UNITY_004, Severity.Error, false),
            E(OW_UNITY_005, Severity.Error, false),
            E(OW_UNITY_006, Severity.Error, false),
            E(OW_UNITY_007, Severity.Error, false),
            E(OW_UNITY_008, Severity.Error, true),
            E(OW_UNITY_009, Severity.Error, true),
            E(OW_UNITY_010, Severity.Error, false),
            E(OW_UNITY_011, Severity.Error, false),
            E(OW_UNITY_012, Severity.Error, false),

            E(OW_API_001, Severity.Error, false),
            E(OW_API_002, Severity.Error, false),
            E(OW_API_003, Severity.Error, true),
            E(OW_API_004, Severity.Error, false),

            E(OW_OP_010, Severity.Error, false),
            E(OW_OP_011, Severity.Error, false),
            E(OW_OP_012, Severity.Error, false),
            E(OW_OP_013, Severity.Error, false),
            E(OW_OP_014, Severity.Error, false),
            E(OW_OP_015, Severity.Error, false),
            E(OW_OP_016, Severity.Error, false),
            E(OW_OP_017, Severity.Error, false),
            E(OW_OP_018, Severity.Error, false),
            E(OW_OP_019, Severity.Error, false),
            E(OW_OP_020, Severity.Error, false),
            E(OW_RUN_013, Severity.Error, false),
            E(OW_RUN_014, Severity.Error, false),
            E(OW_CFG_001, Severity.Error, false),
            E(OW_CFG_002, Severity.Error, false),
            E(OW_CFG_003, Severity.Warning, false),
            E(OW_CFG_004, Severity.Warning, false),
            E(OW_CFG_005, Severity.Warning, false),
        };

        private static readonly IReadOnlyDictionary<string, ErrorCodeInfo> s_byCode = BuildIndex();

        private static IReadOnlyDictionary<string, ErrorCodeInfo> BuildIndex()
        {
            var index = new Dictionary<string, ErrorCodeInfo>(StringComparer.Ordinal);
            foreach (var info in s_all)
            {
                index.Add(info.Code, info);
            }
            return new ReadOnlyDictionary<string, ErrorCodeInfo>(index);
        }

        private static readonly IReadOnlyList<ErrorCodeInfo> s_allReadOnly = Array.AsReadOnly(s_all);

        /// <summary>Every published code of SPEC §J/§J.1 in table order.</summary>
        public static IReadOnlyList<ErrorCodeInfo> All => s_allReadOnly;

        /// <summary>The catalog entry for a published code, or null for anything else.</summary>
        public static ErrorCodeInfo? Find(string code) =>
            code is not null && s_byCode.TryGetValue(code, out var info) ? info : null;

        /// <summary>
        /// The catalog entry for a published code; throws <see cref="ArgumentException"/>
        /// for reserved, aliased or unknown strings.
        /// </summary>
        public static ErrorCodeInfo Get(string code) =>
            Find(code) ?? throw new ArgumentException(
                $"Not a published SPEC §J diagnostic code: {code ?? "<null>"}", nameof(code));

        /// <summary>True when <paramref name="code"/> is a published code of SPEC §J/§J.1.</summary>
        public static bool IsPublished(string code) =>
            code is not null && s_byCode.ContainsKey(code);

        /// <summary>True for the retired reserved numbers OW-OP-009, OW-PERM-005, OW-VAL-009.</summary>
        public static bool IsReserved(string code) =>
            code == OW_OP_009 || code == OW_PERM_005 || code == OW_VAL_009;

        /// <summary>
        /// Emits a <see cref="Diagnostic"/> for a published code with the SPEC defaults;
        /// <paramref name="severity"/> and <paramref name="retryable"/> override the defaults
        /// for the conditional cases documented on this class. Throws
        /// <see cref="ArgumentException"/> for reserved, aliased or unknown code strings.
        /// </summary>
        public static Diagnostic Create(
            string code,
            string message,
            string? nodeId = null,
            string? port = null,
            string? jsonPath = null,
            string? hint = null,
            Severity? severity = null,
            bool? retryable = null)
        {
            if (string.IsNullOrEmpty(message))
            {
                throw new ArgumentException("Diagnostic message must be fixed English text.", nameof(message));
            }
            var info = Get(code);
            return new Diagnostic(
                info.Code,
                severity ?? info.DefaultSeverity,
                message,
                nodeId,
                port,
                jsonPath,
                hint,
                retryable ?? info.DefaultRetryable);
        }
    }
}
