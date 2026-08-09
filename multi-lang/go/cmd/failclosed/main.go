package main

import (
	"fmt"
	"os"

	"qzdb_reader/qzdb"
)

func probe(m []byte) (panicked bool) {
	defer func() {
		if r := recover(); r != nil {
			panicked = true
		}
	}()
	rd, err := qzdb.OpenBufferNoCopy(m, 0, false)
	if err != nil || rd == nil {
		return false
	}
	_, _ = rd.Find("114.114.114.114")
	_, _ = rd.Find("8.8.8.8")
	_, _ = rd.Find("2400:3200::1")
	return false
}

func main() {
	src := "../test_data_202608/ult/china/qqzeng_ip_ult_china.qzdb"
	if len(os.Args) > 1 {
		src = os.Args[1]
	}
	full, err := os.ReadFile(src)
	if err != nil {
		panic(err)
	}
	total := 0

	fmt.Println("== 截断测试 ==")
	np := 0
	for _, n := range []int{0, 3, 100, 191, 192, 200, 250, 500, 5000, 100000, 1000000} {
		if n > len(full) {
			continue
		}
		b := make([]byte, n)
		copy(b, full[:n])
		if probe(b) {
			np++
			fmt.Printf("  [PANIC!!] truncate %d B\n", n)
		}
	}
	fmt.Printf("  panic = %d\n", np)
	total += np

	fmt.Println("== 头部 192 字节全域穷举（每字节 × 4 模式）==")
	np = 0
	cases := 0
	for pos := 4; pos < 192; pos++ {
		for _, pat := range []byte{0x00, 0xFF, 0x7F, 0x80} {
			m := make([]byte, len(full))
			copy(m, full)
			m[pos] = pat
			cases++
			if probe(m) {
				np++
				fmt.Printf("  [PANIC!!] header byte %d = 0x%02X\n", pos, pat)
			}
		}
	}
	fmt.Printf("  %d 例, panic = %d\n", cases, np)
	total += np

	fmt.Println("== 字节洪泛（随机位翻转 2000 次）==")
	np = 0
	seed := uint64(0x9E3779B97F4A7C15)
	lim := len(full)
	if lim > 512*1024 {
		lim = 512 * 1024
	}
	for i := 0; i < 2000; i++ {
		seed = seed*6364136223846793005 + 1442695040888963407
		pos := int(seed>>16) % lim
		m := make([]byte, len(full))
		copy(m, full)
		m[pos] ^= 0xFF
		if probe(m) {
			np++
			fmt.Printf("  [PANIC!!] flip #%d @ %d\n", i, pos)
		}
	}
	fmt.Printf("  panic = %d\n", np)
	total += np

	fmt.Println("== 尾部随机截断 500 次 ==")
	np = 0
	for i := 0; i < 500; i++ {
		seed = seed*6364136223846793005 + 1442695040888963407
		n := int(seed>>16) % len(full)
		b := make([]byte, n)
		copy(b, full[:n])
		if probe(b) {
			np++
			fmt.Printf("  [PANIC!!] truncate #%d @ %d B\n", i, n)
		}
	}
	fmt.Printf("  panic = %d\n", np)
	total += np

	res := "PASS"
	if total != 0 {
		res = "FAIL"
	}
	fmt.Printf("\n==== 总 panic 数 = %d (%s) ====\n", total, res)
}
