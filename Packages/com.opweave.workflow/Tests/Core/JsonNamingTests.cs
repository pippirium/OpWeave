#nullable enable

using System;
using NUnit.Framework;
using OpWeave.Core;

namespace OpWeave.Core.Tests
{
    internal sealed class JsonNamingTests
    {
        [TestCase("FormatVersion", "formatVersion")]
        [TestCase("PlanId", "planId")]
        [TestCase("Sha256", "sha256")]
        [TestCase("NodeId", "nodeId")]
        [TestCase("URLValue", "urlValue")]
        [TestCase("A", "a")]
        [TestCase("planId", "planId")]
        [TestCase("", "")]
        public void ToCamelCaseConvertsPascalCaseMembers(string input, string expected)
        {
            Assert.AreEqual(expected, JsonNaming.ToCamelCase(input));
        }

        [TestCase("FromStep", "from_step")]
        [TestCase("FromInput", "from_input")]
        [TestCase("Constant", "constant")]
        [TestCase("ConstantOnly", "constant_only")]
        [TestCase("Default", "default")]
        [TestCase("Missing", "missing")]
        [TestCase("Queued", "queued")]
        [TestCase("IOOperation", "io_operation")]
        [TestCase("Utf8", "utf8")]
        [TestCase("Version2Value", "version2_value")]
        [TestCase("already_snake", "already_snake")]
        public void ToSnakeCaseConvertsPascalCaseEnumValues(string input, string expected)
        {
            Assert.AreEqual(expected, JsonNaming.ToSnakeCase(input));
        }

        [Test]
        public void SeverityEnumNamesBecomeSnakeCase()
        {
            Assert.AreEqual("error", JsonNaming.ToSnakeCase(Severity.Error.ToString()));
            Assert.AreEqual("warning", JsonNaming.ToSnakeCase(Severity.Warning.ToString()));
            Assert.AreEqual("info", JsonNaming.ToSnakeCase(Severity.Info.ToString()));
        }

        [Test]
        public void NullInputIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => JsonNaming.ToCamelCase(null!));
            Assert.Throws<ArgumentNullException>(() => JsonNaming.ToSnakeCase(null!));
        }
    }
}
