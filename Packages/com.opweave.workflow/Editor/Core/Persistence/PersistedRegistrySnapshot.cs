#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Persistence
{
    /// <summary>
    /// One Operation entry of the persisted <see cref="RegistrySnapshot"/>
    /// summary (SPEC §C3 rule 7): the seven fields <c>registry.json</c>
    /// records per Operation — id, version, origin, assemblyName, sourceFile,
    /// descriptorHash and implementationHash. The Descriptor itself, the
    /// implementation instance and the package slots are runtime state and
    /// are never persisted.
    /// </summary>
    public sealed record PersistedRegistryOperation(
        string Id,
        int Version,
        OperationOrigin Origin,
        string AssemblyName,
        string? SourceFile,
        string DescriptorHash,
        string ImplementationHash)
    {
        /// <summary>The registered Operation ID.</summary>
        public string Id { get; init; } =
            string.IsNullOrEmpty(Id)
                ? throw new ArgumentException(
                    "Operation ID must not be null or empty.", nameof(Id))
                : Id;

        /// <summary>The simple name of the defining assembly.</summary>
        public string AssemblyName { get; init; } =
            string.IsNullOrEmpty(AssemblyName)
                ? throw new ArgumentException(
                    "Assembly name must not be null or empty.", nameof(AssemblyName))
                : AssemblyName;

        /// <summary>Lowercase-hex SHA-256 of the Descriptor's Canonical JSON (SPEC §C3 rule 6).</summary>
        public string DescriptorHash { get; init; } =
            string.IsNullOrEmpty(DescriptorHash)
                ? throw new ArgumentException(
                    "Descriptor hash must not be null or empty.", nameof(DescriptorHash))
                : DescriptorHash;

        /// <summary>Lowercase-hex SHA-256 of the defining assembly's compiled bytes (SPEC §C3 rule 6).</summary>
        public string ImplementationHash { get; init; } =
            string.IsNullOrEmpty(ImplementationHash)
                ? throw new ArgumentException(
                    "Implementation hash must not be null or empty.", nameof(ImplementationHash))
                : ImplementationHash;
    }

    /// <summary>
    /// The persisted summary of one published <see cref="RegistrySnapshot"/>
    /// (SPEC §C3 rule 7): the generation ID, the registryHash and every
    /// registered Operation's persisted entry, written to
    /// <c>Library/OpWeave/registry.json</c> through the SPEC §F3 item 2
    /// atomic replace so the next build can diff against it.
    /// </summary>
    /// <remarks>
    /// The on-disk member set is exactly the summary SPEC §C3 rule 7 names:
    /// <c>generationId</c>, <c>registryHash</c> and <c>operations</c> whose
    /// entries carry <c>id</c>, <c>version</c>, <c>origin</c>
    /// (<c>"standard"</c>/<c>"custom"</c>), <c>assemblyName</c>,
    /// <c>sourceFile</c> (omitted when null — Missing is never serialized as
    /// null), <c>descriptorHash</c> and <c>implementationHash</c>. The file
    /// is Canonical JSON bytes: the same content always lands as the same
    /// byte sequence.
    /// </remarks>
    public sealed record PersistedRegistrySnapshot(
        string GenerationId,
        string RegistryHash,
        IReadOnlyList<PersistedRegistryOperation> Operations)
    {
        /// <summary>The unique-per-build generation identifier ("reg_" + ULID).</summary>
        public string GenerationId { get; init; } =
            string.IsNullOrEmpty(GenerationId)
                ? throw new ArgumentException(
                    "Generation ID must not be null or empty.", nameof(GenerationId))
                : GenerationId;

        /// <summary>The fixed-input content hash of SPEC §C3 rule 6.</summary>
        public string RegistryHash { get; init; } =
            string.IsNullOrEmpty(RegistryHash)
                ? throw new ArgumentException(
                    "Registry hash must not be null or empty.", nameof(RegistryHash))
                : RegistryHash;

        /// <summary>Every persisted Operation entry.</summary>
        public IReadOnlyList<PersistedRegistryOperation> Operations { get; init; } =
            CheckOperations(Operations);

        /// <summary>
        /// Projects a published <see cref="RegistrySnapshot"/> down to the
        /// persisted summary of SPEC §C3 rule 7.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        public static PersistedRegistrySnapshot FromSnapshot(RegistrySnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            var operations = new List<PersistedRegistryOperation>(snapshot.Operations.Count);
            for (var i = 0; i < snapshot.Operations.Count; i++)
            {
                var operation = snapshot.Operations[i];
                operations.Add(new PersistedRegistryOperation(
                    operation.Descriptor.Id,
                    operation.Descriptor.Version,
                    operation.Origin,
                    operation.AssemblyName,
                    operation.SourceFile,
                    operation.DescriptorHash,
                    operation.ImplementationHash));
            }
            return new PersistedRegistrySnapshot(
                snapshot.GenerationId, snapshot.RegistryHash,
                ReadOnlyCollections.CopyList(operations));
        }

        /// <summary>The Canonical JSON form persisted to <c>registry.json</c>.</summary>
        public JObject ToJson()
        {
            var operations = new JArray();
            for (var i = 0; i < Operations.Count; i++)
            {
                var operation = Operations[i];
                var entry = new JObject
                {
                    ["id"] = operation.Id,
                    ["version"] = operation.Version,
                    ["origin"] = JsonNaming.ToSnakeCase(operation.Origin.ToString()),
                    ["assemblyName"] = operation.AssemblyName,
                    ["descriptorHash"] = operation.DescriptorHash,
                    ["implementationHash"] = operation.ImplementationHash,
                };
                if (operation.SourceFile is not null)
                {
                    entry["sourceFile"] = operation.SourceFile;
                }
                operations.Add(entry);
            }
            return new JObject
            {
                ["generationId"] = GenerationId,
                ["registryHash"] = RegistryHash,
                ["operations"] = operations,
            };
        }

        /// <summary>
        /// Reads the persisted summary form of <paramref name="token"/> —
        /// the member-shape half of the <c>registry.json</c> reader. Any
        /// deviation from the written shape fails with a fixed English
        /// reason the caller reports as the <c>previousSnapshot:
        /// "unavailable"</c> reason (SPEC §C3 rule 7); unknown members are
        /// ignored so a newer file is never rejected outright, while a known
        /// member of the wrong type is corruption, never a silent downgrade.
        /// Two consistency checks close the gap between well-formed JSON and
        /// a trustworthy summary: a duplicated (id, version) entry — which
        /// keyed lookups would collapse into false added/removed records —
        /// and a <c>registryHash</c> that does not cover the persisted
        /// operations, recomputed through the single SPEC §C3 rule-6
        /// implementation the writer uses.
        /// </summary>
        public static bool TryRead(JToken token, out PersistedRegistrySnapshot? snapshot, out string? reason)
        {
            snapshot = null;
            reason = null;
            if (token is not JObject root)
            {
                reason = "registry.json is not a JSON object";
                return false;
            }
            if (root["generationId"] is not JValue { Value: string generationId } ||
                string.IsNullOrEmpty(generationId))
            {
                reason = "registry.json member 'generationId' is missing or not a non-empty string";
                return false;
            }
            if (root["registryHash"] is not JValue { Value: string registryHash } ||
                string.IsNullOrEmpty(registryHash))
            {
                reason = "registry.json member 'registryHash' is missing or not a non-empty string";
                return false;
            }
            if (root["operations"] is not JArray entries)
            {
                reason = "registry.json member 'operations' is missing or not an array";
                return false;
            }
            var operations = new List<PersistedRegistryOperation>(entries.Count);
            var seen = new HashSet<(string Id, int Version)>(entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                if (!TryReadOperation(entries[i], i, out var operation, out reason))
                {
                    return false;
                }
                // A repeated (id, version) collapses keyed lookups into
                // false added/removed records — corruption, never
                // "last wins".
                if (!seen.Add((operation!.Id, operation.Version)))
                {
                    reason = "registry.json operations[" + i +
                        "] duplicates an earlier entry's (id, version)";
                    return false;
                }
                operations.Add(operation);
            }
            // SPEC §C3 rule 6 fixes the registryHash input to exactly the
            // persisted (id, version, descriptorHash, implementationHash)
            // fields, so the reader recomputes it: a summary whose declared
            // hash does not cover its operations is internally inconsistent
            // — well-formed JSON, still corruption.
            if (!string.Equals(
                    registryHash,
                    RegistrySnapshotBuilder.ComputeRegistryHash(operations.Select(
                        operation => (operation.Id, operation.Version,
                            operation.DescriptorHash, operation.ImplementationHash))),
                    StringComparison.Ordinal))
            {
                reason =
                    "registry.json member 'registryHash' does not match the persisted operations";
                return false;
            }
            snapshot = new PersistedRegistrySnapshot(
                generationId, registryHash, ReadOnlyCollections.CopyList(operations));
            return true;
        }

        private static bool TryReadOperation(
            JToken token, int index, out PersistedRegistryOperation? operation, out string? reason)
        {
            operation = null;
            reason = null;
            var where = "registry.json operations[" + index + "]";
            if (token is not JObject entry)
            {
                reason = where + " is not a JSON object";
                return false;
            }
            if (entry["id"] is not JValue { Value: string id } || string.IsNullOrEmpty(id))
            {
                reason = where + " member 'id' is missing or not a non-empty string";
                return false;
            }
            if (entry["version"] is not JValue versionToken || versionToken.Type != JTokenType.Integer)
            {
                reason = where + " member 'version' is missing or not an integer";
                return false;
            }
            // An integer the writer could never emit — outside
            // 1..int.MaxValue — is corruption, never a cast to truncate.
            if (versionToken.Value is not long version ||
                version < 1 || version > int.MaxValue)
            {
                reason = where + " member 'version' is outside the supported range";
                return false;
            }
            if (entry["origin"] is not JValue { Value: string originText } ||
                !TryReadOrigin(originText, out var origin))
            {
                reason = where + " member 'origin' is missing or not 'standard'/'custom'";
                return false;
            }
            if (entry["assemblyName"] is not JValue { Value: string assemblyName } ||
                string.IsNullOrEmpty(assemblyName))
            {
                reason = where + " member 'assemblyName' is missing or not a non-empty string";
                return false;
            }
            var sourceFileToken = entry["sourceFile"];
            string? sourceFile = null;
            if (sourceFileToken is not null)
            {
                if (sourceFileToken is not JValue { Value: string file })
                {
                    reason = where + " member 'sourceFile' is not a string";
                    return false;
                }
                sourceFile = file;
            }
            if (entry["descriptorHash"] is not JValue { Value: string descriptorHash } ||
                string.IsNullOrEmpty(descriptorHash))
            {
                reason = where + " member 'descriptorHash' is missing or not a non-empty string";
                return false;
            }
            if (entry["implementationHash"] is not JValue { Value: string implementationHash } ||
                string.IsNullOrEmpty(implementationHash))
            {
                reason = where + " member 'implementationHash' is missing or not a non-empty string";
                return false;
            }
            operation = new PersistedRegistryOperation(
                id, (int)version, origin, assemblyName, sourceFile,
                descriptorHash, implementationHash);
            return true;
        }

        private static bool TryReadOrigin(string text, out OperationOrigin origin)
        {
            switch (text)
            {
                case "standard":
                    origin = OperationOrigin.Standard;
                    return true;
                case "custom":
                    origin = OperationOrigin.Custom;
                    return true;
                default:
                    origin = OperationOrigin.Custom;
                    return false;
            }
        }

        private static IReadOnlyList<PersistedRegistryOperation> CheckOperations(
            IReadOnlyList<PersistedRegistryOperation> operations)
        {
            var copy = ReadOnlyCollections.CopyList(operations);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Operation entries must not be null.", nameof(operations));
                }
            }
            return copy;
        }
    }
}
