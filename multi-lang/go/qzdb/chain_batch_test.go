package qzdb

import (
	"errors"
	"testing"
)

// TestChainedReaderBatchThreeState 验证 ChainedReader.FindBatch 的三态契约：
//   - 非法 IP：GeoInfo==nil 且 Error!=nil 且为 *QzdbError(Code==ErrCodeInvalidParam)
//   - 合法但未命中：GeoInfo==nil 且 Error==nil
//   - 命中：GeoInfo!=nil 且 ToPipe 非空
//
// 使用与 chain_merge_test.go 相同的合成数据库构造方式（buildSyntheticDB），
// 不依赖外部数据文件，保证可重复运行。合成库覆盖 114.114/16、223.5/16、8.8/16、2408:8000:9000::，
// 故 203.0.113.1（TEST-NET-3）必然未命中。
func TestChainedReaderBatchThreeState(t *testing.T) {
	db := buildSyntheticDB(t)
	r, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build reader: %v", err)
	}
	defer r.Close()

	chain := NewChainedReader(r)

	ips := []string{
		"not-an-ip",       // 非法：非地址
		"1.2.3.4 ",        // 非法：尾部空白（SSRF 防护拒绝）
		"203.0.113.1",     // 合法但未命中（TEST-NET-3，合成库不含）
		"114.114.114.114", // 命中
	}
	results := chain.FindBatch(ips)
	if len(results) != len(ips) {
		t.Fatalf("期望 %d 条结果，实际 %d", len(ips), len(results))
	}

	// 索引 0、1：非法 IP → 三态契约（GeoInfo==nil, Error!=nil, INVALID_PARAM）
	for _, idx := range []int{0, 1} {
		res := results[idx]
		if res.GeoInfo != nil {
			t.Errorf("ip=%q 期望 GeoInfo==nil，实际非 nil", res.IP)
		}
		if res.Error == nil {
			t.Errorf("ip=%q 期望 Error!=nil（INVALID_PARAM），实际 nil", res.IP)
			continue
		}
		var qe *QzdbError
		if !errors.As(res.Error, &qe) {
			t.Errorf("ip=%q 期望 *QzdbError，实际 %T(%v)", res.IP, res.Error, res.Error)
			continue
		}
		if qe.Code() != ErrCodeInvalidParam {
			t.Errorf("ip=%q 期望 ErrCodeInvalidParam，实际 %s", res.IP, qe.Code())
		}
	}

	// 索引 2：合法未命中 → GeoInfo==nil 且 Error==nil
	if results[2].GeoInfo != nil {
		t.Errorf("ip=%q 期望未命中 GeoInfo==nil，实际非 nil", results[2].IP)
	}
	if results[2].Error != nil {
		t.Errorf("ip=%q 期望未命中 Error==nil，实际 %v", results[2].IP, results[2].Error)
	}

	// 索引 3：命中 → GeoInfo!=nil 且 ToPipe 非空
	if results[3].GeoInfo == nil {
		t.Fatalf("ip=%q 期望命中 GeoInfo!=nil，实际 nil", results[3].IP)
	}
	if results[3].Error != nil {
		t.Errorf("ip=%q 命中却带 Error=%v", results[3].IP, results[3].Error)
	}
	if results[3].GeoInfo.ToPipe() == "" {
		t.Errorf("ip=%q 命中但 ToPipe 为空", results[3].IP)
	}
	t.Logf("命中结果: %s", results[3].GeoInfo.ToPipe())
}

// TestChainedReaderStreamThreeState 验证 ChainedReader.FindStream 与 FindBatch 一致的三态契约。
func TestChainedReaderStreamThreeState(t *testing.T) {
	db := buildSyntheticDB(t)
	r, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build reader: %v", err)
	}
	defer r.Close()

	chain := NewChainedReader(r)

	ips := []string{
		"not-an-ip",         // 非法
		"1.2.3.4 ",          // 非法（尾部空白）
		"203.0.113.1",       // 合法未命中
		"2408:8000:9000::1", // 命中（合成库 v6 网段）
	}
	stream := chain.FindStream(ips)

	// 收集流式结果，按 IP 建索引便于断言。
	byIP := make(map[string]BatchResult, len(ips))
	for {
		res, ok := stream.Next()
		if !ok {
			break
		}
		byIP[res.IP] = res
	}
	if len(byIP) != len(ips) {
		t.Fatalf("期望 %d 条流式结果，实际 %d", len(ips), len(byIP))
	}

	for _, bad := range []string{"not-an-ip", "1.2.3.4 "} {
		res := byIP[bad]
		if res.GeoInfo != nil {
			t.Errorf("ip=%q 期望 GeoInfo==nil，实际非 nil", bad)
		}
		if res.Error == nil {
			t.Errorf("ip=%q 期望 Error!=nil（INVALID_PARAM），实际 nil", bad)
			continue
		}
		var qe *QzdbError
		if !errors.As(res.Error, &qe) {
			t.Errorf("ip=%q 期望 *QzdbError，实际 %T(%v)", bad, res.Error, res.Error)
			continue
		}
		if qe.Code() != ErrCodeInvalidParam {
			t.Errorf("ip=%q 期望 ErrCodeInvalidParam，实际 %s", bad, qe.Code())
		}
	}

	// 合法未命中
	if byIP["203.0.113.1"].GeoInfo != nil {
		t.Errorf("ip=203.0.113.1 期望未命中 GeoInfo==nil，实际非 nil")
	}
	if byIP["203.0.113.1"].Error != nil {
		t.Errorf("ip=203.0.113.1 期望未命中 Error==nil，实际 %v", byIP["203.0.113.1"].Error)
	}

	// 命中（v6）
	hit := byIP["2408:8000:9000::1"]
	if hit.GeoInfo == nil {
		t.Fatalf("ip=2408:8000:9000::1 期望命中 GeoInfo!=nil，实际 nil")
	}
	if hit.Error != nil {
		t.Errorf("ip=2408:8000:9000::1 命中却带 Error=%v", hit.Error)
	}
	if hit.GeoInfo.ToPipe() == "" {
		t.Errorf("ip=2408:8000:9000::1 命中但 ToPipe 为空")
	}
	t.Logf("v6 命中结果: %s", hit.GeoInfo.ToPipe())
}
