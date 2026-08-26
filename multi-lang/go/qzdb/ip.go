package qzdb

import "strings"

// hexLUT 用于快速十六进制数字判定。
var hexLUT [128]byte

func init() {
	for i := 0; i < 10; i++ {
		hexLUT[48+i] = byte(i) // '0'-'9'
	}
	for i := 0; i < 6; i++ {
		hexLUT[97+i] = byte(10 + i) // 'a'-'f'
		hexLUT[65+i] = byte(10 + i) // 'A'-'F'
	}
}

func isV4Mapped(b [16]byte) bool {
	for i := 0; i < 10; i++ {
		if b[i] != 0 {
			return false
		}
	}
	return b[10] == 0xFF && b[11] == 0xFF
}

func v4FromMapped(b [16]byte) uint32 {
	return uint32(b[12])<<24 | uint32(b[13])<<16 | uint32(b[14])<<8 | uint32(b[15])
}

type parseResult struct {
	v4   uint32
	v6   [16]byte
	isV4 bool
}

// fastParseIp 严格解析 IPv4/IPv6 字符串（拒绝前导零、越界、缺段、超长、zone-id、非法分组）。
// 对 IPv4-mapped IPv6 自动降级为 IPv4。空白字符一律拒绝（SSRF 安全）。
func fastParseIp(s string) (*parseResult, bool) {
	n := len(s)
	// 空白字符一律拒绝（SSRF 安全）
	if n > 0 && strings.IndexAny(s, " \t\n\r\v\f") >= 0 {
		return nil, false
	}
	if n == 0 || n > 45 {
		return nil, false
	}
	if !strings.Contains(s, ":") {
		v4, ok := fastParseIpv4(s)
		if !ok {
			return nil, false
		}
		return &parseResult{v4: v4, isV4: true}, true
	}
	if strings.IndexByte(s, '%') >= 0 {
		return nil, false // zone-id 不支持
	}
	// 用 strings.Cut 处理 "::" 双冒号压缩；拒绝多个 "::"
	lft, rgt, hasGap := strings.Cut(s, "::")
	if hasGap && strings.Contains(rgt, "::") {
		return nil, false // 多个 "::"
	}
	lg := strings.Split(lft, ":")
	rg := strings.Split(rgt, ":")
	if lft == "" {
		lg = nil
	}
	if rgt == "" {
		rg = nil
	}
	for _, g := range lg {
		if g == "" {
			return nil, false
		}
	}
	for _, g := range rg {
		if g == "" {
			return nil, false
		}
	}
	allg := make([]string, 0, len(lg)+len(rg))
	allg = append(allg, lg...)
	allg = append(allg, rg...)
	hasV4 := false
	var v4Int uint32
	if len(allg) > 0 {
		last := allg[len(allg)-1]
		if !strings.Contains(last, ":") && strings.IndexByte(last, '.') >= 0 {
			v, ok := fastParseIpv4(last)
			if !ok {
				return nil, false
			}
			v4Int = v
			hasV4 = true
			allg = allg[:len(allg)-1]
		}
	}
	ng := len(allg)
	v4Slots := 0
	if hasV4 {
		v4Slots = 2
	}
	if hasGap {
		if ng+v4Slots > 7 {
			return nil, false
		}
	} else if ng+v4Slots != 8 {
		return nil, false
	}
	// 内嵌 IPv4 必须位于地址末尾（最后 32 位）。若带 "::" 压缩且 v4 落在 "::" 之前
	// （rgt 为空，即 "a.b.c.d::" 形态），属于非法地址，netip 同样拒绝，这里显式拒绝。
	if hasV4 && hasGap && len(rg) == 0 {
		return nil, false
	}
	for _, g := range allg {
		gl := len(g)
		if gl == 0 || gl > 4 {
			return nil, false
		}
		for j := 0; j < gl; j++ {
			cc := g[j]
			if cc >= 128 || (hexLUT[cc] == 0 && cc != '0') {
				return nil, false
			}
		}
	}
	zeros := 8 - ng - v4Slots
	var buf [16]byte
	off := 0
	for _, g := range lg {
		v := parseHexGroup(g)
		buf[off] = byte(v >> 8)
		buf[off+1] = byte(v)
		off += 2
	}
	off += zeros * 2
	for _, g := range rg {
		v := parseHexGroup(g)
		buf[off] = byte(v >> 8)
		buf[off+1] = byte(v)
		off += 2
	}
	if hasV4 {
		buf[12] = byte(v4Int >> 24)
		buf[13] = byte(v4Int >> 16)
		buf[14] = byte(v4Int >> 8)
		buf[15] = byte(v4Int)
	}
	if isV4Mapped(buf) {
		return &parseResult{v4: v4FromMapped(buf), isV4: true}, true
	}
	return &parseResult{v6: buf}, true
}

func fastParseIpv4(s string) (uint32, bool) {
	n := len(s)
	if n == 0 || s[n-1] == '.' {
		return 0, false
	}
	var result, val uint32
	dots, start := 0, 0
	for i := 0; i <= n; i++ {
		c := byte('.')
		if i < n {
			c = s[i]
		}
		if c == '.' {
			segLen := i - start
			if segLen == 0 || segLen > 3 {
				return 0, false
			}
			if segLen > 1 && s[start] == '0' { // 拒绝前导零
				return 0, false
			}
			val = 0
			for j := start; j < i; j++ {
				d := s[j]
				if d < '0' || d > '9' {
					return 0, false
				}
				val = val*10 + uint32(d-'0')
			}
			if val > 255 {
				return 0, false
			}
			result = (result << 8) | val
			dots++
			start = i + 1
		}
	}
	if dots != 4 {
		return 0, false
	}
	return result, true
}

// ---------- 小工具 ----------

func splitColon(s string) []string {
	if s == "" {
		return nil
	}
	var parts []string
	start := 0
	for i := 0; i <= len(s); i++ {
		if i == len(s) || s[i] == ':' {
			parts = append(parts, s[start:i])
			start = i + 1
		}
	}
	return parts
}

func parseHexGroup(g string) uint16 {
	var v uint16
	for i := 0; i < len(g); i++ {
		v = (v << 4) | uint16(hexLUT[g[i]])
	}
	return v
}
