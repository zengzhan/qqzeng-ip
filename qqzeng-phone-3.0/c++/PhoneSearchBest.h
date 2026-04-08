#include <cstring>
#include <iostream>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <unordered_map>

class PhoneSearchBest
{
public:
	// 获取 PhoneSearchBest 单例
	static PhoneSearchBest* GetInstance();
	~PhoneSearchBest();
	// 查询方法
	std::string Query(std::string  phoneNumber);

private:
	static PhoneSearchBest* instance;
	PhoneSearchBest();
	void LoadDat();
	std::string BytesToUTF8String(const std::vector<unsigned char>& bytes, int pos, int len);
	std::vector<std::string> split(std::string str, char delimiter);
	template<typename T>
	T BytesToInt(const std::vector<unsigned char>& bytes, std::size_t m);

	
	long phone2D[200][10000];
	std::vector<std::string> addrArr;
	std::vector<std::string> ispArr;


	
};


