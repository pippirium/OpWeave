#nullable enable

using System;
using System.Collections.Generic;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for the SPEC §B1 Type ID grammar and parser (T-W1.4-01):
    /// canonicalization of <c>TypeRef.Parse</c>/<see cref="TypeRef.ToString"/>,
    /// the OW-TYPE-001 syntax and OW-TYPE-005 arity diagnostics, the
    /// <c>type:</c> class-id argument leaf and grammar edge cases.
    /// </summary>
    internal sealed class TypeRefTests
    {
        private static TypeRef MustParse(string id)
        {
            var result = TypeRef.Parse(id);
            Assert.IsTrue(result.Ok, "Parse failed: " + string.Join(
                "; ", System.Linq.Enumerable.Select(
                    result.Diagnostics, d => d.Code + " " + d.Hint)));
            return result.Value!;
        }

        private static Diagnostic MustFail(string id, string expectedCode)
        {
            var result = TypeRef.Parse(id);
            Assert.IsFalse(result.Ok, "Parse unexpectedly succeeded: " + id);
            Assert.IsNull(result.Value);
            Assert.AreEqual(1, result.Diagnostics.Count);
            var diagnostic = result.Diagnostics[0];
            Assert.AreEqual(expectedCode, diagnostic.Code, id);
            Assert.AreEqual(Severity.Error, diagnostic.Severity, id);
            Assert.IsFalse(diagnostic.Retryable, id);
            return diagnostic;
        }

        // ── SPEC §B1 test: whitespace is dropped by the canonical form ──────────

        [Test]
        public void SpacedInputCanonicalizesWithoutWhitespace()
        {
            var type = MustParse("unity.ComponentRef< unity.Animator >");
            Assert.AreEqual("unity.ComponentRef<unity.Animator>", type.ToString());
        }

        // ── Grammar: valid TypeIds and their canonical form ──────────────────────

        [TestCase("core.String", "core.String")]
        [TestCase("unity.GameObjectRef", "unity.GameObjectRef")]
        [TestCase("unity.ComponentRef<unity.Animator>", "unity.ComponentRef<unity.Animator>")]
        [TestCase("org.publisher.domain.TypeName", "org.publisher.domain.TypeName")]
        [TestCase("proj.my.Widget", "proj.my.Widget")]
        [TestCase("core.List<core.String>", "core.List<core.String>")]
        [TestCase("core.List<core.List<core.Int>>", "core.List<core.List<core.Int>>")]
        [TestCase("proj.my.Map<core.String,core.Int>", "proj.my.Map<core.String,core.Int>")]
        [TestCase("a.b<c.D<e.F,type:G.H>>", "a.b<c.D<e.F,type:G.H>>")]
        [TestCase("type.Foo<type.Bar>", "type.Foo<type.Bar>")]     // 'type.' is a namespace, not the prefix
        [TestCase(" core.String ", "core.String")]                 // leading/trailing whitespace
        [TestCase("core . List < core . String >", "core.List<core.String>")]
        [TestCase(" proj.map < a.B , c.D > ", "proj.map<a.B,c.D>")]
        [TestCase("unity.ComponentRef<type:MyGame.PlayerController>", "unity.ComponentRef<type:MyGame.PlayerController>")]
        [TestCase("unity.ComponentRef< type:UnityEngine.Animator >", "unity.ComponentRef<type:UnityEngine.Animator>")]
        [TestCase("unity.ComponentRef<type:PlayerController>", "unity.ComponentRef<type:PlayerController>")] // global namespace
        [TestCase("core.List<type:Outer.Inner>", "core.List<type:Outer.Inner>")]
        public void ValidTypeIdsRoundTripToCanonicalForm(string id, string canonical)
        {
            var type = MustParse(id);
            Assert.AreEqual(canonical, type.ToString());
            // The canonical form re-parses to an equal value.
            var reparsed = MustParse(type.ToString());
            Assert.AreEqual(type, reparsed);
            Assert.AreEqual(canonical, reparsed.ToString());
        }

        // ── Parsed structure ────────────────────────────────────────────────────

        [Test]
        public void GenericArgumentsAreExposedAsTypeRefs()
        {
            var type = MustParse("unity.ComponentRef<unity.Animator>");
            Assert.AreEqual("unity.ComponentRef", type.Name);
            Assert.AreEqual(1, type.Args.Count);
            Assert.AreEqual("unity.Animator", type.Args[0].Name);
            Assert.AreEqual(0, type.Args[0].Args.Count);
        }

        [Test]
        public void TypePrefixArgumentIsALeafCarryingThePrefixedName()
        {
            var type = MustParse("unity.ComponentRef<type:MyGame.PlayerController>");
            var arg = type.Args[0];
            Assert.AreEqual("type:MyGame.PlayerController", arg.Name);
            Assert.AreEqual(0, arg.Args.Count);
            Assert.AreEqual("type:MyGame.PlayerController", arg.ToString());
        }

        [Test]
        public void ToStringPrintsCanonicalFormForConstructedValues()
        {
            var leaf = new TypeRef("core.String", Array.Empty<TypeRef>());
            Assert.AreEqual("core.String", leaf.ToString());

            var classRef = new TypeRef("type:MyGame.PlayerController", Array.Empty<TypeRef>());
            Assert.AreEqual("type:MyGame.PlayerController", classRef.ToString());

            var generic = new TypeRef("unity.ComponentRef", new[]
            {
                new TypeRef("unity.Animator", Array.Empty<TypeRef>()),
            });
            Assert.AreEqual("unity.ComponentRef<unity.Animator>", generic.ToString());

            var multi = new TypeRef("proj.my.Map", new[]
            {
                new TypeRef("core.String", Array.Empty<TypeRef>()),
                new TypeRef("core.Int", Array.Empty<TypeRef>()),
            });
            Assert.AreEqual("proj.my.Map<core.String,core.Int>", multi.ToString());
        }

        // ── OW-TYPE-005: empty generic argument list (SPEC §B1 test) ────────────

        [TestCase("unity.AssetRef<>")]
        [TestCase("unity.AssetRef< >")]
        [TestCase("a.B<>")]
        [TestCase("a.B <>")]
        [TestCase("core.List<a.B<>>")]
        [TestCase("a.B<>x")]                      // arity fires before the trailing garbage
        public void EmptyGenericArgumentListReturnsOwType005(string id)
        {
            MustFail(id, "OW-TYPE-005");
        }

        // ── OW-TYPE-001: malformed Type IDs ──────────────────────────────────────

        [TestCase("")]                                 // empty
        [TestCase("   ")]                              // whitespace only
        [TestCase("unity")]                            // no dot
        [TestCase("unity.")]                           // trailing dot
        [TestCase(".unity.X")]                         // leading dot
        [TestCase("a..b")]                             // empty segment
        [TestCase("a.b.")]                             // trailing separator
        [TestCase("1a.b")]                             // digit start
        [TestCase("a.1b")]                             // digit start after dot
        [TestCase("_a.b")]                             // underscore start
        [TestCase("a-b.c")]                            // hyphen in segment
        [TestCase("a.b-c")]                            // hyphen in segment
        [TestCase("a.bé")]                             // non-ASCII
        [TestCase("a.b c")]                            // trailing garbage after whitespace
        [TestCase("a.b<c.d>.e")]                       // trailing garbage after '>'
        [TestCase("a.b<c.d>>")]                        // extra '>'
        [TestCase("a.b<c.d>e")]                        // extra text
        [TestCase("a.b<")]                             // unclosed list
        [TestCase("a.b<c.d")]                          // unclosed list after arg
        [TestCase("a.b<c")]                            // unclosed, arg not dotted
        [TestCase("a.b<c.d,")]                         // unclosed after comma
        [TestCase("a.b<c.d,>")]                        // missing arg after comma
        [TestCase("a.b<,c.d>")]                        // leading comma
        [TestCase("a.b<c.d,,e.f>")]                    // double comma
        [TestCase("a.b<c>")]                           // arg name without dot
        [TestCase("a.b<1c.d>")]                        // arg ident starts with digit
        [TestCase("type:MyGame.PlayerController")]     // 'type:' outside generic args
        [TestCase("type:UnityEngine.Animator")]        // 'type:' outside generic args
        [TestCase("TYPE:X.Y")]                         // prefix is case-sensitive
        [TestCase("a.b<type:>")]                       // empty class name
        [TestCase("a.b<type:.>")]                      // missing class name
        [TestCase("a.b<type:9x>")]                     // class name starts with digit
        [TestCase("a.b<type:C.D<>")]                   // class-id leaf cannot take args
        [TestCase("a.b<type:C.D<e.F>>")]               // class-id leaf cannot take args
        [TestCase("a.b<type :C.D>")]                   // 'type:' is a single token
        [TestCase("a.b<type:>e")]                      // empty class name
        public void MalformedTypeIdsReturnOwType001(string id)
        {
            MustFail(id, "OW-TYPE-001");
        }

        [Test]
        public void GenericNestingIsBounded()
        {
            // 64 levels of nested type arguments parse; the 65th fails OW-TYPE-001.
            var within = "a.B<" + string.Concat(
                Repeat("c.D<", TypeRef.MaxGenericNesting - 1)) + "e.F" +
                string.Concat(Repeat(">", TypeRef.MaxGenericNesting));
            MustParse(within);

            var beyond = "a.B<" + string.Concat(
                Repeat("c.D<", TypeRef.MaxGenericNesting)) + "e.F" +
                string.Concat(Repeat(">", TypeRef.MaxGenericNesting + 1));
            MustFail(beyond, "OW-TYPE-001");
        }

        private static IEnumerable<string> Repeat(string text, int count)
        {
            for (var i = 0; i < count; i++)
            {
                yield return text;
            }
        }

        // ── Result and record shape ──────────────────────────────────────────────

        [Test]
        public void ParseOfNullThrows()
        {
            Assert.Throws<ArgumentNullException>(() => TypeRef.Parse(null!));
        }

        [Test]
        public void FailureCarriesASingleEnglishDiagnostic()
        {
            var diagnostic = MustFail("unity.AssetRef<>", "OW-TYPE-005");
            StringAssert.Contains("unity.AssetRef<>", diagnostic.Message);
        }

        [Test]
        public void ParsedValuesCompareByValue()
        {
            var left = MustParse("core.List<core.String>");
            var same = MustParse("core.List< core.String >");
            var different = MustParse("core.List<core.Int>");

            Assert.AreEqual(left, same);
            Assert.IsTrue(left == same);
            Assert.IsFalse(left != same);
            Assert.AreEqual(left.GetHashCode(), same.GetHashCode());
            Assert.AreNotEqual(left, different);
            Assert.IsFalse(left.Equals(null));
        }

        [Test]
        public void ConstructorHoldsAnImmutableArgsCopy()
        {
            var mutable = new List<TypeRef>
            {
                new TypeRef("core.String", Array.Empty<TypeRef>()),
            };
            var type = new TypeRef("core.List", mutable);
            mutable.Add(new TypeRef("core.Int", Array.Empty<TypeRef>()));
            Assert.AreEqual(1, type.Args.Count);
        }

        [Test]
        public void ConstructorRejectsStructurallyInvalidShapes()
        {
            Assert.Throws<ArgumentException>(
                () => new TypeRef(null!, Array.Empty<TypeRef>()));
            Assert.Throws<ArgumentException>(
                () => new TypeRef("", Array.Empty<TypeRef>()));
            Assert.Throws<ArgumentException>(
                () => new TypeRef("type:X.Y", new[] { new TypeRef("a.B", Array.Empty<TypeRef>()) }));
            Assert.Throws<ArgumentException>(
                () => new TypeRef("a.B", new TypeRef[] { null! }));
        }
    }
}
