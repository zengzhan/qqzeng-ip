package qzdb

import (
	"net/netip"
	"strings"
	"testing"
)

// FuzzFastParseIp 对 fastParseIp 做模糊测试，并与标准库 netip 做差分对拍。
//
// 三条不变量：
//   - A（绝不 panic）：任何输入下 fastParseIp 都不能崩溃。
//   - B（差分对拍）：与 netip.ParseAddr 的分类必须一致；都接受时 16 字节必须逐字节相等。
//   - C（往返稳定）：接受的纯 v6 结果经 netip 规范化再解析，必须得到同一 [16]byte。
//
// 关于 zone-id（'%'）：fastParseIp 出于安全拒绝 zone，而 netip 接受。
// 因此「我们拒绝但 netip 接受」仅在输入含 '%' 时合法；其余任何分歧都视为解析器缺陷，fail loudly。
func FuzzFastParseIp(f *testing.F) {
	// 种子语料：覆盖合法 v4 / mapped / 各类 v6 / 对抗字符串。
	seeds := []string{
		// 合法 v4
		"0.0.0.0", "255.255.255.255", "114.114.114.114",
		// IPv4-mapped IPv6（点分与十六进制两种形态）
		"::ffff:114.114.114.114", "::ffff:7272:7272",
		// 各类 v6
		"::1", "2001:db8::1", "fe80::1%eth0", "2408:8000:9000::1",
		"1:2:3:4:5:6:7:8", "::",
		// 对抗字符串
		"01.2.3.4", "256.1.1.1", "1.2.3.4 ", "1.2.3.4\n",
		"::::", "%", "",
		"0.0.0.0::", // 回归种子：内嵌 v4 后接 "::" 属非法，必须拒绝（与 netip 一致）
		"1234567890123456789012345678901234567890123456", // 46+ 字符
		"\xc2\xa0", // unicode 非断行空格字节
	}
	for _, s := range seeds {
		f.Add(s)
	}

	f.Fuzz(func(t *testing.T, s string) {
		// 不变量 A：绝不 panic。
		res, ok := fastParseIp(s)

		// 不变量 B：与 netip 差分对拍。
		na, nerr := netip.ParseAddr(s)
		ourAccept := ok
		netAccept := nerr == nil

		switch {
		case ourAccept && netAccept:
			// 都接受：把我们的结果展开成 16 字节，与 netip.As16() 逐字节比较。
			// v4 展开为 ::ffff:<v4> 形态（与 netip.As16() 对 v4 的表示一致），
			// 这样既能覆盖纯 v4，也能覆盖我们已降级为 isV4 的 ::ffff:a.b.c.d。
			var our16 [16]byte
			if res.isV4 {
				our16[10], our16[11] = 0xFF, 0xFF
				our16[12] = byte(res.v4 >> 24)
				our16[13] = byte(res.v4 >> 16)
				our16[14] = byte(res.v4 >> 8)
				our16[15] = byte(res.v4)
			} else {
				our16 = res.v6
			}
			if na.As16() != our16 {
				t.Fatalf("差分失配(都接受): %q netip=%v 我们=%v", s, na.As16(), our16)
			}

			// 不变量 C：纯 v6 往返稳定（v4-mapped 会被 netip 规范化为点分形态，
			// 重新解析会降级为 isV4，故跳过，避免误报）。
			if !res.isV4 && !isV4Mapped(res.v6) {
				rt := netip.AddrFrom16(res.v6).String()
				re, ok2 := fastParseIp(rt)
				if !ok2 {
					t.Fatalf("往返解析失败: %q -> %q", s, rt)
				}
				if re.isV4 || re.v6 != res.v6 {
					t.Fatalf("往返字节不一致: %q -> %q 得到 %v", s, rt, re.v6)
				}
			}

		case ourAccept && !netAccept:
			// 我们接受但 netip 拒绝：除含 '%' 的 zone（我们已拒绝，正常不可达）外，
			// 一律视为解析器缺陷，fail loudly。
			t.Fatalf("我们接受但 netip 拒绝: %q", s)

		case !ourAccept && netAccept:
			// 我们拒绝但 netip 接受：仅当含 '%'（zone）才合法，其余 FAIL。
			if !strings.Contains(s, "%") {
				t.Fatalf("我们拒绝但 netip 接受: %q", s)
			}

		case !ourAccept && !netAccept:
			// 都拒绝：符合预期，OK。
		}
	})
}

// TestFastParseIpDifferentialSmoke 用一组固定用例确认差分逻辑本身正确（非模糊）。
func TestFastParseIpDifferentialSmoke(t *testing.T) {
	cases := []string{
		"0.0.0.0", "255.255.255.255", "114.114.114.114",
		"::ffff:114.114.114.114", "::ffff:7272:7272",
		"::1", "2001:db8::1", "2408:8000:9000::1", "1:2:3:4:5:6:7:8", "::",
		"01.2.3.4", "256.1.1.1", "1.2.3.4 ", "1.2.3.4\n", "::::", "%", "",
		"fe80::1%eth0", "203.0.113.1", "1:2:3:4:5:6:7:8.9.10.11",
	}
	for _, s := range cases {
		_, ok := fastParseIp(s)
		_, nerr := netip.ParseAddr(s)
		if ok && nerr != nil {
			t.Errorf("我们接受但 netip 拒绝: %q", s)
		}
		if !ok && nerr == nil && !strings.Contains(s, "%") {
			t.Errorf("我们拒绝但 netip 接受(非zone): %q", s)
		}
	}
}
