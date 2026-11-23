#nullable enable

using System;
using System.Collections.Generic;
using OpWeave.Core.Operations;
using OpWeave.Core.Registry;

namespace OpWeave.Core.Tests
{
    /// <summary>
    /// Catalog-mirroring descriptor fixtures for the SPEC §C5 search-index
    /// tests (T-W3.1-01): every standard Operation plus the AT-01 custom
    /// Operation, carrying the exact id, DisplayName, Summary, Tags, Keywords
    /// and Japanese Localized text of the production descriptors — the fields
    /// the index reads — while ports, effects and the rest stay at builder
    /// defaults because the index never looks at them.
    /// </summary>
    internal static class SearchCatalogFixtures
    {
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

        /// <summary>The 32 standard Operations' indexable descriptor fields.</summary>
        internal static IReadOnlyList<OperationDescriptor> StandardOperations()
        {
            return new List<OperationDescriptor>
            {
                Mirror("core.start", "Start",
                    "The single entry point of a workflow; every workflow contains exactly one.",
                    new[] { "workflow" }, new[] { "start", "entry", "begin" },
                    "開始", "Workflow の実行開始点。Workflow にはちょうど1つ置く。",
                    "開始", "スタート", "入口"),
                Mirror("unity.asset.copy", "Copy Asset",
                    "Duplicates an asset at a new path; the destination is never overwritten.",
                    new[] { "asset", "copy" }, new[] { "asset", "copy", "duplicate", "clone" },
                    "アセット複製", "アセットを複製して新しいパスに作成します。既存パスは上書きしません。",
                    "アセット", "複製", "コピー"),
                Mirror("unity.asset.create_folder", "Create Folder",
                    "Creates an asset folder and any missing parent folders; an existing folder is a success.",
                    new[] { "asset", "folder" }, new[] { "folder", "create", "directory" },
                    "フォルダ作成", "アセットフォルダを、無い親フォルダと一緒に作成します。既存のフォルダは成功になります。",
                    "フォルダ", "作成", "アセット"),
                Mirror("unity.asset.create_material", "Create Material",
                    "Creates a Material asset with a shader and properties; an existing path is never overwritten.",
                    new[] { "asset", "material" }, new[] { "material", "shader", "create" },
                    "マテリアル作成", "シェーダーとプロパティを指定してマテリアルアセットを作成します。既存パスは上書きしません。",
                    "マテリアル", "作成", "アセット"),
                Mirror("unity.asset.create_scriptable_object", "Create Scriptable Object",
                    "Creates a ScriptableObject asset of the named type with the given field values.",
                    new[] { "asset", "scriptableobject" }, new[] { "scriptableobject", "asset", "create", "data" },
                    "ScriptableObject作成", "指定した型のScriptableObjectアセットを作成し、フィールド値を適用します。",
                    "ScriptableObject", "作成", "アセット"),
                Mirror("unity.asset.delete", "Delete Asset",
                    "Deletes an asset — to the trash by default, permanently on request. Destructive.",
                    new[] { "asset", "delete" }, new[] { "asset", "delete", "remove", "trash" },
                    "アセット削除", "アセットを削除します。既定はゴミ箱へ移動、permanent で完全削除です。破壊的操作。",
                    "アセット", "削除", "ゴミ箱"),
                Mirror("unity.asset.find", "Find Assets",
                    "Finds assets with a search filter and returns at most 500 references plus a truncated flag.",
                    new[] { "asset", "search" }, new[] { "search", "find", "query" },
                    "アセット検索", "検索フィルターでアセットを探し、最大500件の参照と切り詰めフラグを返します。",
                    "検索", "アセット", "絞り込み"),
                Mirror("unity.asset.import", "Import Asset",
                    "Imports an existing on-disk file as an asset and waits for the import to finish.",
                    new[] { "asset", "import" }, new[] { "import", "asset", "file" },
                    "アセット取り込み", "ディスク上の既存ファイルをアセットとして取り込み、完了まで待ちます。",
                    "インポート", "取り込み", "アセット"),
                Mirror("unity.asset.move", "Move Asset",
                    "Moves or renames an asset; the destination is validated and never overwritten.",
                    new[] { "asset", "move" }, new[] { "asset", "move", "rename" },
                    "アセット移動", "アセットを移動・名前変更します。移動先を事前検証し、既存パスは上書きしません。",
                    "アセット", "移動", "名前変更"),
                Mirror("unity.asset.save_all", "Save Assets",
                    "Writes every pending asset change of the Editor to disk (AssetDatabase.SaveAssets).",
                    new[] { "asset", "save" }, new[] { "save", "flush", "dirty" },
                    "アセット保存", "エディタが持つ未保存のアセット変更をすべてディスクへ書き出します。",
                    "保存", "アセット", "フラッシュ"),
                Mirror("unity.component.add", "Add Component",
                    "Adds a component of the named type to a GameObject.",
                    new[] { "component", "gameObject", "scene" }, new[] { "component", "add", "attach", "gameobject" },
                    "Component追加", "GameObject に指定した型の Component を追加します。componentType が定数なら出力型を絞り込みます。",
                    "Component", "追加", "GameObject"),
                Mirror("unity.component.remove", "Remove Component",
                    "Removes a component from its GameObject; a Transform is never removable.",
                    new[] { "component", "gameObject", "scene" }, new[] { "component", "remove", "delete", "detach", "gameobject" },
                    "Component削除", "GameObject から対象の Component を削除します。Transform は削除できません。",
                    "Component", "削除", "GameObject"),
                Mirror("unity.gameobject.create", "Create GameObject",
                    "Creates a GameObject or primitive in a scene, optionally under a parent.",
                    new[] { "gameObject", "scene", "hierarchy" }, new[] { "gameobject", "create", "primitive", "spawn" },
                    "GameObject作成", "シーン内に GameObject（またはプリミティブ）を作成します。parent 省略時はルート、scene 省略時はアクティブシーンです。",
                    "GameObject", "作成", "プリミティブ", "階層"),
                Mirror("unity.gameobject.delete", "Delete GameObject",
                    "Deletes a GameObject and its hierarchy from a scene.",
                    new[] { "gameObject", "scene", "hierarchy" }, new[] { "gameobject", "delete", "destroy", "remove" },
                    "GameObject削除", "対象の GameObject とその子階層をシーンから削除します。",
                    "GameObject", "削除", "階層"),
                Mirror("unity.gameobject.find", "Find GameObject",
                    "Finds the single GameObject matching a hierarchy path and/or name, optionally by tag.",
                    new[] { "gameObject", "scene", "hierarchy" }, new[] { "gameobject", "find", "search", "name", "tag", "path" },
                    "GameObject検索", "階層パス・名前（および任意のタグ）に一致する GameObject を1件だけ探します。",
                    "GameObject", "検索", "名前", "タグ"),
                Mirror("unity.gameobject.set_parent", "Set Parent",
                    "Reparents a GameObject under another, or to the scene root; cyclic parents are refused.",
                    new[] { "gameObject", "scene", "hierarchy" }, new[] { "gameobject", "parent", "reparent", "hierarchy", "move" },
                    "親の設定", "GameObject を別の GameObject の子（省略時はルート）に移します。循環する親子は拒否します。",
                    "GameObject", "親", "階層"),
                Mirror("unity.prefab.apply_overrides", "Apply Prefab Overrides",
                    "Apply all overrides of a Prefab instance to its source Prefab asset.",
                    new[] { "prefab", "instance", "overrides", "asset" }, new[] { "prefab", "apply", "overrides", "save" },
                    "Prefab オーバーライドを適用", "Prefab インスタンスのオーバーライドを元の Prefab アセットへ適用し保存する。Prefab アセット自体を書き換える破壊的操作で Undo 不可。",
                    "Prefab", "オーバーライド", "適用"),
                Mirror("unity.prefab.create_from_gameobject", "Create Prefab from GameObject",
                    "Save a scene GameObject as a Prefab asset, optionally connecting the source to it.",
                    new[] { "prefab", "create", "gameobject" }, new[] { "prefab", "create", "save", "gameobject", "connect" },
                    "GameObject から Prefab を作成", "シーンの GameObject を Prefab アセットとして保存する。connect 時は元オブジェクトがその outermost インスタンスになる。",
                    "Prefab", "作成", "GameObject"),
                Mirror("unity.prefab.create_variant", "Create Prefab Variant",
                    "Create a Prefab Variant of a base Prefab at a new .prefab path.",
                    new[] { "prefab", "variant", "create" }, new[] { "prefab", "variant", "create", "derive" },
                    "Prefab Variant を作成", "既存の Prefab を基底とする Prefab Variant を指定パスに作成する。一時的な Preview Scene で作り、ユーザーの Scene には触れない。",
                    "Prefab", "Variant", "作成"),
                Mirror("unity.prefab.instantiate", "Instantiate Prefab",
                    "Instantiate a Prefab asset into a scene, optionally under a parent at a position.",
                    new[] { "prefab", "instance", "scene" }, new[] { "prefab", "instantiate", "spawn", "instance", "place" },
                    "Prefab をシーンに配置", "Prefab アセットのインスタンスをシーンに生成する。parent 省略時はルート、scene 省略時はアクティブシーン。",
                    "Prefab", "配置", "インスタンス"),
                Mirror("unity.prefab.open_contents", "Open Prefab contents",
                    "Open a Prefab's contents for editing and hold them as a Run resource until save_contents or the Run end.",
                    new[] { "prefab", "contents", "edit" }, new[] { "prefab", "contents", "open", "edit", "load" },
                    "Prefab の編集内容を開く", "Prefab アセットの内容を編集用に開き、Run リソースとして保持する。save_contents か Run 終了まで解放されない。",
                    "Prefab", "編集", "内容"),
                Mirror("unity.prefab.revert_overrides", "Revert Prefab Overrides",
                    "Revert all overrides of a Prefab instance back to its source Prefab.",
                    new[] { "prefab", "instance", "overrides", "scene" }, new[] { "prefab", "revert", "overrides", "undo" },
                    "Prefab オーバーライドを取り消し", "Prefab インスタンスのオーバーライドをすべて取り消し、元の Prefab の状態に戻す。シーン側の変更で Undo 可能。",
                    "Prefab", "オーバーライド", "取り消し"),
                Mirror("unity.prefab.save_contents", "Save Prefab contents",
                    "Save the opened Prefab contents back to their Prefab asset and release the editing session.",
                    new[] { "prefab", "contents", "save" }, new[] { "prefab", "contents", "save", "write", "asset" },
                    "Prefab の編集内容を保存", "開いている Prefab の編集内容を元の Prefab アセットに保存し、編集セッションを解放する。",
                    "Prefab", "保存"),
                Mirror("unity.prefab.unpack", "Unpack Prefab Instance",
                    "Sever a Prefab instance's link to its Prefab asset, keeping its values.",
                    new[] { "prefab", "instance", "unpack", "scene" }, new[] { "prefab", "unpack", "instance", "detach" },
                    "Prefab インスタンスをアンパック", "Prefab インスタンスと元の Prefab アセットのリンクを解除する。値は保持され、Undo 可能。mode \"outermost\"（既定）は最外層のみ、\"completely\" は入れ子も含めて解除する。",
                    "Prefab", "アンパック", "インスタンス"),
                Mirror("unity.property.get", "Get Property",
                    "Reads a serialized property of the target as JSON.",
                    new[] { "property", "read" }, new[] { "property", "get", "read", "serialized" },
                    "プロパティ取得", "対象オブジェクトのシリアライズプロパティを読み取り、JSON値として返します。",
                    "プロパティ", "取得", "読み取り"),
                Mirror("unity.property.set", "Set Property",
                    "Writes a JSON value into a serialized property of the target.",
                    new[] { "property", "write" }, new[] { "property", "set", "write", "serialized" },
                    "プロパティ設定", "対象オブジェクトのシリアライズプロパティにJSON値を書き込みます。アセットは直ちに保存されます。",
                    "プロパティ", "設定", "書き込み"),
                Mirror("unity.scene.close", "Close Scene",
                    "Closes a loaded scene; an already closed scene succeeds, the last scene is never closed.",
                    new[] { "scene", "close" }, new[] { "scene", "close", "unload" },
                    "シーンを閉じる", "読み込んでいるシーンを閉じます。既に閉じていれば成功、最後の1つは閉じられません。",
                    "シーン", "閉じる", "アンロード"),
                Mirror("unity.scene.create", "Create Scene",
                    "Creates a new scene asset and loads it, empty or with the default root setup.",
                    new[] { "scene", "create" }, new[] { "scene", "create", "new" },
                    "シーン作成", "新しいシーンアセットを作成して読み込みます。空または既定の構成を選べます。",
                    "シーン", "作成", "新規"),
                Mirror("unity.scene.open", "Open Scene",
                    "Opens a scene, replacing the loaded scenes in single mode or adding to them in additive mode.",
                    new[] { "scene", "open" }, new[] { "scene", "open", "load" },
                    "シーンを開く", "シーンを開きます。single モードでは開いているシーンを置き換え、additive では追加します。",
                    "シーン", "開く", "読み込み"),
                Mirror("unity.scene.save", "Save Scene",
                    "Saves a loaded scene to its own path or to a given path; an untitled scene needs a path.",
                    new[] { "scene", "save" }, new[] { "scene", "save", "write" },
                    "シーン保存", "読み込んでいるシーンを保存します。Untitled のシーンには保存先パスが必要です。",
                    "シーン", "保存", "書き出し"),
                Mirror("unity.script.create_from_template", "Create Script From Template",
                    "Generates a C# script from a bundled template, substituting only class name and namespace.",
                    new[] { "script", "template" }, new[] { "script", "template", "generate", "class" },
                    "テンプレートからスクリプト作成", "同梱テンプレートから C# スクリプトを生成します。クラス名と名前空間だけを置き換えます。",
                    "スクリプト", "テンプレート", "生成"),
                Mirror("unity.transform.set", "Set Transform",
                    "Sets a transform's position, rotation (Euler) and/or scale; scale is always local.",
                    new[] { "gameObject", "transform", "scene" }, new[] { "transform", "position", "rotation", "scale", "move", "rotate" },
                    "Transform設定", "GameObject の位置・回転（オイラー角）・スケールを設定します。1項目以上必須。scale は常にローカルです。",
                    "Transform", "位置", "回転", "スケール"),
            };
        }

