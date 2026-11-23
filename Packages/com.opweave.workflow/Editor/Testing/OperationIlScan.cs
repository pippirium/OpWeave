#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Mono.Cecil;

namespace OpWeave.Testing
{
    /// <summary>
    /// The static inspection of SPEC §M.6 item 3: a Mono.Cecil scan of an
    /// Operation assembly's IL that reports every reference to an API the
    /// SPEC §G0 common prohibitions forbid in any Operation, standard or
    /// custom — modal dialogs, <c>AssetDatabase.StartAssetEditing</c> and
    /// <c>Task.Run</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan is a warning, not a verdict: it reads the compiled assembly
    /// the way §G0 inspection does — the IL on disk, never the loaded
    /// reflection image — and reports each prohibited member reference with
    /// the type and method that holds it, so a contract test can turn the
    /// findings into a readable failure.
    /// </para>
    /// <para>
    /// The prohibited member table names the entries §G0 spells out —
    /// <c>EditorUtility.DisplayDialog</c>,
    /// <c>EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo</c>,
    /// <c>AssetDatabase.StartAssetEditing</c> and
    /// <c>System.Threading.Tasks.Task.Run</c> — plus
    /// <c>EditorUtility.DisplayDialogComplex</c>, which is the same modal
    /// dialog the "DisplayDialog など" wording covers. Matching is by the
    /// member's declaring-type name and member name, so an overload,
    /// generic instantiation or a delegate's <c>ldftn</c> reference is
    /// caught the same way a <c>call</c> is.
    /// </para>
    /// </remarks>
    public static class OperationIlScan
    {
        private const string ModalDialogReason =
            "Operations must not open modal dialogs (SPEC §G0)";

        private const string DeferredImportReason =
            "Operations must not defer asset imports (SPEC §G0)";

        private const string BackgroundThreadReason =
            "Operations must not run work on a Task.Run thread (SPEC §G0)";

        // (declaring type full name, member name, reported API name, reason).
        private static readonly (string DeclaringType, string Member, string Api, string Reason)[]
            ProhibitedMembers =
            {
                ("UnityEditor.EditorUtility", "DisplayDialog",
                    "UnityEditor.EditorUtility.DisplayDialog", ModalDialogReason),
                ("UnityEditor.EditorUtility", "DisplayDialogComplex",
                    "UnityEditor.EditorUtility.DisplayDialogComplex", ModalDialogReason),
                ("UnityEditor.SceneManagement.EditorSceneManager",
                    "SaveCurrentModifiedScenesIfUserWantsTo",
                    "UnityEditor.SceneManagement.EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo",
                    ModalDialogReason),
                ("UnityEditor.AssetDatabase", "StartAssetEditing",
                    "UnityEditor.AssetDatabase.StartAssetEditing", DeferredImportReason),
                ("System.Threading.Tasks.Task", "Run",
                    "System.Threading.Tasks.Task.Run", BackgroundThreadReason),
            };

        /// <summary>
        /// Scans the IL of <paramref name="assembly"/>'s compiled file for
        /// references to the prohibited members and returns one
        /// <see cref="IlScanWarning"/> per (API, type, method) found — an
        /// empty list for a clean assembly.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="assembly"/> is null.</exception>
        /// <exception cref="ArgumentException">
        /// <paramref name="assembly"/> has no on-disk location — for example a
        /// dynamically emitted assembly; the IL scan needs a compiled file.
        /// </exception>
        public static IReadOnlyList<IlScanWarning> Scan(Assembly assembly)
        {
            if (assembly is null)
            {
                throw new ArgumentNullException(nameof(assembly));
            }
            var location = assembly.Location;
            if (string.IsNullOrEmpty(location))
            {
                throw new ArgumentException(
                    $"The assembly '{assembly.GetName().Name}' has no on-disk " +
                    "location to scan.", nameof(assembly));
            }
            return Scan(location);
        }

        /// <summary>
        /// Scans the compiled assembly at <paramref name="assemblyPath"/>
        /// for references to the prohibited members and returns one
        /// <see cref="IlScanWarning"/> per (API, type, method) found — an
        /// empty list for a clean assembly.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// <paramref name="assemblyPath"/> is null, empty or names no
        /// existing file.
        /// </exception>
        public static IReadOnlyList<IlScanWarning> Scan(string assemblyPath)
        {
            if (string.IsNullOrEmpty(assemblyPath))
            {
                throw new ArgumentException(
                    "The assembly path must not be null or empty.", nameof(assemblyPath));
            }
            if (!File.Exists(assemblyPath))
            {
                throw new ArgumentException(
                    $"The assembly file '{assemblyPath}' does not exist.", nameof(assemblyPath));
            }
            using var definition = AssemblyDefinition.ReadAssembly(assemblyPath);
            var warnings = new List<IlScanWarning>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            ScanTypes(definition.MainModule.Types, warnings, seen);
            return warnings;
        }

        // Walks a type list and each type's nested types; Mono.Cecil.Rocks
        // is not among the referenced precompiled assemblies, so the
        // flattening is done by hand.
        private static void ScanTypes(
            IEnumerable<TypeDefinition> types,
            List<IlScanWarning> warnings,
            HashSet<string> seen)
        {
            foreach (var type in types)
            {
                ScanType(type, warnings, seen);
                if (type.HasNestedTypes)
                {
                    ScanTypes(type.NestedTypes, warnings, seen);
                }
            }
        }

        private static void ScanType(
            TypeDefinition type, List<IlScanWarning> warnings, HashSet<string> seen)
        {
            if (!type.HasMethods)
            {
                return;
            }
            foreach (var method in type.Methods)
            {
                if (!method.HasBody)
                {
                    continue;
                }
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MethodReference called &&
                        TryMatch(called, out var api, out var reason) &&
                        seen.Add(api + "\n" + type.FullName + "\n" + method.Name))
                    {
                        warnings.Add(new IlScanWarning(
                            api, type.FullName, method.Name, reason));
                    }
                }
            }
        }

        private static bool TryMatch(
            MethodReference called, out string api, out string reason)
        {
            foreach (var (declaringType, member, prohibitedApi, prohibitedReason) in
                ProhibitedMembers)
            {
                if (string.Equals(called.Name, member, StringComparison.Ordinal) &&
                    string.Equals(
                        called.DeclaringType.FullName, declaringType, StringComparison.Ordinal))
                {
                    api = prohibitedApi;
                    reason = prohibitedReason;
                    return true;
                }
            }
            api = string.Empty;
            reason = string.Empty;
            return false;
        }
    }
}
