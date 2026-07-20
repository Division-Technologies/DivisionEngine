# ネイティブレイヤー開発
[ネイティブレイヤー](https://github.com/Division-Technologies/DivisionEngine)の開発に関するドキュメントです。

- 開発環境について：[C++開発環境入門](./Introduction.md)
- 環境構築について：[C++開発環境構築](./Setup.md)

## デバッグビルド

```powershell
cmake --preset clang-debug
cmake --build --preset clang-debug
```

### 静的解析
```powershell
cmake --build --preset clang-debug --target tidy
```

### テスト
```powershell
ctest --preset clang-debug
```


## リリースビルド
```powershell
cmake --preset clang-release
cmake --build --preset clang-release
```

