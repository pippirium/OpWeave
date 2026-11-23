#nullable enable

using System;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// Narrows an output port's type from a constant parameter (SPEC §C1):
    /// <see cref="FromParameter"/> names the input port carrying the type name,
    /// <see cref="OutputPort"/> the output port to refine, and
    /// <see cref="Template"/> is the Type ID template with a <c>{0}</c> hole
    /// (e.g. <c>"unity.ComponentRef&lt;{0}&gt;"</c>). Each check runs in both
    /// the backing-field initializer and the <c>init</c> accessor so record
    /// <c>with</c> expressions cannot bypass it.
    /// </summary>
    public sealed record TypeRefinement(string FromParameter, string OutputPort, string Template)
    {
        private readonly string _fromParameter = CheckFromParameter(FromParameter);
        private readonly string _outputPort = CheckOutputPort(OutputPort);
        private readonly string _template = CheckTemplate(Template);

        /// <summary>The input port whose constant value selects the refined type.</summary>
        public string FromParameter
        {
            get => _fromParameter;
            init => _fromParameter = CheckFromParameter(value);
        }

        /// <summary>The output port this refinement applies to.</summary>
        public string OutputPort
        {
            get => _outputPort;
            init => _outputPort = CheckOutputPort(value);
        }

        /// <summary>The Type ID template containing a <c>{0}</c> placeholder.</summary>
        public string Template
        {
            get => _template;
            init => _template = CheckTemplate(value);
        }

        private static string CheckFromParameter(string fromParameter) =>
            string.IsNullOrEmpty(fromParameter)
                ? throw new ArgumentException(
                    "Type refinement source parameter must not be null or empty.",
                    nameof(FromParameter))
                : fromParameter;

        private static string CheckOutputPort(string outputPort) =>
            string.IsNullOrEmpty(outputPort)
                ? throw new ArgumentException(
                    "Type refinement output port must not be null or empty.",
                    nameof(OutputPort))
                : outputPort;

        private static string CheckTemplate(string template) =>
            string.IsNullOrEmpty(template)
                ? throw new ArgumentException(
                    "Type refinement template must not be null or empty.", nameof(Template))
                : template;
    }
}
