#include <graphics_backends/dx12/initializer.h>

#include <iterator>
#include <string_view>
#include <utility>


namespace {
// この翻訳単位に閉じた無名名前空間
void EnableDebugLayer() {
#ifdef _DEBUG
    winrt::com_ptr<ID3D12Debug> debug_controller;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(debug_controller.put())))) {
        debug_controller->EnableDebugLayer();
    }
#endif
}
}  // namespace


namespace graphics_backends::dx12 {

void Initializer::Initialize(HWND hwnd) {
    EnableDebugLayer();

    CreateFactoryAndAdapter();
    CreateDevice();
    CreateCommandObjects();
    CreateFence();
    CreateSwapChain(hwnd);
    CreateRenderTargetViews();
}

void Initializer::WaitForGpu() {
    ++fence_value_;
    winrt::check_hresult(command_queue_->Signal(fence_.get(), fence_value_));

    if (fence_->GetCompletedValue() < fence_value_) {
        winrt::check_hresult(
            fence_->SetEventOnCompletion(fence_value_, fence_event_.get())
        );

        // Fenceが完了するまでCPUをブロック
        WaitForSingleObject(fence_event_.get(), INFINITE);
    }
}

ID3D12Resource* Initializer::CurrentBackBuffer() const {
    return back_buffers_.at(swapchain_->GetCurrentBackBufferIndex()).get();
}

D3D12_CPU_DESCRIPTOR_HANDLE Initializer::CurrentRenderTargetView() const {
    D3D12_CPU_DESCRIPTOR_HANDLE handle{
        rtv_heap_->GetCPUDescriptorHandleForHeapStart()
    };
    handle.ptr += static_cast<SIZE_T>(swapchain_->GetCurrentBackBufferIndex()) * rtv_descriptor_size_;

    return handle;
}

void Initializer::CreateFactoryAndAdapter() {
#ifdef _DEBUG
    constexpr UINT FACTORY_FLAGS = DXGI_CREATE_FACTORY_DEBUG;
#else
    constexpr UINT FACTORY_FLAGS = 0;
#endif
    winrt::check_hresult(CreateDXGIFactory2(FACTORY_FLAGS, IID_PPV_ARGS(factory_.put())));

    for (uint32_t i = 0;; ++i) {
        winrt::com_ptr<IDXGIAdapter> adapter;
        if (factory_->EnumAdapters(i, adapter.put()) == DXGI_ERROR_NOT_FOUND) {
            break;
        }

        DXGI_ADAPTER_DESC desc{};
        winrt::check_hresult(adapter->GetDesc(&desc));

        const std::wstring_view description{
            std::begin(desc.Description),
            std::end(desc.Description)
        };

        // NVIDIA製のアダプターがあれば優先して使う.
        // 見つからなければadapter_はnullptrのままとし、D3D12CreateDeviceの既定選択に委ねる.
        if (description.find(L"NVIDIA") != std::wstring_view::npos) {
            adapter_ = std::move(adapter);
            break;
        }
    }
}

void Initializer::CreateDevice() {
    winrt::check_hresult(D3D12CreateDevice(
        adapter_.get(), D3D_FEATURE_LEVEL_12_1, IID_PPV_ARGS(device_.put())
    ));
}

void Initializer::CreateCommandObjects() {
    winrt::check_hresult(device_->CreateCommandAllocator(
        D3D12_COMMAND_LIST_TYPE_DIRECT, IID_PPV_ARGS(command_allocator_.put())
    ));

    winrt::check_hresult(device_->CreateCommandList(
        0, D3D12_COMMAND_LIST_TYPE_DIRECT, command_allocator_.get(), nullptr,
        IID_PPV_ARGS(command_list_.put())
    ));

    D3D12_COMMAND_QUEUE_DESC desc{};
    desc.Flags = D3D12_COMMAND_QUEUE_FLAG_NONE;
    desc.Type = D3D12_COMMAND_LIST_TYPE_DIRECT;
    desc.NodeMask = 0;
    desc.Priority = D3D12_COMMAND_QUEUE_PRIORITY_NORMAL;

    winrt::check_hresult(
        device_->CreateCommandQueue(&desc, IID_PPV_ARGS(command_queue_.put()))
    );
}

void Initializer::CreateFence() {
    winrt::check_hresult(
        device_->CreateFence(0, D3D12_FENCE_FLAG_NONE, IID_PPV_ARGS(fence_.put()))
    );

    fence_event_.attach(CreateEventW(nullptr, FALSE, FALSE, nullptr));
    if (!fence_event_) {
        winrt::throw_last_error();
    }
}

void Initializer::CreateSwapChain(HWND hwnd) {
    RECT client_rect{};
    if (GetClientRect(hwnd, &client_rect) == 0) {
        winrt::throw_last_error();
    }

    DXGI_SWAP_CHAIN_DESC1 desc{};
    desc.Width = static_cast<UINT>(client_rect.right - client_rect.left);
    desc.Height = static_cast<UINT>(client_rect.bottom - client_rect.top);
    desc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
    desc.Stereo = FALSE;
    desc.SampleDesc.Count = 1;
    desc.SampleDesc.Quality = 0;
    desc.BufferUsage = DXGI_USAGE_BACK_BUFFER;
    desc.BufferCount = BACK_BUFFER_COUNT;
    desc.Scaling = DXGI_SCALING_STRETCH;
    desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
    desc.AlphaMode = DXGI_ALPHA_MODE_UNSPECIFIED;
    desc.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;

    // CreateSwapChainForHwndはIDXGISwapChain1**しか受け取らないため、
    // 一旦v1で受けてからas(=QueryInterface)でv4へ変換する.
    winrt::com_ptr<IDXGISwapChain1> swapchain;
    winrt::check_hresult(factory_->CreateSwapChainForHwnd(
        command_queue_.get(), hwnd, &desc, nullptr, nullptr, swapchain.put()
    ));

    swapchain_ = swapchain.as<IDXGISwapChain4>();
}

void Initializer::CreateRenderTargetViews() {
    D3D12_DESCRIPTOR_HEAP_DESC desc{};
    desc.Type = D3D12_DESCRIPTOR_HEAP_TYPE_RTV;
    desc.NodeMask = 0;
    desc.NumDescriptors = BACK_BUFFER_COUNT;
    desc.Flags = D3D12_DESCRIPTOR_HEAP_FLAG_NONE;

    winrt::check_hresult(
        device_->CreateDescriptorHeap(&desc, IID_PPV_ARGS(rtv_heap_.put()))
    );

    rtv_descriptor_size_ = device_->GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV);

    back_buffers_.resize(BACK_BUFFER_COUNT);

    auto rtv_handle = rtv_heap_->GetCPUDescriptorHandleForHeapStart();
    for (uint32_t i = 0; i < BACK_BUFFER_COUNT; ++i) {
        auto& back_buffer = back_buffers_.at(i);

        winrt::check_hresult(swapchain_->GetBuffer(i, IID_PPV_ARGS(back_buffer.put())));
        device_->CreateRenderTargetView(back_buffer.get(), nullptr, rtv_handle);

        rtv_handle.ptr += rtv_descriptor_size_;
    }
}

}  // namespace graphics_backends::dx12
