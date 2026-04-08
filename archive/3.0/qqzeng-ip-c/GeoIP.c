#include "GeoIP.h"
#include "string.h"
#include <stdlib.h>
char *IP_FILENAME = "qqzeng-ip-3.0-ultimate.dat";

//编码：utf-8
//性能：C语言性能之王 每秒解析1680万+ip
//环境：CPU i7-7700K + DDR2400 16G + win10 X64 (Release)
//创建：qqzeng-ip 于 2018-06-21  

geo_ip *geoip_instance()
{
	geo_ip *ret = (geo_ip *)malloc(sizeof(geo_ip));
	if (geoip_loadDat(ret) >= 0)
	{
		return ret;
	}

	if (ret)
	{
		free(ret);
	}
	return NULL;
}

int32_t geoip_loadDat(geo_ip *p)
{
	FILE *file;
	uint8_t *buffer;
	long len = 0;
	int k, i, j;
	uint32_t RecordSize, offset, length;
	errno_t err = fopen_s(&file, IP_FILENAME, "rb");
	if (err == 2)
	{
		printf("%s", "没有此文件或目录");
		return -2;
	}
	fseek(file, 0, SEEK_END);
	len = ftell(file);
	fseek(file, 0, SEEK_SET);
	buffer = (uint8_t *)malloc(len * sizeof(uint8_t));
	fread(buffer, 1, len, file);
	fclose(file);

	for (k = 0; k < 256; k++)
	{
		i = k * 8 + 4;
		p->prefStart[k] = geoip_read_int32(p, buffer, i);
		p->prefEnd[k] = geoip_read_int32(p, buffer, i + 4);
	}

	RecordSize = geoip_read_int32(p, buffer, 0);
	p->endArr = (uint32_t *)malloc(RecordSize * sizeof(uint32_t));
	p->addrArr = (char **)malloc(RecordSize * sizeof(char*));
	for (i = 0; i < RecordSize; i++)
	{
		j = 2052 + (i * 8);
		p->endArr[i] = geoip_read_int32(p, buffer, j);
		offset = geoip_read_int24(p, buffer, 4 + j);
		length = (uint32_t)buffer[7 + j];
		char *result = (char *)malloc((length + 1) * sizeof(char));
		memcpy(result, buffer + offset, length);
		result[length] = '\0';
		p->addrArr[i] = result;
	}
	return 0;
}

char *geoip_query(geo_ip *p, char *ip)
{
	uint32_t pref, cur, intIP, low, high;
	if (NULL == p)
	{
		return NULL;
	}
	intIP = geoip_ip2long(p, ip, &pref);

	low = p->prefStart[pref];
	high = p->prefEnd[pref];
	cur = (low == high) ? low : geoip_binary_search(p, low, high, intIP);
	return p->addrArr[cur];
}

uint32_t geoip_binary_search(geo_ip *p, uint32_t low, uint32_t high, uint32_t k)
{
	uint32_t M = 0;
	while (low <= high)
	{
		uint32_t mid = (low + high) >> 1;

		uint32_t endipNum = p->endArr[mid];
		if (endipNum >= k)
		{
			M = mid;
			if (mid == 0)
			{
				break;
			}
			high = mid - 1;
		}
		else
			low = mid + 1;
	}
	return M;
}

uint32_t geoip_ip2long(geo_ip *p, char *addr, uint32_t *prefix)
{
	uint32_t c, octet, t;
	uint32_t ipnum;
	int i = 3;

	octet = ipnum = 0;
	while ((c = *addr++))
	{
		if (c == '.')
		{			
			ipnum <<= 8;
			ipnum += octet;
			i--;
			octet = 0;


		}
		else
		{
			t = octet;
			octet <<= 3;
			octet += t;
			octet += t;
			c -= '0';
			
			octet += c;
			if (i == 3)
			{
				*prefix = octet;
			}
		}
	}
	
	ipnum <<= 8;

	return ipnum + octet;
}

uint32_t geoip_read_int32(geo_ip *p, uint8_t *buf, int pos)
{
	uint32_t result;
	result = (uint32_t)((buf[pos + 3] << 24 & 0xff000000) | (buf[pos + 2] << 16 & 0x00ff0000) | (buf[pos + 1] << 8 & 0xff00) | (buf[pos] & 0xff));
	return result;
}

uint32_t geoip_read_int24(geo_ip *p, uint8_t *buf, int pos)
{
	uint32_t result;
	result = (uint32_t)((buf[pos + 2] << 16 & 0x00ff0000) | (buf[pos + 1] << 8 & 0xff00) | (buf[pos] & 0xff));
	return result;
}

int main(int argc, char **argv)
{
	geo_ip *finder = geoip_instance();
	if (!finder)
	{
		printf("the IPSearch instance is null!");
		return -1;
	}	

	char *ip = "8.8.8.8";
	char *local = geoip_query(finder, ip);
	printf("%s\n",local);

	system("pause");
	return 0;
}

