package qzdb

import (
	"strconv"
	"strings"
)

// ---------- CIDR 前缀长度重建（叶子深度 = 前缀位数） ----------

func (s *Snapshot) lookupV4PrefixLen(ip uint32) int {
	if !s.hasV4 || s.offV4Jump <= 0 {
		return -1
	}
	ptr := safeReadU32(s.data, s.offV4Jump+uint64((ip>>16&0xFFFF))*4)
	if ptr == 0 {
		return -1
	}
	if ptr&SENTINEL != 0 {
		return s.walkV4Depth(ip, 0, 0, 16)
	}
	return s.walkV4Depth(ip, ptr&SENTINEL_MASK_31, 16, 32)
}

func (s *Snapshot) walkV4Depth(ip uint32, startIdx uint32, startDepth, maxDepth int) int {
	if startDepth >= maxDepth {
		return -1
	}
	idx := startIdx
	mask := s.nodeMask(true)
	for depth := startDepth; depth < maxDepth; depth++ {
		if idx >= s.v4NodeCount {
			return -1
		}
		child := s.readV4Child(idx, (ip>>(31-uint(depth)))&1)
		if child == 0 {
			return -1
		}
		if s.isLeaf(child, true) {
			return depth + 1
		}
		idx = child & mask
	}
	return -1
}

func (s *Snapshot) lookupV6PrefixLen(ip [16]byte) int {
	if !s.hasV6 || s.offV6Jump <= 0 {
		return -1
	}
	ptr := safeReadU32(s.data, s.offV6Jump+uint64(readV6Prefix(ip, s.v6JumpBits))*4)
	if ptr == 0 {
		return -1
	}
	if ptr&SENTINEL != 0 {
		return s.walkV6Depth(ip, 0, 0, s.v6JumpBits)
	}
	return s.walkV6Depth(ip, ptr&SENTINEL_MASK_31, s.v6JumpBits, 128)
}

func (s *Snapshot) walkV6Depth(ip [16]byte, startIdx uint32, startDepth, maxDepth int) int {
	if startDepth >= maxDepth {
		return -1
	}
	idx := startIdx
	mask := s.nodeMask(false)
	for depth := startDepth; depth < maxDepth; depth++ {
		if idx >= s.v6NodeCount {
			return -1
		}
		child := s.readV6Child(idx, uint32((ip[depth>>3]>>(7-uint(depth&7)))&1))
		if child == 0 {
			return -1
		}
		if s.isLeaf(child, false) {
			return depth + 1
		}
		idx = child & mask
	}
	return -1
}

// ---------- CIDR 格式化 ----------

func formatV4Cidr(ip uint32, n int) string {
	var net uint32
	if n > 0 {
		net = ip & (0xFFFFFFFF << (32 - uint(n)))
	}
	return strconv.Itoa(int((net>>24)&0xFF)) + "." +
		strconv.Itoa(int((net>>16)&0xFF)) + "." +
		strconv.Itoa(int((net>>8)&0xFF)) + "." +
		strconv.Itoa(int(net&0xFF)) + "/" + strconv.Itoa(n)
}

