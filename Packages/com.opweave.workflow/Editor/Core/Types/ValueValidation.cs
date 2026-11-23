#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core
{
    /// <summary>
    /// The value-level entry point of the codec framework (SPEC §B7, §A5,
    /// §B0.1): validates a <see cref="JsonValue"/> against a resolved type and
    /// the port's constraints, and produces the canonical stored value of the
    /// built-in codecs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Validate"/> composes the rules a port carries but an
    /// <see cref="IValueCodec"/> signature cannot express: the SPEC §A5/§B0.1
    /// null rule, which admits an explicit <c>null</c> only for
    /// <c>core.Json</c> or a <c>PortDescriptor.Nullable=true</c> port and is
    /// taken here as the <c>nullable</c> flag. The closed-type element checks
    /// of generic <see cref="TypeKind.List"/> definitions
    /// (<c>core.List&lt;T&gt;</c> in v1) live in the Codec the registry binds
    /// to a closed instantiation (<see cref="BoundListCodec"/>), which runs
    /// each element through <see cref="ValidateResolved"/> — the same single
    /// logic applied at the port position — so element <c>null</c> follows the
    /// null rule without a port-level flag (<c>core.Json</c> elements admit
    /// <c>null</c>, every other element type rejects it).
    /// </para>
    /// <para>
    /// An explicit <c>null</c> accepted through the nullable flag ends
    /// validation there — constraints restrict actual data, not the
    /// sanctioned absence of a value. On <c>core.Json</c>, <c>null</c> is
    /// ordinary data and still flows through the Codec and its constraints.
    /// Element diagnostics carry a <see cref="Diagnostic.JsonPath"/> rooted at
    /// the validated value (<c>$[i]</c>, nested as <c>$[i][j]</c>); direct
    /// violations leave <see cref="Diagnostic.JsonPath"/> unset for the
    /// caller to attach port context.
    /// </para>
    /// <para>
    /// <see cref="Normalize"/> applies the resolved definition's
    /// <see cref="IValueCodec.Normalize"/>; for a closed
    /// <c>core.List&lt;T&gt;</c> that Codec is element-bound, so a
    /// <c>core.List&lt;core.Float&gt;</c> written as <c>[0,1]</c> persists as
    /// <c>[0e0,1e0]</c> (SPEC §B7.1). Per SPEC §B7.1 the input must already
    /// satisfy <see cref="Validate"/>; the Codec's output is then re-checked
    /// under the same constraints and null rule and must be idempotent, and
    /// any contract breach — a thrown exception, a null or invalid result, a
    /// non-idempotent result — fails closed as a single sanitized OW-TYPE-003
    /// (SPEC §B7.1 item 4).
    /// </para>
    /// </remarks>
    public static class ValueValidation
    {
        /// <summary>
        /// Validates <paramref name="value"/> for the port position typed
        /// <paramref name="type"/> under <paramref name="constraints"/> and the
        /// <paramref name="nullable"/> flag, returning one
        /// <see cref="Diagnostic"/> per violation (OW-TYPE-003, OW-TYPE-001 for
        /// a type that does not resolve).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registry"/>, <paramref name="type"/> or
        /// <paramref name="value"/> is null.
        /// </exception>
        public static IReadOnlyList<Diagnostic> Validate(
            ITypeRegistry registry,
            TypeRef type,
            JsonValue value,
            PortConstraints? constraints,
            bool nullable)
        {
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var resolved = registry.Resolve(type);
            if (!resolved.Ok)
            {
                return resolved.Diagnostics;
            }
            var diagnostics = new List<Diagnostic>();
            ValidateResolved(type, resolved.Value!, value, constraints, nullable, "$", diagnostics);
            return diagnostics;
        }

        /// <summary>
        /// The typed persisted form of an already validated
        /// <paramref name="value"/> (SPEC §B7.1): the resolved definition's
        /// <see cref="IValueCodec.Normalize"/>. Fails with OW-TYPE-001 when
        /// <paramref name="type"/> — including a List's element type — does not
        /// resolve in <paramref name="registry"/>.
        /// </summary>
        /// <remarks>
        /// The pre-§B7.1 signature, kept for callers compiled against it: it
        /// delegates to the guarded contract with no port constraints and
        /// treats the position as nullable, so an explicit <c>null</c> passes
        /// through unchanged — the observable result the Codec produced when
        /// it saw the token directly.
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registry"/>, <paramref name="type"/> or
        /// <paramref name="value"/> is null.
        /// </exception>
        public static Result<JsonValue> Normalize(
            ITypeRegistry registry,
            TypeRef type,
            JsonValue value) =>
            Normalize(registry, type, value, null, true);

        /// <summary>
        /// The typed persisted form of an already validated
        /// <paramref name="value"/> (SPEC §B7.1): the resolved definition's
        /// <see cref="IValueCodec.Normalize"/> applied under the deterministic
        /// normalization contract. The Codec's result must be non-null, must
        /// satisfy the same checks the input passed — the
        /// <paramref name="nullable"/> null rule and
        /// <paramref name="constraints"/> — and must be idempotent:
        /// <c>Normalize</c> of the normalized value must return an identical
        /// CanonicalText. A Codec that throws, returns null, produces a value
        /// failing that revalidation or breaks idempotence violates SPEC §B7.1
        /// item 4 and fails closed with a single sanitized OW-TYPE-003 whose
        /// text never embeds value or exception content, which may carry
        /// secrets. The Codec may be invoked twice for the idempotence check;
        /// side effects during normalization are a contract violation. Fails
        /// with OW-TYPE-001 when <paramref name="type"/> — including a List's
        /// element type — does not resolve in <paramref name="registry"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registry"/>, <paramref name="type"/> or
        /// <paramref name="value"/> is null.
        /// </exception>
        public static Result<JsonValue> Normalize(
            ITypeRegistry registry,
            TypeRef type,
            JsonValue value,
            PortConstraints? constraints,
            bool nullable)
        {
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }
            var resolved = registry.Resolve(type);
            if (!resolved.Ok)
            {
                return Result<JsonValue>.Failure(resolved.Diagnostics);
            }
            var definition = resolved.Value!;

            // The SPEC §A5/§B0.1 null rule applies at the normalization
            // boundary before the Codec runs: an explicit null admitted
            // through the nullable flag is the sanctioned absence of a
            // value and is preserved verbatim — the Codec contract covers
            // actual data, and a Codec may reject null or promote it to a
            // non-null normal form. On core.Json null is ordinary data and
            // still flows through the Codec. A null the rule does not
            // admit could not have passed Validate; it fails closed here
            // with the same diagnostic rather than reaching the Codec.
            var token = value.ToToken();
            if (token.Type == JTokenType.Null && !IsCoreJson(type))
            {
                return nullable
                    ? Result<JsonValue>.Success(value)
                    : Result<JsonValue>.Failure(ErrorCodes.Create(
                        ErrorCodes.OW_TYPE_003,
                        "Explicit null is not accepted for this type."));
            }

            JsonValue normalized;
            try
            {
                normalized = definition.Codec.Normalize(value);
            }
            catch (Exception)
            {
                return ContractViolation(type, "threw an exception during normalization.");
            }
            if (normalized is null)
            {
                return ContractViolation(type, "returned null during normalization.");
            }

            // SPEC §B7.1 item 4: a JSON null the Codec produced is an
            // unexpected null, not the sanctioned absence the nullable
            // flag admits — the sanctioned case returned above before
            // the Codec ran, so reaching this point means the input was
            // non-null (or core.Json data, excluded here since null is
            // ordinary data its own Codec's Validate decides on). On a
            // Nullable port the null rule inside the revalidation below
            // would accept that produced null and silently erase the
            // value; reject it first. On a non-Nullable port the
            // revalidation's null rule already rejects the same result.
            if (nullable &&
                !IsCoreJson(type) &&
                normalized.ToToken().Type == JTokenType.Null)
            {
                return ContractViolation(
                    type, "produced an unexpected null during normalization.");
            }

            // SPEC §B7.1 item 4: the normalized value must still satisfy the
            // validation the input passed — the same null rule and port
            // constraints — so a Codec cannot move an invalid value or an
            // unexpected explicit null into the persisted form.
            var revalidation = new List<Diagnostic>();
            var frameworkPaths = new List<string?>();
            ValidateResolved(
                type, definition, normalized, constraints, nullable, "$",
                revalidation, frameworkPaths);
            if (revalidation.Count != 0)
            {
                // The location on the sanitized failure is the framework
                // path recorded alongside each diagnostic during
                // composition, never the codec-supplied JsonPath: a Codec
                // can build that path from the rejected value, and a
                // numeric bracket segment it emits is indistinguishable
                // from a framework element index once flattened into the
                // string (SPEC §B7.1 item 4).
                var frameworkPath = frameworkPaths[0];
                return Result<JsonValue>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    $"The codec for type '{type}' produced a normalized value that fails validation.",
                    jsonPath: frameworkPath == "$" ? null : frameworkPath));
            }

            // SPEC §B7.1 item 3: Normalize is idempotent on its own output.
            JsonValue twice;
            try
            {
                twice = definition.Codec.Normalize(normalized);
            }
            catch (Exception)
            {
                return ContractViolation(type, "threw an exception during normalization.");
            }
            if (twice is null)
            {
                return ContractViolation(type, "returned null during normalization.");
            }
            if (!string.Equals(
                normalized.CanonicalText, twice.CanonicalText, StringComparison.Ordinal))
            {
                return ContractViolation(type, "is not idempotent.");
            }
            return Result<JsonValue>.Success(normalized);
        }

        /// <summary>
        /// Validates one <paramref name="value"/> against a resolved
        /// <paramref name="definition"/> — the SPEC §A5/§B0.1 null rule, then
        /// the type's Codec through <see cref="GuardedValidate"/> — appending
        /// diagnostics under <paramref name="path"/> (<c>$</c> at the port
        /// position, <c>$[i]</c> inside a list). <see cref="BoundListCodec"/>
        /// calls this for every element with <paramref name="nullable"/>
        /// <see langword="false"/>: an explicit element <c>null</c> is
        /// accepted only for a <c>core.Json</c> element type.
        /// </summary>
        internal static void ValidateResolved(
            TypeRef type,
            TypeDefinition definition,
            JsonValue value,
            PortConstraints? constraints,
            bool nullable,
            string path,
            List<Diagnostic> sink) =>
            ValidateResolved(
                type, definition, value, constraints, nullable, path, sink, null);

        /// <summary>
        /// <see cref="ValidateResolved"/> plus framework-path recording: when
        /// <paramref name="frameworkPaths"/> is non-null it receives one entry
        /// per appended diagnostic — the share of the diagnostic's
        /// <see cref="Diagnostic.JsonPath"/> the framework composed itself
        /// (<paramref name="path"/>, extended by the element segments an inner
        /// <see cref="BoundListCodec"/> records). The §B7.1 revalidation reads
        /// it to locate a sanitized failure without trusting — or parsing —
        /// anything the Codec contributed to <see cref="Diagnostic.JsonPath"/>.
        /// </summary>
        internal static void ValidateResolved(
            TypeRef type,
            TypeDefinition definition,
            JsonValue value,
            PortConstraints? constraints,
            bool nullable,
            string path,
            List<Diagnostic> sink,
            List<string?>? frameworkPaths)
        {
            var token = value.ToToken();
            if (token.Type == JTokenType.Null)
            {
                if (IsCoreJson(type))
                {
                    var corePaths = frameworkPaths is null ? null : new List<string?>();
                    AddAll(
                        sink, frameworkPaths,
                        GuardedValidate(
                            type, definition.Codec, value, constraints, corePaths),
                        corePaths, path);
                    return;
                }
                if (nullable)
                {
                    return;
                }
                sink.Add(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    "Explicit null is not accepted for this type.",
                    jsonPath: path == "$" ? null : path));
                frameworkPaths?.Add(path);
                return;
            }

            var innerPaths = frameworkPaths is null ? null : new List<string?>();
            AddAll(
                sink, frameworkPaths,
                GuardedValidate(type, definition.Codec, value, constraints, innerPaths),
                innerPaths, path);
        }

        /// <summary>The JsonPath of the element at <paramref name="index"/> below <paramref name="path"/>.</summary>
        internal static string ElementPath(string path, int index) =>
            path + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

        // The single point where a Codec's Validate output enters the
        // framework. A Codec that throws, returns null or yields a malformed
        // diagnostic breaches its contract (SPEC §B7.1 item 4); it is reported
        // as one sanitized OW-TYPE-003 — exception text never crosses, it may
        // carry secrets — instead of an exception escaping the Result<T> /
        // Diagnostic layer boundary (SPEC §0.4). Port-level validation,
        // core.List<T> element validation and the §B7.1 revalidation of a
        // normalized value all share this path.
        //
        // innerPaths, when non-null, is an empty list this call leaves
        // parallel to the returned diagnostics: each entry carries the
        // element path the framework composed inside a BoundListCodec
        // ("$[i]"), or null when the codec itself produced the diagnostic
        // for the value it was given — keeping that information across the
        // IValueCodec boundary without storing it on Diagnostic.
        private static IReadOnlyList<Diagnostic> GuardedValidate(
            TypeRef type,
            IValueCodec codec,
            JsonValue value,
            PortConstraints? constraints,
            List<string?>? innerPaths)
        {
            IReadOnlyList<Diagnostic>? diagnostics;
            List<string?>? located = null;
            try
            {
                if (innerPaths is not null && codec is BoundListCodec bound)
                {
                    located = new List<string?>();
                    diagnostics = bound.ValidateLocated(value, constraints, located);
                }
                else
                {
                    diagnostics = codec.Validate(value, constraints);
                }
            }
            catch (Exception)
            {
                return Sanitized(
                    innerPaths, type, "threw an exception during validation.");
            }
            if (diagnostics is null)
            {
                return Sanitized(
                    innerPaths, type, "returned no validation result.");
            }
            for (var i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i] is null)
                {
                    return Sanitized(
                        innerPaths, type, "returned an invalid validation result.");
                }
            }
            if (innerPaths is not null)
            {
                if (located is null)
                {
                    for (var i = 0; i < diagnostics.Count; i++)
                    {
                        innerPaths.Add(null);
                    }
                }
                else
                {
                    innerPaths.AddRange(located);
                }
            }
            return diagnostics;
        }

        // One sanitized diagnostic per contract breach; its innerPaths entry
        // is null — the breach is located at the value the Codec was given,
        // not inside it.
        private static IReadOnlyList<Diagnostic> Sanitized(
            List<string?>? innerPaths, TypeRef type, string detail)
        {
            if (innerPaths is not null)
            {
                innerPaths.Clear();
                innerPaths.Add(null);
            }
            return new[]
            {
                ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_003,
                    $"The codec for type '{type}' {detail}"),
            };
        }

        private static Result<JsonValue> ContractViolation(TypeRef type, string detail) =>
            Result<JsonValue>.Failure(ErrorCodes.Create(
                ErrorCodes.OW_TYPE_003,
                $"The codec for type '{type}' {detail}"));

        /// <summary>Whether <paramref name="type"/> is the bare <c>core.Json</c> type — the only position whose tokens stay unconverted (SPEC §B7.1, §D0.2).</summary>
        internal static bool IsCoreJson(TypeRef type) =>
            type.Args.Count == 0 &&
            string.Equals(type.Name, "core.Json", StringComparison.Ordinal);

        // Attaches the element-level JsonPath to diagnostics produced below the
        // value root: a codec JsonPath such as "$.x" composes into
        // "$[2].x"; at the root itself only diagnostics that already carry a
        // path are rewritten, so callers may still attach port context. When
        // frameworkPaths is non-null it also receives, parallel to sink, the
        // share of each diagnostic's path the framework composed itself —
        // this call's path, or that path composing the element prefix an
        // inner BoundListCodec recorded in innerPaths — so the sanitized
        // revalidation failure can read a location without trusting anything
        // the Codec contributed.
        private static void AddAll(
            List<Diagnostic> sink,
            List<string?>? frameworkPaths,
            IReadOnlyList<Diagnostic> diagnostics,
            IReadOnlyList<string?>? innerPaths,
            string path)
        {
            for (var i = 0; i < diagnostics.Count; i++)
            {
                var diagnostic = diagnostics[i];
                sink.Add(path == "$" && diagnostic.JsonPath is null
                    ? diagnostic
                    : diagnostic with { JsonPath = ComposePath(path, diagnostic.JsonPath) });
                frameworkPaths?.Add(
                    innerPaths is not null && innerPaths[i] is string inner
                        ? ComposePath(path, inner)
                        : path);
            }
        }

        private static string ComposePath(string path, string? inner) =>
            inner is not null && inner.StartsWith("$", StringComparison.Ordinal)
                ? path + inner.Substring(1)
                : path;
    }
}
