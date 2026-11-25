# OpWeave

[English](README.md)

OpWeave は、Unity Editor 向けのワークフローエンジンです。型付きの操作(Operation)を検索してワークフロー文書に組み立て、コンパイル・逐次実行し、中断された実行を永続ジャーナルから復旧します。UPM パッケージ `com.opweave.workflow` バージョン **0.1.1** として配布し、ライセンスは [Apache License 2.0](LICENSE) です。

## Windows v1 の機能

- **操作カタログ** — 組み込み・拡張の操作を、型・ポート互換性・宣言された副作用・タグ・提供元で検索。
- **ビジュアルなワークフロー作成** — Unity 6000.5 以降では、制御フローとデータフローを分離した Graph Toolkit エディタで `.opweavegraph` アセットを作成・編集。
- **正準ワークフロー文書** — **OpWeave > Save Graph JSON** で `.opweave.json` を明示的に書き出し。グラフアセットの保存が JSON を暗黙に書き換えることはありません。
- **コンパイルと検証** — 型付きポート検査、宣言された副作用、承認要件、安定した planHash を備えた決定的なワークフローコンパイル。
- **逐次実行** — 各操作が宣言する完了条件を待ちながら順に実行し、結果と診断(Diagnostic)を記録。
- **復旧** — 永続 intent/ジャーナルにより、中断された保存や実行を、無断で失ったり重複させたりしない、レビュー可能な状態へ整合。
- **Run Viewer** — **Window > OpWeave > Runs** で実行状態、診断、人の承認待ちを確認。
- **Agent API** — AI エージェントが操作を発見し、スキーマを確認し、ワークフローを検証・実行し、実行状態を確認するための構造化インターフェース。
- **拡張性** — OpWeave 本体を変更せずに、プロジェクト内カスタム操作または独立した UPM 操作拡張を追加。

## 動作要件

- **OS:** Windows 11 x64
- **検証済み Unity Editor:** 6000.5.11f1
- **パッケージマニフェストの下限:** Unity `6000.0`
- **Graph Toolkit オーサリング UI:** Unity 6000.5 以降。検証済み構成は 6000.5.11f1
- macOS / Linux は **v1 では未サポート**です(既知の制限を参照)。

マニフェストの下限はコンパイル下限であり、検証済みサポート対象ではありません。Unity 6000.0 で Graph Toolkit GUI 機能がサポートされると扱わないでください。

## インストール(Unity Package Manager)

リリースタグで固定した Git URL でパッケージを追加します。

1. **Window > Package Manager** を開く。
2. **+ > Add package from git URL…** を選択。
3. 次を入力:

   ```
   https://github.com/pippirium/OpWeave.git?path=Packages/com.opweave.workflow#v0.1.1
   ```

依存パッケージ(`com.unity.nuget.newtonsoft-json`、`com.unity.nuget.mono-cecil`)は Unity レジストリから自動的に解決されます。

### アップグレードと旧バージョンのインストール

アップグレードするには、Git URL のタグを変更してパッケージを再インストールまたは更新してください。リリースタグは不変です。修正は既存タグの付け替えではなく、後続バージョンとして提供します。

前回のリリースも次の URL でインストールできます。

```
https://github.com/pippirium/OpWeave.git?path=Packages/com.opweave.workflow#v0.1.0
```

## 使い方

1. Unity 6000.5 以降では、**Assets > Create > OpWeave > Graph**(Project ウィンドウの Create メニューからも利用可能)で `.opweavegraph` アセットを作成します。ダブルクリックすると Graph Toolkit エディタで開きます。
2. 操作カタログを検索して操作ノードを配置し、制御フローのエッジ(実行順序)とデータポート(型付きの値)を接続します。不足・不適合な接続は編集中に検証結果として表示されます。
3. ビジュアルグラフを保持したいときはグラフアセットを保存します。正準の `.opweave.json` ワークフロー文書を書き出すには、Assets または Tools メニューの **OpWeave > Save Graph JSON** を使います。グラフの作成・名前変更・保存が JSON を暗黙に書き込むことはありません。
4. エディタ統合または Agent API でワークフローを検証・実行します。実行は各操作の完了宣言を待ち、ステップごとの結果と診断を報告します。
5. **Window > OpWeave > Runs** で、実行中・完了済みの Run、診断、人の承認待ちの操作を確認します。
6. 実行または JSON 保存が中断された場合、復旧処理は永続ジャーナルをレビュー可能な状態へ整合します。不確かな状態は暗黙の成功ではなく、レビュー待ちとして表面化します。

## 標準 Operation

組み込みカタログは、Windows v1 の Unity Editor 操作群をカバーします。

- Scene・GameObject の作成、検索、階層、Transform、Component、プロパティ操作。
- Prefab の作成、インスタンス化、contents 編集、オーバーライド、Unpack、保存。
- Asset の作成、インポート、移動/コピー/削除、フォルダ、Material、ScriptableObject、スクリプトテンプレート操作。
- スクリプトコンパイルと Unity パッケージ追加/削除操作。

各操作は入力、出力、副作用、完了条件、Undo 対応、冪等性の振る舞いを宣言します。データ破壊、任意コードの書き込み、その他レビューが必要な操作は人の承認フローを使います。エージェントは自分自身の承認が必要な作業を承認できません。

## Agent 連携

