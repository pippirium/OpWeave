#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.UnityAdapter;
using UnityEditor;
using UnityEngine;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.script.create</c> (SPEC §I.4, §G8 item 2 — the stage-3 code
    /// permission of §H4): writes arbitrary C# source to a project path and
    /// imports it. The step never compiles; the compile wait is
    /// <c>unity.script.compile</c>'s job (§G8 item 3). Immediate, Undo None,
    /// <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Permissions.</b> The Operation declares <c>code.write</c> +
    /// <c>asset.create</c>. An Agent <c>code.write</c> is never below ask —
    /// the §H4 stage floor every permission policy shares through
    /// <c>PermissionEvaluator</c>, and the settings loader already turns a
    /// persisted <c>allow</c> into <c>ask</c> with OW-CFG-005 — so an Agent
    /// step always reaches AwaitingApproval. The <c>content</c> port is
    /// <c>ConstantOnly</c> (SPEC §D4): the full source is a Plan constant the
    /// approval detail shows verbatim, and the approval binds to the
    /// planHash that covers it, so an approved plan cannot have its content
    /// swapped afterwards (§G8 item 2). The <c>path</c> port carries the §G1
    /// <c>path_writable</c> requirement like every other write port, judged
    /// against the Run's frozen <c>allowedPaths</c> snapshot (§H4.1, D-R6).
    /// </para>
    /// <para>
    /// <b>The code-path bound.</b> On top of <c>path_writable</c>, the write
    /// may only land inside the settings <c>codePaths</c> (default
    /// <c>Assets/Scripts/</c>; the custom Operation folder is deliberately
    /// not part of the default set — a human opts in, §G8 item 2, §M.7).
    /// A constant path is refused at <see cref="ValidateStatic"/> and the
    /// same check re-runs at Preflight/Execute for edge-fed values, both as
    /// OW-PERM-002. The configured list is read through the Operation's
    /// settings seam, which fails closed — a settings file that cannot be
    /// loaded leaves no code roots at all — while the Run's accepted
    /// <c>allowedPaths</c> snapshot still bounds every write the requirement
    /// evaluator admits, so a mid-Run settings edit can never widen the
    /// write past what the Plan was validated under.
    /// </para>
    /// <para>
    /// <b>Write semantics.</b> <c>path</c> must name a <c>.cs</c> file
    /// (OW-OP-006 otherwise), its parent folder must exist (OW-UNITY-005),
    /// and an existing target is refused unless <c>overwrite</c> is true —
    /// the overwrite rewrites the file in place and the imported asset
    /// keeps its identity. The file is written UTF-8 without BOM, exactly
    /// the bytes <c>content</c> spells out, then imported synchronously;
    /// a file that does not import as a <see cref="MonoScript"/> is the §F5
    /// partial side effect (OW-UNITY-008).
    /// </para>
    /// <para>
    /// <b>Reconciliation (§F5 row).</b> Preflight persists the evidence row:
    /// the normalized target path, its pre-state (nothing, or the existing
    /// file's content hash when <c>overwrite</c> admits it), the input
    /// <c>content</c>'s hash and the Editor session id. <see cref="Reconcile"/>
    /// decides only from that evidence: the pre-state preserved is
    /// <see cref="ReconcileResult.NotApplied"/> (re-execution writes the
    /// approved content, it cannot duplicate); the file hashing to the
    /// input content <i>and</i> imported as a MonoScript is
    /// <see cref="ReconcileResult.Completed"/> with the <c>script</c> output
    /// restored; everything else — foreign or modified content, a directory,
    /// a leftover <c>.meta</c>, an un-imported file, unrestorable evidence —
    /// is <see cref="ReconcileResult.Unknown"/>. The artifact is a file,
    /// re-identifiable across an Editor restart by the permanent
    /// path/guid/content-hash keys (§F5 common rule 3), so the recorded
    /// session id never gates the verdict.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class CreateScriptOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.script.create";

        internal const string PathPort = "path";
        internal const string ContentPort = "content";
        internal const string OverwritePort = "overwrite";
        internal const string ScriptPort = "script";

        private const string ScriptSuffix = ".cs";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        // The code-path source: the settings file's codePaths, or null when
        // they cannot be established — the test seam replaces the file read.
        private readonly Func<IReadOnlyList<string>?> _codePaths;

        /// <summary>Creates the Operation over the project settings file.</summary>
        public CreateScriptOperation()
            : this(CurrentCodePaths)
        {
        }

        /// <summary>
        /// The test seam over the configured <c>codePaths</c> — the
        /// <see cref="EditorStateProbe"/> pattern — so the bounds decision is
        /// provable without a settings file.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="codePaths"/> is null.</exception>
        internal CreateScriptOperation(Func<IReadOnlyList<string>?> codePaths)
        {
            _codePaths =
                codePaths ?? throw new ArgumentNullException(nameof(codePaths));
        }

        // The configured code roots of SPEC §H4.1: the settings file read
        // once per call, fail closed — a file that fails to load is the
        // settings-error state (D-R6), which admits no code write at all.
        private static IReadOnlyList<string>? CurrentCodePaths() =>
            OpWeaveSettingsLoader.Load() is { Ok: true, Value: { } settings }
                ? settings.CodePaths
                : null;

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override IReadOnlyList<Diagnostic> ValidateStatic(StaticContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var diagnostics = new List<Diagnostic>();
            CheckConstantPath(ctx, diagnostics);
            return diagnostics;
        }

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // §D8 item 1: a pending port has no value to inspect in a Dry Run,
            // and a required port with no value yields no evidence to record.
            if (ctx.IsPending(PathPort) || ctx.IsPending(ContentPort) ||
                ctx.IsPending(OverwritePort) ||
                !ctx.IsPresent(PathPort) || !ctx.IsPresent(ContentPort))
            {
                return base.Preflight(ctx);
            }
            var plan = ReadInputs(ctx);
            if (!plan.Ok)
            {
                return new PreflightResult(
                    plan.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var refused = RefuseUnwritable(plan.Value!, out var existingHash);
            if (refused is not null)
            {
                return new PreflightResult(
                    new[] { refused }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var guarded = RunDefinitionGuard.CheckPath(
                plan.Value!.Path, GuardedPathRole.Destination);
            if (guarded is not null)
            {
                return new PreflightResult(
                    new[] { guarded }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }

            // The §F5 evidence row for this Operation (SPEC §F5): the target
            // path and its pre-state — nothing, or the existing file's
            // content hash when overwrite admits one — plus the input
            // content's hash and the session id as restart evidence (it
            // never gates a file artifact's verdict).
            var snapshot = new JObject
            {
                ["path"] = plan.Value!.Path,
                ["pathExisted"] = existingHash is not null,
                ["expectedContentHash"] =
                    ScriptTemplates.ContentHash(plan.Value!.Content),
                ["overwrite"] = plan.Value!.Overwrite,
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            if (existingHash is not null)
            {
                snapshot["previousContentHash"] = existingHash;
            }
            return new PreflightResult(
                Array.Empty<Diagnostic>(),
                Array.Empty<PlannedChange>(),
                JsonValue.From(snapshot));
        }

        /// <inheritdoc/>
        public override StepResult Execute(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            var plan = ReadInputs(ctx);
            if (!plan.Ok)
            {
                return StepResult.Failed(plan.Diagnostics[0]);
            }
            var inputs = plan.Value!;
            var refused = RefuseUnwritable(inputs, out _);
            if (refused is not null)
            {
                return StepResult.Failed(refused);
            }
            // SPEC §D0.3 item 4: re-checked here, on the main thread Run
            // acceptance serializes on, so a Run admitted since Preflight
            // still blocks the write — nothing is written when it refuses.
            var guarded = RunDefinitionGuard.CheckPath(
                inputs.Path, GuardedPathRole.Destination);
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }

            File.WriteAllText(inputs.Physical!, inputs.Content, ScriptTemplates.Encoding);
            AssetDatabase.ImportAsset(inputs.Path, ImportAssetOptions.ForceSynchronousImport);
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(inputs.Path);
            if (script == null)
            {
                // The file is on disk but not imported as a script — the §F5
                // partial side effect; OW-UNITY-008 is the §J code.
                return StepResult.Failed(ImportFailed());
            }
            var guid = AssetDatabase.AssetPathToGUID(inputs.Path);

            if (inputs.Overwrote)
            {
                ctx.Changes.Modified(
                    new ChangeTarget("asset", inputs.Path, guid), undoable: false);
            }
            else
            {
                ctx.Changes.Created(
                    new ChangeTarget("asset", inputs.Path, guid), undoable: false);
            }
            ctx.SetOutput(ScriptPort, script);
            return StepResult.Succeeded();
        }

        /// <inheritdoc/>
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
            if (snapshot.ToToken() is not JObject obj)
            {
                return Unknown("The Preflight snapshot is not an object.");
            }
            if (!Member(obj, "path", JTokenType.String, out var pathToken) ||
                !Member(obj, "pathExisted", JTokenType.Boolean, out var existedToken) ||
                !Member(obj, "expectedContentHash", JTokenType.String, out var hashToken) ||
                !Member(obj, "overwrite", JTokenType.Boolean, out _) ||
                !Member(obj, "editorSessionId", JTokenType.String, out _))
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var path = (string)pathToken!;
            var pathExisted = (bool)existedToken!;
            var expectedHash = (string)hashToken!;
            string? previousHash = null;
            if (obj["previousContentHash"] is { } previousMember)
            {
                if (previousMember.Type != JTokenType.String)
                {
                    return Unknown("The Preflight snapshot is missing a required evidence member.");
                }
                previousHash = (string?)previousMember;
            }
            if (pathExisted && previousHash is null)
            {
                return Unknown("The Preflight snapshot recorded an existing file without its content hash.");
            }
            // §F5 rule 6: an expectation that cannot be re-established — a
            // malformed hash — can never prove a verdict.
            if (expectedHash.Length != 64 || !IsLowerHex(expectedHash) ||
                (previousHash is not null &&
                    (previousHash.Length != 64 || !IsLowerHex(previousHash))))
            {
                return Unknown("The snapshot's content hash is malformed.");
            }

            var physical = AssetArtifact.ToPhysicalPath(path);
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            var fileExists = physical is not null && File.Exists(physical);
            var dirExists = physical is not null && Directory.Exists(physical);
            var metaExists = physical is not null && File.Exists(physical + ".meta");

            if (fileExists)
            {
                string actualHash;
                try
                {
                    actualHash =
                        ScriptTemplates.ContentHash(File.ReadAllBytes(physical!));
                }
                catch (Exception)
                {
                    return Unknown("The artifact's content cannot be read.");
                }
                // Completed needs the whole evidence row: the file's content
                // is exactly the approved input and it is imported as a
                // MonoScript.
                if (string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
                {
                    // §F5 pre-existing-artifact case: when the recorded
                    // pre-state already held the requested bytes, a content
                    // match cannot distinguish "the overwrite ran" from "the
                    // step never ran" — the evidence proves no verdict.
                    if (pathExisted &&
                        string.Equals(previousHash, expectedHash, StringComparison.Ordinal))
                    {
                        return Unknown(
                            "The recorded pre-state already matches the expected content, so the file cannot prove the step ran.");
                    }
                    if (guid.Length == 0 ||
                        AssetDatabase.LoadAssetAtPath<MonoScript>(path) == null)
                    {
                        // Written but not imported as a script: the §F5
                        // partial side effect, which automatic re-execution
                        // must not retry over.
                        return Unknown(
                            "The file's content matches the expected output, but it is not imported as a script.");
                    }
                    var outputs = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
                    {
                        [ScriptPort] = JsonValue.From(new JObject
                        {
                            ["kind"] = "asset",
                            ["guid"] = guid,
                            ["path"] = path,
                        }),
                    };
                    return new ReconcileResult.Completed(outputs);
                }
                // The recorded pre-state preserved byte-for-byte proves
                // NotApplied for the overwrite create: re-execution writes
                // the approved content over the same file.
                if (pathExisted &&
                    string.Equals(actualHash, previousHash, StringComparison.Ordinal))
                {
                    return new ReconcileResult.NotApplied();
                }
                return Unknown(
                    "The file's content matches neither the expected output nor the recorded pre-state.");
            }
            if (dirExists)
            {
                return Unknown("A directory occupies the target path.");
            }
            if (metaExists)
            {
                return Unknown("A leftover .meta file without a file occupies the target path.");
            }
            if (guid.Length > 0)
            {
                return Unknown("An asset database entry without a file occupies the target path.");
            }
            // The pre-state the snapshot recorded: for a fresh create that
            // is a completely empty path — the step provably did not apply
            // and re-execution can only create, never duplicate. A recorded
            // pre-existing file that is gone is neither.
            return pathExisted
                ? Unknown("The recorded pre-existing file is gone.")
                : (ReconcileResult)new ReconcileResult.NotApplied();
        }

        // ── Input resolution ──────────────────────────────────────────────

        private sealed class StepInputs
        {
            internal string Path = string.Empty;
            internal string? Physical;
            internal string Content = string.Empty;
            internal bool Overwrite;
            internal bool Overwrote;
        }

        // The validated inputs of a ValidateStatic/Preflight/Execute call —
        // every port is re-checked at step time because a value may arrive
        // through a data edge the static phases could not see (SPEC §D3, §E5).
        private Result<StepInputs> ReadInputs(StepContext ctx)
        {
            var diagnostics = new List<Diagnostic>();
            var path = AssetPathPolicy.CheckWritePath(
                ctx.Get<string>(PathPort), PathPort);
            if (!path.Ok)
            {
                diagnostics.AddRange(path.Diagnostics);
            }
            else
            {
                diagnostics.AddRange(CheckScriptPath(path.Value!));
            }

            // The port is required; a present null is the type check's
            // OW-TYPE-003 finding, and the CLR conversion's StepContextException
            // reports a mismatched token (SPEC §0.6 SG-06).
            var content = ctx.Get<string>(ContentPort);
            var overwrite =
                ctx.IsPresent(OverwritePort) && ctx.Get<bool>(OverwritePort);

            if (diagnostics.Count != 0)
            {
                return Result<StepInputs>.Failure(diagnostics);
            }
            var normalized = path.Value!;
            return Result<StepInputs>.Success(new StepInputs
            {
                Path = normalized,
                Physical = AssetArtifact.ToPhysicalPath(normalized),
                Content = content,
                Overwrite = overwrite,
            });
        }

        // The port-shape halves beyond the shared write-path policy: the
        // §I.4 "AssetPath(.cs)" contract (OW-OP-006) and the §G8 item 2
        // code-path bound (OW-PERM-002). A path that cannot resolve inside
        // the project cannot occupy any code root either.
        private List<Diagnostic> CheckScriptPath(string normalized)
        {
            var diagnostics = new List<Diagnostic>();
            if (!normalized.EndsWith(ScriptSuffix, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(NotScriptFile());
            }
            var roots = _codePaths();
            if (roots is null ||
                !AssetPathPolicy.IsInsideAnyRoot(
                    normalized, AssetPathPolicy.RootPrefixes(roots)))
            {
                diagnostics.Add(OutsideCodePaths());
            }
            return diagnostics;
        }

        // The pre-side-effect refusal of Preflight/Execute: resolves the
        // target's disk state, refuses an unwritable destination, and —
        // when overwrite admits an existing file — hashes its current bytes
        // for the §F5 pre-state evidence. Returns the refusing diagnostic,
        // or null when the write may proceed.
        private static Diagnostic? RefuseUnwritable(
            StepInputs inputs, out string? existingHash)
        {
            existingHash = null;
            if (inputs.Physical is null)
            {
                return TargetFolderMissing();
            }
            if (!Directory.Exists(Path.GetDirectoryName(inputs.Physical)))
            {
                return TargetFolderMissing();
            }
            if (Directory.Exists(inputs.Physical))
            {
                return TargetOccupied();
            }
            if (!File.Exists(inputs.Physical))
            {
                // An orphan .meta or an asset database entry without a file
                // still occupies a no-overwrite create (§F5 evidence row:
                // the pre-state is "nothing" only when nothing is there).
                if (!inputs.Overwrite && AssetArtifact.Occupied(
                        inputs.Physical, inputs.Path))
                {
                    return TargetOccupied();
                }
                return null;
            }
            if (!inputs.Overwrite)
            {
                return TargetOccupied();
            }
            try
            {
                existingHash =
                    ScriptTemplates.ContentHash(File.ReadAllBytes(inputs.Physical));
            }
            catch (Exception)
            {
                return ExistingUnreadable();
            }
            inputs.Overwrote = true;
            return null;
        }

        // The constant-path halves of ValidateStatic: the .cs contract and
        // the code-path bound on a constant path, so a refused target fails
        // before a Plan exists. A non-string or absent path is left to the
        // type check and the path_writable requirement (SPEC §D3, §D6).
        private void CheckConstantPath(StaticContext ctx, List<Diagnostic> diagnostics)
        {
            if (!ctx.Parameters.TryGetValue(PathPort, out var input) || !input.IsPresent)
            {
                return;
            }
            var token = input.Value!.ToToken();
            if (token.Type != JTokenType.String)
            {
                // A non-string value is the type check's OW-TYPE-003 finding
                // (SPEC §D3), not the path check's.
                return;
            }
            var normalized = AssetPathPolicy.Normalize(
                (string?)((JValue)token).Value);
            if (normalized is null)
            {
                // The path_writable requirement's own OW-PERM-002 covers a
                // path with no normalized form.
                return;
            }
            diagnostics.AddRange(CheckScriptPath(normalized));
        }

        // ── Diagnostics ───────────────────────────────────────────────────

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        private static bool IsLowerHex(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        private static bool Member(
            JObject obj, string name, JTokenType type, out JToken? value)
        {
            value = obj[name];
            return value is not null && value.Type == type;
        }

        private static Diagnostic NotScriptFile() =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                $"Parameter '{PathPort}' must name a .cs file.",
                port: PathPort,
                hint: "unity.script.create writes C# source; the asset path " +
                    "must end in '.cs'.");

        private static Diagnostic OutsideCodePaths() =>
            ErrorCodes.Create(
                ErrorCodes.OW_PERM_002,
                $"The path of port '{PathPort}' is outside the configured code paths.",
                port: PathPort,
                hint: "Write only inside the codePaths setting (default " +
                    "Assets/Scripts/); a custom Operation folder must be " +
                    "added to codePaths first.");

        private static Diagnostic TargetFolderMissing() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target folder does not exist.",
                port: PathPort,
                hint: "Create the folder first, or choose a path inside a " +
                    "folder that exists.");

        private static Diagnostic TargetOccupied() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target file already exists and 'overwrite' is false.",
                port: PathPort,
                hint: "Set 'overwrite' to true to rewrite an existing file, " +
                    "or choose a free path.");

        private static Diagnostic ExistingUnreadable() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The existing file could not be read to record its content hash.",
                port: PathPort);

        private static Diagnostic ImportFailed() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The written file could not be imported as a script asset.",
                port: PathPort);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Writes an approved C# script file under a configured code path and imports it.")
            .Ja("スクリプト作成",
                "設定されたコードパスの内側に C# スクリプトを作成し、取り込みます。",
                "スクリプト", "コード", "生成")
            .Input(PathPort, "unity.AssetPath", required: true)
            .Input(ContentPort, "core.String", required: true,
                c => c.ConstantOnly())
            .Input(OverwritePort, "core.Bool", required: false,
                @default: JsonValue.From(new JValue(false)))
            .Output(ScriptPort, "unity.AssetRef<unity.MonoScript>")
            .Effects(EffectNames.CodeWrite, EffectNames.AssetCreate)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Reconcilable)
            // SPEC §G1 item 4: the allowed-paths and writability half of the
            // write path, judged with the settings snapshot the Run's
            // acceptance validated (§H4.1, decision D-R6). The codePaths
            // half is this Operation's own check.
            .Requires("path_writable", PathPort)
            .Build() with
            {
                DisplayName = "Create Script",
                Description =
                    "Writes arbitrary C# source — permission stage 3 of SPEC §H4 — " +
                    "to a '.cs' path inside the configured codePaths setting " +
                    "(default Assets/Scripts/) and imports it without compiling. " +
                    "The content port is ConstantOnly: the approval detail shows " +
                    "the full source and the approval is bound to the planHash " +
                    "covering it; an Agent code.write is never below ask, so the " +
                    "step always waits for approval. An existing file is refused " +
                    "(OW-UNITY-005) unless 'overwrite' is true; a path outside " +
                    "codePaths fails OW-PERM-002. The Operation is Reconcilable: " +
                    "after an interruption it completes only when the file's " +
                    "content hash matches the approved input and it is imported " +
                    "as a MonoScript (SPEC §F5).",
                Category = "Script",
                Tags = new[] { "script", "code" },
                Keywords = new[] { "script", "create", "code", "csharp", "generate" },
                Examples = new[]
                {
                    new OperationExample(
                        "Write a generated script under the code path and compile it",
                        ScriptOperationExamples.CreateScript),
                },
            };
    }
}
