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
src/Native/clrprofiler/        ランタイムのイベントをゾーンにする CLR プロファイラ
  include/CorProfAbi.h         手書きの最小 ABI（型・イベントマスク・CLSID）
  include/CorProfSlots.h       vtable のスロット表（生成物）
  source/ClrProfiler.cpp       COM オブジェクトとコールバック
  tools/gen_corprof_slots.py   スロット表の生成器
  → libDivisionClrProfiler.dylib / DivisionClrProfiler.dll
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

## ランタイムの計装（CLR プロファイラ）

エンジンの計装が見せられるのはエンジンがやっていることだけで、
**ストップ・ザ・ワールド、GC、JIT はその下でランタイムの都合で走る**。
[JobSystem.md](./JobSystem.md) の「全スレッド同時のストール」（3 ms 超のジョブが
スレッド時間の 7%、最長 41 ms、8 スレッドが同時に伸びる）はまさにそれが疑われる場所で、
エンジン側にどれだけゾーンを足しても原因には届かない。

CLR はこれをプロファイリング API で外に出す。`CORECLR_PROFILER_PATH` が指すネイティブ共有ライブラリを
マネージドコードより先にロードし、**イベントを起こしたスレッド上で同期的に**コールバックを呼ぶ。
つまりゾーンをその場で開いて閉じるだけでよく、EventPipe / `EventListener` 経由と違って
配送の遅延を気にしなくて済む（Tracy の CPU ゾーンは過去時刻に置けないので、この差は決定的）。

### 出るもの

| ゾーン | 由来 | 意味 |
|---|---|---|
| `EE suspend: <理由>` | `RuntimeSuspendStarted` → `RuntimeResumeFinished` | ストップ・ザ・ワールド全体。理由は GC / GC prep / rejit / shutdown 等に分かれる |
| `EE stopped` | `RuntimeSuspendFinished` → `RuntimeResumeStarted` | 全スレッドが実際に止まっていた区間。「止めるのに手間取った」と「止まっていた時間が長い」を分ける |
| `GC gen0/1/2` | `GarbageCollectionStarted` → `Finished` | 誘発された GC には `induced` のテキストが付く |
| `JIT` | `JITCompilationStarted` → `Finished` | FunctionID を値として持つ。定常状態に出るものは段階的 JIT の再コンパイル |
| `Module load` | `ModuleLoadStarted` → `Finished` | モジュール名をテキストに付ける |

色はエンジン側（[ProfilerColors.cs](../../src/DivisionEngine/DivisionEngine/Profiling/ProfilerColors.cs)）が
青（フェーズ）・橙（Behavior）・くすんだ灰（アイドル）を使っているので、ランタイムには赤系を割り当てた。
エンジン側のスケジューリングではどうにもならない唯一の区間だから、目に付く方がよい。

### COR_PRF_HIGH_BASIC_GC を使う理由

GC のコールバックは `COR_PRF_MONITOR_GC`（低位マスク）でも取れるが、
**このフラグはプロセス全体の並行 GC を止める**。測っている対象が変わってしまうので使えない。
高位マスクの `COR_PRF_HIGH_BASIC_GC`（`SetEventMask2` の第 2 引数）は
`GarbageCollectionStarted` / `Finished` だけを有効にし、並行 GC はそのまま残る。

4 秒間の確保ループで、観測した GC のうち並行だったものの数:

| マスク | 並行 GC / 観測した GC |
|---|---|
| `COR_PRF_MONITOR_GC`（低位 0x80） | **0 / 3,506** |
| `COR_PRF_HIGH_BASIC_GC`（高位 0x10） | 393 / 3,517 |
| プロファイラなし | 398 / 3,568 |

高位マスクならプロファイラを付けていない状態とほぼ同じ挙動になる。
代わりに落ちるのはオブジェクト単位の GC コールバック（`SurvivingReferences` 等）で、これは使っていない。

### ABI の扱い

