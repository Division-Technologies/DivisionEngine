#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE  // NOLINT(bugprone-reserved-identifier,readability-identifier-naming) CRT側が要求する名前.
#endif

#include <Windows.h>
#include <d3d12.h>
#include <graphics_backends/dx12/initializer.h>
#include <winrt/base.h>

#include <array>
#include <exception>
#include <string>


import Printer;

using graphics_backends::dx12::Initializer;


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
        //  DirectX 12
        //
        Initializer initializer;
        initializer.Initialize(hwnd);

        auto* command_list = initializer.CommandList();
        auto* command_allocator = initializer.CommandAllocator();


        ShowWindow(hwnd, SW_SHOW);

        MSG msg{};
        while (true) {
            if (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                if (msg.message == WM_QUIT) {
                    break;
                }

                TranslateMessage(&msg);  // キーボード入力などを文字コードに変換する.
                DispatchMessageW(&msg);  // ウィンドウプロシージャにメッセージを送る.

                // バックバッファをPRESENTからRENDER_TARGETへ遷移させる.
                D3D12_RESOURCE_BARRIER barrier_desc{};
                barrier_desc.Type = D3D12_RESOURCE_BARRIER_TYPE_TRANSITION;
                barrier_desc.Flags = D3D12_RESOURCE_BARRIER_FLAG_NONE;
                // Transitionは共用体メンバだが、D3D12のAPI仕様上ここを埋める以外にない.
                // NOLINTBEGIN(cppcoreguidelines-pro-type-union-access)
                barrier_desc.Transition.pResource = initializer.CurrentBackBuffer();
                barrier_desc.Transition.Subresource = 0;
                barrier_desc.Transition.StateBefore = D3D12_RESOURCE_STATE_PRESENT;
                barrier_desc.Transition.StateAfter = D3D12_RESOURCE_STATE_RENDER_TARGET;
                // NOLINTEND(cppcoreguidelines-pro-type-union-access)

                command_list->ResourceBarrier(1, &barrier_desc);

                const auto rtv_handle = initializer.CurrentRenderTargetView();
                command_list->OMSetRenderTargets(1, &rtv_handle, true, nullptr);

                const std::array<float, 4> clear_color{1.0f, 1.0f, 1.0f, 1.0f};
                command_list->ClearRenderTargetView(rtv_handle, clear_color.data(), 0, nullptr);

                // Presentできる状態へ戻す.
                // NOLINTBEGIN(cppcoreguidelines-pro-type-union-access)
                barrier_desc.Transition.StateBefore = D3D12_RESOURCE_STATE_RENDER_TARGET;
                barrier_desc.Transition.StateAfter = D3D12_RESOURCE_STATE_PRESENT;
                // NOLINTEND(cppcoreguidelines-pro-type-union-access)
                command_list->ResourceBarrier(1, &barrier_desc);

                command_list->Close();
                const std::array<ID3D12CommandList*, 1> command_lists{command_list};
                initializer.CommandQueue()->ExecuteCommandLists(
                    static_cast<UINT>(command_lists.size()), command_lists.data()
                );

                // アロケータを再利用する前にGPUの完了を待つ.
                initializer.WaitForGpu();

                command_allocator->Reset();
                command_list->Reset(command_allocator, nullptr);

                initializer.SwapChain()->Present(1, 0);
            } else {
            }
        }

        // 破棄に入る前にGPU側の処理を終わらせる.
        initializer.WaitForGpu();

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
