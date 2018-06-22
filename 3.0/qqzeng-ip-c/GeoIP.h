#ifndef __GEO_IP_H_
#define __GEO_IP_H_

#include <stdint.h>
#include <stdlib.h>
#include <stdio.h>

#ifdef __cplusplus
extern "C" {
#endif

	typedef struct tag_geoip
	{
		uint32_t prefStart[256];
		uint32_t prefEnd[256];
		uint32_t *endArr;
		char **addrArr;
	}geo_ip;

	geo_ip* geoip_instance();
	int32_t geoip_loadDat(geo_ip* p);
	char* geoip_query(geo_ip* p, char *ip);
	uint32_t geoip_binary_search(geo_ip* p,uint32_t low, uint32_t high, uint32_t k);
	uint32_t geoip_ip2long(geo_ip* p,char *addr, uint32_t* prefix);
	uint32_t geoip_read_int32(geo_ip* p,uint8_t *buf, int pos);
	uint32_t geoip_read_int24(geo_ip* p,uint8_t *buf, int pos);

#ifdef __cplusplus
}
#endif
#endif