プロファイラの実体は**COM ランタイムなしの COM**で、
`ICorProfilerCallback11` と寸分違わぬ vtable を持つオブジェクトを渡す。
公式の宣言（`corprof.h`）は MIDL の出力で `windows.h` と `ole2.h` を引くため、
Windows 以外でビルドするにはランタイム自身の PAL 置き換えごと vendoring することになる。

必要なのはもっと小さいので、2 つに分けた。

- **型・イベントマスク・CLSID は手書き**（`CorProfAbi.h`）。`TracyNative.cs` と同じ扱いで、インターフェースではなく ABI 契約
- **スロット番号だけは生成する**（`CorProfSlots.h`）。ここは推測してはいけない部分で、間違えてもビルドもロードも失敗せず、ランタイムが別の関数を呼ぶだけになる

生成器は `tools/gen_corprof_slots.py` で、dotnet/runtime の `v10.0.0` から `corprof.h` を取り、
継承の連鎖を平坦化してスロット番号を出す（`ICorProfilerCallback11` は 98 スロット、`ICorProfilerInfo12` は 108）。
`--check` で既存の生成物と突き合わせられる。実装しないスロットは共通のスタブで埋めてあり、
引数を読まない以上シグネチャを合わせる必要もない（ただし可変長引数にはしない。
Apple arm64 では可変長の呼び出し規約が違い、レジスタ渡しの引数を取り落とす）。

### 交差するゾーン

Tracy のゾーンはスレッドごとのスタックなので、開いた順の逆でしか閉じられない。
ランタイムのイベントは必ずしもそうならない。
**バックグラウンド gen2 GC はサスペンドの内側で始まり、世界が再開した後も続く**ので、
`GC gen2` と `EE stopped` は同一スレッド上で本当に交差する。

最初の実装はこれを踏んで、キャプチャが丸ごと拒否された
（`Instrumentation failure: Invalid order of zone begin and end events`）。
1 組でも壊れていればトレース全体が無効になるので、運に任せられる話ではない。

今は、交差するゾーンを**いったん閉じて即座に開き直す**。
BGC は「サスペンド内の区間」「再開処理中の区間」「その後の区間」に分かれて隣接して並び、
継続分には `continued` のテキストが付く。時間の被覆は正しいまま、スタック規律も守られる。
8 秒のキャプチャで分割が起きたのは 10 回だった。

同じ理由で、GC ゾーンはスタックとして持っている。
BGC の進行中にエフェメラルな GC が同じスレッドから報告され、その内側に入れ子になるため。

### ディープモード: マネージドの呼び出しを全部ゾーンにする

`DIVISION_CLR_EVENTS=deep` で、マネージドメソッドの呼び出し 1 回ごとにゾーンが出る
（Unity の Deep Profiling に相当）。名前は `Type.Method` まで解決される。

遅さは副作用ではなく仕様で、`COR_PRF_MONITOR_ENTERLEAVE` はプロセス全体のインライン化を止め、
全ての呼び出しと復帰にスタブを挟む。5.2 万エンティティの Transform 階層で測ると
**1,117 fps が 2 fps**（約 550 倍）になり、4.15 秒のキャプチャで 1,716 万ゾーンが出た。
常用するものではない。

#### 実験で決まった 3 つのこと

API には同じことをする方法が複数あり、この環境（macOS arm64 / .NET 10）で動くのは 1 つだけだった。

| 方法 | 結果 |
|---|---|
| `SetEnterLeaveFunctionHooks3` | 登録は成功しフックも呼ばれるが、**ヒープを壊してプロセスが死ぬ**（無関係な場所で AccessViolation） |
| `SetEnterLeaveFunctionHooks3WithInfo` | `0x80131374` で拒否される |
| **`SetEnterLeaveFunctionHooks2`** | 動く。150 万回のフック呼び出しで正常終了 |

`FunctionIDMapper` にも順序の制約がある。**`SetEventMask2` の後に登録するとマッパーの初回呼び出し直後に落ちる**
（返す値には依存せず、恒等写像でも落ちる）。**マスクより前に登録すれば動く**。
ENTERLEAVE を有効にした後でマッパーを差し込むのが不整合なのだと思われる。

