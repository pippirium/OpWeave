#nullable enable

using System;
using OpWeave.Core.Compiler;

namespace OpWeave.UnityAdapter
{
    /// <summary>
    /// The production <see cref="IStaticTypeProbe"/> of SPEC §D6 item 4 and
    /// §G1 item 4: answers whether the current domain's type space knows a
    /// fully qualified type name — for a Descriptor's <c>type_exists</c>
    /// requirement at validation (D6) and at run time (E4 item 2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// SPEC describes the production probe as TypeCache-backed, but TypeCache
    /// exposes no name lookup on Unity 6000.0 — only attribute/derivation
    /// enumeration. The probe therefore resolves the name against every
    /// assembly of the current AppDomain, which indexes the same loaded type
    /// space and additionally covers interfaces, structs and enums (a local,
    /// reversible choice). A type defined by code an earlier node of the same
    /// Run generated is absent until that code compiles and the domain
    /// reloads — after the reload it is found, which is exactly the runtime
    /// re-check SPEC §D6 item 4 (OW-VAL-015) mandates.
    /// </para>
    /// </remarks>
    public sealed class UnityTypeProbe : IStaticTypeProbe
    {
        private readonly Func<string, bool> _typeExists;

        /// <summary>The live probe over the domain's loaded assemblies.</summary>
        public UnityTypeProbe()
            : this(static name => ResolveLoadedType(name) is not null)
        {
        }

        /// <summary>
        /// The probe over a caller-supplied predicate — the test seam (the
        /// <see cref="EditorStateProbe"/> pattern): a fixture drives the answer
        /// without touching the loaded type space.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="typeExists"/> is null.</exception>
        internal UnityTypeProbe(Func<string, bool> typeExists)
        {
            _typeExists = typeExists ?? throw new ArgumentNullException(nameof(typeExists));
        }

        /// <inheritdoc/>
        /// <remarks>
        /// A null or empty name, or a name the type space fails to resolve,
        /// names no existing type: the answer is a definitive "not found" —
        /// never "unknown" (<see cref="IStaticTypeProbe.TypeExists"/>).
        /// </remarks>
        public bool TypeExists(string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
            {
                return false;
            }
            try
            {
                return _typeExists(typeName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Resolution order: Type.GetType first (covers mscorlib types and an
        // assembly-qualified spelling), then every loaded assembly by full
        // name — the same domain type space TypeCache enumerates.
        private static Type? ResolveLoadedType(string typeName)
        {
            var direct = Type.GetType(typeName, throwOnError: false);
            if (direct is not null)
            {
                return direct;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type? found = null;
                try
                {
                    found = assembly.GetType(typeName, throwOnError: false);
                }
                catch (Exception)
                {
                    // A dynamic or partially loaded assembly may fail a name
                    // lookup; it simply has no answer for this name.
                }
                if (found is not null)
                {
                    return found;
                }
            }
            return null;
        }
    }
}
