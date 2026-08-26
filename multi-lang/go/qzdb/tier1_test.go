package qzdb

import (
	"encoding/json"
	"math"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
)

// realDBPath 返回 bundled 数据文件路径（不存在则返回 ""）。
func realDBPath(name string) string {
	candidates := []string{
		filepath.Join("..", "..", "data", name),
		filepath.Join("..", "..", "..", "data", name),
	}
	for _, c := range candidates {
		if _, err := os.Stat(c); err == nil {
			return c
		}
	}
	return ""
}

func TestStrictIPv4Parsing(t *testing.T) {
	ok := func(ip string) bool {
		_, good := fastParseIp(ip)
		return good
	}
	// 合法
	if !ok("0.0.0.0") {
		t.Fatal("0.0.0.0 should parse")
	}
	if !ok("255.255.255.255") {
		t.Fatal("255.255.255.255 should parse")
	}
	if !ok("114.114.114.114") {
		t.Fatal("normal v4 should parse")
	}
	// 非法：前导零段 / 段越界（严格解析，前导零全拒绝）
	cases := []string{
		"192.168.001.001", "1.2.3", "1.2.3.4.5", "114..114.114.114", ".1.2.3.4", "1.2.3.4.",
		"256.1.2.3", "-1.2.3.4", "a.b.c.d", "1.2.3.4:80", "01.2.3.4",
		"1.2.3.256", "1.2.3.4 ", " 1.2.3.4", "", "1.2.3.4\n",
		"1.2.3.4.5.6",
	}
	for _, c := range cases {
		if ok(c) {
			t.Errorf("expected invalid IPv4: %q", c)
		}
	}
	// 段超长 / 越界
	if ok("1.2.3.4.5") {
		t.Error("5 segments should be invalid")
	}
}

func TestStrictIPv6Parsing(t *testing.T) {
	ok := func(ip string) bool {
		_, good := fastParseIp(ip)
		return good
	}
	if !ok("::1") {
		t.Fatal("::1 should parse")
	}
	if !ok("2001:db8::1") {
		t.Fatal("2001:db8::1 should parse")
	}
	if !ok("2001:0db8:0000:0000:0000:0000:0000:0001") {
		t.Fatal("full v6 should parse")
	}
	if !ok("::ffff:114.114.114.114") {
		t.Fatal("v4-mapped should parse")
	}
	if !ok("1:2:3:4:5:6:7:8") {
		t.Fatal("8 groups should parse")
	}
	// 非法
	bad := []string{
		":::", "1::2::3", "gggg::1", "1:2:3:4:5:6:7:8:9",
		"12345::1", "fe80::1%eth0", "1.2.3.4.5:6:7:8", "1:2:3:4:5:6:7",
		"1:2:3:4:5:6:7:8:9", "2001:db8::/32", "1:2:3:4:5:6:7:8:",
		"", "1:2:3:4:5:6:7:8z",
	}
	for _, c := range bad {
		if ok(c) {
			t.Errorf("expected invalid IPv6: %q", c)
		}
	}
}

func TestMappedDowngrade(t *testing.T) {
	db := buildSyntheticDB(t)
	r, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build synthetic: %v", err)
	}
	defer r.Close()
	// ::ffff:114.114.114.114 应等价于 114.114.114.114（字段级一致）
	a, _ := r.Find("114.114.114.114")
	b, _ := r.Find("::ffff:114.114.114.114")
	if a == nil || b == nil {
		t.Fatalf("both should hit: a=%v b=%v", a, b)
	}
	if a.ToPipe() != b.ToPipe() {
		t.Errorf("mapped mismatch: %q vs %q", a.ToPipe(), b.ToPipe())
	}
	// 十六进制形态 ::ffff:7272:7272 也应等价
	c, _ := r.Find("::ffff:7272:7272")
	if c == nil || c.ToPipe() != a.ToPipe() {
		t.Errorf("hex mapped mismatch: %v", c)
	}
}

