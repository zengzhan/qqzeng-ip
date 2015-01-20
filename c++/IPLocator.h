#ifndef _IP_LOCATOR_H_
#define _IP_LOCATOR_H_

#include <string>
#include <iostream>
#include <fstream>

using std::string;
using std::streamsize;

class IPSearch
{
public:
	IPSearch(const string& ipdb_name);
	~IPSearch();
	string getVersion();
	string Query(const string& ip);
	string getIpRange(const string& ip);
private:
	string Query(unsigned int ip);
	string getIpRange(unsigned int ip);
	static unsigned int getIpFromString(const string& ip);
	static string getIpString(unsigned int ip);
	static unsigned int bytes2integer(unsigned char *ip, int count);
	void readFromFile(streamsize offset, unsigned char *buf,int len);
	string readStringFromFile(streamsize offset);
	string getAddr(streamsize offset);
	string getAreaAddr(streamsize offset);
	void setIpRange(unsigned int rec_no);
private:
	std::ifstream ipdb;
	unsigned int first_index;
	unsigned int last_index;
	unsigned int index_count;
	unsigned int cur_start_ip;
	unsigned int cur_start_ip_offset;
	unsigned int cur_end_ip;
};

#endif