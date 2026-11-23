#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OpWeave.Core
{
    /// <summary>
    /// The immutable <see cref="ITypeRegistry"/> snapshot produced by
    /// <see cref="TypeRegistryBuilder"/> (SPEC §B2). Internal on purpose: callers
    /// hold the <see cref="ITypeRegistry"/> surface only, so providers and other
    /// extensions cannot reach registry internals (SPEC §C0.1).
    /// </summary>
    internal sealed class TypeRegistry : ITypeRegistry
    {
        // name → registered definitions for that name, sorted by GenericArity.
        // A name can host distinct definitions per arity (e.g. "a.B" arity 0 and
        // "a.B<>" arity 1), which is why the key is not the bare Type ID string.
        private readonly IReadOnlyDictionary<string, TypeDefinition[]> _byName;
        private readonly IReadOnlyList<TypeDefinition> _all;
        private readonly IClassRelationProvider? _classRelations;

        // SPEC §B6: assignability results are memoized per (S, T) pair. The
        // snapshot is immutable, so a cached answer stays valid for the whole
        // lifetime of the registry; Core runs on the main thread and holds no
        // locks (SPEC §0.4).
        private readonly Dictionary<(TypeRef Source, TypeRef Target), AssignResult> _assignability =
            new Dictionary<(TypeRef Source, TypeRef Target), AssignResult>();

        private static readonly AssignResult s_assignable = new AssignResult(true, null);

        /// <summary>
        /// <paramref name="all"/> must already be deduplicated and sorted by Type
        /// ID (Ordinal); the builder owns that contract.
        /// <paramref name="classRelations"/> is the Editor-side
        /// <see cref="IClassRelationProvider"/> consulted for class-id pairs the
        /// snapshot cannot decide statically (SPEC §B6 rule 4); when it is null
        /// every such pair is undeterminable.
        /// </summary>
        internal TypeRegistry(
            IReadOnlyList<TypeDefinition> all,
            IClassRelationProvider? classRelations)
        {
            _all = all;
            _classRelations = classRelations;
            var groups = new Dictionary<string, List<TypeDefinition>>(StringComparer.Ordinal);
            foreach (var definition in all)
            {
                var name = DefinitionName(definition);
                if (!groups.TryGetValue(name, out var list))
                {
                    list = new List<TypeDefinition>();
                    groups.Add(name, list);
                }
                list.Add(definition);
            }
            var byName = new Dictionary<string, TypeDefinition[]>(StringComparer.Ordinal);
            foreach (var pair in groups)
            {
                pair.Value.Sort((a, b) => a.GenericArity.CompareTo(b.GenericArity));
                byName.Add(pair.Key, pair.Value.ToArray());
            }
            _byName = new ReadOnlyDictionary<string, TypeDefinition[]>(byName);
        }

        /// <inheritdoc/>
        public IReadOnlyList<TypeDefinition> All => _all;

        /// <inheritdoc/>
        /// <remarks>
        /// On a closed generic use a definition whose Codec implements
        /// <see cref="IClosedGenericCodec"/> is returned with the Codec bound to
        /// the resolved arguments — the built-in <c>core.List&lt;T&gt;</c>
        /// element binding of SPEC §B3. Binding resolves the arguments in this
        /// snapshot; when they do not resolve, the closed resolution fails with
        /// the argument's diagnostics.
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
        public Result<TypeDefinition> Resolve(TypeRef type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            if (!_byName.TryGetValue(type.Name, out var definitions))
            {
                return Result<TypeDefinition>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_TYPE_001,
                    $"Unknown type '{type}'."));
            }
            for (var i = 0; i < definitions.Length; i++)
            {
                if (definitions[i].GenericArity != type.Args.Count)
                {
                    continue;
                }
                if (type.Args.Count > 0 && definitions[i].Codec is IClosedGenericCodec closing)
                {
                    var codec = closing.Close(type, this);
                    return codec.Ok
                        ? Result<TypeDefinition>.Success(definitions[i] with { Codec = codec.Value! })
                        : Result<TypeDefinition>.Failure(codec.Diagnostics);
                }
                return Result<TypeDefinition>.Success(definitions[i]);
            }
            var arities = new List<string>();
            for (var i = 0; i < definitions.Length; i++)
            {
                arities.Add(definitions[i].GenericArity.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            return Result<TypeDefinition>.Failure(ErrorCodes.Create(
                ErrorCodes.OW_TYPE_005,
                $"Type '{type.Name}' does not take {type.Args.Count} generic argument(s).",
                hint: $"Registered '{type.Name}' definitions take {string.Join(", ", arities)} generic argument(s)."));
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <para>
        /// The SPEC §B6 rules apply in order; local choices where the SPEC
        /// leaves detail open:
        /// </para>
        /// <list type="bullet">
        /// <item>Rules 1–2 are unconditional: an identical pair is assignable
        /// and anything flows to <c>core.Json</c>, even for type IDs that do
        /// not resolve. Any other pair must be well formed — a
        /// <c>type:</c> class-id leaf or a registered type whose generic
        /// arguments recursively satisfy their definition's
        /// <see cref="TypeDefinition.GenericConstraint"/> (the constraint
        /// checking <see cref="TypeRegistryBuilder"/> defers to assignability).
        /// </item>
        /// <item>Rule 3's transitive walk applies the full procedure to each
        /// declared base, so a base that reaches the target through covariance
        /// or a class relation counts as reached.</item>
        /// <item>Rule 4's covariance applies to every same-name, same-arity
        /// generic pair (ComponentRef/AssetRef/List are the standard cases);
        /// each argument pair is judged by the full procedure.</item>
        /// <item>A class id is a <c>type:</c> leaf or a registered
        /// <see cref="TypeKind.Class"/> type. Two class ids not decided by the
        /// earlier rules go to the snapshot's
        /// <see cref="IClassRelationProvider"/>; a missing provider or a null
        /// answer makes the pair not assignable with an "unresolved class"
        /// reason, exactly like a provider that cannot determine the relation.
        /// The declared bases of the built-in class ids already encode the
        /// relations Core knows statically, so they answer without the
        /// provider; class ids are passed to the provider verbatim
        /// (<c>type:</c>-prefixed leaf name or registered Type ID).
        /// </item>
        /// </list>
        /// </remarks>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="source"/> or <paramref name="target"/> is null.
        /// </exception>
        public AssignResult IsAssignable(TypeRef source, TypeRef target)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            if (target is null)
            {
                throw new ArgumentNullException(nameof(target));
            }
            if (_assignability.TryGetValue((source, target), out var cached))
            {
                return cached;
            }
            var result = DecideAssignable(source, target);
            _assignability.Add((source, target), result);
            return result;
        }

        private AssignResult DecideAssignable(TypeRef source, TypeRef target)
        {
            // Rule 1: identical types.
            if (source.Equals(target))
            {
                return s_assignable;
            }
            // Rule 2: anything flows to core.Json.
            if (target.Args.Count == 0 &&
                string.Equals(target.Name, "core.Json", StringComparison.Ordinal))
            {
                return s_assignable;
            }

            var sourceLeaf = IsClassLeaf(source);
            var targetLeaf = IsClassLeaf(target);
            TypeDefinition? sourceDef = null;
            TypeDefinition? targetDef = null;
            if (!sourceLeaf)
            {
                var resolved = Resolve(source);
                if (!resolved.Ok)
                {
                    return NotAssignable($"Type '{source}' is not registered.");
                }
                sourceDef = resolved.Value;
            }
            if (!targetLeaf)
            {
                var resolved = Resolve(target);
                if (!resolved.Ok)
                {
                    return NotAssignable($"Type '{target}' is not registered.");
                }
                targetDef = resolved.Value;
            }
            if (sourceDef is not null && !IsWellFormed(source, sourceDef, out var malformed))
            {
                return NotAssignable(malformed);
            }
            if (targetDef is not null && !IsWellFormed(target, targetDef, out malformed))
            {
                return NotAssignable(malformed);
            }

            // Rule 3: declared base types, transitively.
            if (sourceDef is not null)
            {
                var baseTypes = sourceDef.BaseTypes;
                for (var i = 0; i < baseTypes.Count; i++)
                {
                    var parsed = TypeRef.Parse(baseTypes[i]);
                    if (parsed.Ok && IsAssignable(parsed.Value!, target).Assignable)
                    {
                        return s_assignable;
                    }
                }
            }

            // Rule 4: same generic definition → per-argument covariance.
            if (sourceDef is not null &&
                sourceDef.GenericArity > 0 &&
                source.Args.Count == sourceDef.GenericArity &&
                string.Equals(source.Name, target.Name, StringComparison.Ordinal) &&
                target.Args.Count == source.Args.Count)
            {
                for (var i = 0; i < source.Args.Count; i++)
                {
                    var argument = IsAssignable(source.Args[i], target.Args[i]);
                    if (!argument.Assignable)
                    {
                        return NotAssignable(
                            $"Type argument '{source.Args[i]}' is not assignable to '{target.Args[i]}': {argument.Reason}");
                    }
                }
                return s_assignable;
            }

            // Rule 4's class clause: two class ids still undecided → provider.
            var sourceClass = sourceLeaf || (sourceDef is not null && sourceDef.Kind == TypeKind.Class);
            var targetClass = targetLeaf || (targetDef is not null && targetDef.Kind == TypeKind.Class);
            if (sourceClass && targetClass)
            {
                var verdict = _classRelations?.IsSubclass(source.Name, target.Name);
                if (verdict == true)
                {
                    return s_assignable;
                }
                return verdict == false
                    ? NotAssignable($"'{source.Name}' is not a subclass of '{target.Name}'.")
                    : NotAssignable(
                        $"Unresolved class relation between '{source.Name}' and '{target.Name}'.");
            }

            // Rule 5: no implicit conversion — nothing else applies.
            return NotAssignable($"Type '{source}' is not assignable to '{target}'.");
        }

        // Every generic argument of a closed use must resolve (or be a class-id
        // leaf), be itself well formed, and satisfy the definition's declared
        // GenericConstraint by assignability — the constraint checking the
        // builder deferred to B6. Returns the failing reason via <paramref name="reason"/>.
        private bool IsWellFormed(TypeRef type, TypeDefinition definition, out string reason)
        {
            var args = type.Args;
            var constraint = definition.GenericConstraint;
            for (var i = 0; i < args.Count; i++)
            {
                var arg = args[i];
                if (IsClassLeaf(arg))
                {
                    // A type: leaf is a well-formed class id; its relation to
                    // the constraint is judged below.
                }
                else
                {
                    var resolved = Resolve(arg);
                    if (!resolved.Ok)
                    {
                        reason = $"Type '{arg}' is not registered.";
                        return false;
                    }
                    if (!IsWellFormed(arg, resolved.Value!, out reason))
                    {
                        return false;
                    }
                }
                if (constraint is not null && !SatisfiesConstraint(arg, constraint, out var failure))
                {
                    reason = $"Type argument '{arg}' does not satisfy the generic constraint '{constraint}': {failure}";
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        }

        // The constraint is a class id ("unity.Component") or, less commonly,
        // another Type ID; an argument satisfies it by being assignable to it.
        // A constraint string that is neither a type: class id nor a parseable
        // Type ID is malformed — nothing can satisfy it (fail closed). The
        // negative AssignResult reason is propagated through <paramref
        // name="reason"/> so an undeterminable class relation keeps its
        // "unresolved class" explanation (SPEC §B6 rule 4) instead of
        // collapsing into a bare constraint violation.
        private bool SatisfiesConstraint(TypeRef arg, string constraint, out string reason)
        {
            TypeRef? required;
            if (constraint.StartsWith(TypeRef.ClassRefPrefix, StringComparison.Ordinal))
            {
                required = new TypeRef(constraint, Array.Empty<TypeRef>());
            }
            else
            {
                var parsed = TypeRef.Parse(constraint);
                required = parsed.Ok ? parsed.Value : null;
            }
            if (required is null)
            {
                reason = "the constraint is neither a 'type:' class id nor a valid Type ID";
                return false;
            }
            var result = IsAssignable(arg, required);
            reason = result.Reason ?? string.Empty;
            return result.Assignable;
        }

        private static bool IsClassLeaf(TypeRef type) =>
            type.Name.StartsWith(TypeRef.ClassRefPrefix, StringComparison.Ordinal);

        private static AssignResult NotAssignable(string reason) =>
            new AssignResult(false, reason);

        /// <summary>The definition's lookup name: <see cref="TypeDefinition.Id"/> minus the <c>&lt;&gt;</c> generic marker.</summary>
        internal static string DefinitionName(TypeDefinition definition)
        {
            var id = definition.Id;
            return id.EndsWith("<>", StringComparison.Ordinal)
                ? id.Substring(0, id.Length - 2)
                : id;
        }
    }
}