func TestFieldNormalization(t *testing.T) {
	g := &GeoInfo{
		FieldNames: []string{"country_code", "country_en", "geo_id"},
		Values:     []string{"CN", "China", "320100"},
		normMap:    buildNormalizedMap([]string{"country_code", "country_en", "geo_id"}),
	}
	// 大小写 / 下划线 / 连字符不敏感且等价
	if g.Get("country_code") != "CN" {
		t.Error("exact fail")
	}
	if g.Get("countryCode") != "CN" {
		t.Error("underscore fail")
	}
	if g.Get("COUNTRY_CODE") != "CN" {
		t.Error("upper fail")
	}
	if g.Get("Country-Code") != "CN" {
		t.Error("dash fail")
	}
	if g.Get("country_en") != "China" {
		t.Error("en exact fail")
	}
	if g.Get("countryEn") != "China" {
		t.Error("en camel fail")
	}
	if g.Get("country-en") != "China" {
		t.Error("en dash fail")
	}
	if g.Get("geo_id") != "320100" {
		t.Error("geoid fail")
	}
	if g.Get("geoId") != "320100" {
		t.Error("geoId camel fail")
	}
	// 未匹配返回 ""，绝不 panic
	if g.Get("nonexistent") != "" {
		t.Error("missing should be empty")
	}
	if g.Get("") != "" {
		t.Error("empty key should be empty")
	}
}

func TestUsageType21(t *testing.T) {
	known := KnownUsageTypes()
	if len(known) != 21 {
		t.Fatalf("expected 21 known usage types, got %d", len(known))
	}
	for _, k := range known {
		if !k.IsKnown() {
			t.Errorf("%s should be known", k.RawValue())
		}
	}
	// 21 个场景原始值覆盖
	expected := []string{"AICrawler", "Backbone", "Broadband", "Business", "CDN", "Cloud",
		"DNS", "DataCenter", "Education", "Finance", "Government", "ISP", "IXP", "IoT",
		"Mobile", "Reserved", "Satellite", "Spider", "Streaming", "Unknown", "VPN"}
	for i, e := range expected {
		if known[i].RawValue() != e {
			t.Errorf("usage[%d] = %s, want %s", i, known[i].RawValue(), e)
		}
	}
	// 未知兜底
	u := ParseUsageType("MyCustomType")
	if u.IsKnown() {
		t.Error("MyCustomType should be unknown")
	}
	if u.RawValue() != "MyCustomType" {
		t.Error("unknown raw should be preserved")
	}
	if ParseUsageType("").RawValue() != "Unknown" {
		t.Error("empty should map to Unknown")
	}
	if ParseUsageType("broadband").RawValue() != "Broadband" {
		t.Error("case-insensitive match fail")
	}
	if ParseUsageType("VPN").DisplayZh() != "VPN/代理" {
		t.Error("display zh fail")
	}
}

func TestToPipeAndToJson(t *testing.T) {
	g := &GeoInfo{
		FieldNames: []string{"continent", "country", "longitude", "latitude", "geo_id", "asn"},
		Values:     []string{"亚洲", "中国", "116.400000", "39.900000", "320100", "137702"},
		numeric:    []bool{false, false, true, true, true, true},
	}
	pipe := g.ToPipe()
	if pipe != "亚洲|中国|116.400000|39.900000|320100|137702" {
		t.Errorf("toPipe wrong: %q", pipe)
	}
	if g.String() != pipe {
		t.Error("String() should equal ToPipe()")
	}
	// toJson 数值类型
	js := g.ToJson()
	var m map[string]any
	if err := json.Unmarshal([]byte(js), &m); err != nil {
		t.Fatalf("json invalid: %v\n%s", err, js)
	}
	if m["longitude"] != 116.400000 {
		t.Errorf("longitude should be number 116.4, got %v", m["longitude"])
	}
	if m["geo_id"] != float64(320100) {
		t.Errorf("geo_id should be number, got %v", m["geo_id"])
	}
	if m["continent"] != "亚洲" {
		t.Errorf("continent should be string, got %v", m["continent"])
	}
	// 空值数值字段 → null
	ge := &GeoInfo{
		FieldNames: []string{"longitude", "country"},
		Values:     []string{"", "中国"},
		numeric:    []bool{true, false},
	}
	if !strings.Contains(ge.ToJson(), `"longitude":null`) {
		t.Errorf("empty numeric should be null: %s", ge.ToJson())
	}
}

