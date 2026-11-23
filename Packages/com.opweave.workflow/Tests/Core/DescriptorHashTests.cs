#nullable enable

using System;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for <see cref="DescriptorHash"/> (T-W1.5-02, SPEC §C3
    /// rule 6, §0.6 SG-12): golden descriptorHash vectors freeze the serialized
    /// shape of the two SPEC example descriptors, the hash is deterministic
    /// across equal content, changes with any definition field, and keeps an
    /// absent <see cref="JsonValue"/> member byte-distinct from an explicit
    /// JSON <c>null</c> (SPEC §A0).
    /// </summary>
    internal sealed class DescriptorHashTests
    {
        // Golden vector: canonical JSON of the §C2 example descriptor.
        private const string C2Canonical =
            @"{""category"":"""",""controlOutputs"":[""next""],""defaultRetry"":{""initialBackoffMs"":500,""maxAttempts"":1,""maxBackoffMs"":10000,""multiplier"":2e0},""defaultTimeoutSeconds"":30,""description"":"""",""destructive"":false,""displayName"":"""",""examples"":[],""executionMode"":""immediate"",""id"":""unity.gameobject.create"",""idempotency"":""reconcilable"",""inputs"":[{""constraints"":null,""description"":"""",""name"":""scene"",""nullable"":false,""required"":false,""type"":""unity.SceneRef""},{""constraints"":null,""description"":"""",""name"":""parent"",""nullable"":false,""required"":false,""type"":""unity.GameObjectRef""},{""constraints"":{""constantOnly"":false,""enum"":null,""max"":null,""maxLength"":256,""min"":null,""pattern"":null},""description"":"""",""name"":""name"",""nullable"":false,""required"":true,""type"":""core.String""},{""constraints"":{""constantOnly"":false,""enum"":[""none"",""cube"",""sphere"",""capsule"",""cylinder"",""plane"",""quad""],""max"":null,""maxLength"":null,""min"":null,""pattern"":null},""default"":""none"",""description"":"""",""name"":""primitive"",""nullable"":false,""required"":false,""type"":""core.String""}],""keywords"":[],""kind"":""native"",""localized"":{""ja"":{""displayName"":""GameObject を作成"",""keywords"":[""作成"",""オブジェクト""],""summary"":""読み込み済みの Scene に空またはプリミティブの GameObject を作る。""}},""outputRefinement"":null,""outputs"":[{""constraints"":null,""description"":"""",""name"":""gameObject"",""nullable"":false,""required"":false,""type"":""unity.GameObjectRef""}],""requirements"":[{""kind"":""edit_mode"",""port"":null},{""kind"":""scene_loaded"",""port"":""scene""}],""sideEffects"":[""scene.modify""],""summary"":""Create an empty or primitive GameObject in a loaded scene."",""tags"":[],""undo"":""full"",""version"":1}";

        private const string C2Hash =
            "e38da86571a78cceb296cf22702ac94511d0b44af951c181e4a9792aef09b7b8";

        // Golden vector: canonical JSON of the §M.4 example descriptor.
        private const string M4Canonical =
            @"{""category"":""Transform"",""controlOutputs"":[""next""],""defaultRetry"":{""initialBackoffMs"":500,""maxAttempts"":1,""maxBackoffMs"":10000,""multiplier"":2e0},""defaultTimeoutSeconds"":30,""description"":"""",""destructive"":false,""displayName"":""Reset Transform"",""examples"":[{""title"":""Reset after create"",""workflowJson"":{""controlEdges"":[{""from"":""start"",""to"":""create""},{""from"":""create"",""to"":""reset""}],""dataEdges"":[{""from"":{""node"":""create"",""port"":""gameObject""},""to"":{""node"":""reset"",""port"":""target""}}],""id"":""example.reset-transform"",""nodes"":[{""id"":""start"",""operation"":""core.start"",""version"":1},{""id"":""create"",""operation"":""unity.gameobject.create"",""parameters"":{""name"":""Player""},""version"":1},{""id"":""reset"",""operation"":""proj.transform.reset"",""version"":1}],""outputs"":[{""from"":{""node"":""reset"",""port"":""gameObject""},""id"":""gameObject""}],""schemaVersion"":""1.0""}}],""executionMode"":""immediate"",""id"":""proj.transform.reset"",""idempotency"":""idempotent"",""inputs"":[{""constraints"":null,""description"":"""",""name"":""target"",""nullable"":false,""required"":true,""type"":""unity.GameObjectRef""}],""keywords"":[],""kind"":""native"",""localized"":{""ja"":{""displayName"":""Transform をリセット"",""keywords"":[""トランスフォーム"",""リセット"",""初期化""],""summary"":""GameObject の位置・回転・スケールを初期値に戻す。""}},""outputRefinement"":null,""outputs"":[{""constraints"":null,""description"":"""",""name"":""gameObject"",""nullable"":false,""required"":false,""type"":""unity.GameObjectRef""}],""requirements"":[{""kind"":""edit_mode"",""port"":null}],""sideEffects"":[""scene.modify""],""summary"":""Reset local position, rotation and scale of a GameObject."",""tags"":[],""undo"":""full"",""version"":1}";

        private const string M4Hash =
            "57970450ec5307ecce148e9d30966288b4dc55e567ef0ed3b1092a951ba26c1e";

        // ── Golden descriptorHash vectors (SPEC §0.6 SG-12) ───────────────────

        [Test]
        public void C2ExampleMatchesGoldenVector()
        {
            var descriptor = DescriptorTestFixtures.BuildC2Example();
            Assert.AreEqual(C2Canonical, CanonicalJson.Serialize(DescriptorHash.ToToken(descriptor)));
            Assert.AreEqual(C2Hash, DescriptorHash.Compute(descriptor));
        }

        [Test]
        public void M4ExampleMatchesGoldenVector()
        {
            var descriptor = DescriptorTestFixtures.BuildM4Example();
            Assert.AreEqual(M4Canonical, CanonicalJson.Serialize(DescriptorHash.ToToken(descriptor)));
            Assert.AreEqual(M4Hash, DescriptorHash.Compute(descriptor));
        }

        // ── Determinism and sensitivity ───────────────────────────────────────

        [Test]
        public void SameContentSameHash()
        {
            Assert.AreEqual(
                DescriptorHash.Compute(DescriptorTestFixtures.BuildC2Example()),
                DescriptorHash.Compute(DescriptorTestFixtures.BuildC2Example()));
        }

        [Test]
        public void FieldChangeChangesHash()
        {
            var descriptor = DescriptorTestFixtures.BuildC2Example();
            var baseline = DescriptorHash.Compute(descriptor);

            Assert.AreNotEqual(baseline,
                DescriptorHash.Compute(descriptor with { Summary = "Another summary." }));
            Assert.AreNotEqual(baseline,
                DescriptorHash.Compute(descriptor with { Version = 2 }));
            Assert.AreNotEqual(baseline,
                DescriptorHash.Compute(descriptor with { Destructive = true }));
            Assert.AreNotEqual(baseline,
                DescriptorHash.Compute(descriptor with { Kind = OperationKind.Blueprint }));
            Assert.AreNotEqual(baseline,
                DescriptorHash.Compute(descriptor with
                {
                    SideEffects = new[] { "read" },
                }));
        }

        [Test]
        public void AbsentDefaultAndExplicitNullDefaultHashDifferently()
        {
            var port = new PortDescriptor("a", "core.Json", false, null, string.Empty, null);
            var withNull = new PortDescriptor(
                "a", "core.Json", false, JsonValue.Parse("null").Value!, string.Empty, null);
            var baseDescriptor = Op.Define("acme.hash", 1).Build();

            var absent = DescriptorHash.Compute(baseDescriptor with { Inputs = new[] { port } });
            var explicitNull = DescriptorHash.Compute(
                baseDescriptor with { Inputs = new[] { withNull } });

            Assert.AreNotEqual(absent, explicitNull);
        }

        [Test]
        public void HashIsLowercaseHexSha256()
        {
            var hash = DescriptorHash.Compute(DescriptorTestFixtures.BuildC2Example());
            Assert.AreEqual(64, hash.Length);
            foreach (var c in hash)
            {
                Assert.IsTrue(
                    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
                    "non-lowercase-hex character: " + c);
            }
        }

        [Test]
        public void NullDescriptorThrows()
        {
            Assert.Throws<ArgumentNullException>(() => DescriptorHash.Compute(null!));
        }
    }
}
