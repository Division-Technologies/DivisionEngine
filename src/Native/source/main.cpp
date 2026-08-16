#ifndef UNICODE
#define UNICODE
#endif
#ifndef _UNICODE
#define _UNICODE
#endif

#include <Windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <winrt/base.h>

#include <array>
#include <cstdint>
#include <exception>
#include <iterator>
#include <string>
#include <string_view>
#include <utility>
#include <vector>


import Printer;

using winrt::com_ptr;


LRESULT WindowProcedure(HWND hwnd, UINT message, WPARAM wparam, LPARAM lparam) {
    if (message == WM_DESTROY) {
        // ウィンドウが破棄されたらOSに終了を通知する.
        PostQuitMessage(0);
        return 0;
    }

    return DefWindowProcW(hwnd, message, wparam, lparam);
}

void EnableDebugLayer() {
    com_ptr<ID3D12Debug> debug_controller;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(debug_controller.put())))) {
        debug_controller->EnableDebugLayer();
    }
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

#ifdef _DEBUG
        EnableDebugLayer();
#endif

        ///
        //  DirectX 12
        //
        com_ptr<IDXGIFactory6> factory;
        com_ptr<IDXGIAdapter> dxgi_adapter;
        if (CreateDXGIFactory2(DXGI_CREATE_FACTORY_DEBUG, IID_PPV_ARGS(factory.put())) == S_OK) {
            // アダプターを列挙.
            // com_ptr::putは空であることを前提とするため、毎回新しい変数で受ける.
            std::vector<com_ptr<IDXGIAdapter>> adapters;
            for (uint32_t i = 0;; ++i) {
                com_ptr<IDXGIAdapter> adapter;
                if (factory->EnumAdapters(i, adapter.put()) == DXGI_ERROR_NOT_FOUND) {
                    break;
                }

                adapters.push_back(std::move(adapter));
            }

            DXGI_ADAPTER_DESC desc{};
            for (const auto& apt : adapters) {
                apt->GetDesc(&desc);
                const std::wstring_view wdesc{
                    std::begin(desc.Description),
                    std::end(desc.Description)
                };

                if (wdesc.find(L"NVIDIA") != std::wstring_view::npos) {
                    dxgi_adapter = apt;
                    break;
                }
            }
        }

        com_ptr<ID3D12Device> device;
        D3D12CreateDevice(
            dxgi_adapter.get(), D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(device.put())
        );

        com_ptr<IDXGISwapChain4> swapchain;

        // コマンドリスト.
        com_ptr<ID3D12CommandAllocator> command_allocator;
        device->CreateCommandAllocator(
            D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(command_allocator.put())
        );

        com_ptr<ID3D12GraphicsCommandList> command_list;
        device->CreateCommandList(
            0, D3D12_COMMAND_LIST_TYPE_DIRECT, command_allocator.get(), nullptr, IID_PPV_ARGS(command_list.put())
        );

        com_ptr<ID3D12CommandQueue> command_queue;
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
        // 一旦v1で受けてからas(=QueryInterface)でv4へ変換する.
        com_ptr<IDXGISwapChain1> tmp_swapchain;
        const auto result = factory->CreateSwapChainForHwnd(
            command_queue.get(), hwnd, &swapchain_desc, nullptr, nullptr, tmp_swapchain.put()
        );
        if (SUCCEEDED(result)) {
            // 変換に失敗した場合はhresult_errorを投げる.
            swapchain = tmp_swapchain.as<IDXGISwapChain4>();
        }

        D3D12_DESCRIPTOR_HEAP_DESC heap_desc{};
        heap_desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
        heap_desc.NodeMask = 0;
        heap_desc.NumDescriptors = 2;  // 裏表
        heap_desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

        com_ptr<ID3D12DescriptorHeap> rtv_heap;
        device->CreateDescriptorHeap(&heap_desc, IID_PPV_ARGS(rtv_heap.put()));

        // レンダーターゲットビュー(RTV)の作成.
        std::vector<com_ptr<ID3D12Resource>> back_buffers(2);
        for (int i = 0; i < 2; ++i) {
            swapchain->GetBuffer(i, IID_PPV_ARGS(back_buffers[i].put()));

            D3D12_CPU_DESCRIPTOR_HANDLE rtv_handle{
                rtv_heap->GetCPUDescriptorHandleForHeapStart()
            };
            rtv_handle.ptr += static_cast<SIZE_T>(i) * device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

            device->CreateRenderTargetView(back_buffers[i].get(), nullptr, rtv_handle);
        }


        ShowWindow(hwnd, SW_SHOW);

        MSG msg{};
        while (true) {
            if (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                if (msg.message == WM_QUIT) {
                    break;
                }

                TranslateMessage(&msg);  // キーボード入力などを文字コードに変換する.
                DispatchMessageW(&msg);  // ウィンドウプロシージャにメッセージを送る.


                const auto bb_idx = swapchain->GetCurrentBackBufferIndex();
                auto rtv_handle = rtv_heap->GetCPUDescriptorHandleForHeapStart();
                rtv_handle.ptr += static_cast<SIZE_T>(bb_idx) * device->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);
                command_list->OMSetRenderTargets(1, &rtv_handle, true, nullptr);

                const std::array<float, 4> clear_color{1.0f, 1.0f, 1.0f, 1.0f};
                command_list->ClearRenderTargetView(rtv_handle, clear_color.data(), 0, nullptr);

                command_list->Close();
                const std::array<ID3D12CommandList*, 1> command_lists{command_list.get()};
                command_queue->ExecuteCommandLists(
                    static_cast<UINT>(command_lists.size()), command_lists.data()
                );

                command_allocator->Reset();
                command_list->Reset(command_allocator.get(), nullptr);

                swapchain->Present(1, 0);
            } else {
            }
        }

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
