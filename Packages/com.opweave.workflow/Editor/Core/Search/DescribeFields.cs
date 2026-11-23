#nullable enable

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The section names <see cref="DescribeOperationRequest.Fields"/> selects
    /// on a describe_operation response (SPEC §C8). Every item §C8 enumerates
    /// is a named section; <see cref="DescribeOperationRequest.Fields"/> = null
    /// returns them all, while a supplied list returns only the named
    /// sections plus the (id, version) identity — unknown names are ignored
    /// rather than rejected (a local, documented choice: §C8 constrains the
    /// example names <c>inputs</c>/<c>outputs</c>/<c>examples</c> only).
    /// </summary>
    public static class DescribeFields
    {
        /// <summary>The full <see cref="OpWeave.Core.Operations.OperationDescriptor"/>
        /// and its language-resolved display text.</summary>
        public const string Descriptor = "descriptor";

        /// <summary>The input port contracts with their §B8 JSON Schemas.</summary>
        public const string Inputs = "inputs";

        /// <summary>The output port contracts with their §B8 JSON Schemas.</summary>
        public const string Outputs = "outputs";

        /// <summary>The declared requirements with fixed-English descriptions.</summary>
        public const string Requirements = "requirements";

        /// <summary>The <see cref="OpWeave.Core.Operations.OperationExample"/>
        /// values, verbatim.</summary>
        public const string Examples = "examples";

        /// <summary>The registry origin (Standard/Custom).</summary>
        public const string Origin = "origin";

        /// <summary>The UPM Package ID and resolved commit of a packaged
        /// Operation.</summary>
        public const string PackageId = "packageId";

        /// <summary>The defining assembly's simple name.</summary>
        public const string AssemblyName = "assemblyName";

        /// <summary>The §C3 rule-6 descriptorHash and implementationHash.</summary>
        public const string Fingerprints = "fingerprints";

        /// <summary>The Registry generation ID of the described snapshot.</summary>
        public const string RegistryGeneration = "registryGeneration";

        /// <summary>The Operation's own registration diagnostics.</summary>
        public const string Diagnostics = "diagnostics";

        /// <summary>The project-relative source file a custom or external
        /// Operation's provenance carries.</summary>
        public const string SourceFile = "sourceFile";

        /// <summary>Every section name, in the §C8 enumeration's order.</summary>
        public static readonly string[] All =
        {
            Descriptor,
            Inputs,
            Outputs,
            Requirements,
            Examples,
            Origin,
            PackageId,
            AssemblyName,
            Fingerprints,
            RegistryGeneration,
            Diagnostics,
            SourceFile,
        };
    }
}
