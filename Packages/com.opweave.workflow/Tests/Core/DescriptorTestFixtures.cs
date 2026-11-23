#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// Shared fixtures for the T-W1.5-02 descriptor tests: a test-local type
    /// registry (SPEC §0.6 SG-10 — never the production provider) and the two
    /// SPEC example descriptors (§C2 unity.gameobject.create, §M.4
    /// proj.transform.reset).
    /// </summary>
    internal static class DescriptorTestFixtures
    {
        private sealed class PassthroughCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) => value;
        }

        private static readonly IValueCodec s_codec = new PassthroughCodec();

        private static TypeDefinition Def(
            string id, TypeKind kind = TypeKind.Primitive) =>
            new TypeDefinition(id, kind, 0, Array.Empty<string>(), null, "test type", s_codec);

        private sealed class FakeProvider : ITypeProvider
        {
            private readonly TypeDefinition[] _definitions;

            internal FakeProvider(params TypeDefinition[] definitions) => _definitions = definitions;

            public IEnumerable<TypeDefinition> GetTypes() => _definitions;
        }

        /// <summary>
        /// A test-local registry carrying the built-in <c>core.*</c> types plus
        /// the <c>unity.*</c> port types used by the SPEC examples
        /// (<c>unity.SceneRef</c>, <c>unity.GameObjectRef</c> — Ref-kind, like
        /// the §B5 Refs they stand in for).
        /// </summary>
        internal static ITypeRegistry CreateRegistry()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "unity.test", new FakeProvider(
                        Def("unity.SceneRef", TypeKind.Ref),
                        Def("unity.GameObjectRef", TypeKind.Ref),
                        Def("unity.TypeName")),
                    OperationOrigin.Standard),
            });
            AssertIsEmpty(result.Failures);
            return result.Registry;
        }

        /// <summary>
        /// The SPEC §C2 example: the exact SG-05 fluent chain used by
        /// <c>unity.gameobject.create</c>.
        /// </summary>
        internal static OperationDescriptor BuildC2Example() =>
            Op.Define("unity.gameobject.create", 1)
                .Summary("Create an empty or primitive GameObject in a loaded scene.")
                .Ja("GameObject を作成", "読み込み済みの Scene に空またはプリミティブの GameObject を作る。", "作成", "オブジェクト")
                .Input("scene", "unity.SceneRef", required: false)
                .Input("parent", "unity.GameObjectRef", required: false)
                .Input("name", "core.String", required: true, c => c.MaxLength(256))
                .Input("primitive", "core.String", required: false, c => c.Enum("none", "cube", "sphere", "capsule", "cylinder", "plane", "quad"), @default: "none")
                .Output("gameObject", "unity.GameObjectRef")
                .Effects("scene.modify").Undo(UndoSupport.Full).Idempotency(Idempotency.Reconcilable)
                .Requires("edit_mode").Requires("scene_loaded", "scene")
                .Build();

        /// <summary>
        /// The SPEC §M.4 example: <c>proj.transform.reset</c> with the SG-05
        /// fluent calls plus the record-level fields the fluent set does not
        /// cover (DisplayName, Category, Examples) set on the §C1 record — the
        /// explicit-factory path SG-05 prescribes.
        /// </summary>
        internal static OperationDescriptor BuildM4Example() =>
            Op.Define("proj.transform.reset", 1)
                .Summary("Reset local position, rotation and scale of a GameObject.")
                .Ja("Transform をリセット", "GameObject の位置・回転・スケールを初期値に戻す。", "トランスフォーム", "リセット", "初期化")
                .Input("target", "unity.GameObjectRef", required: true)
                .Output("gameObject", "unity.GameObjectRef")
                .Effects("scene.modify").Undo(UndoSupport.Full)
                .Idempotency(Idempotency.Idempotent)
                .Requires("edit_mode")
                .Build()
                with
                {
                    DisplayName = "Reset Transform",
                    Category = "Transform",
                    Examples = new[]
                    {
                        new OperationExample(
                            "Reset after create",
                            JsonValue.Parse(ResetAfterCreateWorkflow).Value!),
                    },
                };

        /// <summary>The complete WF embedded by the §M.4 example (C8 shape).</summary>
        internal const string ResetAfterCreateWorkflow = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""example.reset-transform"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""create"", ""operation"": ""unity.gameobject.create"", ""version"": 1, ""parameters"": { ""name"": ""Player"" } },
    { ""id"": ""reset"", ""operation"": ""proj.transform.reset"", ""version"": 1 }
  ],
  ""controlEdges"": [ { ""from"": ""start"", ""to"": ""create"" }, { ""from"": ""create"", ""to"": ""reset"" } ],
  ""dataEdges"": [ { ""from"": { ""node"": ""create"", ""port"": ""gameObject"" }, ""to"": { ""node"": ""reset"", ""port"": ""target"" } } ],
  ""outputs"": [ { ""id"": ""gameObject"", ""from"": { ""node"": ""reset"", ""port"": ""gameObject"" } } ]
}";

        private static void AssertIsEmpty(IReadOnlyList<TypeRegistryFailure> failures)
        {
            if (failures.Count != 0)
            {
                throw new InvalidOperationException(
                    "Test registry build failed: " + failures[0].Detail);
            }
        }
    }
}