現在の実装はマッパーを使っていない。名前は **`JITCompilationFinished` で解決して FunctionID をキーに表に入れ**、
フックはそれを引く。メタデータを読んでよい場所はここだと文書化されており、フックの中から読むのは論外。
表はロックフリーのオープンアドレス法（書き手は JIT だけ、読み手は全スレッドの全呼び出し）。
マッパーを使えばクライアント ID に call site ポインタを入れてフックの引きを無くせる（呼び出しあたり 2 回のハッシュ引きが消える）が、
そのためには名前解決を `JITCompilationStarted` に移し、
「マッパーはその後に呼ばれる」という文書化されていない順序に依存することになる。
ディープモードで 1 割速くなる代わりに名前が黙って劣化しうるので、今は採っていない。

この方式には副次的な性質がある。**enter / leave のスタブを出すのは JIT なので、
事前コンパイル済み（ReadyToRun）のコードにはフックが無く、フックにも届かない**。
つまりディープモードに映るのはエンジンとユーザーコードで、BCL の内部は映らない。
実用上はその方が読みやすい。

#### ゾーンを開くメソッドを除外する

最初の実装はエンジン自身の計装と同時に使えなかった。Tracy のゾーンはスレッドごとのスタックなので、
`Profiler.Zone()` が開いたマネージド側のゾーンと、その `Profiler.Zone` 自身の呼び出しを囲む
ディープゾーンは**必ず交差する**。`Zone()` から戻る時点で、その中で開いたゾーンがまだ開いているためで、
1 組でも壊れればキャプチャ全体が無効になる。

規則は単純で、**ゾーンスタックへの正味の影響がゼロでないメソッドはフックしてはいけない**。
ディープゾーンは入った時に開いて戻る時に閉じるので、既に開いているものの内側にしか入れ子にできない。
エンジンでは `Profiler.Zone` / `Profiler.ZoneNamed` と `ProfilerZone.Dispose` がそれに当たる
（`using var zone = ...` の開きと閉じ）。逆に `Zone.Text` や `Plot` はスタックを触らないので問題ない。

除外は `DIVISION_CLR_DEEP_EXCLUDE`（`Type.Method` の前方一致をカンマ区切り）で指定し、
既定は `DivisionEngine.Profiler.,DivisionEngine.ProfilerZone.`。
除外されたメソッドは名前の表に番兵として入り、enter / leave / tailcall / 巻き戻しの全てで同じ判定が使われる。

これで**エンジンのゾーンとディープゾーンが 1 つのキャプチャに同居する**。
5.2 万エンティティ、5.1 秒、1,876 万ゾーンのキャプチャで両方が正しく入れ子になっていることを確認した:

| ゾーン | 出どころ | 件数 |
|---|---|---|
| `TransformPropagation` | `TaskNode.cs`（エンジンのジョブ） | 140 |
| `DivisionEngine.TaskNode.Execute` | ディープ | 140 |
| `TransformPropagation` | `PhaseGroup.cs`（エンジンのフェーズ） | 7 |
| `DivisionEngine.Engine.RunFrame` | ディープ | 7 |

エンジンのゾーンが骨格を、ディープゾーンがその中身を見せる形になる。

なお、計装無しのビルド（`-p:DivisionProfiling=true` 無し）に対してディープモードを使うこともできる。
そのビルドは Tracy クライアントを起動しないので、`DIVISION_CLR_START_TRACY=1` でプロファイラ側に起動させる。
マネージド側の `Profiler.Startup()` は既に起動済みかを見てから起動するので、二重起動にはならない。

#### 計装されているかは 1 つの表が決める

「このメソッドにフックが付いているか」の答えは、enter・leave・tailcall・例外の巻き戻しの
4 経路で完全に一致していなければならない。片方だけが push / pop すればそこで壊れる。

