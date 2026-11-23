#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Unity <see cref="StepContext"/> implementation SPEC §0.5 assigns to
    /// OpWeave.UnityAdapter (T-W2.3-02): the context the Runner hands to an
    /// Operation's Preflight/Execute/Poll inside the Editor, converting
    /// between the step's <see cref="JsonValue"/> inputs/outputs and the CLR
    /// values the Operation code actually uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Get{T}"/> resolves in order: a <see cref="JsonValue"/> read
    /// returns the stored value verbatim (a Ref-typed value included — it
    /// travels as JSON per SPEC §E5); <typeparamref name="T"/> of
    /// <see cref="Scene"/> goes through the §G2 SceneRef resolution;
    /// <typeparamref name="T"/> backed by <see cref="UnityEngine.Object"/> is
    /// the Ref the G2 <see cref="RefResolver"/> materializes; a port whose
    /// static type carries an <see cref="IClrValueCodec{T}"/> (T-W2.1-05)
    /// converts through it; and the codec-less <c>core.*</c> primitives
    /// convert strictly by the port's static Type ID — the
    /// <c>core.String</c> family (unity.AssetPath/TypeName/HierarchyPath
    /// included) to <see cref="string"/> from a JSON string, <c>core.Int</c>
    /// to <see cref="long"/> or an in-range <see cref="int"/> from a JSON
    /// integer, <c>core.Float</c> to <see cref="double"/>/<see cref="float"/>
    /// from a JSON number and <c>core.Bool</c> to <see cref="bool"/> from a
    /// JSON boolean. Any other <typeparamref name="T"/>, a mismatched token
    /// kind or a port without a static type is refused — <c>core.Json</c>,
    /// <c>core.List&lt;T&gt;</c> and the Ref/class types stay readable only as
    /// <see cref="JsonValue"/>. A conversion that fails or is refused raises
    /// <see cref="StepContextException"/> carrying the diagnostic the step
    /// fails with — the resolver's own findings (OW-UNITY-001…004, malformed
    /// Ref shape OW-TYPE-003) or OW-RUN-007 for a CLR/Codec conversion
    /// failure or a Get type mismatch (SPEC §B7.1 item 4, §J, §0.6 SG-06) —
    /// while a missing or pending port stays the programming error
    /// <see cref="InvalidOperationException"/>,
    /// matching the contract that an Operation checks
    /// <see cref="IsPresent"/>/<see cref="IsPending"/> first. A missing input
    /// is never answered with <see langword="null"/> (SPEC §A0.1); an
    /// explicit JSON <c>null</c> stays a present value.
    /// </para>
    /// <para>
    /// <see cref="SetOutput{T}"/> performs the inverse conversions under the
    /// same static-Type-ID rule: a <see cref="JsonValue"/> argument stores
    /// verbatim on any port; a <see cref="UnityEngine.Object"/> becomes the
    /// fully keyed Ref of the port's declared Ref Type ID through
    /// <see cref="RefResolver.ToRef"/> (SPEC §G2 "出力する Ref には持てる
    /// キーをすべて埋める") — a port that declares no Ref type, or an
    /// incompatible (object, Ref type) pair, is OW-RUN-007; a
    /// CLR-codec-backed type goes through <see cref="IClrValueCodec{T}.ToJson"/>
    /// on the port's own codec; and the codec-less <c>core.*</c> primitives
    /// write strictly by the port's static Type ID — a string to a
    /// core.String-family port, a long/int to <c>core.Int</c>, a finite
    /// double/float to <c>core.Float</c> and a bool to <c>core.Bool</c>.
    /// Every other value — a <see cref="Scene"/>, a mismatched primitive, a
    /// collection or any unknown CLR type — and any port without a static
    /// type is the OW-RUN-007 output type mismatch (SPEC §J), never a
    /// silent token coercion; <c>core.Json</c>, <c>core.List&lt;T&gt;</c>
    /// and the Ref/class types stay writable only as
    /// <see cref="JsonValue"/>. A <see langword="null"/> argument records
    /// an explicit JSON <c>null</c> — a present output, never a missing
    /// one — whose conformance the output validation decides (SPEC §A0.1).
    /// </para>
    /// <para>
    /// A Dry-Run pending input (SPEC §D8 item 1) is the port → producing
    /// nodeId map handed at construction: <see cref="IsPending"/> reports it,
    /// <see cref="IsPresent"/> counts it as a placeholder, and
    /// <see cref="Get{T}"/> refuses it naming the producer. Real Runs carry
    /// no pending ports.
    /// </para>
    /// </remarks>
    public sealed class UnityStepContext : StepContext, IRunResourceRegistry
    {
        private readonly IReadOnlyDictionary<string, InputValue> _inputs;
        private readonly IReadOnlyDictionary<string, TypeRef> _inputTypes;
        private readonly IReadOnlyDictionary<string, TypeRef> _outputTypes;
        private readonly ITypeRegistry _types;
        private readonly RefResolver _refs;
        private readonly IReadOnlyDictionary<string, string> _pending;
        private readonly string? _operationId;
        private readonly Dictionary<string, JsonValue> _outputs =
            new Dictionary<string, JsonValue>(StringComparer.Ordinal);

        /// <summary>
        /// Creates the context over one step's resolved
        /// <paramref name="inputs"/> (the ValueStore's SPEC §E5 product).
        /// <paramref name="inputTypes"/>/<paramref name="outputTypes"/> map a
        /// port name to its static Type ID — the input ports' SPEC §0.6
        /// SG-06 static types (an edge-fed port's source output type, a
        /// bound workflow input's declared type, a constant/default port's
        /// declared type) and the Plan's resolved
        /// <c>PlanStep.OutputTypes</c> —
        /// which select each port's Codec/Ref conversion; an absent entry
        /// leaves the port readable only as <see cref="JsonValue"/>.
        /// <paramref name="pendingProducers"/>
        /// marks the Dry-Run placeholder ports with their producing nodeId.
        /// </summary>
        /// <param name="operationId">
        /// The id of the Operation the context serves, required for the SPEC
        /// §F6 provenance stamp when <paramref name="operationOrigin"/> is
        /// <see cref="OperationOrigin.Custom"/>; optional otherwise.
        /// </param>
        /// <param name="operationOrigin">
        /// The registration's origin for that Operation. When
        /// <see cref="OperationOrigin.Custom"/>, <see cref="Log"/> becomes the
        /// §F6 <see cref="ProvenanceLogSink"/> stamping
        /// <c>data.operationId</c>/<c>data.origin</c> on every entry — the
        /// shared stamping both the Runner's host and any other constructor
        /// of this context go through, so the log data a step produces does
        /// not depend on who built its context.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="inputs"/>, <paramref name="types"/>,
        /// <paramref name="refs"/>, <paramref name="changes"/> or
        /// <paramref name="log"/> is null.
        /// </exception>
        /// <exception cref="ArgumentException">
        /// A port type string in <paramref name="inputTypes"/> or
        /// <paramref name="outputTypes"/> does not parse (SPEC §B1) — a
        /// caller bug, never a runtime condition on a validated Plan; or
        /// <paramref name="operationOrigin"/> is
        /// <see cref="OperationOrigin.Custom"/> without an
        /// <paramref name="operationId"/>.
        /// </exception>
        public UnityStepContext(
            IReadOnlyDictionary<string, InputValue> inputs,
            IReadOnlyDictionary<string, string>? inputTypes,
            IReadOnlyDictionary<string, string>? outputTypes,
            ITypeRegistry types,
            RefResolver refs,
            IChangeRecorder changes,
            IRunLogSink log,
            JsonValue? resumeToken = null,
            IReadOnlyDictionary<string, string>? pendingProducers = null,
            string? operationId = null,
            OperationOrigin? operationOrigin = null)
        {
            _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
            _types = types ?? throw new ArgumentNullException(nameof(types));
            _refs = refs ?? throw new ArgumentNullException(nameof(refs));
            Changes = changes ?? throw new ArgumentNullException(nameof(changes));
            if (log is null)
            {
                throw new ArgumentNullException(nameof(log));
            }
            Log = operationOrigin == OperationOrigin.Custom
                ? new ProvenanceLogSink(
                    log,
                    operationId ?? throw new ArgumentException(
                        "A custom Operation's context must carry its Operation id.",
                        nameof(operationId)))
                : log;
            ResumeToken = resumeToken;
            _operationId = operationId;
            _inputTypes = ParsePortTypes(inputTypes, nameof(inputTypes));
            _outputTypes = ParsePortTypes(outputTypes, nameof(outputTypes));
            _pending = pendingProducers is null
                ? ReadOnlyCollections.CopyMap(
                    Array.Empty<KeyValuePair<string, string>>())
                : ReadOnlyCollections.CopyMap(pendingProducers);
        }

        /// <inheritdoc cref="StepContext.Outputs"/>
        internal override IReadOnlyDictionary<string, JsonValue> Outputs => _outputs;

        /// <summary>
        /// The id of the Operation this context serves, when the caller
        /// supplied one — the runner host and the Dry-Run factory always do.
        /// The requirement adapter reads it so a port-bound requirement
        /// knows which Operation declared it (T-W2.7-05): the
        /// <see cref="OpWeave.Core.Operations.Requirement"/> record carries
        /// only the port name.
        /// </summary>
        internal string? BoundOperationId => _operationId;

        /// <inheritdoc/>
        public override JsonValue? ResumeToken { get; }

        /// <inheritdoc/>
        public override IChangeRecorder Changes { get; }

        /// <inheritdoc/>
        public override IRunLogSink Log { get; }

        /// <inheritdoc cref="StepContext.Get{T}"/>
        /// <exception cref="ArgumentNullException"><paramref name="port"/> is null.</exception>
        /// <exception cref="InvalidOperationException">
        /// <paramref name="port"/> is missing or pending.
        /// </exception>
        /// <exception cref="StepContextException">
        /// The value could not be resolved or converted — the diagnostic the
        /// step fails with (SPEC §0.6 SG-06).
        /// </exception>
        public override T Get<T>(string port)
        {
            if (port is null)
            {
                throw new ArgumentNullException(nameof(port));
            }
            if (_pending.TryGetValue(port, out var producer))
            {
                throw new InvalidOperationException(
                    $"Input port '{port}' is a pending Dry-Run value produced by node " +
                    $"'{producer}' and has no concrete value.");
            }
            if (!_inputs.TryGetValue(port, out var input) || !input.IsPresent)
            {
                throw new InvalidOperationException(
                    $"Input port '{port}' is missing.");
            }
            var value = input.Value!;
            if (typeof(T) == typeof(JsonValue))
            {
                return (T)(object)value;
            }
            if (typeof(T) == typeof(Scene))
            {
                CheckRefPort<T>(port);
                Result<Scene> scene;
                try
                {
                    scene = _refs.ResolveScene(value);
                }
                catch (Exception e)
                {
                    throw new StepContextException(ConversionFailed(port, "input", e));
                }
                if (!scene.Ok)
                {
                    throw new StepContextException(
                        PortedDiagnostics(port, scene.Diagnostics));
                }
                return (T)(object)scene.Value!;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(typeof(T)))
            {
                CheckRefPort<T>(port);
                Result<UnityEngine.Object> resolved;
                try
                {
                    resolved = _refs.Resolve<UnityEngine.Object>(value);
                }
                catch (Exception e)
                {
                    throw new StepContextException(ConversionFailed(port, "input", e));
                }
                if (!resolved.Ok)
                {
                    throw new StepContextException(
                        PortedDiagnostics(port, resolved.Diagnostics));
                }
                var entity = resolved.Value;
                if (entity is not T typed)
                {
                    throw new StepContextException(ResolvedTypeMismatch<T>(port, entity));
                }
                return typed;
            }
            var codec = ClrCodecOf<T>(_inputTypes, port);
            if (codec is not null)
            {
                try
                {
                    return codec.FromJson(value);
                }
                catch (Exception e)
                {
                    throw new StepContextException(ConversionFailed(port, "input", e));
                }
            }
            return ReadCoreValue<T>(port, value);
        }

        /// <inheritdoc cref="StepContext.SetOutput{T}"/>
        /// <exception cref="ArgumentNullException"><paramref name="port"/> is null.</exception>
        /// <exception cref="StepContextException">
        /// The value could not be converted — the diagnostic the step fails
        /// with (SPEC §0.6 SG-06): OW-RUN-007 for a value whose CLR type has
        /// no conversion on the port's static Type ID or whose conversion
        /// fails.
        /// </exception>
        public override void SetOutput<T>(string port, T value)
        {
            if (port is null)
            {
                throw new ArgumentNullException(nameof(port));
            }
            if (value is JsonValue json)
            {
                _outputs[port] = json;
                return;
            }
            if (value is null)
            {
                _outputs[port] = JsonValue.From(JValue.CreateNull());
                return;
            }
            if (value is UnityEngine.Object obj)
            {
                _outputTypes.TryGetValue(port, out var refType);
                if (!IsRefPort(refType))
                {
                    throw new StepContextException(
                        OutputTypeMismatch<T>(port, refType));
                }
                try
                {
                    _outputs[port] = _refs.ToRef(obj, refType!.ToString());
                }
                catch (Exception e)
                {
                    throw new StepContextException(ConversionFailed(port, "output", e));
                }
                return;
            }
            var codec = ClrCodecOf<T>(_outputTypes, port);
            if (codec is not null)
            {
                try
                {
                    _outputs[port] = codec.ToJson(value);
                }
                catch (Exception e)
                {
                    throw new StepContextException(ConversionFailed(port, "output", e));
                }
                return;
            }
            WriteCoreValue(port, value);
        }

        /// <inheritdoc cref="StepContext.IsPresent"/>
        public override bool IsPresent(string port) =>
            port is not null &&
            (_pending.ContainsKey(port) ||
                (_inputs.TryGetValue(port, out var input) && input.IsPresent));

        /// <inheritdoc cref="StepContext.IsPending"/>
        public override bool IsPending(string port) =>
            port is not null && _pending.ContainsKey(port);

        /// <summary>
        /// The step's SPEC §E6 item 4 Run-resource registry behind the
        /// <see cref="IRunResourceRegistry"/> cast — bound by the runner
        /// wiring at construction; a context without one refuses Acquire and
        /// Release with <see cref="NotSupportedException"/> exactly like the
        /// Core JSON context does.
        /// </summary>
        internal IRunResourceRegistry? Resources { get; set; }

        /// <inheritdoc cref="IRunResourceRegistry.Acquire"/>
        /// <exception cref="NotSupportedException">
        /// The context carries no Run resource registry (SPEC §E6 item 4).
        /// </exception>
        void IRunResourceRegistry.Acquire(string kind, JsonValue? data) =>
            (Resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).Acquire(kind, data);

        /// <inheritdoc cref="IRunResourceRegistry.IsOpen"/>
        /// <exception cref="NotSupportedException">
        /// <see cref="Resources"/> is unset — the context was built outside the
        /// run loop, so no Run resource state exists to report.
        /// </exception>
        RunResourceOpenState IRunResourceRegistry.IsOpen(string kind, JsonValue? data) =>
            (Resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).IsOpen(kind, data);

        /// <inheritdoc cref="IRunResourceRegistry.IsHeldByRun"/>
        /// <exception cref="NotSupportedException">
        /// <see cref="Resources"/> is unset — the context was built outside the
        /// run loop, so no Run resource state exists to report.
        /// </exception>
        bool IRunResourceRegistry.IsHeldByRun(string kind, JsonValue? data) =>
            (Resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).IsHeldByRun(kind, data);

        /// <inheritdoc cref="IRunResourceRegistry.Release"/>
        /// <exception cref="NotSupportedException">
        /// The context carries no Run resource registry (SPEC §E6 item 4).
        /// </exception>
        void IRunResourceRegistry.Release(string kind, JsonValue? data) =>
            (Resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).Release(kind, data);

        /// <inheritdoc cref="IRunResourceRegistry.AcquisitionScope"/>
        /// <exception cref="NotSupportedException">
        /// <see cref="Resources"/> is unset — no acquisition exists to name.
        /// </exception>
        string IRunResourceRegistry.AcquisitionScope =>
            (Resources ?? throw new NotSupportedException(
                "The context carries no Run resource registry.")).AcquisitionScope;

        private IClrValueCodec<T>? ClrCodecOf<T>(
            IReadOnlyDictionary<string, TypeRef> portTypes, string port)
        {
            if (!portTypes.TryGetValue(port, out var type))
            {
                return null;
            }
            var resolved = _types.Resolve(type);
            return resolved.Ok && resolved.Value!.Codec is IClrValueCodec<T> codec
                ? codec
                : null;
        }

        // The §B5 Ref Type IDs the Ref/Scene Get branches gate on: a Ref
        // read is still a typed read, so the port's static Type ID decides
        // before the stored value's 'kind' is ever consulted — a Ref-shaped
        // value on a non-Ref port (core.Json included) is the OW-RUN-007
        // Get type mismatch (SPEC §0.6 SG-06, §J), never an implicit Ref.
        private static readonly TypeRef s_unitySceneRef =
            new TypeRef("unity.SceneRef", Array.Empty<TypeRef>());
        private static readonly TypeRef s_unityGameObjectRef =
            new TypeRef("unity.GameObjectRef", Array.Empty<TypeRef>());
        private static readonly TypeRef s_unityPrefabAssetRef =
            new TypeRef("unity.PrefabAssetRef", Array.Empty<TypeRef>());

        // The §B3 primitive Type IDs the codec-less Get<T> conversion keys on.
        private static readonly TypeRef s_coreString =
            new TypeRef("core.String", Array.Empty<TypeRef>());
        private static readonly TypeRef s_coreInt =
            new TypeRef("core.Int", Array.Empty<TypeRef>());
        private static readonly TypeRef s_coreFloat =
            new TypeRef("core.Float", Array.Empty<TypeRef>());
        private static readonly TypeRef s_coreBool =
            new TypeRef("core.Bool", Array.Empty<TypeRef>());

        // The codec-less tail of Get<T> (SPEC §0.6 SG-06): the port's static
        // Type ID decides which CLR type may read the stored JSON — a
        // core.String-family port (unity.AssetPath/TypeName/HierarchyPath are
        // §B3 core.String bases) only as string from a JSON string, core.Int
        // only as long or an in-range int from a JSON integer, core.Float
        // only as double/float from a JSON number and core.Bool only as bool
        // from a JSON boolean. Any other T, a mismatched token kind or a port
        // without a static type is the OW-RUN-007 type mismatch (SPEC §J),
        // never a silent coercion; core.Json, core.List<T> and every other
        // codec-less type stay readable only as JsonValue.
        private T ReadCoreValue<T>(string port, JsonValue value)
        {
            _inputTypes.TryGetValue(port, out var portType);
            var token = value.ToToken();
            if (portType is not null)
            {
                try
                {
                    if (typeof(T) == typeof(string) &&
                        token.Type == JTokenType.String &&
                        _types.IsAssignable(portType, s_coreString).Assignable)
                    {
                        return token.ToObject<T>()!;
                    }
                    if (token.Type == JTokenType.Integer &&
                        _types.IsAssignable(portType, s_coreInt).Assignable)
                    {
                        if (typeof(T) == typeof(long))
                        {
                            return token.ToObject<T>()!;
                        }
                        if (typeof(T) == typeof(int))
                        {
                            var integer = token.ToObject<long>();
                            if (integer >= int.MinValue && integer <= int.MaxValue)
                            {
                                return (T)(object)(int)integer;
                            }
                        }
                    }
                    if ((typeof(T) == typeof(double) || typeof(T) == typeof(float)) &&
                        (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) &&
                        _types.IsAssignable(portType, s_coreFloat).Assignable)
                    {
                        return token.ToObject<T>()!;
                    }
                    if (typeof(T) == typeof(bool) &&
                        token.Type == JTokenType.Boolean &&
                        _types.IsAssignable(portType, s_coreBool).Assignable)
                    {
                        return token.ToObject<T>()!;
                    }
                }
                catch (Exception e)
                {
                    throw new StepContextException(ConversionFailed(port, "input", e));
                }
            }
            throw new StepContextException(TypeMismatch<T>(port, portType));
        }

        // The codec-less tail of SetOutput<T> — the write-side mirror of
        // ReadCoreValue (SPEC §0.6 SG-06): the port's static Type ID decides
        // which CLR values may be stored — a core.String-family port
        // (unity.AssetPath/TypeName/HierarchyPath are §B3 core.String bases)
        // takes a string, core.Int a long or int, core.Float a finite
        // double or float and core.Bool a bool. Any other value or a port
        // without a static type is the same OW-RUN-007 output type mismatch
        // (SPEC §J), never a silent CLR-token coercion; core.Json,
        // core.List<T> and every other codec-less type stay writable only
        // as JsonValue.
        private void WriteCoreValue<T>(string port, T value)
        {
            _outputTypes.TryGetValue(port, out var portType);
            if (portType is not null)
            {
                try
                {
                    if (value is string text &&
                        _types.IsAssignable(portType, s_coreString).Assignable)
                    {
                        _outputs[port] = JsonValue.From(new JValue(text));
                        return;
                    }
                    if (value is bool flag &&
                        _types.IsAssignable(portType, s_coreBool).Assignable)
                    {
                        _outputs[port] = JsonValue.From(new JValue(flag));
                        return;
                    }
                    if (_types.IsAssignable(portType, s_coreInt).Assignable)
                    {
                        if (value is long wide)
                        {
                            _outputs[port] = JsonValue.From(new JValue(wide));
                            return;
                        }
                        if (value is int narrow)
                        {
                            _outputs[port] = JsonValue.From(new JValue((long)narrow));
                            return;
                        }
                    }
                    if (_types.IsAssignable(portType, s_coreFloat).Assignable)
                    {
                        if (value is double dbl && double.IsFinite(dbl))
                        {
                            _outputs[port] = JsonValue.From(new JValue(dbl));
                            return;
                        }
                        if (value is float flt && float.IsFinite(flt))
                        {
                            _outputs[port] = JsonValue.From(new JValue((double)flt));
                            return;
                        }
                    }
                }
                catch (Exception e)
                {
                    throw new StepContextException(ConversionFailed(port, "output", e));
                }
            }
            throw new StepContextException(OutputTypeMismatch<T>(port, portType));
        }

        // Whether the port's static type is a registered Ref type — the
        // only declared type a UnityEngine.Object output can be written to,
        // since RefResolver.ToRef keys the Ref shape on the §B5 Type ID. A
        // non-Ref or undeclared port is the same OW-RUN-007 mismatch the
        // Get side reports (SPEC §0.6 SG-06).
        private bool IsRefPort(TypeRef? portType)
        {
            if (portType is null)
            {
                return false;
            }
            var definition = _types.Resolve(portType);
            return definition.Ok && definition.Value!.Kind == TypeKind.Ref;
        }

        // SG-06 for the Ref/Scene branches: the port's static type must be
        // a registered Ref type whose values can resolve to the requested
        // T before the resolver sees the value; anything else is the same
        // OW-RUN-007 mismatch the codec-less tail reports.
        private void CheckRefPort<T>(string port)
        {
            _inputTypes.TryGetValue(port, out var portType);
            if (portType is not null)
            {
                var definition = _types.Resolve(portType);
                if (definition.Ok && definition.Value!.Kind == TypeKind.Ref &&
                    RefYieldCompatible<T>(portType))
                {
                    return;
                }
            }
            throw new StepContextException(TypeMismatch<T>(port, portType));
        }

        // Whether a value whose static type is the Ref type portType can
        // resolve to the requested T. A Scene read needs a port that can
        // hold scene refs — unity.SceneRef itself, a subtype, or a
        // supertype such as unity.ObjectRef. A UnityEngine.Object T reads
        // the Ref type whose resolution it can hold: the GameObject kinds
        // (GameObjectRef, PrefabInstanceRef, PrefabAssetRef and their
        // subtypes) need GameObject or Object for T,
        // unity.ComponentRef<C>/unity.AssetRef<C> need C's CLR class
        // related to T in either direction — the ref may name a C
        // subclass, and a wider C may still hold a T — while a
        // unity.SceneRef port can never yield a UnityEngine.Object.
        // unity.ObjectRef and every Ref type this mapping does not pin
        // down leave the verdict to the value's 'kind' discriminator
        // inside the resolver.
        private bool RefYieldCompatible<T>(TypeRef portType)
        {
            if (typeof(T) == typeof(Scene))
            {
                return _types.IsAssignable(portType, s_unitySceneRef).Assignable ||
                    _types.IsAssignable(s_unitySceneRef, portType).Assignable;
            }
            if (portType.Args.Count == 1 &&
                (portType.Name == "unity.ComponentRef" ||
                    portType.Name == "unity.AssetRef"))
            {
                return RefArgCompatible<T>(portType.Args[0]);
            }
            if (_types.IsAssignable(portType, s_unityGameObjectRef).Assignable ||
                _types.IsAssignable(portType, s_unityPrefabAssetRef).Assignable)
            {
                return typeof(T).IsAssignableFrom(typeof(GameObject));
            }
            if (_types.IsAssignable(portType, s_unitySceneRef).Assignable)
            {
                return false;
            }
            return true;
        }

        // The class-id argument of unity.ComponentRef<C>/unity.AssetRef<C>
        // bounds the class the ref may name: T reads it only when a C — or
        // a C subclass — can be a T, i.e. the two CLR classes are related
        // in either direction. An unresolvable class id stays undecided
        // here and reaches the resolver's own diagnostics.
        private static bool RefArgCompatible<T>(TypeRef classArg)
        {
            var clr = UnityClassRelationProvider.ResolveClassId(classArg.ToString());
            return clr is null ||
                typeof(T).IsAssignableFrom(clr) ||
                clr.IsAssignableFrom(typeof(T));
        }

        // The §G2 resolver reports the Ref value's own coordinates
        // (jsonPath inside the stored Ref) but cannot name the port the
        // value arrived on — that binding is this class's job, so a failed
        // resolution is annotated here the same way the compiler's
        // validation stages annotate diagnostics with their port.
        private static IReadOnlyList<Diagnostic> PortedDiagnostics(
            string port, IReadOnlyList<Diagnostic> diagnostics)
        {
            var ported = new Diagnostic[diagnostics.Count];
            for (var i = 0; i < diagnostics.Count; i++)
            {
                ported[i] = diagnostics[i].Port is null
                    ? diagnostics[i] with { Port = port }
                    : diagnostics[i];
            }
            return ported;
        }

        // A ref that resolved to an entity the requested CLR type cannot
        // hold is the same §0.6 SG-06 Get type mismatch OW-RUN-007 the
        // static gate reports — OW-UNITY-002 stays the §J ambiguity /
        // conflicting-keys finding the resolver itself emits.
        private static Diagnostic ResolvedTypeMismatch<T>(
            string port, UnityEngine.Object? entity) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_007,
                $"The ref of input port '{port}' resolved to a " +
                $"{entity?.GetType().Name ?? "null"}, which {typeof(T).Name} cannot hold.",
                port: port);

        private static Diagnostic TypeMismatch<T>(string port, TypeRef? portType) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_007,
                portType is null
                    ? $"The input port '{port}' has no static type and cannot " +
                      $"be read as {typeof(T).Name}."
                    : $"The input port '{port}' value of type '{portType}' cannot " +
                      $"be read as {typeof(T).Name}.",
                port: port);

        private static Diagnostic OutputTypeMismatch<T>(string port, TypeRef? portType) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_007,
                portType is null
                    ? $"The output port '{port}' has no static type and cannot " +
                      $"be written as {typeof(T).Name}."
                    : $"The output port '{port}' of type '{portType}' cannot " +
                      $"be written as {typeof(T).Name}.",
                port: port);

        private static Diagnostic ConversionFailed(string port, string direction, Exception e) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_007,
                $"The {direction} port '{port}' value could not be converted to its CLR type.",
                port: port,
                hint: e.GetType().FullName);

        private static IReadOnlyDictionary<string, TypeRef> ParsePortTypes(
            IReadOnlyDictionary<string, string>? portTypes, string parameterName)
        {
            var parsed = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
            if (portTypes is null)
            {
                return parsed;
            }
            foreach (var pair in portTypes)
            {
                var type = TypeRef.Parse(pair.Value);
                if (!type.Ok)
                {
                    throw new ArgumentException(
                        $"Port '{pair.Key}' carries an unparseable Type ID " +
                        $"'{pair.Value}'.", parameterName);
                }
                parsed[pair.Key] = type.Value!;
            }
            return parsed;
        }
    }
}
