#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The <c>descriptorHash</c> of SPEC §C3 rule 6 and §0.6 SG-12: the
    /// lowercase-hex SHA-256 of the Descriptor's Canonical JSON. The input is
    /// every definition field of the §C1 record — camelCase members and
    /// snake_case enum values (SPEC §0.4) — and nothing else: no SourceFile, no
    /// instance state, no Registry generation number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Optional members serialize as explicit <c>null</c>, except
    /// <see cref="JsonValue"/>-typed members (<see cref="PortDescriptor.Default"/>,
    /// <see cref="OperationExample.FixtureManifest"/>), which are omitted when
    /// the reference is null: a descriptor whose port has <em>no</em> default and
    /// one whose port default is the explicit JSON <c>null</c> are different
    /// contracts and must hash differently (SPEC §A0, §0.6 SG-12).
    /// </para>
    /// </remarks>
    public static class DescriptorHash
    {
        /// <summary>
        /// The lowercase-hex SHA-256 of <paramref name="descriptor"/>'s Canonical
        /// JSON (SPEC §0.6 SG-12).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
        public static string Compute(OperationDescriptor descriptor)
        {
            if (descriptor is null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }
            return CanonicalJson.Sha256(ToToken(descriptor));
        }

        // The canonical JSON projection of the record: camelCase member names,
        // snake_case enum values (SPEC §0.4). JsonValue members embed their own
        // canonical form by cloning the held token.
        internal static JToken ToToken(OperationDescriptor descriptor)
        {
            var root = new JObject
            {
                ["id"] = descriptor.Id,
                ["version"] = descriptor.Version,
                ["kind"] = JsonNaming.ToSnakeCase(descriptor.Kind.ToString()),
                ["displayName"] = descriptor.DisplayName,
                ["summary"] = descriptor.Summary,
                ["description"] = descriptor.Description,
                ["localized"] = LocalizedToken(descriptor.Localized),
                ["category"] = descriptor.Category,
                ["tags"] = StringArray(descriptor.Tags),
                ["keywords"] = StringArray(descriptor.Keywords),
                ["inputs"] = PortArray(descriptor.Inputs),
                ["outputs"] = PortArray(descriptor.Outputs),
                ["controlOutputs"] = StringArray(descriptor.ControlOutputs),
                ["executionMode"] = JsonNaming.ToSnakeCase(descriptor.ExecutionMode.ToString()),
                ["sideEffects"] = StringArray(descriptor.SideEffects),
                ["destructive"] = descriptor.Destructive,
                ["undo"] = JsonNaming.ToSnakeCase(descriptor.Undo.ToString()),
                ["idempotency"] = JsonNaming.ToSnakeCase(descriptor.Idempotency.ToString()),
                ["defaultRetry"] = RetryToken(descriptor.DefaultRetry),
                ["defaultTimeoutSeconds"] = descriptor.DefaultTimeoutSeconds,
                ["requirements"] = RequirementArray(descriptor.Requirements),
                ["outputRefinement"] = RefinementToken(descriptor.OutputRefinement),
                ["examples"] = ExampleArray(descriptor.Examples),
            };
            return root;
        }

        private static JToken LocalizedToken(
            IReadOnlyDictionary<string, LocalizedText> localized)
        {
            var obj = new JObject();
            foreach (var pair in localized)
            {
                obj[pair.Key] = new JObject
                {
                    ["displayName"] = pair.Value.DisplayName,
                    ["summary"] = pair.Value.Summary,
                    ["keywords"] = StringArray(pair.Value.Keywords),
                };
            }
            return obj;
        }

        private static JToken PortToken(PortDescriptor port)
        {
            var obj = new JObject
            {
                ["name"] = port.Name,
                ["type"] = port.Type,
                ["required"] = port.Required,
            };
            // A JsonValue member is omitted when the reference is null so an
            // absent default stays byte-distinct from an explicit JSON null
            // default (SPEC §A0).
            if (port.Default is not null)
            {
                obj["default"] = port.Default.ToToken();
            }
            obj["description"] = port.Description;
            obj["constraints"] = ConstraintsToken(port.Constraints);
            obj["nullable"] = port.Nullable;
            return obj;
        }

        private static JToken ConstraintsToken(PortConstraints? constraints)
        {
            if (constraints is null)
            {
                return JValue.CreateNull();
            }
            return new JObject
            {
                ["enum"] = constraints.Enum is null
                    ? JValue.CreateNull()
                    : (JToken)StringArray(constraints.Enum),
                ["pattern"] = constraints.Pattern is null ? JValue.CreateNull() : new JValue(constraints.Pattern),
                ["min"] = constraints.Min.HasValue ? new JValue(constraints.Min.Value) : JValue.CreateNull(),
                ["max"] = constraints.Max.HasValue ? new JValue(constraints.Max.Value) : JValue.CreateNull(),
                ["maxLength"] = constraints.MaxLength.HasValue
                    ? new JValue(constraints.MaxLength.Value)
                    : JValue.CreateNull(),
                ["constantOnly"] = constraints.ConstantOnly,
            };
        }

        private static JToken RetryToken(RetryPolicy retry) =>
            new JObject
            {
                ["maxAttempts"] = retry.MaxAttempts,
                ["initialBackoffMs"] = retry.InitialBackoffMs,
                ["multiplier"] = retry.Multiplier,
                ["maxBackoffMs"] = retry.MaxBackoffMs,
            };

        private static JToken RequirementToken(Requirement requirement) =>
            new JObject
            {
                ["kind"] = requirement.Kind,
                ["port"] = requirement.Port is null
                    ? JValue.CreateNull()
                    : new JValue(requirement.Port),
            };

        private static JToken RefinementToken(TypeRefinement? refinement)
        {
            if (refinement is null)
            {
                return JValue.CreateNull();
            }
            return new JObject
            {
                ["fromParameter"] = refinement.FromParameter,
                ["outputPort"] = refinement.OutputPort,
                ["template"] = refinement.Template,
            };
        }

        private static JToken ExampleToken(OperationExample example)
        {
            var obj = new JObject
            {
                ["title"] = example.Title,
                ["workflowJson"] = example.WorkflowJson.ToToken(),
            };
            if (example.FixtureManifest is not null)
            {
                obj["fixtureManifest"] = example.FixtureManifest.ToToken();
            }
            return obj;
        }

        private static JArray StringArray(IEnumerable<string> values)
        {
            var array = new JArray();
            foreach (var value in values)
            {
                array.Add(value);
            }
            return array;
        }

        private static JArray PortArray(IReadOnlyList<PortDescriptor> ports)
        {
            var array = new JArray();
            for (var i = 0; i < ports.Count; i++)
            {
                array.Add(PortToken(ports[i]));
            }
            return array;
        }

        private static JArray RequirementArray(IReadOnlyList<Requirement> requirements)
        {
            var array = new JArray();
            for (var i = 0; i < requirements.Count; i++)
            {
                array.Add(RequirementToken(requirements[i]));
            }
            return array;
        }

        private static JArray ExampleArray(IReadOnlyList<OperationExample> examples)
        {
            var array = new JArray();
            for (var i = 0; i < examples.Count; i++)
            {
                array.Add(ExampleToken(examples[i]));
            }
            return array;
        }
    }
}
