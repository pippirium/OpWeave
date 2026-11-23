#nullable enable

using System;
using System.Collections.Generic;

namespace OpWeave.Core
{
    /// <summary>
    /// The Unity-independent JSON-shape <see cref="TypeDefinition"/>s of the
    /// standard <c>unity.*</c> types — the string types of SPEC §B3, the value
    /// types of SPEC §B4, the built-in class IDs and Ref types of SPEC §B5 —
    /// each carrying its Core Codec (T-W1.4-05).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per SPEC §0.6 SG-10 these definitions and validators are usable without
    /// any Unity reference, but the <em>only</em> provider that registers them
    /// into the production Registry is the standard TypeProvider of
    /// OpWeave.UnityAdapter (T-W2.1-05), which builds its CLR conversion on
    /// these same shape validators. This type is deliberately not an
    /// <see cref="ITypeProvider"/>: contract tests and the adapter wrap
    /// <see cref="All"/> in their own providers, and a second registration of
    /// the same Type ID invalidates both candidates (SPEC §B0.1 → OW-OP-013).
    /// </para>
    /// <para>
    /// The built-in class IDs of SPEC §B5 (<c>unity.GameObject</c>,
    /// <c>unity.Animator</c>, …) are registered as <see cref="TypeKind.Class"/>
    /// definitions so Ref generic arguments such as
    /// <c>unity.ComponentRef&lt;unity.Animator&gt;</c> and the declared base
    /// <c>unity.AssetRef&lt;unity.GameObject&gt;</c> of
    /// <c>unity.PrefabAssetRef</c> resolve inside the snapshot. Their declared
    /// bases encode the Unity class relations Core knows statically;
    /// <c>type:&lt;FQN&gt;</c> class relations still go through
    /// <see cref="IClassRelationProvider"/> (SPEC §B6 rule 4). Class-id types
    /// carry no JSON values — their Codec rejects every value (OW-TYPE-003).
    /// </para>
    /// <para>
    /// The <c>kind</c> discriminator strings of SPEC §B5 are published here as
    /// constants; the SPEC fixes only <c>"gameObject"</c>, so the remaining
    /// spellings are the documented local choice of <see cref="UnityRefCodec"/>.
    /// </para>
    /// </remarks>
    public static class UnityShapeTypes
    {
        /// <summary>The <c>kind</c> discriminator of a <c>unity.SceneRef</c> value (SPEC §B5).</summary>
        public const string SceneKind = "scene";

        /// <summary>The <c>kind</c> discriminator of a <c>unity.GameObjectRef</c> value (SPEC §B5).</summary>
        public const string GameObjectKind = "gameObject";

        /// <summary>The <c>kind</c> discriminator of a <c>unity.PrefabInstanceRef</c> value (SPEC §B5).</summary>
        public const string PrefabInstanceKind = "prefabInstance";

        /// <summary>The <c>kind</c> discriminator of a <c>unity.ComponentRef&lt;T&gt;</c> value (SPEC §B5).</summary>
        public const string ComponentKind = "component";

        /// <summary>The <c>kind</c> discriminator of a <c>unity.AssetRef&lt;T&gt;</c> value (SPEC §B5).</summary>
        public const string AssetKind = "asset";

        /// <summary>The <c>kind</c> discriminator of a <c>unity.PrefabAssetRef</c> value (SPEC §B5).</summary>
        public const string PrefabAssetKind = "prefabAsset";

        private static readonly IReadOnlyList<TypeDefinition> s_all = CreateTypes();

        // The CLR fully qualified name of each §B5 built-in class ID. The
        // names are an explicit table, not a "UnityEngine." + short-name
        // derivation: MonoScript and AnimatorController are UnityEditor
        // classes, so prefixing every ID with UnityEngine. would leave
        // their real names unmapped and the class-id refinement of
        // DataBindingValidationPhase on the type: form (SPEC §C1).
        private static readonly IReadOnlyDictionary<string, string> s_builtInClassNames =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["unity.Object"] = "UnityEngine.Object",
                ["unity.Component"] = "UnityEngine.Component",
                ["unity.GameObject"] = "UnityEngine.GameObject",
                ["unity.Transform"] = "UnityEngine.Transform",
                ["unity.Animator"] = "UnityEngine.Animator",
                ["unity.Material"] = "UnityEngine.Material",
                ["unity.Texture2D"] = "UnityEngine.Texture2D",
                ["unity.MonoScript"] = "UnityEditor.MonoScript",
                ["unity.ScriptableObject"] = "UnityEngine.ScriptableObject",
                ["unity.AnimatorController"] = "UnityEditor.Animations.AnimatorController",
            };

