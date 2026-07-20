# シリアライゼーション

プレイヤー・オーサリング環境で共通のシリアライゼーションレイヤ。コアライブラリ `DivisionEngine` の `Serialization` 名前空間に実装され、[アセット管理](AssetManagement.md)や[スクリプティング](Scripting.md)のホットリロードはこの上に構築される。

- フォーマット非依存の読み書きインターフェース（現状の実装バックエンドは YAML）
- Source Generator によるシリアライザ実装の自動生成
- スコープ単位のオブジェクト管理と、スコープをまたぐオブジェクト参照の解決
- 部分的な再読み込み（アセットの再インポート）とアセンブリのホットリロードに耐える 2 パスのデシリアライズ

## 全体構造

```
[AutoSerialization] な型
        │  Source Generator が実装を生成
        ▼
ISerializable.Serialize/Deserialize          ... フィールドをIDで読み書き
        │  primitive/string/enum 以外は FormatterStore<T> 経由
        ▼
IValueFormatter<T>                            ... 値型・コレクション・参照の表現を決める
        │
        ▼
ISerializer / IDeserializer                   ... フォーマット抽象（YamlSerializer など）
        │
        ▼
IContainerSerializer / IContainerDeserializer ... オブジェクト単位のフレーミング
```

オブジェクトの所属と識別は以下で表現する。

| 型 | 内容 |
| --- | --- |
| `ScopeId` | `Guid`。シリアライズの単位（≒1ファイル、1アセット）の識別子 |
| `LocalId` | `int`。スコープ内でユニークなオブジェクトID |
| `GlobalId` | `(ScopeId, LocalId)`。スコープをまたぐ参照の識別子 |

## SerializationScope

シリアライズの単位。複数の `ISerializableObject` を内包し、`Guid`（`ScopeId`）で識別する。通常はファイルシステム上の1ファイル（エディタにおける1アセットファイル、ランタイムにおけるバンドル済みアセット）に対応する。

内部は `LocalId → ISerializableObject` の辞書と、実体の供給元である `ISerializationScopeLoader` を持つ。オブジェクトはスコープに**遅延で**materializeされる（`Resolve` 時にローダから生成される）。

## ISerializableObject

スコープに属するオブジェクト。`Scope` と `LocalId` を持ち、シリアライズ実装は `ISerializable` として Source Generator が生成する。

## シリアライザバックエンド

`ISerializer` / `IDeserializer` は次のプリミティブのみを持つ最小のインターフェース。

- スカラ: `Bool` / `I8` / `I16` / `I32` / `I64` / `F32` / `F64`
- `Blob(id, hint, ReadOnlySpan<byte>, BlobKind)`: バイト列。`BlobKind` は `ByteArray` / `Utf8` / `Utf16` で、文字列もこれで表現する
- `ObjectReference(id, hint, ISerializableObject?)`: 他オブジェクトへの参照
- `BeginArray` / `EndArray`、`BeginStruct` / `EndStruct`: 複合値のフレーミング

`IContainerSerializer` / `IContainerDeserializer` はこれを拡張し、`BeginObject(LocalId, Type)` / `TryBeginObject(out LocalId, out Type)` でオブジェクト単位のフレーミング（IDと型名の記録）を提供する。スコープファイルの読み書きはこちらを使う。

バイナリバックエンドは未実装（`Serialization/Binary` は空）。インターフェースはフォーマット非依存なので、実装を追加すれば載る。

### フィールドIDとヒント

すべての呼び出しに `int id`（フィールドID）と `ReadOnlySpan<byte> hintUtf8`（人間可読なフィールド名のヒント）を渡す。

- ID は既定でフィールド名（`[Serialize("name")]` で明示した場合はその名前）の **FNV-1a 32bit ハッシュ**
- 衝突時や互換性維持のために `[Serialize(123)]` で明示指定できる
- 生成時に重複IDを検出したらコンパイルエラー（`DIVSER001`）

ID は書き込み・読み込みともに昇順ソートされている前提で、バックエンドはシーケンシャルに処理できる。`YamlDeserializer` は期待するIDと実際のIDが一致しない場合そのノードをスキップして既定値を返すため、フィールドの追加・削除に対しては壊れない。ただし YAML がバージョン管理などで**未ソート状態になった場合のフォールバック（非シーケンシャル読み）は未実装**。

