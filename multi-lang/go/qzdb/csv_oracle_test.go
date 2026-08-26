package qzdb

// csv_oracle_test.go — 独立真值正确性校验（Tier0）。
//
// 与 golden_test.go 不同：golden 向量由被测代码自身生成，只证明确定性 /
// 跨语言一致；本测试以 .qzdb 的源数据（test_data_202608/{edition}/{region}/
// *_range.csv，带 start_ip_num/end_ip_num + 地理字段）为独立裁判，抽样 +
// 边界扫描比对 SDK 输出，证明"答得对"而非仅"自洽"。Python 端已有等价实现。
//
// 三类探针：
//  1. 全局随机 IPv4（覆盖未命中语义）
//  2. 区间内随机（最大化命中覆盖）
//  3. 区间边界确定性扫描：start / end / start-1 / end+1 / 中点 —— 区间型
//     检索最易错的是 off-by-one；行数超过 boundaryCap 时按确定性步长抽样，
//     首尾行必测。start-1 / end+1 探针同时验证相邻区间衔接与未命中语义。
//
// ASN 档 Schema 不含 province/city 字段（FORMAT 附录 1），该档仅比对
// country/isp，避免"SDK 未携带该维度"与"答错"两类问题混为一谈。
//
// 运行：go test -run TestCSVOracle ./qzdb/...
// 源 CSV 缺失时优雅跳过；任何失配 t.Fatalf 退出非 0。

import (
	"cmp"
	"encoding/csv"
	"fmt"
	"math/rand"
	"os"
	"path/filepath"
	"slices"
	"sort"
	"strconv"
	"strings"
	"testing"
)

