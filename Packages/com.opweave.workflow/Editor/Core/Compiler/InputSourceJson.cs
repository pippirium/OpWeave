#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Compiler
{
    /// <summary>
    /// The SG-12 tagged canonical encoding of an <see cref="InputSource"/>
    /// (SPEC §0.6 SG-12, §A0.1): each source is a JSON object whose
    /// <c>"kind"</c> discriminator is the §0.4 snake_case name —
    /// <c>{"kind":"missing"}</c>, <c>{"kind":"constant","value":&lt;json&gt;}</c>,
    /// <c>{"kind":"default","value":&lt;json&gt;}</c>,
    /// <c>{"kind":"from_step","stepIndex":n,"port":&lt;string&gt;}</c> and
    /// <c>{"kind":"from_input","inputId":&lt;string&gt;}</c>. This is the
    /// persisted form embedded in plan.json and in the planHash semantic input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Write"/> produces a <see cref="JsonValue"/>, so the tag's bytes
    /// are always the Canonical JSON form — <c>"kind"</c> ordering and the
    /// §0.3 key order fall out of the single serializer. A literal-null
    /// <see cref="Constant"/> and <see cref="Missing"/> therefore hash
    /// differently, as SPEC §A0.1 requires.
    /// </para>
    /// <para>
    /// <see cref="Read"/> accepts only the five approved shapes: any other
    /// <c>kind</c>, an extra member or a missing required member fails the
    /// decode. Like the SG-02 low-level reader, failures are returned as
    /// internal diagnostics — an empty <see cref="Diagnostic.Code"/>, a
    /// fixed-English payload-free message and a JsonPath — inside a failed
    /// <see cref="Result{T}"/>; the owning layer (plan.json / journal loading)
    /// maps them to OW-STORE-002 exactly once. A present <c>"value"</c> member
    /// that is JSON <c>null</c> is valid and decodes to a <see cref="Constant"/>
    /// or <see cref="Default"/> of explicit null, never to <see cref="Missing"/>.
    /// </para>
    /// </remarks>
    public static class InputSourceJson
    {
        /// <summary>
        /// Encodes <paramref name="source"/> as its SG-12 tagged
        /// <see cref="JsonValue"/>.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="source"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="source"/> is outside the SG-12 persisted contract — a
        /// negative <see cref="FromStep.StepIndex"/>, a null
        /// <see cref="FromStep.Port"/>/<see cref="FromInput.InputId"/>/
        /// <see cref="Constant.Value"/>/<see cref="Default.Value"/>, or an
        /// <see cref="InputSource"/> subclass outside the five approved kinds.
        /// </exception>
        public static JsonValue Write(InputSource source)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }
            var tag = new JObject();
            switch (source)
            {
                case Missing _:
                    tag["kind"] = "missing";
                    break;
                case Constant constant:
                    tag["kind"] = "constant";
                    tag["value"] = RequiredValue(constant.Value, source);
                    break;
                case Default @default:
                    tag["kind"] = "default";
                    tag["value"] = RequiredValue(@default.Value, source);
                    break;
                case FromStep fromStep:
                    if (fromStep.StepIndex < 0)
                    {
                        throw new ArgumentException(
                            "FromStep.StepIndex must be a non-negative integer.", nameof(source));
                    }
                    tag["kind"] = "from_step";
                    tag["stepIndex"] = fromStep.StepIndex;
                    tag["port"] = RequiredString(fromStep.Port, "FromStep.Port");
                    break;
                case FromInput fromInput:
                    tag["kind"] = "from_input";
                    tag["inputId"] = RequiredString(fromInput.InputId, "FromInput.InputId");
                    break;
                default:
                    throw new ArgumentException(
                        "InputSource kind is outside the SG-12 persisted set.", nameof(source));
            }
            return JsonValue.From(tag);
        }

        /// <summary>
        /// Decodes a tag previously written by <see cref="Write"/>. Any other
        /// <c>kind</c>, an extra member or a missing/mistyped required member
        /// fails with an internal diagnostic for the owning layer to map once.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="json"/> is null.</exception>
        public static Result<InputSource> Read(JsonValue json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }
            if (json.ToToken() is not JObject tag)
            {
                return Fail("An InputSource tag must be a JSON object.", "$");
            }

            var kindToken = tag["kind"];
            if (kindToken is null)
            {
                return Fail("An InputSource tag has no 'kind' member.", "$");
            }
            var kind = (kindToken as JValue)?.Value as string;
            if (kind is null)
            {
                return Fail("An InputSource tag 'kind' member is not a JSON string.", "$.*");
            }

            switch (kind)
            {
                case "missing":
                    if (!HasOnly(tag, "kind"))
                    {
                        return Fail(
                            "A 'missing' InputSource tag has a member besides 'kind'.", "$");
                    }
                    return Result<InputSource>.Success(new Missing());

                case "constant":
                case "default":
                {
                    if (!HasOnly(tag, "kind", "value"))
                    {
                        return Fail(
                            "A '" + kind + "' InputSource tag has a member besides 'kind' and 'value'.",
                            "$");
                    }
                    var value = tag["value"];
                    if (value is null)
                    {
                        return Fail(
                            "A '" + kind + "' InputSource tag has no 'value' member.", "$");
                    }
                    var jsonValue = JsonValue.From(value);
                    return Result<InputSource>.Success(
                        kind == "constant"
                            ? (InputSource)new Constant(jsonValue)
                            : new Default(jsonValue));
                }

                case "from_step":
                {
                    if (!HasOnly(tag, "kind", "stepIndex", "port"))
                    {
                        return Fail(
                            "A 'from_step' InputSource tag has a member besides 'kind', 'stepIndex' and 'port'.",
                            "$");
                    }
                    var stepIndex = tag["stepIndex"];
                    if (stepIndex is null)
                    {
                        return Fail("A 'from_step' InputSource tag has no 'stepIndex' member.", "$");
                    }
                    if (stepIndex is not JValue stepIndexValue ||
                        stepIndexValue.Type != JTokenType.Integer ||
                        stepIndexValue.Value is not long index ||
                        index < 0 || index > int.MaxValue)
                    {
                        return Fail(
                            "A 'from_step' InputSource tag 'stepIndex' member is not a non-negative integer.",
                            "$.*");
                    }
                    var port = tag["port"];
                    if (port is null)
                    {
                        return Fail("A 'from_step' InputSource tag has no 'port' member.", "$");
                    }
                    var portName = (port as JValue)?.Value as string;
                    if (portName is null)
                    {
                        return Fail(
                            "A 'from_step' InputSource tag 'port' member is not a JSON string.", "$.*");
                    }
                    return Result<InputSource>.Success(new FromStep((int)index, portName));
                }

                case "from_input":
                {
                    if (!HasOnly(tag, "kind", "inputId"))
                    {
                        return Fail(
                            "A 'from_input' InputSource tag has a member besides 'kind' and 'inputId'.",
                            "$");
                    }
                    var inputId = tag["inputId"];
                    if (inputId is null)
                    {
                        return Fail("A 'from_input' InputSource tag has no 'inputId' member.", "$");
                    }
                    var inputIdText = (inputId as JValue)?.Value as string;
                    if (inputIdText is null)
                    {
                        return Fail(
                            "A 'from_input' InputSource tag 'inputId' member is not a JSON string.",
                            "$.*");
                    }
                    return Result<InputSource>.Success(new FromInput(inputIdText));
                }

                default:
                    return Fail("An InputSource tag has an unknown 'kind'.", "$.*");
            }
        }

        // The tag's member names must be a subset of the approved set: a decoded
        // member outside it — its name never echoed into a diagnostic — fails.
        // Required-member presence is checked per kind afterwards.
        private static bool HasOnly(JObject tag, params string[] members)
        {
            var allowed = new HashSet<string>(members, StringComparer.Ordinal);
            foreach (var property in tag.Properties())
            {
                if (!allowed.Contains(property.Name))
                {
                    return false;
                }
            }
            return true;
        }

        private static JToken RequiredValue(JsonValue value, InputSource source) =>
            (value ?? throw new ArgumentException(
                "The InputSource value must not be null.", nameof(source))).ToToken();

        private static string RequiredString(string? value, string member) =>
            value ?? throw new ArgumentException(member + " must not be null.", "source");

        // Internal diagnostic convention of the SG-02 reader (SPEC §0.6 SG-02):
        // empty code, payload-free fixed English, JsonPath; the owning layer
        // maps to OW-STORE-002 exactly once before publishing.
        private static Result<InputSource> Fail(string reason, string path) =>
            Result<InputSource>.Failure(
                new Diagnostic(string.Empty, Severity.Error, reason, JsonPath: path));
    }
}
