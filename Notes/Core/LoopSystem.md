# ループシステム

フレームループのフェーズ構成と、その上でのタスク実行の時間的規律に関する設計ノート。並列実行基盤（依存グラフ・ラウンド）は [JobSystem.md](./JobSystem.md)、エンティティ側の前提は [SceneManagement.md](./SceneManagement.md) を参照。

## 現状の実装

`Engine.RunFrame` が `FrameLoop`（`SystemGroup`）を毎フレーム実行する。`FrameLoop` は下記フェーズ列を `PhaseGroup`（1 フェーズ = 1 グループ）として持ち、固定ステップの 4 フェーズは `TimeSteppedGroup<FixedUpdateTimeProvider>` の子としてステップごとに回る。各 `PhaseGroup` は `BeginPhase` → 子 System の発行 → （再開フェーズなら）`ResumePhase` → `EndPhase` を行う。可変ステップのフェーズはフレーム時計（`Engine` の `UpdateTimeProvider`）の時刻を使う。

`FrameLoop[PhaseId]` でフェーズに System を追加し、`FrameLoop.Freeze<T>(phases...)` で凍結型を宣言する。`Engine.AddSystem(ISystem)` は FrameBegin への追加。

`FixedUpdateTimeProvider` はアキュムレータ方式 + 最大キャッチアップ回数（既定 8）で、超過分は原点をずらして捨てる（シミュレーション時刻は飛ばず実時間に遅れる。Unity の `maximumDeltaTime` と同じ挙動）。`UpdateTimeProvider` は最初の更新を原点とし、初回のデルタは 0。`Realtime.FromTicks` / `FromSeconds` でテスト・リプレイ用に明示的な時刻を作れる。

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

| # | フェーズ | 実行場所 | 主な内容 | Behavior の再開点 |
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

- 「再開点 —」のフェーズは Behavior のタイミング用 awaitable を提供しない。ただし Behavior のセグメントがそのフェーズ中に走ること自体は禁止しない（後述）
- Fixed 群は 1 フレームに 0 回でも n 回でも走る。`await Phase.FixedUpdate` は各ステップで再開される

### フェーズと 2 レーンの関係

System レーンと Behavior レーンの扱いは非対称にする。

- **System ジョブ**はフェーズに静的に所属し、フェーズ末はそのフェーズの System ジョブがすべて完了するバリアになる
- **Behavior のセグメント**はフェーズに拘束されない。継続は空きワーカーがあれば即時再開し、フェーズをまたいで走ってよい。整合性はフェーズではなく [JobSystem.md](./JobSystem.md) の依存グラフとラウンドが担う
- フェーズ awaitable（`await Phase.Update` 等）は**タイミング用途のみ**。「毎フレーム Update で動きたい」Behavior のための再開点であり、バリア意味論は持たない。フェーズ内の dispatch はエンティティ ID 順

この非対称は、Behavior を「継続はホームフェーズで再開しフェーズ末で待つ」形（フレーム量子化）にする案と比較して選んだもの。フレーム量子化は非同期チェーンの各ホップに最大 1 フレームの遅延を足し、ストラグラー 1 体でフレーム全体を止め、重い処理をユーザーが手で分割する必要があった。決定性はフレーム量子化でも得られない（フェーズ内 2 セグメント目以降の発行順はタイミング依存）ため、決定性はラウンド機構で別途確保する。

### ラウンドとフェーズ

各フェーズは資源（エンティティ×型）ごとに `initial → (ユーザー定義ラベル)… → main → completed` のバージョン列を持つ（詳細は [JobSystem.md](./JobSystem.md)）。1 フェーズの流れ:

1. `BeginPhase(P)`: ラウンドを作り、取り込みキュー（外部 await の完了、繰り延べセグメント、`completed` 読み、新規 `Start`）を TurnId 順に発行する。このフェーズで再開する待機リストはこの時点でスナップショットする
2. System が静的にジョブを発行する（型レベル、インプレース）
3. `ResumePhase(P)`: スナップショットした待機ターンを TurnId 順に再開し、エントリを閉じる（以降に開始・再開するターンは次フェーズ）
4. Behavior のセグメントが走る。read はエンティティレベルの依存で System の write の後に並ぶ。write はラウンドへバッファされる
5. `EndPhase(P)`: 静止を待ち（ターンごとのセグメント数上限で有界）、残りのラウンドを閉じ、バッファをラウンド順 → (TurnId, seq) 順にメインスレッドでコミットする。`completed` 読みは取り込みへ回す

