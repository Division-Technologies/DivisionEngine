# ループシステム

フレームループのフェーズ構成と、その上でのタスク実行の時間的規律に関する設計ノート。並列実行基盤（依存グラフ・ラウンド）は [JobSystem.md](./JobSystem.md)、エンティティ側の前提は [SceneManagement.md](./SceneManagement.md) を参照。

## 現状の実装

`Engine.Main` が `RootSystemGroup` を毎フレーム実行する。`RootSystemGroup` は `FixedUpdateSystemGroup` → `UpdateSystemGroup` → `PresentationSystemGroup` の順に子グループを持ち、各グループは `ITimeProvider` を通じて自身の時間軸（固定ステップ / 可変ステップ）で 0 回以上実行される。`FrameContext.ActiveTime` はグループ実行中だけ差し替えられる。

この「SystemGroup + TimeProvider」の骨格は以下の設計でもそのまま使う。フェーズは SystemGroup として表現し、Fixed 系は固定ステップのプロバイダ、それ以外は可変ステップのプロバイダを持つ。

既知の問題: `FixedUpdateTimeProvider.DoUpdate` は経過時間を `_fixedDeltaTime` ではなく `_frameCount` で割っており、`_accumulated` も更新されない。固定ステップの回数計算が意図どおりになっていないので、フェーズ設計を反映する際に書き直す（下記「固定ステップ」参照）。

## 既存エンジンの比較

| エンジン | ループ構造 |
|---|---|
| Unity | PlayerLoop。Initialization / EarlyUpdate / FixedUpdate / PreUpdate / Update / PreLateUpdate / PostLateUpdate の固定フェーズに、サブシステムがぶら下がる。`PlayerLoop.SetPlayerLoop` で差し替え可 |
| Unreal | Tick Group。TG_PrePhysics / TG_StartPhysics / TG_DuringPhysics / TG_EndPhysics / TG_PostPhysics / TG_PostUpdateWork の順に Actor/Component の Tick を配置。物理との前後関係が主軸 |
| Bevy | Schedule。Main スケジュールが First / PreUpdate / StateTransition / RunFixedMainLoop（FixedFirst〜FixedLast）/ Update / PostUpdate / Last を順に実行し、その後 ExtractSchedule で Render World へコピー、Render スケジュールがパイプライン並列で走る |
| Godot | `_process` / `_physics_process` の 2 系統。物理はサーバーが別スレッドで進め、SceneTree はコールバックを受ける |

共通しているのは「入力 → 固定ステップ（物理）→ 可変ステップ（ゲームプレイ）→ Transform 確定 → カメラ/UI → 描画」という並びで、差はフェーズ数と、描画をどこで切り離すか（Bevy の Extract、Unreal のレンダースレッド）にある。

---

## Division Engine での戦略

### フェーズ列

| # | フェーズ | 実行場所 | 主な内容 | Behaviour の再開点 |
|---|---|---|---|---|
| 1 | FrameBegin | メインスレッド | OS メッセージポンプ、入力スナップショット、時刻サンプル、SynchronizationContext 消化、外部入力の取り込み、オーサリング（アセットリフレッシュ） | — |
| 2 | Fixed×n: FixedPre | pool | 固定ステップ入力、前回 Transform の退避（補間用） | — |
| 3 | Fixed×n: FixedUpdate | pool | 固定ステップのゲームプレイ System | `await Phase.FixedUpdate` |
| 4 | Fixed×n: Physics | pool | 物理ステップ（Transform を書く） | — |
| 5 | Fixed×n: FixedPost | pool | 衝突イベント配信、物理結果の反映 | `await Phase.FixedPost` |
| 6 | Update | pool | 可変ステップのゲームプレイ System | `await Phase.Update` |
| 7 | PostUpdate | pool | アニメーション評価、IK | `await Phase.PostUpdate` |
| 8 | TransformPropagation | pool | Local → World 伝播 | — |
| 9 | LateUpdate | pool | カメラ追従、UI レイアウト | `await Phase.LateUpdate` |
| 10 | Extract | pool | 描画に必要なデータを render world へコピー | — |
| 11 | Render / Present | pool + メインスレッド（Present） | render world のみを参照。次フレームの 1〜9 と重畳可能 | — |
| 12 | FrameEnd | メインスレッド | 統計、GC ヒント | `await Phase.FrameEnd` |

