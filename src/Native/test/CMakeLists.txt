set(TEST_DIR ${CMAKE_CURRENT_SOURCE_DIR})
file(GLOB_RECURSE TEST_SOURCES CONFIGURE_DEPENDS "${TEST_DIR}/*.c*")

add_executable(test_main ${TEST_SOURCES})
target_link_libraries(test_main PRIVATE core GTest::gtest_main)
target_include_directories(test_main PRIVATE ${CMAKE_SOURCE_DIR}/source)

include(GoogleTest)
gtest_discover_tests(test_main)
