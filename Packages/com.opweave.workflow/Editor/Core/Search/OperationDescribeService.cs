#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The SPEC §C8 describe_operation service over one immutable
    /// <see cref="RegistrySnapshot"/>: <see cref="DescribeOperation"/>
    /// returns the complete contract of one registered Operation — the full
    /// Descriptor, the input/output JSON Schemas §B8 generates, the
    /// requirement descriptions, the complete-WF Examples, provenance,
    /// fingerprints, the Registry generation ID and the Operation's own
    /// diagnostics — called directly, below the Agent API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution: an omitted <see cref="DescribeOperationRequest.Version"/>
    /// answers the newest registered version; a requested version nothing
    /// registered answers OW-OP-002; an ID nothing registered answers
    /// OW-OP-001 — with the registration failure's code and source file in
    /// the hint when the ID names a rejected candidate of the same build
    /// (SPEC §C8).
    /// </para>
    /// <para>
    /// The <see cref="DescribeFields"/> projection narrows the populated
    /// sections of <see cref="OperationDescription"/>; the (id, version)
    /// identity always returns. A null request <c>Fields</c> returns every
    /// section; unknown section names are ignored — a local, documented
    /// choice, since §C8 fixes the section contents and the
    /// <c>inputs</c>/<c>outputs</c>/<c>examples</c> example but leaves the
    /// projection's other names to the wire contract.
    /// </para>
    /// </remarks>
    public sealed class OperationDescribeService
    {
        private readonly RegistrySnapshot _snapshot;

        /// <summary>Binds the service to one published snapshot.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        public OperationDescribeService(RegistrySnapshot snapshot)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        /// <summary>The snapshot this service describes.</summary>
        public RegistrySnapshot Snapshot => _snapshot;

        /// <summary>
        /// Describes one registered Operation (SPEC §C8). The only failures
        /// are the §C8 lookups: OW-OP-001 for an ID no registered version
        /// answers — the hint naming the rejection's code and source file
        /// when the ID failed registration in this build — and OW-OP-002 for
        /// a registered ID at an unregistered version. A port type or schema
        /// failure a validated snapshot cannot produce surfaces as the
        /// underlying OW-TYPE diagnostic unchanged.
        /// </summary>
        /// <param name="request">The ID, optional version, projection and language.</param>
        /// <param name="defaultLanguage">The caller's <c>agentLanguage</c>
        /// default applied when <see cref="DescribeOperationRequest.Lang"/> is
        /// null (the settings value lives in OpWeave.UnityAdapter, which Core
        /// cannot reference); English is used when both are absent.</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public Result<OperationDescription> DescribeOperation(
            DescribeOperationRequest request, string? defaultLanguage = null)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            RegisteredOperation? operation;
            if (request.Version is { } version)
            {
                operation = _snapshot.Find(request.Id, version);
                if (operation is null)
                {
                    return _snapshot.FindLatest(request.Id) is null
                        ? Result<OperationDescription>.Failure(UnknownOperation(request.Id))
                        : Result<OperationDescription>.Failure(
                            UnsupportedVersion(request.Id, version));
                }
            }
            else
            {
                operation = _snapshot.FindLatest(request.Id);
                if (operation is null)
                {
                    return Result<OperationDescription>.Failure(UnknownOperation(request.Id));
                }
            }

            var wants = request.Fields is null
                ? null
                : new HashSet<string>(request.Fields, StringComparer.Ordinal);
            var descriptor = operation.Descriptor;
            var language = request.Lang ?? defaultLanguage ?? "en";

            IReadOnlyList<DescribedPort>? inputs = null;
            IReadOnlyList<DescribedPort>? outputs = null;
            if (Wants(wants, DescribeFields.Inputs))
            {
                var ports = DescribePorts(descriptor.Inputs);
                if (!ports.Ok)
                {
                    return Result<OperationDescription>.Failure(ports.Diagnostics);
                }
                inputs = ports.Value!;
            }
            if (Wants(wants, DescribeFields.Outputs))
            {
                var ports = DescribePorts(descriptor.Outputs);
                if (!ports.Ok)
                {
                    return Result<OperationDescription>.Failure(ports.Diagnostics);
                }
                outputs = ports.Value!;
            }
            IReadOnlyList<DescribedRequirement>? requirements = null;
            if (Wants(wants, DescribeFields.Requirements))
            {
                var list = new List<DescribedRequirement>(descriptor.Requirements.Count);
                for (var i = 0; i < descriptor.Requirements.Count; i++)
                {
                    var requirement = descriptor.Requirements[i];
                    list.Add(new DescribedRequirement(
                        requirement, DescribeRequirement(requirement)));
                }
                requirements = list;
            }

            var description = new OperationDescription(
                descriptor.Id,
                descriptor.Version)
            {
                Descriptor = Wants(wants, DescribeFields.Descriptor) ? descriptor : null,
                DisplayName = Wants(wants, DescribeFields.Descriptor)
                    ? ResolveText(descriptor, language).DisplayName
                    : null,
                Summary = Wants(wants, DescribeFields.Descriptor)
                    ? ResolveText(descriptor, language).Summary
                    : null,
                Description = Wants(wants, DescribeFields.Descriptor)
                    ? descriptor.Description
                    : null,
                Inputs = inputs,
                Outputs = outputs,
                Requirements = requirements,
                Examples = Wants(wants, DescribeFields.Examples)
                    ? descriptor.Examples
                    : null,
                Origin = Wants(wants, DescribeFields.Origin)
                    ? operation.Origin
                    : (OperationOrigin?)null,
                PackageId = Wants(wants, DescribeFields.PackageId)
                    ? operation.PackageId
                    : null,
                ResolvedCommit = Wants(wants, DescribeFields.PackageId)
                    ? operation.ResolvedCommit
                    : null,
                AssemblyName = Wants(wants, DescribeFields.AssemblyName)
                    ? operation.AssemblyName
                    : null,
                DescriptorHash = Wants(wants, DescribeFields.Fingerprints)
                    ? operation.DescriptorHash
                    : null,
                ImplementationHash = Wants(wants, DescribeFields.Fingerprints)
                    ? operation.ImplementationHash
                    : null,
                RegistryGeneration = Wants(wants, DescribeFields.RegistryGeneration)
                    ? _snapshot.GenerationId
                    : null,
                Diagnostics = Wants(wants, DescribeFields.Diagnostics)
                    ? OperationDiagnostics(descriptor.Id)
                    : null,
                SourceFile = Wants(wants, DescribeFields.SourceFile)
                    ? operation.SourceFile
                    : null,
            };
            return Result<OperationDescription>.Success(description);
        }

        private static bool Wants(HashSet<string>? fields, string section) =>
            fields is null || fields.Contains(section);

        // The §B8 schema of every port, in declared order; a port type that
        // fails to parse or resolve fails the call with its OW-TYPE
        // diagnostic — an outcome a validated snapshot cannot produce.
        private Result<IReadOnlyList<DescribedPort>> DescribePorts(
            IReadOnlyList<PortDescriptor> ports)
        {
            var described = new List<DescribedPort>(ports.Count);
            for (var i = 0; i < ports.Count; i++)
            {
                var port = ports[i];
                var parsed = TypeRef.Parse(port.Type);
                if (!parsed.Ok)
                {
                    return Result<IReadOnlyList<DescribedPort>>.Failure(parsed.Diagnostics);
                }
                var schema = TypeSchemas.Generate(
                    _snapshot.Types, parsed.Value!, port.Constraints,
                    port.Description, port.Nullable);
                if (!schema.Ok)
                {
                    return Result<IReadOnlyList<DescribedPort>>.Failure(schema.Diagnostics);
                }
                described.Add(new DescribedPort(port, schema.Value!));
            }
            return Result<IReadOnlyList<DescribedPort>>.Success(described);
        }

        // The fixed-English description of a SPEC §G1 requirement kind —
        // the 前提条件の説明 §C8 returns beside the schemas. A kind outside
        // the closed §G1 vocabulary never registers (OW-OP-003); its
        // fallback names the kind rather than dropping the requirement.
        private static string DescribeRequirement(Requirement requirement) =>
            requirement.Kind switch
            {
                "edit_mode" => "Requires the Editor to be in Edit Mode.",
                "not_compiling" =>
                    "Requires the Editor to not be compiling scripts; " +
                    "the step waits until compilation finishes.",
                "scene_loaded" => requirement.Port is null
                    ? "Requires a scene to be loaded."
                    : $"Requires the scene the '{requirement.Port}' port names to be loaded.",
                "path_writable" => requirement.Port is null
                    ? "Requires a path to be writable under the allowed paths."
                    : $"Requires the path the '{requirement.Port}' port names " +
                        "to be writable under the allowed paths.",
                "type_exists" => requirement.Port is null
                    ? "Requires a type to exist in the project."
                    : $"Requires the type the '{requirement.Port}' port names " +
                        "to exist in the project.",
                _ => $"Requires the '{requirement.Kind}' precondition.",
            };

        // The §C8 item-5 language choice describe shares with §C6: the
        // localized text of the requested language when one is registered,
        // English otherwise.
        private static (string DisplayName, string Summary) ResolveText(
            OperationDescriptor descriptor, string language)
        {
            if (descriptor.Localized.TryGetValue(language, out var text))
            {
                return (text.DisplayName, text.Summary);
            }
            return (descriptor.DisplayName, descriptor.Summary);
        }

        // Every diagnostic this build's registration recorded against the
        // Operation ID — the §C8 "その Operation に関する diagnostics": the
        // candidate-scope and change-scope rejection records of the build,
        // deduplicated (a builder rejection lands in both lists verbatim).
        // A registered ID ordinarily has none; records remain for the
        // versions of the same ID the build rejected.
        private IReadOnlyList<Diagnostic> OperationDiagnostics(string id)
        {
            var diagnostics = new List<Diagnostic>();
            var seen = new HashSet<Diagnostic>();
            var failures = _snapshot.CandidateFailures;
            for (var i = 0; i < failures.Count; i++)
            {
                var failure = failures[i];
                if (failure.Kind == RegistryCandidateKind.Operation &&
                    string.Equals(failure.CandidateId, id, StringComparison.Ordinal))
                {
                    Add(failure.Diagnostics, diagnostics, seen);
                }
            }
            var changes = _snapshot.Failures;
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (change.Kind == RegistryChangeKinds.Failed &&
                    string.Equals(change.OperationId, id, StringComparison.Ordinal))
                {
                    Add(change.Diagnostics, diagnostics, seen);
                }
            }
            return diagnostics;
        }

        private static void Add(
            IReadOnlyList<Diagnostic> source, List<Diagnostic> target,
            HashSet<Diagnostic> seen)
        {
            for (var i = 0; i < source.Count; i++)
            {
                if (seen.Add(source[i]))
                {
                    target.Add(source[i]);
                }
            }
        }

        // OW-OP-001: no registered version answers the ID at all. When the
        // ID names a candidate this build rejected, the hint carries the
        // rejection's published code and source file (SPEC §C8); a
        // never-offered or deliberately excluded ID gets the generic hint —
        // exclusions are Registry state, never failures (§C3 rule 5), so
        // they are not surfaced as registration failures. The ID is
        // published only in its safe form (§C3 error paragraph).
        private Diagnostic UnknownOperation(string id)
        {
            var safeId = PublishedId.OperationId(id);
            var hint = RegistrationFailureHint(id) ?? UnknownHint(id);
            return ErrorCodes.Create(
                ErrorCodes.OW_OP_001,
                $"Unknown operation '{safeId}'.",
                hint: hint);
        }

        private string? RegistrationFailureHint(string id)
        {
            var reasons = new List<string>();
            var failures = _snapshot.CandidateFailures;
            for (var i = 0; i < failures.Count; i++)
            {
                var failure = failures[i];
                if (failure.Kind == RegistryCandidateKind.Operation &&
                    string.Equals(failure.CandidateId, id, StringComparison.Ordinal))
                {
                    reasons.Add(ReasonText(failure.Diagnostics, failure.SourceFile));
                }
            }
            var changes = _snapshot.Failures;
            for (var i = 0; i < changes.Count; i++)
            {
                var change = changes[i];
                if (change.Kind == RegistryChangeKinds.Failed &&
                    string.Equals(change.OperationId, id, StringComparison.Ordinal) &&
                    !reasons.Contains(ReasonText(change.Diagnostics, change.SourceFile)))
                {
                    reasons.Add(ReasonText(change.Diagnostics, change.SourceFile));
                }
            }
            return reasons.Count == 0
                ? null
                : "The operation failed registration: " +
                    string.Join("; ", reasons) + ".";
        }

        // One "code in 'file'" reason for a rejection record: the record's
        // distinct published codes (registration internal diagnostics carry
        // no code and are omitted), then its project-relative source file
        // when one was learned — the (code, source file) pair SPEC §C8 makes
        // the hint carry.
        private static string ReasonText(
            IReadOnlyList<Diagnostic> diagnostics, string? sourceFile)
        {
            var codes = new List<string>(diagnostics.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var code = diagnostics[i].Code;
                if (code.Length != 0 && seen.Add(code))
                {
                    codes.Add(code);
                }
            }
            var reason = codes.Count == 0
                ? "an unpublished diagnostic"
                : string.Join(", ", codes);
            return sourceFile is null ? reason : reason + $" in '{sourceFile}'";
        }

        private static string UnknownHint(string id) =>
            id.StartsWith("core.", StringComparison.Ordinal)
                ? "This version of OpWeave does not support branching or other " +
                    "control-flow Operations."
                : "Register the operation before describing it, or fix the operation id.";

        // OW-OP-002: the ID registers but not at the requested version — the
        // same answer the §D3 phase gives, migration hint included.
        private Diagnostic UnsupportedVersion(string id, int version)
        {
            var latest = _snapshot.FindLatest(id)!;
            string hint;
            if (version < latest.Descriptor.Version)
            {
                // SPEC §A7 item 3: suggest migration; the service never
                // applies migrations itself.
                hint = $"Run 'opweave migrate_workflow' to migrate to version " +
                    $"{latest.Descriptor.Version}.";
            }
            else
            {
                var versions = new List<int>();
                var operations = _snapshot.Operations;
                for (var i = 0; i < operations.Count; i++)
                {
                    if (string.Equals(
                            operations[i].Descriptor.Id, id, StringComparison.Ordinal))
                    {
                        versions.Add(operations[i].Descriptor.Version);
                    }
                }
                hint = "The operation is registered at version(s) " +
                    string.Join(", ", versions) + ".";
            }
            return ErrorCodes.Create(
                ErrorCodes.OW_OP_002,
                $"Operation '{PublishedId.OperationId(id)}' does not have a " +
                    $"registered version {version}.",
                hint: hint);
        }
    }
}