- 「再開点 —」のフェーズは Behaviour のタイミング用 awaitable を提供しない。ただし Behaviour のセグメントがそのフェーズ中に走ること自体は禁止しない（後述）
- Fixed 群は 1 フレームに 0 回でも n 回でも走る。`await Phase.FixedUpdate` は各ステップで再開される

### フェーズと 2 レーンの関係

System レーンと Behaviour レーンの扱いは非対称にする。

- **System ジョブ**はフェーズに静的に所属し、フェーズ末はそのフェーズの System ジョブがすべて完了するバリアになる
- **Behaviour のセグメント**はフェーズに拘束されない。継続は空きワーカーがあれば即時再開し、フェーズをまたいで走ってよい。整合性はフェーズではなく [JobSystem.md](./JobSystem.md) の依存グラフとラウンドが担う
- フェーズ awaitable（`await Phase.Update` 等）は**タイミング用途のみ**。「毎フレーム Update で動きたい」Behaviour のための再開点であり、バリア意味論は持たない。フェーズ内の dispatch はエンティティ ID 順

この非対称は、Behaviour を「継続はホームフェーズで再開しフェーズ末で待つ」形（フレーム量子化）にする案と比較して選んだもの。フレーム量子化は非同期チェーンの各ホップに最大 1 フレームの遅延を足し、ストラグラー 1 体でフレーム全体を止め、重い処理をユーザーが手で分割する必要があった。決定性はフレーム量子化でも得られない（フェーズ内 2 セグメント目以降の発行順はタイミング依存）ため、決定性はラウンド機構で別途確保する。

### ラウンドとフェーズ

各フェーズは資源（エンティティ×型）ごとに `initial → (ユーザー定義ラベル)… → completed` のバージョン列を持つ（詳細は [JobSystem.md](./JobSystem.md)）。フェーズとの対応は次のとおり。

- `initial(P)` = フェーズ P 開始時点の状態 = `completed(P-1)`
- System の write は既定でそのフェーズの主ラウンド（`main`）に所属する
- フェーズ末の System バリアで `main` の確定条件のうち System 側が満たされる。Behaviour 側の write は発行の静止 + キー順コミットで確定する
- `completed(P)` の read はフェーズ P の全 write 確定後にしか走れないので、実質「フェーズ P+1 の initial を読む」のと等価。`completed` を読んだセグメントはフェーズ P への write 権を放棄したものとみなす

ラウンドのラベル列（順序）はフェーズごとに静的に登録する。例: Update フェーズに `initial < damage < heal < main < completed` を登録し、ダメージ計算 System が `damage` ラベルで Health を書き、回復 Behaviour が `damage` の Health を読んで `heal` で書く。

### フェーズごとの書き込み可能型

「LateUpdate 中 Transform は不変」のようなフェーズ保証は、フェーズごとに「このフェーズで write を受け付ける型」を宣言することで表現する。

- 例: TransformPropagation 以降 Extract までは Transform 系の型に対する write を受け付けない
- 受け付けない write が Behaviour から発行された場合はエラーではなく、次に受け付けるフェーズ（次フレームの FixedPre / Update）へ自動繰り延べする
- これにより「Transform の World 値は LateUpdate / Extract で確定している」が API 契約になる。Analyzer で静的に検出できる範囲は検出する

### 構造変更の同期点

