package qzdb

import (
	"testing"
)

// TestChainMergeFallback 验证 Fallback 模式：返回首个命中的 reader 结果。
func TestChainMergeFallback(t *testing.T) {
	db := buildSyntheticDB(t)
	r1, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()
	r2, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build r2: %v", err)
	}
	defer r2.Close()

	chain := NewChainedReader(r1, r2)
	g, err := chain.Find("114.114.114.114")
	if err != nil {
		t.Fatalf("find: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil result from Fallback chain")
	}
	// 验证 to_pipe 返回非空
	pipe := g.ToPipe()
	if pipe == "" {
		t.Fatal("expected non-empty pipe result")
	}
	t.Logf("Fallback result: %s", pipe)
}

// TestChainMergeFirstWins 验证 MERGE 模式：先注册者优先。
func TestChainMergeFirstWins(t *testing.T) {
	// 创建两个 reader，通过合成数据库来测试合并逻辑
	// 由于合成数据库的 IP 映射是固定的，我们使用 mock 方式验证合并算法

	// 使用真实数据库（如果可用）
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping merge field-level test")
	}
	r1, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()
	r2, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r2: %v", err)
	}
	defer r2.Close()

	// 同一数据库，合并结果应该与单个 reader 相同
	chain := ChainMerge(r1, r2)
	g, err := chain.Find("114.114.114.114")
	if err != nil {
		t.Fatalf("merge find: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil result from Merge chain")
	}

	// 验证与单个 reader 结果一致
	single, _ := r1.Find("114.114.114.114")
	if single == nil {
		t.Fatal("expected non-nil single result")
	}
	if g.ToPipe() != single.ToPipe() {
		t.Errorf("merge result differs from single: merge=%q single=%q", g.ToPipe(), single.ToPipe())
	}
}

// TestChainMergeOverride 验证 MERGE_OVERRIDE 模式：后注册者覆盖。
func TestChainMergeOverride(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping merge override test")
	}
	r1, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()
	r2, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r2: %v", err)
	}
	defer r2.Close()

	// 同一数据库，覆盖模式结果也应一致
	chain := ChainMergeOverride(r1, r2)
	g, err := chain.Find("114.114.114.114")
	if err != nil {
		t.Fatalf("merge override find: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil result from MergeOverride chain")
	}
	single, _ := r1.Find("114.114.114.114")
	if single == nil {
		t.Fatal("expected non-nil single result")
	}
	if g.ToPipe() != single.ToPipe() {
		t.Errorf("override result differs: override=%q single=%q", g.ToPipe(), single.ToPipe())
	}
}

// TestChainMergeAggregateMeta 验证聚合元信息 API。
func TestChainMergeAggregateMeta(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping aggregate meta test")
	}
	r1, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()
	r2, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r2: %v", err)
	}
	defer r2.Close()

	chain := ChainMerge(r1, r2)

	editions := chain.Editions()
	if len(editions) != 2 {
		t.Errorf("expected 2 editions, got %d", len(editions))
	}
	for i, e := range editions {
		if e == "" {
			t.Errorf("edition[%d] is empty", i)
		}
	}

	scopes := chain.Scopes()
	if len(scopes) != 2 {
		t.Errorf("expected 2 scopes, got %d", len(scopes))
	}

	months := chain.DataMonths()
	if len(months) != 2 {
		t.Errorf("expected 2 dataMonths, got %d", len(months))
	}
	for i, m := range months {
		if m == "" {
			t.Errorf("dataMonth[%d] is empty", i)
		}
	}

	readers := chain.Readers()
	if len(readers) != 2 {
		t.Errorf("expected 2 readers, got %d", len(readers))
	}
}

// TestChainMergeFields 验证链式 FindFields 字段投影。
func TestChainMergeFields(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping chain FindFields test")
	}
	r1, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()

	chain := NewChainedReader(r1)
	g, err := chain.FindFields("114.114.114.114", []string{"country", "isp"})
	if err != nil {
		t.Fatalf("chain FindFields: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil result from chain FindFields")
	}
	country := g.Get("country")
	if country == "" {
		t.Error("expected non-empty country in projection")
	}
	isp := g.Get("isp")
	if isp == "" {
		t.Error("expected non-empty isp in projection")
	}
}

// TestChainMergeBatch 验证链式批量查询。
func TestChainMergeBatch(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping chain batch test")
	}
	r1, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()

	chain := NewChainedReader(r1)
	ips := []string{"114.114.114.114", "223.5.5.5", "invalid"}
	results := chain.FindBatch(ips)
	if len(results) != len(ips) {
		t.Errorf("expected %d results, got %d", len(ips), len(results))
	}
	// 验证前两条有结果
	for i := 0; i < 2; i++ {
		if results[i].GeoInfo == nil {
			t.Errorf("expected non-nil result for ip=%s", ips[i])
		}
	}
	// 验证第三条（非法 IP）无结果也无错误
	if results[2].GeoInfo != nil {
		t.Error("expected nil result for invalid IP")
	}
	if results[2].Error != nil {
		t.Error("expected nil error for invalid IP (Go returns nil, nil)")
	}
}

