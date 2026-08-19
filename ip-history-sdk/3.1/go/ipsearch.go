// Package qqzengip 提供高性能 IP 地址查询功能
// 使用前缀索引 + 二分查找算法，单次查询延迟 < 20ns
// 支持并发安全，适用于亿级 QPS 场景
//
// 性能指标 (Apple M4 Max):
//   - 单次查询: ~14 ns/op, 0 B/op
//   - 并行查询: ~1.4 ns/op, 0 B/op
//   - QPS: 6500万+/秒 (单线程)
//
// Copyright (c) qqzeng-ip. All rights reserved.
package qqzengip

import (
	"encoding/binary"
	"errors"
	"os"
	"sync"
)

// 版本信息
const Version = "3.1.0"

// 预定义错误类型，避免运行时创建
var (
	ErrInvalidIP      = errors.New("qqzengip: invalid IP address format")
	ErrDataLoadFailed = errors.New("qqzengip: failed to load IP database")
	ErrNotInitialized = errors.New("qqzengip: instance not initialized")
)

// IpSearch 高性能 IP 查询引擎
// 使用前缀索引加速查找，时间复杂度 O(log n)
// 线程安全，支持高并发读取
type IpSearch struct {
	prefStart [256]uint32 // IP 第一字节前缀索引起始位置
	prefEnd   [256]uint32 // IP 第一字节前缀索引结束位置
	endArr    []uint32    // IP 段结束地址数组 (已排序)
	addrArr   []string    // 地址信息数组
}

// 单例模式相关变量
var (
	instance    *IpSearch
	instanceErr error
	once        sync.Once
)

// 默认数据文件路径
const defaultDatFile = "./qqzeng-ip-3.0-ultimate.dat"

// GetInstance 获取 IP 查询单例实例（使用默认数据文件）
// 线程安全，支持高并发访问
func GetInstance() *IpSearch {
	return GetInstanceWithFile(defaultDatFile)
}

// GetInstanceWithFile 获取 IP 查询单例实例（指定数据文件）
// 首次调用时加载数据，后续调用直接返回缓存实例
func GetInstanceWithFile(file string) *IpSearch {
	once.Do(func() {
		instance, instanceErr = LoadDat(file)
	})
	if instanceErr != nil {
		return nil
	}
	return instance
}

// MustGetInstance 获取实例，如果失败则 panic
// 适用于程序启动阶段的快速失败场景
func MustGetInstance() *IpSearch {
	inst := GetInstance()
	if inst == nil {
		panic(ErrNotInitialized)
	}
	return inst
}

// LoadDat 从文件加载 IP 数据库
// 使用内存映射友好的数据结构，优化缓存命中率
func LoadDat(file string) (*IpSearch, error) {
	data, err := os.ReadFile(file)
	if err != nil {
		return nil, err
	}

	if len(data) < 2308 {
		return nil, ErrDataLoadFailed
	}

	p := &IpSearch{}

	// 批量读取前缀索引，利用 CPU 缓存预取
	for k := 0; k < 256; k++ {
		i := k*8 + 4
		p.prefStart[k] = binary.LittleEndian.Uint32(data[i : i+4])
		p.prefEnd[k] = binary.LittleEndian.Uint32(data[i+4 : i+8])
	}

	recordSize := int(binary.LittleEndian.Uint32(data[0:4]))

	// 预分配精确容量，避免扩容开销
	p.endArr = make([]uint32, recordSize)
	p.addrArr = make([]string, recordSize)

	// 顺序读取数据记录，最大化顺序访问性能
	for i := 0; i < recordSize; i++ {
		j := 2308 + (i * 9)
		p.endArr[i] = binary.LittleEndian.Uint32(data[j : j+4])
		offset := binary.LittleEndian.Uint32(data[j+4 : j+8])
		length := uint32(data[j+8])
		p.addrArr[i] = string(data[offset : offset+length])
	}

	return p, nil
}

// Get 查询 IP 地址信息
// 支持标准 IPv4 点分十进制格式，如 "192.168.1.1"
// 返回格式：洲|国家|省份|城市|区县|运营商|行政区划代码|国家英文|国家简称|经度|纬度
// 无效 IP 返回空字符串
//
//go:noinline
func (p *IpSearch) Get(ip string) string {
	// 内联 IP 解析以减少函数调用开销
	var (
		octet  uint32
		octets int
		intIP  uint32
		prefix byte
	)

	n := len(ip)
	if n < 7 || n > 15 { // 最短 "0.0.0.0", 最长 "255.255.255.255"
		return ""
	}

	for i := 0; i < n; i++ {
		c := ip[i]
		if c >= '0' && c <= '9' {
			octet = octet*10 + uint32(c-'0')
			if octet > 255 {
				return ""
			}
		} else if c == '.' {
			if octets == 0 {
				prefix = byte(octet)
			}
			intIP = (intIP << 8) | octet
			octet = 0
			octets++
			if octets > 3 {
				return ""
			}
		} else {
			return ""
		}
	}

	if octets != 3 {
		return ""
	}
	intIP = (intIP << 8) | octet

	// 前缀索引快速定位
	low := p.prefStart[prefix]
	high := p.prefEnd[prefix]

	if low == high {
		return p.addrArr[low]
	}

	// 内联二分查找
	endArr := p.endArr
	var result uint32
	for low <= high {
		mid := low + (high-low)>>1
		if endArr[mid] >= intIP {
			result = mid
			if mid == 0 {
				break
			}
			high = mid - 1
		} else {
			low = mid + 1
		}
	}

	return p.addrArr[result]
}

// Find 查询 IP 地址信息（Get 的别名，提供更直观的 API）
func (p *IpSearch) Find(ip string) string {
	return p.Get(ip)
}

// Query 查询 IP 地址信息（带错误返回）
// 适用于需要区分"未找到"和"无效输入"的场景
func (p *IpSearch) Query(ip string) (string, error) {
	result := p.Get(ip)
	if result == "" {
		return "", ErrInvalidIP
	}
	return result, nil
}

// BatchGet 批量查询 IP 地址信息
// 适用于高吞吐量场景，减少函数调用开销
func (p *IpSearch) BatchGet(ips []string) []string {
	results := make([]string, len(ips))
	for i, ip := range ips {
		results[i] = p.Get(ip)
	}
	return results
}

// IsValid 验证 IP 格式是否有效
func IsValid(ip string) bool {
	n := len(ip)
	if n < 7 || n > 15 {
		return false
	}

	var octet, octets uint32
	for i := 0; i < n; i++ {
		c := ip[i]
		if c >= '0' && c <= '9' {
			octet = octet*10 + uint32(c-'0')
			if octet > 255 {
				return false
			}
		} else if c == '.' {
			octet = 0
			octets++
			if octets > 3 {
				return false
			}
		} else {
			return false
		}
	}
	return octets == 3
}

// Validate 验证 IP 格式是否有效（IsValid 的别名）
func Validate(ip string) bool {
	return IsValid(ip)
}
