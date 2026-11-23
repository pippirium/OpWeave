#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OpWeave.Core;
using OpWeave.Core.Operations;
using UnityEditor.PackageManager;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The bundled templates of SPEC §M.4 item 4 —
    /// <c>Editor/Templates~/Operation.cs.txt</c> and
    /// <c>Editor/Templates~/OperationTests.cs.txt</c> — and the rendering
    /// that turns a <see cref="OperationCreateRequest"/> plus the planner's
    /// derived names into generated source.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The templates use whole-token markers <c>#NAME#</c> that are
    /// replaced verbatim — including the multi-line fragments the source
    /// builder composes (<c>#DESCRIPTOR_LINES#</c>, <c>#RECORD_LINES#</c>,
    /// <c>#STEP_MEMBERS#</c>). Rendering normalizes line endings to
    /// <c>\n</c> and the output is written UTF-8 without BOM, matching
    /// <see cref="OpWeave.Operations.ScriptTemplates"/>.
    /// </para>
    /// </remarks>
    internal static class OperationTemplates
    {
        /// <summary>The operation class template file id.</summary>
        internal const string OperationTemplateId = "Operation";

        /// <summary>The generated contract-test template file id.</summary>
        internal const string OperationTestsTemplateId = "OperationTests";

        private const string TemplateDirectory = "Editor/Templates~";
        private const string TemplateSuffix = ".cs.txt";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);
        private static readonly Regex s_leftoverMarker =
            new Regex("#[A-Z_]+#", RegexOptions.CultureInvariant);

        /// <summary>The UTF-8-no-BOM encoding generated files are written in.</summary>
        internal static UTF8Encoding Encoding => s_utf8;

        /// <summary>
        /// The raw text of the bundled template <paramref name="name"/>, or
        /// null when it cannot be read (a broken package). Mirrors
        /// <see cref="OpWeave.Operations.ScriptTemplates.ReadSource"/>.
        /// </summary>
        internal static string? ReadSource(string name)
        {
            try
            {
                var info = PackageInfo.FindForAssembly(
                    typeof(OperationTemplates).Assembly);
                if (info is null || string.IsNullOrEmpty(info.resolvedPath))
                {
                    return null;
                }
                var path = Path.Combine(
                    info.resolvedPath,
                    TemplateDirectory.Replace('/', Path.DirectorySeparatorChar),
                    name + TemplateSuffix);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Substitutes every <c>#KEY#</c> token of
        /// <paramref name="tokens"/> in <paramref name="source"/> after
        /// normalizing line endings to <c>\n</c>. Returns null when a
        /// marker remains unsubstituted — a template/renderer drift.
        /// </summary>
        internal static string? Render(
            string source, IReadOnlyDictionary<string, string> tokens)
        {
            var text = source.Replace("\r\n", "\n").Replace("\r", "\n");
            foreach (var pair in tokens)
            {
                text = text.Replace("#" + pair.Key + "#", pair.Value);
            }
            return s_leftoverMarker.IsMatch(text) ? null : text;
        }

        /// <summary>
        /// The generated class file text for <paramref name="request"/>
        /// under the planner's derived <paramref name="namespaceName"/> and
        /// <paramref name="className"/>, or null when rendering failed.
        /// <paramref name="operationId"/> is the single ID minted at commit;
        /// the one <c>#OPERATION_ID#</c> token writes it into the attribute,
        /// the Descriptor and the generated test alike (SPEC §M.4 item 2).
        /// </summary>
        internal static string? RenderOperation(
            OperationCreateRequest request, string namespaceName,
            string className, string operationId)
        {
            var source = ReadSource(OperationTemplateId);
            if (source is null)
            {
                return null;
            }
            var version = request.Version.ToString(CultureInfo.InvariantCulture);
            return Render(source, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["NAMESPACE"] = namespaceName,
                ["CLASS_NAME"] = className,
                ["OPERATION_ID"] = operationId,
                ["VERSION"] = version,
                ["DESCRIPTOR_LINES"] = DescriptorLines(request),
                ["RECORD_LINES"] = RecordLines(request),
                ["STEP_MEMBERS"] = StepMembers(request),
            });
        }

        /// <summary>
        /// The generated contract-test file text, or null when rendering
        /// failed. The test addresses the operation by id — it never
        /// references the generated class directly.
        /// </summary>
        internal static string? RenderTests(
            OperationCreateRequest request, string testNamespace,
            string testClassName, string operationId)
        {
            var source = ReadSource(OperationTestsTemplateId);
            if (source is null)
            {
                return null;
            }
            return Render(source, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TEST_NAMESPACE"] = testNamespace,
                ["TEST_CLASS_NAME"] = testClassName,
                ["OPERATION_ID"] = operationId,
            });
        }

        /// <summary>
        /// The fluent-chain lines between <c>.Define(...)</c> and
        /// <c>.Requires("edit_mode")</c>: summary, the Japanese localization,
        /// the ports, effects, undo and idempotency — in the order the
        /// SPEC §M.2 example uses.
        /// </summary>
        private static string DescriptorLines(OperationCreateRequest request)
        {
            var lines = new List<string>();
            if (request.Summary.Length > 0)
            {
                lines.Add(Line(".Summary(" + Literal(request.Summary.Trim()) + ")"));
            }
            if (request.DisplayNameJa.Trim().Length > 0)
            {
                lines.Add(Line(".Ja(" + Literal(request.DisplayNameJa.Trim()) +
                    ", " + Literal(request.SummaryJa.Trim()) + ")"));
            }
            for (var i = 0; i < request.Ports.Count; i++)
            {
                var port = request.Ports[i];
                lines.Add(Line(port.IsOutput
                    ? ".Output(" + Literal(port.Name.Trim()) + ", " +
                        Literal(port.TypeId.Trim()) + ")"
                    : ".Input(" + Literal(port.Name.Trim()) + ", " +
                        Literal(port.TypeId.Trim()) + ", required: true)"));
            }
            if (request.Effects.Count > 0)
            {
                var names = new List<string>(request.Effects.Count);
                for (var i = 0; i < request.Effects.Count; i++)
                {
                    names.Add(Literal(request.Effects[i]));
                }
                lines.Add(Line(".Effects(" + string.Join(", ", names) + ")"));
            }
            lines.Add(Line(".Undo(UndoSupport.None)"));
            lines.Add(Line(".Idempotency(Idempotency." + request.Idempotency + ")"));
            return string.Join("\n", lines);

            static string Line(string text) => "            " + text;
        }

        /// <summary>
        /// The record <c>with</c> clause members: display name and category
        /// (fields outside the approved fluent set, SPEC §0.6) plus the
        /// ExecutionMode override a Waitable request needs.
        /// </summary>
        private static string RecordLines(OperationCreateRequest request)
        {
            var lines = new List<string>
            {
                "                DisplayName = " + Literal(request.DisplayName.Trim()) + ",",
                "                Category = " + Literal(request.Category.Trim()) + ",",
            };
            if (request.Mode == ExecutionMode.Waitable)
            {
                lines.Add("                ExecutionMode = ExecutionMode.Waitable,");
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// The class members below the descriptor: the Execute stub — and
        /// for a Waitable Operation a real <see cref="IOperation.Poll"/>
        /// body, since the §C3 rule-3 check rejects a Waitable whose Poll
        /// is the <see cref="NotSupportedException"/> placeholder.
        /// </summary>
        private static string StepMembers(OperationCreateRequest request)
        {
            const string ExecuteStub =
                "        public override StepResult Execute(StepContext ctx)\n" +
                "        {\n" +
                "            // TODO: implement the Operation's step.\n" +
                "            return StepResult.Succeeded();\n" +
                "        }";
            if (request.Mode != ExecutionMode.Waitable)
            {
                return ExecuteStub;
            }
            return ExecuteStub + "\n\n" +
                "        /// <inheritdoc/>\n" +
                "        public override StepResult Poll(StepContext ctx)\n" +
                "        {\n" +
                "            // TODO: report progress; succeed when the wait is done.\n" +
                "            return StepResult.Succeeded();\n" +
                "        }";
        }

        /// <summary>A C# string literal for <paramref name="text"/>.</summary>
        internal static string Literal(string text)
        {
            var builder = new StringBuilder(text.Length + 2);
            builder.Append('"');
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '"': builder.Append("\\\""); break;
                    case '\\': builder.Append("\\\\"); break;
                    case '\n': builder.Append("\\n"); break;
                    case '\r': builder.Append("\\r"); break;
                    case '\t': builder.Append("\\t"); break;
                    case '\0': builder.Append("\\0"); break;
                    default:
                        if (c < ' ' || c == '﻿')
                        {
                            builder.Append("\\u").Append(
                                ((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            builder.Append(c);
                        }
                        break;
                }
            }
            builder.Append('"');
            return builder.ToString();
        }
    }
}