ヒントは人間可読性のためにバックエンドへ渡しているが、YAML バックエンドでは現状出力していない（キー直後に `#` を書くと YAML として不正になるため、別の表現を検討する必要がある）。

### ref struct 制約

シリアライザ実装は可変の `ref struct`（`YamlSerializer` は `Utf8YamlEmitter` を、`YamlDeserializer` は `YamlParser` を値で保持する）。値コピーするとライタ／パーサの状態が分岐して壊れるため、**必ず `ref` で受け渡す**。インターフェースのメソッドはすべて

```csharp
void Serialize<T>(ref T serializer) where T : ISerializer, allows ref struct
```

の形にしてある。`allows ref struct` によりジェネリック経由で ref struct を扱いつつ、ボクシングと仮想呼び出しを避けてバックエンドごとに特殊化された実装を得る。

### YAML バックエンドの形式

VYaml ベースの `YamlSerializer` / `YamlDeserializer`。キーはIDの文字列表現。

```yaml
# オブジェクト1件 = 1 YAML ドキュメント。複数オブジェクトは --- で区切る
0: 0                                   # LocalId
1: MyGame.Player                       # Type.FullName（アセンブリ名は含めない）
2:                                     # フィールド本体
  1234: 42                             # スカラはそのまま
  2345: {0: 2, 1: "hello"}             # Blob: {kind, value}（ByteArray は base64）
  3456: {0: 3, 1: [1, 2, 3]}           # 配列: {length, elements}、null は length=-1
  4567: {0: 8f3a..., 1: 5}             # 参照: {scope guid, local id}、null は (Guid.Empty, -1)
  5678: {0: 1.0, 1: 2.0}               # struct はそのままマッピング
---
0: 1
...
```

型名に `Type.FullName` のみを使うのは、アセンブリ名を含めるとホットリロードで再コンパイルされたユーザーアセンブリと一致しなくなるため。解決は `ITypeResolver`（後述）→ `Type.GetType` → ロード済みアセンブリの走査、の順にフォールバックする。

## フォーマッタ

`IValueFormatter<T>` は「1つのキー付きエントリとして T を読み書きする」責務を持つ。スカラ型は単一の値を書き、複合型は `BeginStruct`/`EndStruct` で自分自身をフレーミングする。参照型のフォーマッタは null の表現も自分で担う。

解決は型ごとの static キャッシュ `FormatterStore<T>.Formatter` を通す。生成コードは primitive / string / enum 以外のフィールドすべてでここを読む。

登録経路は3つ。

1. **`[CustomFormatter(typeof(T))]`** — Source Generator が `[ModuleInitializer]` を生成し `FormatterRegistry.Register<T>` を呼ぶ。オープンジェネリック（`typeof(List<>)`）はファクトリとして登録し、必要時にクローズする。プリミティブ・`byte[]`・`Guid`・`System.Numerics` のベクトル／行列・`List<>` などが標準で用意されている
2. **`[AutoSerialization]` な struct** — 生成された入れ子の `GeneratedFormatter` が同様に自動登録される
3. **実行時フォールバック** — `FormatterRegistry.Resolve<T>` が、enum → `EnumFormatter<T>`、`ISerializableObject` な class → `ObjectReferenceFormatter<T>`、1次元配列 → `ArrayFormatter<T>`、登録済みオープンジェネリック → クローズ、の順に生成する

### 初期化順序と静的検証

モジュール初期化子はそのアセンブリの何かに触れるまで走らない。そのため各アセンブリはアセンブリ属性 `[FormatterRegistration(target, formatter)]` を生成しておき、解決に失敗した際に `FormatterRegistry` がこの属性を持つロード済みアセンブリの `RunModuleConstructor` を強制的に呼ぶ。これで登録順に依存しなくなる。

同じ属性はコンパイル時にも使う。参照アセンブリ群の `[FormatterRegistration]` を集めて「解決可能な型の集合」を作り、フォーマッタが存在しないフィールドをコンパイルエラーにする（`DIVSER002`）。実行時例外ではなくビルド時に落とすのが狙い。

