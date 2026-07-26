# デバッグ

## VS CodeのRun and Debug

ルート`.vscode/launch.json`にデバッグ構成を用意している。

- **Ctrl+Shift+D**でRun and Debugビューを開き、構成`Debug main (clang-debug)`を選んで**F5**で起動
- 起動前に`preLaunchTask`で自動ビルドが走る(`CMake: Build (clang-debug)`)

### デバッガの種類

Windows上のClangは`x86_64-pc-windows-msvc`をターゲットにしており、MSVC ABIに合わせたデバッグ情報を出力する。そのため、デバッガには`ms-vscode.cpptools`が提供する**cppvsdbg**(Visual Studioデバッガ)を使う。

### デバッグ情報(PDB / CodeView)

`clang-debug`ビルドでは、コンパイル時に`-g -Xclang -gcodeview`が付与され、CodeView形式のデバッグ情報が生成される。リンク時に`main.pdb`が出力され、cppvsdbgはこれを参照してブレークポイント・変数表示・ステップ実行を行う。

## D3D12デバッグレイヤー

> 現状、`main.cpp`の`EnableDebugLayer()`は**コメントアウト**されており未使用。DXGI側の`DXGI_CREATE_FACTORY_DEBUG`(`_DEBUG`ガード)のみ有効化している。有効化・検証は今後の課題。

D3D12デバッグレイヤーは、APIの誤用(リソースステートの不整合、バリア漏れなど)を実行時に検出してデバッグ出力へ警告を出す機能。

- 有効化するには`ID3D12Debug::EnableDebugLayer()`をデバイス生成より前に呼ぶ
- Windowsの**「グラフィックスツール」**オプション機能の導入が必要
- 出力はデバッグ出力(OutputDebugString)に流れる。VS Codeのcppvsdbg配下で実行していれば、そのままデバッグコンソールに表示される
    - デバッガを介さず実行した場合や専用ツールで見たい場合は、`CobaltFusion.DebugViewPP`(DebugView++)などのDebugView系ツールを使う(プロセスフィルタが強い)

## 参考

- [Direct3D 12 のデバッグ レイヤー](https://learn.microsoft.com/ja-jp/windows/win32/direct3d12/understanding-the-d3d12-debug-layer)
