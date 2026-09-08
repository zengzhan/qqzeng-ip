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
// 值返回：21 字节结构体走寄存器/栈，热路径零堆分配。
// 对 IPv4-mapped IPv6 自动降级为 IPv4。空白字符一律拒绝（SSRF 安全）。
func fastParseIp(s string) (parseResult, bool) {
	// IPv4 极速热路径：直走快速解析，免去空白/冒号预扫描。
	// fastParseIpv4 具有 100% 严格校验，任何带空白、带符号、带字母、带冒号或越界的输入均会返回 false。
	if v4, ok := fastParseIpv4(s); ok {
		return parseResult{v4: v4, isV4: true}, true
	}
	n := len(s)
	if n == 0 || n > 45 {
		return parseResult{}, false
	}
	if !strings.Contains(s, ":") {
		return parseResult{}, false
	}
	// 空白字符一律拒绝（SSRF 安全）。
	if strings.ContainsAny(s, " \t\n\r\v\f") {
		return parseResult{}, false
	}
	if strings.IndexByte(s, '%') >= 0 {
		return parseResult{}, false // zone-id 不支持
	}
	// 用 strings.Cut 处理 "::" 双冒号压缩；拒绝多个 "::"
	lft, rgt, hasGap := strings.Cut(s, "::")
	if hasGap && strings.Contains(rgt, "::") {
		return parseResult{}, false // 多个 "::"
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
			return parseResult{}, false
		}
	}
	for _, g := range rg {
		if g == "" {
			return parseResult{}, false
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
				return parseResult{}, false
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
			return parseResult{}, false
		}
	} else if ng+v4Slots != 8 {
		return parseResult{}, false
	}
	// 内嵌 IPv4 必须位于地址末尾（最后 32 位）。若带 "::" 压缩且 v4 落在 "::" 之前
	// （rgt 为空，即 "a.b.c.d::" 形态），属于非法地址，netip 同样拒绝，这里显式拒绝。
	if hasV4 && hasGap && len(rg) == 0 {
		return parseResult{}, false
	}
	for _, g := range allg {
		gl := len(g)
		if gl == 0 || gl > 4 {
			return parseResult{}, false
		}
		for j := 0; j < gl; j++ {
			cc := g[j]
			if cc >= 128 || (hexLUT[cc] == 0 && cc != '0') {
				return parseResult{}, false
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
		return parseResult{v4: v4FromMapped(buf), isV4: true}, true
	}
	return parseResult{v6: buf}, true
}

func fastParseIpv4(s string) (uint32, bool) {
	n := len(s)
	if n < 7 || n > 15 {
		return 0, false
	}

	// --- Octet 0 ---
	var v0 uint32
	c0 := s[0]
	idx := 0
	if c0 == '0' {
		if s[1] != '.' {
			return 0, false
		}
		v0 = 0
		idx = 2
	} else {
		d0 := uint32(c0 - '0')
		if d0 > 9 {
			return 0, false
		}
		if s[1] == '.' {
			v0 = d0
			idx = 2
		} else {
			d1 := uint32(s[1] - '0')
			if s[2] == '.' {
				if d1 > 9 {
					return 0, false
				}
				v0 = d0*10 + d1
				idx = 3
			} else {
				if s[3] != '.' {
					return 0, false
				}
				d2 := uint32(s[2] - '0')
				if d1 > 9 || d2 > 9 {
					return 0, false
				}
				val := d0*100 + d1*10 + d2
				if val > 255 {
					return 0, false
				}
				v0 = val
				idx = 4
			}
		}
	}

	// --- Octet 1 ---
	var v1 uint32
	if idx >= n {
		return 0, false
	}
	c0 = s[idx]
	if c0 == '0' {
		if idx+1 >= n || s[idx+1] != '.' {
			return 0, false
		}
		v1 = 0
		idx += 2
	} else {
		d0 := uint32(c0 - '0')
		if d0 > 9 || idx+1 >= n {
			return 0, false
		}
		if s[idx+1] == '.' {
			v1 = d0
			idx += 2
		} else {
			if idx+2 >= n {
				return 0, false
			}
			d1 := uint32(s[idx+1] - '0')
			if s[idx+2] == '.' {
				if d1 > 9 {
					return 0, false
				}
				v1 = d0*10 + d1
				idx += 3
			} else {
				if idx+3 >= n || s[idx+3] != '.' {
					return 0, false
				}
				d2 := uint32(s[idx+2] - '0')
				if d1 > 9 || d2 > 9 {
					return 0, false
				}
				val := d0*100 + d1*10 + d2
				if val > 255 {
					return 0, false
				}
				v1 = val
				idx += 4
			}
		}
	}

	// --- Octet 2 ---
	var v2 uint32
	if idx >= n {
		return 0, false
	}
	c0 = s[idx]
	if c0 == '0' {
		if idx+1 >= n || s[idx+1] != '.' {
			return 0, false
		}
		v2 = 0
		idx += 2
	} else {
		d0 := uint32(c0 - '0')
		if d0 > 9 || idx+1 >= n {
			return 0, false
		}
		if s[idx+1] == '.' {
			v2 = d0
			idx += 2
		} else {
			if idx+2 >= n {
				return 0, false
			}
			d1 := uint32(s[idx+1] - '0')
			if s[idx+2] == '.' {
				if d1 > 9 {
					return 0, false
				}
				v2 = d0*10 + d1
				idx += 3
			} else {
				if idx+3 >= n || s[idx+3] != '.' {
					return 0, false
				}
				d2 := uint32(s[idx+2] - '0')
				if d1 > 9 || d2 > 9 {
					return 0, false
				}
				val := d0*100 + d1*10 + d2
				if val > 255 {
					return 0, false
				}
				v2 = val
				idx += 4
			}
		}
	}

	// --- Octet 3 ---
	var v3 uint32
	rem := n - idx
	if rem < 1 || rem > 3 {
		return 0, false
	}
	c0 = s[idx]
	if c0 == '0' {
		if rem != 1 {
			return 0, false
		}
		v3 = 0
	} else {
		d0 := uint32(c0 - '0')
		if d0 > 9 {
			return 0, false
		}
		if rem == 1 {
			v3 = d0
		} else if rem == 2 {
			d1 := uint32(s[idx+1] - '0')
			if d1 > 9 {
				return 0, false
			}
			v3 = d0*10 + d1
		} else { // rem == 3
			d1 := uint32(s[idx+1] - '0')
			d2 := uint32(s[idx+2] - '0')
			if d1 > 9 || d2 > 9 {
				return 0, false
			}
			val := d0*100 + d1*10 + d2
			if val > 255 {
				return 0, false
			}
			v3 = val
		}
	}

	return (v0 << 24) | (v1 << 16) | (v2 << 8) | v3, true
}

// ---------- 小工具 ----------

func parseHexGroup(g string) uint16 {
	var v uint16
	for i := 0; i < len(g); i++ {
		v = (v << 4) | uint16(hexLUT[g[i]])
	}
	return v
}
