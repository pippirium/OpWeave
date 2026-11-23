#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The built-in Codec for the open generic definition <c>core.List&lt;&gt;</c>
    /// (SPEC §B3): accepts JSON array tokens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Codec attached to the arity-1 <em>definition</em> never sees the
    /// closed element type, so it checks only the array shape and port-level
    /// constraints. On a closed use such as <c>core.List&lt;core.Float&gt;</c>
    /// SPEC §B3 requires every element to have type <c>T</c>, so this codec
    /// implements <see cref="IClosedGenericCodec"/>: <see cref="TypeRegistry"/>
    /// binds a <see cref="BoundListCodec"/> to the resolved element type and
    /// returns it as the closed instantiation's Codec — this open, shape-only
    /// codec is never the Codec a closed list resolves to.
    /// </para>
    /// </remarks>
    internal sealed class ListCodec : IClosedGenericCodec, ITypeSchemaCodec
    {
        internal static readonly ListCodec Instance = new ListCodec();

        private ListCodec()
        {
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The open definition knows only the array shape — the bound
        /// element type is emitted by <see cref="BoundListCodec"/>.
        /// </remarks>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs) => schema["type"] = "array";

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var diagnostics = new List<Diagnostic>();
            var token = value.ToToken();
            if (token.Type != JTokenType.Array)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Value is not a JSON array."));
            }
            CodecConstraints.Check(token, constraints, diagnostics);
            return diagnostics;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public JsonValue Normalize(JsonValue value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            return value;
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="closedType"/> or <paramref name="registry"/> is null.
        /// </exception>
        public Result<IValueCodec> Close(TypeRef closedType, ITypeRegistry registry)
        {
            if (closedType is null)
            {
                throw new ArgumentNullException(nameof(closedType));
            }
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }
            // core.List<> is arity 1, so a closed use carries exactly the
            // element type. It must resolve in the same snapshot — for a
            // nested core.List<core.List<T>> this recursion binds the inner
            // list's Codec as well. An unresolvable element type fails the
            // closed resolution with the element's diagnostics.
            var elementType = closedType.Args[0];
            var element = registry.Resolve(elementType);
            return element.Ok
                ? Result<IValueCodec>.Success(new BoundListCodec(elementType, element.Value!))
                : Result<IValueCodec>.Failure(element.Diagnostics);
        }
    }

    /// <summary>
    /// Internal extension of <see cref="IValueCodec"/> for a Codec that must
    /// bind the resolved generic arguments of a closed use before it can
    /// validate and normalize — in v1 only the built-in <c>core.List&lt;T&gt;</c>
    /// element binding of SPEC §B3. <see cref="TypeRegistry.Resolve"/> invokes
    /// <see cref="Close"/> for a closed instantiation and returns the bound
    /// Codec on the resolved <see cref="TypeDefinition"/>; the contract is
    /// internal, so custom Codecs cannot implement it and never see it.
    /// </summary>
    internal interface IClosedGenericCodec : IValueCodec
    {
        /// <summary>
        /// The Codec bound to <paramref name="closedType"/>, or a failure
        /// carrying the diagnostics produced when a generic argument does not
        /// resolve in <paramref name="registry"/>.
        /// </summary>
        Result<IValueCodec> Close(TypeRef closedType, ITypeRegistry registry);
    }

    /// <summary>
    /// The Codec of a closed <c>core.List&lt;T&gt;</c> (SPEC §B3): checks the
    /// array shape and the port constraints, then validates every element
    /// through <see cref="ValueValidation.ValidateResolved"/> — the same single
    /// element logic the framework applies at a port position, minus the
    /// nullable flag (an explicit element <c>null</c> is accepted only for a
    /// <c>core.Json</c> element type) — and normalizes every element with the
    /// element Codec (SPEC §B7.1). Diagnostics carry a
    /// <see cref="Diagnostic.JsonPath"/> rooted at the list value
    /// (<c>$[i]</c>), composing for nested lists (<c>$[i][j]</c>).
    /// </summary>
    /// <remarks>
    /// The element definition was resolved by <see cref="TypeRegistry"/> when
    /// this codec was bound, so a nested <c>core.List&lt;T&gt;</c> element type
    /// already carries its own <see cref="BoundListCodec"/> — no recursion or
    /// registry access happens here.
    /// </remarks>
    internal sealed class BoundListCodec : ITypeSchemaCodec
    {
        private readonly TypeRef _elementType;
        private readonly TypeDefinition _element;

        internal BoundListCodec(TypeRef elementType, TypeDefinition element)
        {
            _elementType = elementType;
            _element = element;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// The element type's fragment goes into <c>$defs</c> and
        /// <c>items</c> points at it (SPEC §B8).
        /// </remarks>
        public void WriteSchema(JObject schema, TypeSchemaDefs defs)
        {
            schema["type"] = "array";
            schema["items"] = defs.RefFor(_elementType);
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
            ValidateElements(value, constraints, null);

        // Validate plus element-location recording: every returned
        // diagnostic gets a parallel innerPaths entry carrying the element
        // path the framework composed for it ("$[i]"), or null when the
        // diagnostic describes the list value itself. ValueValidation uses
        // it to keep the framework share of a revalidation diagnostic's
        // JsonPath across the IValueCodec boundary without storing it on
        // Diagnostic or parsing codec-supplied segments back apart
        // (SPEC §B7.1 item 4).
        internal IReadOnlyList<Diagnostic> ValidateLocated(
            JsonValue value, PortConstraints? constraints, List<string?> innerPaths) =>
            ValidateElements(value, constraints, innerPaths);

        private IReadOnlyList<Diagnostic> ValidateElements(
            JsonValue value, PortConstraints? constraints, List<string?>? innerPaths)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var diagnostics = new List<Diagnostic>();
            var token = value.ToToken();
            if (token.Type != JTokenType.Array)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Value is not a JSON array."));
                CodecConstraints.Check(token, constraints, diagnostics);
                PadLocations(innerPaths, diagnostics.Count);
                return diagnostics;
            }
            CodecConstraints.Check(token, constraints, diagnostics);
            PadLocations(innerPaths, diagnostics.Count);
            var array = (JArray)token;
            for (var i = 0; i < array.Count; i++)
            {
                ValueValidation.ValidateResolved(
                    _elementType,
                    _element,
                    JsonValue.From(array[i]),
                    null,
                    false,
                    ValueValidation.ElementPath("$", i),
                    diagnostics,
                    innerPaths);
            }
            return diagnostics;
        }

        // Pads innerPaths with nulls for the diagnostics about the list
        // value itself — their location is the codec's own root, not a
        // framework-composed element path.
        private static void PadLocations(List<string?>? innerPaths, int count)
        {
            if (innerPaths is null)
            {
                return;
            }
            for (var i = innerPaths.Count; i < count; i++)
            {
                innerPaths.Add(null);
            }
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
        public JsonValue Normalize(JsonValue value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var token = value.ToToken();
            if (token.Type != JTokenType.Array)
            {
                return value;
            }
            var source = (JArray)token;
            var normalized = new JArray();
            for (var i = 0; i < source.Count; i++)
            {
                normalized.Add(_element.Codec.Normalize(JsonValue.From(source[i])).ToToken());
            }
            return JsonValue.From(normalized);
        }
    }
}
