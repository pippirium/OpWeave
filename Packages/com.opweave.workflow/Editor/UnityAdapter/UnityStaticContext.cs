#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The production <see cref="StaticContext"/> (SPEC §0.6 SG-06): the
    /// read-only context <see cref="IOperation.ValidateStatic"/> and
    /// <see cref="IEffectResolver"/> see inside the snapshot-backed
    /// <see cref="WorkflowCompiler"/>. It exposes the node's resolved
    /// parameters as written — a port fed by a data edge or a workflow
    /// input stays <see cref="InputValue.Missing"/> here because a static
    /// check never sees runtime values.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Get{T}"/> applies the same conversion order and the same
    /// strictness as <see cref="UnityStepContext.Get{T}"/> — a
    /// <see cref="JsonValue"/> read returns the stored value verbatim,
    /// <see cref="Scene"/>/<see cref="UnityEngine.Object"/> reads go through
    /// the §G2 Ref resolution, a port whose static Type ID carries an
    /// <see cref="IClrValueCodec{T}"/> converts through it, and the
    /// codec-less <c>core.*</c> primitives convert strictly by the port's
    /// static Type ID — so an Operation cannot observe a different
    /// conversion contract at static-check time than the Runner applies at
    /// run time. A refused or failed conversion raises
    /// <see cref="StepContextException"/> carrying the diagnostic; a missing
    /// port stays the programming error
    /// <see cref="InvalidOperationException"/>, matching the contract that an
    /// Operation checks <see cref="Parameters"/> presence first. The D3 phase
    /// reports any escaping exception as OW-RUN-006 like every other
    /// Operation exception (SPEC §C2 item 8).
    /// </para>
    /// </remarks>
    internal sealed class UnityStaticContext : StaticContext
    {
        private readonly IReadOnlyDictionary<string, InputValue> _parameters;
        private readonly IReadOnlyDictionary<string, TypeRef> _declaredTypes;
        private readonly ITypeRegistry _types;
        private readonly RefResolver _refs;

        /// <param name="declaredTypes">
        /// The port name → declared Type ID map of the node's Operation —
        /// the static-type table <see cref="Get{T}"/> chooses its conversion
        /// through. The statically resolved types <see cref="StaticTypeOf"/>
        /// reports are the compiler's own
        /// <see cref="OpWeave.Core.Compiler.StaticTypedContext"/> decorator
        /// (an edge-fed port's supplier type, a bound workflow input's
        /// declared type), which wraps this context at every phase — this
        /// table only names what the Descriptor declares so a conversion
        /// never depends on where the value came from.
        /// </param>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal UnityStaticContext(
            string nodeId,
            IReadOnlyDictionary<string, InputValue> parameters,
            IReadOnlyDictionary<string, TypeRef> declaredTypes,
            ITypeRegistry types,
            RefResolver refs)
        {
            NodeId = nodeId ?? throw new ArgumentNullException(nameof(nodeId));
            _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
            _declaredTypes = declaredTypes ?? throw new ArgumentNullException(nameof(declaredTypes));
            _types = types ?? throw new ArgumentNullException(nameof(types));
            _refs = refs ?? throw new ArgumentNullException(nameof(refs));
        }

        /// <inheritdoc/>
        public override string NodeId { get; }

        /// <inheritdoc/>
        public override IReadOnlyDictionary<string, InputValue> Parameters => _parameters;

        /// <inheritdoc cref="StaticContext.Get{T}"/>
        /// <exception cref="ArgumentNullException"><paramref name="port"/> is null.</exception>
        /// <exception cref="InvalidOperationException"><paramref name="port"/> is missing.</exception>
        /// <exception cref="StepContextException">
        /// The value could not be resolved or converted (SPEC §0.6 SG-06).
        /// </exception>
        public override T Get<T>(string port)
        {
            if (port is null)
            {
                throw new ArgumentNullException(nameof(port));
            }
            if (!_parameters.TryGetValue(port, out var input) || !input.IsPresent)
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
                    throw new StepContextException(ConversionFailed(port, e));
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
                    throw new StepContextException(ConversionFailed(port, e));
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
            var codec = ClrCodecOf<T>(port);
            if (codec is not null)
            {
                try
                {
                    return codec.FromJson(value);
                }
                catch (Exception e)
                {
                    throw new StepContextException(ConversionFailed(port, e));
                }
            }
            return ReadCoreValue<T>(port, value);
        }

        private IClrValueCodec<T>? ClrCodecOf<T>(string port)
        {
            if (!_declaredTypes.TryGetValue(port, out var type))
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
        // before the stored value's 'kind' is ever consulted — the same
        // §0.6 SG-06 rule UnityStepContext applies.
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

        // SG-06 for the Ref/Scene branches: the port's static type must be
        // a registered Ref type whose values can resolve to the requested
        // T before the resolver sees the value; anything else is the same
        // OW-RUN-007 mismatch the codec-less tail reports.
        private void CheckRefPort<T>(string port)
        {
            _declaredTypes.TryGetValue(port, out var portType);
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
        // resolve to the requested T — the same §G2 yield table
        // UnityStepContext.RefYieldCompatible applies: a Scene read needs a
        // scene-compatible Ref type, the GameObject kinds need GameObject or
        // Object for T, a unity.ComponentRef<C>/unity.AssetRef<C> port needs
        // C's CLR class related to T in either direction, a unity.SceneRef
        // port never yields a UnityEngine.Object, and the undecided kinds
        // defer to the resolver's own 'kind' discriminator.
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
                return typeof(T).IsAssignableFrom(typeof(UnityEngine.GameObject));
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

        // The §G2 resolver reports the Ref value's own coordinates but
        // cannot name the port the value arrived on — the same port
        // annotation UnityStepContext applies.
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
        // static gate reports.
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

        private static Diagnostic ConversionFailed(string port, Exception e) =>
            ErrorCodes.Create(
                ErrorCodes.OW_RUN_007,
                $"The input port '{port}' value could not be converted to its CLR type.",
                port: port,
                hint: e.GetType().FullName);

        // The codec-less tail of Get<T> — identical to
        // UnityStepContext.ReadCoreValue (SPEC §0.6 SG-06): the port's
        // static Type ID decides which CLR type may read the stored JSON —
        // a core.String-family port only as string from a JSON string,
        // core.Int only as long or an in-range int from a JSON integer,
        // core.Float only as double/float from a JSON number and core.Bool
        // only as bool from a JSON boolean. Any other T, a mismatched token
        // kind or a port without a static type is the OW-RUN-007 type
        // mismatch (SPEC §J), never a silent coercion; core.Json,
        // core.List<T> and every other codec-less type stay readable only
        // as JsonValue.
        private T ReadCoreValue<T>(string port, JsonValue value)
        {
            _declaredTypes.TryGetValue(port, out var portType);
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
                    throw new StepContextException(ConversionFailed(port, e));
                }
            }
            throw new StepContextException(TypeMismatch<T>(port, portType));
        }
    }
}
