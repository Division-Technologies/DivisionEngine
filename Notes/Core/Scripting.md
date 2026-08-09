# スクリプティング

ユーザーコードは C# で記述する。オーサリング環境では C# コードの変更・コンパイル・リロードを実行中に行える。実装は `DivisionEngine.Authoring` の `Assets/Scripting`。

[アセット管理](AssetManagement.md)のインポートパイプラインの上に載っており、「csproj をインポートすると DLL が生成され、DLL が更新されるとユーザー AssemblyLoadContext がリロードされる」という流れになる。

## インポート

`CSharpProjectImporter`（`[AssetImporter(".csproj")]`）が csproj をアセットとしてインポートし、その一部として DLL へのコンパイルを行う。

現在の .NET で利用される C# コンパイラ (Roslyn) は C# でセルフホストされ .NET 向けライブラリとして NuGet に公開されており、これを直接利用する。

- `MSBuildWorkspace` でプロジェクトを開く。近年の Roslyn では MSBuild が別プロセスの BuildHost で動くため `MSBuildLocator` は不要
- `BaseIntermediateOutputPath` / `BaseOutputPath` をグローバルプロパティで**アセットの成果物ディレクトリに向ける**。Assets ツリーの中に `bin` / `obj` を作らせないため。MSBuild は相対パスをプロジェクトディレクトリ基準で解決するので、これらは必ず絶対パスで渡す
- プロジェクトの全ソースドキュメントを `AssetImportContext.DependsOnFile` で入力依存として記録する。以後 `.cs` の編集がファイル→アセット依存経由で csproj の再インポートを引き起こす
- `Compilation.Emit` の結果をメモリ上で受け、成功時のみ成果物ディレクトリに DLL を書き出す

結果は `CompiledAssembly`（メインオブジェクト）に入る。

```csharp
[AutoSerialization]
public sealed partial class CompiledAssembly : ISerializableObject
{
    [Serialize] public string AssemblyName;
    [Serialize] public string DllPath;        // 成果物 DLL の絶対パス（マシンローカル）
    [Serialize] public bool Success;
    [Serialize] public List<string> Diagnostics;  // エラー・警告
}
```

`AssetDatabase` はインポート結果が成功した `CompiledAssembly` であれば DLL パスを記録し、`ScriptsDirty` フラグを立てる。キャッシュヒット時もスクリプトアセットについてはメインオブジェクトをロードして同様に追跡する。

## AssemblyLoadContext の構成

AssemblyLoadContext は .NET においてアセンブリ参照の解決スコープを分離するための仕組みである。独自に作成した ALC が解決しなかったアセンブリ参照はデフォルトの ALC にフォールバックされる。ALC 間の依存関係を単方向に整理することで、ある ALC に属するアセンブリをまとめてアンロードする、といった操作がしやすくなる。

