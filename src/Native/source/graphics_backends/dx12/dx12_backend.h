#pragma once

#include <graphics_backends/backend.h>

#include <array>
#include <memory>

namespace graphics_backends::dx12 {

class Dx12Initializer;
class Dx12Renderer;

class Dx12Backend final : public graphics_backends::Backend {
  public:
    Dx12Backend();
    ~Dx12Backend() override;

    Dx12Backend(const Dx12Backend&) = delete;
    Dx12Backend& operator=(const Dx12Backend&) = delete;
    Dx12Backend(Dx12Backend&&) = delete;
    Dx12Backend& operator=(Dx12Backend&&) = delete;

    void Initialize(HWND hwnd) override;
    void RenderFrame() override;
    void SetClearColor(const std::array<float, 4>& color) override;
    void WaitForGpu() override;

  private:
    std::unique_ptr<Dx12Initializer> initializer_;
    std::unique_ptr<Dx12Renderer> renderer_;
};

}  // namespace graphics_backends::dx12
