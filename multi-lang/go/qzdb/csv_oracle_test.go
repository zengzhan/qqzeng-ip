package qzdb

// csv_oracle_test.go — 独立真值正确性校验（Tier0）。
//
// 与 golden_test.go 不同：golden 向量由被测代码自身生成，只证明确定性 /
// 跨语言一致；本测试以 .qzdb 的源数据（test_data_202608/{std,ult}/china/
// *_range.csv，带 start_ip_num/end_ip_num + 地理字段）为独立裁判，抽样比对
// SDK 输出，证明"答得对"而非仅"自洽"。Python 端已有等价 test_csv_oracle.py。
//
// 运行：go test -run TestCSVOracle ./qzdb/...
// 源 CSV 缺失时优雅跳过；任何失配 t.Fatalf 退出非 0。

import (
	"encoding/csv"
	"fmt"
	"math/rand"
	"os"
	"path/filepath"
	"sort"
	"strconv"
	"testing"
)

type csvRow struct {
	start, end           uint64
	country, province    string
	city, isp            string
}

func csvSourcePath(rel string) string {
	candidates := []string{
		filepath.Join("..", "..", "..", "test_data_202608", rel),
		filepath.Join("..", "..", "test_data_202608", rel),
		filepath.Join("..", "..", "..", "..", "test_data_202608", rel),
	}
	for _, c := range candidates {
		if _, err := os.Stat(c); err == nil {
			return c
		}
	}
	return ""
}

func loadCSVOracle(path string) ([]csvRow, []uint64) {
	f, err := os.Open(path)
	if err != nil {
		return nil, nil
	}
	defer f.Close()
	r := csv.NewReader(f)
	hdr, err := r.Read()
	if err != nil {
		return nil, nil
	}
	idx := make(map[string]int, len(hdr))
	for i, h := range hdr {
		idx[h] = i
	}
	var rows []csvRow
	for {
		rec, err := r.Read()
		if err != nil {
			break
		}
		s, _ := strconv.ParseUint(rec[idx["start_ip_num"]], 10, 64)
		e, _ := strconv.ParseUint(rec[idx["end_ip_num"]], 10, 64)
		rows = append(rows, csvRow{
			start:    s,
			end:      e,
			country:  rec[idx["country"]],
			province: rec[idx["province"]],
			city:     rec[idx["city"]],
			isp:      rec[idx["isp"]],
		})
	}
	sort.Slice(rows, func(i, j int) bool { return rows[i].start < rows[j].start })
	starts := make([]uint64, len(rows))
	for i := range rows {
		starts[i] = rows[i].start
	}
	return rows, starts
}

// csvLookup 二分查找包含 ip 的区间（区间按 start 有序、互不重叠）。
func csvLookup(rows []csvRow, starts []uint64, ip uint64) *csvRow {
	i := sort.Search(len(starts), func(k int) bool { return starts[k] > ip }) - 1
	if i >= 0 && rows[i].start <= ip && ip <= rows[i].end {
		return &rows[i]
	}
	return nil
}

func ipToStr(ip uint32) string {
	return fmt.Sprintf("%d.%d.%d.%d", ip>>24, (ip>>16)&0xff, (ip>>8)&0xff, ip&0xff)
}

func runCSVOracle(t *testing.T, label, dbName, csvRel string) int {
	dbPath := realDBPath(dbName)
	if dbPath == "" {
		t.Logf("SKIP %s: db not found (%s)", label, dbName)
		return 0
	}
	csvPath := csvSourcePath(csvRel)
	if csvPath == "" {
		t.Logf("SKIP %s: source csv not found (%s)", label, csvRel)
		return 0
	}
	rows, starts := loadCSVOracle(csvPath)
	if len(rows) == 0 {
		t.Logf("SKIP %s: empty csv", label)
		return 0
	}
	reader, err := NewBuilder(dbPath).VerifyCRC(true).Build()
	if err != nil {
		t.Fatalf("load %s: %v", dbName, err)
	}
	defer reader.Close()

	rng := rand.New(rand.NewSource(12345))
	const globalSamples = 5000
	const inRangeSamples = 6000
	mismatch := 0
	checked := 0
	foundBoth := 0
	missBoth := 0
	var details []string

	checkOne := func(ip uint32) {
		exp := csvLookup(rows, starts, uint64(ip))
		g, _ := reader.Find(ipToStr(ip))
		var sdkCountry, sdkProvince, sdkCity, sdkIsp string
		if g != nil {
			sdkCountry = g.GetCountry()
			sdkProvince = g.GetProvince()
			sdkCity = g.GetCity()
			sdkIsp = g.GetIsp()
		}
		checked++
		if exp == nil && g == nil {
			missBoth++
			return
		}
		if exp != nil && g != nil {
			foundBoth++
			if sdkCountry != exp.country || sdkProvince != exp.province ||
				sdkCity != exp.city || sdkIsp != exp.isp {
				mismatch++
				if len(details) < 12 {
					details = append(details, fmt.Sprintf("ip=%s sdk=(%s|%s|%s|%s) csv=(%s|%s|%s|%s)",
						ipToStr(ip), sdkCountry, sdkProvince, sdkCity, sdkIsp,
						exp.country, exp.province, exp.city, exp.isp))
				}
			}
			return
		}
		mismatch++
		if len(details) < 12 {
			details = append(details, fmt.Sprintf("ip=%s sdk_found=%v csv_found=%v",
				ipToStr(ip), g != nil, exp != nil))
		}
	}

	// 1) 全局随机 IPv4 空间
	for i := 0; i < globalSamples; i++ {
		checkOne(rng.Uint32())
	}
	// 2) 区间内随机（最大化 found_both 覆盖）
	for i := 0; i < inRangeSamples; i++ {
		row := rows[rng.Intn(len(rows))]
		lo, hi := row.start, row.end
		if hi > lo {
			span := hi - lo + 1
			if span > 0xFFFFFFFF {
				span = 0xFFFFFFFF
				hi = lo + span - 1
			}
			off := rng.Int63n(int64(span))
			checkOne(uint32(lo + uint64(off)))
		}
	}

	status := "OK"
	if mismatch != 0 {
		status = "FAIL"
	}
	t.Logf("%s: %s checked=%d found_both=%d miss_both=%d MISMATCH=%d",
		label, status, checked, foundBoth, missBoth, mismatch)
	for _, d := range details {
		t.Logf("  MISMATCH %s", d)
	}
	return mismatch
}

func TestCSVOracle(t *testing.T) {
	total := 0
	total += runCSVOracle(t, "std_china", "qqzeng_ip_std_china.qzdb",
		filepath.Join("std", "china", "qqzeng_ip_std_china_range.csv"))
	total += runCSVOracle(t, "ult_china", "qqzeng_ip_ult_china.qzdb",
		filepath.Join("ult", "china", "qqzeng_ip_ult_china_range.csv"))
	if total != 0 {
		t.Fatalf("CSV_ORACLE FAIL: total MISMATCH=%d", total)
	}
}