func TestSemanticGetters(t *testing.T) {
	g := &GeoInfo{
		FieldNames: []string{"country", "country_en", "province", "city", "longitude", "latitude", "asn", "usage_type", "cidr"},
		Values:     []string{"中国", "China", "江苏", "南京", "118.767410", "32.041546", "137702", "DNS", "1.2.3.0/24"},
		numeric:    []bool{false, false, false, false, true, true, true, false, false},
	}
	if g.GetCountry() != "中国" {
		t.Error("GetCountry")
	}
	if g.GetCountryEn() != "China" {
		t.Error("GetCountryEn")
	}
	if g.GetProvince() != "江苏" {
		t.Error("GetProvince")
	}
	if g.GetCity() != "南京" {
		t.Error("GetCity")
	}
	if g.GetLongitude() == nil || *g.GetLongitude() != 118.767410 {
		t.Error("GetLongitude")
	}
	if g.GetLatitude() == nil || *g.GetLatitude() != 32.041546 {
		t.Error("GetLatitude")
	}
	if g.GetAsn() == nil || *g.GetAsn() != 137702 {
		t.Error("GetAsn")
	}
	if g.GetUsageType().RawValue() != "DNS" {
		t.Error("GetUsageType")
	}
	// getCidr 恒返回 ""
	if g.GetCidr() != "" {
		t.Errorf("GetCidr must always return empty, got %q", g.GetCidr())
	}
	// 缺失数值返回 nil
	empty := &GeoInfo{FieldNames: []string{"longitude"}, Values: []string{""}, numeric: []bool{true}}
	if empty.GetLongitude() != nil {
		t.Error("missing longitude should be nil")
	}
}

func TestCorruptedFailClosed(t *testing.T) {
	// 1) 坏 magic
	bad := make([]byte, 256)
	copy(bad[0:4], "XXXX")
	_, err := NewBuilderBytes(bad).Build()
	if err == nil {
		t.Error("bad magic should fail closed")
	}
	// 2) 不支持的版本
	ver := make([]byte, 256)
	copy(ver[0:4], "QZDB")
	ver[4] = 2
	_, err = NewBuilderBytes(ver).Build()
	if err == nil {
		t.Error("unsupported version should fail closed")
	}
	// 3) 截断文件
	trunc := buildSyntheticDB(t)[:100]
	_, err = NewBuilderBytes(trunc).Build()
	if err == nil {
		t.Error("truncated file should fail closed")
	}
	// 4) CRC 不匹配
	crcBad := buildSyntheticDB(t)
	crcBad[len(crcBad)-1] ^= 0xFF
	_, err = NewBuilderBytes(crcBad).Build()
	if err == nil {
		t.Error("crc mismatch should fail closed")
	}
	// 5) 关闭 CRC 校验则可加载（受信数据）
	_, err = NewBuilderBytes(crcBad).VerifyCRC(false).Build()
	if err != nil {
		t.Errorf("with verifyCrc=false should load: %v", err)
	}
}

func TestCRCEnforcement(t *testing.T) {
	db := buildSyntheticDB(t)
	r, err := NewBuilderBytes(db).VerifyCRC(true).Build()
	if err != nil {
		t.Fatalf("valid db should build: %v", err)
	}
	defer r.Close()
	if !r.VerifyCRC() {
		t.Error("VerifyCRC should be true for valid db")
	}
	// 文件哈希为 8 位小写十六进制
	h := r.GetFileHash()
	if len(h) != 8 {
		t.Errorf("file hash should be 8 hex chars, got %q", h)
	}
	if h != strings.ToLower(h) {
		t.Error("file hash should be lowercase")
	}
}

