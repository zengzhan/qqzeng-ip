/*
 * qqzeng-ip 2.0 (.dat) 高性能解析库 - C++ 实现
 *
 * 相比历史版本的修复与优化:
 *   1. Query() 未命中由 return NULL(构造 std::string(nullptr) 未定义行为)改为 return ""。
 *   2. 移除 ReadInt32/ReadInt24 中的 static 局部变量,多线程并发绝对安全。
 *   3. ipToLong 重写为单次扫描零分配的严格位运算解析(修复旧版 char ips[3]
 *      溢出与无 NUL 终止 bug)。
 *   4. 移除 stdafx.h / sys/timeb.h Windows 耦合,C++11 起跨平台编译。
 *   5. 全局 prefix_map c1(std::map) 收编为成员变量,并用固定 256 长度数组替代;
 *      前缀缺失以 -1 标记(修复 "0.0.0.0/8 low=high=0 被判未命中" 的 bug)。
 *   6. 字符串池去重: 全球版 137 万条记录仅 16 万个唯一字符串,去重后常驻,
 *      消除百万次字符串分配。
 *   7. 加载时完整校验文件结构,非法文件抛异常(fail-closed)。
 */

#include "IPLocator.h"

#include <cstdio>
#include <mutex>
#include <stdexcept>
#include <unordered_map>

namespace
{

inline std::uint32_t readLE32(const std::uint8_t* b)
{
    return static_cast<std::uint32_t>(b[0]) | (static_cast<std::uint32_t>(b[1]) << 8) |
           (static_cast<std::uint32_t>(b[2]) << 16) | (static_cast<std::uint32_t>(b[3]) << 24);
}

const char* const kDefaultPaths[] = {
    "qqzeng-ip-utf8.dat",
    "qqzeng-ip.dat",
    "qqzeng-ip-china-utf8.dat",
    "qqzeng-ip-global-utf8.dat",
    "dada/国内精华版-202610/qqzeng-ip-china-utf8.dat",
    "dada/全球旗舰版-202610/qqzeng-ip-global-utf8.dat",
    "../dada/国内精华版-202610/qqzeng-ip-china-utf8.dat",
    "../dada/全球旗舰版-202610/qqzeng-ip-global-utf8.dat",
    "../../dada/国内精华版-202610/qqzeng-ip-china-utf8.dat",
    "../../dada/全球旗舰版-202610/qqzeng-ip-global-utf8.dat",
    nullptr};

std::vector<std::uint8_t> readFile(const std::string& path)
{
    std::FILE* fp = std::fopen(path.c_str(), "rb");
    if (fp == nullptr)
        throw std::runtime_error("cannot open database: " + path);
    std::fseek(fp, 0, SEEK_END);
    long sz = std::ftell(fp);
    std::fseek(fp, 0, SEEK_SET);
    if (sz < 16)
    {
        std::fclose(fp);
        throw std::runtime_error("database too small: " + path);
    }
    std::vector<std::uint8_t> buf(static_cast<std::size_t>(sz));
    if (std::fread(buf.data(), 1, buf.size(), fp) != buf.size())
    {
        std::fclose(fp);
        throw std::runtime_error("read failed: " + path);
    }
    std::fclose(fp);
    return buf;
}

const std::string kEmpty;

} // namespace

IPSearch::IPSearch()
{
    std::string err;
    for (int i = 0; kDefaultPaths[i] != nullptr; i++)
    {
        try
        {
            std::vector<std::uint8_t> buf = readFile(kDefaultPaths[i]);
            load(buf.data(), buf.size());
            return;
        }
        catch (const std::exception& e)
        {
            err = e.what();
        }
    }
    throw std::runtime_error("IPSearch: no loadable .dat found (" + err + ")");
}

IPSearch::IPSearch(const std::string& dbPath)
{
    std::vector<std::uint8_t> buf = readFile(dbPath);
    load(buf.data(), buf.size());
}

IPSearch::IPSearch(const std::uint8_t* bytes, std::size_t len)
{
    if (bytes == nullptr || len < 16)
        throw std::runtime_error("IPSearch: invalid byte stream");
    load(bytes, len);
}

IPSearch::~IPSearch() {}

