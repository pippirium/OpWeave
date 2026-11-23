#nullable enable
// T-W2.10-04: compiled-assembly reference inspection for the structural
// tests (SPEC §0.5 test, §H2 item 7 approach). Reads each compiled
// assembly's own metadata reference table
// (System.Reflection.Assembly.GetReferencedAssemblies) — the references the
// built binary actually carries. The Unity compilation model is not used:
// under the testables flag Unity injects test-framework references into
// every testable package assembly's compiler inputs, so the compiler
// argument list would report references the produced DLL does not carry.
// The T-W3.3-03 structural test reuses this helper for the Unity.Pipeline*
// prohibition.
using System;
using System.Collections.Generic;
using System.Reflection;

namespace OpWeave.UnityAdapter.Tests
{
    /// <summary>
    /// Reads the compiled reference table of a loaded assembly (SPEC §H2
    /// item 7 approach).
    /// </summary>
    internal static class AssemblyReferenceInspector
    {
        /// <summary>
        /// Returns the loaded assembly with the given simple name, loading it
        /// on demand; null when no such assembly exists in this project — an
        /// unmet <c>defineConstraints</c> or a missing package produces no
        /// loadable assembly.
        /// </summary>
        internal static Assembly? FindAssembly(string assemblyName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }
            try
            {
                return Assembly.Load(new AssemblyName(assemblyName));
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Simple names of every assembly <paramref name="assembly"/>
        /// references, read from its compiled metadata reference table
        /// ("OpWeave.Testing", "nunit.framework", "Mono.Cecil").
        /// </summary>
        internal static IReadOnlyList<string> ReferencedAssemblyNames(Assembly assembly)
        {
            var names = new List<string>();
            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                if (!string.IsNullOrEmpty(reference.Name))
                {
                    names.Add(reference.Name!);
                }
            }
            return names;
        }
    }
}
