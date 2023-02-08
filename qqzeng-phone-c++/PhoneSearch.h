#ifndef PHONESEARCH_H
#define PHONESEARCH_H

#include <iostream>
#include <fstream>
#include <vector>
#include <map>
#include <string>
#include <array>

class PhoneSearch
{
public:
	static PhoneSearch& getInstance();
	std::string Query(std::string phone);


private:
	PhoneSearch();
	PhoneSearch(const PhoneSearch&) = delete;
	PhoneSearch& operator=(const PhoneSearch&) = delete;

	void LoadDat();

	std::string BytesToUTF8String(const std::vector<unsigned char>& bytes, int pos, int len);
	std::vector<std::string> Split(std::string str, char delimiter);
	int BinarySearch(int low, int high, int k);

	
	std::array<std::array<long, 2>, 200> prefMap;
	std::vector<std::array<long, 2>> phoneMap;
	std::vector<long> phoneArr;

	std::vector<std::string> addrArr;
	std::vector<std::string> ispArr;
	
	template<typename T>
	T BytesToInt(const std::vector<unsigned char>& bytes, std::size_t m);
};

#endif // PHONESEARCH_H
