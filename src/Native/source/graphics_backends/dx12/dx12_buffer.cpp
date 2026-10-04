#include <graphics_backends/dx12/dx12_buffer.h>

#include <cstring>
#include <stdexcept>


namespace graphics_backends::dx12 {

void Dx12Buffer::Write(std::span<const std::byte> data, uint64_t offset) {
    if (offset > size_in_bytes_ || data.size() > size_in_bytes_ - offset) {
        throw std::out_of_range("Dx12Buffer::Write: range exceeds buffer size");
    }
    if (data.empty()) {
        return;
    }

    // 読み戻さないのでBegin == Endの空レンジを渡し、CPUキャッシュの無効化を省く.
    const D3D12_RANGE read_range{0, 0};
    void* mapped = nullptr;
    winrt::check_hresult(resource_->Map(0, &read_range, &mapped));

    // NOLINTNEXTLINE(cppcoreguidelines-pro-bounds-pointer-arithmetic)
    std::memcpy(static_cast<std::byte*>(mapped) + offset, data.data(), data.size());

    // 書き込んだ範囲を伝える. nullptrだと全域を書いた扱いになる.
    const D3D12_RANGE written_range{
        static_cast<SIZE_T>(offset),
        static_cast<SIZE_T>(offset + data.size())
    };
    resource_->Unmap(0, &written_range);
}

}  // namespace graphics_backends::dx12
