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
using UnityEngine.Rendering;

namespace OpWeave.Operations
{
    /// <summary>
    /// <c>unity.asset.create_material</c> (SPEC §I.3, §G7 item 4): creates a
    /// <see cref="Material"/> asset at <c>path</c> with the named shader and
    /// the <c>properties</c> map — property name to float, Color, Vector4 or
    /// Texture (AssetRef) — applied to it. An existing path is never
    /// overwritten. Immediate, Undo None,
    /// <see cref="Idempotency.Reconcilable"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Paths.</b> <c>path</c> is a write path judged by
    /// <see cref="AssetPathPolicy"/> — a <c>..</c> or rooted spelling is
    /// OW-PERM-002, a read-only package OW-UNITY-010 — and declares the §G1
    /// <c>path_writable</c> requirement, so the settings
    /// <c>allowedPaths</c> half is evaluated like every other write port.
    /// The §I.3 <c>AssetPath(.mat)</c> note is read literally: the path
    /// must carry the <c>.mat</c> extension of the native serialized
    /// format — anything else is OW-UNITY-005, the §J code for an invalid
    /// path — and the parent folder must already exist (OW-UNITY-005).
    /// </para>
    /// <para>
    /// <b>Properties.</b> §G7 item 4 accepts exactly four value kinds,
    /// distinguished by JSON shape: a JSON number is a float
    /// (<c>SetFloat</c>), a <c>#RRGGBBAA</c> string or an
    /// <c>{r,g,b,a}</c> object is a Color (<c>SetColor</c>), an
    /// <c>{x,y,z,w}</c> object is a Vector4 (<c>SetVector</c>), and an
    /// object carrying a Ref <c>kind</c> is a §B5 AssetRef resolved to a
    /// <see cref="Texture"/> (<c>SetTexture</c>, the G2 resolver's own
    /// diagnostics). Any other shape is OW-OP-006, the parameter-constraint
    /// code the sibling Operations use for a value the port cannot take.
    /// Values are applied by name — but only names the shader declares
    /// with a compatible type, and only values that read back equal after
    /// the setter; an undeclared name, an incompatible declared type or a
    /// value that does not stick is OW-UNITY-007, the §J property-
    /// operation failure, and no asset is created. That proven
    /// application is exactly what the §F5 read-back the Reconcile half
    /// performs re-verifies.
    /// </para>
    /// <para>
    /// <b>Reconciliation (§F5).</b> Preflight persists the §F5 evidence row
    /// — the normalized target path, whether anything occupied it, the
    /// shader name and the declared properties — plus the Editor session id
    /// (evidence only; a file artifact re-identifies across an Editor
    /// restart by its permanent path/guid keys, §F5 common rule 3).
    /// <see cref="Reconcile"/> decides only from that evidence: nothing at
    /// the path is <see cref="ReconcileResult.NotApplied"/>; a Material
    /// whose shader resolves to the recorded one and whose declared
    /// properties read back equal is <see cref="ReconcileResult.Completed"/>
    /// with the AssetRef output restored; everything else — a pre-existing
    /// artifact, an asset of another type, a shader or property mismatch,
    /// unresolvable evidence — is <see cref="ReconcileResult.Unknown"/>.
    /// </para>
    /// </remarks>
    [OpWeaveOperation(OperationId, 1)]
    public sealed class CreateMaterialOperation : OperationBase
    {
        /// <summary>The Operation ID this instance registers under.</summary>
        public const string OperationId = "unity.asset.create_material";

        internal const string PathPort = "path";
        internal const string ShaderPort = "shader";
        internal const string PropertiesPort = "properties";
        internal const string MaterialPort = "material";

        /// <summary>The §I.3 <c>AssetPath(.mat)</c> extension.</summary>
        internal const string FileExtension = ".mat";

        private static readonly JsonValue s_emptySnapshot =
            JsonValue.From(new JObject());

        /// <inheritdoc/>
        public override OperationDescriptor Descriptor { get; } = BuildDescriptor();

