#include <string>
#include <iostream>
#include <fstream>
#include  <unordered_map>  
#include <tchar.h>

using namespace std;
using std::string;


struct Interval;
typedef std::unordered_map<int, Interval>  prefix_map;
typedef unsigned char byte;
typedef unsigned int uint;

class IPSearch
{
public:
	IPSearch();
	~IPSearch();

	string Query(const char*ip);

private:

	string GetLocal(uint local_offset, uint local_length);

	

	

	uint BinarySearch(uint low, uint high, uint k);
	uint GetEndIp(uint left);
	

	void GetIndex(uint left, uint &startip, uint &endip, uint &local_offset, uint &local_length);

	uint ReadInt32(byte * buf, int pos);
	uint ReadInt24(byte * buf, int pos);

	
	uint ipToLong(const char * ip, uint &prefix);
	string longToIp(uint adr);

private:
	
	
	byte* dataBuffer;
	byte* indexBuffer;

	uint first_index;
	uint last_index;
	uint first_prefix_index;
	uint last_prefix_index;
	uint index_count;
	uint prefix_count;


};

struct Interval {
	uint start;
	uint end;

};


