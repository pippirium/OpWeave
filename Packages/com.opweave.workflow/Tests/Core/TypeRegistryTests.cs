#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the immutable TypeRegistry snapshot and the SPEC §B0.1
    /// collision rules of <see cref="TypeRegistryBuilder"/> (T-W1.4-02):
    /// resolution with OW-TYPE-001/OW-TYPE-005, dual invalidation on Type ID
    /// collisions, unregistered bases, cycles, missing Codecs, reserved
    /// namespaces, provider failures and snapshot immutability.
    /// </summary>
    internal sealed class TypeRegistryTests
    {
        private sealed class PassthroughCodec : IValueCodec
        {
            public IReadOnlyList<Diagnostic> Validate(JsonValue value, PortConstraints? constraints) =>
                Array.Empty<Diagnostic>();

            public JsonValue Normalize(JsonValue value) => value;
        }

        private static readonly IValueCodec s_codec = new PassthroughCodec();

        private static TypeDefinition Def(
            string id,
            int arity = 0,
            IReadOnlyList<string>? bases = null,
            TypeKind kind = TypeKind.Primitive) =>
            new TypeDefinition(id, kind, arity, bases ?? Array.Empty<string>(), null, "test type", s_codec);

        private sealed class FakeProvider : ITypeProvider
        {
            private readonly Func<IEnumerable<TypeDefinition>> _getTypes;

            internal FakeProvider(params TypeDefinition[] definitions)
                : this(() => definitions)
            {
            }

            internal FakeProvider(Func<IEnumerable<TypeDefinition>> getTypes) => _getTypes = getTypes;

            public IEnumerable<TypeDefinition> GetTypes() => _getTypes();
        }

        private static TypeProviderCandidate Custom(string providerId, ITypeProvider provider) =>
            new TypeProviderCandidate(providerId, provider, OperationOrigin.Custom);

        private static TypeProviderCandidate Standard(string providerId, ITypeProvider provider) =>
            new TypeProviderCandidate(providerId, provider, OperationOrigin.Standard);

        private static Result<TypeDefinition> Resolve(TypeRegistryBuildResult result, string id) =>
            result.Registry.Resolve(TypeRef.Parse(id).Value!);

        private static Diagnostic MustFailResolve(TypeRegistryBuildResult result, string id, string code)
        {
            var resolved = Resolve(result, id);
            Assert.IsFalse(resolved.Ok, "Resolve unexpectedly succeeded: " + id);
            Assert.AreEqual(1, resolved.Diagnostics.Count);
            var diagnostic = resolved.Diagnostics[0];
            Assert.AreEqual(code, diagnostic.Code, id);
            Assert.AreEqual(Severity.Error, diagnostic.Severity);
            Assert.IsFalse(diagnostic.Retryable);
            return diagnostic;
        }

        // ── Normal behavior: registered definitions resolve ─────────────────────

        [Test]
        public void RegisteredDefinitionsResolve()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(
                    Def("acme.Widget"),
                    Def("acme.Box<>", arity: 1),
                    Def("acme.Pair<>", arity: 2))),
            });

            Assert.AreEqual(0, result.Failures.Count);
            Assert.AreEqual(3, result.Registry.All.Count);

            var widget = Resolve(result, "acme.Widget");
            Assert.IsTrue(widget.Ok);
            Assert.AreEqual("acme.Widget", widget.Value!.Id);

            var box = Resolve(result, "acme.Box<acme.Widget>");
            Assert.IsTrue(box.Ok);
            Assert.AreEqual("acme.Box<>", box.Value!.Id);
            Assert.AreEqual(1, box.Value!.GenericArity);

            var pair = Resolve(result, "acme.Pair<core.String,type:MyGame.Player>");
            Assert.IsTrue(pair.Ok);
            Assert.AreEqual("acme.Pair<>", pair.Value!.Id);
        }

        [Test]
        public void AllListsDefinitionsSortedByTypeIdOrdinal()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(
                    Def("acme.Zed"), Def("acme.Alpha"), Def("acme.Mid"))),
            });
            Assert.AreEqual(
                new[] { "acme.Alpha", "acme.Mid", "acme.Zed" },
                new[]
                {
                    result.Registry.All[0].Id,
                    result.Registry.All[1].Id,
                    result.Registry.All[2].Id,
                });
        }

        [Test]
        public void ProviderEnumerationOrderDoesNotChangeTheSnapshot()
        {
            var first = TypeRegistryBuilder.Build(new[]
            {
                Custom("z.provider", new FakeProvider(Def("z.One"))),
                Custom("a.provider", new FakeProvider(Def("a.One"))),
            });
            var second = TypeRegistryBuilder.Build(new[]
            {
                Custom("a.provider", new FakeProvider(Def("a.One"))),
                Custom("z.provider", new FakeProvider(Def("z.One"))),
            });
            Assert.AreEqual(2, first.Registry.All.Count);
            Assert.AreEqual(first.Registry.All[0].Id, second.Registry.All[0].Id);
            Assert.AreEqual(first.Registry.All[1].Id, second.Registry.All[1].Id);
            Assert.AreEqual(0, first.Failures.Count);
            Assert.AreEqual(0, second.Failures.Count);
        }

        // ── OW-TYPE-001 / OW-TYPE-005 on Resolve ────────────────────────────────

        [Test]
        public void UnknownTypeReturnsOwType001()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(Def("acme.Widget"))),
            });
            var diagnostic = MustFailResolve(result, "acme.Missing", ErrorCodes.OW_TYPE_001);
            StringAssert.Contains("acme.Missing", diagnostic.Message);
        }

        [Test]
        public void WrongGenericArityReturnsOwType005()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(Def("acme.Box<>", arity: 1))),
            });
            MustFailResolve(result, "acme.Box<a.B,c.D>", ErrorCodes.OW_TYPE_005);
            MustFailResolve(result, "acme.Box", ErrorCodes.OW_TYPE_005);
        }

        [Test]
        public void ResolveRejectsNull()
        {
            var result = TypeRegistryBuilder.Build(Array.Empty<TypeProviderCandidate>());
            Assert.Throws<ArgumentNullException>(() => result.Registry.Resolve(null!));
        }

        // ── SPEC §B0.1 collision: a shared Type ID invalidates both candidates ──

        [Test]
        public void DuplicateTypeIdInvalidatesBothCandidates()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("p.one", new FakeProvider(Def("acme.Dup"), Def("acme.OnlyP1"))),
                Custom("p.two", new FakeProvider(Def("acme.Dup"), Def("acme.OnlyP2"))),
            });

            MustFailResolve(result, "acme.Dup", ErrorCodes.OW_TYPE_001);
            Assert.IsTrue(Resolve(result, "acme.OnlyP1").Ok);
            Assert.IsTrue(Resolve(result, "acme.OnlyP2").Ok);

            var duplicates = new List<TypeRegistryFailure>();
            foreach (var failure in result.Failures)
            {
                if (failure.Kind == TypeRegistryFailureKind.DuplicateTypeId)
                {
                    duplicates.Add(failure);
                }
            }
            Assert.AreEqual(2, duplicates.Count);
            Assert.AreEqual("acme.Dup", duplicates[0].TypeId);
            Assert.AreEqual("acme.Dup", duplicates[1].TypeId);
            Assert.AreEqual("p.one", duplicates[0].ProviderId);
            Assert.AreEqual("p.two", duplicates[1].ProviderId);
        }

        [Test]
        public void DuplicateTypeIdInvalidatesBothWhenOneCandidateIsInvalid()
        {
            // SPEC §B0.1: the collision count covers every collected candidate,
            // so a codec-less offer still invalidates its valid twin.
            var codecless = new TypeDefinition(
                "acme.Dup", TypeKind.Primitive, 0, Array.Empty<string>(), null, "d", null!);
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("p.one", new FakeProvider(Def("acme.Dup"), Def("acme.OnlyP1"))),
                Custom("p.two", new FakeProvider(codecless)),
            });

            MustFailResolve(result, "acme.Dup", ErrorCodes.OW_TYPE_001);
            Assert.IsTrue(Resolve(result, "acme.OnlyP1").Ok);
            Assert.AreEqual(1, result.Registry.All.Count);

            var duplicates = 0;
            var missingCodec = 0;
            var pOne = 0;
            var pTwo = 0;
            foreach (var failure in result.Failures)
            {
                if (failure.Kind == TypeRegistryFailureKind.DuplicateTypeId)
                {
                    duplicates++;
                    Assert.AreEqual("acme.Dup", failure.TypeId);
                }
                if (failure.Kind == TypeRegistryFailureKind.MissingCodec)
                {
                    missingCodec++;
                    Assert.AreEqual("acme.Dup", failure.TypeId);
                }
                if (failure.ProviderId == "p.one")
                {
                    pOne++;
                }
                if (failure.ProviderId == "p.two")
                {
                    pTwo++;
                }
            }
            Assert.AreEqual(2, duplicates);
            Assert.AreEqual(1, missingCodec);
            Assert.AreEqual(1, pOne);
            Assert.AreEqual(2, pTwo);
        }

        [Test]
        public void DuplicateProviderIdRejectsAllCandidatesOfBothProviders()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(Def("acme.One"))),
                Custom("acme.types", new FakeProvider(Def("acme.Two"))),
                Custom("other.types", new FakeProvider(Def("other.Three"))),
            });

            MustFailResolve(result, "acme.One", ErrorCodes.OW_TYPE_001);
            MustFailResolve(result, "acme.Two", ErrorCodes.OW_TYPE_001);
            Assert.IsTrue(Resolve(result, "other.Three").Ok);

            Assert.AreEqual(2, result.Failures.Count);
            Assert.AreEqual(TypeRegistryFailureKind.DuplicateProviderId, result.Failures[0].Kind);
            Assert.AreEqual(TypeRegistryFailureKind.DuplicateProviderId, result.Failures[1].Kind);
            Assert.IsNull(result.Failures[0].TypeId);
            Assert.AreEqual("acme.types", result.Failures[0].ProviderId);
        }

        // ── Base-type graph: unregistered bases, cascades, cycles ──────────────

        [Test]
        public void UnregisteredBaseTypeIsExcluded()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(
                    Def("acme.Derived", bases: new[] { "acme.Missing" }),
                    Def("acme.Independent"))),
            });

            MustFailResolve(result, "acme.Derived", ErrorCodes.OW_TYPE_001);
            Assert.IsTrue(Resolve(result, "acme.Independent").Ok);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreEqual(TypeRegistryFailureKind.UnregisteredBaseType, result.Failures[0].Kind);
            Assert.AreEqual("acme.Derived", result.Failures[0].TypeId);
        }

        [Test]
        public void DependentsOfExcludedTypesAreRejectedTransitively()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(
                    Def("acme.Base", bases: new[] { "acme.Missing" }),
                    Def("acme.Mid", bases: new[] { "acme.Base" }),
                    Def("acme.Leaf", bases: new[] { "acme.Mid" }))),
            });

            Assert.AreEqual(3, result.Failures.Count);
            Assert.AreEqual(0, result.Registry.All.Count);
            foreach (var failure in result.Failures)
            {
                Assert.AreEqual(TypeRegistryFailureKind.UnregisteredBaseType, failure.Kind);
            }
        }

        [Test]
        public void GenericBaseInstantiationsResolveTheirArguments()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(
                    Def("acme.Item"),
                    Def("acme.Box<>", arity: 1),
                    Def("acme.Special", bases: new[] { "acme.Box<acme.Item>" }),
                    Def("acme.ClassHeld", bases: new[] { "acme.Box<type:MyGame.Player>" }))),
            });

            Assert.AreEqual(0, result.Failures.Count);
            Assert.IsTrue(Resolve(result, "acme.Special").Ok);
            Assert.IsTrue(Resolve(result, "acme.ClassHeld").Ok);
        }

        [Test]
        public void GenericBaseWithUnregisteredArgumentIsExcluded()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(
                    Def("acme.Box<>", arity: 1),
                    Def("acme.Special", bases: new[] { "acme.Box<acme.Missing>" }))),
            });

            MustFailResolve(result, "acme.Special", ErrorCodes.OW_TYPE_001);
            Assert.AreEqual(TypeRegistryFailureKind.UnregisteredBaseType, result.Failures[0].Kind);
        }

        [Test]
        public void BaseTypeCyclesAreExcluded()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(
                    Def("acme.A", bases: new[] { "acme.B" }),
                    Def("acme.B", bases: new[] { "acme.A" }),
                    Def("acme.Self", bases: new[] { "acme.Self" }),
                    Def("acme.Free"))),
            });

            Assert.IsTrue(Resolve(result, "acme.Free").Ok);
            MustFailResolve(result, "acme.A", ErrorCodes.OW_TYPE_001);
            MustFailResolve(result, "acme.B", ErrorCodes.OW_TYPE_001);
            MustFailResolve(result, "acme.Self", ErrorCodes.OW_TYPE_001);
            Assert.AreEqual(3, result.Failures.Count);
            foreach (var failure in result.Failures)
            {
                Assert.AreEqual(TypeRegistryFailureKind.BaseTypeCycle, failure.Kind);
            }
        }

        [Test]
        public void DependentOfCycleMemberIsExcludedAsUnregisteredBase()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(
                    Def("acme.A", bases: new[] { "acme.B" }),
                    Def("acme.B", bases: new[] { "acme.A" }),
                    Def("acme.Dependent", bases: new[] { "acme.A" }))),
            });

            Assert.AreEqual(3, result.Failures.Count);
            Assert.AreEqual(TypeRegistryFailureKind.UnregisteredBaseType, result.Failures[2].Kind);
            Assert.AreEqual("acme.Dependent", result.Failures[2].TypeId);
            Assert.AreEqual(0, result.Registry.All.Count);
        }

        // ── Missing Codec and reserved namespaces ───────────────────────────────

        [Test]
        public void DefinitionWithoutCodecIsExcluded()
        {
            var codecless = new TypeDefinition(
                "acme.NoCodec", TypeKind.Primitive, 0, Array.Empty<string>(), null, "d", null!);
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(codecless, Def("acme.Ok"))),
            });

            Assert.IsTrue(Resolve(result, "acme.Ok").Ok);
            MustFailResolve(result, "acme.NoCodec", ErrorCodes.OW_TYPE_001);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreEqual(TypeRegistryFailureKind.MissingCodec, result.Failures[0].Kind);
            Assert.AreEqual("acme.NoCodec", result.Failures[0].TypeId);
            Assert.AreEqual("acme.types", result.Failures[0].ProviderId);
        }

        [Test]
        public void ReservedNamespacesFromNonStandardProvidersAreExcluded()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("custom.types", new FakeProvider(
                    Def("core.FakeString"),
                    Def("unity.FakeRef"),
                    Def("opweave.Sneaky"),
                    Def("core.Another<>", arity: 1),
                    Def("proj.my.Widget"),
                    Def("acme.Ok"))),
                Standard("@builtin", new FakeProvider(
                    Def("core.String"), Def("unity.ComponentRef<>", arity: 1))),
            });

            foreach (var id in new[] { "core.FakeString", "unity.FakeRef", "opweave.Sneaky" })
            {
                MustFailResolve(result, id, ErrorCodes.OW_TYPE_001);
            }
            Assert.IsTrue(Resolve(result, "proj.my.Widget").Ok);
            Assert.IsTrue(Resolve(result, "acme.Ok").Ok);
            Assert.IsTrue(Resolve(result, "core.String").Ok);
            Assert.IsTrue(Resolve(result, "unity.ComponentRef<unity.Animator>").Ok);

            var reserved = 0;
            foreach (var failure in result.Failures)
            {
                if (failure.Kind == TypeRegistryFailureKind.ReservedNamespace)
                {
                    reserved++;
                    Assert.AreEqual("custom.types", failure.ProviderId);
                }
            }
            Assert.AreEqual(4, reserved);
        }

        // ── Malformed candidates ────────────────────────────────────────────────

        [Test]
        public void MalformedTypeIdsAreExcluded()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(
                    Def("NoDot"),
                    Def("acme.MissingMarker", arity: 2),
                    Def("acme.StrayMarker<>", arity: 0),
                    Def("acme.WithArgs<a.B>"),
                    Def("acme.Trailing "),
                    Def("acme.Ok"))),
            });

            Assert.IsTrue(Resolve(result, "acme.Ok").Ok);
            Assert.AreEqual(1, result.Registry.All.Count);
            Assert.AreEqual(5, result.Failures.Count);
            foreach (var failure in result.Failures)
            {
                Assert.AreEqual(TypeRegistryFailureKind.MalformedTypeId, failure.Kind);
                Assert.AreEqual("acme.types", failure.ProviderId);
            }
        }

        [Test]
        public void UnknownTypeKindIsMalformed()
        {
            var bogus = new TypeDefinition(
                "acme.Bogus", (TypeKind)99, 0, Array.Empty<string>(), null, "d", s_codec);
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(bogus)),
            });
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreEqual(TypeRegistryFailureKind.MalformedTypeId, result.Failures[0].Kind);
        }

        // ── Provider contract violations ────────────────────────────────────────

        private static IEnumerable<TypeDefinition> YieldThenThrow()
        {
            yield return Def("acme.Partial");
            throw new InvalidOperationException("provider failed mid-enumeration");
        }

        private static IEnumerable<TypeDefinition> YieldWithNull()
        {
            yield return Def("acme.One");
            yield return null!;
            yield return Def("acme.Two");
        }

        [Test]
        public void ThrowingProviderLosesAllItsCandidates()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("bad.provider", new FakeProvider(YieldThenThrow)),
                Custom("good.provider", new FakeProvider(Def("good.Ok"))),
            });

            MustFailResolve(result, "acme.Partial", ErrorCodes.OW_TYPE_001);
            Assert.IsTrue(Resolve(result, "good.Ok").Ok);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreEqual(TypeRegistryFailureKind.ProviderFailure, result.Failures[0].Kind);
            Assert.IsNull(result.Failures[0].TypeId);
            Assert.AreEqual("bad.provider", result.Failures[0].ProviderId);
        }

        [Test]
        public void NullReturningProviderIsRejected()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("null.provider", new FakeProvider(() => null!)),
            });
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreEqual(TypeRegistryFailureKind.ProviderFailure, result.Failures[0].Kind);
        }

        [Test]
        public void NullCandidateIsSkippedButSiblingsSurvive()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(YieldWithNull)),
            });
            Assert.IsTrue(Resolve(result, "acme.One").Ok);
            Assert.IsTrue(Resolve(result, "acme.Two").Ok);
            Assert.AreEqual(1, result.Failures.Count);
            Assert.AreEqual(TypeRegistryFailureKind.ProviderFailure, result.Failures[0].Kind);
        }

        // ── Snapshot immutability (SPEC §B2: immutable after build) ────────────

        [Test]
        public void SnapshotRejectsMutation()
        {
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(Def("acme.Widget"))),
            });

            Assert.Throws<NotSupportedException>(
                () => ((IList<TypeDefinition>)result.Registry.All).Add(Def("acme.Extra")));
            Assert.Throws<NotSupportedException>(
                () => ((IList<TypeDefinition>)result.Registry.All).RemoveAt(0));
            Assert.Throws<NotSupportedException>(
                () => ((ICollection<TypeRegistryFailure>)result.Failures).Clear());
            Assert.AreEqual(1, result.Registry.All.Count);
        }

        [Test]
        public void ProviderMutationAfterBuildCannotReachTheSnapshot()
        {
            var definitions = new List<TypeDefinition> { Def("acme.First") };
            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(() => definitions)),
            });
            definitions.Add(Def("acme.Late"));

            Assert.AreEqual(1, result.Registry.All.Count);
            Assert.AreEqual("acme.First", result.Registry.All[0].Id);
            MustFailResolve(result, "acme.Late", ErrorCodes.OW_TYPE_001);
        }

        [Test]
        public void BaseTypesAssignedAfterConstructionAreDefensivelyCopied()
        {
            // BaseTypes set through an object initializer or a `with` expression
            // must be copied and validated just like the constructor argument;
            // otherwise mutating the caller's list rewrites a built snapshot.
            var initializerBases = new List<string> { "acme.Base" };
            var withBases = new List<string> { "acme.Base" };
            var viaInitializer = new TypeDefinition(
                "acme.Init", TypeKind.Primitive, 0, Array.Empty<string>(), null, "d", s_codec)
            {
                BaseTypes = initializerBases,
            };
            var viaWith = Def("acme.With") with { BaseTypes = withBases };

            var result = TypeRegistryBuilder.Build(new[]
            {
                Custom("acme.types", new FakeProvider(Def("acme.Base"), viaInitializer, viaWith)),
            });
            Assert.AreEqual(0, result.Failures.Count);

            initializerBases.Add("acme.Late");
            withBases.Add("acme.Late");

            var init = Resolve(result, "acme.Init");
            Assert.IsTrue(init.Ok);
            Assert.AreEqual(1, init.Value!.BaseTypes.Count);
            Assert.AreEqual("acme.Base", init.Value!.BaseTypes[0]);

            var with = Resolve(result, "acme.With");
            Assert.IsTrue(with.Ok);
            Assert.AreEqual(1, with.Value!.BaseTypes.Count);
            Assert.AreEqual("acme.Base", with.Value!.BaseTypes[0]);

            Assert.Throws<ArgumentException>(() =>
            {
                _ = Def("acme.Bad") with { BaseTypes = new string[] { null! } };
            });
        }

        // ── Input validation ────────────────────────────────────────────────────

        [Test]
        public void BuildRejectsNullInputs()
        {
            Assert.Throws<ArgumentNullException>(() => TypeRegistryBuilder.Build(null!));
            Assert.Throws<ArgumentException>(
                () => TypeRegistryBuilder.Build(new TypeProviderCandidate[] { null! }));
            Assert.Throws<ArgumentException>(
                () => new TypeProviderCandidate("", new FakeProvider(), OperationOrigin.Custom));
            Assert.Throws<ArgumentNullException>(
                () => new TypeProviderCandidate("pid", null!, OperationOrigin.Custom));
        }
    }
}
