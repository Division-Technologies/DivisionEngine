#pragma once

#include <Windows.h>
#include <d3d12.h>
#include <dxgi1_6.h>
#include <winrt/base.h>


namespace graphics_backends::dx12 {

class Dx12Initializer {
  public:
    Dx12Initializer() = default;
    ~Dx12Initializer() = default;

    Dx12Initializer(const Dx12Initializer&) = delete;
    Dx12Initializer& operator=(const Dx12Initializer&) = delete;
    Dx12Initializer(Dx12Initializer&&) noexcept = default;
    Dx12Initializer& operator=(Dx12Initializer&&) noexcept = default;

    void Initialize(HWND hwnd);
    void WaitForGpu();
    [[nodiscard]] ID3D12Device* Device() const noexcept { return device_.get(); }
    [[nodiscard]] ID3D12CommandQueue* CommandQueue() const noexcept { return command_queue_.get(); }
    [[nodiscard]] ID3D12CommandAllocator* CommandAllocator() const noexcept { return command_allocator_.get(); }
    [[nodiscard]] ID3D12GraphicsCommandList* CommandList() const noexcept { return command_list_.get(); }
    [[nodiscard]] IDXGISwapChain4* SwapChain() const noexcept { return swapchain_.get(); }
    [[nodiscard]] ID3D12Resource* CurrentBackBuffer() const;
    [[nodiscard]] D3D12_CPU_DESCRIPTOR_HANDLE CurrentRenderTargetView() const;

  private:
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
