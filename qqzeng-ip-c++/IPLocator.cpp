#include "stdafx.h"
#include "IPLocator.h"
#include <stdio.h>
#include <sstream>
#include <sys/timeb.h>
#include <errno.h>
//#define IP_FILENAME "qqzeng-ip-utf8.dat"
#define IP_FILENAME "qqzeng-ip-gbk.dat"
prefix_map c1;

/**
* 最新一代文件结构 高性能解析IP数据库 qqzeng-ip.dat
* 编码：UTF8和GBK 字节序：Little-Endian
* For detailed information and guide: http://qqzeng.com/
* @author qqzeng-ip 于 2015-08-01
*/

IPSearch::IPSearch()
	: dataBuffer(NULL)
{}

IPSearch::~IPSearch()
{
	free(dataBuffer);
}

IPSearch* IPSearch::instance()
{
	IPSearch *ret = new IPSearch;
	if (ret->init()) {
		return ret;
	}

	if (ret) {
		delete ret;
	}
	return NULL;
}

bool IPSearch::init()
{
	long size = 0;
	dataBuffer = readFile(IP_FILENAME, &size);

	if (dataBuffer)
	{
		first_index = ReadInt32(dataBuffer, 0);
		last_index = ReadInt32(dataBuffer, 4);
		first_prefix_index = ReadInt32(dataBuffer, 8);
		last_prefix_index = ReadInt32(dataBuffer, 12);
		index_count = (last_index - first_index) / 12 + 1;
		prefix_count = (last_prefix_index - first_prefix_index) / 9 + 1;

		uint8_t *indexBuffer = dataBuffer + first_prefix_index;
		for (uint32_t i = 0; i < prefix_count; indexBuffer += 9, i++)
		{
			Interval iv;
			uint32_t prefix = (uint32_t)indexBuffer[0];
			iv.start = ReadInt32(indexBuffer, 1);
			iv.end = ReadInt32(indexBuffer, 5);
			c1[prefix] = iv;
		}

		return true;
	}
	return false;
}

uint8_t* IPSearch::readFile(const string path, long* length) {
	uint8_t *data;

	FILE *file = fopen(path.data(), "rb");
	int readBytes = 0;

	if (!file) return 0;

	fseek(file, 0, SEEK_END);
	*length = ftell(file);
	fseek(file, 0, SEEK_SET);
	data = (uint8_t*)malloc(*length * sizeof(uint8_t));
	readBytes = fread(data, 1, *length, file);
	fclose(file);
	if (readBytes != *length)
	{
		free(data);
		data = NULL;
	}
	return data;
}

const string IPSearch::Query(const char*ip)
{
	uint32_t ip_prefix_value;
	uint32_t intIP = ipToLong(ip, ip_prefix_value);
	uint32_t high = 0;
	uint32_t low = 0;
	uint32_t startIp = 0;
	uint32_t endIp = 0;
	uint32_t local_offset = 0;
	uint32_t local_length = 0;


	prefix_map::iterator it = c1.find(ip_prefix_value);
	if (it != c1.end())
	{
		low = it->second.start;
		high = it->second.end;

		uint32_t my_index = low == high ? low : BinarySearch(low, high, intIP);
		GetIndex(my_index, startIp, endIp, local_offset, local_length);
		if ((startIp <= intIP) && (endIp >= intIP))
		{
			return GetLocal(local_offset, local_length);
		}
	}

	return NULL;
}


uint32_t IPSearch::BinarySearch(uint32_t low, uint32_t high, uint32_t k)
{
	uint32_t M = 0;
	while (low <= high)
	{
		uint32_t mid = (low + high) / 2;

		uint32_t endipNum = GetEndIp(mid);
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
void IPSearch::GetIndex(uint32_t left, uint32_t &startip, uint32_t &endip, uint32_t &local_offset, uint32_t &local_length)
{
	uint32_t left_offset = first_index + (left * 12);
	startip = ReadInt32(dataBuffer, left_offset);
	endip = ReadInt32(dataBuffer, left_offset + 4);
	local_offset = ReadInt24(dataBuffer, left_offset + 8);
	local_length = (uint32_t)dataBuffer[left_offset + 11];
}

uint32_t IPSearch::GetEndIp(uint32_t left)
{
	uint32_t left_offset = first_index + (left * 12);
	return ReadInt32(dataBuffer, left_offset + 4);

}


string IPSearch::GetLocal(uint32_t local_offset, uint32_t local_length)
{
	string str("");
	str.append((const char*)dataBuffer + local_offset, local_length);
	return str;
}



string IPSearch::longToIp(uint32_t adr) {
	char buf[256];
	sprintf(buf, "%d.%d.%d.%d", adr >> 24, (adr >> 16) & 0xff, (adr >> 8) & 0xff, adr & 0xff);
	string ipstr(buf);
	return ipstr;

}


uint32_t IPSearch::ipToLong(const char * ip, uint32_t &prefix)
{

	/*int a, b, c, d;
	sscanf_s(ip, "%u.%u.%u.%u", &a, &b, &c, &d);
	prefix = (BYTE)a;
	return ((BYTE)a << 24) | ((BYTE)b << 16) | ((BYTE)c << 8) | (BYTE)d;
	*/

	int a, b, c, d;
	int iLen;
	int abcdIndex = 0;
	iLen = strlen(ip);
	char ips[3];
	memset(ips, '\0', 3);

	int ipsCnt = 0;
	for (int i = 0; i < iLen; i++)
	{
		if ('.' == ip[i])
		{
			if (0 == abcdIndex)
			{
				abcdIndex = 1;
				a = atoi(ips);
			}
			else if (1 == abcdIndex)
			{
				abcdIndex = 2;
				b = atoi(ips);
			}
			else if (2 == abcdIndex)
			{
				abcdIndex = 3;
				c = atoi(ips);
			}

			ipsCnt = 0;
			memset(ips, '\0', 3);
		}
		else
		{
			ips[ipsCnt] = ip[i];
			ipsCnt++;
		}
	}
	d = atoi(ips);

	prefix = (uint32_t)a;
	return ((uint8_t)a << 24) | ((uint8_t)b << 16) | ((uint8_t)c << 8) | (uint8_t)d;

}

uint32_t IPSearch::ReadInt32(uint8_t *buf, int pos)
{
	static uint32_t retInt = 0;
	retInt = (uint32_t)((buf[pos + 3] << 24 & 0xFF000000) | (buf[pos + 2] << 16 & 0x00FF0000) | (buf[pos + 1] << 8 & 0x0000FF00) | (buf[pos] & 0x000000FF));
	return retInt;
}
uint32_t IPSearch::ReadInt24(uint8_t *buf, int pos)
{
	static uint32_t retInt = 0;
	retInt = (uint32_t)((buf[pos + 2] << 16 & 0x00FF0000) | (buf[pos + 1] << 8 & 0x0000FF00) | (buf[pos] & 0x000000FF));
	return retInt;
}

int main(int argc, char **argv)
{
	IPSearch *finder = IPSearch::instance();
	if (!finder) {
		printf("the IPSearch instance is null!");
		getchar();
		return -1;
	}

	stringstream ostr;
	string ipstr = ipArray[j];
	const char *ip = "123.4.5.68";
	const string local = finder->Query(ip);
	ostr << ipstr + "->" + local << endl;
	cout << ostr.str() << endl;
	getchar();
	return 0;
}