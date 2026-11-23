#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor.PackageManager;

namespace OpWeave.Operations
{
    /// <summary>
    /// The bundled script templates of <c>unity.script.create_from_template</c>
    /// (SPEC §G8 item 1, §I.4): the fixed <c>mono_behaviour</c>,
    /// <c>scriptable_object</c> and <c>plain_class</c> sources shipped inside
    /// the package under <c>Editor/Templates~/Scripts/</c>, the substitution
    /// that renders them and the content hash the §F5 evidence row is built
    /// on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Templates live under a <c>~</c> folder: the Editor never imports or
    /// compiles them, and no Agent-reachable port can alter them — they are
    /// read from the package's own resolved path, so they work whether the
    /// package is embedded, local or registry-installed.
    /// </para>
    /// <para>
    /// <b>Marker contract.</b> A template substitutes exactly two things —
    /// the class name and the namespace (§G8 item 1):
    /// <c>#SCRIPTNAME#</c> is replaced by the class name wherever it
    /// appears, and the whole-line markers <c>#NAMESPACEBEGIN#</c> /
    /// <c>#NAMESPACEEND#</c> delimit the region that lives inside a
    /// namespace. With a namespace the begin marker renders
    /// <c>namespace &lt;name&gt;</c> + <c>{</c>, the end marker <c>}</c> and
    /// every non-empty line in between gains four spaces; without a
    /// namespace the marker lines — including their newlines — are removed
    /// and the region's lines are emitted verbatim. A template with
    /// unbalanced or nested markers is not renderable.
    /// </para>
    /// <para>
    /// <b>Hash stability.</b> Rendering normalizes line endings to
    /// <c>\n</c> first, so the expected content hash does not depend on how
    /// the checkout materialized the template files; the written artifact
    /// uses the same UTF-8-no-BOM, <c>\n</c>-separated bytes the hash
    /// covers.
    /// </para>
    /// </remarks>
    internal static class ScriptTemplates
    {
        /// <summary>The <c>mono_behaviour</c> template id (SPEC §I.4 enum).</summary>
        internal const string MonoBehaviourId = "mono_behaviour";

        /// <summary>The <c>scriptable_object</c> template id (SPEC §I.4 enum).</summary>
        internal const string ScriptableObjectId = "scriptable_object";

        /// <summary>The <c>plain_class</c> template id (SPEC §I.4 enum).</summary>
        internal const string PlainClassId = "plain_class";

        /// <summary>Every bundled template id, in the §I.4 enum's order.</summary>
        internal static readonly IReadOnlyList<string> Ids =
            new[] { MonoBehaviourId, ScriptableObjectId, PlainClassId };

        /// <summary>
        /// The version recorded for each bundled template in the §F5
        /// snapshot ("テンプレートの ID と版"): the bundled set is
        /// format version 1 — a template change that alters output bumps it.
        /// </summary>
        internal const int CurrentVersion = 1;

        internal const string ScriptNameMarker = "#SCRIPTNAME#";
        internal const string NamespaceBeginMarker = "#NAMESPACEBEGIN#";
        internal const string NamespaceEndMarker = "#NAMESPACEEND#";

        private const string TemplateDirectory = "Editor/Templates~/Scripts";
        private const string TemplateSuffix = ".cs.txt";

        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false);

        /// <summary>
        /// The base type the bundled template <paramref name="id"/> renders
        /// against — <c>MonoBehaviour</c> or <c>ScriptableObject</c> — or
        /// <see langword="null"/> for <c>plain_class</c> and unknown ids.
        /// A class name that repeats it could not compile: the generated
        /// class would inherit itself.
        /// </summary>
        internal static string? BaseTypeNameOf(string? id)
        {
            switch (id)
            {
                case MonoBehaviourId:
                    return "MonoBehaviour";
                case ScriptableObjectId:
                    return "ScriptableObject";
                default:
                    return null;
            }
        }

