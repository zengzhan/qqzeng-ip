#include "stdafx.h"
#include "IPLocator.h"
#include <io.h>
#include <iostream>
#include <Windows.h>
#include <memory.h>

#include <string>
#include <sstream>
#include <vector>

#include <sys/timeb.h>
#include <ctime>
#include <climits>
#include <stdio.h>
using namespace std;

using std::cout;
using std::cerr;
using std::endl;
using std::ios;
prefix_map c1;
/**
* 最新一代文件结构 高性能解析IP数据库 qqzeng-ip.dat
* 编码：UTF8 字节序：Little-Endian
* For detailed information and guide: http://qqzeng.com/
* @author qqzeng-ip 于 2015-08-01
*/
IPSearch::IPSearch()
{
	FILE *file = NULL;
	_tfopen_s(&file, _T("qqzeng-ip.dat"), _T("rb"));
	long size = _filelength(_fileno(file));
	dataBuffer = (byte*)malloc(size * sizeof(byte));
	fread_s(dataBuffer, size, sizeof(byte), size, file);
	fclose(file);




	first_index = ReadInt32(dataBuffer, 0);
	last_index = ReadInt32(dataBuffer, 4);
	first_prefix_index = ReadInt32(dataBuffer, 8);
	last_prefix_index = ReadInt32(dataBuffer, 12);
	index_count = (last_index - first_index) / 12 + 1;
	prefix_count = (last_prefix_index - first_prefix_index) / 9 + 1;

	uint prefix_zise = prefix_count * 9;
	indexBuffer = (byte*)malloc(prefix_zise * sizeof(byte));
	memcpy_s(indexBuffer, prefix_zise, dataBuffer + first_prefix_index, prefix_zise);
	for (uint k = 0; k < prefix_count; k++)
	{
		Interval iv;
		uint i = k * 9;
		uint prefix = (uint)indexBuffer[i];
		iv.start = ReadInt32(indexBuffer, i + 1);
		iv.end = ReadInt32(indexBuffer, i + 5);
		c1.insert(prefix_map::value_type(prefix, iv));
	}
}

IPSearch::~IPSearch()
{
	free(indexBuffer);
	free(dataBuffer);
}



string IPSearch::Query(const char * ip)
{
	uint ip_prefix_value;
	uint intIP = ipToLong(ip, ip_prefix_value);
	uint high = 0;
	uint low = 0;
	uint startIp = 0;
	uint endIp = 0;
	uint local_offset = 0;
	uint local_length = 0;


	prefix_map::iterator it = c1.find(ip_prefix_value);
	if (it != c1.end())
	{
		low = it->second.start;
		high = it->second.end;
	}
	else
	{
		return "";
	}
	uint my_index = low == high ? low : BinarySearch(low, high, intIP);
	GetIndex(my_index, startIp, endIp, local_offset, local_length);
	if ((startIp <= intIP) && (endIp >= intIP))
	{
		return GetLocal(local_offset, local_length);
	}
	else
	{
		return "";
	}
}


