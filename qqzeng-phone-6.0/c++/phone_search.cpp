#include <iostream>
#include <fstream>
#include <vector>
#include <string>
#include <sstream>
#include <stdexcept>
#include <cstring>
#include <cstdint>
#include <algorithm>

const int HEADER_SIZE = 32;                  // 8个uint的头部
const int PREFIX_COUNT = 200;                // 电话号码前缀总数（0-199）
const int BITMAP_POP_COUNT_OFFSET = 0x4E2;   // 位图统计信息偏移量

class PhoneSearch6Db {
public:
    static PhoneSearch6Db& Instance() {
        static PhoneSearch6Db instance;
        return instance;
    }

    void LoadDatabase(const std::string& filePath) {
        std::ifstream file(filePath, std::ios::binary);
        if (!file.is_open()) {
            throw std::runtime_error("Database file not found");
        }

        // 读取整个文件到内存
        file.seekg(0, std::ios::end);
        size_t fileSize = file.tellg();
        file.seekg(0, std::ios::beg);
        data.resize(fileSize);
        file.read(reinterpret_cast<char*>(data.data()), fileSize);

        // 解析头部（小端序）
        uint32_t header[8];
        for (int i = 0; i < 8; ++i) {
            header[i] = readUint32LE(data.data() + i * 4);
        }

        // 计算偏移量
        int regionsStart = HEADER_SIZE;
        int ispsStart = regionsStart + header[1];
        int indexStart = ispsStart + header[2];

        // 解析地区和运营商表
        std::string regionsStr(reinterpret_cast<char*>(data.data() + regionsStart), header[1]);
        std::string ispsStr(reinterpret_cast<char*>(data.data() + ispsStart), header[2]);
        std::vector<std::string> regions = splitString(regionsStr, '&');
        std::vector<std::string> isps = splitString(ispsStr, '&');

        // 构建地区-运营商组合
        regionIsps.resize(header[4]);
        int entryOffset = header[3];
        for (int i = 0; i < regionIsps.size(); ++i) {
            uint16_t entry = readUint16LE(data.data() + entryOffset + i * 2);
            regionIsps[i] = regions[entry >> 5] + "|" + isps[entry & 0x1F];
        }

        // 构建前缀索引表
        int pos = indexStart;
        for (int i = 0; i < PREFIX_COUNT; ++i) {
            uint32_t prefix = readUint32LE(data.data() + pos);
            if (prefix == i) {
                index[i][0] = readInt32LE(data.data() + pos + 4);
                index[i][1] = readInt32LE(data.data() + pos + 8);
                pos += 12;
            } else {
                index[i][0] = 0;
                index[i][1] = 0;
            }
        }
    }

    std::string Query(const std::string& phone) {
        if (phone.length() != 7) {
            throw std::invalid_argument("Invalid phone number length");
        }

        // 解析前缀和后四位
        int prefix = parsePhoneSegment(phone.substr(0, 3));
        int subNum = parsePhoneSegment(phone.substr(3, 4));

        if (prefix < 0 || prefix >= PREFIX_COUNT) {
            return "";
        }

        // 获取索引条目
        int bitmapOffset = index[prefix][0];
        int dataOffset = index[prefix][1];
        if (bitmapOffset == 0 || dataOffset == 0) {
            return "";
        }

        // 位图检查
        int byteIndex = subNum >> 3;
        int bitIndex = subNum & 0b0111;
        if (bitmapOffset + byteIndex >= data.size()) {
            return "";
        }

        uint8_t bitmap = data[bitmapOffset + byteIndex];
        if ((bitmap & (1 << bitIndex)) == 0) {
            return "";
        }

        // 计算有效数据位置
        int popCountOffset = bitmapOffset + BITMAP_POP_COUNT_OFFSET + (byteIndex << 1);
        uint16_t preCount = readUint16LE(data.data() + popCountOffset);
        int localCount = __builtin_popcount(bitmap & ((1 << bitIndex) - 1));

        // 定位最终数据
        int dataPos = dataOffset + ((preCount + localCount) << 1);
        uint16_t entry = readUint16LE(data.data() + dataPos);
        if (entry < regionIsps.size()) {
            return regionIsps[entry];
        }

        return "";
    }

private:
    std::vector<uint8_t> data;                     // 数据库原始数据
    std::vector<std::string> regionIsps;           // 地区-运营商组合缓存
    int index[PREFIX_COUNT][2] = { {0, 0} };       // 前缀索引表

    // 小端序读取函数
    static uint32_t readUint32LE(const uint8_t* ptr) {
        return ptr[0] | (ptr[1] << 8) | (ptr[2] << 16) | (ptr[3] << 24);
    }

    static uint16_t readUint16LE(const uint8_t* ptr) {
        return ptr[0] | (ptr[1] << 8);
    }

    static int32_t readInt32LE(const uint8_t* ptr) {
        return static_cast<int32_t>(readUint32LE(ptr));
    }

    // 字符串分割函数
    static std::vector<std::string> splitString(const std::string& str, char delimiter) {
        std::vector<std::string> result;
        std::stringstream ss(str);
        std::string item;
        while (std::getline(ss, item, delimiter)) {
            result.push_back(item);
        }
        return result;
    }

    // 将电话号码段转换为数字
    static int parsePhoneSegment(const std::string& segment) {
        int result = 0;
        for (char c : segment) {
            if (c < '0' || c > '9') {
                throw std::invalid_argument("Invalid phone segment");
            }
            result = result * 10 + (c - '0');
        }
        return result;
    }
};

// 测试主函数
int main() {
    try {
        PhoneSearch6Db& db = PhoneSearch6Db::Instance();
        db.LoadDatabase("qqzeng-phone-6.0.db");

        // 测试查询有效号码
        std::string result = db.Query("1931993");
        if (!result.empty()) {
            std::cout << "Query Result for '1234567': " << result << std::endl; //北京|北京|100000|010|110100|中国电信
        } else {
            std::cout << "No result found for '1234567'" << std::endl;
        }

        // 测试查询无效号码
        result = db.Query("9999999");
        if (!result.empty()) {
            std::cout << "Query Result for '9999999': " << result << std::endl;
        } else {
            std::cout << "No result found for '9999999'" << std::endl;
        }
    } catch (const std::exception& e) {
        std::cerr << "Error: " << e.what() << std::endl;
    }

    return 0;
}