type csvRow struct {
	start, end        uint64
	country, province string
	city, isp         string
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

func loadCSVOracle(path string) ([]csvRow, []uint64, int) {
	f, err := os.Open(path)
	if err != nil {
		return nil, nil, 0
	}
	defer f.Close()
	r := csv.NewReader(f)
	hdr, err := r.Read()
	if err != nil {
		return nil, nil, 0
	}
	idx := make(map[string]int, len(hdr))
	for i, h := range hdr {
		idx[h] = i
	}
	var rows []csvRow
	skippedV6 := 0
	for {
		rec, err := r.Read()
		if err != nil {
			break
		}
		// global 导出在同一数值列混入 IPv6 行（如 ::2-::fffe:ffff:ffff），
		// 其裸整数与 IPv4 空间数值重叠但语义不相交；v4 探针的真值只取点分十进制行。
		if strings.Contains(rec[idx["start_ip"]], ":") {
			skippedV6++
			continue
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
	slices.SortFunc(rows, func(a, b csvRow) int { return cmp.Compare(a.start, b.start) })
	starts := make([]uint64, len(rows))
	for i := range rows {
		starts[i] = rows[i].start
	}
	return rows, starts, skippedV6
}

// csvCandidates 返回包含 ip 的全部真值行（按 start 有序）。导出数据存在少量
// 重叠行（同段多粒度碎片），单一二分命中会把碎片误当唯一真值；这里自命中位
// 向前回溯收集所有覆盖行。回溯窗口上限 16：重叠仅为局部小簇（实测每 20 万行
// 约 3 行），远小于窗口，超出即视为真值文件异常并截断。
func csvCandidates(rows []csvRow, starts []uint64, ip uint64) []csvRow {
	j := sort.Search(len(starts), func(k int) bool { return starts[k] > ip })
	var out []csvRow
	for i := j - 1; i >= 0 && len(out) < 16; i-- {
		if rows[i].start <= ip && ip <= rows[i].end {
			out = append(out, rows[i])
		}
	}
	return out
}

func ipToStr(ip uint32) string {
	return fmt.Sprintf("%d.%d.%d.%d", ip>>24, (ip>>16)&0xff, (ip>>8)&0xff, ip&0xff)
}

func runCSVOracle(t *testing.T, label, dbName, csvRel string, fullFields bool) int {
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
	rows, starts, skippedV6 := loadCSVOracle(csvPath)
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
	const globalSamples = 8000
	const inRangeSamples = 12000
	const boundaryCap = 30000
	mismatch := 0
	checked := 0
	boundary := 0
	foundBoth := 0
	missBoth := 0
	ambiguous := 0
	var details []string

	checkOne := func(ip uint32) {
		cands := csvCandidates(rows, starts, uint64(ip))
		g, _ := reader.Find(ipToStr(ip))
		var sdkCountry, sdkProvince, sdkCity, sdkIsp string
		if g != nil {
			sdkCountry = g.GetCountry()
			sdkProvince = g.GetProvince()
			sdkCity = g.GetCity()
			sdkIsp = g.GetIsp()
		}
		checked++
		if len(cands) == 0 && g == nil {
			missBoth++
			return
		}
		if len(cands) > 0 && g != nil {
			foundBoth++
			if len(cands) > 1 {
				ambiguous++
			}
			for _, exp := range cands {
				if sdkCountry == exp.country && sdkIsp == exp.isp &&
					(!fullFields || (sdkProvince == exp.province && sdkCity == exp.city)) {
					return
				}
			}
			mismatch++
			if len(details) < 12 {
				exp := cands[0]
				details = append(details, fmt.Sprintf("ip=%s cands=%d sdk=(%s|%s|%s|%s) csv[0]=(%s|%s|%s|%s)",
					ipToStr(ip), len(cands), sdkCountry, sdkProvince, sdkCity, sdkIsp,
					exp.country, exp.province, exp.city, exp.isp))
			}
			return
		}
		mismatch++
		if len(details) < 12 {
			details = append(details, fmt.Sprintf("ip=%s sdk_found=%v csv_found=%v",
				ipToStr(ip), g != nil, len(cands) > 0))
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
	// 3) 边界确定性扫描：start/end/start-1/end+1/中点；超 cap 按步长抽样，首尾行必测。
	stride := 1
	if len(rows) > boundaryCap {
		stride = (len(rows) + boundaryCap - 1) / boundaryCap
	}
	for i := 0; i < len(rows); i += stride {
		row := rows[i]
		if row.start <= 0xFFFFFFFF {
			checkOne(uint32(row.start))
			boundary++
		}
		if row.end <= 0xFFFFFFFF {
			checkOne(uint32(row.end))
			boundary++
		}
		if row.start > 0 && row.start <= 0xFFFFFFFF {
			checkOne(uint32(row.start - 1))
			boundary++
		}
		if row.end < 0xFFFFFFFF {
			checkOne(uint32(row.end + 1))
			boundary++
		}
		if row.end > row.start && row.start <= 0xFFFFFFFF {
			checkOne(uint32(row.start + (row.end-row.start)/2))
			boundary++
		}
	}

	status := "OK"
	if mismatch != 0 {
		status = "FAIL"
	}
	t.Logf("%s: %s checked=%d(boundary=%d ambiguous=%d) found_both=%d miss_both=%d skipped_v6_rows=%d MISMATCH=%d",
		label, status, checked, boundary, ambiguous, foundBoth, missBoth, skippedV6, mismatch)
	for _, d := range details {
		t.Logf("  MISMATCH %s", d)
	}
	return mismatch
}

// oracleCase 一个待对拍的 数据库 ↔ 源 CSV 组合。
// fullFields=false 用于 ASN 档（Schema 无 province/city，仅比对 country/isp）。
type oracleCase struct {
	label      string
	dbName     string
	csvRel     string
	fullFields bool
}

func TestCSVOracle(t *testing.T) {
	if testing.Short() {
		t.Skip("Tier0 全量真值对拍（10 数据集 / 数千万行 CSV 解析）耗时约 10 分钟；快速回路请用 go test -short")
	}
	cases := []oracleCase{
		{"std_china", "qqzeng_ip_std_china.qzdb", "std/china/qqzeng_ip_std_china_range.csv", true},
		{"std_global", "qqzeng_ip_std_global.qzdb", "std/global/qqzeng_ip_std_global_range.csv", true},
		{"pro_china", "qqzeng_ip_pro_china.qzdb", "pro/china/qqzeng_ip_pro_china_range.csv", true},
		{"pro_global", "qqzeng_ip_pro_global.qzdb", "pro/global/qqzeng_ip_pro_global_range.csv", true},
		{"max_china", "qqzeng_ip_max_china.qzdb", "max/china/qqzeng_ip_max_china_range.csv", true},
		{"max_global", "qqzeng_ip_max_global.qzdb", "max/global/qqzeng_ip_max_global_range.csv", true},
		{"ult_china", "qqzeng_ip_ult_china.qzdb", "ult/china/qqzeng_ip_ult_china_range.csv", true},
		{"ult_global", "qqzeng_ip_ult_global.qzdb", "ult/global/qqzeng_ip_ult_global_range.csv", true},
		{"asn_china", "qqzeng_ip_asn_china.qzdb", "asn/china/qqzeng_ip_asn_china_range.csv", false},
		{"asn_global", "qqzeng_ip_asn_global.qzdb", "asn/global/qqzeng_ip_asn_global_range.csv", false},
	}
	total := 0
	for _, c := range cases {
		total += runCSVOracle(t, c.label, c.dbName, c.csvRel, c.fullFields)
	}
	if total != 0 {
		t.Fatalf("CSV_ORACLE FAIL: total MISMATCH=%d", total)
	}
}
