cmake_minimum_required(VERSION 4.3)
project(DivisionEngine CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED True)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Collect source files without main.cpp.
set(SOURCE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/source)
set(INCLUDE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/include)
file(GLOB_RECURSE LIB_SOURCES CONFIGURE_DEPENDS
     "${SOURCE_DIR}/*.cpp" "${SOURCE_DIR}/*.h*")
list(FILTER LIB_SOURCES EXCLUDE REGEX ".*/main.cpp$")

# Collect C++20 module interface units (.ixx).
file(GLOB_RECURSE LIB_MODULES CONFIGURE_DEPENDS
     "${SOURCE_DIR}/*.ixx" "${INCLUDE_DIR}/*.ixx")

# add library
add_library(core ${LIB_SOURCES})
target_sources(core
    PUBLIC
        FILE_SET cxx_modules TYPE CXX_MODULES FILES ${LIB_MODULES}
)
target_include_directories(core
    PUBLIC  ${INCLUDE_DIR}
    PRIVATE ${SOURCE_DIR}
)

# executeable
add_executable(main ${SOURCE_DIR}/main.cpp)
set_target_properties(core PROPERTIES LINKER_LANGUAGE CXX)
target_link_libraries(main PRIVATE core)


# Platform-specific graphics backend libraries.
# Windows: Direct3D 12 / DXGI. Linked here instead of via #pragma comment(lib)
# in source so that non-Windows builds (macOS/Linux) stay clean.
#
# oleaut32/ole32/runtimeobject are required by C++/WinRT (winrt::com_ptr):
# winrt::hresult_error pulls in GetErrorInfo, SysStringLen/SysFreeString and
# RoOriginateLanguageException for its error information.
#
# D3D12を呼ぶコードはcore側(graphics_backends/dx12)にあるため、coreに紐付ける。
# PUBLICにすることでcoreをリンクするmainやテストへ伝播する。
if(WIN32)
    target_link_libraries(core PUBLIC d3d12 dxgi oleaut32 ole32 runtimeobject)
endif()


# ----- Enable clang-tidy if available -----.
find_program(CLANG_TIDY_EXE NAMES "clang-tidy")
if(CLANG_TIDY_EXE)
  add_custom_target(tidy
    COMMAND ${CLANG_TIDY_EXE} --use-color -p ${CMAKE_BINARY_DIR} ${LIB_SOURCES} ${SOURCE_DIR}/main.cpp
    WORKING_DIRECTORY ${CMAKE_SOURCE_DIR}
    COMMENT "Running clang-tidy..."
    VERBATIM
  )
else()
  add_custom_target(tidy
    COMMAND ${CMAKE_COMMAND} -E echo "clang-tidy not found."
  )
endif()


# ----- Test module -----.
option(BUILD_TESTS "BUILD_TESTS" ON)
if (BUILD_TESTS)
  enable_testing()
  add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/packages/googletest)
  add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/test)
endif()
