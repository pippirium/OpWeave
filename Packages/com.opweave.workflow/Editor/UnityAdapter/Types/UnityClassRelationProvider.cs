#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using UnityEditor;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The Editor-side <see cref="IClassRelationProvider"/> of SPEC §B2
    /// (T-W2.1-05): judges class-id relations the Unity-independent Core
    /// snapshot cannot decide — the <c>type:&lt;fully qualified name&gt;</c> form by
    /// probing the Editor's loaded assemblies (TypeCache in Unity 6000.0 has
    /// no by-name lookup) and the §B5 built-in short class names via a fixed
    /// CLR table, then <see cref="Type.IsAssignableFrom"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsSubclass"/> answers <see langword="true"/> when
    /// <paramref name="classId"/> is a subclass of (or equal to, or an
    /// implementor of) <paramref name="baseClassId"/> —
    /// <see cref="Type.IsAssignableFrom"/> semantics — and
    /// <see langword="null"/> when either class ID cannot be resolved to a
    /// loaded CLR type at all. The registry's assignability treats a null
    /// verdict as not assignable with the "unresolved class" reason (SPEC
    /// §B6 rule 4): an unresolvable ID is never guessed.
    /// </para>
    /// <para>
    /// The built-in table maps each §B5 short class ID to its CLR type
    /// directly — MonoScript and AnimatorController are UnityEditor classes,
    /// which is also why the IDs do not resolve by a fixed
    /// <c>UnityEngine.</c> prefix. The table is the adapter-side mirror of
    /// the class definitions in <see cref="UnityShapeTypes"/>; the EditMode
    /// tests assert every <see cref="TypeKind.Class"/> definition resolves
    /// here, so a class ID added to Core without the table fails loudly.
    /// </para>
    /// </remarks>
    public sealed class UnityClassRelationProvider : IClassRelationProvider
    {
        private const string ClassRefPrefix = "type:";

        private static readonly IReadOnlyDictionary<string, Type> s_builtInClasses =
            new Dictionary<string, Type>(StringComparer.Ordinal)
            {
                ["unity.Object"] = typeof(UnityEngine.Object),
                ["unity.Component"] = typeof(UnityEngine.Component),
                ["unity.GameObject"] = typeof(UnityEngine.GameObject),
                ["unity.Transform"] = typeof(UnityEngine.Transform),
                ["unity.Animator"] = typeof(UnityEngine.Animator),
                ["unity.Material"] = typeof(UnityEngine.Material),
                ["unity.Texture2D"] = typeof(UnityEngine.Texture2D),
                ["unity.MonoScript"] = typeof(MonoScript),
                ["unity.ScriptableObject"] = typeof(UnityEngine.ScriptableObject),
                ["unity.AnimatorController"] = typeof(UnityEditor.Animations.AnimatorController),
            };

        /// <inheritdoc/>
        public bool? IsSubclass(string classId, string baseClassId)
        {
            var type = ResolveClass(classId);
            var baseType = ResolveClass(baseClassId);
            if (type is null || baseType is null)
            {
                return null;
            }
            return baseType.IsAssignableFrom(type);
        }

        private static Type? ResolveClass(string? classId)
        {
            if (classId is null || classId.Length == 0)
            {
                return null;
            }
            if (classId.StartsWith(ClassRefPrefix, StringComparison.Ordinal))
            {
                return ResolveNamedType(classId.Substring(ClassRefPrefix.Length));
            }
            return s_builtInClasses.TryGetValue(classId, out var type) ? type : null;
        }

        /// <summary>
        /// Resolves a SPEC §B5 class-id string — a built-in short id
        /// (<c>unity.Animator</c>), <c>type:&lt;fully qualified name&gt;</c> or
        /// a bare fully qualified name — to the loaded CLR type, or
        /// <see langword="null"/> when no loaded type matches. The G2
        /// resolver uses this for the ComponentRef <c>type</c> key, whose
        /// Codec grammar (<c>UnityShapePatterns.ClassId</c>) accepts all
        /// three spellings.
        /// </summary>
        internal static Type? ResolveClassId(string? classId)
        {
            var resolved = ResolveClass(classId);
            if (resolved is null &&
                classId is not null &&
                !classId.StartsWith(ClassRefPrefix, StringComparison.Ordinal) &&
                !s_builtInClasses.ContainsKey(classId))
            {
                resolved = ResolveNamedType(classId);
            }
            return resolved;
        }

        /// <summary>
        /// The class-id spelling of a CLR type for the ComponentRef
        /// <c>type</c> key: the built-in short id when the type is one of the
        /// SPEC §B5 built-ins, otherwise <c>type:&lt;full name&gt;</c>.
        /// </summary>
        internal static string ClassIdOf(Type type)
        {
            foreach (var pair in s_builtInClasses)
            {
                if (pair.Value == type)
                {
                    return pair.Key;
                }
            }
            return ClassRefPrefix + type.FullName;
        }

        // A type:<name> id resolves as the exact CLR FullName against every
        // assembly loaded in the Editor domain — the same loaded-type data
        // TypeCache is built on, which 6000.0 simply does not expose by name.
        // A name no assembly defines, or one more than a single assembly
        // defines, is unresolvable and yields null (never a guessed type).
        private static Type? ResolveNamedType(string fullName)
        {
            Type? resolved = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? candidate;
                try
                {
                    candidate = assembly.GetType(fullName, throwOnError: false);
                }
                catch (Exception)
                {
                    // A dynamic or otherwise unqueryable assembly cannot
                    // answer the probe; it simply does not define the name.
                    continue;
                }
                if (candidate is null)
                {
                    continue;
                }
                if (resolved is not null && resolved != candidate)
                {
                    return null;
                }
                resolved = candidate;
            }
            return resolved;
        }
    }
}
