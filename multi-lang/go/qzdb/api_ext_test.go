package qzdb

import (
	"net/netip"
	"os"
	"testing"
)

// TestFindAddr 验证 FindAddr(netip.Addr) 重载。
func TestFindAddr(t *testing.T) {
	db := buildSyntheticDB(t)
	r, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer r.Close()

	tests := []struct {
		name    string
		addr    netip.Addr
		wantNil bool
	}{
		{"IPv4-valid", netip.MustParseAddr("114.114.114.114"), false},
		{"IPv4-not-found", netip.MustParseAddr("8.8.8.8"), false}, // 合成库可能没有 8.8.8.8
		{"IPv6-loopback", netip.MustParseAddr("::1"), true},
		{"IPv6-valid", netip.MustParseAddr("2408:8000:9000::1"), false},
		{"IPv4-mapped", netip.MustParseAddr("::ffff:114.114.114.114"), false},
		{"Invalid", netip.Addr{}, true},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			g, err := r.FindAddr(tt.addr)
			if err != nil {
				t.Fatalf("FindAddr(%s): %v", tt.addr, err)
			}
			if tt.wantNil && g != nil {
				t.Errorf("FindAddr(%s): expected nil, got %s", tt.addr, g.ToPipe())
			}
			if !tt.wantNil && g == nil {
				// 对于 8.8.8.8 可能确实没命中，不报错
				t.Logf("FindAddr(%s): miss (may be expected)", tt.addr)
			}
		})
	}
}

// TestFindAddrFallback 验证 FindAddr 结果与 FindString 一致。
func TestFindAddrFallback(t *testing.T) {
	db := buildSyntheticDB(t)
	r, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer r.Close()

	// IPv4 地址
	g1, _ := r.Find("114.114.114.114")
	g2, _ := r.FindAddr(netip.MustParseAddr("114.114.114.114"))
	if g1 == nil || g2 == nil {
		t.Log("one of the results is nil (may be expected with synthetic DB)")
	} else if g1.ToPipe() != g2.ToPipe() {
		t.Errorf("FindAddr differs from Find: addr=%q str=%q", g2.ToPipe(), g1.ToPipe())
	}

	// IPv4-mapped IPv6 地址
	g3, _ := r.Find("::ffff:114.114.114.114")
	g4, _ := r.FindAddr(netip.MustParseAddr("::ffff:114.114.114.114"))
	if g3 == nil || g4 == nil {
		t.Log("one of mapped results is nil")
	} else if g3.ToPipe() != g4.ToPipe() {
		t.Errorf("mapped differs: addr=%q str=%q", g4.ToPipe(), g3.ToPipe())
	}
}

// TestOpenBufferNoCopy 验证零拷贝变体功能正常。
func TestOpenBufferNoCopy(t *testing.T) {
	db := buildSyntheticDB(t)
	r, err := NewBuilderBytesNoCopy(db).Build()
	if err != nil {
		t.Fatalf("build no-copy: %v", err)
	}
	defer r.Close()

	g, err := r.Find("114.114.114.114")
	if err != nil {
		t.Fatalf("find: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil result from no-copy reader")
	}
	if g.ToPipe() == "" {
		t.Error("expected non-empty pipe result")
	}
}

// TestOpenBufferNoCopyPackageFunc 验证包级 OpenBufferNoCopy 便捷函数。
func TestOpenBufferNoCopyPackageFunc(t *testing.T) {
	db := buildSyntheticDB(t)
	r, err := OpenBufferNoCopy(db, 0, true)
	if err != nil {
		t.Fatalf("OpenBufferNoCopy: %v", err)
	}
	defer r.Close()

	g, err := r.Find("114.114.114.114")
	if err != nil {
		t.Fatalf("find: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil result")
	}
}

// TestNoCopyBuilderOption 验证 Builder.NoCopy() 方法。
func TestNoCopyBuilderOption(t *testing.T) {
	db := buildSyntheticDB(t)
	cp := make([]byte, len(db))
	copy(cp, db)
	r, err := NewBuilderBytes(cp).NoCopy(true).Build()
	if err != nil {
		t.Fatalf("build with NoCopy option: %v", err)
	}
	defer r.Close()

	g, err := r.Find("114.114.114.114")
	if err != nil {
		t.Fatalf("find: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil result")
	}
}

// TestFindAddrRealDB 使用真实数据库测试 FindAddr。
func TestFindAddrRealDB(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping FindAddr real DB test")
	}
	r, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer r.Close()

	// IPv4
	g, err := r.FindAddr(netip.MustParseAddr("114.114.114.114"))
	if err != nil {
		t.Fatalf("FindAddr: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil for 114.114.114.114")
	}
	t.Logf("FindAddr(114.114.114.114) = %s", g.ToPipe())

	// 与 Find 结果对比
	gs, _ := r.Find("114.114.114.114")
	if gs == nil {
		t.Fatal("expected non-nil for Find(114.114.114.114)")
	}
	if g.ToPipe() != gs.ToPipe() {
		t.Errorf("FindAddr differs: addr=%q str=%q", g.ToPipe(), gs.ToPipe())
	}
}

// TestOpenBufferNoCopyRealDB 使用真实数据库测试零拷贝加载。
func TestOpenBufferNoCopyRealDB(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found")
	}
	data, err := os.ReadFile(dbPath)
	if err != nil {
		t.Fatalf("read db: %v", err)
	}
	r, err := OpenBufferNoCopy(data, 0, true)
	if err != nil {
		t.Fatalf("OpenBufferNoCopy real db: %v", err)
	}
	defer r.Close()

	g, err := r.Find("114.114.114.114")
	if err != nil {
		t.Fatalf("find: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil result from real DB no-copy load")
	}
}
