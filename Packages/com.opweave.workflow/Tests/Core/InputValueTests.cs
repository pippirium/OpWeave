#nullable enable

using System;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// [Core] tests for <see cref="InputValue"/> (SPEC §A0.1): "not supplied" is
    /// <see cref="InputValue.IsPresent"/> <c>= false</c>, kept distinct from a
    /// present value whose JSON is literal <c>null</c>. The same sources run
    /// under dotnet and Unity Mono (local:T-W1.3-05:missing-null-encoding).
    /// </summary>
    internal sealed class InputValueTests
    {
        private static JsonValue MustParse(string json)
        {
            var parsed = JsonValue.Parse(json);
            Assert.IsTrue(parsed.Ok, $"Fixture JSON was rejected: {json}");
            return parsed.Value!;
        }

        [Test]
        public void MissingIsAbsentAndDefaultStructIsMissing()
        {
            var missing = InputValue.Missing;
            Assert.IsFalse(missing.IsPresent);
            Assert.IsNull(missing.Value);
            Assert.AreEqual(missing, default(InputValue));
            Assert.IsTrue(missing == default(InputValue));
            Assert.IsFalse(missing != default(InputValue));
        }

        [Test]
        public void PresentKeepsEveryJsonValueIncludingLiteralNull()
        {
            var literalNull = InputValue.Present(MustParse("null"));
            Assert.IsTrue(literalNull.IsPresent,
                "a JsonValue of literal null is present — not Missing (SPEC §A0.1)");
            Assert.IsNotNull(literalNull.Value);
            Assert.AreEqual("null", literalNull.Value!.CanonicalText);
            Assert.AreNotEqual(literalNull, InputValue.Missing);
            Assert.IsTrue(literalNull != InputValue.Missing);

            var present = InputValue.Present(MustParse("{\"a\":[1,null]}"));
            Assert.IsTrue(present.IsPresent);
            Assert.AreEqual("{\"a\":[1,null]}", present.Value!.CanonicalText);
        }

        [Test]
        public void EqualityFollowsPresenceAndJsonValueEquality()
        {
            var one = InputValue.Present(MustParse("1"));
            var oneAgain = InputValue.Present(MustParse("1"));
            var two = InputValue.Present(MustParse("2"));

            Assert.AreEqual(one, oneAgain);
            Assert.AreEqual(one.GetHashCode(), oneAgain.GetHashCode());
            Assert.AreNotEqual(one, two);
            Assert.AreEqual(InputValue.Missing, InputValue.Missing);
            Assert.AreNotEqual(InputValue.Missing, one);
            Assert.IsFalse(one.Equals(null));
            Assert.IsFalse(one.Equals("not an InputValue"));
            Assert.IsTrue(one.Equals((object)oneAgain));
        }

        [Test]
        public void PresentRejectsANullJsonValueReference()
        {
            Assert.Throws<ArgumentNullException>(() => InputValue.Present(null!));
        }
    }
}
