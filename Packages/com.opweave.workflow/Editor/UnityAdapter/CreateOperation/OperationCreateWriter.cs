#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The SPEC §M.4 item 4 write step: renders the two templates for a
    /// valid <see cref="OperationCreatePlan"/>, writes the files atomically
    /// — never overwriting — and imports each written asset through
    /// <see cref="AssetDatabase.ImportAsset(string, ImportAssetOptions)"/>.
    /// When the plan carries the OW-UNITY-012 "create asmdef here"
    /// resolution the §M.2 asmdefs are written first.
    /// </summary>
    /// <remarks>
    /// The Operation ID is minted here, once, at commit (SPEC §A4 / §M.4
    /// item 2): <see cref="OperationIdGenerator.Resolve"/> draws the UUID
    /// and runs the commit-time checks before any file is written, and the
    /// single accepted value fills the attribute, the Descriptor and the
    /// generated test. The <c>operationIdSource</c> parameter is the
    /// internal test seam — production always uses
    /// <see cref="OperationIdGenerator.NewUuidV4Id"/>.
    /// </remarks>
    internal static class OperationCreateWriter
    {
        /// <summary>The outcome of one write: written asset paths and errors.</summary>
        internal sealed class Result
        {
            internal readonly List<string> WrittenAssets = new List<string>();
            internal readonly List<string> Errors = new List<string>();
            internal bool Ok => Errors.Count == 0;

            /// <summary>
            /// The Operation ID generated at commit (SPEC §A4); null when
            /// the write was refused before an ID was accepted.
            /// </summary>
            internal string? OperationId;
        }

        /// <summary>
        /// Writes the plan's files. A plan that still has error issues or a
        /// pending OW-UNITY-012 folder choice is refused.
        /// </summary>
        internal static Result Write(
            OperationCreatePlan plan, Func<string>? operationIdSource = null)
        {
            if (plan is null)
            {
                throw new ArgumentNullException(nameof(plan));
            }
            var result = new Result();
            if (!plan.CanWrite)
            {
                result.Errors.Add(
                    "The plan still has errors or an unresolved folder choice.");
                return result;
            }

            var request = plan.Request;
            var resolved = OperationIdGenerator.Resolve(
                plan, operationIdSource ?? OperationIdGenerator.NewUuidV4Id);
            if (!resolved.Ok)
            {
                result.Errors.AddRange(resolved.Errors);
                return result;
            }
            result.OperationId = resolved.OperationId;
            var operationId = resolved.OperationId!;

            var operationSource = OperationTemplates.RenderOperation(
                request, plan.NamespaceName, plan.ClassName, operationId);
            if (operationSource is null)
            {
                result.Errors.Add("The Operation template could not be rendered.");
                return result;
            }
            string? testSource = null;
            if (request.CreateTests)
            {
                testSource = OperationTemplates.RenderTests(
                    request, plan.TestNamespace, plan.TestClassName, operationId);
                if (testSource is null)
                {
                    result.Errors.Add(
                        "The Operation contract-test template could not be rendered.");
                    return result;
                }
            }

            if (plan.OperationAsmdefPath is { } asmdefPath)
            {
                WriteAsset(result, asmdefPath,
                    CustomAsmdefContent(plan.OperationAsmdefName!));
            }
            if (plan.TestAsmdefPath is { } testAsmdefPath)
            {
                WriteAsset(result, testAsmdefPath,
                    CustomTestsAsmdefContent(
                        plan.TestAsmdefName!, plan.OperationAsmdefName));
            }
            WriteAsset(result, plan.FilePath, operationSource);
            if (testSource is not null && plan.TestFilePath is { } testPath)
            {
                WriteAsset(result, testPath, testSource);
            }
            return result;
        }

        // Writes one generated file with create-only semantics (no overwrite)
        // and imports it — SPEC §M.4 item 4 "ImportAsset で取り込む".
        private static void WriteAsset(
            Result result, string assetPath, string content)
        {
            var absolute = OperationCreatePlanner.ProjectAbsolute(assetPath);
            try
            {
                var parent = Path.GetDirectoryName(absolute);
                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                }
                using (var stream = new FileStream(
                    absolute, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                using (var writer = new StreamWriter(stream, OperationTemplates.Encoding))
                {
                    writer.Write(content);
                }
            }
            catch (Exception ex) when (
                ex is IOException || ex is UnauthorizedAccessException ||
                ex is System.Security.SecurityException || ex is NotSupportedException)
            {
                result.Errors.Add(
                    "The file '" + assetPath + "' could not be written (" +
                    ex.GetType().Name + ").");
                return;
            }
            result.WrittenAssets.Add(assetPath);
            try
            {
                AssetDatabase.ImportAsset(
                    assetPath, ImportAssetOptions.ForceSynchronousImport);
            }
            catch (Exception)
            {
                result.Errors.Add(
                    "The asset '" + assetPath + "' could not be imported.");
            }
        }

        // The SPEC §M.2 custom-Operation asmdef with the planned unique name.
        internal static string CustomAsmdefContent(string name) =>
            "{\n" +
            "    \"name\": \"" + name + "\",\n" +
            "    \"rootNamespace\": \"" + name + "\",\n" +
            "    \"references\": [\n" +
            "        \"OpWeave.Core\",\n" +
            "        \"OpWeave.UnityAdapter\"\n" +
            "    ],\n" +
            "    \"includePlatforms\": [\n" +
            "        \"Editor\"\n" +
            "    ],\n" +
            "    \"excludePlatforms\": [],\n" +
            "    \"allowUnsafeCode\": false,\n" +
            "    \"overrideReferences\": false,\n" +
            "    \"precompiledReferences\": [],\n" +
            "    \"autoReferenced\": false,\n" +
            "    \"defineConstraints\": [],\n" +
            "    \"versionDefines\": [],\n" +
            "    \"noEngineReferences\": false\n" +
            "}\n";

        // The SPEC §M.2 contract-test asmdef (SPEC §0.5: user tests reference
        // OpWeave.Testing) with the planned unique name; operationName is the
        // custom assembly the tests may extend against, when one exists.
        internal static string CustomTestsAsmdefContent(
            string name, string? operationName)
        {
            var references = "        \"OpWeave.Core\",\n";
            if (operationName is not null)
            {
                references += "        \"" + operationName + "\",\n";
            }
            references +=
                "        \"OpWeave.Testing\",\n" +
                "        \"OpWeave.UnityAdapter\",\n" +
                "        \"UnityEngine.TestRunner\",\n" +
                "        \"UnityEditor.TestRunner\"\n";
            return
                "{\n" +
                "    \"name\": \"" + name + "\",\n" +
                "    \"rootNamespace\": \"" + name + "\",\n" +
                "    \"references\": [\n" +
                references +
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
        }
    }
}