対応関係:

- `initial(P)` = フェーズ P の System 適用後・Behavior コミット前の状態。`completed(P-1)` に P の System の効果を足したもの
- System はラウンドに属さない（Behavior より前に走る）。System が Behavior のコミット結果を見たければ次フェーズで読む
- `completed(P)` の read は次フェーズの `initial` 読みとして再発行される。`completed` を読んだセグメントはフェーズ P への write 権を放棄したものとみなす
- 待機リストのスナップショットにより、新しく `Start` した Behavior は「次のフェーズで最初のセグメントが走り、その次の該当フェーズで再開される」。2 フェーズ分の遅延と引き換えに、開始タイミングがワーカー数や実行速度に依存しない

ラウンドのラベル列（順序）はフェーズごとに静的に登録する（`graph.RegisterRounds(PhaseId.Update, "damage", "heal")`）。例: 攻撃 Behavior が `damage` ラウンドに `Modify` し、回復 Behavior が `damage` の Health を読んで `main` に `Modify` する。

### フェーズごとの書き込み可能型

「LateUpdate 中 Transform は不変」のようなフェーズ保証は、フェーズごとに「このフェーズで write を受け付けない型」（凍結型）を宣言することで表現する。

- 例: TransformPropagation 以降 Render までは Transform 系の型を凍結する
- System の型レベル write が凍結型に当たる場合は発行時にエラー（設定ミス扱い）
- Behavior のバッファ書き込みはエラーにせず、コミット時に持ち越して次に許可されるフェーズのコミットで適用する（キー順、そのフェーズ自身の書き込みより先）
- これにより「Transform の World 値は LateUpdate / Extract で確定している」が API 契約になる。Analyzer で静的に検出できる範囲は検出する

`FrameLoop.Freeze<T>(phases...)` として実装し、Transform 系の既定設定は `FrameLoop` のコンストラクタに入れてある。凍結範囲が 2 つの型でずれる点に注意する:

| 型 | 凍結するフェーズ | 理由 |
|---|---|---|
| `LocalTransform` | TransformPropagation, LateUpdate, Extract, Render | 伝播の入力。伝播が始まった後に書いても、その変更はどのみち次フレームまで反映されない。凍結することで「黙って 1 フレーム古い値が使われる」ではなく「明示的に次フレームへ持ち越す」になる |
| `WorldTransform` | LateUpdate, Extract, Render | 伝播の**出力**なので、TransformPropagation では書けなければならない。確定するのはその直後から |

### 構造変更の同期点

コンポーネント追加/削除・エンティティ生成/破棄は「ワールド構造」資源への write としてグラフに載る（[JobSystem.md](./JobSystem.md)）。ほぼ全タスクと競合するため、実際にはフェーズ境界で適用されるタスクになる。

- 初期方針: 全フェーズ境界で適用する。バリアで誰もチャンクを走査していないので、アーキタイプ移動に適したタイミング
- アーキタイプ移動コストが問題になった場合、適用するフェーズ境界を間引く（DOTS の Begin/End EntityCommandBuffer と同じ調整）

`JobGraph.EndPhase` が、ラウンドの write をコミットした後に構造変更を適用する。

| 記録元 | 経路 | 適用順のキー |
|---|---|---|
| System | `graph.Commands`（フェーズ共有の 1 本）。記録するジョブは `Write(graph.Commands.Resource)` を宣言する | 記録順（スケジューラが記録ジョブ同士を直列化するので発行順に一致） |
| Behavior | `context.Commands`（**ターンごとに 1 本**） | `TurnId` 順 |

