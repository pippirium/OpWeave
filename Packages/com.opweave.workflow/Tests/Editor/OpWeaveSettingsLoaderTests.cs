#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// [Edit] tests for the SPEC §H4.1 settings v2 loader: one fixture per row of the
    /// loading table, both v1→v2 migration rules, the debugLogging contract, the
    /// fail-closed settings-error state and unknown-key retention
    /// (local:T-W2.1-03:settings-loader).
    /// </summary>
    internal sealed class OpWeaveSettingsLoaderTests
    {
        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(
                Path.GetTempPath(), "OpWeaveSettingsLoaderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }

        private string WriteFixture(string name, string content)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            return path;
        }

        private static IEnumerable<Diagnostic> WithCode(Result<OpWeaveSettings> result, string code) =>
            result.Diagnostics.Where(d => d.Code == code);

        private static void AssertSingle(
            Result<OpWeaveSettings> result, string code, Severity severity, string? jsonPath = null)
        {
            var matches = WithCode(result, code).ToList();
            Assert.That(matches.Count, Is.EqualTo(1),
                $"Expected exactly one {code}, got: {string.Join(", ", result.Diagnostics.Select(d => d.Code))}");
            Assert.That(matches[0].Severity, Is.EqualTo(severity));
            if (jsonPath is not null)
            {
                Assert.That(matches[0].JsonPath, Is.EqualTo(jsonPath));
            }
        }

        // ── Loading table: missing file → all defaults, no diagnostics ─────────

        [Test]
        public void MissingFileYieldsAllDefaultsWithNoDiagnostics()
        {
            var result = OpWeaveSettingsLoader.Load(Path.Combine(_dir, "absent.json"));

            Assert.IsTrue(result.Ok);
            Assert.That(result.Diagnostics, Is.Empty);
            var settings = result.Value!;
            Assert.That(settings.MigratedFromV1, Is.False);
            Assert.That(settings.PreservedUnknownKeys, Is.Empty);
            Assert.That(settings.DiscoveryMode, Is.EqualTo(DiscoveryMode.Auto));
            Assert.That(settings.IntegrityLevel, Is.EqualTo(IntegrityLevel.Weak));
            Assert.That(settings.WhitelistedOperations, Is.Empty);
            Assert.That(settings.WhitelistedTypeProviders, Is.Empty);
            Assert.That(settings.DestructiveRequiresAsk, Is.True);
            Assert.That(settings.DebugLogging, Is.False);
            Assert.That(settings.AgentLanguage, Is.EqualTo("en"));
            Assert.That(settings.TickBudgetMs, Is.EqualTo(50));
            Assert.That(settings.RetainRuns, Is.EqualTo(50));
            Assert.That(settings.RetainMegabytes, Is.EqualTo(200));
            Assert.That(settings.ApprovalTimeoutMinutes, Is.EqualTo(1440));
            Assert.That(settings.AllowedPaths, Is.EqualTo(new[] { "Assets/" }));
            Assert.That(settings.CodePaths, Is.EqualTo(new[] { "Assets/Scripts/" }));
            Assert.That(settings.CustomOperationFolders,
                Is.EqualTo(new[] { "Assets/OpWeave/Editor/Operations/" }));
            Assert.That(settings.AllowedPackageSources,
                Is.EqualTo(new[] { "registry:com.unity.*" }));
            Assert.That(settings.DisabledOperations, Is.Empty);
            // §H4 example defaults: Agent asks on move/delete/code.write/package.
            Assert.That(settings.AgentPolicy["asset.move"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(settings.AgentPolicy["asset.delete"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(settings.AgentPolicy["code.write"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(settings.AgentPolicy["package.modify"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(settings.AgentPolicy["read"], Is.EqualTo(PolicyLevel.Allow));
            Assert.That(settings.AgentPolicy["compile"], Is.EqualTo(PolicyLevel.Allow));
            Assert.That(settings.AgentPolicy["code.template"], Is.EqualTo(PolicyLevel.Allow));
            // Human defaults: allow except delete/code.write/package.
            Assert.That(settings.HumanPolicy["asset.move"], Is.EqualTo(PolicyLevel.Allow));
            Assert.That(settings.HumanPolicy["asset.delete"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(settings.HumanPolicy["code.write"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(settings.HumanPolicy["package.modify"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(settings.HumanPolicy["read"], Is.EqualTo(PolicyLevel.Allow));
        }

        [Test]
        public void MissingParentDirectoryYieldsDefaultsWithNoDiagnostics()
        {
            var result = OpWeaveSettingsLoader.Load(
                Path.Combine(_dir, "absent-parent", "OpWeaveSettings.json"));

            Assert.IsTrue(result.Ok);
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(result.Value, Is.SameAs(OpWeaveSettings.Default));
        }

        // ── Loading table: unreadable settings file → OW-CFG-001, never defaults ─

        [Test]
        public void DirectoryAtSettingsPathYieldsOwCfg001()
        {
            var result = OpWeaveSettingsLoader.Load(_dir);

            Assert.IsFalse(result.Ok);
            AssertSingle(result, ErrorCodes.OW_CFG_001, Severity.Error, "$");
        }

        // ── Loading table: unparseable JSON → OW-CFG-001 ───────────────────────

        [TestCase("{")]
        [TestCase("{\"version\":2,}")]
        [TestCase("not json")]
        [TestCase("")]
        [TestCase("[1,2,3]")]
        [TestCase("\"text\"")]
        public void UnreadableSettingsFileYieldsOwCfg001(string json)
        {
            var result = OpWeaveSettingsLoader.Parse(json);

            Assert.IsFalse(result.Ok, "Settings error must surface as a failed Result.");
            AssertSingle(result, ErrorCodes.OW_CFG_001, Severity.Error);
        }

        // ── Loading table: version 2 read as-is ────────────────────────────────

        [Test]
        public void Version2FileIsReadAsIs()
        {
            var result = OpWeaveSettingsLoader.Parse(@"{
  ""version"": 2,
  ""policy"": {
    ""agent"": { ""asset.delete"": ""deny"", ""code.write"": ""deny"" },
    ""human"": { ""compile"": ""deny"" }
  },
  ""destructiveRequiresAsk"": false,
  ""extensions"": {
    ""discoveryMode"": ""whitelist"",
    ""integrityLevel"": ""weak"",
    ""whitelist"": {
      ""operations"": [
        { ""packageId"": ""@project"", ""assemblyName"": ""My.Ops.Editor"",
          ""operationId"": ""my.op"", ""version"": 3 }
      ],
      ""typeProviders"": [
        { ""packageId"": ""com.example.types"", ""providerId"": ""example.types"" }
      ]
    }
  },
  ""allowedPaths"": [""Assets/Game/""],
  ""codePaths"": [""Assets/Game/Code/""],
  ""customOperationFolders"": [""Assets/Ops/""],
  ""allowedPackageSources"": [""registry:com.unity.*"", ""git:https://example.com/r.git""],
  ""disabledOperations"": [""my.op""],
  ""agentLanguage"": ""ja"",
  ""debugLogging"": true,
  ""tickBudgetMs"": 16,
  ""retainRuns"": 5,
  ""retainMegabytes"": 10,
  ""approvalTimeoutMinutes"": 60
}");

            Assert.IsTrue(result.Ok);
            Assert.That(result.Diagnostics, Is.Empty);
            var settings = result.Value!;
            Assert.That(settings.MigratedFromV1, Is.False);
            Assert.That(settings.AgentPolicy["asset.delete"], Is.EqualTo(PolicyLevel.Deny));
            Assert.That(settings.AgentPolicy["code.write"], Is.EqualTo(PolicyLevel.Deny));
            Assert.That(settings.HumanPolicy["compile"], Is.EqualTo(PolicyLevel.Deny));
            Assert.That(settings.DestructiveRequiresAsk, Is.False);
            Assert.That(settings.DiscoveryMode, Is.EqualTo(DiscoveryMode.Whitelist));
            Assert.That(settings.IntegrityLevel, Is.EqualTo(IntegrityLevel.Weak));
            Assert.That(settings.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("@project", "My.Ops.Editor", "my.op", 3),
            }));
            Assert.That(settings.WhitelistedTypeProviders, Is.EqualTo(new[]
            {
                new TypeProviderWhitelistEntry("com.example.types", "example.types"),
            }));
            Assert.That(settings.AllowedPaths, Is.EqualTo(new[] { "Assets/Game/" }));
            Assert.That(settings.CodePaths, Is.EqualTo(new[] { "Assets/Game/Code/" }));
            Assert.That(settings.CustomOperationFolders, Is.EqualTo(new[] { "Assets/Ops/" }));
            Assert.That(settings.AllowedPackageSources,
                Is.EqualTo(new[] { "registry:com.unity.*", "git:https://example.com/r.git" }));
            Assert.That(settings.DisabledOperations, Is.EqualTo(new[] { "my.op" }));
            Assert.That(settings.AgentLanguage, Is.EqualTo("ja"));
            Assert.That(settings.DebugLogging, Is.True);
            Assert.That(settings.TickBudgetMs, Is.EqualTo(16));
            Assert.That(settings.RetainRuns, Is.EqualTo(5));
            Assert.That(settings.RetainMegabytes, Is.EqualTo(10));
            Assert.That(settings.ApprovalTimeoutMinutes, Is.EqualTo(60));
        }

        // ── Loading table: unknown version → OW-CFG-001 ────────────────────────

        [TestCase("3")]
        [TestCase("99")]
        [TestCase("0")]
        [TestCase("-1")]
        [TestCase("\"2\"")]
        [TestCase("2.0")]
        [TestCase("null")]
        public void UnknownVersionYieldsOwCfg001(string version)
        {
            var result = OpWeaveSettingsLoader.Parse("{\"version\":" + version + "}");

            Assert.IsFalse(result.Ok);
            AssertSingle(result, ErrorCodes.OW_CFG_001, Severity.Error, "$.version");
        }

        // ── Loading table: invalid value of a known key → OW-CFG-002 + JsonPath ─

        [Test]
        public void InvalidKnownKeyValuesYieldOwCfg002WithJsonPath()
        {
            // Every documented invalid-value shape: unknown enum, wrong type,
            // wildcard, empty string and missing whitelist key (SPEC §H4.1).
            var cases = new (string Json, string Path)[]
            {
                ("{\"version\":2,\"extensions\":{\"discoveryMode\":\"manual\"}}",
                    "$.extensions.discoveryMode"),
                ("{\"version\":2,\"extensions\":{\"discoveryMode\":1}}",
                    "$.extensions.discoveryMode"),
                ("{\"version\":2,\"extensions\":{\"integrityLevel\":\"strong\"}}",
                    "$.extensions.integrityLevel"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":\"x\"}}",
                    "$.extensions.whitelist"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":\"x\"}}}",
                    "$.extensions.whitelist.operations"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[7]}}}",
                    "$.extensions.whitelist.operations[0]"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[{\"packageId\":\"*\",\"assemblyName\":\"A\",\"operationId\":\"o\",\"version\":1}]}}}",
                    "$.extensions.whitelist.operations[0].packageId"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[{\"packageId\":\"p\",\"assemblyName\":\"\",\"operationId\":\"o\",\"version\":1}]}}}",
                    "$.extensions.whitelist.operations[0].assemblyName"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[{\"packageId\":\"p\",\"assemblyName\":\"A\",\"version\":1}]}}}",
                    "$.extensions.whitelist.operations[0].operationId"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[{\"packageId\":\"p\",\"assemblyName\":\"A\",\"operationId\":\"o\",\"version\":\"1\"}]}}}",
                    "$.extensions.whitelist.operations[0].version"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[{\"packageId\":\"p\",\"assemblyName\":\"A\",\"operationId\":\"o\",\"version\":0}]}}}",
                    "$.extensions.whitelist.operations[0].version"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":{\"typeProviders\":[{\"packageId\":\"p\"}]}}}",
                    "$.extensions.whitelist.typeProviders[0].providerId"),
                ("{\"version\":2,\"extensions\":{\"whitelist\":{\"typeProviders\":[{\"packageId\":\"p\",\"providerId\":3}]}}}",
                    "$.extensions.whitelist.typeProviders[0].providerId"),
                ("{\"version\":2,\"policy\":{\"agent\":{\"asset.delete\":\"maybe\"}}}",
                    "$.policy.agent[\"asset.delete\"]"),
                ("{\"version\":2,\"policy\":{\"agent\":{\"asset.delete\":true}}}",
                    "$.policy.agent[\"asset.delete\"]"),
                ("{\"version\":2,\"policy\":{\"agent\":\"allow\"}}",
                    "$.policy.agent"),
                ("{\"version\":2,\"policy\":7}",
                    "$.policy"),
                ("{\"version\":2,\"extensions\":null}",
                    "$.extensions"),
                ("{\"version\":2,\"allowedPaths\":\"Assets/\"}",
                    "$.allowedPaths"),
                ("{\"version\":2,\"allowedPaths\":[1]}",
                    "$.allowedPaths[0]"),
                ("{\"version\":2,\"allowedPackageSources\":[42]}",
                    "$.allowedPackageSources[0]"),
                ("{\"version\":2,\"disabledOperations\":[null]}",
                    "$.disabledOperations[0]"),
                ("{\"version\":2,\"agentLanguage\":1}",
                    "$.agentLanguage"),
                ("{\"version\":2,\"destructiveRequiresAsk\":\"yes\"}",
                    "$.destructiveRequiresAsk"),
                ("{\"version\":2,\"tickBudgetMs\":\"50\"}",
                    "$.tickBudgetMs"),
                ("{\"version\":2,\"tickBudgetMs\":50.5}",
                    "$.tickBudgetMs"),
                ("{\"version\":2,\"retainRuns\":null}",
                    "$.retainRuns"),
            };
            foreach (var (json, expectedPath) in cases)
            {
                var result = OpWeaveSettingsLoader.Parse(json);
                Assert.IsFalse(result.Ok, $"Expected settings error for {json}");
                AssertSingle(result, ErrorCodes.OW_CFG_002, Severity.Error, expectedPath);
            }
        }

        [Test]
        public void MultipleInvalidKeysYieldOneOwCfg002Each()
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"debugLogging\":\"x\",\"retainRuns\":\"y\"}");

            Assert.IsFalse(result.Ok);
            var errors = WithCode(result, ErrorCodes.OW_CFG_002).ToList();
            Assert.That(errors.Select(d => d.JsonPath),
                Is.EqualTo(new[] { "$.debugLogging", "$.retainRuns" }));
        }

        // ── Loading table: unknown key → OW-CFG-003 warning, retained ──────────

        [Test]
        public void UnknownKeysWarnAndAreRetainedForHumanSave()
        {
            var result = OpWeaveSettingsLoader.Parse(@"{
  ""version"": 2,
  ""debugLogging"": true,
  ""futureTop"": { ""nested"": 1 },
  ""policy"": { ""agent"": { ""teleport"": ""allow"", ""read"": ""deny"" }, ""other"": 2 },
  ""extensions"": { ""future"": 3, ""whitelist"": { ""future"": 4,
      ""operations"": [ { ""packageId"": ""p"", ""assemblyName"": ""A"",
        ""operationId"": ""o"", ""version"": 1, ""note"": ""keep"" } ] } }
}");

            Assert.IsTrue(result.Ok);
            var settings = result.Value!;
            var warnings = WithCode(result, ErrorCodes.OW_CFG_003).ToList();
            Assert.That(
                warnings.Select(d => d.JsonPath),
                Is.EquivalentTo(new[]
                {
                    "$.futureTop",
                    "$.policy.agent.teleport",
                    "$.policy.other",
                    "$.extensions.future",
                    "$.extensions.whitelist.future",
                    "$.extensions.whitelist.operations[0].note",
                }));
            Assert.That(warnings.All(d => d.Severity == Severity.Warning), Is.True);
            // Ignored but retained in memory for a later human save.
            Assert.That(settings.PreservedUnknownKeys["$.futureTop"].CanonicalText,
                Is.EqualTo("{\"nested\":1}"));
            Assert.That(settings.PreservedUnknownKeys["$.policy.agent.teleport"].CanonicalText,
                Is.EqualTo("\"allow\""));
            Assert.That(settings.PreservedUnknownKeys["$.extensions.whitelist.operations[0].note"].CanonicalText,
                Is.EqualTo("\"keep\""));
            // Known keys still take effect.
            Assert.That(settings.DebugLogging, Is.True);
            Assert.That(settings.AgentPolicy["read"], Is.EqualTo(PolicyLevel.Deny));
            Assert.That(settings.WhitelistedOperations.Count, Is.EqualTo(1));
        }

        // ── Loading table: integrityLevel "full" is a valid value ──────────────

        [Test]
        public void IntegrityLevelFullLoadsWithoutError()
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"extensions\":{\"integrityLevel\":\"full\"}}");

            Assert.IsTrue(result.Ok);
            Assert.That(result.Value!.IntegrityLevel, Is.EqualTo(IntegrityLevel.Full));
            Assert.That(result.Diagnostics, Is.Empty);
        }

        // ── Loading table: agent code.write "allow" is read as ask; the human
        //    code.write settings range is ask/deny only (SPEC §C4/§H4, SG-18) ──

        [Test]
        public void AgentCodeWriteAllowIsReadAsAsk()
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"policy\":{\"agent\":{\"code.write\":\"allow\"}}}");

            Assert.IsTrue(result.Ok);
            Assert.That(result.Value!.AgentPolicy["code.write"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(
                result.Diagnostics.Any(d => d.Severity == Severity.Error), Is.False);
        }

        [Test]
        public void HumanCodeWriteAllowYieldsOwCfg002()
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"policy\":{\"human\":{\"code.write\":\"allow\"}}}");

            Assert.IsFalse(result.Ok);
            AssertSingle(
                result, ErrorCodes.OW_CFG_002, Severity.Error, "$.policy.human[\"code.write\"]");
        }

        [TestCase("ask", PolicyLevel.Ask)]
        [TestCase("deny", PolicyLevel.Deny)]
        public void HumanCodeWriteAcceptsAskAndDeny(string value, PolicyLevel expected)
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"policy\":{\"human\":{\"code.write\":\"" + value + "\"}}}");

            Assert.IsTrue(result.Ok);
            Assert.That(result.Value!.HumanPolicy["code.write"], Is.EqualTo(expected));
        }

        // ── Required test: debugLogging contract (SPEC §H4.1) ──────────────────

        [TestCase("null")]
        [TestCase("1")]
        [TestCase("\"true\"")]
        [TestCase("[]")]
        public void DebugLoggingAcceptsOnlyBooleans(string value)
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"debugLogging\":" + value + "}");

            Assert.IsFalse(result.Ok);
            AssertSingle(result, ErrorCodes.OW_CFG_002, Severity.Error, "$.debugLogging");
        }

        [TestCase("true", true)]
        [TestCase("false", false)]
        public void DebugLoggingReadsBooleans(string value, bool expected)
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"debugLogging\":" + value + "}");

            Assert.IsTrue(result.Ok);
            Assert.That(result.Value!.DebugLogging, Is.EqualTo(expected));
        }

        // ── Migration: absent or 1 version → v1 read + OW-CFG-004, file untouched ─

        [TestCase("{\"debugLogging\":true}", TestName = "MissingVersionMigratesAsV1")]
        [TestCase("{\"version\":1,\"debugLogging\":true}", TestName = "Version1MigratesAsV1")]
        public void V1FileMigratesInMemoryWithOwCfg004(string json)
        {
            var path = WriteFixture("OpWeaveSettings.json", json);
            var before = File.ReadAllBytes(path);

            var result = OpWeaveSettingsLoader.Load(path);

            Assert.IsTrue(result.Ok);
            AssertSingle(result, ErrorCodes.OW_CFG_004, Severity.Warning);
            var settings = result.Value!;
            Assert.That(settings.MigratedFromV1, Is.True);
            // Other keys keep their v2 meaning during migration.
            Assert.That(settings.DebugLogging, Is.True);
            // Migration is in-memory only: the file is untouched.
            Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
        }

        [Test]
        public void CustomOperationTrustAutoMigratesToDiscoveryModeAuto()
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"customOperationTrust\":\"auto\"}");

            Assert.IsTrue(result.Ok);
            Assert.That(result.Value!.MigratedFromV1, Is.True);
            Assert.That(result.Value!.DiscoveryMode, Is.EqualTo(DiscoveryMode.Auto));
            AssertSingle(result, ErrorCodes.OW_CFG_004, Severity.Warning);
        }

        [Test]
        public void CustomOperationTrustReviewMigratesToEmptyWhitelistMode()
        {
            // Even a populated extensions.whitelist ends empty: review is migrated
            // safe-side so no extension is registered until a human lists it.
            var result = OpWeaveSettingsLoader.Parse(@"{
  ""version"": 1,
  ""customOperationTrust"": ""review"",
  ""extensions"": { ""whitelist"": {
    ""operations"": [ { ""packageId"": ""p"", ""assemblyName"": ""A"",
      ""operationId"": ""o"", ""version"": 1 } ] } }
}");

            Assert.IsTrue(result.Ok);
            var settings = result.Value!;
            Assert.That(settings.DiscoveryMode, Is.EqualTo(DiscoveryMode.Whitelist));
            Assert.That(settings.WhitelistedOperations, Is.Empty);
            Assert.That(settings.WhitelistedTypeProviders, Is.Empty);
            AssertSingle(result, ErrorCodes.OW_CFG_004, Severity.Warning);
        }

        [Test]
        public void InvalidCustomOperationTrustYieldsOwCfg002()
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"version\":1,\"customOperationTrust\":\"strict\"}");

            Assert.IsFalse(result.Ok);
            AssertSingle(result, ErrorCodes.OW_CFG_002, Severity.Error, "$.customOperationTrust");
        }

        [Test]
        public void CustomOperationTrustInV2FileIsAnUnknownKey()
        {
            var result = OpWeaveSettingsLoader.Parse(
                "{\"version\":2,\"customOperationTrust\":\"review\"}");

            Assert.IsTrue(result.Ok);
            AssertSingle(result, ErrorCodes.OW_CFG_003, Severity.Warning, "$.customOperationTrust");
            // The v1-only key cannot steer a v2 file; it is retained for human save.
            Assert.That(result.Value!.DiscoveryMode, Is.EqualTo(DiscoveryMode.Auto));
            Assert.That(result.Value!.PreservedUnknownKeys["$.customOperationTrust"].CanonicalText,
                Is.EqualTo("\"review\""));
        }

        // ── Settings-error state: failed Result is what the Registry builder and
        //    Runner host consume (standard Operations only, no new Runs) ────────

        [Test]
        public void SettingsErrorProducesFailedResultWithCfgDiagnostics()
        {
            var result = OpWeaveSettingsLoader.Parse("{\"version\":2,\"debugLogging\":1}");

            Assert.IsFalse(result.Ok);
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Diagnostics, Is.Not.Empty);
            Assert.That(
                result.Diagnostics.All(d =>
                    d.Code == ErrorCodes.OW_CFG_002 || d.Code == ErrorCodes.OW_CFG_003),
                Is.True);
        }
    }
}
