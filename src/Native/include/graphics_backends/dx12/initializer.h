#pragma once

#include <Windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <winrt/base.h>


namespace graphics_backends::dx12 {

// DirectX 12の初期化と、描画に必要なデバイス群の保持を担う.
// 失敗はすべてwinrt::hresult_errorとして送出する.
class Initializer {
  public:
    Initializer() = default;
    ~Initializer() = default;

    // デバイス群の所有権を複製させないためコピーは禁止し、ムーブのみ許可する.
    Initializer(const Initializer&) = delete;
    Initializer& operator=(const Initializer&) = delete;
    Initializer(Initializer&&) noexcept = default;
    Initializer& operator=(Initializer&&) noexcept = default;

    // ウィンドウに紐付けてDirectX 12一式を初期化する.
    // スワップチェーンのサイズはウィンドウのクライアント領域から決めるため、
    // 幅と高さを外から渡す必要はない.
    void Initialize(HWND hwnd);

    // 積んだコマンドをGPUが消化し終えるまでCPUを待たせる.
    void WaitForGpu();

    [[nodiscard]] ID3D12Device* Device() const noexcept { return device_.get(); }
    [[nodiscard]] ID3D12CommandQueue* CommandQueue() const noexcept { return command_queue_.get(); }
    [[nodiscard]] ID3D12CommandAllocator* CommandAllocator() const noexcept { return command_allocator_.get(); }
    [[nodiscard]] ID3D12GraphicsCommandList* CommandList() const noexcept { return command_list_.get(); }
    [[nodiscard]] IDXGISwapChain4* SwapChain() const noexcept { return swapchain_.get(); }

    // 現在のバックバッファとそのRTVハンドルを返す.
    // 添字はPresentのたびに進むため、都度取得すること.
    [[nodiscard]] ID3D12Resource* CurrentBackBuffer() const;
    [[nodiscard]] D3D12_CPU_DESCRIPTOR_HANDLE CurrentRenderTargetView() const;

  private:
    // 裏表の2枚.
    static constexpr uint32_t BACK_BUFFER_COUNT = 2;

    void CreateFactoryAndAdapter();
    void CreateDevice();
    void CreateCommandObjects();
    void CreateFence();
    void CreateSwapChain(HWND hwnd);
    void CreateRenderTargetViews();

    winrt::com_ptr<IDXGIFactory6> factory_;
    winrt::com_ptr<IDXGIAdapter> adapter_;
    winrt::com_ptr<ID3D12Device> device_;

    winrt::com_ptr<ID3D12CommandAllocator> command_allocator_;
    winrt::com_ptr<ID3D12GraphicsCommandList> command_list_;
    winrt::com_ptr<ID3D12CommandQueue> command_queue_;

    winrt::com_ptr<ID3D12Fence> fence_;
    winrt::handle fence_event_;
    uint64_t fence_value_ = 0;

    winrt::com_ptr<IDXGISwapChain4> swapchain_;
    winrt::com_ptr<ID3D12DescriptorHeap> rtv_heap_;
    std::vector<winrt::com_ptr<ID3D12Resource>> back_buffers_;
    uint32_t rtv_descriptor_size_ = 0;
};

}  // namespace graphics_backends::dx12
