# Notes
Research and documentation for DivisionEngine.

This directory is part of the DivisionEngine monorepo. Every feature implementation must be accompanied by documentation summarizing the research and findings behind it, and that documentation lives here.


## Structure
Documentation mirrors the source layout under `src/`:

| Directory | Corresponds to | Description |
|---|---|---|
| `Engine/` | `src/Engine`（C#） | Research on the C# engine/editor layer: GUI, scripting, and build pipelines |
| `Native/` | `src/Native`（C++） | Research on graphics APIs, OS-level APIs, and native interop |

## Guidelines
- Document what you researched and learned, not just the final implementation
- Include references to external resources (articles, specifications, source code) used during research
- Write documentation before or during implementation, not after


---


## 日本語
DivisionEngine の調査・ドキュメントです。

このディレクトリは DivisionEngine モノレポの一部です。DivisionEngine では機能を実装する際に必ず調査結果をドキュメントにまとめる必要があり、そのドキュメントをここで管理します。


## 構成
ドキュメントは `src/` 以下のソース構成に対応させています：

| ディレクトリ | 対応 | 内容 |
|---|---|---|
| `Engine/` | `src/Engine`（C#） | C#エンジン/エディタレイヤー(GUI、スクリプティング、ビルドパイプライン)に関する調査 |
| `Native/` | `src/Native`（C++） | グラフィックAPI、OS API、ネイティブ連携に関する調査 |

## ガイドライン
- 最終的な実装だけでなく、調査した内容や学んだことを記述する
- 調査中に参照した外部リソース（記事、仕様書、ソースコード）を記載する
- ドキュメントは実装の前または最中に書くこと。実装後ではない
