# アセット管理

プレイヤー・オーサリング環境で共通の[シリアライゼーション](Serialization.md)レイヤの上に、オーサリング環境向けのアセット管理レイヤを提供する。実装は `DivisionEngine.Authoring` の `Assets` 名前空間。

- オーサリング環境にあるアセットファイルの読み込み
- ユーザーコードにより実装可能なインポート処理
- インポートされたデータのキャッシュ管理
- ファイルの変更監視と、依存関係に基づいた部分的な再インポートのトリガー

## AssetDatabase

アセットレイヤの中心。1つのプロジェクト（アセットルートフォルダ群 + 永続キャッシュディレクトリ）に対応する。

**アセットの GUID はそのまま `ScopeId` であり、1つのアセットファイルが1つの `SerializationScope` に対応する。** GUID はソースファイル隣の `.meta` サイドカーに永続化されるため、セッションやマシンをまたいで安定する。アセットをまたぐ参照は `AssetResolver` を通して遅延解決される。

主な API:

| API | 内容 |
| --- | --- |
| `Rescan()` | 全ルートを走査し、新規・変更アセットをインポート、消えたアセットを破棄 |
| `Refresh()` | ウォッチャが溜めた変更のみを適用（非ウォッチ時は `Rescan`） |
| `ImportAsset<T>(path)` / `LoadAsset<T>(guid or path)` | インポート／ロードしてメインオブジェクトを返す |
| `Reimport(path)` | 指定アセットと、それに依存する全アセットを再インポート |
| `CreateScope()` / `AddObject()` / `SaveAsset()` | 新規アセットをメモリ上で構築して保存 |
| `ReloadScripts()` / `ReloadScriptsIfDirty()` | [スクリプティング](Scripting.md)参照 |

## アセットの種類

### 直接読み込めるもの (Direct)