Behavior だけターンごとにバッファを分けるのは性能のためではなく**決定性のため**。セグメントは動的発行でタスクの実行順がタイミング依存なので、1 本の共有バッファに記録すると「先に走ったセグメントが先」になってしまう。ターンごとに分けてフェーズ末に `TurnId` 順で流せば、値の write と同じキーで並ぶ。副作用として記録時の競合もなくなる。

構造変更が見えるのは**次のフェーズから**。ラウンドのコミットと同じ 1 フェーズ遅延。System の適用が Behavior より先なのは、フェーズの System がシミュレーション本体で、Behavior はフェーズ開始時点の状態に対する反応だから。

フェーズ途中で適用したい場合は、自前の `EntityCommandBuffer` と `graph.SchedulePlayback` を使う（こちらは同期点をタスクとしてグラフに載せる形）。

### 固定ステップ

- アキュムレータ方式。`realtime - origin` から期待ステップ数を計算し、不足分だけ Fixed 群を回す
- 最大キャッチアップ回数を設ける（spiral of death 対策、既定 8）。上限を超えた分は原点を前にずらして捨てる。シミュレーション時刻は連続のまま実時間に遅れる（時刻が飛ぶより、ゲームプレイにとって安全）
- Transform 補間: 物理が固定ステップで Transform を書き、Presentation が可変ステップの場合、補間なしだと低レート物理でカクつく。FixedPre で前回 Transform を退避し Extract 時に補間する設計余地を残す。初期実装で入れるかは未決

### メインスレッドの役割

- Thread-affinity が必要な作業（OS メッセージポンプ、入力、Present、エディタ連携）は FrameBegin / Present / FrameEnd に限定する
- それ以外の時間、メインスレッドはワーカープールの 1 ワーカーとして参加する。遊ばせない
- 既存の `DivisionSynchronizationContext` はメインスレッド親和のキューとして残す（エディタ / OS 連携用）。Behavior の `await` は SynchronizationContext を捕捉せず、専用の AsyncMethodBuilder が継続をスケジューラへ直接送る。ここを混同すると全ターンがメインスレッドに集まる

### 外部入力の取り込み

I/O 完了・タイマー・OS イベント・ネットワーク受信は発行順を乱す最後の源になる。

- 外部イベントは到着時にはキューに積むだけとし、FrameBegin で決定的なキー順に取り込む
- 取り込んだ列をリプレイ用に記録できるようにする。これで決定性は「入力列に対する決定性」として閉じる
- 外部 await から戻る Behavior の継続もこの取り込みに従う。つまり外部 await の完了は次フレームの FrameBegin まで観測されない（同一フレーム内の内部 await は即時再開）

実装済みの範囲: 外部 await と `RunBackground` の完了は `JobGraph` の到着キューに溜まり、`Engine.RunFrame` の冒頭で `AdmitExternal` が (TurnId, ターン内連番) のキー順に取り込む。連番は await を開始した時点でセグメント内で採番するので、キーは実行タイミングに依存しない。`FrameLog` は各フレームの `Realtime` と取り込んだキー列を記録し、`Engine.Replay(log)` は記録どおりの時刻・キーで再生する（記録されたキーが未到着なら到着を待ち、記録外の到着は保留する）。OS 入力・ネットワークは未実装で、同じ枠組みに載せる予定。

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
- 固定ステップの既定値（現状 0.02s、最大キャッチアップ 8 回は暫定）

## 参考資料

- Unity PlayerLoop: https://docs.unity3d.com/ScriptReference/LowLevel.PlayerLoop.html
- Unity 実行順序: https://docs.unity3d.com/Manual/ExecutionOrder.html
- Unreal Actor Ticking / Tick Groups: https://dev.epicgames.com/documentation/en-us/unreal-engine/actor-ticking-in-unreal-engine
- Bevy Main schedule / Fixed timestep: https://docs.rs/bevy/latest/bevy/app/struct.Main.html
- Bevy pipelined rendering: https://docs.rs/bevy/latest/bevy/render/pipelined_rendering/index.html
- Glenn Fiedler, "Fix Your Timestep!": https://gafferongames.com/post/fix_your_timestep/
- Unity DOTS EntityCommandBuffer（同期点の設計）: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/systems-entity-command-buffers.html
