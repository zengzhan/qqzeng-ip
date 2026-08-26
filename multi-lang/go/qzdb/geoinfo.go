package qzdb

import (
	"strconv"
	"strings"
	"sync/atomic"
)

// GeoInfo 是 IP 地理定位与元数据的响应实体。所有字段均为只读，可安全跨 goroutine 共享。
type GeoInfo struct {
	FieldNames []string
	Values     []string

	// normMap 归一化字段名 → 索引（共享、只读）。
	normMap map[string]int
	// numeric 逐字段数值标记（toJson 用）。
	numeric []bool
}

// normalizeKey 归一化算法：小写化并移除所有 '_' 与 '-'（契约 §6）。
// 快速路径：若 key 已归一化（全小写、无 _/-）则零分配返回原字符串。
func normalizeKey(key string) string {
	if key == "" {
		return ""
	}
	// 快速路径：检查是否需要归一化
	needsNormalize := false
	for i := 0; i < len(key); i++ {
		c := key[i]
		if c == '_' || c == '-' || (c >= 'A' && c <= 'Z') {
			needsNormalize = true
			break
		}
	}
	if !needsNormalize {
		return key // 零分配快速路径
	}
	// 慢速路径：构建归一化字符串（预分配容量，避免 nil 切片追加增长）
	sb := make([]byte, 0, len(key))
	for i := 0; i < len(key); i++ {
		c := key[i]
		if c == '_' || c == '-' {
			continue
		}
		if c >= 'A' && c <= 'Z' {
			c += 'a' - 'A'
		}
		sb = append(sb, c)
	}
	return string(sb)
}

// buildNormalizedMap 构造归一化字段索引（加载期一次性构建）。
func buildNormalizedMap(fields []string) map[string]int {
	m := make(map[string]int, len(fields)*2)
	for i, f := range fields {
		if f != "" {
			if _, ok := m[normalizeKey(f)]; !ok {
				m[normalizeKey(f)] = i
			}
		}
	}
	return m
}

// isNumericFieldName 判断 toJson 数值类型字段（longitude/latitude/asn/geo_id）。
// 使用非分配归一化避免字符串分配。
func isNumericFieldName(name string) bool {
	switch len(name) {
	case 3:
		return equalNormalized(name, "asn")
	case 5:
		return equalNormalized(name, "geoid")
	case 8:
		return equalNormalized(name, "latitude")
	case 9:
		return equalNormalized(name, "longitude")
	}
	return false
}

// equalNormalized 比较 key 归一化后是否等于 target（零分配）。
func equalNormalized(key, target string) bool {
	if len(key) != len(target) {
		// 归一化后长度可能因移除 _/- 而不同，需要实际归一化
		return normalizeKey(key) == target
	}
	for i := 0; i < len(key); i++ {
		c := key[i]
		if c == '_' || c == '-' {
			return normalizeKey(key) == target
		}
		if c >= 'A' && c <= 'Z' {
			c += 'a' - 'A'
		}
		if c != target[i] {
			return false
		}
	}
	return true
}

// Get 动态访问字段（大小写/下划线/连字符不敏感）。未匹配返回 ""，绝不 panic。
func (g *GeoInfo) Get(name string) string {
	if g == nil || name == "" {
		return ""
	}
	idx := -1
	if g.normMap != nil {
		if v, ok := g.normMap[normalizeKey(name)]; ok {
			idx = v
		}
	} else {
		for i, n := range g.FieldNames {
			if n == name {
				idx = i
				break
			}
		}
	}
	if idx >= 0 && idx < len(g.Values) {
		return g.Values[idx]
	}
	return ""
}

// ToPipe 返回竖线分隔文本：直接拼接已解码的字符串值，禁止重新格式化。
func (g *GeoInfo) ToPipe() string {
	if g == nil || len(g.Values) == 0 {
		return ""
	}
	if len(g.Values) == 1 {
		return g.Values[0]
	}
	var n int
	for _, v := range g.Values {
		n += len(v) + 1
	}
	var b strings.Builder
	b.Grow(n - 1)
	b.WriteString(g.Values[0])
	for _, v := range g.Values[1:] {
		b.WriteByte('|')
		b.WriteString(v)
	}
	return b.String()
}

// String 等价于 ToPipe()。
func (g *GeoInfo) String() string { return g.ToPipe() }

// ToMap 返回字段名 → 值的 map（全 string）。
func (g *GeoInfo) ToMap() map[string]string {
	if g == nil {
		return map[string]string{}
	}
	m := make(map[string]string, len(g.FieldNames))
	for i, n := range g.FieldNames {
		if i < len(g.Values) {
			m[n] = g.Values[i]
		} else {
			m[n] = ""
		}
	}
	return m
}

// ---------- 无锁 GeoInfo 缓存（per-snapshot，row_id 为键） ----------

type geoSlot struct {
	key atomic.Uint32
	val atomic.Pointer[GeoInfo]
}

type geoCache struct {
	mask  uint32
	slots []geoSlot
}

func newGeoCache(capacity uint32) *geoCache {
	// capacity 向上取到 2 的幂
	c := uint32(1)
	for c < capacity {
		c <<= 1
	}
	return &geoCache{mask: c - 1, slots: make([]geoSlot, c)}
}

// get 命中且键匹配时返回缓存，否则返回 nil（碰撞只重算、绝不返回错值）。
// 采用轻量乐观读与二次检查，杜绝并发 put 时读到不一致的脏快照。
func (c *geoCache) get(rowID uint32) *GeoInfo {
	if rowID == 0 {
		return nil
	}
	s := &c.slots[rowID&c.mask]
	if s.key.Load() != rowID {
		return nil
	}
	val := s.val.Load()
	if s.key.Load() == rowID {
		return val
	}
	return nil
}

func (c *geoCache) put(rowID uint32, g *GeoInfo) {
	if rowID == 0 || g == nil {
		return
	}
	s := &c.slots[rowID&c.mask]
	s.key.Store(0) // 先废除当前槽位 key，防止并发读者读到新旧混合状态
	s.val.Store(g)
	s.key.Store(rowID)
}

// ---------- 字段投影 ----------

// projectGeo 从全集 GeoInfo 投影出 fields 指定的子集（未知字段补空串）。
func projectGeo(full *GeoInfo, fields []string) *GeoInfo {
	fns := make([]string, len(fields))
	vals := make([]string, len(fields))
	num := make([]bool, len(fields))
	pm := make(map[string]int, len(fields))
	for i, f := range fields {
		fns[i] = f
		pm[normalizeKey(f)] = i
		idx := -1
		if full.normMap != nil {
			idx = full.normMap[normalizeKey(f)]
		}
		if idx >= 0 && idx < len(full.Values) {
			vals[i] = full.Values[idx]
		}
		num[i] = isNumericFieldName(f)
	}
	return &GeoInfo{FieldNames: fns, Values: vals, normMap: pm, numeric: num}
}

// ---------- JSON 序列化 ----------
// （toJson 实现见下方，使用 encoding/json 手写以保证数值类型正确）

// parseGeoID / parseAsn / parseNumeric 为语义 Getter 提供安全数值解析。
func parseGeoID(s string) (int64, bool) {
	if s == "" {
		return 0, false
	}
	v, err := strconv.ParseInt(s, 10, 64)
	if err != nil {
		return 0, false
	}
	return v, true
}
