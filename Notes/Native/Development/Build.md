# ビルド
ビルドとは、ソースコードをコンパイルし、リンカでオブジェクトファイルを結合して、実行ファイルやライブラリなどの成果物を生成する処理を指す。
そのビルドの仕方を決める設定群（最適化レベル、プリプロセッサ定義、コンパイル/リンクのフラグなど）をビルド構成とよぶ。

`src/Native`はCMake + Ninja + Clangでビルドする。
ビルド構成はCMakePresets(`src/Native/CMakePresets.json`)で管理する。


## プリセット
| プリセット | 用途 |
|---|---|
| `clang-debug` | デバッグビルド(`-O0 -g`、`_DEBUG`定義) |
| `clang-release` | リリースビルド |

プリセットの違いは、CMakeの`CMAKE_BUILD_TYPE`に応じて切り替わるコンパイラフラグ、主に最適化レベルとデバッグ情報の有無である。
`--preset`はCMakePresets.jsonがあるディレクトリ(`src/Native`)から実行する。

### 最適化レベル(`-O`)
- `-O0`: 最適化なし。ソースと生成コードが対応するため、デバッガでの変数確認・ステップ実行が期待どおり動く、実行速度は遅い
- `-O1` / `-O2` / `-O3`: 数字が大きいほど積極的に最適化する、変数の削除や関数のインライン展開などでデバッグしづらくなる代わりに高速
- 他に`-Os`(サイズ優先)、`-Og`(デバッグしやすさを保った最適化)がある

### デバッグ情報(`-g`)
- 変数名・関数名・行番号などのシンボル情報を出力に埋め込む
    - これが無いとデバッガはアドレスしか分からず、ソースとの対応が取れない
- Windowsのclang(MSVCターゲット)では`-g -Xclang -gcodeview`でCodeView形式となり、リンク時に`main.pdb`が生成される
    - 詳細は[Debug](./Debug.md)を参照

### プリセットとの対応
| プリセット | 最適化 | デバッグ情報 | 用途 |
|---|---|---|---|
| `clang-debug` | `-O0` | あり(`-g`、`_DEBUG`定義) | 開発・デバッグ |
| `clang-release` | `-O3` | なし(`NDEBUG`定義) | 配布・性能計測 |

### デバッグとの関係
デバッグにはデバッグ情報（PDB）が必要なため、実質的にデバッグできるのは`clang-debug`のみ。
`clang-release`は`-g`が付かずPDBが生成されず、さらに`-O3`で変数削除・関数のインライン展開が起きるため、ブレークポイントや変数表示がまともに機能しない。
そのため`launch.json`のデバッグ構成もdebug用の1つだけにしている。

最適化されたコードをデバッグしたい場合は、`RelWithDebInfo`（`-O2 -g -DNDEBUG` = 最適化あり+デバッグ情報あり）相当のプリセットを別途用意する（現状は未定義）。
追加する場合はCMakePresets・tasks.json・launch.jsonの3箇所に対応構成を足す。


## コマンドラインからビルド
```powershell
# configure(初回、またはCMakeLists.txt変更時)
cmake --preset clang-debug

# ビルド
cmake --build --preset clang-debug
```

CMakeのビルドはconfigureとbuildの2段階に分かれる。
- configure (`cmake --preset ...`)
    - `CMakeLists.txt`を読み取り、コンパイラ・フラグ・依存関係を解決してビルドシステム（ここではNinjaの`build.ninja`）と`compile_commands.json`を`build/<preset>/`に生成する
        - 結果は`CMakeCache.txt`にキャッシュされる
- build (`cmake --build --preset ...`)
    - configureが生成したビルドシステム（Ninja）を実行し、実際にコンパイルとリンクを行う

build配下が未生成の初回は必ずconfigureが要る。
`CMakeLists.txt`やプリセットを変更した場合も再configureが必要だが、Ninja生成物には変更を検知して自動で再configureするルールが含まれるため、通常はbuildの実行だけで足りることが多い。
ソースファイルの追加・削除は`file(GLOB ... CONFIGURE_DEPENDS)`により検知される。

### 静的解析(clang-tidy)
```powershell
cmake --build --preset clang-debug --target tidy
```

チェック内容は`.clang-tidy` / `.clangd`で定義。
bugprone / performance / cppcoreguidelines / clang-analyzer系をWarningsAsErrorsとして扱う。

### テスト
```powershell
ctest --preset clang-debug
```


## VS CodeのGUIからビルド
リポジトリルートを開いた状態で使えるように`.vscode/`にタスクを用意している。
CMakeのプリセットは`src/Native`にあるため、タスクの`cwd(Current Working Directory)`を`${workspaceFolder}/src/Native`に設定している。

### tasks.json
- Ctrl + Shift + B(Run Build Task)：デフォルト指定した`CMake: Build (clang-debug)`がリストを出さず即実行される
- Ctrl + Shift + P → Tasks: Run Task：定義した全タスクが一覧表示される
    - configureやBuildはここから選ぶ
    - `CMake: Configure (clang-debug)` / `CMake: Configure (clang-release)`
    - `CMake: Build (clang-debug)` / `CMake: Build (clang-release)`

初回、またはCMakeLists.txt変更後は、対応するConfigureタスク（例:`CMake: Configure (clang-debug)`）を一度実行する。
build配下が未生成のままBuildタスクを実行すると「未configure」で失敗する。

### CMake Tools拡張(任意)
`ms-vscode.cmake-tools`を入れると、ステータスバーのボタンやF7(`CMake: Build`)でもビルドできる。
`.vscode/settings.json`で`cmake.sourceDirectory`を`${workspaceFolder}/src/Native`に指定しているため、リポジトリルートを開いても`src/Native`をソースとして認識する。

### Run and Debug(F5)
ビルドしてそのまま実行・デバッグしたい場合は、Run and Debug（Ctrl + Shift + D → F5）から起動できる。
`launch.json`の`preLaunchTask`で起動前に自動ビルド（`CMake: Build (clang-debug)`）が走る。


## 既知の問題
### clangdがモジュールのビルド成果物をロックする
clangdが`build/clang-debug`配下のモジュール成果物（例:`Printer.pcm`）をmmapで開いたままにすると、ビルドが`user-mapped section open`で失敗することがある。
VS Codeを閉じるか、clangd拡張を一時停止してからビルドすると回避できる。
pcm(BMI)やmmapの詳細は[C++20 モジュール](../Cpp/Modules.md)を参照。

### タスクが既定シェルで失敗する(`can't open input file: /d`)
`tasks.json`のタスクは`type: "process"`にして`cmake`を直接実行している。
`type: "shell"`だと、VS Codeの既定ターミナルプロファイルがmsys2のzsh(`msys2_shell.cmd`経由)などの場合、VS Codeが渡すcmd風の`/d /c ...`引数をシェルが解釈できず、`can't open input file: /d`(exit 127)で失敗する。
`process`タイプならシェルを介さないため、既定シェルの種類に依存しない。
