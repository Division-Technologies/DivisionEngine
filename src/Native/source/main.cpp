#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE  // NOLINT(bugprone-reserved-identifier,readability-identifier-naming) CRT側が要求する名前.
#endif

#include <Windows.h>
#include <graphics_backends/backend.h>
#include <graphics_backends/dx12/dx12_backend.h>
#include <winrt/base.h>

#include <exception>
#include <memory>
#include <string>


import Printer;

using graphics_backends::Backend;
namespace dx12 = graphics_backends::dx12;


LRESULT WindowProcedure(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_DESTROY) {
        // ウィンドウが破棄されたらOSに終了を通知する.
        PostQuitMessage(0);
        return 0;
    }

    return DefWindowProcW(hwnd, message, wparam, lparam);
}

int main() {
    try {
        WNDCLASSEXW w{};
        w.cbSize = sizeof(w);
        w.lpfnWndProc = WindowProcedure;  // コールバック関数を指定.
        w.lpszClassName = L"DivisionEngine";
        w.hInstance = GetModuleHandleW(nullptr);

        // OSにウィンドウクラスを登録する.
        RegisterClassExW(&w);

        const int wid = 1280;
        const int hei = 720;
        RECT wrc = {0, 0, wid, hei};
        AdjustWindowRect(&wrc, WS_OVERLAPPEDWINDOW, FALSE);

        HWND hwnd = CreateWindowExW(
            0,
            w.lpszClassName,
            L"DivisionEngine",
            WS_OVERLAPPEDWINDOW,
            CW_USEDEFAULT, CW_USEDEFAULT,
            wrc.right - wrc.left, wrc.bottom - wrc.top,
            nullptr, nullptr, w.hInstance, nullptr
        );
        if (hwnd == nullptr) {
            winrt::throw_last_error();
        }

        ///
        //  グラフィックスバックエンド
        //
        // バックエンドの選択はこの1行に閉じており、以降はBackend越しに扱う.
        std::unique_ptr<Backend> backend = std::make_unique<dx12::Dx12Backend>();
        backend->Initialize(hwnd);


        ShowWindow(hwnd, SW_SHOW);

        MSG msg{};
        while (true) {
            if (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                if (msg.message == WM_QUIT) {
                    break;
                }

                TranslateMessage(&msg);  // キーボード入力などを文字コードに変換する.
                DispatchMessageW(&msg);  // ウィンドウプロシージャにメッセージを送る.
            } else {
            }

            backend->RenderFrame();
        }

        // 破棄に入る前にGPU側の処理を終わらせる.
        backend->WaitForGpu();

        UnregisterClassW(w.lpszClassName, w.hInstance);

        return 0;
    } catch (const winrt::hresult_error& e) {
        // hresult_errorはstd::exceptionを継承していないため個別に受ける.
        try {
            Printer{}.Error("fatal: " + winrt::to_string(e.message()));
        } catch (...) {  // NOLINT(bugprone-empty-catch)
            // ログ出力自体の失敗は諦める(best-effort)
        }
        return 1;
    } catch (const std::exception& e) {
        try {
            Printer{}.Error("fatal: " + std::string(e.what()));
        } catch (...) {  // NOLINT(bugprone-empty-catch)
            // ログ出力自体の失敗は諦める(best-effort)
        }
        return 1;
    } catch (...) {
        try {
            Printer{}.Error("fatal: unknown exception");
        } catch (...) {  // NOLINT(bugprone-empty-catch)
            // ログ出力自体の失敗は諦める(best-effort)
        }
        return 1;
    }
}
