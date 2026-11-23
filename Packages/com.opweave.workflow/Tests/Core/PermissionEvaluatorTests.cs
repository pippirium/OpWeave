#nullable enable

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §H4 three-stage permission policy evaluated by
    /// <see cref="PermissionEvaluator"/> over <see cref="IPermissionPolicy"/>
    /// fixtures ("fixture settings"): stage 1 per-effect levels, stage 2
    /// <c>code.template</c>, the stage 3 <c>code.write</c> floor, the
    /// <c>destructiveRequiresAsk</c> floor, the whitelist/ask stage separation
    /// and OW-PERM-001 on deny (local:T-W3.4-01:policy-stages;
    /// SPEC §H4 tests 3–4, §C0.2, §C4 test 3, §D6 tests 1 and 3).
    /// </summary>
    internal sealed class PermissionEvaluatorTests
    {
        /// <summary>A policy fixture: a settings map handed to the evaluator directly.</summary>
        private sealed class FixturePolicy : IPermissionPolicy
        {
            private readonly IReadOnlyDictionary<string, PermissionLevel> _levels;

            internal FixturePolicy(
                PolicyActor actor,
                IReadOnlyDictionary<string, PermissionLevel> levels,
                bool destructiveRequiresAsk = true)
            {
                Actor = actor;
                _levels = levels;
                DestructiveRequiresAsk = destructiveRequiresAsk;
            }

            public PolicyActor Actor { get; }
            public bool DestructiveRequiresAsk { get; }

            public PermissionLevel LevelFor(string effect) =>
                _levels.TryGetValue(effect, out var level) ? level : PermissionLevel.Deny;
        }

        // §H4 example defaults: Agent asks on move/delete/code.write/package.
        private static FixturePolicy AgentDefaults(bool destructiveRequiresAsk = true) =>
            new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.Read] = PermissionLevel.Allow,
                [EffectNames.SceneLoad] = PermissionLevel.Allow,
                [EffectNames.SceneModify] = PermissionLevel.Allow,
                [EffectNames.SceneSave] = PermissionLevel.Allow,
                [EffectNames.AssetCreate] = PermissionLevel.Allow,
                [EffectNames.AssetModify] = PermissionLevel.Allow,
                [EffectNames.AssetMove] = PermissionLevel.Ask,
                [EffectNames.AssetDelete] = PermissionLevel.Ask,
                [EffectNames.CodeTemplate] = PermissionLevel.Allow,
                [EffectNames.CodeWrite] = PermissionLevel.Ask,
                [EffectNames.Compile] = PermissionLevel.Allow,
                [EffectNames.PackageModify] = PermissionLevel.Ask,
            }, destructiveRequiresAsk);

        // §H4 example defaults for humans: allow except delete/code.write/package.
        private static FixturePolicy HumanDefaults(bool destructiveRequiresAsk = true)
        {
            var levels = new Dictionary<string, PermissionLevel>();
            foreach (var effect in EffectNames.All)
            {
                levels[effect] = PermissionLevel.Allow;
            }
            levels[EffectNames.AssetDelete] = PermissionLevel.Ask;
            levels[EffectNames.CodeWrite] = PermissionLevel.Ask;
            levels[EffectNames.PackageModify] = PermissionLevel.Ask;
            return new FixturePolicy(PolicyActor.Human, levels, destructiveRequiresAsk);
        }

        /// <summary>
        /// The four-field Operation identity a settings whitelist entry
        /// matches on (SPEC §C0.2). Core never sees the settings model, so the
        /// whitelist stage is stubbed with these tuples; the evaluator still
        /// receives no whitelist input.
        /// </summary>
        private sealed record OperationIdentity(
            string PackageId, string AssemblyName, string OperationId, long Version);

        /// <summary>
        /// One fixture workflow node (local:T-W3.4-01:policy-stages): the
        /// Operation identity, the §C4 effects and the two Descriptor facts
        /// <see cref="PermissionTarget"/> needs. Identity is a whitelist-stage
        /// fact the policy stage never sees (SPEC §C0.2, §X.9).
        /// </summary>
        private sealed record FixtureNode(
            string NodeId,
            OperationIdentity Operation,
            IReadOnlyList<string> Effects,
            bool Destructive,
            bool UndoIsFull)
        {
            /// <summary>The permission-check view of this node.</summary>
            internal PermissionTarget ToTarget() =>
                new PermissionTarget(NodeId, Effects, Destructive, UndoIsFull);
        }

        private static readonly OperationIdentity s_fixtureOperation =
            new OperationIdentity(
                "@project", "Example.Ops.Editor", "org.example.fixture_op", 1);

        private static FixtureNode Node(string nodeId, params string[] effects) =>
            new FixtureNode(
                nodeId, s_fixtureOperation, effects, Destructive: false, UndoIsFull: false);

        private static PolicyEvaluation Evaluate(
            IPermissionPolicy policy, params FixtureNode[] nodes) =>
            PermissionEvaluator.Evaluate(policy, nodes.Select(n => n.ToTarget()));

        // ── Stage 1: per-effect policy of the actor ──────────────────────────

        [Test]
        public void AgentDefaultPolicyAsksOnMoveDeleteCodeWriteAndPackage()
        {
            var evaluation = Evaluate(
                AgentDefaults(),
                Node("read-node", EffectNames.Read),
                Node("move-node", EffectNames.AssetMove),
                Node("delete-node", EffectNames.AssetDelete),
                Node("write-node", EffectNames.CodeWrite),
                Node("package-node", EffectNames.PackageModify),
                Node("scene-node", EffectNames.SceneLoad, EffectNames.SceneModify,
                    EffectNames.SceneSave, EffectNames.Compile),
                Node("asset-node", EffectNames.AssetCreate, EffectNames.AssetModify),
                Node("template-node", EffectNames.CodeTemplate));

            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(
                evaluation.Approvals,
                Is.EqualTo(new[]
                {
                    new ApprovalRequest("move-node", EffectNames.AssetMove),
                    new ApprovalRequest("delete-node", EffectNames.AssetDelete),
                    new ApprovalRequest("write-node", EffectNames.CodeWrite),
                    new ApprovalRequest("package-node", EffectNames.PackageModify),
                }));
        }

        [Test]
        public void HumanDefaultPolicyAsksOnlyOnDeleteCodeWriteAndPackage()
        {
            var evaluation = Evaluate(
                HumanDefaults(),
                Node("move-node", EffectNames.AssetMove),
                Node("delete-node", EffectNames.AssetDelete),
                Node("write-node", EffectNames.CodeWrite),
                Node("template-node", EffectNames.CodeTemplate),
                Node("package-node", EffectNames.PackageModify),
                Node("rest-node", EffectNames.Read, EffectNames.SceneModify,
                    EffectNames.AssetCreate, EffectNames.AssetModify, EffectNames.Compile));

            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(
                evaluation.Approvals,
                Is.EqualTo(new[]
                {
                    new ApprovalRequest("delete-node", EffectNames.AssetDelete),
                    new ApprovalRequest("write-node", EffectNames.CodeWrite),
                    new ApprovalRequest("package-node", EffectNames.PackageModify),
                }));
        }

        [Test]
        public void SettingsLevelsDriveEveryStageOneEffect()
        {
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.Read] = PermissionLevel.Deny,
                [EffectNames.SceneLoad] = PermissionLevel.Ask,
                [EffectNames.AssetCreate] = PermissionLevel.Allow,
            });

            var evaluation = Evaluate(
                policy,
                Node("a", EffectNames.Read),
                Node("b", EffectNames.SceneLoad),
                Node("c", EffectNames.AssetCreate));

            Assert.IsTrue(evaluation.HasDenials);
            Assert.That(evaluation.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("b", EffectNames.SceneLoad) }));
            var diagnostics = evaluation.Diagnostics;
            Assert.That(diagnostics.Count, Is.EqualTo(1));
            Assert.That(diagnostics[0].Code, Is.EqualTo(ErrorCodes.OW_PERM_001));
            Assert.That(diagnostics[0].Severity, Is.EqualTo(Severity.Error));
            Assert.That(diagnostics[0].NodeId, Is.EqualTo("a"));
        }

        // ── Stage 2: code.template follows its own setting ───────────────────

        [Test]
        public void CodeTemplateFollowsItsOwnSettingForBothActors()
        {
            foreach (var actor in new[] { PolicyActor.Agent, PolicyActor.Human })
            {
                foreach (var level in new[]
                         { PermissionLevel.Allow, PermissionLevel.Ask, PermissionLevel.Deny })
                {
                    var policy = new FixturePolicy(actor, new Dictionary<string, PermissionLevel>
                    {
                        [EffectNames.CodeTemplate] = level,
                    });

                    var effective = PermissionEvaluator.EffectiveLevel(
                        policy, EffectNames.CodeTemplate, destructive: false, undoIsFull: false);

                    Assert.That(effective, Is.EqualTo(level), $"{actor} code.template {level}");
                }
            }
        }

        // ── Stage 3: code.write is never below ask ───────────────────────────

        // SPEC §H4 test 3 / §C4 test 3 / §D6 test 3.
        [Test]
        public void AgentCodeWriteAllowStillAsks()
        {
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.CodeWrite] = PermissionLevel.Allow,
            });

            var evaluation = Evaluate(policy, Node("write-node", EffectNames.CodeWrite));

            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(evaluation.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("write-node", EffectNames.CodeWrite) }));
        }

        [Test]
        public void CodeWriteDenyStaysDeniedAndAskStaysAsk()
        {
            foreach (var actor in new[] { PolicyActor.Agent, PolicyActor.Human })
            {
                var denyPolicy = new FixturePolicy(actor, new Dictionary<string, PermissionLevel>
                {
                    [EffectNames.CodeWrite] = PermissionLevel.Deny,
                });
                var askPolicy = new FixturePolicy(actor, new Dictionary<string, PermissionLevel>
                {
                    [EffectNames.CodeWrite] = PermissionLevel.Ask,
                });

                Assert.That(
                    PermissionEvaluator.EffectiveLevel(
                        denyPolicy, EffectNames.CodeWrite, destructive: false, undoIsFull: false),
                    Is.EqualTo(PermissionLevel.Deny));
                Assert.That(
                    PermissionEvaluator.EffectiveLevel(
                        askPolicy, EffectNames.CodeWrite, destructive: false, undoIsFull: false),
                    Is.EqualTo(PermissionLevel.Ask));
            }
        }

        // Local reading documented on PermissionEvaluator: §H4 stage 3 forbids
        // "allow" for code.write outright, so the floor holds for humans too
        // even though the settings file can never produce it (OW-CFG-002).
        [Test]
        public void HumanCodeWriteAllowStillAsks()
        {
            var policy = new FixturePolicy(PolicyActor.Human, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.CodeWrite] = PermissionLevel.Allow,
            });

            Assert.That(
                PermissionEvaluator.EffectiveLevel(
                    policy, EffectNames.CodeWrite, destructive: false, undoIsFull: false),
                Is.EqualTo(PermissionLevel.Ask));
        }

        // ── destructiveRequiresAsk floor (SPEC §H4 item 2) ──────────────────

        [Test]
        public void DestructiveWithoutFullUndoIsAtLeastAsk()
        {
            var policy = AgentDefaults();
            var destructive = Node("destructive-node", EffectNames.AssetModify)
                with { Destructive = true };
            var destructiveFullUndo = destructive with { UndoIsFull = true };
            var plain = Node("plain-node", EffectNames.AssetModify);

            Assert.That(
                PermissionEvaluator.EffectiveLevel(
                    policy, EffectNames.AssetModify,
                    destructive.Destructive, destructive.UndoIsFull),
                Is.EqualTo(PermissionLevel.Ask));
            Assert.That(
                PermissionEvaluator.EffectiveLevel(
                    policy, EffectNames.AssetModify,
                    destructiveFullUndo.Destructive, destructiveFullUndo.UndoIsFull),
                Is.EqualTo(PermissionLevel.Allow));
            Assert.That(
                PermissionEvaluator.EffectiveLevel(
                    policy, EffectNames.AssetModify,
                    plain.Destructive, plain.UndoIsFull),
                Is.EqualTo(PermissionLevel.Allow));
        }

        [Test]
        public void DestructiveFloorRespectsTheSettingAndNeverWeakensDeny()
        {
            var relaxed = AgentDefaults(destructiveRequiresAsk: false);
            var destructive = Node("destructive-node", EffectNames.AssetModify)
                with { Destructive = true };
            Assert.That(
                PermissionEvaluator.EffectiveLevel(
                    relaxed, EffectNames.AssetModify,
                    destructive.Destructive, destructive.UndoIsFull),
                Is.EqualTo(PermissionLevel.Allow));

            var denied = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.AssetModify] = PermissionLevel.Deny,
            });
            Assert.That(
                PermissionEvaluator.EffectiveLevel(
                    denied, EffectNames.AssetModify,
                    destructive.Destructive, destructive.UndoIsFull),
                Is.EqualTo(PermissionLevel.Deny));
        }

        // ── Deny produces OW-PERM-001; ask still lands on approvals ─────────

        [Test]
        public void DeniedEffectYieldsOwPerm001AndApprovalsAreStillListed()
        {
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.Read] = PermissionLevel.Deny,
                [EffectNames.AssetDelete] = PermissionLevel.Ask,
                [EffectNames.SceneLoad] = PermissionLevel.Allow,
            });

            var evaluation = Evaluate(
                policy,
                Node("mixed-node", EffectNames.SceneLoad, EffectNames.AssetDelete, EffectNames.Read));

            Assert.IsTrue(evaluation.HasDenials);
            Assert.That(evaluation.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("mixed-node", EffectNames.AssetDelete) }));
            var diagnostic = evaluation.Diagnostics.Single();
            Assert.That(diagnostic.Code, Is.EqualTo(ErrorCodes.OW_PERM_001));
            Assert.That(diagnostic.Severity, Is.EqualTo(Severity.Error));
            Assert.That(diagnostic.Retryable, Is.False);
            Assert.That(diagnostic.NodeId, Is.EqualTo("mixed-node"));
            Assert.That(diagnostic.Hint, Is.EqualTo(EffectNames.Read));
        }

        [Test]
        public void EveryDeniedEffectYieldsItsOwnOwPerm001()
        {
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                [EffectNames.AssetMove] = PermissionLevel.Deny,
                [EffectNames.AssetDelete] = PermissionLevel.Deny,
            });

            var evaluation = Evaluate(
                policy,
                Node("first", EffectNames.AssetMove),
                Node("second", EffectNames.AssetDelete));

            Assert.That(
                evaluation.Diagnostics.Select(d => (d.NodeId, d.Code)),
                Is.EqualTo(new[]
                {
                    ("first", ErrorCodes.OW_PERM_001),
                    ("second", ErrorCodes.OW_PERM_001),
                }));
        }

        // ── whitelist and ask are separate stages (SPEC §C0.2, §X.9, §H4 test 4) ─

        // SPEC §H4 test 4: an Operation that IS whitelisted still requires the
        // ask approvals of its effects — the evaluator has no whitelist input.
        [Test]
        public void WhitelistedOperationStillRequiresAskApprovals()
        {
            var node = new FixtureNode(
                "pose-node",
                new OperationIdentity(
                    "@project", "Example.Ops.Editor", "org.example.animation.make_pose", 1),
                new[] { EffectNames.AssetDelete },
                Destructive: true,
                UndoIsFull: false);
            var whitelistedOperations = new HashSet<OperationIdentity> { node.Operation };
            var policy = AgentDefaults();

            // The stub whitelist holds exactly this node's Operation identity;
            // the §C0.2 match is the field-by-field tuple.
            var entry = whitelistedOperations.Single();
            Assert.That(entry.PackageId, Is.EqualTo(node.Operation.PackageId));
            Assert.That(entry.AssemblyName, Is.EqualTo(node.Operation.AssemblyName));
            Assert.That(entry.OperationId, Is.EqualTo(node.Operation.OperationId));
            Assert.That(entry.Version, Is.EqualTo(node.Operation.Version));

            var evaluation = Evaluate(policy, node);

            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(evaluation.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("pose-node", EffectNames.AssetDelete) }));

            // Negative control: the same node with a non-matching whitelist
            // yields the same approvals — membership never reaches the
            // policy stage.
            var otherOperations = new HashSet<OperationIdentity>
            {
                new OperationIdentity(
                    "@project", "Example.Ops.Editor", "org.example.other_op", 1),
            };
            Assert.IsFalse(otherOperations.Contains(node.Operation));
            Assert.That(Evaluate(policy, node).Approvals,
                Is.EqualTo(evaluation.Approvals));
        }

        // ── Boundaries ───────────────────────────────────────────────────────

        [Test]
        public void UnknownEffectIsDeniedWithOwPerm001()
        {
            var policy = new FixturePolicy(PolicyActor.Agent, new Dictionary<string, PermissionLevel>
            {
                // Even a permissive fixture cannot make an unknown effect run.
                ["unknown.effect"] = PermissionLevel.Allow,
            });

            var evaluation = Evaluate(policy, Node("n", "unknown.effect"));

            Assert.IsTrue(evaluation.HasDenials);
            Assert.That(evaluation.Approvals, Is.Empty);
            Assert.That(evaluation.Diagnostics.Single().Code, Is.EqualTo(ErrorCodes.OW_PERM_001));
        }

        [Test]
        public void DuplicateEffectsAreReportedOnceAndEmptyEffectsNeedNothing()
        {
            var evaluation = Evaluate(
                AgentDefaults(),
                Node("dup", EffectNames.AssetDelete, EffectNames.AssetDelete),
                Node("empty"));

            Assert.IsFalse(evaluation.HasDenials);
            Assert.That(evaluation.Approvals,
                Is.EqualTo(new[] { new ApprovalRequest("dup", EffectNames.AssetDelete) }));
            Assert.That(evaluation.Decisions.Select(d => d.NodeId),
                Is.EqualTo(new[] { "dup", "empty" }));
        }
    }
}
