package qzdb

import (
	"encoding/binary"
	"hash/crc32"
	"testing"
)

// buildSyntheticDBNoTLV56 返回剥离 Metadata TLV type=5/6 的等价数据库，
// 用于验证 FORMAT §8.2 的回落路径（data_month 回落 Header BuildDate、scope 为 ""）。
// Metadata 是文件最后一个段，截掉尾部条目不影响其他段的偏移；
// 截断后必须按 canonical 算法重算 CRC（@16~19 先清零再全文件校验和）。
func buildSyntheticDBNoTLV56(t *testing.T) []byte {
	t.Helper()
	base := buildSyntheticDB(t)
	offMeta := int(binary.LittleEndian.Uint64(base[144:152]))
	if offMeta <= 0 || offMeta >= len(base) {
		t.Fatalf("bad offMeta %d", offMeta)
	}
	meta := base[offMeta:]
	filtered := make([]byte, 0, len(meta))
	for pos := 0; pos+4 <= len(meta); {
		length := int(binary.LittleEndian.Uint16(meta[pos+2 : pos+4]))
		if meta[pos] == 0 || length == 0 || pos+4+length > len(meta) {
			break
		}
		if meta[pos] != 5 && meta[pos] != 6 {
			filtered = append(filtered, meta[pos:pos+4+length]...)
		}
		pos += 4 + length
	}
	out := make([]byte, 0, offMeta+len(filtered))
	out = append(out, base[:offMeta]...)
	out = append(out, filtered...)

	// canonical CRC：偏移 16~19 填 0 后对整个（截断后的）文件计算
	binary.LittleEndian.PutUint32(out[16:], 0)
	crc := crc32.ChecksumIEEE(out)
	binary.LittleEndian.PutUint32(out[16:], crc)
	return out
}

// TestMetadataTLVAuthority 验证 type=5/6 为权威：data_month 压过 Header BuildDate
// 推算值（20260805 → 本应为 "2026-08"），buildTime 始终取自 BuildDate。
func TestMetadataTLVAuthority(t *testing.T) {
	reader, err := NewBuilderBytes(buildSyntheticDB(t)).GroupIndex(0).VerifyCRC(true).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer reader.Close()

	if got := reader.GetDataMonth(); got != "2026-07" {
		t.Errorf("GetDataMonth=%q, want TLV-authoritative %q (BuildDate fallback would be %q)", got, "2026-07", "2026-08")
	}
	if got := reader.GetScope(); got != "global" {
		t.Errorf("GetScope=%q, want %q", got, "global")
	}
	if got := reader.GetBuildTime(); got != "2026-08-05" {
		t.Errorf("GetBuildTime=%q, want %q (always from Header BuildDate)", got, "2026-08-05")
	}
}

// TestMetadataFallbackWithoutTLV56 验证无 type=5/6 的旧文件行为零变化。
func TestMetadataFallbackWithoutTLV56(t *testing.T) {
	reader, err := NewBuilderBytes(buildSyntheticDBNoTLV56(t)).GroupIndex(0).VerifyCRC(true).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer reader.Close()

	if got := reader.GetDataMonth(); got != "2026-08" {
		t.Errorf("GetDataMonth=%q, want BuildDate fallback %q", got, "2026-08")
	}
	if got := reader.GetScope(); got != "" {
		t.Errorf("GetScope=%q, want \"\" for legacy file", got)
	}
	if got := reader.GetBuildTime(); got != "2026-08-05" {
		t.Errorf("GetBuildTime=%q, want %q", got, "2026-08-05")
	}
}
