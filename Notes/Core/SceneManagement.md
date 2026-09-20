# シーン・エンティティ管理

シーン上のオブジェクトをどう構造化し、状態を持たせ、更新するかの設計ノート。実行の並列化と決定性の詳細は [JobSystem.md](./JobSystem.md)、フレームループのフェーズ構成は [LoopSystem.md](./LoopSystem.md) を参照。

## 重要な技術的制約

- レンダリング効率
  - 「シーン上の全 Renderer コンポーネント」に対するクエリが毎フレーム必要
- ワーカースレッドとの連携
  - Transform 階層の更新・参照をワーカースレッドから行えること
- 少数・重いロジックの書きやすさ
  - ECS が効果を発揮しにくいケース（数が少なく、横断的なクエリを持ち、1 件あたりの更新処理が重い）でもコードの書きやすさを担保したい
  - このパラダイムでも並列実行を by default とし、オブジェクト単位の動的ロックを非ブロッキング（ワーカースレッド上の非同期処理）で実現したい

## 既存エンジンの比較

シーン・オブジェクト管理の設計軸を「構造（階層）」「状態（コンポジション）」「更新」の 3 つに分けて比較する。

### 構造（階層）

| エンジン | 階層の持ち方 |
|---|---|
| Unity | Transform が階層を持つ。全 GameObject が Transform を強制保有し、シーン = ルート GameObject のリスト |
| Unreal | 二層構造。World → Actor はほぼフラットで、階層は Actor 内部の SceneComponent ツリーが持つ（Actor 同士の Attach も実体はコンポーネント接続） |
| Godot | Node ツリーが唯一の構造。Node 自体は座標を持たず、Node2D/Node3D/Control が持つ。シーン = サブツリーのシリアライズで、シーンのネスト参照が合成の基本単位 |
| Bevy (ECS) | 階層は特権的な構造ではなく、ただの `ChildOf`/`Children` コンポーネント。Transform 伝播も 1 つのシステムにすぎない |
| Flax | Actor 自体が Transform ノード（Godot の Node3D に近い）。Actor ツリー + スクリプト添付 |
| Stride | Unity 型。Entity + TransformComponent が子リストを持つ |

- 設計上の分岐点は「階層 = Transform 階層か、より一般的な所有ツリーか」
- Unity 型は「座標系の親子」と「論理的な所有関係」が同一視され、座標を持たないオブジェクトの扱いに歪みが出る（RectTransform の特殊化がその症状）
- Unreal の二層構造は「ゲームプレイの単位（Actor）」と「空間構造（SceneComponent）」を分離し、ライフサイクル管理（スポーン・レプリケーション・所有権）を階層と独立に扱える
- ECS 系は階層をデータに降格させたことで Transform 伝播をジョブ化しやすい反面、「親を消したら子はどうなるか」等の整合性維持を hook/observer で自前実装する必要がある

### 状態（コンポジション）

| エンジン | 状態の持ち方 |
|---|---|
| Unity | GameObject は空の容器。Component クラス（データ + 振る舞いの OOP）を合成 |
| Unreal | 継承が第一級（AActor をサブクラス化）+ ActorComponent で合成 |
| Godot | コンポーネントを持たず「子ノードの合成」がコンポジション。1 ノードに付けられるスクリプトは 1 つで、ノード型の拡張（継承寄り） |
| Bevy / Unity DOTS | コンポーネント = 純粋データ（振る舞いなし）。Entity はただの ID。アーキタイプ格納でクエリが高速 |
| Stride / Flax | Unity 型のコンポーネント合成 |

- 対立軸は「データと振る舞いを一体にするか分離するか」「合成の単位をオブジェクト内（コンポーネント）に置くか、ツリー構造そのもの（子ノード）に置くか」
- ECS の純データ設計はシリアライゼーション・スナップショット・ワーカースレッド処理と極めて相性が良い反面、「このコンポーネントの Update」という素朴なメンタルモデルを捨てる必要がある

### 更新

| エンジン | 更新モデル |
|---|---|
| Unity | MonoBehavior のマジックメソッド（Update/FixedUpdate/LateUpdate）を per-instance で呼ぶ。順序は Script Execution Order、ループは PlayerLoop API で差し替え可 |
| Unreal | Tick + TickGroup（PrePhysics/DuringPhysics/PostPhysics…）+ Tick 間の依存指定。公式ガイダンスは「なるべく Tick を切る」（イベント・タイマー駆動推奨） |
| Godot | `_process` / `_physics_process` をツリー順で呼ぶ + シグナル駆動 |
| Bevy | System（クエリを引数に取る関数）を Schedule に登録。データアクセスの重なりから自動で並列実行。順序は SystemSet で宣言 |
| Unity DOTS | SystemGroup による順序制御 + ジョブ化 |
| Stride | SyncScript.Update に加え、AsyncScript（async/await でフレームをまたぐロジックを直接書ける） |