        /// <inheritdoc/>
        public override PreflightResult Preflight(StepContext ctx)
        {
            if (ctx is null)
            {
                throw new ArgumentNullException(nameof(ctx));
            }
            // §D8 item 1: a pending port has no value to inspect in a Dry
            // Run, and a required port with no value yields no evidence to
            // record.
            if (ctx.IsPending(PathPort) || ctx.IsPending(ShaderPort) ||
                ctx.IsPending(PropertiesPort) ||
                !ctx.IsPresent(PathPort) || !ctx.IsPresent(ShaderPort))
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
            var diagnostic = CheckDestination(plan.Path);
            if (diagnostic is not null)
            {
                return new PreflightResult(
                    new[] { diagnostic }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }
            var shader = Shader.Find(plan.Shader);
            if (shader == null)
            {
                return new PreflightResult(
                    new[] { ShaderMissing() }, Array.Empty<PlannedChange>(), s_emptySnapshot);
            }

            // The application half of Dry Run truth: the same
            // declaration and read-back checks Execute performs, on an
            // in-memory probe material that is never persisted — a
            // property the shader does not declare, an incompatible type
            // or a value that does not stick fails here the way it fails
            // there (OW-UNITY-007).
            if (plan.Properties is not null)
            {
                var probe = new Material(shader);
                try
                {
                    var applied = ApplyProperties(probe, plan.Properties);
                    if (applied is not null)
                    {
                        return new PreflightResult(
                            new[] { applied },
                            Array.Empty<PlannedChange>(),
                            s_emptySnapshot);
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(probe);
                }
            }

            // The §F5 evidence row: the normalized path and its
            // "nothing there" state, the shader and the declared
            // properties — plus the session id as restart evidence.
            var snapshot = new JObject
            {
                ["path"] = plan.Path,
                ["pathExisted"] = false,
                ["shader"] = plan.Shader,
                ["editorSessionId"] = EditorSessionId.GetOrCreate(),
            };
            if (plan.Properties is not null)
            {
                snapshot["properties"] = plan.Properties;
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
            var diagnostic = CheckDestination(plan.Path);
            if (diagnostic is not null)
            {
                return StepResult.Failed(diagnostic);
            }
            var shader = Shader.Find(plan.Shader);
            if (shader == null)
            {
                return StepResult.Failed(ShaderMissing());
            }

            var material = new Material(shader);
            if (plan.Properties is not null)
            {
                var applied = ApplyProperties(material, plan.Properties);
                if (applied is not null)
                {
                    UnityEngine.Object.DestroyImmediate(material);
                    return StepResult.Failed(applied);
                }
            }

            AssetDatabase.CreateAsset(material, plan.Path);
            var saved = AssetDatabase.LoadAssetAtPath<Material>(plan.Path);
            if (saved == null)
            {
                // The create did not produce a Material at the path — the
                // §J import-side failure, matching the file-not-imported
                // reading the script Operation applies.
                return StepResult.Failed(NotCreated());
            }
            var guid = AssetDatabase.AssetPathToGUID(plan.Path);

            ctx.Changes.Created(
                new ChangeTarget("asset", plan.Path, guid), undoable: false);
            ctx.SetOutput(MaterialPort, saved);
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
                !Member(obj, "shader", JTokenType.String, out var shaderToken) ||
                !Member(obj, "editorSessionId", JTokenType.String, out _))
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var properties = obj["properties"];
            if (properties is not null && properties.Type != JTokenType.Object)
            {
                return Unknown("The Preflight snapshot is missing a required evidence member.");
            }
            var path = (string)pathToken!;
            var pathExisted = (bool)existedToken!;
            var shaderName = (string)shaderToken!;

            // §F5 rule 6: an expectation that cannot be re-established —
            // a shader the Editor no longer resolves — can never prove a
            // verdict.
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                return Unknown("The snapshot's shader can no longer be resolved.");
            }

            var physical = AssetArtifact.ToPhysicalPath(path);
            var guid = AssetDatabase.AssetPathToGUID(
                path, AssetPathToGUIDOptions.OnlyExistingAssets);
            var fileExists = physical is not null && File.Exists(physical);
            var dirExists = physical is not null && Directory.Exists(physical);
            var metaExists = physical is not null && File.Exists(physical + ".meta");
            if (!fileExists && !dirExists && !metaExists && guid.Length == 0)
            {
                // Nothing occupies the target: the step provably did not
                // apply and re-execution can only create, never duplicate.
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

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null || guid.Length == 0)
            {
                // A file at the path that imports as another asset type, or
                // is not imported at all: the §F5 partial side effect,
                // never a Completed and never safely re-runnable.
                return Unknown("The artifact at the target path is not a Material asset.");
            }
            if (material.shader != shader)
            {
                return Unknown("The Material's shader does not match the recorded input.");
            }
            if (properties is JObject declared &&
                !PropertiesMatch(material, declared, out var mismatch))
            {
                return Unknown(mismatch!);
            }

            var outputs = new Dictionary<string, JsonValue>(StringComparer.Ordinal)
            {
                [MaterialPort] = JsonValue.From(new JObject
                {
                    ["kind"] = "asset",
                    ["guid"] = guid,
                    ["path"] = path,
                }),
            };
            return new ReconcileResult.Completed(outputs);
        }

        // ── Inputs ───────────────────────────────────────────────────────

        private sealed class StepInputs
        {
            internal string Path = string.Empty;
            internal string Shader = string.Empty;
            internal JObject? Properties;
        }

        // The validated inputs of a Preflight/Execute call — every port is
        // re-checked at step time because a value may arrive through a data
        // edge the static phases could not see (SPEC §D3, §E5).
        private static Result<StepInputs> ReadInputs(StepContext ctx)
        {
            var diagnostics = new List<Diagnostic>();

            var path = AssetPathPolicy.CheckWritePath(
                ctx.Get<string>(PathPort), PathPort);
            if (!path.Ok)
            {
                diagnostics.AddRange(path.Diagnostics);
            }
            else if (!path.Value!.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(InvalidExtension());
            }

            var shader = ctx.Get<string>(ShaderPort) ?? string.Empty;
            if (shader.Length == 0)
            {
                diagnostics.Add(ShaderMissing());
            }

            JObject? properties = null;
            if (ctx.IsPresent(PropertiesPort))
            {
                if (ctx.Get<JsonValue>(PropertiesPort).ToToken() is JObject map)
                {
                    foreach (var entry in map.Properties())
                    {
                        if (Classify(entry.Value) == PropertyKind.Invalid)
                        {
                            diagnostics.Add(InvalidProperty());
                            break;
                        }
                    }
                    properties = map;
                }
                else
                {
                    diagnostics.Add(InvalidProperty());
                }
            }

            if (diagnostics.Count != 0)
            {
                return Result<StepInputs>.Failure(diagnostics);
            }
            return Result<StepInputs>.Success(new StepInputs
            {
                Path = path.Value!,
                Shader = shader,
                Properties = properties,
            });
        }

        // The destination checks of the create contract: the parent folder
        // must already exist and the path must be completely free — a file,
        // a folder, an orphan .meta or a known asset all count as occupied
        // (§F5's "nothing was there" is also what a later Reconcile asks).
        private static Diagnostic? CheckDestination(string path)
        {
            var parent = AssetArtifact.ParentFolder(path);
            var physical = AssetArtifact.ToPhysicalPath(path);
            if (physical is null || parent is null || !AssetDatabase.IsValidFolder(parent))
            {
                return FolderMissing();
            }
            return AssetArtifact.Occupied(physical, path) ? TargetOccupied() : null;
        }

        // ── Properties ───────────────────────────────────────────────────

        private enum PropertyKind
        {
            Invalid,
            Float,
            Color,
            Vector,
            Texture,
        }

        // The §G7 item 4 kind of one property value, decided by JSON shape:
        // a number is a float; a "#RRGGBBAA" string is a Color; an object
        // carrying a Ref 'kind' is a texture AssetRef; an {r,g,b,a} object
        // is a Color and an {x,y,z,w} object a Vector4. Anything else is
        // outside the port's accepted set.
        private static PropertyKind Classify(JToken value)
        {
            switch (value)
            {
                case JValue { Type: JTokenType.Integer or JTokenType.Float } number:
                    var scalar = (double)number;
                    return double.IsNaN(scalar) || double.IsInfinity(scalar)
                        ? PropertyKind.Invalid
                        : PropertyKind.Float;
                case JValue { Type: JTokenType.String } text:
                    return TryParseColor(text, out _) ? PropertyKind.Color : PropertyKind.Invalid;
                case JObject obj when obj["kind"] is JValue { Type: JTokenType.String }:
                    return PropertyKind.Texture;
                case JObject obj:
                    if (HasNumericMembers(obj, "r", "g", "b", "a"))
                    {
                        return PropertyKind.Color;
                    }
                    return HasNumericMembers(obj, "x", "y", "z", "w")
                        ? PropertyKind.Vector
                        : PropertyKind.Invalid;
                default:
                    return PropertyKind.Invalid;
            }
        }

        // Whether the object is exactly the named numeric members — no
        // missing, no extras, no non-numbers.
        private static bool HasNumericMembers(JObject obj, params string[] names)
        {
            if (obj.Count != names.Length)
            {
                return false;
            }
            foreach (var name in names)
            {
                if (obj[name] is not JValue { Type: JTokenType.Integer or JTokenType.Float })
                {
                    return false;
                }
            }
            return true;
        }

        private static bool TryParseColor(JToken token, out Color color)
        {
            color = default;
            return token is JValue { Type: JTokenType.String } text &&
                text.Value is string hex &&
                ColorUtility.TryParseHtmlString(hex, out color);
        }

        private static Color ColorOf(JObject obj) => new Color(
            (float)(double)obj["r"]!,
            (float)(double)obj["g"]!,
            (float)(double)obj["b"]!,
            (float)(double)obj["a"]!);

        private static Vector4 VectorOf(JObject obj) => new Vector4(
            (float)(double)obj["x"]!,
            (float)(double)obj["y"]!,
            (float)(double)obj["z"]!,
            (float)(double)obj["w"]!);

        // Whether the shader declares the named property with a type the
        // classified value can be applied to: a JSON number needs a Float
        // or Range, a Color needs Color, a Vector4 needs Vector and a
        // texture AssetRef needs Texture. A name the shader does not
        // declare is refused — the setter would leave the material
        // unchanged while the step reports success.
        private static bool DeclaredCompatible(
            Material material, string name, PropertyKind kind)
        {
            if (!material.HasProperty(name))
            {
                return false;
            }
            var index = material.shader.FindPropertyIndex(name);
            if (index < 0)
            {
                return false;
            }
            var declared = material.shader.GetPropertyType(index);
            switch (kind)
            {
                case PropertyKind.Float:
                    return declared == ShaderPropertyType.Float ||
                        declared == ShaderPropertyType.Range;
                case PropertyKind.Color:
                    return declared == ShaderPropertyType.Color;
                case PropertyKind.Vector:
                    return declared == ShaderPropertyType.Vector;
                case PropertyKind.Texture:
                    return declared == ShaderPropertyType.Texture;
                default:
                    return false;
            }
        }

        // Applies one validated properties map to the material; the first
        // entry that cannot be applied is the step's failure. Every name
        // must be a property the shader declares with a compatible type,
        // and the read-back must return the value that was set — a value
        // that does not stick is OW-UNITY-007, never a silent no-op the
        // step reports as success.
        private static Diagnostic? ApplyProperties(Material material, JObject properties)
        {
            var refs = new RefResolver();
            foreach (var entry in properties.Properties())
            {
                var value = entry.Value;
                var kind = Classify(value);
                if (kind == PropertyKind.Invalid)
                {
                    return InvalidProperty();
                }
                if (!DeclaredCompatible(material, entry.Name, kind))
                {
                    return PropertyNotApplied(entry.Name);
                }
                switch (kind)
                {
                    case PropertyKind.Float:
                    {
                        var expected = (float)(double)value;
                        material.SetFloat(entry.Name, expected);
                        if (material.GetFloat(entry.Name) != expected)
                        {
                            return PropertyNotApplied(entry.Name);
                        }
                        break;
                    }
                    case PropertyKind.Color:
                    {
                        var expected = value is JObject colorObj
                            ? ColorOf(colorObj)
                            : TryParseColor(value, out var parsed)
                                ? parsed
                                : default;
                        material.SetColor(entry.Name, expected);
                        var actual = material.GetColor(entry.Name);
                        if (actual.r != expected.r || actual.g != expected.g ||
                            actual.b != expected.b || actual.a != expected.a)
                        {
                            return PropertyNotApplied(entry.Name);
                        }
                        break;
                    }
                    case PropertyKind.Vector:
                    {
                        var expected = VectorOf((JObject)value);
                        material.SetVector(entry.Name, expected);
                        if (material.GetVector(entry.Name) != expected)
                        {
                            return PropertyNotApplied(entry.Name);
                        }
                        break;
                    }
                    case PropertyKind.Texture:
                    {
                        var resolved = refs.Resolve<Texture>(JsonValue.From(value));
                        if (!resolved.Ok)
                        {
                            return resolved.Diagnostics[0];
                        }
                        material.SetTexture(entry.Name, resolved.Value);
                        if (!ReferenceEquals(material.GetTexture(entry.Name), resolved.Value))
                        {
                            return PropertyNotApplied(entry.Name);
                        }
                        break;
                    }
                }
            }
            return null;
        }

        // The §F5 read-back: every declared property must read back the
        // value the input declared — the shader already matched, so a
        // mismatch means the artifact is not the step's output.
        private static bool PropertiesMatch(
            Material material, JObject properties, out string? mismatch)
        {
            var refs = new RefResolver();
            foreach (var entry in properties.Properties())
            {
                var value = entry.Value;
                switch (Classify(value))
                {
                    case PropertyKind.Float:
                        if (material.GetFloat(entry.Name) !=
                            (float)(double)value)
                        {
                            mismatch = $"The Material property '{entry.Name}' does not match the recorded input.";
                            return false;
                        }
                        break;
                    case PropertyKind.Color:
                    {
                        var expected = value is JObject colorObj
                            ? ColorOf(colorObj)
                            : TryParseColor(value, out var parsed)
                                ? parsed
                                : default;
                        var actual = material.GetColor(entry.Name);
                        if (actual.r != expected.r || actual.g != expected.g ||
                            actual.b != expected.b || actual.a != expected.a)
                        {
                            mismatch = $"The Material property '{entry.Name}' does not match the recorded input.";
                            return false;
                        }
                        break;
                    }
                    case PropertyKind.Vector:
                    {
                        var expected = VectorOf((JObject)value);
                        var actual = material.GetVector(entry.Name);
                        if (actual != expected)
                        {
                            mismatch = $"The Material property '{entry.Name}' does not match the recorded input.";
                            return false;
                        }
                        break;
                    }
                    case PropertyKind.Texture:
                    {
                        var resolved = refs.Resolve<Texture>(JsonValue.From(value));
                        if (!resolved.Ok)
                        {
                            mismatch = $"The recorded texture of property '{entry.Name}' can no longer be resolved.";
                            return false;
                        }
                        var actual = material.GetTexture(entry.Name);
                        if (!SamePersistentAsset(actual, resolved.Value))
                        {
                            mismatch = $"The Material property '{entry.Name}' does not match the recorded input.";
                            return false;
                        }
                        break;
                    }
                    default:
                        mismatch = "The snapshot's properties carry a value the port cannot take.";
                        return false;
                }
            }
            mismatch = null;
            return true;
        }

        // Persistent identity of two asset references across a session
        // boundary: guid and local file id, never the in-memory instance —
        // the texture could be re-imported between the step and the
        // Reconcile call (§F5 common rule 3).
        private static bool SamePersistentAsset(UnityEngine.Object? actual, Texture expected)
        {
            if (actual == null)
            {
                return false;
            }
            if (EditorUtility.IsPersistent(actual) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    actual, out var actualGuid, out var actualId) &&
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(
                    expected, out var expectedGuid, out var expectedId))
            {
                return actualGuid == expectedGuid && actualId == expectedId;
            }
            return ReferenceEquals(actual, expected) || actual == expected;
        }

