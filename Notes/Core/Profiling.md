# プロファイリング

並列実行の様子を時間軸で見るための計装と、その土台である [Tracy Profiler](https://github.com/wolfpld/tracy) の組み込みに関するノート。
スケジューラの設計そのものは [JobSystem.md](./JobSystem.md)、フェーズ構成は [LoopSystem.md](./LoopSystem.md) を参照。

## 何を見たいのか

このエンジンの中心は依存グラフスケジューラなので、知りたいのは合計時間ではなく**時間軸上の配置**である。

- どのワーカーがいつ何を走らせ、いつ寝ているか
- フェーズ境界（ラウンドのコミット、構造変更の適用）でどれだけ待たされているか
- Behavior のセグメントが実際にどれくらい細かく、どれだけ発行のオーバーヘッドを払っているか

具体的な動機は [JobSystem.md](./JobSystem.md) の「細粒度タスクでは並列の方が遅い」で、
**Behavior レーンは 23 ワーカーの方が 0 ワーカーより遅い**（1.34 ms → 2.9 ms）。
原因の候補は発行ロックの競合とセマフォ起床のカーネル遷移だが、どちらも
「待ち時間がどのスレッドのどこに出ているか」を見れば切り分けられる。
ベンチマークの数字だけでは、この 2 つは同じ見え方をする。

## なぜ Tracy か

| 候補 | 判断 |
|---|---|
| **Tracy** | 採用。ns 分解能、スレッド × ゾーンのタイムライン、フレーム単位の統計、ロック可視化、待ちと仕事を色で分けられる。並列スケジューラを見るための道具としてはこれが本命 |
| Chrome trace (Perfetto) JSON を自前出力 | 依存ゼロで半日だが、ライブ表示がなく、1 フレーム数千セグメントではファイルが肥大し、アイドルの可視化が弱い。一次切り分けには足りるが投資先としては弱い |
| dotnet-trace / EventSource | 標準で手に入るがフレーム指向の並列可視化には向かない |

### 既存の C# バインディングを使わなかった理由

NuGet に [Tracy-CSharp](https://github.com/clibequilibrium/Tracy-CSharp) があり最初はこれで素振りする方針だったが、パッケージを開けて 2 つの問題が出た。

1. **osx-arm64 のネイティブバイナリが入っていない**（linux-x64 と win-x64 のみ）。開発機が macOS arm64 なので、どのみちネイティブは自前ビルドになる
2. **どの defines でビルドされたバイナリか分からない**。`TRACY_ON_DEMAND` の有無で `TracyCZoneCtx` のサイズが 8 → 16 バイト変わるのに、パッケージからはそれが読み取れない。実際 managed 側を覗くと 8 バイト（= on-demand なし）で、別パッケージとして Tracy-CSharp-On-Demand が存在することがこの分岐を裏づけている

レイアウトがズレても型エラーにもロードエラーにもならず、**黙ってキャプチャが壊れる**。
必要な関数は 16 個程度なので、自前で束ねた方が安全で、defines も握れる。

## 構成

```
src/Native/packages/tracy/     upstream の submodule（v0.14.1 に固定）
  → libDivisionTracy.dylib / DivisionTracy.dll
src/DivisionEngine/DivisionEngine/Profiling/
  TracyNative.cs               LibraryImport による生のエントリポイント
  Profiler.cs                  エンジンが使う API（ゾーン・フレーム・プロット）
  ProfilerZone.cs              ゾーンハンドルと、名前・テキストの付与
  ProfilerColors.cs            待ちと仕事を見分けるための色
```

### 取り込み方

**submodule**（`src/Native/packages/tracy`、v0.14.1 のコミットに固定）。
`.gitmodules` に `shallow = true` を入れてあるので、`git submodule update --init` は深さ 1 で取る。

ビルドは **upstream 自身の CMakeLists をオプション経由で駆動する**。
必要な defines はほぼ全て upstream が CMake オプションとして公開しているので、
こちら側のビルドロジックは「upstream が公開していない分」だけで済む（`src/Native/CMakeLists.txt`）。

vendoring（`public/` だけをリポジトリに取り込む）と比べた場合、実体は 2.0 MB から 37 MB に増える。
代わりに upstream に一切手を入れない形が構造的に保証され、更新はタグのチェックアウトだけになる。

### ネイティブの defines

`src/Native/CMakeLists.txt` で固定している。これは調整用のつまみではなく **C# 側との ABI 契約**なので、
`FORCE` 付きで上書きしている。

| define | 理由 |
|---|---|
| `TRACY_ENABLE` | これが無いと全エントリポイントが空になる（upstream の既定は OFF） |
| `TRACY_ON_DEMAND` | UI が接続するまで何も記録しない。プロファイリング有効ビルドを普段使いできる |
| `TRACY_DELAYED_INIT` | 動的ロードされるライブラリに必要（CLR は自身の静的初期化の後にロードする） |
| `TRACY_MANUAL_LIFETIME` | 静的初期化順に頼らず、エンジンが明示的に起動する |

`TRACY_DELAYED_INIT` だけは注意が要る。**ソースは今も見ているが、v0.14 で CMake オプションではなくなった**ので、
`add_subdirectory` の後に手で足している。`TracyProfiler.cpp` は
`TRACY_MANUAL_LIFETIME` と両方が定義されているときだけ `___tracy_startup_profiler` 系を生成するため、
落とすと C# 側が起動時にエントリポイント不在で失敗する（`Profiler.Startup` が理由付きで報告する）。

`TRACY_FIBERS` は既定で OFF。ターンを 1 本の線として見せられるのは魅力的だが、
1 フレームに数千のターンが生まれるので fiber 名が爆発する。まずはゾーンに TurnId をテキストで載せる方に倒す。

### コンパイル時に消える

計装は `DIVISION_PROFILING` 定数が定義されたときだけ有効になる（`-p:DivisionProfiling=true`）。
既定では `Profiler` のメソッドは本体が空で、`ProfilerZone` は空の構造体になるため JIT が丸ごと消す。
**ベンチマークの数字を無計装のエンジンと比較可能に保つ**のが目的。

実行時の安全側の作りも入れてある。ネイティブライブラリが無い場合は `Profiler.Startup()` が理由付きで false を返し、
以降の呼び出しは何もしない。ネイティブのビルドは別工程なので、
プロファイラが無いことでエンジンが落ちてはいけない。

## 計装点

スケジューラ側に既に名前と単一の実行入口があったので、刺す場所は 5 か所で済んだ。

| 場所 | 出るもの |
|---|---|
| `TaskNode.Execute` | ジョブ 1 本 = ゾーン 1 つ。名前は `TaskNode.Name`。チャンクジョブはバッチごとに 1 ゾーンなので、ワーカーへの割れ方がそのまま見える |
| `PhaseGroup.Execute` | フェーズごとの不連続フレーム + メインスレッド上のゾーン。12 フェーズが帯として並ぶ |
| `JobScheduler.WorkerLoop` | セマフォ待ちを `Worker idle` ゾーンに。スレッド名は最初の起床時に付ける（プールはプロファイラ起動前に作られうるため） |
| `JobScheduler.Wait` / `WaitAll` | メインスレッドの待ちを `Main idle` ゾーンに |
| `Engine.RunFrame` | フレーム末の `FrameMark` |

アイドルを**色を変えて**別ゾーンにしているのが要点で、
合計時間では「仕事をしているワーカー」と「寝ているワーカー」が同じに見えてしまう。

### 実行時に決まる名前の扱い

ジョブ名もフェーズ名も実行時にしか分からないので、素直に書くと「1 つの call site をみんなで共有し、
ゾーンごとに名前を後付けする」形になる。表示は正しいが、**Tracy の統計は call site 単位で集計する**ため、
「どの System が重いか」が call site 名 1 行に潰れる。実際、最初の計装ではジョブが全て `Job` の 1 行になった。

そこで `Profiler.ZoneNamed(name, shared)` が名前ごとに source location を interning する。
call site の file / function / line は共有元から引き継ぐので、UI からコードに飛べる性質は保たれる。
コストはゾーンあたり辞書引き 1 回で、UTF-8 化より安い。

鍵は **(call site, 名前) の組**にしてある。名前だけで引くと、同名のジョブとフェーズ
（`TransformPropagation` はその両方）が先に interning された方へ潰れる。これも実際に踏んだ。

interning 数には上限（1024）を設けてある。ジョブ名は System / Behavior の型名なので実際は小さな固定集合だが、
`JobGraph.Start` は任意の文字列を受け取れるので、エンティティごとに名前を作られると青天井になりうる。
上限を超えた分は共有 call site + 名前後付けにフォールバックする（タイムライン上の表示は正しいままになる）。

## 使い方

### 1. submodule を取得してネイティブをビルドする

```sh
git submodule update --init --depth 1       # clone 時に --recurse-submodules していれば不要
cmake --preset macos-clang-release          # 初回のみ（Windows は clang-release）
cmake --build --preset macos-clang-release --target tracy_client
```

submodule が未取得のまま configure すると、CMake が取得コマンド付きで止まる。

成果物は `src/Native/build/<preset>/bin/` に出る。
`-p:DivisionProfiling=true` のビルドがここから managed の出力ディレクトリへコピーする
（別の場所を使うなら `-p:DivisionTracyDir=...`）。

### 2. プロファイリング有効でビルド・実行する

```sh
dotnet run --project DivisionEngine.Player -c Release -p:DivisionProfiling=true
```

### 3. UI を繋ぐ

Tracy の UI（サーバ）は**クライアントとバージョンを合わせる必要がある**。プロトコルが変わると接続を拒否される。
v0.14.1 の [リリース](https://github.com/wolfpld/tracy/releases/tag/v0.14.1) に
macOS / Windows / Linux のビルド済みバイナリがあるのでそれを使う。

（Homebrew の `tracy` は執筆時点で 0.13.1 で、こちらが固定している 0.14.1 とは繋がらない。）

クライアントは起動時点で TCP 8086 を listen し、UI が接続するまで何も記録しない。

## 測ったこと

### 未接続時のオーバーヘッド

無視できる水準だった。`DIVISION_PROFILING` 有効・プロファイラ起動済み・UI 未接続で、
空のフレームループを 8 秒回して 138 万フレーム。`TRACY_ON_DEMAND` が効いており、
接続していない限り計装は実質タダで置いておける。

### 実際のキャプチャ

2,000 ルート × 子 25（52,000 エンティティ）の Transform 階層を、ワーカー 7（Apple M1、8 コア）で回した 8 秒。
リリースに同梱の `tracy-capture` で取得し `tracy-csvexport` で集計したもの。

| ゾーン | call site | 件数 | 合計 |
|---|---|---|---|
| TransformPropagation | `TaskNode.cs`（ジョブ） | 86,006 | 42.9 s |
| Worker idle | `JobScheduler.cs` | 37,630 | 19.9 s |
| TransformPropagation | `PhaseGroup.cs`（フェーズ） | 5,375 | 8.0 s |
| Main idle | `JobScheduler.cs` | 5,376 | 2.0 s |
| 他 11 フェーズ | `PhaseGroup.cs` | 各 4,480〜5,376 | 各 20 ms 未満 |

読み取れること: このシーンではフレームがまるごと TransformPropagation であり
（フェーズの合計 8.0 s に対しキャプチャ全体が 8.16 s）、他 11 フェーズを足しても 80 ms に届かない。
一方でワーカーのアイドルが 19.9 s あり、8 スレッド × 8.16 s ≒ 65 s の 3 割が遊んでいる。
ルート 2,000 個に対してバッチが 1 フレーム 16 個しかないので、割り切れなかった分がそのまま待ちになっている。

### 接続時のオーバーヘッド

未測定。ゾーン 1 つあたり P/Invoke 2 回（begin / end）が乗る。

## 未解決・今後

- **接続時のコストを測る**。特に Behavior レーン（1 フレーム 5,000 セグメント）でどれだけ観測が結果を歪めるか
- `[SuppressGCTransition]` の可否。ゾーンあたり数 ns 削れるが、スレッド最初の呼び出しは遅延初期化でブロックしうる。抑制したままブロックすると GC を止めるので、測ってから判断する
- **発行ロックの可視化**。Tracy にはロック可視化の C API（`___tracy_announce_lockable_ctx` ほか）があり、
  `JobGraph` の発行ロックはまさにその対象。並列時の劣化の原因候補なので次の計装対象
- Behavior セグメントのゾーン化と、ターン単位の見せ方（fiber を使うかどうか）
- ワーカーごとのキュー深さ・1 フレームのセグメント数を `Plot` に出す
- GPU ゾーンはレンダラができてから

## 参考資料

- Tracy Profiler: https://github.com/wolfpld/tracy
- Tracy のマニュアル（PDF はリリースに同梱）: https://github.com/wolfpld/tracy/releases
- C API: `src/Native/packages/tracy/public/tracy/TracyC.h`
- Tracy-CSharp（採用しなかった既存バインディング）: https://github.com/clibequilibrium/Tracy-CSharp
