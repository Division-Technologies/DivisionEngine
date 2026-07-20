# C++開発環境構築
この記事では、C++の開発環境を構築するための基本的な手順を説明する。

事前に読む：[C++開発環境入門](./Introduction.md)


## Windows
`winget` を使って必要なツールをインストールします。
```powershell
winget install LLVM.LLVM # install LLVM, LLD, clang, clangd, clang-format, clang-tidy
winget install Kitware.CMake
winget install Ninja-build.Ninja
winget install Cppcheck.Cppcheck

winget install Microsoft.VisualStudio.BuildTools --override "--wait --passive --config ./buildtools.vsconfig"

Add-WindowsCapability -Online -Name Tools.Graphics.DirectX # install Graphics Tools for Debugging
winget install Microsoft.Sysinternals.DebugView
```

Windows環境変数の`Path`にLLVMのパスを追加する：`C:\Program Files\LLVM\bin\`



### Editor
[Visual Studio Code](https://code.visualstudio.com/) をインストールし、C++開発に必要な拡張機能を追加します。
- [Clangd (LLVM)](https://marketplace.visualstudio.com/items?itemName=llvm-vs-code-extensions.vscode-clangd)
- [CMake Tools (Microsoft)](https://marketplace.visualstudio.com/items?itemName=ms-vscode.cmake-tools)
- [C++ Tools (Microsoft)](https://marketplace.visualstudio.com/items?itemName=ms-vscode.cpptools)

src/Native/.vscode/extensions.jsonに必要な拡張機能が記載されているので、起動時にインストールを促されるはずです。
