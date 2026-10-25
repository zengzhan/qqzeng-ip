// qqzeng-ip 6.0 (.db) Go 版冒烟示例
// 用法: go run . [db_path] [ip ...]
package main

import (
	"fmt"
	"os"
	"time"

	"qqzeng-ip/ipdb"
)

func main() {
	t0 := time.Now()
	var db *ipdb.IpDbSearch
	var err error
	if len(os.Args) > 1 {
		db, err = ipdb.NewFromPath(os.Args[1])
	} else {
		db, err = ipdb.Instance()
	}
	if err != nil {
		fmt.Fprintln(os.Stderr, "数据库加载失败:", err)
		os.Exit(1)
	}
	fmt.Printf("load: %.1f ms, nodes=%d, strings=%d\n",
		float64(time.Since(t0).Microseconds())/1000, db.NodeCount(), db.StringCount())

	for _, ip := range []string{"1.10.11.12", "8.8.8.8", "58.213.198.68", "255.255.255.255", "1..2.3"} {
		fmt.Printf("%s -> %s\n", ip, db.Find(ip))
	}
	for _, ip := range os.Args[2:] {
		fmt.Printf("%s -> %s\n", ip, db.Find(ip))
	}

	// 快速压测
	const n = 1000000
	seed := uint64(123456789)
	var checksum uint64
	t0 = time.Now()
	for i := 0; i < n; i++ {
		seed = seed * 48271 % 2147483647
		checksum += uint64(len(db.FindUint32(uint32(seed))))
	}
	ms := float64(time.Since(t0).Microseconds()) / 1000
	fmt.Printf("bench: %d queries, %.0f qps (checksum %d)\n", n, float64(n)/(ms/1000), checksum)
}
