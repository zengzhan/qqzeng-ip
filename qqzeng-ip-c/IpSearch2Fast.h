
#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>
typedef struct IpSearch2Fast
{
    uint32_t prefMap[256][2];
    uint32_t ipMap[1000000][2];
    char** addrArr;
}IpSearch2Fast;

static IpSearch2Fast* instance;
static IpSearch2Fast* createInstance();
IpSearch2Fast* getInstance();

int32_t geoip_load_dat(IpSearch2Fast* p);
char* geoip_query(IpSearch2Fast* p, char* ip);
uint32_t geoip_binary_search(IpSearch2Fast* p, uint32_t low, uint32_t high, uint32_t ip_num);
uint32_t geoip_ip2long( char* ip_str);
uint32_t geoip_read_int32(const uint8_t* buf, int pos);
uint32_t geoip_read_int24(const uint8_t* buf, int pos);
