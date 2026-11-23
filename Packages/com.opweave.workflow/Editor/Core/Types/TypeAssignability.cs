#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// The diagnostic-facing side of SPEC §B6: turns a negative
    /// <see cref="ITypeRegistry.IsAssignable"/> answer into the OW-TYPE-002
    /// diagnostic, whose Hint carries the reason and the candidate types that
    /// <em>would</em> connect (SPEC §B6 エラー, §J).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The SPEC does not say which direction "connectable type candidates"
    /// means, so the hint lists both, in registry order (Type ID Ordinal):
    /// the registered non-generic types the source can feed
    /// (<see cref="TargetsAssignableFrom"/>) and the registered non-generic
    /// types that can feed the target (<see cref="SourcesAssignableTo"/>).
    /// Candidates are the snapshot's arity-0 definitions — open generic
    /// definitions such as <c>unity.ComponentRef&lt;&gt;</c> are not complete
    /// port types and their closed instantiations are unbounded, so they are
    /// not listed; the enumeration covers registered types only and cannot
    /// invent <c>type:</c> class ids. Both directions and the closed-type
    /// restriction are local, reversible choices.
    /// </para>
    /// </remarks>
    public static class TypeAssignability
    {
        /// <summary>
        /// The registered non-generic types a value typed
        /// <paramref name="source"/> can flow to, in Type ID order.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registry"/> or <paramref name="source"/> is null.
        /// </exception>
        public static IReadOnlyList<TypeRef> TargetsAssignableFrom(
            ITypeRegistry registry, TypeRef source)
        {
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            var candidates = new List<TypeRef>();
            var all = registry.All;
            for (var i = 0; i < all.Count; i++)
            {
                var definition = all[i];
                if (definition.GenericArity != 0)
                {
                    continue;
                }
                var candidate = new TypeRef(definition.Id, Array.Empty<TypeRef>());
                if (registry.IsAssignable(source, candidate).Assignable)
                {
                    candidates.Add(candidate);
                }
            }
            return ReadOnlyCollections.CopyList(candidates);
        }

        /// <summary>
        /// The registered non-generic types whose values can feed a position
        /// typed <paramref name="target"/>, in Type ID order.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registry"/> or <paramref name="target"/> is null.
        /// </exception>
        public static IReadOnlyList<TypeRef> SourcesAssignableTo(
            ITypeRegistry registry, TypeRef target)
        {
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }
            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            var candidates = new List<TypeRef>();
            var all = registry.All;
            for (var i = 0; i < all.Count; i++)
            {
                var definition = all[i];
                if (definition.GenericArity != 0)
                {
                    continue;
                }
                var candidate = new TypeRef(definition.Id, Array.Empty<TypeRef>());
                if (registry.IsAssignable(candidate, target).Assignable)
                {
                    candidates.Add(candidate);
                }
            }
            return ReadOnlyCollections.CopyList(candidates);
        }

        /// <summary>
        /// The OW-TYPE-002 diagnostic for the <paramref name="source"/> →
        /// <paramref name="target"/> connection, or null when the pair is
        /// assignable. The Hint carries the negative
        /// <see cref="AssignResult.Reason"/> and both candidate lists
        /// (SPEC §B6); <paramref name="nodeId"/>, <paramref name="port"/> and
        /// <paramref name="jsonPath"/> attach the location of the rejected
        /// connection for the caller (D4 validation, GUI).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="registry"/>, <paramref name="source"/> or
        /// <paramref name="target"/> is null.
        /// </exception>
        public static Diagnostic? CreateNotAssignableDiagnostic(
            ITypeRegistry registry,
            TypeRef source,
            TypeRef target,
            string? nodeId = null,
            string? port = null,
            string? jsonPath = null)
        {
            if (registry is null)
            {
                throw new ArgumentNullException(nameof(registry));
            }
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            var result = registry.IsAssignable(source, target);
            if (result.Assignable)
            {
                return null;
            }
            var targets = TargetsAssignableFrom(registry, source);
            var sources = SourcesAssignableTo(registry, target);
            var hint =
                $"{result.Reason} '{source}' can connect to: {Join(targets)}." +
                $" Sources assignable to '{target}': {Join(sources)}.";
            return ErrorCodes.Create(
                ErrorCodes.OW_TYPE_002,
                $"Type '{source}' is not assignable to '{target}'.",
                nodeId: nodeId,
                port: port,
                jsonPath: jsonPath,
                hint: hint);
        }

        private static string Join(IReadOnlyList<TypeRef> candidates) =>
            candidates.Count == 0
                ? "(none)"
                : string.Join(", ", ToIds(candidates));

        private static List<string> ToIds(IReadOnlyList<TypeRef> candidates)
        {
            var ids = new List<string>(candidates.Count);
            for (var i = 0; i < candidates.Count; i++)
            {
                ids.Add(candidates[i].ToString());
            }
            return ids;
        }
    }
}