        // CLR fully qualified name → §B5 built-in class ID; derived from the
        // TypeKind.Class definitions of s_all so every built-in class is
        // looked up in the table — a definition without an entry fails here.
        private static readonly IReadOnlyDictionary<string, string> s_builtInClassIds =
            CreateBuiltInClassIds();

        /// <summary>
        /// Every standard <c>unity.*</c> JSON-shape definition (SPEC §B3–§B5),
        /// as an immutable list. Registration into any Registry goes through
        /// <see cref="TypeRegistryBuilder"/>; the definitions themselves are
        /// not bound to a snapshot.
        /// </summary>
        public static IReadOnlyList<TypeDefinition> All => s_all;

        /// <summary>
        /// The SPEC §B5 built-in class ID for a CLR fully qualified class
        /// name (<c>"UnityEngine.Animator"</c> → <c>"unity.Animator"</c>);
        /// <c>null</c> when the name is not a built-in class. A constant
        /// <c>unity.TypeName</c> parameter uses it to refine an output to the
        /// short class ID (SPEC §C1: <c>unity.ComponentRef&lt;unity.Animator&gt;</c>),
        /// keeping the <c>type:</c> class-id form for arbitrary classes.
        /// </summary>
        internal static string? BuiltInClassId(string clrName) =>
            s_builtInClassIds.TryGetValue(clrName, out var id) ? id : null;