そのため名前の表には**名前が読めなかったメソッドも入れる**（総称名で記録する）。
表にあることが「JIT された = フックがある」を意味し、無ければ何もしない。
例外の巻き戻しは `ExceptionUnwindFunctionLeave` ではなく `ExceptionUnwindFunctionEnter` で処理している。
FunctionID を受け取れるのはこちらだけで、フックの無いフレーム（事前コンパイル済み）も巻き戻されるため、
無条件に pop すると他人のゾーンを閉じてしまう。
`DynamicMethod` 由来（IL スタブ、ラムダ）はメタデータが無いので
`DynamicMethodJITCompilationFinished` で総称名を入れている。

#### 最初に出た絵

5.2 万エンティティ、4.15 秒、上位 6 件:

| ゾーン | 件数 | 合計スレッド時間 |
|---|---|---|
| `JobSafety.AssertRead` | 3,195,818 | 2.76 s |
| `World.GetLocation` | 2,568,043 | 11.13 s |
| `World.IsAlive` | 2,394,801 | 2.20 s |
| `ComponentTypeRegistry.GetInfo` | 1,205,593 | 1.15 s |
| `Chunk.GetPointer` | 1,198,073 | 1.12 s |
| `World.ThrowIfManaged` | 1,197,336 | 1.15 s |

`World.GetLocation` が突出しているのは
[SceneManagement.md](./SceneManagement.md) の「階層経路の 83% はランダムアクセス」と同じものを別の角度から見たもの。
ジェネリックは実体化ごとに別の FunctionID になるので `ComponentType\`1.get_Info` が複数行に分かれる。

## コールスタックサンプリング

**macOS では動いていない。** Tracy 0.14.1 は Apple 向けのサンプラ（mach + フレームポインタ走査、1000 Hz）を
持っていて自動で起動もするが、`TracyMach.cpp` の `SysTraceStart` の冒頭に

```cpp
if( geteuid() != 0 ) return false;
```

がある。ユーザーモードのサンプリングに権限は要らないが、
他プラットフォームの挙動と揃えるためにあえて root を要求する、とコメントに書いてある。
実際にキャプチャを調べると**サンプル数 0**（`callstack samples: 0`）。

root で走らせれば取れるはずだが未検証。

```sh
sudo CORECLR_ENABLE_PROFILING=... ./DivisionEngine.Player
```

ただし取れたとしても**マネージドのフレームは記号化されない**。
Tracy のクライアントは `dladdr` で名前を引くので、JIT されたコードには何も付かない。
`DOTNET_PerfMapEnabled` の perf map も Tracy は読まない。
マネージドのスタックが見たいなら、サンプリングではなく上のディープモードの方が答えになる。

## 使い方

### 1. submodule を取得してネイティブをビルドする

