# ジョブシステム

ワーカースレッド上での並列実行基盤の設計ノート。エンティティ管理側の前提は [SceneManagement.md](./SceneManagement.md)、フレームループとの対応は [LoopSystem.md](./LoopSystem.md) を参照。

## 要件

- **統一ワーカープール**: System レーンのジョブと Behavior レーンのターン（セグメント）を同一のプール・スケジューラに乗せる。プールを分けるとコア数の取り合いとレイテンシ注入が起きるため分けない
- **非ブロッキング**: 待機はすべて「継続をキューに積んでワーカースレッドを手放す」形で実現し、ワーカースレッドを OS レベルでブロックしない
- **決定性**: 実行結果がスレッドのタイミングに依存しない。決定性は「入力列に対する決定性」として定義し、外部入力の取り込みは [LoopSystem.md](./LoopSystem.md) で規律する
- **並列化を既定に**: ユーザーが何も指定しないパスが最も並列に走るようにする

## 実行モデル: 宣言されたアクセスからの依存推論

すべてのタスクは実行前に**アクセス集合**（どの資源を read / write するか）を宣言し、**直列の発行ストリーム**の順に依存グラフへ追加される。競合するタスク同士の順序はグラフが固定し、競合しないタスク同士は任意の順で並列に走る。結果は「発行順に逐次実行したのと同一」になる（逐次意味論）。

先行例:

- Legion（Stanford の HPC ランタイム）: 論理リージョンへの権限（read / write / reduce）宣言から依存を推論し、逐次意味論を保証する。最も近い先行例
- OpenMP `task depend(in/out/inout)`、StarPU: 同じ発想の小型版
- Unity DOTS: System 単位のアクセス宣言 + JobHandle 依存。`EntityCommandBuffer.ParallelWriter` の sortKey は「キューへの書き込み順を決定的にする」実例
- Rust の借用規則（shared xor mutable）をタスク間に適用したものとも言える

この方式では**実行時ロックは存在しない**。以前検討した多粒度ロック（IS/IX/S/X）は同じ情報（エンティティ×型のアクセス集合）を実行時に使うものだったが、発行時に使って依存辺にすることで置き換えられる。

### 資源

依存推論の対象となる資源は 4 種類。

| 資源 | 粒度 | 主な利用者 |
|---|---|---|
| コンポーネント | エンティティ×型。型レベルの集約ノードの下にエンティティレベルの疎エントリをぶら下げる多粒度構造 | System（型レベル）、Behavior（エンティティレベル） |
| キュー | メールボックス、イベントストリーム等 | Behavior 間メッセージ、イベント配信 |
| ワールド構造 | ワールド全体で 1 資源（将来アーキタイプ単位に細分化の余地） | 構造変更（コンポーネント追加/削除、エンティティ生成/破棄） |
| フェーズ | タイミング用 awaitable（[LoopSystem.md](./LoopSystem.md)） | Behavior の再開点 |

対象は unmanaged コンポーネントのみ。managed コンポーネントはエンティティのターン専有（常に排他）で保護されるため、この機構の対象外（[SceneManagement.md](./SceneManagement.md) のレーン境界の規律を参照）。

### 依存推論のデータ構造

資源ごとに「最後の writer」と「それ以降の reader 集合」を持つ。新タスクの依存は read なら最後の writer、write なら最後の writer + 全 reader。エンティティレベルの疎エントリは競合時のみ生成し、型レベルの集約ノードが System の write(T) と全エンティティレベルタスクとの依存を表現する。

発行時の依存計算はホットパスであり、ここの実装品質がスループットを決める。

### 2 種類の発行

| | System レーン | Behavior レーン |
|---|---|---|
| 発行 | 静的（フェーズごとのプログラム順） | 動的（`await` ごとにセグメントを切って発行） |
| アクセス宣言 | 型レベル、コード上の宣言 | エンティティレベル、`await` の引数 |
| 書き込み | インプレース（writer 集合が静的に分かるためグラフ辺で十分） | バッファし、ラウンド確定時にキー順コミット |
| 発行順の決定性 | 自明 | ラウンド機構で確保（後述） |