uint IPSearch::BinarySearch(uint low, uint high, uint k)
{
	uint M = 0;
	while (low <= high)
	{
		uint mid = (low + high) / 2;

		uint endipNum = GetEndIp(mid);
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
void IPSearch::GetIndex(uint left, uint &startip, uint &endip, uint &local_offset, uint &local_length)
{
	uint left_offset = first_index + (left * 12);
	startip = ReadInt32(dataBuffer, left_offset);
	endip = ReadInt32(dataBuffer, left_offset + 4);
	local_offset = ReadInt24(dataBuffer, left_offset + 8);
	local_length = (uint)dataBuffer[left_offset + 11];
}

uint IPSearch::GetEndIp(uint left)
{
	uint left_offset = first_index + (left * 12);
	return ReadInt32(dataBuffer, left_offset + 4);

}


string IPSearch::GetLocal(uint local_offset, uint local_length)
{
	char* buffer = (char*)malloc(local_length + 1);
	memcpy_s(buffer, local_length, dataBuffer + local_offset, local_length);
	buffer[local_length] = '\0';
	string retStr;
	retStr = buffer;
	free(buffer);

	return retStr;



}


string ws2s(const string s)
{
	int l = MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, NULL, 0);
	wchar_t *str = new wchar_t[l + 1];
	memset(str, 0, l + 1);
	MultiByteToWideChar(CP_UTF8, 0, s.c_str(), -1, str, l);
	str[l] = '\0';

	int len;
	int slength = l + 1;
	len = WideCharToMultiByte(CP_ACP, 0, str, -1, 0, 0, 0, 0);
	char* buf = new char[len + 1];
	memset(buf, 0, len + 1);
	WideCharToMultiByte(CP_ACP, 0, str, -1, buf, len, 0, 0);
	buf[len] = '\0';
	std::string r(buf);
	delete[] buf;
	delete[] str;

	return r;
}



string IPSearch::longToIp(uint ip)
{
	unsigned char val[4];
	char *p = (char *)&ip;
	for (int i = 3; i >= 0; --i)
	{
		unsigned char v = p[i];
		val[3 - i] = v;
	}

	char ip_string[32];
	memset((char *)ip_string, 0x00, sizeof(ip_string));

	for (int i = 0; i < 4; ++i)
	{
		if (i == 0)
		{
			sprintf_s(ip_string, "%s%u", ip_string, val[i]);
		}
		else
		{
			sprintf_s(ip_string, "%s.%u", ip_string, val[i]);
		}
	}

	return string(ip_string);

	/*char buf[256];
	sprintf_s(buf, "%d.%d.%d.%d", adr >> 24, (adr >> 16) & 0xff, (adr >> 8) & 0xff, adr & 0xff);
	string ipstr(buf);
	return ipstr;*/

}


uint IPSearch::ipToLong(const char * ip, uint &prefix)
{


	unsigned int result = 0;
	char *pResult = (char *)&result;
	size_t index = 3;
	char *p = (char *)ip;
	char *pBegin = p;
	char *pEnd = p;
	while (true)
	{
		if (!*p)
		{
			char s[16];
			memset(s, 0x00, sizeof(s));
			memcpy(s, pBegin, p - pBegin);
			int val = atoi(s);
			pResult[index] = val;
			break;
		}

		if (*p != '.')
		{
			++p;
			continue;
		}

		pEnd = p;
		char s[16];
		memset(s, 0x00, sizeof(s));
		memcpy(s, pBegin, pEnd - pBegin);
		int val = atoi(s);

		pResult[index] = val;
		if (index == 3)
		{
			prefix = val;
		}
		--index;
		if (index < 0)
		{
			break;
		}

		pBegin = p + 1;
		pEnd = p + 1;

		++p;
	}

	return result;

	/*int a, b, c, d;
	sscanf_s(ip, "%u.%u.%u.%u", &a, &b, &c, &d);
	prefix = (BYTE)a;
	return ((BYTE)a << 24) | ((BYTE)b << 16) | ((BYTE)c << 8) | (BYTE)d;
	*/
}

uint IPSearch::ReadInt32(byte *buf, int pos)
{
	static uint retInt = 0;
	retInt = (uint)((buf[pos + 3] << 24 & 0xFF000000) | (buf[pos + 2] << 16 & 0x00FF0000) | (buf[pos + 1] << 8 & 0x0000FF00) | (buf[pos] & 0x000000FF));
	return retInt;
}
uint IPSearch::ReadInt24(byte *buf, int pos)
{
	static uint retInt = 0;
	retInt = (uint)((buf[pos + 2] << 16 & 0x00FF0000) | (buf[pos + 1] << 8 & 0x0000FF00) | (buf[pos] & 0x000000FF));
	return retInt;
}



int main(int argc, char **argv)
{
	IPSearch finder = IPSearch();
    const char *ip = ipstr.c_str();
	string local = finder.Query(ip);
	string gbkLocal = ws2s(local);
	cout << ipstr + "->" + ws2s(local) << endl;
    //113.70.39.14->亚洲|中国|广东|佛山|禅城|电信|440604|China|CN|113.1228|23.00842
	getchar();
	return 0;



}