```sh
git submodule update --init --depth 1       # clone 時に --recurse-submodules していれば不要
cmake --preset macos-clang-release          # 初回のみ（Windows は clang-release）
cmake --build --preset macos-clang-release --target tracy_client
cmake --build --preset macos-clang-release --target clr_profiler   # ランタイムの計装も使うなら
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

### 4. ランタイムのイベントも見る（任意）

CLR プロファイラはマネージドコードから一切触れないので、環境変数だけで付け外しする。

```sh
cd src/DivisionEngine/DivisionEngine.Player/bin/Release/net10.0
CORECLR_ENABLE_PROFILING=1 \
CORECLR_PROFILER='{9F2C6D1A-4E7B-4F3D-B05A-1C7D8E942A61}' \
CORECLR_PROFILER_PATH="$PWD/libDivisionClrProfiler.dylib" \
./DivisionEngine.Player
```

`CORECLR_PROFILER_PATH` は**マネージドの出力ディレクトリに置かれたコピー**を指さなければならない
（`-p:DivisionProfiling=true` のビルドがそこへコピーする）。
プロファイラは隣の `libDivisionTracy` を `@loader_path` で引くので、
ビルドツリーの方を指すと**プロセス内に Tracy クライアントが 2 つ**できて、接続もデータも割れる。

| 環境変数 | 既定 | 意味 |
|---|---|---|
| `DIVISION_CLR_EVENTS` | `suspend,gc,jit` | `suspend` / `gc` / `jit` / `loader` / `deep` / `all` / `none` をカンマ区切りで（`deep` は `all` に含まれない） |
| `DIVISION_CLR_VERBOSE` | なし | `1` で、付いたことと設定したマスク、ゾーンが分割された回数を stderr に出す |
| `DIVISION_CLR_DEEP_DEPTH` | 64 | ディープモードでゾーンを開く入れ子の上限 |
| `DIVISION_CLR_DEEP_EXCLUDE` | `DivisionEngine.Profiler.,DivisionEngine.ProfilerZone.` | ディープモードでフックしない `Type.Method` の前方一致（カンマ区切り、空で除外なし） |
| `DIVISION_CLR_START_TRACY` | なし | `1` で、エンジンを待たずにプロファイラ側が Tracy クライアントを起動する（ディープモードに必要） |

CLSID は `CorProfAbi.h` の `kClsidDivisionClrProfiler` と同じもの。

エンジンが Tracy を起動していなければ（`-p:DivisionProfiling=true` なしのビルド）、
プロファイラは付くが何も出さない。Tracy のライフタイムはエンジンが握っている（`TRACY_MANUAL_LIFETIME`）ので、
こちらからは起動しない。

### 検証の仕方

ランタイムのゾーンは入れ子の規律を破りやすく、破ると**トレース全体が無効になる**。
変更したら `tracy-capture` でキャプチャを取り、
`Instrumentation failure: Invalid order of zone begin and end events` が出ないことを確かめる。
中身は `tracy-csvexport` で見る。

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

### CLR プロファイラを付けたキャプチャ

GC を意図的に回す確保ループ（4 KB × 400 / フレーム、ワークステーション GC）を 8 秒。
`DIVISION_CLR_EVENTS=all`。

| ゾーン | 件数 | 合計 | 最大 |
|---|---|---|---|
| EE stopped | 7,622 | 3.56 s | 3.87 ms |
| EE suspend: GC | 6,803 | 4.03 s | 4.00 ms |
| GC gen0 | 4,521 | 288 ms | 0.34 ms |
| GC gen2 | 1,164 | 1.52 s | 3.44 ms |
| GC gen1 | 1,121 | 1.44 s | 3.76 ms |
| EE suspend: GC prep | 819 | 95 ms | 1.95 ms |

`EE suspend` の件数（6,803 + 819）は `EE stopped` の件数と一致する。
gen2 の合計が gen0 の 5 倍あるのはバックグラウンド GC が並行に走っている時間で、
止まっていた時間そのものではない（そちらは `EE stopped` が持っている）。

JIT とモジュールロードのゾーンはこの表に出ていない。
起動の 200 ms ほどで終わってしまい、`TRACY_ON_DEMAND` では UI が繋がるまで何も記録されないため。

## 最初に見つかった問題: `LocalTransform.ToMatrix`

計装を入れて最初に出た当たり。`ToMatrix` は

```csharp
Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position)
```

と書かれていて、**4x4 の積を 2 回**回していた。TRS は回転行列の各行をスケールで割り増しし、
平行移動を最終行に置くだけで組めるので、約 230 flops のところを約 30 flops で済む。

まず疑われる「SIMD 化されていないのでは」は**外れ**だった。`Matrix4x4` の `*` は BCL 側でベクトル化されており、
同じ計算を明示スカラーで書いたものと比べて 2.5 倍速い（3.93 ns 対 9.95 ns / 100k 回）。
実行環境も `AdvSimd.IsSupported` / `Vector128.IsHardwareAccelerated` がいずれも true。
**問題は SIMD でないことではなく、SIMD で不要な仕事をしていたこと。**

| 100,000 回 | 1 回あたり |
|---|---|
| 旧 `ToMatrix()`（行列 3 つを合成） | 14.66 ns |
| 手書き TRS（普通のスカラー演算） | 8.43 ns |
| 手書き TRS（明示 `Vector128`） | 7.20 ns |

明示 SIMD 化（-14%）より**演算量を減らすこと（-43%）の方が 3 倍効く**。すでにベクトル化されているので、
SIMD 化から取れる余地はほとんど残っていない。採ったのは読みやすいスカラー版。

`DivisionEngine.Benchmarks` の `TransformBenchmarks`（10 万エンティティ、`-j Short`、Apple M1）:

| シナリオ | ルート | ワーカー | 変更前 | 変更後 | 変化 |
|---|---|---|---|---|---|
| Propagate_Flat_100k | 1000 | 0 | 1,409.3 µs | 924.1 µs | **-34%** |
| Propagate_Flat_100k | 1000 | 7 | 356.8 µs | 254.5 µs | **-29%** |
| Propagate_Hierarchy_100k | 1000 | 0 | 8,183.2 µs | 7,413.0 µs | -9.4% |
| Propagate_Hierarchy_100k | 1000 | 7 | 2,298.0 µs | 2,297.7 µs | ±0% |

効き方の差がそのまま構造を表している。**フラット経路のコストはほぼ全部が `ToMatrix`**
（変更前 1,409 µs = 14.1 ns/entity は、単体で測った 14.66 ns とほぼ一致する）。
一方の階層経路は 81.8 ns/entity のうち計算は 17 ns しかなく、残り 83% は
`PropagateSubtree` がエンティティごとに行う 6〜7 回のランダムアクセスなので、効果は一桁に留まる。
7 ワーカーの階層が動かないのは、そこが計算ではなく並列化の側で頭打ちになっているため
（[JobSystem.md](./JobSystem.md) の「細粒度タスクでは並列の方が遅い」と、下記の伝播の並列幅の問題）。

新旧はビット一致する。`Tests/Transforms/TransformMathTests.cs` が
恒等・鏡映・ゼロスケール・各軸回転・乱数 200 件を旧実装と突き合わせて固定している
（比較に許容差を置いてあるのは、命令セットが変われば丸め順が変わりうるため。導出を間違えれば桁違いに外れる）。

## 未解決・今後

- **接続時のコストを測る**。特に Behavior レーン（1 フレーム 5,000 セグメント）でどれだけ観測が結果を歪めるか
- `[SuppressGCTransition]` の可否。ゾーンあたり数 ns 削れるが、スレッド最初の呼び出しは遅延初期化でブロックしうる。抑制したままブロックすると GC を止めるので、測ってから判断する
- **発行ロックの可視化**。Tracy にはロック可視化の C API（`___tracy_announce_lockable_ctx` ほか）があり、
  `JobGraph` の発行ロックはまさにその対象。並列時の劣化の原因候補なので次の計装対象
- **JIT ゾーンにメソッド名を載せる**。ディープモードのために `IMetaDataImport` の ABI と
  名前解決はもう入っているので、あとは繋ぐだけになった。
  ディープモードが有効なときだけ名前が取れる状態なので、常時に広げるかは未定
- **サンプリングを root で試す**。macOS の Tracy サンプラは root を要求する（上記）。
  取れたとしてマネージドのフレームがどこまで読めるかは未確認
- **CLR プロファイラのオーバーヘッドを測る**。サスペンドと GC のコールバックだけなら
  ゾーン 1 つあたり数十 ns のはずだが、未測定
- Behavior セグメントのゾーン化と、ターン単位の見せ方（fiber を使うかどうか）
- ワーカーごとのキュー深さ・1 フレームのセグメント数を `Plot` に出す
- GPU ゾーンはレンダラができてから

## 参考資料

- Tracy Profiler: https://github.com/wolfpld/tracy
- Tracy のマニュアル（PDF はリリースに同梱）: https://github.com/wolfpld/tracy/releases
- C API: `src/Native/packages/tracy/public/tracy/TracyC.h`
- Tracy-CSharp（採用しなかった既存バインディング）: https://github.com/clibequilibrium/Tracy-CSharp