Behavior のセグメントとは、`async` メソッドを `await` で区切った区間。`await other.ReadAsync<T>()` は「現在のセグメントを終了し、アクセス集合 `{other: T read}` を宣言した次のセグメントを発行する」操作であり、動的ロックは動的なタスク発行として実現される。セグメントの実行中にアクセス先を増やすことはできず、Analyzer で担保する。

turn-based concurrency（同一エンティティのセグメントは直列、異なるエンティティは並列）は Microsoft Orleans の grain と同じモデル。

## ラウンド: フェーズ内バージョン

動的発行の唯一の穴は「継続の発行順が実行タイミングで決まる」ことにある。並列に走るセグメント A と B が完了して次セグメントを発行するとき、どちらが先かはタイミング依存で、アクセス集合が競合すれば結果に影響する（決定的マルチスレッディング、Kendo や DTHREADS が扱ってきた問題）。これをラウンド機構で解決する。

### 定義

各資源（エンティティ×型）はフェーズ内でバージョン列を持つ。

```
initial → (ユーザー定義ラベル)… → main → completed
```

- `initial`: フェーズ開始時点の状態。`completed(前フェーズ)` と同一
- `main`: write の既定ラウンド
- ユーザー定義ラベル: write 時に指定すると、その write 完了後の状態にラベルが付く
- `completed`: フェーズ内の全 write 完了後の状態

read は特定のラウンドを要求する。**既定は `initial`**。write は既定で `main` に所属し、明示するとラベル付きラウンドに所属する。

フェーズ単位の MVCC（多版同時実行制御）と見ることができる。

### 既定パスは何も待たない

`initial` の read はフェーズ開始時の状態を読むだけなので、writer を待たず、writer を止めない。ラベル付き read と `completed` read だけが依存辺を作る。したがって「read は initial、write は main」だけで書かれた Behavior は自分のターン以外を何も待たず、これが最も並列に走るパスになる。ユーザーはラウンドを意識せずに済む。

### 成立条件

1. **ラウンドはフェーズ内で全順序**。ラベルの順序（`initial < damage < heal < main < completed` 等）はフェーズごとに静的に登録する。順序がないと「`damage` の read は `heal` の write を待つべきか」が決まらない
2. **セグメントは自分の現在ラウンドより前のラウンドしか読めない**。ラウンド k 以降の read を要求した時点で、そのターンは k 以前への write 権を放棄する（ラウンドを前進する）。これにより依存辺は常にラウンド順序の逆方向にしか張られず、グラフは構築時点で非循環になる。**デッドロックは原理的に起きない**
3. **ラウンドの確定条件**は (a) そのラウンドへの新規発行がもう起きない（ラウンドに居るセグメントが全員離れた = 発行の静止、ラウンド単位でグローバル）かつ (b) その資源への当該ラウンドの write を宣言したタスクが全部完了した（資源単位）。write は発行時にアクセス先を宣言しているのでこれが計算できる
4. **同一ラウンド・同一資源への複数 write は決定的キー `(ターン ID, ターン内シーケンス)` で順序付け**る。発行タイミングに依存させないため、Behavior の write はバッファし、ラウンド確定時にキー順でコミットする。セグメント内の read-your-own-write はローカルオーバーレイで見せる
5. **`initial` を後から読めるようにスナップショットを保持する**。動的発行では「write が実行された後に `initial` の read が発行される」ことが普通に起きる。フェーズ内で最初に write されるチャンクをコピーしておく（copy-on-first-write）

### 境界ケース

