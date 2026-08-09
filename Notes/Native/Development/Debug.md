# デバッグ

## VS CodeのRun and Debug
ルート`.vscode/launch.json`にデバッグ構成を用意している。
- Ctrl+Shift+DでRun and Debugビューを開き、構成`Debug main (clang-debug)`を選んで起動
- 起動前に`preLaunchTask`で自動ビルドが走る(`CMake: Build (clang-debug)`)
- `console`は`internalConsole`(VS Codeのデバッグコンソール)にしている
    - `integratedTerminal`にすると、既定ターミナルがmsys2のzshなどの場合に、cppvsdbgが`VsDebugConsole.exe`をシェル経由で起動しようとして`parse error near '}'`で失敗するため


### デバッガの種類
デバッガには`ms-vscode.cpptools`が提供するcppvsdbg(Visual Studioのデバッガエンジン)を使う。
WindowsのClangが生成するバイナリおよびデバッグ情報がMSVC互換であり、これに対応するデバッガが必要となるためである。
- ターゲット(target)：コンパイラがどの環境向けの実行ファイルを生成するかの指定
    - 同一のClangであっても、ターゲットに応じて命令セット・ABI・デバッグ情報の形式が変化する
        - ターゲットは`<CPU>-<ベンダ>-<OS>-<環境>`というターゲットトリプルで表現される
    - `x86_64-pc-windows-msvc`:本プロジェクトのターゲットトリプルであり、内訳は次のとおり
        - `x86_64`：CPUアーキテクチャ
        - `pc`：特定のベンダを指定しない
        - `windows`：OS
        - `msvc`：MSVC(Microsoft Visual C++)のABIに準拠することを指定
- ABI (Application Binary Interface)：コンパイル済みのバイナリ同士が実行時に整合するための、機械語レベルの規約のこと
    - 関数の呼び出し規約（引数をレジスタとスタックのいずれでどのように渡すか）、構造体のメモリ配置、名前修飾（name mangling）、例外処理やデバッグ情報の形式など
    - ソースコードレベルの取り決めであるAPIに対し、ABIはビルド後のバイナリレベルの取り決めに相当する

WindowsのClangはMSVC ABIに従ってバイナリとデバッグ情報を出力するため、同じくMSVC系のデバッガであるcppvsdbgが対応する（GDB/LLDBを用いる`cppdbg`ではない）。


### デバッグ情報(PDB / CodeView)
`clang-debug`ビルドでは、コンパイル時に`-g -Xclang -gcodeview`が付与され、CodeView形式のデバッグ情報が生成される。
リンク時に`main.pdb`が出力され、cppvsdbgはこれを参照してブレークポイント・変数表示・ステップ実行を行う。
- PDB(Program Database)：デバッグ情報を格納するファイル(`.pdb`)
    - 実行ファイル(`.exe`)本体には機械語コードのみが含まれ、シンボル情報（変数名・関数名・型・ソース行との対応など）はもたない
        - それらを外部ファイルとして保持するのがPDBである
    - デバッガは実行中のバイナリのアドレスとPDBの情報を突き合わせ、現在のソース行や変数の値を提示する
        - PDBが無い場合はアドレスしか分からず、ソースレベルのデバッグができない
- CodeView：デバッグ情報のフォーマットの名称

`clang`が`-gcodeview`によりCodeView形式のデバッグ情報を各オブジェクトに埋め込み、リンカ(lld-link)がそれらを集約して`main.pdb`にまとめる、という流れになる。
GCC/LLDB系ではこれに相当する形式がDWARFであり、PDBのような外部ファイルではなくバイナリ内(`.debug_*`セクション)に格納される点が異なる。

配布用のReleaseビルド(`clang-release`)ではPDBを生成しない。
デバッグ情報が不要であることに加え、ファイルサイズの削減や内部情報の秘匿といった理由による。


## D3D12デバッグレイヤー
> 現状、`main.cpp`には`EnableDebugLayer()`が定義されているが、`main()`から呼ばれていない。
> `CreateDXGIFactory2`もフラグ`0`で呼んでおり、`DXGI_CREATE_FACTORY_DEBUG`は未使用。

D3D12デバッグレイヤーは、APIの誤用(リソースステートの不整合、バリア漏れなど)を実行時に検出してデバッグ出力へ警告を出す機能。

- 有効化するには`ID3D12Debug::EnableDebugLayer()`をデバイス生成より前に呼ぶ
- Windowsの「グラフィックスツール」オプション機能の導入が必要
- 出力はデバッグ出力(OutputDebugString)に流れる。VS Codeのcppvsdbg配下で実行していれば、そのままデバッグコンソールに表示される
    - デバッガを介さず実行した場合や専用ツールで見たい場合は、`CobaltFusion.DebugViewPP`(DebugView++)などのDebugView系ツールを使う(プロセスフィルタが強い)


## 参考
- [Cross-compilation using Clang](https://clang.llvm.org/docs/CrossCompilation.html)：ターゲットトリプルの定義
- [MSVC compatibility - Clang](https://clang.llvm.org/docs/MSVCCompatibility.html)：ClangがMSVCのABIとの互換を目指している旨の公式説明
- [Configure C/C++ debugging (launch.json reference)](https://code.visualstudio.com/docs/cpp/launch-json-reference)
- [Add new "console" launch config for cppvsdbg (vscode-cpptools PR #6794)](https://github.com/microsoft/vscode-cpptools/pull/6794)：`console`(`internalConsole` / `integratedTerminal` / `externalTerminal`)が旧`externalConsole`を置き換えた経緯
- [The PDB File Format — LLVM](https://llvm.org/docs/PDB/index.html)
- [DWARF Debugging Information Format](https://dwarfstd.org/)
- [Direct3D 12 のデバッグ レイヤー](https://learn.microsoft.com/ja-jp/windows/win32/direct3d12/understanding-the-d3d12-debug-layer)