func TestReloadAtomic(t *testing.T) {
	db1 := buildSyntheticDB(t)
	r, err := NewBuilderBytes(db1).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	defer r.Close()
	g1, _ := r.Find("114.114.114.114")
	if g1 == nil || !strings.Contains(g1.ToPipe(), "CHINANET") {
		t.Fatalf("pre-reload query wrong: %v", g1)
	}
	// 热替换：ReloadBuffer 加载另一份（修改了 pool[5] 文案）
	db2 := modifySyntheticASName(t, db1, "RELOADED")
	if err := r.ReloadBuffer(db2); err != nil {
		t.Fatalf("reload: %v", err)
	}
	g2, _ := r.Find("114.114.114.114")
	if g2 == nil || !strings.Contains(g2.ToPipe(), "RELOADED") {
		t.Fatalf("post-reload query should reflect new data: %v", g2)
	}
	// 失败保留旧快照：损坏的 reload 不应影响旧数据
	bad := make([]byte, 200)
	copy(bad[0:4], "XXXX")
	if err := r.ReloadBuffer(bad); err == nil {
		t.Error("bad reload should error")
	}
	g3, _ := r.Find("114.114.114.114")
	if g3 == nil || !strings.Contains(g3.ToPipe(), "RELOADED") {
		t.Errorf("old snapshot should survive failed reload: %v", g3)
	}
}

func TestResourceRelease(t *testing.T) {
	db := buildSyntheticDB(t)
	r, err := NewBuilderBytes(db).Build()
	if err != nil {
		t.Fatalf("build: %v", err)
	}
	r.Close()
	// 关闭后查询安全失败，不 panic / 不 UAF
	if _, err := r.Find("114.114.114.114"); err != ErrClosed {
		t.Errorf("post-close Find should be ErrClosed, got %v", err)
	}
	if r.FindStr("114.114.114.114") != "" {
		t.Error("post-close FindStr should be empty")
	}
	if r.LookupCidr("114.114.114.114") != "" {
		t.Error("post-close LookupCidr should be empty")
	}
}

func TestFindSemantics(t *testing.T) {
	db := buildSyntheticDB(t)
	r, _ := NewBuilderBytes(db).Build()
	defer r.Close()
	// 未命中返回 (nil, nil)
	g, err := r.Find("9.9.9.9")
	if g != nil || err != nil {
		t.Errorf("miss should be (nil, nil), got %v %v", g, err)
	}
	// 非法 IP 返回 (nil, nil)
	g, err = r.Find("not-an-ip")
	if g != nil || err != nil {
		t.Errorf("invalid should be (nil, nil), got %v %v", g, err)
	}
	// 命中
	g, err = r.Find("114.114.114.114")
	if g == nil || err != nil {
		t.Errorf("hit should return geo, got %v %v", g, err)
	}
	// FindStr 未命中返回 ""
	if r.FindStr("9.9.9.9") != "" {
		t.Error("FindStr miss should be empty")
	}
}

func TestCIDRRealtime(t *testing.T) {
	path := realDBPath("qqzeng_ip_std_china.qzdb")
	if path == "" {
		t.Skip("std_china db not present; skipping CIDR real-db test")
	}
	r, err := NewBuilder(path).Build()
	if err != nil {
		t.Fatalf("load std_china: %v", err)
	}
	defer r.Close()

	v4Re := regexp.MustCompile(`^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$`)
	v6Re := regexp.MustCompile(`^[0-9a-f:]+/\d{1,3}$`)

	// 覆盖的 V4 应返回格式正确的 CIDR
	c4 := r.LookupCidr("114.114.114.114")
	if !v4Re.MatchString(c4) {
		t.Errorf("expected v4 CIDR, got %q", c4)
	}
	assertCidrContainsIP(t, c4, "114.114.114.114")

	// 覆盖的 V6 应返回 V6 CIDR
	c6 := r.LookupCidr("2408:8000:9000::1")
	if !v6Re.MatchString(c6) {
		t.Errorf("expected v6 CIDR, got %q", c6)
	}

	// 未覆盖返回 ""
	if r.LookupCidr("8.8.8.8") != "" {
		t.Error("uncovered IP should yield empty CIDR")
	}
	// 非法 IP 返回 ""
	if r.LookupCidr("not-an-ip") != "" {
		t.Error("invalid IP should yield empty CIDR")
	}
	// LookupRowId 与 LookupIds 一致性
	rowID := r.LookupRowId("114.114.114.114")
	if rowID == 0 {
		t.Error("LookupRowId should be non-zero for hit")
	}
	ids := r.LookupIds(rowID)
	if ids == nil {
		t.Error("LookupIds should return non-nil for valid row")
	}
}

