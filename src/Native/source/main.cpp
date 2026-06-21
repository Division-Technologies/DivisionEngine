#include <cstdint>
#include <iterator>
#include <string_view>
#include <vector>
#define UNICODE
#define _UNICODE

#include <Windows.h>
#include <tchar.h>

#include <exception>
#include <string>

#include <d3d12.h>
#include <dxgi1_6.h>
#include <wrl/client.h>

import Printer;

using Microsoft::WRL::ComPtr;


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


        ///
        //  DirectX 12
        // 
        ComPtr<IDXGIFactory6> factory;
        ComPtr<IDXGIAdapter> dxgi_adapter;
        if(CreateDXGIFactory2(0, IID_PPV_ARGS(&factory)) == S_OK){
            // アダプターを列挙.
            uint32_t i = 0;
            std::vector<ComPtr<IDXGIAdapter>> adapters;
            ComPtr<IDXGIAdapter> adapter;
            
            while(factory->EnumAdapters(i, adapter.ReleaseAndGetAddressOf()) != DXGI_ERROR_NOT_FOUND) 
            {
                adapters.push_back(adapter); 
                ++i;
            }

            DXGI_ADAPTER_DESC desc{};
            for(const auto& apt : adapters){
                apt->GetDesc(&desc);
                const std::wstring_view wdesc{
                    std::begin(desc.Description),
                    std::end(desc.Description)
                };

                if(wdesc.find(L"NVIDIA") != std::wstring_view::npos){
                    dxgi_adapter = apt;
                    break;
                }
            }
        }


        ComPtr<ID3D12Device> device;
        D3D12CreateDevice(
            dxgi_adapter.Get(), D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(&device)
        );

        ComPtr<IDXGISwapChain4> swapchain;

        // コマンドリスト.
        ComPtr<ID3D12CommandAllocator> command_allocator;
        device->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(&command_allocator)
        );

        ComPtr<ID3D12GraphicsCommandList> command_list;
        device->CreateCommandList(
            0, D3D12_COMMAND_LIST_TYPE_DIRECT, command_allocator.Get(), nullptr, IID_PPV_ARGS(&command_list)
        );

        ComPtr<ID3D12CommandQueue> command_queue;
        D3D12_COMMAND_QUEUE_DESC command_queue_desc{};
        command_queue_desc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
        command_queue_desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
        command_queue_desc.NodeMask = 0;
        command_queue_desc.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;
        device->CreateCommandQueue(&command_queue_desc, IID_PPV_ARGS(&command_queue));

        // スワップチェーン.
        DXGI_SWAP_CHAIN_DESC1 swapchain_desc{};
        swapchain_desc.Width = wid;
        swapchain_desc.Height = hei;
        swapchain_desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        swapchain_desc.Stereo = FALSE;
        swapchain_desc.SampleDesc.Count = 1;
        swapchain_desc.SampleDesc.Quality = 0;
        swapchain_desc.BufferUsage = DXGI_USAGE_BACK_BUFFER;
        swapchain_desc.BufferCount = 2;
        swapchain_desc.Scaling = DXGI_SCALING_STRETCH;
        swapchain_desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        swapchain_desc.AlphaMode = DXGI_ALPHA_MODE_UNSPECIFIED;
        swapchain_desc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;

        // CreateSwapChainForHwndはIDXGISwapChain1**しか受け取らないため、
        // 一旦v1で受けてからAs(=QueryInterface)でv4へ変換する.
        ComPtr<IDXGISwapChain1> tmp_swapchain;
        const auto result = factory->CreateSwapChainForHwnd(
            command_queue.Get(), hwnd, &swapchain_desc, nullptr, nullptr, &tmp_swapchain
        );
        if (SUCCEEDED(result)) {
            tmp_swapchain.As(&swapchain);
        }

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