void IPSearch::load(const std::uint8_t* data, std::size_t len)
{
    for (int i = 0; i < 256; i++)
    {
        prefMap_[i][0] = -1;
        prefMap_[i][1] = -1;
    }

    const std::uint32_t firstIndex = readLE32(data);
    const std::uint32_t lastIndex = readLE32(data + 4);
    const std::uint32_t prefStart = readLE32(data + 8);
    const std::uint32_t prefEnd = readLE32(data + 12);

    if (firstIndex < 16 || lastIndex < firstIndex || (lastIndex - firstIndex) % 12u != 0u)
        throw std::runtime_error("bad index header");
    if (prefEnd < prefStart || prefStart < 16 || (prefEnd - prefStart) % 9u != 0u)
        throw std::runtime_error("bad prefix header");

    const std::uint32_t recordCount = (lastIndex - firstIndex) / 12u + 1u;
    const std::uint32_t prefixCount = (prefEnd - prefStart) / 9u + 1u;
    if (static_cast<std::size_t>(firstIndex) + 12u * recordCount > len ||
        static_cast<std::size_t>(prefStart) + 9u * prefixCount > len)
        throw std::runtime_error("header exceeds file size");

    for (std::uint32_t k = 0; k < prefixCount; k++)
    {
        const std::uint8_t* e = data + prefStart + k * 9u;
        const std::uint32_t n = e[0];
        const std::uint32_t lo = readLE32(e + 1);
        const std::uint32_t hi = readLE32(e + 5);
        if (lo > hi || hi >= recordCount)
            throw std::runtime_error("bad prefix range");
        prefMap_[n][0] = static_cast<std::int32_t>(lo);
        prefMap_[n][1] = static_cast<std::int32_t>(hi);
    }

    starts_.resize(recordCount);
    ends_.resize(recordCount);
    strIdx_.resize(recordCount);

    /* 字符串池去重: key = offset<<8 | len (offset<2^24, len<2^8) */
    std::unordered_map<std::uint32_t, std::uint32_t> seen;
    seen.reserve(recordCount / 2 + 16);
    for (std::uint32_t i = 0; i < recordCount; i++)
    {
        const std::uint8_t* r = data + firstIndex + i * 12u;
        const std::uint32_t off = static_cast<std::uint32_t>(r[8]) |
                                  (static_cast<std::uint32_t>(r[9]) << 8) |
                                  (static_cast<std::uint32_t>(r[10]) << 16);
        const std::uint32_t ln = r[11];
        if (static_cast<std::size_t>(off) + ln > len)
            throw std::runtime_error("string region out of range");
        starts_[i] = readLE32(r);
        ends_[i] = readLE32(r + 4);
        const std::uint32_t key = (off << 8) | ln;
        auto it = seen.find(key);
        if (it == seen.end())
        {
            const std::uint32_t id = static_cast<std::uint32_t>(strings_.size());
            strings_.emplace_back(reinterpret_cast<const char*>(data) + off, ln);
            seen.emplace(key, id);
            strIdx_[i] = id;
        }
        else
        {
            strIdx_[i] = it->second;
        }
    }
}

IPSearch* IPSearch::instance()
{
    static IPSearch* inst = nullptr;
    static std::once_flag once;
    std::call_once(once, []() {
        try
        {
            inst = new IPSearch();
        }
        catch (...)
        {
            inst = nullptr;
        }
    });
    return inst;
}

bool IPSearch::ipToLong(const char* ip, std::uint32_t& out)
{
    if (ip == nullptr)
        return false;
    std::uint32_t val = 0, result = 0;
    int shift = 24, digits = 0;
    for (const unsigned char* p = reinterpret_cast<const unsigned char*>(ip); *p; ++p)
    {
        const unsigned char c = *p;
        if (c >= '0' && c <= '9')
        {
            val = val * 10u + static_cast<std::uint32_t>(c - '0');
            if (val > 255u)
                return false;
            digits++;
            if (digits > 3)
                return false; /* 拒绝 "0008.8.8.8" 这类超长前导零 */
        }
        else if (c == '.')
        {
            if (digits == 0 || shift == 0)
                return false; /* 空段或第 5 段 */
            result |= val << shift;
            val = 0;
            digits = 0;
            shift -= 8;
        }
        else
        {
            return false;
        }
    }
    if (digits == 0 || shift != 0)
        return false;
    out = result | val;
    return true;
}

const std::string& IPSearch::findUint(std::uint32_t val) const
{
    const std::int32_t lo = prefMap_[(val >> 24) & 0xFFu][0];
    if (lo < 0)
        return kEmpty; /* 前缀不存在(不能以 high==0 判未命中) */
    const std::int32_t hi = prefMap_[(val >> 24) & 0xFFu][1];

    std::uint32_t idx;
    if (lo == hi)
    {
        idx = static_cast<std::uint32_t>(lo);
    }
    else
    {
        /* 二分逼近: 最小的 endip >= val */
        std::uint32_t l = static_cast<std::uint32_t>(lo), h = static_cast<std::uint32_t>(hi);
        idx = 0;
        while (l <= h)
        {
            const std::uint32_t mid = l + ((h - l) >> 1);
            if (ends_[mid] >= val)
            {
                idx = mid;
                if (mid == 0)
                    break;
                h = mid - 1u;
            }
            else
            {
                l = mid + 1u;
            }
        }
    }
    if (starts_[idx] <= val && ends_[idx] >= val)
        return strings_[strIdx_[idx]];
    return kEmpty;
}

std::string IPSearch::Query(const char* ip) const
{
    std::uint32_t v;
    if (!ipToLong(ip, v))
        return std::string();
    return findUint(v);
}

std::string IPSearch::Query(const std::string& ip) const
{
    return Query(ip.c_str());
}
