# DivisionEngine

A repository for developing an original game engine from scratch.

The goal of this project is **not** to build a production-ready engine, but to **learn through the process** of game engine development. As such:

- Commits that merely "get things done" without demonstrating understanding will not be accepted.
- Every feature implementation must be accompanied by documentation summarizing the research and findings behind it.
- Research documents are maintained in a separate repository: https://github.com/Division-Technologies/DivisionNotes


## Architecture
### Design Principles
- Graphics abstraction: Graphics APIs are accessed through a Hardware Abstraction Layer (HAL), supporting both DirectX 12 and Vulkan.
- Built-in GUI framework: A custom GUI framework is embedded within the C# engine layer. Engine-level GUI (e.g. debug overlays) is included in the build output, so debug screens are automatically available in every build.
- Platform: Windows is the primary target. Multi-platform support is planned for the future.

### Layer Design
```mermaid
block-beta
  columns 2

  cs_label["C#"]:2
  gui["Built-in GUI"] script["User Script"]
  sep["▼"]:2
  cpp_label["C++"]:2
  hal["HAL (Hardware Abstraction Layer)"]:2
  dx12["DirectX 12"] vulkan["Vulkan"]

  style sep fill:none,stroke:none,color:#888,font-size:12px

  style cs_label fill:#2a7edf,color:#fff
  style gui fill:#4a9eff,color:#fff
  style script fill:#4a9eff,color:#fff
  style cpp_label fill:#df5f30,color:#fff
  style hal fill:#ff7f50,color:#fff
  style dx12 fill:#ff7f50,color:#fff
  style vulkan fill:#ff7f50,color:#fff
```

### Layer Structure

```mermaid
block-beta
  columns 3

  editor_label["DivisionEngine.Editor（C#）"]:3
  e1["自前GUI"] e2["スクリプティング"] e3["ホットリロード"]
  e4["ビルドシステム"] e5["成果物に含められる設計"]:2

  sep1["▼"]:3

  core_label["DivisionEngine.Core（C# → C++）"]:3
  c1["ゲームループ"] c2["オブジェクト管理"] c3["シリアライズ"]
  c4["ネットワーク通信"] c5["ジョブスケジューラ"] c6["画面描画（HAL）"]
  c7["シェーダー（HLSL / DXC）"]:3

  sep2["▼"]:3

  native_label["DivisionEngine.Native（C++）"]:3
  n1["HAL"] n2["入力受付"] n3["サウンド"]
  n4["データ保存"] n5["マルチスレッド処理"] n6["DX / Vulkan"]

  style sep1 fill:none,stroke:none,color:#888
  style sep2 fill:none,stroke:none,color:#888

  style editor_label fill:#2a7edf,color:#fff
  style e1 fill:#4a9eff,color:#fff
  style e2 fill:#4a9eff,color:#fff
  style e3 fill:#4a9eff,color:#fff
  style e4 fill:#4a9eff,color:#fff
  style e5 fill:#4a9eff,color:#fff

  style core_label fill:#2a8f4f,color:#fff
  style c1 fill:#4abf6f,color:#fff
  style c2 fill:#4abf6f,color:#fff
  style c3 fill:#4abf6f,color:#fff
  style c4 fill:#4abf6f,color:#fff
  style c5 fill:#4abf6f,color:#fff
  style c6 fill:#4abf6f,color:#fff
  style c7 fill:#4abf6f,color:#fff

  style native_label fill:#df5f30,color:#fff
  style n1 fill:#ff7f50,color:#fff
  style n2 fill:#ff7f50,color:#fff
  style n3 fill:#ff7f50,color:#fff
  style n4 fill:#ff7f50,color:#fff
  style n5 fill:#ff7f50,color:#fff
  style n6 fill:#ff7f50,color:#fff
```


---


## 日本語

オリジナルのゲームエンジンをゼロから開発するリポジトリです。

本プロジェクトの目的は、完成されたエンジンを作ることでは**ありません**。ゲームエンジン開発を通して**学びを得ること**を目的としています。

- 完成を優先するだけのコミットは受け入れません
- 機能を実装する際は、必ず調査結果をドキュメントにまとめる必要があります
- 調査ドキュメントは別リポジトリで管理しています：https://github.com/Division-Technologies/DivisionNotes

### 設計方針
- グラフィック抽象化：グラフィックAPIはHAL（ハードウェア抽象化レイヤー）を介してアクセスし、DirectX 12 と Vulkan の両方をサポートします。
- 自前GUIフレームワーク：C#エンジンレイヤー内に独自のGUIフレームワークを内包します。エンジン部分のGUI（デバッグ画面等）はビルド成果物に含まれ、すべてのビルドでデバッグ画面が自動的に付随します。
- プラットフォーム：まず Windows を対象とし、将来的にマルチプラットフォーム対応を目指します。
