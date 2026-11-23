#nullable enable

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// [Edit] tests that real <c>ProjectSettings/OpWeaveSettings.json</c> content
    /// drives the §H4 three-stage permission policy: fixture settings are loaded
    /// by <see cref="OpWeaveSettingsLoader"/>, bound by
    /// <see cref="SettingsPermissionPolicy"/> and evaluated as fixture workflows
    /// for each stage and actor (local:T-W3.4-01:policy-stages).
    /// </summary>
    internal sealed class SettingsPermissionPolicyTests
    {
        private static OpWeaveSettings LoadSettings(string json)
        {
            var result = OpWeaveSettingsLoader.Parse(json);
            Assert.IsTrue(result.Ok,
                $"Fixture settings must load: {string.Join(", ", result.Diagnostics.Select(d => d.Code))}");
            return result.Value!;
        }

        /// <summary>
        /// Settings JSON whose <c>extensions.whitelist.operations</c> holds
        /// exactly <paramref name="operation"/> — the identity tuple it
        /// carries is the one the fixture node under test reports.
        /// </summary>
        private static string WhitelistedSettings(OperationWhitelistEntry operation) => $@"{{
  ""version"": 2,
  ""extensions"": {{
    ""discoveryMode"": ""whitelist"",
    ""whitelist"": {{
      ""operations"": [
        {{ ""packageId"": ""{operation.PackageId}"", ""assemblyName"": ""{operation.AssemblyName}"",
          ""operationId"": ""{operation.OperationId}"", ""version"": {operation.Version} }}
      ]
    }}
  }}
}}";

        /// <summary>
        /// One fixture workflow node (local:T-W3.4-01:policy-stages): the
        /// Operation identity an <c>extensions.whitelist.operations</c> entry
        /// matches on (SPEC §C0.2), the §C4 effects and the two Descriptor
        /// facts <see cref="PermissionTarget"/> needs. Identity is a
        /// whitelist-stage fact the policy stage never sees (SPEC §C0.2, §X.9).
        /// </summary>
        private sealed record FixtureNode(
            string NodeId,
            OperationWhitelistEntry Operation,
            IReadOnlyList<string> Effects,
            bool Destructive,
            bool UndoIsFull)
        {
            /// <summary>The permission-check view of this node.</summary>
            internal PermissionTarget ToTarget() =>
                new PermissionTarget(NodeId, Effects, Destructive, UndoIsFull);
        }

        private static readonly OperationWhitelistEntry s_fixtureOperation =
            new OperationWhitelistEntry(
                "@project", "Example.Ops.Editor", "org.example.fixture_op", 1);

        private static FixtureNode Node(string nodeId, params string[] effects) =>
            new FixtureNode(
                nodeId, s_fixtureOperation, effects, Destructive: false, UndoIsFull: false);

        private static PolicyEvaluation Evaluate(
            IPermissionPolicy policy, params FixtureNode[] nodes) =>
            PermissionEvaluator.Evaluate(policy, nodes.Select(n => n.ToTarget()));

        // ── Stage 1: settings drive the Agent policy ─────────────────────────

        [Test]
        public void MissingPolicyKeysUseTheSpecExampleAgentDefaults()
        {
            var settings = LoadSettings("{\"version\":2}");
            var policy = SettingsPermissionPolicy.For(PolicyActor.Agent, settings);

            var evaluation = Evaluate(
                policy,
                Node("a", EffectNames.AssetMove),
                Node("b", EffectNames.AssetDelete),
                Node("c", EffectNames.CodeWrite),
                Node("d", EffectNames.PackageModify),
                Node("e", EffectNames.Read, EffectNames.CodeTemplate,
                    EffectNames.AssetCreate, EffectNames.Compile));

            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(evaluation.Approvals.Select(r => r.Effect),
                Is.EqualTo(new[]
                {
                    EffectNames.AssetMove, EffectNames.AssetDelete,
                    EffectNames.CodeWrite, EffectNames.PackageModify,
                }));
            Assert.That(evaluation.Approvals.Select(r => r.NodeId),
                Is.EqualTo(new[] { "a", "b", "c", "d" }));
        }

        [Test]
        public void AgentPolicyValuesFromSettingsAreAppliedPerEffect()
        {
            var settings = LoadSettings(@"{
  ""version"": 2,
  ""policy"": {
    ""agent"": { ""read"": ""deny"", ""asset.move"": ""allow"", ""code.template"": ""deny"" }
  }
}");
            var policy = SettingsPermissionPolicy.For(PolicyActor.Agent, settings);

            var evaluation = Evaluate(
                policy,
                Node("read-node", EffectNames.Read),
                Node("move-node", EffectNames.AssetMove),
                Node("template-node", EffectNames.CodeTemplate));

            // read and code.template denied → one OW-PERM-001 each; asset.move allowed.
            Assert.That(evaluation.Approvals, Is.Empty);
            Assert.That(evaluation.Diagnostics.Select(d => (d.NodeId, d.Code)),
                Is.EqualTo(new[]
                {
                    ("read-node", ErrorCodes.OW_PERM_001),
                    ("template-node", ErrorCodes.OW_PERM_001),
                }));
        }

        [Test]
        public void HumanPolicyUsesHumanSettingsNotAgentSettings()
        {
            var settings = LoadSettings(@"{
  ""version"": 2,
  ""policy"": {
    ""agent"": { ""read"": ""deny"" },
    ""human"": { ""asset.move"": ""deny"", ""code.template"": ""ask"" }
  }
}");

            var human = Evaluate(
                SettingsPermissionPolicy.For(PolicyActor.Human, settings),
                Node("read-node", EffectNames.Read),
                Node("move-node", EffectNames.AssetMove),
                Node("template-node", EffectNames.CodeTemplate));

            // Human read stays allow (only the agent map denied it);
            // asset.move denied, code.template ask.
            Assert.That(human.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("template-node", EffectNames.CodeTemplate) }));
            Assert.That(human.Diagnostics.Select(d => d.NodeId),
                Is.EqualTo(new[] { "move-node" }));
        }

        // ── Stage 3 and the destructive floor through real settings ─────────

        // SPEC §H4 test 3 / §D6 test 3 via the real settings source.
        [Test]
        public void AgentCodeWriteAllowInSettingsStillRequiresApproval()
        {
            var settings = LoadSettings(
                "{\"version\":2,\"policy\":{\"agent\":{\"code.write\":\"allow\"}}}");
            var policy = SettingsPermissionPolicy.For(PolicyActor.Agent, settings);

            var evaluation = Evaluate(policy, Node("write-node", EffectNames.CodeWrite));

            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(evaluation.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("write-node", EffectNames.CodeWrite) }));
        }

        [Test]
        public void DestructiveRequiresAskSettingControlsTheFloor()
        {
            var destructive = Node("delete-node", EffectNames.AssetModify)
                with { Destructive = true };
            var destructiveFullUndo = destructive
                with { NodeId = "undo-node", UndoIsFull = true };

            var strict = SettingsPermissionPolicy.For(
                PolicyActor.Agent, LoadSettings("{\"version\":2}"));
            var relaxed = SettingsPermissionPolicy.For(
                PolicyActor.Agent,
                LoadSettings("{\"version\":2,\"destructiveRequiresAsk\":false}"));

            Assert.IsTrue(strict.DestructiveRequiresAsk);
            var strictEval = Evaluate(strict, destructive, destructiveFullUndo);
            Assert.That(strictEval.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("delete-node", EffectNames.AssetModify) }));

            Assert.IsFalse(relaxed.DestructiveRequiresAsk);
            Assert.IsEmpty(Evaluate(relaxed, destructive, destructiveFullUndo).Approvals);
        }

        // ── whitelist and ask are separate stages through real settings ─────

        // SPEC §H4 test 4 / §C0.2 / §X.9: an Operation whose identity matches
        // the settings whitelist still needs its ask approvals; the policy
        // stage cannot see whitelist membership.
        [Test]
        public void WhitelistedOperationInSettingsStillRequiresAskApprovals()
        {
            var node = new FixtureNode(
                "pose-node",
                new OperationWhitelistEntry(
                    "@project", "Example.Ops.Editor", "org.example.animation.make_pose", 1),
                new[] { EffectNames.AssetDelete },
                Destructive: false,
                UndoIsFull: false);
            var settings = LoadSettings(WhitelistedSettings(node.Operation));

            // The loaded whitelist entry matches the node's Operation identity
            // field-by-field.
            var entry = settings.WhitelistedOperations.Single();
            Assert.That(entry.PackageId, Is.EqualTo(node.Operation.PackageId));
            Assert.That(entry.AssemblyName, Is.EqualTo(node.Operation.AssemblyName));
            Assert.That(entry.OperationId, Is.EqualTo(node.Operation.OperationId));
            Assert.That(entry.Version, Is.EqualTo(node.Operation.Version));

            var evaluation = Evaluate(
                SettingsPermissionPolicy.For(PolicyActor.Agent, settings), node);

            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(evaluation.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("pose-node", EffectNames.AssetDelete) }));

            // Negative control: the same node under a whitelist that does not
            // contain its identity yields the same approvals — whitelist
            // membership never reaches the policy stage.
            var other = LoadSettings(WhitelistedSettings(
                new OperationWhitelistEntry(
                    "@project", "Example.Ops.Editor", "org.example.other_op", 1)));
            Assert.That(other.WhitelistedOperations.Single(),
                Is.Not.EqualTo(node.Operation));
            Assert.That(
                Evaluate(SettingsPermissionPolicy.For(PolicyActor.Agent, other), node)
                    .Approvals,
                Is.EqualTo(evaluation.Approvals));
        }

        // ── deny produces OW-PERM-001 through real settings ─────────────────

        [Test]
        public void DeniedEffectFromSettingsYieldsOwPerm001()
        {
            var settings = LoadSettings(
                "{\"version\":2,\"policy\":{\"agent\":{\"asset.delete\":\"deny\"}}}");
            var policy = SettingsPermissionPolicy.For(PolicyActor.Agent, settings);

            var evaluation = Evaluate(policy, Node("delete-node", EffectNames.AssetDelete));

            var diagnostic = evaluation.Diagnostics.Single();
            Assert.That(diagnostic.Code, Is.EqualTo(ErrorCodes.OW_PERM_001));
            Assert.That(diagnostic.Severity, Is.EqualTo(Severity.Error));
            Assert.That(diagnostic.NodeId, Is.EqualTo("delete-node"));
            Assert.That(diagnostic.Hint, Is.EqualTo(EffectNames.AssetDelete));
        }
    }
}
