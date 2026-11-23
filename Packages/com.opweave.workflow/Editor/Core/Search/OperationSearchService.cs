#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// The SPEC §C6/§C7 Operation search services over one immutable
    /// <see cref="RegistrySnapshot"/>: <see cref="SearchOperations"/> returns
    /// ranked compact summaries and
    /// <see cref="SearchCompatibleOperations"/> the Operation/port pairs that
    /// connect to a queried type — called directly, below the Agent API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Candidates are the snapshot's registered Operations only: extensions a
    /// whitelist or <c>disabledOperations</c> kept out never reached
    /// registration, so they can never become results (SPEC §C6 item 1,
    /// §M.7). Custom Operations are ordinary snapshot members and rank by the
    /// same rules; the <see cref="OperationSearchRequest.Origin"/> filter is
    /// what narrows them.
    /// </para>
    /// <para>
    /// The §C6 score is <c>3.0×text + 2.0×type + 0.5×tag − 0.5×destructive</c>.
    /// Text is 1.0 on a normalized exact ID match, else the ratio of the
    /// query's distinct §C5 tokens matched against the union of the
    /// descriptor's indexed fields (word prefix / bigram exact). Type is the
    /// best port-level match — exact 1.0, base-type 0.7, <c>core.Json</c> 0.1 —
    /// taken over the acceptsType inputs and the producesType outputs; with
    /// both filters set the score is the better of the two bests. Tag is the
    /// ratio of requested tags the Operation carries; the tags filter itself
    /// keeps Operations carrying at least one requested tag. Ties order by ID
    /// Ordinal then version — the deterministic §C6 item-3 ordering, so an
    /// identical request over an identical snapshot always answers
    /// identically.
    /// </para>
    /// <para>
    /// <see cref="SearchCompatibleOperations"/> classifies every port against
    /// the queried type as <see cref="CompatibleMatchKind.Exact"/>,
    /// <see cref="CompatibleMatchKind.Base"/> or
    /// <see cref="CompatibleMatchKind.Json"/> — a port on the queried type's
    /// input side answers downstream, on the output side upstream. Pairs that
    /// connect only through the <c>core.Json</c> rule (SPEC §B6 rule 2) —
    /// the port type itself or a covariant generic argument inside it — are
    /// returned only for <c>includeJson</c>. Results order by match
    /// kind (exact, base, json), then ID Ordinal, version and port name — the
    /// deterministic local choice where SPEC fixes only the result contents.
    /// </para>
    /// </remarks>
    public sealed class OperationSearchService
    {
        /// <summary>The §C6 item-4 default page size.</summary>
        public const int DefaultLimit = 10;

        /// <summary>The §C6 item-4 maximum page size.</summary>
        public const int MaxLimit = 50;

        private const string CoreJsonName = "core.Json";

        private const double ScoreText = 3.0;
        private const double ScoreType = 2.0;
        private const double ScoreTag = 0.5;
        private const double ScoreDestructive = 0.5;

        private const double TypeExact = 1.0;
        private const double TypeBase = 0.7;
        private const double TypeJson = 0.1;

        private readonly RegistrySnapshot _snapshot;

        // Per-(source, target) memo of the no-Json assignability mirror
        // below; the snapshot is immutable, so a cached verdict stays valid
        // for the service's lifetime.
        private readonly Dictionary<(TypeRef Source, TypeRef Target), bool> _withoutJson =
            new Dictionary<(TypeRef Source, TypeRef Target), bool>();

        // Per-Operation search material, precomputed once per snapshot: the
        // union of the §C5 indexed field tokens (with the word-token subset
        // for prefix matching), the normalized ID, and the parsed port types
        // — null entries mark ports whose Type ID never parses, which a
        // validated snapshot cannot produce but the matcher skips safely.
        private readonly HashSet<string>[] _fieldTokens;
        private readonly List<string>[] _fieldWords;
        private readonly string[] _normalizedIds;
        private readonly TypeRef?[][] _inputTypes;
        private readonly TypeRef?[][] _outputTypes;

        /// <summary>Binds the service to one published snapshot.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
        public OperationSearchService(RegistrySnapshot snapshot)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            var operations = snapshot.Operations;
            var count = operations.Count;
            _fieldTokens = new HashSet<string>[count];
            _fieldWords = new List<string>[count];
            _normalizedIds = new string[count];
            _inputTypes = new TypeRef?[count][];
            _outputTypes = new TypeRef?[count][];
            for (var i = 0; i < count; i++)
            {
                var descriptor = operations[i].Descriptor;
                _normalizedIds[i] = SearchTokenizer.Normalize(descriptor.Id);
                _fieldTokens[i] = new HashSet<string>(StringComparer.Ordinal);
                _fieldWords[i] = new List<string>();
                IndexField(descriptor, _fieldTokens[i], _fieldWords[i]);
                _inputTypes[i] = ParsePortTypes(descriptor.Inputs);
                _outputTypes[i] = ParsePortTypes(descriptor.Outputs);
            }
        }

        /// <summary>The snapshot this service searches.</summary>
        public RegistrySnapshot Snapshot => _snapshot;

        /// <summary>
        /// The SPEC §C6 search: filters the snapshot's Operations by every set
        /// request filter, ranks the survivors by the §C6 score formula and
        /// returns the requested page. A malformed or unregistered
        /// <see cref="OperationSearchRequest.AcceptsType"/> /
        /// <see cref="OperationSearchRequest.ProducesType"/> fails the request
        /// with the OW-TYPE-001/OW-TYPE-005 diagnostic of the parse or
        /// resolve — the only failure this method produces.
        /// </summary>
        /// <param name="request">The query, filters, language and page coordinates.</param>
        /// <param name="defaultLanguage">The caller's <c>agentLanguage</c>
        /// default applied when <see cref="OperationSearchRequest.Lang"/> is
        /// null (the settings value lives in OpWeave.UnityAdapter, which Core
        /// cannot reference); English is used when both are absent.</param>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public Result<OperationSearchPage> SearchOperations(
            OperationSearchRequest request, string? defaultLanguage = null)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var accepts = ResolveFilterType(request.AcceptsType);
            if (!accepts.Ok)
            {
                return Result<OperationSearchPage>.Failure(accepts.Diagnostics);
            }
            var produces = ResolveFilterType(request.ProducesType);
            if (!produces.Ok)
            {
                return Result<OperationSearchPage>.Failure(produces.Diagnostics);
            }

            var queryTokens = request.Query is null
                ? Array.Empty<string>()
                : (IReadOnlyList<string>)SearchTokenizer.Tokenize(request.Query);
            var normalizedQuery = request.Query is null
                ? null
                : SearchTokenizer.Normalize(request.Query);
            var allowedEffects = request.AllowedEffects is null
                ? null
                : new HashSet<string>(request.AllowedEffects, StringComparer.Ordinal);
            var requestedTags = request.Tags is null
                ? null
                : DistinctOrdinal(request.Tags);
            var language = request.Lang ?? defaultLanguage ?? "en";

            var operations = _snapshot.Operations;
            var scored = new List<RankedOperation>(operations.Count);
            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                var descriptor = operation.Descriptor;
                if (request.Origin is { } origin && operation.Origin != origin)
                {
                    continue;
                }
                if (request.Category is { } category &&
                    !string.Equals(descriptor.Category, category, StringComparison.Ordinal))
                {
                    continue;
                }
                if (allowedEffects is not null && !EffectsSubset(descriptor.SideEffects, allowedEffects))
                {
                    continue;
                }
                var tagRatio = TagRatio(descriptor.Tags, requestedTags);
                if (requestedTags is not null && requestedTags.Count != 0 && tagRatio <= 0.0)
                {
                    continue;
                }

                var typeScore = 0.0;
                if (accepts.Value is { } acceptedType)
                {
                    var score = BestTypeScore(acceptedType, _inputTypes[i], downstream: true);
                    if (score is null)
                    {
                        continue;
                    }
                    typeScore = score.Value;
                }
                if (produces.Value is { } producedType)
                {
                    var score = BestTypeScore(producedType, _outputTypes[i], downstream: false);
                    if (score is null)
                    {
                        continue;
                    }
                    if (score.Value > typeScore)
                    {
                        typeScore = score.Value;
                    }
                }

                var textScore = TextScore(i, queryTokens, normalizedQuery);
                var total = ScoreText * textScore
                    + ScoreType * typeScore
                    + ScoreTag * tagRatio
                    - (descriptor.Destructive ? ScoreDestructive : 0.0);
                scored.Add(new RankedOperation(i, total));
            }

            scored.Sort();
            var limit = Math.Min(request.Limit, MaxLimit);
            var items = new List<OperationSearchResult>(
                Math.Min(limit, Math.Max(0, scored.Count - request.Offset)));
            var end = request.Offset;
            for (; end < scored.Count && end - request.Offset < limit; end++)
            {
                var operation = operations[scored[end].Index];
                var descriptor = operation.Descriptor;
                var (displayName, summary) = ResolveText(descriptor, language);
                items.Add(new OperationSearchResult(
                    operation,
                    displayName,
                    summary,
                    PortMap(descriptor.Inputs),
                    PortMap(descriptor.Outputs),
                    scored[end].Score));
            }
            return Result<OperationSearchPage>.Success(
                new OperationSearchPage(items, end < scored.Count ? end : null));
        }

        /// <summary>
        /// The SPEC §C7 compatible-Operation search: every Operation/port
        /// pair that connects to the queried type in the request direction.
        /// A malformed or unregistered Type ID fails with OW-TYPE-001/005, an
        /// unknown Operation with OW-OP-001 and a port the direction side
        /// does not declare with OW-OP-005.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
        public Result<IReadOnlyList<CompatibleOperationMatch>> SearchCompatibleOperations(
            CompatibleOperationsRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            TypeRef queried;
            if (request.TypeId is not null)
            {
                var type = ParseAndResolve(request.TypeId);
                if (!type.Ok)
                {
                    return Result<IReadOnlyList<CompatibleOperationMatch>>.Failure(
                        type.Diagnostics);
                }
                queried = type.Value!;
            }
            else
            {
                var operation = _snapshot.FindLatest(request.OperationId!);
                if (operation is null)
                {
                    return Result<IReadOnlyList<CompatibleOperationMatch>>.Failure(
                        ErrorCodes.Create(
                            ErrorCodes.OW_OP_001,
                            $"Unknown operation '{request.OperationId}'."));
                }
                var ports = request.Direction == CompatibleDirection.Downstream
                    ? operation.Descriptor.Outputs
                    : operation.Descriptor.Inputs;
                PortDescriptor? port = null;
                for (var p = 0; p < ports.Count; p++)
                {
                    if (string.Equals(ports[p].Name, request.Port, StringComparison.Ordinal))
                    {
                        port = ports[p];
                        break;
                    }
                }
                if (port is null)
                {
                    var side = request.Direction == CompatibleDirection.Downstream
                        ? "output"
                        : "input";
                    return Result<IReadOnlyList<CompatibleOperationMatch>>.Failure(
                        ErrorCodes.Create(
                            ErrorCodes.OW_OP_005,
                            $"Operation '{request.OperationId}' has no {side} port '{request.Port}'.",
                            port: request.Port));
                }
                var type = ParseAndResolve(port.Type);
                if (!type.Ok)
                {
                    return Result<IReadOnlyList<CompatibleOperationMatch>>.Failure(
                        type.Diagnostics);
                }
                queried = type.Value!;
            }

            var downstream = request.Direction == CompatibleDirection.Downstream;
            var operations = _snapshot.Operations;
            var matches = new List<CompatibleOperationMatch>();
            for (var i = 0; i < operations.Count; i++)
            {
                var descriptor = operations[i].Descriptor;
                var ports = downstream ? descriptor.Inputs : descriptor.Outputs;
                var portTypes = downstream ? _inputTypes[i] : _outputTypes[i];
                for (var p = 0; p < ports.Count; p++)
                {
                    var portType = portTypes[p];
                    if (portType is null)
                    {
                        continue;
                    }
                    var kind = downstream
                        ? Classify(queried, portType)
                        : Classify(portType, queried);
                    if (kind is null ||
                        (kind.Value == CompatibleMatchKind.Json && !request.IncludeJson))
                    {
                        continue;
                    }
                    matches.Add(new CompatibleOperationMatch(
                        operations[i], ports[p].Name, kind.Value));
                }
            }
            matches.Sort(CompareMatches);
            return Result<IReadOnlyList<CompatibleOperationMatch>>.Success(matches);
        }

        private static readonly Comparison<CompatibleOperationMatch> CompareMatches =
            (a, b) =>
            {
                var byKind = MatchRank(a.Match).CompareTo(MatchRank(b.Match));
                if (byKind != 0)
                {
                    return byKind;
                }
                var byId = string.CompareOrdinal(
                    a.Operation.Descriptor.Id, b.Operation.Descriptor.Id);
                if (byId != 0)
                {
                    return byId;
                }
                var byVersion = a.Operation.Descriptor.Version.CompareTo(
                    b.Operation.Descriptor.Version);
                if (byVersion != 0)
                {
                    return byVersion;
                }
                return string.CompareOrdinal(a.Port, b.Port);
            };

        private static int MatchRank(CompatibleMatchKind kind) => kind switch
        {
            CompatibleMatchKind.Exact => 0,
            CompatibleMatchKind.Base => 1,
            _ => 2,
        };

        // The (snapshot index, score) pairs the ranked list is built from:
        // score descending first, then the canonical §C3 member order — ID
        // Ordinal, then version — which Operations is already sorted by.
        private readonly struct RankedOperation : IComparable<RankedOperation>
        {
            internal RankedOperation(int index, double score)
            {
                Index = index;
                Score = score;
            }

            internal int Index { get; }

            internal double Score { get; }

            public int CompareTo(RankedOperation other)
            {
                var byScore = other.Score.CompareTo(Score);
                return byScore != 0 ? byScore : Index.CompareTo(other.Index);
            }
        }

        private double TextScore(
            int index, IReadOnlyList<string> queryTokens, string? normalizedQuery)
        {
            if (queryTokens.Count == 0)
            {
                return 0.0;
            }
            if (normalizedQuery is not null &&
                string.Equals(_normalizedIds[index], normalizedQuery, StringComparison.Ordinal))
            {
                return 1.0;
            }
            var tokens = _fieldTokens[index];
            var words = _fieldWords[index];
            var matched = 0;
            foreach (var token in queryTokens)
            {
                if (SearchTokenizer.IsWordToken(token))
                {
                    for (var w = 0; w < words.Count; w++)
                    {
                        if (words[w].StartsWith(token, StringComparison.Ordinal))
                        {
                            matched++;
                            break;
                        }
                    }
                }
                else if (tokens.Contains(token))
                {
                    matched++;
                }
            }
            return (double)matched / queryTokens.Count;
        }

        // The best §C6 type score a port list offers the query type: exact
        // 1.0, base-type 0.7, core.Json 0.1 — or null when no port connects.
        private double? BestTypeScore(
            TypeRef queried, TypeRef?[] portTypes, bool downstream)
        {
            var best = 0.0;
            var any = false;
            for (var p = 0; p < portTypes.Length; p++)
            {
                var portType = portTypes[p];
                if (portType is null)
                {
                    continue;
                }
                var kind = downstream
                    ? Classify(queried, portType)
                    : Classify(portType, queried);
                if (kind is null)
                {
                    continue;
                }
                any = true;
                var score = kind.Value == CompatibleMatchKind.Exact
                    ? TypeExact
                    : kind.Value == CompatibleMatchKind.Base
                        ? TypeBase
                        : TypeJson;
                if (score > best)
                {
                    best = score;
                }
            }
            return any ? best : (double?)null;
        }

        // The shared (source → target) classifier: the B6 question "may a
        // value of source fill a position typed target", mapped onto the §C7
        // match kinds — an identical Type ID is exact, a pair that connects
        // only through the core.Json rule (§B6 rule 2), whether the target
        // itself is core.Json or a covariant generic argument reaches it, is
        // json, every other assignable pair is base.
        private CompatibleMatchKind? Classify(TypeRef source, TypeRef target)
        {
            if (source.Equals(target))
            {
                return CompatibleMatchKind.Exact;
            }
            if (!_snapshot.Types.IsAssignable(source, target).Assignable)
            {
                return null;
            }
            return AssignableWithoutJson(source, target)
                ? CompatibleMatchKind.Base
                : CompatibleMatchKind.Json;
        }

        // Whether the pair still connects with §B6 rule 2 disabled, answered
        // through the public ITypeRegistry surface alone so any conforming
        // implementation — a wrapper included — classifies identically. The
        // mirror re-runs the registry's own rule order: identity, resolution
        // and well-formedness, declared base types, covariant generic
        // arguments, then the class-id clause. Only the class-relation
        // provider's verdict is unreachable through the interface, so the
        // clause reuses the full IsAssignable — exact when rule 2 can fire
        // nowhere inside that call for this pair (a Json-free target tree and
        // no Json-decided constraint check inside the pair's own traversal)
        // and otherwise conservatively false: when Json dependence cannot be
        // ruled out the pair classifies as json, never as base.
        private bool AssignableWithoutJson(TypeRef source, TypeRef target)
        {
            if (_withoutJson.TryGetValue((source, target), out var cached))
            {
                return cached;
            }
            var result = DecideWithoutJson(source, target);
            _withoutJson.Add((source, target), result);
            return result;
        }

        private bool DecideWithoutJson(TypeRef source, TypeRef target)
        {
            // Rule 1: identical types. Rule 2 — anything flows to core.Json —
            // is the very rule this re-run excludes, so it is absent here and
            // a bare core.Json target simply falls through to the ordinary
            // checks (which no non-Json source survives).
            if (source.Equals(target))
            {
                return true;
            }

            var types = _snapshot.Types;
            var sourceLeaf = IsClassLeaf(source);
            var targetLeaf = IsClassLeaf(target);
            TypeDefinition? sourceDef = null;
            TypeDefinition? targetDef = null;
            if (!sourceLeaf)
            {
                var resolved = types.Resolve(source);
                if (!resolved.Ok)
                {
                    return false;
                }
                sourceDef = resolved.Value;
            }
            if (!targetLeaf)
            {
                var resolved = types.Resolve(target);
                if (!resolved.Ok)
                {
                    return false;
                }
                targetDef = resolved.Value;
            }
            if (sourceDef is not null && !WellFormedWithoutJson(source, sourceDef))
            {
                return false;
            }
            if (targetDef is not null && !WellFormedWithoutJson(target, targetDef))
            {
                return false;
            }

            // Rule 3: declared base types, transitively.
            if (sourceDef is not null)
            {
                var baseTypes = sourceDef.BaseTypes;
                for (var i = 0; i < baseTypes.Count; i++)
                {
                    var parsed = TypeRef.Parse(baseTypes[i]);
                    if (parsed.Ok && AssignableWithoutJson(parsed.Value!, target))
                    {
                        return true;
                    }
                }
            }

            // Rule 4: same generic definition → per-argument covariance; a
            // Json-reachable argument pair fails the whole clause.
            if (sourceDef is not null &&
                sourceDef.GenericArity > 0 &&
                source.Args.Count == sourceDef.GenericArity &&
                string.Equals(source.Name, target.Name, StringComparison.Ordinal) &&
                target.Args.Count == source.Args.Count)
            {
                for (var i = 0; i < source.Args.Count; i++)
                {
                    if (!AssignableWithoutJson(source.Args[i], target.Args[i]))
                    {
                        return false;
                    }
                }
                return true;
            }

            // Rule 4's class clause: two class ids still undecided → the
            // snapshot's class-relation provider, which ITypeRegistry does
            // not expose. Reusing the full IsAssignable is exact here when
            // rule 2 can change no verdict inside that call for this pair —
            // the target tree carries no bare core.Json node and no
            // constraint check the pair's traversal reaches flips its
            // outcome under the Json rule. When that cannot be guaranteed,
            // the verdict might rest on the Json rule, so the pair stays
            // not-without-Json.
            var sourceClass = sourceLeaf || (sourceDef is not null && sourceDef.Kind == TypeKind.Class);
            var targetClass = targetLeaf || (targetDef is not null && targetDef.Kind == TypeKind.Class);
            return sourceClass && targetClass &&
                !ContainsBareJson(target) &&
                !JsonRuleCouldDecideInside(source, target) &&
                types.IsAssignable(source, target).Assignable;
        }

        // The well-formedness check the registry runs on every resolved
        // generic use, re-run without the Json rule: each argument resolves
        // (or is a class-id leaf), is itself well formed and satisfies the
        // definition's declared GenericConstraint.
        private bool WellFormedWithoutJson(TypeRef type, TypeDefinition definition)
        {
            var constraint = definition.GenericConstraint;
            for (var i = 0; i < type.Args.Count; i++)
            {
                var arg = type.Args[i];
                if (!IsClassLeaf(arg))
                {
                    var resolved = _snapshot.Types.Resolve(arg);
                    if (!resolved.Ok || !WellFormedWithoutJson(arg, resolved.Value!))
                    {
                        return false;
                    }
                }
                if (constraint is not null && !SatisfiesConstraintWithoutJson(arg, constraint))
                {
                    return false;
                }
            }
            return true;
        }

        // The constraint is a class id or another Type ID; an argument
        // satisfies it by being assignable to it without the Json rule. A
        // constraint that is neither form is malformed and unsatisfiable —
        // the registry's fail-closed choice, mirrored.
        private bool SatisfiesConstraintWithoutJson(TypeRef arg, string constraint)
        {
            var required = ParseConstraint(constraint);
            return required is not null && AssignableWithoutJson(arg, required);
        }

        // The constraint string as the TypeRef the satisfaction check is run
        // against — a 'type:' class-id leaf or another Type ID, null when it
        // is neither — the same interpretation the registry's
        // SatisfiesConstraint gives it.
        private static TypeRef? ParseConstraint(string constraint)
        {
            if (constraint.StartsWith(TypeRef.ClassRefPrefix, StringComparison.Ordinal))
            {
                return new TypeRef(constraint, Array.Empty<TypeRef>());
            }
            var parsed = TypeRef.Parse(constraint);
            return parsed.Ok ? parsed.Value : null;
        }

        private static bool IsClassLeaf(TypeRef type) =>
            type.Name.StartsWith(TypeRef.ClassRefPrefix, StringComparison.Ordinal);

        // Whether the type tree carries a bare core.Json node anywhere — the
        // only target shape §B6 rule 2 can fire on.
        private static bool ContainsBareJson(TypeRef type)
        {
            if (type.Args.Count == 0)
            {
                return string.Equals(type.Name, CoreJsonName, StringComparison.Ordinal);
            }
            for (var i = 0; i < type.Args.Count; i++)
            {
                if (ContainsBareJson(type.Args[i]))
                {
                    return true;
                }
            }
            return false;
        }

        // Whether §B6 rule 2 could change a verdict inside the full
        // IsAssignable(source, target) call for this pair: besides a bare
        // core.Json target, the only place the rule can fire is inside a
        // GenericConstraint check — satisfaction is itself an assignability
        // question, so a constraint consulted anywhere in the traversal can
        // flip a well-formedness verdict. The set of definitions the call can
        // consult is the closure of the pair over each resolved definition's
        // generic arguments, declared base types and constraint; for every
        // reachable constraint the mirror replays the satisfaction check per
        // argument — exactly as the registry's IsWellFormed does — and a
        // check that passes only under the Json rule marks the whole pair as
        // possibly Json-decided. A constraint unrelated to the pair is never
        // reached, so its Json content cannot hide the connection.
        private bool JsonRuleCouldDecideInside(TypeRef source, TypeRef target)
        {
            var types = _snapshot.Types;
            var seen = new HashSet<TypeRef>();
            var pending = new Queue<TypeRef>();
            pending.Enqueue(source);
            pending.Enqueue(target);
            while (pending.Count > 0)
            {
                var type = pending.Dequeue();
                if (!seen.Add(type) || IsClassLeaf(type))
                {
                    continue;
                }
                var resolved = types.Resolve(type);
                if (!resolved.Ok)
                {
                    continue;
                }
                var definition = resolved.Value!;
                for (var i = 0; i < type.Args.Count; i++)
                {
                    pending.Enqueue(type.Args[i]);
                }
                var baseTypes = definition.BaseTypes;
                for (var i = 0; i < baseTypes.Count; i++)
                {
                    var parsed = TypeRef.Parse(baseTypes[i]);
                    if (parsed.Ok)
                    {
                        pending.Enqueue(parsed.Value!);
                    }
                }
                var constraint = definition.GenericConstraint;
                if (constraint is null || type.Args.Count == 0)
                {
                    continue;
                }
                var required = ParseConstraint(constraint);
                if (required is null)
                {
                    continue;
                }
                // The constraint's own tree joins the traversal — inside the
                // satisfaction call it is resolved and its arguments checked
                // the same way.
                pending.Enqueue(required);
                for (var i = 0; i < type.Args.Count; i++)
                {
                    var arg = type.Args[i];
                    if (types.IsAssignable(arg, required).Assignable &&
                        !AssignableWithoutJson(arg, required))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        // Parses and registers-resolves a request Type ID so a malformed or
        // unknown filter fails the request once with the OW-TYPE-001/005
        // diagnostic instead of degrading to an empty result. A null filter
        // type is no filter at all.
        private Result<TypeRef?> ResolveFilterType(string? typeId)
        {
            if (typeId is null)
            {
                return Result<TypeRef?>.Success(null);
            }
            var resolved = ParseAndResolve(typeId);
            return resolved.Ok
                ? Result<TypeRef?>.Success(resolved.Value)
                : Result<TypeRef?>.Failure(resolved.Diagnostics);
        }

        private Result<TypeRef> ParseAndResolve(string typeId)
        {
            var parsed = TypeRef.Parse(typeId);
            if (!parsed.Ok)
            {
                return Result<TypeRef>.Failure(parsed.Diagnostics);
            }
            var resolved = _snapshot.Types.Resolve(parsed.Value!);
            return resolved.Ok
                ? Result<TypeRef>.Success(parsed.Value!)
                : Result<TypeRef>.Failure(resolved.Diagnostics);
        }

        private static bool EffectsSubset(
            IReadOnlyList<string> declared, HashSet<string> allowed)
        {
            for (var i = 0; i < declared.Count; i++)
            {
                if (!allowed.Contains(declared[i]))
                {
                    return false;
                }
            }
            return true;
        }

        // The ratio of the requested tags the Operation carries (distinct,
        // Ordinal) — the §C6 tag term; zero when no tags were requested or
        // none matched.
        private static double TagRatio(
            IReadOnlyList<string> declared, IReadOnlyList<string>? requested)
        {
            if (requested is null || requested.Count == 0)
            {
                return 0.0;
            }
            var matched = 0;
            foreach (var tag in requested)
            {
                for (var i = 0; i < declared.Count; i++)
                {
                    if (string.Equals(declared[i], tag, StringComparison.Ordinal))
                    {
                        matched++;
                        break;
                    }
                }
            }
            return (double)matched / requested.Count;
        }

        private static List<string> DistinctOrdinal(IReadOnlyList<string> values)
        {
            var distinct = new List<string>(values.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (seen.Add(value))
                {
                    distinct.Add(value);
                }
            }
            return distinct;
        }

        // The §C6 item-5 language choice: the localized text of the
        // requested language when one is registered, English otherwise.
        private static (string DisplayName, string Summary) ResolveText(
            OperationDescriptor descriptor, string language)
        {
            if (descriptor.Localized.TryGetValue(language, out var text))
            {
                return (text.DisplayName, text.Summary);
            }
            return (descriptor.DisplayName, descriptor.Summary);
        }

        private static IReadOnlyDictionary<string, string> PortMap(
            IReadOnlyList<PortDescriptor> ports)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < ports.Count; i++)
            {
                map.Add(ports[i].Name, ports[i].Type);
            }
            return map;
        }

        private static TypeRef?[] ParsePortTypes(IReadOnlyList<PortDescriptor> ports)
        {
            var types = new TypeRef?[ports.Count];
            for (var i = 0; i < ports.Count; i++)
            {
                var parsed = TypeRef.Parse(ports[i].Type);
                types[i] = parsed.Ok ? parsed.Value : null;
            }
            return types;
        }

        // The union of the descriptor's §C5 indexed fields in one token set —
        // the text a query's match ratio divides over. Fields follow
        // OperationSearchIndex: the ID's identifier tokens, DisplayName,
        // Summary, the joined Tags and Keywords, and every localized
        // language's DisplayName, Summary and Keywords.
        private static void IndexField(
            OperationDescriptor descriptor, HashSet<string> tokens, List<string> words)
        {
            AddTokens(SearchTokenizer.TokenizeIdentifier(descriptor.Id), tokens, words);
            AddTokens(SearchTokenizer.Tokenize(descriptor.DisplayName), tokens, words);
            AddTokens(SearchTokenizer.Tokenize(descriptor.Summary), tokens, words);
            AddTokens(ElementTokens(descriptor.Tags), tokens, words);
            AddTokens(ElementTokens(descriptor.Keywords), tokens, words);
            foreach (var pair in descriptor.Localized)
            {
                var text = pair.Value;
                AddTokens(SearchTokenizer.Tokenize(text.DisplayName), tokens, words);
                AddTokens(SearchTokenizer.Tokenize(text.Summary), tokens, words);
                AddTokens(ElementTokens(text.Keywords), tokens, words);
            }
        }

        private static IReadOnlyList<string> ElementTokens(IReadOnlyList<string> elements) =>
            SearchTokenizer.Tokenize(string.Join(" ", elements));

        private static void AddTokens(
            IReadOnlyList<string> field, HashSet<string> tokens, List<string> words)
        {
            foreach (var token in field)
            {
                if (tokens.Add(token) && SearchTokenizer.IsWordToken(token))
                {
                    words.Add(token);
                }
            }
        }
    }
}
