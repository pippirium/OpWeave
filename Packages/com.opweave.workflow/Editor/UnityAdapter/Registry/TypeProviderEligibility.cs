#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using OpWeave.Core;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The TypeProvider counterpart of <see cref="OpWeave.Core.Operations.OperationEligibility"/>:
    /// the SPEC §C0.1 / §X.6 class contract — marked with
    /// <see cref="OpWeaveTypeProviderAttribute"/>, non-abstract, a public
    /// parameterless constructor, and an <see cref="ITypeProvider"/>
    /// implementation. Violations and construction failures become
    /// OW-OP-019 at snapshot publication (T-W2.12-03); the records this
    /// stage produces stay internal (empty code, fixed English), matching
    /// the deferred OW-OP-013..019 mapping boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="Check"/> inspects type metadata only — it never invokes a
    /// constructor, matching the SPEC §C3 step-2 screen. <see cref="Instantiate"/>
    /// combines the same check with actual construction so a throwing
    /// constructor is reported as a diagnostic instead of escaping the
    /// layer boundary as an exception (SPEC §0.4).
    /// </remarks>
    internal static class TypeProviderEligibility
    {
        /// <summary>
        /// Inspects <paramref name="type"/>'s metadata against the provider
        /// contract without constructing it. Returns an empty list when the
        /// class is eligible; otherwise one internal diagnostic per violated
        /// condition (attribute, abstract, constructor, interface — fixed
        /// order).
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
        internal static IReadOnlyList<Diagnostic> Check(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            var diagnostics = new List<Diagnostic>();
            var name = type.FullName ?? type.Name;

            if (Attribute.GetCustomAttribute(
                    type, typeof(OpWeaveTypeProviderAttribute), inherit: false) is null)
            {
                diagnostics.Add(Internal(
                    $"TypeProvider class '{name}' is missing [OpWeaveTypeProvider]."));
            }
            if (type.IsAbstract)
            {
                diagnostics.Add(Internal(
                    $"TypeProvider class '{name}' is abstract."));
            }
            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                diagnostics.Add(Internal(
                    $"TypeProvider class '{name}' does not declare a public parameterless constructor."));
            }
            if (!typeof(ITypeProvider).IsAssignableFrom(type))
            {
                diagnostics.Add(Internal(
                    $"TypeProvider class '{name}' does not implement ITypeProvider."));
            }
            return diagnostics;
        }

        /// <summary>
        /// Runs <see cref="Check"/> and, when the class is eligible, invokes
        /// its public parameterless constructor. A throwing constructor —
        /// or any other failure to produce an <see cref="ITypeProvider"/> —
        /// becomes an internal diagnostic record rather than an escaping
        /// exception (SPEC §C3 step 4 → OW-OP-019 at publication). Only the
        /// exception's type name is reported; its message may carry secrets.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="type"/> is null.</exception>
        internal static Result<ITypeProvider> Instantiate(Type type)
        {
            if (type is null)
            {
                throw new ArgumentNullException(nameof(type));
            }

            var diagnostics = Check(type);
            if (diagnostics.Count != 0)
            {
                return Result<ITypeProvider>.Failure(diagnostics);
            }

            var name = type.FullName ?? type.Name;
            try
            {
                var instance = (ITypeProvider)Activator.CreateInstance(type)!;
                return Result<ITypeProvider>.Success(instance);
            }
            catch (Exception exception)
            {
                var detail =
                    exception is TargetInvocationException && exception.InnerException is not null
                        ? exception.InnerException
                        : exception;
                return Result<ITypeProvider>.Failure(Internal(
                    $"TypeProvider class '{name}' could not be instantiated: {detail.GetType().Name}."));
            }
        }

        // Every rejection of this contract is OW-OP-019 (SPEC §J.1:
        // "TypeProviderクラスを生成できない") — T-W2.12-03 assigned the code.
        private static Diagnostic Internal(string message) =>
            ErrorCodes.Create(ErrorCodes.OW_OP_019, message);
    }
}
