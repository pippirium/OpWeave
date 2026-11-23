#nullable enable

using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// [Edit] tests for SPEC §0.6 SG-18 / §H4.1: a persisted Agent
    /// <c>policy.agent["code.write"]</c> of <c>"allow"</c> loads as
    /// <see cref="PolicyLevel.Ask"/> with exactly one OW-CFG-005 warning at
    /// <c>$.policy.agent["code.write"]</c>; <c>ask</c>/<c>deny</c> keep their
    /// meaning without a warning, invalid values stay OW-CFG-002, and a
    /// <c>deny</c> never enters an approval queue
    /// (local:T-W2.1-06:codewrite-allow-warning).
    /// </summary>
    /// <remarks>
    /// Assertions strengthened for local:T-W2.1-09:codewrite-allow-reverify:
    /// every fixture asserts the complete Diagnostics array including code,
    /// severity and JsonPath, plus the effective policy, and a persisted
    /// <c>"ask"</c> is shown to keep its meaning in the approval queue.
    /// </remarks>
    internal sealed class CodeWriteAllowWarningTests
    {
        private const string CodeWritePath = "$.policy.agent[\"code.write\"]";

        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(
                Path.GetTempPath(), "CodeWriteAllowWarningTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }

        private Result<OpWeaveSettings> LoadFixture(string content)
        {
            var path = Path.Combine(_dir, "OpWeaveSettings.json");
            File.WriteAllText(path, content);
            return OpWeaveSettingsLoader.Load(path);
        }

        private static string AgentCodeWriteJson(string value) =>
            "{\"version\":2,\"policy\":{\"agent\":{\"code.write\":" + value + "}}}";

        private static PolicyEvaluation EvaluateAgentCodeWrite(OpWeaveSettings settings) =>
            PermissionEvaluator.Evaluate(
                SettingsPermissionPolicy.For(PolicyActor.Agent, settings),
                new[]
                {
                    new PermissionTarget(
                        "write-node", new[] { EffectNames.CodeWrite },
                        Destructive: false, UndoIsFull: false),
                });

        // ── allow → exactly one OW-CFG-005 Warning, effective ask ────────────

        [Test]
        public void AllowEmitsExactlyOneOwCfg005WarningAtTheCodeWritePath()
        {
            var result = LoadFixture(AgentCodeWriteJson("\"allow\""));

            Assert.IsTrue(result.Ok);
            // The complete diagnostics array is the single SG-18 warning.
            var warning = result.Diagnostics.Single();
            Assert.That(warning.Code, Is.EqualTo(ErrorCodes.OW_CFG_005));
            Assert.That(warning.Severity, Is.EqualTo(Severity.Warning));
            Assert.That(warning.Retryable, Is.False);
            Assert.That(warning.JsonPath, Is.EqualTo(CodeWritePath));
            Assert.That(result.Value!.AgentPolicy["code.write"], Is.EqualTo(PolicyLevel.Ask));
        }

        [Test]
        public void AllowWarningAppearsOnceAlongsideOtherDiagnostics()
        {
            var result = LoadFixture(@"{
  ""version"": 2,
  ""policy"": { ""agent"": { ""read"": ""deny"", ""code.write"": ""allow"" } },
  ""unknownKey"": 1
}");

            Assert.IsTrue(result.Ok);
            Assert.That(
                result.Diagnostics.Select(d => (d.Code, d.Severity, d.JsonPath)),
                Is.EqualTo(new[]
                {
                    (ErrorCodes.OW_CFG_005, Severity.Warning, CodeWritePath),
                    (ErrorCodes.OW_CFG_003, Severity.Warning, "$.unknownKey"),
                }));
            Assert.That(result.Value!.AgentPolicy["code.write"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(result.Value!.AgentPolicy["read"], Is.EqualTo(PolicyLevel.Deny));
        }

        [Test]
        public void AllowInV1FileWarnsOnceAlongsideMigrationWarning()
        {
            var result = LoadFixture(
                "{\"policy\":{\"agent\":{\"code.write\":\"allow\"}}}");

            Assert.IsTrue(result.Ok);
            Assert.That(
                result.Diagnostics.Select(d => (d.Code, d.Severity, d.JsonPath)),
                Is.EqualTo(new (string, Severity, string?)[]
                {
                    (ErrorCodes.OW_CFG_005, Severity.Warning, CodeWritePath),
                    (ErrorCodes.OW_CFG_004, Severity.Warning, null),
                }));
            var settings = result.Value!;
            Assert.That(settings.MigratedFromV1, Is.True);
            Assert.That(settings.AgentPolicy["code.write"], Is.EqualTo(PolicyLevel.Ask));
        }

        [Test]
        public void AllowCorrectionActsAsAskInPermissionEvaluation()
        {
            var result = LoadFixture(AgentCodeWriteJson("\"allow\""));
            Assert.IsTrue(result.Ok);

            var evaluation = EvaluateAgentCodeWrite(result.Value!);

            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(
                evaluation.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("write-node", EffectNames.CodeWrite) }));
        }

        // ── ask / deny → no warning, meaning retained ────────────────────────

        [TestCase("ask", PolicyLevel.Ask)]
        [TestCase("deny", PolicyLevel.Deny)]
        public void AskAndDenyEmitNoWarningAndRetainMeaning(string value, PolicyLevel expected)
        {
            var result = LoadFixture(AgentCodeWriteJson("\"" + value + "\""));

            Assert.IsTrue(result.Ok);
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(result.Value!.AgentPolicy["code.write"], Is.EqualTo(expected));
        }

        [Test]
        public void AskEntersAnApprovalQueue()
        {
            var result = LoadFixture(AgentCodeWriteJson("\"ask\""));
            Assert.IsTrue(result.Ok);
            var policy = SettingsPermissionPolicy.For(PolicyActor.Agent, result.Value!);

            // A persisted "ask" keeps its meaning: the node waits for human
            // approval instead of running or being denied (SPEC §H4).
            Assert.That(
                PermissionEvaluator.EffectiveLevel(
                    policy, EffectNames.CodeWrite, destructive: false, undoIsFull: false),
                Is.EqualTo(PermissionLevel.Ask));

            var evaluation = EvaluateAgentCodeWrite(result.Value!);
            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(
                evaluation.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("write-node", EffectNames.CodeWrite) }));
        }

        [Test]
        public void DenyNeverEntersAnApprovalQueue()
        {
            var result = LoadFixture(AgentCodeWriteJson("\"deny\""));
            Assert.IsTrue(result.Ok);
            var policy = SettingsPermissionPolicy.For(PolicyActor.Agent, result.Value!);

            // deny stays deny; it is never loosened to ask (SPEC §0.6 SG-18).
            Assert.That(
                PermissionEvaluator.EffectiveLevel(
                    policy, EffectNames.CodeWrite, destructive: false, undoIsFull: false),
                Is.EqualTo(PermissionLevel.Deny));

            var evaluation = EvaluateAgentCodeWrite(result.Value!);
            Assert.IsTrue(evaluation.HasDenials);
            Assert.That(evaluation.Approvals, Is.Empty);
            var diagnostic = evaluation.Diagnostics.Single();
            Assert.That(diagnostic.Code, Is.EqualTo(ErrorCodes.OW_PERM_001));
            Assert.That(diagnostic.Severity, Is.EqualTo(Severity.Error));
            Assert.That(diagnostic.NodeId, Is.EqualTo("write-node"));
        }

        // ── invalid values → OW-CFG-002 Error, no OW-CFG-005 ────────────────

        [TestCase("\"maybe\"")]
        [TestCase("\"ALLOW\"")]
        [TestCase("1")]
        [TestCase("true")]
        [TestCase("null")]
        [TestCase("[]")]
        public void InvalidCodeWriteValuesYieldOwCfg002NotOwCfg005(string value)
        {
            var result = LoadFixture(AgentCodeWriteJson(value));

            Assert.IsFalse(result.Ok);
            // A settings error produces no effective policy (SPEC §H4.1 fail-closed).
            Assert.IsNull(result.Value);
            var error = result.Diagnostics.Single();
            Assert.That(error.Code, Is.EqualTo(ErrorCodes.OW_CFG_002));
            Assert.That(error.Severity, Is.EqualTo(Severity.Error));
            Assert.That(error.JsonPath, Is.EqualTo(CodeWritePath));
        }
    }
}
