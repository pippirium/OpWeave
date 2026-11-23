#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor.PackageManager;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.package.add</c> (SPEC §I.4): adds one package by registry
    /// <c>name[@version]</c> or Git URL through
    /// <see cref="Client.Add"/>, waits on <see cref="PackageResolvedCondition"/>
    /// and reconciles strictly by source identity — registry name plus
    /// resolved version; Git package name, normalized repository URL,
    /// package sub-path, requested revision and the full resolved commit
    /// SHA (SPEC §G8.1, §F5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>id</c> port is required, <c>core.String</c> and
    /// <b>ConstantOnly</b>: a bound or edge-fed value is the D6 phase's
    /// OW-OP-006 finding before a Plan exists. A credential-bearing Git
    /// URL is rejected with OW-OP-006 — first by the §D6 permission phase
    /// (<see cref="GitUrlCredentialCheck"/>) and again by the source
    /// policy — and an unrecognized or disallowed source is OW-PERM-001
    /// (§G8 item 6). The check runs at static validation and again
    /// immediately before <see cref="Client.Add"/>, because settings can
    /// change between planning and execution (§H4.1). Rejected texts are
    /// never echoed into a diagnostic, a plan, a journal or a log.
    /// </para>
    /// <para>
    /// Preflight persists the §G8.1 evidence — expected source identity
    /// and the expected commit whenever a full SHA is determinable —
    /// together with the recorded pre-execution manifest, lockfile and
    /// registered-package entries for the target, inside the preflight
    /// snapshot the Runner journals before StepStarted. Execution keeps
    /// no state in fields: the wait's resume token carries everything
    /// <see cref="Poll"/> needs across a domain reload (SPEC §C2 item 4,
    /// §G4). Reconcile returns <c>Completed</c> only on the full §G8.1
    /// evidence row, <c>NotApplied</c> only when the target is provably
    /// absent everywhere and the snapshot recorded absence, and
    /// <c>Unknown</c> for every other state — a moved tag, a same-name
    /// package, a manifest-only partial — never re-adding automatically.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class UnityPackageAddOperation : OperationBase
    {
        /// <summary>The operation identifier (SPEC §I.4).</summary>
        public const string OperationId = "unity.package.add";

        /// <summary>The required ConstantOnly input port.</summary>
        internal const string IdPort = "id";

        /// <summary>The JSON output port <c>{name, version, source}</c>.</summary>
        internal const string PackagePort = "package";

        /// <summary>The StepWaiting label the Runner journals.</summary>
        internal const string WaitingForLabel = "package resolution";

        private static readonly JsonValue s_emptySnapshot = JsonValue.From(new JObject());

        private readonly Func<Result<OpWeaveSettings>> _loadSettings;
        private readonly Func<string, JsonValue> _issueAdd;
        private readonly IWaitCondition _condition;
        private readonly Func<PackageInstallView> _view;

        /// <summary>The production operation: real settings, real
        /// <see cref="Client.Add"/>, the real wait condition and the real
        /// package-state capture.</summary>
        public UnityPackageAddOperation()
            : this(
                static () => OpWeaveSettingsLoader.Load(),
                static id => PackageResolvedCondition.InitialToken(
                    id, expectPresent: true, Client.Add(id)),
                new PackageResolvedCondition(),
                PackageInstallView.Capture)
        {
        }

        /// <summary>The seam constructor: every Unity-side dependency is a
        /// delegate, so EditMode tests drive the same decision paths with
        /// controlled settings, waits and package state.</summary>
        internal UnityPackageAddOperation(
            Func<Result<OpWeaveSettings>> loadSettings,
            Func<string, JsonValue> issueAdd,
            IWaitCondition condition,
            Func<PackageInstallView> view)
        {
            _loadSettings = loadSettings ?? throw new ArgumentNullException(nameof(loadSettings));
            _issueAdd = issueAdd ?? throw new ArgumentNullException(nameof(issueAdd));
            _condition = condition ?? throw new ArgumentNullException(nameof(condition));
            _view = view ?? throw new ArgumentNullException(nameof(view));
        }

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor =>
            Op.Define(OperationId, 1)
                .Summary(
                    "Adds a package by registry name@version or Git URL and waits " +
                    "for the package manager to resolve it.")
                .Ja(
                    "パッケージ追加",
                    "name@version または Git URL でパッケージを追加し、解決を待ちます。",
                    "パッケージ", "追加", "git")
                .Input(IdPort, "core.String", required: true, c => c.ConstantOnly())
                .Output(PackagePort, "core.Json")
                .Effects(EffectNames.PackageModify, EffectNames.Compile)
                .Undo(UndoSupport.None)
                .Idempotency(Idempotency.Reconcilable)
                .Build() with
            {
                ExecutionMode = ExecutionMode.Waitable,
                DefaultTimeoutSeconds = OperationDescriptor.DefaultWaitableTimeoutSeconds,
                DisplayName = "Add Package",
                Description =
                    "Adds one Unity package — a registry name@version or a Git URL " +
                    "from the allowedPackageSources setting — and waits for the " +
                    "resolve. Git package reconciliation identifies the result by " +
                    "source and full commit SHA.",
                Category = "Package",
                Keywords = new[] { "package", "add", "upm", "git" },
                Tags = new[] { "package", "registry", "git" },
                Examples = new[]
                {
                    new OperationExample(
                        "Add the Editor Coroutines package",
                        PackageOperationExamples.AddEditorCoroutines),
                },
            };

        /// <summary>
        /// The SG-19 allowlist half of static validation. A credential URL
        /// is skipped here — the §D6 permission phase already reports it
        /// as OW-OP-006 (<see cref="PermissionValidationPhase"/>), and the
        /// policy reports the same finding first for any path that misses
        /// that phase. Missing or non-string values are left to the D3/D4
        /// findings.
        /// </summary>
        public override IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (!ctx.Parameters.TryGetValue(IdPort, out var input) ||
                !input.IsPresent ||
                input.Value!.ToToken() is not JValue { Type: JTokenType.String } text)
            {
                return Array.Empty<Diagnostic>();
            }
            var id = (string)text.Value!;
            if (PackageIdSource.ContainsCredentials(id))
            {
                return Array.Empty<Diagnostic>();
            }
            return CheckSource(id, ctx.NodeId);
        }

        /// <summary>
        /// No side effects: re-checks the source policy, then captures and
        /// returns the §G8.1 evidence snapshot — expected identity,
        /// expected commit where determinable, and the recorded
        /// manifest/lockfile/registered state for the target — so the
        /// Runner journals it before StepStarted.
        /// </summary>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            if (!ctx.IsPresent(IdPort) || ctx.IsPending(IdPort))
            {
                return base.Preflight(ctx);
            }
            var id = ctx.Get<JsonValue>(IdPort).ToToken() is JValue { Type: JTokenType.String } text
                ? (string)text.Value!
                : null;
            var rejected = CheckSource(id, nodeId: null);
            if (rejected.Count > 0)
            {
                return new PreflightResult(
                    rejected, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            // The policy check guarantees the id parses under the strict
            // grammar; the tolerant parse accepts every strict form.
            PackageAddSource.TryParse(id, out var source);
            var view = _view();
            // SPEC §D0.3 item 3: while any Run is unfinished a package
            // mutation is refused before side effects unless it provably
            // changes nothing.
            var guarded = CheckRunMutationGuard(source!, view);
            if (guarded is not null)
            {
                return new PreflightResult(
                    new[] { guarded }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var snapshot = PackageAddEvidence.BuildSnapshot(id!, source!, view);
            return new PreflightResult(
                Array.Empty<Diagnostic>(), Array.Empty<PlannedChange>(), snapshot);
        }

        /// <summary>
        /// Re-checks the source policy immediately before the side effect
        /// (§G8 item 6: settings may have changed since planning), issues
        /// <see cref="Client.Add"/> and suspends on the
        /// <see cref="PackageResolvedCondition"/> resume token.
        /// </summary>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var id = ctx.Get<string>(IdPort);
            var rejected = CheckSource(id, nodeId: null);
            if (rejected.Count > 0)
            {
                return StepResult.Failed(rejected[0]);
            }
            // SPEC §D0.3 item 4: the same refusal repeated immediately
            // before the side effect — on the main thread Run acceptance
            // serializes on — catches a Run admitted since Preflight;
            // Client.Add is never reached when it refuses.
            PackageAddSource.TryParse(id, out var source);
            var guarded = CheckRunMutationGuard(source!, _view());
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }
            return StepResult.Waiting(_issueAdd(id), WaitingForLabel);
        }

        /// <summary>
        /// Reconstructs all wait state from the persisted resume token —
        /// the only state a domain reload preserves (SPEC §C2 item 4) —
        /// and reports the resolved package as the <c>package</c> output
        /// when the wait is done.
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
                    var resolved = ResolveTarget(token);
                    if (resolved is null)
                    {
                        // The condition reported presence but the
                        // registered list cannot attribute a package to
                        // this request's source — keep waiting rather
                        // than guess (§G8.1: identity by source only).
                        return StepResult.Waiting(token, WaitingForLabel);
                    }
                    ctx.Changes.Created(
                        new ChangeTarget("package", resolved.Name), undoable: false);
                    ctx.SetOutput(PackagePort, JsonValue.From(resolved.OutputJson()));
                    return StepResult.Succeeded();
                case WaitStatusKind.Failed:
                    return StepResult.Failed(
                        status.Error is { } failure
                            ? failure with { Port = IdPort }
                            : throw new InvalidOperationException(
                                "A Failed WaitStatus must carry its Diagnostic (SPEC §G4)."));
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
        /// The §G8.1 verdict against the persisted preflight snapshot:
        /// Completed only on the full evidence row, NotApplied only on
        /// provable absence, Unknown for everything else — never a re-add.
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
            return PackageAddEvidence.Judge(snapshot, _view());
        }

        // The settings → SG-19 policy path shared by static validation,
        // preflight and the pre-side-effect recheck. The policy check
        // order (credentials first, then parse, then allowlist) is
        // PackageSourcePolicy's own; a settings load failure surfaces its
        // OW-CFG diagnostics as the rejection.
        private IReadOnlyList<Diagnostic> CheckSource(string? id, string? nodeId)
        {
            var settings = _loadSettings();
            if (!settings.Ok)
            {
                return settings.Diagnostics;
            }
            var rejected = PackageSourcePolicy
                .For(settings.Value!)
                .CheckPackageId(id, nodeId: nodeId, port: IdPort);
            return rejected is null
                ? (IReadOnlyList<Diagnostic>)Array.Empty<Diagnostic>()
                : new[] { rejected };
        }

        // The SPEC §D0.3 half of Preflight and the pre-side-effect recheck:
        // a package add is a mutation of the installed set, and dependency
        // resolution can move packages the request never names — so while
        // any Run is unfinished the only provable no-op allowed through is
        // a reinstall identical to the installed pin (registry name plus
        // resolved version, or a Git source whose resolved commit already
        // equals the requested full SHA). Every other case either names a
        // protected package or cannot prove it leaves every protected
        // package alone, and answers OW-RUN-013; the refusal reports only
        // the package name, never the URL text of a Git request.
        private Diagnostic? CheckRunMutationGuard(
            PackageAddSource source, PackageInstallView view)
        {
            if (!RunDefinitionGuard.HasProtectedRuns)
            {
                return null;
            }
            if (!view.RegisteredRead)
            {
                // The installed set cannot be established — no add can be
                // proven a no-op or shown unrelated (SPEC §D0.3 item 3).
                return RunDefinitionGuard.CheckUnprovableDefinitionChange(
                    source.IsGit ? "git source" : source.PackageName);
            }
            var registered = view.Registered;
            if (source.IsGit)
            {
                ResolvedPackageView? matched = null;
                for (var i = 0; i < registered.Count; i++)
                {
                    if (PackageAddEvidence.SameGitSource(
                        source, registered[i].Dependency))
                    {
                        matched = registered[i];
                        break;
                    }
                }
                if (matched is null)
                {
                    return RunDefinitionGuard.CheckUnprovableDefinitionChange("git source");
                }
                if (source.ExpectedCommit is { } commit &&
                    string.Equals(
                        commit, matched.ResolvedCommit, StringComparison.Ordinal))
                {
                    return null; // reinstalling the identical resolved commit changes nothing.
                }
                return RunDefinitionGuard.CheckPackageMutation(matched.Name) ??
                    RunDefinitionGuard.CheckUnprovableDefinitionChange(matched.Name);
            }
            ResolvedPackageView? installed = null;
            for (var i = 0; i < registered.Count; i++)
            {
                if (string.Equals(
                    registered[i].Name, source.PackageName, StringComparison.Ordinal))
                {
                    installed = registered[i];
                    break;
                }
            }
            if (installed is null)
            {
                // A name absent from the registered list is a fresh
                // install whose resolution effects are unprovable.
                return RunDefinitionGuard.CheckUnprovableDefinitionChange(
                    source.PackageName);
            }
            if (source.Version is { } version &&
                string.Equals(version, installed.Version, StringComparison.Ordinal))
            {
                return null; // the identical pin is already installed.
            }
            return RunDefinitionGuard.CheckPackageMutation(source.PackageName) ??
                RunDefinitionGuard.CheckUnprovableDefinitionChange(source.PackageName);
        }

        // Identifies the installed package the just-resolved request added:
        // by Git origin + sub-path for a Git id, by package name for a
        // registry id — never by package name alone for a Git source.
        private ResolvedPackageView? ResolveTarget(JsonValue token)
        {
            if (token.ToToken() is not JObject obj ||
                obj["id"] is not JValue { Type: JTokenType.String } idText ||
                !PackageAddSource.TryParse((string)idText.Value!, out var source))
            {
                return null;
            }
            var registered = _view().Registered;
            for (var i = 0; i < registered.Count; i++)
            {
                var entry = registered[i];
                var match = source!.IsGit
                    ? PackageAddEvidence.SameGitSource(source, entry.Dependency)
                    : string.Equals(
                        entry.Name, source.PackageName, StringComparison.Ordinal);
                if (match)
                {
                    return entry;
                }
            }
            return null;
        }
    }
}
