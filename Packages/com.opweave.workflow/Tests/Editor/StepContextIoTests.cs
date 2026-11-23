#nullable enable

// T-W2.3-02 [Edit] tests: local:T-W2.3-02:stepcontext-io — a two-step
// workflow of OperationBase test operations exchanges a Vector3, a
// GameObjectRef and a Missing optional input through the Unity StepContext:
// the CLR codec converts the value type, the G2 RefResolver writes and
// resolves the Ref, the absent optional input is reported as not present
// (never null), and an unresolvable Ref input fails its step with
// OW-UNITY-001 (SPEC §C2 item 5, §E5, §0.6 SG-06).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter.Tests
{
    internal sealed class StepContextIoTests
    {
        private const string WorkflowId = "example.stepcontext";
        private const string ProbeFolder = "Assets/OpWeaveStepContextProbe";
        private const string ProbeSinglePath = ProbeFolder + "/single.unity";
        private const string ProbeScenePath = ProbeFolder + "/probe.unity";
        private const string ProbePrefabPath = ProbeFolder + "/probe.prefab";
        private const string ProbeAssetPath = ProbeFolder + "/probe.asset";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<Scene> _scenes = new List<Scene>();
        private SceneSetup[]? _savedSetup;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveStepContextTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
            }
            foreach (var go in _objects)
            {
                if (go != null)
                {
                    UnityEngine.Object.DestroyImmediate(go);
                }
            }
            for (var i = 0; i < _scenes.Count; i++)
            {
                if (_scenes[i].IsValid() && _scenes[i].isLoaded)
                {
                    EditorSceneManager.CloseScene(_scenes[i], true);
                }
            }
            _scenes.Clear();
            if (_savedSetup is not null && _savedSetup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(_savedSetup);
                _savedSetup = null;
            }
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private RunnerHostFixture NewFixture()
        {
            var fixture = new RunnerHostFixture(_root);
            _fixtures.Add(fixture);
            return fixture;
        }

        private GameObject NewObject(string name)
        {
            var go = new GameObject(name);
            _objects.Add(go);
            return go;
        }

        // Opens a saved scene loaded additively — the Get<Scene> read
        // needs a path-addressable loaded scene. An unsaved untitled
        // scene blocks NewScene(Additive), so the current setup is saved,
        // the runner's scene is replaced by a saved single and the probe
        // scene is created and saved — all restored in TearDown (the
        // RefResolverTests pattern).
        private Scene OpenProbeScene()
        {
            _savedSetup = EditorSceneManager.GetSceneManagerSetup();
            var single = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveStepContextProbe");
            }
            Assert.IsTrue(
                EditorSceneManager.SaveScene(single, ProbeSinglePath),
                "the replacement single scene must save");
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            Assert.IsTrue(
                EditorSceneManager.SaveScene(scene, ProbeScenePath),
                "the probe scene must save");
            _scenes.Add(scene);
            return scene;
        }

        private static void EnsureProbeFolder()
        {
            if (!AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveStepContextProbe");
            }
        }

        // A persistent Material asset — the AssetRef fixture of the
        // conversion matrix (the RefResolverTests pattern). The expected
        // identity is the persisted object LoadAssetAtPath returns: an
        // asset-kind Ref names guid + localFileId, so Resolve answers the
        // imported asset, not the pre-import instance CreateAsset was
        // handed.
        private Material NewMaterialAsset()
        {
            EnsureProbeFolder();
            AssetDatabase.CreateAsset(
                new Material(Shader.Find("Hidden/InternalErrorShader")), ProbeAssetPath);
            var asset = AssetDatabase.LoadAssetAtPath<Material>(ProbeAssetPath);
            Assert.IsNotNull(asset, "the saved probe material must load");
            return asset!;
        }

        // A persistent prefab asset — the same persisted-identity rule as
        // NewMaterialAsset: the fixture's object is the one
        // LoadAssetAtPath yields, never the in-memory save input.
        private GameObject NewPrefabAsset()
        {
            EnsureProbeFolder();
            Assert.IsNotNull(
                PrefabUtility.SaveAsPrefabAsset(
                    NewObject("MatrixPrefab"), ProbePrefabPath),
                "the fixture prefab must save");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ProbePrefabPath);
            Assert.IsNotNull(prefab, "the saved prefab asset must load");
            return prefab!;
        }

        private GameObject NewPrefabInstance()
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(NewPrefabAsset())!;
            _objects.Add(instance);
            return instance;
        }

        // A persistent SceneAsset — the §G2 object form of a scene output.
        private SceneAsset NewSceneAsset()
        {
            var scene = OpenProbeScene();
            var asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(scene.path);
            Assert.IsNotNull(asset, "the saved probe scene must load as a SceneAsset");
            return asset!;
        }

        private static void Drive(OpWeaveRunnerHost host, string runId, int maxTicks = 50)
        {
            for (var i = 0; i < maxTicks; i++)
            {
                var state = host.Coordinator.StateOf(runId);
                if (state != RunState.Queued && state != RunState.Running)
                {
                    return;
                }
                var tick = host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed: " + Describe(tick.Diagnostics));
            }
            Assert.Fail("The Run did not reach a terminal state within " + maxTicks + " ticks.");
        }

        private static string Describe(IReadOnlyList<Diagnostic> diagnostics)
        {
            var parts = new List<string>();
            foreach (var diagnostic in diagnostics)
            {
                parts.Add(diagnostic.Code + " " + diagnostic.Message);
            }
            return string.Join("; ", parts);
        }

        private static JsonValue Json(string text)
        {
            var parsed = JsonValue.Parse(text);
            Assert.IsTrue(parsed.Ok, $"fixture JSON must parse: {text}");
            return parsed.Value!;
        }

        // T-W2.3-06: the identity of a live object as a stable string. On
        // Unity 6000.5+ the whole InstanceID family is [Obsolete(error=true)]
        // — a direct call is CS0619 — so the EntityId supplies the identity
        // there (SPEC §T.4 L-04); below 6000.5 the EntityId API does not exist
        // and the InstanceID does, exactly as SessionHandle treats the §B5
        // handle. The boundary is UNITY_6000_5_OR_NEWER rather than a lower
        // define because that is where the switch is forced and where it was
        // measured; 6000.0 keeps its InstanceID text unchanged.
        //
        // The recorded observation and the expected value both call this one
        // helper, so the round-trip assertion still compares the identity of
        // the produced instance with the identity of the resolved Ref target
        // on either Editor generation — a same-named different object cannot
        // satisfy it.
        private static string IdentityOf(UnityEngine.Object obj)
        {
#if UNITY_6000_5_OR_NEWER
            return EntityId.ToULong(obj.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
#pragma warning disable CS0618 // InstanceID is the identity below 6000.5; obsolete-warning on 6000.3-6000.4, error only from 6000.5 (L-04).
            return obj.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#pragma warning restore CS0618
#endif
        }

        // ── Test operations ─────────────────────────────────────────────

        /// <summary>Produces a Vector3 and a fully keyed GameObjectRef.</summary>
        private sealed class ProducerOperation : OperationBase
        {
            private readonly GameObject _target;

            internal ProducerOperation(GameObject target)
            {
                _target = target;
                Descriptor = Op.Define("test.stepcontext.produce", 1)
                    .Summary("Produces a Vector3 and a GameObjectRef.")
                    .Output("position", "unity.Vector3")
                    .Output("target", "unity.GameObjectRef")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("position", new Vector3(1f, 2f, 3f));
                ctx.SetOutput("target", _target);
                return StepResult.Succeeded();
            }
        }

        /// <summary>
        /// Reads the produced Vector3/Ref/Missing inputs and records what it
        /// saw — then echoes the resolved object back out as a Ref so the
        /// round-trip also covers SetOutput of a UnityEngine.Object.
        /// </summary>
        private sealed class ConsumerOperation : OperationBase
        {
            private readonly List<string> _observed;

            internal ConsumerOperation(List<string> observed)
            {
                _observed = observed;
                Descriptor = Op.Define("test.stepcontext.consume", 1)
                    .Summary("Consumes the produced values.")
                    .Input("position", "unity.Vector3", required: true)
                    .Input("target", "unity.GameObjectRef", required: true)
                    .Input("opt", "core.String", required: false)
                    .Output("seen", "core.Json")
                    .Output("echo", "unity.GameObjectRef")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                var position = ctx.Get<Vector3>("position");
                var target = ctx.Get<GameObject>("target");
                _observed.Add($"position:{position.x},{position.y},{position.z}");
                _observed.Add($"target:{target.name}");
                // T-W2.3-05: the name alone would also match a same-named
                // object, so the resolved entity's identity travels too —
                // the Ref round-trip must return the produced instance.
                // T-W2.3-06: the identity text comes from the one
                // version-conditional helper.
                _observed.Add($"target-id:{IdentityOf(target)}");
                _observed.Add($"opt-present:{ctx.IsPresent("opt")}");
                _observed.Add($"opt-pending:{ctx.IsPending("opt")}");
                var missingThrew = false;
                try
                {
                    ctx.Get<JsonValue>("opt");
                }
                catch (InvalidOperationException)
                {
                    missingThrew = true;
                }
                _observed.Add($"opt-get-throws:{missingThrew}");
                ctx.SetOutput("seen", JsonValue.From(new JObject
                {
                    ["x"] = position.x,
                    ["y"] = position.y,
                    ["z"] = position.z,
                    ["name"] = target.name,
                }));
                ctx.SetOutput("echo", target);
                return StepResult.Succeeded();
            }
        }

        /// <summary>Reads one Ref-typed input — the resolution-failure path.</summary>
        private sealed class RefReaderOperation : OperationBase
        {
            private readonly List<string> _observed;

            internal RefReaderOperation(List<string> observed)
            {
                _observed = observed;
                Descriptor = Op.Define("test.stepcontext.refreader", 1)
                    .Summary("Reads one Ref input.")
                    .Input("target", "unity.GameObjectRef", required: true)
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                _observed.Add($"target:{ctx.Get<GameObject>("target").name}");
                return StepResult.Succeeded();
            }
        }

        /// <summary>Produces a Vector3 — the SG-06 edge-typing producer.</summary>
        private sealed class Vector3ProducerOperation : OperationBase
        {
            internal Vector3ProducerOperation()
            {
                Descriptor = Op.Define("test.stepcontext.produce_vec", 1)
                    .Summary("Produces a Vector3.")
                    .Output("position", "unity.Vector3")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("position", new Vector3(1f, 2f, 3f));
                return StepResult.Succeeded();
            }
        }

        /// <summary>
        /// Declares the broader <c>core.Json</c> input and reads it both
        /// ways — the SG-06 static type of an edge-fed port is the source
        /// output's resolved type (unity.Vector3 here), not the port's own
        /// declared type, so the Vector3 read converts through the Vector3
        /// codec (SPEC §0.6 SG-06).
        /// </summary>
        private sealed class BroaderInputConsumerOperation : OperationBase
        {
            private readonly List<string> _observed;

            internal BroaderInputConsumerOperation(List<string> observed)
            {
                _observed = observed;
                Descriptor = Op.Define("test.stepcontext.consume_broader", 1)
                    .Summary("Reads a broader-typed input both ways.")
                    .Input("position", "core.Json", required: true)
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                var position = ctx.Get<Vector3>("position");
                _observed.Add($"position:{position.x},{position.y},{position.z}");
                _observed.Add($"raw:{ctx.Get<JsonValue>("position").CanonicalText}");
                return StepResult.Succeeded();
            }
        }

        // ── Two-step workflow (local:T-W2.3-02:stepcontext-io) ──────────

        [Test]
        public void TwoStepWorkflowRoundTripsVector3RefAndMissingValues()
        {
            var fixture = NewFixture();
            var target = NewObject("StepContextTarget");
            var observed = new List<string>();
            var producer = new ProducerOperation(target);
            var consumer = new ConsumerOperation(observed);
            fixture.Register(producer, 'a');
            fixture.Register(consumer, 'c');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "produce", "test.stepcontext.produce", 'a',
                    outputTypes: new Dictionary<string, string>
                    {
                        ["position"] = "unity.Vector3",
                        ["target"] = "unity.GameObjectRef",
                    },
                    next: 1,
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(1, "consume", "test.stepcontext.consume", 'c',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["position"] = new FromStep(0, "position"),
                        ["target"] = new FromStep(0, "target"),
                        ["opt"] = new Missing(),
                    },
                    outputTypes: new Dictionary<string, string>
                    {
                        ["seen"] = "core.Json",
                        ["echo"] = "unity.GameObjectRef",
                    },
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "produce", Operation = "test.stepcontext.produce", Version = 1 },
                    new NodeData { Id = "consume", Operation = "test.stepcontext.consume", Version = 1 },
                },
                new[] { new ControlEdge("produce", "consume") });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture.Host, runId);

            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId),
                "the two-step run must succeed");
            Assert.That(observed, Is.EqualTo(new[]
            {
                "position:1,2,3",
                "target:StepContextTarget",
                "target-id:" + IdentityOf(target),
                "opt-present:False",
                "opt-pending:False",
                "opt-get-throws:True",
            }), "Vector3, Ref and Missing did not round-trip as expected");
            // The produced and echoed Refs reach the journal fully keyed.
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_succeeded\"", "\"kind\":\"gameObject\""),
                Is.True, "the journaled outputs must carry the Ref shape");
            // T-W2.3-05: the same evidence pinned per step — step 0 journals
            // the codec-written Vector3 beside the produced Ref, step 1
            // journals the Ref it wrote from the resolved object, so neither
            // record can be satisfied by the other step's outputs.
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_succeeded\"", "\"step\":0",
                    "\"kind\":\"gameObject\"", "\"x\":1e0"),
                Is.True, "step 0 must journal the Vector3 and the produced Ref");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_succeeded\"", "\"step\":1",
                    "\"kind\":\"gameObject\""),
                Is.True, "step 1 must journal the echoed Ref");
        }

        // ── SG-06 edge-fed static typing (PR #182 F1) ──────────────────

        [Test]
        public void EdgeFedBroaderInputReadsThroughTheSourceOutputType()
        {
            var fixture = NewFixture();
            var observed = new List<string>();
            var producer = new Vector3ProducerOperation();
            var consumer = new BroaderInputConsumerOperation(observed);
            fixture.Register(producer, 'a');
            fixture.Register(consumer, 'c');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "produce", "test.stepcontext.produce_vec", 'a',
                    outputTypes: new Dictionary<string, string>
                    {
                        ["position"] = "unity.Vector3",
                    },
                    next: 1,
                    idempotency: Idempotency.Idempotent),
                RunnerHostFixture.Step(1, "consume", "test.stepcontext.consume_broader", 'c',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["position"] = new FromStep(0, "position"),
                    },
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "produce", Operation = "test.stepcontext.produce_vec", Version = 1 },
                    new NodeData { Id = "consume", Operation = "test.stepcontext.consume_broader", Version = 1 },
                },
                new[] { new ControlEdge("produce", "consume") });

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture.Host, runId);

            // SPEC §0.6 SG-06: the edge-fed port's static type is the
            // source output's resolved unity.Vector3 — Get<Vector3>
            // converts through the Vector3 codec although the port declares
            // the broader core.Json, and the JsonValue read still returns
            // the stored value verbatim.
            Assert.AreEqual(
                RunState.Succeeded, fixture.Host.Coordinator.StateOf(runId),
                "the edge-typed run must succeed");
            Assert.That(observed, Is.EqualTo(new[]
            {
                "position:1,2,3",
                "raw:" + Json("{\"x\":1e0,\"y\":2e0,\"z\":3e0}").CanonicalText,
            }), "the edge-fed input must convert through the source output's type");
        }

        // ── Unresolvable Ref input → OW-UNITY-001 (SPEC §E5) ────────────

        [Test]
        public void UnresolvableRefInputFailsStepWithOwUnity001()
        {
            var fixture = NewFixture();
            var observed = new List<string>();
            var reader = new RefReaderOperation(observed);
            fixture.Register(reader, 'a');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "read", "test.stepcontext.refreader", 'a',
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["target"] = new Constant(
                            Json("{\"kind\":\"gameObject\",\"path\":\"/NoSuchStepContextObject\"}")),
                    },
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "read", Operation = "test.stepcontext.refreader", Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture.Host, runId);

            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "an unresolvable Ref input fails the step's Run");
            Assert.AreEqual(0, observed.Count, "Execute must not observe a value");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_failed\"", "OW-UNITY-001"),
                Is.True, "the step must fail with OW-UNITY-001 (SPEC §E5)");
            // T-W2.3-05: the same record pinned to the step that failed and
            // to the code the §F2 fold reads as its error member.
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_failed\"", "\"step\":0",
                    "\"error\":\"OW-UNITY-001\""),
                Is.True, "step 0's own record must carry the OW-UNITY-001 error");
            // SPEC §J: OW-UNITY-001 is not retryable, so the step fails once
            // with its own diagnostic — never a retry chain ending in the
            // OW-RUN-008 retry-limit failure (SPEC §E7).
            Assert.AreEqual(
                1, RunnerHostFixture.JournalCount(_root, runId, "\"type\":\"step_failed\""),
                "a non-retryable OW-UNITY-001 fails the step in one attempt");
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "OW-RUN-008"),
                Is.False, "the failure must not be the retry-limit OW-RUN-008");
        }

        // ── Direct context conversion coverage ───────────────────────────

        private static UnityStepContext Context(
            IReadOnlyDictionary<string, InputValue>? inputs = null,
            IReadOnlyDictionary<string, string>? inputTypes = null,
            IReadOnlyDictionary<string, string>? outputTypes = null,
            IReadOnlyDictionary<string, string>? pendingProducers = null,
            JsonValue? resumeToken = null) =>
            new UnityStepContext(
                inputs ?? new Dictionary<string, InputValue>(),
                inputTypes,
                outputTypes,
                RunnerHostFixture.Types,
                new RefResolver(),
                new StepChangeRecorder(0, "node"),
                new StepLogSink(
                    "run", 0, (_, _, _, _) => Result<Unit>.Success(Unit.Value)),
                resumeToken,
                pendingProducers);

        private static readonly IReadOnlyDictionary<string, string> s_ioTypes =
            new Dictionary<string, string>
            {
                ["position"] = "unity.Vector3",
                ["target"] = "unity.GameObjectRef",
                ["opt"] = "core.String",
            };

        [Test]
        public void GetConvertsClrCodecAndTokenAndDistinguishesMissing()
        {
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["position"] = InputValue.Present(Json("{\"x\":1,\"y\":2,\"z\":3}")),
                    ["opt"] = InputValue.Missing,
                    ["nothing"] = InputValue.Present(Json("null")),
                    ["name"] = InputValue.Present(Json("\"player\"")),
                },
                inputTypes: new Dictionary<string, string>
                {
                    ["position"] = "unity.Vector3",
                    ["name"] = "core.String",
                });

            Assert.AreEqual(new Vector3(1f, 2f, 3f), context.Get<Vector3>("position"));
            Assert.AreEqual("player", context.Get<string>("name"));
            Assert.AreEqual("{\"x\":1,\"y\":2,\"z\":3}", context.Get<JsonValue>("position").CanonicalText);
            Assert.IsTrue(context.IsPresent("nothing"), "explicit null is present");
            Assert.AreEqual("null", context.Get<JsonValue>("nothing").CanonicalText);
            Assert.IsFalse(context.IsPresent("opt"), "missing stays absent, never null");
            Assert.Throws<InvalidOperationException>(() => context.Get<JsonValue>("opt"));
            Assert.Throws<ArgumentNullException>(() => context.Get<JsonValue>(null!));
        }

        // T-W2.3-05: the "never null" half of the absent-optional condition on
        // the branches a codec-less string port cannot reach — a Missing
        // Ref-typed port is refused as missing before the resolver or any
        // codec sees a value, so no read can answer null (SPEC §E5, §0.6 SG-06).
        [Test]
        public void AbsentOptionalRefInputIsNotPresentAndNeverYieldsNull()
        {
            var context = Context(
                new Dictionary<string, InputValue> { ["opt"] = InputValue.Missing },
                inputTypes: new Dictionary<string, string>
                {
                    ["opt"] = "unity.GameObjectRef",
                });

            Assert.IsFalse(context.IsPresent("opt"), "a Missing input is absent");
            Assert.IsFalse(context.IsPending("opt"), "a Missing input is not pending");
            Assert.Throws<InvalidOperationException>(() => context.Get<GameObject>("opt"));
            Assert.Throws<InvalidOperationException>(() => context.Get<Scene>("opt"));
            Assert.Throws<InvalidOperationException>(() => context.Get<JsonValue>("opt"));
        }

        [Test]
        public void PendingInputIsPresentPendingAndCarriesProducerNode()
        {
            var context = Context(
                pendingProducers: new Dictionary<string, string> { ["later"] = "producer_node" });

            Assert.IsTrue(context.IsPending("later"));
            Assert.IsTrue(context.IsPresent("later"), "a pending value is a placeholder");
            var thrown = Assert.Throws<InvalidOperationException>(
                () => context.Get<JsonValue>("later"));
            StringAssert.Contains("producer_node", thrown!.Message);
            Assert.IsFalse(context.IsPending("other"));
            Assert.IsFalse(context.IsPresent("other"));
        }

        [Test]
        public void SetOutputConvertsRefCodecAndNullValues()
        {
            var target = NewObject("RefRoundTrip");
            var context = Context(outputTypes: s_ioTypes);
            context.SetOutput("position", new Vector3(4f, 5f, 6f));
            context.SetOutput("target", target);
            context.SetOutput("nothing", (object?)null);

            Assert.AreEqual(
                Json("{\"x\":4e0,\"y\":5e0,\"z\":6e0}").CanonicalText,
                context.Outputs["position"].CanonicalText);
            var refJson = context.Outputs["target"].ToToken() as JObject;
            Assert.IsNotNull(refJson, "the Ref output must be a JSON object");
            Assert.AreEqual("gameObject", (string?)refJson!["kind"]);
            Assert.IsNotNull(refJson!["handle"], "ToRef fills the session handle key");
            Assert.AreEqual("null", context.Outputs["nothing"].CanonicalText);

            // And the written Ref resolves straight back to the same object.
            var resolved = new RefResolver().Resolve<GameObject>(context.Outputs["target"]);
            Assert.IsTrue(resolved.Ok, "the written Ref must resolve");
            Assert.AreEqual(target, resolved.Value);
        }

        // ── Get<T> strict declared-type reads (SPEC §0.6 SG-06, §J) ────────

        [Test]
        public void GetRejectsMismatchedClrReadsWithOwRun007()
        {
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["count"] = InputValue.Present(Json("7")),
                    ["big"] = InputValue.Present(Json("3000000000")),
                    ["title"] = InputValue.Present(Json("\"hello\"")),
                    ["path"] = InputValue.Present(Json("\"Assets/x.asset\"")),
                    ["ratio"] = InputValue.Present(Json("2.5")),
                    ["flag"] = InputValue.Present(Json("true")),
                    ["blob"] = InputValue.Present(Json("{\"a\":1}")),
                    ["loose"] = InputValue.Present(Json("\"undeclared\"")),
                },
                inputTypes: new Dictionary<string, string>
                {
                    ["count"] = "core.Int",
                    ["big"] = "core.Int",
                    ["title"] = "core.String",
                    ["path"] = "unity.AssetPath",
                    ["ratio"] = "core.Float",
                    ["flag"] = "core.Bool",
                    ["blob"] = "core.Json",
                });

            // The sanctioned reads: each §B3 primitive Type ID to its CLR
            // value, the core.String-derived unity.AssetPath included, and
            // JsonValue on any port.
            Assert.AreEqual(7L, context.Get<long>("count"));
            Assert.AreEqual(7, context.Get<int>("count"));
            Assert.AreEqual("hello", context.Get<string>("title"));
            Assert.AreEqual("Assets/x.asset", context.Get<string>("path"));
            Assert.AreEqual(2.5, context.Get<double>("ratio"));
            Assert.AreEqual(2.5f, context.Get<float>("ratio"));
            Assert.IsTrue(context.Get<bool>("flag"));
            Assert.AreEqual(3000000000L, context.Get<long>("big"));
            Assert.AreEqual("7", context.Get<JsonValue>("count").CanonicalText);
            Assert.AreEqual("{\"a\":1}", context.Get<JsonValue>("blob").CanonicalText);

            // Everything else is OW-RUN-007, never a silent coercion: a wrong
            // T, a mismatched token kind, an int overflow, a codec-less type
            // or a port with no declared type.
            AssertOwRun007(() => context.Get<string>("count"), "core.Int is not a string");
            AssertOwRun007(() => context.Get<double>("count"), "core.Int reads only as long/int");
            AssertOwRun007(() => context.Get<int>("big"), "an out-of-range int is OW-RUN-007");
            AssertOwRun007(() => context.Get<long>("title"), "a string token reads only as string");
            AssertOwRun007(() => context.Get<long>("ratio"), "core.Float reads only as double/float");
            AssertOwRun007(() => context.Get<long>("flag"), "core.Bool reads only as bool");
            AssertOwRun007(() => context.Get<int>("blob"), "core.Json reads only as JsonValue");
            AssertOwRun007(() => context.Get<string>("loose"), "an undeclared port reads only as JsonValue");
        }

        private static void AssertOwRun007(TestDelegate read, string because)
        {
            var thrown = Assert.Throws<StepContextException>(read);
            Assert.AreEqual(ErrorCodes.OW_RUN_007, thrown!.Diagnostics[0].Code, because);
        }

        // ── SG-06 Ref/Scene branch static typing (PR #182 F1) ─────────

        [Test]
        public void RefShapedCoreJsonInputCannotBeReadAsARef()
        {
            var target = NewObject("RefShapedJson");
            var refJson = new RefResolver().ToRef(target, "unity.GameObjectRef");
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(refJson),
                },
                inputTypes: new Dictionary<string, string>
                {
                    ["target"] = "core.Json",
                });

            // The stored value carries a valid Ref shape, but the port's
            // static type is core.Json — the Ref and Scene branches refuse
            // the read before the value's 'kind' is ever consulted
            // (SPEC §0.6 SG-06, §J).
            AssertOwRun007(() => context.Get<GameObject>("target"),
                "a Ref-shaped core.Json value is not a GameObjectRef input");
            AssertOwRun007(() => context.Get<Scene>("target"),
                "the Scene read is gated by the static type as well");
            Assert.AreEqual(
                refJson.CanonicalText,
                context.Get<JsonValue>("target").CanonicalText,
                "the same port still reads as JsonValue");
        }

        [Test]
        public void TypedRefInputsStillResolve()
        {
            var scene = OpenProbeScene();
            var target = NewObject("TypedRefTarget");
            var animator = target.AddComponent<Animator>();
            var resolver = new RefResolver();
            var context = Context(
                new Dictionary<string, InputValue>
                {
                    ["target"] = InputValue.Present(
                        resolver.ToRef(target, "unity.GameObjectRef")),
                    ["motor"] = InputValue.Present(
                        resolver.ToRef(animator, "unity.ComponentRef<unity.Animator>")),
                    ["level"] = InputValue.Present(
                        Json("{\"kind\":\"scene\",\"path\":\"" + ProbeScenePath + "\"}")),
                },
                inputTypes: new Dictionary<string, string>
                {
                    ["target"] = "unity.GameObjectRef",
                    ["motor"] = "unity.ComponentRef<unity.Animator>",
                    ["level"] = "unity.SceneRef",
                });

            Assert.AreEqual(target, context.Get<GameObject>("target"),
                "a typed GameObjectRef still resolves");
            Assert.AreEqual(animator, context.Get<Animator>("motor"),
                "a typed ComponentRef still resolves");
            Assert.AreEqual(scene.path, context.Get<Scene>("level").path,
                "a typed SceneRef still resolves");

            // The same gate rejects a static mismatch before resolution:
            // an Animator component ref is never a GameObject or a
            // Transform, and a scene ref is never a UnityEngine.Object.
            AssertOwRun007(() => context.Get<GameObject>("motor"),
                "a component ref cannot be read as GameObject");
            AssertOwRun007(() => context.Get<Transform>("motor"),
                "an Animator ref cannot be read as Transform");
            AssertOwRun007(() => context.Get<GameObject>("level"),
                "a scene ref cannot be read as a UnityEngine.Object");
        }

        // ── SG-06 conversion matrix (PR #182 F1, round 4) ──────────────
        // One table over the whole (CLR form, static Type ID) space of
        // Get<T> and SetOutput<T>: a valid pair converts, and every
        // invalid pair raises StepContextException carrying OW-RUN-007 —
        // never a raw ArgumentException, InvalidCastException,
        // JsonException or NullReferenceException. The
        // Func<StepContextIoTests, …> factories keep every live-object
        // fixture inside the test's own SetUp/TearDown window; the source
        // only names the cases.

        internal sealed class MatrixRow
        {
            internal MatrixRow(
                string name,
                string? typeId,
                bool isSet,
                bool valid,
                Func<StepContextIoTests, JsonValue>? stored,
                Func<StepContextIoTests, Func<UnityStepContext, object?>> act,
                Func<StepContextIoTests, object?>? expected)
            {
                Name = name;
                TypeId = typeId;
                IsSet = isSet;
                Valid = valid;
                Stored = stored;
                Act = act;
                Expected = expected;
            }

            internal string Name { get; }
            internal string? TypeId { get; }
            internal bool IsSet { get; }
            internal bool Valid { get; }
            internal Func<StepContextIoTests, JsonValue>? Stored { get; }
            internal Func<StepContextIoTests, Func<UnityStepContext, object?>> Act { get; }
            internal Func<StepContextIoTests, object?>? Expected { get; }
        }

        private static MatrixRow GetCase(
            string name, string? typeId, string json, bool valid,
            object? expected, Func<UnityStepContext, object?> read) =>
            new MatrixRow(name, typeId, false, valid, _ => Json(json), _ => read,
                valid ? ExpectedOf(expected) : null);

        // A live fixture row: `make` runs once per test invocation, so the
        // stored Ref and the expected identity share the same object.
        private static MatrixRow GetLiveCase(
            string name, string? typeId,
            Func<StepContextIoTests, object> make,
            Func<object, JsonValue> stored, bool valid,
            Func<UnityStepContext, object?> read)
        {
            object? entity = null;
            Func<StepContextIoTests, object> once = f => entity ??= make(f);
            return new MatrixRow(name, typeId, false, valid,
                f => stored(once(f)),
                _ => read,
                valid ? (Func<StepContextIoTests, object?>)once : null);
        }

        private static MatrixRow SetCase(
            string name, string? typeId, bool valid,
            Func<StepContextIoTests, Action<UnityStepContext>> write,
            Func<StepContextIoTests, object?>? expected = null) =>
            new MatrixRow(name, typeId, true, valid, null,
                fixture => context =>
                {
                    write(fixture)(context);
                    return context.Outputs.TryGetValue("p", out var written)
                        ? written
                        : null;
                },
                expected);

        // The live-object write half of the matrix: `make` runs once, the
        // port writes the object, and the expected stored value is the Ref
        // the resolver's own ToRef produces for the port's Ref Type ID.
        private static MatrixRow SetLiveCase(
            string name, string typeId,
            Func<StepContextIoTests, object> make,
            string refTypeId)
        {
            object? entity = null;
            Func<StepContextIoTests, object> once = f => entity ??= make(f);
            return new MatrixRow(name, typeId, true, true, null,
                fixture => context =>
                {
                    context.SetOutput("p", (UnityEngine.Object)once(fixture));
                    return context.Outputs.TryGetValue("p", out var written)
                        ? written
                        : null;
                },
                f => new RefResolver().ToRef((UnityEngine.Object)once(f), refTypeId));
        }

        private static Func<StepContextIoTests, object?> ExpectedOf(object? value) =>
            _ => value;

        private static IEnumerable<TestCaseData> ConversionMatrixCases()
        {
            var rows = new[]
            {
                // ── Get: JsonValue and the codec-less core primitives ────
                GetCase("get JsonValue on a Ref port", "unity.GameObjectRef",
                    "{\"kind\":\"gameObject\",\"path\":\"/X\"}", true,
                    Json("{\"kind\":\"gameObject\",\"path\":\"/X\"}"), c => c.Get<JsonValue>("p")),
                GetCase("get JsonValue on an undeclared port", null,
                    "{\"a\":1}", true, Json("{\"a\":1}"), c => c.Get<JsonValue>("p")),
                GetCase("get string on core.String", "core.String",
                    "\"hello\"", true, "hello", c => c.Get<string>("p")),
                GetCase("get string on unity.AssetPath", "unity.AssetPath",
                    "\"Assets/x.asset\"", true, "Assets/x.asset", c => c.Get<string>("p")),
                GetCase("get string on unity.TypeName", "unity.TypeName",
                    "\"UnityEngine.Animator\"", true, "UnityEngine.Animator", c => c.Get<string>("p")),
                GetCase("get string on unity.HierarchyPath", "unity.HierarchyPath",
                    "\"/Root/Child\"", true, "/Root/Child", c => c.Get<string>("p")),
                GetCase("get long on core.Int", "core.Int",
                    "7", true, 7L, c => c.Get<long>("p")),
                GetCase("get int on core.Int", "core.Int",
                    "7", true, 7, c => c.Get<int>("p")),
                GetCase("get double on core.Float", "core.Float",
                    "2.5", true, 2.5, c => c.Get<double>("p")),
                GetCase("get float on core.Float", "core.Float",
                    "2.5", true, 2.5f, c => c.Get<float>("p")),
                GetCase("get double on core.Float from an integer token", "core.Float",
                    "5", true, 5.0, c => c.Get<double>("p")),
                GetCase("get bool on core.Bool", "core.Bool",
                    "true", true, true, c => c.Get<bool>("p")),

                // ── Get: the six CLR-codec value types ───────────────────
                GetCase("get Vector2 on unity.Vector2", "unity.Vector2",
                    "{\"x\":1,\"y\":2}", true, new Vector2(1f, 2f), c => c.Get<Vector2>("p")),
                GetCase("get Vector3 on unity.Vector3", "unity.Vector3",
                    "{\"x\":1,\"y\":2,\"z\":3}", true, new Vector3(1f, 2f, 3f), c => c.Get<Vector3>("p")),
                GetCase("get Vector4 on unity.Vector4", "unity.Vector4",
                    "{\"x\":1,\"y\":2,\"z\":3,\"w\":4}", true, new Vector4(1f, 2f, 3f, 4f), c => c.Get<Vector4>("p")),
                GetCase("get Quaternion on unity.Quaternion", "unity.Quaternion",
                    "{\"x\":0,\"y\":0,\"z\":0,\"w\":1}", true, Quaternion.identity, c => c.Get<Quaternion>("p")),
                GetCase("get Vector3 on unity.Euler", "unity.Euler",
                    "{\"x\":0,\"y\":90,\"z\":0}", true, new Vector3(0f, 90f, 0f), c => c.Get<Vector3>("p")),
                GetCase("get Color on unity.Color", "unity.Color",
                    "{\"r\":1,\"g\":0,\"b\":0,\"a\":1}", true, new Color(1f, 0f, 0f, 1f), c => c.Get<Color>("p")),

                // ── Get: Ref reads of every kind ─────────────────────────
                GetLiveCase("get GameObject on unity.GameObjectRef", "unity.GameObjectRef",
                    f => f.NewObject("M"),
                    e => new RefResolver().ToRef((GameObject)e, "unity.GameObjectRef"),
                    true, c => c.Get<GameObject>("p")),
                GetLiveCase("get GameObject on unity.ObjectRef", "unity.ObjectRef",
                    f => f.NewObject("M"),
                    e => new RefResolver().ToRef((GameObject)e, "unity.GameObjectRef"),
                    true, c => c.Get<GameObject>("p")),
                GetLiveCase("get Animator on unity.ComponentRef<unity.Animator>",
                    "unity.ComponentRef<unity.Animator>",
                    f => f.NewObject("M").AddComponent<Animator>(),
                    e => new RefResolver().ToRef(
                        (Component)e, "unity.ComponentRef<unity.Animator>"),
                    true, c => c.Get<Animator>("p")),
                GetLiveCase("get Component on unity.ComponentRef<unity.Animator>",
                    "unity.ComponentRef<unity.Animator>",
                    f => f.NewObject("M").AddComponent<Animator>(),
                    e => new RefResolver().ToRef(
                        (Component)e, "unity.ComponentRef<unity.Animator>"),
                    true, c => c.Get<Component>("p")),
                GetLiveCase("get Scene on unity.SceneRef", "unity.SceneRef",
                    f => f.OpenProbeScene(),
                    e => Json("{\"kind\":\"scene\",\"path\":\"" + ((Scene)e).path + "\"}"),
                    true, c => c.Get<Scene>("p")),
                GetLiveCase("get Scene on unity.ObjectRef", "unity.ObjectRef",
                    f => f.OpenProbeScene(),
                    e => Json("{\"kind\":\"scene\",\"path\":\"" + ((Scene)e).path + "\"}"),
                    true, c => c.Get<Scene>("p")),
                GetLiveCase("get Material on unity.AssetRef<unity.Material>",
                    "unity.AssetRef<unity.Material>",
                    f => f.NewMaterialAsset(),
                    e => new RefResolver().ToRef(
                        (Material)e, "unity.AssetRef<unity.Material>"),
                    true, c => c.Get<Material>("p")),
                GetLiveCase("get SceneAsset on unity.AssetRef<unity.Object>",
                    "unity.AssetRef<unity.Object>",
                    f => f.NewSceneAsset(),
                    e => new RefResolver().ToRef(
                        (SceneAsset)e, "unity.AssetRef<unity.Object>"),
                    true, c => c.Get<SceneAsset>("p")),

                // ── Get: primitive/type/token mismatches → OW-RUN-007 ────
                GetCase("get string on core.Int", "core.Int",
                    "7", false, null, c => c.Get<string>("p")),
                GetCase("get long on a string token", "core.String",
                    "\"7\"", false, null, c => c.Get<long>("p")),
                GetCase("get int out of range on core.Int", "core.Int",
                    "3000000000", false, null, c => c.Get<int>("p")),
                GetCase("get double on core.Int", "core.Int",
                    "7", false, null, c => c.Get<double>("p")),
                GetCase("get long on core.Float", "core.Float",
                    "2.5", false, null, c => c.Get<long>("p")),
                GetCase("get bool on a string token", "core.Bool",
                    "\"true\"", false, null, c => c.Get<bool>("p")),
                GetCase("get string on a bool token", "core.String",
                    "true", false, null, c => c.Get<string>("p")),
                GetCase("get string on core.Json", "core.Json",
                    "\"x\"", false, null, c => c.Get<string>("p")),
                GetCase("get int on core.Json", "core.Json",
                    "5", false, null, c => c.Get<int>("p")),
                GetCase("get object on core.String", "core.String",
                    "\"x\"", false, null, c => c.Get<object>("p")),
                GetCase("get JObject on core.Json", "core.Json",
                    "{\"a\":1}", false, null, c => c.Get<JObject>("p")),
                GetCase("get int[] on core.List<core.Int>", "core.List<core.Int>",
                    "[1,2]", false, null, c => c.Get<int[]>("p")),
                GetCase("get DateTime on core.String", "core.String",
                    "\"2026-01-01\"", false, null, c => c.Get<DateTime>("p")),
                GetCase("get GameObject on core.Json", "core.Json",
                    "{\"kind\":\"gameObject\",\"path\":\"/X\"}", false, null, c => c.Get<GameObject>("p")),
                GetCase("get GameObject on core.String", "core.String",
                    "{\"kind\":\"gameObject\",\"path\":\"/X\"}", false, null, c => c.Get<GameObject>("p")),
                GetCase("get GameObject on unity.SceneRef", "unity.SceneRef",
                    "{\"kind\":\"scene\",\"path\":\"Assets/x.unity\"}", false, null, c => c.Get<GameObject>("p")),
                GetCase("get SceneAsset on unity.SceneRef", "unity.SceneRef",
                    "{\"kind\":\"scene\",\"path\":\"Assets/x.unity\"}", false, null, c => c.Get<SceneAsset>("p")),
                GetLiveCase("get GameObject on unity.ComponentRef<unity.Animator>",
                    "unity.ComponentRef<unity.Animator>",
                    f => f.NewObject("M").AddComponent<Animator>(),
                    e => new RefResolver().ToRef(
                        (Component)e, "unity.ComponentRef<unity.Animator>"),
                    false, c => c.Get<GameObject>("p")),
                GetLiveCase("get Transform on unity.ComponentRef<unity.Animator>",
                    "unity.ComponentRef<unity.Animator>",
                    f => f.NewObject("M").AddComponent<Animator>(),
                    e => new RefResolver().ToRef(
                        (Component)e, "unity.ComponentRef<unity.Animator>"),
                    false, c => c.Get<Transform>("p")),
                GetCase("get Scene on unity.GameObjectRef", "unity.GameObjectRef",
                    "{\"kind\":\"gameObject\",\"path\":\"/X\"}", false, null, c => c.Get<Scene>("p")),
                GetLiveCase("get Scene on unity.ComponentRef<unity.Animator>",
                    "unity.ComponentRef<unity.Animator>",
                    f => f.NewObject("M").AddComponent<Animator>(),
                    e => new RefResolver().ToRef(
                        (Component)e, "unity.ComponentRef<unity.Animator>"),
                    false, c => c.Get<Scene>("p")),
                GetCase("get Vector3 on unity.Vector2", "unity.Vector2",
                    "{\"x\":1,\"y\":2}", false, null, c => c.Get<Vector3>("p")),
                GetCase("get Vector2 on unity.Vector3", "unity.Vector3",
                    "{\"x\":1,\"y\":2,\"z\":3}", false, null, c => c.Get<Vector2>("p")),
                GetCase("get Vector3 on core.String", "core.String",
                    "{\"x\":1,\"y\":2,\"z\":3}", false, null, c => c.Get<Vector3>("p")),
                GetCase("get Vector3 on unity.Vector3 with a non-object token", "unity.Vector3",
                    "\"x\"", false, null, c => c.Get<Vector3>("p")),
                GetCase("get Vector3 on unity.Vector3 with a missing member", "unity.Vector3",
                    "{\"x\":1}", false, null, c => c.Get<Vector3>("p")),
                GetCase("get Vector3 on an undeclared port", null,
                    "{\"x\":1,\"y\":2,\"z\":3}", false, null, c => c.Get<Vector3>("p")),
                GetCase("get string on an undeclared port", null,
                    "\"x\"", false, null, c => c.Get<string>("p")),
                GetLiveCase("get GameObject on an undeclared port", null,
                    f => f.NewObject("M"),
                    e => new RefResolver().ToRef((GameObject)e, "unity.GameObjectRef"),
                    false, c => c.Get<GameObject>("p")),
                GetLiveCase("get GameObject on unity.AssetRef<unity.Material>",
                    "unity.AssetRef<unity.Material>",
                    f => f.NewMaterialAsset(),
                    e => new RefResolver().ToRef(
                        (Material)e, "unity.AssetRef<unity.Material>"),
                    false, c => c.Get<GameObject>("p")),
                // A resolvable broad Ref whose entity the requested CLR
                // type cannot hold resolves first, then fails the Get as
                // the OW-RUN-007 type mismatch (PR #182 F2), never the
                // OW-UNITY-002 ambiguity/conflict of §J.
                GetLiveCase("get Component on unity.ObjectRef holding a GameObject",
                    "unity.ObjectRef",
                    f => f.NewObject("M"),
                    e => new RefResolver().ToRef((GameObject)e, "unity.ObjectRef"),
                    false, c => c.Get<Component>("p")),
                GetLiveCase("get Animator on unity.ObjectRef holding a GameObject",
                    "unity.ObjectRef",
                    f => f.NewObject("M"),
                    e => new RefResolver().ToRef((GameObject)e, "unity.ObjectRef"),
                    false, c => c.Get<Animator>("p")),
                GetLiveCase("get Material on unity.ObjectRef holding a GameObject",
                    "unity.ObjectRef",
                    f => f.NewObject("M"),
                    e => new RefResolver().ToRef((GameObject)e, "unity.ObjectRef"),
                    false, c => c.Get<Material>("p")),

                // ── Set: JsonValue, null and the codec-less primitives ───
                SetCase("set JsonValue on any port", "core.String",
                    true, _ => c => c.SetOutput("p", Json("5")),
                    _ => Json("5")),
                SetCase("set JsonValue on an undeclared port", null,
                    true, _ => c => c.SetOutput("p", Json("{\"a\":1}")),
                    _ => Json("{\"a\":1}")),
                SetCase("set null on any port", "core.Bool",
                    true, _ => c => c.SetOutput("p", (object?)null),
                    _ => Json("null")),
                SetCase("set null on an undeclared port", null,
                    true, _ => c => c.SetOutput("p", (object?)null),
                    _ => Json("null")),
                SetCase("set string on core.String", "core.String",
                    true, _ => c => c.SetOutput("p", "hello"),
                    _ => Json("\"hello\"")),
                SetCase("set string on unity.AssetPath", "unity.AssetPath",
                    true, _ => c => c.SetOutput("p", "Assets/x.asset"),
                    _ => Json("\"Assets/x.asset\"")),
                SetCase("set string on unity.TypeName", "unity.TypeName",
                    true, _ => c => c.SetOutput("p", "UnityEngine.Animator"),
                    _ => Json("\"UnityEngine.Animator\"")),
                SetCase("set string on unity.HierarchyPath", "unity.HierarchyPath",
                    true, _ => c => c.SetOutput("p", "/Root/Child"),
                    _ => Json("\"/Root/Child\"")),
                SetCase("set long on core.Int", "core.Int",
                    true, _ => c => c.SetOutput("p", 7L),
                    _ => Json("7")),
                SetCase("set int on core.Int", "core.Int",
                    true, _ => c => c.SetOutput("p", 7),
                    _ => Json("7")),
                SetCase("set double on core.Float", "core.Float",
                    true, _ => c => c.SetOutput("p", 2.5),
                    _ => Json("2.5")),
                SetCase("set float on core.Float", "core.Float",
                    true, _ => c => c.SetOutput("p", 2.5f),
                    _ => Json("2.5")),
                SetCase("set bool on core.Bool", "core.Bool",
                    true, _ => c => c.SetOutput("p", true),
                    _ => Json("true")),

                // ── Set: the six CLR-codec value types ───────────────────
                SetCase("set Vector2 on unity.Vector2", "unity.Vector2",
                    true, _ => c => c.SetOutput("p", new Vector2(1f, 2f)),
                    _ => Json("{\"x\":1e0,\"y\":2e0}")),
                SetCase("set Vector3 on unity.Vector3", "unity.Vector3",
                    true, _ => c => c.SetOutput("p", new Vector3(1f, 2f, 3f)),
                    _ => Json("{\"x\":1e0,\"y\":2e0,\"z\":3e0}")),
                SetCase("set Vector3 on unity.Euler", "unity.Euler",
                    true, _ => c => c.SetOutput("p", new Vector3(0f, 90f, 0f)),
                    _ => Json("{\"x\":0e0,\"y\":90e0,\"z\":0e0}")),
                SetCase("set Vector4 on unity.Vector4", "unity.Vector4",
                    true, _ => c => c.SetOutput("p", new Vector4(1f, 2f, 3f, 4f)),
                    _ => Json("{\"x\":1e0,\"y\":2e0,\"z\":3e0,\"w\":4e0}")),
                SetCase("set Quaternion on unity.Quaternion", "unity.Quaternion",
                    true, _ => c => c.SetOutput("p", Quaternion.identity),
                    _ => Json("{\"x\":0e0,\"y\":0e0,\"z\":0e0,\"w\":1e0}")),
                SetCase("set Color on unity.Color", "unity.Color",
                    true, _ => c => c.SetOutput("p", new Color(1f, 0f, 0f, 1f)),
                    _ => Json("{\"r\":1e0,\"g\":0e0,\"b\":0e0,\"a\":1e0}")),

                // ── Set: Ref writes of every kind ────────────────────────
                SetLiveCase("set GameObject on unity.GameObjectRef", "unity.GameObjectRef",
                    f => f.NewObject("M"), "unity.GameObjectRef"),
                SetLiveCase("set GameObject on unity.ObjectRef", "unity.ObjectRef",
                    f => f.NewObject("M"), "unity.ObjectRef"),
                SetLiveCase("set Animator on unity.ComponentRef<unity.Animator>",
                    "unity.ComponentRef<unity.Animator>",
                    f => f.NewObject("M").AddComponent<Animator>(),
                    "unity.ComponentRef<unity.Animator>"),
                SetLiveCase("set Animator on unity.ComponentRef<unity.Component>",
                    "unity.ComponentRef<unity.Component>",
                    f => f.NewObject("M").AddComponent<Animator>(),
                    "unity.ComponentRef<unity.Component>"),
                SetLiveCase("set prefab asset on unity.PrefabAssetRef", "unity.PrefabAssetRef",
                    f => f.NewPrefabAsset(), "unity.PrefabAssetRef"),
                SetLiveCase("set prefab asset on unity.AssetRef<unity.GameObject>",
                    "unity.AssetRef<unity.GameObject>",
                    f => f.NewPrefabAsset(), "unity.AssetRef<unity.GameObject>"),
                SetLiveCase("set prefab instance on unity.PrefabInstanceRef", "unity.PrefabInstanceRef",
                    f => f.NewPrefabInstance(), "unity.PrefabInstanceRef"),
                SetLiveCase("set Material asset on unity.AssetRef<unity.Material>",
                    "unity.AssetRef<unity.Material>",
                    f => f.NewMaterialAsset(), "unity.AssetRef<unity.Material>"),
                SetLiveCase("set SceneAsset on unity.SceneRef", "unity.SceneRef",
                    f => f.NewSceneAsset(), "unity.SceneRef"),
                SetLiveCase("set SceneAsset on unity.ObjectRef", "unity.ObjectRef",
                    f => f.NewSceneAsset(), "unity.ObjectRef"),

                // ── Set: every invalid pair → OW-RUN-007 ─────────────────
                SetCase("set GameObject on core.String", "core.String",
                    false, f => c => c.SetOutput("p", f.NewObject("M"))),
                SetCase("set GameObject on core.Json", "core.Json",
                    false, f => c => c.SetOutput("p", f.NewObject("M"))),
                SetCase("set GameObject on core.Int", "core.Int",
                    false, f => c => c.SetOutput("p", f.NewObject("M"))),
                SetCase("set GameObject on unity.SceneRef", "unity.SceneRef",
                    false, f => c => c.SetOutput("p", f.NewObject("M"))),
                SetCase("set GameObject on unity.PrefabAssetRef", "unity.PrefabAssetRef",
                    false, f => c => c.SetOutput("p", f.NewObject("M"))),
                SetCase("set GameObject on unity.PrefabInstanceRef", "unity.PrefabInstanceRef",
                    false, f => c => c.SetOutput("p", f.NewObject("M"))),
                SetCase("set GameObject on unity.ComponentRef<unity.Animator>",
                    "unity.ComponentRef<unity.Animator>",
                    false, f => c => c.SetOutput("p", f.NewObject("M"))),
                SetCase("set GameObject on an undeclared port", null,
                    false, f => c => c.SetOutput("p", f.NewObject("M"))),
                SetCase("set Animator on unity.GameObjectRef", "unity.GameObjectRef",
                    false, f => c => c.SetOutput("p", f.NewObject("M").AddComponent<Animator>())),
                SetCase("set Animator on unity.ComponentRef<unity.Transform>",
                    "unity.ComponentRef<unity.Transform>",
                    false, f => c => c.SetOutput("p", f.NewObject("M").AddComponent<Animator>())),
                SetCase("set Animator on unity.AssetRef<unity.Animator>",
                    "unity.AssetRef<unity.Animator>",
                    false, f => c => c.SetOutput("p", f.NewObject("M").AddComponent<Animator>())),
                SetCase("set Animator on core.String", "core.String",
                    false, f => c => c.SetOutput("p", f.NewObject("M").AddComponent<Animator>())),
                SetCase("set Material asset on unity.GameObjectRef", "unity.GameObjectRef",
                    false, f => c => c.SetOutput("p", f.NewMaterialAsset())),
                SetCase("set Material asset on unity.AssetRef<unity.Texture2D>",
                    "unity.AssetRef<unity.Texture2D>",
                    false, f => c => c.SetOutput("p", f.NewMaterialAsset())),
                SetCase("set SceneAsset on unity.GameObjectRef", "unity.GameObjectRef",
                    false, f => c => c.SetOutput("p", f.NewSceneAsset())),
                SetCase("set SceneAsset on unity.AssetRef<unity.Material>",
                    "unity.AssetRef<unity.Material>",
                    false, f => c => c.SetOutput("p", f.NewSceneAsset())),
                SetCase("set Scene on unity.SceneRef", "unity.SceneRef",
                    false, f => c => c.SetOutput("p", f.OpenProbeScene())),
                SetCase("set Scene on core.Json", "core.Json",
                    false, f => c => c.SetOutput("p", f.OpenProbeScene())),
                SetCase("set Vector3 on unity.Vector2", "unity.Vector2",
                    false, _ => c => c.SetOutput("p", new Vector3(1f, 2f, 3f))),
                SetCase("set Vector4 on unity.Vector3", "unity.Vector3",
                    false, _ => c => c.SetOutput("p", new Vector4(1f, 2f, 3f, 4f))),
                SetCase("set Vector3 on core.Json", "core.Json",
                    false, _ => c => c.SetOutput("p", new Vector3(1f, 2f, 3f))),
                SetCase("set Vector3 on an undeclared port", null,
                    false, _ => c => c.SetOutput("p", new Vector3(1f, 2f, 3f))),
                SetCase("set non-finite Quaternion on unity.Quaternion", "unity.Quaternion",
                    false, _ => c => c.SetOutput("p",
                        new Quaternion(float.NaN, 0f, 0f, 0f))),
                SetCase("set string on core.Int", "core.Int",
                    false, _ => c => c.SetOutput("p", "x")),
                SetCase("set string on unity.GameObjectRef", "unity.GameObjectRef",
                    false, _ => c => c.SetOutput("p", "x")),
                SetCase("set string on core.Json", "core.Json",
                    false, _ => c => c.SetOutput("p", "x")),
                SetCase("set string on an undeclared port", null,
                    false, _ => c => c.SetOutput("p", "x")),
                SetCase("set long on core.String", "core.String",
                    false, _ => c => c.SetOutput("p", 7L)),
                SetCase("set long on core.Float", "core.Float",
                    false, _ => c => c.SetOutput("p", 7L)),
                SetCase("set double on core.Int", "core.Int",
                    false, _ => c => c.SetOutput("p", 2.5)),
                SetCase("set non-finite double on core.Float", "core.Float",
                    false, _ => c => c.SetOutput("p", double.NaN)),
                SetCase("set bool on core.Int", "core.Int",
                    false, _ => c => c.SetOutput("p", true)),
                SetCase("set int[] on core.List<core.Int>", "core.List<core.Int>",
                    false, _ => c => c.SetOutput("p", new[] { 1, 2 })),
                SetCase("set DateTime on core.Json", "core.Json",
                    false, _ => c => c.SetOutput("p", new DateTime(2026, 1, 1))),
                SetCase("set JObject on core.Json", "core.Json",
                    false, _ => c => c.SetOutput("p", new JObject())),
                SetCase("set anonymous object on core.Json", "core.Json",
                    false, _ => c => c.SetOutput("p", new { X = 1 })),
            };
            foreach (var row in rows)
            {
                yield return new TestCaseData(row).SetName(row.Name);
            }
        }

        [TestCaseSource(nameof(ConversionMatrixCases))]
        public void ConversionMatrixConvertsValidPairsAndRejectsInvalidWithOwRun007(
            MatrixRow row)
        {
            var types = row.TypeId is null
                ? null
                : new Dictionary<string, string> { ["p"] = row.TypeId };
            var context = row.IsSet
                ? Context(outputTypes: types)
                : Context(
                    inputs: new Dictionary<string, InputValue>
                    {
                        ["p"] = InputValue.Present(row.Stored!(this)),
                    },
                    inputTypes: types);
            var act = row.Act(this);
            if (row.Valid)
            {
                AssertExpected(row.Expected!(this), act(context), row.Name);
            }
            else
            {
                AssertOwRun007(() => act(context), row.Name);
            }
        }

        // A valid matrix row asserts the produced value itself (PR #182
        // F3): canonical JSON for a verbatim JsonValue read and every
        // SetOutput write, reference identity for a resolved Ref, a float
        // tolerance for the decoded single-precision value types and exact
        // equality for the integral CLR values.
        private static void AssertExpected(object? expected, object? actual, string name)
        {
            Assert.IsNotNull(actual, name);
            Assert.IsNotNull(expected, name);
            switch (expected)
            {
                case JsonValue json:
                    Assert.IsInstanceOf<JsonValue>(actual, name);
                    Assert.AreEqual(
                        json.CanonicalText, ((JsonValue)actual!).CanonicalText, name);
                    break;
                case UnityEngine.Object entity:
                    Assert.AreSame(entity, actual, name);
                    break;
                case float single:
                    Assert.AreEqual(single, (float)actual!, Tolerance, name);
                    break;
                case double wide:
                    Assert.AreEqual(wide, (double)actual!, Tolerance, name);
                    break;
                case Vector2 vector:
                    var vector2 = (Vector2)actual!;
                    AssertComponentsNear(
                        new[] { vector.x, vector.y },
                        new[] { vector2.x, vector2.y }, name);
                    break;
                case Vector3 vector:
                    var vector3 = (Vector3)actual!;
                    AssertComponentsNear(
                        new[] { vector.x, vector.y, vector.z },
                        new[] { vector3.x, vector3.y, vector3.z }, name);
                    break;
                case Vector4 vector:
                    var vector4 = (Vector4)actual!;
                    AssertComponentsNear(
                        new[] { vector.x, vector.y, vector.z, vector.w },
                        new[] { vector4.x, vector4.y, vector4.z, vector4.w }, name);
                    break;
                case Quaternion quaternion:
                    var rotation = (Quaternion)actual!;
                    AssertComponentsNear(
                        new[] { quaternion.x, quaternion.y, quaternion.z, quaternion.w },
                        new[] { rotation.x, rotation.y, rotation.z, rotation.w }, name);
                    break;
                case Color color:
                    var rgba = (Color)actual!;
                    AssertComponentsNear(
                        new[] { color.r, color.g, color.b, color.a },
                        new[] { rgba.r, rgba.g, rgba.b, rgba.a }, name);
                    break;
                default:
                    Assert.AreEqual(expected, actual, name);
                    break;
            }
        }

        private const double Tolerance = 1e-6;

        private static void AssertComponentsNear(
            float[] expected, float[] actual, string name)
        {
            Assert.AreEqual(expected.Length, actual.Length, name);
            for (var i = 0; i < expected.Length; i++)
            {
                Assert.AreEqual(expected[i], actual[i], Tolerance, name);
            }
        }

        // ── Runner level: an incompatible SetOutput is OW-RUN-007 ────────

        /// <summary>Writes a GameObject to a core.String-typed output (F1).</summary>
        private sealed class BadOutputWriterOperation : OperationBase
        {
            private readonly GameObject _target;

            internal BadOutputWriterOperation(GameObject target)
            {
                _target = target;
                Descriptor = Op.Define("test.stepcontext.badwrite", 1)
                    .Summary("Writes an incompatible output value.")
                    .Output("result", "core.String")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx)
            {
                ctx.SetOutput("result", _target);
                return StepResult.Succeeded();
            }
        }

        [Test]
        public void IncompatibleSetOutputFailsStepWithOwRun007NotOwRun006()
        {
            var fixture = NewFixture();
            fixture.Register(new BadOutputWriterOperation(NewObject("BadWriteTarget")), 'a');
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "write", "test.stepcontext.badwrite", 'a',
                    outputTypes: new Dictionary<string, string>
                    {
                        ["result"] = "core.String",
                    },
                    next: -1,
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[]
                {
                    new NodeData { Id = "write", Operation = "test.stepcontext.badwrite", Version = 1 },
                },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed: " + Describe(accepted.Diagnostics));
            var runId = accepted.Value!.RunId;
            Drive(fixture.Host, runId);

            // SPEC §0.6 SG-06 / §J: the context's conversion failure is the
            // step's OW-RUN-007 type diagnostic, never the generic
            // OW-RUN-006 an arbitrary Operation exception earns.
            Assert.AreEqual(
                RunState.Failed, fixture.Host.Coordinator.StateOf(runId),
                "an incompatible SetOutput fails the step's Run");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_failed\"", "OW-RUN-007"),
                Is.True, "the step must fail with OW-RUN-007");
            Assert.That(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_failed\"", "OW-RUN-006"),
                Is.False, "the mismatch must not collapse into OW-RUN-006");
        }

        // ── OperationBase defaults (SPEC §C2) ────────────────────────────

        private sealed class BareOperation : OperationBase
        {
            internal BareOperation()
            {
                Descriptor = Op.Define("test.stepcontext.bare", 1)
                    .Summary("Overrides nothing.")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build();
            }

            public override OperationDescriptor Descriptor { get; }

            public override StepResult Execute(StepContext ctx) => StepResult.Succeeded();
        }

        [Test]
        public void OperationBaseDefaultsMatchTheDeclaredMode()
        {
            var op = new BareOperation();
            var context = Context();

            Assert.AreEqual(0, op.ValidateStatic(new StaticStub()).Count);
            var preflight = op.Preflight(context);
            Assert.IsNotNull(preflight);
            Assert.AreEqual(0, preflight.Diagnostics.Count);
            Assert.AreEqual(0, preflight.PlannedChanges.Count);
            Assert.AreEqual("{}", preflight.Snapshot.CanonicalText);
            Assert.Throws<NotSupportedException>(() => op.Poll(context),
                "Poll is meaningful only for a Waitable Operation");
            Assert.IsInstanceOf<ReconcileResult.Unknown>(
                op.Reconcile(context, Json("{}")),
                "an unimplemented Reconcile is NeedsReview, never a guessed verdict");
            Assert.DoesNotThrow(() => op.Cancel(context));
            Assert.AreEqual(StepResultKind.Succeeded, op.Execute(context).Kind);
        }

        private sealed class StaticStub : StaticContext
        {
            public override string NodeId => "node";
            public override IReadOnlyDictionary<string, InputValue> Parameters =>
                new Dictionary<string, InputValue>();
            public override T Get<T>(string port) =>
                throw new InvalidOperationException("No parameters.");
        }
    }
}
