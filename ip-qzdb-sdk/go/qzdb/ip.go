package qzdb

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
// 值返回：21 字节结构体走寄存器/栈，热路径零堆分配（IPv6 亦为单趟扫描、零分配）。
// 对 IPv4-mapped IPv6 自动降级为 IPv4。空白字符一律拒绝（SSRF 安全）。
func fastParseIp(s string) (parseResult, bool) {
	// IPv4 极速热路径：直走快速解析，免去空白/冒号预扫描。
	// fastParseIpv4 具有 100% 严格校验，任何带空白、带符号、带字母、带冒号或越界的输入均会返回 false。
	if v4, ok := fastParseIpv4(s); ok {
		return parseResult{v4: v4, isV4: true}, true
	}
	buf, ok := parseIpv6(s)
	if !ok {
		return parseResult{}, false
	}
	if isV4Mapped(buf) {
		return parseResult{v4: v4FromMapped(buf), isV4: true}, true
	}
	return parseResult{v6: buf}, true
}

// parseIpv6 单趟零分配解析 IPv6 文本（RFC 4291 §2.2 三种形式）。
// 规则：每组 1~4 位十六进制；至多一个 "::"（可代表 1 组以上的零）；不带 "::" 时必须
// 恰好 8 组；内嵌 IPv4 只能位于末尾、占 2 组、按 fastParseIpv4 严格口径；
// 空白、'%'（zone-id）、'[]'、非 ASCII 等任何其他字符一律拒绝；总长 > 45 拒绝。
func parseIpv6(s string) ([16]byte, bool) {
	var buf [16]byte
	n := len(s)
	if n < 2 || n > 45 {
		return buf, false
	}
	var groups [8]uint16
	ng := 0   // 已解析组数（内嵌 IPv4 计 2 组）
	gap := -1 // "::" 所在的组下标
	i := 0
	if s[0] == ':' {
		if s[1] != ':' {
			return buf, false // 单个前导冒号
		}
		gap = 0
		i = 2
	}
	for i < n {
		if ng == 8 {
			return buf, false
		}
		start := i
		var v uint32
		for i < n && i-start < 5 {
			c := s[i]
			if c >= 128 {
				return buf, false
			}
			h := hexLUT[c]
			if h == 0 && c != '0' {
				break
			}
			v = v<<4 | uint32(h)
			i++
		}
		if i < n && s[i] == '.' {
			// 内嵌 IPv4：必须延伸到串尾（fastParseIpv4 拒绝任何 ':'），占 2 组
			if ng > 6 {
				return buf, false
			}
			v4, ok := fastParseIpv4(s[start:])
			if !ok {
				return buf, false
			}
			groups[ng] = uint16(v4 >> 16)
			groups[ng+1] = uint16(v4)
			ng += 2
			break
		}
		if gl := i - start; gl == 0 || gl > 4 {
			return buf, false
		}
		groups[ng] = uint16(v)
		ng++
		if i == n {
			break
		}
		if s[i] != ':' {
			return buf, false
		}
		i++
		if i == n {
			return buf, false // 单个尾随冒号
		}
		if s[i] == ':' {
			if gap >= 0 {
				return buf, false // 多个 "::"
			}
			gap = ng
			i++
		}
	}
	if gap >= 0 {
		if ng > 7 {
			return buf, false // "::" 至少代表 1 组
		}
	} else if ng != 8 {
		return buf, false
	}
	// 组装：gap 之前的组放前面，之后的组右对齐放到末尾
	if gap < 0 {
		gap = ng
	}
	for k := 0; k < gap; k++ {
		buf[2*k] = byte(groups[k] >> 8)
		buf[2*k+1] = byte(groups[k])
	}
	tail := ng - gap
	for k := 0; k < tail; k++ {
		pos := 8 - tail + k
		buf[2*pos] = byte(groups[gap+k] >> 8)
		buf[2*pos+1] = byte(groups[gap+k])
	}
	return buf, true
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

