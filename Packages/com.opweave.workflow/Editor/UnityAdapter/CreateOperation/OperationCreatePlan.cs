#nullable enable

using System.Collections.Generic;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// One live-validation finding of the SPEC §M.4 create dialog.
    /// <see cref="Code"/> carries the SPEC §J diagnostic code when the
    /// finding is a codified error — OW-UNITY-012 (non-Editor folder) —
    /// and is <see langword="null"/> for plain input problems the SPEC
    /// gives no code (name collisions, port issues). The generated ID's
    /// OW-OP-007 check runs at commit, in
    /// <see cref="OperationIdGenerator.Resolve"/>.
    /// </summary>
    internal sealed record OperationCreateIssue(
        Severity Severity, string Message, string? Code)
    {
        /// <summary>An error-level finding; <paramref name="code"/> is the §J code when codified.</summary>
        internal static OperationCreateIssue Error(string message, string? code = null) =>
            new OperationCreateIssue(Severity.Error, message, code);

        /// <summary>A warning-level finding that does not block generation.</summary>
        internal static OperationCreateIssue Warning(string message) =>
            new OperationCreateIssue(Severity.Warning, message, null);
    }

    /// <summary>
    /// One of the two SPEC §M.4 item 3 choices offered when the target
    /// folder does not belong to an Editor-only assembly (OW-UNITY-012).
    /// </summary>
    internal enum OperationCreateFolderChoice
    {
        /// <summary>"ここに OpWeave.Custom.asmdef を作る" — write the §M.2 asmdef into the folder.</summary>
        CreateCustomAsmdef,

        /// <summary>"既定のフォルダに保存する" — write under <c>customOperationFolders</c>[0].</summary>
        UseDefaultFolder,
    }

    /// <summary>
    /// The plan <see cref="OperationCreatePlanner"/> computes from a
    /// <see cref="OperationCreateRequest"/>: the derived identifiers and
    /// paths, every live-validation issue, and the OW-UNITY-012 folder
    /// choices when the target is not Editor-only. The dialog shows this;
    /// <see cref="OperationCreateWriter"/> consumes it.
    /// </summary>
    internal sealed class OperationCreatePlan
    {
        /// <summary>The request this plan was computed for.</summary>
        internal OperationCreateRequest Request = null!;

        /// <summary>The normalized asset-relative target folder.</summary>
        internal string TargetFolder = string.Empty;

        /// <summary>
        /// The compilation assembly the generated file lands in
        /// (<c>CompilationPipeline.GetAssemblyNameFromScriptPath</c>), or
        /// null when the pipeline cannot name one.
        /// </summary>
        internal string? TargetAssemblyName;

        /// <summary>The namespace the generated class is placed in.</summary>
        internal string NamespaceName = "OpWeave.Custom";

        /// <summary>The generated class name — also the file name.</summary>
        internal string ClassName = string.Empty;

        /// <summary>The asset-relative path of the generated class file.</summary>
        internal string FilePath = string.Empty;

        /// <summary>The asset-relative path of the generated test file, or null.</summary>
        internal string? TestFilePath;

        /// <summary>The generated test class name.</summary>
        internal string TestClassName = string.Empty;

        /// <summary>The generated test file's namespace.</summary>
        internal string TestNamespace = "OpWeave.Custom.Tests";

        /// <summary>
        /// The <c>OpWeave.Custom.asmdef</c> file to write into the target
        /// folder when the OW-UNITY-012 "create asmdef here" choice is
        /// taken, or null for an already-Editor-only target.
        /// </summary>
        internal string? OperationAsmdefPath;

        /// <summary>The assembly name inside <see cref="OperationAsmdefPath"/>.</summary>
        internal string? OperationAsmdefName;

        /// <summary>
        /// The test asmdef to create under a <c>Tests</c> subfolder when no
        /// existing test assembly can host the generated test, or null.
        /// </summary>
        internal string? TestAsmdefPath;

        /// <summary>The assembly name inside <see cref="TestAsmdefPath"/>.</summary>
        internal string? TestAsmdefName;

        /// <summary>Every live-validation finding, in deterministic order.</summary>
        internal readonly List<OperationCreateIssue> Issues =
            new List<OperationCreateIssue>();

        /// <summary>
        /// The SPEC §M.4 item 3 choices offered when the OW-UNITY-012
        /// condition holds: <see cref="OperationCreateFolderChoice.UseDefaultFolder"/>
        /// always, and <see cref="OperationCreateFolderChoice.CreateCustomAsmdef"/>
        /// only when the target folder does not already carry an .asmdef
        /// (a same-folder second asmdef is not a valid layout).
        /// </summary>
        internal readonly List<OperationCreateFolderChoice> FolderChoices =
            new List<OperationCreateFolderChoice>();

        /// <summary>True when any <see cref="Severity.Error"/> issue was reported.</summary>
        internal bool HasErrors
        {
            get
            {
                for (var i = 0; i < Issues.Count; i++)
                {
                    if (Issues[i].Severity == Severity.Error)
                    {
                        return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// True when the plan can be written as-is: no errors and no
        /// pending OW-UNITY-012 folder choice.
        /// </summary>
        internal bool CanWrite => !HasErrors && FolderChoices.Count == 0;

        /// <summary>True when at least one issue carries <paramref name="code"/>.</summary>
        internal bool HasCode(string code)
        {
            for (var i = 0; i < Issues.Count; i++)
            {
                if (Issues[i].Code == code)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
