package qqzengip

import (
	"testing"
)

var testIP = "223.104.69.182"
var benchFinder *IpSearch

func init() {
	var err error
	benchFinder, err = LoadDat("../qqzeng-ip-3.1-ultimate.dat")
	if err != nil {
		panic(err)
	}
}

// BenchmarkGet 单次查询性能基准测试
func BenchmarkGet(b *testing.B) {
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		_ = benchFinder.Get(testIP)
	}
}

// BenchmarkGetParallel 并行查询性能基准测试
func BenchmarkGetParallel(b *testing.B) {
	b.ReportAllocs()
	b.RunParallel(func(pb *testing.PB) {
		for pb.Next() {
			_ = benchFinder.Get(testIP)
		}
	})
}

// BenchmarkIsValid IP 验证性能
func BenchmarkIsValid(b *testing.B) {
	b.ReportAllocs()
	for i := 0; i < b.N; i++ {
		_ = IsValid(testIP)
	}
}

// BenchmarkBatchGet 批量查询性能基准测试
func BenchmarkBatchGet(b *testing.B) {
	testIPs := make([]string, 100)
	for i := range testIPs {
		testIPs[i] = testIP
	}
	b.ReportAllocs()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		_ = benchFinder.BatchGet(testIPs)
	}
}

// TestGet 测试 IP 查询功能
func TestGet(t *testing.T) {
	tests := []struct {
		ip      string
		wantOK  bool
		contain string
	}{
		{"223.104.69.182", true, "中国"},
		{"114.114.114.114", true, "中国"},
		{"255.255.255.255", true, "保留"},
		{"invalid", false, ""},
		{"256.1.1.1", false, ""},
		{"1.2.3", false, ""},
		{"", false, ""},
	}

	for _, tt := range tests {
		t.Run(tt.ip, func(t *testing.T) {
			result := benchFinder.Get(tt.ip)
			if tt.wantOK && result == "" {
				t.Errorf("Get(%q) = empty, want non-empty", tt.ip)
			}
			if !tt.wantOK && result != "" {
				t.Errorf("Get(%q) = %q, want empty", tt.ip, result)
			}
		})
	}
}

// TestIsValid 测试 IP 验证功能
func TestIsValid(t *testing.T) {
	tests := []struct {
		ip   string
		want bool
	}{
		{"0.0.0.0", true},
		{"255.255.255.255", true},
		{"192.168.1.1", true},
		{"256.1.1.1", false},
		{"1.2.3", false},
		{"abc.def.ghi.jkl", false},
		{"", false},
		{"1.2.3.4.5", false},
	}

	for _, tt := range tests {
		t.Run(tt.ip, func(t *testing.T) {
			if got := IsValid(tt.ip); got != tt.want {
				t.Errorf("IsValid(%q) = %v, want %v", tt.ip, got, tt.want)
			}
		})
	}
}

// TestVersion 测试版本号
func TestVersion(t *testing.T) {
	if Version == "" {
		t.Error("Version should not be empty")
	}
}


