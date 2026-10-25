// Package ipdb 实现 qqzeng-ip 6.0 (.db) 高性能解析 (Go 版, 零依赖)。
//
// 文件结构 (UTF-8):
//
//	文件头    4 字节: 节点数量 nodeCount (uint32 小端)
//	一级索引  65536 条 x 3 字节 (大端 int24): IPv4 前 16 位 -> 节点号或叶子
//	节点区    nodeCount x 6 字节 (每节点 2 个子指针, 各 3 字节大端 int24)
//	字符串区  其余部分, '\t' 分隔的 UTF-8 结果串
//	int24 最高位 (0x800000) 置位 = 叶子, 低 22 位为字符串下标;
//	否则为节点区跳转下标。查询消耗 IP 后 16 位 (MSB first)。
//
// 兼容旧版 API:
//
//	Instance() / Find(ip) / FindUint(prefix, suffix)
//
// 新增:
//
//	New() / NewFromPath(path) / NewFromBytes(data)
//	FindUint32(ipInt) 整数查询 / FastParseIp 导出
//
// 相比历史版本的修复与优化:
//  1. 废弃的 ioutil.ReadFile 改为 os.ReadFile。
//  2. fastParseIp 严格化: 旧版接受 "1..2.3"/"1.2.3." 等残缺 IP,
//     5 段 IP 触发负数移位 panic; 现按统一严格规则校验。
//  3. 加载期完整校验 (fail-closed): 尺寸/头字段/全部一级索引与节点跳转
//     合法性/叶子下标范围, 非法文件返回 error。
//  4. 逐查询 int24 拼装改为 []uint32 预解析, 查询路径零拼装;
//     原始字节缓冲加载后即可被 GC 回收。
//  5. 路径探测补充仓库 dada/ 目录; 单例失败可重试 (不再吞错误)。
package ipdb

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

const (
	IndexStart = 0x30004 // 节点区起始偏移
	firstCount = 65536   // 一级索引条数 = 2^16
	endMask    = 0x800000
	complMask  = 0x7FFFFF
	// DbFileName 默认数据库文件名。
	DbFileName = "qqzeng-ip-6.0-global.db"
)

// IpDbSearch 只读结构, 加载完成后可并发安全查询。
type IpDbSearch struct {
	first   []uint32 // 一级索引 (预解析)
	nodes   []uint32 // nodeCount*2 个子指针 (预解析)
	geoisp  []string // 字符串表
	nodeCnt uint32
}

var (
	instance     *IpDbSearch
	instanceOnce sync.Once
	instanceErr  error
)

// Instance 获取单例 (自动探测默认路径; 失败返回 error, 可重复调用)。
func Instance() (*IpDbSearch, error) {
	instanceOnce.Do(func() {
		instance, instanceErr = New()
	})
	return instance, instanceErr
}

// New 自动探测默认路径初始化。
func New() (*IpDbSearch, error) {
	path := FindDbPath()
	if path == "" {
		return nil, errors.New("ipdb: cannot find " + DbFileName)
	}
	return NewFromPath(path)
}

// NewFromPath 指定路径初始化。
func NewFromPath(path string) (*IpDbSearch, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, err
	}
	return NewFromBytes(data)
}

// NewFromBytes 从内存字节流初始化。
func NewFromBytes(data []byte) (*IpDbSearch, error) {
	return parse(data)
}