- **`completed` を読んだ後の write**: 矛盾なので、`completed` read はそのフェーズの write 権放棄と定義し、以降の write は次フェーズに繰り越す（条件 2 の特殊ケース）
- **ストラグラー**: ラウンド k に長い計算を持つセグメントがいると k の確定が遅れ、k 以降を読む全員が待つ。ただし既定パス（`initial` read）は無影響。影響範囲は「ラベル付き read を使ったターン」に限定される
- **System レーンとの合流**: System の write もラウンドに所属する（既定は `main`）。Behavior が `initial` を読むと System 書き込み前の値、System の書き込み後を読みたければその write のラベルを要求する
- **同一ラベルへの複数 writer**: 条件 4 のキー順で解決する。ラベルは「write の集合」であり、その read は集合内の全 write を待つ

### スナップショットの扱い

- スナップショットは**型ごとのオプトイン**にする（`[Snapshot]` 属性等）。毎フレーム書かれる型（Transform）は実質ダブルバッファになり、その型のメモリが 2 倍になる
- オプトインしていない型で「write 後の `initial` read」が発行された場合はエラーではなく、次フェーズの `initial` に自動繰り延べする。決定性は保たれ、遅延が 1 フェーズ増えるだけなので安全側

### 決定性の帰結

- ラベルを使わないターンは、読みは即時（待たない）、書きはフェーズ末にキー順コミット、で決定的
- ラベル付き read を使うターンだけが局所的にラウンド待ちを払う
- 「フレーム量子化して継続をフェーズで再開する」案との比較は [LoopSystem.md](./LoopSystem.md) を参照。決定性は量子化では得られず、ラウンド機構で得る

## キューと構造変更

### キュー

キューへの書き込み順を保証するため、キューも資源としてグラフに載せる。ただし 1 本のキューへの write を排他にすると書き手が全員直列化されるので、**タスクごとの部分キューに書き、読み出し時に発行順でマージする**（DOTS `EntityCommandBuffer.ParallelWriter` の sortKey と同じ）。メールボックス（エンティティごと）は受信側の直列化点なので、この方式で順序を保ったまま送信側は並列に走れる。

### 構造変更

コンポーネント追加/削除・エンティティ生成/破棄は「ワールド構造」資源への write。ほぼ全タスクと競合するため巨大な競合集合を持ち、自然にフェーズ境界の同期点になる。同期点を特別扱いのバリアとしてではなく、ただのタスクとしてグラフに載せる点が要点。粒度は「ワールド全体で 1 資源」から始める。

## 実装メモ: System レーン

System レーンは `DivisionEngine/Scheduling/` に実装した。設計との対応と、実装して分かったこと:

- 資源 id は「0 = 構造、正 = コンポーネント型、負 = 名前付き / インスタンス固有」の 1 つの整数に符号化した。コンポーネントアクセスは構造の read を暗黙に含み、構造の write（構造変更、従来型の即時 System）は全エンティティアクセスと競合する。**名前付き資源は構造と独立**なので、構造 write だけでは順序付けされない。コマンドバッファには固有の資源 id を持たせ、記録側は write、playback 側は「構造 write + バッファ write」を宣言する。この宣言が抜けていた版は逐次オラクルが 1 回目で検出した
- チャンク並列ジョブのチャンク集合は**ノードが ready になった時点**でスナップショットする。発行時にスナップショットすると、依存先の構造変更（playback）が追加したチャンクが見えない
- メインスレッドはバリア待ちの間ワーカーとして参加し、メインスレッド専用キューを唯一 drain できるスレッドでもある。完了通知は「待機中フラグ」付きのセマフォで行い、待たれていないノードの完了は通知しない
- 安全機構はワーカーごとのスレッド静的な「実行中の AccessSet」で実現し、アクセサ側で照合する。コストは小さな sorted 配列の二分探索
- 例外はノードに記録してスケジューラに報告し、次の `Wait` / `WaitAll` で `JobFailedException` として再スローする。ワーカーは止まらない

## 実装メモ: Behavior レーン

Behavior レーンは `DivisionEngine/Behaviors/` に実装した（トラッカーとグラフの拡張は `Scheduling/`）。

