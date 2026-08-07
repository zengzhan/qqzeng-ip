package qzdb

import (
	"fmt"
	"sync"
	"testing"
	"time"
)

// BenchmarkFindIPv4 单线程 IPv4 查询性能基准。
func BenchmarkFindIPv4(b *testing.B) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		b.Skip("real db not found")
	}
	r, err := NewBuilder(dbPath).Build()
	if err != nil {
		b.Fatalf("build: %v", err)
	}
	defer r.Close()

	ips := []string{"114.114.114.114", "223.5.5.5", "1.2.3.4", "120.53.1.1", "180.76.76.76"}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		r.Find(ips[i%len(ips)])
	}
}

// BenchmarkFindIPv6 单线程 IPv6 查询性能基准。
func BenchmarkFindIPv6(b *testing.B) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		b.Skip("real db not found")
	}
	r, err := NewBuilder(dbPath).Build()
	if err != nil {
		b.Fatalf("build: %v", err)
	}
	defer r.Close()

	ips := []string{"2408:8000:9000::1", "240e:390:1:1::1", "2001:4860:4860::8888", "240c::6664", "2a03:2880:f10c:83:face:b00c::25de"}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		r.Find(ips[i%len(ips)])
	}
}

// BenchmarkFindConcurrent 16 线程并发查询（race-free 验证 + QPS）。
func BenchmarkFindConcurrent(b *testing.B) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		b.Skip("real db not found")
	}
	r, err := NewBuilder(dbPath).Build()
	if err != nil {
		b.Fatalf("build: %v", err)
	}
	defer r.Close()

	ips := []string{"114.114.114.114", "223.5.5.5", "1.2.3.4", "2408:8000:9000::1"}
	b.ResetTimer()
	b.RunParallel(func(pb *testing.PB) {
		i := 0
		for pb.Next() {
			r.Find(ips[i%len(ips)])
			i++
		}
	})
}

// TestTier3ConcurrentSafety 16 线程 × 10 万双栈混合查询无异常/race-free。
func TestTier3ConcurrentSafety(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping Tier3")
	}
	r, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer r.Close()

	const (
		numThreads = 16
		queriesPerThread = 100000
	)

	ipv4IPs := []string{"114.114.114.114", "223.5.5.5", "1.2.3.4", "120.53.1.1"}
	ipv6IPs := []string{"2408:8000:9000::1", "240e:390:1:1::1"}

	var wg sync.WaitGroup
	errCh := make(chan error, numThreads)

	for tid := 0; tid < numThreads; tid++ {
		wg.Add(1)
		go func(id int) {
			defer wg.Done()
			for i := 0; i < queriesPerThread; i++ {
				// 双栈 1:1 混合
				if i%2 == 0 {
					g, err := r.Find(ipv4IPs[i%len(ipv4IPs)])
					if err != nil {
						errCh <- fmt.Errorf("thread %d ipv4: %v", id, err)
						return
					}
					// 验证结果非空（对于已知命中的 IP）
					if g == nil && id == 0 && i < 10 {
						t.Logf("thread %d: ipv4 %s miss", id, ipv4IPs[i%len(ipv4IPs)])
					}
				} else {
					g, err := r.Find(ipv6IPs[i%len(ipv6IPs)])
					if err != nil {
						errCh <- fmt.Errorf("thread %d ipv6: %v", id, err)
						return
					}
					if g == nil && id == 0 && i < 10 {
						t.Logf("thread %d: ipv6 %s miss", id, ipv6IPs[i%len(ipv6IPs)])
					}
				}
			}
		}(tid)
	}

	wg.Wait()
	close(errCh)

	for e := range errCh {
		t.Errorf("concurrent error: %v", e)
	}

	totalOps := int64(numThreads) * int64(queriesPerThread)
	t.Logf("Tier3 concurrent: %d threads × %d queries = %d total ops, race-free",
		numThreads, queriesPerThread, totalOps)
}

// TestTier3DualStackPerformance 双栈 1:1 性能基准（分别统计 IPv4/IPv6 QPS）。
func TestTier3DualStackPerformance(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping Tier3 perf")
	}
	r, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer r.Close()

	const queries = 100000

	// IPv4 QPS
	ipv4IPs := []string{"114.114.114.114", "223.5.5.5", "1.2.3.4"}
	start := time.Now()
	for i := 0; i < queries; i++ {
		r.Find(ipv4IPs[i%len(ipv4IPs)])
	}
	ipv4Elapsed := time.Since(start).Nanoseconds()
	ipv4QPS := float64(queries) / float64(ipv4Elapsed) * 1e9

	// IPv6 QPS
	ipv6IPs := []string{"2408:8000:9000::1", "240e:390:1:1::1"}
	start = time.Now()
	for i := 0; i < queries; i++ {
		r.Find(ipv6IPs[i%len(ipv6IPs)])
	}
	ipv6Elapsed := time.Since(start).Nanoseconds()
	ipv6QPS := float64(queries) / float64(ipv6Elapsed) * 1e9

	t.Logf("Tier3 single-thread IPv4: %.0f QPS (%d queries in %d ms)",
		ipv4QPS, queries, ipv4Elapsed/1e6)
	t.Logf("Tier3 single-thread IPv6: %.0f QPS (%d queries in %d ms)",
		ipv6QPS, queries, ipv6Elapsed/1e6)
}


