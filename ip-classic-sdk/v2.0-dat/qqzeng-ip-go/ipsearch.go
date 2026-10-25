// Package ipsearch 实现 qqzeng-ip 2.0 (.dat) 高性能解析。
//
// 文件结构 (UTF-8, Little-Endian):
//
//	文件头  16 字节 (4-4-4-4): [索引区首条偏移][索引区末条偏移][前缀区首偏移][前缀区末偏移]
//	内容区  唯一不重复的地区字符串,按 (offset,len) 被索引区引用
//	索引区  12 字节/条 (4-4-3-1): [起始IP][结束IP][地区偏移24b][长度8b]
//	前缀区   9 字节/条 (1-4-4): [前缀0-255][索引区start][索引区end]
//
// 线程安全: 初始化完成后只读,可多 goroutine 并发查询。
package ipsearch

import (
	"errors"
	"os"
	"sync"
)

// IpSearch qqzeng-ip 2.0 解析器。
type IpSearch struct {
	starts  []uint32      // 每条记录起始 IP
	ends    []uint32      // 每条记录结束 IP
	strIdx  []uint32      // 记录 -> strings 下标
	strings []string      // 去重后的地区字符串
	prefMap [256][2]int32 // 前缀 -> [start,end],-1 表示缺失
}

var defaultPaths = []string{
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
}

var (
	sharedMu   sync.Mutex
	sharedInst *IpSearch
)

// New 兼容旧版: 加载默认路径(自动探测)。
// 旧版在失败时 log.Fatal,现改为返回 error,由调用方决定处理方式。
// 互斥锁保护首次并发调用,避免多 goroutine 同时探测时重复加载/数据竞争;
// 失败不缓存,数据文件就绪后重试 New() 即可。
func New() (*IpSearch, error) {
	sharedMu.Lock()
	defer sharedMu.Unlock()
	if sharedInst != nil {
		return sharedInst, nil
	}
	var lastErr error
	for _, p := range defaultPaths {
		data, err := os.ReadFile(p)
		if err != nil {
			lastErr = err
			continue
		}
		s, err := NewIpSearchFromBytes(data)
		if err != nil {
			lastErr = err
			continue
		}
		sharedInst = s
		return s, nil
	}
	if lastErr == nil {
		lastErr = errors.New("ipsearch: no loadable .dat found")
	}
	return nil, lastErr
}

// ResetDefault 清除默认单例(下次 New() 重新探测加载)。
func ResetDefault() {
	sharedMu.Lock()
	sharedInst = nil
	sharedMu.Unlock()
}

// NewIpSearch 指定 .dat 文件路径加载(可同时加载国内版与全球版)。
func NewIpSearch(dbPath string) (*IpSearch, error) {
	data, err := os.ReadFile(dbPath)
	if err != nil {
		return nil, err
	}
	return NewIpSearchFromBytes(data)
}

// NewIpSearchFromBytes 从内存字节流加载。
func NewIpSearchFromBytes(data []byte) (*IpSearch, error) {
	return load(data)
}