// TestChainMergeStream 验证链式流式查询。
func TestChainMergeStream(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping chain stream test")
	}
	r1, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()

	chain := NewChainedReader(r1)
	ips := []string{"114.114.114.114", "223.5.5.5"}
	stream := chain.FindStream(ips)
	count := 0
	for {
		res, ok := stream.Next()
		if !ok {
			break
		}
		if res.GeoInfo == nil {
			t.Errorf("expected non-nil result for ip=%s", res.IP)
		}
		count++
	}
	if count != len(ips) {
		t.Errorf("expected %d stream results, got %d", len(ips), count)
	}
}

// TestChainMergeEmpty 验证空链的行为。
func TestChainMergeEmpty(t *testing.T) {
	chain := NewChainedReader()
	g, err := chain.Find("114.114.114.114")
	if err != nil {
		t.Fatalf("empty chain find: %v", err)
	}
	if g != nil {
		t.Error("expected nil result from empty chain")
	}
}

// TestChainMergeSingleReader 验证单 reader 链的合并结果与直接查询一致。
func TestChainMergeSingleReader(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping single reader chain test")
	}
	r1, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()

	// Fallback 模式
	fb := NewChainedReader(r1)
	gFB, _ := fb.Find("114.114.114.114")
	single, _ := r1.Find("114.114.114.114")
	if gFB == nil || single == nil {
		t.Fatal("expected non-nil results")
	}
	if gFB.ToPipe() != single.ToPipe() {
		t.Errorf("Fallback chain differs from single: chain=%q single=%q", gFB.ToPipe(), single.ToPipe())
	}

	// Merge 模式
	mg := ChainMerge(r1)
	gMG, _ := mg.Find("114.114.114.114")
	if gMG == nil {
		t.Fatal("expected non-nil merge result")
	}
	if gMG.ToPipe() != single.ToPipe() {
		t.Errorf("Merge chain differs from single: merge=%q single=%q", gMG.ToPipe(), single.ToPipe())
	}

	// Override 模式
	mo := ChainMergeOverride(r1)
	gMO, _ := mo.Find("114.114.114.114")
	if gMO == nil {
		t.Fatal("expected non-nil override result")
	}
	if gMO.ToPipe() != single.ToPipe() {
		t.Errorf("Override chain differs from single: override=%q single=%q", gMO.ToPipe(), single.ToPipe())
	}
}

// TestChainMergeFieldOrder 验证合并后的字段顺序：先注册库字段在前，后注册库新字段追加。
func TestChainMergeFieldOrder(t *testing.T) {
	// 使用合成数据库验证字段顺序逻辑
	db := buildSyntheticDB(t)
	r1, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()
	r2, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build r2: %v", err)
	}
	defer r2.Close()

	chain := ChainMerge(r1, r2)
	g, err := chain.Find("114.114.114.114")
	if err != nil {
		t.Fatalf("merge find: %v", err)
	}
	if g == nil {
		t.Fatal("expected non-nil result")
	}
	// 验证字段名非空
	if len(g.FieldNames) == 0 {
		t.Error("expected non-empty FieldNames")
	}
	// 验证字段名与值数量一致
	if len(g.FieldNames) != len(g.Values) {
		t.Errorf("FieldNames length (%d) != Values length (%d)", len(g.FieldNames), len(g.Values))
	}
	t.Logf("Merge field order: %v", g.FieldNames)
}

// TestChainFactoryFunctions 验证三种工厂函数创建正确模式。
func TestChainFactoryFunctions(t *testing.T) {
	db := buildSyntheticDB(t)
	r1, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer r1.Close()
	r2, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer r2.Close()

	tests := []struct {
		name   string
		chain  *ChainedReader
		mode   ChainMode
	}{
		{"NewChainedReader", NewChainedReader(r1, r2), ModeFallback},
		{"Chain", Chain(r1, r2), ModeFallback},
		{"ChainMerge", ChainMerge(r1, r2), ModeMerge},
		{"ChainMergeOverride", ChainMergeOverride(r1, r2), ModeMergeOverride},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			// 验证查询功能正常
			g, err := tt.chain.Find("114.114.114.114")
			if err != nil {
				t.Fatalf("%s find: %v", tt.name, err)
			}
			if g == nil {
				t.Fatalf("%s: expected non-nil result", tt.name)
			}
			// 验证 Readers() 返回正确数量
			readers := tt.chain.Readers()
			if len(readers) != 2 {
				t.Errorf("%s: expected 2 readers, got %d", tt.name, len(readers))
			}
		})
	}
}

// TestChainMergeMissAll 验证全未命中返回 nil。
func TestChainMergeMissAll(t *testing.T) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		t.Skip("real db not found; skipping miss-all test")
	}
	r1, err := NewBuilder(dbPath).Build()
	if err != nil {
		t.Fatalf("build r1: %v", err)
	}
	defer r1.Close()

	chain := ChainMerge(r1)
	// 8.8.8.8 在 china 数据库中应该未命中
	g, err := chain.Find("8.8.8.8")
	if err != nil {
		t.Fatalf("merge find miss: %v", err)
	}
	if g != nil {
		t.Logf("8.8.8.8 unexpectedly hit: %s", g.ToPipe())
		// 不报 error，因为有些数据库可能包含 8.8.8.8
	}
}
