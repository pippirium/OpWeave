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
    /// <c>unity.script.create_from_template</c> (SPEC §I.4, §G8 item 1 — the
    /// stage-2 code permission of §H4): writes a new C# script under
    /// <c>folder</c> from one of the three bundled templates —
    /// <c>mono_behaviour</c>, <c>scriptable_object</c> or
    /// <c>plain_class</c> — substituting only the class name and the
    /// namespace, then imports it. The step never compiles; the compile
    /// wait is <c>unity.script.compile</c>'s job (§G8 item 3). Immediate,
    /// Undo None, <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Paths and names.</b> <c>folder</c> is an asset path judged by
    /// <see cref="AssetPathPolicy"/> — a <c>..</c> or rooted spelling is
    /// OW-PERM-002, a read-only package OW-UNITY-010 — and declares the
    /// §G1 <c>path_writable</c> requirement, so the settings
    /// <c>allowedPaths</c> half is evaluated like every other write port.
    /// The file name is the class name plus <c>.cs</c>, and
    /// <c>className</c>/<c>namespace</c> are validated as usable C#
    /// identifiers by <see cref="ScriptIdentifiers"/> — OW-OP-006 when they
    /// are not, and when a class name repeats the selected template's base
    /// type (the generated class could not inherit itself). A target that
    /// already exists — file, folder or known asset — is never overwritten
    /// (OW-UNITY-005), and the destination folder must exist (OW-UNITY-005).
    /// </para>
    /// <para>
    /// <b>Reconciliation (§F5).</b> Preflight persists the §F5 evidence row:
    /// the normalized target path, whether anything occupied it, the
    /// template id and version, the inputs and the expected content hash of
    /// the rendered source — plus the Editor session id. <see cref="Reconcile"/>
    /// decides only from that evidence: nothing at the path is
    /// <see cref="ReconcileResult.NotApplied"/> (re-execution creates, it
    /// cannot duplicate); a file whose on-disk bytes hash to the expected
    /// content and that imports as a <see cref="MonoScript"/> is
    /// <see cref="ReconcileResult.Completed"/> with the outputs restored;
    /// everything else — a pre-existing artifact, modified or mismatched
    /// content, a file that is not imported as a script, unrestorable
    /// evidence — is <see cref="ReconcileResult.Unknown"/>. The artifact is
    /// a file, re-identifiable across an Editor restart by the permanent
    /// path/guid/content-hash keys (§F5 common rule 3), so the recorded
    /// session id never gates the verdict: it is evidence for the restart
    /// boundary, not a veto on provable states.
    /// </para>
    /// <para>
    /// The template text itself is substitutable by no port — the Operation
    /// reads the bundled files from its own package — and the templates
    /// carry no <c>[InitializeOnLoad]</c>, static constructor, Editor
    /// callback or <c>[OpWeaveOperation]</c>, so a generated script does
    /// nothing and registers nothing until a human uses it (§G8 item 1).
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class CreateScriptFromTemplateOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.script.create_from_template";

        internal const string TemplatePort = "template";
        internal const string ClassNamePort = "className";
        internal const string NamespacePort = "namespace";
        internal const string FolderPort = "folder";
        internal const string ScriptPort = "script";
        internal const string TypeNamePort = "typeName";

        private const string TemplateFileSuffix = ".cs";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        // The template-text source: template id → raw source, or null when
        // the bundled file cannot be read. The live binding is the package's
        // own Templates~ folder; the seam is the test's.
        private readonly Func<string, string?> _templateSource;

        /// <summary>Creates the Operation over the bundled template files.</summary>
        public CreateScriptFromTemplateOperation()
            : this(ScriptTemplates.ReadSource)
        {
        }

        /// <summary>
        /// The test seam over the bundled-template read — the
        /// <see cref="EditorStateProbe"/> pattern — so the unreadable-template
        /// failure is provable without corrupting the package.
        /// </summary>
        internal CreateScriptFromTemplateOperation(Func<string, string?> templateSource)
        {
            _templateSource =
                templateSource ?? throw new ArgumentNullException(nameof(templateSource));
        }

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
            CheckConstantIdentifier(ctx, ClassNamePort, isNamespace: false, diagnostics);
            CheckConstantIdentifier(ctx, NamespacePort, isNamespace: true, diagnostics);
            CheckConstantBaseType(ctx, diagnostics);
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
            if (ctx.IsPending(TemplatePort) || ctx.IsPending(ClassNamePort) ||
                ctx.IsPending(NamespacePort) || ctx.IsPending(FolderPort) ||
                !ctx.IsPresent(TemplatePort) || !ctx.IsPresent(ClassNamePort) ||
                !ctx.IsPresent(FolderPort))
            {
                return base.Preflight(ctx);
            }

            var inputs = ReadInputs(ctx);
            if (!inputs.Ok)
            {
                return new PreflightResult(
                    inputs.Diagnostics, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var plan = inputs.Value!;
            var physical = ToPhysicalPath(plan.TargetPath);
            if (physical is null || !AssetDatabase.IsValidFolder(plan.Folder))
            {
                return new PreflightResult(
                    new[] { FolderMissing() }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            if (Occupied(physical, plan.TargetPath))
            {
                return new PreflightResult(
                    new[] { TargetOccupied() }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            // SPEC §D0.3 item 3: the new script joins the domain governing
            // the destination folder — judged before any side effect.
            var guarded = RunDefinitionGuard.CheckPath(
                plan.TargetPath, GuardedPathRole.Destination);
            if (guarded is not null)
            {
                return new PreflightResult(
                    new[] { guarded }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var content = Render(plan);
            if (content is null)
            {
                return new PreflightResult(
                    new[] { TemplateUnreadable() },
                    Array.Empty<PlannedChange>(),
                    s_emptySnapshot);
            }

            // The §F5 evidence row for this Operation: target path and its
            // "nothing there" state, template id and version, the inputs and
            // the expected content hash — plus the session id as restart
            // evidence (it never gates a file artifact's verdict).
            var snapshot = new JObject
            {
                ["path"] = plan.TargetPath,
                ["pathExisted"] = false,
                ["template"] = plan.Template,
                ["templateVersion"] = ScriptTemplates.CurrentVersion,
                ["className"] = plan.ClassName,
                ["expectedContentHash"] = ScriptTemplates.ContentHash(content),
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            if (plan.Ns is not null)
            {
                snapshot["namespace"] = plan.Ns;
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
            var inputs = ReadInputs(ctx);
            if (!inputs.Ok)
            {
                return StepResult.Failed(inputs.Diagnostics[0]);
            }
            var plan = inputs.Value!;
            var physical = ToPhysicalPath(plan.TargetPath);
            if (physical is null || !AssetDatabase.IsValidFolder(plan.Folder))
            {
                return StepResult.Failed(FolderMissing());
            }
            if (Occupied(physical, plan.TargetPath))
            {
                return StepResult.Failed(TargetOccupied());
            }
            // SPEC §D0.3 item 4: re-checked here, on the main thread Run
            // acceptance serializes on, so a Run admitted since Preflight
            // still blocks the write — nothing is written when it refuses.
            var guarded = RunDefinitionGuard.CheckPath(
                plan.TargetPath, GuardedPathRole.Destination);
            if (guarded is not null)
            {
                return StepResult.Failed(guarded);
            }
            var content = Render(plan);
            if (content is null)
            {
                return StepResult.Failed(TemplateUnreadable());
            }

            File.WriteAllText(physical, content, ScriptTemplates.Encoding);
            AssetDatabase.ImportAsset(plan.TargetPath, ImportAssetOptions.ForceSynchronousImport);
            var script = AssetDatabase.LoadAssetAtPath<MonoScript>(plan.TargetPath);
            if (script == null)
            {
                // The file is on disk but not imported as a script — the
                // §F5 partial side effect; retrying is the import's business,
                // so OW-UNITY-008 is the §J code.
                return StepResult.Failed(ImportFailed());
            }
            // The asset was just imported and loaded above, so existence
            // is proven — this asks for the known asset's GUID, not
            // whether an asset exists, and either overload answers alike.
            var guid = AssetDatabase.AssetPathToGUID(plan.TargetPath);

            ctx.Changes.Created(
                new ChangeTarget("asset", plan.TargetPath, guid), undoable: false);
            ctx.SetOutput(ScriptPort, script);
            ctx.SetOutput(TypeNamePort, plan.TypeName);
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
                !Member(obj, "template", JTokenType.String, out var templateToken) ||
                !Member(obj, "templateVersion", JTokenType.Integer, out var versionToken) ||
                !Member(obj, "className", JTokenType.String, out var classToken) ||
                !Member(obj, "expectedContentHash", JTokenType.String, out var hashToken) ||
                !Member(obj, "editorSessionId", JTokenType.String, out _))
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            if (obj["namespace"] is { } nsMember && nsMember.Type != JTokenType.String)
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var path = (string)pathToken!;
            var pathExisted = (bool)existedToken!;
            var template = (string)templateToken!;
            var version = (long)versionToken!;
            var className = (string)classToken!;
            var expectedHash = (string)hashToken!;
            var ns = (string?)obj["namespace"];

            // §F5 rule 6: an expectation that cannot be re-established — an
            // unknown or re-versioned template, a malformed hash or unusable
            // inputs — can never prove a verdict.
            if (!ScriptTemplates.IsKnown(template) || version != ScriptTemplates.CurrentVersion)
            {
                return Unknown("The snapshot's template identity cannot be re-established.");
            }
            if (expectedHash.Length != 64 || !IsLowerHex(expectedHash))
            {
                return Unknown("The snapshot's expected content hash is malformed.");
            }
            if (!ScriptIdentifiers.IsIdentifier(className) ||
                (ns is not null && !ScriptIdentifiers.IsNamespace(ns)))
            {
                return Unknown("The snapshot's class name or namespace is not a usable C# identifier.");
            }

            var physical = ToPhysicalPath(path);
            // The existence question needs OnlyExistingAssets: the default
            // overload also answers for deleted assets whose path→GUID
            // mapping the database still remembers, which could never
            // prove the §F5 "nothing there" half of NotApplied (no file,
            // no .meta and no existing asset at the path).
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            var fileExists = physical is not null && File.Exists(physical);
            var dirExists = physical is not null && Directory.Exists(physical);
            var metaExists = physical is not null && File.Exists(physical + ".meta");
            if (!fileExists && !dirExists && !metaExists && guid.Length == 0)
            {
                // Nothing occupies the target: the step provably did not apply
                // and re-execution can only create, never duplicate.
                return new ReconcileResult.NotApplied();
            }
            if (pathExisted)
            {
                return Unknown(
                    "A pre-existing artifact occupies the target path; it cannot be attributed to the step.");
            }
            if (dirExists)
            {
                return Unknown("A directory occupies the target path.");
            }
            if (!fileExists)
            {
                return Unknown(metaExists
                    ? "A leftover .meta file without a file occupies the target path."
                    : "An asset database entry without a file occupies the target path.");
            }

            string actualHash;
            try
            {
                actualHash = ScriptTemplates.ContentHash(File.ReadAllBytes(physical!));
            }
            catch (Exception)
            {
                return Unknown("The artifact's content cannot be read.");
            }
            if (!string.Equals(actualHash, expectedHash, StringComparison.Ordinal))
            {
                return Unknown("The file's content does not match the expected template output.");
            }
            if (guid.Length == 0 ||
                AssetDatabase.LoadAssetAtPath<MonoScript>(path) == null)
            {
                // Written but not imported as a script: the §F5 partial side
                // effect, which automatic re-execution must not retry over.
                return Unknown("The file's content matches the expected output, but it is not imported as a script.");
            }

            var outputs = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                [ScriptPort] = JsonValue.From(new JObject
                {
                    ["kind"] = "asset",
                    ["guid"] = guid,
                    ["path"] = path,
                }),
                [TypeNamePort] = JsonValue.From(
                    new JValue(ScriptTemplates.TypeNameOf(className, ns))),
            };
            return new ReconcileResult.Completed(outputs);
        }

        // ── Input resolution ──────────────────────────────────────────────

        private sealed class StepInputs
        {
            internal string Template = string.Empty;
            internal string ClassName = string.Empty;
            internal string? Ns;
            internal string Folder = string.Empty;
            internal string TargetPath = string.Empty;
            internal string TypeName = string.Empty;
        }

        // The validated inputs of a Preflight/Execute call — every port is
        // re-checked at step time because a value may arrive through a data
        // edge the static phases could not see (SPEC §D3, §E5).
        private static Result<StepInputs> ReadInputs(StepContext ctx)
        {
            var diagnostics = new List<Diagnostic>();

            var template = ctx.Get<string>(TemplatePort);
            if (!ScriptTemplates.IsKnown(template))
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_006,
                    $"Parameter '{TemplatePort}' is not one of the bundled script templates.",
                    port: TemplatePort,
                    hint: "Bundled templates: " + string.Join(", ", ScriptTemplates.Ids) + "."));
            }

            var className = ctx.Get<string>(ClassNamePort);
            if (!ScriptIdentifiers.IsIdentifier(className))
            {
                diagnostics.Add(InvalidIdentifier(ClassNamePort));
            }
            else if (ScriptTemplates.BaseTypeNameOf(template) is { } baseType &&
                string.Equals(className, baseType, StringComparison.Ordinal))
            {
                diagnostics.Add(BaseTypeCollision(baseType));
            }

            string? ns = null;
            if (ctx.IsPresent(NamespacePort))
            {
                ns = ctx.Get<string>(NamespacePort);
                if (!ScriptIdentifiers.IsNamespace(ns))
                {
                    diagnostics.Add(InvalidIdentifier(NamespacePort));
                }
            }

            var folder = AssetPathPolicy.CheckWritePath(
                ctx.Get<string>(FolderPort), FolderPort);
            if (!folder.Ok)
            {
                diagnostics.AddRange(folder.Diagnostics);
            }

            if (diagnostics.Count != 0)
            {
                return Result<StepInputs>.Failure(diagnostics);
            }
            var normalized = folder.Value!;
            return Result<StepInputs>.Success(new StepInputs
            {
                Template = template,
                ClassName = className,
                Ns = ns,
                Folder = normalized,
                TargetPath = normalized + "/" + className + TemplateFileSuffix,
                TypeName = ScriptTemplates.TypeNameOf(className, ns),
            });
        }

        private static void CheckConstantIdentifier(
            StaticContext ctx, string port, bool isNamespace, List<Diagnostic> diagnostics)
        {
            if (!ctx.Parameters.TryGetValue(port, out var input) || !input.IsPresent)
            {
                return;
            }
            var token = input.Value!.ToToken();
            if (token.Type != JTokenType.String)
            {
                // A non-string value is the type check's OW-TYPE-003 finding
                // (SPEC §D3), not the identifier check's.
                return;
            }
            var text = (string?)((JValue)token).Value;
            var valid = isNamespace
                ? ScriptIdentifiers.IsNamespace(text)
                : ScriptIdentifiers.IsIdentifier(text);
            if (!valid)
            {
                diagnostics.Add(InvalidIdentifier(port));
            }
        }

        // The constant cross-port half of the className/base-type rule: when
        // both values arrive as constants the collision is already provable
        // at validation; an edge-fed value is refused at step time instead.
        private static void CheckConstantBaseType(
            StaticContext ctx, List<Diagnostic> diagnostics)
        {
            if (!ctx.Parameters.TryGetValue(TemplatePort, out var templateInput) ||
                !templateInput.IsPresent ||
                !ctx.Parameters.TryGetValue(ClassNamePort, out var classInput) ||
                !classInput.IsPresent)
            {
                return;
            }
            var templateToken = templateInput.Value!.ToToken();
            var classToken = classInput.Value!.ToToken();
            if (templateToken.Type != JTokenType.String ||
                classToken.Type != JTokenType.String)
            {
                // A non-string value is the type check's OW-TYPE-003 finding
                // (SPEC §D3), not the identifier check's.
                return;
            }
            var baseType = ScriptTemplates.BaseTypeNameOf(
                (string?)((JValue)templateToken).Value);
            if (baseType is not null &&
                string.Equals(
                    (string?)((JValue)classToken).Value, baseType,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(BaseTypeCollision(baseType));
            }
        }

        // The rendered script text, or null when the bundled template cannot
        // be read or its markers are malformed — a broken package, reported
        // as the §J import-side failure OW-UNITY-008.
        private string? Render(StepInputs plan)
        {
            var source = _templateSource(plan.Template);
            return source is null
                ? null
                : ScriptTemplates.Render(source, plan.ClassName, plan.Ns);
        }

        // ── Artifact state ────────────────────────────────────────────────

        // Anything occupying the target path: a file, a directory, a
        // leftover orphan .meta the refresh has not cleaned up, or an
        // asset the database still knows — the step never writes over an
        // existing path. OnlyExistingAssets asks about assets that exist;
        // the default overload also answers for deleted ones whose
        // mapping the database remembers and would call an empty path
        // occupied.
        private static bool Occupied(string physical, string assetPath) =>
            File.Exists(physical) ||
            Directory.Exists(physical) ||
            File.Exists(physical + ".meta") ||
            AssetDatabase.AssetPathToGUID(
                assetPath, AssetPathToGUIDOptions.OnlyExistingAssets).Length > 0;

        // The physical location of a project asset path — the same mapping
        // the requirement evaluator applies: Packages/ resolves through the
        // owning package (a local or embedded package may live outside the
        // project root), anything else resolves under the project root.
        private static string? ToPhysicalPath(string assetPath)
        {
            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
                var prefix = info?.assetPath is { } packageRoot
                    ? packageRoot + "/"
                    : null;
                if (prefix is null ||
                    !assetPath.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return null;
                }
                return Path.GetFullPath(Path.Combine(
                    info!.resolvedPath, assetPath.Substring(prefix.Length)));
            }
            var full = Path.GetFullPath(assetPath);
            var projectRoot =
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")) +
                Path.DirectorySeparatorChar;
            return full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }

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

        // ── Diagnostics ───────────────────────────────────────────────────

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        private static Diagnostic BaseTypeCollision(string baseType) =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                $"Parameter '{ClassNamePort}' must not name the template's base type '{baseType}'.",
                port: ClassNamePort,
                hint: "The generated class would inherit itself; choose another class name.");

        private static Diagnostic InvalidIdentifier(string port) =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                $"Parameter '{port}' value is not a usable C# identifier.",
                port: port,
                hint: "Use a letter-start ASCII identifier that is not a C# " +
                    "keyword; 'namespace' takes dot-separated identifiers.");

        private static Diagnostic FolderMissing() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target folder does not exist.",
                port: FolderPort,
                hint: "Create the folder first, or choose a folder that exists.");

        private static Diagnostic TargetOccupied() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target file already exists and is never overwritten.",
                port: FolderPort,
                hint: "Choose a class name or folder whose script path is free.");

        private static Diagnostic TemplateUnreadable() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The bundled script template could not be read.",
                hint: "The package's Templates~ folder is missing or unreadable.");

        private static Diagnostic ImportFailed() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The generated script could not be imported as a script asset.",
                port: FolderPort);

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Generates a C# script from a bundled template, substituting only class name and namespace.")
            .Ja("テンプレートからスクリプト作成",
                "同梱テンプレートから C# スクリプトを生成します。クラス名と名前空間だけを置き換えます。",
                "スクリプト", "テンプレート", "生成")
            .Input(TemplatePort, "core.String", required: true,
                c => c.Enum(
                    ScriptTemplates.MonoBehaviourId,
                    ScriptTemplates.ScriptableObjectId,
                    ScriptTemplates.PlainClassId))
            .Input(ClassNamePort, "core.String", required: true,
                c => c.Pattern(ScriptIdentifiers.IdentifierPattern))
            .Input(NamespacePort, "core.String", required: false,
                c => c.Pattern(ScriptIdentifiers.NamespacePattern))
            .Input(FolderPort, "unity.AssetPath", required: true)
            .Output(ScriptPort, "unity.AssetRef<unity.MonoScript>")
            .Output(TypeNamePort, "unity.TypeName")
            .Effects(EffectNames.CodeTemplate, EffectNames.AssetCreate)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Reconcilable)
            // SPEC §G1 item 4: the allowed-paths and writability half of the
            // write path, judged with the settings snapshot the Run's
            // acceptance validated (§H4.1, decision D-R6).
            .Requires("path_writable", FolderPort)
            .Build() with
            {
                DisplayName = "Create Script From Template",
                Description =
                    "Writes a new C# script into an existing project folder from one of " +
                    "the bundled templates — mono_behaviour, scriptable_object or " +
                    "plain_class — substituting only the class name and, when given, " +
                    "the dot-separated namespace, and imports it without compiling. " +
                    "The class name and namespace must be usable C# identifiers " +
                    "(OW-OP-006); the target file name is the class name plus '.cs'; " +
                    "an existing file is never overwritten and a missing folder " +
                    "fails (OW-UNITY-005). The Operation is Reconcilable: after an " +
                    "interruption it completes only when the file's content hash " +
                    "matches the expected template output and it is imported as a " +
                    "MonoScript (SPEC §F5).",
                Category = "Script",
                Tags = new[] { "script", "template" },
                Keywords = new[] { "script", "template", "generate", "class" },
                Examples = new[]
                {
                    new OperationExample(
                        "Generate a MonoBehaviour script from the bundled template",
                        ScriptOperationExamples.CreateFromTemplate),
                },
            };
    }
}
