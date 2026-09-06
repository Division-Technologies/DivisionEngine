# 前方宣言
ヘッダの`#include`が連鎖するとビルドが遅くなる理由と、前方宣言でそれを断つ方法をまとめる。


## ヘッダとコスト
- C++のインクルードはテキストの展開であり、翻訳単位ごとに毎回やり直される
    - `A.h`が`B.h`を含み、`B.h`が`C.h`を含む場合、`A.h`を読む全ての`.cpp`が`C.h`まで解析する
    - プリコンパイル済みヘッダやモジュールを使わない限り、この解析はファイル数だけ繰り返される
    - ヘッダを1行変更すると、それを含む全ての翻訳単位が再コンパイルされる


## 前方宣言とは
型や関数の名前だけを先に宣言し、定義を伴わないものを前方宣言（forward declaration）とよぶ。

```cpp
// クラスの前方宣言.
// Fooという名前のクラスがあることだけを宣言する
class Foo;

// 関数の前方宣言
// Fooはポインタなので定義が無くても宣言できる（型に依らずポインタの大きさは決まっている）
void Bar(Foo* p);
```

コンパイラはこの時点で`Foo`は型であることだけを知り、大きさもメンバも知らない。
定義は、完全な型が必要になる翻訳単位にだけ含めればよく、ポインタや参照しか使わない翻訳単位には無くても構わない。


## 不完全型
宣言だけがあり定義が与えられていない型を不完全型（incomplete type）とよぶ。
```cpp
// 前方宣言
// この時点でFooは不完全型
class Foo;
```

不完全型でもできること
- ポインタ・参照の宣言（`Foo*`、`Foo&`）
- 関数の宣言（引数や戻り値に使う）

不完全型ではできないこと
- 実体の定義（`Foo foo;`）
- `sizeof`を取る
- メンバへのアクセス
- 生成と破棄（`new`、`delete`、デストラクタ呼び出し）


## ヘッダと前方宣言
利用側がポインタ経由でしか触らないなら、ヘッダでは前方宣言に留められる。
型定義が必要な場所を`.cpp`ひとつに閉じ込める、というのが要点になる。

```cpp
// backend.h
class Initializer;  // 前方宣言のみ

class Backend {
  public:
    void Initialize();

  private:
    Initializer* initializer_;
};
```

```cpp
// backend.cpp
#include "backend.h"
#include "initializer.h"  // ここで初めて完全型になる

void Backend::Initialize() {
    initializer_->Initialize();  // メンバへのアクセスには完全型が要る
}
```

インクルードの連鎖が、前方宣言を置いた地点で切れる。
```
適用前：main.cpp → backend.h → initializer.h → d3d12.h, dxgi1_6.h, winrt/base.h → <chrono>, <format>, ...
適用後：main.cpp → backend.h                                             （initializer.hより先を読まない）
                   backend.cpp → initializer.h → d3d12.h, ...           （ここだけが読む）
```

- `backend.h`を読む全ての翻訳単位が、`initializer.h`より先を解析しなくなる
- `initializer.h`以降を解析するのは`backend.cpp`だけになる
- 展開後の行数が減るため、それに比例してコンパイル時間も減る
- `initializer.h`を変更しても、`backend.h`を読む翻訳単位は再コンパイルされなくなる


## 参考
- [Incomplete type — cppreference](https://en.cppreference.com/w/cpp/language/type#Incomplete_type)
- [C++ Core Guidelines: C.9 Minimize exposure of members](https://isocpp.github.io/CppCoreGuidelines/CppCoreGuidelines#Rc-private)
