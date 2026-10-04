#pragma once

#include <graphics_backends/dx12/dx12_initializer.h>

#include <array>

namespace graphics_backends::dx12 {

class Dx12Renderer {
  public:
    explicit Dx12Renderer(Dx12Initializer& initializer) noexcept : initializer_(&initializer) {}
    ~Dx12Renderer() = default;

    Dx12Renderer(const Dx12Renderer&) = delete;
    Dx12Renderer& operator=(const Dx12Renderer&) = delete;
    Dx12Renderer(Dx12Renderer&&) noexcept = default;
    Dx12Renderer& operator=(Dx12Renderer&&) noexcept = default;

    void Render();
    void SetClearColor(const std::array<float, 4>& color) noexcept { clear_color_ = color; }
    const std::array<float, 4>& ClearColor() const noexcept { return clear_color_; }

  private:
    void TransitionBackBuffer(D3D12_RESOURCE_STATES before, D3D12_RESOURCE_STATES after);

    Dx12Initializer* initializer_;
    std::array<float, 4> clear_color_{1.0f, 1.0f, 1.0f, 1.0f};
};

}  // namespace graphics_backends::dx12