コンポーネント追加/削除・エンティティ生成/破棄は「ワールド構造」資源への write としてグラフに載る（[JobSystem.md](./JobSystem.md)）。ほぼ全タスクと競合するため、実際にはフェーズ境界で適用されるタスクになる。

- 初期方針: 全フェーズ境界で適用する。バリアで誰もチャンクを走査していないので、アーキタイプ移動に適したタイミング
- アーキタイプ移動コストが問題になった場合、適用するフェーズ境界を間引く（DOTS の Begin/End EntityCommandBuffer と同じ調整）

### 固定ステップ

- アキュムレータ方式。`realtime - initial` から期待ステップ数を計算し、不足分だけ Fixed 群を回す
- 最大キャッチアップ回数を設ける（spiral of death 対策）。上限を超えた分は捨て、シミュレーション時刻を実時間に追従させる
- Transform 補間: 物理が固定ステップで Transform を書き、Presentation が可変ステップの場合、補間なしだと低レート物理でカクつく。FixedPre で前回 Transform を退避し Extract 時に補間する設計余地を残す。初期実装で入れるかは未決

### メインスレッドの役割

- Thread-affinity が必要な作業（OS メッセージポンプ、入力、Present、エディタ連携）は FrameBegin / Present / FrameEnd に限定する
- それ以外の時間、メインスレッドはワーカープールの 1 ワーカーとして参加する。遊ばせない
- 既存の `DivisionSynchronizationContext` はメインスレッド親和のキューとして残す（エディタ / OS 連携用）。Behaviour の `await` は SynchronizationContext を捕捉せず、専用の AsyncMethodBuilder が継続をスケジューラへ直接送る。ここを混同すると全ターンがメインスレッドに集まる

### 外部入力の取り込み

I/O 完了・タイマー・OS イベント・ネットワーク受信は発行順を乱す最後の源になる。

- 外部イベントは到着時にはキューに積むだけとし、FrameBegin で決定的なキー（到着フレーム内の種別 + 通し番号）順に取り込む
- 取り込んだ列をリプレイ用に記録できるようにする。これで決定性は「入力列に対する決定性」として閉じる
- 外部 await から戻る Behaviour の継続もこの取り込みに従う。つまり外部 await の完了は次フレームの FrameBegin まで観測されない（同一フレーム内の内部 await は即時再開）

### レンダリングのパイプライン化

Extract 以降は render world しか参照しないので、フレーム N の Render / Present とフレーム N+1 の FrameBegin〜LateUpdate を重畳できる（Bevy の pipelined rendering、Unreal のレンダースレッドに相当）。

- 代償は入力レイテンシ +1 フレーム
- ポリシースイッチにする。設計上は重畳可能にしておき、初期実装は直列
- Extract → Render の境界は C# / C++（グラフィックス）の受け渡し境界でもある

## 未決事項

- パイプライン化の既定（直列開始で進める想定）
- Transform 補間を初期実装に含めるか
- 構造変更の同期点の粒度（全フェーズ境界で開始）
- ラウンドラベル列の登録 API（属性 / 明示登録）
- 固定ステップの既定値と最大キャッチアップ回数

## 参考資料

- Unity PlayerLoop: https://docs.unity3d.com/ScriptReference/LowLevel.PlayerLoop.html
- Unity 実行順序: https://docs.unity3d.com/Manual/ExecutionOrder.html
- Unreal Actor Ticking / Tick Groups: https://dev.epicgames.com/documentation/en-us/unreal-engine/actor-ticking-in-unreal-engine
- Bevy Main schedule / Fixed timestep: https://docs.rs/bevy/latest/bevy/app/struct.Main.html
- Bevy pipelined rendering: https://docs.rs/bevy/latest/bevy/render/pipelined_rendering/index.html
- Glenn Fiedler, "Fix Your Timestep!": https://gafferongames.com/post/fix_your_timestep/
- Unity DOTS EntityCommandBuffer（同期点の設計）: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/systems-entity-command-buffers.html
