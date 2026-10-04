#pragma once

#include <graphics_backends/buffer.h>

#include <Windows.h>
#include <d3d12.h>
#include <winrt/base.h>

#include <cstddef>
#include <cstdint>
#include <span>
#include <utility>


namespace graphics_backends::dx12 {

// アップロードヒープ上のID3D12Resourceを包むバッファ.
class Dx12Buffer final : public graphics_backends::Buffer {
  public:
    Dx12Buffer(winrt::com_ptr<ID3D12Resource> resource, uint64_t size_in_bytes) noexcept
        : resource_(std::move(resource)), size_in_bytes_(size_in_bytes) {}
    ~Dx12Buffer() override = default;

    Dx12Buffer(const Dx12Buffer&) = delete;
    Dx12Buffer& operator=(const Dx12Buffer&) = delete;
    Dx12Buffer(Dx12Buffer&&) = delete;
    Dx12Buffer& operator=(Dx12Buffer&&) = delete;

    [[nodiscard]] uint64_t SizeInBytes() const noexcept override { return size_in_bytes_; }
    void Write(std::span<const std::byte> data, uint64_t offset) override;

    // DX12固有コード(レンダラなど)がビューの作成やバインドに使う.
    [[nodiscard]] ID3D12Resource* Resource() const noexcept { return resource_.get(); }
    [[nodiscard]] D3D12_GPU_VIRTUAL_ADDRESS GpuVirtualAddress() const {
        return resource_->GetGPUVirtualAddress();
    }

  private:
    winrt::com_ptr<ID3D12Resource> resource_;
    uint64_t size_in_bytes_ = 0;
};

}  // namespace graphics_backends::dx12
