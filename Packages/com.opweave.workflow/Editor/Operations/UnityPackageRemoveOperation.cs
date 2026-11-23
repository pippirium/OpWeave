#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.package.remove</c> (SPEC §I.4): removes one package by
    /// registry name through <see cref="Client.Remove"/>, waits on
    /// <see cref="PackageResolvedCondition"/> until the name leaves the
    /// registered package list, and reconciles on the SPEC §F5 row —
    /// manifest entry plus registered package list against the persisted
    /// preflight snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>name</c> port is required and <c>core.String</c>: the bare
    /// registry package name <see cref="Client.Remove"/> accepts. Unlike
    /// <c>unity.package.add</c>'s <c>id</c> it is not ConstantOnly — no
    /// credential can hide in a package name, so §G8.1's constant-input
    /// rule does not apply — but it must still parse as a package name;
    /// anything else (a <c>name@version</c> id, a URL, an empty string)
    /// is an OW-OP-006 constraint violation reported before any side
    /// effect.
    /// </para>
    /// <para>
    /// Preflight persists the §F5 evidence — the target name and the
    /// recorded pre-execution manifest, lockfile and registered-package
    /// entries for it — inside the preflight snapshot the Runner journals
    /// before StepStarted. Execution keeps no state in fields: the wait's
    /// resume token carries everything <see cref="Poll"/> needs across a
    /// domain reload (SPEC §C2 item 4, §G4). Removing an already-absent
    /// package is a Package Manager failure (OW-UNITY-009), and Reconcile
    /// returns <c>Completed</c> only when the snapshot proves the package
    /// existed and it is now absent from manifest and registered list
    /// alike, <c>NotApplied</c> only when the state equals the snapshot —
    /// so re-execution removes exactly the recorded artifact or fails
    /// cleanly on a proven absent one — and <c>Unknown</c> for everything
    /// else: a partial removal, a same-name artifact the snapshot never
    /// recorded, a modified entry, an unread channel. Reconciliation
    /// never removes anything itself (SPEC §F5).
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class UnityPackageRemoveOperation : OperationBase
    {
        /// <summary>The operation identifier (SPEC §I.4).</summary>
        public const string OperationId = "unity.package.remove";

        /// <summary>The required input port — the registry package name
        /// to remove.</summary>
        internal const string NamePort = "name";

        /// <summary>The StepWaiting label the Runner journals.</summary>
        internal const string WaitingForLabel = "package resolution";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        private readonly Func<string, JsonValue> _issueRemove;
        private readonly IWaitCondition _condition;
        private readonly Func<PackageInstallView> _view;

        /// <summary>The production operation: real
        /// <see cref="Client.Remove"/>, the real wait condition and the
        /// real package-state capture.</summary>
        public UnityPackageRemoveOperation()
            : this(
                static name => PackageResolvedCondition.InitialToken(
                    name, expectPresent: false, Client.Remove(name)),
                new PackageResolvedCondition(),
                PackageInstallView.Capture)
        {
        }

        /// <summary>The seam constructor: every Unity-side dependency is
        /// a delegate, so EditMode tests drive the same decision paths
        /// with controlled waits and package state.</summary>
        internal UnityPackageRemoveOperation(
            Func<string, JsonValue> issueRemove,
            IWaitCondition condition,
            Func<PackageInstallView> view)
        {
            _issueRemove = issueRemove ?? throw new ArgumentNullException(nameof(issueRemove));
            _condition = condition ?? throw new ArgumentNullException(nameof(condition));
            _view = view ?? throw new ArgumentNullException(nameof(view));
        }

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor =>
            Op.Define(OperationId, 1)
                .Summary(
                    "Removes a package by name and waits for the package " +
                    "manager to resolve the removal.")
                .Ja(
                    "パッケージ削除",
                    "名前で指定したパッケージを削除し、解決を待ちます。",
                    "パッケージ", "削除", "upm")
                .Input(NamePort, "core.String", required: true)
                .Effects(EffectNames.PackageModify, EffectNames.Compile)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
            {
                ExecutionMode = ExecutionMode.Waitable,
                DefaultTimeoutSeconds =
                    OperationDescriptor.DefaultWaitableTimeoutSeconds,
                DisplayName = "Remove Package",
                Description =
                    "Removes the named Unity package through the Package " +
                    "Manager and waits for the name to leave the " +
                    "registered package list. Reconciliation follows " +
                    "SPEC §F5: the manifest entry and the registered " +
                    "package list must both show the removal — or the " +
                    "exact pre-execution state — against the persisted " +
                    "preflight snapshot.",
                Category = "Package",
                Keywords = new[] { "package", "remove", "uninstall", "upm" },
                Tags = new[] { "package", "registry" },
                Destructive = true,
                Examples = new[]
                {
                    new OperationExample(
                        "Remove the Editor Coroutines package",
                        PackageOperationExamples.RemoveEditorCoroutines),
                },
            };

        /// <summary>
        /// The name-grammar half of static validation: a constant
        /// <c>name</c> that is not a registry package name is reported
        /// now, so the Plan carries the finding instead of the Run
        /// finding it at <see cref="Client.Remove"/>. Missing or
        /// non-string values are left to the D3/D4 findings.
        /// </summary>
        public override IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (!ctx.Parameters.TryGetValue(NamePort, out var input) ||
                !input.IsPresent ||
                input.Value!.ToToken() is not JValue { Type: JTokenType.String } text)
            {
                return Array.Empty<Diagnostic>();
            }
            return PackageSourceRule.IsValidRegistryName((string)text.Value!)
                ? Array.Empty<Diagnostic>()
                : new[] { InvalidName(ctx.NodeId) };
        }

        /// <summary>
        /// No side effects: re-checks the name grammar, then captures and
        /// returns the §F5 evidence snapshot — the name and its recorded
        /// pre-execution manifest, lockfile and registered-package
        /// entries — so the Runner journals it before StepStarted.
        /// </summary>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (!ctx.IsPresent(NamePort) || ctx.IsPending(NamePort))
            {
                return base.Preflight(ctx);
            }
            var name =
                ctx.Get<JsonValue>(NamePort).ToToken() is JValue
                    { Type: JTokenType.String } text
                    ? (string)text.Value!
                    : null;
            if (name is null || !PackageSourceRule.IsValidRegistryName(name))
            {
                return new PreflightResult(
                    new[] { InvalidName(nodeId: null) },
                    Array.Empty<PlannedChange>(),
                    s_emptySnapshot);
            }
            // SPEC §D0.3 item 3: the removal of a package an unfinished
            // Run's definitions live in — or depend on — is refused
            // (OW-RUN-013) before the evidence snapshot is even built.
            var guarded = RunDefinitionGuard.CheckPackageMutation(name);
            if (guarded is not null)
            {
                return new PreflightResult(
                    new[] { guarded },
                    Array.Empty<PlannedChange>(),
                    s_emptySnapshot);
            }
            return new PreflightResult(
                Array.Empty<Diagnostic>(),
                Array.Empty<PlannedChange>(),
                PackageRemoveEvidence.BuildSnapshot(name, _view()));
        }

        /// <summary>
        /// Issues <see cref="Client.Remove"/> and suspends on the
        /// <see cref="PackageResolvedCondition"/> resume token — the only
        /// state the wait needs across a domain reload (SPEC §C2 item 4).
        /// The name-grammar check runs immediately before the side
        /// effect, after which <see
        /// cref="PackageResolvedCondition.InitialToken(string, bool, Request)"/>
        /// is guaranteed a registry name.
        /// </summary>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var name = ctx.Get<string>(NamePort);
            if (!PackageSourceRule.IsValidRegistryName(name))
            {
                return StepResult.Failed(InvalidName(nodeId: null));
            }
            // SPEC §D0.3 item 4: the same refusal repeated immediately
            // before the side effect — on the main thread Run acceptance
            // serializes on — catches a Run admitted since Preflight;
            // Client.Remove is never reached when it refuses.
            var guarded = RunDefinitionGuard.CheckPackageMutation(name);
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }
            return StepResult.Waiting(_issueRemove(name), WaitingForLabel);
        }

        /// <summary>
        /// Reconstructs all wait state from the persisted resume token —
        /// the only state a domain reload preserves (SPEC §C2 item 4) —
        /// and reports the removal's <c>Deleted</c> change record when
        /// the name has left the registered package list.
        /// </summary>
        public override StepResult Poll(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var token = ctx.ResumeToken ?? throw new InvalidOperationException(
                "A Waitable Operation keeps its wait state only in the resume " +
                "token (SPEC §C2 item 4); none was handed back.");
            var status = _condition.Check(token);
            switch (status.Kind)
            {
                case WaitStatusKind.Done:
                    ctx.Changes.Deleted(
                        new ChangeTarget(
                            "package",
                            PackageResolvedCondition.NameOf(token) ??
                                ctx.Get<string>(NamePort)),
                        undoable: false);
                    return StepResult.Succeeded();
                case WaitStatusKind.Failed:
                    return StepResult.Failed(
                        status.Error is { } failure
                            ? failure with { Port = NamePort }
                            : throw new InvalidOperationException(
                                "A Failed WaitStatus must carry its " +
                                "Diagnostic (SPEC §G4)."));
                default:
                    return StepResult.Waiting(status.ResumeToken!, WaitingForLabel);
            }
        }

        /// <summary>
        /// Ends the same-domain wait request when the step is cancelled —
        /// the only release the token carries (§G4); the registered
        /// package list is unaffected either way.
        /// </summary>
        public override void Cancel(StepContext ctx)
        {
            if (ctx?.ResumeToken is { } token)
            {
                PackageResolvedCondition.EndWait(token);
            }
        }

        /// <summary>
        /// The §F5 verdict against the persisted preflight snapshot:
        /// Completed only when the package provably existed and is absent
        /// from manifest and registered list alike, NotApplied only when
        /// the state equals the snapshot (re-execution is provably safe),
        /// Unknown for everything else — never an automatic re-remove.
        /// </summary>
        public override ReconcileResult Reconcile(StepContext ctx, JsonValue snapshot)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }
            return PackageRemoveEvidence.Judge(snapshot, _view());
        }

        // The OW-OP-006 finding for a constant or bound value that is not
        // a registry package name — the only input constraint the port
        // carries (SPEC §I.4's `name*: String`).
        private static Diagnostic InvalidName(string? nodeId) =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                $"Parameter '{NamePort}' must be a package name.",
                nodeId: nodeId,
                port: NamePort,
                hint: "unity.package.remove removes by registry package " +
                    "name — the lower-case dot-separated name (like " +
                    "'com.unity.timeline'), not a name@version id or a " +
                    "Git URL.");
    }
}