- [System.Runtime.Loader.AssemblyLoadContext について](https://learn.microsoft.com/ja-jp/dotnet/core/dependency-loading/understanding-assemblyloadcontext)
- [.NET でアセンブリのアンロード機能を使用およびデバッグする方法](https://learn.microsoft.com/ja-jp/dotnet/standard/assembly/unloadability)

DivisionEngine での実例を示す。`UserAssemblyLoadContext` は collectible な ALC で、**ユーザーがコンパイルした DLL のみ**を保持する。

```csharp
public sealed class UserAssemblyLoadContext() : AssemblyLoadContext("DivisionUser", true)
{
    protected override Assembly? Load(AssemblyName assemblyName) => null;
}
```

`Load` が null を返すことで、エンジン・オーサリングライブラリ・BCL は既定 ALC から解決される。これにより `ISerializableObject` などの共有型の同一性が1つに保たれる（ユーザー ALC 側に複製がロードされると型が別物になり、シリアライズもキャストも壊れる）。アンロード時に解放されるのはユーザーコードだけになる。

`ScriptHost` がこの ALC を所有し、次を担当する。

- `Swap(dllPaths)`: 旧 ALC をアンロードし、新しい ALC に DLL をロードする。DLL は **ファイルではなくメモリから**ロードする（ファイルをロックすると再コンパイルできないため）
- ロード後に各モジュールの `RunModuleConstructor` を実行し、ユーザーアセンブリ側の[フォーマッタ登録](Serialization.md#初期化順序と静的検証)を確実に走らせる
- `TypeResolver`: シリアライズされた型名（`Type.FullName`）を、現在ロードされているユーザーアセンブリに対して解決する `ITypeResolver`

## コードのリロード

DLL が更新されると（= `ScriptsDirty`）、ユーザー ALC をリロードする。アセンブリを正しくアンロードするには、エンジン側からユーザー ALC 由来のインスタンスへの参照をアンロード前にすべて消す必要がある。シリアライズ可能なデータについては、アンロード前に全部シリアライズし、リロード後にデシリアライズする。

実際の手順は `AssetDatabase.ReloadScripts` が持つ（ライブオブジェクトグラフを所有しているのが `AssetDatabase` のため）。

1. インスタンス化済みの全スコープを現在の状態でシリアライズしてメモリに退避する
2. ライブオブジェクトグラフを破棄する（スコープ・ローダを全クリア）。これで旧 ALC への参照が消える
3. `ScriptHost.Swap` で ALC を差し替える。以後の型解決は新しい `TypeResolver` を経由させる
4. 退避したバイト列から `FileScopeLoader` を作ってスコープを再構築し、全オブジェクトを解決 → `DrainPending` でデシリアライズする

オブジェクトの相互参照は `GlobalId` で表現されているのでリロードをまたいで復元される。一方で**インスタンスは別物になる**ため、データベース外部で参照を保持していた場合はリロード後に取り直す必要がある。

`ScriptHost.Unload` はアンロード後に `GC.Collect` + `WaitForPendingFinalizers` を数回回す。ALC のアンロードは (.NET Framework で利用できた AppDomain のような) 専制的なものではなく、全参照が消えて初めて完了する協調的な処理であり、リークがある場合は旧アセンブリが残り続ける。ALC 間に強いオブジェクト参照が残存しないようにエンジン側のコードを注意深く記述する必要がある。

### 型解決の優先順位

`YamlDeserializer.ResolveType` は、シリアライズされた GUID 型ID（[シリアライゼーション](Serialization.md#型の識別)参照）から次の順で型を探す。

1. `ITypeResolver`（リロード中に注入されるユーザー ALC のリゾルバ。`[SerializedTypeRegistration]` から構築したマップ → 登録属性を持たないアセンブリ向けに `ISerializable` 実装型を走査したハッシュ索引）
2. `SerializedTypeRegistry`（非 collectible アセンブリの登録属性）

GUID でない型ID（旧形式の `Type.FullName`）は名前ベースの解決（`ITypeResolver` → `Type.GetType` → アセンブリ走査）にフォールバックする。1 を最優先にするのは、AppDomain 上にまだ残っている**アンロード予定の古いアセンブリ**に型がバインドされるのを防ぐため。

## エンジンループへの組み込み

いずれもエンジンスレッド上で実行するため、システムとして登録する。順序が重要。

1. `AssetRefreshSystem` — ウォッチャが溜めた変更を処理する。`.cs` の変更→ `.csproj` の再インポート→再コンパイル→`ScriptsDirty` が立つ
2. `ScriptReloadSystem` — `ScriptsDirty` なら上記のリロード手順を実行する

ファイル変更イベントのコールバックスレッドでオブジェクトグラフを触らないための構成。

## 制約・今後

- `static` フィールドなどシリアライズ対象外の状態はリロードで失われる
- リロードは全スコープを対象とする全体処理で、部分的なリロードはできない（型自体が差し替わるため。[シリアライゼーション](Serialization.md#クラス定義が変わる場合ホットリロード)参照）
- コンパイルエラー時は `CompiledAssembly.Success = false` として診断のみ保持し、リロードは行わない。エディタ UI への提示は今後
