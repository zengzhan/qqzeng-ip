/*
 * qqzeng-ip 2.0 (.dat) C++ 版使用示例
 */
#include "IPLocator.h"

#include <iostream>

int main()
{
    /* 方式一: 默认路径单例 */
    IPSearch* finder = IPSearch::instance();
    /* 方式二: 指定路径多实例:
       IPSearch china("qqzeng-ip-china-utf8.dat");
       IPSearch global("qqzeng-ip-global-utf8.dat"); */
    if (!finder)
    {
        std::cout << "the IPSearch instance is null!" << std::endl;
        return -1;
    }

    const char* ip = "58.213.198.68";
    std::cout << ip << "\n" << finder->Query(ip) << std::endl;
    std::cout << finder->findUint(0x08080808u) << std::endl;
    return 0;
}