        /// <summary>
        /// The AT-01 custom Operation <c>proj.transform.reset</c> — mirrors
        /// <c>ResetTransformOperation</c> under
        /// <c>Assets/OpWeave/Editor/Operations/Transform</c> (origin Custom).
        /// </summary>
        internal static OperationDescriptor At01() =>
            Mirror("proj.transform.reset", "Reset Transform",
                "Reset local position, rotation and scale of a GameObject.",
                new[] { "transform", "gameObject" }, new[] { "transform", "reset", "position", "rotation", "scale" },
                "Transform をリセット", "GameObject の位置・回転・スケールを初期値に戻します。",
                "トランスフォーム", "リセット", "初期化");

        /// <summary>The standard catalog plus AT-01 — the set L-11 measures.</summary>
        internal static List<OperationDescriptor> Catalog()
        {
            var descriptors = new List<OperationDescriptor>(StandardOperations());
            descriptors.Add(At01());
            return descriptors;
        }

        /// <summary>
        /// Wraps descriptors in <see cref="RegisteredOperation"/> records and
        /// a real <see cref="RegistrySnapshot"/> (canonical id-Ordinal order),
        /// so the index is exercised through its production entry point.
        /// </summary>
        internal static RegistrySnapshot Snapshot(
            IEnumerable<OperationDescriptor> descriptors,
            OperationOrigin origin = OperationOrigin.Standard)
        {
            var members = new List<RegisteredOperation>();
            var n = 0;
            foreach (var descriptor in descriptors)
            {
                // The AT-01 fixture registers as a project custom Operation.
                var actual = descriptor.Id == "proj.transform.reset"
                    ? OperationOrigin.Custom
                    : origin;
                members.Add(new RegisteredOperation(
                    descriptor,
                    actual,
                    "OpWeave.Operations",
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
                "reg_searchtest",
                new string('f', 64),
                members,
                DescriptorTestFixtures.CreateRegistry(),
                Array.Empty<RegistryChange>(),
                Array.Empty<TypeRegistryFailure>());
        }
    }
}
