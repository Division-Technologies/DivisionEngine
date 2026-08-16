# com_ptr
`src/Native`ではDirectX 12のオブジェクトを`winrt::com_ptr`で保持している。
com_ptrはCOMオブジェクトの参照カウントを自動で管理するスマートポインタで、C++/WinRTの`<winrt/base.h>`が提供する。
スマートポインタとRAIIそのものについては[スマートポインタとRAII](SmartPointer.md)を参照。


## 前提：COMと参照カウント
### COM（Component Object Model）
- バイナリレベルでの相互運用を目的とした、Windowsのオブジェクトモデル
- DirectXのオブジェクト（`ID3D12Device`、`IDXGIFactory6`など）はすべてCOMオブジェクトである
    - 型名の先頭の`I`はインターフェース（Interface）を表す
- 実体はvtableを持つインターフェースであり、アプリケーション側は実装クラスを知らないまま関数ポインタ経由で呼び出す

`IUnknown`
- すべてのCOMインターフェースが継承する基底インターフェース
- 3つのメソッドを持つ
    - `AddRef()`：参照カウントを1増やす
    - `Release()`：参照カウントを1減らし、0になったらオブジェクト自身を破棄する
    - `QueryInterface()`：同じオブジェクトの別のインターフェースを取得する

### オブジェクトモデル
オブジェクトモデルとは、オブジェクト（データと操作をまとめたもの）をどう定義し、生成、参照、破棄し、呼び出すかを定めた規約のことである。
- 言語やフレームワークはそれぞれ独自のオブジェクトモデルを持つ
    - 例：C++のクラスとvtable、.NETの型システム、JavaScriptのプロトタイプ
- ただしそれらは、通常その言語の処理系の中でしか通用しない

COMはこの規約を、特定の言語ではなくバイナリレベル（ABI）で定めたものである。
- 「メモリ上でオブジェクトがどう並び、どの位置の関数ポインタを呼べばメソッドを呼べるか」まで規定する
- そのためC++、C#、Rustなど言語をまたいでも、同じDLLのオブジェクトを同じ手順で扱える
    - DirectXがC++以外の言語からも利用できるのはこのため

### なぜバイナリレベルで定めるのか
C++にはABIの標準がなく、クラスのメモリ配置や名前修飾（マングリング）はコンパイラやそのバージョンごとに異なる。
- あるコンパイラでビルドしたDLLのC++クラスを、別のコンパイラでビルドしたアプリから安全には使えない
- `new` / `delete`が使うヒープもモジュールごとに異なるため、DLLの外で`delete`すると壊れる

COMはこれを次のルールで回避する。
- オブジェクトの実体（データメンバの配置）は公開せず、vtableを持つ純粋仮想インターフェースだけを公開する
    - vtableの並び（関数ポインタが宣言順に並ぶ）は事実上どのコンパイラでも一致するため、これが安定した共通点になる
- 生成はDLL側の関数（`D3D12CreateDevice`など）が行い、破棄も`Release()`でオブジェクト自身が行う
    - 確保と解放が同じモジュール内で完結するため、ヒープの不一致が起きない
- 呼び出し規約と、エラーの返し方（`HRESULT`）も固定する
    - 例外はABIを越えられないため、エラーは戻り値で返す

### COMが定めていること
| 項目 | COMでの決め方 |
|---|---|
| インターフェースの表現 | vtableを持つ純粋仮想クラス。`IUnknown`を継承する |
| 型の識別 | GUID（`IID`）。名前ではなく128bitの値で識別する |
| 寿命管理 | 参照カウント（`AddRef` / `Release`） |
| 機能の問い合わせ | `QueryInterface` |
| 生成 | API関数やクラスファクトリ（`D3D12CreateDevice`、`CoCreateInstance`など） |
| エラー通知 | `HRESULT` |

一度公開したインターフェースは変更しない、というルールもこのモデルに含まれる。
- メソッドの追加も含めて変更できない
    - vtableの並びが変わると、既存のバイナリが誤った位置の関数を呼ぶことになるため