[シリアライゼーションバックエンド](Serialization.md#シリアライザバックエンド)がそのまま読めるもの。`.meta` は持つがインポーターを持たず、アセットファイル自体がスコープファイルになる。

### インポートが必要なもの (Imported)

上記以外。拡張子で判別し、ユーザーコードで実装可能なインポーターを通して実行時のデータ構造に変換する。インポート結果はディスクにキャッシュされ、次回以降は再インポートを避けられる。

### インポーターの選択

`AssetDatabase.ResolveImporter` は次の順で決定する。

1. `.meta` に記録されたインポーターインスタンス（設定込みで復元される）
2. 拡張子に対して `AssetImporterRegistry` に登録されたインポーター
3. `.meta` が存在する場合（＝インポーター無しの `.meta`）→ Direct アセットとして扱う
4. いずれでもなければ `RawBinaryImporter`（フォールバック）

`RawBinaryImporter` はファイル全体を `BinaryAsset`（バイト列 + 拡張子）として取り込む。専用インポーターがない拡張子でもとりあえずアセットとして扱えるようにするためのもの。

## インポーター

```csharp
public interface IAssetImporter : ISerializable
{
    int Version => 0;                       // ロジック変更時に上げてキャッシュを無効化
    void Import(AssetImportContext context);
}
```

- `[AssetImporter(".png", "jpg")]` で拡張子に紐づける。`AssetImporterRegistry` がロード済みアセンブリを走査して収集する（将来的には Source Generator による登録に置き換える余地がある）
- インポーター自身が `ISerializable`。`[AutoSerialization]` を付ければ設定が `.meta` に永続化される

`AssetImportContext` がインポータに渡される I/O 面。

- `SourcePath` / `OpenSource()` / `ReadAllBytes()`: 入力
- `AddObject()` / `SetMainObject()`: 生成したオブジェクトをスコープに登録する。メインオブジェクトが `LoadAsset<T>` の戻り値になる
- `DependsOnAsset(ScopeId)`: 他アセットへの依存（アセット間依存グラフに反映）
- `DependsOnFile(path)`: アセットではない入力ファイルへの依存（例: csproj が使う `.cs`）
- `ArtifactPath(name)` / `WriteArtifact(name, data)`: アセットごとの成果物ディレクトリ（例: コンパイル済み DLL）

インポーターが例外を投げた場合はログを出してそのアセットをスキップする。1つの壊れたアセットで Refresh 全体が失敗しないようにするため。

## `.meta` ファイル

アセットと同じディレクトリに `<asset>.meta` として置く。中身は YAML で、2つのドキュメントからなる。

1. `AssetMetaHeader` — GUID、`HasImporter`、`SourceHash`、`ImporterVersion`、メインオブジェクトの `LocalId`、依存アセットGUIDリスト、入力ファイル（ソースファイルからの**相対パス**）
2. インポーター本体 — 具象型名付きのドキュメント。型情報が入るのでインポーターの種別と設定がそのまま復元される（Direct アセットでは省略）

入力ファイルを相対で持つのはプロジェクトを移動・共有しても壊れないようにするため。読み込み時にソースファイルのディレクトリ基準で絶対化する。

## インポートキャッシュ

キャッシュディレクトリ（プロジェクトの Library 相当）に GUID をキーとして置く。

- `<guid>.cache` — インポートで生成されたスコープをシリアライズしたもの
- `<guid>.artifacts/` — インポーターが書き出した成果物（DLL など）

キャッシュヒットの条件は次の全て。

- `.meta` にインポーターが記録されている
- `SourceHash` が一致する（ソースファイル + 全入力ファイルの内容を、パス順にソートして連結した XxHash64）
- `ImporterVersion` が一致する
- `<guid>.cache` が存在する

ヒット時はインポーターを実行せず、GUID・スコープファイル・メインID・依存関係を登録するだけで済ませる。

## 依存関係

2種類の依存を持つ。

- **アセット → アセット** (`AssetDependencyGraph`): `DependsOnAsset` で記録。逆辺を保持し、あるアセットが再インポートされたら依存側を再帰的に再インポートする
- **ファイル → アセット** (`_fileDependents`): `DependsOnFile` で記録した入力ファイルの逆引き。`.cs` が変更されたらそれをコンパイルした `.csproj` アセットを再インポートする、という経路がこれ

## ファイルの変更監視

`AssetWatcher` が `FileSystemWatcher` でルートを監視するが、**イベントでは何もインポートしない**。変更／削除パスをキューに積むだけで、実際の処理は `Refresh()` が呼ばれたときに行う。

これは、ウォッチャのコールバックスレッドでオブジェクトグラフを触らないための設計。`AssetRefreshSystem` をエンジンに登録しておけば、フレームごとにエンジンスレッド上で `Refresh()` が実行される（[スクリプティング](Scripting.md)のリロードも同様に `ScriptReloadSystem` としてこの後段に置く）。

`.meta` 自体の変更イベントは無視する（インポートが `.meta` を書き換えるため、無視しないとループする）。

## 再インポートとオブジェクト同一性

すでにロード済みのスコープを再インポートする場合、`ReloadInPlace` により**既存インスタンスを作り直さずフィールドだけを更新**する。他アセットが保持している参照が切れないようにするためで、[シリアライゼーションの2パス設計](Serialization.md#2パスデシリアライズ)がこれを可能にしている。

## ロードの流れと VYaml の制約

`LoadAsset` は次の順で動く。

1. 対象スコープをロード（`GetOrLoadScope`）
2. `PreloadClosure` — 参照先スコープの推移閉包を先に幅優先でインデックス化する
3. `AssetResolver` でメインオブジェクトを解決 → `DrainPending` で 2 パス目を流す

2 が必要なのは VYaml のパーサが再入不可なため。デシリアライズの途中で別ファイルをパースし始めることはできないので、必要なファイルのインデックス化（`FileScopeLoader.BuildIndex`、および `CollectReferencedScopes` による参照先スコープの収集）を先に済ませておき、デシリアライズ中は `Activator.CreateInstance` だけで済むようにしている。

`FileScopeLoader` はスコープファイル（Direct アセット本体、またはキャッシュファイル）のバイト列を保持し、`Load` = インスタンス生成、`Deserialize` = 該当ドキュメントまで再走査してフィールド充填、という2パス契約を実装する。

## 今後

- スコープファイル内のオブジェクト位置インデックス化（現状の線形走査の解消）
- インポートの並列化・非同期化
- インポーター登録の Source Generator 化
- プレイヤー向けバンドル（複数スコープのパッケージング）
