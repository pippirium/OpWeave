# OpWeave

[English](README.md)

OpWeave は、Unity Editor 向けのワークフローエンジンです。型付きの操作(Operation)を検索してワークフロー文書に組み立て、コンパイル・逐次実行し、中断された実行を永続ジャーナルから復旧します。UPM パッケージ `com.opweave.workflow` バージョン **0.1.0** として配布し、ライセンスは [Apache License 2.0](LICENSE) です。

## Windows v1 の機能

- **操作カタログ** — 組み込み・拡張の操作を、型・ポート互換性・宣言された副作用で検索。
- **ビジュアルなワークフロー作成** — 制御フローとデータフローを分離したノードエディタ。裏付けとなる正準 `.opweave.json` 文書を保持。
- **コンパイルと検証** — 型付きポート検査と安定した planHash を備えた、決定的なワークフローコンパイル。
- **逐次実行** — 各操作が宣言する完了条件を待ちながら順に実行し、結果と診断(Diagnostic)を記録。
- **復旧** — 永続 intent/ジャーナルにより、中断された保存や実行を、無断で失ったり重複させたりしない、レビュー可能な状態へ整合。
- **Agent API** — AI エージェントが操作を発見し、スキーマを確認し、ワークフローを検証するための構造化インターフェース。
- **拡張性** — 公開拡張契約による、第三者製の操作・型プロバイダー・コーデック。

## 動作要件

- **OS:** Windows 11 x64
- **検証済み Unity Editor:** 6000.5.11f1(マニフェストの下限は `6000.0`、検証済みのサポート対象は 6000.5.11f1)
- macOS / Linux は **v1 では未サポート**(先送り。既知の制限を参照)。

## インストール(Unity Package Manager)

リリースタグで固定した Git URL でパッケージを追加します。

1. **Window > Package Manager** を開く。
2. **+ > Add package from git URL…** を選択。
3. 次を入力:

   ```
   https://github.com/pippirium/OpWeave.git?path=Packages/com.opweave.workflow#v0.1.0
   ```

依存パッケージ(`com.unity.nuget.newtonsoft-json`、`com.unity.nuget.mono-cecil`)は Unity レジストリから自動的に解決されます。

## 使い方

1. ワークフローアセットを作成し、OpWeave のノードエディタを開く。
2. 操作カタログを検索して操作ノードを配置。制御フローのエッジ(実行順序)とデータポート(型付きの値)を接続。
3. グラフを保存。**OpWeave > Save Graph JSON** で正準のワークフロー文書を書き出す。
4. エディタ統合からワークフローを実行。各操作の完了宣言を待ち、ステップごとの結果と診断を報告。
5. 実行が中断された場合、復旧処理がジャーナルをレビュー可能な状態へ整合します。不確かな状態は暗黙の成功ではなく、レビュー待ちとして表面化します。

## パッケージ構成

| パス | 内容 |
| --- | --- |
| `Editor/Core` | 正準文書モデル、コンパイラ、実行ランタイム、レジストリ、復旧、ポリシー |
| `Editor/Operations` | 組み込み操作の実装 |
| `Editor/AgentApi` | エージェント向け構造化 API |
| `Editor/GraphToolkit`, `Editor/Viewer` | ノードエディタとグラフビューアの UI |
| `Editor/Pipeline`, `Editor/UnityAdapter` | Unity Pipeline と Unity 固有アダプタ |
| `Editor/Testing` | テスト支援(テスト専用アセンブリ) |
| `Schemas~`, `Samples~`, `Editor/Templates~` | JSON スキーマ、サンプルワークフロー、テンプレート(Unity が無視するフォルダ) |
| `Tests` | パッケージのテストアセンブリと、テスト専用の同梱プラグイン |

## ライセンス

OpWeave 自体のコードと文書は [Apache License, Version 2.0](LICENSE)。[NOTICE](NOTICE) と [LICENSING.md](LICENSING.md) を参照してください。第三者製コンポーネントはそれぞれのライセンスが適用されます — [Third Party Notices](Packages/com.opweave.workflow/Third%20Party%20Notices.md)、テストプラグイン一覧 `Packages/com.opweave.workflow/Tests/Plugins/THIRD_PARTY_NOTICES.txt`、`Tests/Plugins/Licenses/` のライセンス本文を参照してください。

## 既知の制限

- **Windows のみ。** macOS / Linux 対応は v1 以降に先送り。それ以外の環境での動作は未サポートです。
- **エディタ専用。** ランタイム/プレイヤー、ゲーム内ビジュアルスクリプティングは対象外。
- 初期スコープは Unity Editor の編集操作。任意の C# コードの無制限実行は v1 の対象外です。

## 出所(Provenance)

バージョン `0.1.0` は、[VERSION](VERSION) と [release-lock.json](release-lock.json) に記録された、受入済みの Windows v1 開発ベースラインに対応します(ソース: 非公開の `pippirium/OpWeave-dev`。G-09 受入証跡はそこに記録)。`0.1.0` は単一の不変プロダクトを識別し、修正はタグの付け替えではなく次のバージョンで提供します。
