# スマートポインタとRAII
C++で資源の解放漏れを防ぐための仕組みについてまとめる。
COMオブジェクト専用のスマートポインタについては[com_ptr](ComPtr.md)を参照。


## RAII
RAII（Resource Acquisition Is Initialization）
- 資源の確保をオブジェクトの初期化に、解放をデストラクタに紐づける設計
- C++ではスコープを抜けるときにデストラクタが必ず呼ばれる
    - 早期return、例外によるスタックの巻き戻し（スタックアンワインド）でも呼ばれる
    - そのため「解放処理を書き忘れる」「例外で解放を飛ばす」が構造的に起こらなくなる
- 対象はメモリに限らず、ファイル、ソケット、ロック、GPUリソースなど確保と解放が対になるものすべてに適用できる
    - 標準ライブラリの多くがRAIIで作られている（`std::vector`：メモリ、`std::fstream`：ファイル、`std::lock_guard`：ミューテックス）

```cpp
void manual() {
    auto* p = new Foo();
    DoSomething();  // ここで例外が飛ぶとdeleteに到達せずリークする.
    delete p;
}

void raii() {
    auto p = std::make_unique<Foo>();
    DoSomething();  // 例外が飛んでもpのデストラクタが呼ばれる.
}
```


## 生ポインタの問題
- 解放忘れ（リーク）、二重解放、解放後の参照（ダングリングポインタ）が起きる
- 所有権が型に現れない
    - `Foo* GetFoo()`の戻り値を呼び出し側が解放すべきかどうか、シグネチャからは判断できない
    - 結果としてドキュメントやコメントに頼ることになる
- スマートポインタは所有権を型として表現するため、この曖昧さがなくなる
    - `std::unique_ptr<Foo>`を返す関数は所有権を渡している、`const Foo&`を取る関数は借りているだけ、と読める


## 標準のスマートポインタ
`std::unique_ptr<T>`
- 単独所有を表す。コピーできず、ムーブでのみ所有権を移動する
- サイズは生ポインタと同じで、実行時コストは基本的にゼロ
- カスタムデリータを指定でき、`delete`以外で解放する資源にも使える
    ```cpp
    // HANDLEのようにdeleteで解放できない資源をRAII化する.
    struct HandleDeleter {
        using pointer = HANDLE;
        void operator()(HANDLE h) const noexcept { CloseHandle(h); }
    };
    using UniqueHandle = std::unique_ptr<HANDLE, HandleDeleter>;
    ```
    - デリータに`pointer`を定義すると、`T*`ではなくその型を保持するようになる
    - ただし`INVALID_HANDLE_VALUE`は`nullptr`ではないため、無効値の扱いは別途考える必要がある

`std::shared_ptr<T>`
- 共有所有を表す。参照カウントが0になった時点で解放される
- カウンタはオブジェクト本体とは別の制御ブロック（control block）に置かれる
    - `std::make_shared`を使うと本体と制御ブロックをまとめて1回で確保できる
- コストがある
    - 制御ブロックの確保、カウント操作がアトミック、サイズはポインタ2つ分
- 同じ生ポインタから2つの`shared_ptr`を作ると制御ブロックが2つでき、二重解放になる
    - 共有したい場合は必ず`shared_ptr`同士でコピーする

`std::weak_ptr<T>`
- 所有権を持たない参照。`lock()`で`shared_ptr`へ昇格する
- `shared_ptr`同士が相互に参照するとカウントが0にならずリークするため、その循環を断ち切るために使う

使い分け
- 既定は`unique_ptr`とし、共有が本当に必要な場合のみ`shared_ptr`を使う
- 所有権を持たない参照（借用）には生ポインタや参照をそのまま使ってよい
    - 関数の引数をスマートポインタにするのは、所有権を移す場合や共有する場合に限る


## 参照カウントの方式
非侵入型
- `shared_ptr`のように、カウンタをオブジェクトの外（制御ブロック）に置く方式
- 任意の型に後付けできる一方、生ポインタとカウンタの対応が失われやすい
    - 前述の「同じ生ポインタから2つ作ると二重解放」はこれが原因

侵入型
- カウンタをオブジェクト自身が持つ方式
- COMの`IUnknown`（`AddRef` / `Release`）がこれにあたる
    - 生ポインタからいつでも正しくカウントを操作できるため、[com_ptr](ComPtr.md)は同じオブジェクトから何度作り直しても整合する
- 型の側が対応している必要がある


## Windows APIとの組み合わせ
- `HANDLE`、`HWND`、`HMODULE`などは`delete`で解放できないため、`unique_ptr`のカスタムデリータか専用のRAIIラッパーを使う
- COMオブジェクトは自身が参照カウントを持つため、専用の[com_ptr](ComPtr.md)を使う
- WILの`wil::unique_handle`のように、既製のRAIIラッパーを提供するライブラリもある


## 参考
- [RAII — cppreference](https://en.cppreference.com/w/cpp/language/raii)
- [std::unique_ptr — cppreference](https://en.cppreference.com/w/cpp/memory/unique_ptr)
- [std::shared_ptr — cppreference](https://en.cppreference.com/w/cpp/memory/shared_ptr)
- [C++ Core Guidelines: Resource management](https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#S-resource)
