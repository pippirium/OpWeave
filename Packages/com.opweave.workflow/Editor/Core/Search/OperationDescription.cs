#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The describe_operation response for one registered Operation (SPEC
    /// §C8, §H0): the (id, version) identity plus every §C8 item — the full
    /// Descriptor with its language-resolved display text, the input/output
    /// §B8 JSON Schemas, the requirement descriptions, the complete-WF
    /// Examples, origin, package provenance, defining assembly, both
    /// fingerprints, the Registry generation ID, the Operation's own
    /// diagnostics and, for a custom or external Operation, its source file.
    /// </summary>
    /// <remarks>
    /// Each section member is null when the request's
    /// <see cref="DescribeFields"/> projection excluded it; the identity
    /// pair is always present. A member's value answers "the item itself" —
    /// a null <see cref="PackageId"/> under a requested
    /// <see cref="DescribeFields.PackageId"/> section means the Operation
    /// ships in no package, exactly as the Registry records it.
    /// </remarks>
    public sealed record OperationDescription(
        string Id,
        int Version)
    {
        private readonly string _id = CheckId(Id);
        private readonly int _version = CheckVersion(Version);
        private IReadOnlyList<DescribedPort>? _inputs;
        private IReadOnlyList<DescribedPort>? _outputs;
        private IReadOnlyList<DescribedRequirement>? _requirements;
        private IReadOnlyList<OperationExample>? _examples;
        private IReadOnlyList<Diagnostic>? _diagnostics;

        /// <summary>The described Operation ID.</summary>
        public string Id
        {
            get => _id;
            init => _id = CheckId(value);
        }

        /// <summary>The described version.</summary>
        public int Version
        {
            get => _version;
            init => _version = CheckVersion(value);
        }

        /// <summary>The full §C1 Descriptor — section
        /// <see cref="DescribeFields.Descriptor"/>.</summary>
        public OperationDescriptor? Descriptor { get; init; }

        /// <summary>The display name resolved for the response language —
        /// section <see cref="DescribeFields.Descriptor"/>.</summary>
        public string? DisplayName { get; init; }

        /// <summary>The summary resolved for the response language — section
        /// <see cref="DescribeFields.Descriptor"/>.</summary>
        public string? Summary { get; init; }

        /// <summary>The Descriptor's English description — section
        /// <see cref="DescribeFields.Descriptor"/>.</summary>
        public string? Description { get; init; }

        /// <summary>The input ports with their §B8 schemas — section
        /// <see cref="DescribeFields.Inputs"/>.</summary>
        public IReadOnlyList<DescribedPort>? Inputs
        {
            get => _inputs;
            init => _inputs = CopyPorts(value);
        }

        /// <summary>The output ports with their §B8 schemas — section
        /// <see cref="DescribeFields.Outputs"/>.</summary>
        public IReadOnlyList<DescribedPort>? Outputs
        {
            get => _outputs;
            init => _outputs = CopyPorts(value);
        }

        /// <summary>The declared requirements with descriptions — section
        /// <see cref="DescribeFields.Requirements"/>.</summary>
        public IReadOnlyList<DescribedRequirement>? Requirements
        {
            get => _requirements;
            init => _requirements = CopyRequirements(value);
        }

        /// <summary>The complete-WF examples, verbatim — section
        /// <see cref="DescribeFields.Examples"/>.</summary>
        public IReadOnlyList<OperationExample>? Examples
        {
            get => _examples;
            init => _examples = CopyExamples(value);
        }

        /// <summary>The registry origin — section
        /// <see cref="DescribeFields.Origin"/>.</summary>
        public OperationOrigin? Origin { get; init; }

        /// <summary>The UPM Package ID when the Operation ships inside a
        /// package — section <see cref="DescribeFields.PackageId"/>.</summary>
        public string? PackageId { get; init; }

        /// <summary>The resolved commit the defining package was installed
        /// at — section <see cref="DescribeFields.PackageId"/>.</summary>
        public string? ResolvedCommit { get; init; }

        /// <summary>The defining assembly's simple name — section
        /// <see cref="DescribeFields.AssemblyName"/>.</summary>
        public string? AssemblyName { get; init; }

        /// <summary>The §C3 rule-6 descriptorHash — section
        /// <see cref="DescribeFields.Fingerprints"/>.</summary>
        public string? DescriptorHash { get; init; }

        /// <summary>The §C3 rule-6 implementationHash — section
        /// <see cref="DescribeFields.Fingerprints"/>.</summary>
        public string? ImplementationHash { get; init; }

        /// <summary>The Registry generation ID of the answering snapshot —
        /// section <see cref="DescribeFields.RegistryGeneration"/>.</summary>
        public string? RegistryGeneration { get; init; }

        /// <summary>The project-relative source file — section
        /// <see cref="DescribeFields.SourceFile"/>; only a custom or external
        /// Operation's provenance carries one.</summary>
        public string? SourceFile { get; init; }

        /// <summary>The diagnostics the registration of this Operation ID
        /// recorded — section <see cref="DescribeFields.Diagnostics"/>; empty
        /// for a clean registration.</summary>
        public IReadOnlyList<Diagnostic>? Diagnostics
        {
            get => _diagnostics;
            init => _diagnostics = CopyDiagnostics(value);
        }

        private static string CheckId(string id) =>
            string.IsNullOrEmpty(id)
                ? throw new ArgumentException(
                    "A description must carry an Operation ID.", nameof(Id))
                : id;

        private static int CheckVersion(int version) =>
            version >= 1
                ? version
                : throw new ArgumentOutOfRangeException(
                    nameof(Version), version, "An Operation version is a positive integer.");

        private static IReadOnlyList<DescribedPort>? CopyPorts(
            IReadOnlyList<DescribedPort>? ports)
        {
            if (ports is null)
            {
                return null;
            }
            var copy = ReadOnlyCollections.CopyList(ports);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Port entries must not be null.", nameof(ports));
                }
            }
            return copy;
        }

        private static IReadOnlyList<DescribedRequirement>? CopyRequirements(
            IReadOnlyList<DescribedRequirement>? requirements)
        {
            if (requirements is null)
            {
                return null;
            }
            var copy = ReadOnlyCollections.CopyList(requirements);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Requirement entries must not be null.", nameof(requirements));
                }
            }
            return copy;
        }

        private static IReadOnlyList<OperationExample>? CopyExamples(
            IReadOnlyList<OperationExample>? examples)
        {
            if (examples is null)
            {
                return null;
            }
            var copy = ReadOnlyCollections.CopyList(examples);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Example entries must not be null.", nameof(examples));
                }
            }
            return copy;
        }

        private static IReadOnlyList<Diagnostic>? CopyDiagnostics(
            IReadOnlyList<Diagnostic>? diagnostics)
        {
            if (diagnostics is null)
            {
                return null;
            }
            var copy = ReadOnlyCollections.CopyList(diagnostics);
            for (var i = 0; i < copy.Count; i++)
            {
                if (copy[i] is null)
                {
                    throw new ArgumentException(
                        "Diagnostic entries must not be null.", nameof(diagnostics));
                }
            }
            return copy;
        }
    }
}
