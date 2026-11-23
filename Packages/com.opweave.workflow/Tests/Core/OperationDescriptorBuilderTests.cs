#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SG-05 fluent Descriptor builder (T-W1.5-02): the
    /// exact SPEC §C2 chain compiles and produces the expected Descriptor, the
    /// builder defaults match SPEC §C1/§E6/§M.4, the port-constraints builder
    /// maps to <see cref="PortConstraints"/>, and the public fluent surface is
    /// exactly the approved SG-05 set — nothing undocumented.
    /// </summary>
    internal sealed class OperationDescriptorBuilderTests
    {
        [Test]
        public void C2ExampleBuildsWithAllValues()
        {
            var descriptor = DescriptorTestFixtures.BuildC2Example();

            Assert.AreEqual("unity.gameobject.create", descriptor.Id);
            Assert.AreEqual(1, descriptor.Version);
            Assert.AreEqual(
                "Create an empty or primitive GameObject in a loaded scene.",
                descriptor.Summary);

            Assert.AreEqual(1, descriptor.Localized.Count);
            var ja = descriptor.Localized["ja"];
            Assert.AreEqual("GameObject を作成", ja.DisplayName);
            Assert.AreEqual(
                "読み込み済みの Scene に空またはプリミティブの GameObject を作る。",
                ja.Summary);
            CollectionAssert.AreEqual(new[] { "作成", "オブジェクト" }, ja.Keywords);

            Assert.AreEqual(4, descriptor.Inputs.Count);
            AssertPort(descriptor.Inputs[0], "scene", "unity.SceneRef", false);
            AssertPort(descriptor.Inputs[1], "parent", "unity.GameObjectRef", false);

            var name = descriptor.Inputs[2];
            AssertPort(name, "name", "core.String", true);
            Assert.IsNotNull(name.Constraints);
            Assert.AreEqual(256, name.Constraints!.MaxLength);
            Assert.IsNull(name.Constraints.Enum);
            Assert.IsNull(name.Default);

            var primitive = descriptor.Inputs[3];
            AssertPort(primitive, "primitive", "core.String", false);
            Assert.IsNotNull(primitive.Constraints);
            CollectionAssert.AreEqual(
                new[] { "none", "cube", "sphere", "capsule", "cylinder", "plane", "quad" },
                primitive.Constraints!.Enum);
            Assert.IsNotNull(primitive.Default);
            Assert.AreEqual("\"none\"", primitive.Default!.CanonicalText);

            Assert.AreEqual(1, descriptor.Outputs.Count);
            AssertPort(descriptor.Outputs[0], "gameObject", "unity.GameObjectRef", false);

            CollectionAssert.AreEqual(new[] { "next" }, descriptor.ControlOutputs);
            Assert.AreEqual(ExecutionMode.Immediate, descriptor.ExecutionMode);
            CollectionAssert.AreEqual(new[] { "scene.modify" }, descriptor.SideEffects);
            Assert.IsFalse(descriptor.Destructive);
            Assert.AreEqual(UndoSupport.Full, descriptor.Undo);
            Assert.AreEqual(Idempotency.Reconcilable, descriptor.Idempotency);

            Assert.AreEqual(2, descriptor.Requirements.Count);
            Assert.AreEqual(new Requirement("edit_mode"), descriptor.Requirements[0]);
            Assert.AreEqual(new Requirement("scene_loaded", "scene"), descriptor.Requirements[1]);

            // Builder defaults for the fields the fluent set does not cover.
            Assert.AreEqual(string.Empty, descriptor.DisplayName);
            Assert.AreEqual(string.Empty, descriptor.Description);
            Assert.AreEqual(string.Empty, descriptor.Category);
            Assert.AreEqual(0, descriptor.Tags.Count);
            Assert.AreEqual(0, descriptor.Keywords.Count);
            Assert.AreEqual(0, descriptor.Examples.Count);
            Assert.IsNull(descriptor.OutputRefinement);
            Assert.AreEqual(OperationKind.Native, descriptor.Kind);
            Assert.AreEqual(new RetryPolicy(), descriptor.DefaultRetry);
            Assert.AreEqual(30, descriptor.DefaultTimeoutSeconds);
        }

        [Test]
        public void M4ExampleBuildsWithRecordLevelFields()
        {
            var descriptor = DescriptorTestFixtures.BuildM4Example();

            Assert.AreEqual("proj.transform.reset", descriptor.Id);
            Assert.AreEqual(1, descriptor.Version);
            Assert.AreEqual("Reset Transform", descriptor.DisplayName);
            Assert.AreEqual("Transform", descriptor.Category);
            Assert.AreEqual(Idempotency.Idempotent, descriptor.Idempotency);
            Assert.AreEqual(UndoSupport.Full, descriptor.Undo);

            Assert.AreEqual(1, descriptor.Examples.Count);
            var example = descriptor.Examples[0];
            Assert.AreEqual("Reset after create", example.Title);
            Assert.IsNull(example.FixtureManifest);
            // The embedded WF is a complete document (schemaVersion, core.start,
            // control/data edges, outputs) — never a fragment (SPEC §C1, §C8).
            var workflow = example.WorkflowJson.CanonicalText;
            StringAssert.Contains("\"schemaVersion\":\"1.0\"", workflow);
            StringAssert.Contains("\"operation\":\"core.start\"", workflow);
        }

        [Test]
        public void MinimalBuildUsesSpecDefaults()
        {
            var descriptor = Op.Define("acme.noop", 3).Build();

            Assert.AreEqual("acme.noop", descriptor.Id);
            Assert.AreEqual(3, descriptor.Version);
            Assert.AreEqual(string.Empty, descriptor.Summary);
            Assert.AreEqual(0, descriptor.Inputs.Count);
            Assert.AreEqual(0, descriptor.Outputs.Count);
            CollectionAssert.AreEqual(new[] { "next" }, descriptor.ControlOutputs);
            Assert.AreEqual(ExecutionMode.Immediate, descriptor.ExecutionMode);
            Assert.AreEqual(0, descriptor.SideEffects.Count);
            Assert.IsFalse(descriptor.Destructive);
            Assert.AreEqual(UndoSupport.None, descriptor.Undo);
            Assert.AreEqual(Idempotency.Unsafe, descriptor.Idempotency);
            Assert.AreEqual(new RetryPolicy(), descriptor.DefaultRetry);
            Assert.AreEqual(30, descriptor.DefaultTimeoutSeconds);
            Assert.AreEqual(0, descriptor.Requirements.Count);
            Assert.IsNull(descriptor.OutputRefinement);
            Assert.AreEqual(0, descriptor.Examples.Count);
            Assert.AreEqual(0, descriptor.Localized.Count);
            Assert.AreEqual(OperationKind.Native, descriptor.Kind);
        }

        [Test]
        public void ConstraintBuilderMapsEveryMethod()
        {
            var descriptor = Op.Define("acme.ports", 1)
                .Input("a", "core.String", required: true,
                    c => c.Enum("x", "y").MaxLength(8).Pattern("^[xy]+$"))
                .Input("b", "core.Float", required: false, c => c.Min(-1.5).Max(9.25))
                .Input("c", "core.String", required: true, c => c.ConstantOnly())
                .Build();

            var a = descriptor.Inputs[0].Constraints!;
            CollectionAssert.AreEqual(new[] { "x", "y" }, a.Enum);
            Assert.AreEqual(8, a.MaxLength);
            Assert.AreEqual("^[xy]+$", a.Pattern);
            Assert.IsFalse(a.ConstantOnly);

            var b = descriptor.Inputs[1].Constraints!;
            Assert.AreEqual(-1.5, b.Min);
            Assert.AreEqual(9.25, b.Max);
            Assert.IsFalse(b.ConstantOnly);

            var c = descriptor.Inputs[2].Constraints!;
            Assert.IsTrue(c.ConstantOnly);
            Assert.IsNull(c.Enum);
            Assert.IsNull(c.Pattern);
            Assert.IsNull(c.Min);
            Assert.IsNull(c.Max);
            Assert.IsNull(c.MaxLength);
        }

        [Test]
        public void FluentSurfaceIsExactlySg05()
        {
            CollectionAssert.AreEquivalent(
                new[] { "Define" },
                PublicMethodNames(typeof(Op), BindingFlags.Static | BindingFlags.Public));

            CollectionAssert.AreEquivalent(
                new[]
                {
                    "Summary", "Ja", "Input", "Output", "Effects",
                    "Undo", "Idempotency", "Requires", "Build",
                },
                PublicMethodNames(
                    typeof(OperationDescriptorBuilder),
                    BindingFlags.Instance | BindingFlags.Public));

            CollectionAssert.AreEquivalent(
                new[] { "Enum", "MaxLength", "Pattern", "Min", "Max", "ConstantOnly" },
                PublicMethodNames(
                    typeof(PortConstraintsBuilder),
                    BindingFlags.Instance | BindingFlags.Public));

            // The exact SG-05 signatures.
            var define = typeof(Op).GetMethod("Define")!;
            Assert.AreEqual(typeof(OperationDescriptorBuilder), define.ReturnType);
            Assert.AreEqual(
                new[] { typeof(string), typeof(int) },
                ParameterTypes(define));

            var input = typeof(OperationDescriptorBuilder).GetMethod("Input")!;
            Assert.AreEqual(
                new[]
                {
                    typeof(string), typeof(string), typeof(bool),
                    typeof(Action<PortConstraintsBuilder>), typeof(JsonValue),
                },
                ParameterTypes(input));

            var requires = typeof(OperationDescriptorBuilder).GetMethod("Requires")!;
            Assert.AreEqual(
                new[] { typeof(string), typeof(string) },
                ParameterTypes(requires));

            Assert.AreEqual(
                typeof(OperationDescriptor),
                typeof(OperationDescriptorBuilder).GetMethod("Build")!.ReturnType);
        }

        [Test]
        public void BuilderOutputIsImmutable()
        {
            var builder = Op.Define("acme.immutable", 1).Input("a", "core.String", required: true);
            var first = builder.Build();
            builder.Input("b", "core.String", required: false);

            // The already built descriptor keeps its snapshot.
            Assert.AreEqual(1, first.Inputs.Count);
            var second = builder.Build();
            Assert.AreEqual(2, second.Inputs.Count);
        }

        [Test]
        public void InputDefaultAcceptsJsonValueAndLiterals()
        {
            var descriptor = Op.Define("acme.defaults", 1)
                .Input("a", "core.String", required: false, @default: "lit")
                .Input("b", "core.Json", required: false,
                    @default: JsonValue.Parse("null").Value!)
                .Build();

            Assert.AreEqual("\"lit\"", descriptor.Inputs[0].Default!.CanonicalText);
            Assert.AreEqual("null", descriptor.Inputs[1].Default!.CanonicalText);
        }

        [Test]
        public void NullArgumentsThrow()
        {
            Assert.Throws<ArgumentException>(() => Op.Define(null!, 1));
            Assert.Throws<ArgumentException>(() => Op.Define("", 1));
            Assert.Throws<ArgumentNullException>(
                () => Op.Define("acme.x", 1).Summary(null!));
            Assert.Throws<ArgumentNullException>(
                () => Op.Define("acme.x", 1).Effects(null!));
        }

        private static void AssertPort(
            PortDescriptor port, string name, string type, bool required)
        {
            Assert.AreEqual(name, port.Name);
            Assert.AreEqual(type, port.Type);
            Assert.AreEqual(required, port.Required);
            Assert.IsFalse(port.Nullable);
        }

        private static List<string> PublicMethodNames(Type type, BindingFlags flags)
        {
            var names = new List<string>();
            foreach (var method in type.GetMethods(flags | BindingFlags.DeclaredOnly))
            {
                names.Add(method.Name);
            }
            return names;
        }

        private static Type[] ParameterTypes(MethodInfo method)
        {
            var parameters = method.GetParameters();
            var types = new Type[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                types[i] = parameters[i].ParameterType;
            }
            return types;
        }
    }
}
