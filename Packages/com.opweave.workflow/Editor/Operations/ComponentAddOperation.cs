#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.component.add</c> (SPEC §I.1, §G5 item 5): adds a component
    /// of the fully qualified <c>componentType</c> to the target
    /// GameObject. The output is <c>unity.ComponentRef&lt;unity.Component&gt;</c>
    /// refined to <c>unity.ComponentRef&lt;{0}&gt;</c> when
    /// <c>componentType</c> is a constant (SPEC §C1 OutputRefinement: a
    /// constant "UnityEngine.Animator" yields
    /// <c>unity.ComponentRef&lt;unity.Animator&gt;</c>). Undo
    /// <see cref="UndoSupport.Full"/>, <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Type resolution.</b> §G5 item 5 names TypeCache as the resolution
    /// surface, but Unity 6000.0's TypeCache exposes no by-name lookup
    /// (documented on <see cref="UnityTypeProbe"/> and
    /// <see cref="UnityClassRelationProvider"/>), so the fully qualified
    /// name resolves against the same loaded type space through
    /// <see cref="UnityClassRelationProvider.ResolveClassId"/>. A name that
    /// resolves to nothing, a type that is not <see cref="Component"/>-
    /// derived, and a type that cannot be added (abstract, interface,
    /// open generic) are all OW-UNITY-006 — as is a second component of a
    /// <see cref="DisallowMultipleComponent"/> type.
    /// </para>
    /// <para>
    /// <b>The §F5 evidence row.</b> Preflight records the target's
    /// GameObjectRef, the ordered component list (type · handle ·
    /// serialized content), the requested type, the expected additions
    /// and the session id. The expected additions are computed by
    /// reflection over the type's <see cref="RequireComponent"/>
    /// attributes (transitively, filtered to the types the target
    /// lacks) — never by adding the type to a probe: Preflight must
    /// not run the editor callbacks a managed component fires when it
    /// is instantiated (SPEC §C2 item 7). An addition whose default
    /// serialized content cannot be captured without instantiating a
    /// managed type is recorded without content. Reconcile in the same
    /// session returns <see cref="ReconcileResult.Completed"/> only when
    /// the snapshot's components are all still in place as a prefix —
    /// unchanged in content — and exactly the expected additions follow,
    /// each with its recorded default content, and rebuilds the
    /// <c>component</c> output from the added component. An unchanged
    /// list is <see cref="ReconcileResult.NotApplied"/>; anything else is
    /// <see cref="ReconcileResult.Unknown"/>.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class ComponentAddOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.component.add";

        internal const string TargetPort = "target";
        internal const string ComponentTypePort = "componentType";
        internal const string ComponentPort = "component";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // §D8 item 1: an edge-fed port has no value to inspect in a Dry
            // Run; the evidence row cannot be captured either.
            if (ctx.IsPending(TargetPort) || ctx.IsPending(ComponentTypePort))
            {
                return base.Preflight(ctx);
            }
            GameObject target;
            try
            {
                target = ctx.Get<GameObject>(TargetPort);
            }
            catch (StepContextException failure)
            {
                return new PreflightResult(
                    failure.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var typeName = ctx.Get<string>(ComponentTypePort);
            var type = ResolveComponentType(typeName);
            var invalid = CheckAddable(target, type);
            List<Type>? planned = null;
            Dictionary<string, JToken>? defaults = null;
            if (invalid is null)
            {
                planned = PlanAdditions(type!, target);
                defaults = ProbeDefaultContents(
                    type!, planned, out var disallowsMultiple);
                // Engine-native component types carry no managed
                // DisallowMultipleComponent attribute — on Unity 6000 only
                // managed classes emit it — so CheckAddable's attribute
                // check can never fire for a Unity type the target already
                // holds. The detached probe's refused second add reports
                // the engine's native single-instance flag for those
                // types, but a refused add alone is not proof the step
                // must fail: it only closes the §G5 item 5
                // DisallowMultipleComponent precondition when the target
                // already carries a same-type component. The presence
                // check goes through Unity lifetime semantics: the Editor
                // hands GetComponent's miss back as a fake-null wrapper,
                // which a C# "is not null" would read as present.
                if (disallowsMultiple && target.TryGetComponent(type!, out _))
                {
                    invalid = ErrorCodes.Create(
                        ErrorCodes.OW_UNITY_006,
                        $"The component of type '{type!.FullName}' cannot be added: " +
                        "the target already has one and the type disallows multiple.",
                        port: ComponentTypePort);
                }
            }
            if (invalid is not null)
            {
                return new PreflightResult(
                    new[] { invalid }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var snapshot = new JObject
            {
                ["target"] = GameObjectEvidence.RefOf(target),
                ["componentType"] = typeName,
                ["components"] = ComponentEvidence.ComponentsOf(target),
                ["expectedAdded"] = ExpectedAdded(planned!, defaults),
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            return new PreflightResult(
                Array.Empty<Diagnostic>(),
                Array.Empty<PlannedChange>(),
                JsonValue.From(snapshot));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            GameObject target;
            try
            {
                target = ctx.Get<GameObject>(TargetPort);
            }
            catch (StepContextException failure)
            {
                return StepResult.Failed(failure.Diagnostics[0]);
            }
            var typeName = ctx.Get<string>(ComponentTypePort);
            var type = ResolveComponentType(typeName);
            var invalid = CheckAddable(target, type);
            if (invalid is not null)
            {
                return StepResult.Failed(invalid);
            }

            var component = Undo.AddComponent(target, type!);
            if (component == null)
            {
                return StepResult.Failed(CannotAdd());
            }

            ctx.Changes.Created(
                new ChangeTarget(
                    "component",
                    RefHierarchyPath.For(target),
                    null,
                    GlobalObjectId.GetGlobalObjectIdSlow(component).ToString()),
                undoable: true);
            ctx.SetOutput(ComponentPort, component);
            return StepResult.Succeeded();
        }

        /// <inheritdoc/>
        public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            if (snapshot.ToToken() is not JObject obj)
            {
                return ComponentEvidence.Unknown("The Preflight snapshot is not an object.");
            }
            if (obj["target"] is not JObject targetRef ||
                obj["componentType"] is not JValue { Type: JTokenType.String } typeToken ||
                obj["components"] is not JArray components ||
                obj["expectedAdded"] is not JArray expectedAdded ||
                obj["editorSessionId"] is not JValue { Type: JTokenType.String } sessionToken)
            {
                return ComponentEvidence.Unknown(
                    "The Preflight snapshot is missing a required evidence member.");
            }
            // §F5 common rule 3: the session handles a component entry names
            // die with the session, so a different session proves nothing.
            if (!string.Equals(
                (string)sessionToken.Value!, EditorSessionId.GetOrCreate(),
                StringComparison.Ordinal))
            {
                return ComponentEvidence.Unknown(
                    "The snapshot belongs to a different Editor session.");
            }

            var typeName = (string)typeToken.Value!;
            var type = ResolveComponentType(typeName);
            if (type is null || !typeof(Component).IsAssignableFrom(type) ||
                type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            {
                // §F5 rule 6: the expected addition can no longer be
                // identified, so no verdict is provable.
                return ComponentEvidence.Unknown(
                    "The snapshot's component type can no longer be resolved.");
            }
            var resolved = new RefResolver().Resolve<GameObject>(JsonValue.From(targetRef));
            if (!resolved.Ok || resolved.Value == null)
            {
                return ComponentEvidence.Unknown(
                    "The snapshot's target GameObject cannot be resolved.");
            }
            var target = resolved.Value;
            var live = ComponentEvidence.LiveComponents(target);

            // NotApplied: the component list is exactly the snapshot's —
            // the step provably added nothing and re-execution only adds.
            if (ComponentEvidence.SameComponentList(components, live))
            {
                return new ReconcileResult.NotApplied();
            }

            // Completed: the snapshot's components stay in place as a
            // prefix and exactly the recorded additions follow — each
            // carrying the default content the probe captured.
            var expected = new List<(string TypeId, JToken? Content)>(expectedAdded.Count);
            foreach (var entry in expectedAdded)
            {
                var typeId = ComponentEvidence.TypeOf(entry);
                if (typeId is null || entry is not JObject entryObj)
                {
                    return ComponentEvidence.Unknown(
                        "The snapshot's expected additions are malformed.");
                }
                expected.Add((typeId, entryObj["content"]));
            }
            if (!ComponentEvidence.SameComponentPrefix(components, live, components.Count))
            {
                return ComponentEvidence.Unknown(
                    "The target's pre-existing components changed since the snapshot.");
            }
            var tail = new List<Component>(live.Length - components.Count);
            for (var i = components.Count; i < live.Length; i++)
            {
                tail.Add(live[i]);
            }
            if (tail.Count != expected.Count)
            {
                return ComponentEvidence.Unknown(
                    "The target gained something other than the expected additions.");
            }
            // The tail's class-id multiset must equal the expected one.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (typeId, _) in expected)
            {
                counts[typeId] = counts.TryGetValue(typeId, out var n) ? n + 1 : 1;
            }
            var tailByType = new Dictionary<string, Component>(StringComparer.Ordinal);
            foreach (var component in tail)
            {
                var classId = UnityClassRelationProvider.ClassIdOf(component.GetType());
                if (!counts.TryGetValue(classId, out var remaining) || remaining == 0)
                {
                    return ComponentEvidence.Unknown(
                        "The target gained a component the step could not have added.");
                }
                counts[classId] = remaining - 1;
                tailByType[classId] = component;
            }
            // Exactly one of the additions is the requested component type
            // (§F5: "componentType の Component がちょうど 1 つ増え").
            Component? addedMain = null;
            var addedMainCount = 0;
            foreach (var component in tail)
            {
                if (component.GetType() == type)
                {
                    addedMain = component;
                    addedMainCount++;
                }
            }
            if (addedMainCount != 1 || addedMain == null)
            {
                return ComponentEvidence.Unknown(
                    "The target did not gain exactly one component of the step's type.");
            }
            // The recorded default content must match — every expected
            // addition carries the content the probe captured, or the
            // comparison is unprovable and fails closed (§F5 rule 6).
            foreach (var (typeId, content) in expected)
            {
                if (content is null || content.Type == JTokenType.Null)
                {
                    return ComponentEvidence.Unknown(
                        "The snapshot carries no expected content to compare.");
                }
                if (!ComponentEvidence.TryNormalizeComponent(
                        tailByType[typeId], out var actual, out var failure))
                {
                    return ComponentEvidence.Unknown(
                        "An added component's content cannot be normalized: " + failure);
                }
                if (!JToken.DeepEquals(content, actual))
                {
                    return ComponentEvidence.Unknown(
                        "An added component's content differs from its defaults.");
                }
            }
            return new ReconcileResult.Completed(
                new Dictionary<string, JsonValue>
                {
                    [ComponentPort] = new RefResolver().ToRef(
                        addedMain, "unity.ComponentRef<unity.Component>"),
                });
        }

        // The domain type-space resolution of the fully qualified type
        // name — the same answer the UnityTypeProbe gives the type_exists
        // requirement: Type.GetType and assembly-qualified spellings via
        // the class-id resolver, then every loaded assembly by full name.
        private static Type? ResolveComponentType(string? typeName) =>
            UnityClassRelationProvider.ResolveClassId(typeName);

        // The OW-UNITY-006 half of §G5 item 5: the type must resolve to a
        // Component-derived, non-abstract, non-open-generic type, and a
        // DisallowMultipleComponent type must not already be on the target.
        private static Diagnostic? CheckAddable(GameObject target, Type? type)
        {
            if (type is null || !typeof(Component).IsAssignableFrom(type) ||
                type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
            {
                return CannotAdd();
            }
            if (type.IsDefined(typeof(DisallowMultipleComponent), inherit: true) &&
                target.TryGetComponent(type, out _))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_006,
                    $"The component of type '{type.FullName}' cannot be added: " +
                    "the target already has one and the type disallows multiple.",
                    port: ComponentTypePort);
            }
            return null;
        }

        private static Diagnostic CannotAdd() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_006,
                $"The type named by port '{ComponentTypePort}' is not an addable Component type.",
                port: ComponentTypePort,
                hint: "Use the fully qualified name of a non-abstract type that derives from UnityEngine.Component.");

        // The expected additions the Execute of this step produces on the
        // target, each recorded with its normalized default serialized
        // content (the §F5 "追加直後の既定値") where the defaults can be
        // captured without instantiating a managed type. The set itself
        // comes from reflection over the RequireComponent attributes —
        // never from an AddComponent probe: Preflight and Dry Run may not
        // run editor callbacks, and instantiating a component calls Reset
        // and (for ExecuteInEditMode/ExecuteAlways types) Awake/OnEnable —
        // arbitrary user code with project or scene side effects the
        // probe's destruction would not undo (SPEC §C2 item 7).
        private static JArray ExpectedAdded(
            IReadOnlyList<Type> planned, IReadOnlyDictionary<string, JToken>? defaults)
        {
            var expected = new JArray();
            foreach (var type in planned)
            {
                var entry = new JObject
                {
                    ["type"] = UnityClassRelationProvider.ClassIdOf(type),
                };
                if (defaults is not null &&
                    defaults.TryGetValue(
                        UnityClassRelationProvider.ClassIdOf(type), out var content))
                {
                    entry["content"] = content;
                }
                expected.Add(entry);
            }
            return expected;
        }

        // The ordered set of component types Unity adds when the target
        // gains <paramref name="componentType"/>: the type itself plus the
        // transitive closure of its RequireComponent attributes. A
        // requirement is skipped when the target or an earlier addition
        // already satisfies it (a required type R is satisfied by any
        // component assignable to R — the same rule Unity applies), and
        // a required type Unity can never instantiate — abstract, an
        // interface, not Component-derived or an open generic — is a
        // constraint, not an addition.
        private static List<Type> PlanAdditions(Type componentType, GameObject target)
        {
            var planned = new List<Type> { componentType };
            var pending = new Queue<Type>();
            pending.Enqueue(componentType);
            while (pending.Count > 0)
            {
                var attributes = pending.Dequeue().GetCustomAttributes(
                    typeof(RequireComponent), inherit: true);
                foreach (RequireComponent attribute in attributes)
                {
                    foreach (var required in new[]
                        { attribute.m_Type0, attribute.m_Type1, attribute.m_Type2 })
                    {
                        if (required is null ||
                            !typeof(Component).IsAssignableFrom(required) ||
                            required.IsAbstract || required.IsInterface ||
                            required.ContainsGenericParameters)
                        {
                            continue;
                        }
                        if (planned.Contains(required) ||
                            target.TryGetComponent(required, out _) ||
                            SatisfiedByPlanned(required, planned))
                        {
                            continue;
                        }
                        planned.Add(required);
                        pending.Enqueue(required);
                    }
                }
            }
            return planned;
        }

        // A required type R is already satisfied when the planned set
        // holds a type assignable to R — the requested type itself or an
        // earlier auto-addition counts (Unity adds one component of a
        // required hierarchy, never two).
        private static bool SatisfiedByPlanned(Type required, List<Type> planned)
        {
            foreach (var type in planned)
            {
                if (required.IsAssignableFrom(type))
                {
                    return true;
                }
            }
            return false;
        }

        // The normalized default serialized contents of the planned
        // additions, keyed by class-id — or null when the set cannot be
        // instantiated without running managed editor callbacks. Only a
        // MonoBehaviour-derived type can carry Reset/OnEnable/Awake user
        // code, so a set of purely engine-native types is instantiated on
        // a probe GameObject inside a detached preview scene — no user
        // code runs, and nothing enters or dirties a real scene. When
        // any planned type is managed, or the probe itself fails, no
        // defaults are captured: the snapshot records the planned types
        // without content and Reconcile fails closed to Unknown rather
        // than claiming unverifiable defaults (§F5 common rule 6). The same
        // probe also reports <paramref name="disallowsMultiple"/>: engine
        // types expose no managed DisallowMultipleComponent attribute, so
        // Unity's refusal of a second add on the probe is the only
        // managed-visible form of that flag.
        private static Dictionary<string, JToken>? ProbeDefaultContents(
            Type componentType, IReadOnlyList<Type> planned, out bool disallowsMultiple)
        {
            disallowsMultiple = false;
            foreach (var type in planned)
            {
                if (typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    return null;
                }
            }
            var contents = new Dictionary<string, JToken>(StringComparer.Ordinal);
            Scene probeScene = default;
            GameObject? probe = null;
            var probeSceneOpen = false;
            try
            {
                // The probe scene is a detached preview scene — the §G6
                // probe mechanism — because it never enters the scene
                // list: no caller's scene is saved, activated, dirtied or
                // restored, and the creation is never refused the way an
                // additive NewScene is while an unsaved untitled scene is
                // loaded. A GameObject always starts in the active scene,
                // so it is moved across immediately after creation.
                probeScene = EditorSceneManager.NewPreviewScene();
                probeSceneOpen = true;
                probe = new GameObject("OpWeaveComponentProbe");
                SceneManager.MoveGameObjectToScene(probe, probeScene);
                probe.AddComponent(componentType);
                // A second add answers the DisallowMultipleComponent
                // question the managed attribute cannot: Unity refuses the
                // add for a single-instance engine type and honours it
                // otherwise.
                var instances = probe.GetComponents(componentType).Length;
                probe.AddComponent(componentType);
                disallowsMultiple = instances > 0 &&
                    probe.GetComponents(componentType).Length == instances;
                foreach (var component in probe.GetComponents<Component>())
                {
                    if (component == null || component is Transform)
                    {
                        continue;
                    }
                    var classId = UnityClassRelationProvider.ClassIdOf(
                        component.GetType());
                    if (!contents.ContainsKey(classId) &&
                        ComponentEvidence.TryNormalizeComponent(
                            component, out var content, out _))
                    {
                        contents[classId] = content!;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (probe != null)
                {
                    UnityEngine.Object.DestroyImmediate(probe);
                }
                if (probeSceneOpen)
                {
                    EditorSceneManager.ClosePreviewScene(probeScene);
                }
            }
            return contents;
        }

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Adds a component of the named type to a GameObject.")
            .Ja("Component追加",
                "GameObject に指定した型の Component を追加します。componentType が定数なら出力型を絞り込みます。",
                "Component", "追加", "GameObject")
            .Input(TargetPort, "unity.GameObjectRef", required: true)
            .Input(ComponentTypePort, "unity.TypeName", required: true)
            .Output(ComponentPort, "unity.ComponentRef<unity.Component>")
            .Effects(EffectNames.SceneModify)
            .Undo(UndoSupport.Full)
            .Idempotency(Idempotency.Reconcilable)
            // §I.1's named precondition: the named type must exist in the
            // current domain (D6 checks constants statically; G1 re-checks
            // at run time after an OW-VAL-015).
            .Requires("type_exists", ComponentTypePort)
            .Build() with
            {
                DisplayName = "Add Component",
                Description =
                    "Adds a component of the fully qualified type to the target " +
                    "GameObject. A type that is missing, abstract or not " +
                    "Component-derived fails with OW-UNITY-006, as does adding a " +
                    "second component of a DisallowMultipleComponent type. When " +
                    "componentType is a constant, the component output refines to " +
                    "the component's own type (unity.ComponentRef<T>).",
                Category = "Component",
                Tags = new[] { "component", "gameObject", "scene" },
                Keywords = new[] { "component", "add", "attach", "gameobject" },
                OutputRefinement = new TypeRefinement(
                    ComponentTypePort, ComponentPort, "unity.ComponentRef<{0}>"),
                Examples = new[]
                {
                    new OperationExample(
                        "Add an Animator to the created GameObject",
                        ComponentOperationExamples.Add),
                },
            };
    }
}
