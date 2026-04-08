#include <stdint.h>
#include <iostream>
#include <map>

using namespace std;

class Interval;
typedef map<int32_t, Interval>  prefix_map;

class IPSearch
{
public:
    static IPSearch* instance();
    
	const string Query(const char*ip);

   
private:
    IPSearch();
	~IPSearch();
    
    bool init();
    
    uint8_t* readFile (const string path, long* length);

	string GetLocal(uint32_t local_offset, uint32_t local_length);

	uint32_t BinarySearch(uint32_t low, uint32_t high, uint32_t k);
	uint32_t GetEndIp(uint32_t left);
	

	void GetIndex(uint32_t left, uint32_t &startip, uint32_t &endip, uint32_t &local_offset, uint32_t &local_length);

	uint32_t ReadInt32(uint8_t * buf, int pos);
	uint32_t ReadInt24(uint8_t * buf, int pos);

	
	uint32_t ipToLong(const char * ip, uint32_t &prefix);
	string longToIp(uint32_t adr);

private:
	uint8_t* dataBuffer;

	uint32_t first_index;
	uint32_t last_index;
	uint32_t first_prefix_index;
	uint32_t last_prefix_index;
	uint32_t index_count;
	uint32_t prefix_count;
};

class Interval {
public:
	uint32_t start;
	uint32_t end;
};
