#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Persistence;
using OpWeave.Core.Runtime;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.Testing
{
    /// <summary>
    /// Runs one Operation in isolation and cleans up everything it and the
    /// Operation created (SPEC §M.6 item 1) — the fixture a contract test of an
    /// Operation, a TypeProvider or a Codec is written on, because a standard
    /// Operation and a custom one are held to the same contract and both have to
    /// be run the same way.
    /// <code>
    /// using var harness = OperationTestHarness.Create();
    /// var result = harness.Run("unity.asset.create_folder", new { path = harness.TempFolder + "/New" });
    /// Assert.That(result.Ok, Is.True);
    /// Assert.That(result.Outputs["path"], Is.Not.Null);
    /// </code>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the run shares with the Runner.</b> The step is assembled the way
    /// SPEC §E4 assembles it: the Descriptor's ports resolve against the standard
    /// Type Registry, the Operation gets the same <see cref="UnityStepContext"/>
    /// with the same §F7 recorder and the same §F6 log sink, the named SPEC §G3
    /// undo group is open while <c>Execute</c> runs, and the SPEC §F2 records are
    /// produced in the write-ahead order — <c>step_started</c> with the Preflight
    /// snapshot and the inputs digest before any side effect, then the outcome
    /// record. The §M.6 pass condition is that outputs, changes and diagnostics
    /// equal a normal run's, so a harness run goes through production objects
    /// rather than a look-alike.
    /// </para>
    /// <para>
    /// <b>What a harness run is not.</b> There is no Plan, no Run Store and no
    /// Run directory: a workflow-less run has no <c>planHash</c> to verify and
    /// nothing to resume, so the records the step would journal are kept in order
    /// on the <see cref="HarnessResult.Journal"/> instead of on disk, each one
    /// still encoding as a real journal line. The §E5 output validation is
    /// Plan-bound — a node's declared output type against the Registry — so
    /// <see cref="HarnessResult.Outputs"/> reports what the Operation wrote and
    /// SPEC §M.6 item 2's <c>OperationContract</c> is what checks a port's Codec
    /// round-trip. A Waiting result is reported as the outcome it is; nothing
    /// polls it, because a single isolated step has no deadline owner.
    /// </para>
    /// <para>
    /// <b>Ownership.</b> The harness owns the temporary folder under
    /// <c>Assets/OpWeaveTesting</c> and the temporary scene inside it, and
    /// <see cref="HarnessObjects"/> hands out the GameObjects of the active
    /// harness scene, which <see cref="Dispose"/> destroys before closing the
    /// scene without saving it. Nothing outside those two — the caller's scenes,
    /// folders and selection — is touched: unsaved scene changes are always the
    /// caller's to keep or discard, a dirty untitled scene included, so the
    /// harness refuses to open its own scene while one has changes instead of
    /// taking that decision over, and a harness that cannot set its own scene
    /// up refuses to run rather than borrowing one.
    /// </para>
    /// </remarks>
    public sealed class OperationTestHarness : IDisposable
    {
        /// <summary>The one root folder every harness workspace sits under.</summary>
        private const string RootFolder = "Assets/OpWeaveTesting";

        // SPEC §E1 keeps the journal's `step` member an index into the Plan's
        // step list. There is no Plan here; the single step is step 0, the same
        // index a one-step workflow's only step carries.
        private const int StepIndex = 0;

        // A harness run never retries, so its one attempt is attempt 1 — the
        // value the first attempt of a Run carries.
        private const int Attempt = 1;

        // The step has no Plan node; this is the synthetic node identity the
        // journal records and §F7 change records carry, so a harness record is
        // recognizable as one instead of pretending to be a workflow node's.
        private const string NodeId = "harness";

        // The one synthetic Run identity every harness run claims for its SPEC
        // §E6 item 4 Run resources: a harness has no Run of its own, so all of
        // its runs — the caller's and the ones OperationContract creates
        // internally alike — act as sibling steps of this Run. That is the
        // shape §G6 item 5's open_contents → save_contents hand-off needs:
        // an editing session one harness run opened and bound is "this Run's"
        // for the save_contents run of any other harness, exactly as it is
        // for the sibling step of a real two-step workflow.
        internal const string RunScopeRunId = "harness";

        // SPEC §E3 item 2: the control port a Succeeded step returns through,
        // which its §F2 outcome record names.
        private const string NextPort = "next";

        private static readonly List<OperationTestHarness> s_live =
            new List<OperationTestHarness>();

        // The asset GUID of the current root folder when a harness created
        // it, or null — shared process state because every harness under one
        // root must agree on whether that root is theirs to remove: a
        // per-harness flag dies with the harness that created the root and
        // would strand root folder and .meta whenever that harness is not
        // the last to dispose. The last Dispose to find the root empty
        // removes it; a root that pre-existed the harnesses is never marked,
        // and one holding foreign content is never empty, so neither is ever
        // deleted. The GUID — not the path — identifies the harness-created
        // folder, so a root deleted and recreated outside the harness can
        // never be mistaken for it either.
        private static string? s_harnessCreatedRootGuid;

        private readonly string _id;
        private readonly string _folder;
        private readonly string _scenePath;
        private readonly string _baseScenePath;
        private readonly List<GameObject> _owned = new List<GameObject>();

        // The Run-wide tracked-resource list of the harness's synthetic Run:
        // it lives on the harness rather than on one run because the §E6
        // item 4 model keeps an acquisition after the step that journaled it
        // ends — a later step's IsHeldByRun sees what a sibling step acquired,
        // which is what a second harness run is here.
        private readonly List<TrackedRunResource> _runResources =
            new List<TrackedRunResource>();

        private SceneSetup[]? _savedSetup;
        private string? _activeScenePath;
        private bool _replacedSetup;
        private bool _disposed;

        private OperationTestHarness()
        {
            _id = Identifiers.NewUlid();
            _folder = RootFolder + "/harness-" + _id;
            _scenePath = _folder + "/opweave-harness-" + _id + ".unity";
            _baseScenePath = _folder + "/opweave-harness-base.unity";
            EnsureRootFolder();
            try
            {
                if (!AssetDatabase.IsValidFolder(_folder) &&
                    string.IsNullOrEmpty(AssetDatabase.CreateFolder(RootFolder, "harness-" + _id)))
                {
                    throw new InvalidOperationException(
                        $"Could not create the harness folder '{_folder}'.");
                }
            }
            catch (Exception)
            {
                // A harness that never came up leaves nothing behind either.
                DeleteOwnedFolders();
                throw;
            }
            // The Editor's main thread creates and disposes every harness, so
            // this list needs no lock — the same single-thread rule Core runs
            // under (SPEC §0.5).
            s_live.Add(this);
        }

        /// <summary>
        /// Creates the harness's temporary workspace: a unique folder under
        /// <c>Assets/OpWeaveTesting</c> that holds everything the harness and
        /// the runs it serves put on disk. The temporary scene is created by the
        /// first run that needs one, because a harness used only for asset work
        /// never needs it.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The workspace could not be created — the <c>Assets</c> folder is not
        /// available, or the asset database refused the new folder.
        /// </exception>
        public static OperationTestHarness Create() => new OperationTestHarness();

        /// <summary>
        /// The temporary folder's project-relative path. Everything a run writes
        /// without a caller asking for it lands here, and nothing the harness
        /// created lives outside it.
        /// </summary>
        public string TempFolder => _folder;

        /// <summary>
        /// The path the temporary scene is saved at, whether or not a run has
        /// needed it yet.
        /// </summary>
        public string TempScenePath => _scenePath;

        /// <summary>
        /// How a run treats a <see cref="StepResultKind.Waiting"/> outcome:
        /// <see cref="Report"/> delivers it once, the way the Runner's
        /// journal shows a suspended step before its resume;
        /// <see cref="Poll"/> arms the pump <see cref="RunToCompletion"/>
        /// uses to keep calling <see cref="IOperation.Poll"/> on the
        /// persisted resume token until the step settles.
        /// </summary>
        internal enum RunContinuation
        {
            /// <summary>Report the Waiting outcome as the run's result.</summary>
            Report,

            /// <summary>Drive Poll until the step settles or the round bound runs out.</summary>
            Poll,
        }

        // The bound on Poll calls one RunToCompletion run may make — the
        // editor-tick limit a real wait lives under: high enough that a
        // wait condition still genuinely Pending settles, low enough that
        // one that never settles cannot hang a test.
        private const int PollRoundBound = 240;

        /// <summary>
        /// Runs <paramref name="operationId"/> to its end: Preflight, the
        /// write-ahead record, <c>Execute</c> inside the Operation's undo group,
        /// then the step's outcome record.
        /// </summary>
        /// <param name="operationId">
        /// Operation ID of an <see cref="IOperation"/> class some loaded assembly
        /// declares — the standard set and a custom Operation alike.
        /// </param>
        /// <param name="inputs">
        /// The input values, one public member per declared input port: an
        /// anonymous object, a dictionary or a JSON object. See
        /// <see cref="HarnessInputs"/> for the value forms.
        /// </param>
        /// <param name="version">
        /// The Operation version to run; the Descriptor must declare it.
        /// </param>
        /// <exception cref="ObjectDisposedException">The harness was disposed.</exception>
        /// <exception cref="ArgumentException"><paramref name="operationId"/> is null or empty.</exception>
        /// <exception cref="InvalidOperationException">
        /// The run needs a scene and the harness could not create or save its own.
        /// </exception>
        public HarnessResult Run(string operationId, object inputs, int version = 1) =>
            Execute(operationId, inputs, version, null, RunContinuation.Report);

        /// <summary>
        /// <see cref="Run"/> that keeps driving a Waitable step's
        /// <see cref="IOperation.Poll"/> — each round on a fresh context
        /// carrying the persisted resume token, the same fresh-context
        /// boundary the Run Loop puts between polls — until the step reports
        /// Succeeded or Failed, or <see cref="PollRoundBound"/> rounds still
        /// left it pending, in which case the run reports the same Waiting a
        /// plain <see cref="Run"/> would.
        /// </summary>
        /// <param name="operationId">Operation ID, as for <see cref="Run"/>.</param>
        /// <param name="inputs">Input values, as for <see cref="Run"/>.</param>
        /// <param name="version">The Operation version to run, as for <see cref="Run"/>.</param>
        /// <exception cref="ObjectDisposedException">The harness was disposed.</exception>
        /// <exception cref="ArgumentException"><paramref name="operationId"/> is null or empty.</exception>
        /// <exception cref="InvalidOperationException">
        /// The run needs a scene and the harness could not create or save its own.
        /// </exception>
        internal HarnessResult RunToCompletion(
            string operationId, object inputs, int version = 1) =>
            Execute(operationId, inputs, version, null, RunContinuation.Poll);

        /// <summary>
        /// Runs as far as <paramref name="point"/> and stops there, which is what
        /// makes the SPEC §F5 reconciliation question testable: a crash between
        /// the write-ahead record and the outcome record leaves the step
        /// <see cref="StepState.Running"/> with no evidence of its own about
        /// whether the effect happened.
        /// </summary>
        /// <remarks>
        /// Both points leave the same journal — one <c>step_started</c> record
        /// with no outcome record — because that is exactly what the Editor's
        /// death leaves in a real Run's journal at either crash window. What
        /// differs, and what <c>Reconcile</c> exists to ask about, is whether the
        /// world already changed: at
        /// <see cref="InterruptPoint.BeforeExecute"/> <c>Execute</c> was never
        /// called, so nothing changed and nothing was recorded; at
        /// <see cref="InterruptPoint.AfterExecute"/> <c>Execute</c> ran and its
        /// side effect and §F7 change records are there, and the outputs the
        /// Operation wrote are reported, but no outcome record was produced.
        /// The step's undo group is closed either way — what an Editor crash
        /// leaves is the journaled state, not an open Undo group.
        /// </remarks>
        /// <param name="operationId">Operation ID, as for <see cref="Run"/>.</param>
        /// <param name="inputs">Input values, as for <see cref="Run"/>.</param>
        /// <param name="point">Where to stop.</param>
        /// <exception cref="ObjectDisposedException">The harness was disposed.</exception>
        /// <exception cref="ArgumentException"><paramref name="operationId"/> is null or empty.</exception>
        /// <exception cref="InvalidEnumArgumentException">
        /// <paramref name="point"/> is not an <see cref="InterruptPoint"/> value.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The run needs a scene and the harness could not create or save its own.
        /// </exception>
        public HarnessResult RunInterrupted(
            string operationId, object inputs, InterruptPoint point) =>
            Execute(operationId, inputs, 1, point, RunContinuation.Report);

        /// <summary>
        /// Destroys every <see cref="HarnessObjects"/> GameObject that is still
        /// alive, closes the temporary scene without saving it, puts the scene
        /// setup that was open beforehand back — a fresh empty scene when the
        /// setup held only untitled scenes, which name no asset to reopen — and
        /// deletes the temporary folder, so the project after
        /// <see cref="Dispose"/> is the project before <see cref="Create"/>:
        /// no scene, folder or asset left behind, and exactly one loaded
        /// scene active. Idempotent, and safe inside a <c>using</c>.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;

            for (var i = 0; i < _owned.Count; i++)
            {
                var owned = _owned[i];
                if (owned != null)
                {
                    UnityEngine.Object.DestroyImmediate(owned);
                }
            }
            _owned.Clear();

            try
            {
                RestoreSceneState();
            }
            finally
            {
                // A loaded scene's asset must never be deleted: on the rare
                // path the ordered restore above failed, whatever of the
                // harness's own is still open goes first — best-effort only,
                // the folder deletion proceeds either way.
                ForceCloseIfLoaded(_scenePath);
                ForceCloseIfLoaded(_baseScenePath);
                DeleteOwnedFolders();
                s_live.Remove(this);
                AssetDatabase.Refresh();
            }
        }

        /// <summary>
        /// The live harnesses in creation order — the list
        /// <see cref="HarnessObjects"/> resolves an unqualified
        /// <c>NewGameObject</c> against.
        /// </summary>
        internal static List<OperationTestHarness> LiveHarnesses() =>
            new List<OperationTestHarness>(s_live);

        /// <summary>
        /// The harness's temporary scene, opening it when no run has needed it
        /// yet — the scene <see cref="HarnessObjects"/> places an object in.
        /// </summary>
        /// <exception cref="ObjectDisposedException">The harness was disposed.</exception>
        /// <exception cref="InvalidOperationException">
        /// The temporary scene could not be created or saved.
        /// </exception>
        internal Scene OwnedScene()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OperationTestHarness));
            }
            EnsureScene();
            return FindScene(_scenePath) ?? throw new InvalidOperationException(
                $"The harness scene '{_scenePath}' is not open.");
        }

        // The harness's own folders only, and the shared root only while it
        // is still the very folder a harness created and it is empty — a
        // folder someone else created or filled is never touched. The mark
        // survives a refused root deletion, so a still-standing
        // harness-created root stays another live harness's to remove.
        private void DeleteOwnedFolders()
        {
            if (AssetDatabase.IsValidFolder(_folder))
            {
                AssetDatabase.DeleteAsset(_folder);
            }
            if (s_harnessCreatedRootGuid is { } ownedGuid &&
                AssetDatabase.IsValidFolder(RootFolder) &&
                string.Equals(
                    AssetDatabase.AssetPathToGUID(RootFolder), ownedGuid,
                    StringComparison.Ordinal) &&
                IsEmptyFolder(RootFolder))
            {
                AssetDatabase.DeleteAsset(RootFolder);
                s_harnessCreatedRootGuid =
                    AssetDatabase.IsValidFolder(RootFolder) ? ownedGuid : null;
            }
        }

        /// <summary>
        /// Registers a live GameObject the harness destroys on
        /// <see cref="Dispose"/>; called by <see cref="HarnessObjects"/> for
        /// every object it creates, and a no-op for one that is already
        /// registered or already destroyed.
        /// </summary>
        internal void Track(GameObject gameObject)
        {
            if (gameObject == null || _owned.Contains(gameObject))
            {
                return;
            }
            _owned.Add(gameObject);
        }

        // The §F6 provenance stamp follows the registration's §C3 origin,
        // which a Registry-free harness cannot read: it is reproduced from
        // the Operation's defining assembly instead. SPEC §I keeps every
        // standard Operation in the OpWeave.Operations assembly, so a type
        // defined there reports Standard and every other defining assembly
        // reports Custom — the same answer the Run's Registry snapshot gives
        // a standard Operation and the answer a fixture's registration gives
        // a project or test probe (the Registry's package-wide classification
        // would mark a probe inside com.opweave.workflow Standard, which no
        // fixture registration does).
        private static readonly HashSet<string> s_standardOperationAssemblies =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "OpWeave.Operations",
            };

        /// <summary>
        /// The §C3 <see cref="OperationOrigin"/> a harness run reports for the
        /// <paramref name="operationType"/>-defined Operation's §F6 provenance
        /// stamp: <see cref="OperationOrigin.Standard"/> when the type's
        /// defining assembly is one of the standard Operations' assemblies,
        /// <see cref="OperationOrigin.Custom"/> otherwise.
        /// </summary>
        internal static OperationOrigin OriginOf(Type operationType) =>
            operationType is not null &&
            s_standardOperationAssemblies.Contains(
                operationType.Assembly.GetName().Name ?? string.Empty)
                ? OperationOrigin.Standard
                : OperationOrigin.Custom;

        private HarnessResult Execute(
            string operationId, object inputs, int version, InterruptPoint? interrupt,
            RunContinuation continuation)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(OperationTestHarness));
            }
            if (interrupt is { } point &&
                point != InterruptPoint.BeforeExecute && point != InterruptPoint.AfterExecute)
            {
                throw new InvalidEnumArgumentException(
                    nameof(point), (int)point, typeof(InterruptPoint));
            }

            var journal = new HarnessJournal("opweave-harness-" + _id);
            var diagnostics = new List<Diagnostic>();
            var noOutputs = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            var noChanges = Array.Empty<ChangeRecord>();

            var resolved = HarnessOperationSource.Resolve(operationId, version);
            if (!resolved.Ok || resolved.Value is null)
            {
                // SPEC §E0: a step that never resolved its Operation is Failed
                // before its write-ahead record — no step_started, no side
                // effect, nothing for Reconcile to wonder about. The run loop
                // still journals step_failed (its FailStep always does), so the
                // harness journal shows the same Failed a Runner journal would.
                var unresolved = resolved.Diagnostics.Count == 0
                    ? new[]
                    {
                        ErrorCodes.Create(
                            ErrorCodes.OW_RUN_011,
                            "The step's Operation cannot be resolved.",
                            nodeId: NodeId),
                    }
                    : resolved.Diagnostics;
                diagnostics.AddRange(unresolved);
                Append(journal, JournalRecordType.StepFailed, unresolved[0]);
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }

            var operation = resolved.Value;
            OperationDescriptor descriptor;
            try
            {
                descriptor = operation.Descriptor;
            }
            catch (Exception exception)
            {
                var threw = ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    "The Operation threw an exception.",
                    nodeId: NodeId,
                    hint: exception.GetType().FullName);
                diagnostics.Add(threw);
                Append(journal, JournalRecordType.StepFailed, threw);
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }

            var built = HarnessInputs.Build(descriptor, inputs, NodeId);
            if (!built.Ok || built.Value is null)
            {
                // The harness's input build answers the same questions SPEC
                // §E4 step 1's input resolution does, so its failure takes the
                // same shape: a step_failed record, never a write-ahead one.
                diagnostics.AddRange(built.Diagnostics);
                var failed = built.Diagnostics.Count == 0
                    ? ErrorCodes.Create(
                        ErrorCodes.OW_RUN_007,
                        "The step's inputs could not be resolved.",
                        nodeId: NodeId)
                    : built.Diagnostics[0];
                if (built.Diagnostics.Count == 0)
                {
                    diagnostics.Add(failed);
                }
                Append(journal, JournalRecordType.StepFailed, failed);
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }

            var changes = new StepChangeRecorder(StepIndex, NodeId);
            var log = new StepLogSink(
                "harness_" + _id, StepIndex, journal.AppendFromLogSink);
            // The StepContext static type is deliberate: Outputs is the
            // Core-internal collection seam SPEC §0.6 SG-06 leaves off the
            // public surface, and reading it through the base member is what
            // the InternalsVisibleTo grant on OpWeave.Core alone covers — the
            // harness widens no production surface of the UnityAdapter.
            StepContext context = new UnityStepContext(
                built.Value,
                HarnessInputs.InputTypes(descriptor),
                HarnessInputs.OutputTypes(descriptor),
                HarnessTypes.Registry,
                new RefResolver(PrefabContentsResources.FindEditingRoot),
                changes,
                log,
                operationId: descriptor.Id,
                operationOrigin: OriginOf(operation.GetType()));
            // SPEC §E6 item 4: the step's Run-resource registry — the same
            // object a real Run's step sees, bound to the synthetic Run the
            // harness's records describe and appending its required
            // resource_acquired/resource_released records into this run's
            // journal. Without it a §G6 contents Operation's registry call
            // throws the no-registry NotSupportedException and the step can
            // never run the way the Runner runs it.
            ((UnityStepContext)context).Resources = new StepResourceRegistry(
                RunScopeRunId, StepIndex, _runResources,
                (type, step, data) => journal.AppendFromLogSink(
                    RunScopeRunId, type, step, data));

            // SPEC §E4 step 2: the runtime Requirements are judged before the
            // write-ahead record, evaluated by the same live evaluator the
            // Runner host wires, against the allowed paths the project's
            // settings currently declare (the harness's stand-in for the
            // snapshot a Run's acceptance freezes). A Violated requirement is
            // a step_failed before step_started, exactly as in a Run; a Wait —
            // only not_compiling produces one — ends the step's work with no
            // record and no diagnostic, so a synchronous harness reports the
            // step as never started (Pending) and the caller may ask again.
            var requirement = RequirementEvaluator().EvaluateAll(
                descriptor.Requirements,
                port => built.Value.TryGetValue(port, out var input) ? input.Value : null,
                NodeId,
                descriptor.Id);
            if (requirement.Verdict == RequirementVerdict.Wait)
            {
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }
            if (requirement.Verdict == RequirementVerdict.Violated)
            {
                var violated = requirement.Diagnostic!;
                diagnostics.Add(violated);
                Append(journal, JournalRecordType.StepFailed, violated);
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }

            PreflightResult? preflight;
            try
            {
                preflight = operation.Preflight(context);
            }
            catch (StepContextException contextError)
            {
                // A context conversion failure carries its own diagnostic
                // (SPEC §0.6 SG-06), never OW-RUN-006 — the same answer the
                // run loop gives for a Preflight that throws one.
                diagnostics.AddRange(contextError.Diagnostics);
                Append(journal, JournalRecordType.StepFailed, contextError.Diagnostics[0]);
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }
            catch (Exception exception)
            {
                // SPEC §E1 admits no exception out of Preflight either: it is
                // the same OW-RUN-006 the run loop reports for a throwing
                // Execute, and the step never reached its write-ahead record.
                var threw = ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    "The Operation threw an exception.",
                    nodeId: NodeId,
                    hint: exception.GetType().FullName);
                diagnostics.Add(threw);
                Append(journal, JournalRecordType.StepFailed, threw);
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }
            if (preflight is null)
            {
                var missing = ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    "The Operation returned no Preflight result.",
                    nodeId: NodeId);
                diagnostics.Add(missing);
                Append(journal, JournalRecordType.StepFailed, missing);
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }

            var refused = ErrorsOf(preflight.Diagnostics);
            if (refused.Count != 0)
            {
                // SPEC §E4 step 3: a refused Preflight never produced a
                // write-ahead record — the step is Failed and the world did not
                // change.
                diagnostics.AddRange(refused);
                Append(journal, JournalRecordType.StepFailed, refused[0]);
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }

            Append(journal, JournalRecordType.StepStarted, new JObject
            {
                ["snapshot"] = preflight.Snapshot?.ToToken() ?? new JObject(),
                ["inputsDigest"] = CanonicalJson.Sha256(PresentInputs(built.Value)),
            });

            if (interrupt == InterruptPoint.BeforeExecute)
            {
                return Finish(operationId, version, interrupt, null,
                    noOutputs, noChanges, diagnostics, journal);
            }

            EnsureScene();
            var outputs = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
            var outcome = StepResult.Succeeded();
            Exception? thrown = null;
            IDisposable undo =
                new HarnessUndoScope($"OpWeave: {descriptor.Id}/{NodeId}");
            try
            {
                try
                {
                    outcome = operation.Execute(context);
                }
                catch (StepContextException contextError)
                {
                    thrown = contextError;
                }
                catch (Exception exception)
                {
                    thrown = exception;
                }
                foreach (var pair in context.Outputs)
                {
                    outputs[pair.Key] = pair.Value;
                }
            }
            finally
            {
                // The group is closed on every outcome, an interrupted run
                // included: what an Editor crash leaves behind is the journal
                // state — step_started with no outcome record — not an open
                // Undo group, which a dead Editor does not carry at all.
                // Leaving it open would instead leak the group into the live
                // Editor's Undo stack and swallow unrelated records.
                undo.Dispose();
            }

            var records = changes.Records;
            if (interrupt == InterruptPoint.AfterExecute)
            {
                // The second crash window: the Execute call completed — cleanly
                // or by throwing — and the world already changed, but the
                // outcome record was never produced, so the journal still ends
                // at step_started and the fold reads Running. Whatever the
                // call would have reported past this point — an outcome record,
                // a failure diagnostic, the output check — is what the crash
                // took with it: Outcome reports the StepResult Execute
                // actually returned, or null when it threw instead.
                return Finish(operationId, version, interrupt,
                    thrown is null ? (StepResultKind?)outcome.Kind : null,
                    outputs, records, diagnostics, journal);
            }
            if (thrown is StepContextException contextFailure)
            {
                // A port conversion failure carries its own diagnostic —
                // OW-RUN-007 or OW-UNITY-001 — never OW-RUN-006 (SPEC §E5).
                diagnostics.AddRange(contextFailure.Diagnostics);
                Append(journal, JournalRecordType.StepFailed,
                    contextFailure.Diagnostics[0], records);
                return Finish(operationId, version, interrupt, StepResultKind.Failed,
                    outputs, records, diagnostics, journal);
            }
            if (thrown is not null)
            {
                // SPEC §E4 step 6: an Operation exception is OW-RUN-006.
                var threw = ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    "The Operation threw an exception.",
                    nodeId: NodeId,
                    hint: thrown.GetType().FullName);
                diagnostics.Add(threw);
                Append(journal, JournalRecordType.StepFailed, threw, records);
                return Finish(operationId, version, interrupt, StepResultKind.Failed,
                    outputs, records, diagnostics, journal);
            }
            if (outcome.Kind == StepResultKind.Failed)
            {
                var error = outcome.Error ?? ErrorCodes.Create(
                    ErrorCodes.OW_RUN_006,
                    "The Operation returned an uninterpretable result.",
                    nodeId: NodeId);
                diagnostics.Add(error);
                Append(journal, JournalRecordType.StepFailed, error, records);
                return Finish(operationId, version, interrupt, outcome.Kind,
                    outputs, records, diagnostics, journal);
            }
            if (outcome.Kind == StepResultKind.Waiting)
            {
                // The harness reports the Waiting it was given and its journal
                // shows it, exactly as a Run's journal shows it before a
                // resume. There is no deadline — no deadline owner — so the
                // required member is the explicit JSON null.
                var waitingData = new JObject
                {
                    ["resumeToken"] = outcome.ResumeToken?.ToToken() ?? new JObject(),
                    ["waitingFor"] = CredentialMask.MaskText(outcome.WaitingFor ?? string.Empty),
                    ["deadline"] = JValue.CreateNull(),
                };
                if (records.Count != 0)
                {
                    waitingData["changes"] = ChangeRecordJson.Write(records);
                }
                Append(journal, JournalRecordType.StepWaiting, waitingData);
                if (continuation == RunContinuation.Poll)
                {
                    return PollWaitedStep(operationId, version, interrupt,
                        operation, descriptor, built.Value, changes, log,
                        journal, diagnostics, outputs, outcome.ResumeToken);
                }
                return Finish(operationId, version, interrupt, outcome.Kind,
                    outputs, records, diagnostics, journal);
            }

            // SPEC §E5: a Succeeded step must have written its declared outputs.
            // The Plan-bound half of that check — the declared Type ID against
            // the Registry — has no Plan here; SPEC §M.6 item 2 is what tests it.
            var unwritten = UnwrittenOutputs(descriptor, outputs);
            if (unwritten.Count != 0)
            {
                diagnostics.AddRange(unwritten);
                Append(journal, JournalRecordType.StepFailed, unwritten[0], records);
                return Finish(operationId, version, interrupt, StepResultKind.Failed,
                    outputs, records, diagnostics, journal);
            }

            Append(journal, JournalRecordType.StepSucceeded, new JObject
            {
                ["outputs"] = OutputsToken(outputs),
                ["changes"] = ChangeRecordJson.Write(records),
                ["port"] = NextPort,
                ["reconciled"] = false,
            });
            return Finish(operationId, version, interrupt, outcome.Kind,
                outputs, records, diagnostics, journal);
        }

        // The pump RunToCompletion arms: each round builds a fresh context
        // carrying the persisted resume token — the same fresh-context
        // boundary the Run Loop puts between Poll calls — over the step's own
        // inputs, change recorder and log sink, calls the Operation's Poll,
        // and journals the round's outcome the way the step's own outcome is
        // journaled. A Pending round hands back its updated token and the
        // wait continues; a round still pending at the bound leaves the step
        // Waiting, exactly the result a plain Run reports.
        private HarnessResult PollWaitedStep(
            string operationId,
            int version,
            InterruptPoint? interrupt,
            IOperation operation,
            OperationDescriptor descriptor,
            IReadOnlyDictionary<string, InputValue> inputs,
            StepChangeRecorder changes,
            StepLogSink log,
            HarnessJournal journal,
            List<Diagnostic> diagnostics,
            Dictionary<string, JsonValue> outputs,
            JsonValue? resumeToken)
        {
            var token = resumeToken;
            for (var round = 0; round < PollRoundBound; round++)
            {
                StepContext pollContext = new UnityStepContext(
                    inputs,
                    HarnessInputs.InputTypes(descriptor),
                    HarnessInputs.OutputTypes(descriptor),
                    HarnessTypes.Registry,
                    new RefResolver(PrefabContentsResources.FindEditingRoot),
                    changes,
                    log,
                    resumeToken: token,
                    operationId: descriptor.Id,
                    operationOrigin: OriginOf(operation.GetType()));
                ((UnityStepContext)pollContext).Resources = new StepResourceRegistry(
                    RunScopeRunId, StepIndex, _runResources,
                    (type, step, data) => journal.AppendFromLogSink(
                        RunScopeRunId, type, step, data));

                StepResult polled;
                try
                {
                    polled = operation.Poll(pollContext);
                }
                catch (StepContextException contextError)
                {
                    diagnostics.AddRange(contextError.Diagnostics);
                    Append(journal, JournalRecordType.StepFailed,
                        contextError.Diagnostics[0], changes.Records);
                    return Finish(operationId, version, interrupt,
                        StepResultKind.Failed, outputs, changes.Records,
                        diagnostics, journal);
                }
                catch (Exception exception)
                {
                    var threw = ErrorCodes.Create(
                        ErrorCodes.OW_RUN_006,
                        "The Operation threw an exception.",
                        nodeId: NodeId,
                        hint: exception.GetType().FullName);
                    diagnostics.Add(threw);
                    Append(journal, JournalRecordType.StepFailed,
                        threw, changes.Records);
                    return Finish(operationId, version, interrupt,
                        StepResultKind.Failed, outputs, changes.Records,
                        diagnostics, journal);
                }
                foreach (var pair in pollContext.Outputs)
                {
                    outputs[pair.Key] = pair.Value;
                }
                if (polled.Kind == StepResultKind.Waiting)
                {
                    var waitingData = new JObject
                    {
                        ["resumeToken"] =
                            polled.ResumeToken?.ToToken() ?? new JObject(),
                        ["waitingFor"] = CredentialMask.MaskText(
                            polled.WaitingFor ?? string.Empty),
                        ["deadline"] = JValue.CreateNull(),
                    };
                    if (changes.Records.Count != 0)
                    {
                        waitingData["changes"] =
                            ChangeRecordJson.Write(changes.Records);
                    }
                    Append(journal, JournalRecordType.StepWaiting, waitingData);
                    token = polled.ResumeToken;
                    continue;
                }
                if (polled.Kind == StepResultKind.Failed)
                {
                    var error = polled.Error ?? ErrorCodes.Create(
                        ErrorCodes.OW_RUN_006,
                        "The Operation returned an uninterpretable result.",
                        nodeId: NodeId);
                    diagnostics.Add(error);
                    Append(journal, JournalRecordType.StepFailed,
                        error, changes.Records);
                    return Finish(operationId, version, interrupt, polled.Kind,
                        outputs, changes.Records, diagnostics, journal);
                }

                // Succeeded: the §E5 declared-outputs check and the
                // step_succeeded record close the step exactly as they do
                // for a synchronous Execute.
                var unwritten = UnwrittenOutputs(descriptor, outputs);
                if (unwritten.Count != 0)
                {
                    diagnostics.AddRange(unwritten);
                    Append(journal, JournalRecordType.StepFailed,
                        unwritten[0], changes.Records);
                    return Finish(operationId, version, interrupt,
                        StepResultKind.Failed, outputs, changes.Records,
                        diagnostics, journal);
                }
                Append(journal, JournalRecordType.StepSucceeded, new JObject
                {
                    ["outputs"] = OutputsToken(outputs),
                    ["changes"] = ChangeRecordJson.Write(changes.Records),
                    ["port"] = NextPort,
                    ["reconciled"] = false,
                });
                return Finish(operationId, version, interrupt, polled.Kind,
                    outputs, changes.Records, diagnostics, journal);
            }
            return Finish(operationId, version, interrupt, StepResultKind.Waiting,
                outputs, changes.Records, diagnostics, journal);
        }

        private HarnessResult Finish(
            string operationId,
            int version,
            InterruptPoint? interrupt,
            StepResultKind? outcome,
            Dictionary<string, JsonValue> outputs,
            IReadOnlyList<ChangeRecord> changes,
            List<Diagnostic> diagnostics,
            HarnessJournal journal) =>
            new HarnessResult(
                operationId,
                version,
                HarnessJournal.JournaledStateOf(journal.Records),
                outcome,
                interrupt,
                ReadOnlyCollections.CopyMap(outputs),
                ReadOnlyCollections.CopyList(changes),
                ReadOnlyCollections.CopyList(diagnostics),
                ReadOnlyCollections.CopyList(journal.Records),
                // The list is copied so a later acquisition cannot mutate a
                // reported result; the entries stay live — §F5's
                // resource-backed verdicts judge the physical state they
                // track.
                ReadOnlyCollections.CopyList(_runResources));

        private static List<Diagnostic> UnwrittenOutputs(
            OperationDescriptor descriptor, Dictionary<string, JsonValue> outputs)
        {
            var errors = new List<Diagnostic>();
            foreach (var port in descriptor.Outputs)
            {
                if (!outputs.ContainsKey(port.Name))
                {
                    errors.Add(ErrorCodes.Create(
                        ErrorCodes.OW_RUN_007,
                        $"The Operation did not write output port '{port.Name}'.",
                        nodeId: NodeId,
                        port: port.Name,
                        hint: "Write every declared output port in Execute."));
                }
            }
            return errors;
        }

        private static void Append(
            HarnessJournal journal, JournalRecordType type, JObject data) =>
            journal.AppendStep(type, StepIndex, Attempt, data);

        // SPEC §F2's step_failed payload: the first error's code and its
        // credential-masked message — and the attempt's cumulative §F7
        // `changes` list whenever one exists (2026-11-08 attempt-durability
        // decision).
        private static void Append(
            HarnessJournal journal, JournalRecordType type, Diagnostic error,
            IReadOnlyList<ChangeRecord>? changes = null)
        {
            var data = new JObject
            {
                ["error"] = error.Code,
                ["message"] = CredentialMask.MaskText(error.Message),
            };
            if (changes is { Count: > 0 })
            {
                data["changes"] = ChangeRecordJson.Write(changes);
            }
            journal.AppendStep(type, StepIndex, Attempt, data);
        }

        private static List<Diagnostic> ErrorsOf(IReadOnlyList<Diagnostic> diagnostics)
        {
            var errors = new List<Diagnostic>();
            for (var i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i].Severity == Severity.Error)
                {
                    errors.Add(diagnostics[i]);
                }
            }
            return errors;
        }

        // The inputsDigest input: only present inputs take part — a Missing port
        // is never serialized (SPEC §A0.1).
        private static JObject PresentInputs(
            IReadOnlyDictionary<string, InputValue> inputs)
        {
            var present = new JObject();
            foreach (var pair in inputs)
            {
                if (pair.Value.IsPresent)
                {
                    present[pair.Key] = pair.Value.Value!.ToToken();
                }
            }
            return present;
        }

        private static JObject OutputsToken(
            IReadOnlyDictionary<string, JsonValue> outputs)
        {
            var token = new JObject();
            foreach (var pair in outputs)
            {
                token[pair.Key] = pair.Value.ToToken();
            }
            return token;
        }

        // ── temporary scene ───────────────────────────────────────────────

        // The step needs a scene the Operation may add objects to and that the
        // harness can throw away. It is saved inside the harness folder the
        // moment it opens: an unsaved scene would be the next Additive open's
        // problem, and it would be the caller's to answer for.
        private void EnsureScene()
        {
            if (FindScene(_scenePath) is { } already)
            {
                return;
            }
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isDirty)
                {
                    throw new InvalidOperationException(
                        "The harness cannot open its temporary scene while a scene " +
                        "has unsaved changes, because saving or discarding them " +
                        "belongs to the caller. Save or revert the open scene first.");
                }
            }
            // Captured before an untitled-scene takeover, so the path never
            // names the harness's own base scene: an untitled active scene
            // records "", and the saved setup is what Dispose restores.
            _activeScenePath = EditorSceneManager.GetActiveScene().path;
            if (HasUntitledScene())
            {
                TakeOverUntitledScene();
            }

            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            if (!EditorSceneManager.SaveScene(scene, _scenePath))
            {
                throw new InvalidOperationException(
                    $"Could not save the harness scene at '{_scenePath}'.");
            }
            var saved = EditorSceneManager.GetSceneByPath(_scenePath);
            EditorSceneManager.SetActiveScene(saved.isLoaded ? saved : scene);
        }

        private bool HasUntitledScene()
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (string.IsNullOrEmpty(EditorSceneManager.GetSceneAt(i).path))
                {
                    return true;
                }
            }
            return false;
        }

        // An untitled scene blocks NewScene(Additive) and names no asset, so the
        // harness replaces it with a saved scene of its own inside the harness
        // folder — nothing of the caller's is left in it, and Dispose puts the
        // scene setup back the way it found it. Only a clean untitled scene
        // reaches here: the check above already refused a dirty one, because
        // unsaved changes belong to the caller even when no file backs them.
        private void TakeOverUntitledScene()
        {
            _savedSetup = EditorSceneManager.GetSceneManagerSetup();
            var single = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (!EditorSceneManager.SaveScene(single, _baseScenePath))
            {
                throw new InvalidOperationException(
                    $"Could not save the harness base scene at '{_baseScenePath}'.");
            }
            _replacedSetup = true;
        }

        // The Editor must never be left with no loaded scene — several scene
        // APIs refuse that state outright — so every close happens while a
        // replacement is still loaded, and the last step guarantees one. A
        // scene problem here is reported, not raised: Dispose still owes the
        // folder cleanup the caller runs it for.
        private void RestoreSceneState()
        {
            try
            {
                var scene = FindScene(_scenePath);
                if (scene is { } loaded)
                {
                    ActivateReplacementFor(loaded);
                    EditorSceneManager.CloseScene(loaded, true);
                }

                // An untitled-scene takeover saved the caller's setup; put
                // the scenes back the way the harness found them — which
                // also closes the base scene the takeover left loaded.
                if (_replacedSetup)
                {
                    RestoreSavedSetup();
                }

                // The base scene of an untitled-scene takeover is also the
                // harness's own asset; it can only still be open when the
                // restore above did not already close it — and its asset is
                // deleted with the folder below, so it must not stay loaded.
                var baseScene = FindScene(_baseScenePath);
                if (baseScene is { } leftover && leftover.isLoaded)
                {
                    ActivateReplacementFor(leftover);
                    EditorSceneManager.CloseScene(leftover, true);
                }

                EnsureOneLoadedScene();
            }
            catch (Exception exception)
            {
                // Dispose must not poison the rest of a test session, so a
                // scene problem is reported instead of raised — and one fresh
                // scene both ends the required loaded state and closes
                // whatever the ordered close above left loaded.
                Debug.LogWarning(
                    "The OpWeave test harness could not fully restore the " +
                    "scene state; a fresh empty scene replaces what was " +
                    "left. " + exception.Message);
                TryNewEmptySingleScene();
            }
        }

        // A setup entry is only restorable when it names a scene asset: an
        // untitled scene (empty path) cannot be reopened, so a pathless
        // entry is dropped — and when nothing restorable remains, the blank
        // state the untitled scenes stood for is one fresh empty scene.
        // RestoreSceneManagerSetup also refuses a setup with no active
        // scene, which dropping the untitled entries can produce.
        private void RestoreSavedSetup()
        {
            var restorable = new List<SceneSetup>();
            if (_savedSetup is { } setup)
            {
                for (var i = 0; i < setup.Length; i++)
                {
                    if (!string.IsNullOrEmpty(setup[i].path))
                    {
                        restorable.Add(setup[i]);
                    }
                }
            }
            if (restorable.Count == 0)
            {
                NewEmptySingleScene();
                return;
            }
            var hasActive = false;
            for (var i = 0; i < restorable.Count; i++)
            {
                var entry = restorable[i];
                entry.isActive = !hasActive && entry.isLoaded;
                hasActive |= entry.isActive;
                restorable[i] = entry;
            }
            if (!hasActive)
            {
                var entry = restorable[0];
                entry.isLoaded = true;
                entry.isActive = true;
                restorable[0] = entry;
            }
            EditorSceneManager.RestoreSceneManagerSetup(restorable.ToArray());
        }

        // Closing needs another loaded scene to leave active: the harness's
        // base scene while a takeover is open, then the scene that was
        // active when the harness's own opened, then any other loaded
        // scene — the caller's own ordering comes back with the setup
        // restore either way.
        private void ActivateReplacementFor(Scene closing)
        {
            var candidates = new List<Scene>();
            if (FindScene(_baseScenePath) is { } baseScene)
            {
                candidates.Add(baseScene);
            }
            if (!string.IsNullOrEmpty(_activeScenePath))
            {
                var previous = EditorSceneManager.GetSceneByPath(_activeScenePath!);
                if (previous.IsValid())
                {
                    candidates.Add(previous);
                }
            }
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                candidates.Add(EditorSceneManager.GetSceneAt(i));
            }
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate.isLoaded && candidate.handle != closing.handle)
                {
                    EditorSceneManager.SetActiveScene(candidate);
                    return;
                }
            }
        }

        // Dispose's end state is exactly one loaded scene: the caller's
        // setup restored, or one fresh empty scene — never zero, which
        // RestoreSceneManagerSetup and the next scene open both refuse to
        // start from.
        private static void EnsureOneLoadedScene()
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                if (EditorSceneManager.GetSceneAt(i).isLoaded)
                {
                    return;
                }
            }
            NewEmptySingleScene();
        }

        private static void NewEmptySingleScene() =>
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

        // Best effort for the broken-Editor edge: a failed NewScene is
        // reported, never raised — Dispose has nothing left it could do.
        private static void TryNewEmptySingleScene()
        {
            try
            {
                NewEmptySingleScene();
            }
            catch (Exception exception)
            {
                Debug.LogWarning(
                    "The OpWeave test harness could not open a fresh empty " +
                    "scene. " + exception.Message);
            }
        }

        private static void ForceCloseIfLoaded(string path)
        {
            try
            {
                if (FindScene(path) is { } scene && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
            catch (Exception)
            {
                // Best-effort only: the folder deletion proceeds either way.
            }
        }

        private static Scene? FindScene(string path)
        {
            for (var i = 0; i < EditorSceneManager.sceneCount; i++)
            {
                var scene = EditorSceneManager.GetSceneAt(i);
                if (scene.isLoaded &&
                    string.Equals(
                        scene.path.Replace('\\', '/'),
                        path,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return scene;
                }
            }
            return null;
        }

        // The live §G1 evaluator a Run would judge the step's Requirements
        // with — the same public evaluator the Runner host wires behind its
        // adapter — over the allowed paths the project's settings currently
        // declare: the harness's stand-in for the settings snapshot a Run's
        // acceptance validates (SPEC §H4.1). A settings file that fails to
        // load yields an empty allowed set, so a write port's path_writable
        // requirement fails closed (decision D-R6) instead of being skipped.
        private static EditorRequirementEvaluator RequirementEvaluator()
        {
            IReadOnlyList<string> allowedPaths;
            try
            {
                var loaded = OpWeaveSettingsLoader.Load();
                allowedPaths = loaded.Ok && loaded.Value is { } settings
                    ? settings.AllowedPaths
                    : (IReadOnlyList<string>)Array.Empty<string>();
            }
            catch (Exception)
            {
                allowedPaths = Array.Empty<string>();
            }
            return new EditorRequirementEvaluator(allowedPaths);
        }

        private void EnsureRootFolder()
        {
            if (AssetDatabase.IsValidFolder(RootFolder))
            {
                return;
            }
            if (!AssetDatabase.IsValidFolder("Assets"))
            {
                throw new InvalidOperationException(
                    "The harness needs an Assets folder in the open project.");
            }
            var guid = AssetDatabase.CreateFolder("Assets", "OpWeaveTesting");
            if (string.IsNullOrEmpty(guid))
            {
                throw new InvalidOperationException(
                    $"Could not create the harness root folder '{RootFolder}'.");
            }
            s_harnessCreatedRootGuid = guid;
        }

        // Only a folder the harness created itself, and only while nothing but
        // meta files is left in it, is removed — a folder someone else reused or
        // filled is never touched.
        private static bool IsEmptyFolder(string folder)
        {
            var entries = Directory.GetFileSystemEntries(
                folder, "*", SearchOption.AllDirectories);
            for (var i = 0; i < entries.Length; i++)
            {
                if (!entries[i].EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }
    }
}
