#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;

namespace OpWeave.Core.Operations
{
    /// <summary>
    /// The SPEC §C2 item 1 class check every Operation candidate must pass
    /// before registration: marked with
    /// <see cref="OpWeaveOperationAttribute"/>, non-abstract, a public
    /// parameterless constructor, and an <see cref="IOperation"/>
    /// implementation. Each violated condition and each instantiation failure
    /// yields OW-OP-008 (SPEC §C3 step 6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Check"/> inspects type metadata only — it never invokes a
    /// constructor, matching the discovery stage that screens candidates
    /// before instantiation (SPEC §C3 step 2). <see cref="Instantiate"/>
    /// combines the same check with actual construction so a constructor
    /// that throws is reported as OW-OP-008 instead of escaping the layer
    /// boundary as an exception (SPEC §0.4).
    /// </para>
    /// <para>
    /// Diagnostics are collected in a fixed order (attribute, abstract,
    /// constructor, interface) so the same candidate always reports the same
    /// list.
    /// </para>
    /// </remarks>
    public static class OperationEligibility
    {
        /// <summary>
        /// Reads <paramref name="type"/>'s <see cref="OpWeaveOperationAttribute"/>
        /// — the declared (Id, Version) identity of SPEC §C2 item 2 — without
        /// constructing the class (SPEC §C3 step 2). Returns null when the
        /// class carries no attribute; the absence itself is the OW-OP-008
        /// condition <see cref="Check"/> reports.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="type"/> is null.
        /// </exception>
        public static OpWeaveOperationAttribute? DeclaredIdentityOf(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }
            return Attribute.GetCustomAttribute(
                type, typeof(OpWeaveOperationAttribute), inherit: false)
                as OpWeaveOperationAttribute;
        }

        /// <summary>
        /// Inspects <paramref name="type"/>'s metadata against SPEC §C2 item 1
        /// without constructing it. Returns an empty list when the class is
        /// eligible; otherwise one OW-OP-008 diagnostic per violated
        /// condition.
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="type"/> is null.
        /// </exception>
        public static IReadOnlyList<Diagnostic> Check(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            var diagnostics = new List<Diagnostic>();
            var name = type.FullName ?? type.Name;

            if (DeclaredIdentityOf(type) is null)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_008,
                    $"Operation class '{name}' is missing [OpWeaveOperation]."));
            }
            if (type.IsAbstract)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_008,
                    $"Operation class '{name}' is abstract."));
            }
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_008,
                    $"Operation class '{name}' does not declare a public parameterless constructor."));
            }
            if (!typeof(IOperation).IsAssignableFrom(type))
            {
                diagnostics.Add(ErrorCodes.Create(
                    ErrorCodes.OW_OP_008,
                    $"Operation class '{name}' does not implement IOperation."));
            }
            return diagnostics;
        }

        /// <summary>
        /// Runs <see cref="Check"/> and, when the class is eligible, invokes
        /// its public parameterless constructor. A constructor that throws —
        /// or any other failure to produce an <see cref="IOperation"/>
        /// instance — is reported as OW-OP-008 rather than escaping as an
        /// exception (SPEC §C2 item 1, §C3 step 6). The diagnostic names only
        /// the thrown exception's type; the exception message may carry
        /// credentials or source text and is never copied (SPEC §C3).
        /// </summary>
        /// <exception cref="ArgumentNullException">
        /// <paramref name="type"/> is null.
        /// </exception>
        public static Result<IOperation> Instantiate(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            var diagnostics = Check(type);
            if (diagnostics.Count != 0)
            {
                return Result<IOperation>.Failure(diagnostics);
            }

            var name = type.FullName ?? type.Name;
            try
            {
                var instance = (IOperation)Activator.CreateInstance(type)!;
                return Result<IOperation>.Success(instance);
            }
            catch (Exception exception)
            {
                var detail =
                    exception is TargetInvocationException && exception.InnerException is not null
                        ? exception.InnerException
                        : exception;
                return Result<IOperation>.Failure(ErrorCodes.Create(
                    ErrorCodes.OW_OP_008,
                    $"Operation class '{name}' could not be instantiated: {detail.GetType().Name}."));
            }
        }
    }
}
