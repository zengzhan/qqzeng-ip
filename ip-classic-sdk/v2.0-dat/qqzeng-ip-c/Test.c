/*
 * qqzeng-ip 2.0 (.dat) C 版使用示例
 */
#include "IpSearch2Fast.h"

#include <stdio.h>

int main(void)
{
    /* 方式一: 默认路径单例(自动探测) */
    IpSearch2Fast* finder = getInstance();
    /* 方式二: 指定路径,可同时加载国内版与全球版:
       IpSearch2Fast* china  = geoip_init("qqzeng-ip-china-utf8.dat");
       IpSearch2Fast* global = geoip_init("qqzeng-ip-global-utf8.dat"); */
    if (!finder)
    {
        printf("the IPSearch instance is null! (把 .dat 放到工作目录或用 geoip_init(path))\n");
        return -1;
    }

    const char* ip = "58.213.198.68";
    const char* local = geoip_query(finder, ip);
    printf("%s\n%s\n", ip, local);

    /* 整数查询(高性能场景): */
    printf("%s\n", geoip_query_uint(finder, 0x08080808u));

    geoip_free(finder);
    return 0;
}