// assertCidrContainsIP 校验 CIDR 网络地址包含该 IP（自洽性）。
func assertCidrContainsIP(t *testing.T, cidr, ipStr string) {
	t.Helper()
	idx := strings.Index(cidr, "/")
	if idx < 0 {
		t.Fatalf("malformed CIDR %q", cidr)
	}
	prefixLen := 0
	for _, c := range cidr[idx+1:] {
		prefixLen = prefixLen*10 + int(c-'0')
	}
	netParts := strings.Split(cidr[:idx], ".")
	ipParts := strings.Split(ipStr, ".")
	if len(netParts) != 4 || len(ipParts) != 4 {
		t.Fatalf("v4 parse fail %q %q", cidr, ipStr)
	}
	for i := 0; i < 4; i++ {
		nb, _ := parseOctet(netParts[i])
		ib, _ := parseOctet(ipParts[i])
		bits := prefixLen
		if bits > 32 {
			bits = 32
		}
		if bits > 8 {
			bits = 8
		}
		mask := byte(0)
		if bits > 0 {
			mask = byte(0xFF << (8 - uint(bits)))
		}
		if (nb & mask) != (ib & mask) {
			t.Errorf("CIDR %q does not contain IP %q at byte %d", cidr, ipStr, i)
		}
		if prefixLen > 8 {
			prefixLen -= 8
		} else {
			prefixLen = 0
		}
	}
}

func parseOctet(s string) (byte, bool) {
	var v int
	for _, c := range s {
		if c < '0' || c > '9' {
			return 0, false
		}
		v = v*10 + int(c-'0')
	}
	if v > 255 {
		return 0, false
	}
	return byte(v), true
}

func TestFloatFormat6(t *testing.T) {
	// 整数值无小数点
	if got := formatFloat6(116.0); got != "116" {
		t.Errorf("116.0 -> %q, want 116", got)
	}
	if got := formatFloat6(0.0); got != "0" {
		t.Errorf("0.0 -> %q, want 0", got)
	}
	// 非整数固定 6 位小数
	if got := formatFloat6(116.4); got != "116.400000" {
		t.Errorf("116.4 -> %q, want 116.400000", got)
	}
	if got := formatFloat6(-32.041546); got != "-32.041546" {
		t.Errorf("-32.041546 -> %q", got)
	}
	// NaN / Inf
	if got := formatFloat6(math.NaN()); got != "" {
		t.Errorf("NaN -> %q, want empty", got)
	}
	if got := formatFloat6(math.Inf(1)); got != "" {
		t.Errorf("Inf -> %q, want empty", got)
	}
}

func TestFindFieldsProjection(t *testing.T) {
	db := buildSyntheticDB(t)
	r, _ := NewBuilderBytes(db).Build()
	defer r.Close()
	// 投影只返回指定字段
	g, err := r.FindFields("114.114.114.114", []string{"country", "isp"})
	if err != nil || g == nil {
		t.Fatalf("FindFields: %v %v", g, err)
	}
	if len(g.FieldNames) != 2 {
		t.Fatalf("expected 2 projected fields, got %d", len(g.FieldNames))
	}
	if g.Get("isp") != "中国电信" {
		t.Errorf("projected isp wrong: %q", g.Get("isp"))
	}
	// 归一化字段名投影
	g2, _ := r.FindFields("114.114.114.114", []string{"country_code"})
	if g2 == nil || g2.Get("countryCode") != "CN" {
		t.Errorf("normalized projection wrong: %v", g2)
	}
}