- 機能追加は、新しいインターフェースを別のGUIDで追加し、`QueryInterface`で問い合わせる形をとる
    - `IDXGISwapChain1`→`IDXGISwapChain4`、`ID3D12Device`→`ID3D12Device5`といった連番はこれにあたる
    - 後述の[`as()`によるインターフェースの変換](#asによるインターフェースの変換)が必要になるのはこのため

### 参照カウント
- COMオブジェクトは自身の内部に参照カウントを持つ（侵入型参照カウント）
    - `std::shared_ptr`のように外部の制御ブロックで数えるのではなく、カウンタはオブジェクト側にある
    - そのため同じ生ポインタから何度com_ptrを作り直しても、カウントの管理は一貫する
- 生成関数（`D3D12CreateDevice`など）から受け取った時点でカウントは1であり、使い終わったら`Release()`を呼ぶ義務が呼び出し側にある
    - この`Release()`の呼び忘れ・二重解放を防ぐのがcom_ptrの役割


## C++/WinRT
com_ptrを提供するC++/WinRTは、WinRT（Windows Runtime）のAPIを標準C++から扱うためのライブラリである。
- Windows SDKに同梱されるヘッダオンリーの実装で、`winrt/`配下に置かれている
    - 名前空間は`winrt`
- com_ptrをCOMスマートポインタとして使うだけなら`<winrt/base.h>`のインクルードだけでよい
    - WinRTのAPI投影ヘッダや、cppwinrt.exeによるコード生成は不要
    - DirectXの利用にあたって`CoInitialize`などのCOMランタイム初期化も不要
- エラーは`HRESULT`ではなく例外（`winrt::hresult_error`）で扱う設計

### COMスマートポインタの世代
WindowsのC++向けライブラリは世代ごとに異なるCOMスマートポインタを持っており、資料やサンプルによって出てくる型が変わる。

| 世代 | 型 | 概要 |
|---|---|---|
| ATL (Active Template Library) | `CComPtr` | COM時代のヘルパー。Visual C++のATLに依存する |
| C++/CX | `^`（ハット） | `^`などのコンパイラ拡張で言語自体を拡張する方式。標準C++ではなく非推奨 |
| WRL (Windows Runtime C++ Template Library) | `Microsoft::WRL::ComPtr` | 標準C++のテンプレートのみで構成される。Windows 8世代 |
| C++/WinRT | `winrt::com_ptr` | 標準C++17のヘッダオンリー実装。WRLの後継にあたる |

- DirectXの公式サンプル（DirectX-Graphics-Samples、DirectX Tool Kitなど）はWRLの`ComPtr`を使っているものが多い
    - `Get()`は`get()`、`As()`は`as<T>()`、`&`や`ReleaseAndGetAddressOf()`は`put()`に読み替えればほぼ対応する
- Windows SDK外の選択肢として`wil::com_ptr`（[microsoft/wil](https://github.com/microsoft/wil)）もある
    - 例外版と`HRESULT`版を選べるが、外部依存として取り込む必要がある


## com_ptrの基本
- コピーすると`AddRef()`、デストラクタで`Release()`が呼ばれる
    - ムーブは所有権の移動なので参照カウントを変化させない
- 空かどうかは`bool`変換で判定できる
- `operator->`でインターフェースのメソッドを直接呼び出せる
    ```cpp
    winrt::com_ptr<ID3D12Device> device;
    device->CreateCommandAllocator(...);
    ```
    - ただし`device->AddRef()` / `device->Release()` / `device->QueryInterface()`はコンパイルエラーになる
        - `operator->`が`IUnknown`の3メソッドを隠しているため
        - 参照カウントを手動で壊すことを防ぎ、`as()`などのメンバ関数を使わせるという設計

### 主なメンバ関数
| メンバ | 戻り値 | 用途 |
|---|---|---|
| `get()` | `T*` | 生ポインタを引数として渡す（所有権は移動しない） |
| `put()` | `T**` | オブジェクトの受け取り先としてアドレスを渡す |
| `put_void()` | `void**` | `void**`を取るAPIへ渡す |
| `as<U>()` | `com_ptr<U>` | `QueryInterface`。失敗時に例外を投げる |
| `try_as<U>()` | `com_ptr<U>` | `QueryInterface`。失敗時は空を返す |
| `copy_from()` / `copy_to()` | — | `AddRef`を伴って生ポインタと受け渡しする |
| `attach()` / `detach()` | — | `AddRef`せずに所有権を受け取る／手放す |
| `= nullptr` | — | 参照を解放して空にする |

### `get()`を使う場面
COMのAPIは他のオブジェクトを引数に取ることが多く、そこには生ポインタを渡す。
```cpp
device->CreateCommandList(
    0, D3D12_COMMAND_LIST_TYPE_DIRECT, command_allocator.get(), nullptr, IID_PPV_ARGS(command_list.put())
);
```
呼び出し先が内部で保持する場合は呼び出し先が`AddRef()`するため、こちら側でカウントを操作する必要はない。

### `put()`によるオブジェクトの受け取り
DirectXの生成関数は`(REFIID riid, void** ppv)`という形でオブジェクトを返す。
```cpp
winrt::com_ptr<IDXGIFactory6> factory;
winrt::check_hresult(CreateDXGIFactory2(0, winrt::guid_of<IDXGIFactory6>(), factory.put_void()));
```
- `winrt::guid_of<T>()`と`put_void()`の組み合わせがC++/WinRTでの本来の書き方
- `IID_PPV_ARGS`マクロも使える
    ```cpp
    winrt::com_ptr<ID3D12Device> device;
    D3D12CreateDevice(adapter.get(), D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(device.put()));
    ```
    - `IID_PPV_ARGS`はGUIDと受け取り先のアドレスをまとめて展開するマクロで、GUIDを`__uuidof`から導く
    - 変数の型を変えるだけで正しいGUIDが選ばれるため、両者がずれるバグを防げる

### `as()`によるインターフェースの変換
同じオブジェクトの別バージョン・別インターフェースが欲しい場合は`QueryInterface`を使う。
com_ptrでは`as()`と`try_as()`がそのラッパーになっている。
```cpp
// CreateSwapChainForHwndはIDXGISwapChain1**しか受け取らないため、
// 一旦v1で受けてからas(=QueryInterface)でv4へ変換する.
winrt::com_ptr<IDXGISwapChain1> tmp_swapchain;
factory->CreateSwapChainForHwnd(..., tmp_swapchain.put());
auto swapchain = tmp_swapchain.as<IDXGISwapChain4>();
```
- `as<U>()`は失敗時に`winrt::hresult_error`を投げ、`try_as<U>()`は空のcom_ptrを返す
    - 対応していない可能性がある拡張インターフェースの問い合わせには`try_as`を使う
- 変換後も指しているオブジェクトは同一で、参照カウントが1増えた状態になる


## エラー処理
`winrt::check_hresult`
- 渡された`HRESULT`が失敗を示す場合に`winrt::hresult_error`を投げる
- `HRESULT`を戻り値のまま扱いたい箇所では使わず、`SUCCEEDED`で分岐すればよい
    - com_ptr自体が例外を強制しているわけではない

`winrt::hresult_error`
- `std::exception`を継承していないため、`catch (const std::exception&)`では捕捉できない
    ```cpp
    } catch (const winrt::hresult_error& e) {
        Printer{}.Error("fatal: " + winrt::to_string(e.message()));
        return 1;
    } catch (const std::exception& e) {
        ...
    ```
- `e.message()`は`winrt::hstring`（UTF-16）を返すため、`winrt::to_string`でUTF-8の`std::string`へ変換する
- `e.code()`で元の`HRESULT`を取得できる


## 参考
- [C++/WinRT で COM コンポーネントを使用する](https://learn.microsoft.com/ja-jp/windows/uwp/cpp-and-winrt-apis/consume-com)
- [winrt::com_ptr struct template](https://learn.microsoft.com/en-us/uwp/cpp-ref-for-winrt/com-ptr)
- [IUnknown interface](https://learn.microsoft.com/en-us/windows/win32/api/unknwn/nn-unknwn-iunknown)
- [IID_PPV_ARGS macro](https://learn.microsoft.com/en-us/windows/win32/api/combaseapi/nf-combaseapi-iid_ppv_args)
- [Windows Runtime C++ テンプレート ライブラリ (WRL)](https://learn.microsoft.com/ja-jp/cpp/cppcx/wrl/windows-runtime-cpp-template-library-wrl)
- [Direct3D 12 プログラミング ガイド](https://learn.microsoft.com/ja-jp/windows/win32/direct3d12/directx-12-programming-guide)
