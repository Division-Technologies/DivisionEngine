#pragma once

#include <cstddef>
#include <cstdint>
#include <span>


namespace graphics_backends {

// バッファの生成情報.
struct BufferDesc {
    uint64_t size_in_bytes = 0;
};

// CPUから書き込めるGPUバッファ.
// 具象型(Dx12Bufferなど)はグラフィックスAPI固有のリソースを内側に閉じ込め、
// 共通コードはこのインターフェース越しにだけ扱う.
class Buffer {
  public:
    Buffer() = default;
    virtual ~Buffer() = default;

    Buffer(const Buffer&) = delete;
    Buffer& operator=(const Buffer&) = delete;
    Buffer(Buffer&&) = delete;
    Buffer& operator=(Buffer&&) = delete;

    [[nodiscard]] virtual uint64_t SizeInBytes() const noexcept = 0;

    // CPU側のデータをoffsetバイト目から書き込む.
    // offset + data.size()がSizeInBytes()を超える場合はstd::out_of_rangeを投げる.
    virtual void Write(std::span<const std::byte> data, uint64_t offset = 0) = 0;
};

}  // namespace graphics_backends
