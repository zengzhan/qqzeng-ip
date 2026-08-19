#include "PhoneSearch4Binary.h"
#include <iostream>
#include <fstream>
#include <string>
#include <sstream>
#include <vector>
#include <unordered_map>
#include <memory>

PhoneSearch4Binary* PhoneSearch4Binary::instance = nullptr;
std::mutex PhoneSearch4Binary::mutex;



void PhoneSearch4Binary::LoadData() {
	// 打开文件
	std::string filename = "qqzeng-phone-4.0.dat";
	std::ifstream file(filename, std::ios::binary);
	if (!file.is_open()) {
		throw std::runtime_error("Error opening file.");
	}

	// 获取文件大小并调整向量大小
	file.seekg(0, std::ios::end);
	std::streampos fileSize = file.tellg();
	file.seekg(0, std::ios::beg);


	// 使用文件大小创建 std::unique_ptr 对象
	data = std::make_unique<std::vector<unsigned char>>(fileSize);

	// 读取文件内容到向量中
	file.read(reinterpret_cast<char*>(data->data()), fileSize);

	// 关闭文件
	file.close();

	// 解析文件数据
	uint32_t PrefSize = BytesToInt(data, 0, 4);
	uint32_t PhoneSize = BytesToInt(data, 4, 4);
	uint32_t addrLen = BytesToInt(data, 8, 4);
	uint32_t ispLen = BytesToInt(data, 12, 4);
	uint32_t verNum = BytesToInt(data, 16, 4);


	size_t headLen = 20;
	addressArray = SplitStringArray(data, headLen, addrLen);
	ispArray = SplitStringArray(data, headLen + addrLen, ispLen);
	size_t startIndex = headLen + addrLen + ispLen;

	for (size_t m = 0; m < PrefSize; m++) {
		size_t i = m * 5 + startIndex;
		size_t pref = BytesToInt(data, i, 1);
		size_t index = BytesToInt(data, i + 1, 4);
		prefixDictionary[std::to_string(pref)] = index;
	}


}



std::size_t  PhoneSearch4Binary::BytesToInt(const std::unique_ptr<std::vector<unsigned char>>& bytes, std::size_t startIndex, std::uint8_t size) {
	
	size_t result = 0;
	for (uint8_t i = 0; i < size; ++i) {
		result |= static_cast<size_t>(bytes->at(startIndex + i)) << (8 * i);
	}
	return result;
}



std::vector<std::string> PhoneSearch4Binary::SplitStringArray(const std::unique_ptr<std::vector<unsigned char>>& bytes, size_t start, size_t len) {
	std::vector<std::string> result;
	std::string str(bytes->begin() + start, bytes->begin() + start + len);
	std::stringstream ss(str);
	std::string item;
	while (std::getline(ss, item, '&')) {
		if (!item.empty()) {
			result.push_back(item);
		}
	}
	return result;
}

std::string PhoneSearch4Binary::Query(const std::string& phoneNumber) {
	
	std::string prefix = phoneNumber.substr(0, 3);
	int suffix = std::stoi(phoneNumber.substr(3, 4));
	int addrispIndex = 0;

	auto it = prefixDictionary.find(prefix);
	if (it != prefixDictionary.end()) {
		int start = it->second;
		int p = start + suffix * 2;
		addrispIndex = BytesToInt(data, p, 2);
	}

	if (addrispIndex == 0) {
		return "|||||";
	}

	return addressArray[addrispIndex >> 5] + "|" + ispArray[addrispIndex & 0x001F];
}

