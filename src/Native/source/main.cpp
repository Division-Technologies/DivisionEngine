// test code
#include <exception>
#include <iostream>

#include "include_test.hpp"
#include "test.hpp"


int main() {
    try {
        division_engine::Hoge hoge;

        std::cout << hoge.Test() << '\n';

        Test test;
        std::cout << test.Hoge() << '\n';

        int result = 0;
        std::cout << result << '\n';

        std::cout << "Hello, DivisionNotes!" << '\n';
        return 0;
    } catch (const std::exception& e) {
        try {
            std::cerr << "fatal: " << e.what() << '\n';
        } catch (...) {  // NOLINT(bugprone-empty-catch)
            // ログ出力自体の失敗は諦める(best-effort)
        }
        return 1;
    } catch (...) {
        try {
            std::cerr << "fatal: unknown exception\n";
        } catch (...) {  // NOLINT(bugprone-empty-catch)
            // ログ出力自体の失敗は諦める(best-effort)
        }
        return 1;
    }
}
