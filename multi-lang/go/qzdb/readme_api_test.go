package qzdb

import (
	"strings"
	"testing"
)

// TestREADMEAPISurface 复刻 README 示例用到的全部 API，确保文档零幻觉（全部真实可编译可运行）。
func TestREADMEAPISurface(t *testing.T) {
	db := buildSyntheticDB(t)
	reader, err := NewBuilderBytes(db).GroupIndex(0).VerifyCRC(true).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer reader.Close()

	// 单条查询
	info, _ := reader.Find("114.114.114.114")
	if info == nil {
		t.Fatal("Find nil")
	}
	if info.ToPipe() == "" {
		t.Error("ToPipe empty")
	}
	if info.ToJson() == "" {
		t.Error("ToJson empty")
	}
	if info.GetCountry() == "" {
		t.Error("GetCountry empty")
	}
	// 语义化 province/city/geo_id 仅在字段存在的数据库中断言（synthetic 为 ASN 版，无这些字段）。
	if reader.HasField("province") && info.GetProvince() == "" {
		t.Error("GetProvince empty")
	}
	if reader.HasField("city") && info.GetCity() == "" {
		t.Error("GetCity empty")
	}
	if info.GetIsp() == "" {
		t.Error("GetIsp empty")
	}
	if info.GetAsn() == nil {
		t.Error("GetAsn nil")
	}
	if info.Get("country_code") != "CN" {
		t.Error("Get normalized fail")
	}
	if info.ToMap()["country"] != "中国" {
		t.Error("ToMap fail")
	}
	if reader.FindStr("223.5.5.5") == "" {
		t.Error("FindStr empty")
	}

	// 多入口
	v4 := uint32(0x7272_7272) // 114.114.114.114
	if g, _ := reader.FindUint(v4); g == nil {
		t.Error("FindUint nil")
	}
	var v6 [16]byte
	v6[0] = 0x24
	v6[1] = 0x08
	v6[2] = 0x80
	v6[3] = 0x00
	if g, _ := reader.FindV6Uint(v6); g == nil {
		t.Error("FindV6Uint nil")
	}
	var mapped [16]byte
	mapped[10], mapped[11] = 0xFF, 0xFF
	mapped[12], mapped[13], mapped[14], mapped[15] = 114, 114, 114, 114
	if g, _ := reader.FindBytes(mapped); g == nil {
		t.Error("FindBytes nil")
	}
	if g, _ := reader.FindFields("114.114.114.114", []string{"country", "isp"}); g == nil {
		t.Error("FindFields nil")
	}

	// 低级
	if reader.LookupRowId("114.114.114.114") == 0 {
		t.Error("LookupRowId 0")
	}
	if reader.LookupRowIdUint(v4) == 0 {
		t.Error("LookupRowIdUint 0")
	}
	if reader.LookupRowIdV6(v6) == 0 {
		t.Error("LookupRowIdV6 0")
	}
	if reader.LookupRowIdBytes(mapped[:]) == 0 {
		t.Error("LookupRowIdBytes 0")
	}
	if reader.LookupIds(1) == nil {
		t.Error("LookupIds nil")
	}
	// CIDR（synthetic 仅 jump 表、无节点，返回 "" 属正常）
	_ = reader.LookupCidr("114.114.114.114")
	_ = reader.LookupCidrUint(v4)
	_ = reader.LookupCidrBytes(mapped[:])

	// 批量 / 流式
	ips := []string{"114.114.114.114", "223.5.5.5", "8.8.8.8"}
	batch := reader.FindBatch(ips)
	if len(batch) != 3 {
		t.Errorf("FindBatch len %d", len(batch))
	}
	proj := reader.FindBatchFields(ips, []string{"country"})
	if len(proj) != 3 {
		t.Errorf("FindBatchFields len %d", len(proj))
	}
	st := reader.FindStream(ips)
	n := 0
	for {
		res, ok := st.Next()
		if !ok {
			break
		}
		if res.IP == "" {
			t.Error("stream empty ip")
		}
		n++
	}
	if n != 3 {
		t.Errorf("stream count %d", n)
	}

	// ChainedReader
	chain := NewChainedReader(reader)
	chain.Add(reader)
	if chain.FindStr("114.114.114.114") == "" {
		t.Error("chain FindStr empty")
	}

	// QzdbRegistry
	reg := NewQzdbRegistry()
	reg.Register("std", reader)
	if reg.FindStr("114.114.114.114") == "" {
		t.Error("reg FindStr empty")
	}
	if len(reg.Names()) != 1 {
		t.Error("reg Names")
	}

	// 元信息
	if reader.GetVersion() != "asn" {
		t.Errorf("GetVersion=%q", reader.GetVersion())
	}
	if reader.GetFileHash() == "" {
		t.Error("GetFileHash empty")
	}
	if !reader.HasField("country_code") {
		t.Error("HasField false")
	}
	if len(reader.GetFieldNames()) != 8 {
		t.Errorf("GetFieldNames %d", len(reader.GetFieldNames()))
	}
	if got := reader.GetDataMonth(); got != "2026-07" {
		t.Errorf("GetDataMonth=%q, want TLV type=5 value", got)
	}
	if reader.GetEdition() != "asn" {
		t.Errorf("GetEdition=%q", reader.GetEdition())
	}
	if got := reader.GetScope(); got != "global" {
		t.Errorf("GetScope=%q, want TLV type=6 value", got)
	}
	if reader.GetBuildTime() == "" {
		t.Error("GetBuildTime empty")
	}
	if reader.GetDescription() == "" {
		t.Error("GetDescription empty")
	}
	if reader.GetGroupCount() != 1 {
		t.Errorf("GetGroupCount %d", reader.GetGroupCount())
	}
	if reader.GetPoolCount() != 8 {
		t.Errorf("GetPoolCount %d", reader.GetPoolCount())
	}
	if !reader.VerifyCRC() {
		t.Error("VerifyCRC false")
	}

	// UsageType
	ut := info.GetUsageType()
	if ut.RawValue() != "ISP" {
		t.Errorf("usage %q", ut.RawValue())
	}
	if !ut.IsKnown() {
		t.Error("usage unknown")
	}
	u := ParseUsageType("MyCustom")
	if u.IsKnown() || u.RawValue() != "MyCustom" {
		t.Error("ParseUsageType unknown fail")
	}
	_ = ut.DisplayZh()

	// ReloadBuffer 热替换（强制 CRC）
	db2 := modifySyntheticASName(t, db, "RELOADED")
	if err := reader.ReloadBuffer(db2); err != nil {
		t.Errorf("ReloadBuffer: %v", err)
	}
	if !strings.Contains(reader.FindStr("114.114.114.114"), "RELOADED") {
		t.Error("reload did not take effect")
	}

	// Close 后安全失败
	reader.Close()
	if _, e := reader.Find("114.114.114.114"); e != ErrClosed {
		t.Errorf("post-close err = %v", e)
	}
	_ = ErrClosed
	_ = errors_IsHelper()
}

func errors_IsHelper() bool { return true }
