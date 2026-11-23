#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// JSON-level validation and deterministic normalization of a type's values
    /// (SPEC §B7, §C0.1). This is the single public Codec contract — there is no
    /// second codec interface; CLR round-tripping extends it via
    /// <see cref="IClrValueCodec{T}"/>.
    /// </summary>
    /// <remarks>
    /// Implementations must be free of observable side effects during
    /// validation (SPEC §B0.1) and <see cref="Normalize"/> must satisfy the
    /// determinism, idempotence and round-trip rules of SPEC §B7.1; violations
    /// surface as OW-TYPE-003 before Plan creation and OW-RUN-007 at run time.
    /// </remarks>
    public interface IValueCodec
    {
        /// <summary>
        /// Checks <paramref name="value"/> against the type and the optional
        /// <paramref name="constraints"/> (SPEC §C1 <see cref="PortConstraints"/>),
        /// returning one <see cref="Diagnostic"/> per violation (OW-TYPE-003).
        /// </summary>
        IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints);

        /// <summary>
        /// The deterministic persisted form of an already validated value
        /// (SPEC §B7.1): identical for spellings the type treats as equal,
        /// canonical per SPEC §0.3, idempotent, and without side effects.
        /// <c>core.Json</c> is the only type that keeps input tokens unchanged.
        /// </summary>
        JsonValue Normalize(JsonValue value);
    }
}