func formatV6Cidr(ip [16]byte, n int) string {
	net := ip
	for bit := n; bit < 128; bit++ {
		net[bit>>3] &= ^byte(1 << (7 - uint(bit&7)))
	}
	g := [8]int{}
	for i := 0; i < 8; i++ {
		g[i] = int(net[2*i])<<8 | int(net[2*i+1])
	}
	// RFC 5952：最长全零段（并列取最左），长度 ≥ 2 才压缩
	bestStart, bestLen, curStart, curLen := -1, 0, -1, 0
	for i := 0; i < 8; i++ {
		if g[i] == 0 {
			if curStart < 0 {
				curStart, curLen = i, 1
			} else {
				curLen++
			}
		} else {
			if curLen > bestLen {
				bestStart, bestLen = curStart, curLen
			}
			curStart, curLen = -1, 0
		}
	}
	if curLen > bestLen {
		bestStart, bestLen = curStart, curLen
	}

	var sb strings.Builder
	if bestLen >= 2 {
		for i := 0; i < bestStart; i++ {
			if i > 0 {
				sb.WriteByte(':')
			}
			sb.WriteString(strconv.FormatInt(int64(g[i]), 16))
		}
		sb.WriteString("::")
		for i := bestStart + bestLen; i < 8; i++ {
			if i > bestStart+bestLen {
				sb.WriteByte(':')
			}
			sb.WriteString(strconv.FormatInt(int64(g[i]), 16))
		}
	} else {
		for i := 0; i < 8; i++ {
			if i > 0 {
				sb.WriteByte(':')
			}
			sb.WriteString(strconv.FormatInt(int64(g[i]), 16))
		}
	}
	sb.WriteByte('/')
	sb.WriteString(strconv.Itoa(n))
	return sb.String()
}

// ---------- 公开 CIDR 反查 API ----------

// LookupCidr 返回包含该 IP 的最具体网段标准 CIDR（如 "1.0.1.0/24"、"2001:218::/32"）。
// 未覆盖或非法 IP 返回 ""（契约 §5：Go 返回空值）。IPv4-mapped 自动降级走 V4 Trie。
func (r *QzdbReader) LookupCidr(ipStr string) string {
	if ipStr == "" {
		return ""
	}
	s := r.snapshot()
	if s == nil {
		return ""
	}
	// 回归修复：三个 CIDR 入口此前漏了 release，每次查询净增 1 个引用计数，
	// mmap 永不释放（频繁 Reload 的服务持续泄漏虚拟内存）。
	res, ok := fastParseIp(ipStr)
	if !ok {
		return ""
	}
	if res.isV4 {
		n := s.lookupV4PrefixLen(res.v4)
		if n < 0 {
			return ""
		}
		return formatV4Cidr(res.v4, n)
	}
	if isV4Mapped(res.v6) {
		v4 := v4FromMapped(res.v6)
		n := s.lookupV4PrefixLen(v4)
		if n < 0 {
			return ""
		}
		return formatV4Cidr(v4, n)
	}
	n := s.lookupV6PrefixLen(res.v6)
	if n < 0 {
		return ""
	}
	return formatV6Cidr(res.v6, n)
}

// LookupCidrUint 返回 IPv4 uint32 的最具体网段 CIDR；未覆盖返回 ""。
func (r *QzdbReader) LookupCidrUint(ipInt uint32) string {
	s := r.snapshot()
	if s == nil {
		return ""
	}
	n := s.lookupV4PrefixLen(ipInt)
	if n < 0 {
		return ""
	}
	return formatV4Cidr(ipInt, n)
}

// LookupCidrBytes 返回 4/16 字节地址的最具体网段 CIDR；未覆盖或长度非法返回 ""。
func (r *QzdbReader) LookupCidrBytes(ip []byte) string {
	s := r.snapshot()
	if s == nil {
		return ""
	}
	if len(ip) == 16 {
		if isV4Mapped([16]byte(ip)) {
			v4 := v4FromMapped([16]byte(ip))
			n := s.lookupV4PrefixLen(v4)
			if n < 0 {
				return ""
			}
			return formatV4Cidr(v4, n)
		}
		n := s.lookupV6PrefixLen([16]byte(ip))
		if n < 0 {
			return ""
		}
		return formatV6Cidr([16]byte(ip), n)
	}
	if len(ip) == 4 {
		v4 := uint32(ip[0])<<24 | uint32(ip[1])<<16 | uint32(ip[2])<<8 | uint32(ip[3])
		n := s.lookupV4PrefixLen(v4)
		if n < 0 {
			return ""
		}
		return formatV4Cidr(v4, n)
	}
	return ""
}
