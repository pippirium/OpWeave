#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// Entry point of the public fluent Descriptor builder (SPEC §0.6 SG-05).
    /// <see cref="Define"/> starts a chain ending in
    /// <see cref="OperationDescriptorBuilder.Build"/>; Descriptor fields outside
    /// the approved fluent set are set through the
    /// <see cref="OperationDescriptor"/> record itself, not through additional
    /// fluent methods.
    /// </summary>
    public static class Op
    {
        /// <summary>Starts a Descriptor builder for the Operation <paramref name="id"/> and <paramref name="version"/>.</summary>
        /// <exception cref="ArgumentException"><paramref name="id"/> is null or empty.</exception>
        public static OperationDescriptorBuilder Define(string id, int version)
        {
            if (string.IsNullOrEmpty(id))
            {
                throw new ArgumentException(
                    "Operation ID must not be null or empty.", nameof(id));
            }
            return new OperationDescriptorBuilder(id, version);
        }
    }
}
