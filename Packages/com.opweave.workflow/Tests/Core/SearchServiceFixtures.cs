#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// Catalog-mirroring descriptor fixtures for the SPEC §C6/§C7 search
    /// service tests (T-W3.1-02): the full standard catalog plus the AT-01
    /// custom Operation, carrying the real port declarations, side effects,
    /// categories, destructive flags and execution modes the service's
    /// filters and type matching read — layered over the indexable text of
    /// <see cref="SearchCatalogFixtures"/> so the C5 text material stays
    /// shared.
    /// </summary>
    internal static class SearchServiceFixtures
    {
        private sealed class UnityShapesProvider : ITypeProvider
        {
            public IEnumerable<TypeDefinition> GetTypes() => UnityShapeTypes.All;
        }

        /// <summary>
        /// A test-local registry of the built-in <c>core.*</c> types and the
        /// <see cref="UnityShapeTypes"/> definitions — every port type the
        /// catalog declares — so §B6 assignability resolves exactly as it
        /// does over the production registry.
        /// </summary>
        internal static ITypeRegistry CreateTypes()
        {
            var build = TypeRegistryBuilder.Build(new[]
            {
                new TypeProviderCandidate(
                    CoreTypeProvider.ProviderId, new CoreTypeProvider(), OperationOrigin.Standard),
                new TypeProviderCandidate(
                    "opweave.unity-shapes", new UnityShapesProvider(), OperationOrigin.Standard),
            });
            if (build.Failures.Count != 0)
            {
                throw new InvalidOperationException(
                    "Search fixture type registry failed: " + build.Failures[0].Detail);
            }
            return build.Registry;
        }

        private static PortDescriptor In(string name, string type, bool required = true) =>
            new PortDescriptor(name, type, required, null, string.Empty, null);

        private static PortDescriptor Out(string name, string type) =>
            new PortDescriptor(name, type, false, null, string.Empty, null);

        private static OperationDescriptor Shape(
            OperationDescriptor descriptor,
            string category,
            PortDescriptor[] inputs,
            PortDescriptor[] outputs,
            string[] effects,
            bool destructive = false,
            ExecutionMode mode = ExecutionMode.Immediate) =>
            descriptor with
            {
                Inputs = inputs,
                Outputs = outputs,
                SideEffects = effects,
                Category = category,
                Destructive = destructive,
                ExecutionMode = mode,
            };

        // The standard Operations absent from SearchCatalogFixtures' 32-entry
        // set, with the same mirror quality (production text + ports).
        private static OperationDescriptor Mirror(
            string id,
            string displayName,
            string summary,
            string[] tags,
            string[] keywords,
            string jaDisplayName,
            string jaSummary,
            params string[] jaKeywords) =>
            Op.Define(id, 1)
                .Summary(summary)
                .Ja(jaDisplayName, jaSummary, jaKeywords)
                .Build()
                with
                {
                    DisplayName = displayName,
                    Tags = tags,
                    Keywords = keywords,
                };

        /// <summary>
        /// The catalog the §C6/§C7 tests search: the 35 standard Operations
        /// and — when <paramref name="includeCustom"/> — the AT-01
        /// <c>proj.transform.reset</c> custom Operation, each with its real
        /// descriptor contract.
        /// </summary>
        internal static List<OperationDescriptor> Catalog(bool includeCustom)
        {
            var byId = new Dictionary<string, OperationDescriptor>(StringComparer.Ordinal);
            foreach (var descriptor in SearchCatalogFixtures.StandardOperations())
            {
                byId.Add(descriptor.Id, descriptor);
            }

            var shaped = new List<OperationDescriptor>
            {
                Shape(byId["core.start"], "Core",
                    Array.Empty<PortDescriptor>(), Array.Empty<PortDescriptor>(),
                    new[] { EffectNames.Read }),
                Shape(byId["unity.asset.copy"], "Asset",
                    new[] { In("asset", "unity.AssetRef<unity.Object>"), In("destination", "unity.AssetPath") },
                    new[] { Out("asset", "unity.AssetRef<unity.Object>") },
                    new[] { EffectNames.AssetCreate }),
                Shape(byId["unity.asset.create_folder"], "Asset",
                    new[] { In("path", "unity.AssetPath") },
                    new[] { Out("path", "unity.AssetPath") },
                    new[] { EffectNames.AssetCreate }),
                Shape(byId["unity.asset.create_material"], "Asset",
                    new[] { In("path", "unity.AssetPath"), In("shader", "core.String"), In("properties", "core.Json", required: false) },
                    new[] { Out("material", "unity.AssetRef<unity.Material>") },
                    new[] { EffectNames.AssetCreate }),
                Shape(byId["unity.asset.create_scriptable_object"], "Asset",
                    new[] { In("path", "unity.AssetPath"), In("type", "unity.TypeName"), In("values", "core.Json", required: false) },
                    new[] { Out("asset", "unity.AssetRef<unity.ScriptableObject>") },
                    new[] { EffectNames.AssetCreate }),
                Shape(byId["unity.asset.delete"], "Asset",
                    new[] { In("asset", "unity.AssetRef<unity.Object>"), In("mode", "core.String", required: false) },
                    Array.Empty<PortDescriptor>(),
                    new[] { EffectNames.AssetDelete },
                    destructive: true),
                Shape(byId["unity.asset.find"], "Asset",
                    new[] { In("filter", "core.String"), In("folders", "core.List<unity.AssetPath>", required: false) },
                    new[] { Out("assets", "core.List<unity.AssetRef<unity.Object>>"), Out("truncated", "core.Bool") },
                    new[] { EffectNames.Read }),
                Shape(byId["unity.asset.import"], "Asset",
                    new[] { In("path", "unity.AssetPath") },
                    new[] { Out("asset", "unity.AssetRef<unity.Object>") },
                    new[] { EffectNames.AssetCreate },
                    mode: ExecutionMode.Waitable),
                Shape(byId["unity.asset.move"], "Asset",
                    new[] { In("asset", "unity.AssetRef<unity.Object>"), In("destination", "unity.AssetPath") },
                    new[] { Out("asset", "unity.AssetRef<unity.Object>") },
                    new[] { EffectNames.AssetMove }),
                Shape(byId["unity.asset.save_all"], "Asset",
                    Array.Empty<PortDescriptor>(), Array.Empty<PortDescriptor>(),
                    new[] { EffectNames.AssetModify }),
                Shape(byId["unity.component.add"], "Component",
                    new[] { In("target", "unity.GameObjectRef"), In("componentType", "unity.TypeName") },
                    new[] { Out("component", "unity.ComponentRef<unity.Component>") },
                    new[] { EffectNames.SceneModify }),
                Shape(byId["unity.component.remove"], "Component",
                    new[] { In("component", "unity.ComponentRef<unity.Component>") },
                    Array.Empty<PortDescriptor>(),
                    new[] { EffectNames.SceneModify },
                    destructive: true),
                Shape(byId["unity.gameobject.create"], "GameObject",
                    new[] { In("name", "core.String"), In("scene", "unity.SceneRef", required: false), In("parent", "unity.GameObjectRef", required: false), In("primitive", "core.String", required: false), In("position", "unity.Vector3", required: false) },
                    new[] { Out("gameObject", "unity.GameObjectRef") },
                    new[] { EffectNames.SceneModify }),
                Shape(byId["unity.gameobject.delete"], "GameObject",
                    new[] { In("target", "unity.GameObjectRef") },
                    Array.Empty<PortDescriptor>(),
                    new[] { EffectNames.SceneModify },
                    destructive: true),
                Shape(byId["unity.gameobject.find"], "GameObject",
                    new[] { In("scene", "unity.SceneRef", required: false), In("path", "unity.HierarchyPath", required: false), In("name", "core.String", required: false), In("tag", "core.String", required: false) },
                    new[] { Out("gameObject", "unity.GameObjectRef") },
                    new[] { EffectNames.Read }),
                Shape(byId["unity.gameobject.set_parent"], "GameObject",
                    new[] { In("target", "unity.GameObjectRef"), In("parent", "unity.GameObjectRef", required: false), In("worldPositionStays", "core.Bool", required: false) },
                    new[] { Out("gameObject", "unity.GameObjectRef") },
                    new[] { EffectNames.SceneModify }),
                Shape(byId["unity.prefab.apply_overrides"], "Prefab",
                    new[] { In("instance", "unity.PrefabInstanceRef") },
                    new[] { Out("prefab", "unity.PrefabAssetRef") },
                    new[] { EffectNames.AssetModify },
                    destructive: true),
                Shape(byId["unity.prefab.create_from_gameobject"], "Prefab",
                    new[] { In("source", "unity.GameObjectRef"), In("path", "unity.AssetPath"), In("connect", "core.Bool", required: false), In("overwrite", "core.Bool", required: false) },
                    new[] { Out("prefab", "unity.PrefabAssetRef"), Out("instance", "unity.PrefabInstanceRef") },
                    new[] { EffectNames.AssetCreate, EffectNames.SceneModify }),
                Shape(byId["unity.prefab.create_variant"], "Prefab",
                    new[] { In("base", "unity.PrefabAssetRef"), In("path", "unity.AssetPath") },
                    new[] { Out("variant", "unity.PrefabAssetRef") },
                    new[] { EffectNames.AssetCreate }),
                Shape(byId["unity.prefab.instantiate"], "Prefab",
                    new[] { In("prefab", "unity.PrefabAssetRef"), In("scene", "unity.SceneRef", required: false), In("parent", "unity.GameObjectRef", required: false), In("position", "unity.Vector3", required: false) },
                    new[] { Out("instance", "unity.PrefabInstanceRef") },
                    new[] { EffectNames.SceneModify }),
                Shape(byId["unity.prefab.open_contents"], "Prefab",
                    new[] { In("prefab", "unity.PrefabAssetRef") },
                    new[] { Out("root", "unity.GameObjectRef") },
                    new[] { EffectNames.Read }),
                Shape(byId["unity.prefab.revert_overrides"], "Prefab",
                    new[] { In("instance", "unity.PrefabInstanceRef") },
                    new[] { Out("instance", "unity.PrefabInstanceRef") },
                    new[] { EffectNames.SceneModify }),
                Shape(byId["unity.prefab.save_contents"], "Prefab",
                    new[] { In("root", "unity.GameObjectRef") },
                    new[] { Out("prefab", "unity.PrefabAssetRef") },
                    new[] { EffectNames.AssetModify }),
                Shape(byId["unity.prefab.unpack"], "Prefab",
                    new[] { In("instance", "unity.PrefabInstanceRef"), In("mode", "core.String", required: false) },
                    new[] { Out("gameObject", "unity.GameObjectRef") },
                    new[] { EffectNames.SceneModify }),
                Shape(byId["unity.property.get"], "GameObject",
                    new[] { In("target", "unity.ObjectRef"), In("propertyPath", "core.String") },
                    new[] { Out("value", "core.Json") },
                    new[] { EffectNames.Read }),
                Shape(byId["unity.property.set"], "GameObject",
                    new[] { In("target", "unity.ObjectRef"), In("propertyPath", "core.String"), In("value", "core.Json") },
                    Array.Empty<PortDescriptor>(),
                    new[] { EffectNames.SceneModify, EffectNames.AssetModify }),
                Shape(byId["unity.scene.close"], "Scene",
                    new[] { In("scene", "unity.SceneRef"), In("dirtyScenes", "core.String", required: false) },
                    Array.Empty<PortDescriptor>(),
                    new[] { EffectNames.SceneLoad }),
                Shape(byId["unity.scene.create"], "Scene",
                    new[] { In("path", "unity.AssetPath"), In("setup", "core.String", required: false), In("mode", "core.String", required: false) },
                    new[] { Out("scene", "unity.SceneRef") },
                    new[] { EffectNames.AssetCreate, EffectNames.SceneLoad }),
                Shape(byId["unity.scene.open"], "Scene",
                    new[] { In("path", "unity.AssetPath"), In("mode", "core.String", required: false), In("dirtyScenes", "core.String", required: false) },
                    new[] { Out("scene", "unity.SceneRef") },
                    new[] { EffectNames.SceneLoad }),
                Shape(byId["unity.scene.save"], "Scene",
                    new[] { In("scene", "unity.SceneRef", required: false), In("path", "unity.AssetPath", required: false) },
                    new[] { Out("scene", "unity.SceneRef") },
                    new[] { EffectNames.SceneSave }),
                Shape(byId["unity.script.create_from_template"], "Script",
                    new[] { In("template", "core.String"), In("className", "core.String"), In("namespace", "core.String", required: false), In("folder", "unity.AssetPath") },
                    new[] { Out("script", "unity.AssetRef<unity.MonoScript>"), Out("typeName", "unity.TypeName") },
                    new[] { EffectNames.CodeTemplate, EffectNames.AssetCreate }),
                Shape(byId["unity.transform.set"], "GameObject",
                    new[] { In("target", "unity.GameObjectRef"), In("position", "unity.Vector3", required: false), In("rotation", "unity.Euler", required: false), In("scale", "unity.Vector3", required: false), In("space", "core.String", required: false) },
                    Array.Empty<PortDescriptor>(),
                    new[] { EffectNames.SceneModify }),

                Shape(
                    Mirror("unity.script.create", "Create Script",
                        "Writes an approved C# script file under a configured code path and imports it.",
                        new[] { "script", "code" }, new[] { "script", "create", "code", "csharp", "generate" },
                        "スクリプト作成", "設定されたコードパスの内側に C# スクリプトを作成し、取り込みます。",
                        "スクリプト", "コード", "生成"),
                    "Script",
                    new[] { In("path", "unity.AssetPath"), In("content", "core.String"), In("overwrite", "core.Bool", required: false) },
                    new[] { Out("script", "unity.AssetRef<unity.MonoScript>") },
                    new[] { EffectNames.CodeWrite, EffectNames.AssetCreate }),
                Shape(
                    Mirror("unity.script.compile", "Compile Scripts",
                        "Requests a script compilation and waits for it to finish.",
                        new[] { "script", "compile" }, new[] { "compile", "script", "domain", "reload" },
                        "スクリプトコンパイル", "スクリプトのコンパイルを要求し、完了まで待ちます。",
                        "コンパイル", "スクリプト", "ドメインリロード"),
                    "Script",
                    Array.Empty<PortDescriptor>(),
                    new[] { Out("result", "core.Json") },
                    new[] { EffectNames.Compile },
                    mode: ExecutionMode.Waitable),
                Shape(
                    Mirror("unity.package.add", "Add Package",
                        "Adds a package by registry name@version or Git URL and waits for the package manager to resolve it.",
                        new[] { "package", "registry", "git" }, new[] { "package", "add", "upm", "git" },
                        "パッケージ追加", "name@version または Git URL でパッケージを追加し、解決を待ちます。",
                        "パッケージ", "追加", "git"),
                    "Package",
                    new[] { In("id", "core.String") },
                    new[] { Out("package", "core.Json") },
                    new[] { EffectNames.PackageModify, EffectNames.Compile },
                    mode: ExecutionMode.Waitable),
            };

            if (includeCustom)
            {
                shaped.Add(Shape(
                    SearchCatalogFixtures.At01(), "Transform",
                    new[] { In("target", "unity.GameObjectRef") },
                    new[] { Out("gameObject", "unity.GameObjectRef") },
                    new[] { EffectNames.SceneModify }));
            }
            return shaped;
        }

        /// <summary>
        /// The catalog snapshot the §C6/§C7 tests search.
        /// </summary>
        internal static RegistrySnapshot Snapshot(bool includeCustom) =>
            SnapshotOf(Catalog(includeCustom));

        /// <summary>
        /// Wraps descriptors in <see cref="RegisteredOperation"/> records and
        /// a real <see cref="RegistrySnapshot"/> over <see cref="CreateTypes"/>
        /// — <c>proj.transform.reset</c> registers as
        /// <see cref="OperationOrigin.Custom"/>, every other member Standard.
        /// </summary>
        internal static RegistrySnapshot SnapshotOf(IEnumerable<OperationDescriptor> descriptors)
        {
            var members = new List<RegisteredOperation>();
            var n = 0;
            foreach (var descriptor in descriptors)
            {
                var origin = descriptor.Id == "proj.transform.reset"
                    ? OperationOrigin.Custom
                    : OperationOrigin.Standard;
                members.Add(new RegisteredOperation(
                    descriptor,
                    origin,
                    origin == OperationOrigin.Standard ? "OpWeave.Operations" : "OpWeave.Custom",
                    null,
                    DescriptorHash.Compute(descriptor),
                    new string((char)('a' + n++ % 26), 64)));
            }
            members.Sort(static (a, b) =>
            {
                var byId = string.CompareOrdinal(a.Descriptor.Id, b.Descriptor.Id);
                return byId != 0 ? byId : a.Descriptor.Version.CompareTo(b.Descriptor.Version);
            });
            return new RegistrySnapshot(
                "reg_searchsvc",
                new string('e', 64),
                members,
                CreateTypes(),
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());
        }
    }
}
