#nullable enable

// T-W4.5-04 [Edit] tests: local:T-W4.5-04:at02-compile-package — the SPEC
// §T.3 AT-02 contract applied to every standard Operation of the SPEC
// §I.4 Compile/Package catalog group through the OpWeave.Testing §M.6
// item 2 assertions:
//
//   * AssertDescriptorValid — the §C3 registration checks pass with no
//     OW-OP-003 violation (SPEC §C1 test 1);
//   * AssertIdempotency — per the declaration: an Idempotent Operation
//     (unity.script.compile) runs twice on the same inputs with an
//     identical world and equal outputs; a Reconcilable Operation
//     (unity.script.create_from_template, unity.script.create,
//     unity.package.add, unity.package.remove) is judged through
//     RunInterrupted at both §F5 crash windows, Unknown is accepted as
//     the safe verdict, an evidence-less Completed fails, and the
//     damaged-evidence probe admits Unknown only;
//   * AssertUndo — no §I.4 Operation declares Undo=Full, so the check is
//     exercised and skips per its own semantics;
//   * AssertDeclaredEffectsOnly — no recorded or observed change outside
//     the declared §C4 vocabulary.
//
// The group's three Waitables are what this suite's seams exist for.
// unity.script.compile settles through RunToCompletion's Poll pump on a
// scripted wait condition, the compile request itself a no-op — its
// contract is the idempotent declaration, not a real compile. The two
// Reconcilable Waitables, unity.package.add and unity.package.remove,
// are the first Operations whose Execute answers Waiting inside a
// contract run: RunInterrupted(AfterExecute) therefore reports the §G4
// suspension, and the §F5 judgement asks the persisted snapshot and the
// package-state view what the issued effect proved — the exact same
// question a real crash journal poses. The package Operations' UPM
// reads go through the PackageInstallView seam their constructors
// expose, so the §F5 rows — pre-state, executed state and damaged
// evidence — are deterministic: never a real manifest or Client call.
// The suspended-step verdict itself is then judged by the SPEC §M.6
// item 4 evidence table through the public three-argument
// AssertIdempotency: the probe reads the same scripted package world on
// its own — including whether the request the step issued is still
// outstanding, which keeps an absent effect Indeterminate.
//
// The Runner half of §F5 — a Run ending at a bare step_started whose
// evidence no longer holds must end NeedsReview, never an automatic
// success or re-execution — lives in At02CompilePackageRecoveryTests.
// This file closes with the deliberately weakened Reconcile fixtures the
// ticket requires the contract to catch: a Completed claimed on an
// artifact's mere existence, and a suspended-step NotApplied claimed for
// an installed package.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Operations;
using OpWeave.Testing;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// <c>local:T-W4.5-04:at02-compile-package</c>: the AT-02 contract
    /// suite for the SPEC §I.4 Compile/Package group.
    /// </summary>
    internal sealed class At02CompilePackageContractTests
    {
        // The SPEC §I.4 catalog group, verbatim and in table order — the
        // complete exercised list the AT-02 pass condition requires with
        // none skipped.
        private static readonly string[] s_specGroup =
        {
            CreateScriptFromTemplateOperation.OperationId,
            CreateScriptOperation.OperationId,
            CompileScriptsOperation.OperationId,
            UnityPackageAddOperation.OperationId,
            UnityPackageRemoveOperation.OperationId,
        };

        // The script probes live under the default codePaths entry, so
        // the standard implementations' own path policy accepts them —
        // no settings stand-in is needed for this group.
        private const string CodeFolder = "Assets/Scripts";
        private const string ProbeFolder = CodeFolder + "/OpWeaveAt02Compile";
        private const string BaseScenePath = ProbeFolder + "/Base.unity";

        // A registry id inside the default allowedPackageSources entry
        // "registry:com.unity.*" — the source policy passes it and every
        // UPM read below is scripted, so the name never has to resolve.
        private const string PackageName = "com.unity.at02probe";
        private const string PackageVersion = "1.2.3";
        private const string PackageId = PackageName + "@" + PackageVersion;

        private const string WeakenedId = "test.at02.weakened_script_create";
        private const string WeakenedPackageAddId = "test.at02.weakened_package_add";

        private readonly List<OperationTestHarness> _harnesses =
            new List<OperationTestHarness>();
        private readonly string _tag =
            Guid.NewGuid().ToString("N").Substring(0, 6);
        private OperationTestHarness? _fixture;
        private int _serial;

        [SetUp]
        public void SetUp()
        {
            EnsureFolder(ProbeFolder);
            // A saved, clean base scene: an untitled caller scene would be
            // taken over by the first harness that opens — and additive
            // fixture scenes cannot be created while one is open.
            var scene = EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Assert.That(
                EditorSceneManager.SaveScene(scene, BaseScenePath), Is.True,
                "the test's base scene must save.");
            HarnessOperationSource.ClearOverrides();
        }

        [TearDown]
        public void TearDown()
        {
            HarnessOperationSource.ClearOverrides();
            _fixture = null;
            foreach (var harness in _harnesses)
            {
                harness.Dispose();
            }
            _harnesses.Clear();
            // One fresh untitled scene — every loaded fixture, opened or
            // created scene is discarded before the probe folders leave.
            EditorSceneManager.NewScene(
                NewSceneSetup.EmptyScene, NewSceneMode.Single);
            if (AssetDatabase.IsValidFolder(ProbeFolder))
            {
                AssetDatabase.DeleteAsset(ProbeFolder);
            }
            var probeDisk = PhysicalPath(ProbeFolder);
            if (Directory.Exists(probeDisk))
            {
                try { Directory.Delete(probeDisk, recursive: true); }
                catch (Exception) { /* a still-held file stays for the next run */ }
            }
            if (File.Exists(probeDisk + ".meta"))
            {
                try { File.Delete(probeDisk + ".meta"); } catch (Exception) { }
            }
            // Only the group's own subfolder: Assets/Scripts itself is a
            // normal project folder — never deleted by a test.
            if (AssetDatabase.IsValidFolder("Assets/OpWeaveTesting"))
            {
                AssetDatabase.DeleteAsset("Assets/OpWeaveTesting");
            }
            AssetDatabase.Refresh();
        }

        // ── The SPEC §I.4 group, exercised in catalog order ─────────────

        // The pass condition's coverage half: the exercised list is exactly
        // the SPEC §I.4 catalog group — every entry resolves to a real
        // standard Operation and carries a valid §C3 descriptor, so a
        // missing or skipped entry fails here before the per-Operation
        // assertions below are even reached.
        [Test]
        public void ExercisedList_IsTheSpecI4Group()
        {
            Assert.That(s_specGroup, Has.Length.EqualTo(5));
            Assert.That(s_specGroup.Distinct().Count(), Is.EqualTo(5));
            foreach (var id in s_specGroup)
            {
                Assert.DoesNotThrow(
                    () => OperationContract.AssertDescriptorValid(id), id);
            }
        }

        // unity.script.create_from_template — Reconcilable, Immediate:
        // the contract drives both §F5 crash windows; the recorded
        // expected-content hash is the evidence the damaged-artifact
        // probe breaks.
        [Test]
        public void ScriptCreateFromTemplate_Contract()
        {
            var id = CreateScriptFromTemplateOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, TemplateInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, TemplateInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, TemplateInputs()));
        }

        // unity.script.create — Reconcilable, Immediate: the recorded
        // expected content is the §F5 evidence row; existence alone can
        // never answer Completed.
        [Test]
        public void ScriptCreate_Contract()
        {
            var id = CreateScriptOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, ScriptInputs()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, ScriptInputs()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, ScriptInputs()));
        }

        // unity.script.compile — the group's Idempotent Waitable: the
        // contract pumps Poll on a scripted condition instead of a real
        // compile, and the same inputs run twice must answer the same
        // result with an identical world.
        [Test]
        public void ScriptCompile_Contract()
        {
            HarnessOperationSource.Override(
                CompileScriptsOperation.OperationId, 1,
                () => new CompileScriptsOperation(
                    static () => { }, new DoneCondition()));
            var id = CompileScriptsOperation.OperationId;
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(id));
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(id, new object()));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(id, new object()));
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                id, new object()));
        }

        // unity.package.add — Reconcilable AND Waitable: Execute returns
        // Waiting once Client.Add is issued, so the after-Execute
        // judgement reads the scripted package view, not a completed
        // step. The view sequence scripts the §F5 rows in the exact
        // order the contract asks them: pre-state absent, absent at the
        // before-Execute verdict, absent again through the second
        // Preflight and Execute recheck, the installed three-channel
        // agreement for the after-Execute verdict, and the manifest-only
        // partial the damaged-evidence probe admits Unknown only for.
        [Test]
        public void PackageAdd_Contract()
        {
            var world = new ScriptedPackageWorld();
            HarnessOperationSource.Override(
                UnityPackageAddOperation.OperationId, 1,
                () => new UnityPackageAddOperation(
                    static () => Result<OpWeaveSettings>.Success(
                        OpWeaveSettings.Default),
                    static id => PackageResolvedCondition.InitialToken(
                        id, expectPresent: true),
                    new DoneCondition(),
                    world.Next));
            var inputs = new { id = PackageId };
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(
                UnityPackageAddOperation.OperationId));
            // The §G8.1 evidence rows the verdict is judged against —
            // the suspended-step probe reads the same world on its own
            // after the Reconcile, so every verdict read is scripted
            // twice: once for the Reconcile under judgement, once for the
            // §M.6 item 4 evidence answer.
            var absent = AbsentView();
            var installed = InstalledView();
            world.Script(
                absent,         // (a) Preflight snapshot
                absent,         // (a) verdict — proven absence → NotApplied
                absent,         // (b) Preflight snapshot
                absent,         // (b) Execute's guard recheck
                installed,      // (b) verdict — three channels agree → Completed
                installed,      // (b) probe — landed and settled → Applied
                PartialAddView()); // (c) manifest-only partial → Unknown only
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(
                    UnityPackageAddOperation.OperationId, inputs,
                    () => AddEvidence(world)));
            // The same judgement against the proven-absent row: the
            // issued add never landed and no request remains that could
            // still land it — NotApplied evidence admits the honest
            // NotApplied verdict.
            world.Script(
                absent,   // (a) Preflight snapshot
                absent,   // (a) verdict — proven absence → NotApplied
                absent,   // (b) Preflight snapshot
                absent,   // (b) Execute's guard recheck
                absent,   // (b) verdict — issued add never landed → NotApplied
                absent);  // (b) probe — absent and settled → NotApplied
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(
                    UnityPackageAddOperation.OperationId, inputs,
                    () => AddEvidence(world)));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(
                    UnityPackageAddOperation.OperationId, inputs));
            world.Script(
                AbsentView(),   // declared-effects Preflight
                AbsentView(),   // declared-effects Execute recheck
                InstalledView());// Poll's resolved-package lookup
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                UnityPackageAddOperation.OperationId, inputs));
        }

        // unity.package.remove — Reconcilable AND Waitable on the same
        // shape: the recorded pre-state is the installed package, the
        // after-Execute verdict is the absent-everywhere Completed, and
        // the damaged row is the partial removal Unknown only admits.
        [Test]
        public void PackageRemove_Contract()
        {
            var world = new ScriptedPackageWorld();
            HarnessOperationSource.Override(
                UnityPackageRemoveOperation.OperationId, 1,
                () => new UnityPackageRemoveOperation(
                    static name => PackageResolvedCondition.InitialToken(
                        name, expectPresent: false),
                    new DoneCondition(),
                    world.Next));
            var inputs = new { name = PackageName };
            Assert.DoesNotThrow(() => OperationContract.AssertDescriptorValid(
                UnityPackageRemoveOperation.OperationId));
            // A remove's §F5 evidence rows mirror the add's — the probe
            // reads the same world on its own, so each suspended-step
            // verdict read is scripted twice.
            var installed = InstalledView();
            var absent = AbsentView();
            world.Script(
                installed,      // (a) Preflight records the installed identity
                installed,      // (a) verdict — state equals snapshot → NotApplied
                installed,      // (b) Preflight snapshot
                absent,         // (b) verdict — gone everywhere → Completed
                absent,         // (b) probe — removal landed → Applied
                PartialRemoveView()); // (c) registered still lists it → Unknown
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(
                    UnityPackageRemoveOperation.OperationId, inputs,
                    () => RemoveEvidence(world)));
            // The proven-absent half of a remove: the issued removal
            // never landed — the state still equals the installed
            // snapshot — and no request remains outstanding, so the
            // honest NotApplied verdict is admitted.
            world.Script(
                installed,   // (a) Preflight snapshot
                installed,   // (a) verdict — state equals snapshot → NotApplied
                installed,   // (b) Preflight snapshot
                installed,   // (b) verdict — issued remove never landed → NotApplied
                installed);  // (b) probe — still installed, settled → NotApplied
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(
                    UnityPackageRemoveOperation.OperationId, inputs,
                    () => RemoveEvidence(world)));
            Assert.DoesNotThrow(
                () => OperationContract.AssertUndo(
                    UnityPackageRemoveOperation.OperationId, inputs));
            world.Script(InstalledView()); // declared-effects Preflight
            Assert.DoesNotThrow(() => OperationContract.AssertDeclaredEffectsOnly(
                UnityPackageRemoveOperation.OperationId, inputs));
        }

        // ── The deliberately weakened Reconcile fixture ─────────────────

        // The ticket's boundary acceptance: a Reconcilable Operation whose
        // Reconcile is weakened to answer Completed on the artifact's mere
        // existence — exactly the evidence SPEC §F5 rules out — must fail
        // the contract. The dummy below writes a script file like the
        // §I.4 script Operations do: its (a)/(b) judgements pass, and the
        // damaged-evidence probe's overwritten file is what exposes the
        // lie.
        [Test]
        public void WeakenedReconcile_IsDetected()
        {
            // The damaged-evidence probe must be the failing assertion —
            // an exception from an earlier before/after-Execute check would
            // prove nothing about the lie this fixture exists to expose, so
            // the verdict's specific §F5 rejection text is required, not
            // just any AssertionException.
            var failure = Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    WeakenedId,
                    new { path = Fixture().TempFolder + "/At02Weakened.cs" }));
            Assert.IsNotNull(failure);
            StringAssert.Contains(WeakenedId, failure!.Message);
            StringAssert.Contains(
                "after its artifact was damaged", failure.Message);
            StringAssert.Contains("admit Unknown only", failure.Message);
        }

        // The suspended-step half of the boundary acceptance: a Waitable
        // Reconcilable shaped on unity.package.add — Execute suspends on
        // the issued effect exactly like the real Operation — whose
        // Reconcile answers NotApplied whatever the evidence shows. With
        // the SPEC §M.6 item 4 probe answering Applied — the issued
        // effect provably landed — the contract must reject the
        // NotApplied that would send recovery to issue the add a second
        // time (SPEC §F5 judgement b, §G8.1).
        [Test]
        public void WeakenedPackageAddReconcile_IsDetected()
        {
            // The failing assertion must be the after-Execute judgement
            // itself — an earlier window's failure would prove nothing
            // about the suspended-step NotApplied lie this fixture exists
            // to expose, so the verdict's specific §F5 rejection text is
            // required, not just any AssertionException.
            var failure = Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    WeakenedPackageAddId,
                    new { id = PackageId },
                    static () => EffectEvidence.Applied));
            Assert.IsNotNull(failure);
            StringAssert.Contains(WeakenedPackageAddId, failure!.Message);
            StringAssert.Contains("NotApplied", failure.Message);
            StringAssert.Contains("SPEC §F5", failure.Message);
        }

        // The same weakened Operation through the two-argument public
        // §M.6 surface: with no evidence probe to consult, a suspended
        // step's verdict is judged as Indeterminate — missing evidence is
        // never treated as proof of absence — so only Unknown is
        // admitted. The fixture's unconditional NotApplied must be
        // rejected there too, and the failure must point at the
        // evidence-aware three-argument overload.
        [Test]
        public void WeakenedPackageAddReconcile_PublicAssertRejectsNotApplied()
        {
            var failure = Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    WeakenedPackageAddId,
                    new { id = PackageId }));
            Assert.IsNotNull(failure);
            StringAssert.Contains(WeakenedPackageAddId, failure!.Message);
            StringAssert.Contains("NotApplied", failure.Message);
            StringAssert.Contains("SPEC §F5", failure.Message);
            StringAssert.Contains("evidenceProbe", failure.Message);
        }

        // ── SPEC §M.6 item 4: the Indeterminate row ────────────────────

        // The Indeterminate row's positive half: an effect state the
        // probe cannot decide — the manifest-only partial an interrupted
        // add leaves — admits Unknown only, and the honest Operation's
        // Unknown verdict passes through the public overload.
        [Test]
        public void InterruptedWaitable_UnknownPassesUnderIndeterminate()
        {
            var world = new ScriptedPackageWorld();
            HarnessOperationSource.Override(
                UnityPackageAddOperation.OperationId, 1,
                () => new UnityPackageAddOperation(
                    static () => Result<OpWeaveSettings>.Success(
                        OpWeaveSettings.Default),
                    static id => PackageResolvedCondition.InitialToken(
                        id, expectPresent: true),
                    new DoneCondition(),
                    world.Next));
            var absent = AbsentView();
            var partial = PartialAddView();
            world.Script(
                absent,     // (a) Preflight snapshot
                absent,     // (a) verdict — proven absence → NotApplied
                absent,     // (b) Preflight snapshot
                absent,     // (b) Execute's guard recheck
                partial,    // (b) verdict — manifest-only partial → Unknown
                partial);   // (b) probe — undecidable → Indeterminate
            Assert.DoesNotThrow(
                () => OperationContract.AssertIdempotency(
                    UnityPackageAddOperation.OperationId,
                    new { id = PackageId },
                    () => AddEvidence(world)));
        }

        // The Indeterminate row's Completed half: a request the step
        // issued is still outstanding, so even a fully installed world
        // cannot prove that request's effect landed — the probe answers
        // Indeterminate and the honest Completed must be rejected.
        [Test]
        public void InterruptedWaitable_CompletedFailsUnderIndeterminate()
        {
            var world = new ScriptedPackageWorld();
            HarnessOperationSource.Override(
                UnityPackageAddOperation.OperationId, 1,
                () => new UnityPackageAddOperation(
                    static () => Result<OpWeaveSettings>.Success(
                        OpWeaveSettings.Default),
                    world.IssueHoldingAdd,
                    new DoneCondition(),
                    world.Next));
            var absent = AbsentView();
            var installed = InstalledView();
            world.Script(
                absent,     // (a) Preflight snapshot
                absent,     // (a) verdict — proven absence → NotApplied
                absent,     // (b) Preflight snapshot
                absent,     // (b) Execute's guard recheck
                installed,  // (b) verdict — three channels agree → Completed
                installed); // (b) probe — request outstanding → Indeterminate
            var failure = Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    UnityPackageAddOperation.OperationId,
                    new { id = PackageId },
                    () => AddEvidence(world)));
            Assert.IsNotNull(failure);
            StringAssert.Contains(
                UnityPackageAddOperation.OperationId, failure!.Message);
            StringAssert.Contains("only Unknown", failure.Message);
        }

        // The Indeterminate row's NotApplied half — the ticket's
        // outstanding-request boundary: the effect is absent, but the
        // request the step issued is still outstanding and could still
        // land it, so the probe answers Indeterminate and the honest
        // NotApplied fails — SPEC §M.6 item 4's "issued request remains"
        // case.
        [Test]
        public void InterruptedWaitable_NotAppliedFailsWithOutstandingRequest()
        {
            var world = new ScriptedPackageWorld();
            HarnessOperationSource.Override(
                UnityPackageAddOperation.OperationId, 1,
                () => new UnityPackageAddOperation(
                    static () => Result<OpWeaveSettings>.Success(
                        OpWeaveSettings.Default),
                    world.IssueHoldingAdd,
                    new DoneCondition(),
                    world.Next));
            var absent = AbsentView();
            world.Script(
                absent,   // (a) Preflight snapshot
                absent,   // (a) verdict — proven absence → NotApplied
                absent,   // (b) Preflight snapshot
                absent,   // (b) Execute's guard recheck
                absent,   // (b) verdict — issued add never landed → NotApplied
                absent);  // (b) probe — request outstanding → Indeterminate
            var failure = Assert.Throws<AssertionException>(() =>
                OperationContract.AssertIdempotency(
                    UnityPackageAddOperation.OperationId,
                    new { id = PackageId },
                    () => AddEvidence(world)));
            Assert.IsNotNull(failure);
            StringAssert.Contains(
                UnityPackageAddOperation.OperationId, failure!.Message);
            StringAssert.Contains("NotApplied", failure.Message);
        }

        // ── Fixture inputs ──────────────────────────────────────────────

        private OperationTestHarness Fixture()
        {
            if (_fixture is null)
            {
                _fixture = OperationTestHarness.Create();
                _harnesses.Add(_fixture);
            }
            return _fixture;
        }

        // A C#-identifier-safe unique stem — file names, class names and
        // namespaces can never carry the '-' a Unique separator would.
        private string UniqueIdentifier(string stem) =>
            stem + "_" + _tag + "_" + (++_serial);

        private object TemplateInputs() =>
            new
            {
                template = "mono_behaviour",
                className = UniqueIdentifier("At02Template"),
                folder = ProbeFolder,
            };

        private object ScriptInputs()
        {
            var className = UniqueIdentifier("At02Script");
            return new
            {
                path = ProbeFolder + "/" + className + ".cs",
                content = ProbeScriptSource(className),
            };
        }

        // Valid C# for every probe write: the imported .cs may sit in a
        // deferred compile at the next editor tick, so every byte the
        // suite writes must compile cleanly — class name equal to the
        // file name the way Unity expects a script to be.
        private static string ProbeScriptSource(string className) =>
            "public static class " + className + "\n" +
            "{\n" +
            "    public const int Value = 0;\n" +
            "}\n";

        // ── Scripted package world ──────────────────────────────────────

        // The full three-channel install state the §G8.1 / §F5 evidence
        // judges read — manifest, lockfile and the registered package
        // list all read successfully, the target absent from every one.
        private static PackageInstallView AbsentView() =>
            new PackageInstallView(
                new JObject(), new JObject(), Array.Empty<ResolvedPackageView>());

        // The one internally consistent installed identity: manifest spec
        // = resolved version = registered version, source "registry" in
        // the lockfile and the registered list alike.
        private static PackageInstallView InstalledView() =>
            new PackageInstallView(
                new JObject { [PackageName] = PackageVersion },
                new JObject
                {
                    [PackageName] = new JObject
                    {
                        ["version"] = PackageVersion,
                        ["source"] = "registry",
                    },
                },
                new[]
                {
                    new ResolvedPackageView(
                        PackageName, PackageVersion, "registry",
                        PackageVersion, null, null),
                });

        // The damaged row for an add: the manifest entry landed but the
        // lockfile and the registered list never did — a partial state
        // whose verdict is Unknown only.
        private static PackageInstallView PartialAddView() =>
            new PackageInstallView(
                new JObject { [PackageName] = PackageVersion },
                new JObject(), Array.Empty<ResolvedPackageView>());

        // The damaged row for a remove: the manifest and lockfile entries
        // are gone but the registered list still carries the package —
        // the §F5 row's own partial-removal Unknown.
        private static PackageInstallView PartialRemoveView() =>
            new PackageInstallView(
                new JObject(), new JObject(),
                new[]
                {
                    new ResolvedPackageView(
                        PackageName, PackageVersion, "registry",
                        PackageVersion, null, null),
                });

        // ── SPEC §M.6 item 4 evidence probes ─────────────────────────

        // The probe for unity.package.add — reads the scripted package
        // world on its own, never the view the Reconcile under judgement
        // consumed (SPEC §M.6 item 4: the probe inspects the effect
        // state, it does not repeat the Reconcile's answer). The issued
        // add landed when the target is present on every channel, and is
        // proven absent only when it is missing from every channel and
        // no issued request is still outstanding that could still land
        // it — an outstanding request leaves the state Indeterminate
        // whatever the channels show (SPEC §M.6 item 4's table, §G8.1).
        private static EffectEvidence AddEvidence(ScriptedPackageWorld world)
        {
            var view = world.Next();
            if (world.RequestOutstanding)
            {
                return EffectEvidence.Indeterminate;
            }
            if (PresentEverywhere(view))
            {
                return EffectEvidence.Applied;
            }
            return AbsentEverywhere(view)
                ? EffectEvidence.NotApplied
                : EffectEvidence.Indeterminate;
        }

        // The probe for unity.package.remove — the mirror answer: the
        // issued removal landed when the target is absent from every
        // channel, and is proven unapplied only while the target is
        // still present on every channel and no issued request is still
        // outstanding that could still remove it (SPEC §M.6 item 4's
        // table, §F5's unity.package.remove row).
        private static EffectEvidence RemoveEvidence(ScriptedPackageWorld world)
        {
            var view = world.Next();
            if (world.RequestOutstanding)
            {
                return EffectEvidence.Indeterminate;
            }
            if (AbsentEverywhere(view))
            {
                return EffectEvidence.Applied;
            }
            return PresentEverywhere(view)
                ? EffectEvidence.NotApplied
                : EffectEvidence.Indeterminate;
        }

        // The target present on every channel — manifest entry,
        // lockfile entry and the registered list — with all three
        // channels read: the only capture that can prove the effect.
        private static bool PresentEverywhere(PackageInstallView view) =>
            view.AllChannelsRead &&
            view.ManifestDependencies?[PackageName] is not null &&
            view.LockDependencies?[PackageName] is not null &&
            ContainsRegistered(view);

        // The target absent from every channel, with all three read —
        // the only capture that can prove absence (SPEC §G8.1).
        private static bool AbsentEverywhere(PackageInstallView view) =>
            view.AllChannelsRead &&
            (view.ManifestDependencies is null ||
                view.ManifestDependencies[PackageName] is null) &&
            (view.LockDependencies is null ||
                view.LockDependencies[PackageName] is null) &&
            !ContainsRegistered(view);

        private static bool ContainsRegistered(PackageInstallView view)
        {
            for (var i = 0; i < view.Registered.Count; i++)
            {
                if (string.Equals(
                    view.Registered[i].Name, PackageName,
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        // The ordered answers the package Operations' state view returns
        // across one contract run — the run's Preflight, Execute recheck,
        // every Reconcile call and the §M.6 item 4 probe's own read
        // consume exactly the views the test scripted, and an unscripted
        // read fails the test itself: no contract ever asks for package
        // state the row did not set up.
        private sealed class ScriptedPackageWorld
        {
            private readonly Queue<PackageInstallView> _queue =
                new Queue<PackageInstallView>();

            // True while a request issued through the holding seam below
            // has not settled — the outstanding-request channel the §M.6
            // item 4 probe consults: an issued request still in flight
            // can still land its effect, so the channel keeps the answer
            // Indeterminate whatever the install views show.
            // Script clears it: each scripted block is a fresh world.
            internal bool RequestOutstanding { get; private set; }

            internal void Script(params PackageInstallView[] views)
            {
                _queue.Clear();
                RequestOutstanding = false;
                foreach (var view in views)
                {
                    _queue.Enqueue(view);
                }
            }

            internal PackageInstallView Next()
            {
                Assert.That(
                    _queue.Count, Is.GreaterThan(0),
                    "the contract asked for package state the test did not script");
                return _queue.Dequeue();
            }

            // The issue seam for a wait whose UPM request never settles
            // inside this scripted world: Execute issues it exactly like
            // the real Operation's Client.Add, but the request stays
            // outstanding — the state the §M.6 item 4 probe reads as
            // Indeterminate even on an absent view, because the request
            // could still land the effect.
            internal JsonValue IssueHoldingAdd(string id)
            {
                RequestOutstanding = true;
                return PackageResolvedCondition.InitialToken(
                    id, expectPresent: true);
            }
        }

        // The always-settled wait condition — the compile and package
        // waits the contract pump drives must resolve in one Poll.
        private sealed class DoneCondition : IWaitCondition
        {
            public WaitStatus Check(JsonValue token) => WaitStatus.Done();
        }

        // ── Path helpers ────────────────────────────────────────────────

        private static void EnsureFolder(string folderPath)
        {
            var segments = folderPath.Split('/');
            var parent = segments[0];
            for (var i = 1; i < segments.Length; i++)
            {
                var child = parent + "/" + segments[i];
                if (!AssetDatabase.IsValidFolder(child))
                {
                    Assert.That(AssetDatabase.CreateFolder(parent, segments[i]),
                        Is.Not.Empty, "the probe folder must be created: " + child);
                }
                parent = child;
            }
        }

        private static string ProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string PhysicalPath(string assetPath) =>
            Path.Combine(
                ProjectRoot(), assetPath.Replace('/', Path.DirectorySeparatorChar));

        // ── Fixture Operations ─────────────────────────────────────────

        /// <summary>
        /// The deliberately weakened fixture of the ticket's boundary
        /// acceptance: shaped on the script-writing Operations of the
        /// §I.4 group — a Reconcilable create whose Execute writes the
        /// .cs file — but its Reconcile answers
        /// <see cref="ReconcileResult.Completed"/> whenever a file exists
        /// at the recorded path. Existence and paths are the evidence
        /// SPEC §F5 explicitly rules out, so the damaged-evidence probe's
        /// overwritten file must make AssertIdempotency fail: the correct
        /// verdict there is Unknown only.
        /// </summary>
        [OpWeaveOperation("test.at02.weakened_script_create", 1)]
        public sealed class WeakenedScriptCreateReconcileOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.at02.weakened_script_create", 1)
                .Summary("Writes a .cs file; claims Completed on its existence.")
                .Input("path", "unity.AssetPath", required: true)
                .Effects(EffectNames.CodeWrite, EffectNames.AssetCreate)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.at02.weakened_script_create", 1,
                            "{\"path\":\"Assets/OpWeaveExamples/ProbeScript.cs\"}"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject
                    {
                        ["path"] = ctx.Get<string>("path"),
                    }));

            public StepResult Execute(StepContext ctx)
            {
                var path = ctx.Get<string>("path");
                File.WriteAllText(
                    Path.Combine(Application.dataPath, "..", path),
                    "public static class At02Weakened\n" +
                    "{\n" +
                    "    public const int Value = 0;\n" +
                    "}\n");
                ctx.Changes.Created(
                    new ChangeTarget("asset", path, null),
                    undoable: false);
                return StepResult.Succeeded();
            }

            public StepResult Poll(StepContext ctx) =>
                throw new NotSupportedException(
                    "Immediate operations are never polled.");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
            {
                var path = snapshot.ToToken()["path"]?.ToString();
                // The weakening this fixture exists for: Completed from the
                // file's mere presence — §F5's forbidden evidence.
                return path is not null && File.Exists(
                        Path.Combine(Application.dataPath, "..", path))
                    ? new ReconcileResult.Completed(
                        new Dictionary<string, JsonValue>(StringComparer.Ordinal))
                    : (ReconcileResult)new ReconcileResult.NotApplied();
            }

            public void Cancel(StepContext ctx)
            {
            }
        }

        /// <summary>
        /// The suspended-step weakened fixture of the ticket's boundary
        /// acceptance: shaped on <c>unity.package.add</c> — a Reconcilable
        /// Waitable whose Execute suspends on the issued effect — but its
        /// Reconcile answers <see cref="ReconcileResult.NotApplied"/>
        /// whatever the evidence shows. SPEC §M.6 item 4 admits that
        /// verdict only on <see cref="EffectEvidence.NotApplied"/>
        /// evidence — the issued effect provably absent — so
        /// AssertIdempotency must fail it under Applied evidence: a
        /// landed effect re-issued by recovery is exactly what judgement
        /// (b) exists to prevent.
        /// </summary>
        [OpWeaveOperation("test.at02.weakened_package_add", 1)]
        public sealed class WeakenedPackageAddReconcileOperation : IOperation
        {
            public OperationDescriptor Descriptor { get; } = Op
                .Define("test.at02.weakened_package_add", 1)
                .Summary("Suspends on an issued package add; claims NotApplied.")
                .Input("id", "core.String", required: true)
                .Effects(EffectNames.PackageModify)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
                {
                    ExecutionMode = ExecutionMode.Waitable,
                    Examples = new[]
                    {
                        ProbeOperationExample.For(
                            "test.at02.weakened_package_add", 1,
                            "{\"id\":\"com.unity.example@1.0.0\"}"),
                    },
                };

            public IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx) =>
                Array.Empty<Diagnostic>();

            public PreflightResult Preflight(StepContext ctx) =>
                new PreflightResult(
                    Array.Empty<Diagnostic>(),
                    Array.Empty<PlannedChange>(),
                    JsonValue.From(new JObject
                    {
                        ["id"] = ctx.Get<string>("id"),
                    }));

            public StepResult Execute(StepContext ctx) =>
                // The issued package add suspends exactly like the real
                // Operation's — the §G4 suspension is the crash window
                // this fixture's verdict lies inside.
                StepResult.Waiting(
                    JsonValue.From(new JObject
                    {
                        ["id"] = ctx.Get<string>("id"),
                    }),
                    "package resolution");

            public StepResult Poll(StepContext ctx) =>
                StepResult.Waiting(ctx.ResumeToken!, "package resolution");

            public ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot) =>
                // The weakening this fixture exists for: NotApplied
                // whatever the installed evidence shows — admissible only
                // on proven absence (SPEC §F5, §G8.1).
                new ReconcileResult.NotApplied();

            public void Cancel(StepContext ctx)
            {
            }
        }
    }
}
