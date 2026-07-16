#include "IpSearch2Fast.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <stdint.h>


static IpSearch2Fast* createInstance()
{
	if (instance == NULL)
	{
		instance = (IpSearch2Fast*)malloc(sizeof(IpSearch2Fast));	
		geoip_load_dat(instance);
	}	
	return instance;
}

IpSearch2Fast* getInstance()
{
	return createInstance();
}

int32_t geoip_load_dat(IpSearch2Fast* p) {

	FILE* fp = NULL;
	const char* filename = "qqzeng-ip-utf8.dat";
	fopen_s(&fp, filename, "rb");
	if (fp == NULL)
	{
		printf("%s", "打开文件失败");
		return -1;
	}

	fseek(fp, 0, SEEK_END);
	size_t size = ftell(fp);
	rewind(fp);

	uint8_t* data = malloc(size);
	fread(data, 1, size, fp);

	uint32_t firstStartIpOffset = geoip_read_int32(data, 0);
	uint32_t lastStartIpOffset = geoip_read_int32(data, 4);
	uint32_t prefixStartOffset = geoip_read_int32(data, 8);
	uint32_t prefixEndOffset = geoip_read_int32(data, 12);

	uint32_t  ipCount = (lastStartIpOffset - firstStartIpOffset) / 12 + 1;
	uint32_t  prefixCount = (prefixEndOffset - prefixStartOffset) / 9 + 1;

	uint32_t m = 0;
	memset(p->prefMap, 0, sizeof(p->prefMap));
	for (uint32_t k = 0; k < prefixCount; k++) {
		int i = k * 9;
		uint32_t n = (uint32_t)data[prefixStartOffset + i];
		p->prefMap[n][0] = geoip_read_int32(data, prefixStartOffset + i + 1);
		p->prefMap[n][1] = geoip_read_int32(data, prefixStartOffset + i + 5);
		if (m < n)
		{
			for (; m < n; m++)
			{
				p->prefMap[m][0] = 0;
				p->prefMap[m][1] = 0;
			}
			m++;
		}
		else
		{
			m++;
		}
	}

	p->addrArr = (char**)malloc(ipCount * sizeof(char*));
	for (int i = 0; i < ipCount; i++)
	{
		long pos = firstStartIpOffset + (i * 12);
		uint32_t startip = geoip_read_int32(data, pos);
		uint32_t endip = geoip_read_int32(data, 4 + pos);
		int offset = geoip_read_int24(data, 8 + pos);
		int length = data[11 + pos];
		p->ipMap[i][0] = startip;
		p->ipMap[i][1] = endip;
		
		char* str = (char*)malloc(length + 1);
		memcpy(str, data + offset, length);
		str[length] = '\0';
		p->addrArr[i] = str;
	}


	free(data);
	fclose(fp);
	return 0;
}

char* geoip_query(IpSearch2Fast* p, char* ip_str) {
	uint32_t val, pref, low, high, cur;
	val = geoip_ip2long(ip_str);
	pref= (val >> 24) & 0xFF;
	low = p->prefMap[pref][0];
	high = p->prefMap[pref][1];

	if (high == 0) {
		return "";
	}

	cur = low == high ? low : geoip_binary_search(p, low, high, val);

	if (p->ipMap[cur][0] <= val && p->ipMap[cur][1] >= val) {
		return p->addrArr[cur];
	}
	else {	
		return "no data|country|province|city|district|isp|area_code|country_english|country_code|longitude|latitude";
	}
}

uint32_t geoip_binary_search(IpSearch2Fast* p, uint32_t low, uint32_t high, uint32_t ip_num)
{
	uint32_t M = 0;
	while (low <= high) {
		uint32_t mid = (low + high) / 2;
		uint32_t endipNum = p->ipMap[mid][1];
		if (endipNum >= ip_num) {
			M = mid;
			if (mid == 0) {
				break;
			}
			high = mid - 1;
		}
		else {
			low = mid + 1;
		}
	}
	return M;
}



uint32_t geoip_ip2long(char* ip_str) {
	uint32_t parts[4] = { 0 };
	int dotCount = 0;

	while (*ip_str != '\0') {
		uint32_t value = 0;

		while (*ip_str != '.' && *ip_str != '\0') {
			value = value * 10 + (*ip_str - '0');
			ip_str++;
		}

		parts[dotCount++] = value;

		if (*ip_str == '.') {
			ip_str++;
		}
		else {
			break;
		}
	}

	return (parts[0] << 24) | ((parts[1] & 0xff) << 16) | ((parts[2] & 0xff) << 8) | (parts[3] & 0xff);
}

uint32_t geoip_read_int32(const uint8_t* buf, int pos)
{
	uint32_t result;
	result = (uint32_t)((buf[pos + 3] & 0xff) << 24 | (buf[pos + 2] & 0xff) << 16 | (buf[pos + 1] & 0xff) << 8 | (buf[pos] & 0xff));
	return result;
}

uint32_t geoip_read_int24(const uint8_t* buf, int pos)
{
	uint32_t result;
	result = (uint32_t)((buf[pos + 2] & 0xff) << 16 | (buf[pos + 1] & 0xff) << 8 | (buf[pos] & 0xff));
	return result;
}
