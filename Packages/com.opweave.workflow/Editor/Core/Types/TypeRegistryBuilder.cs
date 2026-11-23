#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// Builds an immutable <see cref="ITypeRegistry"/> snapshot from permitted
    /// <see cref="TypeProviderCandidate"/>s (SPEC §B2, §B0.1, §C3 step 4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Candidates are processed in deterministic order — Provider ID (Ordinal),
    /// then input position — so the same inputs always produce the same snapshot
    /// and the same failure list. Only defective candidates and their dependents
    /// are excluded (SPEC §C3 step 4):
    /// </para>
    /// <list type="number">
    /// <item>Provider-scope: a Provider ID declared twice rejects <em>all</em>
    /// candidates of <em>both</em> providers; a provider that throws or returns
    /// null from <see cref="ITypeProvider.GetTypes"/>, or yields a null
    /// definition, is rejected as a whole (the null entry alone when the rest
    /// enumerated cleanly).</item>
    /// <item>Definition-scope: malformed Type IDs (non-canonical §B1 form,
    /// <c>&lt;&gt;</c> marker inconsistent with <see cref="TypeDefinition.GenericArity"/>,
    /// unknown <see cref="TypeKind"/>), reserved namespaces
    /// (<c>core.*</c>/<c>unity.*</c>/<c>opweave.*</c>) from non-standard providers,
    /// and missing Codecs are rejected individually.</item>
    /// <item>Collision: the same Type ID from two candidates invalidates
    /// <em>both</em>, never just the later one (SPEC §B0.1). The count covers
    /// every collected candidate — including ones the individual screen
    /// rejects — so a defective offer still takes its twin down with it.</item>
    /// <item>Graph: base-type references must resolve inside the surviving set
    /// (a <c>type:</c> class-id argument is left to the Editor-side
    /// <see cref="IClassRelationProvider"/> and is not resolved here), and any
    /// definition sitting on a base-type cycle is rejected. Both rules re-run to
    /// a fixed point so dependents of excluded definitions are removed too.</item>
    /// </list>
    /// <para>
    /// Rejections are returned as <see cref="TypeRegistryFailure"/> records;
    /// the registration layer maps them to OW-OP-013..019 (T-W2.12-03).
    /// <see cref="TypeDefinition.GenericConstraint"/> is not resolved here —
    /// constraint checking belongs to assignability (T-W1.4-03).
    /// </para>
    /// <para>
    /// A Type ID that fails canonical §B1 validation is never published
    /// verbatim — an unvalidated provider-supplied ID can carry
    /// credentials or other unsafe text. Failure records carry the
    /// <see cref="RedactedTypeId"/> form for a malformed ID wherever it
    /// would otherwise appear — the record's Type ID, its detail and
    /// every published field derived from them (SPEC §C3 error
    /// paragraph) — while canonical IDs are published unchanged.
    /// </para>
    /// </remarks>
    public static class TypeRegistryBuilder
    {
        private const string GenericMarker = "<>";

        /// <summary>
        /// Builds the snapshot; never fails wholesale — the result always carries
        /// a usable (possibly empty) registry plus one
        /// <see cref="TypeRegistryFailure"/> per rejected candidate or provider.
        /// <paramref name="classRelations"/> is the Editor-side
        /// <see cref="IClassRelationProvider"/> the snapshot consults for
        /// class-id assignability it cannot decide statically (SPEC §B6 rule 4);
        /// when omitted, every such relation is undeterminable.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="providers"/> is null.</exception>
        /// <exception cref="ArgumentException">The enumeration yields a null candidate.</exception>
        public static TypeRegistryBuildResult Build(
            IEnumerable<TypeProviderCandidate> providers,
            IClassRelationProvider? classRelations = null)
        {
            if (providers is null)
            {
                throw new ArgumentNullException(nameof(providers));
            }

            var failures = new List<TypeRegistryFailure>();
            var survivors = CollectSurvivors(providers, failures);

            // ── Snapshot ──
            var definitions = new List<TypeDefinition>();
            foreach (var candidate in survivors)
            {
                definitions.Add(candidate.Definition);
            }
            definitions.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
            return new TypeRegistryBuildResult(
                new TypeRegistry(ReadOnlyCollections.CopyList(definitions), classRelations),
                ReadOnlyCollections.CopyList(failures));
        }

        /// <summary>
        /// The Type ID → registering candidate associations the same §C3
        /// step-4 pipeline accepts for <paramref name="providers"/>: every
        /// survivor of provider-scope rejection, definition screening,
        /// collision and graph pruning bound to the candidate that offered
        /// it. Rejections are not surfaced here — <see cref="Build"/>
        /// reports them as <see cref="TypeRegistryFailure"/>s.
        /// </summary>
        /// <remarks>
        /// The Unity-side provenance layer binds weak-fingerprint atoms
        /// through this map (SPEC §0.6 SG-11): a Type ID the build accepted
        /// is always bound to the provider that registered it, and an offer
        /// rejected before registration — a duplicate Provider ID's
        /// candidates, a failed provider, a defective or
        /// collision-invalidated definition — can never shadow it.
        /// Re-running the pipeline is safe because providers are stateless
        /// and deterministic (SPEC §C0.1).
        /// </remarks>
        /// <exception cref="ArgumentNullException"><paramref name="providers"/> is null.</exception>
        /// <exception cref="ArgumentException">The enumeration yields a null candidate.</exception>
        public static IReadOnlyDictionary<string, TypeProviderCandidate> AcceptedBindings(
            IEnumerable<TypeProviderCandidate> providers)
        {
            if (providers is null)
            {
                throw new ArgumentNullException(nameof(providers));
            }

            var survivors = CollectSurvivors(providers, new List<TypeRegistryFailure>());
            var bindings = new Dictionary<string, TypeProviderCandidate>(StringComparer.Ordinal);
            foreach (var candidate in survivors)
            {
                bindings.Add(candidate.Definition.Id, candidate.Provider);
            }
            return bindings;
        }

        // The acceptance pipeline Build and AcceptedBindings share:
        // ordered providers → duplicate Provider ID rejection → screened
        // collection → Type ID collision → base-type graph pruning.
        private static List<Candidate> CollectSurvivors(
            IEnumerable<TypeProviderCandidate> providers,
            List<TypeRegistryFailure> failures)
        {
            var ordered = OrderProviders(providers);

            // ── Provider scope: duplicate Provider IDs reject all their candidates ──
            var providerAlive = new bool[ordered.Count];
            for (var i = 0; i < providerAlive.Length; i++)
            {
                providerAlive[i] = true;
            }
            for (var i = 0; i < ordered.Count;)
            {
                var j = i + 1;
                while (j < ordered.Count && string.Equals(
                    ordered[j].ProviderId, ordered[i].ProviderId, StringComparison.Ordinal))
                {
                    j++;
                }
                if (j - i > 1)
                {
                    for (var k = i; k < j; k++)
                    {
                        providerAlive[k] = false;
                        failures.Add(new TypeRegistryFailure(
                            TypeRegistryFailureKind.DuplicateProviderId,
                            null,
                            ordered[k].ProviderId,
                            $"Provider ID '{ordered[k].ProviderId}' is declared by {j - i} candidates; all were rejected.",
                            ProviderAssembly(ordered[k])));
                    }
                }
                i = j;
            }

            // ── Definition scope: collect and screen candidates; screened-out
            //    definitions stay listed until the collision pass has counted
            //    their Type ID (SPEC §B0.1) ──
            var survivors = new List<Candidate>();
            for (var i = 0; i < ordered.Count; i++)
            {
                if (!providerAlive[i])
                {
                    continue;
                }
                CollectProviderDefinitions(ordered[i], survivors, failures);
            }

            // ── Collision: a shared Type ID invalidates every candidate for it ──
            var idCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var candidate in survivors)
            {
                idCounts.TryGetValue(candidate.Definition.Id, out var count);
                idCounts[candidate.Definition.Id] = count + 1;
            }
            survivors.RemoveAll(candidate =>
            {
                if (idCounts[candidate.Definition.Id] <= 1)
                {
                    return false;
                }
                // The shared ID may itself be malformed — a screened-out
                // offer still counts toward the collision — so the record
                // carries the safe published form, never the raw text.
                var safeId = SafeTypeId(
                    candidate.Definition.Id, candidate.Definition.GenericArity);
                failures.Add(new TypeRegistryFailure(
                    TypeRegistryFailureKind.DuplicateTypeId,
                    safeId,
                    candidate.ProviderId,
                    $"Type ID '{safeId}' is declared by {idCounts[candidate.Definition.Id]} candidates; all were invalidated.",
                    ProviderAssembly(candidate.Provider)));
                return true;
            });

            // Screened-out candidates were still counted for the collision above;
            // drop them before the graph pass.
            survivors.RemoveAll(candidate => candidate.Name is null);

            // ── Graph: unregistered bases and cycles, to a fixed point ──
            var removed = true;
            while (removed)
            {
                removed = false;
                var byKey = IndexByKey(survivors);
                var doomed = new HashSet<Candidate>();
                foreach (var candidate in survivors)
                {
                    var badBase = FirstUnresolvableBase(candidate.Definition, byKey);
                    if (badBase is not null)
                    {
                        doomed.Add(candidate);
                        failures.Add(new TypeRegistryFailure(
                            TypeRegistryFailureKind.UnregisteredBaseType,
                            candidate.Definition.Id,
                            candidate.ProviderId,
                            badBase,
                            ProviderAssembly(candidate.Provider)));
                    }
                }
                if (doomed.Count > 0)
                {
                    survivors.RemoveAll(doomed.Contains);
                    removed = true;
                    byKey = IndexByKey(survivors);
                }
                doomed.Clear();
                foreach (var candidate in survivors)
                {
                    if (IsOnCycle(candidate, byKey))
                    {
                        doomed.Add(candidate);
                        failures.Add(new TypeRegistryFailure(
                            TypeRegistryFailureKind.BaseTypeCycle,
                            candidate.Definition.Id,
                            candidate.ProviderId,
                            $"Type '{candidate.Definition.Id}' is part of a base-type cycle.",
                            ProviderAssembly(candidate.Provider)));
                    }
                }
                if (doomed.Count > 0)
                {
                    survivors.RemoveAll(doomed.Contains);
                    removed = true;
                }
            }
            return survivors;
        }

        private sealed class Candidate
        {
            internal Candidate(TypeProviderCandidate provider, TypeDefinition definition, string? name)
            {
                Provider = provider;
                Definition = definition;
                Name = name;
            }

            internal TypeProviderCandidate Provider { get; }
            internal TypeDefinition Definition { get; }
            internal string ProviderId => Provider.ProviderId;

            /// <summary>Canonical base name; null when the definition failed screening.</summary>
            internal string? Name { get; }
        }

        private static List<TypeProviderCandidate> OrderProviders(
            IEnumerable<TypeProviderCandidate> providers)
        {
            var ordered = new List<TypeProviderCandidate>();
            foreach (var candidate in providers)
            {
                if (candidate is null)
                {
                    throw new ArgumentException(
                        "Provider candidates must not contain null.", nameof(providers));
                }
                ordered.Add(candidate);
            }
            // Deterministic provider order (SPEC §C3 step 4): Provider ID Ordinal,
            // input position breaks ties between same-ID duplicates. List.Sort is
            // unstable, so carry the original index along.
            var indexed = new List<KeyValuePair<TypeProviderCandidate, int>>();
            for (var i = 0; i < ordered.Count; i++)
            {
                indexed.Add(new KeyValuePair<TypeProviderCandidate, int>(ordered[i], i));
            }
            indexed.Sort((a, b) =>
            {
                var byId = string.Compare(a.Key.ProviderId, b.Key.ProviderId, StringComparison.Ordinal);
                return byId != 0 ? byId : a.Value.CompareTo(b.Value);
            });
            var result = new List<TypeProviderCandidate>(indexed.Count);
            foreach (var pair in indexed)
            {
                result.Add(pair.Key);
            }
            return result;
        }

        private static void CollectProviderDefinitions(
            TypeProviderCandidate candidate,
            List<Candidate> collected,
            List<TypeRegistryFailure> failures)
        {
            List<TypeDefinition?> offered;
            try
            {
                var enumerable = candidate.Provider.GetTypes();
                if (enumerable is null)
                {
                    failures.Add(ProviderFailure(candidate,
                        "GetTypes() returned null; all candidates from this provider were rejected."));
                    return;
                }
                offered = new List<TypeDefinition?>();
                foreach (var definition in enumerable)
                {
                    offered.Add(definition);
                }
            }
            catch (Exception)
            {
                // A throwing provider violates the §C0.1 stateless/deterministic
                // contract; its exception text is never propagated — it may carry
                // secrets — and partial output is untrusted, so every candidate
                // from this provider is rejected.
                failures.Add(ProviderFailure(candidate,
                    "GetTypes() threw an exception; all candidates from this provider were rejected."));
                return;
            }

            for (var i = 0; i < offered.Count; i++)
            {
                var definition = offered[i];
                if (definition is null)
                {
                    failures.Add(ProviderFailure(candidate,
                        "GetTypes() yielded a null TypeDefinition; the candidate was rejected."));
                    continue;
                }
                var name = ScreenDefinition(candidate, definition, failures);
                // A screened-out candidate (name is null) is still collected so
                // the collision pass counts its Type ID against every offer.
                collected.Add(new Candidate(candidate, definition, name));
            }
        }

        private static TypeRegistryFailure ProviderFailure(
            TypeProviderCandidate candidate, string detail) =>
            new TypeRegistryFailure(
                TypeRegistryFailureKind.ProviderFailure, null,
                candidate.ProviderId, detail, ProviderAssembly(candidate));

        // The offering provider's defining assembly — the SPEC §C3 error
        // paragraph's 出自アセンブリ safe context — derived from the
        // instantiated provider's own type (T-W2.12-03).
        private static string? ProviderAssembly(TypeProviderCandidate candidate) =>
            candidate.Provider.GetType().Assembly.GetName().Name;

        /// <summary>
        /// The publishable form of a provider-supplied Type ID: the ID
        /// itself when it is a canonical SPEC §B1 ID (the
        /// <c>&lt;&gt;</c> marker consistent with
        /// <paramref name="genericArity"/> and an exact parse
        /// round-trip), otherwise the redacted form of
        /// <see cref="RedactedTypeId"/>. SPEC §C3's safe diagnostic
        /// context never carries an unvalidated ID verbatim — it may be
        /// a credential-bearing URL or other unsafe text.
        /// </summary>
        internal static string SafeTypeId(string id, int genericArity) =>
            PublishedId.TypeId(id, genericArity);

        /// <summary>
        /// The safe stand-in for an unvalidated Type ID —
        /// <see cref="PublishedId.Placeholder"/>'s fixed marker plus the
        /// raw length and a short lowercase SHA-256 prefix of the raw
        /// UTF-8, enough to correlate equal offers (for example both
        /// sides of a Type ID collision) without exposing the text.
        /// </summary>
        internal static string RedactedTypeId(string id) =>
            PublishedId.Placeholder(id);

        /// <summary>Returns the definition's canonical base name when it passes screening, else null.</summary>
        private static string? ScreenDefinition(
            TypeProviderCandidate provider,
            TypeDefinition definition,
            List<TypeRegistryFailure> failures)
        {
            var id = definition.Id;
            var providerId = provider.ProviderId;
            // The publishable form of the offered ID: the ID itself when
            // canonical, the redacted marker otherwise — a malformed ID may
            // carry credentials and is never embedded verbatim (SPEC §C3
            // error paragraph). For a canonical ID safeId is id.
            var safeId = SafeTypeId(id, definition.GenericArity);

            var hasMarker = id.EndsWith(GenericMarker, StringComparison.Ordinal);
            if (hasMarker != definition.GenericArity > 0)
            {
                Fail(TypeRegistryFailureKind.MalformedTypeId, hasMarker
                    ? $"Generic definition '{safeId}' must have GenericArity > 0."
                    : $"Type ID '{safeId}' lacks the '<>' marker required for GenericArity {definition.GenericArity}.");
                return null;
            }
            var name = hasMarker ? id.Substring(0, id.Length - GenericMarker.Length) : id;

            var parsed = TypeRef.Parse(name);
            if (!parsed.Ok || parsed.Value!.Args.Count != 0 ||
                !string.Equals(parsed.Value!.ToString(), name, StringComparison.Ordinal))
            {
                Fail(TypeRegistryFailureKind.MalformedTypeId,
                    $"Type ID '{safeId}' is not a canonical SPEC §B1 Type ID.");
                return null;
            }
            if (!Enum.IsDefined(typeof(TypeKind), definition.Kind))
            {
                Fail(TypeRegistryFailureKind.MalformedTypeId,
                    $"Type '{safeId}' has an unrecognized TypeKind.");
                return null;
            }
            if (provider.Origin != OperationOrigin.Standard &&
                IdGrammar.HasReservedOperationPrefix(name))
            {
                Fail(TypeRegistryFailureKind.ReservedNamespace,
                    $"Type ID '{safeId}' uses a namespace reserved for standard providers.");
                return null;
            }
            if (definition.Codec is null)
            {
                Fail(TypeRegistryFailureKind.MissingCodec,
                    $"Type '{safeId}' has no Codec.");
                return null;
            }
            // SPEC §B7.1 item 4 → OW-OP-016: a Codec provably broken on the
            // registration-time probe is rejected per definition.
            var contractDetail = CodecContractProbe.Violation(definition.Codec);
            if (contractDetail is not null)
            {
                Fail(TypeRegistryFailureKind.CodecContractViolation,
                    $"Type '{safeId}' has a Codec that violates the normalization contract: {contractDetail}.");
                return null;
            }
            return name;

            void Fail(TypeRegistryFailureKind kind, string detail) =>
                failures.Add(new TypeRegistryFailure(
                    kind, safeId, providerId, detail, ProviderAssembly(provider)));
        }

        // Lookup key for a candidate: base name + generic arity. Keys are unique
        // among survivors — a shared key implies a shared Type ID, which the
        // collision pass already removed.
        private static Dictionary<string, Candidate> IndexByKey(List<Candidate> survivors)
        {
            var byKey = new Dictionary<string, Candidate>(StringComparer.Ordinal);
            foreach (var candidate in survivors)
            {
                byKey[Key(candidate.Name!, candidate.Definition.GenericArity)] = candidate;
            }
            return byKey;
        }

        private static string Key(string name, int arity) => name + "#" + arity;

        private static Candidate? ResolveKey(
            Dictionary<string, Candidate> byKey, string name, int arity) =>
            byKey.TryGetValue(Key(name, arity), out var candidate) ? candidate : null;

        /// <summary>
        /// Whether every part of <paramref name="type"/> — its own name/arity and
        /// each non-<c>type:</c> generic argument — resolves inside
        /// <paramref name="byKey"/>. <c>type:</c> class-id leaves are not resolved
        /// in Core (SPEC §B2).
        /// </summary>
        private static bool IsResolvable(TypeRef type, Dictionary<string, Candidate> byKey)
        {
            if (ResolveKey(byKey, type.Name, type.Args.Count) is null)
            {
                return false;
            }
            for (var i = 0; i < type.Args.Count; i++)
            {
                var arg = type.Args[i];
                if (arg.Name.StartsWith(TypeRef.ClassRefPrefix, StringComparison.Ordinal))
                {
                    continue;
                }
                if (!IsResolvable(arg, byKey))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The detail text for the first base entry that fails to parse or does
        /// not fully resolve; null when all bases resolve.
        /// </summary>
        private static string? FirstUnresolvableBase(
            TypeDefinition definition, Dictionary<string, Candidate> byKey)
        {
            var baseTypes = definition.BaseTypes;
            for (var i = 0; i < baseTypes.Count; i++)
            {
                var baseType = baseTypes[i];
                var parsed = TypeRef.Parse(baseType);
                if (!parsed.Ok)
                {
                    // The reference is unvalidated provider text — publish
                    // the redacted form, never the raw string (SPEC §C3
                    // error paragraph).
                    return $"Base type '{RedactedTypeId(baseType)}' of '{definition.Id}' is not a valid Type ID.";
                }
                if (!IsResolvable(parsed.Value!, byKey))
                {
                    return $"Base type '{baseType}' of '{definition.Id}' does not resolve to a registered type.";
                }
            }
            return null;
        }

        /// <summary>
        /// Whether <paramref name="start"/> can reach itself through resolved
        /// base-type edges — i.e. sits on a cycle. Only definitions still in
        /// <paramref name="byKey"/> count as nodes.
        /// </summary>
        private static bool IsOnCycle(Candidate start, Dictionary<string, Candidate> byKey)
        {
            var visited = new HashSet<Candidate>();
            var queue = new Queue<Candidate>();
            EnqueueBases(start, queue);
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (ReferenceEquals(next, start))
                {
                    return true;
                }
                if (visited.Add(next))
                {
                    EnqueueBases(next, queue);
                }
            }
            return false;

            void EnqueueBases(Candidate candidate, Queue<Candidate> targets)
            {
                var baseTypes = candidate.Definition.BaseTypes;
                for (var i = 0; i < baseTypes.Count; i++)
                {
                    var parsed = TypeRef.Parse(baseTypes[i]);
                    if (!parsed.Ok)
                    {
                        continue;
                    }
                    var target = ResolveKey(byKey, parsed.Value!.Name, parsed.Value!.Args.Count);
                    if (target is not null)
                    {
                        targets.Enqueue(target);
                    }
                }
            }
        }
    }
}
