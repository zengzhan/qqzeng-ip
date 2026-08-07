package qzdb

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
)

type goldenCase struct {
	IP       string `json:"ip"`
	Expected string `json:"expected"`
	Label    string `json:"label"`
}

func goldenFilePath() string {
	candidates := []string{
		filepath.Join("..", "..", "tools", "golden_vectors.json"),
		filepath.Join("..", "..", "..", "tools", "golden_vectors.json"),
	}
	for _, c := range candidates {
		if _, err := os.Stat(c); err == nil {
			return c
		}
	}
	return ""
}

// TestGoldenTier2 强制 0 失败：对 golden_vectors.json 中 std_china / ult_china，
// 断言 find(ip).ToPipe() == expected（未命中/非法映射为 ""）。
func TestGoldenTier2(t *testing.T) {
	gp := goldenFilePath()
	if gp == "" {
		t.Skip("golden_vectors.json not found; skipping Tier2 golden test")
	}
	data, err := os.ReadFile(gp)
	if err != nil {
		t.Fatalf("read golden: %v", err)
	}
	var root map[string]json.RawMessage
	if err := json.Unmarshal(data, &root); err != nil {
		t.Fatalf("parse golden: %v", err)
	}

	type libSpec struct {
		libKey  string
		dbFile  string
	}
	libs := []libSpec{
		{"std_china", "qqzeng_ip_std_china.qzdb"},
		{"ult_china", "qqzeng_ip_ult_china.qzdb"},
	}

	total, failures := 0, 0
	for _, spec := range libs {
		raw, ok := root[spec.libKey]
		if !ok {
			t.Fatalf("golden missing lib %s", spec.libKey)
		}
		var lib map[string]json.RawMessage
		if err := json.Unmarshal(raw, &lib); err != nil {
			t.Fatalf("parse lib %s: %v", spec.libKey, err)
		}
		dbp := realDBPath(spec.dbFile)
		if dbp == "" {
			t.Skipf("db %s not found; skipping golden for %s", spec.dbFile, spec.libKey)
		}
		reader, err := NewBuilder(dbp).Build()
		if err != nil {
			t.Fatalf("load %s: %v", spec.dbFile, err)
		}
		for cat, craw := range lib {
			var cases []goldenCase
			if err := json.Unmarshal(craw, &cases); err != nil {
				continue // 非用例类别（如 db / seed）
			}
			for _, c := range cases {
				total++
				info, _ := reader.Find(c.IP)
				got := ""
				if info != nil {
					got = info.ToPipe()
				}
				if got != c.Expected {
					failures++
					if failures <= 20 {
						t.Errorf("[%s/%s] ip=%q label=%q expected=%q got=%q",
							spec.libKey, cat, c.IP, c.Label, c.Expected, got)
					}
				}
			}
		}
		reader.Close()
	}
	if failures > 0 {
		t.Fatalf("Tier2 golden FAILED: %d/%d mismatches", failures, total)
	}
	t.Logf("Tier2 golden PASSED: %d assertions, 0 failures", total)
}
