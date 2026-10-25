/*
 * qqzeng-ip 2.0 (.dat) 高性能解析库 - C++ 版 (C++11/17/20,跨平台)
 *
 * 文件结构 (UTF-8, Little-Endian):
 *   文件头  16 字节 (4-4-4-4): [索引区首条偏移][索引区末条偏移][前缀区首偏移][前缀区末偏移]
 *   内容区  唯一不重复的地区字符串,按 (offset,len) 被索引区引用
 *   索引区  12 字节/条 (4-4-3-1): [起始IP][结束IP][地区偏移24b][长度8b]
 *   前缀区   9 字节/条 (1-4-4): [前缀0-255][索引区start][索引区end]
 *
 * 线程安全: 初始化完成后只读,可多线程并发查询;
 *           instance() 内部 std::call_once 保证初始化竞争安全。
 */

#ifndef QQZENG_IP_IPSEARCH_H
#define QQZENG_IP_IPSEARCH_H

#include <cstddef>
#include <cstdint>
#include <string>
#include <vector>

class IPSearch
{
public:
    /* 兼容旧版: 默认路径单例(自动探测,失败返回 nullptr) */
    static IPSearch* instance();

    /* 新增: 指定 .dat 路径 / 内存字节流构造,可同时加载国内版与全球版。
     * 解析失败抛出 std::runtime_error(fail-closed)。 */
    explicit IPSearch(const std::string& dbPath);
    explicit IPSearch(const std::uint8_t* bytes, std::size_t len);
    ~IPSearch();

    IPSearch(const IPSearch&) = delete;
    IPSearch& operator=(const IPSearch&) = delete;

    /* 查询: 未命中/非法 IP 返回 "" (修复旧版 return NULL 构造 std::string 的 UB) */
    std::string Query(const char* ip) const;
    std::string Query(const std::string& ip) const;

    /* 零拷贝高性能路径: 返回内部字符串引用(生命周期同本对象,勿并发析构) */
    const std::string& findUint(std::uint32_t ipNum) const;

    /* IP 字符串严格解析: 合法返回 true 并写入 out */
    static bool ipToLong(const char* ip, std::uint32_t& out);

private:
    IPSearch(); /* 默认路径 */
    void load(const std::uint8_t* data, std::size_t len);

    std::vector<std::uint32_t> starts_;   /* 记录起始 IP */
    std::vector<std::uint32_t> ends_;     /* 记录结束 IP */
    std::vector<std::uint32_t> strIdx_;   /* 记录 -> strings_ 下标 */
    std::vector<std::string>  strings_;   /* 去重后的地区字符串 */
    std::int32_t prefMap_[256][2];        /* 前缀 -> [start,end],-1 表示缺失 */
};

#endif /* QQZENG_IP_IPSEARCH_H */