- 対立軸は「オブジェクトごとの仮想呼び出し（per-instance）か、型ごとの一括処理（per-system）か」
- per-instance は書きやすいが、呼び出しオーバーヘッドとキャッシュ非局所性が数万オブジェクトでボトルネック化する（Unity コミュニティの Update Manager パターンはその回避策）
- per-system はデータアクセス宣言から安全な並列実行をスケジューラが自動導出できる

### 「全 Renderer クエリ」の実装実態

どのエンジンもシーングラフを歩いて描画対象を列挙してはいない。ゲームオブジェクトモデルの外側に retained なミラー構造を持つ。

- Unreal: レンダースレッドが `FScene` / `FPrimitiveSceneProxy` というミラーを保持し、コンポーネントの変更はコマンドで差分更新
- Godot: サーバーアーキテクチャ。SceneTree は RenderingServer / PhysicsServer のフロントエンドにすぎず、描画対象の列挙・カリングはサーバー側の保持構造で行う
- Unity: C++ 側がコンポーネント種別ごとの内部リスト（Renderer 一覧など）を保持。Transform 本体も C++ 側の階層バッファにあり、ジョブからは `TransformAccessArray` 経由で触る
- Bevy: 毎フレーム Extract フェーズで Main World → Render World にコピーし、レンダリングを 1 フレーム遅れでパイプライン並列化

つまり「シーングラフの設計」と「レンダラーが見る世界の設計」は別問題で、前者をどう選んでも後者には retained なミラー/登録リストが要る。

### 潮流

オーサリングの表向きはオブジェクト/ツリーモデル、実行系のバックエンドはデータ指向、というハイブリッド化が進んでいる（Unity ECS、Unreal の Mass、Godot のサーバー）。パラダイム混在の構成パターンは 3 つ：

- (a) 二重ワールド + ミラーリング（Unreal Mass 方式）: 既存資産を壊せない場合の選択。同期コード自体が複雑性の主要因になるため、グリーンフィールドで選ぶ理由はない
- (b) ECS ストレージに managed component を同居（Unity DOTS 方式）: Entity ID 空間は 1 つで、クエリが両者を横断できる
- (c) 実行モデル側だけ二本立て（Stride AsyncScript / Orleans のアクターモデル）: 重いロジックを async メソッド（アクターのターン）としてワーカープール上で走らせる

---

## Division Engine での戦略

### 基本方針: ストレージと実行モデルの直交

「データモデル（ECS ストレージ）」と「実行モデル（誰がいつコードを走らせるか）」を直交させる。ECS 向きでないオブジェクトに必要なのは別のストレージではなく別の実行モデルであり、上記 (b) + (c) を組み合わせる。「オブジェクト用の別ワールド」を作ると二重管理とミラーリング同期が複雑性の主要因になるため作らない。

なお「少数・横断的クエリ」自体は ECS ストレージ上でも遅くない（アーキタイプクエリは母数が少なければ一瞬）。ECS がつらいのは書き味（振る舞いの置き場所）であって、クエリ性能ではない。

### ワールドとストレージ

- 単一ワールド。エンティティは世代付き ID（破棄後の参照失効を検出可能にする）
- コンポーネントは 2 種類の格納を同居させる
  - **unmanaged コンポーネント**（Transform、Renderer 参照データ、物理ステートなど）: blittable。アーキタイプ/チャンク格納。ジョブから直接触る対象
  - **managed コンポーネント**（重いゲームロジックの状態）: エンティティに紐づく class インスタンス。クエリには乗るが、チャンク線形走査の対象外
- クエリ API は両者を横断して同一に扱える
- 階層（Transform 親子）は特権構造にせず、コンポーネントとして表現する。Transform 伝播はシステムの 1 つ
  - 実装した形: 子が `Parent`（親の id）と `Sibling`（前後の兄弟）を、親が `Child`（子リストの先頭と末尾）を持つ**内包双方向リンクリスト**。全て unmanaged なのでチャンク格納に乗り、ラウンドの対象にもなる。付け替え・切り離しは O(1)、子の列挙順は接続順
  - 子リストを managed な `List<Entity>` にしなかったのは、managed コンポーネントがターン専有でラウンドの対象外になり、伝播ループにポインタ追跡と GC 圧が入るため
- **階層整合性**は `World` の構造変更フック（`IStructuralHook`）で維持する。ワールド自体は階層を知らず、`HierarchyIntegrity` が破棄の直前に「子を再帰的に破棄し、自身を親のリストから外す」を行う。親を消すと部分木ごと消える（Unity / DOTS と同じ既定）。子だけ残したい場合は破棄前に `ClearParent` で切り離す
- レンダラー等のバックエンドはシーン構造とは別に retained なミラー/登録リストを持つ（詳細はグラフィックス設計時に詰める）

