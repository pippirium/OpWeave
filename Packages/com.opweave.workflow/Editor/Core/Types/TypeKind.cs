#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// The kind of a <see cref="TypeDefinition"/> (SPEC §B2). The set is closed:
    /// introducing a new kind is a Core public-API contract change, not something an
    /// external extension can do on its own (SPEC §X.6).
    /// </summary>
    public enum TypeKind
    {
        Primitive,
        Value,
        Ref,
        List,
        Class,
        Json,
    }
}
