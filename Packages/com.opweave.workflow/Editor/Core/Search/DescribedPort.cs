#nullable enable

using System;
using OpWeave.Core.Operations;

namespace OpWeave.Core.Search
{
    /// <summary>
    /// One port of a described Operation (SPEC §C8): the declared
    /// <see cref="Operations.PortDescriptor"/> together with the §B8 JSON
    /// Schema fragment <see cref="TypeSchemas"/> generated for its port
    /// position — constraints, description and the Nullable flag applied,
    /// referenced types carried in <c>$defs</c>.
    /// </summary>
    public sealed record DescribedPort(PortDescriptor Port, JsonValue Schema)
    {
        private readonly PortDescriptor _port =
            Port ?? throw new ArgumentNullException(nameof(Port));
        private readonly JsonValue _schema =
            Schema ?? throw new ArgumentNullException(nameof(Schema));

        /// <summary>The declared port contract.</summary>
        public PortDescriptor Port
        {
            get => _port;
            init => _port = value ?? throw new ArgumentNullException(nameof(Port));
        }

        /// <summary>The port's §B8 JSON Schema as an immutable JSON value.</summary>
        public JsonValue Schema
        {
            get => _schema;
            init => _schema = value ?? throw new ArgumentNullException(nameof(Schema));
        }
    }
}
