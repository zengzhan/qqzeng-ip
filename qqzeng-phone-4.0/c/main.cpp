#include "PhoneSearch4Binary.h"
#include <iostream>
#include <windows.h>
int main() {

    SetConsoleOutputCP(65001);

    try {
        PhoneSearch4Binary* phoneSearch = PhoneSearch4Binary::getInstance();
        std::string phoneNumber = "16616056666";
        std::string result = phoneSearch->Query(phoneNumber);
        std::cout << "Result: " << result << std::endl;
        //黑龙江|齐齐哈尔|161000|0452|230200|中国联通
    }
    catch (const std::exception& e) {
        std::cerr << "Error: " << e.what() << std::endl;
    }
    return 0;
}
