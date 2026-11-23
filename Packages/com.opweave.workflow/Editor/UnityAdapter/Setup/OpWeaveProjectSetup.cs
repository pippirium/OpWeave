#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpWeave.Core.Persistence;
using UnityEditor;
using UnityEngine;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §M.3 initial setup: <c>Tools &gt; OpWeave &gt; Setup Project</c>
    /// prepares the recommended custom-Operation and workflow layout of SPEC §M.2
    /// in one idempotent step — the folders, the two assembly definitions, the
    /// sample workflow and <c>ProjectSettings/OpWeaveSettings.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The run never overwrites existing files or folders (SPEC §M.3 item 3):
    /// an item that already exists is reported <see cref="OpWeaveSetupItemState.Skipped"/>
    /// and left untouched, so running setup a second time changes nothing. The
    /// items the run created and skipped are listed to the Console by the menu
    /// entry (<see cref="LogResult"/>).
    /// </para>
    /// <para>
    /// The layout is fixed: <c>customOperationFolders</c> only sets the §M.4
    /// create menu's default save destination (SPEC §M.3 item 4), so the setup
    /// always writes the §M.2 paths below regardless of settings.
    /// </para>
    /// <para>
    /// Local, reversible choices where SPEC leaves detail open: the settings
    /// file's content is <see cref="OpWeaveSettingsDocument.DefaultsFileText"/> —
    /// the exact v2 defaults document a human save of a missing file writes —
    /// created through the same atomic create-if-absent write as every other
    /// generated file; the test asmdef's shape mirrors the project's own
    /// <c>OpWeave.Custom.Tests</c> fixture (references <c>OpWeave.Testing</c>
    /// per SPEC §0.5); and an item that cannot be created is reported
    /// <see cref="OpWeaveSetupItemState.Failed"/> instead of aborting the run,
    /// so the Console listing stays complete.
    /// </para>
    /// </remarks>
    public static class OpWeaveProjectSetup
    {
        /// <summary>The Unity menu path of the setup entry (SPEC §M.3 item 1).</summary>
        internal const string MenuPath = "Tools/OpWeave/Setup Project";

        private const string CustomAsmdefPath =
            "Assets/OpWeave/Editor/Operations/OpWeave.Custom.asmdef";
        private const string CustomTestsAsmdefPath =
            "Assets/OpWeave/Editor/Tests/OpWeave.Custom.Tests.asmdef";
        private const string SampleWorkflowPath =
            "Assets/OpWeave/Workflows/sample.opweave.json";
        private const string SettingsPath = OpWeaveSettingsLoader.DefaultFilePath;

        // The SPEC §M.2 OpWeave.Custom.asmdef, verbatim.
        internal const string CustomAsmdefJson =
            "{\n" +
            "  \"name\": \"OpWeave.Custom\",\n" +
            "  \"rootNamespace\": \"OpWeave.Custom\",\n" +
            "  \"references\": [\"OpWeave.Core\", \"OpWeave.UnityAdapter\"],\n" +
            "  \"includePlatforms\": [\"Editor\"],\n" +
            "  \"autoReferenced\": false\n" +
            "}\n";

        // The custom Operation contract-test assembly of SPEC §M.2 (user tests
        // reference OpWeave.Testing per SPEC §0.5); same shape as the project's
        // own OpWeave.Custom.Tests fixture.
        internal const string CustomTestsAsmdefJson =
            "{\n" +
            "    \"name\": \"OpWeave.Custom.Tests\",\n" +
            "    \"rootNamespace\": \"OpWeave.Custom.Tests\",\n" +
            "    \"references\": [\n" +
            "        \"OpWeave.Core\",\n" +
            "        \"OpWeave.Custom\",\n" +
            "        \"OpWeave.Testing\",\n" +
            "        \"OpWeave.UnityAdapter\",\n" +
            "        \"UnityEngine.TestRunner\",\n" +
            "        \"UnityEditor.TestRunner\"\n" +
            "    ],\n" +
            "    \"includePlatforms\": [\n" +
            "        \"Editor\"\n" +
            "    ],\n" +
            "    \"excludePlatforms\": [],\n" +
            "    \"allowUnsafeCode\": false,\n" +
            "    \"overrideReferences\": true,\n" +
            "    \"precompiledReferences\": [\n" +
            "        \"nunit.framework.dll\"\n" +
            "    ],\n" +
            "    \"autoReferenced\": false,\n" +
            "    \"defineConstraints\": [\n" +
            "        \"UNITY_INCLUDE_TESTS\"\n" +
            "    ],\n" +
            "    \"versionDefines\": [],\n" +
            "    \"noEngineReferences\": false\n" +
            "}\n";

        // The sample workflow of SPEC §M.3 item 2: a complete, statically
        // validatable WF built only from standard Operations — the first thing
        // a fresh project can open, validate and run.
        internal const string SampleWorkflowJson =
            "{\n" +
            "  \"schemaVersion\": \"1.0\",\n" +
            "  \"id\": \"sample.hello-opweave\",\n" +
            "  \"title\": \"Sample workflow\",\n" +
            "  \"description\": \"Creates a GameObject named SampleObject in the active scene.\",\n" +
            "  \"nodes\": [\n" +
            "    { \"id\": \"start\", \"operation\": \"core.start\", \"version\": 1 },\n" +
            "    { \"id\": \"create\", \"operation\": \"unity.gameobject.create\", \"version\": 1,\n" +
            "      \"parameters\": { \"name\": \"SampleObject\" } }\n" +
            "  ],\n" +
            "  \"controlEdges\": [\n" +
            "    { \"from\": \"start\", \"to\": \"create\" }\n" +
            "  ],\n" +
            "  \"outputs\": [\n" +
            "    { \"id\": \"gameObject\", \"from\": { \"node\": \"create\", \"port\": \"gameObject\" } }\n" +
            "  ]\n" +
            "}\n";

        private static readonly LayoutEntry[] s_layout =
        {
            LayoutEntry.Folder("Assets/OpWeave"),
            LayoutEntry.Folder("Assets/OpWeave/Workflows"),
            LayoutEntry.File(SampleWorkflowPath, SampleWorkflowJson),
            LayoutEntry.Folder("Assets/OpWeave/Editor"),
            LayoutEntry.Folder("Assets/OpWeave/Editor/Operations"),
            LayoutEntry.File(CustomAsmdefPath, CustomAsmdefJson),
            LayoutEntry.Folder("Assets/OpWeave/Editor/Operations/Transform"),
            LayoutEntry.Folder("Assets/OpWeave/Editor/Operations/Animation"),
            LayoutEntry.Folder("Assets/OpWeave/Editor/Operations/Material"),
            LayoutEntry.Folder("Assets/OpWeave/Editor/Operations/Custom"),
            LayoutEntry.Folder("Assets/OpWeave/Editor/Tests"),
            LayoutEntry.File(CustomTestsAsmdefPath, CustomTestsAsmdefJson),
            LayoutEntry.File(SettingsPath, null),
        };

        /// <summary>
        /// The <c>Tools &gt; OpWeave &gt; Setup Project</c> menu entry of SPEC §M.3
        /// item 1: runs the setup on the current project and lists created and
        /// skipped items to the Console.
        /// </summary>
        [MenuItem(MenuPath)]
        private static void SetupProjectMenuItem() => LogResult(Run());

        /// <summary>
        /// Runs the setup on the current project. Existing items are reported
        /// skipped and never overwritten; created asset paths are then imported
        /// so the new folders and asmdefs appear in the project.
        /// </summary>
        public static OpWeaveSetupResult Run()
        {
            var result = Run(ProjectRootPath());
            if (result.Items.Any(
                item => item.State == OpWeaveSetupItemState.Created &&
                    item.Path.StartsWith("Assets/", StringComparison.Ordinal)))
            {
                AssetDatabase.Refresh();
            }
            return result;
        }

        /// <summary>
        /// True when every item of the setup layout already exists — the state
        /// the §M.3 Registry-tab button reads to decide whether setup is still
        /// needed.
        /// </summary>
        public static bool IsSetupComplete() => IsSetupComplete(ProjectRootPath());

        /// <summary>
        /// Runs the setup under <paramref name="projectRoot"/>: the layout paths
        /// are interpreted relative to it, so a scratch root can stand in for an
        /// empty project in tests. Pure filesystem work — no AssetDatabase.
        /// </summary>
        internal static OpWeaveSetupResult Run(string projectRoot)
        {
            if (projectRoot is null)
            {
                throw new ArgumentNullException(nameof(projectRoot));
            }
            var items = new List<OpWeaveSetupItem>(s_layout.Length);
            foreach (var entry in s_layout)
            {
                items.Add(entry.IsSettings
                    ? CreateSettingsFile(projectRoot, entry)
                    : CreateItem(projectRoot, entry));
            }
            return new OpWeaveSetupResult(items);
        }

        /// <summary>True when every layout item exists under <paramref name="projectRoot"/>.</summary>
        internal static bool IsSetupComplete(string projectRoot)
        {
            if (projectRoot is null)
            {
                throw new ArgumentNullException(nameof(projectRoot));
            }
            foreach (var entry in s_layout)
            {
                var absolute = CombinePath(projectRoot, entry.Path);
                var exists = entry.Kind == OpWeaveSetupItemKind.Folder
                    ? Directory.Exists(absolute)
                    : File.Exists(absolute);
                if (!exists)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// The fixed English Console listing of SPEC §M.3 item 3: one line per
        /// created item and one per skipped item, grouped under their counts;
        /// failed creations are listed last with their reason.
        /// </summary>
        internal static string FormatSummary(OpWeaveSetupResult result)
        {
            if (result is null)
            {
                throw new ArgumentNullException(nameof(result));
            }
            var text = new StringBuilder();
            text.Append("OpWeave project setup: ")
                .Append(result.Created.Count).Append(" created, ")
                .Append(result.Skipped.Count).Append(" skipped.");
            if (result.Failed.Count > 0)
            {
                text.Append(' ').Append(result.Failed.Count).Append(" failed.");
            }
            AppendSection(text, "Created", result.Created);
            AppendSection(text, "Skipped", result.Skipped);
            AppendSection(text, "Failed", result.Failed);
            return text.ToString();

            static void AppendSection(
                StringBuilder text, string heading, IReadOnlyList<OpWeaveSetupItem> items)
            {
                if (items.Count == 0)
                {
                    return;
                }
                text.Append('\n').Append(heading).Append(':');
                foreach (var item in items)
                {
                    text.Append("\n  ").Append(item.Path);
                    if (item.Error is not null)
                    {
                        text.Append(" (").Append(item.Error).Append(')');
                    }
                }
            }
        }

        /// <summary>Prints <see cref="FormatSummary"/> to the Console.</summary>
        internal static void LogResult(OpWeaveSetupResult result) =>
            Debug.Log(FormatSummary(result));

        // Internal test seam for the check/create gap: invoked with the
        // absolute path immediately before each atomic create-if-absent write,
        // after the existence check — a regression test drops a file into the
        // gap that FileMode.CreateNew closes. Production callers leave it null.
        internal static Action<string>? CreateFileProbe { get; set; }

        private static OpWeaveSetupItem CreateItem(string projectRoot, LayoutEntry entry)
        {
            var absolute = CombinePath(projectRoot, entry.Path);
            try
            {
                if (entry.Kind == OpWeaveSetupItemKind.Folder)
                {
                    if (Directory.Exists(absolute))
                    {
                        return Skipped(entry);
                    }
                    Directory.CreateDirectory(absolute);
                    return Created(entry);
                }
                if (File.Exists(absolute))
                {
                    return Skipped(entry);
                }
                if (entry.Path == SampleWorkflowPath)
                {
                    return CreateSampleWorkflow(projectRoot, entry, absolute);
                }
                WriteFileIfAbsent(absolute, entry.Content!);
                return Created(entry);
            }
            catch (IOException) when (
                entry.Kind == OpWeaveSetupItemKind.File && File.Exists(absolute))
            {
                // The atomic create lost the race to a destination created
                // between the check and the write: the item is skipped, never
                // overwritten (SPEC §M.3 item 3). A file item blocked by a
                // directory or a folder blocked by a file stays Failed.
                return Skipped(entry);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is NotSupportedException)
            {
                return Failed(entry, "the item could not be created (" + ex.GetType().Name + ")");
            }
        }

        // The sample workflow is a *.opweave.json — an OpWeave-controlled
        // file, so its create goes through the same JsonSaveCoordinator
        // every JSON writer uses (SPEC §H7 item 3): the path must resolve
        // inside the project, the per-path lock serializes against other
        // writers, and the write stays create-only — a file that appeared
        // between the check and the publish move is skipped, never
        // overwritten (SPEC §M.3 item 3).
        private static OpWeaveSetupItem CreateSampleWorkflow(
            string projectRoot, LayoutEntry entry, string absolute)
        {
            try
            {
                var coordinator = new JsonSaveCoordinator(projectRoot);
                var owned = coordinator.ResolveOwnedPath(absolute);
                if (owned.Status != PathResolveStatus.Resolved)
                {
                    return Failed(entry, "the item could not be created");
                }
                var acquired = coordinator.TryAcquire(owned.ResolvedPath!);
                if (acquired.Status != AcquireStatus.Acquired)
                {
                    return Failed(entry, "the item could not be created");
                }
                using (acquired.Lock)
                {
                    if (File.Exists(owned.ResolvedPath))
                    {
                        return Skipped(entry);
                    }
                    CreateFileProbe?.Invoke(absolute);
                    var published = coordinator.PublishCreateAtomic(
                        owned.ResolvedPath!,
                        JsonSaveCoordinator.Utf8Bytes(entry.Content!));
                    if (published.Status == PublishCreateStatus.Created)
                    {
                        return Created(entry);
                    }
                    if (published.Status == PublishCreateStatus.AlreadyExists &&
                        File.Exists(owned.ResolvedPath))
                    {
                        return Skipped(entry);
                    }
                    return Failed(entry, "the item could not be created");
                }
            }
            catch (IOException) when (File.Exists(absolute))
            {
                return Skipped(entry);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is NotSupportedException)
            {
                return Failed(entry, "the item could not be created (" + ex.GetType().Name + ")");
            }
        }

        // The settings file's content is OpWeaveSettingsDocument's v2 defaults
        // text — the same document a SettingsProvider save of a missing file
        // writes — created through the same atomic create-if-absent write as
        // the other generated files: a check-then-Save sequence could truncate
        // a file that appeared mid-run (SPEC §M.3 forbids overwriting).
        private static OpWeaveSetupItem CreateSettingsFile(string projectRoot, LayoutEntry entry)
        {
            var absolute = CombinePath(projectRoot, entry.Path);
            if (File.Exists(absolute))
            {
                return Skipped(entry);
            }
            try
            {
                WriteFileIfAbsent(absolute, OpWeaveSettingsDocument.DefaultsFileText());
                return Created(entry);
            }
            catch (IOException) when (File.Exists(absolute))
            {
                return Skipped(entry);
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is NotSupportedException)
            {
                return Failed(entry, "the settings file could not be written (" + ex.GetType().Name + ")");
            }
        }

        // Writes a generated file atomically: FileMode.CreateNew throws
        // IOException when the destination already exists, so a file that
        // appeared after the existence check is never truncated — the callers
        // map that case to Skipped.
        private static void WriteFileIfAbsent(string absolute, string content)
        {
            var parent = Path.GetDirectoryName(absolute);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }
            CreateFileProbe?.Invoke(absolute);
            using (var stream = new FileStream(
                absolute, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
            }
        }

        private static string ProjectRootPath() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string CombinePath(string projectRoot, string relative) =>
            Path.Combine(
                projectRoot,
                relative.Replace('/', Path.DirectorySeparatorChar));

        private static OpWeaveSetupItem Created(LayoutEntry entry) =>
            new OpWeaveSetupItem(entry.Kind, entry.Path, OpWeaveSetupItemState.Created, null);

        private static OpWeaveSetupItem Skipped(LayoutEntry entry) =>
            new OpWeaveSetupItem(entry.Kind, entry.Path, OpWeaveSetupItemState.Skipped, null);

        private static OpWeaveSetupItem Failed(LayoutEntry entry, string error) =>
            new OpWeaveSetupItem(entry.Kind, entry.Path, OpWeaveSetupItemState.Failed, error);

        private sealed class LayoutEntry
        {
            private LayoutEntry(
                OpWeaveSetupItemKind kind, string path, string? content, bool isSettings)
            {
                Kind = kind;
                Path = path;
                Content = content;
                IsSettings = isSettings;
            }

            internal OpWeaveSetupItemKind Kind { get; }
            internal string Path { get; }
            internal string? Content { get; }
            internal bool IsSettings { get; }

            internal static LayoutEntry Folder(string path) =>
                new LayoutEntry(OpWeaveSetupItemKind.Folder, path, null, isSettings: false);

            internal static LayoutEntry File(string path, string? content) =>
                new LayoutEntry(
                    OpWeaveSetupItemKind.File, path, content,
                    isSettings: path == SettingsPath);
        }
    }
}