- **ターンの単位は Behavior インスタンス**（= 1 本の async メソッド）。1 エンティティに複数の Behavior を付ければそれぞれ独立したターンになる。同一 Behavior のセグメントは「前セグメントが次を発行する」構造上重ならないが、念のため次セグメントは前セグメントのノードに明示的に依存させている
- **カスタム AsyncMethodBuilder の本当の役割**は「外部 await の迂回」だった。エンジン自身の awaitable は `UnsafeOnCompleted` で自分でセグメントを発行できるので、ビルダーは `IBehaviorAwaiter` 以外の awaiter（`Task` 等）の継続を取り込みキュー行きに差し替えるだけでよい。ステートマシンのプール化はまだしていない
- **awaiter の状態は構築時に持たせる**。最初の await でステートマシンがボックス化（コピー）されるのは `OnCompleted` 呼び出しの前なので、`OnCompleted` の中で awaiter 自身に書いた状態はコピー側に残らない。アクセスハンドルや結果ボックスは `GetAwaiter()` の時点で確保する。標準の awaiter が `OnCompleted` で自身を変更しないのと同じ理由
- **多粒度トラッカー**は型ノードに `EntityWriters`（IX）/ `EntityReaders`（IS）を追加し、エンティティ×型の疎エントリは型の write 世代（epoch）で失効させる。型 write は全リストをクリアし epoch を進める。エンティティ read は型の最終 writer とエンティティの最終 writer に、エンティティ write はさらに型 reader とエンティティ reader に依存する
- **動的発行は発行ロックで直列化**し、ワーカーからの発行を許可した。フレームを閉じる（`EndFrame`）間に発行されたセグメントは次フレームの取り込みへ繰り延べる。これによりフェーズ await を含まない読みの連鎖でもフレームが有界になる（1 フレームに 1 ホップ以上は進む）
- **セグメントの実行前にエンティティの生存を確認**し、破棄されていればターンをキャンセルする（async メソッドは放置され、完了しない）。相手エンティティの消失は `Read` が `EntityNotAliveException` を投げ、`TryRead` が null を返す
- **アクセスハンドル（`EntityAccess`）**はセグメント終了で無効化する。セグメント内の宣言外アクセスと、終了後のハンドル使用はどちらも Behavior の失敗として `JobFailedException` → `BehaviorFailedException` の連鎖で `WaitAll` に浮上する
- **フェーズ再開は TurnId 順に発行**する。実行は並列なので観測順は不定（0 ワーカーなら発行順 = 実行順）。決定性そのものはラウンドで扱う
- 外部 await と `RunBackground` の完了は取り込みキューに積まれ、フレーム冒頭の `AdmitExternal` で TurnId 順に発行される

### 未解決: 細粒度タスクでは並列の方が遅い

1,000 Behavior × 5 セグメント = 1 フレーム 5,000 セグメントのベンチマークで、
**ワーカー 23 の方が ワーカー 0 より遅い**（1.34 ms → 2.9 ms、24 コア機）。
System レーンのチャンク並列は素直に速くなる（10 万エンティティの積分が 188 µs → 47 µs）ので、
問題はセグメントという細粒度タスクに対するスケジューラの作りにある。

候補は 2 つで、どちらも「待ちがどこに出ているか」を見ないと切り分けられない。

1. **発行ロックの競合**。動的発行を 1 本のロックで直列化しているため、ワーカーが増えるほど発行が詰まる
2. **セグメントごとのセマフォ起床**。1 タスクごとにカーネル遷移を払っており、タスクが数百 ns の世界では支配的になりうる

対処の候補はワーカーのスピン待ち、起床のバッチ化、スレッド別キューと work-stealing、
`TaskNode` とクロージャのプール化。ただし**測ってから決める**。
このための計装は [Profiling.md](./Profiling.md) にある。

### 未解決: ワーカー数はコア数から決められない

Tracy のキャプチャで分かったこと。Apple M1（**4 P コア + 4 E コア**）で、
5 万エンティティの Transform 伝播をワーカー数を振って測ると、スケールが単調でない。

