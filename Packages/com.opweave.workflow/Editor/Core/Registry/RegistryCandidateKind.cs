#nullable enable

namespace OpWeave.Core.Registry
{
    /// <summary>
    /// The kind of candidate a <see cref="RegistryCandidateFailure"/> describes
    /// (SPEC §C3: the "Type/Operation/Provider ID" the diagnostic data must
    /// name).
    /// </summary>
    public enum RegistryCandidateKind
    {
        /// <summary>An <c>[OpWeaveOperation]</c> candidate.</summary>
        Operation,

        /// <summary>A single <see cref="TypeDefinition"/> offer.</summary>
        Type,

        /// <summary>An <see cref="ITypeProvider"/> candidate — the class or
        /// its bound Provider ID, including provider-scope rejections with no
        /// single Type ID.</summary>
        TypeProvider,
    }
}
