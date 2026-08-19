#include "PhoneSearchBest.h"
#include <fstream>
#include <sstream>
#include <string>
#include <vector>
#include <unordered_map>
#include <locale>
#include <codecvt>
#include <algorithm>

using namespace std;





PhoneSearchBest* PhoneSearchBest::instance = nullptr;

// 单例模式获取 PhoneSearchBest 实例
PhoneSearchBest* PhoneSearchBest::GetInstance()
{
	if (!instance)
		instance = new PhoneSearchBest();
	return instance;
}
PhoneSearchBest::PhoneSearchBest() {
	LoadDat();
}
PhoneSearchBest::~PhoneSearchBest() {}

void PhoneSearchBest::LoadDat() {
	string dataPath = "qqzeng-phone-3.0.dat";//项目根目录

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



	int pref_size, desc_length, isp_length, phone_size;
	pref_size = BytesToInt<uint32_t>(bytes, 0);
	phone_size = BytesToInt<uint32_t>(bytes, 4);
	desc_length = BytesToInt<uint32_t>(bytes, 8);
	isp_length = BytesToInt<uint32_t>(bytes, 12);
	


	int head_length = 20;
	int start_index = head_length + desc_length + isp_length;


	string descString = BytesToUTF8String(bytes, head_length, desc_length);
	string ispString = BytesToUTF8String(bytes, head_length + desc_length, isp_length);


	addrArr = split(descString, '&');


	ispArr = split(ispString, '&');







	for (int m = 0; m < pref_size; m++)
	{
		int i = m * 7 + start_index;
		int pref = BytesToInt<uint8_t>(bytes, i);
		int index = BytesToInt<uint32_t>(bytes, i + 1);
		int length = BytesToInt<uint16_t>(bytes, i + 5);

		for (int n = 0; n < length; n++)
		{
			int p = (int)(start_index + pref_size * 7 + (n + index) * 4);
			int suff = BytesToInt<uint16_t>(bytes, p);
			int addrispIndex = BytesToInt<uint16_t>(bytes, p + 2);
			phone2D[pref][suff] = addrispIndex;
		}


	}

	file.close();
}

std::string PhoneSearchBest::BytesToUTF8String(const std::vector<unsigned char>& bytes, int pos, int len)
{
	std::string str(bytes.begin() + pos, bytes.begin() + pos + len);
	return str;
}

std::vector<std::string> PhoneSearchBest::split(std::string str, char delimiter) {

	std::vector<std::string> result;
	std::stringstream ss(str);
	std::string item;
	while (std::getline(ss, item, delimiter)) {
		result.push_back(item);
	}
	return result;
}


template <typename T>
T PhoneSearchBest::BytesToInt(const std::vector<unsigned char>& bytes, std::size_t m) {
	T result = 0;
	for (std::size_t i = 0; i < sizeof(T); ++i) {
		result |= (T(bytes[m + i]) << (i * 8));
	}
	return result;
}

// 查询方法
string PhoneSearchBest::Query(std::string  phoneNum) {
	int prefix = std::stoi(phoneNum.substr(0, 3));
	int suffix = std::stoi(phoneNum.substr(3, 4));
	int addrisp_index = phone2D[prefix][suffix];
	if (addrisp_index == 0) {
		return "";
	}
	return addrArr[addrisp_index / 100] + "|" + ispArr[addrisp_index % 100];
}

