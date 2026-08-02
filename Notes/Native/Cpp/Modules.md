# C++20 モジュール
`src/Native`はC++20のモジュールを利用している。
モジュールは、従来のヘッダ`#include`（プリプロセッサによるテキストの逐次挿入）に代わる仕組みで、宣言を名前付きの単位としてエクスポート・インポートする。
ヘッダのようにテキストを毎回展開・再解析しないため、ビルドの高速化や、マクロ汚染・重複定義の回避といった利点がある。


## 用語
モジュールインターフェース単位（module interface unit）
- モジュールが外部へ公開する宣言を定義する翻訳単位
    - `export module <名前>;`で始める
- 拡張子はMSVC/CMakeの慣習で`.ixx`（Clang単体では`.cppm`も用いられる）
    - 本プロジェクトはCMakeの`FILE_SET TYPE CXX_MODULES`に`.ixx`を登録している
- 例（`Printer.ixx`）：`export module Printer;`のあと`export struct Printer { ... };`で型をエクスポートする

グローバルモジュールフラグメント（global module fragment）
- モジュール宣言より前の、`module;`から`export module`までの領域
- 従来のヘッダ`#include`（例：`#include <iostream>`）はここに置く
    - モジュール本体のパースから切り離すための領域

BMI（Built Module Interface）
- モジュールインターフェースをコンパイルして得られる、バイナリの中間表現
    - Clangでの拡張子が`.pcm`
- `import Printer;`すると、コンパイラは`Printer.ixx`を再解析せず、この`Printer.pcm`（BMI）を読み込んで宣言を取得する
    - プリコンパイル済みヘッダ（PCH）のモジュール版にあたる
- Clangはモジュールをインポートする際、`.pcm`を探索して読み込む


## ビルドとの関係
- モジュールを使うと、翻訳単位の間に「モジュールインターフェースを先にコンパイルしてBMIを作り、それを他の翻訳単位が読み込む」という依存関係が生じる
    - そのためソースを単純に並列コンパイルできず、依存関係のスキャンが必要になる
- CMake + Ninjaはこれに対応しており、ビルド時にモジュール依存を走査して順序を決める
    - ビルドログの`Scanning ... for CXX dependencies` / `Generating CXX dyndep file`がこれにあたる
- 生成されたBMIは`build/<preset>/`配下に`.pcm`として置かれる


## clangdによるpcmのロック（mmap）
エディタの言語サーバであるclangdも、補完・診断のためにモジュールのBMI（`.pcm`）を生成・参照する。
この際、clangdは`.pcm`をmmap（メモリマップドファイル）で開くことがある。
- mmap：ファイルの内容をプロセスの仮想アドレス空間に直接マッピングするOSの機能
    - `read` / `write`で明示的にコピーせず、ファイルをメモリ上のデータ（ポインタ）として扱えるため、大きなファイルを高速・省メモリで扱える
    - mmap自体はC++の仕様ではなくOSが提供する機能である
- Windowsでは、あるプロセス（clangd）が`.pcm`をmmapで開いている間、別プロセス（ビルド）が同じファイルを上書きできず、ビルドが`user-mapped section open`で失敗する

これが[Build](../Development/Build.md)の「clangdがモジュールのビルド成果物をロックする」問題の実体である。
回避策（VS Codeを閉じる、clangdを一時停止する）はそちらを参照。


## 参考
- [Standard C++ Modules — Clang](https://clang.llvm.org/docs/StandardCPlusPlusModules.html)
- [Modules — cppreference](https://en.cppreference.com/w/cpp/language/modules)
- [モジュール [P1103R3] — cpprefjp](https://cpprefjp.github.io/lang/cpp20/modules.html)
