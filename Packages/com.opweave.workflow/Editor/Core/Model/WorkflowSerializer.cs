#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Model
{
    /// <summary>
    /// Options for <see cref="WorkflowSerializer.Read"/> (SPEC §A3): <c>strict</c>
    /// upgrades unknown-property diagnostics from Warning to Error; <c>maxBytes</c>
    /// bounds the UTF-8 size of the document text (default 1 MiB, SPEC §0.6 SG-13).
    /// </summary>
    public sealed record ReadOptions(bool Strict = false, int MaxBytes = 1 << 20);

    /// <summary>
    /// Converts <c>.opweave.json</c> text to and from the immutable
    /// <see cref="WorkflowDocument"/> model (SPEC §A3). File I/O belongs to the caller;
    /// this type only translates strings.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Read"/> first validates the text with the strict RFC 8259 SG-02
    /// reader (<see cref="CanonicalJson.Parse"/>), so JSON extensions a Newtonsoft
    /// reader would accept — single-quoted or unquoted names, trailing commas,
    /// <c>NaN</c>/<c>Infinity</c> — are OW-WF-001, then streams it through a
    /// <see cref="JsonTextReader"/> with <see cref="DateParseHandling.None"/>,
    /// <see cref="FloatParseHandling.Double"/> and <c>MaxDepth = 32</c> (SPEC §A3
    /// item 1). Comments, duplicate keys, excess depth, non-finite numbers and
    /// integers outside Int64 are OW-WF-001. Unknown properties
    /// are OW-WF-006 warnings (errors under <c>strict</c>), <c>x-</c> members are kept
    /// silently in <see cref="WorkflowDocument.Extensions"/>, and objects inside a
    /// node's <c>parameters</c> that carry a <c>$</c>-prefixed key — including the
    /// parameters object itself — are OW-WF-006 errors (SPEC §A5). The SG-13 limits
    /// (file bytes, 200 nodes, 64 KiB string parameter, equality allowed) are
    /// OW-WF-008. A newer-major or malformed <c>schemaVersion</c> is OW-WF-002; a
    /// same-major newer-minor reads with a warning (SPEC §A7, §N.3 item 3). Structural
    /// checks that the token stream cannot express — missing required members — are
    /// left to <see cref="WorkflowDocument.Create"/> and come back as OW-WF-003.
    /// </para>
    /// <para>
    /// <see cref="Write"/> emits <c>schemaVersion</c> <c>"1.0"</c> — the minimal version
    /// the v1 feature set needs (SPEC §N.3 item 2) — fixes the top-level member order to
    /// the SPEC §A2 table, keeps author order for arrays, emits <c>JsonValue</c>
    /// payloads as their canonical text, and formats with 2-space indentation, LF
    /// endings and a trailing newline.
    /// </para>
    /// <para>
    /// <see cref="Diagnostic"/> carries no line/column member (SPEC §0.4), so every
    /// diagnostic emitted here reports its JsonPath in <see cref="Diagnostic.JsonPath"/>
    /// and its 1-based line and 0-based column in the message as
    /// <c> (line L, column C)</c> — the same location-in-message convention the SG-02
    /// low-level reader uses for offsets. Strict SG-02 failures keep the reader's
    /// <c> at offset N</c> location ahead of the line/column it resolves to.
    /// </para>
    /// </remarks>
    public static class WorkflowSerializer
    {
        /// <summary>Maximum number of nodes in a workflow document (SPEC §0.6 SG-13).</summary>
        private const int MaxNodes = 200;

        /// <summary>Maximum UTF-8 size of a single string parameter (SPEC §0.6 SG-13).</summary>
        private const int MaxStringParameterBytes = 64 * 1024;

        /// <summary>Maximum container nesting the reader accepts (SPEC §A3 item 1).</summary>
        private const int MaxJsonDepth = 32;

        // Non-throwing UTF-8 encoding for byte counting; unpaired surrogates count as
        // the 3-byte replacement the encoder would produce.
        private static readonly UTF8Encoding s_utf8 = new UTF8Encoding(false, false);

        // The SG-02 reader ends every failure message with " at offset N.", where
        // N is a UTF-16 index into the source text (CanonicalJsonReader).
        private static readonly System.Text.RegularExpressions.Regex s_offsetSuffix =
            new System.Text.RegularExpressions.Regex(
                @" at offset ([0-9]+)\.$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Reads <paramref name="json"/> into a <see cref="WorkflowDocument"/> (SPEC
        /// §A3). The result carries every diagnostic found: it fails when any of them
        /// is an error, and succeeds — still carrying the warnings — otherwise.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        public static Result<WorkflowDocument> Read(string json, ReadOptions? options = null)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }
            options ??= new ReadOptions();

            var positions = new Dictionary<string, (int Line, int Column)>(StringComparer.Ordinal);
            var mapper = new Mapper(options.Strict, positions);

            // SPEC §0.6 SG-13: the UTF-8 file size is bounded; equality is allowed.
            var overflowIndex = Utf8OverflowIndex(json, options.MaxBytes);
            if (overflowIndex >= 0)
            {
                var (overflowLine, overflowColumn) = LineColumnAt(json, overflowIndex);
                return Result<WorkflowDocument>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_WF_008,
                    WithPosition(
                        "The workflow document exceeds the maximum size of " +
                        options.MaxBytes.ToString(CultureInfo.InvariantCulture) + " bytes",
                        overflowLine, overflowColumn),
                    jsonPath: "$"));
            }

            var root = ParseTokens(json, positions, out var parseFailure);
            if (parseFailure is not null)
            {
                return Result<WorkflowDocument>.Failure(parseFailure);
            }

            WorkflowDocumentData data;
            if (root is JObject rootObject)
            {
                data = mapper.MapRoot(rootObject);
            }
            else
            {
                // A scalar or array root supplies none of the required members;
                // WorkflowDocument.Create reports them all as OW-WF-003.
                data = new WorkflowDocumentData();
            }

            var created = WorkflowDocument.Create(data);
            foreach (var diagnostic in created.Diagnostics)
            {
                mapper.Adopt(diagnostic);
            }

            if (!created.Ok || mapper.HasErrors)
            {
                return Result<WorkflowDocument>.Failure(mapper.Diagnostics);
            }
            return Result<WorkflowDocument>.Success(created.Value!, mapper.Diagnostics);
        }

        /// <summary>
        /// Writes <paramref name="doc"/> as <c>.opweave.json</c> text: the §A2
        /// top-level member order, author order for arrays, canonical
        /// <see cref="JsonValue"/> text for parameter/default/extension values,
        /// 2-space indentation, LF endings and a trailing newline (SPEC §A3 item 3).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="doc"/> is null.</exception>
        public static string Write(WorkflowDocument doc)
        {
            if (doc is null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            var root = new JObject();

            // SPEC §N.3 item 2: the minimal schemaVersion the document's features
            // need — the v1 feature set needs 1.0.
            root["schemaVersion"] = "1.0";
            root["id"] = doc.Id;
            if (doc.Title is not null)
            {
                root["title"] = doc.Title;
            }
            if (doc.Description is not null)
            {
                root["description"] = doc.Description;
            }
            if (doc.Tags.Count != 0)
            {
                var tags = new JArray();
                foreach (var tag in doc.Tags)
                {
                    tags.Add(new JValue(tag));
                }
                root["tags"] = tags;
            }
            if (doc.Inputs.Count != 0)
            {
                root["inputs"] = WriteInputs(doc.Inputs);
            }
            if (doc.Outputs.Count != 0)
            {
                root["outputs"] = WriteOutputs(doc.Outputs);
            }
            root["nodes"] = WriteNodes(doc.Nodes);
            root["controlEdges"] = WriteControlEdges(doc.ControlEdges);
            if (doc.DataEdges.Count != 0)
            {
                root["dataEdges"] = WriteDataEdges(doc.DataEdges);
            }
            if (doc.InputBindings.Count != 0)
            {
                root["inputBindings"] = WriteInputBindings(doc.InputBindings);
            }
            if (doc.Layout is not null)
            {
                root["layout"] = WriteLayout(doc.Layout);
            }
            foreach (var pair in doc.Extensions)
            {
                root[pair.Key] = new JRaw(pair.Value.CanonicalText);
            }

            var builder = new StringBuilder(1024);
            WriteValue(builder, root, 0);
            builder.Append('\n');
            return builder.ToString();
        }

        private static JArray WriteInputs(IReadOnlyList<WorkflowInput> inputs)
        {
            var array = new JArray();
            foreach (var input in inputs)
            {
                var item = new JObject();
                item["id"] = input.Id;
                item["type"] = input.Type;
                item["required"] = input.Required;
                if (input.Default is not null)
                {
                    item["default"] = new JRaw(input.Default.CanonicalText);
                }
                if (input.Description is not null)
                {
                    item["description"] = input.Description;
                }
                array.Add(item);
            }
            return array;
        }

        private static JArray WriteOutputs(IReadOnlyList<WorkflowOutput> outputs)
        {
            var array = new JArray();
            foreach (var output in outputs)
            {
                var item = new JObject();
                item["id"] = output.Id;
                item["from"] = WritePortRef(output.From);
                if (output.Description is not null)
                {
                    item["description"] = output.Description;
                }
                array.Add(item);
            }
            return array;
        }

        private static JArray WriteNodes(IReadOnlyList<NodeModel> nodes)
        {
            var array = new JArray();
            foreach (var node in nodes)
            {
                var item = new JObject();
                item["id"] = node.Id;
                item["operation"] = node.Operation;
                item["version"] = node.Version;
                if (node.Parameters.Count != 0)
                {
                    var parameters = new JObject();
                    foreach (var pair in node.Parameters)
                    {
                        parameters[pair.Key] = new JRaw(pair.Value.CanonicalText);
                    }
                    item["parameters"] = parameters;
                }
                if (node.Label is not null)
                {
                    item["label"] = node.Label;
                }
                if (node.Policy is not null)
                {
                    var policy = new JObject();
                    if (node.Policy.TimeoutSeconds is not null)
                    {
                        policy["timeoutSeconds"] = node.Policy.TimeoutSeconds.Value;
                    }
                    if (node.Policy.MaxAttempts is not null)
                    {
                        policy["maxAttempts"] = node.Policy.MaxAttempts.Value;
                    }
                    item["policy"] = policy;
                }
                array.Add(item);
            }
            return array;
        }

        private static JArray WriteControlEdges(IReadOnlyList<ControlEdge> edges)
        {
            var array = new JArray();
            foreach (var edge in edges)
            {
                var item = new JObject();
                item["from"] = edge.From;
                item["to"] = edge.To;
                if (!string.Equals(edge.FromPort, "next", StringComparison.Ordinal))
                {
                    item["fromPort"] = edge.FromPort;
                }
                array.Add(item);
            }
            return array;
        }

        private static JArray WriteDataEdges(IReadOnlyList<DataEdge> edges)
        {
            var array = new JArray();
            foreach (var edge in edges)
            {
                var item = new JObject();
                item["from"] = WritePortRef(edge.From);
                item["to"] = WritePortRef(edge.To);
                array.Add(item);
            }
            return array;
        }

        private static JArray WriteInputBindings(IReadOnlyList<InputBinding> bindings)
        {
            var array = new JArray();
            foreach (var binding in bindings)
            {
                var item = new JObject();
                item["fromInput"] = binding.FromInput;
                item["to"] = WritePortRef(binding.To);
                array.Add(item);
            }
            return array;
        }

        private static JObject WritePortRef(PortRef portRef)
        {
            var item = new JObject();
            item["node"] = portRef.Node;
            item["port"] = portRef.Port;
            return item;
        }

        private static JObject WriteLayout(LayoutInfo layout)
        {
            var item = new JObject();
            var nodes = new JObject();
            foreach (var pair in layout.Nodes)
            {
                var position = new JObject();
                position["x"] = pair.Value.X;
                position["y"] = pair.Value.Y;
                nodes[pair.Key] = position;
            }
            item["nodes"] = nodes;
            var notes = new JArray();
            foreach (var note in layout.Notes)
            {
                var entry = new JObject();
                entry["id"] = note.Id;
                entry["x"] = note.X;
                entry["y"] = note.Y;
                entry["text"] = note.Text;
                notes.Add(entry);
            }
            item["notes"] = notes;
            return item;
        }

        // Document-structure writer: objects and arrays expand over lines with
        // 2-space indentation; JsonValue payloads (JRaw) stay on one line in their
        // canonical text; scalars serialize through the single CanonicalJson
        // implementation (SPEC §A3 items 3-4).
        private static void WriteValue(StringBuilder builder, JToken token, int depth)
        {
            switch (token)
            {
                case JObject obj:
                {
                    if (obj.Count == 0)
                    {
                        builder.Append("{}");
                        return;
                    }
                    builder.Append('{');
                    var first = true;
                    foreach (var property in obj.Properties())
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }
                        first = false;
                        builder.Append('\n');
                        Indent(builder, depth + 1);
                        builder.Append(CanonicalJson.Serialize(new JValue(property.Name)));
                        builder.Append(": ");
                        WriteValue(builder, property.Value, depth + 1);
                    }
                    builder.Append('\n');
                    Indent(builder, depth);
                    builder.Append('}');
                    return;
                }
                case JArray array:
                {
                    if (array.Count == 0)
                    {
                        builder.Append("[]");
                        return;
                    }
                    builder.Append('[');
                    var first = true;
                    foreach (var item in array)
                    {
                        if (!first)
                        {
                            builder.Append(',');
                        }
                        first = false;
                        builder.Append('\n');
                        Indent(builder, depth + 1);
                        WriteValue(builder, item, depth + 1);
                    }
                    builder.Append('\n');
                    Indent(builder, depth);
                    builder.Append(']');
                    return;
                }
                case JRaw raw:
                    builder.Append((string?)raw.Value);
                    return;
                case JValue value:
                    builder.Append(CanonicalJson.Serialize(value));
                    return;
                default:
                    throw new ArgumentException(
                        "Unexpected token in serializer output: " + token.Type);
            }
        }

        private static void Indent(StringBuilder builder, int depth) =>
            builder.Append(' ', depth * 2);

        // ── Read path ─────────────────────────────────────────────────────────

        // One open container while streaming JsonTextReader tokens: object frames
        // track seen keys for duplicate detection (OW-WF-001) and the pending
        // property name; array frames track the next element index. The path is the
        // plain-concatenation JsonPath ("$.nodes[0].parameters.p") that
        // WorkflowDocument.Create's diagnostics also use, so positions recorded
        // here resolve those diagnostics.
        private sealed class Frame
        {
            public Frame(JContainer container, string path, bool isObject)
            {
                Container = container;
                Path = path;
                IsObject = isObject;
            }

            public JContainer Container { get; }
            public string Path { get; }
            public bool IsObject { get; }
            public HashSet<string> Keys { get; } = new HashSet<string>(StringComparer.Ordinal);
            public string? PendingKey { get; set; }
            public int NextIndex { get; set; }
        }

        private static JToken? ParseTokens(
            string json,
            Dictionary<string, (int Line, int Column)> positions,
            out Diagnostic? failure)
        {
            // SPEC §A3/§0.6 SG-02: the text must satisfy the strict RFC 8259
            // reader before the token walk sees it — JsonTextReader alone
            // accepts JSON.NET extensions (single-quoted strings or names,
            // unquoted names, trailing commas, NaN/Infinity literals) that
            // SPEC §A3 does not allow. The internal SG-02 diagnostic maps to
            // OW-WF-001 exactly once: payload-free message, offset and
            // JsonPath kept, object member steps rendered as the "*"
            // wildcard so decoded source keys never reach a diagnostic.
            // The offset is resolved over the original text to the same
            // 1-based line / 0-based column every other reader diagnostic
            // reports (SPEC §A3 item 5) and appended as " (line L, column C)".
            var strict = CanonicalJson.Parse(json);
            if (!strict.Ok)
            {
                var syntax = strict.Diagnostics[0];
                var message = syntax.Message;
                var offsetMatch = s_offsetSuffix.Match(message);
                if (offsetMatch.Success && int.TryParse(
                        offsetMatch.Groups[1].Value, NumberStyles.None,
                        CultureInfo.InvariantCulture, out var offset))
                {
                    var (line, column) = LineColumnAt(json, offset);
                    message = WithPosition(message.TrimEnd('.'), line, column);
                }
                failure = ErrorCodes.Create(
                    ErrorCodes.OW_WF_001,
                    message,
                    jsonPath: syntax.JsonPath);
                return null;
            }

            var stack = new List<Frame>();
            JToken? root = null;
            failure = null;
            using (var stringReader = new StringReader(json))
            {
                var reader = new JsonTextReader(stringReader)
                {
                    DateParseHandling = DateParseHandling.None,
                    FloatParseHandling = FloatParseHandling.Double,
                    MaxDepth = MaxJsonDepth,
                    Culture = CultureInfo.InvariantCulture,
                };
                try
                {
                    while (reader.Read())
                    {
                        var lineInfo = (IJsonLineInfo)reader;
                        var line = lineInfo.LineNumber;
                        var column = lineInfo.LinePosition;
                        if (root is not null && stack.Count == 0)
                        {
                            failure = ParseError(
                                "Unexpected content after the workflow document",
                                "$", line, column);
                            return null;
                        }
                        switch (reader.TokenType)
                        {
                            case JsonToken.StartObject:
                            case JsonToken.StartArray:
                            {
                                var path = ValuePath(stack);
                                positions[path] = (line, column);
                                stack.Add(new Frame(
                                    reader.TokenType == JsonToken.StartObject
                                        ? (JContainer)new JObject() : new JArray(),
                                    path,
                                    reader.TokenType == JsonToken.StartObject));
                                break;
                            }
                            case JsonToken.PropertyName:
                            {
                                var frame = stack[stack.Count - 1];
                                var name = (string)reader.Value!;
                                if (!frame.Keys.Add(name))
                                {
                                    failure = ParseError(
                                        "Duplicate object key",
                                        MemberPath(frame.Path, name), line, column);
                                    return null;
                                }
                                frame.PendingKey = name;
                                break;
                            }
                            case JsonToken.EndObject:
                            case JsonToken.EndArray:
                            {
                                var frame = stack[stack.Count - 1];
                                stack.RemoveAt(stack.Count - 1);
                                Attach(stack, frame.Container, line, column, positions, ref root);
                                break;
                            }
                            case JsonToken.Comment:
                                failure = ParseError(
                                    "Comments are not allowed in workflow documents",
                                    stack.Count != 0 && stack[stack.Count - 1].PendingKey is not null
                                        ? ValuePath(stack) : EnclosingPath(stack),
                                    line, column);
                                return null;
                            case JsonToken.String:
                            case JsonToken.Boolean:
                            case JsonToken.Null:
                            {
                                Attach(stack, new JValue(reader.Value), line, column, positions, ref root);
                                break;
                            }
                            case JsonToken.Integer:
                            {
                                if (reader.Value is BigInteger bigInteger &&
                                    (bigInteger < new BigInteger(long.MinValue) ||
                                     bigInteger > new BigInteger(long.MaxValue)))
                                {
                                    failure = ParseError(
                                        "JSON integer is out of the Int64 range",
                                        ValuePath(stack), line, column);
                                    return null;
                                }
                                Attach(stack, new JValue(reader.Value), line, column, positions, ref root);
                                break;
                            }
                            case JsonToken.Float:
                            {
                                var number = (double)reader.Value!;
                                if (double.IsNaN(number) || double.IsInfinity(number))
                                {
                                    failure = ParseError(
                                        "JSON number is not a finite value",
                                        ValuePath(stack), line, column);
                                    return null;
                                }
                                Attach(stack, new JValue(number), line, column, positions, ref root);
                                break;
                            }
                            default:
                                // Undefined, Date, Bytes, (Start)Constructor, Raw, None.
                                failure = ParseError(
                                    "The document is not valid JSON",
                                    ValuePath(stack), line, column);
                                return null;
                        }
                    }
                }
                catch (JsonException exception)
                {
                    var lineInfo = (IJsonLineInfo)reader;
                    var line = lineInfo.LineNumber;
                    var column = lineInfo.LinePosition;
                    if (exception is JsonReaderException readerException)
                    {
                        line = readerException.LineNumber;
                        column = readerException.LinePosition;
                    }
                    // Depth overflow is reported through the same JsonReaderException
                    // channel as other parse failures; it keeps its own reason text.
                    var reason = exception.Message.IndexOf("MaxDepth", StringComparison.Ordinal) >= 0
                        ? "JSON nesting exceeds the maximum depth of " +
                          MaxJsonDepth.ToString(CultureInfo.InvariantCulture)
                        : "The document is not valid JSON";
                    failure = ParseError(reason, EnclosingPath(stack), line, column);
                    return null;
                }
            }
            if (root is null)
            {
                failure = ParseError("The document is not valid JSON", "$", 1, 0);
            }
            return root;
        }

        private static Diagnostic ParseError(string reason, string path, int line, int column) =>
            ErrorCodes.Create(
                ErrorCodes.OW_WF_001,
                WithPosition(reason, line, column),
                jsonPath: path);

        private static string WithPosition(string reason, int line, int column) =>
            reason + " (line " + line.ToString(CultureInfo.InvariantCulture) +
            ", column " + column.ToString(CultureInfo.InvariantCulture) + ").";

        private static void Attach(
            List<Frame> stack,
            JToken token,
            int line,
            int column,
            Dictionary<string, (int Line, int Column)> positions,
            ref JToken? root)
        {
            if (stack.Count == 0)
            {
                root = token;
                positions["$"] = (line, column);
                return;
            }
            if (stack.Count == 0)
            {
                root = token;
                // A container's position was already recorded at its open token.
                if (token is not JContainer)
                {
                    positions["$"] = (line, column);
                }
                return;
            }
            var frame = stack[stack.Count - 1];
            string path;
            if (frame.IsObject)
            {
                var key = frame.PendingKey!;
                path = frame.Path + "." + key;
                frame.PendingKey = null;
                frame.Container.Add(new JProperty(key, token));
            }
            else
            {
                path = frame.Path + "[" + frame.NextIndex.ToString(CultureInfo.InvariantCulture) + "]";
                frame.Container.Add(token);
                frame.NextIndex++;
            }
            if (token is not JContainer)
            {
                positions[path] = (line, column);
            }
        }

        // Path of the container enclosing the token being read ("$" at the root).
        private static string EnclosingPath(List<Frame> stack) =>
            stack.Count == 0 ? "$" : stack[stack.Count - 1].Path;

        // Path of the value currently being read.
        private static string ValuePath(List<Frame> stack)
        {
            if (stack.Count == 0)
            {
                return "$";
            }
            var frame = stack[stack.Count - 1];
            return frame.IsObject
                ? frame.Path + "." + frame.PendingKey
                : frame.Path + "[" + frame.NextIndex.ToString(CultureInfo.InvariantCulture) + "]";
        }

        // First UTF-16 index whose inclusive UTF-8 byte prefix exceeds maxBytes,
        // or -1 when the whole text fits (equality allowed, SPEC §0.6 SG-13).
        private static int Utf8OverflowIndex(string text, int maxBytes)
        {
            var bytes = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c < 0x80)
                {
                    bytes += 1;
                }
                else if (c < 0x800)
                {
                    bytes += 2;
                }
                else if (char.IsHighSurrogate(c) &&
                         i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    bytes += 4;
                    i++;
                }
                else
                {
                    bytes += 3;
                }
                if (bytes > maxBytes)
                {
                    return i;
                }
            }
            return -1;
        }

        // 1-based line and 0-based column of the UTF-16 index — the same
        // convention JsonTextReader's IJsonLineInfo reports.
        private static (int Line, int Column) LineColumnAt(string text, int index)
        {
            var line = 1;
            var column = 0;
            var end = Math.Min(index, text.Length);
            for (var i = 0; i < end; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    column = 0;
                }
                else
                {
                    column++;
                }
            }
            return (line, column);
        }

        // The JsonPath member step: dotted for plain identifiers, bracketed for
        // names like "a.b" or "$expr" — the convention IdGrammar uses
        // (SPEC §0.6 SG-18: $.policy.agent["code.write"]).
        private static string MemberPath(string parent, string name)
        {
            if (IsSimpleName(name))
            {
                return parent + "." + name;
            }
            return parent + "[\"" + name
                .Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"]";
        }

        private static bool IsSimpleName(string name)
        {
            if (name.Length == 0)
            {
                return false;
            }
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                var ok = c == '_' ||
                    (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                    (i > 0 && c >= '0' && c <= '9');
                if (!ok)
                {
                    return false;
                }
            }
            return true;
        }

        // Maps the parsed root object onto WorkflowDocumentData, collecting the
        // serializer's own diagnostics (OW-WF-001/-002/-006/-008) with positions.
        // Missing required structure is left for WorkflowDocument.Create (OW-WF-003).
        private sealed class Mapper
        {
            private readonly List<Diagnostic> _diagnostics = new List<Diagnostic>();
            private readonly Dictionary<string, (int Line, int Column)> _positions;
            private readonly bool _strict;
            private readonly HashSet<string> _nodeIds = new HashSet<string>(StringComparer.Ordinal);

            public Mapper(bool strict, Dictionary<string, (int Line, int Column)> positions)
            {
                _strict = strict;
                _positions = positions;
            }

            public List<Diagnostic> Diagnostics => _diagnostics;

            public bool HasErrors
            {
                get
                {
                    foreach (var diagnostic in _diagnostics)
                    {
                        if (diagnostic.Severity == Severity.Error)
                        {
                            return true;
                        }
                    }
                    return false;
                }
            }

            public WorkflowDocumentData MapRoot(JObject root)
            {
                var data = new WorkflowDocumentData();
                Dictionary<string, JToken>? extensions = null;
                JToken? schemaVersionToken = null;

                // SPEC §A8 drops layout.nodes entries for nonexistent nodes;
                // gather the node ids before the member walk so a "layout"
                // member that precedes "nodes" keeps its positions.
                CollectNodeIds(root["nodes"]);

                foreach (var property in root.Properties())
                {
                    var name = property.Name;
                    var value = property.Value;
                    var rawPath = "$." + name;
                    var memberPath = MemberPath("$", name);
                    switch (name)
                    {
                        case "schemaVersion":
                            schemaVersionToken = value;
                            data.SchemaVersion = OptionalString(value, rawPath, memberPath);
                            break;
                        case "id":
                            data.Id = OptionalString(value, rawPath, memberPath);
                            break;
                        case "title":
                            data.Title = OptionalString(value, rawPath, memberPath);
                            break;
                        case "description":
                            data.Description = OptionalString(value, rawPath, memberPath);
                            break;
                        case "tags":
                            data.Tags = MapStringArray(value, rawPath, memberPath);
                            break;
                        case "inputs":
                            data.Inputs = MapInputs(value, rawPath, memberPath);
                            break;
                        case "outputs":
                            data.Outputs = MapOutputs(value, rawPath, memberPath);
                            break;
                        case "nodes":
                            data.Nodes = MapNodes(value, rawPath, memberPath);
                            break;
                        case "controlEdges":
                            data.ControlEdges = MapControlEdges(value, rawPath, memberPath);
                            break;
                        case "dataEdges":
                            data.DataEdges = MapDataEdges(value, rawPath, memberPath);
                            break;
                        case "inputBindings":
                            data.InputBindings = MapInputBindings(value, rawPath, memberPath);
                            break;
                        case "layout":
                            data.Layout = MapLayout(value, rawPath, memberPath);
                            break;
                        default:
                            if (name.StartsWith("x-", StringComparison.Ordinal))
                            {
                                (extensions ??= new Dictionary<string, JToken>(StringComparer.Ordinal))
                                    [name] = value;
                            }
                            else
                            {
                                UnknownMember(name, rawPath, memberPath);
                            }
                            break;
                    }
                }
                data.Extensions = extensions;

                CheckSchemaVersion(schemaVersionToken);
                return data;
            }

            // Fills _nodeIds from the "nodes" array so the SPEC §A8 layout
            // filter sees every node id independently of member order. Uses
            // the same acceptance as MapNodes: object elements with a string
            // "id" member.
            private void CollectNodeIds(JToken? token)
            {
                if (token is not JArray array)
                {
                    return;
                }
                foreach (var element in array)
                {
                    if (element is JObject obj &&
                        obj["id"] is { Type: JTokenType.String } idToken)
                    {
                        _nodeIds.Add((string)((JValue)idToken).Value!);
                    }
                }
            }

            // Re-bases a WorkflowDocument.Create diagnostic onto the source: same
            // code, severity, path and fields, with the source position of the
            // member it names (or its nearest recorded ancestor) appended.
            public void Adopt(Diagnostic diagnostic)
            {
                var (line, column) = Pos(diagnostic.JsonPath ?? "$");
                _diagnostics.Add(new Diagnostic(
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.Message.TrimEnd('.') +
                        " (line " + line.ToString(CultureInfo.InvariantCulture) +
                        ", column " + column.ToString(CultureInfo.InvariantCulture) + ").",
                    diagnostic.NodeId,
                    diagnostic.Port,
                    diagnostic.JsonPath,
                    diagnostic.Hint,
                    diagnostic.Retryable));
            }

            private (int Line, int Column) Pos(string rawPath)
            {
                var path = rawPath;
                while (true)
                {
                    if (_positions.TryGetValue(path, out var position))
                    {
                        return position;
                    }
                    var cut = Math.Max(path.LastIndexOf('.'), path.LastIndexOf('['));
                    if (cut <= 0)
                    {
                        return (1, 0);
                    }
                    path = path.Substring(0, cut);
                }
            }

            private void Add(
                string code,
                string reason,
                string rawPath,
                string memberPath,
                Severity? severity = null,
                string? hint = null)
            {
                var (line, column) = Pos(rawPath);
                _diagnostics.Add(ErrorCodes.Create(
                    code,
                    WithPosition(reason, line, column),
                    jsonPath: memberPath,
                    hint: hint,
                    severity: severity));
            }

            private void Missing(string rawPath, string memberPath) =>
                Add(ErrorCodes.OW_WF_003,
                    "Missing required structure '" + memberPath + "'", rawPath, memberPath);

            private void KindError(string rawPath, string memberPath, string expected) =>
                Add(ErrorCodes.OW_WF_001,
                    "Member '" + memberPath + "' must be " + expected, rawPath, memberPath);

            // SPEC §A3 item 2: unknown structural properties warn, or error
            // under strict. The reserved-"$" Error of SPEC §A5 applies only to
            // objects inside parameter values (CheckParameterValue), not to
            // structural members that happen to start with "$".
            private void UnknownMember(string name, string rawPath, string memberPath) =>
                Add(ErrorCodes.OW_WF_006,
                    "Unknown property '" + name + "'",
                    rawPath,
                    memberPath,
                    severity: _strict ? Severity.Error : (Severity?)null);

            private string? OptionalString(JToken token, string rawPath, string memberPath)
            {
                switch (token.Type)
                {
                    case JTokenType.Null:
                        return null;
                    case JTokenType.String:
                        return (string?)((JValue)token).Value;
                    default:
                        KindError(rawPath, memberPath, "a string");
                        return null;
                }
            }

            private int? OptionalInt(JToken token, string rawPath, string memberPath)
            {
                switch (token.Type)
                {
                    case JTokenType.Null:
                        return null;
                    case JTokenType.Integer:
                        var value = ToInt64((JValue)token);
                        if (value < int.MinValue || value > int.MaxValue)
                        {
                            KindError(rawPath, memberPath, "an Int32-range integer");
                            return null;
                        }
                        return (int)value;
                    default:
                        KindError(rawPath, memberPath, "an integer");
                        return null;
                }
            }

            private double? OptionalNumber(JToken token, string rawPath, string memberPath)
            {
                switch (token.Type)
                {
                    case JTokenType.Null:
                        return null;
                    case JTokenType.Integer:
                        return ToInt64((JValue)token);
                    case JTokenType.Float:
                        return (double)((JValue)token).Value!;
                    default:
                        KindError(rawPath, memberPath, "a number");
                        return null;
                }
            }

            private bool? OptionalBool(JToken token, string rawPath, string memberPath)
            {
                switch (token.Type)
                {
                    case JTokenType.Null:
                        return null;
                    case JTokenType.Boolean:
                        return (bool)((JValue)token).Value!;
                    default:
                        KindError(rawPath, memberPath, "a boolean");
                        return null;
                }
            }

            private List<string>? MapStringArray(JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JArray array)
                {
                    KindError(rawPath, memberPath, "an array");
                    return null;
                }
                var list = new List<string>(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    var elementRaw = rawPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var elementPath = memberPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    switch (element.Type)
                    {
                        case JTokenType.Null:
                            list.Add(null!);
                            break;
                        case JTokenType.String:
                            list.Add((string)((JValue)element).Value!);
                            break;
                        default:
                            KindError(elementRaw, elementPath, "a string");
                            list.Add(null!);
                            break;
                    }
                }
                return list;
            }

            private List<WorkflowInput>? MapInputs(JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JArray array)
                {
                    KindError(rawPath, memberPath, "an array");
                    return null;
                }
                var list = new List<WorkflowInput>(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    var elementRaw = rawPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var elementPath = memberPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (element.Type == JTokenType.Null)
                    {
                        list.Add(null!);
                        continue;
                    }
                    if (element is not JObject obj)
                    {
                        KindError(elementRaw, elementPath, "an object");
                        list.Add(null!);
                        continue;
                    }
                    string? id = null;
                    string? type = null;
                    bool required = false;
                    var requiredSupplied = false;
                    JsonValue? defaultValue = null;
                    string? description = null;
                    foreach (var member in obj.Properties())
                    {
                        var memberRaw = elementRaw + "." + member.Name;
                        var memberName = MemberPath(elementPath, member.Name);
                        switch (member.Name)
                        {
                            case "id":
                                id = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            case "type":
                                type = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            case "required":
                                requiredSupplied = member.Value.Type != JTokenType.Null;
                                if (member.Value.Type == JTokenType.Boolean)
                                {
                                    required = (bool)((JValue)member.Value).Value!;
                                }
                                else if (member.Value.Type != JTokenType.Null)
                                {
                                    KindError(memberRaw, memberName, "a boolean");
                                }
                                break;
                            case "default":
                                defaultValue = JsonValue.From(member.Value);
                                break;
                            case "description":
                                description = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            default:
                                UnknownMember(member.Name, memberRaw, memberName);
                                break;
                        }
                    }
                    if (!requiredSupplied)
                    {
                        Missing(elementRaw + ".required", elementPath + ".required");
                    }
                    list.Add(new WorkflowInput(id!, type!, required, defaultValue, description));
                }
                return list;
            }

            private List<WorkflowOutput>? MapOutputs(JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JArray array)
                {
                    KindError(rawPath, memberPath, "an array");
                    return null;
                }
                var list = new List<WorkflowOutput>(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    var elementRaw = rawPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var elementPath = memberPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (element.Type == JTokenType.Null)
                    {
                        list.Add(null!);
                        continue;
                    }
                    if (element is not JObject obj)
                    {
                        KindError(elementRaw, elementPath, "an object");
                        list.Add(null!);
                        continue;
                    }
                    string? id = null;
                    PortRef? from = null;
                    string? description = null;
                    foreach (var member in obj.Properties())
                    {
                        var memberRaw = elementRaw + "." + member.Name;
                        var memberName = MemberPath(elementPath, member.Name);
                        switch (member.Name)
                        {
                            case "id":
                                id = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            case "from":
                                from = MapPortRef(member.Value, memberRaw, memberName);
                                break;
                            case "description":
                                description = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            default:
                                UnknownMember(member.Name, memberRaw, memberName);
                                break;
                        }
                    }
                    list.Add(new WorkflowOutput(id!, from!, description));
                }
                return list;
            }

            private List<NodeData>? MapNodes(JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JArray array)
                {
                    KindError(rawPath, memberPath, "an array");
                    return null;
                }
                if (array.Count > MaxNodes)
                {
                    var overflowRaw = rawPath + "[" +
                        MaxNodes.ToString(CultureInfo.InvariantCulture) + "]";
                    Add(ErrorCodes.OW_WF_008,
                        "The workflow has " + array.Count.ToString(CultureInfo.InvariantCulture) +
                        " nodes, which exceeds the maximum of " +
                        MaxNodes.ToString(CultureInfo.InvariantCulture),
                        overflowRaw,
                        memberPath + "[" + MaxNodes.ToString(CultureInfo.InvariantCulture) + "]");
                }
                var list = new List<NodeData>(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    var elementRaw = rawPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var elementPath = memberPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (element.Type == JTokenType.Null)
                    {
                        list.Add(null!);
                        continue;
                    }
                    if (element is not JObject obj)
                    {
                        KindError(elementRaw, elementPath, "an object");
                        list.Add(null!);
                        continue;
                    }
                    list.Add(MapNode(obj, elementRaw, elementPath));
                }
                return list;
            }

            private NodeData MapNode(JObject obj, string rawPath, string memberPath)
            {
                var node = new NodeData();
                var versionSupplied = false;
                foreach (var member in obj.Properties())
                {
                    var memberRaw = rawPath + "." + member.Name;
                    var memberName = MemberPath(memberPath, member.Name);
                    switch (member.Name)
                    {
                        case "id":
                            node.Id = OptionalString(member.Value, memberRaw, memberName);
                            break;
                        case "operation":
                            node.Operation = OptionalString(member.Value, memberRaw, memberName);
                            break;
                        case "version":
                            versionSupplied = member.Value.Type != JTokenType.Null;
                            var version = OptionalInt(member.Value, memberRaw, memberName);
                            if (version is not null)
                            {
                                node.Version = version.Value;
                            }
                            break;
                        case "parameters":
                            node.Parameters = MapParameters(member.Value, memberRaw, memberName);
                            break;
                        case "label":
                            node.Label = OptionalString(member.Value, memberRaw, memberName);
                            break;
                        case "policy":
                            node.Policy = MapPolicy(member.Value, memberRaw, memberName);
                            break;
                        default:
                            UnknownMember(member.Name, memberRaw, memberName);
                            break;
                    }
                }
                if (!versionSupplied)
                {
                    Missing(rawPath + ".version", memberPath + ".version");
                }
                return node;
            }

            private Dictionary<string, JToken>? MapParameters(
                JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JObject obj)
                {
                    KindError(rawPath, memberPath, "an object");
                    return null;
                }
                CheckParameterValue(obj, rawPath, memberPath);
                var map = new Dictionary<string, JToken>(StringComparer.Ordinal);
                foreach (var property in obj.Properties())
                {
                    map[property.Name] = property.Value;
                }
                return map;
            }

            // SPEC §A5: objects inside parameters that carry a "$"-prefixed key —
            // including the parameters object itself — are reserved for future
            // expressions and are OW-WF-006 errors. SPEC §0.6 SG-13: a single string
            // parameter over 64 KiB (UTF-8) is OW-WF-008; string scalars nested in
            // parameter values count, so the check is recursive.
            private void CheckParameterValue(JToken token, string rawPath, string memberPath)
            {
                switch (token)
                {
                    case JObject obj:
                        foreach (var property in obj.Properties())
                        {
                            var memberRaw = rawPath + "." + property.Name;
                            var memberName = MemberPath(memberPath, property.Name);
                            if (property.Name.StartsWith("$", StringComparison.Ordinal))
                            {
                                Add(ErrorCodes.OW_WF_006,
                                    "Member '" + property.Name +
                                    "' uses the reserved \"$\" prefix",
                                    memberRaw,
                                    memberName,
                                    severity: Severity.Error);
                            }
                            CheckParameterValue(property.Value, memberRaw, memberName);
                        }
                        break;
                    case JArray array:
                        for (var i = 0; i < array.Count; i++)
                        {
                            var index = i.ToString(CultureInfo.InvariantCulture);
                            CheckParameterValue(
                                array[i], rawPath + "[" + index + "]", memberPath + "[" + index + "]");
                        }
                        break;
                    case JValue value when value.Type == JTokenType.String:
                        var text = (string)value.Value!;
                        if (s_utf8.GetByteCount(text) > MaxStringParameterBytes)
                        {
                            Add(ErrorCodes.OW_WF_008,
                                "A string parameter exceeds the maximum length of " +
                                MaxStringParameterBytes.ToString(CultureInfo.InvariantCulture) +
                                " UTF-8 bytes",
                                rawPath,
                                memberPath);
                        }
                        break;
                }
            }

            private NodePolicy? MapPolicy(JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JObject obj)
                {
                    KindError(rawPath, memberPath, "an object");
                    return null;
                }
                int? timeoutSeconds = null;
                int? maxAttempts = null;
                foreach (var member in obj.Properties())
                {
                    var memberRaw = rawPath + "." + member.Name;
                    var memberName = MemberPath(memberPath, member.Name);
                    switch (member.Name)
                    {
                        case "timeoutSeconds":
                            timeoutSeconds = OptionalInt(member.Value, memberRaw, memberName);
                            break;
                        case "maxAttempts":
                            maxAttempts = OptionalInt(member.Value, memberRaw, memberName);
                            break;
                        default:
                            UnknownMember(member.Name, memberRaw, memberName);
                            break;
                    }
                }
                return new NodePolicy(timeoutSeconds, maxAttempts);
            }

            private List<ControlEdge>? MapControlEdges(JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JArray array)
                {
                    KindError(rawPath, memberPath, "an array");
                    return null;
                }
                var list = new List<ControlEdge>(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    var elementRaw = rawPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var elementPath = memberPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (element.Type == JTokenType.Null)
                    {
                        list.Add(null!);
                        continue;
                    }
                    if (element is not JObject obj)
                    {
                        KindError(elementRaw, elementPath, "an object");
                        list.Add(null!);
                        continue;
                    }
                    string? from = null;
                    string? to = null;
                    var fromPort = "next";
                    foreach (var member in obj.Properties())
                    {
                        var memberRaw = elementRaw + "." + member.Name;
                        var memberName = MemberPath(elementPath, member.Name);
                        switch (member.Name)
                        {
                            case "from":
                                from = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            case "to":
                                to = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            case "fromPort":
                                if (member.Value.Type == JTokenType.String)
                                {
                                    fromPort = (string)((JValue)member.Value).Value!;
                                }
                                else if (member.Value.Type != JTokenType.Null)
                                {
                                    KindError(memberRaw, memberName, "a string");
                                }
                                break;
                            default:
                                UnknownMember(member.Name, memberRaw, memberName);
                                break;
                        }
                    }
                    list.Add(new ControlEdge(from!, to!, fromPort));
                }
                return list;
            }

            private List<DataEdge>? MapDataEdges(JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JArray array)
                {
                    KindError(rawPath, memberPath, "an array");
                    return null;
                }
                var list = new List<DataEdge>(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    var elementRaw = rawPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var elementPath = memberPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (element.Type == JTokenType.Null)
                    {
                        list.Add(null!);
                        continue;
                    }
                    if (element is not JObject obj)
                    {
                        KindError(elementRaw, elementPath, "an object");
                        list.Add(null!);
                        continue;
                    }
                    PortRef? from = null;
                    PortRef? to = null;
                    foreach (var member in obj.Properties())
                    {
                        var memberRaw = elementRaw + "." + member.Name;
                        var memberName = MemberPath(elementPath, member.Name);
                        switch (member.Name)
                        {
                            case "from":
                                from = MapPortRef(member.Value, memberRaw, memberName);
                                break;
                            case "to":
                                to = MapPortRef(member.Value, memberRaw, memberName);
                                break;
                            default:
                                UnknownMember(member.Name, memberRaw, memberName);
                                break;
                        }
                    }
                    list.Add(new DataEdge(from!, to!));
                }
                return list;
            }

            private List<InputBinding>? MapInputBindings(
                JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JArray array)
                {
                    KindError(rawPath, memberPath, "an array");
                    return null;
                }
                var list = new List<InputBinding>(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    var elementRaw = rawPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var elementPath = memberPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (element.Type == JTokenType.Null)
                    {
                        list.Add(null!);
                        continue;
                    }
                    if (element is not JObject obj)
                    {
                        KindError(elementRaw, elementPath, "an object");
                        list.Add(null!);
                        continue;
                    }
                    string? fromInput = null;
                    PortRef? to = null;
                    foreach (var member in obj.Properties())
                    {
                        var memberRaw = elementRaw + "." + member.Name;
                        var memberName = MemberPath(elementPath, member.Name);
                        switch (member.Name)
                        {
                            case "fromInput":
                                fromInput = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            case "to":
                                to = MapPortRef(member.Value, memberRaw, memberName);
                                break;
                            default:
                                UnknownMember(member.Name, memberRaw, memberName);
                                break;
                        }
                    }
                    list.Add(new InputBinding(fromInput!, to!));
                }
                return list;
            }

            private PortRef? MapPortRef(JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JObject obj)
                {
                    KindError(rawPath, memberPath, "an object");
                    return null;
                }
                string? node = null;
                string? port = null;
                foreach (var member in obj.Properties())
                {
                    var memberRaw = rawPath + "." + member.Name;
                    var memberName = MemberPath(memberPath, member.Name);
                    switch (member.Name)
                    {
                        case "node":
                            node = OptionalString(member.Value, memberRaw, memberName);
                            break;
                        case "port":
                            port = OptionalString(member.Value, memberRaw, memberName);
                            break;
                        default:
                            UnknownMember(member.Name, memberRaw, memberName);
                            break;
                    }
                }
                return new PortRef(node!, port!);
            }

            private LayoutInfo? MapLayout(JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JObject obj)
                {
                    KindError(rawPath, memberPath, "an object");
                    return null;
                }
                IReadOnlyDictionary<string, LayoutNodePosition>? nodes = null;
                IReadOnlyList<LayoutNote>? notes = null;
                foreach (var member in obj.Properties())
                {
                    var memberRaw = rawPath + "." + member.Name;
                    var memberName = MemberPath(memberPath, member.Name);
                    switch (member.Name)
                    {
                        case "nodes":
                            nodes = MapLayoutNodes(member.Value, memberRaw, memberName);
                            break;
                        case "notes":
                            notes = MapLayoutNotes(member.Value, memberRaw, memberName);
                            break;
                        default:
                            UnknownMember(member.Name, memberRaw, memberName);
                            break;
                    }
                }
                return new LayoutInfo(nodes!, notes!);
            }

            private IReadOnlyDictionary<string, LayoutNodePosition>? MapLayoutNodes(
                JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JObject obj)
                {
                    KindError(rawPath, memberPath, "an object");
                    return null;
                }
                var map = new Dictionary<string, LayoutNodePosition>(StringComparer.Ordinal);
                foreach (var member in obj.Properties())
                {
                    // SPEC §A8: layout entries for nodes that do not exist are
                    // dropped silently while reading — the node-id check runs
                    // before the entry's contents are mapped or validated, so
                    // a stale entry produces no diagnostic of any kind.
                    if (!_nodeIds.Contains(member.Name))
                    {
                        continue;
                    }
                    var memberRaw = rawPath + "." + member.Name;
                    var memberName = MemberPath(memberPath, member.Name);
                    if (member.Value.Type == JTokenType.Null)
                    {
                        map[member.Name] = null!;
                        continue;
                    }
                    if (member.Value is not JObject position)
                    {
                        KindError(memberRaw, memberName, "an object");
                        continue;
                    }
                    double? x = null;
                    double? y = null;
                    foreach (var coordinate in position.Properties())
                    {
                        var coordinateRaw = memberRaw + "." + coordinate.Name;
                        var coordinatePath = MemberPath(memberName, coordinate.Name);
                        switch (coordinate.Name)
                        {
                            case "x":
                                x = OptionalNumber(coordinate.Value, coordinateRaw, coordinatePath);
                                break;
                            case "y":
                                y = OptionalNumber(coordinate.Value, coordinateRaw, coordinatePath);
                                break;
                            default:
                                UnknownMember(coordinate.Name, coordinateRaw, coordinatePath);
                                break;
                        }
                    }
                    if (x is null)
                    {
                        Missing(memberRaw + ".x", memberName + ".x");
                    }
                    if (y is null)
                    {
                        Missing(memberRaw + ".y", memberName + ".y");
                    }
                    map[member.Name] = x is not null && y is not null
                        ? new LayoutNodePosition(x.Value, y.Value)
                        : null!;
                }
                return map;
            }

            private IReadOnlyList<LayoutNote>? MapLayoutNotes(
                JToken token, string rawPath, string memberPath)
            {
                if (token.Type == JTokenType.Null)
                {
                    return null;
                }
                if (token is not JArray array)
                {
                    KindError(rawPath, memberPath, "an array");
                    return null;
                }
                var list = new List<LayoutNote>(array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    var element = array[i];
                    var elementRaw = rawPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var elementPath = memberPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    if (element.Type == JTokenType.Null)
                    {
                        list.Add(null!);
                        continue;
                    }
                    if (element is not JObject obj)
                    {
                        KindError(elementRaw, elementPath, "an object");
                        list.Add(null!);
                        continue;
                    }
                    string? id = null;
                    double? x = null;
                    double? y = null;
                    string? text = null;
                    foreach (var member in obj.Properties())
                    {
                        var memberRaw = elementRaw + "." + member.Name;
                        var memberName = MemberPath(elementPath, member.Name);
                        switch (member.Name)
                        {
                            case "id":
                                id = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            case "x":
                                x = OptionalNumber(member.Value, memberRaw, memberName);
                                break;
                            case "y":
                                y = OptionalNumber(member.Value, memberRaw, memberName);
                                break;
                            case "text":
                                text = OptionalString(member.Value, memberRaw, memberName);
                                break;
                            default:
                                UnknownMember(member.Name, memberRaw, memberName);
                                break;
                        }
                    }
                    if (x is null)
                    {
                        Missing(elementRaw + ".x", elementPath + ".x");
                    }
                    if (y is null)
                    {
                        Missing(elementRaw + ".y", elementPath + ".y");
                    }
                    list.Add(x is not null && y is not null
                        ? new LayoutNote(id!, x.Value, y.Value, text!)
                        : null!);
                }
                return list;
            }

            // SPEC §A7/§N.3: the rule itself lives in SchemaVersionCheck so the
            // D2 structural phase emits the identical diagnostic for a
            // programmatically built document (SPEC §D2); the serializer only
            // re-bases the message onto the member's source position.
            private void CheckSchemaVersion(JToken? token)
            {
                if (token is null || token.Type != JTokenType.String)
                {
                    return;
                }
                var text = (string)((JValue)token).Value!;
                var diagnostic = SchemaVersionCheck.Check(text);
                if (diagnostic is null)
                {
                    return;
                }
                const string path = "$.schemaVersion";
                var (line, column) = Pos(path);
                _diagnostics.Add(diagnostic with
                {
                    Message = WithPosition(diagnostic.Message.TrimEnd('.'), line, column),
                });
            }

            private static long ToInt64(JValue token) =>
                token.Value is BigInteger bigInteger
                    ? (long)bigInteger
                    : Convert.ToInt64(token.Value, CultureInfo.InvariantCulture);
        }
    }
}
