package qzdb

import (
	"testing"
)

// 包级 sink 变量：防止编译器把基准结果优化掉（dead-code elimination）。
var (
	benchSinkParse *parseResult
	benchSinkOk    bool
	benchSinkGeo   *GeoInfo
	benchSinkStr   string
)

// ---------- IP 解析基准（经典写法：按切片下标循环，不使用 b.Loop） ----------

// BenchmarkFastParseIPv4 固定 v4 字面量解析吞吐。
func BenchmarkFastParseIPv4(b *testing.B) {
	ips := []string{"0.0.0.0", "255.255.255.255", "114.114.114.114", "223.5.5.5", "8.8.8.8", "192.168.1.1"}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		res, ok := fastParseIp(ips[i%len(ips)])
		benchSinkParse, benchSinkOk = res, ok
	}
}

// BenchmarkFastParseIPv6 固定 v6 字面量解析吞吐。
func BenchmarkFastParseIPv6(b *testing.B) {
	ips := []string{"::1", "2001:db8::1", "2408:8000:9000::1", "1:2:3:4:5:6:7:8", "fe80::1", "::"}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		res, ok := fastParseIp(ips[i%len(ips)])
		benchSinkParse, benchSinkOk = res, ok
	}
}

// BenchmarkFastParseMapped IPv4-mapped IPv6 自动降级吞吐。
func BenchmarkFastParseMapped(b *testing.B) {
	ips := []string{"::ffff:114.114.114.114", "::ffff:7272:7272", "::ffff:8.8.8.8", "::ffff:223.5.5.5"}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		res, ok := fastParseIp(ips[i%len(ips)])
		benchSinkParse, benchSinkOk = res, ok
	}
}

// ---------- GeoInfo 序列化基准 ----------

// buildUltGeoInfo 构造一个贴近 ult 档次的 GeoInfo（字段名取自导出的 EditionFieldNames）。
// 数值字段（longitude/latitude/asn/geo_id）按真实长度填充，便于衡量序列化开销。
func buildUltGeoInfo() *GeoInfo {
	names := EditionFieldNames["ult"]
	values := []string{
		"亚洲", "Asia", "CN", "CHN", "中国", "China",
		"江苏", "Jiangsu", "南京", "Nanjing", "鼓楼", "Gulou",
		"320106", "118.767410", "32.041546", "UTC+8",
		"zh-CN,zh,en", "CNY", "86", "\U0001F1E8\U0001F1F3",
		"中国电信", "137702", "Chinanet", "chinatelecom.cn", "DNS",
	}
	// 与 names 对齐：numeric 标记 longitude/latitude/asn/geo_id 四个字段。
	numeric := make([]bool, len(names))
	for i, n := range names {
		switch n {
		case "longitude", "latitude", "asn", "geo_id":
			numeric[i] = true
		}
	}
	return &GeoInfo{
		FieldNames: names,
		Values:     values,
		numeric:    numeric,
		normMap:    buildNormalizedMap(names),
	}
}

// BenchmarkGeoInfoToPipe 竖线拼接吞吐。
func BenchmarkGeoInfoToPipe(b *testing.B) {
	g := buildUltGeoInfo()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		benchSinkStr = g.ToPipe()
	}
}

// BenchmarkGeoInfoToJson 手写 JSON 序列化吞吐。
func BenchmarkGeoInfoToJson(b *testing.B) {
	g := buildUltGeoInfo()
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		benchSinkStr = g.ToJson()
	}
}

// BenchmarkEscapeJsonPlain 无转义字符的快速路径吞吐。
func BenchmarkEscapeJsonPlain(b *testing.B) {
	s := "中国电信江苏南京鼓楼chinatelecom.cn"
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		benchSinkStr = escapeJson(s)
	}
}

// BenchmarkEscapeJsonEscaped 含引号/反斜杠/换行的慢速路径吞吐。
func BenchmarkEscapeJsonEscaped(b *testing.B) {
	s := "中国\"江苏\\南京\n鼓楼\tISP"
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		benchSinkStr = escapeJson(s)
	}
}

// ---------- 数据库查询基准（沿用现有数据文件 skip 模式） ----------

// BenchmarkFindV4 单线程 IPv4 查询吞吐（数据库缺失时跳过）。
func BenchmarkFindV4(b *testing.B) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		b.Skip("real db not found")
	}
	r, err := NewBuilder(dbPath).Build()
	if err != nil {
		b.Fatalf("build: %v", err)
	}
	defer r.Close()

	ips := []string{"114.114.114.114", "223.5.5.5", "1.2.3.4", "120.53.1.1", "180.76.76.76"}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		g, _ := r.Find(ips[i%len(ips)])
		benchSinkGeo = g
	}
}

// BenchmarkFindStr 单线程 FindStr 吞吐（数据库缺失时跳过）。
func BenchmarkFindStr(b *testing.B) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		b.Skip("real db not found")
	}
	r, err := NewBuilder(dbPath).Build()
	if err != nil {
		b.Fatalf("build: %v", err)
	}
	defer r.Close()

	ips := []string{"114.114.114.114", "223.5.5.5", "1.2.3.4", "120.53.1.1", "180.76.76.76"}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		benchSinkStr = r.FindStr(ips[i%len(ips)])
	}
}

// BenchmarkLookupCidr 单线程 CIDR 反查吞吐（数据库缺失时跳过）。
func BenchmarkLookupCidr(b *testing.B) {
	dbPath := realDBPath("qqzeng_ip_std_china.qzdb")
	if dbPath == "" {
		b.Skip("real db not found")
	}
	r, err := NewBuilder(dbPath).Build()
	if err != nil {
		b.Fatalf("build: %v", err)
	}
	defer r.Close()

	ips := []string{"114.114.114.114", "223.5.5.5", "1.2.3.4", "120.53.1.1", "180.76.76.76"}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		benchSinkStr = r.LookupCidr(ips[i%len(ips)])
	}
}
