#nullable enable

namespace OpWeave.Testing
{
    /// <summary>
    /// One finding of <see cref="OperationIlScan"/> (SPEC §M.6 item 3): a
    /// method in the scanned Operation assembly that references an API the
    /// SPEC §G0 common prohibitions forbid — a modal dialog,
    /// <c>AssetDatabase.StartAssetEditing</c> or <c>Task.Run</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A warning names the prohibited reference (<see cref="Api"/>) and the
    /// place that holds it (<see cref="TypeName"/>.<see cref="MethodName"/>).
    /// One method is reported once per prohibited API it touches — calling
    /// the same API twice inside one method is still one warning, so a
    /// report line always points at a type and method rather than at an
    /// instruction index.
    /// </para>
    /// </remarks>
    public sealed class IlScanWarning
    {
        public IlScanWarning(string api, string typeName, string methodName, string reason)
        {
            Api = api;
            TypeName = typeName;
            MethodName = methodName;
            Reason = reason;
        }

        /// <summary>
        /// The fully qualified prohibited member the method references, for
        /// example <c>UnityEditor.EditorUtility.DisplayDialog</c>.
        /// </summary>
        public string Api { get; }

        /// <summary>
        /// The full name of the type that declares the calling method, in
        /// metadata form (<c>/</c> separates a nested type from its parent).
        /// </summary>
        public string TypeName { get; }

        /// <summary>The name of the method that references <see cref="Api"/>.</summary>
        public string MethodName { get; }

        /// <summary>
        /// The SPEC §G0 prohibition the reference breaks — the modal-dialog
        /// rule, the deferred-import rule or the background-thread rule.
        /// </summary>
        public string Reason { get; }

        public override string ToString() =>
            $"{TypeName}.{MethodName} references {Api} — {Reason}";
    }
}
