# The CLR profiler: a shared library the .NET runtime loads in-process to report its own events
# (stop-the-world suspensions, garbage collections, JIT compilation) as Tracy zones.
# See Notes/Core/Profiling.md.

add_library(DivisionClrProfiler SHARED source/ClrProfiler.cpp)

target_include_directories(DivisionClrProfiler PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)

# The shared Tracy client, not a second copy of it: the runtime events have to land in the same
# capture as the engine's own zones, and two clients in one process would open two connections.
target_link_libraries(DivisionClrProfiler PRIVATE TracyClient)

set_target_properties(DivisionClrProfiler PROPERTIES
    # DllGetClassObject is the only symbol the runtime looks up; nothing else needs to be visible.
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON
    LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
    RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin
    # libDivisionTracy records itself as @rpath/..., and both libraries end up side by side in the
    # managed output directory, so the lookup has to be relative to this library rather than to the
    # process - the process is the dotnet host, somewhere else entirely.
    BUILD_RPATH "$<IF:$<PLATFORM_ID:Darwin>,@loader_path,$ORIGIN>"
    INSTALL_RPATH "$<IF:$<PLATFORM_ID:Darwin>,@loader_path,$ORIGIN>")

# Our own name for the target, matching tracy_client.
add_custom_target(clr_profiler)
add_dependencies(clr_profiler DivisionClrProfiler)