        private static IReadOnlyDictionary<string, string> CreateBuiltInClassIds()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var definition in s_all)
            {
                if (definition.Kind == TypeKind.Class)
                {
                    map.Add(s_builtInClassNames[definition.Id], definition.Id);
                }
            }
            return map;
        }

        private static IReadOnlyList<TypeDefinition> CreateTypes()
        {
            var noBases = Array.Empty<string>();
            var stringBase = new[] { "core.String" };
            return ReadOnlyCollections.CopyList(new[]
            {
                // ── SPEC §B3 string types (base core.String) ─────────────────
                new TypeDefinition(
                    "unity.AssetPath",
                    TypeKind.Primitive,
                    0,
                    stringBase,
                    null,
                    "Asset or package path ('Assets/…' or 'Packages/…', '/' separated).",
                    UnityStringCodec.AssetPath),
                new TypeDefinition(
                    "unity.TypeName",
                    TypeKind.Primitive,
                    0,
                    stringBase,
                    null,
                    "Fully qualified type name (e.g. 'UnityEngine.Animator').",
                    UnityStringCodec.TypeName),
                new TypeDefinition(
                    "unity.HierarchyPath",
                    TypeKind.Primitive,
                    0,
                    stringBase,
                    null,
                    "Scene hierarchy path ('/Root/Child').",
                    UnityStringCodec.HierarchyPath),

                // ── SPEC §B4 value types ──────────────────────────────────────
                new TypeDefinition(
                    "unity.Vector2",
                    TypeKind.Value,
                    0,
                    noBases,
                    null,
                    "Vector2 as {\"x\":0,\"y\":0}; components are Double positions.",
                    UnityVectorCodec.Vector2),
                new TypeDefinition(
                    "unity.Vector3",
                    TypeKind.Value,
                    0,
                    noBases,
                    null,
                    "Vector3 as {\"x\":0,\"y\":0,\"z\":0}; components are Double positions.",
                    UnityVectorCodec.Vector3),
                new TypeDefinition(
                    "unity.Vector4",
                    TypeKind.Value,
                    0,
                    noBases,
                    null,
                    "Vector4 as {\"x\":0,\"y\":0,\"z\":0,\"w\":0}; components are Double positions.",
                    UnityVectorCodec.Vector4),
                new TypeDefinition(
                    "unity.Quaternion",
                    TypeKind.Value,
                    0,
                    noBases,
                    null,
                    "Quaternion as {\"x\":0,\"y\":0,\"z\":0,\"w\":1}; components are Double positions.",
                    UnityVectorCodec.Quaternion),
                new TypeDefinition(
                    "unity.Euler",
                    TypeKind.Value,
                    0,
                    noBases,
                    null,
                    "Euler rotation in degrees as {\"x\":0,\"y\":90,\"z\":0}; a distinct type from unity.Vector3.",
                    UnityVectorCodec.Euler),
                new TypeDefinition(
                    "unity.Color",
                    TypeKind.Value,
                    0,
                    noBases,
                    null,
                    "Color as {\"r\":1,\"g\":1,\"b\":1,\"a\":1} or \"#RRGGBBAA\".",
                    UnityColorCodec.Instance),

                // ── SPEC §B5 built-in class IDs ───────────────────────────────
                new TypeDefinition(
                    "unity.Object",
                    TypeKind.Class,
                    0,
                    noBases,
                    null,
                    "Built-in class ID for UnityEngine.Object; carries no JSON value.",
                    UnityClassCodec.Instance),
                new TypeDefinition(
                    "unity.Component",
                    TypeKind.Class,
                    0,
                    new[] { "unity.Object" },
                    null,
                    "Built-in class ID for UnityEngine.Component; carries no JSON value.",
                    UnityClassCodec.Instance),
                new TypeDefinition(
                    "unity.GameObject",
                    TypeKind.Class,
                    0,
                    new[] { "unity.Object" },
                    null,
                    "Built-in class ID for UnityEngine.GameObject; carries no JSON value.",
                    UnityClassCodec.Instance),
                new TypeDefinition(
                    "unity.Transform",
                    TypeKind.Class,
                    0,
                    new[] { "unity.Component" },
                    null,
                    "Built-in class ID for UnityEngine.Transform; carries no JSON value.",
                    UnityClassCodec.Instance),
                new TypeDefinition(
                    "unity.Animator",
                    TypeKind.Class,
                    0,
                    new[] { "unity.Component" },
                    null,
                    "Built-in class ID for UnityEngine.Animator; carries no JSON value.",
                    UnityClassCodec.Instance),
                new TypeDefinition(
                    "unity.Material",
                    TypeKind.Class,
                    0,
                    new[] { "unity.Object" },
                    null,
                    "Built-in class ID for UnityEngine.Material; carries no JSON value.",
                    UnityClassCodec.Instance),
                new TypeDefinition(
                    "unity.Texture2D",
                    TypeKind.Class,
                    0,
                    new[] { "unity.Object" },
                    null,
                    "Built-in class ID for UnityEngine.Texture2D; carries no JSON value.",
                    UnityClassCodec.Instance),
                new TypeDefinition(
                    "unity.MonoScript",
                    TypeKind.Class,
                    0,
                    new[] { "unity.Object" },
                    null,
                    "Built-in class ID for UnityEditor.MonoScript; carries no JSON value.",
                    UnityClassCodec.Instance),
                new TypeDefinition(
                    "unity.ScriptableObject",
                    TypeKind.Class,
                    0,
                    new[] { "unity.Object" },
                    null,
                    "Built-in class ID for UnityEngine.ScriptableObject; carries no JSON value.",
                    UnityClassCodec.Instance),
                new TypeDefinition(
                    "unity.AnimatorController",
                    TypeKind.Class,
                    0,
                    new[] { "unity.Object" },
                    null,
                    "Built-in class ID for UnityEditor.Animations.AnimatorController; carries no JSON value.",
                    UnityClassCodec.Instance),

                // ── SPEC §B5 Ref types ────────────────────────────────────────
                new TypeDefinition(
                    "unity.ObjectRef",
                    TypeKind.Ref,
                    0,
                    noBases,
                    null,
                    "Reference to a Unity object; 'kind' discriminates the actual Ref type.",
                    UnityRefCodec.ObjectRef),
                new TypeDefinition(
                    "unity.SceneRef",
                    TypeKind.Ref,
                    0,
                    new[] { "unity.ObjectRef" },
                    null,
                    "Reference to a scene asset: kind 'scene', keys path, guid.",
                    UnityRefCodec.SceneRef),
                new TypeDefinition(
                    "unity.GameObjectRef",
                    TypeKind.Ref,
                    0,
                    new[] { "unity.ObjectRef" },
                    null,
                    "Reference to a scene GameObject: kind 'gameObject'/'prefabInstance', keys scene, path, globalId, handle.",
                    UnityRefCodec.GameObjectRef),
                new TypeDefinition(
                    "unity.PrefabInstanceRef",
                    TypeKind.Ref,
                    0,
                    new[] { "unity.GameObjectRef" },
                    null,
                    "Reference to a prefab instance root: kind 'prefabInstance', adds prefabAsset.",
                    UnityRefCodec.PrefabInstanceRef),
                new TypeDefinition(
                    "unity.ComponentRef<>",
                    TypeKind.Ref,
                    1,
                    new[] { "unity.ObjectRef" },
                    "unity.Component",
                    "Reference to a component on a GameObject: kind 'component', keys owner, type, index.",
                    UnityRefCodec.ComponentRef),
                new TypeDefinition(
                    "unity.AssetRef<>",
                    TypeKind.Ref,
                    1,
                    new[] { "unity.ObjectRef" },
                    "unity.Object",
                    "Reference to an asset of class T: kind 'asset'/'prefabAsset', keys guid, path, localId.",
                    UnityRefCodec.AssetRef),
                new TypeDefinition(
                    "unity.PrefabAssetRef",
                    TypeKind.Ref,
                    0,
                    new[] { "unity.AssetRef<unity.GameObject>" },
                    null,
                    "Reference to a prefab asset: kind 'prefabAsset', keys guid, path.",
                    UnityRefCodec.PrefabAssetRef),
            });
        }
    }
}
