#define UNICODE
#define _UNICODE

#include <Windows.h>
#include <tchar.h>

#include <exception>
#include <string>

import Printer;


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
        WNDCLASSEX w{};
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
        }

        UnregisterClassW(w.lpszClassName, w.hInstance);

        return 0;
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