| ワーカー | スレッド | ms/frame | 速度比 |
|---|---|---|---|
| 0 | 1 | 3.928 | 1.00x |
| 1 | 2 | 2.009 | 1.96x |
| 2 | 3 | 1.551 | 2.53x |
| **3** | **4** | **1.239** | **3.17x** |
| 4 | 5 | 1.266 | 3.10x |
| 5 | 6 | 1.276 | 3.08x |
| 6 | 7 | 1.212 | 3.24x |
| 7 | 8 | 1.114 | 3.53x |

**4・5・6 ワーカーは 3 ワーカーより遅い。** 4 スレッドで 3.17x に達したあと、
スレッド数を倍にしても 3.53x にしかならない。`JobScheduler.CreateDefault()` の
`ProcessorCount - 1`（この機械では 7）は、たまたま谷を抜けた位置にあるだけで根拠がない。

原因はコアの非対称性で、ゾーンの分布がそれを示している。**同じ大きさのバッチ**なのに:

| | メイン p50 | メイン p90 | ワーカー p50 | ワーカー p90 |
|---|---|---|---|---|
| 3 ワーカー（4 スレッド） | 256 µs | 328 µs | 256 µs | 266〜330 µs |
| 7 ワーカー（8 スレッド） | 277 µs | 316 µs | **340 µs** | **850 µs** |

4 スレッドでは全スレッドが揃う（p90/p50 が 1.0〜1.3）。8 スレッドではワーカーだけ p90/p50 が 2.5 に開き、
**中央値そのものが 33% 悪化する**。メインがほとんど影響を受けない点がメモリ帯域説を否定する
（帯域なら全スレッドが等しく遅くなる）。E コアに載ったワーカーが同じバッチに 2.5 倍かけ、
粗い粒度ではフレームがその遅い 1 本を待つ。

ワーカー数はコア数ではなく**性能コア数**から決めるべきで、macOS なら
`hw.perflevel0.logicalcpu` で取れる。ただし既定値を変える前に、単調でない領域が
コア構成由来なのかスケジューラ由来なのかを他の機械でも確認する。

### 未解決: 全スレッド同時のストール

同じキャプチャで、3 ms を超えるジョブは全体の 0.47%（405 本）だが、
スレッド時間の 7%（42.91 s 中 3.05 s）を占める。重なりで束ねると上位の塊は
**8 スレッド全部が同時に伸びており**、最長 41 ms。

GC ではない。20 秒の実行で gen0 33 回 / gen1 2 回 / gen2 0 回、停止の合計 17 ms（実時間の 0.1%）、
確保は 13.8 KB/frame。OS のプリエンプションと見られるが未特定。
プロファイラを同じ飽和した機械で動かしている影響も混じる（キャプチャ中 659 fps 対 非キャプチャ 732 fps）。

なお計装の網羅性は確認済みで、ジョブとアイドルのゾーンで
**ワーカーのスレッド時間の 99.9%、メインの 97.5%** が説明できている。

## 実装メモ: ラウンド

ラウンドは `Behaviors/Round.cs` と `JobGraph`（フェーズ / ラウンド / コミット）に実装した。設計からの主な確定・変更点:

