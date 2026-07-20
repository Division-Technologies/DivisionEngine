# クロスプラットフォーム方針

`src/Native`は、Windows / macOS / Linuxで動作するゲームエンジンのコア部分。
コードや設計は常にクロスプラットフォームを前提にする。

## なぜ

プラットフォーム依存の実装をそのまま書くと、他OSでビルド・動作が破綻する。具体例:

- D3D12などWindows専用グラフィックスAPI
- `#pragma comment(lib)`のようなMSVC固有ディレクティブ

## 守ること

- **プラットフォーム固有コードは抽象化レイヤーの背後に隔離する**。プラットフォーム依存のAPI呼び出しを共通コードへ直接書かない。
- **ビルド設定(リンクするライブラリ等)はソース内pragmaではなくCMake側に集約する**。
  - 例: DX12/DXGIのリンクは`#pragma comment(lib, ...)`ではなく`CMakeLists.txt`の`if(WIN32) target_link_libraries(main PRIVATE d3d12 dxgi) endif()`で行う。
- Windows専用APIを直接使う箇所(現状のDX12初期化など)は、あくまで隔離される前提の一時的な実装として扱う。

## 現状のビルド

clang + CMake + Ninja(プリセット`clang-debug` / `clang-release`)。
