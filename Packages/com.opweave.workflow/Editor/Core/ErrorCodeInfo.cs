#nullable enable

namespace OpWeave.Core
{
    /// <summary>
    /// One published <c>OW-&lt;AREA&gt;-&lt;NNN&gt;</c> diagnostic code of SPEC §J/§J.1 with the
    /// defaults from its table row. Where the table cell is conditional, the conservative
    /// default documented on <see cref="ErrorCodes"/> is stored and emitters override it.
    /// </summary>
    public sealed class ErrorCodeInfo
    {
        internal ErrorCodeInfo(string code, Severity defaultSeverity, bool defaultRetryable)
        {
            Code = code;
            DefaultSeverity = defaultSeverity;
            DefaultRetryable = defaultRetryable;
        }

        /// <summary>The code string, e.g. "OW-VAL-005".</summary>
        public string Code { get; }

        /// <summary>The default <see cref="Diagnostic.Severity"/> for this code.</summary>
        public Severity DefaultSeverity { get; }

        /// <summary>The default <see cref="Diagnostic.Retryable"/> for this code.</summary>
        public bool DefaultRetryable { get; }
    }
}
