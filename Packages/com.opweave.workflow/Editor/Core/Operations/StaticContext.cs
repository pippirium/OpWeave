#nullable enable

using System.Collections.Generic;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The read-only context handed to <see cref="IOperation.ValidateStatic"/>
    /// (and to <see cref="IEffectResolver"/>): the owner-approved Core Context
    /// surface of SPEC §0.6 SG-06. It exposes exactly
    /// <see cref="NodeId"/>, <see cref="Parameters"/>, <see cref="Get{T}"/>
    /// and <see cref="StaticTypeOf"/> — no mutation operations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implementations live in the UnityAdapter (and in test fixtures); Core
    /// never hosts a Unity-specific CLR/Undo/Console implementation
    /// (SPEC §0.6 SG-06). Missing parameters are never surfaced as a
    /// <see langword="null"/> value: <see cref="Parameters"/> values carry the
    /// <see cref="InputValue.IsPresent"/> distinction, and a caller tests
    /// presence instead of comparing against <see langword="null"/>.
    /// </para>
    /// </remarks>
    public abstract class StaticContext
    {
        /// <summary>The id of the WF node being validated.</summary>
        public abstract string NodeId { get; }

        /// <summary>
        /// The node's resolved parameters keyed by input port name, as an
        /// immutable dictionary. A port whose value was not supplied is
        /// represented as <see cref="InputValue.Missing"/> — never as JSON
        /// <c>null</c> (SPEC §A0.1, §0.6 SG-06); an explicit JSON <c>null</c>
        /// stays a present value.
        /// </summary>
        public abstract IReadOnlyDictionary<string, InputValue> Parameters { get; }

        /// <summary>
        /// Reads one input port as <typeparamref name="T"/> (SPEC §C2 item 5).
        /// A missing port or a type mismatch is reported through the
        /// implementation's type/port diagnostics (SPEC §0.6 SG-06), not by
        /// returning <see langword="null"/>.
        /// </summary>
        public abstract T Get<T>(string port);

        /// <summary>
        /// The statically resolved type of one input port (SPEC §0.6 SG-06,
        /// added 2026-11-06): a port supplied by a data edge reports the
        /// connected output's type as the D4 phase resolved it, a port bound
        /// to a workflow input reports that input's declared type, a constant
        /// or defaulted port reports the port's declared type, and a Missing
        /// port reports <see langword="null"/>. The answer is a type, never a
        /// value, and there are no mutation operations; a Resolver consults it
        /// for every input including data-edge-fed ones (SPEC §C4).
        /// </summary>
        /// <remarks>
        /// The default returns <see langword="null"/> — "not statically
        /// known" — so existing implementations keep compiling and never
        /// report a wrong type; the contexts the compiler phases build
        /// override it with the resolved answers.
        /// </remarks>
        /// <param name="port">The input port name.</param>
        /// <returns>
        /// The port's statically resolved <see cref="TypeRef"/>, or
        /// <see langword="null"/> when the port is Missing or its type is not
        /// statically known.
        /// </returns>
        public virtual TypeRef? StaticTypeOf(string port) => null;
    }
}
