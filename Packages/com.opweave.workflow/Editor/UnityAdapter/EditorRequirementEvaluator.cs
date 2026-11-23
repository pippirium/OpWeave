#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Compiler;
using OpWeave.Core.Operations;
using OpWeave.Core.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Requirement evaluators of SPEC §G1 item 4 — <c>edit_mode</c>,
    /// <c>not_compiling</c>, <c>scene_loaded(port)</c>,
    /// <c>path_writable(port)</c> and <c>type_exists(port)</c> — checked at
    /// run time inside the Core precondition step (SPEC §E4 item 2). Standard
    /// and custom Operations declare the same <see cref="Requirement"/>
    /// records, so this single evaluator judges both identically.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each call answers a <see cref="RequirementResult"/>:
    /// <see cref="RequirementVerdict.Satisfied"/>,
    /// <see cref="RequirementVerdict.Wait"/> — not yet satisfied but resolved
    /// by waiting, carrying no error code and no
    /// <see cref="Diagnostic"/> — or <see cref="RequirementVerdict.Violated"/>
    /// with the failure <see cref="Diagnostic"/>. Only <c>not_compiling</c>
    /// reports Wait: while the Editor compiles the requirement is simply not
    /// yet satisfied and becomes satisfied once compilation finishes (the
    /// 2026-11-06 owner decision, SPEC §1.4, §E4 step 2 and §G1 item 4);
    /// OW-RUN-001 means only "not in Edit Mode" and stays with
    /// <c>edit_mode</c>. The failure codes are the SPEC §G1/§J set:
    /// OW-RUN-001 for <c>edit_mode</c>; OW-UNITY-003
    /// for <c>scene_loaded</c>; OW-PERM-002 for <c>path_writable</c> outside
    /// the settings <c>allowedPaths</c> (checked first) and OW-UNITY-010 when
    /// the path is inside them but read-only — an immutable package
    /// (registry/Git/built-in/tarball) or a destination that is not
    /// effectively writable on disk: a read-only or access-denied file, an
    /// existing directory under a file destination — or, under a folder
    /// destination, one that rejects a real create/delete probe file — or
    /// a path whose nearest existing ancestor directory rejects the same
    /// probe (SPEC §G1 item 4 "writable and outside
    /// immutable packages", §G7 item
    /// 1); OW-UNITY-006 for <c>type_exists</c>, the same verdict SPEC §D6
    /// item 4 emits statically and mandates re-checking at run time after an
    /// OW-VAL-015 warning. An unknown kind fails closed as an invalid
    /// Descriptor (OW-OP-003) rather than being silently satisfied.
    /// </para>
    /// <para>
    /// <b>Port values.</b> Port-bound kinds read the port's resolved
    /// <see cref="JsonValue"/>: a SceneRef object for <c>scene_loaded</c>
    /// (resolved in the §G2 order guid → path → <c>GetSceneByPath</c>), a
    /// path string for <c>path_writable</c>, a type-name string for
    /// <c>type_exists</c>. A requirement whose port carries no value —
    /// unsupplied or explicit JSON <c>null</c> — is satisfied: the
    /// Descriptor's optional-port default takes over (e.g.
    /// <c>unity.gameobject.create</c> falling back to the active scene).
    /// A value that cannot name the checked object — a Ref without
    /// guid/path, a non-string path or type name — cannot establish the
    /// requirement, so it fails with that kind's violation code.
    /// </para>
    /// <para>
    /// <b>Evaluation order.</b> <see cref="EvaluateAll"/> checks the
    /// Descriptor's Requirements in declared order: the first
    /// <see cref="RequirementVerdict.Violated"/> result reports its
    /// <see cref="Diagnostic"/>, matching how Descriptors declare
    /// <c>edit_mode</c> ahead of port-bound kinds. A
    /// <see cref="RequirementVerdict.Wait"/> — reported only by
    /// <c>not_compiling</c> — short-circuits the loop (SPEC §E4 step 2,
    /// the 2026-11-06 amendment): while the Editor compiles the Tick's
    /// processing for the step ends with no diagnostic and the step is
    /// retried from E4 step 1 on the next Tick, so requirements declared
    /// after <c>not_compiling</c> are not evaluated (a still-compiling
    /// type would otherwise draw a spurious OW-UNITY-006). A violation
    /// found before the Wait still reports first, in declared order;
    /// when every requirement holds the result is Satisfied. Inside
    /// <c>path_writable</c> the allowed-paths check precedes the
    /// read-only check (OW-PERM-002 before OW-UNITY-010).
    /// </para>
    /// <para>
    /// <b>Path containment.</b> The normalization and prefix rules mirror the
    /// D6 phase's static half (Core's PermissionValidationPhase): segments
    /// joined with <c>/</c>, empty and <c>.</c> segments collapsed, rooted /
    /// absolute / <c>..</c> spellings rejected outright, allowed prefixes
    /// matched at segment boundaries and case-insensitively like Unity asset
    /// paths. The two assemblies share no internals, so the bounded helpers
    /// are duplicated here rather than widening Core's surface. The §H4 item 5
    /// settings-file test is the deliberate exception: both layers ask Core's
    /// internal <c>SettingsPathGuard</c>, so a Windows spelling of the settings
    /// file that one layer recognizes cannot be missed by another.
    /// </para>
    /// <para>
    /// <b>Test seam.</b> Every live Unity dependency is injected as a
    /// delegate (the <see cref="EditorStateProbe"/> pattern): the public
    /// constructor binds the real APIs and the internal one lets tests drive
    /// each Editor state. The allowed-path set is likewise a delegate:
    /// the list constructors freeze it at construction while the provider
    /// constructors resolve it per check — the form the runner host uses so
    /// each accepted Run is judged by the settings its acceptance validated.
    /// </para>
    /// </remarks>
    public sealed class EditorRequirementEvaluator
    {
        /// <summary>The SPEC §G1 item 4 requirement kinds.</summary>
        internal const string EditModeRequirement = "edit_mode";
        internal const string NotCompilingRequirement = "not_compiling";
        internal const string SceneLoadedRequirement = "scene_loaded";
        internal const string PathWritableRequirement = "path_writable";
        internal const string TypeExistsRequirement = "type_exists";

        private readonly IEditorState _editorState;
        private readonly IStaticTypeProbe _typeProbe;
        private readonly Func<IReadOnlyList<string>> _allowedPaths;
        private readonly Func<string, string> _guidToAssetPath;
        private readonly Func<string, bool> _isSceneLoadedAtPath;
        private readonly Func<string, bool> _isInImmutablePackage;
        private readonly Func<string, bool, bool> _isReadOnlyOnDisk;

        /// <summary>
        /// The live evaluator over the real Editor state, scenes, package
        /// manager and file system, checking <c>path_writable</c> against the
        /// settings <paramref name="allowedPaths"/> (SPEC §H4.1; the loaded
        /// <see cref="OpWeaveSettings.AllowedPaths"/>), frozen at
        /// construction — the runner host instead uses the provider form so
        /// each accepted Run is judged by the settings snapshot its own
        /// acceptance validated.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="allowedPaths"/> is null.</exception>
        public EditorRequirementEvaluator(IReadOnlyList<string> allowedPaths)
            : this(
                new EditorStateProbe(),
                new UnityTypeProbe(),
                Snapshot(allowedPaths),
                static guid => AssetDatabase.GUIDToAssetPath(guid),
                static path => SceneManager.GetSceneByPath(path).isLoaded,
                IsInImmutablePackage,
                IsReadOnlyOnDisk)
        {
        }

        /// <summary>
        /// The live evaluator whose <c>path_writable</c> allowed set is
        /// resolved through <paramref name="allowedPaths"/> on every check —
        /// the runner host binds it to the settings snapshot validated at
        /// the active Run's acceptance (SPEC §H4.1), so a settings file
        /// tightened or repaired after startup applies to the Run admitted
        /// under it and an invalid file can never be silently replaced by a
        /// default.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="allowedPaths"/> is null.</exception>
        internal EditorRequirementEvaluator(Func<IReadOnlyList<string>> allowedPaths)
            : this(
                new EditorStateProbe(),
                new UnityTypeProbe(),
                allowedPaths,
                static guid => AssetDatabase.GUIDToAssetPath(guid),
                static path => SceneManager.GetSceneByPath(path).isLoaded,
                IsInImmutablePackage,
                IsReadOnlyOnDisk)
        {
        }

        /// <summary>
        /// The evaluator over caller-supplied probes — the test seam.
        /// <paramref name="guidToAssetPath"/> answers
        /// <see cref="AssetDatabase.GUIDToAssetPath"/>'s shape ("" when the
        /// guid resolves to no asset); <paramref name="isSceneLoadedAtPath"/>
        /// answers whether a scene at the path is loaded;
        /// <paramref name="isInImmutablePackage"/> and
        /// <paramref name="isReadOnlyOnDisk"/> answer the two read-only
        /// halves of <c>path_writable</c> for an already-normalized path —
        /// the latter reporting whether the destination is effectively
        /// non-writable on disk, told by its second argument whether the
        /// port's declared destination is the folder itself or a file path.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal EditorRequirementEvaluator(
            IEditorState editorState,
            IStaticTypeProbe typeProbe,
            IReadOnlyList<string> allowedPaths,
            Func<string, string> guidToAssetPath,
            Func<string, bool> isSceneLoadedAtPath,
            Func<string, bool> isInImmutablePackage,
            Func<string, bool, bool> isReadOnlyOnDisk)
            : this(
                editorState,
                typeProbe,
                Snapshot(allowedPaths),
                guidToAssetPath,
                isSceneLoadedAtPath,
                isInImmutablePackage,
                isReadOnlyOnDisk)
        {
        }

        /// <summary>
        /// The evaluator over caller-supplied probes whose allowed set is
        /// resolved through <paramref name="allowedPaths"/> on every check —
        /// the provider form of the test seam; the runner host uses it so
        /// the settings snapshot each acceptance validated reaches the
        /// <c>path_writable</c> check of the Run it admitted.
        /// </summary>
        /// <exception cref="ArgumentNullException">Any argument is null.</exception>
        internal EditorRequirementEvaluator(
            IEditorState editorState,
            IStaticTypeProbe typeProbe,
            Func<IReadOnlyList<string>> allowedPaths,
            Func<string, string> guidToAssetPath,
            Func<string, bool> isSceneLoadedAtPath,
            Func<string, bool> isInImmutablePackage,
            Func<string, bool, bool> isReadOnlyOnDisk)
        {
            _editorState = editorState ?? throw new ArgumentNullException(nameof(editorState));
            _typeProbe = typeProbe ?? throw new ArgumentNullException(nameof(typeProbe));
            _guidToAssetPath =
                guidToAssetPath ?? throw new ArgumentNullException(nameof(guidToAssetPath));
            _isSceneLoadedAtPath =
                isSceneLoadedAtPath ?? throw new ArgumentNullException(nameof(isSceneLoadedAtPath));
            _isInImmutablePackage =
                isInImmutablePackage ?? throw new ArgumentNullException(nameof(isInImmutablePackage));
            _isReadOnlyOnDisk =
                isReadOnlyOnDisk ?? throw new ArgumentNullException(nameof(isReadOnlyOnDisk));
            _allowedPaths = allowedPaths ?? throw new ArgumentNullException(nameof(allowedPaths));
        }

        // Freezes a fixed allowed-path set into the provider shape — the
        // list-taking constructors' semantics: the set cannot change after
        // construction.
        private static Func<IReadOnlyList<string>> Snapshot(IReadOnlyList<string> allowedPaths)
        {
            var copy = ReadOnlyCollections.CopyList(
                allowedPaths ?? throw new ArgumentNullException(nameof(allowedPaths)));
            return () => copy;
        }

        // The allowed prefixes of the Run currently evaluating, resolved
        // through the injected provider on every check — the runner host
        // answers the settings snapshot validated at that Run's acceptance.
        // An allowed prefix that fails normalization is skipped rather than
        // degrading to a match-all empty prefix (fail closed), the D6
        // phase's own reading.
        private IReadOnlyList<string> AllowedPrefixes()
        {
            var allowed = _allowedPaths();
            var prefixes = new List<string>(allowed.Count);
            foreach (var prefix in allowed)
            {
                var normalized = NormalizePathPrefix(prefix);
                if (normalized is not null)
                {
                    prefixes.Add(normalized);
                }
            }
            return prefixes;
        }

        /// <summary>
        /// Evaluates one requirement. <paramref name="portValue"/> is the
        /// resolved <see cref="JsonValue"/> of the port
        /// <see cref="Requirement.Port"/> names — null when the kind binds no
        /// port or the port carries no value. Returns the
        /// <see cref="RequirementResult"/>: Satisfied, Wait (only
        /// <c>not_compiling</c> while the Editor compiles — no error code, no
        /// diagnostic, per the 2026-11-06 owner decision), or Violated with
        /// the failure <see cref="Diagnostic"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="requirement"/> is null.</exception>
        public RequirementResult Evaluate(
            Requirement requirement, JsonValue? portValue, string? nodeId = null) =>
            Evaluate(requirement, portValue, nodeId, operationId: null);

        /// <summary>
        /// The operation-aware form of
        /// <see cref="Evaluate(Requirement, JsonValue?, string?)"/>:
        /// <paramref name="operationId"/> names the Operation the requirement
        /// was declared by — the <see cref="Requirement"/> record itself
        /// carries only the port name — so a port-bound kind can read its
        /// declared destination (T-W2.7-05). A null id is the anonymous
        /// reading: every destination counts as a file destination.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="requirement"/> is null.</exception>
        internal RequirementResult Evaluate(
            Requirement requirement, JsonValue? portValue, string? nodeId, string? operationId)
        {
            if (requirement is null)
            {
                throw new ArgumentNullException(nameof(requirement));
            }
            switch (requirement.Kind)
            {
                case EditModeRequirement:
                    return _editorState.IsEditMode
                        ? RequirementResult.Satisfied
                        : RequirementResult.Violated(ErrorCodes.Create(
                            ErrorCodes.OW_RUN_001,
                            "The Editor is not in Edit Mode.",
                            nodeId: nodeId));
                case NotCompilingRequirement:
                    // SPEC §1.4 / §E4 step 2 / §G1 item 4 (the 2026-11-06
                    // owner decision): while the Editor compiles the
                    // requirement is not yet satisfied — a Wait with no
                    // error code and no Diagnostic; after compilation it is
                    // satisfied. OW-RUN-001 reports only "not in Edit Mode".
                    return _editorState.IsCompiling
                        ? RequirementResult.Wait
                        : RequirementResult.Satisfied;
                case SceneLoadedRequirement:
                    return ToResult(CheckSceneLoaded(requirement.Port, portValue, nodeId));
                case PathWritableRequirement:
                    return ToResult(CheckPathWritable(
                        requirement.Port, portValue, nodeId, operationId));
                case TypeExistsRequirement:
                    return ToResult(CheckTypeExists(requirement.Port, portValue, nodeId));
                default:
                    // A kind this evaluator does not know cannot be declared
                    // satisfied; registration should have rejected it, so the
                    // failure is the invalid-Descriptor code (fail closed).
                    return RequirementResult.Violated(ErrorCodes.Create(
                        ErrorCodes.OW_OP_003,
                        $"The requirement kind '{requirement.Kind}' is unknown.",
                        nodeId: nodeId,
                        port: requirement.Port));
            }
        }

        private static RequirementResult ToResult(Diagnostic? diagnostic) =>
            diagnostic is null
                ? RequirementResult.Satisfied
                : RequirementResult.Violated(diagnostic);

        /// <summary>
        /// The SPEC §E4 item 2 shape: evaluates a step's requirements in
        /// declared order and reports the first
        /// <see cref="RequirementVerdict.Violated"/> result with its
        /// <see cref="Diagnostic"/>. A
        /// <see cref="RequirementVerdict.Wait"/> — only
        /// <c>not_compiling</c> produces one — returns immediately: SPEC
        /// §E4 step 2 (the 2026-11-06 amendment) ends the Tick's
        /// processing for the step with no diagnostic and retries it from
        /// step 1 on the next Tick, so requirements declared after
        /// <c>not_compiling</c> are not evaluated while the Editor
        /// compiles. Returns Satisfied when every requirement holds.
        /// <paramref name="portValue"/> resolves a port name to the
        /// step's resolved input <see cref="JsonValue"/> (null = absent).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="requirements"/> or <paramref name="portValue"/> is null.
        /// </exception>
        public RequirementResult EvaluateAll(
            IReadOnlyList<Requirement> requirements,
            Func<string, JsonValue?> portValue,
            string? nodeId = null) =>
            EvaluateAll(requirements, portValue, nodeId, operationId: null);

        /// <summary>
        /// The operation-aware form of
        /// <see cref="EvaluateAll(IReadOnlyList{Requirement}, Func{string, JsonValue?}, string?)"/>:
        /// every declared requirement is evaluated with
        /// <paramref name="operationId"/>, the id of the Operation the list
        /// was declared by.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="requirements"/> or <paramref name="portValue"/> is null.
        /// </exception>
        internal RequirementResult EvaluateAll(
            IReadOnlyList<Requirement> requirements,
            Func<string, JsonValue?> portValue,
            string? nodeId, string? operationId)
        {
            if (requirements is null)
            {
                throw new ArgumentNullException(nameof(requirements));
            }
            if (portValue is null)
            {
                throw new ArgumentNullException(nameof(portValue));
            }
            for (var i = 0; i < requirements.Count; i++)
            {
                var requirement = requirements[i];
                var value = requirement.Port is null ? null : portValue(requirement.Port);
                var result = Evaluate(requirement, value, nodeId, operationId);
                if (result.Verdict != RequirementVerdict.Satisfied)
                {
                    // A violation reports its diagnostic in declared
                    // order; a Wait — only not_compiling produces one —
                    // short-circuits per SPEC §E4 step 2 (the 2026-11-06
                    // amendment): while the Editor compiles the Tick's
                    // processing for the step ends with no diagnostic and
                    // the step retries from step 1 on the next Tick, so
                    // requirements declared later are not evaluated.
                    return result;
                }
            }
            return RequirementResult.Satisfied;
        }

        // SPEC §G1 item 4: scene_loaded(port) — the §G2 SceneRef candidate
        // order: guid resolves to an asset path first; the path key only when
        // the guid resolves to nothing. A string value is taken as the scene
        // path directly (the nested 'scene' key of a GameObjectRef may be a
        // bare path string). No identifiable path, or a path whose scene is
        // not loaded, is OW-UNITY-003.
        private Diagnostic? CheckSceneLoaded(string? port, JsonValue? portValue, string? nodeId)
        {
            var token = portValue?.ToToken();
            if (token is null || token.Type == JTokenType.Null)
            {
                return null;
            }
            string? scenePath = null;
            if (token is JValue { Type: JTokenType.String } text)
            {
                scenePath = (string?)text.Value;
            }
            else if (token is JObject sceneRef)
            {
                var guid = StringMember(sceneRef, "guid");
                if (guid is not null)
                {
                    var resolved = _guidToAssetPath(guid);
                    if (!string.IsNullOrEmpty(resolved))
                    {
                        scenePath = resolved;
                    }
                }
                scenePath ??= StringMember(sceneRef, "path");
            }
            if (scenePath is null || !_isSceneLoadedAtPath(scenePath))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_003,
                    $"The scene referenced by port '{port ?? "<none>"}' is not loaded.",
                    nodeId: nodeId,
                    port: port);
            }
            return null;
        }

        // SPEC §G1 item 4 + §G7 item 1: path_writable(port) — outside the
        // allowed paths is OW-PERM-002 (checked first), then inside them but
        // read-only — an immutable package or a non-writable file or
        // directory — is OW-UNITY-010. What "writable" means for an existing
        // directory destination depends on the kind of destination the port
        // declares: a folder destination is writable when files can be
        // created in the directory, a file destination is never writable
        // where a directory stands. The Requirement record carries only the
        // port name, so the declaring Operation's id — carried by the
        // operation-aware evaluation form — selects the reading; an
        // evaluation that cannot name its Operation keeps the
        // file-destination reading (T-W2.7-05).
        private Diagnostic? CheckPathWritable(
            string? port, JsonValue? portValue, string? nodeId, string? operationId)
        {
            var token = portValue?.ToToken();
            if (token is null || token.Type == JTokenType.Null)
            {
                return null;
            }
            var path = token is JValue { Type: JTokenType.String } text
                ? (string?)text.Value
                : null;
            var normalized = path is null ? null : NormalizePath(path);
            // SPEC §H4 item 5: the OpWeave settings file is never part of the
            // permitted write paths — a widened allowedPaths cannot admit it.
            // Judged before the containment half so a Windows spelling that
            // names the settings file without sitting inside an allowed prefix
            // ("ProjectSettings./…") is refused on its own; the shared Core
            // guard also covers the trailing dot/space, NTFS stream and 8.3
            // short-name spellings of the same file. The code stays OW-PERM-002
            // — the same refusal an outside path earns.
            if (normalized is not null &&
                SettingsPathGuard.IsProtectedSettingsPath(normalized))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_PERM_002,
                    $"The path of port '{port ?? "<none>"}' is outside the allowed paths.",
                    nodeId: nodeId,
                    port: port,
                    hint: "The OpWeave settings file is never writable by Operations; only the human Project Settings screen saves it.");
            }
            var allowed = AllowedPrefixes();
            if (normalized is null || !IsInsideAllowedPaths(normalized, allowed))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_PERM_002,
                    $"The path of port '{port ?? "<none>"}' is outside the allowed paths.",
                    nodeId: nodeId,
                    port: port,
                    hint: allowed.Count == 0
                        ? "No path is allowed; configure allowedPaths in the project settings."
                        : "Allowed paths: " + string.Join(", ", allowed) + ".");
            }
            if (_isInImmutablePackage(normalized) ||
                _isReadOnlyOnDisk(normalized, IsFolderDestination(operationId, port)))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_010,
                    $"The path of port '{port ?? "<none>"}' is read-only (an immutable package or a non-writable file or directory).",
                    nodeId: nodeId,
                    port: port);
            }
            return null;
        }

        // The standard path_writable ports whose declared destination is the
        // folder itself, keyed by Operation id (SPEC §I): create_folder's
        // 'path', whose "existing folder is a success" row needs the
        // folder-destination reading (§G7 item 3, §I.3 "既存なら成功", §F5
        // Idempotent), and create_from_template's 'folder', the folder its
        // contract requires the generated script to land in (§I.4). Every
        // other path_writable port names a file destination, so a directory
        // standing at its path keeps the OW-UNITY-010 refusal the ticket
        // scopes to (T-W2.7-05 "Apply this only where the requirement's
        // destination is a folder"). Both ports declare the same
        // 'unity.AssetPath' type and the contract exposes no destination
        // marker, so the (id, port) pair is the discriminator; a custom
        // Operation's folder destination keeps the file-destination reading.
        private static readonly IReadOnlyDictionary<string, string> s_folderDestinations =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unity.asset.create_folder"] = "path",
                ["unity.script.create_from_template"] = "folder",
            };

        private static bool IsFolderDestination(string? operationId, string? port) =>
            operationId is not null && port is not null &&
            s_folderDestinations.TryGetValue(operationId, out var folderPort) &&
            string.Equals(folderPort, port, StringComparison.Ordinal);

        // SPEC §G1 item 4 + §D6 item 4: type_exists(port) — the runtime
        // re-check behind the OW-VAL-015 warning emits the same OW-UNITY-006
        // the static check reports.
        private Diagnostic? CheckTypeExists(string? port, JsonValue? portValue, string? nodeId)
        {
            var token = portValue?.ToToken();
            if (token is null || token.Type == JTokenType.Null)
            {
                return null;
            }
            var typeName = token is JValue { Type: JTokenType.String } text
                ? (string?)text.Value
                : null;
            if (typeName is null || typeName.Length == 0 || !_typeProbe.TypeExists(typeName))
            {
                return ErrorCodes.Create(
                    ErrorCodes.OW_UNITY_006,
                    typeName is null || typeName.Length == 0
                        ? $"The type required by port '{port ?? "<none>"}' was not found."
                        : $"The type '{typeName}' required by port '{port ?? "<none>"}' was not found.",
                    nodeId: nodeId,
                    port: port,
                    hint: "A type defined by code generated in the same run becomes available only after it compiles and the domain reloads.");
            }
            return null;
        }

        private static string? StringMember(JObject obj, string name) =>
            obj[name] is JValue { Type: JTokenType.String } value
                ? (string?)value.Value
                : null;

        // ── Live bindings ─────────────────────────────────────────────────

        // The immutable-package half of "read-only" (SPEC §G1 item 4, §G7
        // item 1): only embedded and local (file:) packages are writable
        // project sources; registry, Git, built-in and tarball packages —
        // and any source the package manager cannot classify — are immutable.
        private static bool IsInImmutablePackage(string assetPath)
        {
            var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assetPath);
            return info is not null &&
                info.source != UnityEditor.PackageManager.PackageSource.Embedded &&
                info.source != UnityEditor.PackageManager.PackageSource.Local;
        }

        // The "writable" half: the destination must be effectively writable
        // by the current user, not merely lack the read-only flag. An
        // existing file is not writable when it is marked read-only or
        // cannot be opened for writing (an ACL deny, for example). An
        // existing directory answers by the destination's declared kind
        // (folderDestination — the (Operation, port) pairs of
        // s_folderDestinations): a folder destination is writable when the
        // current user can create files in it — the same create/delete
        // probe a missing path applies to its nearest ancestor, and the
        // reading create_folder's "existing folder is a success" needs
        // (SPEC §G7 item 3, §I.3 "既存なら成功"; T-W2.7-05) — while a file
        // destination is never writable where a directory stands, the
        // OW-UNITY-010 refusal every file-writing path_writable user kept
        // on main. A missing destination is not writable when a regular
        // file blocks an ancestor component, or when no ancestor directory
        // exists; otherwise the nearest existing ancestor directory is
        // probed by actually creating — and immediately deleting — one
        // uniquely named hidden file in it (ACL APIs are unreliable on the
        // Editor's Mono runtime). Anything the probe cannot determine fails
        // closed — the path counts as not writable (OW-UNITY-010).
        private static bool IsReadOnlyOnDisk(string assetPath, bool folderDestination)
        {
            var physical = ToPhysicalPath(assetPath);
            if (physical is null)
            {
                // The physical destination cannot be determined (for
                // example a Packages/ path whose package the package
                // manager does not know): fail closed — not writable.
                return true;
            }
            if (File.Exists(physical))
            {
                return !CanWriteToExistingFile(physical);
            }
            if (Directory.Exists(physical))
            {
                // A directory standing where a file must be written is not
                // a writable file destination; a folder destination is
                // writable when the current user can create files in it.
                return folderDestination ? !CanCreateFilesIn(physical) : true;
            }
            var ancestor = NearestExistingAncestorDirectory(physical);
            return ancestor is null || !CanCreateFilesIn(ancestor);
        }

        // An existing file is writable only when it is not marked
        // read-only and the current user can actually open it for writing;
        // the stream is opened and immediately disposed — never truncated
        // or written.
        private static bool CanWriteToExistingFile(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                {
                    return false;
                }
                using (new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                {
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The nearest existing ancestor directory of a missing path, or
        // null when none exists or an ancestor component is a regular file
        // (the destination then can never be created).
        private static string? NearestExistingAncestorDirectory(string path)
        {
            var dir = Path.GetDirectoryName(path);
            while (dir is not null && dir.Length > 0)
            {
                if (Directory.Exists(dir))
                {
                    return dir;
                }
                if (File.Exists(dir))
                {
                    return null;
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        // The effective create-files right on an existing directory, probed
        // by creating a uniquely named hidden file in it: the '.' prefix
        // keeps Unity's asset importer from seeing it (Unity ignores
        // dot-files) and FileOptions.DeleteOnClose makes Dispose remove it.
        // The probe therefore briefly creates and deletes one hidden file
        // in the directory. A denied creation (UnauthorizedAccessException
        // or IOException) — or any result the probe cannot determine —
        // counts as not writable. When the FileStream still leaves the file
        // behind (a runtime where DeleteOnClose is unsupported), the
        // finally block deletes it so no residue remains.
        private static bool CanCreateFilesIn(string directory)
        {
            var probe = Path.Combine(
                directory,
                ".opweave-write-probe-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (new FileStream(
                    probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(probe))
                    {
                        File.Delete(probe);
                    }
                }
                catch (Exception)
                {
                    // Best-effort residue cleanup only.
                }
            }
        }

        // Maps a project-relative asset path to its physical location:
        // Packages/<name>/... resolves through the owning package's
        // resolvedPath (a local package may live outside the project root);
        // other paths resolve under the project root — never outside it.
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
                return Path.GetFullPath(
                    Path.Combine(info!.resolvedPath, assetPath.Substring(prefix.Length)));
            }
            var full = Path.GetFullPath(assetPath);
            var projectRoot =
                Path.GetFullPath(Path.Combine(Application.dataPath, "..")) +
                Path.DirectorySeparatorChar;
            return full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? full
                : null;
        }

        // ── Path containment (mirrors the D6 phase's reading, §G7 item 1) ──

        // SPEC §G1 item 4's closed-prefix containment: a normalized path is
        // inside the allowed set only when it begins with one of the
        // normalized allowed roots, each already closed with '/' — the root
        // folder itself does not count, and a sibling that merely shares a
        // leading string ("Assets/AllowedBroad/" under "Assets/Allowed/")
        // does not either. Shared with the resolved-path write bound
        // AssetPathPolicy applies to the Ref-targeted Operations' move
        // source / delete target, which no declared path_writable port
        // covers — one helper keeps the two readings of the same clause
        // from drifting (OpWeave.Operations sees it through
        // InternalsVisibleTo).
        internal static bool IsInsideAllowedPaths(string normalized, IReadOnlyList<string> allowed)
        {
            foreach (var prefix in allowed)
            {
                if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static string? NormalizePathPrefix(string prefix)
        {
            var normalized = NormalizePath(prefix);
            if (normalized is null)
            {
                return null;
            }
            return normalized.Length == 0 || normalized.EndsWith("/", StringComparison.Ordinal)
                ? normalized
                : normalized + "/";
        }

        // '/'-separated, '.'- and empty-segment-collapsed path, or null when
        // a '..' segment escapes the root — always outside the allowed paths.
        // Rooted and absolute spellings reject before collapsing: a leading
        // '/' or '\', a Windows drive prefix, and a '~' home prefix would
        // otherwise lose their first segment and pass the prefix check as a
        // relative path.
        private static string? NormalizePath(string path)
        {
            if (path.Length > 0 &&
                (path[0] == '/' || path[0] == '\\' || path[0] == '~' ||
                 (path.Length > 1 && char.IsLetter(path[0]) && path[1] == ':')))
            {
                return null;
            }
            var segments = path.Replace('\\', '/').Split('/');
            var kept = new List<string>(segments.Length);
            foreach (var segment in segments)
            {
                if (segment == "..")
                {
                    return null;
                }
                if (segment.Length == 0 || segment == ".")
                {
                    continue;
                }
                kept.Add(segment);
            }
            return kept.Count == 0 ? string.Empty : string.Join("/", kept);
        }
    }
}
