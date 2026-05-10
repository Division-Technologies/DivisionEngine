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
list(FILTER LIB_SOURCES EXCLUDE REGEX ".*/main.cpp$")

# add library
add_library(core ${LIB_SOURCES})
target_include_directories(core
    PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/source
)

# executeable
add_executable(main ${SOURCE_DIR}/main.cpp)
set_target_properties(core PROPERTIES LINKER_LANGUAGE CXX)
target_link_libraries(main PRIVATE core)


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
