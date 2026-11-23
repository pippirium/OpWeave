#nullable enable

// T-W3.4-03 [Edit] tests: local:T-W3.4-03:agent-restrictions — SPEC §H4
// item 5 / §H4 test 1 / §H0: the Agent-facing surface (OpWeave.AgentApi,
// and the OpWeave.Pipeline command adapter when the package is present)
// carries no approval, rejection, NeedsReview-resolution or settings
// entry point, so an Agent has no way to approve its own Run; the Core
// human-only decision API of T-W1.8-09 keeps no actor parameter for an
// Agent to call it under; and a write attempt on
// ProjectSettings/OpWeaveSettings.json through a standard Operation is
// refused with OW-PERM-002 under the default and a widened allowedPaths
// while the file itself stays unchanged.
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.AgentApi;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.Operations;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// The §H4 item 5 restriction fixtures of
    /// <c>local:T-W3.4-03:agent-restrictions</c>.
    /// </summary>
    internal sealed class AgentRestrictionSurfaceTests
    {
        private const string WorkflowId = "example.agent-restrictions";
        private const string SettingsRelativePath = "ProjectSettings/OpWeaveSettings.json";

        private string _root = null!;
        private readonly List<RunnerHostFixture> _fixtures = new List<RunnerHostFixture>();

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(
                RunnerHostFixture.ProjectRoot, "Library", "OpWeaveAgentRestrictionTests",
                "t" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (var fixture in _fixtures)
            {
                fixture.Dispose();
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

        // ── the Agent surface carries no human-only entry point ────────────

        // SPEC §H4 test 1 / item 5: inspecting the Agent-facing assemblies —
        // OpWeave.AgentApi (the whole §H1 service) and OpWeave.Pipeline (the
        // §H2 command adapter, compiled only when com.unity.pipeline is
        // present) — finds no approval, rejection, NeedsReview-resolution or
        // settings member anywhere, public or private. §H2's CliCommand
        // methods may be non-public, so every declared member is judged, not
        // only the exported surface.
        [Test]
        public void AgentAssemblies_DeclareNoApprovalReviewOrSettingsMembers()
        {
            var assemblies = new List<Assembly> { typeof(AgentApiService).Assembly };
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(
                        loaded.GetName().Name, "OpWeave.Pipeline", StringComparison.Ordinal))
                {
                    assemblies.Add(loaded);
                }
            }

            // The §H4 item 5 exclusions, as name fragments: approval /
            // rejection, NeedsReview resolution, settings (including the
            // whitelist and discoveryMode halves of a settings change).
            var forbidden = new[]
            {
                "approve", "reject", "resolve", "review",
                "settings", "whitelist", "discovery",
            };
            const BindingFlags declared =
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            var offenders = new List<string>();
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetTypes())
                {
                    var names = new List<string> { type.Name };
                    foreach (var member in type.GetMembers(declared))
                    {
                        names.Add(member.Name);
                    }
                    foreach (var name in names)
                    {
                        foreach (var fragment in forbidden)
                        {
                            if (name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                offenders.Add(
                                    $"{assembly.GetName().Name}:{type.FullName}::{name}");
                            }
                        }
                    }
                }
            }
            Assert.That(offenders, Is.Empty,
                "the Agent surface must carry no approval/review/settings member");
        }

        // The exposure half of the same clause: no public member of the
        // Agent-facing assemblies hands the Agent the objects the human-only
        // decisions run on — the approval service, the Core decision API, the
        // NeedsReview decision enum or the settings model. Approval detail
        // data (RunApprovalDetail) is deliberately not on this list: §H3
        // item 4 lets get_run_status report required approvals to the Agent.
        [Test]
        public void AgentAssemblies_ExposeNoDecisionOrSettingsTypes()
        {
            var forbiddenTypes = new HashSet<Type>
            {
                typeof(OpWeaveApprovalService),
                typeof(IRunCoordinator),
                typeof(RunCoordinator),
                typeof(ReviewDecision),
                typeof(OpWeaveSettings),
                typeof(OpWeaveSettingsDocument),
                typeof(OpWeaveSettingsProvider),
                typeof(OpWeaveSettingsLoader),
            };
            var assemblies = new List<Assembly> { typeof(AgentApiService).Assembly };
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(
                        loaded.GetName().Name, "OpWeave.Pipeline", StringComparison.Ordinal))
                {
                    assemblies.Add(loaded);
                }
            }

            var offenders = new List<string>();
            foreach (var assembly in assemblies)
            {
                foreach (var type in assembly.GetExportedTypes())
                {
                    const BindingFlags publicDeclared =
                        BindingFlags.Public | BindingFlags.Static |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly;
                    foreach (var member in type.GetMembers(publicDeclared))
                    {
                        foreach (var exposed in ExposedTypes(member))
                        {
                            if (ExposesForbidden(exposed, forbiddenTypes))
                            {
                                offenders.Add(
                                    $"{assembly.GetName().Name}:{type.FullName}::{member.Name}");
                            }
                        }
                    }
                }
            }
            Assert.That(offenders, Is.Empty,
                "the Agent surface must not expose the human-only decision or settings types");
        }

        // The §H2 adapter cannot smuggle the human-only services in either:
        // OpWeave.Approval* and OpWeaveSettings* live in OpWeave.UnityAdapter,
        // and the Pipeline asmdef must not reference it — the adapter's only
        // OpWeave input is OpWeave.AgentApi.
        [Test]
        public void PipelineAdapter_DoesNotReferenceTheUnityAdapterAssembly()
        {
            var asmdef = JObject.Parse(
                File.ReadAllText(PackageFile("Editor/Pipeline/OpWeave.Pipeline.asmdef")));
            var references = asmdef["references"]!.ToObject<string[]>()!;

            Assert.That(references, Does.Not.Contain("OpWeave.UnityAdapter"),
                "approval and settings services live in OpWeave.UnityAdapter; " +
                "the Agent-facing adapter must not reach them");
        }

        // The Core human-only API of T-W1.8-09 stays human-only: none of
        // IRunCoordinator's methods takes an ActorKind for a caller to pose
        // as the human (SPEC §H4 item 5, §E2). Accept's actor records which
        // side submitted the Run — it grants nothing.
        [Test]
        public void RunCoordinatorDecisionMethods_CarryNoActorParameter()
        {
            foreach (var name in new[] { "Approve", "Reject", "ResolveReview", "Cancel", "Get" })
            {
                var method = typeof(IRunCoordinator).GetMethod(name);
                Assert.That(method, Is.Not.Null, name + " is the Core coordinator API");
                foreach (var parameter in method!.GetParameters())
                {
                    Assert.That(
                        parameter.ParameterType, Is.Not.EqualTo(typeof(ActorKind)),
                        name + " must keep no actor parameter an Agent could set");
                }
            }
        }

        // SPEC §H1: the service's actor is fixed to Agent — a property, not
        // a parameter, so no caller can impersonate the human side.
        [Test]
        public void AgentApiService_ActorIsFixedToAgent()
        {
            var dir = Path.Combine(
                Path.GetTempPath(), "OpWeaveAgentRestriction", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var store = new FileRunStore(dir, "session-" + Guid.NewGuid().ToString("N"));
                var api = new AgentApiService(
                    store,
                    new AgentApiCursorCodec(new byte[32]),
                    static (runId, reason) => throw new System.InvalidOperationException(
                        "The surface fixtures carry no cancel seam."));
                Assert.That(api.Actor, Is.EqualTo(ActorKind.Agent));
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }

        // ── the settings file is never a writable destination ─────────────

        // SPEC §H4 item 5 / the ticket's pass condition: a write attempt on
        // ProjectSettings/OpWeaveSettings.json through a standard Operation
        // — as Agent, under the default allowedPaths and under one widened
        // to cover the file — fails the step with OW-PERM-002 before
        // step_started, and the file itself is unchanged.
        [Test]
        public void SettingsFileWrite_IsRefusedWithOwPerm002_AndFileUnchanged()
        {
            var fixture = NewFixture();
            var settingsFile = Path.Combine(
                RunnerHostFixture.ProjectRoot, "ProjectSettings", "OpWeaveSettings.json");
            var existedBefore = File.Exists(settingsFile);
            var bytesBefore = existedBefore ? File.ReadAllBytes(settingsFile) : null;

            // The real standard write Operation (unity.asset.create_folder,
            // SPEC §I.3) and a gated Operation whose path port is a plain
            // core.String — the spelling a custom Operation may declare and
            // the AssetPath codec would not narrow.
            var createFolder = new CreateFolderOperation();
            var gated = new RunnerHostFixture.GatedOperation(
                "test.restrict.settings_write", "path_writable", "path", fixture.Events,
                inputName: "path", inputType: "core.String");
            fixture.Register(createFolder, 'a');
            fixture.Register(gated, 'c');

            // Attempt 1 — the standard Operation under the default
            // allowedPaths (Assets/ only): outside the set, OW-PERM-002.
            var runId = RunWriteAttempt(fixture, createFolder.Descriptor.Id, 'a');
            AssertRefusedWithOwPerm002(runId);

            // Attempt 2 — the same Operation under a settings file widened
            // to cover ProjectSettings/: still OW-PERM-002.
            fixture.LoadSettings = () => OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"allowedPaths\":[\"Assets/\",\"ProjectSettings/\"]}");
            runId = RunWriteAttempt(fixture, createFolder.Descriptor.Id, 'a');
            AssertRefusedWithOwPerm002(runId);

            // Attempt 3 — the un-narrowed port type under the same widened
            // settings: the requirement layer refuses independently of the
            // codec, before Preflight or Execute.
            runId = RunWriteAttempt(fixture, gated.Descriptor.Id, 'c');
            AssertRefusedWithOwPerm002(runId);
            Assert.AreEqual(0, gated.PreflightCalls, "Preflight ran after a violation");
            Assert.AreEqual(0, gated.ExecuteCalls, "Execute ran after a violation");

            // The settings file itself is untouched — absent before stays
            // absent, present keeps its bytes.
            Assert.That(File.Exists(settingsFile), Is.EqualTo(existedBefore),
                "the write attempts must leave the settings file unchanged");
            if (bytesBefore is not null)
            {
                Assert.That(File.ReadAllBytes(settingsFile), Is.EqualTo(bytesBefore));
            }
        }

        // Drives one Agent Run of one path_writable step bound to the
        // settings file path and returns the failed Run's id.
        private string RunWriteAttempt(
            RunnerHostFixture fixture, string operationId, char hashSeed)
        {
            var steps = new[]
            {
                RunnerHostFixture.Step(0, "write", operationId, hashSeed,
                    inputs: new Dictionary<string, InputSource>
                    {
                        ["path"] = new Constant(
                            RunnerHostFixture.Json("\"" + SettingsRelativePath + "\"")),
                    },
                    idempotency: Idempotency.Idempotent),
            };
            var plan = RunnerHostFixture.Plan(WorkflowId, steps);
            var doc = RunnerHostFixture.Doc(
                WorkflowId,
                new[] { new NodeData { Id = "write", Operation = operationId, Version = 1 } },
                Array.Empty<ControlEdge>());

            var accepted = fixture.Host.AcceptRun(
                plan, doc, ActorKind.Agent, RunnerHostFixture.Options());
            Assert.IsTrue(accepted.Ok, "accept failed");
            var runId = accepted.Value!.RunId;
            for (var i = 0; i < 10 &&
                fixture.Host.Coordinator.StateOf(runId) != RunState.Failed; i++)
            {
                var tick = fixture.Host.Tick();
                Assert.IsTrue(tick.Ok, "Tick failed");
            }
            Assert.AreEqual(RunState.Failed, fixture.Host.Coordinator.StateOf(runId));
            return runId;
        }

        private void AssertRefusedWithOwPerm002(string runId)
        {
            Assert.That(
                RunnerHostFixture.JournalHas(_root, runId, "OW-PERM-002"),
                Is.True, "the StepFailed did not carry OW-PERM-002");
            Assert.IsFalse(
                RunnerHostFixture.JournalHasAll(
                    _root, runId, "\"type\":\"step_started\"", "\"step\":0,"),
                "the refused write must not reach StepStarted");
        }

        // The physical path of a file inside the installed
        // com.opweave.workflow package, resolved through the package manager
        // so the test never assumes the checkout layout.
        private static string PackageFile(string relativePath)
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            foreach (var package in packages)
            {
                if (string.Equals(package.name, "com.opweave.workflow", StringComparison.Ordinal))
                {
                    return Path.Combine(
                        package.resolvedPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                }
            }
            Assert.Fail("the com.opweave.workflow package is not registered in this project");
            return null!;
        }

        // The types a member exposes to a caller: a method's return type and
        // parameters, a constructor's parameters, a property's or field's or
        // event's type.
        private static IEnumerable<Type> ExposedTypes(MemberInfo member)
        {
            switch (member)
            {
                case MethodInfo method:
                    yield return method.ReturnType;
                    foreach (var parameter in method.GetParameters())
                    {
                        yield return parameter.ParameterType;
                    }
                    break;
                case ConstructorInfo constructor:
                    foreach (var parameter in constructor.GetParameters())
                    {
                        yield return parameter.ParameterType;
                    }
                    break;
                case PropertyInfo property:
                    yield return property.PropertyType;
                    break;
                case FieldInfo field:
                    yield return field.FieldType;
                    break;
                case EventInfo @event:
                    yield return @event.EventHandlerType!;
                    break;
            }
        }

        // Whether <paramref name="type"/> or any type argument it nests —
        // generic arguments, array and byref element types — is one of the
        // forbidden types.
        private static bool ExposesForbidden(Type type, ISet<Type> forbidden)
        {
            if (forbidden.Contains(type))
            {
                return true;
            }
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    if (ExposesForbidden(argument, forbidden))
                    {
                        return true;
                    }
                }
            }
            return type.HasElementType && ExposesForbidden(type.GetElementType()!, forbidden);
        }
    }
}
