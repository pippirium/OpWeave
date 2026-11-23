#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Migration
{
    /// <summary>
    /// The SPEC §A7 version-migration framework: upgrades a workflow document JSON
    /// from an older <c>schemaVersion</c> and older per-node Operation versions to
    /// the current format. Invoked only through <c>migrate_workflow</c>
    /// (T-W3.2-03) — never during validate or execute (SPEC §A7 item 3). The
    /// framework performs no file I/O: it returns the migrated JSON plus the list
    /// of applied changes and leaves saving to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kept internal for this ticket: the only public contract SPEC §A7 defines is
    /// the <see cref="IWorkflowMigration"/> / <see cref="IOperationMigration"/>
    /// interface pair, and the registered-version lookup it needs —
    /// <see cref="IOperationMigrationCatalog"/> — must not leak Registry internals
    /// into the public extension surface (SPEC §C0.1). T-W3.2-03 decides how
    /// <c>migrate_workflow</c> reaches the engine.
    /// </para>
    /// <para>
    /// Phase 1 chains <see cref="IWorkflowMigration"/> steps while the document's
    /// major version is older than the current one. Each step is selected by its
    /// declared <see cref="IWorkflowMigration.From"/>, its returned document must
    /// declare exactly <see cref="IWorkflowMigration.To"/>, and a version no
    /// registered migration can advance is OW-WF-002. A newer major version is
    /// OW-WF-002 outright; a same-major newer minor needs no migration because
    /// minor additions never change existing semantics (SPEC §N.3 items 1 and 3).
    /// </para>
    /// <para>
    /// Phase 2 resolves every node's <c>version</c> against the registered
    /// versions exposed by <see cref="IOperationMigrationCatalog"/> and chains the
    /// <see cref="IOperationMigration"/> each newer registered version provides
    /// until the node reaches the latest registered version — the framework owns
    /// the <c>version</c> bump. An Operation with no registered versions is
    /// unknown and reported as OW-OP-001 (SPEC §J, §N.3 item 3); a node version
    /// newer than every registered version, or a chain with no step accepting
    /// the current version of a known Operation, is OW-OP-002 (SPEC §A7 item 2);
    /// custom Operations follow the same mechanism.
    /// </para>
    /// <para>
    /// Any thrown exception, null return or contract violation from a migration is
    /// OW-WF-007; the exception text is never propagated into a diagnostic because
    /// it may carry secrets. Nodes whose <c>operation</c>/<c>version</c> members are
    /// absent or malformed are left for validation (the migrator is not a
    /// validator) and skipped.
    /// </para>
    /// </remarks>
    internal static class WorkflowMigrator
    {
        // The schemaVersion this implementation migrates documents to (SPEC §A2, §N.3 item 2).
        private const int TargetMajor = 1;
        private const int TargetMinor = 0;
        private const string TargetSchemaVersion = "1.0";

        private static readonly System.Text.RegularExpressions.Regex s_versionPattern =
            new System.Text.RegularExpressions.Regex(
                @"\A(\d+)\.(\d+)\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Migrates <paramref name="document"/> to the current schemaVersion and its
        /// nodes to the latest registered Operation versions. The input
        /// <paramref name="document"/> is never mutated and no file is written.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal static Result<WorkflowMigrationResult> Migrate(
            JObject document,
            IReadOnlyList<IWorkflowMigration> workflowMigrations,
            IOperationMigrationCatalog operationMigrations)
        {
            if (document is null)
            {
                throw new ArgumentNullException(nameof(document));
            }
            if (workflowMigrations is null)
            {
                throw new ArgumentNullException(nameof(workflowMigrations));
            }
            if (operationMigrations is null)
            {
                throw new ArgumentNullException(nameof(operationMigrations));
            }

            var work = (JObject)document.DeepClone();
            var changes = new List<MigrationChange>();

            var schemaResult = ApplySchemaMigrations(work, workflowMigrations, changes);
            if (!schemaResult.Ok)
            {
                return Result<WorkflowMigrationResult>.Failure(schemaResult.Diagnostics);
            }
            work = schemaResult.Value!;

            var operationDiagnostics = ApplyOperationMigrations(work, operationMigrations, changes);
            if (operationDiagnostics.Count != 0)
            {
                return Result<WorkflowMigrationResult>.Failure(operationDiagnostics);
            }

            return Result<WorkflowMigrationResult>.Success(new WorkflowMigrationResult(work, changes));
        }

        // Chains IWorkflowMigration steps until the document reaches the current
        // major version; the returned JObject is the next chain input. A major
        // already newer than the target, or a version no registered migration
        // advances, is OW-WF-002 (unsupported schemaVersion); a migration that
        // throws, returns null, or produces a version other than its declared To
        // is OW-WF-007 (migration failed).
        private static Result<JObject> ApplySchemaMigrations(
            JObject work,
            IReadOnlyList<IWorkflowMigration> migrations,
            List<MigrationChange> changes)
        {
            const string path = "$.schemaVersion";
            if (!TryReadVersion(work["schemaVersion"], out var text, out var version))
            {
                const string reason = "The workflow document has an unreadable or unsupported schemaVersion.";
                return Result<JObject>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_WF_002, reason, jsonPath: path));
            }

            var target = new SchemaVersion(TargetMajor, TargetMinor);
            if (version.Major > target.Major)
            {
                return Result<JObject>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_WF_002,
                    "The workflow schemaVersion major is newer than this implementation supports.",
                    jsonPath: path));
            }

            // A same-major version — newer minor included — already reads with the
            // current semantics (SPEC §N.3 item 3); only an older major chains.
            var visited = new HashSet<SchemaVersion>();
            while (version.CompareTo(target) < 0)
            {
                if (!visited.Add(version))
                {
                    return Result<JObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_WF_007,
                        "The registered schemaVersion migrations do not reach schemaVersion " +
                        TargetSchemaVersion,
                        jsonPath: path));
                }

                var step = FindSchemaStep(migrations, version);
                if (step is null)
                {
                    return Result<JObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_WF_002,
                        "No registered workflow migration reaches the supported schemaVersion.",
                        jsonPath: path));
                }
                if (!TryParseVersion(step.To, out var declaredTo))
                {
                    return Result<JObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_WF_007,
                        "A registered workflow migration declares an unreadable target version.",
                        jsonPath: path));
                }

                JObject? next;
                try
                {
                    next = step.Migrate(work);
                }
                catch (Exception)
                {
                    // The exception text is never propagated — it may carry secrets.
                    return Result<JObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_WF_007,
                        "A workflow migration threw an exception.",
                        jsonPath: path));
                }
                if (next is null)
                {
                    return Result<JObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_WF_007,
                        "A workflow migration returned no document.",
                        jsonPath: path));
                }
                if (!TryReadVersion(next["schemaVersion"], out var producedText, out var produced))
                {
                    return Result<JObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_WF_007,
                        "A workflow migration produced a document without a readable schemaVersion.",
                        jsonPath: path));
                }
                if (!produced.Equals(declaredTo))
                {
                    return Result<JObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_WF_007,
                        "A workflow migration produced a schemaVersion different from its declared target.",
                        jsonPath: path));
                }
                if (produced.CompareTo(target) > 0)
                {
                    return Result<JObject>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_WF_007,
                        "A workflow migration produced a schemaVersion beyond the supported version.",
                        jsonPath: path));
                }

                changes.Add(new MigrationChange(
                    MigrationChangeKind.SchemaVersion, null, null, text, producedText));
                work = next;
                version = produced;
                text = producedText;
            }
            return Result<JObject>.Success(work);
        }

        private static IWorkflowMigration? FindSchemaStep(
            IReadOnlyList<IWorkflowMigration> migrations, SchemaVersion version)
        {
            foreach (var migration in migrations)
            {
                if (migration is null ||
                    !TryParseVersion(migration.From, out var from) ||
                    !from.Equals(version))
                {
                    continue;
                }
                return migration;
            }
            return null;
        }

        // Resolves every node's version against the registered versions in the
        // catalog and chains their IOperationMigration steps up to the latest
        // registered version. Nodes without a readable operation/version pair are
        // skipped — structural errors belong to validation, not migration.
        private static List<Diagnostic> ApplyOperationMigrations(
            JObject work,
            IOperationMigrationCatalog catalog,
            List<MigrationChange> changes)
        {
            var diagnostics = new List<Diagnostic>();
            if (work["nodes"] is not JArray nodes)
            {
                return diagnostics;
            }

            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i] is not JObject node)
                {
                    continue;
                }
                var readStatus = TryReadNode(
                    node, out var operationId, out var nodeVersion, out var nodeId);
                var path = "$.nodes[" + i.ToString(CultureInfo.InvariantCulture) + "].version";
                if (readStatus == NodeReadStatus.UnsupportedVersion)
                {
                    // A JSON integer outside the supported Int32 operation-version
                    // range must never silently bypass migration as a success.
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_002,
                        "Operation version is outside the supported integer range.",
                        nodeId: nodeId,
                        jsonPath: path));
                    continue;
                }
                if (readStatus == NodeReadStatus.Malformed)
                {
                    // Missing/wrongly typed members belong to structural validation.
                    continue;
                }
                var entries = catalog.RegisteredVersions(operationId);
                if (entries is null || entries.Count == 0)
                {
                    // An Operation with no registered versions is unknown, not an
                    // unsupported version of a known Operation (SPEC §J OW-OP-001,
                    // §N.3 item 3). The path names the member holding the
                    // unregistered ID; the ID itself never enters the message.
                    diagnostics.Add(ErrorCodes.Create(
                        ErrorCodes.OW_OP_001,
                        "The node's Operation is not registered.",
                        nodeId: nodeId,
                        jsonPath: "$.nodes[" + i.ToString(CultureInfo.InvariantCulture) +
                                  "].operation"));
                    continue;
                }

                // The contract is ascending order; the latest version is the last entry.
                var latest = entries[entries.Count - 1].Version;
                var current = nodeVersion;
                var steps = 0;
                while (current != latest)
                {
                    if (current > latest)
                    {
                        diagnostics.Add(UnsupportedVersion(nodeId, path,
                            "The node version is newer than the latest registered version."));
                        break;
                    }

                    var step = FindOperationStep(entries, current);
                    if (step is null)
                    {
                        diagnostics.Add(UnsupportedVersion(nodeId, path,
                            "No registered Operation migration upgrades the node version."));
                        break;
                    }

                    // A null list means the Registry never declared whether
                    // this version changes external port names. Reject rather
                    // than silently leaving stale workflow references. An
                    // explicitly empty list means this step renames no ports.
                    if (step.PortRenames is null)
                    {
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_WF_007,
                            "Operation migration is missing port compatibility metadata.",
                            nodeId: nodeId,
                            jsonPath: path));
                        break;
                    }

                    try
                    {
                        step.Migration!.Migrate(node);
                    }
                    catch (Exception)
                    {
                        // The exception text is never propagated — it may carry secrets.
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_WF_007,
                            "An Operation migration threw an exception.",
                            nodeId: nodeId,
                            jsonPath: "$.nodes[" + i.ToString(CultureInfo.InvariantCulture) + "]"));
                        break;
                    }

                    // A version migration must not silently change node identity:
                    // other workflow references are keyed by this ID/operation.
                    if (nodeId is not null &&
                        (node["id"]?.Type != JTokenType.String ||
                         node["operation"]?.Type != JTokenType.String ||
                         (string?)node["id"] != nodeId ||
                         (string?)node["operation"] != operationId))
                    {
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_WF_007,
                            "An Operation migration changed node identity.",
                            nodeId: nodeId,
                            jsonPath: path));
                        break;
                    }

                    // Node-only migrations cannot rewrite the workflow's edges.
                    // The internal Registry descriptor supplies explicit port
                    // renames when required, without widening the public API.
                    if (step.PortRenames is { Count: > 0 } &&
                        (nodeId is null ||
                         !TryRewritePortReferences(work, nodeId, step.PortRenames)))
                    {
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_WF_007,
                            "Operation port migration metadata is invalid.",
                            nodeId: nodeId,
                            jsonPath: path));
                        break;
                    }

                    var from = current;
                    current = step.Version;
                    node["version"] = new JValue(current);
                    changes.Add(new MigrationChange(
                        MigrationChangeKind.OperationVersion,
                        nodeId,
                        operationId,
                        from.ToString(CultureInfo.InvariantCulture),
                        current.ToString(CultureInfo.InvariantCulture)));
                    if (++steps > entries.Count)
                    {
                        diagnostics.Add(ErrorCodes.Create(
                            ErrorCodes.OW_WF_007,
                            "The registered Operation migrations do not reach the latest version.",
                            nodeId: nodeId,
                            jsonPath: path));
                        break;
                    }
                }
            }
            return diagnostics;
        }

        // The smallest registered version above `current` whose migration accepts it.
        private static RegisteredOperationVersion? FindOperationStep(
            IReadOnlyList<RegisteredOperationVersion> entries, int current)
        {
            RegisteredOperationVersion? next = null;
            foreach (var entry in entries)
            {
                if (entry.Version <= current ||
                    entry.Migration is null ||
                    entry.Migration.FromVersion != current)
                {
                    continue;
                }
                if (next is null || entry.Version < next.Version)
                {
                    next = entry;
                }
            }
            return next;
        }

        /// <summary>
        /// An Operation migration only receives its JObject node (SPEC §A7).
        /// The internal registered-version metadata is the authoritative list
        /// of external port renames for that step. Port references in different
        /// arrays are rewritten by original node ID, kind and old port name.
        /// Maps are applied simultaneously per referenced field to avoid
        /// accidental A->B->C cascading within a single migration step.
        /// </summary>
        private static bool TryRewritePortReferences(
            JObject document,
            string nodeId,
            IReadOnlyList<OperationPortRename> renames)
        {
            var input = new Dictionary<string, string>(StringComparer.Ordinal);
            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            var control = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var rename in renames)
            {
                if (rename is null ||
                    !IsPortName(rename.From) || !IsPortName(rename.To))
                {
                    return false;
                }
                Dictionary<string, string> map;
                switch (rename.Kind)
                {
                    case OperationPortKind.Input:
                        map = input;
                        break;
                    case OperationPortKind.Output:
                        map = output;
                        break;
                    case OperationPortKind.Control:
                        map = control;
                        break;
                    default:
                        return false;
                }
                // Ambiguous old mappings or multiple sources targeting one new
                // port would make the result impossible to interpret safely.
                if (map.ContainsKey(rename.From) ||
                    (rename.From != rename.To && map.ContainsValue(rename.To)))
                {
                    return false;
                }
                map.Add(rename.From, rename.To);
            }

            if (document["dataEdges"] is JArray dataEdges)
            {
                foreach (var edge in dataEdges)
                {
                    if (edge is JObject dataEdge)
                    {
                        RewritePortRef(dataEdge["from"], nodeId, output);
                        RewritePortRef(dataEdge["to"], nodeId, input);
                    }
                }
            }
            if (document["inputBindings"] is JArray inputBindings)
            {
                foreach (var binding in inputBindings)
                {
                    if (binding is JObject item)
                    {
                        RewritePortRef(item["to"], nodeId, input);
                    }
                }
            }
            if (document["outputs"] is JArray outputs)
            {
                foreach (var entry in outputs)
                {
                    if (entry is JObject item)
                    {
                        RewritePortRef(item["from"], nodeId, output);
                    }
                }
            }
            if (document["controlEdges"] is JArray controlEdges)
            {
                foreach (var edge in controlEdges)
                {
                    if (edge is not JObject item ||
                        item["from"]?.Type != JTokenType.String ||
                        (string?)item["from"] != nodeId)
                    {
                        continue;
                    }
                    // Omitted fromPort has the SPEC §A2 default "next".
                    var old = item["fromPort"] is null ? "next"
                        : item["fromPort"]?.Type == JTokenType.String
                            ? (string?)item["fromPort"] : null;
                    if (old is not null && control.TryGetValue(old, out var renamed))
                    {
                        item["fromPort"] = renamed;
                    }
                }
            }
            return true;
        }

        private static void RewritePortRef(
            JToken? reference, string nodeId, Dictionary<string, string> renames)
        {
            if (reference is not JObject portRef ||
                portRef["node"]?.Type != JTokenType.String ||
                (string?)portRef["node"] != nodeId ||
                portRef["port"]?.Type != JTokenType.String)
            {
                return;
            }
            var oldPort = (string?)portRef["port"];
            if (oldPort is not null && renames.TryGetValue(oldPort, out var renamed))
            {
                portRef["port"] = renamed;
            }
        }

        private static bool IsPortName(string? name)
        {
            // SPEC §A4, ASCII identifier, 1–64 chars.
            if (name is null || name.Length == 0 || name.Length > 64 ||
                !IsAsciiLetter(name[0]))
            {
                return false;
            }
            for (var i = 1; i < name.Length; i++)
            {
                var ch = name[i];
                if (!IsAsciiLetter(ch) && ch != '_' && (ch < '0' || ch > '9'))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool IsAsciiLetter(char ch) =>
            (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

        private static Diagnostic UnsupportedVersion(
            string? nodeId, string path, string hint) =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_002,
                "Operation version is not supported.",
                nodeId: nodeId,
                jsonPath: path,
                hint: hint);

        private enum NodeReadStatus
        {
            Valid,
            Malformed,
            UnsupportedVersion,
        }

        private static NodeReadStatus TryReadNode(
            JObject node, out string operationId, out int version, out string? nodeId)
        {
            operationId = string.Empty;
            version = 0;
            nodeId = node["id"] is JValue idValue && idValue.Type == JTokenType.String
                ? (string)idValue.Value!
                : null;
            if (node["operation"] is not JValue operation ||
                operation.Type != JTokenType.String ||
                node["version"] is not JValue versionValue ||
                versionValue.Type != JTokenType.Integer)
            {
                return NodeReadStatus.Malformed;
            }

            // JObject.Parse typically boxes integers as Int64, while an in-memory
            // JObject built by a caller can contain an Int32 JValue. Both are
            // valid JSON integers. Do not coerce floating point or strings.
            switch (versionValue.Value)
            {
                case int intValue:
                    version = intValue;
                    break;
                case long longValue when longValue >= int.MinValue &&
                                         longValue <= int.MaxValue:
                    version = (int)longValue;
                    break;
                default:
                    return NodeReadStatus.UnsupportedVersion;
            }

            operationId = (string)operation.Value!;
            return NodeReadStatus.Valid;
        }

        private static bool TryReadVersion(JToken? token, out string text, out SchemaVersion version)
        {
            text = string.Empty;
            version = default;
            if (token is not JValue value || value.Type != JTokenType.String)
            {
                return false;
            }
            text = (string)value.Value!;
            return TryParseVersion(text, out version);
        }

        private static bool TryParseVersion(string? text, out SchemaVersion version)
        {
            version = default;
            if (text is null)
            {
                return false;
            }
            var match = s_versionPattern.Match(text);
            if (!match.Success ||
                !int.TryParse(
                    match.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var major) ||
                !int.TryParse(
                    match.Groups[2].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var minor))
            {
                return false;
            }
            version = new SchemaVersion(major, minor);
            return true;
        }

        private readonly struct SchemaVersion : IComparable<SchemaVersion>, IEquatable<SchemaVersion>
        {
            public SchemaVersion(int major, int minor)
            {
                Major = major;
                Minor = minor;
            }

            public int Major { get; }
            public int Minor { get; }

            public int CompareTo(SchemaVersion other) =>
                Major != other.Major
                    ? Major.CompareTo(other.Major)
                    : Minor.CompareTo(other.Minor);

            public bool Equals(SchemaVersion other) => CompareTo(other) == 0;

            public override bool Equals(object? obj) =>
                obj is SchemaVersion other && Equals(other);

            public override int GetHashCode() => Major * 31 + Minor;
        }
    }
}
