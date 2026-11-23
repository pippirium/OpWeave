#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §C2/§C0.1/§0.6 SG-06 operation lifecycle
    /// contract (T-W1.5-03): Unity-independent dummy Operations exercise
    /// <see cref="IOperation"/>, the abstract
    /// <see cref="StaticContext"/>/<see cref="StepContext"/> surface,
    /// <see cref="IChangeRecorder"/>, <see cref="IRunLogSink"/> and
    /// <see cref="IEffectResolver"/>; reflection pins the exact public member
    /// set the SG-06 surface approves.
    /// </summary>
    internal sealed class OperationContractTests
    {
        private static readonly BindingFlags s_publicInstance =
            BindingFlags.Public | BindingFlags.Instance;

        private static Fixture Step(
            IReadOnlyDictionary<string, InputValue>? parameters = null,
            JsonValue? resumeToken = null,
            IEnumerable<string>? pendingPorts = null) =>
            new Fixture(parameters, resumeToken, pendingPorts);

        /// <summary>Bundles a fixture static/step context pair for one test.</summary>
        private sealed class Fixture
        {
            public Fixture(
                IReadOnlyDictionary<string, InputValue>? parameters,
                JsonValue? resumeToken,
                IEnumerable<string>? pendingPorts)
            {
                Static = new OperationContractFixtures.Static("fixtureNode", parameters);
                Step = new OperationContractFixtures.Step(
                    Static, stepIndex: 0, resumeToken: resumeToken, pendingPorts: pendingPorts);
            }

            public OperationContractFixtures.Static Static { get; }

            public OperationContractFixtures.Step Step { get; }
        }

        private static IReadOnlyDictionary<string, InputValue> Params(
            params KeyValuePair<string, InputValue>[] entries) =>
            new Dictionary<string, InputValue>(entries, StringComparer.Ordinal);

        private static KeyValuePair<string, InputValue> Param(string name, string json) =>
            new KeyValuePair<string, InputValue>(
                name, InputValue.Present(OperationContractFixtures.Json(json)));

        private static List<string> PublicMemberNames(Type type)
        {
            var names = new List<string>();
            foreach (var member in type.GetMembers(s_publicInstance))
            {
                if (member is MethodBase method && method.IsSpecialName)
                {
                    continue;
                }
                if (member is ConstructorInfo)
                {
                    continue;
                }
                if (member.DeclaringType == typeof(object))
                {
                    continue;
                }
                names.Add(member.Name);
            }
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        // ── SG-06 exact public surface (SPEC §0.6 SG-06) ──────────────────────

        [Test]
        public void StaticContextHasExactlyTheSg06PublicSurface()
        {
            Assert.IsTrue(typeof(StaticContext).IsClass);
            Assert.IsTrue(typeof(StaticContext).IsAbstract);
            Assert.AreEqual(
                new[] { "Get", "NodeId", "Parameters", "StaticTypeOf" },
                PublicMemberNames(typeof(StaticContext)));

            var get = typeof(StaticContext).GetMethod(
                nameof(StaticContext.Get), s_publicInstance);
            Assert.IsNotNull(get);
            Assert.IsTrue(get!.IsGenericMethodDefinition);
            Assert.AreEqual(get.GetGenericArguments()[0], get.ReturnType);

            var parameters = typeof(StaticContext).GetProperty(
                nameof(StaticContext.Parameters), s_publicInstance);
            Assert.AreEqual(
                typeof(IReadOnlyDictionary<string, InputValue>),
                parameters!.PropertyType);

            // SPEC §0.6 SG-06 (2026-11-06): StaticTypeOf is a virtual method —
            // non-abstract so existing external subclasses keep compiling.
            var staticTypeOf = typeof(StaticContext).GetMethod(
                nameof(StaticContext.StaticTypeOf), s_publicInstance);
            Assert.IsNotNull(staticTypeOf);
            Assert.AreEqual(typeof(TypeRef), staticTypeOf!.ReturnType);
            Assert.IsTrue(staticTypeOf.IsVirtual);
            Assert.IsFalse(staticTypeOf.IsAbstract);
            var staticTypeOfParameters = staticTypeOf.GetParameters();
            Assert.AreEqual(1, staticTypeOfParameters.Length);
            Assert.AreEqual(typeof(string), staticTypeOfParameters[0].ParameterType);
        }

        [Test]
        public void StepContextHasExactlyTheSg06PublicSurface()
        {
            Assert.IsTrue(typeof(StepContext).IsClass);
            Assert.IsTrue(typeof(StepContext).IsAbstract);
            Assert.AreEqual(
                new[]
                {
                    "Changes", "Get", "IsPending", "IsPresent",
                    "Log", "ResumeToken", "SetOutput",
                },
                PublicMemberNames(typeof(StepContext)));

            var setOutput = typeof(StepContext).GetMethod(
                nameof(StepContext.SetOutput), s_publicInstance);
            Assert.IsTrue(setOutput!.IsGenericMethodDefinition);

            Assert.AreEqual(
                typeof(JsonValue),
                typeof(StepContext).GetProperty(
                    nameof(StepContext.ResumeToken), s_publicInstance)!.PropertyType);
            Assert.AreEqual(
                typeof(IChangeRecorder),
                typeof(StepContext).GetProperty(
                    nameof(StepContext.Changes), s_publicInstance)!.PropertyType);
            Assert.AreEqual(
                typeof(IRunLogSink),
                typeof(StepContext).GetProperty(
                    nameof(StepContext.Log), s_publicInstance)!.PropertyType);
        }

        [Test]
        public void ChangeRecorderHasExactlyTheFiveSpecVerbs()
        {
            Assert.AreEqual(
                new[] { "Created", "Deleted", "Modified", "Moved", "Saved" },
                PublicMemberNames(typeof(IChangeRecorder)));
        }

        [Test]
        public void RunLogSinkHasExactlyTheSg06Write()
        {
            Assert.AreEqual(new[] { "Write" }, PublicMemberNames(typeof(IRunLogSink)));

            var write = typeof(IRunLogSink).GetMethod(
                nameof(IRunLogSink.Write), s_publicInstance)!;
            var parameters = write.GetParameters();
            Assert.AreEqual(4, parameters.Length);
            Assert.AreEqual(typeof(Severity), parameters[0].ParameterType);
            Assert.AreEqual(typeof(string), parameters[1].ParameterType);
            Assert.AreEqual(typeof(string), parameters[2].ParameterType);
            Assert.AreEqual(typeof(JsonValue), parameters[3].ParameterType);
            Assert.IsTrue(parameters[2].HasDefaultValue);
            Assert.IsTrue(parameters[3].HasDefaultValue);
        }

        [Test]
        public void EffectResolverHasExactlyResolveEffects()
        {
            Assert.AreEqual(
                new[] { "ResolveEffects" },
                PublicMemberNames(typeof(IEffectResolver)));

            var resolve = typeof(IEffectResolver).GetMethod(
                nameof(IEffectResolver.ResolveEffects), s_publicInstance)!;
            Assert.AreEqual(
                typeof(IReadOnlyList<string>), resolve.ReturnType);
            var parameters = resolve.GetParameters();
            Assert.AreEqual(1, parameters.Length);
            Assert.AreEqual(typeof(StaticContext), parameters[0].ParameterType);
        }

        [Test]
        public void OperationInterfaceExposesTheSpecMembers()
        {
            Assert.IsTrue(typeof(IOperation).IsInterface);
            Assert.AreEqual(
                new[]
                {
                    "Cancel", "Descriptor", "Execute", "Poll",
                    "Preflight", "Reconcile", "ValidateStatic",
                },
                PublicMemberNames(typeof(IOperation)));

            Assert.AreEqual(
                typeof(OperationDescriptor),
                typeof(IOperation).GetProperty(
                    nameof(IOperation.Descriptor), s_publicInstance)!.PropertyType);
            Assert.AreEqual(
                typeof(IReadOnlyList<Diagnostic>),
                typeof(IOperation).GetMethod(
                    nameof(IOperation.ValidateStatic), s_publicInstance)!.ReturnType);
            Assert.AreEqual(
                typeof(PreflightResult),
                typeof(IOperation).GetMethod(
                    nameof(IOperation.Preflight), s_publicInstance)!.ReturnType);
            Assert.AreEqual(
                typeof(StepResult),
                typeof(IOperation).GetMethod(
                    nameof(IOperation.Execute), s_publicInstance)!.ReturnType);
            Assert.AreEqual(
                typeof(StepResult),
                typeof(IOperation).GetMethod(
                    nameof(IOperation.Poll), s_publicInstance)!.ReturnType);
            Assert.AreEqual(
                typeof(ReconcileResult),
                typeof(IOperation).GetMethod(
                    nameof(IOperation.Reconcile), s_publicInstance)!.ReturnType);
        }

        [Test]
        public void OpWeaveOperationAttributeIsAClassOnlyDetectionContract()
        {
            var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
                typeof(OpWeaveOperationAttribute), typeof(AttributeUsageAttribute))!;
            Assert.AreEqual(AttributeTargets.Class, usage.ValidOn);
            Assert.IsFalse(usage.Inherited);

            var attribute = (OpWeaveOperationAttribute)Attribute.GetCustomAttribute(
                typeof(OperationContractFixtures.ImmediateOperation),
                typeof(OpWeaveOperationAttribute))!;
            Assert.IsNotNull(attribute);
            // SPEC §C2 item 2: the attribute declares the Operation's
            // (id, version) on the type — the same identity the Descriptor
            // reports after construction (T-W2.2-08).
            Assert.AreEqual("test.dummy.immediate", attribute.Id);
            Assert.AreEqual(1, attribute.Version);
            StringAssert.EndsWith("OperationContractFixtures.cs", attribute.SourceFile);
        }

        // ── Lifecycle (SPEC §C2 tests) ─────────────────────────────────────────

        [Test]
        public void ImmediateOperationRunsToSucceeded()
        {
            var operation = new OperationContractFixtures.ImmediateOperation();
            var fixture = Step(Params(Param("name", "\"alpha\"")));

            var result = operation.Execute(fixture.Step);

            Assert.AreEqual(StepResultKind.Succeeded, result.Kind);
            Assert.AreEqual(1, fixture.Step.WrittenOutputs.Count);
            Assert.AreEqual("echo", fixture.Step.WrittenOutputs[0].Key);
            Assert.AreEqual("\"echo:alpha\"", fixture.Step.WrittenOutputs[0].Value.CanonicalText);
            Assert.AreEqual(1, fixture.Step.Sink.Entries.Count);
            Assert.AreEqual(Severity.Info, fixture.Step.Sink.Entries[0].Level);
        }

        [Test]
        public void WaitableOperationWaitsThenPollsToSucceeded()
        {
            var operation = new OperationContractFixtures.WaitableOperation();
            var fixture = Step();

            var waiting = operation.Execute(fixture.Step);
            Assert.AreEqual(StepResultKind.Waiting, waiting.Kind);
            Assert.IsNotNull(waiting.ResumeToken);
            Assert.AreEqual(
                OperationContractFixtures.WaitableOperation.StageToken,
                waiting.ResumeToken!.CanonicalText);
            Assert.AreEqual("fixture_wait", waiting.WaitingFor);
            Assert.AreEqual(50, waiting.PollIntervalMs);

            var resumed = Step(resumeToken: waiting.ResumeToken);
            var finished = operation.Poll(resumed.Step);
            Assert.AreEqual(StepResultKind.Succeeded, finished.Kind);
            Assert.AreEqual("done", resumed.Step.WrittenOutputs[0].Key);
            Assert.AreEqual("true", resumed.Step.WrittenOutputs[0].Value.CanonicalText);
        }

        [Test]
        public void PollResumesFromResumeTokenOnAFreshInstance()
        {
            // SPEC §C2 items 3-4 / test: waiting state lives only in the
            // resumeToken, so a new Operation instance — as after a domain
            // reload — continues from the token alone.
            var first = new OperationContractFixtures.WaitableOperation();
            var waiting = first.Execute(Step().Step);
            Assert.AreEqual(StepResultKind.Waiting, waiting.Kind);

            var fresh = new OperationContractFixtures.WaitableOperation();
            var resumed = Step(resumeToken: waiting.ResumeToken);
            var finished = fresh.Poll(resumed.Step);

            Assert.AreEqual(StepResultKind.Succeeded, finished.Kind);
            Assert.AreEqual("true", resumed.Step.WrittenOutputs[0].Value.CanonicalText);
        }

        [Test]
        public void MissingInputIsNotPresentAndNeverBecomesNull()
        {
            var fixture = Step(Params(
                Param("explicitNull", "null"),
                Param("name", "\"alpha\""),
                new KeyValuePair<string, InputValue>("missing", InputValue.Missing)));

            Assert.IsFalse(fixture.Step.IsPresent("missing"));
            Assert.IsFalse(fixture.Step.IsPresent("absent"));

            // An explicit JSON null is present and stays a JsonValue, never a
            // null reference (SPEC §A0.1, §0.6 SG-06).
            Assert.IsTrue(fixture.Step.IsPresent("explicitNull"));
            Assert.AreEqual(
                "null", fixture.Static.Parameters["explicitNull"].Value!.CanonicalText);
            var value = fixture.Step.Get<JsonValue>("explicitNull");
            Assert.AreEqual("null", value.CanonicalText);
        }

        [Test]
        public void PendingInputIsReportedByIsPending()
        {
            var fixture = Step(
                Params(Param("name", "\"alpha\"")),
                pendingPorts: new[] { "upstreamValue" });

            Assert.IsTrue(fixture.Step.IsPending("upstreamValue"));
            Assert.IsTrue(fixture.Step.IsPresent("upstreamValue"));
            Assert.IsFalse(fixture.Step.IsPending("name"));
            Assert.Throws<InvalidOperationException>(
                () => fixture.Step.Get<string>("upstreamValue"));
        }

        [Test]
        public void PreflightIsSideEffectFreeAndReturnsPlannedChangesAndSnapshot()
        {
            var operation = new OperationContractFixtures.ImmediateOperation();
            var fixture = Step(Params(Param("name", "\"alpha\"")));

            var result = operation.Preflight(fixture.Step);

            Assert.AreEqual(0, result.Diagnostics.Count);
            Assert.AreEqual(1, result.PlannedChanges.Count);
            Assert.AreEqual("fixtureNode", result.PlannedChanges[0].NodeId);
            Assert.AreEqual("create", result.PlannedChanges[0].Kind);
            Assert.AreEqual("fixtureTarget", result.PlannedChanges[0].Target);
            Assert.AreEqual(EffectNames.Read, result.PlannedChanges[0].Effect);
            Assert.AreEqual(
                "{\"namePresent\":true}", result.Snapshot.CanonicalText);

            // Preflight produced no outputs and no recorded changes (SPEC §C2
            // item 7).
            Assert.AreEqual(0, fixture.Step.WrittenOutputs.Count);
            Assert.AreEqual(0, fixture.Step.Recorder.Records.Count);
        }

        [Test]
        public void ReconcileReportsAllThreeSpecOutcomes()
        {
            var operation = new OperationContractFixtures.ReconcilableOperation();
            var fixture = Step();

            var completed = (ReconcileResult.Completed)operation.Reconcile(
                fixture.Step, OperationContractFixtures.Json("{\"applied\":true}"));
            Assert.AreEqual("\"created\"", completed.Outputs["marker"].CanonicalText);

            var notApplied = operation.Reconcile(
                fixture.Step, OperationContractFixtures.Json("{\"applied\":false}"));
            Assert.IsInstanceOf<ReconcileResult.NotApplied>(notApplied);

            var unknown = (ReconcileResult.Unknown)operation.Reconcile(
                fixture.Step, OperationContractFixtures.Json("{}"));
            Assert.IsNotEmpty(unknown.Reason);
        }

        [Test]
        public void ReconcilableExecuteRecordsASpecShapedChange()
        {
            var operation = new OperationContractFixtures.ReconcilableOperation();
            var fixture = Step();

            var result = operation.Execute(fixture.Step);

            Assert.AreEqual(StepResultKind.Succeeded, result.Kind);
            Assert.AreEqual(1, fixture.Step.Recorder.Records.Count);
            var record = fixture.Step.Recorder.Records[0];
            Assert.AreEqual(0, record.Step);
            Assert.AreEqual("fixtureNode", record.NodeId);
            Assert.AreEqual(ChangeAction.Created, record.Action);
            Assert.AreEqual("asset", record.Target.Kind);
            Assert.AreEqual("Assets/marker.txt", record.Target.Path);
            Assert.AreEqual(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", record.Target.Guid);
            Assert.IsNull(record.Target.GlobalId);
            Assert.IsTrue(record.Undoable);
        }

        [Test]
        public void ChangeRecorderCapturesEverySpecVerb()
        {
            var recorder = new OperationContractFixtures.Changes(3, "node-7");
            var target = new ChangeTarget("asset", "Assets/a.mat");

            recorder.Created(target, undoable: true);
            recorder.Modified(target, undoable: true);
            recorder.Deleted(target, undoable: false);
            recorder.Moved(target, undoable: true);
            recorder.Saved(target, undoable: false);

            Assert.AreEqual(5, recorder.Records.Count);
            var actions = new ChangeAction[recorder.Records.Count];
            for (var i = 0; i < actions.Length; i++)
            {
                actions[i] = recorder.Records[i].Action;
            }
            Assert.AreEqual(
                new[]
                {
                    ChangeAction.Created, ChangeAction.Modified, ChangeAction.Deleted,
                    ChangeAction.Moved, ChangeAction.Saved,
                },
                actions);
            Assert.AreEqual(3, recorder.Records[2].Step);
            Assert.AreEqual("node-7", recorder.Records[2].NodeId);
            Assert.IsFalse(recorder.Records[2].Undoable);
            Assert.IsFalse(recorder.Records[4].Undoable);
        }

        [Test]
        public void RunLogSinkCapturesSpecShapedEntries()
        {
            var sink = new OperationContractFixtures.LogEntries(2);
            var data = OperationContractFixtures.Json("{\"attempt\":1}");

            sink.Write(Severity.Warning, "Low disk space.", "OW-STORE-001", data);
            sink.Write(Severity.Info, "Step started.");

            Assert.AreEqual(2, sink.Entries.Count);
            var entry = sink.Entries[0];
            StringAssert.EndsWith("Z", entry.Ts);
            Assert.AreEqual(Severity.Warning, entry.Level);
            Assert.AreEqual(2, entry.Step);
            Assert.AreEqual("OW-STORE-001", entry.Code);
            Assert.AreEqual("Low disk space.", entry.Message);
            Assert.AreEqual("{\"attempt\":1}", entry.Data!.CanonicalText);
            Assert.IsNull(sink.Entries[1].Code);
            Assert.IsNull(sink.Entries[1].Data);
        }

        [Test]
        public void EffectResolverReturnsOnlyClosedEffectNames()
        {
            var resolver = new OperationContractFixtures.EffectResolver();
            var asset = Step(Params(Param("assetTarget", "\"x\"")));
            var scene = Step();

            var assetEffects = resolver.ResolveEffects(asset.Static);
            var sceneEffects = resolver.ResolveEffects(scene.Static);

            Assert.AreEqual(new[] { EffectNames.AssetModify }, assetEffects);
            Assert.AreEqual(new[] { EffectNames.SceneModify }, sceneEffects);
            foreach (var effect in assetEffects)
            {
                Assert.IsTrue(EffectNames.IsKnown(effect));
            }
        }

        // ── Result and record shapes ────────────────────────────────────────────

        [Test]
        public void StepResultFactoriesExposeTheirPayloads()
        {
            var succeeded = StepResult.Succeeded();
            Assert.AreEqual(StepResultKind.Succeeded, succeeded.Kind);
            Assert.IsNull(succeeded.ResumeToken);
            Assert.IsNull(succeeded.WaitingFor);
            Assert.IsNull(succeeded.Error);

            var token = OperationContractFixtures.Json("{\"stage\":2}");
            var waiting = StepResult.Waiting(token, "compile_done", 250);
            Assert.AreEqual(StepResultKind.Waiting, waiting.Kind);
            Assert.AreSame(token, waiting.ResumeToken);
            Assert.AreEqual("compile_done", waiting.WaitingFor);
            Assert.AreEqual(250, waiting.PollIntervalMs);
            Assert.IsNull(waiting.Error);

            var error = ErrorCodes.Create(ErrorCodes.OW_RUN_006, "Boom.");
            var failed = StepResult.Failed(error);
            Assert.AreEqual(StepResultKind.Failed, failed.Kind);
            Assert.AreSame(error, failed.Error);
            Assert.IsNull(failed.ResumeToken);

            var defaulted = StepResult.Waiting(token, "wait");
            Assert.AreEqual(100, defaulted.PollIntervalMs);
        }

        [Test]
        public void StepResultFactoriesRejectInvalidArguments()
        {
            var token = OperationContractFixtures.Json("{}");
            Assert.Throws<ArgumentNullException>(
                () => StepResult.Waiting(null!, "wait"));
            Assert.Throws<ArgumentNullException>(
                () => StepResult.Waiting(token, null!));
            Assert.Throws<ArgumentException>(
                () => StepResult.Waiting(token, ""));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StepResult.Waiting(token, "wait", 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => StepResult.Waiting(token, "wait", -5));
            Assert.Throws<ArgumentNullException>(
                () => StepResult.Failed(null!));
        }

        [Test]
        public void ReconcileResultIsAClosedThreeOutcomeHierarchy()
        {
            Assert.IsTrue(typeof(ReconcileResult).IsAbstract);
            // The private constructor keeps external code from adding a fourth
            // verdict; only the three nested SPEC §F5 records exist.
            Assert.AreEqual(
                0, typeof(ReconcileResult).GetConstructors(
                    BindingFlags.Public | BindingFlags.Instance).Length);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    typeof(ReconcileResult.Completed),
                    typeof(ReconcileResult.NotApplied),
                    typeof(ReconcileResult.Unknown),
                },
                typeof(ReconcileResult).GetNestedTypes(
                    BindingFlags.Public | BindingFlags.NonPublic));
        }

        [Test]
        public void ReconcileCompletedCopiesItsOutputs()
        {
            var outputs = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                ["a"] = OperationContractFixtures.Json("1"),
            };
            var completed = new ReconcileResult.Completed(outputs);
            outputs["b"] = OperationContractFixtures.Json("2");
            Assert.AreEqual(1, completed.Outputs.Count);
            Assert.Throws<ArgumentException>(
                () => new ReconcileResult.Completed(
                    new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        ["a"] = null!,
                    }));
        }

        [Test]
        public void PreflightResultCopiesItsCollections()
        {
            var diagnostics = new List<Diagnostic>
            {
                ErrorCodes.Create(ErrorCodes.OW_UNITY_005, "Path occupied."),
            };
            var changes = new List<PlannedChange>
            {
                new PlannedChange("node-1", "create", "Assets/a", EffectNames.AssetCreate),
            };
            var snapshot = OperationContractFixtures.Json("{\"pathExisted\":false}");

            var result = new PreflightResult(diagnostics, changes, snapshot);
            diagnostics.Clear();
            changes.Clear();

            Assert.AreEqual(1, result.Diagnostics.Count);
            Assert.AreEqual(ErrorCodes.OW_UNITY_005, result.Diagnostics[0].Code);
            Assert.AreEqual(1, result.PlannedChanges.Count);
            Assert.AreEqual("{\"pathExisted\":false}", result.Snapshot.CanonicalText);

            Assert.Throws<ArgumentNullException>(
                () => new PreflightResult(
                    Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), null!));
        }
    }
}