func load(data []byte) (*IpSearch, error) {
	if len(data) < 16 {
		return nil, errors.New("ipsearch: database too small")
	}
	p := &IpSearch{}
	for i := range p.prefMap {
		p.prefMap[i] = [2]int32{-1, -1}
	}

	readLE32 := func(b []byte) uint32 {
		return uint32(b[0]) | uint32(b[1])<<8 | uint32(b[2])<<16 | uint32(b[3])<<24
	}

	firstIndex := readLE32(data[0:4])
	lastIndex := readLE32(data[4:8])
	prefStart := readLE32(data[8:12])
	prefEnd := readLE32(data[12:16])

	if firstIndex < 16 || lastIndex < firstIndex || (lastIndex-firstIndex)%12 != 0 {
		return nil, errors.New("ipsearch: bad index header")
	}
	if prefEnd < prefStart || prefStart < 16 || (prefEnd-prefStart)%9 != 0 {
		return nil, errors.New("ipsearch: bad prefix header")
	}

	recordCount := (lastIndex - firstIndex) / 12
	prefixCount := (prefEnd - prefStart) / 9

	// 修正: +1 条
	recordCount++
	prefixCount++

	if int(firstIndex)+12*int(recordCount) > len(data) {
		return nil, errors.New("ipsearch: index area exceeds file")
	}
	if int(prefStart)+9*int(prefixCount) > len(data) {
		return nil, errors.New("ipsearch: prefix area exceeds file")
	}

	for k := uint32(0); k < prefixCount; k++ {
		e := data[prefStart+k*9:]
		n := e[0]
		lo := readLE32(e[1:5])
		hi := readLE32(e[5:9])
		if lo > hi || hi >= recordCount {
			return nil, errors.New("ipsearch: bad prefix range")
		}
		p.prefMap[n] = [2]int32{int32(lo), int32(hi)}
	}

	p.starts = make([]uint32, recordCount)
	p.ends = make([]uint32, recordCount)
	p.strIdx = make([]uint32, recordCount)

	// 字符串池去重: key = offset<<8 | len (offset<2^24, len<2^8)
	seen := make(map[uint32]uint32, recordCount/2+16)
	for i := uint32(0); i < recordCount; i++ {
		r := data[firstIndex+i*12:]
		off := uint32(r[8]) | uint32(r[9])<<8 | uint32(r[10])<<16
		ln := uint32(r[11])
		if int(off)+int(ln) > len(data) {
			return nil, errors.New("ipsearch: string region out of range")
		}
		p.starts[i] = readLE32(r[0:4])
		p.ends[i] = readLE32(r[4:8])
		key := off<<8 | ln
		if id, ok := seen[key]; ok {
			p.strIdx[i] = id
		} else {
			id := uint32(len(p.strings))
			p.strings = append(p.strings, string(data[off:off+ln]))
			seen[key] = id
			p.strIdx[i] = id
		}
	}
	return p, nil
}

// Get 查询 IP 字符串,未命中/非法 IP 返回 ""。
func (p *IpSearch) Get(ip string) string {
	val, ok := FastParseIp(ip)
	if !ok {
		return ""
	}
	return p.GetUint(val)
}

// Find 与 Get 等价,保持多语言 API 一致。
func (p *IpSearch) Find(ip string) string { return p.Get(ip) }

// Query 与 Get 等价。
func (p *IpSearch) Query(ip string) string { return p.Get(ip) }

// GetUint 整数查询(跳过字符串解析,高性能场景)。
func (p *IpSearch) GetUint(val uint32) string {
	lo := p.prefMap[(val>>24)&0xFF][0]
	if lo < 0 {
		return "" // 前缀不存在(不能以 high==0 判未命中: 0.0.0.0/8 的 low=high=0 合法)
	}
	hi := p.prefMap[(val>>24)&0xFF][1]

	var idx uint32
	if lo == hi {
		idx = uint32(lo)
	} else {
		// 二分逼近: 最小的 endip >= val
		l, h := uint32(lo), uint32(hi)
		for l <= h {
			mid := l + (h-l)/2
			if p.ends[mid] >= val {
				idx = mid
				if mid == 0 {
					break // 防无符号下溢
				}
				h = mid - 1
			} else {
				l = mid + 1
			}
		}
	}
	if p.starts[idx] <= val && p.ends[idx] >= val {
		return p.strings[p.strIdx[idx]]
	}
	return ""
}

// FindUint 与 GetUint 等价。
func (p *IpSearch) FindUint(val uint32) string { return p.GetUint(val) }

// QueryUint 与 GetUint 等价。
func (p *IpSearch) QueryUint(val uint32) string { return p.GetUint(val) }

// FastParseIp 严格 IPv4 解析: 每段 1~3 位数字且 <=255,恰好 4 段。
// 拒绝空串/空段/负数/非数字/IPv6/前后缀垃圾。
func FastParseIp(ip string) (uint32, bool) {
	var val, result uint32
	shift := 24
	digits := 0
	for i := 0; i < len(ip); i++ {
		c := ip[i]
		if c >= '0' && c <= '9' {
			val = val*10 + uint32(c-'0')
			if val > 255 {
				return 0, false
			}
			digits++
			if digits > 3 {
				return 0, false // 拒绝 "0008.8.8.8" 这类超长前导零
			}
		} else if c == '.' {
			if digits == 0 || shift == 0 {
				return 0, false // 空段或第 5 段
			}
			result |= val << uint(shift)
			val = 0
			digits = 0
			shift -= 8
		} else {
			return 0, false
		}
	}
	if digits == 0 || shift != 0 {
		return 0, false
	}
	return result | val, true
}