func parse(data []byte) (*IpDbSearch, error) {
	if len(data) < IndexStart {
		return nil, errors.New("ipdb: invalid database file size")
	}
	nodeCount := uint32(data[0]) | uint32(data[1])<<8 | uint32(data[2])<<16 | uint32(data[3])<<24
	if nodeCount == 0 {
		return nil, errors.New("ipdb: invalid node count")
	}
	nodeAreaEnd := uint64(IndexStart) + uint64(nodeCount)*6
	if nodeAreaEnd > uint64(len(data)) {
		return nil, errors.New("ipdb: node area exceeds file size")
	}
	sa := int(nodeAreaEnd)

	s := &IpDbSearch{
		first:   make([]uint32, firstCount),
		nodes:   make([]uint32, uint64(nodeCount)*2),
		nodeCnt: nodeCount,
	}

	// 字符串区: []byte -> string 一次拷贝, split 共享底层数组
	content := string(data[sa:])
	s.geoisp = strings.Split(content, "\t")
	stringCount := uint32(len(s.geoisp))

	entryOK := func(v uint32) bool {
		if v&endMask != 0 {
			return v&complMask < stringCount
		}
		return v < nodeCount
	}

	// 一级索引预解析 + 校验
	for i := 0; i < firstCount; i++ {
		o := 4 + i*3
		v := uint32(data[o])<<16 | uint32(data[o+1])<<8 | uint32(data[o+2])
		if !entryOK(v) {
			return nil, errors.New("ipdb: bad first-level index entry")
		}
		s.first[i] = v
	}

	// 节点区预解析 + 跳转合法性校验 (fail-closed)
	for n := uint32(0); n < nodeCount; n++ {
		o := IndexStart + int(n)*6
		a := uint32(data[o])<<16 | uint32(data[o+1])<<8 | uint32(data[o+2])
		b := uint32(data[o+3])<<16 | uint32(data[o+4])<<8 | uint32(data[o+5])
		if !entryOK(a) || !entryOK(b) {
			return nil, errors.New("ipdb: bad node jump")
		}
		s.nodes[n*2] = a
		s.nodes[n*2+1] = b
	}
	return s, nil
}

// Find 查询 IP 字符串, 未命中/非法返回 ""。
func (s *IpDbSearch) Find(ip string) string {
	v, ok := FastParseIp(ip)
	if !ok {
		return ""
	}
	return s.FindUint32(v)
}

// FindUint 兼容旧版: (prefix, suffix) = (前 16 位, 后 16 位)。
func (s *IpDbSearch) FindUint(prefix, suffix uint16) string {
	return s.FindUint32(uint32(prefix)<<16 | uint32(suffix))
}

// FindUint32 整数查询 (跳过字符串解析, 高性能场景)。
func (s *IpDbSearch) FindUint32(ipInt uint32) string {
	record := s.first[ipInt>>16]
	suffix := ipInt & 0xFFFF
	hops := 0
	// 后缀 16 位 -> 最多 16 跳; 超限说明文件非法 (fail-closed)
	for record&endMask == 0 {
		if hops >= 16 {
			return ""
		}
		hops++
		record = s.nodes[record*2+(suffix>>15)&1]
		suffix <<= 1
	}
	idx := record & complMask
	if idx < uint32(len(s.geoisp)) {
		return s.geoisp[idx]
	}
	return ""
}

// NodeCount / StringCount 调试辅助。
func (s *IpDbSearch) NodeCount() int   { return int(s.nodeCnt) }
func (s *IpDbSearch) StringCount() int { return len(s.geoisp) }

// FastParseIp 严格 IPv4 解析: 每段 1~3 位数字且 <=255, 恰好 4 段。
// 非法返回 (0, false)。
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
			result |= val << shift
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

// FindDbPath 探测默认数据库路径 (当前目录/data/仓库 dada//可执行文件目录)。
func FindDbPath() string {
	var attempts []string
	for _, d := range []string{
		".", "data", "../data", "../../data", "../../../data",
		"dada/全球旗舰版-202610", "../dada/全球旗舰版-202610",
		"../../dada/全球旗舰版-202610", "../../../dada/全球旗舰版-202610",
	} {
		attempts = append(attempts, filepath.Join(d, DbFileName))
	}
	if exe, err := os.Executable(); err == nil {
		dir := filepath.Dir(exe)
		attempts = append(attempts,
			filepath.Join(dir, DbFileName),
			filepath.Join(dir, "../data", DbFileName))
	}
	for _, p := range attempts {
		if st, err := os.Stat(p); err == nil && !st.IsDir() {
			return p
		}
	}
	return ""
}
