#pragma once

#include <Windows.h>

#include <array>


namespace graphics_backends {
class Backend {
  public:
    Backend() = default;
    virtual ~Backend() = default;

    // 複製と移動は禁止
    Backend(const Backend&) = delete;
    Backend& operator=(const Backend&) = delete;
    Backend(Backend&&) = delete;
    Backend& operator=(Backend&&) = delete;

    virtual void Initialize(HWND hwnd) = 0;
    virtual void RenderFrame() = 0;
    virtual void SetClearColor(const std::array<float, 4>& color) = 0;
    virtual void WaitForGpu() = 0;
};

}  // namespace graphics_backends
