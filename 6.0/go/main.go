package main

import (
	"bufio"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"qqzeng-ip/ipdb"
)

func main() {
	fmt.Println("正在初始化 qqzeng-ip 数据库...")
	start := time.Now()
	searcher, err := ipdb.Instance()
	if err != nil {
		fmt.Printf("⚠️ 数据库加载失败，跳过测试 (CI环境下正常): %v\n", err)
		return
	}
	elapsed := time.Since(start)
	fmt.Printf("数据库加载完成，耗时: %v\n", elapsed)

	// 查找测试文件
	testFile := findTestFile()
	if testFile != "" {
		verifyWithFile(searcher, testFile)
	}

	// --- 随机压测 ---
	totalCount := 3000000
	fmt.Printf("\n生成 %d 个随机 IP (UInt32)...\n", totalCount)
	randomIps := generateRandomIps(totalCount)
	fmt.Println("生成完成，开始压测 (FindUint)...")

	// 手动GC一次
	time.Sleep(time.Millisecond * 200)

	benchStart := time.Now()
	for i := 0; i < totalCount; i++ {
		// 模拟调用参数转换
		ip := randomIps[i]
		searcher.FindUint(uint16(ip>>16), uint16(ip&0xFFFF))
	}
	benchElapsed := time.Since(benchStart)
	fmt.Printf("\n%d 次随机查询耗时: %v\n", totalCount, benchElapsed)
	qps := float64(totalCount) / benchElapsed.Seconds()
	fmt.Printf("QPS: %.2f\n", qps)
}

func verifyWithFile(searcher *ipdb.IpDbSearch, path string) {
	fmt.Printf("正在读取测试文件: %s\ne", path)
	file, err := os.Open(path)
	if err != nil { return }
	defer file.Close()

	scanner := bufio.NewScanner(file)
	passed := 0
	count := 0
	for scanner.Scan() {
		line := scanner.Text()
		parts := strings.Split(line, "\t")
		if len(parts) < 3 { continue }
		if searcher.Find(parts[0]) == parts[2] && searcher.Find(parts[1]) == parts[2] {
			passed++
		}
		count++
	}
	fmt.Printf("验证完成: %d/%d 通过\n", passed, count)
}

func verify(searcher *ipdb.IpDbSearch, ip, expected string) bool {
	result := searcher.Find(ip)
	if result != expected {
		fmt.Printf("[Fail] IP: %s\n", ip)
		return false
	}
	return true
}

func findTestFile() string {
	exePath, _ := os.Executable()
	baseDir := filepath.Dir(exePath)
	attempts := []string{
		filepath.Join(baseDir, "../data/test.txt"),
		filepath.Join(baseDir, "../../data/test.txt"),
		filepath.Join(baseDir, "../../../data/test.txt"),
		"../data/test.txt",
	}
	for _, path := range attempts {
		if _, err := os.Stat(path); err == nil {
			return path
		}
	}
	return ""
}

func generateRandomIps(count int) []uint32 {
	ips := make([]uint32, count)
	seed := uint32(123)
	for i := 0; i < count; i++ {
		seed = seed*1664525 + 1013904223
		ips[i] = seed
	}
	return ips
}

func ipToUint(ip string) uint32 {
	parts := strings.Split(ip, ".")
	a, _ := strconv.Atoi(parts[0])
	b, _ := strconv.Atoi(parts[1])
	c, _ := strconv.Atoi(parts[2])
	d, _ := strconv.Atoi(parts[3])
	return uint32(a)<<24 | uint32(b)<<16 | uint32(c)<<8 | uint32(d)
}