### 実行モデル: 2 レーン

両レーンは同一のワーカープール・スケジューラに乗る（詳細は [JobSystem.md](./JobSystem.md)）。

1. **System レーン**: アクセス宣言（read/write するコンポーネント型）からジョブ依存グラフを静的に構築し、チャンク並列で流す。Transform 伝播・カリング・物理はここ
2. **Behavior（アクター）レーン**: エンティティごとに逐次実行コンテキスト（メールボックス）を持ち、振る舞いを `async` メソッドとして書く。同一エンティティのターンは直列化、異なるエンティティのターンは by default で並列。他エンティティへのアクセスは `await` でセグメントを切り、アクセス集合を宣言した次セグメントを発行する形（動的なタスク発行）で実現され、実行時ロックは存在しない

使い分けの指針：

| | System | Behavior |
|---|---|---|
| 対象数 | 多い | 少ない |
| 実行頻度 | 毎フレーム | イベント駆動・時間軸をまたぐ |
| 1 件あたりの処理 | 軽い | 重い |
| 状態の置き場所 | unmanaged コンポーネント | managed コンポーネント（+ unmanaged の公開値） |

### レーン境界の規律

パラダイム混在の難所は境界面に集中する。以下をルール化する。

- **構造変更**（コンポーネント追加/削除、エンティティ生成/破棄）はコマンドバッファに積み、フレーム内の決まった同期点でのみ適用する
- **unmanaged データへの読み書き**は Behavior から `await` によるアクセス宣言で行う。読みは既定でフェーズ開始時点の状態（`initial` ラウンド）を返し何も待たない。書きはバッファされ、ラウンド確定時に決定的なキー順でコミットされる（[JobSystem.md](./JobSystem.md) のラウンド）。コマンドバッファ相当の仕組みは構造変更専用
- **managed コンポーネント**は class 参照を渡した時点で read-only を強制できないため、ロックは常に排他 =「エンティティのターン専有」に一本化する。他エンティティの managed を読みたいケースは (a) 相手のターンに入る（排他）、(b) 相手が unmanaged なミラー値を publish する、のいずれかに寄せる
- **読み取りの安定性**は 2 段で担保する。既定の読み（`initial` ラウンド）はフェーズ開始時のスナップショットなので常に安定。「LateUpdate 中 Transform の World 値は確定済み」のようなフェーズ保証は、フェーズごとの書き込み可能型の宣言で表現する（[LoopSystem.md](./LoopSystem.md)）
- **エンティティ参照**は世代付き ID とし、`await` から復帰した時点で相手が死んでいるケースを API レベルで強制チェックさせる（`TryGet` 形）。await をまたぐ参照失効はこの構成で最も踏みやすいバグ
- **決定性**: 両レーンとも決定的。System は静的な発行順、Behavior は「読みは `initial`、書きはキー順コミット」で結果がスレッドのタイミングに依存しない。非決定性の源は外部入力（I/O 完了、OS イベント）だけで、FrameBegin での決定的な取り込みとリプレイ記録で閉じる（[LoopSystem.md](./LoopSystem.md)）

### 未解決の論点

- オーサリングモデル（シーンのシリアライズ表現、Prefab/ネストシーン相当の合成単位）とランタイムモデルの対応付け
- レンダリングミラーの更新方式（差分コマンド vs Extract コピー）

解決済み:

- 階層整合性（親の破棄時の子の扱い）→ 上記「ワールドとストレージ」を参照
- ラウンドのラベル列を登録する API → `graph.RegisterRounds(phase, labels...)` で登録し、`Round.Label("damage")` で参照する（[JobSystem.md](./JobSystem.md)）

## 参考資料

- Unity DOTS managed components: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/components-managed.html
- Unreal MassEntity: https://dev.epicgames.com/documentation/en-us/unreal-engine/mass-entity-in-unreal-engine
- Godot サーバーアーキテクチャ: https://docs.godotengine.org/en/stable/tutorials/performance/using_servers.html
- Bevy ECS: https://docs.rs/bevy_ecs/latest/bevy_ecs/
- Stride のスクリプト種別（AsyncScript）: https://doc.stride3d.net/latest/en/manual/scripts/types-of-script.html
- Microsoft Orleans（turn-based concurrency の実例）: https://learn.microsoft.com/en-us/dotnet/orleans/
- Unity PlayerLoop: https://docs.unity3d.com/ScriptReference/LowLevel.PlayerLoop.html
- Unreal Actor Ticking: https://dev.epicgames.com/documentation/en-us/unreal-engine/actor-ticking-in-unreal-engine
