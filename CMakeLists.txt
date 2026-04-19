cmake_minimum_required(VERSION 4.3)
project(DivisionEngine CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED True)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# Collect source files without main.cpp.
set(SOURCE_DIR ${CMAKE_CURRENT_SOURCE_DIR}/source)
file(GLOB_RECURSE LIB_SOURCES CONFIGURE_DEPENDS
     "${SOURCE_DIR}/*.c*" "${SOURCE_DIR}/*.h*")
list(FILTER LIB_SOURCES EXCLUDE REGEX ".*/main\\.(c|cc|cpp|cxx)$")

# add library
add_library(core ${LIB_SOURCES})
target_include_directories(core
    PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/source
)

# executeable
add_executable(main ${SOURCE_DIR}/main.cpp)
target_link_libraries(main PRIVATE core)