OpWeave は、操作検索、操作説明、スキーマ取得、ワークフロー検証、実行、移行、実行状態/ログ、キャンセル、レジストリ状態のための構造化 Agent API を公開します。利用側プロジェクトにオプションの `com.unity.pipeline` **0.9.1-exp.1** がある場合、`OpWeave.Pipeline` アダプタが Unity のコマンドトランスポート上の `opweave` コマンドグループとしてこれらのコマンドを公開します。

通常のエージェントフローは次のとおりです。

1. `search_operations` で候補操作を探す。
2. `describe_operation` で操作契約と例を確認する。
3. `validate_workflow` でワークフローをコンパイルし、診断、副作用、承認要件、planHash を得る。
4. `execute_workflow` で実行を投入する。検証済み planHash に紐付けることもできる。
5. `get_run_status` と `get_run_logs` で終端状態まで追跡する。

業務失敗は、トランスポートの終了コードだけではなく、構造化レスポンス envelope の `ok: false` と診断で返されます。エージェントは `ok` を確認してください。

## カスタム・外部 Operation

`com.opweave.workflow` を編集せずにカタログを拡張できます。

- **プロジェクト内カスタム Operation** は、利用側プロジェクトの Editor 専用アセンブリに置きます。
- **外部 Operation 拡張** は、Operation・型プロバイダー・コーデック・例を収めた独立した UPM パッケージです。

Operation 実装は通常 `OperationBase` から派生し、`[OpWeaveOperation(id, version)]` で identity を宣言し、ポート・副作用・完了条件・Undo・冪等性・例・ローカライズを記述する一致した descriptor を構築します。適格な Operation はコンパイル/ドメインリロード後に発見され、組み込み操作と同じカタログで検索できます。テンプレートはパッケージ内の `Editor/Templates~` に含まれます。

## パッケージ構成

| パス | 内容 |
| --- | --- |
| `Editor/Core` | 正準文書モデル、コンパイラ、実行ランタイム、レジストリ、復旧、ポリシー |
| `Editor/Operations` | 組み込み操作の実装 |
| `Editor/AgentApi` | エージェント向け構造化 API |
| `Editor/GraphToolkit`, `Editor/Viewer` | Graph Toolkit エディタ統合と Run Viewer UI |
| `Editor/Pipeline`, `Editor/UnityAdapter` | オプションのコマンドトランスポートと Unity 固有アダプタ |
| `Editor/Testing` | テスト支援(テスト専用アセンブリ) |
| `Schemas~`, `Samples~`, `Editor/Templates~` | JSON スキーマ、サンプルワークフロー、テンプレート(Unity が無視するフォルダ) |
| `Tests` | パッケージのテストアセンブリと、テスト専用の同梱プラグイン |

## 0.1.1 の変更

バージョン 0.1.1 は後方互換のパッチリリースです。

- Unity Project Browser から `.opweavegraph` アセットを直接作成する **Assets > Create > OpWeave > Graph** を追加。
- Run ロックを、その排他が解除されるまで保持するよう Runner の active-lock 引き継ぎを改善。
- Scene、GameObject、Transform、Prefab 操作で、edge 経由の未確定入力を Preflight が扱う方法を修正。
- ドメインリロード、中断処理、レジストリ公開、復旧まわりの安定性と回帰テスト整合性を改善。
- Unity 6000.5 ゲートの Graph Toolkit テストを独立したテストアセンブリへ分離。これはテスト基盤の品質改善であり、新しい公開機能ではありません。

0.1.1 で既存の公開 API、Operation/Type ID、planHash、ジャーナル形式、設定形式を意図的に変更したものはありません。

## バージョン履歴

| バージョン | 状態 | 内容 |
| --- | --- | --- |
| 0.1.1 | 現在の公開リリース | Project Browser でのグラフ作成、Runner lock 引き継ぎ修正、Preflight 未確定入力修正、安定性・受入改善 |
| 0.1.0 | 以前の公開リリース | 初回の Windows v1 公開パッケージ移行 |

## ライセンス

OpWeave 自体のコードと文書は [Apache License, Version 2.0](LICENSE)。[NOTICE](NOTICE) と [LICENSING.md](LICENSING.md) を参照してください。第三者製コンポーネントはそれぞれのライセンスが適用されます — [Third Party Notices](Packages/com.opweave.workflow/Third%20Party%20Notices.md)、テストプラグイン一覧 `Packages/com.opweave.workflow/Tests/Plugins/THIRD_PARTY_NOTICES.txt`、`Tests/Plugins/Licenses/` のライセンス本文を参照してください。

## 既知の制限

- **Windows のみ。** macOS / Linux 対応は v1 以降に先送り。それ以外の環境での動作は未サポートです。
- **エディタ専用。** ランタイム/プレイヤー、ゲーム内ビジュアルスクリプティングは対象外です。
- **v1 は逐次実行。** 分岐、ループ、並列実行、入れ子ワークフロー、再利用 Blueprint の実行は v1 の実行契約外です。
- **人の承認境界。** 承認が必要な操作は人の判断で停止します。エージェントは自分自身の承認が必要な作業を承認できません。
- 初期スコープは Unity Editor の編集操作。任意の C# コードの無制限実行は v1 の対象外です。

## 出所(Provenance)

バージョン `0.1.1` は、[VERSION](VERSION) と [release-lock.json](release-lock.json) に記録された、受入済みの Windows v1 開発リビジョンに対応します(ソース: 非公開の `pippirium/OpWeave-dev`。G-09 受入証跡はそこに記録)。`0.1.1` は単一の不変プロダクトを識別し、修正はタグの付け替えではなく次のバージョンで提供します。
