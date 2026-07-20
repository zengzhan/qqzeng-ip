package main

import (
	"fmt"
	"math/rand"
	"time"

	"qzdb_searcher/qzdb"
)

func run(name, path string, count, count6 int) {
	s, err := qzdb.NewSearcher(path)
	if err != nil {
		fmt.Printf("  %s: load failed (%v)\n", name, err)
		return
	}

	// V4
	rng := rand.New(rand.NewSource(123))
	ips := make([]uint32, count)
	for i := 0; i < count; i++ {
		ips[i] = rng.Uint32()
	}
	start := time.Now()
	for _, ip := range ips {
		s.FindUint(ip)
	}
	fmt.Printf("  %-12s V4 QPS: %.0f\n", name, float64(count)/time.Since(start).Seconds())

	// V6
	v6rng := rand.New(rand.NewSource(456))
	v6start := time.Now()
	for i := 0; i < count6; i++ {
		v6high := (uint64(v6rng.Uint32()) << 32) | uint64(v6rng.Uint32())
		v6low := (uint64(v6rng.Uint32()) << 32) | uint64(v6rng.Uint32())
		s.FindV6(v6high, v6low)
	}
	fmt.Printf("  %-12s V6 QPS: %.0f\n", name, 1_000_000/time.Since(v6start).Seconds())
}

func main() {
	fmt.Println("Go QPS Benchmarks (M4 Pro)")
	run("std_china", "../data/qqzeng_ip_std_china.qzdb", 3000000, 1000000)
	run("max_china", "../data/qqzeng_ip_max_china.qzdb", 3000000, 1000000)
	run("max_global", "../data/qqzeng_ip_max_global.qzdb", 3000000, 1000000)
}
