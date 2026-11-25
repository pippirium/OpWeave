#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.UnityAdapter;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// [Edit] fixtures for the SPEC §H4/§H4.1 human save path
    /// (<see cref="OpWeaveSettingsDocument"/>): saving patches the original parsed
    /// JSON tree — it writes <c>version: 2</c> with the migrated or explicitly
    /// edited values, keeps unknown keys byte-equivalent in value (CanonicalText
    /// equality), never replaces invalid or unreadable content with defaults,
    /// blocks the lossless-impossible v1 saves behind an explicit human
    /// resolution, and gates the OW-CFG-001 state behind the explicit replace
    /// confirmation (local:T-W3.4-04:settings-ui-save; rule table T01–T29).
    /// </summary>
    internal sealed class OpWeaveSettingsDocumentTests
    {
        private string _dir = "";

        [SetUp]
        public void SetUp()
        {
            _dir = Path.Combine(
                Path.GetTempPath(), "OpWeaveSettingsDocumentTests", Guid.NewGuid().ToString("N"));
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

        private string WriteFixture(string content)
        {
            var path = Path.Combine(_dir, "OpWeaveSettings.json");
            File.WriteAllText(path, content);
            return path;
        }

        private static Result<OpWeaveSettings> Reload(string path) =>
            OpWeaveSettingsLoader.Load(path);

        private static string VersionText(string path) =>
            File.ReadAllText(path);

        private static JObject SavedRoot(string path) =>
            (JObject)JsonValue.Parse(File.ReadAllText(path)).Value!.ToToken();

        private static string Canon(JToken? token)
        {
            Assert.IsNotNull(token);
            return JsonValue.From(token!).CanonicalText;
        }

        private static string CanonOf(string json) =>
            JsonValue.Parse(json).Value!.CanonicalText;

        // Asserts the typed save outcome is Saved and returns its post-save load
        // result (a failed result means the written file still holds invalid
        // content the human did not fix).
        private static Result<OpWeaveSettings> Saved(OpWeaveSettingsSaveOutcome outcome)
        {
            Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.Saved));
            return outcome.Result!.Value;
        }

        private static OpWeaveSettingsSaveOutcome Blocked(
            OpWeaveSettingsSaveOutcome outcome)
        {
            Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.Blocked));
            Assert.That(outcome.FailureMessage, Is.Not.Null.And.Not.Empty);
            return outcome;
        }

        private static List<string> DiagnosticPaths(
            Result<OpWeaveSettings> result, string code) =>
            result.Diagnostics
                .Where(d => d.Code == code)
                .Select(d => d.JsonPath ?? "")
                .ToList();

        // ── Missing file: defaults in memory, save writes a complete v2 file ───

        [Test]
        public void MissingFileLoadsDefaultsAndSavesAsV2()
        {
            var path = Path.Combine(_dir, "absent", "OpWeaveSettings.json");
            var document = OpWeaveSettingsDocument.Load(path);

            Assert.IsFalse(document.FileExists);
            Assert.IsTrue(document.LoadResult.Ok);
            Assert.IsFalse(document.RequiresReplaceConfirmation);
            Assert.IsFalse(document.SourceWasV1);
            Assert.That(document.SaveBlockReasons, Is.Empty);
            Assert.IsTrue(document.CanSave(replaceConfirmed: false));
            Assert.IsTrue(document.TryGetDiscoveryMode(out var mode));
            Assert.That(mode, Is.EqualTo(DiscoveryMode.Auto));

            var saved = Saved(document.Save());

            Assert.IsTrue(saved.Ok);
            Assert.That(VersionText(path), Does.Contain("\"version\": 2"));
            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.RetainRuns, Is.EqualTo(50));
            Assert.That(reloaded.Value.ApprovalTimeoutMinutes, Is.EqualTo(1440));
        }

        // ── T01: v1 "auto" migrates to extensions.discoveryMode ──────────────

        [Test]
        public void V1AutoTrustSavesAsV2WithMigratedMode()
        {
            var path = WriteFixture("{\"customOperationTrust\":\"auto\",\"retainRuns\":7}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.SourceWasV1);
            Assert.That(document.SaveBlockReasons, Is.Empty);
            Assert.IsTrue(document.TryGetDiscoveryMode(out var effective));
            Assert.That(effective, Is.EqualTo(DiscoveryMode.Auto));
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var root = SavedRoot(path);
            Assert.That(Canon(root["version"]), Is.EqualTo("2"));
            Assert.IsNull(root["customOperationTrust"]);
            var extensions = (JObject)root["extensions"]!;
            Assert.That(Canon(extensions["discoveryMode"]), Is.EqualTo("\"auto\""));
            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.DiscoveryMode, Is.EqualTo(DiscoveryMode.Auto));
            Assert.That(reloaded.Value.RetainRuns, Is.EqualTo(7));
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004),
                Is.False, "A saved v2 file must not re-emit the migration warning.");
        }

        // ── T02: v1 "review" migrates to whitelist mode, whitelist absent ────

        [Test]
        public void V1ReviewTrustSavesAsV2WithEmptyWhitelist()
        {
            var path = WriteFixture(
                "{\"version\":1,\"customOperationTrust\":\"review\",\"debugLogging\":true}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.SourceWasV1);
            Assert.IsFalse(document.V1WhitelistResolutionRequired,
                "No stored whitelist elements means nothing to resolve.");
            Assert.IsTrue(document.TryGetDiscoveryMode(out var effective));
            Assert.That(effective, Is.EqualTo(DiscoveryMode.Whitelist));
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var root = SavedRoot(path);
            Assert.That(Canon(root["version"]), Is.EqualTo("2"));
            Assert.IsNull(root["customOperationTrust"]);
            var extensions = (JObject)root["extensions"]!;
            Assert.That(Canon(extensions["discoveryMode"]), Is.EqualTo("\"whitelist\""));
            Assert.IsNull(extensions["whitelist"],
                "The migrated whitelist stays absent (defaults apply), never written as empty arrays.");
            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            var settings = reloaded.Value!;
            Assert.IsFalse(settings.MigratedFromV1);
            Assert.That(settings.DiscoveryMode, Is.EqualTo(DiscoveryMode.Whitelist));
            Assert.That(settings.WhitelistedOperations, Is.Empty);
            Assert.That(settings.DebugLogging, Is.True);
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004), Is.False);
        }

        // ── T03: v1 without trust keeps every other member verbatim ──────────

        [Test]
        public void V1WithoutTrustKeepsOtherKeysAndStoredWhitelist()
        {
            var path = WriteFixture(
                "{\"version\":1,\"extensions\":{\"discoveryMode\":\"whitelist\"," +
                "\"whitelist\":{\"operations\":[{\"packageId\":\"p\",\"assemblyName\":\"A\"," +
                "\"operationId\":\"o\",\"version\":1}]}},\"tickBudgetMs\":16,\"future\":1}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.SourceWasV1);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            var settings = reloaded.Value!;
            Assert.That(settings.TickBudgetMs, Is.EqualTo(16));
            Assert.That(settings.DiscoveryMode, Is.EqualTo(DiscoveryMode.Whitelist));
            Assert.That(settings.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("p", "A", "o", 1),
            }));
            Assert.That(
                settings.PreservedUnknownKeys["$.future"].CanonicalText, Is.EqualTo("1"));
        }

        // ── T04/T05: an invalid v1 trust blocks the save until resolved ──────

        [TestCase("\"whitelist\"")]
        [TestCase("\"manual\"")]
        [TestCase("\"\"")]
        [TestCase("\"on\"")]
        [TestCase("5")]
        [TestCase("true")]
        [TestCase("null")]
        [TestCase("{\"a\":1}")]
        [TestCase("[\"auto\"]")]
        public void V1InvalidTrustBlocksSaveUntilHumanChoosesMode(string trustJson)
        {
            var fixture = "{\"version\":1,\"customOperationTrust\":" + trustJson +
                ",\"debugLogging\":true}";
            var path = WriteFixture(fixture);

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.SourceWasV1);
            Assert.IsFalse(document.LoadResult.Ok);
            Assert.IsTrue(document.DiscoveryModeChoiceRequired);
            Assert.That(document.SaveBlockReasons, Is.Not.Empty);
            Assert.IsFalse(document.CanSave(replaceConfirmed: false));

            var blocked = Blocked(document.Save());
            Assert.That(blocked.Diagnostics.Any(d =>
                d.Code == ErrorCodes.OW_CFG_002 && d.JsonPath == "$.customOperationTrust"),
                Is.True, trustJson);
            Assert.That(VersionText(path), Is.EqualTo(fixture),
                "A blocked save must leave the file untouched: " + trustJson);

            var reloaded = Reload(path);
            Assert.IsFalse(reloaded.Ok);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.customOperationTrust" }), trustJson);
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004),
                Is.True, "A v1 file keeps its migration warning: " + trustJson);
        }

        // ── T06: the explicit mode choice resolves the blocked v1 save ───────

        [Test]
        public void V1InvalidTrustResolvedByExplicitModeChoice()
        {
            var fixture =
                "{\"version\":1,\"customOperationTrust\":\"whitelist\",\"debugLogging\":true}";
            var path = WriteFixture(fixture);

            var document = OpWeaveSettingsDocument.Load(path);
            Blocked(document.Save());
            Assert.That(VersionText(path), Is.EqualTo(fixture),
                "A blocked save preserves the file's original bytes.");

            document.SetDiscoveryMode(DiscoveryMode.Whitelist);
            Assert.That(document.SaveBlockReasons, Is.Empty);
            Assert.IsFalse(document.DiscoveryModeChoiceRequired);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var root = SavedRoot(path);
            Assert.IsNull(root["customOperationTrust"],
                "The explicit choice consumes the trust key (P-1).");
            var extensions = (JObject)root["extensions"]!;
            Assert.That(Canon(extensions["discoveryMode"]), Is.EqualTo("\"whitelist\""));
            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.DiscoveryMode, Is.EqualTo(DiscoveryMode.Whitelist));
            Assert.That(reloaded.Value.DebugLogging, Is.True);
        }

        // ── T07/T11: a stored discoveryMode conflicts with a valid trust ─────

        [Test]
        public void V1TrustConflictingInvalidStoredModeBlocksSave()
        {
            var fixture = "{\"version\":1,\"customOperationTrust\":\"auto\"," +
                "\"extensions\":{\"discoveryMode\":\"manual\"}}";
            var path = WriteFixture(fixture);

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.DiscoveryModeChoiceRequired);
            Blocked(document.Save());
            Assert.That(VersionText(path), Is.EqualTo(fixture));

            var reloaded = Reload(path);
            Assert.IsFalse(reloaded.Ok);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.extensions.discoveryMode" }));
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004), Is.True);

            // The human's explicit choice resolves the conflict and writes v2.
            document.SetDiscoveryMode(DiscoveryMode.Whitelist);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);
            var root = SavedRoot(path);
            Assert.IsNull(root["customOperationTrust"]);
            Assert.That(Canon(((JObject)root["extensions"]!)["discoveryMode"]),
                Is.EqualTo("\"whitelist\""));
        }

        [Test]
        public void V1TrustConflictingValidStoredModeBlocksSave()
        {
            var fixture = "{\"version\":1,\"customOperationTrust\":\"auto\"," +
                "\"extensions\":{\"discoveryMode\":\"whitelist\"}}";
            var path = WriteFixture(fixture);

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.DiscoveryModeChoiceRequired);
            Blocked(document.Save());
            Assert.That(VersionText(path), Is.EqualTo(fixture));

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok,
                "The conflict is a save blocker, not a load error (P-2).");
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004), Is.True);
            Assert.That(reloaded.Value!.DiscoveryMode, Is.EqualTo(DiscoveryMode.Auto),
                "The merged loader lets the valid trust win while the file stays v1.");
        }

        // ── T08: every stored OW-CFG-002 stays put while the save is blocked ─

        [Test]
        public void V1TrustConflictBlocksSaveWithoutMovingStoredErrors()
        {
            var fixture = "{\"version\":1,\"customOperationTrust\":\"auto\"," +
                "\"extensions\":{\"discoveryMode\":\"bad\",\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p\",\"assemblyName\":\"A\",\"operationId\":\"*\"," +
                "\"version\":1}]}}}";
            var path = WriteFixture(fixture);

            var document = OpWeaveSettingsDocument.Load(path);
            Blocked(document.Save());
            Assert.That(VersionText(path), Is.EqualTo(fixture));

            var reloaded = Reload(path);
            Assert.IsFalse(reloaded.Ok);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_002),
                Is.EquivalentTo(new[]
                {
                    "$.extensions.discoveryMode",
                    "$.extensions.whitelist.operations[0].operationId",
                }));
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004), Is.True);
        }

        // ── T09/T10/T09b/T28: review migration meets stored whitelist entries ─

        private const string ReviewWithNotedEntryFixture =
            "{\"version\":1,\"customOperationTrust\":\"review\",\"extensions\":{\"whitelist\":{" +
            "\"operations\":[{\"packageId\":\"p\",\"assemblyName\":\"A\",\"operationId\":\"o\"," +
            "\"version\":1,\"note\":\"keep\"}]}}}";

        [Test]
        public void V1ReviewWithStoredWhitelistEntriesBlocksSave()
        {
            var path = WriteFixture(ReviewWithNotedEntryFixture);

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.SourceWasV1);
            Assert.IsTrue(document.V1WhitelistResolutionRequired);
            Assert.IsFalse(document.CanSave(replaceConfirmed: false));
            Blocked(document.Save());
            Assert.That(VersionText(path), Is.EqualTo(ReviewWithNotedEntryFixture));

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok,
                "A valid stored entry produces no OW-CFG-002 (finding 4).");
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].note" }));
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004), Is.True);
            Assert.That(reloaded.Value!.WhitelistedOperations, Is.Empty,
                "Review migration keeps the effective whitelist empty until resolved.");
        }

        [Test]
        public void V1ReviewKeepEntriesWritesThemVerbatim()
        {
            var path = WriteFixture(ReviewWithNotedEntryFixture);

            var document = OpWeaveSettingsDocument.Load(path);
            document.KeepV1WhitelistEntries();
            Assert.IsFalse(document.V1WhitelistResolutionRequired);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var root = SavedRoot(path);
            Assert.IsNull(root["customOperationTrust"]);
            var operations = (JArray)((JObject)((JObject)root["extensions"]!)["whitelist"]!)["operations"]!;
            var entry = (JObject)operations[0];
            Assert.That(Canon(entry["note"]), Is.EqualTo("\"keep\""),
                "The kept entry's unknown property is byte-equivalent in value.");
            Assert.That(Canon(entry["operationId"]), Is.EqualTo("\"o\""));

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004),
                Is.False, "The saved file is v2 now.");
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].note" }));
            Assert.That(reloaded.Value!.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("p", "A", "o", 1),
            }), "The kept entries become active under whitelist mode.");
        }

        [Test]
        public void V1ReviewEntryRemovalIsDisabledOnlyWithUnknownProperties()
        {
            var path = WriteFixture(
                "{\"version\":1,\"customOperationTrust\":\"review\",\"extensions\":{\"whitelist\":{" +
                "\"operations\":[{\"packageId\":\"p\",\"assemblyName\":\"A\",\"operationId\":\"o\"," +
                "\"version\":1,\"note\":\"keep\"}," +
                "{\"packageId\":\"p2\",\"assemblyName\":\"A2\",\"operationId\":\"o2\"," +
                "\"version\":2}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.Operations);
            Assert.That(items.Count, Is.EqualTo(2));
            Assert.IsFalse(items[0].CanRemove,
                "Removal is disabled for the entry carrying unknown keys.");
            Assert.IsTrue(items[1].CanRemove);
            Assert.Throws<InvalidOperationException>(
                () => document.RemoveWhitelistItem(items[0]));
            document.RemoveWhitelistItem(items[1]);
            Assert.IsTrue(document.V1WhitelistResolutionRequired,
                "The entry with unknown keys is still pending after the clean removal.");

            document.KeepV1WhitelistEntries();
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var operations = (JArray)((JObject)((JObject)SavedRoot(path)["extensions"]!)
                ["whitelist"]!)["operations"]!;
            Assert.That(operations.Count, Is.EqualTo(1));
            Assert.That(Canon(((JObject)operations[0])["note"]), Is.EqualTo("\"keep\""));
            var reloaded = Reload(path);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].note" }));
            Assert.That(reloaded.Value!.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("p", "A", "o", 1),
            }));
        }

        [Test]
        public void V1ReviewWhitelistBlockIsNotBypassedByModeEdit()
        {
            var path = WriteFixture(ReviewWithNotedEntryFixture);

            var document = OpWeaveSettingsDocument.Load(path);
            document.SetDiscoveryMode(DiscoveryMode.Whitelist);
            Assert.IsTrue(document.V1WhitelistResolutionRequired,
                "An explicit discoveryMode edit never bypasses the entry resolution (V1-10a).");
            Blocked(document.Save());
            Assert.That(VersionText(path), Is.EqualTo(ReviewWithNotedEntryFixture),
                "The blocked save leaves the file untouched.");
            var reloaded = Reload(path);
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004), Is.True);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].note" }));
        }

        // ── T12: a blocked save ignores unrelated pending edits ──────────────

        [Test]
        public void V1InvalidTrustStaysBlockedAfterUnrelatedEdit()
        {
            var fixture = "{\"version\":1,\"customOperationTrust\":\"bad\"}";
            var path = WriteFixture(fixture);

            var document = OpWeaveSettingsDocument.Load(path);
            document.SetPolicyLevel(PolicySide.Agent, EffectNames.AssetDelete, PolicyLevel.Deny);
            Blocked(document.Save());
            Assert.That(VersionText(path), Is.EqualTo(fixture),
                "Edits from the same session stay unsaved while the block stands (P-4).");

            var reloaded = Reload(path);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.customOperationTrust" }),
                "The error is never moved to another JsonPath (finding 5).");
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004), Is.True);
        }

        // ── V1-11: non-object extensions under a valid trust blocks too ──────

        [Test]
        public void V1NonObjectExtensionsBlocksSaveUntilRebuilt()
        {
            var fixture = "{\"version\":1,\"customOperationTrust\":\"auto\",\"extensions\":\"x\"}";
            var path = WriteFixture(fixture);

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.DiscoveryModeChoiceRequired);
            Blocked(document.Save());
            Assert.That(VersionText(path), Is.EqualTo(fixture));
            Assert.That(DiagnosticPaths(Reload(path), ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.extensions" }));

            document.SetDiscoveryMode(DiscoveryMode.Whitelist);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);
            var extensions = (JObject)SavedRoot(path)["extensions"]!;
            Assert.That(Canon(extensions["discoveryMode"]), Is.EqualTo("\"whitelist\""),
                "The explicit edit rebuilds the invalid parent as an object.");
        }

        // ── T13: unknown keys at every level survive a v2 save ───────────────

        [Test]
        public void UnknownKeysSurviveSaveByteEquivalentAtEveryLevel()
        {
            var path = WriteFixture(@"{
  ""version"": 2,
  ""customOperationTrust"": ""review"",
  ""futureTop"": { ""nested"": 1 },
  ""policy"": { ""agent"": { ""teleport"": ""allow"" }, ""other"": 2 },
  ""extensions"": { ""future"": 3, ""whitelist"": { ""future"": 4,
      ""operations"": [ { ""packageId"": ""p"", ""assemblyName"": ""A"",
        ""operationId"": ""o"", ""version"": 1, ""note"": ""keep"" } ] } }
}");
            var before = Reload(path).Value!.PreservedUnknownKeys;

            var document = OpWeaveSettingsDocument.Load(path);
            document.SetDebugLogging(true); // one explicit edit
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            var after = reloaded.Value!.PreservedUnknownKeys;
            Assert.That(after.Keys, Is.EquivalentTo(before.Keys));
            foreach (var key in before.Keys)
            {
                Assert.That(after[key].CanonicalText, Is.EqualTo(before[key].CanonicalText),
                    $"Unknown key {key} must be byte-equivalent in value.");
            }
            Assert.That(after["$.customOperationTrust"].CanonicalText,
                Is.EqualTo("\"review\""),
                "In a v2 file the retired trust key is just another unknown key.");
            Assert.That(reloaded.Value.DebugLogging, Is.True);
            Assert.That(
                reloaded.Diagnostics.Count(d => d.Code == ErrorCodes.OW_CFG_003),
                Is.EqualTo(before.Count));
        }

        // ── T14: OW-CFG-002 state — only explicitly edited values are written ─

        [Test]
        public void OwCfg002SaveWritesOnlyTheEditedValue()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"discoveryMode\":\"manual\"}," +
                "\"retainRuns\":\"x\",\"futureKeep\":{\"n\":1}}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsFalse(document.LoadResult.Ok);
            Assert.IsFalse(document.RequiresReplaceConfirmation);
            Assert.IsTrue(document.CanSave(replaceConfirmed: false));
            Assert.IsFalse(document.TryGetDiscoveryMode(out _));
            Assert.IsFalse(document.TryGetInteger(SettingsIntegerKey.RetainRuns, out _));

            // The human fixes only discoveryMode.
            document.SetDiscoveryMode(DiscoveryMode.Whitelist);
            var saved = Saved(document.Save());

            // The file was written and still reports the untouched invalid value.
            Assert.IsFalse(saved.Ok);
            var text = VersionText(path);
            Assert.That(text, Does.Contain("\"discoveryMode\": \"whitelist\""));
            Assert.That(text, Does.Contain("\"retainRuns\": \"x\""));
            Assert.That(text, Does.Contain("\"version\": 2"));

            var errors = saved.Diagnostics
                .Where(d => d.Code == ErrorCodes.OW_CFG_002).ToList();
            Assert.That(errors.Select(d => d.JsonPath),
                Is.EqualTo(new[] { "$.retainRuns" }),
                "Only the unedited invalid value may still error; none was replaced with a default.");
            Assert.That(saved.Diagnostics
                .Any(d => d.Code == ErrorCodes.OW_CFG_003 && d.JsonPath == "$.futureKeep"),
                Is.True, "Unknown keys remain preserved (and warned) after an OW-CFG-002 save.");
        }

        [Test]
        public void OwCfg002SaveDoesNotReplaceInvalidValuesWithDefaults()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"discoveryMode\":\"manual\"}," +
                "\"policy\":{\"agent\":{\"asset.move\":\"maybe\"}}," +
                "\"allowedPackageSources\":[\"http://x\"]}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsFalse(document.LoadResult.Ok);
            // A completely unrelated explicit edit.
            document.SetAgentLanguage("ja");
            Saved(document.Save());

            var errors = Reload(path).Diagnostics
                .Where(d => d.Code == ErrorCodes.OW_CFG_002)
                .Select(d => d.JsonPath).ToList();
            Assert.That(errors, Is.EquivalentTo(new[]
            {
                "$.extensions.discoveryMode",
                "$.policy.agent[\"asset.move\"]",
                "$.allowedPackageSources[0]",
            }));
        }

        // ── A2-25/P-5: an invalid string list is edited as a full draft ──────

        [Test]
        public void InvalidStringListDraftShowsEveryStoredElement()
        {
            var path = WriteFixture(
                "{\"version\":2,\"allowedPackageSources\":[\"registry:com.unity.*\"," +
                "\"bogus\",42,{\"nested\":1}]}");
            var before = Reload(path);
            Assert.IsFalse(before.Ok);
            Assert.That(DiagnosticPaths(before, ErrorCodes.OW_CFG_002),
                Is.EquivalentTo(new[]
                {
                    "$.allowedPackageSources[1]",
                    "$.allowedPackageSources[2]",
                    "$.allowedPackageSources[3]",
                }));

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsFalse(document.TryGetStringList(
                SettingsStringListKey.AllowedPackageSources, out _));
            var lines = document.GetStringListEditLines(
                SettingsStringListKey.AllowedPackageSources);
            Assert.That(lines, Is.EqualTo(new[]
            {
                "registry:com.unity.*",
                "bogus",
                "42",
                "{\"nested\":1}",
            }), "The draft shows every stored element, invalid ones raw — no hidden entry is dropped.");

            // The draft replaces the stored list only once every line validates.
            Assert.Throws<ArgumentException>(() =>
                document.SetStringList(SettingsStringListKey.AllowedPackageSources, lines));
            Assert.IsFalse(document.TryGetStringList(
                SettingsStringListKey.AllowedPackageSources, out _),
                "A partially invalid draft never replaces the stored list.");

            var repaired = new List<string>(lines)
            {
                [1] = "registry:com.example.fixed",
                [2] = "git:https://example.com/repo.git",
                [3] = "registry:org.other.*",
            };
            document.SetStringList(SettingsStringListKey.AllowedPackageSources, repaired);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.AllowedPackageSources, Is.EqualTo(new[]
            {
                "registry:com.unity.*",
                "registry:com.example.fixed",
                "git:https://example.com/repo.git",
                "registry:org.other.*",
            }), "The valid entry the draft displayed survives the repair.");
        }

        // ── T15: agent code.write "allow" is kept until edited ───────────────

        [Test]
        public void AgentCodeWriteAllowIsKeptUntilTheHumanEditsIt()
        {
            var path = WriteFixture(
                "{\"version\":2,\"policy\":{\"agent\":{\"code.write\":\"allow\"}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.TryGetPolicyLevel(
                PolicySide.Agent, EffectNames.CodeWrite, out var shown));
            Assert.That(shown, Is.EqualTo(PolicyLevel.Ask),
                "The UI shows the §H4-corrected ask, not the file's allow.");

            var saved = Saved(document.Save()); // no explicit edit
            Assert.IsTrue(saved.Ok);
            var text = VersionText(path);
            Assert.That(text, Does.Contain("\"code.write\": \"allow\""),
                "An untouched value is written back verbatim.");
            var reloaded = Reload(path);
            Assert.That(reloaded.Value!.AgentPolicy["code.write"], Is.EqualTo(PolicyLevel.Ask));
            Assert.That(
                reloaded.Diagnostics.Count(d => d.Code == ErrorCodes.OW_CFG_005),
                Is.EqualTo(1), "OW-CFG-005 is emitted exactly once.");

            var edited = OpWeaveSettingsDocument.Load(path);
            edited.SetPolicyLevel(PolicySide.Agent, EffectNames.CodeWrite, PolicyLevel.Deny);
            Assert.IsTrue(Saved(edited.Save()).Ok);
            var afterEdit = Reload(path);
            Assert.That(afterEdit.Value!.AgentPolicy["code.write"], Is.EqualTo(PolicyLevel.Deny));
            Assert.That(
                afterEdit.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_005), Is.False);
        }

        // ── T16–T19: whitelist edits keep each entry's own unknown properties ─

        private const string TwoEntryFixture =
            "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
            "{\"packageId\":\"p1\",\"assemblyName\":\"A1\",\"operationId\":\"o1\"," +
            "\"version\":1,\"note\":\"A-marker\"}," +
            "{\"packageId\":\"p2\",\"assemblyName\":\"A2\",\"operationId\":\"o2\"," +
            "\"version\":2,\"note\":\"B-marker\"}]}}}";

        private static JArray SavedOperations(string path) =>
            (JArray)((JObject)((JObject)SavedRoot(path)["extensions"]!)["whitelist"]!)
                ["operations"]!;

        [Test]
        public void V2WhitelistReorderKeepsUnknownPropertiesOnTheirEntry()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p1\",\"assemblyName\":\"A1\",\"operationId\":\"o1\"," +
                "\"version\":1,\"note\":\"nA\"}," +
                "{\"packageId\":\"p2\",\"assemblyName\":\"A2\",\"operationId\":\"o2\"," +
                "\"version\":2}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.Operations);
            document.MoveWhitelistItem(items[0], 1);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var operations = SavedOperations(path);
            Assert.That(Canon(((JObject)operations[0])["packageId"]), Is.EqualTo("\"p2\""));
            Assert.That(Canon(((JObject)operations[1])["note"]), Is.EqualTo("\"nA\""),
                "The moved entry keeps its own unknown properties at its new index.");
            var reloaded = Reload(path);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[1].note" }));
            Assert.That(reloaded.Value!.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("p2", "A2", "o2", 2),
                new OperationWhitelistEntry("p1", "A1", "o1", 1),
            }));
        }

        [Test]
        public void V2WhitelistEditKeepsEachEntrysOwnUnknownProperties()
        {
            var path = WriteFixture(TwoEntryFixture);
            var originalB = "{\"packageId\":\"p2\",\"assemblyName\":\"A2\"," +
                "\"operationId\":\"o2\",\"version\":2,\"note\":\"B-marker\"}";

            var document = OpWeaveSettingsDocument.Load(path);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.Operations);
            document.UpdateWhitelistItem(
                items[0], new OperationWhitelistEntry("p1", "A1", "o1", 2));
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var operations = SavedOperations(path);
            var entryA = (JObject)operations[0];
            var entryB = (JObject)operations[1];
            Assert.That(Canon(entryA["note"]), Is.EqualTo("\"A-marker\""));
            Assert.That(Canon(entryA["version"]), Is.EqualTo("2"),
                "The edit wrote the new version onto the same object.");
            Assert.That(Canon(entryA["packageId"]), Is.EqualTo("\"p1\""));
            Assert.That(Canon(entryA["assemblyName"]), Is.EqualTo("\"A1\""));
            Assert.That(Canon(entryA["operationId"]), Is.EqualTo("\"o1\""));
            Assert.That(Canon(entryB), Is.EqualTo(CanonOf(originalB)),
                "The untouched entry is byte-equivalent in value to the original.");
            var reloaded = Reload(path);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[]
                {
                    "$.extensions.whitelist.operations[0].note",
                    "$.extensions.whitelist.operations[1].note",
                }));
        }

        [Test]
        public void V2WhitelistAddWritesOnlyKnownKeys()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p1\",\"assemblyName\":\"A1\",\"operationId\":\"o1\"," +
                "\"version\":1,\"note\":\"keep\"}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            document.AddWhitelistItem(
                new OperationWhitelistEntry("@project", "New.Asm", "new.op", 1));
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var operations = SavedOperations(path);
            Assert.That(operations.Count, Is.EqualTo(2));
            var added = (JObject)operations[1];
            Assert.That(added.Properties().Select(p => p.Name), Is.EquivalentTo(new[]
            {
                "packageId", "assemblyName", "operationId", "version",
            }), "A new entry holds only the four known keys.");
            Assert.That(Canon(((JObject)operations[0])["note"]), Is.EqualTo("\"keep\""));
            var reloaded = Reload(path);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].note" }));
            Assert.That(reloaded.Value!.WhitelistedOperations.Count, Is.EqualTo(2));
        }

        [Test]
        public void V2WhitelistRemovalIsDisabledForEntryWithUnknownProperties()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p1\",\"assemblyName\":\"A1\",\"operationId\":\"o1\"," +
                "\"version\":1,\"note\":\"keep\"}," +
                "{\"packageId\":\"p2\",\"assemblyName\":\"A2\",\"operationId\":\"o2\"," +
                "\"version\":2}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.Operations);
            Assert.IsFalse(items[0].CanRemove);
            Assert.IsTrue(items[1].CanRemove);
            Assert.Throws<InvalidOperationException>(
                () => document.RemoveWhitelistItem(items[0]));
            document.RemoveWhitelistItem(items[1]);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var operations = SavedOperations(path);
            Assert.That(operations.Count, Is.EqualTo(1));
            Assert.That(Canon(((JObject)operations[0])["note"]), Is.EqualTo("\"keep\""),
                "The entry that could not be removed survives with its unknown keys.");
            var reloaded = Reload(path);
            Assert.That(reloaded.Value!.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("p1", "A1", "o1", 1),
            }));
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].note" }));
        }

        [Test]
        public void V2WhitelistListEditRefusesToDropUnknownKeyEntries()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p1\",\"assemblyName\":\"A1\",\"operationId\":\"o1\"," +
                "\"version\":1,\"note\":\"keep\"}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistedOperations(Array.Empty<OperationWhitelistEntry>()));
            Assert.That(
                document.GetWhitelistItems(SettingsWhitelistKind.Operations).Count,
                Is.EqualTo(1), "The refused list edit left the working array untouched.");
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);
            Assert.That(
                Reload(path).Value!.PreservedUnknownKeys[
                    "$.extensions.whitelist.operations[0].note"].CanonicalText,
                Is.EqualTo("\"keep\""));
        }

        // ── A2-13a/A2-15: an invalid entry is repaired in place, unknowns kept ─

        [Test]
        public void V2InvalidWhitelistEntryIsCorrectedInPlaceKeepingUnknownProperties()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p\",\"assemblyName\":\"A\",\"operationId\":\"o\"," +
                "\"note\":\"keep\"}]}}}");
            var before = Reload(path);
            Assert.IsFalse(before.Ok);
            Assert.That(DiagnosticPaths(before, ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].version" }));
            Assert.That(DiagnosticPaths(before, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].note" }));

            var document = OpWeaveSettingsDocument.Load(path);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.Operations);
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.IsNull(items[0].OperationEntry);
            Assert.IsFalse(items[0].CanRemove,
                "The invalid entry carries an unknown property and cannot be removed.");
            var raw = (JObject)items[0].Raw.ToToken();
            Assert.That(raw["packageId"]!.Value<string>(), Is.EqualTo("p"));
            Assert.IsNull(raw["version"],
                "The missing member is what the human's edit fills in.");

            // The member edit the provider's version field commits: only the
            // missing member is written, never the untouched ones.
            document.SetWhitelistItemMember(items[0], "version", 1);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok, "Correcting the known fields cleared the OW-CFG-002.");
            Assert.That(reloaded.Value!.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("p", "A", "o", 1),
            }));
            var entry = (JObject)SavedOperations(path)[0];
            Assert.That(Canon(entry["note"]), Is.EqualTo("\"keep\""),
                "The unknown property stays on the corrected entry, byte-equivalent in value.");
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].note" }));
        }

        [Test]
        public void V2InvalidTypeProviderEntryIsCorrectedInPlaceKeepingUnknownProperties()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"typeProviders\":[" +
                "{\"packageId\":\"p\",\"note\":\"keep\"}]}}}");
            var before = Reload(path);
            Assert.IsFalse(before.Ok);
            Assert.That(DiagnosticPaths(before, ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.extensions.whitelist.typeProviders[0].providerId" }));
            Assert.That(DiagnosticPaths(before, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.typeProviders[0].note" }));

            var document = OpWeaveSettingsDocument.Load(path);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.TypeProviders);
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.IsNull(items[0].TypeProviderEntry);
            Assert.IsFalse(items[0].CanRemove);

            // The member edit the provider's providerId field commits.
            document.SetWhitelistItemMember(items[0], "providerId", "tp");
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.WhitelistedTypeProviders, Is.EqualTo(new[]
            {
                new TypeProviderWhitelistEntry("p", "tp"),
            }));
            var entry = (JObject)((JArray)((JObject)((JObject)SavedRoot(path)["extensions"]!)
                ["whitelist"]!)["typeProviders"]!)[0];
            Assert.That(Canon(entry["note"]), Is.EqualTo("\"keep\""),
                "The unknown property stays on the corrected entry, byte-equivalent in value.");
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.typeProviders[0].note" }));
        }

        // ── Field-level repair of invalid whitelist entries (P-7 follow-up) ──

        [Test]
        public void V2InvalidWhitelistEntryRepairsFieldByFieldKeepingUnknownProperties()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"assemblyName\":\"A\",\"note\":\"keep\"}]}}}");
            var before = Reload(path);
            Assert.IsFalse(before.Ok);
            Assert.That(DiagnosticPaths(before, ErrorCodes.OW_CFG_002),
                Is.EquivalentTo(new[]
                {
                    "$.extensions.whitelist.operations[0].packageId",
                    "$.extensions.whitelist.operations[0].operationId",
                    "$.extensions.whitelist.operations[0].version",
                }));

            var document = OpWeaveSettingsDocument.Load(path);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.Operations);
            Assert.That(items.Count, Is.EqualTo(1));
            Assert.IsNull(items[0].OperationEntry);
            Assert.IsFalse(items[0].CanRemove);

            // The per-field edits the provider's controls commit: each repair
            // is retained even while the entry's other required fields stay
            // invalid — a whole-entry update could not be committed here.
            document.SetWhitelistItemMember(items[0], "packageId", "p");
            var raw = (JObject)items[0].Raw.ToToken();
            Assert.That(raw["packageId"]!.Value<string>(), Is.EqualTo("p"),
                "The first correction is retained.");
            Assert.IsNull(raw["operationId"], "An untouched missing member stays missing.");
            Assert.IsNull(raw["version"]);
            Assert.IsNull(items[0].OperationEntry,
                "The entry stays invalid until every required field is repaired.");

            document.SetWhitelistItemMember(items[0], "operationId", "o");
            Assert.IsNull(((JObject)items[0].Raw.ToToken())["version"],
                "Editing another member never invents the missing version.");
            Assert.IsNull(items[0].OperationEntry);

            document.SetWhitelistItemMember(items[0], "version", 1);
            Assert.That(items[0].OperationEntry,
                Is.EqualTo(new OperationWhitelistEntry("p", "A", "o", 1)));

            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("p", "A", "o", 1),
            }));
            var entry = (JObject)SavedOperations(path)[0];
            Assert.That(Canon(entry["note"]), Is.EqualTo("\"keep\""),
                "The unknown property stays on the repaired entry, byte-equivalent in value.");
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_003),
                Is.EqualTo(new[] { "$.extensions.whitelist.operations[0].note" }));
        }

        [Test]
        public void V2WhitelistEditNeverInventsAMissingVersion()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p\",\"assemblyName\":\"A\",\"operationId\":\"o\"," +
                "\"note\":\"keep\"}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            var item = document.GetWhitelistItems(SettingsWhitelistKind.Operations)[0];
            Assert.IsNull(item.OperationEntry);

            // An edit to another field must not silently write version: 1.
            document.SetWhitelistItemMember(item, "packageId", "p2");
            Assert.IsNull(((JObject)item.Raw.ToToken())["version"],
                "The untouched missing version stays missing.");
            Assert.IsNull(item.OperationEntry);

            // The human's explicit choice of 1 repairs the entry — the commit
            // the provider issues when the version field changes from its
            // displayed 0 seed.
            document.SetWhitelistItemMember(item, "version", 1);
            Assert.That(item.OperationEntry,
                Is.EqualTo(new OperationWhitelistEntry("p2", "A", "o", 1)));

            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("p2", "A", "o", 1),
            }));
            Assert.That(Canon(((JObject)SavedOperations(path)[0])["note"]),
                Is.EqualTo("\"keep\""));
        }

        [Test]
        public void V2InvalidTypeProviderEntryRepairsFieldByFieldKeepingUnknownProperties()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"typeProviders\":[" +
                "{\"note\":\"keep\"}]}}}");
            var before = Reload(path);
            Assert.That(DiagnosticPaths(before, ErrorCodes.OW_CFG_002),
                Is.EquivalentTo(new[]
                {
                    "$.extensions.whitelist.typeProviders[0].packageId",
                    "$.extensions.whitelist.typeProviders[0].providerId",
                }));

            var document = OpWeaveSettingsDocument.Load(path);
            var item = document.GetWhitelistItems(SettingsWhitelistKind.TypeProviders)[0];
            Assert.IsNull(item.TypeProviderEntry);
            Assert.IsFalse(item.CanRemove);

            document.SetWhitelistItemMember(item, "packageId", "p");
            var raw = (JObject)item.Raw.ToToken();
            Assert.That(raw["packageId"]!.Value<string>(), Is.EqualTo("p"),
                "The first correction is retained.");
            Assert.IsNull(raw["providerId"], "An untouched missing member stays missing.");
            Assert.IsNull(item.TypeProviderEntry);

            document.SetWhitelistItemMember(item, "providerId", "tp");
            Assert.That(item.TypeProviderEntry,
                Is.EqualTo(new TypeProviderWhitelistEntry("p", "tp")));

            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.WhitelistedTypeProviders, Is.EqualTo(new[]
            {
                new TypeProviderWhitelistEntry("p", "tp"),
            }));
            var entry = (JObject)((JArray)((JObject)((JObject)SavedRoot(path)["extensions"]!)
                ["whitelist"]!)["typeProviders"]!)[0];
            Assert.That(Canon(entry["note"]), Is.EqualTo("\"keep\""),
                "The unknown property stays on the repaired entry.");
        }

        [Test]
        public void V2WhitelistMemberEditRejectsInvalidValuesAndKeys()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p\",\"assemblyName\":\"A\",\"operationId\":\"o\"," +
                "\"version\":1}],\"typeProviders\":[" +
                "{\"packageId\":\"q\",\"providerId\":\"tp\"}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            var operation = document.GetWhitelistItems(SettingsWhitelistKind.Operations)[0];
            var provider = document.GetWhitelistItems(SettingsWhitelistKind.TypeProviders)[0];

            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistItemMember(operation, "packageId", ""));
            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistItemMember(operation, "packageId", "bad*id"));
            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistItemMember(operation, "version", 0L));
            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistItemMember(operation, "version", -2L));
            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistItemMember(operation, "version", "1"),
                "version is a numeric field, not a text field.");
            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistItemMember(operation, "packageId", 1L));
            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistItemMember(provider, "operationId", "x"));
            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistItemMember(provider, "version", 1L));

            Assert.That(operation.OperationEntry,
                Is.EqualTo(new OperationWhitelistEntry("p", "A", "o", 1)),
                "The refused edits changed nothing.");
            Assert.That(provider.TypeProviderEntry,
                Is.EqualTo(new TypeProviderWhitelistEntry("q", "tp")));
        }

        // ── Per-element string-list drafts (P-5 follow-up) ──────────────────

        [Test]
        public void V2StringListDraftEditBesideInvalidElementKeepsItInvalid()
        {
            var path = WriteFixture("{\"version\":2,\"allowedPaths\":[\"Assets/\",42]}");
            var before = Reload(path);
            Assert.IsFalse(before.Ok);
            Assert.That(DiagnosticPaths(before, ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.allowedPaths[1]" }));

            var document = OpWeaveSettingsDocument.Load(path);
            var draft = document.GetStringListDraft(SettingsStringListKey.AllowedPaths);
            Assert.That(draft.Count, Is.EqualTo(2));
            Assert.That(draft[0].Text, Is.EqualTo("Assets/"));
            Assert.IsTrue(draft[0].IsStoredElement);
            Assert.IsFalse(draft[0].Edited);
            Assert.That(draft[1].Original!.CanonicalText, Is.EqualTo("42"));
            Assert.IsFalse(draft[1].Edited);

            // The commit the provider performs after the human edits only the
            // valid line: the untouched number must never launder into the
            // string "42".
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(
                    SettingsStringListKey.AllowedPaths,
                    new[] { draft[0].WithText("Assets/Extra/"), draft[1] }));
            var stored = (JArray)document.GetRawValue("allowedPaths")!.ToToken();
            Assert.That(stored[1]!.Type, Is.EqualTo(JTokenType.Integer),
                "The refused commit left the stored array untouched.");

            // Retyping the element's raw text keeps it untouched — it does not
            // resolve the invalid element.
            Assert.IsFalse(draft[1].WithText("42").Edited,
                "A line that still equals the raw seed stays the stored element.");

            // The held edit stays uncommitted: saving still writes the stored
            // invalid value and keeps its OW-CFG-002.
            var saved = Saved(document.Save());
            Assert.IsFalse(saved.Ok);
            var array = (JArray)SavedRoot(path)["allowedPaths"]!;
            Assert.That(array[0]!.Value<string>(), Is.EqualTo("Assets/"),
                "The refused edit did not partially apply.");
            Assert.That(array[1]!.Type, Is.EqualTo(JTokenType.Integer),
                "The invalid element keeps its type — and its OW-CFG-002.");
            Assert.That(DiagnosticPaths(Reload(path), ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.allowedPaths[1]" }));
        }

        [Test]
        public void V2StringListDraftResolvesWhenInvalidElementsAreFixed()
        {
            var path = WriteFixture(
                "{\"version\":2,\"allowedPaths\":[\"Assets/\",42,{\"n\":1}]}");

            var document = OpWeaveSettingsDocument.Load(path);
            var draft = document.GetStringListDraft(SettingsStringListKey.AllowedPaths);
            Assert.That(draft.Count, Is.EqualTo(3));
            Assert.That(draft[2].Original!.CanonicalText, Is.EqualTo("{\"n\":1}"));

            // Editing one raw element while another stays raw still refuses.
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(
                    SettingsStringListKey.AllowedPaths,
                    new[]
                    {
                        draft[0],
                        draft[1].WithText("Assets/Fixed/"),
                        draft[2],
                    }));

            // Fixing the last raw element resolves the draft; the untouched
            // valid element is written back verbatim at its index.
            document.SetStringListDraft(
                SettingsStringListKey.AllowedPaths,
                new[]
                {
                    draft[0],
                    draft[1].WithText("Assets/Fixed/"),
                    draft[2].WithText("Assets/Other/"),
                });
            Assert.IsTrue(document.TryGetStringList(
                SettingsStringListKey.AllowedPaths, out var values));
            Assert.That(values, Is.EqualTo(new[] { "Assets/", "Assets/Fixed/", "Assets/Other/" }));

            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);
            Assert.That(Reload(path).Value!.AllowedPaths,
                Is.EqualTo(new[] { "Assets/", "Assets/Fixed/", "Assets/Other/" }));
        }

        [Test]
        public void V2StringListDraftAddsAndRemovesLines()
        {
            var path = WriteFixture(
                "{\"version\":2,\"allowedPaths\":[\"Assets/\",\"Assets/Old/\"]}");
            var document = OpWeaveSettingsDocument.Load(path);
            var draft = document.GetStringListDraft(SettingsStringListKey.AllowedPaths);

            // An added line carries no stored element and always counts as
            // edited; dropping draft[0] removes the stored first element.
            var added = SettingsStringListDraftElement.Added("Assets/Extra/");
            Assert.IsTrue(added.Edited);
            Assert.IsNull(added.Original);
            Assert.IsFalse(added.IsStoredElement);
            document.SetStringListDraft(
                SettingsStringListKey.AllowedPaths,
                new[] { added, draft[1] });

            Assert.IsTrue(document.TryGetStringList(
                SettingsStringListKey.AllowedPaths, out var values));
            Assert.That(values, Is.EqualTo(new[] { "Assets/Extra/", "Assets/Old/" }),
                "The untouched second element is written back verbatim at its new index.");
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);
            Assert.That(Reload(path).Value!.AllowedPaths,
                Is.EqualTo(new[] { "Assets/Extra/", "Assets/Old/" }));
        }

        [Test]
        public void V2PackageSourcesDraftKeepsInvalidEntriesUntilResolved()
        {
            var path = WriteFixture(
                "{\"version\":2,\"allowedPackageSources\":[" +
                "\"registry:com.unity.*\",\"bogus\",42]}");

            var document = OpWeaveSettingsDocument.Load(path);
            var draft = document.GetStringListDraft(
                SettingsStringListKey.AllowedPackageSources);

            // An untouched grammar-invalid string keeps its OW-CFG-002 exactly
            // like a raw one: editing the valid element does not commit while
            // either invalid entry is unresolved.
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(
                    SettingsStringListKey.AllowedPackageSources,
                    new[]
                    {
                        draft[0].WithText("registry:com.example.*"),
                        draft[1],
                        draft[2],
                    }));

            // Fixing the grammar-invalid entry leaves the raw element
            // unresolved — the commit still refuses.
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(
                    SettingsStringListKey.AllowedPackageSources,
                    new[]
                    {
                        draft[0].WithText("registry:com.example.*"),
                        draft[1].WithText("registry:com.fixed.*"),
                        draft[2],
                    }));

            // An edited element must itself validate.
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(
                    SettingsStringListKey.AllowedPackageSources,
                    new[]
                    {
                        draft[0].WithText("registry:com.example.*"),
                        draft[1].WithText("still bogus"),
                        draft[2].WithText("registry:com.raw.*"),
                    }));

            document.SetStringListDraft(
                SettingsStringListKey.AllowedPackageSources,
                new[]
                {
                    draft[0].WithText("registry:com.example.*"),
                    draft[1].WithText("registry:com.fixed.*"),
                    draft[2].WithText("registry:com.raw.*"),
                });
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);
            Assert.That(Reload(path).Value!.AllowedPackageSources, Is.EqualTo(new[]
            {
                "registry:com.example.*",
                "registry:com.fixed.*",
                "registry:com.raw.*",
            }));
        }

        [Test]
        public void V2StringListNonArrayMemberNeedsExplicitResolution()
        {
            var path = WriteFixture("{\"version\":2,\"allowedPaths\":\"Assets/\"}");
            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsFalse(document.TryGetStringList(
                SettingsStringListKey.AllowedPaths, out _));

            var draft = document.GetStringListDraft(SettingsStringListKey.AllowedPaths);
            Assert.That(draft.Count, Is.EqualTo(1));
            Assert.IsFalse(draft[0].IsStoredElement,
                "A stored non-array member is a raw line, not an array element.");
            Assert.IsFalse(draft[0].Edited);

            // Appending a line must not silently wrap the scalar member in an
            // array.
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(
                    SettingsStringListKey.AllowedPaths,
                    new[] { draft[0], SettingsStringListDraftElement.Added("Assets/Other/") }));

            // An explicit edit of the raw line resolves the member into a
            // string list.
            document.SetStringListDraft(
                SettingsStringListKey.AllowedPaths,
                new[] { draft[0].WithText("Assets/Fixed/") });
            Assert.IsTrue(document.TryGetStringList(
                SettingsStringListKey.AllowedPaths, out var values));
            Assert.That(values, Is.EqualTo(new[] { "Assets/Fixed/" }));
        }

        // ── Held drafts gate the save (per-element UI follow-up) ────────────

        [Test]
        public void V2StringListRemoveBeforeInvalidElementKeepsRawValueAndBlocksSave()
        {
            var path = WriteFixture("{\"version\":2,\"allowedPaths\":[\"Assets/\",42]}");
            var document = OpWeaveSettingsDocument.Load(path);
            var draft = document.GetStringListDraft(SettingsStringListKey.AllowedPaths);

            // The per-element remove control drops this element by identity:
            // no line-index reconciliation can turn the untouched number's raw
            // "42" display into the first element's new text.
            draft.Remove(draft[0]);
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(SettingsStringListKey.AllowedPaths, draft));
            document.HoldStringListDraft(SettingsStringListKey.AllowedPaths, draft);

            // The untouched number stays stored verbatim — never laundered
            // into the string "42" — and keeps its OW-CFG-002.
            var stored = (JArray)document.GetRawValue("allowedPaths")!.ToToken();
            Assert.That(stored.Count, Is.EqualTo(2));
            Assert.That(stored[0]!.Value<string>(), Is.EqualTo("Assets/"));
            Assert.That(stored[1]!.Type, Is.EqualTo(JTokenType.Integer));
            Assert.That(DiagnosticPaths(Reload(path), ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.allowedPaths[1]" }));

            // The held removal blocks the save: a visible edit is never
            // silently discarded by saving the unchanged document.
            Assert.IsTrue(document.HasPendingStringListDraft(
                SettingsStringListKey.AllowedPaths));
            Assert.IsFalse(document.CanSave(replaceConfirmed: false));
            Assert.That(document.SaveBlockReasons[0], Does.Contain("allowedPaths"));
            Blocked(document.Save());
            Assert.That(VersionText(path),
                Does.Contain("\"Assets/\"").And.Contain("42"),
                "A blocked save writes nothing.");

            // The held draft still carries the removal: the number's own
            // element, still raw and still unresolved.
            var held = document.GetStringListDraft(SettingsStringListKey.AllowedPaths);
            Assert.That(held.Count, Is.EqualTo(1));
            Assert.That(held[0].Original!.CanonicalText, Is.EqualTo("42"));
            Assert.IsFalse(held[0].Edited);
            Assert.IsTrue(held[0].IsUnresolved);

            // The removal commits once the human resolves the invalid element
            // explicitly; the number leaves only through that explicit edit.
            document.SetStringListDraft(
                SettingsStringListKey.AllowedPaths,
                new[] { held[0].WithText("Assets/Fixed/") });
            Assert.IsFalse(document.HasPendingStringListDraft(
                SettingsStringListKey.AllowedPaths));
            Assert.IsTrue(document.CanSave(replaceConfirmed: false));
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);
            Assert.That(Reload(path).Value!.AllowedPaths,
                Is.EqualTo(new[] { "Assets/Fixed/" }));
        }

        [Test]
        public void V2StringListEditBesideInvalidElementBlocksSaveUntilResolved()
        {
            var path = WriteFixture("{\"version\":2,\"allowedPaths\":[\"Assets/\",42]}");
            var document = OpWeaveSettingsDocument.Load(path);
            var draft = document.GetStringListDraft(SettingsStringListKey.AllowedPaths);

            // The per-element text control edits only this element; the
            // untouched number beside it keeps the commit refused.
            var edited = new List<SettingsStringListDraftElement>(draft);
            edited[0] = draft[0].WithText("Assets/Edited/");
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(SettingsStringListKey.AllowedPaths, edited));
            document.HoldStringListDraft(SettingsStringListKey.AllowedPaths, edited);

            // While the visible edit is held the save is blocked — it can
            // never be discarded by saving the unchanged document.
            Assert.IsTrue(document.HasPendingStringListDraft(
                SettingsStringListKey.AllowedPaths));
            Assert.IsFalse(document.CanSave(replaceConfirmed: false));
            Blocked(document.Save());
            var stored = (JArray)SavedRoot(path)["allowedPaths"]!;
            Assert.That(stored[0]!.Value<string>(), Is.EqualTo("Assets/"),
                "A blocked save writes nothing — the stored value is untouched.");
            Assert.That(stored[1]!.Type, Is.EqualTo(JTokenType.Integer));

            // The held draft still shows the human's edit.
            var held = document.GetStringListDraft(SettingsStringListKey.AllowedPaths);
            Assert.That(held[0].Text, Is.EqualTo("Assets/Edited/"));
            Assert.IsTrue(held[0].Edited);

            // Resolving the invalid element commits the held edit exactly.
            document.SetStringListDraft(
                SettingsStringListKey.AllowedPaths,
                new[] { held[0], held[1].WithText("Assets/Fixed/") });
            Assert.IsFalse(document.HasPendingStringListDraft(
                SettingsStringListKey.AllowedPaths));
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);
            Assert.That(Reload(path).Value!.AllowedPaths,
                Is.EqualTo(new[] { "Assets/Edited/", "Assets/Fixed/" }));
        }

        [Test]
        public void V2StringListRevertedOrDiscardedDraftReleasesTheSaveGate()
        {
            var path = WriteFixture("{\"version\":2,\"allowedPaths\":[\"Assets/\",42]}");
            var document = OpWeaveSettingsDocument.Load(path);
            var draft = document.GetStringListDraft(SettingsStringListKey.AllowedPaths);

            // A held draft identical to the stored list is no pending edit.
            document.HoldStringListDraft(SettingsStringListKey.AllowedPaths, draft);
            Assert.IsFalse(document.HasPendingStringListDraft(
                SettingsStringListKey.AllowedPaths));
            Assert.IsTrue(document.CanSave(replaceConfirmed: false));

            var edited = new List<SettingsStringListDraftElement>(draft);
            edited[0] = draft[0].WithText("Assets/Edited/");
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(SettingsStringListKey.AllowedPaths, edited));
            document.HoldStringListDraft(SettingsStringListKey.AllowedPaths, edited);
            Assert.IsTrue(document.HasPendingStringListDraft(
                SettingsStringListKey.AllowedPaths));
            Assert.IsFalse(document.CanSave(replaceConfirmed: false));

            // Reverting the edit back to the seeded elements is again a draft
            // equal to the stored list: the gate releases.
            Assert.Throws<ArgumentException>(() =>
                document.SetStringListDraft(SettingsStringListKey.AllowedPaths, draft));
            document.HoldStringListDraft(SettingsStringListKey.AllowedPaths, draft);
            Assert.IsFalse(document.HasPendingStringListDraft(
                SettingsStringListKey.AllowedPaths));
            Assert.IsTrue(document.CanSave(replaceConfirmed: false));

            // Discarding a held draft also releases the gate without writing
            // the held edits: the stored list is saved back verbatim.
            document.HoldStringListDraft(SettingsStringListKey.AllowedPaths, edited);
            Assert.IsTrue(document.HasPendingStringListDraft(
                SettingsStringListKey.AllowedPaths));
            document.DiscardStringListDraft(SettingsStringListKey.AllowedPaths);
            Assert.IsFalse(document.HasPendingStringListDraft(
                SettingsStringListKey.AllowedPaths));
            Assert.IsTrue(document.CanSave(replaceConfirmed: false));
            var saved = Saved(document.Save());
            Assert.IsFalse(saved.Ok);
            var stored = (JArray)SavedRoot(path)["allowedPaths"]!;
            Assert.That(stored[1]!.Type, Is.EqualTo(JTokenType.Integer),
                "Discarding the draft wrote the stored list back verbatim.");
            Assert.That(DiagnosticPaths(Reload(path), ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.allowedPaths[1]" }));
        }

        // ── T20: integrityLevel "full" survives without downgrade ────────────

        [Test]
        public void IntegrityLevelFullSurvivesSaveWithoutDowngrade()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"integrityLevel\":\"full\"}}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.TryGetIntegrityLevel(out var level));
            Assert.That(level, Is.EqualTo(IntegrityLevel.Full));
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);
            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.IntegrityLevel, Is.EqualTo(IntegrityLevel.Full));
            Assert.That(VersionText(path), Does.Contain("\"integrityLevel\": \"full\""));
        }

        // ── T21/T21b/T21c: the OW-CFG-001 replace gate ───────────────────────

        [Test]
        public void UnparseableFileDisablesSaveUntilReplaceIsConfirmed()
        {
            var path = WriteFixture("{not json");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.FileExists);
            Assert.IsFalse(document.LoadResult.Ok);
            Assert.IsTrue(document.RequiresReplaceConfirmation);
            Assert.IsFalse(document.CanSave(replaceConfirmed: false));
            var refused = document.Save();
            Assert.That(refused.Status,
                Is.EqualTo(SettingsSaveStatus.ReplaceConfirmationRequired));
            Assert.That(refused.Diagnostics.Any(
                d => d.Code == ErrorCodes.OW_CFG_001 && d.Severity == Severity.Error),
                Is.True, "The refusal carries the read-time OW-CFG-001 diagnostic.");
            var refusedAgain = document.Save(replaceConfirmed: false);
            Assert.That(refusedAgain.Status,
                Is.EqualTo(SettingsSaveStatus.ReplaceConfirmationRequired));
            Assert.That(VersionText(path), Is.EqualTo("{not json"),
                "Refusing to save must leave the file untouched.");

            var saved = Saved(document.Save(replaceConfirmed: true));
            Assert.IsTrue(saved.Ok);
            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.DiscoveryMode, Is.EqualTo(DiscoveryMode.Auto));
            Assert.That(VersionText(path), Does.Contain("\"version\": 2"));
        }

        [Test]
        public void UnknownVersionReplaceWritesV2AndKeepsUnknownKeys()
        {
            var path = WriteFixture(@"{
  ""version"": 3,
  ""retainRuns"": 9,
  ""futureTop"": { ""x"": 1 },
  ""policy"": { ""other"": 2, ""agent"": { ""read"": ""deny"", ""teleport"": ""allow"" } },
  ""extensions"": { ""future"": 4,
    ""whitelist"": { ""weird"": 7, ""operations"": [ { ""note"": ""keep"" } ] } }
}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsFalse(document.LoadResult.Ok);
            Assert.IsTrue(document.RequiresReplaceConfirmation);
            Assert.That(document.Save().Status,
                Is.EqualTo(SettingsSaveStatus.ReplaceConfirmationRequired));

            var saved = Saved(document.Save(replaceConfirmed: true));
            Assert.IsTrue(saved.Ok,
                $"Replacement file must be valid v2: {string.Join(", ", saved.Diagnostics.Select(d => d.Code))}");

            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            var preserved = reloaded.Value!.PreservedUnknownKeys;
            // Every v2-unknown key survives the replace, nested ones included.
            Assert.That(preserved["$.futureTop"].CanonicalText, Is.EqualTo("{\"x\":1}"));
            Assert.That(preserved["$.policy.other"].CanonicalText, Is.EqualTo("2"));
            Assert.That(preserved["$.policy.agent.teleport"].CanonicalText, Is.EqualTo("\"allow\""));
            Assert.That(preserved["$.extensions.future"].CanonicalText, Is.EqualTo("4"));
            Assert.That(preserved["$.extensions.whitelist.weird"].CanonicalText, Is.EqualTo("7"));
            // v2-known member names of an unknown version are never trusted: the
            // replace writes the documented defaults, not the file's v3 values.
            var settings = reloaded.Value;
            Assert.That(settings.RetainRuns, Is.EqualTo(50));
            Assert.That(settings.AgentPolicy["read"], Is.EqualTo(PolicyLevel.Allow));
            Assert.That(settings.WhitelistedOperations, Is.Empty);
        }

        [Test]
        public void NonObjectRootRequiresConfirmedReplace()
        {
            var path = WriteFixture("[1]");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsFalse(document.LoadResult.Ok);
            Assert.IsTrue(document.RequiresReplaceConfirmation,
                "A non-object root is OW-CFG-001: the replace-the-file path applies.");
            Assert.That(document.Save().Status,
                Is.EqualTo(SettingsSaveStatus.ReplaceConfirmationRequired));
            Assert.That(document.Save(replaceConfirmed: false).Status,
                Is.EqualTo(SettingsSaveStatus.ReplaceConfirmationRequired));
            Assert.That(VersionText(path), Is.EqualTo("[1]"),
                "An unconfirmed save leaves the file untouched.");

            var saved = Saved(document.Save(replaceConfirmed: true));
            Assert.IsTrue(saved.Ok);
            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(VersionText(path), Does.Contain("\"version\": 2"));
        }

        [Test]
        public void UnreadableFileRefusesEveryWriteUntilReRead()
        {
            var path = Path.Combine(_dir, "OpWeaveSettings.json");
            Directory.CreateDirectory(path); // a directory at the settings path

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.FileExists);
            Assert.IsFalse(document.LoadResult.Ok);
            Assert.That(document.LoadResult.Diagnostics.Any(d =>
                d.Code == ErrorCodes.OW_CFG_001 && d.JsonPath == "$"), Is.True);
            Assert.IsFalse(document.RequiresReplaceConfirmation,
                "An I/O failure is not a replace confirmation case (E-01c).");
            Assert.That(document.SaveBlockReasons, Is.Not.Empty);
            Assert.IsFalse(document.CanSave(replaceConfirmed: false));
            Assert.IsFalse(document.CanSave(replaceConfirmed: true));

            Blocked(document.Save());
            Blocked(document.Save(replaceConfirmed: true));
            Assert.IsTrue(Directory.Exists(path),
                "Refused writes never create or overwrite the unreadable path.");

            // Once the file can be read again the normal rules apply.
            Directory.Delete(path);
            File.WriteAllText(path, "{\"version\":2}");
            var reloadedDocument = OpWeaveSettingsDocument.Load(path);
            Assert.That(reloadedDocument.SaveBlockReasons, Is.Empty);
            var saved = Saved(reloadedDocument.Save());
            Assert.IsTrue(saved.Ok);
        }

        // ── T22: an external change between load and save refuses the write ──

        [Test]
        public void ExternalChangeBetweenLoadAndSaveIsRefused()
        {
            var path = WriteFixture("{\"version\":2}");
            var document = OpWeaveSettingsDocument.Load(path);
            var replacement = "{\"version\":2,\"debugLogging\":true}";
            File.WriteAllText(path, replacement); // someone else edits the file

            var outcome = document.Save();
            Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.ChangedExternally));
            Assert.That(VersionText(path), Is.EqualTo(replacement),
                "The newer on-disk content is never overwritten.");

            // A second save on the same document still refuses.
            Assert.That(document.Save().Status,
                Is.EqualTo(SettingsSaveStatus.ChangedExternally));
        }

        // ── T23: an explicit null is written back verbatim ───────────────────

        [Test]
        public void V2ExplicitNullIsWrittenBackVerbatim()
        {
            var path = WriteFixture("{\"version\":2,\"debugLogging\":null}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsFalse(document.TryGetDebugLogging(out _));
            document.SetInteger(SettingsIntegerKey.TickBudgetMs, 16);
            Saved(document.Save());

            var root = SavedRoot(path);
            Assert.That(root["debugLogging"]!.Type, Is.EqualTo(JTokenType.Null),
                "The untouched explicit null is never dropped or defaulted.");
            var reloaded = Reload(path);
            Assert.IsFalse(reloaded.Ok);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_002),
                Is.EqualTo(new[] { "$.debugLogging" }));
        }

        // ── T24: Agents get no settings write API ────────────────────────────

        [Test]
        public void AgentSurfaceHasNoSettingsWritePath()
        {
            foreach (var type in new[] { typeof(OpWeaveSettings), typeof(OpWeaveSettingsLoader) })
            {
                foreach (var method in type.GetMethods(
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                {
                    Assert.IsFalse(
                        method.Name.Contains("Save") || method.Name.Contains("Write") ||
                        method.Name.StartsWith("set_", StringComparison.Ordinal),
                        type.Name + "." + method.Name +
                            " must not give Agents a settings write path (SPEC §H4 item 5).");
                }
            }
        }

        // ── T25/T27: byte-equivalent scalar preservation at the top level ────

        private const string UnknownScalarFixture =
            "\"u1\":1.0,\"u2\":1,\"u4\":1E2,\"u5\":9223372036854775807," +
            "\"u6\":\"é\\t\",\"u7\":\"2026-01-01T00:00:00Z\"," +
            "\"u8\":{\"b\":1,\"a\":[null,{}]},\"u9\":null";

        private static readonly string[] s_unknownScalarPaths =
        {
            "$.u1", "$.u2", "$.u4", "$.u5", "$.u6", "$.u7", "$.u8", "$.u9",
        };

        private static void AssertUnknownScalarsPreserved(string path, Result<OpWeaveSettings> before)
        {
            var after = Reload(path);
            Assert.IsTrue(after.Ok);
            var beforeMap = before.Value!.PreservedUnknownKeys;
            var afterMap = after.Value!.PreservedUnknownKeys;
            foreach (var key in s_unknownScalarPaths)
            {
                Assert.That(afterMap[key].CanonicalText,
                    Is.EqualTo(beforeMap[key].CanonicalText),
                    $"Unknown member {key} must be byte-equivalent in value.");
                Assert.That(after.Diagnostics.Any(d =>
                    d.Code == ErrorCodes.OW_CFG_003 && d.JsonPath == key), Is.True, key);
            }
        }

        [Test]
        public void V2UnknownScalarsStayByteEquivalentInValue()
        {
            var path = WriteFixture("{\"version\":2," + UnknownScalarFixture + "}");
            var before = Reload(path);

            var document = OpWeaveSettingsDocument.Load(path);
            document.SetDebugLogging(true);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            AssertUnknownScalarsPreserved(path, before);
            var root = SavedRoot(path);
            Assert.That(root["u1"]!.Type, Is.EqualTo(JTokenType.Float),
                "1.0 stays a Double, not an integer.");
            Assert.That(root["u2"]!.Type, Is.EqualTo(JTokenType.Integer));
            Assert.That(root["u5"]!.Type, Is.EqualTo(JTokenType.Integer),
                "Int64 values keep their integer type.");
            Assert.That(root["u7"]!.Type, Is.EqualTo(JTokenType.String),
                "An ISO-date-looking string stays a string.");
            Assert.That(root["u9"]!.Type, Is.EqualTo(JTokenType.Null),
                "An explicit null stays present, not absent.");
        }

        [Test]
        public void V1UnknownScalarsStayByteEquivalentInValue()
        {
            var path = WriteFixture(
                "{\"customOperationTrust\":\"auto\"," + UnknownScalarFixture + "}");
            var before = Reload(path);

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.SourceWasV1);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            AssertUnknownScalarsPreserved(path, before);
            var reloaded = Reload(path);
            Assert.That(reloaded.Value!.DiscoveryMode, Is.EqualTo(DiscoveryMode.Auto));
            Assert.That(reloaded.Diagnostics.Any(d => d.Code == ErrorCodes.OW_CFG_004),
                Is.False);
        }

        // ── T26: nested unknown values survive reorder and policy edits ──────

        [Test]
        public void UnknownKeysInsideReorderedEntryAndPolicyStayEqual()
        {
            var path = WriteFixture(
                "{\"version\":2,\"policy\":{\"agent\":{\"read\":\"ask\",\"u8\":{\"b\":1," +
                "\"a\":[null,{}]},\"u9\":null}},\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p\",\"assemblyName\":\"A\",\"operationId\":\"o\",\"version\":1," +
                "\"u1\":1.0,\"u9\":null,\"note\":\"m\"}," +
                "{\"packageId\":\"q\",\"assemblyName\":\"B\",\"operationId\":\"p\",\"version\":3}]}}}");
            var before = Reload(path).Value!.PreservedUnknownKeys;

            var document = OpWeaveSettingsDocument.Load(path);
            var items = document.GetWhitelistItems(SettingsWhitelistKind.Operations);
            document.MoveWhitelistItem(items[0], 1);
            document.SetPolicyLevel(PolicySide.Agent, "read", PolicyLevel.Deny);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var after = Reload(path).Value!.PreservedUnknownKeys;
            Assert.That(
                after["$.extensions.whitelist.operations[1].u1"].CanonicalText,
                Is.EqualTo(before["$.extensions.whitelist.operations[0].u1"].CanonicalText),
                "The moved entry keeps its own unknown values at its new index.");
            Assert.That(
                after["$.extensions.whitelist.operations[1].u9"].CanonicalText,
                Is.EqualTo(before["$.extensions.whitelist.operations[0].u9"].CanonicalText));
            Assert.That(
                after["$.extensions.whitelist.operations[1].note"].CanonicalText,
                Is.EqualTo(before["$.extensions.whitelist.operations[0].note"].CanonicalText));
            Assert.That(after["$.policy.agent.u8"].CanonicalText,
                Is.EqualTo(before["$.policy.agent.u8"].CanonicalText),
                "Unknown policy members stay under the same parent.");
            Assert.That(after["$.policy.agent.u9"].CanonicalText,
                Is.EqualTo(before["$.policy.agent.u9"].CanonicalText));
            Assert.That(Reload(path).Value!.AgentPolicy["read"], Is.EqualTo(PolicyLevel.Deny));
        }

        // ── T29: non-object whitelist elements are verbatim at their index ───

        [Test]
        public void V2NonObjectWhitelistElementsAreVerbatimAtElementPath()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[42]," +
                "\"typeProviders\":[\"x\"]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            document.SetAgentLanguage("ja");
            Saved(document.Save());

            var whitelist = (JObject)((JObject)SavedRoot(path)["extensions"]!)["whitelist"]!;
            var operations = (JArray)whitelist["operations"]!;
            var typeProviders = (JArray)whitelist["typeProviders"]!;
            Assert.That(Canon(operations[0]), Is.EqualTo("42"));
            Assert.That(Canon(typeProviders[0]), Is.EqualTo("\"x\""));
            var reloaded = Reload(path);
            Assert.IsFalse(reloaded.Ok);
            Assert.That(DiagnosticPaths(reloaded, ErrorCodes.OW_CFG_002),
                Is.EquivalentTo(new[]
                {
                    "$.extensions.whitelist.operations[0]",
                    "$.extensions.whitelist.typeProviders[0]",
                }), "Non-object elements keep their OW-CFG-002 at the element path.");
        }

        // ── Explicit edits round-trip through the file ────────────────────────

        [Test]
        public void PolicyEditsWriteOnlyTheTouchedMembers()
        {
            var path = WriteFixture(
                "{\"version\":2,\"policy\":{\"agent\":{\"read\":\"deny\"}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            document.SetPolicyLevel(PolicySide.Agent, EffectNames.AssetDelete, PolicyLevel.Deny);
            document.SetPolicyLevel(PolicySide.Human, EffectNames.Compile, PolicyLevel.Ask);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var settings = Reload(path).Value!;
            Assert.That(settings.AgentPolicy["read"], Is.EqualTo(PolicyLevel.Deny),
                "An untouched policy member keeps its file value.");
            Assert.That(settings.AgentPolicy["asset.delete"], Is.EqualTo(PolicyLevel.Deny));
            Assert.That(settings.HumanPolicy["compile"], Is.EqualTo(PolicyLevel.Ask));
        }

        [Test]
        public void CodeWriteCannotBeSetToAllow()
        {
            var document = OpWeaveSettingsDocument.Load(Path.Combine(_dir, "absent.json"));
            Assert.Throws<ArgumentException>(() =>
                document.SetPolicyLevel(PolicySide.Agent, EffectNames.CodeWrite, PolicyLevel.Allow));
            Assert.Throws<ArgumentException>(() =>
                document.SetPolicyLevel(PolicySide.Human, EffectNames.CodeWrite, PolicyLevel.Allow));
            Assert.Throws<ArgumentException>(() =>
                document.SetPolicyLevel(PolicySide.Agent, "not.an.effect", PolicyLevel.Ask));
        }

        [Test]
        public void WhitelistAndListEditsRoundTrip()
        {
            var path = WriteFixture("{\"version\":2}");

            var document = OpWeaveSettingsDocument.Load(path);
            document.SetDiscoveryMode(DiscoveryMode.Whitelist);
            document.SetIntegrityLevel(IntegrityLevel.Full);
            document.SetWhitelistedOperations(new[]
            {
                new OperationWhitelistEntry("@project", "My.Ops.Editor", "my.op", 3),
            });
            document.SetWhitelistedTypeProviders(new[]
            {
                new TypeProviderWhitelistEntry("com.example.types", "example.types"),
            });
            document.SetStringList(
                SettingsStringListKey.AllowedPaths, new[] { "Assets/", "Assets/Extra/" });
            document.SetStringList(
                SettingsStringListKey.DisabledOperations, new[] { "my.op" });
            document.SetAgentLanguage("ja");
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var settings = Reload(path).Value!;
            Assert.That(settings.DiscoveryMode, Is.EqualTo(DiscoveryMode.Whitelist));
            Assert.That(settings.IntegrityLevel, Is.EqualTo(IntegrityLevel.Full));
            Assert.That(settings.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("@project", "My.Ops.Editor", "my.op", 3),
            }));
            Assert.That(settings.WhitelistedTypeProviders, Is.EqualTo(new[]
            {
                new TypeProviderWhitelistEntry("com.example.types", "example.types"),
            }));
            Assert.That(settings.AllowedPaths,
                Is.EqualTo(new[] { "Assets/", "Assets/Extra/" }));
            Assert.That(settings.DisabledOperations, Is.EqualTo(new[] { "my.op" }));
            Assert.That(settings.AgentLanguage, Is.EqualTo("ja"));
        }

        [Test]
        public void InvalidPackageSourceEditIsRejectedByTheDocument()
        {
            var document = OpWeaveSettingsDocument.Load(Path.Combine(_dir, "absent.json"));
            Assert.Throws<ArgumentException>(() =>
                document.SetStringList(
                    SettingsStringListKey.AllowedPackageSources,
                    new[] { "git:https://user:pw@example.com/r.git" }));
            Assert.Throws<ArgumentException>(() =>
                document.SetWhitelistedOperations(new[]
                {
                    new OperationWhitelistEntry("*", "A", "o", 1),
                }));
        }

        [Test]
        public void GetRawValueExposesTheWorkingCopy()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"discoveryMode\":\"manual\"}}");
            var document = OpWeaveSettingsDocument.Load(path);

            Assert.That(
                document.GetRawValue("extensions", "discoveryMode")!.CanonicalText,
                Is.EqualTo("\"manual\""));
            Assert.IsNull(document.GetRawValue("extensions", "missing"));
            Assert.IsNull(document.GetRawValue("extensions", "discoveryMode", "deeper"));

            document.SetDiscoveryMode(DiscoveryMode.Whitelist);
            Assert.That(
                document.GetRawValue("extensions", "discoveryMode")!.CanonicalText,
                Is.EqualTo("\"whitelist\""));
        }

        // ── Whitelist list edits keep retained entries' unknown properties ────

        [Test]
        public void WhitelistOperationEditKeepsUnknownEntryProperties()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p1\",\"assemblyName\":\"A1\",\"operationId\":\"o1\"," +
                "\"version\":1,\"note\":\"keep-1\"}," +
                "{\"packageId\":\"p2\",\"assemblyName\":\"A2\",\"operationId\":\"o2\"," +
                "\"version\":2,\"note\":\"keep-2\",\"extra\":{\"x\":1}}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.TryGetWhitelistedOperations(out var entries));
            var next = new List<OperationWhitelistEntry>(entries)
            {
                [0] = new OperationWhitelistEntry("p1", "A1", "o1-edited", 1),
            };
            document.SetWhitelistedOperations(next);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var text = VersionText(path);
            Assert.That(text, Does.Contain("\"operationId\": \"o1-edited\""));
            var preserved = Reload(path).Value!.PreservedUnknownKeys;
            Assert.That(
                preserved["$.extensions.whitelist.operations[0].note"].CanonicalText,
                Is.EqualTo("\"keep-1\""),
                "An in-place edit keeps the entry's own unknown properties.");
            Assert.That(
                preserved["$.extensions.whitelist.operations[1].note"].CanonicalText,
                Is.EqualTo("\"keep-2\""));
            Assert.That(
                preserved["$.extensions.whitelist.operations[1].extra"].CanonicalText,
                Is.EqualTo("{\"x\":1}"));
        }

        [Test]
        public void WhitelistOperationAddKeepsExistingEntryProperties()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"operations\":[" +
                "{\"packageId\":\"p1\",\"assemblyName\":\"A1\",\"operationId\":\"o1\"," +
                "\"version\":1,\"note\":\"keep\"}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.TryGetWhitelistedOperations(out var entries));
            var next = new List<OperationWhitelistEntry>(entries)
            {
                new OperationWhitelistEntry("@project", "New.Asm", "new.op", 1),
            };
            document.SetWhitelistedOperations(next);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path).Value!;
            Assert.That(reloaded.WhitelistedOperations, Is.EqualTo(new[]
            {
                new OperationWhitelistEntry("p1", "A1", "o1", 1),
                new OperationWhitelistEntry("@project", "New.Asm", "new.op", 1),
            }));
            Assert.That(
                reloaded.PreservedUnknownKeys["$.extensions.whitelist.operations[0].note"]
                    .CanonicalText,
                Is.EqualTo("\"keep\""));
        }

        [Test]
        public void WhitelistTypeProviderEditKeepsUnknownEntryProperties()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"typeProviders\":[" +
                "{\"packageId\":\"p1\",\"providerId\":\"tp1\",\"note\":\"keep-1\"}," +
                "{\"packageId\":\"p2\",\"providerId\":\"tp2\",\"note\":\"keep-2\"}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.TryGetWhitelistedTypeProviders(out var entries));
            var next = new List<TypeProviderWhitelistEntry>(entries)
            {
                [0] = new TypeProviderWhitelistEntry("p1", "tp1-edited"),
            };
            document.SetWhitelistedTypeProviders(next);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var text = VersionText(path);
            Assert.That(text, Does.Contain("\"providerId\": \"tp1-edited\""));
            var preserved = Reload(path).Value!.PreservedUnknownKeys;
            Assert.That(
                preserved["$.extensions.whitelist.typeProviders[0].note"].CanonicalText,
                Is.EqualTo("\"keep-1\""));
            Assert.That(
                preserved["$.extensions.whitelist.typeProviders[1].note"].CanonicalText,
                Is.EqualTo("\"keep-2\""));
        }

        [Test]
        public void WhitelistTypeProviderAddKeepsExistingEntryProperties()
        {
            var path = WriteFixture(
                "{\"version\":2,\"extensions\":{\"whitelist\":{\"typeProviders\":[" +
                "{\"packageId\":\"p1\",\"providerId\":\"tp1\",\"note\":\"keep\"}]}}}");

            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsTrue(document.TryGetWhitelistedTypeProviders(out var entries));
            var next = new List<TypeProviderWhitelistEntry>(entries)
            {
                new TypeProviderWhitelistEntry("com.example.types", "tp.new"),
            };
            document.SetWhitelistedTypeProviders(next);
            var saved = Saved(document.Save());
            Assert.IsTrue(saved.Ok);

            var reloaded = Reload(path).Value!;
            Assert.That(reloaded.WhitelistedTypeProviders, Is.EqualTo(new[]
            {
                new TypeProviderWhitelistEntry("p1", "tp1"),
                new TypeProviderWhitelistEntry("com.example.types", "tp.new"),
            }));
            Assert.That(
                reloaded.PreservedUnknownKeys["$.extensions.whitelist.typeProviders[0].note"]
                    .CanonicalText,
                Is.EqualTo("\"keep\""));
        }

        // ── Write failure: typed outcome, never a misused error code ─────────

        [Test]
        public void WriteFailureReturnsTypedOutcomeAndLeavesTheFile()
        {
            var path = WriteFixture("{\"version\":2}");
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                var document = OpWeaveSettingsDocument.Load(path);
                // The held destination is retried inside the bounded budget
                // before the save fails closed; the fake clock keeps the
                // waits instant.
                FakeRetryClock(document, new List<int>());
                var outcome = document.Save();

                Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.WriteFailed));
                Assert.That(outcome.FailureMessage, Is.Not.Null.And.Not.Empty);
                Assert.That(outcome.ExceptionTypeName, Is.Not.Null.And.Not.Empty);
                Assert.That(outcome.Diagnostics, Is.Empty,
                    "A write failure is not a settings diagnostic; OW-CFG-001 " +
                    "keeps its specified read-failure meaning.");
                Assert.That(VersionText(path), Is.EqualTo("{\"version\":2}"));
            }
            finally
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }

        // ── T-W3.4-05: bounded retry of a transient write hold (Issue #151) ──
        // A transient external hold on the file or its temporary replacement —
        // an indexer or virus scanner briefly holding one open — is retried
        // inside the bounded budget; every attempt re-proves the destination
        // against the loaded content and rewrites the replacement, a hold that
        // releases into changed or vanished content is refused, and a
        // persistent or non-transient failure still returns WriteFailed. The
        // write-step probe makes every failure deterministic; on the pre-fix
        // implementation the first injected hold surfaced WriteFailed.

        // A fake retry clock/sleep pair: the sleep records each delay and
        // advances the clock, so the budget is exercised instantly and the
        // backoff sequence is asserted exactly.
        private static void FakeRetryClock(
            OpWeaveSettingsDocument document, List<int> delays)
        {
            long now = 0;
            document.SaveRetryClock = () => now;
            document.SaveRetrySleep = delay =>
            {
                delays.Add(delay);
                now += delay;
            };
        }

        [Test]
        public void TransientWriteFailureClassification()
        {
            var transient = new Exception[]
            {
                new IOException("sharing violation", unchecked((int)0x80070020)),
                new IOException("lock violation", unchecked((int)0x80070021)),
                new IOException("access denied", unchecked((int)0x80070005)),
                new IOException(
                    "unable to remove replaced", unchecked((int)0x80070497)),
                // The same Win32 errors as bare codes (0x0000xxxx), the
                // form new IOException(message, code) stores.
                new IOException("bare sharing violation", 0x20),
                new IOException("bare lock violation", 0x21),
                new IOException("bare access denied", 0x05),
                new IOException("bare unable to remove replaced", 0x497),
                // COR_E_UNAUTHORIZEDACCESS shares the Win32 access-denied
                // HResult: File.Replace surfaces a held target either way.
                new UnauthorizedAccessException(),
            };
            foreach (var e in transient)
            {
                Assert.IsTrue(
                    OpWeaveSettingsDocument.IsTransientWriteFailure(e),
                    e.GetType().Name + " (HResult 0x" +
                    e.HResult.ToString("X8") + ") must be transient");
            }

            var notTransient = new Exception[]
            {
                // ERROR_UNABLE_TO_MOVE_REPLACEMENT[2] (1176/1177) can leave
                // the destination removed or renamed — never retried.
                new IOException(
                    "unable to move replacement", unchecked((int)0x80070498)),
                new IOException(
                    "unable to move replacement 2", unchecked((int)0x80070499)),
                new IOException("disk full", unchecked((int)0x80070070)),
                new IOException("already exists", unchecked((int)0x800700B7)),
                new FileNotFoundException("file not found"),
                new DirectoryNotFoundException("directory not found"),
                new IOException("no win32 code"),
                // A Win32 error's low word under a non-Win32 facility is
                // not a Win32 error: 32, 33, 5 and 1175 under FACILITY_ITF
                // (0x8004xxxx) are not transient.
                new IOException("itf 32", unchecked((int)0x80040020)),
                new IOException("itf 33", unchecked((int)0x80040021)),
                new IOException("itf 5", unchecked((int)0x80040005)),
                new IOException("itf 1175", unchecked((int)0x80040497)),
            };
            foreach (var e in notTransient)
            {
                Assert.IsFalse(
                    OpWeaveSettingsDocument.IsTransientWriteFailure(e),
                    e.GetType().Name + " (HResult 0x" +
                    e.HResult.ToString("X8") + ") must not be retried");
            }
        }

        [TestCase(0x20)]
        [TestCase(0x21)]
        [TestCase(0x05)]
        [TestCase(0x497)]
        public void TransientWriteHoldRetriesAndSaves(int win32Error)
        {
            var path = WriteFixture("{\"version\":2,\"debugLogging\":null}");
            var document = OpWeaveSettingsDocument.Load(path);
            document.SetInteger(SettingsIntegerKey.TickBudgetMs, 16);
            var delays = new List<int>();
            FakeRetryClock(document, delays);
            var attempts = 0;
            document.SaveWriteStepProbe = (temp, target, targetExists) =>
            {
                Assert.IsTrue(targetExists);
                return attempts++ == 0
                    ? new IOException(
                        "simulated hold",
                        unchecked((int)0x80070000) | win32Error)
                    : null;
            };

            var outcome = document.Save();

            Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.Saved),
                "a hold released within the budget saves");
            Assert.That(attempts, Is.EqualTo(2),
                "the transient hold is retried exactly once");
            Assert.That(delays, Is.EqualTo(new[] { 50 }),
                "the first retry waits the first backoff delay");
            Assert.That(SavedRoot(path)["debugLogging"]!.Type,
                Is.EqualTo(JTokenType.Null),
                "the explicit null is still written back verbatim");
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"),
                "the temporary replacement is cleaned up");
        }

        [Test]
        public void TransientAccessDeniedRetriesAndSaves()
        {
            var path = WriteFixture("{\"version\":2}");
            var document = OpWeaveSettingsDocument.Load(path);
            var delays = new List<int>();
            FakeRetryClock(document, delays);
            var attempts = 0;
            document.SaveWriteStepProbe = (temp, target, targetExists) =>
                attempts++ == 0 ? new UnauthorizedAccessException() : (Exception?)null;

            var outcome = document.Save();

            Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.Saved));
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(delays, Is.EqualTo(new[] { 50 }));
        }

        [Test]
        public void TransientHoldOnMissingFileRetriesAndSaves()
        {
            var path = Path.Combine(_dir, "created", "OpWeaveSettings.json");
            var document = OpWeaveSettingsDocument.Load(path);
            Assert.IsFalse(document.FileExists);
            var delays = new List<int>();
            FakeRetryClock(document, delays);
            var attempts = 0;
            document.SaveWriteStepProbe = (temp, target, targetExists) =>
            {
                Assert.IsFalse(targetExists);
                return attempts++ == 0
                    ? new IOException("simulated hold", unchecked((int)0x80070020))
                    : null;
            };

            var outcome = document.Save();

            Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.Saved),
                "a transient hold on the create path is retried too");
            Assert.That(attempts, Is.EqualTo(2));
            Assert.That(delays, Is.EqualTo(new[] { 50 }));
            Assert.IsTrue(File.Exists(path));
            var reloaded = Reload(path);
            Assert.IsTrue(reloaded.Ok);
            Assert.That(reloaded.Value!.RetainRuns, Is.EqualTo(50));
        }

        [Test]
        public void TransientHoldReleasingIntoExternalChangeIsRefused()
        {
            var path = WriteFixture("{\"version\":2}");
            var document = OpWeaveSettingsDocument.Load(path);
            FakeRetryClock(document, new List<int>());
            var attempts = 0;
            document.SaveWriteStepProbe = (temp, target, targetExists) =>
            {
                attempts++;
                File.WriteAllText(target, "{\"version\":2,\"future\":1}");
                return new IOException("simulated hold", unchecked((int)0x80070020));
            };

            var outcome = document.Save();

            Assert.That(outcome.Status,
                Is.EqualTo(SettingsSaveStatus.ChangedExternally),
                "a hold that releases into new content is refused, not overwritten");
            Assert.That(VersionText(path),
                Is.EqualTo("{\"version\":2,\"future\":1}"),
                "the external bytes are left untouched");
            Assert.That(attempts, Is.EqualTo(1),
                "the second attempt refuses at re-verification, before the seam");
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Test]
        public void TransientHoldReleasingIntoDeletionIsRefused()
        {
            var path = WriteFixture("{\"version\":2}");
            var document = OpWeaveSettingsDocument.Load(path);
            FakeRetryClock(document, new List<int>());
            var attempts = 0;
            document.SaveWriteStepProbe = (temp, target, targetExists) =>
            {
                attempts++;
                File.Delete(target);
                return new IOException("simulated hold", unchecked((int)0x80070020));
            };

            var outcome = document.Save();

            Assert.That(outcome.Status,
                Is.EqualTo(SettingsSaveStatus.ChangedExternally));
            Assert.IsFalse(File.Exists(path),
                "the vanished destination is never recreated");
            Assert.That(attempts, Is.EqualTo(1),
                "the second attempt refuses at re-verification, before the seam");
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Test]
        public void PersistentWriteHoldFailsClosedAfterBudget()
        {
            var path = WriteFixture("{\"version\":2}");
            var document = OpWeaveSettingsDocument.Load(path);
            var delays = new List<int>();
            FakeRetryClock(document, delays);
            var attempts = 0;
            document.SaveWriteStepProbe = (temp, target, targetExists) =>
            {
                attempts++;
                return new IOException("simulated hold", unchecked((int)0x80070020));
            };

            var outcome = document.Save();

            Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.WriteFailed),
                "a hold that never releases fails closed once the budget is used up");
            Assert.That(outcome.FailureMessage, Is.Not.Null.And.Not.Empty);
            Assert.That(outcome.ExceptionTypeName, Is.EqualTo("IOException"));
            Assert.That(attempts, Is.EqualTo(7),
                "seven attempts fit inside the 2000 ms budget");
            Assert.That(delays,
                Is.EqualTo(new[] { 50, 100, 200, 400, 500, 500 }),
                "the backoff doubles from 50 ms, caps at 500 ms per delay " +
                "and stays within the 2000 ms budget");
            Assert.That(VersionText(path), Is.EqualTo("{\"version\":2}"),
                "the destination's bytes are unchanged");
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Test]
        public void NonTransientWriteFailureIsNotRetried()
        {
            var path = WriteFixture("{\"version\":2}");
            var document = OpWeaveSettingsDocument.Load(path);
            var delays = new List<int>();
            FakeRetryClock(document, delays);
            var attempts = 0;
            document.SaveWriteStepProbe = (temp, target, targetExists) =>
            {
                attempts++;
                return new IOException("disk full", unchecked((int)0x80070070));
            };

            var outcome = document.Save();

            Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.WriteFailed),
                "a genuine write error is never retried into a false success");
            Assert.That(attempts, Is.EqualTo(1));
            Assert.IsEmpty(delays, "no retry delay was slept");
            Assert.That(VersionText(path), Is.EqualTo("{\"version\":2}"));
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Test]
        public void DestinationVanishedAtWriteIsChangedExternally()
        {
            var path = WriteFixture("{\"version\":2}");
            var document = OpWeaveSettingsDocument.Load(path);
            FakeRetryClock(document, new List<int>());
            document.SaveWriteStepProbe = (temp, target, targetExists) =>
            {
                File.Delete(target);
                return (Exception?)new FileNotFoundException("simulated vanish");
            };

            var outcome = document.Save();

            Assert.That(outcome.Status,
                Is.EqualTo(SettingsSaveStatus.ChangedExternally),
                "a destination that vanished before the write is refused");
            Assert.IsFalse(File.Exists(path));
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }

        [Test]
        public void ReplacementVanishedAtWriteIsWriteFailed()
        {
            var path = WriteFixture("{\"version\":2}");
            var document = OpWeaveSettingsDocument.Load(path);
            FakeRetryClock(document, new List<int>());
            var attempts = 0;
            document.SaveWriteStepProbe = (temp, target, targetExists) =>
            {
                attempts++;
                return (Exception?)new FileNotFoundException("simulated temp loss");
            };

            var outcome = document.Save();

            Assert.That(outcome.Status, Is.EqualTo(SettingsSaveStatus.WriteFailed),
                "the destination is provably unchanged, so the loss is a write fault");
            Assert.That(attempts, Is.EqualTo(1));
            Assert.That(VersionText(path), Is.EqualTo("{\"version\":2}"));
            Assert.IsEmpty(Directory.GetFiles(_dir, "*.tmp"));
        }
    }
}
