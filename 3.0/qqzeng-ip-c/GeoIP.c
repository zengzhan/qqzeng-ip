#include "GeoIP.h"
#include "string.h"
#include <stdlib.h>
char *IP_FILENAME = "qqzeng-ip-3.0-ultimate.dat";


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

	char *ip = "58.62.92.106";
	char *local = geoip_query(finder, ip);
	printf("%s\n",local);

	system("pause");
	return 0;
}



//环境：CPU i7-7700K + DDR2400 16G + win10 X64 (Release)

//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 3690.000000 万ip->2.262000 秒   每秒 1631.299735 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 18630.000000 万ip->11.212000 秒 每秒 1661.612558 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 9900.000000 万ip->5.954000 秒   每秒 1662.747733 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 17820.000000 万ip->10.641000 秒 每秒 1674.654638 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 12870.000000 万ip->7.689000 秒  每秒 1673.819742 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 14490.000000 万ip->8.663000 秒  每秒 1672.630728 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 18450.000000 万ip->11.027000 秒 每秒 1673.165866 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 18900.000000 万ip->11.305000 秒 每秒 1671.826625 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 7380.000000 万ip->4.441000 秒   每秒 1661.787886 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 18630.000000 万ip->11.209000 秒 每秒 1662.057275 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 1107.000000 万ip->0.670000 秒   每秒 1652.238806 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 135.000000 万ip->0.078000 秒    每秒 1730.769231 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 5724.000000 万ip->3.398000 秒   每秒 1684.520306 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 5022.000000 万ip->2.984000 秒   每秒 1682.975871 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 4806.000000 万ip->2.853000 秒   每秒 1684.542587 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 5913.000000 万ip->3.529000 秒   每秒 1675.545480 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 2943.000000 万ip->1.751000 秒   每秒 1680.753855 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 1836.000000 万ip->1.093000 秒   每秒 1679.780421 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 2673.000000 万ip->1.594000 秒   每秒 1676.913425 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 6777.000000 万ip->4.026000 秒   每秒 1683.308495 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 73.800000 万ip->0.043000 秒     每秒 1716.279070 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 252.000000 万ip->0.153000 秒    每秒 1647.058824 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 109.800000 万ip->0.065000 秒    每秒 1689.230769 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 25.200000 万ip->0.015000 秒     每秒 1680.000000 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 167.400000 万ip->0.100000 秒    每秒 1674.000000 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 102.600000 万ip->0.061000 秒    每秒 1681.967213 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 354.600000 万ip->0.213000 秒    每秒 1664.788732 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 52.200000 万ip->0.030000 秒     每秒 1740.000000 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 63.000000 万ip->0.036000 秒     每秒 1750.000000 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 432.000000 万ip->0.257000 秒    每秒 1680.933852 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 15.990000 万ip->0.009000 秒     每秒 1776.666667 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 7.800000 万ip->0.005000 秒      每秒 1560.000000 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 44.850000 万ip->0.026000 秒     每秒 1725.000000 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 73.320000 万ip->0.042000 秒     每秒 1745.714286 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 90.480000 万ip->0.053000 秒     每秒 1707.169811 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 76.830000 万ip->0.045000 秒     每秒 1707.333333 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 48.360000 万ip->0.028000 秒     每秒 1727.142857 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 42.120000 万ip->0.024000 秒     每秒 1755.000000 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 54.990000 万ip->0.031000 秒     每秒 1773.870968 万次
//C语言 查询 qqzeng-ip-ultimate.dat 【3.0】内存优化版 76.830000 万ip->0.044000 秒     每秒 1746.136364 万次