## Source Generator によるシリアライザ実装

`AutoSerializationGenerator`（`IIncrementalGenerator`）が `[AutoSerialization]` の付いた型を処理する。

- 対象は `[Serialize]` の付いたフィールド。自動プロパティは `[field: Serialize]` で対応する（`IFieldSymbol.IsImplicitlyDeclared` で検出し、名前はプロパティ名を使う）
- **継承階層をベース側から辿って**フィールドを収集し、出力時にIDでソートする。派生クラス側にベースクラスのフィールドアクセスを含むコードを生成することで、ID順のシーケンシャルアクセスを保証する
- class の場合: `ISerializable` の明示的実装を `partial` に生成する
- struct の場合: 入れ子の `GeneratedFormatter`（`IValueFormatter<T>` 実装）、`static Formatter` プロパティ、モジュール初期化子を生成する

### インラインシリアライゼーション

struct に対する上記の生成が「インラインシリアライゼーション」にあたり、struct は `ISerializableObject` のフィールドとして値のまま埋め込まれる。class をインラインにしないのは、参照として表現すればポリモーフィズムも共有参照も自然に扱えるため。

### 既知の制約

- ベースクラスの `private` フィールドは派生クラス側の生成コードから参照できない。`UnsafeAccessor` の利用、またはベース側にバイパス経路を用意する（ユーザーコードから隠すために `EditorBrowsable` などで制御する）対応は未実装
- ジェネリック型はモジュール初期化子を生成できないため自動登録の対象外。使用側で明示的に登録する必要がある
- マルチスレッド対応は未着手

## オブジェクト参照とライフサイクル

他の `ISerializableObject` への参照は `GlobalId`（スコープID + ローカルID）で表現する。

```csharp
public interface ISerializationScopeLoader : IDisposable
{
    ISerializableObject? Load(LocalId id);                                   // pass 1: インスタンス生成のみ
    void Deserialize(ISerializableObject obj, ISerializedObjectResolver r);  // pass 2: フィールド充填
}
```

### 2パスデシリアライズ

デシリアライズ実行時点で参照先を取得できる必要があるが、参照先の中身がまだ埋まっていなくても構わない。したがって

1. **pass 1**: `Load` が `Activator.CreateInstance` で空のインスタンスを作り、スコープに登録する
2. **pass 2**: `Deserialize` がそのインスタンスのフィールドを埋める。参照フィールドは `ISerializedObjectResolver.Resolve(GlobalId)` を通り、未生成なら pass 1 が走ってキューに積まれる

リゾルバ実装（`ObjectManager.Resolver` / オーサリング側の `AssetResolver`）は新規生成されたオブジェクトをキューに積み、現在のオブジェクトのデシリアライズ完了後に `DrainPending` でまとめて pass 2 を実行する。これにより参照の循環があっても無限再帰しない。

この設計上、**デシリアライズをコンストラクタで行うことはできない**。同じ理由で、アセットの再インポートなど部分的な再読み込みではオブジェクトを作り直さず、既存インスタンスのフィールドだけを更新する（`SerializationScope.Reload`）。これにより他スコープからの参照が生き残る。

### クラス定義が変わる場合（ホットリロード）

型そのものが差し替わるとインスタンスの再作成が避けられないため、部分的な更新はできない。ロード済みの全スコープをシリアライズし、リロード後にデシリアライズし直すしかない。コア側には次の仕組みがある。

- `SerializationScope(SerializationScope source)`: 同じID構成で新しい型のインスタンス群を作るコピーコンストラクタ
- `SerializationScope.Transfer`: 旧オブジェクトを YAML にシリアライズし、新オブジェクトへ即座にデシリアライズして状態を移送する
- `ObjectManager.ReloadClasses`: 全スコープに対して上記を適用した新しい `ObjectManager` を返す

オーサリング環境で実際に行われるリロードは、これをディスク上のスコープファイル経由で行う `AssetDatabase.ReloadScripts` が担当する。詳細は[スクリプティング](Scripting.md#コードのリロード)を参照。