        // ── Diagnostics ──────────────────────────────────────────────────

        private static bool Member(
            JObject obj, string name, JTokenType type, out JToken? value)
        {
            value = obj[name];
            return value is not null && value.Type == type;
        }

        private static ReconcileResult Unknown(string reason) =>
            new ReconcileResult.Unknown(reason);

        private static Diagnostic InvalidExtension() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                $"The asset path of port '{PathPort}' must carry the '{FileExtension}' extension.",
                port: PathPort,
                hint: "A Material is serialized in the native .mat format.");

        private static Diagnostic ShaderMissing() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_006,
                $"The shader named by port '{ShaderPort}' was not found.",
                port: ShaderPort,
                hint: "Use the name of a shader the project compiles, for example 'Standard'.");

        private static Diagnostic InvalidProperty() =>
            ErrorCodes.Create(
                ErrorCodes.OW_OP_006,
                $"Parameter '{PropertiesPort}' must map each property name to a float, " +
                    "a Color ('#RRGGBBAA' or {r,g,b,a}), a Vector4 ({x,y,z,w}) " +
                    "or a texture AssetRef.",
                port: PropertiesPort);

        private static Diagnostic FolderMissing() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target folder does not exist.",
                port: PathPort,
                hint: "Create the folder first, or choose a folder that exists.");

        private static Diagnostic TargetOccupied() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_005,
                "The target asset already exists and is never overwritten.",
                port: PathPort,
                hint: "Choose a path that is free.");

        private static Diagnostic NotCreated() =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_008,
                "The Material asset could not be created at the target path.",
                port: PathPort);

        private static Diagnostic PropertyNotApplied(string name) =>
            ErrorCodes.Create(
                ErrorCodes.OW_UNITY_007,
                $"The Material property '{name}' could not be applied.",
                port: PropertiesPort,
                hint: "The shader must declare the property with a type the value matches.");

        private static OperationDescriptor BuildDescriptor() => Op
            .Define(OperationId, 1)
            .Summary("Creates a Material asset with a shader and properties; an existing path is never overwritten.")
            .Ja("マテリアル作成",
                "シェーダーとプロパティを指定してマテリアルアセットを作成します。既存パスは上書きしません。",
                "マテリアル", "作成", "アセット")
            .Input(PathPort, "unity.AssetPath", required: true)
            .Input(ShaderPort, "core.String", required: true)
            .Input(PropertiesPort, "core.Json", required: false)
            .Output(MaterialPort, "unity.AssetRef<unity.Material>")
            .Effects(EffectNames.AssetCreate)
            .Undo(UndoSupport.None)
            .Idempotency(Idempotency.Reconcilable)
            // SPEC §G1 item 4 / §G7 item 1: the allowed-paths and
            // writability half of the asset path policy, judged with the
            // settings snapshot this Run's acceptance validated (§H4.1,
            // decision D-R6).
            .Requires("path_writable", PathPort)
            .Build() with
            {
                DisplayName = "Create Material",
                Description =
                    "Creates a Material asset at the given project path with the named " +
                    "shader and applies each entry of the properties map: a number is a " +
                    "float, a '#RRGGBBAA' string or an {r,g,b,a} object is a Color, an " +
                    "{x,y,z,w} object is a Vector4 and an AssetRef value is a Texture. " +
                    "The path must carry the .mat extension and its parent folder must " +
                    "exist; an existing path fails with OW-UNITY-005 and is never " +
                    "overwritten, a shader that cannot be found fails with OW-UNITY-006, " +
                    "a property the shader does not declare or a value that does not " +
                    "read back fails with OW-UNITY-007. " +
                    "The Operation is Reconcilable: after an interruption it completes " +
                    "only when the artifact at the path is a Material whose shader and " +
                    "declared properties match the recorded inputs (SPEC §F5).",
                Category = "Asset",
                Tags = new[] { "asset", "material" },
                Keywords = new[] { "material", "shader", "create" },
                Examples = new[]
                {
                    new OperationExample(
                        "Create a red material for later assignment",
                        AssetOperationExamples.CreateMaterial),
                },
            };
    }
}