- **`initial` の定義を「そのフェーズの System 適用後、Behavior コミット前」に確定した**。System ジョブはフェーズ冒頭で静的に発行され、Behavior の再開はその後（`BeginPhase` → System の発行 → `ResumePhase` → `EndPhase`）なので、トラッカーがエンティティ read を型 write の後に並べる。Behavior の write はすべてバッファされフェーズ末にコミットされるため、フェーズ中のインプレースデータは Behavior からは不変に見える。この結果、設計で検討していた `[Snapshot]`（copy-on-first-write）は**不要**になった
- **累積更新は `Modify<T>(e, f, round)`**。`Write` は last-writer-wins（キー順）なので、read-modify-write を複数ターンから行うと最後の書き手だけが残る。`Modify` はコミット時にその時点の値へ関数を適用するもので、キー順に適用されるため非可換な更新も決定的になる。関数は純粋でなければならない（コミット時とオーバーレイ読み取り時の両方で呼ばれる）
- **ラベル付き read はゲートノードで待つ**。ラウンド k のゲートは「エントリが閉じている」かつ「ラウンド ≤ k に居るターンが 0」で開く。エントリはそのフェーズの `ResumePhase` 完了時に閉じ、それ以降に開始・再開されるターンは次フェーズへ送られる（閉じたラウンドへの書き込みを構造的に防ぐ）
- **ラベル付き read の値は閉じたラウンドのバッファをキー順に重ねて計算する**（インプレースはフェーズ中不変なので MVCC の層はバッファそのもの）。`completed` の read はフェーズ末まで待ち、次フェーズの `initial` 読みとして再発行される
- **ターンのラウンド前進は発行時に行う**。write ハンドル付きセグメントの発行はその write ラウンドへ、ラウンド k の read は k+1 へ前進させる。read だけのセグメントは前進しない（後で任意のラウンドに書ける余地を残す）。`Modify` は前進させない（同一セグメント内の別ラウンドのバッファがまだ可変なため）
- **`Start` は常に次の `BeginPhase` で TurnId 順に発行される**。以前の「フェーズ外なら即時」は、0 ワーカーでは次の `WaitAll` まで走らないため、Behavior が動き始めるフレームがワーカー数に依存していた。決定性ハーネスが最初に検出した非決定性がこれ
- **`ResumePhase` は `BeginPhase` 時点の待機リストのスナップショットを再開する**。フェーズ中に待機したターン（取り込みから発行された最初のセグメントなど）は次回に回す。セグメントの実行速度に結果が依存しないようにするため
- **フェーズの有界性はターンごとのセグメント数上限（`MaxSegmentsPerPhase`、既定 256）で担保する**。当初の「`EndPhase` 中の発行は繰り延べ」だけだと、エンジンループでは `ResumePhase` 直後に `EndPhase` が来るため、再開セグメントの次ホップが常に次フェーズへ回ってしまった。上限は回数ベースなので決定的
- 決定性ハーネス（`DeterminismTests.BehaviorScenario`）: 攻撃者が `damage` ラウンドに `Modify`、回復者が `damage` を読んで `main` に `Modify` し、共有エンティティへ `Write`（last-writer-wins）、System が位置を積分する。ワーカー数 0/1/3/7 × ジッタ有無 × 2 回でハッシュ一致

## API スケッチ

実装済みの API:

```csharp
public sealed class Chaser : Behavior
{
    protected override async BehaviorTask Run(BehaviorContext ctx)
    {
        while (true)
        {
            // タイミング用 awaitable（バリア意味論なし）。再開直後のセグメントはデータアクセスを持たない
            await ctx.Phase(PhaseId.Update);

            // 読み: 値のコピーを返す。相手が消えていれば EntityNotAliveException（TryRead は null）
            LocalTransform target = await ctx.Read<LocalTransform>(_target);

            // 書き: このセグメントの間だけ有効な参照
            var self = await ctx.Write<LocalTransform>(ctx.Entity);
            self.Value.Position += Direction(self.Value.Position, target.Position) * step;

            // 複数同時宣言: 1 セグメントで複数のエンティティ×型を触る
            var access = await ctx.Access().Read<Health>(_target).Write<Health>(ctx.Entity);
            access.Ref<Health>(ctx.Entity).Current += access.Get<Health>(_target).Current / 10;

            // 重い計算はプールに逃がす。結果は次フレームの取り込みで届く
            var path = await ctx.RunBackground(() => FindPath(from, to));
        }
    }
}
```

ラウンド API:

```csharp
graph.RegisterRounds(PhaseId.Update, "damage");                      // フェーズごとのラベル列（initial < damage < main < completed）

Health h = await ctx.Read<Health>(other, Round.Label("damage"));    // damage ラウンドが閉じるまで待ち、その結果を読む
Health h = await ctx.Read<Health>(other, Round.Completed);          // 次フェーズの initial と等価
var w = await ctx.Write<Health>(other, Round.Label("damage"));      // damage ラウンドへのバッファ書き込み（last-writer-wins）
ctx.Modify<Health>(other, h => h with { Current = h.Current - 10 }, Round.Label("damage")); // コミット時に関数を適用（累積に使う）
await ctx.Access().Read<A>(e1).Write<B>(e2).At(Round.Label("damage")).To(Round.Main); // 複数宣言 + ラウンド指定
```

