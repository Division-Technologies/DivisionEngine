#include <gtest/gtest.h>

#include "include_test.hpp"
#include "test.hpp"

TEST(SmokeTest, BasicTypesCanBeConstructedAndCalled) {
    division_engine::Hoge hoge;
    ::Test test;

    EXPECT_EQ(hoge.Test(), 0);
    EXPECT_EQ(test.Hoge(), 0);
}
