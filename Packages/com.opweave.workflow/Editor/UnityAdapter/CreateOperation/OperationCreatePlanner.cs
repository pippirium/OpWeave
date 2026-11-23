#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using OpWeave.Core;
using OpWeave.Core.Operations;
using UnityEditor;
using UnityEditor.Compilation;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The live validation and file-layout planning of the SPEC §M.4
    /// "Assets > Create > OpWeave > Operation" dialog: from a
    /// <see cref="OperationCreateRequest"/> it derives the class name,
    /// namespace, file paths and test placement and reports every input
    /// problem as an <see cref="OperationCreateIssue"/> — the codified
    /// ones carrying their SPEC §J code (OW-UNITY-012).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live-validation list of SPEC §M.4 item 2 is applied here:
    /// class/file name collisions and the port, effect and summary
    /// checks. The dialog takes no Operation ID input — the ID is
    /// generated once at commit (SPEC §A4), so the grammar /
    /// reserved-namespace (OW-OP-007) / duplicate checks on the generated
    /// value run in <see cref="OperationIdGenerator.Resolve"/>, not here.
    /// Type ID candidates come from the published TypeRegistry —
    /// <see cref="RegistryPublication.Shared"/> — directly, never through
    /// the Agent API.
    /// </para>
    /// <para>
    /// A target folder whose assembly is not provably Editor-only
    /// (<c>CompilationPipeline.GetAssemblyNameFromScriptPath</c>, with the
    /// same path conventions when it reports nothing, then the
    /// <c>AssemblyFlags.EditorAssembly</c> / Player-compilation /
    /// declared-<c>includePlatforms</c> evidence) yields OW-UNITY-012 and
    /// the SPEC §M.4 item 3 choices on
    /// <see cref="OperationCreatePlan.FolderChoices"/> — "save to default
    /// folder" always, "create OpWeave.Custom.asmdef here" only where the
    /// folder does not already carry an .asmdef. An Editor-only assembly
    /// that cannot see the template's OpWeave.Core + OpWeave.UnityAdapter
    /// references yields the same refusal: a generated class there could
    /// never compile.
    /// </para>
    /// <para>
    /// Local, reversible choices where SPEC leaves detail open: the class
    /// name is the English display name in PascalCase plus an
    /// "Operation" suffix (the SPEC §M.4 example's shape), falling back to
    /// the ID's tail segments when no display name is given; the namespace
    /// is the covering asmdef's <c>rootNamespace</c> plus the PascalCased
    /// folder path below it (the folder's own name when the file lands in
    /// the asmdef's root), or <c>OpWeave.Custom</c> for the predefined
    /// editor assembly; the generated test file goes to the first existing
    /// Assets-side test asmdef that references OpWeave.Testing — preferring
    /// one under the target folder — or a newly created
    /// <c>&lt;target&gt;/Tests/</c> test asmdef; the generated file name
    /// equals the class name; input ports are generated
    /// <c>required: true</c> (the §M.4 example's form) and Undo stays
    /// <see cref="UndoSupport.None"/>.
    /// </para>
    /// </remarks>
    internal static class OperationCreatePlanner
    {
        private const string DefaultFolderFallback = "Assets/OpWeave/Editor/Operations";
        private const string CustomAsmdefFileName = "OpWeave.Custom.asmdef";
        private const string CustomTestsSuffix = ".Tests";
        private const string TestsFolderName = "Tests";

        /// <summary>
        /// The default save destination of SPEC §M.4 item 3: the first
        /// <c>customOperationFolders</c> entry of the loaded settings
        /// (defaults when the file cannot be read), else the §M.2 folder.
        /// </summary>
        internal static string DefaultFolder()
        {
            var loaded = OpWeaveSettingsLoader.Load();
            var settings = loaded.Ok ? loaded.Value! : OpWeaveSettings.Default;
            var folders = settings.CustomOperationFolders;
            return folders.Count > 0 ? NormalizeFolder(folders[0]) : DefaultFolderFallback;
        }

        /// <summary>
        /// Computes the plan for <paramref name="request"/> — every live
        /// validation finding and the derived file layout.
        /// </summary>
        internal static OperationCreatePlan Plan(OperationCreateRequest request)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            var plan = new OperationCreatePlan
            {
                Request = request,
                TargetFolder = NormalizeFolder(request.TargetFolder),
            };
            PlanInto(plan, request);
            return plan;
        }

        /// <summary>
        /// The plan after taking one SPEC §M.4 item 3 OW-UNITY-012 choice:
        /// <see cref="OperationCreateFolderChoice.UseDefaultFolder"/>
        /// re-plans against <see cref="DefaultFolder"/> and
        /// <see cref="OperationCreateFolderChoice.CreateCustomAsmdef"/>
        /// schedules the §M.2 asmdef (and a test asmdef when needed) inside
        /// the chosen folder.
        /// </summary>
        internal static OperationCreatePlan PlanForChoice(
            OperationCreateRequest request, OperationCreateFolderChoice choice)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (choice == OperationCreateFolderChoice.UseDefaultFolder)
            {
                request.TargetFolder = DefaultFolder();
                return Plan(request);
            }

            var plan = Plan(request);
            if (!plan.HasCode(ErrorCodes.OW_UNITY_012))
            {
                return plan;
            }
            plan.Issues.RemoveAll(
                issue => issue.Code == ErrorCodes.OW_UNITY_012);
            plan.FolderChoices.Clear();

            // A second .asmdef inside a folder that already carries one is
            // not a valid layout; only a folder whose coverage comes from
            // an ancestor (or nothing) can take the §M.2 asmdef.
            if (FolderHasAsmdef(plan.TargetFolder))
            {
                plan.Issues.Add(OperationCreateIssue.Error(
                    "The target folder '" + plan.TargetFolder +
                    "' already contains an assembly definition file."));
                return plan;
            }

            var asmdefName = UniqueAsmdefName("OpWeave.Custom");
            plan.OperationAsmdefName = asmdefName;
            plan.OperationAsmdefPath =
                plan.TargetFolder + "/" + CustomAsmdefFileName;
            plan.TargetAssemblyName = asmdefName;
            // The new asmdef's rootNamespace equals its assembly name; the
            // file lands in the asmdef's root so the folder name is appended.
            plan.NamespaceName = asmdefName + "." +
                PascalIdentifier(FolderName(plan.TargetFolder));
            if (request.CreateTests)
            {
                // The §M.2 layout next to the new asmdef — a brand-new
                // custom folder has no test assembly of its own yet.
                var testName = UniqueAsmdefName(asmdefName + CustomTestsSuffix);
                plan.TestAsmdefName = testName;
                plan.TestAsmdefPath = plan.TargetFolder + "/" + TestsFolderName +
                    "/" + testName + ".asmdef";
                plan.TestNamespace = testName;
                plan.TestFilePath = plan.TargetFolder + "/" + TestsFolderName +
                    "/" + plan.TestClassName + ".cs";
            }
            CheckCollisions(plan, request);
            return plan;
        }

        private static void PlanInto(OperationCreatePlan plan, OperationCreateRequest request)
        {
            // ── Operation ID: none at plan time ──────────────────────────
            // SPEC §M.4 item 2: the dialog takes no ID input and the ID is
            // generated once at commit. Redraws, previews and this live
            // validation never mint one — the commit-time grammar /
            // reserved-namespace / duplicate checks live in
            // OperationIdGenerator.Resolve.
            if (request.Version < 1)
            {
                plan.Issues.Add(OperationCreateIssue.Error(
                    "Operation version must be a positive integer."));
            }

            // ── Derived class name ──────────────────────────────────────
            plan.ClassName = DeriveClassName(request.DisplayName);
            if (!IsIdentifier(plan.ClassName))
            {
                plan.Issues.Add(OperationCreateIssue.Error(
                    "The derived class name '" + plan.ClassName + "' is not a valid C# identifier."));
            }
            plan.FilePath = plan.TargetFolder + "/" + plan.ClassName + ".cs";
            plan.TestClassName = plan.ClassName + "Tests";

            // ── Target folder: Editor-only assembly? (OW-UNITY-012) ─────
            plan.TargetAssemblyName = AssemblyNameForScriptPath(plan.FilePath);
            if (!IsEditorOnlyAssembly(plan.TargetAssemblyName))
            {
                plan.Issues.Add(OperationCreateIssue.Error(
                    "The target folder '" + plan.TargetFolder +
                    "' does not belong to an Editor-only assembly.",
                    ErrorCodes.OW_UNITY_012));
                AddFolderChoices(plan);
            }
            else if (!AssemblyCanHostTemplate(plan.TargetAssemblyName!))
            {
                // The generated class uses OpWeave.Core ([OpWeaveOperation],
                // Op.Define, OperationDescriptor) and OpWeave.UnityAdapter
                // (OperationBase); an existing Editor-only assembly that does
                // not reference both cannot compile it — the same
                // OW-UNITY-012 refusal, resolvable through the default
                // folder (a new §M.2 asmdef carries both references).
                plan.Issues.Add(OperationCreateIssue.Error(
                    "The target assembly '" + plan.TargetAssemblyName +
                    "' does not reference OpWeave.Core and " +
                    "OpWeave.UnityAdapter, so the generated Operation " +
                    "would not compile.",
                    ErrorCodes.OW_UNITY_012));
                AddFolderChoices(plan);
            }
            plan.NamespaceName = DeriveNamespace(plan.TargetFolder, plan.TargetAssemblyName);

            if (request.CreateTests)
            {
                PlanTestPlacement(plan);
            }
            CheckCollisions(plan, request);
            CheckPorts(plan, request);
            CheckEffects(plan, request);
            if (request.Summary.Trim().Length > OperationDescriptorValidator.SummaryMaxLength)
            {
                plan.Issues.Add(OperationCreateIssue.Error(
                    "The English summary exceeds " +
                    OperationDescriptorValidator.SummaryMaxLength + " characters."));
            }
        }

        // ── Collisions (files and generated type names) ──────────────────
        private static void CheckCollisions(
            OperationCreatePlan plan, OperationCreateRequest request)
        {
            if (FileExists(plan.FilePath))
            {
                plan.Issues.Add(OperationCreateIssue.Error(
                    "The file '" + plan.FilePath + "' already exists."));
            }
            var fullName = plan.NamespaceName + "." + plan.ClassName;
            foreach (var type in TypeCache.GetTypesDerivedFrom<IOperation>())
            {
                if (string.Equals(type.FullName, fullName, StringComparison.Ordinal))
                {
                    plan.Issues.Add(OperationCreateIssue.Error(
                        "An Operation class '" + fullName + "' already exists."));
                    break;
                }
            }
            if (plan.TestFilePath is not null && FileExists(plan.TestFilePath))
            {
                plan.Issues.Add(OperationCreateIssue.Error(
                    "The file '" + plan.TestFilePath + "' already exists."));
            }
            if (plan.OperationAsmdefPath is not null && FileExists(plan.OperationAsmdefPath))
            {
                plan.Issues.Add(OperationCreateIssue.Error(
                    "The file '" + plan.OperationAsmdefPath + "' already exists."));
            }
        }

        // ── Test file placement ─────────────────────────────────────────
        // The generated test only needs OpWeave.Testing plus the test
        // framework — it addresses the operation by ID string — so any
        // Assets-side test asmdef that references OpWeave.Testing, is gated
        // on UNITY_INCLUDE_TESTS and can actually compile the generated
        // file (NUnit access, engine references) can host it. A candidate
        // under the target folder wins (the §M.2 Tests/ layout); otherwise
        // the ordinally first candidate. With no candidate a new test
        // asmdef is scheduled under "<target>/Tests/".
        private static void PlanTestPlacement(OperationCreatePlan plan)
        {
            var candidate = FindTestAsmdef(plan.TargetFolder);
            string testFolder;
            if (candidate is not null)
            {
                testFolder = Path.GetDirectoryName(candidate.Value.Path)!
                    .Replace('\\', '/');
                plan.TestNamespace = candidate.Value.RootNamespace;
            }
            else
            {
                testFolder = plan.TargetFolder + "/" + TestsFolderName;
                var testName = UniqueAsmdefName("OpWeave.Custom.Tests");
                plan.TestAsmdefName = testName;
                plan.TestAsmdefPath = testFolder + "/" + testName + ".asmdef";
                plan.TestNamespace = testName;
            }
            plan.TestFilePath = testFolder + "/" + plan.TestClassName + ".cs";
        }

        private static void CheckPorts(OperationCreatePlan plan, OperationCreateRequest request)
        {
            var snapshot = RegistryPublication.Shared.Snapshot;
            var seenInputs = new HashSet<string>(StringComparer.Ordinal);
            var seenOutputs = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < request.Ports.Count; i++)
            {
                var port = request.Ports[i];
                var name = port.Name.Trim();
                var typeId = port.TypeId.Trim();
                var kind = port.IsOutput ? "output" : "input";
                var seen = port.IsOutput ? seenOutputs : seenInputs;
                if (!IdGrammar.IsValidScopedId(name))
                {
                    plan.Issues.Add(OperationCreateIssue.Error(
                        "The " + kind + " port name '" + name +
                        "' must match the SPEC A4 scoped-id grammar."));
                }
                else if (!seen.Add(name))
                {
                    plan.Issues.Add(OperationCreateIssue.Error(
                        "Duplicate " + kind + " port name '" + name + "'."));
                }
                if (typeId.Length == 0)
                {
                    plan.Issues.Add(OperationCreateIssue.Error(
                        "The " + kind + " port '" + name + "' has no Type ID."));
                    continue;
                }
                var parsed = TypeRef.Parse(typeId);
                if (!parsed.Ok)
                {
                    plan.Issues.Add(OperationCreateIssue.Error(
                        "The " + kind + " port '" + name +
                        "' has a malformed Type ID '" + typeId + "'."));
                    continue;
                }
                if (snapshot is null)
                {
                    plan.Issues.Add(OperationCreateIssue.Warning(
                        "The TypeRegistry snapshot is not published yet; port '" +
                        name + "' cannot be checked against registered types."));
                }
                else if (snapshot.Types.Resolve(parsed.Value!) is { Ok: false })
                {
                    plan.Issues.Add(OperationCreateIssue.Error(
                        "The " + kind + " port '" + name +
                        "' references unregistered type '" + typeId + "'."));
                }
            }
        }

        private static void CheckEffects(OperationCreatePlan plan, OperationCreateRequest request)
        {
            for (var i = 0; i < request.Effects.Count; i++)
            {
                var effect = request.Effects[i];
                if (!EffectNames.IsKnown(effect))
                {
                    plan.Issues.Add(OperationCreateIssue.Error(
                        "The side effect '" + effect +
                        "' is outside the closed SPEC C4 vocabulary."));
                }
            }
        }

        // ── Derived names ────────────────────────────────────────────────
        // The SPEC §M.4 example's shape: "Reset Transform" →
        // ResetTransformOperation. The Operation ID is a generated UUID
        // (SPEC §A4) and carries no readable segments, so an empty display
        // name falls back to the plain "Operation" base name; the existing
        // class/file collision check still applies.
        internal static string DeriveClassName(string displayName)
        {
            var baseName = PascalIdentifier(displayName);
            if (baseName.Length == 0)
            {
                baseName = "Operation";
            }
            return baseName.EndsWith("Operation", StringComparison.Ordinal)
                ? baseName
                : baseName + "Operation";
        }

        // rootNamespace + PascalCased relative folder path; the folder's own
        // name when the file lands in the asmdef's root (the fixture's
        // OpWeave.Custom.Transform shape). Predefined editor assembly →
        // OpWeave.Custom.
        internal static string DeriveNamespace(string folder, string? assemblyName)
        {
            if (assemblyName is not null)
            {
                var asmdefPath = AsmdefPathOf(assemblyName);
                if (!string.IsNullOrEmpty(asmdefPath))
                {
                    var root = AsmdefMember(asmdefPath, "rootNamespace") ?? assemblyName;
                    var dir = Path.GetDirectoryName(asmdefPath)!.Replace('\\', '/');
                    var relative = RelativeUnder(folder, dir);
                    if (relative.Length == 0)
                    {
                        relative = FolderName(folder);
                    }
                    var suffix = PascalIdentifier(relative.Replace('/', ' '));
                    return suffix.Length == 0 ? root : root + "." + suffix;
                }
            }
            return "OpWeave.Custom";
        }

        /// <summary>True when <paramref name="text"/> is a plain C# identifier.</summary>
        internal static bool IsIdentifier(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            if (!(char.IsLetter(text[0]) || text[0] == '_'))
            {
                return false;
            }
            for (var i = 1; i < text.Length; i++)
            {
                if (!(char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    return false;
                }
            }
            return true;
        }

        // ── Compilation lookups ─────────────────────────────────────────
        // The assemblies the generated Operation template needs to see:
        // OpWeave.Core ([OpWeaveOperation], Op.Define, OperationDescriptor)
        // and OpWeave.UnityAdapter (OperationBase).
        private static readonly string[] s_templateReferences =
            { "OpWeave.Core", "OpWeave.UnityAdapter" };

        // Top-level Assets folders Unity never compiles scripts from.
        private static readonly string[] s_nonCompilingTopFolders =
            { "Editor Default Resources", "Gizmos", "StreamingAssets", "WebGLTemplates" };

        private static string? AssemblyNameForScriptPath(string assetPath)
        {
            try
            {
                var name =
                    CompilationPipeline.GetAssemblyNameFromScriptPath(assetPath);
                if (!string.IsNullOrEmpty(name))
                {
                    return NormalizeAssemblyName(name!);
                }
            }
            catch (Exception)
            {
            }
            // The pipeline can leave the name unset for a path that does not
            // exist on disk yet; the same conventions decide it directly.
            return DeriveAssemblyNameFromPath(assetPath);
        }

        // The Unity assignment rules applied to the path itself: the
        // deepest ancestor carrying an .asmdef owns the script; with none,
        // any ancestor named "Editor" selects the predefined editor
        // assembly. Paths Unity never compiles (non-Assets roots, hidden or
        // ~ folders, the top-level non-script folders) yield null. The
        // predefined-name result is verdict-equivalent for the legacy
        // first-pass folders, not name-exact.
        private static string? DeriveAssemblyNameFromPath(string assetPath)
        {
            var path = assetPath.Replace('\\', '/');
            const string assetsPrefix = "Assets/";
            if (!path.StartsWith(assetsPrefix, StringComparison.Ordinal))
            {
                return null;
            }
            var segments = path.Substring(assetsPrefix.Length).Split('/');
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                if (segment.StartsWith(".", StringComparison.Ordinal) ||
                    segment.EndsWith("~", StringComparison.Ordinal) ||
                    (i == 0 && Array.IndexOf(
                        s_nonCompilingTopFolders, segment) >= 0))
                {
                    return null;
                }
            }
            for (var depth = segments.Length - 1; depth >= 0; depth--)
            {
                var dir = depth == 0
                    ? "Assets"
                    : assetsPrefix + string.Join("/", segments, 0, depth);
                string[] asmdefs;
                try
                {
                    asmdefs = Directory.GetFiles(ToAbsolute(dir), "*.asmdef");
                }
                catch (Exception)
                {
                    continue;
                }
                if (asmdefs.Length == 0)
                {
                    continue;
                }
                Array.Sort(asmdefs, StringComparer.Ordinal);
                var relative = (dir + "/" + Path.GetFileName(asmdefs[0]))
                    .Replace('\\', '/');
                return AsmdefMember(relative, "name") ??
                    Path.GetFileNameWithoutExtension(asmdefs[0]);
            }
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (segments[i] == "Editor")
                {
                    return "Assembly-CSharp-Editor";
                }
            }
            return "Assembly-CSharp";
        }

        // Unity 6000.0 reports assembly names with a trailing ".dll" —
        // GetAssemblyNameFromScriptPath, Assembly.name and the compiled
        // assemblyReferences entries alike — while diagnostics, name
        // comparisons and the asmdef lookups below want the declared
        // name. Strip exactly one terminal extension.
        private static string NormalizeAssemblyName(string name) =>
            name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - ".dll".Length)
                : name;

        private static bool IsEditorOnlyAssembly(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            var normalized = NormalizeAssemblyName(name!);
            try
            {
                var matched = false;
                foreach (var assembly in CompilationPipeline.GetAssemblies(
                    AssembliesType.Editor))
                {
                    if (!string.Equals(
                        NormalizeAssemblyName(assembly.name),
                        normalized, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    matched = true;
                    if ((assembly.flags & AssemblyFlags.EditorAssembly) != 0)
                    {
                        return true;
                    }
                }
                if (matched)
                {
                    // The flag is not reported for every Editor-only
                    // compilation product: an assembly the Player
                    // compilation does not contain is Editor-only.
                    foreach (var assembly in CompilationPipeline.GetAssemblies(
                        AssembliesType.Player))
                    {
                        if (string.Equals(
                            NormalizeAssemblyName(assembly.name),
                            normalized, StringComparison.Ordinal))
                        {
                            return false;
                        }
                    }
                    return true;
                }
            }
            catch (Exception)
            {
                // A lookup failure can never prove Editor-only: stays false.
                return false;
            }
            // Not in the current compilation set — a predefined editor
            // assembly with no sources yet, or an asmdef not yet compiled:
            // the declaration decides.
            return IsPredefinedEditorAssembly(normalized) ||
                (AsmdefPathOf(normalized) is { } asmdefPath &&
                    AsmdefIsEditorOnly(asmdefPath));
        }

        // The predefined editor products: scripts under an Editor folder
        // never enter a player build — Editor-only by construction, with or
        // without a compilation entry or the flag.
        private static bool IsPredefinedEditorAssembly(string name) =>
            string.Equals(name, "Assembly-CSharp-Editor", StringComparison.Ordinal) ||
            string.Equals(name, "Assembly-CSharp-Editor-firstpass", StringComparison.Ordinal);

        private static bool IsPredefinedAssemblyName(string name) =>
            IsPredefinedEditorAssembly(name) ||
            string.Equals(name, "Assembly-CSharp", StringComparison.Ordinal) ||
            string.Equals(name, "Assembly-CSharp-firstpass", StringComparison.Ordinal);

        // True when <paramref name="name"/> can see every assembly the
        // generated templates use — the compiled assembly's referenced
        // assembly names, the .asmdef's declared references (GUID entries
        // resolved), or — for the predefined assemblies — the autoReferenced
        // flag of the required asmdefs themselves.
        private static bool AssemblyCanHostTemplate(string name)
        {
            var normalized = NormalizeAssemblyName(name);
            var required = new List<string>(s_templateReferences);
            required.Remove(normalized);
            if (required.Count == 0)
            {
                return true;
            }
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            var compilation = FindCompilationAssembly(normalized);
            if (compilation?.assemblyReferences is { } compiledReferences)
            {
                foreach (var entry in compiledReferences)
                {
                    if (!string.IsNullOrEmpty(entry?.name))
                    {
                        referenced.Add(NormalizeAssemblyName(entry!.name));
                    }
                }
            }
            if (AsmdefPathOf(normalized) is { } asmdefPath)
            {
                foreach (var entry in AsmdefReferenceNames(asmdefPath))
                {
                    referenced.Add(entry);
                }
            }
            foreach (var req in required)
            {
                if (referenced.Contains(req))
                {
                    continue;
                }
                if (IsPredefinedAssemblyName(normalized) &&
                    AsmdefIsAutoReferenced(req))
                {
                    continue;
                }
                return false;
            }
            return true;
        }

        private static UnityEditor.Compilation.Assembly? FindCompilationAssembly(
            string name)
        {
            var normalized = NormalizeAssemblyName(name);
            try
            {
                foreach (var assembly in CompilationPipeline.GetAssemblies(
                    AssembliesType.Editor))
                {
                    if (string.Equals(
                        NormalizeAssemblyName(assembly.name),
                        normalized, StringComparison.Ordinal))
                    {
                        return assembly;
                    }
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // The OW-UNITY-012 choices: "create OpWeave.Custom.asmdef here"
        // only where no .asmdef already sits in the folder (a same-folder
        // second asmdef is not a valid layout); "save to default folder"
        // always.
        private static void AddFolderChoices(OperationCreatePlan plan)
        {
            if (!FolderHasAsmdef(plan.TargetFolder))
            {
                plan.FolderChoices.Add(
                    OperationCreateFolderChoice.CreateCustomAsmdef);
            }
            plan.FolderChoices.Add(OperationCreateFolderChoice.UseDefaultFolder);
        }

        private static bool FolderHasAsmdef(string folder)
        {
            try
            {
                var absolute = ToAbsolute(folder);
                return Directory.Exists(absolute) &&
                    Directory.GetFiles(absolute, "*.asmdef").Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string? AsmdefPathOf(string assemblyName)
        {
            try
            {
                var path = CompilationPipeline
                    .GetAssemblyDefinitionFilePathFromAssemblyName(
                        NormalizeAssemblyName(assemblyName));
                return string.IsNullOrEmpty(path) ? null : path.Replace('\\', '/');
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ── asmdef JSON helpers ──────────────────────────────────────────
        private static string? AsmdefMember(string asmdefPath, string member)
        {
            try
            {
                var absolute = ToAbsolute(asmdefPath);
                if (!File.Exists(absolute))
                {
                    return null;
                }
                var value = JObject.Parse(File.ReadAllText(absolute))[member];
                return value?.Type == JTokenType.String
                    ? value.Value<string>()
                    : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // The asmdef's declared platform set is exactly [Editor] — the
        // Editor-only verdict for a named assembly that never compiled
        // (or is not in the current compilation set).
        private static bool AsmdefIsEditorOnly(string asmdefPath)
        {
            try
            {
                var platforms = JObject.Parse(
                    File.ReadAllText(ToAbsolute(asmdefPath)))["includePlatforms"]
                    as JArray;
                if (platforms is null || platforms.Count == 0)
                {
                    return false;
                }
                foreach (var token in platforms)
                {
                    if (token.Value<string>() != "Editor")
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // autoReferenced defaults to true when the member is absent — a
        // predefined assembly auto-references every asmdef that does not
        // opt out.
        private static bool AsmdefIsAutoReferenced(string assemblyName)
        {
            var asmdefPath = AsmdefPathOf(assemblyName);
            if (asmdefPath is null)
            {
                return false;
            }
            try
            {
                var token = JObject.Parse(
                    File.ReadAllText(ToAbsolute(asmdefPath)))["autoReferenced"];
                return token is null ||
                    (token.Type == JTokenType.Boolean && token.Value<bool>());
            }
            catch (Exception)
            {
                return false;
            }
        }

        // The asmdef's declared assembly references as names — plain
        // entries verbatim, "GUID:<id>" entries resolved through the asset
        // path to the referenced asmdef's declared name (or file name).
        private static List<string> AsmdefReferenceNames(string asmdefPath)
        {
            var names = new List<string>();
            try
            {
                var root = JObject.Parse(
                    File.ReadAllText(ToAbsolute(asmdefPath)));
                if (root["references"] is not JArray references)
                {
                    return names;
                }
                foreach (var token in references)
                {
                    var text = token.Value<string>();
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }
                    if (text.StartsWith("GUID:", StringComparison.Ordinal))
                    {
                        var guidPath = AssetDatabase.GUIDToAssetPath(
                            text.Substring(5));
                        if (!string.IsNullOrEmpty(guidPath))
                        {
                            names.Add(AsmdefMember(guidPath, "name") ??
                                Path.GetFileNameWithoutExtension(guidPath));
                        }
                    }
                    else
                    {
                        names.Add(text);
                    }
                }
            }
            catch (Exception)
            {
            }
            return names;
        }

        // A candidate test asmdef qualifies only when the generated test —
        // `using NUnit.Framework`, `using OpWeave.Testing`,
        // `using UnityEditor` — can compile inside it: Editor-only, gated
        // on UNITY_INCLUDE_TESTS, referencing OpWeave.Testing, with NUnit
        // access and the engine references the template's Undo.ClearAll
        // needs. overrideReferences replaces the default precompiled set
        // (which carries the test framework's nunit.framework.dll), so an
        // overriding asmdef must list it in precompiledReferences;
        // noEngineReferences strips the UnityEditor reference.
        private static bool AsmdefQualifiesAsTestHost(string asmdefPath)
        {
            try
            {
                var root = JObject.Parse(File.ReadAllText(ToAbsolute(asmdefPath)));
                var platforms = root["includePlatforms"] as JArray;
                if (platforms is null || platforms.Count != 1 ||
                    platforms[0].Value<string>() != "Editor")
                {
                    return false;
                }
                var constraints = root["defineConstraints"] as JArray;
                var gated = false;
                if (constraints is not null)
                {
                    foreach (var token in constraints)
                    {
                        if (token.Value<string>() == "UNITY_INCLUDE_TESTS")
                        {
                            gated = true;
                        }
                    }
                }
                if (!gated)
                {
                    return false;
                }
                var references = root["references"] as JArray;
                if (references is null)
                {
                    return false;
                }
                var referencesTesting = false;
                foreach (var token in references)
                {
                    if (token.Value<string>() == "OpWeave.Testing")
                    {
                        referencesTesting = true;
                    }
                }
                if (!referencesTesting)
                {
                    return false;
                }
                var noEngine = root["noEngineReferences"];
                if (noEngine is not null && noEngine.Type == JTokenType.Boolean &&
                    noEngine.Value<bool>())
                {
                    return false;
                }
                var overrideRefs = root["overrideReferences"];
                if (overrideRefs is not null &&
                    overrideRefs.Type == JTokenType.Boolean &&
                    overrideRefs.Value<bool>())
                {
                    var nunit = false;
                    if (root["precompiledReferences"] is JArray precompiled)
                    {
                        foreach (var token in precompiled)
                        {
                            if (token.Value<string>() == "nunit.framework.dll")
                            {
                                nunit = true;
                            }
                        }
                    }
                    if (!nunit)
                    {
                        return false;
                    }
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static (string Path, string RootNamespace)? FindTestAsmdef(string targetFolder)
        {
            string[] guids;
            try
            {
                guids = AssetDatabase.FindAssets("t:AssemblyDefinitionAsset");
            }
            catch (Exception)
            {
                return null;
            }
            var candidates = new List<(string Path, string RootNamespace)>();
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path) ||
                    !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                    !path.EndsWith(".asmdef", StringComparison.Ordinal) ||
                    !AsmdefQualifiesAsTestHost(path))
                {
                    continue;
                }
                var ns = AsmdefMember(path, "rootNamespace")
                    ?? Path.GetFileNameWithoutExtension(path);
                candidates.Add((path.Replace('\\', '/'), ns));
            }
            candidates.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            var prefix = targetFolder + "/";
            foreach (var candidate in candidates)
            {
                if (candidate.Path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            return candidates.Count > 0 ? candidates[0] : null;
        }

        // Every declared assembly name across the project's .asmdef files —
        // parse-based so names of not-yet-compiled asmdefs count too.
        private static HashSet<string> ExistingAsmdefNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                foreach (var guid in AssetDatabase.FindAssets("t:AssemblyDefinitionAsset"))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    var name = AsmdefMember(path, "name");
                    if (!string.IsNullOrEmpty(name))
                    {
                        names.Add(name);
                    }
                }
            }
            catch (Exception)
            {
            }
            return names;
        }

        private static string UniqueAsmdefName(string baseName)
        {
            var existing = ExistingAsmdefNames();
            if (!existing.Contains(baseName))
            {
                return baseName;
            }
            for (var n = 2; ; n++)
            {
                var candidate = baseName + n.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        // ── path helpers ────────────────────────────────────────────────
        internal static string NormalizeFolder(string folder)
        {
            var value = (folder ?? string.Empty).Trim().Replace('\\', '/');
            while (value.EndsWith("/", StringComparison.Ordinal) && value.Length > 1)
            {
                value = value.Substring(0, value.Length - 1);
            }
            return value;
        }

        private static string FolderName(string folder)
        {
            var trimmed = folder.TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            return slash < 0 ? trimmed : trimmed.Substring(slash + 1);
        }

        private static string RelativeUnder(string path, string ancestor)
        {
            var prefix = ancestor.TrimEnd('/') + "/";
            return path.StartsWith(prefix, StringComparison.Ordinal)
                ? path.Substring(prefix.Length)
                : string.Empty;
        }

        private static string PascalIdentifier(string text)
        {
            var builder = new System.Text.StringBuilder(text.Length);
            var upper = true;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (!char.IsLetterOrDigit(c) && c != '_')
                {
                    upper = true;
                    continue;
                }
                builder.Append(upper ? char.ToUpperInvariant(c) : c);
                upper = false;
            }
            if (builder.Length > 0 && !char.IsLetter(builder[0]) && builder[0] != '_')
            {
                builder.Insert(0, '_');
            }
            return builder.ToString();
        }

        private static string ToAbsolute(string assetRelative) =>
            Path.Combine(
                Path.GetFullPath(
                    Path.Combine(UnityEngine.Application.dataPath, "..")),
                assetRelative.Replace('/', Path.DirectorySeparatorChar));

        internal static string ProjectAbsolute(string assetRelative) =>
            ToAbsolute(assetRelative);

        private static bool FileExists(string assetRelative) =>
            File.Exists(ToAbsolute(assetRelative));
    }
}