        /// <summary>Whether <paramref name="id"/> names a bundled template.</summary>
        internal static bool IsKnown(string? id)
        {
            for (var i = 0; i < Ids.Count; i++)
            {
                if (string.Equals(Ids[i], id, StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The raw text of the bundled template <paramref name="id"/>, or
        /// <see langword="null"/> when the id is unknown or the template file
        /// cannot be read — a missing or unreadable bundled template means a
        /// broken package, which the caller reports as its own failure.
        /// </summary>
        internal static string? ReadSource(string id)
        {
            if (!IsKnown(id))
            {
                return null;
            }
            try
            {
                var info = PackageInfo.FindForAssembly(
                    typeof(ScriptTemplates).Assembly);
                if (info is null || string.IsNullOrEmpty(info.resolvedPath))
                {
                    return null;
                }
                var path = Path.Combine(
                    info.resolvedPath,
                    TemplateDirectory.Replace('/', Path.DirectorySeparatorChar),
                    id + TemplateSuffix);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Renders one bundled template: normalizes line endings to
        /// <c>\n</c>, substitutes the class name for
        /// <see cref="ScriptNameMarker"/>, then expands or removes the
        /// whole-line namespace region as described on the class. Returns
        /// <see langword="null"/> when the source's markers are malformed
        /// (unbalanced, nested or misplaced).
        /// </summary>
        /// <param name="source">Raw template text.</param>
        /// <param name="className">The already-validated class name.</param>
        /// <param name="ns">
        /// The already-validated namespace, or <see langword="null"/> for the
        /// global namespace.
        /// </param>
        internal static string? Render(string source, string className, string? ns)
        {
            if (source is null || className is null)
            {
                return null;
            }
            var text = source
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace(ScriptNameMarker, className);
            var lines = text.Split('\n');

            var output = new List<string>(lines.Length);
            var inner = new List<string>();
            var seenBegin = false;
            var seenEnd = false;
            for (var i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].Trim();
                if (trimmed == NamespaceBeginMarker)
                {
                    if (seenBegin)
                    {
                        return null;
                    }
                    seenBegin = true;
                    continue;
                }
                if (trimmed == NamespaceEndMarker)
                {
                    if (!seenBegin || seenEnd)
                    {
                        return null;
                    }
                    seenEnd = true;
                    continue;
                }
                if (seenBegin && !seenEnd)
                {
                    inner.Add(lines[i]);
                    continue;
                }
                output.Add(lines[i]);
            }
            if (seenBegin != seenEnd)
            {
                return null;
            }

            if (!seenBegin)
            {
                return string.Join("\n", output);
            }
            if (ns is null)
            {
                output.AddRange(inner);
                return string.Join("\n", output);
            }
            output.Add("namespace " + ns);
            output.Add("{");
            for (var i = 0; i < inner.Count; i++)
            {
                output.Add(inner[i].Length == 0 ? inner[i] : "    " + inner[i]);
            }
            output.Add("}");
            return string.Join("\n", output);
        }

        /// <summary>
        /// The fully qualified name the <c>typeName</c> output reports:
        /// <c>namespace.className</c>, or <c>className</c> in the global
        /// namespace (SPEC §I.4 出力 column).
        /// </summary>
        internal static string TypeNameOf(string className, string? ns) =>
            string.IsNullOrEmpty(ns) ? className : ns + "." + className;

        /// <summary>
        /// The lowercase-hex SHA-256 of the rendered content's UTF-8 bytes —
        /// the §F5 evidence row's "テンプレートから求めたソースの内容
        /// ハッシュ", compared byte-for-byte with the file on disk.
        /// </summary>
        internal static string ContentHash(string content) =>
            ContentHash(s_utf8.GetBytes(content));

        /// <summary>The same hash over raw file bytes, for reconciliation.</summary>
        internal static string ContentHash(byte[] bytes)
        {
            byte[] hash;
            using (var sha256 = SHA256.Create())
            {
                hash = sha256.ComputeHash(bytes);
            }
            var builder = new StringBuilder(hash.Length * 2);
            for (var i = 0; i < hash.Length; i++)
            {
                builder.Append(hash[i].ToString("x2"));
            }
            return builder.ToString();
        }

        /// <summary>
        /// The UTF-8-no-BOM encoding generated scripts are written and
        /// hashed in.
        /// </summary>
        internal static UTF8Encoding Encoding => s_utf8;
    }
}
