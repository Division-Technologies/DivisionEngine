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
if(WIN32)
    target_link_libraries(main PRIVATE d3d12 dxgi)
endif()


# ----- Tracy profiler client -----.
# Built as a shared library for the C# engine to P/Invoke; nothing in this C++ tree links against
# it. The sources are the packages/tracy submodule, built by upstream's own CMakeLists driven
# through its options, so the only build logic here is what upstream does not expose.
# See Notes/Core/Profiling.md.
option(DIVISION_BUILD_TRACY "Build the Tracy client shared library" ON)
set(DIVISION_TRACY_LIBRARY_NAME "DivisionTracy" CACHE STRING
    "Output name of the Tracy client library, as the C# binding loads it")

if(DIVISION_BUILD_TRACY)
  if(NOT EXISTS ${CMAKE_CURRENT_SOURCE_DIR}/packages/tracy/CMakeLists.txt)
    message(FATAL_ERROR
            "packages/tracy is empty. It is a git submodule:\n"
            "    git submodule update --init --depth 1")
  endif()

  # FORCE because these are not preferences: they are part of the ABI contract with
  # DivisionEngine/Profiling/TracyNative.cs. TRACY_ON_DEMAND in particular adds a field to
  # TracyCZoneCtx, and a mismatch neither fails to build nor fails to load - it silently
  # corrupts the capture. Upstream defaults TRACY_ENABLE to OFF and static to ON.
  set(TRACY_STATIC OFF CACHE BOOL "" FORCE)          # a shared library for P/Invoke
  set(TRACY_ENABLE ON CACHE BOOL "" FORCE)           # without it every entry point is empty
  set(TRACY_ON_DEMAND ON CACHE BOOL "" FORCE)        # record nothing until the UI connects
  set(TRACY_MANUAL_LIFETIME ON CACHE BOOL "" FORCE)  # the engine starts the profiler explicitly

  add_subdirectory(${CMAKE_CURRENT_SOURCE_DIR}/packages/tracy)

  # TRACY_DELAYED_INIT is still honoured by the sources but stopped being a CMake option in
  # v0.14, so it has to be set from here. It is not optional: TracyProfiler.cpp compiles
  # ___tracy_startup_profiler and friends only when both it and TRACY_MANUAL_LIFETIME are
  # defined, and the C# side needs them to bring the profiler up. It is also what makes a
  # dynamically loaded library safe, since the CLR loads us after its own static initializers.
  target_compile_definitions(TracyClient PUBLIC TRACY_DELAYED_INIT)

  set_target_properties(TracyClient PROPERTIES
      OUTPUT_NAME ${DIVISION_TRACY_LIBRARY_NAME}
      # One predictable place for every configuration, for the C# build to copy from.
      LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
      RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)

  # Upstream stamps the version onto the file name, which leaves the plain name as a symlink -
  # and the C# build copies the plain name, so it would copy the link rather than the library.
  # The property has to be removed, not set to an empty string: an empty VERSION still counts
  # as versioned and yields libDivisionTracy..dylib.
  set_property(TARGET TracyClient PROPERTY VERSION)
  set_property(TARGET TracyClient PROPERTY SOVERSION)

  # Third-party code we do not patch: keep its warnings from burying ours.
  if(MSVC)
    target_compile_options(TracyClient PRIVATE /w)
  else()
    target_compile_options(TracyClient PRIVATE -w)
  endif()

  # Our own name for the target, so presets and docs do not depend on upstream's.
  add_custom_target(tracy_client)
  add_dependencies(tracy_client TracyClient)
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
