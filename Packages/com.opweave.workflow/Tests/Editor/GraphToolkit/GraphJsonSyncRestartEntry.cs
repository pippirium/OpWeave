#if UNITY_6000_5_OR_NEWER
#nullable enable

// T-W5.3-06 [Edit] restart recovery entries for the JSON sync intent
// protocol (SPEC §H7 item 3): the -executeMethod entry a child Editor
// launched by EditorProcessHarness runs inside the cloned project.
// Session 1 arranges the real state and dies inside the test-only fault
// point GraphJsonSync.PublishPersistFault — after the JSON publish,
// before the graph baseline persist — leaving the intent record, the
// published file and the unpersisted .opweavegraph exactly as a real
// crash would. A later session verifies the recovery through the
// explicit save entry GraphJsonSync.Save — the seam T-W5.3-08's command
// wraps — under a scripted prompt.
//
// Scenarios (OWP_EPH_SCENARIO); OWP_EPH_JOB carries the work directory
// and OWP_W53_CHOICE the terminal prompt answer for verify-b and
// verify-d-prompt ("reload" or "overwrite"):
//   arrange-a1     linked graph, edited and asset-saved: dies during the
//                  linked update's publish→persist. Session 2's persisted
//                  projection equals outputGuiHash — recovery (A1).
//   arrange-a2     unlinked, asset-saved graph: dies during the
//                  first-link create's publish→persist. The persisted
//                  graph has no jsonPath — recovery (A2).
//   arrange-b      linked graph, edited but NOT asset-saved: dies during
//                  the linked update's publish→persist. Session 2's
//                  persisted projection differs — recovery (B1). Run
//                  mutate-b2 afterwards for the post-crash-edit case (B2).
//   verify-a       session 2+: the explicit save's recovery finds the
//                  intent by GUID and reconciles — no prompt, no rebuild,
//                  no write (A).
//   verify-b       session 2+: exactly one three-choice prompt bound to
//                  R = hash(D); the graph is preserved until the choice;
//                  Cancel keeps the intent; the explicit retry shows one
//                  dialog again and completes with OWP_W53_CHOICE.
//   mutate-b2      post-crash session: changes and asset-saves the graph
//                  (the B2 precondition), then exits cleanly.
//   verify-c       invalid intent: reported exactly once, never acted
//                  on — the save is refused (the interim guard).
//   crash-d        session 2 on arrange-b state: the recovery prompt's
//                  Overwrite is killed mid publish→persist — the intent
//                  it leaves is its own update's — recovery (D).
//   mutate-d       post-crash session: changes and asset-saves the graph
//                  so session 3's projection differs — the D prompt case.
//   verify-d       session 3: projection matches the replaced intent's
//                  outputGuiHash — reconcile with no prompt.
//   verify-d-prompt session 3 after mutate-d: exactly one prompt bound to
//                  the new disk revision, completed with OWP_W53_CHOICE.
//
// Every scenario exits 0 on success; any failure writes fatal.txt and
// exits 3, so a hung or crashing child is a test failure, never a silent
// pass. The arrange scenarios that must die do so through
// Environment.FailFast inside the armed fault — no finally, no unwind.
// The open-time scenarios (verify-a-open, arrange-reopen, edit-json,
// verify-reopen, arrange-clean-save, verify-clean-open) belong to
// T-W5.3-07 and dispatch through the RunOpenScenario partial: a scenario
// it claims never exits synchronously — the EditorApplication.update
// subscription it installs waits for the real delayCall drain, then
// asserts and exits.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpWeave.Core;
using OpWeave.Core.Model;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Registry;
using OpWeave.GraphToolkit;
using OpWeave.UnityAdapter;
using Unity.GraphToolkit.Editor;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>The batchmode <c>-executeMethod</c> entry point of the
    /// T-W5.3-06 restart recovery children.</summary>
    public static partial class GraphJsonSyncRestartEntry
    {
        private const string Dir = "Assets/OpWeaveJsonRestart";
        private const string AssetPath = Dir + "/graph.opweavegraph";
        private const string JsonPath = Dir + "/graph.opweave.json";
        private const string ChoiceEnvVar = "OWP_W53_CHOICE";

        // The fixture graph content: a GUI-owned parameter ("name") the
        // session-1 edit changes so the exported output differs from the
        // linked JSON.
        private const string FixtureV1 = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.sync.restart"",
  ""title"": ""Restart fixture"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""alpha"", ""operation"": ""test.conv.alpha"", ""version"": 1,
      ""parameters"": { ""name"": ""Alpha"" } }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""alpha"" }
  ],
  ""x-owner"": { ""reviewed"": true }
}";

        private const string FixtureV2 = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.sync.restart"",
  ""title"": ""Restart fixture"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""alpha"", ""operation"": ""test.conv.alpha"", ""version"": 1,
      ""parameters"": { ""name"": ""Alpha2"" } }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""alpha"" }
  ],
  ""x-owner"": { ""reviewed"": true }
}";

        private const string FixtureV3 = @"{
  ""schemaVersion"": ""1.0"",
  ""id"": ""test.sync.restart"",
  ""title"": ""Restart fixture"",
  ""nodes"": [
    { ""id"": ""start"", ""operation"": ""core.start"", ""version"": 1 },
    { ""id"": ""alpha"", ""operation"": ""test.conv.alpha"", ""version"": 1,
      ""parameters"": { ""name"": ""Alpha3"" } }
  ],
  ""controlEdges"": [
    { ""from"": ""start"", ""to"": ""alpha"" }
  ],
  ""x-owner"": { ""reviewed"": true }
}";

        // The scripted §H7.3 answer a verify/crash scenario installs; the
        // arrange scenarios leave it null so the save runs the production
        // dialog surface, which a batch child resolves to Cancel
        // (SPEC §0.6 SG-17 — never a silent write).
        private static Func<GraphJsonSyncCheck, GraphJsonSyncChoice>? s_prompt;

        /// <summary>The -executeMethod entry: dispatches on
        /// OWP_EPH_SCENARIO, writes fatal.txt and exits nonzero on any
        /// failure so a hung or crashing child is a test failure, never a
        /// silent pass.</summary>
        public static void Run()
        {
            var scenario = Environment.GetEnvironmentVariable(
                EditorProcessHarness.ScenarioEnvVar) ?? "";
            var jobDir = Environment.GetEnvironmentVariable(
                EditorProcessHarness.JobDirEnvVar) ?? "";
            var choice = Environment.GetEnvironmentVariable(ChoiceEnvVar) ?? "overwrite";
            try
            {
                InstallSnapshot();
                // The open scenarios defer their exit: the
                // EditorApplication.update subscription T-W5.3-07's
                // RunOpenScenario installs waits for the real delayCall
                // drain before asserting.
                var deferred = false;
                switch (scenario)
                {
                    case "arrange-a1":
                        ArrangeLinked(persistEdit: true);
                        break;
                    case "arrange-a2":
                        ArrangeFirstLink();
                        break;
                    case "arrange-b":
                        ArrangeLinked(persistEdit: false);
                        break;
                    case "verify-a":
                        VerifyReconcile(jobDir);
                        break;
                    case "verify-b":
                        VerifyConflictRetry(jobDir, choice);
                        break;
                    case "mutate-b2":
                        MutatePersisted();
                        break;
                    case "verify-c":
                        VerifyInvalid(jobDir);
                        break;
                    case "crash-d":
                        CrashRecoveryOverwrite();
                        break;
                    case "mutate-d":
                        MutatePersisted();
                        break;
                    case "verify-d":
                        VerifyReconcile(jobDir);
                        break;
                    case "verify-d-prompt":
                        VerifyReplacedIntentPrompt(jobDir, choice);
                        break;
                    default:
                        var handled = false;
                        RunOpenScenario(scenario, jobDir, ref handled);
                        if (!handled)
                        {
                            throw new InvalidOperationException(
                                "unknown scenario '" + scenario + "'");
                        }
                        deferred = true;
                        break;
                }
                if (!deferred)
                {
                    EditorApplication.Exit(0);
                }
            }
            catch (Exception exception)
            {
                Fail(jobDir, exception);
            }
        }

        // T-W5.3-07's open-time scenarios: the partial claims a scenario
        // by setting handled — an open scenario never exits
        // synchronously, so a claimed scenario defers the Run's exit.
        // Unimplemented here, it compiles away and an open scenario name
        // is "unknown".
        static partial void RunOpenScenario(
            string scenario, string jobDir, ref bool handled);

        // The one failure path every scenario shares — the synchronous
        // catch and the deferred update tick alike: log, write fatal.txt
        // and exit nonzero so a hung or crashing child is a test failure,
        // never a silent pass.
        private static void Fail(string jobDir, Exception exception)
        {
            Debug.LogError("T-W5.3-06 restart child fatal: " + exception);
            try
            {
                EditorProcessHarness.AtomicWriteFile(
                    Path.Combine(jobDir, "fatal.txt"), exception.ToString());
            }
            catch (Exception)
            {
                // Best effort — a full disk must not mask the real failure.
            }
            EditorApplication.Exit(3);
        }

        // ── arrange phases ────────────────────────────────────────────

        // The linked-graph crash state: graph created, imported V1 and
        // first-linked through the real save path; the V2 edit imported;
        // when persistEdit the asset save makes the persisted graph equal
        // the exported one (case A), otherwise the persisted graph stays
        // at the pre-save state (case B). The armed fault then dies
        // inside the save's publish→persist — the intent record and the
        // published JSON stand, the baseline persist never ran.
        private static void ArrangeLinked(bool persistEdit)
        {
            var graph = NewGraph(FixtureV1);
            var first = SaveJson(graph);
            Require(
                first.SaveStatus == GraphJsonSaveStatus.Saved &&
                File.Exists(JsonPath),
                "the first-link save must create " + JsonPath +
                    " (status " + first.SaveStatus + ")");
            Import(graph, FixtureV2);
            if (persistEdit)
            {
                GraphDatabase.SaveGraph(graph);
            }
            ArmFault();
            SaveJson(graph);
            throw new InvalidOperationException(
                "the armed fault must kill the save inside publish→persist");
        }

        // The interrupted-first-link state: an asset-saved graph with no
        // recorded JSON path dies inside the save's create publish→
        // persist — the intent record and the created JSON stand, the
        // graph asset never learned the link (case A2).
        private static void ArrangeFirstLink()
        {
            var graph = NewGraph(FixtureV1);
            GraphDatabase.SaveGraph(graph);
            ArmFault();
            SaveJson(graph);
            throw new InvalidOperationException(
                "the armed fault must kill the save inside publish→persist");
        }

        // The B2 / D-prompt precondition: a clean session after the crash
        // changes the graph and saves the asset, so the next session's
        // persisted projection differs from the intent's outputGuiHash.
        private static void MutatePersisted()
        {
            var graph = LoadGraph();
            Import(graph, FixtureV3);
            GraphDatabase.SaveGraph(graph);
        }

        // The D-case crash: session 2 on the arrange-b state answers the
        // recovery conflict's prompt with Overwrite — and the armed fault
        // dies inside that Overwrite's own publish→persist, leaving the
        // replaced intent (base = the interrupted output, output = this
        // update's bytes) the next session must recognize.
        private static void CrashRecoveryOverwrite()
        {
            var graph = LoadGraph();
            var prompts = 0;
            s_prompt = check =>
            {
                prompts++;
                return GraphJsonSyncChoice.OverwriteJson;
            };
            ArmFault();
            SaveJson(graph);
            throw new InvalidOperationException(
                "the armed fault must kill the recovery overwrite inside publish→persist");
        }

        // ── verify phases ─────────────────────────────────────────────

        // Recovery (A) and the no-prompt half of (D): the next save finds
        // the intent by GUID, sees the published output already on disk
        // and a graph projection equal to outputGuiHash, and reconciles —
        // no prompt, no rebuild of the graph, no write to the JSON.
        private static void VerifyReconcile(string jobDir)
        {
            var graph = LoadGraph();
            var wasLinked = graph.jsonPath.Length != 0;
            var diskBefore = File.Exists(JsonPath)
                ? File.ReadAllBytes(JsonPath)
                : throw new InvalidOperationException(
                    "the published JSON must be on disk: " + JsonPath);
            var graphBefore = ProjectionOf(graph, diskBefore);
            var prompts = 0;
            s_prompt = check =>
            {
                prompts++;
                return GraphJsonSyncChoice.Cancel;
            };
            var result = SaveJson(graph);
            Require(
                result.SaveStatus == GraphJsonSaveStatus.Saved,
                "recovery must reconcile to Saved, got " + result.SaveStatus);
            Require(prompts == 0, "recovery must not prompt (saw " + prompts + ")");
            Require(
                File.ReadAllBytes(JsonPath).SequenceEqual(diskBefore),
                "reconcile must not write the JSON");
            Require(
                graph.jsonPath == JsonPath,
                "the reconcile must record the link " + JsonPath +
                    " (jsonPath: " + graph.jsonPath + ")");
            Require(
                graphBefore == ProjectionOf(graph, diskBefore),
                "reconcile must not rebuild the graph");
            Require(
                !wasLinked || graph.jsonPath == JsonPath,
                "a linked graph keeps its recorded path");
            var published = JsonSaveCoordinator.Sha256Hex(diskBefore);
            Require(
                IntentFileOf(graph) is null ||
                    (string.Equals(
                        IntentOutputHashOf(graph), published,
                        StringComparison.Ordinal) &&
                     !File.ReadAllText(AssetPath).Contains(published)),
                "the intent record must be cleared or stand as the " +
                    "pending-persist of exactly the published revision");
            Verdict(
                jobDir,
                "save reconciled" +
                (wasLinked ? "" : " and recorded the first link") +
                " with no prompt, no rebuild, no write");
        }

        // Recovery (B): the persisted graph differs from the intent's
        // outputGuiHash — exactly one three-choice prompt bound to
        // R = hash(D); the graph and its asset are preserved until the
        // choice; Cancel keeps the intent and a later explicit retry shows
        // exactly one dialog again, completing with the scenario's answer.
        private static void VerifyConflictRetry(string jobDir, string choice)
        {
            var graph = LoadGraph();
            var intentPath = IntentFileOf(graph);
            Require(intentPath is not null, "the pending intent must exist");
            var disk = File.ReadAllBytes(JsonPath);
            var diskHash = JsonSaveCoordinator.Sha256Hex(disk);
            var assetBefore = File.ReadAllBytes(AssetPath);
            var graphBefore = ProjectionOf(graph, disk);
            var revisions = new List<string>();
            var prompts = 0;
            s_prompt = check =>
            {
                prompts++;
                revisions.Add(check.PresentedRevision ?? "<none>");
                return prompts == 1
                    ? GraphJsonSyncChoice.Cancel
                    : ChoiceOf(choice);
            };

            // Cancel keeps the intent and touches nothing.
            var cancelled = SaveJson(graph);
            Require(
                cancelled.Outcome == GraphJsonSyncOutcome.Cancelled,
                "Cancel must report Cancelled, got " + cancelled.Outcome);
            Require(prompts == 1, "the conflict must show exactly one prompt");
            Require(
                revisions[0] == diskHash,
                "the prompt must be bound to R = hash(D)");
            Require(
                File.Exists(intentPath!), "Cancel must keep the intent");
            Require(
                File.ReadAllBytes(JsonPath).SequenceEqual(disk),
                "Cancel must not write the JSON");
            Require(
                File.ReadAllBytes(AssetPath).SequenceEqual(assetBefore),
                "the persisted graph must be preserved until a choice");
            Require(
                graphBefore == ProjectionOf(graph, disk),
                "the graph change must be preserved until a choice");

            // The later explicit request presents the retained recovery
            // conflict again — one dialog, then the scenario's answer.
            var resolved = SaveJson(graph);
            Require(prompts == 2,
                "the explicit retry must show exactly one dialog (saw " + prompts + ")");
            Require(revisions[1] == diskHash,
                "the retry prompt stays bound to the same D");
            Require(
                !File.Exists(intentPath!),
                "the applied choice clears the intent");
            if (choice == "reload")
            {
                Require(
                    resolved.Outcome == GraphJsonSyncOutcome.Reloaded,
                    "Reload must report Reloaded, got " + resolved.Outcome);
                Require(
                    File.ReadAllBytes(JsonPath).SequenceEqual(disk),
                    "Reload must not write the JSON");
                Require(
                    graph.syncedGuiOwnedHash ==
                        GraphJsonFile.GuiOwnedHash(
                            WorkflowSerializer.Read(
                                System.Text.Encoding.UTF8.GetString(disk)).Value!),
                    "Reload must record the published document's projection");
            }
            else
            {
                Require(
                    resolved.Outcome == GraphJsonSyncOutcome.Saved ||
                    resolved.Outcome == GraphJsonSyncOutcome.Overwritten,
                    "Overwrite must report a write outcome, got " + resolved.Outcome);
                var written = File.ReadAllBytes(JsonPath);
                Require(
                    graph.syncedBytesHash ==
                        JsonSaveCoordinator.Sha256Hex(written),
                    "Overwrite must move the baseline to the written revision");
            }
            Verdict(jobDir, "recovery conflict: cancel→retry→" + choice);
        }

        // Recovery (C): an intent whose GUID/path does not validate is
        // never acted on and is reported exactly once — the save is
        // refused (the interim guard) and the record's bytes stay exactly
        // as found.
        private static void VerifyInvalid(string jobDir)
        {
            var graph = NewGraph(FixtureV1);
            var first = SaveJson(graph);
            Require(
                first.SaveStatus == GraphJsonSaveStatus.Saved,
                "the first-link save must succeed before the invalid-intent gate");
            var jsonBefore = File.ReadAllBytes(JsonPath);
            var coordinator = GraphJsonSync.Coordinator();
            var guid = AssetDatabase.AssetPathToGUID(AssetPath);
            Require(guid.Length != 0, "the graph asset must have a GUID");
            var intentPath = coordinator.IntentPathFor(guid);
            var garbage = "{ not a valid intent record";
            EditorProcessHarness.AtomicWriteFile(intentPath, garbage);

            var warnings = new List<string>();
            Application.LogCallback listener = (message, stackTrace, type) =>
            {
                if (type == LogType.Warning &&
                    message.Contains("intent record"))
                {
                    warnings.Add(message);
                }
            };
            Application.logMessageReceived += listener;
            try
            {
                var second = SaveJson(graph);
                Require(
                    second.Outcome == GraphJsonSyncOutcome.Refused,
                    "an invalid intent refuses the save (outcome " +
                        second.Outcome + ")");
                var third = SaveJson(graph);
                Require(
                    third.Outcome == GraphJsonSyncOutcome.Refused,
                    "the invalid intent refuses every later save too");
            }
            finally
            {
                Application.logMessageReceived -= listener;
            }
            Require(warnings.Count == 1,
                "the invalid intent must be reported exactly once (saw " +
                    warnings.Count + ")");
            Require(
                File.ReadAllText(intentPath) == garbage,
                "the invalid intent is left exactly as found");
            Require(
                File.ReadAllBytes(JsonPath).SequenceEqual(jsonBefore),
                "the refused saves never write the JSON");
            Verdict(
                jobDir,
                "invalid intent reported once, never acted on, save refused");
        }

        // The prompt half of recovery (D): session 3's persisted
        // projection differs from the replaced intent's outputGuiHash —
        // exactly one prompt bound to the new disk revision, completed
        // with the scenario's answer. No conflict names the superseded
        // record.
        private static void VerifyReplacedIntentPrompt(string jobDir, string choice)
        {
            var graph = LoadGraph();
            var intentPath = IntentFileOf(graph);
            Require(intentPath is not null,
                "the replaced intent must survive the session-2 crash");
            var diskHash = JsonSaveCoordinator.Sha256Hex(
                File.ReadAllBytes(JsonPath));
            var prompts = 0;
            var revisions = new List<string>();
            s_prompt = check =>
            {
                prompts++;
                revisions.Add(check.PresentedRevision ?? "<none>");
                return ChoiceOf(choice);
            };
            var result = SaveJson(graph);
            Require(prompts == 1,
                "the replaced intent must show exactly one prompt (saw " +
                    prompts + ")");
            Require(revisions[0] == diskHash,
                "the prompt must be bound to the new disk revision");
            Require(
                result.SaveStatus == GraphJsonSaveStatus.Saved ||
                result.Outcome == GraphJsonSyncOutcome.Reloaded ||
                result.Outcome == GraphJsonSyncOutcome.Overwritten,
                "the choice must complete, got " + result.Outcome);
            var published = JsonSaveCoordinator.Sha256Hex(
                File.ReadAllBytes(JsonPath));
            Require(
                IntentFileOf(graph) is null ||
                    (string.Equals(
                        IntentOutputHashOf(graph), published,
                        StringComparison.Ordinal) &&
                     !File.ReadAllText(AssetPath).Contains(published)),
                "the applied choice clears the replaced intent or leaves " +
                    "the pending-persist of exactly the published revision");
            Verdict(jobDir, "replaced intent: one prompt bound to new D → " + choice);
        }

        // ── shared helpers ────────────────────────────────────────────

        // The explicit save the scenarios drive: the seam T-W5.3-08's
        // command wraps — the SPEC §H7 item 3 engine under the scripted
        // prompt, or the production dialog surface when none is set.
        private static GraphJsonSyncResult SaveJson(OpWeaveGraph graph) =>
            GraphJsonSync.Save(
                graph, s_prompt ?? GraphJsonSyncDialog.ShowConflictPrompt);

        // The crash inside publish→persist: durable intent + durable JSON,
        // no baseline persist — the exact state recovery reconciles from.
        private static void ArmFault() =>
            GraphJsonSync.PublishPersistFault = _ =>
                Environment.FailFast("T-W5.3-06 restart fault");

        private static OpWeaveGraph NewGraph(string fixture)
        {
            if (!AssetDatabase.IsValidFolder(Dir))
            {
                AssetDatabase.CreateFolder("Assets", "OpWeaveJsonRestart");
            }
            if (AssetDatabase.LoadMainAssetAtPath(AssetPath) is not null)
            {
                AssetDatabase.DeleteAsset(AssetPath);
            }
            var graph = GraphDatabase.CreateGraph<OpWeaveGraph>(AssetPath);
            Require(graph is not null, "GraphDatabase.CreateGraph must return a graph");
            Import(graph!, fixture);
            return graph!;
        }

        private static OpWeaveGraph LoadGraph()
        {
            var graph = GraphDatabase.LoadGraph<OpWeaveGraph>(AssetPath);
            Require(graph is not null, "the graph asset must load: " + AssetPath);
            return graph!;
        }

        private static void Import(OpWeaveGraph graph, string fixture)
        {
            var read = GraphJsonConverter.Import(graph, fixture);
            Require(read.Ok, "the fixture must import");
        }

        // The graph's GUI-owned projection over the disk document's
        // non-owned members — the X the recovery compares against the
        // intent's outputGuiHash.
        private static string ProjectionOf(OpWeaveGraph graph, byte[] diskBytes)
        {
            var text = System.Text.Encoding.UTF8.GetString(diskBytes);
            var disk = WorkflowSerializer.Read(text);
            Require(disk.Ok && disk.Value is not null, "the disk JSON must parse");
            var exported = GraphJsonConverter.Export(graph, disk.Value!);
            Require(exported.Ok && exported.Value is not null,
                "the graph must project over the disk document");
            return GraphJsonFile.GuiOwnedHash(exported.Value!);
        }

        // The intent record path for this graph, or null when none exists.
        private static string? IntentFileOf(OpWeaveGraph graph)
        {
            var guid = AssetDatabase.AssetPathToGUID(AssetPath);
            Require(guid.Length != 0, "the graph asset must have a GUID");
            var path = GraphJsonSync.Coordinator().IntentPathFor(guid);
            return File.Exists(path) ? path : null;
        }

        // The graph's intent record when it validates against the
        // recorded link or the sibling path — the record recovery acts
        // on — or null while none stands or none validates.
        private static JsonSyncIntent? ValidIntentOf(OpWeaveGraph graph)
        {
            var guid = AssetDatabase.AssetPathToGUID(AssetPath);
            Require(guid.Length != 0, "the graph asset must have a GUID");
            var read = GraphJsonSync.Coordinator().ReadIntent(
                guid, graph.jsonPath, JsonPath);
            return read.State == IntentReadState.Valid ? read.Intent : null;
        }

        // The published revision a standing intent record still owes a
        // persist for — its outputHash — or null when no actionable
        // record stands for this graph.
        private static string? IntentOutputHashOf(OpWeaveGraph graph) =>
            ValidIntentOf(graph)?.OutputHash;

        private static GraphJsonSyncChoice ChoiceOf(string choice) =>
            choice switch
            {
                "reload" => GraphJsonSyncChoice.ReloadJson,
                "cancel" => GraphJsonSyncChoice.Cancel,
                "overwrite" => GraphJsonSyncChoice.OverwriteJson,
                _ => throw new InvalidOperationException(
                    "unknown OWP_W53_CHOICE '" + choice + "'"),
            };

        private static void Verdict(string jobDir, string summary)
        {
            try
            {
                EditorProcessHarness.AtomicWriteFile(
                    Path.Combine(jobDir, "verdict.txt"), summary + "\n");
            }
            catch (Exception)
            {
                // The verdict file is evidence, not the gate — the exit
                // code carries the outcome.
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        // The conversion fixture's registry: the test.* Operation the
        // fixture documents name must exist for Import to build the
        // graph — the same snapshot the EditMode tests install.
        private static void InstallSnapshot() =>
            GraphToolkitRegistry.SnapshotSource = () => s_snapshot ??= BuildSnapshot();

        private static RegistrySnapshot? s_snapshot;

        private static RegistrySnapshot BuildSnapshot()
        {
            var descriptors = new[]
            {
                Op.Define("test.conv.alpha", 1)
                    .Summary("Sync fixture alpha.")
                    .Input("target", "unity.GameObjectRef", required: true)
                    .Input("name", "core.String", required: false)
                    .Output("result", "unity.GameObjectRef")
                    .Effects(EffectNames.Read)
                    .Idempotency(Idempotency.Idempotent)
                    .Build(),
            };
            var operations = descriptors
                .Select((d, i) => new RegisteredOperation(
                    d,
                    OperationOrigin.Custom,
                    "OpWeave.UnityAdapter.Tests",
                    null,
                    new string('a', 64),
                    new string('A', 64))
                {
                    // OperationValidationPhase refuses a snapshot entry
                    // without an implementation instance: a published §C3
                    // snapshot always carries the instantiated Operation
                    // (step 6), and the child Editor's live validation
                    // compiles every graph processing pass against this
                    // snapshot — the fixture op needs a no-op one.
                    Implementation = new NoopOperation(d),
                })
                .ToArray();
            return new RegistrySnapshot(
                "reg_01J00000000000000000000000",
                new string('f', 64),
                operations,
                Types,
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());
        }

        // The no-op IOperation every snapshot entry carries: a live
        // validation reaches it only through the D3 phase's
        // ValidateStatic — it is no IEffectResolver for the D6 phase to
        // consult, and the Runner lifecycle members never run on a
        // fixture op.
        private sealed class NoopOperation : IOperation
        {
            internal NoopOperation(OperationDescriptor descriptor) =>
                Descriptor = descriptor;

            public OperationDescriptor Descriptor { get; }

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Execute(StepContext ctx) =>
                throw new NotSupportedException();
            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException();
            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                throw new NotSupportedException();
            public void Cancel(StepContext ctx)
            {
            }
        }

        private static ITypeRegistry? s_types;

        private static ITypeRegistry Types =>
            s_types ??= TypeRegistryBuilder.Build(
                    new[]
                    {
                        new TypeProviderCandidate(
                            CoreTypeProvider.ProviderId,
                            new CoreTypeProvider(),
                            OperationOrigin.Standard),
                        new TypeProviderCandidate(
                            UnityTypeProvider.ProviderId,
                            new UnityTypeProvider(),
                            OperationOrigin.Standard),
                    },
                    new UnityClassRelationProvider())
                .Registry;
    }
}
#endif
