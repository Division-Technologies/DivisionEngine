module;

#include <iostream>

export module Printer;

export struct Printer {
    void Log(const std::string_view message) const {
        std::cout << message << '\n';
    }

    void Error(const std::string_view message) const {
        std::cerr << message << '\n';
    }
};