- `Read` の既定（`initial`）は値のコピーを返し、何も待たない。unmanaged はコピーが安価なので、保持型の read は不要
- `Write` はバッファへの参照を返す。バッファはそのターンが読む値（initial + 自分の保留書き込み）で初期化され、フェーズ末にキー順でコミットされる。複数ターンからの累積は `Modify` を使う
- `Access()` は 1 セグメントに複数資源を宣言する形。入れ子の `await` で逐次獲得する必要はない（デッドロックが構造的に起きないので順序規約も不要）
- アクセスハンドルはセグメント終了で無効化され、次の await をまたいで使うと失敗する
- `Round` に string からの暗黙変換は付けない。付けると `cond ? null : round` の `null` が string と推論され `Round.Label(null)` になる（実際に踏んだ）

## Analyzer による規約強制

DivisionEngine.Generators を活かし、規約を文書ではなく診断で強制する。

- セグメント実行中に宣言外の資源へアクセスするコード（`await` を挟まずに別エンティティのコンポーネントへ触る）を検出する
- 条件 2 違反（自分の現在ラウンド以降を read しつつ同フェーズで write を続ける）を静的に検出できる範囲で検出する
- フェーズごとの書き込み可能型（[LoopSystem.md](./LoopSystem.md)）に反する write の警告
- Behavior メソッドへの専用 AsyncMethodBuilder 注入（下記）

## async ステートマシンのコスト

素朴な `async Task` はセグメントごとにステートマシンをヒープ確保する。対策:

- `PoolingAsyncValueTaskMethodBuilder`（`[AsyncMethodBuilder]` 指定、.NET 6+）によるステートマシンのプール化
- 専用ビルダーを Generator で注入し、継続を SynchronizationContext ではなくスケジューラへ直接送る（[LoopSystem.md](./LoopSystem.md) のメインスレッドの役割を参照）
- write バッファ、依存推論のエントリもプール化する

## 参考資料

- Legion Programming System（逐次意味論と依存推論）: https://legion.stanford.edu/
- Bauer et al., "Legion: Expressing Locality and Independence with Logical Regions" (SC 2012)
- OpenMP task depend: https://www.openmp.org/spec-html/5.0/openmpsu99.html
- Unity DOTS Job dependencies: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/scheduling-jobs-dependencies.html
- Unity DOTS EntityCommandBuffer.ParallelWriter（sortKey による決定的な書き込み順）: https://docs.unity3d.com/Packages/com.unity.entities@1.3/manual/systems-entity-command-buffer-playback.html
- Bevy のシステム並列実行（アクセス宣言からの自動導出）: https://docs.rs/bevy_ecs/latest/bevy_ecs/schedule/index.html
- Microsoft Orleans - Request scheduling（turn-based concurrency）: https://learn.microsoft.com/en-us/dotnet/orleans/grains/request-scheduling
- Olszewski et al., "Kendo: Efficient Deterministic Multithreading in Software" (ASPLOS 2009)
- Liu et al., "Dthreads: Efficient Deterministic Multithreading" (SOSP 2011)
- Multiversion concurrency control: https://en.wikipedia.org/wiki/Multiversion_concurrency_control
- Timothy Ford, "Overwatch Gameplay Architecture and Netcode" (GDC 2017、ECS における遅延書き込み): https://www.gdcvault.com/play/1024001/-Overwatch-Gameplay-Architecture-and
- PoolingAsyncValueTaskMethodBuilder: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.poolingasyncvaluetaskmethodbuilder-1
- Stephen Toub, "Async ValueTask Pooling in .NET 5": https://devblogs.microsoft.com/dotnet/async-valuetask-pooling-in-net-5/
