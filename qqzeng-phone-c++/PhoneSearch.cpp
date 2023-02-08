#include "PhoneSearch.h"
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <unordered_map>
#include <locale>
#include <codecvt>
#include <algorithm>

using namespace std;




// 单例模式获取 PhoneSearch 实例
PhoneSearch& PhoneSearch::getInstance() {
	static PhoneSearch instance;
	return instance;
}
PhoneSearch::PhoneSearch() {
	LoadDat();
}

void PhoneSearch::LoadDat() {
	string dataPath = "qqzeng-phone.dat";//项目根目录

	std::locale::global(std::locale("zh_CN.UTF-8"));

	std::vector<unsigned char> bytes;
	std::ifstream file(dataPath, std::ios::binary);
	if (!file) {
		std::cerr << "Can't open the file." << std::endl;
		return;
	}

	file.seekg(0, std::ios::end);
	std::streampos fileSize = file.tellg();
	bytes.resize(fileSize);

	file.seekg(0, std::ios::beg);
	file.read((char*)&bytes[0], fileSize);
	file.close();

	int PrefSize = BytesToInt<uint32_t>(bytes, 0);
	int RecordSize = BytesToInt<uint32_t>(bytes, 4);
	int descLength = BytesToInt<uint32_t>(bytes, 8);
	int ispLength = BytesToInt<uint32_t>(bytes, 12);;

	int startIndex = 16 + PrefSize * 9 + RecordSize * 7;
	string descString = BytesToUTF8String(bytes, startIndex, descLength);
	string ispString = BytesToUTF8String(bytes, startIndex + descLength, ispLength);

	addrArr = Split(descString, '&');
	ispArr = Split(ispString, '&');



	//前缀区
	

	int m = 0;
	for (int k = 0; k < PrefSize; k++) {
		int pos = k * 9 + 16;
		int n = BytesToInt<uint8_t>(bytes, pos);
		prefMap[n][0] = BytesToInt<uint32_t>(bytes, pos + 1);
		prefMap[n][1] = BytesToInt<uint32_t>(bytes, pos + 5);

		if (m < n) {
			for (; m < n; m++) {
				prefMap[m][0] = 0;
				prefMap[m][1] = 0;
			}
			m++;
		}
		else {
			m++;
		}
	}
	

	//索引区	
	phoneArr.resize(RecordSize);
	phoneMap.resize(RecordSize);
	for (int i = 0; i < RecordSize; i++)
	{
		int p = 16 + PrefSize * 9 + i * 7;
		int s = BytesToInt<uint16_t>(bytes, p + 6);
		phoneArr[i]=BytesToInt<uint32_t>(bytes, p);		
		phoneMap[i][0] = BytesToInt<uint16_t>(bytes, p + 4);
		phoneMap[i][1] = BytesToInt<uint8_t>(bytes, p + 6);
	}

	
}

std::string PhoneSearch::BytesToUTF8String(const std::vector<unsigned char>& bytes, int pos, int len)
{
	std::string str(bytes.begin() + pos, bytes.begin() + pos + len);
	return str;
}

std::vector<std::string> PhoneSearch::Split(std::string str, char delimiter) {

	std::vector<std::string> result;
	std::stringstream ss(str);
	std::string item;
	while (std::getline(ss, item, delimiter)) {
		result.push_back(item);
	}
	return result;
}


template <typename T>
T PhoneSearch::BytesToInt(const std::vector<unsigned char>& bytes, std::size_t m) {
	T result = 0;
	for (std::size_t i = 0; i < sizeof(T); ++i) {
		result |= (T(bytes[m + i]) << (i * 8));
	}
	return result;
}

int PhoneSearch::BinarySearch(int low, int high, int k) {
	if (low > high) return -1;
	else {
		int mid = (low + high) >> 1;
		int phoneNum =phoneArr[mid];
		if (phoneNum == k) return mid;
		else if (phoneNum > k) return BinarySearch(low, mid - 1, k);
		else return BinarySearch(mid + 1, high, k);
	}
}



// 查询方法
string PhoneSearch::Query(std::string  phone) {
	int pref = stoi(phone.substr(0, 3));
	int val = stoi(phone.substr(0, 7));

	int low = prefMap[pref][0];
	int high = prefMap[pref][1];
	if (high == 0)
	{
		return "";
	}
	int cur = low == high ? low : BinarySearch(low, high, val);
	if (cur != -1)
	{
		return addrArr[phoneMap[cur][0]] + '|' + ispArr[phoneMap[cur][1]];
	}
	else
	{
		return "";
	}
}


