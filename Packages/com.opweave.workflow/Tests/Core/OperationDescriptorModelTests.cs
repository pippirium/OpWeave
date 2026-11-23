#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §C1 record shapes and defaults (T-W1.5-02):
    /// <see cref="PortDescriptor.Nullable"/> defaults to false, optional
    /// members default to null, collections are defensive copies (SPEC §0.4),
    /// <see cref="RetryPolicy"/> carries the §E7 defaults, and the record
    /// rejects structurally invalid input (null members, empty names).
    /// </summary>
    internal sealed class OperationDescriptorModelTests
    {
        private static OperationDescriptor Minimal() =>
            Op.Define("acme.model", 1).Build();

        [Test]
        public void PortDescriptorDefaults()
        {
            var port = new PortDescriptor("p", "core.String", true, null, string.Empty, null);
            Assert.IsFalse(port.Nullable);
            Assert.IsNull(port.Default);
            Assert.IsNull(port.Constraints);
        }

        [Test]
        public void RequirementPortDefaultsToNull()
        {
            Assert.IsNull(new Requirement("edit_mode").Port);
            Assert.AreEqual("scene", new Requirement("scene_loaded", "scene").Port);
        }

        [Test]
        public void OperationExampleFixtureManifestDefaultsToNull()
        {
            var workflow = JsonValue.Parse("{}").Value!;
            var example = new OperationExample("t", workflow);
            Assert.IsNull(example.FixtureManifest);
            Assert.AreEqual("{}", example.WorkflowJson.CanonicalText);
        }

        [Test]
        public void RetryPolicySpecDefaults()
        {
            var retry = new RetryPolicy();
            Assert.AreEqual(1, retry.MaxAttempts);
            Assert.AreEqual(500, retry.InitialBackoffMs);
            Assert.AreEqual(2.0, retry.Multiplier);
            Assert.AreEqual(10_000, retry.MaxBackoffMs);
        }

        [Test]
        public void RetryPolicyRejectsBadValues()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(MaxAttempts: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(InitialBackoffMs: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(Multiplier: 0.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(Multiplier: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(Multiplier: double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(MaxBackoffMs: -1));
        }

        [Test]
        public void DescriptorCopiesCollections()
        {
            var inputs = new List<PortDescriptor>
            {
                new PortDescriptor("a", "core.String", true, null, string.Empty, null),
            };
            var localized = new Dictionary<string, LocalizedText>
            {
                ["ja"] = new LocalizedText("名前", "概要", new[] { "語" }),
            };
            var descriptor = Minimal() with
            {
                Inputs = inputs,
                Localized = localized,
            };

            inputs.Add(new PortDescriptor("b", "core.Int", false, null, string.Empty, null));
            localized["en"] = new LocalizedText("name", "summary", Array.Empty<string>());

            Assert.AreEqual(1, descriptor.Inputs.Count);
            Assert.AreEqual(1, descriptor.Localized.Count);
        }

        [Test]
        public void DescriptorRejectsNullAndEmptyMembers()
        {
            Assert.Throws<ArgumentException>(() => { _ = Minimal() with { Id = "" }; });
            Assert.Throws<ArgumentNullException>(() => { _ = Minimal() with { Summary = null! }; });
            Assert.Throws<ArgumentNullException>(() => { _ = Minimal() with { DefaultRetry = null! }; });
            Assert.Throws<ArgumentNullException>(() => { _ = Minimal() with { Inputs = null! }; });
            Assert.Throws<ArgumentException>(() =>
            {
                _ = Minimal() with
                {
                    Inputs = new PortDescriptor[] { null! },
                };
            });
        }

        [Test]
        public void NestedRecordsApplyChecksOnInit()
        {
            var port = new PortDescriptor("p", "core.String", true, null, string.Empty, null);
            Assert.Throws<ArgumentException>(() => { _ = port with { Name = "" }; });
            Assert.Throws<ArgumentException>(() => { _ = port with { Type = "" }; });
            Assert.Throws<ArgumentNullException>(() => { _ = port with { Description = null! }; });
            Assert.Throws<ArgumentException>(() =>
            {
                _ = new PortDescriptor("p", "core.String", true, null, string.Empty, null)
                {
                    Name = "",
                };
            });

            var localized = new LocalizedText("n", "s", new[] { "k" });
            Assert.Throws<ArgumentNullException>(() => { _ = localized with { DisplayName = null! }; });
            Assert.Throws<ArgumentNullException>(() => { _ = localized with { Summary = null! }; });
            Assert.Throws<ArgumentException>(() =>
            {
                _ = localized with { Keywords = new string[] { null! } };
            });

            var keywords = new List<string> { "k" };
            var copied = localized with { Keywords = keywords };
            keywords.Add("other");
            Assert.AreEqual(1, copied.Keywords.Count);

            var example = new OperationExample("t", JsonValue.Parse("{}").Value!);
            Assert.Throws<ArgumentException>(() => { _ = example with { Title = "" }; });
            Assert.Throws<ArgumentNullException>(() => { _ = example with { WorkflowJson = null! }; });

            Assert.Throws<ArgumentException>(() => { _ = new Requirement("k") with { Kind = "" }; });
            Assert.Throws<ArgumentException>(() =>
            {
                _ = new TypeRefinement("a", "b", "c") with { Template = "" };
            });
            Assert.Throws<ArgumentOutOfRangeException>(() =>
            {
                _ = new RetryPolicy() with { MaxAttempts = 0 };
            });

            var enumValues = new List<string> { "a" };
            var constraints = new PortConstraints(enumValues, null, null, null, null)
                with { Enum = enumValues };
            enumValues.Add("b");
            Assert.AreEqual(1, constraints.Enum!.Count);
        }

        [Test]
        public void RecordShapesMatchSpec()
        {
            var localized = new LocalizedText("表示", "要約", new[] { "検索" });
            Assert.AreEqual("表示", localized.DisplayName);
            Assert.AreEqual("要約", localized.Summary);
            CollectionAssert.AreEqual(new[] { "検索" }, localized.Keywords);

            var refinement = new TypeRefinement("componentType", "component", "unity.ComponentRef<{0}>");
            Assert.AreEqual("componentType", refinement.FromParameter);
            Assert.AreEqual("component", refinement.OutputPort);
            Assert.AreEqual("unity.ComponentRef<{0}>", refinement.Template);
        }

        [Test]
        public void OperationKindIsReserved()
        {
            Assert.AreEqual(0, (int)OperationKind.Native);
            Assert.AreEqual(1, (int)OperationKind.Blueprint);
            Assert.AreEqual(OperationKind.Native, Minimal().Kind);
        }
    }
}
